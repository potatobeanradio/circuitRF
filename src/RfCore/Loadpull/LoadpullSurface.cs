// ================================================================
//  LoadpullSurface.cs — Loadpull metric surface engine
//
//  Turns a loadpull DataSet ({gridPoint, pinStep} FOM cubes) into
//  smooth 2-D metric surfaces over Γ (or Z) via:
//    1. Per-grid-point compression preprocessing (port of SPLData.py __init__)
//    2. Scatter reduction (1-D interp at constant constraint)
//    3. RBF 2-D fit + in-memory cache
//    4. Resample to grid + MXP/MXE auto-view-box
//
//  Reference (algorithm): SPLData.py (generate_interpolator, interpolate_2D,
//  get_recommended_grid, get_MXX, calcMXPMXE, __init__ compression block).
//
//  Firewall: RfCore only. No Avalonia, no UI, no DataSet mutation.
//  The cube stays honest — surfaces are derived and cached here only.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using RfCore.Data;

namespace RfCore.Loadpull
{
    // ================================================================
    //  Public enums
    // ================================================================

    public enum CompressionType { Gmax, Gss }
    public enum SurfacePlane    { Gamma, Z }
    public enum ConstraintKind  { Compression, ConstantMetric }

    // ================================================================
    //  Public value / result types
    // ================================================================

    /// <summary>
    /// What a contour, a summary or an optimum is read at: a compression level, or a constant level of another metric.
    /// A compression is measured on <see cref="CompressionReference"/> (<c>Gt_dB</c>, the default and everything
    /// before brief LPW, or <c>Gp_dB</c>) from <see cref="CompressionType"/>'s reference point — the running maximum
    /// (<see cref="Loadpull.CompressionType.Gmax"/>, the default) or the small-signal gain at the first valid drive
    /// (<see cref="Loadpull.CompressionType.Gss"/>). Both are part of the record's equality, so a fit or stack cached at
    /// one reference is never returned for another.
    /// </summary>
    public readonly record struct ConstraintSpec(ConstraintKind Kind, string MetricName, double Value,
                                                 string? CompressionReference = null,
                                                 CompressionType CompressionType = CompressionType.Gmax)
    {
        /// <summary>The gain compression is measured on: <c>Gt_dB</c> unless <c>Gp_dB</c> is named.</summary>
        public string Reference => CompressionGain.Normalize(CompressionReference);

        public static ConstraintSpec AtCompression(double dB) =>
            new(ConstraintKind.Compression, "Compression", dB);

        public static ConstraintSpec AtCompression(double dB, string? reference, CompressionType type) =>
            new(ConstraintKind.Compression, "Compression", dB, CompressionGain.Normalize(reference), type);

        public static ConstraintSpec AtConstantMetric(string metric, double value) =>
            new(ConstraintKind.ConstantMetric, metric, value);
    }

    /// <summary>The gains a compression can be measured on (brief LPW, R-lpw-7).</summary>
    public static class CompressionGain
    {
        public const string Gt = "Gt_dB";
        public const string Gp = "Gp_dB";
        public static readonly IReadOnlyList<string> All = new[] { Gt, Gp };

        /// <summary><c>Gp_dB</c> (or <c>Gp</c>) → <c>Gp_dB</c>; anything else, null included, → <c>Gt_dB</c>.</summary>
        public static string Normalize(string? reference) =>
            reference is not null && (reference.Equals(Gp, StringComparison.OrdinalIgnoreCase)
                                      || reference.Equals("Gp", StringComparison.OrdinalIgnoreCase)) ? Gp : Gt;

        /// <summary>"Gt" / "Gp" — the short name a card or a title shows.</summary>
        public static string Short(string? reference) => Normalize(reference) == Gp ? "Gp" : "Gt";

        /// <summary>
        /// What a title adds for a compression that is not the default: "" for Gt from its maximum (every title written
        /// before brief LPW), else "Gp", "Gt from Gss" or "Gp from Gss".
        /// </summary>
        public static string Qualifier(string? reference, CompressionType type)
        {
            bool gp = Normalize(reference) == Gp;
            if (!gp && type == CompressionType.Gmax) return "";
            return type == CompressionType.Gss ? $"{Short(reference)} from Gss" : Short(reference);
        }
    }

    public readonly record struct ViewBox(double MinRe, double MaxRe, double MinIm, double MaxIm)
    {
        public bool IsValid => MinRe < MaxRe && MinIm < MaxIm;
        public double SpanRe => MaxRe - MinRe;
        public double SpanIm => MaxIm - MinIm;
    }

    public sealed record ScatterReduction(
        Complex[] Coords,
        double[]  Values,
        int[]     UsedGridIndices);

    public sealed record LoadpullFit(
        Rbf2D       Rbf,
        SurfacePlane Plane,
        Complex?    Z0,
        int         FreqIdx,
        string      MetricY,
        ConstraintSpec Constraint,
        RbfKernel   Kernel,
        double      Smooth,
        double?     Epsilon = null);

    /// <summary>
    /// A resampled surface. <paramref name="ClipRadius"/>, when set, is the |Γ| of the measured footprint: the values run
    /// a cell past it so every cell its edge crosses is whole, and a fill is clipped to that circle rather than ending on
    /// the grid's staircase of whole cells.
    /// </summary>
    public sealed record SurfaceGrid(
        double[] XSpace,
        double[] YSpace,
        double[] Values,
        double?  ClipRadius = null);

    public sealed record MxxResult(Complex Measured, Complex Interpolated);

    /// <summary>
    /// How far a constraint reaches over the loads: <see cref="Reached"/> of <see cref="Loads"/> give a value there, a
    /// surface needs <see cref="Needed"/>, and [<see cref="Lo"/>, <see cref="Hi"/>] is the range of constraint levels
    /// that <see cref="Needed"/> loads or more cover (NaN when no level does). For a compression <see cref="Lo"/> is 0.
    /// </summary>
    public sealed record ConstraintReachInfo(int Reached, int Loads, int Needed, double Lo, double Hi);

    // ================================================================
    //  LoadpullSurface
    // ================================================================

    /// <summary>
    /// Derived surface-model engine over a loadpull DataSet.
    /// Holds raw cubes by reference, computes compression preprocessing once,
    /// and lazily fits/caches RBF metric surfaces.
    /// </summary>
    public sealed class LoadpullSurface
    {
        // ── per-frequency preprocessed data ─────────────────────────────────

        private readonly FreqSlice[] _freqs;

        // ── fit cache ────────────────────────────────────────────────────────
        // Key: (freqIdx, metricY, constraint, plane, z0, kernel, smooth)
        private readonly Dictionary<FitKey, LoadpullFit> _cache = new();

        // ── public API ───────────────────────────────────────────────────────

        public IReadOnlyList<double> Frequencies { get; }

        /// <summary>
        /// Name of the leading slice axis — "freq" for a built-in frequency-swept loadpull, or the swept
        /// variable name (e.g. "RFfreq", "Vds") for a loadpull/pursuit wrapped in a parametric sweep.
        /// Consumers (contour freq picker, summary "Freq" column) label by this instead of assuming "Freq".
        /// </summary>
        public string LeadingAxisName { get; }

        /// <summary>
        /// Unit of the leading slice axis ("Hz" for a frequency sweep; the variable's base unit otherwise,
        /// e.g. "V"; "" when the swept variable is unitless). A frequency unit is shown in the plot's
        /// FreqUnits (GHz); any other unit is shown verbatim.
        /// </summary>
        public string LeadingAxisUnit { get; }

        public LoadpullSurface(DataSet data, string group = "")
        {
            _freqs = BuildFreqSlices(data, group);
            var freqs = new double[_freqs.Length];
            for (int i = 0; i < _freqs.Length; i++) freqs[i] = _freqs[i].FreqHz;
            Frequencies = Array.AsReadOnly(freqs);
            LeadingAxisName = _freqs.Length > 0 ? _freqs[0].LeadAxisName : "freq";
            LeadingAxisUnit = _freqs.Length > 0 ? _freqs[0].LeadAxisUnit : "Hz";
        }

        public int GridPointCount(int freqIdx) => _freqs[freqIdx].NGrid;

        /// <summary>Grid point <paramref name="gi"/>'s load at <paramref name="freqIdx"/>: its Γ (as stored) or its Z.</summary>
        public Complex GridCoord(int freqIdx, int gi, SurfacePlane plane = SurfacePlane.Gamma)
            => plane == SurfacePlane.Gamma ? _freqs[freqIdx].Gammas[gi] : _freqs[freqIdx].Zs[gi];

        /// <summary>The index on the <c>gridPoint</c> axis of the measured load nearest <paramref name="coord"/>
        /// (in the stored Γ, or in Z), or −1 when there are none — a free marker's Nearest read (brief-ddl-3 R-ddl3-2).</summary>
        public int NearestGridPoint(int freqIdx, Complex coord, SurfacePlane plane = SurfacePlane.Gamma)
        {
            var fs = _freqs[freqIdx];
            int best = -1; double bestD2 = double.PositiveInfinity;
            for (int gi = 0; gi < fs.NGrid; gi++)
            {
                var c = plane == SurfacePlane.Gamma ? fs.Gammas[gi] : fs.Zs[gi];
                double dx = c.Real - coord.Real, dy = c.Imaginary - coord.Imaginary, d2 = dx * dx + dy * dy;
                if (d2 < bestD2) { bestD2 = d2; best = gi; }
            }
            return best;
        }

        /// <summary>Whether <paramref name="metric"/> is one this surface can fit — a drive-up it carries.</summary>
        public bool HasMetric(int freqIdx, string metric) => _freqs[freqIdx].DriveUps.ContainsKey(metric);

        /// <summary>
        /// Whether <paramref name="coord"/> lies inside the convex hull of the loads measured at <paramref name="freqIdx"/>
        /// — on its boundary counts as inside. Outside it, anything read off the surface is an extrapolation
        /// (brief-ddl-3 R-ddl3-4).
        /// </summary>
        public bool IsInsideMeasured(int freqIdx, Complex coord, SurfacePlane plane = SurfacePlane.Gamma)
            => DistanceOutsideMeasured(freqIdx, coord, plane) <= 1e-9;

        /// <summary>How far <paramref name="coord"/> lies outside that hull, in the plane's own units (|ΔΓ|, or Ω):
        /// 0 inside it, +∞ when fewer than three loads were measured.</summary>
        public double DistanceOutsideMeasured(int freqIdx, Complex coord, SurfacePlane plane = SurfacePlane.Gamma)
        {
            var key = (freqIdx, plane);
            if (!_hulls.TryGetValue(key, out var hull))
            {
                var fs  = _freqs[freqIdx];
                var pts = new List<Complex>(fs.NGrid);
                for (int gi = 0; gi < fs.NGrid; gi++)
                {
                    var c = plane == SurfacePlane.Gamma ? fs.Gammas[gi] : fs.Zs[gi];
                    if (double.IsFinite(c.Real) && double.IsFinite(c.Imaginary)) pts.Add(c);
                }
                _hulls[key] = hull = ConvexHull(pts);
            }
            if (hull.Count < 3) return double.PositiveInfinity;
            if (InsideHull(hull, coord.Real, coord.Imaginary)) return 0;
            double best = double.PositiveInfinity;
            for (int i = 0, j = hull.Count - 1; i < hull.Count; j = i++)
                best = Math.Min(best, DistanceToSegment(coord, hull[j], hull[i]));
            return best;
        }

        private readonly Dictionary<(int, SurfacePlane), IReadOnlyList<Complex>> _hulls = new();

        private static double DistanceToSegment(Complex p, Complex a, Complex b)
        {
            var ab = b - a;
            double len2 = ab.Real * ab.Real + ab.Imaginary * ab.Imaginary;
            double t = len2 > 0 ? Math.Clamp(((p - a).Real * ab.Real + (p - a).Imaginary * ab.Imaginary) / len2, 0, 1) : 0;
            return Complex.Abs(p - (a + t * ab));
        }

        public double MedianCompression(int freqIdx)     => _freqs[freqIdx].MedianCompression;
        public double RecommendedCompression(int freqIdx) => _freqs[freqIdx].RecommendedCompression;

        // ── Reduce ───────────────────────────────────────────────────────────

        /// <summary>
        /// Compute scattered {coord, Y} at constant constraint, NaN-dropped.
        /// coord is Γ (Z0-renorm if z0 != null) or Z per plane. <paramref name="z0"/> may be complex
        /// (brief-dd-z0-renormalization.md §3); the stored grid's OWN reference is assumed 50 Ω real
        /// (see <see cref="RenormGamma"/> — no per-run loadpull reference is carried in the DataSet
        /// today).
        /// </summary>
        public ScatterReduction Reduce(
            int freqIdx, string metricY, ConstraintSpec constraint,
            SurfacePlane plane, Complex? z0 = null)
        {
            var fs     = _freqs[freqIdx];
            int nGrid  = fs.NGrid;

            var coords = new List<Complex>(nGrid);
            var values = new List<double>(nGrid);
            var used   = new List<int>(nGrid);

            for (int gi = 0; gi < nGrid; gi++)
            {
                double yi = ReducePoint(fs, gi, metricY, constraint);
                if (double.IsNaN(yi)) continue;

                Complex coord = plane == SurfacePlane.Gamma ? fs.Gammas[gi] : fs.Zs[gi];

                if (z0.HasValue && plane == SurfacePlane.Gamma)
                    coord = RenormGamma(coord, new Complex(AssumedSourceZ0, 0), z0.Value);

                coords.Add(coord);
                values.Add(yi);
                used.Add(gi);
            }

            return new ScatterReduction(coords.ToArray(), values.ToArray(), used.ToArray());
        }

        // ── Fit ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Fit (or fetch cached) the RBF surface for this query.
        /// Returns null if too few points to fit (&lt; MinFitNodes).
        /// </summary>
        public LoadpullFit? Fit(
            int freqIdx, string metricY, ConstraintSpec constraint,
            SurfacePlane plane, Complex? z0 = null,
            RbfKernel kernel = RbfKernel.Multiquadric, double smooth = 1e-3,
            double? epsilon = null)
        {
            var key = new FitKey(freqIdx, metricY, constraint, plane, z0, kernel, smooth, epsilon);
            if (_cache.TryGetValue(key, out var cached)) return cached;

            var reduction = Reduce(freqIdx, metricY, constraint, plane, z0);
            if (reduction.Coords.Length < MinFitNodes)
            {
                RFNetwork.Warn($"[LoadpullSurface] insufficient scatter points " +
                               $"({reduction.Coords.Length} < {MinFitNodes}) for {metricY}; skipping fit.");
                return null;
            }

            var rbf = new Rbf2D(reduction.Coords, reduction.Values, kernel, smooth, epsilon);
            var fit = new LoadpullFit(rbf, plane, z0, freqIdx, metricY, constraint, kernel, smooth, epsilon);
            _cache[key] = fit;
            return fit;
        }

        // ── ConstraintReach ──────────────────────────────────────────────────

        /// <summary>
        /// Why a fit may be missing: how many loads reach <paramref name="constraint"/> for <paramref name="metricY"/>,
        /// and the window of constraint levels enough loads cover to make a surface (brief LPW: the contour card says
        /// so when nothing can be drawn).
        /// </summary>
        public ConstraintReachInfo ConstraintReach(int freqIdx, string metricY, ConstraintSpec constraint)
        {
            var fs = _freqs[freqIdx];
            int reached = Reduce(freqIdx, metricY, constraint, SurfacePlane.Gamma).Values.Length;
            var spans = new List<(double Lo, double Hi)>();
            fs.DriveUps.TryGetValue(metricY, out var ys);
            if (constraint.Kind == ConstraintKind.Compression)
            {
                if (CompressionsFor(fs, constraint) is { } comps)
                    for (int gi = 0; gi < fs.NGrid; gi++)
                    {
                        double max = double.NaN;
                        foreach (var v in comps[gi].Compression)
                            if (!double.IsNaN(v) && (double.IsNaN(max) || v > max)) max = v;
                        if (!double.IsNaN(max)) spans.Add((0, max));
                    }
            }
            else if (fs.DriveUps.TryGetValue(constraint.MetricName, out var doms))
            {
                for (int gi = 0; gi < fs.NGrid; gi++)
                {
                    double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
                    for (int p = 0; p < doms[gi].Length; p++)
                    {
                        double d = doms[gi][p];
                        if (double.IsNaN(d) || (ys is not null && (p >= ys[gi].Length || double.IsNaN(ys[gi][p])))) continue;
                        lo = Math.Min(lo, d); hi = Math.Max(hi, d);
                    }
                    if (lo <= hi) spans.Add((lo, hi));
                }
            }

            // Coverage only changes at a span's ends, so the window is found among them.
            double wLo = double.NaN, wHi = double.NaN;
            foreach (var x in spans.SelectMany(sp => new[] { sp.Lo, sp.Hi }))
                if (spans.Count(sp => sp.Lo <= x && x <= sp.Hi) >= MinFitNodes)
                {
                    if (double.IsNaN(wLo) || x < wLo) wLo = x;
                    if (double.IsNaN(wHi) || x > wHi) wHi = x;
                }
            return new ConstraintReachInfo(reached, fs.NGrid, MinFitNodes, wLo, wHi);
        }

        // ── MaxPower / MaxEfficiency ─────────────────────────────────────────

        /// <summary>Max output power location (MXP): measured + interpolated Γ or Z.</summary>
        public MxxResult? MaxPower(
            int freqIdx, ConstraintSpec constraint,
            SurfacePlane plane, Complex? z0 = null,
            RbfKernel kernel = RbfKernel.Multiquadric, double smooth = 1e-3, double? epsilon = null)
            => GetMxx(freqIdx, "Pout_dBm", constraint, plane, z0, kernel, smooth, epsilon);

        /// <summary>Max drain efficiency location (MXE): measured + interpolated Γ or Z.</summary>
        public MxxResult? MaxEfficiency(
            int freqIdx, ConstraintSpec constraint,
            SurfacePlane plane, Complex? z0 = null,
            RbfKernel kernel = RbfKernel.Multiquadric, double smooth = 1e-3, double? epsilon = null)
            => GetMxx(freqIdx, "Efficiency", constraint, plane, z0, kernel, smooth, epsilon);

        // ── MetricAtCoord / SourceZ / OperatingPoint (summary-table accessors) ─

        /// <summary>
        /// Scalar value of <paramref name="metricY"/> at <paramref name="coord"/> (in the fit plane: Γ if
        /// plane==Gamma, Z if plane==Z), at the given constraint.
        ///   nearest == false (Interp): evaluate the metric's RBF surface at coord.
        ///   nearest == true  (Nearest): value of the nearest measured node to coord.
        /// Returns NaN if the metric cube is absent or the fit cannot be built (presence-tolerant).
        /// Used by the summary table to read each metric column at the MXP/MXE optimum.
        /// </summary>
        public double MetricAtCoord(
            int freqIdx, string metricY, Complex coord, ConstraintSpec constraint,
            SurfacePlane plane, Complex? z0 = null,
            bool nearest = false,
            RbfKernel kernel = RbfKernel.Multiquadric, double smooth = 1e-3, double? epsilon = null)
        {
            var fit = Fit(freqIdx, metricY, constraint, plane, z0, kernel, smooth, epsilon);
            if (fit == null || fit.Rbf.NodeCount == 0) return double.NaN;
            var rbf = fit.Rbf;

            if (!nearest)
                return rbf.Evaluate(coord.Real, coord.Imaginary);

            int    best   = 0;
            double bestD2 = double.PositiveInfinity;
            for (int i = 0; i < rbf.NodeCount; i++)
            {
                double dx = rbf.NodesRe[i] - coord.Real;
                double dy = rbf.NodesIm[i] - coord.Imaginary;
                double d2 = dx * dx + dy * dy;
                if (d2 < bestD2) { bestD2 = d2; best = i; }
            }
            return rbf.NodeValues[best];
        }

        /// <summary>
        /// Per-frequency source termination impedance (Ω), assumed constant across load terminations.
        /// Reads a source-Z cube if present (canonical name "ZSource"); null when absent (presence-tolerant).
        /// Read at grid point 0 (source termination does not vary with the load grid).
        /// </summary>
        public Complex? SourceZ(int freqIdx) => _freqs[freqIdx].SourceZ;

        /// <summary>
        /// Per-frequency operating-point scalar from a bias cube (e.g. "BiasVLoad"=VDD, "BiasILoad"=Idq).
        /// Returns the value at grid point 0, pinStep 0 (constant over the sweep). null if the cube is absent.
        /// Stored unit is the cube's native unit (e.g. BiasILoad in Amps — caller scales to mA for display).
        /// </summary>
        public double? OperatingPoint(int freqIdx, string cubeName)
        {
            if (!_freqs[freqIdx].DriveUps.TryGetValue(cubeName, out var driveUps)) return null;
            if (driveUps.Length == 0 || driveUps[0] is not { Length: > 0 } du0) return null;
            double v = du0[0];
            return double.IsNaN(v) ? (double?)null : v;
        }

        // ── RecommendedBox ───────────────────────────────────────────────────

        /// <summary>
        /// Auto-view-box enclosing MXP and MXE with VSWR margin, clipped
        /// to the measured data extent.  Port of SPLData.get_recommended_grid.
        /// </summary>
        /// <summary>
        /// Bounding box of the fit's scatter nodes — the FULL measured data extent (every load
        /// termination with a valid reduction). Unlike <see cref="RecommendedBox"/> (which auto-zooms
        /// to MXP/MXE), this covers all the data, so a Z-plane contour resampled over it renders
        /// iso-lines wherever data exists — the Rect analog of the Smith plot's full-disk coverage.
        /// </summary>
        public ViewBox MeasuredBox(LoadpullFit fit)
        {
            double minRe = double.MaxValue, maxRe = double.MinValue;
            double minIm = double.MaxValue, maxIm = double.MinValue;
            for (int i = 0; i < fit.Rbf.NodeCount; i++)
            {
                double re = fit.Rbf.NodesRe[i], im = fit.Rbf.NodesIm[i];
                if (re < minRe) minRe = re;
                if (re > maxRe) maxRe = re;
                if (im < minIm) minIm = im;
                if (im > maxIm) maxIm = im;
            }
            return new ViewBox(minRe, maxRe, minIm, maxIm);
        }

        /// <summary>
        /// Default compression levels (dB) tried, in order, for the MXP/MXE recommended terminations.
        /// </summary>
        private static readonly double[] RecommendedCompressionLevels = { 3.0, 1.0 };

        /// <summary>
        /// The MXP / MXE recommended-termination locations — max-power / max-efficiency loads defined at
        /// a COMPRESSION level. These are NOT a function of whatever metric/constraint a contour happens
        /// to plot (e.g. "Efficiency at Constant Pout"); computing them off the contour's constraint puts
        /// the "MXE" in the wrong load region. Tries P-3dB, then P-1dB; returns (null,null) when the data
        /// reaches neither (caller falls back to the full data extent / <see cref="MeasuredBox"/>).
        /// </summary>
        public (MxxResult? Mxp, MxxResult? Mxe) RecommendedMxx(LoadpullFit fit)
        {
            foreach (double dB in RecommendedCompressionLevels)
            {
                var c   = ConstraintSpec.AtCompression(dB);
                var mxp = GetMxx(fit.FreqIdx, "Pout_dBm",   c, fit.Plane, fit.Z0, fit.Kernel, fit.Smooth, fit.Epsilon);
                var mxe = GetMxx(fit.FreqIdx, "Efficiency", c, fit.Plane, fit.Z0, fit.Kernel, fit.Smooth, fit.Epsilon);
                if (mxp is not null && mxe is not null) return (mxp, mxe);
            }
            return (null, null);
        }

        public ViewBox RecommendedBox(LoadpullFit fit)
        {
            // Measured-data bounding box (from RBF node coords)
            var measuredBox = MeasuredBox(fit);

            // MXP/MXE are compression-only (P-3dB → P-1dB). If the data reaches neither, there is no
            // meaningful recommended zoom → fall back to the full measured data extent.
            var (mxpResult, mxeResult) = RecommendedMxx(fit);
            if (mxpResult is null || mxeResult is null) return measuredBox;

            Complex mxp = mxpResult.Measured;
            Complex mxe = mxeResult.Measured;

            // VSWR include factors (SPLData: 99 for Gamma "as much as possible"; 1.3 for Z)
            double includeVswr = fit.Plane == SurfacePlane.Gamma ? 99.0 : 1.3;
            Complex? z0ref     = fit.Z0 ?? (fit.Plane == SurfacePlane.Gamma ? new Complex(AssumedSourceZ0, 0) : null);

            var mxpBox = VswrBoundingBox(mxp, includeVswr, fit.Plane, z0ref);
            var mxeBox = VswrBoundingBox(mxe, includeVswr, fit.Plane, z0ref);

            // Union
            var desired = new ViewBox(
                Math.Min(mxpBox.MinRe, mxeBox.MinRe),
                Math.Max(mxpBox.MaxRe, mxeBox.MaxRe),
                Math.Min(mxpBox.MinIm, mxeBox.MinIm),
                Math.Max(mxpBox.MaxIm, mxeBox.MaxIm));

            // Clip to measured box
            var final = new ViewBox(
                Math.Max(desired.MinRe, measuredBox.MinRe),
                Math.Min(desired.MaxRe, measuredBox.MaxRe),
                Math.Max(desired.MinIm, measuredBox.MinIm),
                Math.Min(desired.MaxIm, measuredBox.MaxIm));

            if (!final.IsValid) final = measuredBox;

            // Round to nice grid for Z-plane (SPLData: RoundFactor = xSpan/5) — OUTWARD, then clipped to the data again.
            // Rounding to nearest pulled an edge inward by up to half a step, past the MXP it was built around: a GaN
            // bench's MXP at 36 Ω sat on a box rounded up to 39 Ω, drawn half off the plot.
            if (fit.Plane == SurfacePlane.Z) final = RoundOutward(final, measuredBox);

            return final;
        }

        /// <summary>The Z-plane box on a nice grid of a fifth of its width, rounded outward and clipped to the data.</summary>
        internal static ViewBox RoundOutward(ViewBox box, ViewBox measured)
        {
            double xSpan       = box.SpanRe;
            double roundFactor = xSpan > 0 ? xSpan / 5.0 : 1.0;
            var rounded = new ViewBox(
                Math.Max(Math.Floor(box.MinRe / roundFactor) * roundFactor, measured.MinRe),
                Math.Min(Math.Ceiling(box.MaxRe / roundFactor) * roundFactor, measured.MaxRe),
                Math.Max(Math.Floor(box.MinIm / roundFactor) * roundFactor, measured.MinIm),
                Math.Min(Math.Ceiling(box.MaxIm / roundFactor) * roundFactor, measured.MaxIm));
            return rounded.IsValid ? rounded : measured;
        }

        // ── Resample ─────────────────────────────────────────────────────────

        /// <summary>
        /// Evaluate the fit on a grid.  Uses MXP/MXE auto-view-box if box is null.
        /// Returns a (resolution × resolution) value grid, row-major,
        /// with NaN outside the Γ-disk when plane==Gamma.
        /// </summary>
        public SurfaceGrid Resample(LoadpullFit fit, ViewBox? box = null, int resolution = 50, bool toFootprintEdge = false)
        {
            var viewBox = box ?? RecommendedBox(fit);
            if (!viewBox.IsValid)
            {
                return new SurfaceGrid(Array.Empty<double>(), Array.Empty<double>(), Array.Empty<double>());
            }

            var xSpace = Linspace(viewBox.MinRe, viewBox.MaxRe, resolution);
            var ySpace = Linspace(viewBox.MinIm, viewBox.MaxIm, resolution);
            var values = new double[resolution * resolution];

            double maxGamma = fit.Plane == SurfacePlane.Gamma
                ? MaxNodeRadius(fit.Rbf) * 1.02
                : double.PositiveInfinity;
            // A fill (toFootprintEdge) is evaluated one cell diagonal past the footprint, so the cells the circle crosses
            // have all four corners and the renderer can clip to the circle itself; iso-lines keep the footprint mask.
            double? clip = null;
            double evalRadius = maxGamma;
            if (toFootprintEdge && fit.Plane == SurfacePlane.Gamma && resolution > 1)
            {
                clip = maxGamma;
                double dx = (viewBox.MaxRe - viewBox.MinRe) / (resolution - 1), dy = (viewBox.MaxIm - viewBox.MinIm) / (resolution - 1);
                evalRadius = maxGamma + Math.Sqrt(dx * dx + dy * dy);
            }

            int idx = 0;
            for (int yi = 0; yi < resolution; yi++)
            {
                for (int xi = 0; xi < resolution; xi++)
                {
                    double re = xSpace[xi], im = ySpace[yi];
                    if (re * re + im * im > evalRadius * evalRadius)
                    {
                        values[idx++] = double.NaN;
                    }
                    else
                    {
                        values[idx++] = fit.Rbf.Evaluate(re, im);
                    }
                }
            }

            return new SurfaceGrid(xSpace, ySpace, values, clip);
        }

        // ================================================================
        //  Private — compression preprocessing
        // ================================================================

        private const int    MinFitNodes   = 6;
        private const int    VswrNPoints   = 100;

        /// <summary>
        /// Assumed reference impedance the stored Γ grid is natively referenced to. There is no
        /// per-run "loadpull reference Z0" carried in the DataSet today — LoadpullExportModel and
        /// the pre-brief-dd-z0-renormalization RenormGamma both hardcoded 50 Ω for the same reason.
        /// Known limitation, not silently assumed: if a future loadpull format ever carries its own
        /// reference, this constant (and every <c>RenormGamma</c> call site) is where it plugs in.
        /// </summary>
        private const double AssumedSourceZ0 = 50.0;

        private static FreqSlice[] BuildFreqSlices(DataSet data, string group)
        {
            // Locate cubes (group prefix if non-empty, else bare canonical names)
            DataCube GetCube(string name) =>
                group.Length > 0 ? data[$"{group}.{name}"] : data[name];

            bool hasCube(string name)
            {
                string spec = group.Length > 0 ? $"{group}.{name}" : name;
                return data.Contains(spec);
            }

            var poutCube     = GetCube("Pout_dBm");
            var gtCube       = GetCube("Gt_dB");
            var gammaCube    = GetCube("GammaLoad");
            var zCube        = GetCube("ZLoad");
            var pinAxisCube  = GetCube("PavlDbm");

            // Build optional metric cubes
            var metricNames  = new[] { "Pout_dBm", "Pout_W", "Gt_dB", "Gp_dB", "Efficiency", "PAE",
                                       "PavlDbm", "Pdc_W",
                                       "BiasVLoad", "BiasILoad", "BiasVSrc", "BiasISrc",
                                       // Derived display metrics (post-processor / .spl reader) — must be
                                       // loadable so the summary table's MetricAtCoord can read them.
                                       "Zin_real", "Zin_imag", "AMPM_deg", "IRL_dB",
                                       "NF",     // brief NZ-6: the noise figure of a Noise=yes run
                                       // brief LPW: the input powers at the device's plane, from the waves
                                       "PinInc_dBm", "PinDel_dBm" };
            var metricCubes  = new Dictionary<string, DataCube>(StringComparer.Ordinal);
            foreach (var m in metricNames)
                if (hasCube(m)) metricCubes[m] = GetCube(m);
            // ...and the harmonic powers a wave file carries (Pout_2f0_dBm, PinDel_3f0_dBm, …), whatever n it holds.
            if (data.Groups.Contains(group))
            foreach (var (name, cube) in data.CubesIn(group))
                if (LoadpullWaves.IsHarmonicPowerMetric(name) && cube.DataKind == DataKind.Real)
                    metricCubes[name] = cube;

            // Detect a leading sweep axis by POSITION, not by name. The FOM cube is canonically
            // [gridPoint, pinStep] (rank 2); a sweep prepends ONE leading axis (rank 3), which may be a
            // built-in frequency sweep (named "freq") OR a parametric sweep wrapping the loadpull/pursuit
            // over any variable (named "RFfreq", "Vds", …). All slicing below is position-based, so any
            // leading-axis name works — matching LoadpullRecognition, which keys on the trailing signature.
            bool hasFreq   = poutCube.Rank == 3;
            int  nFreq     = hasFreq ? poutCube.Axes[0].Length : 1;
            int  nGrid     = poutCube.Axis("gridPoint").Length;
            int  nPin      = poutCube.Axis("pinStep").Length;

            // Leading-axis identity — "freq"/"Hz" for a single or built-in frequency sweep; the swept
            // variable's name/base-unit for a parametric-swept loadpull/pursuit (e.g. "RFfreq"/"Hz",
            // "Vds"/"V"). The display layer labels by this instead of assuming "Freq"/GHz.
            string leadAxisName = hasFreq ? poutCube.Axes[0].Name : "freq";
            string leadAxisUnit = hasFreq ? poutCube.Axes[0].Unit : "Hz";

            double[]  pinAxisVals = pinAxisCube.Axis("pinStep").Values;
            double[]? freqVals   = hasFreq ? poutCube.Axes[0].Values : null;

            // Single-freq datasets carry no "freq" axis on the FOM cubes; the importer preserves
            // the measured frequency on a rank-1 "__Freq" carrier cube (or on "ZSource" when a
            // source termination is present). Recover it so the summary table shows the real freq
            // instead of 0. (Bug: single-freq .spl reported Freq = 0.)
            if (!hasFreq)
            {
                double single = 0.0;
                if (hasCube("__Freq"))
                {
                    var fc = GetCube("__Freq");
                    var fr = fc[0];
                    if (fr.IsReal) single = fr.RealValue!.Value;
                }
                else if (hasCube("ZSource"))
                {
                    var za = GetCube("ZSource");
                    var ax = za.Axes.FirstOrDefault(a => a.Name == "freq");
                    if (ax is not null && ax.Values.Length > 0) single = ax.Values[0];
                }
                freqVals = new[] { single };
            }

            var slices = new FreqSlice[nFreq];

            for (int fi = 0; fi < nFreq; fi++)
            {
                // Per-grid-point data
                var gammas       = new Complex[nGrid];
                var zs           = new Complex[nGrid];
                var driveUps     = new Dictionary<string, double[][]>(StringComparer.Ordinal);
                var compressions = new GridPointCompression[nGrid];

                // Initialize drive-up dictionaries
                foreach (var m in metricCubes.Keys)
                    driveUps[m] = new double[nGrid][];

                // Extract GammaLoad and ZLoad
                if (hasFreq)
                {
                    var gSlice = ((DataCube)gammaCube[fi, Range.All]).ComplexValues;
                    var zSlice = ((DataCube)zCube[fi, Range.All]).ComplexValues;
                    Array.Copy(gSlice, gammas, nGrid);
                    Array.Copy(zSlice, zs,     nGrid);
                }
                else
                {
                    Array.Copy(gammaCube.ComplexValues, gammas, nGrid);
                    Array.Copy(zCube.ComplexValues,     zs,     nGrid);
                }

                // Extract per-grid drive-ups
                foreach (var (mName, mCube) in metricCubes)
                {
                    for (int gi = 0; gi < nGrid; gi++)
                    {
                        double[] du;
                        if (hasFreq)
                            du = ((DataCube)mCube[fi, gi, Range.All]).RealValues;
                        else
                            du = ((DataCube)mCube[gi, Range.All]).RealValues;
                        driveUps[mName][gi] = du;
                    }
                }

                // Compression preprocessing (§1 of brief) — the default reference, Gt from its running maximum, which the
                // recommended compression is computed on. Any other reference is computed on first use (CompressionsFor).
                var gtDriveUps = driveUps["Gt_dB"];
                var compValues = new double[nGrid];

                for (int gi = 0; gi < nGrid; gi++)
                {
                    compressions[gi] = ComputeCompression(gtDriveUps[gi], CompressionType.Gmax);
                    // Max compression at this grid point = max of valid Compression values
                    var compArr = compressions[gi].Compression;
                    double maxComp = double.NaN;
                    foreach (var v in compArr)
                        if (!double.IsNaN(v) && (double.IsNaN(maxComp) || v > maxComp))
                            maxComp = v;
                    compValues[gi] = maxComp;
                }

                // Per-freq stats
                var validComp      = compValues.Where(v => !double.IsNaN(v) && v > 0).ToArray();
                double medianComp  = validComp.Length > 0 ? Median(validComp) : 0.0;
                double recCompress = RecommendedCompressionSetting(medianComp);

                Complex? srcZ = null;
                if (hasCube("ZSource"))
                {
                    var zc = GetCube("ZSource");   // always rank-1 {freq} from 7.5g importers
                    var sr = zc[fi];
                    if (sr.IsComplex) srcZ = sr.ComplexValue;
                }

                slices[fi] = new FreqSlice
                {
                    FreqHz              = freqVals != null ? freqVals[fi] : 0.0,
                    LeadAxisName        = leadAxisName,
                    LeadAxisUnit        = leadAxisUnit,
                    NGrid               = nGrid,
                    Gammas              = gammas,
                    Zs                  = zs,
                    DriveUps            = driveUps,
                    PinAxis             = pinAxisVals,
                    Compressions        = compressions,
                    MedianCompression   = medianComp,
                    RecommendedCompression = recCompress,
                    SourceZ             = srcZ,
                };
            }

            return slices;
        }

        // ================================================================
        //  Private — per-point compression
        // ================================================================

        private static GridPointCompression ComputeCompression(
            double[] gt, CompressionType type)
        {
            // Find valid (non-NaN) points
            int n = gt.Length;

            // Find compression_index
            int ci = 0;
            if (type == CompressionType.Gmax)
            {
                double maxGt = double.NegativeInfinity;
                for (int i = 0; i < n; i++)
                {
                    if (!double.IsNaN(gt[i]) && gt[i] > maxGt)
                    {
                        maxGt = gt[i];
                        ci    = i;
                    }
                }
            }
            else
            {
                // Gss: the small-signal gain is the first valid drive's.
                ci = Array.FindIndex(gt, v => !double.IsNaN(v));
                if (ci < 0) ci = 0;
            }

            // Compression curve from ci onward (0 at ci, increasing)
            int len  = n - ci;
            var comp = new double[len];
            for (int p = 0; p < len; p++)
                comp[p] = double.IsNaN(gt[ci]) ? double.NaN
                          : double.IsNaN(gt[ci + p]) ? double.NaN
                          : gt[ci] - gt[ci + p];

            return new GridPointCompression(ci, comp);
        }

        /// <summary>
        /// Each load's compression curve on the constraint's reference gain and type: the precomputed Gt/Gmax one, or one
        /// computed on first use and kept on the slice. Null when the reference gain is not in the data (a file without
        /// Gp_dB contoured at compression of Gp): nothing is drawn rather than a Gt compression mislabelled as Gp.
        /// </summary>
        private static GridPointCompression[]? CompressionsFor(FreqSlice fs, ConstraintSpec constraint)
        {
            string reference = constraint.Reference;
            var type = constraint.CompressionType;
            if (reference == CompressionGain.Gt && type == CompressionType.Gmax) return fs.Compressions;
            var key = (reference, type);
            lock (fs.OtherCompressions)
            {
                if (fs.OtherCompressions.TryGetValue(key, out var cached)) return cached;
                GridPointCompression[]? result = null;
                if (fs.DriveUps.TryGetValue(reference, out var gains))
                {
                    result = new GridPointCompression[fs.NGrid];
                    for (int gi = 0; gi < fs.NGrid; gi++) result[gi] = ComputeCompression(gains[gi], type);
                }
                fs.OtherCompressions[key] = result;
                return result;
            }
        }

        // ================================================================
        //  Private — scatter reduction
        // ================================================================

        private static double ReducePoint(
            FreqSlice fs, int gi,
            string metricY, ConstraintSpec constraint)
        {
            var compressions = CompressionsFor(fs, constraint);
            if (compressions is null) return double.NaN;
            var comp = compressions[gi];

            if (constraint.Kind == ConstraintKind.Compression)
            {
                // Domain = Compression array (length = nPin - ci, values 0..max_comp)
                // Values = Y[ci..] aligned to Compression array
                // Both start at ci in the original drive-up.
                if (!fs.DriveUps.TryGetValue(metricY, out var yDriveUps))
                    return double.NaN;

                double[] compDomain = comp.Compression;
                double[] yAll       = yDriveUps[gi];
                int      ci         = comp.CompressionIndex;

                if (compDomain.Length < 2) return double.NaN;

                // Build (domain=Compression, values=Y[ci..]) with same length
                int len    = Math.Min(compDomain.Length, yAll.Length - ci);
                var ySlice = new double[len];
                Array.Copy(yAll, ci, ySlice, 0, len);

                var (dom, vals) = ExtractAscending(compDomain, ySlice, 0);
                if (dom.Length < 2) return double.NaN;

                return new Interp1DLinear(dom, vals).Eval(constraint.Value);
            }
            else
            {
                // Constant-metric constraint: domain = that metric's drive-up
                if (!fs.DriveUps.TryGetValue(constraint.MetricName, out var domDriveUps))
                    return double.NaN;
                if (!fs.DriveUps.TryGetValue(metricY, out var yDriveUps))
                    return double.NaN;

                double[] domAll = domDriveUps[gi];
                double[] yAll   = yDriveUps[gi];

                var (dom, vals) = ExtractAscending(domAll, yAll, 0);
                if (dom.Length < 2) return double.NaN;

                return new Interp1DLinear(dom, vals).Eval(constraint.Value);
            }
        }

        /// <summary>
        /// Extract (domain, values) pairs with strictly ascending domain,
        /// starting at <paramref name="startIdx"/> in both arrays,
        /// dropping NaN domain/value entries.
        /// </summary>
        private static (double[] dom, double[] vals) ExtractAscending(
            double[] domain, double[] values, int startIdx)
        {
            var ds = new List<double>();
            var vs = new List<double>();
            double prev = double.NegativeInfinity;

            for (int i = startIdx; i < Math.Min(domain.Length, values.Length); i++)
            {
                double d = domain[i], v = values[i];
                if (double.IsNaN(d) || double.IsNaN(v)) continue;
                if (d > prev)
                {
                    ds.Add(d);
                    vs.Add(v);
                    prev = d;
                }
            }
            return (ds.ToArray(), vs.ToArray());
        }

        // ================================================================
        //  Private — MXX (max power / max efficiency)
        // ================================================================

        private MxxResult? GetMxx(
            int freqIdx, string metricY, ConstraintSpec constraint,
            SurfacePlane plane, Complex? z0,
            RbfKernel kernel = RbfKernel.Multiquadric, double smooth = 1e-3, double? epsilon = null)
        {
            // Use Compression constraint for MXX regardless of caller constraint
            // (SPLData.get_MXX always uses constant_var='Compression' with the same val)
            var mxxConstraint = constraint.Kind == ConstraintKind.Compression
                ? constraint
                : new ConstraintSpec(ConstraintKind.Compression, "Compression",
                                     _freqs[freqIdx].RecommendedCompression);

            var fit = Fit(freqIdx, metricY, mxxConstraint, plane, z0, kernel, smooth, epsilon);
            if (fit == null || fit.Rbf.NodeCount == 0) return null;

            // 1. Measured peak = argmax over node values
            var rbf = fit.Rbf;
            int maxIdx = 0;
            double maxVal = double.NegativeInfinity;
            for (int i = 0; i < rbf.NodeCount; i++)
            {
                if (rbf.NodeValues[i] > maxVal) { maxVal = rbf.NodeValues[i]; maxIdx = i; }
            }
            var measured = new Complex(rbf.NodesRe[maxIdx], rbf.NodesIm[maxIdx]);

            // 2. Interpolated peak — the fitted surface's maximum over the MEASURED region: a coarse pass over the nodes' convex hull,
            //    then a fine one around its best cell. It used to search only a VSWR 1.2 circle round the measured argmax, and on a
            //    flat peak that argmax is a tie-break: the example loadpull's Pout reads 41.43 dBm at 0.2∠±30° and 41.41 at 0.2∠0°,
            //    the surface peaks on the real axis at |Γ| ≈ 0.13, 0.11 away, outside the circle, so MXP was drawn at +30°, off the
            //    centre of the contours drawn from the same fit. The hull keeps the search off the extrapolation outside the data.
            //    Fewer than three non-collinear nodes have no hull, and keep the circle.
            Complex interpolated = measured;
            double  interpMax    = rbf.Evaluate(measured.Real, measured.Imaginary);
            void Consider(double re, double im)
            {
                double v = rbf.Evaluate(re, im);
                if (v > interpMax) { interpMax = v; interpolated = new Complex(re, im); }
            }

            var nodes = new Complex[rbf.NodeCount];
            for (int i = 0; i < nodes.Length; i++) nodes[i] = new Complex(rbf.NodesRe[i], rbf.NodesIm[i]);
            var hull = ConvexHull(nodes);
            if (hull.Count >= 3)
            {
                const int coarse = 60, fine = 24;
                var cx = Linspace(hull.Min(h => h.Real), hull.Max(h => h.Real), coarse);
                var cy = Linspace(hull.Min(h => h.Imaginary), hull.Max(h => h.Imaginary), coarse);
                for (int yi = 0; yi < coarse; yi++)
                    for (int xi = 0; xi < coarse; xi++)
                        if (InsideHull(hull, cx[xi], cy[yi])) Consider(cx[xi], cy[yi]);
                double dx = cx[1] - cx[0], dy = cy[1] - cy[0];
                var (bx, by) = (interpolated.Real, interpolated.Imaginary);
                var fx = Linspace(bx - dx, bx + dx, fine);
                var fy = Linspace(by - dy, by + dy, fine);
                for (int yi = 0; yi < fine; yi++)
                    for (int xi = 0; xi < fine; xi++)
                        if (InsideHull(hull, fx[xi], fy[yi])) Consider(fx[xi], fy[yi]);
            }
            else
            {
                double searchVswr = 1.2;
                Complex? z0ref    = z0 ?? (plane == SurfacePlane.Gamma ? new Complex(AssumedSourceZ0, 0) : null);
                var searchCircle  = VswrCirclePoints(measured, searchVswr, plane, z0ref);
                int hRes = 50;
                var hx   = Linspace(searchCircle.MinRe, searchCircle.MaxRe, hRes);
                var hy   = Linspace(searchCircle.MinIm, searchCircle.MaxIm, hRes);
                for (int yi = 0; yi < hRes; yi++)
                    for (int xi = 0; xi < hRes; xi++) Consider(hx[xi], hy[yi]);
            }

            return new MxxResult(measured, interpolated);
        }

        // ================================================================
        //  Private — VSWR circle helpers
        // ================================================================

        /// <summary>
        /// VSWR circle in Z-plane around zCenter with given VSWR.
        /// Parametrization: Z(θ) = (Zc + ρ·eʲθ·conj(Zc)) / (1 - ρ·eʲθ)
        /// where ρ = (VSWR-1)/(VSWR+1).
        /// </summary>
        private static Complex[] VswrCircleZ(Complex zCenter, double vswr, int nPoints = VswrNPoints)
        {
            double rho = (vswr - 1.0) / (vswr + 1.0);
            var pts = new Complex[nPoints];
            for (int k = 0; k < nPoints; k++)
            {
                double theta = 2.0 * Math.PI * k / nPoints;
                var gamma_k  = new Complex(rho * Math.Cos(theta), rho * Math.Sin(theta));
                pts[k]       = (zCenter + gamma_k * Complex.Conjugate(zCenter)) / (1.0 - gamma_k);
            }
            return pts;
        }

        /// <summary>
        /// Constant-VSWR locus around <paramref name="center"/>, as a closed ring of points
        /// in the requested plane. For <see cref="SurfacePlane.Z"/> the locus is computed directly
        /// in the Z-plane. For <see cref="SurfacePlane.Gamma"/> the locus is drawn as a TRUE CIRCLE
        /// in the Γ-plane: a constant-VSWR contour is the set |s| = ρ of the Kurokawa power-wave
        /// reflection coefficient s = (Z − Z0)/(Z + conj(Z0)), and s relates to the ordinary Γ by a
        /// Möbius transform, which maps the circle |s| = ρ to another circle in Γ. We compute that
        /// Γ-circle's center and radius in closed form and sample it uniformly in angle, so the ring
        /// is smooth at any VSWR with a fixed point count (no Z→Γ mapping jaggies). See
        /// docs/design/vswr-locus-gamma-plane.md for the derivation. <paramref name="z0ref"/> is the
        /// host trace's reference impedance — a FULL complex value, imaginary part included.
        /// vswr is unclamped (negative permitted; ρ may be negative — the circle formulas still hold).
        /// </summary>
        public static Complex[] VswrLocus(
            Complex center, double vswr, SurfacePlane plane, Complex z0ref, int nPoints = VswrNPoints)
        {
            if (plane == SurfacePlane.Gamma)
                return VswrCircleGamma(center, vswr, z0ref, nPoints);
            return VswrCircleZ(center, vswr, nPoints);
        }

        /// <summary>
        /// Constant-VSWR locus as a true Γ-plane circle, sampled uniformly in angle.
        ///
        /// Derivation (docs/design/vswr-locus-gamma-plane.md): the center impedance is
        /// Zc = G2Z(center)·Z0 (ohms). The Z-plane locus about Zc is the power-wave circle
        /// |s_c| = ρ, s_c = (Z − Zc)/(Z + conj(Zc)), ρ = (V−1)/(V+1) — i.e. Z = (Zc + s_c·conj(Zc))/(1 − s_c).
        /// Composing with Γ = (Z − Z0)/(Z + Z0) gives a single Möbius map Γ = (a·s_c + b)/(c·s_c + d) with
        ///   a = conj(Zc) + Z0,  b = Zc − Z0,  c = conj(Zc) − Z0,  d = Zc + Z0.
        /// The image of |s_c| = ρ is the circle
        ///   center = (b·conj(d) − a·conj(c)·ρ²) / (|d|² − |c|²·ρ²),
        ///   radius = |a·d − b·c|·|ρ| / | |d|² − |c|²·ρ² |.
        /// (For real Z0 this reduces to Γ = ρ·e^{jθ} about the matched point — the textbook result.)
        /// Falls back to direct Möbius sampling in the rare degenerate case where the circle passes
        /// through Γ = ∞ (denominator ≈ 0).
        /// </summary>
        private static Complex[] VswrCircleGamma(
            Complex center, double vswr, Complex z0ref, int nPoints)
        {
            double  rho = (vswr - 1.0) / (vswr + 1.0);
            Complex Zc  = RfHelpers.G2Z(center) * z0ref;
            Complex Zcc = Complex.Conjugate(Zc);

            // Mobius coefficients: Gamma = (a*s + b)/(c*s + d), s on |s| = rho about Zc.
            Complex a = Zcc + z0ref;
            Complex b = Zc  - z0ref;
            Complex c = Zcc - z0ref;
            Complex d = Zc  + z0ref;

            double denom = d.Magnitude * d.Magnitude - c.Magnitude * c.Magnitude * rho * rho;

            var pts = new Complex[nPoints];

            if (Math.Abs(denom) < 1e-30)
            {
                // Degenerate: image circle passes through infinity. Sample the Mobius map directly.
                for (int k = 0; k < nPoints; k++)
                {
                    double th = 2.0 * Math.PI * k / nPoints;
                    Complex s = rho * new Complex(Math.Cos(th), Math.Sin(th));
                    pts[k] = (a * s + b) / (c * s + d);
                }
                return pts;
            }

            Complex ctr = (b * Complex.Conjugate(d) - a * Complex.Conjugate(c) * rho * rho) / denom;
            double  rad = (a * d - b * c).Magnitude * Math.Abs(rho) / Math.Abs(denom);

            for (int k = 0; k < nPoints; k++)
            {
                double th = 2.0 * Math.PI * k / nPoints;
                pts[k] = ctr + rad * new Complex(Math.Cos(th), Math.Sin(th));
            }
            return pts;
        }

        /// <summary>
        /// Bounding box of the VSWR circle in the coordinate space of the fit plane.
        /// For Gamma: converts Z-plane circle back to Γ (as SPLData does).
        /// </summary>
        private static ViewBox VswrBoundingBox(
            Complex center, double vswr,
            SurfacePlane plane, Complex? z0ref)
            => BoundingBox(VswrLocus(center, vswr, plane, z0ref ?? new Complex(AssumedSourceZ0, 0.0)));

        /// <summary>
        /// Bounding box of the VSWR search circle for the MXX grid search.
        /// </summary>
        private static ViewBox VswrCirclePoints(
            Complex center, double vswr,
            SurfacePlane plane, Complex? z0ref)
            => VswrBoundingBox(center, vswr, plane, z0ref);

        private static ViewBox BoundingBox(Complex[] pts)
        {
            double minRe = double.MaxValue, maxRe = double.MinValue;
            double minIm = double.MaxValue, maxIm = double.MinValue;
            foreach (var p in pts)
            {
                if (p.Real < minRe) minRe = p.Real;
                if (p.Real > maxRe) maxRe = p.Real;
                if (p.Imaginary < minIm) minIm = p.Imaginary;
                if (p.Imaginary > maxIm) maxIm = p.Imaginary;
            }
            return new ViewBox(minRe, maxRe, minIm, maxIm);
        }

        // ================================================================
        //  Private — Γ renormalization
        // ================================================================

        /// <summary>
        /// Renormalize Γ from <paramref name="z0Src"/> to <paramref name="z0New"/> — generalizes the
        /// old real-only <c>z2g(50*g2z(X)/Z0)</c> (SPLData) to an arbitrary complex source/target
        /// (brief-dd-z0-renormalization.md §3): Z2G(z/z0New) = (Z−z0New)/(Z+z0New) holds algebraically
        /// for a complex z0New too (division, no conjugation), so this is an exact generalization, not
        /// an approximation — reduces to the original formula when z0Src = 50 + 0j. Short-circuits on
        /// an identical reference so the (still overwhelmingly common) default-50 case stays bit-exact.
        /// </summary>
        private static Complex RenormGamma(Complex gammaSrc, Complex z0Src, Complex z0New)
        {
            if (z0Src == z0New) return gammaSrc;
            Complex z = RfHelpers.G2Z(gammaSrc) * z0Src;   // physical impedance (Ω)
            return RfHelpers.Z2G(z / z0New);
        }

        // ================================================================
        //  Private — statistics helpers
        // ================================================================

        private static double Median(double[] values)
        {
            var sorted = (double[])values.Clone();
            Array.Sort(sorted);
            int n = sorted.Length;
            return n % 2 == 0
                ? (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0
                : sorted[n / 2];
        }

        /// <summary>
        /// Recommended compression setting — nearest of {0.1, 0.5, 1..19} ≤ median.
        /// Port of SPLData.__init__ CompressionSetting logic.
        /// </summary>
        private static double RecommendedCompressionSetting(double median)
        {
            // [0.1, 0.5, 1, 2, 3, ..., 19]
            var settings = new double[21];
            settings[0] = 0.1; settings[1] = 0.5;
            for (int i = 2; i < 21; i++) settings[i] = i - 1;

            double best    = 1.0;
            double bestDiff = double.PositiveInfinity;
            foreach (var s in settings)
            {
                double diff = median - s;
                if (diff >= 0 && diff < bestDiff)
                {
                    bestDiff = diff;
                    best     = s;
                }
            }
            return best;
        }

        // ================================================================
        //  7.4c — Power-sweep synthesis (DataInterpStack)
        //
        //  Port of SPLData.generate_PS_interpolator_at_compression
        //  and get_power_sweep.  Builds a stack of NStacks RBF surfaces,
        //  one per back-off level, and evaluates them at an arbitrary
        //  off-grid Γ to reconstruct a synthetic drive-up.
        //
        //  Sweep basis: "PavlDbm" (available input power in dBm).
        //  The 16-dB OBO ladder is in dBm, so the sweep axis must be
        //  a dBm quantity.  "PavlDbm" is the canonical dBm cube; "Pout"
        //  is stored in Watts (DbmToW-converted) and cannot serve as
        //  the sweep basis without log-conversion.
        // ================================================================

        // Canonical sweep-basis cube — available input power in dBm.
        public const string SweepKeyName = "PavlDbm";

        public const int InterpStackOBO  = 16;   // back-off span in dB
        public const int NumInterpStacks = 32;   // 2 × OBO
        public const int NumInterpSweep  = 160;  // 5 × NStacks
        public const int MinStackNodes   = 12;   // min Rbf2D.NodeCount per slice

        // ── Public result type ───────────────────────────────────────────

        /// <summary>Synthesized drive-up (metricY vs metricX) at an arbitrary load Γ.</summary>
        public sealed record PowerSweep(double[] X, double[] Y, string MetricX, string MetricY);

        // ── Private stack types ──────────────────────────────────────────

        private sealed class InterpStack
        {
            public readonly IReadOnlyList<Rbf2D> Slices;
            public readonly string               Metric;
            public InterpStack(IReadOnlyList<Rbf2D> slices, string metric)
            { Slices = slices; Metric = metric; }
        }

        private readonly Dictionary<StackKey, InterpStack> _stackCache = new();
        private readonly HashSet<string> _warnedOnce = new(StringComparer.Ordinal);

        private readonly record struct StackKey(
            int          FreqIdx,
            string       Metric,
            double       CompressionVal,
            string       CompressionReference,
            CompressionType CompressionType,
            SurfacePlane Plane,
            double?      Z0,
            RbfKernel    Kernel,
            double       Smooth);

        // ── Public API ───────────────────────────────────────────────────

        /// <summary>
        /// Synthesize a drive-up (metricY vs metricX) at an arbitrary off-grid Γ (or Z),
        /// by evaluating a stack of RBF surfaces spanning the back-off range.
        /// <para>
        /// queryCoord is in the same plane as <paramref name="plane"/>
        /// (Γ if Gamma, Z if Z), Z0-renormalized consistently with the stack.
        /// </para>
        /// Returns null if the supporting stacks cannot be built (too few points).
        /// </summary>
        public PowerSweep? GetPowerSweep(
            int freqIdx, Complex queryCoord, string metricX, string metricY,
            double compressionVal, SurfacePlane plane, double? z0 = null,
            RbfKernel kernel = RbfKernel.Multiquadric, double smooth = 1e-3,
            string? compressionReference = null, CompressionType compressionType = CompressionType.Gmax)
        {
            var comp = ConstraintSpec.AtCompression(compressionVal, compressionReference, compressionType);
            // Build (or fetch cached) the three stacks
            var sweepStack = BuildStackAtCompression(freqIdx, SweepKeyName, comp, plane, z0, kernel, smooth);
            var xStack     = BuildStackAtCompression(freqIdx, metricX,      comp, plane, z0, kernel, smooth);
            var yStack     = BuildStackAtCompression(freqIdx, metricY,      comp, plane, z0, kernel, smooth);

            // Port of get_power_sweep: NumPoints = len(DataInterpStack[key_x])
            int numSlices = Math.Min(xStack.Slices.Count,
                            Math.Min(yStack.Slices.Count, sweepStack.Slices.Count));
            if (numSlices < 2)
            {
                WarnOnce($"[LoadpullSurface] insufficient stack slices ({numSlices}) " +
                         $"for power sweep {metricX}/{metricY}; returning null.");
                return null;
            }

            // min_sweep_key = max(stack[sweep_key][0].di)
            // Guards against extrapolating below the measured low-power end.
            double minSweepKey = double.NegativeInfinity;
            foreach (double v in sweepStack.Slices[0].NodeValues)
                if (v > minSweepKey) minSweepKey = v;

            // Evaluate all three stacks at the query coordinate
            double re = queryCoord.Real, im = queryCoord.Imaginary;
            var resultX     = new double[numSlices];
            var resultY     = new double[numSlices];
            var resultSweep = new double[numSlices];
            for (int s = 0; s < numSlices; s++)
            {
                resultX[s]     = xStack.Slices[s].Evaluate(re, im);
                resultY[s]     = yStack.Slices[s].Evaluate(re, im);
                resultSweep[s] = sweepStack.Slices[s].Evaluate(re, im);
            }

            // Build final sweep axis: linspace(max(min(result_sweep), min_sweep_key),
            //                                  max(result_sweep), NumInterpSweep)
            double minRes = resultSweep[0], maxRes = resultSweep[0];
            for (int s = 1; s < numSlices; s++)
            {
                if (resultSweep[s] < minRes) minRes = resultSweep[s];
                if (resultSweep[s] > maxRes) maxRes = resultSweep[s];
            }
            double lowerBound = Math.Max(minRes, minSweepKey);
            if (lowerBound >= maxRes) lowerBound = minRes; // fallback if minSweepKey too high
            var finalSweep = Linspace(lowerBound, maxRes, NumInterpSweep);

            // Low-power HACK: clamp result_x[0] to prevent extrapolation noise
            // at the low end of the sweep (mirrors SPLData: if result_x[0] > min_sweep_key).
            // ONLY when X is the sweep key itself (brief-ddl-3 R-ddl3-3): min_sweep_key is an available-input-power
            // floor in dBm, and comparing a different X with it — Pout, some 15 dB above the drive — pulled the
            // curve's first X down to the drive's floor and drew a spike back to it at the low-power end.
            if (metricX == SweepKeyName && resultX[0] > minSweepKey) resultX[0] = minSweepKey;

            // Sort all three arrays jointly by resultSweep (ascending domain for interp)
            var sorted = SortJointly(resultSweep, resultX, resultY, numSlices);
            if (sorted.Dom.Length < 2)
            {
                WarnOnce($"[LoadpullSurface] degenerate sweep domain for {metricX}/{metricY}; returning null.");
                return null;
            }

            var interpX = new Interp1DLinear(sorted.Dom, sorted.ValsX);
            var interpY = new Interp1DLinear(sorted.Dom, sorted.ValsY);

            var finalX = new double[NumInterpSweep];
            var finalY = new double[NumInterpSweep];
            for (int i = 0; i < NumInterpSweep; i++)
            {
                finalX[i] = interpX.Eval(finalSweep[i]);
                finalY[i] = interpY.Eval(finalSweep[i]);
            }

            return new PowerSweep(finalX, finalY, metricX, metricY);
        }

        /// <summary>
        /// Whether <see cref="GetPowerSweep"/> with these arguments would only EVALUATE — its three stacks are already
        /// built — rather than fit 3 × <see cref="NumInterpStacks"/> surfaces first. What a display asks to decide whether
        /// a consumer can follow a drag or must wait for its release (brief-ddl-3 R-ddl3-7).
        /// </summary>
        public bool HasPowerSweepStacks(
            int freqIdx, string metricX, string metricY, double compressionVal, SurfacePlane plane, double? z0 = null,
            RbfKernel kernel = RbfKernel.Multiquadric, double smooth = 1e-3,
            string? compressionReference = null, CompressionType compressionType = CompressionType.Gmax)
        {
            var comp = ConstraintSpec.AtCompression(compressionVal, compressionReference, compressionType);
            bool Has(string m) => _stackCache.ContainsKey(new StackKey(freqIdx, m, compressionVal, comp.Reference,
                                                                       comp.CompressionType, plane, z0, kernel, smooth));
            return Has(SweepKeyName) && Has(metricX) && Has(metricY);
        }

        // ── BuildStackAtCompression ──────────────────────────────────────

        /// <summary>
        /// Port of SPLData.generate_PS_interpolator_at_compression.
        /// Builds NStacks RBF surfaces for <paramref name="metric"/> across the
        /// back-off ladder [P_comp − OBO .. P_comp] per grid point,
        /// where P_comp is the sweep-key (PavlDbm) value at the given compression.
        /// </summary>
        private InterpStack BuildStackAtCompression(
            int freqIdx, string metric, ConstraintSpec poutConstraint,
            SurfacePlane plane, double? z0, RbfKernel kernel, double smooth)
        {
            double compressionVal = poutConstraint.Value;
            var key = new StackKey(freqIdx, metric, compressionVal, poutConstraint.Reference, poutConstraint.CompressionType,
                                   plane, z0, kernel, smooth);
            if (_stackCache.TryGetValue(key, out var cached)) return cached;

            var fs = _freqs[freqIdx];
            var stackCompressions = CompressionsFor(fs, poutConstraint) ?? fs.Compressions;

            // Base scatter: sweep-key (PavlDbm) values at the given compression,
            // per used grid point.  sweep_compression = DataInterp[sweep_key].di
            //                       sweep_compression_index = DataInterp[sweep_key].IndexUsed
            var baseScatter    = Reduce(freqIdx, SweepKeyName, poutConstraint, plane, z0);

            int nUsed = baseScatter.Coords.Length;
            if (nUsed < MinFitNodes)
            {
                WarnOnce($"[LoadpullSurface] too few scatter points ({nUsed}) for {metric} " +
                         $"stack at compression={compressionVal}; caching empty stack.");
                var emptyStack = new InterpStack(Array.Empty<Rbf2D>(), metric);
                _stackCache[key] = emptyStack;
                return emptyStack;
            }

            // Build Y matrix [nUsed × NumInterpStacks]:
            //   Y[i][s] = metric value at grid point i, back-off slice s.
            double[][] Y = new double[nUsed][];
            for (int i = 0; i < nUsed; i++)
            {
                Y[i] = new double[NumInterpStacks];

                int    origIdx       = baseScatter.UsedGridIndices[i];
                double sweepAtComp_i = baseScatter.Values[i]; // PavlDbm at compression for this point

                // Per-point back-off ladder in PavlDbm (dBm) space
                double[] sweepRange = Linspace(
                    sweepAtComp_i - InterpStackOBO, sweepAtComp_i, NumInterpStacks);

                if (metric == "Compression")
                {
                    // Compression branch (Python: key_y == 'Compression').
                    // Domain: SweepKey[ci:] (PavlDbm from gain-peak index onward)
                    // Values: Compression[] curve (aligned to ci..)
                    // Note: Python uses loop index i (not origIdx) — replicated here.
                    if (i < fs.NGrid && fs.DriveUps.TryGetValue(SweepKeyName, out var swDUs))
                    {
                        int      ci       = stackCompressions[i].CompressionIndex;
                        double[] swFull   = swDUs[i]; // PavlDbm full drive-up (loop index i)
                        double[] compArr  = stackCompressions[i].Compression;
                        int      len      = Math.Min(swFull.Length - ci, compArr.Length);
                        if (len >= 2)
                        {
                            var swSlice = new double[len];
                            Array.Copy(swFull, ci, swSlice, 0, len);
                            var (dom, vals) = ExtractAscending(swSlice, compArr, 0);
                            if (dom.Length >= 2)
                            {
                                var interp = new Interp1DLinear(dom, vals);
                                for (int s = 0; s < NumInterpStacks; s++)
                                    Y[i][s] = interp.Eval(sweepRange[s]);
                                continue;
                            }
                        }
                    }
                    for (int s = 0; s < NumInterpStacks; s++) Y[i][s] = double.NaN;
                }
                else
                {
                    // Normal branch (Python: else).
                    // Domain: full SweepKey drive-up at origIdx (PavlDbm, ascending)
                    // Values: metric drive-up at origIdx
                    if (!fs.DriveUps.TryGetValue(SweepKeyName, out var swDUs) ||
                        !fs.DriveUps.TryGetValue(metric, out var metricDUs))
                    {
                        for (int s = 0; s < NumInterpStacks; s++) Y[i][s] = double.NaN;
                        continue;
                    }

                    double[] swDU     = swDUs[origIdx];
                    double[] metricDU = metricDUs[origIdx];
                    var (dom, vals)   = ExtractAscending(swDU, metricDU, 0);
                    if (dom.Length >= 2)
                    {
                        var interp = new Interp1DLinear(dom, vals);
                        for (int s = 0; s < NumInterpStacks; s++)
                            Y[i][s] = interp.Eval(sweepRange[s]);
                    }
                    else
                    {
                        for (int s = 0; s < NumInterpStacks; s++) Y[i][s] = double.NaN;
                    }
                }
            }

            // Fit one Rbf2D per back-off slice.
            // Min-support guard (from SPLData): keep slice only if NodeCount > MinStackNodes (> 12).
            var slices  = new List<Rbf2D>(NumInterpStacks);
            var ySlice  = new double[nUsed];
            for (int s = 0; s < NumInterpStacks; s++)
            {
                for (int i = 0; i < nUsed; i++) ySlice[i] = Y[i][s];
                try
                {
                    var rbf = new Rbf2D(baseScatter.Coords, ySlice, kernel, smooth);
                    if (rbf.NodeCount > MinStackNodes)
                        slices.Add(rbf);
                }
                catch { /* degenerate slice — skip, not fatal */ }
            }

            var stack = new InterpStack(slices, metric);
            _stackCache[key] = stack;
            return stack;
        }

        // ── Helpers ──────────────────────────────────────────────────────

        /// Sort (sweep, x, y) jointly by ascending sweep; de-duplicate sweep domain.
        private static (double[] Dom, double[] ValsX, double[] ValsY) SortJointly(
            double[] sweep, double[] x, double[] y, int n)
        {
            // Build (sweep, x, y) tuples and sort by sweep
            int[] order = new int[n];
            for (int i = 0; i < n; i++) order[i] = i;
            Array.Sort(order, (a, b) => sweep[a].CompareTo(sweep[b]));

            var dom   = new List<double>(n);
            var valsX = new List<double>(n);
            var valsY = new List<double>(n);
            double prev = double.NegativeInfinity;
            foreach (int idx in order)
            {
                double sw = sweep[idx];
                if (sw > prev)
                {
                    dom.Add(sw);
                    valsX.Add(x[idx]);
                    valsY.Add(y[idx]);
                    prev = sw;
                }
            }
            return (dom.ToArray(), valsX.ToArray(), valsY.ToArray());
        }

        private void WarnOnce(string message)
        {
            if (_warnedOnce.Add(message))
                RFNetwork.Warn(message);
        }

        private static double MaxNodeRadius(Rbf2D rbf)
        {
            double max = 0;
            for (int i = 0; i < rbf.NodeCount; i++)
            {
                double r2 = rbf.NodesRe[i] * rbf.NodesRe[i] + rbf.NodesIm[i] * rbf.NodesIm[i];
                double r  = Math.Sqrt(r2);
                if (r > max) max = r;
            }
            return max;
        }

        /// <summary>The convex hull of <paramref name="pts"/>, counter-clockwise (monotone chain); the points themselves when fewer than three.</summary>
        internal static IReadOnlyList<Complex> ConvexHull(IReadOnlyList<Complex> pts)
        {
            if (pts.Count < 3) return pts;
            var sorted = pts.OrderBy(p => p.Real).ThenBy(p => p.Imaginary).ToList();
            static double Cross(Complex o, Complex a, Complex b)
                => (a.Real - o.Real) * (b.Imaginary - o.Imaginary) - (a.Imaginary - o.Imaginary) * (b.Real - o.Real);
            static List<Complex> Chain(List<Complex> seq)
            {
                var half = new List<Complex>();
                foreach (var p in seq)
                {
                    while (half.Count >= 2 && Cross(half[^2], half[^1], p) <= 0) half.RemoveAt(half.Count - 1);
                    half.Add(p);
                }
                half.RemoveAt(half.Count - 1);
                return half;
            }
            var lower = Chain(sorted);
            sorted.Reverse();
            lower.AddRange(Chain(sorted));
            return lower;
        }

        /// <summary>Whether (re, im) is inside the polygon <paramref name="hull"/> (even-odd).</summary>
        internal static bool InsideHull(IReadOnlyList<Complex> hull, double re, double im)
        {
            if (hull.Count < 3) return false;
            bool inside = false;
            for (int i = 0, j = hull.Count - 1; i < hull.Count; j = i++)
            {
                double xi = hull[i].Real, yi = hull[i].Imaginary, xj = hull[j].Real, yj = hull[j].Imaginary;
                if (yi > im != yj > im && re < (xj - xi) * (im - yi) / (yj - yi) + xi) inside = !inside;
            }
            return inside;
        }

        private static double[] Linspace(double min, double max, int n)
        {
            var arr = new double[n];
            if (n == 1) { arr[0] = min; return arr; }
            double step = (max - min) / (n - 1);
            for (int i = 0; i < n; i++) arr[i] = min + i * step;
            return arr;
        }

        // ================================================================
        //  Private — internal types
        // ================================================================

        private sealed class GridPointCompression
        {
            public readonly int      CompressionIndex;
            public readonly double[] Compression;

            public GridPointCompression(int ci, double[] comp)
            {
                CompressionIndex = ci;
                Compression      = comp;
            }
        }

        private sealed class FreqSlice
        {
            public double    FreqHz;
            public string    LeadAxisName = "freq";
            public string    LeadAxisUnit = "Hz";
            public int       NGrid;
            public Complex[] Gammas  = Array.Empty<Complex>();
            public Complex[] Zs      = Array.Empty<Complex>();
            public double[]  PinAxis = Array.Empty<double>();
            public Dictionary<string, double[][]> DriveUps = new(StringComparer.Ordinal);
            public GridPointCompression[] Compressions = Array.Empty<GridPointCompression>();
            /// <summary>Compression curves for references other than Gt/Gmax, computed on first use.</summary>
            public Dictionary<(string Reference, CompressionType Type), GridPointCompression[]?> OtherCompressions = new();
            public double    MedianCompression;
            public double    RecommendedCompression;
            public Complex?  SourceZ;
        }

        private readonly record struct FitKey(
            int           FreqIdx,
            string        MetricY,
            ConstraintSpec Constraint,
            SurfacePlane  Plane,
            Complex?      Z0,
            RbfKernel     Kernel,
            double        Smooth,
            double?       Epsilon);
    }
}

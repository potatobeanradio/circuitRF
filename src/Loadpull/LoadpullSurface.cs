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

    public readonly record struct ConstraintSpec(ConstraintKind Kind, string MetricName, double Value)
    {
        public static ConstraintSpec AtCompression(double dB) =>
            new(ConstraintKind.Compression, "Compression", dB);

        public static ConstraintSpec AtConstantMetric(string metric, double value) =>
            new(ConstraintKind.ConstantMetric, metric, value);
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
        double?     Z0,
        int         FreqIdx,
        string      MetricY,
        ConstraintSpec Constraint,
        RbfKernel   Kernel,
        double      Smooth,
        double?     Epsilon = null);

    public sealed record SurfaceGrid(
        double[] XSpace,
        double[] YSpace,
        double[] Values);

    public sealed record MxxResult(Complex Measured, Complex Interpolated);

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

        public LoadpullSurface(DataSet data, string group = "")
        {
            _freqs = BuildFreqSlices(data, group);
            var freqs = new double[_freqs.Length];
            for (int i = 0; i < _freqs.Length; i++) freqs[i] = _freqs[i].FreqHz;
            Frequencies = Array.AsReadOnly(freqs);
        }

        public int GridPointCount(int freqIdx) => _freqs[freqIdx].NGrid;

        public double MedianCompression(int freqIdx)     => _freqs[freqIdx].MedianCompression;
        public double RecommendedCompression(int freqIdx) => _freqs[freqIdx].RecommendedCompression;

        // ── Reduce ───────────────────────────────────────────────────────────

        /// <summary>
        /// Compute scattered {coord, Y} at constant constraint, NaN-dropped.
        /// coord is Γ (Z0-renorm if z0 != null) or Z per plane.
        /// </summary>
        public ScatterReduction Reduce(
            int freqIdx, string metricY, ConstraintSpec constraint,
            SurfacePlane plane, double? z0 = null)
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
                    coord = RenormGamma(coord, z0.Value);

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
            SurfacePlane plane, double? z0 = null,
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

        // ── MaxPower / MaxEfficiency ─────────────────────────────────────────

        /// <summary>Max output power location (MXP): measured + interpolated Γ or Z.</summary>
        public MxxResult? MaxPower(
            int freqIdx, ConstraintSpec constraint,
            SurfacePlane plane, double? z0 = null,
            RbfKernel kernel = RbfKernel.Multiquadric, double smooth = 1e-3, double? epsilon = null)
            => GetMxx(freqIdx, "Pout", constraint, plane, z0, kernel, smooth, epsilon);

        /// <summary>Max drain efficiency location (MXE): measured + interpolated Γ or Z.</summary>
        public MxxResult? MaxEfficiency(
            int freqIdx, ConstraintSpec constraint,
            SurfacePlane plane, double? z0 = null,
            RbfKernel kernel = RbfKernel.Multiquadric, double smooth = 1e-3, double? epsilon = null)
            => GetMxx(freqIdx, "DE", constraint, plane, z0, kernel, smooth, epsilon);

        // ── RecommendedBox ───────────────────────────────────────────────────

        /// <summary>
        /// Auto-view-box enclosing MXP and MXE with VSWR margin, clipped
        /// to the measured data extent.  Port of SPLData.get_recommended_grid.
        /// </summary>
        public ViewBox RecommendedBox(LoadpullFit fit)
        {
            var fs        = _freqs[fit.FreqIdx];
            var mxpResult = GetMxx(fit.FreqIdx, "Pout", fit.Constraint, fit.Plane, fit.Z0, fit.Kernel, fit.Smooth, fit.Epsilon);
            var mxeResult = GetMxx(fit.FreqIdx, "DE",   fit.Constraint, fit.Plane, fit.Z0, fit.Kernel, fit.Smooth, fit.Epsilon);

            // Measured-data bounding box (from RBF node coords)
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
            var measuredBox = new ViewBox(minRe, maxRe, minIm, maxIm);

            // MXP/MXE locations; fall back to measured-box center if missing
            Complex mxp = mxpResult?.Measured ?? new Complex((minRe + maxRe) / 2, (minIm + maxIm) / 2);
            Complex mxe = mxeResult?.Measured ?? mxp;

            // VSWR include factors (SPLData: 99 for Gamma "as much as possible"; 1.3 for Z)
            double includeVswr = fit.Plane == SurfacePlane.Gamma ? 99.0 : 1.3;
            double? z0ref      = fit.Z0 ?? (fit.Plane == SurfacePlane.Gamma ? 50.0 : null);

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

            // Round to nice grid for Z-plane (SPLData: RoundFactor = xSpan/5)
            if (fit.Plane == SurfacePlane.Z)
            {
                double xSpan      = final.SpanRe;
                double roundFactor = xSpan > 0 ? xSpan / 5.0 : 1.0;
                final = new ViewBox(
                    Math.Round(final.MinRe / roundFactor) * roundFactor,
                    Math.Round(final.MaxRe / roundFactor) * roundFactor,
                    Math.Round(final.MinIm / roundFactor) * roundFactor,
                    Math.Round(final.MaxIm / roundFactor) * roundFactor);
                if (!final.IsValid) final = measuredBox;
            }

            return final;
        }

        // ── Resample ─────────────────────────────────────────────────────────

        /// <summary>
        /// Evaluate the fit on a grid.  Uses MXP/MXE auto-view-box if box is null.
        /// Returns a (resolution × resolution) value grid, row-major,
        /// with NaN outside the Γ-disk when plane==Gamma.
        /// </summary>
        public SurfaceGrid Resample(LoadpullFit fit, ViewBox? box = null, int resolution = 50)
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

            int idx = 0;
            for (int yi = 0; yi < resolution; yi++)
            {
                for (int xi = 0; xi < resolution; xi++)
                {
                    double re = xSpace[xi], im = ySpace[yi];
                    if (re * re + im * im > maxGamma * maxGamma)
                    {
                        values[idx++] = double.NaN;
                    }
                    else
                    {
                        values[idx++] = fit.Rbf.Evaluate(re, im);
                    }
                }
            }

            return new SurfaceGrid(xSpace, ySpace, values);
        }

        // ================================================================
        //  Private — compression preprocessing
        // ================================================================

        private const int    MinFitNodes   = 6;
        private const int    VswrNPoints   = 100;

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

            var poutCube     = GetCube("Pout");
            var gtCube       = GetCube("Gt");
            var gammaCube    = GetCube("GammaLoad");
            var zCube        = GetCube("ZLoad");
            var pinAxisCube  = GetCube("PavlDbm");

            // Build optional metric cubes
            var metricNames  = new[] { "Pout", "Gt", "Gp", "DE", "PAE", "PavlDbm" };
            var metricCubes  = new Dictionary<string, DataCube>(StringComparer.Ordinal);
            foreach (var m in metricNames)
                if (hasCube(m)) metricCubes[m] = GetCube(m);

            // Detect freq axis (by name)
            bool hasFreq   = poutCube.Axes.Any(a => a.Name == "freq");
            int  nFreq     = hasFreq ? poutCube.Axis("freq").Length : 1;
            int  nGrid     = poutCube.Axis("gridPoint").Length;
            int  nPin      = poutCube.Axis("pinStep").Length;

            double[]  pinAxisVals = pinAxisCube.Axis("pinStep").Values;
            double[]? freqVals   = hasFreq ? poutCube.Axis("freq").Values : null;

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

                // Compression preprocessing (§1 of brief)
                var gtDriveUps = driveUps["Gt"];
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

                slices[fi] = new FreqSlice
                {
                    FreqHz              = freqVals != null ? freqVals[fi] : 0.0,
                    NGrid               = nGrid,
                    Gammas              = gammas,
                    Zs                  = zs,
                    DriveUps            = driveUps,
                    PinAxis             = pinAxisVals,
                    Compressions        = compressions,
                    MedianCompression   = medianComp,
                    RecommendedCompression = recCompress,
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
            // else Gss: ci = 0

            // Compression curve from ci onward (0 at ci, increasing)
            int len  = n - ci;
            var comp = new double[len];
            for (int p = 0; p < len; p++)
                comp[p] = double.IsNaN(gt[ci]) ? double.NaN
                          : double.IsNaN(gt[ci + p]) ? double.NaN
                          : gt[ci] - gt[ci + p];

            return new GridPointCompression(ci, comp);
        }

        // ================================================================
        //  Private — scatter reduction
        // ================================================================

        private static double ReducePoint(
            FreqSlice fs, int gi,
            string metricY, ConstraintSpec constraint)
        {
            var comp = fs.Compressions[gi];

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
            SurfacePlane plane, double? z0,
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

            // 2. Interpolated peak — high-res search in VSWR=1.2 circle around measured
            double searchVswr = 1.2;
            double? z0ref     = z0 ?? (plane == SurfacePlane.Gamma ? 50.0 : null);
            var searchCircle  = VswrCirclePoints(measured, searchVswr, plane, z0ref);

            double cMinRe = searchCircle.MinRe, cMaxRe = searchCircle.MaxRe;
            double cMinIm = searchCircle.MinIm, cMaxIm = searchCircle.MaxIm;

            int hRes = 50;
            var hx   = Linspace(cMinRe, cMaxRe, hRes);
            var hy   = Linspace(cMinIm, cMaxIm, hRes);

            Complex interpolated = measured;
            double  interpMax    = double.NegativeInfinity;
            for (int yi = 0; yi < hRes; yi++)
            {
                for (int xi = 0; xi < hRes; xi++)
                {
                    double v = rbf.Evaluate(hx[xi], hy[yi]);
                    if (v > interpMax) { interpMax = v; interpolated = new Complex(hx[xi], hy[yi]); }
                }
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
        /// Bounding box of the VSWR circle in the coordinate space of the fit plane.
        /// For Gamma: converts Z-plane circle back to Γ (as SPLData does).
        /// </summary>
        private static ViewBox VswrBoundingBox(
            Complex center, double vswr,
            SurfacePlane plane, double? z0ref)
        {
            Complex[] pts;
            if (plane == SurfacePlane.Gamma)
            {
                // z0ref is the reference impedance for this gamma plane
                double z0 = z0ref ?? 50.0;
                // Convert Γ_center → actual Z
                Complex zActual = RfHelpers.G2Z(center) * z0;
                // Build VSWR circle in Z-plane
                Complex[] zPts = VswrCircleZ(zActual, vswr);
                // Convert each Z point back to Γ (normalized to z0)
                pts = new Complex[zPts.Length];
                for (int i = 0; i < zPts.Length; i++)
                    pts[i] = RfHelpers.Z2G(zPts[i] / z0);
            }
            else
            {
                pts = VswrCircleZ(center, vswr);
            }

            return BoundingBox(pts);
        }

        /// <summary>
        /// Bounding box of the VSWR search circle for the MXX grid search.
        /// </summary>
        private static ViewBox VswrCirclePoints(
            Complex center, double vswr,
            SurfacePlane plane, double? z0ref)
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

        /// <summary>Renormalize Γ from 50Ω to z0 (SPLData: z2g(50*g2z(X)/Z0)).</summary>
        private static Complex RenormGamma(Complex gamma50, double z0)
        {
            // g2z normalized → × 50 → / z0 → z2g
            Complex zNorm = RfHelpers.G2Z(gamma50) * (50.0 / z0);
            return RfHelpers.Z2G(zNorm);
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
            RbfKernel kernel = RbfKernel.Multiquadric, double smooth = 1e-3)
        {
            // Build (or fetch cached) the three stacks
            var sweepStack = BuildStackAtCompression(freqIdx, SweepKeyName, compressionVal, plane, z0, kernel, smooth);
            var xStack     = BuildStackAtCompression(freqIdx, metricX,      compressionVal, plane, z0, kernel, smooth);
            var yStack     = BuildStackAtCompression(freqIdx, metricY,      compressionVal, plane, z0, kernel, smooth);

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
            if (resultX[0] > minSweepKey) resultX[0] = minSweepKey;

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

        // ── BuildStackAtCompression ──────────────────────────────────────

        /// <summary>
        /// Port of SPLData.generate_PS_interpolator_at_compression.
        /// Builds NStacks RBF surfaces for <paramref name="metric"/> across the
        /// back-off ladder [P_comp − OBO .. P_comp] per grid point,
        /// where P_comp is the sweep-key (PavlDbm) value at the given compression.
        /// </summary>
        private InterpStack BuildStackAtCompression(
            int freqIdx, string metric, double compressionVal,
            SurfacePlane plane, double? z0, RbfKernel kernel, double smooth)
        {
            var key = new StackKey(freqIdx, metric, compressionVal, plane, z0, kernel, smooth);
            if (_stackCache.TryGetValue(key, out var cached)) return cached;

            var fs = _freqs[freqIdx];

            // Base scatter: sweep-key (PavlDbm) values at the given compression,
            // per used grid point.  sweep_compression = DataInterp[sweep_key].di
            //                       sweep_compression_index = DataInterp[sweep_key].IndexUsed
            var poutConstraint = ConstraintSpec.AtCompression(compressionVal);
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
                        int      ci       = fs.Compressions[i].CompressionIndex;
                        double[] swFull   = swDUs[i]; // PavlDbm full drive-up (loop index i)
                        double[] compArr  = fs.Compressions[i].Compression;
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
            public double   FreqHz;
            public int      NGrid;
            public Complex[] Gammas  = Array.Empty<Complex>();
            public Complex[] Zs      = Array.Empty<Complex>();
            public double[]  PinAxis = Array.Empty<double>();
            public Dictionary<string, double[][]> DriveUps = new(StringComparer.Ordinal);
            public GridPointCompression[] Compressions = Array.Empty<GridPointCompression>();
            public double MedianCompression;
            public double RecommendedCompression;
        }

        private readonly record struct FitKey(
            int           FreqIdx,
            string        MetricY,
            ConstraintSpec Constraint,
            SurfacePlane  Plane,
            double?       Z0,
            RbfKernel     Kernel,
            double        Smooth,
            double?       Epsilon);
    }
}

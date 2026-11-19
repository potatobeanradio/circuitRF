// brief-paa-9 R-paa9-2…R-paa9-4 — one frame of the Phased Array tool: the (u,v) map, the two principal cuts, the readouts and the grating
// lobes, every one formed from the run's own ArrayFieldState and nothing else (no second solve of the circuit).
//
// WHAT A FRAME COSTS, AND WHAT IT NEVER BUILDS. A drag re-forms a frame per step, so nothing here may grow a cache per step: the map is
// PAA-2's padded FFT (no phase table) times the element pattern sampled on ONE direction set per lattice geometry and frequency (kept in
// MapLayouts, so the element pattern is interpolated once and read thereafter); the beam's search and cuts are ArrayBeam's transient sets,
// summed through ArrayPattern.SumOnce and never kept. So a drag builds no phase table at all (gate 6, ArrayPattern.TablesBuilt).
//
// THE RADIATED POWER IS INTEGRATED ON THE MAP, NOT ON THE RECORD'S GRID. Directivity and the power balance divide by it, and the record's
// grid (1°, or the fixtures' 5°) puts one or two samples across a 64 × 64 beam (note §6.2): on the 5° fixture it read a 64 × 64 array's
// directivity 4 dB low, below its own gain. The map has eight samples to a beamwidth; ∫U/cosθ du dv over it is the integral.
//
// THE MAP'S ELEMENT PATTERN is the centre element's: the array factor of the whole array times one element's pattern. At L0 that is the
// pattern; at L1 with position classes the edge elements' own patterns differ, and the cuts and readouts (which sum each class) are the
// numbers to read — the map is the picture of where the beam and the grating lobes are.

using System.Numerics;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Arrays;
using CircuitRF.Design.Em3d;
using CircuitRF.Engine.AntennaArray;
using RfCore.Data;

namespace CircuitRF.Ui.PhasedArray;

/// <summary>The (u,v) map on a regular grid: <see cref="Db"/>[iv·Nu + iu] at u = (iu − (Nu−1)/2)·Du, v = ((Nv−1)/2 − iv)·Dv (row 0 is +v,
/// the top of the picture); NaN outside the visible circle.</summary>
public sealed record UvGrid(float[] Db, int Nu, int Nv, double Du, double Dv, double MaxDb, double PeakU, double PeakV)
{
    /// <summary>The array factor alone (isotropic elements), power over its own maximum, on the same cells; NaN outside the circle.</summary>
    public float[] FactorDb { get; init; } = [];

    /// <summary>A value of <paramref name="cells"/> at (u, v), bilinear in power; NaN outside the grid's visible circle.</summary>
    public double Sample(float[] cells, double u, double v)
    {
        if (Du <= 0 || Dv <= 0) return double.NaN;
        double fu = u / Du + (Nu - 1) / 2.0, fv = (Nv - 1) / 2.0 - v / Dv;
        int iu = (int)Math.Floor(fu), iv = (int)Math.Floor(fv);
        if (iu < 0 || iv < 0 || iu + 1 >= Nu || iv + 1 >= Nv) return double.NaN;
        double tu = fu - iu, tv = fv - iv, sum = 0, weight = 0;
        foreach (var (di, dj, w) in new[] { (0, 0, (1 - tu) * (1 - tv)), (1, 0, tu * (1 - tv)), (0, 1, (1 - tu) * tv), (1, 1, tu * tv) })
        {
            float c = cells[(iv + dj) * Nu + iu + di];
            if (float.IsNaN(c) || w <= 0) continue;
            sum += w * Math.Pow(10, c / 10.0);
            weight += w;
        }
        return weight > 0 && sum > 0 ? 10 * Math.Log10(sum / weight) : double.NaN;
    }
}

/// <summary>One principal cut: the angle from the peak along it, and co-polar, cross-polar and the isotropic array factor (L0), dB.</summary>
/// <param name="CoDb">Co-polar over the beam's peak power.</param>
/// <param name="CrossDb">Cross-polar over the beam's peak power.</param>
/// <param name="IsotropicDb">The array factor of isotropic elements with the same weights, over its own peak in the cut.</param>
public sealed record ArrayCutTraces(string Plane, double[] OffsetDeg, double[] CoDb, double[] CrossDb, double[] IsotropicDb);

/// <summary>R-paa9-4 — the readouts (R-paa2-7 and PAA-7).</summary>
public sealed record ArrayReadouts(
    double DirectivityDbi, double GainDbi, double RealizedGainDbi, double? EirpDbm, double? GoverTDbK,
    double ElevationBeamwidthDeg, double AzimuthBeamwidthDeg, double? SidelobeDb, double? PointingErrorDeg, double ScanLossDb,
    double GratingMarginDeg, double WorstGammaActive, double PowerBalance, string Level, double PeakThetaDeg, double PeakPhiDeg);

/// <summary>R-paa9-2…4 — one evaluated frame.</summary>
public sealed record PhasedArrayFrame(
    ArrayFieldState State, UvGrid Map, ArrayBeamMetrics Beam, ArrayCutTraces Elevation, ArrayCutTraces Azimuth, ArrayReadouts Readouts,
    IReadOnlyList<(double U, double V)> GratingLobes, IReadOnlyDictionary<string, double> Measurements)
{
    /// <summary>Milliseconds the circuit evaluation took, and the frame's own forming.</summary>
    public double EvaluationMs { get; init; }
    public double FormingMs { get; init; }

    /// <summary>Where the forming went, ms per part.</summary>
    public string Breakdown { get; init; } = "";

    /// <summary>The run's warnings, as it reported them.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>brief-paa-12 R-paa12-2 — "one point of sweep(s) …: var = value" when the tool collapsed the sweeps wrapping the waves
    /// source to evaluate this frame; null otherwise.</summary>
    public string? SweepPoint { get; init; }

    /// <summary>False for a drag's step, which evaluates no measurements: its empty <see cref="Measurements"/> say nothing about what the
    /// bench measures, so the colour-by list keeps what the last settled frame offered (brief-paa-12).</summary>
    public bool Measured { get; init; } = true;
}

/// <summary>R-paa9-3 — the map's cells per lattice geometry and frequency: which FFT point each visible cell reads its array factor from, and
/// the direction its element pattern is sampled at, so the element pattern is interpolated once per geometry and read thereafter.</summary>
/// <remarks>The FFT covers one period of the array factor, λ/dx by λ/dy, which at a pitch over half a wavelength is narrower than the visible
/// circle. The array factor is periodic over the lattice's reciprocal vectors (a shift by one turns every element's phase by a whole cycle),
/// so a visible cell outside the period reads the FFT point one reciprocal vector away; its element pattern is its own direction's.</remarks>
public sealed class MapLayouts
{
    private readonly object _gate = new();
    private (object Key, Layout Layout)? _last;
    private (object Key, double Peak)? _broadside;

    /// <summary>Entry k is grid cell <see cref="Cell"/>[k], reading FFT point <see cref="Source"/>[k], its element pattern at direction k;
    /// <see cref="Inside"/>[k] is false for a rim cell drawn to close the circle whose centre lies past it: it is not integrated.</summary>
    public sealed record Layout(ArrayDirections Directions, int[] Cell, int[] Source, int Nu, int Nv, double Du, double Dv, bool[] Inside);

    /// <summary>The broadside beam's peak power for scan loss: it depends on the amplitudes alone, which a steer drag does not move, so it is
    /// evaluated again only when they (or the lattice or frequency) change.</summary>
    public double BroadsidePeak(ArrayFieldState state)
    {
        var a = state.A;
        var hash = new HashCode();
        hash.Add(state.Lattice.GeometryKey);
        hash.Add(state.Hz);
        foreach (var w in a) hash.Add(Math.Round(w.Magnitude, 12));
        var key = (hash.ToHashCode(), a.Length, state.Elements);
        lock (_gate) if (_broadside is { } b && Equals(b.Key, key)) return b.Peak;
        Complex[] boresight = [.. a.Select(w => new Complex(w.Magnitude, 0))];
        double peak = ArrayBeam.PeakPower(state.Lattice, boresight, state.Elements, state.Hz, 0, 0);
        lock (_gate) _broadside = (key, peak);
        return peak;
    }

    public Layout For(ArrayLattice lattice, double hz, ArrayUvMap map)
    {
        var key = (lattice.Rows, lattice.Cols, lattice.Dx, lattice.Dy, lattice.Kind, hz, map.U.Length);
        lock (_gate)
        {
            if (_last is { } l && Equals(l.Key, key)) return l.Layout;
            double du = Step(map.U), dv = Step(map.V);
            var at = new Dictionary<(int, int), int>();
            for (int i = 0; i < map.U.Length; i++)
                at[(du > 0 ? (int)Math.Round(map.U[i] / du) : 0, dv > 0 ? (int)Math.Round(map.V[i] / dv) : 0)] = i;
            // a single row (or column) has no pitch along it, so its array factor is the same at every v (or u): that axis is drawn at the
            // other's step, every cell reading the FFT point at its own u (or v)
            bool flatU = !(du > 0), flatV = !(dv > 0);
            if (flatU && flatV) du = dv = 0.02;
            else if (flatV) dv = du;
            else if (flatU) du = dv;
            int hu = (int)Math.Ceiling(1 / du), hv = (int)Math.Ceiling(1 / dv);
            int nu = 2 * hu + 1, nv = 2 * hv + 1;
            // the reciprocal vectors in bins (PhasedArrayFrames.GratingLobes' b1, b2, times λ)
            double lambda = Engine.Mom.EmConstants.C0 / hz;
            double s = lattice.Rows > 1 ? lattice.RowShift(1) - lattice.RowShift(0) : 0;
            (int U, int V) b1 = (du > 0 && lattice.Cols > 1 ? (int)Math.Round(lambda / lattice.Dx / du) : 0,
                                 dv > 0 && lattice.Cols > 1 && lattice.Rows > 1 ? (int)Math.Round(-s * lambda / (lattice.Dx * lattice.Dy) / dv) : 0);
            (int U, int V) b2 = (0, dv > 0 && lattice.Rows > 1 ? (int)Math.Round(lambda / lattice.Dy / dv) : 0);
            var cells = new List<int>();
            var inside = new List<bool>();
            double rim = 1 + 0.75 * Math.Max(du, dv);
            var sources = new List<int>();
            var th = new List<double>();
            var ph = new List<double>();
            for (int bu = -hu; bu <= hu; bu++)
                for (int bv = -hv; bv <= hv; bv++)
                {
                    double u = bu * du, v = bv * dv;
                    if (u * u + v * v > rim * rim) continue;
                    int src = -1;
                    if (!at.TryGetValue((flatU ? 0 : bu, flatV ? 0 : bv), out src))
                    {
                        src = -1;
                        for (int p = -2; p <= 2 && src < 0; p++)
                            for (int q = -2; q <= 2 && src < 0; q++)
                                if (at.TryGetValue((flatU ? 0 : bu - p * b1.U - q * b2.U, flatV ? 0 : bv - p * b1.V - q * b2.V), out int k)) src = k;
                    }
                    if (src < 0) continue;
                    cells.Add((hv - bv) * nu + bu + hu);
                    inside.Add(u * u + v * v <= 1 + 1e-12);
                    sources.Add(src);
                    double r = Math.Min(1, Math.Sqrt(u * u + v * v));
                    th.Add(Math.Asin(r));
                    ph.Add(Math.Atan2(v, u));
                }
            var layout = new Layout(ArrayDirections.Of(th, ph), [.. cells], [.. sources], nu, nv, du, dv, [.. inside]);
            _last = (key, layout);
            return layout;
        }
    }

    private static double Step(double[] axis)
    {
        double best = double.PositiveInfinity;
        foreach (double a in axis) if (Math.Abs(a) > 1e-12 && Math.Abs(a) < best) best = Math.Abs(a);
        return double.IsPositiveInfinity(best) ? 0 : best;
    }
}

/// <summary>R-paa9-2…4 — forms a frame from a run's state.</summary>
public static class PhasedArrayFrames
{
    /// <summary>The frame of <paramref name="state"/>. <paramref name="steer"/> is the requested (θ₀, φ₀) in degrees when the weights are a
    /// steer() that resolved — the pointing error is against it; <paramref name="grouped"/> the run's results, for the EIRP's drive level.</summary>
    public static PhasedArrayFrame Build(ArrayFieldState state, (double ThetaDeg, double PhiDeg)? steer, DataSet? grouped,
                                         IReadOnlyDictionary<string, double> measurements, double? goverT, MapLayouts? layouts = null)
    {
        var lattice = state.Lattice;
        double hz = state.Hz;
        var a = state.A;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var parts = new List<string>();
        void Lap(string what) { parts.Add($"{what} {clock.Elapsed.TotalMilliseconds:0.0}"); clock.Restart(); }

        // ── the (u,v) map: the FFT of the weights times the centre element's pattern ─────────────────────────────
        var uv = ArrayUv.Map(lattice, a, hz);
        var layout = (layouts ?? new MapLayouts()).For(lattice, hz, uv);
        int centre = (lattice.Rows / 2) * lattice.Cols + lattice.Cols / 2;
        var pattern = state.Elements.Patterns[state.Elements.PatternOf[centre]];
        var (et, ep) = pattern.Sample(layout.Directions);
        double perWatt = 4 * Math.PI / (2 * ArrayWaves.Eta0) / AvailablePeakW(state);
        var db = new float[layout.Nu * layout.Nv];
        var factor = new float[layout.Nu * layout.Nv];
        Array.Fill(db, float.NaN);
        Array.Fill(factor, float.NaN);
        double max = double.NegativeInfinity, afMax = 0, prad = 0;
        int peak = 0;
        foreach (var f in uv.Factor) afMax = Math.Max(afMax, f.Magnitude);
        for (int k = 0; k < layout.Cell.Length; k++)
        {
            double af = uv.Factor[layout.Source[k]].Magnitude;
            double field = af * af * (et[k].Magnitude * et[k].Magnitude + ep[k].Magnitude * ep[k].Magnitude);
            double p = field * perWatt;
            // the radiated power, ∫U dΩ = ∫∫U/cosθ du dv over the visible circle; cosθ is kept off zero at the rim, where a cell's
            // centre can sit within half a cell of the horizon
            double cos = Math.Max(Math.Cos(layout.Directions.ThetaRad[k]), 0.5 * Math.Sqrt(layout.Du * layout.Dv));
            if (layout.Inside[k]) prad += field / (2 * ArrayWaves.Eta0) * layout.Du * layout.Dv / cos;
            double v = p > 0 ? 10 * Math.Log10(p) : -300;
            db[layout.Cell[k]] = (float)v;
            factor[layout.Cell[k]] = af > 0 ? (float)(20 * Math.Log10(af / afMax)) : -300f;
            if (v > max) { max = v; peak = k; }
        }
        double peakU = layout.Directions.U[peak], peakV = layout.Directions.V[peak];
        var map = new UvGrid(db, layout.Nu, layout.Nv, layout.Du, layout.Dv, max, peakU, peakV) { FactorDb = factor };
        Lap("map");

        // ── the beam, from PAA-2's local evaluation, started at the map's maximum ───────────────────────────────
        var (startTheta, startPhi) = ArrayDirections.Angles((peakU, peakV, Math.Sqrt(Math.Max(0, 1 - peakU * peakU - peakV * peakV))));
        var beam = ArrayBeam.Evaluate(lattice, a, state.Elements, hz, startTheta * 180 / Math.PI, startPhi * 180 / Math.PI,
                                      steer?.ThetaDeg, steer?.PhiDeg);

        Lap("beam");
        var elevation = Traces(beam.Elevation, beam, map);
        var azimuth = Traces(beam.Azimuth, beam, map);
        Lap("cuts");

        // ── readouts ─────────────────────────────────────────────────────────────────────────────────────────────
        double u = beam.PeakPower / (2 * ArrayWaves.Eta0);
        double Db(double x) => 10 * Math.Log10(x);
        double directivity = Db(4 * Math.PI * u / prad);
        double gain = Db(4 * Math.PI * u / (state.AcceptedW / 2));
        double realized = Db(4 * Math.PI * u / AvailablePeakW(state));
        double? eirp = DriveDbm(grouped, state.Analysis) is { } drive ? realized + drive : null;

        double inc = a.Sum(w => w.Magnitude * w.Magnitude);
        double broad = (layouts ?? new MapLayouts()).BroadsidePeak(state);
        double scanLoss = ArrayBeam.ScanLossDb(beam.PeakPower, inc, broad, inc);
        Lap("scan loss");

        var lobe = ArrayBeam.GratingLobe(lattice, hz, beam.PeakThetaDeg, beam.PeakPhiDeg);
        double s0 = Math.Sin(beam.PeakThetaDeg * Math.PI / 180);
        double margin = double.IsPositiveInfinity(lobe.Margin) ? double.PositiveInfinity
                      : Math.Asin(Math.Clamp(s0 + lobe.Margin, -1, 1)) * 180 / Math.PI - beam.PeakThetaDeg;

        double worst = 0, accepted = 0;
        for (int e = 0; e < lattice.Count; e++)
        {
            if (state.ActiveGamma(e) is { } g) worst = Math.Max(worst, g.Magnitude);
            accepted += state.AcceptedBy(e);
        }
        var readouts = new ArrayReadouts(directivity, gain, realized, eirp, goverT, beam.ElevationBeamwidthDeg, beam.AzimuthBeamwidthDeg,
                                         beam.FirstSidelobeDb, beam.PointingErrorDeg, scanLoss, margin, worst, prad / accepted,
                                         LevelWords(state.Level), beam.PeakThetaDeg, beam.PeakPhiDeg);

        var peakDir = ArrayDirections.Vector(beam.PeakThetaDeg * Math.PI / 180, beam.PeakPhiDeg * Math.PI / 180);
        return new PhasedArrayFrame(state, map, beam, elevation, azimuth, readouts, GratingLobes(lattice, hz, peakDir.X, peakDir.Y), measurements)
            { Breakdown = string.Join(", ", parts) + " ms" };
    }

    /// <summary>The power the drive makes available on the pattern's own scale. The run scales its waves so that Σ|a|² at the drive is
    /// <see cref="ArrayFieldState.AvailableW"/>, and a peak wave carries ½|a|², which is the convention U and every accepted power here use;
    /// so the available power is half the stated figure, and gains formed on it equal the run's own GainDbi and RealizedGainDbi.</summary>
    public static double AvailablePeakW(ArrayFieldState state) => state.AvailableW / 2;

    /// <summary>The level (D3) in words.</summary>
    public static string LevelWords(PhasedArrayLevel level) => level switch
    {
        PhasedArrayLevel.Isolated => "Isolated (L0)",
        PhasedArrayLevel.Embedded => "Embedded (L1)",
        _ => "Exact (L2)",
    };

    /// <summary>The power the run's EIRP is stated at, dBm: its published peak EIRP less its published peak realized gain; null when the run
    /// published neither.</summary>
    private static double? DriveDbm(DataSet? grouped, string analysis)
    {
        if (grouped is null || !grouped.Groups.Contains(analysis)) return null;
        var cubes = grouped.CubesIn(analysis);
        double? First(string name) => cubes.TryGetValue($"farfield.{name}", out var c) && c.BufferLength > 0 && c.DataKind == DataKind.Real && double.IsFinite(c.RealValues[0]) ? c.RealValues[0] : null;
        return First("PeakEirpDbm") is { } e && First("RealizedGainDbi") is { } g ? e - g : null;
    }

    /// <summary>The grating lobes of a beam at direction cosines (u₀, v₀): u₀ + λ·(p·b₁ + q·b₂) over the lattice's reciprocal vectors,
    /// within |u|, |v| ≤ 1.6 — a lobe inside the unit circle is in real space.</summary>
    public static IReadOnlyList<(double U, double V)> GratingLobes(ArrayLattice lattice, double hz, double u0, double v0)
    {
        double lambda = Engine.Mom.EmConstants.C0 / hz;
        var lobes = new List<(double, double)>();
        if (lattice.Cols < 2 && lattice.Rows < 2) return lobes;
        double s = lattice.Rows > 1 ? lattice.RowShift(1) - lattice.RowShift(0) : 0;
        // basis a1 = (dx, 0), a2 = (s, dy); reciprocal b1 = (1/dx, −s/(dx·dy)), b2 = (0, 1/dy)
        double b1u = lattice.Cols > 1 ? lambda / lattice.Dx : 0, b1v = lattice.Cols > 1 && lattice.Rows > 1 ? -s * lambda / (lattice.Dx * lattice.Dy) : 0;
        double b2v = lattice.Rows > 1 ? lambda / lattice.Dy : 0;
        for (int p = -4; p <= 4; p++)
            for (int q = -4; q <= 4; q++)
            {
                if (p == 0 && q == 0) continue;
                if ((b1u == 0 && p != 0) || (b2v == 0 && q != 0)) continue;
                double u = u0 + p * b1u, v = v0 + p * b1v + q * b2v;
                if (Math.Abs(u) <= 1.6 && Math.Abs(v) <= 1.6) lobes.Add((u, v));
            }
        return lobes;
    }

    /// <summary>A cut's co- and cross-polar parts (Ludwig-3, referred to whichever of x and y is co-polar at the peak) and the isotropic
    /// array factor along it, read off the map's FFT (eight samples to a beamwidth, a picture's worth for a dimmed comparison curve).</summary>
    private static ArrayCutTraces Traces(ArrayCut cut, ArrayBeamMetrics beam, UvGrid map)
    {
        var f = cut.Field!;
        var dirs = f.Directions;
        int n = dirs.Count, mid = n / 2;
        Complex X(int d) { var (sp, cp) = Math.SinCos(dirs.PhiRad[d]); return f.ETheta[d] * cp - f.EPhi[d] * sp; }
        Complex Y(int d) { var (sp, cp) = Math.SinCos(dirs.PhiRad[d]); return f.ETheta[d] * sp + f.EPhi[d] * cp; }
        bool xCo = X(mid).Magnitude >= Y(mid).Magnitude;
        var co = new double[n];
        var cross = new double[n];
        double Rel(Complex z) => z == Complex.Zero ? double.NegativeInfinity : 10 * Math.Log10(z.Magnitude * z.Magnitude / beam.PeakPower);
        for (int d = 0; d < n; d++)
        {
            co[d] = Rel(xCo ? X(d) : Y(d));
            cross[d] = Rel(xCo ? Y(d) : X(d));
        }
        var iso = new double[n];
        for (int d = 0; d < n; d++) iso[d] = dirs.W[d] < 0 ? double.NegativeInfinity : map.Sample(map.FactorDb, dirs.U[d], dirs.V[d]);
        double isoMax = iso.Where(double.IsFinite).DefaultIfEmpty(0).Max();
        for (int d = 0; d < n; d++) iso[d] -= isoMax;
        return new ArrayCutTraces(cut.Plane, cut.OffsetDeg, co, cross, iso);
    }
}

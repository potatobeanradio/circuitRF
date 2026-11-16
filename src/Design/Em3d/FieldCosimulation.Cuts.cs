// brief-paa-11 R-paa11-1 / R-paa11-2 — a fields analysis on a Phased Antenna Array publishes its pattern's PLANE CUTS beside the overview.
//
// The overview (farfield.U, Etheta, Ephi) is on the record's grid and stays there: it is the whole sphere the 3D view shows. A cut is one
// plane φc through boresight over a signed angle t from −90° to +90° (t < 0 is the plane's φc + 180° half), at a step of a tenth of the
// array's beamwidth or finer (ArrayPattern.CutGrid), with the element pattern interpolated and the array factor exact — the evaluation the
// beam's metrics already make (note §6.2), now published so a display can draw what the metrics read.
//
// THE CUT GRID IS FIXED ACROSS A SWEEP. A stacked cube has one angle axis, and the step depends on the lattice and the frequency, which a
// sweep of dx or of the frequency moves; so a sweep keeps its first point's grid (CutMemo, threaded from the sweep's runner as SweepPoints
// keeps its first frequency) and says when a later point's beam asks for a finer one.

using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Arrays;
using CircuitRF.Engine.AntennaArray;
using CircuitRF.Engine.Mom;
using RfCore.Data;

namespace CircuitRF.Design.Em3d;

public static partial class FieldCosimulation
{
    /// <summary>The cut cubes, in the <c>farfield</c> group: [freq, cut, angle, port].</summary>
    public const string CutUCube = "CutU", CutEthetaCube = "CutEtheta", CutEphiCube = "CutEphi";

    /// <summary>The most planes <c>cuts=</c> may name.</summary>
    public const int MaxCutPlanes = 16;

    /// <summary>One sweep's cut grid, set by its first point and read by every other, so the cubes stack.</summary>
    public sealed class CutMemo
    {
        internal ArrayCutGrid? Grid;
    }

    /// <summary>R-paa11-1 — <c>cuts=</c>: the planes in degrees, comma-separated; blank for <see cref="ArrayPattern.DefaultCutPlanesDeg"/>.</summary>
    internal static double[] CutPlanesOf(FieldsAnalysis fa, string who)
    {
        string text = fa.CutsExpr.Trim();
        if (text.Length == 0) return [.. ArrayPattern.DefaultCutPlanesDeg];
        var items = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (items.Length > MaxCutPlanes)
            throw new InvalidOperationException($"{who}: cuts= names {items.Length} planes, and a run cuts at most {MaxCutPlanes}. Name fewer.");
        var planes = new List<double>();
        foreach (string item in items)
        {
            if (!double.TryParse(item, NumberStyles.Float, CultureInfo.InvariantCulture, out double phi) || !double.IsFinite(phi))
                throw new InvalidOperationException($"{who}: cuts= item '{item}' is not a plane: write each plane's φ in degrees, comma-separated (0,45,90).");
            // a plane and the one 180° round are the same plane drawn the other way about
            double key = phi - 180 * Math.Floor(phi / 180);
            if (planes.Any(p => Math.Abs(p - 180 * Math.Floor(p / 180) - key) < 1e-9))
                throw new InvalidOperationException($"{who}: cuts= names the plane φ = {G(phi)}° twice (a plane and the one 180° round are one plane, from −90° to +90°).");
            planes.Add(phi);
        }
        if (planes.Count == 0) return [.. ArrayPattern.DefaultCutPlanesDeg];
        return [.. planes];
    }

    /// <summary>The record's θ step, degrees, when its θ axis is uniform; zero otherwise.</summary>
    internal static double RecordThetaStepDeg(PlanarFarFieldGrid grid)
    {
        var th = grid.ThetaDeg;
        if (th.Count < 2) return 0;
        double step = th[1] - th[0];
        for (int i = 2; i < th.Count; i++)
            if (Math.Abs(th[i] - th[i - 1] - step) > 1e-9 * Math.Max(1, Math.Abs(step))) return 0;
        return step;
    }

    /// <summary>The cut grid of one run: the sweep's first point's when there is one, else the array's at the highest frequency.</summary>
    private static ArrayCutGrid CutGridOf(FieldsAnalysis fa, PhasedArrayModel pa, PortFarFieldSet far, IReadOnlyList<double> freqs, CutMemo? memo, List<string> lines)
    {
        string who = $"Fields '{fa.Name}'";
        double hz = freqs.Max();
        double recordStep = RecordThetaStepDeg(far.Grid);
        if (memo?.Grid is { } kept)
        {
            double asks = ArrayPattern.CutStepDeg(ArrayBeam.EstimateBeamwidthDeg(pa.Lattice, hz), recordStep, out _);
            if (asks < kept.StepDeg * (1 - 1e-9))
                lines.Add($"{who}: the pattern cuts keep the sweep's first step, {G(kept.StepDeg)}°, so every point stacks on one angle axis; at this point " +
                          $"the array's beam asks for {G(asks)}°.");
            return kept;
        }
        var planes = CutPlanesOf(fa, who);
        // R-paa11-6 — the same grid object for the same geometry, frequency and planes, so the tables and sampled patterns it keeps are
        // formed once across a slider's drag rather than at every move
        string key = string.Create(CultureInfo.InvariantCulture, $"{pa.Lattice.GeometryKey}|{hz:R}|{recordStep:R}|{string.Join(",", planes.Select(p => p.ToString("R", CultureInfo.InvariantCulture)))}");
        ArrayCutGrid grid;
        lock (KeptCutGrids)
        {
            int at = KeptCutGrids.FindIndex(k => k.Key == key);
            if (at >= 0)
            {
                grid = KeptCutGrids[at].Grid;
                KeptCutGrids.RemoveAt(at);
            }
            else
            {
                grid = ArrayPattern.CutGrid(pa.Lattice, hz, planes, recordStep);
                if (KeptCutGrids.Count >= 6) KeptCutGrids.RemoveAt(0);
            }
            KeptCutGrids.Add((key, grid));
        }
        if (memo is not null) memo.Grid = grid;
        return grid;
    }

    /// <summary>The cut grids kept across evaluations, the most recently used last.</summary>
    private static readonly List<(string Key, ArrayCutGrid Grid)> KeptCutGrids = [];

    /// <summary>
    /// R-paa11-1 — the cuts of a run whose pattern is one weighting per frequency (the linear solve's or a harmonic-balance solution's waves),
    /// referred to 1 V at the drive as the overview is; and R-paa11-2's sentences.
    /// </summary>
    private static void AddArrayCuts(DataSet data, FieldsAnalysis fa, PhasedArrayModel pa, PortFarFieldSet far, double[] freqs, CosimWaves[] waves,
                                     ArrayElements[] elements, int port, CutMemo? memo, List<string> lines)
    {
        var grid = CutGridOf(fa, pa, far, freqs, memo, lines);
        int nf = freqs.Length, nc = grid.CutPhiDeg.Length, na = grid.AngleDeg.Length;
        var u = new double[nf * nc * na];
        var et = new Complex[nf * nc * na];
        var ep = new Complex[nf * nc * na];
        for (int f = 0; f < nf; f++)
        {
            var field = ArrayPattern.Cuts(pa.Lattice, waves[f].A, elements[f], grid, freqs[f]);
            Complex v = DriveVoltage(waves[f]);
            for (int d = 0; d < grid.Count; d++)
            {
                Complex t = field.ETheta[d] / v, p = field.EPhi[d] / v;
                int at = f * grid.Count + d;
                et[at] = t;
                ep[at] = p;
                u[at] = (t.Magnitude * t.Magnitude + p.Magnitude * p.Magnitude) / (2.0 * FarFieldStage.Eta0);
            }
        }
        Axis[] Ax() => CutAxes(freqs, grid, port);
        data.Add($"{PlanarFarField.Group}.{CutEthetaCube}", new DataCube(Ax(), et) { Unit = "V" });
        data.Add($"{PlanarFarField.Group}.{CutEphiCube}", new DataCube(Ax(), ep) { Unit = "V" });
        data.Add($"{PlanarFarField.Group}.{CutUCube}", new DataCube(Ax(), u) { Unit = "W/sr" });
        lines.AddRange(CutSentences(fa, grid, far, $"{CutUCube}, {CutEthetaCube} and {CutEphiCube}"));
    }

    /// <summary>brief-paa-11 — an envelope's time-averaged cut, the quadratic form of the incident waves' covariance over each element's own
    /// contribution, as its overview farfield.U is (FromEnvelope). Watts per steradian of the run's own drive, as that overview is.</summary>
    private static void AddEnvelopeCuts(DataSet data, FieldsAnalysis fa, PhasedArrayModel pa, PortFarFieldSet far, double hz, Complex[][] a, int t0,
                                        CutMemo? memo, List<string> lines)
    {
        var grid = CutGridOf(fa, pa, far, [hz], memo, lines);
        var (bt, bp) = ArrayPattern.CutBasis(pa.Lattice, Elements(pa, far, hz), grid, hz);
        int n = pa.PortCount, nt = a.Length, measured = Math.Max(1, nt - t0);
        var cov = new Complex[n, n];
        for (int t = t0; t < nt; t++)
            for (int p = 0; p < n; p++)
            {
                if (a[t][p] == Complex.Zero) continue;
                for (int q = 0; q < n; q++) cov[p, q] += a[t][p] * Complex.Conjugate(a[t][q]);
            }
        for (int p = 0; p < n; p++) for (int q = 0; q < n; q++) cov[p, q] /= measured;
        var u = new double[grid.Count];
        Parallel.For(0, grid.Count, d =>
        {
            double s = 0;
            for (int p = 0; p < n; p++)
            {
                Complex yt = 0, yp = 0;
                for (int q = 0; q < n; q++)
                {
                    yt += cov[p, q] * Complex.Conjugate(bt[q][d]);
                    yp += cov[p, q] * Complex.Conjugate(bp[q][d]);
                }
                s += (bt[p][d] * yt).Real + (bp[p][d] * yp).Real;
            }
            u[d] = Math.Max(0, s) / (2.0 * FarFieldStage.Eta0);
        });
        data.Add($"{PlanarFarField.Group}.{CutUCube}", new DataCube(CutAxes([hz], grid, 1), u) { Unit = "W/sr" });
        lines.AddRange(CutSentences(fa, grid, far, $"{CutUCube} (time-averaged, as farfield.U is)"));
    }

    private static Axis[] CutAxes(IReadOnlyList<double> freqs, ArrayCutGrid grid, int port) =>
    [
        new Axis("freq", [.. freqs], "Hz"),
        new Axis("cut", (double[])grid.CutPhiDeg.Clone(), "deg"),
        new Axis("angle", (double[])grid.AngleDeg.Clone(), "deg"),
        new Axis("port", [port], ""),
    ];

    /// <summary>The step the cuts took (R-paa11-1) and, when the overview's grid is coarser than a tenth of the beam, that it is (R-paa11-2).</summary>
    private static IEnumerable<string> CutSentences(FieldsAnalysis fa, ArrayCutGrid grid, PortFarFieldSet far, string cubes)
    {
        string planes = string.Join(", ", grid.CutPhiDeg.Select(p => $"{G(p)}°"));
        string why = grid.RecordStepDeg > 0 && grid.StepDeg < grid.RecordStepDeg
            ? $"the record's {G(grid.RecordStepDeg)}° step divided by {Math.Round(grid.RecordStepDeg / grid.StepDeg).ToString(CultureInfo.InvariantCulture)}, no coarser than a tenth of the array's estimated {G(grid.EstimatedBeamwidthDeg)}° beamwidth"
            : grid.StepDeg <= ArrayPattern.CutFloorStepDeg * (1 + 1e-9)
                ? $"the finest a cut takes; a tenth of the array's estimated {G(grid.EstimatedBeamwidthDeg)}° beamwidth is finer"
                : $"a tenth of the array's estimated {G(grid.EstimatedBeamwidthDeg)}° beamwidth or the record's own step";
        yield return $"Fields '{fa.Name}': {PlanarFarField.Group}.{cubes} cut the pattern in the {(grid.CutPhiDeg.Length == 1 ? "plane" : "planes")} φ = {planes} through " +
                     $"boresight, from −90° to +90° (a negative angle is the plane's φ + 180° half), at {G(grid.StepDeg)}° ({why}): {grid.AngleDeg.Length} samples a plane, " +
                     "the element pattern interpolated from the record and the array factor exact there.";
        double overview = RecordThetaStepDeg(far.Grid);
        if (overview > grid.EstimatedBeamwidthDeg / 10 * (1 + 1e-9))
            yield return $"Fields '{fa.Name}': the overview {PlanarFarField.Group}.U, Etheta and Ephi is on the record's {G(overview)}° grid, coarser than a tenth of the array's " +
                         $"estimated {G(grid.EstimatedBeamwidthDeg)}° beamwidth, so its lobes are drawn through too few samples. Plot {PlanarFarField.Group}.{CutUCube} for the beam.";
    }

    /// <summary>A note when <c>cuts=</c> is given to a block that is not an array, which has no cuts to form.</summary>
    private static string? CutsNotApplied(FieldsAnalysis fa)
        => fa.CutsExpr.Trim().Length > 0
            ? $"Fields '{fa.Name}': cuts= forms the pattern cuts of a {PhasedArrayModel.DisplayName}, and '{fa.Block}' is not one, so it is not used."
            : null;
}

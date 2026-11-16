// brief-paa-11 R-paa11-1 / R-paa11-6 — PATTERN CUTS through boresight, at a step fine enough to draw the beam (note §6.2).
//
// The overview is on the record's grid, which on the shipped example is 5°: two samples across a 16 × 16 array's 7° beam. The metrics never
// read it (ArrayBeam), but a display can only plot what is published, so a fields analysis on an array also publishes one cut per plane φc,
// over a signed angle t from −90° to +90°: t ≥ 0 is (θ = t, φc) and t < 0 is (θ = −t, φc + 180°). One cut is then a whole plane through
// boresight, and a family of them over a sweep overlays on one polar plot with no mirrored second trace.
//
// THE STEP is a tenth of the narrowest beam the lattice can make (ArrayBeam.EstimateBeamwidthDeg: the uniform beam at boresight, which a
// scan along the cut and a taper only widen), never coarser than the record's own θ step and never finer than FloorStepDeg. It is the record's
// step divided by a whole number, so every record grid point along the plane is also a cut sample, read there rather than interpolated.
//
// THE COST is the overview's, per sample: the element pattern of each class is evaluated once per direction (interpolated, ElementPattern), the
// position phases are ArrayPhaseTables' row and column tables (samples × (rows + cols) exponentials), and what is left is one complex
// multiply-add per element and sample. The counters below are per thread, as PropagationChannelModel.PatternEvaluations is, so a test reads its
// own evaluation and not another class's under a parallel run.

using System.Numerics;

using CircuitRF.Core.Devices.Arrays;

namespace CircuitRF.Engine.AntennaArray;

/// <summary>brief-paa-11 — the directions of a set of plane cuts: one row of signed angles per plane, row-major [cut, angle].</summary>
public sealed class ArrayCutGrid
{
    internal ArrayCutGrid(double[] cutPhiDeg, double[] angleDeg, double stepDeg, double estimateDeg, double recordStepDeg)
    {
        CutPhiDeg = cutPhiDeg;
        AngleDeg = angleDeg;
        StepDeg = stepDeg;
        EstimatedBeamwidthDeg = estimateDeg;
        RecordStepDeg = recordStepDeg;
        int nc = cutPhiDeg.Length, na = angleDeg.Length;
        ThetaDeg = new double[nc * na];
        PhiDeg = new double[nc * na];
        var thetaRad = new double[nc * na];
        var phiRad = new double[nc * na];
        for (int c = 0; c < nc; c++)
            for (int i = 0; i < na; i++)
            {
                int d = c * na + i;
                double t = angleDeg[i];
                ThetaDeg[d] = Math.Abs(t);
                double phi = t >= 0 ? cutPhiDeg[c] : cutPhiDeg[c] + 180;
                PhiDeg[d] = phi - 360 * Math.Floor(phi / 360);
                thetaRad[d] = ThetaDeg[d] * Math.PI / 180;
                phiRad[d] = PhiDeg[d] * Math.PI / 180;
            }
        Directions = ArrayDirections.Of(thetaRad, phiRad);
    }

    /// <summary>Each plane's azimuth φc, degrees, as asked for.</summary>
    public double[] CutPhiDeg { get; }

    /// <summary>The signed angle t from boresight along each plane, degrees, −90 … +90.</summary>
    public double[] AngleDeg { get; }

    /// <summary>The step between samples, degrees.</summary>
    public double StepDeg { get; }

    /// <summary>The beamwidth the step was formed from (<see cref="ArrayBeam.EstimateBeamwidthDeg"/>), degrees.</summary>
    public double EstimatedBeamwidthDeg { get; }

    /// <summary>The record's θ step the cut's step divides, degrees; zero when there was none.</summary>
    public double RecordStepDeg { get; }

    /// <summary>θ and φ of each sample, degrees, φ in [0, 360): what the element pattern is read at.</summary>
    public double[] ThetaDeg { get; }
    public double[] PhiDeg { get; }

    /// <summary>Every sample as one direction set, row-major [cut, angle].</summary>
    public ArrayDirections Directions { get; }

    /// <summary>Samples in all, every plane together.</summary>
    public int Count => CutPhiDeg.Length * AngleDeg.Length;

    // R-paa11-6 — what a grid kept across evaluations (a slider's drag, a sweep's points) need not form again: each class's pattern at its
    // samples (the patterns are the record's own referred ones, kept with it, EmbeddedPatterns) and the phase tables of each geometry
    internal readonly System.Runtime.CompilerServices.ConditionalWeakTable<ElementPattern, Complex[][]> Sampled = new();
    internal readonly List<ArrayPhaseTables> Tables = [];
}

/// <summary>brief-paa-11 — a pattern over a cut grid: Eθ and Eφ, row-major [cut, angle], in the elements' units times the weights'.</summary>
public sealed record ArrayCutField(ArrayCutGrid Grid, Complex[] ETheta, Complex[] EPhi);

public static partial class ArrayPattern
{
    /// <summary>R-paa11-6 — the finest step a cut takes, degrees: 3,601 samples from −90° to +90°, whatever the array.</summary>
    public const double CutFloorStepDeg = 0.05;

    /// <summary>The most samples one cut can have: 180° at <see cref="CutFloorStepDeg"/>, both ends included.</summary>
    public const int MaxCutSamples = 3601;

    /// <summary>The planes a fields analysis cuts when <c>cuts=</c> names none: the lattice's two principal planes.</summary>
    public static readonly double[] DefaultCutPlanesDeg = [0, 90];

    [ThreadStatic] private static long _cutsFormed, _cutPatternEvaluations, _cutTableEntries;

    /// <summary>Planes cut on this thread (one per plane per call).</summary>
    public static long CutsFormed => _cutsFormed;

    /// <summary>Element-pattern evaluations a cut made on this thread: at most one per pattern class per cut direction.</summary>
    public static long CutPatternEvaluations => _cutPatternEvaluations;

    /// <summary>Phase-table entries a cut built on this thread: samples × (rows + cols) per call.</summary>
    public static long CutTableEntries => _cutTableEntries;

    /// <summary>
    /// The step of a cut, degrees: a tenth of <paramref name="estimateDeg"/>, no finer than <see cref="CutFloorStepDeg"/>, and when the record
    /// has a step, that step divided by the smallest whole number that makes it no coarser than the tenth (so the record's grid points are
    /// samples). <paramref name="divisor"/> is that number; zero when there is no record step.
    /// </summary>
    public static double CutStepDeg(double estimateDeg, double recordStepDeg, out int divisor)
    {
        double want = Math.Max(estimateDeg / 10, CutFloorStepDeg);
        if (!(recordStepDeg > 0) || !double.IsFinite(recordStepDeg))
        {
            divisor = 0;
            return want;
        }
        int most = Math.Max(1, (int)Math.Floor(recordStepDeg / CutFloorStepDeg + 1e-9));
        divisor = Math.Clamp((int)Math.Ceiling(recordStepDeg / want - 1e-9), 1, most);
        return recordStepDeg / divisor;
    }

    /// <summary>R-paa11-1 — the cut grid of <paramref name="lattice"/> at <paramref name="hz"/> through the planes <paramref name="cutPhiDeg"/>,
    /// its step from the lattice's estimated beamwidth and the record's θ step <paramref name="recordStepDeg"/> (zero or less: none).</summary>
    public static ArrayCutGrid CutGrid(ArrayLattice lattice, double hz, IReadOnlyList<double> cutPhiDeg, double recordStepDeg)
    {
        if (cutPhiDeg.Count == 0) throw new ArgumentException("A cut grid needs at least one plane.", nameof(cutPhiDeg));
        double estimate = ArrayBeam.EstimateBeamwidthDeg(lattice, hz);
        double step = CutStepDeg(estimate, recordStepDeg, out int divisor);
        // The angles as whole multiples of the record's step over the divisor, so a sample on the record's grid is ITS value to the last bit.
        int half = divisor > 0 ? (int)Math.Floor(90.0 * divisor / recordStepDeg + 1e-9) : (int)Math.Floor(90.0 / step + 1e-9);
        var angles = new double[2 * half + 1];
        for (int i = -half; i <= half; i++) angles[i + half] = divisor > 0 ? (double)i * recordStepDeg / divisor : i * step;
        return new ArrayCutGrid([.. cutPhiDeg], angles, step, estimate, divisor > 0 ? recordStepDeg : 0);
    }

    /// <summary>
    /// R-paa11-1 — the pattern over <paramref name="grid"/>: each element pattern class evaluated once per direction (interpolated from its
    /// record, ElementPattern), times the exact array factor of the elements that take it: every class's factor in one pass over the elements per
    /// direction, one complex multiply-add each. The tables and the sampled patterns are kept with the grid, so a grid used again forms neither.
    /// </summary>
    public static ArrayCutField Cuts(ArrayLattice lattice, IReadOnlyList<Complex> weights, ArrayElements elements, ArrayCutGrid grid, double hz)
    {
        if (weights.Count != lattice.Count) throw new ArgumentException($"{lattice.Count} elements and {weights.Count} weights.");
        if (elements.PatternOf.Count != lattice.Count) throw new ArgumentException($"{lattice.Count} elements and {elements.PatternOf.Count} pattern assignments.");
        var t = CutTables(lattice, grid, hz);
        int n = grid.Count, rows = t.Rows, cols = t.Cols, classes = elements.Patterns.Count;
        // A class whose weights are rank one (a steered separable taper, a class of one element) takes the overview's separable sum, so a cut
        // sample on the record's grid is the overview's own arithmetic (Sum); the rest go row by row.
        var used = new bool[classes];
        for (int e = 0; e < lattice.Count; e++)
            if (lattice.IsOn(e) && weights[e] != Complex.Zero) used[elements.PatternOf[e]] = true;
        var separable = new (Complex[] P, Complex[] Q)?[classes];
        var masked = new Complex[lattice.Count];
        for (int p = 0; p < classes; p++)
        {
            if (!used[p]) continue;
            for (int e = 0; e < masked.Length; e++) masked[e] = elements.PatternOf[e] == p && lattice.IsOn(e) ? weights[e] : Complex.Zero;
            separable[p] = Separable(masked, rows, cols);
        }
        // the other classes' non-zero elements, row by row, each with its weight and its class
        var rowStart = new int[rows + 1];
        var col = new List<int>();
        var cls = new List<int>();
        var wt = new List<Complex>();
        for (int r = 0; r < rows; r++)
        {
            rowStart[r] = col.Count;
            for (int c = 0; c < cols; c++)
            {
                int e = r * cols + c;
                if (!lattice.IsOn(e) || weights[e] == Complex.Zero || separable[elements.PatternOf[e]] is not null) continue;
                col.Add(c);
                cls.Add(elements.PatternOf[e]);
                wt.Add(weights[e]);
            }
        }
        rowStart[rows] = col.Count;
        int[] colOf = [.. col], classOf = [.. cls];
        Complex[] w = [.. wt];
        var patterns = new Complex[classes][][];
        for (int p = 0; p < classes; p++)
            if (used[p]) patterns[p] = Sampled(elements.Patterns[p], grid);

        var eTheta = new Complex[n];
        var ePhi = new Complex[n];
        Parallel.For(0, n, () => (Row: new Complex[classes], All: new Complex[classes]), (d, _, acc) =>
        {
            Array.Clear(acc.All);
            int xAt = d * cols, yAt = d * rows;
            for (int r = 0; r < rows; r++)
            {
                int from = rowStart[r], to = rowStart[r + 1];
                if (from == to) continue;
                Array.Clear(acc.Row);
                for (int m = from; m < to; m++) acc.Row[classOf[m]] += w[m] * t.X[xAt + colOf[m]];   // one multiply-add per element
                Complex y = t.Y[yAt + r];
                for (int p = 0; p < classes; p++) if (acc.Row[p] != Complex.Zero) acc.All[p] += acc.Row[p] * y;
            }
            for (int p = 0; p < classes; p++)
            {
                if (separable[p] is not { } sep) continue;
                Complex a = 0, b = 0;
                for (int r = 0; r < rows; r++) a += sep.P[r] * t.Y[yAt + r];
                for (int c = 0; c < cols; c++) b += sep.Q[c] * t.X[xAt + c];
                acc.All[p] = a * b;
            }
            Complex et = 0, ep = 0;
            for (int p = 0; p < classes; p++)
            {
                if (acc.All[p] == Complex.Zero) continue;
                et += patterns[p][0][d] * acc.All[p];
                ep += patterns[p][1][d] * acc.All[p];
            }
            eTheta[d] = et;
            ePhi[d] = ep;
            return acc;
        }, _ => { });
        _cutsFormed += grid.CutPhiDeg.Length;
        return new ArrayCutField(grid, eTheta, ePhi);
    }

    /// <summary>
    /// brief-paa-11 — each element's own contribution over <paramref name="grid"/> for a unit weight: its class's pattern times its position
    /// phase, [element][sample], zero for an element switched off. What a pattern formed as a quadratic form over the elements (an envelope's
    /// time-averaged one) needs; each class's pattern is still evaluated once per direction.
    /// </summary>
    public static (Complex[][] ETheta, Complex[][] EPhi) CutBasis(ArrayLattice lattice, ArrayElements elements, ArrayCutGrid grid, double hz)
    {
        if (elements.PatternOf.Count != lattice.Count) throw new ArgumentException($"{lattice.Count} elements and {elements.PatternOf.Count} pattern assignments.");
        var t = CutTables(lattice, grid, hz);
        int n = grid.Count, cols = t.Cols, rows = t.Rows;
        var bt = new Complex[lattice.Count][];
        var bp = new Complex[lattice.Count][];
        var sampled = new Complex[][]?[elements.Patterns.Count];
        for (int e = 0; e < lattice.Count; e++)
        {
            bt[e] = new Complex[n];
            bp[e] = new Complex[n];
            if (!lattice.IsOn(e)) continue;
            int p = elements.PatternOf[e];
            var both = sampled[p] ??= Sampled(elements.Patterns[p], grid);
            var (et, ep) = (both[0], both[1]);
            int r = e / cols, c = e % cols;
            for (int d = 0; d < n; d++)
            {
                Complex phase = t.X[d * cols + c] * t.Y[d * rows + r];
                bt[e][d] = et[d] * phase;
                bp[e][d] = ep[d] * phase;
            }
        }
        _cutsFormed += grid.CutPhiDeg.Length;
        return (bt, bp);
    }

    /// <summary>The grid's tables for this geometry and frequency, built once and kept with the grid (a few at most).</summary>
    private static ArrayPhaseTables CutTables(ArrayLattice lattice, ArrayCutGrid grid, double hz)
    {
        lock (grid.Tables)
        {
            foreach (var kept in grid.Tables)
                if (kept.Key == lattice.GeometryKey && kept.Hz == hz) return kept;
            var t = new ArrayPhaseTables(lattice, grid.Directions, hz);
            _cutTableEntries += t.X.Length + t.Y.Length;
            if (grid.Tables.Count >= 4) grid.Tables.RemoveAt(0);
            grid.Tables.Add(t);
            return t;
        }
    }

    /// <summary>One class's pattern at every sample of the grid, read in degrees so a sample on the record's grid is the record's value.</summary>
    private static Complex[][] Sampled(ElementPattern pattern, ArrayCutGrid grid)
    {
        if (grid.Sampled.TryGetValue(pattern, out var kept)) return kept;
        int n = grid.Count;
        var et = new Complex[n];
        var ep = new Complex[n];
        Parallel.For(0, n, d => (et[d], ep[d]) = pattern.AtDegrees(grid.ThetaDeg[d], grid.PhiDeg[d]));
        _cutPatternEvaluations += n;
        Complex[][] both = [et, ep];
        grid.Sampled.AddOrUpdate(pattern, both);
        return both;
    }
}

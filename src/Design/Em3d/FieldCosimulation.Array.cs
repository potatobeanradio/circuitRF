// brief-paa-3 R-paa3-6 — a fields analysis whose block is a Phased Antenna Array (docs/design/phased-array.md §2, §4.4).
//
// THE WEIGHTS ARE THE CIRCUIT'S, as for an EM Field Data block: SParameterEngine.BlockWaves at the array's pins, weighed into incident and
// reflected waves in the pins' Z0. An element switched off is terminated inside and its pin left open, so its waves are zero here.
//
// EXACT IS EQUATION (1) AND IS THE BLOCK IT REPLACES: the waves are put back in the record's port order and handed to PointOf, the very sum
// an EM Field Data block on the same record takes. EMBEDDED and ISOLATED are equation (2) through PAA-2's engine: each element's terminated
// embedded pattern (its own port's, its class's or the centre's, referred to its position by EmbeddedPatterns) times the array factor of
// the elements that share it, on the record's grid, referred to 1 V at the circuit's first drive as PointOf refers its sum. Everything after
// the sum is Em3dRadiation.Publish, so every far-field cube, metric, trace and slider means what it means for a solved array.
//
// The array group adds what a solved block has no name for: a, b, the active reflection and the power each element accepts, over an
// element axis with each element's row, column and position; and the beam's metrics as measurement functions (ArrayFieldsMetrics).

using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Arrays;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Layout;
using CircuitRF.Engine;
using CircuitRF.Engine.AntennaArray;
using CircuitRF.Engine.Mom;
using RfCore.Data;

namespace CircuitRF.Design.Em3d;

public static partial class FieldCosimulation
{
    /// <summary>The group of the array's own cubes.</summary>
    public const string ArrayGroup = "array";

    /// <summary>The cubes of <see cref="ArrayGroup"/>.</summary>
    public const string ActiveReflectionCube = "GammaActive", ElementAcceptedCube = "Pacc";

    /// <summary>The block a fields analysis names, when it is a Phased Antenna Array; null otherwise.</summary>
    private static PhasedArrayModel? ArrayBlock(FieldsAnalysis fa, ElaboratedNetlist nl)
        => nl.Components.FirstOrDefault(c => string.Equals(c.InstancePath, fa.Block, StringComparison.Ordinal))?.Model as PhasedArrayModel;

    /// <summary>R-paa3-6 — <see cref="Run"/> on a Phased Antenna Array.</summary>
    private static DataSet RunArray(FieldsAnalysis fa, ElaboratedNetlist nl, PhasedArrayModel pa, double hz, List<(int Port, Complex Amplitude)> drives,
                                    List<string> notes, RunControl? control, CircuitRF.Design.Circuit.FieldsOutput output,
                                    Action<IFieldMetrics>? metrics, string? sweepPoint)
    {
        string who = $"Fields '{fa.Name}'";
        string name = Path.GetFileName(pa.RecordPath);
        var (far, refused) = PortFarFieldSet.Open(pa.RecordPath);
        if (far is null) throw new InvalidOperationException($"{who}: {refused}");
        if (far.PortCount != pa.Record.PortCount)
            throw new InvalidOperationException($"{who}: '{name}' lists {pa.Record.PortCount} ports and its far field holds {far.PortCount}. Run its EM setup again.");
        int[] at;
        if (fa.AtEveryFrequency) at = [.. Enumerable.Range(0, far.FrequenciesHz.Count)];
        else
        {
            int i = far.IndexOf(hz);
            if (i < 0)
                throw new InvalidOperationException($"{who}: '{name}' holds a pattern at {HzList(far.FrequenciesHz)} only, not at {MaterialDispersion.Hz(hz)}: a pattern exists " +
                                                    "only at frequencies the run solved, because its phase is not safe to interpolate between them. Name one of these, or freq=all.");
            at = [i];
        }

        int n = pa.PortCount;
        Complex[] z0 = [.. Enumerable.Repeat(pa.Z0, n)];
        var freqs = new double[at.Length];
        var waves = new CosimWaves[at.Length];
        var points = new Em3dPatternPoint[at.Length];
        var elements = new ArrayElements[at.Length];
        for (int f = 0; f < at.Length; f++)
        {
            control?.ThrowIfCancellationRequested();
            freqs[f] = far.FrequenciesHz[at[f]];
            waves[f] = SwitchedOff(pa, Weigh(fa, z0, SParameterEngine.BlockWaves(nl, freqs[f], fa.Block, drives)));
            elements[f] = Elements(pa, far, freqs[f]);
            points[f] = ArrayPointOf(fa, pa, far, at[f], waves[f], elements[f]);
        }
        string where = at.Length == 1 ? MaterialDispersion.Hz(freqs[0]) : $"{at.Length} frequencies, {MaterialDispersion.Hz(freqs[0])} to {MaterialDispersion.Hz(freqs[^1])}";
        double firstHz = freqs[0];
        WarnIfNothingReachesTheBlock(fa, nl, waves, where, d => Weigh(fa, z0, SParameterEngine.BlockWaves(nl, firstHz, fa.Block, d)));

        var data = new DataSet();
        var lines = new List<string>();
        var published = new DataSet();
        var sentences = Em3dRadiation.Publish(published, far.Solver, far.Grid, [drives[0].Port], freqs, points, far.ReferenceInputPowerDbm, far.Hemisphere,
                                              origin: $"The pattern is the array's, formed from '{name}' at level {pa.Level.ToString().ToLowerInvariant()}.",
                                              planarSum: far.Planar);
        foreach (string group in published.Groups)
            foreach (var (cube, value) in published.CubesIn(group))
                data.Add($"{group}.{cube}", value);
        AddWaves(data, freqs, waves, [.. Enumerable.Range(1, n)]);
        AddArrayCubes(data, pa, freqs, waves);
        AddCircuitS(data, nl, freqs);

        var first = waves[0];
        lines.Add($"Fields '{fa.Name}' at {where}: the pattern of the {n}-element array '{fa.Block}' with this circuit around it, " +
                  $"{(first.Multi ? $"driven from circuit ports {string.Join(", ", first.Drive.Drives.Select(d => d.Port))} together" : $"driven from circuit port {drives[0].Port}")} " +
                  $"({Pct(first.Accepts)} of the available power accepted) and referred to 1 V at port {drives[0].Port}. Gain is per watt accepted and realized gain includes mismatch.");
        lines.Add(pa.LevelNote);
        if (pa.CouplingNote is { } coupling) lines.Add(coupling);
        lines.AddRange(sentences.Skip(1));
        Interlocked.Increment(ref _runs);

        if (sweepPoint is not null)
            foreach (string line in lines) nl.AddNoteOnce($"fields:{fa.Name}:{line}", line);
        else
        {
            for (int i = 0; i < lines.Count; i++) nl.AddNoteOnce($"fields:{fa.Name}:{i}", lines[i]);
            notes.Add($"Fields '{fa.Name}' at {where}: {Pct(first.Accepts)} of the available power accepted by the array");
        }
        if (metrics is not null)
        {
            var pattern = fa.AtEveryFrequency ? null : new CosimPatternMetrics(fa, far, at[0], waves[0], points[0]);
            metrics(new ArrayFieldsMetrics(fa, pa, far, freqs[0], waves[0], elements[0], pattern));
        }
        _ = output;
        return data;
    }

    /// <summary>The waves with every element switched off at zero: terminated inside, its pin open, it neither radiates nor accepts.</summary>
    private static CosimWaves SwitchedOff(PhasedArrayModel pa, CosimWaves w)
    {
        if (pa.Lattice.Off.Count == 0) return w;
        var a = (Complex[])w.A.Clone();
        var b = (Complex[])w.B.Clone();
        foreach (int k in pa.Lattice.Off) { a[k - 1] = 0; b[k - 1] = 0; }
        return w with { A = a, B = b };
    }

    /// <summary>Each element's referred embedded pattern, one per distinct record port, at <paramref name="hz"/>.</summary>
    internal static ArrayElements Elements(PhasedArrayModel pa, PortFarFieldSet far, double hz)
    {
        var fit = pa.RecordFit;
        var ports = Enumerable.Range(0, pa.PortCount).Select(pa.PatternPort).ToArray();
        var distinct = ports.Distinct().ToArray();
        var patterns = distinct.Select(p => EmbeddedPatterns.Referred(far, p, hz, fit.Positions[p])).ToArray();
        return ArrayElements.Classes(patterns, [.. ports.Select(p => Array.IndexOf(distinct, p))]);
    }

    /// <summary>One frequency's pattern: equation (1) through PointOf at exact, equation (2) through the engine otherwise, each referred to
    /// 1 V at the circuit's first drive.</summary>
    private static Em3dPatternPoint ArrayPointOf(FieldsAnalysis fa, PhasedArrayModel pa, PortFarFieldSet far, int i, CosimWaves w, ArrayElements elements)
    {
        if (pa.Level == PhasedArrayLevel.Exact)
        {
            // The record's port order: element e is port PatternPort(e).
            var a = new Complex[far.PortCount];
            var b = new Complex[far.PortCount];
            for (int e = 0; e < pa.PortCount; e++) { a[pa.PatternPort(e)] = w.A[e]; b[pa.PatternPort(e)] = w.B[e]; }
            return PointOf(fa, far, i, w with { A = a, B = b }, out _);
        }
        double hz = far.FrequenciesHz[i];
        var field = ArrayPattern.Sum(pa.Lattice, w.A, elements, ArrayDirections.OnGrid(far.Grid), hz);
        Complex v = DriveVoltage(w);
        if (!(v.Magnitude > 0))
            throw new InvalidOperationException($"Fields '{fa.Name}': circuit port {w.Drive.DrivenPort}'s voltage is zero at {MaterialDispersion.Hz(hz)}, so the pattern " +
                                                "has nothing to be referred to. Drive the circuit from a port that is not shorted.");
        var eTheta = new Complex[field.ETheta.Length];
        var ePhi = new Complex[field.EPhi.Length];
        for (int d = 0; d < eTheta.Length; d++) { eTheta[d] = field.ETheta[d] / v; ePhi[d] = field.EPhi[d] / v; }
        double perVolt = 0.5 / (v.Magnitude * v.Magnitude);
        return new Em3dPatternPoint(FarFieldStage.Pattern(far.Grid, eTheta, ePhi, w.Drive.DrivenPort, hz), w.AcceptedW * perVolt, w.Reflection);
    }

    /// <summary>R-paa3-6 — the array group: the element axis with its rows, columns and positions; a, b, Γ_act and each element's accepted
    /// power per frequency, per watt as the waves' cubes are. Γ_act of an element switched off is NaN: it has none.</summary>
    private static void AddArrayCubes(DataSet data, PhasedArrayModel pa, double[] freqs, CosimWaves[] waves)
    {
        var lattice = pa.Lattice;
        int n = lattice.Count, nf = freqs.Length;
        var element = new Axis("element", [.. Enumerable.Range(1, n).Select(k => (double)k)], "");
        var f = new Axis("f", (double[])freqs.Clone(), "Hz");
        data.Add($"{ArrayGroup}.row", new DataCube([element], [.. Enumerable.Range(0, n).Select(e => (double)(e / lattice.Cols + 1))]));
        data.Add($"{ArrayGroup}.col", new DataCube([element], [.. Enumerable.Range(0, n).Select(e => (double)(e % lattice.Cols + 1))]));
        data.Add($"{ArrayGroup}.x", new DataCube([element], [.. Enumerable.Range(0, n).Select(e => lattice.Position(e).X)]) { Unit = "m" });
        data.Add($"{ArrayGroup}.y", new DataCube([element], [.. Enumerable.Range(0, n).Select(e => lattice.Position(e).Y)]) { Unit = "m" });
        var a = new Complex[nf * n];
        var b = new Complex[nf * n];
        var gamma = new Complex[nf * n];
        var pacc = new double[nf * n];
        for (int i = 0; i < nf; i++)
        {
            double scale = Math.Sqrt(waves[i].DivisorW);
            for (int e = 0; e < n; e++)
            {
                Complex ai = waves[i].A[e] / scale, bi = waves[i].B[e] / scale;
                a[i * n + e] = ai;
                b[i * n + e] = bi;
                gamma[i * n + e] = lattice.IsOn(e) && ai != Complex.Zero ? bi / ai : new Complex(double.NaN, double.NaN);
                pacc[i * n + e] = 0.5 * (ai.Magnitude * ai.Magnitude - bi.Magnitude * bi.Magnitude);
            }
        }
        data.Add($"{ArrayGroup}.{IncidentCube}", new DataCube([f, element], a) { Unit = "sqrt(W)" });
        data.Add($"{ArrayGroup}.{ReflectedCube}", new DataCube([f, element], b) { Unit = "sqrt(W)" });
        data.Add($"{ArrayGroup}.{ActiveReflectionCube}", new DataCube([f, element], gamma));
        data.Add($"{ArrayGroup}.{ElementAcceptedCube}", new DataCube([f, element], pacc) { Unit = "W" });
    }

    /// <summary>R-paa3-6 — what a measurement reads of one array run: the far-field metrics as for any block, and the beam's own, each from
    /// PAA-2's local evaluation (never from the record's grid), computed once when first asked.</summary>
    private sealed class ArrayFieldsMetrics(FieldsAnalysis fa, PhasedArrayModel pa, PortFarFieldSet far, double hz, CosimWaves w,
                                            ArrayElements elements, CosimPatternMetrics? pattern) : IFieldMetrics
    {
        private readonly object _gate = new();
        private ArrayBeamMetrics? _beam;
        private double? _balance;

        public double Evaluate(FieldMetric metric, string? region, double? massGrams)
            => throw new ExpressionException($"'{FieldMetricFunctions.NameOf(metric)}' reads a field set, and fields analysis '{fa.Name}' is the pattern of a {PhasedArrayModel.DisplayName}.");

        public double EvaluatePattern(FieldMetric metric, double? thetaDeg, double? phiDeg)
            => pattern?.Evaluate(metric, thetaDeg, phiDeg)
               ?? throw new ExpressionException($"{metric}({fa.Name}) is one number, and fields analysis '{fa.Name}' has freq=all. Read it from a fields analysis at one frequency.");

        public double EvaluateArray(FieldMetric metric, string? plane, double? theta0Deg, double? phi0Deg)
        {
            lock (_gate)
            {
                switch (metric)
                {
                    case FieldMetric.ActiveVswrMax:
                        double worst = 0;
                        for (int e = 0; e < pa.PortCount; e++)
                            if (pa.Lattice.IsOn(e) && w.A[e] != Complex.Zero) worst = Math.Max(worst, (w.B[e] / w.A[e]).Magnitude);
                        return worst >= 1 ? double.PositiveInfinity : (1 + worst) / (1 - worst);
                    case FieldMetric.PowerBalance:
                        return _balance ??= Balance();
                    case FieldMetric.GratingMarginDeg:
                    {
                        var beam = Beam();
                        var lobe = ArrayBeam.GratingLobe(pa.Lattice, hz, beam.PeakThetaDeg, beam.PeakPhiDeg);
                        if (double.IsPositiveInfinity(lobe.Margin)) return double.PositiveInfinity;
                        double s0 = Math.Sin(beam.PeakThetaDeg * Math.PI / 180);
                        return Math.Asin(Math.Clamp(s0 + lobe.Margin, -1, 1)) * 180 / Math.PI - beam.PeakThetaDeg;
                    }
                    case FieldMetric.HpbwDeg:
                    {
                        var beam = Beam();
                        return (plane ?? "").ToLowerInvariant() switch
                        {
                            "elevation" or "e" => beam.ElevationBeamwidthDeg,
                            "azimuth" or "a" => beam.AzimuthBeamwidthDeg,
                            _ => throw new ExpressionException($"HpbwDeg({fa.Name}, plane): the plane is \"elevation\" (the plane of the peak and boresight) or \"azimuth\" (across it)."),
                        };
                    }
                    case FieldMetric.SllDb:
                        return Beam().FirstSidelobeDb ?? throw new ExpressionException($"SllDb({fa.Name}): neither principal cut through the peak has a sidelobe.");
                    case FieldMetric.PointingErrorDeg:
                    {
                        var beam = Beam();
                        var peak = ArrayDirections.Vector(beam.PeakThetaDeg * Math.PI / 180, beam.PeakPhiDeg * Math.PI / 180);
                        var want = ArrayDirections.Vector(theta0Deg!.Value * Math.PI / 180, phi0Deg!.Value * Math.PI / 180);
                        return ArrayBeam.Separation(peak, want) * 180 / Math.PI;
                    }
                    case FieldMetric.ScanLossDb:
                    {
                        var beam = Beam();
                        Complex[] boresight = [.. w.A.Select(a => new Complex(a.Magnitude, 0))];
                        var broad = ArrayBeam.Evaluate(pa.Lattice, boresight, elements, hz, 0, 0);
                        double inc = w.A.Sum(a => a.Magnitude * a.Magnitude);
                        return ArrayBeam.ScanLossDb(beam.PeakPower, inc, broad.PeakPower, inc);
                    }
                    default:
                        throw new ExpressionException($"'{metric}' is not a measurement of an array's beam.");
                }
            }
        }

        /// <summary>The beam from its local evaluation, started at the overview's maximum.</summary>
        private ArrayBeamMetrics Beam()
        {
            if (_beam is not null) return _beam;
            var dirs = ArrayDirections.OnGrid(far.Grid);
            var overview = ArrayPattern.Sum(pa.Lattice, w.A, elements, dirs, hz);
            int best = 0;
            for (int d = 1; d < dirs.Count; d++) if (overview.Power(d) > overview.Power(best)) best = d;
            return _beam = ArrayBeam.Evaluate(pa.Lattice, w.A, elements, hz, dirs.ThetaRad[best] * 180 / Math.PI, dirs.PhiRad[best] * 180 / Math.PI);
        }

        /// <summary>Radiated over accepted power (note §2.4).</summary>
        private double Balance()
        {
            var field = ArrayPattern.Sum(pa.Lattice, w.A, elements, ArrayDirections.OnGrid(far.Grid), hz);
            double accepted = 0;
            for (int e = 0; e < pa.PortCount; e++) accepted += 0.5 * (w.A[e].Magnitude * w.A[e].Magnitude - w.B[e].Magnitude * w.B[e].Magnitude);
            return ArrayWaves.PowerBalance(ArrayWaves.RadiatedPowerW(field), accepted);
        }
    }
}

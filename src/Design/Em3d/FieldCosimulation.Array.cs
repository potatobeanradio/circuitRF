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

    /// <summary>brief-paa-4 — a Feed-mode array's weights per element, requested and quantised.</summary>
    public const string WeightCube = "w", QuantisedWeightCube = "wQuantised";

    /// <summary>The block a fields analysis names, when it is a Phased Antenna Array; null otherwise.</summary>
    private static PhasedArrayModel? ArrayBlock(FieldsAnalysis fa, ElaboratedNetlist nl)
        => nl.Components.FirstOrDefault(c => string.Equals(c.InstancePath, fa.Block, StringComparison.Ordinal))?.Model as PhasedArrayModel;

    /// <summary>R-paa3-6 — <see cref="Run"/> on a Phased Antenna Array.</summary>
    private static DataSet RunArray(FieldsAnalysis fa, ElaboratedNetlist nl, PhasedArrayModel pa, double hz, List<(int Port, Complex Amplitude)> drives,
                                    List<string> notes, RunControl? control, CircuitRF.Design.Circuit.FieldsOutput output,
                                    Action<IFieldMetrics>? metrics, string? sweepPoint, HbWaveSource? hb = null, CutMemo? cutMemo = null)
    {
        string who = $"Fields '{fa.Name}'";
        string name = Path.GetFileName(pa.RecordPath);
        var (far, refused) = PortFarFieldSet.Open(pa.RecordPath);
        if (far is null) throw new InvalidOperationException($"{who}: {refused}");
        if (far.PortCount != pa.Record.PortCount)
            throw new InvalidOperationException($"{who}: '{name}' lists {pa.Record.PortCount} ports and its far field holds {far.PortCount}. Run its EM setup again.");
        int[] at;
        if (fa.AtEveryFrequency && hb is null) at = [.. Enumerable.Range(0, far.FrequenciesHz.Count)];
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
        for (int f = 0; f < at.Length; f++) freqs[f] = far.FrequenciesHz[at[f]];

        // brief-paa-8 R-paa8-2 — a Feed-mode array whose channel is identical closes without solving the whole circuit; R-paa8-4 — where it
        // cannot, a slider's drag (FieldsOutput.Local) shows the last full solve with the new weights, and the release solves.
        // brief PCH-13 R-pch13-2 — a receiving array's pattern is its receive pattern: each element's transfer to the driven port, the
        // direction its LNAs conduct (Received). The fast path closes the transmit direction, so a receiving array solves.
        var blockEc = nl.Components.First(c => c.InstancePath == fa.Block);
        int[] sourceNodes = [.. Enumerable.Range(0, n).Select(k => PhasedArrayModel.SourceNode(blockEc, k, n))];
        bool receiving = sourceNodes.All(s => s > 0)
                         && (pa.IncidentWaves.Count > 0
                             || nl.Components.Any(c => c.Model is PropagationChannelModel ch && ch.RxArray?.Ec.InstancePath == fa.Block));
        PhasedArrayFastPath.Solved? quick = null;
        if (hb is null && pa.Feed is not null && !receiving)
        {
            control?.ThrowIfCancellationRequested();
            quick = PhasedArrayFastPath.TryWaves(nl, fa.Block, freqs, drives);
            if (quick is null && output == CircuitRF.Design.Circuit.FieldsOutput.Local) quick = PhasedArrayFastPath.Estimate(nl, fa.Block, freqs, drives);
        }
        var solved = new SParameterEngine.BlockPortDrive[at.Length];
        for (int f = 0; f < at.Length; f++)
        {
            control?.ThrowIfCancellationRequested();
            if (hb is null) solved[f] = quick?.Waves[f] ?? SParameterEngine.BlockWaves(nl, freqs[f], fa.Block, drives);
            // brief-paa-6 — a harmonic-balance solution's waves at its one frequency, in place of the linear solve's
            var w = hb?.Waves ?? Weigh(fa, z0, solved[f]);
            if (receiving && hb is null) w = Received(pa, nl, freqs[f], drives, sourceNodes, w);
            waves[f] = SwitchedOff(pa, w);
            elements[f] = Elements(pa, far, freqs[f]);
            points[f] = ArrayPointOf(fa, pa, far, at[f], waves[f], elements[f]);
        }
        string where = at.Length == 1 ? MaterialDispersion.Hz(freqs[0]) : $"{at.Length} frequencies, {MaterialDispersion.Hz(freqs[0])} to {MaterialDispersion.Hz(freqs[^1])}";
        double firstHz = freqs[0];
        if (hb is null)
            WarnIfNothingReachesTheBlock(fa, nl, waves, where, d => receiving
                ? Received(pa, nl, firstHz, d, sourceNodes, Weigh(fa, z0, SParameterEngine.BlockWaves(nl, firstHz, fa.Block, d)))
                : Weigh(fa, z0, SParameterEngine.BlockWaves(nl, firstHz, fa.Block, d)));

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
        // brief-paa-11 R-paa11-1 / R-paa11-6 — the fine cuts beside the overview; an OnDemand evaluation (an optimizer's point) forms none,
        // since nothing it hands back is a plot
        var cutLines = new List<string>();
        if (output != CircuitRF.Design.Circuit.FieldsOutput.OnDemand)
            AddArrayCuts(data, fa, pa, far, freqs, waves, elements, drives[0].Port, cutMemo, cutLines);
        if (hb is not null)
        {
            // brief-paa-6 R-paa6-3 — the watts HB delivers into each element, beside the per-watt cubes
            var pdel = new double[freqs.Length * n];
            for (int i = 0; i < freqs.Length; i++)
                for (int e = 0; e < n; e++)
                    pdel[i * n + e] = waves[i].A[e].Magnitude * waves[i].A[e].Magnitude - waves[i].B[e].Magnitude * waves[i].B[e].Magnitude;
            data.Add($"{ArrayGroup}.{ElementDeliveredCube}",
                     new DataCube([new Axis("f", (double[])freqs.Clone(), "Hz"), new Axis("element", [.. Enumerable.Range(1, n).Select(k => (double)k)], "")], pdel) { Unit = "W" });
            lines.Add(HbHeadline(fa, hb, n));
            lines.Add(pa.LevelNote);
            if (pa.CouplingNote is { } hbCoupling) lines.Add(hbCoupling);
            lines.AddRange(sentences.Skip(1));
            lines.AddRange(cutLines);
            Interlocked.Increment(ref _runs);
            Report(fa, nl, notes, lines, [], sweepPoint, hb);
            if (metrics is not null)
            {
                var hbPattern = new CosimPatternMetrics(fa, far, at[0], waves[0], points[0]);
                var hbState = new ArrayFieldState(fa.Name, fa.Block, pa, far, freqs[0], waves[0].A, waves[0].B, waves[0].AvailableW, waves[0].AcceptedW,
                                                  elements[0], null, false, true);
                metrics(new ArrayFieldsMetrics(fa, pa, far, freqs[0], waves[0], elements[0], hbPattern, nl, [], hbState));
            }
            return data;
        }
        if (quick is not null)
            foreach (var (cube, value) in quick.CircuitS.Cubes) data.Add(cube, value);
        else
        {
            var circuit = SParameterEngine.Run(nl, freqs);
            var s = new DataSet();
            foreach (string cube in (string[])["S", "Z0"])
                if (circuit.Cubes.TryGetValue(cube, out var value)) { data.Add(cube, value); s.Add(cube, value); }
            if (pa.Feed is not null) PhasedArrayFastPath.Remember(nl, fa.Block, freqs, drives, solved, s);
        }

        var first = waves[0];
        lines.Add($"Fields '{fa.Name}' at {where}: the pattern of the {n}-element array '{fa.Block}' with this circuit around it, " +
                  $"{(first.Multi ? $"driven from {string.Join(", ", first.Drive.Drives.Select(d => DrivenName(nl, d.Port)))} together" : $"driven from {DrivenName(nl, drives[0].Port)}")} " +
                  $"({Pct(first.Accepts)} of the available power accepted) and referred to 1 V there. Gain is per watt accepted and realized gain includes mismatch." +
                  (receiving ? $" It is the receive pattern: each element's transfer to {DrivenName(nl, drives[0].Port)}, the direction its channel conducts." : ""));
        lines.Add(pa.LevelNote);
        if (pa.CouplingNote is { } coupling) lines.Add(coupling);
        var solvePath = pa.Feed is not null ? quick?.Path ?? PhasedArrayFastPath.Decide(nl, fa.Block) : null;
        if (solvePath is { } path) lines.Add(path.Sentence);
        lines.AddRange(sentences.Skip(1));
        lines.AddRange(cutLines);
        Interlocked.Increment(ref _runs);
        if (quick is { Path.Fast: false }) notes.Add($"Fields '{fa.Name}': {PhasedArrayFastPath.EstimatedSentence}");

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
            var state = new ArrayFieldState(fa.Name, fa.Block, pa, far, freqs[0], waves[0].A, waves[0].B, waves[0].AvailableW, waves[0].AcceptedW,
                                            elements[0], solvePath, quick is { Path.Fast: false }, false);
            metrics(new ArrayFieldsMetrics(fa, pa, far, freqs[0], waves[0], elements[0], pattern, nl, drives, state));
        }
        return data;
    }

    /// <summary>The waves with every element switched off at zero: terminated inside, its pin open, it neither radiates nor accepts.</summary>
    /// <summary>
    /// brief PCH-13 R-pch13-2 — a receiving array's waves: element k's a is the wave its emission reaches each driven port with, per unit
    /// emitted (<see cref="SParameterEngine.PortReceiveWeights"/> on the element's source node, whose row reads b − S·a = V_src), weighted by
    /// the drive and scaled as <see cref="Weigh"/> scales the transmit waves; b is the array's own scattering of them, S·a. A reciprocal
    /// network gives the transmit waves back; behind one-way LNAs the transmit waves are zero and these are the receive pattern's. The
    /// port's reflection and accepted power stay the forward drive's.
    /// </summary>
    private static CosimWaves Received(PhasedArrayModel pa, ElaboratedNetlist nl, double hz, IReadOnlyList<(int Port, Complex Amplitude)> drives,
                                       int[] sourceNodes, CosimWaves forward)
    {
        int n = sourceNodes.Length;
        double incident = forward.Drive.Drives.Sum(d => d.Incident.Magnitude * d.Incident.Magnitude);
        double root = Math.Sqrt(PalaceDrive.IncidentPowerW / incident);
        var a = new Complex[n];
        foreach (var (port, amplitude) in drives)
        {
            var t = SParameterEngine.PortReceiveWeights(nl, hz, port, sourceNodes);
            for (int k = 0; k < n; k++) a[k] += amplitude * t[k] * root;
        }
        var s = pa.SAt(hz);
        var b = new Complex[n];
        for (int k = 0; k < n; k++)
        {
            Complex sum = 0;
            for (int q = 0; q < n; q++) sum += s[k, q] * a[q];
            b[k] = sum;
        }
        return forward with { A = a, B = b };
    }

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

        // brief-paa-4 R-paa4-4 — a Feed-mode array's weights, requested and as quantised, beside each other so a plot shows both.
        if (pa.Feed is { } feed)
        {
            var w = new Complex[nf * n];
            var wq = new Complex[nf * n];
            for (int i = 0; i < nf; i++)
                for (int e = 0; e < n; e++)
                {
                    w[i * n + e] = feed.Requested(e, freqs[i]);
                    wq[i * n + e] = feed.Quantised(e, freqs[i]);
                }
            data.Add($"{ArrayGroup}.{WeightCube}", new DataCube([f, element], w));
            data.Add($"{ArrayGroup}.{QuantisedWeightCube}", new DataCube([f, element], wq));
        }
    }

    /// <summary>R-paa3-6 — what a measurement reads of one array run: the far-field metrics as for any block, and the beam's own, each from
    /// PAA-2's local evaluation (never from the record's grid), computed once when first asked.</summary>
    private sealed class ArrayFieldsMetrics(FieldsAnalysis fa, PhasedArrayModel pa, PortFarFieldSet far, double hz, CosimWaves w,
                                            ArrayElements elements, CosimPatternMetrics? pattern, ElaboratedNetlist nl,
                                            List<(int Port, Complex Amplitude)> drives, ArrayFieldState state) : IFieldMetrics, IArrayFieldStateSource
    {
        public ArrayFieldState State { get; } = state;

        private readonly object _gate = new();
        private ArrayBeamMetrics? _beam;
        private double? _balance, _nf;

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
                        return ActiveVswrMaxOf(w, pa.Lattice.IsOn);
                    case FieldMetric.PowerBalance:
                        return _balance ??= Balance();
                    case FieldMetric.BeamNfDb:
                        if (drives.Count == 0)
                            throw new ExpressionException($"BeamNfDb({fa.Name}) is the noise figure from a driven circuit port, and fields analysis '{fa.Name}' forms its pattern from " +
                                                          "harmonic balance, which has no noise. Read it from a fields analysis without waves=.");
                        return _nf ??= BeamNoiseFigureDb();
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

        /// <summary>
        /// brief-paa-7 R-paa7-4 — the receive measurements (docs/design/phased-array.md §5.3). The array receives through its RECEIVE weights
        /// T_n, the wave leaving the analysis's driven port per unit wave emitted by element n (SParameterEngine.PortReceiveWeights, one transposed
        /// solve), so a received wave reaches the port as b = Σ T_n·c_n whether or not the circuit between is reciprocal — a channel of amplifiers
        /// is not. With E_rx = Σ T_n·E_n the receive pattern (an array sum with weights T), C the elements' noise correlation and D = I − S·Sᴴ:
        ///   AntennaTempK  T_A = T·C·Tᴴ / (k·A),  A = T·D·Tᴴ — the noise the elements deliver to the port over what a passive array at 1 K would;
        ///   SystemTempK   T_sys = N/(k·A), N the noise the noise analysis delivers into the port's termination: every noise referred to the elements;
        ///   GoverTDbK     10·log10(G/T_sys) = 10·log10(k·G_rx/N), G_rx = 4π·|E_rx|²/η₀ at the receive beam's peak and G = G_rx/A its gain at the
        ///                 elements' terminals;
        ///   SnrDb         10·log10(S·λ²·|p̂·E_rx(θ_k,φ_k)|²/η₀ / (N·B)) for incident wave k — the wave b it delivers carries ½|b|² of that;
        ///   NullDepthDb   10·log10(|E_rx(θ,φ)|²/|E_rx,peak|²).
        /// </summary>
        public double EvaluateReceive(FieldMetric metric, Func<int, double, double>? noise, int? wave, double? bandwidthHz, double? thetaDeg, double? phiDeg)
        {
            lock (_gate)
            {
                string name = FieldMetricFunctions.NameOf(metric);
                if (drives.Count == 0)
                    throw new ExpressionException($"{name}({fa.Name}, …) receives at the fields analysis's driven port, and fields analysis '{fa.Name}' forms its pattern " +
                                                  "from harmonic balance, which drives no port. Read it from a fields analysis without waves=.");
                var rx = Receive();
                switch (metric)
                {
                    case FieldMetric.NullDepthDb:
                    {
                        double th = thetaDeg!.Value * Math.PI / 180, ph = phiDeg!.Value * Math.PI / 180;
                        double p = ArrayPattern.Sum(pa.Lattice, rx.T, elements, ArrayDirections.Of([th], [ph]), hz).Power(0);
                        return 10 * Math.Log10(p / rx.Beam.PeakPower);
                    }
                    case FieldMetric.AntennaTempK:
                        return rx.AntennaW / (RfCore.Data.NoiseMetrics.Boltzmann * rx.A);
                }
                if (noise is null) throw new ExpressionException($"{name}({fa.Name}, NF): NF names a noise analysis.");
                double delivered = noise(drives[0].Port, hz);
                double k = RfCore.Data.NoiseMetrics.Boltzmann;
                switch (metric)
                {
                    case FieldMetric.SystemTempK: return delivered / (k * rx.A);
                    case FieldMetric.GoverTDbK: return 10 * Math.Log10(k * (4 * Math.PI * rx.Beam.PeakPower / ArrayWaves.Eta0) / delivered);
                    case FieldMetric.SnrDb:
                    {
                        if (!pa.IncidentWaves.TryGetValue(wave!.Value, out var inc))
                            throw new ExpressionException($"SnrDb({fa.Name}, …, {wave}, …): '{fa.Block}' has no incident wave {wave}" +
                                                          (pa.IncidentWaves.Count == 0 ? "; it is given none (RxPd[k], RxFreq[k] …)." : $"; it has {string.Join(", ", pa.IncidentWaves.Keys)}."));
                        if (Math.Abs(inc.Hz - hz) > 1e-9 * hz)
                            throw new ExpressionException($"SnrDb({fa.Name}, …, {wave}, …): incident wave {wave} is at {MaterialDispersion.Hz(inc.Hz)} and fields analysis '{fa.Name}' at " +
                                                          $"{MaterialDispersion.Hz(hz)}: the pattern that receives it is the one at its own frequency.");
                        var f = ArrayPattern.Sum(pa.Lattice, rx.T, elements, ArrayDirections.Of([inc.ThetaRad], [inc.PhiRad]), hz);
                        Complex along = inc.PolTheta * f.ETheta[0] + inc.PolPhi * f.EPhi[0];
                        double lambda = EmConstants.C0 / hz;
                        double signal = inc.DensityW * lambda * lambda * (along.Magnitude * along.Magnitude) / ArrayWaves.Eta0;
                        return 10 * Math.Log10(signal / (delivered * bandwidthHz!.Value));
                    }
                    default:
                        throw new ExpressionException($"'{metric}' is not a receive measurement.");
                }
            }
        }

        private sealed record Reception(Complex[] T, Func<ArrayBeamMetrics> BeamOf, double AntennaW, double A)
        {
            private ArrayBeamMetrics? _beam;
            public ArrayBeamMetrics Beam => _beam ??= BeamOf();
        }
        private Reception? _rx;

        /// <summary>The receive weights, the receive beam, the noise the elements deliver to the port and the referral A = T·D·Tᴴ.</summary>
        private Reception Receive()
        {
            if (_rx is not null) return _rx;
            var block = nl.Components.First(c => string.Equals(c.InstancePath, fa.Block, StringComparison.Ordinal));
            int n = pa.PortCount;
            int[] sources = [.. Enumerable.Range(0, n).Select(e => PhasedArrayModel.SourceNode(block, e, n))];
            var t = SParameterEngine.PortReceiveWeights(nl, hz, drives[0].Port, sources);
            double physical = CircuitRF.Core.Devices.Temperature.ResolveDeviceC(block.Parameters, nl.AmbientC) + CircuitRF.Core.Devices.Temperature.KelvinOffset;
            var noise = pa.NoiseCorrelation(hz, physical);
            double antenna = block.NoiseOff ? 0 : noise.Correlation.Form(t, t).Real;
            var s = pa.SparseAt(hz);
            double a = 0;
            var ts = new Complex[n];
            for (int i = 0; i < n; i++)
            {
                a += t[i].Real * t[i].Real + t[i].Imaginary * t[i].Imaginary;
                foreach (var (c, v) in s.Row(i)) ts[c] += t[i] * v;
            }
            for (int c = 0; c < n; c++) a -= ts[c].Real * ts[c].Real + ts[c].Imaginary * ts[c].Imaginary;
            if (!(a > 0))
                throw new ExpressionException($"Fields '{fa.Name}': nothing the elements of '{fa.Block}' receive reaches {DrivenName(nl, drives[0].Port)}, so the array has no " +
                                              "antenna temperature, system temperature or G/T there.");
            ArrayBeamMetrics Beam()
            {
                var dirs = ArrayDirections.OnGrid(far.Grid);
                var overview = ArrayPattern.Sum(pa.Lattice, t, elements, dirs, hz);
                int best = 0;
                for (int d = 1; d < dirs.Count; d++) if (overview.Power(d) > overview.Power(best)) best = d;
                return ArrayBeam.Evaluate(pa.Lattice, t, elements, hz, dirs.ThetaRad[best] * 180 / Math.PI, dirs.PhiRad[best] * 180 / Math.PI);
            }
            return _rx = new Reception(t, Beam, antenna, a);
        }

        /// <summary>brief-paa-6 R-paa6-3 — element <paramref name="element"/>'s active reflection.</summary>
        public Complex ActiveGamma(int element) => ActiveGammaOf(fa, w, element, pa.Lattice.IsOn);

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

        /// <summary>
        /// brief-paa-4 — the noise figure from the first driven circuit port to the beam's peak: F = 1 + N/(kT0·|E_s|²), with E_s the field the
        /// port's unit incident wave radiates at the peak and N the noise every noisy part radiates there (SParameterEngine.BlockFunctionalNoise,
        /// the field at one direction being a linear functional of the waves incident on the elements). The port's own termination at T0 is the
        /// source; the ports are otherwise the run's noiseless terminations, as for an S-parameter noise figure. Both polarizations count.
        /// </summary>
        private double BeamNoiseFigureDb()
        {
            var beam = Beam();
            double th = beam.PeakThetaDeg * Math.PI / 180, ph = beam.PeakPhiDeg * Math.PI / 180;
            var dir = ArrayDirections.Of([th], [ph]);
            double k = 2 * Math.PI * hz / EmConstants.C0;
            int n = pa.PortCount;
            var vt = new Complex[n];
            var vp = new Complex[n];
            for (int e = 0; e < n; e++)
            {
                var (et, ep) = elements.Patterns[elements.PatternOf[e]].At(th, ph);
                var (x, y) = pa.Lattice.Position(e);
                var phase = Complex.FromPolarCoordinates(1, k * (dir.U[0] * x + dir.V[0] * y));
                vt[e] = et * phase;
                vp[e] = ep * phase;
            }
            var noise = SParameterEngine.BlockFunctionalNoise(nl, hz, fa.Block, pa.Z0, [vt, vp]);
            var signal = SParameterEngine.BlockWaves(nl, hz, fa.Block, [(drives[0].Port, Complex.One)]);
            Complex st = 0, sp = 0;
            double root = 2 * Math.Sqrt(pa.Z0.Real);
            for (int e = 0; e < n; e++)
            {
                Complex a = (signal.Voltage[e] + pa.Z0 * signal.Current[e]) / root;
                st += vt[e] * a;
                sp += vp[e] * a;
            }
            double s2 = st.Magnitude * st.Magnitude + sp.Magnitude * sp.Magnitude;
            if (!(s2 > 0))
                throw new ExpressionException($"BeamNfDb({fa.Name}): nothing from {DrivenName(nl, drives[0].Port)} reaches the beam's peak, so it has no noise figure.");
            return 10 * Math.Log10(1 + (noise[0] + noise[1]) / (RfCore.Data.NoiseMetrics.KT0 * s2));
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

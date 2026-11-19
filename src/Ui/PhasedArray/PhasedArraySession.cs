// brief-paa-9 — the Phased Array tool's evaluations of one circuit, framework-free so the tests, UiSnapshot and the window drive the same
// code. A frame is ONE evaluation of the fields analysis that names the array (CircuitEvaluation, the run a tuning slider makes), so a move
// of the weights goes through PAA-8's fast path and PAA-8b's re-weigh and costs what they say (R-paa9-7, gate 6).
//
// THE SCAN MAP (R-paa9-5) is that evaluation repeated over (θ₀, φ₀) with the steering VARs as tunables — re-weighs of one elaboration and
// back-substitutions of one factorisation per frequency (gate 5). It starts only on a bench whose array closes on the fast path: anywhere
// else every point would be a full solve of the circuit, and the tab says so instead. Any edit cancels it (ScanMapsCancelled counts them).
//
// FAILURES (R-paa9-6) switch elements off IN MEMORY: the run's waves with the chosen elements at zero, summed again through the array
// engine. Nothing is written and the circuit is not solved again; each trial is the pattern of the remaining elements as the circuit drove
// them. FREQUENCY sums the run's weights at other frequencies (a delay turned at each, a phase held) for the beam's pointing.
//
// A SWEPT SOURCE (brief-paa-12 R-paa12-1). When the fields analysis's waves= names an analysis a parametric sweep wraps, a run of it
// forms a pattern at every sweep point and leaves no single array state for a frame. The tool shows one steering at a time and a drag
// writes the steering VAR, so its evaluations collapse those sweeps (a disabled sweep's inner runs in its place, AnalysisChain) on a copy
// of the bench made once per session: the schematic, the bench's own Run and every other caller still see the sweeps. The frame names
// the sweeps it collapsed and the values it used (R-paa12-2), so nobody reads it as the sweep's.

using System.Diagnostics;
using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices.Arrays;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Em3d;
using CircuitRF.Engine;
using CircuitRF.Engine.AntennaArray;
using CircuitRF.Engine.Mom;
using RfCore.Data;

namespace CircuitRF.Ui.PhasedArray;

/// <summary>One evaluation: a frame, or why there is none.</summary>
public sealed record PhasedArrayEvaluation(PhasedArrayFrame? Frame, string? Error);

/// <summary>R-paa9-5 — one point of a scan map: realized gain in the commanded direction and the worst |Γ_act| there.</summary>
public sealed record ScanPoint(double ThetaDeg, double PhiDeg, double RealizedGainDbi, double WorstGamma);

/// <summary>R-paa9-6 — the spread of a failure study.</summary>
public sealed record FailureTrials(double Fraction, int Off, double[] GainChangeDb, double[] SidelobeDb);

/// <summary>R-paa9-6 — the beam's pointing against frequency, with phase shifters and with true delays.</summary>
public sealed record FrequencyPointing(double[] Hz, double[] PhaseShifterThetaDeg, double[] DelayThetaDeg, bool ArrayFactorOnly);

/// <summary>
/// brief-pch-9 R-pch9-7 — the channel the array is an end of, scored at one drop: which end it is, the table (null with
/// <see cref="Refusal"/> when nothing can be scored), the channel's own Drop and the instance it is.
/// </summary>
public sealed record ChannelBeams(string Channel, bool IsTx, BeamTable? Table, string? Refusal, long Drop, long WrittenDrop)
{
    /// <summary>This end's codebook (the transmit beams on a transmit array).</summary>
    public IReadOnlyList<BeamEntry> Beams => Table is null ? [] : IsTx ? Table.TxBeams : Table.RxBeams;

    /// <summary>This end's direction of each ray (departure on a transmit array, arrival on a receive one), with its power.</summary>
    public IEnumerable<(double ThetaDeg, double PhiDeg, double PowerDb)> Rays
        => Table is null ? [] : Table.Rays.Select(r => IsTx ? (r.TxThetaDeg, r.TxPhiDeg, r.PowerDb) : (r.RxThetaDeg, r.RxPhiDeg, r.PowerDb));

    /// <summary>This end's chosen beam.</summary>
    public BeamEntry? Chosen => Table is null ? null : Beams[IsTx ? Table.Chosen.Tx : Table.Chosen.Rx];
}

/// <summary>brief-paa-9 — evaluations of one prepared circuit for the Phased Array tool.</summary>
public sealed class PhasedArraySession
{
    private static long _scansStarted, _scansCancelled, _scansRefused;
    private readonly MapLayouts _layouts = new();

    /// <summary>Scan maps started, cancelled by an edit, and refused because the bench is not on the fast path, in this process.</summary>
    public static long ScanMapsStarted => Interlocked.Read(ref _scansStarted);
    public static long ScanMapsCancelled => Interlocked.Read(ref _scansCancelled);
    public static long ScanMapsRefused => Interlocked.Read(ref _scansRefused);

    public PhasedArraySession(PreparedCircuit circuit, string block)
    {
        Circuit = circuit;
        Block = block;
        Fields = circuit.Tb is { } tb ? FieldsAnalysisFor(tb, block) : null;
        (Evaluated, CollapsedSweeps) = Collapse(circuit, Fields);
    }

    public PreparedCircuit Circuit { get; }

    /// <summary>What the tool's frames evaluate: <see cref="Circuit"/>, or a copy of it with <see cref="CollapsedSweeps"/> disabled.</summary>
    private PreparedCircuit Evaluated { get; }

    /// <summary>R-paa12-1 — the enabled sweeps wrapping the fields analysis's waves source, outermost first, that the tool's frames run
    /// without: the bench at its own values. Empty when nothing wraps it.</summary>
    public IReadOnlyList<ParametricSweepAnalysis> CollapsedSweeps { get; }

    /// <summary>The array instance.</summary>
    public string Block { get; }

    /// <summary>The fields analysis that names the array; null when the bench has none.</summary>
    public FieldsAnalysis? Fields { get; }

    /// <summary>Why this session cannot evaluate, or null.</summary>
    public string? Refusal => Circuit.ReadError
        ?? (Fields is null ? $"The bench has no fields analysis on '{Block}': add one (analysis F1 type=fields block={Block} freq=… freqUnit=GHz) to see the array." : null);

    /// <summary>The first enabled fields analysis whose block is <paramref name="block"/>.</summary>
    public static FieldsAnalysis? FieldsAnalysisFor(TestBench tb, string block)
        => tb.Analyses.OfType<FieldsAnalysis>().FirstOrDefault(f => f.Enabled && string.Equals(f.Block.Trim(), block, StringComparison.Ordinal));

    /// <summary>
    /// One frame at <paramref name="tunables"/>. <paramref name="interactive"/> is a drag's step: only the fields analysis, no measurements,
    /// and off the fast path the circuit is held at its last solve (PAA-8's estimate). Otherwise the bench's measurements are read too, with
    /// the analyses they name.
    /// </summary>
    /// <para><paramref name="progress"/> hears the run's stages (a channel's beam selection is the long one on a large array).</para>
    public PhasedArrayEvaluation Evaluate(IReadOnlyDictionary<string, string> tunables, bool interactive, (double ThetaDeg, double PhiDeg)? steer,
                                          CancellationToken cancel = default, IProgress<RunProgress>? progress = null)
    {
        if (Refusal is { } refusal) return new(null, refusal);
        var sw = Stopwatch.StartNew();
        var request = new CircuitEvaluationRequest
        {
            Tunables = tunables,
            Analyses = interactive ? [Fields!.Name] : SettledAnalyses(),
            Measurements = !interactive,
            // a drag's step holds the circuit at its last solve off the fast path (PAA-8's estimate); a settled frame solves. A far field's
            // Written evaluation writes nothing (FieldsOutput.Written)
            Fields = interactive ? FieldsOutput.Local : FieldsOutput.Written,
            Control = new RunControl { Token = cancel, Progress = progress, MinReportIntervalMs = 100 },
        };
        RunResult run;
        try { run = CircuitEvaluation.Evaluate(Evaluated, request); }
        catch (OperationCanceledException) { return new(null, "Cancelled."); }
        if (cancel.IsCancellationRequested) return new(null, "Cancelled.");
        double evalMs = sw.Elapsed.TotalMilliseconds;
        if (!run.ArrayStates.TryGetValue(Fields!.Name, out var state))
            return new(null, run.Status == RunStatus.Success && run.Errors.Count == 0 ? $"Fields '{Fields.Name}' gave no pattern." : string.Join(" ", run.Errors.DefaultIfEmpty(run.StatusMessage)));
        // brief PCH-13 R-pch13-3 — nothing reaching the elements is a pattern hundreds of dB down that looks like a result: say why instead
        // (the run's own threshold, FieldCosimulation's "below -240 dB of incident power is numerical zero").
        if (state.A.Sum(x => x.Magnitude * x.Magnitude) < 1e-24 * state.AvailableW)
            return new(null, run.Warnings.FirstOrDefault(w => w.StartsWith($"Fields '{Fields.Name}'", StringComparison.Ordinal)
                                                              && w.Contains("results are empty", StringComparison.Ordinal))
                             ?? $"Fields '{Fields.Name}': nothing the drive sends reaches '{Block}', so it has no pattern.");

        var measurements = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        double? goverT = null;
        if (run.GroupedResults is { } g && g.Groups.Contains(DataSet.MeasurementsGroup))
        {
            foreach (var (name, cube) in g.CubesIn(DataSet.MeasurementsGroup))
                if (cube.BufferLength == 1 && cube.DataKind == DataKind.Real && double.IsFinite(cube.RealValues[0])) measurements[name] = cube.RealValues[0];
            var gt = Circuit.Tb!.Measurements.FirstOrDefault(m => m.Expression.Contains("GoverTDbK(", StringComparison.Ordinal));
            if (gt is not null && measurements.TryGetValue(gt.Name, out var v)) goverT = v;
        }
        sw.Restart();
        var frame = PhasedArrayFrames.Build(state, steer, run.GroupedResults, measurements, goverT, _layouts)
                    with { EvaluationMs = evalMs, Warnings = run.Warnings, SweepPoint = SweepPointText(tunables), Measured = !interactive };
        return new(frame with { FormingMs = sw.Elapsed.TotalMilliseconds }, null);
    }

    /// <summary>
    /// The fields analysis, the harmonic balance its waves come from, and every analysis a measurement the tool SHOWS names: a per-element
    /// Pout, PAE or NF it colours the aperture by, or G/T. Any other measure's analyses are the bench's business, not the frame's: an
    /// envelope a link's MER reads is promoted to the drop sweep around it, which made a settled frame on the mmWave Array Link run all
    /// 20 drops (brief PCH-13 R-pch13-4).
    /// </summary>
    private List<string> SettledAnalyses()
    {
        var tb = Circuit.Tb!;
        var names = new List<string> { Fields!.Name };
        if (Fields.FromHarmonicBalance) names.Add(Fields.Waves.Trim());
        var analyses = tb.Analyses.Where(a => a.Enabled).Select(a => a.Name).ToHashSet(StringComparer.Ordinal);
        static bool Shown(Measurement m)
            => m.Expression.Contains("GoverTDbK(", StringComparison.Ordinal)
               || new[] { ApertureQuantity.PaPout, ApertureQuantity.PaPae, ApertureQuantity.ChannelNf }
                      .Any(q => PhasedArrayToolViewModel.MeasurementPattern(q).Pattern.IsMatch(m.Name));
        foreach (var m in tb.Measurements.Where(Shown))
        {
            Token[] tokens;
            try { tokens = new Tokenizer(m.Expression).Tokenize(); }
            catch (ExpressionException) { continue; }
            foreach (var t in tokens)
                if (t.Kind == TokenKind.Identifier && analyses.Contains(t.Text) && !names.Contains(t.Text)
                    && tb.Analyses.First(a => a.Name == t.Text) is not ParametricSweepAnalysis and not LoadpullAnalysis and not LoadpullPursuitAnalysis)
                    names.Add(t.Text);
        }
        return names;
    }

    /// <summary>
    /// R-paa12-1 — the circuit the tool evaluates: <paramref name="circuit"/> itself, or, when an enabled sweep wraps the analysis the
    /// fields analysis takes its waves from, a copy whose bench has each such sweep disabled (the copy shares every other analysis,
    /// instance and measurement; nothing of the original is changed).
    /// </summary>
    private static (PreparedCircuit Evaluated, IReadOnlyList<ParametricSweepAnalysis> Collapsed) Collapse(PreparedCircuit circuit, FieldsAnalysis? fields)
    {
        if (circuit is not { Tb: { } tb, Lib: { } lib } || fields is not { FromHarmonicBalance: true }) return (circuit, []);
        string source = fields.Waves.Trim();
        var wrapping = new List<ParametricSweepAnalysis>();
        // the runnable chain that holds the source, walked from its top down to it: every enabled sweep on the way wraps it
        var top = AnalysisChain.RunnableTops(tb).FirstOrDefault(t => t is ParametricSweepAnalysis && AnalysisChain.ChainContains(t, source, tb));
        for (Analysis? a = top; a is ParametricSweepAnalysis ps && wrapping.Count < 64; a = AnalysisChain.ResolveEffectiveInner(ps.InnerAnalysisName, tb))
            wrapping.Add(ps);
        if (wrapping.Count == 0) return (circuit, []);

        var copy = new TestBench(tb.Name) { Technology = tb.Technology, Tuning = tb.Tuning, StatisticalDraws = tb.StatisticalDraws };
        copy.Instances.AddRange(tb.Instances);
        copy.GlobalVariables.AddRange(tb.GlobalVariables);
        copy.Functions.AddRange(tb.Functions);
        copy.Measurements.AddRange(tb.Measurements);
        copy.RawDirectives.AddRange(tb.RawDirectives);
        copy.ReadWarnings.AddRange(tb.ReadWarnings);
        copy.ReadNotes.AddRange(tb.ReadNotes);
        copy.LabeledNets.UnionWith(tb.LabeledNets);
        foreach (var a in tb.Analyses)
            copy.Analyses.Add(a is ParametricSweepAnalysis ps && wrapping.Contains(ps)
                ? new ParametricSweepAnalysis(ps.Name, ps.SweepVarName, ps.SweepValues, ps.InnerAnalysisName) { Enabled = false, Model = ps.Model, EmSetup = ps.EmSetup }
                : a);
        var evaluated = PreparedCircuit.FromBench(lib, copy, circuit.BaseDirectory, circuit.PartTunables);
        evaluated.SourcePath = circuit.SourcePath;
        return (evaluated, wrapping);
    }

    /// <summary>
    /// R-paa12-2 — "one point of sweep(s) …: var = value, …" for a frame of a bench whose sweeps the tool collapsed, with each swept
    /// variable as this evaluation has it (a drag's tunable, else the bench's own definition); null when nothing was collapsed.
    /// </summary>
    public string? SweepPointText(IReadOnlyDictionary<string, string> tunables)
    {
        if (CollapsedSweeps.Count == 0 || Circuit.Tb is not { } tb) return null;
        var values = new List<string>();
        foreach (var ps in CollapsedSweeps)
        {
            string name = ps.SweepVarName;
            var v = tb.GlobalVariables.FirstOrDefault(g => g.Name == name);
            string text = tunables.TryGetValue(name, out var t) ? t.Trim() : (v?.Expression.Trim() ?? "?");
            // a VAR with no unit column is read with the unit of the parameter it is written into (Pavl=Pin dBm), when one names it alone
            string unit = v?.Unit?.Trim() is { Length: > 0 } u ? u
                : tb.Instances.SelectMany(i => i.Overrides).FirstOrDefault(o => o.Expression.Trim() == name && !string.IsNullOrWhiteSpace(o.Unit))?.Unit?.Trim() ?? "";
            if (text.EndsWith(" deg", StringComparison.Ordinal)) { text = text[..^4].TrimEnd(); unit = "deg"; }
            else if (!double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)) unit = "";
            values.Add(unit is "deg" or "°" ? $"{name} = {text}°" : unit.Length > 0 ? $"{name} = {text} {unit}" : $"{name} = {text}");
        }
        string sweeps = string.Join(", ", CollapsedSweeps.Select(s => $"'{s.Name}'"));
        return $"one point of {(CollapsedSweeps.Count == 1 ? "sweep" : "sweeps")} {sweeps}: {string.Join(", ", values)}";
    }

    /// <summary>
    /// R-paa9-5 — the scan map over θ₀ ∈ [0, <paramref name="limitDeg"/>] and φ₀ ∈ [0°, 360°), each point a re-weighed evaluation with
    /// <paramref name="tunablesAt"/>'s values. Refused (null, and the reason) when the array does not close on the fast path. Cancelled
    /// points stop the map and are counted once.
    /// </summary>
    public (IReadOnlyList<ScanPoint>? Points, string? Refusal) ScanMap(PhasedArrayFrame current, Func<double, double, IReadOnlyDictionary<string, string>> tunablesAt,
                                                                      double limitDeg, double thetaStepDeg, double phiStepDeg, Action<ScanPoint>? progress,
                                                                      CancellationToken cancel)
    {
        if (current.State.Path is not { Fast: true })
        {
            Interlocked.Increment(ref _scansRefused);
            return (null, current.State.Path?.Sentence is { } s
                ? $"No scan map: every direction would be a full solve of the circuit. {s}"
                : "No scan map: the array is not driven from a Feed-mode beamformer, so every direction would be a full solve of the circuit.");
        }
        Interlocked.Increment(ref _scansStarted);
        var points = new List<ScanPoint>();
        var thetas = new List<double>();
        for (double t = 0; t <= limitDeg + 1e-9; t += thetaStepDeg) thetas.Add(t);
        foreach (double t in thetas)
        {
            int nphi = t == 0 ? 1 : (int)Math.Round(360 / phiStepDeg);
            for (int j = 0; j < nphi; j++)
            {
                if (cancel.IsCancellationRequested) { Interlocked.Increment(ref _scansCancelled); return (null, "Cancelled."); }
                double p = j * phiStepDeg;
                var run = CircuitEvaluation.Evaluate(Evaluated, new CircuitEvaluationRequest
                {
                    Tunables = tunablesAt(t, p),
                    Analyses = [Fields!.Name],
                    Measurements = false,
                    Fields = FieldsOutput.Local,
                    Control = new RunControl { Token = cancel },
                });
                if (cancel.IsCancellationRequested) { Interlocked.Increment(ref _scansCancelled); return (null, "Cancelled."); }
                if (!run.ArrayStates.TryGetValue(Fields.Name, out var st)) continue;
                var point = new ScanPoint(t, p, RealizedGainAt(st, t, p), WorstGamma(st));
                points.Add(point);
                progress?.Invoke(point);
            }
        }
        return (points, null);
    }

    /// <summary>The first Propagation Channel whose Tx= or Rx= names this array, and which end it is; null when none does.</summary>
    public (Instance Channel, bool IsTx)? ChannelEnd()
    {
        if (Circuit.Tb is not { } tb) return null;
        static string Word(Instance i, string key) => i.Overrides.FirstOrDefault(o => o.Name.Equals(key, StringComparison.OrdinalIgnoreCase))?.Expression.Trim().Trim('"') ?? "";
        foreach (var inst in tb.Instances.Where(i => i.Reference.Equals(Core.Devices.PropagationChannelModel.TypeName, StringComparison.OrdinalIgnoreCase)))
        {
            if (Word(inst, "Tx") == Block) return (inst, true);
            if (Word(inst, "Rx") == Block) return (inst, false);
        }
        return null;
    }

    /// <summary>
    /// R-pch9-7 — the channel this array ends, at <paramref name="drop"/> (null: the channel's own), scored at <paramref name="hz"/> as a run
    /// scores it (BeamSelection.Score: nothing is applied). One elaboration of the bench with only the channel's Drop changed, in memory.
    /// </summary>
    /// <para><paramref name="control"/>: the scoring's progress (its stage counter) and cancellation, which throws
    /// <see cref="OperationCanceledException"/> to the caller.</para>
    public ChannelBeams? ChannelAt(long? drop, double hz, RunControl? control = null)
    {
        if (ChannelEnd() is not var (inst, isTx) || Circuit.Tb is not { } tb || Circuit.Lib is not { } lib) return null;
        var dropOv = inst.Overrides.FirstOrDefault(o => o.Name.Equals("Drop", StringComparison.OrdinalIgnoreCase));
        long written = 1;
        if (dropOv is not null && long.TryParse(dropOv.Expression.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long w)) written = w;
        long at = drop ?? written;
        var copy = new TestBench(tb.Name) { Technology = tb.Technology };
        copy.GlobalVariables.AddRange(tb.GlobalVariables);
        copy.Functions.AddRange(tb.Functions);
        foreach (var i in tb.Instances)
        {
            if (!ReferenceEquals(i, inst) || drop is null) { copy.Instances.Add(i); continue; }
            var ovs = i.Overrides.Where(o => !o.Name.Equals("Drop", StringComparison.OrdinalIgnoreCase))
                       .Append(new ParameterAssignment("Drop", at.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            copy.Instances.Add(new Instance(i.InstanceName, i.Reference, i.NetBindings, ovs) { RefNetBinding = i.RefNetBinding });
        }
        try
        {
            using var nl = new Core.Elaboration.Elaborator(lib) { BaseDirectory = Circuit.BaseDirectory }.Elaborate(copy);
            var ec = nl.Components.FirstOrDefault(c => c.InstancePath == inst.InstanceName);
            if (ec?.Model is not Core.Devices.PropagationChannelModel ch) return new(inst.InstanceName, isTx, null, $"'{inst.InstanceName}' did not elaborate as a channel.", at, written);
            // Drop=drop (a variable, as every example writes it) does not parse above: the drop the bench draws is the elaborated value.
            if (drop is null && ec.Parameters.TryGetValue("Drop", out var dv) && dv.Kind == ValueKind.Real)
                at = written = (long)Math.Round(dv.AsReal());
            var table = BeamSelection.Score(ch, inst.InstanceName, hz, control);
            return new(inst.InstanceName, isTx, table, table is null ? $"Nothing to choose: neither end of '{inst.InstanceName}' is an array steered by steer()." : null, at, written);
        }
        catch (Exception e) when (e is Core.Devices.ParameterRefusalException or InvalidOperationException or ExpressionException)
        {
            return new(inst.InstanceName, isTx, null, e.Message, at, written);
        }
    }

    /// <summary>Realized gain in direction (θ, φ) in degrees, dBi.</summary>
    public static double RealizedGainAt(ArrayFieldState st, double thetaDeg, double phiDeg)
    {
        var dir = ArrayDirections.Of([thetaDeg * Math.PI / 180], [phiDeg * Math.PI / 180]);
        double p = ArrayPattern.SumOnce(st.Lattice, st.A, st.Elements, dir, st.Hz).Power(0);
        double u = p / (2 * ArrayWaves.Eta0);
        return p > 0 ? 10 * Math.Log10(4 * Math.PI * u / PhasedArrayFrames.AvailablePeakW(st)) : double.NegativeInfinity;
    }

    private static double WorstGamma(ArrayFieldState st)
    {
        double worst = 0;
        for (int e = 0; e < st.Lattice.Count; e++) if (st.ActiveGamma(e) is { } g) worst = Math.Max(worst, g.Magnitude);
        return worst;
    }

    /// <summary>R-paa9-6 — <paramref name="trials"/> draws of <paramref name="fraction"/> of the elements that are on, switched off in memory;
    /// each trial's peak gain change against the intact array and its first sidelobe.</summary>
    public static FailureTrials Failures(PhasedArrayFrame frame, double fraction, int trials, int seed, CancellationToken cancel = default)
    {
        var st = frame.State;
        var on = Enumerable.Range(0, st.Lattice.Count).Where(st.Lattice.IsOn).ToArray();
        int off = (int)Math.Round(Math.Clamp(fraction, 0, 1) * on.Length);
        var rng = new Random(seed);
        var gain = new List<double>();
        var sll = new List<double>();
        for (int t = 0; t < trials && !cancel.IsCancellationRequested; t++)
        {
            var a = (Complex[])st.A.Clone();
            foreach (int e in on.OrderBy(_ => rng.Next()).Take(off)) a[e] = Complex.Zero;
            var beam = ArrayBeam.Evaluate(st.Lattice, a, st.Elements, st.Hz, frame.Beam.PeakThetaDeg, frame.Beam.PeakPhiDeg);
            gain.Add(10 * Math.Log10(beam.PeakPower / frame.Beam.PeakPower));
            sll.Add(beam.FirstSidelobeDb ?? double.NaN);
        }
        return new FailureTrials(fraction, off, [.. gain], [.. sll]);
    }

    /// <summary>R-paa9-6 — the beam's elevation against frequency, with the run's phases held (phase shifters) and turned with frequency as
    /// true delays. Over the record's frequencies when it holds three or more; otherwise the array factor alone over ±20 % of the run's
    /// frequency, since an element pattern exists only where the record solved one.</summary>
    public static FrequencyPointing Frequency(PhasedArrayFrame frame, CancellationToken cancel = default)
    {
        var st = frame.State;
        var lattice = st.Lattice;
        bool afOnly = st.RecordFrequenciesHz.Count < 3;
        double[] hz = afOnly ? [.. Enumerable.Range(0, 21).Select(i => st.Hz * (0.8 + 0.02 * i))] : [.. st.RecordFrequenciesHz];
        // the beam's direction at the run's frequency, and each element's unwrapped phase towards it: k₀·r·û₀
        var u0 = ArrayDirections.Vector(frame.Beam.PeakThetaDeg * Math.PI / 180, frame.Beam.PeakPhiDeg * Math.PI / 180);
        double k0 = 2 * Math.PI * st.Hz / EmConstants.C0;
        var amp = st.A.Select(w => w.Magnitude).ToArray();
        var shifter = new double[hz.Length];
        var delay = new double[hz.Length];
        var iso = afOnly ? ArrayElements.Single(Isotropic(st.Grid), lattice.Count) : null;
        for (int i = 0; i < hz.Length && !cancel.IsCancellationRequested; i++)
        {
            var elements = iso ?? st.ElementsAt(hz[i]);
            var ps = new Complex[lattice.Count];
            var td = new Complex[lattice.Count];
            for (int e = 0; e < lattice.Count; e++)
            {
                if (amp[e] == 0) continue;
                var (x, y) = lattice.Position(e);
                double phase = -k0 * (x * u0.X + y * u0.Y);
                ps[e] = Complex.FromPolarCoordinates(amp[e], phase);
                td[e] = Complex.FromPolarCoordinates(amp[e], phase * hz[i] / st.Hz);
            }
            shifter[i] = ArrayBeam.Evaluate(lattice, ps, elements, hz[i], frame.Beam.PeakThetaDeg, frame.Beam.PeakPhiDeg).PeakThetaDeg;
            delay[i] = ArrayBeam.Evaluate(lattice, td, elements, hz[i], frame.Beam.PeakThetaDeg, frame.Beam.PeakPhiDeg).PeakThetaDeg;
        }
        return new FrequencyPointing(hz, shifter, delay, afOnly);
    }

    /// <summary>An isotropic θ-polarized element on <paramref name="grid"/>.</summary>
    private static ElementPattern Isotropic(PlanarFarFieldGrid grid)
    {
        var et = Enumerable.Repeat(Complex.One, grid.DirectionCount).ToArray();
        return new ElementPattern(grid, et, new Complex[grid.DirectionCount]);
    }
}

// ================================================================
//  EnvelopeRunner.cs  —  resolve an `env` directive, run it (in
//  parallel time segments), and pack the DataSet.
//
//  Parallel in time: the record is split into S segments; each runs on
//  its own freshly elaborated netlist (device models and sparse solvers
//  carry per-instance scratch state, so nothing is shared), starts
//  `Warmup` early from its own harmonic-balance point and discards the
//  warm-up.  With the warm-up past the network's memory the stitched
//  result is identical to one sequential run (measured: 5 us on a
//  7 MHz-resonant drain feed -> bit-identical at float precision).
//  Auto warm-up is 5 time constants of the circuit's slowest memory
//  (EnvelopeMemory), at least 5 us; a run too short to hold two
//  segments that long runs as one.  Each segment also runs a short
//  tail into the next one, and the disagreement on that overlap,
//  relative to each recorded voltage's and IProbe current's RMS there,
//  is reported as the seam error.
// ================================================================

using System.Diagnostics;
using System.Numerics;
using System.Runtime.ExceptionServices;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Engine.HarmonicBalance;
using RfCore.Data;
using RfCore.Signals;

namespace CircuitRF.Engine.Envelope;

public static class EnvelopeRunner
{
    /// <param name="Mode">auto / lattice: widely spaced carriers get their own fundamentals; single: one envelope spans them all.</param>
    /// <param name="ZmeasWarning">The one warning an old file's retired <c>Zmeas</c> other than 50 earns; null when it
    /// has none, or 50.</param>
    /// <param name="ProbesNote">The one note an old file's retired <c>Probes</c> list earns; null when it has none.</param>
    /// <param name="Name">The analysis, for the notes a run writes.</param>
    /// <param name="SeedPerPoint">A parametric sweep gives each point its own seed (<see cref="ForPoint"/>).</param>
    /// <param name="EvmSplit">Run the record again with every noise source and mask off, to split EVM into distortion and
    /// noise and measure the SNR (brief NZ-8).</param>
    public sealed record Plan(EnvelopeParams Params, int Segments, double WarmupS, bool SaveWaveforms, int MaxMixOrder = 5,
                              string Mode = "auto", string? ZmeasWarning = null, string? ProbesNote = null, string Name = "",
                              bool SeedPerPoint = false, bool EvmSplit = false)
    {
        /// <summary>This plan at the sweep point <paramref name="point"/> (its drive text, outer points included): the same
        /// seed at every point unless <see cref="SeedPerPoint"/>, then one mixed from the point.</summary>
        public Plan ForPoint(string point) => SeedPerPoint
            ? this with { Params = Params with { Seed = RfCore.Signals.NoiseDraw.Mix(Params.Seed ^ RfCore.Signals.NoiseDraw.Hash(point)) } }
            : this;
    }

    public static Plan Resolve(EnvelopeAnalysis a, IReadOnlyDictionary<string, Value> globals)
    {
        double Num(string expr, double def)
        {
            if (string.IsNullOrWhiteSpace(expr) || expr.Equals("auto", StringComparison.OrdinalIgnoreCase)) return def;
            try
            {
                var v = HbEngine.BuildEvaluator(globals).EvalExpr(Parser.Parse(expr), HbEngine.BuildScope(globals));
                return v.Kind == ValueKind.Real ? v.AsReal() : v.AsComplex().Real;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"envelope '{a.Name}': cannot evaluate '{expr}': {ex.Message}");
            }
        }
        int osr = a.Accuracy.ToLowerInvariant() switch
        {
            "draft" => 1,
            "fine" => 8,
            "standard" or "" => 4,
            var x when int.TryParse(x, out int n) && n > 0 => n,
            _ => throw new InvalidOperationException($"envelope '{a.Name}': Accuracy must be draft, standard, fine or an oversampling count"),
        };
        double stop = Num(a.StopExpr, 100e-6);
        if (stop <= 0) throw new InvalidOperationException($"envelope '{a.Name}': Stop must be positive");
        int seg = a.SegmentsExpr.Equals("auto", StringComparison.OrdinalIgnoreCase) ? 0 : Math.Max(1, (int)Num(a.SegmentsExpr, 1));
        double warm = a.WarmupExpr.Equals("auto", StringComparison.OrdinalIgnoreCase) ? double.NaN : Math.Max(0, Num(a.WarmupExpr, 0));
        var p = new EnvelopeParams
        {
            StopS = stop,
            StepS = Num(a.StepExpr, 0),
            Oversample = osr,
            MaxHarm = Math.Max(1, (int)Num(a.MaxHarmExpr, 5)),
            Tol = Num(a.TolExpr, 1e-6),
            Probes = a.Probes.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries),
            StartS = Num(a.StartExpr, 0),
            Noise = a.Noise,
            Seed = SeedOf(Num(a.SeedExpr, 1), a.Name),
        };
        bool save = !a.SaveWaveforms.Equals("no", StringComparison.OrdinalIgnoreCase) &&
                    !a.SaveWaveforms.Equals("false", StringComparison.OrdinalIgnoreCase);
        string mode = (a.Envelope ?? "auto").Trim().ToLowerInvariant();
        if (mode is "" ) mode = "auto";
        if (mode is not ("auto" or "single" or "lattice"))
            throw new InvalidOperationException($"envelope '{a.Name}': Envelope must be auto, single or lattice (got '{a.Envelope}')");
        string? probesNote = p.Probes.Length == 0 ? null
            : $"Envelope '{a.Name}': Probes is no longer a setting. Every labeled net's voltage is recorded and the nodes it " +
              $"lists ({string.Join(", ", p.Probes)}) join them; power is measured at IProbes.";
        return new Plan(p, seg, warm, save, Math.Max(1, (int)Num(a.MaxMixOrderExpr, 7)), mode, ZmeasWarning(a, globals),
                        probesNote, a.Name, a.SeedPerPoint, a.EvmSplit);
    }

    /// <summary><c>Seed=</c> as the draws' seed: a whole number, negative ones included.</summary>
    private static ulong SeedOf(double v, string name)
    {
        if (!double.IsFinite(v) || Math.Abs(v - Math.Round(v)) > 1e-9 || Math.Abs(v) > 9e15)
            throw new InvalidOperationException(NoiseDiagnostics.EnvelopeSeed(name, v).Render());
        return unchecked((ulong)(long)Math.Round(v));
    }

    /// <summary>
    /// <c>Zmeas</c> is retired (brief ENV-MEAS-1): an envelope run reports voltages at its labeled nets and power at its
    /// IProbes, and assumes no impedance anywhere (brief ENV-MEAS-3). An old file still carries the key; one that says 50,
    /// the old default, reads silently, and any other value earns this warning, because it asked for something the run
    /// no longer does.
    /// </summary>
    private static string? ZmeasWarning(EnvelopeAnalysis a, IReadOnlyDictionary<string, Value> globals)
    {
        if (string.IsNullOrWhiteSpace(a.ZmeasExpr)) return null;
        try
        {
            var v = HbEngine.BuildEvaluator(globals).EvalExpr(Parser.Parse(a.ZmeasExpr), HbEngine.BuildScope(globals));
            var z = v.Kind == ValueKind.Real ? new Complex(v.AsReal(), 0) : v.AsComplex();
            if (z == new Complex(50, 0)) return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { }
        return $"Envelope '{a.Name}': Zmeas={a.ZmeasExpr} is no longer a setting. An envelope run reports voltages at its " +
               "labeled nets and power at its IProbes; to measure the power into a load, place an IProbe in series with it.";
    }

    /// <summary>The envelope step the engines will use (same rule as theirs: signal rate x oversampling, raised to resolve every
    /// carrier's band edge (EnvelopeEngine.Oversample), or explicit).</summary>
    public static double StepSeconds(Core.Devices.ModSourceModel[] sources, EnvelopeParams p, int fundamentals, bool spanIntermod = false)
        => StepRule(sources, p, fundamentals, spanIntermod).StepS;

    /// <summary><see cref="StepSeconds"/> with the steps per signal sample it took and the band edge that set them.</summary>
    private static (double StepS, int Oversample, double EdgeHz) StepRule(Core.Devices.ModSourceModel[] sources, EnvelopeParams p,
                                                                          int fundamentals, bool spanIntermod)
    {
        if (p.StepS > 0) return (p.StepS, Math.Max(1, p.Oversample), 0);
        double need, edge;
        if (fundamentals == 2)
        {
            var groups = EnvelopeEngineMt.Plan(sources);
            need = groups.Max(g => g.needRate);
            edge = groups.Max(g => g.edgeHz);
        }
        else
        {
            need = edge = 0;
            double f0 = sources[0].FcHz;
            foreach (var s in sources)
                foreach (var c in s.Signal.Carriers)
                {
                    need = Math.Max(need, 2 * (Math.Abs(s.FcHz + c.OffsetHz - f0) + 2.5 * c.Spec.BandwidthHz));
                    need = Math.Max(need, c.NativeRateHz);
                    edge = Math.Max(edge, Math.Abs(s.FcHz + c.OffsetHz - f0) + 0.5 * c.Spec.BandwidthHz);
                }
            // One envelope over carriers that are far apart (Envelope=single): the band must also hold their
            // third-order products 2fi - fj, or IM3 would fold back across the envelope's Nyquist edge.
            if (spanIntermod)
            {
                var cs = sources.SelectMany(s => s.Signal.Carriers.Select(c => (f: s.FcHz + c.OffsetHz, bw: c.Spec.BandwidthHz))).ToArray();
                foreach (var a in cs)
                    foreach (var b in cs)
                        if (a.f != b.f)
                            need = Math.Max(need, 2 * (Math.Abs(2 * a.f - b.f - f0) + 2.5 * Math.Max(a.bw, b.bw)));
            }
        }
        double rate = SignalRate.AtLeast(need, sources.Select(s => s.Signal).ToArray());
        int osr = EnvelopeEngine.Oversample(p.Oversample, rate, edge);
        return (1.0 / (rate * osr), osr, edge);
    }

    /// <summary>
    /// The steps per signal sample a run of <paramref name="plan"/> on <paramref name="nl"/> takes: the Accuracy preset's,
    /// or more where a carrier's band edge needs them (EnvelopeEngine.Oversample). The preset when Step is explicit or
    /// the netlist has no MOD_Source.
    /// </summary>
    public static int Oversampling(Plan plan, ElaboratedNetlist nl)
    {
        var src = nl.Components.Select(c => c.Model).OfType<Core.Devices.ModSourceModel>().ToArray();
        if (plan.Params.StepS > 0 || src.Length == 0) return Math.Max(1, plan.Params.Oversample);
        var (fundamentals, groups) = Fundamentals(plan, src, EnvelopeEngineMt.ToneSources(nl));
        return StepRule(src, plan.Params, fundamentals, fundamentals == 1 && groups > 1).Oversample;
    }

    /// <summary>The fundamentals a run takes (one, or the lattice's carrier groups and off-carrier tones) and the carrier
    /// groups.</summary>
    private static (int Fundamentals, int Groups) Fundamentals(Plan plan, Core.Devices.ModSourceModel[] src,
                                                                IReadOnlyList<(string Name, double Hz)> tones)
    {
        int groups = EnvelopeEngineMt.Plan(src).Count;
        int fundamentals = plan.Mode == "single"
            ? 1
            : EnvelopeEngineMt.Plan(src, tones, src[0].FcHz, plan.Params.MaxHarm, plan.MaxMixOrder).Count;
        return (fundamentals, groups);
    }

    /// <summary>Runs the plan; <paramref name="elaborate"/> must return a fresh netlist on every call. The FIRST one
    /// stays the caller's; every later one is a segment's and is disposed here, or a compiled model's worker
    /// instances would outlive the run (a sweep elaborates one per segment per point).</summary>
    public static DataSet Run(Plan plan, Func<ElaboratedNetlist> elaborate, AnalysisSettings? settings = null,
                              RunControl? control = null, Action<string>? note = null)
    {
        using var prepared = Prepare(plan, elaborate, note: note, settings: settings);
        return Execute(prepared, settings, control, note);
    }

    /// <summary>
    /// A run with every netlist it needs elaborated and nothing simulated: what <see cref="Execute"/> takes. Split from
    /// <see cref="Run"/> for a sweep running its points side by side, which can elaborate only while that point's
    /// value is in the testbench and must simulate after it has moved on. Disposing it disposes the segments'
    /// netlists, never the first, which stays the caller's.
    /// </summary>
    internal sealed class Prepared : IDisposable
    {
        internal required Plan Plan { get; init; }
        internal required ElaboratedNetlist[] Nets { get; init; }
        internal required int Fundamentals { get; init; }
        internal required double StepS { get; init; }
        internal required double WarmS { get; init; }
        internal required int[] Bound { get; init; }
        internal required int TailSteps { get; init; }
        internal Stopwatch Clock { get; } = Stopwatch.StartNew();
        public int Segments => Nets.Length;

        public void Dispose()
        {
            for (int s = 1; s < Nets.Length; s++) Nets[s]?.Dispose();
        }
    }

    /// <param name="maxSegments">The most time segments this run may take when <c>Segments</c> is auto; 0 = one per
    /// core. A sweep running points side by side shares the cores between them this way.</param>
    internal static Prepared Prepare(Plan plan, Func<ElaboratedNetlist> elaborate, int maxSegments = 0,
                                   Action<string>? note = null, AnalysisSettings? settings = null)
    {
        var p = plan.Params;
        var first = elaborate();
        if (plan.ZmeasWarning is { } zw) first.AddWarningOnce("envelope-zmeas:" + zw, zw);
        if (plan.ProbesNote is { } pn) first.AddNoteOnce("envelope-probes:" + pn, pn);
        if (!first.Components.Any(c => c.Model is Core.Devices.IProbeModel && c.Nodes.Length >= 2))
            first.AddNoteOnce("envelope-no-iprobe:" + plan.Name,
                $"Envelope '{plan.Name}': no IProbe is placed, so no output power, gain or AM-AM was measured. " +
                "Place an IProbe in series with a load to measure the power into it.");
        var src = first.Components.Select(c => c.Model).OfType<Core.Devices.ModSourceModel>().FirstOrDefault()
                  ?? throw new InvalidOperationException("Envelope analysis needs at least one MOD_Source (the modulated signal).");
        _ = src.Signal;                                             // generate (or load) the signal once, up front
        var allSrc = first.Components.Select(c => c.Model).OfType<Core.Devices.ModSourceModel>().ToArray();
        foreach (var m in allSrc) _ = m.Signal;
        // A P1Tone off the carriers' frequencies (an LO) is a fundamental of its own (brief NZ-11).
        var toneSources = EnvelopeEngineMt.ToneSources(first);
        var (fundamentals, groups) = Fundamentals(plan, allSrc, toneSources);
        if (plan.Mode == "single" && groups > 1)
            note?.Invoke($"envelope: Envelope=single - one envelope around {allSrc[0].FcHz / 1e9:0.####} GHz spans all {groups} carrier groups and their IM3 (no lattice)");
        if (plan.Mode == "single")
            foreach (var (name, hz) in toneSources)
                if (!Enumerable.Range(1, Math.Max(1, p.MaxHarm)).Any(k => Math.Abs(hz - k * allSrc[0].FcHz) < 1.0))
                    first.AddWarningOnce("envelope-tone-off:" + name,
                        EnvelopeToneDiagnostics.OffSingleEnvelope(plan.Name, name, hz, allSrc[0].FcHz).Render());
        if (fundamentals > 2)
            throw new InvalidOperationException($"envelope: {fundamentals} widely spaced carrier groups would need {fundamentals} fundamentals; up to 2 are supported");

        // measured once, on a netlist of its own, and only when something asks
        bool measured = false;
        EnvelopeMemory.Slowest? slowest = null;
        EnvelopeMemory.Slowest? SlowestMemory()
        {
            if (!measured) { slowest = Memory(first, allSrc, elaborate, settings ?? new AnalysisSettings(), p.StopS); measured = true; }
            return slowest;
        }
        if (allSrc.Length == 1 && SignalComposer.PulseOf(allSrc[0].Spec) is { } pulse)
            note?.Invoke(PulseMemoryNote(pulse, p, SlowestMemory()));

        int cores = Math.Max(1, Environment.ProcessorCount);
        double warm = double.IsNaN(plan.WarmupS) ? 5e-6 : plan.WarmupS;
        int most = plan.Segments > 0 ? plan.Segments : maxSegments > 0 ? Math.Min(maxSegments, cores) : cores;
        // never cut a segment shorter than 4x its warm-up or 20 us — the warm-up would dominate
        int SegmentsFor(double w) => (int)Math.Clamp(Math.Floor(p.StopS / Math.Max(4 * w, 20e-6)), 1, most);
        int segs = SegmentsFor(warm);
        if (plan.Segments == 1) segs = 1;
        // Both auto and about to split: the warm-up must outlast the circuit's memory, 5 time constants of the slowest.
        // An explicit Segments or Warmup is obeyed as written (the seam warning reports what it costs), and a run that
        // stays one segment anyway is not asked.
        if (segs > 1 && plan.Segments == 0 && double.IsNaN(plan.WarmupS) &&
            SlowestMemory() is { } memory &&
            5 * memory.Seconds > warm)
        {
            warm = 5 * memory.Seconds;
            segs = SegmentsFor(warm);
            if (segs == 1)
                note?.Invoke($"Envelope ran as one segment: {memory.What} remembers {EnvelopeMemory.Time(memory.Seconds)}. " +
                             "Set Segments in the Envelope analysis setup to run in parallel.");
        }
        if (segs > 1 && warm <= 0)
            note?.Invoke($"envelope: Warmup=0 with {segs} parallel segments - each segment starts cold, so the seams are not settled (see EnvSeamError); leave Warmup auto or use Segments=1");
        // segment boundaries on WHOLE steps (a non-integer split drifts the stitched record by samples)
        var (h, osr, edge) = StepRule(allSrc, p, fundamentals, spanIntermod: fundamentals == 1 && groups > 1);
        if (p.StepS <= 0 && osr > Math.Max(1, p.Oversample))
            first.AddNoteOnce("envelope-oversample:" + plan.Name,
                $"Envelope '{plan.Name}': stepped at {osr}x the signal rate, not the Accuracy's {Math.Max(1, p.Oversample)}x: a carrier's " +
                $"band reaches {edge / 1e6:0.###} MHz from the envelope's centre, and the step takes at least " +
                $"{EnvelopeEngine.StepsPerEdgeCycle:0} steps a cycle there. EVM through a filter reads closer at standard or fine, " +
                "or with the source's Fc on the carrier.");
        int totalSteps = (int)Math.Round(p.StopS / h);
        var bound = Enumerable.Range(0, segs + 1).Select(i => (int)((long)totalSteps * i / segs)).ToArray();
        int tailSteps = segs > 1 ? Math.Max(1, (int)Math.Min(0.25 * totalSteps / segs, Math.Round(1e-6 / h))) : 0;

        var nets = new ElaboratedNetlist[segs];
        nets[0] = first;
        var prepared = new Prepared
        {
            Plan = plan, Nets = nets, Fundamentals = fundamentals, StepS = h, WarmS = warm, Bound = bound, TailSteps = tailSteps,
        };
        try
        {
            for (int s = 1; s < segs; s++) nets[s] = elaborate();
        }
        catch
        {
            prepared.Dispose();
            throw;
        }
        return prepared;
    }

    /// <summary>The seam error (a probe's worst overlap mismatch over its RMS) above which a run warns; measured in
    /// src/Engine/RESOLVED.md (brief ENV-SEG) as where Pout_avg moves by 0.01 dB or ACLR by 0.1 dB.</summary>
    internal const double SeamWarnAbove = 0.01;

    /// <summary>
    /// One seam: each recorded signal's (a node voltage, then an IProbe current) worst harmonic-<paramref name="kk"/> mismatch on the <paramref name="ov"/>-step overlap
    /// (<paramref name="prev"/> from step <paramref name="pseg"/>, <paramref name="cur"/> from 0), relative to its own RMS
    /// there — the value at a step would let one sample near zero inflate it. A probe with next to no harmonic is
    /// measured against the largest probe's RMS. Returns the worst over every probe, and over the probes that are not
    /// <paramref name="state"/>, with that probe's index (−1 when there is none).
    /// </summary>
    internal static (double All, double External, int Probe) Seam(Complex[][][] prev, int pseg, Complex[][][] cur, int ov, int kk, bool[] state)
    {
        int np = state.Length;
        var rms = new double[np];
        for (int pi = 0; pi < np; pi++)
        {
            double sum = 0;
            for (int i = 0; i < ov; i++) { var a = prev[pseg + i][pi][kk]; sum += a.Real * a.Real + a.Imaginary * a.Imaginary; }
            rms[pi] = ov > 0 ? Math.Sqrt(sum / ov) : 0;
        }
        double floor = Math.Max(1e-6 * (np > 0 ? rms.Max() : 0), 1e-12);
        double all = 0, external = 0;
        int probe = -1;
        for (int pi = 0; pi < np; pi++)
        {
            double worst = 0;
            for (int i = 0; i < ov; i++) worst = Math.Max(worst, (prev[pseg + i][pi][kk] - cur[i][pi][kk]).Magnitude);
            double e = worst / Math.Max(rms[pi], floor);
            all = Math.Max(all, e);
            if (!state[pi] && (probe < 0 || e > external)) { external = e; probe = pi; }
        }
        return (all, external, probe);
    }

    /// <summary>Which probes are a model's internal state — a trap, dispersion or thermal node — whose harmonic 1 means
    /// little: measured in the seam error, left out of the warning.</summary>
    private static bool[] StateProbes(ElaboratedNetlist nl, string[] probes)
    {
        var state = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ec in nl.Components)
            foreach (int port in ec.Model.StatePorts)
                if (2 * port < ec.Nodes.Length && ec.Nodes[2 * port] > 0)
                    state.Add(nl.Nodes.NameOf(ec.Nodes[2 * port]));
        return [.. probes.Select(n => state.Contains(n) || n.StartsWith("__", StringComparison.Ordinal))];
    }

    /// <summary>
    /// What a pulsed-CW run will show, said before it starts, because a thermal train is long: the slowest memory the
    /// circuit holds against Stop. A train settles only after several of its slowest time constants, so a run shorter
    /// than five of them measures the first pulses from cold rather than a settled train.
    /// </summary>
    internal static string PulseMemoryNote(PulseShape pulse, EnvelopeParams p, EnvelopeMemory.Slowest? memory)
    {
        int pulses = EnvelopePulse.Starts(pulse, p.StartS, p.StopS).Count;
        string head = $"Pulsed CW: {pulses} pulse{(pulses == 1 ? "" : "s")} of {PulsedCw.FormatTime(pulse.Width)} every " +
                      $"{PulsedCw.FormatTime(pulse.Pri)} in Stop {PulsedCw.FormatTime(p.StopS)}.";
        if (memory is null) return head + " The circuit declares no slow memory, so every pulse is a settled one.";
        double settle = 5 * memory.Seconds;
        return settle <= p.StopS - pulse.Pri
            ? $"{head} The slowest memory, {memory.What} ({EnvelopeMemory.Time(memory.Seconds)}), settles within the run: the last pulses are a settled train."
            : $"{head} The slowest memory, {memory.What} ({EnvelopeMemory.Time(memory.Seconds)}), needs about {EnvelopeMemory.Time(settle)} to settle: " +
              "this run shows the first pulse from cold, not a settled train.";
    }

    /// <summary>The slowest memory of the circuit: what its models declare, or its linear network's slowest pole, measured
    /// on a netlist of its own so the run's netlists are never stamped by it.</summary>
    private static EnvelopeMemory.Slowest? Memory(ElaboratedNetlist first, Core.Devices.ModSourceModel[] sources,
                                                  Func<ElaboratedNetlist> elaborate, AnalysisSettings settings, double stopS)
    {
        var declared = EnvelopeMemory.Declared(first);
        double f0 = sources[0].FcHz, need = 0;
        foreach (var s in sources)
            foreach (var c in s.Signal.Carriers)
            {
                need = Math.Max(need, 2 * (Math.Abs(s.FcHz + c.OffsetHz - f0) + 2.5 * c.Spec.BandwidthHz));
                need = Math.Max(need, c.NativeRateHz);
            }
        EnvelopeMemory.Slowest? pole = null;
        var own = elaborate();
        if (!ReferenceEquals(own, first))
            using (own)
                try { pole = EnvelopeMemory.LinearPole(own, settings, Math.PI * need, stopS); }
                catch (Exception ex) when (ex is not OperationCanceledException) { pole = null; }
        return pole is null ? declared : declared is null || pole.Seconds > declared.Seconds ? pole : declared;
    }

    /// <summary>Simulates a prepared run. Touches no testbench and elaborates nothing, so prepared runs of different
    /// sweep points may execute at the same time.</summary>
    internal static DataSet Execute(Prepared prepared, AnalysisSettings? settings = null, RunControl? control = null,
                                  Action<string>? note = null)
    {
        var plan = prepared.Plan;
        var p = plan.Params;
        var nets = prepared.Nets;
        int segs = nets.Length;
        double warm = prepared.WarmS;

        var (merged, seam) = Simulate(prepared, p, settings, control, note, quiet: false);

        // EvmSplit (brief NZ-8): the same record again with every noise source and mask off — the same netlists, the same
        // steps — so EVM splits into distortion and noise and the noise the first run added can be measured in-channel.
        EnvelopeRunResult? quiet = null;
        if (plan.EvmSplit)
        {
            bool masks = nets[0].Components.Any(ec => EnvelopePhaseNoise.Mask(ec) is not null);
            if (p.Noise || masks)
                // MinNewton = 1: the noise defeats the noisy run's predictor at nearly every step, so it is solved to well
                // inside Tol; the noiseless run's predictor is accepted wherever it is within Tol — about 1 % of a
                // -37 dBm probe's current at the default 1e-6 A — and would read distortion the noisy run does not have.
                quiet = Simulate(prepared, p with { Noise = false, PhaseNoise = false, MinNewton = 1 }, settings, control, note, quiet: true).Merged;
            else
                nets[0].AddNoteOnce("envelope-evmsplit-nothing:" + plan.Name,
                    $"Envelope '{plan.Name}': EvmSplit=yes has nothing to split - the run has no Noise and no PhaseNoise mask - so it ran once.");
        }
        // a run that started cold (no warm-up on its first segment) is measured after a short settle
        double firstWarm = segs > 1 ? warm : (double.IsNaN(plan.WarmupS) ? 0 : plan.WarmupS);
        // — except a pulse train, which starts in its off-time at the quiescent point and has nothing to settle: its
        // Pin_avg and every probe's Pavg are then averages over the whole record, off-time included
        double skip = firstWarm > 0 || EnvelopePulse.Of(merged) is not null
            ? 0 : Math.Min(5e-6, 0.1 * p.StopS);
        var ds = EnvelopeDataSet.Build(merged, plan, skip, note, quiet);
        double[] productHz = merged.MixFreqHz ?? [.. Enumerable.Range(0, merged.MaxHarm + 1).Select(k => k * merged.F0Hz)];
        EnvelopeWireTemperature.Add(ds, nets[0], merged, productHz, skip);
        ds.Add("__EnvSegments", DataCube.Scalar(segs));
        ds.Add("__EnvWarmup_s", DataCube.Scalar(warm));
        ds.Add("__EnvSeamError", DataCube.Scalar(seam));
        return ds;
    }

    /// <summary>Runs every segment of <paramref name="prepared"/> with <paramref name="p"/> and stitches them: the record and
    /// its worst seam error. The <paramref name="quiet"/> run (EvmSplit's second) reports nothing a first run has not.</summary>
    private static (EnvelopeRunResult Merged, double Seam) Simulate(Prepared prepared, EnvelopeParams p, AnalysisSettings? settings,
                                                                    RunControl? control, Action<string>? note, bool quiet)
    {
        var sw = prepared.Clock;
        var plan = prepared.Plan;
        var nets = prepared.Nets;
        int segs = nets.Length, fundamentals = prepared.Fundamentals, tailSteps = prepared.TailSteps;
        double h = prepared.StepS, warm = prepared.WarmS;
        var bound = prepared.Bound;
        int cores = Math.Max(1, Environment.ProcessorCount);
        var results = new EnvelopeRunResult[segs];
        var opts = new ParallelOptions { MaxDegreeOfParallelism = Environment.GetEnvironmentVariable("CRF_ENV_SERIAL") is null ? cores : 1, CancellationToken = control?.Token ?? default };
        try
        {
            Parallel.For(0, segs, opts, s =>
            {
                var ps = p with
                {
                    StepS = h,
                    StartS = p.StartS + bound[s] * h,
                    StopS = (bound[s + 1] - bound[s] + (s < segs - 1 ? tailSteps : 0)) * h,
                    WarmupS = segs > 1 && s > 0 ? warm : (plan.Segments == 1 || segs == 1 ? (double.IsNaN(plan.WarmupS) ? 0 : plan.WarmupS) : warm),
                };
                results[s] = fundamentals == 2
                    ? new EnvelopeEngineMt(nets[s], settings).Run(ps, plan.MaxMixOrder, control)
                    : new EnvelopeEngine(nets[s], settings).Run(ps, control);
            });
        }
        catch (AggregateException ae)
        {
            // A segment's own exception rather than "One or more errors occurred (…)": a cancellation if there
            // was one, else the first failure reported.
            var all = ae.Flatten().InnerExceptions;
            ExceptionDispatchInfo.Capture(all.FirstOrDefault(e => e is OperationCanceledException) ?? all[0]).Throw();
            throw;
        }

        // stitch, measure the seam on the overlap tails
        h = results[0].StepS;
        var time = new List<double>(); var probe = new List<Complex[][]>(); var sup = new List<double[]>();
        var ipI = new List<Complex[][]>(); var ipV = new List<Complex[][]>(); var wbI = new List<Complex[][]>();
        var drv = new List<Complex>[results[0].Drive.Length];
        for (int i = 0; i < drv.Length; i++) drv[i] = new List<Complex>();
        double seam = 0, seamExternal = 0;
        string? seamProbe = null;
        // every recorded voltage, then every IProbe current: a current is never a model's internal state
        var stateProbe = StateProbes(nets[0], results[0].ProbeNames).Concat(results[0].IProbeNames.Select(_ => false)).ToArray();
        string[] seamNames = [.. results[0].ProbeNames, .. results[0].IProbeNames];
        static Complex[][][] Signals(EnvelopeRunResult x) =>
            [.. Enumerable.Range(0, x.Time.Length).Select(t => (Complex[][])[.. x.Probe[t], .. x.IProbeCurrent[t]])];
        for (int s = 0; s < segs; s++)
        {
            var r = results[s];
            int perSeg = bound[s + 1] - bound[s];
            int take = s < segs - 1 ? perSeg : r.Time.Length;
            for (int i = 0; i < take && i < r.Time.Length; i++)
            {
                time.Add((bound[s] + i) * h);
                probe.Add(r.Probe[i]); sup.Add(r.SupplyCurrent[i]);
                ipI.Add(r.IProbeCurrent[i]); ipV.Add(r.IProbeVoltage[i]);
                if (r.WBondCurrent.Length > 0) wbI.Add(r.WBondCurrent[i]);
                for (int d = 0; d < drv.Length; d++) drv[d].Add(r.Drive[d][i]);
            }
            if (s > 0)
            {
                var prev = results[s - 1];
                int pseg = bound[s] - bound[s - 1];
                int ov = Math.Min(prev.Time.Length - pseg, r.Time.Length);
                int kk = Math.Min(1, r.Probe[0][0].Length - 1);
                var (all, external, worstProbe) = Seam(Signals(prev), pseg, Signals(r), ov, kk, stateProbe);
                seam = Math.Max(seam, all);
                if (worstProbe >= 0 && external > seamExternal) { seamExternal = external; seamProbe = seamNames[worstProbe]; }
            }
        }
        var R = results[0];
        var merged = new EnvelopeRunResult
        {
            Time = time.ToArray(), ProbeNames = R.ProbeNames, Probe = probe.ToArray(), SupplyNames = R.SupplyNames,
            SupplyCurrent = sup.ToArray(), SupplyVoltage = R.SupplyVoltage,
            IProbeNames = R.IProbeNames, IProbeCurrent = ipI.ToArray(), IProbeVoltage = ipV.ToArray(),
            WBondPaths = R.WBondPaths, WBondCurrent = wbI.ToArray(), Drive = drv.Select(d => d.ToArray()).ToArray(),
            DriveRe = R.DriveRe, F0Hz = R.F0Hz, StepS = h, MaxHarm = R.MaxHarm, SignalRateHz = R.SignalRateHz, Sources = R.Sources,
            WallSeconds = sw.Elapsed.TotalSeconds, MeanNewton = results.Average(x => x.MeanNewton),
            JacobianBuilds = results.Sum(x => x.JacobianBuilds), WorstTangentError = results.Max(x => x.WorstTangentError),
            SnpFits = R.SnpFits,
            CarrierMix = R.CarrierMix, CarrierOffsetHz = R.CarrierOffsetHz, CarrierLabels = R.CarrierLabels,
            ToneFreqsHz = R.ToneFreqsHz, MixLabels = R.MixLabels, MixFreqHz = R.MixFreqHz, MixTones = R.MixTones,
        };
        if (!quiet) foreach (var n in results.SelectMany(x => x.Notes).Distinct()) { merged.Notes.Add(n); note?.Invoke(n); }
        if (!quiet && seamExternal > SeamWarnAbove)
            note?.Invoke($"Envelope segments disagree at their seams on probe '{seamProbe}'. " +
                         "Set Warm-up and Segments to auto in the Envelope analysis setup.");
        return (merged, seam);
    }
}

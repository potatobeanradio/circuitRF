// brief-paa-6 R-paa6-1 / R-paa6-2 / R-paa6-3 — a fields analysis whose waves come from a harmonic-balance solution (docs/design/phased-array.md §7).
//
// `waves=HB1` reads the block's terminal voltages at harmonic k from HB1's solution (its linear back-solver, which is what fills HB's own V cube) and
// forms the currents INTO the block from the block's own linear stamp at k·f0: the block alone in a small MNA with each of its nodes held at the
// voltage HB solved for it, so I = Y·V with exactly the Y HB stamped, and no unknown is added to HB. The waves at each port's Z0 are then the weights
// of equation (1) (an EM Field Data block, PointOf) or (2) (a Phased Antenna Array at a level other than exact, ArrayPointOf), and everything after is
// the existing far-field path.
//
// THE SCALE IS HB'S OWN. HB's phasors are peak amplitudes, so a peak wave a = (V + Z0·I)/(2√Z0) carries |a|²/2 watts. CosimWaves keeps |A|² as
// watts, so A = a/√2, and the pattern is referred to a "drive voltage" of 1/√2 on that scale, which makes it the field the PAs actually radiate:
// E per 1 V peak with the voltage 1 V, and the accepted power the watts HB delivers into the block. The realized gain is per watt incident on the
// block, so it carries the block's own active mismatch.
//
// INSIDE A SWEEP (R-paa6-2) the sweep wraps the HB analysis, not this one, so this one runs that sweep itself: the HB analysis's entry in the bench is
// stood in for, for the length of the run, by this analysis, and the sweep engine's runOtherInner runs HB and forms the pattern at each point. The
// result has the sweep's axes exactly as HB1's own result does.

using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Layout;
using CircuitRF.Engine;
using CircuitRF.Engine.HarmonicBalance;
using RfCore.Data;

namespace CircuitRF.Design.Em3d;

/// <summary>brief-paa-6 — what a fields analysis with <c>waves=</c> needs beyond its netlist: the bench to run the HB analysis on.</summary>
public sealed record FieldsHbContext(Library Library, TestBench Bench, string? BaseDirectory);

public static partial class FieldCosimulation
{
    /// <summary>The power each element accepts from the circuit, in watts at HB's own drive (R-paa6-3).</summary>
    public const string ElementDeliveredCube = "Pdel";

    /// <summary>A harmonic-balance solution's waves at the block, and what they came from.</summary>
    internal sealed record HbWaveSource(string Analysis, int Harmonic, double F0, CosimWaves Waves);

    /// <summary>
    /// R-paa6-1 / R-paa6-2 — runs a fields analysis whose <c>waves=</c> names an HB analysis: that analysis alone, or the parametric sweep that wraps it,
    /// with a pattern at every point. Throws the refusal's sentence.
    /// </summary>
    public static DataSet RunFromHarmonicBalance(FieldsAnalysis fa, ElaboratedNetlist nl, FieldsHbContext ctx, List<string> notes, RunControl? control = null,
                                                 Action<IFieldMetrics>? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(fa);
        ArgumentNullException.ThrowIfNull(ctx);
        string who = $"Fields '{fa.Name}'";
        var source = WavesAnalysisOf(fa, ctx.Bench, who);
        var sel = ChainSelector.Select(ctx.Bench, source.Name, a => a.GetType() == source.GetType(), source is EnvelopeAnalysis ? "envelope" : "HB", "");
        if (sel.Selected is not ParametricSweepAnalysis sweep)
        {
            if (source is EnvelopeAnalysis env) return FromEnvelope(fa, nl, env, ctx, notes, control, metrics, null);
            var hba = (HarmonicBalanceAnalysis)source;
            var run = RunHb(hba, nl, ctx, null);
            return FromHarmonicBalance(fa, nl, hba.Name, run, notes, control, metrics, null);
        }

        // The sweep wraps the source; this analysis stands in for it while it runs, so the engine hands each point's netlist to us.
        var bench = ctx.Bench;
        int at = bench.Analyses.IndexOf(source);
        var standIn = new FieldsAnalysis(source.Name) { Block = fa.Block, Waves = source.Name, HarmonicExpr = fa.HarmonicExpr, Enabled = true };
        Complex[,]? seed = null;
        var cuts = new CutMemo();   // brief-paa-11 — one cut grid for every point, so the cut cubes stack
        DataSet Point(Analysis _, ElaboratedNetlist pointNl, RunControl? pointControl)
        {
            string point = pointNl.WireThermal?.Drive ?? "a sweep point";
            if (source is EnvelopeAnalysis env) return FromEnvelope(fa, pointNl, env, ctx, [], pointControl, null, point, cuts);
            var hba = (HarmonicBalanceAnalysis)source;
            var run = RunHb(hba, pointNl, ctx, seed);
            seed = run.Converged ? run.InterfaceV : null;
            return FromHarmonicBalance(fa, pointNl, hba.Name, run, [], pointControl, null, point, cuts);
        }
        bench.Analyses[at] = standIn;
        try
        {
            var data = ParametricSweepEngine.Run(sweep, ctx.Library, bench, null, ctx.BaseDirectory, control, diagnosticsInto: nl, runOtherInner: Point);
            notes.Add($"Fields '{fa.Name}': a result at each point of sweep '{sweep.Name}', from {source.Name}'s waves at '{fa.Block}'");
            metrics?.Invoke(SweptMetrics(fa, sweep));
            return data;
        }
        finally
        {
            bench.Analyses[at] = source;
        }
    }

    /// <summary>R-cp5-1's runner, for a sweep that wraps a fields analysis with <c>waves=</c> directly: HB at each point, then the pattern.</summary>
    private static DataSet HbSweepPoint(FieldsAnalysis fa, ElaboratedNetlist nl, FieldsHbContext ctx, RunControl? control, CutMemo cuts)
    {
        string point = nl.WireThermal?.Drive ?? "a sweep point";
        var source = WavesAnalysisOf(fa, ctx.Bench, $"Fields '{fa.Name}'");
        if (source is EnvelopeAnalysis env) return FromEnvelope(fa, nl, env, ctx, [], control, null, point, cuts);
        var hba = (HarmonicBalanceAnalysis)source;
        return FromHarmonicBalance(fa, nl, hba.Name, RunHb(hba, nl, ctx, null), [], control, null, point, cuts);
    }

    /// <summary>The analysis <c>waves=</c> names — harmonic balance (brief-paa-6) or envelope (brief-paa-6b) — or the refusal: none of that name, or
    /// one of another kind.</summary>
    internal static Analysis WavesAnalysisOf(FieldsAnalysis fa, TestBench tb, string who)
    {
        string name = fa.Waves.Trim();
        var named = tb.Analyses.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.Ordinal))
                    ?? tb.Analyses.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
        if (named is HarmonicBalanceAnalysis or EnvelopeAnalysis) return named;
        var ok = tb.Analyses.Where(a => a is HarmonicBalanceAnalysis or EnvelopeAnalysis).Select(a => a.Name).ToList();
        string these = ok.Count == 0 ? "This bench has none." : $"This bench's: {string.Join(", ", ok)}.";
        if (named is null) throw new InvalidOperationException($"{who}: waves='{name}' names no analysis of this bench. It names a harmonic-balance or an envelope analysis. {these}");
        throw new InvalidOperationException($"{who}: waves='{name}' names a {KindOf(named)} analysis, and waves= takes a harmonic-balance or an envelope analysis, whose solution " +
                                            $"gives the block's waves. {these}");
    }

    private static string KindOf(Analysis a) => a switch
    {
        SParameterAnalysis => "S-parameter",
        DcAnalysis => "DC",
        ParametricSweepAnalysis => "parametric sweep",
        LoadpullAnalysis => "loadpull",
        LoadpullPursuitAnalysis => "loadpull-pursuit",
        FieldsAnalysis => "fields",
        EnvelopeAnalysis => "envelope",
        OscillatorAnalysis => "oscillator",
        _ => a.GetType().Name,
    };

    /// <summary>One HB solve, warm-started when a seed is given and run again cold when that does not converge.</summary>
    private static HbRunResult RunHb(HarmonicBalanceAnalysis hba, ElaboratedNetlist nl, FieldsHbContext ctx, Complex[,]? seed)
    {
        var p = HbEngine.Resolve(hba, nl.ResolvedGlobals, nl.GlobalsWithExplicitUnit);
        var run = new HbEngine(nl, ctx.Bench, null, null, ctx.Library, ctx.BaseDirectory).Run(p, seed);
        if (!run.Converged && seed is not null)
            run = new HbEngine(nl, ctx.Bench, null, null, ctx.Library, ctx.BaseDirectory).Run(p);
        return run;
    }

    /// <summary>The pattern and the array group from one HB solution.</summary>
    private static DataSet FromHarmonicBalance(FieldsAnalysis fa, ElaboratedNetlist nl, string hbName, HbRunResult run, List<string> notes,
                                               RunControl? control, Action<IFieldMetrics>? metrics, string? sweepPoint, CutMemo? cutMemo = null)
    {
        string who = $"Fields '{fa.Name}'";
        string where = sweepPoint is null ? "" : $" at {sweepPoint}";
        var back = run.BackSolver
                   ?? throw new InvalidOperationException($"{who}: '{hbName}' is a multi-tone analysis, whose solution has a mixing product for each frequency, and waves= forms a " +
                                                          "pattern from a single-tone solution's harmonic. Run the array from a single-tone analysis.");
        if (!run.Converged)
            nl.AddWarningOnce($"fields:{fa.Name}:hb-unconverged:{sweepPoint}",
                              $"{who}{where}: '{hbName}' did not converge, so the pattern is formed from a solution that does not satisfy the circuit.");
        double f0 = run.DataSet.Cubes.TryGetValue("ToneFreqs", out var tones) ? tones.RealValues[0] : double.NaN;
        int maxHarm = run.DataSet.Cubes.TryGetValue("V", out var v) && v.Axes.FirstOrDefault(x => x.Name == "harmonic") is { } h ? h.Length - 1 : 0;
        int k = HarmonicOf(fa, nl, who);
        if (k > maxHarm)
            throw new InvalidOperationException($"{who}: harmonic={k}, and '{hbName}' solves harmonics up to {maxHarm} (its MaxHarm). Raise MaxHarm, or name a lower harmonic.");
        double hz = k * f0;

        var block = nl.Components.FirstOrDefault(c => string.Equals(c.InstancePath, fa.Block, StringComparison.Ordinal))
                    ?? throw new InvalidOperationException($"{who}: the circuit has no instance '{fa.Block}'.");
        string recordPath;
        IReadOnlyList<Complex> z0s;
        switch (block.Model)
        {
            case PhasedArrayModel pa:
                if (pa.Feed is not null)
                    throw new InvalidOperationException($"{who}: '{fa.Block}' is a {PhasedArrayModel.DisplayName} in Feed mode, whose elements are inside it; waves= reads an " +
                                                        "amplifier on every element's pin, which is Elements mode.");
                recordPath = pa.RecordPath;
                z0s = [.. Enumerable.Repeat(pa.Z0, pa.PortCount)];
                break;
            case SnpModel { RecordPath: { } rp } snp:
                recordPath = rp;
                z0s = [.. Enumerable.Repeat(Complex.Zero, snp.PortCount)];
                break;
            case SnpModel snp:
                throw new PlainSnpFieldsBlockException(who, fa.Block, snp.FilePath);
            default:
                throw new InvalidOperationException($"{who}: '{fa.Block}' is a {block.ComponentType}, not an {EmFieldDataRecord.DisplayName} block or a {PhasedArrayModel.DisplayName}.");
        }
        var (far, refused) = PortFarFieldSet.Open(recordPath);
        if (far is null) throw new InvalidOperationException($"{who}: {refused}");
        string name = Path.GetFileName(recordPath);
        int i = far.IndexOf(hz);
        if (i < 0)
        {
            double nearest = far.FrequenciesHz.MinBy(f => Math.Abs(f - hz));
            throw new InvalidOperationException($"{who}: harmonic {k} of '{hbName}' is {MaterialDispersion.Hz(hz)}, and '{name}' holds no pattern there: the nearest frequency it " +
                                                $"holds is {MaterialDispersion.Hz(nearest)}. A pattern exists only at frequencies the run solved. Run '{hbName}' at a fundamental whose harmonic " +
                                                "the record holds, or name another harmonic.");
        }
        if (block.Model is SnpModel) z0s = [.. Enumerable.Repeat(far.Z0, far.PortCount)];
        if (z0s.Count != far.PortCount)
            throw new InvalidOperationException($"{who}: '{fa.Block}' has {z0s.Count} ports and its far field {far.PortCount}.");

        var waves = HbWavesAt(fa, nl, block, back, k, hz, z0s);
        var source = new HbWaveSource(hbName, k, f0, waves);
        return block.Model is PhasedArrayModel array
            ? RunArray(fa, nl, array, hz, [(1, Complex.One)], notes, control, FieldsOutput.Written, metrics, sweepPoint, source, cutMemo)
            : RunBlockFromHb(fa, nl, far, i, source, notes, control, metrics, sweepPoint);
    }

    /// <summary>The harmonic <c>harmonic=</c> names, evaluated over the globals: a whole number of at least 1.</summary>
    private static int HarmonicOf(FieldsAnalysis fa, ElaboratedNetlist nl, string who)
    {
        string text = fa.HarmonicExpr.Trim();
        if (text.Length == 0) return 1;
        double value;
        try
        {
            var eval = new Evaluator();
            var scope = new Scope("globals");
            foreach (var (name, val) in nl.ResolvedGlobals)
            {
                scope.Bind(name, val.ToString()!);
                eval.InjectResolved("globals", name, val);
            }
            value = eval.Eval(text, scope, null).AsReal();
        }
        catch (Exception e) { throw new InvalidOperationException($"{who}: harmonic='{text}' is not a number ({e.Message})."); }
        if (!(value >= 1) || Math.Abs(value - Math.Round(value)) > 1e-9)
            throw new InvalidOperationException($"{who}: harmonic='{text}' is {value.ToString("G6", CultureInfo.InvariantCulture)}; it is a whole number of at least 1, the fundamental being 1. " +
                                                "Harmonic 0 is DC, which radiates nothing.");
        return (int)Math.Round(value);
    }

    /// <summary>
    /// R-paa6-1 — the block's waves in HB's solution at harmonic <paramref name="k"/>: each node's voltage from the back-solver, the currents into the block
    /// from its own stamp at <paramref name="hz"/> with those voltages held, and the waves in each port's Z0, on CosimWaves' scale (|A|² in watts).
    /// </summary>
    internal static CosimWaves HbWavesAt(FieldsAnalysis fa, ElaboratedNetlist nl, ElaboratedComponent block, HbLinearBackSolver back, int k, double hz,
                                         IReadOnlyList<Complex> z0s)
    {
        int n = z0s.Count;
        Complex Node(int node) => node > 0 ? back.GetNodeVoltage(node, k, 0) : Complex.Zero;
        var current = BlockCurrents(nl, block, 2 * Math.PI * hz, Node);
        var a = new Complex[n];
        var b = new Complex[n];
        double incident = 0, reflected = 0;
        for (int p = 0; p < n; p++)
        {
            Complex volt = Node(block.Nodes[p]) - Node(block.ReferenceNode);
            Complex z0 = z0s[p];
            Complex sq = RfCore.RFNetwork.ComplexSqrt(z0);
            // peak waves over √2: |A|² is the power the wave carries
            a[p] = (volt + z0 * current[p]) / (2 * sq) / Math.Sqrt(2);
            b[p] = (volt - z0 * current[p]) / (2 * sq) / Math.Sqrt(2);
            incident += a[p].Magnitude * a[p].Magnitude;
            reflected += b[p].Magnitude * b[p].Magnitude;
        }
        double accepted = incident - reflected;
        var drive = new SParameterEngine.BlockPortDrive(1, z0s.Count > 0 ? z0s[0] : 50, Complex.Zero, [], []);
        Complex gamma = new(incident > 0 ? Math.Sqrt(Math.Max(0, reflected / incident)) : 0, 0);
        return new CosimWaves(a, b, drive, incident, accepted, gamma, fa.PerAvailable ? incident : accepted, fa.PerAvailable)
        {
            ReferenceVoltage = 1 / Math.Sqrt(2),
        };
    }

    /// <summary>
    /// The current into each of the block's ports with each of its nodes held at <paramref name="voltage"/>: the block stamped alone at
    /// <paramref name="omega"/> with an ideal source on every node it touches, so the current each source delivers is what the block draws there.
    /// Every other node of the netlist, which the block does not touch, is tied to ground so the system is not singular.
    /// </summary>
    private static Complex[] BlockCurrents(ElaboratedNetlist nl, ElaboratedComponent block, double omega, Func<int, Complex> voltage)
    {
        int nodes = Math.Max(0, nl.Nodes.Count - 1);
        var mna = new MnaSystem(nodes);
        mna.Reset();
        block.Stamp(mna, omega);
        var held = block.Nodes.Append(block.ReferenceNode).Where(x => x > 0).Distinct().ToList();
        var branchOf = new Dictionary<int, int>();
        foreach (int node in held)
        {
            int br = mna.AddBranch();
            mna.AddBranchCurrent(br, node, 0);
            mna.AddConstraint(br, node, Complex.One);
            mna.AddSourceValue(br, voltage(node));
            branchOf[node] = br;
        }
        var heldSet = held.ToHashSet();
        for (int node = 1; node <= nodes; node++)
            if (!heldSet.Contains(node)) mna.AddAdmittance(node, 0, Complex.One);
        var lu = mna.Factorize();
        var rhs = mna.BuildRhs();
        var x = new Complex[mna.Size];
        lu.Solve(rhs, x);
        // KCL at a held node: what the block draws there plus what leaves into the source is zero.
        int n = block.Model switch { PhasedArrayModel pa => pa.PortCount, SnpModel s => s.PortCount, _ => block.Nodes.Length };
        var current = new Complex[n];
        for (int p = 0; p < n; p++)
            current[p] = block.Nodes[p] > 0 ? -x[branchOf[block.Nodes[p]]] : Complex.Zero;
        return current;
    }

    /// <summary>An EM Field Data block's pattern and array group from HB's waves at one of its record's frequencies.</summary>
    private static DataSet RunBlockFromHb(FieldsAnalysis fa, ElaboratedNetlist nl, PortFarFieldSet far, int i, HbWaveSource source, List<string> notes,
                                          RunControl? control, Action<IFieldMetrics>? metrics, string? sweepPoint)
    {
        var data = new DataSet();
        var lines = new List<string>();
        var warnings = new List<string>();
        var pattern = PublishFarField(fa, nl, far, [i], [(1, Complex.One)], data, lines, warnings, control, FieldsOutput.Written, out _, source);
        double[] freqs = [far.FrequenciesHz[i]];
        AddElementCubes(data, freqs, [source.Waves], far.PortNumbers, null);
        if (CutsNotApplied(fa) is { } notCut) lines.Add(notCut);
        Interlocked.Increment(ref _runs);
        Report(fa, nl, notes, lines, warnings, sweepPoint, source);
        metrics?.Invoke(new HbBlockMetrics(fa, pattern, source.Waves));
        return data;
    }

    /// <summary>
    /// The input power the far field's dBm scalars (TrpDbm, PeakEirpDbm, and ReferenceInputPowerDbm itself) are stated at for
    /// <paramref name="w"/>. A harmonic-balance solution's waves are HB's own watts and its pattern the field at HB's own drive (R-paa6-1),
    /// so its reference is the power incident on the block, Σ|A|², which is what its realized gain is per watt of: TRP is then the watts
    /// radiated and EIRP the gain times the watts accepted. Every other run keeps the record's reference (brief-paa-12).
    /// </summary>
    internal static double ReferenceDbm(PortFarFieldSet far, CosimWaves w)
        => w.ReferenceVoltage is not null && w.AvailableW > 0 ? 10 * Math.Log10(w.AvailableW * 1e3) : far.ReferenceInputPowerDbm;

    /// <summary>What a run from HB's waves says: per sweep, not per point, when a sweep wraps it.</summary>
    private static void Report(FieldsAnalysis fa, ElaboratedNetlist nl, List<string> notes, List<string> lines, List<string> warnings, string? sweepPoint, HbWaveSource source)
    {
        if (sweepPoint is not null)
        {
            nl.AddNoteOnce($"fields:{fa.Name}:hb-sweep", lines[0]);
            foreach (string line in lines.Skip(1)) nl.AddNoteOnce($"fields:{fa.Name}:{line}", line);
            foreach (string warning in warnings) nl.AddWarningOnce($"fields:{fa.Name}:{sweepPoint}:{warning}", $"At {sweepPoint}: {warning}");
            return;
        }
        for (int l = 0; l < lines.Count; l++) nl.AddNoteOnce($"fields:{fa.Name}:{l}", lines[l]);
        foreach (string warning in warnings) nl.AddWarning(warning);
        notes.Add($"Fields '{fa.Name}' at {MaterialDispersion.Hz(source.Harmonic * source.F0)}: {G(source.Waves.AcceptedW)} W delivered into '{fa.Block}' by {source.Analysis}");
    }

    /// <summary>The sentence that opens a run from HB's waves.</summary>
    private static string HbHeadline(FieldsAnalysis fa, HbWaveSource source, int ports)
    {
        var w = source.Waves;
        string harmonic = source.Harmonic == 1 ? "the fundamental" : $"harmonic {source.Harmonic}";
        return $"Fields '{fa.Name}' at {MaterialDispersion.Hz(source.Harmonic * source.F0)}: the pattern of '{fa.Block}' from harmonic-balance analysis '{source.Analysis}' at {harmonic}, " +
               $"its {ports} ports' waves formed from the voltages that analysis solved and the block's own S, so each amplifier's compression and load are in it. " +
               $"{G(w.AcceptedW)} W is delivered into the block of {G(w.AvailableW)} W incident on it. The pattern is the field at that drive; gain is per watt delivered " +
               "and realized gain per watt incident on the block.";
    }

    /// <summary>
    /// R-paa6-3 — the array group over the element axis: a, b (per watt as the waves' cubes are), the active reflection b/a with a the wave from the
    /// circuit into the block, the power each element accepts per watt, and, from HB, the power delivered into each in watts. An element with no
    /// incident wave, or switched off, has a NaN reflection.
    /// </summary>
    private static void AddElementCubes(DataSet data, double[] freqs, CosimWaves[] waves, IReadOnlyList<int> elements, Func<int, bool>? isOn)
    {
        int n = elements.Count, nf = freqs.Length;
        var element = new Axis("element", [.. elements.Select(e => (double)e)], "");
        var f = new Axis("f", (double[])freqs.Clone(), "Hz");
        var a = new Complex[nf * n];
        var b = new Complex[nf * n];
        var gamma = new Complex[nf * n];
        var pacc = new double[nf * n];
        var pdel = new double[nf * n];
        for (int i = 0; i < nf; i++)
        {
            double scale = Math.Sqrt(waves[i].DivisorW);
            for (int e = 0; e < n; e++)
            {
                Complex ai = waves[i].A[e], bi = waves[i].B[e];
                a[i * n + e] = ai / scale;
                b[i * n + e] = bi / scale;
                gamma[i * n + e] = (isOn?.Invoke(e) ?? true) && ai != Complex.Zero ? bi / ai : new Complex(double.NaN, double.NaN);
                pacc[i * n + e] = 0.5 * (ai.Magnitude * ai.Magnitude - bi.Magnitude * bi.Magnitude) / waves[i].DivisorW;
                pdel[i * n + e] = ai.Magnitude * ai.Magnitude - bi.Magnitude * bi.Magnitude;
            }
        }
        data.Add($"{ArrayGroup}.{IncidentCube}", new DataCube([f, element], a) { Unit = "sqrt(W)" });
        data.Add($"{ArrayGroup}.{ReflectedCube}", new DataCube([f, element], b) { Unit = "sqrt(W)" });
        data.Add($"{ArrayGroup}.{ActiveReflectionCube}", new DataCube([f, element], gamma));
        data.Add($"{ArrayGroup}.{ElementAcceptedCube}", new DataCube([f, element], pacc) { Unit = "W" });
        if (waves.All(w => w.ReferenceVoltage is not null))
            data.Add($"{ArrayGroup}.{ElementDeliveredCube}", new DataCube([f, element], pdel) { Unit = "W" });
    }

    /// <summary>The active reflection of element <paramref name="n"/> (1-based) in <paramref name="w"/>, or the refusal.</summary>
    internal static Complex ActiveGammaOf(FieldsAnalysis fa, CosimWaves w, int n, Func<int, bool>? isOn = null)
    {
        if (n < 1 || n > w.A.Length)
            throw new ExpressionException($"ActiveGamma({fa.Name}, {n}): '{fa.Block}' has elements 1 to {w.A.Length}.");
        if (isOn is not null && !isOn(n - 1))
            throw new ExpressionException($"ActiveGamma({fa.Name}, {n}): element {n} is switched off, so it has no active reflection.");
        if (w.A[n - 1] == Complex.Zero)
            throw new ExpressionException($"ActiveGamma({fa.Name}, {n}): no wave reaches element {n} from the circuit, so it has no active reflection.");
        return w.B[n - 1] / w.A[n - 1];
    }

    /// <summary>The worst active VSWR over the elements a wave reaches.</summary>
    internal static double ActiveVswrMaxOf(CosimWaves w, Func<int, bool>? isOn = null)
    {
        double worst = 0;
        for (int e = 0; e < w.A.Length; e++)
            if ((isOn?.Invoke(e) ?? true) && w.A[e] != Complex.Zero) worst = Math.Max(worst, (w.B[e] / w.A[e]).Magnitude);
        return worst >= 1 ? double.PositiveInfinity : (1 + worst) / (1 - worst);
    }

    /// <summary>What a measurement reads of an EM Field Data block's run from HB: the pattern's metrics, and each element's active reflection.</summary>
    private sealed class HbBlockMetrics(FieldsAnalysis fa, CosimPatternMetrics? pattern, CosimWaves w) : IFieldMetrics
    {
        public double Evaluate(FieldMetric metric, string? region, double? massGrams)
            => throw new ExpressionException($"'{FieldMetricFunctions.NameOf(metric)}' reads a field set, and fields analysis '{fa.Name}' forms a far field from harmonic balance.");

        public double EvaluatePattern(FieldMetric metric, double? thetaDeg, double? phiDeg)
            => pattern?.Evaluate(metric, thetaDeg, phiDeg) ?? throw new ExpressionException($"{metric}({fa.Name}) has no pattern to read.");

        public double EvaluateArray(FieldMetric metric, string? plane, double? theta0Deg, double? phi0Deg)
            => metric == FieldMetric.ActiveVswrMax
                ? ActiveVswrMaxOf(w)
                : throw new ExpressionException($"'{FieldMetricFunctions.NameOf(metric)}' reads the beam of a {PhasedArrayModel.DisplayName}, and '{fa.Block}' is an {EmFieldDataRecord.DisplayName} block.");

        public Complex ActiveGamma(int element) => ActiveGammaOf(fa, w, element);
    }
}

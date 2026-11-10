// A chain, transferred: test benches of circuitRF's own System blocks, each drawn by NetlistSchematic (the drawing
// `circuitrf netlist --to-schematic` makes), with the analyses that verify the budget already set up and the budget's
// own numbers beside the simulated ones in a MEAS block. Three for every chain, and two more for an array:
//
//   <chain> 1-Tone    a P1Tone per element, a Port at the output. Without a mixer, an S-parameter run with Noise=yes:
//                     gain, NF and each stage's share of it. With one, a harmonic-balance noise run with the LO: the
//                     conversion gain and the SSB NF. And a harmonic-balance drive sweep for the 1 dB compression point.
//   <chain> 2-Tone    a PnTone per element: two tones 50 dB below the predicted IIP3, the intercept and each stage's.
//   <chain> Envelope  a MOD_Source per element, an NR carrier with its source's noise: the in-channel SNR, the EVM's
//                     noise part and the output noise floor.
//   <chain> Steering  the elements and the combiner fed by a plane wave: one Port through a Combiner used as a
//                     splitter whose path k has the wave's phase −(k − 1)·ψ_in, ψ = 360°·(d/λ)·sin θ. The phase
//                     shifters steer from VAR theta_steer, a taper from VAR taper; a sweep of theta_in inside a sweep
//                     of theta_steer gives the array pattern, set beside the closed-form array factor.
//   <chain> Combiners the same elements into each of the four combiners — direct and tree, passive and active — side by
//                     side, each fed by its own wave at broadside: gain and NF from one S-parameter run.
//
// The chain's own parts are placed as written. Three things are added, each the bench's and none the chain's:
//   - the sources, the load and each mixer's LO (a P1Tone at Flo and Plo);
//   - an ISOLATOR in front of each mixer's RF port — a Circulator with its third port terminated at the ambient. A
//     mixer's SSB noise figure is defined with its image terminated at T0, and the budget's Friis sum assumes exactly
//     that; a filter in front of the mixer reflects the image instead, which changes the mixer's noise (the 2.45 GHz
//     superhet example reads 0.28 dB worse without it). Delete it to see what the image termination does;
//   - in an array, a TLIN in front of element k of (k − 1)·Steer: the arriving wave's extra path, which each branch's
//     phase shifter is set to remove.
//
// Every measurement row reads ONE analysis, so a CLI run of one analysis (`circuitrf hb … -a SW1`) still evaluates its
// own rows. `temp = 16.85` puts every part at 290 K, the temperature a noise figure is stated at.

using System.Globalization;
using System.Text;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Symbol;

namespace CircuitRF.Design.Chain;

public enum ChainBench { OneTone, TwoTone, Envelope, Steering, Combiners }

/// <summary>One bench: its cell name, its netlist and where each of its instances is drawn.</summary>
public sealed record ChainBenchText(ChainBench Kind, string CellName, string Netlist, ExactPlacement Layout);

/// <summary>What a transfer wrote, or why it wrote nothing.</summary>
/// <summary>The chain drawn as a schematic, and the stage each drawn component (by its id) belongs to.</summary>
public sealed record ChainDiagram(SchematicEditModel Model, IReadOnlyDictionary<string, int> StageOfComponent);

public sealed record ChainTransferResult(IReadOnlyList<string> SchematicPaths, IReadOnlyList<string> Refusals,
                                         IReadOnlyList<string> Notes);

public static class ChainBenches
{
    public static string CellName(string chain, ChainBench b) => b switch
    {
        ChainBench.OneTone => chain + " 1-Tone",
        ChainBench.TwoTone => chain + " 2-Tone",
        ChainBench.Envelope => chain + " Envelope",
        ChainBench.Steering => chain + " Steering",
        _                  => chain + " Combiners",
    };

    /// <summary>The steer angles the Steering bench sweeps, degrees from broadside.</summary>
    public static readonly double[] SteerAnglesDeg = [-30, 0, 30];

    /// <summary>The four combiners the Combiners bench sets side by side: topology, kind, and the suffix of its parts.</summary>
    public static readonly (string Topology, string Kind, string Code)[] CombinerVariants =
        [("Direct", "Passive", "DP"), ("Tree", "Passive", "TP"), ("Direct", "Active", "DA"), ("Tree", "Active", "TA")];

    /// <summary>
    /// The benches a chain transfers to: 1-Tone, 2-Tone and Envelope always; for an array whose branch is linear (no
    /// mixer before the combiner, so an S-parameter run reaches the combiner), Combiners, and Steering when a phase
    /// shifter in the branch has something to steer with. <paramref name="why"/> says what was left out and why.
    /// </summary>
    public static IReadOnlyList<ChainBench> BenchesFor(ChainDesign d, List<string>? why = null)
    {
        List<ChainBench> b = [ChainBench.OneTone, ChainBench.TwoTone, ChainBench.Envelope];
        int cmb = d.Stages.FindIndex(s => s.Part == ChainParts.Combiner);
        if (cmb < 0) return b;
        if (d.Stages.Take(cmb).Any(s => s.Part == ChainParts.Mixer))
        {
            why?.Add("The branch before the combiner has a mixer, so no S-parameter run reaches the combiner: there is no " +
                     "Steering or Combiners bench.");
            return b;
        }
        if (d.Stages.Take(cmb).Any(s => s.Part == ChainParts.PhaseShifter)) b.Add(ChainBench.Steering);
        else why?.Add("The branch has no PhaseShifter to steer with, so there is no Steering bench.");
        b.Add(ChainBench.Combiners);
        return b;
    }

    /// <summary>The chain up to and including its combiner, with the combiner's topology and kind set: what one
    /// sub-circuit of the Combiners bench is budgeted as. A direct combiner made from a tree's per-level lists takes
    /// their first level's values; a tree made from single values takes them at every level.</summary>
    public static ChainDesign Variant(ChainDesign d, string topology, string kind)
    {
        var v = d.Clone();
        int cmb = v.Stages.FindIndex(s => s.Part == ChainParts.Combiner);
        v.Stages.RemoveRange(cmb + 1, v.Stages.Count - cmb - 1);
        var p = v.Stages[cmb].Parameters;
        p["Topology"] = topology;
        p["Kind"] = kind;
        if (kind == "Active") p.Remove("PathIL");
        if (topology == "Direct")
            foreach (string key in new[] { "IL", "Gain", "NF", "IIP3" })
                if (p.TryGetValue(key, out var text) && text.Split([',', ';']) is { Length: > 1 } items) p[key] = items[0].Trim();
        return v;
    }

    /// <summary>The level of the two tones below the predicted IIP3, dB: far enough down that tanh's fifth-order term
    /// moves the intercept by under 0.001 dB.</summary>
    public const double TwoToneBackoffDb = 50;

    /// <summary>The small-signal point of the compression sweep, below the predicted input P1dB, dB.</summary>
    public const double CompressionBackoffDb = 40;

    /// <summary>The compression sweep's two points either side of the predicted input P1dB, dB.</summary>
    public const double CompressionStepDb = 0.25;

    /// <summary>
    /// The NR carrier the envelope bench sends: the 15 kHz-spacing channel whose occupied bandwidth (resource blocks ×
    /// 180 kHz) is nearest the chain's bandwidth, and that occupied bandwidth, which is the one its SNR is measured in.
    /// </summary>
    public static (int ChannelMHz, double OccupiedHz) Carrier(double bandwidthHz)
    {
        (int Mhz, int Rb)[] nr = [(5, 25), (10, 52), (15, 79), (20, 106), (25, 133), (30, 160), (40, 216), (50, 270)];
        var best = nr.MinBy(c => Math.Abs(Math.Log(c.Rb * 180e3 / bandwidthHz)));
        return (best.Mhz, best.Rb * 180e3);
    }

    /// <summary>The chain's benches (<see cref="BenchesFor"/>), or none when the budget is refused.</summary>
    public static IReadOnlyList<ChainBenchText> Build(ChainDesign d, ChainBudgetResult r, string chain)
        => r.Totals is null ? [] : [.. BenchesFor(d).Select(b => new Emitter(d, r, chain, b).Emit())];

    /// <summary>
    /// Draws the chain's benches and writes each as a cell under <paramref name="workspaceDir"/>, the way a cell from any
    /// generator is placed: each is written first into a staging folder and read back through
    /// <see cref="CellViewFileValidator"/>, a history checkpoint is taken when the workspace keeps one, and only then
    /// is each written in with <see cref="CellCreate.Create"/>, the function New Cell calls. Writes nothing when any
    /// bench is refused or does not validate, when the checkpoint fails, or, unless <paramref name="replace"/>, when one
    /// of the cells already exists.
    /// </summary>
    public static ChainTransferResult Write(ChainDesign d, string workspaceDir, string chain, bool replace = false)
    {
        var r = ChainBudget.Evaluate(d);
        if (r.Totals is null) return new([], r.Refusals, []);
        if (NameValidator.Validate(chain) is { } bad) return new([], [$"'{chain}' cannot name a cell: {bad}"], []);

        var benches = Build(d, r, chain);
        var refusals = new List<string>();
        var notes = new List<string>();
        var drawn = new List<(string Cell, SchematicEditModel Model)>();
        foreach (var b in benches)
        {
            string cellDir = Path.Combine(workspaceDir, b.CellName);
            if (!replace && Directory.Exists(cellDir))
            {
                refusals.Add($"The cell '{b.CellName}' already exists. Replace it, or transfer under another name.");
                continue;
            }
            var (lib, tb) = new CnlReader().Read(b.Netlist, "tb", workspaceDir);
            var res = NetlistSchematic.Build(lib, tb, Path.Combine(cellDir, CellFolder.SchematicSubFolder), exact: b.Layout);
            if (res.Schematic is null) refusals.AddRange(res.Refusals.Select(x => $"{b.CellName}: {x}"));
            else
            {
                drawn.Add((b.CellName, res.Schematic));
                notes.AddRange(res.Notes.Select(x => $"{b.CellName}: {x}"));
            }
        }
        if (refusals.Count > 0) return new([], refusals, notes);

        // Staged and read back before anything reaches the workspace.
        string staging = Path.Combine(Path.GetTempPath(), "crf-chain-" + Guid.NewGuid().ToString("N")[..12]);
        try
        {
            foreach (var (cell, model) in drawn)
                if (CellViewFileValidator.DescribeDefect(CellCreate.Create(staging, cell, CellViews.Schematic, model).SchematicPath!,
                                                         ViewType.Schematic) is { } defect)
                    refusals.Add($"{cell}: {defect}");
        }
        finally { try { Directory.Delete(staging, recursive: true); } catch (IOException) { } }
        if (refusals.Count > 0) return new([], refusals, notes);

        if (Revision.WorkspaceCheckpoints.BeforeWrite(Path.Combine(workspaceDir, chain + ChainDesignIo.Extension),
                $"Before Transfer to Schematic of {chain}", Revision.CheckpointOrigin.SavePoint) is { } failed)
            return new([], [failed.Render()], notes);

        var paths = new List<string>();
        foreach (var (cell, model) in drawn)
            paths.Add(CellCreate.Create(workspaceDir, cell, CellViews.Schematic, model).SchematicPath!);
        var left = new List<string>();
        BenchesFor(d, left);
        return new(paths, [], [.. r.Notes, .. notes, .. left]);
    }

    /// <summary>
    /// The chain as a drawing: its parts exactly as the benches place them, between a Port per element and a Port at
    /// the output, with each mixer's LO, drawn by <see cref="NetlistSchematic.Build"/> as every bench is. Over each stage
    /// on the first element's row, four lines of text: the stage's own gain, NF, intercept and compression point, and
    /// the cascade's gain, NF, IIP3 and SNR at its output. Null when the budget is refused or the drawing is.
    /// </summary>
    public static ChainDiagram? Diagram(ChainDesign d, ChainBudgetResult r)
    {
        if (r.Totals is null) return null;
        var e = new Emitter(d, r, "chain", ChainBench.OneTone, diagram: true);
        var text = e.Emit();
        var (lib, tb) = new CnlReader().Read(text.Netlist, "tb", null);
        var drawn = NetlistSchematic.Build(lib, tb, null, exact: text.Layout);
        if (drawn.Schematic is not { } model) return null;

        static string D(double v) => double.IsFinite(v) ? v.ToString("0.0#", CultureInfo.InvariantCulture) : "—";
        double h = EditableText.DefaultFontSize * 1.3;
        foreach (var (stage, (x, y)) in e.Anchor)
        {
            // Four short lines, bottom up, so a note stays within its own block's width: the stage on its own, then
            // the cascade at its output (Σ).
            var s = r.Stages[stage];
            string[] lines =
            [
                $"G {D(s.Stage.GainDb)}  NF {D(s.Stage.NfDb)}",
                double.IsFinite(s.Stage.Iip3Dbm) ? $"IIP3 {D(s.Stage.Iip3Dbm)}  P1 {D(s.Stage.Ip1dbDbm)}" : "linear",
                $"Σ G {D(s.GainDb)}  NF {D(s.NfDb)}",
                $"Σ IIP3 {D(s.Iip3Dbm)}  SNR {D(s.SnrDb)}",
            ];
            for (int i = 0; i < lines.Length; i++)
                model.CanvasObjects.Add(Note(x, y - 260 - (lines.Length - 1 - i) * h, lines[i],
                                             i < 2 ? SymbolFontStyle.Bold : SymbolFontStyle.Regular));
        }
        var stageOf = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var c in model.Components)
            if (e.StageOf.TryGetValue(c.InstanceName, out int st)) stageOf[c.Id] = st;
        return new ChainDiagram(model, stageOf);
    }

    private static EditableText Note(double x, double y, string text, SymbolFontStyle style) => new()
    {
        X = x, Y = y, Text = text, FontStyle = style,
        Align = SymbolTextAlign.Center, VAlign = SymbolTextVAlign.Bottom,
    };

    // ── the netlist ──────────────────────────────────────────────────────────

    private sealed class Emitter(ChainDesign d, ChainBudgetResult r, string chain, ChainBench bench, bool diagram = false)
    {
        /// <summary>The stage each placed instance belongs to, and where each stage's first block sits on the first
        /// element's row — what the diagram's selection and annotations are read from.</summary>
        public readonly Dictionary<string, int> StageOf = new(StringComparer.Ordinal);
        public readonly Dictionary<int, (double X, double Y)> Anchor = [];
        private int _stage = -1;

        private const double Lead = 500, BranchPitch = 1400;
        private readonly StringBuilder _parts = new();
        private readonly List<string> _vars = ["temp = 16.85"], _analyses = [], _meas = [];
        private readonly Dictionary<string, ExactInstance> _at = new(StringComparer.Ordinal);
        private readonly ChainTotals _t = r.Totals!;
        private double _x, _y, _yBase;

        private static string F(double v) => ChainValue.Format(v);
        private static string G(double v) => v.ToString("R", CultureInfo.InvariantCulture);

        private int N => d.Stages.FirstOrDefault(s => s.Part == ChainParts.Combiner) is { } c
                         ? Math.Max(2, (int)Math.Round(ChainValue.Or(c.Get("N"), 2))) : 1;
        private int Branch => d.Stages.FindIndex(s => s.Part == ChainParts.Combiner) is >= 0 and var i ? i : 0;
        private double Steer => d.Stages.FirstOrDefault(s => s.Part == ChainParts.Combiner) is { } c ? ChainValue.Or(c.Get("Steer"), 0) : 0;
        private int Mixers => d.Stages.Count(s => s.Part == ChainParts.Mixer);

        private void Line(string text) => _parts.AppendLine(text);
        private void Place(string inst, double x, double y, bool mirror = false)
        {
            _at[inst] = new ExactInstance(x, y, SymbolRotation.R0, mirror);
            if (_stage < 0) return;
            StageOf[inst] = _stage;
            if (!Anchor.ContainsKey(_stage)) Anchor[_stage] = (x, y);
        }
        private void Inline(string inst, double half) { _x += half; Place(inst, _x, _y); _x += half + Lead; }

        /// <summary>A parameter as its netlist text: a bare number gets the parameter's unit, as a schematic writes it.</summary>
        private static string Param(ChainParam p, string text)
        {
            text = text.Trim();
            bool bare = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
            return bare && p.Unit is "dB" or "dBm" or "deg" ? $"{text} {p.Unit}" : text;
        }

        /// <summary>The stage's own parameters on its line: every one the part lists that is not blank, and every other
        /// one the stage carries, as written.</summary>
        private static string Params(ChainStage s, params string[] skip)
        {
            var part = ChainParts.Find(s.Part)!;
            var sb = new StringBuilder();
            foreach (var p in part.Parameters.Where(p => !skip.Contains(p.Name)))
                if (s.Get(p.Name).Trim() is { Length: > 0 } v) sb.Append($"  {p.Name}={Param(p, v)}");
            foreach (var (k, v) in s.Parameters.Where(kv => part.Parameters.All(p => p.Name != kv.Key) && !skip.Contains(kv.Key)))
                sb.Append($"  {k}={v.Trim()}");
            return sb.ToString();
        }

        private static string Fmt(double v, string unit) => $"{F(v)} {unit}";

        public ChainBenchText Emit()
        {
            if (bench == ChainBench.Steering) return EmitSteering();
            if (bench == ChainBench.Combiners) return EmitCombiners();
            int n = N, nb = Branch;
            bool array = n > 1;
            double frf = d.FrequencyHz;
            _vars.Add($"Frf = {G(frf)}");
            if (bench == ChainBench.TwoTone)
            {
                double df = Math.Min(1e6, d.BandwidthHz);
                _vars.Add($"F1 = {G(frf - df / 2)}");
                _vars.Add($"F2 = {G(frf + df / 2)}");
                _vars.Add($"P2t = {F(Finite(_t.Iip3Dbm, 0) - TwoToneBackoffDb)}");
            }
            else if (bench == ChainBench.OneTone)
            {
                // Pss is the small-signal drive and Pin the source's: the compression sweep moves Pin, and a row that
                // read Pin would read the sweep's axis, not the drive the noise run was made at.
                _vars.Add($"Pss = {F(Finite(_t.Ip1dbDbm, 0) - CompressionBackoffDb)}");
                _vars.Add("Pin = Pss");
            }
            else _vars.Add($"Pin = {F(d.InputPowerDbm)}");

            // Frequency and mixing-product tracking: v1 is the wanted product's index on the tone lattice and v3 the
            // lower IM3's, both updated by every mixer the signal passes.
            int tones = (bench == ChainBench.TwoTone ? 2 : 1) + Mixers;
            var v1 = new int[tones]; v1[0] = 1;
            var v3 = new int[tones];
            if (bench == ChainBench.TwoTone) { v3[0] = 2; v3[1] = -1; }
            int lo = bench == ChainBench.TwoTone ? 2 : 1;
            var stageNet = new string[d.Stages.Count];
            var stageIdx = new (int[] V1, int[] V3)[d.Stages.Count];
            var stageParts = new List<string>[d.Stages.Count];
            var stageFreq = new double[d.Stages.Count];
            double f = frf;

            // ── each element's branch, one row per element ──
            var branchOut = new string[n];
            double rowX = 0;
            int[] v1b = [], v3b = [];
            double fb = f;
            for (int k = 1; k <= n; k++)
            {
                _x = 0; _y = (k - 1) * BranchPitch;
                string src = array ? $"in{k}" : "in";
                Source(k, src);
                string prev = src;
                double path = array ? Mod360((k - 1) * Steer) : 0;
                if (path > 0)
                {
                    string a = $"a{k}";
                    Line($"TLIN:PATH{k}  {src}  {a}  Z=50 Ohm  E={F(path)} deg  F={G(frf)} Hz");
                    Inline($"PATH{k}", 200);
                    prev = a;
                }
                else if (array && Steer != 0) _x += 400 + Lead;
                var bv1 = (int[])v1.Clone(); var bv3 = (int[])v3.Clone(); double bf = f; int blo = lo;
                for (int j = 0; j < nb; j++)
                {
                    string net = $"b{k}_{d.Stages[j].Name}";
                    _stage = j;
                    var parts = Stage(d.Stages[j], d.Stages[j].Name + (array ? $"_{k}" : ""), prev, net, ref bf, bv1, bv3, ref blo, k, n);
                    if (k == 1) { stageNet[j] = net; stageIdx[j] = ((int[])bv1.Clone(), (int[])bv3.Clone()); stageFreq[j] = bf; stageParts[j] = []; }
                    stageParts[j].AddRange(parts);
                    prev = net;
                    _stage = -1;
                }
                branchOut[k - 1] = prev;
                rowX = Math.Max(rowX, _x);
                if (k == n) { v1b = bv1; v3b = bv3; fb = bf; lo = blo; }
            }
            v1 = v1b; v3 = v3b; f = fb;

            // ── the combiner, and the rest of the chain on its output's row ──
            _y = 0;
            _x = rowX;
            string cur = branchOut[0];
            for (int j = nb; j < d.Stages.Count; j++)
            {
                var s = d.Stages[j];
                string net = j == d.Stages.Count - 1 ? "out" : $"n_{s.Name}";
                List<string> parts;
                _stage = j;
                if (s.Part == ChainParts.Combiner) parts = Combiner(s, branchOut, net);
                else parts = Stage(s, s.Name, cur, net, ref f, v1, v3, ref lo, 1, 1);
                stageNet[j] = net; stageIdx[j] = ((int[])v1.Clone(), (int[])v3.Clone()); stageFreq[j] = f; stageParts[j] = parts;
                cur = net;
                _stage = -1;
            }
            if (cur != "out") { /* the last stage always writes "out" */ }
            Load(n);
            if (!diagram) Measurements(stageNet, stageIdx, stageParts, stageFreq, v1, v3, f, n);
            else _vars.Clear();
            return Text();
        }

        private ChainBenchText Text()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"; {CellName(chain, bench)}: transferred from the chain budget '{chain}'");
            sb.AppendLine();
            foreach (var v in _vars) sb.AppendLine(v);
            sb.AppendLine();
            sb.Append(_parts);
            sb.AppendLine();
            foreach (var a in _analyses) sb.AppendLine(a);
            foreach (var m in _meas) sb.AppendLine("measure " + m);
            return new ChainBenchText(bench, CellName(chain, bench), sb.ToString(),
                                      new ExactPlacement { Instances = _at, AutoRoute = true });
        }

        // ── the array benches ────────────────────────────────────────────────

        /// <summary>e, so that e^(j·x) is a measurement's cos x + j·sin x over a sweep's cube (a power broadcasts over a
        /// cube where sin and cos take a number).</summary>
        private const string Euler = "2.718281828459045";

        /// <summary>The VAR taper's share at element k of n, as text to add to its path's loss: the full taper at the two
        /// edges, the others a share in proportion to their distance from the centre; "" where it is none.</summary>
        private static string Edge(int k, int n)
        {
            int m = Math.Abs(2 * k - n - 1);
            return n < 2 || m == 0 ? "" : m == n - 1 ? "+taper" : $"+taper*{m}/{n - 1}";
        }

        /// <summary>A sweep value as a name: −30 → m30, 30 → p30.</summary>
        private static string AngleName(double deg) => deg < 0 ? $"m{F(-deg)}" : deg > 0 ? $"p{F(deg)}" : "0";

        /// <summary>
        /// One element per row from a wave's split, each element's branch placed with <paramref name="suffix"/> on its
        /// names; returns the branch outputs. The wave is a Port into a Combiner used as a splitter, its path k carrying
        /// <paramref name="phase"/>(k) (null: none).
        /// </summary>
        private string[] WaveAndBranches(string port, int num, string suffix, Func<int, string?> phase)
        {
            int n = N, nb = Branch;
            double yc = _yBase + (n - 1) * BranchPitch / 2;
            string w = $"w{suffix}";
            Line($"Port:{port}  {w}  0  Num={num}  Z=50 Ohm");
            Place(port, 0, yc + 200);
            var ph = Enumerable.Range(1, n).Select(k => phase(k) is { } p ? $"  Phase[{k}]={p} deg" : "");
            Line($"Combiner:WAVE{suffix}  {w}  0  {string.Join("  ", Enumerable.Range(1, n).Select(k => $"in{k}{suffix}  0"))}  N={n}" +
                 $"{string.Concat(ph)}  Z0=50 Ohm");
            Place($"WAVE{suffix}", 900, yc, mirror: true);
            var outs = new string[n];
            double rowX = 0;
            for (int k = 1; k <= n; k++)
            {
                _x = 1900; _y = _yBase + (k - 1) * BranchPitch;
                string prev = $"in{k}{suffix}";
                double fb = d.FrequencyHz; int lo = 1; int[] v1 = [1], v3 = [0];
                for (int j = 0; j < nb; j++)
                {
                    string net = $"b{k}_{d.Stages[j].Name}{suffix}";
                    _stage = j;
                    Stage(d.Stages[j], $"{d.Stages[j].Name}_{k}{suffix}", prev, net, ref fb, v1, v3, ref lo, k, n);
                    prev = net;
                    _stage = -1;
                }
                outs[k - 1] = prev;
                rowX = Math.Max(rowX, _x);
            }
            _x = rowX;
            return outs;
        }

        /// <summary>
        /// The Steering bench: a plane wave from theta_in into the elements, steered by their phase shifters from
        /// theta_steer, combined, and read at the combiner's output; a sweep of theta_in within a sweep of theta_steer
        /// over <see cref="SteerAnglesDeg"/>. The pattern is the simulated gain over the budget's at the combiner, beside
        /// the closed-form array factor |Σ a_k·e^(−j(k−1)(ψ_in − ψ_s))| / Σ a_k, with a_k each path's weight and the VAR
        /// taper's. At each steer angle's own peak: the gain, the NF from the wave's port, and the array gain.
        /// </summary>
        private ChainBenchText EmitSteering()
        {
            int n = N, nb = Branch;
            var row = r.Stages[nb];
            var c = ChainParts.ReadCombiner(d.Stages[nb]);
            _vars.AddRange([$"Frf = {G(d.FrequencyHz)}", "theta_in = 0", "theta_steer = 0", "d_over_lambda = 0.5", "taper = 0",
                            "psi_in = 360*d_over_lambda*sin(theta_in*pi/180)",
                            "dphi = 360*d_over_lambda*sin(theta_steer*pi/180)"]);
            var outs = WaveAndBranches("P1", 1, "", k => k > 1 ? $"-{k - 1}*psi_in" : null);
            _y = 0;
            _stage = nb;
            Combiner(d.Stages[nb], outs, "out");
            _stage = -1;
            Line("Port:P2  out  0  Num=2  Z=50 Ohm");
            Place("P2", _x, _y + 200);

            _analyses.Add("analysis SP1 type=sparam start=\"Frf\" stop=\"Frf\" npts=1 Noise=yes");
            _analyses.Add("analysis SWI type=parametric_sweep Var=theta_in Start=-90 Stop=90 Step=1 Inner=SP1");
            _analyses.Add($"analysis SWS type=parametric_sweep Var=theta_steer Values={string.Join(",", SteerAnglesDeg.Select(F))} Inner=SWI");

            double nDb = 10 * Math.Log10(n);
            _meas.Add($"G_calc = {G(row.GainDb)}");
            _meas.Add($"NF_calc = {G(row.NfDb)}");
            // From the wave's port the elements see equal shares of it, so unequal paths cost their taper loss: N/A.
            _meas.Add($"NFw_calc = {G(row.NfDb + nDb - row.ArrayGainDb)}");
            _meas.Add($"A_calc = {G(row.ArrayGainDb)}");
            _meas.Add($"G_sim = at(dB(SP1.S(2,1)), \"freq\", 0) + {G(nDb)}");
            _meas.Add("NF_sim = at(SP1.NF, \"freq\", 0)");
            _meas.Add($"u_in = imag({Euler}^(j*theta_in*pi/180))");
            _meas.Add($"u_s = imag({Euler}^(j*theta_steer*pi/180))");
            _meas.Add("psd = 2*pi*d_over_lambda*(u_in - u_s)");
            string Wt(int k)
            {
                double path = c.PathIlDb.Count == n ? c.PathIlDb[k - 1] : 0;
                return Edge(k, n) is { Length: > 0 } edge ? $"10^(-({F(path)}{edge})/20)" : G(Math.Pow(10, -path / 20));
            }
            string sum = string.Join(" + ", Enumerable.Range(1, n).Select(k => k == 1 ? Wt(k) : $"{Wt(k)}*{Euler}^(-j*{k - 1}*psd)"));
            double sa0 = Enumerable.Range(1, n).Sum(k => c.Weight(k));
            _meas.Add($"AF_calc = 20*log10(mag({sum})/{G(sa0)})");
            _meas.Add("Pattern_sim = G_sim - G_calc");
            _meas.Add("Pattern_err = Pattern_sim - AF_calc");
            for (int i = 0; i < SteerAnglesDeg.Length; i++)
            {
                string a = AngleName(SteerAnglesDeg[i]);
                int idx = (int)Math.Round(SteerAnglesDeg[i] + 90);
                _meas.Add($"G_{a}_sim = at(at(G_sim, \"theta_steer\", {i}), \"theta_in\", {idx})");
                _meas.Add($"NF_{a}_sim = at(at(NF_sim, \"theta_steer\", {i}), \"theta_in\", {idx})");
                // The SNR the array gains over one element: N equal shares of the wave, less what its NF from the wave's
                // port says it costs over one element's own.
                _meas.Add($"A_{a}_sim = {G(nDb)} - NF_{a}_sim + NF_calc");
                _meas.Add($"G_{a}_err = G_{a}_sim - G_calc");
                _meas.Add($"NF_{a}_err = NF_{a}_sim - NFw_calc");
                _meas.Add($"A_{a}_err = A_{a}_sim - A_calc");
            }
            return Text();
        }

        /// <summary>
        /// The Combiners bench: the same elements into each of the four combiners (a tree only for N a power of 2), side by
        /// side, each from its own wave at broadside to its own output port, in one S-parameter run. For each: the gain
        /// referred to one element, the NF from its wave's port (the noise-wave correlation at its output over the gain
        /// from its input, every other port noiseless), and the budget's gain, NF, intercepts and array gain.
        /// </summary>
        private ChainBenchText EmitCombiners()
        {
            int n = N, nb = Branch;
            _vars.Add($"Frf = {G(d.FrequencyHz)}");
            bool pow2 = (n & (n - 1)) == 0;
            var variants = CombinerVariants.Where(v => pow2 || v.Topology == "Direct").ToList();
            double nDb = 10 * Math.Log10(n), kt0 = ChainBudget.K * ChainBudget.T0;
            for (int v = 0; v < variants.Count; v++)
            {
                var (topo, kind, code) = variants[v];
                string sfx = "_" + code;
                int pin = 2 * v + 1, pout = 2 * v + 2;
                _yBase = v * (n * BranchPitch + 1800);
                var outs = WaveAndBranches($"PI{sfx}", pin, sfx, _ => null);
                var vs = d.Stages[nb].Clone();
                vs.Parameters["Topology"] = topo;
                vs.Parameters["Kind"] = kind;
                if (kind == "Active") vs.Parameters.Remove("PathIL");
                _y = _yBase;
                Combiner(vs, outs, $"out{sfx}", sfx);
                Line($"Port:PO{sfx}  out{sfx}  0  Num={pout}  Z=50 Ohm");
                Place($"PO{sfx}", _x, _y + 200);

                var vr = ChainBudget.Evaluate(Variant(d, topo, kind));
                var row = vr.Stages[^1];
                _meas.Add($"G_{code}_calc = {G(row.GainDb)}");
                _meas.Add($"NF_{code}_calc = {G(row.NfDb + nDb - row.ArrayGainDb)}");
                _meas.Add($"IIP3_{code}_calc = {G(Finite(row.Iip3Dbm, double.NaN))}");
                _meas.Add($"OIP3_{code}_calc = {G(Finite(row.Oip3Dbm, double.NaN))}");
                _meas.Add($"A_{code}_calc = {G(row.ArrayGainDb)}");
                _meas.Add($"G_{code}_sim = dB(SP1.S[0, {pout}, {pin}]) + {G(nDb)}");
                _meas.Add($"NF_{code}_sim = 10*log10(1 + real(SP1.Cs[0, {pout}, {pout}])/({G(kt0)}*mag(SP1.S[0, {pout}, {pin}])^2))");
                _meas.Add($"G_{code}_err = G_{code}_sim - G_{code}_calc");
                _meas.Add($"NF_{code}_err = NF_{code}_sim - NF_{code}_calc");
            }
            _analyses.Add("analysis SP1 type=sparam start=\"Frf\" stop=\"Frf\" npts=1 Noise=yes");
            return Text();
        }

        private static double Finite(double v, double fallback) => double.IsFinite(v) ? v : fallback;
        private static double Mod360(double deg) { double m = deg % 360; if (m < 0) m += 360; return Math.Abs(m) < 1e-9 || Math.Abs(m - 360) < 1e-9 ? 0 : m; }

        private void Source(int k, string net)
        {
            string name = $"SRC{(N > 1 ? k.ToString(CultureInfo.InvariantCulture) : "")}";
            switch (diagram ? (ChainBench)(-1) : bench)
            {
                case (ChainBench)(-1):
                    name = N > 1 ? $"IN{k}" : "IN";
                    Line($"Port:{name}  {net}  0  Num={k}  Z=50 Ohm"); break;
                case ChainBench.OneTone:
                    Line($"P1Tone:{name}  {net}  0  Num={k}  Freq=Frf  Pavl=Pin dBm  Z=50 Ohm"); break;
                case ChainBench.TwoTone:
                    Line($"PnTone:{name}  {net}  0  Freq[1]=F1  Pavl[1]=P2t dBm  Phase[1]=0 deg  Freq[2]=F2  Pavl[2]=P2t dBm  Phase[2]=0 deg  Z=50 Ohm"); break;
                default:
                    var (mhz, _) = Carrier(d.BandwidthHz);
                    Line($"MOD_Source:{name}  {net}  0  Fc=Frf  Pavg=Pin dBm  Z=50 Ohm  " +
                         $"Carrier[1]=\"NR;BW={mhz}MHz;SCS=15kHz;Mod=64QAM;Off=0;P=0;Seed=1\"  Record=0.0005  Noise=yes  " +
                         $"Temp={F(d.TemperatureK - 273.15)}");
                    break;
            }
            Place(name, _x, _y + 200);
            _x += Lead;
        }

        private void Load(int n)
        {
            if (diagram) { Line($"Port:OUT  out  0  Num={n + 1}  Z=50 Ohm"); Place("OUT", _x, _y + 200); return; }
            switch (bench)
            {
                case ChainBench.OneTone: Line($"Port:PO  out  0  Num={n + 1}  Z=50 Ohm"); Place("PO", _x, _y + 200); break;
                case ChainBench.TwoTone: Line("R:RL  out  0  R=50 Ohm"); Place("RL", _x, _y + 200); break;
                default:
                    Line("IProbe:Iout  out  load");
                    Line("R:RL  load  0  R=50 Ohm");
                    Place("Iout", _x, _y - 100);
                    Place("RL", _x + 600, _y + 200);
                    break;
            }
        }

        /// <summary>One stage between two nets. Returns the instances it placed that a noise run lists among its shares:
        /// the lossy and the noisy ones. A lossless part's share is zero, so leaving it out changes no sum.</summary>
        private List<string> Stage(ChainStage s, string name, string a, string b, ref double f, int[] v1, int[] v3, ref int lo, int k, int n)
        {
            var placed = new List<string>();
            switch (s.Part)
            {
                case ChainParts.Amp:
                    Line($"Amp:{name}  {a}  0  {b}  0{Params(s)}"); Inline(name, 300);
                    if (ChainValue.Or(s.Get("NF"), 0) > 0) placed.Add(name);
                    break;
                case ChainParts.Atten:
                    Line($"Atten:{name}  {a}  0  {b}  0{Params(s)}  Z0=50 Ohm"); Inline(name, 300);
                    if (ChainValue.Or(s.Get("Loss"), 0) > 0) placed.Add(name);
                    break;
                case ChainParts.Filter:
                    Line($"Filter:{name}  {a}  0  {b}  0{Params(s)}"); Inline(name, 200);
                    if (ChainValue.Or(s.Get("IL"), 0) > 0) placed.Add(name);
                    break;
                case ChainParts.Switch:
                    Line($"Switch:{name}  {a}  0  {b}  0  State=1{Params(s)}"); Inline(name, 300);
                    if (ChainValue.Or(s.Get("IL"), 0) > 0) placed.Add(name);
                    break;
                case ChainParts.Circulator:
                    Line($"Circulator:{name}  {a}  0  {b}  0  {name}_t  0  Direction=CW{Params(s)}");
                    Line($"R:{name}_R  {name}_t  0  R=50 Ohm");
                    Inline(name, 300); Place($"{name}_R", _x - Lead - 300, _y + 900);
                    if (ChainValue.Or(s.Get("IL"), 0) > 0) placed.Add(name);
                    placed.Add($"{name}_R"); break;
                case ChainParts.Coupler:
                    Line($"Coupler:{name}  {a}  0  {b}  0  {name}_c  0  {name}_i  0  Phase=90 deg{Params(s)}");
                    Line($"R:{name}_Rc  {name}_c  0  R=50 Ohm");
                    Line($"R:{name}_Ri  {name}_i  0  R=50 Ohm");
                    _x += 300; Place(name, _x, _y + 100);
                    Place($"{name}_Rc", _x + 300, _y + 700); Place($"{name}_Ri", _x - 300, _y + 700);
                    _x += 300 + Lead;
                    if (ChainValue.Or(s.Get("IL"), 0) > 0) placed.Add(name);
                    placed.AddRange([$"{name}_Rc", $"{name}_Ri"]); break;
                case ChainParts.Mixer:
                {
                    double flo = ChainValue.Or(s.Get("Flo"), 0);
                    string iso = $"ISO_{name}", rf = diagram ? a : $"{a}_rf", lon = $"lo_{name}";
                    if (!diagram)
                    {
                        Line($"Circulator:{iso}  {a}  0  {rf}  0  {iso}_t  0  Direction=CW");
                        Line($"R:{iso}_R  {iso}_t  0  R=50 Ohm");
                        Inline(iso, 300); Place($"{iso}_R", _x - Lead - 300, _y + 900);
                    }
                    Line($"Mixer:{name}  {rf}  0  {lon}  0  {b}  0{Params(s, "Flo")}");
                    Line($"P1Tone:LO_{name}  {lon}  0  Freq={G(flo)}  Pavl={Param(new ChainParam("Plo", "", "dBm"), s.Get("Plo"))}  Z=50 Ohm");
                    Inline(name, 300); Place($"LO_{name}", _x - Lead - 300, _y + 900);
                    placed.Add($"{iso}_R");
                    if (ChainValue.TryParse(s.Get("NF"), out _) && r.Stages.First(x => x.Name == s.Name).Stage.NfDb > ChainParts.NoiselessMixerSsbDb + 1e-9)
                        placed.Add(name);
                    int t = lo++;
                    if (f > flo) { v1[t] -= 1; v3[t] -= 1; f -= flo; }
                    else
                    {
                        for (int i = 0; i < v1.Length; i++) { v1[i] = -v1[i]; v3[i] = -v3[i]; }
                        v1[t] += 1; v3[t] += 1; f = flo - f;
                    }
                    break;
                }
                case ChainParts.PhaseShifter:
                {
                    double il = ChainValue.Or(s.Get("IL"), 0);
                    string nf = s.Get("NF").Trim().Length == 0 ? F(il) : s.Get("NF").Trim();
                    string ip = s.Get("IIP3").Trim().Length == 0 ? "200" : s.Get("IIP3").Trim();
                    double phase = ChainValue.Or(s.Get("Phase"), 0);
                    // The branch's own phase plus what it takes to align element k with the last element's arrival; at
                    // broadside (the Combiners bench) the branch's own phase alone. The Steering bench steers from its
                    // VAR: a line of (k − 1)·(360° − Δφ) advances element k by (k − 1)·Δφ at the line's frequency, and
                    // keeps its length positive for any angle.
                    string? e = bench switch
                    {
                        ChainBench.Steering => k > 1 || Mod360(phase) > 0 ? $"{F(Mod360(phase))}+{k - 1}*(360-dphi)" : null,
                        ChainBench.Combiners => Mod360(phase) > 0 ? F(Mod360(phase)) : null,
                        _ => Mod360(phase + (n > 1 ? (n - k) * Steer : 0)) is > 0 and var ph ? F(ph) : null,
                    };
                    string mid = e is not null ? $"{b}_ps" : b;
                    Line($"Amp:{name}  {a}  0  {mid}  0  Gain={F(-il)} dB  NF={nf}  IP3={ip} dBm  IP3Ref=Input");
                    Inline(name, 300);
                    if (ChainValue.Or(nf, 0) > 0) placed.Add(name);
                    if (e is not null) { Line($"TLIN:{name}_PH  {mid}  {b}  Z=50 Ohm  E={e} deg  F={G(f)} Hz"); Inline($"{name}_PH", 200); }
                    else if (n > 1 && (Steer != 0 || bench == ChainBench.Steering)) _x += 400 + Lead;   // keep the columns aligned
                    break;
                }
                case ChainParts.Limiter:
                {
                    double il = ChainValue.Or(s.Get("IL"), 0), p1 = ChainValue.Or(s.Get("P1dB"), 10);
                    Line($"Amp:{name}  {a}  0  {b}  0  Gain={F(-il)} dB  NF={F(il)}  IP3={F(p1 + ChainParts.TanhP1dbBelowIip3)} dBm  IP3Ref=Input");
                    Inline(name, 300);
                    if (il > 0) placed.Add(name);
                    break;
                }
                case ChainParts.Splitter:
                {
                    // The library's Combiner from its common port: output 1 followed, the others terminated in Z0.
                    int ways = Math.Max(2, (int)Math.Round(ChainValue.Or(s.Get("N"), 2)));
                    var outs = Enumerable.Range(2, ways - 1).Select(w => $"{name}_o{w}").ToList();
                    Line($"Combiner:{name}  {a}  0  {b}  0  {string.Join("  ", outs.Select(o => o + "  0"))}  N={ways}  " +
                         $"IL={F(ChainValue.Or(s.Get("IL"), 0))} dB  Z0=50 Ohm");
                    _x += 300; Place(name, _x, _y + (ways - 1) * 100, mirror: true);
                    for (int w = 2; w <= ways; w++)
                    {
                        Line($"R:{name}_T{w}  {name}_o{w}  0  R=50 Ohm");
                        Place($"{name}_T{w}", _x + 700, _y + (2 * w - ways - 1) * 100 + (ways - 1) * 100 + 200);
                    }
                    _x += 300 + Lead + 400;
                    placed.Add(name);
                    break;
                }
                default:
                    throw new InvalidOperationException(s.Part);
            }
            return placed;
        }

        /// <summary>
        /// The combiner, as its topology and kind say: Direct, one N:1 library Combiner; Tree, log2(N) levels of 2:1
        /// Combiners, each level's parts fed in pairs by the level before; Active, an Amp after each combiner. Each
        /// element path's own loss is its first-level part's IL[k], and on the Steering bench that part also carries the
        /// VAR taper, toward the edges. Returns the parts a noise run lists: the lossy combiners and the noisy amplifiers.
        /// Leaves <c>_y</c> on the output's row, where the rest of the chain continues.
        /// </summary>
        private List<string> Combiner(ChainStage s, string[] branches, string b, string suffix = "")
        {
            var placed = new List<string>();
            var c = ChainParts.ReadCombiner(s);
            int n = branches.Length;
            string name = s.Name + suffix;
            var inputs = branches.Select((net, i) => (Net: net, Y: _yBase + i * BranchPitch)).ToList();
            double x0 = _x + 300, step = c.Active ? 1700 : 1000;
            double outX = x0, outY = 0;
            for (int l = 0; l < c.Levels.Count; l++)
            {
                var lv = c.Levels[l];
                var next = new List<(string Net, double Y)>();
                for (int j = 1; j <= lv.Parts; j++)
                {
                    var ins = inputs.Skip((j - 1) * lv.Ways).Take(lv.Ways).ToList();
                    string part = c.Tree ? $"{name}_{l + 1}_{j}" : name;
                    bool last = l == c.Levels.Count - 1;
                    string o = last && !c.Active ? b : c.Tree ? $"{name}_n{l + 1}_{j}" : $"{b}_c";
                    var line = new StringBuilder($"Combiner:{part}  {o}  0  {string.Join("  ", ins.Select(i => i.Net + "  0"))}  " +
                                                 $"N={lv.Ways}  IL={F(lv.IlDb)} dB");
                    bool lossy = lv.IlDb > 0;
                    if (l == 0)
                        for (int i = 1; i <= lv.Ways; i++)
                        {
                            int k = (j - 1) * lv.Ways + i;
                            double path = c.PathIlDb.Count == c.N ? c.PathIlDb[k - 1] : 0;
                            if (bench == ChainBench.Steering && Edge(k, n) is { Length: > 0 } edge)
                                line.Append($"  IL[{i}]={F(lv.IlDb + path)}{edge} dB");
                            else if (path != 0) line.Append($"  IL[{i}]={F(lv.IlDb + path)} dB");
                            lossy |= path != 0;
                        }
                    line.Append("  Z0=50 Ohm");
                    Line(line.ToString());
                    double y = ins.Average(i => i.Y), x = x0 + l * step;
                    Place(part, x, y);
                    if (lossy || bench == ChainBench.Steering) placed.Add(part);
                    string net = o;
                    if (c.Active)
                    {
                        string amp = c.Tree ? $"{name}_A{l + 1}_{j}" : $"{name}_A";
                        net = last ? b : $"{o}_a";
                        Line($"Amp:{amp}  {o}  0  {net}  0  Gain={F(lv.GainDb)} dB  NF={F(lv.NfDb)} dB  " +
                             $"IP3={F(double.IsFinite(lv.Iip3Dbm) ? lv.Iip3Dbm : 200)} dBm  IP3Ref=Input");
                        Place(amp, x + 800, y);
                        if (lv.NfDb > 0) placed.Add(amp);
                        x += 800;
                    }
                    next.Add((net, y));
                    (outX, outY) = (x, y);
                }
                inputs = next;
            }
            if (_stage >= 0) Anchor[_stage] = (outX, outY);
            _x = outX + 300 + Lead;
            _y = outY;
            return placed;
        }

        /// <summary>Σ a_k² / a_1²: how many element 1's the paths' noise is worth — N for equal paths.</summary>
        private double PathWeight()
        {
            if (d.Stages.FirstOrDefault(s => s.Part == ChainParts.Combiner) is not { } st) return 1;
            var c = ChainParts.ReadCombiner(st);
            return Enumerable.Range(1, c.N).Sum(k => c.Weight(k) * c.Weight(k)) / (c.Weight(1) * c.Weight(1));
        }

        private static string Idx(int[] v) => "\"(" + string.Join(",", v) + ")\"";

        private void Measurements(string[] nets, (int[] V1, int[] V3)[] idx, List<string>[] parts, double[] freqs,
                                  int[] v1, int[] v3, double fout, int n)
        {
            var rows = r.Stages;
            string tonesDirective(bool two)
            {
                var names = new List<string>(two ? ["F1", "F2"] : ["Frf"]);
                names.AddRange(Enumerable.Range(1, Mixers).Select(i => $"Flo{i}"));
                return names.Count == 1 ? $"Tone=\"{names[0]}\" ToneUnit=Hz"
                     : $"NumFreqs={names.Count} " + string.Join(" ", names.Select((t, i) => $"Tone[{i + 1}]=\"{t}\" ToneUnit[{i + 1}]=Hz"));
            }
            // One LO variable per mixer STAGE (an array's per-branch mixers share it), in chain order.
            var mixerStages = d.Stages.Where(s => s.Part == ChainParts.Mixer).ToList();
            for (int i = 0; i < mixerStages.Count; i++) _vars.Add($"Flo{i + 1} = {G(ChainValue.Or(mixerStages[i].Get("Flo"), 0))}");
            int mix = mixerStages.Count;
            string p1 = mix == 0 ? "1" : Idx(v1);
            // An array's NF from one element's: F = 1 + (F1 − 1)·|S(out,1)|²/Σ|S(out,k)|², the other elements' terminations
            // noiseless in the run — 1 + (F1 − 1)/N for equal paths, the paths' own weights otherwise.
            string w = G(PathWeight());
            string ka = n > 1 ? $"/{w}" : "";
            string nfArray(string f1) => n > 1 ? $"10*log10(1 + ({f1} - 1)/{w})" : $"10*log10({f1})";

            switch (bench)
            {
                case ChainBench.OneTone:
                {
                    _meas.Add($"G_calc = {G(_t.GainDb)}");
                    _meas.Add($"NF_calc = {G(_t.NfDb)}");
                    _meas.Add($"IP1dB_calc = {G(Finite(_t.Ip1dbDbm, double.NaN))}");
                    _meas.Add($"OP1dB_calc = {G(Finite(_t.Op1dbDbm, double.NaN))}");
                    string noise;
                    if (mix == 0)
                    {
                        // A two-port's run writes each part's share of F − 1 (NFc); an array's, with more ports than two, only
                        // each part's share of the noise-wave matrix (NCs), whose output entry over kT0·|S(out,1)|² is the same.
                        _analyses.Add($"analysis SP1 type=sparam start=\"{G(d.FrequencyHz)}\" stop=\"{G(d.FrequencyHz)}\" npts=1 Noise=yes" +
                                      (n > 1 ? " NoiseContrib=yes" : ""));
                        // The elements' waves add at the output in phase: the array's gain is |Σ S(out,k)|².
                        string sum = string.Join(" + ", Enumerable.Range(1, n).Select(k => $"SP1.S[0, {n + 1}, {k}]"));
                        _meas.Add($"G_sim = dB({sum})");
                        _meas.Add(n > 1 ? $"NF_sim = at({nfArray($"10^(nf(SP1.S, 0, {n + 1})/10)")}, \"freq\", 0)" : "NF_sim = at(SP1.NF, \"freq\", 0)");
                        noise = n > 1
                            ? $"real(SP1.NCs[0, \"{{0}}\", {n + 1}, {n + 1}])/({G(ChainBudget.K * ChainBudget.T0)}*mag(SP1.S[0, {n + 1}, 1])^2)"
                            : "SP1.NFc[0, \"{0}\"]";
                    }
                    else
                    {
                        double ss = fout + 1e3;   // off the tone lattice, where the noise at the IF is defined
                        var sb = Enumerable.Range(0, v1.Length).Select(i => (i == 0 ? 1 : 0) - v1[i]);
                        // Order 1 + 2·mixers keeps each mixer's image sideband, where its isolator's termination is noisy.
                        _analyses.Add($"analysis HBN type=hb {tonesDirective(false)} MaxHarm=3 MaxMixOrder={1 + 2 * mix} Tol=1e-12 " +
                                      $"SSStart=\"{G(ss)}\" SSStop=\"{G(ss)}\" SSNpts=1 Noise=yes NoiseIn=1 NoiseOut={n + 1} " +
                                      $"NoiseSideband=\"{string.Join(",", sb)}\" NoiseContrib=yes");
                        _meas.Add($"G_sim = dB(HBN.V(\"out\",{p1})) + 10 - Pss");
                        _meas.Add($"NF_sim = at({nfArray("HBN.Fn")}, \"ssfreq\", 0)");
                        noise = "HBN.NFc[0, \"{0}\"]";
                    }
                    _meas.Add("G_err = G_sim - G_calc");
                    _meas.Add("NF_err = NF_sim - NF_calc");
                    // Each stage's cumulative NF from the run's own shares of F − 1: Fc_<stage> adds that stage's parts to
                    // the stages before it, which is Friis' sum one stage at a time.
                    string prevF = "0";
                    for (int j = 0; j < rows.Count; j++)
                    {
                        string fc = $"Fc_{rows[j].Name}";
                        var terms = parts[j].Select(part => string.Format(CultureInfo.InvariantCulture, noise, part)).ToList();
                        _meas.Add($"{fc} = {string.Join(" + ", terms.Prepend(prevF).Where(x => x != "0" || terms.Count == 0))}");
                        prevF = fc;
                        _meas.Add($"NF_{rows[j].Name}_calc = {G(rows[j].NfDb)}");
                        _meas.Add($"NF_{rows[j].Name}_sim = 10*log10(1 + {fc}{ka})");
                    }
                    double lo = Finite(_t.Ip1dbDbm, 0);
                    // After a mixer an IF product is of order 2 on the lattice and its third-order term of order 6: the
                    // order the IF's compression converges at (measured: 0.04 dB off at 1 + 2·mixers, converged at 3 + 2·mixers).
                    _analyses.Add($"analysis HB1 type=hb {tonesDirective(false)} MaxHarm=3 MaxMixOrder={3 + 2 * mix} Tol=1e-12");
                    _analyses.Add($"analysis SW1 type=parametric_sweep Var=Pin Values={F(lo - CompressionBackoffDb)},{F(lo - CompressionStepDb)},{F(lo + CompressionStepDb)} Inner=HB1");
                    _meas.Add($"Ghb = dB(HB1.V(\"out\",{p1})) + 10 - Pin");
                    _meas.Add("Comp = at(Ghb,\"Pin\",0) - Ghb");
                    _meas.Add("IP1dB_sim = at(Pin,\"Pin\",1) + (1 - at(Comp,\"Pin\",1))*(at(Pin,\"Pin\",2) - at(Pin,\"Pin\",1))/(at(Comp,\"Pin\",2) - at(Comp,\"Pin\",1))");
                    _meas.Add("OP1dB_sim = IP1dB_sim + at(Ghb,\"Pin\",0) - 1");
                    _meas.Add("IP1dB_err = IP1dB_sim - IP1dB_calc");
                    _meas.Add("OP1dB_err = OP1dB_sim - OP1dB_calc");
                    break;
                }
                case ChainBench.TwoTone:
                {
                    // Each LO raises a product's order by one, so the IF's IM5 is of order 5 + mixers; below it the IF
                    // stages' IM3 reads 0.01 dB off (measured on the superhet example: 0.012 dB at 5, 1.8e-4 dB at 6).
                    _analyses.Add($"analysis HB2 type=hb {tonesDirective(true)} MaxHarm=3 MaxMixOrder={5 + mix} Tol=1e-12");
                    _meas.Add($"G_calc = {G(_t.GainDb)}");
                    _meas.Add($"IIP3_calc = {G(Finite(_t.Iip3Dbm, double.NaN))}");
                    _meas.Add($"OIP3_calc = {G(Finite(_t.Oip3Dbm, double.NaN))}");
                    _meas.Add($"P1_out = dB(HB2.V(\"out\",{Idx(v1)})) + 10");
                    _meas.Add($"P3_out = dB(HB2.V(\"out\",{Idx(v3)})) + 10");
                    _meas.Add("G_sim = P1_out - P2t");
                    _meas.Add("OIP3_sim = P1_out + (P1_out - P3_out)/2");
                    _meas.Add("IIP3_sim = OIP3_sim - G_sim");
                    _meas.Add("G_err = G_sim - G_calc");
                    _meas.Add("IIP3_err = IIP3_sim - IIP3_calc");
                    _meas.Add("OIP3_err = OIP3_sim - OIP3_calc");
                    for (int j = 0; j < rows.Count; j++)
                    {
                        string s = rows[j].Name, net = nets[j];
                        _meas.Add($"G_{s}_calc = {G(rows[j].GainDb)}");
                        _meas.Add($"OIP3_{s}_calc = {G(Finite(rows[j].Oip3Dbm, double.NaN))}");
                        _meas.Add($"P1_{s} = dB(HB2.V(\"{net}\",{Idx(idx[j].V1)})) + 10");
                        _meas.Add($"P3_{s} = dB(HB2.V(\"{net}\",{Idx(idx[j].V3)})) + 10");
                        // A branch node carries one element's signal; after the combiner the array's.
                        _meas.Add($"G_{s}_sim = P1_{s} - P2t");
                        _meas.Add($"OIP3_{s}_sim = P1_{s} + (P1_{s} - P3_{s})/2");
                    }
                    break;
                }
                default:
                {
                    var (_, bocc) = Carrier(d.BandwidthHz);
                    double te = _t.TeK, a = _t.ArrayGainDb;
                    double snr = d.InputPowerDbm + a - (10 * Math.Log10(ChainBudget.K * (d.TemperatureK + te) * bocc) + 30);
                    double nout = 10 * Math.Log10(ChainBudget.K * (d.TemperatureK + te) * bocc) + 30 + _t.GainDb - a;
                    _analyses.Add($"analysis ENV1 type=env Stop=0.5e-3 Step=auto Accuracy=draft MaxHarm=1 MaxMixOrder={Math.Max(2, 1 + 2 * mix)} " +
                                  "Segments=auto Warmup=auto Start=0 Tol=1e-6 SaveWaveforms=yes Noise=yes EvmSplit=yes");
                    _vars.Add($"Bocc = {G(bocc)}");
                    _meas.Add($"Pout_calc = {G(_t.PoutDbm)}");
                    _meas.Add($"SNR_calc = {G(snr)}");
                    _meas.Add($"EVM_calc = {G(100 * Math.Pow(10, -snr / 20))}");
                    _meas.Add($"Nout_calc = {G(nout)}");
                    _meas.Add("Pout_sim = ENV1.Pavg_dBm[\"Iout\"]");
                    _meas.Add("SNR_sim = ENV1.SNR_dB[\"Iout\", 0]");
                    _meas.Add("EVM_sim = ENV1.EVMnoise_pct[\"Iout\", 0]");
                    _meas.Add("Nout_sim = Pout_sim - SNR_sim");
                    _meas.Add("Pout_err = Pout_sim - Pout_calc");
                    _meas.Add("SNR_err = SNR_sim - SNR_calc");
                    _meas.Add("Nout_err = Nout_sim - Nout_calc");
                    break;
                }
            }
        }

    }
}

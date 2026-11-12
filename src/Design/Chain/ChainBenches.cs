// A chain, transferred: test benches of circuitRF's own System blocks, each drawn by NetlistSchematic (the drawing
// `circuitrf netlist --to-schematic` makes), with the analyses that verify the budget already set up and the budget's
// own numbers beside the simulated ones in a MEAS block. Four for every chain; one more each when it has blockers, when it
// has a canceller and when it asks for the demo; and two more for an array:
//
//   <chain> 1-Tone    a P1Tone per element, a Port at the output. Without a mixer, an S-parameter run with Noise=yes:
//                     gain, NF and each stage's share of it. With one, a harmonic-balance noise run with the LO: the
//                     conversion gain and the SSB NF. And a harmonic-balance drive sweep for the 1 dB compression point.
//   <chain> 2-Tone    a PnTone per element: two tones 50 dB below the predicted IIP3, the intercept and each stage's.
//   <chain> Envelope  a MOD_Source per element, an NR carrier with its source's noise: the in-channel SNR, the EVM's
//                     noise part and the output noise floor.
//   <chain> Levels    a PnTone per element, two tones each at the input power: the signal and its IM3 at every node,
//                     beside the budget's levels (ChainLevels) over a node axis.
//   <chain> Blockers  the blockers alone, each at its own frequency and level, an isolator after every stage so that
//                     each node reads the wave going forward.
//   <chain> Canceller the chain's canceller with real signals: the wanted pair and a three-carrier interference from
//                     two MOD_Sources, the depth against each error swept, and the EVM with and without cancellation.
//   <chain> Demo      the chain demodulated end to end, NR 64-QAM and DBPSK, the LO with a phase-noise mask, over a
//                     sweep of the input power: the constellation, the EVM and its noise and distortion parts.
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
//
// Every bench is checked against what its runs take before it is written (Build): a harmonic-balance run at most six
// tones, 600 mixing products and no two of them on one frequency, its noise run two tones; an envelope run two
// fundamentals, the carrier's and one LO's. A chain with more than one mixer outgrows that: each LO is a tone. So each
// bench one run can hold is written for the whole chain, at a lower mixing order when the cap needs it and said; the
// others once per CONVERSION (Conversions), each from the IF the one before it ends at, beside its own budget; and one
// no run can hold even so is not written, and Build says why.

using System.Globalization;
using System.Text;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Symbol;
using CircuitRF.Engine;
using CircuitRF.Engine.HarmonicBalance;

namespace CircuitRF.Design.Chain;

public enum ChainBench { OneTone, TwoTone, Envelope, Steering, Combiners, Levels, Blockers, Canceller, Demo, Transmitter }

/// <summary>One bench: its cell name, its netlist, where each of its instances is drawn, and what it verifies: the chain
/// named <paramref name="Chain"/>, the whole chain or one of its conversions (<see cref="ChainBenches.Conversions"/>),
/// with that chain's design and budget, which its display is built from.</summary>
public sealed record ChainBenchText(ChainBench Kind, string CellName, string Netlist, ExactPlacement Layout,
                                    string Chain, ChainDesign Design, ChainBudgetResult Budget);

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
        ChainBench.Combiners => chain + " Combiners",
        ChainBench.Levels  => chain + " Levels",
        ChainBench.Blockers => chain + " Blockers",
        ChainBench.Demo    => chain + " Demo",
        ChainBench.Transmitter => chain + " TX",
        _                  => chain + " Canceller",
    };

    /// <summary>The demo bench's input powers, dBm: from 5 dB of SNR (noise-limited) to 5 dB past the input 1 dB
    /// compression point (compression-limited), in seven steps.</summary>
    public static double[] DemoPowers(ChainTotals t)
    {
        double lo = t.MdsDbm + 5, hi = (double.IsFinite(t.Ip1dbDbm) ? t.Ip1dbDbm : t.MdsDbm + 80) + 5;
        return [.. Enumerable.Range(0, 7).Select(i => Math.Round(lo + i * (hi - lo) / 6, 2))];
    }

    /// <summary>The demo's second carrier: DBPSK at this bit rate, offset from the NR carrier by twice the bandwidth.</summary>
    public const double DemoDbpskRateBps = 1e6;

    /// <summary>The Canceller bench's sweeps: phase error, deg; amplitude error, dB; delay mismatch, s.</summary>
    public static readonly double[] CancPhaseErrDeg = [-10, -3, -1, 0, 1, 3, 10];
    public static readonly double[] CancAmpErrDb = [-2, -0.5, 0, 0.5, 2];
    public static readonly double[] CancDelayErrS = [0, 0.5e-9, 1e-9, 2e-9];

    /// <summary>The Canceller bench's interference carriers, (bandwidth, offset from the frequency), Hz: three
    /// bandwidths for the depth a delay mismatch leaves, the first two under the wanted carriers.</summary>
    public static readonly (int Mhz, double OffsetHz)[] CancInterference = [(10, 0), (5, -15e6), (20, 20e6)];

    /// <summary>The variable, and so the axis, the Levels displays plot against: the node, 0 the chain's input and k the
    /// output of stage k.</summary>
    public const string NodeVar = "nodeix";

    /// <summary>The TX bench's back-offs from Psat, dB, the link's own added: from 10 dB of overdrive to 16 dB back.</summary>
    public static readonly double[] TxBackoffsDb = [-10, -6, -3, 0, 2, 4, 6, 8, 10, 12, 16];

    /// <summary>How far an ideal Amp's saturated output sits below its output intercept, dB: 10·log10(π²/4). The tanh
    /// limiter saturates its fundamental at (4/π)·Vsat in amplitude, and its intercept is 2·Vsat.</summary>
    public static readonly double TanhPsatBelowOip3Db = 10 * Math.Log10(Math.PI * Math.PI / 4);

    /// <summary>The steer angles the Steering bench sweeps, degrees from broadside.</summary>
    public static readonly double[] SteerAnglesDeg = [-30, 0, 30];

    /// <summary>The four combiners the Combiners bench sets side by side: topology, kind, and the suffix of its parts.</summary>
    public static readonly (string Topology, string Kind, string Code)[] CombinerVariants =
        [("Direct", "Passive", "DP"), ("Tree", "Passive", "TP"), ("Direct", "Active", "DA"), ("Tree", "Active", "TA")];

    /// <summary>
    /// The benches a chain transfers to: 1-Tone, 2-Tone, Envelope and Levels always; Blockers when it has blockers, Demo
    /// when it asks for it, Canceller when it has a canceller; for an array whose branch is linear (no
    /// mixer before the combiner, so an S-parameter run reaches the combiner), Combiners, and Steering when a phase
    /// shifter in the branch has something to steer with. <paramref name="why"/> says what was left out and why.
    /// </summary>
    public static IReadOnlyList<ChainBench> BenchesFor(ChainDesign d, List<string>? why = null)
    {
        List<ChainBench> b = [ChainBench.OneTone, ChainBench.TwoTone, ChainBench.Envelope, ChainBench.Levels];
        if (d.Blockers.Count > 0) b.Add(ChainBench.Blockers);
        if (d.Demo) b.Add(ChainBench.Demo);
        if (d.Stages.Any(s => s.Part == ChainParts.Canceller)) b.Add(ChainBench.Canceller);
        if (d.Link is not null) b.Add(ChainBench.Transmitter);
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

    /// <summary>
    /// The chain's benches (<see cref="BenchesFor"/>), each one that a single run can hold as the whole chain, and the
    /// others once per conversion (<see cref="Conversions"/>); none when the budget is refused. A bench no run can hold
    /// even per conversion is not written. Every bench run is checked against what its analysis takes: a
    /// harmonic-balance run at most <see cref="AnalysisSettings.HbMaxTones"/> tones and
    /// <see cref="AnalysisSettings.HbMaxMixProducts"/> mixing products, each at the order its numbers need or above
    /// (below what the bench would ask for, when that is too many); an envelope run two fundamentals, the carrier's and
    /// one LO's, the LO more than three of the carrier's bandwidths from it.
    /// </summary>
    /// <param name="benchBase">The directory the benches' own relative references resolve from once written (the
    /// workspace they land in); an SnP stage's file is written relative to it when under it, as a full path otherwise.</param>
    /// <param name="notes">Each bench written per conversion, and why.</param>
    /// <param name="leftOut">Each bench not written, and why.</param>
    public static IReadOnlyList<ChainBenchText> Build(ChainDesign d, ChainBudgetResult r, string chain, string? benchBase = null,
                                                      List<string>? notes = null, List<string>? leftOut = null)
    {
        if (r.Totals is null) return [];
        d = r.Design ?? d;
        var kinds = BenchesFor(d);
        var whole = kinds.ToDictionary(b => b, b => Emit(d, r, chain, b, benchBase));
        var conv = Conversions(d, r, chain);
        // The Levels display reads the 1-Tone and Blockers benches node by node, on its own node axis: the three are
        // written for the whole chain or per conversion together.
        ChainBench[] nodes = [ChainBench.OneTone, ChainBench.Levels, ChainBench.Blockers];
        string? nodeRefused = nodes.Where(x => whole.TryGetValue(x, out var w) && w.Refusal is not null)
                                   .Select(x => CellName(chain, x)).FirstOrDefault();
        string? Why(ChainBench b) => whole[b].Refusal
            ?? (conv is not null && nodes.Contains(b) && nodeRefused is not null
                ? $"{nodeRefused} cannot be, and the Levels display reads the 1-Tone, Levels and Blockers benches node by node, so the three go together"
                : null);
        var list = new List<ChainBenchText>();
        foreach (var b in kinds)
        {
            if (Why(b) is not { } why)
            {
                list.Add(whole[b].Text);
                notes?.AddRange(whole[b].Notes.Select(n => $"{whole[b].Text.CellName}: {n}."));
                continue;
            }
            if (conv is null) { leftOut?.Add($"{CellName(chain, b)} is not written: {why}."); continue; }
            var made = new List<string>();
            foreach (var c in conv)
            {
                if (c.Budget.Totals is null)
                {
                    leftOut?.Add($"{CellName(c.Chain, b)} is not written: {string.Join(" ", c.Budget.Refusals)}");
                    continue;
                }
                var (text, refusal, said) = Emit(c.Design, c.Budget, c.Chain, b, benchBase);
                if (refusal is not null) { leftOut?.Add($"{text.CellName} is not written: {refusal}."); continue; }
                list.Add(text);
                notes?.AddRange(said.Select(n => $"{text.CellName}: {n}."));
                made.Add(text.CellName);
            }
            if (made.Count > 0)
                notes?.Add($"{CellName(chain, b)} is written once per conversion, as {string.Join(", ", made)}, each beside its own " +
                           $"conversion's budget: as the whole chain, {why}.");
        }
        return list;
    }

    private static (ChainBenchText Text, string? Refusal, List<string> Notes) Emit(ChainDesign d, ChainBudgetResult r, string chain,
                                                                                 ChainBench b, string? benchBase)
    {
        var e = new Emitter(d, r, chain, b, benchBase: benchBase);
        var text = e.Emit();
        return (text, e.Refusal, e.Notes);
    }

    /// <summary>One conversion of a chain: the name its benches are prefixed with, the conversion as a chain of its own,
    /// and its budget.</summary>
    public sealed record Conversion(string Chain, ChainDesign Design, ChainBudgetResult Budget);

    /// <summary>
    /// A chain with two or more mixers, cut into its conversions; null for one with fewer. Conversion k runs from the
    /// stage after mixer k − 1 (the chain's input for the first) to mixer k (to the chain's output for the last), so
    /// each holds one mixer and the filtering in front of it, and each is the next one's input: named
    /// "&lt;chain&gt; IFk", it is a chain of its own at the IF the one before it ends at, driven at the budget's level
    /// there, with every blocker as it arrives there, from a source whose temperature is the noise the stages before it
    /// deliver, (Ts + Te)·Gn. Its budget then is the whole chain's from that point on: the same signal, noise floor and
    /// SNR at every node, its gain, NF and intercepts those of its own stages, which the whole chain's follow from by the
    /// same Friis and intercept sums.
    /// </summary>
    public static IReadOnlyList<Conversion>? Conversions(ChainDesign d, ChainBudgetResult r, string chain)
    {
        if (r.Totals is null) return null;
        var mixers = d.Stages.Select((s, i) => (s, i)).Where(x => x.s.Part == ChainParts.Mixer).Select(x => x.i).ToList();
        if (mixers.Count < 2) return null;
        var starts = mixers.SkipLast(1).Select(i => i + 1).Prepend(0).ToList();
        var fin = d.StageInputFrequencies();
        var fb = d.Blockers.Select(b => d.FrequencyHz + b.OffsetHz).ToArray();
        var pb = d.Blockers.Select(b => b.LevelDbm).ToArray();
        var list = new List<Conversion>();
        for (int i = 0, k = 0; k < starts.Count; i++)
        {
            if (i == starts[k])
            {
                int end = k + 1 < starts.Count ? starts[k + 1] : d.Stages.Count;
                var c = d.Clone();
                c.Stages = c.Stages.GetRange(i, end - i);
                if (i > 0)
                {
                    var at = r.Stages[i - 1];
                    c.FrequencyHz = fin[i];
                    c.InputPowerDbm = at.PoutDbm;
                    c.TemperatureK = Math.Pow(10, (at.NoiseOutDbm - 30) / 10) / (ChainBudget.K * d.BandwidthHz);
                    c.Blockers = [.. pb.Select((p, j) => new ChainBlocker(fb[j] - fin[i], p))];
                }
                list.Add(new Conversion($"{chain} IF{k + 1}", c, ChainBudget.Evaluate(c)));
                k++;
            }
            for (int j = 0; j < fb.Length; j++) pb[j] += ChainLevels.GainAt(d.Stages[i], r.Stages[i].Stage, ref fb[j], d.BaseDirectory);
        }
        return list;
    }

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
        d = r.Design ?? d;
        if (NameValidator.Validate(chain) is { } bad) return new([], [$"'{chain}' cannot name a cell: {bad}"], []);

        var planned = new List<string>();
        var benches = Build(d, r, chain, Workspace.OwningRoot.Of(Path.GetFullPath(workspaceDir)), planned, planned);
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
        return new(paths, [], [.. r.Notes, .. notes, .. left, .. planned]);
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
        d = r.Design ?? d;
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

    private sealed class Emitter(ChainDesign d, ChainBudgetResult r, string chain, ChainBench bench, bool diagram = false,
                                 string? benchBase = null)
    {
        /// <summary>Why no run can hold this bench as emitted, or null: the first analysis that cannot be run.</summary>
        public string? Refusal { get; private set; }

        /// <summary>What the bench gave up to be run: an analysis below the mixing order its numbers converge at.</summary>
        public List<string> Notes { get; } = [];

        /// <summary>
        /// The mixing order a harmonic-balance run on <paramref name="toneHz"/> is given: <paramref name="desired"/>,
        /// what the bench's numbers converge at, or the highest below it that the run takes, down to
        /// <paramref name="needed"/>, the least that carries what the bench reads (<paramref name="what"/>). An order is
        /// not taken when it retains more products than a run takes, or when a product the bench reads
        /// (<paramref name="tracked"/>) falls on the frequency of another: a run cannot tell the two apart, and reads
        /// them wrongly rather than refusing. Two products on one frequency that the bench reads neither of are run and
        /// said. Below <paramref name="needed"/> the bench is refused. A noise run (<paramref name="noise"/>) takes one or
        /// two tones, as the small-signal sweep it runs on does.
        /// </summary>
        private int Order(string analysis, double[] toneHz, int desired, int needed, string what, IEnumerable<int[]> tracked,
                          bool noise = false)
        {
            var s = AnalysisSettings.Default;
            int tones = toneHz.Length, most = noise ? 2 : s.HbMaxTones;
            static string N0(int n) => n.ToString("N0", CultureInfo.InvariantCulture);
            var read = tracked.ToList();
            var passed = new List<string>();   // why each order above the one taken was not
            string? clash = null;
            for (int o = desired; o >= needed && tones <= most; o--)
            {
                if (MixingLattice.CountFor(tones, o) > s.HbMaxMixProducts)
                {
                    passed.Add($"at {o} its {tones} tones retain {N0(MixingLattice.CountFor(tones, o))} mixing products, over the " +
                               $"{N0(s.HbMaxMixProducts)} a run takes");
                    continue;
                }
                var (onRead, other) = Coincident(toneHz, o, read);
                if (onRead is not null) { clash = onRead; passed.Add($"at {o} the {onRead}, which it reads"); continue; }
                if (o < desired)
                    Notes.Add($"its harmonic-balance run {analysis} runs at MaxMixOrder {o}, below the {desired} its numbers converge " +
                              $"at: {string.Join("; ", passed)}. So its numbers read a little off the budget's (the reference page's " +
                              "agreement table says how far)");
                if (other is not null)
                    Notes.Add($"on its harmonic-balance run {analysis} the {other}. The bench reads neither, so it runs at MaxMixOrder " +
                              $"{o}, but a run cannot tell the two apart, and what it reads can move by thousandths of a dB");
                return o;
            }
            Refusal ??= tones > most
                ? $"its harmonic-balance {(noise ? "noise " : "")}run {analysis} has {tones} tones, and a {(noise ? "noise " : "")}run takes at most {most}"
                : clash is not null
                ? $"on its harmonic-balance run {analysis} the {clash}, a product it reads, at MaxMixOrder {needed}, the least that " +
                  $"carries {what}, and at every order above it the run takes. A run cannot tell two products on one frequency " +
                  "apart: move a blocker or an LO off it"
                : $"its harmonic-balance run {analysis} has {tones} tones, and MaxMixOrder {needed}, the least that carries {what}, " +
                  $"retains {N0(MixingLattice.CountFor(tones, needed))} mixing products, over the {N0(s.HbMaxMixProducts)} a run takes";
            return desired;
        }

        /// <summary>
        /// Two products of the lattice on one frequency, said: the first such pair with a product of
        /// <paramref name="read"/> in it (null: any pair), and the first with none. Commensurate tones put them there: a
        /// blocker on a mixer's image, whose product lands where another's does, or blockers spaced evenly, whose
        /// differences coincide.
        /// </summary>
        private static (string? OnRead, string? Other) Coincident(double[] toneHz, int order, IEnumerable<int[]>? read)
        {
            var lat = new MixingLattice(toneHz.Length, order);
            var keep = read?.Select(v => Canonical(v)).ToHashSet(StringComparer.Ordinal);
            var f = Enumerable.Range(0, lat.MixCount).Select(m => (Hz: Math.Abs(lat.FrequencyOf(m, toneHz)), M: m)).OrderBy(x => x.Hz).ToList();
            string? onRead = null, other = null;
            for (int i = 1; i < f.Count && onRead is null; i++)
            {
                if (f[i].Hz - f[i - 1].Hz >= 1.0) continue;
                string a = lat.Label(f[i - 1].M), b = lat.Label(f[i].M);
                string said = $"products {a} and {b} fall on the same frequency, {(f[i].Hz / 1e6).ToString("0.######", CultureInfo.InvariantCulture)} MHz";
                if (keep is null || keep.Contains(a) || keep.Contains(b)) onRead = said;
                else other ??= said;
            }
            return (onRead, other);
        }

        /// <summary>A product's label as the lattice keeps it: k or −k, whichever has its first nonzero entry positive.</summary>
        private static string Canonical(int[] v)
        {
            int sign = v.FirstOrDefault(x => x != 0) < 0 ? -1 : 1;
            return MixingLattice.Label([.. v.Select(x => sign * x)]);
        }

        /// <summary>
        /// An envelope run's fundamentals and lattice (<c>EnvelopeEngineMt</c>): the carriers' group and one LO, the LO more
        /// than three of a carrier's bandwidths from it, and no two products of the two on one frequency at the run's
        /// MaxMixOrder, max(2, 1 + 2·mixers). <paramref name="carriers"/>: each carrier's frequency and bandwidth, Hz; they
        /// make one group (the demo's second carrier is two bandwidths off, inside three), centred midway between the
        /// outermost.
        /// </summary>
        private void EnvelopeFits(params (double Hz, double Bw)[] carriers)
        {
            var los = d.Stages.Where(s => s.Part == ChainParts.Mixer).Select(s => ChainValue.Or(s.Get("Flo"), 0)).Distinct().ToList();
            if (los.Count > 1)
                Refusal ??= $"an envelope run has two fundamentals, the carrier's and one LO's, and {los.Count} LOs would make {los.Count + 1}";
            static string Mhz(double hz) => (hz / 1e6).ToString("0.###", CultureInfo.InvariantCulture);
            foreach (double lo in los)
                foreach (var (hz, bw) in carriers)
                    if (Math.Abs(lo - hz) <= 3 * Math.Max(bw, 1e6))
                        Refusal ??= $"its LO at {Mhz(lo)} MHz is {Mhz(Math.Abs(lo - hz))} MHz from the {Mhz(bw)} MHz carrier at {Mhz(hz)} MHz, " +
                                    "and an envelope run takes an LO as a fundamental of its own only more than three of the carrier's " +
                                    "bandwidths from it";
            if (los.Count != 1) return;
            double centre = 0.5 * (carriers.Min(c => c.Hz) + carriers.Max(c => c.Hz));
            int order = Math.Max(2, 1 + 2 * los.Count);
            if (Coincident([.. new[] { centre, los[0] }.Order()], order, null).OnRead is { } clash)
                Refusal ??= $"its envelope run's {clash} at MaxMixOrder {order}, and an envelope run cannot tell two products on one " +
                            "frequency apart";
        }

        /// <summary>The stage each placed instance belongs to, and where each stage's first block sits on the first
        /// element's row — what the diagram's selection and annotations are read from.</summary>
        public readonly Dictionary<string, int> StageOf = new(StringComparer.Ordinal);
        public readonly Dictionary<int, (double X, double Y)> Anchor = [];
        private int _stage = -1;

        private const double Lead = 500, BranchPitch = 1400;
        /// <summary>How far below the row the diagram's IN and OUT Ports sit: their pin two grid steps under it.</summary>
        private const double DiagramPortDrop = 400;
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
            foreach (var p in part.Parameters.Where(p => !skip.Contains(p.Name) && !p.BudgetOnly))
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
            if (bench == ChainBench.Canceller) return EmitCanceller();
            if (bench == ChainBench.Transmitter) return EmitTransmitter();
            int n = N, nb = Branch;
            bool array = n > 1;
            double frf = d.FrequencyHz;
            _vars.Add($"Frf = {G(frf)}");
            if (bench is ChainBench.TwoTone or ChainBench.Levels)
            {
                double df = Math.Min(1e6, d.BandwidthHz);
                _vars.Add($"F1 = {G(frf - df / 2)}");
                _vars.Add($"F2 = {G(frf + df / 2)}");
                if (bench == ChainBench.TwoTone) _vars.Add($"P2t = {F(Finite(_t.Iip3Dbm, 0) - TwoToneBackoffDb)}");
                else _vars.Add($"Pin = {F(d.InputPowerDbm)}");   // two tones, each at the chain's input power
            }
            else if (bench == ChainBench.Blockers)
                for (int k = 0; k < d.Blockers.Count; k++)
                {
                    _vars.Add($"FB{k + 1} = {G(frf + d.Blockers[k].OffsetHz)}");
                    _vars.Add($"PB{k + 1} = {F(d.Blockers[k].LevelDbm)}");
                }
            else if (bench == ChainBench.OneTone)
            {
                // Pss is the small-signal drive and Pin the source's: the compression sweep moves Pin, and a row that
                // read Pin would read the sweep's axis, not the drive the noise run was made at.
                _vars.Add($"Pss = {F(Finite(_t.Ip1dbDbm, 0) - CompressionBackoffDb)}");
                _vars.Add("Pin = Pss");
            }
            else if (bench != ChainBench.Blockers) _vars.Add($"Pin = {F(d.InputPowerDbm)}");

            // Frequency and mixing-product tracking: v1 is the wanted product's index on the tone lattice and v3 the
            // lower IM3's, both updated by every mixer the signal passes.
            int signalTones = bench switch { ChainBench.TwoTone or ChainBench.Levels => 2, ChainBench.Blockers => d.Blockers.Count, _ => 1 };
            int tones = signalTones + Mixers;
            var v1 = new int[tones]; v1[0] = 1;
            var v3 = new int[tones];
            if (bench is ChainBench.TwoTone or ChainBench.Levels) { v3[0] = 2; v3[1] = -1; }
            int lo = signalTones;
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
                    prev = Forward(net, d.Stages[j].Name + (array ? $"_{k}" : ""));
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
                cur = j == d.Stages.Count - 1 ? net : Forward(net, s.Name);
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
                                      new ExactPlacement { Instances = _at, AutoRoute = true }, chain, d, r);
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

        /// <summary>
        /// The Canceller bench: the chain's (first) canceller on its own, with real signals. The wanted signal (two
        /// carriers, 64-QAM and QPSK) and the interference (three carriers of 10, 5 and 20 MHz, the first two under the
        /// wanted ones) come from MOD_Sources the Signal Designer edits. The interference is split: one share leaks into
        /// the main path through a line of 1 ns, the other is the reference path, an Amp for the amplitude, a Combiner
        /// path for a flat phase and a line for the delay, into the coupler's isolated port. Its VAR block sets the
        /// errors: amp_err, phase_err, delay_err; canc_on and sig_on switch the reference path and the wanted signal.
        /// Envelope runs: the interference uncancelled; the depth against each error, swept; and the wanted carriers'
        /// EVM and SNR with and without cancellation. Beside each depth, the closed form (<see cref="CancellerSpec"/>)
        /// at each interference carrier's bandwidth and offset.
        /// </summary>
        private ChainBenchText EmitCanceller()
        {
            int ci = d.Stages.FindIndex(st => st.Part == ChainParts.Canceller);
            var c = CancellerSpec.Read(d.Stages[ci]);
            double psig = ci == 0 ? d.InputPowerDbm : r.Stages[ci - 1].PoutDbm;
            double cAmp = Math.Pow(10, -c.CouplingDb / 20), tauLeak = 1e-9;
            // The reference path's gain at no error: the coupled arm's |S| against the through arm's, the two 3 dB shares
            // (the splitter's and the flat phase's) cancelling the main combiner's and the leak's.
            double g0 = 20 * Math.Log10(Math.Sqrt(1 - cAmp * cAmp) / cAmp);
            var (_, bsig) = Carrier(5e6);
            _vars.AddRange([$"Frf = {G(d.FrequencyHz)}", $"Psig = {F(psig)}", $"Pint = {F(c.PintDbm)}",
                            $"amp_err = {F(c.AmpErrDb)}", $"phase_err = {F(c.PhaseErrDeg)}", $"delay_err = {c.DelayErrS.ToString("G12", CultureInfo.InvariantCulture)}",
                            "canc_on = 1", "sig_on = 1", $"tau_leak = {G(tauLeak)}"]);
            string name = d.Stages[ci].Name;
            int ni = CancInterference.Length;
            // Two wanted carriers at Psig each, 3 dB more for the main combiner's share; the interference's carriers at
            // Pint each at the canceller's input, 6 dB more for the split and the combiner.
            Line("MOD_Source:SIG  s  0  Fc=Frf  Pavg=Psig+6.0206+(sig_on-1)*300 dBm  Z=50 Ohm  " +
                 "Carrier[1]=\"NR;BW=5MHz;SCS=15kHz;Mod=64QAM;Off=0;P=0;Seed=1\"  " +
                 $"Carrier[2]=\"NR;BW=5MHz;SCS=15kHz;Mod=QPSK;Off={F(CancInterference[1].OffsetHz / 1e6)}MHz;P=0;Seed=2\"  " +
                 $"Record=0.00025  Noise=yes  Temp={F(d.TemperatureK - 273.15)}");
            Place("SIG", 0, 200);
            Line($"MOD_Source:INT  i  0  Fc=Frf  Pavg=Pint+6.0206+{G(10 * Math.Log10(ni))} dBm  Z=50 Ohm  " +
                 string.Join("  ", CancInterference.Select((ic, k) =>
                     $"Carrier[{k + 1}]=\"NR;BW={ic.Mhz}MHz;SCS=15kHz;Mod=64QAM;Off={F(ic.OffsetHz / 1e6)}MHz;P=0;Seed={k + 3}\"")) +
                 "  Record=0.00025");
            Place("INT", 0, 2600);
            Line("Combiner:ISPL  i  0  il  0  ir  0  N=2  Z0=50 Ohm");
            Place("ISPL", 900, 2400, mirror: true);
            Line("TLIN:LEAK  il  lk  Z=50 Ohm  E=360*Frf*tau_leak deg  F=Frf Hz");
            Place("LEAK", 1800, 1300);
            Line("Combiner:MAIN  m  0  s  0  lk  0  N=2  Z0=50 Ohm");
            Place("MAIN", 2700, 300);
            _stage = ci;
            Line($"Coupler:{name}  m  0  t  0  {name}_cp  0  {name}_iso  0  Coupling={F(c.CouplingDb)} dB  IL={F(c.IlDb)} dB  Phase=90 deg");
            Line($"R:{name}_Rc  {name}_cp  0  R=50 Ohm");
            Place(name, 3700, 400); Place($"{name}_Rc", 4000, 1000);
            string nf = c.NfRefDb is { } nfr ? $"  NF={F(nfr)} dB" : "";
            Line($"Amp:{name}_VMA  ir  0  r1  0  Gain={G(g0)}+amp_err+(canc_on-1)*300 dB{nf}  IP3=200 dBm  IP3Ref=Input");
            Place($"{name}_VMA", 1900, 2700);
            // A flat phase: one path of a 2-way Combiner, the other terminated. A line would be a delay as well. The
            // phase is set where it nulls the carrier's centre, which takes back the delay mismatch's own phase there,
            // 360°·Frf·delay_err, so the mismatch shows only across the band, as the closed form has it.
            Line($"Combiner:{name}_VMP  r2  0  r1  0  {name}_VMP_t  0  N=2  Phase[1]=-90+phase_err+360*Frf*delay_err deg  Z0=50 Ohm");
            Line($"R:{name}_VMP_T  {name}_VMP_t  0  R=50 Ohm");
            Place($"{name}_VMP", 2800, 2600, mirror: true); Place($"{name}_VMP_T", 3500, 3100);
            Line($"TLIN:{name}_DL  r2  {name}_iso  Z=50 Ohm  E=360*Frf*(tau_leak+delay_err) deg  F=Frf Hz");
            Place($"{name}_DL", 3400, 2000);
            _stage = -1;
            Line("IProbe:Iout  t  load");
            Line("R:RL  load  0  R=50 Ohm");
            Place("Iout", 4600, 300); Place("RL", 5200, 600);

            string env(string nm, bool noise) =>
                $"analysis {nm} type=env Stop=0.25e-3 Step=auto Accuracy=draft MaxHarm=1 Segments=auto Warmup=auto Start=0 Tol=1e-6 " +
                (noise ? "SaveWaveforms=no Noise=yes EvmSplit=yes" : "SaveWaveforms=no");
            string list(IEnumerable<double> v) => string.Join(",", v.Select(x => x.ToString("G12", CultureInfo.InvariantCulture)));
            // The interference alone, uncancelled: the reference for every depth.
            _analyses.Add(env("ENVN", false));
            _analyses.Add("analysis SWN2 type=parametric_sweep Var=canc_on Values=0 Inner=ENVN");
            _analyses.Add("analysis SWN1 type=parametric_sweep Var=sig_on Values=0 Inner=SWN2");
            // The depth against each error, the others at their nominal values.
            foreach (var (an, v, vals) in new[] { ("ENVP", "phase_err", CancPhaseErrDeg), ("ENVA", "amp_err", CancAmpErrDb), ("ENVD", "delay_err", CancDelayErrS) })
            {
                _analyses.Add(env(an, false));
                _analyses.Add($"analysis SW{an[3]}2 type=parametric_sweep Var={v} Values={list(vals)} Inner={an}");
                _analyses.Add($"analysis SW{an[3]}1 type=parametric_sweep Var=sig_on Values=0 Inner=SW{an[3]}2");
            }
            // The wanted signal with and without cancellation, its noise on.
            _analyses.Add(env("ENVO", true));
            _analyses.Add("analysis SWO type=parametric_sweep Var=canc_on Values=0,1 Inner=ENVO");

            _meas.Add($"Pint_calc = {G(c.PintDbm - c.ThroughLossDb)}");
            for (int k = 0; k < ni; k++)
            {
                int carrier = 2 + k;   // the carrier axis: the wanted source's two, then the interference's
                var (mhz, off) = CancInterference[k];
                double bocc = Carrier(mhz * 1e6).OccupiedHz;
                string tag = $"I{k + 1}";
                // Each sweep prepends its axes: the two setters' single points, then the swept error.
                _meas.Add($"Pref_{tag} = ENVN.ACLR[0, 0, \"Iout\", {carrier}, \"Pch_dBm\"]");
                foreach (var (an, sw) in new[] { ("ENVP", "P"), ("ENVA", "A"), ("ENVD", "D") })
                {
                    _meas.Add($"Depth{sw}_{tag}_sim = Pref_{tag} - {an}.ACLR[0, :, \"Iout\", {carrier}, \"Pch_dBm\"]");
                    // R = 1 + a² − 2a·cos(φ − 2πντ)·sinc(πBτ): the swept error as its sweep's axis, the other two at the
                    // stage's own values (a measurement reading any swept variable reads that sweep's axis, whichever
                    // analysis it is about). cos and sin as e^(jx), which a sweep's cube takes.
                    string amp = sw == "A" ? "amp_err" : $"({G(c.AmpErrDb)})", phs = sw == "P" ? "phase_err" : $"({G(c.PhaseErrDeg)})";
                    string dly = sw == "D" ? "delay_err" : $"({c.DelayErrS.ToString("G12", CultureInfo.InvariantCulture)})";
                    string x = $"(pi*{G(bocc)}*{dly} + 1e-300)";
                    string ph = $"({phs}*pi/180 - 2*pi*{G(off)}*{dly})";
                    _meas.Add($"Depth{sw}_{tag}_calc = -10*log10(1 + 10^({amp}/10) - 2*10^({amp}/20)*real(e^(j*{ph}))*imag(e^(j*{x}))/{x})");
                }
            }
            // The wanted carriers, without (canc_on = 0) and with cancellation.
            for (int k = 0; k < 2; k++)
            {
                string tag = k == 0 ? "QAM64" : "QPSK";
                foreach (var (on, w) in new[] { (0, "off"), (1, "on") })
                {
                    _meas.Add($"EVM_{tag}_{w} = ENVO.EVM_pct[{on}, \"Iout\", {k}]");
                    _meas.Add($"SNR_{tag}_{w} = ENVO.SNR_dB[{on}, \"Iout\", {k}]");
                }
            }
            return Text();
        }

        /// <summary>
        /// The TX bench: one transmit element's PA driven by an NR 64-QAM carrier, through its Loss to a 50 Ω load, over
        /// a sweep of the back-off from Psat (<see cref="TxBackoffsDb"/> and the link's own). The PA is an Amp whose output
        /// intercept is Psat + 10·log10(π²/4): the tanh limiter saturates its fundamental at that much below its OIP3. Beside
        /// the budget's Pout − Loss, the simulated output and their difference (the compression the back-off leaves), and
        /// the EVM and the adjacent-channel ratios that choose the back-off. A memoryless PA's EVM and ACPR do not depend
        /// on the carrier's bandwidth, so the NR channel nearest the chain's bandwidth up to 50 MHz stands for it.
        /// </summary>
        private ChainBenchText EmitTransmitter()
        {
            var link = d.Link!;
            double psat = ChainValue.Or(link.GetTx("Psat"), 0), bo = ChainValue.Or(link.GetTx("Backoff"), 0);
            double g = ChainValue.Or(link.GetTx("PaGain"), 0), loss = ChainValue.Or(link.GetTx("Loss"), 0);
            var bos = TxBackoffsDb.Append(bo).Distinct().Order().ToArray();
            var (mhz, _) = Carrier(d.BandwidthHz);
            _vars.AddRange([$"Frf = {G(d.FrequencyHz)}", $"Psat = {F(psat)}", $"PaGain = {F(g)}", $"Loss = {F(loss)}",
                            $"BO = {F(bo)}", "Pin = Psat - BO - PaGain"]);
            Line($"MOD_Source:SRC  in  0  Fc=Frf  Pavg=Pin dBm  Z=50 Ohm  " +
                 $"Carrier[1]=\"NR;BW={mhz}MHz;SCS=15kHz;Mod=64QAM;Off=0;P=0;Seed=1\"  Record=0.0005  Noise=no");
            Place("SRC", 0, 200);
            Line($"Amp:PA  in  0  pa  0  Gain=PaGain dB  IP3=Psat+{G(TanhPsatBelowOip3Db)} dBm  IP3Ref=Output");
            Place("PA", 800, 0);
            Line("Atten:LOSS  pa  0  ant  0  Loss=Loss dB  Z0=50 Ohm");
            Place("LOSS", 1600, 0);
            Line("IProbe:Iout  ant  load");
            Line("R:RL  load  0  R=50 Ohm");
            Place("Iout", 2300, -100); Place("RL", 2900, 200);
            _analyses.Add("analysis ENVT type=env Stop=0.5e-3 Step=auto Accuracy=draft MaxHarm=1 Segments=auto Warmup=auto Start=0 " +
                          "Tol=1e-6 SaveWaveforms=no");
            _analyses.Add($"analysis SWBO type=parametric_sweep Var=BO Values={string.Join(",", bos.Select(F))} Inner=ENVT");
            _meas.Add("Pant_calc = Psat - BO - Loss");
            _meas.Add("Pant_sim = ENVT.Pavg_dBm[:, \"Iout\"]");
            _meas.Add("Comp = Pant_calc - Pant_sim");
            _meas.Add("EVM_sim = ENVT.EVM_pct[:, \"Iout\", 0]");
            _meas.Add("ACPR_L = ENVT.ACLR[:, \"Iout\", 0, \"ACPR1L\"]");
            _meas.Add("ACPR_U = ENVT.ACLR[:, \"Iout\", 0, \"ACPR1U\"]");
            // The deepest overdrive is near saturation: its output against Psat, the PA's own limit.
            _meas.Add($"Psat_err = ENVT.Pavg_dBm[0, \"Iout\"] + Loss - Psat");
            // The link's own back-off, as scalars for the display's table.
            int k = System.Array.IndexOf(bos, bo);
            _meas.Add($"Pant_at_calc = {G(psat - bo - loss)}");
            _meas.Add($"Pant_at_sim = ENVT.Pavg_dBm[{k}, \"Iout\"]");
            _meas.Add("Pant_at_err = Pant_at_sim - Pant_at_calc");
            _meas.Add($"EVM_at = ENVT.EVM_pct[{k}, \"Iout\", 0]");
            _meas.Add($"ACPR_L_at = ENVT.ACLR[{k}, \"Iout\", 0, \"ACPR1L\"]");
            _meas.Add($"ACPR_U_at = ENVT.ACLR[{k}, \"Iout\", 0, \"ACPR1U\"]");
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
                case ChainBench.Levels:
                    Line($"PnTone:{name}  {net}  0  Freq[1]=F1  Pavl[1]=Pin dBm  Phase[1]=0 deg  Freq[2]=F2  Pavl[2]=Pin dBm  Phase[2]=0 deg  Z=50 Ohm"); break;
                case ChainBench.Blockers:
                    Line($"PnTone:{name}  {net}  0" + string.Concat(Enumerable.Range(1, d.Blockers.Count).Select(b =>
                             $"  Freq[{b}]=FB{b}  Pavl[{b}]=PB{b} dBm  Phase[{b}]=0 deg")) + "  Z=50 Ohm"); break;
                case ChainBench.Demo:
                {
                    // Two signals of two kinds, each at Pin: NR 64-QAM at the frequency and DBPSK beside it. A DBPSK
                    // carrier is a source's only one, so each has its own source, the two joined by a 2-way Combiner
                    // (3 dB more each for its share; their two noises at Ts average to Ts).
                    var (mhz, _) = Carrier(d.BandwidthHz);
                    string temp = $"Noise=yes  Temp={F(d.TemperatureK - 273.15)}";
                    Line($"MOD_Source:{name}  {net}_q  0  Fc=Frf  Pavg=Pin+3.0103 dBm  Z=50 Ohm  " +
                         $"Carrier[1]=\"NR;BW={mhz}MHz;SCS=15kHz;Mod=64QAM;Off=0;P=0;Seed=1\"  Record=0.0005  {temp}");
                    Line($"MOD_Source:{name}B  {net}_b  0  Fc=Frf  Pavg=Pin+3.0103 dBm  Z=50 Ohm  " +
                         $"Carrier[1]=\"DBPSK;Rb={F(DemoDbpskRateBps / 1e6)}Mbps;Data=PN9;Off={F(2 * d.BandwidthHz / 1e6)}MHz;P=0\"  Record=0.0005  {temp}");
                    Line($"Combiner:{name}C  {net}  0  {net}_q  0  {net}_b  0  N=2  Z0=50 Ohm");
                    Place(name, _x - 600, _y - 200);
                    Place($"{name}B", _x - 600, _y + 600);
                    Place($"{name}C", _x + 200, _y, mirror: true);
                    _x += 1000;
                    return;
                }
                default:
                    var (mhz0, _) = Carrier(d.BandwidthHz);
                    Line($"MOD_Source:{name}  {net}  0  Fc=Frf  Pavg=Pin dBm  Z=50 Ohm  " +
                         $"Carrier[1]=\"NR;BW={mhz0}MHz;SCS=15kHz;Mod=64QAM;Off=0;P=0;Seed=1\"  Record=0.0005  Noise=yes  " +
                         $"Temp={F(d.TemperatureK - 273.15)}");
                    break;
            }
            // The diagram's Port two grid steps below the row, so its wire rises straight to the row and turns once. Any
            // higher and the router, kept a grid step clear of the glyph everywhere but straight out of the pin, climbed
            // past the row and came back down to it: a jog.
            Place(name, _x, _y + (diagram ? DiagramPortDrop : 200));
            _x += Lead;
        }

        private void Load(int n)
        {
            if (diagram) { Line($"Port:OUT  out  0  Num={n + 1}  Z=50 Ohm"); Place("OUT", _x, _y + DiagramPortDrop); return; }
            switch (bench)
            {
                case ChainBench.OneTone: Line($"Port:PO  out  0  Num={n + 1}  Z=50 Ohm"); Place("PO", _x, _y + 200); break;
                case ChainBench.TwoTone or ChainBench.Levels or ChainBench.Blockers: Line("R:RL  out  0  R=50 Ohm"); Place("RL", _x, _y + 200); break;
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
                    // The demo's LO carries a phase-noise mask, which an envelope run plays as a random phase.
                    string pn = bench == ChainBench.Demo ? $"  PhaseNoise=\"{DemoPhaseNoise}\"" : "";
                    Line($"P1Tone:LO_{name}  {lon}  0  Freq={G(flo)}  Pavl={Param(new ChainParam("Plo", "", "dBm"), s.Get("Plo"))}  Z=50 Ohm{pn}");
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
                case ChainParts.Snp:
                {
                    // The library's SnP block on the file, the chain through In → Out and every other port terminated
                    // in 50 Ω; a stated NF above the file's own, or a stated intercept, an ideal Amp of 0 dB after it.
                    var snp = ChainSnp.Read(s, new ChainStageContext(f, d.BaseDirectory), [])!;
                    string mid = snp.NeedsAmp ? $"{b}_sp" : b;
                    var nets = Enumerable.Range(1, snp.Ports)
                        .Select(p => p == snp.In ? a : p == snp.Out ? mid : $"{name}_p{p}").ToList();
                    string file = ChainSnp.Reference(snp.FullPath, benchBase);
                    // A file with gain and no noise data is noiseless in a simulation; saying so on the line makes it the
                    // bench's statement rather than a gap the noise run warns about. The NF stated is the Amp's.
                    Line($"SnP:{name}  {string.Join("  ", nets)}  NumPorts={snp.Ports}  File=\"{file}\"" +
                         (snp.NoiseFrom == SnpNoiseFrom.None ? "  Noise=no" : ""));
                    Inline(name, 300);
                    if (snp.FileNoiseFactor > 1 + 1e-12) placed.Add(name);
                    int t = 0;
                    for (int p = 1; p <= snp.Ports; p++)
                    {
                        if (p == snp.In || p == snp.Out) continue;
                        Line($"R:{name}_T{p}  {name}_p{p}  0  R=50 Ohm");
                        Place($"{name}_T{p}", _x - Lead - 300 + 300 * t++, _y + 900);
                        placed.Add($"{name}_T{p}");
                    }
                    if (snp.NeedsAmp)
                    {
                        double nfAmp = 10 * Math.Log10(snp.AmpNoiseFactor);
                        string ip = double.IsFinite(snp.Iip3Dbm) ? G(snp.Iip3Dbm + snp.GainDb) : "200";
                        Line($"Amp:{name}_NL  {mid}  0  {b}  0  Gain=0 dB" + (nfAmp > 1e-12 ? $"  NF={G(nfAmp)} dB" : "") +
                             $"  IP3={ip} dBm  IP3Ref=Input");
                        Inline($"{name}_NL", 300);
                        if (nfAmp > 1e-12) placed.Add($"{name}_NL");
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
                case ChainParts.Canceller:
                {
                    // The through arm carries the chain; the coupled port is terminated, and the isolated port takes the
                    // reference path. With no interference on these benches, the path is only its noise: a T0 termination
                    // when passive, an Amp of the gain it would need, its own NF, from a T0 termination when not.
                    var c = CancellerSpec.Read(s);
                    Line($"Coupler:{name}  {a}  0  {b}  0  {name}_cp  0  {name}_iso  0  Coupling={F(c.CouplingDb)} dB  IL={F(c.IlDb)} dB  Phase=90 deg");
                    Line($"R:{name}_Rc  {name}_cp  0  R=50 Ohm");
                    _x += 300; Place(name, _x, _y + 100);
                    Place($"{name}_Rc", _x + 300, _y + 700);
                    if (c.NfRefDb is { } nfr)
                    {
                        Line($"Amp:{name}_REF  {name}_r  0  {name}_iso  0  Gain={F(c.RefGainDb)} dB  NF={F(nfr)} dB  IP3=200 dBm  IP3Ref=Input");
                        Line($"R:{name}_RT  {name}_r  0  R=50 Ohm");
                        Place($"{name}_REF", _x - 300, _y + 1100); Place($"{name}_RT", _x - 1000, _y + 1300);
                        placed.AddRange([$"{name}_REF", $"{name}_RT"]);
                    }
                    else
                    {
                        Line($"R:{name}_Ri  {name}_iso  0  R=50 Ohm");
                        Place($"{name}_Ri", _x - 300, _y + 700);
                        placed.Add($"{name}_Ri");
                    }
                    if (c.IlDb > 0) placed.Add(name);
                    _x += 300 + Lead;
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

        /// <summary>
        /// The Levels display's node axis: a variable <c>nodeix</c> swept over 0 … count − 1 around a DC solve, which is
        /// what turns one scalar measurement per node into one cube over the nodes (a swept variable is its sweep's axis
        /// in a measurement). The DC solve of these ideal blocks is trivial. Not <c>node</c>: a harmonic-balance
        /// result's own voltage cube has an axis of that name, its nets, and a measurement would read that instead.
        /// </summary>
        private void NodeAxis(int count)
        {
            _vars.Add($"{NodeVar} = 0");
            _analyses.Add("analysis DCN type=dc");
            _analyses.Add($"analysis SWN type=parametric_sweep Var={NodeVar} Values={string.Join(",", Enumerable.Range(0, count))} Inner=DCN");
        }

        /// <summary>One cube over the node axis from one expression per node: Σ v_k·L_k(nodeix), L_k the Lagrange basis
        /// polynomial that is 1 at node k and 0 at every other node — exact at the integers, in arithmetic alone.</summary>
        private static string NodeCube(int count, Func<int, string> value)
        {
            var terms = new List<string>();
            for (int k = 0; k < count; k++)
            {
                double den = 1;
                var num = new List<string>();
                for (int j = 0; j < count; j++)
                    if (j != k) { num.Add($"({NodeVar} - {j})"); den *= k - j; }
                string basis = num.Count == 0 ? "1" : $"{string.Join("*", num)}/{G(den)}";
                terms.Add($"({value(k)})*{basis}");
            }
            return string.Join(" + ", terms);
        }

        /// <summary>On the Blockers bench, an isolator after a stage (a Circulator, its third port terminated), so the
        /// stage's output node sees a match and its voltage is the forward wave; elsewhere the net itself.</summary>
        private string Forward(string net, string stage)
        {
            if (bench != ChainBench.Blockers) return net;
            string iso = $"FWD_{stage}";
            Line($"Circulator:{iso}  {net}  0  {net}_f  0  {iso}_t  0  Direction=CW");
            Line($"R:{iso}_R  {iso}_t  0  R=50 Ohm");
            Inline(iso, 300); Place($"{iso}_R", _x - Lead - 300, _y + 900);
            return net + "_f";
        }

        /// <summary>The nets the Levels displays read, node by node: element 1's input, then each stage's output.</summary>
        private List<string> NodeNets(string[] stageNets) => [N > 1 ? "in1" : "in", .. stageNets];

        /// <summary>The demo LO's phase noise, single-sided, offset:level in dBc/Hz: a synthesizer's shape.</summary>
        private const string DemoPhaseNoise = "1kHz:-75, 10kHz:-90, 100kHz:-100, 1MHz:-120, 10MHz:-140";

        /// <summary>Σ a_k² / a_1²: how many element 1's the paths' noise is worth — N for equal paths.</summary>
        private double PathWeight()
        {
            if (d.Stages.FirstOrDefault(s => s.Part == ChainParts.Combiner) is not { } st) return 1;
            var c = ChainParts.ReadCombiner(st);
            return Enumerable.Range(1, c.N).Sum(k => c.Weight(k) * c.Weight(k)) / (c.Weight(1) * c.Weight(1));
        }

        /// <summary>A product as a measurement names it. The lattice keeps one of k and −k, the one whose first nonzero
        /// entry is positive (the other is its conjugate, of the same magnitude): after a mixer whose LO is above the
        /// signal the tracked index is the other one.</summary>
        private static string Idx(int[] v) => "\"" + Canonical(v) + "\"";

        private void Measurements(string[] nets, (int[] V1, int[] V3)[] idx, List<string>[] parts, double[] freqs,
                                  int[] v1, int[] v3, double fout, int n)
        {
            var rows = r.Stages;
            string tonesDirective(bool two)
            {
                var names = new List<string>(two ? ["F1", "F2"] : ["Frf"]);
                if (bench == ChainBench.Blockers) names = [.. Enumerable.Range(1, d.Blockers.Count).Select(k => $"FB{k}")];
                names.AddRange(Enumerable.Range(1, Mixers).Select(i => $"Flo{i}"));
                return names.Count == 1 ? $"Tone=\"{names[0]}\" ToneUnit=Hz"
                     : $"NumFreqs={names.Count} " + string.Join(" ", names.Select((t, i) => $"Tone[{i + 1}]=\"{t}\" ToneUnit[{i + 1}]=Hz"));
            }
            // One LO variable per mixer STAGE (an array's per-branch mixers share it), in chain order.
            var mixerStages = d.Stages.Where(s => s.Part == ChainParts.Mixer).ToList();
            for (int i = 0; i < mixerStages.Count; i++) _vars.Add($"Flo{i + 1} = {G(ChainValue.Or(mixerStages[i].Get("Flo"), 0))}");
            int mix = mixerStages.Count;
            // The tones' frequencies, in the directive's order.
            double[] toneHz(bool two)
            {
                double frf = d.FrequencyHz, df = Math.Min(1e6, d.BandwidthHz);
                List<double> hz = bench == ChainBench.Blockers ? [.. d.Blockers.Select(b => frf + b.OffsetHz)]
                                : two ? [frf - df / 2, frf + df / 2] : [frf];
                hz.AddRange(mixerStages.Select(s => ChainValue.Or(s.Get("Flo"), 0)));
                return [.. hz];
            }
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
                        // The input sideband reaches the IF through the LOs alone: f_out − Σ v1·f_LO, which is +f_RF, or −f_RF
                        // (its conjugate) when an LO above the signal has inverted the spectrum. Through the RF tone's own
                        // index instead it would be a product of the drive, which carries no signal.
                        var sb = Enumerable.Range(0, v1.Length).Select(i => i == 0 ? 0 : -v1[i]);
                        // Order 1 + 2·mixers keeps each mixer's image sideband, where its isolator's termination is noisy.
                        _analyses.Add($"analysis HBN type=hb {tonesDirective(false)} MaxHarm=3 MaxMixOrder={Order("HBN", toneHz(false), 1 + 2 * mix, 1 + 2 * mix, "each mixer's image sideband", [v1], noise: true)} Tol=1e-12 " +
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
                    _analyses.Add($"analysis HB1 type=hb {tonesDirective(false)} MaxHarm=3 MaxMixOrder={Order("HB1", toneHz(false), 3 + 2 * mix, 1 + 2 * mix, "the output's compression", [v1])} Tol=1e-12");
                    _analyses.Add($"analysis SW1 type=parametric_sweep Var=Pin Values={F(lo - CompressionBackoffDb)},{F(lo - CompressionStepDb)},{F(lo + CompressionStepDb)} Inner=HB1");
                    _meas.Add($"Ghb = dB(HB1.V(\"out\",{p1})) + 10 - Pin");
                    _meas.Add("Comp = at(Ghb,\"Pin\",0) - Ghb");
                    _meas.Add("IP1dB_sim = at(Pin,\"Pin\",1) + (1 - at(Comp,\"Pin\",1))*(at(Pin,\"Pin\",2) - at(Pin,\"Pin\",1))/(at(Comp,\"Pin\",2) - at(Comp,\"Pin\",1))");
                    _meas.Add("OP1dB_sim = IP1dB_sim + at(Ghb,\"Pin\",0) - 1");
                    _meas.Add("IP1dB_err = IP1dB_sim - IP1dB_calc");
                    _meas.Add("OP1dB_err = OP1dB_sim - OP1dB_calc");
                    // The NF at each node, simulated (each stage's cumulative NF above) against the budget's, and the
                    // noise floor in the bandwidth that NF gives at the node through the budget's noise gain: cubes over
                    // the Levels display's node axis.
                    var lv = ChainLevels.Evaluate(d, r);
                    NodeAxis(lv.Count);
                    _meas.Add($"NF_node_sim = {NodeCube(lv.Count, k => k == 0 ? "0" : $"NF_{rows[k - 1].Name}_sim")}");
                    _meas.Add($"NF_node_calc = {NodeCube(lv.Count, k => G(lv[k].NfDb))}");
                    string floor(int k)
                    {
                        if (k == 0) return G(lv[0].NoiseDbm);
                        double gn = rows[k - 1].NoiseOutDbm - (10 * Math.Log10(ChainBudget.K * (d.TemperatureK + rows[k - 1].TeK) * d.BandwidthHz) + 30);
                        return $"(10*log10({G(ChainBudget.K)}*({G(d.TemperatureK)} + {G(ChainBudget.T0)}*(10^(NF_{rows[k - 1].Name}_sim/10) - 1))*{G(d.BandwidthHz)}) + 30 + {G(gn)})";
                    }
                    _meas.Add($"Noise_node_sim = {NodeCube(lv.Count, floor)}");
                    _meas.Add($"Noise_node_calc = {NodeCube(lv.Count, k => G(lv[k].NoiseDbm))}");
                    break;
                }
                case ChainBench.TwoTone:
                {
                    // Each LO raises a product's order by one, so the IF's IM5 is of order 5 + mixers; below it the IF
                    // stages' IM3 reads 0.01 dB off (measured on the superhet example: 0.012 dB at 5, 1.8e-4 dB at 6).
                    // What it reads: the signal at every stage's output, and the IM3 where the stage has an intercept.
                    var read2 = idx.Select(x => x.V1).Concat(idx.Where((_, j) => double.IsFinite(rows[j].Oip3Dbm)).Select(x => x.V3)).Concat([v1, v3]);
                    _analyses.Add($"analysis HB2 type=hb {tonesDirective(true)} MaxHarm=3 MaxMixOrder={Order("HB2", toneHz(true), 5 + mix, 3 + mix, "the output's IM3", read2)} Tol=1e-12");
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
                        _meas.Add($"P1_{s} = dB(HB2.V(\"{net}\",{Idx(idx[j].V1)})) + 10");
                        // A branch node carries one element's signal; after the combiner the array's.
                        _meas.Add($"G_{s}_sim = P1_{s} - P2t");
                        // A node before anything nonlinear has no intercept, and no IM3 to read one from.
                        if (!double.IsFinite(rows[j].Oip3Dbm)) continue;
                        _meas.Add($"OIP3_{s}_calc = {G(rows[j].Oip3Dbm)}");
                        _meas.Add($"P3_{s} = dB(HB2.V(\"{net}\",{Idx(idx[j].V3)})) + 10");
                        _meas.Add($"OIP3_{s}_sim = P1_{s} + (P1_{s} - P3_{s})/2");
                    }
                    break;
                }
                case ChainBench.Levels:
                {
                    // Two tones, each at the chain's input power: the signal and its IM3 at every node, on the 2-Tone
                    // bench's mixing order. The budget's levels beside them, and its noise floor, SNR, margin and a
                    // canceller's interference, for the display's node axis.
                    var levels = ChainLevels.Evaluate(d, r);
                    var nodeNet = NodeNets(nets);
                    var nodeV1 = new List<int[]> { Seed(1, 0) };
                    var nodeV3 = new List<int[]> { Seed(2, -1) };
                    for (int j = 0; j < rows.Count; j++) { nodeV1.Add(idx[j].V1); nodeV3.Add(idx[j].V3); }
                    int[] Seed(int a0, int a1) { var v = new int[v1.Length]; v[0] = a0; v[1] = a1; return v; }
                    _analyses.Add($"analysis HBL type=hb {tonesDirective(true)} MaxHarm=3 MaxMixOrder={Order("HBL", toneHz(true), 5 + mix, 3 + mix, "the output's IM3", nodeV1.Concat(nodeV3.Skip(1)))} Tol=1e-12");
                    for (int k = 0; k < nodeNet.Count; k++)
                    {
                        _meas.Add($"P1_{k} = dB(HBL.V(\"{nodeNet[k]}\",{Idx(nodeV1[k])})) + 10");
                        if (k > 0) _meas.Add($"P3_{k} = dB(HBL.V(\"{nodeNet[k]}\",{Idx(nodeV3[k])})) + 10");
                    }
                    NodeAxis(levels.Count);
                    double Lo(double v) => double.IsFinite(v) ? v : -300;
                    _meas.Add($"Psig_sim = {NodeCube(levels.Count, k => $"P1_{k}")}");
                    _meas.Add($"IM3_sim = {NodeCube(levels.Count, k => double.IsFinite(levels[k].Im3Dbm) ? $"P3_{k}" : "-300")}");
                    _meas.Add($"Psig_calc = {NodeCube(levels.Count, k => G(levels[k].SignalDbm))}");
                    _meas.Add($"IM3_calc = {NodeCube(levels.Count, k => G(Lo(levels[k].Im3Dbm)))}");
                    _meas.Add($"Noise_calc = {NodeCube(levels.Count, k => G(levels[k].NoiseDbm))}");
                    _meas.Add($"SNR_calc = {NodeCube(levels.Count, k => G(levels[k].SnrDb))}");
                    // A linear chain has no margin at all: above the display's window.
                    _meas.Add($"Margin_calc = {NodeCube(levels.Count, k => G(double.IsFinite(levels[k].MarginDb) ? levels[k].MarginDb : 300))}");
                    if (levels.Any(l => double.IsFinite(l.InterferenceDbm)))
                        _meas.Add($"Int_calc = {NodeCube(levels.Count, k => G(Lo(levels[k].InterferenceDbm)))}");
                    for (int k = 0; k < levels.Count; k++)
                    {
                        _meas.Add($"Psig_{k}_err = P1_{k} - {G(levels[k].SignalDbm)}");
                        if (double.IsFinite(levels[k].Im3Dbm)) _meas.Add($"IM3_{k}_err = P3_{k} - {G(levels[k].Im3Dbm)}");
                    }
                    break;
                }
                case ChainBench.Blockers:
                {
                    // The blockers alone, every one at its own frequency and level, through every stage; each node is a
                    // stage's output into the bench's isolator after it, so its voltage is the forward wave, which is
                    // what the budget carries (a reflective filter's stopband would otherwise stand on its input).
                    var levels = ChainLevels.Evaluate(d, r);
                    var nodeNet = NodeNets(nets);
                    var blkV = new List<List<int[]>>();
                    for (int b = 0; b < d.Blockers.Count; b++)
                    {
                        var v = new int[v1.Length]; v[b] = 1;
                        double fbk = d.FrequencyHz + d.Blockers[b].OffsetHz;
                        var perNode = new List<int[]> { (int[])v.Clone() };
                        int mi = 0;
                        foreach (var st in d.Stages)
                        {
                            if (st.Part == ChainParts.Mixer)
                            {
                                double flo = ChainValue.Or(st.Get("Flo"), 0);
                                int t = d.Blockers.Count + mi++;
                                if (fbk > flo) { v[t] -= 1; fbk -= flo; }
                                else { for (int i = 0; i < v.Length; i++) v[i] = -v[i]; v[t] += 1; fbk = flo - fbk; }
                            }
                            perNode.Add((int[])v.Clone());
                        }
                        blkV.Add(perNode);
                    }
                    _analyses.Add($"analysis HBK type=hb {tonesDirective(false)} MaxHarm=3 MaxMixOrder={Order("HBK", toneHz(false), 3 + mix, 1 + mix, "every blocker at the output", blkV.SelectMany(x => x))} Tol=1e-12");
                    for (int k = 0; k < nodeNet.Count; k++)
                        for (int b = 0; b < d.Blockers.Count; b++)
                            _meas.Add($"B{b + 1}_{k} = dB(HBK.V(\"{nodeNet[k]}\",{Idx(blkV[b][k])})) + 10");
                    NodeAxis(levels.Count);
                    for (int b = 0; b < d.Blockers.Count; b++)
                    {
                        int bb = b;
                        _meas.Add($"Blk{b + 1}_sim = {NodeCube(levels.Count, k => $"B{bb + 1}_{k}")}");
                        _meas.Add($"Blk{b + 1}_calc = {NodeCube(levels.Count, k => G(levels[k].BlockerDbm[bb]))}");
                        for (int k = 0; k < levels.Count; k++) _meas.Add($"Blk{b + 1}_{k}_err = B{b + 1}_{k} - {G(levels[k].BlockerDbm[b])}");
                    }
                    break;
                }
                case ChainBench.Demo:
                {
                    // The chain demodulated end to end at the output probe, the input power swept from noise-limited to
                    // compression-limited; the LO's phase noise on. Beside the simulated EVM, the budget's from its SNR,
                    // EVM ≈ 1/√SNR, which holds where noise dominates.
                    var (mhz, bocc) = Carrier(d.BandwidthHz);
                    EnvelopeFits((d.FrequencyHz, mhz * 1e6), (d.FrequencyHz + 2 * d.BandwidthHz, 2 * DemoDbpskRateBps));
                    _analyses.Add($"analysis ENVD type=env Stop=0.5e-3 Step=auto Accuracy=draft MaxHarm=1 MaxMixOrder={Math.Max(2, 1 + 2 * mix)} " +
                                  "Segments=auto Warmup=auto Start=0 Tol=1e-6 SaveWaveforms=no Noise=yes EvmSplit=yes");
                    _analyses.Add($"analysis SWPIN type=parametric_sweep Var=Pin Values={string.Join(",", DemoPowers(_t).Select(F))} Inner=ENVD");
                    double floor = 10 * Math.Log10(ChainBudget.K * (d.TemperatureK + _t.TeK) * bocc) + 30;
                    _meas.Add($"SNR_QAM_calc = Pin + {G(_t.ArrayGainDb)} - {G(floor)}");
                    _meas.Add("EVM_QAM_calc = 100*10^(-SNR_QAM_calc/20)");
                    _meas.Add("EVM_QAM_sim = ENVD.EVM_pct[:, \"Iout\", 0]");
                    _meas.Add("EVMdist_QAM = ENVD.EVMdist_pct[:, \"Iout\", 0]");
                    _meas.Add("EVMnoise_QAM = ENVD.EVMnoise_pct[:, \"Iout\", 0]");
                    _meas.Add("SNR_QAM_sim = ENVD.SNR_dB[:, \"Iout\", 0]");
                    _meas.Add("EVM_BPSK_sim = ENVD.EVM_pct[:, \"Iout\", 1]");
                    _meas.Add("SNR_BPSK_sim = ENVD.SNR_dB[:, \"Iout\", 1]");
                    _meas.Add("SNR_QAM_err = SNR_QAM_sim - SNR_QAM_calc");
                    break;
                }
                default:
                {
                    var (mhz, bocc) = Carrier(d.BandwidthHz);
                    EnvelopeFits((d.FrequencyHz, mhz * 1e6));
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

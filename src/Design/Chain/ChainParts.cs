// The parts a chain stage can be, and what each one does to a budget.
//
// SEVEN ARE THE SYSTEM LIBRARY'S OWN BLOCKS — Amp, Mixer, Atten, Filter, Switch, Circulator, Coupler — and a stage of
// one of them carries that block's own parameters, with the block's own defaults (read from ComponentTypeRegistry, so
// a stage and a freshly placed block cannot disagree). The splitter and the array combiner are the library's N-way
// Combiner block, an active combiner that block with an Amp after it. The library has no phase shifter or limiter, so
// TWO ARE COMPOSED of parts it does have, and the transfer places exactly that composition:
//
//   PhaseShifter  an Amp (gain −IL, its NF and intercept) and a TLIN whose electrical length is the phase
//   Limiter       an Amp (gain −IL, NF = IL) whose intercept puts its 1 dB compression point at P1dB
//   Splitter      a Combiner used from its common port, one output followed, the others terminated
//   Combiner      Direct: one N:1 Combiner; Tree: log2(N) levels of 2:1 Combiners; Active: an Amp after each
//   Canceller     a Coupler whose isolated port takes a reference path (an Amp for the amplitude, a Combiner path for
//                 a flat phase, a TLIN for the delay) that subtracts a copy of the interference from the main path
//
// And ONE IS DATA: an SnP stage is a Touchstone file on the library's SnP block, its budget read from the file at the
// stage's frequency (ChainSnp).
//
// The budget of each is closed form. A matched passive part at T0 has NF = its loss. An ideal Amp's and Mixer's
// 1 dB compression point is its tanh limiter's own, IIP3 − 8.9625 dB (src/Core/Devices/ThirdOrderLimiter.cs), which is
// what the transfer's simulation then measures. The combiner adds 10·log10(N) of COHERENT signal gain and none of noise
// gain: N element signals in phase add in amplitude, their uncorrelated noise in power. Each variant of the combiner is
// budgeted as the cascade of its own levels (CombinerSpec.Budget), so a tree's losses, gains, noise and intercepts
// accumulate level by level exactly as a chain's do.

using CircuitRF.Design.Schematic;

namespace CircuitRF.Design.Chain;

/// <summary>One parameter of a chain part: its name, its default text, the unit a bare number is in, and how it reads.</summary>
/// <param name="Options">For a parameter that names a choice (<c>IP3Ref</c>, <c>Response</c>), the choices.</param>
/// <param name="BlankAllowed">True when blank means something (an Amp's blank NF is a noiseless amplifier).</param>
/// <param name="IsFile">The parameter names a file, which the window offers a picker for.</param>
/// <param name="BudgetOnly">Read by the budget and never written onto the part's netlist line (a DC power, a gain per
/// amplifier stage).</param>
public sealed record ChainParam(string Name, string Default, string Unit, bool Numeric = true, bool BlankAllowed = false,
                                IReadOnlyList<string>? Options = null, string Help = "", bool IsFile = false,
                                bool BudgetOnly = false);

/// <summary>Where a stage sits in its chain: the frequency at its INPUT (the chain's, or a mixer's output after one),
/// Hz, and the directory a relative file reference resolves against (null for a chain that has none).</summary>
public sealed record ChainStageContext(double FrequencyHz, string? BaseDirectory);

/// <summary>What one stage does to a budget, on its own.</summary>
/// <param name="GainDb">Signal (available-power) gain, dB. A Combiner's includes its coherent 10·log10(N).</param>
/// <param name="NoiseGainDb">The gain uncorrelated noise sees, dB: the signal gain everywhere but a Combiner.</param>
/// <param name="NfDb">The stage's noise figure at T0, referred to its input, dB.</param>
/// <param name="Iip3Dbm">Input-referred third-order intercept, dBm; +∞ for a linear stage.</param>
/// <param name="Ip1dbDbm">Input-referred 1 dB compression point, dBm; +∞ for a linear stage.</param>
/// <param name="ArrayN">N for a Combiner, 1 otherwise.</param>
public sealed record StageBudget(double GainDb, double NoiseGainDb, double NfDb, double Iip3Dbm, double Ip1dbDbm, int ArrayN,
                                 IReadOnlyList<string> Notes);

/// <summary>A chain part: its token in a <c>.cchain</c>, what it is called, whether the library has it as one block, and
/// its budget.</summary>
public sealed record ChainPart(string Token, string Title, bool BuiltIn, IReadOnlyList<ChainParam> Parameters,
                               string Summary, Func<ChainStage, ChainStageContext, StageBudget> Budget)
{
    public string Default(string key) => Parameters.FirstOrDefault(p => p.Name == key)?.Default ?? "";
}

public static class ChainParts
{
    public const string Amp = "Amp", Mixer = "Mixer", Atten = "Atten", Filter = "Filter", Switch = "Switch",
                        Circulator = "Circulator", Coupler = "Coupler", PhaseShifter = "PhaseShifter",
                        Limiter = "Limiter", Splitter = "Splitter", Combiner = "Combiner", Canceller = "Canceller",
                        Snp = "SnP";

    /// <summary>tanh's 1 dB compression point below its input intercept, dB — the ideal Amp's and Mixer's own
    /// (<c>ThirdOrderLimiter</c>; system-components.md, Amplifier).</summary>
    public const double TanhP1dbBelowIip3 = 8.9625;

    /// <summary>The Amp's intercept at or above which it is exactly linear (<c>AmplifierModel</c>).</summary>
    public const double AmpLinearDbm = 190;

    /// <summary>The Mixer's, likewise (<c>MixerModel</c>).</summary>
    public const double MixerLinearDbm = 90;

    /// <summary>The noise figure a noiseless double-sideband multiplier has single-sideband: 10·log10(2).</summary>
    public static readonly double NoiselessMixerSsbDb = 10 * Math.Log10(2);

    public static ChainPart? Find(string token) => All.FirstOrDefault(p => p.Token == token);

    /// <summary>A built-in block's parameter, its default read from the block's own placement defaults.</summary>
    private static ChainParam Reg(SymbolKind kind, string name, bool numeric = true, bool blank = false,
                                  IReadOnlyList<string>? options = null, string help = "")
    {
        var d = ComponentTypeRegistry.DefaultParameters(kind, 0).First(p => p.Name == name);
        // A frequency default carries its unit in its text, since the text is what the netlist line carries.
        string text = d.Expression.Length > 0 && d.Unit is "GHz" or "MHz" or "kHz" ? $"{d.Expression} {d.Unit}" : d.Expression;
        return new ChainParam(name, text, d.Unit, numeric, blank, options, help);
    }

    private static double Num(ChainStage s, string key, double fallback = 0) => ChainValue.Or(s.Get(key), fallback);

    private static StageBudget Passive(double lossDb, params string[] notes)
        => new(-lossDb, -lossDb, lossDb, double.PositiveInfinity, double.PositiveInfinity, 1, notes);

    private static double P1(double iip3) => double.IsPositiveInfinity(iip3) ? iip3 : iip3 - TanhP1dbBelowIip3;

    /// <summary>Every part's DC power, which only the budget's power readout reads.</summary>
    public static readonly ChainParam Pdc = new("Pdc", "", "W", BlankAllowed: true, BudgetOnly: true,
        Help: "DC power, W or with its unit (5 mW); with GainPerStage, per amplifier stage; blank: none");

    /// <summary>An Amp's gain per amplifier stage, which its stage count follows from.</summary>
    public static readonly ChainParam GainPerStage = new("GainPerStage", "", "dB", BlankAllowed: true, BudgetOnly: true,
        Help: "the gain of one amplifier stage: Gain takes ceil(Gain / GainPerStage) of them, each drawing Pdc; blank: Pdc is the whole amplifier's");

    /// <summary>The stages an Amp of <paramref name="gainDb"/> takes at <paramref name="perStageDb"/> each,
    /// ⌈G/g − 10⁻⁹⌉ (the 10⁻⁹ so that 20/5 is 4, not 5, after rounding); null when either is not above 0 dB.</summary>
    public static int? StageCount(double gainDb, double perStageDb)
        => gainDb > 0 && perStageDb > 0 ? (int)Math.Ceiling(gainDb / perStageDb - 1e-9) : null;

    /// <summary>A resolved stage's amplifier-stage count, null when it states no GainPerStage.</summary>
    public static int? StageCount(ChainStage s)
        => s.Part == Amp && ChainValue.TryParse(s.Get("GainPerStage"), out double g) ? StageCount(ChainValue.Or(s.Get("Gain"), 0), g) : null;

    /// <summary>A resolved stage's DC power, W: its Pdc, times its stage count when it has one.</summary>
    public static double StagePdcW(ChainStage s) => ChainValue.Or(s.Get("Pdc"), 0) * (StageCount(s) ?? 1);

    public static readonly IReadOnlyList<ChainPart> All = [.. Parts().Select(p => p with { Parameters = [.. p.Parameters, Pdc] })];

    private static IReadOnlyList<ChainPart> Parts() =>
    [
        new(Amp, "Amplifier", true,
            [Reg(SymbolKind.Amp, "Gain"), Reg(SymbolKind.Amp, "NF", blank: true, help: "blank: noiseless"),
             Reg(SymbolKind.Amp, "IP3"), Reg(SymbolKind.Amp, "IP3Ref", numeric: false, options: ["Output", "Input"]), GainPerStage],
            "An ideal gain block. P1dB follows from IP3: IIP3 − 8.96 dB. Any gain, negative included, so it is also the " +
            "stage for any other block that has a gain, a noise figure and an intercept.",
            (s, _) =>
            {
                double g = Num(s, "Gain"), ip = Num(s, "IP3", 40);
                bool blankNf = s.Get("NF").Trim().Length == 0;
                double iip3 = ip >= AmpLinearDbm ? double.PositiveInfinity
                            : s.Get("IP3Ref").Trim().Equals("Input", StringComparison.OrdinalIgnoreCase) ? ip : ip - g;
                return new(g, g, blankNf ? 0 : Num(s, "NF"), iip3, P1(iip3), 1,
                           blankNf ? [$"{s.Name}: NF is blank, so the amplifier is noiseless."] : []);
            }),
        new(Mixer, "Mixer", true,
            [Reg(SymbolKind.Mixer, "ConvGain"), Reg(SymbolKind.Mixer, "NF", blank: true, help: "SSB or DSB, as NFType says"),
             Reg(SymbolKind.Mixer, "NFType", numeric: false, options: ["ssb", "dsb"]), Reg(SymbolKind.Mixer, "IIP3"),
             Reg(SymbolKind.Mixer, "Plo"),
             new("Flo", "2.1 GHz", "Hz", Help: "the LO frequency; the output is the difference |f − Flo|")],
            "An ideal multiplier, down-converting to |f − Flo|. Its noise is its single-sideband figure: the image is " +
            "taken as terminated at T0 and filtered of every earlier stage's noise.",
            (s, _) =>
            {
                double g = Num(s, "ConvGain"), ip = Num(s, "IIP3", 100);
                var notes = new List<string>();
                double nf;
                if (s.Get("NF").Trim().Length == 0) { nf = NoiselessMixerSsbDb; notes.Add($"{s.Name}: NF is blank, so the mixer is a noiseless multiplier: 3.01 dB single-sideband."); }
                else
                {
                    nf = Num(s, "NF");
                    if (s.Get("NFType").Trim().Equals("dsb", StringComparison.OrdinalIgnoreCase)) nf += NoiselessMixerSsbDb;
                    if (nf < NoiselessMixerSsbDb)
                    {
                        notes.Add($"{s.Name}: an SSB NF below 3.01 dB is below a noiseless mixer's; it is taken as 3.01 dB, as the run takes it.");
                        nf = NoiselessMixerSsbDb;
                    }
                }
                double iip3 = ip >= MixerLinearDbm ? double.PositiveInfinity : ip;
                return new(g, g, nf, iip3, P1(iip3), 1, notes);
            }),
        new(Atten, "Attenuator", true, [Reg(SymbolKind.Atten, "Loss")],
            "A matched pad. Its noise figure is its loss.", (s, _) => Passive(Num(s, "Loss"))),
        new(Filter, "Filter", true,
            [Reg(SymbolKind.Filter, "Response", numeric: false, options: ["Butterworth", "Chebyshev", "InvChebyshev", "Bessel", "Elliptic"]),
             Reg(SymbolKind.Filter, "Form", numeric: false, options: ["Bandpass", "Lowpass", "Highpass"]),
             Reg(SymbolKind.Filter, "Order"), Reg(SymbolKind.Filter, "Fc"), Reg(SymbolKind.Filter, "F1"),
             Reg(SymbolKind.Filter, "F2"), Reg(SymbolKind.Filter, "Ripple"), Reg(SymbolKind.Filter, "Astop"),
             Reg(SymbolKind.Filter, "IL")],
            "A synthesised filter. In its passband the budget is its IL, and its noise figure is that loss; " +
            "a Chebyshev's ripple is not in the budget.",
            (s, _) => Passive(Num(s, "IL"))),
        new(Switch, "Switch", true, [Reg(SymbolKind.Switch, "IL")],
            "A closed SPST switch. Its noise figure is its insertion loss.", (s, _) => Passive(Num(s, "IL"))),
        new(Circulator, "Isolator (Circulator)", true, [Reg(SymbolKind.Circulator, "IL")],
            "A circulator with its third port terminated: an isolator. Its noise figure is its insertion loss.",
            (s, _) => Passive(Num(s, "IL"))),
        new(Coupler, "Directional coupler (through arm)", true, [Reg(SymbolKind.Coupler, "Coupling"), Reg(SymbolKind.Coupler, "IL")],
            "The through arm of a directional coupler, coupled and isolated ports terminated: the coupled power and IL " +
            "are both loss.",
            (s, _) => Passive(-10 * Math.Log10(1 - Math.Pow(10, -Num(s, "Coupling", 20) / 10)) + Num(s, "IL"))),
        new(PhaseShifter, "Phase shifter (composed)", false,
            [new("Phase", "0", "deg", Help: "insertion phase; an array adds each element's steering to it"),
             new("IL", "5", "dB", Help: "insertion loss"),
             new("NF", "", "dB", BlankAllowed: true, Help: "blank: equal to IL, a passive shifter"),
             new("IIP3", "", "dBm", BlankAllowed: true, Help: "blank: linear")],
            "An Amp of gain −IL with this NF and IIP3, and a TLIN whose electrical length is the phase.",
            (s, _) =>
            {
                double il = Num(s, "IL");
                double nf = s.Get("NF").Trim().Length == 0 ? il : Num(s, "NF");
                double iip3 = s.Get("IIP3").Trim().Length == 0 ? double.PositiveInfinity : Num(s, "IIP3");
                return new(-il, -il, nf, iip3, P1(iip3), 1, []);
            }),
        new(Limiter, "Limiter (composed)", false,
            [new("IL", "0.5", "dB", Help: "insertion loss"), new("P1dB", "10", "dBm", Help: "input 1 dB compression point")],
            "An Amp of gain −IL and NF = IL whose intercept puts its input 1 dB compression point at P1dB.",
            (s, _) =>
            {
                double il = Num(s, "IL"), p1 = Num(s, "P1dB", 10);
                return new(-il, -il, il, p1 + TanhP1dbBelowIip3, p1, 1, []);
            }),
        new(Splitter, "N-way splitter", true,
            [new("N", "2", "", Help: "number of ways"), new("IL", "0.3", "dB", Help: "excess loss above the split")],
            "The library's N-way Combiner used from its common port, one output followed and the others terminated: a " +
            "loss of 10·log10(N) + IL, and that loss is its noise figure.",
            (s, _) =>
            {
                int n = Math.Max(2, (int)Math.Round(Num(s, "N", 2)));
                return Passive(10 * Math.Log10(n) + Num(s, "IL"));
            }),
        new(Canceller, "Canceller (composed)", false,
            [new("Coupling", "10", "dB", Help: "the injection coupler's coupling; the main path's through loss follows from it"),
             new("IL", "0.2", "dB", Help: "the coupler's excess loss"),
             new("Pint", "-30", "dBm", Help: "the interference at the canceller's main input"),
             new("Bint", "20 MHz", "Hz", Help: "the interference's bandwidth"),
             new("Pref", "0", "dBm", Help: "the reference input: the interfering signal at its source"),
             new("NFref", "", "dB", BlankAllowed: true, Help: "the reference path's noise figure; blank: passive"),
             new("AmpErr", "0.1", "dB", Help: "the cancellation path's amplitude error"),
             new("PhaseErr", "1", "deg", Help: "its phase error"),
             new("DelayErr", "0", "s", Help: "its delay mismatch against the interference's path")],
            "A reference path — amplitude, phase and delay — subtracted from the main path through a directional " +
            "coupler. Its depth is set by the amplitude and phase error and, over the interference's bandwidth, the delay " +
            "mismatch; on the wanted signal it is the coupler's through loss and the noise the reference path injects.",
            (s, _) => CancellerSpec.Read(s).Budget()),
        new(Snp, "S-parameter data (SnP)", true,
            [new("File", "", "", Numeric: false, Help: "a Touchstone file (.s2p, .snp); a relative path is from the workspace", IsFile: true),
             new("In", "1", "", Help: "the file's port the chain enters at"),
             new("Out", "2", "", Help: "the file's port the chain leaves from; every other port is terminated in 50 Ω"),
             new("NF", "", "dB", BlankAllowed: true, Help: "blank: from the file, its noise data or a passive network's own loss"),
             new("IIP3", "", "dBm", BlankAllowed: true, Help: "blank: linear")],
            "Measured or simulated S-parameters from a Touchstone file, interpolated to the stage's frequency (after a " +
            "mixer, the IF) as the SnP block interpolates them. Its gain is |S(Out,In)|² between 50 Ω ports; its noise " +
            "figure is the file's noise data at a 50 Ω source, or a passive network's own loss when the file has none.",
            (s, c) => ChainSnp.Budget(s, c)),
        new(Combiner, "N-way array combiner", true,
            [new("N", "4", "", Help: "number of elements; every stage before this one is one element's branch"),
             new("Topology", "Direct", "", Numeric: false, Options: ["Direct", "Tree", "Levels"],
                 Help: "Direct: one N:1 combiner. Tree: log2(N) levels of 2:1 combiners, N a power of 2. Levels: one N:1 " +
                       "combiner, any N, with the loss of ceil(log2 N) levels of IL each"),
             new("Kind", "Passive", "", Numeric: false, Options: ["Passive", "Active"],
                 Help: "Active: an amplifier after each combiner"),
             new("IL", "0.5", "dB", Numeric: false,
                 Help: "each combiner's loss above the ideal split; a tree: one value for every level, or one per level"),
             new("PathIL", "", "dB", Numeric: false, BlankAllowed: true,
                 Help: "passive only: each element path's own extra loss, N values (a taper); blank: none"),
             new("Gain", "10", "dB", Numeric: false, Help: "active only: each combiner's amplifier; one value, or one per level"),
             new("NF", "2", "dB", Numeric: false, Help: "active only: each amplifier's noise figure; one value, or one per level"),
             new("IIP3", "10", "dBm", Numeric: false, Help: "active only: each amplifier's input intercept; one value, or one per level"),
             new("Steer", "0", "deg", Help: "phase step between adjacent elements of the arriving wave; the phase shifters remove it")],
            "N identical branches combined in phase: signal +10·log10(N), uncorrelated noise not, so the SNR improves by " +
            "10·log10(N). Direct is one N:1 combiner, Tree log2(N) levels of 2:1 ones; a passive combiner's noise figure " +
            "is its loss, an active one cascades its amplifiers level by level.",
            (s, _) => ReadCombiner(s).Budget()),
    ];

    // ── the combiner's variants ──────────────────────────────────────────────

    /// <summary>A text list of numbers, comma- or semicolon-separated, each with or without its unit.</summary>
    internal static bool TryList(string text, out List<double> values)
    {
        values = [];
        foreach (var item in text.Split([',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!ChainValue.TryParse(item, out double v)) return false;
            values.Add(v);
        }
        return true;
    }

    /// <summary>A Combiner stage, read; the refusals that stop it are listed and the rest defaulted.</summary>
    public static CombinerSpec ReadCombiner(ChainStage s, List<string>? refusals = null)
    {
        void No(string why) => refusals?.Add($"Stage '{s.Name}': {why}");
        int n = Math.Max(2, (int)Math.Round(Num(s, "N", 4)));
        string topo = s.Get("Topology").Trim(), kind = s.Get("Kind").Trim();
        bool tree = topo.Equals("Tree", StringComparison.OrdinalIgnoreCase);
        bool active = kind.Equals("Active", StringComparison.OrdinalIgnoreCase);
        // Levels: the loss of the ceil(log2 N) levels a tree of any N would take, on one N:1 combiner.
        int lossLevels = 0;
        if (topo.Equals("Levels", StringComparison.OrdinalIgnoreCase)) while ((1 << lossLevels) < n) lossLevels++;
        else if (topo.Length > 0 && !tree && !topo.Equals("Direct", StringComparison.OrdinalIgnoreCase))
            No($"Topology = '{topo}' is neither Direct nor Tree, nor Levels.");
        if (kind.Length > 0 && !active && !kind.Equals("Passive", StringComparison.OrdinalIgnoreCase))
            No($"Kind = '{kind}' is neither Passive nor Active.");
        if (tree && (n & (n - 1)) != 0)
        {
            No($"a tree combines in pairs, so its N is a power of 2 (2, 4, 8, …), and {n} is not. Set Topology to Direct " +
               $"for {n} elements, or to Levels for one combiner with the loss of a tree's levels.");
            tree = false;
        }
        int levels = tree ? (int)Math.Round(Math.Log2(n)) : 1;

        List<double> PerLevel(string key, string fallback)
        {
            string text = s.Get(key).Trim().Length > 0 ? s.Get(key) : fallback;
            if (!TryList(text, out var v) || v.Count == 0) { No($"{key} = '{s.Get(key)}' is not a number or a list of numbers."); v = [0]; }
            if (v.Count != 1 && v.Count != levels)
            {
                No($"{key} lists {v.Count} values, and this combiner has {levels} level{(levels == 1 ? "" : "s")}: give one value " +
                   "for every level, or one per level.");
                v = [v[0]];
            }
            return v.Count == 1 ? [.. Enumerable.Repeat(v[0], levels)] : v;
        }
        var il = PerLevel("IL", "0");
        if (lossLevels > 0) il = [il[0] * lossLevels];
        var gain = active ? PerLevel("Gain", "10") : [];
        var nf = active ? PerLevel("NF", "2") : [];
        var ip3 = active ? PerLevel("IIP3", "10") : [];

        List<double> path = [];
        if (s.Get("PathIL").Trim().Length > 0)
        {
            if (!TryList(s.Get("PathIL"), out path)) No($"PathIL = '{s.Get("PathIL")}' is not a list of numbers.");
            else if (path.Count != n) No($"PathIL lists {path.Count} losses for {n} element paths: give one per element.");
            else if (active) No("PathIL is a passive combiner's: an active one's paths are its amplifiers, and equal.");
            if (path.Count != n || active) path = [];
        }

        var lv = new List<CombinerLevel>();
        for (int l = 0; l < levels; l++)
            lv.Add(new CombinerLevel(tree ? 2 : n, tree ? n >> (l + 1) : 1, il[l],
                                     active ? gain[l] : 0, active ? nf[l] : 0, active ? ip3[l] : double.PositiveInfinity));
        return new CombinerSpec(n, tree, active, lv, path, lossLevels);
    }
}

/// <summary>One level of a combiner: <paramref name="Parts"/> combiners of <paramref name="Ways"/> inputs each, their
/// loss above the ideal split, and (active) the amplifier after each.</summary>
public sealed record CombinerLevel(int Ways, int Parts, double IlDb, double GainDb, double NfDb, double Iip3Dbm);

/// <summary>
/// A Combiner stage's variant: Direct (one N:1 level) or Tree (log2 N levels of 2:1), Passive or Active, and each
/// element path's own loss.
///
/// <para><b>Its budget, per element, is the cascade of its levels.</b> Write a_k = 10^(−PathIL_k/20). The element paths
/// first: N inputs of power P with these weights combine coherently to (Σa_k)²·P/N and their uncorrelated noise to
/// (Σa_k²/N)·n², and the weighting itself is a matched passive network at T0, so it adds T0·(N/Σa_k² − 1) referred to
/// its input (Bosma's theorem on its S). Then each level: a passive n-way combiner of loss L adds n of coherent signal
/// gain over 1/L, 1/L of noise gain and T0·(L − 1) of noise; an active level's amplifier follows with its gain, noise
/// figure and intercept. Signal and noise gains, noise temperatures and intercepts cascade through the levels as they do
/// through a chain (Friis with the noise gains, the intercept sum with the signal gains), which gives the stage's
/// signal gain, noise gain, noise figure and intercept, all referred to ONE element's input. The array gain
/// is the signal gain over the noise gain, (Σa_k)²/Σa_k², N for equal paths.</para>
/// </summary>
/// <param name="LossLevels">Levels only: the ceil(log2 N) levels whose loss its one combiner takes; 0 otherwise.</param>
public sealed record CombinerSpec(int N, bool Tree, bool Active, IReadOnlyList<CombinerLevel> Levels,
                                  IReadOnlyList<double> PathIlDb, int LossLevels = 0)
{
    /// <summary>Path k's amplitude weight (1-based), 1 with no PathIL.</summary>
    public double Weight(int k) => PathIlDb.Count == N ? Math.Pow(10, -PathIlDb[k - 1] / 20) : 1;

    public StageBudget Budget()
    {
        static double Lin(double db) => Math.Pow(10, db / 10);
        static double Db(double x) => 10 * Math.Log10(x);
        const double t0 = ChainBudget.T0;
        double gs = 1, gn = 1, te = 0, inv3 = 0, inv1 = 0;
        void Sub(double sGain, double nGain, double teIn, double iip3Mw)
        {
            te += teIn / gn;
            if (double.IsFinite(iip3Mw)) { inv3 += gs / iip3Mw; inv1 += gs / (iip3Mw / Lin(ChainParts.TanhP1dbBelowIip3)); }
            gs *= sGain; gn *= nGain;
        }
        double sa = 0, sa2 = 0;
        for (int k = 1; k <= N; k++) { sa += Weight(k); sa2 += Weight(k) * Weight(k); }
        // The weights, as a share of the equal-path combination: coherent (Σa)²/N², incoherent Σa²/N.
        double wn = sa2 / N;
        Sub(sa * sa / ((double)N * N), wn, t0 * (1 / wn - 1), double.PositiveInfinity);
        foreach (var l in Levels)
        {
            double g = Lin(-l.IlDb);
            Sub(l.Ways * g, g, t0 * (1 / g - 1), double.PositiveInfinity);
            if (Active) Sub(Lin(l.GainDb), Lin(l.GainDb), t0 * (Lin(l.NfDb) - 1),
                            l.Iip3Dbm >= ChainParts.AmpLinearDbm ? double.PositiveInfinity : Lin(l.Iip3Dbm));
        }
        return new StageBudget(Db(gs), Db(gn), Db(1 + te / t0),
                               inv3 > 0 ? -Db(inv3) : double.PositiveInfinity, inv1 > 0 ? -Db(inv1) : double.PositiveInfinity,
                               N, []);
    }

    /// <summary>"Direct, passive", "Tree of 3 levels, active", as the block view and the variants bench say it.</summary>
    public string Describe() => (Tree ? $"Tree of {Levels.Count} level{(Levels.Count == 1 ? "" : "s")} of 2:1"
                                 : LossLevels > 0 ? $"{N}:1, {LossLevels} levels' loss" : $"Direct {N}:1")
                                + (Active ? ", active" : ", passive");
}

/// <summary>
/// A canceller stage read: a directional coupler of coupling c (linear, 10^(−C/10)) and excess loss L_ex, its main
/// path the through arm, and a reference path into its isolated port that subtracts a copy of the interference.
///
/// <para><b>On the wanted signal</b> it is the through arm, loss L_t = L_ex/(1 − c), and the noise the reference
/// path injects: an output at T0·F_ref·G_ref (its input at T0) coupled by c/(1 − c) relative to the through arm adds
/// T0·(F_ref·G_ref − 1)·c/(1 − c) at the input, so F = L_t + (F_ref·G_ref − 1)·c/(1 − c). G_ref is the gain the
/// path needs to cancel: G_ref·P_ref·c = P_int·(1 − c). A passive path (F_ref·G_ref = 1) adds nothing.</para>
///
/// <para><b>On the interference</b> the residual is the fraction R of it the subtraction leaves. With an amplitude
/// ratio a = 10^(AmpErr/20), a phase error φ (an advance) and a delay mismatch τ (the reference path later), a carrier of
/// bandwidth B and flat spectrum, centred ν from where the phase was set, leaves</para>
/// <code>
///   R = 1 + a² − 2a·cos(φ − 2πντ)·sinc(πBτ),      sinc x = sin x / x
/// </code>
/// <para>the average over the band of |1 − a·e^(j(φ − 2πfτ))|². Its depth is −10·log10 R: 33.5 dB at 1° and 0.1 dB;
/// with 1 ns of delay mismatch alone, 40.8 dB over 5 MHz, 28.8 dB over 20 MHz and 22.8 dB over 40 MHz.</para>
/// </summary>
public sealed record CancellerSpec(double CouplingDb, double IlDb, double PintDbm, double BintHz, double PrefDbm,
                                   double? NfRefDb, double AmpErrDb, double PhaseErrDeg, double DelayErrS)
{
    public static CancellerSpec Read(ChainStage s) => new(
        ChainValue.Or(s.Get("Coupling"), 10), ChainValue.Or(s.Get("IL"), 0), ChainValue.Or(s.Get("Pint"), -30),
        ChainValue.Or(s.Get("Bint"), 20e6), ChainValue.Or(s.Get("Pref"), 0),
        s.Get("NFref").Trim().Length == 0 ? null : ChainValue.Or(s.Get("NFref"), 0),
        ChainValue.Or(s.Get("AmpErr"), 0), ChainValue.Or(s.Get("PhaseErr"), 0), ChainValue.Or(s.Get("DelayErr"), 0));

    private double C => Math.Pow(10, -CouplingDb / 10);

    /// <summary>The main path's through loss, dB: the coupled power and the excess loss.</summary>
    public double ThroughLossDb => IlDb - 10 * Math.Log10(1 - C);

    /// <summary>The reference path's gain the subtraction needs, dB.</summary>
    public double RefGainDb => PintDbm + 10 * Math.Log10((1 - C) / C) - PrefDbm;

    public StageBudget Budget()
    {
        double lt = Math.Pow(10, ThroughLossDb / 10);
        double fr = NfRefDb is { } nf ? Math.Pow(10, nf / 10) * Math.Pow(10, RefGainDb / 10) : 1;
        double f = lt + (fr - 1) * C / (1 - C);
        return new StageBudget(-ThroughLossDb, -ThroughLossDb, 10 * Math.Log10(f), double.PositiveInfinity,
                               double.PositiveInfinity, 1, []);
    }

    /// <summary>The fraction of the interference left, for these errors over a band <paramref name="bwHz"/> wide centred
    /// <paramref name="offsetHz"/> from where the phase was set.</summary>
    public static double Residual(double ampErrDb, double phaseErrDeg, double delayErrS, double bwHz, double offsetHz = 0)
    {
        double a = Math.Pow(10, ampErrDb / 20), x = Math.PI * bwHz * delayErrS;
        double sinc = Math.Abs(x) < 1e-12 ? 1 : Math.Sin(x) / x;
        return 1 + a * a - 2 * a * Math.Cos(phaseErrDeg * Math.PI / 180 - 2 * Math.PI * offsetHz * delayErrS) * sinc;
    }

    /// <summary>The cancellation depth, dB.</summary>
    public double DepthDb => -10 * Math.Log10(Residual(AmpErrDb, PhaseErrDeg, DelayErrS, BintHz));

    /// <summary>The residual interference at the canceller's output, dBm.</summary>
    public double ResidualDbm => PintDbm - ThroughLossDb - DepthDb;
}

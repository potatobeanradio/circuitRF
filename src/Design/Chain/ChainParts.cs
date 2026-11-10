// The parts a chain stage can be, and what each one does to a budget.
//
// SEVEN ARE THE SYSTEM LIBRARY'S OWN BLOCKS — Amp, Mixer, Atten, Filter, Switch, Circulator, Coupler — and a stage of
// one of them carries that block's own parameters, with the block's own defaults (read from ComponentTypeRegistry, so
// a stage and a freshly placed block cannot disagree). The library has no phase shifter, limiter or N-way
// splitter/combiner, so FOUR ARE COMPOSED of parts it does have, and the transfer places exactly that composition:
//
//   PhaseShifter  an Amp (gain −IL, its NF and intercept) and a TLIN whose electrical length is the phase
//   Limiter       an Amp (gain −IL, NF = IL) whose intercept puts its 1 dB compression point at P1dB
//   Splitter      an N-way Wilkinson divider of TLINs and resistors, one output used, the others terminated
//   Combiner      the same N-way Wilkinson, every input used: the array combiner
//
// The budget of each is closed form. A matched passive part at T0 has NF = its loss. An ideal Amp's and Mixer's
// 1 dB compression point is its tanh limiter's own, IIP3 − 8.9625 dB (src/Core/Devices/ThirdOrderLimiter.cs), which is
// what the transfer's simulation then measures. The combiner adds 10·log10(N) of COHERENT signal gain and none of noise
// gain: N element signals in phase add in amplitude, their uncorrelated noise in power (Wilkinson 1960; for the array
// gain, Van Trees, Optimum Array Processing, §2.2).

using CircuitRF.Design.Schematic;

namespace CircuitRF.Design.Chain;

/// <summary>One parameter of a chain part: its name, its default text, the unit a bare number is in, and how it reads.</summary>
/// <param name="Options">For a parameter that names a choice (<c>IP3Ref</c>, <c>Response</c>), the choices.</param>
/// <param name="BlankAllowed">True when blank means something (an Amp's blank NF is a noiseless amplifier).</param>
public sealed record ChainParam(string Name, string Default, string Unit, bool Numeric = true, bool BlankAllowed = false,
                                IReadOnlyList<string>? Options = null, string Help = "");

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
                               string Summary, Func<ChainStage, StageBudget> Budget)
{
    public string Default(string key) => Parameters.FirstOrDefault(p => p.Name == key)?.Default ?? "";
}

public static class ChainParts
{
    public const string Amp = "Amp", Mixer = "Mixer", Atten = "Atten", Filter = "Filter", Switch = "Switch",
                        Circulator = "Circulator", Coupler = "Coupler", PhaseShifter = "PhaseShifter",
                        Limiter = "Limiter", Splitter = "Splitter", Combiner = "Combiner";

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

    public static readonly IReadOnlyList<ChainPart> All =
    [
        new(Amp, "Amplifier", true,
            [Reg(SymbolKind.Amp, "Gain"), Reg(SymbolKind.Amp, "NF", blank: true, help: "blank: noiseless"),
             Reg(SymbolKind.Amp, "IP3"), Reg(SymbolKind.Amp, "IP3Ref", numeric: false, options: ["Output", "Input"])],
            "An ideal gain block. P1dB follows from IP3: IIP3 − 8.96 dB. Any gain, negative included, so it is also the " +
            "stage for any other block that has a gain, a noise figure and an intercept.",
            s =>
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
            s =>
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
            "A matched pad. Its noise figure is its loss.", s => Passive(Num(s, "Loss"))),
        new(Filter, "Filter", true,
            [Reg(SymbolKind.Filter, "Response", numeric: false, options: ["Butterworth", "Chebyshev", "InvChebyshev", "Bessel", "Elliptic"]),
             Reg(SymbolKind.Filter, "Form", numeric: false, options: ["Bandpass", "Lowpass", "Highpass"]),
             Reg(SymbolKind.Filter, "Order"), Reg(SymbolKind.Filter, "Fc"), Reg(SymbolKind.Filter, "F1"),
             Reg(SymbolKind.Filter, "F2"), Reg(SymbolKind.Filter, "Ripple"), Reg(SymbolKind.Filter, "Astop"),
             Reg(SymbolKind.Filter, "IL")],
            "A synthesised filter. In its passband the budget is its IL, and its noise figure is that loss; " +
            "a Chebyshev's ripple is not in the budget.",
            s => Passive(Num(s, "IL"))),
        new(Switch, "Switch", true, [Reg(SymbolKind.Switch, "IL")],
            "A closed SPST switch. Its noise figure is its insertion loss.", s => Passive(Num(s, "IL"))),
        new(Circulator, "Isolator (Circulator)", true, [Reg(SymbolKind.Circulator, "IL")],
            "A circulator with its third port terminated: an isolator. Its noise figure is its insertion loss.",
            s => Passive(Num(s, "IL"))),
        new(Coupler, "Directional coupler (through arm)", true, [Reg(SymbolKind.Coupler, "Coupling"), Reg(SymbolKind.Coupler, "IL")],
            "The through arm of a directional coupler, coupled and isolated ports terminated: the coupled power and IL " +
            "are both loss.",
            s => Passive(-10 * Math.Log10(1 - Math.Pow(10, -Num(s, "Coupling", 20) / 10)) + Num(s, "IL"))),
        new(PhaseShifter, "Phase shifter (composed)", false,
            [new("Phase", "0", "deg", Help: "insertion phase; an array adds each element's steering to it"),
             new("IL", "5", "dB", Help: "insertion loss"),
             new("NF", "", "dB", BlankAllowed: true, Help: "blank: equal to IL, a passive shifter"),
             new("IIP3", "", "dBm", BlankAllowed: true, Help: "blank: linear")],
            "An Amp of gain −IL with this NF and IIP3, and a TLIN whose electrical length is the phase.",
            s =>
            {
                double il = Num(s, "IL");
                double nf = s.Get("NF").Trim().Length == 0 ? il : Num(s, "NF");
                double iip3 = s.Get("IIP3").Trim().Length == 0 ? double.PositiveInfinity : Num(s, "IIP3");
                return new(-il, -il, nf, iip3, P1(iip3), 1, []);
            }),
        new(Limiter, "Limiter (composed)", false,
            [new("IL", "0.5", "dB", Help: "insertion loss"), new("P1dB", "10", "dBm", Help: "input 1 dB compression point")],
            "An Amp of gain −IL and NF = IL whose intercept puts its input 1 dB compression point at P1dB.",
            s =>
            {
                double il = Num(s, "IL"), p1 = Num(s, "P1dB", 10);
                return new(-il, -il, il, p1 + TanhP1dbBelowIip3, p1, 1, []);
            }),
        new(Splitter, "N-way splitter (composed)", false,
            [new("N", "2", "", Help: "number of ways"), new("IL", "0.3", "dB", Help: "excess loss above the split")],
            "An N-way Wilkinson divider, one output followed: a loss of 10·log10(N) + IL, and that loss is its noise figure.",
            s =>
            {
                int n = Math.Max(2, (int)Math.Round(Num(s, "N", 2)));
                return Passive(10 * Math.Log10(n) + Num(s, "IL"));
            }),
        new(Combiner, "N-way array combiner (composed)", false,
            [new("N", "4", "", Help: "number of elements; every stage before this one is one element's branch"),
             new("IL", "0.5", "dB", Help: "excess loss above the ideal combiner"),
             new("Steer", "0", "deg", Help: "phase step between adjacent elements of the arriving wave; the phase shifters remove it")],
            "An N-way Wilkinson combiner fed by N identical branches in phase: signal +10·log10(N), noise not, so the " +
            "SNR improves by 10·log10(N). Its own noise figure is its excess loss.",
            s =>
            {
                int n = Math.Max(2, (int)Math.Round(Num(s, "N", 4)));
                double il = Num(s, "IL");
                return new(10 * Math.Log10(n) - il, -il, il, double.PositiveInfinity, double.PositiveInfinity, n, []);
            }),
    ];
}

namespace CircuitRF.Core.Netlist.Spice;

/// <summary>How one circuitRF device parameter crosses into the dialect's model (brief TRN-6 R-trn6-3).</summary>
public enum SpiceParameterStatus
{
    /// <summary>The dialect's parameter of the same equation, unit and meaning: written whenever its value is not the
    /// dialect's own default.</summary>
    Maps,
    /// <summary>No counterpart: omitted at circuitRF's default (the dialect then behaves as circuitRF's default does), and
    /// set to anything else it refuses the instance by name.</summary>
    NoCounterpart,
    /// <summary>Has no effect on a transient waveform the deck could carry: a noise coefficient, or a quantity read only by a
    /// relation that is the identity here (temperature relations at T = Tnom, a process derivation that needs Tox). Omitted.</summary>
    NoEffect,
}

/// <summary>One row of a device's parameter table.</summary>
/// <param name="CircuitRf">circuitRF's parameter name.</param>
/// <param name="Dialect">The dialect's name, or null for none.</param>
/// <param name="OnInstance">Written on the instance line rather than the <c>.model</c> card.</param>
/// <param name="CircuitRfDefault">circuitRF's default; NaN when it depends on something else (a channel's sign).</param>
/// <param name="DialectDefault">The dialect's default where it has the same meaning; null where it has none to compare (so
/// the value is always written).</param>
/// <param name="Note">Why the row is what it is, for spice-models.md §15 and for a refusal.</param>
public sealed record SpiceDeviceParameter(
    string CircuitRf, string? Dialect, bool OnInstance, double CircuitRfDefault, double? DialectDefault,
    SpiceParameterStatus Status, string Note = "");

/// <summary>One device's translation: its types, the dialect's element letter, terminals and card, and its parameter table.</summary>
/// <param name="Model">The model's name in words, as a refusal names it ("diode").</param>
/// <param name="Terminals">How many of the element's nets are its external terminals (internal nodes follow).</param>
/// <param name="CardType">The <c>.model</c> type for a circuitRF type (D, NPN, NMOS).</param>
/// <param name="Level">The card's LEVEL, or null.</param>
public sealed record SpiceDeviceModel(
    string Model, IReadOnlyList<string> Types, char Letter, int Terminals, Func<string, string> CardType, int? Level,
    IReadOnlyList<SpiceDeviceParameter> Parameters);

/// <summary>
/// The four device models the dialect and circuitRF both name (docs/design/transient.md §9.2.1; spice-models.md §15):
/// the junction diode, the Gummel-Poon BJT and the MOS levels 1 and 3. Every circuitRF parameter is tabulated against the
/// dialect's, by equation, unit and default. The dialect spellings are the first of the reader's own
/// (<see cref="SpiceModelCardTranslation"/>) for the same parameter, so a deck written from this table reads back to the
/// same parameters.
///
/// <para><b>Two rules apply to every model, outside the table.</b> The temperature relations differ (circuitRF's bandgap
/// follows its own temperature relation, <c>Temperature</c>), so a device whose temperature is not its <c>Tnom</c> is refused
/// by that; at T = Tnom every temperature relation in both is the identity, and the card states <c>TNOM</c> and the instance
/// its <c>temp</c> so the dialect is there too. And the device multiplier is the instance's <c>m</c>.</para>
/// </summary>
public static class SpiceDeviceTable
{
    private const double TnomDialect = 27.0;
    private const double TnomCircuitRf = Devices.Temperature.NominalC;

    private static SpiceDeviceParameter M(string crf, string dialect, double crfDefault, double? dialectDefault, string note = "")
        => new(crf, dialect, false, crfDefault, dialectDefault, SpiceParameterStatus.Maps, note);
    private static SpiceDeviceParameter I(string crf, string dialect, double crfDefault, double? dialectDefault, string note = "")
        => new(crf, dialect, true, crfDefault, dialectDefault, SpiceParameterStatus.Maps, note);
    private static SpiceDeviceParameter None(string crf, double crfDefault, string note)
        => new(crf, null, false, crfDefault, null, SpiceParameterStatus.NoCounterpart, note);
    private static SpiceDeviceParameter NoEffect(string crf, double crfDefault, string note)
        => new(crf, null, false, crfDefault, null, SpiceParameterStatus.NoEffect, note);

    public static readonly SpiceDeviceModel Diode = new("diode", ["Diode"], 'D', 2, _ => "D", null,
    [
        M("Is", "IS", 1e-14, 1e-14),
        M("N", "N", 1, 1),
        M("Rs", "RS", 0, 0),
        M("Cj0", "CJO", 0, 0),
        M("Vj", "VJ", 1, 1),
        M("M", "M", 0.5, 0.5),
        M("Fc", "FC", 0.5, 0.5),
        M("Tt", "TT", 0, 0),
        M("Bv", "BV", 0, 0, "0 is breakdown not modelled in circuitRF, which is the dialect's default (BV infinite)."),
        M("Ibv", "IBV", 1e-3, 1e-3),
        M("Nbv", "NBV", 1, null, "The dialect's NBV defaults to N, circuitRF's to 1, so it is always written."),
        None("Isr", 0, "the dialect models recombination only when ISR is given, and its manual does not state that term "
                     + "as circuitRF's second exponential Isr·(exp(V/(Nr·Vt)) − 1)"),
        NoEffect("Nr", 2, "Read only with Isr, which is refused when set."),
        None("Gmin", 0, "the dialect adds its own GMIN across every junction from its options; circuitRF's diode has its own"),
        I("Area", "area", 1, 1),
        M("Tnom", "TNOM", TnomCircuitRf, TnomDialect, "circuitRF's nominal is 26.85 °C (300 K), the dialect's 27 °C."),
        M("Xti", "XTI", 3, 3),
        M("Eg", "EG", Devices.Temperature.SiliconBandgapEv, 1.11),
        M("Kf", "KF", 0, 0),
        M("Af", "AF", 1, 1),
    ]);

    private static readonly SpiceDeviceParameter[] BjtRows =
    [
        M("Is", "IS", 9.57e-17, 1e-16),
        M("Bf", "BF", 131.1, 100),
        M("Nf", "NF", 1.0, 1),
        M("Vaf", "VAF", 71.02, 0, "0 is not modelled in both (the dialect's infinite Early voltage)."),
        M("Ikf", "IKF", 0.09745, 0, "0 is not modelled in both."),
        M("Ise", "ISE", 1.618e-15, 0),
        M("Ne", "NE", 1.692, 1.5),
        M("Br", "BR", 3.287, 1),
        M("Nr", "NR", 0.959, 1),
        M("Var", "VAR", 4.081, 0, "0 is not modelled in both."),
        M("Ikr", "IKR", 0.07617, 0, "0 is not modelled in both."),
        M("Isc", "ISC", 5.969e-15, 0),
        M("Nc", "NC", 1.974, 2),
        M("Rb", "RB", 9.72444, 0),
        M("Irb", "IRB", 3.017e-6, 0),
        M("Rbm", "RBM", 6.94667, 0),
        M("Re", "RE", 0.7979, 0),
        M("Rc", "RC", 2.089, 0),
        M("Cje", "CJE", 8.287e-14, 0),
        M("Vje", "VJE", 0.8281, 0.75),
        M("Mje", "MJE", 0.7138, 0.33),
        M("Cjc", "CJC", 8.781e-14, 0),
        M("Vjc", "VJC", 0.7715, 0.75),
        M("Mjc", "MJC", 0.7552, 0.33),
        M("Xcjc", "XCJC", 0.6209, 1),
        M("Fc", "FC", 0.6275, 0.5),
        M("Tf", "TF", 1.72653e-11, 0),
        M("Xtf", "XTF", 0.07, 0),
        M("Vtf", "VTF", 0.00381019, 0, "0 is not modelled in both."),
        M("Itf", "ITF", 0.027024, 0),
        M("Tr", "TR", 1.71536e-8, 0),
        I("Area", "area", 1, 1),
        M("Tnom", "TNOM", TnomCircuitRf, TnomDialect, "circuitRF's nominal is 26.85 °C (300 K), the dialect's 27 °C."),
        M("Xti", "XTI", 6.548, 3),
        M("Xtb", "XTB", 1.303, 0),
        M("Eg", "EG", 1.11, 1.11),
        M("Kf", "KF", 0, 0),
        M("Af", "AF", 1, 1),
    ];

    public static readonly SpiceDeviceModel Bjt = new("bipolar transistor (Gummel-Poon)", ["BJT_NPN", "BJT_PNP"], 'Q', 3,
        t => t.Equals("BJT_PNP", StringComparison.OrdinalIgnoreCase) ? "PNP" : "NPN", null, BjtRows);

    private static SpiceDeviceParameter[] MosRows(int level) =>
    [
        M("Vto", "VTO", double.NaN, 0, "circuitRF's default is +1 V n-channel, −1 V p-channel, so it is always written."),
        M("Kp", "KP", 2e-5, 2e-5),
        M("Gamma", "GAMMA", 0, 0),
        M("Phi", "PHI", 0.6, 0.6),
        .. level == 1
            ? new[] { M("Lambda", "LAMBDA", 0, 0) }
            : new[]
            {
                M("Eta", "ETA", 0, 0), M("Theta", "THETA", 0, 0), M("Kappa", "KAPPA", 0.2, 0.2),
                M("Vmax", "VMAX", 0, 0), M("Delta", "DELTA", 0, 0), M("Xj", "XJ", 0, 0),
            },
        I("W", "W", 100e-6, null, "Always written: the dialect's default width is an option (DEFW)."),
        I("L", "L", 100e-6, null, "Always written: the dialect's default length is an option (DEFL)."),
        M("Ld", "LD", 0, 0),
        None("Tox", 0, "with Tox the gate charge is circuitRF's charge-based long-channel form, and the dialect's is Meyer's "
                     + "capacitance set; with Tox unstated neither has a thin-oxide charge"),
        NoEffect("Uo", 600, "Derives Kp only with Tox, which is refused when set."),
        NoEffect("Nsub", 0, "Derives Gamma and Phi only with Tox, which is refused when set."),
        M("Cgso", "CGSO", 0, 0),
        M("Cgdo", "CGDO", 0, 0),
        M("Cgbo", "CGBO", 0, 0),
        M("Is", "IS", 1e-14, 1e-14),
        M("Js", "JS", 0, 0),
        None("N", 1, "the dialect's MOS bulk junctions have emission coefficient 1 and no parameter for it"),
        M("Cbd", "CBD", 0, 0),
        M("Cbs", "CBS", 0, 0),
        M("Cj", "CJ", 0, 0),
        M("Cjsw", "CJSW", 0, 0),
        I("Ad", "AD", 0, 0),
        I("As", "AS", 0, 0),
        I("Pd", "PD", 0, 0),
        I("Ps", "PS", 0, 0),
        M("Pb", "PB", 0.8, 0.8),
        M("Mj", "MJ", 0.5, 0.5),
        M("Mjsw", "MJSW", 0.33, level == 1 ? 0.5 : 0.33,
          level == 1 ? "The dialect's level-1 MJSW defaults to 0.5, circuitRF's to 0.33." : ""),
        M("Fc", "FC", 0.5, 0.5),
        M("Rd", "RD", 0, 0),
        M("Rs", "RS", 0, 0),
        M("Rsh", "RSH", 0, 0),
        I("Nrd", "NRD", 0, null, "Always written: the dialect's default squares differ by version."),
        I("Nrs", "NRS", 0, null, "Always written: the dialect's default squares differ by version."),
        M("Tnom", "TNOM", TnomCircuitRf, TnomDialect, "circuitRF's nominal is 26.85 °C (300 K), the dialect's 27 °C."),
        NoEffect("Xti", 3, "The dialect's level-1 and level-3 models have no XTI; it acts only away from Tnom, which is refused."),
        NoEffect("Eg", Devices.Temperature.SiliconBandgapEv, "As Xti."),
        M("Kf", "KF", 0, 0),
        M("Af", "AF", 1, 1),
        NoEffect("GammaN", 2.0 / 3.0, "A channel thermal-noise coefficient; a transient run has no noise."),
    ];

    private static string MosCard(string type) =>
        type.EndsWith("_P", StringComparison.OrdinalIgnoreCase) ? "PMOS" : "NMOS";

    public static readonly SpiceDeviceModel Mos1 = new("MOS level 1", ["MOS1_N", "MOS1_P"], 'M', 4, MosCard, 1, MosRows(1));
    public static readonly SpiceDeviceModel Mos3 = new("MOS level 3", ["MOS3_N", "MOS3_P"], 'M', 4, MosCard, 3, MosRows(3));

    /// <summary>The four models, in the order of spice-models.md §15.</summary>
    public static readonly IReadOnlyList<SpiceDeviceModel> All = [Diode, Bjt, Mos1, Mos3];

    /// <summary>The model a circuitRF type translates through, or null.</summary>
    public static SpiceDeviceModel? For(string componentType) =>
        All.FirstOrDefault(m => m.Types.Contains(componentType, StringComparer.OrdinalIgnoreCase));

    /// <summary>The refusal sentence for a parameter with no counterpart (R-trn6-3's own wording, then why).</summary>
    public static string NoCounterpart(string instance, SpiceDeviceParameter p, SpiceDeviceModel m) =>
        $"{instance}: {p.CircuitRf} has no counterpart in the SPICE dialect's {m.Model}" + (p.Note.Length > 0 ? $": {p.Note}" : "");
}

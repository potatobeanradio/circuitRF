// The dissipation factor per dielectric class, and the word INDICATIVE
// (docs/sonnet-briefs/brief-railrf-11-part-models.md R-rail11-3, R-rail11-5; railrf.md §2.2, §9,
//  Q-15).
//
// ── THIS TABLE IS THE BASIS, NOT A STOPGAP ─────────────────────────────────────────────────────
//
// Q-15 is unusually blunt and its wording is the requirement: "there is no per-part ESR figure to be
// had, so a dissipation-factor default per dielectric class IS THE BASIS rather than a stopgap, and
// every peak height computed from one is marked INDICATIVE wherever it appears."
//
// Why it matters at all, from §2.2: "With C and f0 alone, railRF can place a resonance but not size
// it." ESR sets the DEPTH of the minimum and the HEIGHT of every anti-resonance peak, and §9 states
// the consequence of leaving that unmarked: "a mask margin in dB computed from an indicative peak
// looks exactly as authoritative as a real one."
//
// ── THE FIGURES ARE GENERAL ENGINEERING GUIDANCE, NOT A STANDARD HELD INTERNALLY ───────────────
//
// The same sentence PdnViaCurrentLimit carries, for the same reason. Every number below is the
// dissipation factor a class of part is SPECIFIED at — the maximum a datasheet quotes, at the
// measurement frequency that class is quoted at (1 kHz for the bulk types, 1 MHz for class-I and
// class-II ceramics). It is a bound on a population, not a measurement of one part.
//
// TWO THINGS ARE WRONG WITH IT ON PURPOSE, AND BOTH ARE WHY THE MARKING IS LOAD-BEARING:
//
//   1. A quoted DF is a MAXIMUM. A real part is usually better, often by a factor of two or three,
//      so a peak height computed from one is pessimistic more often than it is optimistic — but
//      "usually pessimistic" is not a number anyone can put a margin against.
//   2. DF is FREQUENCY-DEPENDENT and these figures are quoted at one frequency. ESR = DF/(2*pi*f*C)
//      therefore falls as 1/f here, while a real part's ESR flattens out near its own resonance
//      where the dielectric loss stops dominating and the electrode resistance takes over.
//
// Neither is repairable from the data a part library holds, which is exactly Q-15's point: the
// remedy is the part's own Touchstone file, which is why §2.2 RECOMMENDS that path rather than
// merely offering it — see RailPartResolver, where a file-backed part reports Measured.
//
// ── A PART WITH NO CLASS GETS NO DEFAULT ───────────────────────────────────────────────────────
//
// R-rail11-3, and brief 2 already made it representable: PartLibraryRow.DielectricClass is null
// where the table states none, and it stays null. Such a part is COUNTED and REPORTED, never given
// a class-II figure on the grounds that most parts are class II. So is a part whose class this
// table does not recognise — the two are different sentences and neither is a number.
//
// NUMBERS ARE DIMENSIONLESS (a dissipation factor is tan(delta)) and FREQUENCIES ARE HERTZ.

using CircuitRF.Design.Layout;

namespace CircuitRF.Design.RailRf;

/// <summary>
/// Dissipation factor per dielectric class, the ESR that follows from it, and the same figure
/// serving the board dielectric where the stackup states no tan δ of its own (Q-15).
/// </summary>
public static class RailEsrDefaults
{
    /// <summary>
    /// The word every number computed from a class default is marked with, spelled ONCE.
    ///
    /// <para>§9 names five places it has to appear — the part row, the plot, the anti-resonance
    /// table, the mask-margin readout and the provenance of every export — and five places that each
    /// spelled it for themselves would drift. <b>The marking is load-bearing rather than
    /// temporary.</b></para>
    /// </summary>
    public const string Marking = "indicative";

    /// <summary>
    /// The sentence that travels with an indicative number. <b>A sentence on the result, not a log
    /// line</b> (R-rail11-4): a flag computed here and dropped at the <c>DataSet</c> boundary reaches
    /// none of the five places.
    /// </summary>
    public const string IndicativeLine =
        "Some ESRs here are a dissipation-factor default for the part's dielectric class rather " +
        "than a measurement, so every peak height and every mask margin derived from one is " +
        Marking + ". A part's own Touchstone file is the only route to a real ESR.";

    // ── the table ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Dissipation factor by canonical class name. <b>General engineering guidance rather than a
    /// standard held internally</b>, and the file header says what is wrong with it on purpose.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    ///   <item><b>Class-I ceramic</b> (C0G/NP0) — 0.001. The low-loss ceramic; quoted at or below
    ///   0.1 % and the reason a C0G part's resonance is sharp enough to matter.</item>
    ///   <item><b>Class-II ceramic</b> (X5R, X6S, X7R, X7S, X5S) — 0.025. The 2.5 % maximum these are
    ///   quoted at, and the class nearly every decoupling capacitor on these boards belongs to.</item>
    ///   <item><b>Class-III ceramic</b> (Y5V, Z5U) — 0.05. The high-permittivity, poorly-behaved
    ///   class, quoted at 5 %; its capacitance derates hard too (Q-12).</item>
    ///   <item><b>Film</b> — 0.005. Spans polyester at a few per cent down to polypropylene near
    ///   0.05 %, which is a two-decade spread this one figure cannot express — a film part is
    ///   exactly the case for its own Touchstone file.</item>
    ///   <item><b>Tantalum</b> (manganese-dioxide cathode) — 0.08.</item>
    ///   <item><b>Polymer</b> (polymer-cathode tantalum or aluminium) — 0.04. The reason a polymer
    ///   part replaces a tantalum one in a bulk position.</item>
    ///   <item><b>Aluminium electrolytic</b> — 0.15. The bulk part whose ESR is large enough to
    ///   DAMP the anti-resonance it takes part in, which is half of why it is there.</item>
    /// </list>
    /// </remarks>
    public static readonly IReadOnlyDictionary<string, double> DissipationFactors =
        new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["C0G"]         = 0.001,
            ["X5R"]         = 0.025,
            ["X6S"]         = 0.025,
            ["X7R"]         = 0.025,
            ["X7S"]         = 0.025,
            ["X5S"]         = 0.025,
            ["Y5V"]         = 0.05,
            ["Z5U"]         = 0.05,
            ["FILM"]        = 0.005,
            ["TANTALUM"]    = 0.08,
            ["POLYMER"]     = 0.04,
            ["ELECTROLYTIC"] = 0.15,
        };

    /// <summary>Every class this table recognises, for a refusal that LISTS them rather than saying
    /// a class was not recognised and stopping there.</summary>
    public static IReadOnlyList<string> KnownClasses => [.. DissipationFactors.Keys];

    /// <summary>
    /// The canonical spelling of a dielectric class as a maintained table writes it, or null where
    /// the table states none or this one does not recognise it.
    ///
    /// <para>NP0 and C0G are the same class under two names — one is the EIA code and the other the
    /// industry spelling — so they canonicalise together. Case, spaces and hyphens are ignored,
    /// because a class typed in one case and exported in another is the same class, on
    /// <see cref="PartLibrary.Part"/>'s own rule for part numbers.</para>
    /// </summary>
    public static string? CanonicalClass(string? dielectricClass)
    {
        if (string.IsNullOrWhiteSpace(dielectricClass)) return null;

        // The house guard (GerberMacro.cs): a stackalloc sized by an INPUT string is a stack
        // overflow waiting for a malformed file, and a stack overflow cannot be caught. A dielectric
        // class is four characters; a cell of a maintained table that lost its delimiter is not.
        Span<char> buf = dielectricClass.Length <= 256
            ? stackalloc char[dielectricClass.Length]
            : new char[dielectricClass.Length];
        int n = 0;
        foreach (char c in dielectricClass)
            if (char.IsAsciiLetterOrDigit(c)) buf[n++] = char.ToUpperInvariant(c);

        string key = new(buf[..n]);
        if (key is "NP0" or "NPO") key = "C0G";
        if (key is "ALUMINIUM" or "ALUMINUM" or "ALELECTROLYTIC") key = "ELECTROLYTIC";
        if (key is "TA" or "TANT") key = "TANTALUM";

        return DissipationFactors.ContainsKey(key) ? key : null;
    }

    /// <summary>
    /// The dissipation factor for a class, or <b>null where there is none to be had</b> — the class
    /// was not stated, or was stated in a spelling this table does not recognise.
    ///
    /// <para><b>Null is the answer, never a substituted figure</b> (R-rail11-3). A part that lands
    /// here with nothing is counted by <see cref="RailPartModelSet.WithoutEsrBasis"/> and reported,
    /// because a peak sized from a class it does not belong to is worse than a peak nobody sized.</para>
    /// </summary>
    public static double? DissipationFactorFor(string? dielectricClass) =>
        CanonicalClass(dielectricClass) is { } key ? DissipationFactors[key] : null;

    /// <summary>
    /// <c>ESR = DF/(2·π·f·C)</c>, in OHMS — the dissipation factor evaluated at the frequency in
    /// question (R-rail11-3).
    ///
    /// <para>DF is defined as <c>tan δ = ESR/|X_C| = ESR·ω·C</c>, so this is that identity solved
    /// for ESR and nothing more. It falls as 1/f, which is the second of the two things the file
    /// header records as deliberately wrong with a one-frequency figure.</para>
    ///
    /// <para>NaN where the arithmetic has no answer — at DC, or for a part with no capacitance —
    /// never a clamped number, on <see cref="RfCore.Data.PassiveMetrics"/>'s own rule: a clamped
    /// value reads as a measurement.</para>
    /// </summary>
    public static double EsrOhms(double dissipationFactor, double capacitanceFarads, double frequencyHz) =>
        capacitanceFarads > 0 && frequencyHz > 0
            ? dissipationFactor / (2.0 * Math.PI * frequencyHz * capacitanceFarads)
            : double.NaN;

    // ── R-rail11-5: the same figure serves the board dielectric ──────────────────────────────

    /// <summary>
    /// The general-purpose laminate's tan δ — the figure a board is taken at when nothing else is
    /// known. <b>0.02</b>, the woven-glass epoxy figure, which is what these boards are unless
    /// somebody says otherwise.
    /// </summary>
    public const double GeneralPurposeLaminateTanDelta = 0.02;

    /// <summary>
    /// tan δ by laminate class, recognised from the stackup layer's own NAME. Same standing as
    /// <see cref="DissipationFactors"/>: guidance, at one frequency, for a class rather than a part.
    /// </summary>
    /// <remarks>
    /// The spread is what makes this worth flagging: a low-loss laminate is five times better than a
    /// general-purpose one and a fluoropolymer twenty times better, and §2.2 names tan δ as one of
    /// the two stackup numbers most often wrong — <i>"it sets how sharp the cavity resonances are,
    /// which is the difference between a 6 dB bump and a 20 dB one."</i>
    /// </remarks>
    public static readonly IReadOnlyDictionary<string, double> LaminateTanDeltas =
        new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["FR-4"]      = GeneralPurposeLaminateTanDelta,
            ["low-loss"]  = 0.004,
            ["polyimide"] = 0.008,
            ["PTFE"]      = 0.001,
        };

    /// <summary>
    /// What a board dielectric's tan δ was taken to be, and <b>whether the stackup said so</b>
    /// (R-rail11-5, Q-15).
    /// </summary>
    /// <param name="TanDelta">The value used.</param>
    /// <param name="IsClassDefault">True where the stackup stated none and this table supplied one.
    /// <b>Flagged exactly as an indicative ESR is</b>, and for the same reason.</param>
    /// <param name="ClassUsed">Which laminate class the figure came from, or null where the stackup
    /// stated its own.</param>
    /// <param name="LayerName">The stackup layer this is about, so a report can name it.</param>
    public sealed record RailBoardLoss(double TanDelta, bool IsClassDefault, string? ClassUsed, string LayerName)
    {
        /// <summary>The sentence a report prints — the number, and whether anybody stated it.</summary>
        public string Describe() =>
            IsClassDefault
                ? $"'{LayerName}' states no tan δ, so railRF used {TanDelta:0.####} for a " +
                  $"{ClassUsed} laminate. Every cavity Q computed from it is {Marking}: tan δ sets " +
                  "how sharp the plane resonances are, which is the difference between a 6 dB bump " +
                  "and a 20 dB one."
                : $"'{LayerName}' states tan δ = {TanDelta:0.####}.";
    }

    /// <summary>
    /// R-rail11-5. The tan δ to use for one dielectric layer of the stackup: <b>its own where it
    /// states one, and the per-class figure where it does not — flagged the same way an indicative
    /// ESR is.</b>
    /// </summary>
    /// <remarks>
    /// <b>Zero is what "not stated" looks like here</b>, and that is a property of the stackup model
    /// rather than a choice made here: <see cref="StackupLayer.TanD"/> is a plain double that opens
    /// at 0, so a lossless dielectric and an unstated one are the same bytes. Treating 0 as stated
    /// would give every stackup that never mentioned tan δ an infinitely sharp cavity, which is the
    /// optimistic direction — and treating a genuinely lossless one as unstated costs nothing, since
    /// no real laminate is.
    /// </remarks>
    public static RailBoardLoss BoardLossTangent(StackupLayer layer)
    {
        string name = layer.Name.Length > 0 ? layer.Name : "(unnamed dielectric)";
        if (layer.TanD > 0) return new RailBoardLoss(layer.TanD, false, null, name);

        string cls = LaminateClassOf(layer.Name);
        return new RailBoardLoss(LaminateTanDeltas[cls], true, cls, name);
    }

    /// <summary>Which laminate class a stackup layer's NAME names, or the general-purpose one.
    /// Longest match first, so a layer called <c>FR-4 low-loss</c> reads as low-loss rather than as
    /// the first key that happens to appear in it; hyphens and spaces are ignored on both sides, so
    /// <c>FR4</c> and <c>FR-4</c> are one class.</summary>
    private static string LaminateClassOf(string layerName)
    {
        string name = Squash(layerName);
        string? best = null;
        foreach (string key in LaminateTanDeltas.Keys)
        {
            if (name.Contains(Squash(key), StringComparison.Ordinal) &&
                (best is null || key.Length > best.Length))
                best = key;
        }
        return best ?? "FR-4";

        static string Squash(string s)
        {
            // Sized by a stackup layer's NAME — see CanonicalClass for the guard and why.
            Span<char> buf = s.Length <= 256 ? stackalloc char[s.Length] : new char[s.Length];
            int n = 0;
            foreach (char c in s) if (char.IsAsciiLetterOrDigit(c)) buf[n++] = char.ToUpperInvariant(c);
            return new string(buf[..n]);
        }
    }
}

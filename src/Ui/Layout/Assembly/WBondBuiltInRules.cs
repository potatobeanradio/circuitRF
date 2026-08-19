// The rule set circuitRF itself supplies, for a design that references no `.wasm`
// (docs/design/wbond.md §8; owner, 2026-08-19).
//
// ── Why a built-in set exists at all ────────────────────────────────────────────────────────────
//
// `WasmDefaults` next door answers a different question: it is the STARTER FILE a user creates and
// then edits against their assembly house's own document. Until they do that — and most designs
// never do — a check over a wirebond design had nothing to say except "reference a .wasm". That is
// a check that runs and reports nothing, which is the shape of a tool people stop pressing.
//
// So the checker always carries this set, and the panel always names which of the two it used.
//
// ── What may go in here, and what may not ───────────────────────────────────────────────────────
//
// **Only rules whose FAILURE is a fault of the design rather than of a house's tolerance for it.** A
// `.wasm` rule is a STATEMENT BY A HOUSE: a minimum pitch, a loop envelope, a wire they stock.
// Nothing here may be that, because a number circuitRF invented, reported as though a house had
// stated it, would pass a design the house rejects — the exact failure `WasmDefaults`' "PLACEHOLDER"
// wording exists to prevent.
//
// The one rule that qualifies today is that two wires must not occupy the same space. That is not a
// house's limit at all; it is geometry, and a design containing it is wrong before anybody quotes it.
// Which is also why it keeps running WHEN a `.wasm` is resolved: a house's rule file is not what
// makes overlapping metal invalid, so a file that omits the rule does not repeal it.
//
// **Its guard band is the one number circuitRF states, and every surface says whose it is.** The rule
// is checked at half a mil rather than at zero (see `DefaultClearanceNm` for why zero catches only the
// mistakes nobody makes), and half a mil IS a number this program chose. It is legitimate here for two
// reasons and would not be otherwise: it is reported under this set's own name — never as a house's —
// and the user can change it (`WBondWireClearance`). Any FURTHER rule wanting a number of its own has
// to clear the same bar, and almost none will.
//
// Framework-free, like everything else in this folder.

using CircuitRF.WBond;

namespace CircuitRF.Ui.Layout.Assembly;

/// <summary>One rule circuitRF supplies itself.</summary>
/// <param name="Name">What a violation traces back to — also its waiver identity.</param>
/// <param name="Severity">
/// <see cref="DrcSeverity.Error"/> for the touching rule, and it is not a matter of taste: the
/// design cannot be built, so there is no version of it that ships with the violation in place.
/// </param>
/// <param name="Description">Shown beside a violation, in the house-rule slot a `.wasm` fills.</param>
public sealed record WBondBuiltInRule(string Name, DrcSeverity Severity, string Description);

/// <summary>
/// The rules circuitRF checks with no `.wasm` in sight — and, for the ones that are geometry rather
/// than policy, alongside one.
/// </summary>
public static class WBondBuiltInRules
{
    /// <summary>What the panel and the Messages line call this set.</summary>
    public const string SetName = "circuitRF built-in assembly rules";

    /// <summary>
    /// The clearance the built-in rule holds wires to, measured <b>surface to surface</b> — outer
    /// edge of one wire's metal to the outer edge of the other's, which is what
    /// <c>WireGeometry3D.Clearance</c> returns and what a bonder operator would measure.
    ///
    /// <para><b>Half a mil, and not zero</b> (owner, 2026-08-19). Zero is the point at which the
    /// design is impossible; it is not the point at which it is buildable. Two wires a nanometre
    /// apart pass a zero test and short on the first sweep of encapsulant, so a rule set at the
    /// impossibility threshold catches only the mistakes nobody makes. Half a mil is a guard band
    /// wide enough to survive the placement tolerance of a wire bonder and narrow enough that no
    /// deliberate design trips it.</para>
    ///
    /// <para><b>It is a DEFAULT, not a limit</b> — see <see cref="WBondWireClearance"/> for where a
    /// user changes it, and why that lives in preferences rather than in a design.</para>
    /// </summary>
    public static readonly double DefaultClearanceNm = WBondUnits.ToNm(0.5, WBondUnit.Mil);

    /// <summary>
    /// The floor a stated clearance is held at.
    ///
    /// <para><b>Zero has to remain checkable and cannot be spelled as zero.</b> A user who sets the
    /// clearance to 0 means "only report metal that actually collides", and the sweep reports
    /// <c>clearance &lt; limit</c> — so a limit of exactly 0 finds interpenetration but silently
    /// skips two wires that touch EXACTLY, which is not an exotic case: an array laid out on a pitch
    /// equal to its own wire diameter is drawn that way on purpose. A picometre is a hundredth of an
    /// atom, so treating it as the smallest meaningful limit swallows no real clearance.</para>
    /// </summary>
    public const double MinimumClearanceNm = 1e-3;

    /// <summary>The touching-wires rule. Named as a constant because the waiver key is built from it,
    /// so a rename un-waives every waiver granted against it.</summary>
    public const string WireClearanceRuleName = "Wire-to-wire clearance";

    public static readonly WBondBuiltInRule WireClearance = new(
        WireClearanceRuleName,
        DrcSeverity.Error,
        "Two wires' metal is closer than the built-in minimum clearance, or touching. Two conductors " +
        "cannot occupy the same space and a bonder cannot hold two wires to zero gap, so this is a " +
        "geometry error in the design rather than a clearance close to somebody's limit — it is " +
        "reported whether or not an assembly house states a spacing rule.");

    /// <summary>Every built-in rule, in the order they are checked.</summary>
    public static IReadOnlyList<WBondBuiltInRule> All { get; } = [WireClearance];

    /// <summary>
    /// The one sentence a surface shows when this set is the only thing that ran — the built-in twin
    /// of <c>WasmResolution.Describe</c>, and it must be as explicit about its own LIMITS as that one
    /// is about which house it names.
    /// </summary>
    public static string Describe(double clearanceNm) =>
        $"{SetName}: {All.Count} rule(s), wire-to-wire clearance {FormatMil(clearanceNm)} — no .wasm " +
        "referenced, so no bonder, process or material rules were checked.";

    /// <summary>
    /// Lengths in this set read in MIL, matching every other wire violation: a bonder is set up in
    /// mil and a `.wasm` is written in mil, so a limit quoted in the layout's own display unit is one
    /// the user has to convert before they can check it against anything.
    /// </summary>
    public static string FormatMil(double nm) =>
        $"{nm / WBondUnits.NmPerUnit(WBondUnit.Mil):0.###} mil";
}

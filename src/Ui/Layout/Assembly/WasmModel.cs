// The assembly rule document — `.wasm` (docs/design/wbond.md §8, WB31/WB32).
//
// ── Why this is its own document and not a section of `.ctech` ───────────────────────────────────
//
// WB31 is an owner decision with a structural argument behind it. The relation between assembly
// houses and process technologies is MANY-TO-MANY: one OSAT bonds GaAs, GaN and Si die from a dozen
// foundries, and one die technology is bonded at several houses across its life. Rules that are
// many-to-many with technologies cannot live inside a technology without duplication, and duplicated
// rules drift. The lifecycles differ too — a `.ctech` changes when a process node revises; assembly
// rules change when the house buys a bonder, qualifies a wire, or a product gets a waiver.
//
// In one sentence: `.ctech` owns what the pad IS; this file owns what the bonder can DO with it.
//
// ── Why THIS folder and not `src/Ui/Layout/Drc/` ────────────────────────────────────────────────
//
// A `.wasm` is a peer of `Technology`: a resolvable, workspace-scoped document with its own
// persistence, its own cache and its own resolution order. `Drc/` holds the checking ENGINE and the
// rule-expression language it evaluates; a document that happens to carry rules is not part of that
// engine any more than `.ctech` is. The predicate language those rules are written in DOES live in
// `Drc/`, beside the layer language it extends — see `DrcPredicateParser`.
//
// Framework-free: no Avalonia, no Skia. Same bar as `TechModel`/`TechPersistence` next door.

using CircuitRF.WBond;

namespace CircuitRF.Ui.Layout.Assembly;

/// <summary>
/// Which of WB32's three sections a rule came from.
///
/// <para><b>A violation reports its section, and that is not decoration.</b> "Your bonder cannot do
/// this" and "your assembly house prefers not to do this" have very different answers — the first is
/// a redesign, the second is a phone call. A panel that reported only "wire spacing too small" would
/// leave the user unable to tell which conversation they are in.</para>
/// </summary>
public enum WasmSection
{
    /// <summary>Bonder capability limits. Hard — the machine physically cannot.</summary>
    Machine,

    /// <summary>What this house will actually run for this product. Tighter than the machine.</summary>
    Process,

    /// <summary>Wire types, diameters and metals available.</summary>
    Material,
}

/// <summary>
/// One assembly rule: a name, a boolean predicate over the widened rule language, and a severity.
///
/// <para><b>Shape note.</b> A `.ctech` <see cref="DrcRule"/> is a (kind, region, value) triple
/// because a die-side rule is always one of a small set of MEASUREMENTS applied to a region. An
/// assembly rule is not: "loop height must stay under a curve of span" is a comparison between two
/// computed scalars, and there is no measurement kind that expresses it. So the rule carries an
/// EXPRESSION rather than a kind — which is exactly what §8.1 means by adding wire vocabulary as new
/// operands and functions inside the existing language rather than as a second rule language.</para>
/// </summary>
public sealed class WasmRule
{
    /// <summary>The house's own name for the rule. This is what a violation traces back to, and it
    /// is the merge identity — same reasoning as <see cref="DrcRule.Name"/>.</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// The predicate, in the widened rule language — e.g.
    /// <c>wire_spacing(G1, G2) &gt;= 4mil &amp;&amp; loop_height(G1) &lt;= envelope(max_loop, span(G1))</c>.
    /// A rule whose expression will not parse is reported by name and not checked, never guessed at.
    /// </summary>
    public string Expression { get; set; } = "";

    /// <summary>Free text shown beside a violation — the house's own wording for why the rule exists.</summary>
    public string? Description { get; set; }

    public DrcSeverity Severity { get; set; } = DrcSeverity.Error;
}

/// <summary>One point of a piecewise-linear table: at span <paramref name="X"/>, the limit is
/// <paramref name="Y"/>. Both in nanometres when the table is a length curve.</summary>
public readonly record struct WasmEnvelopePoint(long X, long Y);

/// <summary>
/// A named piecewise-linear lookup — the one genuine language extension WB-D adds (§8.1).
///
/// <para>Minimum and maximum loop height are both FUNCTIONS OF SPAN, and houses supply them as a
/// table. Making the table a first-class named value rather than a special case bolted onto one rule
/// means any other tabulated limit a house supplies is expressible the same way, with no further
/// language work.</para>
///
/// <para><b>Out-of-order or duplicated X points are refused, never interpolated.</b> A table whose
/// points are not strictly increasing has no single answer between them, and picking one would be
/// the checker inventing a limit the house never stated. <b>A one-point table is legal and is a
/// constant</b> (§5 open question 3, adopted): a house that states one number means one number, and
/// refusing it would force a pointless second point.</para>
/// </summary>
public sealed class WasmEnvelope
{
    public string Name { get; set; } = "";

    /// <summary>Strictly increasing in <see cref="WasmEnvelopePoint.X"/>. At least one point.</summary>
    public List<WasmEnvelopePoint> Points { get; set; } = [];

    /// <summary>
    /// Evaluates the table at <paramref name="x"/>, clamping outside the stated range.
    ///
    /// <para><b>Clamping, not extrapolating.</b> A house's table states what it will do over the
    /// span range it states; running the last two points' slope out past the end of the table would
    /// manufacture a limit nobody agreed to. Outside the range the nearest stated limit applies —
    /// which is also what a bonder operator reading the same table would do.</para>
    /// </summary>
    public double ValueAt(double x)
    {
        if (Points.Count == 0) return 0.0;
        if (Points.Count == 1 || x <= Points[0].X) return Points[0].Y;
        if (x >= Points[^1].X) return Points[^1].Y;

        for (int i = 1; i < Points.Count; i++)
        {
            var a = Points[i - 1];
            var b = Points[i];
            if (x > b.X) continue;

            double span = b.X - a.X;
            if (span <= 0) return a.Y;          // guarded by validation; harmless if it slips through
            double t = (x - a.X) / span;
            return a.Y + t * (b.Y - a.Y);
        }

        return Points[^1].Y;
    }
}

/// <summary>
/// The `.wasm` document: three rule sections plus the material section's own allowed-value lists and
/// the envelope tables the rules look limits up in.
///
/// <para>The three sections are separate lists rather than one list with a section field because
/// WB32 makes the section part of the file's STRUCTURE — a house authoring one edits the section it
/// owns, and a diff that shows a rule moving between sections is information. <see cref="AllRules"/>
/// is the flat view the checker walks.</para>
/// </summary>
public sealed class WasmFile
{
    /// <summary>The house's name for this rule set — reported by every surface that shows a result,
    /// for exactly the reason <c>DrcRunResult.TechnologyName</c> exists.</summary>
    public string Name { get; set; } = "";

    /// <summary>Bonder capability limits (WB32 <see cref="WasmSection.Machine"/>).</summary>
    public List<WasmRule> Machine { get; set; } = [];

    /// <summary>What this house will actually run (WB32 <see cref="WasmSection.Process"/>).</summary>
    public List<WasmRule> Process { get; set; } = [];

    /// <summary>Material-section rules (WB32 <see cref="WasmSection.Material"/>).</summary>
    public List<WasmRule> Material { get; set; } = [];

    /// <summary>
    /// Wire diameters this house stocks, in nanometres. Empty means "not stated", which is NOT the
    /// same as "none allowed" — an unstated list is not checked at all.
    ///
    /// <para>Checked structurally rather than through the expression language: an allowed-value list
    /// is a set membership test, and inventing a <c>diameter(...)</c> function plus a set literal to
    /// spell it would be more language for no more expressive power.</para>
    /// </summary>
    public List<long> AllowedDiametersNm { get; set; } = [];

    /// <summary>Metals this house bonds, by <c>Wire.Material</c> name. Empty means "not stated".</summary>
    public List<string> AllowedMetals { get; set; } = [];

    /// <summary>Named piecewise-linear tables the rules look limits up in.</summary>
    public List<WasmEnvelope> Envelopes { get; set; } = [];

    /// <summary>Every rule, section by section in WB32's own order. The checker's flat view.</summary>
    public IEnumerable<(WasmSection Section, WasmRule Rule)> AllRules()
    {
        foreach (var r in Machine)  yield return (WasmSection.Machine, r);
        foreach (var r in Process)  yield return (WasmSection.Process, r);
        foreach (var r in Material) yield return (WasmSection.Material, r);
    }

    public int RuleCount => Machine.Count + Process.Count + Material.Count;

    /// <summary>The rules of one section, for the merge machinery to address a section at a time.</summary>
    public List<WasmRule> RulesOf(WasmSection section) => section switch
    {
        WasmSection.Machine  => Machine,
        WasmSection.Process  => Process,
        WasmSection.Material => Material,
        _ => throw new ArgumentOutOfRangeException(nameof(section), section, "Unknown .wasm section."),
    };

    /// <summary>Looks an envelope table up by name, case-insensitively.</summary>
    public WasmEnvelope? EnvelopeByName(string name) =>
        Envelopes.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True when a wire's diameter is one the house stocks. An unstated list allows everything —
    /// see <see cref="AllowedDiametersNm"/>.
    /// </summary>
    /// <param name="toleranceNm">
    /// Diameters are stated in mil and stored in nanometres, so an exact integer match would fail on
    /// a value that round-tripped through a different unit. One nanometre of slack costs nothing and
    /// removes a whole class of false positive.
    /// </param>
    public bool DiameterAllowed(long diameterNm, long toleranceNm = 1) =>
        AllowedDiametersNm.Count == 0 ||
        AllowedDiametersNm.Any(d => Math.Abs(d - diameterNm) <= toleranceNm);

    /// <summary>True when a wire's metal is one the house bonds. An unstated list allows everything.</summary>
    public bool MetalAllowed(string? metal) =>
        AllowedMetals.Count == 0 ||
        AllowedMetals.Any(m => string.Equals(m, metal, StringComparison.OrdinalIgnoreCase));

    /// <summary>Formats the allowed diameters for a message, in the unit a bonder is set up in.</summary>
    public string DescribeAllowedDiameters() =>
        AllowedDiametersNm.Count == 0
            ? "(not stated)"
            : string.Join(", ", AllowedDiametersNm.Select(d =>
                $"{WBondUnits.FromNm(d, WBondUnit.Mil):0.###} mil"));
}

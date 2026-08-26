// The DRC result model (docs/design/layout-view.md §9A.1). Framework-free — no Avalonia, no Skia,
// no Clipper2 types leak out of here; a violation is geometry the same way every other layout type
// is, so the panel, the renderer, the exporter and the tests all read the same thing.
//
// §9A.1 is explicit that a violation carries "a GEOMETRIC MARKER (the region that actually violates),
// not just a point" — "a spacing violation somewhere on M1" is not usable. So the marker is rings of
// DBU, in the same world coordinates as the artwork, and the renderer draws them without knowing
// anything about rules.

namespace CircuitRF.Ui.Layout.Drc;

/// <summary>
/// One design-rule violation: which rule, how bad, and — the part that makes it usable — WHERE.
/// </summary>
/// <param name="RuleName">The <see cref="DrcRule.Name"/> as the technology states it.</param>
/// <param name="Kind">Which check produced it.</param>
/// <param name="Severity">The rule's own severity, carried through unchanged.</param>
/// <param name="Layer">
/// The drawing layer checked, or <b>null for a wire violation</b>, which has none.
///
/// <para><b>Why nullable and not a reserved synthetic key</b> (brief-wbond-wbd §5 open question 2,
/// answered). A bond wire is not on a drawing layer — it is a piece of metal in space above them —
/// and the panel groups and colours by this field. A reserved key would make every wire violation
/// group under a layer that means nothing, and the layer of the pad a wire happens to land on would
/// be worse still: it reads as a claim about where the violation IS. Null is the honest answer, and
/// it costs one nullable field on a record every existing violation already uses. Every consumer
/// sorts and displays wire violations after layer-bearing ones, which is also the order a user wants
/// them in.</para>
/// </param>
/// <param name="RequiredDbu">The rule's value — what the geometry had to satisfy and did not.</param>
/// <param name="MarkerRings">
/// The violating REGION, as flat implicitly-closed DBU vertex lists in the design's own world
/// coordinates (element 0 of each contiguous region is its outer ring; holes are not distinguished —
/// a marker is drawn, never filled meaningfully). For a width violation this is the part of the
/// conductor that is too narrow; for a spacing violation it is the GAP between the two conductors,
/// which is the region a user has to widen.
/// </param>
/// <param name="Marker">Bounding box of <paramref name="MarkerRings"/> — what click-to-zoom uses.</param>
/// <param name="NetA">One side's net, or null when the geometry states none.</param>
/// <param name="NetB">The other side's net (spacing only; null for width).</param>
/// <param name="Key">
/// The stable identity a waiver names — see <see cref="DrcWaiver"/>. Derived from the rule, the layer
/// and the marker's exact bounding box, so re-running on unchanged geometry reproduces it exactly and
/// moving the offending shape produces a different violation (which is the correct outcome: a waiver
/// names a place, and that place no longer exists).
/// </param>
public sealed record DrcViolation(
    string                RuleName,
    DrcRuleKind           Kind,
    DrcSeverity           Severity,
    LayerKey?             Layer,
    long                  RequiredDbu,
    IReadOnlyList<long[]> MarkerRings,
    Bbox                  Marker,
    string?               NetA,
    string?               NetB,
    string                Key)
{
    /// <summary>True when this violation is currently suppressed by a waiver. Set by
    /// <see cref="DrcEngine"/> at run time; a waived violation is still REPORTED (§9A.1: waiving must
    /// be "persisted, and visible"), it is just not counted against the design.</summary>
    public bool Waived { get; init; }

    /// <summary>The waiver's own reason, when <see cref="Waived"/>. Null when none was given.</summary>
    public string? WaiverReason { get; init; }

    /// <summary>
    /// Which `.wasm` section an assembly rule came from (WB32), or null for a die-side rule.
    ///
    /// <para>Reported because "your bonder cannot do this" and "your assembly house prefers not to"
    /// have very different answers — a machine limit is a redesign, a process preference is a phone
    /// call. A panel that showed only the rule name would leave the user unable to tell which
    /// conversation they are in.</para>
    /// </summary>
    public Assembly.WasmSection? Section { get; init; }

    /// <summary>
    /// The wire GROUPS (array names) that participate, for a wire violation — one for a per-wire
    /// rule, two for a pair rule. Empty for a die-side violation.
    ///
    /// <para>Part of the waiver key, and the reason it is groups rather than wire indices: see
    /// <see cref="Key"/>.</para>
    /// </summary>
    public IReadOnlyList<string> WireGroups { get; init; } = [];

    /// <summary>
    /// The measured value that failed the rule, formatted by whoever produced it — "3.2 mil" against
    /// a 4 mil limit. Null for a die-side violation, whose measurement is
    /// <see cref="RequiredDbu"/> and whose <see cref="Kind"/> already says what was measured.
    /// </summary>
    public string? MeasuredText { get; init; }

    /// <summary>True when this violation came from an assembly rule rather than the technology.</summary>
    public bool IsAssembly => Section is not null;
}

// DrcWaiver lives in CircuitRF.Design.Layout.Drc — it is persisted on the LayoutView, so the `.clay`
// format names it and it had to cross to the non-UI side with the rest of the layout model
// (brief-cli-em-verb.md R-emcli-3). Everything else in this file stays here with the engine.

/// <summary>How a run was bounded and what it flattened at — see <see cref="DrcEngine.Run"/>.</summary>
/// <param name="MaxShapes">
/// v1 runs FLAT (§9A.1's "hierarchy answer"), so a deep hierarchy can elaborate into far more geometry
/// than a person expects. Above this the run refuses rather than hanging; the default is L3c's own
/// flatten ceiling, reused rather than re-derived because it guards the identical risk.
/// </param>
/// <param name="MaxWires">
/// The wire-count ceiling for the assembly half (brief-wbond-wbd R-wbd-4). Wire-to-wire clearance is
/// quadratic in wires before the broad phase prunes it, so a pathological design must cost a message
/// rather than a hang — the same bargain <paramref name="MaxShapes"/> already strikes for artwork.
/// It rides on this record rather than a second settings type for the same reason: one place a
/// caller states how far a check is allowed to go.
/// </param>
public sealed record DrcRunSettings(
    int MaxShapes = DrcEngine.DefaultMaxShapes,
    int MaxWires  = DrcEngine.DefaultMaxWires)
{
    /// <summary>
    /// The built-in rule set's wire-to-wire clearance, in nanometres, surface to surface.
    ///
    /// <para>An init-only member rather than a positional parameter so that every existing
    /// construction of this record — <c>new DrcRunSettings(MaxWires: 4)</c> and the rest — keeps
    /// compiling and keeps meaning what it meant. Defaults to circuitRF's own half a mil; the run
    /// that reaches a user's preference (<c>WBondWireClearance</c>) sets it explicitly.</para>
    /// </summary>
    public double WireClearanceNm { get; init; } = Assembly.WBondBuiltInRules.DefaultClearanceNm;

    public static readonly DrcRunSettings Default = new();
}

/// <summary>What one DRC run found, and enough context to know what it actually checked.</summary>
/// <param name="Violations">
/// Every violation found, waived ones included and marked (§9A.1). Ordered deterministically: by
/// layer, then rule name, then marker position — so two runs over unchanged geometry produce
/// identical lists and a test can assert on order.
/// </param>
/// <param name="RulesEvaluated">How many technology rules actually ran.</param>
/// <param name="LayersChecked">How many drawing layers carried geometry a rule applied to.</param>
/// <param name="ShapesChecked">How many flat shapes the run saw after hierarchy elaboration.</param>
/// <param name="TechnologyName">
/// The technology the rules came from. Reported by every surface that shows a result: a layout with
/// no technology reference of its own resolves the WORKSPACE DEFAULT, and in a workspace holding two
/// processes that default may not be the one the designer has in mind. Naming it is the whole
/// mitigation and it costs nothing.
/// </param>
/// <param name="Diagnostics">Anything the run could not do, stated rather than dropped.</param>
public sealed record DrcRunResult(
    IReadOnlyList<DrcViolation> Violations,
    int                         RulesEvaluated,
    int                         LayersChecked,
    int                         ShapesChecked,
    string?                     TechnologyName,
    IReadOnlyList<string>       Diagnostics)
{
    public static DrcRunResult Empty(string? techName = null, IReadOnlyList<string>? diagnostics = null) =>
        new([], 0, 0, 0, techName, diagnostics ?? []);

    public int ErrorCount   => Violations.Count(v => !v.Waived && v.Severity == DrcSeverity.Error);
    public int WarningCount => Violations.Count(v => !v.Waived && v.Severity == DrcSeverity.Warning);
    public int WaivedCount  => Violations.Count(v => v.Waived);

    /// <summary>True when nothing outstanding remains — waived violations do not count.</summary>
    public bool IsClean => ErrorCount == 0 && WarningCount == 0;
}

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
/// <param name="Layer">The drawing layer checked.</param>
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
    LayerKey              Layer,
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
}

/// <summary>
/// A deliberate, persisted exception to one rule at one place (§9A.1: "real designs have deliberate
/// violations. Waiving must be per-violation, persisted, and visible, or people stop running DRC
/// entirely").
///
/// <para>Stored on the <see cref="LayoutView"/> that was checked, not on the technology — the
/// violation belongs to the artwork, and a technology shared by twenty cells must not accumulate one
/// cell's exceptions.</para>
/// </summary>
public sealed class DrcWaiver
{
    /// <summary><see cref="DrcViolation.Key"/> of the violation this waives.</summary>
    public string Key { get; set; } = "";

    /// <summary>Why. Free text, may be empty — but the UI asks for it, because a waiver with no
    /// reason is indistinguishable from a mistake six months later.</summary>
    public string Reason { get; set; } = "";

    /// <summary>The rule's name at the time of waiving, carried so a waiver that no longer matches
    /// anything can still be listed and removed by a human who recognises it.</summary>
    public string RuleName { get; set; } = "";
}

/// <summary>How a run was bounded and what it flattened at — see <see cref="DrcEngine.Run"/>.</summary>
/// <param name="MaxShapes">
/// v1 runs FLAT (§9A.1's "hierarchy answer"), so a deep hierarchy can elaborate into far more geometry
/// than a person expects. Above this the run refuses rather than hanging; the default is L3c's own
/// flatten ceiling, reused rather than re-derived because it guards the identical risk.
/// </param>
public sealed record DrcRunSettings(int MaxShapes = DrcEngine.DefaultMaxShapes)
{
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

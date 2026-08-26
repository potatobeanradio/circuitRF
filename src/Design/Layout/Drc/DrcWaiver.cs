// Split out of src/Ui/Layout/Drc/DrcModel.cs when the EM setup pipeline moved to CircuitRF.Design
// (brief-cli-em-verb.md R-emcli-3).
//
// WHY IT IS HERE AND THE REST OF DRC IS NOT: a waiver is persisted ON THE LAYOUT
// (LayoutView.DrcWaivers), so LayoutModel/LayoutPersistence — which are part of the `.clay` format —
// name this type. The DRC ENGINE that produces the violations a waiver suppresses stays in src/Ui,
// where it belongs. Moving DrcModel.cs whole would have dragged the engine, its layer/predicate
// expression evaluation and the wBond assembly rules across the firewall with it, for one class of
// three strings.

namespace CircuitRF.Design.Layout.Drc;

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
    /// <summary><c>DrcViolation.Key</c> of the violation this waives.</summary>
    public string Key { get; set; } = "";

    /// <summary>Why. Free text, may be empty — but the UI asks for it, because a waiver with no
    /// reason is indistinguishable from a mistake six months later.</summary>
    public string Reason { get; set; } = "";

    /// <summary>The rule's name at the time of waiving, carried so a waiver that no longer matches
    /// anything can still be listed and removed by a human who recognises it.</summary>
    public string RuleName { get; set; } = "";
}

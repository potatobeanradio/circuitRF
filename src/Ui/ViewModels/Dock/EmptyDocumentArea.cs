namespace CircuitRF.Ui.ViewModels.Dock;

/// <summary>
/// What the document area shows when NO document is open: the Welcome page's icon on its own, with
/// no text — not "No documents open", and not "Welcome to circuitRF" either (owner request,
/// 2026-09-04). The Welcome document PAGE is untouched; this is only the empty area behind it.
///
/// <para><b>Where the old text came from, since it cannot be found by grepping this repository.</b>
/// <c>Dock.Model.Mvvm</c>'s <c>DocumentDock.EmptyContent</c> defaults to the literal string
/// <c>"No documents open"</c>. The Fluent theme's <c>DocumentControl</c> template hands that to
/// <c>PART_EmptyContentHost</c>, whose <c>ContentTemplate</c> is template-bound to
/// <c>DocumentControl.EmptyContentTemplate</c>.</para>
///
/// <para><b>So the fix is in two halves, and both are deliberate.</b>
/// <list type="bullet">
/// <item><description><c>Styles/CircuitRfStyles.axaml</c> sets <c>EmptyContentTemplate</c> on
/// <c>DocumentControl</c> (and on <c>MdiDocumentControl</c>, for the layout mode the tab context
/// menu can switch to). This is what actually draws the icon, and it covers EVERY document dock —
/// including the ones Dock's own context menu creates (New Horizontal/Vertical Document Dock),
/// which this repository never constructs and therefore could not reach at the model end.</description></item>
/// <item><description><see cref="CircuitRfDockFactory"/> additionally sets
/// <c>EmptyContent = null</c> on each of the three document docks it builds itself, so the string is
/// gone from the model too. A null renders the template exactly as a non-null value does (verified
/// against the real theme), so the failure mode if that template is ever lost to a Dock upgrade is
/// an EMPTY area rather than the old text quietly returning.</description></item>
/// </list></para>
/// </summary>
internal static class EmptyDocumentArea
{
    /// <summary>
    /// The Material icon the empty area draws. It is the Welcome page's own icon, so this must stay
    /// equal to the <c>Kind</c> in <c>Views/Content/StubContentView.axaml</c> and to the two
    /// <c>EmptyContentTemplate</c> setters in <c>Styles/CircuitRfStyles.axaml</c> —
    /// <c>EmptyDocumentAreaTests</c> compares all four.
    /// </summary>
    internal const string IconKind = "IntegratedCircuitChip";
}

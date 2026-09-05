using System.Threading.Tasks;

namespace CircuitRF.Ui.Layout;

/// <summary>
/// Allows a <see cref="LayoutDocument"/> (and its view) to call into the workspace-level hierarchy
/// service without a direct reference to <c>WorkspaceViewModel</c>. Mirrors
/// <c>CircuitRF.Ui.Schematic.IHierarchyHost</c> exactly (brief-L3b-hierarchy-navigation.md §1) — same
/// shape, retargeted from <c>EditableComponent</c>/<c>SchematicEditModel</c> to
/// <see cref="LayoutInstance"/>/<see cref="LayoutEditorViewModel"/>. Injected at document creation
/// time via <see cref="LayoutDocument.Hierarchy"/>.
/// </summary>
public interface ILayoutHierarchyHost
{
    bool CanPushInto(LayoutInstance? instance, LayoutEditorViewModel? parentVm, out string? reason);
    void PushIntoCell(LayoutDocument doc, LayoutInstance instance);
    void PopOutOf(LayoutDocument doc);
    void PopToLevel(LayoutDocument doc, int frameIndex);
    void OpenCellInNewTab(LayoutDocument fromDoc, LayoutInstance instance);

    /// <summary>
    /// Saves <paramref name="doc"/> with the same behaviour as ⌘S single-doc scope:
    /// materialized → writes to its known path (plus every dirty pushed-in sub-cell frame);
    /// scratch → the Save-to-Cell/Save-as-File offer dialog. Registers the file/session and
    /// refreshes the project tree. The host resolves the owner window.
    /// </summary>
    Task SaveLayoutDocumentAsync(LayoutDocument doc);

    /// <summary>
    /// <b>Re-reference Cell…</b> for a broken instance — the layout counterpart of
    /// <c>IHierarchyHost.ReReferenceCellAsync</c>, and the same flow behind it: look for the cell
    /// first, ask the user to point at it only when that fails, record whatever reference the answer
    /// needs, and rewrite the document only when the reference itself changed.
    /// </summary>
    Task ReReferenceInstanceCellAsync(LayoutDocument doc, LayoutInstance instance);

    /// <summary>
    /// The <b>Reference Cell…</b> escape hatch in the cell picker: pick a cell folder anywhere on
    /// disk and take it into THIS workspace — by reference or by copy, through the same prompt and the
    /// same code as File ▸ Add Cell to Workspace… and the cross-workspace drag. Returns the absolute
    /// cell folder the chosen cell now occupies (unchanged for a reference, the new copy for a copy),
    /// or null when the user cancelled anywhere along the way.
    ///
    /// <para>Run by the CALLER after the picker has closed, never from inside it: the flow shows
    /// modal dialogs of its own on the same owner window.</para>
    /// </summary>
    Task<string?> ReferenceExternalCellAsync();

    /// <summary>False when there is no workspace to take a cell into — the picker then offers its
    /// plain folder-browse instead of the reference flow.</summary>
    bool CanReferenceExternalCell { get; }
}

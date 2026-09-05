using System.Threading.Tasks;

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// Allows a <see cref="SchematicDocument"/> (and its view) to call into the workspace-level
/// hierarchy service without a direct reference to <c>WorkspaceViewModel</c>.
/// Injected at document creation time via <see cref="SchematicDocument.Hierarchy"/>.
/// </summary>
public interface IHierarchyHost
{
    bool CanPushInto(EditableComponent? comp, SchematicEditModel? parentModel, out string? reason);
    void PushIntoCell(SchematicDocument doc, EditableComponent comp);
    void PopOutOf(SchematicDocument doc);
    void PopToLevel(SchematicDocument doc, int frameIndex);
    void OpenCellInNewTab(SchematicDocument fromDoc, EditableComponent comp);

    /// <summary>
    /// Saves <paramref name="doc"/> with the same behaviour as ⌘S single-doc scope:
    /// materialized → writes to its known path; scratch → the Save-to-Cell plan dialog.
    /// Registers the file/session and refreshes the project tree. The host resolves the owner window.
    /// </summary>
    Task SaveSchematicDocumentAsync(SchematicDocument doc);

    /// <summary>
    /// <b>Re-reference Cell…</b> — put a broken cell reference back together: look for the cell, and
    /// ask the user to point at it only when the search comes up empty or ambiguous. Records whatever
    /// reference the answer needs (an alias, a row in the Project Tree) and rewrites the document only
    /// when the reference itself actually changed.
    /// </summary>
    Task ReReferenceCellAsync(SchematicDocument doc, EditableComponent comp);
}

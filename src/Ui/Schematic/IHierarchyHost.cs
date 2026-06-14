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
}

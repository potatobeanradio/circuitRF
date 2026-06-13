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
}

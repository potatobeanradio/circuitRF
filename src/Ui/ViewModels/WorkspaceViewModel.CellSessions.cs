using System.IO;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.ViewModels;

/// <summary>brief-sdl-10 D7 — a cell's schematic session, for Make These the Cell's Own Values.</summary>
public partial class WorkspaceViewModel : ICellSchematicSessions
{
    SchematicViewModel ICellSchematicSessions.CellSession(string cschPath) => GetOrCreateSession(Path.GetFullPath(cschPath));

    void ICellSchematicSessions.SaveCellSession(string cschPath) => SaveSchematicByPath(Path.GetFullPath(cschPath));
}

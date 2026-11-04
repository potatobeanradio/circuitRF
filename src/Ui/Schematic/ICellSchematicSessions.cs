using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// brief-sdl-10 D7 — the workspace's session for a cell's schematic, for a command that changes a cell other than the
/// one being edited. The workspace resolves a placed cell through that session (unsaved edits included), so a value
/// written to the file alone is not the value the next Simulate reads, and a later save of the session would overwrite
/// it. Writing through the session and saving it keeps the two the same.
/// </summary>
public interface ICellSchematicSessions
{
    /// <summary>The session holding <paramref name="cschPath"/>, loaded from disk when none is open.</summary>
    SchematicViewModel CellSession(string cschPath);

    /// <summary>Writes the session holding <paramref name="cschPath"/> to its file and marks it saved.</summary>
    void SaveCellSession(string cschPath);
}

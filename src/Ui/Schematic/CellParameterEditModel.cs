namespace CircuitRF.Ui.Schematic;

/// <summary>
/// Framework-free mutable model for editing a cell's declared parameter interface.
/// Wraps a CcellFile loaded from .ccell and exposes its parameter list for
/// add / remove / rename / property-edit operations through commands.
/// Commands call <see cref="Save"/> then <see cref="NotifyChanged"/> after every mutation.
/// </summary>
public sealed class CellParameterEditModel
{
    private readonly CcellFile _file;

    public string CcellPath { get; }

    /// <summary>
    /// Fired by commands in both Execute and Undo so the ViewModel rebuilds its rows.
    /// </summary>
    public event EventHandler? Changed;

    public CellParameterEditModel(string ccellPath, CcellFile file)
    {
        CcellPath = ccellPath;
        _file     = file;
    }

    /// <summary>Current parameter list (read view for the ViewModel).</summary>
    public IReadOnlyList<CcellParameter> Parameters => _file.Parameters;

    /// <summary>
    /// Mutable list accessed only by commands.
    /// Each command adds/removes/mutates entries then calls <see cref="Save"/>
    /// and <see cref="NotifyChanged"/>.
    /// </summary>
    internal List<CcellParameter> MutableParameters => _file.Parameters;

    /// <summary>
    /// Persist the current file state to disk.  Called by every command after mutation.
    /// I/O errors propagate to the caller (the command); the ViewModel may surface them.
    /// </summary>
    public void Save() => CellPersistence.SaveToFile(CcellPath, _file);

    /// <summary>
    /// Notify observers that the parameter list has changed.
    /// Called by commands in both Execute and Undo.
    /// </summary>
    public void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);
}

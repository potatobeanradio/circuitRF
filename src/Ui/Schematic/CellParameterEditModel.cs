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

    /// <summary>Absolute directory of the cell folder (parent of .ccell).</summary>
    public string CellDir => Path.GetDirectoryName(CcellPath)!;

    /// <summary>
    /// Fired by commands in both Execute and Undo so the ViewModel rebuilds its rows.
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Fired (with the cell directory) when the primary symbol changes — including on undo.
    /// WorkspaceViewModel subscribes to invalidate the cell-symbol resolver.
    /// </summary>
    public event Action<string>? PrimarySymbolChanged;

    public CellParameterEditModel(string ccellPath, CcellFile file)
    {
        CcellPath = ccellPath;
        _file     = file;
    }

    /// <summary>
    /// Number of ports the cell declares.  The primary symbol's ExternalPortCount is fed
    /// from this value.  Commands use <see cref="SetNumPorts"/> to mutate it.
    /// </summary>
    public int NumPorts => _file.NumPorts;

    /// <summary>
    /// Fired (with the cell directory) when NumPorts changes — including on undo.
    /// WorkspaceViewModel subscribes to invalidate the cell-symbol resolver.
    /// </summary>
    public event Action<string>? PortCountChanged;

    internal void SetNumPorts(int value)
    {
        _file.NumPorts = value;
        Save();
        NotifyChanged();
        PortCountChanged?.Invoke(CellDir);
    }

    /// <summary>Current parameter list (read view for the ViewModel).</summary>
    public IReadOnlyList<CcellParameter> Parameters => _file.Parameters;

    /// <summary>
    /// Mutable list accessed only by commands.
    /// Each command adds/removes/mutates entries then calls <see cref="Save"/>
    /// and <see cref="NotifyChanged"/>.
    /// </summary>
    internal List<CcellParameter> MutableParameters => _file.Parameters;

    // ── Primary view accessors (read-only from outside; written via internal setters) ─

    /// <summary>Primary schematic filename (e.g. "amp.csch"), or null if none chosen.</summary>
    public string? PrimarySchematic => _file.PrimarySchematic;

    /// <summary>Primary symbol filename (e.g. "amp.csym"), or null if none chosen.</summary>
    public string? PrimarySymbol => _file.PrimarySymbol;

    // ── Internal mutation used only by SetCellPrimaryCommand ─────────────────────────

    internal void SetPrimarySchematic(string? value)
    {
        _file.PrimarySchematic = value;
        Save();
        NotifyChanged();
    }

    internal void SetPrimarySymbol(string? value)
    {
        _file.PrimarySymbol = value;
        Save();
        NotifyChanged();
        PrimarySymbolChanged?.Invoke(CellDir);
    }

    // ── Shared persistence + notification ────────────────────────────────────────────

    /// <summary>
    /// Persist the current file state to disk.  Called by every command after mutation.
    /// I/O errors propagate to the caller (the command); the ViewModel may surface them.
    /// </summary>
    public void Save() => CellPersistence.SaveToFile(CcellPath, _file);

    /// <summary>
    /// Notify observers that the model has changed.
    /// Called by commands in both Execute and Undo.
    /// </summary>
    public void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);
}

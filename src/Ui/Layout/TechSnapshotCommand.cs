using CircuitRF.Ui.Commands;

namespace CircuitRF.Ui.Layout;

/// <summary>
/// One coarse-grained undo entry for the .ctech editor: the whole <see cref="Technology"/>,
/// before and after one committed edit, each captured via <see cref="TechPersistence.Serialize"/>.
/// See <see cref="TechEditorViewModel"/>'s header for why this departs from the fine-grained
/// per-field <see cref="IUiCommand"/>s used elsewhere.
/// </summary>
internal sealed class TechSnapshotCommand : IUiCommand
{
    private readonly TechEditorViewModel _owner;
    private readonly string _beforeJson;
    private readonly string _afterJson;

    public string Description { get; }

    public TechSnapshotCommand(TechEditorViewModel owner, string beforeJson, string afterJson, string description)
    {
        _owner      = owner;
        _beforeJson = beforeJson;
        _afterJson  = afterJson;
        Description = description;
    }

    // Both directions replace Working wholesale — the edit that led here already happened in
    // place on the live object graph before this command was constructed, so Execute() is simply
    // "make Working equal the after-snapshot" (a no-op in content, not in reference identity).
    public void Execute() => _owner.ApplySnapshot(_afterJson);
    public void Undo()    => _owner.ApplySnapshot(_beforeJson);
}

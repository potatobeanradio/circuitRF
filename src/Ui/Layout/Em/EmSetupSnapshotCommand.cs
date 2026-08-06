using CircuitRF.Ui.Commands;

namespace CircuitRF.Ui.Layout.Em;

/// <summary>
/// One coarse-grained undo entry for the <c>.cem</c> editor: the whole <see cref="EmSetup"/>,
/// before and after one committed edit, each captured via <see cref="EmSetupPersistence.Serialize"/>.
/// Mirrors <c>TechSnapshotCommand</c> for the same reason it exists there — an EM setup is a
/// handful of scalars, so its own serializer doubles as an exact, trivial deep clone, and there is
/// nothing to gain from fine-grained per-field commands.
/// </summary>
internal sealed class EmSetupSnapshotCommand : IUiCommand
{
    private readonly EmSetupEditorViewModel _owner;
    private readonly string _beforeJson;
    private readonly string _afterJson;

    public string Description { get; }

    public EmSetupSnapshotCommand(EmSetupEditorViewModel owner, string beforeJson, string afterJson,
                                  string description)
    {
        _owner      = owner;
        _beforeJson = beforeJson;
        _afterJson  = afterJson;
        Description = description;
    }

    public void Execute() => _owner.ApplySnapshot(_afterJson);
    public void Undo()    => _owner.ApplySnapshot(_beforeJson);
}

using CircuitRF.Core.Devices;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Schematic;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Commands.Schematic;

/// <summary>
/// brief-cosim-pattern-2 R-cp2-8 — a plain SnP a fields analysis was refused on, made an EM Field Data block on the same run's record. A
/// saved document is never converted by loading it, because no record exists to point at until the setup runs again or the file is
/// adopted; this is the one click that does both, as one undoable edit. The adoption itself stays: it writes a new file beside the
/// result and touches none of its data.
/// </summary>
internal sealed class ChangeToEmFieldDataCommand : IUiCommand
{
    private readonly SchematicEditModel _model;
    private readonly EditableComponent _comp;
    private readonly List<EditableParameter> _old, _new;
    private readonly SymbolKind _oldKind;

    public string Description => "Change to EM Field Data";

    private ChangeToEmFieldDataCommand(SchematicEditModel model, EditableComponent comp, string storedRecord, int ports)
    {
        _model = model;
        _comp = comp;
        _oldKind = comp.Symbol;
        _old = [.. comp.Parameters.Select(p => p.Clone())];
        _new = [.. ComponentTypeRegistry.DefaultParameters(SymbolKind.EmFieldData, ports)
                   .Select(d => new EditableParameter { Name = d.Name, Expression = d.Expression, Unit = d.Unit, Dimension = d.Dimension, ShowOnSchematic = d.ShowOnSchematic })];
        _new.First(p => p.Name == "File").Expression = storedRecord;
    }

    public void Execute() => Apply(SymbolKind.EmFieldData, _new);
    public void Undo() => Apply(_oldKind, _old);

    private void Apply(SymbolKind kind, List<EditableParameter> parameters)
    {
        _comp.Symbol = kind;
        _comp.Parameters.Clear();
        _comp.Parameters.AddRange(parameters.Select(p => p.Clone()));
        _model.NotifyChanged();
    }

    /// <summary>The offer for <paramref name="plain"/>, or null when there is none to make: the block is not on this schematic's own sheet (a
    /// sub-cell's document is where it would change), or its result can neither be found nor adopted. The refusal stands either way.</summary>
    public static Offer? For(SchematicViewModel schematic, PlainSnpFieldsBlock plain)
    {
        var comp = schematic.EditModel.Components.FirstOrDefault(c => c.Symbol == SymbolKind.Snp && c.InstanceName == plain.Block);
        if (comp is null) return null;
        string record = Path.ChangeExtension(plain.SnpPath, EmFieldDataRecord.Extension);
        bool exists = File.Exists(record);
        if (!exists && EmResultRecord.AdoptionRefusal(plain.SnpPath) is not null) return null;
        bool refPin = comp.Parameters.FirstOrDefault(p => p.Name == "RefNode")?.Expression.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
        string sentence = $"'{plain.Block}' can be an {EmFieldDataRecord.DisplayName} block on '{Path.GetFileName(record)}'" +
                          (exists ? "." : ", which would be written beside its Touchstone file.") +
                          (refPin ? " Its reference pin goes with the change: the block's ports return to ground." : "");
        return new Offer(sentence, () =>
        {
            if (!exists && EmResultRecord.Adopt(plain.SnpPath) is { RecordPath: null }) return;
            int ports = EmFieldDataRecord.ReadPortNames(record)?.Count ?? comp.PortCount;
            string stored = SnpPathPolicy.ToStored(record, schematic.WorkspaceRoot, schematic.EditModel.SchematicDirectory);
            schematic.Execute(new ChangeToEmFieldDataCommand(schematic.EditModel, comp, stored, ports));
        });
    }

    internal sealed record Offer(string Sentence, Action Apply);
}

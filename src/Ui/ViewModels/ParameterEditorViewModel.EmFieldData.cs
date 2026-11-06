using CircuitRF.Core.Devices;
using CircuitRF.Design.Layout.Em;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// brief-cosim-pattern-2 — the EM Field Data block's panel: its record, one read-only line from it, and the settings that run used.
/// </summary>
public sealed partial class ParameterEditorViewModel
{
    public bool IsEmFieldData => _target?.Symbol == SymbolKind.EmFieldData;

    /// <summary>When, which solver and quality, the sweep and the port count; or why there is nothing to say.</summary>
    [ObservableProperty] private string _emFieldSummary = "";

    /// <summary>R-cp2-2 — set when the record no longer holds the ports the block is drawn with.</summary>
    [ObservableProperty] private string _emFieldPinsNote = "";

    public Func<Task<string?>>? PickEmFieldFileAsync { get; set; }

    /// <summary>Asks whether to write a record for an older result: the Touchstone's name and the record's.</summary>
    public Func<string, string, Task<bool>>? ConfirmAdoptAsync { get; set; }

    /// <summary>Takes the record's resolved path, which is null or names no file when there is nothing to read; the dialog then says why.</summary>
    public Func<string?, Task>? ShowEmRunSettingsAsync { get; set; }

    [RelayCommand]
    private async Task PickEmFieldFile()
    {
        if (_target is null || _schematicVm is null || PickEmFieldFileAsync is null) return;
        string? path = await PickEmFieldFileAsync();
        if (path is null) return;
        if (await RecordFor(path) is { } record) SetSnpFile(_schematicVm, _target, record);
    }

    /// <summary>
    /// R-cp2-1 — the record to point at for what was picked. A <c>.cemr</c> is itself. A Touchstone with a record beside it is that record,
    /// and one without is offered <see cref="EmResultRecord.Adopt"/>; a refusal is shown on the panel's line and changes nothing.
    /// </summary>
    internal async Task<string?> RecordFor(string picked)
    {
        if (picked.EndsWith(EmFieldDataRecord.Extension, StringComparison.OrdinalIgnoreCase)) return picked;
        string beside = Path.ChangeExtension(picked, EmFieldDataRecord.Extension);
        if (File.Exists(beside)) return beside;
        if (EmResultRecord.AdoptionRefusal(picked) is { } refusal) { EmFieldSummary = refusal; return null; }
        if (ConfirmAdoptAsync is null || !await ConfirmAdoptAsync(Path.GetFileName(picked), Path.GetFileName(beside))) return null;
        var (record, refused) = EmResultRecord.Adopt(picked);
        if (record is null) EmFieldSummary = refused!;
        return record;
    }

    private string? ResolvedEmFieldRecord()
        => _target is null || _schematicVm is null ? null
         : CircuitRF.Design.Schematic.SnpPathPolicy.Resolve(_target.Parameters.FirstOrDefault(p => p.Name == "File")?.Expression,
                                                            _schematicVm.WorkspaceRoot, _schematicVm.EditModel.SchematicDirectory);

    [RelayCommand]
    private async Task ShowEmRunSettings()
    {
        if (ShowEmRunSettingsAsync is not null) await ShowEmRunSettingsAsync(ResolvedEmFieldRecord());
    }

    private void RefreshEmFieldDataProperties()
    {
        if (_target?.Symbol != SymbolKind.EmFieldData) return;
        _isRefreshing = true;
        SnpFilePath      = _target.Parameters.FirstOrDefault(p => p.Name == "File")?.Expression ?? "";
        ShowFileLabel    = _target.Parameters.FirstOrDefault(p => p.Name == "File")?.ShowOnSchematic ?? false;
        ShowFileNameOnly = _target.ShowFileNameOnly;
        _isRefreshing = false;

        string? path = ResolvedEmFieldRecord();
        var (opened, refusal) = EmFieldDataRecord.Open(path);
        var (record, _) = path is not null && File.Exists(path) ? EmResultRecord.Load(path) : (null, null);
        EmFieldSummary = opened is null && refusal is not null ? refusal
                       : record is null ? ""
                       : record.Setup is null ? $"{record.Summary()}; {EmResultRecord.SettingsNotRecorded}"
                       : record.Summary();
        int? ports = EmFieldDataRecord.ReadPortNames(path)?.Count;
        EmFieldPinsNote = ports is { } n && n != _target.PortCount
            ? $"The record now holds {n} port{(n == 1 ? "" : "s")} and this block is drawn with {_target.PortCount} pin{(_target.PortCount == 1 ? "" : "s")}. Browse… and pick the record again to update them."
            : "";
    }
}

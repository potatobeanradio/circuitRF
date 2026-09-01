using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CircuitRF.Ui.Commands.Schematic;
using CircuitRF.Ui.Schematic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// The SpiceModel panel of the Parameter Editor — SnP's panel, asking SPICE's two questions instead
/// of Touchstone's one.
///
/// <h3>The two questions, and why there are two</h3>
/// <para><b>Which FILE</b> is the same question SnP asks, and gets the same two buttons: Browse…
/// and Show, reusing SnP's own picker and reveal seams rather than a second pair.</para>
///
/// <para><b>Which DEFINITION in it</b> has no SnP counterpart, and it cannot be dropped: a
/// Touchstone file is one network, while a vendor SPICE file is routinely a part plus every piece
/// the part is built from, each of them a definition in its own right. So the combo lists
/// everything the file holds, and a blank value resolves to the HIGHEST-LEVEL supported definition —
/// the one nothing else in the file calls (owner, 2026-09-01). Taking the first instead would place
/// an internal transistor where the user asked for the package around it.</para>
///
/// <h3>Pins and Pitch appear only for a subcircuit</h3>
/// <para>They lay out a BOX, and a <c>.model</c> card does not draw as one — it draws as the
/// circuitRF device that implements it, whose terminals are where that device's terminals are. Two
/// combos that visibly do nothing are worse than two that are absent, so they are hidden rather
/// than disabled.</para>
///
/// <h3>The generic rows below the panel are the subcircuit's own parameters</h3>
/// <para>A <c>.subckt</c> line declares them, so they are adopted onto the instance at its declared
/// default and forwarded as overrides at extraction — the same mechanism, and the same no-undo
/// no-value-change rule, as <c>AdoptCellDeclaredParameters</c>. A card has none: its parameters ARE
/// the card, and editing one on the instance would mean the schematic no longer says what the file
/// says.</para>
/// </summary>
public sealed partial class ParameterEditorViewModel
{
    /// <summary>True only for a placed SpiceModel — gates the whole panel.</summary>
    public bool IsSpiceModel => _target?.Symbol == SymbolKind.SpiceModel;

    public IAsyncRelayCommand PickSpiceModelFileCommand { get; private set; } = null!;
    public IAsyncRelayCommand ShowSpiceModelFileCommand { get; private set; } = null!;

    [ObservableProperty] private string _spiceModelFilePath = "";

    /// <summary>Every definition the chosen file holds, as the combo shows them.</summary>
    public ObservableCollection<string> SpiceModelNameOptions { get; } = [];

    [ObservableProperty] private int _spiceModelNameIndex = -1;

    /// <summary>What was found, or the reason it cannot be run. Never both, never empty when a file is set.</summary>
    [ObservableProperty] private string _spiceModelStatus = "";

    /// <summary>True when <see cref="SpiceModelStatus"/> is a refusal rather than a description.</summary>
    [ObservableProperty] private bool _spiceModelStatusIsProblem;

    public bool HasSpiceModelStatus => SpiceModelStatus.Length > 0;

    partial void OnSpiceModelStatusChanged(string value) => OnPropertyChanged(nameof(HasSpiceModelStatus));

    partial void OnSpiceModelFilePathChanged(string value) => ShowSpiceModelFileCommand.NotifyCanExecuteChanged();

    /// <summary>True only while the selected definition is a <c>.subckt</c> — see the class remarks.</summary>
    [ObservableProperty] private bool _spiceModelShowPinLayout;

    [ObservableProperty] private int _spiceModelPinConfigIndex;
    [ObservableProperty] private int _spiceModelPitchIndex = 1;   // Loose

    /// <summary>The definitions behind <see cref="SpiceModelNameOptions"/>, index for index.</summary>
    private IReadOnlyList<SpiceModelDefinition> _spiceModelDefinitions = [];

    // ── Writes ────────────────────────────────────────────────────────────────

    partial void OnSpiceModelNameIndexChanged(int oldValue, int newValue)
    {
        if (_isRefreshing || _target is null || _schematicVm is null) return;
        if ((uint)newValue >= (uint)_spiceModelDefinitions.Count) return;

        ApplySpiceModelChoice(SpiceModelFilePath, _spiceModelDefinitions[newValue].Name);
    }

    partial void OnSpiceModelPinConfigIndexChanged(int oldValue, int newValue)
    {
        if (_isRefreshing || _target is null || _schematicVm is null) return;
        string val = (uint)newValue < (uint)SnpPinConfigOptions.Length ? SnpPinConfigOptions[newValue] : "Standard";
        ApplySpiceModelParam("PinConfig", val);
    }

    partial void OnSpiceModelPitchIndexChanged(int oldValue, int newValue)
    {
        if (_isRefreshing || _target is null || _schematicVm is null) return;
        string val = (uint)newValue < (uint)SnpPitchOptions.Length ? SnpPitchOptions[newValue] : "Loose";
        ApplySpiceModelParam("Pitch", val);
    }

    private void ApplySpiceModelParam(string name, string value)
    {
        var newParams = _target!.Parameters.Select(p => p.Clone()).ToList();
        var param = newParams.FirstOrDefault(p => p.Name == name);
        if (param is not null) param.Expression = value;
        else newParams.Add(new EditableParameter { Name = name, Expression = value });
        _schematicVm!.Execute(new SetParametersCommand(_schematicVm.EditModel, _target, newParams));
    }

    private async Task PickSpiceModelFileAsync()
    {
        if (_target is null || _schematicVm is null || PickSpiceFileAsync is null) return;
        string? picked = await PickSpiceFileAsync();
        if (picked is null) return;

        // Portable when it can be, exactly as a picked Touchstone path is.
        string stored = SnpPathPolicy.ToStored(picked, _schematicVm.WorkspaceRoot);

        // A NEW file means a new set of definitions, so the name is cleared and re-resolved rather
        // than carried over: the old name almost certainly does not exist in the new file, and a
        // stale one would report "not defined in" for something the user never typed.
        ApplySpiceModelChoice(stored, "");
    }

    private async Task RevealSpiceModelFileAsync()
    {
        if (RevealFileAsync is null || _target is null) return;
        string? path = SpiceModelSymbolProvider.ResolvePath(
            SpiceModelFilePath, _schematicVm?.EditModel.SchematicDirectory);
        if (path is null) return;
        await RevealFileAsync(path);
    }

    /// <summary>
    /// Writes the file and the definition together, and re-seeds the instance's parameter rows from
    /// whatever the new definition declares — <b>one undo entry for all three</b>.
    ///
    /// <para>They must move together. A subcircuit's parameters belong to the definition that
    /// declares them, so leaving the previous one's rows behind would show a user the wrong list and
    /// then quietly drop them at extraction (the emitted overrides are filtered to what the cell
    /// actually declares). This is the one place rows are REMOVED — a plain refresh only ever adds,
    /// because opening a dialog is not an edit.</para>
    /// </summary>
    private void ApplySpiceModelChoice(string file, string name)
    {
        if (_target is null || _schematicVm is null) return;

        var newParams = _target.Parameters.Select(p => p.Clone()).ToList();

        Set(SpiceModelSymbolProvider.FileParameter, file);
        Set(SpiceModelSymbolProvider.NameParameter, name);

        var declared = DeclaredParametersOf(file, name);

        // Out with the previous definition's rows, in with this one's — keeping any value the user
        // had already typed for a name that survives the change.
        var kept = newParams
            .Where(p => SpiceModelSymbolProvider.IsPanelParameter(p.Name))
            .ToList();

        foreach (var d in declared)
        {
            var existing = newParams.FirstOrDefault(
                p => p.Name.Equals(d.Name, StringComparison.OrdinalIgnoreCase)
                     && !SpiceModelSymbolProvider.IsPanelParameter(p.Name));
            kept.Add(existing ?? d);
        }

        _schematicVm.Execute(new SetParametersCommand(_schematicVm.EditModel, _target, kept));
        RefreshSpiceModelProperties();

        void Set(string n, string v)
        {
            var p = newParams.FirstOrDefault(q => q.Name == n);
            if (p is not null) p.Expression = v;
            else newParams.Add(new EditableParameter { Name = n, Expression = v, ShowOnSchematic = true });
        }
    }

    // ── Reads ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Gives a placed SpiceModel any parameter its chosen <c>.subckt</c> declares but the instance
    /// does not yet carry, at the definition's own default. Never removes, never changes a value,
    /// never lands on the undo stack — <c>AdoptCellDeclaredParameters</c>' rule, for the same
    /// reason: a file that GAINS a parameter must not leave every instance placed before then
    /// without a row for it, and opening a dialog is not an edit.
    /// </summary>
    private void AdoptSpiceModelDeclaredParameters(EditableComponent comp)
    {
        foreach (var d in ComponentTypeRegistry.DefaultParameters(SymbolKind.SpiceModel, 0))
        {
            if (comp.Parameters.Any(p => p.Name.Equals(d.Name, StringComparison.Ordinal))) continue;
            comp.Parameters.Add(new EditableParameter
            {
                Name = d.Name, Expression = d.Expression, Unit = d.Unit,
                Dimension = d.Dimension, ShowOnSchematic = d.ShowOnSchematic,
            });
        }

        string file = ParamValue(comp, SpiceModelSymbolProvider.FileParameter);
        string name = ParamValue(comp, SpiceModelSymbolProvider.NameParameter);

        foreach (var d in DeclaredParametersOf(file, name))
        {
            if (comp.Parameters.Any(p => p.Name.Equals(d.Name, StringComparison.OrdinalIgnoreCase))) continue;
            comp.Parameters.Add(d);
        }
    }

    /// <summary>
    /// The rows a definition declares, ready to place on an instance. Empty for a <c>.model</c>
    /// card, for a file that does not read, and for a definition that is refused — in every one of
    /// those there is nothing the user could usefully set.
    /// </summary>
    private List<EditableParameter> DeclaredParametersOf(string file, string name)
    {
        var def = ResolveDefinition(file, name, out _);
        if (def?.Candidate.Subcircuit is not { IsSupported: true } sub) return [];

        return [.. sub.Definition.Parameters
            .Where(d => !string.IsNullOrWhiteSpace(d.Name)
                        && !SpiceModelSymbolProvider.IsPanelParameter(d.Name))
            .Select(d => new EditableParameter
            {
                Name            = d.Name,
                Expression      = d.DefaultExpression,
                ShowOnSchematic = false,
            })];
    }

    /// <summary>Reads a file + name pair through the peek, resolved against this schematic.</summary>
    private SpiceModelDefinition? ResolveDefinition(string file, string name, out SpiceModelFile peeked)
    {
        peeked = SpiceModelFile.Empty;
        if (string.IsNullOrWhiteSpace(file)) return null;

        string? path = SpiceModelSymbolProvider.ResolvePath(
            file, _schematicVm?.EditModel.SchematicDirectory);
        if (path is null) return null;

        peeked = SpiceModelPeek.Read(path);
        return peeked.Error is null ? SpiceModelPeek.Select(peeked, name) : null;
    }

    private void RefreshSpiceModelProperties()
    {
        if (_target?.Symbol != SymbolKind.SpiceModel) return;

        string file = ParamValue(_target, SpiceModelSymbolProvider.FileParameter);
        string name = ParamValue(_target, SpiceModelSymbolProvider.NameParameter);

        var def = ResolveDefinition(file, name, out var peeked);

        int cfgIdx = Array.IndexOf(SnpPinConfigOptions,
            ParamValue(_target, "PinConfig") is { Length: > 0 } c ? c : "Standard");
        if (cfgIdx < 0) cfgIdx = 0;
        int pitchIdx = Array.IndexOf(SnpPitchOptions,
            ParamValue(_target, "Pitch") is { Length: > 0 } t ? t : "Loose");
        if (pitchIdx < 0) pitchIdx = 1;

        _isRefreshing = true;

        SpiceModelFilePath = file;

        _spiceModelDefinitions = peeked.Definitions;
        SpiceModelNameOptions.Clear();
        foreach (var d in peeked.Definitions) SpiceModelNameOptions.Add(d.DisplayLabel);
        SpiceModelNameIndex = def is null ? -1 : peeked.Definitions.ToList().IndexOf(def);

        (SpiceModelStatus, SpiceModelStatusIsProblem) = DescribeSpiceModelState(file, name, def, peeked);
        SpiceModelShowPinLayout = def is { IsSubcircuit: true, Refusal: null };
        SpiceModelPinConfigIndex = cfgIdx;
        SpiceModelPitchIndex     = pitchIdx;

        _isRefreshing = false;

        OnPropertyChanged(nameof(HasSpiceModelStatus));
    }

    /// <summary>
    /// The one line under the file box: what will be simulated, or exactly why nothing will be.
    ///
    /// <para>The refusal is shown HERE, when the file is chosen, rather than only at Run — the
    /// difference between a minute and a wrong measurement built on a part that never resolved.
    /// This is the SAME sentence the extractor reports, because both come from the same peek.</para>
    /// </summary>
    private static (string Text, bool IsProblem) DescribeSpiceModelState(
        string file, string name, SpiceModelDefinition? def, SpiceModelFile peeked)
    {
        if (file.Length == 0)
            return ("No file chosen — this component draws as a generic two-port and will not simulate.", true);

        if (peeked.Error is { } error) return (error, true);

        if (def is null)
            return (peeked.Definitions.Count == 0
                        ? "This file holds no '.model' cards and no '.subckt' definitions."
                        : $"'{name}' is not defined in this file. It defines: "
                          + string.Join(", ", peeked.Definitions.Select(d => d.Name)) + ".",
                    true);

        if (def.Refusal is { } refusal) return (refusal, true);

        string ports = def.PortNames.Count == 1 ? "1 pin" : $"{def.PortNames.Count} pins";
        string chosen = name.Length == 0 ? " (chosen automatically)" : "";
        return ($"{def.TypeLabel} {def.Name}{chosen} — {def.Detail}; {ports}: "
                + string.Join(", ", def.PortNames) + ".", false);
    }
}

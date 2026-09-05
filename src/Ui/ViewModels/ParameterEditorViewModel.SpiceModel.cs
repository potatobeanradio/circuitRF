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

    /// <summary>
    /// Whether the <c>Name</c> parameter draws its own label on the sheet — the standard
    /// "Show in schematic" checkbox every generic parameter row carries, brought onto this panel
    /// because <c>Name</c> has no generic row to carry it (owner, 2026-09-01).
    ///
    /// <para>It is the same flag and the same undo entry the row checkbox writes
    /// (<see cref="SetParameterVisibilityCommand"/>) — not a second setting that could disagree with
    /// it — so an instance saved with the label off reads back with the box clear.</para>
    /// </summary>
    [ObservableProperty] private bool _spiceModelShowNameOnSchematic = true;

    partial void OnSpiceModelShowNameOnSchematicChanged(bool value)
    {
        if (_isRefreshing || _target is null || _schematicVm is null) return;

        var p = _target.Parameters.FirstOrDefault(
            q => q.Name == SpiceModelSymbolProvider.NameParameter);
        if (p is null || p.ShowOnSchematic == value) return;

        _schematicVm.Execute(new SetParameterVisibilityCommand(_schematicVm.EditModel, p, value));
    }

    /// <summary>What was found, or the reason it cannot be run. Never both, never empty when a file is set.</summary>
    [ObservableProperty] private string _spiceModelStatus = "";

    /// <summary>True when <see cref="SpiceModelStatus"/> is a refusal rather than a description.</summary>
    [ObservableProperty] private bool _spiceModelStatusIsProblem;

    public bool HasSpiceModelStatus => SpiceModelStatus.Length > 0;

    partial void OnSpiceModelStatusChanged(string value) => OnPropertyChanged(nameof(HasSpiceModelStatus));

    partial void OnSpiceModelFilePathChanged(string value) => ShowSpiceModelFileCommand.NotifyCanExecuteChanged();

    /// <summary>True only while the selected definition is a <c>.subckt</c> — see the class remarks.</summary>
    [ObservableProperty] private bool _spiceModelShowPinLayout;

    /// <summary>
    /// The <c>.lib</c> sections the chosen file offers, with "Whole file" first — which is what a
    /// blank <c>Section</c> means and what every file that declares none is read as.
    /// </summary>
    public ObservableCollection<string> SpiceModelSectionOptions { get; } = [];

    [ObservableProperty] private int _spiceModelSectionIndex;

    /// <summary>
    /// True only when the file declares sections. <b>Hidden rather than disabled</b> for the files
    /// that do not — which is nearly all of them — for the same reason Pins and Pitch are hidden on a
    /// <c>.model</c> card: a combo with one entry that can never change is a question with no answer.
    /// </summary>
    [ObservableProperty] private bool _spiceModelShowSections;

    /// <summary>The section names behind <see cref="SpiceModelSectionOptions"/>, offset by the leading
    /// "Whole file" entry. Index 0 of the combo is no section at all.</summary>
    private IReadOnlyList<string> _spiceModelSections = [];

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

    /// <summary>
    /// Choosing a section re-reads the file, so the definition list, the declared parameter rows and
    /// the symbol all change together — the same all-at-once rule choosing a FILE follows, and for
    /// the same reason: the previous section's definition almost certainly is not in this one.
    /// </summary>
    partial void OnSpiceModelSectionIndexChanged(int oldValue, int newValue)
    {
        if (_isRefreshing || _target is null || _schematicVm is null) return;
        if ((uint)newValue > (uint)_spiceModelSections.Count) return;

        ApplySpiceModelChoice(
            SpiceModelFilePath, "",
            newValue <= 0 ? "" : _spiceModelSections[newValue - 1]);
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

        // Portable when it can be, exactly as a picked Touchstone path is — and against the same
        // root SpiceModelSymbolProvider.ResolvePath will read it back with, which is NOT the open
        // window's workspace root. See SpiceModelSymbolProvider.ToStored.
        string stored = SpiceModelSymbolProvider.ToStored(
            picked, _schematicVm.EditModel.SchematicDirectory);

        // A NEW file means a new set of definitions, so the name is cleared and re-resolved rather
        // than carried over: the old name almost certainly does not exist in the new file, and a
        // stale one would report "not defined in" for something the user never typed.
        ApplySpiceModelChoice(stored, "", "");
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
    /// <param name="section">
    /// Which section, or null to keep whatever the instance already carries — the ordinary case, since
    /// only the Section combo itself changes it. Choosing a new FILE passes "" instead, because a
    /// section named in one file means nothing in another.
    /// </param>
    private void ApplySpiceModelChoice(string file, string name, string? section = null)
    {
        if (_target is null || _schematicVm is null) return;

        var newParams = _target.Parameters.Select(p => p.Clone()).ToList();

        section ??= ParamValue(_target, SpiceModelSymbolProvider.SectionParameter);

        Set(SpiceModelSymbolProvider.FileParameter, file);
        Set(SpiceModelSymbolProvider.NameParameter, name);
        Set(SpiceModelSymbolProvider.SectionParameter, section);

        var declared = DeclaredParametersOf(file, name, section);

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

        foreach (var d in DeclaredParametersOf(
                     file, name, ParamValue(comp, SpiceModelSymbolProvider.SectionParameter)))
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
    private List<EditableParameter> DeclaredParametersOf(string file, string name, string section)
    {
        var def = ResolveDefinition(file, name, section, out _);
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
    private SpiceModelDefinition? ResolveDefinition(
        string file, string name, string section, out SpiceModelFile peeked)
    {
        peeked = SpiceModelFile.Empty;
        if (string.IsNullOrWhiteSpace(file)) return null;

        string? path = SpiceModelSymbolProvider.ResolvePath(
            file, _schematicVm?.EditModel.SchematicDirectory);
        if (path is null) return null;

        peeked = SpiceModelPeek.Read(path, section);
        return peeked.Error is null ? SpiceModelPeek.Select(peeked, name) : null;
    }

    private void RefreshSpiceModelProperties()
    {
        if (_target?.Symbol != SymbolKind.SpiceModel) return;

        string file    = ParamValue(_target, SpiceModelSymbolProvider.FileParameter);
        string name    = ParamValue(_target, SpiceModelSymbolProvider.NameParameter);
        string section = ParamValue(_target, SpiceModelSymbolProvider.SectionParameter);

        var def = ResolveDefinition(file, name, section, out var peeked);

        int cfgIdx = Array.IndexOf(SnpPinConfigOptions,
            ParamValue(_target, "PinConfig") is { Length: > 0 } c ? c : "Standard");
        if (cfgIdx < 0) cfgIdx = 0;
        int pitchIdx = Array.IndexOf(SnpPitchOptions,
            ParamValue(_target, "Pitch") is { Length: > 0 } t ? t : "Loose");
        if (pitchIdx < 0) pitchIdx = 1;

        _isRefreshing = true;

        SpiceModelFilePath = file;
        SpiceModelShowNameOnSchematic = _target.Parameters
            .FirstOrDefault(p => p.Name == SpiceModelSymbolProvider.NameParameter)
            ?.ShowOnSchematic ?? true;

        _spiceModelDefinitions = peeked.Definitions;
        SpiceModelNameOptions.Clear();
        foreach (var d in peeked.Definitions) SpiceModelNameOptions.Add(d.DisplayLabel);
        SpiceModelNameIndex = def is null ? -1 : peeked.Definitions.ToList().IndexOf(def);

        // The section names come from the same read: the reader records a `.LIB` frame it is
        // SKIPPING as well as one it is reading, so one pass answers both "which are there" and
        // "what is in the chosen one".
        _spiceModelSections = peeked.Scan.SectionNames;
        SpiceModelSectionOptions.Clear();
        SpiceModelSectionOptions.Add("Whole file (no section)");
        foreach (var sec in _spiceModelSections) SpiceModelSectionOptions.Add(sec);
        SpiceModelShowSections = _spiceModelSections.Count > 0;

        int secIdx = section.Length == 0 ? 0 : IndexOfSection(_spiceModelSections, section) + 1;
        SpiceModelSectionIndex = secIdx > 0 ? secIdx : 0;

        (SpiceModelStatus, SpiceModelStatusIsProblem) =
            DescribeSpiceModelState(file, name, section, def, peeked);
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
    private static int IndexOfSection(IReadOnlyList<string> sections, string wanted)
    {
        for (int i = 0; i < sections.Count; i++)
            if (sections[i].Equals(wanted, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private static (string Text, bool IsProblem) DescribeSpiceModelState(
        string file, string name, string section, SpiceModelDefinition? def, SpiceModelFile peeked)
    {
        if (file.Length == 0)
            return ("No file chosen — this component draws as a generic two-port and will not simulate.", true);

        // Checked BEFORE the generic error, because a section the file does not declare is exactly
        // what makes it read nothing — and "this file holds no definitions" would then be a true
        // sentence about the read and a false one about the file.
        if (section.Length > 0 && peeked.Scan.SectionNames.Count > 0 &&
            IndexOfSection(peeked.Scan.SectionNames, section) < 0)
            return ($"This file does not declare a section called '{section}'. It offers: "
                    + string.Join(", ", peeked.Scan.SectionNames) + ".", true);

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

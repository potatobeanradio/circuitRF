using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.ViewModels;

/// <summary>One row of the part picker's drop-down: a part of the placed part's own library file.</summary>
public sealed class VendorPartChoice(VendorLibraryEntry entry)
{
    public VendorLibraryEntry Entry { get; } = entry;
    public string Part   => Entry.Part;
    public string Value  => Entry.Header.ValueText();

    /// <summary>The value with the part's tolerance after it (brief vendorlib-5 R-vl5-4c): <c>10 pF ±5 %</c>.</summary>
    public string ValueWithTolerance => Entry.Tolerance is { } t ? $"{Value} {t.DisplayText}" : Value;

    /// <summary>The second line: the description and the frequency range, whichever are stated.</summary>
    public string Detail => string.Join("  ·  ",
        new[] { Entry.Header.Description, Entry.Header.FrequencyRangeText() }.Where(s => !string.IsNullOrEmpty(s)));
}

/// <summary>
/// The SpiceModel panel for a VENDOR LIBRARY part (brief vendorlib-3 R-vl3-1): the library and file
/// as text, a Part field that searches the file's index entries as the user types, the part's
/// description and frequency range, and Value ▲ / ▼.
///
/// <h3>Selecting the instance parses nothing</h3>
/// <para>A library file can hold 12,000 parts in 14 MB. The file-based panel lists every definition
/// a whole-file parse finds; here every list and every line comes from the library's in-memory index
/// (<see cref="VendorPartSource.IndexedFileEntries"/>) — no <see cref="SpiceModelPeek"/> call, no
/// <c>SpiceNetlistReader</c> call, no file read. While the library is still being indexed the panel
/// says so and fills in when the index lands.</para>
///
/// <h3>A part change is one gesture</h3>
/// <para>Choosing a part and stepping the value both go through
/// <see cref="SchematicViewModel.ChangeVendorPart"/>, which writes the part, its glyph and — when it
/// came from the part — its footprint as ONE undo entry, and leaves the instance where it is.</para>
/// </summary>
public sealed partial class ParameterEditorViewModel
{
    /// <summary>True for a placed SpiceModel whose <c>File</c> names a vendor library part.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFileSpiceModel), nameof(ShowVendorPartPanel))]
    private bool _isVendorPart;

    /// <summary>The file-based SpiceModel panel — every SpiceModel that is not a vendor part. Unchanged
    /// from before vendor libraries (R-vl3-1c).</summary>
    public bool IsFileSpiceModel => IsSpiceModel && !IsVendorPart;

    /// <summary>The vendor part panel.</summary>
    public bool ShowVendorPartPanel => IsSpiceModel && IsVendorPart;

    /// <summary>The library's folder, or null when it does not resolve — where Help looks for documents.</summary>
    public string? VendorLibraryRoot { get; private set; }

    /// <summary>The library's name, as the workspace references it.</summary>
    [ObservableProperty] private string _vendorLibraryName = "";

    /// <summary>The file, relative to the library's folder.</summary>
    [ObservableProperty] private string _vendorLibraryFile = "";

    /// <summary>The Part field's text — the placed part's name, or what the user is typing.</summary>
    [ObservableProperty] private string _vendorPartQuery = "";

    /// <summary>What the drop-down offers for <see cref="VendorPartQuery"/>: the file's parts that match.</summary>
    public ObservableCollection<VendorPartChoice> VendorPartMatches { get; } = [];

    [ObservableProperty] private bool _isVendorPartListOpen;

    /// <summary>The drop-down's highlighted row — the placed part when the whole file is shown.</summary>
    [ObservableProperty] private VendorPartChoice? _selectedVendorPartMatch;

    [ObservableProperty] private string _vendorPartValue = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVendorPartDescription))]
    private string _vendorPartDescription = "";

    /// <summary>An inductor's header states no Description, and its line is then not drawn at all.</summary>
    public bool HasVendorPartDescription => VendorPartDescription.Length > 0;
    [ObservableProperty] private string _vendorPartRange = "";

    /// <summary>The Tolerance row (brief vendorlib-5 R-vl5-4a): <c>±0.1 pF</c>, or <c>not known</c> — the reason is
    /// <c>check</c>'s and <c>find</c>'s to give, never a line under the row.</summary>
    [ObservableProperty] private string _vendorPartTolerance = "";

    /// <summary>Why there is no part to show — indexing, a missing library, a vanished part — or empty.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVendorPartStatus))]
    private string _vendorPartStatus = "";

    [ObservableProperty] private bool _vendorPartStatusIsProblem;

    public bool HasVendorPartStatus => VendorPartStatus.Length > 0;

    /// <summary>True while the part's own lines (value, description, range) have something to say.</summary>
    [ObservableProperty] private bool _hasVendorPartDetail;

    public IRelayCommand StepVendorValueUpCommand   { get; private set; } = null!;
    public IRelayCommand StepVendorValueDownCommand { get; private set; } = null!;

    /// <summary>The placed part's key in the schematic's tuning setup: <c>C1.Part</c>.</summary>
    private string? VendorTuneKey => _target is null ? null : $"{_target.InstanceName}.{CircuitRF.Core.Design.TunableKey.PartParameter}";

    // The part's two toggles (brief vendorlib-4 R-vl4-5c), the same toggles every parameter row has and on the same
    // surfaces: Tune adds the part's Part entry to the Tuning panel, Optimize to the Optimizer — each one undo step,
    // the entry created with the parts of the same kind (TuningSetupEdits.NewEntry). Absent, not greyed, where the
    // session has no such panel or the part is not resolved.

    /// <summary>The part carries the Tune toggle.</summary>
    public bool CanTuneVendorPart => IsVendorPart && _vendorCurrent is not null && _schematicVm?.Tuning is not null;

    /// <summary>The part carries the Optimize toggle.</summary>
    public bool CanOptimizeVendorPart => IsVendorPart && _vendorCurrent is not null && _schematicVm?.Optimize is not null;

    /// <summary>The Tune toggle: the part's Part entry has its tune flag — its slider in the Tuning panel steps
    /// through the candidates.</summary>
    public bool IsVendorPartTuned
    {
        get => VendorTuneKey is { } key && _schematicVm?.Tuning?.IsTuned(key) == true;
        set
        {
            if (_isRefreshing || VendorTuneKey is not { } key || value == IsVendorPartTuned) return;
            _schematicVm?.Tuning?.SetTuned(key, value);
            OnPropertyChanged();
        }
    }

    /// <summary>The Optimize toggle: the part's Part entry has its opt flag — the optimizer chooses among the
    /// candidates.</summary>
    public bool IsVendorPartOptimized
    {
        get => VendorTuneKey is { } key && _schematicVm?.Optimize?.IsOptimized(key) == true;
        set
        {
            if (_isRefreshing || VendorTuneKey is not { } key || value == IsVendorPartOptimized) return;
            _schematicVm?.Optimize?.SetOptimized(key, value);
            OnPropertyChanged();
        }
    }

    /// <summary>The placed part's VALUE key in the tuning setup: <c>C1.C</c> or <c>C1.L</c>; null for another quantity.</summary>
    private string? VendorValueKey => _target is null ? null : _vendorCurrent?.Header.Quantity switch
    {
        VendorPartQuantity.Capacitance => $"{_target.InstanceName}.C",
        VendorPartQuantity.Inductance  => $"{_target.InstanceName}.L",
        _ => null,
    };

    // The Yield toggle (brief vendorlib-5 R-vl5-4b): beside Tune and Optimize, adding the part's value to the Yield panel
    // drawn from its own tolerance. Present whenever the part has a C or L value and the session a Yield panel, so nothing
    // shifts when the part changes; disabled, with the reason as its tooltip, when the tolerance is not known.

    /// <summary>The part carries the Yield toggle.</summary>
    public bool CanYieldVendorPart => IsVendorPart && VendorValueKey is not null && _schematicVm?.Tolerance is not null;

    /// <summary>The Yield toggle can be switched on: the part's tolerance is known (or it is already on, to switch off).</summary>
    public bool IsVendorPartYieldEnabled => _vendorCurrent?.Tolerance is not null || IsVendorPartToleranced;

    /// <summary>The Yield toggle's tooltip: what it does, or why it cannot.</summary>
    public string VendorPartYieldTip => _vendorCurrent?.Tolerance is { } t
        ? $"Yield — adds this part's value to the Yield panel, drawn from its own tolerance ({t.DisplayText})"
        : $"Yield — not available: the tolerance of this part is not known ({_vendorCurrent?.ToleranceNote ?? "its header states none"})";

    /// <summary>The Yield toggle: the part's value entry carries a tolerance that is on.</summary>
    public bool IsVendorPartToleranced
    {
        get => VendorValueKey is { } key && _schematicVm?.Tolerance?.IsToleranced(key) == true;
        set
        {
            if (_isRefreshing || VendorValueKey is not { } key || value == IsVendorPartToleranced) return;
            _schematicVm?.Tolerance?.SetToleranced(key, value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsVendorPartYieldEnabled));
        }
    }

    /// <summary>Re-reads both toggles — on a refresh and on every model change, since either panel can change them.</summary>
    internal void NotifyVendorPartFlags()
    {
        OnPropertyChanged(nameof(CanYieldVendorPart));
        OnPropertyChanged(nameof(IsVendorPartYieldEnabled));
        OnPropertyChanged(nameof(VendorPartYieldTip));
        OnPropertyChanged(nameof(IsVendorPartToleranced));
        OnPropertyChanged(nameof(CanTuneVendorPart));
        OnPropertyChanged(nameof(CanOptimizeVendorPart));
        OnPropertyChanged(nameof(IsVendorPartTuned));
        OnPropertyChanged(nameof(IsVendorPartOptimized));
    }

    /// <summary>The placed part's file's index entries, and the placed part among them.</summary>
    private IReadOnlyList<VendorLibraryEntry> _vendorFileEntries = [];
    private VendorLibraryEntry? _vendorCurrent;

    private void InitVendorPartCommands()
    {
        StepVendorValueUpCommand   = new RelayCommand(() => StepVendorValue(+1), () => CanStepVendorValue(+1));
        StepVendorValueDownCommand = new RelayCommand(() => StepVendorValue(-1), () => CanStepVendorValue(-1));

        // A panel opened while its library was still being indexed fills in when the index lands.
        // Raised on a pool thread; PostToUi is the WBond panel's own seam onto the UI thread. Held
        // weakly: the event is static and an editor must not be kept alive by it.
        var weak = new WeakReference<ParameterEditorViewModel>(this);
        Action<string>? handler = null;
        handler = _ =>
        {
            if (!weak.TryGetTarget(out var vm)) { VendorLibraryIndex.Built -= handler; return; }
            vm.PostToUi(vm.OnVendorIndexBuilt);
        };
        VendorLibraryIndex.Built += handler;
    }

    private void OnVendorIndexBuilt()
    {
        if (IsVendorPart && _vendorCurrent is null && _target?.Symbol == SymbolKind.SpiceModel)
            RefreshSpiceModelProperties();
    }

    partial void OnVendorPartQueryChanged(string value)
    {
        if (_isRefreshing || !IsVendorPart) return;
        FilterVendorParts(value);
        IsVendorPartListOpen = VendorPartMatches.Count > 0;
    }

    /// <summary>
    /// Fills the drop-down from the file's entries through the shared search grammar
    /// (<see cref="VendorLibrarySearch"/>), on this thread: it reads only the in-memory entries,
    /// measured at a few milliseconds a keystroke over a whole library. Typing exactly the placed
    /// part's name lists the whole file, so opening the drop-down on it shows every alternative.
    /// </summary>
    private void FilterVendorParts(string query)
    {
        var q = VendorLibrarySearch.Parse(
            _vendorCurrent is not null && string.Equals(query.Trim(), _vendorCurrent.Part, StringComparison.OrdinalIgnoreCase)
                ? "" : query);
        var rows = q.IsEmpty
            ? _vendorFileEntries
            : _vendorFileEntries.Where(e => VendorLibrarySearch.Match(e, q));

        VendorPartMatches.Clear();
        VendorPartChoice? current = null;
        foreach (var e in rows)
        {
            var choice = new VendorPartChoice(e);
            if (ReferenceEquals(e, _vendorCurrent)) current = choice;
            VendorPartMatches.Add(choice);
        }
        SelectedVendorPartMatch = current ?? VendorPartMatches.FirstOrDefault();
    }

    /// <summary>Opens the drop-down on every part of the file — the button beside the Part field.</summary>
    [RelayCommand]
    private void ShowAllVendorParts()
    {
        if (!IsVendorPart) return;
        FilterVendorParts("");
        IsVendorPartListOpen = VendorPartMatches.Count > 0;
    }

    /// <summary>A row of the drop-down was chosen.</summary>
    public void ChooseVendorPart(VendorPartChoice? choice)
    {
        IsVendorPartListOpen = false;
        if (choice is null || _target is null || _schematicVm is null) return;
        if (_vendorCurrent is not null && choice.Entry.Part.Equals(_vendorCurrent.Part, StringComparison.OrdinalIgnoreCase))
        {
            RefreshSpiceModelProperties();   // puts the field's text back to the placed part
            return;
        }
        _schematicVm.ChangeVendorPart(_target, _vendorCurrent, choice.Entry);
        RefreshSpiceModelProperties();
    }

    /// <summary>Escape in the Part field: close the drop-down and put the placed part's name back.</summary>
    public void CancelVendorPartQuery()
    {
        IsVendorPartListOpen = false;
        RefreshSpiceModelProperties();
    }

    private bool CanStepVendorValue(int direction)
        => IsVendorPart && _vendorCurrent is not null
        && VendorPartChange.Step(_vendorFileEntries, _vendorCurrent, direction) is not null;

    private void StepVendorValue(int direction)
    {
        if (_target is null || _schematicVm is null || _vendorCurrent is null) return;
        if (VendorPartChange.Step(_vendorFileEntries, _vendorCurrent, direction) is not { } next) return;
        _schematicVm.ChangeVendorPart(_target, _vendorCurrent, next);
        RefreshSpiceModelProperties();
    }

    /// <summary>
    /// The vendor half of <see cref="RefreshSpiceModelProperties"/>: everything from the index, nothing
    /// from the file. Returns false when <paramref name="file"/> is not a vendor reference, so the
    /// file-based panel refreshes as it always has.
    /// </summary>
    private bool RefreshVendorPart(string file, string name)
    {
        if (!VendorLibraryRefs.TryParse(file, out string library, out string rel))
        {
            IsVendorPart = false;
            return false;
        }

        _isRefreshing = true;
        IsVendorPart       = true;
        VendorLibraryName  = library;
        VendorLibraryFile  = rel;
        VendorPartQuery    = name;
        SpiceModelShowNameOnSchematic = _target?.Parameters
            .FirstOrDefault(p => p.Name == SpiceModelSymbolProvider.NameParameter)?.ShowOnSchematic ?? true;
        SpiceModelShowFileOnSchematic = _target?.Parameters
            .FirstOrDefault(p => p.Name == SpiceModelSymbolProvider.FileParameter)?.ShowOnSchematic ?? false;
        IsVendorPartListOpen = false;
        VendorPartMatches.Clear();

        var entries = VendorPartSource.IndexedFileEntries(
            file, _schematicVm?.EditModel.SchematicDirectory, out string? root, out string? reason);
        VendorLibraryRoot = root;
        _vendorFileEntries = entries ?? [];
        _vendorCurrent = _vendorFileEntries.FirstOrDefault(e => e.Part.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));

        var h = _vendorCurrent?.Header;
        VendorPartValue       = h?.ValueText() ?? "";
        VendorPartDescription = h?.Description ?? "";
        VendorPartRange       = h?.FrequencyRangeText() ?? "";
        VendorPartTolerance   = _vendorCurrent?.Tolerance?.DisplayText ?? "not known";
        HasVendorPartDetail   = _vendorCurrent is not null;

        (VendorPartStatus, VendorPartStatusIsProblem) =
            root is null              ? (reason ?? "This library does not resolve.", true)
            : entries is null         ? ("Indexing the library…", false)
            : _vendorFileEntries.Count == 0 ? ($"The library no longer holds {rel}.", true)
            : _vendorCurrent is null  ? (name.Trim().Length == 0
                                            ? "No part is chosen. Type in the Part field to find one."
                                            : $"The library no longer holds '{name.Trim()}' in this file.", true)
            : ("", false);
        _isRefreshing = false;

        StepVendorValueUpCommand.NotifyCanExecuteChanged();
        StepVendorValueDownCommand.NotifyCanExecuteChanged();
        NotifyVendorPartFlags();
        return true;
    }
}

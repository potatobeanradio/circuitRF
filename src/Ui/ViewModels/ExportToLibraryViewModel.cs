// Export to Library's dialog — brief vendorlib-10 R-vl10-3.
//
// A SHELL, and nothing else. What the export copies, every refusal, the default kind and symbol, and the export itself
// all come from ModelLibraryExport in src/Design — the function `circuitrf export part` calls. What is here is the
// dialog's state: its fields, re-asking Plan whenever one changes so the copy list and the refusals are live (R-vl10-3e),
// and running Export off the UI thread with progress and Cancel on a RunControl (R-vl10-3g). A source scan
// (ModelLibraryExportTests) holds that shut.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.VendorLibraries.ModelLibrary;
using CircuitRF.Engine;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.ViewModels;

/// <summary>One row of the symbol menu: what <see cref="ExportOptions.Symbol"/> is set to, its label, and what its
/// picture is drawn from (a built-in kind, or a cell folder / symbol reference).</summary>
public sealed record ExportSymbolRow(string Value, string Label, SymbolKind Kind, string? GlyphRef, int PortCount);

/// <summary>A library the workspace already references, for the Library drop-down.</summary>
public sealed record ExportLibraryChoice(string Name, string Root)
{
    public override string ToString() => $"{Name}  —  {Root}";
}

public sealed partial class ExportToLibraryViewModel : ObservableObject
{
    public string CellDir { get; }
    public string CellName { get; }

    /// <summary>The workspace's referenced vendor libraries.</summary>
    public IReadOnlyList<ExportLibraryChoice> KnownLibraries { get; }

    public ExportToLibraryViewModel(string cellDir, IReadOnlyList<ExportLibraryChoice> knownLibraries, string? initialLibrary)
    {
        CellDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cellDir));
        CellName = Path.GetFileName(CellDir);
        KnownLibraries = knownLibraries;
        _partNumber = CellName;
        _libraryPath = initialLibrary ?? knownLibraries.FirstOrDefault()?.Root ?? "";
        foreach (var segment in ModelLibraryExport.DefaultSweep) AddFrequencyRow(segment);
        LoadTables();
        Replan();
    }

    // ── Target (R-vl10-3a) ─────────────────────────────────────────────────

    [ObservableProperty] private string _libraryPath = "";
    [ObservableProperty] private string _category = "";
    /// <summary>The library's existing category folders, for the Category combo.</summary>
    public ObservableCollection<string> Categories { get; } = [];

    // ── Library fields (R-vl10-3b), enabled only for a new library ─────────

    [ObservableProperty] private bool _libraryIsNew = true;
    [ObservableProperty] private string _libraryName = "";
    [ObservableProperty] private string _publisher = "";
    [ObservableProperty] private string _libraryVersion = "1.0.0";
    [ObservableProperty] private string _license = "";

    // ── Part fields (R-vl10-3c) ────────────────────────────────────────────

    [ObservableProperty] private string _partNumber;
    [ObservableProperty] private string _description = "";
    public IReadOnlyList<string> Kinds { get; } = [ModelPartManifest.KindActive, ModelPartManifest.KindPassive];
    /// <summary>Null until the user picks one: the plan's default stands.</summary>
    [ObservableProperty] private string? _kind;
    [ObservableProperty] private string _value = "";
    [ObservableProperty] private string _frequencyLow = "";
    [ObservableProperty] private string _frequencyHigh = "";
    [ObservableProperty] private string _conditions = "";
    [ObservableProperty] private string _footprint = "";
    public ObservableCollection<string> Documents { get; } = [];

    /// <summary>The kind shown: the user's, else the default the cell implies.</summary>
    public string EffectiveKind => Kind ?? Plan?.DefaultKind ?? ModelPartManifest.KindPassive;
    public bool IsPassive => EffectiveKind == ModelPartManifest.KindPassive;

    /// <summary>The Value box's hint: an example value for a passive, nothing for an active part, whose box is greyed —
    /// a greyed "10 pF" on a FET reads as the part's value.</summary>
    public string ValuePlaceholder => IsPassive ? "10 pF" : "";

    // ── Frequency (brief vendorlib-11 R-vl11-4) ────────────────────────────

    /// <summary>The plan the technology-bound parts are sampled over: the EM Setup's own segment rows, seeded with
    /// <see cref="ModelLibraryExport.DefaultSweep"/>.</summary>
    public ObservableCollection<FrequencySpecViewModel> FrequencySegments { get; } = [];

    /// <summary>What will be converted, or that nothing uses the technology (R-vl11-4c) — the core's sentence.</summary>
    [ObservableProperty] private string _conversionLine = "";

    /// <summary>The Frequency group is live only when something is converted.</summary>
    public bool IsFrequencyPlanEnabled => Plan is { Conversions.Count: > 0 };

    [RelayCommand]
    private void AddFrequencySegment()
    {
        var first = FrequencySegments.Count > 0 ? FrequencySegments[0].Build() : ModelLibraryExport.DefaultSweep[0];
        AddFrequencyRow(new Core.Design.FrequencySpec(first.StopExpr, first.StopExpr, 1, Core.Design.SweepKind.Linear,
                                                      first.StopUnit, first.StopUnit));
        Replan();
    }

    private void AddFrequencyRow(Core.Design.FrequencySpec segment)
    {
        // An empty schematic model: the plan is literal numbers, as an EM setup's is.
        var row = new FrequencySpecViewModel(new SchematicEditModel(), segment, offersSinglePoint: true);
        row.PropertyChanged += (_, e) => { if (e.PropertyName != nameof(FrequencySpecViewModel.CanRemoveSelf)) Replan(); };
        row.SetRemoveCallback(RemoveFrequencyRow);
        FrequencySegments.Add(row);
        foreach (var r in FrequencySegments) r.CanRemoveSelf = FrequencySegments.Count > 1;
    }

    private void RemoveFrequencyRow(FrequencySpecViewModel row)
    {
        if (FrequencySegments.Count <= 1 || !FrequencySegments.Remove(row)) return;
        foreach (var r in FrequencySegments) r.CanRemoveSelf = FrequencySegments.Count > 1;
        Replan();
    }

    /// <summary>The Frequency range the plan last filled in, so a range the user typed is never overwritten.</summary>
    private string _filledLow = "", _filledHigh = "";

    // ── Symbol (R-vl10-3d) ─────────────────────────────────────────────────

    public ObservableCollection<ExportSymbolRow> Symbols { get; } = [];
    /// <summary>Null until the user picks one: the plan's default stands.</summary>
    [ObservableProperty] private string? _symbol;
    [ObservableProperty] private ExportSymbolRow? _currentSymbol;

    // ── What will happen (R-vl10-3e, 3f) ───────────────────────────────────

    public ObservableCollection<string> CopyLines { get; } = [];
    public ObservableCollection<string> RefusalLines { get; } = [];
    [ObservableProperty] private string _replaceNote = "";
    [ObservableProperty] private bool _addToWorkspace = true;
    [ObservableProperty] private bool _addToWorkspaceEnabled = true;
    [ObservableProperty] private bool _isPlanning;
    [ObservableProperty] private bool _isExporting;
    [ObservableProperty] private string _status = "";

    public ExportPlan? Plan { get; private set; }

    public bool CanExport => !IsExporting && !IsPlanning && Plan is { Refusals.Count: 0 } && Finished is null;

    private int _generation;

    partial void OnLibraryPathChanged(string value) => Replan();
    partial void OnCategoryChanged(string value) => Replan();
    partial void OnPartNumberChanged(string value) => Replan();
    partial void OnKindChanged(string? value) { OnPropertyChanged(nameof(EffectiveKind)); OnPropertyChanged(nameof(IsPassive)); OnPropertyChanged(nameof(ValuePlaceholder)); Replan(); }
    partial void OnValueChanged(string value) => Replan();
    partial void OnFrequencyLowChanged(string value) => Replan();
    partial void OnFrequencyHighChanged(string value) => Replan();
    partial void OnSymbolChanged(string? value) => Replan();
    partial void OnIsPlanningChanged(bool value) => OnPropertyChanged(nameof(CanExport));
    partial void OnIsExportingChanged(bool value) => OnPropertyChanged(nameof(CanExport));

    /// <summary>The options the fields say. Vendor parts are always included: the dialog lists each with its library,
    /// which is the dialog's way of asking. Replace is said by the confirmation <see cref="Export"/> is handed.</summary>
    public ExportOptions Options(bool replace) => new()
    {
        LibraryDir = LibraryPath.Trim(), Category = Category, PartNumber = PartNumber, Description = Description,
        Kind = Kind, Value = IsPassive ? Value : "", FrequencyLow = FrequencyLow, FrequencyHigh = FrequencyHigh,
        Conditions = Conditions, Footprint = Footprint, Documents = [.. Documents], Symbol = Symbol,
        Replace = replace, IncludeVendorParts = true,
        // The plan is stated only while something is converted: with nothing to sample, a plan is a refusal.
        Sweep = IsFrequencyPlanEnabled ? [.. FrequencySegments.Select(r => r.Build())] : null,
        CompileModels = CompileModels, Builds = BuildsOption(),
        CompileNetlist = CompileNetlist, CheckBias = CheckBiasPoints(),
        LibraryName = LibraryName, Publisher = Publisher, LibraryVersion = LibraryVersion, License = License,
    };

    /// <summary>Re-asks <see cref="ModelLibraryExport.Plan"/> off the UI thread; the latest answer wins.</summary>
    public void Replan() { if (!_planningOff) _ = ReplanAsync(); }

    public Task Replanned { get; private set; } = Task.CompletedTask;

    private Task ReplanAsync()
    {
        int gen = ++_generation;
        if (LibraryPath.Trim().Length == 0)
        {
            Plan = null;
            RefusalLines.Clear();
            RefusalLines.Add("Choose the library folder. An empty folder starts a new library.");
            CopyLines.Clear();
            OnPropertyChanged(nameof(CanExport));
            return Replanned = Task.CompletedTask;
        }
        IsPlanning = true;
        // Planned with Replace so an existing part is a note to confirm, not a refusal to clear.
        var options = Options(replace: true);
        var scheduler = SynchronizationContext.Current is null ? TaskScheduler.Default : TaskScheduler.FromCurrentSynchronizationContext();
        return Replanned = Task.Run(() => ModelLibraryExport.Plan(CellDir, options)).ContinueWith(t =>
        {
            if (gen != _generation) return;
            Apply(t.IsCompletedSuccessfully ? t.Result : null, t.Exception?.GetBaseException().Message);
        }, scheduler);
    }

    internal void Apply(ExportPlan? plan, string? error)
    {
        Plan = plan;
        IsPlanning = false;
        RefusalLines.Clear();
        CopyLines.Clear();
        if (plan is null) { RefusalLines.Add(error ?? "The export could not be planned."); OnPropertyChanged(nameof(CanExport)); return; }

        foreach (string r in plan.Refusals) RefusalLines.Add(r);
        RaiseBuildState();
        ConversionLine = ModelLibraryExport.ConversionLine(plan);
        OnPropertyChanged(nameof(IsFrequencyPlanEnabled));
        // R-vl11-4d: the part's range follows the plan until the user types one of their own.
        if (plan.Conversions.Count > 0 && plan.Frequencies.Length > 0)
        {
            string low = ModelLibraryExport.FormatFrequency(plan.Frequencies[0]);
            string high = ModelLibraryExport.FormatFrequency(plan.Frequencies[^1]);
            if (FrequencyLow.Trim().Length == 0 || FrequencyLow == _filledLow) { _filledLow = low; FrequencyLow = low; }
            if (FrequencyHigh.Trim().Length == 0 || FrequencyHigh == _filledHigh) { _filledHigh = high; FrequencyHigh = high; }
        }
        // Brief vendorlib-16: compiled, the cell's topology is not copied — the list says what is compiled and what stays.
        if (plan.ProtectLines.Count > 0) foreach (string l in plan.ProtectLines) CopyLines.Add(l);
        else foreach (var c in plan.Copies) CopyLines.Add(c.Describe());
        OnPropertyChanged(nameof(CopiedHeader));
        if (plan.VendorParts.Any())
            CopyLines.Add("Vendor parts are copied into the model, and their libraries' terms travel with it.");

        LibraryIsNew = plan.LibraryIsNew;
        if (LibraryIsNew && LibraryName.Trim().Length == 0)
            LibraryName = Path.GetFileName(Path.TrimEndingDirectorySeparator(LibraryPath.Trim()));
        ReplaceNote = plan.Exists ? $"{plan.PartNumber} is already in this library (revision {plan.ExistingRevision}). Export replaces it, after asking." : "";

        RefreshCategories();
        RefreshSymbols(plan);
        bool referenced = KnownLibraries.Any(l => SamePath(l.Root, LibraryPath));
        AddToWorkspaceEnabled = !referenced;
        if (referenced) AddToWorkspace = false;
        OnPropertyChanged(nameof(EffectiveKind));
        OnPropertyChanged(nameof(IsPassive)); OnPropertyChanged(nameof(ValuePlaceholder));
        OnPropertyChanged(nameof(CanExport));
    }

    private void RefreshCategories()
    {
        var found = new List<string>();
        string root = LibraryPath.Trim();
        try
        {
            if (Directory.Exists(root))
                foreach (string d in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
                {
                    string rel = Path.GetRelativePath(root, d).Replace('\\', '/');
                    if (rel.Split('/').Any(s => s.StartsWith('.'))) continue;
                    if (ModelFolder.OwningModelFolder(d) is not null) continue;
                    found.Add(rel);
                }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        found.Sort(StringComparer.Ordinal);
        if (found.SequenceEqual(Categories)) return;
        Categories.Clear();
        foreach (string c in found) Categories.Add(c);
    }

    private void RefreshSymbols(ExportPlan plan)
    {
        var pins = plan.Pins;
        string terminals = VerilogASymbolProvider.FormatTerminals(pins, []);
        var rows = new List<ExportSymbolRow>();
        if (plan.HasOwnSymbol) rows.Add(new(ModelLibraryExport.SymbolOwn, "The cell's own symbol", SymbolKind.Generic, CellDir, pins.Count));
        if (pins.Count is 3 or 4)
            rows.Add(new(ModelLibraryExport.SymbolFet, "FET", SymbolKind.VerilogA,
                         VerilogASymbolProvider.GlyphRefFor(pins.Count, terminals, ""), pins.Count));
        if (pins.Count == 2)
        {
            rows.Add(new("R", "Resistor", SymbolKind.Resistor, null, 2));
            rows.Add(new("C", "Capacitor", SymbolKind.Capacitor, null, 2));
            rows.Add(new("L", "Inductor", SymbolKind.Inductor, null, 2));
        }
        rows.Add(new(ModelLibraryExport.SymbolBox, "Box", SymbolKind.Generic, null, Math.Max(pins.Count, 1)));

        if (!rows.Select(r => r.Value).SequenceEqual(Symbols.Select(r => r.Value)))
        {
            Symbols.Clear();
            foreach (var r in rows) Symbols.Add(r);
        }
        string current = Symbol ?? plan.DefaultSymbol;
        CurrentSymbol = Symbols.FirstOrDefault(r => r.Value == current) ?? Symbols.LastOrDefault();
    }

    // ── Export (R-vl10-3g) ─────────────────────────────────────────────────

    private RunControl? _control;

    /// <summary>Runs <see cref="ModelLibraryExport.Export"/> off the UI thread. <paramref name="replace"/> is the
    /// user's answer to the replace question, asked by the dialog when <see cref="ExportPlan.Exists"/>.</summary>
    public async Task<ExportResult> Export(bool replace)
    {
        var options = Options(replace);
        IsExporting = true;
        Status = "Exporting…";
        var progress = new Progress<RunProgress>(p => Status = p.StageTotal > 0 ? $"{p.Stage}… {p.StageCompleted} / {p.StageTotal}" : $"{p.Stage}…");
        _control = new RunControl { Progress = progress };
        try
        {
            var control = _control;
            var result = await Task.Run(() => ModelLibraryExport.Export(CellDir, options, control));
            Status = result.Succeeded ? result.Message ?? "" : result.Refusal ?? "";
            return result;
        }
        finally
        {
            IsExporting = false;
            _control = null;
        }
    }

    /// <summary>Stops a running export at its next step; nothing is left behind.</summary>
    public void Cancel() => _control?.RequestStop();

    public bool IsReferenced(string root) => KnownLibraries.Any(l => SamePath(l.Root, root));

    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
                                 Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
                                 OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}

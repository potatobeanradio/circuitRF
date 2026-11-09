// The Model Composer window's view model (brief-modelcomposer-14; overview §3, D2, D13, D15).
//
// ONE DOCUMENT IN MEMORY, EDITED; THE FILE IS WHERE RUNS HAPPEN. Every edit — a value, a lock, a bound, an exclusion, a
// step setting, a network edit — changes Document and is one undo step (R-mc14-5). Every run — an import, a fit — is a
// file-based ModelCommands function, the one the `model` verb calls (D15): the window writes the document first, runs on
// the file off the UI thread (D13), and reads the result back. A run is never an undo step; an edit made while a run was
// going is replayed onto the run's result, so neither is lost.
//
// Framework-free apart from CommunityToolkit: the window installs PostToUi and RunOffThread, the pickers and the
// clipboard, which is what lets the gate drive it with no application host — railRF's and the Smith Chart's own split.
//
// Split by concern: .Tasks (the background runner and its progress), .Cursor (the bias cursor), .Data (the Data step and
// import), .Extrinsic (pads, network, values, clipboard), .Plots, .Passive; MC-15's .Steps (the model steps' settings and
// runs), .StepPlots (their plots) and .Report (the Model Validation Report dialog).

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Design.Modeling;
using CircuitRF.Design.Modeling.Passive;
using CircuitRF.Ui.Views.Dialogs;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;

namespace CircuitRF.Ui.ModelComposer;

/// <summary>What the status strip's one line is.</summary>
public enum ModelStatusLevel { Info, Warning, Error }

public sealed partial class ModelComposerViewModel : ObservableObject, IDisposable
{
    public const string StepsPanel = "Steps", PlotsPanel = "Plots", NetworkPanel = "Network", ValuesPanel = "Values";

    /// <summary>The four panels, in the window's left-to-right order — the lamps' order.</summary>
    public static readonly IReadOnlyList<string> PanelNames = [StepsPanel, PlotsPanel, NetworkPanel, ValuesPanel];

    /// <summary>The steps, overview §3.</summary>
    public static readonly IReadOnlyList<string> ActiveSteps =
        ["Data", "Pads", "Network", "Current", "Charge", "Thermal", "Traps", "Noise", "Check", "Validate", "Export"];
    public static readonly IReadOnlyList<string> PassiveSteps = ["Data", "Topology", "Fit", "Export"];

    /// <summary>A new document: the window opens on the Active / Passive chooser.</summary>
    public ModelComposerViewModel()
    {
        _document = new CmcDocument { Kind = CmcKind.Active };
        IsChoosingKind = true;
        BuildPlotHost();
    }

    /// <summary>A document read from <paramref name="path"/>. Its data is loaded by <see cref="BeginLoad"/>.</summary>
    public ModelComposerViewModel(CmcDocument document, string path)
    {
        _document = document;
        DocumentPath = Path.GetFullPath(path);
        BuildPlotHost();
        AfterDocumentReplaced();
        RestoreView(document.View);
    }

    /// <summary>Reads <paramref name="path"/>; a refusal throws <see cref="CmcFormatException"/> naming its field.</summary>
    public static ModelComposerViewModel OpenFile(string path) => new(CmcFile.Read(path), path);

    // ── the seams the window fills ───────────────────────────────────────────

    /// <summary>Asks where to save; given the suggested name, folder and title, returns a path or null for a cancelled picker.</summary>
    public Func<SavePrompt, Task<string?>>? PickSavePath { get; set; }

    /// <summary>What the Save picker opens on: a suggested file name, the folder to start in (null: the document's), and the
    /// dialog's title (null: the plain save title).</summary>
    public sealed record SavePrompt(string Name, string? Folder = null, string? Title = null);

    /// <summary>Asks whether to save unsaved work before closing — railRF's and wBond's dialog.</summary>
    public Func<string, Task<SaveChangesResult>>? AskSaveChanges { get; set; }

    // ── the document ─────────────────────────────────────────────────────────

    private CmcDocument _document;

    /// <summary>The document as edited.</summary>
    public CmcDocument Document
    {
        get => _document;
        private set
        {
            if (ReferenceEquals(_document, value)) return;
            _document = value;
            OnPropertyChanged();
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(FileName), nameof(PathTip))]
    private string? _documentPath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    private bool _isDirty;

    /// <summary>True until a new document's kind is chosen.</summary>
    [ObservableProperty] private bool _isChoosingKind;

    public CmcKind Kind => Document.Kind;
    public bool IsActive => Kind == CmcKind.Active;
    public bool IsPassive => Kind == CmcKind.Passive;

    public string FileName => DocumentPath is { } p ? Path.GetFileName(p) : "Untitled" + CmcFile.Extension;
    public string PathTip => DocumentPath ?? "Not saved yet";
    public string Title => $"{(IsDirty ? "• " : "")}Model Composer · {FileName}";

    /// <summary>The folder a Save As picker starts in, and that a new document's network is copied to.</summary>
    public string? DocumentFolder => DocumentPath is { } p ? Path.GetDirectoryName(p) : null;

    [RelayCommand]
    private void ChooseKind(string kind)
    {
        if (!IsChoosingKind) return;
        Document = new CmcDocument { Kind = Enum.Parse<CmcKind>(kind) };
        IsChoosingKind = false;
        AfterDocumentReplaced();
    }

    /// <summary>Everything that follows the document being replaced wholesale — a choice, an open, a run's result.</summary>
    private void AfterDocumentReplaced()
    {
        OnPropertyChanged(nameof(Kind));
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(IsPassive));
        if (Steps.Count != (IsActive ? ActiveSteps.Count : PassiveSteps.Count)) BuildSteps();
        SyncPanels();
        RefreshDocumentViews();
    }

    /// <summary>Re-projects the document into every list and readout — after any edit, undo or run.</summary>
    private void RefreshDocumentViews()
    {
        RefreshDataSets();
        RefreshPeriphery();
        RefreshExtrinsic();
        RefreshPassive();
        UpdateStepStates();
        RefreshModelSteps();
        RefreshCursorChoices();
        RequestStepPlots();
        RebuildPlots();
    }

    // ── the status strip: one line ───────────────────────────────────────────

    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private ModelStatusLevel _statusLevel;

    public void Info(string line) { StatusLevel = ModelStatusLevel.Info; StatusText = line; }
    public void Warn(string line) { StatusLevel = ModelStatusLevel.Warning; StatusText = line; }
    public void Error(string line) { StatusLevel = ModelStatusLevel.Error; StatusText = line; }

    // ── steps (R-mc14-2) ─────────────────────────────────────────────────────

    public ObservableCollection<ModelStepItem> Steps { get; } = [];

    private ModelStepItem? _selectedStep;

    /// <summary>The step whose settings and plots are showing. A disabled step cannot be selected.</summary>
    public ModelStepItem? SelectedStep
    {
        get => _selectedStep;
        set
        {
            if (value is { IsEnabled: false }) { OnPropertyChanged(); return; }
            if (ReferenceEquals(_selectedStep, value)) return;
            if (_selectedStep is not null) _selectedStep.IsSelected = false;
            _selectedStep = value;
            if (value is not null) value.IsSelected = true;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StepName));
            NotifyRunItems();
            NotifyStepPanels();
            NotifyModelSteps();
            if (value is not null) SetView(Document.View with { Step = value.Name });
            RebuildPlotTabs();
            RequestStepPlots();
            RebuildPlots();
        }
    }

    public string StepName => SelectedStep?.Name ?? "Data";

    public bool IsDataStep => StepName == "Data";
    public bool IsPadsStep => StepName == "Pads";
    public bool IsNetworkStep => StepName == "Network";
    public bool IsTopologyStep => StepName == "Topology";

    private void NotifyStepPanels()
    {
        OnPropertyChanged(nameof(IsDataStep));
        OnPropertyChanged(nameof(IsPadsStep));
        OnPropertyChanged(nameof(IsNetworkStep));
        OnPropertyChanged(nameof(IsTopologyStep));
    }

    private static MaterialIconKind IconOf(string step) => step switch
    {
        "Data" => MaterialIconKind.DatabaseOutline,
        "Pads" => MaterialIconKind.ViewGridOutline,
        "Network" => MaterialIconKind.Sitemap,
        "Current" => MaterialIconKind.CurrentDc,
        "Charge" => MaterialIconKind.Battery,
        "Thermal" => MaterialIconKind.Thermometer,
        "Noise" => MaterialIconKind.Waveform,
        "Traps" => MaterialIconKind.TimerSandEmpty,
        "Check" => MaterialIconKind.CheckDecagramOutline,
        "Validate" => MaterialIconKind.ChartBellCurve,
        "Export" => MaterialIconKind.ExportVariant,
        "Topology" => MaterialIconKind.FamilyTree,
        "Fit" => MaterialIconKind.TuneVariant,
        _ => MaterialIconKind.CircleOutline,
    };

    private void BuildSteps()
    {
        _selectedStep = null;
        Steps.Clear();
        foreach (string s in IsActive ? ActiveSteps : PassiveSteps) Steps.Add(new ModelStepItem(s, IconOf(s)));
        SelectedStep = Steps[0];
    }

    /// <summary>
    /// Each step's availability and stale marker, from the document and the loaded data: a step that cannot run yet is
    /// disabled, with its reason as the tooltip.
    /// </summary>
    private void UpdateStepStates()
    {
        bool sData = Document.DataSets.Any(s => s.Kind is DataSetKind.SParams or DataSetKind.PulsedSParams or DataSetKind.SimulatedTables);
        bool ivData = Document.DataSets.Any(s => s.Kind is DataSetKind.DcIv or DataSetKind.PulsedIv or DataSetKind.SimulatedTables);
        bool pulsed = Document.DataSets.Any(s => s.Kind is DataSetKind.PulsedIv or DataSetKind.PulsedSParams);
        bool passiveData = Document.DataSets.Any(s => s.Kind == DataSetKind.PassiveSParams);
        bool topology = PassiveStep.Settings(Document).Topology is not null;
        var stale = StaleSteps;
        foreach (var step in Steps)
        {
            string reason = step.Name switch
            {
                "Data" => "",
                "Pads" or "Network" => sData ? "" : "Needs an S-parameter data set.",
                "Charge" => sData ? "" : "Needs an S-parameter data set.",
                "Current" or "Thermal" => ivData ? "" : "Needs an I–V data set.",
                "Traps" => pulsed ? "" : "Needs a pulsed data set.",
                "Check" when IsActive => sData ? "" : "Needs an S-parameter data set.",
                "Validate" or "Export" when IsActive =>
                    sData && (ivData || CurrentFit.Record(Document) is not null) ? "" : "Needs I–V and S-parameter data.",
                "Topology" => passiveData ? "" : "Needs a passive S-parameter data set.",
                "Fit" => passiveData ? "" : "Needs a passive S-parameter data set.",
                "Export" => topology ? "" : "Needs a topology.",
                _ => "",
            };
            step.Reason = reason;
            step.IsEnabled = reason.Length == 0;
            step.IsStale = stale.Any(s => string.Equals(s, step.Name, StringComparison.OrdinalIgnoreCase));
            step.IsDone = step.IsEnabled && !step.IsStale && IsDone(step.Name);
        }
        if (SelectedStep is { IsEnabled: false }) SelectedStep = Steps[0];
        RefreshNeeds();
        NotifyRunItems();
    }

    /// <summary>MC-30 D5: what the imported data sets can support, one row per kind of data in model-composer.md#needs.</summary>
    public ObservableCollection<NeedRow> NeedRows { get; } = [];

    /// <summary>
    /// The needs list, from the rail itself: a row a step stands for is lit exactly when that step is enabled and, dim, shows
    /// that step's own reason — so the list and the rail cannot disagree. Active documents only; the row count is fixed, so
    /// the list's height does not move as sets are imported.
    /// </summary>
    private void RefreshNeeds()
    {
        NeedRows.Clear();
        if (!IsActive) return;
        NeedRow FromStep(string data, string gives, string step) =>
            Steps.FirstOrDefault(s => s.Name == step) is { } item
                ? new NeedRow(data, gives, item.IsEnabled, item.Reason)
                : new NeedRow(data, gives, false, "");
        int temperatures = Document.DataSets.Select(s => s.Conditions.AmbientC).OfType<double>().Distinct().Count();
        NeedRows.Add(FromStep("I–V", "Id and Ig.", "Current"));
        NeedRows.Add(FromStep("S, Y or capacitances", "Qg and Qd.", "Charge"));
        NeedRows.Add(FromStep("Cold-FET S", "The network fit.", "Network"));
        NeedRows.Add(new NeedRow("Two or more temperatures", "T and self-heating.", temperatures >= 2,
                                 temperatures == 1 ? "Needs a set at a second temperature." : "Needs sets at two temperatures."));
        NeedRows.Add(FromStep("Pulsed I–V or S", "Traps.", "Traps"));
        NeedRows.Add(new NeedRow("Power, two-tone, loadpull", "Validation.",
                                 Document.DataSets.Any(s => s.Kind == DataSetKind.LargeSignal), "Needs a large-signal data set."));
    }

    /// <summary>The stale steps: the last open's (data and network moved under a fit), and Traps whenever the trap model now
    /// chosen is not the one the currents were trained with — an edit, which no open follows.</summary>
    public IReadOnlyList<string> StaleSteps
    {
        get
        {
            bool traps;
            try { traps = TrapFit.CurrentsDisagree(Document); }
            catch (CmcFormatException) { traps = false; }
            return traps && !Loaded.Stale.Contains(TrapFit.StepName) ? [.. Loaded.Stale, TrapFit.StepName] : Loaded.Stale;
        }
    }

    /// <summary>
    /// Whether <paramref name="step"/> holds its result: data imported, pads stated, a fit recorded (a current or charge fit
    /// with its arrays in the cache), the model or cell exported — so a <c>.cmc</c> opened with its work done says so on the
    /// rail. Check and
    /// Validate keep no record in the document; they are done once run in this window.
    /// </summary>
    private bool IsDone(string step)
    {
        try
        {
            return step switch
            {
                "Data" => Document.DataSets.Count > 0,
                "Pads" => Document.Pads is not null,
                "Network" => CmcExtrinsic.Network(Document).Result is not null,
                // A fit is done when its arrays are in the cache too, not only its record in the document: a copy that lost
                // the cache otherwise showed every step ticked and refused the report and the export (owner, review 4).
                "Current" => DocumentPath is { } cp && CurrentFit.HasArrays(cp, Document),
                "Charge" => DocumentPath is { } qp && ChargeFit.HasArrays(qp, Document),
                "Thermal" => ThermalFit.Record(Document) is not null,
                "Noise" => NoiseFit.Record(Document) is not null,
                "Traps" => TrapFit.Record(Document) is not null
                           || !TrapFit.Settings(Document).Modelled && CurrentFit.Record(Document) is { Traps: null },
                "Check" => CheckReport is not null,
                "Validate" => Validation is not null,
                "Export" when IsActive => (ExportPath.Length > 0 ? ExportPath : DocumentPath is { } p ? Path.ChangeExtension(p, CmodelFile.Extension) : "")
                                          is { Length: > 0 } model && File.Exists(model),
                "Export" => PassiveStep.Settings(Document).Export is { } e && DocumentFolder is { } dir
                            && Directory.Exists(Path.GetFullPath(Path.Combine(dir, e.Library, e.Cell))),
                "Topology" => PassiveStep.Settings(Document).Topology is not null,
                "Fit" => PassiveStep.Record(Document) is not null,
                _ => false,
            };
        }
        catch (Exception ex) when (ex is CmcFormatException or ModelingException) { return false; }
    }

    // ── panels (R-mc14-2): lamps persisted in the .cmc ───────────────────────

    public bool ShowSteps => IsShown(StepsPanel);
    public bool ShowPlots => IsShown(PlotsPanel);
    public bool ShowNetwork => IsShown(NetworkPanel);
    public bool ShowValues => IsShown(ValuesPanel);

    /// <summary>The right column shows while the network or the values do.</summary>
    public bool ShowRightColumn => ShowNetwork || ShowValues;

    private bool IsShown(string panel) => !Document.Panels.Hidden.Contains(panel);

    private int ShownCount => PanelNames.Count(IsShown);

    [RelayCommand(CanExecute = nameof(CanToggleSteps))] private void ToggleSteps() => TogglePanel(StepsPanel);
    [RelayCommand(CanExecute = nameof(CanTogglePlots))] private void TogglePlots() => TogglePanel(PlotsPanel);
    [RelayCommand(CanExecute = nameof(CanToggleNetwork))] private void ToggleNetwork() => TogglePanel(NetworkPanel);
    [RelayCommand(CanExecute = nameof(CanToggleValues))] private void ToggleValues() => TogglePanel(ValuesPanel);

    private bool CanToggleSteps() => CanToggle(StepsPanel);
    private bool CanTogglePlots() => CanToggle(PlotsPanel);
    private bool CanToggleNetwork() => CanToggle(NetworkPanel);
    private bool CanToggleValues() => CanToggle(ValuesPanel);

    /// <summary>The last lit lamp cannot be turned off: at least one panel always shows.</summary>
    private bool CanToggle(string panel) => !IsShown(panel) || ShownCount > 1;

    /// <summary>Shows or hides <paramref name="panel"/>. A view change, so not an undo step — but the document's, so dirty.</summary>
    public void TogglePanel(string panel)
    {
        if (!CanToggle(panel)) return;
        var hidden = Document.Panels.Hidden.ToList();
        if (!hidden.Remove(panel)) hidden.Add(panel);
        Document = Document with { Panels = new CmcPanels { Hidden = hidden } };
        IsDirty = true;
        SyncPanels();
    }

    /// <summary>A document hiding every panel opens with everything shown — railRF's rule for a hand-edited file.</summary>
    private void SyncPanels()
    {
        if (ShownCount == 0) Document = Document with { Panels = new CmcPanels() };
        OnPropertyChanged(nameof(ShowSteps));
        OnPropertyChanged(nameof(ShowPlots));
        OnPropertyChanged(nameof(ShowNetwork));
        OnPropertyChanged(nameof(ShowValues));
        OnPropertyChanged(nameof(ShowRightColumn));
        ToggleStepsCommand.NotifyCanExecuteChanged();
        TogglePlotsCommand.NotifyCanExecuteChanged();
        ToggleNetworkCommand.NotifyCanExecuteChanged();
        ToggleValuesCommand.NotifyCanExecuteChanged();
    }

    // ── view state: the step and the cursor (R-mc14-1) ───────────────────────

    /// <summary>True when the step or the cursor moved since the file was last written — saved with a clean document on close.</summary>
    public bool ViewChanged { get; private set; }

    private void SetView(CmcView view)
    {
        if (Document.View == view) return;
        Document = Document with { View = view };
        ViewChanged = true;
    }

    /// <summary>Puts the window back where <paramref name="stored"/> left it — the step, and the cursor the next data load
    /// snaps onto. Building the rail selects its first step, so the stored view is passed in rather than read back.</summary>
    private void RestoreView(CmcView stored)
    {
        Document = Document with { View = stored };
        if (stored.Step is { } name && Steps.FirstOrDefault(s => s.Name == name) is { IsEnabled: true } step)
            SelectedStep = step;
        ViewChanged = false;
    }

    // ── undo (R-mc14-5) ──────────────────────────────────────────────────────

    /// <summary>One edit: what it is called, and how to do and undo it on whatever document is current — so an undo after a run
    /// reverts the edit and keeps the run's result.</summary>
    private sealed record CmcEdit(string Label, Func<CmcDocument, CmcDocument> Redo, Func<CmcDocument, CmcDocument> Undo);

    private readonly List<CmcEdit> _undo = [];
    private readonly List<CmcEdit> _redo = [];

    public int UndoDepth => _undo.Count;

    /// <summary>
    /// Applies an edit of one part of the document — <paramref name="get"/> reads it, <paramref name="set"/> writes it — as one
    /// undo step. A refused value (the document's own validation, or a <see cref="ModelingException"/>) is the status line
    /// and changes nothing.
    /// </summary>
    private bool EditPart<T>(string label, Func<CmcDocument, T> get, Func<CmcDocument, T, CmcDocument> set, T value)
    {
        T old = get(Document);
        return Apply(new CmcEdit(label, d => set(d, value), d => set(d, old)));
    }

    private bool Apply(CmcEdit edit)
    {
        CmcDocument next;
        try
        {
            next = edit.Redo(Document);
            CmcFile.Validate(next);
        }
        catch (Exception ex) when (ex is CmcFormatException or ModelingException or ArgumentException)
        {
            Error(ex.Message);
            RefreshDocumentViews();
            return false;
        }
        _undo.Add(edit);
        _redo.Clear();
        if (IsRunning) _editsDuringRun.Add(edit);
        Commit(next);
        Info(edit.Label);
        return true;
    }

    private void Commit(CmcDocument next)
    {
        Document = next;
        IsDirty = true;
        RefreshDocumentViews();
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        AfterEdit();
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        if (_undo.Count == 0) return;
        var e = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(e);
        if (IsRunning) _editsDuringRun.Add(e with { Redo = e.Undo, Undo = e.Redo });
        Commit(e.Undo(Document));
        Info($"Undo: {e.Label}");
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        if (_redo.Count == 0) return;
        var e = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(e);
        if (IsRunning) _editsDuringRun.Add(e);
        Commit(e.Redo(Document));
        Info($"Redo: {e.Label}");
    }

    private bool CanUndo() => _undo.Count > 0;
    private bool CanRedo() => _redo.Count > 0;

    // ── save and close (R-mc14-1) ────────────────────────────────────────────

    [RelayCommand] private Task Save() => SaveAsync(saveAs: false);
    [RelayCommand] private Task SaveAs() => SaveAsync(saveAs: true);

    /// <summary>
    /// Writes the document — to its file, or to a path the user picks (a new document always saves as). A refusal is the
    /// status line and leaves the document dirty. Returns false for a cancelled picker or a refusal.
    /// </summary>
    public async Task<bool> SaveAsync(bool saveAs, SavePrompt? prompt = null)
    {
        if (IsChoosingKind) return false;
        if (IsFileRunning)
        {
            Warn($"{TaskStage} is writing this document; save when it finishes.");
            return false;
        }
        string? path = saveAs ? null : DocumentPath;
        if (path is null)
        {
            if (PickSavePath is null) return false;
            path = await PickSavePath(prompt ?? new SavePrompt(Path.GetFileNameWithoutExtension(FileName)));
            if (path is null) return false;
            if (!path.EndsWith(CmcFile.Extension, StringComparison.OrdinalIgnoreCase)) path += CmcFile.Extension;
        }
        if (SaveRefusedFor is { } refuse && refuse(path) is { } why)
        {
            Error(why);
            return false;
        }
        try
        {
            ModelCommands.Save(path, Document);
        }
        catch (Exception ex) when (ex is CmcFormatException or IOException or UnauthorizedAccessException)
        {
            Error($"{Path.GetFileName(path)} did not save: {ex.Message}");
            return false;
        }
        bool moved = !string.Equals(DocumentPath, Path.GetFullPath(path), StringComparison.Ordinal);
        DocumentPath = Path.GetFullPath(path);
        IsDirty = false;
        ViewChanged = false;
        Info($"Saved {FileName}.");
        PathAdopted?.Invoke(DocumentPath);
        if (moved) BeginLoad();
        return true;
    }

    /// <summary>The window's one-window-per-document refusal for a Save As target another window holds; null allows it.</summary>
    public Func<string, string?>? SaveRefusedFor { get; set; }

    /// <summary>Raised when the document takes a path — its first save, or a Save As.</summary>
    public event Action<string>? PathAdopted;

    /// <summary>
    /// Settles the document before the window closes: a clean one keeps its view state (written when only the step or the
    /// cursor moved), a dirty one asks. False cancels the close — Cancel, or a cancelled picker behind Save.
    /// </summary>
    public async Task<bool> ConfirmCloseAsync()
    {
        CancelRunningTask();
        if (!IsDirty)
        {
            if (ViewChanged && DocumentPath is { } p && !IsChoosingKind)
            {
                try { ModelCommands.Save(p, Document); } catch (Exception ex) when (ex is IOException or CmcFormatException or UnauthorizedAccessException) { }
            }
            return true;
        }
        if (AskSaveChanges is null) return true;
        var answer = await AskSaveChanges($"Save changes to {FileName} before closing?");
        if (answer == SaveChangesResult.Cancel) return false;
        if (answer == SaveChangesResult.Save) return await SaveAsync(saveAs: false);
        return true;
    }

    public void Dispose() => CancelRunningTask();
}

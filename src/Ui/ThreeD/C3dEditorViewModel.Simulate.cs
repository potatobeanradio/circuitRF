// brief-em3d-49 — simulate from the document: the Setup Analyses dialog on the embedded setups, ports drawn and inferred by
// contact, the active setup's air box drawn and edited, boundaries on the faces of solids, Simulate, and the fields over
// the run's geometry.
//
// EVERY EDIT HERE IS A DOCUMENT EDIT (R-em3d49-1b): one undo entry (C3dRecordsEdit — the ports, face boundaries and
// setups as the file spells them, before and after), the document dirty, saved with the .c3d. The setup editor is the
// .cem panel itself (EmSetupEditorViewModel), bound to an embedded setup with its commit handed here: one editor, two
// containers, and its own undo stack stays empty.
//
// WHAT IS DRAWN IS WHAT A RUN GETS (overview §0). The air box is the active setup's, padded by the one rule a run pads
// by (C3dProblemAssembly.AirBox → Em3dGenerator.PaddedAirBox); the ports are resolved by the one inference a run makes
// (C3dPorts), against that box and that setup's ground set — so a wave port off the box, or a port touching three
// conductors, is refused on screen with the words a run would refuse it with.

using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Ui.Layout.Em;
using CircuitRF.Ui.ThreeD.Tools;
using CircuitRF.Ui.Viewer3D;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using CircuitRF.Design.Layout.Em3d;

namespace CircuitRF.Ui.ThreeD;

/// <summary>
/// One row of the 3D view's Setup Analyses dialog: an embedded setup (by its index in the document), or the external
/// <c>.cem</c>. Drawn as a schematic's analysis card is — a type badge, the name, a one-line summary — with the ACTIVE
/// mark where a schematic card has its Enabled box: a 3D view runs one setup, not every enabled one.
/// </summary>
public sealed partial class C3dSetupItem(string name, int index, string? refusal, string? externalPath, EmSetup? setup = null) : ObservableObject
{
    /// <summary>The card's badge: what the setup solves, in the analysis cards' two- or three-letter spelling.</summary>
    public string TypeLabel => setup is null ? "?" : setup.Problem3D switch
    {
        Em3dProblemType.Electrostatic => "ES",
        Em3dProblemType.Magnetostatic => "MS",
        Em3dProblemType.Eigenmode     => "EIG",
        Em3dProblemType.Thermal       => "TH",
        _                             => "SP",
    };

    /// <summary>The card's second line: solver, problem and sweep — or why it cannot run, which is all that matters then.</summary>
    public string Summary
    {
        get
        {
            if (Refusal is { } why) return why;
            if (setup is null) return "";
            // brief-em3d-73 — a thermal setup names no EM solver: circuitRF's own solves it.
            string solver = setup.IsThermal ? "circuitRF thermal"
                          : string.Join(" + ", CircuitRF.Design.Em3d.Em3dSolvers.Of(setup.Solver3D).Select(d => d.DisplayName));
            var f = setup.Frequency;
            string body = setup.Problem3D switch
            {
                Em3dProblemType.Electrostatic => "electrostatic",
                Em3dProblemType.Magnetostatic => "magnetostatic",
                Em3dProblemType.Eigenmode     => $"eigenmodes above {f.StartExpr} {f.StartUnit}",
                Em3dProblemType.Thermal       => "thermal",
                _ => (f.NumPoints is { } n
                    ? $"{f.StartExpr}–{f.StopExpr} {f.StopUnit}, {n} pts"
                    : $"{f.StartExpr}–{f.StopExpr} {f.StopUnit}, step {f.StepExpr} {f.StepUnit}")
                    + setup.AdditionalFrequencies.Count switch { 0 => "", 1 => " + 1 more segment", var k => $" + {k} more segments" },
            };
            return $"{solver} · {body}" + (IsExternal ? $" · read-only, from {Path.GetFileName(ExternalPath)}" : "");
        }
    }

    /// <summary>The card menu names the card it acts on, as a schematic card's does.</summary>
    public string RunLabel => $"Run {Name}";
    public string RemoveLabel => $"Remove {Name}";

    public string Name { get; } = name;
    public int Index { get; } = index;
    public string? Refusal { get; } = refusal;
    public string? ExternalPath { get; } = externalPath;
    public bool IsExternal => ExternalPath is not null;

    [ObservableProperty] private bool _isActive;

    /// <summary>brief-em3d-65 R-em3d65-4d — what this setup's solver will not respect of the document's kernel solids,
    /// recomputed with the scene (the document or the setup changed). Shown under the card; Simulate is not blocked by it.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasFidelity))] private IReadOnlyList<C3dFidelityRow> _fidelity = [];

    public bool HasFidelity => Fidelity.Count > 0;

    /// <summary>brief-em3d-98 R-em3d98-5 — whether this setup has a result of the model as it is now, in words, one line per
    /// solver leg (a Both setup has two): <c>Solved 14:32 (18 min)</c>, <c>Out of date: 'Board.clay' has changed</c>,
    /// <c>Not run</c>. From <see cref="C3dSolveStatus"/>, refreshed in the background; empty until the first answer.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasSolveLines))] private IReadOnlyList<string> _solveLines = [];

    public bool HasSolveLines => SolveLines.Count > 0;

    /// <summary>Sets <see cref="SolveLines"/> from this setup's legs in <paramref name="statuses"/>.</summary>
    internal void ApplySolveStatuses(IReadOnlyList<SetupSolveStatus> statuses)
    {
        var mine = statuses.Where(s => s.Setup == Name).ToList();
        SolveLines = mine.Count switch
        {
            0 => [],
            1 => [mine[0].Words + (mine[0].Partial && mine[0].Detail is { } d ? $" — {d}" : "")],
            _ => [.. mine.Select(s => $"{C3dSolveStatus.Name(s.Solver)}: {s.Words}" + (s.Partial && s.Detail is { } d ? $" — {d}" : ""))],
        };
    }

    public string Label => (IsActive ? "● " : "   ") + Name + (IsExternal ? $"  (external: {Path.GetFileName(ExternalPath)})" : "") +
                           (Refusal is null ? "" : "  — cannot run");

    partial void OnIsActiveChanged(bool value) => OnPropertyChanged(nameof(Label));
}

/// <summary>brief-em3d-65 — one fidelity row under a setup's card: a warning (the answer near the feature is the grid's, or
/// the loss model's) or a note.</summary>
public sealed record C3dFidelityRow(bool IsWarning, string Text)
{
    public bool IsNote => !IsWarning;
}

public sealed partial class C3dEditorViewModel
{
    // ── brief-em3d-65 R-em3d65-4d — each setup's fidelity rows, computed with the scene ─────────

    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, IReadOnlyDictionary<string, IReadOnlyList<C3dFidelityRow>>> _fidelity = new();
    private IReadOnlyDictionary<string, IReadOnlyList<C3dFidelityRow>> _fidelityNow = new Dictionary<string, IReadOnlyList<C3dFidelityRow>>();

    /// <summary>
    /// On the scene's worker, under the elaboration lock: every embedded setup's rows, from the problem a run of it would
    /// assemble. Nothing is assembled for a document with no kernel object — every existing document pays nothing.
    /// </summary>
    private void ComputeFidelity(long generation, C3dDocument doc, string path, string? workspaceCws)
    {
        var rows = new Dictionary<string, IReadOnlyList<C3dFidelityRow>>(StringComparer.Ordinal);
        if (C3dKernelUse.Of(doc).Count > 0)
            foreach (var embedded in C3dSetups.Read(doc))
            {
                if (embedded is not { Refusal: null, Setup: { Is3D: true } s }) continue;
                try
                {
                    var run = C3dSetups.ForRun(s, path);
                    if (C3dProblemAssembly.Assemble(run, doc, path, workspaceCws, _elaborator).Problem is not { } problem) continue;
                    rows[embedded.Name] = [.. Em3dFidelityReport.For(problem, run).Select(r =>
                        new C3dFidelityRow(r.Severity == Em3dFidelitySeverity.Warning, $"{Em3dFidelityReport.SolverName(r.Solver)}: {r.Sentence}"))];
                }
                catch (Exception e) when (e is InvalidOperationException or ArgumentException or IOException) { /* the run says it */ }
            }
        _fidelity[generation] = rows;
    }

    /// <summary>The adopted scene's rows onto the setup cards.</summary>
    private void AdoptFidelity(long generation)
    {
        foreach (long old in _fidelity.Keys.Where(k => k < generation).ToList()) _fidelity.TryRemove(old, out _);
        if (!_fidelity.TryRemove(generation, out var rows)) return;
        _fidelityNow = rows;
        foreach (var item in SetupItems) item.Fidelity = item.IsExternal ? [] : rows.GetValueOrDefault(item.Name) ?? [];
    }

    // ── records: ports, face boundaries and setups, as one undoable document edit ──────────────

    /// <summary>
    /// One document edit of the records (R-em3d49-1b): <paramref name="mutate"/> changes the document's ports, face
    /// boundaries or setups; the change is one undo entry, or none when nothing changed.
    /// </summary>
    public bool ChangeRecords(string description, Action<C3dDocument> mutate)
    {
        string before = C3dRecordsEdit.Of(Document);
        mutate(Document);
        string after = C3dRecordsEdit.Of(Document);
        if (after == before) return false;
        Push(new C3dRecordsEdit(description, before, after, ApplyRecords, alreadyApplied: true));
        RecordsChanged();
        return true;
    }

    /// <summary>An undo or redo put the records back.</summary>
    private void ApplyRecords(string text)
    {
        DocumentWrites++;
        C3dRecordsEdit.Apply(Document, text);
        RecordsChanged();
    }

    private void RecordsChanged()
    {
        ApplyAirBoxShown();
        DocumentChanged();
        RebuildSetupItems();
        ReloadSetupEditor();
        RefreshFieldsStale();
        // brief-em3d-75 — the thermal rows follow at once (the scene's tints follow its rebuild).
        RebuildThermalTree();
    }

    // ── setups (R-em3d49-1) ──────────────────────────────────────────────────────────────────

    /// <summary>The Setup Analyses dialog's rows: the embedded setups, in file order, then the external one.</summary>
    public ObservableCollection<C3dSetupItem> SetupItems { get; } = [];

    [ObservableProperty] private C3dSetupItem? _selectedSetupItem;

    /// <summary>
    /// The shell opens Simulate ▸ Setup Analyses… on this 3D view — the toolbar's tune button asks through here, so
    /// the button and the menu open the one dialog. Set by the workspace.
    /// </summary>
    public Action<C3dEditorViewModel>? SetupAnalysesRequested { get; set; }

    /// <summary>The toolbar's tune button: Simulate ▸ Setup Analyses… for this 3D view.</summary>
    [RelayCommand]
    public void OpenSetupAnalyses() => SetupAnalysesRequested?.Invoke(this);

    /// <summary>The active setup's name — editor state, per user (the shell stores it in the <c>.cwsuser</c>).</summary>
    public string? ActiveSetupName { get; private set; }

    /// <summary>Raised when the active setup changes: the shell remembers it, the scene redraws its box.</summary>
    public event Action? ActiveSetupChanged;

    /// <summary>The panel on the selected setup: <c>EmSetupEditorView</c>'s own document, never docked.</summary>
    [ObservableProperty] private EmSetupDocument? _setupEditor;

    /// <summary>R-em3d49-1c — a <c>.cem</c> naming this <c>.c3d</c>: shown read-only, marked with its path.</summary>
    public (string Path, EmSetup Setup)? ExternalSetup { get; private set; }

    /// <summary>The shell runs a setup: its name (null: the active one). Set by the workspace.</summary>
    public Func<C3dEditorViewModel, string?, Task>? RunRequested { get; set; }

    public string SetupsHeading => SetupItems.Count == 0
        ? "No setups. Add one to simulate this 3D view."
        : $"{SetupItems.Count} setup{(SetupItems.Count == 1 ? "" : "s")} · active: {ActiveSetupName ?? "none"}";

    /// <summary>Called by the shell with the stored active setup, before the first scene. brief-em3d-98 R-em3d98-3 — the
    /// document's own <c>ActiveSetup</c> wins; <paramref name="name"/> (the <c>.cwsuser</c>'s, from before the document kept it)
    /// is used only when the document names none; a missing or unknown name falls back to the first setup.</summary>
    public void RestoreActiveSetup(string? name)
    {
        ActiveSetupName = Document.ActiveSetup ?? name;
        RebuildSetupItems();
        if ((ActiveSetupName is null || !SetupItems.Any(i => i.Name == ActiveSetupName)) && SetupItems.FirstOrDefault(i => i.Refusal is null) is { } first)
            ActiveSetupName = first.Name;
        KeepActiveSetupInDocument(markDirty: false);
        RebuildSetupItems();
        ActiveSetupBadgesChanged();
        Viewer.SetRunSetup(ActiveRunSetup);
        RefreshFieldsStale();
        ApplyVisiblePlots();
    }

    /// <summary>R-em3d49-1c — Show 3D from a <c>.cem</c> pointing here: that <c>.cem</c> joins the list, read-only, active.</summary>
    public void ShowExternalSetup(string cemPath, EmSetup setup)
    {
        ExternalSetup = (Path.GetFullPath(cemPath), setup);
        RebuildSetupItems();
        SetActiveSetup(ExternalItemName);
        SelectedSetupItem = SetupItems.FirstOrDefault(i => i.IsExternal);
    }

    private string ExternalItemName => ExternalSetup is { } x ? (x.Setup.Name is { Length: > 0 } n ? n : Path.GetFileNameWithoutExtension(x.Path)) : "";

    private void RebuildSetupItems()
    {
        string? keep = SelectedSetupItem?.Name;
        bool keepExternal = SelectedSetupItem?.IsExternal == true;
        SetupItems.Clear();
        foreach (var s in C3dSetups.Read(Document))
        {
            var item = new C3dSetupItem(s.Name, s.Index, s.Refusal, null, s.Setup)
            {
                IsActive = !IsExternalActive && s.Name == ActiveSetupName, Fidelity = _fidelityNow.GetValueOrDefault(s.Name) ?? [],
            };
            item.ApplySolveStatuses(SolveStatuses);
            SetupItems.Add(item);
        }
        if (ExternalSetup is { } x)
            SetupItems.Add(new C3dSetupItem(ExternalItemName, -1, x.Setup.Is3D ? null : C3dSetups.PlanarRefusal, x.Path, x.Setup) { IsActive = IsExternalActive });
        _syncingSetups = true;
        try { SelectedSetupItem = SetupItems.FirstOrDefault(i => i.Name == keep && i.IsExternal == keepExternal) ?? SetupItems.FirstOrDefault(i => i.IsActive); }
        finally { _syncingSetups = false; }
        OnPropertyChanged(nameof(SetupsHeading));
        OnPropertyChanged(nameof(HasSetups));
        ReloadSetupEditor();
        RefreshSimulateGate();
    }

    public bool HasSetups => SetupItems.Count > 0;

    private bool IsExternalActive => ExternalSetup is not null && _externalActive;
    private bool _externalActive;
    private bool _syncingSetups;

    /// <summary>The active setup as the editor draws it and Simulate runs it: an embedded one, or the external <c>.cem</c>;
    /// null when there is none (or it cannot be read).</summary>
    public EmSetup? ActiveSetup
    {
        get
        {
            if (IsExternalActive) return ExternalSetup!.Value.Setup;
            if (ActiveSetupName is null) return null;
            return C3dSetups.Read(Document).FirstOrDefault(s => s.Name == ActiveSetupName)?.Setup;
        }
    }

    /// <summary>The active setup as its run is named ("&lt;stem&gt; &lt;name&gt;", or the .cem itself) — what the fields are read from.</summary>
    public EmSetup? ActiveRunSetup => IsExternalActive ? ExternalSetup!.Value.Setup
                                    : ActiveSetup is { } s ? C3dSetups.ForRun(s, TopFilePath) : null;

    [RelayCommand]
    public void SetActiveSetup(string? name)
    {
        bool external = ExternalSetup is not null && name == ExternalItemName && SetupItems.Any(i => i.IsExternal && i.Name == name);
        if (name == ActiveSetupName && external == _externalActive) return;
        ActiveSetupName = name;
        _externalActive = external;
        foreach (var i in SetupItems) i.IsActive = i.Name == name && i.IsExternal == external;
        OnPropertyChanged(nameof(SetupsHeading));
        KeepActiveSetupInDocument();
        ActiveSetupBadgesChanged();
        ActiveSetupChanged?.Invoke();
        Viewer.SetRunSetup(ActiveRunSetup);
        Viewer.Regenerate();
        RefreshFieldsStale();
        // brief-em3d-83 — a plot pinned to a setup is unmoved; its row says which setup when it is not the active one.
        RebuildFieldPlotTree();
        ApplyVisiblePlots();
        RefreshSimulateGate();
    }

    partial void OnSelectedSetupItemChanged(C3dSetupItem? value)
    {
        if (_syncingSetups) return;
        ReloadSetupEditor();
    }

    /// <summary>Builds the panel for the selected setup: the <c>.cem</c> view model in its embedded mode.</summary>
    private void ReloadSetupEditor()
    {
        if (_suppressEditorReload) return;
        var item = SelectedSetupItem;
        if (item is null) { SetupEditor = null; return; }
        EmSetup? setup = item.IsExternal ? ExternalSetup?.Setup.Clone()
                       : item.Index >= 0 && item.Index < Document.Setups.Count ? TryRead(Document.Setups[item.Index]) : null;
        if (setup is null) { SetupEditor = null; return; }
        // The same view model, the same panel: rebuilt only when the setup is not the one already shown, so an edit made
        // in the panel does not tear the panel down under the user's cursor.
        if (SetupEditor?.ViewModel is { } shown && _editorFor == (item.Name, item.IsExternal) &&
            EmSetupPersistence.Serialize(shown.Working) == EmSetupPersistence.Serialize(setup))
        {
            shown.RefreshThermal();          // brief-em3d-75 — the page lists the document's heat sources
            return;
        }
        var vm = new EmSetupEditorViewModel(item.IsExternal ? item.ExternalPath! : FilePath, setup, embedded: true)
        {
            IsReadOnly = item.IsExternal,
        };
        // brief-em3d-75 R-em3d75-3 — the thermal page reads this document's heat sources, probes and variables.
        // brief-em3d-79: and its path and results folder, which a circuit link resolves against
        if (!item.IsExternal)
            vm.ThermalContext = new EmThermalContext(() => Document, () => Resolution, () => Elaboration, () => FilePath, () => ResultsRootProvider?.Invoke());
        // brief-emb-1 R-emb1-8 — the Component ports list reads this document's placed layouts and builds through the run's own code.
        if (!item.IsExternal)
            vm.ComponentPortsContext = new EmComponentPortsContext(() => Document, () => FilePath, _workspaceCws, () => Elaboration,
                                                                   (text, warning) => PostMessage?.Invoke(text, warning));
        if (!item.IsExternal)
        {
            string name = item.Name;
            vm.EmbeddedCommit = (_, after, description) => CommitSetupFromPanel(name, after, description);
        }
        vm.RunRequested = _ => RunRequested?.Invoke(this, item.IsExternal ? null : item.Name) ?? Task.CompletedTask;
        // brief-dfr31-1 R-dfr31-1-4 — the terminal picker lists this view's pieces of metal (from the elaboration the view
        // already holds, grouped off the UI thread), a row's Pick takes the next object clicked in the view, and the bodies
        // a row names are selected in the view while it is edited.
        if (BodyListerNow() is { } lister) vm.BodyLister = lister;
        vm.CanPickInView = true;
        vm.HighlightRequested += HighlightSetupBodies;
        // A drawing tool or a measurement takes a click in the view before the selection sees it, so arming a Pick puts them away.
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(EmSetupEditorViewModel.PickingRow) || vm.PickingRow is null) return;
            if (_tool is not null) Disarm();
            Viewer.EndMeasure();
        };
        // A rebuilt panel in a window already open (an undo, a setup edited elsewhere) lists at once, as the window's open did.
        if (SetupEditor?.ViewModel is { IsShown: true })
        {
            vm.IsShown = true;
            _ = vm.ListBodiesAsync();
        }
        _editorFor = (item.Name, item.IsExternal);
        SetupEditor = new EmSetupDocument(item.Name, vm, item.IsExternal ? item.ExternalPath! : FilePath);
    }

    private (string Name, bool External)? _editorFor;

    /// <summary>The setup panel's lister over the elaboration the view holds now (grouped off the UI thread); null before the first.</summary>
    private Func<CancellationToken, IReadOnlyList<Em3dBodyChoice>>? BodyListerNow()
    {
        if (Elaboration is not { } elaboration) return null;
        var document = Document;
        return _ => Em3dBodyList.Of(document, elaboration);
    }

    /// <summary>A new elaboration adopted: the open setup panel's conductors follow it.</summary>
    private void RefreshSetupBodies()
    {
        if (SetupEditor?.ViewModel is { IsStaticSetup: true } vm && BodyListerNow() is { } lister) vm.SetBodyLister(lister);
    }

    /// <summary>brief-dfr31-1 — selects in the view every object of the bodies a terminal row names.</summary>
    private void HighlightSetupBodies(IReadOnlyList<string> members)
    {
        if (members.Count == 0) return;
        var wanted = members.ToHashSet(StringComparer.Ordinal);
        // A highlight is not a pick: an armed row must not take the bodies of the row being looked at.
        _highlightingSetupBodies = true;
        try
        {
            Viewer.SetSelection(Viewer.Scene.Objects.Where(o => o.Pickable && wanted.Contains(o.Name))
                                                    .Select(o => Scene3DItem.OfObject(o.Id)));
        }
        finally { _highlightingSetupBodies = false; }
    }

    private bool _highlightingSetupBodies;

    /// <summary>brief-dfr31-1 — a click in the view while a terminal row's Pick is armed fills that row.</summary>
    private void PickForSetup()
    {
        if (_highlightingSetupBodies || SetupEditor?.ViewModel is not { PickingRow: not null } setup) return;
        if (Viewer.SelectedObjects() is [var picked, ..]) setup.PickConductor(picked.Name);
    }

    private static EmSetup? TryRead(JsonElement e)
    {
        try { return EmSetupPersistence.FromEmbedded(e); }
        catch (Exception) { return null; }
    }

    /// <summary>R-em3d49-1b — the panel's edit, as the document's: the setup written back where it is, one entry.</summary>
    private void CommitSetupFromPanel(string name, string afterJson, string description)
    {
        var edited = EmSetupPersistence.Deserialize(afterJson);
        int index = C3dSetups.Read(Document).FirstOrDefault(s => s.Name == name)?.Index ?? -1;
        if (index < 0) return;
        bool renamed = edited.Name != name;
        _suppressEditorReload = true;
        try
        {
            ChangeRecords($"{description} ({name})", d =>
            {
                d.Setups[index] = EmSetupPersistence.ToEmbedded(edited);
                if (renamed) C3dThermal.RenameSetupReferences(d, name, edited.Name);
            });
        }
        finally { _suppressEditorReload = false; }
        if (renamed && ActiveSetupName == name) SetActiveSetup(edited.Name);
        if (name == ActiveSetupName) { Viewer.SetRunSetup(ActiveRunSetup); }
    }

    private bool _suppressEditorReload;

    /// <summary>Add: a new Palace setup, named S1, S2 …; the first one added becomes the active one.</summary>
    [RelayCommand]
    public void AddSetup()
    {
        var names = C3dSetups.Read(Document).Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        string name = Enumerable.Range(1, 10_000).Select(n => $"S{n}").First(n => !names.Contains(n));
        var setup = new EmSetup { Name = name, Solver3D = CircuitRF.Design.Em3d.Em3dSolvers.Default, RobustMeshing = true,
                                  Frequency = EmSetup.NewSetupFrequency(), AirBox = EmSetupPersistence.FromCem(Document.AirBox) };
        ChangeRecords($"Add setup {name}", d => d.Setups.Add(EmSetupPersistence.ToEmbedded(setup)));
        if (ActiveSetupName is null || ActiveSetup is null) SetActiveSetup(name);
        SelectedSetupItem = SetupItems.FirstOrDefault(i => i.Name == name && !i.IsExternal);
    }

    /// <summary>brief-em3d-75 R-em3d75-3 — Add Thermal: a new thermal setup (no EM solver; brief 73 D1), named T1, T2 …, with
    /// its Thermal section to fill in on its page; the first setup added becomes the active one.</summary>
    [RelayCommand]
    public void AddThermalSetup()
    {
        var names = C3dSetups.Read(Document).Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        string name = Enumerable.Range(1, 10_000).Select(n => $"T{n}").First(n => !names.Contains(n));
        var setup = new EmSetup { Name = name, Solver3D = Em3dSolverChoice.None, Problem3D = Em3dProblemType.Thermal, Thermal = new CemThermal(),
                                  AirBox = EmSetupPersistence.FromCem(Document.AirBox) };
        ChangeRecords($"Add thermal setup {name}", d => d.Setups.Add(EmSetupPersistence.ToEmbedded(setup)));
        if (ActiveSetupName is null || ActiveSetup is null) SetActiveSetup(name);
        SelectedSetupItem = SetupItems.FirstOrDefault(i => i.Name == name && !i.IsExternal);
    }

    /// <summary>Duplicate: the selected setup, named "&lt;name&gt; copy" (then 2, 3 …).</summary>
    [RelayCommand]
    public void DuplicateSetup()
    {
        if (SelectedSetupItem is not { IsExternal: false } item || item.Index < 0 || TryRead(Document.Setups[item.Index]) is not { } s) return;
        var names = C3dSetups.Read(Document).Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        string name = item.Name + " copy";
        for (int n = 2; names.Contains(name); n++) name = $"{item.Name} copy {n}";
        s.Name = name;
        ChangeRecords($"Duplicate setup {item.Name}", d => d.Setups.Insert(item.Index + 1, EmSetupPersistence.ToEmbedded(s)));
        SelectedSetupItem = SetupItems.FirstOrDefault(i => i.Name == name && !i.IsExternal);
    }

    /// <summary>Rename the selected setup; null on success, else why not. The active one stays active under its new name.</summary>
    public string? RenameSetup(string newName)
    {
        newName = newName.Trim();
        if (SelectedSetupItem is not { IsExternal: false } item || item.Index < 0) return "Only a setup of this 3D view can be renamed here.";
        if (newName == item.Name) return null;
        if (newName.Length == 0) return "A setup's name cannot be empty: a 3D view's setups are chosen by name.";
        if (C3dSetups.Read(Document).Any(s => s.Name == newName)) return $"This 3D view already has a setup named '{newName}'.";
        if (TryRead(Document.Setups[item.Index]) is not { } s) return "That setup cannot be read, so it cannot be renamed.";
        s.Name = newName;
        bool wasActive = ActiveSetupName == item.Name && !IsExternalActive;
        ChangeRecords($"Rename setup {item.Name} to {newName}", d =>
        {
            d.Setups[item.Index] = EmSetupPersistence.ToEmbedded(s);
            C3dThermal.RenameSetupReferences(d, item.Name, newName);
        });
        if (wasActive) SetActiveSetup(newName);
        SelectedSetupItem = SetupItems.FirstOrDefault(i => i.Name == newName && !i.IsExternal);
        return null;
    }

    /// <summary>Remove the selected setup (undoable). Its results stay where they were written.</summary>
    [RelayCommand]
    public void RemoveSetup()
    {
        if (SelectedSetupItem is not { IsExternal: false } item || item.Index < 0) return;
        bool wasActive = ActiveSetupName == item.Name && !IsExternalActive;
        ChangeRecords($"Remove setup {item.Name}", d => d.Setups.RemoveAt(item.Index));
        if (wasActive) SetActiveSetup(SetupItems.FirstOrDefault(i => i.Refusal is null)?.Name);
    }

    [RelayCommand]
    public void MakeSelectedActive()
    {
        if (SelectedSetupItem is { } item) SetActiveSetup(item.Name);
    }

    /// <summary>Simulate ▸ Run: the active setup through the shell's run path.</summary>
    [RelayCommand(CanExecute = nameof(CanSimulateActive))]
    public Task SimulateActive() => RunRequested?.Invoke(this, null) ?? Task.CompletedTask;

    /// <summary>
    /// brief-emi-6 — why Run is off for the active setup: a comparison with fewer than two solvers that can run here (the
    /// setup panel's own check, <see cref="EmSetupEditorViewModel.CompareBlockingReasonFor"/>). Null otherwise: every other
    /// refusal is still said when Run is pressed, as before.
    /// </summary>
    public string? SimulateBlockedReason
        => ActiveSetup is { } setup ? EmSetupEditorViewModel.CompareBlockingReasonFor(setup, Em3dSolverInstallState.Known) : null;

    private bool CanSimulateActive() => SimulateBlockedReason is null;

    /// <summary>Re-asks <see cref="SimulateBlockedReason"/>: the active setup, its solvers, or what is installed changed.</summary>
    public void RefreshSimulateGate()
    {
        OnPropertyChanged(nameof(SimulateBlockedReason));
        SimulateActiveCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Why the active setup cannot be run now, or null.</summary>
    public string? SimulateRefusal()
    {
        if (CanPopOut) return "Pop out to the top 3D view to simulate it: a run solves the document the tab holds.";
        if (IsExternalActive) return null;
        if (ActiveSetupName is null)
            return SetupItems.Count == 0 ? "This 3D view has no setup. Add one in Simulate ▸ Setup Analyses…" : "No setup is active: make one active in Simulate ▸ Setup Analyses…";
        var read = C3dSetups.Read(Document).FirstOrDefault(s => s.Name == ActiveSetupName);
        return read is null ? $"The active setup '{ActiveSetupName}' is no longer in this 3D view." : read.Refusal ?? SimulateBlockedReason;
    }

    /// <summary>The setup a run of <paramref name="name"/> (null: the active one) uses, as its run names it — or why not.</summary>
    public (EmSetup? Setup, bool FromCem, string? Refusal) RunSetupFor(string? name)
    {
        if (CanPopOut) return (null, false, SimulateRefusal());
        if (name is null && IsExternalActive) return (ExternalSetup!.Value.Setup.Clone(), true, null);
        name ??= ActiveSetupName;
        if (name is null) return (null, false, SimulateRefusal());
        var read = C3dSetups.Read(Document).FirstOrDefault(s => s.Name == name);
        if (read is null) return (null, false, $"This 3D view has no setup named '{name}'.");
        if (read.Refusal is { } why) return (null, false, why);
        if (EmSetupEditorViewModel.CompareBlockingReasonFor(read.Setup!, Em3dSolverInstallState.Known) is { } blocked)
            return (null, false, blocked);
        return (C3dSetups.ForRun(read.Setup!, TopFilePath), false, null);
    }

    /// <summary>The document as a run reads it: a copy, so an edit during the run changes nothing it solves.</summary>
    public C3dDocument RunDocument() => C3dPersistence.Deserialize(C3dPersistence.Serialize(Document));

    // ── the active setup's box and the ports, as the scene shows them ────────────────────────

    /// <summary>What a scene build resolved for the records: the box, each port, each face boundary (keyed by generation).</summary>
    private sealed record RecordsView(Em3dAirBox? Box, IReadOnlyList<C3dPortResult> Ports,
                                      IReadOnlyList<(C3dFaceBoundary Boundary, IReadOnlyList<Em3dFacePolygon> Pieces, string? Refusal)> Boundaries,
                                      C3dPortContext Context)
    {
        /// <summary>brief-em3d-75 R-em3d75-2 — the active thermal setup's conditioned faces, tinted by kind.</summary>
        public IReadOnlyList<Scene3DFaceTint> ThermalTints { get; init; } = [];

        /// <summary>brief-sar-3 — each lumped element, resolved by the port's rule against this elaboration.</summary>
        public IReadOnlyList<C3dLumpedResult> Lumped { get; init; } = [];

        /// <summary>The active setup's component ports, built as a run builds them: each part, with its port resolved when it
        /// was built (null when it is left out). Drawn, labelled and listed like the document's own ports, but never editable
        /// as one: the setup's ComponentPorts list is what makes them.</summary>
        public IReadOnlyList<(C3dComponentPort Part, C3dPortResult? Result)> ComponentPorts { get; init; } = [];

        /// <summary>The conductors the component ports changed, by name, as the run solves them: a conductor with its landing
        /// rectangles unioned in, and each connector shell bridge. The scene draws these in place of the elaboration's own, so
        /// a port sheet is seen against the copper it is verified against rather than the copper before its landings.</summary>
        public IReadOnlyDictionary<string, Em3dSolid> ComponentPortSolids { get; init; } = new Dictionary<string, Em3dSolid>();
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, RecordsView> _records = new();
    private RecordsView? _recordsView;

    /// <summary>The ports as the current scene resolved them (each resolved or refused).</summary>
    public IReadOnlyList<C3dPortResult> PortResults => _recordsView?.Ports ?? [];

    /// <summary>brief-em3d-90 — the thermal boundary tints the current scene was built with, by tint name
    /// (<c>thermal:die/zmin</c>): a boundary whose row is unticked is not among them.</summary>
    public IReadOnlyList<string> ThermalTintNames => [.. (_recordsView?.ThermalTints ?? []).Select(t => t.Name)];

    /// <summary>The active setup's air box as the current scene drew it, or null.</summary>
    public Em3dAirBox? ShownAirBox => _recordsView?.Box;

    /// <summary>The setup the scene draws with: the active one's full .cem spelling, for the build's snapshot.</summary>
    private string? SceneSetupJson() => ActiveSetup is { } s ? EmSetupPersistence.Serialize(s) : null;

    /// <summary>Runs on the build's thread: the box, the ports and the face boundaries for this elaboration.</summary>
    private RecordsView ResolveRecords(C3dDocument doc, C3dElaboration e, string? setupJson, IReadOnlySet<string>? hiddenTints = null)
    {
        EmSetup? setup = null;
        if (setupJson is not null)
            try { setup = EmSetupPersistence.Deserialize(setupJson); } catch (Exception) { setup = null; }
        // 3D editor round 3 — with no setup, the box a new setup would solve in (owner decision): drawn once there is a solid
        // or a sheet to size it (Extent is null until then), never before.
        // With no setup, the document's own box (its padding and walls, stated before any setup exists) on that default.
        var boxSetup = setup ?? (setupJson is null
            ? new EmSetup { Solver3D = CircuitRF.Design.Em3d.Em3dSolvers.Default, AirBox = EmSetupPersistence.FromCem(doc.AirBox) }
            : null);
        // 3D editor round 5 — the box is sized from what DID elaborate, refusals or not: a wire whose end a boolean took the
        // pad from is the wire's refusal, and gating the box on a clean elaboration took the box — and, with no setup, its
        // tree row — away with it. The ports and face boundaries below still wait for a clean elaboration.
        var box = boxSetup is not null ? C3dProblemAssembly.AirBox(boxSetup, e, out _) : null;
        var ctx = C3dProblemAssembly.PortContext(setup, doc, e, box);
        var ports = e.Ok ? C3dPorts.Resolve(doc, ctx) : [];
        var boundaries = e.Ok ? C3dProblemAssembly.FaceBoundaryPreview(doc, e) : [];
        var lumped = e.Ok ? doc.LumpedElements.Select(l => C3dLumpedElements.Resolve(l, doc.DbuPerMicron, ctx, e.Resolution)).ToList() : [];
        return new RecordsView(box, ports, boundaries, ctx)
        {
            ThermalTints = ThermalTints(doc, e, setup, hiddenTints), Lumped = lumped,
            ComponentPorts = e.Ok ? ComponentPortsOf(setup, setupJson, doc, e, box) : [],
            ComponentPortSolids = e.Ok && _componentPortsBuilt is { } built ? built.Solids : new Dictionary<string, Em3dSolid>(),
        };
    }

    /// <summary>The last component ports built, and what they were built from (see <see cref="ComponentPortsOf"/>).</summary>
    private (string Key, IReadOnlyList<(C3dComponentPort, C3dPortResult?)> Ports, IReadOnlyDictionary<string, Em3dSolid> Solids)? _componentPortsBuilt;

    /// <summary>
    /// The active setup's component ports on this elaboration (the build's thread, under the elaboration lock): each part, and its
    /// port resolved against the elaboration with the landings applied, numbered after the document's own as a run numbers them.
    /// Kept while nothing they are built from changes — the document but for its own ports and setups, the files the elaboration
    /// read, the setup, and the first number free — so an edit to a drawn port, an object's look or a selection does not build
    /// every part's port again (a board's fourteen took about a quarter of a second, each scene).
    /// </summary>
    private IReadOnlyList<(C3dComponentPort, C3dPortResult?)> ComponentPortsOf(EmSetup? setup, string? setupJson, C3dDocument doc, C3dElaboration e,
                                                                             Em3dAirBox? box)
    {
        if (setup is not { Is3D: true, IsThermal: false, ComponentPorts.Count: > 0 } || setup.Problem3D == Em3dProblemType.Electrostatic)
        {
            _componentPortsBuilt = null;
            return [];
        }
        int first = C3dProblemAssembly.FirstComponentPortNumber(doc);
        var geometry = C3dPersistence.Deserialize(C3dPersistence.Serialize(doc));
        geometry.Ports.Clear();
        geometry.Setups.Clear();
        string key = string.Join("\n", setupJson, first.ToString(CultureInfo.InvariantCulture), ReadStamp(e.FilesRead, FilePath),
                                 C3dPersistence.Serialize(geometry));
        if (_componentPortsBuilt is { } kept && kept.Key == key) return kept.Ports;
        C3dComponentPortBuild? build;
        try { build = C3dComponentPorts.Build(setup, doc, e, first); }
        catch (Exception) { _componentPortsBuilt = null; return []; }
        if (build is null) { _componentPortsBuilt = null; return []; }
        var ctx = C3dProblemAssembly.PortContext(setup, doc, build.Elaboration, box);
        IReadOnlyList<(C3dComponentPort, C3dPortResult?)> ports =
            [.. build.Ports.Select(p => (p, p.Port is { } port ? C3dPorts.Resolve(port, doc.DbuPerMicron, ctx) : (C3dPortResult?)null))];
        _componentPortsBuilt = (key, ports, ChangedSolids(build));
        return ports;
    }

    /// <summary>The solids of <paramref name="build"/>'s elaboration that its landings and shell bridges made or changed, by name
    /// (see <see cref="RecordsView.ComponentPortSolids"/>).</summary>
    private static IReadOnlyDictionary<string, Em3dSolid> ChangedSolids(C3dComponentPortBuild build)
    {
        var names = new HashSet<string>(build.Ports.SelectMany(p => p.Landings).Select(l => l.Conductor), StringComparer.Ordinal);
        names.UnionWith(build.Bridges.Select(b => b.Name));
        return build.Elaboration.Solids.Where(s => names.Contains(s.Name)).ToDictionary(s => s.Name, StringComparer.Ordinal);
    }

    /// <summary>The component ports the current scene built (see <see cref="RecordsView.ComponentPorts"/>).</summary>
    public IReadOnlyList<(C3dComponentPort Part, C3dPortResult? Result)> ComponentPortResults => _recordsView?.ComponentPorts ?? [];

    /// <summary>A component port's row kind: listed and shown like a port, never edited as the document's own.</summary>
    public const string ComponentPortKind = "ComponentPort";

    /// <summary>
    /// 3D editor round 3 — whether the air box is drawn (faces and edges): the toolbar switch and the tree tick. The user's
    /// choice, put back on every scene the editor adopts, so a rebuild never turns a hidden box back on. On by default: the
    /// box is drawn once there is a shape to size it (absorbing faces are clear in the editor, so only its outline and any
    /// wall show). Round 5: it is the document's <see cref="C3dDocument.AirBoxHidden"/>, one undoable records edit — held
    /// by the editor alone, it was lost on close and a reopened document showed the box again.
    /// </summary>
    public bool AirBoxShown
    {
        get => !Document.AirBoxHidden;
        set
        {
            if (value == AirBoxShown) return;
            // Drawing state, as an object's Hidden is: one records entry (it is saved and undone with them), but it writes
            // only the flag back and rebuilds nothing. Through ChangeRecords the tick re-elaborated the whole document.
            string before = C3dRecordsEdit.Of(Document);
            Document.AirBoxHidden = !value;
            Push(new C3dRecordsEdit($"{(value ? "Show" : "Hide")} {AirBoxName}", before, C3dRecordsEdit.Of(Document),
                                    ApplyAirBoxHidden, alreadyApplied: true));
            AirBoxHiddenChanged();
        }
    }

    /// <summary>The air box's tick, undone or redone: its records entry differs from the document in the flag alone.</summary>
    private void ApplyAirBoxHidden(string text)
    {
        DocumentWrites++;
        Document.AirBoxHidden = C3dRecordsEdit.AirBoxHiddenOf(text);
        AirBoxHiddenChanged();
    }

    private void AirBoxHiddenChanged()
    {
        ApplyAirBoxShown();
        OnPropertyChanged(nameof(IsDirty));
        RefreshFieldsStale();
    }

    private void ApplyAirBoxShown()
    {
        OnPropertyChanged(nameof(AirBoxShown));
        if (Viewer.ShowBoundaryFaces != AirBoxShown) Viewer.ShowBoundaryFaces = AirBoxShown;
        if (AllTreeItems().FirstOrDefault(t => t.IsAirBox) is { } box) box.Sync(AirBoxShown);
    }

    /// <summary>After a scene is adopted: the records it resolved become the editor's, and the box starts shown when the
    /// active setup has any face that is not absorbing (R-em3d49-3a).</summary>
    private void AdoptRecords(long generation)
    {
        AdoptFidelity(generation);
        foreach (long old in _records.Keys.Where(k => k < generation).ToList()) _records.TryRemove(old, out _);
        if (!_records.TryRemove(generation, out var view)) return;
        _recordsView = view;
        RebuildRecordsTree();
        ApplyAirBoxShown();
    }

    // ── ports (R-em3d49-2) ───────────────────────────────────────────────────────────────────

    /// <summary>The smallest port number the document does not use — a terminal's number is a port's (brief-em3d-114).</summary>
    public int NextPortNumber() => NextPortNumbers(1)[0];

    /// <summary>The <paramref name="count"/> smallest port numbers the document does not use, ascending.</summary>
    private List<int> NextPortNumbers(int count)
    {
        var used = C3dPorts.UsedNumbers(Document).ToHashSet();
        var free = new List<int>(count);
        for (int n = 1; free.Count < count; n++) if (!used.Contains(n)) free.Add(n);
        return free;
    }

    /// <summary>R-em3d49-2d — what a new port starts as: the next number, and the last port's Z0.</summary>
    public C3dPort NewPortTemplate(Em3dPortKind kind = Em3dPortKind.Lumped)
    {
        int n = NextPortNumber();
        var last = Document.Ports.LastOrDefault();
        return new C3dPort { Number = n, Name = $"P{n}", Kind = kind, Z0 = last?.Terminals?.LastOrDefault()?.Z0 ?? last?.Z0 ?? "50" };
    }

    /// <summary>Adds a port: one undo entry; the status line says what it joins, or why it cannot be built.
    /// <paramref name="referenceWhy"/> is why Make Port chose the reference it wrote (brief-em3d-121), which the resolver
    /// would otherwise call stated.</summary>
    /// <param name="context">What the status line judges the new port against; the scene's when null. A port that replaced a
    /// sheet passes the scene's without it — the elaboration still shows the sheet until the scene is rebuilt.</param>
    public void AddPort(C3dPort port, string? referenceWhy = null, C3dPortContext? context = null)
    {
        ChangeRecords($"Add port {port.Name}", d => d.Ports.Add(port));
        ToolCommits++;
        var r = C3dPorts.Resolve(port, Document.DbuPerMicron, context ?? CurrentPortContext());
        StatusMessage = r.Refusal ?? (r.Terminals is { } ts
            ? $"Made {C3dPorts.Label(port)}, a wave port with {ts.Count} terminals ({string.Join(", ", ts.Select(t => $"{t.SourceLabel} on '{t.PositiveObject}'"))}); " +
              $"'{r.Resolved!.NegativeObject}' is the reference: {referenceWhy ?? r.Reason}."
            : C3dPortReports.Describe(r));
    }

    /// <summary>The port context for the scene now shown — for a port the user is drawing.</summary>
    private C3dPortContext CurrentPortContext()
        => _recordsView?.Context ?? (Elaboration is { } e ? C3dProblemAssembly.PortContext(ActiveSetup, Document, e, null) : new C3dPortContext(
               new C3dElaboration([], [], [], new Dictionary<string, C3dProvenance>(), [], []), Document.DbuPerMicron, null, []));

    /// <summary>The document port a scene object is (<c>port/N</c>: the port, or a terminal of it), or null.</summary>
    public C3dPort? PortOf(Scene3DObject o)
        => o.Kind == Scene3DKind.Port ? C3dPorts.OwnerOf(Document, o.PortNumber) : null;

    internal IReadOnlyList<C3dPort> SelectedPorts()
        => [.. Viewer.SelectedObjects().Select(PortOf).OfType<C3dPort>().Distinct()];

    /// <summary>brief-em3d-114 — the terminals selected in the scene (a terminal's sheet is its own object), with their ports.</summary>
    internal IReadOnlyList<(C3dPort Port, C3dTerminal Terminal)> SelectedTerminals()
        => [.. Viewer.SelectedObjects().Where(o => o.Kind == Scene3DKind.Port)
                  .Select(o => (Port: PortOf(o), Number: o.PortNumber))
                  .Where(t => t.Port is { Terminals: not null })
                  .Select(t => (t.Port!, C3dPorts.TerminalOf(t.Port!, t.Number)))
                  .Where(t => t.Item2 is not null).Select(t => (t.Item1, t.Item2!)).Distinct()];

    /// <summary>Flips ordinary ports; a multi-terminal port has no polarity of its own (its terminals each have one).</summary>
    public void FlipPorts(IReadOnlyList<C3dPort> ports)
    {
        var set = ports.Where(p => !C3dPorts.HasTerminals(p)).ToHashSet();
        if (set.Count == 0) return;
        ChangeRecords(set.Count == 1 ? $"Flip {C3dPorts.Label(set.First())}" : $"Flip {set.Count} ports",
                      d => { foreach (var p in d.Ports.Where(set.Contains)) p.Flip = !p.Flip; });
    }

    /// <summary>brief-em3d-114 R-em3d114-3d — reverses one terminal's voltage path; the reference stays the reference.</summary>
    public void FlipTerminal(C3dPort port, C3dTerminal terminal)
        => ChangeRecords($"Flip {C3dPorts.TerminalLabel(port, terminal)}", _ => terminal.Flip = !terminal.Flip);

    /// <summary>
    /// brief-em3d-114 R-em3d114-3d — states a multi-terminal port's reference. A conductor that is one of its terminals swaps
    /// places with the reference it had, so the port keeps one terminal per signal conductor; null on success, else why not.
    /// </summary>
    public string? SetPortReference(C3dPort port, string conductor)
    {
        if (port.Terminals is not { Count: > 0 } ts) return $"{C3dPorts.Label(port)} has no terminals, so it has no reference to set.";
        string? was = PortResults.FirstOrDefault(r => r.Port == port)?.Resolved?.NegativeObject ?? port.Reference;
        var swap = ts.FirstOrDefault(t => t.Conductor == conductor);
        if (swap is not null && was is null)
            return $"'{conductor}' is {C3dPorts.TerminalLabel(port, swap)}, and the port has no reference to give that terminal in its place.";
        ChangeRecords($"Reference of {C3dPorts.Label(port)}: {conductor}", _ =>
        {
            port.Reference = conductor;
            if (swap is not null) swap.Conductor = was!;
        });
        return null;
    }

    public void SetPortKind(IReadOnlyList<C3dPort> ports, Em3dPortKind kind)
    {
        var set = ports.ToHashSet();
        ChangeRecords($"Make {(ports.Count == 1 ? C3dPorts.Label(ports[0]) : $"{ports.Count} ports")} {kind.ToString().ToLowerInvariant()}",
                      d => { foreach (var p in d.Ports.Where(set.Contains)) p.Kind = kind; });
    }

    /// <summary>Sets the Z0 of port <paramref name="number"/> — an ordinary port's, or a terminal's (brief-em3d-114); null on
    /// success, else why not.</summary>
    public string? SetPortZ0(int number, string z0)
    {
        if (!C3dPorts.TryParseZ0(z0, out var z) || !(z.Real > 0))
            return $"'{z0}' is not a reference impedance: a number of ohms with a positive real part, or a complex one such as 25+j10.";
        ChangeRecords($"Z0 of port {number}", d =>
        {
            foreach (var p in d.Ports)
            {
                if (C3dPorts.TerminalOf(p, number) is { } t) t.Z0 = z0.Trim();
                else if (!C3dPorts.HasTerminals(p) && p.Number == number) p.Z0 = z0.Trim();
            }
        });
        return null;
    }

    /// <summary>
    /// Renumbers one port (not a wave port with terminals, whose terminals carry the numbers): a whole number from 1 that
    /// no other port or terminal uses. A name that was the default for the old number (<c>P3</c>) follows it; a name the
    /// user gave is kept. One undo entry. Returns why it was refused, or null.
    /// </summary>
    public string? SetPortNumber(C3dPort port, string text)
    {
        if (C3dPorts.HasTerminals(port))
            return $"{C3dPorts.Label(port)} has terminals; each terminal has its own number.";
        if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 1)
            return $"'{text.Trim()}' is not a port number: a whole number from 1.";
        int old = port.Number;
        if (n == old) return null;
        if (Document.Ports.Where(q => !ReferenceEquals(q, port)).SelectMany(C3dPorts.Numbers).Contains(n))
            return $"Port {n} is already taken; each port has its own number.";
        ChangeRecords($"Renumber port {old} to {n}", d =>
        {
            if (port.Name == $"P{old}") port.Name = $"P{n}";
            port.Number = n;
            d.Ports = [.. d.Ports.OrderBy(q => C3dPorts.Numbers(q).DefaultIfEmpty(0).Min())];
        });
        if (_hiddenPorts.Remove(C3dPorts.ProblemName(old))) _hiddenPorts.Add(C3dPorts.ProblemName(n));
        StatusMessage = $"Port {old} is now port {n}. A result saved before keeps the old numbering.";
        return null;
    }

    /// <summary>Renames one port. Blank is refused; one undo entry. Returns why it was refused, or null.</summary>
    public string? SetPortName(C3dPort port, string text)
    {
        string name = text.Trim();
        if (name.Length == 0) return "A port needs a name.";
        if (name == port.Name) return null;
        ChangeRecords($"Rename {C3dPorts.Label(port)} to {name}", _ => port.Name = name);
        return null;
    }

    /// <summary>A drawn port's place on its plane: the centre (u, v), where the plane sits, and the size (DBU).</summary>
    public static (long CentreU, long CentreV, long At, long Width, long Height) PortPlace(C3dPort p)
        => (p.Rect.Min.U + p.Rect.Size.U / 2, p.Rect.Min.V + p.Rect.Size.V / 2, p.Offset, p.Rect.Size.U, p.Rect.Size.V);

    /// <summary>
    /// Places a drawn port: its plane, where the plane sits, its centre on it and its width and height there (DBU). One undo
    /// entry; null on success, else why not. A centre and size left as they were keep the corner exactly, so an odd size that
    /// halves a DBU short does not creep. A cylinder-patch port is placed by its cylinder, not here.
    /// </summary>
    public string? SetPortGeometry(C3dPort port, C3dPlane plane, long at, long centreU, long centreV, long width, long height)
    {
        if (port.Cylinder is not null) return $"{C3dPorts.Label(port)} is a patch of a cylinder; its place is its cylinder's.";
        if (width <= 0 || height <= 0) return "A port's width and height are lengths greater than zero.";
        var was = PortPlace(port);
        bool samePlane = plane == port.Plane;
        long minU = samePlane && width == was.Width && centreU == was.CentreU ? port.Rect.Min.U : centreU - width / 2;
        long minV = samePlane && height == was.Height && centreV == was.CentreV ? port.Rect.Min.V : centreV - height / 2;
        ChangeRecords($"Place {C3dPorts.Label(port)}", _ =>
        {
            port.Plane = plane;
            port.Offset = at;
            port.Rect = new C3dRect { Min = new C3dPoint2(minU, minV), Size = new C3dPoint2(width, height) };
        });
        return null;
    }

    /// <summary>The component port a scene object is (its sheet), or null.</summary>
    public (C3dComponentPort Part, C3dPortResult? Result)? ComponentPortOf(Scene3DObject o)
        => o.Kind == Scene3DKind.Port && ComponentPortResults.FirstOrDefault(c => c.Part.Port?.Number == o.PortNumber) is { Part: not null } hit
            ? hit : null;

    /// <summary>The component port a Component ports row stands for — a left-out part's included, which has no sheet.</summary>
    public (C3dComponentPort Part, C3dPortResult? Result)? ComponentPortOfRow(C3dTreeItem row)
        => row.Kind != ComponentPortKind ? null
         : ComponentPortResults.FirstOrDefault(c => c.Result?.Resolved?.Name == row.Name
                                                    || "component/" + c.Part.Refdes + "/" + (c.Part.Pin ?? "") == row.Name) is { Part: not null } hit
            ? hit : null;

    /// <summary>What the active setup states for one part (its own entry, beside <c>*</c> or alone), or null.</summary>
    public EmComponentPort? ComponentPortEntry(string refdes)
        => ActiveSetup?.ComponentPorts.FirstOrDefault(c => c.Refdes.Equals(refdes, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A component port's own settings — its Z0 and its width across the gap (µm), null for the default — written into the active
    /// setup's entry for the part (added beside <c>*</c> when there was none, dropped again when it states nothing). Its place
    /// and orientation stay the part's pads'. One undo entry; null on success, else why not.
    /// </summary>
    public string? SetComponentPortOverride(string refdes, string? z0, double? widthUm)
    {
        if (z0 is { } zt && (!C3dPorts.TryParseZ0(zt, out var z) || !(z.Real > 0)))
            return $"'{zt}' is not a reference impedance: a number of ohms with a positive real part, or a complex one such as 25+j10.";
        if (widthUm is <= 0) return "A port's width is a length greater than zero; leave it blank for the width the pads share.";
        if (ActiveSetupName is not { } name || C3dSetups.Read(Document).FirstOrDefault(s => s.Name == name) is not { Setup: { } current } item)
            return "No setup is active, so there is no component port to change.";
        var setup = EmSetupPersistence.Deserialize(EmSetupPersistence.Serialize(current));
        var list = setup.ComponentPorts.ToList();
        int at = list.FindIndex(c => c.Refdes.Equals(refdes, StringComparison.OrdinalIgnoreCase));
        var entry = (at >= 0 ? list[at] : new EmComponentPort(refdes)) with { Z0 = z0?.Trim(), WidthUm = widthUm };
        bool all = list.Any(c => c.Refdes == EmSetup.AllComponents);
        if (at >= 0)
        {
            if (entry.IsPlain && all) list.RemoveAt(at);
            else list[at] = entry;
        }
        else if (!entry.IsPlain) list.Add(entry);
        setup.ComponentPorts = list;
        ChangeRecords($"Component port {refdes}", d => d.Setups[item.Index] = EmSetupPersistence.ToEmbedded(setup));
        return null;
    }

    public void DeletePorts(IReadOnlyList<C3dPort> ports)
    {
        var set = ports.ToHashSet();
        Viewer.SetSelection([]);
        ChangeRecords(ports.Count == 1 ? $"Delete {C3dPorts.Label(ports[0])}" : $"Delete {ports.Count} ports",
                      d => d.Ports.RemoveAll(set.Contains));
    }

    /// <summary>
    /// R-em3d49-2d — Make Port on an axis-aligned face: the face's rectangle becomes the port's, on the face's plane. A
    /// face that is not a rectangle in an axis plane is refused, naming why. Returns the refusal, or null.
    /// </summary>
    public string? MakePortFromFace(uint objectId, int face, Em3dPortKind kind)
    {
        if (PortFromFace(objectId, face, kind, out var port, out string? referenceWhy) is { } why) return why;
        // A port made from a drawn sheet's surface REPLACES the sheet: the sheet and its material go, the port takes its place,
        // and the two are one undo entry. A face of a solid stays: the port is drawn on it.
        if (Viewer.Scene.Object(objectId) is { } o && SheetReplacedBy(o) is var sheet and >= 0)
        {
            string name = Document.Objects[sheet].Name;
            BeginGroup($"Make {name} a port");
            try
            {
                DeleteObjects([sheet]);
                AddPort(port!, referenceWhy, CurrentPortContext().Without(name));
            }
            finally { EndGroup(); }
            return null;
        }
        AddPort(port!, referenceWhy);
        return null;
    }

    /// <summary>The document index of the drawn sheet a port made from <paramref name="o"/>'s surface replaces, or −1 when
    /// <paramref name="o"/> is not a top-level document sheet (a solid's face, an instance's part).</summary>
    private int SheetReplacedBy(Scene3DObject o)
    {
        int i = DocumentIndex(o);
        return i >= 0 && i < Document.Objects.Count && Document.Objects[i] is C3dSheet s && s.Name == o.Name ? i : -1;
    }

    /// <summary>The port context a port made from <paramref name="o"/>'s face is judged in: without <paramref name="o"/> when
    /// the port replaces it (a sheet), else the scene's.</summary>
    private C3dPortContext PortContextFor(Scene3DObject o)
        => SheetReplacedBy(o) >= 0 ? CurrentPortContext().Without(o.Name) : CurrentPortContext();

    /// <summary>The port Make Port would add on a face, not added — or why the face cannot take one.</summary>
    private string? PortFromFace(uint objectId, int face, Em3dPortKind kind, out C3dPort? port, out string? referenceWhy)
    {
        port = null;
        referenceWhy = null;
        var scene = Viewer.Scene;
        if (scene.Object(objectId) is not { } o || face < 0) return "Select a face first.";
        var (area, normal) = Scene3DFaces.AreaAndNormal(scene, objectId, face);
        if (normal is not { } n) return $"Face {o.FaceName(face)} is curved: a port is a flat rectangle.";
        int axis = Math.Abs(n.X) > 0.999f ? 0 : Math.Abs(n.Y) > 0.999f ? 1 : Math.Abs(n.Z) > 0.999f ? 2 : -1;
        if (axis < 0) return $"Face {o.FaceName(face)} is not normal to x, y or z: a port lies on a drawing plane.";
        if (scene.FeaturesOf(objectId) is not { Table: { } t } fr || face >= t.FaceCount) return "That face's corners are not known.";
        var pts = Enumerable.Range(t.FaceVertexStart[face], t.FaceVertexStart[face + 1] - t.FaceVertexStart[face])
                            .Select(k => fr.Vertex(t.FaceVertices[k])).ToList();
        double per = C3dLowering.Metres(1, Document.DbuPerMicron);
        long D(double m) => (long)Math.Round(m / per, MidpointRounding.AwayFromZero);
        var plane = axis switch { 0 => C3dPlane.YZ, 1 => C3dPlane.XZ, _ => C3dPlane.XY };
        double Get(Point3 q, int a) => a == 0 ? q.X : a == 1 ? q.Y : q.Z;
        int ua = axis == 0 ? 1 : 0, va = axis == 2 ? 1 : 2;
        long u0 = D(pts.Min(q => Get(q, ua))), u1 = D(pts.Max(q => Get(q, ua)));
        long v0 = D(pts.Min(q => Get(q, va))), v1 = D(pts.Max(q => Get(q, va)));
        double rectArea = (u1 - u0) * per * ((v1 - v0) * per);
        if (u1 <= u0 || v1 <= v0 || Math.Abs(rectArea - area) > 1e-6 * rectArea)
        {
            // brief-em3d-121 — a wave port's rectangle is a region of the box's face, not the face itself: a flat face of any
            // other shape (a coax's dielectric end, a disc) gives its bounding rectangle
            if (kind != Em3dPortKind.Wave || WaveRegion(o, face, axis, ua, va, D) is not var (wu0, wv0, wu1, wv1) || wu1 <= wu0 || wv1 <= wv0)
                return $"Face {o.FaceName(face)} is not a rectangle: a port takes a rectangular face.";
            (u0, v0, u1, v1) = (wu0, wv0, wu1, wv1);
        }
        port = NewPortTemplate(kind);
        port.Plane = plane;
        port.Offset = D(Get(pts[0], axis));
        port.Rect = new C3dRect { Min = new C3dPoint2(u0, v0), Size = new C3dPoint2(u1 - u0, v1 - v0) };
        // brief-em3d-114 R-em3d114-3a — a box face met by three or more conductors: one terminal per conductor besides the
        // reference, numbered with the next free numbers in a stable order and named P<n>; the reference is written.
        if (C3dPorts.TerminalsFor(port, Document.DbuPerMicron, PortContextFor(o), out string? tie) is var (reference, why, conductors))
        {
            referenceWhy = why;
            var numbers = NextPortNumbers(conductors.Count);
            string z0 = port.Z0;
            port.Name = UniquePortName(BoxFaceKey(axis, port.Offset));
            port.Number = 0;
            port.Reference = reference;
            port.Terminals = [.. conductors.Select((c, k) => new C3dTerminal { Number = numbers[k], Name = $"P{numbers[k]}", Conductor = c, Z0 = z0 })];
            port.Z0 = "50";
        }
        else if (tie is not null) return tie;
        return null;
    }

    /// <summary>
    /// brief-em3d-121 — the bounding rectangle of a flat face that is not a rectangle, in DBU on the plane's u and v axes: a
    /// cylinder's cap exactly, from its axis and radius (its tessellation's polygon falls short of the circle), and any other
    /// face from its tessellation's corners.
    /// </summary>
    private (long U0, long V0, long U1, long V1)? WaveRegion(Scene3DObject o, int face, int axis, int ua, int va, Func<double, long> dbu)
    {
        double[] lo, hi;
        if (Elaboration?.Solids.FirstOrDefault(s => s.Name == o.Name)?.Primitive is Em3dCylinder c &&
            Math.Abs(C3dPortContext.Get(c.AxisEnd, ua) - C3dPortContext.Get(c.AxisStart, ua)) <= 1e-12 &&
            Math.Abs(C3dPortContext.Get(c.AxisEnd, va) - C3dPortContext.Get(c.AxisStart, va)) <= 1e-12)
        {
            var (x0, y0, z0, x1, y1, z1) = Em3dProblem.Bounds(c);
            (lo, hi) = ([x0, y0, z0], [x1, y1, z1]);
        }
        else
        {
            var corners = Scene3DFaces.Vertices(Viewer.Scene, o.Id, face);
            if (corners.Count < 3) return null;
            var origin = Viewer.Scene.Origin;
            double[] Of(System.Numerics.Vector3 q) => [q.X + origin.X, q.Y + origin.Y, q.Z + origin.Z];
            lo = [.. Enumerable.Range(0, 3).Select(a => corners.Min(q => Of(q)[a]))];
            hi = [.. Enumerable.Range(0, 3).Select(a => corners.Max(q => Of(q)[a]))];
        }
        return (dbu(lo[ua]), dbu(lo[va]), dbu(hi[ua]), dbu(hi[va]));
    }

    /// <summary>Which air-box face (<c>xmin</c> …) a face at <paramref name="offsetDbu"/> on <paramref name="axis"/> is, by the box
    /// the scene drew; the axis alone when there is none.</summary>
    private string BoxFaceKey(int axis, long offsetDbu)
    {
        string a = "xyz"[axis].ToString();
        if (ShownAirBox is not { } box) return a;
        double at = C3dLowering.Metres(offsetDbu, Document.DbuPerMicron);
        double lo = axis == 0 ? box.Min.X : axis == 1 ? box.Min.Y : box.Min.Z, hi = axis == 0 ? box.Max.X : axis == 1 ? box.Max.Y : box.Max.Z;
        return a + (Math.Abs(at - lo) <= Math.Abs(at - hi) ? "min" : "max");
    }

    /// <summary><paramref name="stem"/>, or <c>stem 2</c>, <c>stem 3</c> … — the first no port or terminal is named.</summary>
    private string UniquePortName(string stem)
    {
        var taken = Document.Ports.Select(p => p.Name).Concat(Document.Ports.SelectMany(p => p.Terminals ?? []).Select(t => t.Name))
                                  .ToHashSet(StringComparer.Ordinal);
        if (!taken.Contains(stem)) return stem;
        for (int k = 2; ; k++) if (!taken.Contains($"{stem} {k}")) return $"{stem} {k}";
    }

    /// <summary>
    /// 3D editor round 4 — one kind of Make Port on a face, offered only when the port it would add resolves. Which kind a
    /// face can take is decided by WHERE its rectangle lies, never by what it was drawn as: a wave port is a region of an
    /// air-box face (a sheet drawn there is the usual way to state one), and a lumped port bridges two conductors on
    /// opposite edges (a face of a small gap block does exactly that). So the resolver the run uses answers, and its
    /// refusal is the item's tip.
    /// </summary>
    private Viewer3DMenuItem MakePortItem(Scene3DItem item, Em3dPortKind kind)
    {
        string word = kind == Em3dPortKind.Wave ? "Wave" : "Lumped";
        string? why = PortFromFace(item.Object, item.Face, kind, out var port, out _)
                      ?? C3dPorts.Resolve(port!, Document.DbuPerMicron,
                                          Viewer.Scene.Object(item.Object) is { } o ? PortContextFor(o) : CurrentPortContext()).Refusal;
        string tip = kind == Em3dPortKind.Wave
            ? "A wave port lies on a face of the active setup's air box — the end of a line that reaches the box. A face met by " +
              "more than two conductors makes one terminal per conductor besides the reference, each its own port of the result."
            : "A lumped port bridges two conductors, one on each of two opposite edges of the face.";
        if (Viewer.Scene.Object(item.Object) is { } replaced && SheetReplacedBy(replaced) >= 0)
            tip += $" The port replaces '{replaced.Name}': the sheet and its material go.";
        return new Viewer3DMenuItem(word, () => Report(MakePortFromFace(item.Object, item.Face, kind)), Enabled: why is null,
                                    Tip: why ?? tip);
    }

    // ── the air box (R-em3d49-3) ─────────────────────────────────────────────────────────────

    /// <summary>The air-box face a scene object is (<c>xmin</c> … <c>zmax</c>), or null.</summary>
    public static string? BoxFaceOf(Scene3DObject o)
        => o.Kind == Scene3DKind.Boundary && o.Name.StartsWith("airbox/", StringComparison.Ordinal) ? o.Name["airbox/".Length..] : null;

    /// <summary>R-em3d49-3b — a box face's boundary, written to the ACTIVE setup's AirBox; null on success, else why not.</summary>
    public string? SetAirBoxBoundary(string face, Em3dBoundaryKind kind)
        => EditActiveAirBox(face, f => f is null ? new EmAirBoxFace(null, kind) : f with { Boundary = kind }, $"Air box {face}: {kind}");

    /// <summary>3D editor round 3 — the air box's material choices: the technology's materials, and Air and Vacuum (built in
    /// when the technology lacks them).</summary>
    public IReadOnlyList<string> AirBoxMaterialChoices
    {
        get
        {
            var list = Materials.ToList();
            foreach (string b in new[] { CircuitRF.Design.Layout.Em3d.Em3dGenerator.AirMaterial, C3dProblemAssembly.VacuumMaterial })
                if (!list.Contains(b, StringComparer.OrdinalIgnoreCase)) list.Add(b);
            return list;
        }
    }

    /// <summary>3D editor round 3 — the material that fills the air box (the document's, Air by default); one undo entry.
    /// Air writes nothing (it is the default). Null on success, else why not.</summary>
    public string? SetAirBoxMaterial(string material)
    {
        if (string.IsNullOrWhiteSpace(material)) return "Pick a material for the air box.";
        string? stored = string.Equals(material, CircuitRF.Design.Layout.Em3d.Em3dGenerator.AirMaterial, StringComparison.Ordinal) ? null : material;
        if (Document.AirBoxMaterial == stored) return null;
        ChangeRecords($"Air box material: {material}", d => d.AirBoxMaterial = stored);
        return null;
    }

    /// <summary>R-em3d49-3b — a box face's padding, typed in the display unit; null on success, else why not.</summary>
    public string? SetAirBoxPadding(string face, string text)
    {
        var d = C3dDimension.Parse(text, Document.DisplayUnit, Document.DbuPerMicron);
        if (d.Kind != C3dDimensionKind.Value) return d.Why;
        if (d.Dbu < 0) return "A padding is a distance from the geometry: zero or more.";
        double um = (double)LayoutUnits.FromDbu(d.Dbu, LayoutUnit.Um, Document.DbuPerMicron);
        return EditActiveAirBox(face, f => new EmAirBoxFace(um, f?.Boundary), $"Air box {face} padding");
    }

    /// <summary>The padding a box face has now, in the display unit — the Padding… field's prefill.</summary>
    public string AirBoxPaddingText(string face)
    {
        if (AirBoxPaddingM(face) is not { } m) return "";
        long dbu = (long)Math.Round(m / C3dLowering.Metres(1, Document.DbuPerMicron));
        return C3dDimension.Spell(dbu, Document.DisplayUnit, Document.DbuPerMicron);
    }

    /// <summary>brief-dfr32-1 R-dfr32-1-4 — a box face's distance from the modelled content now, metres; null with no box.</summary>
    public double? AirBoxPaddingM(string face)
    {
        if (ShownAirBox is not { } box || Elaboration?.ModelledExtent() is not { } x) return null;   // brief-em3d-93 — padded round what is modelled
        return face switch
        {
            "xmin" => x.X0 - box.Min.X, "xmax" => box.Max.X - x.X1, "ymin" => x.Y0 - box.Min.Y,
            "ymax" => box.Max.Y - x.Y1, "zmin" => x.Z0 - box.Min.Z, _ => box.Max.Z - x.Z1,
        };
    }

    /// <summary>The padding a face has now, whether stated or the default, as a percentage of the modelled extent along its
    /// axis: what the Inspector's percentage box shows when the setup states none. Null when the content is flat there.</summary>
    public double? AirBoxPaddingPercent(string face)
    {
        if (ShownAirBox is not { } box || Elaboration?.ModelledExtent() is not { } x) return null;
        var (pad, span) = face switch
        {
            "xmin" => (x.X0 - box.Min.X, x.X1 - x.X0), "xmax" => (box.Max.X - x.X1, x.X1 - x.X0),
            "ymin" => (x.Y0 - box.Min.Y, x.Y1 - x.Y0), "ymax" => (box.Max.Y - x.Y1, x.Y1 - x.Y0),
            "zmin" => (x.Z0 - box.Min.Z, x.Z1 - x.Z0), _ => (box.Max.Z - x.Z1, x.Z1 - x.Z0),
        };
        return span > 0 ? pad / span * 100 : null;
    }

    /// <summary>3D editor round 1 — a face's padding as a percentage of the content's extent along its axis (10 pads a tenth
    /// of the extent beyond that face); one undo entry. Null on success, else why not.</summary>
    public string? SetAirBoxPaddingPercent(string face, string text)
    {
        string t = text.Trim().TrimEnd('%').Trim();
        if (!NumericText.TryParseDouble(t, out double pct) || !double.IsFinite(pct))
            return $"'{text}' is not a percentage: a number such as 10.";
        if (pct < 0) return "A padding is a distance from the geometry: zero or more.";
        return EditActiveAirBox(face, f => new EmAirBoxFace(null, f?.Boundary, pct),
                                $"Air box {face} padding {pct.ToString("G6", CultureInfo.InvariantCulture)} %");
    }

    /// <summary>The active setup as the Inspector names it.</summary>
    public string ActiveSetupLabel => IsExternalActive ? $"{ExternalItemName} ({Path.GetFileName(ExternalSetup!.Value.Path)}, read-only here)"
                                                       : ActiveSetupName ?? "none";

    private string? EditActiveAirBox(string face, Func<EmAirBoxFace?, EmAirBoxFace> change, string description)
        => EditActiveAirBox([face], change, description);

    private string? EditActiveAirBox(IReadOnlyList<string> faces, Func<EmAirBoxFace?, EmAirBoxFace> change, string description)
    {
        if (IsExternalActive) return $"The active setup is the .cem {Path.GetFileName(ExternalSetup!.Value.Path)}: edit its air box in its own panel.";
        EmAirBox Changed(EmAirBox? box)
        {
            box ??= new EmAirBox();
            foreach (string face in faces)
                box = face switch
                {
                    "xmin" => box with { XMin = change(box.XMin) }, "xmax" => box with { XMax = change(box.XMax) },
                    "ymin" => box with { YMin = change(box.YMin) }, "ymax" => box with { YMax = change(box.YMax) },
                    "zmin" => box with { ZMin = change(box.ZMin) }, _ => box with { ZMax = change(box.ZMax) },
                };
            return box;
        }
        // No setup active: the document's own box, which a setup added later starts with.
        if (ActiveSetup is null)
        {
            var mine = EmSetupPersistence.ToCem(Changed(EmSetupPersistence.FromCem(Document.AirBox)));
            ChangeRecords(description, d => d.AirBox = mine);
            StatusMessage = "No setup is active: the air box is the document's, and a setup added now starts with it.";
            return null;
        }
        if (ActiveSetupName is not { } name || C3dSetups.Read(Document).FirstOrDefault(s => s.Name == name) is not { Setup: { } setup } read)
            return "The active setup cannot be read, so its air box cannot be edited.";
        setup.AirBox = Changed(setup.AirBox);
        ChangeRecords(description, d => d.Setups[read.Index] = EmSetupPersistence.ToEmbedded(setup));
        StatusMessage = $"Writes setup '{name}': the air box is the setup's, not the geometry's.";
        return null;
    }

    // ── face boundaries (R-em3d49-4) ─────────────────────────────────────────────────────────

    /// <summary>
    /// R-em3d49-4a — a boundary on face <paramref name="face"/> (the document's name) of object <paramref name="obj"/>;
    /// <paramref name="kind"/> null removes it. A conductor's face is refused, naming the object. Null on success.
    /// </summary>
    public string? SetFaceBoundary(string obj, string face, Em3dFaceBoundaryKind? kind, string? material = null)
    {
        if (Elaboration?.Solids.FirstOrDefault(s => s.Name == obj) is { Role: Em3dRole.Conductor })
            return $"'{obj}' is a conductor: it is already a void bounded by its own metal, so a boundary on its face has nothing to add.";
        if (kind is not null && Document.Objects.FirstOrDefault(o => o.Name == obj) is C3dSheet or C3dPolyline or null)
            return $"'{obj}' has no solid face for a boundary: put boundaries on dielectric and air objects.";
        string what = kind switch { null => "None", Em3dFaceBoundaryKind.Pec => "Perfect Conductor", _ => $"Conductive ({material})" };
        ChangeRecords($"Boundary on {obj} {face}: {what}", d =>
        {
            d.FaceBoundaries.RemoveAll(b => b.Object == obj && b.Face == face);
            if (kind is { } k) d.FaceBoundaries.Add(new C3dFaceBoundary { Object = obj, Face = face, Kind = k, Material = k == Em3dFaceBoundaryKind.Conductive ? material : null });
        });
        return null;
    }

    /// <summary>The metals a Conductive face may be: the technology's materials that conduct.</summary>
    public IReadOnlyList<string> Metals
        => Elaboration?.Technology?.ResolvedMaterials.Where(m => m.Sigma20 is > 0).Select(m => m.Name).ToList() ?? (IReadOnlyList<string>)[];

    // ── the context menu (R-em3d49-2, -3b, -4) ───────────────────────────────────────────────

    /// <summary>One port's Z0, Number and Name, each asked for as text — the canvas menu's and the Object Tree row's.
    /// A wave port with terminals numbers each terminal instead, so it has no single number to change.</summary>
    internal IEnumerable<Viewer3DMenuItem> PortValueItems(C3dPort p)
    {
        if (!C3dPorts.HasTerminals(p))
        {
            yield return new Viewer3DMenuItem($"Z0… ({p.Z0} Ω)", () => TextRequested?.Invoke($"Z0 of {C3dPorts.Label(p)}",
                "Reference impedance, Ω (a complex one as 25+j10):", p.Z0, text => SetPortZ0(p.Number, text)));
            string number = p.Number.ToString(CultureInfo.InvariantCulture);
            yield return new Viewer3DMenuItem($"Number… ({number})", () => TextRequested?.Invoke($"Number of {C3dPorts.Label(p)}",
                "Port number (a whole number from 1, not used by another port):", number, text => SetPortNumber(p, text)));
        }
        yield return new Viewer3DMenuItem($"Name… ({C3dPorts.Label(p)})", () => TextRequested?.Invoke($"Name of {C3dPorts.Label(p)}",
            "Port name:", p.Name, text => SetPortName(p, text)));
    }

    private IEnumerable<Viewer3DMenuItem> SimulateMenuItems()
    {
        var sel = Viewer.Selection;
        if (Viewer.SelectMode == Scene3DSelectMode.Object && SelectedPorts() is { Count: > 0 } ports)
        {
            // brief-em3d-114 R-em3d114-3d — a multi-terminal port: its reference, and each selected terminal's own Flip and Z0
            if (ports is [{ Terminals: { Count: > 0 } } multi])
            {
                foreach (var m in TerminalPortItems(multi)) yield return m;
                yield return Viewer3DMenuItem.Separator;
                yield break;
            }
            yield return new Viewer3DMenuItem(ports.Count == 1 ? $"Flip {C3dPorts.Label(ports[0])}" : "Flip Ports", () => FlipPorts(ports),
                                              Tip: "Swap the port's + and − ends: the arrow turns round.");
            yield return new Viewer3DMenuItem("Port Kind", Children:
            [
                new Viewer3DMenuItem("Lumped", () => SetPortKind(ports, Em3dPortKind.Lumped)),
                new Viewer3DMenuItem("Wave", () => SetPortKind(ports, Em3dPortKind.Wave),
                                     Tip: "A wave port lies on a face of the active setup's air box."),
            ]);
            yield return PortModelItem(ports);                  // brief-em3d-93
            if (ports.Count == 1)
                foreach (var m in PortValueItems(ports[0])) yield return m;
            yield return new Viewer3DMenuItem(ports.Count == 1 ? $"Delete {C3dPorts.Label(ports[0])}" : "Delete Ports", () => DeletePorts(ports));
            yield return Viewer3DMenuItem.Separator;
        }
        // A drawn sheet selected in Object mode: its one face is the port, as in Face mode.
        if (Viewer.SelectMode == Scene3DSelectMode.Object && sel.Count == 1 && SelectedPorts().Count == 0 &&
            Viewer.Scene.Object(sel[0].Object) is { } sheetObj && SheetReplacedBy(sheetObj) >= 0)
        {
            var surface = sel[0] with { Face = 0 };
            yield return new Viewer3DMenuItem("Make Port", Children: [MakePortItem(surface, Em3dPortKind.Lumped), MakePortItem(surface, Em3dPortKind.Wave)]);
            yield return Viewer3DMenuItem.Separator;
            yield break;
        }
        if (Viewer.SelectMode != Scene3DSelectMode.Face || sel.Count != 1 || sel[0].Face < 0) yield break;
        var item = sel[0];
        if (Viewer.Scene.Object(item.Object) is not { } o) yield break;

        if (BoxFaceOf(o) is { } boxFace)
        {
            var current = ShownAirBox is { } bx ? FaceKindOf(bx, boxFace) : Em3dBoundaryKind.Absorbing;
            string tip = ActiveSetup is not null && ActiveSetupName is { } n ? $"Writes setup '{n}': the air box is the setup's."
                       : "No setup is active: writes the 3D view's own air box, which a setup added later starts with.";
            yield return new Viewer3DMenuItem("Boundary", Tip: tip, Children:
            [
                .. new[] { Em3dBoundaryKind.Absorbing, Em3dBoundaryKind.Pec, Em3dBoundaryKind.Pmc, Em3dBoundaryKind.Symmetry }
                    .Select(k => new Viewer3DMenuItem((k == current ? "● " : "") + k switch
                    {
                        Em3dBoundaryKind.Absorbing => "Absorbing", Em3dBoundaryKind.Pec => "PEC", Em3dBoundaryKind.Pmc => "PMC", _ => "Symmetry",
                    }, () => Report(SetAirBoxBoundary(boxFace, k)), Tip: tip)),
            ]);
            yield return new Viewer3DMenuItem("Padding…", () => TextRequested?.Invoke($"Air box {boxFace} padding",
                $"Distance from the geometry to the {boxFace} face ({LayoutUnits.Suffix(Document.DisplayUnit)}):", AirBoxPaddingText(boxFace),
                text => SetAirBoxPadding(boxFace, text)), Tip: tip);
            yield return Viewer3DMenuItem.Separator;
            yield break;
        }
        if (InstanceOf(o) is not null || o.Kind == Scene3DKind.Port) yield break;
        string faceName = o.FaceName(item.Face);
        bool conductor = Elaboration?.Solids.FirstOrDefault(s => s.Name == o.Name) is { Role: Em3dRole.Conductor };
        bool solid = Document.Objects.FirstOrDefault(d => d.Name == o.Name) is not (C3dSheet or C3dPolyline or null);
        var existing = Document.FaceBoundaries.FirstOrDefault(b => b.Object == o.Name && b.Face == faceName);
        if (solid)
        {
            string? why = conductor ? $"'{o.Name}' is a conductor: a boundary on its face has nothing to add." : null;
            var metals = Metals;
            yield return new Viewer3DMenuItem("Boundary", Enabled: why is null, Tip: why, Children:
            [
                new Viewer3DMenuItem((existing?.Kind == Em3dFaceBoundaryKind.Pec ? "● " : "") + "Perfect Conductor",
                                     () => Report(SetFaceBoundary(o.Name, faceName, Em3dFaceBoundaryKind.Pec))),
                new Viewer3DMenuItem((existing?.Kind == Em3dFaceBoundaryKind.Conductive ? "● " : "") + "Conductive Surface",
                                     Enabled: metals.Count > 0, Tip: metals.Count > 0 ? null : "The technology defines no conducting material.",
                                     Children: [.. metals.Select(m => new Viewer3DMenuItem((existing?.Material == m ? "● " : "") + m,
                                         () => Report(SetFaceBoundary(o.Name, faceName, Em3dFaceBoundaryKind.Conductive, m))))]),
                new Viewer3DMenuItem((existing is null ? "● " : "") + "None", () => Report(SetFaceBoundary(o.Name, faceName, null))),
            ]);
        }
        yield return new Viewer3DMenuItem("Make Port", Children: [MakePortItem(item, Em3dPortKind.Lumped), MakePortItem(item, Em3dPortKind.Wave)]);
        yield return Viewer3DMenuItem.Separator;
    }

    /// <summary>
    /// brief-em3d-114 R-em3d114-3d — a multi-terminal port's items: Set Reference (the conductors meeting its region; a terminal's
    /// conductor swaps places with the reference), each selected terminal's Flip and Z0, its Model and Delete.
    /// </summary>
    private IEnumerable<Viewer3DMenuItem> TerminalPortItems(C3dPort port)
    {
        var result = PortResults.FirstOrDefault(r => r.Port == port);
        string? current = result?.Resolved?.NegativeObject ?? port.Reference;
        var region = result?.Contacts.FirstOrDefault(c => c.Edge == "region").Objects ?? [];
        yield return new Viewer3DMenuItem("Set Reference", Enabled: region.Count > 0,
            Tip: region.Count > 0 ? "The conductor every terminal's voltage is measured from. Choosing a terminal's conductor swaps the two."
                                  : result?.Refusal ?? "No conductor meets the port's region.",
            Children: [.. region.Select(c => new Viewer3DMenuItem((c == current ? "● " : "") + c, () => Report(SetPortReference(port, c))))]);
        foreach (var (p, t) in SelectedTerminals().Where(x => x.Port == port))
        {
            yield return new Viewer3DMenuItem($"Flip {t.Name}", () => FlipTerminal(p, t),
                                              Tip: "Reverse the terminal's voltage path: its arrow turns round, and the reference stays the reference.");
            yield return new Viewer3DMenuItem($"Z0… of {t.Name} ({t.Z0} Ω)", () => TextRequested?.Invoke($"Z0 of {C3dPorts.TerminalLabel(p, t)}",
                "Reference impedance, Ω (a complex one as 25+j10):", t.Z0, text => SetPortZ0(t.Number, text)));
        }
        yield return PortModelItem([port]);
        yield return new Viewer3DMenuItem($"Delete {C3dPorts.Label(port)}", () => DeletePorts([port]));
    }

    private static Em3dBoundaryKind FaceKindOf(Em3dAirBox b, string face) => face switch
    {
        "xmin" => b.Faces.XMin, "xmax" => b.Faces.XMax, "ymin" => b.Faces.YMin,
        "ymax" => b.Faces.YMax, "zmin" => b.Faces.ZMin, _ => b.Faces.ZMax,
    };

    private void Report(string? refusal)
    {
        if (refusal is not null) StatusMessage = refusal;
    }

    /// <summary>A text the view should ask for: title, prompt, the prefill, and the commit (null on success, else why not).</summary>
    public event Action<string, string, string, Func<string, string?>>? TextRequested;

    // ── the overlay: numbers, refusals, the port tool's live inference ───────────────────────

    /// <summary>Ports whose row is unticked — the view's alone, as a thermal place's tick is (a port has no Hidden in its file).
    /// Kept by name rather than read only off the scene, because a REFUSED port has no scene object to hide.</summary>
    private readonly HashSet<string> _hiddenPorts = new(StringComparer.Ordinal);

    /// <summary>Whether the port named <paramref name="problemName"/> is drawn at all — its sheet, its number, its refusal and its
    /// red outline: its row is ticked, and its sheet (when it has one) was not hidden in the view (H, Isolate).</summary>
    public bool IsPortShown(string problemName)
        => !_hiddenPorts.Contains(problemName) && (SceneObject(problemName) is not { } s || Viewer.View.IsVisible(s.Id));

    /// <summary>A port row's tick: its sheet, and everything the overlay draws for it, shown or hidden for this session.</summary>
    private void SetPortShown(string problemName, bool shown)
    {
        if (shown) _hiddenPorts.Remove(problemName); else _hiddenPorts.Add(problemName);
        Viewer.RequestFrame();
    }

    /// <summary>After an adoption: a hidden port's sheet stays hidden — including one that was refused (no sheet) when it was
    /// unticked and resolves now.</summary>
    private void ApplyHiddenPorts()
    {
        if (_hiddenPorts.Count == 0) return;
        Viewer.SetVisibleEverywhere(_hiddenPorts.Select(SceneObject).OfType<Scene3DObject>().Select(s => (s.Id, false)).ToList());
    }

    private void FillSimulateOverlay(Viewer3DDrawOverlay overlay)
    {
        int dbu = Document.DbuPerMicron;
        // The realistic view's Look hides the ports (their sheets, in the frame plan): their numbers, refdes and refused outlines
        // go with them, as an unticked port's do. A port being drawn still shows what it would join.
        if (!(Viewer.View.DrawsRealistic && !Viewer.View.Look.Shows(CircuitRF.Render.Scene3D.Look.Scene3DChrome.Ports))) FillPortLabels(overlay, dbu);
        if (_tool is PortTool pt && pt.Preview(CursorInput()) is { } draft) FillPortPreview(overlay, draft, dbu);
    }

    /// <summary>Each port's number (a terminal's at its arrow), each component port's refdes, and a refused port's outline.</summary>
    private void FillPortLabels(Viewer3DDrawOverlay overlay, int dbu)
    {
        // Each component port's refdes at its sheet, as a port's number is at its own.
        foreach (var (part, result) in ComponentPortResults)
            if (result?.Resolved is { } cp && IsPortShown(cp.Name))
                overlay.Labels.Add((Centre(cp.Min, cp.Max), part.Port?.Name ?? part.Refdes));
        foreach (var r in PortResults)
        {
            // A port unticked shows nothing of itself: no number, no refusal, no red outline.
            if (!C3dPorts.Numbers(r.Port).Any(n => IsPortShown(C3dPorts.ProblemName(n)))) continue;
            // brief-em3d-114 R-em3d114-3c — each terminal's number at the head of its arrow (the scene draws the arrow, reference
            // to conductor, in the port colour)
            if (r.Terminals is { } ts)
            {
                foreach (var t in ts.Where(t => IsPortShown(t.Name) && t.VoltagePath is not null))
                    overlay.Labels.Add((t.VoltagePath!.Value.To, t.Number.ToString(CultureInfo.InvariantCulture)));
                continue;
            }
            if (r.Resolved is { } p)
            {
                overlay.Labels.Add((Centre(p.Min, p.Max), r.Port.Number.ToString(CultureInfo.InvariantCulture)));
                continue;
            }
            var (min, max) = Rectangle(r.Port, dbu);
            AddRect(overlay.Crossing, r.Port, dbu);
            overlay.Labels.Add((Centre(min, max), $"{C3dPorts.Label(r.Port)}: refused"));
        }
    }

    /// <summary>The port tool's preview: the port it would add, its + and − ends, or why it cannot be one.</summary>
    private void FillPortPreview(Viewer3DDrawOverlay overlay, C3dPort draft, int dbu)
    {
        {
            var r = C3dPorts.Resolve(draft, dbu, CurrentPortContext());
            var (min, max) = Rectangle(draft, dbu);
            if (r.Resolved is { } p)
            {
                var c = Centre(min, max);
                double side = Math.Max(max.X - min.X, Math.Max(max.Y - min.Y, max.Z - min.Z)) * 0.3;
                var d = p.Direction;
                var from = new Point3(c.X - d.X * side, c.Y - d.Y * side, c.Z - d.Z * side);
                var to = new Point3(c.X + d.X * side, c.Y + d.Y * side, c.Z + d.Z * side);
                overlay.Rubber.Add(new Render.Scene3D.Edit.DrawSegment(from, to));
                overlay.Labels.Add((to, $"+ {p.PositiveObject}"));
                overlay.Labels.Add((from, $"− {p.NegativeObject}"));
            }
            else overlay.Labels.Add((Centre(min, max), r.Refusal ?? ""));
        }
    }

    private static Point3 Centre(Point3 a, Point3 b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2, (a.Z + b.Z) / 2);

    private static (Point3 Min, Point3 Max) Rectangle(C3dPort p, int dbu)
    {
        double M(long v) => C3dLowering.Metres(v, dbu);
        var a = C3dLowering.OnPlane(p.Plane, M(p.Rect.Min.U), M(p.Rect.Min.V), M(p.Offset));
        var b = C3dLowering.OnPlane(p.Plane, M(p.Rect.Min.U + p.Rect.Size.U), M(p.Rect.Min.V + p.Rect.Size.V), M(p.Offset));
        return (new Point3(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z)), new Point3(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z)));
    }

    private static void AddRect(List<Render.Scene3D.Edit.DrawSegment> into, C3dPort p, int dbu)
    {
        double M(long v) => C3dLowering.Metres(v, dbu);
        long u0 = p.Rect.Min.U, v0 = p.Rect.Min.V, u1 = u0 + p.Rect.Size.U, v1 = v0 + p.Rect.Size.V;
        var c = new[] { (u0, v0), (u1, v0), (u1, v1), (u0, v1) }
            .Select(q => C3dLowering.OnPlane(p.Plane, M(q.Item1), M(q.Item2), M(p.Offset))).ToArray();
        for (int k = 0; k < 4; k++) into.Add(new Render.Scene3D.Edit.DrawSegment(c[k], c[(k + 1) % 4]));
    }

    // ── the tree: the ports, and each object's face boundaries (R-em3d49-4d) ─────────────────

    private void RebuildRecordsTree()
    {
        // brief-em3d-101 — the record rows are thrown away and made again, and the tree view drops a removed row from its
        // selection (raising an empty one): the rows selected before are found again by name afterwards, as RebuildTree does.
        // Without this an Inspector edit of a selected face image (Hidden above all) lost its row once the scene was adopted.
        var keep = _selectedTreeItems.ToList();
        bool outer = _syncingTree;
        _syncingTree = true;
        try { RebuildRecordRows(); }
        finally { _syncingTree = outer; }
        if (outer || keep.Count == 0) return;
        var all = AllTreeItems().ToHashSet();
        List<C3dTreeItem> rows = [.. keep.Select(r => all.Contains(r) ? r : RebuiltRow(r)).OfType<C3dTreeItem>().Distinct()];
        if (rows.SequenceEqual(_selectedTreeItems)) return;
        _syncingTree = true;
        try { SetTreeRows(rows); }
        finally { _syncingTree = false; }
        Properties.Reload();
    }

    private void RebuildRecordRows()
    {
        foreach (var g in Tree.Where(g => g.Role is C3dTreeGroupRole.Ports or C3dTreeGroupRole.ComponentPorts).ToList())
        {
            DetachExpansion([g]);
            Tree.Remove(g);
        }
        RebuildAirBoxItem();
        // brief-em3d-93 — a port that is off is greyed; by type it lists under Not Modeled, as every other kind does
        foreach (var g in Tree.Where(g => g.Role == C3dTreeGroupRole.NotModelled).ToList())
        {
            foreach (var old in g.Items.Where(i => i.Kind == "Port").ToList()) g.Items.Remove(old);
            if (g.Items.Count == 0) { DetachExpansion([g]); Tree.Remove(g); }
        }
        var rows = PortResults.Select(r => (Off: !r.Port.Model, Row: C3dPorts.HasTerminals(r.Port) ? TerminalPortRow(r) : new C3dTreeItem(this, C3dPorts.ProblemName(r.Port.Number), "Port",
            r.Resolved is { } p ? $"{C3dPorts.Label(r.Port)} {(p.Kind == Em3dPortKind.Wave ? "wave" : "lumped")}: {p.NegativeObject} → {p.PositiveObject}" +
                                  (r.Port.Model ? "" : C3dModelled.Suffix)
                                : $"{C3dPorts.Label(r.Port)}: {(r.Port.Model ? "refused" : "not modelled")}", -1, -1,
                                  IsPortShown(C3dPorts.ProblemName(r.Port.Number)))
            { IsReadOnly = true, IsModelled = r.Port.Model })).ToList();
        bool byType = TreeGrouping == C3dTreeGrouping.Primitive;
        var ports = rows.Where(t => !(byType && t.Off)).Select(t => t.Row).ToList();
        if (ports.Count > 0) Tree.Add(new C3dTreeGroup("Ports", ports, C3dTreeGroupRole.Ports));
        // The setup's component ports, after the document's own: one row per part, its port or why it is left out.
        var parts = ComponentPortResults.Select(t => t.Result?.Resolved is { } cp
            ? new C3dTreeItem(this, cp.Name, ComponentPortKind,
                  $"port {t.Part.Port!.Number}, lumped: {cp.NegativeObject} → {cp.PositiveObject}", -1, -1, IsPortShown(cp.Name))
              { IsReadOnly = true, DisplayName = t.Part.Port.Name }
            : new C3dTreeItem(this, "component/" + t.Part.Refdes + "/" + (t.Part.Pin ?? ""), ComponentPortKind,
                  $"{t.Part.Refdes}: left out. {t.Part.Refusal ?? t.Result?.Refusal}", -1, -1, false)
              { IsReadOnly = true, DisplayName = t.Part.Refdes, Refusal = t.Part.Refusal ?? t.Result?.Refusal }).ToList();
        if (parts.Count > 0) Tree.Add(new C3dTreeGroup("Component ports", parts, C3dTreeGroupRole.ComponentPorts));
        if (byType && rows.Where(t => t.Off).Select(t => t.Row).ToList() is { Count: > 0 } offPorts)
        {
            if (Tree.FirstOrDefault(g => g.Role == C3dTreeGroupRole.NotModelled) is { } home)
                foreach (var r in offPorts) home.Items.Add(r);
            else
            {
                // after the type groups (and the Groups section), before Instances
                int at = Tree.Select((g, i) => (g, i)).LastOrDefault(t => t.g.Role is C3dTreeGroupRole.Groups or C3dTreeGroupRole.Objects
                                                                                        or C3dTreeGroupRole.Construction or C3dTreeGroupRole.Booleans).i + 1;
                if (!Tree.Any(g => g.Role is C3dTreeGroupRole.Groups or C3dTreeGroupRole.Objects or C3dTreeGroupRole.Construction or C3dTreeGroupRole.Booleans)) at = 0;
                Tree.Insert(at, new C3dTreeGroup(NotModeledHeader, offPorts, C3dTreeGroupRole.NotModelled) { HeaderTip = C3dModelled.Tip });
            }
        }
        RebuildLumpedTree();                   // brief-sar-3
        RebuildThermalTree();
        foreach (var item in Tree.Where(g => g.Role is C3dTreeGroupRole.Objects or C3dTreeGroupRole.Construction or C3dTreeGroupRole.Booleans
                                                  or C3dTreeGroupRole.NotModelled).SelectMany(g => g.Items))
        {
            foreach (var c in item.Children.Where(c => c.Kind is "Boundary" or FaceImageKind).ToList()) item.Children.Remove(c);
            foreach (var b in Document.FaceBoundaries.Where(b => b.Object == item.Name))
                item.Children.Add(new C3dTreeItem(this, Scene3DBuilder.FaceTintPrefix + b.Object + "/" + b.Face, "Boundary",
                    $"{b.Face}: {(b.Kind == Em3dFaceBoundaryKind.Conductive ? $"Conductive ({b.Material})" : b.Kind.ToString())}", -1, -1, true)
                    { IsReadOnly = true });
            // brief-em3d-101 R-em3d101-9c — each face image beneath its object: "Image on zmax — die.png", its tick its Hidden.
            if (item.ObjectIndex >= 0 && item.OperandPath is null && !item.IsFeature && item.ObjectIndex < Document.Objects.Count
                && Document.Objects[item.ObjectIndex] is { FaceImages: { Count: > 0 } faceImages } host)
                foreach (var fi in faceImages)
                    item.Children.Add(new C3dTreeItem(this, FaceImageSceneName(host.Name, fi.Face), FaceImageKind, C3dImages.FileName(fi.Image), -1, -1, !fi.Hidden)
                    {
                        DisplayName = $"Image on {fi.Face}", FaceImageHost = item.ObjectIndex, FaceImageFace = fi.Face,
                        Icon = Material.Icons.MaterialIconKind.ImageOutline, Refusal = FaceImageProblem(host.Name, fi),
                    });
        }
        RestoreExpansion();
    }

    /// <summary>brief-em3d-114 — the name of a multi-terminal port's own row (its terminals' rows are their scene objects').</summary>
    public const string PortGroupRowPrefix = "ports/";

    /// <summary>The document port a tree row stands for: a port's row, a multi-terminal port's row, or one of its terminals'.</summary>
    internal C3dPort? PortOfRow(C3dTreeItem row)
    {
        if (row.Kind == TerminalKind || (row.Kind == "Port" && !row.Name.StartsWith(PortGroupRowPrefix, StringComparison.Ordinal)))
            return int.TryParse(row.Name.AsSpan(row.Name.IndexOf('/') + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
                ? C3dPorts.OwnerOf(Document, n) : null;
        return row.Kind == "Port" && int.TryParse(row.Name.AsSpan(PortGroupRowPrefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out int g)
            ? C3dPorts.OwnerOf(Document, g) : null;
    }

    /// <summary>A terminal's row, beneath its port's.</summary>
    public const string TerminalKind = "Terminal";

    /// <summary>
    /// brief-em3d-114 R-em3d114-3b — a multi-terminal port is ONE row, its tick the whole port's (overview D5), with a row per
    /// terminal beneath it: number, name, conductor and Z0. Each terminal row is its sheet's, so selecting it selects the
    /// terminal, whose Flip and Z0 the context menu edits.
    /// </summary>
    private C3dTreeItem TerminalPortRow(C3dPortResult r)
    {
        var port = r.Port;
        var numbers = C3dPorts.Numbers(port);
        bool shown = numbers.Any(n => IsPortShown(C3dPorts.ProblemName(n)));
        string detail = r.Resolved is { } p
            ? $"{C3dPorts.Label(port)} wave, {numbers.Count} terminals, reference {p.NegativeObject}" + (port.Model ? "" : C3dModelled.Suffix)
            : $"{C3dPorts.Label(port)}: {(port.Model ? "refused" : "not modelled")}";
        var row = new C3dTreeItem(this, PortGroupRowPrefix + numbers.DefaultIfEmpty(0).Min().ToString(CultureInfo.InvariantCulture), "Port", detail, -1, -1, shown)
        { IsReadOnly = true, IsModelled = port.Model };
        foreach (var t in port.Terminals!)
            row.Children.Add(new C3dTreeItem(this, C3dPorts.ProblemName(t.Number), TerminalKind,
                $"{t.Number} {t.Name}: {t.Conductor}, {t.Z0} Ω{(t.Flip ? ", flipped" : "")}", -1, -1, IsPortShown(C3dPorts.ProblemName(t.Number)))
            { IsReadOnly = true, IsModelled = port.Model });
        return row;
    }

    /// <summary>
    /// 3D editor round 1 — the active setup's air box, first under Boxes: selectable (the Properties Inspector shows its
    /// boundaries and padding), never deletable or duplicable — it is the setup's, not the geometry's. Its tick is the
    /// toolbar's air-box switch. Round 2: by material there is no Boxes group to share. Round 3: by material it is listed
    /// under the material that fills it (<see cref="CircuitRF.Design.Layout.Em3d.Em3dGenerator.AirMaterial"/>, beside any object of that material,
    /// in the material groups' name order), and it is listed whenever the document has content — with no active setup
    /// too, when there is no box to draw yet and its row says so: a run always solves in one, and a tree that left it
    /// out until a setup existed read as "there is no air box".
    /// </summary>
    private void RebuildAirBoxItem()
    {
        // 3D editor round 3 — listed when a box is drawn (with no setup, once a solid or a sheet exists), and with a setup
        // whose box could not be built, so its row can say so. Headed, by material, by what fills it (Air by default).
        bool want = ShownAirBox is not null || (ActiveSetup is not null && (Document.Objects.Count > 0 || Document.Instances.Count > 0));
        string detail = ActiveSetup is null ? "no setup yet"
                      : $"setup {(IsExternalActive ? ExternalItemName : ActiveSetupName)}{(ShownAirBox is null ? ": not built" : "")}";
        string air = C3dProblemAssembly.BoxFill(Document);
        if (Tree.FirstOrDefault(g => g.Items.Any(i => i.IsAirBox)) is { } home)
        {
            var old = home.Items.First(i => i.IsAirBox);
            if (want && old.Detail == detail && (TreeGrouping == C3dTreeGrouping.Primitive || string.Equals(home.Header, air, StringComparison.OrdinalIgnoreCase)))
            {
                old.Sync(AirBoxShown);
                return;
            }
            home.Items.Remove(old);
            if (home.Items.Count == 0) { DetachExpansion([home]); Tree.Remove(home); }
        }
        if (!want) return;
        var item = new C3dTreeItem(this, AirBoxName, C3dTreeItem.AirBoxKind, detail, -1, -1, AirBoxShown) { IsReadOnly = true };
        if (TreeGrouping == C3dTreeGrouping.Primitive)
        {
            if (Tree.FirstOrDefault(g => g.Role == C3dTreeGroupRole.Objects && g.Header == "Boxes") is { } boxes) boxes.Items.Insert(0, item);
            else Tree.Insert(0, new C3dTreeGroup("Boxes", [item]));
            return;
        }
        if (Tree.FirstOrDefault(g => g.Role == C3dTreeGroupRole.Objects && string.Equals(g.Header, air, StringComparison.OrdinalIgnoreCase)) is { } shared)
        {
            shared.Items.Insert(0, item);
            return;
        }
        // Its own group, where the material groups' name order puts it ("No material" leads them).
        int at = 0;
        while (at < Tree.Count && Tree[at].Role == C3dTreeGroupRole.Objects &&
               (Tree[at].Header == NoMaterialHeader || string.Compare(Tree[at].Header, air, StringComparison.OrdinalIgnoreCase) < 0))
            at++;
        Tree.Insert(at, new C3dTreeGroup(air, [item], C3dTreeGroupRole.AirBox));
    }

    /// <summary>The air box's name in the tree and in the scene (its faces are <c>airbox/xmin</c> …).</summary>
    public const string AirBoxName = "airbox";

    /// <summary>The scene objects that are the air box's faces.</summary>
    public IReadOnlyList<Scene3DObject> AirBoxFaceObjects()
        => [.. Viewer.Scene.Objects.Where(o => BoxFaceOf(o) is not null)];

    // ── fields: from the run's own directory, and a banner when the model has moved on (R-em3d49-5b) ──

    /// <summary>The file a run's directory keeps the document it solved in — what the stale-fields banner compares with.</summary>
    public const string RunDocumentFile = C3dRunDocument.FileName;

    [ObservableProperty] private string? _fieldsStaleText;

    /// <summary>The shell's results root (the workspace's <c>results</c> folder, or the session's).</summary>
    public Func<string?>? ResultsRootProvider { get; set; }

    /// <summary>The run directories the active setup's run writes, first Palace's.</summary>
    private IEnumerable<string> ActiveRunDirectories() => RunDirectoriesOf(ActiveRunSetup);

    /// <summary>The directories setup <paramref name="s"/>'s runs keep their results in (a thermal setup's own; a Palace and/or an
    /// openEMS one).</summary>
    /// brief-em3d-98 — THE list, C3dSolveStatus.RunDirectories, so the editor, the glyphs and the CLI agree on where a setup's
    /// results live.
    private IEnumerable<string> RunDirectoriesOf(EmSetup? s)
        => C3dSolveStatus.RunDirectories(s, ResultsRootProvider?.Invoke()).Select(x => x.Directory);

    /// <summary>A run finished: the fields are read again. The run service kept the document it solved and every file it read
    /// beside its result (brief-em3d-87 — in <c>src/Design</c>, so a <c>circuitrf em</c> run keeps the same record).</summary>
    public void RunFinished()
    {
        ForgetDiscoveries();                       // brief-em3d-83 — every plot's check reads the runs again
        Viewer.SetRunSetup(ActiveRunSetup);
        RefreshFieldsStale();
        RefreshSolveStatusNow();                   // brief-em3d-98 — whichever setup ran, active or not
    }

    /// <summary>R-em3d49-5b — the banner: fields from a run whose document differs from the one being edited now. They are
    /// still shown — the solver's own geometry, never re-mapped onto a model it did not see.</summary>
    public void RefreshFieldsStale()
    {
        FieldsStaleText = StaleText(ActiveRunSetup, IsExternalActive ? "" : ActiveSetupName);
        ScheduleSolveStatus();                     // brief-em3d-98 — every edit path comes through here
    }

    /// <summary>brief-em3d-83 — the banner for plot <paramref name="p"/>: ITS setup's run, which need not be the active one (a plot
    /// is pinned to a setup). Read with the active setup's, a plot of T2's out-of-date temperatures showed no warning while T1
    /// was active, and a current plot was called stale because T1's run was old.</summary>
    public string? FieldPlotStaleText(C3dFieldPlot p)
    {
        var request = PlotRequest(p, resolveScene: false);
        return StaleText(request.RunSetup, request.SetupName);
    }

    /// <summary>Why setup <paramref name="setup"/>'s result is stale — its run solved a different model, or a file it was solved
    /// from (a placed layout, a nested 3D view, the technology, a material library, a circuit-driven thermal run's schematic and
    /// its sub-cells) has changed since (brief-em3d-87) — or null. Each file is re-hashed only when it changes on disk.</summary>
    /// <para>brief-em3d-98 — compared as <paramref name="embeddedName"/>'s run sees the document, so another setup's edit
    /// never makes this one's fields stale.</para>
    private string? StaleText(EmSetup? setup, string? embeddedName)
    {
        foreach (string dir in RunDirectoriesOf(setup))
        {
            if (C3dRunDocument.Check(dir, Document, TopFilePath, embeddedName) is not { } check) continue;
            // brief-em3d-83 R-em3d83-2 — the plots are display: SerializeForRun leaves them out on both sides.
            return check.What is { } what
                ? $"Fields are from the run at {check.Written:HH:mm}; {what} {check.Has} changed since. They are drawn on the " +
                  "geometry that run solved."
                : null;
        }
        return null;
    }
}

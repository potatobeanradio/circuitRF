using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// One row of the Instances panel — a component of one schematic or layout, at its top level or,
/// with "Include sub-cells" on, inside a cell placed there.
///
/// <para><b>Holds the OBJECT, never an index</b> (brief-find-instance-panel.md §2). A layout selects
/// instances by index into <c>LayoutView.Instances</c>, and that index shifts on any delete, so the
/// index is looked up at double-click time. The view model the row came from is held too, so a row
/// double-clicked after the user moved to another tab still acts on ITS document.</para>
/// </summary>
public sealed class InstanceRow
{
    internal InstanceRow(string name, string type, string detail, object source, object sourceViewModel)
    {
        Name            = name;
        Type            = type;
        Detail          = detail;
        Source          = source;
        SourceViewModel = sourceViewModel;
        NameKey         = name.ToLowerInvariant();
    }

    /// <summary>The instance name (schematic) or designator (layout) — a dotted path
    /// (<c>X1.X3.R5</c>) for one found inside a placed cell.</summary>
    public string Name { get; }

    /// <summary>What it is — the component type, the kit part, the PCell generator or the cell.</summary>
    public string Type { get; }

    /// <summary>The dimmed Cell/Part column; empty where it would only repeat <see cref="Type"/>.</summary>
    public string Detail { get; }

    public bool HasDetail => Detail.Length > 0;

    /// <summary>An <see cref="EditableComponent"/> or a <see cref="LayoutInstance"/> of the listed
    /// frame, or a <see cref="SubCellInstance"/> naming one inside a placed cell.</summary>
    public object Source { get; }

    /// <summary>The <see cref="SchematicViewModel"/> or <see cref="LayoutEditorViewModel"/> listed —
    /// for a <see cref="SubCellInstance"/>, the frame its path starts from.</summary>
    public object SourceViewModel { get; }

    public bool IsInSubCell => Source is SubCellInstance;

    /// <summary>Lower-cased once at rebuild, so a keystroke compares and never allocates per row.</summary>
    internal string NameKey { get; }
}

/// <summary>
/// The Instances panel (brief-find-instance-panel.md): the top-level component instances of the
/// focused <c>.csch</c> or <c>.clay</c>, filtered by name and by type — and, with
/// <see cref="IncludeSubCells"/> on, every instance inside the cells placed there.
///
/// <para><b>Include sub-cells lists the TAB, not the frame.</b> Off, the panel lists what the canvas
/// shows, so a push-in re-points it. On, it lists from the document's top frame down, and a push-in
/// changes nothing — which is what lets a double-click on <c>X1.X3.R5</c> push down to R5 without the
/// list it came from being replaced by X3's contents mid-search.</para>
///
/// <para><b>The sub-cell walk never runs on the UI thread</b>
/// (<see cref="InstanceHierarchyWalk"/>). The top level is listed at once, exactly as with the box
/// off; the rows below it arrive when the walk does, already sorted, and are merged in. A newer
/// rebuild cancels an older walk, and a result that arrives after that is dropped.</para>
///
/// <para><b>Rows are rebuilt from the model, never tracked per edit</b> (R-fi-7). A document switch
/// rebuilds at once; the model's own change event rebuilds after <see cref="DebounceDelay"/> of
/// quiet, so a drag rebuilds once when it ends rather than on every tick; a layout change that
/// touched only shapes rebuilds nothing; and nothing at all is done while the panel is not on
/// screen — it rebuilds when it next appears.</para>
///
/// <para><b>Filtering never reads the model</b> (R-fi-8). It scans an array of row records built
/// at the last rebuild, and a keystroke that NARROWS the text (appends to it) scans only the rows
/// the previous keystroke kept — which is what <see cref="LastFilterScanned"/> counts.</para>
/// </summary>
public sealed partial class InstanceListViewModel : ObservableObject
{
    public const string AllTypes = "All types";

    public const string NoDocumentText = "Open a schematic or layout to list its instances.";

    public const string SearchingText = "Searching sub-cells…";

    /// <summary>R-fi-7's "~150 ms".</summary>
    public static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(150);

    // What the workspace says is focused: the frame on the canvas, and the tab's top frame.
    private object? _activeVm;
    private string? _activeName;
    private object? _rootVm;
    private string? _rootName;

    private object? _documentVm;   // the frame listed — the active one, or the root with sub-cells on
    private object? _followedVm;   // with sub-cells on, the active frame too: edits happen there
    private InstanceRow[] _all = [];
    private bool _stale;
    private bool _shown;
    private IDisposable? _pendingRebuild;

    // What the rows on screen were filtered by, so the next keystroke can tell whether it narrows.
    private string? _lastNeedle;
    private string? _lastType;

    // The two halves of _all: the listed frame's own rows, and what the sub-cell walk found.
    private InstanceRow[] _topRows = [];
    private InstanceRow[] _subCellRows = [];
    private string[] _subCellTypes = [];
    private bool _truncated;

    private CancellationTokenSource? _walkCts;
    private int _walkGeneration;
    private readonly DiskLevelCache _diskCache = new();

    [ObservableProperty] private string _headerLabel = "Instances";
    [ObservableProperty] private bool _hasDocument;
    [ObservableProperty] private IReadOnlyList<InstanceRow> _visibleRows = [];
    [ObservableProperty] private IReadOnlyList<string> _typeOptions = [AllTypes];
    [ObservableProperty] private string _selectedType = AllTypes;
    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private InstanceRow? _selectedRow;
    [ObservableProperty] private string _countText = "";
    [ObservableProperty] private string _emptyStateText = NoDocumentText;
    [ObservableProperty] private bool _showEmptyState = true;
    [ObservableProperty] private string? _countToolTip;

    /// <summary>List the instances inside placed cells too, found off the UI thread.</summary>
    [ObservableProperty] private bool _includeSubCells;

    /// <summary>True while a sub-cell walk is running.</summary>
    [ObservableProperty] private bool _isSearchingSubCells;

    /// <summary>
    /// Raised by <see cref="Activate"/> for a row whose instance still exists. The workspace answers
    /// it — it alone can bring the row's document forward — through <see cref="InstanceReveal"/>.
    /// </summary>
    public event Action<InstanceRow>? InstanceActivated;

    /// <summary>Starts the debounce: a one-shot callback after the delay, cancelled by disposing
    /// what it returns. A dispatcher timer by default; replaced by tests, which drive time by hand.</summary>
    internal Func<TimeSpan, Action, IDisposable> Schedule { get; set; } = DispatcherOneShot;

    /// <summary>Where the sub-cell walk runs — the thread pool; tests run it inline.</summary>
    internal Action<Action> RunInBackground { get; set; } = work => Task.Run(work);

    /// <summary>How the walk's result gets back to the UI thread; tests run it inline.</summary>
    internal Action<Action> PostToUi { get; set; } = work => Dispatcher.UIThread.Post(work);

    /// <summary>
    /// A copy of every OPEN cell's placements, by absolute path, taken on the UI thread when a walk
    /// starts — an unsaved edit in an open sub-cell is what that cell holds, and the walk may not read
    /// a live model from its own thread. Supplied by the workspace, which alone knows the sessions.
    /// </summary>
    internal Func<IReadOnlyDictionary<string, InstanceHierarchyWalk.Level>>? LiveLevels { get; set; }

    /// <summary>The cells the sub-cell search read from disk; re-read only when a file changes.</summary>
    internal DiskLevelCache DiskCache => _diskCache;

    // ── Counters (R-fi-7/R-fi-8 are gated on these, never on a timing) ────────

    /// <summary>How many times the rows have been rebuilt from a model.</summary>
    public int RebuildCount { get; private set; }

    /// <summary>How many rows the most recent filter pass looked at.</summary>
    public int LastFilterScanned { get; private set; }

    /// <summary>The number of rows the last rebuild produced, before filtering.</summary>
    public int TotalRows => _all.Length;

    /// <summary>The view model being listed — a <see cref="SchematicViewModel"/>, a
    /// <see cref="LayoutEditorViewModel"/>, or null.</summary>
    public object? DocumentViewModel => _documentVm;

    // ── Following the focused document ───────────────────────────────────────

    /// <summary>
    /// Points the panel at <paramref name="documentViewModel"/> — a <see cref="SchematicViewModel"/>
    /// or a <see cref="LayoutEditorViewModel"/>; anything else (including null) empties it, because
    /// one document's list beside another document is worse than none (R-fi-3). Called from every
    /// fan-out that follows the focused document, some of them per keystroke, so a call that changes
    /// nothing returns at once.
    ///
    /// <para><paramref name="rootViewModel"/> is the tab's TOP frame when the canvas is pushed into a
    /// sub-cell — what <see cref="IncludeSubCells"/> lists from. Omitted, it is the same frame.</para>
    /// </summary>
    public void SetActiveDocument(object? documentViewModel, string? displayName,
                                  object? rootViewModel = null, string? rootDisplayName = null)
    {
        if (documentViewModel is not (SchematicViewModel or LayoutEditorViewModel)) documentViewModel = rootViewModel = null;
        if (rootViewModel is not (SchematicViewModel or LayoutEditorViewModel))
        {
            rootViewModel   = documentViewModel;
            rootDisplayName = displayName;
        }

        _activeVm   = documentViewModel;
        _activeName = displayName;
        _rootVm     = rootViewModel;
        _rootName   = rootDisplayName;
        Retarget();
    }

    /// <summary>Lists the frame <see cref="IncludeSubCells"/> says to, and follows the edits that can
    /// change it. Returns whether the listed frame changed (and so has been, or will be, rebuilt).</summary>
    private bool Retarget()
    {
        object? listed = IncludeSubCells ? _rootVm : _activeVm;
        string? name   = IncludeSubCells ? _rootName : _activeName;
        object? follow = IncludeSubCells && !ReferenceEquals(_activeVm, _rootVm) ? _activeVm : null;

        HeaderLabel = listed is null || string.IsNullOrEmpty(name) ? "Instances" : name;

        if (!ReferenceEquals(follow, _followedVm))
        {
            Unsubscribe(_followedVm);
            _followedVm = follow;
            Subscribe(_followedVm);
        }

        if (ReferenceEquals(listed, _documentVm)) return false;   // a rename, or nothing at all

        Unsubscribe(_documentVm);
        _documentVm = listed;
        HasDocument = listed is not null;
        Subscribe(_documentVm);

        CancelPendingRebuild();
        _stale = true;
        if (_shown || listed is null) Rebuild();
        return true;
    }

    partial void OnIncludeSubCellsChanged(bool value)
    {
        if (Retarget()) return;

        // The same frame either way: only the rows below it come or go.
        CancelPendingRebuild();
        _stale = true;
        if (_shown) Rebuild();
    }

    /// <summary>Rebuilds now — the workspace's answer to a sub-cell row whose instance it could not
    /// find again, since the list was plainly out of date.</summary>
    public void Refresh()
    {
        CancelPendingRebuild();
        _stale = true;
        if (_shown || _documentVm is null) Rebuild();
    }

    /// <summary>Whether the panel is on screen. Nothing is rebuilt while it is not; the first time it
    /// is shown again, a list that went stale meanwhile is rebuilt at once.</summary>
    public void SetShown(bool shown)
    {
        if (_shown == shown) return;
        _shown = shown;

        if (!shown)
        {
            CancelPendingRebuild();
            if (CancelWalk()) _stale = true;   // half a walk is no list; finish it when shown again
            return;
        }
        if (_stale) Rebuild();
    }

    private void Subscribe(object? vm)
    {
        switch (vm)
        {
            case SchematicViewModel s:     s.EditModel.Changed += OnSchematicChanged; break;
            case LayoutEditorViewModel l:  l.Model.Changed     += OnLayoutChanged;    break;
        }
    }

    private void Unsubscribe(object? vm)
    {
        switch (vm)
        {
            case SchematicViewModel s:     s.EditModel.Changed -= OnSchematicChanged; break;
            case LayoutEditorViewModel l:  l.Model.Changed     -= OnLayoutChanged;    break;
        }
    }

    private void OnSchematicChanged(object? sender, EventArgs e) => ModelChanged();

    // A shapes-only change cannot add, remove or rename an instance. Full is the "anything may have
    // changed" default every unclassified command falls back to, so it must rebuild.
    private void OnLayoutChanged(object? sender, LayoutChangeInfo info)
    {
        if (info.Kind is LayoutChangeKind.InstancesChanged or LayoutChangeKind.Full) ModelChanged();
    }

    private void ModelChanged()
    {
        _stale = true;
        if (!_shown) return;

        // Restarted on every change: the rebuild runs once the model has been quiet for the delay.
        CancelPendingRebuild();
        _pendingRebuild = Schedule(DebounceDelay, () =>
        {
            _pendingRebuild = null;
            if (_stale && _shown) Rebuild();
        });
    }

    private void CancelPendingRebuild()
    {
        _pendingRebuild?.Dispose();
        _pendingRebuild = null;
    }

    // ── Rebuild ───────────────────────────────────────────────────────────────

    private void Rebuild()
    {
        _stale = false;
        RebuildCount++;
        CancelWalk();

        var rows = _documentVm switch
        {
            SchematicViewModel s    => SchematicRows(s),
            LayoutEditorViewModel l => LayoutRows(l),
            _                       => [],
        };
        Array.Sort(rows, RowOrder);

        _topRows      = rows;
        _subCellRows  = [];
        _subCellTypes = [];
        _truncated    = false;

        if (IncludeSubCells) StartWalk();
        PublishRows();
    }

    /// <summary>Natural name order (R10 after R2), then type.</summary>
    private static readonly Comparison<InstanceRow> RowOrder = static (a, b) =>
    {
        int c = SchematicSortPlacement.NaturalNameComparer.Instance.Compare(a.Name, b.Name);
        return c != 0 ? c : string.CompareOrdinal(a.Type, b.Type);
    };

    /// <summary>
    /// Starts the sub-cell walk for the listed frame. What it reads of any live model is copied HERE,
    /// on the UI thread; the walk itself, its sort and its type list run on the pool.
    /// </summary>
    private void StartWalk()
    {
        var (root, rootPath) = _documentVm switch
        {
            SchematicViewModel s    => (InstanceHierarchyWalk.Of(s.EditModel), (string?)null),
            LayoutEditorViewModel l => (InstanceHierarchyWalk.Of(l.Model, l.InstanceBaseDir), l.CurrentLayoutPath),
            _                       => (null, null),
        };
        if (root is null || string.IsNullOrEmpty(root.BaseDir) || !root.Entries.Any(e => e.CellRef is not null))
            return;   // no placed cell to look inside, and a scratch document resolves none

        var live      = LiveLevels?.Invoke() ?? new Dictionary<string, InstanceHierarchyWalk.Level>();
        var sourceVm  = _documentVm!;
        var cache     = _diskCache;
        var cts       = new CancellationTokenSource();
        int gen       = ++_walkGeneration;
        _walkCts      = cts;
        IsSearchingSubCells = true;

        RunInBackground(() =>
        {
            InstanceHierarchyWalk.Result? result;
            string[] types = [];
            try
            {
                result = InstanceHierarchyWalk.Walk(root, rootPath, sourceVm, live, cache, cts.Token);
                Array.Sort(result.Rows, RowOrder);
                types = [.. result.Rows.Select(r => r.Type).Distinct(StringComparer.Ordinal)];
            }
            catch (OperationCanceledException) { return; }
            catch { result = null; }   // a list without its sub-cells beats a panel that throws

            PostToUi(() =>
            {
                if (gen != _walkGeneration) return;   // superseded, or cancelled after it finished
                _walkCts = null;
                IsSearchingSubCells = false;
                if (result is null) return;

                _subCellRows  = result.Rows;
                _subCellTypes = types;
                _truncated    = result.Truncated;
                PublishRows();
            });
        });
    }

    /// <summary>Stops a running walk and drops whatever it would have posted. Returns whether one was
    /// running.</summary>
    private bool CancelWalk()
    {
        _walkGeneration++;
        IsSearchingSubCells = false;
        if (_walkCts is not { } cts) return false;
        cts.Cancel();
        _walkCts = null;
        return true;
    }

    /// <summary>Both halves, each already sorted, merged into <c>_all</c>; the type list; the filter.</summary>
    private void PublishRows()
    {
        _all = _subCellRows.Length == 0 ? _topRows : Merge(_topRows, _subCellRows);

        var types = new List<string>(16) { AllTypes };
        types.AddRange(_topRows.Select(r => r.Type).Concat(_subCellTypes).Distinct(StringComparer.Ordinal)
                               .OrderBy(t => t, SchematicSortPlacement.NaturalNameComparer.Instance));
        if (!TypeOptions.SequenceEqual(types, StringComparer.Ordinal)) TypeOptions = types;
        if (!types.Contains(SelectedType, StringComparer.Ordinal)) SelectedType = AllTypes;

        _lastNeedle = null;   // new rows: the next filter pass may not narrow from the old ones
        ApplyFilter();
    }

    private static InstanceRow[] Merge(InstanceRow[] a, InstanceRow[] b)
    {
        var merged = new InstanceRow[a.Length + b.Length];
        int i = 0, j = 0, k = 0;
        while (i < a.Length && j < b.Length)
            merged[k++] = RowOrder(a[i], b[j]) <= 0 ? a[i++] : b[j++];
        while (i < a.Length) merged[k++] = a[i++];
        while (j < b.Length) merged[k++] = b[j++];
        return merged;
    }

    /// <summary>The rows of a schematic — <see cref="InstanceHierarchyWalk.Of(SchematicEditModel)"/>'s
    /// entries, each holding its component.</summary>
    internal static InstanceRow[] SchematicRows(SchematicViewModel vm)
    {
        var comps = vm.EditModel.Components;
        var level = InstanceHierarchyWalk.Of(vm.EditModel);
        return [.. level.Entries.Select(e => new InstanceRow(e.Name, e.Type, e.Detail, comps[e.Index], vm))];
    }

    /// <summary>The one predicate R-fi-4 asks for: everything but Ground and the annotation symbols.</summary>
    internal static bool IsListed(SymbolKind symbol)
        => symbol != SymbolKind.Ground && !SchematicComponent.IsAnnotationSymbol(symbol);

    /// <summary>The rows of a layout — <see cref="InstanceHierarchyWalk.Of(LayoutView, string?)"/>'s
    /// entries, each holding its instance.</summary>
    internal static InstanceRow[] LayoutRows(LayoutEditorViewModel vm)
    {
        var insts = vm.Model.Instances;
        var level = InstanceHierarchyWalk.Of(vm.Model, vm.InstanceBaseDir);
        return [.. level.Entries.Select(e => new InstanceRow(e.Name, e.Type, e.Detail, insts[e.Index], vm))];
    }

    // ── Filter ────────────────────────────────────────────────────────────────

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    partial void OnIsSearchingSubCellsChanged(bool value) => RefreshEmptyState();

    partial void OnSelectedTypeChanged(string value)
    {
        // The picker can hand back null while its items are being replaced.
        if (value is null) { SelectedType = AllTypes; return; }
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        string needle = (FilterText ?? "").Trim().ToLowerInvariant();
        string? type  = SelectedType is null or AllTypes ? null : SelectedType;

        // Narrowing: the same type, and text that CONTAINS the last text — every row that matches now
        // matched then, so only the rows already showing need looking at.
        IReadOnlyList<InstanceRow> source =
            _lastNeedle is not null && type == _lastType && needle.Contains(_lastNeedle, StringComparison.Ordinal)
                ? VisibleRows
                : _all;

        var kept = new List<InstanceRow>(source.Count);
        foreach (var r in source)
        {
            if (type is not null && !string.Equals(r.Type, type, StringComparison.Ordinal)) continue;
            if (needle.Length > 0 && !r.NameKey.Contains(needle, StringComparison.Ordinal)) continue;
            kept.Add(r);
        }

        LastFilterScanned = source.Count;
        _lastNeedle = needle;
        _lastType   = type;

        var keepSelection = SelectedRow;
        VisibleRows = kept;
        SelectedRow = keepSelection is not null && kept.Contains(keepSelection) ? keepSelection : null;

        string more = _truncated ? "+" : "";
        CountText = _all.Length == 0 ? ""
                  : kept.Count == _all.Length ? $"{_all.Length:N0}{more}"
                  : $"{kept.Count:N0} of {_all.Length:N0}{more}";
        CountToolTip = _truncated
            ? $"The sub-cell search stopped at {InstanceHierarchyWalk.MaxRows:N0} instances; narrow the search by pushing into a cell."
            : null;
        RefreshEmptyState();
    }

    private void RefreshEmptyState()
    {
        const string topLevel =
            "Only the top level is listed — turn on Include sub-cells to search inside placed cells.";

        string scope = IsSearchingSubCells ? SearchingText
                     : IncludeSubCells     ? "Sub-cells included."
                     :                       topLevel;

        EmptyStateText = !HasDocument     ? NoDocumentText
                       : _all.Length == 0 ? "No component instances here. " + scope
                       :                    "No instance matches. " + scope;
        ShowEmptyState = VisibleRows.Count == 0;
    }

    // ── Double-click / Enter ──────────────────────────────────────────────────

    /// <summary>
    /// R-fi-9/R-fi-10's double-click, and R-fi-11's Enter. A row whose instance has gone since the
    /// last rebuild (the debounce had not fired yet) rebuilds the list and does nothing else — the
    /// brief's "rebuild and say nothing". Returns whether the instance was found.
    /// </summary>
    public bool Activate(InstanceRow? row)
    {
        if (row is null) return false;

        if (!StillExists(row))
        {
            CancelPendingRebuild();
            Rebuild();
            return false;
        }

        InstanceActivated?.Invoke(row);
        return true;
    }

    /// <summary>
    /// Whether the row's instance is still there. For one inside a sub-cell that is asked of the
    /// placement its path starts from — the only part of the path in a model the list watches; the
    /// rest is checked on the way down, by the workspace, which says so if it is gone.
    /// </summary>
    internal static bool StillExists(InstanceRow row) => (row.SourceViewModel, row.Source) switch
    {
        (SchematicViewModel s, EditableComponent c) => s.EditModel.Components.Contains(c),
        (LayoutEditorViewModel l, LayoutInstance i) => l.Model.Instances.Contains(i),
        (SchematicViewModel s, SubCellInstance p)   => s.EditModel.Components.Any(c => InstanceHierarchyWalk.NameOf(c) == p.Chain[0].Name),
        (LayoutEditorViewModel l, SubCellInstance p) => l.Model.Instances.Any(i => InstanceHierarchyWalk.NameOf(i) == p.Chain[0].Name),
        _                                           => false,
    };

    // ── Default scheduler ─────────────────────────────────────────────────────

    private static IDisposable DispatcherOneShot(TimeSpan delay, Action action)
    {
        var timer = new DispatcherTimer { Interval = delay };
        timer.Tick += (_, _) => { timer.Stop(); action(); };
        timer.Start();
        return new Cancel(timer.Stop);
    }

    private sealed class Cancel(Action stop) : IDisposable
    {
        public void Dispose() => stop();
    }
}

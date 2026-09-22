using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// One row of the Instances panel — a top-level component of one schematic or layout.
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

    /// <summary>The instance name (schematic) or designator (layout).</summary>
    public string Name { get; }

    /// <summary>What it is — the component type, the kit part, the PCell generator or the cell.</summary>
    public string Type { get; }

    /// <summary>The dimmed Cell/Part column; empty where it would only repeat <see cref="Type"/>.</summary>
    public string Detail { get; }

    public bool HasDetail => Detail.Length > 0;

    /// <summary>An <see cref="EditableComponent"/> or a <see cref="LayoutInstance"/>.</summary>
    public object Source { get; }

    /// <summary>The <see cref="SchematicViewModel"/> or <see cref="LayoutEditorViewModel"/> listed.</summary>
    public object SourceViewModel { get; }

    /// <summary>Lower-cased once at rebuild, so a keystroke compares and never allocates per row.</summary>
    internal string NameKey { get; }
}

/// <summary>
/// The Instances panel (brief-find-instance-panel.md): the top-level component instances of the
/// focused <c>.csch</c> or <c>.clay</c>, filtered by name and by type.
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

    /// <summary>R-fi-7's "~150 ms".</summary>
    public static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(150);

    private object? _documentVm;
    private string? _displayName;
    private InstanceRow[] _all = [];
    private bool _stale;
    private bool _shown;
    private IDisposable? _pendingRebuild;

    // What the rows on screen were filtered by, so the next keystroke can tell whether it narrows.
    private string? _lastNeedle;
    private string? _lastType;

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

    /// <summary>
    /// Raised by <see cref="Activate"/> for a row whose instance still exists. The workspace answers
    /// it — it alone can bring the row's document forward — through <see cref="InstanceReveal"/>.
    /// </summary>
    public event Action<InstanceRow>? InstanceActivated;

    /// <summary>Starts the debounce: a one-shot callback after the delay, cancelled by disposing
    /// what it returns. A dispatcher timer by default; replaced by tests, which drive time by hand.</summary>
    internal Func<TimeSpan, Action, IDisposable> Schedule { get; set; } = DispatcherOneShot;

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
    /// </summary>
    public void SetActiveDocument(object? documentViewModel, string? displayName)
    {
        if (documentViewModel is not (SchematicViewModel or LayoutEditorViewModel)) documentViewModel = null;
        if (ReferenceEquals(documentViewModel, _documentVm) && displayName == _displayName) return;

        _displayName = displayName;
        HeaderLabel  = documentViewModel is null ? "Instances"
                     : string.IsNullOrEmpty(displayName) ? "Instances" : displayName;

        if (ReferenceEquals(documentViewModel, _documentVm)) return;   // a rename only

        Unsubscribe();
        _documentVm = documentViewModel;
        HasDocument = documentViewModel is not null;
        Subscribe();

        CancelPendingRebuild();
        _stale = true;
        if (_shown || documentViewModel is null) Rebuild();
    }

    /// <summary>Whether the panel is on screen. Nothing is rebuilt while it is not; the first time it
    /// is shown again, a list that went stale meanwhile is rebuilt at once.</summary>
    public void SetShown(bool shown)
    {
        if (_shown == shown) return;
        _shown = shown;

        if (!shown) { CancelPendingRebuild(); return; }
        if (_stale) Rebuild();
    }

    private void Subscribe()
    {
        switch (_documentVm)
        {
            case SchematicViewModel s:     s.EditModel.Changed += OnSchematicChanged; break;
            case LayoutEditorViewModel l:  l.Model.Changed     += OnLayoutChanged;    break;
        }
    }

    private void Unsubscribe()
    {
        switch (_documentVm)
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

        var rows = _documentVm switch
        {
            SchematicViewModel s    => SchematicRows(s),
            LayoutEditorViewModel l => LayoutRows(l),
            _                       => [],
        };

        Array.Sort(rows, static (a, b) =>
        {
            int c = SchematicSortPlacement.NaturalNameComparer.Instance.Compare(a.Name, b.Name);
            return c != 0 ? c : string.CompareOrdinal(a.Type, b.Type);
        });
        _all = rows;

        var types = new List<string>(rows.Length == 0 ? 1 : 16) { AllTypes };
        types.AddRange(rows.Select(r => r.Type).Distinct(StringComparer.Ordinal)
                           .OrderBy(t => t, SchematicSortPlacement.NaturalNameComparer.Instance));
        if (!TypeOptions.SequenceEqual(types, StringComparer.Ordinal)) TypeOptions = types;
        if (!types.Contains(SelectedType, StringComparer.Ordinal)) SelectedType = AllTypes;

        _lastNeedle = null;   // new rows: the next filter pass may not narrow from the old ones
        ApplyFilter();
    }

    /// <summary>
    /// The rows of a schematic. <b>Ground, VAR and MEAS are left out</b> (R-fi-4): they are not what
    /// anyone is looking for, and dozens of grounds would bury the parts.
    /// </summary>
    internal static InstanceRow[] SchematicRows(SchematicViewModel vm)
    {
        var comps = vm.EditModel.Components;
        var rows  = new List<InstanceRow>(comps.Count);

        foreach (var c in comps)
        {
            if (!IsListed(c.Symbol)) continue;

            string type, detail = "";
            if (PdkKitRegistry.TryParse(c.CellRef, out string kit, out string part))
            {
                type   = part;
                detail = kit;
            }
            else
            {
                type = c.TypeLabelText();
                if (c.CellRef is { Length: > 0 } cellRef)
                {
                    string spelled = cellRef.Replace('\\', '/').TrimEnd('/');
                    if (!string.Equals(spelled, type, StringComparison.Ordinal)) detail = spelled;
                }
                else if (c.Footprint is { Length: > 0 } fp)
                    detail = fp;
            }

            rows.Add(new InstanceRow(c.InstanceName.Length > 0 ? c.InstanceName : "(unnamed)",
                                     type, detail, c, vm));
        }

        return [.. rows];
    }

    /// <summary>The one predicate R-fi-4 asks for: everything but Ground and the annotation symbols.</summary>
    internal static bool IsListed(SymbolKind symbol)
        => symbol != SymbolKind.Ground && !SchematicComponent.IsAnnotationSymbol(symbol);

    /// <summary>
    /// The rows of a layout. The name is <see cref="LayoutInstance.DisplayRefDes"/> — never
    /// <c>RefDes</c>, whose own comment says why — and the cell's name for an unnamed placement.
    /// </summary>
    internal static InstanceRow[] LayoutRows(LayoutEditorViewModel vm)
    {
        var model = vm.Model;
        var insts = model.Instances;
        var rows  = new InstanceRow[insts.Count];

        for (int i = 0; i < insts.Count; i++)
        {
            var inst     = insts[i];
            string cell  = CellName(inst.CellRef);

            string? generator = model.PCellSnapshots.TryGetValue(cell, out var snap) ? snap.GeneratorId : null;
            string? partKind  = LayoutPartKind.Of(inst) is { } kind ? ComponentTypeRegistry.DisplayName(kind) : null;
            string type       = generator ?? partKind ?? cell;

            string detail = string.Equals(type, cell, StringComparison.Ordinal) ? "" : cell;
            if (inst.Rows * inst.Cols > 1)
                detail = (detail.Length > 0 ? detail + " · " : "") + $"{inst.Rows}×{inst.Cols} array";

            rows[i] = new InstanceRow(inst.DisplayRefDes ?? cell, type, detail, inst, vm);
        }

        return rows;
    }

    private static string CellName(string cellRef)
    {
        string trimmed = cellRef.TrimEnd('/', '\\');
        int cut = trimmed.LastIndexOfAny(['/', '\\']);
        return cut >= 0 ? trimmed[(cut + 1)..] : trimmed;
    }

    // ── Filter ────────────────────────────────────────────────────────────────

    partial void OnFilterTextChanged(string value) => ApplyFilter();

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

        CountText = _all.Length == 0 ? ""
                  : kept.Count == _all.Length ? $"{_all.Length:N0}"
                  : $"{kept.Count:N0} of {_all.Length:N0}";
        RefreshEmptyState();
    }

    private void RefreshEmptyState()
    {
        const string topLevel =
            "Only the top level is listed — instances inside a placed cell are that cell's own; push into it to list them.";

        EmptyStateText = !HasDocument     ? NoDocumentText
                       : _all.Length == 0 ? "No component instances here. " + topLevel
                       :                    "No instance matches. " + topLevel;
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

    internal static bool StillExists(InstanceRow row) => (row.SourceViewModel, row.Source) switch
    {
        (SchematicViewModel s, EditableComponent c) => s.EditModel.Components.Contains(c),
        (LayoutEditorViewModel l, LayoutInstance i) => l.Model.Instances.Contains(i),
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

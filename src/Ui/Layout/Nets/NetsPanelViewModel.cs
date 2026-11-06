// The Nets panel (brief-ipx-1 R-ipx1-1, R-ipx1-2).
//
// The list is LayoutNets.Of's rows, the function `explain --nets` prints, so the two cannot disagree. It is built once per document
// change, on a posted job so a burst of changes builds it once and never under the RenderLock Model.Changed holds. Moving through
// rows reads what that one build kept: a row's shapes by name, and each net's highlight, whose merged paths are built on first draw.

using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia.Threading;
using CircuitRF.Design.Layout;
using CircuitRF.Render;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.Layout.Nets;

public sealed partial class NetsPanelViewModel : ObservableObject
{
    private LayoutNetList? _list;
    private readonly Dictionary<string, LayoutNetHighlight> _highlights = new(System.StringComparer.Ordinal);
    private readonly List<LayoutNetRow> _selected = [];
    private bool _isStale = true, _buildPosted, _isShown, _replacingRows;

    /// <summary>How a build is scheduled after a document change. A test runs it where it can see it.</summary>
    internal System.Action<System.Action> PostBuild { get; set; } = job => Dispatcher.UIThread.Post(job);

    /// <summary>How many times the list has been built. A move through the rows adds none.</summary>
    internal int ListBuilds { get; private set; }

    [ObservableProperty] private LayoutEditorViewModel? _editor;

    /// <summary>The rows the filter lets through. Replaced whole: a board can have thousands, and the list virtualizes them.</summary>
    [ObservableProperty] private IReadOnlyList<LayoutNetRow> _rows = [];

    /// <summary>Narrows the list by substring, ignoring case.</summary>
    [ObservableProperty] private string _filterText = "";

    /// <summary>Draw everything but the selected nets at reduced opacity.</summary>
    [ObservableProperty] private bool _dimOtherCopper;

    public bool IsLayoutActive => Editor is not null;

    /// <summary>The rows selected in the list.</summary>
    public IReadOnlyList<LayoutNetRow> SelectedRows => _selected;

    public bool HasSelection => _selected.Count > 0;

    /// <summary>How many nets, and what the counts leave out.</summary>
    public string HeaderText
    {
        get
        {
            if (_list is not { } list) return "";
            if (list.Rows.Count == 0) return list.Scope ?? "";
            string count = Rows.Count == list.Rows.Count
                ? $"{list.Rows.Count:N0} {(list.Rows.Count == 1 ? "net" : "nets")}"
                : $"{Rows.Count:N0} of {list.Rows.Count:N0} nets";
            return list.Scope is { } scope ? $"{count}. {scope}" : count;
        }
    }

    /// <summary>What stands where the list would, so an empty panel never looks broken. Empty when there are rows.</summary>
    public string EmptyText =>
        Editor is null ? "Open a layout document to see its nets."
        : _list is null or { Rows.Count: 0 } ? LayoutNets.NoNamedNets
        : Rows.Count == 0 ? "No net matches this filter."
        : "";

    public bool HasEmptyText => EmptyText.Length > 0;

    /// <summary>Asks the view to select these rows in the list: what was selected, after the list was replaced.</summary>
    public event System.Action<IReadOnlyList<LayoutNetRow>>? SelectRowsRequested;

    /// <summary>Set by the view as it is attached and detached. A closed panel leaves no outline on the canvas, and costs no
    /// list build for edits nobody is looking at.</summary>
    public bool IsShown
    {
        get => _isShown;
        set
        {
            if (_isShown == value) return;
            _isShown = value;
            if (value && _isStale) Build();
            else PushHighlight();
        }
    }

    public void SetEditor(LayoutEditorViewModel? vm)
    {
        if (ReferenceEquals(vm, Editor)) return;
        Editor = vm;
    }

    partial void OnEditorChanged(LayoutEditorViewModel? oldValue, LayoutEditorViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.Model.Changed -= OnModelChanged;
            oldValue.PropertyChanged -= OnEditorPropertyChanged;
            oldValue.SetNetHighlights([], false);
        }
        if (newValue is not null)
        {
            newValue.Model.Changed += OnModelChanged;
            newValue.PropertyChanged += OnEditorPropertyChanged;
        }

        _list = null;
        _selected.Clear();
        _isStale = true;
        if (_isShown || newValue is null) Build();
        OnPropertyChanged(nameof(IsLayoutActive));
    }

    private void OnModelChanged(object? sender, LayoutChangeInfo e) => MarkStale();

    private void OnEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The technology names the layers and gives a via its span.
        if (e.PropertyName == nameof(LayoutEditorViewModel.Technology)) MarkStale();
    }

    private void MarkStale()
    {
        _isStale = true;
        if (!_isShown || _buildPosted) return;
        _buildPosted = true;
        PostBuild(() =>
        {
            _buildPosted = false;
            if (_isShown && _isStale) Build();
        });
    }

    private void Build()
    {
        _isStale = false;
        _highlights.Clear();
        if (Editor is { } vm)
        {
            _list = LayoutNets.Of(vm.Model, vm.Technology);
            ListBuilds++;
        }
        else _list = null;
        ApplyFilter();
    }

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        var all = _list?.Rows ?? [];
        string filter = FilterText.Trim();
        IReadOnlyList<LayoutNetRow> rows = filter.Length == 0
            ? all
            : [.. all.Where(r => r.Name.Contains(filter, System.StringComparison.OrdinalIgnoreCase))];

        var kept = _selected.Select(r => r.Name).ToHashSet(System.StringComparer.Ordinal);

        // Replacing the source empties the list box's selection, and that is not the user deselecting anything.
        _replacingRows = true;
        Rows = rows;
        _replacingRows = false;

        _selected.Clear();
        if (kept.Count > 0) _selected.AddRange(rows.Where(r => kept.Contains(r.Name)));
        SelectRowsRequested?.Invoke(_selected);

        OnPropertyChanged(nameof(HeaderText));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(HasEmptyText));
        SelectionMoved();
    }

    /// <summary>The list's selection, as the view hands it over. Reads what the last build kept and scans no shape.</summary>
    public void SetSelectedRows(IReadOnlyList<LayoutNetRow> rows)
    {
        if (_replacingRows) return;
        _selected.Clear();
        _selected.AddRange(rows);
        SelectionMoved();
    }

    private void SelectionMoved()
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectedRows));
        ZoomToNetCommand.NotifyCanExecuteChanged();
        SelectShapesCommand.NotifyCanExecuteChanged();
        PushHighlight();
    }

    partial void OnDimOtherCopperChanged(bool value) => PushHighlight();

    private void PushHighlight()
    {
        if (Editor is not { } vm) return;
        vm.SetNetHighlights(_isShown ? [.. _selected.Select(r => HighlightOf(r.Name))] : [], DimOtherCopper);
    }

    private LayoutNetHighlight HighlightOf(string net)
    {
        if (_highlights.TryGetValue(net, out var known)) return known;
        var shapes = Editor!.Model.Shapes;
        return _highlights[net] = new LayoutNetHighlight(net, [.. _list!.ShapesOf(net).Select(i => shapes[i])]);
    }

    private IEnumerable<int> SelectedShapes() => _selected.SelectMany(r => _list!.ShapesOf(r.Name));

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void ZoomToNet() => Editor?.ZoomToShapes(SelectedShapes());

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void SelectShapes() => Editor?.SelectShapes(SelectedShapes());
}

using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CircuitRF.Ui.Controls;
using CircuitRF.Ui.ViewModels;
using CircuitRF.Ui.ViewModels.Dock;

namespace CircuitRF.Ui.Views.Instances;

/// <summary>
/// Code-behind for the Instances panel (brief-find-instance-panel.md). It decides nothing about what
/// is listed — that is <see cref="InstanceListViewModel"/> — and owns only the gestures: double-click
/// and Enter (R-fi-11), Escape back to the canvas (R-fi-15), the two focus requests (R-fi-14), the
/// type picker's selection, and telling the list whether it is on screen (R-fi-7).
/// </summary>
public partial class InstancesToolView : UserControl
{
    private InstancesTool? _tool;
    private bool _attached;
    private bool _syncingPicker;

    public InstancesToolView()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
        RowsList.DoubleTapped += OnRowDoubleTapped;
        TypePicker.SelectionChanged += OnTypePickerSelectionChanged;

        // Tunnel, with handledEventsToo: WorkspaceWindow binds Escape to DisarmPlacementCommand, and a
        // Window KeyBinding marks the event handled before it reaches the box — the Project Tree's
        // search box needed exactly this.
        AddHandler(KeyDownEvent, OnPanelKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    // ── On screen or not ─────────────────────────────────────────────────────
    //
    // A panel behind another tab, collapsed to a strip, or closed has its view taken out of the tree,
    // and nothing is rebuilt while that is so.

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        PublishShown();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        PublishShown();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty) PublishShown();
    }

    private void PublishShown() => _tool?.ListVm.SetShown(_attached && IsVisible);

    // ── Binding to the tool ──────────────────────────────────────────────────

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_tool is not null)
        {
            _tool.ActivationFocusRequested -= OnActivationFocusRequested;
            _tool.SearchFocusRequested     -= OnSearchFocusRequested;
            _tool.ListVm.PropertyChanged   -= OnListPropertyChanged;
            _tool.ListVm.SetShown(false);
        }

        _tool = DataContext as InstancesTool;
        if (_tool is null) return;

        _tool.ActivationFocusRequested += OnActivationFocusRequested;
        _tool.SearchFocusRequested     += OnSearchFocusRequested;
        _tool.ListVm.PropertyChanged   += OnListPropertyChanged;
        SyncTypePicker();
        PublishShown();

        // Either request may have been made before this view existed — the first Ctrl/⌘+F of a session
        // shows a panel that has never been built.
        bool search   = _tool.ConsumeSearchFocus();
        bool activate = _tool.ConsumeActivationFocus();
        if (search) OnSearchFocusRequested();
        else if (activate) OnActivationFocusRequested();
    }

    private void OnListPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(InstanceListViewModel.TypeOptions) or nameof(InstanceListViewModel.SelectedType))
            SyncTypePicker();
    }

    /// <summary>Items first, then the selection, in one path — the order src/Ui/CLAUDE.md requires.</summary>
    private void SyncTypePicker()
    {
        if (_tool is null) return;
        var list = _tool.ListVm;

        _syncingPicker = true;
        try
        {
            if (!ReferenceEquals(TypePicker.ItemsSource, list.TypeOptions)) TypePicker.ItemsSource = list.TypeOptions;
            TypePicker.SelectedItem = list.SelectedType;
        }
        finally { _syncingPicker = false; }
    }

    private void OnTypePickerSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingPicker || _tool is null) return;
        if (TypePicker.SelectedItem is string type) _tool.ListVm.SelectedType = type;
    }

    // ── Focus ────────────────────────────────────────────────────────────────

    /// <summary>The panel was activated by its tab: caret in the search box, unless focus is already
    /// somewhere inside the panel (a click in the list must not have the caret yanked away).</summary>
    private void OnActivationFocusRequested() =>
        Dispatcher.UIThread.Post(() =>
        {
            if (PanelActivationFocus.AlreadyInside(this)) return;
            if (!SearchBox.Focus()) RowsList.Focus();
        }, DispatcherPriority.Input);

    /// <summary>Design ▸ Find Instance…: caret in the box with the last search SELECTED, so typing
    /// replaces it — whether or not the caret was already there.</summary>
    private void OnSearchFocusRequested() =>
        Dispatcher.UIThread.Post(() =>
        {
            if (!SearchBox.Focus()) return;
            SearchBox.SelectAll();
        }, DispatcherPriority.Input);

    // ── Gestures ─────────────────────────────────────────────────────────────

    /// <summary>A single click only selects the row (R-fi-11); the camera moves on the double-click.</summary>
    private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_tool is null) return;
        if ((e.Source as StyledElement)?.DataContext is InstanceRow row) _tool.ListVm.Activate(row);
    }

    private void OnPanelKeyDown(object? sender, KeyEventArgs e)
    {
        if (_tool is null || e.KeyModifiers != KeyModifiers.None) return;
        var list = _tool.ListVm;

        bool inSearch = SearchBox.IsKeyboardFocusWithin;
        bool inList   = RowsList.IsKeyboardFocusWithin;

        switch (e.Key)
        {
            // R-fi-15: back to the document's canvas.
            case Key.Escape when inSearch || inList:
                WorkspaceLocator.For(this)?.ReturnFocusFromInstancesPanel();
                e.Handled = true;
                break;

            // R-fi-11: Enter does what double-click does. From the search box it takes the highlighted
            // row, or the first match — type a name, press Enter.
            case Key.Enter when inList:
                list.Activate(list.SelectedRow);
                e.Handled = true;
                break;

            case Key.Enter when inSearch:
                var target = list.SelectedRow ?? (list.VisibleRows.Count > 0 ? list.VisibleRows[0] : null);
                if (target is not null) { list.SelectedRow = target; list.Activate(target); }
                e.Handled = true;
                break;

            // Down from the box walks into the list.
            case Key.Down when inSearch && list.VisibleRows.Count > 0:
                int index = Math.Max(RowsList.SelectedIndex, 0);
                RowsList.SelectedIndex = index;
                RowsList.ScrollIntoView(index);
                Dispatcher.UIThread.Post(() => RowsList.ContainerFromIndex(index)?.Focus(), DispatcherPriority.Input);
                e.Handled = true;
                break;
        }
    }
}

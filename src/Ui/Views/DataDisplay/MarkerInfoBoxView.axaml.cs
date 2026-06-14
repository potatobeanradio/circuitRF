// ================================================================
//  MarkerInfoBoxView.axaml.cs
//
//  Skia-rendered floating marker info box.
//
//  Rendering: overrides Render() to push ICustomDrawOperation that
//  calls MarkerRenderer.DrawInfoBox — same code path as the
//  PDF/SVG export renderer.
//
//  Interaction:
//    Left-drag   → reposition box (Canvas.Left/Top updated via VM,
//                  compositor repositions — no Skia repaint needed)
//    Double-tap  → opens compact marker editor Flyout
//
//  When the VM's NeedsRedraw property changes, InvalidateVisual()
//  is called so Skia redraws the box content (text updates on marker move).
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Material.Icons;
using Material.Icons.Avalonia;
using SkiaSharp;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;

namespace CircuitRF.Ui.Views.DataDisplay;

public partial class MarkerInfoBoxView : UserControl
{
    private MarkerInfoBoxViewModel? Vm => DataContext as MarkerInfoBoxViewModel;

    // ---- Flyout state ---------------------------------------------------
    private Flyout? _editorFlyout;

    // ---- Constructor ----------------------------------------------------

    public MarkerInfoBoxView()
    {
        InitializeComponent();

        PointerPressed  += OnPointerPressed;
        PointerMoved    += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        DoubleTapped    += OnDoubleTapped;

        // Invalidate when any resource in the lookup chain changes (e.g.
        // SystemAccentColor on a macOS accent-color or theme switch).
        ((Avalonia.Controls.IResourceHost)this).ResourcesChanged += (_, _) => InvalidateVisual();
    }

    // ---- Observe VM for data-driven redraws and build context menu ------

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is MarkerInfoBoxViewModel vm)
        {
            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName is nameof(MarkerInfoBoxViewModel.NeedsRedraw)
                                      or nameof(MarkerInfoBoxViewModel.IsSelected))
                    InvalidateVisual();
            };

            var menu = new ContextMenu();
            menu.Opening += (_, _) => RebuildContextMenu(menu);
            ContextMenu = menu;
        }
    }

    private void RebuildContextMenu(ContextMenu menu)
    {
        if (Vm is null) return;
        PopulateMarkerMenu(
            menu,
            Vm.Marker,
            Vm.Trace,
            Vm.Container.PlotVM.Plot.Traces,
            openEditorFlyout: () => OpenEditorFlyout(),
            changeToTrace:    t  => Vm.ChangeToTrace(t),
            removeMarker:     () => Vm.RemoveMarker(),
            showFilePrefix:   Vm.ShowFilePrefix);
    }

    /// <summary>
    /// Fills <paramref name="menu"/> with the standard marker context-menu items.
    /// Called by both <see cref="RebuildContextMenu"/> and
    /// <see cref="CircuitRF.Ui.DataDisplay.Controls.PlotControl.ShowMarkerContextMenu"/>
    /// so both surfaces always present identical options.
    /// </summary>
    internal static void PopulateMarkerMenu(
        ContextMenu                               menu,
        Marker                                    marker,
        Trace                                     trace,
        System.Collections.Generic.IList<Trace>   allTraces,
        Action?                                   openEditorFlyout,
        Action<Trace>                             changeToTrace,
        Action                                    removeMarker,
        bool                                      showFilePrefix = true)
    {
        menu.Items.Clear();

        var editItem = new MenuItem
        {
            Header    = $"Edit {marker.Name} Properties…",
            Icon      = new MaterialIcon { Kind = MaterialIconKind.PencilOutline },
            IsEnabled = openEditorFlyout is not null,
        };
        editItem.Click += (_, _) => openEditorFlyout?.Invoke();

        var changeItem = new MenuItem
        {
            Header = "Change to Trace…",
            Icon   = new MaterialIcon { Kind = MaterialIconKind.SwapHorizontalBold },
        };
        var otherTraces = allTraces.Where(t => t != trace).ToList();
        foreach (var t in otherTraces)
        {
            var captured = t;
            var sub = new MenuItem { Header = showFilePrefix ? t.Description : t.ShortDescription };
            sub.Click += (_, _) => changeToTrace(captured);
            changeItem.Items.Add(sub);
        }
        changeItem.IsEnabled = otherTraces.Count > 0;

        var removeItem = new MenuItem
        {
            Header = $"Remove {marker.Name} Marker",
            Icon   = new MaterialIcon { Kind = MaterialIconKind.DeleteOutline },
        };
        removeItem.Click += (_, _) => removeMarker();

        menu.Items.Add(editItem);
        menu.Items.Add(changeItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(removeItem);
    }

    // ---- Skia rendering -------------------------------------------------

    public override void Render(DrawingContext context)
    {
        if (Vm is null) return;
        // Resolve accent color here on the UI thread — InfoBoxDrawOperation.Render runs
        // on the compositor thread where Dispatcher.UIThread.Invoke can deadlock.
        bool    isSelected = Vm.IsSelected;
        SKColor selColor   = isSelected
            ? RenderTheme.GetTransparentAccent(RenderTheme.SelectionAlpha)
            : default;
        context.Custom(new InfoBoxDrawOperation(
            new Rect(Bounds.Size),
            Vm.Marker,
            Vm.Trace,
            Vm.FreqUnit,
            Vm.Theme,
            Vm.ShowFilePrefix,
            AppSettingsViewModel.Instance.MarkerBoxTransparentBackground,
            isSelected,
            selColor,
            Vm.Marker.IsMulti ? Vm.OtherTraces : null));
    }

    private sealed class InfoBoxDrawOperation : ICustomDrawOperation
    {
        private readonly Rect        _bounds;
        private readonly Marker      _marker;
        private readonly Trace       _trace;
        private readonly FreqUnit    _freqUnit;
        private readonly RenderTheme _theme;
        private readonly bool        _showFilePrefix;
        private readonly bool        _transparentBackground;
        private readonly bool        _isSelected;
        private readonly SKColor     _selectionColor;
        private readonly IReadOnlyList<Trace>? _otherTraces;

        public InfoBoxDrawOperation(
            Rect bounds, Marker marker, Trace trace,
            FreqUnit freqUnit, RenderTheme theme, bool showFilePrefix,
            bool transparentBackground, bool isSelected = false,
            SKColor selectionColor = default, IReadOnlyList<Trace>? otherTraces = null)
        {
            _bounds                = bounds;
            _marker                = marker;
            _trace                 = trace;
            _freqUnit              = freqUnit;
            _theme                 = theme;
            _showFilePrefix        = showFilePrefix;
            _transparentBackground = transparentBackground;
            _isSelected            = isSelected;
            _selectionColor        = selectionColor;
            _otherTraces           = otherTraces;
        }

        public bool   Equals(ICustomDrawOperation? other) => false;
        public Rect   Bounds   => _bounds;
        public bool   HitTest(Point p) => _bounds.Contains(p);

        public void Render(ImmediateDrawingContext context)
        {
            var lease = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (lease is null) return;
            using var l = lease.Lease();
            MarkerRenderer.DrawInfoBox(
                l.SkCanvas,
                (W: _bounds.Width, H: _bounds.Height),
                _marker, _trace, _freqUnit, _theme, _showFilePrefix,
                _transparentBackground, _isSelected, _selectionColor,
                _otherTraces);
        }

        public void Dispose() { }
    }

    // ---- Drag: move the info box ----------------------------------------

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        // Ctrl/Cmd+click toggles this InfoBox in the selection without clearing others.
        // Plain click selects only this InfoBox (clears all plots and other InfoBoxes).
        bool isCtrlOrMeta =
            e.KeyModifiers.HasFlag(KeyModifiers.Control) ||
            e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (isCtrlOrMeta) Vm?.ToggleSelect();
        else              Vm?.SelectOnly();

        var root = TopLevel.GetTopLevel(this);
        var pt   = root is not null ? e.GetPosition(root) : e.GetPosition(this);
        Vm?.StartGroupDrag(pt);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (e.Pointer.Captured != this) return;
        var root = TopLevel.GetTopLevel(this);
        Vm?.UpdateGroupDrag(root is not null ? e.GetPosition(root) : e.GetPosition(this));
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.Pointer.Captured != this) return;
        Vm?.EndGroupDrag();
        e.Pointer.Capture(null);
    }

    // ---- Double-tap: open marker editor flyout --------------------------

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        OpenEditorFlyout();
        e.Handled = true;
    }

    private void OpenEditorFlyout()
    {
        if (Vm is null) return;

        _editorFlyout?.Hide();

        var editor = new MarkerEditorView { DataContext = new MarkerEditorViewModel(Vm) };

        _editorFlyout = new Flyout
        {
            Content  = editor,
            Placement = PlacementMode.RightEdgeAlignedTop,
            ShowMode  = FlyoutShowMode.Standard
        };

        _editorFlyout.ShowAt(this, showAtPointer: true);
    }
}

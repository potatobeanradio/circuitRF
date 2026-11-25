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
using CircuitRF.Ui.Controls;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore;

namespace CircuitRF.Ui.Views.DataDisplay;

public partial class MarkerInfoBoxView : UserControl
{
    private MarkerInfoBoxViewModel? Vm => DataContext as MarkerInfoBoxViewModel;

    // ---- Flyout state ---------------------------------------------------
    private Flyout? _editorFlyout;
    private Point _pressPoint;
    private bool _frequencyPress;
    private bool _frequencyDragged;
    private bool _closingFrequencyEditor;

    // ---- Constructor ----------------------------------------------------

    public MarkerInfoBoxView()
    {
        InitializeComponent();

        Focusable = true;   // so a selected info box receives arrow keys for marker fine-movement

        PointerPressed  += OnPointerPressed;
        PointerMoved    += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        DoubleTapped    += OnDoubleTapped;
        FrequencyEditor.KeyDown += OnFrequencyEditorKeyDown;
        FrequencyEditor.TextChanged += (_, _) => { if (FrequencyEditor.IsVisible) FitFrequencyEditor(); };
        FrequencyEditor.LayoutUpdated += (_, _) => PlaceFrequencyUnit();
        FrequencyEditor.LostFocus += (_, _) => CloseFrequencyEditor(commit: true);

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
            Vm.Container.PlotVM.Plot,
            openEditorFlyout:     () => OpenEditorFlyout(),
            changeToTrace:        t  => Vm.ChangeToTrace(t),
            removeMarker:         () => Vm.RemoveMarker(),
            showFilePrefix:       Vm.ShowFilePrefix,
            onContourModeToggled: () => Vm.RequestRedraw(),
            onShowInfoBoxToggled: () => Vm.Container.RequestInfoBoxRebuild(),
            onVswrToggled:        () => { Vm.RequestRedraw(); Vm.Container.RequestPlotRedraw(); },
            linkTargets:          Vm.Container.LinkTargetsFor(Vm.Marker),
            onSnapToggled:        () => { Vm.OnMarkerMoved(); Vm.Container.RequestInfoBoxRebuild(); });
    }

    /// <summary>
    /// The traces <b>Change to Trace…</b> offers: every other trace on the plot, minus the ones the
    /// plot DERIVED (owner instruction, 2026-09-19).
    /// </summary>
    /// <remarks>
    /// <b>On a plot whose trace set is rebuilt from something else</b> — the Smith Chart tool's,
    /// where every trajectory, load point, generator glyph, band and constant-Q arc is made again
    /// from the design on each edit — re-pointing a marker at one of those offers a reading of
    /// something that will be a different object by the next keystroke, and the menu was a dozen
    /// rows of them.
    ///
    /// <para><b>The rule is <c>PlotInspectorViewModel.IsOwnersTrace</c>'s and is not a second
    /// one</b>: a fixed-readout plot's derived traces are exactly those carrying
    /// <see cref="Trace.ExcludeFromAxisLabels"/>, which that tool sets on everything it makes and
    /// never on the reference data a user adds through Plot Properties ▸ Add. On an ordinary Data
    /// Display nothing is fixed-readout, so this passes everything through and the menu is
    /// unchanged.</para>
    /// </remarks>
    internal static List<Trace> ChangeToTraceCandidates(
        System.Collections.Generic.IList<Trace> allTraces, Trace current, Plot hostPlot)
        => [.. allTraces.Where(t => t != current)
                        .Where(t => !(hostPlot.IsFixedReadout && t.ExcludeFromAxisLabels))];

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
        Plot                                      hostPlot,
        Action?                                   openEditorFlyout,
        Action<Trace>                             changeToTrace,
        Action                                    removeMarker,
        bool                                      showFilePrefix = true,
        Action?                                   onContourModeToggled = null,
        Action?                                   onShowInfoBoxToggled = null,
        Action?                                   onVswrToggled = null,
        System.Collections.Generic.IReadOnlyList<(string Label, bool IsLinked, Action Toggle)>? linkTargets = null,
        Action?                                   onSnapToggled = null)
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
        var otherTraces = ChangeToTraceCandidates(allTraces, trace, hostPlot);
        foreach (var t in otherTraces)
        {
            var captured = t;
            var sub = new MenuItem { Header = t.ReadoutDescription(showFilePrefix) };
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
        // "Change to Trace…" is meaningless for a contour marker (it isn't bound to a swept
        // network/cube trace it could be re-pointed onto), so it is never shown for contours.
        if (marker.MarkerKind != MarkerKind.Contour)
            menu.Items.Add(changeItem);

        // brief-ddl-2 R-ddl2-2: the plots whose pins this marker can drive — one tick links the pin, from the
        // overview straight to a detail plot that already exists. Only shown when there is somewhere to link.
        if (linkTargets is { Count: > 0 })
        {
            var linkItem = new MenuItem
            {
                Header = "Link plots",
                Icon   = new MaterialIcon { Kind = MaterialIconKind.LinkVariant },
            };
            foreach (var (label, isLinked, toggle) in linkTargets)
            {
                var sub = new MenuItem
                {
                    Header = label,
                    Icon   = new MaterialIcon
                    {
                        Kind = isLinked ? MaterialIconKind.CheckboxOutline : MaterialIconKind.CheckboxBlankOutline,
                    },
                };
                var act = toggle;
                sub.Click += (_, _) => act();
                linkItem.Items.Add(sub);
            }
            menu.Items.Add(linkItem);
        }

        // ---- Grouped toggles (no separators between them) ----
        if (PlotRenderer.VswrAvailableFor(hostPlot, trace, marker))
        {
            var vswrItem = new MenuItem
            {
                Header = marker.VswrEnabled
                    ? $"VSWR: {marker.VswrValue.ToString("0.###", System.Globalization.CultureInfo.CurrentCulture)}"
                    : "VSWR",
                Icon = new MaterialIcon
                {
                    Kind = marker.VswrEnabled
                        ? MaterialIconKind.CheckboxOutline
                        : MaterialIconKind.CheckboxBlankOutline,
                },
            };
            vswrItem.Click += (_, _) =>
            {
                marker.VswrEnabled = !marker.VswrEnabled;
                onVswrToggled?.Invoke();
            };
            menu.Items.Add(vswrItem);
        }

        if (marker.MarkerKind == MarkerKind.Contour)
        {
            var snapItem = new MenuItem
            {
                Header = "Snap to Point",
                Icon   = new MaterialIcon
                {
                    Kind = marker.ContourSnapped
                        ? MaterialIconKind.CheckboxOutline
                        : MaterialIconKind.CheckboxBlankOutline,
                },
            };
            snapItem.Click += (_, _) =>
            {
                marker.ContourSnapped = !marker.ContourSnapped;
                marker.PositionStatic = trace.ResolveContourMarkerPosition(marker, marker.PositionStatic);
                onContourModeToggled?.Invoke();
            };
            menu.Items.Add(snapItem);
        }

        // brief-ddl-3 R-ddl3-1: a marker on a loadpull's own loads is snapped to a measured load by default, as a
        // contour marker can be. Unticked it is placed anywhere — and the plots following it read the loadpull there.
        if (trace.IsLoadpullLoadTrace && hostPlot.PlotType == PlotType.Smith && marker.MarkerKind != MarkerKind.Contour)
        {
            bool onLoad = !marker.FreePosition || marker.SnappedToCurve;
            var snapLoad = new MenuItem
            {
                Header = "Snap to Point",
                Icon   = new MaterialIcon
                {
                    Kind = onLoad ? MaterialIconKind.CheckboxOutline : MaterialIconKind.CheckboxBlankOutline,
                },
            };
            snapLoad.Click += (_, _) =>
            {
                if (onLoad)
                {
                    // Free from where it stands: the position IS the load it was on.
                    marker.PositionStatic = trace.GetMarkerDataLocation(marker);
                    marker.FreePosition   = true;
                }
                else
                {
                    // Bound to the trace again; its position resolves to the nearest measured load.
                    marker.FreePosition = false;
                }
                marker.SnappedToCurve = false;
                onSnapToggled?.Invoke();
            };
            menu.Items.Add(snapLoad);
        }

        var showBoxItem = new MenuItem
        {
            Header = "Show Info Box",
            Icon   = new MaterialIcon
            {
                Kind = marker.ShowInfoBox
                    ? MaterialIconKind.CheckboxOutline
                    : MaterialIconKind.CheckboxBlankOutline,
            },
            // A Table forces every info box on (no on-canvas way to re-open a hidden box), so the
            // off-toggle is disabled there.
            IsEnabled = hostPlot.PlotType != PlotType.Table,
        };
        showBoxItem.Click += (_, _) =>
        {
            marker.ShowInfoBox = !marker.ShowInfoBox;
            onShowInfoBoxToggled?.Invoke();
        };
        menu.Items.Add(showBoxItem);

        // ---- Remove — always last, separator above ----
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
            ? PlotAccentColor.GetTransparentAccent(RenderTheme.SelectionAlpha)
            : default;
        context.Custom(new InfoBoxDrawOperation(
            new Rect(Bounds.Size),
            Vm.DisplayMarker,
            Vm.Trace,
            Vm.FreqUnit,
            Vm.Theme,
            Vm.ShowFilePrefix,
            AppSettingsViewModel.Instance.MarkerBoxTransparentBackground,
            isSelected,
            selColor,
            Vm.PlotTraces));
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
        private readonly IReadOnlyList<Trace>? _plotTraces;

        public InfoBoxDrawOperation(
            Rect bounds, Marker marker, Trace trace,
            FreqUnit freqUnit, RenderTheme theme, bool showFilePrefix,
            bool transparentBackground, bool isSelected = false,
            SKColor selectionColor = default, IReadOnlyList<Trace>? plotTraces = null)
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
            _plotTraces            = plotTraces;
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
                _plotTraces);
        }

        public void Dispose() { }
    }

    // ---- Drag: move the info box ----------------------------------------

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (FrequencyEditor.IsVisible) return;

        // Ctrl/Cmd+click toggles this InfoBox in the selection without clearing others.
        // Plain click selects only this InfoBox (clears all plots and other InfoBoxes).
        bool isCtrlOrMeta =
            e.KeyModifiers.HasFlag(KeyModifiers.Control) ||
            e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (isCtrlOrMeta) Vm?.ToggleSelect();
        else              Vm?.SelectOnly();

        Focus();   // claim keyboard focus so arrow keys drive marker fine-movement

        var root = TopLevel.GetTopLevel(this);
        var pt   = root is not null ? e.GetPosition(root) : e.GetPosition(this);
        _pressPoint = pt;
        _frequencyDragged = false;
        _frequencyPress = FrequencyRowBounds() is { } row && row.Contains(e.GetPosition(this));
        Vm?.StartGroupDrag(pt);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    /// <summary>
    /// Arrow keys step the selected marker(s) by one x-axis sample (Up/Right → higher x, Down/Left →
    /// lower; spectral axes step in frequency) — the same handler the plot canvas uses, so the behavior
    /// is identical whether the canvas or this info box has focus. Only on Rect plots.
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (FrequencyEditor.IsVisible)
        {
            base.OnKeyDown(e);
            return;
        }
        if (Vm is { } vm && (vm.PlotType.IsRect() || vm.PlotType.IsComplex()) &&
            e.Key is Key.Up or Key.Down or Key.Left or Key.Right)
        {
            int direction = e.Key is Key.Up or Key.Right ? +1 : -1;
            if (vm.Container.StepSelectedMarkers(direction))
            {
                e.Handled = true;
                return;
            }
        }
        base.OnKeyDown(e);
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (e.Pointer.Captured != this) return;
        var root = TopLevel.GetTopLevel(this);
        var point = root is not null ? e.GetPosition(root) : e.GetPosition(this);
        if (Math.Abs(point.X - _pressPoint.X) > 4 || Math.Abs(point.Y - _pressPoint.Y) > 4)
            _frequencyDragged = true;
        Vm?.UpdateGroupDrag(point);
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.Pointer.Captured != this) return;
        Vm?.EndGroupDrag();
        e.Pointer.Capture(null);
        if (_frequencyPress && !_frequencyDragged) OpenFrequencyEditor();
        _frequencyPress = false;
    }

    // ---- Double-tap: open marker editor flyout --------------------------

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (FrequencyEditor.IsVisible) return;
        OpenEditorFlyout();
        e.Handled = true;
    }

    /// <summary>Where the freq line's VALUE is drawn — its left edge, its baseline and the text size — as
    /// <c>MarkerRenderer.DrawInfoBox</c> draws it: after the "freq=" prefix, in IBM Plex at the box's text size.</summary>
    private (double X, double Baseline, double Size, double ValueWidth)? FrequencyValueAnchor()
    {
        if (Vm is not { } vm) return null;
        var lines = vm.Trace.BuildMarkerBoxLines(vm.DisplayMarker, vm.FreqUnit, vm.ShowFilePrefix, vm.PlotTraces);
        int index = lines.FindIndex(line => line.Item1.StartsWith("freq=", StringComparison.OrdinalIgnoreCase));
        if (index < 0) return null;
        double ts = Bounds.Height / (1.3 * lines.Count + 0.3);
        double padding = ts * 0.3;
        string line = lines[index].Item1;
        string prefix = line[..(line.IndexOf('=') + 1)];
        using var font   = new SkiaSharp.SKFont(CircuitRF.Render.SkiaFonts.PlexRegular, (float)ts);
        using var fontFb = new SkiaSharp.SKFont(CircuitRF.Render.SkiaFonts.DejaVuRegular, (float)ts);
        double prefixW = CircuitRF.Render.DataDisplay.RendererText.MeasureTextWithFallback(prefix, font, fontFb);
        double valueW = CircuitRF.Render.DataDisplay.RendererText.MeasureTextWithFallback(line[prefix.Length..], font, fontFb);
        return (padding + prefixW, padding + ts * (index + 0.85) + index * padding, ts, valueW);
    }

    private Rect? FrequencyRowBounds()
    {
        if (Vm is not { } vm || !vm.Trace.TryGetMarkerFrequency(vm.DisplayMarker, out _)) return null;
        var lines = vm.Trace.BuildMarkerBoxLines(vm.DisplayMarker, vm.FreqUnit, vm.ShowFilePrefix, vm.PlotTraces);
        int index = lines.FindIndex(line => line.Item1.StartsWith("freq=", StringComparison.OrdinalIgnoreCase));
        if (index < 0) return null;
        double textSize = Bounds.Height / (1.3 * lines.Count + 0.3);
        double padding = textSize * 0.3;
        return new Rect(padding, padding + index * (textSize + padding),
                        Math.Max(0, Bounds.Width - padding * 2), textSize + padding);
    }

    private void OpenFrequencyEditor()
    {
        if (Vm is not { } vm || FrequencyValueAnchor() is not { } at ||
            !vm.Trace.TryGetMarkerFrequency(vm.DisplayMarker, out double frequency)) return;
        // The number, opened where the line drew it after "freq=", in the label's face and size; the unit is plain text
        // past the box, not in it. The box widens with what is typed, and the unit follows.
        string number = (frequency * vm.FreqUnit.Scale()).ToString("G12", System.Globalization.CultureInfo.InvariantCulture);
        FrequencyUnit.Text = vm.FreqUnit.Description();
        FrequencyUnit.FontSize = at.Size;
        FrequencyUnit.FontFamily = FrequencyEditor.FontFamily;
        // Opaque, in the colour the box is drawn in, and together with the editor at least as wide as the value the line
        // drew — so none of the old "2 GHz" shows past the box while it is edited.
        var fill = AppSettingsViewModel.Instance.MarkerBoxTransparentBackground
            ? vm.Theme.BackgroundColor : CircuitRF.Render.DataDisplay.CardStyle.Fill(vm.Theme);
        FrequencyUnit.Background = new SolidColorBrush(Color.FromArgb(fill.Alpha, fill.Red, fill.Green, fill.Blue));
        _frequencyValueRight = at.X + at.ValueWidth + 1;
        FrequencyEditor.Open(number, at.X, at.Baseline, at.Size);
        FitFrequencyEditor();
        FrequencyUnit.IsVisible = true;
        FrequencyEditor.Focus();
        FrequencyEditor.SelectAll();
    }

    private void OnFrequencyEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CloseFrequencyEditor(commit: true); e.Handled = true; }
        else if (e.Key == Key.Escape) { CloseFrequencyEditor(commit: false); e.Handled = true; }
    }

    private void CloseFrequencyEditor(bool commit)
    {
        if (_closingFrequencyEditor || !FrequencyEditor.IsVisible) return;
        _closingFrequencyEditor = true;
        if (commit && Vm is { } vm && FrequencyHz(FrequencyEditor.Text, vm.FreqUnit) is { } hz)
            vm.TrySetFrequency(hz);
        FrequencyEditor.IsVisible = false;
        FrequencyUnit.IsVisible = false;
        _closingFrequencyEditor = false;
    }

    /// <summary>
    /// The box holds the number alone, sized to it as typed (with no two-character floor, so a single digit's box is not
    /// wide), and the unit is drawn just past the box's right edge on the line's baseline — where the line's own unit
    /// sat, and following the box as digits are typed.
    /// </summary>
    private void FitFrequencyEditor()
    {
        double size = FrequencyEditor.FontSize;
        using var font = new SkiaSharp.SKFont(CircuitRF.Render.SkiaFonts.PlexRegular, (float)size);
        double chrome = SchematicInlineEditBox.WidthFor("", size) - InlineEdit.WidthFromMeasuredText(0, size);
        double number = font.MeasureText(FrequencyEditor.Text ?? "");
        FrequencyEditor.Width = chrome + Math.Max(size * 0.6, number + size * 0.3);
        PlaceFrequencyUnit();
    }

    /// <summary>
    /// The unit just past the box's right edge, as the box is actually laid out (its theme can add to the width asked
    /// for), at the height the box sets its own text — its top, its border and its top padding.
    /// </summary>
    private void PlaceFrequencyUnit()
    {
        if (!FrequencyEditor.IsVisible) return;
        var b = FrequencyEditor.Bounds;
        if (b.Width <= 0) return;
        double gap = FrequencyEditor.FontSize * 0.25;
        double left = b.Right;
        FrequencyUnit.Padding = new Thickness(gap, 0, 2, 0);
        FrequencyUnit.Margin = new Thickness(left, b.Top + FrequencyEditor.BorderThickness.Top + FrequencyEditor.Padding.Top, 0, 0);
        FrequencyUnit.MinWidth = Math.Max(0, _frequencyValueRight - left);
    }

    private double _frequencyValueRight;

    private static readonly System.Text.RegularExpressions.Regex NumberWithUnit = new(@"^\s*(\S+?)\s*([A-Za-z]*Hz)?\s*$");

    /// <summary>The frequency typed into the editor, in Hz: a number in the display unit, or a number with a frequency
    /// unit of its own (<c>2400 MHz</c>). Null for anything else.</summary>
    internal static double? FrequencyHz(string? text, FreqUnit displayUnit)
    {
        var m = NumberWithUnit.Match(text ?? "");
        if (!m.Success || !NumericText.TryParseDouble(m.Groups[1].Value, out double v)) return null;
        if (m.Groups[2].Value is not { Length: > 0 } unit) return v / displayUnit.Scale();
        var (scale, baseUnit) = CircuitRF.Render.DataDisplay.SiUnit.Split(unit);
        return baseUnit == "Hz" ? v * scale : null;
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

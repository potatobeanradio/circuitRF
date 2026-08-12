using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using CircuitRF.Ui.Controls;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.WBond;
using CircuitRF.Ui.Views.Dialogs;
using CircuitRF.Ui.Theming;
using CircuitRF.WBond;
using Avalonia.Styling;

namespace CircuitRF.Ui.Views.WBond;

/// <summary>
/// The wBond editor: the inductance panel, the profile view, and the layout view with the wire
/// overlay on it (wbond.md §6.1).
///
/// <para>The overlay is attached to the layout canvas HERE rather than by a binding, because the
/// canvas must also be told to repaint when the overlay's own state changes — and that repaint must
/// not go anywhere near the layout's path cache (WB17). <see cref="LayoutCanvas.InvalidateOverlay"/>
/// is the seam that guarantees it.</para>
/// </summary>
public partial class WBondEditorView : UserControl
{
    private WBondDocumentViewModel? _bound;
    private WBondDocument? _subscribedDoc;

    public WBondEditorView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;

        // Tunnel + handledEventsToo, for the reason src/Ui/CLAUDE.md already records: a window-level
        // KeyBinding is processed before visual-tree routing and marks the event handled, so a plain
        // bubble handler here would silently never run after a toolbar click moved focus.
        AddHandler(KeyDownEvent, OnViewKeyDownTunnel, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    // ── Clipboard (§6.7) ──────────────────────────────────────────────────────

    private async void OnViewKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (_bound is null || !IsKeyboardFocusWithin) return;

        bool ctrl = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
        if (!ctrl) return;

        switch (e.Key)
        {
            // Shift+Ctrl/⌘+C is the GRAPHIC copy (§6.7's "to other applications"); the plain gesture
            // stays the wire copy, so the muscle-memory one never surprises anyone with a picture.
            case Key.C when (e.KeyModifiers & KeyModifiers.Shift) != 0:
                await CopyGraphicAsync(); e.Handled = true; break;
            case Key.C: e.Handled = await CopyAsync(); break;
            case Key.X: e.Handled = await CopyAsync() && Cut(); break;
            case Key.V: e.Handled = await PasteAsync(); break;
            case Key.Z when (e.KeyModifiers & KeyModifiers.Shift) != 0: _bound.Editor.Redo(); RepaintBoth(); e.Handled = true; break;
            case Key.Z: _bound.Editor.Undo(); RepaintBoth(); e.Handled = true; break;
            case Key.Y: _bound.Editor.Redo(); RepaintBoth(); e.Handled = true; break;
        }
    }

    /// <summary>
    /// Copies whatever is selected — wires, layout geometry, or BOTH (§6.7).
    ///
    /// <para>A single-kind selection writes the plain single-kind payload, so it still pastes into
    /// another wBond editor or into the Layout Editor. Only a genuinely mixed selection is wrapped in
    /// <see cref="WBondMixedClipboard"/>'s envelope.</para>
    /// </summary>
    internal async Task<bool> CopyAsync()
    {
        if (_bound is null) return false;
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return false;

        string? wires = _bound.Editor.CopySelection();

        string? layout = null;
        if (_bound.ReferenceLayout?.BuildCopyPayload() is { } fragment)
            layout = LayoutFragment.Serialize(fragment);

        if (WBondMixedClipboard.Compose(wires, layout) is not { } text) return false;

        await clipboard.SetTextAsync(text);
        return true;
    }

    /// <summary>
    /// §6.7 — copies the design to other applications as a GRAPHIC (PDF + SVG + bitmap at once),
    /// through the shared <c>PlotExporter</c> clipboard path rather than a second implementation.
    /// </summary>
    internal async Task CopyGraphicAsync()
    {
        if (_bound is null) return;

        var variant = ActualThemeVariant == ThemeVariant.Dark ? ColorVariant.Dark : ColorVariant.Light;

        await WBondGraphicExport.CopyToClipboardAsync(
            this,
            _bound.Editor.Design,
            _bound.ReferenceLayout?.Model,
            _bound.ReferenceLayout?.Technology,
            _bound.ReferenceLayout?.InstanceBaseDir,
            _bound.Overlay.Theme,
            LayoutRenderTheme.FromTheme(ThemeService.Active, variant),
            _bound.Overlay.Thickness);
    }

    /// <summary>
    /// Cut is Copy then Delete, across BOTH kinds — the wires and the geometry go together, matching
    /// what the copy just put on the clipboard.
    /// </summary>
    private bool Cut()
    {
        if (_bound is null) return false;

        int wires = _bound.Editor.DeleteSelectedWires();
        _bound.ReferenceLayout?.DeleteSelectedGeometry();

        RepaintBoth();
        return true;
    }

    /// <summary>
    /// Pastes wires, layout geometry, or both — dispatching on which marker the clipboard carries.
    ///
    /// <para>The mixed envelope is tried FIRST because a plain payload can never be mistaken for one,
    /// while the reverse is not true: both single-kind parsers ignore properties they do not declare,
    /// so an envelope handed to either would deserialize into an all-default object rather than
    /// failing loudly.</para>
    /// </summary>
    internal async Task<bool> PasteAsync()
    {
        if (_bound is null) return false;
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return false;

        string? text = await clipboard.TryGetTextAsync();

        // The one shared unwrap: a mixed envelope splits, anything else is offered to both parsers
        // and each refuses what is not its own.
        var (wiresJson, layoutJson) = WBondMixedClipboard.Unwrap(text);

        // Offset by one nudge step so the copies are visibly distinct from their originals rather
        // than hiding exactly on top of them — which would also make the fill singular.
        long step = WireEdits.CoarseNudgeNm;

        int pasted = _bound.Editor.PasteWires(wiresJson, 0, step);
        pasted += PasteLayoutHalf(layoutJson, step);

        if (pasted <= 0) return false;

        RepaintBoth();
        return true;
    }

    /// <summary>
    /// Pastes the geometry half in place, offset to match the wires so a mixed paste stays together.
    ///
    /// <para>Routed through the layout editor's own rebase and paste commands, so a pasted instance's
    /// <c>CellRef</c> is resolved against THIS document exactly as it would be in the Layout Editor —
    /// never a second placement path.</para>
    /// </summary>
    private int PasteLayoutHalf(string? json, long stepNm)
    {
        if (_bound?.ReferenceLayout is not { } layout) return 0;
        if (!LayoutFragment.TryDeserialize(json, out var payload) || payload is null) return 0;

        var shapes = LayoutFragment.Translate(payload.Shapes, stepNm, stepNm);
        var instances = layout.RebaseFragmentInstances(payload);
        if (instances.Count > 0) instances = LayoutFragment.Translate(instances, stepNm, stepNm);

        if (shapes.Count == 0 && instances.Count == 0) return 0;

        layout.PasteInPlace(shapes, instances);
        return shapes.Count + instances.Count;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_bound is not null)
        {
            _bound.Overlay.OverlayChanged -= OnOverlayChanged;
            _bound.Editor.EditRefused -= OnEditRefused;
        }
        if (_subscribedDoc is not null)
        {
            _subscribedDoc.ZoomToFitRequested -= OnZoomToFitRequestedFromMenu;
            _subscribedDoc.CutRequested       -= OnCutRequestedFromMenu;
            _subscribedDoc.CopyRequested      -= OnCopyRequestedFromMenu;
            _subscribedDoc.PasteRequested     -= OnPasteRequestedFromMenu;
        }

        _subscribedDoc = DataContext as WBondDocument;
        if (_subscribedDoc is not null)
        {
            _subscribedDoc.ZoomToFitRequested += OnZoomToFitRequestedFromMenu;
            _subscribedDoc.CutRequested       += OnCutRequestedFromMenu;
            _subscribedDoc.CopyRequested      += OnCopyRequestedFromMenu;
            _subscribedDoc.PasteRequested     += OnPasteRequestedFromMenu;
        }

        _bound = _subscribedDoc?.ViewModel;
        if (_bound is null) return;

        _bound.Overlay.OverlayChanged += OnOverlayChanged;
        _bound.Editor.EditRefused += OnEditRefused;
        LayoutCanvasCtrl.CanvasOverlay = _bound.Overlay;

        _updatingUnits = true;
        UnitCombo.ItemsSource = Enum.GetValues<WBondUnit>();
        UnitCombo.SelectedItem = _bound.Editor.DisplayUnit;
        _updatingUnits = false;

        RefreshQualityText();
    }

    private void OnOverlayChanged()
    {
        // The wires moved; the layout did not. Repaint without touching LayoutPathCache.
        LayoutCanvasCtrl.InvalidateOverlay();
        RefreshQualityText();
    }

    /// <summary>
    /// WB15: while the ladder is degraded the readout is an approximation, and the panel says so.
    /// A live number that is silently provisional is worse than one that is visibly provisional.
    /// </summary>
    private void RefreshQualityText()
    {
        if (_bound is null) { QualityText.Text = ""; return; }

        // A refusal outranks the ladder's own status until the next edit clears it — the user needs
        // to read why their edit came back before being told how fast the last frame was.
        if (_refusalShowing)
        {
            if (!_bound.Overlay.ReadoutIsProvisional) return;
            _refusalShowing = false;
        }

        QualityText.ClearValue(TextBlock.ForegroundProperty);
        QualityText.Text = _bound.Overlay.ReadoutIsProvisional ? "provisional — " + _bound.Overlay.Quality : "";
    }

    /// <summary>
    /// An edit the physics could not evaluate was rolled back. Shown in the toolbar strip rather than
    /// a modal: the edit is already undone, so there is nothing to decide — but it must not be
    /// silent, or the wire visibly springing back reads as the editor being broken.
    /// </summary>
    private void OnEditRefused(string reason)
    {
        QualityText.Foreground = this.FindResource("CrfWarningBrush") as Avalonia.Media.IBrush;
        QualityText.Text = reason;
        _refusalShowing = true;
        RepaintBoth();
    }

    private bool _refusalShowing;

    private void OnFitProfile(object? sender, RoutedEventArgs e) => ProfileCanvas.ZoomToFit();

    // View->Zoom to Fit dispatches here from WorkspaceViewModel via WBondDocument.RequestZoomToFit().
    // A wBond editor shows two canvases at once; fit both, mirroring the "Fit profile view" button
    // for the profile side and the ordinary layout Zoom to Fit for the artwork side.
    private void OnZoomToFitRequestedFromMenu()
    {
        ProfileCanvas.ZoomToFit();
        LayoutCanvasCtrl.ZoomToFit();
    }

    // Workspace toolbar Cut/Copy/Paste dispatch here — mirrors OnViewKeyDownTunnel's Ctrl+C/X/V case
    // exactly (the plain, non-Shift copy — the wire copy, not the "copy as graphic" one).
    private async void OnCutRequestedFromMenu()   { if (await CopyAsync()) Cut(); }
    private async void OnCopyRequestedFromMenu()  => await CopyAsync();
    private async void OnPasteRequestedFromMenu() => await PasteAsync();

    private async void OnCopyGraphic(object? sender, RoutedEventArgs e) => await CopyGraphicAsync();

    private void OnThicknessModeChanged(object? sender, RoutedEventArgs e)
    {
        var mode = TrueDiameterToggle.IsChecked == true
            ? WireThicknessMode.TrueDiameter
            : WireThicknessMode.ConstantPixels;

        // Per-view (WB22a) — but this one toggle drives both, because the two views are showing the
        // same wires and a mode that applied to only one of them would read as a bug.
        ProfileCanvas.Thickness = mode;
        if (_bound is not null) _bound.Overlay.Thickness = mode;

        ProfileCanvas.InvalidateVisual();
        LayoutCanvasCtrl.InvalidateOverlay();
    }

    private void OnSnapChanged(object? sender, RoutedEventArgs e)
    {
        if (_bound is not null) _bound.Overlay.SnapEnabled = SnapToggle.IsChecked == true;
    }

    private void OnWireMarqueeChanged(object? sender, RoutedEventArgs e)
    {
        if (_bound is not null) _bound.Overlay.WireMarqueeEnabled = WireMarqueeToggle.IsChecked == true;
    }

    private void OnDrawWireChanged(object? sender, RoutedEventArgs e)
    {
        if (_bound is null) return;

        _bound.Overlay.WireDrawArmed = DrawWireToggle.IsChecked == true;
        if (_bound.Overlay.WireDrawArmed && RotateToolToggle.IsChecked == true)
            RotateToolToggle.IsChecked = false;

        LayoutCanvasCtrl.InvalidateOverlay();
    }

    // ── Transforms (§6.4) ─────────────────────────────────────────────────────

    private void OnRotateToolChanged(object? sender, RoutedEventArgs e)
    {
        if (_bound is null) return;

        _bound.Overlay.WireRotateArmed = RotateToolToggle.IsChecked == true;

        // The two tools want the same press, so arming one disarms the other rather than leaving the
        // user to discover which of them their click went to.
        if (_bound.Overlay.WireRotateArmed && DrawWireToggle.IsChecked == true)
            DrawWireToggle.IsChecked = false;
    }

    private void OnReverse(object? sender, RoutedEventArgs e) => Apply(() => _bound!.Editor.ReverseSelection());

    private void OnStraighten(object? sender, RoutedEventArgs e) => Apply(() => _bound!.Editor.StraightenSelection());

    private void OnReapplyProfile(object? sender, RoutedEventArgs e) => Apply(() => _bound!.Editor.ReapplyProfileToSelection());

    private void OnDetach(object? sender, RoutedEventArgs e) => Apply(() => _bound!.Editor.DetachSelection());

    private async void OnTransform(object? sender, RoutedEventArgs e)
    {
        if (_bound is null) return;

        int touched = await WBondTransformDialog.ShowAsync(
            TopLevel.GetTopLevel(this) as Window, _bound.Editor, _bound.Editor.DisplayUnit);

        if (touched > 0) RepaintBoth();
    }

    /// <summary>
    /// Runs a transform and repaints. A transform that touched nothing is a no-op rather than an
    /// error — the selection simply had nothing it applied to, which the toolbar already shows.
    /// </summary>
    private void Apply(Func<int> transform)
    {
        if (_bound is null) return;
        if (transform() > 0) RepaintBoth();
    }

    private void RepaintBoth()
    {
        LayoutCanvasCtrl.InvalidateOverlay();
        ProfileCanvas.InvalidateVisual();
    }

    private bool _updatingUnits;

    private void OnUnitChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingUnits || _bound is null || UnitCombo.SelectedItem is not WBondUnit unit) return;

        // §6.5: readouts only. Storage stays in DBU, so this is lossless and changes no geometry —
        // and it deliberately does NOT touch the nudge step, which is a bonder-process quantity
        // (WB25) rather than a display convenience.
        _bound.Editor.DisplayUnit = unit;
    }
}

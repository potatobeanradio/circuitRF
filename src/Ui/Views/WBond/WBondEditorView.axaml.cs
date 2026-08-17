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

        // One resolution of the wire palette, pushed to both canvases. The profile canvas is the
        // control — it has the theme notifications — and the overlay is a plain object, so it follows.
        ProfileCanvas.ThemeRefreshed += SyncOverlayWireTheme;
        ActualThemeVariantChanged += (_, _) => SyncOverlayWireTheme();

        // Tunnel + handledEventsToo, for the reason src/Ui/CLAUDE.md already records: a window-level
        // KeyBinding is processed before visual-tree routing and marks the event handled, so a plain
        // bubble handler here would silently never run after a toolbar click moved focus.
        AddHandler(KeyDownEvent, OnViewKeyDownTunnel, RoutingStrategies.Tunnel, handledEventsToo: true);

        // ── Rulers. Each strip MIRRORS its canvas's viewport — there is no independent state, which
        // is the whole contract LayoutRulerControl was built to; see LayoutEditorView's identical
        // three subscriptions.
        LayoutCanvasCtrl.ViewportChanged    += (_, _) => SyncLayoutRulers();
        LayoutCanvasCtrl.LayoutUpdated      += (_, _) => SyncLayoutRulers();
        LayoutCanvasCtrl.CursorWorldChanged += OnLayoutCursorWorldChanged;

        ProfileCanvas.ViewportChanged    += (_, _) => SyncProfileRulers();
        ProfileCanvas.CursorWorldChanged += OnProfileCursorWorldChanged;
    }

    // ── Rulers (owner, 2026-08-16) ────────────────────────────────────────────

    /// <summary>
    /// Pushes the layout canvas's pan/zoom and the reference layout's units onto its two strips.
    /// </summary>
    private void SyncLayoutRulers()
    {
        var vp = LayoutCanvasCtrl.CurrentViewport;
        LayoutHRuler.SetViewport(vp.PanX, vp.PanY, vp.Zoom, vp.Width, vp.Height);
        LayoutVRuler.SetViewport(vp.PanX, vp.PanY, vp.Zoom, vp.Width, vp.Height);

        int dbuPerMicron = _bound?.ReferenceLayout?.Model.DbuPerMicron ?? LayoutUnits.DefaultDbuPerMicron;
        var unit = WBondDocumentViewModel.ToLayoutUnit(_bound?.Editor.DisplayUnit ?? WBondUnit.Mil);

        LayoutHRuler.SetUnits(dbuPerMicron, unit);
        LayoutVRuler.SetUnits(dbuPerMicron, unit);
    }

    /// <summary>
    /// The same, for the profile view — whose world unit is the NANOMETRE.
    ///
    /// <para>That is why the strips are driven at 1,000 DBU/µm rather than at the reference layout's
    /// resolution: one DBU is one nanometre there, so <c>LayoutUnits.Format</c> labels this canvas's
    /// own coordinates correctly with no conversion. Driving it at the layout's resolution would put
    /// a ruler on the profile view that disagreed with the profile view by that factor.</para>
    /// </summary>
    private void SyncProfileRulers()
    {
        var vp = ProfileCanvas.CurrentViewport;
        ProfileHRuler.SetViewport(vp.PanX, vp.PanY, vp.Zoom, vp.Width, vp.Height);
        ProfileVRuler.SetViewport(vp.PanX, vp.PanY, vp.Zoom, vp.Width, vp.Height);

        var unit = WBondDocumentViewModel.ToLayoutUnit(_bound?.Editor.DisplayUnit ?? WBondUnit.Mil);
        ProfileHRuler.SetUnits(NanometreDbuPerMicron, unit);
        ProfileVRuler.SetUnits(NanometreDbuPerMicron, unit);
    }

    /// <summary>The resolution at which one database unit IS one nanometre — the profile view's own.</summary>
    private const int NanometreDbuPerMicron = 1000;

    private void OnLayoutCursorWorldChanged(object? sender, (double X, double Y)? world)
    {
        LayoutHRuler.SetCursorWorld(world?.X);
        LayoutVRuler.SetCursorWorld(world?.Y);
    }

    private void OnProfileCursorWorldChanged(object? sender, (double Span, double Z)? world)
    {
        ProfileHRuler.SetCursorWorld(world?.Span);
        ProfileVRuler.SetCursorWorld(world?.Z);
    }

    /// <summary>
    /// Pushes the active tool into BOTH canvases.
    ///
    /// <para>The overlay's two armed flags follow <c>ActiveTool</c> on the view-model already; the
    /// PROFILE canvas is a control and cannot see it, which is exactly why the Wire tool did nothing
    /// there (owner, 2026-08-16). Rotate stays layout-only: it rotates a wire about an end point in
    /// the XY plane, and the profile view has no XY plane to rotate in.</para>
    /// </summary>
    private void ApplyActiveTool()
    {
        ProfileCanvas.WireDrawArmed = _bound?.ActiveTool == WBondTool.DrawWire;
        LayoutCanvasCtrl.InvalidateOverlay();
        ProfileCanvas.InvalidateVisual();
    }

    /// <summary>Applies the ruler toggle to both views at once — one switch, two canvases.</summary>
    private void ApplyRulerVisibility()
    {
        bool show = _bound?.RulersVisible ?? true;

        ProfileRulerRow.IsVisible = show;
        ProfileVRuler.IsVisible   = show;
        LayoutRulerRow.IsVisible  = show;
        LayoutVRuler.IsVisible    = show;
    }

    // ── Save (owner, 2026-08-16) ──────────────────────────────────────────────
    //
    // The button asks; the HOST answers. The workspace routes it through SaveWBondDoc (which knows
    // the workspace's picker, its open-document map and its message log) and the standalone binary
    // through its own — so neither gains a second way to write a .wBond.

    private void OnSave(object? sender, RoutedEventArgs e) => _subscribedDoc?.RequestSave(saveAs: false);

    private void OnSaveAs(object? sender, RoutedEventArgs e) => _subscribedDoc?.RequestSave(saveAs: true);

    /// <summary>
    /// Double-clicking an array's name in the inductance panel selects that array's wires (owner,
    /// 2026-08-16).
    ///
    /// <para>The row carries its own array INDEX, so membership is read from the mesh rather than
    /// matched by name — two arrays may not share a name today, but resolving a selection by string
    /// would make that a silent selection bug the day one does.</para>
    /// </summary>
    private void OnArrayNameDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_bound is null || sender is not Control { DataContext: WBondArrayRowViewModel row }) return;

        _bound.Editor.SelectArray(row.ArrayIndex);
        RepaintBoth();
        e.Handled = true;
    }

    /// <summary>
    /// Double-clicking a card's Loop height / Span / Diameter / Material opens the SAME prompt the
    /// profile view's context menu opens, on the same array (owner, 2026-08-16), and applies to every
    /// wire in it.
    ///
    /// <para>The panel is where a user reads those four numbers, so it is where they reach for them.
    /// Both routes land on <c>SetGroup…Async</c> — one implementation, one undo entry, one refusal
    /// path.</para>
    /// </summary>
    private void OnArrayLoopHeightDoubleTapped(object? sender, TappedEventArgs e) =>
        PromptForArray(sender, e, SetGroupLoopHeightAsync);

    private void OnArraySpanDoubleTapped(object? sender, TappedEventArgs e) =>
        PromptForArray(sender, e, SetGroupSpanAsync);

    private void OnArrayDiameterDoubleTapped(object? sender, TappedEventArgs e) =>
        PromptForArray(sender, e, SetGroupDiameterAsync);

    private void OnArrayMaterialDoubleTapped(object? sender, TappedEventArgs e) =>
        PromptForArray(sender, e, SetGroupMaterialAsync);

    private void PromptForArray(object? sender, TappedEventArgs e, Func<int, Task> prompt)
    {
        if (_bound is null || sender is not Control { DataContext: WBondArrayRowViewModel row }) return;

        e.Handled = true;
        _ = prompt(row.ArrayIndex);
    }

    // ── Clipboard (§6.7) ──────────────────────────────────────────────────────

    private async void OnViewKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (_bound is null || !IsKeyboardFocusWithin) return;

        // Escape is handled HERE, not in the canvases, because two of the three things it has to do
        // are toolbar state that no canvas can reach. See HandleEscape.
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
        {
            e.Handled = HandleEscape();
            return;
        }

        if (e.KeyModifiers == KeyModifiers.None && !IsTypingInAField())
        {
            switch (e.Key)
            {
                // The two TOOL keys (owner, 2026-08-16). W was previously a held promotion modifier
                // for a click (w+click selected the whole wire); that gesture is gone rather than
                // sharing the key, because double-clicking a point or a segment already promotes to
                // the whole wire and is the gesture the rest of the application uses.
                case Key.W:
                    _bound.ActiveTool = WBondTool.DrawWire;
                    e.Handled = true;
                    return;

                case Key.R:
                    _bound.ActiveTool = WBondTool.Rotate;
                    e.Handled = true;
                    return;

                // T is the Transform DIALOG, not a tool — the same button, and gated the same way:
                // with nothing selected the button is disabled, so the key does nothing either.
                case Key.T:
                    if (_bound.HasSelection) OnTransform(this, new RoutedEventArgs());
                    e.Handled = true;
                    return;

                // Delete removes the selected WIRES. Structural, and therefore one undo entry that
                // restores the deleted wire objects themselves — see WBondViewModel.Restore.
                case Key.Delete:
                case Key.Back:
                    if (_bound.Editor.DeleteSelectedWires() > 0) RepaintBoth();
                    e.Handled = true;
                    return;

                // V cycles which canvases are showing; I toggles the inductance panel. Both are the
                // owner's own bindings and both persist with the document.
                //
                // V rather than Tab (owner, 2026-08-16): Tab is the focus-navigation key every
                // Avalonia control expects, so claiming it here would have to out-race the focus
                // manager in every host this view is embedded in — and would leave keyboard users
                // with no way to walk the toolbar.
                case Key.V:
                    _bound.CycleViewMode();
                    e.Handled = true;
                    return;

                case Key.I:
                    _bound.PanelVisible = !_bound.PanelVisible;
                    e.Handled = true;
                    return;
            }
        }

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
    /// Whether keyboard focus is inside a text field, where a bare letter is TEXT and not a command.
    ///
    /// <para>Without this, typing "45" into the profile-axis box or "2mil" into Snap would be read
    /// letter by letter as editor shortcuts — <c>I</c> would hide the inductance panel mid-word and
    /// <c>V</c> would rearrange the editor under the user's hands.</para>
    /// </summary>
    private bool IsTypingInAField() =>
        TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement()
            is TextBox or AutoCompleteBox or ComboBox { IsEditable: true };

    /// <summary>
    /// Escape, in one place, doing one thing at a time — the standard CAD unwind.
    ///
    /// <para>Ordered most-specific first, so a single press never undoes two things at once:</para>
    /// <list type="number">
    /// <item>A tool is armed — Draw Wire or Rotate. <b>Disarm it and return to select mode.</b>
    ///   Un-checking Draw Wire also abandons any half-placed wire, through
    ///   <c>WBondLayoutOverlay.WireDrawArmed</c>'s own setter, so cancelling a wire in progress and
    ///   leaving the tool are one press rather than two.</item>
    /// <item>Nothing armed but something selected — <b>clear the selection.</b></item>
    /// <item>Nothing armed and nothing selected — do nothing, and leave the key UNHANDLED so an
    ///   ancestor (a dialog, the shell) still sees it.</item>
    /// </list>
    ///
    /// <para><b>Why not in the canvases.</b> The overlay could already cancel a half-placed wire and
    /// clear a selection, but it cannot change the active TOOL — so the tool stayed armed, the next
    /// click started another wire, and Escape read as doing nothing. <c>ActiveTool</c> is the source
    /// of truth for arming (the overlay's two flags are derived from it), so the unwind starts
    /// there and the toolbar's Select button lights up as the visible proof it happened.</para>
    /// </summary>
    /// <returns>True when the key was consumed.</returns>
    private bool HandleEscape()
    {
        if (_bound is null) return false;

        if (_bound.ActiveTool != WBondTool.Select)
        {
            _bound.ActiveTool = WBondTool.Select;
            RepaintBoth();
            return true;
        }

        if (!_bound.Editor.Selection.IsEmpty)
        {
            _bound.Editor.ClearSelection();
            RepaintBoth();
            return true;
        }

        return false;
    }

    /// <summary>
    /// Copies whatever is selected — wires, layout geometry, or BOTH (§6.7).
    ///
    /// <para>A single-kind selection writes the plain single-kind payload, so it still pastes into
    /// another wBond editor or into the Layout Editor. Only a genuinely mixed selection is wrapped in
    /// <see cref="WBondMixedClipboard"/>'s envelope.</para>
    ///
    /// <para><b>The JSON rides alongside a PDF, an SVG and a PNG</b> (owner, 2026-08-16: copy into
    /// PowerPoint/Keynote did nothing, because the JSON was all that was ever written). The
    /// multi-format write is <see cref="WBondClipboardWriter"/>, a transcription of the Layout
    /// Editor's own — including its Windows bypass, which is the half that took several rounds to get
    /// right and must not be re-derived here.</para>
    /// </summary>
    internal async Task<bool> CopyAsync()
    {
        if (_bound is null) return false;
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return false;

        string? wires = _bound.Editor.CopySelection();

        var fragment = _bound.ReferenceLayout?.BuildCopyPayload();
        string? layout = fragment is null ? null : LayoutFragment.Serialize(fragment);

        if (WBondMixedClipboard.Compose(wires, layout) is not { } text) return false;

        return await WBondClipboardWriter.CopyAsync(
            this, clipboard, text,
            WBondClipboardWriter.SelectionDesign(_bound.Editor.Design, _bound.Editor.Selection),
            WBondClipboardWriter.TransientLayout(fragment),
            _bound.ReferenceLayout?.Technology,
            _bound.ReferenceLayout?.InstanceBaseDir,
            _bound.Overlay.Theme,
            LayoutRenderTheme.FromTheme(ThemeService.Active, CurrentVariant),
            _bound.Overlay.Thickness);
    }

    /// <summary>
    /// §6.7 — copies the design to other applications as a GRAPHIC (PDF + SVG + bitmap at once),
    /// through the shared <c>PlotExporter</c> clipboard path rather than a second implementation.
    /// </summary>
    /// <summary>
    /// Gives the layout overlay the wire palette the profile canvas just resolved, so the two views
    /// of the same wires cannot show them in different colours.
    /// </summary>
    private void SyncOverlayWireTheme()
    {
        if (_bound is null) return;

        _bound.Overlay.Theme = ProfileCanvas.WireTheme;
        LayoutCanvasCtrl.InvalidateOverlay();
    }

    private ColorVariant CurrentVariant =>
        ActualThemeVariant == ThemeVariant.Dark ? ColorVariant.Dark : ColorVariant.Light;

    internal async Task CopyGraphicAsync()
    {
        if (_bound is null) return;

        var variant = CurrentVariant;

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

        // Offset ACROSS the wires by the Settings dialog's paste pitch — stepped until nothing lands
        // on top of a wire already there. Pasting the SAME clipboard twice used to place the second
        // copy exactly on the first, which makes the fill singular; see PasteWiresAtFreePitch.
        var payload = WBondClipboard.TryParse(wiresJson);
        var (dx, dy) = payload is null
            ? (0L, WBondDefaults.PastePitchNm)
            : _bound.Editor.FreePasteOffset(payload, WBondDefaults.PastePitchNm);

        int pasted = _bound.Editor.PasteWires(wiresJson, dx, dy);

        // The geometry half moves by the SAME displacement, so a mixed paste arrives together.
        pasted += PasteLayoutHalf(layoutJson, dx, dy);

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
    private int PasteLayoutHalf(string? json, long dxNm, long dyNm)
    {
        if (_bound?.ReferenceLayout is not { } layout) return 0;
        if (!LayoutFragment.TryDeserialize(json, out var payload) || payload is null) return 0;

        // The SAME displacement the wire half took — the two must arrive on top of each other or a
        // mixed paste comes apart. (nm and DBU coincide at the 1,000 DBU/µm default this editor
        // works at; see WBondSnap for why that bridge is restated wherever it is crossed.)
        var shapes = LayoutFragment.Translate(payload.Shapes, dxNm, dyNm);
        var instances = layout.RebaseFragmentInstances(payload);
        if (instances.Count > 0) instances = LayoutFragment.Translate(instances, dxNm, dyNm);

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
            _bound.Editor.ReadoutChanged -= OnReadoutChanged;
            _bound.Editor.PropertyChanged -= OnEditorPropertyChanged;
            _bound.PropertyChanged -= OnDocumentPropertyChanged;
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
        _bound.Editor.ReadoutChanged += OnReadoutChanged;
        _bound.Editor.PropertyChanged += OnEditorPropertyChanged;
        _bound.PropertyChanged += OnDocumentPropertyChanged;
        LayoutCanvasCtrl.CanvasOverlay = _bound.Overlay;

        _updatingUnits = true;
        UnitCombo.ItemsSource = UnitLabels;
        UnitCombo.SelectedItem = WBondUnits.Suffix(_bound.Editor.DisplayUnit);
        _updatingUnits = false;

        SyncProfileAxisCombo();
        ProfileCanvas.Azimuth = _bound.Editor.ProfileAzimuthRadians;
        ApplyActiveTool();
        SyncOverlayWireTheme();
        RebindReferenceLayout();
        ApplyArrangement();
        ApplyRulerVisibility();
        SyncLayoutRulers();
        SyncProfileRulers();
        RefreshQualityText();
    }

    /// <summary>
    /// Follows the reference layout's SNAP, which is the pitch both canvases draw their grid from and
    /// the step wire points land on.
    ///
    /// <para>One control governs both views deliberately: a visible grid the wires ignored — or two
    /// grids at different pitches in two views of the same wires — would be worse than none.</para>
    /// </summary>
    private void RebindReferenceLayout()
    {
        if (_snapVm is not null) _snapVm.PropertyChanged -= OnLayoutVmPropertyChanged;

        _snapVm = _bound?.ReferenceLayout;
        if (_snapVm is not null) _snapVm.PropertyChanged += OnLayoutVmPropertyChanged;

        PushSnapPitch();
    }

    private LayoutEditorViewModel? _snapVm;

    private void OnLayoutVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LayoutEditorViewModel.SnapDbu)) PushSnapPitch();
    }

    private void PushSnapPitch()
    {
        int dbuPerMicron = _snapVm?.Model.DbuPerMicron ?? LayoutUnits.DefaultDbuPerMicron;
        long snapDbu = _snapVm?.SnapDbu ?? 0;
        long pitchNm = snapDbu > 0 ? WBondSnap.ToNm(snapDbu, dbuPerMicron) : 0;

        ProfileCanvas.GridPitchNm = pitchNm;
        if (_bound is not null) _bound.Overlay.GridPitchNm = pitchNm;

        ProfileCanvas.InvalidateVisual();
        LayoutCanvasCtrl.InvalidateOverlay();
    }

    /// <summary>
    /// A SELECTION change repaints both canvases.
    ///
    /// <para>Selection is not part of the readout, so it raises no <c>ReadoutChanged</c>, and each
    /// canvas only ever repainted itself — which is why clicking empty space in the layout view left
    /// the same wires still drawn as selected in the profile view. One place, both canvases.</para>
    /// </summary>
    private void OnEditorPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // PreviewSelection is the LIVE marquee's contents, raised on every frame of the box — and both
        // canvases repaint, because a wire caught in the profile view is the same wire in the layout
        // view and has to light up in both.
        if (e.PropertyName is nameof(WBondViewModel.Selection)
                           or nameof(WBondViewModel.PreviewSelection)) RepaintBoth();
        else if (e.PropertyName == nameof(WBondViewModel.ProfileAzimuthRadians))
        {
            ProfileCanvas.Azimuth = _bound?.Editor.ProfileAzimuthRadians;
            SyncProfileAxisCombo();
            ProfileCanvas.InvalidateVisual();
        }
        else if (e.PropertyName == nameof(WBondViewModel.DisplayUnit))
        {
            _updatingUnits = true;
            UnitCombo.SelectedItem = WBondUnits.Suffix(_bound!.Editor.DisplayUnit);
            _updatingUnits = false;

            // §6.5 — the rulers are a READOUT and follow the editor's unit, exactly as the panel's
            // length rows and the metadata bar do.
            SyncLayoutRulers();
            SyncProfileRulers();
        }
    }

    private void OnDocumentPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WBondDocumentViewModel.ViewMode)
                           or nameof(WBondDocumentViewModel.PanelVisible))
            ApplyArrangement(restoreFocus: true);
        else if (e.PropertyName == nameof(WBondDocumentViewModel.RulersVisible))
            ApplyRulerVisibility();
        else if (e.PropertyName == nameof(WBondDocumentViewModel.ActiveTool))
            ApplyActiveTool();
        else if (e.PropertyName == nameof(WBondDocumentViewModel.ReferenceLayout))
        {
            RebindReferenceLayout();
            SyncLayoutRulers();
        }
    }

    /// <summary>
    /// The display units, spelled the way they are WRITTEN — lower case, and the same spellings
    /// <see cref="WBondUnits.TryParseUnit"/> accepts and <see cref="WBondUnits.Suffix"/> emits.
    ///
    /// <para>Binding the bare enum put <c>Mil</c>/<c>Um</c>/<c>Inch</c> in the toolbar, which is not
    /// how anyone writes a unit and did not match the suffix shown in every readout beside it. Using
    /// the suffix strings themselves means there is nothing to keep in sync: the picker offers exactly
    /// what the parser takes.</para>
    /// </summary>
    private static readonly string[] UnitLabels =
        [.. Enum.GetValues<WBondUnit>().Select(WBondUnits.Suffix)];

    private void OnOverlayChanged()
    {
        // The wires moved; the layout did not. Repaint without touching LayoutPathCache.
        LayoutCanvasCtrl.InvalidateOverlay();
        RefreshQualityText();
    }

    /// <summary>
    /// The geometry changed from somewhere OTHER than a canvas gesture — the Properties panel, the
    /// profile context menu, undo, a dialog.
    ///
    /// <para><b>Both canvases repaint, and the layout one is the point.</b> Only the profile canvas
    /// listened to this event before, so a Properties-panel edit that moved a wire's feet in XY — Span
    /// above all — left the layout view showing the OLD geometry until some unrelated event happened
    /// to repaint it. That is the owner's "changing the Span takes seconds": the edit itself is
    /// ~0.05 ms, measured; what took seconds was the picture catching up. Loop height looked fast
    /// because its visible effect is in the profile view, which was already repainting.</para>
    /// </summary>
    private void OnReadoutChanged()
    {
        // A REFUSAL is cleared by the next successful edit, and this is that edit (owner, 2026-08-16:
        // "the 'inductance matrix is not positive definite' message does not ever go away").
        //
        // It was meant to be cleared by RefreshQualityText, which is called on every overlay repaint
        // — but that method's guard read "keep the refusal unless the readout is PROVISIONAL", so on
        // an ordinary (non-degraded) frame it returned early and left the message standing forever.
        // The condition was inverted against its own intent, and the intent is here instead: a
        // readout change is the one event that means "an edit went through", which is exactly when a
        // rolled-back edit's message has stopped being true.
        //
        // Ordering is what makes this safe: WBondViewModel.RefuseEdit restores the snapshot — which
        // republishes and lands here — BEFORE it raises EditRefused, so a genuine refusal still ends
        // with its own message on screen.
        _refusalShowing = false;
        RefreshQualityText();

        RepaintBoth();
    }

    /// <summary>
    /// WB15: while the ladder is degraded the readout is an approximation, and the panel says so.
    /// A live number that is silently provisional is worse than one that is visibly provisional.
    /// </summary>
    private void RefreshQualityText()
    {
        if (_bound is null) { QualityText.Text = ""; return; }

        // A refusal outranks the ladder's own status, and is cleared by the next successful edit —
        // which is OnReadoutChanged, not this method. This one runs on every overlay repaint,
        // including ones that changed no geometry at all (a selection, a theme change, a pan), so
        // clearing here would drop the message before it had been read.
        if (_refusalShowing) return;

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

    // ── View arrangement and zoom ─────────────────────────────────────────────

    private void OnCycleViewMode(object? sender, RoutedEventArgs e) => _bound?.CycleViewMode();

    /// <summary>
    /// Applies the current view mode and panel visibility to the grid definitions.
    ///
    /// <para><b>Sizes are written, not bound, and the reason is the GridSplitter.</b> A splitter
    /// resizes by writing a concrete <see cref="GridLength"/> straight into the definition it is
    /// attached to, which silently replaces any binding on that property — so a bound collapse would
    /// work exactly until the first time the user dragged the splitter. Each collapsible size is
    /// therefore remembered on the way down and restored on the way up, which also means a user's own
    /// split survives a trip through single-view mode.</para>
    /// </summary>
    private void ApplyArrangement(bool restoreFocus = false)
    {
        if (_bound is null) return;

        bool hadFocus = IsKeyboardFocusWithin;
        bool profile = _bound.ProfileVisible;
        bool layout = _bound.LayoutVisible;
        bool split = _bound.SplitterVisible;

        var profileRow = CanvasGrid.RowDefinitions[0];
        var splitRow = CanvasGrid.RowDefinitions[1];
        var layoutRow = CanvasGrid.RowDefinitions[2];

        if (profileRow.Height.IsStar) _profileRowSize = profileRow.Height;
        if (layoutRow.Height.IsStar) _layoutRowSize = layoutRow.Height;

        profileRow.Height = profile ? (layout ? _profileRowSize : new GridLength(1, GridUnitType.Star))
                                    : new GridLength(0);
        layoutRow.Height = layout ? (profile ? _layoutRowSize : new GridLength(1, GridUnitType.Star))
                                  : new GridLength(0);
        splitRow.Height = split ? new GridLength(4) : new GridLength(0);

        ProfileBorder.IsVisible = profile;
        LayoutCanvasHost.IsVisible = layout;   // the canvas AND its two ruler strips
        CanvasSplitter.IsVisible = split;

        var panelColumn = RootGrid.ColumnDefinitions[0];
        var panelSplitColumn = RootGrid.ColumnDefinitions[1];

        if (panelColumn.Width.Value > 0) _panelColumnSize = panelColumn.Width;

        panelColumn.Width = _bound.PanelVisible ? _panelColumnSize : new GridLength(0);
        panelSplitColumn.Width = _bound.PanelVisible ? new GridLength(4) : new GridLength(0);
        PanelBorder.IsVisible = _bound.PanelVisible;
        PanelSplitter.IsVisible = _bound.PanelVisible;

        if (restoreFocus && hadFocus) FocusVisibleCanvas();
    }

    /// <summary>
    /// Puts keyboard focus back on a canvas that is still on screen.
    ///
    /// <para><b>Hiding a control that HAS focus orphans the focus</b>, and this view's key handler is
    /// gated on <c>IsKeyboardFocusWithin</c> — so cycling away from the canvas the user was working in
    /// left the editor deaf to its own shortcuts until they clicked something. That is exactly the
    /// owner's "pressing V repeatedly does not cycle unless I click on a canvas between keystrokes".
    /// </para>
    ///
    /// <para>Only ever called when focus was already inside this view, so it can never yank focus out
    /// of a field somewhere else in the application.</para>
    /// </summary>
    private void FocusVisibleCanvas()
    {
        if (_bound is null) return;

        Control target = _bound.ProfileVisible ? ProfileCanvas : LayoutCanvasCtrl;
        if (target.IsEffectivelyVisible) target.Focus();
    }

    private GridLength _profileRowSize = new(2, GridUnitType.Star);
    private GridLength _layoutRowSize = new(3, GridUnitType.Star);

    /// <summary>
    /// The inductance panel's shipped width. Narrowed from 260 to 156 px at the owner's request
    /// (2026-08-16) — a card is now a name, a number and a chevron, so the width the expanded card
    /// needed is width the two canvases can have back.
    /// </summary>
    private GridLength _panelColumnSize = new(DefaultPanelWidth);

    internal const double DefaultPanelWidth = 156;

    /// <summary>
    /// Zoom to Fit frames BOTH canvases — a wBond editor shows two pictures of the same wires at two
    /// different scales (§6.1), and fitting only the one with focus would leave the other stale.
    /// The three relative zooms act on whichever canvases are actually showing.
    /// </summary>
    private void OnZoomToFit(object? sender, RoutedEventArgs e)
    {
        ProfileCanvas.ZoomToFit();
        LayoutCanvasCtrl.ZoomToFit();
    }

    private void OnZoomIn(object? sender, RoutedEventArgs e) => ForEachVisibleCanvas(
        () => ProfileCanvas.ZoomIn(), () => LayoutCanvasCtrl.ZoomIn());

    private void OnZoomOut(object? sender, RoutedEventArgs e) => ForEachVisibleCanvas(
        () => ProfileCanvas.ZoomOut(), () => LayoutCanvasCtrl.ZoomOut());

    private void OnZoom1To1(object? sender, RoutedEventArgs e) => ForEachVisibleCanvas(
        () => ProfileCanvas.Zoom1To1(_bound?.Editor.DisplayUnit ?? WBondUnit.Mil),
        () => LayoutCanvasCtrl.Zoom1To1());

    private void ForEachVisibleCanvas(Action profile, Action layout)
    {
        if (_bound?.ProfileVisible != false) profile();
        if (_bound?.LayoutVisible != false) layout();
    }

    // ── The profile view's plane (§6.2) ───────────────────────────────────────

    private void OnProfileAxisCommit(object? sender, RoutedEventArgs e)
    {
        if (sender is ComboBox box) CommitProfileAxis(box);
    }

    private void OnProfileAxisKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Return) || sender is not ComboBox box) return;

        CommitProfileAxis(box);
        e.Handled = true;
        ProfileCanvas.Focus();
    }

    private void OnProfileAxisSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingProfileAxis || sender is not ComboBox { SelectedItem: string preset }) return;
        Commit(preset);
    }

    private void CommitProfileAxis(ComboBox box) => Commit(box.Text ?? "");

    /// <summary>
    /// Commits a plane and puts the box back on the CANONICAL spelling of what was accepted — so
    /// typing "90" reads back as "YZ", and text that means nothing snaps back rather than sitting
    /// there looking as though it took.
    /// </summary>
    private void Commit(string text)
    {
        if (_bound is null) return;

        _bound.Editor.CommitProfileAxisText(text);
        SyncProfileAxisCombo();

        ProfileCanvas.Azimuth = _bound.Editor.ProfileAzimuthRadians;
        ProfileCanvas.InvalidateVisual();
    }

    private void SyncProfileAxisCombo()
    {
        if (_bound is null) return;

        _updatingProfileAxis = true;
        ProfileAxisCombo.SelectedItem = null;
        ProfileAxisCombo.Text = _bound.Editor.ProfileAxisText;
        _updatingProfileAxis = false;
    }

    private bool _updatingProfileAxis;

    // ── Snap (the reference layout's own ladder, reused verbatim) ─────────────

    private LayoutEditorViewModel? LayoutVm => _bound?.ReferenceLayout;

    private void OnSnapDistanceCommit(object? sender, RoutedEventArgs e)
    {
        if (sender is ComboBox box) LayoutVm?.CommitSnapDistanceText(box.Text ?? "");
    }

    private void OnSnapDistanceKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Return) || sender is not ComboBox box) return;

        LayoutVm?.CommitSnapDistanceText(box.Text ?? "");
        e.Handled = true;
        LayoutCanvasCtrl.Focus();
    }

    private void OnSnapDistanceSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { SelectedItem: string text }) LayoutVm?.CommitSnapLadderSelection(text);
    }

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
            : WireThicknessMode.Thin;

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

    // ── Transforms (§6.4) ─────────────────────────────────────────────────────

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
        if (_updatingUnits || _bound is null) return;
        if (UnitCombo.SelectedItem is not string label) return;
        if (!WBondUnits.TryParseUnit(label, out var unit)) return;

        // §6.5: readouts only. Storage stays in DBU, so this is lossless and changes no geometry —
        // and it deliberately does NOT touch the nudge step, which is a bonder-process quantity
        // (WB25) rather than a display convenience.
        _bound.Editor.DisplayUnit = unit;
    }
}

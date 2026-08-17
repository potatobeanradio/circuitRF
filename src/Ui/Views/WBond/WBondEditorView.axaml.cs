using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using CircuitRF.Ui.Commands;
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
        ProfileView.ThemeRefreshed += SyncOverlayWireTheme;
        ActualThemeVariantChanged += (_, _) => SyncOverlayWireTheme();

        // Tunnel + handledEventsToo, for the reason src/Ui/CLAUDE.md already records: a window-level
        // KeyBinding is processed before visual-tree routing and marks the event handled, so a plain
        // bubble handler here would silently never run after a toolbar click moved focus.
        AddHandler(KeyDownEvent, OnViewKeyDownTunnel, RoutingStrategies.Tunnel, handledEventsToo: true);

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
        ProfileView.WireDrawArmed = _bound?.ActiveTool == WBondTool.DrawWire;

        // A WIRE tool taking over disarms whatever the hosted Layout Editor's toolbar had armed. The
        // converse lives in OnLayoutToolChanged; both are needed, because either toolbar can be
        // clicked at any time and two "the next click places something" tools armed at once has no
        // correct behaviour.
        if (_bound?.ActiveTool != WBondTool.Select) DisarmLayoutTool();

        HostedLayoutView.InvalidateOverlay();
        ProfileView.Repaint();
    }

    /// <summary>
    /// Applies the ruler toggle to both views at once — one switch, two canvases.
    ///
    /// <para>The layout half's strips belong to the hosted <c>LayoutEditorView</c> now (WB39a), so
    /// this reaches them through its own <c>RulersVisible</c> rather than hosting a second pair.</para>
    /// </summary>
    private void ApplyRulerVisibility()
    {
        bool show = _bound?.RulersVisible ?? true;

        ProfileView.RulersVisible      = show;
        HostedLayoutView.RulersVisible = show;
    }

    // ── Save (owner, 2026-08-16) ──────────────────────────────────────────────
    //
    // The button asks; the HOST answers. The workspace routes it through SaveWBondDoc (which knows
    // the workspace's picker, its open-document map and its message log) and the standalone binary
    // through its own — so neither gains a second way to write a .wBond.

    private void OnSave(object? sender, RoutedEventArgs e) => _subscribedDoc?.RequestSave(saveAs: false);

    private void OnSaveAs(object? sender, RoutedEventArgs e) => _subscribedDoc?.RequestSave(saveAs: true);

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
                //
                // Gated on there actually BEING a wire selection, and that gate is load-bearing now
                // that the layout half is the real Layout Editor (WB39a): this tunnel handler sits on
                // an ANCESTOR of it, so an unconditional e.Handled here would swallow every Delete
                // meant for a selected shape or instance and the layout's own delete would look
                // broken. Left unhandled, the key reaches LayoutEditorViewModel.OnKeyDown as usual.
                case Key.Delete:
                case Key.Back:
                    if (_bound.Editor.Selection.IsEmpty) return;
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
            case Key.Z when (e.KeyModifiers & KeyModifiers.Shift) != 0: RedoLast(); e.Handled = true; break;
            case Key.Z: UndoLast(); e.Handled = true; break;
            case Key.Y: RedoLast(); e.Handled = true; break;
        }
    }

    /// <summary>
    /// <b>Ctrl+Z undoes whichever edit was made last</b> — a wire edit or a layout edit.
    ///
    /// <para>WB39a put two real histories in front of one user: this editor's wire snapshots and the
    /// hosted Layout Editor's command stack, which follows whichever frame is pushed in. They cannot
    /// be one stack (one restores whole-design snapshots, the other replays commands) and neither can
    /// simply win: routing by focus is wrong because a WIRE drag happens on the LAYOUT canvas, and
    /// "wires first" would undo a wire move the user made ten minutes ago rather than the rectangle
    /// they just drew. Each recorded entry carries an <c>EditSequence</c> stamp, so the question
    /// "which did I do last" has one total answer.</para>
    /// </summary>
    private void UndoLast()
    {
        if (_bound is null) return;

        var wires = _bound.Editor;
        var layout = _bound.LayoutDocument?.UndoRedo;

        // The rule itself is EditSequence's, shared with LayoutEditorViewModel.UndoLast — a wirebond
        // cell in the ordinary Layout Editor asks the identical question, and two copies of the
        // comparison would be two chances to get the direction wrong.
        if (layout is not null && EditSequence.UndoTakesFirst(
                layout.CanUndo, layout.TopUndoStamp, wires.CanUndo, wires.TopUndoStamp))
        {
            layout.Undo();
            HostedLayoutView.InvalidateCanvas();
            return;
        }

        wires.Undo();
        RepaintBoth();
    }

    /// <summary>The mirror image: redo takes the OLDEST undone entry, which is the one undo produced last.</summary>
    private void RedoLast()
    {
        if (_bound is null) return;

        var wires = _bound.Editor;
        var layout = _bound.LayoutDocument?.UndoRedo;

        if (layout is not null && EditSequence.RedoTakesFirst(
                layout.CanRedo, layout.TopRedoStamp, wires.CanRedo, wires.TopRedoStamp))
        {
            layout.Redo();
            HostedLayoutView.InvalidateCanvas();
            return;
        }

        wires.Redo();
        RepaintBoth();
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

        _bound.Overlay.Theme = ProfileView.WireTheme;
        HostedLayoutView.InvalidateOverlay();
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

        // The wires ride over the HOSTED editor's canvas (WB39a) — reached through its own
        // pass-through property, never by walking into its visual tree.
        HostedLayoutView.CanvasOverlay = _bound.Overlay;

        // A wBond document IS a document, so the layout half must not offer a second torn-off File
        // menu describing a different file.
        HostedLayoutView.IsHostedInAnotherDocument = true;


        // The MENU's Copy is the mixed one (§6.7) — wires + geometry + the picture formats — which
        // only a host with a layout half can perform. The profile view offers it because this host
        // supplies it, and omits it where nobody does.
        ProfileView.CopyRequested = () => CopyAsync();

        ApplyActiveTool();
        SyncOverlayWireTheme();
        RebindHostedLayout();
        ApplyArrangement();
        ApplyRulerVisibility();
        RefreshQualityText();
    }

    /// <summary>
    /// Re-points everything this view keeps in code-behind at whichever layout frame is on screen.
    ///
    /// <para><b>The frame can change now</b>, which it never could before WB39a: hosting
    /// <c>LayoutEditorView</c> hands the wBond editor push-in and pop-out, so the active view model is
    /// no longer the same object for the life of the document. XAML bindings through
    /// <c>ActiveViewModel</c> re-point themselves; these subscriptions do not.</para>
    /// </summary>
    private void RebindHostedLayout()
    {
        if (_hostedDoc is not null) _hostedDoc.ActiveViewModelChanged -= OnHostedFrameChanged;

        _hostedDoc = _bound?.LayoutDocument;
        if (_hostedDoc is not null) _hostedDoc.ActiveViewModelChanged += OnHostedFrameChanged;

        RebindActiveLayoutFrame();
    }

    private LayoutDocument? _hostedDoc;

    private void OnHostedFrameChanged(object? sender, EventArgs e) => RebindActiveLayoutFrame();

    /// <summary>
    /// Follows the active frame's SNAP — the pitch both canvases draw their grid from and the step
    /// wire points land on — its armed TOOL, and the descent chain the wires have to be drawn through.
    ///
    /// <para>One snap control governs both views deliberately: a visible grid the wires ignored — or
    /// two grids at different pitches in two views of the same wires — would be worse than none.</para>
    /// </summary>
    private void RebindActiveLayoutFrame()
    {
        if (_activeLayoutVm is not null) _activeLayoutVm.PropertyChanged -= OnLayoutVmPropertyChanged;

        _activeLayoutVm = _hostedDoc?.ActiveViewModel;
        if (_activeLayoutVm is not null) _activeLayoutVm.PropertyChanged += OnLayoutVmPropertyChanged;

        WireLayoutToolExclusion();
        PushDescentChain();
        PushSnapPitch();
    }

    private LayoutEditorViewModel? _activeLayoutVm;

    // ── Whose gesture is it: the wire tools' or the layout tools'? ────────────
    //
    // The wire overlay is offered every left press before the layout editor sees it
    // (LayoutCanvas.OnPointerPressed), so an armed LAYOUT tool has to MEAN something to the overlay —
    // otherwise arming Rectangle on the hosted toolbar would silently start a wire marquee and the
    // tool would appear to do nothing. Two rules, both here, and this is ALL that survives of the
    // transcribed toolbar row WB39a deleted:
    //
    //   • An armed layout tool takes the canvas (WBondLayoutOverlay.LayoutToolArmed). The overlay
    //     still handles a press that lands ON a wire, so a wire stays clickable; it simply stops
    //     claiming empty space.
    //   • Arming one disarms the other. Draw Wire and Rectangle are both "the next click places
    //     something", and two of those armed at once is a state with no correct behaviour.

    /// <summary>Connects the two toolbars' tool states, in both directions, for the active frame.</summary>
    private void WireLayoutToolExclusion()
    {
        if (_toolExclusionVm is not null) _toolExclusionVm.PropertyChanged -= OnLayoutToolChanged;

        _toolExclusionVm = _activeLayoutVm;
        if (_toolExclusionVm is not null) _toolExclusionVm.PropertyChanged += OnLayoutToolChanged;

        if (_bound is not null)
            _bound.Overlay.LayoutToolArmed = () =>
                _activeLayoutVm is { } layout && layout.ActiveTool != LayoutEditorViewModel.Tool.Select;
    }

    private LayoutEditorViewModel? _toolExclusionVm;

    /// <summary>
    /// A layout tool was armed on the hosted toolbar — so the WIRE tool goes back to Select, and both
    /// canvases repaint so the wBond toolbar's highlight moves with it.
    /// </summary>
    private void OnLayoutToolChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(LayoutEditorViewModel.ActiveTool)) return;
        if (_bound is null) return;

        if (_activeLayoutVm?.ActiveTool != LayoutEditorViewModel.Tool.Select
            && _bound.ActiveTool != WBondTool.Select)
            _bound.ActiveTool = WBondTool.Select;

        RepaintBoth();
    }

    /// <summary>The converse: a WIRE tool was armed, so the layout tool goes back to Select.</summary>
    private void DisarmLayoutTool()
    {
        if (_activeLayoutVm is { } layout && layout.ActiveTool != LayoutEditorViewModel.Tool.Select)
            layout.ActiveTool = LayoutEditorViewModel.Tool.Select;
    }

    /// <summary>
    /// Select All means everything selectable in this editor — every wire AND every piece of layout
    /// geometry — because the two are one design as far as the user is concerned. Reached from the
    /// standalone binary's Edit menu; the canvas's own right-click reaches the same pair through
    /// <c>WBondLayoutOverlay.BuildContextMenuItems</c>.
    /// </summary>
    internal void SelectAllIncludingWires()
    {
        if (_bound is null) return;

        _bound.Editor.SelectAllWires();
        _activeLayoutVm?.SelectAllCommand.Execute(null);
        RepaintBoth();
    }

    /// <summary>
    /// WB27 — pushed into a sub-cell, the wires are drawn in that cell's own frame, dimmed and locked.
    ///
    /// <para>This is the wiring that milestone always needed and never had: before the wBond editor
    /// hosted <c>LayoutEditorView</c> there was no push-in here to trigger it, so
    /// <c>WBondDescent</c> was reachable only from its tests. <c>CanPlace</c> answers false for a
    /// descent that cannot be composed exactly, and the overlay then draws nothing at all — a wire
    /// foot at a silently wrong offset is worse than no wire, because judging where it sits relative
    /// to the pad under it is the whole reason to be down there.</para>
    /// </summary>
    private void PushDescentChain()
    {
        if (_bound is null) return;

        if (_hostedDoc is not { } doc)
        {
            _bound.Overlay.DescentChain = [];
            _bound.Overlay.CanPlaceAtDepth = true;
            return;
        }

        _bound.Overlay.DescentChain = doc.DescentChain;
        _bound.Overlay.CanPlaceAtDepth =
            WBondDescent.CanPlace(doc, doc.ViewModel.Model, doc.ActiveViewModel.Model);

        HostedLayoutView.InvalidateOverlay();
    }

    private void OnLayoutVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LayoutEditorViewModel.SnapDbu)) PushSnapPitch();
    }

    private void PushSnapPitch()
    {
        int dbuPerMicron = _activeLayoutVm?.Model.DbuPerMicron ?? LayoutUnits.DefaultDbuPerMicron;
        long snapDbu = _activeLayoutVm?.SnapDbu ?? 0;
        long pitchNm = snapDbu > 0 ? WBondSnap.ToNm(snapDbu, dbuPerMicron) : 0;

        ProfileView.GridPitchNm = pitchNm;
        if (_bound is not null) _bound.Overlay.GridPitchNm = pitchNm;

        ProfileView.Repaint();
        HostedLayoutView.InvalidateOverlay();
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
        else if (e.PropertyName == nameof(WBondDocumentViewModel.LayoutDocument))
            RebindHostedLayout();
    }

    private void OnOverlayChanged()
    {
        // The wires moved; the layout did not. Repaint without touching LayoutPathCache.
        HostedLayoutView.InvalidateOverlay();
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

        ProfileView.IsVisible = profile;
        HostedLayoutView.IsVisible = layout;   // toolbar, breadcrumbs, canvas, rulers and all
        CanvasSplitter.IsVisible = split;

        var panelColumn = RootGrid.ColumnDefinitions[0];
        var panelSplitColumn = RootGrid.ColumnDefinitions[1];

        if (panelColumn.Width.Value > 0) _panelColumnSize = panelColumn.Width;

        panelColumn.Width = _bound.PanelVisible ? _panelColumnSize : new GridLength(0);
        panelSplitColumn.Width = _bound.PanelVisible ? new GridLength(4) : new GridLength(0);
        InductancePanel.IsVisible = _bound.PanelVisible;
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

        if (_bound.ProfileVisible)
        {
            if (ProfileView.CanvasIsVisible) ProfileView.FocusCanvas();
        }
        else if (HostedLayoutView.IsEffectivelyVisible) HostedLayoutView.FocusCanvas();
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
        ProfileView.ZoomToFit();
        HostedLayoutView.ZoomCanvasToFit();
    }

    private void OnZoomIn(object? sender, RoutedEventArgs e) => ForEachVisibleCanvas(
        ProfileView.ZoomIn, HostedLayoutView.ZoomCanvasIn);

    private void OnZoomOut(object? sender, RoutedEventArgs e) => ForEachVisibleCanvas(
        ProfileView.ZoomOut, HostedLayoutView.ZoomCanvasOut);

    private void OnZoom1To1(object? sender, RoutedEventArgs e) => ForEachVisibleCanvas(
        () => ProfileView.Zoom1To1(_bound?.Editor.DisplayUnit ?? WBondUnit.Mil),
        HostedLayoutView.ZoomCanvas1To1);

    private void ForEachVisibleCanvas(Action profile, Action layout)
    {
        if (_bound?.ProfileVisible != false) profile();
        if (_bound?.LayoutVisible != false) layout();
    }

    // The Snap ladder and the Unit picker were this editor's own once, transcribed from the Layout
    // Editor's metadata bar. Both are the hosted LayoutEditorView's now (WB39a) — its bar carries
    // technology, unit, snap, shape and instance counts, extent and cursor, and a second bar
    // underneath restating three of them would disagree with it the first time either was touched.
    // What the wBond side still needs from the snap is the GRID PITCH, which PushSnapPitch reads
    // straight off the active frame.

    // View->Zoom to Fit dispatches here from WorkspaceViewModel via WBondDocument.RequestZoomToFit().
    // A wBond editor shows two canvases at once; fit both, mirroring the "Fit profile view" button
    // for the profile side and the ordinary layout Zoom to Fit for the artwork side.
    private void OnZoomToFitRequestedFromMenu()
    {
        ProfileView.ZoomToFit();
        HostedLayoutView.ZoomCanvasToFit();
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
        ProfileView.Thickness = mode;
        if (_bound is not null) _bound.Overlay.Thickness = mode;

        ProfileView.Repaint();
        HostedLayoutView.InvalidateOverlay();
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
        HostedLayoutView.InvalidateOverlay();
        ProfileView.Repaint();
    }

}

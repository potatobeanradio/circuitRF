using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Styling;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Controls;

/// <summary>
/// The stackup cross-section, in the Technology Editor's Stackup tab, above the cards.
///
/// <h3>It draws, it SELECTS, and it hosts two gestures that edit THROUGH the view model</h3>
/// <para>brief 2 was read-only; brief 3 added the click; brief 4 the double-click that opens an
/// inline editor; brief 5 the drags — reorder a band, re-span a via, slide one sideways. None of them
/// writes <c>Working.Stackup.Layers</c> here and none pushes an undo entry of its own: every one goes
/// through the function the card below already calls, which is the rule the whole series is built on
/// (series overview §0). The placement arithmetic they read is <see cref="StackupScene"/>'s, and the
/// drag STATE MACHINE is <see cref="StackupDragController"/>'s; this control is the hosting.</para>
///
/// <h3>Selection is the VIEW MODEL's, held by name</h3>
/// <para>This control neither owns nor caches what is selected. It writes
/// <see cref="TechEditorViewModel.SelectedStackupLayerName"/> and reads it back, because the card
/// list below is looking at the same property — and because a reference to a band or a row VM would
/// not survive the next committed edit (R-stk3-1).</para>
///
/// <h3>What it is, structurally</h3>
/// <para>A <c>Control</c> on <c>WBondProfileCanvas</c>'s pattern: an <see cref="ICustomDrawOperation"/>
/// leases the Skia canvas and hands it to <c>StackupRenderer.Draw</c>. Everything that decides WHERE
/// anything goes is in <see cref="StackupScene"/>, below the firewall, and is shared with the
/// clipboard export and the documentation figures. There is no second renderer and no second
/// placement arithmetic.</para>
///
/// <h3>It measures to the scene, which is what makes the pane scroll</h3>
/// <para><see cref="MeasureOverride"/> returns the scene's INTRINSIC height (brief 1 §2) against the
/// pane's width. Nothing compresses the drawing to fit a viewport — a tall stack is a tall drawing
/// and the <c>ScrollViewer</c> around it scrolls. The width is taken as given, which is why that
/// <c>ScrollViewer</c> has its horizontal bar disabled: a horizontal scrollbar here would mean the
/// scene got its own width wrong.</para>
/// </summary>
public sealed partial class StackupCanvas : Control
{
    /// <summary>The width to lay a scene out at when the measure constraint gives none — a design
    /// surface, a measure outside a <c>ScrollViewer</c> with a horizontal bar. Wide enough for the
    /// two-column layout rather than the narrow fallback, so a canvas with no constraint does not
    /// look broken.</summary>
    public const double FallbackWidth = 600;

    // ── ViewModel ─────────────────────────────────────────────────────────────────────────────────

    public static readonly DirectProperty<StackupCanvas, TechEditorViewModel?> ViewModelProperty =
        AvaloniaProperty.RegisterDirect<StackupCanvas, TechEditorViewModel?>(
            nameof(ViewModel), o => o.ViewModel, (o, v) => o.ViewModel = v);

    private readonly StackupSceneCache _cache = new();
    private readonly StackupDragController _drag = new();
    private TechEditorViewModel? _viewModel;
    private bool _hooked;

    public TechEditorViewModel? ViewModel
    {
        get => _viewModel;
        set
        {
            if (ReferenceEquals(_viewModel, value)) return;

            // Unhooked FIRST, on the OLD view model, and never on the new one. A handler left on a
            // technology this canvas no longer shows repaints it for edits nobody here can see, and
            // keeps that editor alive — the bug WBondProfileCanvas's own setter carries a comment
            // about, and the same one R-stk2-2 states for the theme handler.
            Unhook();

            SetAndRaise(ViewModelProperty, ref _viewModel, value);

            Hook();
            _cache.Invalidate();
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    /// <summary>
    /// Resolved from <c>ThemeService</c> on attach and on every theme change, exactly as
    /// <c>WBondProfileCanvas.RefreshTheme</c> resolves its own. Settable, for a caller with no visual
    /// tree.
    ///
    /// <para><b>Not called <c>Theme</c></b>, which brief 2 §1 spelled it: <c>StyledElement.Theme</c>
    /// already exists and is the STYLING theme (a <c>ControlTheme</c>). Shadowing it with <c>new</c>
    /// would leave a <c>Theme="…"</c> attribute in any .axaml meaning whichever of the two the
    /// compiler picked. <c>WBondProfileCanvas</c> hit the same wall and answered it the same way —
    /// <c>WireTheme</c>, <c>LayoutTheme</c>.</para>
    /// </summary>
    public StackupRenderTheme StackupTheme { get; set; } = StackupRenderTheme.Fallback;

    public StackupCanvas()
    {
        // STILL not Focusable, though this control now handles a pointer — R-stk2-10 has not moved.
        // A focusable canvas inside the drawing's ScrollViewer re-points Page Up/Page Down at the
        // drawing, because TechEditorView's TargetScrollViewer walks up from whatever holds focus;
        // those keys scroll the CARD list and must keep scrolling it. Pointer input needs no focus.
        //
        // Esc therefore cannot be a key handler HERE. It is a tunnelling handler on the view instead,
        // which is the half of R-stk3-9 that was going to be needed anyway ("pressing Esc will
        // unselect", not "pressing Esc while the drawing happens to have focus") and which covers the
        // canvas as well as the card list. See src/Ui/RESOLVED.md.
        Focusable = false;
        ClipToBounds = true;

        ActualThemeVariantChanged += (_, _) => RefreshTheme();
    }

    // ── Theme (R-stk2-2) ──────────────────────────────────────────────────────────────────────────

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ThemeService.ThemeChanged += OnThemeChanged;
        RefreshTheme();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        ThemeService.ThemeChanged -= OnThemeChanged;
    }

    private void OnThemeChanged(object? sender, EventArgs e) => RefreshTheme();

    private void RefreshTheme()
    {
        var variant = ActualThemeVariant == ThemeVariant.Dark ? ColorVariant.Dark : ColorVariant.Light;
        StackupTheme = StackupRenderTheme.FromTheme(ThemeService.Active, variant);
        InvalidateVisual();
    }

    // ── Live repaint (R-stk2-3) ───────────────────────────────────────────────────────────────────

    private void Hook()
    {
        if (_hooked || _viewModel is null) return;
        _viewModel.StackupChanged += OnStackupChanged;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _hooked = true;
    }

    private void Unhook()
    {
        if (!_hooked || _viewModel is null) return;
        _viewModel.StackupChanged -= OnStackupChanged;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _hooked = false;
    }

    /// <summary>
    /// A selection change repaints and NOTHING ELSE — no measure, no scene rebuild.
    ///
    /// <para>The outline is overlay chrome laid over rects the scene already placed (R-stk1-1), so
    /// selecting a band changes not one thing about the layout. Invalidating measure here would drop
    /// and rebuild the scene on every click, and on a large stackup that is the whole layout pass for
    /// a two-pixel outline.</para>
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TechEditorViewModel.SelectedStackupLayerName)
                           or nameof(TechEditorViewModel.SelectedFilmLayerKey))
            InvalidateVisual();
    }

    /// <summary>
    /// MEASURE as well as visual, and that is the whole of it: a thickness edit changes the scene's
    /// HEIGHT, and a visual-only invalidation would repaint new bands inside the old height — the
    /// drawing correct and the scrollbar lying about it.
    /// </summary>
    private void OnStackupChanged()
    {
        // R-stk4-8, FIRST: an undo, a redo, an edit committed from the card below — every one of them
        // rebuilds the scene, and a box left open is pointing at a rect that no longer exists. A
        // commit made through the box has already closed it (R-stk4-7), so this is a no-op there.
        _inlineEdit?.Close();

        // The same reason, for the drag: a committed edit, an undo or a redo rebuilds the scene, and
        // a ghost or an insertion line left over from a gesture is chrome pointing at rects that no
        // longer exist. A gesture that committed has already reset itself, so this is a no-op there.
        _drag.Cancel();

        _cache.Invalidate();
        InvalidateMeasure();
        InvalidateVisual();
    }

    /// <summary>The scene this canvas is holding. Brief 3's pointer handlers read it — the same one
    /// the renderer drew, which is R-stk1-1 — and the tests read it because there is no headless
    /// render here to read instead.</summary>
    internal StackupSceneCache SceneCache => _cache;

    /// <summary><see cref="MeasureOverride"/> at a stated width, reachable without a layout pass.</summary>
    internal Size MeasureForWidth(double width) => MeasureOverride(new Size(width, double.PositiveInfinity));

    // ── Pointer: click selects, hover marks (R-stk3-2, R-stk3-4) ──────────────────────────────────

    /// <summary>
    /// <b>The switch that turns hover highlighting off, and it is off.</b>
    ///
    /// <para>Owner, 2026-09-13: remove the mouse-over highlighting — keep the code, gate it. So this
    /// is the one thing that decides, and it is read in exactly two places:
    /// <see cref="Overlay"/> stops publishing what the pointer is over, and <see cref="MoveAt"/>
    /// stops tracking it at all so a mouse sweep costs no repaints. Everything downstream —
    /// <c>StackupOverlay.HoverLayer</c>, <c>StackupRenderer</c>'s lighter outline, the grippers a
    /// hovered via revealed — is untouched and comes back by setting this true.</para>
    ///
    /// <para>What R-stk3-4 argued for is not wrong and is worth keeping written down: without hover
    /// the first click on this drawing is a guess, because nothing says it is clickable. What
    /// replaced it is the selection outline, which says what the drawing is ABOUT rather than where a
    /// pointer happens to be — and a via's grippers therefore appear on SELECTION, which is one click
    /// and is a rule that holds whether or not a pointer is over the drawing at all.</para>
    ///
    /// <para>A settable static rather than a <c>const</c>: a const would make every guarded branch
    /// unreachable, which this solution treats as an error, and — more to the point — it would make
    /// the kept code untestable. The gate's own tests flip it.</para>
    /// </summary>
    internal static bool HoverHighlighting { get; set; }

    /// <summary>The layer under the pointer. Canvas-local and deliberately NOT on the view model:
    /// hover is a property of one pointer over one drawing, it survives nothing and nobody else reads
    /// it, whereas the SELECTION is shared with the card list and is the view model's.</summary>
    private string? _hoverLayer;

    /// <summary>What the renderer is told about this frame's transient state. Rebuilt per paint from
    /// the two pieces of state above, so there is no third copy to keep in step.</summary>
    private StackupOverlay Overlay => _drag.Decorate(new StackupOverlay
    {
        HoverLayer    = HoverHighlighting ? _hoverLayer : null,
        SelectedLayer = _viewModel?.SelectedStackupLayerName,
        SelectedFilm  = _viewModel?.SelectedFilmLayerKey,
    });

    /// <summary>
    /// The scene coordinates of a pointer event.
    ///
    /// <para>The scene is drawn at the control's own origin (<see cref="Render"/> hands the draw
    /// operation <c>Bounds.Size</c> and the renderer draws in scene coordinates from 0,0), so a
    /// position relative to this control IS a scene position. No transform, and no second one to keep
    /// in step with the renderer's.</para>
    /// </summary>
    private StackupHit? HitAt(Point p) => SceneNow().HitTest((float)p.X, (float)p.Y);

    /// <summary>The scene this canvas is showing right now — the one the renderer drew, per R-stk1-1,
    /// which is what makes a hit-test and a drag answer about the picture the user is looking at.</summary>
    private StackupScene SceneNow()
        => _cache.Current ?? _cache.Get(_viewModel?.Working, (float)Bounds.Width);

    /// <summary>
    /// R-stk3-2. A <c>Band</c> or <c>ViaBarrel</c> hit selects that entry; a <c>Label</c> hit selects
    /// the layer the label belongs to (brief 4 makes a DOUBLE click on a value open an editor — a
    /// single click still just selects); a via's grippers belong to their via and select it.
    ///
    /// <para><b>A hit on nothing CLEARS the selection</b> — clicking the background is how a drawing
    /// surface has always meant "nothing", and a drawing where the only way to deselect is a keystroke
    /// is one the user fights.</para>
    /// </summary>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        // R-stk6-1. The right-click only SELECTS and RECORDS; it neither builds nor opens a menu.
        // The one ContextMenu instance is declared on this control in TechEditorView.axaml, and
        // Avalonia opens it itself and raises Opening — which is where ItemsSource is rebuilt.
        //
        // Deliberately NOT e.Handled = true, for LayoutCanvas's own stated reason: handling the press
        // risks suppressing Avalonia's right-click-opens-ContextMenu gesture recognition, and then
        // nothing opens at all.
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            RightClickAt(e.GetPosition(this));
            return;
        }

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var p = e.GetPosition(this);
        if (!PressAt(p)) return;

        // Captured on the PRESS, before it is known whether this becomes a drag: capture is what makes
        // the moves and the release keep arriving once the pointer leaves the control, and taking it
        // at the threshold instead would lose the first frames of a drag that left the pane. Only
        // when something was armed — a press on the background captures nothing.
        if (_drag.IsArmed) e.Pointer.Capture(this);

        // R-stk4-3. The second press of a double-click, and only the second: a single click that
        // opened an editor would be an editor the user only meant to click past — InlineEditText's
        // own doc comment records that finding. The first press has already selected, and this one
        // selects the same entry again on its way through.
        if (e.ClickCount == 2) DoubleClickAt(p);

        e.Handled = true;
    }

    /// <summary>The gesture itself, without the pointer event — the same kind of seam
    /// <see cref="MeasureForWidth"/> and <see cref="SceneCache"/> already are, and for the same
    /// reason: there is no input device in this project's tests to raise a real press with.</summary>
    /// <returns>False when there is nothing to select into (no view model).</returns>
    internal bool PressAt(Point p)
    {
        if (_viewModel is null) return false;
        var hit = HitAt(p);
        _viewModel.SelectFromDrawing(hit?.LayerName, hit?.FilmLayer);

        // R-stk5-3. ARMED, not started: below the movement threshold this whole gesture is the
        // selection above and nothing else, which is why the selection is written here on the press
        // rather than waiting to find out whether a drag happened.
        _drag.Arm(SceneNow(), (float)p.X, (float)p.Y, _viewModel.Working);
        return true;
    }

    /// <summary>
    /// R-stk3-4. Without hover the first click is a guess: nothing on this drawing says it is
    /// clickable at all. A hovered band takes a lighter outline and a hovered via reveals brief 1's
    /// grippers, which the scene has already placed whether or not a frame drew them.
    /// </summary>
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        MoveAt(e.GetPosition(this), e.KeyModifiers.HasFlag(KeyModifiers.Alt));
    }

    /// <summary>The hover half of the same seam. <see cref="HoverLayer"/> reads back what it decided.</summary>
    internal void MoveAt(Point p, bool freeLane = false)
    {
        // A live drag FREEZES the hover mark. Hover says "this is what a click would take", and while
        // the pointer is dragging one thing across others that sentence is false — a hover outline
        // chasing the pointer across bands the drag is passing over reads as a second selection.
        if (_drag.Update(SceneNow(), (float)p.X, (float)p.Y, freeLane))
        {
            ShowDragRefusal();
            InvalidateVisual();
            return;
        }
        if (_drag.IsDragging) return;

        // With the switch off, a pointer crossing the drawing does nothing at all — not even the
        // hit-test. The tracking is what costs; the overlay already refuses to publish it.
        if (!HoverHighlighting) return;

        SetHover(HitAt(p)?.LayerName);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.InitialPressMouseButton != MouseButton.Left) return;

        bool wrote = ReleaseAt(e.GetPosition(this));

        // AFTER the gesture, not before: dropping capture raises PointerCaptureLost, which cancels —
        // and Avalonia raises this event while the capture is still held, which is what makes the
        // order work (the same sequence HarmonicaCanvas uses).
        if (ReferenceEquals(e.Pointer.Captured, this)) e.Pointer.Capture(null);
        if (wrote) e.Handled = true;
    }

    /// <summary>
    /// R-stk5-2/R-stk5-6's commit point, and R-stk5-3's "releasing outside the canvas cancels".
    ///
    /// <para>The pointer is captured for the duration of a drag, so a release beyond the control's
    /// own bounds still arrives here — which is exactly the case that has to cancel rather than
    /// commit, because the drop target the user was aiming at is not on this drawing.</para>
    /// </summary>
    /// <returns>True when the gesture wrote something.</returns>
    internal bool ReleaseAt(Point p)
    {
        // Against the SCENE's extent and not Bounds. The two are the same rectangle by construction —
        // MeasureOverride returns the scene's own width and height — and the scene is the one of the
        // pair that exists before a layout pass, so the gesture answers the same question in the app
        // and in a gate that never arranged anything.
        var scene = SceneNow();
        bool inside = p.X >= 0 && p.Y >= 0 && p.X <= scene.Width && p.Y <= scene.Height;
        bool wrote = false;

        if (inside && _viewModel is not null && _drag.IsDragging) wrote = _drag.Release(_viewModel);
        else _drag.Cancel();
        ShowDragRefusal();

        // A committed drag repaints through StackupChanged; a cancelled one has to drop its own
        // chrome, and there is nothing else to raise it.
        if (!wrote) InvalidateVisual();
        return wrote;
    }

    /// <summary>R-stk5-3's <c>Esc</c>, and the pointer-capture-lost case. Returns true when there was
    /// a gesture to abandon — which is what tells <c>TechEditorView.OnEscapeKeyDown</c> to stop here
    /// rather than fall through to brief 3's clear-selection.</summary>
    internal bool CancelDrag()
    {
        if (!_drag.Cancel()) return false;
        ShowDragRefusal();
        InvalidateVisual();
        return true;
    }

    /// <summary>A refused drop's tooltip, whose words are the binding's own and never a second wording.</summary>
    private void ShowDragRefusal()
    {
        string? why = _drag.RefusalReason is { Length: > 0 } reason ? reason : null;
        if (VisualRoot is null) return;
        ToolTip.SetTip(this, why);
        ToolTip.SetIsOpen(this, why is not null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        CancelDrag();
    }

    /// <summary>The live gesture, for the gate.</summary>
    internal StackupDragKind DragKind => _drag.Kind;

    /// <summary>What a release would do right now, for the gate.</summary>
    internal StackupDragController Drag => _drag;

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        SetHover(null);
    }

    /// <summary>Repaints only when the hovered layer actually CHANGED — a pointer crossing a band
    /// raises a move event per pixel, and repainting the whole drawing for each of them would make a
    /// mouse sweep the most expensive thing this tab does.</summary>
    private void SetHover(string? name)
    {
        if (string.Equals(_hoverLayer, name, StringComparison.Ordinal)) return;
        _hoverLayer = name;
        InvalidateVisual();
    }

    /// <summary>What the pointer is over, for the tests and for nobody else — the renderer is handed
    /// <see cref="Overlay"/>, which is built from this.</summary>
    internal string? HoverLayer => _hoverLayer;

    // ── The inline editor (brief 4) ───────────────────────────────────────────────────────────────

    private StackupInlineEditor? _inlineEdit;

    /// <summary>
    /// The box this canvas opens over a double-clicked value, handed in by the host.
    ///
    /// <para>The BOX is the host's — it is a sibling in the host's panel, because a box floated over
    /// this control has to be laid out beside it rather than inside it, and because the host is what
    /// wires the three-key contract to it. What the canvas owns is the half that needs the scene:
    /// which label was hit, what it seeds from and where exactly it goes.</para>
    /// </summary>
    internal StackupInlineEditor? InlineEditor
    {
        get => _inlineEdit;
        set => _inlineEdit = value;
    }

    /// <summary>Whether an inline editor is open over this drawing — the gate
    /// <c>TechEditorView.OnEscapeKeyDown</c> checks so that <c>Esc</c> reverts the edit before it
    /// clears the selection (R-stk4-6).</summary>
    internal bool InlineEditIsOpen => _inlineEdit?.IsOpen == true;

    /// <summary>
    /// R-stk4-3's gesture, without the pointer event — the same seam <see cref="PressAt"/> is, and
    /// for the same reason.
    /// </summary>
    /// <returns>True when a box was opened.</returns>
    internal bool DoubleClickAt(Point p)
    {
        if (_inlineEdit is null || _viewModel is null) return false;
        var scene = _cache.Current ?? _cache.Get(_viewModel.Working, (float)Bounds.Width);
        return _inlineEdit.TryOpen(scene, _viewModel, (float)p.X, (float)p.Y);
    }

    /// <summary>The overlay this canvas would hand the renderer right now.</summary>
    internal StackupOverlay CurrentOverlay => Overlay;

    // ── Measure and draw ──────────────────────────────────────────────────────────────────────────

    protected override Size MeasureOverride(Size available)
    {
        double w = available.Width;
        if (double.IsNaN(w) || double.IsInfinity(w) || w <= 0) w = FallbackWidth;

        var scene = _cache.Get(_viewModel?.Working, (float)w);
        return new Size(w, scene.Height);
    }

    public override void Render(DrawingContext context)
    {
        // Current, not a fresh build: MeasureOverride has already run this layout pass and the scene
        // it produced is the one whose height the ScrollViewer is scrolling. Building a second one
        // here — at Bounds.Width, which is not necessarily the measured width — is how a picture and
        // a scrollbar come to disagree.
        var scene = _cache.Current ?? _cache.Get(_viewModel?.Working, (float)Bounds.Width);
        context.Custom(new StackupDrawOperation(new Rect(Bounds.Size), scene, StackupTheme, Overlay));
    }

    // ── ICustomDrawOperation ──────────────────────────────────────────────────────────────────────

    private sealed class StackupDrawOperation : ICustomDrawOperation
    {
        private readonly Rect _bounds;
        private readonly StackupScene _scene;
        private readonly StackupRenderTheme _theme;
        private readonly StackupOverlay _overlay;

        public StackupDrawOperation(
            Rect bounds, StackupScene scene, StackupRenderTheme theme, StackupOverlay overlay)
        {
            _bounds = bounds;
            _scene = scene;
            _theme = theme;
            _overlay = overlay;
        }

        /// <summary>Never equal, so a repaint is never skipped — the same answer
        /// <c>WBondProfileCanvas.ProfileDrawOperation</c> gives, for the same reason: the scene is a
        /// reference and comparing two of them here would re-derive what the cache already knows.</summary>
        public bool Equals(ICustomDrawOperation? other) => false;

        public Rect Bounds => _bounds;
        public bool HitTest(Point p) => _bounds.Contains(p);
        public void Dispose() { }

        public void Render(ImmediateDrawingContext context)
        {
            var leaseFeature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (leaseFeature is null) return;

            using var lease = leaseFeature.Lease();
            StackupRenderer.Draw(lease.SkCanvas, _scene, _theme, _overlay);
        }
    }
}

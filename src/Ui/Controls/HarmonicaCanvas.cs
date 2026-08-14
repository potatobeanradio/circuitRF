using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.VisualTree;
using CircuitRF.Harmonica;
using CircuitRF.Ui.Harmonica;
using CircuitRF.Ui.Harmonica.Renderers;
using CircuitRF.Ui.Renderers;
using SkiaSharp;

namespace CircuitRF.Ui.Controls;

/// <summary>
/// The harmonicaRF document canvas: the §7.1 panels, laid out from <see cref="CharmLayout"/> and
/// drawn with <see cref="HarmonicaPanelRenderer"/>.
///
/// <para>Same host pattern as <c>SchematicCanvas</c> / <c>LayoutCanvas</c> —
/// <c>Control.Render → ICustomDrawOperation → ISkiaSharpApiLease</c>, never <c>SKCanvasView</c>,
/// which is not composited correctly in Avalonia 11+.</para>
///
/// <para><b>ClipToBounds must stay set on this control</b> (it is, in the AXAML). Avalonia hands a
/// custom draw operation the WHOLE render-surface canvas, and <c>ICustomDrawOperation.Bounds</c> does
/// NOT clip Skia — the same trap the layout renderer already documents. The renderer additionally
/// clips per panel, so this is belt and braces rather than the only guard.</para>
///
/// <para><b>The layout is DATA</b> (R-h45-1): each panel's rect comes from the document's
/// <see cref="CharmLayout"/>, in fractions, so H7's Edit Display has something to unlock rather than
/// something to rewrite. Nothing here hardcodes where a panel goes.</para>
/// </summary>
public sealed class HarmonicaCanvas : Control
{
    // R-h9b-2 — without this, OnKeyDown never fires: Escape-cancels-a-drag and Delete-removes-the-
    // panel-under-the-pointer (§7.7) were both unreachable, and HarmonicaView.FocusCanvas()'s
    // Canvas.Focus() was a no-op. SchematicCanvas / LayoutCanvas both set this in their constructors;
    // this control had none at all.
    public HarmonicaCanvas() => Focusable = true;

    // brief-harmonicarf-r4 §4 — one backdrop cache PER SMITH PANEL, owned by this control instance
    // (never static — src/Harmonica/CLAUDE.md's "no static mutable state" rule, applied here too)
    // so it survives across Render() calls even though HarmonicaDrawOperation itself is rebuilt fresh
    // every frame. Copy Plot / export never touches these — HarmonicaCanvasRenderer.Snapshot.Of(vm)
    // alone (no WithBackdropCaches) is what those call, on purpose.
    private readonly HarmonicaBackdropCache _powerBackdrop      = new();
    private readonly HarmonicaBackdropCache _efficiencyBackdrop = new();

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        _powerBackdrop.Dispose();
        _efficiencyBackdrop.Dispose();
        base.OnDetachedFromVisualTree(e);
    }

    public static readonly DirectProperty<HarmonicaCanvas, HarmonicaViewModel?> ViewModelProperty =
        AvaloniaProperty.RegisterDirect<HarmonicaCanvas, HarmonicaViewModel?>(
            nameof(ViewModel), o => o.ViewModel, (o, v) => o.ViewModel = v);

    private HarmonicaViewModel? _vm;

    public HarmonicaViewModel? ViewModel
    {
        get => _vm;
        set
        {
            if (ReferenceEquals(_vm, value)) return;
            if (_vm is not null) _vm.RedrawRequested -= OnRedrawRequested;
            SetAndRaise(ViewModelProperty, ref _vm, value);
            if (_vm is not null) _vm.RedrawRequested += OnRedrawRequested;
            InvalidateVisual();
        }
    }

    private void OnRedrawRequested() => Avalonia.Threading.Dispatcher.UIThread.Post(InvalidateVisual);

    // ── M1 — the pointer gesture (brief-harmonicarf-h6) ──────────────────────

    private HarmonicaGesture? _gesture;

    /// <summary>
    /// The gesture this canvas drives. Created on demand against the current view model, so the two
    /// can never be out of step; a test drives the SAME class directly with synthetic coordinates.
    /// </summary>
    public HarmonicaGesture? Gesture => _vm is null ? null : _gesture ??= new HarmonicaGesture(_vm);

    /// <summary>R-h6-2 — device pixels per DIP, read PER EVENT rather than cached. Moving a window
    /// between a Retina display and an external monitor changes it mid-session. Public so the
    /// context-menu builder (R-h9r2-6) can resolve a right-click through the SAME
    /// <see cref="HarmonicaHitTest.Resolve"/> a drag uses, at the same radius.</summary>
    public double RenderScaling => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;

    /// <summary>
    /// R-h9b-10/12 — where the last right-click landed, in this canvas's own coordinates. Only
    /// RECORDED here, mirroring <c>LayoutCanvas.ContextMenuTarget</c>'s own pattern: the single
    /// <c>ContextMenu</c> declared once in <c>HarmonicaView.axaml</c> reads it via
    /// <see cref="ConsumeContextMenuTarget"/> on <c>Opening</c> and builds its items fresh, so two
    /// right-click gestures (the power-sweep X-axis unit picker and the DCIV Sweeps dialog) share one
    /// context menu instance rather than each popping its own.
    /// </summary>
    public Avalonia.Point? ContextMenuTarget { get; private set; }

    /// <summary>Consumes <see cref="ContextMenuTarget"/> — read once per menu opening, the same
    /// one-shot contract <c>LayoutCanvas.ConsumeContextMenuTarget</c> uses.</summary>
    public Avalonia.Point? ConsumeContextMenuTarget()
    {
        var t = ContextMenuTarget;
        ContextMenuTarget = null;
        return t;
    }

    protected override void OnPointerPressed(Avalonia.Input.PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Gesture is not { } g || Bounds.Width <= 0) return;

        var p = e.GetPosition(this);

        // R-h9b-10 — a right-click must not begin a drag. Deliberately NOT e.Handled = true: that
        // would risk suppressing Avalonia's own right-click-opens-ContextMenu gesture recognition,
        // the same reason LayoutCanvas's own right-click branch leaves it unhandled.
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            ContextMenuTarget = p;
            return;
        }

        if (g.PointerDown(p.X, p.Y, Bounds.Width, Bounds.Height, RenderScaling))
        {
            e.Pointer.Capture(this);
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(Avalonia.Input.PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        // Tracked on every move, not only on enter: Delete and Copy Plot both name "the panel under
        // the pointer", and a position captured once at the edge of the canvas is the wrong one for
        // both of them the moment the pointer goes anywhere.
        _lastPointer = e.GetPosition(this);

        if (Gesture is not { IsLive: true } g) return;

        var p = _lastPointer;
        g.PointerMoved(p.X, p.Y, Bounds.Width, Bounds.Height, RenderScaling);
        e.Handled = true;
    }

    protected override void OnPointerReleased(Avalonia.Input.PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (Gesture is not { IsLive: true } g) return;

        var p = e.GetPosition(this);
        g.PointerUp(p.X, p.Y, Bounds.Width, Bounds.Height, RenderScaling);
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(Avalonia.Input.PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        Gesture?.Cancel();
    }

    /// <summary>Where the pointer last was, so Delete can name a panel without one being "selected".
    /// Edit Display has no selection model — a modeless one is what §7.7's toolbar describes.</summary>
    private Avalonia.Point _lastPointer;

    protected override void OnPointerEntered(Avalonia.Input.PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        _lastPointer = e.GetPosition(this);
    }

    /// <summary>
    /// §7.7's <i>delete</i>. Escape cancels a drag in progress; Delete removes the panel under the
    /// pointer while unlocked — and is a whole gesture, so it is one undo entry like a drag.
    /// </summary>
    protected override void OnKeyDown(Avalonia.Input.KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_vm is null) return;

        if (e.Key == Avalonia.Input.Key.Escape && Gesture is { } g && (g.IsDragging || g.EditGrab != HarmonicaEditGrab.None))
        {
            g.Cancel();
            e.Handled = true;
            return;
        }

        if (e.Key is not (Avalonia.Input.Key.Delete or Avalonia.Input.Key.Back)) return;
        if (!_vm.EditDisplay.Unlocked || Bounds.Width <= 0) return;

        var (kind, panelId) = HarmonicaEditTarget.Resolve(
            _vm.Layout, [.. _vm.PickedTraces], _lastPointer.X, _lastPointer.Y,
            Bounds.Width, Bounds.Height, RenderScaling);
        if (kind == HarmonicaEditGrab.None) return;

        _vm.EditDisplay.BeginGesture();
        // A picked trace goes away entirely; a §7.1 panel only loses its placement, because the
        // four are what the document IS and there would be no way to get one back.
        var picked = _vm.PickedTraces.FirstOrDefault(t => t.PanelId == panelId);
        if (picked is not null) _vm.RemovePickedTrace(picked);
        else _vm.EditDisplay.RemovePanel(panelId);
        _vm.EditDisplay.EndGesture();

        e.Handled = true;
        InvalidateVisual();
    }

    /// <summary>
    /// The panel the pointer was last over, or null when it was over none. <i>Copy Plot</i> asks
    /// this; it deliberately reuses <see cref="HarmonicaEditTarget"/>'s own resolution rather than a
    /// second hit test, so "the panel under the pointer" cannot mean two different things depending
    /// on which gesture asked.
    /// </summary>
    public string? PanelUnderPointer()
    {
        if (_vm is null || Bounds.Width <= 0) return null;
        var (kind, panelId) = HarmonicaEditTarget.Resolve(
            _vm.Layout, [.. _vm.PickedTraces], _lastPointer.X, _lastPointer.Y,
            Bounds.Width, Bounds.Height, RenderScaling);
        return kind == HarmonicaEditGrab.None ? null : panelId;
    }

    /// <summary>Where each panel landed on the last frame, so pointer handling can resolve a hit to
    /// a panel without re-deriving the layout arithmetic.</summary>
    public Rect PanelRect(string panelId)
    {
        var p = (_vm?.Layout ?? CharmLayout.Default).PlacementOf(panelId);
        return new Rect(p.X * Bounds.Width, p.Y * Bounds.Height,
                        p.W * Bounds.Width, p.H * Bounds.Height);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return;
        context.Custom(new HarmonicaDrawOperation(new Rect(Bounds.Size), _vm,
                                                  _powerBackdrop, _efficiencyBackdrop, RenderScaling));
    }

    private sealed class HarmonicaDrawOperation(
        Rect bounds, HarmonicaViewModel? vm,
        HarmonicaBackdropCache powerBackdrop, HarmonicaBackdropCache efficiencyBackdrop, double deviceScale)
        : ICustomDrawOperation
    {
        // A SNAPSHOT of what to draw, captured at render time. The VM may change on the UI thread
        // while the operation is queued; capturing here is the same discipline every other custom
        // draw operation in this codebase follows. brief-harmonicarf-r4 §4 — the two backdrop caches
        // are the LIVE canvas's own, attached here rather than by Snapshot.Of itself, so every other
        // caller of Snapshot.Of (Copy Plot, export, tests) stays uncached exactly as before.
        private readonly HarmonicaCanvasRenderer.Snapshot _snap =
            HarmonicaCanvasRenderer.Snapshot.Of(vm).WithBackdropCaches(powerBackdrop, efficiencyBackdrop, deviceScale);
        private readonly HarmonicaViewModel? _vm = vm;

        private CharmLayout          _layout => _snap.Layout;
        private HarmonicaRenderTheme _theme  => _snap.Theme;
        private HarmonicaPickedTrace[] _picked => [.. _snap.Picked];

        // §7.7 — the Edit Display state, snapshotted with everything else.
        private readonly bool _editing = vm?.EditDisplay.Unlocked ?? false;

        public Rect Bounds { get; } = bounds;
        public void Dispose() { }
        public bool HitTest(Point p) => Bounds.Contains(p);
        public bool Equals(ICustomDrawOperation? other) => false;

        public void Render(ImmediateDrawingContext ctx)
        {
            var lease = ctx.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (lease is null) return;
            using var l = lease.Lease();
            var canvas = l.SkCanvas;

            // R-h6-4 / D6 — the RENDER stage of a FrameTiming, measured where it happens. The solver
            // fills in every other stage; this is the one it cannot see.
            var sw = System.Diagnostics.Stopwatch.StartNew();

            // Fill our OWN rect rather than canvas.Clear — Clear uses Src blend and would replace the
            // whole leased region, wiping sibling controls. This codebase has shipped that bug twice.
            HarmonicaCanvasRenderer.FillBackground(canvas, Bounds.Width, Bounds.Height, _theme);

            // ONE composer, several consumers: this control and Copy Plot's exporter draw the same
            // picture from the same place, so they cannot disagree about where a panel goes.
            HarmonicaCanvasRenderer.DrawAll(canvas, Bounds.Width, Bounds.Height, _snap);

            if (_editing) DrawEditChrome(canvas);

            if (_vm is not null) _vm.LastRenderMs = sw.Elapsed.TotalMilliseconds;
        }

        /// <summary>
        /// §7.7's edit-mode outlines and corner grips. Drawn in <c>Harmonica.EditChrome</c>, over
        /// everything, so a panel being moved is unmistakably the thing under the pointer.
        /// </summary>
        private void DrawEditChrome(SKCanvas canvas)
        {
            using var outline = new SKPaint
            {
                Style = SKPaintStyle.Stroke, StrokeWidth = 1.2f, IsAntialias = true,
                Color = _theme.EditChrome.WithAlpha(200),
                PathEffect = SKPathEffect.CreateDash([5f, 4f], 0),
            };
            using var grip = new SKPaint
            {
                Style = SKPaintStyle.Fill, IsAntialias = true, Color = _theme.EditChrome,
            };

            foreach (string id in HarmonicaEditTarget.PanelIds(_layout, _picked))
            {
                var p = _layout.PlacementOf(id);
                float x = (float)(p.X * Bounds.Width),  y = (float)(p.Y * Bounds.Height);
                float w = (float)(p.W * Bounds.Width),  h = (float)(p.H * Bounds.Height);
                if (w <= 1 || h <= 1) continue;

                canvas.DrawRect(new SKRect(x + 1, y + 1, x + w - 1, y + h - 1), outline);

                float g = (float)HarmonicaEditTarget.GripDevicePixels;
                canvas.DrawRect(new SKRect(x + w - g, y + h - g, x + w - 1, y + h - 1), grip);
            }
        }
    }
}

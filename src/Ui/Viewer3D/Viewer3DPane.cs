// brief-em3d-28 R-em3d28-1b — the 3D pane: composition GPU interop (em-3d.md §8.3), in the spike's
// InteropPane shape, which passed every counter on the owner's Mac.
//
//   * a RENDER THREAD of its own draws each frame into a free image of a three-image swapchain;
//   * the swapchain is imported into the compositor ONCE (and again only on a resize or a re-host);
//   * the UI thread's share of a frame (OnTick) is handing the finished image over and planning the
//     next — it never touches geometry and never waits on the GPU.
//
// So the compositor's share of a 3D frame is placing a finished image, and a frame still rendering or a
// problem still regenerating leaves the previous image up: the rest of the window cannot be starved by
// it (the lesson of "the whole UI crawled", src/Ui/RESOLVED.md).
//
// INPUT (R-em3d28-4a, brief-em3d-43 R-em3d43-2a): orbit = left drag; pan = middle drag, right drag or
// Alt + left drag; a click selects and Shift-click adds or removes; a right-CLICK (no drag) opens the
// selection's context menu; zoom to the cursor = wheel or pinch. The keys are the view model's
// (Viewer3DViewModel.HandleKey): O / F / V the selection mode, B / Shift+B next behind / in front, Home
// fits, P toggles the projection, 1–7 the standard views, C the clip plane, A the axis indicator, Esc
// cancels the gesture in progress or clears the selection. A trackpad's two-finger scroll zooms and its pinch zooms (macOS). No
// modal dialog opens mid-gesture (§8.2 point 5): nothing here opens a dialog at all.
//
// A present step that cannot signal is a FAULT (Viewer3DPresentFault): the pane stops, says why in
// its place, and never loops (R-em3d28-1d).

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using CircuitRF.Render.Scene3D;
using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.Viewer3D;

public sealed class Viewer3DPane : Control
{
    /// <summary>Images in the swapchain: one being drawn, one being shown, one spare.</summary>
    public const int SwapchainImages = 3;

    /// <summary>How long the render thread waits for the compositor to release an image before it
    /// counts a release timeout (reported on the status line, never retried in a loop).</summary>
    public const int ReleaseTimeoutMs = 250;

    /// <summary>A press and release closer than this (DIPs) is a click, not a drag: a mouse click or a
    /// trackpad tap routinely reports a pixel of motion.</summary>
    public const double ClickSlopDips = 3;

    /// <summary>brief-em3d-99 — how long after this window last changed size it still counts as being resized.</summary>
    public const int WindowResizeQuietMs = 200;

    /// <summary>brief-em3d-99 — how long a resize step waits for its frame before leaving it to present itself.</summary>
    public const int ResizeFrameWaitMs = 100;

    private Viewer3DViewModel? _vm;
    private Compositor? _compositor;
    private ICompositionGpuInterop? _interop;
    private CompositionDrawingSurface? _surface;
    private CompositionSurfaceVisual? _visual;
    private readonly Action _tick;
    private readonly Action<Action> _frameClock;
    private readonly Action _retryWhenDrawing;
    private bool _tickQueued;
    private int _imgW, _imgH;
    private int _attachGen;
    private TopLevel? _topLevel;

    private Thread? _thread;
    private readonly AutoResetEvent _go = new(false);
    private volatile bool _stop, _busy, _done;
    private readonly Scene3DFramePlan _plan = new();
    private Scene3DModel _planScene = Scene3DModel.Empty();
    private Scene3DOverlay _pMesh = Scene3DOverlay.None, _pSection = Scene3DOverlay.None, _pGrid = Scene3DOverlay.None;
    private CircuitRF.Render.Scene3D.Fields.Scene3DFieldGeometry _pField = CircuitRF.Render.Scene3D.Fields.Scene3DFieldGeometry.None;
    private bool _pOrbit;
    private int _nextImage, _doneImage;
    private ulong _frame, _doneValue;
    private volatile string? _fault;

    // Software present: no usable GPU interop (a remote or software-rendered display, e.g. a remote X session with
    // Mesa's software Vulkan). Each frame is rendered by the backend into an image of its own, read back, and drawn as a
    // bitmap.
    private bool _soft;
    private byte[]? _px;
    private int _pxW, _pxH;
    private Avalonia.Media.Imaging.WriteableBitmap? _bmp;

    // brief-em3d-99 — a window resize draws in the layout pass that sized the pane (DrawDuringWindowResize).
    private long _windowResizedAt;
    private int _awaited;                                     // 1: the UI thread waits for the frame in flight and presents it
    private readonly ManualResetEventSlim _finished = new(false);
    private Avalonia.Threading.DispatcherTimer? _catchUp;

    /// <summary>Frames drawn and presented inside a window-resize step (diagnostics).</summary>
    public int ResizeStepFrames { get; private set; }

    /// <summary>Why the pane is not drawing, or null while it is.</summary>
    public string? Fault => _fault;

    /// <summary>Raised on the UI thread after each presented frame — the overlay redraws its axes.</summary>
    public event Action? FramePresented;

    /// <summary>A right-click that did not move — the view's context menu. A right DRAG still pans.</summary>
    public event Action? ContextMenuRequested;

    /// <summary>Raised when the pane faults or recovers, for the view to show the reason.</summary>
    public event Action<string?>? FaultChanged;

    public int ReleaseTimeouts { get; private set; }

    public Viewer3DPane()
    {
        _tick = OnTick;
        _frameClock = FrameClock;
        _retryWhenDrawing = () => { _dirty = true; FrameClock(RequestFrame); };
        Controls.EffectiveVisibility.Observe(this, _ => UpdateShown());
        AddHandler(PointerTouchPadGestureMagnifyEvent, OnMagnify);
        ClipToBounds = true;
        Focusable = true;
        Background = Brushes.Transparent;
        // 3D round 3 — a Project Tree cell or view file dropped here places an instance (C3dTreeDrop).
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnTreeDragOver);
        AddHandler(DragDrop.DropEvent, OnTreeDrop);
        AddHandler(DragDrop.DragLeaveEvent, OnTreeDragLeave);
    }

    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        Border.BackgroundProperty.AddOwner<Viewer3DPane>();

    public IBrush? Background { get => GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }

    /// <summary>Hit-testable everywhere: the composition visual draws on top, but pointer input is ours.</summary>
    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Background ?? Brushes.Transparent, new Rect(Bounds.Size));
        if (_soft && _bmp is not null) context.DrawImage(_bmp, new Rect(Bounds.Size));
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) { _vm.FrameRequested -= RequestFrame; _vm.ReconnectRequested -= Reconnect; _vm.ShowIn(null); }
        _vm = DataContext as Viewer3DViewModel;
        if (_vm is not null)
        {
            _vm.FrameRequested += RequestFrame;
            _vm.ReconnectRequested += Reconnect;
            _vm.PaneFault = _fault;
            _vm.ShowIn(_shown ? _frameClock : null);
        }
        RequestFrame();
    }

    /// <summary>
    /// brief-idle-power — true while this pane can be seen: attached, and it and every ancestor visible. Dock keeps a background
    /// document's view attached and only hides it (EffectiveVisibility), so detaching alone was not enough: a playing field
    /// behind another tab kept rendering and presenting at display rate (~33 % CPU, measured). Nor is a minimised window's pane
    /// shown. While hidden, a frame request
    /// only marks the pane dirty; it is drawn when the pane is seen again.
    /// </summary>
    private bool _shown;

    private void UpdateShown()
    {
        // Minimised is not shown: Avalonia keeps its frame clock running for a minimised window (measured, ~7 % CPU with a
        // field playing), though it stops it for a covered one.
        bool shown = _topLevel is not null && IsEffectivelyVisible && (_topLevel as Window)?.WindowState != WindowState.Minimized;
        if (shown == _shown) return;
        _shown = shown;
        _vm?.ShowIn(shown ? _frameClock : null);
        if (shown) RequestFrame();
    }

    /// <summary>brief-idle-power — this window's frame clock, which paces a playing field (Viewer3DViewModel.ShowIn).</summary>
    private void FrameClock(Action next) => _topLevel?.RequestAnimationFrame(_ => next());

    // ── hosting ─────────────────────────────────────────────────────────────────────────────

    protected override async void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // A Dock float or re-dock can detach and re-attach before the await below returns: only the
        // LATEST attach may build a surface and start a render thread, or a second thread would share
        // _go, _plan and the images with the first, and no detach would ever join it.
        int gen = ++_attachGen;
        _lossRebuilds = 0;
        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel is not null)
        {
            _topLevel.ScalingChanged += OnScalingChanged;
            _topLevel.PropertyChanged += OnTopLevelPropertyChanged;
        }
        UpdateShown();
        await Host(gen);
    }

    /// <summary>Builds the surface and starts the render thread, for attach <paramref name="gen"/>.</summary>
    private async Task Host(int gen)
    {
        try
        {
            var visual = ElementComposition.GetElementVisual(this);
            if (visual is null) { SetFault("the view has no composition visual."); return; }
            var compositor = visual.Compositor;
            var interop = await compositor.TryGetCompositionGpuInterop();
            if (gen != _attachGen) return;
            _compositor = compositor;
            _interop = interop;
            // Still the lost context (designer feedback round 34): wait for the compositor's next one rather than import
            // against this one, which fails the same way.
            if (_interop is { IsLost: true }) { Faulted(new InvalidOperationException("the GPU context is lost.")); return; }
            if (_vm is null) return;
            var backend = _vm.Session.EnsureBackend();
            // No usable GPU interop: a backend that can present in software reads each frame back instead of refusing,
            // unless Settings ▸ General turned that off.
            string? noInterop = _interop is null ? "this window's compositor offers no GPU interop, so the 3D view cannot present."
                              : backend.CheckInterop(_interop) is { } why ? $"{backend.Description}: {why}." : null;
            if (noInterop is not null)
            {
                if (!backend.CanPresentInSoftware) { SetFault(noInterop); return; }
                if (!(AppPreferencesIo.Load().View3DSoftwarePresent ?? true))
                { SetFault(noInterop + " Settings ▸ General ▸ Software 3D without GPU interop draws it, more slowly."); return; }
            }
            _soft = noInterop is not null;
            if (_soft) _interop = null;
            backend.PresentFaulted -= OnPresentFaulted;
            backend.PresentFaulted += OnPresentFaulted;
            backend.ClearPresentFault();

            if (!_soft)
            {
                _surface = _compositor.CreateDrawingSurface();
                _visual = _compositor.CreateSurfaceVisual();
                _visual.Surface = _surface;
                _visual.Size = new Vector(Bounds.Width, Bounds.Height);
                ElementComposition.SetElementChildVisual(this, _visual);
            }

            if (_thread is { IsAlive: false }) _thread = null;
            if (_thread is not null)
            {
                // The last detach could not join its thread (it outlived Join's timeout): it must stop
                // before a new one starts, never be revived by clearing _stop under it.
                SetFault("the previous render thread has not stopped; close and reopen the 3D view.");
                return;
            }
            _stop = false;
            // Teardown's wake is left set when no thread was there to take it (the view had refused, or its thread
            // had ended on an exception); the new thread would take it as a frame and wait on an image never created.
            _go.Reset();
            _thread = new Thread(RenderLoop) { IsBackground = true, Name = "viewer3d-render" };
            _thread.Start();
            SetFault(null);
            RequestFrame();
        }
        catch (Exception ex) { Faulted(ex); }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        ++_attachGen;
        if (_topLevel is not null)
        {
            _topLevel.ScalingChanged -= OnScalingChanged;
            _topLevel.PropertyChanged -= OnTopLevelPropertyChanged;
            _topLevel = null;
        }
        UpdateShown();                                       // a closed document animates nothing
        Teardown();
    }

    /// <summary>Stops the render thread and releases this compositor's surface and images.</summary>
    private void Teardown()
    {
        _catchUp?.Stop();
        _tracking?.Stop();
        _stop = true;
        _go.Set();
        if (_thread is null || _thread.Join(2000)) _thread = null;
        ElementComposition.SetElementChildVisual(this, null);
        // The images belong to THIS compositor; the device and the uploaded buffers stay with the
        // session, so a re-dock re-imports three images and uploads no geometry.
        if (_vm?.Session.Backend is { } b)
        {
            b.PresentFaulted -= OnPresentFaulted;
            try { lock (_vm.Session.RenderLock) b.ReleaseImages(); }
            catch (Exception) { /* a lost context may refuse the release; the images are dropped with the device */ }
        }
        _surface?.Dispose(); _surface = null;
        _soft = false; _bmp = null; _px = null;
        _imgW = _imgH = 0;
        _busy = _done = false;
        _tickQueued = false;
    }

    // ── a lost GPU context (brief-dfr31-5 R-dfr31-5-3) ─────────────────────────────────────────
    //
    // A machine moved between docking setups lost the compositor's GPU context: every present then faulted
    // ("OpenSharedResource failed", PlatformGraphicsContextLostException), unobserved, and the view stayed as it was
    // until the app was restarted. Now a failure rebuilds the surface, the images and the device; once the rebuilds
    // below are spent, the failure is the pane's fault, said in its place and once in Messages. A LOST context is not a
    // missing one, so software present (_soft) is no fallback for it here, and the fault stands until
    // Reconnect (the fault text's, or the stalled bar's Retry: brief-dfr34-5 R-dfr34-5-2) or a reattach, each of which
    // starts afresh with its rebuilds again.

    // Designer feedback round 34 — one rebuild was not enough: docking a laptop changes the display several times in a
    // row, and a rebuild made straight away imported against a context that was still lost, so the second fault was
    // final. Now a loss is rebuilt up to MaxLossRebuilds times, each after a longer wait (0.5, 1, 2, 4, 8 s) for the
    // compositor to bring its own context back; a fault more than LossBurst after the last rebuild starts a new count.

    private const int MaxLossRebuilds = 5;
    private static readonly TimeSpan LossBurst = TimeSpan.FromSeconds(30);
    private int _lossRebuilds;
    private DateTime _lastLossRebuild = DateTime.MinValue;

    private void OnPresentFaulted(Exception ex) => Avalonia.Threading.Dispatcher.UIThread.Post(() => Faulted(ex));

    private void Faulted(Exception ex)
    {
        if (_topLevel is null) return;                       // detached meanwhile: the next attach starts afresh
        var now = DateTime.UtcNow;
        if (now - _lastLossRebuild > LossBurst) _lossRebuilds = 0;
        if (_lossRebuilds < MaxLossRebuilds)
        {
            var wait = TimeSpan.FromMilliseconds(500 * (1 << _lossRebuilds));
            _lossRebuilds++;
            _lastLossRebuild = now;
            Teardown();
            _vm?.Session.ResetBackend();
            _fault = null;
            int gen = ++_attachGen;
            Avalonia.Threading.DispatcherTimer.RunOnce(() =>
            {
                if (gen == _attachGen && _topLevel is not null) _ = Host(gen);
            }, wait);
            return;
        }
        string why = ex.GetBaseException().Message;
        if (_fault != why) _vm?.PostMessage?.Invoke($"The 3D view cannot draw here: {why} Reconnect it from the view to try again.", true);
        SetFault(why);
    }

    /// <summary>
    /// brief-dfr34-5 R-dfr34-5-2 — rebuilds the surface, the images and the device now, with the loss rebuilds counted
    /// afresh: what attaching does, without closing and reopening the view. The fault text's Reconnect and the stalled
    /// bar's Retry both come here.
    /// </summary>
    public void Reconnect()
    {
        if (_topLevel is null) return;                       // detached: the next attach starts afresh anyway
        _lossRebuilds = 0;
        _lastLossRebuild = DateTime.MinValue;
        Teardown();
        _vm?.Session.ResetBackend();
        SetFault(null);
        _ = Host(++_attachGen);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty)
        {
            if (_visual is not null) _visual.Size = new Vector(Bounds.Width, Bounds.Height);
            _vm?.Resized((float)Bounds.Width, (float)Bounds.Height);
            if (WindowResizing && OperatingSystem.IsMacOS()) DrawDuringWindowResize();
            else RequestFrame();
        }
    }

    private void OnTopLevelPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == TopLevel.ClientSizeProperty) _windowResizedAt = Environment.TickCount64;
        else if (e.Property == Window.WindowStateProperty) UpdateShown();
    }

    /// <summary>This pane's window changed size a moment ago: its edge is (probably) being dragged.</summary>
    private bool WindowResizing => _windowResizedAt != 0 && Environment.TickCount64 - _windowResizedAt < WindowResizeQuietMs;

    /// <summary>
    /// brief-em3d-99 — one step of a live WINDOW resize (macOS): the frame at the new size is drawn and presented here, in
    /// the layout pass the window's resize runs (TopLevel.HandleResized), so it goes out in the commit of the synchronous
    /// paint that follows. Presented on the next tick instead, as everything else is, each step's frame was a commit of its
    /// own: Avalonia's render thread composed it between two synchronous paints and, during a live resize, blocked in
    /// Metal's BeginRenderingSession waiting for a drawable WHILE HOLDING the compositor lock the next paint needs. That was
    /// the stutter (src/Ui/RESOLVED.md, 2026-10-02). The UI thread waits only for the frame to be ENCODED: with timeline
    /// semaphores the compositor's GPU waits for the GPU, so this costs the frame's CPU side and never a GPU round trip.
    /// A splitter drag changes no window size and keeps the asynchronous path, which was already smooth.
    /// </summary>
    private void DrawDuringWindowResize()
    {
        _dirty = true;
        if (_surface is null || _stop || _vm is null || _fault is not null || !_shown || _vm.Session.Backend is not { } backend) { QueueTick(); return; }
        if (_busy && !_done && !AwaitFrame()) { CatchUpAfterResize(); return; }    // it outlived the wait and presents itself
        _done = _busy = false;                                                      // a frame drawn at the old size is never shown
        var s = _vm.Session;
        s.Ui.BeginFrame(false);
        bool started = PlanFrame(s, backend);
        s.Ui.EndFrame();
        if (!started) return;
        if (!AwaitFrame()) { CatchUpAfterResize(); return; }
        if (_done) { ResizeStepFrames++; PresentFinished(backend); }
        else { _dirty = true; CatchUpAfterResize(); }                               // skipped: the compositor held the image
    }

    /// <summary>
    /// Waits up to <see cref="ResizeFrameWaitMs"/> for the frame in flight; a frame that finishes while it is awaited does
    /// not queue its own present. True: it finished (or was skipped, <see cref="_done"/> false). False: it is still
    /// drawing and will queue its present when it finishes, as usual.
    /// </summary>
    private bool AwaitFrame()
    {
        _finished.Reset();
        Interlocked.Exchange(ref _awaited, 1);
        if (_busy && !_done) _finished.Wait(ResizeFrameWaitMs);
        if (Interlocked.Exchange(ref _awaited, 0) == 1) return !_busy || _done;     // reclaimed: the render thread never saw it
        _finished.Wait(ResizeFrameWaitMs);                                          // the render thread took it and sets at once
        return true;
    }

    /// <summary>The render thread's end of a frame: wake the awaiting resize step, or post the usual follow-up.</summary>
    private void FrameFinished(Action post, Avalonia.Threading.DispatcherPriority priority)
    {
        if (Interlocked.Exchange(ref _awaited, 0) == 1) _finished.Set();
        else Avalonia.Threading.Dispatcher.UIThread.Post(post, priority);
    }

    /// <summary>A frame requested mid-resize (a field animation, a skipped step) is drawn once the window is still.</summary>
    private void CatchUpAfterResize()
    {
        _catchUp ??= new Avalonia.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(WindowResizeQuietMs), Avalonia.Threading.DispatcherPriority.Background,
            (_, _) =>
            {
                if (WindowResizing) return;
                _catchUp!.Stop();
                if (_dirty) RequestFrame();
            });
        _catchUp.Start();
    }

    /// <summary>Snap tracking's clock: a point held under a still cursor is acquired with no mouse move to trigger a frame, and
    /// an unused reference is forgotten. Ticks only while the tracker has something pending, so an idle view stays idle.</summary>
    private Avalonia.Threading.DispatcherTimer? _tracking;

    private void SyncTrackingTimer()
    {
        if (_vm is not { TrackingNeedsTick: true })
        {
            _tracking?.Stop();
            return;
        }
        _tracking ??= new Avalonia.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(100), Avalonia.Threading.DispatcherPriority.Background,
            (_, _) =>
            {
                _vm?.TickSnapTracking();
                if (_vm is not { TrackingNeedsTick: true }) _tracking!.Stop();
            });
        if (!_tracking.IsEnabled) _tracking.Start();
    }

    /// <summary>The window moved to a monitor of another scale: the images are re-sized on the next frame.</summary>
    private void OnScalingChanged(object? sender, EventArgs e) => RequestFrame();

    private void SetFault(string? why)
    {
        if (_fault == why) return;
        _fault = why;
        if (_vm is not null) _vm.PaneFault = why;
        FaultChanged?.Invoke(why);
    }

    /// <summary>
    /// Something that is drawn changed: a new frame is planned on the next tick. 2026-10-02 (brief-em3d-99): the pane used to
    /// plan a new frame on EVERY tick it was not busy, and each finished frame queued the next tick, so it rendered and
    /// presented at display rate with nothing changing, and Avalonia's render thread spent most of every second blocked in
    /// Metal's BeginRenderingSession. Every change of what is drawn already requests its frame (the view model raises
    /// FrameRequested at each), so the pane now renders only when asked, on a size change, and while orbiting. Measured: at
    /// rest the render thread went from 600-1000 ms/s of ticks to ~40. A live WINDOW resize is DrawDuringWindowResize's.
    /// </summary>
    public void RequestFrame()
    {
        _dirty = true;
        QueueTick();
    }

    /// <summary>A tick with nothing new to draw: present a finished frame.</summary>
    private void QueueTick()
    {
        if (_compositor is null || _tickQueued || _fault is not null || !_shown) return;
        _tickQueued = true;
        _compositor.RequestCompositionUpdate(_tick);
    }

    private bool _dirty;
    private long _frameStarted;

    /// <summary>The UI thread's whole per-frame cost: present what is finished, plan the next.</summary>
    private void OnTick()
    {
        _tickQueued = false;
        if ((_surface is null && !_soft) || _stop || _vm is null) return;
        var s = _vm.Session;
        var backend = s.Backend;
        if (backend is null) return;
        var view = _vm.View;
        s.Ui.BeginFrame(view.Orbiting);
        PresentFinished(backend);
        bool more = view.Orbiting;
        // brief-em3d-99: mid window-resize a frame is drawn only by the resize step itself; one planned here would present
        // in a commit of its own, which is the stutter. It is drawn once the window is still.
        if (WindowResizing && OperatingSystem.IsMacOS()) { if (_dirty || more) { _dirty = true; CatchUpAfterResize(); } more = false; }
        else PlanFrame(s, backend);
        s.Ui.EndFrame();
        if (more) RequestFrame();
        // Nothing polls the frame in flight: it queues its own present, and a request made meanwhile is planned then.
    }

    /// <summary>Hands a finished frame to the compositor (a server job in the next commit).</summary>
    private void PresentFinished(Viewer3DBackend backend)
    {
        if (!_done) return;
        try
        {
            if (_soft) ShowSoftFrame();
            else
            {
                backend.Present(_surface!, _doneImage, _doneValue);
                SurfaceUpdateFlush.AfterUpdate(_compositor!);      // drawn by the render that applies it (brief-em3d-99)
            }
        }
        catch (Exception ex) { _done = _busy = false; Faulted(ex); return; }
        _done = false;
        _busy = false;
        _vm!.FrameTook(System.Diagnostics.Stopwatch.GetElapsedTime(_frameStarted).TotalMilliseconds);
        _vm.OnPicked(backend.PickedId, backend.PickedFace, backend.PickedPoint, backend.PickedSomething, backend.PickPatch);
        SyncTrackingTimer();
        FramePresented?.Invoke();
    }

    /// <summary>Software present: copies the frame read back on the render thread into the bitmap Render draws.</summary>
    private void ShowSoftFrame()
    {
        if (_px is not { } px) return;
        if (_bmp is null || _bmp.PixelSize.Width != _pxW || _bmp.PixelSize.Height != _pxH)
            _bmp = new Avalonia.Media.Imaging.WriteableBitmap(new PixelSize(_pxW, _pxH), new Vector(96, 96),
                Avalonia.Platform.PixelFormat.Rgba8888, Avalonia.Platform.AlphaFormat.Premul);
        using (var fb = _bmp.Lock())
            for (int y = 0; y < _pxH; y++)
                System.Runtime.InteropServices.Marshal.Copy(px, y * _pxW * 4, fb.Address + y * fb.RowBytes, _pxW * 4);
        InvalidateVisual();
    }

    /// <summary>Plans the next frame and starts the render thread on it, if one is wanted and none is in flight.</summary>
    private bool PlanFrame(Viewer3DSession s, Viewer3DBackend backend)
    {
        var view = _vm!.View;
        double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        int w = Math.Max(1, (int)Math.Ceiling(Bounds.Width * scale)), h = Math.Max(1, (int)Math.Ceiling(Bounds.Height * scale));
        if (_busy || _fault is not null || !(_dirty || view.Orbiting || w != _imgW || h != _imgH)) return false;
        _dirty = false;
        try
        {
            if (w != _imgW || h != _imgH)
            {
                lock (s.RenderLock) backend.CreateImages(_interop!, w, h, SwapchainImages);
                _imgW = w; _imgH = h;
            }
            // The cursor is in device pixels for the ID pass.
            float cx = view.CursorX, cy = view.CursorY;
            if (cx >= 0) { view.CursorX = (float)(cx * scale); view.CursorY = (float)(cy * scale); }
            // brief-em3d-44: the ID pass reads the snap's patch around the cursor, in device pixels.
            _plan.PickSize = _vm.PickSizeFor(scale, backend.MaxPickSize);
            _plan.PickPixelsPerDip = (float)scale;
            _plan.TriangleBudget = _vm.TriangleBudget;
            _plan.Plan(_vm.Scene, view, w, h, backend.FlipY, pick: view.CursorX >= 0,
                       _vm.MeshOverlay, _vm.SectionOverlay, _vm.GridOverlay, _vm.FieldDrawn);
            _vm.FramePlanned(_plan);
            _pField = _vm.FieldDrawn;
            view.CursorX = cx; view.CursorY = cy;
            _planScene = _vm.Scene;
            (_pMesh, _pSection, _pGrid) = (_vm.MeshOverlay, _vm.SectionOverlay, _vm.GridOverlay);
            _pOrbit = view.Orbiting;
            view.Orbiting = false;
            _nextImage = (int)(_frame % SwapchainImages);
            _frame++;
            _busy = true;
            _frameStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            _go.Set();
            return true;
        }
        catch (Exception ex) { Faulted(ex); return false; }
    }

    private void RenderLoop()
    {
        while (true)
        {
            _go.WaitOne();
            if (_stop) return;
            var s = _vm!.Session;
            int image = _nextImage;
            ulong value = _frame;
            try
            {
                // The whole frame, the wait included, holds the render lock: a tab closed mid-frame
                // disposes the session under this lock, and a backend freed during WaitReusable would
                // leave a native call (D3D11's AcquireSync) on a released object — an access violation
                // no catch can stop. The UI thread takes the lock only to create or release images,
                // which it never does while a frame is in flight.
                lock (s.RenderLock)
                {
                    if (s.Backend is not { } backend) return;      // the session was disposed
                    if (!backend.WaitReusable(image, ReleaseTimeoutMs))
                    {
                        // The compositor still holds this image (a minimised or occluded window may not
                        // composite at all). Never draw into an image we do not hold — D3D11's keyed
                        // mutex and Vulkan's release semaphore both forbid it — so this frame is skipped,
                        // counted, and asked for again on the window's next animation frame (brief-idle-power):
                        // asked for at once, a compositor that is not compositing made it a loop of timeouts.
                        ReleaseTimeouts++;
                        _busy = false;
                        FrameFinished(_retryWhenDrawing, Avalonia.Threading.DispatcherPriority.Background);
                        continue;
                    }
                    if (!s.Frame(image, _plan, value, _planScene, _pMesh, _pSection, _pGrid, _pOrbit, _pField)) return;
                    if (_soft) { _px = backend.ReadFramePixels(image); _pxW = _imgW; _pxH = _imgH; }
                }
                _doneImage = image;
                _doneValue = value;
                _done = true;
            }
            catch (Exception ex)
            {
                _fault = ex.Message;
                _busy = false;
                Avalonia.Threading.Dispatcher.UIThread.Post(() => { _fault = null; Faulted(ex); });
                return;
            }
            FrameFinished(QueueTick, default);   // present it; it plans nothing new by itself
        }
    }

    // ── input (R-em3d28-4a) ─────────────────────────────────────────────────────────────────

    private Point? _last, _pressedAt;
    private bool _orbiting, _panning;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var p = e.GetCurrentPoint(this);
        _last = _pressedAt = p.Position;
        // 3D editor bugs round 2 — the axis indicator: a double-click turns the view (Viewer3DOverlay.AxisIndicatorHit), and a
        // single click on it selects nothing — it would otherwise clear the selection, being a click on empty space.
        _onTriad = false;
        if (_vm is { } tv && tv.View.ShowAxisIndicator && p.Properties.IsLeftButtonPressed && e.KeyModifiers == KeyModifiers.None
            && Viewer3DOverlay.AxisIndicatorHit(tv.View.Camera, Viewer3DOverlay.AxisIndicatorCentre(Bounds.Height), p.Position) is { } sv)
        {
            _onTriad = true;
            // 3D editor bugs round 3 — every second click, not only the second: Avalonia keeps counting while the clicks stay
            // put, so a quick second double-click on the same letter (Right, then Left) reported 3 and 4 and did nothing.
            if (e.ClickCount % 2 == 0)
            {
                tv.StandardViewCommand.Execute(sv);
                _last = _pressedAt = null;
                e.Handled = true;
                return;
            }
        }
        // brief-em3d-43: Shift + left is Shift-CLICK now (add to the selection), so a Shift + left DRAG still
        // pans — decided on release, by whether it moved — and Alt + left pans outright.
        // brief-em3d-46 R-em3d46-5 — a plain left press on a gizmo handle is a constrained move, not an orbit: the
        // drag's moves are hovers (the snap and the preview follow them) and the release commits.
        if (p.Properties.IsLeftButtonPressed && e.KeyModifiers == KeyModifiers.None && _vm?.PressGizmo() == true)
        {
            _gizmoDrag = true;
            _moved = false;
            _orbiting = _panning = _rightPressed = _shiftPress = false;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }
        // 3D round 1 — while a tool, an operation or Measure waits for a click, a plain left drag does NOT orbit: the press
        // is that gesture's click however far it wobbles, and only Ctrl/Cmd + drag orbits. macOS reports Control + left
        // as a RIGHT press carrying Control (AppKit's one-button convention), so that is read as the left press it is.
        bool drawing = _vm?.OrbitNeedsCommand == true;
        bool command = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
        bool controlLeft = drawing && p.Properties.IsRightButtonPressed && e.KeyModifiers.HasFlag(KeyModifiers.Control);
        bool left = p.Properties.IsLeftButtonPressed || controlLeft;
        bool right = p.Properties.IsRightButtonPressed && !controlLeft;
        _shiftPress = left && e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        _panning = p.Properties.IsMiddleButtonPressed || (left && (_shiftPress || e.KeyModifiers.HasFlag(KeyModifiers.Alt))) || right;
        _drawPress = !_panning && left && drawing && !command;
        _orbiting = !_panning && left && !_drawPress;
        _rightPressed = right;
        _pressModifiers = e.KeyModifiers;
        _pressClicks = e.ClickCount;
        _moved = false;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    private bool _moved, _rightPressed, _shiftPress;
    /// <summary>3D editor bugs round 2 — the press landed on the axis indicator.</summary>
    private bool _onTriad;
    /// <summary>3D round 1 — a plain left press while something waits for a click: its release is the click, moved or not.</summary>
    private bool _drawPress;
    // brief-em3d-45 — what the press carried, for the drawing: Ctrl/Cmd for a plane gesture, 2 for a double-click.
    private KeyModifiers _pressModifiers;
    private int _pressClicks = 1;

    /// <summary>brief-em3d-46 — a gizmo handle is being dragged.</summary>
    private bool _gizmoDrag;

    /// <summary>A drag (orbit, pan or a gizmo handle) is under way — the mode keys wait for it (R-em3d43-2a).</summary>
    public bool GestureInProgress => ((_orbiting || _panning) && _moved) || _gizmoDrag;

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pos = e.GetPosition(this);
        // A key pressed while the button is still down and has not moved (G on the vertex just pressed) armed a tool: the
        // press becomes that tool's click, as if it had started armed — its drag moves what the tool moves and its
        // release places it. Orbiting it instead turned the camera under the cursor the tool was following.
        if (_orbiting && !_moved && _vm?.OrbitNeedsCommand == true && (_pressModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) == 0)
            (_orbiting, _drawPress) = (false, true);
        // A gizmo or point drag: whether it went past the click slop — a point pressed and released in place was a click.
        if (_gizmoDrag && _pressedAt is { } from && Math.Abs(pos.X - from.X) + Math.Abs(pos.Y - from.Y) > ClickSlopDips) _moved = true;
        if (_last is { } last && (_orbiting || _panning))
        {
            var d = pos - last;
            if (_pressedAt is { } at && Math.Abs(pos.X - at.X) + Math.Abs(pos.Y - at.Y) > ClickSlopDips) _moved = true;
            if (_orbiting) _vm?.Orbit((float)d.X, (float)d.Y);
            else _vm?.Pan((float)d.X, (float)d.Y, (float)Bounds.Height);
            _last = pos;
            // 3D editor bugs round 2 — a camera drag hovers nothing and snaps to nothing (Viewer3DViewModel.CameraGesture).
            // Not before the drag has MOVED: a press that stays put is still a click, and selects what it hovers.
            if (_moved) _vm?.SetCameraGesture(true);
        }
        _vm?.SetGeometrySnapSuspended(e.KeyModifiers.HasFlag(KeyModifiers.Alt));
        _vm?.SetShiftHeld(e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        _vm?.SetCommandHeld((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0);
        _vm?.Hover((float)pos.X, (float)pos.Y);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_gizmoDrag)
        {
            _gizmoDrag = false;
            e.Pointer.Capture(null);
            _vm?.ReleaseGizmo(_moved);
            _last = _pressedAt = null;
            return;
        }
        if (_onTriad && !_moved) { }
        else if (_drawPress || (_orbiting && !_moved)) _vm?.Click(shift: false, _pressModifiers, _pressClicks);
        else if (_shiftPress && !_moved) _vm?.Click(shift: true, _pressModifiers, _pressClicks);
        bool menu = _rightPressed && !_moved && e.InitialPressMouseButton == MouseButton.Right;
        _orbiting = _panning = _rightPressed = _shiftPress = _drawPress = false;
        _last = _pressedAt = null;
        e.Pointer.Capture(null);
        if (_vm?.CameraGesture == true)
        {
            _vm.SetCameraGesture(false);
            var at = e.GetPosition(this);
            _vm.Hover((float)at.X, (float)at.Y);
        }
        if (menu) ContextMenuRequested?.Invoke();
    }

    /// <summary>Alt-Tab or a focus steal mid-drag loses the capture and the release never arrives:
    /// without this, every later hover would keep orbiting or panning until the next click.</summary>
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _orbiting = _panning = _rightPressed = _drawPress = false;
        _last = _pressedAt = null;
        _vm?.SetCameraGesture(false);
        if (_gizmoDrag) { _gizmoDrag = false; _vm?.CancelGizmo(); }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (!_orbiting && !_panning) _vm?.Leave();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var p = e.GetPosition(this);
        _vm?.Zoom((float)e.Delta.Y, (float)p.X, (float)p.Y, (float)Bounds.Width, (float)Bounds.Height);
        e.Handled = true;
    }

    // ── 3D round 3: Project Tree drops — the payload is text on the pasteboard (CellDragPayload's reason) ──────

    private static string? DragText(DragEventArgs e)
    {
        foreach (var item in e.DataTransfer.Items)
            if (item.TryGetRaw(DataFormat.Text) is string text) return text;
        return null;
    }

    private static bool Command(DragEventArgs e) => (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;

    /// <summary>brief-em3d-101 R-em3d101-2c — the files a drop carries (DataFormat.File): one IStorageItem per item on macOS, a
    /// list on other backends, or a bare path — every one of them.</summary>
    private static List<string> DragFiles(DragEventArgs e)
    {
        var list = new List<string>();
        foreach (var item in e.DataTransfer.Items)
        {
            switch (item.TryGetRaw(DataFormat.File))
            {
                case Avalonia.Platform.Storage.IStorageItem single when single.Path?.LocalPath is { Length: > 0 } p: list.Add(p); break;
                case IEnumerable<Avalonia.Platform.Storage.IStorageItem> many: list.AddRange(many.Select(f => f.Path?.LocalPath).OfType<string>()); break;
                case string s: list.Add(s); break;
            }
        }
        return list;
    }

    private void OnTreeDragOver(object? sender, DragEventArgs e)
    {
        var p = e.GetPosition(this);
        if (DragFiles(e) is { Count: > 0 } files)
        {
            // A file from the operating system: an image places a sheet; anything else is refused, never half-imported.
            bool place = _vm?.FileDragOver((float)p.X, (float)p.Y, files) == true;
            e.DragEffects = place ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
            return;
        }
        bool ok = DragText(e) is { } text && _vm?.TreeDragOver((float)p.X, (float)p.Y, text, Command(e)) == true;
        e.DragEffects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        if (ok) e.Handled = true;
    }

    private void OnTreeDrop(object? sender, DragEventArgs e)
    {
        if (DragFiles(e) is { Count: > 0 } files)
        {
            var at = e.GetPosition(this);
            if (_vm?.FileDrop((float)at.X, (float)at.Y, files, (e.KeyModifiers & KeyModifiers.Shift) != 0) == true) { e.Handled = true; Focus(); }
            return;
        }
        if (DragText(e) is not { } text) return;
        var p = e.GetPosition(this);
        if (_vm?.TreeDrop((float)p.X, (float)p.Y, text, Command(e)) != true) return;
        e.Handled = true;
        Focus();
    }

    private void OnTreeDragLeave(object? sender, DragEventArgs e) => _vm?.TreeDragLeave();

    private void OnMagnify(object? sender, PointerDeltaEventArgs e)
    {
        var p = e.GetPosition(this);
        // A pinch reports a relative magnification; ~0.1 per notch reads naturally on a trackpad.
        _vm?.Zoom((float)(e.Delta.X * 10), (float)p.X, (float)p.Y, (float)Bounds.Width, (float)Bounds.Height);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_vm is null) return;
        if (e.Key == Key.Escape)
        {
            if (Escape()) e.Handled = true;
            return;
        }
        if (e.Key is Key.LeftAlt or Key.RightAlt) _vm.SetGeometrySnapSuspended(true);
        if (e.Key is Key.LeftShift or Key.RightShift) _vm.SetShiftHeld(true);
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LWin or Key.RWin) _vm.SetCommandHeld(true);
        // brief-em3d-46 — keys during a gizmo drag belong to the move it started (X/Y/Z, a typed distance, Esc).
        if (_gizmoDrag)
        {
            if (_vm.HandleKey(e.Key, e.KeyModifiers, gestureInProgress: false)) e.Handled = true;
            if (e.Key == Key.Escape) { _gizmoDrag = false; _vm.CancelGizmo(); }
            return;
        }
        if (_vm.HandleKey(e.Key, e.KeyModifiers, GestureInProgress)) e.Handled = true;
    }

    /// <summary>
    /// 3D editor keys — a key pressed while focus is ELSEWHERE in the view hosting this pane (a toolbar button just clicked,
    /// a tree row): the same keys as <see cref="OnKeyDown"/>, so 1 is still Isometric and G still Move. The host decides
    /// what may forward (never a text field); a gizmo drag holds the pointer here, so none is under way.
    /// </summary>
    public bool ForwardKey(KeyEventArgs e) => _vm?.HandleKey(e.Key, e.KeyModifiers, GestureInProgress) == true;

    /// <summary>
    /// 3D round 1 — one Esc, one step back. The workspace window binds Escape to a command and a window key binding
    /// marks the key handled before routing reaches the focused control, so this pane's own key handler never sees
    /// Esc in a docked view: the view hosting the pane claims it (tunnel, handled events too) and calls this. A drag
    /// under way is cancelled first (the camera stays where the drag left it — a camera move is not an edit — and the
    /// release is no longer a click); a gizmo drag's move is cancelled; otherwise the view model's ladder runs:
    /// the tool steps back or disarms, then a measurement goes, then Measure, then Face/Vertex/Edge mode (back to Object), then
    /// the selection.
    /// </summary>
    public bool Escape()
    {
        if (_vm is null) return false;
        if (_gizmoDrag)
        {
            _vm.HandleKey(Key.Escape, KeyModifiers.None, gestureInProgress: false);
            _gizmoDrag = false;
            _vm.CancelGizmo();
            return true;
        }
        if (GestureInProgress)
        {
            _orbiting = _panning = _rightPressed = _shiftPress = _drawPress = false;
            _last = _pressedAt = null;
            return true;
        }
        return _vm.HandleKey(Key.Escape, KeyModifiers.None, gestureInProgress: false);
    }

    /// <summary>brief-em3d-44 — Alt / Option released: geometry snap resumes.</summary>
    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.Key is Key.LeftAlt or Key.RightAlt) _vm?.SetGeometrySnapSuspended(false);
        if (e.Key is Key.LeftShift or Key.RightShift) _vm?.SetShiftHeld(false);
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LWin or Key.RWin) _vm?.SetCommandHeld(false);
    }

    /// <summary>The latched-key lesson: a key-up delivered to another window never reaches this one, so every
    /// held-key latch is cleared on losing focus — otherwise geometry snap would silently stay suspended.</summary>
    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        _vm?.ClearHeldKeys();
        _vm?.SetShiftHeld(false);
        _vm?.SetCommandHeld(false);
    }
}

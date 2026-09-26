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
// INPUT (R-em3d28-4a): orbit = left drag; pan = middle drag or Shift + left drag; zoom to the cursor =
// wheel or pinch; F fits; 1–7 are the standard views; P / O switch projection; C toggles the clip
// plane; A the axis indicator. A trackpad's two-finger scroll zooms and its pinch zooms (macOS). No
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

namespace CircuitRF.Ui.Viewer3D;

public sealed class Viewer3DPane : Control
{
    /// <summary>Images in the swapchain: one being drawn, one being shown, one spare.</summary>
    public const int SwapchainImages = 3;

    /// <summary>How long the render thread waits for the compositor to release an image before it
    /// counts a release timeout (reported on the status line, never retried in a loop).</summary>
    public const int ReleaseTimeoutMs = 250;

    private Viewer3DViewModel? _vm;
    private Compositor? _compositor;
    private ICompositionGpuInterop? _interop;
    private CompositionDrawingSurface? _surface;
    private CompositionSurfaceVisual? _visual;
    private readonly Action _tick;
    private bool _tickQueued;
    private int _imgW, _imgH;

    private Thread? _thread;
    private readonly AutoResetEvent _go = new(false);
    private volatile bool _stop, _busy, _done;
    private readonly Scene3DFramePlan _plan = new();
    private Scene3DModel _planScene = Scene3DModel.Empty();
    private Scene3DOverlay _pMesh = Scene3DOverlay.None, _pSection = Scene3DOverlay.None, _pGrid = Scene3DOverlay.None;
    private bool _pOrbit;
    private int _nextImage, _doneImage;
    private ulong _frame, _doneValue;
    private volatile string? _fault;

    /// <summary>Why the pane is not drawing, or null while it is.</summary>
    public string? Fault => _fault;

    /// <summary>Raised on the UI thread after each presented frame — the overlay redraws its axes.</summary>
    public event Action? FramePresented;

    /// <summary>Raised when the pane faults or recovers, for the view to show the reason.</summary>
    public event Action<string?>? FaultChanged;

    public int ReleaseTimeouts { get; private set; }

    public Viewer3DPane()
    {
        _tick = OnTick;
        AddHandler(PointerTouchPadGestureMagnifyEvent, OnMagnify);
        ClipToBounds = true;
        Focusable = true;
        Background = Brushes.Transparent;
    }

    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        Border.BackgroundProperty.AddOwner<Viewer3DPane>();

    public IBrush? Background { get => GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }

    /// <summary>Hit-testable everywhere: the composition visual draws on top, but pointer input is ours.</summary>
    public override void Render(DrawingContext context) => context.FillRectangle(Background ?? Brushes.Transparent, new Rect(Bounds.Size));

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.FrameRequested -= RequestFrame;
        _vm = DataContext as Viewer3DViewModel;
        if (_vm is not null) _vm.FrameRequested += RequestFrame;
        RequestFrame();
    }

    // ── hosting ─────────────────────────────────────────────────────────────────────────────

    protected override async void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        try
        {
            var visual = ElementComposition.GetElementVisual(this);
            if (visual is null) { SetFault("the view has no composition visual."); return; }
            _compositor = visual.Compositor;
            _interop = await _compositor.TryGetCompositionGpuInterop();
            if (_interop is null) { SetFault("this window's compositor offers no GPU interop, so the 3D view cannot present."); return; }
            if (_vm is null) return;
            var backend = _vm.Session.EnsureBackend();
            if (backend.CheckInterop(_interop) is { } why) { SetFault($"{backend.Description}: {why}."); return; }

            _surface = _compositor.CreateDrawingSurface();
            _visual = _compositor.CreateSurfaceVisual();
            _visual.Surface = _surface;
            _visual.Size = new Vector(Bounds.Width, Bounds.Height);
            ElementComposition.SetElementChildVisual(this, _visual);

            _stop = false;
            _thread = new Thread(RenderLoop) { IsBackground = true, Name = "viewer3d-render" };
            _thread.Start();
            SetFault(null);
            RequestFrame();
        }
        catch (Exception ex) { SetFault(ex.Message); }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _stop = true;
        _go.Set();
        _thread?.Join(2000);
        _thread = null;
        ElementComposition.SetElementChildVisual(this, null);
        // The images belong to THIS compositor; the device and the uploaded buffers stay with the
        // session, so a re-dock re-imports three images and uploads no geometry.
        if (_vm?.Session.Backend is { } b) lock (_vm.Session.RenderLock) b.ReleaseImages();
        _surface?.Dispose(); _surface = null;
        _imgW = _imgH = 0;
        _busy = _done = false;
        _tickQueued = false;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty)
        {
            if (_visual is not null) _visual.Size = new Vector(Bounds.Width, Bounds.Height);
            _vm?.Resized((float)Bounds.Width, (float)Bounds.Height);
            RequestFrame();
        }
    }

    private void SetFault(string? why)
    {
        if (_fault == why) return;
        _fault = why;
        FaultChanged?.Invoke(why);
    }

    public void RequestFrame()
    {
        if (_compositor is null || _tickQueued || _fault is not null) return;
        _tickQueued = true;
        _compositor.RequestCompositionUpdate(_tick);
    }

    /// <summary>The UI thread's whole per-frame cost: present what is finished, plan the next.</summary>
    private void OnTick()
    {
        _tickQueued = false;
        if (_surface is null || _stop || _vm is null) return;
        var s = _vm.Session;
        var backend = s.Backend;
        if (backend is null) return;
        double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        int w = Math.Max(1, (int)Math.Ceiling(Bounds.Width * scale)), h = Math.Max(1, (int)Math.Ceiling(Bounds.Height * scale));
        var view = _vm.View;
        s.Ui.BeginFrame(view.Orbiting);

        if (_done)
        {
            try { backend.Present(_surface, _doneImage, _doneValue); }
            catch (Exception ex) { SetFault(ex.Message); }
            _done = false;
            _busy = false;
            _vm.OnPicked(backend.PickedId, backend.PickedPoint, backend.PickedSomething);
            FramePresented?.Invoke();
        }

        bool more = view.Orbiting;
        if (!_busy && _fault is null)
        {
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
                _plan.Plan(_vm.Scene, view, w, h, backend.FlipY, pick: view.CursorX >= 0,
                           _vm.MeshOverlay, _vm.SectionOverlay, _vm.GridOverlay);
                view.CursorX = cx; view.CursorY = cy;
                _planScene = _vm.Scene;
                (_pMesh, _pSection, _pGrid) = (_vm.MeshOverlay, _vm.SectionOverlay, _vm.GridOverlay);
                _pOrbit = view.Orbiting;
                view.Orbiting = false;
                _nextImage = (int)(_frame % SwapchainImages);
                _frame++;
                _busy = true;
                _go.Set();
            }
            catch (Exception ex) { SetFault(ex.Message); }
        }
        s.Ui.EndFrame();
        if (more || _busy) RequestFrame();
    }

    private void RenderLoop()
    {
        while (true)
        {
            _go.WaitOne();
            if (_stop) return;
            var s = _vm!.Session;
            var backend = s.Backend!;
            int image = _nextImage;
            ulong value = _frame;
            try
            {
                if (!backend.WaitReusable(image, ReleaseTimeoutMs))
                {
                    // The compositor still holds this image (a minimised or occluded window may not
                    // composite at all). Never draw into an image we do not hold — D3D11's keyed
                    // mutex and Vulkan's release semaphore both forbid it — so this frame is skipped,
                    // counted, and asked for again; nothing loops here.
                    ReleaseTimeouts++;
                    _busy = false;
                    Avalonia.Threading.Dispatcher.UIThread.Post(RequestFrame, Avalonia.Threading.DispatcherPriority.Background);
                    continue;
                }
                s.Frame(image, _plan, value, _planScene, _pMesh, _pSection, _pGrid, _pOrbit);
                _doneImage = image;
                _doneValue = value;
                _done = true;
            }
            catch (Exception ex)
            {
                _fault = ex.Message;
                Avalonia.Threading.Dispatcher.UIThread.Post(() => FaultChanged?.Invoke(ex.Message));
                return;
            }
            Avalonia.Threading.Dispatcher.UIThread.Post(RequestFrame);
        }
    }

    // ── input (R-em3d28-4a) ─────────────────────────────────────────────────────────────────

    private Point? _last;
    private bool _orbiting, _panning;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var p = e.GetCurrentPoint(this);
        _last = p.Position;
        _panning = p.Properties.IsMiddleButtonPressed || (p.Properties.IsLeftButtonPressed && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                   || p.Properties.IsRightButtonPressed;
        _orbiting = !_panning && p.Properties.IsLeftButtonPressed;
        _moved = false;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    private bool _moved;

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pos = e.GetPosition(this);
        if (_last is { } last && (_orbiting || _panning))
        {
            var d = pos - last;
            if (Math.Abs(d.X) + Math.Abs(d.Y) > 0) _moved = true;
            if (_orbiting) _vm?.Orbit((float)d.X, (float)d.Y);
            else _vm?.Pan((float)d.X, (float)d.Y, (float)Bounds.Height);
            _last = pos;
        }
        _vm?.Hover((float)pos.X, (float)pos.Y);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_orbiting && !_moved) _vm?.Click();
        _orbiting = _panning = false;
        _last = null;
        e.Pointer.Capture(null);
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
        if (_vm is null || e.KeyModifiers != KeyModifiers.None) return;
        StandardView3D? v = e.Key switch
        {
            Key.D1 => StandardView3D.Iso, Key.D2 => StandardView3D.Top, Key.D3 => StandardView3D.Front,
            Key.D4 => StandardView3D.Right, Key.D5 => StandardView3D.Back, Key.D6 => StandardView3D.Left,
            Key.D7 => StandardView3D.Bottom, _ => null,
        };
        if (v is { } view) { _vm.StandardViewCommand.Execute(view); e.Handled = true; return; }
        switch (e.Key)
        {
            case Key.F: _vm.FitCommand.Execute(null); e.Handled = true; break;
            case Key.P: _vm.IsPerspective = true; e.Handled = true; break;
            case Key.O: _vm.IsPerspective = false; e.Handled = true; break;
            case Key.C: _vm.ClipEnabled = !_vm.ClipEnabled; e.Handled = true; break;
            case Key.A: _vm.ShowAxisIndicator = !_vm.ShowAxisIndicator; e.Handled = true; break;
        }
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Viewer3dSpike.App;

interface IPane { void RequestFrame(); }

/// <summary>
/// The input loop (em-3d.md §8.4): a transparent, hit-testable panel around the pane that turns
/// pointer events into camera edits and hover positions. It computes no geometry. Left drag
/// orbits, right/middle or Shift+left drag pans, wheel / trackpad scroll and pinch zoom, a click
/// selects what is hovered.
/// </summary>
sealed class ViewportHost : Panel
{
    readonly Session _s;
    readonly Control _pane;
    readonly TextBlock _overlay = new() { Foreground = Brushes.Orange, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12), IsHitTestVisible = false };
    Point _last;
    bool _pressed, _dragged, _pan;

    public ViewportHost(Session s)
    {
        _s = s;
        Background = Brushes.Transparent;
        _pane = s.Route == "gl" ? new GlPaneAdapter(s) : new InteropPane(s);
        Children.Add(_pane);
        Children.Add(_overlay);
        AddHandler(PointerTouchPadGestureMagnifyEvent, (_, e) => { _s.Input.Zoom((float)e.Delta.X * 6f); Frame(); });
    }

    public string? Error => (_pane as InteropPane)?.Error;
    public void ShowError() => _overlay.Text = Error ?? "";

    double Scale => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
    void Frame() => (_pane as IPane)?.RequestFrame();

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var pt = e.GetCurrentPoint(this);
        _pressed = true; _dragged = false;
        _pan = pt.Properties.IsRightButtonPressed || pt.Properties.IsMiddleButtonPressed || e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        _last = pt.Position;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        double k = Scale;
        _s.Input.Hover((float)(p.X * k), (float)(p.Y * k));
        if (_pressed)
        {
            var d = p - _last;
            if (Math.Abs(d.X) + Math.Abs(d.Y) > 0.5) _dragged |= Math.Abs(d.X) + Math.Abs(d.Y) > 2;
            if (_pan) _s.Input.Pan((float)(d.X * k), (float)(d.Y * k), (float)(Bounds.Height * k));
            else _s.Input.Orbit((float)d.X, (float)d.Y);
            _last = p;
        }
        Frame();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_pressed && !_dragged && !_pan) _s.Input.Select(_s.Hovered);
        _pressed = false;
        e.Pointer.Capture(null);
        Frame();
    }

    protected override void OnPointerExited(PointerEventArgs e) { base.OnPointerExited(e); _s.Input.Leave(); Frame(); }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        _s.Input.Zoom((float)e.Delta.Y);
        e.Handled = true;
        Frame();
    }
}

/// <summary>GlPane plus the IPane hook.</summary>
sealed class GlPaneAdapter(Session s) : Panel, IPane
{
    readonly GlPane _gl = new(s);
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (Children.Count == 0) Children.Add(_gl);
        s.HostInfo = "OpenGlControlBase (Avalonia supplies context + framebuffer)";
        // OnOpenGlInit may never be called at all (Avalonia could not create a context for the control)
        Avalonia.Threading.DispatcherTimer.RunOnce(() =>
        {
            if (s.GlRenderCalls == 0)
                s.HostInfo = $"OpenGlControlBase: NO FRAME after 2 s — OnOpenGlInit/Render never called; bounds {_gl.Bounds.Width:F0}x{_gl.Bounds.Height:F0} (Avalonia's reason, if any, is in the terminal)";
        }, TimeSpan.FromSeconds(2));
    }
    public void RequestFrame() => _gl.RequestNextFrameRendering();
}

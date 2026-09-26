using Avalonia;
using Avalonia.Controls;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Viewer3dSpike.Render.Gl;

namespace Viewer3dSpike.App;

/// <summary>
/// Route C: OpenGL inside Avalonia's own render pass. Avalonia supplies the context and the
/// framebuffer; OnOpenGlRender runs on whatever thread Avalonia calls it on (the status line
/// reports which — the design note expects the UI thread, which is the whole objection to this
/// route: the GL frame's CPU cost is charged to the thread the text box types on).
/// </summary>
sealed class GlPane(Session s) : OpenGlControlBase
{
    GlRenderer? _r;

    protected override void OnOpenGlInit(GlInterface gl)
    {
        string ctx = $"{GlVersion.Type} {GlVersion.Major}.{GlVersion.Minor}{(GlVersion.IsCompatibilityProfile ? " compatibility" : "")}";
        s.DeviceInfo = $"OpenGL via Avalonia ({ctx}): initialising…";
        try
        {
            _r = new GlRenderer(gl.GetProcAddress, GlVersion.Type == GlProfileType.OpenGLES, s.Ui);
            _r.Init(s.Scene);
            s.Inits++;
            s.DeviceInfo = $"OpenGL via Avalonia ({ctx}): {_r.Info}";
        }
        catch (Exception ex)
        {
            _r = null;
            s.DeviceInfo = $"OpenGL via Avalonia ({ctx}): INIT FAILED — {ex.GetType().Name}: {ex.Message}";
            Console.Error.WriteLine(s.DeviceInfo + "\n" + ex);
        }
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        _r?.Dispose();
        _r = null;
    }

    protected override void OnOpenGlLost() => _r = null;

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        s.GlRenderCalls++;
        if (_r == null) return;
        s.RenderThreadId = Environment.CurrentManagedThreadId;
        double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var input = s.Input.Snapshot((int)Math.Ceiling(Bounds.Width * scale), (int)Math.Ceiling(Bounds.Height * scale));
        s.Ui.BeginFrame(input.Orbiting);
        try { _r.Render(fb, input); }
        catch (Exception ex)
        {
            s.DeviceInfo += $" | RENDER FAILED — {ex.GetType().Name}: {ex.Message}";
            Console.Error.WriteLine(ex);
            _r = null;
        }
        s.Ui.EndFrame();
        if (_r == null) return;
        s.Hovered = _r.HoveredId;
        if (s.Continuous || input.Orbiting || _r.PickPending) RequestNextFrameRendering();
    }
}

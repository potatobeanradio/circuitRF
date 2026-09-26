using System.Runtime.InteropServices;

namespace Viewer3dSpike.Render.Gl;

/// <summary>
/// macOS only: a headless OpenGL 4.1 core context (CGL, no window) with an offscreen framebuffer,
/// so the harness can drive <see cref="GlRenderer"/> without a display. The Avalonia pane gets its
/// context from Avalonia instead; this exists only for the counters.
/// </summary>
public sealed unsafe class CglOffscreen : IDisposable
{
    const string Cgl = "/System/Library/Frameworks/OpenGL.framework/OpenGL";
    [DllImport(Cgl)] static extern int CGLChoosePixelFormat(int* attribs, out nint pix, out int npix);
    [DllImport(Cgl)] static extern int CGLCreateContext(nint pix, nint share, out nint ctx);
    [DllImport(Cgl)] static extern int CGLSetCurrentContext(nint ctx);
    [DllImport(Cgl)] static extern int CGLDestroyContext(nint ctx);
    [DllImport(Cgl)] static extern int CGLDestroyPixelFormat(nint pix);

    readonly nint _ctx, _lib;
    public int Fbo { get; private set; }
    int _color, _depth;
    public Func<string, nint> GetProc { get; }

    public CglOffscreen()
    {
        int* a = stackalloc int[] { 99, 0x4100, 73, 8, 24, 11, 8, 12, 24, 0 };  // profile GL4 core, accelerated, colour, alpha, depth
        int err = CGLChoosePixelFormat(a, out var pix, out int n);
        if (err != 0 || pix == 0) throw new InvalidOperationException($"CGLChoosePixelFormat failed ({err})");
        err = CGLCreateContext(pix, 0, out _ctx);
        CGLDestroyPixelFormat(pix);
        if (err != 0) throw new InvalidOperationException($"CGLCreateContext failed ({err})");
        CGLSetCurrentContext(_ctx);
        _lib = NativeLibrary.Load(Cgl);
        GetProc = name => NativeLibrary.TryGetExport(_lib, name, out var p) ? p : 0;
    }

    public void CreateTarget(Gl gl, int w, int h)
    {
        Fbo = gl.Gen(gl.GenFramebuffers);
        _color = gl.Gen(gl.GenRenderbuffers);
        _depth = gl.Gen(gl.GenRenderbuffers);
        gl.BindRenderbuffer(Gl.RENDERBUFFER, _color);
        gl.RenderbufferStorage(Gl.RENDERBUFFER, Gl.RGBA8, w, h);
        gl.BindRenderbuffer(Gl.RENDERBUFFER, _depth);
        gl.RenderbufferStorage(Gl.RENDERBUFFER, Gl.DEPTH_COMPONENT24, w, h);
        gl.BindFramebuffer(Gl.FRAMEBUFFER, Fbo);
        gl.FramebufferRenderbuffer(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.RENDERBUFFER, _color);
        gl.FramebufferRenderbuffer(Gl.FRAMEBUFFER, Gl.DEPTH_ATTACHMENT, Gl.RENDERBUFFER, _depth);
        if (gl.CheckFramebufferStatus(Gl.FRAMEBUFFER) != Gl.FRAMEBUFFER_COMPLETE) throw new InvalidOperationException("offscreen FBO incomplete");
    }

    /// <summary>Reads the target back as top-down RGBA rows (harness screenshot only).</summary>
    public byte[] ReadRgba(Gl gl, int w, int h)
    {
        var px = new byte[w * h * 4];
        gl.BindFramebuffer(Gl.FRAMEBUFFER, Fbo);
        fixed (byte* p = px) gl.ReadPixels(0, 0, w, h, Gl.RGBA, Gl.UNSIGNED_BYTE, p);
        var flipped = new byte[px.Length];
        for (int y = 0; y < h; y++) Buffer.BlockCopy(px, (h - 1 - y) * w * 4, flipped, y * w * 4, w * 4);
        return flipped;
    }

    public void Dispose()
    {
        CGLSetCurrentContext(0);
        CGLDestroyContext(_ctx);
    }
}

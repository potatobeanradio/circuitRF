using Viewer3dSpike.Scene;

namespace Viewer3dSpike.Render.Gl;

/// <summary>
/// Route C's renderer: OpenGL 3.3+ core (macOS 4.1, Linux GLX) or OpenGL ES 3.0 (Windows, where
/// Avalonia's default is ANGLE). One vertex buffer, one index buffer, uploaded once. A frame draws the
/// opaque range in ONE call, then the translucent objects sorted back to front, one call each.
/// Picking renders the opaque range into a 1x1 R32UI target with a pick-narrowed projection, then
/// reads that pixel back through a ring of pixel-pack buffers guarded by fences, polled without
/// waiting — so the result lands a frame or more later and the frame never stalls on it.
/// </summary>
public sealed unsafe class GlRenderer : IDisposable
{
    const int RingSize = 3;
    const int RGBA_INTEGER = 0x8D99;
    public readonly Gl GL;
    readonly FrameCounters _counters;
    public readonly bool IsEs;
    public string Info { get; private set; } = "";

    int _vao, _vbo, _ibo, _prog, _pickProg, _pickFbo, _pickColor, _pickDepth;
    int _uVP, _uColors, _uPer, _uHover, _uSel, _uEye, _uPickVP;
    SceneModel _scene = null!;
    TranslucentSorter _sorter = null!;
    readonly int[] _pbo = new int[RingSize];
    readonly nint[] _fence = new nint[RingSize];
    readonly long[] _pbFrame = new long[RingSize];
    int _pbHead;
    public uint HoveredId { get; private set; }
    public int DrawCallsLastFrame { get; private set; }

    public GlRenderer(Func<string, nint> getProc, bool isEs, FrameCounters counters)
    {
        GL = new Gl(getProc);
        IsEs = isEs;
        _counters = counters;
    }

    // 330 core, not 410: Avalonia's GLX context on Linux is not guaranteed to be 4.x, and nothing
    // here needs more than 3.3. macOS's 4.1 core profile accepts it.
    string Header => IsEs ? "#version 300 es\nprecision highp float;\nprecision highp int;\n" : "#version 330 core\n";

    public void Init(SceneModel scene)
    {
        _scene = scene;
        _sorter = new TranslucentSorter(scene.Translucent);
        if (scene.ObjectsPerReplica > 32) throw new InvalidOperationException("colour table holds 32 objects");
        Info = $"{GL.Str(Gl.VERSION)} | {GL.Str(Gl.RENDERER)} | GLSL {GL.Str(Gl.SHADING_LANGUAGE_VERSION)}";

        string vs = Header + """
            layout(location=0) in vec3 aPos;
            layout(location=1) in uint aId;
            uniform mat4 uVP;
            out vec3 vWorld;
            flat out uint vId;
            void main() { vWorld = aPos; vId = aId; gl_Position = uVP * vec4(aPos, 1.0); }
            """;
        string fs = Header + """
            in vec3 vWorld;
            flat in uint vId;
            uniform vec4 uColors[32];
            uniform uint uPer, uHover, uSel;
            uniform vec3 uEye;
            layout(location=0) out vec4 oColor;
            void main() {
                vec3 n = normalize(cross(dFdx(vWorld), dFdy(vWorld)));
                float d = abs(dot(n, normalize(uEye - vWorld)));
                vec4 c = uColors[(vId - 1u) % uPer];
                vec3 rgb = c.rgb * (0.25 + 0.75 * d);
                if (vId == uHover) rgb = mix(rgb, vec3(0.2, 0.9, 1.0), 0.6);
                if (vId == uSel) rgb = mix(rgb, vec3(1.0, 0.3, 1.0), 0.6);
                oColor = vec4(rgb, c.a);
            }
            """;
        string pfs = Header + """
            flat in uint vId;
            in vec3 vWorld;
            layout(location=0) out uint oId;
            void main() { oId = vId; }
            """;
        _prog = GL.Program(vs, fs);
        _pickProg = GL.Program(vs, pfs);
        _uVP = GL.Uniform(_prog, "uVP"); _uColors = GL.Uniform(_prog, "uColors"); _uPer = GL.Uniform(_prog, "uPer");
        _uHover = GL.Uniform(_prog, "uHover"); _uSel = GL.Uniform(_prog, "uSel"); _uEye = GL.Uniform(_prog, "uEye");
        _uPickVP = GL.Uniform(_pickProg, "uVP");

        _vao = GL.Gen(GL.GenVertexArrays);
        GL.BindVertexArray(_vao);
        _vbo = GL.Gen(GL.GenBuffers);
        GL.BindBuffer(Gl.ARRAY_BUFFER, _vbo);
        fixed (byte* p = scene.Vertices) GL.BufferData(Gl.ARRAY_BUFFER, scene.Vertices.Length, p, Gl.STATIC_DRAW);
        _counters.CountUpload(scene.Vertices.Length);
        _ibo = GL.Gen(GL.GenBuffers);
        GL.BindBuffer(Gl.ELEMENT_ARRAY_BUFFER, _ibo);
        fixed (uint* p = scene.Indices) GL.BufferData(Gl.ELEMENT_ARRAY_BUFFER, scene.Indices.Length * 4, p, Gl.STATIC_DRAW);
        _counters.CountUpload(scene.Indices.Length * 4L);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 3, Gl.FLOAT, 0, SceneModel.VertexStride, (void*)0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribIPointer(1, 1, Gl.UNSIGNED_INT, SceneModel.VertexStride, (void*)12);
        GL.BindVertexArray(0);

        GL.UseProgram(_prog);
        fixed (float* c = scene.Colors) GL.Uniform4fv(_uColors, scene.ObjectsPerReplica, c);
        GL.Uniform1ui(_uPer, (uint)scene.ObjectsPerReplica);
        GL.UseProgram(0);

        _pickFbo = GL.Gen(GL.GenFramebuffers);
        _pickColor = GL.Gen(GL.GenRenderbuffers);
        _pickDepth = GL.Gen(GL.GenRenderbuffers);
        GL.BindRenderbuffer(Gl.RENDERBUFFER, _pickColor);
        GL.RenderbufferStorage(Gl.RENDERBUFFER, Gl.R32UI, 1, 1);
        GL.BindRenderbuffer(Gl.RENDERBUFFER, _pickDepth);
        GL.RenderbufferStorage(Gl.RENDERBUFFER, Gl.DEPTH_COMPONENT24, 1, 1);
        GL.BindFramebuffer(Gl.FRAMEBUFFER, _pickFbo);
        GL.FramebufferRenderbuffer(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.RENDERBUFFER, _pickColor);
        GL.FramebufferRenderbuffer(Gl.FRAMEBUFFER, Gl.DEPTH_ATTACHMENT, Gl.RENDERBUFFER, _pickDepth);
        if (GL.CheckFramebufferStatus(Gl.FRAMEBUFFER) != Gl.FRAMEBUFFER_COMPLETE) throw new InvalidOperationException("pick FBO incomplete");
        GL.BindFramebuffer(Gl.FRAMEBUFFER, 0);
        for (int i = 0; i < RingSize; i++)
        {
            _pbo[i] = GL.Gen(GL.GenBuffers);
            GL.BindBuffer(Gl.PIXEL_PACK_BUFFER, _pbo[i]);
            GL.BufferData(Gl.PIXEL_PACK_BUFFER, 16, null, Gl.STREAM_READ);
        }
        GL.BindBuffer(Gl.PIXEL_PACK_BUFFER, 0);
    }

    /// <summary>Draw one frame into <paramref name="fb"/>. The caller brackets it with
    /// FrameCounters.BeginFrame/EndFrame; everything in here is allocation-free.</summary>
    public void Render(int fb, in FrameInput input)
    {
        var gl = GL;
        int draws = 0;
        float* vp = stackalloc float[16];

        PollPicks();

        gl.BindVertexArray(_vao);
        // ---- ID pass: 1x1 target, the cursor pixel only ----
        if (input.PickX >= 0 && input.PickY >= 0)
        {
            int slot = _pbHead % RingSize;
            if (_fence[slot] == 0)
            {
                input.Camera.ViewProjection(new Span<float>(vp, 16), input.Width, input.Height, false, input.PickX, input.PickY);
                gl.BindFramebuffer(Gl.FRAMEBUFFER, _pickFbo);
                gl.Viewport(0, 0, 1, 1);
                uint* zero = stackalloc uint[4];
                float one = 1f;
                gl.ClearBufferuiv(Gl.COLOR, 0, zero);
                gl.ClearBufferfv(Gl.DEPTH, 0, &one);
                gl.Enable(Gl.DEPTH_TEST); gl.DepthFunc(Gl.LESS); gl.DepthMask(1); gl.Disable(Gl.BLEND);
                gl.UseProgram(_pickProg);
                gl.UniformMatrix4fv(_uPickVP, 1, 0, vp);
                gl.DrawElements(Gl.TRIANGLES, _scene.OpaqueIndexCount, Gl.UNSIGNED_INT, (void*)0); draws++;
                gl.BindBuffer(Gl.PIXEL_PACK_BUFFER, _pbo[slot]);
                gl.PixelStorei(Gl.PACK_ALIGNMENT, 4);
                gl.ReadPixels(0, 0, 1, 1, RGBA_INTEGER, Gl.UNSIGNED_INT, (void*)0);
                gl.BindBuffer(Gl.PIXEL_PACK_BUFFER, 0);
                _fence[slot] = gl.FenceSync(Gl.SYNC_GPU_COMMANDS_COMPLETE, 0);
                _pbFrame[slot] = _counters.FrameIndex;
                _pbHead++;
            }
        }

        // ---- colour pass ----
        gl.BindFramebuffer(Gl.FRAMEBUFFER, fb);
        gl.Viewport(0, 0, input.Width, input.Height);
        gl.ClearColor(0.11f, 0.12f, 0.14f, 1f);
        gl.DepthMask(1);
        gl.Clear(Gl.COLOR_BUFFER_BIT | Gl.DEPTH_BUFFER_BIT);
        gl.Enable(Gl.DEPTH_TEST); gl.DepthFunc(Gl.LEQUAL);
        gl.UseProgram(_prog);
        input.Camera.ViewProjection(new Span<float>(vp, 16), input.Width, input.Height, false);
        gl.UniformMatrix4fv(_uVP, 1, 0, vp);
        var eye = input.Camera.Eye;
        gl.Uniform3f(_uEye, eye.X, eye.Y, eye.Z);
        gl.Uniform1ui(_uHover, HoveredId);
        gl.Uniform1ui(_uSel, input.Selected);
        _counters.CountUniform(64 + 12 + 8);
        gl.Disable(Gl.BLEND);
        gl.DrawElements(Gl.TRIANGLES, _scene.OpaqueIndexCount, Gl.UNSIGNED_INT, (void*)0); draws++;

        _sorter.Sort(input.Camera);
        gl.Enable(Gl.BLEND);
        // alpha channel stays 1: the compositor blends this surface over the window, so a
        // translucent object must not punch a hole through the pane
        gl.BlendFuncSeparate(Gl.SRC_ALPHA, Gl.ONE_MINUS_SRC_ALPHA, Gl.ONE, Gl.ONE_MINUS_SRC_ALPHA);
        gl.DepthMask(0);
        var tr = _scene.Translucent;
        foreach (int i in _sorter.Order)
        {
            gl.DrawElements(Gl.TRIANGLES, tr[i].IndexCount, Gl.UNSIGNED_INT, (void*)(tr[i].FirstIndex * 4L)); draws++;
        }
        gl.DepthMask(1);
        gl.Disable(Gl.BLEND);
        gl.UseProgram(0);
        gl.BindVertexArray(0);
        DrawCallsLastFrame = draws;
    }

    void PollPicks()
    {
        for (int k = 0; k < RingSize; k++)
        {
            int slot = (_pbHead + k) % RingSize;   // oldest first
            if (_fence[slot] == 0) continue;
            int r = GL.ClientWaitSync(_fence[slot], 0, 0);
            if (r != Gl.ALREADY_SIGNALED && r != Gl.CONDITION_SATISFIED) continue;
            GL.DeleteSync(_fence[slot]);
            _fence[slot] = 0;
            GL.BindBuffer(Gl.PIXEL_PACK_BUFFER, _pbo[slot]);
            var p = (uint*)GL.MapBufferRange(Gl.PIXEL_PACK_BUFFER, 0, 16, Gl.MAP_READ_BIT);
            if (p != null) { HoveredId = p[0]; GL.UnmapBuffer(Gl.PIXEL_PACK_BUFFER); }
            GL.BindBuffer(Gl.PIXEL_PACK_BUFFER, 0);
            _counters.PickResolved(_pbFrame[slot]);
        }
    }

    /// <summary>True when a pick is still in flight (the harness uses it to drain before exit).</summary>
    public bool PickPending { get { foreach (var f in _fence) if (f != 0) return true; return false; } }

    public void Dispose()
    {
        var gl = GL;
        int v;
        v = _vao; gl.DeleteVertexArrays(1, &v);
        v = _vbo; gl.DeleteBuffers(1, &v);
        v = _ibo; gl.DeleteBuffers(1, &v);
        foreach (int b in _pbo) { v = b; gl.DeleteBuffers(1, &v); }
        foreach (var f in _fence) if (f != 0) gl.DeleteSync(f);
        v = _pickFbo; gl.DeleteFramebuffers(1, &v);
        v = _pickColor; gl.DeleteRenderbuffers(1, &v);
        v = _pickDepth; gl.DeleteRenderbuffers(1, &v);
        gl.DeleteProgram(_prog); gl.DeleteProgram(_pickProg);
    }
}

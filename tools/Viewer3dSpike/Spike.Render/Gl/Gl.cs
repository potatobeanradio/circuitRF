using System.Runtime.InteropServices;

namespace Viewer3dSpike.Render.Gl;

/// <summary>
/// The GL entry points route C needs, as unmanaged function pointers resolved through whatever
/// GetProcAddress the host supplies (Avalonia's context, or the harness's own CGL context). Calling
/// through a function pointer allocates nothing, which the per-frame allocation counter relies on.
/// </summary>
public sealed unsafe class Gl
{
    public const int ARRAY_BUFFER = 0x8892, ELEMENT_ARRAY_BUFFER = 0x8893, PIXEL_PACK_BUFFER = 0x88EB, STATIC_DRAW = 0x88E4, STREAM_READ = 0x88E1;
    public const int FLOAT = 0x1406, UNSIGNED_INT = 0x1405, UNSIGNED_BYTE = 0x1401, TRIANGLES = 4;
    public const int VERTEX_SHADER = 0x8B31, FRAGMENT_SHADER = 0x8B30, COMPILE_STATUS = 0x8B81, LINK_STATUS = 0x8B82;
    public const int COLOR_BUFFER_BIT = 0x4000, DEPTH_BUFFER_BIT = 0x100, DEPTH_TEST = 0x0B71, BLEND = 0x0BE2, SCISSOR_TEST = 0x0C11;
    public const int ONE = 1, LEQUAL = 0x0203, LESS = 0x0201, SRC_ALPHA = 0x0302, ONE_MINUS_SRC_ALPHA = 0x0303;
    public const int FRAMEBUFFER = 0x8D40, READ_FRAMEBUFFER = 0x8CA8, DRAW_FRAMEBUFFER = 0x8CA9, RENDERBUFFER = 0x8D41;
    public const int COLOR_ATTACHMENT0 = 0x8CE0, DEPTH_ATTACHMENT = 0x8D00, FRAMEBUFFER_COMPLETE = 0x8CD5;
    public const int R32UI = 0x8236, RGBA8 = 0x8058, DEPTH_COMPONENT24 = 0x81A6, RED_INTEGER = 0x8D94, RGBA = 0x1908, COLOR = 0x1800, DEPTH = 0x1801;
    public const int SYNC_GPU_COMMANDS_COMPLETE = 0x9117, ALREADY_SIGNALED = 0x911A, CONDITION_SATISFIED = 0x911C, MAP_READ_BIT = 0x0001;
    public const int VERSION = 0x1F02, RENDERER = 0x1F01, VENDOR = 0x1F00, SHADING_LANGUAGE_VERSION = 0x8B8C, FRAMEBUFFER_BINDING = 0x8CA6;
    public const int PACK_ALIGNMENT = 0x0D05, VERTEX_ARRAY_BINDING = 0x85B5;

    public delegate* unmanaged<int, byte*> GetString;
    public delegate* unmanaged<int> GetError;
    public delegate* unmanaged<int, int*, void> GenVertexArrays, GenBuffers, GenFramebuffers, GenRenderbuffers;
    public delegate* unmanaged<int, int*, void> DeleteVertexArrays, DeleteBuffers, DeleteFramebuffers, DeleteRenderbuffers;
    public delegate* unmanaged<int, void> BindVertexArray, UseProgram, CompileShader, LinkProgram, Enable, Disable, DepthFunc, DepthMask, Clear, EnableVertexAttribArray, DeleteProgram, DeleteShader;
    public delegate* unmanaged<int, int, void> BindBuffer, BindFramebuffer, BindRenderbuffer, AttachShader, BlendFunc, Uniform1i, PixelStorei;
    public delegate* unmanaged<int, uint, void> Uniform1ui;
    public delegate* unmanaged<int, nint, void*, int, void> BufferData;
    public delegate* unmanaged<int, int, int, int, int, void*, void> VertexAttribPointer;
    public delegate* unmanaged<int, int, int, int, void*, void> VertexAttribIPointer;
    public delegate* unmanaged<int, int> CreateShader, CheckFramebufferStatus;
    public delegate* unmanaged<int> CreateProgram;
    public delegate* unmanaged<int, int, byte**, int*, void> ShaderSource;
    public delegate* unmanaged<int, int, int*, void> GetShaderiv, GetProgramiv;
    public delegate* unmanaged<int, int, int*, byte*, void> GetShaderInfoLog, GetProgramInfoLog;
    public delegate* unmanaged<int, byte*, int> GetUniformLocation;
    public delegate* unmanaged<int, int, byte, float*, void> UniformMatrix4fv;
    public delegate* unmanaged<int, int, float*, void> Uniform4fv;
    public delegate* unmanaged<int, float, float, float, void> Uniform3f;
    public delegate* unmanaged<int, int, int, int, void> Viewport, Scissor;
    public delegate* unmanaged<float, float, float, float, void> ClearColor;
    public delegate* unmanaged<int, int, uint*, void> ClearBufferuiv;
    public delegate* unmanaged<int, int, float*, void> ClearBufferfv;
    public delegate* unmanaged<int, int, int, void*, void> DrawElements;
    public delegate* unmanaged<int, int, int, int, void> FramebufferRenderbuffer, RenderbufferStorage;
    public delegate* unmanaged<int, int, int, int, int, int, void*, void> ReadPixels;
    public delegate* unmanaged<int, int, nint> FenceSync;
    public delegate* unmanaged<nint, int, ulong, int> ClientWaitSync;
    public delegate* unmanaged<nint, void> DeleteSync;
    public delegate* unmanaged<int, nint, nint, int, void*> MapBufferRange;
    public delegate* unmanaged<int, byte> UnmapBuffer;
    public delegate* unmanaged<int, int*, void> GetIntegerv;
    public delegate* unmanaged<void> Finish, Flush;
    public delegate* unmanaged<int, int*, void> DrawBuffers;
    public delegate* unmanaged<int, int, int, int, void> BlendFuncSeparate;

    public Gl(Func<string, nint> load)
    {
        nint L(string n)
        {
            var p = load(n);
            if (p == 0) throw new EntryPointNotFoundException(n);
            return p;
        }
        GetString = (delegate* unmanaged<int, byte*>)L("glGetString");
        GetError = (delegate* unmanaged<int>)L("glGetError");
        GenVertexArrays = (delegate* unmanaged<int, int*, void>)L("glGenVertexArrays");
        GenBuffers = (delegate* unmanaged<int, int*, void>)L("glGenBuffers");
        GenFramebuffers = (delegate* unmanaged<int, int*, void>)L("glGenFramebuffers");
        GenRenderbuffers = (delegate* unmanaged<int, int*, void>)L("glGenRenderbuffers");
        DeleteVertexArrays = (delegate* unmanaged<int, int*, void>)L("glDeleteVertexArrays");
        DeleteBuffers = (delegate* unmanaged<int, int*, void>)L("glDeleteBuffers");
        DeleteFramebuffers = (delegate* unmanaged<int, int*, void>)L("glDeleteFramebuffers");
        DeleteRenderbuffers = (delegate* unmanaged<int, int*, void>)L("glDeleteRenderbuffers");
        BindVertexArray = (delegate* unmanaged<int, void>)L("glBindVertexArray");
        UseProgram = (delegate* unmanaged<int, void>)L("glUseProgram");
        CompileShader = (delegate* unmanaged<int, void>)L("glCompileShader");
        LinkProgram = (delegate* unmanaged<int, void>)L("glLinkProgram");
        Enable = (delegate* unmanaged<int, void>)L("glEnable");
        Disable = (delegate* unmanaged<int, void>)L("glDisable");
        DepthFunc = (delegate* unmanaged<int, void>)L("glDepthFunc");
        DepthMask = (delegate* unmanaged<int, void>)L("glDepthMask");
        Clear = (delegate* unmanaged<int, void>)L("glClear");
        EnableVertexAttribArray = (delegate* unmanaged<int, void>)L("glEnableVertexAttribArray");
        DeleteProgram = (delegate* unmanaged<int, void>)L("glDeleteProgram");
        DeleteShader = (delegate* unmanaged<int, void>)L("glDeleteShader");
        BindBuffer = (delegate* unmanaged<int, int, void>)L("glBindBuffer");
        BindFramebuffer = (delegate* unmanaged<int, int, void>)L("glBindFramebuffer");
        BindRenderbuffer = (delegate* unmanaged<int, int, void>)L("glBindRenderbuffer");
        AttachShader = (delegate* unmanaged<int, int, void>)L("glAttachShader");
        BlendFunc = (delegate* unmanaged<int, int, void>)L("glBlendFunc");
        Uniform1i = (delegate* unmanaged<int, int, void>)L("glUniform1i");
        PixelStorei = (delegate* unmanaged<int, int, void>)L("glPixelStorei");
        Uniform1ui = (delegate* unmanaged<int, uint, void>)L("glUniform1ui");
        BufferData = (delegate* unmanaged<int, nint, void*, int, void>)L("glBufferData");
        VertexAttribPointer = (delegate* unmanaged<int, int, int, int, int, void*, void>)L("glVertexAttribPointer");
        VertexAttribIPointer = (delegate* unmanaged<int, int, int, int, void*, void>)L("glVertexAttribIPointer");
        CreateShader = (delegate* unmanaged<int, int>)L("glCreateShader");
        CheckFramebufferStatus = (delegate* unmanaged<int, int>)L("glCheckFramebufferStatus");
        CreateProgram = (delegate* unmanaged<int>)L("glCreateProgram");
        ShaderSource = (delegate* unmanaged<int, int, byte**, int*, void>)L("glShaderSource");
        GetShaderiv = (delegate* unmanaged<int, int, int*, void>)L("glGetShaderiv");
        GetProgramiv = (delegate* unmanaged<int, int, int*, void>)L("glGetProgramiv");
        GetShaderInfoLog = (delegate* unmanaged<int, int, int*, byte*, void>)L("glGetShaderInfoLog");
        GetProgramInfoLog = (delegate* unmanaged<int, int, int*, byte*, void>)L("glGetProgramInfoLog");
        GetUniformLocation = (delegate* unmanaged<int, byte*, int>)L("glGetUniformLocation");
        UniformMatrix4fv = (delegate* unmanaged<int, int, byte, float*, void>)L("glUniformMatrix4fv");
        Uniform4fv = (delegate* unmanaged<int, int, float*, void>)L("glUniform4fv");
        Uniform3f = (delegate* unmanaged<int, float, float, float, void>)L("glUniform3f");
        Viewport = (delegate* unmanaged<int, int, int, int, void>)L("glViewport");
        Scissor = (delegate* unmanaged<int, int, int, int, void>)L("glScissor");
        ClearColor = (delegate* unmanaged<float, float, float, float, void>)L("glClearColor");
        ClearBufferuiv = (delegate* unmanaged<int, int, uint*, void>)L("glClearBufferuiv");
        ClearBufferfv = (delegate* unmanaged<int, int, float*, void>)L("glClearBufferfv");
        DrawElements = (delegate* unmanaged<int, int, int, void*, void>)L("glDrawElements");
        FramebufferRenderbuffer = (delegate* unmanaged<int, int, int, int, void>)L("glFramebufferRenderbuffer");
        RenderbufferStorage = (delegate* unmanaged<int, int, int, int, void>)L("glRenderbufferStorage");
        ReadPixels = (delegate* unmanaged<int, int, int, int, int, int, void*, void>)L("glReadPixels");
        FenceSync = (delegate* unmanaged<int, int, nint>)L("glFenceSync");
        ClientWaitSync = (delegate* unmanaged<nint, int, ulong, int>)L("glClientWaitSync");
        DeleteSync = (delegate* unmanaged<nint, void>)L("glDeleteSync");
        MapBufferRange = (delegate* unmanaged<int, nint, nint, int, void*>)L("glMapBufferRange");
        UnmapBuffer = (delegate* unmanaged<int, byte>)L("glUnmapBuffer");
        GetIntegerv = (delegate* unmanaged<int, int*, void>)L("glGetIntegerv");
        Finish = (delegate* unmanaged<void>)L("glFinish");
        Flush = (delegate* unmanaged<void>)L("glFlush");
        DrawBuffers = (delegate* unmanaged<int, int*, void>)L("glDrawBuffers");
        BlendFuncSeparate = (delegate* unmanaged<int, int, int, int, void>)L("glBlendFuncSeparate");
    }

    public string Str(int name) => Marshal.PtrToStringAnsi((nint)GetString(name)) ?? "";

    public int Gen(delegate* unmanaged<int, int*, void> f) { int v; f(1, &v); return v; }

    public int Uniform(int program, string name)
    {
        var b = System.Text.Encoding.ASCII.GetBytes(name + "\0");
        fixed (byte* p = b) return GetUniformLocation(program, p);
    }

    public int Program(string vs, string fs)
    {
        int Compile(int type, string src)
        {
            int s = CreateShader(type);
            var b = System.Text.Encoding.UTF8.GetBytes(src);
            fixed (byte* p = b)
            {
                byte* pp = p; int len = b.Length;
                ShaderSource(s, 1, &pp, &len);
            }
            CompileShader(s);
            int ok; GetShaderiv(s, COMPILE_STATUS, &ok);
            if (ok == 0) throw new InvalidOperationException("shader: " + Log(s, GetShaderInfoLog) + "\n" + src);
            return s;
        }
        int v = Compile(VERTEX_SHADER, vs), f = Compile(FRAGMENT_SHADER, fs);
        int prog = CreateProgram();
        AttachShader(prog, v); AttachShader(prog, f);
        LinkProgram(prog);
        int linked; GetProgramiv(prog, LINK_STATUS, &linked);
        if (linked == 0) throw new InvalidOperationException("link: " + Log(prog, GetProgramInfoLog));
        DeleteShader(v); DeleteShader(f);
        return prog;
    }

    static string Log(int obj, delegate* unmanaged<int, int, int*, byte*, void> f)
    {
        var buf = new byte[4096]; int len;
        fixed (byte* p = buf) f(obj, buf.Length, &len, p);
        return System.Text.Encoding.UTF8.GetString(buf, 0, len);
    }
}

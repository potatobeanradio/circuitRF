using System.Diagnostics;
using System.IO.Compression;
using System.Numerics;
using Viewer3dSpike.Render;
using Viewer3dSpike.Scene;

// Brief em3d-27 §3: the counter harness. Drives one route's renderer OFFSCREEN (no window, no
// Avalonia) through an orbit, with a moving pick every frame, paced at a display rate, and asserts
// the counters. Usage:
//   dotnet run -c Release --project Spike.Harness -- --route gl|metal|wgpu|d3d11|vulkan [--msh case.msh]
//          [--frames 300] [--size 1600x1000] [--tris 1000000] [--hz 60] [--png out.png]
var opt = Args.Parse(args);
var sw = Stopwatch.StartNew();
SceneModel scene;
string source;
if (opt.Msh != null && File.Exists(opt.Msh))
{
    var mesh = MshReader.Read(opt.Msh);
    source = $"{Path.GetFileName(opt.Msh)} ({mesh.Triangles.Count:N0} surface triangles, {mesh.Tets.Count:N0} tets)";
    scene = SceneModel.FromMsh(mesh, opt.Tris);
}
else
{
    source = "SYNTHETIC stand-in (no --msh given or file missing)";
    scene = SceneModel.Synthetic(opt.Tris);
}
Console.WriteLine($"scene   {source}: {scene.Describe()}  [{sw.ElapsedMilliseconds} ms]");

#if DEBUG
const string Config = "Debug";
#else
const string Config = "Release";
#endif

var counters = new FrameCounters($"{opt.Route}/{Config}/render");
using IHarnessBackend backend = opt.Route switch
{
    "gl" => new GlBackend(),
    "metal" => new MetalBackend(),
    "wgpu" => new WgpuBackend(),
    "wgpu-bridge" => new WgpuBridgeBackend(),
    "d3d11" => new D3D11Backend(),
    "vulkan" => new VulkanBackend(),
    _ => throw new ArgumentException("--route gl|metal|wgpu|wgpu-bridge|d3d11|vulkan")
};
sw.Restart();
backend.Init(scene, counters, opt.W, opt.H);
long initUpload = counters.UploadBytesTotal;
Console.WriteLine($"device  {backend.Info}");
Console.WriteLine($"init    uploaded {initUpload:N0} B once (VB+IB = {scene.Vertices.Length + scene.Indices.Length * 4L:N0} B)  [{sw.ElapsedMilliseconds} ms]");

// ---- 1. does it pick? top-down view, cursor on the highest wire vertex of replica 0 ----
var cam = Camera.Frame(scene.BoundsMin, scene.BoundsMax);
uint wireId = 0; Vector3 wireTop = default;
{
    // the wire is several Gmsh entities; the target is whichever holds replica 0's highest wire vertex
    var f = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(scene.Vertices.AsSpan());
    var u = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(scene.Vertices.AsSpan());
    float best = float.MinValue;
    for (int v = 0; v < scene.VertexCount; v++)
    {
        uint id = u[4 * v + 3];
        if (id > scene.ObjectsPerReplica || !scene.Objects[id - 1].Name.StartsWith("wire")) continue;
        if (f[4 * v + 2] > best) { best = f[4 * v + 2]; wireTop = new Vector3(f[4 * v], f[4 * v + 1], f[4 * v + 2]); wireId = id; }
    }
}
var pickCam = cam with { Target = wireTop with { Z = 0 }, Pitch = 1.5f, Yaw = -1.5707f, Distance = 3000 };
var (px, py) = Project(pickCam, wireTop, opt.W, opt.H);
var input = new FrameInput { Camera = pickCam, Width = opt.W, Height = opt.H, PickX = px, PickY = py };
// paced like the orbit: the first frames sit behind the one-time upload and pipeline builds
for (int i = 0; i < 30; i++)
{
    counters.BeginFrame(false); backend.Frame(input); counters.EndFrame(); backend.EndOfFrame();
    if (Environment.GetEnvironmentVariable("SPIKE_DEBUG") != null) Console.WriteLine($"  pick frame {i}: hovered {backend.Hovered} pending {backend.PickPending}");
    Thread.Sleep(1000 / opt.Hz);
}
input.PickX = -1;
for (int i = 0; i < 30 && backend.PickPending; i++) { counters.BeginFrame(false); backend.Frame(input); counters.EndFrame(); backend.EndOfFrame(); Thread.Sleep(1000 / opt.Hz); }
uint got = backend.Hovered;
bool picked = got >= 1 && got <= scene.ObjectsPerReplica && scene.Objects[got - 1].Name.StartsWith("wire");
Console.WriteLine($"pick    cursor ({px:F0},{py:F0}) over {scene.Objects[wireId - 1].Name} (id {wireId}): read back id {got} ({(got >= 1 && got <= scene.ObjectsPerReplica ? scene.Objects[got - 1].Name : "background/other replica")}) -> {(picked ? "PICKED a wire entity" : "WRONG")}");
if (opt.Png != null) { WritePng(opt.Png.Replace(".png", "-pick.png"), backend.Screenshot(), opt.W, opt.H); }

// ---- 2. orbit with a moving pick, paced ----
counters.Reset();
input = new FrameInput { Camera = cam, Width = opt.W, Height = opt.H };
double period = 1.0 / opt.Hz;
var frameClock = Stopwatch.StartNew();
for (int i = 0; i < opt.Frames; i++)
{
    double due = i * period;
    while (frameClock.Elapsed.TotalSeconds < due) Thread.SpinWait(200);
    input.Camera.Orbit(4f, MathF.Sin(i * 0.05f) * 1.5f);
    input.PickX = opt.W * (0.3f + 0.4f * (0.5f + 0.5f * MathF.Sin(i * 0.037f)));
    input.PickY = opt.H * (0.3f + 0.4f * (0.5f + 0.5f * MathF.Cos(i * 0.029f)));
    input.Orbiting = true;
    counters.BeginFrame(orbiting: true);
    backend.Frame(input);
    counters.EndFrame();
    backend.EndOfFrame();
}
input.PickX = -1;
for (int i = 0; i < 6 && backend.PickPending; i++) { counters.BeginFrame(false); backend.Frame(input); counters.EndFrame(); backend.EndOfFrame(); }
var s = counters.Summarize();
Console.WriteLine($"orbit   {s}");
Console.WriteLine($"        draw calls/frame {backend.DrawCalls}, wall {frameClock.Elapsed.TotalSeconds:F2} s for {opt.Frames} frames at {opt.Hz} Hz pacing");
if (opt.Png != null) WritePng(opt.Png, backend.Screenshot(), opt.W, opt.H);

bool okUpload = s.OrbitUploadMax == 0;
bool okAlloc = s.AllocSteadyMax == 0;
bool okPick = s.Picks > 0 && s.PickLatencyMax <= 1;
Console.WriteLine($"ASSERT  bytes uploaded per orbit frame = 0 ........ {(okUpload ? "PASS" : "FAIL")} (max {s.OrbitUploadMax} B)");
Console.WriteLine($"ASSERT  picking reads back the object under cursor . {(picked ? "PASS" : "FAIL")}");
Console.WriteLine($"TARGET  managed alloc per frame ~0 (steady state) .. {(okAlloc ? "MET" : "NOT MET")} (max {s.AllocSteadyMax} B, first-10% max {s.AllocMax} B)");
Console.WriteLine($"TARGET  pick latency 1 frame ....................... {(okPick ? "MET" : "NOT MET")} (min {s.PickLatencyMin} max {s.PickLatencyMax} mean {s.PickLatencyMean:F2})");
Console.WriteLine($"REPORT  pane time per frame p50 {s.MsP50:F3} ms, p95 {s.MsP95:F3} ms, max {s.MsMax:F3} ms ({Config})");
return okUpload && picked ? 0 : 1;

static (float X, float Y) Project(Camera c, Vector3 p, int w, int h)
{
    Span<float> m = stackalloc float[16];
    c.ViewProjection(m, w, h, true);
    float x = m[0] * p.X + m[4] * p.Y + m[8] * p.Z + m[12];
    float y = m[1] * p.X + m[5] * p.Y + m[9] * p.Z + m[13];
    float ww = m[3] * p.X + m[7] * p.Y + m[11] * p.Z + m[15];
    return ((x / ww * 0.5f + 0.5f) * w - 0.5f, (0.5f - y / ww * 0.5f) * h - 0.5f);
}

static void WritePng(string path, byte[] rgba, int w, int h)
{
    using var fs = File.Create(path);
    fs.Write([137, 80, 78, 71, 13, 10, 26, 10]);
    void Chunk(string type, byte[] data)
    {
        var len = BitConverter.GetBytes(data.Length); Array.Reverse(len); fs.Write(len);
        var t = System.Text.Encoding.ASCII.GetBytes(type); fs.Write(t); fs.Write(data);
        var crc = BitConverter.GetBytes(Crc32([.. t, .. data])); Array.Reverse(crc); fs.Write(crc);
    }
    var ihdr = new byte[13];
    ihdr[0] = (byte)(w >> 24); ihdr[1] = (byte)(w >> 16); ihdr[2] = (byte)(w >> 8); ihdr[3] = (byte)w;
    ihdr[4] = (byte)(h >> 24); ihdr[5] = (byte)(h >> 16); ihdr[6] = (byte)(h >> 8); ihdr[7] = (byte)h;
    ihdr[8] = 8; ihdr[9] = 6;
    Chunk("IHDR", ihdr);
    using var ms = new MemoryStream();
    using (var z = new ZLibStream(ms, CompressionLevel.Fastest, true))
        for (int y = 0; y < h; y++) { z.WriteByte(0); z.Write(rgba, y * w * 4, w * 4); }
    Chunk("IDAT", ms.ToArray());
    Chunk("IEND", []);
    Console.WriteLine($"png     {path}");
}

static uint Crc32(byte[] d)
{
    uint c = 0xFFFFFFFF;
    foreach (byte b in d) { c ^= b; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1; }
    return ~c;
}

sealed class Args
{
    public string Route = "gl";
    public string? Msh, Png;
    public int Frames = 300, W = 1600, H = 1000, Tris = 1_000_000, Hz = 60;
    public static Args Parse(string[] a)
    {
        var o = new Args();
        for (int i = 0; i < a.Length; i++)
            switch (a[i])
            {
                case "--route": o.Route = a[++i]; break;
                case "--msh": o.Msh = a[++i]; break;
                case "--png": o.Png = a[++i]; break;
                case "--frames": o.Frames = int.Parse(a[++i]); break;
                case "--tris": o.Tris = int.Parse(a[++i]); break;
                case "--hz": o.Hz = int.Parse(a[++i]); break;
                case "--size": var p = a[++i].Split('x'); o.W = int.Parse(p[0]); o.H = int.Parse(p[1]); break;
                default: throw new ArgumentException($"unknown argument {a[i]}");
            }
        return o;
    }
}

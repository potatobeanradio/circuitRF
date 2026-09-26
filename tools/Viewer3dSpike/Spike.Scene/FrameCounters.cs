using System.Diagnostics;

namespace Viewer3dSpike.Scene;

/// <summary>
/// The four counters of brief 27 §3 (em-3d.md §8.6), written to be lifted into brief 28 as they are:
/// <list type="number">
/// <item><b>bytes uploaded per frame</b> — every GPU buffer/texture upload goes through
///   <see cref="CountUpload"/>; an orbit frame must add 0. Per-frame uniform bytes (the camera matrix,
///   the hover id) are counted apart by <see cref="CountUniform"/>, because some APIs (WebGPU core)
///   can only deliver a uniform through a buffer write — reported, and never geometry.</item>
/// <item><b>managed allocations per frame</b> on the thread that runs the pane's code, from the
///   <see cref="GC.GetAllocatedBytesForCurrentThread"/> delta across the frame.</item>
/// <item><b>time per frame in the pane's own code</b> on that thread — a distribution, reported,
///   never asserted (timings measure the machine).</item>
/// <item><b>pick readback latency in frames</b>: frames from the one that drew the ID pixel to the
///   one that read it back.</item>
/// </list>
/// One instance per thread lane (UI thread; render thread where a route has one). The hot path
/// (<see cref="BeginFrame"/>/<see cref="EndFrame"/>/<see cref="CountUpload"/>) allocates nothing.
/// </summary>
public sealed class FrameCounters(string lane, int capacity = 8192)
{
    public readonly string Lane = lane;
    long _uploadTotal, _uniformTotal;
    long _uploadAtBegin, _uniformAtBegin, _allocAtBegin, _ticksAtBegin;
    bool _orbitThisFrame, _inFrame;
    public long FrameIndex { get; private set; }

    readonly long[] _upload = new long[capacity], _uniform = new long[capacity], _alloc = new long[capacity], _ticks = new long[capacity];
    readonly bool[] _orbit = new bool[capacity];
    int _count;
    readonly int[] _pickLatency = new int[capacity];
    int _pickCount;

    public long UploadBytesTotal => Interlocked.Read(ref _uploadTotal);
    public void CountUpload(long bytes) => Interlocked.Add(ref _uploadTotal, bytes);
    public void CountUniform(long bytes) => Interlocked.Add(ref _uniformTotal, bytes);

    public void BeginFrame(bool orbiting)
    {
        _inFrame = true;
        _orbitThisFrame = orbiting;
        _uploadAtBegin = Interlocked.Read(ref _uploadTotal);
        _uniformAtBegin = Interlocked.Read(ref _uniformTotal);
        _allocAtBegin = GC.GetAllocatedBytesForCurrentThread();
        _ticksAtBegin = Stopwatch.GetTimestamp();
    }

    public void EndFrame()
    {
        if (!_inFrame) return;
        long ticks = Stopwatch.GetTimestamp() - _ticksAtBegin;
        long alloc = GC.GetAllocatedBytesForCurrentThread() - _allocAtBegin;
        int i = _count % _upload.Length;
        _upload[i] = Interlocked.Read(ref _uploadTotal) - _uploadAtBegin;
        _uniform[i] = Interlocked.Read(ref _uniformTotal) - _uniformAtBegin;
        _alloc[i] = alloc;
        _ticks[i] = ticks;
        _orbit[i] = _orbitThisFrame;
        _count++;
        FrameIndex++;
        _inFrame = false;
    }

    /// <summary>A pick issued at frame <paramref name="drawnAtFrame"/> was read back now.</summary>
    public void PickResolved(long drawnAtFrame)
    {
        _pickLatency[_pickCount % _pickLatency.Length] = (int)(FrameIndex - drawnAtFrame);
        _pickCount++;
    }

    public void Reset() { _count = 0; _pickCount = 0; }

    /// <summary>Last-frame figures for a live status line (no allocation beyond the caller's format).</summary>
    public (long Upload, long Uniform, long Alloc, double Ms) Last
    {
        get
        {
            if (_count == 0) return default;
            int i = (_count - 1) % _upload.Length;
            return (_upload[i], _uniform[i], _alloc[i], _ticks[i] * 1000.0 / Stopwatch.Frequency);
        }
    }

    public int LastPickLatency => _pickCount == 0 ? -1 : _pickLatency[(_pickCount - 1) % _pickLatency.Length];

    public sealed record Summary(string Lane, int Frames, int OrbitFrames, long OrbitUploadMax, long OrbitUploadTotal,
        double UniformPerFrame, long AllocMax, double AllocMean, long AllocSteadyMax,
        double MsP50, double MsP95, double MsMax, int Picks, int PickLatencyMin, int PickLatencyMax, double PickLatencyMean, int[] PickHistogram)
    {
        public override string ToString() =>
            $"[{Lane}] frames {Frames} (orbit {OrbitFrames}) | upload/orbit-frame max {OrbitUploadMax} B (total {OrbitUploadTotal}) | " +
            $"uniform {UniformPerFrame:F0} B/frame | alloc/frame max {AllocMax} B, mean {AllocMean:F1} B, steady-state max {AllocSteadyMax} B | " +
            $"pane ms p50 {MsP50:F3} p95 {MsP95:F3} max {MsMax:F3} | picks {Picks}, latency frames min {PickLatencyMin} max {PickLatencyMax} mean {PickLatencyMean:F2} (frames 0/1/2/3+: {string.Join("/", PickHistogram)})";
    }

    /// <summary>Statistics over the recorded frames. Allocates; call it off the hot path.
    /// "Steady state" skips the first 10 % of frames (JIT, first-use caches).</summary>
    public Summary Summarize()
    {
        int n = Math.Min(_count, _upload.Length);
        var ms = new double[n];
        long orbitMax = 0, orbitTotal = 0, allocMax = 0, steadyMax = 0, allocSum = 0, uniSum = 0;
        int orbitFrames = 0, skip = n / 10;
        for (int i = 0; i < n; i++)
        {
            ms[i] = _ticks[i] * 1000.0 / Stopwatch.Frequency;
            if (_orbit[i]) { orbitFrames++; orbitMax = Math.Max(orbitMax, _upload[i]); orbitTotal += _upload[i]; }
            allocMax = Math.Max(allocMax, _alloc[i]);
            if (i >= skip) steadyMax = Math.Max(steadyMax, _alloc[i]);
            allocSum += _alloc[i]; uniSum += _uniform[i];
        }
        Array.Sort(ms);
        double P(double q) => n == 0 ? 0 : ms[Math.Min(n - 1, (int)(q * n))];
        int pn = Math.Min(_pickCount, _pickLatency.Length);
        var pl = _pickLatency.AsSpan(0, pn);
        return new Summary(Lane, n, orbitFrames, orbitMax, orbitTotal, n == 0 ? 0 : uniSum / (double)n, allocMax,
            n == 0 ? 0 : allocSum / (double)n, steadyMax, P(0.5), P(0.95), n == 0 ? 0 : ms[^1],
            pn, pn == 0 ? -1 : Min(pl), pn == 0 ? -1 : Max(pl), pn == 0 ? 0 : Sum(pl) / (double)pn, Histogram(pl));
    }

    static int[] Histogram(ReadOnlySpan<int> s) { var h = new int[4]; foreach (var v in s) h[Math.Clamp(v, 0, 3)]++; return h; }
    static int Min(ReadOnlySpan<int> s) { int m = int.MaxValue; foreach (var v in s) m = Math.Min(m, v); return m; }
    static int Max(ReadOnlySpan<int> s) { int m = int.MinValue; foreach (var v in s) m = Math.Max(m, v); return m; }
    static long Sum(ReadOnlySpan<int> s) { long m = 0; foreach (var v in s) m += v; return m; }
}

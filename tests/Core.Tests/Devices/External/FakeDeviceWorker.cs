using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using CircuitRF.Core.Devices.External;

namespace CircuitRF.Core.Tests.Devices.External;

/// <summary>
/// A device worker that is not a process: it speaks the real wire format over in-memory streams,
/// so the provider, the command shapes and the evaluation decoding are all exercised end to end
/// without needing a compiled device model to be present.
///
/// <para>Its device behaviour is chosen to make a wrong decode visible rather than plausible — see
/// <see cref="EvaluateModel"/>.</para>
///
/// <para>It deliberately declares a parameter called <c>TYPE</c>, which differs from circuitRF's own
/// <c>Type</c> selector only in case. Real compact models do — a MOS model's <c>TYPE</c> is its
/// channel polarity — and a case-blind reading of the selector eats it, leaving a device that builds,
/// solves, and is the wrong transistor.</para>
/// </summary>
public sealed class FakeDeviceWorker : IDeviceWorkerTransport
{
    /// <summary>A nonlinear type with internal nodes, one of which follows another.</summary>
    public const string NonlinearType = "generic_nonlinear_v1";

    /// <summary>A linear-only type, which the worker refuses to instantiate nonlinearly.</summary>
    public const string LinearOnlyType = "generic_coupler_v1";

    public const int ExternalPins  = 4;
    public const int InternalNodes = 2;
    public const int Nodes         = ExternalPins + InternalNodes;

    private readonly RequestStream _requests;
    private readonly ReplyStream   _replies = new();

    private readonly Dictionary<int, Dictionary<string, string>> _instances = [];
    private int _nextHandle = 1;

    /// <summary>Every command verb received, in order. Lets a test assert round-trip counts.</summary>
    public List<string> Commands { get; } = [];

    /// <summary>Parameters the worker was actually given, by handle.</summary>
    public IReadOnlyDictionary<int, Dictionary<string, string>> Instances => _instances;

    /// <summary>When set, every command is refused with this reason.</summary>
    public string? RefuseEverythingBecause { get; set; }

    /// <summary>When true, the worker refuses to probe — as an older build with no probe would.</summary>
    public bool RefuseProbe { get; set; }

    /// <summary>Points, by index, the worker should report it could not evaluate.</summary>
    public HashSet<int> FailPoints { get; } = [];

    /// <summary>When true, the eval reply claims a different node count than it was asked for.</summary>
    public bool LieAboutShape { get; set; }

    public FakeDeviceWorker() => _requests = new RequestStream(this);

    public Stream Requests          => _requests;
    public Stream Replies           => _replies;
    public string Origin            => "fake device worker";
    public bool   IsAlive           => true;
    public string RecentErrorOutput => string.Empty;
    public bool   Disposed          { get; private set; }

    public void Dispose() => Disposed = true;

    // ── the device the fake worker models ─────────────────────────────────────

    /// <summary>
    /// A deliberately asymmetric model: node k's current depends on its own voltage with a
    /// k-dependent conductance, and node 0 additionally depends on node 1.
    ///
    /// <para>The asymmetry is the point. With a symmetric model, a decoder that transposed the
    /// Jacobian, or that read the charge block as the current block, would still pass — so the one
    /// cross term (<c>G[0,1] = 3</c>, with <c>G[1,0] = 0</c>) is what actually pins the layout down.</para>
    /// </summary>
    public static void EvaluateModel(ReadOnlySpan<double> v, Span<double> i, Span<double> q,
                                     Span<double> g, Span<double> c)
    {
        int n = v.Length;
        g.Clear();
        c.Clear();

        for (int k = 0; k < n; k++)
        {
            i[k] = (k + 1) * v[k];
            q[k] = 0.5 * v[k];
            g[k * n + k] = k + 1;
            c[k * n + k] = 0.5;
        }

        if (n < 2) return;
        i[0]         += 3.0 * v[1];
        g[0 * n + 1]  = 3.0;
    }

    // ── command handling ──────────────────────────────────────────────────────

    private void Handle(string json, ReadOnlyMemory<double> payload)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string cmd = root.TryGetProperty("cmd", out var c) ? c.GetString() ?? "" : "";
        Commands.Add(cmd);

        if (RefuseEverythingBecause is { } reason) { Refuse(reason); return; }

        switch (cmd)
        {
            case "describe": Describe();                 break;
            case "create":   Create(root);               break;
            case "probe":    Probe(root);                break;
            case "eval":     Eval(root, payload);        break;
            case "destroy":  Destroy(root);              break;
            case "shutdown": Reply("""{"ok":true}""");   break;
            default:         Refuse("unknown cmd");      break;
        }
    }

    private void Describe()
    {
        string nodes = string.Join(",", Enumerable.Range(0, Nodes).Select(n =>
            $$"""{"index":{{n}},"external":{{(n < ExternalPins ? "true" : "false")}},"slavedTo":{{(n == 5 ? "4" : "null")}}}"""));

        Reply($$"""
        {"ok":true,"protocol":1,"types":[
          {"typeId":"{{NonlinearType}}","displayName":"{{NonlinearType}}",
           "externalPinCount":{{ExternalPins}},"internalNodeCount":{{InternalNodes}},
           "nonlinear":true,"linear":true,
           "params":[{"name":"Scale","kind":"double"},{"name":"Fingers","kind":"int"},
                     {"name":"File","kind":"filePath"},{"name":"Note","kind":"whatIsThis"},
                     {"name":"TYPE","kind":"int"}],
           "nodes":[{{nodes}}]},
          {"typeId":"{{LinearOnlyType}}","displayName":"{{LinearOnlyType}}",
           "externalPinCount":4,"internalNodeCount":0,"nonlinear":false,"linear":true,
           "params":[{"name":"L1","kind":"double"}],"nodes":[]}
        ]}
        """);
    }

    private void Create(JsonElement root)
    {
        string typeId = root.TryGetProperty("typeId", out var t) ? t.GetString() ?? "" : "";
        if (typeId == LinearOnlyType) { Refuse("family has no nonlinear analyze entry point"); return; }
        if (typeId != NonlinearType)  { Refuse("unknown typeId"); return; }

        var given = new Dictionary<string, string>();
        if (root.TryGetProperty("params", out var pars) && pars.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in pars.EnumerateObject())
                given[p.Name] = p.Value.ValueKind == JsonValueKind.String
                    ? p.Value.GetString() ?? ""
                    : p.Value.GetRawText();
        }

        int handle = _nextHandle++;
        _instances[handle] = given;

        Reply($$"""
        {"ok":true,"handle":{{handle}},"pinCount":{{Nodes}},"externalPinCount":{{ExternalPins}},
         "internalNodeCount":{{InternalNodes}},"probeEval":true,
         "delayPairs":[{"i":5,"j":4,"tau":7.15e-12}],
         "alias":[-1,-1,-1,-1,-1,4]}
        """);
    }

    private void Probe(JsonElement root)
    {
        if (RefuseProbe) { Refuse("unknown cmd"); return; }
        if (!TryHandle(root, out _)) return;

        // Pin 3 is reported thermal — an external pin with no conductive coupling.
        string nodes = string.Join(",", Enumerable.Range(0, Nodes).Select(n =>
            $$"""
            {"index":{{n}},"external":{{(n < ExternalPins ? "true" : "false")}},
             "degenerate":{{(n == 5 ? "true" : "false")}},"conductivelyCoupled":{{(n == 3 ? "false" : "true")}},
             "slavedTo":{{(n == 5 ? 4 : -1)}},"quantityKind":"{{(n == 3 ? "thermal" : "electrical")}}"}
            """));

        Reply($$"""{"ok":true,"nodes":[{{nodes}}]}""");
    }

    private void Eval(JsonElement root, ReadOnlyMemory<double> payload)
    {
        if (!TryHandle(root, out _)) return;

        int count = root.TryGetProperty("count", out var c) && c.TryGetInt32(out int ci) ? ci : 0;
        int n     = Nodes;

        if (payload.Length < count * n) { Refuse("short eval payload"); return; }

        int perPoint = 2 * n + 2 * n * n;
        var outBuf   = new double[count + count * perPoint];

        for (int k = 0; k < count; k++)
        {
            outBuf[k] = FailPoints.Contains(k) ? 0.0 : 1.0;

            int at = count + k * perPoint;
            var span = outBuf.AsSpan();
            EvaluateModel(
                payload.Span.Slice(k * n, n),
                span.Slice(at, n),
                span.Slice(at + n, n),
                span.Slice(at + 2 * n, n * n),
                span.Slice(at + 2 * n + n * n, n * n));
        }

        Reply($$"""
        {"ok":true,"count":{{count}},"pinCount":{{(LieAboutShape ? n + 1 : n)}},
         "layout":"status[count],then per vector I[n],Q[n],G[n*n],C[n*n]"}
        """, outBuf);
    }

    private void Destroy(JsonElement root)
    {
        if (!TryHandle(root, out int handle)) return;
        _instances.Remove(handle);
        Reply("""{"ok":true}""");
    }

    private bool TryHandle(JsonElement root, out int handle)
    {
        handle = root.TryGetProperty("handle", out var h) && h.TryGetInt32(out int i) ? i : -1;
        if (_instances.ContainsKey(handle)) return true;
        Refuse("bad handle");
        return false;
    }

    // ── framing ───────────────────────────────────────────────────────────────

    private void Refuse(string reason) => Reply($$"""{"ok":false,"error":"{{reason}}"}""");

    private void Reply(string json, double[]? payload = null)
        => _replies.Enqueue(new DeviceWorkerFrame(json, payload ?? []));

    /// <summary>
    /// Consumes whole frames from what the host has written so far. The host flushes at the end of
    /// every frame, so a reply is always ready by the time the host reads.
    /// </summary>
    private void Drain(MemoryStream pending)
    {
        byte[] all = pending.ToArray();
        int at = 0;

        while (all.Length - at >= 8)
        {
            uint jsonLen = BitConverter.ToUInt32(all, at);
            uint binLen  = BitConverter.ToUInt32(all, at + 4);
            long total   = 8L + jsonLen + binLen;
            if (all.Length - at < total) break;

            string json = Encoding.UTF8.GetString(all, at + 8, (int)jsonLen);
            var payload = new double[binLen / sizeof(double)];
            Buffer.BlockCopy(all, at + 8 + (int)jsonLen, payload, 0, (int)binLen);

            at += (int)total;
            Handle(json, payload);
        }

        pending.SetLength(0);
        if (at < all.Length) pending.Write(all, at, all.Length - at);
    }

    private sealed class RequestStream(FakeDeviceWorker owner) : Stream
    {
        private readonly MemoryStream _pending = new();

        public override void Write(ReadOnlySpan<byte> buffer) => _pending.Write(buffer);
        public override void Write(byte[] buffer, int offset, int count) => _pending.Write(buffer, offset, count);
        public override void Flush() => owner.Drain(_pending);

        public override bool CanRead  => false;
        public override bool CanSeek  => false;
        public override bool CanWrite => true;
        public override long Length   => _pending.Length;
        public override long Position { get => _pending.Position; set => throw new NotSupportedException(); }
        public override int  Read(byte[] b, int o, int c) => throw new NotSupportedException();
        public override long Seek(long o, SeekOrigin s)   => throw new NotSupportedException();
        public override void SetLength(long v)            => throw new NotSupportedException();
    }

    private sealed class ReplyStream : Stream
    {
        private readonly Queue<byte> _bytes = new();

        public void Enqueue(DeviceWorkerFrame frame)
        {
            var ms = new MemoryStream();
            DeviceWorkerProtocol.WriteFrame(ms, frame);
            foreach (byte b in ms.ToArray()) _bytes.Enqueue(b);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = 0;
            while (n < count && _bytes.Count > 0) buffer[offset + n++] = _bytes.Dequeue();
            return n;
        }

        public override bool CanRead  => true;
        public override bool CanSeek  => false;
        public override bool CanWrite => false;
        public override long Length   => _bytes.Count;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v)          => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }
}

using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;

// ─────────────────────────────────────────────────────────────────────────────
//  A reference device worker.
//
//  circuitRF evaluates externally-supplied device models by running them in a
//  separate process and talking to it over standard input and output. This is a
//  complete, working example of that process: it serves one synthetic transistor,
//  in about as little code as the protocol allows.
//
//  It exists for three reasons:
//    - as a template for anyone writing a real worker,
//    - as an executable definition of the wire format, and
//    - so circuitRF's own process plumbing is tested against a real process
//      rather than only against in-memory streams.
//
//  Run it with no arguments and it serves the device below. It is not a stub:
//  the currents, charges and derivatives it returns are a consistent model, so a
//  circuit built on it solves to an operating point that can be checked by hand.
//
//  --fail-with <message> is the OTHER thing a real worker must do: a model that
//  cannot start — a library it cannot load, a data file that is not there — has
//  no reply to send, so it says why on its error stream and exits. That stream
//  is the only description the user ever gets of such a failure, which is why it
//  is part of the reference rather than left to each worker to remember.
// ─────────────────────────────────────────────────────────────────────────────

if (args is ["--fail-with", var reason, ..])
{
    Console.Error.WriteLine(reason);
    Console.Error.Flush();
    return 1;
}

var worker = new Worker();
return worker.Run(Console.OpenStandardInput(), Console.OpenStandardOutput());

/// <summary>
/// A square-law field-effect transistor with gate leakage and a gate-source capacitance.
///
/// <para><b>Node order is [gate, drain, source]</b>, and <b>current is positive flowing INTO the
/// device</b> at each node — the convention circuitRF stamps directly. The three currents sum to
/// zero at every bias, which is what makes the device physical rather than merely plausible.</para>
/// </summary>
file sealed class Device
{
    public const int NodeCount = 3;

    public double Beta   = 0.02;    // A/V²   transconductance parameter
    public double Vth    = 0.7;     // V      threshold
    public double Lambda = 0.01;    // 1/V    channel-length modulation
    public double Cgs    = 1e-12;   // F      gate-source capacitance
    public double Ggs    = 1e-9;    // S      gate leakage

    /// <summary>
    /// Evaluates at one node-voltage vector, filling currents, charges and both derivative
    /// matrices. Matrices are row-major with <c>G[i,j] = ∂I[i]/∂V[j]</c>.
    /// </summary>
    public void Evaluate(ReadOnlySpan<double> v, Span<double> i, Span<double> q,
                         Span<double> g, Span<double> c)
    {
        const int n = NodeCount;
        i.Clear(); q.Clear(); g.Clear(); c.Clear();

        double vgs = v[0] - v[2];
        double vds = v[1] - v[2];

        // Channel current. Below threshold the device is off, and — importantly for a solver — it
        // is off smoothly: the current and both derivatives go to zero together at Vgs = Vth.
        double id = 0, didVgs = 0, didVds = 0;
        double over = vgs - Vth;

        if (over > 0 && vds > 0)
        {
            double mod = 1 + Lambda * vds;
            id     = Beta * over * over * mod;
            didVgs = 2 * Beta * over * mod;
            didVds = Beta * over * over * Lambda;
        }

        double igs = Ggs * vgs;     // gate leakage, so the gate row is not identically zero

        i[0] = igs;
        i[1] = id;
        i[2] = -(id + igs);

        // ∂I[gate]
        g[0 * n + 0] = Ggs;
        g[0 * n + 2] = -Ggs;

        // ∂I[drain]
        g[1 * n + 0] = didVgs;
        g[1 * n + 1] = didVds;
        g[1 * n + 2] = -(didVgs + didVds);

        // ∂I[source] — the negated sum of the others, which is Kirchhoff's law as a matrix identity
        for (int col = 0; col < n; col++)
            g[2 * n + col] = -(g[0 * n + col] + g[1 * n + col]);

        double qgs = Cgs * vgs;
        q[0] =  qgs;
        q[2] = -qgs;

        c[0 * n + 0] =  Cgs;
        c[0 * n + 2] = -Cgs;
        c[2 * n + 0] = -Cgs;
        c[2 * n + 2] =  Cgs;
    }

    /// <summary>Applies a named parameter. Unknown names are refused rather than ignored.</summary>
    public bool TrySet(string name, double value)
    {
        switch (name)
        {
            case "Beta":   Beta   = value; return true;
            case "Vth":    Vth    = value; return true;
            case "Lambda": Lambda = value; return true;
            case "Cgs":    Cgs    = value; return true;
            case "Ggs":    Ggs    = value; return true;
            default:       return false;
        }
    }
}

file sealed class Worker
{
    private const string TypeId = "example_fet_v1";

    private readonly Dictionary<int, Device> _instances = [];
    private int _nextHandle = 1;

    public int Run(Stream input, Stream output)
    {
        while (true)
        {
            if (!TryReadFrame(input, out string json, out double[] payload)) return 0;

            string reply;
            double[]? bulk = null;

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                string cmd = root.TryGetProperty("cmd", out var c) ? c.GetString() ?? "" : "";

                switch (cmd)
                {
                    case "describe": reply = Describe();                        break;
                    case "create":   reply = Create(root);                      break;
                    case "probe":    reply = Probe(root);                       break;
                    case "eval":     reply = Eval(root, payload, out bulk);     break;
                    case "destroy":  reply = Destroy(root);                     break;

                    case "shutdown":
                        WriteFrame(output, """{"ok":true}""", null);
                        return 0;

                    default: reply = Error($"unknown cmd '{cmd}'"); break;
                }
            }
            catch (Exception ex)
            {
                // A worker that dies takes the whole simulation with it. Any failure inside one
                // command is reported as a refusal of that command instead.
                reply = Error(ex.Message);
                bulk  = null;
            }

            WriteFrame(output, reply, bulk);
        }
    }

    // ── commands ──────────────────────────────────────────────────────────────

    private static string Describe() => $$"""
        {"ok":true,"protocol":1,"types":[
          {"typeId":"{{TypeId}}","displayName":"Example square-law FET",
           "externalPinCount":3,"internalNodeCount":0,"nonlinear":true,"linear":false,
           "params":[{"name":"Beta","kind":"double"},{"name":"Vth","kind":"double"},
                     {"name":"Lambda","kind":"double"},{"name":"Cgs","kind":"double"},
                     {"name":"Ggs","kind":"double"}],
           "nodes":[{"index":0,"external":true,"slavedTo":null},
                    {"index":1,"external":true,"slavedTo":null},
                    {"index":2,"external":true,"slavedTo":null}]}
        ]}
        """;

    private string Create(JsonElement root)
    {
        if ((root.TryGetProperty("typeId", out var t) ? t.GetString() : null) != TypeId)
            return Error("unknown typeId");

        var device = new Device();

        if (root.TryGetProperty("params", out var pars) && pars.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in pars.EnumerateObject())
            {
                double value = p.Value.ValueKind == JsonValueKind.Number
                    ? p.Value.GetDouble()
                    : double.TryParse(p.Value.GetString(), NumberStyles.Float,
                                      CultureInfo.InvariantCulture, out double parsed) ? parsed : double.NaN;

                if (double.IsNaN(value))          return Error($"parameter '{p.Name}' is not a number");
                if (!device.TrySet(p.Name, value)) return Error($"unknown parameter '{p.Name}'");
            }
        }

        int handle = _nextHandle++;
        _instances[handle] = device;

        return $$"""
            {"ok":true,"handle":{{handle}},"pinCount":{{Device.NodeCount}},"externalPinCount":3,
             "internalNodeCount":0,"probeEval":true,"delayPairs":[],"alias":[-1,-1,-1]}
            """;
    }

    /// <summary>
    /// Reports what each node is, measured rather than declared — the same structural test a real
    /// worker uses, and the reason circuitRF needs no per-model knowledge to wire a device up.
    ///
    /// <para>A node whose current row is identically zero is not a free unknown. An external pin
    /// with no <i>symmetric</i> coupling carries something other than current: symmetry is the
    /// discriminator, because a large but one-sided row is a dependent source, not a conductance.</para>
    /// </summary>
    private string Probe(JsonElement root)
    {
        if (!TryGetInstance(root, out _, out Device? device)) return Error("bad handle");

        const int n = Device.NodeCount;
        Span<double> v = stackalloc double[n];
        Span<double> i0 = stackalloc double[n], q = stackalloc double[n];
        Span<double> g = stackalloc double[n * n], c = stackalloc double[n * n];

        device!.Evaluate(v, i0, q, g, c);

        var sb = new StringBuilder("""{"ok":true,"nodes":[""");

        for (int node = 0; node < n; node++)
        {
            bool degenerate = true, coupled = false;

            for (int col = 0; col < n; col++)
                if (Math.Abs(g[node * n + col]) > 1e-12) degenerate = false;

            for (int other = 0; other < n; other++)
            {
                if (other == node) continue;
                double a = g[node * n + other], b = g[other * n + node];
                if (Math.Abs(a) > 1e-12 && Math.Abs(a - b) <= 1e-3 * Math.Abs(a)) coupled = true;
            }

            if (node > 0) sb.Append(',');
            sb.Append($$"""
                {"index":{{node}},"external":true,"degenerate":{{Json(degenerate)}},
                 "conductivelyCoupled":{{Json(coupled)}},"slavedTo":-1,
                 "quantityKind":"{{(coupled ? "electrical" : "thermal")}}"}
                """);
        }

        return sb.Append("]}").ToString();
    }

    private string Eval(JsonElement root, double[] payload, out double[]? bulk)
    {
        bulk = null;

        if (!TryGetInstance(root, out _, out Device? device)) return Error("bad handle");

        int count = root.TryGetProperty("count", out var c) && c.TryGetInt32(out int ci) ? ci : 0;
        if (count <= 0) return Error("count must be positive");

        const int n = Device.NodeCount;
        if (payload.Length < count * n) return Error("short eval payload");

        int perPoint = 2 * n + 2 * n * n;
        var result = new double[count + count * perPoint];

        for (int k = 0; k < count; k++)
        {
            int at = count + k * perPoint;
            var span = result.AsSpan();

            device!.Evaluate(
                payload.AsSpan(k * n, n),
                span.Slice(at, n),
                span.Slice(at + n, n),
                span.Slice(at + 2 * n, n * n),
                span.Slice(at + 2 * n + n * n, n * n));

            // A point is reported as failed rather than returned as a number that is not one.
            bool finite = true;
            for (int j = at; j < at + perPoint; j++)
                if (!double.IsFinite(result[j])) { finite = false; break; }

            result[k] = finite ? 1.0 : 0.0;
        }

        bulk = result;
        return $$"""
            {"ok":true,"count":{{count}},"pinCount":{{n}},
             "layout":"status[count],then per vector I[n],Q[n],G[n*n],C[n*n]"}
            """;
    }

    private string Destroy(JsonElement root)
    {
        if (!TryGetInstance(root, out int handle, out _)) return Error("bad handle");
        _instances.Remove(handle);
        return """{"ok":true}""";
    }

    private bool TryGetInstance(JsonElement root, out int handle, out Device? device)
    {
        handle = root.TryGetProperty("handle", out var h) && h.TryGetInt32(out int i) ? i : -1;
        return _instances.TryGetValue(handle, out device);
    }

    private static string Json(bool value) => value ? "true" : "false";

    private static string Error(string message)
        => $$"""{"ok":false,"error":{{JsonSerializer.Serialize(message)}}}""";

    // ── framing ───────────────────────────────────────────────────────────────
    //
    //  [ uint32 jsonLen ][ uint32 binLen ][ jsonLen bytes UTF-8 ][ binLen bytes of float64 ]
    //
    //  Little-endian throughout. binLen is a BYTE count. Control stays JSON so a frame is readable
    //  in a hex dump; bulk numbers ride as raw doubles so a large batch costs no parsing.

    private static bool TryReadFrame(Stream input, out string json, out double[] payload)
    {
        json    = "";
        payload = [];

        Span<byte> header = stackalloc byte[8];
        if (!TryReadExactly(input, header)) return false;      // the host closed the pipe: exit

        uint jsonLen = BinaryPrimitives.ReadUInt32LittleEndian(header[..4]);
        uint binLen  = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);

        var jsonBytes = new byte[jsonLen];
        if (!TryReadExactly(input, jsonBytes)) return false;

        var values = new double[binLen / sizeof(double)];
        if (values.Length > 0 &&
            !TryReadExactly(input, System.Runtime.InteropServices.MemoryMarshal.AsBytes(values.AsSpan())))
            return false;

        json    = Encoding.UTF8.GetString(jsonBytes);
        payload = values;
        return true;
    }

    private static void WriteFrame(Stream output, string json, double[]? payload)
    {
        byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
        int binBytes = (payload?.Length ?? 0) * sizeof(double);

        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(header[..4], (uint)jsonBytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)binBytes);

        output.Write(header);
        output.Write(jsonBytes);

        if (binBytes > 0)
            output.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(payload.AsSpan()));

        // Without this the host waits for a reply that is sitting in this process's buffer, and the
        // two deadlock. It is the single most common way a working worker appears to hang.
        output.Flush();
    }

    /// <summary>
    /// Fills the buffer completely, or reports that the stream ended. A short read is normal on a
    /// pipe and must be looped — treating one as the end yields frames that decode as nonsense only
    /// under load.
    /// </summary>
    private static bool TryReadExactly(Stream input, Span<byte> buffer)
    {
        int done = 0;
        while (done < buffer.Length)
        {
            int read = input.Read(buffer[done..]);
            if (read <= 0) return false;
            done += read;
        }
        return true;
    }
}

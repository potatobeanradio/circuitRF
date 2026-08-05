using System.Globalization;
using System.Text.Json;

namespace CircuitRF.Core.Devices.External;

// ─────────────────────────────────────────────────────────────────────────────
//  A provider backed by an out-of-process device worker.
//
//  Nothing here names a supplier, a library, a model family or a part. Every
//  device type, every parameter name, every pin count and every node role is
//  learned at runtime from the worker's own replies. What circuitRF supplies is
//  the seam: a worker executable path is configuration, and everything past it
//  is data.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Exposes the device types of one worker process to circuitRF.
///
/// <para><b>Why a process rather than a library.</b> A compiled device model calls back into the
/// process that loaded it for services that process must export — which a managed host cannot do —
/// and one process can hold exactly one build of one library. Both constraints are properties of
/// the arrangement, not of any particular model, and both dissolve once the model lives in its own
/// process. circuitRF loads nothing and links against nothing.</para>
/// </summary>
public sealed class DeviceWorkerProvider : IExternalDeviceProvider, IDisposable
{
    private readonly DeviceWorkerChannel _channel;
    private readonly Lock                _describeGate = new();

    private IReadOnlyList<ExternalDeviceDescriptor>? _described;
    private bool _disposed;

    public DeviceWorkerProvider(string name, IDeviceWorkerTransport transport)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(transport);

        Name     = name;
        _channel = new DeviceWorkerChannel(transport);
    }

    /// <summary>
    /// Start a worker executable and expose its device types under <paramref name="name"/>.
    /// </summary>
    /// <param name="name">Registration name. Opaque to circuitRF; rendered, never interpreted.</param>
    /// <param name="executablePath">Path to the worker binary — runtime configuration.</param>
    /// <param name="arguments">Whatever the worker needs to know, typically which library to load.</param>
    /// <summary>Marks a key as circuitRF's own plumbing rather than something the model declared.</summary>
    public const string ReservedPrefix = "__";

    /// <summary>
    /// Absolute device temperature in KELVIN, supplied alongside the parameters and lifted out of
    /// them at <c>create</c>. Kelvin because that is what the ABIs that take a temperature want;
    /// circuitRF's own parameters are Celsius throughout, and the conversion belongs at this
    /// boundary — the same rule the diode's factory already follows.
    /// </summary>
    public const string ReservedTemperatureKey = ReservedPrefix + "temperatureK";

    public static DeviceWorkerProvider Launch(
        string               name,
        string               executablePath,
        IEnumerable<string>? arguments = null)
        => new(name, ProcessDeviceWorkerTransport.Start(executablePath, arguments));

    public string Name { get; }

    /// <summary>
    /// What this worker last wrote to its error stream. Surfaced so a HEADLESS run can show it: the
    /// worker's log carries facts available nowhere else — how it classified each node, whether its
    /// data files opened — and until now those were only ever attached to an exception, so a run
    /// that merely failed to converge threw them away.
    /// </summary>
    public string RecentErrorOutput => _channel.RecentErrorOutput;

    /// <summary>
    /// Every device type this worker exposes.
    ///
    /// <para>Cached after the first call. A worker's set of types is fixed once it has loaded its
    /// library, and re-describing on every query would put a round trip in front of routine UI.</para>
    /// </summary>
    public IReadOnlyList<ExternalDeviceDescriptor> Describe()
    {
        lock (_describeGate)
        {
            if (_described is not null) return _described;

            using var reply = _channel.Send(w => w.WriteString("cmd", "describe"));

            var types = new List<ExternalDeviceDescriptor>();
            if (reply.Root.TryGetProperty("types", out var array) &&
                array.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in array.EnumerateArray())
                    types.Add(ReadDescriptor(element));
            }

            return _described = types;
        }
    }

    public IExternalDeviceInstance Create(string typeId, IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeId);
        ArgumentNullException.ThrowIfNull(parameters);
        ObjectDisposedException.ThrowIf(_disposed, this);

        ExternalDeviceDescriptor descriptor =
            Describe().FirstOrDefault(d => d.TypeId == typeId)
            ?? throw new ExternalDeviceException(
                   $"'{Name}' has no device type named '{typeId}'. It offers: " +
                   $"{string.Join(", ", Describe().Select(d => d.TypeId))}.");

        int handle;
        int nodeCount;
        IReadOnlyList<DeviceWorkerDelayPair> delayPairs;
        IReadOnlyList<(int Node, int To)>    collapsed;

        using (var reply = _channel.Send(w =>
        {
            w.WriteString("cmd", "create");
            w.WriteString("typeId", typeId);

            // A device's TEMPERATURE is not a model parameter and must not be written as one. Some
            // ABIs take it as a required argument to instance setup, and a model that happens to
            // declare a parameter of the same name would then receive it twice with the two
            // meanings competing. So it rides as its own top-level field, keyed on a reserved name
            // that no descriptor can collide with — the same "__ is circuitRF plumbing" rule the
            // component factory already applies when deciding what to forward.
            if (parameters.TryGetValue(ReservedTemperatureKey, out var tk) &&
                double.TryParse(tk, NumberStyles.Float, CultureInfo.InvariantCulture, out double kelvin) &&
                kelvin > 0.0)
                w.WriteNumber("temperatureK", kelvin);

            WriteParameters(w, descriptor, parameters);
        }))
        {
            handle     = ReadInt(reply.Root, "handle", -1);
            nodeCount  = ReadInt(reply.Root, "pinCount", descriptor.NodeCount);
            delayPairs = ReadDelayPairs(reply.Root);
            collapsed  = ReadCollapsedNodes(reply.Root);

            if (handle < 0)
                throw new ExternalDeviceException(
                    $"'{Name}' created '{typeId}' but did not say which instance it created.");
        }

        // Node collapsing is answered at CREATE, not at describe, because which nodes collapse
        // depends on the parameters this instance was given — a series resistance of zero degenerates
        // a node that a nonzero one leaves free. It is folded in BEFORE the probe below so a probing
        // worker refines the collapsed shape rather than erasing it.
        descriptor = ApplyCollapsedNodes(descriptor, collapsed);

        // Node roles are measured, not declared: which pins are thermal and which nodes are not
        // free unknowns comes back from a structural probe of the instance that was just built.
        // A worker that cannot probe is not a failure — the declared descriptor still stands.
        ExternalDeviceDescriptor measured = descriptor;
        try { measured = ProbeNodes(handle, descriptor); }
        catch (ExternalDeviceException) { /* declared shape stands */ }

        return new DeviceWorkerInstance(_channel, handle, measured, nodeCount, delayPairs);
    }

    // ── describe ──────────────────────────────────────────────────────────────

    private static ExternalDeviceDescriptor ReadDescriptor(JsonElement element)
    {
        string typeId = ReadString(element, "typeId", "");
        int externals = ReadInt(element, "externalPinCount", 0);
        int internals = ReadInt(element, "internalNodeCount", 0);

        var parameters = new List<ExternalParamDescriptor>();
        if (element.TryGetProperty("params", out var pars) && pars.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in pars.EnumerateArray())
            {
                string pname = ReadString(p, "name", "");
                if (pname.Length == 0) continue;
                parameters.Add(new ExternalParamDescriptor(pname, ParseParamKind(ReadString(p, "kind", ""))));
            }
        }

        var nodes = new List<ExternalNodeDescriptor>();
        if (element.TryGetProperty("nodes", out var ns) && ns.ValueKind == JsonValueKind.Array)
        {
            foreach (var n in ns.EnumerateArray())
            {
                int index = ReadInt(n, "index", nodes.Count);
                nodes.Add(new ExternalNodeDescriptor(
                    index,
                    External: ReadBool(n, "external", index < externals),
                    SlavedTo: ReadSlavedTo(n),
                    CollapsedToGround: ReadBool(n, "collapsedToGround", false)));
            }
        }

        if (nodes.Count == 0)
        {
            for (int i = 0; i < externals + internals; i++)
                nodes.Add(new ExternalNodeDescriptor(i, External: i < externals));
        }

        return new ExternalDeviceDescriptor(
            typeId,
            ReadString(element, "displayName", typeId),
            externals,
            internals,
            parameters,
            nodes,
            SupportsNonlinear: ReadBool(element, "nonlinear", true),
            SupportsLinear:    ReadBool(element, "linear",    false));
    }

    /// <summary>
    /// Maps a worker's parameter kind onto circuitRF's. An unrecognised kind becomes a string —
    /// the value then reaches the worker exactly as the user typed it, which is the one choice that
    /// cannot corrupt a value circuitRF does not understand.
    /// </summary>
    private static ExternalParamKind ParseParamKind(string kind) => kind switch
    {
        "double"   => ExternalParamKind.Double,
        "int"      => ExternalParamKind.Int,
        "filePath" => ExternalParamKind.FilePath,
        _          => ExternalParamKind.String,
    };

    // ── create ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes the parameter object, converting each value according to the kind the worker declared.
    ///
    /// <para><b>An unknown parameter name is rejected here.</b> The worker matches parameters by
    /// keyword and ignores anything it does not recognise, so a typo would otherwise be silently
    /// dropped and show up as a device that quietly uses a default. Failing at creation names the
    /// parameter and lists what the type actually declares.</para>
    /// </summary>
    private static void WriteParameters(
        Utf8JsonWriter                      writer,
        ExternalDeviceDescriptor            descriptor,
        IReadOnlyDictionary<string, string> parameters)
    {
        writer.WriteStartObject("params");

        foreach (var (name, text) in parameters)
        {
            // Reserved plumbing keys are consumed by the caller above, never offered to the model —
            // and so are never checked against the descriptor, which of course does not declare them.
            if (name.StartsWith(ReservedPrefix, StringComparison.Ordinal)) continue;

            ExternalParamDescriptor declared =
                descriptor.Parameters.FirstOrDefault(p => p.Name == name)
                ?? throw new ExternalDeviceException(
                       $"'{descriptor.TypeId}' has no parameter named '{name}'. It declares: " +
                       $"{string.Join(", ", descriptor.Parameters.Select(p => p.Name))}.");

            if (string.IsNullOrWhiteSpace(text)) continue;   // unset: the model keeps its own default

            switch (declared.Kind)
            {
                case ExternalParamKind.Double:
                case ExternalParamKind.Int:
                    if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                        throw new ExternalDeviceException(
                            $"Parameter '{name}' of '{descriptor.TypeId}' expects a number, but is set to '{text}'.");
                    writer.WriteNumber(name, value);
                    break;

                default:
                    writer.WriteString(name, text);
                    break;
            }
        }

        writer.WriteEndObject();
    }

    private static IReadOnlyList<DeviceWorkerDelayPair> ReadDelayPairs(JsonElement root)
    {
        if (!root.TryGetProperty("delayPairs", out var array) || array.ValueKind != JsonValueKind.Array)
            return [];

        var pairs = new List<DeviceWorkerDelayPair>();
        foreach (var p in array.EnumerateArray())
        {
            pairs.Add(new DeviceWorkerDelayPair(
                ReadInt(p, "i", -1),
                ReadInt(p, "j", -1),
                ReadDouble(p, "tau", 0.0)));
        }
        return pairs;
    }

    // ── collapsed nodes ───────────────────────────────────────────────────────

    /// <summary>
    /// Reads a worker's report of which nodes this instance collapsed, as
    /// <c>[{ "node": n, "to": m }]</c>, where a <c>to</c> below zero means the ground reference
    /// rather than another node.
    ///
    /// <para>An ABI that <i>declares</i> its collapsible pairs can say which node a degenerate one
    /// follows — which is the part a structural probe cannot recover, and the part that has to be
    /// right: a collapsed node left as a free unknown is an all-zero row and column, a solve that
    /// simply does not converge with nothing anywhere saying why.</para>
    /// </summary>
    private static IReadOnlyList<(int Node, int To)> ReadCollapsedNodes(JsonElement root)
    {
        if (!root.TryGetProperty("collapsed", out var array) || array.ValueKind != JsonValueKind.Array)
            return [];

        var collapsed = new List<(int, int)>();
        foreach (var c in array.EnumerateArray())
        {
            int node = ReadInt(c, "node", -1);
            if (node >= 0) collapsed.Add((node, ReadInt(c, "to", -1)));
        }
        return collapsed;
    }

    /// <summary>
    /// Folds a create-time collapse report onto the type's declared nodes.
    ///
    /// <para>The result is a NEW record — the cached type descriptor is shared by every instance of
    /// the type and collapsing is per-instance, so writing into it would let one instance's zero
    /// series resistance degenerate a node on every other instance.</para>
    /// </summary>
    private static ExternalDeviceDescriptor ApplyCollapsedNodes(
        ExternalDeviceDescriptor              descriptor,
        IReadOnlyList<(int Node, int To)>     collapsed)
    {
        if (collapsed.Count == 0) return descriptor;

        var byNode = new Dictionary<int, int>();
        foreach (var (node, to) in collapsed) byNode[node] = to;

        var nodes = descriptor.Nodes.Select(n =>
            byNode.TryGetValue(n.Index, out int to)
                ? (to < 0
                       ? n with { CollapsedToGround = true, SlavedTo = null }
                       : n with { SlavedTo = to, CollapsedToGround = false })
                : n).ToList();

        return descriptor with { Nodes = nodes };
    }

    // ── probe ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Refines a declared descriptor with what the worker measured on a live instance: which
    /// external pins carry a non-electrical quantity, and which nodes are not free unknowns.
    /// </summary>
    private ExternalDeviceDescriptor ProbeNodes(int handle, ExternalDeviceDescriptor declared)
    {
        using var reply = _channel.Send(w =>
        {
            w.WriteString("cmd", "probe");
            w.WriteNumber("handle", handle);
        });

        if (!reply.Root.TryGetProperty("nodes", out var array) || array.ValueKind != JsonValueKind.Array)
            return declared;

        var nodes = new List<ExternalNodeDescriptor>();
        foreach (var n in array.EnumerateArray())
        {
            int index = ReadInt(n, "index", nodes.Count);
            ExternalNodeDescriptor? original = declared.Nodes.FirstOrDefault(d => d.Index == index);

            nodes.Add(new ExternalNodeDescriptor(
                index,
                External:     ReadBool(n, "external", original?.External ?? index < declared.ExternalPinCount),
                QuantityKind: ReadString(n, "quantityKind", "") == "thermal"
                                  ? NodeQuantityKind.Thermal
                                  : NodeQuantityKind.Electrical,
                Label:        original?.Label ?? "",
                SlavedTo:     ReadSlavedTo(n) ?? original?.SlavedTo,
                // A probe refines; it does not repeal. A worker whose collapse report came back at
                // create and whose probe says nothing about node roles must keep the collapse.
                CollapsedToGround: ReadBool(n, "collapsedToGround", original?.CollapsedToGround ?? false)));
        }

        return nodes.Count == 0 ? declared : declared with { Nodes = nodes };
    }

    // ── JSON helpers ──────────────────────────────────────────────────────────
    //
    //  A worker's reply is read defensively throughout: a missing or wrong-typed field falls back
    //  rather than throwing, because a worker that grows a field circuitRF has not heard of must
    //  keep working, and one that omits a field circuitRF wants is better reported by whatever
    //  fails downstream than by an unreadable parse error here.

    private static string ReadString(JsonElement element, string name, string fallback)
        => element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? fallback
            : fallback;

    private static int ReadInt(JsonElement element, string name, int fallback)
        => element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
        && v.TryGetInt32(out int i)
            ? i
            : fallback;

    private static double ReadDouble(JsonElement element, string name, double fallback)
        => element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
        && v.TryGetDouble(out double d)
            ? d
            : fallback;

    private static bool ReadBool(JsonElement element, string name, bool fallback)
        => element.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : fallback;

    /// <summary>
    /// Reads a "which node does this one follow" field. A worker may say null or -1 for "none";
    /// both mean an ordinary free node, and neither is an error.
    /// </summary>
    private static int? ReadSlavedTo(JsonElement element)
    {
        if (!element.TryGetProperty("slavedTo", out var v)) return null;
        if (v.ValueKind != JsonValueKind.Number) return null;
        return v.TryGetInt32(out int i) && i >= 0 ? i : null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _channel.Dispose();
    }
}

/// <summary>
/// A pair of nodes whose voltage difference the model evaluates at a delayed time, and the delay.
///
/// <para>At DC this is just the instantaneous difference and needs no special handling. In harmonic
/// balance it is where each harmonic picks up its <c>exp(-jωτ)</c> rotation — the reason the pairs
/// are surfaced now rather than when an engine needs them.</para>
/// </summary>
public readonly record struct DeviceWorkerDelayPair(int FromNode, int ToNode, double Tau);

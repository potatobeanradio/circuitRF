using System.Text.Json;
using CircuitRF.Diagnostics;

namespace CircuitRF.Core.Devices.External;

/// <summary>
/// One live device instance held by a worker process.
///
/// <para><b>Sign convention: none applied.</b> A worker reports current positive flowing INTO the
/// device at each node, which is already circuitRF's convention
/// (<see cref="ExternalDeviceEvaluation"/>), so currents are passed through untouched. This is
/// checked against behaviour rather than assumed from documentation: at a drain bias the drain
/// node's current comes back positive while the device sinks that current, and the thermal node's
/// current comes back negative with its magnitude equal to the electrical power the device
/// dissipates — power leaving the device. Both signs agree, and they agree in the direction that
/// needs no flip. A second flip applied "to be safe" would invert every operating point while still
/// converging, which is why this is stated rather than left to a reader to infer.</para>
/// </summary>
public sealed class DeviceWorkerInstance : IExternalDeviceInstance
{
    private readonly DeviceWorkerChannel _channel;
    private readonly int                 _handle;
    private readonly int                 _nodeCount;

    private bool _disposed;

    internal DeviceWorkerInstance(
        DeviceWorkerChannel                 channel,
        int                                 handle,
        ExternalDeviceDescriptor            descriptor,
        int                                 nodeCount,
        IReadOnlyList<DeviceWorkerDelayPair> delayPairs)
    {
        _channel   = channel;
        _handle    = handle;
        _nodeCount = nodeCount;
        Descriptor = descriptor;
        DelayPairs = delayPairs;
    }

    public ExternalDeviceDescriptor Descriptor { get; }

    /// <summary>
    /// Node pairs this instance evaluates at a delayed time. Empty for a device with no delay.
    /// Surfaced for a future harmonic-balance engine, which applies the per-harmonic rotation.
    /// </summary>
    public IReadOnlyList<DeviceWorkerDelayPair> DelayPairs { get; }

    /// <summary>Node count this device occupies — external pins followed by internal nodes.</summary>
    public int NodeCount => _nodeCount;

    public ExternalDeviceEvaluation Evaluate(IReadOnlyList<double> nodeVoltages)
        => EvaluateBatch([nodeVoltages])[0];

    /// <summary>
    /// Evaluate every supplied voltage vector in a single round trip.
    ///
    /// <para><b>This is the whole reason the transport exists in this shape.</b> Measured against
    /// this worker, one evaluation per round trip costs ~100 µs while a batch of 2000 costs ~4 µs
    /// each — a factor of 24. Harmonic balance evaluates every device once per sample per Newton
    /// iteration, so the per-call version would make the transport, not the model, the simulator.</para>
    /// </summary>
    public IReadOnlyList<ExternalDeviceEvaluation> EvaluateBatch(IReadOnlyList<IReadOnlyList<double>> nodeVoltages)
    {
        ArgumentNullException.ThrowIfNull(nodeVoltages);
        ObjectDisposedException.ThrowIf(_disposed, this);

        int count = nodeVoltages.Count;
        if (count == 0) return [];

        int n = _nodeCount;
        var request = BuildRequest(nodeVoltages, count, n);

        using var reply = _channel.Send(w =>
        {
            w.WriteString("cmd", "eval");
            w.WriteNumber("handle", _handle);
            w.WriteNumber("count", count);
        }, request);

        return Decode(reply, count, n, out _);
    }

    /// <summary>
    /// The model's operating-point variables at the bias it last evaluated — a read, never an
    /// evaluation. See <see cref="IExternalDeviceInstance.ReadOperatingPoint"/> for why the
    /// distinction is the whole design.
    ///
    /// <para><b>Only asked when the descriptor declared some.</b> circuitRF's other two workers host
    /// different ABIs and will never implement this command; an unknown command is an error to
    /// them, so probing blind would turn a working simulation into a refusal on two providers that
    /// were never in scope. Null here means "nothing to show", never a failure a caller must
    /// handle.</para>
    /// </summary>
    public IReadOnlyDictionary<string, double>? ReadOperatingPoint()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Descriptor.OpVars.Count == 0) return null;

        using var reply = _channel.Send(w =>
        {
            w.WriteString("cmd", "opvars");
            w.WriteNumber("handle", _handle);
        });

        string[] names  = ReadNames(reply, "names");
        var      values = reply.Payload.Span;

        if (values.Length != names.Length)
            throw new ExternalDeviceException(
                $"'{Descriptor.TypeId}' named {names.Length} operating-point variables and sent " +
                $"{values.Length} values.");

        var map = new Dictionary<string, double>(names.Length, StringComparer.Ordinal);
        for (int i = 0; i < names.Length; i++) map[names[i]] = values[i];
        return map;
    }

    /// <summary>
    /// Evaluate the whole batch and capture the operating-point variables at every point, in one
    /// round trip — the only shape harmonic balance can use, since after a batch the instance holds
    /// nothing but the last sample.
    /// </summary>
    public ExternalOperatingPoint? EvaluateOperatingPoint(IReadOnlyList<IReadOnlyList<double>> nodeVoltages)
    {
        ArgumentNullException.ThrowIfNull(nodeVoltages);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (Descriptor.OpVars.Count == 0) return null;

        int count = nodeVoltages.Count;
        if (count == 0) return new ExternalOperatingPoint([], []);

        int n = _nodeCount;
        var request = BuildRequest(nodeVoltages, count, n);

        using var reply = _channel.Send(w =>
        {
            w.WriteString("cmd", "eval");
            w.WriteNumber("handle", _handle);
            w.WriteNumber("count", count);
            w.WriteBoolean("opvars", true);
        }, request);

        Decode(reply, count, n, out var opVars);
        return opVars;
    }

    /// <summary>
    /// The noise sources' densities at one bias and every frequency, in one round trip (the worker README's Noise
    /// section): <c>[k, s]</c>, single-sided A²/Hz, unscaled. Like <see cref="ReadOperatingPoint"/>, asked only when the
    /// descriptor declared some, so a worker that does not know the command never receives it.
    /// </summary>
    public double[,]? EvaluateNoise(IReadOnlyList<double> nodeVoltages, IReadOnlyList<double> frequenciesHz)
    {
        ArgumentNullException.ThrowIfNull(nodeVoltages);
        ArgumentNullException.ThrowIfNull(frequenciesHz);
        ObjectDisposedException.ThrowIf(_disposed, this);

        int sources = Descriptor.NoiseSources.Count, count = frequenciesHz.Count, n = _nodeCount;
        if (sources == 0) return null;

        var request = new double[n + count];
        BuildRequest([nodeVoltages], 1, n).CopyTo(request, 0);
        for (int k = 0; k < count; k++) request[n + k] = frequenciesHz[k];

        using var reply = _channel.Send(w =>
        {
            w.WriteString("cmd", "noise");
            w.WriteNumber("handle", _handle);
            w.WriteNumber("freqCount", count);
        }, request);

        var data = reply.Payload.Span;
        int said = reply.Root.TryGetProperty("sourceCount", out var sc) && sc.ValueKind == JsonValueKind.Number
                   && sc.TryGetInt32(out int v) ? v : -1;
        if (said != sources || data.Length != sources * count)
            throw new ExternalDeviceException(NoiseReplyMismatch(Descriptor.TypeId, sources, count, said, data.Length).Render());

        var psd = new double[count, sources];
        for (int k = 0; k < count; k++)
            for (int s = 0; s < sources; s++)
                psd[k, s] = data[k * sources + s];
        return psd;
    }

    /// <summary>
    /// The noise sources' densities at EVERY state of <paramref name="nodeVoltages"/> and every frequency, in one round trip
    /// (<c>noise_batch</c>, the worker README's Noise section): <c>[t, k, s]</c>, unscaled. Harmonic-balance noise asks this
    /// once per instance per operating point with the whole waveform (brief NZ-5) — one request per time sample would make
    /// the transport the simulator, which is the rule <c>eval</c>'s batching already states.
    /// </summary>
    public double[,,]? EvaluateNoiseBatch(IReadOnlyList<IReadOnlyList<double>> nodeVoltages, IReadOnlyList<double> frequenciesHz)
    {
        ArgumentNullException.ThrowIfNull(nodeVoltages);
        ArgumentNullException.ThrowIfNull(frequenciesHz);
        ObjectDisposedException.ThrowIf(_disposed, this);

        int sources = Descriptor.NoiseSources.Count, count = nodeVoltages.Count, freqs = frequenciesHz.Count, n = _nodeCount;
        if (sources == 0) return null;
        if (count == 0 || freqs == 0) return new double[count, freqs, sources];

        var states = BuildRequest(nodeVoltages, count, n);
        var request = new double[states.Length + freqs];
        states.CopyTo(request, 0);
        for (int k = 0; k < freqs; k++) request[states.Length + k] = frequenciesHz[k];

        using var reply = _channel.Send(w =>
        {
            w.WriteString("cmd", "noise_batch");
            w.WriteNumber("handle", _handle);
            w.WriteNumber("count", count);
            w.WriteNumber("freqCount", freqs);
        }, request);
        NoiseBatchRequests++;

        var data = reply.Payload.Span;
        int said = reply.Root.TryGetProperty("sourceCount", out var sc) && sc.ValueKind == JsonValueKind.Number
                   && sc.TryGetInt32(out int v) ? v : -1;
        if (said != sources || data.Length != (long)count * freqs * sources)
            throw new ExternalDeviceException(NoiseReplyMismatch(Descriptor.TypeId, sources, count * freqs, said, data.Length).Render());

        var psd = new double[count, freqs, sources];
        for (int t = 0; t < count; t++)
            for (int k = 0; k < freqs; k++)
                for (int s = 0; s < sources; s++)
                    psd[t, k, s] = data[(t * freqs + k) * sources + s];
        return psd;
    }

    /// <summary>How many <c>noise_batch</c> requests this instance has sent: what brief NZ-5's batching gate counts.</summary>
    public int NoiseBatchRequests { get; private set; }

    /// <summary>A <c>noise</c> reply that does not match what was declared and asked: a protocol desync, never a result.</summary>
    private static Diagnostic NoiseReplyMismatch(string type, int sources, int freqs, int said, int values) => Diagnostic.Create(
        "external.noise.reply-mismatch", DiagnosticSeverity.Error,
        "'{type}' declares {sources} noise sources and was asked for {freqs} frequencies, but its noise reply named " +
        "{said} sources and carried {values} values.",
        ("type", type), ("sources", sources), ("freqs", freqs), ("said", said), ("values", values));

    private double[] BuildRequest(IReadOnlyList<IReadOnlyList<double>> nodeVoltages, int count, int n)
    {
        var request = new double[(long)count * n <= int.MaxValue ? count * n : throw TooLarge(count, n)];

        for (int k = 0; k < count; k++)
        {
            IReadOnlyList<double> v = nodeVoltages[k];
            if (v.Count != n)
                throw new ExternalDeviceException(
                    $"'{Descriptor.TypeId}' has {n} nodes, but evaluation point {k} supplied {v.Count} voltages.");

            for (int i = 0; i < n; i++) request[k * n + i] = v[i];
        }
        return request;
    }

    private static string[] ReadNames(DeviceWorkerReply reply, string property)
    {
        if (!reply.Root.TryGetProperty(property, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return [];

        var names = new List<string>();
        foreach (var e in arr.EnumerateArray())
            if (e.ValueKind == JsonValueKind.String) names.Add(e.GetString() ?? "");
        return [.. names];
    }

    /// <summary>
    /// Unpacks the reply: a status value per point, then per point <c>I[n]</c>, <c>Q[n]</c>,
    /// <c>G[n×n]</c>, <c>C[n×n]</c>, all row-major.
    /// </summary>
    /// <summary>
    /// Appends whatever the worker wrote to its own error stream.
    ///
    /// <para><b>Why this is needed here and not only where a worker dies.</b> A failed evaluation
    /// point arrives as a perfectly normal reply, so it never passes through the channel's own
    /// failure path — which is the only place that used to attach a worker's log. The distinctions
    /// that matter are invisible on the wire and stated only in that log: the worker writes
    /// "eval: SIGSEGV caught" when the model crashed, and a model that cannot read a data file
    /// usually says so there too. Without it the report can do no better than list the
    /// possibilities, which is what sent one real diagnosis chasing the bias.</para>
    /// </summary>
    private string WithWorkerOutput(string message)
    {
        string errors = _channel.ErrorOutputAfterRefusal;

        if (string.IsNullOrWhiteSpace(errors)) return message;

        string withOutput = WorkerOutputDiagnosis.LedByModelsOwnWords(message, errors)
                          + Environment.NewLine + "Worker output:" + Environment.NewLine
                          + WorkerOutputDiagnosis.CollapseRepeats(errors);

        // Same reason as DeviceWorkerChannel.Failed: a recognised failure gets its explanation
        // appended, so the actionable sentence is the last thing read rather than something buried
        // inside the worker's own text.
        string? diagnosis = WorkerOutputDiagnosis.Explain(errors);
        return diagnosis is null
            ? withOutput
            : withOutput + Environment.NewLine + Environment.NewLine + diagnosis;
    }

    private IReadOnlyList<ExternalDeviceEvaluation> Decode(
        DeviceWorkerReply reply, int count, int n, out ExternalOperatingPoint? opVars)
    {
        // The op-var block, when one was asked for, rides at the END of each point's own block, and
        // the worker names what it carries rather than sending one slot per declared quantity — a
        // string-valued op-var has nowhere to land, and one the model will not read out is an
        // omission rather than a zero. So the stride comes from the reply, not from the descriptor.
        string[] opNames = ReadNames(reply, "opvarNames");

        int perPoint = 2 * n + 2 * n * n + opNames.Length;
        int expected = count + count * perPoint;

        // The worker states the shape it sent. Checking it here means a mismatched worker build is
        // reported as a mismatch, rather than decoding into a plausible-looking wrong answer.
        int statedCount = reply.Root.TryGetProperty("count", out var c) && c.TryGetInt32(out int ci) ? ci : count;
        int statedNodes = reply.Root.TryGetProperty("pinCount", out var p) && p.TryGetInt32(out int pi) ? pi : n;

        if (statedCount != count || statedNodes != n)
            throw new ExternalDeviceException(
                $"Asked '{Descriptor.TypeId}' for {count} points of {n} nodes and was answered with " +
                $"{statedCount} points of {statedNodes} nodes.");

        ReadOnlySpan<double> data = reply.Payload.Span;
        if (data.Length != expected)
            throw new ExternalDeviceException(
                $"'{Descriptor.TypeId}' returned {data.Length} values for {count} evaluation points; " +
                $"{expected} were expected.");

        // A point the worker could not evaluate carries no usable numbers. Returning those values
        // would put a NaN into the matrix and surface as an unexplained non-convergence far from
        // here, so it is raised now.
        var failed = new List<int>();
        for (int k = 0; k < count; k++)
            if (data[k] == 0.0) failed.Add(k);

        if (failed.Count > 0)
            throw new ExternalDeviceException(WithWorkerOutput(
                $"'{Descriptor.TypeId}' could not be evaluated at {failed.Count} of {count} operating " +
                $"points (first: point {failed[0]}). A worker marks a point failed the same way " +
                $"whether the model refused it, crashed inside it, or returned a value that was not " +
                $"finite, so it is one of those three. The usual causes are a bias outside the range " +
                $"the model is valid over, and a file the model needs and could not open."));

        var results = new ExternalDeviceEvaluation[count];
        var opRows  = opNames.Length > 0 ? new double[count][] : null;

        for (int k = 0; k < count; k++)
        {
            int at = count + k * perPoint;

            var current = new double[n];
            var charge  = new double[n];
            var g       = new double[n, n];
            var cap     = new double[n, n];

            data.Slice(at, n).CopyTo(current);   at += n;
            data.Slice(at, n).CopyTo(charge);    at += n;

            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    g[i, j] = data[at + i * n + j];
            at += n * n;

            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    cap[i, j] = data[at + i * n + j];
            at += n * n;

            if (opRows is not null)
            {
                var row = new double[opNames.Length];
                data.Slice(at, opNames.Length).CopyTo(row);
                opRows[k] = row;
            }

            results[k] = new ExternalDeviceEvaluation(current, charge, g, cap);
        }

        opVars = opRows is null ? null : new ExternalOperatingPoint(opNames, opRows);
        return results;
    }

    private static ExternalDeviceException TooLarge(int count, int n)
        => new($"An evaluation batch of {count} points across {n} nodes is too large to send in one request.");

    /// <summary>
    /// Releases the instance in the worker. Failure is swallowed: disposal runs during teardown and
    /// on the way out of an error, and a worker that has already died must not turn tidying up into
    /// a second, more confusing exception.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            using var _ = _channel.Send(w =>
            {
                w.WriteString("cmd", "destroy");
                w.WriteNumber("handle", _handle);
            });
        }
        catch (ExternalDeviceException) { /* the worker is already gone; nothing to release */ }
        catch (ObjectDisposedException) { /* the channel closed first */ }
    }
}

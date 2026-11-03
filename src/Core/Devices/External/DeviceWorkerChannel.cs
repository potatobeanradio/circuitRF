using System.Text.Json;

namespace CircuitRF.Core.Devices.External;

/// <summary>
/// One reply from a worker: the control-plane object it sent, and any bulk numerics alongside it.
/// </summary>
public sealed class DeviceWorkerReply(JsonDocument document, ReadOnlyMemory<double> payload) : IDisposable
{
    public JsonElement             Root    => document.RootElement;
    public ReadOnlyMemory<double>  Payload => payload;

    public void Dispose() => document.Dispose();
}

/// <summary>
/// Request/reply over a <see cref="IDeviceWorkerTransport"/>: writes a command frame, reads the
/// reply, and turns a refusal into an exception.
///
/// <para><b>One request at a time, enforced.</b> Two threads writing frames into the same pipe
/// interleave them, and the worker then reads a header out of the middle of somebody else's JSON.
/// The result is a desynchronised stream that presents as corrupt numerics much later, so the lock
/// here is a correctness requirement rather than a convenience.</para>
/// </summary>
public sealed class DeviceWorkerChannel(IDeviceWorkerTransport transport) : IDisposable
{
    private readonly Lock _gate = new();
    private bool _disposed;

    /// <summary>Where this worker is, for diagnostics.</summary>
    public string Origin => transport.Origin;

    /// <summary>
    /// Whether the worker process is still running. A worker that has exited cannot be revived —
    /// its pipes are gone — so a caller holding one has to replace it rather than retry on it.
    /// </summary>
    public bool IsAlive => !_disposed && transport.IsAlive;

    /// <summary>
    /// What the worker last wrote to its own error stream. Surfaced so a caller decoding a REPLY can
    /// attach it too: a worker reports a point it could not evaluate in-band and perfectly normally,
    /// and its log is then the only thing that says which of the several possible reasons it was.
    /// </summary>
    public string RecentErrorOutput => transport.RecentErrorOutput;

    /// <summary>
    /// <see cref="RecentErrorOutput"/>, read just after the worker refused a point in-band: waits briefly for its error
    /// stream to go quiet, so the line explaining the refusal is not still in flight (<see cref="IDeviceWorkerTransport.ErrorOutputAfterRefusal"/>).
    /// </summary>
    public string ErrorOutputAfterRefusal => transport.ErrorOutputAfterRefusal();

    /// <summary>
    /// Send one command and read its reply.
    /// </summary>
    /// <param name="writeCommand">
    /// Writes the command object. Called inside the lock, so it must not itself use this channel.
    /// </param>
    /// <param name="payload">Bulk numerics the command carries, in the order the command defines.</param>
    /// <exception cref="ExternalDeviceException">
    /// The worker refused the command, stopped answering, or sent something unreadable. Callers do
    /// not distinguish these: all three mean the evaluation cannot proceed, and all three want the
    /// worker's own error output attached.
    /// </exception>
    public DeviceWorkerReply Send(Action<Utf8JsonWriter> writeCommand, ReadOnlyMemory<double> payload = default)
    {
        ArgumentNullException.ThrowIfNull(writeCommand);
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writeCommand(writer);
                writer.WriteEndObject();
            }

            string json = System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);

            DeviceWorkerFrame reply;
            try
            {
                DeviceWorkerProtocol.WriteFrame(transport.Requests, new DeviceWorkerFrame(json, payload));
                reply = DeviceWorkerProtocol.ReadFrame(transport.Replies);
            }
            catch (ExternalDeviceException ex)
            {
                throw Failed(ex.Message, ex);
            }
            catch (Exception ex)
            {
                throw Failed($"the connection failed ({ex.Message})", ex);
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(reply.Json);
            }
            catch (JsonException ex)
            {
                throw Failed($"its reply was not readable ({ex.Message})", ex);
            }

            // A worker reports refusal in-band — an unknown type, a bad handle, a parameter it will
            // not take. That is a normal answer on the wire and a hard failure to the caller.
            //
            // AN `error` MEMBER IS ENOUGH ON ITS OWN, and requiring `ok: false` beside it was a real
            // defect rather than strictness. A worker circuitRF ships writes refusals as plain
            // `{"error": "…"}` with no `ok` at all, so every one of its refusals was read as a normal
            // reply carrying no data — and the caller then reported whatever it made of the absence.
            // Measured: "create: too many live instances" surfaced as *"created 'MDLA_VA' but did
            // not say which instance it created"*, which names neither the cause nor anything the
            // reader could act on. There is no reply where an `error` member means anything else.
            bool refused = document.RootElement.TryGetProperty("ok", out var ok)
                        && ok.ValueKind == JsonValueKind.False;

            bool named = document.RootElement.TryGetProperty("error", out var e)
                      && e.ValueKind == JsonValueKind.String;

            if (refused || named)
            {
                string detail = named ? e.GetString() ?? "no reason given" : "no reason given";
                document.Dispose();
                throw Failed(detail);
            }

            return new DeviceWorkerReply(document, reply.Payload);
        }
    }

    /// <summary>
    /// Builds the one exception shape this channel throws. A dead worker's own error output is
    /// attached here because it is usually the only description of what actually went wrong, and a
    /// user will not think to go looking for it.
    /// </summary>
    private ExternalDeviceException Failed(string detail, Exception? inner = null)
    {
        var message = new System.Text.StringBuilder();
        // A colon, not a space: every detail below is a clause with its own subject ("the connection
        // failed", "its reply was not readable"), and a worker's own in-band error text is free-form.
        // Joining with a space produced "The device worker (x) the connection failed".
        message.Append("The device worker (").Append(transport.Origin).Append("): ").Append(detail);
        if (!message[^1].Equals('.')) message.Append('.');

        // A non-null inner means the failure came from the WIRE — the write, the read, or a reply
        // that would not parse — rather than from a refusal the worker sent in-band. On the wire
        // case the worker is gone or going and its own last line is the whole report, so waiting for
        // it is right; on an in-band refusal the worker is alive and must not be blocked on.
        // Read BEFORE asking whether it is alive: the wait is what lets a dying worker be reaped.
        //
        // An in-band refusal waits for the error stream to go briefly quiet (ErrorOutputAfterRefusal, bounded at
        // 100 ms): the worker flushes a model's own sentence before it writes the refusal, but the two pipes are read
        // by different threads, and reading without the wait could compose "model setup reported errors" with the
        // sentence explaining it still in flight (brief vendorlib-15 R-vl15-2).
        string errors = inner is null
            ? transport.ErrorOutputAfterRefusal()
            : transport.ErrorOutputAfterConnectionFailure();

        bool exited = !transport.IsAlive;
        int? exitCode = exited ? transport.ExitCode : null;
        if (exited) message.Append(" The worker process has exited").Append(exitCode is { } c ? $" with code {c}." : ".");

        if (!string.IsNullOrWhiteSpace(errors))
            message.Append(Environment.NewLine).Append("Worker output:").Append(Environment.NewLine)
                   .Append(WorkerOutputDiagnosis.CollapseRepeats(errors));

        // BEFORE the raw output would be read, not after: the one failure this recognises buries its
        // actual cause in the third clause of 400 characters of dyld search-path noise, and a reader
        // who has already given up on that text will not come back for a footnote.
        string? diagnosis = WorkerOutputDiagnosis.Explain(errors);
        if (diagnosis is not null)
            message.Append(Environment.NewLine).Append(Environment.NewLine).Append(diagnosis);

        // A model that refused to run said why in its own words, and those lead (brief vendorlib-15 R-vl15-2).
        string text = WorkerOutputDiagnosis.LedByModelsOwnWords(message.ToString(), errors);
        if (exited)
        {
            string last = errors.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "";
            return new DeviceWorkerExitedException(text, transport.Origin, exitCode, last, inner);
        }
        return new ExternalDeviceException(text, inner);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        transport.Dispose();
    }
}

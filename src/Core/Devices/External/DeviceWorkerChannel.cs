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
    /// What the worker last wrote to its own error stream. Surfaced so a caller decoding a REPLY can
    /// attach it too: a worker reports a point it could not evaluate in-band and perfectly normally,
    /// and its log is then the only thing that says which of the several possible reasons it was.
    /// </summary>
    public string RecentErrorOutput => transport.RecentErrorOutput;

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
            if (document.RootElement.TryGetProperty("ok", out var ok) &&
                ok.ValueKind == JsonValueKind.False)
            {
                string detail = document.RootElement.TryGetProperty("error", out var e)
                             && e.ValueKind == JsonValueKind.String
                    ? e.GetString() ?? "no reason given"
                    : "no reason given";
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

        if (!transport.IsAlive) message.Append(" The worker process has exited.");

        string errors = transport.RecentErrorOutput;
        if (!string.IsNullOrWhiteSpace(errors))
            message.Append(Environment.NewLine).Append("Worker output:").Append(Environment.NewLine).Append(errors);

        return new ExternalDeviceException(message.ToString(), inner);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        transport.Dispose();
    }
}

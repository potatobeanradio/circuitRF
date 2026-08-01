using System.Buffers.Binary;

namespace CircuitRF.Core.Devices.External;

// ─────────────────────────────────────────────────────────────────────────────
//  Wire format for talking to an out-of-process device-model worker.
//
//  A worker owns a compiled device model and evaluates it on request. It runs as
//  a separate process for two reasons that are properties of the arrangement, not
//  of any particular model: the model library expects the LOADING process to
//  export the services it calls back into — which a managed host cannot do — and
//  one process can hold exactly one build of one library, so several builds means
//  several processes.
//
//  Nothing here names a supplier, a library, or a part. This is a frame codec.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// One protocol frame: a JSON control plane plus an optional block of raw doubles.
///
/// <para><b>Why the split.</b> Control stays JSON so a frame is readable in a hex dump when
/// something goes wrong; bulk numerics ride as raw little-endian doubles so a batch of thousands of
/// evaluation points costs no parsing. Measured, that difference is ~24× — at one round trip per
/// evaluation the transport, not the model, becomes the simulator.</para>
/// </summary>
/// <param name="Json">Control-plane text. Never empty — every frame states what it is.</param>
/// <param name="Payload">Bulk numerics, in the order the command defines. May be empty.</param>
public readonly record struct DeviceWorkerFrame(string Json, ReadOnlyMemory<double> Payload)
{
    public DeviceWorkerFrame(string json) : this(json, ReadOnlyMemory<double>.Empty) { }
}

/// <summary>
/// Reads and writes <see cref="DeviceWorkerFrame"/>s.
///
/// <para>Frame layout, little-endian throughout:</para>
/// <code>
/// [ uint32 jsonLen ][ uint32 binLen ][ jsonLen bytes UTF-8 ][ binLen bytes of doubles ]
/// </code>
///
/// <para><c>binLen</c> is a BYTE count, not a value count — it is what the reader must consume, and
/// a length in elements would be ambiguous the moment anything but a double is ever carried.</para>
/// </summary>
public static class DeviceWorkerProtocol
{
    /// <summary>
    /// Refuses a frame claiming more than this. A worker is a local subprocess, so a length beyond
    /// any plausible batch means a desynchronised stream — and reading it would mean allocating
    /// gigabytes on a corrupt number rather than reporting the desync.
    /// </summary>
    public const int MaxFrameBytes = 512 * 1024 * 1024;

    private const int HeaderBytes = 8;

    // ── Write ─────────────────────────────────────────────────────────────────

    public static void WriteFrame(Stream stream, in DeviceWorkerFrame frame)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (string.IsNullOrEmpty(frame.Json))
            throw new ArgumentException("Every frame must carry a control-plane object.", nameof(frame));

        byte[] json = System.Text.Encoding.UTF8.GetBytes(frame.Json);
        int binBytes = frame.Payload.Length * sizeof(double);

        Span<byte> header = stackalloc byte[HeaderBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(header[..4], (uint)json.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)binBytes);

        stream.Write(header);
        stream.Write(json);

        if (binBytes > 0)
            stream.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(frame.Payload.Span));

        stream.Flush();
    }

    // ── Read ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads one frame. Throws <see cref="ExternalDeviceException"/> — never a raw I/O or overflow
    /// error — because every failure here means the same thing to a user: the worker stopped
    /// answering, or is no longer in step.
    /// </summary>
    public static DeviceWorkerFrame ReadFrame(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        Span<byte> header = stackalloc byte[HeaderBytes];
        if (!TryReadExactly(stream, header))
            throw new ExternalDeviceException(
                "The device worker closed its output before sending a reply. It may have exited; " +
                "check its own error output for the reason.");

        uint jsonLen = BinaryPrimitives.ReadUInt32LittleEndian(header[..4]);
        uint binLen  = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);

        if (jsonLen == 0)
            throw new ExternalDeviceException(
                "The device worker sent a frame with no control-plane object; the stream is not in step.");

        if (jsonLen > MaxFrameBytes || binLen > MaxFrameBytes)
            throw new ExternalDeviceException(
                $"The device worker announced an implausible frame ({jsonLen} + {binLen} bytes). " +
                "The stream is out of step — this is a desynchronisation, not a large result.");

        if (binLen % sizeof(double) != 0)
            throw new ExternalDeviceException(
                $"The device worker announced {binLen} payload bytes, which is not a whole number " +
                "of values. The stream is out of step.");

        var jsonBytes = new byte[jsonLen];
        if (!TryReadExactly(stream, jsonBytes))
            throw new ExternalDeviceException("The device worker's reply was truncated mid-frame.");

        var payload = new double[binLen / sizeof(double)];
        if (payload.Length > 0 &&
            !TryReadExactly(stream, System.Runtime.InteropServices.MemoryMarshal.AsBytes(payload.AsSpan())))
            throw new ExternalDeviceException("The device worker's reply was truncated mid-payload.");

        return new DeviceWorkerFrame(System.Text.Encoding.UTF8.GetString(jsonBytes), payload);
    }

    /// <summary>
    /// Fills <paramref name="buffer"/> completely, or reports that the stream ended first. A partial
    /// read is normal on a pipe and must be looped, not treated as the end — getting this wrong
    /// produces frames that decode as garbage only under load.
    /// </summary>
    private static bool TryReadExactly(Stream stream, Span<byte> buffer)
    {
        int done = 0;
        while (done < buffer.Length)
        {
            int n = stream.Read(buffer[done..]);
            if (n <= 0) return false;
            done += n;
        }
        return true;
    }
}

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace CircuitRF.Ui.Layout.PCells.Wire;

/// <summary>Anything wrong with a PCell wire exchange — a malformed frame, a desynchronised stream,
/// a message this build cannot read. One type, because every one of them means the same thing to a
/// user: the generator is not talking to us properly.</summary>
public sealed class PCellWireException : Exception
{
    public PCellWireException(string message) : base(message) { }
    public PCellWireException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// One frame: a JSON control plane plus an optional block of raw int64 coordinates.
/// </summary>
/// <param name="Json">Control-plane text. Never empty — every frame states what it is.</param>
/// <param name="Payload">Bulk geometry in database units, in the order the message defines.</param>
public readonly record struct PCellWireFrame(string Json, ReadOnlyMemory<long> Payload)
{
    public PCellWireFrame(string json) : this(json, ReadOnlyMemory<long>.Empty) { }
}

/// <summary>
/// Reads and writes <see cref="PCellWireFrame"/>s. See <c>docs/design/pcell-wire-schema.md</c> §2.
///
/// <para>Frame layout, little-endian throughout:</para>
/// <code>
/// [ uint32 jsonLen ][ uint32 binLen ][ jsonLen bytes UTF-8 ][ binLen bytes of int64 ]
/// </code>
///
/// <para><b>Why this is a sibling of <c>DeviceWorkerProtocol</c> rather than the same class.</b> The
/// layout is identical and so is the reasoning behind it — JSON so a frame is readable in a hex dump,
/// raw binary so bulk data costs no parsing. What differs is the payload's ELEMENT TYPE: geometry is
/// int64 database units, a device evaluation is doubles. That changes the length arithmetic, the
/// "not a whole number of values" check and every desync message, and the device path is live against
/// a production kit — sharing eighty lines is not worth destabilising it. The two protocols are
/// independent and neither may assume anything about the other.</para>
///
/// <para><b>The one property both must keep, and it is the one that is easy to get wrong:</b> a
/// partial read on a pipe is normal and must be LOOPED, never treated as end-of-stream. Getting it
/// wrong produces frames that decode as garbage only under load. It is tested on each side
/// separately, because a shared test would not prove it of both implementations.</para>
/// </summary>
public static class PCellWireProtocol
{
    /// <summary>
    /// Refuses a frame claiming more than this. A generator is a local subprocess, so a length beyond
    /// any plausible cell means a desynchronised stream — and believing it means allocating gigabytes
    /// on a corrupt number instead of reporting the desync.
    /// </summary>
    public const int MaxFrameBytes = 256 * 1024 * 1024;

    private const int HeaderBytes = 8;

    // ── Write ─────────────────────────────────────────────────────────────────

    public static void WriteFrame(Stream stream, in PCellWireFrame frame)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (string.IsNullOrEmpty(frame.Json))
            throw new ArgumentException("Every frame must carry a control-plane object.", nameof(frame));

        byte[] json = Encoding.UTF8.GetBytes(frame.Json);
        long binBytes = (long)frame.Payload.Length * sizeof(long);

        if (json.Length > MaxFrameBytes || binBytes > MaxFrameBytes)
            throw new PCellWireException(
                $"Refusing to send a frame of {json.Length} + {binBytes} bytes — beyond any plausible cell.");

        Span<byte> header = stackalloc byte[HeaderBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(header[..4], (uint)json.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)binBytes);

        stream.Write(header);
        stream.Write(json);

        if (binBytes > 0)
            stream.Write(MemoryMarshal.AsBytes(frame.Payload.Span));

        stream.Flush();
    }

    // ── Read ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads one frame. Throws <see cref="PCellWireException"/> — never a raw I/O or overflow error —
    /// because every failure here means the same thing: the generator stopped answering, or is no
    /// longer in step.
    /// </summary>
    public static PCellWireFrame ReadFrame(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        Span<byte> header = stackalloc byte[HeaderBytes];
        if (!TryReadExactly(stream, header))
            throw new PCellWireException(
                "The PCell generator closed its output before sending a reply. It may have exited; " +
                "check its own error output for the reason.");

        uint jsonLen = BinaryPrimitives.ReadUInt32LittleEndian(header[..4]);
        uint binLen  = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);

        if (jsonLen == 0)
            throw new PCellWireException(
                "The PCell generator sent a frame with no control-plane object; the stream is not in step.");

        if (jsonLen > MaxFrameBytes || binLen > MaxFrameBytes)
            throw new PCellWireException(
                $"The PCell generator announced an implausible frame ({jsonLen} + {binLen} bytes). " +
                "The stream is out of step — this is a desynchronisation, not a large cell.");

        if (binLen % sizeof(long) != 0)
            throw new PCellWireException(
                $"The PCell generator announced {binLen} payload bytes, which is not a whole number " +
                "of coordinates. The stream is out of step.");

        var jsonBytes = new byte[jsonLen];
        if (!TryReadExactly(stream, jsonBytes))
            throw new PCellWireException("The PCell generator's reply was truncated mid-frame.");

        var payload = new long[binLen / sizeof(long)];
        if (payload.Length > 0 && !TryReadExactly(stream, MemoryMarshal.AsBytes(payload.AsSpan())))
            throw new PCellWireException("The PCell generator's reply was truncated mid-payload.");

        return new PCellWireFrame(Encoding.UTF8.GetString(jsonBytes), payload);
    }

    /// <summary>
    /// Fills <paramref name="buffer"/> completely, or reports that the stream ended first. A partial
    /// read is normal on a pipe and must be looped, not treated as the end — see the type's own note
    /// on why this is the subtle one.
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

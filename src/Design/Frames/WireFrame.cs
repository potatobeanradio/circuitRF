using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace CircuitRF.Design.Frames;

/// <summary>Anything wrong with a framed exchange — a malformed frame, a desynchronised stream, a stream
/// that ended mid-frame. One type, because each means the same thing to a user: the program on the other end
/// is not talking to us properly.</summary>
public sealed class WireFrameException : Exception
{
    public WireFrameException(string message) : base(message) { }
    public WireFrameException(string message, Exception inner) : base(message, inner) { }

    /// <summary>True when the stream ended cleanly before a header — the other program closed its output, which
    /// usually means it exited. Every other failure is a stream that is no longer in step.</summary>
    public bool EndOfStream { get; init; }
}

/// <summary>
/// One frame: a JSON control plane plus an optional block of raw int64 values.
/// </summary>
/// <param name="Json">Control-plane text. Never empty — every frame states what it is.</param>
/// <param name="Payload">Bulk int64 data (geometry in database units), in the order the message defines.</param>
public readonly record struct WireFrame(string Json, ReadOnlyMemory<long> Payload)
{
    public WireFrame(string json) : this(json, ReadOnlyMemory<long>.Empty) { }
}

/// <summary>
/// Reads and writes <see cref="WireFrame"/>s — the one implementation of the frame a PCell generator and a
/// plugin both speak (<c>pcell-wire-schema.md</c> §2, <c>plugins.md</c> §6.1).
///
/// <para>Frame layout, little-endian throughout:</para>
/// <code>
/// [ uint32 jsonLen ][ uint32 binLen ][ jsonLen bytes UTF-8 ][ binLen bytes of int64 ]
/// </code>
///
/// <para><b>A neutral home, not a second codec.</b> The frame began as the PCell wire's and that is still its
/// first user (<c>PCellWireProtocol</c> calls this). Plugins needed the same bytes; copying eighty lines would
/// have made two frames that agree today. Messages name the other program through <c>peer</c>, so a PCell
/// failure still says "The PCell generator…" and a plugin's says "The plugin…".</para>
///
/// <para><b>The one property that is easy to get wrong:</b> a partial read on a pipe is normal and must be
/// LOOPED, never treated as end-of-stream. Getting it wrong produces frames that decode as garbage only under
/// load.</para>
/// </summary>
public static class WireFrames
{
    /// <summary>
    /// Refuses a frame claiming more than this. The other end is a local subprocess, so a length beyond any
    /// plausible message means a desynchronised stream — and believing it means allocating gigabytes on a
    /// corrupt number instead of reporting the desync.
    /// </summary>
    public const int MaxFrameBytes = 256 * 1024 * 1024;

    /// <summary>The fixed header: two little-endian uint32 lengths.</summary>
    public const int HeaderBytes = 8;

    // ── Write ─────────────────────────────────────────────────────────────────

    public static void Write(Stream stream, in WireFrame frame)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (string.IsNullOrEmpty(frame.Json))
            throw new ArgumentException("Every frame must carry a control-plane object.", nameof(frame));

        byte[] json = Encoding.UTF8.GetBytes(frame.Json);
        long binBytes = (long)frame.Payload.Length * sizeof(long);

        if (json.Length > MaxFrameBytes || binBytes > MaxFrameBytes)
            throw new WireFrameException(
                $"Refusing to send a frame of {json.Length} + {binBytes} bytes — beyond any plausible message.");

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
    /// Reads one frame. Throws <see cref="WireFrameException"/> — never a raw I/O or overflow error — because
    /// every failure here means the same thing: <paramref name="peer"/> stopped answering, or is no longer in
    /// step.
    /// </summary>
    /// <param name="peer">The other program as a sentence's subject: "The PCell generator", "The plugin".</param>
    public static WireFrame Read(Stream stream, string peer)
    {
        ArgumentNullException.ThrowIfNull(stream);

        Span<byte> header = stackalloc byte[HeaderBytes];
        if (!TryReadExactly(stream, header))
            throw new WireFrameException(
                $"{peer} closed its output before sending a reply. It may have exited; " +
                "check its own error output for the reason.") { EndOfStream = true };

        uint jsonLen = BinaryPrimitives.ReadUInt32LittleEndian(header[..4]);
        uint binLen  = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);

        if (jsonLen == 0)
            throw new WireFrameException(
                $"{peer} sent a frame with no control-plane object; the stream is not in step.");

        if (jsonLen > MaxFrameBytes || binLen > MaxFrameBytes)
            throw new WireFrameException(
                $"{peer} announced an implausible frame ({jsonLen} + {binLen} bytes). " +
                "The stream is out of step — this is a desynchronisation, not a large message.");

        if (binLen % sizeof(long) != 0)
            throw new WireFrameException(
                $"{peer} announced {binLen} payload bytes, which is not a whole number " +
                "of values. The stream is out of step.");

        var jsonBytes = new byte[jsonLen];
        if (!TryReadExactly(stream, jsonBytes))
            throw new WireFrameException($"{peer}'s reply was truncated mid-frame.");

        var payload = new long[binLen / sizeof(long)];
        if (payload.Length > 0 && !TryReadExactly(stream, MemoryMarshal.AsBytes(payload.AsSpan())))
            throw new WireFrameException($"{peer}'s reply was truncated mid-payload.");

        return new WireFrame(Encoding.UTF8.GetString(jsonBytes), payload);
    }

    /// <summary>
    /// Fills <paramref name="buffer"/> completely, or reports that the stream ended first. A partial read is
    /// normal on a pipe and must be looped, not treated as the end.
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

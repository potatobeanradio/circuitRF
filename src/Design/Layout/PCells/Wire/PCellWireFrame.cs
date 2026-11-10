namespace CircuitRF.Design.Layout.PCells.Wire;

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
/// <para><b>The codec itself lives in <see cref="CircuitRF.Design.Frames.WireFrames"/></b>, the neutral home it
/// moved to when plugins came to speak the same frame (brief-plg-1 §3). This class keeps the PCell names its
/// callers use, and its failures are still <see cref="PCellWireException"/>s worded for a PCell generator.</para>
///
/// <para><b>Why this is a sibling of <c>DeviceWorkerProtocol</c> rather than the same class.</b> The
/// layout is identical and so is the reasoning behind it — JSON so a frame is readable in a hex dump,
/// raw binary so bulk data costs no parsing. What differs is the payload's ELEMENT TYPE: geometry is
/// int64 database units, a device evaluation is doubles. That changes the length arithmetic, the
/// "not a whole number of values" check and every desync message, and the device path is live against
/// a production kit — sharing eighty lines is not worth destabilising it. The two protocols are
/// independent and neither may assume anything about the other.</para>
/// </summary>
public static class PCellWireProtocol
{
    /// <summary>Refuses a frame claiming more than this — see <see cref="CircuitRF.Design.Frames.WireFrames.MaxFrameBytes"/>.</summary>
    public const int MaxFrameBytes = CircuitRF.Design.Frames.WireFrames.MaxFrameBytes;

    private const string Peer = "The PCell generator";

    public static void WriteFrame(Stream stream, in PCellWireFrame frame)
    {
        try { CircuitRF.Design.Frames.WireFrames.Write(stream, new CircuitRF.Design.Frames.WireFrame(frame.Json, frame.Payload)); }
        catch (CircuitRF.Design.Frames.WireFrameException ex) { throw new PCellWireException(ex.Message, ex); }
    }

    /// <summary>
    /// Reads one frame. Throws <see cref="PCellWireException"/> — never a raw I/O or overflow error —
    /// because every failure here means the same thing: the generator stopped answering, or is no
    /// longer in step.
    /// </summary>
    public static PCellWireFrame ReadFrame(Stream stream)
    {
        try
        {
            var frame = CircuitRF.Design.Frames.WireFrames.Read(stream, Peer);
            return new PCellWireFrame(frame.Json, frame.Payload);
        }
        catch (CircuitRF.Design.Frames.WireFrameException ex) { throw new PCellWireException(ex.Message, ex); }
    }
}

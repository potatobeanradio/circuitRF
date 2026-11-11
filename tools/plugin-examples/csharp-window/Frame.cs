// The circuitRF plugin frame, written to be copied into another program unchanged. BCL only.
//
//     [ uint32 jsonLen ][ uint32 binLen ][ jsonLen bytes of UTF-8 JSON ][ binLen bytes of int64 ]
//
// Both lengths and every int64 are LITTLE-ENDIAN, whatever the machine. binLen counts BYTES, so it is always a multiple
// of 8. One frame carries one message: the JSON object, and the layout coordinates it indexes (empty for most messages).

using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace LineSectionPlugin;

/// <summary>A malformed frame, or a stream that ended inside one.</summary>
public sealed class FrameException(string message) : Exception(message);

/// <summary>One message: its JSON text and the int64 values its spans index.</summary>
public readonly record struct Frame(string Json, long[] Payload)
{
    public Frame(string json) : this(json, []) { }
}

public static class Frames
{
    /// <summary>Larger than any real message. A bigger announced length means the stream is out of step, and believing
    /// it would mean allocating gigabytes on a corrupt number.</summary>
    public const int MaxFrameBytes = 256 * 1024 * 1024;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Writes one frame and flushes: circuitRF is waiting for it, so a buffered frame is a deadlock.</summary>
    public static void Write(Stream stream, in Frame frame)
    {
        byte[] json = Utf8.GetBytes(frame.Json);
        if (json.Length == 0) throw new FrameException("Every frame carries a JSON object.");
        long binBytes = (long)frame.Payload.Length * 8;
        if (json.Length > MaxFrameBytes || binBytes > MaxFrameBytes) throw new FrameException("A frame this large is not a message.");

        var buffer = new byte[8 + json.Length + binBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), (uint)json.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4, 4), (uint)binBytes);
        json.CopyTo(buffer, 8);
        for (int i = 0; i < frame.Payload.Length; i++)
            BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(8 + json.Length + i * 8, 8), frame.Payload[i]);
        stream.Write(buffer, 0, buffer.Length);
        stream.Flush();
    }

    /// <summary>Reads one frame, or returns null when the stream ends cleanly BETWEEN frames (circuitRF closed the pipe).
    /// A stream that ends inside a frame is a <see cref="FrameException"/>.</summary>
    public static Frame? Read(Stream stream)
    {
        var header = new byte[8];
        int got = ReadUpTo(stream, header);
        if (got == 0) return null;
        if (got < 8) throw new FrameException($"The stream ended after {got} of a frame header's 8 bytes.");

        uint jsonLen = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0, 4));
        uint binLen = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4));
        if (jsonLen == 0) throw new FrameException("A frame arrived with no JSON; the stream is out of step.");
        if (jsonLen > MaxFrameBytes || binLen > MaxFrameBytes)
            throw new FrameException($"A frame announced {jsonLen} + {binLen} bytes; the stream is out of step.");
        if (binLen % 8 != 0) throw new FrameException($"A frame announced {binLen} payload bytes, not a whole number of int64s.");

        var json = new byte[jsonLen];
        if (ReadUpTo(stream, json) < json.Length) throw new FrameException("The stream ended inside a frame's JSON.");
        var bin = new byte[binLen];
        if (ReadUpTo(stream, bin) < bin.Length) throw new FrameException("The stream ended inside a frame's payload.");

        var payload = new long[binLen / 8];
        for (int i = 0; i < payload.Length; i++) payload[i] = BinaryPrimitives.ReadInt64LittleEndian(bin.AsSpan(i * 8, 8));
        return new Frame(Utf8.GetString(json), payload);
    }

    /// <summary>Fills <paramref name="buffer"/> or stops at the end of the stream, returning the count read. A pipe hands
    /// back whatever has arrived, often less than asked for, so ONE Read is never enough: this loop is the part of a frame
    /// reader that is wrong in a way that only shows under load.</summary>
    private static int ReadUpTo(Stream stream, byte[] buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = stream.Read(buffer, total, buffer.Length - total);
            if (n == 0) break;
            total += n;
        }
        return total;
    }
}

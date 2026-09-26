// brief-em3d-29 R-em3d29-2a — a managed reader for the ParaView files the PINNED Palace (0.18.1) writes,
// and only those. What it writes was read off a real run, not assumed (src/Render/RESOLVED.md §brief-em3d-29):
//
//   * a .pvd collection → one .pvtu per step → one .vtu PIECE PER MPI RANK (proc000000.vtu, …);
//   * an UnstructuredGrid whose geometry (Points, connectivity, offsets, types, the cell "attribute") is
//     INLINE base64 (format="binary", a UInt32 byte-count header encoded on its own, then the data), and
//     whose field arrays are APPENDED RAW (format="appended" offset=…, each block a UInt32 byte count then
//     the bytes) after <AppendedData encoding="raw"> and its "_" marker;
//   * NO compression (the VTKFile element has no compressor attribute);
//   * VTK_LAGRANGE_TETRAHEDRON (71) and VTK_LAGRANGE_TRIANGLE (69) cells whose node count says the order:
//     4/3 nodes at element order 1, 10/6 at order 2 — every cell with its OWN nodes, none shared (a
//     Nédélec field is not continuous across an element face, so there is nothing to share).
//
// Anything else — compressed data, ASCII arrays, another cell type — is REFUSED by name rather than read
// approximately: a field this reader half-understood is exactly the picture the series rule forbids.
//
// No VTK library, no XML DOM over the file (its appended section is raw binary, not text). The header is
// scanned as bytes; an array is decoded straight into the caller's destination, so loading a step costs
// the arrays it keeps plus one transient copy of the base64 header.

using System.Buffers;
using System.Buffers.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace CircuitRF.Render.Scene3D.Fields;

/// <summary>A field file circuitRF cannot read, and why.</summary>
public sealed class FieldReadException(string message) : Exception(message);

/// <summary>The VTK numeric types the pinned Palace writes (and Float64 / Int64, which cost nothing).</summary>
public enum VtuType { Float32, Float64, Int32, Int64, UInt8 }

/// <summary>One DataArray of a piece: where its bytes are and what they are.</summary>
public sealed record VtuArray(string Name, VtuType Type, int Components, bool PerCell, bool Appended,
                              long Start, long End, long AppendedOffset)
{
    public int ElementSize => Type switch { VtuType.Float64 or VtuType.Int64 => 8, VtuType.UInt8 => 1, _ => 4 };
}

/// <summary>A piece's header: its counts and every array in it. Reading one reads no field values.</summary>
public sealed class VtuPiece
{
    public required string Path { get; init; }
    public required int NumberOfPoints { get; init; }
    public required int NumberOfCells { get; init; }
    public required IReadOnlyList<VtuArray> Arrays { get; init; }
    /// <summary>Where the appended section's data starts (just after its "_"), or −1.</summary>
    public required long AppendedStart { get; init; }
    public required bool HeaderIs64 { get; init; }

    public VtuArray? Array(string name) => Arrays.FirstOrDefault(a => a.Name == name);
}

public static class VtuReader
{
    /// <summary>VTK's cell types for Lagrange cells: the only two the pinned Palace writes.</summary>
    public const byte LagrangeTriangle = 69, LagrangeTetrahedron = 71;

    private static readonly byte[] AppendedTag = "<AppendedData"u8.ToArray();

    /// <summary>Reads a piece's header: the XML before the appended section, scanned as bytes.</summary>
    public static VtuPiece ReadPiece(string path)
    {
        using var fh = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var header = ReadHeader(fh, path, out long appendedStart);
        try
        {
            var text = header.Span;
            string vtk = Tag(text, "<VTKFile"u8, path);
            if (Attr(vtk, "compressor") is { } comp)
                throw new FieldReadException($"'{path}' is compressed ({comp}); the 3D view reads the uncompressed files Palace 0.18.1 writes.");
            if (Attr(vtk, "type") != "UnstructuredGrid")
                throw new FieldReadException($"'{path}' is a VTK {Attr(vtk, "type") ?? "file"}, not the unstructured grid Palace writes.");
            if (Attr(vtk, "byte_order") is { } order && order != "LittleEndian")
                throw new FieldReadException($"'{path}' is {order}; the 3D view reads little-endian files.");
            bool header64 = Attr(vtk, "header_type") == "UInt64";

            string piece = Tag(text, "<Piece"u8, path);
            int points = int.Parse(Attr(piece, "NumberOfPoints") ?? "0", CultureInfo.InvariantCulture);
            int cells = int.Parse(Attr(piece, "NumberOfCells") ?? "0", CultureInfo.InvariantCulture);

            var arrays = new List<VtuArray>();
            // The section an array is in decides what it is attached to: <Points>, <Cells> and <CellData>
            // are per cell (or geometry); <PointData> is per node.
            int pointData = text.IndexOf("<PointData"u8), cellData = text.IndexOf("<CellData"u8);
            int at = 0;
            while (true)
            {
                int open = text[at..].IndexOf("<DataArray"u8);
                if (open < 0) break;
                open += at;
                int close = text[open..].IndexOf((byte)'>') + open;
                string tag = Encoding.ASCII.GetString(text[open..(close + 1)]);
                string name = Attr(tag, "Name") ?? "Points";
                var type = Type(Attr(tag, "type"), path, name);
                int comps = int.Parse(Attr(tag, "NumberOfComponents") ?? "1", CultureInfo.InvariantCulture);
                string format = Attr(tag, "format") ?? "ascii";
                bool perCell = cellData >= 0 && open > cellData && (pointData < 0 || open < pointData || cellData > pointData);
                if (name is "connectivity" or "offsets" or "types") perCell = true;
                if (format == "appended")
                {
                    long off = long.Parse(Attr(tag, "offset") ?? "0", CultureInfo.InvariantCulture);
                    arrays.Add(new VtuArray(name, type, comps, perCell, true, 0, 0, off));
                    at = close + 1;
                }
                else if (format == "binary")
                {
                    int end = text[(close + 1)..].IndexOf("</DataArray>"u8) + close + 1;
                    arrays.Add(new VtuArray(name, type, comps, perCell, false, close + 1, end, 0));
                    at = end;
                }
                else throw new FieldReadException($"'{path}' stores '{name}' as {format}; the 3D view reads Palace's binary and appended arrays.");
            }
            return new VtuPiece
            {
                Path = path, NumberOfPoints = points, NumberOfCells = cells, Arrays = arrays,
                AppendedStart = appendedStart, HeaderIs64 = header64,
            };
        }
        finally { if (MemoryMarshal.TryGetArray<byte>(header, out var seg)) ArrayPool<byte>.Shared.Return(seg.Array!); }
    }

    /// <summary>Reads array <paramref name="a"/> of <paramref name="piece"/> into <paramref name="dest"/>
    /// as float (every numeric type converts). <paramref name="dest"/> must hold exactly its values.</summary>
    public static void ReadFloats(VtuPiece piece, VtuArray a, Span<float> dest)
    {
        if (a.Type == VtuType.Float32) { ReadRaw(piece, a, MemoryMarshal.AsBytes(dest)); return; }
        var tmp = ArrayPool<byte>.Shared.Rent(dest.Length * a.ElementSize);
        try
        {
            var bytes = tmp.AsSpan(0, dest.Length * a.ElementSize);
            ReadRaw(piece, a, bytes);
            Convert(a.Type, bytes, dest, v => (float)v);
        }
        finally { ArrayPool<byte>.Shared.Return(tmp); }
    }

    /// <summary>As <see cref="ReadFloats"/>, into int.</summary>
    public static void ReadInts(VtuPiece piece, VtuArray a, Span<int> dest)
    {
        if (a.Type == VtuType.Int32) { ReadRaw(piece, a, MemoryMarshal.AsBytes(dest)); return; }
        var tmp = ArrayPool<byte>.Shared.Rent(dest.Length * a.ElementSize);
        try
        {
            var bytes = tmp.AsSpan(0, dest.Length * a.ElementSize);
            ReadRaw(piece, a, bytes);
            Convert(a.Type, bytes, dest, v => checked((int)v));
        }
        finally { ArrayPool<byte>.Shared.Return(tmp); }
    }

    private static void Convert<T>(VtuType type, ReadOnlySpan<byte> bytes, Span<T> dest, Func<double, T> to)
    {
        for (int i = 0; i < dest.Length; i++)
            dest[i] = to(type switch
            {
                VtuType.Float32 => MemoryMarshal.Read<float>(bytes[(4 * i)..]),
                VtuType.Float64 => MemoryMarshal.Read<double>(bytes[(8 * i)..]),
                VtuType.Int32   => MemoryMarshal.Read<int>(bytes[(4 * i)..]),
                VtuType.Int64   => MemoryMarshal.Read<long>(bytes[(8 * i)..]),
                _               => bytes[i],
            });
    }

    /// <summary>The array's raw bytes, exactly <paramref name="dest"/>'s length of them: from the appended
    /// section, or base64-decoded from the header's inline text.</summary>
    private static void ReadRaw(VtuPiece piece, VtuArray a, Span<byte> dest)
    {
        // RandomAccess on a handle: no stream buffer, so a read allocates nothing beyond the destination.
        using var fh = File.OpenHandle(piece.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        int hsize = piece.HeaderIs64 ? 8 : 4;
        if (a.Appended)
        {
            if (piece.AppendedStart < 0) throw new FieldReadException($"'{piece.Path}' names appended data but has no <AppendedData> section.");
            long at = piece.AppendedStart + a.AppendedOffset;
            Span<byte> h = stackalloc byte[8];
            ReadExactly(fh, h[..hsize], at, piece.Path);
            long n = piece.HeaderIs64 ? MemoryMarshal.Read<long>(h) : MemoryMarshal.Read<uint>(h);
            if (n != dest.Length)
                throw new FieldReadException($"'{piece.Path}': '{a.Name}' holds {n:N0} bytes where its counts say {dest.Length:N0}.");
            ReadExactly(fh, dest, at + hsize, piece.Path);
            return;
        }
        // Inline: the header's byte count is base64 on its own (8 characters for UInt32, 12 for UInt64),
        // then the data's own base64 — the two are NOT one stream.
        long len = a.End - a.Start;
        var text = ArrayPool<byte>.Shared.Rent((int)len);
        try
        {
            var t = text.AsSpan(0, (int)len);
            ReadExactly(fh, t, a.Start, piece.Path);
            t = Trim(t);
            int hchars = piece.HeaderIs64 ? 12 : 8;
            Span<byte> h = stackalloc byte[9];
            if (Base64.DecodeFromUtf8(t[..hchars], h, out _, out int hw) != OperationStatus.Done || hw < hsize)
                throw new FieldReadException($"'{piece.Path}': '{a.Name}' has no readable length header.");
            long n = piece.HeaderIs64 ? MemoryMarshal.Read<long>(h) : MemoryMarshal.Read<uint>(h);
            if (n != dest.Length)
                throw new FieldReadException($"'{piece.Path}': '{a.Name}' holds {n:N0} bytes where its counts say {dest.Length:N0}.");
            var status = Base64.DecodeFromUtf8(t[hchars..], dest, out _, out int written);
            if (status != OperationStatus.Done || written != dest.Length)
                throw new FieldReadException($"'{piece.Path}': '{a.Name}' could not be decoded ({status}).");
        }
        finally { ArrayPool<byte>.Shared.Return(text); }
    }

    private static void ReadExactly(Microsoft.Win32.SafeHandles.SafeFileHandle fh, Span<byte> dest, long offset, string path)
    {
        while (dest.Length > 0)
        {
            int r = RandomAccess.Read(fh, dest, offset);
            if (r <= 0) throw new FieldReadException($"'{path}' ends before its data does.");
            dest = dest[r..];
            offset += r;
        }
    }

    private static Span<byte> Trim(Span<byte> s)
    {
        int a = 0, b = s.Length;
        while (a < b && s[a] <= (byte)' ') a++;
        while (b > a && s[b - 1] <= (byte)' ') b--;
        return s[a..b];
    }

    /// <summary>The file up to its appended section (the whole file if it has none), in a pooled buffer.</summary>
    private static Memory<byte> ReadHeader(Microsoft.Win32.SafeHandles.SafeFileHandle fh, string path, out long appendedStart)
    {
        appendedStart = -1;
        long length = RandomAccess.GetLength(fh);
        var buf = ArrayPool<byte>.Shared.Rent((int)Math.Min(length, 1 << 20) + 16);
        int n = 0;
        while (true)
        {
            if (n == buf.Length)
            {
                var bigger = ArrayPool<byte>.Shared.Rent(buf.Length * 2);
                buf.AsSpan(0, n).CopyTo(bigger);
                ArrayPool<byte>.Shared.Return(buf);
                buf = bigger;
            }
            int r = RandomAccess.Read(fh, buf.AsSpan(n), n);
            int from = Math.Max(0, n - AppendedTag.Length);
            n += r;
            int tag = buf.AsSpan(from, n - from).IndexOf(AppendedTag);
            if (tag >= 0)
            {
                tag += from;
                // The appended data starts after the first '_' past the tag's '>'.
                int us = -1;
                while (true)
                {
                    int gt = buf.AsSpan(tag, n - tag).IndexOf((byte)'>');
                    if (gt >= 0 && (us = buf.AsSpan(tag + gt, n - tag - gt).IndexOf((byte)'_')) >= 0) { us += tag + gt; break; }
                    if (n == buf.Length)
                    {
                        var bigger = ArrayPool<byte>.Shared.Rent(buf.Length * 2);
                        buf.AsSpan(0, n).CopyTo(bigger);
                        ArrayPool<byte>.Shared.Return(buf);
                        buf = bigger;
                    }
                    int more = RandomAccess.Read(fh, buf.AsSpan(n), n);
                    if (more <= 0) throw new FieldReadException($"'{path}' ends inside its <AppendedData> tag.");
                    n += more;
                }
                appendedStart = us + 1;
                return buf.AsMemory(0, tag);
            }
            if (r == 0) return buf.AsMemory(0, n);
        }
    }

    private static string Tag(ReadOnlySpan<byte> text, ReadOnlySpan<byte> open, string path)
    {
        int a = text.IndexOf(open);
        if (a < 0) throw new FieldReadException($"'{path}' has no {Encoding.ASCII.GetString(open)}> element; it is not a VTK unstructured grid.");
        int b = text[a..].IndexOf((byte)'>') + a;
        return Encoding.ASCII.GetString(text[a..(b + 1)]);
    }

    /// <summary>An attribute's value in a start tag, or null.</summary>
    internal static string? Attr(string tag, string name)
    {
        int i = 0;
        while ((i = tag.IndexOf(name + "=\"", i, StringComparison.Ordinal)) >= 0)
        {
            // Whole-name match only: "Name" must not match "ComponentName0".
            if (i == 0 || tag[i - 1] is ' ' or '\t' or '\n' or '\r')
            {
                int s = i + name.Length + 2, e = tag.IndexOf('"', s);
                return e < 0 ? null : tag[s..e];
            }
            i += name.Length;
        }
        return null;
    }

    private static VtuType Type(string? t, string path, string name) => t switch
    {
        "Float32" => VtuType.Float32, "Float64" => VtuType.Float64, "Int32" => VtuType.Int32,
        "Int64" => VtuType.Int64, "UInt8" => VtuType.UInt8,
        _ => throw new FieldReadException($"'{path}' stores '{name}' as {t ?? "an unnamed type"}, which the 3D view does not read."),
    };
}

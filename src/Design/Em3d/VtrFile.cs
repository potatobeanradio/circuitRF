// brief-em3d-31 — openEMS's VTK rectilinear-grid files, decoded. Moved here out of src/Render's VtrReader
// (brief-em3d-29, which records what the pinned openEMS writes and how that was established) because the
// radiation pattern reads openEMS's surface dumps in src/Design, which cannot reference src/Render. The 3D
// view's reader now calls this one, so there is one decoder.
//
//   * INLINE binary (format="binary"), zlib-compressed (compressor="vtkZLibDataCompressor"), UInt32 headers:
//     each array is a base64 header [blocks, block size, last block size, compressed sizes…] followed by
//     the base64 of the compressed blocks, each block its own zlib stream. System.IO.Compression's
//     ZLibStream is .NET's own — no native library.

using System.Buffers.Text;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace CircuitRF.Design.Em3d;

/// <summary>A rectilinear grid's lines and one array on its nodes, <paramref name="Components"/> per node.</summary>
public sealed record VtrData(double[] X, double[] Y, double[] Z, string Name, int Components, float[] Values);

public static class VtrFile
{
    private static readonly Regex DataArray = new(@"<DataArray\b([^>]*)>(.*?)</DataArray>", RegexOptions.Singleline | RegexOptions.CultureInvariant);

    /// <summary>Reads <paramref name="path"/>: its coordinates and its first point-data array.</summary>
    public static VtrData Read(string path)
    {
        string text = File.ReadAllText(path);
        int head = text.IndexOf("<VTKFile", StringComparison.Ordinal);
        if (head < 0) throw new InvalidDataException($"'{path}' is not a VTK XML file.");
        string vtk = text[head..(text.IndexOf('>', head) + 1)];
        if (TagAttr(vtk, "type") != "RectilinearGrid")
            throw new InvalidDataException($"'{path}' is a VTK {TagAttr(vtk, "type")}, not the rectilinear grid openEMS dumps.");
        if (TagAttr(vtk, "byte_order") is { } bo && bo != "LittleEndian")
            throw new InvalidDataException($"'{path}' is {bo}; circuitRF reads little-endian files.");
        bool h64 = TagAttr(vtk, "header_type") == "UInt64";
        string? compressor = TagAttr(vtk, "compressor");
        if (compressor is not (null or "vtkZLibDataCompressor"))
            throw new InvalidDataException($"'{path}' is compressed with {compressor}; circuitRF reads zlib, the pinned openEMS's.");
        bool zlib = compressor is not null;

        int pd = text.IndexOf("<PointData", StringComparison.Ordinal), co = text.IndexOf("<Coordinates", StringComparison.Ordinal);
        if (pd < 0 || co < 0) throw new InvalidDataException($"'{path}' has no point data or no coordinates.");
        var arrays = DataArray.Matches(text).ToList();
        var coords = arrays.Where(m => m.Index > co).Take(3).ToList();
        var field = arrays.FirstOrDefault(m => m.Index > pd && (co < pd || m.Index < co));
        if (coords.Count != 3 || field is null) throw new InvalidDataException($"'{path}' does not hold three coordinate arrays and a field.");
        double[] Coords(Match m) => Doubles(Decode(m, zlib, h64, path), Attr(m, "type"), path);
        var (x, y, z) = (Coords(coords[0]), Coords(coords[1]), Coords(coords[2]));
        int comps = int.Parse(Attr(field, "NumberOfComponents") ?? "1", CultureInfo.InvariantCulture);
        var raw = Decode(field, zlib, h64, path);
        float[] values = Attr(field, "type") switch
        {
            "Float32" => MemoryMarshal.Cast<byte, float>(raw).ToArray(),
            "Float64" => [.. MemoryMarshal.Cast<byte, double>(raw).ToArray().Select(v => (float)v)],
            var t => throw new InvalidDataException($"'{path}' stores its field as {t}."),
        };
        if (values.Length != (long)x.Length * y.Length * z.Length * comps)
            throw new InvalidDataException($"'{path}' holds {values.Length:N0} values for a {x.Length} × {y.Length} × {z.Length} grid of {comps}.");
        return new VtrData(x, y, z, Attr(field, "Name") ?? "field", comps, values);
    }

    private static string? Attr(Match m, string name) => TagAttr(" " + m.Groups[1].Value, name);

    private static double[] Doubles(byte[] raw, string? type, string path) => type switch
    {
        "Float64" => MemoryMarshal.Cast<byte, double>(raw).ToArray(),
        "Float32" => [.. MemoryMarshal.Cast<byte, float>(raw).ToArray().Select(v => (double)v)],
        _ => throw new InvalidDataException($"'{path}' stores coordinates as {type}."),
    };

    /// <summary>An inline binary array's bytes: base64, and zlib blocks when the file says so.</summary>
    private static byte[] Decode(Match m, bool zlib, bool h64, string path)
    {
        if ((Attr(m, "format") ?? "ascii") != "binary")
            throw new InvalidDataException($"'{path}' stores an array as {Attr(m, "format")}; circuitRF reads openEMS's binary arrays.");
        // The element's text holds the base64 and, after it, any <InformationKey> children VTK adds.
        string body = m.Groups[2].Value;
        int lt = body.IndexOf('<');
        var b64 = System.Text.Encoding.ASCII.GetBytes((lt >= 0 ? body[..lt] : body).Trim());
        int hs = h64 ? 8 : 4;
        if (!zlib)
        {
            int hc = h64 ? 12 : 8;
            var h = new byte[9];
            Base64.DecodeFromUtf8(b64.AsSpan(0, hc), h, out _, out _);
            long n = h64 ? BitConverter.ToInt64(h) : BitConverter.ToUInt32(h);
            var data = new byte[n];
            if (Base64.DecodeFromUtf8(b64.AsSpan(hc), data, out _, out int w) != System.Buffers.OperationStatus.Done || w != n)
                throw new InvalidDataException($"'{path}': an array could not be decoded.");
            return data;
        }
        // The compressed header: [blocks, block size, last block size, compressed size × blocks], one base64 unit.
        Span<byte> first = stackalloc byte[12];
        Base64.DecodeFromUtf8(b64.AsSpan(0, 16), first, out _, out _);
        long blocks = h64 ? BitConverter.ToInt64(first) : BitConverter.ToUInt32(first);
        long headerBytes = (3 + blocks) * hs;
        int headerChars = (int)(4 * ((headerBytes + 2) / 3));
        var header = new byte[headerBytes + 3];
        Base64.DecodeFromUtf8(b64.AsSpan(0, headerChars), header, out _, out _);
        long Word(int i) => h64 ? BitConverter.ToInt64(header, i * hs) : BitConverter.ToUInt32(header, i * hs);
        long blockSize = Word(1), last = Word(2);
        long total = blocks == 0 ? 0 : (blocks - 1) * blockSize + (last == 0 ? blockSize : last);
        var compressed = new byte[b64.Length];
        if (Base64.DecodeFromUtf8(b64.AsSpan(headerChars), compressed, out _, out int cw) != System.Buffers.OperationStatus.Done)
            throw new InvalidDataException($"'{path}': an array's compressed blocks could not be decoded.");
        var output = new byte[total];
        long at = 0, from = 0;
        for (int b = 0; b < blocks; b++)
        {
            long csize = Word(3 + b), usize = b == blocks - 1 && last != 0 ? last : blockSize;
            if (from + csize > cw) throw new InvalidDataException($"'{path}': an array's compressed blocks are shorter than its header says.");
            using var z = new ZLibStream(new MemoryStream(compressed, (int)from, (int)csize), CompressionMode.Decompress);
            z.ReadExactly(output.AsSpan((int)at, (int)usize));
            at += usize;
            from += csize;
        }
        return output;
    }

    /// <summary>An attribute's value in a start tag, or null — whole-name match only.</summary>
    public static string? TagAttr(string tag, string name)
    {
        int i = 0;
        while ((i = tag.IndexOf(name + "=\"", i, StringComparison.Ordinal)) >= 0)
        {
            if (i == 0 || tag[i - 1] is ' ' or '\t' or '\n' or '\r')
            {
                int s = i + name.Length + 2, e = tag.IndexOf('"', s);
                return e < 0 ? null : tag[s..e];
            }
            i += name.Length;
        }
        return null;
    }
}

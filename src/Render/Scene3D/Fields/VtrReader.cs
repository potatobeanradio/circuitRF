// brief-em3d-29 R-em3d29-6 — openEMS's frequency-domain field dump, read. What the pinned openEMS writes was
// read off a real run and its own source (processfields_fd.cpp, vtk_file_writer.cpp), not assumed:
//
//   * a dump box with FileType 0 (VTK) and DumpType 10 writes, per frequency, <name>_f=<Hz>_abs.vtr and
//     <name>_f=<Hz>_arg.vtr — each component's MAGNITUDE and PHASE — plus 21 real snapshots at fixed
//     phases (_p=000 … _p=342), which carry nothing the pair does not;
//   * each is a VTK XML RectilinearGrid: Coordinates (three Float64 arrays), and the field ("E-Field",
//     three Float32 components) as PointData — at the grid's cell centres, with DumpMode 2;
//   * INLINE binary (format="binary"), zlib-compressed (compressor="vtkZLibDataCompressor"), UInt32 headers:
//     each array is a base64 header [blocks, block size, last block size, compressed sizes…] followed by
//     the base64 of the compressed blocks, each block its own zlib stream.
//
// HDF5 would have needed a native library (CLAUDE.md, ask first); the VTK route needs none, and
// System.IO.Compression's ZLibStream is .NET's own.
//
// THE SAME FIELD MODEL (R-em3d29-6b): every grid cell is six linear tetrahedra over its eight corners, so the
// slicer, the surfaces, the sampler and the GPU pass draw openEMS's field with no special case. On a grid
// line the slice is exact, as the brief asks: every tetrahedron's corners are grid nodes.

using System.Buffers.Text;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace CircuitRF.Render.Scene3D.Fields;

/// <summary>A rectilinear grid's lines and one vector array on its nodes.</summary>
public sealed record VtrField(double[] X, double[] Y, double[] Z, string Name, int Components, float[] Values);

public static class VtrReader
{
    private static readonly Regex DataArray = new(@"<DataArray\b([^>]*)>(.*?)</DataArray>", RegexOptions.Singleline | RegexOptions.CultureInvariant);

    /// <summary>Reads <paramref name="path"/>: its coordinates and its first point-data array.</summary>
    public static VtrField Read(string path)
    {
        string text = File.ReadAllText(path);
        int head = text.IndexOf("<VTKFile", StringComparison.Ordinal);
        if (head < 0) throw new FieldReadException($"'{path}' is not a VTK XML file.");
        string vtk = text[head..(text.IndexOf('>', head) + 1)];
        if (VtuReader.Attr(vtk, "type") != "RectilinearGrid")
            throw new FieldReadException($"'{path}' is a VTK {VtuReader.Attr(vtk, "type")}, not the rectilinear grid openEMS dumps.");
        if (VtuReader.Attr(vtk, "byte_order") is { } bo && bo != "LittleEndian")
            throw new FieldReadException($"'{path}' is {bo}; the 3D view reads little-endian files.");
        bool h64 = VtuReader.Attr(vtk, "header_type") == "UInt64";
        string? compressor = VtuReader.Attr(vtk, "compressor");
        if (compressor is not (null or "vtkZLibDataCompressor"))
            throw new FieldReadException($"'{path}' is compressed with {compressor}; the 3D view reads zlib, the pinned openEMS's.");
        bool zlib = compressor is not null;

        int pd = text.IndexOf("<PointData", StringComparison.Ordinal), co = text.IndexOf("<Coordinates", StringComparison.Ordinal);
        if (pd < 0 || co < 0) throw new FieldReadException($"'{path}' has no point data or no coordinates.");
        var arrays = DataArray.Matches(text).ToList();
        var coords = arrays.Where(m => m.Index > co).Take(3).ToList();
        var field = arrays.FirstOrDefault(m => m.Index > pd && (co < pd || m.Index < co));
        if (coords.Count != 3 || field is null) throw new FieldReadException($"'{path}' does not hold three coordinate arrays and a field.");
        double[] Coords(Match m) => Doubles(Decode(m, zlib, h64, path), Attr(m, "type"), path);
        var (x, y, z) = (Coords(coords[0]), Coords(coords[1]), Coords(coords[2]));
        int comps = int.Parse(Attr(field, "NumberOfComponents") ?? "1", CultureInfo.InvariantCulture);
        var raw = Decode(field, zlib, h64, path);
        float[] values = Attr(field, "type") switch
        {
            "Float32" => MemoryMarshal.Cast<byte, float>(raw).ToArray(),
            "Float64" => [.. MemoryMarshal.Cast<byte, double>(raw).ToArray().Select(v => (float)v)],
            var t => throw new FieldReadException($"'{path}' stores its field as {t}."),
        };
        if (values.Length != (long)x.Length * y.Length * z.Length * comps)
            throw new FieldReadException($"'{path}' holds {values.Length:N0} values for a {x.Length} × {y.Length} × {z.Length} grid of {comps}.");
        return new VtrField(x, y, z, Attr(field, "Name") ?? "field", comps, values);
    }

    private static string? Attr(Match m, string name) => VtuReader.Attr(" " + m.Groups[1].Value, name);

    private static double[] Doubles(byte[] raw, string? type, string path) => type switch
    {
        "Float64" => MemoryMarshal.Cast<byte, double>(raw).ToArray(),
        "Float32" => [.. MemoryMarshal.Cast<byte, float>(raw).ToArray().Select(v => (double)v)],
        _ => throw new FieldReadException($"'{path}' stores coordinates as {type}."),
    };

    /// <summary>An inline binary array's bytes: base64, and zlib blocks when the file says so.</summary>
    private static byte[] Decode(Match m, bool zlib, bool h64, string path)
    {
        if ((Attr(m, "format") ?? "ascii") != "binary")
            throw new FieldReadException($"'{path}' stores an array as {Attr(m, "format")}; the 3D view reads openEMS's binary arrays.");
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
                throw new FieldReadException($"'{path}': an array could not be decoded.");
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
            throw new FieldReadException($"'{path}': an array's compressed blocks could not be decoded.");
        var output = new byte[total];
        long at = 0, from = 0;
        for (int b = 0; b < blocks; b++)
        {
            long csize = Word(3 + b), usize = b == blocks - 1 && last != 0 ? last : blockSize;
            if (from + csize > cw) throw new FieldReadException($"'{path}': an array's compressed blocks are shorter than its header says.");
            using var z = new ZLibStream(new MemoryStream(compressed, (int)from, (int)csize), CompressionMode.Decompress);
            z.ReadExactly(output.AsSpan((int)at, (int)usize));
            at += usize;
            from += csize;
        }
        return output;
    }

    /// <summary>
    /// A rectilinear grid's cells as linear tetrahedra — six per cell over its eight corners, about the
    /// cell's (0,0,0)–(1,1,1) diagonal — sharing the grid's nodes. Attribute 0 throughout: a dump box
    /// carries no material. Positions are the grid's own (metres, for openEMS).
    /// </summary>
    public static FieldMesh Mesh(VtrField f, double toMetres = 1)
    {
        int nx = f.X.Length, ny = f.Y.Length, nz = f.Z.Length;
        var points = new float[3 * nx * ny * nz];
        for (int k = 0, p = 0; k < nz; k++)
            for (int j = 0; j < ny; j++)
                for (int i = 0; i < nx; i++, p += 3)
                {
                    points[p] = (float)f.X[i]; points[p + 1] = (float)f.Y[j]; points[p + 2] = (float)f.Z[k];
                }
        long cells = (long)Math.Max(0, nx - 1) * Math.Max(0, ny - 1) * Math.Max(0, nz - 1);
        var conn = new int[checked(24 * cells)];
        ReadOnlySpan<int> six = [0, 1, 3, 7, 0, 1, 5, 7, 0, 2, 3, 7, 0, 2, 6, 7, 0, 4, 5, 7, 0, 4, 6, 7];
        int Id(int i, int j, int k) => (k * ny + j) * nx + i;
        int c = 0;
        for (int k = 0; k + 1 < nz; k++)
            for (int j = 0; j + 1 < ny; j++)
                for (int i = 0; i + 1 < nx; i++)
                    foreach (int b in six)
                        conn[c++] = Id(i + (b & 1), j + ((b >> 1) & 1), k + ((b >> 2) & 1));
        return new FieldMesh
        {
            Shape = FieldCellShape.Tetrahedron, Order = 1, NodesPerCell = 4, Points = points, Cells = conn,
            Attribute = new int[6 * cells], ToMetres = toMetres,
        };
    }
}

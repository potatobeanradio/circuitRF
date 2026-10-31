// ================================================================
//  NpzReader.cs  —  NumPy .npz archives and plain .npy arrays, for data another program wrote
//
//  numpy.savez / savez_compressed write a zip archive with one plain .npy member per named array
//  ("vgs.npy", "Y.npy"). A plain .npy is the NPY format's own header — a Python dict literal giving
//  'descr' (the dtype), 'fortran_order' and 'shape' — followed by the raw values. Written in-house from
//  the published NPY format description; no third-party code.
//
//  circuitRF's own .npy is a different thing: a structured array whose 'descr' is a LIST of fields,
//  read by NpyReader. IsPlainNpy tells the two apart without reading the data.
//
//  Arrays come back as MatArray, in C order over their logical shape, exactly as MatReader returns
//  them: a Fortran-ordered member is transposed. Numbers of any width and either byte order, booleans
//  and complex numbers are read; text, object and structured members are not, and are named by List
//  and refused by Read when asked for.
// ================================================================

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace RfCore.Export;

public static class NpzReader
{
    /// <summary>The arrays <paramref name="path"/> (a <c>.npz</c>) holds; <paramref name="names"/> limits what is decoded.
    /// A requested name the archive does not hold is absent — the caller says which it needed.</summary>
    public static IReadOnlyDictionary<string, MatArray> Read(string path, IReadOnlyCollection<string>? names = null) =>
        ReadCore(path, names, null);

    /// <summary>Every member, numeric ones decoded and the rest named with the reason they are not read.</summary>
    public static (IReadOnlyDictionary<string, MatArray> Arrays, IReadOnlyList<(string Name, string Why)> Skipped) List(string path)
    {
        var skipped = new List<(string, string)>();
        return (ReadCore(path, null, skipped), skipped);
    }

    /// <summary>True when <paramref name="path"/> is a plain NPY array (what <c>numpy.save</c> writes), with its dtype and
    /// shape; false for circuitRF's structured <c>.npy</c> or anything that is not NPY.</summary>
    public static bool IsPlainNpy(string path, out string descr, out int[] shape)
    {
        descr = ""; shape = [];
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var h = Header(fs, Path.GetFileName(path));
            if (h is null) return false;
            descr = h.Value.Descr; shape = h.Value.Shape;
            return true;
        }
        catch (InvalidDataException) { return false; }
    }

    /// <summary>One plain NPY array from <paramref name="path"/>.</summary>
    public static MatArray ReadNpy(string path, string name)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Member(fs, name, Path.GetFileName(path));
    }

    private static Dictionary<string, MatArray> ReadCore(string path, IReadOnlyCollection<string>? names, List<(string, string)>? skipped)
    {
        var result = new Dictionary<string, MatArray>(StringComparer.Ordinal);
        ZipArchive zip;
        try { zip = ZipFile.OpenRead(path); }
        catch (InvalidDataException) { throw new InvalidDataException($"{Path.GetFileName(path)} is not an .npz archive."); }
        using (zip)
            foreach (var entry in zip.Entries.OrderBy(e => e.FullName, StringComparer.Ordinal))
            {
                if (!entry.FullName.EndsWith(".npy", StringComparison.Ordinal)) continue;
                string name = entry.FullName[..^4];
                if (names is not null && !names.Contains(name)) continue;
                using var s = entry.Open();
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                ms.Position = 0;
                try { result[name] = Member(ms, name, $"{Path.GetFileName(path)}:{entry.FullName}"); }
                catch (InvalidDataException ex)
                {
                    if (skipped is null) throw new InvalidDataException($"'{name}' {ex.Message}");
                    skipped.Add((name, ex.Message));
                }
            }
        return result;
    }

    private readonly record struct NpyHeader(string Descr, bool Fortran, int[] Shape);

    /// <summary>The header of a plain NPY array, the stream left at its data; null for a structured array.</summary>
    private static NpyHeader? Header(Stream s, string where)
    {
        Span<byte> magic = stackalloc byte[10];
        if (s.ReadAtLeast(magic, 10, throwOnEndOfStream: false) < 10 || magic[0] != 0x93 || Encoding.ASCII.GetString(magic[1..6]) != "NUMPY")
            throw new InvalidDataException($"{where} is not an NPY array.");
        int major = magic[6];
        int len;
        if (major == 1) len = BinaryPrimitives.ReadUInt16LittleEndian(magic[8..]);
        else
        {
            Span<byte> more = stackalloc byte[2];
            if (s.ReadAtLeast(more, 2, throwOnEndOfStream: false) < 2) throw new InvalidDataException($"{where}: its header is truncated.");
            len = (int)(magic[8] | (uint)magic[9] << 8 | (uint)more[0] << 16 | (uint)more[1] << 24);
        }
        var hb = new byte[len];
        if (s.ReadAtLeast(hb, len, throwOnEndOfStream: false) < len) throw new InvalidDataException($"{where}: its header is truncated.");
        string header = Encoding.Latin1.GetString(hb);

        string descrValue = Value(header, "descr", where);
        if (descrValue.StartsWith('[')) return null;
        string descr = descrValue.Trim('\'', '"');
        bool fortran = Value(header, "fortran_order", where).StartsWith("True", StringComparison.Ordinal);
        string shapeText = Value(header, "shape", where).Trim('(', ')');
        int[] shape = [.. shapeText.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                                   .Select(t => int.Parse(t.TrimEnd('L'), CultureInfo.InvariantCulture))];
        return new NpyHeader(descr, fortran, shape);
    }

    /// <summary>The literal text of <paramref name="key"/>'s value in the header dict.</summary>
    private static string Value(string header, string key, string where)
    {
        int k = header.IndexOf($"'{key}'", StringComparison.Ordinal);
        if (k < 0) k = header.IndexOf($"\"{key}\"", StringComparison.Ordinal);
        if (k < 0) throw new InvalidDataException($"{where}: its NPY header has no '{key}'.");
        int colon = header.IndexOf(':', k);
        int start = colon + 1;
        while (start < header.Length && header[start] == ' ') start++;
        char open = header[start];
        int end = open switch
        {
            '(' => header.IndexOf(')', start) + 1,
            '[' => header.IndexOf(']', start) + 1,
            '\'' or '"' => header.IndexOf(open, start + 1) + 1,
            _ => header.IndexOfAny([',', '}'], start),
        };
        if (end <= start) throw new InvalidDataException($"{where}: its NPY header's '{key}' is malformed.");
        return header[start..end].Trim();
    }

    private static MatArray Member(Stream s, string name, string where)
    {
        var h = Header(s, where) ?? throw new InvalidDataException("is a structured array, not a numeric one.");
        string d = h.Descr;
        if (d.Length < 2) throw new InvalidDataException($"has dtype '{d}', which is not a number type.");
        bool big = d[0] == '>';
        char kind = d[1];
        int width = int.Parse(d[2..], CultureInfo.InvariantCulture);
        if (kind is not ('f' or 'i' or 'u' or 'b' or 'c'))
            throw new InvalidDataException(kind switch
            {
                'U' or 'S' => "holds text, not numbers.",
                'O' => "holds Python objects, not numbers.",
                _ => $"has dtype '{d}', which is not a number type.",
            });
        int count = h.Shape.Aggregate(1, (a, x) => a * x);
        bool complex = kind == 'c';
        int part = complex ? width / 2 : width;
        var bytes = new byte[(long)count * width];
        if (s.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false) < bytes.Length)
            throw new InvalidDataException($"holds fewer values than its shape ({string.Join(" × ", h.Shape)}).");

        var re = new double[count];
        var im = complex ? new double[count] : null;
        for (int i = 0; i < count; i++)
        {
            if (complex)
            {
                re[i] = Number(bytes.AsSpan(i * width, part), 'f', big);
                im![i] = Number(bytes.AsSpan(i * width + part, part), 'f', big);
            }
            else re[i] = Number(bytes.AsSpan(i * width, width), kind, big);
        }
        if (h.Fortran && h.Shape.Length > 1)
        {
            re = FortranToC(re, h.Shape);
            if (im is not null) im = FortranToC(im, h.Shape);
        }
        return new MatArray(name, h.Shape.Length == 0 ? [1] : h.Shape, re, im);
    }

    private static double Number(ReadOnlySpan<byte> b, char kind, bool big) => (kind, b.Length) switch
    {
        ('f', 8) => big ? BinaryPrimitives.ReadDoubleBigEndian(b) : BinaryPrimitives.ReadDoubleLittleEndian(b),
        ('f', 4) => big ? BinaryPrimitives.ReadSingleBigEndian(b) : BinaryPrimitives.ReadSingleLittleEndian(b),
        ('f', 2) => (double)(big ? BinaryPrimitives.ReadHalfBigEndian(b) : BinaryPrimitives.ReadHalfLittleEndian(b)),
        ('i', 1) => (sbyte)b[0],
        ('u', 1) or ('b', 1) => b[0],
        ('i', 2) => big ? BinaryPrimitives.ReadInt16BigEndian(b) : BinaryPrimitives.ReadInt16LittleEndian(b),
        ('u', 2) => big ? BinaryPrimitives.ReadUInt16BigEndian(b) : BinaryPrimitives.ReadUInt16LittleEndian(b),
        ('i', 4) => big ? BinaryPrimitives.ReadInt32BigEndian(b) : BinaryPrimitives.ReadInt32LittleEndian(b),
        ('u', 4) => big ? BinaryPrimitives.ReadUInt32BigEndian(b) : BinaryPrimitives.ReadUInt32LittleEndian(b),
        ('i', 8) => big ? BinaryPrimitives.ReadInt64BigEndian(b) : BinaryPrimitives.ReadInt64LittleEndian(b),
        ('u', 8) => big ? BinaryPrimitives.ReadUInt64BigEndian(b) : BinaryPrimitives.ReadUInt64LittleEndian(b),
        _ => throw new InvalidDataException($"has a {b.Length}-byte '{kind}' type this reader does not know."),
    };

    private static double[] FortranToC(double[] src, int[] shape)
    {
        var dst = new double[src.Length];
        int rank = shape.Length;
        var idx = new int[rank];
        for (int c = 0; c < src.Length; c++)
        {
            int rem = c;
            for (int k = rank - 1; k >= 0; k--) { idx[k] = rem % shape[k]; rem /= shape[k]; }
            int f = 0, stride = 1;
            for (int k = 0; k < rank; k++) { f += idx[k] * stride; stride *= shape[k]; }
            dst[c] = src[f];
        }
        return dst;
    }
}

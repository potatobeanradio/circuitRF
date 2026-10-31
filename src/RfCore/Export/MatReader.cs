// ================================================================
//  MatReader.cs  —  MATLAB .mat reader (brief-modelcomposer-3 R-mc3-3)
//
//  The other half of MatWriter, for arrays another program wrote: a model built from simulated data
//  reads its tables from whatever the other simulator saved. Two containers:
//
//    MAT v5     the classic binary format — numeric matrices, structs of them, and compressed
//               (miCOMPRESSED) elements, in either byte order. Written in-house from the published
//               format description; no third-party code.
//    MAT v7.3   an HDF5 file, read through PureHDF (the library MatWriter writes with). MATLAB's own
//               files carry a 512-byte user block before the HDF5 superblock; HDF5 looks for the
//               superblock there itself.
//
//  ARRAY ORDER. Every array comes back in C order over its LOGICAL dimensions — MATLAB's (rows, cols,
//  …). A v5 file stores column-major, so the reader transposes. A v7.3 file that MATLAB wrote (its
//  datasets carry a MATLAB_class attribute) stores the same column-major bytes with the HDF5
//  dimensions reversed, so it is transposed too. An HDF5 file with no MATLAB_class — MatWriter's
//  own, or one h5py wrote — is already C order over its HDF5 dimensions and is taken as it is. So a
//  3 × 4 matrix is a 3 × 4 array from all three writers, element for element.
//
//  NAMES. A top-level variable is its name; a field of a 1 × 1 struct is "s.field", recursively; an
//  HDF5 dataset inside a plain group is "group/dataset". MATLAB's bookkeeping groups (#refs#,
//  #subsystem#) are never listed.
//
//  READ ONLY WHAT IS NEEDED. Given a set of names, an unrequested variable is skipped — in a v5
//  file without decoding its data, in a compressed element without inflating past its name. A cell
//  array, an object, a sparse matrix or a struct array is REFUSED by name when it is asked for, or
//  when everything is asked for (a null name set); an unrequested one costs nothing and says nothing.
//  List is the one exception: it is for choosing what to read, so it names such a variable with its reason
//  and carries on.
// ================================================================

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using PureHDF;

namespace RfCore.Export;

/// <summary>A <c>.mat</c> the reader refuses. <see cref="Variable"/> is the name it is about, when there is one.</summary>
public sealed class MatFormatException(string message, string? variable = null) : Exception(message)
{
    public string? Variable { get; } = variable;
}

/// <summary>
/// One numeric array from a <c>.mat</c>: its logical shape and its values in C order over that
/// shape. <see cref="Imag"/> is null for a real array.
/// </summary>
public sealed class MatArray(string name, int[] shape, double[] real, double[]? imag)
{
    public string Name { get; } = name;
    public int[] Shape { get; } = shape;
    public double[] Real { get; } = real;
    public double[]? Imag { get; } = imag;

    public bool IsComplex => Imag is not null;
    public int Count => Real.Length;

    public Complex At(int i) => new(Real[i], Imag is null ? 0 : Imag[i]);
}

public static class MatReader
{
    /// <summary>
    /// Reads <paramref name="path"/>. <paramref name="names"/> limits what is decoded (null reads
    /// every numeric array the file holds). A requested name the file does not hold is simply absent
    /// from the result — the caller says which name it needed.
    /// </summary>
    public static IReadOnlyDictionary<string, MatArray> Read(string path, IReadOnlyCollection<string>? names = null)
    {
        byte[] head = new byte[128];
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            int n = fs.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            if (n < 8) throw new MatFormatException($"{Path.GetFileName(path)} is not a MAT-file: it is {n} bytes long.");
        }

        if (IsHdf5(head) || Encoding.ASCII.GetString(head, 0, 20).StartsWith("MATLAB 7.3", StringComparison.Ordinal))
            return ReadHdf5(path, names);
        if (Encoding.ASCII.GetString(head, 0, 6) == "MATLAB")
            return ReadV5(File.ReadAllBytes(path), names);
        throw new MatFormatException($"{Path.GetFileName(path)} is not a MAT-file: it begins with neither a MATLAB header nor an HDF5 signature.");
    }

    /// <summary>
    /// Every variable <paramref name="path"/> holds, for a caller choosing what to read: each numeric array decoded, and each
    /// variable this reader cannot read (a cell array, text, an object, a sparse matrix) by name with the reason, instead of
    /// refusing the whole file for it. A file that is not a MAT-file at all is still refused.
    /// </summary>
    public static (IReadOnlyDictionary<string, MatArray> Arrays, IReadOnlyList<(string Name, string Why)> Skipped) List(string path)
    {
        var skipped = new List<(string, string)>();
        byte[] head = new byte[128];
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            int n = fs.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            if (n < 8) throw new MatFormatException($"{Path.GetFileName(path)} is not a MAT-file: it is {n} bytes long.");
        }
        if (IsHdf5(head) || Encoding.ASCII.GetString(head, 0, 20).StartsWith("MATLAB 7.3", StringComparison.Ordinal))
            return (ReadHdf5(path, null, skipped), skipped);
        if (Encoding.ASCII.GetString(head, 0, 6) == "MATLAB")
            return (ReadV5(File.ReadAllBytes(path), null, skipped), skipped);
        throw new MatFormatException($"{Path.GetFileName(path)} is not a MAT-file: it begins with neither a MATLAB header nor an HDF5 signature.");
    }

    /// <summary>Records <paramref name="why"/> for a listing and returns true; refuses when this is a read.</summary>
    private static bool Skip(List<(string, string)>? skipped, string name, string why)
    {
        if (skipped is null) throw new MatFormatException($"'{name}' {why}", name);
        skipped.Add((name, why));
        return true;
    }

    private static readonly byte[] Hdf5Signature = [0x89, (byte)'H', (byte)'D', (byte)'F', 0x0D, 0x0A, 0x1A, 0x0A];

    private static bool IsHdf5(byte[] head) => head.AsSpan(0, 8).SequenceEqual(Hdf5Signature);

    private static bool Wanted(IReadOnlyCollection<string>? names, string name) =>
        names is null || names.Contains(name) || names.Any(n => n.StartsWith(name + ".", StringComparison.Ordinal)
                                                             || n.StartsWith(name + "/", StringComparison.Ordinal));

    /// <summary>C order over <paramref name="shape"/> from column-major (Fortran) order over it.</summary>
    private static double[] ColumnMajorToC(double[] src, int[] shape)
    {
        if (shape.Length < 2) return src;
        var dst = new double[src.Length];
        int rank = shape.Length;
        var idx = new int[rank];
        for (int c = 0; c < src.Length; c++)
        {
            // c walks C order; compute its column-major offset.
            int rem = c;
            for (int d = rank - 1; d >= 0; d--) { idx[d] = rem % shape[d]; rem /= shape[d]; }
            int f = 0, stride = 1;
            for (int d = 0; d < rank; d++) { f += idx[d] * stride; stride *= shape[d]; }
            dst[c] = src[f];
        }
        return dst;
    }

    // ── MAT v5 ─────────────────────────────────────────────────────────────────

    private const int MiInt8 = 1, MiUInt8 = 2, MiInt16 = 3, MiUInt16 = 4, MiInt32 = 5, MiUInt32 = 6,
                      MiSingle = 7, MiDouble = 9, MiInt64 = 12, MiUInt64 = 13, MiMatrix = 14, MiCompressed = 15;

    private const int ClassCell = 1, ClassStruct = 2, ClassObject = 3, ClassChar = 4, ClassSparse = 5,
                      ClassDouble = 6, ClassUInt64 = 15;

    private static IReadOnlyDictionary<string, MatArray> ReadV5(byte[] bytes, IReadOnlyCollection<string>? names, List<(string, string)>? skipped = null)
    {
        if (bytes.Length < 128) throw new MatFormatException("The MAT-file header is truncated.");
        bool big = bytes[126] == (byte)'M' && bytes[127] == (byte)'I';
        if (!big && !(bytes[126] == (byte)'I' && bytes[127] == (byte)'M'))
            throw new MatFormatException("The MAT-file header has no endian indicator; it is not a version 5 MAT-file.");

        var result = new SortedDictionary<string, MatArray>(StringComparer.Ordinal);
        int pos = 128;
        while (pos + 8 <= bytes.Length)
        {
            uint type = U32(bytes, pos, big), size = U32(bytes, pos + 4, big);
            int body = pos + 8;
            if (body + (long)size > bytes.Length) throw new MatFormatException($"A data element at byte {pos} runs past the end of the file.");

            if (type == MiCompressed)
            {
                ReadCompressed(bytes.AsSpan(body, (int)size), big, names, result, skipped);
                pos = body + (int)size;          // a compressed element is not padded
            }
            else
            {
                if (type == MiMatrix) ReadMatrix(bytes.AsSpan(body, (int)size), big, "", names, result, skipped);
                pos = body + (((int)size + 7) & ~7);     // padding is relative: a compressed element before this one need not end aligned
            }
        }
        return result;
    }

    private static uint U32(ReadOnlySpan<byte> d, int at, bool big) =>
        big ? BinaryPrimitives.ReadUInt32BigEndian(d[at..]) : BinaryPrimitives.ReadUInt32LittleEndian(d[at..]);

    private static void ReadCompressed(ReadOnlySpan<byte> z, bool big, IReadOnlyCollection<string>? names,
                                       SortedDictionary<string, MatArray> result, List<(string, string)>? skipped)
    {
        using var ms = new MemoryStream(z.ToArray());
        using var zs = new ZLibStream(ms, CompressionMode.Decompress);

        // The tag and the matrix's first three sub-elements (flags, dimensions, name) come first, so
        // an unrequested variable is known by its name before the rest is inflated.
        byte[] tag = new byte[8];
        if (zs.ReadAtLeast(tag, 8, throwOnEndOfStream: false) < 8) throw new MatFormatException("A compressed element is empty.");
        uint type = big ? BinaryPrimitives.ReadUInt32BigEndian(tag) : BinaryPrimitives.ReadUInt32LittleEndian(tag);
        uint size = big ? BinaryPrimitives.ReadUInt32BigEndian(tag.AsSpan(4)) : BinaryPrimitives.ReadUInt32LittleEndian(tag.AsSpan(4));
        if (type != MiMatrix) return;

        byte[] payload = new byte[size];
        int peek = (int)Math.Min(size, 256u);
        int got = zs.ReadAtLeast(payload.AsSpan(0, peek), peek, throwOnEndOfStream: false);
        if (got < peek) throw new MatFormatException("A compressed element is truncated.");
        if (names is not null && TryPeekName(payload.AsSpan(0, peek), big) is { } name && !Wanted(names, name)) return;

        if (size > (uint)peek)
        {
            int rest = zs.ReadAtLeast(payload.AsSpan(peek), (int)size - peek, throwOnEndOfStream: false);
            if (rest < size - peek) throw new MatFormatException("A compressed element is truncated.");
        }
        ReadMatrix(payload, big, "", names, result, skipped);
    }

    private static string? TryPeekName(ReadOnlySpan<byte> m, bool big)
    {
        try
        {
            int p = 0;
            for (int k = 0; k < 2; k++) p = SubElement(m, p, big).Next;
            var (_, start, length, _) = SubElement(m, p, big);
            return Encoding.ASCII.GetString(m.Slice(start, length));
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or MatFormatException) { return null; }
    }

    /// <summary>One sub-element at <paramref name="p"/>: its type, where its data lies, and where the next begins.</summary>
    private static (uint Type, int Start, int Length, int Next) SubElement(ReadOnlySpan<byte> m, int p, bool big)
    {
        if (p + 8 > m.Length) throw new MatFormatException("A matrix element is truncated.");
        uint first = big ? BinaryPrimitives.ReadUInt32BigEndian(m[p..]) : BinaryPrimitives.ReadUInt32LittleEndian(m[p..]);
        if ((first >> 16) != 0)
        {
            // Small data element: the size in the upper half, the type in the lower, data in the next four bytes.
            return (first & 0xFFFF, p + 4, (int)(first >> 16), p + 8);
        }
        uint size = big ? BinaryPrimitives.ReadUInt32BigEndian(m[(p + 4)..]) : BinaryPrimitives.ReadUInt32LittleEndian(m[(p + 4)..]);
        if (p + 8 + (long)size > m.Length) throw new MatFormatException("A matrix sub-element runs past its matrix.");
        return (first, p + 8, (int)size, (p + 8 + (int)size + 7) & ~7);
    }

    private static void ReadMatrix(ReadOnlySpan<byte> m, bool big, string prefix, IReadOnlyCollection<string>? names,
                                   SortedDictionary<string, MatArray> result, List<(string, string)>? skipped, string? nameOverride = null)
    {
        if (m.Length == 0) return;      // an empty struct field
        int p = 0;
        var e = SubElement(m, p, big); p = e.Next;
        uint flags = U32(m, e.Start, big);
        int cls = (int)(flags & 0xFF);
        bool complex = (flags & 0x0800) != 0;

        e = SubElement(m, p, big); p = e.Next;
        var dimData = m.Slice(e.Start, e.Length);
        if (e.Type != MiInt32) throw new MatFormatException("A matrix's dimensions are not 32-bit integers.");
        int[] shape = new int[dimData.Length / 4];
        for (int i = 0; i < shape.Length; i++)
            shape[i] = big ? BinaryPrimitives.ReadInt32BigEndian(dimData[(4 * i)..]) : BinaryPrimitives.ReadInt32LittleEndian(dimData[(4 * i)..]);

        e = SubElement(m, p, big); p = e.Next;
        string own = nameOverride ?? Encoding.ASCII.GetString(m.Slice(e.Start, e.Length));
        string name = prefix.Length == 0 ? own : $"{prefix}.{own}";

        if (!Wanted(names, name)) return;

        switch (cls)
        {
            case ClassCell:   Skip(skipped, name, "is a cell array; only numeric arrays and structs of them are read."); return;
            case ClassObject: Skip(skipped, name, "is an object; only numeric arrays and structs of them are read."); return;
            case ClassSparse: Skip(skipped, name, "is a sparse matrix; only full numeric arrays are read."); return;
            case ClassChar:
                if (names is not null && names.Contains(name))
                    throw new MatFormatException($"'{name}' is a character array, not a numeric one.", name);
                return;
            case ClassStruct:
                int count = shape.Aggregate(1, (a, d) => a * d);
                if (count != 1) { Skip(skipped, name, $"is a struct array of {count} elements; only a 1 × 1 struct is read."); return; }
                e = SubElement(m, p, big); p = e.Next;
                int len = (int)U32(m, e.Start, big);
                e = SubElement(m, p, big); p = e.Next;
                var fieldData = m.Slice(e.Start, e.Length);
                int nFields = len == 0 ? 0 : fieldData.Length / len;
                for (int f = 0; f < nFields; f++)
                {
                    string field = Encoding.ASCII.GetString(fieldData.Slice(f * len, len)).TrimEnd('\0');
                    var fe = SubElement(m, p, big); p = fe.Next;
                    if (fe.Type == MiMatrix) ReadMatrix(m.Slice(fe.Start, fe.Length), big, name, names, result, skipped, field);
                }
                return;
        }
        if (cls < ClassDouble || cls > ClassUInt64) return;      // a class this reader has no use for (a function handle's opaque, say)

        e = SubElement(m, p, big); p = e.Next;
        double[] real = ColumnMajorToC(Numbers(e.Type, m.Slice(e.Start, e.Length), big, name), shape);
        double[]? imag = null;
        if (complex)
        {
            e = SubElement(m, p, big);
            imag = ColumnMajorToC(Numbers(e.Type, m.Slice(e.Start, e.Length), big, name), shape);
        }
        if (real.Length != shape.Aggregate(1, (a, d) => a * d))
            throw new MatFormatException($"'{name}' holds {real.Length} values for its shape ({string.Join(" × ", shape)}).", name);
        result[name] = new MatArray(name, shape, real, imag);
    }

    private static double[] Numbers(uint type, ReadOnlySpan<byte> d, bool big, string name)
    {
        int width = type switch
        {
            MiInt8 or MiUInt8 => 1, MiInt16 or MiUInt16 => 2, MiInt32 or MiUInt32 or MiSingle => 4,
            MiDouble or MiInt64 or MiUInt64 => 8,
            _ => throw new MatFormatException($"'{name}' stores its data as type {type}, which is not a numeric type.", name),
        };
        var v = new double[d.Length / width];
        for (int i = 0; i < v.Length; i++)
        {
            var s = d.Slice(i * width, width);
            v[i] = type switch
            {
                MiInt8   => (sbyte)s[0],
                MiUInt8  => s[0],
                MiInt16  => big ? BinaryPrimitives.ReadInt16BigEndian(s) : BinaryPrimitives.ReadInt16LittleEndian(s),
                MiUInt16 => big ? BinaryPrimitives.ReadUInt16BigEndian(s) : BinaryPrimitives.ReadUInt16LittleEndian(s),
                MiInt32  => big ? BinaryPrimitives.ReadInt32BigEndian(s) : BinaryPrimitives.ReadInt32LittleEndian(s),
                MiUInt32 => big ? BinaryPrimitives.ReadUInt32BigEndian(s) : BinaryPrimitives.ReadUInt32LittleEndian(s),
                MiSingle => big ? BinaryPrimitives.ReadSingleBigEndian(s) : BinaryPrimitives.ReadSingleLittleEndian(s),
                MiDouble => big ? BinaryPrimitives.ReadDoubleBigEndian(s) : BinaryPrimitives.ReadDoubleLittleEndian(s),
                MiInt64  => big ? BinaryPrimitives.ReadInt64BigEndian(s) : BinaryPrimitives.ReadInt64LittleEndian(s),
                _        => big ? BinaryPrimitives.ReadUInt64BigEndian(s) : BinaryPrimitives.ReadUInt64LittleEndian(s),
            };
        }
        return v;
    }

    // ── MAT v7.3 (HDF5) ────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct ComplexDouble { public double real; public double imag; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ComplexSingle { public float real; public float imag; }

    private static IReadOnlyDictionary<string, MatArray> ReadHdf5(string path, IReadOnlyCollection<string>? names, List<(string, string)>? skipped = null)
    {
        var result = new SortedDictionary<string, MatArray>(StringComparer.Ordinal);
        using var file = H5File.OpenRead(path);
        Walk(file, "", names, result, structPath: false, skipped);
        return result;
    }

    private static string? MatlabClass(IH5Object o)
    {
        if (!o.AttributeExists("MATLAB_class")) return null;
        try { return o.Attribute("MATLAB_class").Read<string>(); }
        catch (Exception) { return null; }
    }

    private static void Walk(IH5Group group, string prefix, IReadOnlyCollection<string>? names,
                             SortedDictionary<string, MatArray> result, bool structPath, List<(string, string)>? skipped = null)
    {
        foreach (var child in group.Children())
        {
            if (child.Name.StartsWith('#')) continue;        // #refs#, #subsystem#: MATLAB's own bookkeeping
            string sep = structPath ? "." : "/";
            string name = prefix.Length == 0 ? child.Name : prefix + sep + child.Name;
            if (!Wanted(names, name)) continue;
            string? cls = MatlabClass(child);

            if (child is IH5Group g)
            {
                if (cls is not null && cls != "struct")
                {
                    Skip(skipped, name, $"is an object of class {cls}; only numeric arrays and structs of them are read.");
                    continue;
                }
                Walk(g, name, names, result, structPath: cls == "struct", skipped);
                continue;
            }
            if (child is not IH5Dataset ds) continue;

            if (cls == "cell" && Skip(skipped, name, "is a cell array; only numeric arrays and structs of them are read.")) continue;
            if (cls is "char" or "logical" && !(names?.Contains(name) ?? false)) continue;
            if (cls == "char")
                throw new MatFormatException($"'{name}' is a character array, not a numeric one.", name);
            if (ds.AttributeExists("MATLAB_sparse") && Skip(skipped, name, "is a sparse matrix; only full numeric arrays are read.")) continue;
            if (ds.AttributeExists("MATLAB_empty")) continue;

            var t = ds.Type;
            if (t.Class == H5DataTypeClass.Reference
                && Skip(skipped, name, "holds object references (a cell array or an object); only numeric arrays are read.")) continue;
            if (t.Class is not (H5DataTypeClass.FloatingPoint or H5DataTypeClass.FixedPoint or H5DataTypeClass.Compound))
            {
                if (names?.Contains(name) ?? false)
                    throw new MatFormatException($"'{name}' is not a numeric array.", name);
                continue;
            }

            ulong[] dims = ds.Space.Dimensions;
            bool matlab = cls is not null;
            int[] shape = dims.Select(d => checked((int)d)).ToArray();
            if (matlab) Array.Reverse(shape);

            double[] re; double[]? im = null;
            if (t.Class == H5DataTypeClass.Compound)
            {
                var members = t.Compound.Members;
                if ((members.Length != 2 || members[0].Name != "real" || members[1].Name != "imag")
                    && Skip(skipped, name, "is a compound type that is not a complex number {real, imag}.")) continue;
                if (members[0].Type.Size == 8)
                {
                    var c = ds.Read<ComplexDouble[]>();
                    re = Array.ConvertAll(c, x => x.real); im = Array.ConvertAll(c, x => x.imag);
                }
                else
                {
                    var c = ds.Read<ComplexSingle[]>();
                    re = Array.ConvertAll(c, x => (double)x.real); im = Array.ConvertAll(c, x => (double)x.imag);
                }
            }
            else re = ReadReal(ds, t);

            if (matlab)
            {
                re = ColumnMajorToC(re, shape);
                if (im is not null) im = ColumnMajorToC(im, shape);
            }
            result[name] = new MatArray(name, shape, re, im);
        }
    }

    private static double[] ReadReal(IH5Dataset ds, IH5DataType t) => (t.Class, t.Size) switch
    {
        (H5DataTypeClass.FloatingPoint, 8) => ds.Read<double[]>(),
        (H5DataTypeClass.FloatingPoint, 4) => Array.ConvertAll(ds.Read<float[]>(), x => (double)x),
        (H5DataTypeClass.FixedPoint, 1) => t.FixedPoint.IsSigned ? Array.ConvertAll(ds.Read<sbyte[]>(), x => (double)x) : Array.ConvertAll(ds.Read<byte[]>(), x => (double)x),
        (H5DataTypeClass.FixedPoint, 2) => t.FixedPoint.IsSigned ? Array.ConvertAll(ds.Read<short[]>(), x => (double)x) : Array.ConvertAll(ds.Read<ushort[]>(), x => (double)x),
        (H5DataTypeClass.FixedPoint, 4) => t.FixedPoint.IsSigned ? Array.ConvertAll(ds.Read<int[]>(), x => (double)x) : Array.ConvertAll(ds.Read<uint[]>(), x => (double)x),
        (H5DataTypeClass.FixedPoint, 8) => t.FixedPoint.IsSigned ? Array.ConvertAll(ds.Read<long[]>(), x => (double)x) : Array.ConvertAll(ds.Read<ulong[]>(), x => (double)x),
        _ => throw new MatFormatException($"A dataset of {t.Class} size {t.Size} is not a number type this reader knows."),
    };
}

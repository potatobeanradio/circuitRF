// ================================================================
//  BinaryDatasetReader.cs  —  reads a binary simulation dataset (.ds)
//
//  A paged binary container holding one or more BLOCKS. Each block is a nested sweep: one or more independent
//  variables, outermost first, and the dependent variables measured at every point of the sweep. Every field this
//  reader relies on is named below, and anything else in the file is ignored. All integers and doubles are
//  little-endian. BinaryDatasetWriter writes the same container.
//
//    Header     512 bytes. Bytes 13-15 are CR LF 0x1A, the u32 at 0x10 is 0xCEFAEDFE and bytes 0x20-0x23 are
//               "DCBA". The u32 at 0x24 is the container version (3 and 4 are read), at 0x30 the page size, at
//               0x50 the page count. Page n starts at 512 + n × page size.
//    Pages      An object's LINK page is page <object id>: "LINK", its link id (i32 at 4), and from 0x2C the i64
//               start pages of its storage, ended by −1. Page 0 is the LINK page of the directory.
//    Directory  "DIR*" pages, each holding from 0x14 the triples (i64 object id, i64 byte size, i64 pages per
//               start page) — version 3 stores one page per start page, version 4 a run of eight.
//    Keys       The object with link id 1 holds NUL-separated key strings. Key n (from 1) names the object whose
//               link id is n + 1: the string pool, the block index, and per block "DN=", "DNS_I=", "DNS_D=" and
//               "DNS_Y=" keys followed by a level count and a comma-separated position, then one "…_top_<b>" key
//               for the block's description.
//    Strings    The pool: u64 at 4 is the offset of its index, i64 at 20 the string count. The index holds 25-byte
//               entries (u8 1, i64 offset, i64 length with its NUL, i64 capacity) for string ids 1, 2, ….
//    Blocks     The block index (i32 2003 first): i32 [2] is the block count, then from i32 [7] one entry per 4
//               ints — the block name's string id, and at +2 the block number.
//    Variables  A block's description: i32 version 4 (12-int header, entries of 10 ints) or 5 (14-int header,
//               entries of 19 ints); two groups (marker, count, 0) — independents, then dependents. An entry's first
//               two ints are its type (400 real, 500 complex, 600 text) and its name's string id; a version-5 entry
//               names a quantity string at int 17 ("voltage type=real indep=no").
//    Values     24-byte records: f64 real, f64 imaginary, 8 bytes not read. A text value's real part holds the
//               string id in its bits. DNS_I at level l holds level l's independent values once per outer point;
//               DNS_D holds the dependents interleaved, point by point, innermost fastest.
//    Counts     Sweep records (i32 30195 first) carry at byte 36 the i64 count − 1 of the level below them: a DN
//               key with a d-long position gives level d's count, every DNS_Y record the innermost one.
//
//  Only a full grid is read — every outer point sweeping the same inner values. A sweep whose inner counts or values
//  change from one outer point to the next is refused, naming the variable, rather than reshaped into something the
//  writer never meant.
// ================================================================

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using RfCore.Data;

namespace RfCore.Export;

/// <summary>What a variable of a binary dataset holds.</summary>
public enum BinaryValueKind { Real, Complex, Text }

/// <summary>
/// One variable of a block. An independent variable holds its axis values; a dependent one holds a value at every
/// point of the block's grid, in C order (innermost axis fastest). <see cref="Quantity"/> is the writer's own word for
/// it ("voltage", "current", "s-param", …), empty when the file names none.
/// </summary>
public sealed record BinaryDatasetVariable(string Name, string Quantity, BinaryValueKind Kind, double[] Real, double[]? Imag, string[]? Text)
{
    public int Count => Text?.Length ?? Real.Length;

    /// <summary>The SI unit <see cref="Quantity"/> implies, or empty.</summary>
    public string Unit => Quantity switch
    {
        "voltage" => "V",
        "current" => "A",
        "frequency" => "Hz",
        "port-impedance" => "Ohm",
        _ => "",
    };
}

/// <summary>
/// One block: its name as the file spells it, the short <see cref="Group"/> it is read as (the analysis name, e.g.
/// <c>SP1</c> of <c>SweepVds.SP1.SP</c>), its independent variables outermost first, and its dependent variables.
/// </summary>
public sealed record BinaryDatasetBlock(
    string Name, string Group, IReadOnlyList<BinaryDatasetVariable> Axes, IReadOnlyList<BinaryDatasetVariable> Values)
{
    public int[] Shape => [.. Axes.Select(a => a.Count)];
}

/// <summary>Every block of one binary dataset.</summary>
public sealed class BinaryDataset(IReadOnlyList<BinaryDatasetBlock> blocks)
{
    public IReadOnlyList<BinaryDatasetBlock> Blocks { get; } = blocks;

    private static readonly Regex Element = new(@"^(?<base>.+)\[(?<i>\d+)(?:,(?<j>\d+))?\]$", RegexOptions.CultureInvariant);

    /// <summary>
    /// The numeric arrays, each named <c>Group.Variable</c>: every independent variable as its axis values, every
    /// dependent one over the block's grid. A family of elements <c>S[1,1]</c> … <c>S[n,n]</c> (or <c>X[1]</c> …
    /// <c>X[n]</c>) that is complete is read as one array <c>Group.S</c> with port axes i, j (or port) last; text
    /// variables are named in <paramref name="skipped"/> with the reason. <paramref name="withAxes"/> false leaves the
    /// independent variables out (they are already every array's axes).
    /// </summary>
    public IReadOnlyList<(string Name, string Unit, Axis[] Axes, double[] Real, double[]? Imag)> Arrays(
        List<(string Name, string Why)>? skipped = null, bool withAxes = true)
    {
        var result = new List<(string, string, Axis[], double[], double[]?)>();
        foreach (var b in Blocks)
        {
            var axes = b.Axes.Select(a => new Axis(a.Name, a.Real, a.Unit)).ToArray();
            if (withAxes)
                foreach (var a in b.Axes)
                    result.Add(($"{b.Group}.{a.Name}", a.Unit, [new Axis(a.Name, a.Real, a.Unit)], a.Real, a.Imag));

            var families = new Dictionary<string, List<(BinaryDatasetVariable V, int I, int J)>>(StringComparer.Ordinal);
            foreach (var v in b.Values)
            {
                if (v.Kind == BinaryValueKind.Text) { skipped?.Add(($"{b.Group}.{v.Name}", "holds text, not numbers.")); continue; }
                var m = Element.Match(v.Name);
                if (!m.Success) { result.Add(($"{b.Group}.{v.Name}", v.Unit, axes, v.Real, v.Imag)); continue; }
                string key = m.Groups["base"].Value + (m.Groups["j"].Success ? "[,]" : "[]");
                if (!families.TryGetValue(key, out var list)) families[key] = list = [];
                list.Add((v, int.Parse(m.Groups["i"].Value, CultureInfo.InvariantCulture),
                    m.Groups["j"].Success ? int.Parse(m.Groups["j"].Value, CultureInfo.InvariantCulture) : 0));
            }
            foreach (var (key, list) in families)
            {
                bool matrix = key.EndsWith("[,]", StringComparison.Ordinal);
                string name = key[..^(matrix ? 3 : 2)];
                int n = list.Max(e => Math.Max(e.I, e.J));
                bool complete = matrix
                    ? list.Count == n * n && list.Select(e => (e.I, e.J)).Distinct().Count() == n * n && list.All(e => e.I >= 1 && e.J >= 1)
                    : list.Count == n && list.Select(e => e.I).Distinct().Count() == n && list.All(e => e.I >= 1);
                if (!complete || list.Select(e => e.V.Kind).Distinct().Count() != 1)
                {
                    foreach (var e in list) result.Add(($"{b.Group}.{e.V.Name}", e.V.Unit, axes, e.V.Real, e.V.Imag));
                    continue;
                }
                double[] ports = [.. Enumerable.Range(1, n).Select(p => (double)p)];
                int points = list[0].V.Real.Length, per = matrix ? n * n : n;
                bool complex = list[0].V.Kind == BinaryValueKind.Complex;
                var re = new double[points * per];
                var im = complex ? new double[points * per] : null;
                foreach (var (v, i, j) in list)
                {
                    int slot = matrix ? (i - 1) * n + (j - 1) : i - 1;
                    for (int p = 0; p < points; p++)
                    {
                        re[p * per + slot] = v.Real[p];
                        if (im is not null) im[p * per + slot] = v.Imag![p];
                    }
                }
                Axis[] full = matrix
                    ? [.. axes, new Axis("i", ports, "port"), new Axis("j", ports, "port")]
                    : [.. axes, new Axis("port", ports, "port")];
                result.Add(($"{b.Group}.{name}", list[0].V.Unit, full, re, im));
            }
        }
        return result;
    }

    /// <summary>The dependent arrays as a <see cref="DataSet"/>: one group per block, each a cube on its axes. Text
    /// variables, which are not cubes, are named in <paramref name="skipped"/>.</summary>
    public DataSet ToDataSet(List<(string Name, string Why)>? skipped = null)
    {
        var ds = new DataSet();
        foreach (var (name, unit, axes, re, im) in Arrays(skipped, withAxes: false))
        {
            int dot = name.IndexOf('.');
            string group = name[..dot], cube = name[(dot + 1)..];
            var c = im is null
                ? new DataCube(axes, re)
                : new DataCube(axes, [.. re.Select((r, k) => new Complex(r, im[k]))]);
            c.Unit = unit;
            ds.AddToGroup(group, cube, c);
        }
        return ds;
    }
}

public static class BinaryDatasetReader
{
    private const int HeaderSize = 512, RecordSize = 24, SweepMagic = 30195, IndexMagic = 2003;
    private const uint PoolMagic = 0x7A3C0E3B, SignatureWord = 0xCEFAEDFE;

    /// <summary>True when <paramref name="path"/> starts as a binary dataset does. Reads 0x28 bytes; never throws for a
    /// readable file.</summary>
    public static bool IsBinaryDataset(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var head = new byte[0x28];
            return fs.Read(head, 0, head.Length) == head.Length && Signed(head);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static bool Signed(ReadOnlySpan<byte> b) =>
        b.Length >= 0x28 && b[13] == 0x0D && b[14] == 0x0A && b[15] == 0x1A
        && BinaryPrimitives.ReadUInt32LittleEndian(b[0x10..]) == SignatureWord
        && b[0x20] == (byte)'D' && b[0x21] == (byte)'C' && b[0x22] == (byte)'B' && b[0x23] == (byte)'A';

    /// <summary>Every block of the binary dataset at <paramref name="path"/>.</summary>
    /// <exception cref="InvalidDataException">The file is not a binary dataset, or holds something this reader does not
    /// read; the message is a sentence saying which.</exception>
    public static BinaryDataset Read(string path) => Read(File.ReadAllBytes(path), Path.GetFileName(path));

    /// <summary>The numeric arrays of <paramref name="path"/> by name (see <see cref="BinaryDataset.Arrays"/>);
    /// <paramref name="names"/> limits what is returned.</summary>
    public static IReadOnlyDictionary<string, MatArray> ReadArrays(string path, IReadOnlyCollection<string>? names = null)
    {
        var result = new Dictionary<string, MatArray>(StringComparer.Ordinal);
        foreach (var (name, _, axes, re, im) in Read(path).Arrays())
            if (names is null || names.Contains(name))
                result[name] = new MatArray(name, [.. axes.Select(a => a.Length)], re, im);
        return result;
    }

    /// <summary>Every numeric array, and every variable that is not read with the reason.</summary>
    public static (IReadOnlyDictionary<string, MatArray> Arrays, IReadOnlyList<(string Name, string Why)> Skipped) List(string path)
    {
        var skipped = new List<(string, string)>();
        var result = new Dictionary<string, MatArray>(StringComparer.Ordinal);
        foreach (var (name, _, axes, re, im) in Read(path).Arrays(skipped))
            result[name] = new MatArray(name, [.. axes.Select(a => a.Length)], re, im);
        return (result, skipped);
    }

    /// <summary>The file as a <see cref="DataSet"/>, one group per block.</summary>
    public static DataSet ReadDataSet(string path) => Read(path).ToDataSet();

    public static BinaryDataset Read(byte[] file, string fileName)
    {
        if (file.Length < HeaderSize || !Signed(file)) throw Bad(fileName, "is not a binary dataset");
        uint version = U32(file, 0x24);
        if (version is not (3 or 4)) throw Bad(fileName, $"is a binary dataset of container version {version}, which is not read (3 and 4 are)");
        int pageSize = checked((int)U32(file, 0x30));
        if (pageSize < 64 || (pageSize & (pageSize - 1)) != 0) throw Bad(fileName, $"states a page size of {pageSize} bytes");

        var c = new Container(file, fileName, pageSize);
        var objects = c.Objects();

        // Link id → object; key n (from 1) names the object whose link id is n + 1.
        var byLink = new Dictionary<int, byte[]>();
        foreach (var (id, blob) in objects) byLink[c.LinkId(id)] = blob;
        if (!byLink.TryGetValue(1, out var keyText)) throw Bad(fileName, "holds no key table");
        string[] keys = Encoding.ASCII.GetString(keyText).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var byKey = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        for (int n = 1; n <= keys.Length; n++)
            if (byLink.TryGetValue(n + 1, out var blob)) byKey[keys[n - 1]] = blob;
            else throw Bad(fileName, $"names an object for '{keys[n - 1]}' that it does not hold");

        if (!byKey.TryGetValue("STRINGDATA", out var poolBlob)) throw Bad(fileName, "holds no string pool");
        string[] strings = Pool(poolBlob, fileName);
        string Str(long id) => id >= 1 && id < strings.Length ? strings[id] : throw Bad(fileName, $"refers to string {id}, which its pool does not hold");

        if (!byKey.TryGetValue("DATAMANAGER_INDEX", out var indexBlob)) throw Bad(fileName, "holds no block index");
        var blockNames = BlockIndex(indexBlob, fileName).ToDictionary(e => e.Block, e => Str(e.NameId));

        var parsed = new Dictionary<int, List<(string Role, int[] Pos, byte[] Blob)>>();
        var tops = new Dictionary<int, byte[]>();
        foreach (var (key, blob) in byKey)
        {
            var m = Regex.Match(key, @"^(DN|DNS_I|DNS_D|DNS_Y)=(\d+)=([\d,]+)$", RegexOptions.CultureInvariant);
            if (m.Success)
            {
                int[] pos = [.. m.Groups[3].Value.Split(',').Select(s => int.Parse(s, CultureInfo.InvariantCulture))];
                if (!parsed.TryGetValue(pos[0], out var list)) parsed[pos[0]] = list = [];
                list.Add((m.Groups[1].Value, pos, blob));
                continue;
            }
            var t = Regex.Match(key, @"_top_(\d+)$", RegexOptions.CultureInvariant);
            if (t.Success) tops[int.Parse(t.Groups[1].Value, CultureInfo.InvariantCulture)] = blob;
        }

        var blocks = new List<BinaryDatasetBlock>();
        foreach (var (b, top) in tops.OrderBy(p => p.Key))
        {
            string name = blockNames.TryGetValue(b, out var bn) ? bn : $"block{b}";
            blocks.Add(Block(name, top, parsed.GetValueOrDefault(b) ?? [], Str, fileName));
        }
        return new BinaryDataset(WithGroups(blocks));
    }

    // ── container ────────────────────────────────────────────────────────────

    private sealed class Container(byte[] file, string fileName, int pageSize)
    {
        private ReadOnlySpan<byte> Page(long n)
        {
            long at = HeaderSize + n * pageSize;
            if (n < 0 || at + pageSize > file.Length) throw Bad(fileName, $"refers to page {n}, past its end");
            return file.AsSpan((int)at, pageSize);
        }

        private List<long> Starts(long linkPage)
        {
            var p = Page(linkPage);
            if (!p[..4].SequenceEqual("LINK"u8)) throw Bad(fileName, $"has no page list at page {linkPage}");
            var starts = new List<long>();
            for (int o = 0x2C; o + 8 <= p.Length; o += 8)
            {
                long v = BinaryPrimitives.ReadInt64LittleEndian(p[o..]);
                if (v == -1) break;
                starts.Add(v);
            }
            return starts;
        }

        public int LinkId(long objectId) => BinaryPrimitives.ReadInt32LittleEndian(Page(objectId)[4..]);

        public List<(long Id, byte[] Blob)> Objects()
        {
            var entries = new List<(long Id, long Size, long Run)>();
            foreach (long dirPage in Starts(0))
            {
                var d = Page(dirPage);
                if (!d[..4].SequenceEqual("DIR*"u8)) throw Bad(fileName, $"has no directory at page {dirPage}");
                for (int o = 0x14; o + 24 <= d.Length; o += 24)
                {
                    long id = BinaryPrimitives.ReadInt64LittleEndian(d[o..]);
                    if (id <= 0) break;
                    entries.Add((id, BinaryPrimitives.ReadInt64LittleEndian(d[(o + 8)..]), BinaryPrimitives.ReadInt64LittleEndian(d[(o + 16)..])));
                }
            }
            var result = new List<(long, byte[])>(entries.Count);
            foreach (var (id, size, run) in entries.OrderBy(e => e.Id))
            {
                if (size < 0 || run < 1) throw Bad(fileName, $"describes object {id} as {size} bytes in runs of {run} pages");
                var blob = new byte[size];
                long have = 0;
                foreach (long start in Starts(id))
                    for (long k = 0; k < run && have < size; k++)
                    {
                        var p = Page(start + k);
                        int take = (int)Math.Min(p.Length, size - have);
                        p[..take].CopyTo(blob.AsSpan((int)have));
                        have += take;
                    }
                if (have < size) throw Bad(fileName, $"stores {have} of object {id}'s {size} bytes");
                result.Add((id, blob));
            }
            return result;
        }
    }

    private static string[] Pool(byte[] blob, string fileName)
    {
        if (blob.Length < 28 || U32(blob, 0) != PoolMagic) throw Bad(fileName, "has a string pool this reader does not recognise");
        long index = BinaryPrimitives.ReadInt64LittleEndian(blob.AsSpan(4)), count = BinaryPrimitives.ReadInt64LittleEndian(blob.AsSpan(20));
        var strings = new List<string> { "" };
        for (long o = index + 8; strings.Count < count && o + 25 <= blob.Length; o += 25)
        {
            if (blob[o] != 1) break;
            long off = BinaryPrimitives.ReadInt64LittleEndian(blob.AsSpan((int)o + 1)), len = BinaryPrimitives.ReadInt64LittleEndian(blob.AsSpan((int)o + 9));
            if (off < 0 || len < 1 || off + len > blob.Length) throw Bad(fileName, $"has a string-pool entry outside its pool (string {strings.Count})");
            strings.Add(Encoding.UTF8.GetString(blob, (int)off, (int)len - 1));
        }
        return [.. strings];
    }

    private static List<(int Block, long NameId)> BlockIndex(byte[] blob, string fileName)
    {
        int[] iv = Ints(blob);
        if (iv.Length < 3 || iv[0] != IndexMagic) throw Bad(fileName, "has a block index this reader does not recognise");
        var result = new List<(int, long)>();
        for (int k = 0; k < iv[2]; k++)
        {
            int at = 7 + 4 * k;
            if (at + 2 >= iv.Length) throw Bad(fileName, "has a block index shorter than its block count");
            result.Add((iv[at + 2], iv[at]));
        }
        return result;
    }

    // ── one block ────────────────────────────────────────────────────────────

    private sealed record Entry(int Type, long NameId, long QuantityId);

    private static (List<Entry> Indep, List<Entry> Dep) Description(byte[] blob, string fileName, string block)
    {
        int[] iv = Ints(blob);
        int version = iv.Length > 0 ? iv[0] : 0;
        (int header, int length, int marker) = version switch
        {
            4 => (12, 10, 2),
            5 => (14, 19, 3),
            _ => throw Bad(fileName, $"describes block '{block}' in a layout ({version}) this reader does not read"),
        };
        var groups = new List<List<Entry>>();
        int i = header;
        while (groups.Count < 2 && i + 3 <= iv.Length)
        {
            if (iv[i] != marker || iv[i + 2] != 0) throw Bad(fileName, $"describes block '{block}' in a layout this reader does not read");
            int count = iv[i + 1];
            var list = new List<Entry>(count);
            i += 3;
            for (int e = 0; e < count; e++, i += length)
            {
                if (i + length > iv.Length) throw Bad(fileName, $"describes block '{block}' with fewer variables than it counts");
                list.Add(new Entry(iv[i], iv[i + 1], length == 19 ? iv[i + 17] : 0));
            }
            groups.Add(list);
        }
        if (groups.Count == 0 || groups[0].Count == 0) throw Bad(fileName, $"describes block '{block}' with no independent variable");
        return (groups[0], groups.Count > 1 ? groups[1] : []);
    }

    private static BinaryDatasetBlock Block(string name, byte[] top, List<(string Role, int[] Pos, byte[] Blob)> parts,
        Func<long, string> str, string fileName)
    {
        var (indep, dep) = Description(top, fileName, name);
        int levels = indep.Count;
        foreach (var e in indep.Concat(dep))
            if (e.Type is not (400 or 500 or 600))
                throw Bad(fileName, $"holds variable '{str(e.NameId)}' of block '{name}' in a type ({e.Type}) this reader does not read");

        // Each level's count, from the sweep records: every record at a level must agree.
        var counts = new HashSet<long>[levels];
        for (int l = 0; l < levels; l++) counts[l] = [];
        foreach (var (role, pos, blob) in parts.Where(p => p.Role is "DN" or "DNS_Y"))
        {
            int level = pos.Length;
            if (level < 1 || level > levels) continue;
            foreach (long c in SweepCounts(blob)) counts[level - 1].Add(c + 1);
        }
        var shape = new int[levels];
        for (int l = 0; l < levels; l++)
        {
            if (counts[l].Count != 1)
                throw Bad(fileName, counts[l].Count == 0
                    ? $"holds no sweep count for '{str(indep[l].NameId)}' in block '{name}'"
                    : $"sweeps '{str(indep[l].NameId)}' in block '{name}' over {string.Join(", ", counts[l].Order())} points from one outer point to the next — only a full grid is read");
            shape[l] = checked((int)counts[l].First());
        }

        static int Cmp(int[] a, int[] b)
        {
            for (int k = 0; k < Math.Min(a.Length, b.Length); k++) if (a[k] != b[k]) return a[k].CompareTo(b[k]);
            return a.Length.CompareTo(b.Length);
        }
        byte[] Join(IEnumerable<(string Role, int[] Pos, byte[] Blob)> xs)
        {
            var list = xs.ToList();
            list.Sort((a, b) => Cmp(a.Pos, b.Pos));
            var all = new byte[list.Sum(x => x.Blob.Length)];
            int at = 0;
            foreach (var x in list) { x.Blob.CopyTo(all, at); at += x.Blob.Length; }
            return all;
        }

        var axes = new List<BinaryDatasetVariable>(levels);
        long outer = 1;
        for (int l = 0; l < levels; l++)
        {
            byte[] raw = Join(parts.Where(p => p.Role == "DNS_I" && p.Pos.Length == l + 1));
            var all = Values(raw, indep[l], str, fileName, name);
            if (all.Kind == BinaryValueKind.Text)
                throw Bad(fileName, $"sweeps '{all.Name}' in block '{name}' over text, which this reader does not read as an axis");
            long expect = outer * shape[l];
            if (all.Count != expect)
                throw Bad(fileName, $"holds {all.Count} values of '{all.Name}' in block '{name}' where its sweep counts give {expect}");
            // A grid repeats the same axis under every outer point.
            for (long o = 1; o < outer; o++)
                for (int k = 0; k < shape[l]; k++)
                    if (all.Real[o * shape[l] + k] != all.Real[k] || (all.Imag is { } im && im[o * shape[l] + k] != im[k]))
                        throw Bad(fileName, $"sweeps '{all.Name}' in block '{name}' over different values from one outer point to the next — only a full grid is read");
            axes.Add(all with
            {
                Real = all.Real[..shape[l]],
                Imag = all.Imag?[..shape[l]],
                Text = all.Text?[..shape[l]],
            });
            outer = expect;
        }

        var values = new List<BinaryDatasetVariable>(dep.Count);
        if (dep.Count > 0)
        {
            byte[] raw = Join(parts.Where(p => p.Role == "DNS_D"));
            long points = outer;
            if (raw.Length != points * dep.Count * RecordSize)
                throw Bad(fileName, $"holds {raw.Length / RecordSize} values for the {dep.Count} variables of block '{name}' where its sweep counts give {points * dep.Count}");
            for (int c = 0; c < dep.Count; c++)
            {
                var column = new byte[points * RecordSize];
                for (long p = 0; p < points; p++)
                    Buffer.BlockCopy(raw, (int)((p * dep.Count + c) * RecordSize), column, (int)(p * RecordSize), RecordSize);
                values.Add(Values(column, dep[c], str, fileName, name));
            }
        }
        return new BinaryDatasetBlock(name, "", axes, values);
    }

    private static BinaryDatasetVariable Values(byte[] raw, Entry e, Func<long, string> str, string fileName, string block)
    {
        string name = str(e.NameId);
        if (raw.Length % RecordSize != 0) throw Bad(fileName, $"stores '{name}' of block '{block}' in a length that is not whole values");
        int n = raw.Length / RecordSize;
        var re = new double[n];
        double[]? im = e.Type == 500 ? new double[n] : null;
        string[]? text = e.Type == 600 ? new string[n] : null;
        for (int k = 0; k < n; k++)
        {
            var r = raw.AsSpan(k * RecordSize);
            if (text is not null) { text[k] = str(BinaryPrimitives.ReadInt64LittleEndian(r)); continue; }
            re[k] = BinaryPrimitives.ReadDoubleLittleEndian(r);
            if (im is not null) im[k] = BinaryPrimitives.ReadDoubleLittleEndian(r[8..]);
        }
        var kind = e.Type switch { 500 => BinaryValueKind.Complex, 600 => BinaryValueKind.Text, _ => BinaryValueKind.Real };
        return new BinaryDatasetVariable(name, e.QuantityId > 0 ? QuantityWord(str(e.QuantityId)) : "", kind, text is null ? re : [], im, text);
    }

    /// <summary>"voltage type=real indep=no" → "voltage"; "type=real indep=yes" → "".</summary>
    private static string QuantityWord(string flags)
    {
        string first = flags.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return first.Contains('=') || first == "none" ? "" : first;
    }

    private static IEnumerable<long> SweepCounts(byte[] blob)
    {
        for (int o = 0; o + 44 <= blob.Length; o += 4)
            if (BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(o)) == SweepMagic && BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(o + 4)) == 2)
                yield return BinaryPrimitives.ReadInt64LittleEndian(blob.AsSpan(o + 36));
    }

    /// <summary>
    /// Each block's group: its name without the writer's "…var_" prefix, then, for a dotted name such as
    /// <c>SweepVds.SP1.SP</c>, the analysis name before the last segment (<c>SP1</c>). Where that would name two
    /// blocks alike, the whole name, dots as underscores.
    /// </summary>
    private static List<BinaryDatasetBlock> WithGroups(List<BinaryDatasetBlock> blocks)
    {
        static string Stripped(string n) => Regex.Replace(n, @"^[A-Za-z]*var_", "", RegexOptions.CultureInvariant);
        static string Short(string n)
        {
            string[] seg = Stripped(n).Split('.');
            return seg.Length >= 2 ? seg[^2] : seg[0];
        }
        var shorts = blocks.Select(b => Short(b.Name)).ToList();
        return [.. blocks.Select((b, k) => b with
        {
            Group = shorts.Count(s => s == shorts[k]) == 1 && shorts[k].Length > 0 ? shorts[k] : Stripped(b.Name).Replace('.', '_'),
        })];
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static uint U32(byte[] b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at));

    private static int[] Ints(byte[] blob)
    {
        var iv = new int[blob.Length / 4];
        for (int k = 0; k < iv.Length; k++) iv[k] = BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(4 * k));
        return iv;
    }

    private static InvalidDataException Bad(string fileName, string what) => new($"{fileName} {what}.");
}

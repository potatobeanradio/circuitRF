// ================================================================
//  BinaryDatasetWriter.cs  —  writes a binary simulation dataset (.ds)
//
//  The other half of BinaryDatasetReader: it writes the container that reader's header describes, from a
//  BinaryDataset — every block a full grid, its independent variables outermost first and its dependent variables
//  one value per grid point. Two container versions:
//
//    V3   8 KB pages, each object stored one page per start page, block descriptions of layout 4 (no quantities).
//    V4   1 KB pages, values stored in runs of eight pages, block descriptions of layout 5, each variable naming its
//         quantity ("voltage type=real indep=no") — what the reader reports as Quantity and turns into a unit.
//
//  A block of three or more levels stores its innermost values split by the outermost index, one object per outer
//  value; anything less stores each level in one object. An object too large for one page list is stored in longer
//  runs of pages (the directory records each object's run). The first 13 bytes of the header hold "circuitRF" and
//  NULs. The writer and the reader are held together by round-trip tests.
// ================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using RfCore.Data;

namespace RfCore.Export;

/// <summary>The container version a binary dataset is written in (see <see cref="BinaryDatasetWriter"/>).</summary>
public enum BinaryDatasetVersion { V3 = 3, V4 = 4 }

public static class BinaryDatasetWriter
{
    private const int SweepMagic = 30195;

    /// <summary>
    /// A <see cref="DataSet"/> as blocks: one block per group and per set of sweep axes its cubes share (the first named
    /// after the group, the default group <c>data</c>; any further one <c>&lt;group&gt;_2</c>, …). A cube's trailing
    /// port axes <c>i</c>, <c>j</c> become elements <c>X[i,j]</c> and a trailing <c>port</c> axis elements <c>X[p]</c>
    /// — what <see cref="BinaryDataset.Arrays"/> assembles back into one cube. A cube with no sweep axis sits on a
    /// one-point <c>index</c> axis, except a <c>Z0 [port]</c> beside an <c>S</c>, which is written as
    /// <c>PortZ[p]</c> at every point of S's sweep. Cubes named <c>__…</c> (circuitRF's own bookkeeping) are not
    /// written; a labelled axis keeps its values and loses its labels.
    /// </summary>
    public static BinaryDataset FromDataSet(DataSet ds)
    {
        var blocks = new List<BinaryDatasetBlock>();
        foreach (string group in ds.Groups)
        {
            var sets = new List<(Axis[] Sweep, List<BinaryDatasetVariable> Values)>();
            var cubes = ds.CubesIn(group);
            // A Z0 [port] beside an S [sweep…, i, j] is written as PortZ[p] at every point of S's sweep, in S's block.
            bool z0AsPortZ = cubes.TryGetValue("S", out var sCube) && cubes.TryGetValue("Z0", out var z0Cube)
                && z0Cube.Rank == 1 && z0Cube.Axes[0].Name == "port"
                && sCube.Rank >= 3 && sCube.Axes[^1].Name == "j" && sCube.Axes[^2].Name == "i" && sCube.Axes[^1].Length == z0Cube.Axes[0].Length;
            foreach (var (name, cube) in cubes)
            {
                if (name.StartsWith("__", StringComparison.Ordinal) || (z0AsPortZ && name == "Z0")) continue;
                var axes = cube.Axes;
                int ports = axes.Count >= 2 && axes[^2].Name == "i" && axes[^1].Name == "j" && axes[^1].Length == axes[^2].Length ? 2
                          : axes.Count >= 1 && axes[^1].Name == "port" ? 1 : 0;
                Axis[] sweep = axes.Count - ports > 0 ? [.. axes.Take(axes.Count - ports)] : [new Axis("index", [0.0])];
                int at = sets.FindIndex(s => SameAxes(s.Sweep, sweep));
                if (at < 0) { sets.Add((sweep, [])); at = sets.Count - 1; }

                bool complex = cube.DataKind == DataKind.Complex;
                Complex[] values = complex ? cube.ComplexValues : [.. cube.RealValues.Select(r => new Complex(r, 0))];
                string quantity = QuantityOf(name, cube.Unit);
                if (ports == 0) { sets[at].Values.Add(Variable(name, quantity, complex, values, 0, 1)); continue; }
                int n = axes[^1].Length, per = ports == 2 ? n * n : n;
                for (int slot = 0; slot < per; slot++)
                {
                    string element = ports == 2
                        ? $"{name}[{Port(axes[^2], slot / n)},{Port(axes[^1], slot % n)}]"
                        : $"{name}[{Port(axes[^1], slot)}]";
                    sets[at].Values.Add(Variable(element, quantity, complex, values, slot, per));
                }
                if (z0AsPortZ && name == "S")
                {
                    int points = values.Length / per;
                    var z0 = cubes["Z0"];
                    var z = z0.DataKind == DataKind.Complex ? z0.ComplexValues : [.. z0.RealValues.Select(r => new Complex(r, 0))];
                    for (int p = 0; p < z.Length; p++)
                        sets[at].Values.Add(new BinaryDatasetVariable($"PortZ[{Port(z0.Axes[0], p)}]", "port-impedance", BinaryValueKind.Complex,
                            [.. Enumerable.Repeat(z[p].Real, points)], [.. Enumerable.Repeat(z[p].Imaginary, points)], null));
                }
            }
            string blockName = group.Length == 0 ? "data" : group;
            for (int k = 0; k < sets.Count; k++)
                blocks.Add(new BinaryDatasetBlock(k == 0 ? blockName : $"{blockName}_{k + 1}", "",
                    [.. sets[k].Sweep.Select(a => new BinaryDatasetVariable(a.Name, QuantityOf(a.Name, a.Unit), BinaryValueKind.Real, a.Values, null, null))],
                    sets[k].Values));
        }
        return new BinaryDataset(blocks);

        static bool SameAxes(Axis[] a, Axis[] b) =>
            a.Length == b.Length && a.Zip(b).All(p => p.First.Name == p.Second.Name && p.First.Values.SequenceEqual(p.Second.Values));

        static string Port(Axis axis, int k) => ((int)Math.Round(axis.Values[k])).ToString(CultureInfo.InvariantCulture);

        static BinaryDatasetVariable Variable(string name, string quantity, bool complex, Complex[] all, int slot, int per)
        {
            int points = all.Length / per;
            var re = new double[points];
            var im = complex ? new double[points] : null;
            for (int p = 0; p < points; p++)
            {
                re[p] = all[p * per + slot].Real;
                if (im is not null) im[p] = all[p * per + slot].Imaginary;
            }
            return new BinaryDatasetVariable(name, quantity, complex ? BinaryValueKind.Complex : BinaryValueKind.Real, re, im, null);
        }
    }

    /// <summary>The quantity word a unit (or, for S/Z0, a name) implies, as <see cref="BinaryDatasetVariable.Unit"/> reads it back.</summary>
    private static string QuantityOf(string name, string unit) => unit switch
    {
        "V" => "voltage",
        "A" => "current",
        "Hz" => "frequency",
        "Ohm" or "Ω" or "ohm" => "port-impedance",
        _ => name switch { "S" => "s-param", "Z0" => "port-impedance", "freq" => "frequency", _ => "" },
    };

    /// <summary>Writes <paramref name="ds"/> to <paramref name="path"/> through <see cref="FromDataSet"/>.</summary>
    public static void Write(string path, DataSet ds, BinaryDatasetVersion version = BinaryDatasetVersion.V4) =>
        Write(path, FromDataSet(ds), version);

    /// <summary>Writes <paramref name="data"/> to <paramref name="path"/>.</summary>
    /// <exception cref="ArgumentException">A block whose values do not fill its grid, or one with no axis.</exception>
    public static void Write(string path, BinaryDataset data, BinaryDatasetVersion version = BinaryDatasetVersion.V4) =>
        File.WriteAllBytes(path, ToBytes(data, version));

    public static byte[] ToBytes(BinaryDataset data, BinaryDatasetVersion version = BinaryDatasetVersion.V4) =>
        ToBytes(data, version, null, null);

    /// <summary>
    /// The file, with — for tests of the reader's refusals only — the first block's innermost counts
    /// (<paramref name="innerCounts"/>, one per outer run) or innermost values (<paramref name="innerAxis"/>, one array
    /// per outer run) stored as given rather than as its grid says.
    /// </summary>
    internal static byte[] ToBytes(BinaryDataset data, BinaryDatasetVersion version, int[]? innerCounts, double[][]? innerAxis)
    {
        bool v4 = version == BinaryDatasetVersion.V4;
        int pageSize = v4 ? 1024 : 8192, valueRun = v4 ? 8 : 1;
        var blocks = data.Blocks;

        var strings = new List<string>();
        int Str(string s) { strings.Add(s); return strings.Count; }
        int[] blockNameIds = [.. blocks.Select(b => Str(b.Name))];
        var textIds = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var b in blocks)
            foreach (var v in b.Values)
                if (v.Text is { } t)
                    foreach (string s in t) if (!textIds.ContainsKey(s)) textIds[s] = Str(s);

        var parts = new List<List<(string Key, byte[] Blob, int Run)>>();
        var descriptions = new List<byte[]>();
        for (int b = 0; b < blocks.Count; b++)
        {
            var (p, d) = BlockParts(b, blocks[b], v4, valueRun, Str, textIds,
                b == 0 ? innerCounts : null, b == 0 ? innerAxis : null);
            parts.Add(p);
            descriptions.Add(d);
        }

        // Objects in link order: key directory, key strings, string pool, block index, then each block's.
        var keys = new List<string>();
        var objs = new List<(byte[] Blob, int Run)>();
        void Obj(string? key, byte[] blob, int run = 1) { if (key is not null) keys.Add(key); objs.Add((blob, run)); }
        Obj(null, new byte[64]);
        Obj(null, []);
        Obj("STRINGDATA", Pool(strings), valueRun);
        Obj("DATAMANAGER_INDEX", BlockIndex(blockNameIds));
        for (int b = 0; b < blocks.Count; b++)
        {
            foreach (var (key, blob, run) in parts[b]) Obj(key, blob, run);
            Obj($"var_top_{b}", descriptions[b]);
        }
        objs[1] = (Encoding.ASCII.GetBytes(string.Concat(keys.Select(k => k + "\0"))), 1);

        // Pages: 0 is the directory's page list; each object's page list sits at the page numbered by its id.
        var pages = new List<byte[]> { new byte[pageSize] };
        var dir = new List<(long Id, long Size, long Run)>();
        for (int link = 0; link < objs.Count; link++)
        {
            var (blob, run) = objs[link];
            // A large object takes longer runs, so its page list still fits one page.
            long capacity = (pageSize - 0x2C) / 8;
            while ((blob.Length + (long)run * pageSize - 1) / ((long)run * pageSize) > capacity) run *= 2;
            int id = pages.Count;
            var linkPage = new byte[pageSize];
            pages.Add(linkPage);
            var starts = new List<long>();
            for (int at = 0; at < Math.Max(blob.Length, 1); at += run * pageSize)
            {
                starts.Add(pages.Count);
                for (int k = 0; k < run; k++)
                {
                    var p = new byte[pageSize];
                    int from = at + k * pageSize, take = Math.Clamp(blob.Length - from, 0, pageSize);
                    if (take > 0) Array.Copy(blob, from, p, 0, take);
                    pages.Add(p);
                }
            }
            Link(linkPage, link, starts, pageSize);
            dir.Add((id, blob.Length, run));
        }
        int perPage = (pageSize - 0x14) / 24;
        var dirStarts = new List<long>();
        for (int at = 0; at < dir.Count; at += perPage)
        {
            var p = new byte[pageSize];
            "DIR*"u8.CopyTo(p);
            int o = 0x14;
            foreach (var (id, size, run) in dir.Skip(at).Take(perPage))
            {
                BitConverter.TryWriteBytes(p.AsSpan(o), id);
                BitConverter.TryWriteBytes(p.AsSpan(o + 8), size);
                BitConverter.TryWriteBytes(p.AsSpan(o + 16), run);
                o += 24;
            }
            if (o + 24 <= pageSize) p.AsSpan(o, 24).Fill(0xFF);
            dirStarts.Add(pages.Count);
            pages.Add(p);
        }
        Link(pages[0], unchecked((int)0xDEADBEEF), dirStarts, pageSize);
        if (dirStarts.Count > (pageSize - 0x2C) / 8) throw new ArgumentException("The dataset has more objects than one directory list holds.");

        var file = new byte[512 + (long)pages.Count * pageSize];
        "circuitRF"u8.CopyTo(file);
        file[13] = 0x0D; file[14] = 0x0A; file[15] = 0x1A;
        BitConverter.TryWriteBytes(file.AsSpan(0x10), 0xCEFAEDFEu);
        "DCBA"u8.CopyTo(file.AsSpan(0x20));
        BitConverter.TryWriteBytes(file.AsSpan(0x24), (uint)version);
        BitConverter.TryWriteBytes(file.AsSpan(0x30), (uint)pageSize);
        BitConverter.TryWriteBytes(file.AsSpan(0x34), (uint)valueRun);
        BitConverter.TryWriteBytes(file.AsSpan(0x50), (uint)pages.Count);
        for (int n = 0; n < pages.Count; n++) pages[n].CopyTo(file, 512 + n * pageSize);
        return file;
    }

    private static (List<(string, byte[], int)> Parts, byte[] Description) BlockParts(int b, BinaryDatasetBlock block, bool v4,
        int valueRun, Func<string, int> str, Dictionary<string, int> textIds, int[]? innerCounts, double[][]? innerAxis)
    {
        int levels = block.Axes.Count;
        if (levels == 0) throw new ArgumentException($"Block '{block.Name}' has no axis.");
        foreach (var a in block.Axes)
            if (a.Kind == BinaryValueKind.Text) throw new ArgumentException($"Block '{block.Name}' sweeps '{a.Name}' over text.");
        int[] n = block.Shape;
        int points = n.Aggregate(1, (x, y) => checked(x * y));
        foreach (var v in block.Values)
            if (v.Count != points)
                throw new ArgumentException($"'{v.Name}' of block '{block.Name}' holds {v.Count} values where its grid has {points}.");

        var parts = new List<(string, byte[], int)>();
        string Key(string role, params int[] pos) => $"{role}={levels}={string.Join(",", new[] { b }.Concat(pos))}";
        static int[] Zeros(int k) => [.. Enumerable.Repeat(0, k)];

        // Independent values: level l once per outer point.
        long outer = 1;
        for (int l = 0; l < levels; l++)
        {
            var axis = block.Axes[l];
            bool inner = l == levels - 1;
            var stored = new List<Complex>();
            for (long o = 0; o < outer; o++)
                if (inner && innerAxis is not null) stored.AddRange(innerAxis[o].Select(x => new Complex(x, 0)));
                else for (int k = 0; k < axis.Count; k++) stored.Add(new Complex(axis.Real[k], axis.Imag?[k] ?? 0));
            if (inner && levels >= 3)
            {
                int per = stored.Count / n[0];
                for (int k = 0; k < n[0]; k++)
                    parts.Add((Key("DNS_I", [k, .. Zeros(l - 1)]), Records(stored.Skip(k * per).Take(per)), valueRun));
            }
            else parts.Add((Key("DNS_I", Zeros(l)), Records(stored), valueRun));
            outer *= n[l];
        }

        // Sweep counts: one record per outer level, one per innermost run.
        if (levels >= 2)
        {
            parts.Add((Key("DN"), Sweep([n[0] - 1]), 1));
            for (int d = 2; d < levels; d++)
                for (int k = 0; k < n[0]; k++)
                    parts.Add((Key("DN", [k, .. Zeros(d - 2)]), Sweep([n[d - 1] - 1]), 1));
        }
        long runs = outer / n[^1];
        long[] counts = innerCounts is not null ? [.. innerCounts.Select(c => (long)c - 1)] : [.. Enumerable.Repeat((long)n[^1] - 1, (int)runs)];
        if (levels >= 3)
        {
            long per = runs / n[0];
            for (int k = 0; k < n[0]; k++)
                parts.Add((Key("DNS_Y", [k, .. Zeros(levels - 2)]), Sweep(counts.Skip((int)(k * per)).Take((int)per)), 1));
        }
        else parts.Add((Key("DNS_Y", Zeros(levels - 1)), Sweep(counts), 1));

        // Dependent values, interleaved point by point.
        int nd = block.Values.Count;
        if (nd > 0)
        {
            var all = new Complex[(long)points * nd];
            for (int c = 0; c < nd; c++)
            {
                var v = block.Values[c];
                for (int p = 0; p < points; p++)
                    all[(long)p * nd + c] = v.Text is { } t
                        ? new Complex(BitConverter.Int64BitsToDouble(textIds[t[p]]), 0)
                        : new Complex(v.Real[p], v.Imag?[p] ?? 0);
            }
            if (levels >= 3)
            {
                int per = points / n[0];
                for (int k = 0; k < n[0]; k++)
                    parts.Add((Key("DNS_D", [k, .. Zeros(levels - 2)]), Records(all.Skip(k * per * nd).Take(per * nd)), valueRun));
            }
            else parts.Add((Key("DNS_D", Zeros(levels - 1)), Records(all), valueRun));
        }

        // The description: independents, then dependents.
        var iv = new List<int>();
        iv.AddRange(v4 ? [5, 1, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0] : [4, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        void Group(IReadOnlyList<BinaryDatasetVariable> vars, bool indep)
        {
            iv.AddRange([v4 ? 3 : 2, vars.Count, 0]);
            foreach (var v in vars)
            {
                int type = v.Kind switch { BinaryValueKind.Complex => 500, BinaryValueKind.Text => 600, _ => 400 };
                int name = str(v.Name);
                if (!v4) { iv.AddRange([type, name, 0, 24, 0, 0, 10, 0, 0, 0]); continue; }
                int flags = str("flags");
                string kind = type switch { 500 => "complex", 600 => "string", _ => "real" };
                int q = str($"{(v.Quantity.Length > 0 ? v.Quantity + " " : "")}type={kind} indep={(indep ? "yes" : "no")}");
                iv.AddRange([type, name, 0, 0, 0, 24, 0, 0, 1, 0, 28, 0, 2, 1, 0, flags, 0, q, 0]);
            }
        }
        Group(block.Axes, true);
        Group(block.Values, false);
        return (parts, [.. iv.SelectMany(BitConverter.GetBytes)]);
    }

    private static byte[] Records(IEnumerable<Complex> values)
    {
        var list = values as IList<Complex> ?? values.ToList();
        var b = new byte[list.Count * 24];
        for (int k = 0; k < list.Count; k++)
        {
            BitConverter.TryWriteBytes(b.AsSpan(24 * k), list[k].Real);
            BitConverter.TryWriteBytes(b.AsSpan(24 * k + 8), list[k].Imaginary);
            BitConverter.TryWriteBytes(b.AsSpan(24 * k + 20), 1);
        }
        return b;
    }

    /// <summary>Sweep records, 100 bytes each: the magic, 2, and the count − 1 at byte 36.</summary>
    private static byte[] Sweep(IEnumerable<long> countsMinusOne)
    {
        var list = countsMinusOne.ToList();
        var b = new byte[list.Count * 100];
        for (int k = 0; k < list.Count; k++)
        {
            BitConverter.TryWriteBytes(b.AsSpan(100 * k), SweepMagic);
            BitConverter.TryWriteBytes(b.AsSpan(100 * k + 4), 2);
            BitConverter.TryWriteBytes(b.AsSpan(100 * k + 36), list[k]);
        }
        return b;
    }

    private static byte[] Pool(List<string> strings)
    {
        const int head = 36;
        var text = new MemoryStream();
        var entries = new List<(long Off, long Len)>(strings.Count);
        foreach (string s in strings)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(s + "\0");
            entries.Add((head + text.Length, bytes.Length));
            text.Write(bytes);
        }
        long index = head + text.Length;
        var b = new byte[index + 8 + 25L * entries.Count + 25];
        BitConverter.TryWriteBytes(b.AsSpan(0), 0x7A3C0E3Bu);
        BitConverter.TryWriteBytes(b.AsSpan(4), index);
        BitConverter.TryWriteBytes(b.AsSpan(20), (long)strings.Count + 1);
        BitConverter.TryWriteBytes(b.AsSpan(28), (long)b.Length);
        text.ToArray().CopyTo(b, head);
        long o = index + 8;
        foreach (var (off, len) in entries)
        {
            b[o] = 1;
            BitConverter.TryWriteBytes(b.AsSpan((int)o + 1), off);
            BitConverter.TryWriteBytes(b.AsSpan((int)o + 9), len);
            BitConverter.TryWriteBytes(b.AsSpan((int)o + 17), len);
            o += 25;
        }
        return b;
    }

    private static byte[] BlockIndex(int[] nameIds)
    {
        var iv = new List<int> { 2003, 3, nameIds.Length, 0, 16, 0, 0 };
        for (int k = 0; k < nameIds.Length; k++) iv.AddRange([nameIds[k], 0, k, 0]);
        return [.. iv.SelectMany(BitConverter.GetBytes)];
    }

    private static void Link(byte[] page, int linkId, List<long> starts, int pageSize)
    {
        if (starts.Count > (pageSize - 0x2C) / 8) throw new ArgumentException("An object is larger than one page list can address.");
        "LINK"u8.CopyTo(page);
        BitConverter.TryWriteBytes(page.AsSpan(4), linkId);
        int o = 0x2C;
        foreach (long s in starts) { BitConverter.TryWriteBytes(page.AsSpan(o), s); o += 8; }
        for (; o + 8 <= page.Length; o += 8) BitConverter.TryWriteBytes(page.AsSpan(o), -1L);
    }
}

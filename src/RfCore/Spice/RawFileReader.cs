using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;

namespace RfCore.Spice;

/// <summary>One variable of a rawfile plot, as its header lists it: <c>1	v(out)	voltage</c>.</summary>
/// <param name="Index">Its position in every point, from 0 (the scale, usually <c>time</c> or <c>frequency</c>).</param>
/// <param name="Name">The vector's name as the simulator wrote it (<c>v(out)</c>, <c>i(vprobe)</c>, <c>time</c>).</param>
/// <param name="Type">Its quantity (<c>time</c>, <c>voltage</c>, <c>current</c>, <c>frequency</c>, …).</param>
public sealed record RawVariable(int Index, string Name, string Type);

/// <summary>
/// One plot of a rawfile: its header and its vectors. A real plot fills <see cref="Real"/>; a complex one (<c>Flags: complex</c>,
/// an AC or noise analysis) fills <see cref="Complex"/>, whose scale vector carries its value in the real part.
/// </summary>
public sealed class RawPlot
{
    public string Title { get; init; } = "";
    public string Date { get; init; } = "";
    public string Plotname { get; init; } = "";
    /// <summary>The <c>Flags:</c> words, lower case (<c>real</c>, <c>complex</c>, <c>padded</c>, <c>unpadded</c>, …).</summary>
    public IReadOnlyList<string> Flags { get; init; } = [];
    public required IReadOnlyList<RawVariable> Variables { get; init; }
    public int Points { get; init; }
    /// <summary>True for a complex plot.</summary>
    public bool IsComplex => Flags.Contains("complex");
    /// <summary>A real plot's vectors, one per variable, each <see cref="Points"/> long; null for a complex plot.</summary>
    public double[][]? Real { get; init; }
    /// <summary>A complex plot's vectors, one per variable; null for a real plot.</summary>
    public Complex[][]? Complex { get; init; }

    /// <summary>The variable named <paramref name="name"/> (the dialect is case-insensitive), or null.</summary>
    public RawVariable? Find(string name)
        => Variables.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The real values of <paramref name="name"/> (a complex plot's real parts), or null when the plot has no such vector.</summary>
    public double[]? RealOf(string name)
        => Find(name) is not { } v ? null : Real is { } r ? r[v.Index] : Complex![v.Index].Select(c => c.Real).ToArray();
}

/// <summary>
/// Reads a SPICE rawfile, binary or ASCII (brief TRN-7 R-trn7-5; transient.md §9.3): the header's title, date, plot name, flags
/// and variable list, then every point. A file may hold several plots one after another (an operating point and a transient);
/// each is returned in order. Binary values are little-endian doubles, point by point, two per variable in a complex plot.
/// </summary>
public static class RawFileReader
{
    /// <summary>Every plot in the file at <paramref name="path"/>.</summary>
    public static IReadOnlyList<RawPlot> Read(string path)
    {
        using var s = File.OpenRead(path);
        return Read(s);
    }

    /// <summary>Every plot in <paramref name="stream"/>, read to its end.</summary>
    public static IReadOnlyList<RawPlot> Read(Stream stream)
    {
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        byte[] data = ms.ToArray();
        var plots = new List<RawPlot>();
        int pos = 0;
        while (pos < data.Length)
        {
            // trailing whitespace after the last plot
            int probe = pos;
            while (probe < data.Length && data[probe] is (byte)' ' or (byte)'\n' or (byte)'\r' or (byte)'\t') probe++;
            if (probe >= data.Length) break;
            plots.Add(ReadPlot(data, ref pos));
        }
        if (plots.Count == 0) throw new InvalidDataException("the rawfile holds no plot");
        return plots;
    }

    private static RawPlot ReadPlot(byte[] data, ref int pos)
    {
        string title = "", date = "", plotname = "";
        var flags = new List<string>();
        int nVars = -1, nPoints = -1;
        var vars = new List<RawVariable>();
        bool? binary = null;
        while (binary is null)
        {
            string? line = ReadLine(data, ref pos) ?? throw new InvalidDataException("the rawfile ends inside a header");
            int colon = line.IndexOf(':');
            if (colon < 0) { if (line.Trim().Length == 0) continue; throw new InvalidDataException($"a rawfile header line has no key: '{line}'"); }
            string key = line[..colon].Trim().ToLowerInvariant(), value = line[(colon + 1)..].Trim();
            switch (key)
            {
                case "title": title = value; break;
                case "date": date = value; break;
                case "plotname": plotname = value; break;
                case "flags": flags.AddRange(value.ToLowerInvariant().Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)); break;
                case "no. variables": nVars = int.Parse(value, CultureInfo.InvariantCulture); break;
                case "no. points": nPoints = int.Parse(value, CultureInfo.InvariantCulture); break;
                case "variables":
                {
                    if (nVars < 0) throw new InvalidDataException("the rawfile lists its variables before saying how many");
                    // the first may follow the colon on the same line
                    string first = value;
                    for (int k = 0; k < nVars; k++)
                    {
                        string vline = k == 0 && first.Length > 0 ? first
                            : ReadLine(data, ref pos) ?? throw new InvalidDataException("the rawfile ends inside its variable list");
                        var parts = vline.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length < 3 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx) || idx != k)
                            throw new InvalidDataException($"rawfile variable {k} is malformed: '{vline.Trim()}'");
                        vars.Add(new RawVariable(idx, parts[1], parts[2]));
                    }
                    break;
                }
                case "binary": binary = true; break;
                case "values": binary = false; break;
                default: break;   // Command:, Option:, Dimensions: and the like are informational
            }
        }
        if (nVars <= 0 || nPoints < 0 || vars.Count != nVars)
            throw new InvalidDataException("the rawfile's header does not give its variables and points");
        bool complex = flags.Contains("complex");
        double[][]? real = complex ? null : Enumerable.Range(0, nVars).Select(_ => new double[nPoints]).ToArray();
        Complex[][]? cplx = complex ? Enumerable.Range(0, nVars).Select(_ => new Complex[nPoints]).ToArray() : null;

        if (binary == true)
        {
            int width = complex ? 16 : 8;
            long need = (long)nPoints * nVars * width;
            if (pos + need > data.Length)
                throw new InvalidDataException($"the rawfile's binary data stops short: {nPoints} points of {nVars} variables need {need} bytes, {data.Length - pos} remain");
            var span = data.AsSpan(pos);
            int o = 0;
            for (int p = 0; p < nPoints; p++)
                for (int v = 0; v < nVars; v++)
                {
                    double re = BitConverter.ToDouble(LittleEndian(span.Slice(o, 8)));
                    o += 8;
                    if (complex)
                    {
                        double im = BitConverter.ToDouble(LittleEndian(span.Slice(o, 8)));
                        o += 8;
                        cplx![v][p] = new Complex(re, im);
                    }
                    else real![v][p] = re;
                }
            pos += (int)need;
        }
        else
        {
            for (int p = 0; p < nPoints; p++)
                for (int v = 0; v < nVars; v++)
                {
                    string? tok = NextToken(data, ref pos) ?? throw new InvalidDataException($"the rawfile's values stop at point {p}");
                    // the first value of a point is preceded by its index
                    if (v == 0)
                    {
                        if (!int.TryParse(tok, NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx) || idx != p)
                            throw new InvalidDataException($"rawfile point {p} does not begin with its index (found '{tok}')");
                        tok = NextToken(data, ref pos) ?? throw new InvalidDataException($"the rawfile's values stop at point {p}");
                    }
                    if (complex)
                    {
                        int comma = tok.IndexOf(',');
                        if (comma < 0) throw new InvalidDataException($"a complex rawfile value is not re,im: '{tok}'");
                        cplx![v][p] = new Complex(Num(tok[..comma]), Num(tok[(comma + 1)..]));
                    }
                    else real![v][p] = Num(tok);
                }
        }
        return new RawPlot
        {
            Title = title, Date = date, Plotname = plotname, Flags = flags, Variables = vars, Points = nPoints, Real = real, Complex = cplx,
        };
    }

    private static double Num(string s) => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);

    private static ReadOnlySpan<byte> LittleEndian(ReadOnlySpan<byte> b)
    {
        if (BitConverter.IsLittleEndian) return b;
        var r = b.ToArray();
        Array.Reverse(r);
        return r;
    }

    /// <summary>The next line of the header, without its end; null at the end of the data.</summary>
    private static string? ReadLine(byte[] data, ref int pos)
    {
        if (pos >= data.Length) return null;
        int start = pos;
        while (pos < data.Length && data[pos] != (byte)'\n') pos++;
        int end = pos;
        if (pos < data.Length) pos++;   // the newline
        if (end > start && data[end - 1] == (byte)'\r') end--;
        return Encoding.UTF8.GetString(data, start, end - start);
    }

    /// <summary>The next whitespace-separated token of an ASCII value block; null at the end of the data.</summary>
    private static string? NextToken(byte[] data, ref int pos)
    {
        while (pos < data.Length && data[pos] is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r') pos++;
        if (pos >= data.Length) return null;
        int start = pos;
        while (pos < data.Length && data[pos] is not ((byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r')) pos++;
        return Encoding.ASCII.GetString(data, start, pos - start);
    }
}

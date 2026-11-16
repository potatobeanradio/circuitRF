using System;
using System.IO;
using System.Linq;
using RfCore.Spice;
using Xunit;

namespace RfCore.Tests.Spice;

/// <summary>
/// brief TRN-7 gate 1 — the same ngspice run written as a binary and as an ASCII rawfile reads to the same plot: header, variable
/// list, flags and every value. A transient run (real vectors) and a small-signal run (complex vectors, which later analyses will
/// read) of one RC, from <c>testdata/transient/rawfile/</c>; their decks are beside them.
/// </summary>
public sealed class RawFileReaderTests
{
    private static string Fixture(string name)
    {
        for (var dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
            if (Directory.Exists(Path.Combine(dir, "testdata", "transient", "rawfile")))
                return Path.Combine(dir, "testdata", "transient", "rawfile", name);
        throw new DirectoryNotFoundException("testdata not found above " + AppContext.BaseDirectory);
    }

    // The ASCII form prints 16 significant digits; a double needs 17 to round-trip, so the two agree to the last digit printed.
    private static bool Same(double a, double b) => a == b || Math.Abs(a - b) <= 1e-15 * Math.Max(Math.Abs(a), Math.Abs(b));

    [Theory]
    [InlineData("rc_tran", false, 45, new[] { "time", "v(in)", "v(out)", "i(vp)" })]
    [InlineData("rc_ac", true, 17, new[] { "frequency", "v(in)", "v(out)", "i(vs)" })]
    public void BinaryAndAscii_ReadToTheSamePlot(string run, bool complex, int points, string[] names)
    {
        var bin = Assert.Single(RawFileReader.Read(Fixture(run + ".binary.raw")));
        var asc = Assert.Single(RawFileReader.Read(Fixture(run + ".ascii.raw")));
        foreach (var p in new[] { bin, asc })
        {
            Assert.Equal(complex, p.IsComplex);
            Assert.Equal(points, p.Points);
            Assert.Equal(names, p.Variables.Select(v => v.Name));
        }
        Assert.Equal(bin.Plotname, asc.Plotname);
        Assert.Equal(bin.Variables, asc.Variables);
        for (int v = 0; v < names.Length; v++)
            for (int k = 0; k < points; k++)
                if (complex)
                {
                    var (x, y) = (bin.Complex![v][k], asc.Complex![v][k]);
                    Assert.True(Same(x.Real, y.Real), $"{names[v]}[{k}] re {x.Real:R} vs {y.Real:R}");
                    // ngspice leaves the scale's imaginary part unset (a denormal); only the vectors' imaginary parts mean anything
                    if (v > 0) Assert.True(Same(x.Imaginary, y.Imaginary), $"{names[v]}[{k}] im {x.Imaginary:R} vs {y.Imaginary:R}");
                }
                else Assert.True(Same(bin.Real![v][k], asc.Real![v][k]), $"{names[v]}[{k}] {bin.Real![v][k]:R} vs {asc.Real![v][k]:R}");
        // and the values are the circuit's: the RC's output at 2 µs, and the AC corner where |v(out)| = 1/√2
        if (!complex) Assert.InRange(bin.RealOf("v(out)")![^1], 0.84, 0.86);
        else
        {
            var f = bin.RealOf("frequency")!;
            int corner = Array.FindIndex(f, x => x >= 1 / (2 * Math.PI * 1e-6));
            Assert.InRange(bin.Complex![2][corner].Magnitude, 0.5, 0.75);
        }
    }
}

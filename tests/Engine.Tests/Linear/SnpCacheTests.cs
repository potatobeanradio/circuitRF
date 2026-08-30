// ================================================================
//  SnpCacheTests.cs  —  SP-P1 gate, engine side
//
//  Two things a process-wide Touchstone/fit cache can break, neither
//  of which any existing golden would notice:
//    * a cached SNP shared between two models, one of which mutates it;
//    * a file the user re-saves between runs still reading as the old one.
//  Both are asserted here, and the first is asserted BIT-IDENTICALLY —
//  the whole claim of SP-P1 is that no number moves.
// ================================================================

using System.Numerics;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using RfCore;
using RfCore.Data;

namespace CircuitRF.Engine.Tests.Linear;

public class SnpCacheTests
{
    private static string Hero1Dir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, "testdata", "Hero1");
            if (Directory.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException("testdata/Hero1 not found");
    }

    private static double[] Grid()
    {
        var f = new double[41];
        for (int i = 0; i < f.Length; i++) f[i] = 1.0e9 + i * 25e6;
        return f;
    }

    private static string Netlist(string snpPath) => $@"
Port:P1   n1 0  Num=1  Z=50 Ohm
Port:P2   n2 0  Num=2  Z=50 Ohm
SnP:X1    n1 n2  NumPorts=2  File=""{snpPath}""
analysis SP  type=sparam  start=1e9  stop=2e9  npts=41
";

    private static ElaboratedNetlist Elaborate(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        return new Elaborator(lib).Elaborate(tb);
    }

    private static void AssertBitIdentical(DataCube a, DataCube b, string what)
    {
        var av = a.ComplexValues;
        var bv = b.ComplexValues;
        Assert.Equal(av.Length, bv.Length);
        for (int k = 0; k < av.Length; k++)
            Assert.True(av[k].Real == bv[k].Real && av[k].Imaginary == bv[k].Imaginary,
                $"{what}: element {k} moved, {av[k]} vs {bv[k]}");
    }

    /// <summary>
    /// Three runs — twice on ONE netlist, once on a fresh elaboration — must agree bit for bit.
    /// Runs 1 and 2 share an SnpModel (so they share its interpolator); run 3 has a new model that
    /// pulls the SAME cached parse and fit out of <see cref="TouchstoneCache"/>. If anything
    /// mutated the shared SNP, or the cached fit belonged to different settings, run 3 diverges.
    /// This is the shape ParametricSweepEngine produces, which re-elaborates at every sweep point.
    /// </summary>
    [Fact]
    public void SharedCacheAcrossElaborations_ProducesBitIdenticalSCubes()
    {
        var cnl  = Netlist(Path.Combine(Hero1Dir(), "potentially_unstable_amp.s2p"));
        var grid = Grid();

        var nl1 = Elaborate(cnl);
        var s1  = SParameterEngine.Run(nl1, grid)["S"];
        var s2  = SParameterEngine.Run(nl1, grid)["S"];   // same models, same interpolator
        var s3  = SParameterEngine.Run(Elaborate(cnl), grid)["S"]; // fresh models, cached fit

        AssertBitIdentical(s1, s2, "same netlist, run twice");
        AssertBitIdentical(s1, s3, "fresh elaboration");
    }

    /// <summary>
    /// A parsed file is cached, but the cache is keyed on last-write time and length, so a file
    /// the user re-saves between runs IS re-read. Without this the GUI's re-run path would keep
    /// serving the old data with no way for the user to tell.
    /// </summary>
    [Fact]
    public void RewrittenFile_IsReRead_NotServedFromCache()
    {
        string dir = Path.Combine(Path.GetTempPath(),
                                  "crf-snp-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "dut.s2p");
            var grid = new double[] { 1.5e9 };

            // A flat, matched, purely-real through with S21 = 0.5.
            File.WriteAllText(path, Through(0.5));
            var first = SParameterEngine.Run(Elaborate(Netlist(path)), grid)["S"];
            var s21First = (Complex)first[0, 1, 0];

            // Rewrite with a different S21. Same length, so the mtime is what has to catch it —
            // and the file system's timestamp resolution is what forces the explicit stamp below.
            File.WriteAllText(path, Through(0.25));
            File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(2));

            var second = SParameterEngine.Run(Elaborate(Netlist(path)), grid)["S"];
            var s21Second = (Complex)second[0, 1, 0];

            Assert.True((s21First - new Complex(0.5, 0)).Magnitude < 1e-9,
                $"first run S21 = {s21First}, expected 0.5");
            Assert.True((s21Second - new Complex(0.25, 0)).Magnitude < 1e-9,
                $"second run S21 = {s21Second}, expected 0.25 — the rewritten file was not re-read");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>A matched, reciprocal, frequency-flat 2-port with the given real S21.</summary>
    private static string Through(double s21)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("! generated by SnpCacheTests");
        sb.AppendLine("# GHz S RI R 50");
        foreach (double fGHz in new[] { 1.0, 1.5, 2.0, 2.5 })
            sb.AppendLine($"{fGHz:0.0000} 0 0 {s21:0.0000} 0 {s21:0.0000} 0 0 0");
        return sb.ToString();
    }
}

using System.Numerics;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Optimization;
using CircuitRF.Engine;
using NumFlat;
using RfCore;

namespace CircuitRF.Ui.Tests;

/// <summary>Touchstone files written for the brief-sens-2 gates, in a folder of their own.</summary>
internal static class SnpFiles
{
    private static readonly string Dir = Directory.CreateTempSubdirectory("crf-sens2-").FullName;

    /// <summary>An N-port at 1, 2 and 3 GHz whose S(i,j) is <paramref name="entry"/>(i, j) (0-based).</summary>
    public static string Write(string name, int ports, Func<int, int, Complex> entry)
    {
        double[] f = [1e9, 2e9, 3e9];
        var m = f.Select(_ =>
        {
            var s = new Mat<Complex>(ports, ports);
            for (int i = 0; i < ports; i++)
                for (int j = 0; j < ports; j++) s[i, j] = entry(i, j);
            return s;
        }).ToArray();
        string path = Path.Combine(Dir, $"{name}.s{ports}p");
        TouchstoneIO.WriteFile(new SNP(f, m, MatrixType.S, MatrixFormat.RI), path);
        return path;
    }

    /// <summary>A passive, reciprocal 3-port: every off-diagonal 0.3∠−40°.</summary>
    public static string Reciprocal3 => _reciprocal3.Value;
    private static readonly Lazy<string> _reciprocal3 = new(() => Write("recip3", 3,
        (i, j) => i == j ? new Complex(0.1, 0) : Complex.FromPolarCoordinates(0.3, -40 * Math.PI / 180)));

    /// <summary>A 3-port whose forward entries (i &gt; j) are ten times its reverse ones — an amplifier's shape.</summary>
    public static string NonReciprocal3 => _nonReciprocal3.Value;
    private static readonly Lazy<string> _nonReciprocal3 = new(() => Write("gain3", 3,
        (i, j) => i == j ? new Complex(0.1, 0) : i > j ? new Complex(2, 0) : new Complex(0.2, 0)));

    /// <summary>A matched 6 dB attenuator: S11 = S22 = 0, S21 = S12 = 0.5.</summary>
    public static string Attenuator => _attenuator.Value;
    private static readonly Lazy<string> _attenuator = new(() => Write("pad", 2, (i, j) => i == j ? Complex.Zero : new Complex(0.5, 0)));
}

/// <summary>brief-sens-2 R-sens2-1…4: SnP entries as tunables, the tie rule's refusal, and the offset reaching the circuit.</summary>
public sealed class SnpEntryTunableTests
{
    private static string Bench(string file, string extra = "") => $"""
        Port:P1 a 0 Num=1 Z=50 Ohm
        Port:P2 b 0 Num=2 Z=50 Ohm
        SnP:SNP1 a b c NumPorts=3 File="{file}" {extra}
        R:R3 c 0 R=50 Ohm
        analysis SP1 type=sparam start=1 stop=3 npts=3 Unit=GHz
        """;

    private static TunableCatalog Catalog(string cnl)
    {
        var c = PreparedCircuit.FromText(cnl, null, null);
        Assert.Null(c.ReadError);
        return TunableCatalog.FromNetlist(c.Tb!, c.Lib!);
    }

    [Fact]
    public void ATiedThreePort_Offers12Keys_AndAnUntiedOne18()
    {
        var tied = Catalog(Bench(SnpFiles.Reciprocal3)).Tunables.Where(t => t.Kind == TunableKind.SnpEntry).ToList();
        Assert.Equal(12, tied.Count);
        Assert.All(tied, t => Assert.True(t.SnpEntry!.I >= t.SnpEntry.J));
        Assert.Contains(tied, t => t.Key == "SNP1.SdB_3_1" && t.Label == "SNP1 · S31 dB" && t.IsDefault && t.Value == 0);

        var untied = Catalog(Bench(SnpFiles.NonReciprocal3)).Tunables.Count(t => t.Kind == TunableKind.SnpEntry);
        Assert.Equal(18, untied);
    }

    [Fact]
    public void AnUpperTriangleKey_OnATiedInstance_IsRefusedNamingTheLowerKeyAndUntied()
    {
        var c = PreparedCircuit.FromText(Bench(SnpFiles.Reciprocal3, "SdB_1_3=0.5 dB"), null, null);
        var ex = Assert.Throws<ParameterRefusalException>(() => new Elaborator(c.Lib!).Elaborate(c.Tb!).Dispose());
        Assert.Contains("SNP1", ex.Message);
        Assert.Contains("SdB_3_1", ex.Message);
        Assert.Contains("Reciprocal=untied", ex.Message);
    }

    [Fact]
    public void AnSnpEntry_IsNeverTunedOrOptimized_ButMayCarryATolerance()
    {
        static IReadOnlyList<CircuitRF.Diagnostics.Diagnostic> Findings(string line)
        {
            var c = PreparedCircuit.FromText(Bench(SnpFiles.Reciprocal3) + "\n" + line, null, null);
            return TuningValidator.Validate(c.Tb!, TunableCatalog.FromNetlist(c.Tb!, c.Lib!));
        }
        Assert.Contains(Findings("tune SNP1.SdB_3_1 opt=1 min=-1 dB max=1 dB"), d => d.Id == "tuning.snp.statistical-only");
        Assert.Contains(Findings("tune SNP1.Sdeg_3_1 tune=1"), d => d.Id == "tuning.snp.statistical-only");
        Assert.DoesNotContain(Findings("tune SNP1.SdB_3_1 dist=gauss sd=0.15 dB"), d => d.Id == "tuning.snp.statistical-only");
    }

    [Fact]
    public void SdB21OfOne_OnAThroughPad_MovesTheCircuitsS21ByOneDb()
    {
        double S21Db(string extra)
        {
            var c = PreparedCircuit.FromText($"""
                Port:P1 a 0 Num=1 Z=50 Ohm
                Port:P2 b 0 Num=2 Z=50 Ohm
                SnP:PAD a b NumPorts=2 File="{SnpFiles.Attenuator}" {extra}
                analysis SP1 type=sparam start=2 stop=2 npts=1 Unit=GHz
                """, null, null);
            using var nl = new Elaborator(c.Lib!).Elaborate(c.Tb!);
            return 20 * Math.Log10(SParameterEngine.Run(nl, [2e9]).S(2, 1).ComplexValues[0].Magnitude);
        }
        Assert.Equal(1.0, S21Db("SdB_2_1=1 dB") - S21Db(""), 9);
    }
}

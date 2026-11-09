using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Modeling;
using CircuitRF.Engine;
using NumFlat;
using RfCore.Data;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>
/// Brief NZ-4 gate 4: a <c>.cmodel</c>'s <c>Core.Noise</c> block reads, writes and reads identically, and the Model
/// Composer's Noise fit recovers the gate and drain temperatures a synthetic measurement was made with.
/// </summary>
public sealed class NoiseFitTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("nz4-noise-fit-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private static string Example()
    {
        for (string? d = AppContext.BaseDirectory; d is not null; d = Path.GetDirectoryName(d))
            if (File.Exists(Path.Combine(d, "circuitRF.slnx")))
                return Path.Combine(d, "examples", "Model Composer", "synthetic-gan", "gan_4x125.cmodel");
        throw new DirectoryNotFoundException("repository root");
    }

    private static string ModelJson(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        using var r = new StreamReader(zip.GetEntry(CmodelFile.ModelEntry)!.Open());
        return r.ReadToEnd();
    }

    /// <summary>The example's network with an intrinsic gate resistance Ri in series with its gate resistor: what Tg heats.</summary>
    private static ComposedModel WithRi()
    {
        var m = CmodelFile.Read(Example());
        string net = m.Network.Netlist.Replace("R:R1  n1  P4  R=rg", "R:R1  n1  ngi  R=rg\n  R:Ri  ngi  P4  R=3 Ohm");
        Assert.NotEqual(m.Network.Netlist, net);
        return m with { Network = m.Network with { Netlist = net } };
    }

    [Fact]
    public void NoiseBlock_RoundTrips()
    {
        var m = WithRi();
        var noise = new ModelNoise(
            NoiseValue.Of(Constituent.Equation("300+10*vds", [ComposedCoreProgram.Vds])),
            NoiseValue.Of(2500), Kf: 1e-12, Af: 1.2);
        string a = Path.Combine(_dir, "a.cmodel"), b = Path.Combine(_dir, "b.cmodel");
        CmodelFile.Write(m with { Core = m.Core with { Noise = noise } }, a);
        var read = CmodelFile.Read(a);
        Assert.Equal(2500, read.Core.Noise!.Td!.Constant);
        Assert.Equal("300+10*vds", read.Core.Noise.Tg!.Function!.Expression("Expression"));
        Assert.Equal((1e-12, 1.2), (read.Core.Noise.Kf, read.Core.Noise.Af));
        CmodelFile.Write(read, b);
        Assert.Equal(ModelJson(a), ModelJson(b));
        Assert.Equal(ModelJson(b), ModelJson(Write(CmodelFile.Read(b), "c.cmodel")));
    }

    private string Write(ComposedModel m, string name)
    {
        string p = Path.Combine(_dir, name);
        CmodelFile.Write(m, p);
        return p;
    }

    /// <summary>A model with no Noise block reads as one, so a model made with no noise data is complete.</summary>
    [Fact]
    public void NoNoiseBlock_IsAWholeModel()
    {
        var m = CmodelFile.Read(Example());
        Assert.Null(m.Core.Noise);
        Assert.Null(CmodelFile.Read(Write(m, "plain.cmodel")).Core.Noise);
    }

    [Fact]
    public void Fit_RecoversTgAndTd()
    {
        const double Tg = 500, Td = 3000, Vgs = -0.5, Vds = 20, Ambient = 25;
        var model = WithRi();
        double[] hz = [2e9, 4e9, 6e9, 8e9, 10e9];
        var measured = Simulate(model with { Core = model.Core with { Noise = new ModelNoise(NoiseValue.Of(Tg), NoiseValue.Of(Td)) } },
                                Vgs, Vds, hz, Ambient);

        var fit = NoiseFit.FitBias(model, Vgs, Vds, measured, Ambient, Path.Combine(_dir, "scratch"));
        output.WriteLine($"Tg {fit.Tg:R} K, Td {fit.Td:R} K; NFmin {fit.NfMinErrorDb:G3} dB, Γopt {fit.GammaOptError:G3}, Rn {fit.RnError:G3}");
        Assert.Equal(1.0, fit.Tg / Tg, 1e-3);
        Assert.Equal(1.0, fit.Td / Td, 1e-3);
        Assert.True(fit.NfMinErrorDb < 1e-3, $"{fit.NfMinErrorDb}");
    }

    /// <summary>The measurement: the model's own noise parameters on 50 Ω at one bias, from the S-parameter noise analysis.</summary>
    private List<(double Hz, TwoPortNoise P)> Simulate(ComposedModel m, double vgs, double vds, double[] hz, double ambient)
    {
        string file = Write(m, "synthetic.cmodel");
        string N(double x) => x.ToString("R", CultureInfo.InvariantCulture);
        var (lib, tb) = new CnlReader().Read($"""
            temp = {N(ambient)}
            Port:P1  p1 0  Num=1 Z=50 Ohm
            Port:P2  p2 0  Num=2 Z=50 Ohm
            C:CB1  p1 g  C=1 F
            C:CB2  p2 d  C=1 F
            L:LG  vg g  L=1 H
            L:LD  vd d  L=1 H
            Vdc:VG  vg 0  Vdc={N(vgs)}
            Vdc:VD  vd 0  Vdc={N(vds)}
            ComposedFET:X1  g d 0  File="{file}"
            """);
        using var nl = new Elaborator(lib).Elaborate(tb);
        var ds = SParameterEngine.Run(nl, hz, AnalysisSettings.Default.WithNoise(true));
        var z0 = new[] { new Complex(50, 0), new Complex(50, 0) };
        var rows = new List<(double, TwoPortNoise)>();
        for (int i = 0; i < hz.Length; i++)
        {
            var s = new Mat<Complex>(2, 2); var c = new Mat<Complex>(2, 2);
            for (int p = 0; p < 2; p++)
                for (int q = 0; q < 2; q++) { s[p, q] = (Complex)ds["S"][i, p, q]; c[p, q] = (Complex)ds["Cs"][i, p, q]; }
            rows.Add((hz[i], NoiseMetrics.FromWaves(s, c, z0, 50)));
        }
        return rows;
    }
}

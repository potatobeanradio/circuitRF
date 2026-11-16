using System.Numerics;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Design.Layout.SchematicSync;
using CircuitRF.Design.Schematic;
using CircuitRF.Engine;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Mmic;

/// <summary>
/// MCRC as placed: at the registry's defaults it draws its two pins as every shape the picker offers; and on the shipped
/// MMIC and RFIC processes a hand-written .cnl in a GaAs or Si workspace takes the 100 µm substrate from the technology,
/// as every microstrip part does, and a hairpin filter drawn for it passes where its resonators are half a guided
/// wavelength.
/// </summary>
public sealed class CoupledResonatorGaAsTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-mcrc-" + Guid.NewGuid().ToString("N")[..12]);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    [Theory]
    [InlineData("mmic-GaAs_2LM_100um")]
    [InlineData("rfic-Si_3LM_TFR_100um")]
    public void AHairpinFilter_BuildsOnTheProcess_AndPassesAtItsHalfWave(string tech)
    {
        Directory.CreateDirectory(_root);
        string dir = Path.Combine(WorkspaceCreate.Create(_root, tech, tech).WorkspaceDir, "cell");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "t.cnl");
        const double w = 70e-6, l = 2.6e-3;
        File.WriteAllText(path, """
            Port:P1 a 0 Num=1 Z=50 Ohm
            Port:P2 b 0 Num=2 Z=50 Ohm
            MCRC:F1 a b N=3 W=70um L=2.6mm S1=20um S2=80um S3=80um S4=20um Shape=Hairpin Sa=140um
            """);
        var (lib, tb) = CnlTechnologyBinding.ReadFile(path);
        var nl = new Elaborator(lib).Elaborate(tb);
        var f1 = nl.Components.Single(c => c.InstancePath == "F1");
        Assert.IsType<MicrostripPlanModel>(f1.Model);
        double h = f1.Parameters["H"].AsReal(), er = f1.Parameters["Er"].AsReal();
        Assert.InRange(h, 90e-6, 110e-6);   // the process's substrate, not the factory's 1.6 mm board

        var line = new MicrostripLineModel(w, 0.0, h, f1.Parameters["T"].AsReal(), er, 5.8e7, 0.0, "t");
        double half = 299_792_458.0 / (2.0 * l * Math.Sqrt(line.LineParameters(20e9).Eeff));
        var f = Enumerable.Range(0, 201).Select(i => half * (0.7 + 0.6 * i / 200.0)).ToArray();
        var s = SParameterEngine.Run(nl, f)["S"];
        var db = Enumerable.Range(0, f.Length).Select(i => 20.0 * Math.Log10(((Complex)s[i, 1, 0]).Magnitude)).ToArray();
        double peak = f[Array.IndexOf(db, db.Max())];
        output.WriteLine($"{tech}: H {h * 1e6} µm, Er {er}; half-wave {half / 1e9:F2} GHz, |S21| peaks {db.Max():F2} dB at {peak / 1e9:F2} GHz");
        Assert.True(db.Max() > -6.0, $"|S21| peaks at {db.Max()} dB");   // gold on GaAs, gaps not designed for a match
        Assert.InRange(peak / half, 0.9, 1.1);
    }

    /// <summary>A freshly placed part switched to any shape the picker offers draws, with its two pins and nothing
    /// refused: the registry states S1 to S4, and the four cross-coupled loops take S5 from the common gap S.</summary>
    [Theory]
    [InlineData("Straight", "Inline", 3)]
    [InlineData("ZigZag", "Inline", 3)]
    [InlineData("Hairpin", "Inline", 3)]
    [InlineData("OpenLoop", "Inline", 3)]
    [InlineData("OpenLoop", "Cross", 4)]
    [InlineData("Ring", "Inline", 1)]
    public void AFreshlyPlacedPart_DrawsItsTwoPins_AsEveryShape(string shape, string arrangement, int n)
    {
        var p = new Dictionary<string, PCellValue>(SchematicLayoutPlanner.ResolveDefaultParameters(SymbolKind.Mcrc, 2))
        {
            ["Shape"] = PCellValue.Text(shape), ["Arrangement"] = PCellValue.Text(arrangement), ["N"] = n,
        };
        Assert.True(PCellRegistry.TryGet(ComponentModelFactory.CoupledResonatorTypeName, out var generate));
        var art = generate(p, null, PCellLayerSelection.Default);
        Assert.True(art.Diagnostics is null or { Count: 0 }, string.Join("; ", art.Diagnostics ?? []));
        Assert.Equal(["1", "2"], art.Pins.Select(x => x.Name));
    }
}

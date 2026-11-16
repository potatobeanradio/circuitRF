// MDLY, the delay line: registered as a part and a layout cell on MLIN's two pins, drawn and simulated on a GaAs die; and
// MRRC's meandered ring, the same route folded inside a rat-race, drawn smaller than the curved ring.

using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Design.Schematic;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests;

public sealed class DelayLinePartTests(ITestOutputHelper output)
{
    [Fact]
    public void TheDelayLine_IsRegistered_AsAPartAndALayoutCell_OnTwoPins()
    {
        Assert.True(ComponentTypeRegistry.TryParseCode("DLY", out var kind, out _));
        Assert.Equal(SymbolKind.Mdly, kind);
        Assert.Equal("MDLY", ComponentTypeRegistry.EngineReference(kind, 2));
        Assert.True(PCellRegistry.TryGet("MDLY", out _));
        Assert.Equal([("1", -200f, 0f), ("2", 200f, 0f)], SymbolPortDefs.For(kind).Select(p => (p.Name, p.LocalX, p.LocalY)));
        Assert.Equal(["Straight", "Corner", "Meander"], ComponentTypeRegistry.NamedParamOptions(kind, "Style"));
        Assert.Equal(["Curve", "Miter", "Square"], ComponentTypeRegistry.NamedParamOptions(kind, "Corner"));
    }

    /// <summary>On the GaAs die a 360° meander at 20 GHz draws on the die's signal layer with no finding, its pins its
    /// route's two ends, and the model built on the die's substrate is −360° at F.</summary>
    [Fact]
    public void OnAGaAsDie_AMeanderDrawsAndHoldsItsLength()
    {
        var tech = ShippedTechnologies.Load("mmic-GaAs_2LM_100um");
        var art = PassivePlanPCells.GenerateDelayLine(new Dictionary<string, PCellValue>
        {
            ["Z0"] = 50.0, ["F"] = 20e9, ["Theta"] = 360.0, ["Style"] = PCellValue.Text("Meander"), ["Turns"] = 3.0,
        }, tech, PCellLayerSelection.Default);
        Assert.Null(art.Diagnostics);
        Assert.Equal(["1", "2"], art.Pins.Select(p => p.Name));

        var (s, _, _) = SubstrateResolver.ResolveElectrical(tech, PCellLayerSelection.Default);
        Assert.NotNull(s);
        var sub = new MicrostripSubstrate(s!.HeightMeters, s.ThicknessMeters, s.RelativePermittivity, s.ConductivitySPerM,
                                          s.LossTangent, s.RoughnessRmsMeters);
        var d = DelayLinePlan.Build(50.0, 20e9, 2.0 * Math.PI, sub, turns: 3);
        double phase = d.Plan.S21(sub, 20e9, 50.0).Phase * 180.0 / Math.PI;
        output.WriteLine($"GaAs: W {d.WMeters * 1e6:F1} µm, {d.Route.Sections.Count} pieces, S21 {phase:F3}° at 20 GHz");
        Assert.True(Math.Abs(phase) < 0.5, $"S21 {phase}° at F");
        Assert.Empty(new MicrostripPlanModel(d.Plan, sub, "D1").DrainWarnings());
    }

    /// <summary>MRRC with Meander on the example board: the drawn copper, feeds included, against the curved ring's.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void TheMeanderedRatRace_DrawsSmallerThanTheCurvedRing(int turns)
    {
        var tech = ShippedTechnologies.Load(ShippedTechnologies.DefaultId);
        (long W, long H) Extent(double meander)
        {
            var art = PassivePlanPCells.GenerateRatRace(new Dictionary<string, PCellValue>
            {
                ["Z0"] = 50.0, ["F"] = 2.4e9, ["Meander"] = meander,
            }, tech, PCellLayerSelection.Default);
            Assert.Null(art.Diagnostics);
            var boxes = art.Shapes.Select(LayoutGeometry.BboxOf).ToList();
            return (boxes.Max(b => b.MaxX) - boxes.Min(b => b.MinX), boxes.Max(b => b.MaxY) - boxes.Min(b => b.MinY));
        }
        var (curved, folded) = (Extent(0.0), Extent(turns));
        double ratio = (double)folded.W * folded.H / ((double)curved.W * curved.H);
        output.WriteLine($"curved {curved.W / 1e6:F2} × {curved.H / 1e6:F2} mm, meandered ({turns}) {folded.W / 1e6:F2} × "
                       + $"{folded.H / 1e6:F2} mm, area {ratio:P0}");
        Assert.True(folded.W < curved.W && folded.H <= curved.H, $"{folded} vs {curved}");
        Assert.True(ratio < 0.65, $"area ratio {ratio:F3}");
    }
}

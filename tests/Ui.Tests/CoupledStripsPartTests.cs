// MCLINN, N coupled microstrip lines: registered as a part and a layout cell, its pins following N, its artwork N MLIN
// strips at their own widths and gaps with each strip's pins on its two ends, and its cross-section built from an RFIC
// stack's substrate as from a board's.

using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Design.Schematic;

namespace CircuitRF.Ui.Tests;

public sealed class CoupledStripsPartTests
{
    [Fact]
    public void TheCoupledStrips_AreRegistered_AsAPartAndALayoutCell_WithPinsFollowingN()
    {
        Assert.True(ComponentTypeRegistry.TryParseCode("MCLN", out var kind, out _));
        Assert.Equal(SymbolKind.MCLineN, kind);
        Assert.Equal("MCLINN", ComponentTypeRegistry.EngineReference(kind, 6));
        Assert.Contains(ComponentTypeRegistry.DefaultParameters(kind, 6), p => p.Name == "N" && p.Expression == "3");
        Assert.True(PCellRegistry.TryGet("MCLINN", out _));

        var component = new EditableComponent { Symbol = kind };
        component.Parameters.Add(new EditableParameter { Name = "N", Expression = "4" });
        Assert.Equal(8, component.PortCount);
        var four = SymbolPortDefs.For(kind, component.PortCount);
        Assert.Equal(["1", "2", "3", "4", "5", "6", "7", "8"], four.Select(p => p.Name));
        Assert.All(Enumerable.Range(0, 4), k => Assert.Equal(four[k].LocalY, four[4 + k].LocalY));
        Assert.Equal(four.Select(p => (p.Name, p.LocalX, p.LocalY)),
                     BuiltInSymbols.Primitives(kind, 8).Pins.Select(p => (p.Name, (float)p.LocalX, (float)p.LocalY)));
        Assert.Equal(6, SymbolPortDefs.For(kind).Length);      // the one-argument default: three strips
    }

    [Fact]
    public void TheArtwork_IsOneMlinPerStrip_AtItsOwnWidthAndGap_WithEachStripsPinsOnItsEnds()
    {
        var cell = PassivePlanPCells.GenerateCoupledStrips(new Dictionary<string, PCellValue>
        {
            ["N"] = 3.0, ["W"] = 1.1e-3, ["S"] = 0.5e-3, ["W[2]"] = 1.4e-3, ["S[1]"] = 0.3e-3, ["L"] = 20e-3,
        }, ShippedTechnologies.Load(ShippedTechnologies.DefaultId), PCellLayerSelection.Default);
        Assert.Null(cell.Diagnostics);
        Assert.Equal(["1", "2", "3", "4", "5", "6"], cell.Pins.Select(p => p.Name));

        var strips = cell.Shapes.OfType<RectShape>().OrderByDescending(r => r.Y2).ToList();
        Assert.Equal(3, strips.Count);
        Assert.Equal([1_100_000L, 1_400_000L, 1_100_000L], strips.Select(r => r.Y2 - r.Y1));
        Assert.Equal(300_000L, strips[0].Y1 - strips[1].Y2);
        Assert.Equal(500_000L, strips[1].Y1 - strips[2].Y2);
        for (int k = 0; k < 3; k++)
        {
            long mid = (strips[k].Y1 + strips[k].Y2) / 2;
            Assert.Equal((0L, mid), (cell.Pins[k].X, cell.Pins[k].Y));
            Assert.Equal((20_000_000L, mid), (cell.Pins[3 + k].X, cell.Pins[3 + k].Y));
        }

        var refused = PassivePlanPCells.GenerateCoupledStrips(new Dictionary<string, PCellValue> { ["N"] = 1.0 },
            null, PCellLayerSelection.Default);
        Assert.Empty(refused.Shapes);
        Assert.Contains("coupled lines are at least two", Assert.Single(refused.Diagnostics!));
    }

    /// <summary>On a thin-film RFIC stack the cross-section builds from the substrate the technology resolves for a
    /// microstrip part, as on a board: three modes, each slower than light and faster than the bulk dielectric, and the
    /// strips drawn on its signal layer.</summary>
    [Fact]
    public void TheCrossSection_BuildsOnAnRficStack()
    {
        var tech = ShippedTechnologies.Load("rfic-Si_3LM_TFR_100um");
        var (s, _, _) = SubstrateResolver.ResolveElectrical(tech, PCellLayerSelection.Default);
        Assert.NotNull(s);
        var sub = new MicrostripSubstrate(s!.HeightMeters, s.ThicknessMeters, s.RelativePermittivity, s.ConductivitySPerM,
                                          s.LossTangent, s.RoughnessRmsMeters);
        var model = new MicrostripMultiLineModel([10e-6, 10e-6, 10e-6], [5e-6, 5e-6], 200e-6, sub, "t");
        var modes = model.Section.Modes;
        Assert.Equal(3, modes.ModeCount);
        Assert.All(Enumerable.Range(0, 3), m =>
        {
            Assert.InRange(modes.Eeff[m], 1.0, sub.EpsR);
            Assert.True(modes.Z0(m) > 0);
        });

        var cell = PassivePlanPCells.GenerateCoupledStrips(new Dictionary<string, PCellValue>
        {
            ["N"] = 3.0, ["W"] = 10e-6, ["S"] = 5e-6, ["L"] = 200e-6,
        }, tech, PCellLayerSelection.Default);
        Assert.Equal(3, cell.Shapes.OfType<RectShape>().Count());
    }
}

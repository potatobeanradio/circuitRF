// MCLA, the coupled array: registered as a part and a layout cell, its pins following N, Form, Feed and Ends, its artwork
// the plan's strips, taps and folds with each end pin on its strip's end, Place Site Parts filling a combline's
// capacitors and vias, and a via-less array built and simulated on the GaAs die.

using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Design.Schematic;
using CircuitRF.Ui.Commands.Schematic;
using CircuitRF.Ui.Tests.SchematicSync;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests;

public sealed class CoupledArrayPartTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "crf-cla-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void TheArray_IsRegisteredAsAPartAndALayoutCell_WithPinsFollowingItsEnds()
    {
        Assert.True(ComponentTypeRegistry.TryParseCode("CLA", out var kind, out _));
        Assert.Equal(SymbolKind.Mcla, kind);
        Assert.Equal("MCLA", ComponentTypeRegistry.EngineReference(kind, 5));
        Assert.Contains(ComponentTypeRegistry.DefaultParameters(kind, 5), p => p.Name == "Form" && p.Expression == "Interdigital");
        Assert.Equal(Enum.GetNames<CoupledArrayForm>(), ComponentTypeRegistry.NamedParamOptions(kind, "Form"));
        Assert.True(PCellRegistry.TryGet("MCLA", out _));

        var component = new EditableComponent { Symbol = kind };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(kind, 5))
            component.Parameters.Add(new EditableParameter { Name = dp.Name, Expression = dp.Expression, Unit = dp.Unit });
        Assert.Equal(5, component.PortCount);                                    // three strips, one short each
        component.Parameters.Single(p => p.Name == "Form").Expression = "Combline";
        component.Parameters.Single(p => p.Name == "Feed").Expression = "Coupled";
        component.Parameters.Single(p => p.Name == "N").Expression = "4";
        Assert.Equal(8, component.PortCount);                                    // four shorts and two sites
        var pins = SymbolPortDefs.For(kind, component.PortCount);
        Assert.Equal(["1", "2", "E1", "E2", "E3", "E4", "E5", "E6"], pins.Select(p => p.Name));
        Assert.Equal(pins.Select(p => (p.Name, p.LocalX, p.LocalY)),
                     BuiltInSymbols.Primitives(kind, 8).Pins.Select(p => (p.Name, (float)p.LocalX, (float)p.LocalY)));
        // Each end pin at least as far from the middle across as along, so a shunt part on it stands straight out.
        Assert.All(pins.Skip(2), p => Assert.True(Math.Abs(p.LocalY) >= Math.Abs(p.LocalX)));
        component.Parameters.Single(p => p.Name == "N").Expression = "Nres";
        Assert.Equal(CoupledArrayPlan.DefaultPinCount, component.PortCount);  // not a plain number: the default's pins
    }

    /// <summary>The tapped interdigital array's artwork: each strip an MLIN per section between the taps, a feed line up
    /// from strip 1 and down from strip 3 at Tap from their shorts, the ports at the feeds' far ends and each end pin on
    /// its strip's end. A pseudo-interdigital group's far wall stands G beyond the strips' ends, from strip 2's centre to
    /// strip 4's.</summary>
    [Fact]
    public void TheArtwork_IsThePlansSections_WithEachEndPinOnItsStripsEnd()
    {
        var cell = PassivePlanPCells.GenerateCoupledArray(new Dictionary<string, PCellValue>
        {
            ["N"] = 3.0, ["W"] = 1e-3, ["S"] = 0.5e-3, ["L"] = 16e-3, ["Tap"] = 3e-3, ["Wf"] = 1.2e-3,
            ["Form"] = PCellValue.Text("Interdigital"), ["Feed"] = PCellValue.Text("Tapped"),
        }, ShippedTechnologies.Load(ShippedTechnologies.DefaultId), PCellLayerSelection.Default);
        Assert.Null(cell.Diagnostics);
        Assert.Equal(["1", "2", "E1", "E2", "E3"], cell.Pins.Select(p => p.Name));
        Assert.Equal(3 * 2 + 2, cell.Shapes.OfType<RectShape>().Count());     // three strips in two sections, two feeds
        Assert.Equal((3_000_000L, 500_000L + 1_200_000L), (cell.Pins[0].X, cell.Pins[0].Y));
        Assert.Equal((3_000_000L, -3_500_000L - 1_200_000L), (cell.Pins[1].X, cell.Pins[1].Y));
        Assert.Equal([(0L, 0L), (16_000_000L, -1_500_000L), (0L, -3_000_000L)], cell.Pins.Skip(2).Select(p => (p.X, p.Y)));

        var pseudo = PassivePlanPCells.GenerateCoupledArray(new Dictionary<string, PCellValue>
        {
            ["N"] = 6.0, ["W"] = 1e-3, ["S"] = 0.5e-3, ["L"] = 16e-3, ["G"] = 0.5e-3,
            ["Form"] = PCellValue.Text("PseudoInterdigital"), ["Feed"] = PCellValue.Text("Coupled"),
        }, ShippedTechnologies.Load(ShippedTechnologies.DefaultId), PCellLayerSelection.Default);
        Assert.Null(pseudo.Diagnostics);
        Assert.Equal(6 + 6, pseudo.Shapes.OfType<RectShape>().Count());
        var wall = pseudo.Shapes.OfType<RectShape>().Single(r => r.X1 == 16_500_000L);
        Assert.Equal((17_500_000L, -4_500_000L, -1_500_000L), (wall.X2, wall.Y1, wall.Y2));

        var refused = PassivePlanPCells.GenerateCoupledArray(new Dictionary<string, PCellValue>
        {
            ["N"] = 3.0, ["Form"] = PCellValue.Text("Combline"), ["Ends"] = PCellValue.Text("SC SC SC"),
        }, null, PCellLayerSelection.Default);
        Assert.Empty(refused.Shapes);
        Assert.Contains("read only with Form = Custom", Assert.Single(refused.Diagnostics!));
    }

    /// <summary>Place Site Parts on the combline fixture with its parts taken off: a via on each short and a capacitor of
    /// its strip's C on each loaded end, each standing on its pin, which the sites then read as filled.</summary>
    [Fact]
    public void PlaceSiteParts_FillsTheComblinesCapacitorsAndVias()
    {
        var p = SdlChecker.Prepare("F32-coupled-array-combline", Path.Combine(_scratch, "combline"));
        var model = p.Model;
        model.Components.RemoveAll(c => c.Symbol is SymbolKind.ViaGnd or SymbolKind.Capacitor
                                        || c.Symbol == SymbolKind.Ground && c.InstanceName is not ("GND1" or "GND2"));

        var passive = Assert.Single(PassiveSites.Read(model, out string? refusal));
        Assert.Null(refusal);
        var placement = PassiveSites.PlaceParts(model, passive, p.Tech);
        Assert.Empty(placement.Notes);
        Assert.Equal([SymbolKind.ViaGnd, SymbolKind.ViaGnd, SymbolKind.Capacitor, SymbolKind.ViaGnd, SymbolKind.Capacitor,
                      SymbolKind.ViaGnd], placement.Parts.Where(c => c.Symbol != SymbolKind.Ground).Select(c => c.Symbol));
        Assert.All(placement.Parts.Where(c => c.Symbol == SymbolKind.Capacitor),
                   c => Assert.Equal("1.5", c.Parameters.Single(q => q.Name == "C").Expression));

        new PlaceSitePartsCommand(model, placement).Execute();
        Assert.All(Assert.Single(PassiveSites.Read(model, out _)).Sites, s => Assert.Single(s.Parts));
    }

    /// <summary>On the GaAs die (100 µm, εr 12.9) a via-less pseudo-interdigital array — one group of two interleaved
    /// hairpins between two feed strips, its pins only the two ports, in the proportions of Hong and Lancaster's board
    /// scaled to the die (strips 0.4·h wide, 0.24·h from the feeds and 0.8·h apart, walls 0.4·h clear, 1.33 mm long)
    /// — builds on the stack's substrate, draws, and passes a band between 10 and 40 GHz, |S21| never above one. (The
    /// published board itself is CoupledArrayTests'.)</summary>
    [Fact]
    public void APseudoInterdigitalArray_BuildsAndPassesOnTheGaAsDie()
    {
        var tech = ShippedTechnologies.Load("mmic-GaAs_2LM_100um");
        var (s, _, _) = SubstrateResolver.ResolveElectrical(tech, PCellLayerSelection.Default);
        Assert.NotNull(s);
        var sub = new MicrostripSubstrate(s!.HeightMeters, s.ThicknessMeters, s.RelativePermittivity, s.ConductivitySPerM,
                                          s.LossTangent, s.RoughnessRmsMeters);
        var spec = new CoupledArraySpec([.. Enumerable.Repeat(40e-6, 6)], [24e-6, 80e-6, 80e-6, 80e-6, 24e-6], 1.33e-3,
            CoupledArrayForm.PseudoInterdigital, CoupledArrayFeed.Coupled, null, 0, 0, [.. Enumerable.Repeat(1e-12, 6)], 40e-6);
        var plan = CoupledArrayPlan.Build(spec, sub).Plan;
        Assert.Equal(["1", "2"], plan.Pins);
        var s21 = Enumerable.Range(0, 121).Select(i => 10e9 + 0.25e9 * i).Select(f => (F: f, S: plan.S21(sub, f, 50.0).Magnitude)).ToList();
        var best = s21.MaxBy(x => x.S);
        output.WriteLine($"best |S21| {20 * Math.Log10(best.S):F2} dB at {best.F / 1e9:F1} GHz");
        Assert.All(s21, x => Assert.True(x.S <= 1.0));
        Assert.True(best.S > 0.7, $"best |S21| {best.S:F3}");

        var cell = PassivePlanPCells.GenerateCoupledArray(new Dictionary<string, PCellValue>
        {
            ["N"] = 6.0, ["W"] = 40e-6, ["S"] = 80e-6, ["S[1]"] = 24e-6, ["S[5]"] = 24e-6, ["L"] = 1.33e-3, ["G"] = 40e-6,
            ["Form"] = PCellValue.Text("PseudoInterdigital"), ["Feed"] = PCellValue.Text("Coupled"),
        }, tech, PCellLayerSelection.Default);
        Assert.Null(cell.Diagnostics);
        Assert.Equal(6 + 6, cell.Shapes.OfType<RectShape>().Count());
    }
}

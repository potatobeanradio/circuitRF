using System.Globalization;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine;
using CircuitRF.Ui.Commands.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests.SchematicSync;

/// <summary>
/// The lumped realisations and the resistive pad in the schematic and the layout (docs/design/rf-passives.md, "Lumped
/// realisations"): each layout's symbol has its plan's pins, the pad's bridged-T and splitters one list between them; Place
/// Site Parts fills every site of the F26 lumped divider and the F27 pad with parts of the plan's values, each series part
/// straight on its pins; the readout lists them; and on a GaAs die the same plan builds with MIMCAP, SPIRAL and TFR parts
/// on its sites. The fixtures' Update Layout and LVS are SdlDesignSetTests' and ComparisonTests'.
/// </summary>
public sealed class LumpedPassiveTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "crf-lumped-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Every layout's symbol pins are its plan's, in order, on the grid and apart; the bridged-T and the two
    /// splitters share one list, so a pad's pin count names it; the distributed parts' counts are as they were.</summary>
    [Fact]
    public void EveryLumpedSymbolHasItsPlansPins()
    {
        foreach (var t in LumpedPlan.All)
        {
            Assert.True(ComponentTypeRegistry.TryParseCode(t.Part, out var kind, out _), t.Part);
            var defs = SymbolPortDefs.For(kind, t.Pins.Count);
            Assert.Equal(t.Pins, defs.Select(d => d.Name));
            Assert.All(defs, d => Assert.True(d.LocalX % 100 == 0 && d.LocalY % 100 == 0, $"{t.Part} {d.Name}"));
            Assert.Equal(defs.Length, defs.Select(d => (d.LocalX, d.LocalY)).Distinct().Count());
            Assert.Equal(defs.Length, BuiltInSymbols.Primitives(kind, t.Pins.Count).Pins.Count);
        }
        var bridged = SymbolPortDefs.LumpedPins(LumpedPlan.PadBridgedT);
        Assert.Equal(bridged, SymbolPortDefs.LumpedPins(LumpedPlan.PadWye));
        Assert.Equal(bridged, SymbolPortDefs.LumpedPins(LumpedPlan.PadDelta));
        Assert.Equal([5, 7, 9, 4, 5], new[] { (SymbolKind.Mwpd, 5), (SymbolKind.Mwpd, 7), (SymbolKind.Mwpd, 9), (SymbolKind.Mblc, 4),
            (SymbolKind.Mcbl, 5) }.Select(x => SymbolPortDefs.For(x.Item1, x.Item2).Length));
    }

    /// <summary>The fixture with its site parts taken off: Place Site Parts puts back one part of the plan's value on every
    /// site, the series ones straight on their pins with no wire, and every site reads as filled; Undo takes them off.</summary>
    [Theory]
    [InlineData("F26-wilkinson-lumped")]
    [InlineData("F27-resistive-pad")]
    public void PlaceSitePartsFillsEveryLumpedSite(string design)
    {
        var p = SdlChecker.Prepare(design, Path.Combine(_scratch, design));
        var model = p.Model;
        model.Components.RemoveAll(c => c.Symbol is SymbolKind.Resistor or SymbolKind.Inductor or SymbolKind.Capacitor);

        var passive = Assert.Single(PassiveSites.Read(model, out string? refusal));
        Assert.Null(refusal);
        Assert.All(passive.Sites, s => Assert.Empty(s.Parts));
        var placement = PassiveSites.PlaceParts(model, passive, p.Tech);
        Assert.Empty(placement.Wires);
        Assert.Empty(placement.Notes);

        var command = new PlaceSitePartsCommand(model, placement);
        command.Execute();
        foreach (var site in Assert.Single(PassiveSites.Read(model, out _)).Sites)
        {
            var part = Assert.Single(site.Parts);
            Assert.Equal(site.Plan.Role switch { PlanSiteRole.L => SymbolKind.Inductor, PlanSiteRole.C => SymbolKind.Capacitor,
                _ => SymbolKind.Resistor }, part.Symbol);
            var row = part.Parameters.Single(q => q.Name is "R" or "L" or "C");
            double value = double.Parse(row.Expression, CultureInfo.InvariantCulture)
                         * (Units.Scale(UnitNormalizer.ToEngineUnit(row.Unit)) ?? 1.0);
            Assert.Equal(site.Plan.Value!.Value, value, site.Plan.Value.Value * 1e-5);
        }
        command.Undo();
        Assert.All(Assert.Single(PassiveSites.Read(model, out _)).Sites, s => Assert.Empty(s.Parts));
    }

    /// <summary>The Properties readout of a lumped divider: what it is, its interconnect, and every site's value at its
    /// span; Place Site Parts is offered.</summary>
    [Fact]
    public void TheReadoutListsTheLumpedSites()
    {
        var model = new SchematicEditModel();
        var divider = new EditableComponent { InstanceName = "WPD1", Symbol = SymbolKind.Mwpd };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(SymbolKind.Mwpd, 5))
            divider.Parameters.Add(new EditableParameter { Name = dp.Name, Expression = dp.Expression, Unit = dp.Unit, Dimension = dp.Dimension });
        divider.Parameters.Single(q => q.Name == "Realization").Expression = "Lumped";
        model.Components.Add(divider);

        var editor = new ViewModels.ParameterEditorViewModel();
        editor.SetTargetDirect(new ViewModels.SchematicViewModel(model), divider, showClose: false);

        Assert.True(editor.HasSites);
        Assert.Equal(12, divider.PortCount);
        Assert.StartsWith("Lumped (π), the values at F", editor.DividerReadoutText);
        Assert.Contains("Site R2-R3: R 100 Ω, span 1.35 mm", editor.DividerReadoutText);
        Assert.Contains("Site L1a-L1b: L 4.689 nH, span 1.35 mm", editor.DividerReadoutText);
        Assert.Contains("Site G1: C 1.876 pF", editor.DividerReadoutText);
    }

    /// <summary>On the GaAs die the lumped divider's plan builds on the die's own 50 Ω width, its cell draws with no finding,
    /// and a netlist with a TFR across R2-R3, a SPIRAL on each arm and a MIMCAP on each G pin binds to the process and
    /// simulates. The parts are the process defaults, not sized for the values (a possible enhancement).</summary>
    [Fact]
    public void OnAGaAsDie_TheLumpedDividerBuildsWithDieParts()
    {
        Directory.CreateDirectory(_scratch);
        string ws = WorkspaceCreate.Create(_scratch, "die", "mmic-GaAs_2LM_100um").WorkspaceDir;
        string dir = Path.Combine(ws, "cell");
        Directory.CreateDirectory(dir);
        string cnl = Path.Combine(dir, "t.cnl");
        File.WriteAllText(cnl, """
            Port:P1 p1 0 Num=1 Z=50 Ohm
            Port:P2 p2 0 Num=2 Z=50 Ohm
            Port:P3 p3 0 Num=3 Z=50 Ohm
            MWPD:WPD1 p1 p2 p3 r2 r3 l1a l1b l2a l2b g1 g2 g3 Z0=50 Ohm F=10 GHz Gap=100um Realization=Lumped
            TFR:R1 r2 r3 N=3 W=10um L=40um
            SPIRAL:L1 l1a l1b N=2.5 W=10um
            SPIRAL:L2 l2a l2b N=2.5 W=10um
            MIMCAP:C1 g1 0 W=50um L=50um
            MIMCAP:C2 g2 0 W=50um L=50um
            MIMCAP:C3 g3 0 W=50um L=50um
            """);
        var (lib, tb) = CnlTechnologyBinding.ReadFile(cnl);
        Assert.Empty(tb.ReadWarnings);
        using var nl = new Elaborator(lib).Elaborate(tb);
        var plan = ((CircuitRF.Core.Devices.MicrostripPlanModel)nl.Components.Single(c => c.InstancePath == "WPD1").Model).Plan;
        Assert.Equal(12, plan.Pins.Count);
        Assert.InRange(((PlanCross)plan.Sections.First(x => x is PlanCross)).W1, 20e-6, 120e-6);   // the die's 50 Ω line
        var s = SParameterEngine.Run(nl, [10e9])["S"];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                Assert.True(double.IsFinite(((System.Numerics.Complex)s[0, i, j]).Magnitude), $"S{i + 1}{j + 1}");

        var tech = Design.Layout.ShippedTechnologies.Load("mmic-GaAs_2LM_100um");
        var art = Design.Layout.PCells.PassivePlanPCells.GenerateWilkinson(new Dictionary<string, Design.Layout.PCells.PCellValue>
        {
            ["Z0"] = 50.0, ["F"] = 10e9, ["Gap"] = 100e-6, ["Realization"] = Design.Layout.PCells.PCellValue.Text("Lumped"),
        }, tech, Design.Layout.PCells.PCellLayerSelection.Default);
        Assert.Null(art.Diagnostics);
        Assert.Equal(plan.Pins, art.Pins.Select(q => q.Name));
    }
}

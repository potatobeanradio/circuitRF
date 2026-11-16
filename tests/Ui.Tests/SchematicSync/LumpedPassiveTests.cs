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

    /// <summary>A placed part of <paramref name="kind"/> with its default rows and <paramref name="rows"/> on top.</summary>
    private static EditableComponent Part(SymbolKind kind, string name, double x, params (string Name, string Value)[] rows)
    {
        var part = new EditableComponent { InstanceName = name, Symbol = kind, X = x };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(kind, 5))
            part.Parameters.Add(new EditableParameter { Name = dp.Name, Expression = dp.Expression, Unit = dp.Unit, Dimension = dp.Dimension });
        foreach (var (n, v) in rows)
        {
            if (part.Parameters.FirstOrDefault(q => q.Name == n) is { } row) row.Expression = v;
            else part.Parameters.Add(new EditableParameter { Name = n, Expression = v });
        }
        return part;
    }

    /// <summary>The words that make a part of <paramref name="t"/>'s kind draw <paramref name="t"/>.</summary>
    private static (string, string)[] WordsFor(LumpedTopology t)
        => t.Part == "MRPAD"
            ? [("Form", Enum.GetValues<ResistivePadForm>().First(f => LumpedPlan.PadTopology(f) == t).ToString())]
            : t.Part == "MWPD" ? [("Realization", "Lumped"), ("Arm", t == LumpedPlan.WilkinsonTee ? "Tee" : "Pi")]
            : [("Realization", "Lumped")];

    /// <summary>Every layout, placed: its part's own words name it, and its pins, its symbol's pins and its drawn ports are
    /// the plan's, in order, on the grid and apart. A pin count alone names a pad's layout (the bridged-T and the two
    /// splitters share one list) but never another part's, which is always the distributed one.</summary>
    [Fact]
    public void EveryLumpedSymbolHasItsPlansPins()
    {
        foreach (var t in LumpedPlan.All)
        {
            Assert.True(ComponentTypeRegistry.TryParseCode(t.Part, out var kind, out _), t.Part);
            var part = Part(kind, "X1", 0.0, WordsFor(t));
            Assert.Same(t, part.LumpedLayout());
            Assert.Equal(t.Pins.Count, part.PortCount);
            var defs = SymbolPortDefs.For(part);
            Assert.Equal(t.Pins, defs.Select(d => d.Name));
            Assert.All(defs, d => Assert.True(d.LocalX % 100 == 0 && d.LocalY % 100 == 0, $"{t.Part} {d.Name}"));
            Assert.Equal(defs.Length, defs.Select(d => (d.LocalX, d.LocalY)).Distinct().Count());
            var drawn = part.ToRenderComponent();
            Assert.Equal(t.Pins, drawn.InstanceSymbol!.Pins.Select(q => q.Name));
            Assert.Equal(t.Pins, drawn.Ports.Select(q => q.Name));
            if (kind == SymbolKind.Mrpad) Assert.Equal(defs, SymbolPortDefs.For(kind, t.Pins.Count));
            else Assert.DoesNotContain(SymbolPortDefs.For(kind, t.Pins.Count), d => d.Name.StartsWith('L'));
        }
        var bridged = SymbolPortDefs.LumpedPins(LumpedPlan.PadBridgedT);
        Assert.Equal(bridged, SymbolPortDefs.LumpedPins(LumpedPlan.PadWye));
        Assert.Equal(bridged, SymbolPortDefs.LumpedPins(LumpedPlan.PadDelta));
    }

    /// <summary>A six-section ring divider and a lumped T divider side by side: both have fifteen pins, 3 + 2·6, and each
    /// keeps its own. The ring's pins are its sections' resistor pairs and its symbol is the ring, with no per-instance glyph;
    /// the T's are its layout's on its box. Each one's generated cell meets its own symbol's pins, so Update Layout makes both,
    /// and the schematic binds each one's nets in its own order.</summary>
    [Fact]
    public void ASixSectionRingAndALumpedTeeKeepTheirOwnPins()
    {
        var ring = Part(SymbolKind.Mwpd, "WPD1", 0.0, ("N", "6"));
        var tee = Part(SymbolKind.Mwpd, "WPD2", 4000.0, ("Realization", "Lumped"), ("Arm", "Tee"));
        Assert.Equal((15, 15), (ring.PortCount, tee.PortCount));

        Assert.Null(ring.LumpedLayout());
        Assert.Equal(WilkinsonPlan.PinsFor(6), SymbolPortDefs.For(ring).Select(d => d.Name));
        var ringDrawn = ring.ToRenderComponent();
        Assert.Null(ringDrawn.InstanceSymbol);
        Assert.Equal(WilkinsonPlan.PinsFor(6), ringDrawn.Ports.Select(q => q.Name));
        Assert.Equal(SymbolPortDefs.For(ring).Select(d => (d.LocalX, d.LocalY)),
            BuiltInSymbols.Primitives(SymbolKind.Mwpd, 15).Pins.Select(q => ((float)q.LocalX, (float)q.LocalY)));

        Assert.Same(LumpedPlan.WilkinsonTee, tee.LumpedLayout());
        Assert.Equal(LumpedPlan.WilkinsonTee.Pins, SymbolPortDefs.For(tee).Select(d => d.Name));
        var teeDrawn = tee.ToRenderComponent();
        Assert.Same(BuiltInSymbols.PrimitivesForLumped(SymbolKind.Mwpd, LumpedPlan.WilkinsonTee), teeDrawn.InstanceSymbol);
        Assert.Equal(LumpedPlan.WilkinsonTee.Pins, teeDrawn.Ports.Select(q => q.Name));
        Assert.NotEqual(ring.GetPortWorldCoord(14).X - ring.X, tee.GetPortWorldCoord(14).X - tee.X);

        Directory.CreateDirectory(_scratch);
        foreach (var (rows, pins) in new[]
        {
            (new Dictionary<string, Design.Layout.PCells.PCellValue> { ["N"] = 6.0 }, WilkinsonPlan.PinsFor(6)),
            (new Dictionary<string, Design.Layout.PCells.PCellValue>
            {
                ["Realization"] = Design.Layout.PCells.PCellValue.Text("Lumped"), ["Arm"] = Design.Layout.PCells.PCellValue.Text("Tee"),
            }, LumpedPlan.WilkinsonTee.Pins),
        })
        {
            string cell = Design.Layout.PCells.GeneratedCellStore.GetOrCreate(_scratch, "MWPD", rows, null, null,
                Design.Layout.PCells.PCellLayerSelection.Default, out _);
            Assert.True(Directory.Exists(cell));
            Assert.Equal(pins, Design.Layout.PCells.PassivePlanPCells.GenerateWilkinson(rows, null,
                Design.Layout.PCells.PCellLayerSelection.Default).Pins.Select(q => q.Name));
        }

        var model = new SchematicEditModel();
        model.Components.Add(ring);
        model.Components.Add(tee);
        var extracted = SchematicCircuit.Extract(model, "tb").TestBench.Instances;
        Assert.Equal((15, 15), (extracted.Single(i => i.InstanceName == "WPD1").NetBindings.Count,
                                extracted.Single(i => i.InstanceName == "WPD2").NetBindings.Count));
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

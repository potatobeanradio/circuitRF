using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Design.Layout.Lvs;
using CircuitRF.Diagnostics;
using CircuitRF.Ui.Commands.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests.SchematicSync;

/// <summary>
/// A planned passive's sites (docs/design/rf-passives.md, "Sites"): an optional <c>Footprint</c> sets the site span the
/// run, a netlist, Update Layout and LVS read; a part with another footprint is a misfit Update Layout reports and an LVS
/// finding names; and Place site parts fills an empty site with a part of the readout's value, undoably.
/// </summary>
public sealed class PassiveSiteTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "crf-sites-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { /* best effort */ }
    }

    private static double Span(string footprint)
        => MicrostripSubstrateInjection.SiteSpanMeters(footprint, null, null, out _)!.Value;

    /// <summary>The F8 divider with <paramref name="divider"/> on WPD1 (none for null) and <paramref name="resistor"/>
    /// on R1.</summary>
    private SdlChecker.Prepared Divider(string? divider, string resistor, string scratch = "a")
    {
        var p = SdlChecker.Prepare("F8-wilkinson-divider", Path.Combine(_scratch, scratch));
        if (divider is not null)
            p.Model.Components.Single(c => c.InstanceName == "WPD1").Parameters.Add(
                new EditableParameter { Name = "Footprint", Expression = divider });
        p.Model.Components.Single(c => c.InstanceName == "R1").Parameters.Single(q => q.Name == "Footprint").Expression = resistor;
        return p;
    }

    private static CircuitRF.Core.Design.ParameterAssignment? Row(CircuitRF.Core.Design.Instance inst, string name) => inst.Overrides.LastOrDefault(o => o.Name == name);

    /// <summary>The span is where a line meets each land's outer edge: 1.35 mm for an 0402 at nominal density, the
    /// divider's default Gap; nothing for no footprint; a refusal for one that is not a land.</summary>
    [Fact]
    public void AFootprintsSpanIsItsLandsOuterSpan()
    {
        Assert.Equal(1.35e-3, Span("smt:0402"), 12);
        Assert.Equal(Span("smt:0402"), Span("smt:0402@N"));
        Assert.True(Span("smt:0603") > Span("smt:0402"));
        Assert.Null(MicrostripSubstrateInjection.SiteSpanMeters("", null, null, out string? none));
        Assert.Null(none);
        Assert.Null(MicrostripSubstrateInjection.SiteSpanMeters("smt:9999", null, null, out string? refusal));
        Assert.NotNull(refusal);
    }

    /// <summary><c>Footprint = smt:0603</c> moves the gap the run and a hand-written netlist read to that land's span;
    /// no footprint leaves the extracted line exactly as it was.</summary>
    [Fact]
    public void AFootprintMovesTheGapTheRunAndANetlistRead()
    {
        var p = Divider(null, "smt:0402");
        var plain = SchematicCircuit.Extract(p.Model, "tb").TestBench.Instances.Single(i => i.InstanceName == "WPD1");
        Assert.Equal(("1.35", "mm"), (Row(plain, "Gap")!.Expression, Row(plain, "Gap")!.Unit));
        Assert.Null(Row(plain, "Footprint"));

        var withFootprint = Divider("smt:0603", "smt:0603", "b");
        var spanned = SchematicCircuit.Extract(withFootprint.Model, "tb").TestBench.Instances.Single(i => i.InstanceName == "WPD1");
        Assert.Equal(Span("smt:0603"), double.Parse(Row(spanned, "Gap")!.Expression, System.Globalization.CultureInfo.InvariantCulture), 15);
        Assert.Null(Row(spanned, "Footprint"));           // consumed: the line carries the span alone

        string cnl = Path.Combine(withFootprint.Workspace, "divider.cnl");
        File.WriteAllText(cnl, "MWPD:WPD1 a b c d e Z0=50 F=2.4GHz Gap=1.35mm Footprint=smt:0603\n");
        var (_, tb) = CnlTechnologyBinding.ReadFile(cnl);
        Assert.Equal(Span("smt:0603"), double.Parse(Row(tb.Instances.Single(), "Gap")!.Expression,
            System.Globalization.CultureInfo.InvariantCulture), 15);
    }

    /// <summary>Update Layout lays the 0603 across the gap a divider with <c>Footprint = smt:0603</c> closes on, and LVS
    /// compares clean; the resistor's footprint changed to 0402 afterwards is one finding naming the site and both
    /// footprints, and laid out afresh it is one misfit (the existing bridge report).</summary>
    [Fact]
    public void AMisfitOnASiteIsReportedByUpdateLayoutAndNamedByLvs()
    {
        var p = Divider("smt:0603", "smt:0603");
        var layout = p.Applied(p.Plan());
        Assert.Empty(SdlChecker.Verify(p, layout));
        Assert.DoesNotContain(LvsRun.Run(p.CellDir).Findings, f => f.Severity == DiagnosticSeverity.Error);

        p.Model.Components.Single(c => c.InstanceName == "R1").Parameters.Single(q => q.Name == "Footprint").Expression = "smt:0402";
        SchematicPersistence.SaveToFile(p.SchematicPath, p.Model, Path.GetFileNameWithoutExtension(p.SchematicPath));
        var finding = Assert.Single(LvsRun.Run(p.CellDir).Findings, f => f.Severity == DiagnosticSeverity.Error);
        Assert.Equal("lvs.property.site-footprint", finding.Id);
        Assert.Equal(("WPD1", "R2-R3", "R1", "smt:0603", "smt:0402"),
            ((string)finding.Diagnostic.Arguments["passive"]!, (string)finding.Diagnostic.Arguments["site"]!,
             (string)finding.Diagnostic.Arguments["part"]!, (string)finding.Diagnostic.Arguments["wants"]!,
             (string)finding.Diagnostic.Arguments["has"]!));

        var misfit = Divider("smt:0603", "smt:0402", "c").Plan();
        Assert.Contains("R1 bridges", Assert.Single(misfit.Lines, l => l.Kind == SchematicLayoutPlanner.ReportKind.LoopGap).Text);
    }

    /// <summary>Place site parts on a divider whose resistor was taken off: one resistor of 2·Z0 laid straight on R2-R3,
    /// on the land that spans the gap, which the site then reads as filled; Undo takes it off again.</summary>
    [Fact]
    public void PlaceSitePartsFillsAnEmptySiteUndoably()
    {
        var p = Divider(null, "smt:0402");
        var model = p.Model;
        model.Components.Remove(model.Components.Single(c => c.InstanceName == "R1"));

        var passive = Assert.Single(PassiveSites.Read(model, out string? refusal));
        Assert.Null(refusal);
        Assert.Empty(Assert.Single(passive.Sites).Parts);

        var placement = PassiveSites.PlaceParts(model, passive, p.Tech);
        var part = Assert.Single(placement.Parts);
        Assert.Empty(placement.Wires);
        Assert.Equal((SymbolKind.Resistor, "100", "smt:0402@N"),
            (part.Symbol, part.Parameters.Single(q => q.Name == "R").Expression, part.Footprint));

        var command = new PlaceSitePartsCommand(model, placement);
        command.Execute();
        Assert.Same(part, Assert.Single(Assert.Single(Assert.Single(PassiveSites.Read(model, out _)).Sites).Parts));
        command.Undo();
        Assert.DoesNotContain(part, model.Components);
        Assert.Empty(Assert.Single(Assert.Single(PassiveSites.Read(model, out _)).Sites).Parts);
    }

    /// <summary>On the balun: a via on each grounded end, standing on its pin, and the load across the outputs left with a
    /// note.</summary>
    [Fact]
    public void PlaceSitePartsPutsAViaOnEachGroundOfTheBalun()
    {
        var p = SdlChecker.Prepare("F12-coupled-line-balun", Path.Combine(_scratch, "balun"));
        var model = p.Model;
        model.Components.RemoveAll(c => c.Symbol == SymbolKind.ViaGnd);

        var placement = PassiveSites.PlaceParts(model, Assert.Single(PassiveSites.Read(model, out _)), p.Tech);
        Assert.Equal([SymbolKind.ViaGnd, SymbolKind.ViaGnd], placement.Parts.Select(c => c.Symbol));
        Assert.Contains("CBL1 2-3", Assert.Single(placement.Notes));

        new PlaceSitePartsCommand(model, placement).Execute();
        var sites = Assert.Single(PassiveSites.Read(model, out _)).Sites;
        Assert.All(sites.Where(s => s.Plan.Role == PlanSiteRole.Via), s => Assert.Single(s.Parts));
    }

    /// <summary>The Properties editor offers a divider the Footprint row, lists its site at the footprint's span, and
    /// offers Place site parts.</summary>
    [Fact]
    public void TheReadoutListsTheSiteAtTheFootprintsSpan()
    {
        var model = new SchematicEditModel();
        var divider = new EditableComponent { InstanceName = "WPD1", Symbol = SymbolKind.Mwpd };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(SymbolKind.Mwpd, 5))
            divider.Parameters.Add(new EditableParameter { Name = dp.Name, Expression = dp.Expression, Unit = dp.Unit, Dimension = dp.Dimension });
        divider.Parameters.Add(new EditableParameter { Name = "Footprint", Expression = "smt:0603" });
        model.Components.Add(divider);

        var editor = new ViewModels.ParameterEditorViewModel();
        editor.SetTargetDirect(new ViewModels.SchematicViewModel(model), divider, showClose: false);

        Assert.True(editor.ShowFootprintRow);
        Assert.True(editor.HasSites);
        string span = (Span("smt:0603") * 1e3).ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Contains($"Site R2-R3: R 100 Ω, span {span} mm", editor.DividerReadoutText);
    }

    /// <summary>A route's plan drawn by <see cref="PassivePlanPCells.Draw"/>: its two ends where the route says it starts and
    /// ends, and its copper exactly the extent the route reports.</summary>
    [Theory]
    [InlineData(PlanCorner.Miter, 2, 0.0)]
    [InlineData(PlanCorner.Square, 3, 90.0)]
    public void ARoutesDrawnCopperIsItsSections(PlanCorner corner, int turns, double heading)
    {
        var s = new MicrostripSubstrate(0.508e-3, 35e-6, 3.66, 5.8e7, 0.0037);
        var r = PlanRoute.Build(1e-3, -2e-3, heading, 1.1e-3, Math.PI / 2.0, 2.4e9, s, PlanRouteStyle.Meander, corner, turns,
            pitch: 4.4e-3);
        var drawn = PassivePlanPCells.Draw(new PassivePlan(["1", "2"], r.Sections, r.NextNode - 1), null, PCellLayerSelection.Default);

        long Dbu(double m) => PCellUnits.MetresToDbu(m, LayoutUnits.DefaultDbuPerMicron);
        var (start, end) = (drawn.Pins.Single(q => q.Name == "1"), drawn.Pins.Single(q => q.Name == "2"));
        Assert.True(Math.Abs(start.X - Dbu(1e-3)) <= 1 && Math.Abs(start.Y - Dbu(-2e-3)) <= 1);
        Assert.True(Math.Abs(end.X - Dbu(r.EndX)) <= 1 && Math.Abs(end.Y - Dbu(r.EndY)) <= 1);

        var boxes = drawn.Shapes.Select(LayoutGeometry.BboxOf).ToList();
        Assert.True(Math.Abs(boxes.Min(b => b.MinX) - Dbu(r.MinX)) <= 2 && Math.Abs(boxes.Max(b => b.MaxX) - Dbu(r.MaxX)) <= 2
                 && Math.Abs(boxes.Min(b => b.MinY) - Dbu(r.MinY)) <= 2 && Math.Abs(boxes.Max(b => b.MaxY) - Dbu(r.MaxY)) <= 2,
            $"copper {boxes.Min(b => b.MinX)}..{boxes.Max(b => b.MaxX)} x {boxes.Min(b => b.MinY)}..{boxes.Max(b => b.MaxY)}, "
          + $"route {Dbu(r.MinX)}..{Dbu(r.MaxX)} x {Dbu(r.MinY)}..{Dbu(r.MaxY)}");
    }
}

using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Design.Layout.Lvs;
using CircuitRF.Diagnostics;
using CircuitRF.Ui.Commands.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests.SchematicSync;

/// <summary>
/// The ladder's lumped sites (MLAD, docs/design/rf-passives.md §3.11) in the schematic and the layout: a series site is a
/// pair of G pins the copper stops a site span short between, which Update Layout lands the part across and LVS compares;
/// the readout lists it, a <c>Footprint</c> sets its span, and Place Site Parts fills it.
/// </summary>
public sealed class LadderSiteTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "crf-ladsite-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>F25 as Update Layout places it compares clean, the 0402 across G1-G2; the list changed on the schematic
    /// afterwards is exactly one property mismatch naming Sections.</summary>
    [Fact]
    public void ALadderWithASiteComparesCleanAndASectionsMismatchIsAFinding()
    {
        var p = SdlChecker.Prepare("F30-ladder-site", _scratch);
        var layout = p.Applied(p.Plan());
        Assert.Empty(SdlChecker.Verify(p, layout));
        Assert.DoesNotContain(LvsRun.Run(p.CellDir).Findings, f => f.Severity == DiagnosticSeverity.Error);

        p.Model.Components.Single(c => c.InstanceName == "LAD1").Parameters.Single(q => q.Name == "Sections").Expression =
            "line(W=1.1mm, L=5mm); site(role=C, value=12pF); line(W=1.1mm, L=5mm)";
        SchematicPersistence.SaveToFile(p.SchematicPath, p.Model, Path.GetFileNameWithoutExtension(p.SchematicPath));

        var mismatch = Assert.Single(LvsRun.Run(p.CellDir).Findings.Where(f => f.Severity == DiagnosticSeverity.Error));
        Assert.Equal("lvs.property.mismatch", mismatch.Id);
        Assert.Equal("Sections", mismatch.Diagnostic.Arguments["name"]);
    }

    /// <summary>Place Site Parts on the ladder whose capacitor was taken off: one capacitor of the site's value on the land
    /// that spans it, standing beside G1-G2 with a wire to each, which the site then reads as filled.</summary>
    [Fact]
    public void PlaceSitePartsFillsTheLaddersSite()
    {
        var p = SdlChecker.Prepare("F30-ladder-site", _scratch);
        var model = p.Model;
        model.Components.Remove(model.Components.Single(c => c.InstanceName == "C1"));

        var passive = Assert.Single(PassiveSites.Read(model, out string? refusal));
        Assert.Null(refusal);
        var site = Assert.Single(passive.Sites);
        Assert.Equal(("G1-G2", PlanSiteRole.C), (site.Plan.Name, site.Plan.Role));
        Assert.Equal(10.0, site.Plan.Value!.Value * 1e12, 9);
        Assert.Empty(site.Parts);

        var placement = PassiveSites.PlaceParts(model, passive, p.Tech);
        var part = Assert.Single(placement.Parts);
        Assert.Equal((SymbolKind.Capacitor, "smt:0402@N"), (part.Symbol, part.Footprint));
        Assert.Equal(2, placement.Wires.Count);

        new PlaceSitePartsCommand(model, placement).Execute();
        Assert.Same(part, Assert.Single(Assert.Single(Assert.Single(PassiveSites.Read(model, out _)).Sites).Parts));
    }

    /// <summary>The ladder draws its radial ends through MRADIAL's generator, one outline each (a butterfly's two sectors
    /// in one), its sites' pads and pins, and every G pin in list order.</summary>
    [Fact]
    public void TheLadderDrawsItsRadialEndsAndItsSitesPads()
    {
        var art = Design.Layout.PCells.PassivePlanPCells.GenerateLadder(new Dictionary<string, Design.Layout.PCells.PCellValue>
        {
            ["Sections"] = Design.Layout.PCells.PCellValue.Text(
                "site(role=L, value=8nH); stub(W=0.6mm, L=1mm, end=radial, Ro=8mm, side=both); site(role=C, value=6pF, type=shunt); "
              + "stub(W=0.6mm, L=3mm, end=butterfly, Ro=6mm, side=down); site(role=L, value=8nH)"),
        }, null, Design.Layout.PCells.PCellLayerSelection.Default);
        Assert.Null(art.Diagnostics);
        Assert.Equal(["1", "2", "G1", "G2", "G3", "G4", "G5"], art.Pins.Select(q => q.Name));
        Assert.Equal(3, art.Shapes.OfType<Design.Layout.CurveShape>().Count());
        // A series site's two pins face each other a site span apart.
        var (g1, g2) = (art.Pins.Single(q => q.Name == "G1"), art.Pins.Single(q => q.Name == "G2"));
        Assert.Equal((1_350_000L, 0L, 0.0, 180.0), (g2.X - g1.X, g2.Y - g1.Y, g1.OutwardDirectionDeg, g2.OutwardDirectionDeg));
    }

    /// <summary>The readout lists the site with its value and span; a Footprint on the ladder sets the span from its land,
    /// and the readout offers Place Site Parts.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("smt:0603")]
    public void TheReadoutListsTheSiteAtItsSpan(string? footprint)
    {
        var model = new SchematicEditModel();
        var ladder = new EditableComponent { InstanceName = "LAD1", Symbol = SymbolKind.Mlad };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(SymbolKind.Mlad, 2))
            ladder.Parameters.Add(new EditableParameter { Name = dp.Name, Expression = dp.Expression, Unit = dp.Unit, Dimension = dp.Dimension });
        ladder.Parameters.Single(q => q.Name == "Sections").Expression =
            "line(W=1.1mm, L=5mm); site(role=C, value=10pF); stub(W=0.6mm, L=1mm, end=radial, Ro=8mm); site(role=L, value=12nH, type=shunt)";
        if (footprint is not null) ladder.Parameters.Add(new EditableParameter { Name = "Footprint", Expression = footprint });
        model.Components.Add(ladder);
        Assert.Equal(5, ladder.PortCount);

        var editor = new ViewModels.ParameterEditorViewModel();
        editor.SetTargetDirect(new ViewModels.SchematicViewModel(model), ladder, showClose: false);

        Assert.True(editor.HasSites);
        Assert.Equal("", editor.DividerReadoutNote);
        double span = footprint is null ? LadderPlan.DefaultGap
            : MicrostripSubstrateInjection.SiteSpanMeters(footprint, null, null, out _)!.Value;
        string text = editor.DividerReadoutText;
        Assert.Contains("2 site, series: C 10 pF", text);
        Assert.Contains("3 stub, radial, up: ", text);
        Assert.Contains(", then a sector of Ro 8 mm, 60°: ", text);
        Assert.Contains("4 site, shunt: L 12 nH", text);
        Assert.Contains($"Site G1-G2: C 10 pF, span {(span * 1e3).ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)} mm", text);
        Assert.Contains("Site G3: L 12 nH", text);
    }
}

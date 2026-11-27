// Owner: PCD-12 (brief-pcd-12 R-pcd12-2 to R-pcd12-4). A three-pole tapped hairpin, 0.1 dB Chebyshev, 2.2 to 2.6 GHz (the
// Filter Designer's default specification), on the microstrip-0508 scratch workspace.

using CircuitRF.Cli;
using CircuitRF.Core.Synthesis.Filters;
using CircuitRF.Core.Systems;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Filters;
using CircuitRF.Design.PCellDesigner;
using CircuitRF.Design.Schematic;
using CircuitRF.Diagnostics;
using CircuitRF.Ui.Filters;
using CircuitRF.Ui.PCellDesigner;

namespace CircuitRF.Ui.Tests.PCellDesigner;

public class Pcd12FilterCellTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static readonly FilterSpec Spec = new(FilterResponse.Chebyshev, FilterBand.Bandpass, 3, 2.2e9, 2.6e9, 0.1);
    private static readonly TappedHairpinRealiser Realiser = new();

    /// <summary>The hairpin realised once for the process, on the workspace's board.</summary>
    private static readonly Lazy<FilterRealisation> Hairpin = new(() =>
    {
        using var ws = new Pcd5Workspace();
        return Realiser.Realise(Spec, FilterSynthesisService.Substrate(ws.Tech, null, null, out _));
    });

    private static IEnumerable<Diagnostic> Errors(IEnumerable<Diagnostic> d) => d.Where(f => f.Severity == DiagnosticSeverity.Error);

    /// <summary>R-pcd12-2: Make PCell… in the Filter Designer makes FilterPCell1, a designed cell holding the hairpin and two
    /// Ports, each length, gap and width a parameter bound into the part with a ±20 % range; the design check passes and it
    /// exports. <c>filter --pcell</c> makes the same cell.</summary>
    [Fact]
    public async Task MakePCell_TurnsTheRealisedHairpin_IntoADesignedCellThatChecksAndExports()
    {
        using var ws = new Pcd5Workspace();
        var vm = new FilterDesignerViewModel(null, realisers: [Realiser], technology: ws.Tech) { WorkspaceRoot = () => ws.Root };
        string? opened = null;
        vm.OpenDesigner = (cell, _) => opened = cell;
        await vm.Realising;
        Assert.True(vm.MakePCellCommand.CanExecute(null), vm.MakePCellTip);
        vm.MakePCellCommand.Execute(null);
        await vm.Work;
        string cell = Path.Combine(ws.Root, "FilterPCell1");
        Assert.True(vm.LastPCell == cell, vm.Status);
        Assert.Equal(cell, opened);
        Assert.StartsWith("Made FilterPCell1:", vm.Status);
        Assert.True(PCellDesign.IsDesigned(cell));

        var ccell = CellFolder.ReadCcell(cell)!;
        string[] names = [.. ccell.Parameters.Select(p => p.Name)];
        Assert.Equal(["L1", "L2", "L3", "S2", "S3", "Sa", "Tap", "W"], names.Order(StringComparer.Ordinal));
        foreach (var p in ccell.Parameters)
        {
            double si = double.Parse(p.DefaultExpression, System.Globalization.CultureInfo.InvariantCulture) * 1e-3;   // mm
            Assert.Equal(("mm", UnitDimension.Length), (p.Unit, p.Dimension));
            Assert.Equal(0.8 * si, p.Minimum!.Value, 12);
            Assert.Equal(1.2 * si, p.Maximum!.Value, 12);
        }
        var schematic = SchematicPersistence.LoadFromFile(Path.Combine(cell, "schematic", "FilterPCell1.csch")).model;
        var part = schematic.Components.Single(c => c.InstanceName == PCellFilter.PartName);
        Assert.All(names, n => Assert.Equal(n, part.Parameters.Single(r => r.Name == n).Expression));
        Assert.Equal(2, PCellDesign.TerminalsOf(schematic).Count);
        Assert.Equal(Spec, PCellFilter.SpecOf(cell));
        Assert.NotEmpty(CircuitRF.Design.Layout.LayoutPersistence.LoadFromFile(Path.Combine(cell, "layout", "FilterPCell1.clay")).Instances);
        foreach (var p in ccell.Parameters)
            output.WriteLine($"{p.Name} = {p.DefaultExpression} {p.Unit}, {p.Minimum * 1e3:0.######} to {p.Maximum * 1e3:0.######} mm");

        var findings = PCellDesignCheck.Run(cell);
        Assert.True(!Errors(findings).Any(), string.Join("\n", findings.Select(f => f.Render())));
        var package = PCellPackage.Export(cell, Path.Combine(ws.Root, "FilterPCell1.cpcell"));
        Assert.True(package.Path is not null && File.Exists(package.Path), string.Join("\n", package.Findings.Select(f => f.Render())));
        output.WriteLine($"check: {Errors(findings).Count()} errors, {findings.Count(f => f.Severity == DiagnosticSeverity.Warning)} warnings; "
                         + $"{Path.GetFileName(package.Path)} {new FileInfo(package.Path!).Length} bytes, generators {string.Join(",", package.Manifest!.Generators)}");

        // The verb: the same parameters, from the same realiser on the same board.
        using (JsonRun.Isolate())
        {
            int exit = FilterVerb.Run(["--band", "bandpass", "--order", "3", "--f1", "2.2GHz", "--f2", "2.6GHz",
                "--realiser", Realiser.Name, "--pcell", Path.Combine(ws.Root, "HairpinCli")], [Realiser]);
            Assert.True(exit == 0, string.Join("\n", JsonRun.CapturedDiagnostics.Select(d => d.Message)));
        }
        var cli = CellFolder.ReadCcell(Path.Combine(ws.Root, "HairpinCli"))!;
        Assert.Equal(ccell.Parameters.Select(p => (p.Name, p.DefaultExpression)), cli.Parameters.Select(p => (p.Name, p.DefaultExpression)));
    }

    /// <summary>R-pcd12-3: the cell keeps its spec in the .ccell (a cell made otherwise writes no Filter); its own circuit
    /// response reads back to the bench's metrics, an EM result is read the same way, and the panel shows the Filter
    /// Designer's rows.</summary>
    [Fact]
    public void ADesignedFilterCell_ReadsBackItsMetrics_FromItsCircuitAndFromAnEmResult()
    {
        using var ws = new Pcd5Workspace();
        var made = PCellFilter.Make(ws.Root, "Pcd12Metrics", Spec, Hairpin.Value, Realiser.Name);
        Assert.True(made.CellDir is not null, made.Refusal);
        Assert.Contains("\"Filter\"", File.ReadAllText(Path.Combine(made.CellDir!, CellFolder.CcellFileName)));
        Assert.DoesNotContain("\"Filter\"", File.ReadAllText(Path.Combine(PCellDesign.Create(ws.Root, "Pcd12Plain").CellDir, CellFolder.CcellFileName)));

        var circuit = PCellFilter.CircuitMetrics(made.CellDir!, ws.Root);
        Assert.True(circuit.Filter is not null, circuit.Refusal);
        // The cell is the realisation: its response is the Filter Designer's bench on the same board.
        var f = FilterSynthesisService.Frequencies(Spec, FilterPolish.Points);
        var bench = FilterSynthesisService.Response(FilterSynthesisService.Cnl(Spec, Hairpin.Value, Realiser.Name,
            FilterSynthesisService.Substrate(ws.Tech, null, null, out _), ws.Tech, f.Length, []), f, Spec.Z0);
        var em = PCellFilter.Metrics(Spec, bench, "Planar EM, 401 points");   // any S-parameters: an EM run's are read alike
        Assert.Equal(em.Filter!.CentreHz, circuit.Filter!.CentreHz, 1e-6 * Spec.CentreHz);
        Assert.Equal(em.Filter.ReturnLossDb, circuit.Filter.ReturnLossDb, 0.01);
        Assert.Equal(em.Filter.Checks.Select(c => (c.Name, c.Pass)), circuit.Filter.Checks.Select(c => (c.Name, c.Pass)));
        output.WriteLine(FilterMetricRow.Note("Circuit response", circuit.Filter) + $" centre {circuit.Filter.CentreHz / 1e9:0.0000} GHz, "
                         + $"bandwidth {circuit.Filter.BandwidthHz / 1e6:0.0} MHz, return loss {circuit.Filter.ReturnLossDb:0.00} dB, Qu {circuit.Filter.UnloadedQ:0}");

        var view = FilterMetricsView.Of(circuit);
        Assert.Equal(FilterMetricRow.Note("Circuit response", circuit.Filter), view.Note);
        Assert.Equal(circuit.Filter.Checks.Count(c => !c.Name.Contains('(')) + 2, view.SpecRows.Count);
        Assert.Contains(view.MatrixRows, r => r.Name == "k(1,2)");
        Assert.Equal("Circuit response: no spec", FilterMetricsView.Of(new PCellFilterMetrics(null, "Circuit response", "no spec")).Note);
    }

    /// <summary>R-pcd12-4: one polish pass on the cell's parameters: the defaults move, the worst return loss does not get
    /// worse, and the cell's own response at the new defaults is the polished one.</summary>
    [Fact]
    public void Polish_CorrectsADesignedHairpinCell_OnItsParameters()
    {
        using var ws = new Pcd5Workspace();
        var made = PCellFilter.Make(ws.Root, "Pcd12Polish", Spec, Hairpin.Value, Realiser.Name);
        Assert.True(made.CellDir is not null, made.Refusal);
        var before = made.Parameters.ToDictionary(p => p.Name, p => p.DefaultExpression);

        var polished = PCellFilter.Polish(made.CellDir!, ws.Root, passes: 1);
        Assert.True(polished.Refusal is null, polished.Refusal);
        Assert.True(polished.AfterDb >= polished.BeforeDb, $"{polished.BeforeDb} → {polished.AfterDb}");
        var saved = CellFolder.ReadCcell(made.CellDir!)!.Parameters.ToDictionary(p => p.Name, p => p.DefaultExpression);
        Assert.Contains(saved, p => p.Value != before[p.Key]);
        output.WriteLine($"polish: {polished.BeforeDb:0.00} dB -> {polished.AfterDb:0.00} dB; "
                         + string.Join(", ", saved.Where(p => p.Value != before[p.Key]).Select(p => $"{p.Key} {before[p.Key]} -> {p.Value}")));
        Assert.Equal(polished.AfterDb, PCellFilter.CircuitMetrics(made.CellDir!, ws.Root).Filter!.ReturnLossDb, 0.01);
    }
}

using System.Globalization;
using System.Numerics;
using System.Xml.Linq;
using Avalonia.Input;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Matching;
using CircuitRF.Core.Netlist;
using CircuitRF.Core.Systems;
using CircuitRF.Design.Filters;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.VendorLibraries;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.Filters;
using CircuitRF.Ui.Tests.VendorLibraries;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests.Filters;

/// <summary>
/// The LC Filter Designer's Vendor library entry: it is a Values choice that reveals the series pickers,
/// a placed ladder is built from the library parts, a Filter block whose Values are not Exact simulates
/// as its ladder, the mask reaches an exported plot, and the ladder pane re-fits on resize.
/// </summary>
public sealed class LcFilterVendorLibraryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "crf-lcf-vl-" + Guid.NewGuid().ToString("N"));
    private readonly string _ws;
    private readonly string _sheetDir;

    public LcFilterVendorLibraryTests()
    {
        _ws = Path.Combine(_dir, "ws");
        string lib = Path.Combine(_dir, "lib");
        _sheetDir = Path.Combine(_ws, "cells", "top", "schematic");
        Directory.CreateDirectory(_sheetDir);
        VendorLibraryFixture.Write(lib, physical: true);
        WorkspacePersistence.SaveToFile(Path.Combine(_ws, ".cws"),
            new CwsFile { VendorLibraries = [new CwsVendorLibraryRef { Name = "Test", Path = lib }] });
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    /// <summary>A 3 GHz third-order Butterworth lowpass: values inside the fixture's inductor and capacitor ranges.</summary>
    private async Task<LcFilterDesignerViewModel> Designed(FilterSeriesKind values, bool library = true)
    {
        var vm = new LcFilterDesignerViewModel
        {
            WorkspaceRoot = _ws, Response = FilterResponse.Butterworth, Form = NetworkForm.Lowpass, Unit = "GHz",
        };
        vm.OrderMode = FilterOrderMode.Specify;
        vm.OrderText = "3";
        vm.FaText = "3";
        vm.FbText = "9";
        // A mask the fixture's parts meet as modelled (their parasitics move a 3.01 dB edge).
        vm.ApText = "4";
        vm.AsText = "20";
        vm.SeriesChoice = vm.SeriesChoices.Single(c => c.Value == values);
        if (values == FilterSeriesKind.VendorLibrary && library)
        {
            vm.InductorChoice = vm.InductorSeries.Single(s => s.Label == "series-l");
            vm.CapacitorChoice = vm.CapacitorSeries.Single(s => s.Label == "series-a");
        }
        await vm.DesignAsync(0);
        Assert.True(vm.Elements.Count > 0, vm.Status);
        if (values == FilterSeriesKind.VendorLibrary && library) Assert.True(vm.MaskMet, vm.Status);
        return vm;
    }

    private SchematicViewModel Sheet(out SchematicEditModel model)
    {
        model = new SchematicEditModel { GridSize = 100, GridSnap = true, SchematicDirectory = _sheetDir };
        return new SchematicViewModel(model);
    }

    // ── 1: Vendor library is a Values entry, and reveals the two pickers only then ──────────────

    [Fact]
    public async Task VendorLibraryIsAValuesEntry_AndOnlyItShowsTheSeriesPickers()
    {
        var vm = new LcFilterDesignerViewModel { WorkspaceRoot = _ws };
        Assert.Contains(vm.SeriesChoices, c => c.Label == "Vendor library" && c.Value == FilterSeriesKind.VendorLibrary);
        Assert.False(vm.ShowLibraryPickers);

        vm.SeriesChoice = vm.SeriesChoices.Single(c => c.Value == FilterSeriesKind.VendorLibrary);
        Assert.Equal(FilterSeriesKind.VendorLibrary, vm.Series);
        Assert.True(vm.LibraryAvailable);     // choosing the entry indexed the libraries
        Assert.True(vm.ShowLibraryPickers);
        Assert.Contains(vm.InductorSeries, s => s.Label == "series-l");

        vm.SeriesChoice = vm.SeriesChoices.Single(c => c.Value == FilterSeriesKind.E12);
        Assert.False(vm.ShowLibraryPickers);
        await Task.CompletedTask;
    }

    [Fact]
    public void TheWindowHasNoLoadButton_AndThePickersFollowShowLibraryPickers()
    {
        string xaml = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Ui", "Views", "Filters", "LcFilterDesignerWindow.axaml"));
        Assert.DoesNotContain("Load library series", xaml);
        Assert.DoesNotContain("LoadLibrariesCommand", xaml);
        var doc = XDocument.Parse(xaml);
        foreach (string items in (string[])["{Binding InductorSeries}", "{Binding CapacitorSeries}"])
        {
            var combo = doc.Descendants().Single(e => (string?)e.Attribute("ItemsSource") == items);
            Assert.Equal("{Binding ShowLibraryPickers}", (string?)combo.Attribute("IsVisible"));
        }
    }

    [Fact]
    public async Task AnIecSeriesIgnoresTheLibraryChoices()
    {
        var vm = await Designed(FilterSeriesKind.VendorLibrary);
        Assert.All(vm.Elements, r => Assert.StartsWith("TST", r.Part));
        vm.SeriesChoice = vm.SeriesChoices.Single(c => c.Value == FilterSeriesKind.E12);
        await vm.DesignAsync(0);
        Assert.All(vm.Elements, r => Assert.Equal("", r.Part));
        Assert.Empty(vm.LadderParts);
        var applied = vm.FilterAssignments()!;
        Assert.Equal("", applied.Single(a => a.Name == "LSeries").Expression);
        Assert.Equal("", applied.Single(a => a.Name == "Parts").Expression);
    }

    // ── 3: Place filter puts the library parts on the sheet ────────────────────────────────────

    [Fact]
    public async Task PlaceFilter_WithAVendorLibrary_PlacesTheLibraryParts()
    {
        var vm = await Designed(FilterSeriesKind.VendorLibrary);
        Assert.Equal(vm.Elements.Count, vm.LadderParts.Count);

        var sheet = Sheet(out var model);
        vm.SchematicProvider = () => sheet;
        sheet.PointerWorldProvider = () => (0, 0);
        vm.PlaceFilter();
        Assert.True(sheet.IsPasteArmed);
        sheet.OnPointerPressed(0, 0, Avalonia.Input.KeyModifiers.None);

        // A sheet with no ports gets the ladder ending in ports 1 and 2; the elements are the library parts.
        Assert.Equal(2, model.Components.Count(c => c.Symbol is SymbolKind.TermG or SymbolKind.Term));
        var parts = model.Components.Where(c => c.Symbol is not (SymbolKind.Ground or SymbolKind.TermG or SymbolKind.Term)).ToList();
        Assert.Equal(vm.Elements.Count, parts.Count);
        Assert.DoesNotContain(parts, c => c.Symbol is SymbolKind.Inductor or SymbolKind.Capacitor);
        foreach (var c in parts)
        {
            Assert.Equal(SymbolKind.SpiceModel, c.Symbol);
            string file = c.Parameters.Single(p => p.Name == SpiceModelSymbolProvider.FileParameter).Expression;
            Assert.StartsWith("vendorlib://Test/", file);
            string name = c.Parameters.Single(p => p.Name == SpiceModelSymbolProvider.NameParameter).Expression;
            Assert.Contains(vm.Elements, r => r.Part == name);
        }

        // It is a circuit: every part resolves to its library subcircuit.
        var extracted = NetExtractor.Extract(model);
        Assert.Empty(extracted.PartRefusals);
        Assert.All(extracted.TestBench.Instances.Where(i => i.Reference is not ("Term" or "TermG" or "Port")),
                   i => Assert.NotNull(extracted.Library.Find(i.Reference)));
    }

    // ── 4: a Filter block simulates as what its Properties say ────────────────────────────────

    private (NetExtractor.ExtractionResult Extracted, EditableComponent Filter) PlacedBlock(LcFilterDesignerViewModel vm)
    {
        var model = new SchematicEditModel { GridSize = 100, SchematicDirectory = _sheetDir };
        var filter = new EditableComponent { InstanceName = "F1", Symbol = SymbolKind.Filter };
        filter.Parameters.AddRange(vm.AppliedParameters(FilterPlacement.DefaultParameters())!);
        model.Components.Add(filter);
        return (NetExtractor.Extract(model), filter);
    }

    [Fact]
    public async Task AFilterBlockWithVendorLibraryValues_SimulatesAsTheLibraryParts()
    {
        var vm = await Designed(FilterSeriesKind.VendorLibrary);
        var (extracted, _) = PlacedBlock(vm);

        var block = Assert.Single(extracted.TestBench.Instances);
        Assert.NotEqual("Filter", block.Reference);
        var cell = extracted.Library.Find(block.Reference);
        Assert.NotNull(cell);
        Assert.Equal(vm.Elements.Count, cell!.Instances.Count);
        // Each element is the very library subcircuit the table names.
        foreach (var row in vm.Elements)
        {
            var inst = cell.Instances.Single(i => i.InstanceName == row.Name);
            Assert.Equal(row.Part, inst.Reference, StringComparer.OrdinalIgnoreCase);
            Assert.NotNull(extracted.Library.Find(inst.Reference));
        }
        // A part inside the block is not a part on the sheet: it offers no tunable of its own.
        Assert.Empty(extracted.PartTunables);

        // The block simulates exactly as the same ladder placed from the library parts (Place filter).
        using var blockNet = new Elaborator(extracted.Library).Elaborate(WithTerms(extracted.TestBench, block));
        var sheet = Sheet(out var model);
        vm.SchematicProvider = () => sheet;
        sheet.PointerWorldProvider = () => (0, 0);
        vm.PlaceFilter();
        sheet.OnPointerPressed(0, 0, KeyModifiers.None);
        // On a sheet with no ports the ladder arrives ending in ports 1 and 2, at 50 ohm for this odd-order Butterworth.
        var placed = NetExtractor.Extract(model);
        using var placedNet = new Elaborator(placed.Library).Elaborate(placed.TestBench);
        foreach (double f in (double[])[0.5e9, 2e9, 3e9, 9e9])
            Assert.Equal(S21Db(placedNet, f), S21Db(blockNet, f), 9);
    }

    [Fact]
    public async Task AFilterBlockWithAnIecSeries_SimulatesAsItsSnappedLadder()
    {
        var vm = await Designed(FilterSeriesKind.E12);
        var (extracted, _) = PlacedBlock(vm);
        var block = Assert.Single(extracted.TestBench.Instances);
        var cell = extracted.Library.Find(block.Reference)!;
        Assert.All(cell.Instances, i => Assert.Contains(i.Reference, (string[])["L", "C"]));

        // The same response as the window's own ladder netlist, which carries the snapped values.
        using var blockNet = new Elaborator(extracted.Library).Elaborate(WithTerms(extracted.TestBench, block));
        var (lib, tb) = new CnlReader().Read(vm.Cnl);
        using var windowNet = new Elaborator(lib).Elaborate(tb);
        foreach (double f in (double[])[0.5e9, 2e9, 3e9, 5e9])
            Assert.Equal(S21Db(windowNet, f), S21Db(blockNet, f), 9);
    }

    [Fact]
    public async Task AnExactOrUnlockedFilterBlock_KeepsTheIdealResponse()
    {
        var exact = await Designed(FilterSeriesKind.Exact);
        Assert.Equal("Filter", Assert.Single(PlacedBlock(exact).Extracted.TestBench.Instances).Reference);

        var vm = await Designed(FilterSeriesKind.VendorLibrary);
        var model = new SchematicEditModel { GridSize = 100, SchematicDirectory = _sheetDir };
        var filter = new EditableComponent { InstanceName = "F1", Symbol = SymbolKind.Filter };
        filter.Parameters.AddRange(vm.AppliedParameters(FilterPlacement.DefaultParameters())!);
        filter.Parameters.Single(p => p.Name == "Synthesis").Expression = "Off";
        model.Components.Add(filter);
        Assert.Equal("Filter", Assert.Single(NetExtractor.Extract(model).TestBench.Instances).Reference);
    }

    [Fact]
    public async Task AVendorLibraryPartTheWorkspaceNoLongerHas_IsSaid_AndTheBlockStaysIdeal()
    {
        var vm = await Designed(FilterSeriesKind.VendorLibrary);
        var model = new SchematicEditModel { GridSize = 100, SchematicDirectory = _sheetDir };
        var filter = new EditableComponent { InstanceName = "F1", Symbol = SymbolKind.Filter };
        filter.Parameters.AddRange(vm.AppliedParameters(FilterPlacement.DefaultParameters())!);
        filter.Parameters.Single(p => p.Name == "LSeries").Expression = "Test: no-such-series";
        model.Components.Add(filter);

        var extracted = NetExtractor.Extract(model);
        Assert.Equal("Filter", Assert.Single(extracted.TestBench.Instances).Reference);
        Assert.Contains(extracted.Conflicts, c => c.Contains("no-such-series") && c.Contains("F1"));
    }

    [Fact]
    public async Task ReopeningABlock_AppliedWithLibrarySeries_ChoosesVendorLibrary()
    {
        var vm = await Designed(FilterSeriesKind.VendorLibrary);
        var filter = new EditableComponent { InstanceName = "F1", Symbol = SymbolKind.Filter };
        filter.Parameters.AddRange(vm.AppliedParameters(FilterPlacement.DefaultParameters())!);
        Assert.Equal("VendorLibrary", filter.Parameters.Single(p => p.Name == "Values").Expression);

        // A block applied before Vendor library was a Values entry: an IEC series beside the library names.
        filter.Parameters.Single(p => p.Name == "Values").Expression = "E12";
        var reopened = new LcFilterDesignerViewModel { WorkspaceRoot = _ws };
        reopened.SeedFrom(filter);
        Assert.Equal(FilterSeriesKind.VendorLibrary, reopened.Series);
        Assert.True(reopened.ShowLibraryPickers);
        Assert.Equal("series-l", reopened.InductorChoice?.Label);
    }

    // ── 2: the mask reaches an exported plot ───────────────────────────────────────────────────

    [Fact]
    public async Task TheMaskIsOnTheContainerTheExportComposesFrom()
    {
        var vm = await Designed(FilterSeriesKind.Exact);
        Assert.Same(vm.MagnitudeMask, vm.MagnitudeContainer.Overlay);
        Assert.NotEmpty(vm.MagnitudeMask.Lines);
        Assert.NotNull(PlotExporter.Place(vm.MagnitudeContainer).Overlay);
    }

    // ── 5: the ladder pane re-fits when the window is resized ─────────────────────────────────

    [Fact]
    public void TheLadderPaneFitsOnResize_AndTheMatchDesignersDoesNot()
    {
        Assert.False(CircuitRF.Ui.Views.Match.MatchSchematicCanvas.FitOnResizeProperty
            .GetDefaultValue(typeof(CircuitRF.Ui.Views.Match.MatchSchematicCanvas)));
        string xaml = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Ui", "Views", "Filters", "LcFilterDesignerWindow.axaml"));
        var canvas = XDocument.Parse(xaml).Descendants().Single(e => e.Name.LocalName == "MatchSchematicCanvas");
        Assert.Equal("True", (string?)canvas.Attribute("FitOnResize"));
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheDesignersCurveIsThePlacedBlocksResponse_TheLibraryPartsAsTheirModels()
    {
        var vm = await Designed(FilterSeriesKind.VendorLibrary);
        Assert.Contains("SpiceModel:", vm.Cnl);
        var (extracted, _) = PlacedBlock(vm);
        var block = Assert.Single(extracted.TestBench.Instances);
        using var blockNet = new Elaborator(extracted.Library).Elaborate(WithTerms(extracted.TestBench, block));
        double[] hz = [0.5e9, 2e9, 3e9, 5e9, 9e9];
        var designer = FilterLadderCircuit.S21(vm.Cnl, hz, _ws);
        for (int i = 0; i < hz.Length; i++)
            Assert.Equal(S21Db(blockNet, hz[i]), 20 * Math.Log10(designer[i].Magnitude), 6);
    }

    [Fact]
    public async Task TheFastJudgeWithPartImpedances_IsTheCircuitOfTheParts()
    {
        var vm = await Designed(FilterSeriesKind.VendorLibrary);
        var parts = vm.LadderParts;
        Assert.NotEmpty(parts);
        var built = FilterLadder.Synthesize(FilterResponse.Butterworth, NetworkForm.Lowpass, 3, 3e9, 0, 0, 0, 50,
                                            FilterFirstElement.Parallel, 40, EllipticType.A);
        var real = built.Realization!;
        Assert.Contains(real.Elements, e => parts.ContainsKey(e.Name));
        double[] hz = [0.5e9, 2e9, 3e9, 5e9, 9e9];
        string cnl = FilterLadderCircuit.ToCnl(real.Elements, parts, real.SourceOhms, real.LoadOhms);
        var circuit = FilterLadderCircuit.S21(cnl, hz, _ws);
        var z = real.Elements.Select(e => parts.TryGetValue(e.Name, out var p) ? FilterLadderCircuit.PartImpedance(p, hz, _ws) : null).ToList();
        Assert.Contains(z, a => a is not null);
        var fast = FilterLadder.InsertionLossDb(real.Elements, real.SourceOhms, real.LoadOhms, hz, z);
        for (int i = 0; i < hz.Length; i++)
            Assert.Equal(-20 * Math.Log10(circuit[i].Magnitude), fast[i], 6);
    }

    private static TestBench WithTerms(TestBench extracted, Instance block)
    {
        var tb = new TestBench("tb");
        tb.Instances.AddRange(extracted.Instances);
        tb.Instances.Add(new Instance("T1", "Term", [block.NetBindings[0], "0"],
            [new ParameterAssignment("Num", "1"), new ParameterAssignment("Z", "50")]));
        tb.Instances.Add(new Instance("T2", "Term", [block.NetBindings[1], "0"],
            [new ParameterAssignment("Num", "2"), new ParameterAssignment("Z", "50")]));
        return tb;
    }

    private static double S21Db(ElaboratedNetlist netlist, double hz)
    {
        var cube = SParameterEngine.Run(netlist, [hz])["S"];
        int np = cube.Axes[1].Length;
        return 20 * Math.Log10(cube.ComplexValues[1 * np + 0].Magnitude);
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "circuitRF.slnx"))) d = d.Parent;
        return d!.FullName;
    }
}

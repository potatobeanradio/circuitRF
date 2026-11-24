using System.Numerics;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Matching;
using CircuitRF.Core.Netlist;
using CircuitRF.Core.Systems;
using CircuitRF.Design.Filters;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine;
using CircuitRF.Ui.Filters;
using CircuitRF.Ui.Tests.VendorLibraries;
using Xunit;

namespace CircuitRF.Ui.Tests.Filters;

/// <summary>
/// When the snapped ladder misses the mask, the Designer offers a search: other values of the
/// series element by element, other orders and floors, and for Vendor library every pairing of an
/// inductor series with a capacitor series. What it finds meets the mask, and the block keeps it.
/// </summary>
public sealed class LcFilterValueSearchTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "crf-lcf-vs-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private static async Task<LcFilterDesignerViewModel> Lowpass(
        FilterResponse response, string fp, string fs, string ap, string ripple, string astop,
        FilterSeriesKind series, string? workspace = null)
    {
        var vm = new LcFilterDesignerViewModel { WorkspaceRoot = workspace, Response = response, Form = NetworkForm.Lowpass };
        vm.OrderMode = FilterOrderMode.Minimum;
        vm.UnitA = vm.UnitB = "MHz";
        vm.FaText = fp; vm.FbText = fs;
        vm.ApText = ap; vm.RippleText = ripple; vm.AsText = astop;
        vm.SeriesChoice = vm.SeriesChoices.Single(c => c.Value == series);
        if (series == FilterSeriesKind.VendorLibrary)
        {
            vm.InductorChoice = vm.InductorSeries[0];
            vm.CapacitorChoice = vm.CapacitorSeries[0];
        }
        await vm.DesignAsync(0);
        return vm;
    }

    /// <summary>|S21| of a ladder netlist from the engine, the response the Designer checks.</summary>
    private static Complex S21(string cnl, double hz)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var ds = SParameterEngine.Run(new Elaborator(lib).Elaborate(tb), [hz]);
        int np = ds["S"].Axes[1].Length;
        return ds["S"].ComplexValues[1 * np + 0];
    }

    private static double LossDb(string cnl, double hz) => -20 * Math.Log10(S21(cnl, hz).Magnitude);

    private static Dictionary<string, string> Applied(LcFilterDesignerViewModel vm) =>
        vm.FilterAssignments()!.ToDictionary(a => a.Name, a => a.Expression);

    [Fact]
    public void TheFastLadderResponseIsTheEnginesResponse()
    {
        var built = FilterLadder.Synthesize(FilterResponse.Elliptic, NetworkForm.Bandpass, 5, 0, 0.9e9, 1.1e9, 0.2, 50,
                                            FilterFirstElement.Parallel, 40, EllipticType.A);
        var real = built.Realization!;
        string cnl = FilterLadder.ToCnl(real.Elements, real.SourceOhms, real.LoadOhms);
        double[] hz = [0.5e9, 0.85e9, 1e9, 1.08e9, 1.5e9];
        var fast = FilterLadder.InsertionLossDb(real.Elements, real.SourceOhms, real.LoadOhms, hz);
        for (int i = 0; i < hz.Length; i++)
            Assert.Equal(LossDb(cnl, hz[i]), fast[i], 6);
    }

    [Fact]
    public async Task ASnappedMissOffersTheSearch_AndTheSearchFindsE12ValuesThatMeetTheMask()
    {
        var vm = await Lowpass(FilterResponse.Chebyshev, "1000", "1300", "0.5", "0.5", "30", FilterSeriesKind.E12);
        Assert.False(vm.MaskMet);
        Assert.True(vm.CanSearchValues, vm.Status);
        Assert.Contains("E12", vm.SearchQuestion);

        await vm.SearchValuesAsync();

        Assert.True(vm.MaskMet, vm.Status);
        Assert.False(vm.CanSearchValues);
        Assert.Equal(FilterOrderMode.Specify, vm.OrderMode);
        // The ladder as built, through the engine: inside the mask at both edges and beyond.
        foreach (double mhz in new[] { 100.0, 500, 900, 1000 })
            Assert.True(LossDb(vm.Cnl, mhz * 1e6) <= 0.5 + 1e-3, $"{LossDb(vm.Cnl, mhz * 1e6):F3} dB at {mhz} MHz");
        foreach (double mhz in new[] { 1300.0, 2000, 5000 })
            Assert.True(LossDb(vm.Cnl, mhz * 1e6) >= 30 - 1e-3, $"{LossDb(vm.Cnl, mhz * 1e6):F3} dB at {mhz} MHz");
        // The block names each element's pick, from the E12 series.
        var applied = Applied(vm);
        Assert.Contains(FilterValues.IecLibrary + ": E12", applied["Parts"]);
        Assert.Equal("E12", applied["Values"]);
        Assert.All(FilterBlockLadder.ParseParts(applied["Parts"]), p => Assert.StartsWith(FilterValues.IecLibrary, p.SeriesKey));
        Assert.Equal(vm.Elements.Count, FilterBlockLadder.ParseParts(applied["Parts"]).Count());
    }

    [Fact]
    public async Task TheBlockSimulatesTheFoundValues_AndReopensOnThem()
    {
        var vm = await Lowpass(FilterResponse.Chebyshev, "1000", "1300", "0.5", "0.5", "30", FilterSeriesKind.E12);
        await vm.SearchValuesAsync();
        Assert.True(vm.MaskMet, vm.Status);

        var filter = new EditableComponent { InstanceName = "F1", Symbol = SymbolKind.Filter };
        filter.Parameters.AddRange(vm.AppliedParameters(FilterPlacement.DefaultParameters())!);
        var ladder = FilterBlockLadder.Build(filter.Parameters, null, out string? refusal);
        Assert.True(ladder is not null, refusal);
        // Between the same terminations as the Designer's netlist (an even-order Chebyshev's load is not 50 ohm).
        double rl = double.Parse(System.Text.RegularExpressions.Regex.Match(vm.Cnl, @"Num=2 Z=(\S+)").Groups[1].Value,
                                 System.Globalization.CultureInfo.InvariantCulture);
        string blockCnl = FilterLadder.ToCnl(ladder!.Parts.Select(p => p.Element).ToList(), 50, rl);
        foreach (double mhz in new[] { 500.0, 1000, 1300, 2000 })
            Assert.Equal(LossDb(vm.Cnl, mhz * 1e6), LossDb(blockCnl, mhz * 1e6), 6);

        var reopened = new LcFilterDesignerViewModel();
        reopened.SeedFrom(filter);
        await reopened.DesignAsync(0);
        Assert.True(reopened.MaskMet, reopened.Status);
        Assert.Equal(Applied(vm)["Parts"], Applied(reopened)["Parts"]);
    }

    [Fact]
    public async Task ChangingTheSpecificationDropsTheFoundValues()
    {
        var vm = await Lowpass(FilterResponse.Chebyshev, "1000", "1300", "0.5", "0.5", "30", FilterSeriesKind.E12);
        await vm.SearchValuesAsync();
        Assert.True(vm.MaskMet, vm.Status);
        vm.AsText = "25";
        await vm.DesignAsync(0);
        Assert.True(vm.FilterAssignments() is not { } a || a.Single(p => p.Name == "Parts").Expression == "");
    }

    [Fact]
    public async Task AMaskNoValuesCanMeetSaysSo()
    {
        var vm = new LcFilterDesignerViewModel { Response = FilterResponse.Chebyshev, Form = NetworkForm.Bandpass };
        vm.UnitA = vm.UnitB = vm.UnitC = vm.UnitD = "MHz";
        vm.FaText = "2390"; vm.FbText = "2400"; vm.FcText = "2500"; vm.FdText = "2510";
        vm.ApText = "3"; vm.RippleText = "0.1"; vm.AsText = "20";
        vm.SeriesChoice = vm.SeriesChoices.Single(c => c.Value == FilterSeriesKind.E12);
        await vm.DesignAsync(0);
        Assert.True(vm.CanSearchValues, vm.Status);
        await vm.SearchValuesAsync();
        Assert.False(vm.MaskMet);
        Assert.Contains("No choice of values", vm.Status);
        Assert.Null(vm.FilterAssignments());
    }

    [Fact]
    public async Task WithAVendorLibrary_TheSearchTriesTheSeriesAndUsesTheirParts()
    {
        string ws = Path.Combine(_dir, "ws"), lib = Path.Combine(_dir, "lib");
        Directory.CreateDirectory(ws);
        VendorLibraryFixture.Write(lib, physical: true);
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"),
            new CwsFile { VendorLibraries = [new CwsVendorLibraryRef { Name = "Test", Path = lib }] });

        var vm = await Lowpass(FilterResponse.Chebyshev, "4000", "5000", "0.5", "0.5", "25",
                               FilterSeriesKind.VendorLibrary, ws);
        Assert.False(vm.MaskMet);
        Assert.True(vm.CanSearchValues, vm.Status);
        Assert.Contains("pairing", vm.SearchQuestion);

        await vm.SearchValuesAsync();

        Assert.True(vm.MaskMet, vm.Status);
        Assert.All(vm.Elements, r => Assert.StartsWith("TST", r.Part));
        Assert.NotNull(vm.InductorChoice);
        Assert.NotNull(vm.CapacitorChoice);
        // The window's ladder is the parts as modelled, resolved through the workspace as a placed ladder is.
        var s21 = FilterLadderCircuit.S21(vm.Cnl, [1000e6, 4000e6, 5000e6], ws);
        Assert.Contains("SpiceModel:", vm.Cnl);
        Assert.True(-20 * Math.Log10(s21[0].Magnitude) <= 0.5 + 1e-3);
        Assert.True(-20 * Math.Log10(s21[1].Magnitude) <= 0.5 + 1e-3);
        Assert.True(-20 * Math.Log10(s21[2].Magnitude) >= 25 - 1e-3);
        var applied = Applied(vm);
        Assert.Equal(vm.Elements.Count, FilterBlockLadder.ParseParts(applied["Parts"]).Count());
    }
}

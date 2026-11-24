using System.Globalization;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Matching;
using CircuitRF.Core.Netlist;
using CircuitRF.Core.Systems;
using CircuitRF.Design.Filters;
using CircuitRF.Engine;
using CircuitRF.Ui.Filters;
using Xunit;

namespace CircuitRF.Ui.Tests.Filters;

/// <summary>
/// The Designer never hands back a filter outside its own mask: Minimum searches orders (and, for
/// a family with finite zeros, deeper floors) until the ladder meets it, and a design that misses
/// it can be neither applied nor placed.
/// </summary>
public class LcFilterMaskGuaranteeTests
{
    /// <summary>A narrow 2.4 GHz bandpass: 10 MHz transitions, Ap 3, Ripple 0.1, As 20.</summary>
    private static async Task<LcFilterDesignerViewModel> NarrowBandpass(
        FilterResponse response, Action<LcFilterDesignerViewModel>? edit = null)
    {
        var vm = new LcFilterDesignerViewModel { Response = response, Form = NetworkForm.Bandpass };
        vm.OrderMode = FilterOrderMode.Minimum;
        vm.UnitA = vm.UnitB = vm.UnitC = vm.UnitD = "MHz";
        vm.FaText = "2390"; vm.FbText = "2400"; vm.FcText = "2500"; vm.FdText = "2510";
        vm.ApText = "3"; vm.RippleText = "0.1"; vm.AsText = "20";
        edit?.Invoke(vm);
        await vm.DesignAsync(0);
        return vm;
    }

    private static double LadderDb(string cnl, double hz)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var ds = SParameterEngine.Run(new Elaborator(lib).Elaborate(tb), [hz]);
        int np = ds["S"].Axes[1].Length;
        return -20 * Math.Log10(ds["S"].ComplexValues[1 * np + 0].Magnitude);
    }

    private static void AssertMeetsNarrowMask(LcFilterDesignerViewModel vm)
    {
        Assert.True(vm.MaskMet, vm.Status);
        Assert.True(vm.Elements.Count > 0, vm.Status);
        foreach (double mhz in new[] { 2400.0, 2425, 2450, 2475, 2500 })
            Assert.True(LadderDb(vm.Cnl, mhz * 1e6) <= 3 + 1e-3, $"{LadderDb(vm.Cnl, mhz * 1e6):F3} dB at {mhz} MHz");
        foreach (double mhz in new[] { 2000.0, 2300, 2390, 2510, 2600, 3000 })
            Assert.True(LadderDb(vm.Cnl, mhz * 1e6) >= 20 - 1e-3, $"{LadderDb(vm.Cnl, mhz * 1e6):F3} dB at {mhz} MHz");
        Assert.NotNull(vm.FilterAssignments());
    }

    [Fact]
    public async Task MinimumChebyshevMeetsTheNarrowMask()
    {
        var vm = await NarrowBandpass(FilterResponse.Chebyshev);
        AssertMeetsNarrowMask(vm);
    }

    [Fact]
    public async Task MinimumInverseChebyshevHasALadderThatMeetsTheNarrowMask()
    {
        var vm = await NarrowBandpass(FilterResponse.InvChebyshev);
        AssertMeetsNarrowMask(vm);
        // Built deeper than the mask's 20 dB, where the ladder has every element positive; the
        // mask's own floor is still what is written for the Designer to reopen on.
        var applied = vm.FilterAssignments()!.ToDictionary(a => a.Name, a => a.Expression);
        Assert.Equal("20", applied["AsMask"]);
        Assert.True(double.Parse(applied["Astop"], CultureInfo.InvariantCulture) >= 20);
    }

    [Fact]
    public async Task ASpecifiedOrderThatMissesTheMaskCannotBePlacedOrApplied()
    {
        var vm = await NarrowBandpass(FilterResponse.Chebyshev, v =>
        {
            v.OrderMode = FilterOrderMode.Specify;
            v.OrderText = "3";
        });
        Assert.False(vm.MaskMet);
        Assert.False(vm.CanPlace);
        Assert.False(vm.CanApply);
        Assert.Null(vm.FilterAssignments());
        Assert.Contains("mask", vm.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ARippleAboveApIsRefused()
    {
        var vm = await NarrowBandpass(FilterResponse.Chebyshev, v => { v.ApText = "0.5"; v.RippleText = "1"; });
        Assert.False(vm.MaskMet);
        Assert.False(vm.CanPlace);
    }

    [Fact]
    public void TheSearchStepsPastAShallowFloorWithNoLadder()
    {
        // Inverse Chebyshev lowpass, order 5, has no positive ladder at As 20 but does deeper.
        var mask = new FilterMaskSpec(NetworkForm.Lowpass, 3, 20, 1e9, 1.3e9, 0, 0);
        var choice = FilterMask.Find(FilterResponse.InvChebyshev, NetworkForm.Lowpass, 5, 5, 1.3e9, 0, 0, 0.1, 50,
                                     FilterFirstElement.Parallel, EllipticType.Auto, mask, null, default, out string? why);
        Assert.NotNull(choice);
        Assert.Null(why);
        Assert.True(choice!.Ladder.Ok);
        Assert.True(choice.MissDb <= FilterMask.ToleranceDb);
    }

    /// <summary>
    /// Chebyshev lowpass 1000/1300 MHz, Ap 0.5, Ripple 0.5, As 20: the order formula gives 6, and an
    /// even-order Chebyshev ladder ends in about twice its source. Minimum between 50 Ω and 50 Ω
    /// moves to the next order whose ladder ends in 50 Ω.
    /// </summary>
    [Fact]
    public async Task MinimumKeepsTheLoadAskedFor()
    {
        var vm = new LcFilterDesignerViewModel { Response = FilterResponse.Chebyshev, Form = NetworkForm.Lowpass };
        vm.OrderMode = FilterOrderMode.Minimum;
        vm.FaText = "1000"; vm.FbText = "1300";
        vm.ApText = "0.5"; vm.RippleText = "0.5"; vm.AsText = "20";
        await vm.DesignAsync(0);
        Assert.True(vm.MaskMet, vm.Status);
        var load = System.Text.RegularExpressions.Regex.Match(vm.Cnl, @"Term:T2\s+\S+\s+0\s+Num=2\s+Z=(\S+)");
        Assert.True(load.Success, vm.Cnl);
        Assert.Equal(50, double.Parse(load.Groups[1].Value, CultureInfo.InvariantCulture), 6);
        Assert.Equal(1, int.Parse(vm.OrderText, CultureInfo.InvariantCulture) % 2);

        // A typed even order is built as typed, at the load its family gives.
        vm.OrderMode = FilterOrderMode.Specify;
        vm.OrderText = "6";
        await vm.DesignAsync(0);
        Assert.Contains("The ladder's load is", vm.Status);
    }

    [Fact]
    public void TheEdgesDefaultToMHz()
    {
        var vm = new LcFilterDesignerViewModel();
        Assert.Equal(("MHz", "MHz", "MHz", "MHz"), (vm.UnitA, vm.UnitB, vm.UnitC, vm.UnitD));
        Assert.Equal(("500", "1000", "2000", "2500"), (vm.FaText, vm.FbText, vm.FcText, vm.FdText));
    }
}

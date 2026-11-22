using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Matching;
using CircuitRF.Core.Netlist;
using CircuitRF.Core.Systems;
using CircuitRF.Design.Schematic;
using CircuitRF.Engine;
using CircuitRF.Ui.Filters;
using Xunit;

namespace CircuitRF.Ui.Tests.Filters;

/// <summary>
/// The Designer's mask and its band edges: a bandstop designs from the window's own default edges,
/// a refused specification still draws its mask, the mask is drawn by role over its own bands, and
/// Apply then Seed gives back the edges the ladder was built at.
/// </summary>
public class LcFilterDesignerMaskTests
{
    private static async Task<LcFilterDesignerViewModel> Designed(
        FilterResponse response, NetworkForm form, Action<LcFilterDesignerViewModel>? edit = null)
    {
        var vm = new LcFilterDesignerViewModel { Response = response, Form = form };
        edit?.Invoke(vm);
        await vm.DesignAsync(0);
        return vm;
    }

    private static double Ghz(string text) => double.Parse(text, CultureInfo.InvariantCulture) * 1e9;

    /// <summary>|S21| in dB of the window's own ladder netlist at one frequency.</summary>
    private static double LadderDb(string cnl, double hz)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var ds = SParameterEngine.Run(new Elaborator(lib).Elaborate(tb), [hz]);
        var raw = ds["S"].ComplexValues;
        int np = ds["S"].Axes[1].Length;
        return -20 * Math.Log10(raw[1 * np + 0].Magnitude);
    }

    [Theory]
    [InlineData(FilterResponse.Butterworth)]
    [InlineData(FilterResponse.Chebyshev)]
    [InlineData(FilterResponse.Elliptic)]
    [InlineData(FilterResponse.InvChebyshev)]
    public async Task ABandstopFromTheDefaultEdgesDesignsAndMeetsItsMask(FilterResponse response)
    {
        var vm = await Designed(response, NetworkForm.Bandstop, v =>
        {
            // The order is sized from Ap and the ladder built at Ripple; the two agree here.
            v.ApText = "0.5";
            v.RippleText = "0.5";
            v.AsText = "30";
            // Inverse Chebyshev past order 11 has no positive ladder; this mask's transitions are
            // too narrow for it, so it gets wider ones.
            if (response == FilterResponse.InvChebyshev) { v.FaText = "0.3"; v.FdText = "3.5"; }
        });
        Assert.True(vm.Elements.Count > 0, vm.Status);
        Assert.DoesNotContain("transition", vm.Status);

        double fp1 = Ghz(vm.FaText), fs1 = Ghz(vm.FbText), fs2 = Ghz(vm.FcText), fp2 = Ghz(vm.FdText);
        var applied = vm.FilterAssignments()!.ToDictionary(a => a.Name, a => a.Expression);
        bool inv = response == FilterResponse.InvChebyshev;
        Assert.Equal(inv ? fs1 : fp1, Ghz(applied["F1"]), 3);
        Assert.Equal(inv ? fs2 : fp2, Ghz(applied["F2"]), 3);

        // Butterworth's edge is its 3.01 dB point, which Ap 0.5 dB is not; the rest meet Ap there.
        double passLimit = response == FilterResponse.Butterworth ? 3.0103 : 0.5;
        foreach (double f in new[] { fp1, fp2 })
            Assert.True(LadderDb(vm.Cnl, f) <= passLimit + 1e-3, $"{response}: {LadderDb(vm.Cnl, f):F3} dB at Fp {f:G4}");
        if (response != FilterResponse.Butterworth)
            foreach (double f in new[] { fs1, fs2 })
                Assert.True(LadderDb(vm.Cnl, f) >= 30 - 1e-3, $"{response}: {LadderDb(vm.Cnl, f):F3} dB at Fs {f:G4}");
    }

    [Fact]
    public async Task ARefusedBandstopStillDrawsItsMask()
    {
        var vm = await Designed(FilterResponse.Chebyshev, NetworkForm.Bandstop, v => v.FbText = "0.3");
        Assert.Contains("bandstop needs", vm.Status);
        Assert.Empty(vm.Elements);
        Assert.Equal(7, vm.MagnitudeMask.Lines.Count);   // four edges, −Ap either side, −As between
    }

    [Fact]
    public void TheMaskIsDrawnByRoleOverItsOwnBands()
    {
        var bs = LcFilterDesignerViewModel.MaskLinesFor(
            new(NetworkForm.Bandstop, 0.5, 30, 1e9, 2e9, 3e9, 4e9), "Fp1", "Fs1", "Fs2", "Fp2");
        var edges = bs.Where(l => l.Vertical).ToList();
        Assert.Equal([FilterMaskColors.Pass, FilterMaskColors.Stop, FilterMaskColors.Stop, FilterMaskColors.Pass],
                     edges.Select(e => e.Color));
        var limits = bs.Where(l => !l.Vertical).ToList();
        var asLimit = limits.Single(l => l.Label == "As");
        Assert.Equal((-30.0, 2e9, 3e9, 1), (asLimit.Value, asLimit.X0, asLimit.X1, asLimit.Forbid));
        var apLimits = limits.Where(l => l.Label == "Ap").OrderBy(l => double.IsNaN(l.X0) ? 0 : l.X0).ToList();
        Assert.Equal(2, apLimits.Count);
        Assert.True(double.IsNaN(apLimits[0].X0) && apLimits[0].X1 == 1e9);
        Assert.True(apLimits[1].X0 == 4e9 && double.IsNaN(apLimits[1].X1));
        Assert.All(apLimits, l => Assert.Equal(-1, l.Forbid));

        var bp = LcFilterDesignerViewModel.MaskLinesFor(
            new(NetworkForm.Bandpass, 0.5, 30, 1e9, 2e9, 3e9, 4e9), "Fs1", "Fp1", "Fp2", "Fs2");
        var ap = bp.Single(l => l.Label == "Ap");
        Assert.Equal((2e9, 3e9), (ap.X0, ap.X1));
        Assert.Equal(2, bp.Count(l => l.Label == "As"));

        var lp = LcFilterDesignerViewModel.MaskLinesFor(
            new(NetworkForm.Lowpass, 0.5, 30, 1e9, 2e9, 0, 0), "Fp", "Fs", "", "");
        Assert.Equal(2, lp.Count(l => l.Vertical));
        Assert.Equal(2e9, lp.Single(l => l.Label == "As").X0);
    }

    /// <summary>Apply writes the edges the ladder used; Seed reads them back into the right boxes.</summary>
    [Theory]
    [InlineData(FilterResponse.Chebyshev, NetworkForm.Bandpass)]
    [InlineData(FilterResponse.Chebyshev, NetworkForm.Bandstop)]
    [InlineData(FilterResponse.InvChebyshev, NetworkForm.Bandstop)]
    [InlineData(FilterResponse.Elliptic, NetworkForm.Lowpass)]
    public async Task ApplyThenSeedGivesBackTheEdges(FilterResponse response, NetworkForm form)
    {
        var first = await Designed(response, form, v =>
        {
            if (form == NetworkForm.Lowpass) { v.FaText = "1"; v.FbText = "1.6"; }
            if (response == FilterResponse.InvChebyshev) {
                // Seed reads Ap back from the Filter's Ripple; keep the two equal so the second
                // window sizes the same order.
                v.FaText = "0.3"; v.FdText = "3.5"; v.AsText = "30"; v.RippleText = v.ApText;
            }
        });
        var applied = first.FilterAssignments();
        Assert.NotNull(applied);

        var filter = new EditableComponent { Symbol = SymbolKind.Filter };
        foreach (var p in ComponentTypeRegistry.DefaultParameters(SymbolKind.Filter, 2))
            filter.Parameters.Add(new EditableParameter { Name = p.Name, Expression = p.Expression, Unit = p.Unit });
        foreach (var (name, expression) in applied!)
        {
            var p = filter.Parameters.First(x => x.Name == name);
            p.Expression = expression;
            if (name is "Fc" or "F1" or "F2") p.Unit = "GHz";
        }

        var second = new LcFilterDesignerViewModel();
        second.SeedFrom(filter);
        await second.DesignAsync(0);
        Assert.True(second.Elements.Count > 0, second.Status);
        var back = second.FilterAssignments()!.ToDictionary(a => a.Name, a => a.Expression);
        foreach (var (name, expression) in applied!)
            if (name is "Fc" or "F1" or "F2")
                Assert.Equal(Ghz(expression), Ghz(back[name]), 3);
    }
}

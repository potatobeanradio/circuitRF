using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Microstrip;
using Xunit;

namespace CircuitRF.Core.Tests.Devices.Microstrip;

/// <summary>
/// MCLA's plan (<see cref="CoupledArrayPlan"/>): each form's end pattern, its pins and sites, where the open ends, taps
/// and folds sit, and the coded refusals. Its circuit is <c>CoupledArrayTests</c>' (Engine.Tests).
/// </summary>
public class CoupledArrayPlanTests
{
    private static readonly MicrostripSubstrate Ro4350b = new(0.508e-3, 35e-6, 3.66, 5.8e7, 0.0037);

    private static CoupledArraySpec Spec(CoupledArrayForm form, CoupledArrayFeed feed, int n, string? ends = null,
                                         double tap = 3e-3)
        => new([.. Enumerable.Repeat(1e-3, n)], [.. Enumerable.Repeat(0.5e-3, n - 1)], 16e-3, form, feed, ends, tap, 1.1e-3,
               [.. Enumerable.Range(1, n).Select(k => k * 1e-12)]);

    private static string Pattern(CoupledArrayDesign d) => string.Join(" ", d.Ends.Select(CoupledArrayPlan.Token));

    [Theory]
    [InlineData(CoupledArrayForm.Interdigital, CoupledArrayFeed.Tapped, 4, "SO OS SO OS")]
    [InlineData(CoupledArrayForm.Interdigital, CoupledArrayFeed.Coupled, 5, "SP OS SO OS SP")]
    [InlineData(CoupledArrayForm.Interdigital, CoupledArrayFeed.Coupled, 4, "SP OS SO PS")]
    [InlineData(CoupledArrayForm.Combline, CoupledArrayFeed.Tapped, 3, "SC SC SC")]
    [InlineData(CoupledArrayForm.Combline, CoupledArrayFeed.Coupled, 4, "SP SC SC SP")]
    [InlineData(CoupledArrayForm.PseudoInterdigital, CoupledArrayFeed.Coupled, 6, "PO OJ JO OJ JO OP")]
    [InlineData(CoupledArrayForm.PseudoInterdigital, CoupledArrayFeed.Tapped, 4, "OJ JO OJ JO")]
    public void EachForm_FillsItsEndPattern(CoupledArrayForm form, CoupledArrayFeed feed, int n, string pattern)
    {
        var d = CoupledArrayPlan.Build(Spec(form, feed, n), Ro4350b);
        Assert.Equal(pattern, Pattern(d));
        // The same pattern written as Custom is the same plan.
        var custom = CoupledArrayPlan.Build(Spec(CoupledArrayForm.Custom, feed, n, pattern), Ro4350b);
        Assert.Equal(d.Plan.Pins, custom.Plan.Pins);
        Assert.Equal(d.Plan.Sites, custom.Plan.Sites);
        Assert.Equal(d.Plan.Sections.Count, custom.Plan.Sections.Count);
    }

    /// <summary>1 and 2, then a pin on each shorted or loaded end in strip order, near before far: a via site on a short, a
    /// C site of the strip's own C on a load. The schematic's count from the written parameters is the plan's.</summary>
    [Fact]
    public void ThePins_AreThePortsThenEachShortedOrLoadedEnd_WithTheirSites()
    {
        var d = CoupledArrayPlan.Build(Spec(CoupledArrayForm.Combline, CoupledArrayFeed.Coupled, 4), Ro4350b);
        Assert.Equal(["1", "2", "E1", "E2", "E3", "E4", "E5", "E6"], d.Plan.Pins);
        Assert.Equal(
        [
            new PlanSite(PlanSiteRole.Via, PlanSiteKind.Shunt, "E1", null, null, null),
            new PlanSite(PlanSiteRole.Via, PlanSiteKind.Shunt, "E2", null, null, null),
            new PlanSite(PlanSiteRole.C, PlanSiteKind.Shunt, "E3", null, null, 2e-12),
            new PlanSite(PlanSiteRole.Via, PlanSiteKind.Shunt, "E4", null, null, null),
            new PlanSite(PlanSiteRole.C, PlanSiteKind.Shunt, "E5", null, null, 3e-12),
            new PlanSite(PlanSiteRole.Via, PlanSiteKind.Shunt, "E6", null, null, null),
        ], d.Plan.Sites);
        Assert.Equal(8, CoupledArrayPlan.PinCountOf("4", "Combline", "Coupled", null));
        Assert.Equal(5, CoupledArrayPlan.PinCountOf("3", "interdigital", "", ""));
        Assert.Equal(2, CoupledArrayPlan.PinCountOf("6", "PseudoInterdigital", "Coupled", null));
        Assert.Null(CoupledArrayPlan.PinCountOf("N1", "Combline", "Tapped", null));
        Assert.Null(CoupledArrayPlan.PinCountOf("4", "Comb", "Tapped", null));
    }

    /// <summary>
    /// Where the pieces sit. The tapped feed cuts the coupled length at each tap, measured from the outer strip's short (the
    /// near end of strip 1, the far end of strip 4 here), and leaves strip 1 upward and strip 4 downward from their edges.
    /// Open-end nodes come first of the internal nodes. A join of neighbours is a line straight down across the gap at the
    /// strips' ends; a join past a strip is each strip run on by G and half the wall, and the wall down between them.
    /// </summary>
    [Fact]
    public void TheTaps_CutTheCoupledLength_AndTheOpenEndsComeFirst()
    {
        var d = CoupledArrayPlan.Build(Spec(CoupledArrayForm.Interdigital, CoupledArrayFeed.Tapped, 4), Ro4350b);
        var strips = d.Plan.Sections.OfType<PlanCoupledStrips>().ToList();
        Assert.Equal([0.0, 3e-3, 13e-3], strips.Select(m => Math.Round(m.X, 12)));
        Assert.Equal(16e-3, strips.Sum(m => m.L), 12);
        var feeds = d.Plan.Sections.OfType<PlanLine>().ToList();
        Assert.Equal(2, feeds.Count);
        Assert.Equal((3e-3, 0.5e-3, 90.0, 1), (feeds[0].X, feeds[0].Y, feeds[0].RotationDeg, feeds[0].Nodes[1]));
        Assert.Equal((13e-3, -5e-3, 270.0, 2), (Math.Round(feeds[1].X, 12), Math.Round(feeds[1].Y, 12),
            feeds[1].RotationDeg, feeds[1].Nodes[1]));
        var opens = d.Plan.Sections.OfType<PlanOpen>().Select(o => o.Nodes[0]).Order().ToList();
        Assert.Equal(Enumerable.Range(d.Plan.Pins.Count + 1, 4), opens);

        var folded = CoupledArrayPlan.Build(Spec(CoupledArrayForm.Custom, CoupledArrayFeed.Coupled, 4, "PO OJ OJ OP"), Ro4350b);
        var fold = Assert.Single(folded.Plan.Sections.OfType<PlanLine>());
        Assert.Equal((15.5e-3, -2e-3, 270.0, 1e-3, 0.5e-3),
            (Math.Round(fold.X, 12), Math.Round(fold.Y, 12), fold.RotationDeg, fold.W, fold.L));

        var pseudo = CoupledArrayPlan.Build(Spec(CoupledArrayForm.PseudoInterdigital, CoupledArrayFeed.Coupled, 6), Ro4350b);
        var lines = pseudo.Plan.Sections.OfType<PlanLine>().ToList();
        Assert.Equal(6, lines.Count);
        var farWall = lines.Single(p => p.RotationDeg == 270.0 && p.X > 16e-3);
        Assert.Equal((17e-3, -1.5e-3, 1e-3, 3e-3),
            (Math.Round(farWall.X, 12), Math.Round(farWall.Y, 12), farWall.W, Math.Round(farWall.L, 12)));
        var nearWall = lines.Single(p => p.RotationDeg == 270.0 && p.X < 0);
        Assert.Equal((-1e-3, -3e-3), (Math.Round(nearWall.X, 12), Math.Round(nearWall.Y, 12)));
        Assert.Empty(pseudo.Plan.Sites);
    }

    [Theory]
    [InlineData(CoupledArrayForm.Interdigital, CoupledArrayFeed.Coupled, 2, null, "strips-too-few")]
    [InlineData(CoupledArrayForm.Combline, CoupledArrayFeed.Tapped, 3, "SC SC SC", "ends-with-form")]
    [InlineData(CoupledArrayForm.Custom, CoupledArrayFeed.Tapped, 3, "SC SX SC", "ends-unreadable")]
    [InlineData(CoupledArrayForm.Custom, CoupledArrayFeed.Tapped, 3, "SC SC", "ends-unreadable")]
    [InlineData(CoupledArrayForm.Custom, CoupledArrayFeed.Tapped, 3, "SP SC SC", "ports-misplaced")]
    [InlineData(CoupledArrayForm.Custom, CoupledArrayFeed.Coupled, 3, "SP SC SC", "ports-misplaced")]
    [InlineData(CoupledArrayForm.Custom, CoupledArrayFeed.Tapped, 3, "SJ SO SC", "join-unpaired")]
    [InlineData(CoupledArrayForm.Custom, CoupledArrayFeed.Tapped, 3, "OC SO SC", "tap-ungrounded")]
    [InlineData(CoupledArrayForm.PseudoInterdigital, CoupledArrayFeed.Coupled, 4, null, "groups-of-four")]
    public void AnInvalidCombination_IsACodedRefusal(CoupledArrayForm form, CoupledArrayFeed feed, int n, string? ends,
                                                     string code)
    {
        var ex = Assert.Throws<ParameterRefusalException>(() => CoupledArrayPlan.Build(Spec(form, feed, n, ends), Ro4350b));
        Assert.Equal("device.coupledarray." + code, ex.Diagnostic!.Id);
    }

    [Fact]
    public void ATapOutsideTheStrips_OrANonPositiveSpec_IsRefused()
    {
        var tap = Assert.Throws<ParameterRefusalException>(() => CoupledArrayPlan.Build(
            Spec(CoupledArrayForm.Combline, CoupledArrayFeed.Tapped, 3, tap: 20e-3), Ro4350b));
        Assert.Equal("device.coupledarray.tap-outside", tap.Diagnostic!.Id);
        var c = Spec(CoupledArrayForm.Combline, CoupledArrayFeed.Tapped, 3) with { C = [1e-12, 0.0, 1e-12] };
        Assert.Equal("device.coupledarray.spec-not-positive",
            Assert.Throws<ParameterRefusalException>(() => CoupledArrayPlan.Build(c, Ro4350b)).Diagnostic!.Id);
        var form = Assert.Throws<ParameterRefusalException>(() => CoupledArraySpec.Of((_, f) => f, n => n == "Form" ? "Hairpin" : null));
        Assert.Equal("device.coupledarray.name-unknown", form.Diagnostic!.Id);
    }
}

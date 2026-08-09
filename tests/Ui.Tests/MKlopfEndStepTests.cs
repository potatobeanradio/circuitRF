using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.PCells;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Owner report: "in its layout, I still see a strange step at either end of the transformer — are we
/// using non-uniform step spacing? Can we try a couple of geometry tests with uniform spacing?"
///
/// <para><b>Answered by measurement: the artwork's spacing was already uniform, and is not the cause.</b>
/// The two "step spacing" quantities MKlopf has are easy to conflate and are different things:</para>
/// <list type="bullet">
/// <item>The ELECTRICAL cascade (<c>MicrostripCascadeSectioning.NonUniformBoundaries</c>, consumed by
/// <c>MicrostripKlopfModel.Stamp</c>) is deliberately non-uniform — equal Δ(ln Z), so each section's
/// own reflection contribution is bounded. It never reaches the artwork.</item>
/// <item>The ARTWORK's outline stations are evenly spaced in x, always — pinned by
/// <see cref="ArtworkStationsAreEvenlySpacedInX"/>.</item>
/// </list>
///
/// <para><b>The step is inherent to the Klopfenstein profile</b> (the Kajfez-Prewitt endpoint term in
/// <see cref="KlopfensteinTaper.ImpedanceAt"/> is a Heaviside: Z(0) is exactly Z1 while Z(0⁺) is not).
/// Measured on the 50→100 Ω worked example it is a 2.564 Ω discontinuity, ≈0.27 mm of width on 1.6 mm
/// FR-4 — and it does not shrink with more stations, which is what
/// <see cref="TheEndStepIsInherentToTheProfile_NotToTheSampling"/> pins.</para>
///
/// <para><b>What WAS broken is <c>SmoothSteps</c>, whose whole job is to absorb that step.</b> Its
/// blend length is 3× the END WIDTH — a cross-section quantity — so on a taper short relative to its
/// own end widths it exceeded the whole component, and <c>ApplyEndBlend</c>'s "taper shorter than the
/// blend length; skip rather than overreach" guard then declined to blend at all, silently drawing the
/// full step. A 50 Ω line is ~3 mm wide on 1.6 mm FR-4, so 3×W1 ≈ 9 mm: every transformer shorter than
/// that got no smoothing whatever. Measured on a 5 mm 50→100 Ω taper, the first drawn half-width step
/// was 132,687 DBU against a 12,281 DBU mean — 10.8× — i.e. exactly one visible step and then a smooth
/// taper. The blend is clamped to half the taper per end now, and the mirror-image regime (a blend
/// narrower than one station, on a taper hundreds of end-widths long) is covered by adding evenly
/// spaced stations rather than by making them non-uniform.</para>
/// </summary>
public sealed class MKlopfEndStepTests
{
    private static Technology Pcb() => StarterTechnologies.Pcb2Layer();

    private static Dictionary<string, PCellValue> Params(
        double z1, double z2, double lMeters, bool smooth, double offset = 0.0) => new()
    {
        ["Z1"] = PCellValue.Real(z1),
        ["Z2"] = PCellValue.Real(z2),
        ["GammaMax"] = PCellValue.Real(0.05),
        ["L"] = PCellValue.Real(lMeters),
        ["Offset"] = PCellValue.Real(offset),
        ["SmoothSteps"] = PCellValue.Bool(smooth),
    };

    /// <summary>The outline is one closed polygon: the left edge forward, then the right edge back —
    /// so <c>Xy.Length / 4 - 1</c> is the station count, and the first half is the left edge.</summary>
    private static (long[] X, long[] Y) LeftEdge(PCellResult result)
    {
        var poly = (PolygonShape)result.Shapes[0];
        int n = poly.Xy.Length / 4;
        var x = new long[n];
        var y = new long[n];
        for (int i = 0; i < n; i++) { x[i] = poly.Xy[2 * i]; y[i] = poly.Xy[2 * i + 1]; }
        return (x, y);
    }

    // ── The owner's question, answered directly ─────────────────────────────────────────────────

    [Theory]
    [InlineData(0.005)]   // short — the clamp regime
    [InlineData(0.020)]   // the default
    [InlineData(0.100)]   // long enough to add stations
    public void ArtworkStationsAreEvenlySpacedInX(double lMeters)
    {
        var (x, _) = LeftEdge(MKlopfPCell.Generate(Params(50, 100, lMeters, smooth: true), Pcb(), PCellLayerSelection.Default));

        Assert.True(x.Length >= 97, $"expected at least 97 stations, got {x.Length}");

        // Every consecutive x delta equal, to within the one-DBU rounding of the metres->DBU
        // conversion. Non-uniform sampling would show a systematic spread, not a ±1 jitter.
        var deltas = Enumerable.Range(1, x.Length - 1).Select(i => x[i] - x[i - 1]).ToArray();
        long min = deltas.Min(), max = deltas.Max();
        Assert.True(max - min <= 1,
            $"stations are not evenly spaced in x: delta ranges {min}..{max} DBU over {deltas.Length} intervals");
    }

    [Fact]
    public void TheElectricalSectioningIsNonUniform_AndIsASeparateConcernFromTheArtwork()
    {
        // Stated as a test so the two are never conflated again: this IS non-uniform, deliberately,
        // and it is the stamp's own business — nothing here feeds the outline.
        var bounds = MicrostripCascadeSectioning.NonUniformBoundaries(
            t => KlopfensteinTaper.ImpedanceAt(t, 50, 100, 0.05), 12);

        var spans = Enumerable.Range(1, bounds.Length - 1).Select(i => bounds[i] - bounds[i - 1]).ToArray();
        Assert.True(spans.Max() > 4 * spans.Min(),
            "the electrical sectioning is expected to be strongly non-uniform (equal delta-lnZ)");
    }

    [Fact]
    public void TheEndStepIsInherentToTheProfile_NotToTheSampling()
    {
        double z0 = KlopfensteinTaper.ImpedanceAt(0.0, 50, 100, 0.05);
        double limit = KlopfensteinTaper.ImpedanceAt(1e-12, 50, 100, 0.05);

        Assert.Equal(50.0, z0, 6);
        Assert.True(limit - z0 > 2.5, $"expected a real endpoint discontinuity, got {limit - z0:F4} ohm");

        // Sampling more finely converges TO the discontinuity rather than away from it.
        foreach (int n in new[] { 24, 96, 384, 4096 })
        {
            double first = KlopfensteinTaper.ImpedanceAt(1.0 / n, 50, 100, 0.05);
            Assert.True(first - z0 > 2.5,
                $"N={n}: the first-station jump ({first - z0:F4} ohm) should not vanish with finer sampling");
        }
    }

    // ── The defect the question uncovered ───────────────────────────────────────────────────────

    /// <summary>
    /// How much bigger the FIRST (or last) station-to-station change in drawn half-width is than its
    /// own immediate neighbour. This measures a step AT AN END specifically, which is what was
    /// reported — not the taper's overall steepness, which legitimately peaks in the middle and would
    /// make a "worst step vs. mean" metric fail on any long taper for the right reason.
    ///
    /// <para>A zero-slope blend accelerates away from the end, so the first change is SMALLER than the
    /// second and this sits below 1. An unblended profile discontinuity puts the whole endpoint jump
    /// into the first interval, so this runs to double digits.</para>
    /// </summary>
    private static double EndStepRatio(PCellResult result)
    {
        var (_, y) = LeftEdge(result);
        int n = y.Length - 1;

        double Ratio(long step, long neighbour) => Math.Abs(step) / Math.Max(Math.Abs(neighbour), 1.0);

        return Math.Max(Ratio(y[0] - y[1], y[1] - y[2]),
                        Ratio(y[n - 1] - y[n], y[n - 2] - y[n - 1]));
    }

    [Fact]
    public void AShortTaper_NoLongerDrawsTheFullEndStep()
    {
        // 5 mm 50->100 ohm on FR-4: 3xW1 is ~9 mm, longer than the taper, which is exactly the case
        // the old "skip rather than overreach" guard silently refused to blend.
        var tech = Pcb();
        var smoothed = MKlopfPCell.Generate(Params(50, 100, 0.005, smooth: true), tech, PCellLayerSelection.Default);
        var stepped  = MKlopfPCell.Generate(Params(50, 100, 0.005, smooth: false), tech, PCellLayerSelection.Default);

        double withSmoothing = EndStepRatio(smoothed);
        double without       = EndStepRatio(stepped);

        // The control: SmoothSteps OFF genuinely does draw one big step, so this test cannot pass
        // vacuously on a taper that happened to have no step to remove.
        Assert.True(without > 5.0,
            $"SmoothSteps=false should still show the profile's own end step (got {without:F1}x its neighbour)");
        Assert.True(withSmoothing < 2.0,
            $"SmoothSteps=true should absorb it (got {withSmoothing:F1}x its neighbour)");
    }

    [Fact]
    public void AShortTaper_ReportsTheBlendLengthItActuallyUsed()
    {
        var result = MKlopfPCell.Generate(Params(50, 100, 0.005, smooth: true), Pcb(), PCellLayerSelection.Default);

        Assert.NotNull(result.Diagnostics);
        Assert.Contains(result.Diagnostics!, d => d.Contains("R-klp-4a") && d.Contains("clamped to half the taper"));
    }

    [Fact]
    public void AFarEndNarrowerThanTheNearEnd_IsClampedToo()
    {
        // 50 -> 12.5 ohm: W2 is ~19 mm, so 3xW2 is ~58 mm against a 20 mm taper. Before the clamp the
        // FAR end alone kept the full step (the near end's own blend fitted and was applied).
        var tech = Pcb();
        var smoothed = MKlopfPCell.Generate(Params(50, 12.5, 0.020, smooth: true), tech, PCellLayerSelection.Default);
        var stepped  = MKlopfPCell.Generate(Params(50, 12.5, 0.020, smooth: false), tech, PCellLayerSelection.Default);

        Assert.True(EndStepRatio(stepped) > 5.0, "control: the unsmoothed far end should step");
        Assert.True(EndStepRatio(smoothed) < 2.0,
            $"the far end should be blended too (got {EndStepRatio(smoothed):F1}x its neighbour)");
    }

    [Fact]
    public void ADefaultTaper_IsUnaffected_SameStationCountAndNoDiagnostic()
    {
        // The palette's own defaults (L = 20 mm, 50 -> 100 ohm) sit inside both regimes, so this
        // pass must not have moved the geometry every existing design already has.
        var result = MKlopfPCell.Generate(Params(50, 100, 0.020, smooth: true), Pcb(), PCellLayerSelection.Default);
        var (x, _) = LeftEdge(result);

        Assert.Equal(97, x.Length);   // the baseline 96 stations, unchanged
        Assert.True(result.Diagnostics is null or { Count: 0 },
            "the default taper needs neither a clamp nor extra stations: " +
            string.Join(" | ", result.Diagnostics ?? []));
    }

    [Fact]
    public void ALongTaper_AddsEvenlySpacedStations_RatherThanSteppingOrGoingNonUniform()
    {
        // 100 mm 50->100 ohm: the far end's blend (3 x 0.64 mm) is narrower than a 96-station spacing
        // (1.04 mm), so before this pass the blend was computed correctly and had nowhere to be drawn.
        var result = MKlopfPCell.Generate(Params(50, 100, 0.100, smooth: true), Pcb(), PCellLayerSelection.Default);
        var (x, _) = LeftEdge(result);

        Assert.True(x.Length > 97, $"expected extra stations for a long taper, got {x.Length}");
        Assert.True(EndStepRatio(result) < 2.0,
            $"the long taper's ends should be drawn smoothly (got {EndStepRatio(result):F1}x its neighbour)");

        var deltas = Enumerable.Range(1, x.Length - 1).Select(i => x[i] - x[i - 1]).ToArray();
        Assert.True(deltas.Max() - deltas.Min() <= 1, "the extra stations must stay evenly spaced");
    }

    [Fact]
    public void AnExtremeAspectRatio_ReportsRatherThanSilentlyStepping()
    {
        // 1 m long, 50->100 ohm. Even at the station ceiling the far blend spans under two stations,
        // so the end genuinely cannot be drawn smoothly — said, not silently drawn as a step.
        var result = MKlopfPCell.Generate(Params(50, 100, 1.000, smooth: true), Pcb(), PCellLayerSelection.Default);

        Assert.NotNull(result.Diagnostics);
        Assert.Contains(result.Diagnostics!, d => d.Contains("R-klp-4a") && d.Contains("outline stations"));
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Render.DataDisplay;
using RfCore;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.DataDisplay;

/// <summary>
/// <b>The user-reported antenna display defects of 2026-09-11, one test each.</b>
///
/// <para>Every one of them is a thing a user concluded from the screen that was not true — that the
/// run had produced no <c>U</c> cube, that a cut is four traces, that a pattern is at 1.74e+09 Hz —
/// so each assertion here is written against the thing they could SEE rather than against the
/// mechanism underneath it.</para>
/// </summary>
public sealed class AntennaFeedbackTests(ITestOutputHelper output)
{
    private const string U = "farfield.U";

    private static Trace Resolve(DataSet ds, string spec, PlotType type,
                                 FreqUnit freqUnit = FreqUnit.GHz, bool wholePlane = false)
    {
        Assert.True(CubeTraceSpecParser.TryParse(spec, ds, out string cube, out var slice,
                                                 out var transform, out string error), error);
        var t = new Trace(new SNP([1e9], 2), MatrixType.S, 0, 0, DependentVarFormat.Db)
        {
            CubeName          = cube,
            Slice             = slice,
            Transform         = transform,
            PatternWholePlane = wholePlane,
        };
        TraceResolve.SetCubeDataFrom(t, ds, type, freqUnit);
        TraceResolve.ApplyPinnedAxisDisplay(t, ds, freqUnit);
        return t;
    }

    private static Plot PatternPlot(params Trace[] traces)
    {
        var plot = new Plot(PlotType.Polar, FreqUnit.GHz)
        {
            PolarRadial     = PolarRadialMode.Db,
            PolarDbFloor    = -40,
            PolarDbRingStep = 10,
        };
        foreach (var t in traces) plot.Traces.Add(t);
        plot.Autoscale(force: true);
        foreach (var t in plot.Traces) t.BuildPath(plot.PlotType, plot.FreqUnits);
        return plot;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // "Why do we force the user to have two traces?" — the whole plane, in one.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>One whole-plane trace draws the same disc the two-trace pair draws.</b> Not "a similar
    /// picture" — the SAME point set, because the one trace gathers the very slice the back-branch
    /// trace gathers and maps it through the same <c>PatternPoint</c>. Asserted as exact equality of
    /// the two point lists so the simple spelling can never quietly become a different curve from
    /// the one every `.cdd` written before it carries.
    /// </summary>
    [Fact]
    public void OneWholePlaneTrace_DrawsExactlyWhatTheTwoTracePairDraws()
    {
        var ds = PatternFixture.Data;
        int back = BackIndexOf(ds, 0);

        var front = Resolve(ds, $"dB10({U}[0, :, 0, 1])", PlotType.Polar);
        var mirror = Resolve(ds, $"dB10({U}[0, :, {back}, 1])", PlotType.Polar);
        mirror.MirrorPatternAngle = true;
        var pair = PatternPlot(front, mirror);

        var one = Resolve(ds, $"dB10({U}[0, :, 0, 1])", PlotType.Polar, wholePlane: true);
        var solo = PatternPlot(one);

        Assert.True(one.HasPatternBackBranch);

        // The pair draws the back branch as its own trace, outward from broadside; the single trace
        // draws it inward, first, so the one curve runs -theta_max -> 0 -> +theta_max in order. So
        // the comparison reverses the mirror's list, which is the only difference there is.
        var expected = Enumerable.Reverse(mirror.Points).Concat(front.Points).ToList();
        Assert.Equal(expected.Count, one.Points.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].X, one.Points[i].X, 6);
            Assert.Equal(expected[i].Y, one.Points[i].Y, 6);
        }

        output.WriteLine($"pair: {front.Points.Count} + {mirror.Points.Count} points across " +
                         $"{pair.Traces.Count} traces; one trace: {one.Points.Count} points across " +
                         $"{solo.Traces.Count}");
    }

    /// <summary>
    /// <b>The label says BOTH azimuths</b> — "phi=0/180 deg". Without it a whole-plane cut is
    /// indistinguishable in the label strip from a front-half-only one, which is exactly the
    /// difference the reader needs, and the caption's "the cut is a PLANE" line has to appear for it
    /// as it does for the mirrored pair.
    /// </summary>
    [Fact]
    public void AWholePlaneCut_NamesBothAzimuths_AndTheCaptionSaysItIsAPlane()
    {
        var ds = PatternFixture.Data;
        var one = Resolve(ds, $"dB10({U}[0, :, 0, 1])", PlotType.Polar, wholePlane: true);
        var plot = PatternPlot(one);

        string? phi = one.PinnedAxisDisplay("phi");
        Assert.NotNull(phi);
        Assert.Contains("/", phi);
        Assert.StartsWith("phi=0/", phi);

        Assert.Contains(PatternCaption.Lines(plot),
                        l => l.Contains("the cut is a PLANE", StringComparison.Ordinal));
        output.WriteLine($"phi token: {phi}");
    }

    /// <summary>
    /// <b>A trace with no azimuth to move is REFUSED by name, never drawn as a half.</b> Silently
    /// falling back to the front branch would draw a perfectly ordinary-looking half-disc for a
    /// setting the user had switched on, which is the class of quiet failure this repository refuses
    /// by rule.
    /// </summary>
    [Fact]
    public void WholePlaneOnATraceWithNoPinnedAzimuth_RefusesByName()
    {
        var ds = PatternFixture.Data;
        // Sweep phi and pin theta: there is no azimuth PINNED, so there is none to move.
        var t = Resolve(ds, $"dB10({U}[0, 0, :, 1])", PlotType.Polar, wholePlane: true);

        Assert.False(t.HasPatternBackBranch);
        Assert.NotNull(t.ExpressionError);
        Assert.Contains("AZIMUTH", t.ExpressionError);
        Assert.Empty(t.Points);
        output.WriteLine(t.ExpressionError);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // "The y-axis label freq= does not respect the plot's frequency units"
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>A pinned frequency reads in the PLOT's unit and drops the axis name.</b> Reported: a cut
    /// plot set to GHz labelled its traces <c>freq=5e+09 Hz</c>. "5 GHz" already says it is a
    /// frequency, so the owner asked for the prefix to go too.
    /// </summary>
    [Theory]
    [InlineData(FreqUnit.GHz, "5 GHz")]
    [InlineData(FreqUnit.MHz, "5000 MHz")]
    [InlineData(FreqUnit.Hz,  "5000000000 Hz")]
    public void APinnedFrequency_ReadsInThePlotsOwnUnit_WithNoAxisNamePrefix(FreqUnit unit, string want)
    {
        var ds = PatternFixture.Data;
        var t  = Resolve(ds, $"dB10({U}[0, :, 0, 1])", PlotType.Polar, unit);

        Assert.Equal(want, t.PinnedAxisDisplay("freq"));
        output.WriteLine($"{unit}: {t.PinnedAxisDisplay("freq")}");
    }

    /// <summary>A NON-frequency pinned axis is untouched — it keeps its name, its own value and its
    /// own unit. The frequency rule is a frequency rule, not a general one.</summary>
    [Fact]
    public void ANonFrequencyPinnedAxis_KeepsItsNameAndItsOwnUnit()
    {
        var ds = new DataSet();
        ds.Add("H", new DataCube(
            [new Axis("VDS", [0.0, 2.0, 4.0, 6.0], "V"), new Axis("freq", [1e9, 2e9], "Hz")],
            new[] { 1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 7.0, 8.0 }));

        var t = Resolve(ds, "H[3, :]", PlotType.Rect);
        Assert.Equal("VDS=6 V", t.PinnedAxisDisplay("VDS"));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // "Sometimes U is disabled in the trace card picker"
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>A REAL cube draws on a dB-radial polar plot, and the plot type itself says so.</b> The
    /// picker greyed <c>U</c> out on every polar plot because a polar plot was taken to be a complex
    /// one — and the user concluded the run had produced no <c>U</c> at all. It had; every far-field
    /// run publishes <c>Etheta</c>, <c>Ephi</c> and <c>U</c> together.
    ///
    /// <para>The picker's own gate is a view-model property, but the FACT it has to agree with is
    /// this one: a real cube resolves and draws here, and does not on a linear polar plot.</para>
    /// </summary>
    [Fact]
    public void ARealCube_DrawsOnADbRadialPolarPlot_AndNotOnALinearOne()
    {
        var ds = PatternFixture.Data;
        Assert.True(ds.Contains(U));
        Assert.Equal(DataKind.Real, ds[U].DataKind);

        var pattern = Resolve(ds, $"dB10({U}[0, :, 0, 1])", PlotType.Polar);
        PatternPlot(pattern);
        Assert.NotEmpty(pattern.Points);

        // The same cube on a LINEAR polar plot draws nothing — which is what the picker's gate is
        // right about, and is the whole of what it is right about.
        var locus = Resolve(ds, $"dB10({U}[0, :, 0, 1])", PlotType.Polar);
        var linear = new Plot(PlotType.Polar, FreqUnit.GHz) { PolarRadial = PolarRadialMode.Linear };
        linear.Traces.Add(locus);
        linear.Autoscale(force: true);
        locus.BuildPath(linear.PlotType, linear.FreqUnits);
        Assert.Empty(locus.Points);

        output.WriteLine($"U is {ds[U].DataKind}, {pattern.Points.Count} points in dB mode, " +
                         $"{locus.Points.Count} in linear");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // "Add the angle around the circle's edge"
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The bearings follow the plot's OWN angular convention.</b> A pattern reads 0° at the top
    /// increasing clockwise; a locus reads 0° at the right increasing counter-clockwise, because
    /// there the angle is the phase of a complex number. Printing one convention's numbers around
    /// the other's plot would be worse than printing none, so the two are asserted apart.
    /// </summary>
    [Fact]
    public void TheBearings_FollowThePlotsOwnAngularConvention()
    {
        // Compass: 0 at the top (canvas y is DOWN, so -1), 90 to the right.
        var (x0, y0) = AxesRenderer.BearingUnit(0, compass: true);
        Assert.Equal(0.0, x0, 9);
        Assert.Equal(-1.0, y0, 9);
        var (x90, y90) = AxesRenderer.BearingUnit(90, compass: true);
        Assert.Equal(1.0, x90, 9);
        Assert.Equal(0.0, y90, 9);

        // Complex plane: 0 to the right, 90 UP.
        var (cx0, cy0) = AxesRenderer.BearingUnit(0, compass: false);
        Assert.Equal(1.0, cx0, 9);
        Assert.Equal(0.0, cy0, 9);
        var (cx90, cy90) = AxesRenderer.BearingUnit(90, compass: false);
        Assert.Equal(0.0, cx90, 9);
        Assert.Equal(-1.0, cy90, 9);

        // Twelve marks, and 360 divides by the step — a ring that does not close would be visible
        // as a missing spoke on exactly one bearing.
        Assert.Equal(0, 360 % AxesRenderer.BearingStepDeg);
        Assert.Equal(12, 360 / AxesRenderer.BearingStepDeg);
    }

    /// <summary>
    /// <b>Turning the bearings on SHRINKS the disc rather than overprinting it.</b> On a pattern
    /// plot the outer ring is the reference and a trace sits ON it at the peak, so a bearing drawn
    /// over the ring lands on the one sample the reader came for. The room comes out of the
    /// viewport, which is what makes the clip, the transform and the ring lattice all agree.
    /// </summary>
    [Fact]
    public void TurningTheBearingsOn_ShrinksTheDisc_RatherThanOverprintingIt()
    {
        var ds = PatternFixture.Data;
        var without = PatternPlot(Resolve(ds, $"dB10({U}[0, :, 0, 1])", PlotType.Polar));
        var with    = PatternPlot(Resolve(ds, $"dB10({U}[0, :, 0, 1])", PlotType.Polar));
        with.ShowPolarAngleLabels = true;

        var a = PlotRenderer.BuildTransforms(without, (400.0, 400.0)).Viewport;
        var b = PlotRenderer.BuildTransforms(with,    (400.0, 400.0)).Viewport;

        Assert.True(b.Width < a.Width, $"{b.Width} should be under {a.Width}");
        Assert.True(b.Height < a.Height);
        // Still centred horizontally — the margin is taken off both sides, not one.
        Assert.Equal(a.X + a.Width / 2, b.X + b.Width / 2, 9);
        output.WriteLine($"viewport {a.Width:F4} -> {b.Width:F4} wide with bearings on");
    }

    /// <summary>Off by default, so every `.cdd` written before 2026-09-11 draws what it always
    /// drew — and it round-trips through the config when it is on.</summary>
    [Fact]
    public void TheBearingsAreOffByDefault_AndRoundTrip()
    {
        Assert.False(new Plot(PlotType.Polar, FreqUnit.GHz).ShowPolarAngleLabels);
        Assert.False(new PlotContainerConfig().PolarAngleLabels);
        Assert.False(new TraceConfig().PatternWholePlane);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // "Put the reference input power on the trace card" — re-reference with no re-run
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>A dBm level is re-referenced EXACTLY, from data already in hand.</b> That is the whole
    /// argument for the control being on the trace card: a level is linear in its reference, so
    /// changing it is arithmetic on the decibel value, and charging a user a re-run of an
    /// hours-long EM sweep for a dB offset would be charging the price of a simulation for a shift.
    /// </summary>
    [Theory]
    [InlineData(20.0)]
    [InlineData(-13.5)]
    public void ReReferencingADbmLevel_ShiftsItByExactlyTheDifference(double want)
    {
        var ds = PatternFixture.Data;
        var baseline = Resolve(ds, "farfield.TrpDbm[0, 1]", PlotType.Rect);
        Assert.True(baseline.IsReferencedLevel);
        double baked = baseline.BakedReferenceInputPowerDbm;

        var shifted = Resolve(ds, "farfield.TrpDbm[0, 1]", PlotType.Rect);
        shifted.ReferenceInputPowerDbmOverride = want;
        shifted.BuildPath(PlotType.Rect, FreqUnit.GHz);

        Assert.Equal(want - baked, shifted.ReferenceLevelOffsetDb, 12);
        Assert.Equal(baseline.Points.Count, shifted.Points.Count);
        for (int i = 0; i < baseline.Points.Count; i++)
        {
            Assert.Equal(baseline.Points[i].X, shifted.Points[i].X, 6);
            Assert.Equal(baseline.Points[i].Y + (want - baked), shifted.Points[i].Y, 4);
        }

        output.WriteLine($"run published at {baked} dBm; re-read at {want} dBm shifts every " +
                         $"sample by {want - baked:F3} dB");
    }

    /// <summary>
    /// <b>The label always states the reference the trace is DRAWN at</b>, overridden or not. A
    /// picture of a level carries no file, so a reader has no other way to tell an overridden trace
    /// from an un-overridden one — the same rule that stops a normalised pattern being drawn
    /// unlabelled.
    /// </summary>
    [Fact]
    public void ALevelsLabel_AlwaysStatesTheReferenceItIsDrawnAt()
    {
        var ds = PatternFixture.Data;
        var t = Resolve(ds, "farfield.TrpDbm[0, 1]", PlotType.Rect);

        string plain = TraceLabeler.ComputeMinimalLabels([t])[0];
        Assert.Contains("dBm in", plain);
        Assert.Contains(t.BakedReferenceInputPowerDbm.ToString("0.###",
                            System.Globalization.CultureInfo.InvariantCulture), plain);

        t.ReferenceInputPowerDbmOverride = 27.0;
        string moved = TraceLabeler.ComputeMinimalLabels([t])[0];
        Assert.Contains("@ 27 dBm in", moved);
        Assert.NotEqual(plain, moved);

        output.WriteLine($"{plain}  ->  {moved}");
    }

    /// <summary>
    /// <b>The override is INERT on anything that is not a referenced level</b>, and the control is
    /// not offered for one. A cube is a level because its own unit says so AND its group publishes
    /// the reference — a relationship, never a list of names, so nothing in the display has to be
    /// told about one engine's metric names.
    /// </summary>
    [Theory]
    [InlineData("farfield.DirectivityDbi[0, 1]")]   // dBi — a ratio, referenced to nothing
    [InlineData("farfield.PowerRadiated[0, 1]")]    // W — absolute already
    public void AnOverrideOnSomethingThatIsNotALevel_DoesNothing(string spec)
    {
        var ds = PatternFixture.Data;
        var plain = Resolve(ds, spec, PlotType.Rect);
        Assert.False(plain.IsReferencedLevel);

        var t = Resolve(ds, spec, PlotType.Rect);
        t.ReferenceInputPowerDbmOverride = 30.0;
        t.BuildPath(PlotType.Rect, FreqUnit.GHz);

        Assert.Equal(0.0, t.ReferenceLevelOffsetDb);
        for (int i = 0; i < plain.Points.Count; i++)
            Assert.Equal(plain.Points[i].Y, t.Points[i].Y, 9);
        Assert.DoesNotContain("dBm in", TraceLabeler.ComputeMinimalLabels([t])[0]);
    }

    /// <summary>The reference cube is not itself a level — re-referencing it would be asking what
    /// 0 dBm is above 10 dBm.</summary>
    [Fact]
    public void TheReferenceCubeItself_IsNotARereferenceableLevel()
    {
        var ds = PatternFixture.Data;
        Assert.False(LevelReference.IsReferencedLevel(ds, "farfield.ReferenceInputPowerDbm", out _));
        Assert.True(LevelReference.IsReferencedLevel(ds, "farfield.TrpDbm", out double r));
        Assert.Equal(0.0, r);   // the shipped default
    }

    /// <summary>A group with no published reference offers no override, whatever a unit says —
    /// there would be nothing to subtract, so the shift could only be a guess.</summary>
    [Fact]
    public void ADbmCubeWithNoPublishedReference_IsNotRereferenceable()
    {
        var ds = new DataSet();
        ds.Add("Lonely", new DataCube([new Axis("freq", [1e9, 2e9], "Hz")],
                                      new[] { 1.0, 2.0 }) { Unit = "dBm" });
        Assert.False(LevelReference.IsReferencedLevel(ds, "Lonely", out _));
    }

    /// <summary>The override round-trips through the `.cdd`, and null — "the run's own" — is what
    /// every document written before this carries.</summary>
    [Fact]
    public void TheOverrideRoundTrips_AndDefaultsToTheRunsOwn()
    {
        Assert.Null(new TraceConfig().ReferenceInputPowerDbmOverride);
        Assert.Null(new Trace(new SNP([1e9], 2), MatrixType.S, 0, 0, DependentVarFormat.Db)
                        .ReferenceInputPowerDbmOverride);
    }

    /// <summary>The φ index whose bearing is 180° away from <paramref name="fromIndex"/> on the
    /// fixture's own grid — found the way the resolve finds it, by value.</summary>
    private static int BackIndexOf(DataSet ds, int fromIndex)
    {
        var phi = ds[U].Axes.First(a => a.Name == "phi");
        double want = (phi.Values[fromIndex] + 180.0) % 360.0;
        int best = 0;
        for (int k = 1; k < phi.Length; k++)
            if (Math.Abs(phi.Values[k] - want) < Math.Abs(phi.Values[best] - want)) best = k;
        return best;
    }
}

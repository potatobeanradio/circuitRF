// ================================================================
//  PatternPlotTests.cs — ANT-7's gates.
//
//  §1's three claims are CONFIRMED here rather than assumed, on the real "farfield" group
//  PatternFixture solves for — because the brief's plan for §2 rests on them, and one of the three
//  turned out to be true only of a COMPLEX cube (see ThePolarRadialAxisIsGeneral… below).
//
//  Everything after that is the dB radial mode itself: the ring lattice, the floor, the two
//  references, the two trace spellings, and the sentences the plot states.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.DataDisplay;

public sealed class PatternPlotTests(ITestOutputHelper output)
{
    private const string U = "farfield.U";

    // ══ helpers ══════════════════════════════════════════════════════════════

    /// <summary>One trace over a cube, with the slice spelled the way the trace card spells it.</summary>
    private static Trace Resolve(DataSet ds, string spec, PlotType type, out string error)
    {
        Assert.True(CubeTraceSpecParser.TryParse(spec, ds, out string cube, out var slice,
                                                 out var transform, out error), error);
        var t = new Trace(new SNP([1e9], 2), MatrixType.S, 0, 0, DependentVarFormat.Db)
        {
            CubeName  = cube,
            Slice     = slice,
            Transform = transform,
        };
        TraceResolve.SetCubeDataFrom(t, ds, type, FreqUnit.GHz);
        TraceResolve.ApplyPinnedAxisDisplay(t, ds);
        return t;
    }

    private static Trace Resolve(DataSet ds, string spec, PlotType type) => Resolve(ds, spec, type, out _);

    /// <summary>A pattern plot carrying the traces given, with its scale already resolved.</summary>
    private static Plot PatternPlot(IEnumerable<Trace> traces, double floor = -40, double ring = 10,
                                    PolarDbReferenceMode reference = PolarDbReferenceMode.Peak,
                                    double referenceValue = 0, string unit = "")
    {
        var plot = new Plot(PlotType.Polar, FreqUnit.GHz)
        {
            PolarRadial           = PolarRadialMode.Db,
            PolarDbFloor          = floor,
            PolarDbRingStep       = ring,
            PolarDbReference      = reference,
            PolarDbReferenceValue = referenceValue,
            PolarDbUnit           = unit,
        };
        foreach (var t in traces) plot.Traces.Add(t);
        plot.Autoscale(force: true);
        return plot;
    }

    /// <summary>A hand-built pattern cube on ANT-4's own axes, so a shape can be CHOSEN — a pencil
    /// beam, a near-isotropic blob, a deep null — which a real structure will not oblige with.</summary>
    private static DataSet HandPattern(Func<double, double> uOfThetaDeg,
                                       double[]? thetaDeg = null, double thetaMax = 90)
    {
        double[] theta = thetaDeg ?? Enumerable.Range(0, 19).Select(i => i * (thetaMax / 18.0)).ToArray();
        double[] phi   = [0, 90, 180, 270];
        double[] port  = [1, 2];

        var values = new double[1 * theta.Length * phi.Length * port.Length];
        for (int t = 0; t < theta.Length; t++)
            for (int p = 0; p < phi.Length; p++)
                for (int q = 0; q < port.Length; q++)
                    // Port 2 is deliberately 20 dB down, so a test that pinned the wrong port
                    // cannot pass by accident.
                    values[((t * phi.Length) + p) * port.Length + q] =
                        uOfThetaDeg(theta[t]) * (q == 0 ? 1.0 : 0.01);

        var ds = new DataSet();
        ds.AddToGroup("farfield", "U", new DataCube(
        [
            new Axis("freq",  [5e9],  "Hz"),
            new Axis("theta", theta,  "deg"),
            new Axis("phi",   phi,    "deg"),
            new Axis("port",  port,   ""),
        ], values));
        return ds;
    }

    /// <summary>
    /// <b>A cut is a PLANE, so the plot draws BOTH branches and the back one is the &#966; + 180&#176;
    /// slice drawn at &#8722;angle.</b>
    ///
    /// <para>Measured on an imported 1.74 GHz patch, 2026-09-10: a polar cut drew a QUARTER of the
    /// disc. <c>PlanarBeamwidth</c> has defined a cut as both azimuths since ANT-5 — it refuses when
    /// the grid carries only one of them — so a beamwidth read off the picture was half the one the
    /// metric published, and ANT-7 §4 had already written its own hemisphere note for "a polar plot
    /// occupying a half-disc".</para>
    ///
    /// <para><b>The mirror is on the ANGLE and never on the value</b>, which is what this asserts:
    /// the back branch's radii are its OWN data against the plot's shared reference, and only the
    /// sign of x separates the two point sets on a symmetric fixture.</para>
    /// </summary>
    [Fact]
    public void TheBackBranchOfACut_DrawsAtNegativeAngle_AndKeepsItsOwnRadius()
    {
        var ds    = PatternFixture.Data;
        var front = Resolve(ds, $"db10({U}[0, :, 0, 1])", PlotType.Polar);   // phi = 0
        var back  = Resolve(ds, $"db10({U}[0, :, 4, 1])", PlotType.Polar);   // phi = 180
        back.MirrorPatternAngle = true;

        // The SAME slice again, unmirrored, so the assertion is "this is that reflected" and not a
        // second derivation of where the points ought to be.
        var unmirrored = Resolve(ds, $"db10({U}[0, :, 4, 1])", PlotType.Polar);
        var plot = PatternPlot([front, back, unmirrored]);

        Assert.NotEmpty(back.Points);
        Assert.Equal(unmirrored.Points.Count, back.Points.Count);
        for (int i = 0; i < back.Points.Count; i++)
        {
            Assert.Equal(-unmirrored.Points[i].X, back.Points[i].X, 5);   // angle negated
            Assert.Equal(unmirrored.Points[i].Y, back.Points[i].Y, 5);   // radius untouched
            Assert.Equal(Radius(unmirrored, i), Radius(back, i), 5);
        }

        // Broadside is on the axis, so the two branches MEET there rather than leaving a gap.
        Assert.Equal(front.Points[0].X, back.Points[0].X, 4);

        // Under the plot is the ANGLE AXIS and nothing else (owner, 2026-09-11 — see PatternCaption).
        // The sentences that used to be here, including the back-branch one, are gone; what the two
        // halves are is said by the two traces' own labels, which name their φ.
        var pair = PatternPlot(
        [
            Resolve(ds, $"db10({U}[0, :, 0, 1])", PlotType.Polar),
            Mirrored(Resolve(ds, $"db10({U}[0, :, 4, 1])", PlotType.Polar)),
        ]);

        Assert.Equal(["θ (deg)"], PatternCaption.Lines(pair));

        // THE STRIP SET DOES NOT MOVE WHEN THE BACK-HALF BOX DOES — reported 2026-09-11: ticking
        // the back-half checkbox made the Y-axis label appear and disappear with it, and the report
        // asked whether that was intended. It was: the strips filtered on MirrorPatternAngle
        // directly. They
        // dedupe on the LABEL now, which a back branch pinned at its own φ does not share with the
        // front one — so both halves are named, and named the same whichever way the box is set.
        var backTrace = pair.Traces[1];
        foreach (bool mirrored in new[] { true, false, true })
        {
            backTrace.MirrorPatternAngle = mirrored;
            var (l, r) = PlotLabelStrips.For(pair, showFilePrefix: false);
            Assert.Equal(2, l.Count);
            Assert.Empty(r);
        }

        // Two traces that ARE one curve — the same slice twice — still collapse to one strip, which
        // is what the rule was always for.
        var twice = PatternPlot(
        [
            Resolve(ds, $"db10({U}[0, :, 0, 1])", PlotType.Polar),
            Mirrored(Resolve(ds, $"db10({U}[0, :, 0, 1])", PlotType.Polar)),
        ]);
        Assert.Single(PlotLabelStrips.For(twice, showFilePrefix: false).Left);

        output.WriteLine("caption: " + string.Join(" | ", PatternCaption.Lines(pair)));
    }

    /// <summary>
    /// <b>One branch or two, the line under the plot is the same</b> — the angle axis. Nothing about
    /// the caption depends on the back-branch flag any more, which is what makes the strip set and
    /// the caption both stable under the checkbox.
    /// </summary>
    [Fact]
    public void OneBranchAlone_DrawsTheSameAngleLabel()
    {
        var plot = PatternPlot([Resolve(PatternFixture.Data, $"db10({U}[0, :, 0, 1])", PlotType.Polar)]);
        string note = Assert.Single(PatternCaption.Lines(plot));
        Assert.Equal("θ (deg)", note);
        output.WriteLine(note);
    }

    /// <summary>
    /// <b>The WINDOW'S OWN SAVE carries the back-branch flag.</b>
    ///
    /// <para>Caught while building it, and it is the failure that would have been worst: the
    /// renderer reads <c>TraceConfig.MirrorPatternAngle</c>, so a <c>.cdd</c> written by
    /// <c>Cli plot</c> draws the whole plane in the window — and
    /// <c>DataDisplayViewModel.BuildTraceConfig</c> did not write the field back, so the first SAVE
    /// silently returned the picture to a quarter-disc. A field the window can draw but not save is
    /// worse than one it cannot draw at all, because nothing about the moment it is lost is
    /// visible.</para>
    ///
    /// <para>Asserted through <c>BuildTraceConfig</c> and <c>PlotConfigLoader</c>, which are the two
    /// halves the application actually runs — a hand-copied pair of fields would agree with itself
    /// and prove nothing.</para>
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheWindowsOwnSave_RoundTripsTheBackBranchFlag(bool mirrored)
    {
        var trace = Resolve(PatternFixture.Data, $"db10({U}[0, :, 4, 1])", PlotType.Polar);
        trace.MirrorPatternAngle = mirrored;

        var tc = DataDisplayViewModel.BuildTraceConfig(trace, configDir: ".");
        Assert.Equal(mirrored, tc.MirrorPatternAngle);

        // And back out through the loader's own JSON, so an omitted field is exercised too.
        var round = JsonSerializer.Deserialize<TraceConfig>(
            JsonSerializer.Serialize(tc, DataDisplayJson.Options), DataDisplayJson.Options)!;
        Assert.Equal(mirrored, round.MirrorPatternAngle);
        output.WriteLine($"mirrored={mirrored} survived the window's save");
    }

    /// <summary>
    /// <b>A PATTERN RADIUS IS DECIBELS, and a complex cube with no dB transform is refused rather
    /// than drawn.</b>
    ///
    /// <para>Owner report, 2026-09-11: <c>Etheta</c> on a 3D surface drew a uniform pink hemisphere.
    /// With no transform <c>RectY</c> returns the LINEAR magnitude — volts — and against a peak
    /// reference that spans a hundredth of a dB, so every direction sits on the outer radius in the
    /// top colour. <b>A hemisphere is not an error shape</b>: it is what an isotropic radiator over a
    /// ground plane looks like, so nothing in the picture could have said otherwise.</para>
    /// </summary>
    [Theory]
    [InlineData("farfield.Etheta")]
    [InlineData("farfield.Ephi")]
    public void AComplexPatternCubeWithNoDbTransform_IsRefused_NotDrawnAsAHemisphere(string cube)
    {
        var ds = PatternFixture.Data;

        var bare = Resolve(ds, $"{cube}[0, :, 0, 1]", PlotType.Polar);
        PatternPlot([bare]);
        Assert.True(bare.PatternValueInvalid, "a linear magnitude was accepted as dB");
        Assert.Empty(bare.Points);
        Assert.Contains("<invalid", bare.RectYLabel("U", dimensionMismatch: false));

        // db20 is the spelling that answers it, and the refusal names it.
        var db20 = Resolve(ds, $"db20({cube}[0, :, 0, 1])", PlotType.Polar);
        PatternPlot([db20]);
        Assert.False(db20.PatternValueInvalid);
        Assert.NotEmpty(db20.Points);
        output.WriteLine(Trace.PatternValueRefusal);
    }

    /// <summary>
    /// <b>A REAL cube with no transform is still drawn</b>, and that is the deliberate half of the
    /// rule rather than an oversight: it is how an already-dB cube (<c>GainDbi</c>,
    /// <c>CoPolLudwig3Db</c>) is legitimately plotted, and nothing on the cube says whether it is dB
    /// — ANT-7 §8 records the missing unit. So a linear real cube is still drawable and still wrong;
    /// only the case the plot can PROVE is refused.
    /// </summary>
    [Fact]
    public void ARealPatternCubeWithNoTransform_IsStillDrawn()
    {
        var t = Resolve(PatternFixture.Data, $"{U}[0, :, 0, 1]", PlotType.Polar);
        PatternPlot([t]);
        Assert.False(t.PatternValueInvalid);
        Assert.NotEmpty(t.Points);
    }

    /// <summary>The same trace, flagged as the back half of its cut.</summary>
    private static Trace Mirrored(Trace t) { t.MirrorPatternAngle = true; return t; }

    private static double Radius(Trace t, int i) =>
        Math.Sqrt(t.Points[i].X * t.Points[i].X + t.Points[i].Y * t.Points[i].Y);

    // ══ §1 — the three claims, on a real "farfield" cube ═════════════════════

    /// <summary>
    /// <b>§1 claim 1 — a rectangular pattern cut works today with no changes at all.</b> Confirmed:
    /// θ on x in degrees, dB on y, four samples for the fixture's four θ points.
    /// </summary>
    [Fact]
    public void RectangularPatternCuts_WorkWithNoChangesAtAll()
    {
        var ds = PatternFixture.Data;
        var t  = Resolve(ds, $"db10({U}[0, :, 0, 1])", PlotType.Rect);

        Assert.Equal(4, t.Points.Count);
        Assert.Equal("theta", t.CubeXAxisName);
        Assert.Equal("deg",   t.CubeXUnit);
        Assert.Equal([0f, 30f, 60f, 90f], t.Points.Select(p => p.X).ToArray());

        // The y values ARE 10·log10(U) of the cube, not something re-derived.
        var cube = ds[U];
        for (int i = 0; i < 4; i++)
            Assert.Equal(DbFloor.Db10(cube[0, i, 0, 0].RealValue!.Value), t.Points[i].Y, 3);

        output.WriteLine("§1.1 confirmed — Rect cut: " +
            string.Join(", ", t.Points.Select(p => $"θ={p.X}° {p.Y:0.00} dB")));
    }

    /// <summary>
    /// <b>§1 claim 2 — pinning the extra axes is already expressible.</b> Three separate statements,
    /// all confirmed: an int PINS, a Range KEEPS, and a MISSING entry defaults to pin-index-0.
    /// </summary>
    [Fact]
    public void PinningTheExtraAxes_IsAlreadyExpressible()
    {
        var ds   = PatternFixture.Data;
        var cube = ds[U];

        // An int pins: phi index 2 (90°) differs from phi index 0, and the trace follows the pin.
        var pinned = Resolve(ds, $"db10({U}[0, :, 2, 1])", PlotType.Rect);
        for (int i = 0; i < 4; i++)
            Assert.Equal(DbFloor.Db10(cube[0, i, 2, 0].RealValue!.Value), pinned.Points[i].Y, 3);

        // A Range keeps — and narrows. 1..3 is end-exclusive, so two of the four θ samples.
        var ranged = Resolve(ds, $"db10({U}[0, 1..3, 0, 1])", PlotType.Rect);
        Assert.Equal(2, ranged.Points.Count);
        Assert.Equal([30f, 60f], ranged.Points.Select(p => p.X).ToArray());

        // A MISSING entry defaults to pin-index-0: the slice below names only theta, and the freq,
        // phi and port axes resolve to their first sample without being mentioned.
        var sparse = new Trace(new SNP([1e9], 2), MatrixType.S, 0, 0, DependentVarFormat.Db)
        {
            CubeName  = U,
            Slice     = [new AxisSlice("theta", AxisRole.KeepAsX, 0)],
            Transform = CubeTransform.dB10,
        };
        TraceResolve.SetCubeDataFrom(sparse, ds, PlotType.Rect, FreqUnit.GHz);
        Assert.Equal(4, sparse.Points.Count);
        for (int i = 0; i < 4; i++)
            Assert.Equal(DbFloor.Db10(cube[0, i, 0, 0].RealValue!.Value), sparse.Points[i].Y, 3);

        output.WriteLine("§1.2 confirmed — int pins, Range keeps, a missing entry pins index 0.");
    }

    /// <summary>
    /// <b>§1 claim 3, and it is the one that needed measuring.</b>
    ///
    /// <para>The RADIAL AXIS half is true: <see cref="AxesRenderer.PolarRings"/> frames on whatever
    /// radius it is given and is in no way locked to |Γ| ≤ 1 — it returns the same five-ring lattice
    /// at 1, at 12.5 and at 0.004.</para>
    ///
    /// <para><b>The brief's gloss on it — "a linear-magnitude pattern renders on it today" — is NOT
    /// true of a pattern cube.</b> <c>Trace.BuildCubePath</c>'s complex-plot branch required a
    /// COMPLEX cube and returned no points for a real one, and every pattern quantity worth plotting
    /// (U, gain in dBi, the Ludwig-3 pair) is real. What did draw was a complex cube's Re/Im locus —
    /// which is not a pattern at all, and is exactly the "derived complex expression" §2 refuses. So
    /// the mode had to add the real-cube path, not merely re-label an existing one.</para>
    /// </summary>
    [Fact]
    public void ThePolarRadialAxisIsGeneral_ButARealPatternCubeDrewNothingOnIt()
    {
        foreach (double rMax in new[] { 1.0, 2.0, 12.5, 50.0, 0.004 })
        {
            var (step, sub) = AxesRenderer.PolarRings(rMax);
            Assert.True(step > 0 && sub > 1);
            int rings = (int)Math.Floor(rMax / step);
            Assert.InRange(rings, 3, 7);
            output.WriteLine($"§1.3 rMax={rMax}: step={step}, {rings} rings, {sub} subdivisions");
        }

        // The measurement the brief's gloss needed. With no pattern scale on the plot — which is the
        // behaviour that shipped before ANT-7 — a REAL cube produces no points on a Polar plot…
        var ds   = PatternFixture.Data;
        var real = Resolve(ds, $"{U}[0, :, 0, 1]", PlotType.Polar);
        Assert.Empty(real.Points);

        // …while a COMPLEX one draws its Re/Im locus, which is not a pattern.
        var cplx = Resolve(ds, $"farfield.Etheta[0, :, 0, 1]", PlotType.Polar);
        Assert.Equal(4, cplx.Points.Count);
        var e0 = ds["farfield.Etheta"][0, 0, 0, 0].ComplexValue!.Value;
        Assert.Equal((float)e0.Real,      cplx.Points[0].X, 3);
        Assert.Equal((float)e0.Imaginary, cplx.Points[0].Y, 3);

        output.WriteLine("§1.3 — the radial axis IS general; a REAL pattern cube drew nothing on it "
                       + "before this brief, and a complex one drew a Re/Im locus rather than a pattern.");
    }

    // ══ §2 — the dB radial mode ══════════════════════════════════════════════

    /// <summary>The ring lattice, the floor and the label text on a HIGH-DIRECTIVITY pattern — a
    /// cos^40 θ pencil beam, whose whole interesting range is the top few decibels.</summary>
    [Fact]
    public void HighDirectivity_LaysTenDbRingsFromThePeakDownToTheFloor()
    {
        var ds   = HandPattern(th => Math.Pow(Math.Cos(th * Math.PI / 180.0), 40));
        var t    = Resolve(ds, $"db10({U}[0, :, 0, 1])", PlotType.Polar);
        var plot = PatternPlot([t]);

        var scale = plot.PatternScale!;
        Assert.True(scale.Normalised);
        Assert.Equal(0.0,   scale.ReferenceDb, 9);   // cos^40(0) = 1 → 0 dB
        Assert.Equal(-40.0, scale.FloorDb,     9);
        Assert.Equal([0.0, -10.0, -20.0, -30.0], scale.RingsDb.ToArray());
        Assert.Equal(["0 dB", "-10", "-20", "-30"],
                     scale.RingsDb.Select((r, k) => scale.RingLabel(r, withUnit: k == 0)).ToArray());

        // The outer ring is radius 1 and the peak is on it.
        Assert.Equal(1.0, scale.Radius(scale.ReferenceDb), 9);
        Assert.Equal(0.0, scale.Radius(scale.FloorDb),     9);
        Assert.Equal(1.0, Radius(t, 0), 4);

        // …and the window is the unit disc, so the rings mean what they are labelled.
        Assert.Equal(-1.0, plot.Axes.Window.X,     9);
        Assert.Equal(2.0,  plot.Axes.Window.Width, 9);
        output.WriteLine("high directivity: " + scale.ReferenceCaption());
    }

    /// <summary>
    /// A NEAR-ISOTROPIC pattern — 1 dB of variation across the whole hemisphere. On the default
    /// 40 dB disc it is a ring of curve hard against the rim, which is exactly why the floor is a
    /// CONTROL: at −3 dB the same data fills the disc.
    /// </summary>
    [Fact]
    public void NearIsotropic_IsWhyTheFloorIsAControl()
    {
        var ds = HandPattern(th => Math.Pow(10.0, -0.1 * (th / 90.0)));   // 0 → −1 dB

        var wide  = PatternPlot([Resolve(ds, $"db10({U}[0, :, 0, 1])", PlotType.Polar)]);
        var tight = PatternPlot([Resolve(ds, $"db10({U}[0, :, 0, 1])", PlotType.Polar)], floor: -3, ring: 1);

        var tw = wide.Traces[0];
        var tt = tight.Traces[0];

        // Same data, same peak; the 40 dB disc squeezes it into the outermost 2.5 % of the radius
        // and the 3 dB one spreads it over a third.
        Assert.True(Radius(tw, 0) - Radius(tw, tw.Points.Count - 1) < 0.03,
                    $"40 dB disc should flatten a 1 dB pattern; span was {Radius(tw, 0) - Radius(tw, tw.Points.Count - 1)}");
        Assert.True(Radius(tt, 0) - Radius(tt, tt.Points.Count - 1) > 0.3,
                    $"3 dB disc should open it up; span was {Radius(tt, 0) - Radius(tt, tt.Points.Count - 1)}");

        Assert.Equal([0.0, -1.0, -2.0], tight.PatternScale!.RingsDb.Select(v => Math.Round(v, 9)).ToArray());
        output.WriteLine($"near-isotropic: 40 dB disc spans {Radius(tw, 0) - Radius(tw, tw.Points.Count - 1):0.000} of the "
                       + $"radius, 3 dB disc spans {Radius(tt, 0) - Radius(tt, tt.Points.Count - 1):0.000}");
    }

    /// <summary>
    /// <b>The case that distinguishes "drawn at the floor" from "clipped" (§5).</b> A pattern with an
    /// exact null at θ = 45°: the sample is BELOW the floor by any margin you like — an exact zero is
    /// −∞ dB — and it must still be a POINT, at the centre. A dropped sample would leave a gap, and a
    /// gap in a pattern trace reads as a null in the antenna, which is the one thing that must not be
    /// indistinguishable from a real one.
    /// </summary>
    [Fact]
    public void ADeepNull_IsDrawnAtTheFloorAndNeverOmitted()
    {
        double[] theta = [0, 15, 30, 45, 60, 75, 90];
        var ds = HandPattern(th => Math.Abs(th - 45) < 1e-9 ? 0.0 : Math.Cos(th * Math.PI / 180.0),
                             thetaDeg: theta);

        var t    = Resolve(ds, $"db10({U}[0, :, 0, 1])", PlotType.Polar);
        var plot = PatternPlot([t]);

        // Nothing was dropped: one point per sample, the null included.
        Assert.Equal(theta.Length, t.Points.Count);

        // …and the null is AT the centre, not missing and not at the rim.
        Assert.Equal(0.0, Radius(t, 3), 6);
        Assert.True(Radius(t, 2) > 0.9, "the shoulders either side of the null are near the rim");
        Assert.True(Radius(t, 4) > 0.9);

        // The reference took no notice of the null — an exact zero is not finite in dB and DbFloor's
        // own -250 clamp is what stops it from becoming the scale.
        Assert.Equal(0.0, plot.PatternScale!.ReferenceDb, 6);
        output.WriteLine("deep null: radii " + string.Join(", ", Enumerable.Range(0, t.Points.Count)
                                                                          .Select(i => Radius(t, i).ToString("0.000"))));
    }

    /// <summary>Normalised and absolute both render, and each SAYS which — §5, and §6's "do not draw
    /// an unlabelled normalised pattern".</summary>
    [Fact]
    public void NormalisedAndAbsolute_BothRenderAndBothSayWhichTheyAre()
    {
        var ds = HandPattern(th => 4.0 * Math.Pow(Math.Cos(th * Math.PI / 180.0), 4));   // peak 6.02 dB

        var norm = PatternPlot([Resolve(ds, $"db10({U}[0, :, 0, 1])", PlotType.Polar)], unit: "dBi");
        var abs  = PatternPlot([Resolve(ds, $"db10({U}[0, :, 0, 1])", PlotType.Polar)],
                               reference: PolarDbReferenceMode.Absolute, referenceValue: 10, unit: "dBi");

        Assert.Contains("normalised", norm.PatternScale!.ReferenceCaption());
        Assert.Contains("6.02 dBi",   norm.PatternScale.ReferenceCaption());
        Assert.Equal("0 dBi", norm.PatternScale.RingLabel(norm.PatternScale.RingsDb[0], true));

        Assert.Contains("absolute",  abs.PatternScale!.ReferenceCaption());
        Assert.Contains("10 dBi",    abs.PatternScale.ReferenceCaption());
        Assert.Equal("10 dBi", abs.PatternScale.RingLabel(abs.PatternScale.RingsDb[0], true));

        // The same sample sits at a DIFFERENT radius under the two references, which is the whole
        // reason the plot has to say which it is: the pictures are otherwise identical in shape.
        Assert.Equal(1.0, Radius(norm.Traces[0], 0), 6);
        Assert.Equal((6.0206 - -30.0) / 40.0, Radius(abs.Traces[0], 0), 3);

        // The sentence is still COMPOSED — it is the text every refusal and every diagnostic quotes
        // — but it is no longer DRAWN under the plot (owner, 2026-09-11). What a reader reads the
        // reference off is the outer ring's own number with its unit, asserted above.
        Assert.Equal(["θ (deg)"], PatternCaption.Lines(norm));
        Assert.Equal(["θ (deg)"], PatternCaption.Lines(abs));
        output.WriteLine(norm.PatternScale.ReferenceCaption() + "\n" + abs.PatternScale.ReferenceCaption());
    }

    /// <summary>An absolute reference BELOW the data flattens samples onto the outer ring — and the
    /// plot counts them and says so, rather than letting a clipped peak read as a measured one.</summary>
    [Fact]
    public void AnAbsoluteReferenceBelowTheData_IsReportedRatherThanHidden()
    {
        var ds   = HandPattern(th => 4.0 * Math.Pow(Math.Cos(th * Math.PI / 180.0), 4));
        var plot = PatternPlot([Resolve(ds, $"db10({U}[0, :, 0, 1])", PlotType.Polar)],
                               reference: PolarDbReferenceMode.Absolute, referenceValue: 0);

        Assert.True(plot.PatternScale!.AboveReferenceCount > 0);
        Assert.Contains("above the reference", plot.PatternScale.ReferenceCaption());
        Assert.Equal(1.0, Radius(plot.Traces[0], 0), 6);
        output.WriteLine(plot.PatternScale.ReferenceCaption());
    }

    /// <summary>
    /// <b>The reference is per-PLOT, not per-trace.</b> Two cuts 20 dB apart must keep their 20 dB —
    /// each normalised to its own peak would put both on the outer ring and say the antenna is
    /// equally strong in two planes it is not.
    /// </summary>
    [Fact]
    public void TwoCuts_ShareOneReference()
    {
        var ds = HandPattern(th => Math.Pow(Math.Cos(th * Math.PI / 180.0), 4));
        var a  = Resolve(ds, $"db10({U}[0, :, 0, 1])", PlotType.Polar);   // port 1
        var b  = Resolve(ds, $"db10({U}[0, :, 0, 2])", PlotType.Polar);   // port 2, 20 dB down
        var plot = PatternPlot([a, b]);

        Assert.Equal(1.0, Radius(a, 0), 6);
        Assert.Equal((-20.0 + 40.0) / 40.0, Radius(b, 0), 6);
        output.WriteLine($"shared reference: cut A peak r={Radius(a, 0):0.000}, cut B peak r={Radius(b, 0):0.000}");
    }

    /// <summary>A plot whose X axis is not an ANGLE is refused rather than drawn as a plausible
    /// spiral — the label says so, exactly as a complex-on-Rect trace does.</summary>
    [Fact]
    public void ANonAngularSweep_IsRefusedRatherThanDrawn()
    {
        var ds = PatternFixture.Data;
        var t  = Resolve(ds, "db10(farfield.PowerRadiated[:, 1])", PlotType.Polar);
        var plot = PatternPlot([t]);

        Assert.True(t.PatternAxisInvalid);
        Assert.Empty(t.Points);
        Assert.Contains("<invalid", t.RectYLabel("PowerRadiated", false));
        output.WriteLine(t.RectYLabel("PowerRadiated", false));
    }

    // ══ §4 — what the plot must state ════════════════════════════════════════

    /// <summary>
    /// <b>The line under the plot is the TRACE'S OWN swept axis, in its own symbol</b> — read off the
    /// resolved axis rather than assumed, so a φ cut and a θ cut are not both called θ. That is the
    /// one thing the picture could not otherwise say: the two are the same disc with the same rings
    /// and different meanings.
    ///
    /// <para>The hemisphere sentence this test used to assert is gone with the rest of the caption
    /// (owner, 2026-09-11). A θ axis stopping at 90° still draws a half-disc, which is the model's
    /// own limit and is stated in the run's notes and in the metric registry's own wording.</para>
    /// </summary>
    [Fact]
    public void TheAngleLabel_ComesFromTheAxisTheTraceActuallySwept()
    {
        var theta = PatternPlot([Resolve(PatternFixture.Data, $"db10({U}[0, :, 0, 1])", PlotType.Polar)]);
        var phi   = PatternPlot([Resolve(PatternFixture.Data, $"db10({U}[0, 3, :, 1])", PlotType.Polar)]);

        Assert.Equal(["θ (deg)"], PatternCaption.Lines(theta));
        Assert.Equal(["φ (deg)"], PatternCaption.Lines(phi));

        // A LINEAR polar plot is a locus, not a pattern, and says nothing under itself.
        var linear = PatternPlot([Resolve(PatternFixture.Data, $"db10({U}[0, :, 0, 1])", PlotType.Polar)]);
        linear.PolarRadial = PolarRadialMode.Linear;
        Assert.Empty(PatternCaption.Lines(linear));

        output.WriteLine("θ cut and φ cut label their own axes");
    }

    /// <summary>
    /// §4's other three items — which cut, which port, which frequency — are the trace's own pinned
    /// axes, and the LABEL already carries all three. Confirmed here rather than duplicated into the
    /// caption, which is why <see cref="PatternCaption"/> does not restate them.
    ///
    /// <para>ANT-5 attaches no NAME to a cut: a derived plane lands on the beamwidth cube's own
    /// <c>cut</c> axis as a φ in degrees and the "derived" flag lives in the run's notes, so the φ
    /// IS the cut's name here.</para>
    /// </summary>
    [Fact]
    public void TheCutThePortAndTheFrequency_AreAlreadyInTheTraceLabel()
    {
        var ds = PatternFixture.Data;
        var t  = Resolve(ds, $"db10({U}[0, :, 2, 2])", PlotType.Polar);

        string label = TraceLabeler.ComputeMinimalLabels([t])[0];
        Assert.Contains("φ=90 deg", label);      // the SYMBOL, not the cube's ASCII axis name
        Assert.Contains("port=2",     label);
        Assert.Contains("freq=5",     label);

        // No cube in the "farfield" group names a cut in words — the φ is the name.
        Assert.DoesNotContain(ds.CubesIn("farfield").Keys, k => k.Contains("Plane", StringComparison.Ordinal));
        output.WriteLine("trace label: " + label);
    }

    // ══ §3 — the port axis is a 1-based PORT NUMBER ══════════════════════════

    /// <summary>
    /// <b>An integer on a <c>port</c> axis is a 1-based PORT NUMBER, not an index.</b> The fixture's
    /// port 2 is a real second port, so an off-by-one here would draw the other port's pattern in
    /// silence — which on a reciprocal structure is invisible.
    /// </summary>
    [Fact]
    public void AnIntegerOnAPortAxis_IsAPortNumber()
    {
        var ds   = PatternFixture.Data;
        var cube = ds[U];

        var p1 = Resolve(ds, $"db10({U}[0, :, 0, 1])", PlotType.Rect);
        var p2 = Resolve(ds, $"db10({U}[0, :, 0, 2])", PlotType.Rect);

        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(DbFloor.Db10(cube[0, i, 0, 0].RealValue!.Value), p1.Points[i].Y, 3);
            Assert.Equal(DbFloor.Db10(cube[0, i, 0, 1].RealValue!.Value), p2.Points[i].Y, 3);
        }

        // A port the run does not have is refused BY NAME, listing the ports it does.
        Assert.False(CubeTraceSpecParser.TryParse($"{U}[0, :, 0, 3]", ds, out _, out _, out _, out string err));
        Assert.Contains("Port 3", err);
        Assert.Contains("1, 2",   err);

        // Port 0 is not an index-0 alias — it is simply not a port.
        Assert.False(CubeTraceSpecParser.TryParse($"{U}[0, :, 0, 0]", ds, out _, out _, out _, out string zero));
        Assert.Contains("Port 0", zero);

        output.WriteLine("port axis refusal: " + err);
    }

    /// <summary>
    /// harmonicaRF's own <c>port</c> axis holds 0, 1, 2 … rather than port numbers, and the
    /// value-lookup rule leaves every one of its existing specs meaning exactly what it meant — which
    /// is why the rule is a lookup in the axis VALUES and not a subtraction of one.
    /// </summary>
    [Fact]
    public void AZeroBasedPortAxis_StillMeansWhatItAlwaysMeant()
    {
        var ds = new DataSet();
        ds.Add("V_intr", new DataCube(
        [
            new Axis("port",     [0, 1, 2], "", ["gate", "drain", "source"]),
            new Axis("harmonic", [0, 1, 2], ""),
        ], new Complex[9].Select((_, i) => new Complex(i, 0)).ToArray()));

        Assert.True(CubeTraceSpecParser.TryParse("V_intr[1, :]", ds, out _, out var slice, out _, out string e), e);
        Assert.Equal(1, slice![0].Index);      // value 1 lives at index 1 — unchanged
    }

    // ══ Theme ════════════════════════════════════════════════════════════════

    /// <summary>Both variants draw, and differently — the same gate every other plot carries.</summary>
    [Fact]
    public void ThePatternPlot_DrawsOnBothThemes()
    {
        var ds   = HandPattern(th => Math.Pow(Math.Cos(th * Math.PI / 180.0), 8));
        var plot = PatternPlot([Resolve(ds, $"db10({U}[0, :, 0, 1])", PlotType.Polar)], unit: "dBi");

        string light = Svg(plot, RenderTheme.Light);
        string dark  = Svg(plot, RenderTheme.Dark);

        Assert.NotEqual(light, dark);
        foreach (string svg in new[] { light, dark })
        {
            // The ring lattice: three inner rings plus the boundary, as <ellipse> — which is what
            // Skia's SVG device writes a circle as, and nothing else on this plot emits one.
            Assert.Equal(4, System.Text.RegularExpressions.Regex.Matches(svg, "<ellipse").Count);

            // Every ring's own dB number, and the unit on the outer one.
            string text = string.Join(" ", System.Text.RegularExpressions.Regex
                .Matches(svg, ">([^<]*)</text>").Select(m => m.Groups[1].Value));
            foreach (string want in new[] { "0 dBi", "-10", "-20", "-30" })
                Assert.Contains(want, text);

            // …and the one line under the plot, which is the angle axis (owner, 2026-09-11).
            Assert.Contains("θ (deg)", text);
            Assert.DoesNotContain("normalised", text);
            Assert.DoesNotContain("hemisphere", text);
        }
        output.WriteLine($"light {light.Length} bytes, dark {dark.Length} bytes");
    }

    /// <summary>
    /// The application's OWN SVG writer, so what is asserted is what an export contains — and at the
    /// canvas height <see cref="PlotCanvasGeometry.BottomLabelExtraLogical"/> asks for, which is how
    /// every real caller sizes a complex plot. At a bare square the caption rows fall off the bottom,
    /// which is precisely what that function exists to prevent.
    /// </summary>
    private static string Svg(Plot plot, RenderTheme theme)
    {
        const float w = 420f;
        float h = w + (float)PlotCanvasGeometry.BottomLabelExtraLogical(plot, w);
        return PlotDocumentWriter.BuildSvgString(
            canvas => PlotRenderer.Draw(canvas, (w, h), plot, PlotDetail.Full, theme),
            new PagePlacement(w, h, 0));
    }
}

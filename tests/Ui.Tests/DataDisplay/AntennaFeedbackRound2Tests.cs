// ================================================================
//  AntennaFeedbackRound2Tests.cs — the antenna-display defects and
//  requests of 2026-09-11, round two, one test each.
//
//  Round one (AntennaFeedbackTests) was about what the run PUBLISHED.
//  This round is almost entirely about the window: a plot that cannot
//  be picked up, a panel that changes width when a checkbox moves, a
//  card row that says "port=1" on a one-port antenna, and three
//  sentences under every pattern that a reader had stopped reading.
//
//  Each assertion is written against the thing the user could SEE.
// ================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using CircuitRF.Engine.Mom;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.DataDisplay;

public sealed class AntennaFeedbackRound2Tests(ITestOutputHelper output)
{
    // ══ fixtures ═════════════════════════════════════════════════════════════

    /// <summary>A per-point metric cube on the axes a sweep publishes — <c>[freq, port]</c> — with
    /// the port count under the caller's control, which is the whole subject of §2.</summary>
    private static DataSet Metric(string name, string unit, int ports, params double[] values)
    {
        var ds = new DataSet();
        ds.AddToGroup(PlanarFarField.Group, name, new DataCube(
        [
            new Axis("freq", [1.74e9, 2.4e9], "Hz"),
            new Axis("port", Enumerable.Range(1, ports).Select(i => (double)i).ToArray(), ""),
        ], Enumerable.Range(0, 2 * ports).Select(i => values[i % values.Length]).ToArray()) { Unit = unit });
        return ds;
    }

    private static Trace Resolve(DataSet ds, string spec, PlotType type = PlotType.Rect)
    {
        Assert.True(CubeTraceSpecParser.TryParse(spec, ds, out string cube, out var slice,
                                                 out var transform, out string error), error);
        var t = new Trace(new SNP([1e9], 2), MatrixType.S, 0, 0, DependentVarFormat.Db)
        {
            CubeName = cube, Slice = slice, Transform = transform,
        };
        TraceResolve.SetCubeDataFrom(t, ds, type, FreqUnit.GHz);
        TraceResolve.ApplyPinnedAxisDisplay(t, ds, FreqUnit.GHz);
        return t;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // §1 — the radiation efficiency in percent, and a decibel form beside it
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The registry publishes the efficiency twice, in the two scales it is quoted in</b>, and
    /// the percentage is a hundred times the fraction rather than a relabelled one.
    /// </summary>
    [Fact]
    public void TheEfficiencyIsPublishedInPercentAndInDecibels()
    {
        var pc = PlanarMetrics.Of(PlanarMetric.RadiationEfficiency);
        var db = PlanarMetrics.Of(PlanarMetric.RadiationEfficiencyDb);

        Assert.Equal("%",  pc.Unit);
        Assert.Equal("dB", db.Unit);
        Assert.Equal("RadiationEfficiencyDb", db.CubeName);
        Assert.Equal(PlanarMetricAxis.PerPoint, db.Axis);

        // Both names are in the ONE registry every picker, exporter and listing reads.
        Assert.Contains("RadiationEfficiency",   PlanarMetrics.Registry.Select(d => d.CubeName));
        Assert.Contains("RadiationEfficiencyDb", PlanarMetrics.Registry.Select(d => d.CubeName));
        output.WriteLine($"{pc.CubeName} [{pc.Unit}] and {db.CubeName} [{db.Unit}]");
    }

    /// <summary>
    /// <b>A percentage says so on the axis</b> — "RadiationEfficiency (%)", which is what the
    /// 2026-09-11 request asked the Y-axis label to end in. Without it 92 reads as a ratio and is
    /// wrong by two orders of magnitude, which is invisible on a plot whose Y axis autoscales to
    /// whatever is on it.
    /// </summary>
    [Fact]
    public void APercentCube_PutsTheUnitOnTheAxisLabel()
    {
        var ds = Metric("RadiationEfficiency", "%", ports: 1, 62.6, 58.1);
        var t  = Resolve(ds, "farfield.RadiationEfficiency[:, 1]");

        string label = TraceLabeler.QuantityFor(t);
        Assert.EndsWith(" (%)", label);
        Assert.Equal(label, TraceLabeler.ComputeMinimalLabels([t])[0]);
        Assert.Equal(label, t.RectYLabel(label, dimensionMismatch: false));

        // A cube with any OTHER unit is untouched — the rule is about the one unit whose absence
        // changes the reading by a factor of a hundred, not about units in general.
        var dbi = Resolve(Metric("GainDbi", "dBi", ports: 1, 6.2, 5.9), "farfield.GainDbi[:, 1]");
        Assert.DoesNotContain("(", TraceLabeler.QuantityFor(dbi));

        output.WriteLine("y-axis: " + label);
    }

    /// <summary>
    /// A TRANSFORM replaces the scale, so it replaces the unit. "dB10 (%)" would name a unit that is
    /// not what is on the axis — which is exactly why the decibel form is its own published cube
    /// rather than a transform of this one.
    /// </summary>
    [Fact]
    public void ATransformedPercentCube_DoesNotClaimToBeInPercent()
    {
        var ds = Metric("RadiationEfficiency", "%", ports: 1, 62.6, 58.1);
        var t  = Resolve(ds, "db10(farfield.RadiationEfficiency[:, 1])");

        string label = TraceLabeler.QuantityFor(t);
        Assert.Contains("dB10", label);
        Assert.DoesNotContain("(%)", label);
        output.WriteLine("y-axis: " + label);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // §2 — a one-port dataset should not spend a card row, or a label, on the port
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>"port=1" is not said on a one-port antenna, and IS said as soon as there are two.</b> The
    /// test is the axis LENGTH: on a two-port run the token is the whole point of the label.
    /// </summary>
    [Fact]
    public void ASinglePortAxis_SaysNothingInTheLabel_AndATwoPortOneStillDoes()
    {
        var one = Resolve(Metric("GainDbi", "dBi", ports: 1, 6.2), "farfield.GainDbi[:, 1]");
        var two = Resolve(Metric("GainDbi", "dBi", ports: 2, 6.2, 5.1), "farfield.GainDbi[:, 2]");

        Assert.DoesNotContain("port", TraceLabeler.QuantityFor(one));
        Assert.Contains("port=2",     TraceLabeler.QuantityFor(two));

        // And the suppression is an EMPTY token, not a missing one — a missing entry falls back to
        // the raw index and would have printed "port=0", which is worse than what was reported.
        Assert.Equal("", one.PinnedAxisDisplay("port"));
        output.WriteLine($"one port: {TraceLabeler.QuantityFor(one)} | two: {TraceLabeler.QuantityFor(two)}");
    }

    /// <summary>
    /// <b>The card's own row goes with it</b> — a combo box offering one choice, on every trace card
    /// of every single-port antenna. It is HIDDEN rather than never built, so the axis keeps its
    /// entry in the slice the card writes back.
    /// </summary>
    [Fact]
    public void ASinglePortAxisRow_IsHiddenOnTheCard()
    {
        var row = AxisRow("port", ["1"]);
        Assert.False(row.IsRowVisible);

        Assert.True(AxisRow("port", ["1", "2"]).IsRowVisible);          // two ports: a real choice
        Assert.True(AxisRow("freq", ["1.74 GHz"]).IsRowVisible);        // one frequency is still a choice
        Assert.True(AxisRow("port", ["1"], isX: true).IsRowVisible);    // never hidden while it is X

        output.WriteLine("one-value port row hidden; every other one-value row kept");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // §3 — the angle axes are shown as symbols rather than as their ASCII names
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>&#952; and &#966; on the card and on the axis, from ONE mapping</b> — the point being that
    /// the two cannot spell an axis differently. The cube, the slice and every spec keep the ASCII
    /// name, which is what a user types.
    /// </summary>
    [Fact]
    public void TheAngleAxesAreShownAsSymbols_OnTheCardAndOnTheLabel()
    {
        Assert.Equal("θ", AxisSymbols.Display("theta"));
        Assert.Equal("φ", AxisSymbols.Display("phi"));
        Assert.Equal("freq",   AxisSymbols.Display("freq"));
        Assert.Equal("cut",    AxisSymbols.Display("cut"));   // a plane, not an angle — see AxisSymbols

        Assert.Equal("θ (deg)", AxisRow("theta", ["0"], unit: "deg").AxisLabel);
        Assert.Equal("φ (deg)", AxisRow("phi",   ["0"], unit: "deg").AxisLabel);
        Assert.Equal("freq (GHz)",   AxisRow("freq",  ["1.74"], unit: "GHz").AxisLabel);

        var t = Resolve(PatternFixture.Data, "db10(farfield.U[0, :, 2, 1])", PlotType.Polar);
        Assert.Contains("φ=90 deg", TraceLabeler.QuantityFor(t));
        Assert.DoesNotContain("phi", TraceLabeler.QuantityFor(t));
        output.WriteLine("label: " + TraceLabeler.QuantityFor(t));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // §4 — a 3D pattern plot could not be dragged: every press rotated it
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The scene rotates and the furniture does not.</b> A press on the caption line under the
    /// scene or on the colour bar down the right is an ordinary press, which is what lets the
    /// container pick the plot up and move it — before this, every press on a Surface3D plot started
    /// a rotate and marked itself handled, so the plot could not be moved at all.
    /// </summary>
    [Fact]
    public void OnlyThePatternItselfRotates_TheCaptionAndTheColourBarDoNot()
    {
        var plot = Surface3D();
        (double W, double H) size = (520, 420);

        Assert.True(SurfaceRenderer.HitsPattern(size, plot, size.W / 2, size.H / 2),
                    "the middle of the scene did not rotate");

        Assert.False(SurfaceRenderer.HitsPattern(size, plot, size.W / 2, size.H - 2),
                     "the caption line under the scene started a rotate");
        Assert.False(SurfaceRenderer.HitsPattern(size, plot, size.W - 2, size.H / 2),
                     "the colour bar started a rotate");
        Assert.False(SurfaceRenderer.HitsPattern(size, plot, 2, 2),
                     "the top-left corner, outside the scene square, started a rotate");

        output.WriteLine("scene rotates; caption block and colour bar drag");
    }

    /// <summary>
    /// <b>With nothing plotted, the whole plot drags</b> — the owner's second sentence. There is no
    /// pattern to be near, so every part of it is furniture.
    /// </summary>
    [Fact]
    public void AnEmptySurfacePlot_NeverRotates()
    {
        var empty = new Plot(PlotType.Surface3D, FreqUnit.GHz);
        (double W, double H) size = (520, 420);

        foreach (var (x, y) in new[] { (size.W / 2, size.H / 2), (4.0, 4.0), (size.W - 4, size.H - 4) })
            Assert.False(SurfaceRenderer.HitsPattern(size, empty, x, y));

        output.WriteLine("an empty 3D plot drags from anywhere");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // §5 — the two cut checkboxes, as one control
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>One control, three named states, and it never leaves the card.</b> Reported 2026-09-11:
    /// the back-half checkbox disappeared while whole-plane was on and the card shifted under the
    /// pointer. The states are mutually exclusive, so they are a choice rather than two checkboxes
    /// one of which has to hide.
    /// </summary>
    [Fact]
    public void TheCutIsOneControl_WithThreeStates_AndItNeverDisappears()
    {
        Assert.Equal(3, TraceRowViewModel.PatternCutModes.Count);

        var (plot, row) = PatternCard();
        Assert.True(row.ShowPatternCut);
        Assert.Equal(0, row.PatternCutIndex);

        row.PatternCutIndex = 1;
        Assert.True(row.MirrorPatternAngle);
        Assert.False(row.PatternWholePlane);
        Assert.True(row.ShowPatternCut);

        row.PatternCutIndex = 2;
        Assert.False(row.MirrorPatternAngle);          // the illegal pair is not expressible
        Assert.True(row.PatternWholePlane);
        Assert.True(row.ShowPatternCut);

        row.PatternCutIndex = 0;
        Assert.False(row.MirrorPatternAngle);
        Assert.False(row.PatternWholePlane);
        Assert.True(row.ShowPatternCut);

        // It is a VIEW of the two flags on the Trace, which are what the `.cdd` carries — so setting
        // either of them from anywhere else moves the control.
        plot.Traces[0].MirrorPatternAngle = true;
        row.MirrorPatternAngle = true;
        Assert.Equal(1, row.PatternCutIndex);

        output.WriteLine(string.Join(" / ", TraceRowViewModel.PatternCutModes));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // §6 — unchecking dB radial removed controls and shrank the Plot Inspector
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The pattern scale controls are DISABLED off a pattern plot, never hidden.</b> A control
    /// that disappears takes the panel's width with it and moves everything below it; greyed, the
    /// row keeps its size and the checkbox that turns it back on is right beside it.
    ///
    /// <para>Asserted on the XAML, because that is where the bug was: the view model already
    /// answered correctly and every one of these controls was <c>IsVisible</c>-bound to it.</para>
    /// </summary>
    [Fact]
    public void ThePatternScaleControls_AreDisabledRatherThanHidden()
    {
        string xaml = File.ReadAllText(
            Path.Combine(RepoRoot(), "src", "Ui", "Views", "DataDisplay", "PlotInspectorView.axaml"));

        Assert.DoesNotContain("IsVisible=\"{Binding HasPatternScale}\"", xaml);
        Assert.Contains("IsEnabled=\"{Binding HasPatternScale}\"", xaml);
        Assert.Contains("IsEnabled=\"{Binding CanEditPolarDbReference}\"", xaml);

        // The ROW itself is still gated on there being a pattern plot at all — an S-parameter plot
        // has no radial mode and offers none.
        Assert.Contains("IsVisible=\"{Binding HasPatternControls}\"", xaml);
        output.WriteLine("no IsVisible gate left on HasPatternScale");
    }

    /// <summary>The two gates on the outer-ring level, as one property, so the view makes up no
    /// conjunction of its own: there must BE a scale, and it must be an absolute one.</summary>
    [Fact]
    public void TheOuterRingLevel_IsEditableOnlyOnAnAbsolutePatternPlot()
    {
        var plot = new Plot(PlotType.Polar, FreqUnit.GHz);
        var vm   = new PlotInspectorViewModel(plot, () => { }, library: null);

        Assert.False(vm.HasPatternScale);              // a LINEAR polar plot is a locus
        Assert.False(vm.CanEditPolarDbReference);

        vm.PolarRadialIsDb = true;
        Assert.True(vm.HasPatternScale);
        Assert.False(vm.CanEditPolarDbReference);      // normalised: the ring IS the peak

        vm.PolarDbNormalised = false;
        Assert.True(vm.CanEditPolarDbReference);

        vm.PolarRadialIsDb = false;
        Assert.False(vm.CanEditPolarDbReference);      // still absolute, but there is no scale now
        output.WriteLine("the level box follows both gates");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // §7b — with Angles on, the θ (deg) label was drawn on top of the 180° bearing
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Two things are drawn under a pattern plot and they have to know about each other</b> — the
    /// bearing at 180°, which lives in the margin the VIEWPORT reserves for it, and the angle-axis
    /// line, which is placed from the viewport's own bottom edge. With "Angles" on they landed on
    /// each other (reported 2026-09-11), because the caption's placement knew only where the ring
    /// was.
    ///
    /// <para>Asserted on the real SVG the application's own writer produces, at the canvas height
    /// every real caller sizes a complex plot to — both halves of the fix are needed and only the
    /// rendered picture exercises both: the caption has to clear the bearing, AND the canvas has to
    /// be tall enough for where it now sits.</para>
    /// </summary>
    [Fact]
    public void TheAngleLabel_ClearsTheBearingRing_WhenAnglesAreOn()
    {
        var (svgOn,  hOn,  plotOn)  = PolarPatternSvg(angleLabels: true);
        var (svgOff, hOff, plotOff) = PolarPatternSvg(angleLabels: false);

        double capOn   = BaselineOf(svgOn,  "θ (deg)");
        double capOff  = BaselineOf(svgOff, "θ (deg)");
        double bearing = BaselineOf(svgOn,  "180");

        // It is BELOW the bearing, by more than a rounding — the whole number has to be clear of it,
        // not merely anchored one pixel further down.
        Assert.True(capOn > bearing + 10.0,
                    $"the angle label is at y={capOn:F1} and the 180° bearing at y={bearing:F1}");

        // And it still FITS: BottomLabelExtraLogical has to reserve the extra drop as well, or the
        // fix trades an overprint for a line off the bottom of the canvas. Asserted for BOTH, since
        // the two configurations size their canvas differently.
        Assert.True(capOn  + 4.0 <= hOn,  $"the angle label at y={capOn:F1} fell off a {hOn:F1} canvas");
        Assert.True(capOff + 4.0 <= hOff, $"the angle label at y={capOff:F1} fell off a {hOff:F1} canvas");

        // The drop is CONDITIONAL, and THIS is where that has to be asserted rather than by
        // comparing the two pictures' y values: with the bearings off the disc is BIGGER (it keeps
        // the margin they would have taken), so the label sits lower in absolute terms even though
        // nothing was added to it. Measured: 436.1 with angles off against 431.1 with them on.
        float lw = (float)(Math.Min(420.0, hOn) / 200.0);
        Assert.Equal(0f, AxesRenderer.PolarBearingDropPx(plotOff, lw));
        Assert.True(AxesRenderer.PolarBearingDropPx(plotOn, lw) > 0f);

        output.WriteLine($"angles on: 180° at y={bearing:F1}, θ (deg) at y={capOn:F1}, canvas {hOn:F1}");
        output.WriteLine($"angles off: θ (deg) at y={capOff:F1}, canvas {hOff:F1}");
    }

    /// <summary>The application's OWN SVG writer, at the canvas height
    /// <see cref="PlotCanvasGeometry.BottomLabelExtraLogical"/> asks for — which is how every real
    /// caller sizes a complex plot, and is half of what this test is about.</summary>
    private static (string Svg, float Height, Plot Plot) PolarPatternSvg(bool angleLabels)
    {
        var t = Resolve(PatternFixture.Data, "db10(farfield.U[0, :, 0, 1])", PlotType.Polar);

        var plot = new Plot(PlotType.Polar, FreqUnit.GHz)
        {
            PolarRadial          = PolarRadialMode.Db,
            PolarDbFloor         = -40,
            PolarDbRingStep      = 10,
            ShowPolarAngleLabels = angleLabels,
        };
        plot.Traces.Add(t);
        plot.Autoscale(force: true);
        t.BuildPath(plot.PlotType, plot.FreqUnits);

        const float w = 420f;
        float h = w + (float)PlotCanvasGeometry.BottomLabelExtraLogical(plot, w);
        string svg = PlotDocumentWriter.BuildSvgString(
            c => PlotRenderer.Draw(c, (w, h), plot, PlotDetail.Full, RenderTheme.Light),
            new PagePlacement(w, h, 0));
        return (svg, h, plot);
    }

    /// <summary>The y of one drawn string in a Skia SVG. Skia pads the element's content with
    /// whitespace, so the comparison is on the TRIMMED text.</summary>
    private static double BaselineOf(string svg, string text)
    {
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex
                     .Matches(svg, "<text[^>]*y=\"([0-9.]+)\"[^>]*>([^<]*)</text>"))
            if (m.Groups[2].Value.Trim() == text)
                return double.Parse(m.Groups[1].Value,
                                    System.Globalization.CultureInfo.InvariantCulture);

        Assert.Fail($"'{text}' was not drawn at all");
        return 0;
    }


    // ══════════════════════════════════════════════════════════════════════════════════════════
    // §8 — a farfield.U plot could not be built from the Plot Inspector
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>THE ROOT CAUSE: the spec box printed text the spec box itself refused.</b>
    ///
    /// <para>Reported 2026-09-11, as two 3D plots one of which read <c>&lt;invalid&gt;</c>: the
    /// working one was authored through the PICKER and carries a slice; the broken one carries only
    /// the expression, copied from the other's spec box — <c>dB10(farfield.U[42, :, ~, 0])</c>. That
    /// text does not parse. <c>SliceTokenParser</c> reads an integer on a <c>port</c> axis by
    /// matching the axis's own VALUES, so <c>0</c> is "Port 0 is not a port of axis 'port' (it has
    /// 1)" — and <see cref="Trace.BuildPickerExpression"/> was writing the INDEX.</para>
    ///
    /// <para>On a one-port antenna that is every trace, which is why it took a second plot to
    /// notice: the picker path never reads the text it displays.</para>
    /// </summary>
    [Fact]
    public void ThePickersOwnExpression_ParsesBackToTheSliceItCameFrom()
    {
        var ds = PatternFixture.Data;
        var slice = new[]
        {
            new AxisSlice("freq",  AxisRole.PinToIndex,   0),
            new AxisSlice("theta", AxisRole.KeepAsX,      0),
            new AxisSlice("phi",   AxisRole.FamilyIterate, 0),
            new AxisSlice("port",  AxisRole.PinToIndex,   1),   // the SECOND port, index 1
        };
        var t = new Trace(new SNP([5e9], 2), MatrixType.S, 0, 0, DependentVarFormat.Db)
        { CubeName = "farfield.U", Slice = slice, Transform = CubeTransform.dB10 };
        TraceResolve.SetCubeDataFrom(t, ds, PlotType.Surface3D, FreqUnit.GHz);
        TraceResolve.ApplyPinnedAxisDisplay(t, ds, FreqUnit.GHz);

        string spec = t.BuildPickerExpression();
        Assert.Contains(", 2]", spec);      // the port NUMBER, not the index

        Assert.True(CubeTraceSpecParser.TryParse(spec, ds, out string cube, out var back,
                                                 out var tr, out string err), $"{spec}: {err}");
        Assert.Equal("farfield.U", cube);
        Assert.Equal(CubeTransform.dB10, tr);

        // And it comes back as the SAME slice — the round trip is what makes the text usable, not
        // merely parseable.
        Assert.Equal(slice.Length, back!.Length);
        for (int i = 0; i < slice.Length; i++)
        {
            Assert.Equal(slice[i].AxisName, back[i].AxisName);
            Assert.Equal(slice[i].Role,     back[i].Role);
            if (slice[i].Role == AxisRole.PinToIndex) Assert.Equal(slice[i].Index, back[i].Index);
        }

        output.WriteLine("picker writes, and reads back: " + spec);
    }

    /// <summary>
    /// The same round trip on a ONE-port cube, which is the shape the report came from and the one
    /// where the old spelling was wrong on every trace rather than on one of them.
    /// </summary>
    [Fact]
    public void ThePickersOwnExpression_RoundTripsOnASinglePortCube()
    {
        var ds = Metric("GainDbi", "dBi", ports: 1, 6.2);
        var t  = new Trace(new SNP([1e9], 1), MatrixType.S, 0, 0, DependentVarFormat.Db)
        {
            CubeName  = "farfield.GainDbi",
            Slice     = [new AxisSlice("freq", AxisRole.KeepAsX, 0),
                         new AxisSlice("port", AxisRole.PinToIndex, 0)],
            Transform = CubeTransform.None,
        };
        TraceResolve.SetCubeDataFrom(t, ds, PlotType.Rect, FreqUnit.GHz);
        TraceResolve.ApplyPinnedAxisDisplay(t, ds, FreqUnit.GHz);

        string spec = t.BuildPickerExpression();
        Assert.Equal("farfield.GainDbi[:, 1]", spec);
        Assert.True(CubeTraceSpecParser.TryParse(spec, ds, out _, out var back, out _, out string err), err);
        Assert.Equal(0, back!.Single(x => x.AxisName == "port").Index);
        output.WriteLine(spec);
    }

    /// <summary>
    /// <b>A pattern trace is born with the right dB, and "always dB10" is not the answer.</b> That
    /// was the question asked on 2026-09-11, of <c>farfield.U</c>: U is an intensity and its dB is
    /// 10·log₁₀, a FIELD's is 20·log₁₀, and a cube already in dB takes none. One rule for the
    /// three would be wrong twice, by a factor of two in dB, silently.
    /// </summary>
    [Theory]
    [InlineData("farfield.U",            "W/sr", false, CubeTransform.dB10)]
    [InlineData("farfield.Etheta",       "V",    true,  CubeTransform.dB20)]
    [InlineData("farfield.CoPolLudwig3Db", "dB", false, CubeTransform.None)]
    // The unit is what ANT-7 §8 put on these cubes FOR — but every result file written before that
    // has none, so the DataKind has to carry it, and it is exact for the cubes that matter.
    [InlineData("farfield.U",      "", false, CubeTransform.dB10)]
    [InlineData("farfield.Ephi",   "", true,  CubeTransform.dB20)]
    [InlineData("farfield.AxialRatioDb", "", false, CubeTransform.None)]
    // Reported 2026-09-11: RadiationEfficiency was born dB10. The far-field GROUP is what routes a
    // cube here, and the group holds ANT-5's per-point metrics and ANT-6's polarization as well as
    // the pattern — so a stated unit that is neither a field nor a power is a quantity decibels do
    // not apply to, and the DataKind guess (which exists only for a unit-less legacy file) must not
    // reach it. A percentage is the one with a visible second symptom: the labeller appends "(%)"
    // only to an untransformed percentage, so the wrong default suppressed the unit too.
    [InlineData("farfield.RadiationEfficiency",    "%",   false, CubeTransform.None)]
    [InlineData("farfield.BeamwidthDeg",           "deg", false, CubeTransform.None)]
    [InlineData("farfield.DirectivityPeakThetaDeg", "deg", false, CubeTransform.None)]
    // Signed, and in −1…+1: its dB would be of the MAGNITUDE, which discards the half of the cube
    // that carries the answer (the sign IS the handedness).
    [InlineData("farfield.PolarizationSense",      "1",   false, CubeTransform.None)]
    public void APatternTrace_IsBornWithTheRightDecibel(string name, string unit, bool complex,
                                                        CubeTransform want)
    {
        var axes = new[] { new Axis("freq", [1e9, 2e9], "Hz") };
        var cube = complex
            ? new DataCube(axes, new[] { new Complex(1, 0), new Complex(2, 0) }) { Unit = unit }
            : new DataCube(axes, new[] { 1.0, 2.0 }) { Unit = unit };

        Assert.Equal(want, TraceRowViewModel.DefaultPatternTransform(cube, name));
        output.WriteLine($"{name} [{unit}] {(complex ? "complex" : "real")} -> {want}");
    }

    /// <summary>
    /// <b>A metric's default is None, and None is only the DEFAULT.</b> Said by the owner on
    /// 2026-09-11, alongside the RadiationEfficiency report: dB10 or dB20 IS wanted on some of
    /// these metrics, just not as what the trace is born with. The seed and the picker are separate
    /// decisions and this holds them apart — the one thing a default must never do is take a
    /// transform away, and the transform list is keyed on the plot type and the DataKind, never on
    /// the unit the seed reads.
    /// </summary>
    [Theory]
    [InlineData("%")]
    [InlineData("deg")]
    [InlineData("1")]
    public void AMetricsDefaultIsNone_ButEveryDecibelIsStillOffered(string unit)
    {
        var axes = new[] { new Axis("freq", [1e9, 2e9], "Hz") };
        var cube = new DataCube(axes, new[] { 92.0, 93.0 }) { Unit = unit };

        Assert.Equal(CubeTransform.None,
                     TraceRowViewModel.DefaultTransformFor(cube, PlotType.Rect,
                                                           "farfield.RadiationEfficiency"));

        var items = TraceRowViewModel.BuildTransformItems(
            isCubeBound: true, PlotType.Rect, isComplexData: false);

        foreach (var want in new[] { CubeTransform.dB10, CubeTransform.dB20, CubeTransform.dB,
                                     CubeTransform.Mag,  CubeTransform.None })
            Assert.True(items.Single(i => i.Transform == want).Enabled, $"{want} was not offered");

        output.WriteLine($"[{unit}] seeds None and still offers dB10 / dB20 / dB");
    }

    /// <summary>
    /// <b>And the picker is SHOWN on a pattern plot, offering exactly the transforms a radius can
    /// be.</b> It was <c>IsRectOrTablePlot</c>-gated, so on the one plot type that REQUIRES a
    /// transform the only way to set one was to type it into the spec box — which is the box that
    /// was printing an unparseable spec.
    /// </summary>
    [Fact]
    public void ThePatternPlot_OffersTheTransformPicker_AndOnlyTheDecibelEntries()
    {
        var (_, row) = PatternCard();
        Assert.True(row.IsPatternPlot);
        Assert.True(row.ShowTransformCombo);

        foreach (bool complex in new[] { false, true })
        {
            var items = TraceRowViewModel.BuildTransformItems(
                isCubeBound: true, PlotType.Surface3D, isComplexData: complex,
                isPatternPlot: true);

            foreach (var want in new[] { CubeTransform.dB10, CubeTransform.dB20, CubeTransform.dB })
                Assert.True(items.Single(i => i.Transform == want).Enabled, $"{want} was not offered");

            // A magnitude or a phase cannot be a radius on a dB scale.
            foreach (var no in new[] { CubeTransform.Mag, CubeTransform.Phase,
                                       CubeTransform.Real, CubeTransform.Imag, CubeTransform.Conj })
                Assert.False(items.Single(i => i.Transform == no).Enabled, $"{no} was offered");

            // None joins them only for REAL data — how an already-dB cube is drawn.
            Assert.Equal(!complex, items.Single(i => i.Transform == CubeTransform.None).Enabled);
        }

        output.WriteLine("the pattern picker offers dB10 / dB20 / dB, and None on real data");
    }

    /// <summary>
    /// <b>A 3D pattern's title is the trace's own identity, and the user's title replaces it.</b>
    /// Asked for on 2026-09-11: remove the bottom caption, make its text the plot's DEFAULT title,
    /// and let the user's own axes title override it.
    /// </summary>
    [Fact]
    public void TheSurfacesIdentity_IsItsDefaultTitle_AndACustomTitleReplacesIt()
    {
        var plot = Surface3D();

        string auto = SurfaceSvg(plot);
        Assert.Contains("farfield.U", auto);

        plot.CustomTitle   = "Radiation pattern, 5 GHz";
        plot.CustomTitleOn = true;
        string custom = SurfaceSvg(plot);
        Assert.Contains("Radiation pattern, 5 GHz", custom);
        Assert.DoesNotContain("farfield.U", custom);

        // An EMPTY custom title is how the line is turned off — which is only reachable because the
        // fallback is keyed on CustomTitleOn rather than on the text being blank.
        plot.CustomTitle = "";
        Assert.DoesNotContain("farfield.U", SurfaceSvg(plot));

        output.WriteLine("identity by default; the custom title replaces it; blank removes it");
    }

    private static string SurfaceSvg(Plot plot) =>
        PlotDocumentWriter.BuildSvgString(
            c => SurfaceRenderer.Draw(c, (480, 420), plot, PlotDetail.Full, RenderTheme.Light),
            new PagePlacement(480, 420, 0));


    // ══════════════════════════════════════════════════════════════════════════════════════════
    // §9 — the legend, and the dB default for every far-field cube
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The legend is drawn at every canvas size.</b> Reported 2026-09-11: it stopped rendering
    /// once the Data Display was zoomed out past a threshold. It was gated on the label font coming
    /// out at least 4 px — a rule about when TEXT is worth the ink, applied to the whole colour bar
    /// — so a zoomed-out plot silently lost the one thing that says whether a colour is the peak or
    /// the floor.
    ///
    /// <para>Asserted by COUNTING the bar's own bands in the SVG, not by looking for text: the
    /// numbers beside it legitimately shrink, and it is the bar that must not vanish.</para>
    /// </summary>
    [Fact]
    public void TheLegendIsDrawnAtEveryCanvasSize_AndCanBeTurnedOff()
    {
        var plot = Surface3D();

        // 760 px is a comfortable plot; 90 px is well below the old 4 px font threshold
        // (baseSize = FontSizeLabel * min(W,H)/200).
        foreach (double side in new[] { 760.0, 320.0, 90.0 })
        {
            int bands = ColourBarBands(plot, side);
            Assert.True(bands > 4, $"the legend vanished at {side} px (drew {bands} bands)");
            output.WriteLine($"{side,6:F0} px canvas: {bands} legend bands");
        }

        // And it is a SETTING — the one piece of chrome that costs the scene its width.
        plot.SurfaceShowLegend = false;
        Assert.Equal(0, ColourBarBands(plot, 760.0));
    }

    /// <summary>The setting round-trips through the <c>.cdd</c>, and a document written before it
    /// existed loads with the legend ON — which is what it had.</summary>
    [Fact]
    public void TheLegendSetting_RoundTripsAndDefaultsOn()
    {
        Assert.True(new Plot(PlotType.Surface3D, FreqUnit.GHz).SurfaceShowLegend);
        Assert.True(new PlotContainerConfig().SurfaceShowLegend);

        var pc = new PlotContainerConfig { PlotType = PlotType.Surface3D, SurfaceShowLegend = false };
        var round = System.Text.Json.JsonSerializer.Deserialize<PlotContainerConfig>(
            System.Text.Json.JsonSerializer.Serialize(pc, DataDisplayJson.Options),
            DataDisplayJson.Options)!;
        Assert.False(round.SurfaceShowLegend);

        // And the LOAD direction, which is the half that can silently lose a setting.
        Assert.False(PlotConfigLoader.LoadPlot(round, new EmptySources()).SurfaceShowLegend);

        // A .cdd written before the setting existed has no such key at all — it must load ON.
        var older = System.Text.Json.JsonSerializer.Deserialize<PlotContainerConfig>(
            "{\"PlotType\":\"Surface3D\"}", DataDisplayJson.Options)!;
        Assert.True(older.SurfaceShowLegend);
        Assert.True(PlotConfigLoader.LoadPlot(older, new EmptySources()).SurfaceShowLegend);
    }

    /// <summary>A plot config carries no traces here, so the loader needs no data at all.</summary>
    private sealed class EmptySources : IPlotDataSources
    {
        public string?  ResolveAbs(string? sourceRef)  => sourceRef;
        public bool     Contains(string absPath)       => false;
        public SNP?     NetworkFor(string absPath)     => null;
        public DataSet? DataFor(string absPath)        => null;
        public string?  AliasFor(string absPath)       => null;
        public string?  DisplayNameFor(string absPath) => absPath;
        public bool     HasMultipleSources             => false;
        public DataSet? SelectedData                   => null;
    }

    /// <summary>
    /// <b>Every far-field cube is born in decibels, on any plot type</b> — asked for on 2026-09-11,
    /// of picking <c>farfield.U</c> on a trace card for the first time. The GROUP is what decides
    /// rather than the bare name: a cube called "U" outside that group is not necessarily a
    /// radiation intensity.
    /// </summary>
    [Fact]
    public void AFarFieldCube_IsBornInDecibels_OnARectangularPlotToo()
    {
        var ds   = Metric("U", "W/sr", ports: 1, 1e-4, 2e-4);
        var cube = ds["farfield.U"];

        Assert.Equal(CubeTransform.dB10,
                     TraceRowViewModel.DefaultTransformFor(cube, PlotType.Rect, "farfield.U"));
        Assert.Equal(CubeTransform.dB10,
                     TraceRowViewModel.DefaultTransformFor(cube, PlotType.Table, "farfield.U"));

        // The group is the test. The same REAL cube under any other name keeps the old rule, which
        // for real data on Rect is no transform at all.
        Assert.True(TraceRowViewModel.IsFarFieldCube("farfield.U"));
        Assert.False(TraceRowViewModel.IsFarFieldCube("HB1.U"));
        Assert.Equal(CubeTransform.None,
                     TraceRowViewModel.DefaultTransformFor(cube, PlotType.Rect, "HB1.U"));

        output.WriteLine("farfield.* opens in dB on every plot type");
    }

    /// <summary>How many colour bands the legend drew — the bar is painted as horizontal bands, and
    /// nothing else on a surface emits a filled rect.</summary>
    private static int ColourBarBands(Plot plot, double side)
    {
        string svg = PlotDocumentWriter.BuildSvgString(
            c => SurfaceRenderer.Draw(c, (side, side), plot, PlotDetail.Full, RenderTheme.Light),
            new PagePlacement((float)side, (float)side, 0));
        return System.Text.RegularExpressions.Regex.Matches(svg, "<rect").Count;
    }


    // ══ helpers ══════════════════════════════════════════════════════════════

    private static AxisRoleRowViewModel AxisRow(string name, string[] options,
                                                string? unit = null, bool isX = false)
    {
        var (_, row) = PatternCard();
        return new AxisRoleRowViewModel(row, name, unit, options, isX, pinIndex: 0);
    }

    /// <summary>A live trace card on a dB-radial polar plot — the state every pattern control is
    /// gated on.</summary>
    private static (Plot Plot, TraceRowViewModel Row) PatternCard()
    {
        var ds = PatternFixture.Data;
        var t  = Resolve(ds, "db10(farfield.U[0, :, 0, 1])", PlotType.Polar);

        var plot = new Plot(PlotType.Polar, FreqUnit.GHz) { PolarRadial = PolarRadialMode.Db };
        plot.Traces.Add(t);
        var inspector = new PlotInspectorViewModel(plot, () => { }, library: null);
        return (plot, new TraceRowViewModel(t, inspector));
    }

    private static Plot Surface3D()
    {
        var ds = PatternFixture.Data;
        Assert.True(CubeTraceSpecParser.TryParse("db10(farfield.U[0, :, :, 1])", ds,
                                                 out string cube, out var slice,
                                                 out var transform, out string err), err);
        var t = new Trace(new SNP([5e9], 2), MatrixType.S, 0, 0, DependentVarFormat.Db)
        {
            CubeName = cube, Slice = slice, Transform = transform,
        };
        var plot = new Plot(PlotType.Surface3D, FreqUnit.GHz) { PolarDbFloor = -40 };
        plot.Traces.Add(t);
        TraceResolve.SetCubeDataFrom(t, ds, PlotType.Surface3D, FreqUnit.GHz);
        plot.Autoscale(force: true);
        return plot;
    }
}

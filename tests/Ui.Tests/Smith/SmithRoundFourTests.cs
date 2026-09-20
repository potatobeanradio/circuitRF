// ════════════════════════════════════════════════════════════════════════════
//  SmithRoundFourTests.cs — the fourth round of owner items over the finished
//  tool (2026-09-19).
//
//  ONE TEST PER CLAIM, and only the claims whose failure would be SILENT. The
//  items with no test here are the ones whose evidence is a pixel or a button:
//  Fit and the zoom box at the head of the network toolbar, Save / Save As and
//  the Q toggle on the chart strip, the narrower generator column, the two
//  square generator verbs, the narrower source combo, and the withdrawal of the
//  constant-Q grab ring. Each is one expression in the AXAML or one deleted draw
//  call, and each is visible the moment the window is opened.
//
//  Ui.Tests may call no Avalonia runtime API, so the zoom box's own gesture is
//  not driven here either — what it needs is a real pointer press.
// ════════════════════════════════════════════════════════════════════════════

using System;
using System.Linq;
using System.Numerics;
using CircuitRF.Design.Smith;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Render.Smith;
using CircuitRF.Ui.DataDisplay.Controls;
using CircuitRF.Ui.Smith;
using CircuitRF.Ui.Views.DataDisplay;
using RfCore;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests.Smith;

public sealed class SmithRoundFourTests
{
    private const double ChartZ0 = 50.0;
    private static readonly (double W, double H) Canvas = (600.0, 600.0);

    /// <summary>Three rows around 2 GHz, so the median IS a row and there is a band.</summary>
    private static SmithDesign Design()
    {
        var d = new SmithDesign();
        d.Chart.Z0Ohm = ChartZ0;
        d.Generator.Rows.Add(new SmithGeneratorRow(1.8e9, 12.0, -8.5));
        d.Generator.Rows.Add(new SmithGeneratorRow(2.0e9, 11.4, -9.1));
        d.Generator.Rows.Add(new SmithGeneratorRow(2.2e9, 10.9, -9.8));
        d.Elements.Add(new SmithElement
        {
            Kind = SmithElementKind.L, Placement = SmithPlacement.Series, Name = "L1",
            ActiveParameter = SmithParameter.L,
            Values = { LHenry = 2.2e-9 },
        });
        return d;
    }

    private static TransformSet Tf(SmithChartViewModel vm)
        => PlotRenderer.BuildTransforms(vm.ChartPlot, Canvas);

    // ═════════════════════════════════════════════════════════════════════════
    //  1. The design frequency is a generator ROW — the one nearest the table's median
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>There is no design-frequency field: it is the generator row nearest the table's median,
    /// and it moves when the table does.</b> An odd table hands back its middle row; an even one
    /// ties between the two middle rows and the upper of them wins (owner instruction, 2026-09-19),
    /// so the design frequency is always a frequency the table actually states.
    /// </summary>
    /// <remarks>
    /// <b>The silent failure this catches is a stale one.</b> It used to be a stored number typed
    /// into a panel, and the whole risk of deriving it instead is that some path keeps a cached copy
    /// and answers with yesterday's value — so the claim is asserted ACROSS an edit rather than on a
    /// freshly-built document, and through the view model's own committed-edit seam rather than by
    /// mutating the design directly.
    ///
    /// <para>It is also no longer written to the file, which is the other half: a `.csmith` that
    /// still carried one would be a second answer, and the one that is wrong is the one nobody is
    /// looking at.</para>
    /// </remarks>
    [Fact]
    public void TheDesignFrequencyIsTheGeneratorTablesMedian()
    {
        // Odd: the middle ROW, so the design frequency's load point is one of the drawn ones.
        var vm = new SmithChartViewModel(Design());
        Assert.Equal(2.0e9, vm.Design.DesignFrequencyHz);

        // Even: a ROW, not the mean of two. The midpoint of 2 and 3 GHz is equidistant from both, so
        // the tie rule decides and it is the upper — 3 GHz, which the table states, rather than
        // 2.5 GHz, which it does not and whose Z_gen would be interpolated.
        var two = new SmithDesign();
        two.Generator.Rows.Add(new SmithGeneratorRow(2.0e9, 50, 0));
        two.Generator.Rows.Add(new SmithGeneratorRow(3.0e9, 50, 0));
        Assert.Equal(3.0e9, two.DesignFrequencyHz);

        // THE POINT OF THE RULE: whatever the table is, the design frequency is one of its own rows,
        // so the emphasised load point is one of the drawn ones. Asserted over an even table whose
        // middle pair is not its outer pair, which is where an "average" would leave the span.
        var four = new SmithDesign();
        foreach (double f in new[] { 1.8e9, 2.0e9, 2.2e9, 3.0e9 })
            four.Generator.Rows.Add(new SmithGeneratorRow(f, 50, 0));
        Assert.Contains(four.Generator.Rows, r => r.FrequencyHz == four.DesignFrequencyHz);
        Assert.Equal(2.2e9, four.DesignFrequencyHz);

        // …and it FOLLOWS the table. Retuning the middle row is what "retuning the chart" means now.
        vm.GeneratorRows[1].FrequencyEntry = "2.1 GHz";
        Assert.Equal(2.1e9, vm.Design.DesignFrequencyHz, 3);

        // One undo takes the whole edit, and the design frequency goes back with it.
        vm.UndoRedo.Undo();
        Assert.Equal(2.0e9, vm.Design.DesignFrequencyHz, 3);

        // It is not in the file at all — there is nothing to drift from.
        Assert.DoesNotContain("DesignFrequency", SmithDesignIo.Serialize(vm.Design),
                              StringComparison.OrdinalIgnoreCase);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  2. Shift-dragging a generator glyph edits that row
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>A generator glyph is grabbable only with shift, and dragging it writes the row it grabbed
    /// — live, and as one undo entry.</b>
    /// </summary>
    /// <remarks>
    /// <b>The modifier is the safety and is therefore part of the claim.</b> This handle edits node
    /// 0 of the walk: every trajectory, every load point and the band move with it. An unmodified
    /// press on a glyph has to keep falling through to the control, which is what leaves it panning
    /// the chart exactly as a press on empty space does — so the null answer is asserted as well as
    /// the handle.
    ///
    /// <para>The impedance is checked through Γ's own inverse rather than against a literal: the
    /// drag is handed a Γ and the row has to be the impedance that Γ means, which is the one thing a
    /// wrong sign or a wrong Z₀ would break while the picture still looked ordinary.</para>
    /// </remarks>
    [Fact]
    public void AGeneratorGlyphIsGrabbedOnlyWithShift_AndTheDragWritesItsRow()
    {
        var vm = new SmithChartViewModel(Design()) { ChartCanvasSize = Canvas };

        var point = vm.Scene.GeneratorPoints.Single(g => g.RowIndex == 1);
        var at    = Tf(vm).PrimaryToCanvas(point.Gamma.Real, point.Gamma.Imaginary);

        // UNMODIFIED: nothing of the overlay's is there, so the press pans.
        Assert.Null(vm.ChartOverlay.HitTest(at.X, at.Y, Tf(vm)));

        // SHIFTED: the glyph, naming its own row.
        var handle = Assert.IsType<SmithGeneratorHandle>(
            vm.ChartOverlay.HitTest(at.X, at.Y, Tf(vm), shift: true));
        Assert.Equal(1, handle.RowIndex);

        // The drag, through the seam PlotControl drives.
        var target = new Complex(-0.25, 0.4);
        vm.ChartOverlay.DragBegin(handle);
        vm.ChartOverlay.DragTo(target, shift: true);

        var expected = ChartZ0 * (Complex.One + target) / (Complex.One - target);
        Assert.Equal(expected.Real,      vm.Design.Generator.Rows[1].ResistanceOhm, 9);
        Assert.Equal(expected.Imaginary, vm.Design.Generator.Rows[1].ReactanceOhm,  9);

        // The frequency is untouched — the table is kept sorted by it, and a drag that moved one
        // sideways would re-sort the table under the hand holding it.
        Assert.Equal(2.0e9, vm.Design.Generator.Rows[1].FrequencyHz);

        // …and nothing is pushed until the release, which then pushes exactly once.
        Assert.False(vm.UndoRedo.CanUndo);
        vm.ChartOverlay.DragEnd(cancelled: false);
        Assert.True(vm.UndoRedo.CanUndo);

        vm.UndoRedo.Undo();
        Assert.Equal(11.4, vm.Design.Generator.Rows[1].ResistanceOhm, 9);
        Assert.False(vm.UndoRedo.CanUndo);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  3. A floating marker is a ring; a snapped one is the triangle
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The glyph says whether a free marker is sitting on anything.</b> Floating draws the
    /// loadpull contour marker's ring; a shift-drag that landed it on a curve draws the ordinary
    /// triangle; anything that takes it off again goes back to the ring.
    /// </summary>
    /// <remarks>
    /// <b>Asserted against the RENDERER rather than against the flag</b>, because the flag is not
    /// the claim — what the user sees is. Two renders of the same marker, one with the flag and one
    /// without, differing in pixels is the whole of it; a test on <c>SnappedToCurve</c> alone would
    /// pass on a renderer that ignored it.
    /// </remarks>
    [Fact]
    public void AFloatingMarkerDrawsARing_AndASnappedOneDrawsTheTriangle()
    {
        Assert.NotEqual(RenderMarker(snapped: false), RenderMarker(snapped: true));

        // The snapped one is the glyph every ordinary marker has, which is what makes the OTHER one
        // the exception rather than both of them being new.
        Assert.Equal(RenderMarker(snapped: true), RenderMarker(snapped: true, free: false));
    }

    /// <summary>One marker, dead centre, rendered on its own — the glyph and nothing else.</summary>
    private static byte[] RenderMarker(bool snapped, bool free = true)
    {
        var trace = new Trace(new SNP([1e9], 1), MatrixType.S, 0, 0, DependentVarFormat.Complex);
        trace.Points.Add(new System.Numerics.Vector2(0f, 0f));

        var marker = new Marker(trace, 0.0, false, false, 1)
        {
            FreePosition   = free,
            SnappedToCurve = snapped,
            PositionStatic = new System.Numerics.Vector2(0f, 0f),
        };
        trace.Markers.Add(marker);

        const int W = 120, H = 120;
        var map = (XScale: 1.0, YScale: 1.0, XOffset: W / 2.0, YOffset: H / 2.0);
        var tf  = new TransformSet { Primary = map, Secondary = map, CanvasSize = (W, H) };

        var bmp = new SKBitmap(W, H);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.White);
        MarkerRenderer.DrawSymbol(canvas, (W, H), marker, trace, tf, RenderTheme.Light);
        return bmp.Bytes;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  4. Change to Trace… offers only the traces the user added
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>On this document's chart, the menu lists the reference data and none of the tool's own
    /// curves.</b>
    /// </summary>
    /// <remarks>
    /// <b>An ordinary Data Display is the control</b>, and it is not decoration: the filter runs on
    /// every plot in the application, and one that quietly emptied a Data Display's own menu would
    /// be a far worse defect than the one it fixes.
    /// </remarks>
    [Fact]
    public void ChangeToTraceOffersOnlyTheTracesTheUserAdded()
    {
        var vm = new SmithChartViewModel(Design()) { ChartCanvasSize = Canvas };

        // Everything on this chart is derived, so a marker on any of them can be re-pointed at
        // nothing: there is no reference data on it yet.
        var derived = vm.ChartPlot.Traces[0];
        Assert.Empty(MarkerInfoBoxView.ChangeToTraceCandidates(
                         vm.ChartPlot.Traces, derived, vm.ChartPlot));

        // One trace the user added — which is exactly "it did not come through the tool's own
        // factory", and is the flag the inspector already tells the two apart by.
        var added = new Trace(new SNP([1e9], 1), MatrixType.S, 0, 0, DependentVarFormat.Complex);
        vm.ChartPlot.Traces.Add(added);

        Assert.Equal([added],
                     MarkerInfoBoxView.ChangeToTraceCandidates(
                         vm.ChartPlot.Traces, derived, vm.ChartPlot));

        // THE CONTROL: on a plot that is not fixed-readout the filter passes everything, so a Data
        // Display's own menu is unchanged.
        var ordinary = new Plot(PlotType.Smith, FreqUnit.GHz);
        var a = new Trace(new SNP([1e9], 1), MatrixType.S, 0, 0, DependentVarFormat.Complex)
                    { ExcludeFromAxisLabels = true };
        var b = new Trace(new SNP([1e9], 1), MatrixType.S, 0, 0, DependentVarFormat.Complex);
        ordinary.Traces.Add(a);
        ordinary.Traces.Add(b);

        Assert.Equal([b], MarkerInfoBoxView.ChangeToTraceCandidates(ordinary.Traces, a, ordinary));
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  5. A load label's clearance is CONTINUOUS in the geometry
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Moving an obstacle by a pixel moves a label by about a pixel, never by a whole row.</b>
    /// </summary>
    /// <remarks>
    /// <b>This is the owner's reported glitch, stated as a property</b> (2026-09-19 — the labels
    /// flicked back and forth during a drag). The old rule tried the box one whole box-height
    /// further out at a time and stopped at the first row that intersected nothing, which is a
    /// DISCRETE decision recomputed from scratch on every frame: a load point moving half a pixel
    /// could flip the answer between two rows, and a drag that wandered across that threshold made
    /// the label jump about fourteen pixels each way, twenty times a second.
    ///
    /// <para>So the assertion is Lipschitz rather than positional: sweeping an obstacle through the
    /// label's whole approach, the largest single step in the answer is small. A jump of a row would
    /// be tens of pixels and nothing else about the placement would look wrong.</para>
    /// </remarks>
    [Fact]
    public void ALoadLabelsClearanceMovesContinuouslyWithWhatIsInItsWay()
    {
        const float halfW = 22f, halfH = 7f, first = 12f, limit = 100f, scale = 1f;
        var glyph = new SKPoint(100f, 100f);

        float previous = float.NaN;
        float worst    = 0f;

        // The obstacle walks from well clear of the label, horizontally, to directly over it — the
        // approach that used to switch a whole row on or off in one step.
        for (float dx = 80f; dx >= 0f; dx -= 0.25f)
        {
            SKRect[] taken = [new(glyph.X + dx - 10f, glyph.Y - 30f, glyph.X + dx + 10f, glyph.Y - 16f)];

            float offset = SmithChartChrome.LabelOffset(
                glyph, halfW, halfH, dir: -1f, taken, first, limit, scale);

            if (!float.IsNaN(previous)) worst = Math.Max(worst, Math.Abs(offset - previous));
            previous = offset;
        }

        Assert.True(worst < 2.0f,
            $"a 0.25 px move of an obstacle moved the label by {worst:0.##} px — the placement is "
          + "stepping rather than sliding, which is what made it flicker during a drag.");

        // …and it does move: a test that passed because the answer never changed would prove
        // nothing at all.
        float clear   = SmithChartChrome.LabelOffset(glyph, halfW, halfH, -1f, [], first, limit, scale);
        float blocked = SmithChartChrome.LabelOffset(
            glyph, halfW, halfH, -1f,
            [new(glyph.X - 10f, glyph.Y - 30f, glyph.X + 10f, glyph.Y - 16f)], first, limit, scale);

        Assert.Equal(first, clear);
        Assert.True(blocked > clear);
    }
}

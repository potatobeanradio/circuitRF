// The Smith Chart's transient chrome — the arrowheads, the load-point labels, the grippers and the
// constant-Q grab ring (docs/design/smith-chart.md §5.4; brief-smith-5-chart.md R-smith5-2/3/6/7,
// brief-smith-9-q-and-sweep.md R-smith9-3, brief-smith-10-cli-verb.md R-smith10-3).
//
// IT DRAWS; IT DOES NOT EDIT. The gesture half — the hit test, the press, the drag and the undo
// entry — is SmithGripperOverlay's and stayed in src/Ui with the view model it calls back into.
// What is here is the four draw calls that produce PIXELS, and they are here because
// `circuitrf smith` draws the same chart with no window: PlacedPlot.Overlay's own remark says what
// happens otherwise — an overlay a PlotControl draws on every frame is silently absent from every
// export, the picture is still produced, it still looks correct, and the arrowheads and the
// frequency labels the user exported it for are gone.
//
// EVERY ARGUMENT OF A FRAME IS AN ARGUMENT. Nothing here caches a canvas, a transform or a theme
// between calls — ContourRenderer once drew every contour on every Smith plot to the first target
// it had been handed, because the target was remembered rather than passed.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CircuitRF.Design.Matching;
using CircuitRF.Design.Smith;
using CircuitRF.Render.DataDisplay;
using SkiaSharp;

namespace CircuitRF.Render.Smith;

/// <summary>
/// The constant-Q pair's handle — <b>a handle kind on brief 5's seam, not a second overlay</b>
/// (<c>brief-smith-9-q-and-sweep.md</c> <c>R-smith9-3</c>).
/// </summary>
/// <param name="Inductive">Which branch was grabbed. <b>It changes nothing about the answer</b> —
/// dragging either branch moves both, because they are one setting — and it is carried so the ring
/// under the cursor is drawn on the branch the user is actually holding.</param>
/// <param name="At">The point ON the arc nearest the cursor, in Γ. The ring is drawn here rather
/// than at the pointer, so the handle stays on the curve it belongs to.</param>
public sealed record SmithQHandle(bool Inductive, Complex At);

/// <summary>
/// Which handles the frame being drawn is holding — <b>the whole of what a live drag adds to the
/// picture</b>.
/// </summary>
/// <remarks>
/// <b>Passed rather than read</b>, which is what lets a headless render draw the same chrome: the
/// default is "nothing under the cursor and nothing being dragged", which is exactly the state an
/// export is in. The window fills it from the overlay's own fields on every frame.
/// </remarks>
/// <param name="HoverNode">The node the cursor is over, or −1.</param>
/// <param name="DragNode">The node being dragged, or −1.</param>
/// <param name="HoverQ">The constant-Q handle under the cursor, or null.</param>
/// <param name="DragQ">The constant-Q handle being dragged, or null.</param>
public readonly record struct SmithChromeState(
    int HoverNode = -1, int DragNode = -1, SmithQHandle? HoverQ = null, SmithQHandle? DragQ = null)
{
    /// <summary>No cursor and no drag — an export, and the first frame of a window alike.</summary>
    public static SmithChromeState None => new();
}

/// <summary>
/// The four things drawn over a Smith chart that a <c>Trace</c> cannot express.
/// </summary>
/// <remarks>
/// <b>What it draws, and nothing else:</b> an arrowhead at each trajectory's own reported midpoint,
/// the generator's anchor, one hollow ring per draggable node, the load points' label boxes, and the
/// constant-Q pair's grab ring. The curves and the points themselves are TRACES —
/// <see cref="SmithPlotBuilder"/> puts them on the plot and the Data Display draws those.
///
/// <para><b>The constant-Q ARCS are traces too, and deliberately</b> (<c>R-smith9-3</c>): they are
/// drawn BENEATH the trajectories, and this chrome goes over the top with the rest of the handles.</para>
/// </remarks>
public static class SmithChartChrome
{
    /// <summary>Canvas-pixel radius of a gripper ring, and of its hit target.</summary>
    internal const float RingRadius = 4.5f;

    /// <summary>The hit radius the window's own <c>HitTest</c> measures in. Here because the ring
    /// it has to agree with is here.</summary>
    internal const double HitRadius = 8.0;

    /// <summary>
    /// The clearance, in canvas pixels at the nominal line width, between a load point's glyph and
    /// the nearest edge of the label box naming it.
    /// </summary>
    private const float LabelClearance = 7f;

    /// <summary>The gap left between two label boxes, and between a box and a glyph it is not
    /// naming, when a box has to be pushed further out to clear one.</summary>
    private const float LabelBoxGap = 2f;

    /// <summary>How far, in box heights, a label may be pushed out from its glyph before it is
    /// drawn where it is. A bound rather than an open search: a chart zoomed until every point is
    /// one pixel apart has no placement that clears, and a label marching off the canvas is worse
    /// than a slight overlap.</summary>
    private const int LabelPushLimit = 6;

    /// <summary>
    /// The band, in canvas pixels at the nominal line width, over which an obstacle's claim on a
    /// label FADES IN as the two come into horizontal range.
    /// </summary>
    /// <remarks>
    /// <b>It is what keeps the placement continuous</b> (owner report, 2026-09-19 — labels flicking
    /// between two positions during a drag). Without it the obstacle either counts or does not, so
    /// a sub-pixel move of a load point can add or remove a whole box height of offset, and a drag
    /// that wanders across that threshold makes the label jump back and forth. See
    /// <see cref="DrawLoadLabels"/>.
    /// </remarks>
    private const float LabelApproachBand = 6f;

    /// <summary>
    /// Handed to <c>ContourRenderer.ComputeLabelAnchors</c> as the world-unit spacing. The stub
    /// passed with it is TWO PIXELS long in canvas coordinates, so any spacing above that answers
    /// with exactly ONE anchor — see that method's own remarks. One box per load point, placed by
    /// the shared placer.
    /// </summary>
    private const double LabelSpacing = 1e6;

    private const float LabelFontSize = 9f;
    private const float LabelBaseLw   = 2.0f;

    /// <summary>The arcs' and the readings' own colour — the grey the load points and node 0 are
    /// drawn in, so a handle belongs to the arc it sits on rather than to a trajectory.</summary>
    private static SKColor ReadingColorOpaque => SmithPlotBuilder.ReadingColor;

    /// <summary>
    /// Draws the whole of one frame's chrome, in the order it stacks.
    /// </summary>
    /// <param name="canvas">This frame's target. Never kept.</param>
    /// <param name="tf">The transform the traces beneath were drawn with, handed down rather than
    /// rebuilt, so the chrome cannot land a pixel away from the curve it belongs to.</param>
    /// <param name="theme">The same theme object the plot underneath was just drawn with.</param>
    /// <param name="scene">The evaluation the traces were filled from. <b>One evaluation feeds both
    /// halves</b> — evaluating twice is how a handle ends up off the curve it belongs to.</param>
    /// <param name="design">The document, for its three show/hide settings and for which nodes
    /// carry a parameter to drag.</param>
    /// <param name="state">What is hovered and what is being dragged. <see cref="SmithChromeState.None"/>
    /// for an export.</param>
    public static void Draw(
        SKCanvas canvas, TransformSet tf, RenderTheme theme,
        SmithChartScene scene, SmithDesign design, SmithChromeState state)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(design);

        if (!scene.HasContent) return;

        DrawArrowheads(canvas, tf, scene);
        DrawQValue(canvas, tf, theme, design);
        if (design.Chart.ShowLabels)   DrawLoadLabels(canvas, tf, theme, scene);
        if (design.Chart.ShowGrippers) DrawGrippers(canvas, tf, theme, scene, design, state);

        // THERE IS NO CONSTANT-Q GRAB RING (owner report, 2026-09-19 — "a circle rendered around
        // the cursor during a shift-drag of a constant-Q line"). The ring was drawn at the point of
        // the arc NEAREST THE POINTER, which during a drag is within a pixel or two of the pointer
        // itself, so what it actually looked like was a circle stuck to the mouse cursor. Its
        // earlier hover form had already been withdrawn for the same complaint one revision before,
        // which is the tell: the handle has nowhere to sit that is not under the cursor. The arcs
        // are still grabbed and dragged exactly as before — SmithGripperOverlay's hit test is
        // untouched — and what says the drag is working is that both arcs and the Q= readout move.
    }

    /// <summary>
    /// The constant-Q value, <b>printed on the chart under the apex of the inductive arc</b> (owner
    /// instruction, 2026-09-19).
    /// </summary>
    /// <remarks>
    /// <b>It is drawn HERE, with the chart, and not in the side panel</b>, which is the whole point
    /// of the instruction: this file is below the firewall and is what a copy to the clipboard, an
    /// SVG/PDF export and a headless <c>circuitrf smith</c> all run, so the number travels with the
    /// picture. A value that lived only in a panel would be missing from every chart anybody sent
    /// anyone.
    ///
    /// <para><b>Anchored to the arc, not to the frame.</b> The inductive circle is centred at
    /// (0, −1/Q) with radius √(1 + 1/Q²), so its topmost point is Γ = (0, √(1 + 1/Q²) − 1/Q) — inside
    /// the disc for every positive Q. The text is centred on that point horizontally and hangs from
    /// it downwards, which puts it immediately under the line and makes it track the arc up and down
    /// as a drag changes Q. It is drawn UNDER the handle and over the trajectories, like every other
    /// piece of chrome here.</para>
    ///
    /// <para>A background fill and no border: the arcs cross the busiest part of the chart, and a
    /// boxed number there would read as one more annotation rather than as the ruler's own scale.</para>
    /// </remarks>
    private static void DrawQValue(SKCanvas canvas, TransformSet tf, RenderTheme theme,
                                   SmithDesign design)
    {
        var q = design.ConstantQ;
        if (!q.Enabled || !(double.IsFinite(q.Q) && q.Q > 0)) return;

        var circle = SmithQArcs.Circle(q.Q, inductive: true);
        var apex   = new Complex(0.0, circle.Centre.Imaginary + circle.Radius);

        float lw = AxesRenderer.LineWidth((tf.CanvasSize.W, tf.CanvasSize.H));

        using var font = new SKFont(SkiaFonts.PlexRegular, LabelFontSize * lw / LabelBaseLw);
        font.GetFontMetrics(out var metrics);

        // "Q=1.234", with the equals sign (owner instruction, 2026-09-19): a bare gap read as two
        // separate things rather than as one quantity and its value.
        string text  = "Q=" + MatchValueFormat.Significant(q.Q, 4);
        float  width = font.MeasureText(text);

        var   at = tf.PrimaryToCanvas(apex.Real, apex.Imaginary);
        float padY = 2f * lw / LabelBaseLw;

        // Hangs from the apex: the baseline is one ascent below it, plus the gap, so the text's TOP
        // sits on the line rather than across it.
        float baseline = at.Y + padY - metrics.Ascent;
        float left     = at.X - width / 2f;

        // NO BACKGROUND FILL (owner instruction, 2026-09-19). The panel drew a near-opaque plate
        // behind the number, which on a light theme reads as a white patch punched out of the grid
        // directly under the arc — more conspicuous than the grid lines it was hiding.
        using var ink = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill,
                                      Color = ReadingColorOpaque };
        canvas.DrawText(text, left, baseline, SKTextAlign.Left, font, ink);
    }

    /// <summary>
    /// Which way load point <paramref name="k"/>'s label hangs: <b>−1 above the glyph, +1 below
    /// it</b> (owner instruction, 2026-09-19).
    /// </summary>
    /// <param name="canvasY">Every load point's canvas Y, in canvas coordinates — so LARGER is
    /// further DOWN the screen.</param>
    /// <remarks>
    /// <b>Away from the rest of the cluster</b>, decided by the mean of the others. For the two
    /// points the rule was stated about that is exactly "is the other one below me": the lower
    /// frequency's label goes above its glyph and the higher one's below, which is the arrangement
    /// with no box between the two points. It is separate from the drawing so the rule can be
    /// checked without a canvas.
    /// </remarks>
    internal static float LabelDirection(IReadOnlyList<float> canvasY, int k)
    {
        ArgumentNullException.ThrowIfNull(canvasY);
        if (canvasY.Count < 2) return -1f;

        float others = 0f;
        for (int j = 0; j < canvasY.Count; j++) if (j != k) others += canvasY[j];
        others /= canvasY.Count - 1;

        return others > canvasY[k] ? -1f : +1f;
    }

    /// <summary>True when node <paramref name="k"/> belongs to an element with a parameter to
    /// drag. An S1P or an S2P has none — its value is a file — so it carries no gripper
    /// (<c>R-smith2-6</c>), and the walk simply has no handle at that joint.</summary>
    public static bool Draggable(SmithChartScene scene, SmithDesign design, int k)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(design);

        if (k < 0 || k >= scene.Nodes.Count) return false;

        int ei = scene.Nodes[k].ElementIndex;
        if (ei < 0 || ei >= design.Elements.Count) return false;

        var element = design.Elements[ei];
        return !SmithComponentMap.UsesFile(element.Kind)
            && SmithComponentMap.ActiveParameterOf(element) != SmithParameter.None;
    }

    /// <summary>
    /// One arrowhead per curve, at the midpoint the sampler reported.
    /// </summary>
    /// <remarks>
    /// <b>The midpoint and the tangent come from brief 2 and are not re-derived here</b>
    /// (<c>R-smith5-2</c>). The midpoint is the polyline's ARC-LENGTH midpoint in Γ, so the arrow
    /// sits where the curve looks halfway and does not slide along it while somebody zooms; two
    /// adjacent arcs sharing a gripper are otherwise ambiguous about which way the walk goes, and a
    /// second derivation here would be a second chance to get the sign wrong.
    /// </remarks>
    private static void DrawArrowheads(SKCanvas canvas, TransformSet tf, SmithChartScene scene)
    {
        using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };

        foreach (var curve in scene.Trajectories)
        {
            if (curve.Tangent == Complex.Zero) continue;   // no length, no direction, no arrow

            paint.Color = SmithPlotBuilder.ColorFor(curve.ElementIndex);

            var at = tf.PrimaryToCanvas(curve.Midpoint.Real, curve.Midpoint.Imaginary);

            // The tangent is a unit vector in Γ; the canvas Y axis points the other way, so the
            // direction is transformed through the same map rather than used raw. A short step along
            // the curve, mapped, is the honest screen direction at any zoom or aspect.
            var ahead = tf.PrimaryToCanvas(curve.Midpoint.Real      + curve.Tangent.Real * 1e-3,
                                           curve.Midpoint.Imaginary + curve.Tangent.Imaginary * 1e-3);

            float dx = ahead.X - at.X, dy = ahead.Y - at.Y;
            float len = MathF.Sqrt(dx * dx + dy * dy);
            if (!(len > 0)) continue;
            dx /= len; dy /= len;

            const float L = 7f, W = 3.5f;
            using var path = new SKPath();
            path.MoveTo(at.X + dx * L, at.Y + dy * L);
            path.LineTo(at.X - dx * L * 0.35f - dy * W, at.Y - dy * L * 0.35f + dx * W);
            path.LineTo(at.X - dx * L * 0.35f + dy * W, at.Y - dy * L * 0.35f - dx * W);
            path.Close();
            canvas.DrawPath(path, paint);
        }
    }

    /// <summary>
    /// Each load point's frequency, in a label box <b>placed clear of the glyphs</b>.
    /// </summary>
    /// <remarks>
    /// <b>The box is the loadpull iso-lines' own</b> (<c>R-smith5-3</c>): the padded box
    /// <c>ContourRenderer.DrawIsoLineLabel</c> draws. It is CALLED, not re-drawn — the two surfaces
    /// cannot then drift apart in appearance, which is the whole reason the design note names this
    /// reuse specifically. What is passed to it is a two-point stub in CANVAS coordinates with an
    /// identity projection, so the placement below is in pixels and the box is still the placer's.
    ///
    /// <para><b>The placement is vertical and it is chosen per point</b> (owner instruction,
    /// 2026-09-19 — a label was landing on a glyph). The stubs used to fan radially outward from the
    /// centre of the chart, which spaces labels apart from EACH OTHER but says nothing about where
    /// the other load points are: a locus running outward from the centre puts every label straight
    /// over the next point along it. So each label goes ABOVE its own glyph when the other load
    /// points are below it on the chart and BELOW when they are above — the lowest frequency's label
    /// above the cluster, the highest frequency's below it, which is the owner's own rule and
    /// generalises to any number of rows through the mean.</para>
    ///
    /// <para><b>Then each box is pushed out just far enough to clear what is in its way, and the
    /// distance is a CONTINUOUS function of where everything is</b> (owner report, 2026-09-19 — the
    /// labels flicked between two positions during a drag). Every glyph and every box already
    /// placed is an obstacle; the offset is the largest any of them demands, and each demand fades
    /// in over <see cref="LabelApproachBand"/> pixels as the obstacle comes into horizontal range.
    /// It is bounded — see <see cref="LabelPushLimit"/> — because a chart zoomed until the points
    /// are a pixel apart has no placement that clears, and a label marching off the canvas is worse
    /// than a slight overlap.</para>
    ///
    /// <para><b>The continuity is the fix and it is not a refinement of the old rule.</b> This used
    /// to try the box one whole row further out at a time and stop at the first row that did not
    /// intersect anything, which is a DISCRETE decision taken from scratch on every frame of a drag:
    /// a load point moving half a pixel could flip the answer from row 1 to row 2 and back, moving
    /// the label a whole box height each way, twenty times a second. A maximum of continuous
    /// demands has no such threshold — the box slides out as an obstacle approaches and slides back
    /// as it leaves.</para>
    /// </remarks>
    private static void DrawLoadLabels(SKCanvas canvas, TransformSet tf, RenderTheme theme,
                                       SmithChartScene scene)
    {
        // ONE LOAD POINT CARRIES NO LABEL (owner instruction, 2026-09-19). The label exists to tell
        // one frequency's point from another's; with a single frequency there is nothing to tell it
        // from, the status strip along the bottom already names that frequency, and the box sits on
        // top of the one reading the chart is about. Two or more and it comes back.
        if (scene.LoadPoints.Count < 2) return;

        float lw = AxesRenderer.LineWidth((tf.CanvasSize.W, tf.CanvasSize.H));

        using var font = new SKFont(SkiaFonts.PlexRegular, LabelFontSize * lw / LabelBaseLw);
        using var labelPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill,
                                             Color = theme.TextColor };
        using var bgPaint    = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Fill,
                                             Color = theme.BackgroundColor.WithAlpha(215) };
        using var bgStroke   = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Stroke,
                                             StrokeWidth = 0.75f, Color = theme.GridColor };

        float scale = lw / LabelBaseLw;
        float padX  = 4f * scale;
        float padY  = 3f * scale;

        // The points, in canvas pixels, with the ones that cannot be projected dropped.
        var at    = new List<SKPoint>(scene.LoadPoints.Count);
        var index = new List<int>(scene.LoadPoints.Count);
        for (int i = 0; i < scene.LoadPoints.Count; i++)
        {
            var g = scene.LoadPoints[i].Gamma;
            if (!double.IsFinite(g.Real) || !double.IsFinite(g.Imaginary)) continue;

            var p = tf.PrimaryToCanvas(g.Real, g.Imaginary);
            if (!float.IsFinite(p.X) || !float.IsFinite(p.Y)) continue;

            at.Add(p);
            index.Add(i);
        }
        if (at.Count < 2) return;

        font.GetFontMetrics(out var metrics);
        float boxH  = metrics.Descent - metrics.Ascent + 2f * padY;
        float step  = boxH + LabelBoxGap;
        float first = LabelClearance * scale + boxH / 2f;

        // Every glyph is an obstacle from the start, including the ones whose own label has not been
        // placed yet — otherwise the first label placed would be the only one that avoided anything.
        float glyphHalf = LabelClearance * scale;
        var   taken     = at.Select(p => new SKRect(p.X - glyphHalf, p.Y - glyphHalf,
                                                    p.X + glyphHalf, p.Y + glyphHalf)).ToList();

        float   limit = first + LabelPushLimit * step;
        float[] ys    = [.. at.Select(p => p.Y)];

        for (int k = 0; k < at.Count; k++)
        {
            string text = scene.LoadPoints[index[k]].Label;

            float tw    = font.MeasureText(text);
            float halfW = tw / 2f + padX;
            float halfH = boxH / 2f;

            float dir = LabelDirection(ys, k);

            float offset = LabelOffset(at[k], halfW, halfH, dir, taken, first, limit, scale);
            float cy     = at[k].Y + dir * offset;
            var   box    = new SKRect(at[k].X - halfW, cy - halfH, at[k].X + halfW, cy + halfH);

            taken.Add(box);

            // The box is the shared placer's. The stub is two pixels long about the box's centre and
            // the projection is the identity, so ComputeLabelAnchors' single anchor lands exactly
            // where the arithmetic above put it — see LabelSpacing.
            (double X, double Y)[] stub = [(box.MidX, box.MidY - 1.0), (box.MidX, box.MidY + 1.0)];

            ContourRenderer.DrawIsoLineLabel(
                canvas, stub, static (x, y) => new SKPoint((float)x, (float)y),
                text, LabelSpacing, ringIndex: 1,
                font, labelPaint, bgPaint, bgStroke, padX, padY);
        }
    }

    /// <summary>
    /// How far along <paramref name="dir"/> one label box has to sit so that it clears everything
    /// already on the canvas — <b>a continuous function of where those things are</b>.
    /// </summary>
    /// <param name="glyph">The load point the label names, in canvas pixels.</param>
    /// <param name="halfW">Half the box's width, padding included.</param>
    /// <param name="halfH">Half its height.</param>
    /// <param name="dir">−1 for above the glyph, +1 for below — <see cref="LabelDirection"/>'s.</param>
    /// <param name="taken">Every glyph square and every box already placed.</param>
    /// <param name="first">The offset a label with nothing in its way takes.</param>
    /// <param name="limit">The furthest it may be pushed; past that the overlap is accepted.</param>
    /// <param name="scale">Canvas pixels per nominal line width, for <see cref="LabelApproachBand"/>.</param>
    /// <remarks>
    /// <b>The answer is the largest demand any obstacle makes, and each demand FADES IN.</b> An
    /// obstacle that is far away horizontally asks for nothing; one directly under the box asks for
    /// exactly enough to clear it; in between, the ask ramps over <see cref="LabelApproachBand"/>
    /// pixels. A maximum of continuous functions is continuous, which is the whole point — the
    /// previous rule tried whole rows and stopped at the first that did not intersect, so a
    /// sub-pixel move could change the answer by a box height and a drag made the label flicker
    /// between the two (owner report, 2026-09-19).
    ///
    /// <para>An obstacle on the far side of the glyph, or one the box already clears at
    /// <paramref name="first"/>, demands nothing: this only ever pushes a label further out, never
    /// pulls it in past its own clearance.</para>
    /// </remarks>
    internal static float LabelOffset(
        SKPoint glyph, float halfW, float halfH, float dir,
        IReadOnlyList<SKRect> taken, float first, float limit, float scale)
    {
        float band   = LabelApproachBand * scale;
        float offset = first;

        foreach (var r in taken)
        {
            // How far the box would have to be from the glyph for its NEAR edge to sit
            // LabelBoxGap clear of this obstacle, measured along dir.
            float need = dir < 0
                ? (glyph.Y - r.Top)    + halfH + LabelBoxGap
                : (r.Bottom - glyph.Y) + halfH + LabelBoxGap;

            if (!(need > offset) || need > limit) continue;

            // The horizontal ramp. Centre-to-centre separation at which the two just touch, plus
            // the band over which the claim fades in.
            float sep   = Math.Abs(glyph.X - (r.Left + r.Right) / 2f);
            float touch = halfW + (r.Right - r.Left) / 2f;
            float t     = Math.Clamp((touch + band - sep) / band, 0f, 1f);
            if (t <= 0f) continue;

            float asked = first + t * (need - first);
            if (asked > offset) offset = asked;
        }

        return Math.Min(offset, limit);
    }

    /// <summary>
    /// The anchor at node 0 and a ring at every draggable node after it.
    /// </summary>
    /// <remarks>
    /// <b>Subtle, per the specification</b> (§5.4): a small hollow ring in the trajectory's own
    /// colour, brightening on hover, filled while dragging. Node 0 is a small FILLED dot in the
    /// reading colour instead — it is the start of the walk rather than one of the moves, and
    /// drawing it as a ring would invite a drag it cannot answer.
    /// </remarks>
    private static void DrawGrippers(SKCanvas canvas, TransformSet tf, RenderTheme theme,
                                     SmithChartScene scene, SmithDesign design,
                                     SmithChromeState state)
    {
        using var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke,
                                         StrokeWidth = 1.4f };
        using var fill   = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };

        // Node 0 — the generator's anchor.
        var anchor = tf.PrimaryToCanvas(scene.NodeGamma[0].Real, scene.NodeGamma[0].Imaginary);
        fill.Color = SmithPlotBuilder.ReadingColor;
        canvas.DrawCircle(anchor.X, anchor.Y, RingRadius * 0.55f, fill);

        for (int k = 1; k < scene.NodeGamma.Count; k++)
        {
            if (!Draggable(scene, design, k)) continue;

            var colour = SmithPlotBuilder.ColorFor(scene.Nodes[k].ElementIndex);
            var at     = tf.PrimaryToCanvas(scene.NodeGamma[k].Real, scene.NodeGamma[k].Imaginary);

            bool dragging = k == state.DragNode;
            bool hovered  = k == state.HoverNode;

            if (dragging)
            {
                fill.Color = colour;
                canvas.DrawCircle(at.X, at.Y, RingRadius, fill);
            }

            // The RING is always drawn, filled or not, so a dragged handle keeps its outline against
            // the curve it sits on rather than becoming a coloured blob on a curve of the same colour.
            stroke.Color       = colour.WithAlpha(dragging || hovered ? (byte)255 : (byte)165);
            stroke.StrokeWidth = hovered || dragging ? 1.8f : 1.4f;
            canvas.DrawCircle(at.X, at.Y, RingRadius, stroke);

            // A hairline of the background inside the ring, so a hollow ring reads as hollow even
            // where it sits directly on top of its own trajectory.
            if (!dragging)
            {
                fill.Color = theme.BackgroundColor.WithAlpha(150);
                canvas.DrawCircle(at.X, at.Y, RingRadius - 1.4f, fill);
                stroke.Color = colour.WithAlpha(hovered ? (byte)255 : (byte)165);
                canvas.DrawCircle(at.X, at.Y, RingRadius, stroke);
            }
        }
    }
}

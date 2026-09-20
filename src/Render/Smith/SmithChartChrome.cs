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
    /// How far, in Γ, the FIRST load point's label is offset from the point it names — and the step
    /// each one after it adds.
    /// </summary>
    /// <remarks>
    /// <b>The stub grows with the index because the points do not.</b> A generator table is a few
    /// frequencies a few percent apart, so the load points sit almost on top of each other while
    /// their labels are fifty pixels wide; the iso-line placer's own per-ring stagger spaces labels
    /// along ONE polyline and is far too small a fraction of one stub to separate them. Fanning the
    /// stubs radially outward is what does — and the box is still the placer's, which is the part
    /// <c>R-smith5-3</c> is about.
    /// </remarks>
    private const double LabelOffsetGamma = 0.085;
    private const double LabelOffsetStep  = 0.075;

    /// <summary>
    /// A spacing wider than the label stub's own arc length, which
    /// <c>ContourRenderer.ComputeLabelAnchors</c> answers with exactly ONE anchor — see its own
    /// remarks. That is the property being used here: one box per load point, placed by the shared
    /// placer, staggered by ring index.
    /// </summary>
    private const double LabelSpacing = 10.0;

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
        DrawQHandle(canvas, tf, theme, state);
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

        string text  = "Q " + MatchValueFormat.Significant(q.Q, 4);
        float  width = font.MeasureText(text);

        var   at = tf.PrimaryToCanvas(apex.Real, apex.Imaginary);
        float padX = 3f * lw / LabelBaseLw;
        float padY = 2f * lw / LabelBaseLw;

        // Hangs from the apex: the baseline is one ascent below it, plus the gap, so the text's TOP
        // sits on the line rather than across it.
        float baseline = at.Y + padY - metrics.Ascent;
        float left     = at.X - width / 2f;

        using var bg = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Fill,
                                     Color = theme.BackgroundColor.WithAlpha(190) };
        canvas.DrawRect(new SKRect(left - padX, baseline + metrics.Ascent - padY,
                                   left + width + padX, baseline + metrics.Descent + padY), bg);

        using var ink = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill,
                                      Color = ReadingColorOpaque };
        canvas.DrawText(text, left, baseline, SKTextAlign.Left, font, ink);
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
    /// The constant-Q pair's grab ring, under the cursor and <b>on the arc</b>.
    /// </summary>
    /// <remarks>
    /// <b>The ARCS are traces and are drawn beneath the trajectories</b> (<c>R-smith9-3</c>); this is
    /// the HANDLE, which is chrome like every other handle here and belongs over the top with them.
    /// Nothing is drawn until the cursor is on an arc, so the pair reads as a ruler until the moment
    /// it is something to hold — which is also why an export draws none of it.
    /// </remarks>
    private static void DrawQHandle(SKCanvas canvas, TransformSet tf, RenderTheme theme,
                                    SmithChromeState state)
    {
        if ((state.DragQ ?? state.HoverQ) is not { } handle) return;

        var at = tf.PrimaryToCanvas(handle.At.Real, handle.At.Imaginary);

        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill,
                                       Color = theme.BackgroundColor.WithAlpha(150) };
        using var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke,
                                         StrokeWidth = 1.6f, Color = ReadingColorOpaque };

        if (state.DragQ is not null)
        {
            fill.Color = ReadingColorOpaque;
            canvas.DrawCircle(at.X, at.Y, RingRadius, fill);
        }
        else
        {
            canvas.DrawCircle(at.X, at.Y, RingRadius - 1.4f, fill);
        }

        canvas.DrawCircle(at.X, at.Y, RingRadius, stroke);
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
    /// Each load point's frequency, in a label box.
    /// </summary>
    /// <remarks>
    /// <b>The box is the loadpull iso-lines' own</b> (<c>R-smith5-3</c>): the padded, world-unit
    /// spaced, staggered box <c>ContourRenderer.DrawIsoLineLabel</c> draws and
    /// <c>ComputeLabelAnchors</c> places. It is CALLED, not re-drawn — the two surfaces cannot then
    /// drift apart in appearance, which is the whole reason the design note names this reuse
    /// specifically.
    ///
    /// <para>The placer walks a POLYLINE, so each point is handed a short stub running radially
    /// outward from the centre of the chart, and the stagger that spaces successive iso-line labels
    /// is what spaces successive load points' boxes here. Outward rather than in any fixed
    /// direction, so a label never lands over the middle of the chart where the trajectories are.</para>
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

        float padX = 4f * lw / LabelBaseLw;
        float padY = 3f * lw / LabelBaseLw;

        SKPoint Project(double wx, double wy) => tf.PrimaryToCanvas(wx, wy);

        for (int i = 0; i < scene.LoadPoints.Count; i++)
        {
            var g = scene.LoadPoints[i].Gamma;
            if (!double.IsFinite(g.Real) || !double.IsFinite(g.Imaginary)) continue;

            double mag = g.Magnitude;
            var dir = mag > 1e-9 ? g / mag : Complex.One;

            double reach = LabelOffsetGamma + LabelOffsetStep * i;
            (double X, double Y)[] stub =
            [
                (g.Real, g.Imaginary),
                (g.Real + dir.Real * reach, g.Imaginary + dir.Imaginary * reach),
            ];

            ContourRenderer.DrawIsoLineLabel(canvas, stub, Project,
                                             scene.LoadPoints[i].Label, LabelSpacing, i,
                                             font, labelPaint, bgPaint, bgStroke, padX, padY);
        }
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

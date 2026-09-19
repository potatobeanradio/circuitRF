using System;
using System.Numerics;
using CircuitRF.Design.Smith;
using CircuitRF.Ui.DataDisplay.Controls;
using SkiaSharp;

namespace CircuitRF.Ui.Smith;

/// <summary>
/// One gripper: the node of the walk it sits on. Node k, for k ≥ 1, drags element k−1.
/// </summary>
/// <remarks>
/// <b>Node 0 never produces one.</b> It is the generator — drawn, so the walk has a visible start,
/// and not draggable, because a drag there would have to guess which generator-table row it meant
/// (§4.3). <see cref="SmithGripperOverlay.HitTest"/> returns null over it, which is what leaves the
/// press to the control and lets a click on the anchor pan the chart like any other empty spot.
/// </remarks>
public sealed record SmithGripperHandle(int NodeIndex);

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
/// The grippers, drawn over the chart and dragged on it (<c>brief-smith-5-chart.md</c>
/// <c>R-smith5-6</c>, <c>R-smith5-7</c>; <c>docs/design/smith-chart.md</c> §5.4, §4.3).
/// </summary>
/// <remarks>
/// <b>It draws transient chrome and calls back; it never touches the <c>Plot</c>.</b> That is
/// <see cref="IPlotOverlay"/>'s second rule and <c>ILayoutCanvasOverlay</c>'s before it — the view
/// model owns the design, mutates it, and rebuilds the plot, and an overlay that edited the scene it
/// is drawn over would be a second, invisible author of the picture.
///
/// <para><b>What it draws, and nothing else:</b> an arrowhead at each trajectory's own reported
/// midpoint, the generator's anchor, one hollow ring per draggable node, the load points' label
/// boxes, and the constant-Q pair's grab ring. The curves and the points themselves are TRACES — the
/// Data Display draws those.</para>
///
/// <para><b>The constant-Q ARCS are traces too, and deliberately</b> (<c>R-smith9-3</c>): they are
/// drawn BENEATH the trajectories, and this overlay's hook is inside <c>PlotRenderer.Draw</c> above
/// them. What brief 9 added here is a HANDLE KIND — <see cref="SmithQHandle"/> — and not a second
/// overlay. See <c>src/Ui/RESOLVED.md</c>.</para>
/// </remarks>
public sealed class SmithGripperOverlay : IPlotOverlay
{
    private readonly SmithChartViewModel _vm;

    /// <summary>Canvas-pixel radius of a gripper ring, and of its hit target.</summary>
    private const float RingRadius = 4.5f;
    private const double HitRadius = 8.0;

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

    public SmithGripperOverlay(SmithChartViewModel vm) => _vm = vm;

    private int _hoverNode = -1;
    private int _dragNode  = -1;

    /// <summary>The Q handle under the cursor, and the one being dragged — null when neither.</summary>
    private SmithQHandle? _hoverQ;
    private SmithQHandle? _dragQ;

    /// <summary>Inductive first, so a tie on a point both branches touch — Γ = ±1 — goes to the
    /// upper half of the chart, which is the one the cursor is in when it is not exactly on the
    /// real axis.</summary>
    private static readonly bool[] TwoBranches = [true, false];

    // ── the seam ─────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public object? HitTest(double canvasX, double canvasY, TransformSet tf)
    {
        var scene = _vm.Scene;
        if (!scene.HasContent) return null;

        int    best     = -1;
        double bestDist = double.MaxValue;

        // FROM 1, never from 0 — node 0 is the anchor and offers no handle (R-smith5-7).
        //
        //  HIDING THE GRIPPERS HIDES THE GRIPPERS. The constant-Q pair is its own setting and keeps
        //  its handle either way, because a visible arc nobody could grab would be a control that
        //  had silently stopped working.
        for (int k = 1; _vm.Design.Chart.ShowGrippers && k < scene.NodeGamma.Count; k++)
        {
            if (!Draggable(scene, k)) continue;

            var p  = tf.PrimaryToCanvas(scene.NodeGamma[k].Real, scene.NodeGamma[k].Imaginary);
            double dx = p.X - canvasX, dy = p.Y - canvasY;
            double d  = Math.Sqrt(dx * dx + dy * dy);

            // <= rather than <, so a tie goes to the LATER node: it is drawn on top, and the one the
            // user can see is the one they meant.
            if (d <= HitRadius && d <= bestDist) { bestDist = d; best = k; }
        }

        if (best >= 0) return new SmithGripperHandle(best);

        // THE GRIPPERS WIN A TIE, which is why this runs only after they have all missed. A gripper
        // is the work and the arcs are the ruler laid over it; an arc that could steal a joint
        // sitting on it would make the one handle the user came for unreachable.
        return HitTestQ(canvasX, canvasY, tf);
    }

    /// <summary>
    /// The nearer constant-Q branch under a canvas point, or null.
    /// </summary>
    /// <remarks>
    /// <b>Analytically, against the CIRCLE</b> rather than by walking the emitted samples: a hit test
    /// runs on every pointer move over the plot, and the closest point on a circle is one normalize.
    /// The pixel distance is then measured between the projections of the cursor's Γ and that closest
    /// point, so the hit radius is in pixels at whatever zoom the chart is at — which is what the
    /// grippers' own radius means too.
    ///
    /// <para><b>The closest point has to be on the DRAWN arc.</b> Each circle leaves the unit disc,
    /// and the half outside it is not painted (<see cref="SmithQArcs.Arc"/>), so a grab out there
    /// would be a grab on something invisible.</para>
    /// </remarks>
    private SmithQHandle? HitTestQ(double canvasX, double canvasY, TransformSet tf)
    {
        var q = _vm.Design.ConstantQ;
        if (!q.Enabled || !(double.IsFinite(q.Q) && q.Q > 0)) return null;

        var (wx, wy) = tf.PrimaryFromCanvas((float)canvasX, (float)canvasY);
        var cursor   = new Complex(wx, wy);
        if (!double.IsFinite(cursor.Real) || !double.IsFinite(cursor.Imaginary)) return null;

        SmithQHandle? best     = null;
        double        bestDist = double.MaxValue;

        foreach (bool inductive in TwoBranches)
        {
            var circle = SmithQArcs.Circle(q.Q, inductive);

            var    radial = cursor - circle.Centre;
            double mag    = radial.Magnitude;
            if (mag < 1e-12) continue;                       // dead centre: every point is nearest

            var on = circle.Centre + radial / mag * circle.Radius;

            // Outside the disc is the half of the circle nothing painted.
            if (on.Magnitude > 1.0 + 1e-9) continue;

            var    p = tf.PrimaryToCanvas(on.Real, on.Imaginary);
            double d = Math.Sqrt((p.X - canvasX) * (p.X - canvasX)
                               + (p.Y - canvasY) * (p.Y - canvasY));

            if (d <= HitRadius && d < bestDist) { bestDist = d; best = new SmithQHandle(inductive, on); }
        }

        return best;
    }

    /// <inheritdoc/>
    public void DragBegin(object handle)
    {
        switch (handle)
        {
            case SmithGripperHandle h when _vm.BeginGripperDrag(h.NodeIndex):
                _dragNode = h.NodeIndex;
                break;

            case SmithQHandle q when _vm.BeginQDrag():
                _dragQ = q;
                break;
        }
    }

    /// <inheritdoc/>
    public void DragTo(Complex gammaWorld) => DragTo(gammaWorld, shift: false);

    /// <inheritdoc/>
    public void DragTo(Complex gammaWorld, bool shift)
    {
        if (_dragQ is not null)
        {
            // The ring follows the ARC and not the cursor, so it is re-placed from the Q the drag
            // just produced rather than from where the pointer is: that is the difference between a
            // handle that slides along the curve it belongs to and one that leaves it.
            _vm.DragQTo(gammaWorld, shift);
            _dragQ = NearestOn(_dragQ.Inductive, gammaWorld) ?? _dragQ;
            return;
        }

        if (_dragNode < 0) return;
        _vm.DragGripperTo(gammaWorld);
    }

    /// <inheritdoc/>
    public void DragEnd(bool cancelled)
    {
        if (_dragQ is not null)
        {
            _dragQ = null;
            _vm.EndQDrag(cancelled);
            return;
        }

        if (_dragNode < 0) return;
        _dragNode = -1;
        _vm.EndGripperDrag(cancelled);
    }

    /// <inheritdoc/>
    public bool Hover(object? handle)
    {
        int  node    = handle is SmithGripperHandle h ? h.NodeIndex : -1;
        var  q       = handle as SmithQHandle;
        bool changed = node != _hoverNode || q != _hoverQ;

        _hoverNode = node;
        _hoverQ    = q;
        return changed;
    }

    /// <summary>The point on one branch nearest <paramref name="gamma"/>, or null when the pair is
    /// off or that point is outside the disc — <see cref="HitTestQ"/>'s arithmetic, once.</summary>
    private SmithQHandle? NearestOn(bool inductive, Complex gamma)
    {
        var settings = _vm.Design.ConstantQ;
        if (!settings.Enabled || !(double.IsFinite(settings.Q) && settings.Q > 0)) return null;

        var    circle = SmithQArcs.Circle(settings.Q, inductive);
        var    radial = gamma - circle.Centre;
        double mag    = radial.Magnitude;
        if (!(mag > 1e-12)) return null;

        var on = circle.Centre + radial / mag * circle.Radius;
        return on.Magnitude <= 1.0 + 1e-9 ? new SmithQHandle(inductive, on) : null;
    }

    /// <summary>True when node <paramref name="k"/> belongs to an element with a parameter to
    /// drag. An S1P or an S2P has none — its value is a file — so it carries no gripper
    /// (<c>R-smith2-6</c>), and the walk simply has no handle at that joint.</summary>
    private bool Draggable(SmithChartScene scene, int k)
    {
        int ei = scene.Nodes[k].ElementIndex;
        if (ei < 0 || ei >= _vm.Design.Elements.Count) return false;

        var element = _vm.Design.Elements[ei];
        return !SmithComponentMap.UsesFile(element.Kind)
            && SmithChartViewModel.ActiveParameterOf(element) != SmithParameter.None;
    }

    // ── drawing ──────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public void Draw(SKCanvas canvas, TransformSet tf, RenderTheme theme)
    {
        var scene = _vm.Scene;
        if (!scene.HasContent) return;

        // The canvas, the transform and the theme are ARGUMENTS of this frame and none of them is
        // kept — ContourRenderer once drew every contour on every Smith plot to the first target it
        // had been handed, because the target was remembered rather than passed.
        DrawArrowheads(canvas, tf, scene);
        if (_vm.Design.Chart.ShowLabels) DrawLoadLabels(canvas, tf, theme, scene);
        if (_vm.Design.Chart.ShowGrippers) DrawGrippers(canvas, tf, theme, scene);
        DrawQHandle(canvas, tf, theme);
    }

    /// <summary>
    /// The constant-Q pair's grab ring, under the cursor and <b>on the arc</b>.
    /// </summary>
    /// <remarks>
    /// <b>The ARCS are traces and are drawn beneath the trajectories</b> (<c>R-smith9-3</c>); this is
    /// the HANDLE, which is chrome like every other handle on this overlay and belongs over the top
    /// with them. Nothing is drawn until the cursor is on an arc, so the pair reads as a ruler until
    /// the moment it is something to hold.
    /// </remarks>
    private void DrawQHandle(SKCanvas canvas, TransformSet tf, RenderTheme theme)
    {
        if ((_dragQ ?? _hoverQ) is not { } handle) return;

        var at = tf.PrimaryToCanvas(handle.At.Real, handle.At.Imaginary);

        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill,
                                       Color = theme.BackgroundColor.WithAlpha(150) };
        using var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke,
                                         StrokeWidth = 1.6f, Color = ReadingColorOpaque };

        if (_dragQ is not null)
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

    /// <summary>The arcs' own colour — the READING grey the load points and node 0 are drawn in, so
    /// the handle belongs to the arc it sits on rather than to a trajectory.</summary>
    private static SKColor ReadingColorOpaque => SmithPlotBuilder.ReadingColor;

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
        if (scene.LoadPoints.Count == 0) return;

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
    private void DrawGrippers(SKCanvas canvas, TransformSet tf, RenderTheme theme,
                              SmithChartScene scene)
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
            if (!Draggable(scene, k)) continue;

            var colour = SmithPlotBuilder.ColorFor(scene.Nodes[k].ElementIndex);
            var at     = tf.PrimaryToCanvas(scene.NodeGamma[k].Real, scene.NodeGamma[k].Imaginary);

            bool dragging = k == _dragNode;
            bool hovered  = k == _hoverNode;

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

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
/// The grippers' GESTURE — hit-tested, pressed and dragged on the chart (<c>brief-smith-5-chart.md</c>
/// <c>R-smith5-6</c>, <c>R-smith5-7</c>; <c>docs/design/smith-chart.md</c> §5.4, §4.3).
/// </summary>
/// <remarks>
/// <b>It draws transient chrome and calls back; it never touches the <c>Plot</c>.</b> That is
/// <see cref="IPlotOverlay"/>'s second rule and <c>ILayoutCanvasOverlay</c>'s before it — the view
/// model owns the design, mutates it, and rebuilds the plot, and an overlay that edited the scene it
/// is drawn over would be a second, invisible author of the picture.
///
/// <para><b>The DRAWING is <see cref="SmithChartChrome"/>'s and is below the firewall</b>
/// (<c>brief-smith-10-cli-verb.md</c> <c>R-smith10-3</c>): the arrowheads, the load-point label
/// boxes, the generator's anchor, the rings and the constant-Q grab ring are all produced there, so
/// an exported picture and a headless <c>circuitrf smith</c> carry them rather than quietly losing
/// them. What is left here is the half a live frame has and an export does not — which node is under
/// the cursor, which one is being dragged, and what that drag does to the document.</para>
///
/// <para><b>The constant-Q ARCS are traces too, and deliberately</b> (<c>R-smith9-3</c>): they are
/// drawn BENEATH the trajectories, and this overlay's hook is inside <c>PlotRenderer.Draw</c> above
/// them. What brief 9 added here is a HANDLE KIND — <see cref="SmithQHandle"/> — and not a second
/// overlay. See <c>src/Ui/RESOLVED.md</c>.</para>
/// </remarks>
public sealed class SmithGripperOverlay : IPlotOverlay
{
    private readonly SmithChartViewModel _vm;

    /// <summary>The hit radius the rings are drawn at, which is <see cref="SmithChartChrome"/>'s —
    /// a handle whose target and whose ring disagreed would miss by the difference, silently.</summary>
    private const double HitRadius = SmithChartChrome.HitRadius;

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

    /// <summary>Which nodes offer a handle — <see cref="SmithChartChrome.Draggable"/>'s answer and
    /// no other, so the ring that is DRAWN and the target that is HIT are one rule.</summary>
    private bool Draggable(SmithChartScene scene, int k)
        => SmithChartChrome.Draggable(scene, _vm.Design, k);

    // ── drawing ──────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>
    /// <b>Every pixel of this is <see cref="SmithChartChrome"/>'s</b> (<c>R-smith10-3</c>), which is
    /// below the firewall so that <c>circuitrf smith</c> draws the arrowheads, the load labels and
    /// the grippers rather than a chart that is missing them. What this class keeps is the GESTURE —
    /// the hit test, the press, the drag and the undo entry — and the hover/drag state, which is the
    /// only thing a live frame knows that an exported one does not.
    /// </remarks>
    public void Draw(SKCanvas canvas, TransformSet tf, RenderTheme theme)
        => SmithChartChrome.Draw(canvas, tf, theme, _vm.Scene, _vm.Design,
                                 new SmithChromeState(_hoverNode, _dragNode, _hoverQ, _dragQ));
}

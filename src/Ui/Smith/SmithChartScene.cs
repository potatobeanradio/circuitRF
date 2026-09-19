using System.Collections.Generic;
using System.Numerics;
using CircuitRF.Design.Smith;

namespace CircuitRF.Ui.Smith;

/// <summary>
/// One load point: where the cascade lands at one frequency, and how that frequency is spelled.
/// </summary>
/// <param name="Gamma">Γ of the walk's LAST node, against the chart's own Z₀.</param>
/// <param name="Label">The frequency, formatted by <c>MatchValueFormat</c> — the strip's own
/// spelling, so the chart and the strip cannot disagree about which point is which.</param>
/// <param name="IsDesignFrequency">True for the one the status strip reports, which is drawn
/// emphasised (<c>R-smith5-2</c>).</param>
internal readonly record struct SmithLoadPoint(Complex Gamma, string Label, bool IsDesignFrequency);

/// <summary>
/// Everything one frame of the chart is drawn from, evaluated once (<c>brief-smith-5-chart.md</c>
/// <c>R-smith5-2</c>).
/// </summary>
/// <remarks>
/// <b>It exists so the traces and the overlay are the SAME evaluation.</b> The trajectories and the
/// load points go onto the <c>Plot</c> as traces; the grippers, the arrowheads and the load-point
/// labels are drawn by <see cref="SmithGripperOverlay"/> over the top. Evaluating twice — once for
/// each — is how a handle ends up half a pixel off the curve it belongs to during a drag, and the
/// difference is invisible until someone looks closely at a screenshot.
///
/// <para><b>Nothing here is re-derived.</b> The nodes are <see cref="SmithCascade.Evaluate"/>'s, the
/// curves and their arrow midpoints and tangents are <see cref="SmithCascade.Trajectories"/>'s
/// (<c>R-smith5-2</c>: do not re-derive the arrowhead from the polyline — two adjacent arcs sharing
/// a gripper are ambiguous about direction and a second derivation is a second chance to get the
/// sign wrong), and Γ is <see cref="SmithCascade.Gamma"/>'s.</para>
/// </remarks>
internal sealed class SmithChartScene
{
    /// <summary>The empty scene — what a design whose evaluation refused draws. The status strip is
    /// already saying what is wrong, with its numbers in it.</summary>
    public static readonly SmithChartScene Empty = new();

    /// <summary>The walk at the design frequency. Node 0 is the generator; node k is the output of
    /// the k-th ENABLED element. Empty when the evaluation refused.</summary>
    public IReadOnlyList<SmithNode> Nodes { get; init; } = [];

    /// <summary>Γ of each of <see cref="Nodes"/>, against the chart's Z₀ — the gripper positions, in
    /// the plane they are drawn in.</summary>
    public IReadOnlyList<Complex> NodeGamma { get; init; } = [];

    /// <summary>One curve per enabled element, in the document's own element order.</summary>
    public IReadOnlyList<SmithTrajectory> Trajectories { get; init; } = [];

    /// <summary>One per generator-table row, plus the design frequency when it is not one of
    /// them.</summary>
    public IReadOnlyList<SmithLoadPoint> LoadPoints { get; init; } = [];

    /// <summary>Γ(conj(Z_gen(f))) per generator-table row — the faint, un-selectable targets.</summary>
    public IReadOnlyList<Complex> ConjugateTargets { get; init; } = [];

    /// <summary>True when the evaluation produced something to draw.</summary>
    public bool HasContent => NodeGamma.Count > 0;
}

// Convex decomposition — M0's INSTRUMENT. A measurement, not a behaviour.
//
// brief-convex-decomposition.md §2 asks the conformal mesher's per-cell refusal site to CLASSIFY
// every cell it turns away, before anything is built on top of the answer. The classification is the
// milestone: §1 argues that convexity is a SUFFICIENT test being used as a necessary one, and that
// what the strip construction in RooftopSupport actually needs is FLOW-SIMPLICITY — one interval per
// transverse line, per direction. Whether that is true of the artwork is a measurement.
//
// Attaching an instrument rather than reading the answer out of the code is the standing rule here
// (PlanarFillDiagnostics, L9's Tier 1): the fill is asserted BIT-IDENTICAL with and without it, and
// so is the mesh — an instrument that perturbed what it measures would be worse than none.

namespace CircuitRF.Engine.Mom;

/// <summary>
/// One cell the conformal pass turned away, with the four quantities §2 asks for.
/// </summary>
/// <param name="AreaFraction">The clipped region's area as a fraction of its grid rectangle. A
/// fallback that is nearly the whole rectangle costs almost nothing; a thin wedge costs everything.</param>
/// <param name="ReflexVertices">Reflex vertices of the CLIPPED region — 1 is the case §1 expects
/// (the rim's own curvature), more is a different animal.</param>
/// <param name="XSimple">Every line of constant y meets the region in one interval, so an x-directed
/// rooftop's strips describe it exactly.</param>
/// <param name="YSimple">The same in the other direction. <b>The two are independent</b> — a region
/// can be x-simple and not y-simple, which is why the refusal being asked of the whole CELL for both
/// directions at once is what §1 objects to.</param>
public readonly record struct ConformalFallbackCell(
    int    LayerIndex,
    int    IX,
    int    IY,
    string Reason,
    double AreaFraction,
    int    ReflexVertices,
    bool   XSimple,
    bool   YSimple);

/// <summary>
/// Optional collector handed to <see cref="SurfaceMesher.Mesh"/>. Null — the default — is the shipped
/// path exactly; nothing on it feeds back into the mesh.
/// </summary>
public sealed class ConformalDiagnostics
{
    /// <summary>Every cell the conformal pass STAIRCASED because its clipped region is flow-simple in
    /// neither direction, in (IY, IX) scan order. M1 drove this to zero on every shipping part.</summary>
    public List<ConformalFallbackCell> Fallbacks { get; } = [];

    /// <summary>Cells that were CUT and kept, but whose strips describe them in one direction only —
    /// R-cvx-2's second count, and a different event from a staircased cell.</summary>
    public List<ConformalFallbackCell> OneDirectionOnly { get; } = [];

    /// <summary>
    /// <b>M0's own table, and M1's non-vacuity guard: cut cells whose clipped region is NOT
    /// convex</b> — i.e. exactly the set the old predicate refused, now admitted.
    ///
    /// <para>It has to be collected rather than inferred, because once M1 ships there is no refusal
    /// left to instrument and the measurement would have nothing to count. Keeping it live is what
    /// lets §2's table be re-taken, and what proves the cells M1 admits are the ones M0 measured
    /// rather than some other set.</para>
    /// </summary>
    public List<ConformalFallbackCell> AdmittedNonConvex { get; } = [];
}

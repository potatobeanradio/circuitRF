// L8b — the surface mesh itself: rectangular cells, their ORDER, and the rooftop pairs the cells
// imply.
//
// R-msh-2 — CELL ORDER IS A PERMANENT CONTRACT. L8c's matrix fill, L8d's port excitation and L8e's
// current-density heat map all index by this order. A mesher whose output order depends on a hash
// iteration, a parallel loop or a floating-point tie is a solver whose s-parameters are
// irreproducible run to run — and that is found much later, as "the answer moved slightly", which is
// the most expensive way to find it.
//
// The order is (LayerIndex, IY, IX), compared as INTEGERS. Those are grid indices the mesher itself
// assigns, not coordinates: there is no floating-point tie to break, and no dictionary or set
// iteration anywhere on the path that produces them.

namespace CircuitRF.Engine.Mom;

/// <summary>
/// One rectangular cell of the surface mesh, in metres.
///
/// <para><see cref="IX"/>/<see cref="IY"/> are its indices into the mesh's own gridline arrays —
/// the cell spans <c>GridX[IX] … GridX[IX+1]</c> by <c>GridY[IY] … GridY[IY+1]</c>. They exist so
/// R-msh-2's ordering and the adjacency that produces <see cref="PlanarBasis"/> are exact integer
/// questions rather than floating-point ones.</para>
///
/// <para><b>D8 forward note.</b> The cell is described by its own four coordinates rather than only
/// by its grid indices, so a future conformal or diagonal boundary cell — one straight cut through
/// an otherwise rectangular cell, which is a far smaller commitment than a triangulator and
/// addresses the mitre directly — can be added as an extra field here without reshaping the type or
/// the report. It is explicitly NOT built in L8b.</para>
/// </summary>
public sealed record PlanarCell(
    int    LayerIndex,
    int    IX,
    int    IY,
    double XMin,
    double YMin,
    double XMax,
    double YMax)
{
    public double Width   => XMax - XMin;
    public double Height  => YMax - YMin;
    public double Area    => Width * Height;
    public double CenterX => 0.5 * (XMin + XMax);
    public double CenterY => 0.5 * (YMin + YMax);

    /// <summary>The longer of the two edges — what the λ_g/N cap is checked against (R-msh-3).</summary>
    public double LongestEdge => Math.Max(Width, Height);
}

public enum PlanarBasisDirection
{
    /// <summary>Current flows along +x; the basis spans a pair of cells adjacent in x.</summary>
    X,
    /// <summary>Current flows along +y.</summary>
    Y,
    /// <summary>
    /// <b>L9c — current flows along +z, up a via.</b> The basis spans a pair of cells adjacent in
    /// <b>z</b>: the same <c>(IX, IY)</c> of the shared grid on two consecutive conductor levels. See
    /// <see cref="PlanarBasisFunctions"/>'s header for why that is the rooftop construction one
    /// dimension over rather than a second basis family.
    /// </summary>
    Z,
}

/// <summary>
/// One rooftop basis function: a pair of cells sharing an internal edge, with the current flowing
/// across that edge. <b>This is the unknown</b> — R-msh-6's whole point is that N is the number of
/// these, not the number of cells.
/// </summary>
/// <param name="CellA">Index into <see cref="PlanarMesh.Cells"/> — the lower-index (left/below) cell.</param>
/// <param name="CellB">Index into <see cref="PlanarMesh.Cells"/> — the higher-index cell.</param>
public sealed record PlanarBasis(int LayerIndex, int CellA, int CellB, PlanarBasisDirection Direction);

/// <summary>
/// The surface mesh: one tensor-product grid shared by every conductor layer (D8), the cells of it
/// that are covered by metal, and the rooftop pairs those cells imply.
///
/// <para><b>D8 — the grid model, decided here because L8c inherits it and it is expensive to
/// reverse: TENSOR-PRODUCT.</b> Every gridline spans the whole domain. The alternative — an
/// independent-cell (quadtree-ish) mesh — refines locally and is far cheaper on mixed-scale
/// geometry, but its non-conforming cell edges make rooftop pairing genuinely harder, and that
/// difficulty would land on L8c rather than here. The cost of the choice is that a cell size demanded
/// <i>anywhere</i> propagates a fine row or column <i>everywhere</i>; that cost is measured rather
/// than assumed — see <c>SurfaceMesherTests</c> Tier 7, which reports N for the three non-Manhattan
/// library PCells against R17's 5,000 ceiling.</para>
///
/// <para>The grid is shared across layers rather than per-layer so that cells on different levels
/// stay conforming — L9's multi-level stack needs vertical current to cross between them, and a
/// per-layer grid would make that a re-mesh rather than an addition.</para>
/// </summary>
public sealed record PlanarMesh(
    IReadOnlyList<PlanarCell>  Cells,
    IReadOnlyList<PlanarBasis> Bases,
    IReadOnlyList<string>      LayerNames,
    IReadOnlyList<double>      GridX,
    IReadOnlyList<double>      GridY);

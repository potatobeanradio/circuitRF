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
/// <para><b>D8's forward note, now taken up.</b> The cell is described by its own four coordinates
/// rather than only by its grid indices, so a conformal boundary cell could be "added as an extra
/// field here without reshaping the type or the report". <see cref="Region"/> is that field. Null —
/// the whole rectangle — is the pre-conformal case and is what every Manhattan cell still is, so
/// R-cut-2's bit-identity holds by construction: the same expressions produce the same numbers.</para>
/// </summary>
/// <param name="XMin">The cell's GRID rectangle. For a cut cell this BOUNDS the metal rather than
/// equalling it, and for a merged cell (R-cut-3) it is the bounding box of the two grid positions the
/// cell now covers. <see cref="Area"/>, <see cref="CentroidX"/> and every integral are asked of
/// <see cref="Region"/>; only the quadrature's own frame and the λ_g cap are asked of the
/// rectangle.</param>
/// <param name="Region">
/// <b>Conformal boundary cells — the part of the rectangle that is actually metal.</b> Null means the
/// whole rectangle, which is the only thing L8b could produce and is what a Manhattan mesh is made
/// entirely of. See <see cref="PlanarCellRegion"/>.
/// </param>
public sealed record PlanarCell(
    int    LayerIndex,
    int    IX,
    int    IY,
    double XMin,
    double YMin,
    double XMax,
    double YMax,
    PlanarCellRegion? Region = null)
{
    /// <summary>
    /// <b>EM-TFR — which film of <see cref="PlanarMesh.Films"/> this cell is, or −1 for the level's
    /// own metal.</b> A property rather than a positional field so every existing construction of a
    /// cell still reads as metal.
    /// </summary>
    public int Film { get; init; } = -1;

    /// <summary>
    /// <b>PMS-3 — which stack of <see cref="PlanarMesh.Stacks"/> this cell stands for, or −1.</b> Set only on the
    /// interior of a via-joined stacked region, where the stack's other levels were not meshed: the cell carries
    /// their sheets in parallel with its own. Never set together with <see cref="Film"/>.
    /// </summary>
    public int Stack { get; init; } = -1;

    /// <summary>The GRID rectangle's width — the quadrature's own frame and the λ_g cap's subject.
    /// <b>Not</b> the metal's extent when <see cref="Region"/> is set.</summary>
    public double Width   => XMax - XMin;
    /// <inheritdoc cref="Width"/>
    public double Height  => YMax - YMin;

    /// <summary><b>The METAL's area</b> — the region's when there is one, the rectangle's otherwise.
    /// This is what the basis is normalised by (1/Area), so it is the one place a cut has to be
    /// honoured or the divergence pulse stops integrating to ±1.</summary>
    public double Area    => Region?.Area ?? Width * Height;

    /// <summary>The grid rectangle's centre — the frame the tensor quadrature rule is written in.
    /// Equal to <see cref="CentroidX"/> for an uncut cell, and deliberately NOT the metal's centroid
    /// for a cut one.</summary>
    public double CenterX => 0.5 * (XMin + XMax);
    /// <inheritdoc cref="CenterX"/>
    public double CenterY => 0.5 * (YMin + YMax);

    /// <summary>The METAL's centroid — what "how far apart are these two cells" means once a cell is
    /// not its own rectangle, and therefore what the fill's rule selection keys off.</summary>
    public double CentroidX => Region?.CentroidX ?? CenterX;
    /// <inheritdoc cref="CentroidX"/>
    public double CentroidY => Region?.CentroidY ?? CenterY;

    /// <summary>True when the cell follows the metal rather than the grid.</summary>
    public bool IsCut => Region is not null;

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
/// <param name="CellA">Index into <see cref="PlanarMesh.Cells"/> — the lower-index (left/below) cell.
/// <b>For an <see cref="AttachesToGround"/> basis both cells are the ONE meshed foot</b>; the signs
/// come from <c>PlanarBasisFunctions.Halves</c>, not from the two indices.</param>
/// <param name="CellB">Index into <see cref="PlanarMesh.Cells"/> — the higher-index cell.</param>
/// <param name="AttachesToGround">
/// <b>The ground-attachment (HALF) basis: one meshed foot, and the other terminal is the laterally
/// infinite PEC the Green's function handles analytically.</b> A backside via joins a signal level to
/// the ground plane, which is never a meshed level — L9c's rooftop-in-z spans two MESHED levels and
/// cannot express it, which is why L9's own phase gate could not gate the thing §11 asks for.
///
/// <para>Its z span runs from <c>PlanarLevels.GroundZ</c> to <c>PlanarLevels.Of(LayerIndex)</c>, and
/// for this basis alone <see cref="LayerIndex"/> names the MESHED level rather than the lower of two.
/// Its current still flows +z (from the ground up to the metal), exactly like every other vertical
/// basis — see <c>PlanarBasisFunctions</c>' header for why matching that direction was worth more
/// than matching a sign in prose.</para>
/// </param>
public sealed record PlanarBasis(int LayerIndex, int CellA, int CellB, PlanarBasisDirection Direction,
                                 bool AttachesToGround = false);

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
    IReadOnlyList<double>      GridY)
{
    /// <summary>
    /// <b>PM-2 — the merged basis this mesh is SOLVED in</b>, or null (the default) when every
    /// elemental rooftop is its own unknown. <see cref="Cells"/> and <see cref="Bases"/> are the
    /// elemental mesh either way: ports, the current-density map, the far field and the DC solve all
    /// index by them, and a solve hands back elemental currents (<c>T·I</c>) whichever basis it was
    /// factored in. Only the dense system reads this.
    /// </summary>
    public PlanarRooftopMerge? Merge { get; init; }

    /// <summary>
    /// <b>EM-TFR — the films this mesh's cells can name.</b> Empty on a mesh with no film, on which
    /// <see cref="SheetClassOf"/> is the cell's level and every table is today's.
    /// </summary>
    public IReadOnlyList<PlanarMeshFilm> Films { get; init; } = [];

    /// <summary>
    /// <b>PMS-3 — the stacked regions' level sets this mesh's cells can name.</b> Empty unless
    /// <see cref="PlanarMeshSettings.StackedRegions"/> merged something, on which every table is today's.
    /// </summary>
    public IReadOnlyList<PlanarMeshStack> Stacks { get; init; } = [];

    /// <summary>
    /// <b>EM-TFR — the entry of a sheet table this cell reads its Z_s from.</b> The level's metal is
    /// the level's own index, so a mesh with no film has exactly today's table; film <c>k</c> follows
    /// the levels at <c>LayerNames.Count + k</c>.
    /// </summary>
    public int SheetClassOf(PlanarCell cell)
        => cell.Film >= 0 ? Math.Max(LayerNames.Count, 1) + cell.Film
         : cell.Stack >= 0 ? StackClassOf(cell.Stack)
         : cell.LayerIndex;

    /// <summary>The number of sheet classes: every level's metal, then every film, then every stack (PMS-3).</summary>
    public int SheetClassCount => Math.Max(LayerNames.Count, 1) + Films.Count + Stacks.Count;

    /// <summary>True when a sheet-class index names a film rather than a level's metal or a stack.</summary>
    public bool IsFilmClass(int sheetClass)
        => sheetClass >= Math.Max(LayerNames.Count, 1) && sheetClass < Math.Max(LayerNames.Count, 1) + Films.Count;

    /// <summary>PMS-3 — the sheet-class index of stack <paramref name="stack"/>: after every level and every film.</summary>
    public int StackClassOf(int stack) => Math.Max(LayerNames.Count, 1) + Films.Count + stack;

    /// <summary>The sheet resistance of a cell's own material, or null when it is the level's metal.</summary>
    public double? FilmSheetResistanceOf(PlanarCell cell)
        => cell.Film < 0 ? null : Films[cell.Film].SheetResistanceOhmPerSq;

    /// <summary>The size of the matrix the DENSE path holds and factors — the merged count when
    /// <see cref="Merge"/> is set, the basis count otherwise. What every dense ceiling is asked of.</summary>
    public int SolvedUnknownCount => Merge?.Count ?? Bases.Count;
}

/// <summary>
/// <b>EM-TFR — one film as the mesh carries it:</b> the level it sits on, the drawing layer a refusal
/// names, and its sheet resistance. On the mesh rather than only on the problem because a fill with no
/// loss model at all still owes the film its term: a film is a circuit element, not a loss correction.
/// </summary>
public sealed record PlanarMeshFilm(int LayerIndex, string Name, double SheetResistanceOhmPerSq);

/// <summary>
/// <b>PMS-3 — one stacked region's level set as the mesh carries it:</b> the via-joined levels
/// <see cref="LowerLayerIndex"/> … <see cref="UpperLayerIndex"/>, of which only <see cref="KeptLayerIndex"/> is meshed in
/// the region's interior. A cell naming it carries every one of those levels' sheets in parallel.
/// </summary>
public sealed record PlanarMeshStack(int LowerLayerIndex, int UpperLayerIndex, int KeptLayerIndex);

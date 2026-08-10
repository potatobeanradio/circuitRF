namespace CircuitRF.Engine.Mom;

/// <summary>
/// <b>Whether a boundary cell follows the GRID or the METAL.</b>
///
/// <para><b>This is the fourth user control, and D3 said there were exactly three.</b> The reversal
/// is on the owner's explicit instruction (brief-conformal-boundary-cells.md §5) and is recorded
/// rather than slipped in; D3's reasoning still stands for everything else and is not generally
/// relaxed. What earns this one is that it is not the same KIND of control as the three: cells per
/// wavelength and edge cells change how finely the same structure is discretised, while this changes
/// <i>which structure is discretised at all</i> — a staircased disc and a conformal disc are
/// different geometry, not two resolutions of one. That is a modelling decision, and modelling
/// decisions belong to the user.</para>
///
/// <para>It also needs an off switch on evidence rather than on taste: <b>every L8/L9 measurement in
/// this repository was taken on the staircase</b>, and a user reproducing one of them must be able
/// to.</para>
/// </summary>
public enum PlanarBoundaryCells
{
    /// <summary>L8b's rule as shipped: a cell is a whole grid rectangle, and a diagonal or curved
    /// outline is approximated by a staircase (D2). <b>The default</b>, and the model every measured
    /// number in this directory's <c>CLAUDE.md</c> was taken on.</summary>
    Staircase,

    /// <summary>A boundary cell is the grid rectangle intersected with the metal, so the union of the
    /// cells is the drawn polygon to round-off (R-cut-1). The interior of the mesh is unchanged and
    /// a Manhattan mesh is bit-identical (R-cut-2).</summary>
    Conformal,
}

/// <summary>
/// D3 — <b>exactly three user controls</b>, plus the fourth <see cref="PlanarBoundaryCells"/>
/// documents: <c>Auto</c> (default), <c>Cells per wavelength</c>, and <c>Edge mesh on/off + cell
/// count</c>. That is §10.5's own list, verbatim.
///
/// <para><b>Kernel A's <see cref="EmMeshSettings"/> has six, and the temptation is to mirror it. Do
/// not.</b> Its six exist because a boundary mesher over infinite dielectric interfaces has a
/// TRUNCATION problem (R-mom-10: truncation half-extent, tail cells) that a surface mesher over a
/// bounded piece of artwork does not have at all, and because a cross-section's edge cell is a
/// fraction of the metal THICKNESS, which a zero-thickness sheet does not have either. Everything
/// the user does not need to think about is auto-derived from the analysis — §10.5's own
/// instruction, and what makes §10.10's 30-second target reachable.</para>
/// </summary>
/// <param name="Auto">
/// When true the other three are ignored and the defaults below are used. This is the control the
/// user actually sees first, and leaving it on is meant to be the normal way to run.
/// </param>
/// <param name="CellsPerWavelength">§10.5's "20" in <c>max cell ≤ λ_g/20</c>.</param>
/// <param name="EdgeMesh">Graded cells at every conductor edge (R-msh-5).</param>
/// <param name="EdgeCells">How many, when on. §10.5 asks for 2–4.</param>
/// <param name="BoundaryCells">
/// <b>Conformal (cut) boundary cells — the fourth control, added as a TRAILING parameter</b> so every
/// existing positional construction is unchanged. <b>It ships OFF</b>; see
/// <see cref="PlanarBoundaryCells"/> for why it is a control at all, and this directory's
/// <c>CLAUDE.md</c> for the measurement that decides whether the default ever flips.
/// </param>
public sealed record PlanarMeshSettings(
    bool Auto               = true,
    int  CellsPerWavelength = 20,      // = DefaultCellsPerWavelength (a record's own const cannot
    bool EdgeMesh           = true,    //   be referenced from its primary-constructor defaults;
    int  EdgeCells          = 3,       //   DefaultsMatchLiterals pins the two together)
    PlanarBoundaryCells BoundaryCells = PlanarBoundaryCells.Staircase)
{
    public const int  DefaultCellsPerWavelength = 20;
    public const bool DefaultEdgeMesh           = true;
    public const int  DefaultEdgeCells          = 3;
    public const PlanarBoundaryCells DefaultBoundaryCells = PlanarBoundaryCells.Staircase;

    /// <summary>
    /// R-msh-4 — §10.5's "at least 3–5 cells across any conductor width". <b>Not a user control</b>
    /// (D3 permits three and this is not one of them): it is auto-derived, and 4 sits in the middle
    /// of the range the design note asks for.
    /// </summary>
    public const int MinCellsAcrossConductor = 4;

    /// <summary>
    /// R-msh-5 — the outermost edge cell as a fraction of the reference length, §10.5's own 2–5%.
    /// <b>Which length that fraction is OF is the measured question</b>; see
    /// <see cref="SurfaceMesher.EdgeReferenceLength"/>.
    /// </summary>
    public const double EdgeFractionOfReference = 0.03;

    /// <summary>Geometric growth ratio inward from an edge — §10.5's "~1.5–2".</summary>
    public const double EdgeGrowthRatio = 1.7;

    public static readonly PlanarMeshSettings Default = new();

    /// <summary>
    /// The settings actually used: <see cref="Auto"/> collapses to the defaults, so there is exactly
    /// one place the "what does Auto mean" question is answered and no code path downstream has to
    /// ask it again.
    ///
    /// <para><b><see cref="BoundaryCells"/> SURVIVES Auto, and that is a decision rather than an
    /// oversight.</b> <c>Auto = true</c> throwing the whole record away is the shape that already
    /// cost this area once — a fixture that sets a control and leaves Auto on then silently meshes
    /// the other way. The rule that resolves it is the same one that earned this control a place at
    /// all: Auto means "choose the RESOLUTION for me", and boundary cells are not a resolution. A
    /// user who asks for the metal to be followed is asking about the structure, and Auto has no
    /// opinion about the structure. Gated by <c>SurfaceMesherConformalTests</c>.</para>
    /// </summary>
    public PlanarMeshSettings Resolved => Auto
        ? new PlanarMeshSettings(Auto: false, BoundaryCells: BoundaryCells)
        : this with
        {
            CellsPerWavelength = Math.Max(2, CellsPerWavelength),
            EdgeCells          = Math.Max(0, EdgeCells),
        };
}

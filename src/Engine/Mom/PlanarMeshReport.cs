namespace CircuitRF.Engine.Mom;

/// <summary>R17's three outcomes. <see cref="Refused"/> means the mesh was built and counted but the
/// problem must not be solved — the count is the whole point of building it.</summary>
public enum PlanarBudgetVerdict
{
    Ok,
    Warn,
    Refused,
}

/// <summary>
/// R-msh-8 — <b>everything the panel shows, computed in the engine</b>, the same rule
/// <see cref="EmMeshReport"/> already follows: report it from the layer that knows, so the UI has
/// nothing to recompute and the two cannot disagree.
/// </summary>
/// <param name="Mesh">The cells, their order, and the rooftop pairs.</param>
/// <param name="CellCount">Rectangles. <b>Not N</b> — see <paramref name="UnknownCount"/>.</param>
/// <param name="UnknownCount">
/// R-msh-6 — <b>N, the number of BASIS FUNCTIONS L8c will produce, which is not the cell count.</b>
/// A rooftop spans a <i>pair</i> of adjacent cells, so N is the number of shared internal edges. On
/// a rectangular grid that is roughly 2× the cell count, and reporting cells while budgeting basis
/// functions would be a factor-of-two error in the one number this whole slice exists to produce.
/// <b>R17's ceiling is about this number</b>, because it is the matrix dimension: N unknowns cost
/// N² × 16 bytes.
/// </param>
/// <param name="CellsPerLayer">Parallel to <c>Mesh.LayerNames</c>.</param>
/// <param name="UnknownsPerLayer">Parallel to <c>Mesh.LayerNames</c>.</param>
/// <param name="MinCellEdgeM">Shortest cell edge anywhere, metres.</param>
/// <param name="MaxCellEdgeM">Longest cell edge anywhere, metres — the quantity λ_g/N caps.</param>
/// <param name="CellsAcrossNarrowestConductor">
/// R-msh-4, measured on the mesh that was actually produced: the fewest cells in any contiguous run
/// along a grid row or column. On Manhattan artwork this is exactly "cells across the narrowest
/// conductor". On staircased artwork a one-cell run can also come from the tip of a staircased
/// diagonal, which is honest information rather than a defect — the number is reported, never
/// silently enforced.
/// </param>
/// <param name="NarrowestConductorWidthM">The narrowest conductor dimension the mesher measured from
/// the geometry, before any grid existed — what the transverse cell size was derived from.</param>
/// <param name="FrequencyHz">
/// D4 — <b>the frequency λ_g was taken at, named in the report</b>, so a user who widens the sweep
/// and sees N change is not left guessing why.
/// </param>
/// <param name="GuidedWavelengthM">λ_g in the local dielectric at that frequency.</param>
/// <param name="MaxCellSizeM">The λ_g/N cap that produced <paramref name="MaxCellEdgeM"/>.</param>
/// <param name="EdgeReferenceLengthM">R-msh-5 — the length the edge cell is a fraction OF.</param>
/// <param name="StaircasedPolygons">How many input polygons were not Manhattan and are therefore
/// approximated by a staircase (D2). Zero means the mesh tiles its input exactly.</param>
/// <param name="ViaUnknownCount">L9c — how many of <paramref name="UnknownCount"/> are VERTICAL
/// (via) bases. They sit at the END of the unknown vector (R-via-5), so the horizontal unknowns are
/// <c>UnknownCount − ViaUnknownCount</c> and their indices are unchanged by adding a via.</param>
/// <param name="Verdict">R17.</param>
/// <param name="Refusal">Non-null only when <paramref name="Verdict"/> is
/// <see cref="PlanarBudgetVerdict.Refused"/>. Names the predicted N, the ceiling, and what to change.</param>
/// <param name="Notes">Human-readable remarks: the staircasing note, the R-msh-8a analytic-model
/// notes, the warning band, anything auto-derived a user would otherwise have to guess at.</param>
public sealed record PlanarMeshReport(
    PlanarMesh            Mesh,
    int                   CellCount,
    int                   UnknownCount,
    IReadOnlyList<int>    CellsPerLayer,
    IReadOnlyList<int>    UnknownsPerLayer,
    double                MinCellEdgeM,
    double                MaxCellEdgeM,
    int                   CellsAcrossNarrowestConductor,
    double                NarrowestConductorWidthM,
    double                FrequencyHz,
    double                GuidedWavelengthM,
    double                MaxCellSizeM,
    double                EdgeReferenceLengthM,
    int                   StaircasedPolygons,
    int                   ViaUnknownCount,
    PlanarBudgetVerdict   Verdict,
    string?               Refusal,
    IReadOnlyList<string> Notes)
{
    /// <summary>True when the problem may be handed to a solver — R17's gate, asked once.</summary>
    public bool CanSolve => Verdict != PlanarBudgetVerdict.Refused;
}

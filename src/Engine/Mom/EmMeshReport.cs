namespace CircuitRF.Engine.Mom;

/// <summary>
/// Everything the mesh viewer will later draw and everything §10.5's "report the unknown count
/// <i>before</i> solving" needs — <b>reported from the engine so the UI has nothing to
/// recompute</b>.
/// </summary>
/// <param name="Mesh">The segments themselves.</param>
/// <param name="UnknownCount">One unknown per segment.</param>
/// <param name="SegmentsPerConductor">Parallel to <c>Mesh.ConductorNames</c>.</param>
/// <param name="SegmentsPerInterface">Per dielectric interface, in bottom-to-top order.</param>
/// <param name="InterfaceYs">The y of each interface, bottom-to-top.</param>
/// <param name="MinCellLength">Smallest cell, metres.</param>
/// <param name="MaxCellLength">Largest cell, metres.</param>
/// <param name="TruncationHalfExtent">
/// How far each dielectric interface extends beyond the outermost conductor, metres — the R-mom-10
/// quantity a user has to be able to see in order to trust the answer.
/// </param>
/// <param name="WheelerValidAboveHz">
/// R-mom-13: per conductor, the frequency at which the skin depth equals half the metal thickness.
/// Below it Wheeler's incremental-inductance rule (which assumes δ ≪ t) is invalid and R is
/// carried by the DC floor instead. Surfaced so a user sweeping down into the invalid region is
/// <i>told</i> rather than quietly misled.
/// </param>
/// <param name="Template">Per-edge subdivision fractions, for the Wheeler perturbation re-mesh.</param>
/// <param name="Notes">Human-readable remarks — truncation extent, Wheeler crossover, dropped interfaces.</param>
public sealed record EmMeshReport(
    EmMesh                  Mesh,
    int                     UnknownCount,
    IReadOnlyList<int>      SegmentsPerConductor,
    IReadOnlyList<int>      SegmentsPerInterface,
    IReadOnlyList<double>   InterfaceYs,
    double                  MinCellLength,
    double                  MaxCellLength,
    double                  TruncationHalfExtent,
    IReadOnlyList<double>   WheelerValidAboveHz,
    ConductorMeshTemplate   Template,
    IReadOnlyList<string>   Notes);

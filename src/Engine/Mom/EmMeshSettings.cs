namespace CircuitRF.Engine.Mom;

/// <summary>
/// Boundary-mesh controls (§10.5). One dimension, not two: segments along conductor perimeters and
/// along dielectric interfaces.
/// </summary>
/// <param name="MinCellsAcrossWidth">§10.5 asks for "at least 3–5"; 6 buys margin cheaply.</param>
/// <param name="EdgeCells">Graded cells at every conductor edge (2–4).</param>
/// <param name="EdgeFractionOfWidth">Outermost cell, as a fraction of the conductor width (2–5%).</param>
/// <param name="EdgeGrowthRatio">Geometric growth ratio inward from an edge (1.5–2).</param>
/// <param name="TruncationHeights">
/// Dielectric-interface half-extent beyond the outermost conductor, in substrate heights.
/// <b>R-mom-10: never a hidden constant</b> — §10.3.1 names truncation as the one place kernel A
/// can be quietly wrong, so it is an explicit setting with its own convergence test.
/// </param>
/// <param name="TruncationTailCells">Graded cells in each truncated tail.</param>
public sealed record EmMeshSettings(
    int    MinCellsAcrossWidth = 6,
    int    EdgeCells           = 3,
    double EdgeFractionOfWidth = 0.03,
    double EdgeGrowthRatio     = 1.7,
    double TruncationHeights   = 20.0,
    int    TruncationTailCells = 12)
{
    public static readonly EmMeshSettings Default = new();

    /// <summary>A uniformly refined variant, for the mesh-convergence gate.</summary>
    public EmMeshSettings Refined(double factor) => this with
    {
        MinCellsAcrossWidth = (int)Math.Round(MinCellsAcrossWidth * factor),
        EdgeFractionOfWidth = EdgeFractionOfWidth / factor,
        TruncationTailCells = (int)Math.Round(TruncationTailCells * factor),
    };
}

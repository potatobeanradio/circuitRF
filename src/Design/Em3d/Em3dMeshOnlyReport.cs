namespace CircuitRF.Design.Em3d;

/// <summary>
/// brief-dfr35-1 R-dfr35-1-5 — what <c>em --mesh-only</c> found: the mesh a run would solve on.
/// </summary>
/// <param name="Elements">The tetrahedra in the mesh, counted from the file when it could be read.</param>
/// <param name="Estimated">The count estimated before meshing, or null.</param>
/// <param name="Reused">The script was unchanged, so the mesh already beside it was kept.</param>
/// <param name="RunDirectory">The run's own folder, holding the script, its log and the mesh.</param>
/// <param name="Warnings">Gmsh's distinct warnings, in its words.</param>
public sealed record Em3dMeshOnlyReport(long? Elements, long? Estimated, bool Reused, string RunDirectory, IReadOnlyList<string> Warnings);

/// <summary>brief-dfr36-1 R-dfr36-1-3 — a mesh on disk against the model as it is now.</summary>
public enum Em3dMeshCurrency
{
    /// <summary>A run would reuse it.</summary>
    Current,

    /// <summary>A run would mesh again.</summary>
    Stale,

    /// <summary>Only a run can tell: a thermal setup driven from a circuit lowers from that circuit's harmonic balance.</summary>
    Unknown,
}

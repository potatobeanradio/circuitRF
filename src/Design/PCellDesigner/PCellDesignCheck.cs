// Owner: PCD-5 (brief-pcd-0 §5, seam 7; written by PCD-1's seams commit). Framework-free.

using CircuitRF.Design.Schematic;

namespace CircuitRF.Design.PCellDesigner;

/// <summary>brief-pcd-0 R-pcd5-7: the design check of a designed cell.</summary>
public static class PCellDesignCheck
{
    /// <summary>
    /// Every finding about cellDir as a designed cell, ids "pcell.*": S1 (every part joined or offset-placed, every
    /// anchor resolves, every expression evaluates at the defaults and at each parameter's Minimum and Maximum),
    /// D-numbers, a parameter that is also a VAR, MSHAPE pins and Model, and what Export needs. schematic: the
    /// designer's in-memory one; null reads the cell's primary.
    /// </summary>
    /// <remarks>Seams-commit stub: no findings.</remarks>
    public static IReadOnlyList<CircuitRF.Diagnostics.Diagnostic> Run(string cellDir, SchematicEditModel? schematic = null)
        => [];
}

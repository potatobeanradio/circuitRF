// Owner: PCD-4 (brief-pcd-0 §5, seam 8; written by PCD-1's seams commit). Framework-free.

using CircuitRF.Design.Schematic;

namespace CircuitRF.Design.PCellDesigner;

public sealed record CellNetlistMergeResult(SchematicEditModel? Schematic, IReadOnlyList<string> Refusals,
                                            IReadOnlyList<string> Notes);

/// <summary>brief-pcd-0 R-pcd4-1: the designer's text view merged back into its schematic.</summary>
public static class CellNetlistMerge
{
    /// <summary>cnlText merged into a copy of current by designator (D-drawing). Never mutates current. Schematic null
    /// when refused.</summary>
    public static CellNetlistMergeResult Merge(SchematicEditModel current, string cnlText)
        => throw new NotImplementedException("PCD-4");
}

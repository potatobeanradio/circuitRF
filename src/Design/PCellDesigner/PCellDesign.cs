// Owner: PCD-5 (brief-pcd-0 §5, seam 4; written by PCD-1's seams commit). Framework-free.

using CircuitRF.Design.Cells;

namespace CircuitRF.Design.PCellDesigner;

/// <summary>brief-pcd-0 D-cell: what makes a cell a designed cell, and how one is made.</summary>
public static class PCellDesign
{
    public const string GeneratorPrefix      = "cpcell:";
    public const string PackageExtension     = ".cpcell";
    public const string ManifestFileName     = "manifest.json";
    public const string PackageFormat        = "circuitrf.pcell";
    public const int    PackageFormatVersion = 1;

    public static string GeneratorIdFor(string cellName) => GeneratorPrefix + cellName;

    public static bool IsDesigned(string? cellDir) => CellFolder.ReadCcell(cellDir)?.PCellDesign is not null;

    /// <summary>
    /// A new designed cell under parentDir: CellCreate.Create with schematic and symbol views, then the .ccell's
    /// PCellDesign, LayoutGenerator = GeneratorIdFor(cellName) and EmCopper = true. What Tools ▸ PCell Designer… and
    /// <c>pcell new</c> both call.
    /// </summary>
    public static CellCreateResult Create(string parentDir, string cellName)
        => throw new NotImplementedException("PCD-5");
}

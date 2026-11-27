// Owner: PCD-5 (brief-pcd-0 §5, seam 6; written by PCD-1's seams commit). Framework-free.

namespace CircuitRF.Design.PCellDesigner;

/// <summary>brief-pcd-0 §4.4: the <c>manifest.json</c> of a <c>.cpcell</c>.</summary>
public sealed record PCellPackageManifest
{
    public string Format { get; init; } = PCellDesign.PackageFormat;
    public int    FormatVersion { get; init; } = PCellDesign.PackageFormatVersion;
    public string Name { get; init; } = "";
    public string GeneratorId { get; init; } = "";
    public string ContentKey { get; init; } = "";
    public IReadOnlyList<string> Generators { get; init; } = [];
    public IReadOnlyList<string> Layers { get; init; } = [];
    public string WrittenBy { get; init; } = "";
}

/// <summary>Path: the package written (Export) or the cell folder imported (Import); null when refused, the refusal an
/// Error in Findings.</summary>
public sealed record PCellPackageOutcome(string? Path, PCellPackageManifest? Manifest,
                                         IReadOnlyList<CircuitRF.Diagnostics.Diagnostic> Findings);

/// <summary>brief-pcd-0 §4.4: export and import of a designed cell as a <c>.cpcell</c>.</summary>
public static class PCellPackage
{
    /// <summary>Runs <see cref="PCellDesignCheck"/> first.</summary>
    public static PCellPackageOutcome Export(string cellDir, string packagePath)
        => throw new NotImplementedException("PCD-5");

    public static PCellPackageOutcome Import(string packagePath, string libraryDir)
        => throw new NotImplementedException("PCD-5");

    public static PCellPackageManifest? ReadManifest(string packagePath, out string? refusal)
        => throw new NotImplementedException("PCD-5");
}

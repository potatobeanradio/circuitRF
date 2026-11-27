// Owner: PCD-5 (brief-pcd-0 §5, seam 5; written by PCD-1's seams commit). Framework-free.

using CircuitRF.Design.Layout.PCells;
using CircuitRF.Design.Layout.PCells.Wire;

namespace CircuitRF.Design.PCellDesigner;

/// <summary>brief-pcd-0 S3: resolves <c>"cpcell:&lt;Name&gt;"</c> to the designed cell of that name.</summary>
public sealed class DesignedPCellResolver : IPCellGeneratorResolver
{
    /// <summary>
    /// Makes every designed cell under workspaceRootDir (and the folders its .cws references: LibraryRefs,
    /// ReferencedWorkspaces) resolvable as "cpcell:&lt;Name&gt;" until disposed. Reference-counted per root: nested calls
    /// are free. Registration starts nothing; the folders are read on the first Resolve.
    /// </summary>
    /// <remarks>Seams-commit stub: registers nothing and returns a no-op.</remarks>
    public static IDisposable Register(string workspaceRootDir) => NoOp.Instance;

    /// <summary>The cell folder a registered "cpcell:&lt;Name&gt;" id names, or null. Stub: null.</summary>
    public static string? CellDirOf(string generatorId) => null;

    public PCellGenerator? Resolve(string generatorId) => null;

    public IReadOnlyCollection<string> KnownGeneratorIds => [];

    public string Describe() => "designed cells (PCell Designer)";

    public IReadOnlyDictionary<string, PCellValue>? DeclaredDefaults(string generatorId) => null;

    public IReadOnlyList<PCellParameterInfo>? DeclaredParameters(string generatorId) => null;

    public string? ContentKeyFor(string generatorId) => null;

    private sealed class NoOp : IDisposable
    {
        public static readonly NoOp Instance = new();
        public void Dispose() { }
    }
}

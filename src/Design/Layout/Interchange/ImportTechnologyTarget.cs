// Which technology an artwork import lands in (docs/sonnet-briefs/brief-gerber-import-target-technology.md
// R-gt-1, R-gt-2). Framework-free: the dialog that asks the question lives in src/Ui, and `convert
// --into-tech` answers it with a flag.
//
// Made for the Gerber import and named after it until the board-file import took the same choice (issue #34):
// both import a whole board's drawing layers, so both land in a technology of their own beside the cells unless
// told to use an existing one, and neither ever modifies a technology other cells share.

namespace CircuitRF.Design.Layout.Interchange;

/// <summary>
/// R-gt-1 — <b>New</b> (a technology of the import's own, beside the cell: what every Gerber import has
/// always done) or <b>Use</b> an existing technology file, which the <c>.clay</c> then references and
/// which the import never modifies (D5).
/// </summary>
public sealed record ImportTechnologyTarget
{
    /// <summary>The full path of the technology used, or null for <see cref="New"/>.</summary>
    public string? UsePath { get; }

    private ImportTechnologyTarget(string? usePath) => UsePath = usePath;

    /// <summary>A technology of the import's own — the default, and railRF's always.</summary>
    public static ImportTechnologyTarget New { get; } = new((string?)null);

    /// <summary>Import against <paramref name="technologyPath"/>, reference it, and write none.</summary>
    public static ImportTechnologyTarget Use(string technologyPath) => new(Path.GetFullPath(technologyPath));

    public bool IsUse => UsePath is not null;
}

/// <summary>
/// R-gt-2 — what the import hands its mapping dialog: the rows as proposed, how many copper files
/// the set holds (what D3's default and D4's disabled entries are decided by), and a way to propose
/// the rows again against another technology <b>without reading any file again</b>.
/// </summary>
/// <param name="Rows">The rows as proposed against <paramref name="Target"/>'s technology (New: the
/// workspace's, as a donor).</param>
/// <param name="CopperCount">The copper files the identity cascade found.</param>
/// <param name="Target">What <paramref name="Rows"/> were proposed for.</param>
/// <param name="Repropose">The rows for USING that technology; null proposes them for New.</param>
public sealed record ImportMappingRequest(
    IReadOnlyList<LayerMappingRow> Rows,
    int CopperCount,
    ImportTechnologyTarget Target,
    Func<Technology?, IReadOnlyList<LayerMappingRow>> Repropose);

/// <summary>The dialog's answer: the settled rows, and the technology they were settled against.</summary>
public sealed record ImportMappingAnswer(IReadOnlyList<LayerMappingRow> Rows, ImportTechnologyTarget Target);

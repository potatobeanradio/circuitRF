using CircuitRF.Design.Layout.Interchange;

namespace CircuitRF.Ui.RailRf;

/// <summary>
/// What <c>RailImportDialog</c> settled — everything the import needs and nothing it can work out.
/// </summary>
/// <remarks>
/// <b>Framework-free on purpose.</b> The dialog is a view; this is what it produced, and it is what
/// the view model and the tests both handle. R-rail7-6 and R-rail7-7 are both properties OF THIS
/// RECORD rather than of the XAML — the checkbox default, the unanswered origin — so both are
/// testable without an application host.
/// </remarks>
public sealed record RailImportOptions
{
    /// <summary>The artwork: a Gerber set's folder, one Gerber file, or a <c>.kicad_pcb</c>.</summary>
    public string ArtworkPath { get; init; } = "";

    /// <summary>
    /// <b>ON by default, and the default is the owner's rev-3 decision</b> (§2.3 step 1).
    ///
    /// <para>Someone will eventually wonder why the default is not the cheap path, so the consequences
    /// are listed here rather than left to be rediscovered: with this on, the artwork lands in the open
    /// workspace as an ordinary circuitRF cell with a layout view, which means it is <b>saved with the
    /// design</b> rather than re-imported every session, it <b>opens in the layout editor</b>, <b>DRC
    /// runs on it</b>, <c>circuitrf render</c> <b>draws it headlessly</b>, <b>revision control keeps
    /// it</b>, and the <c>.crail</c> holds a <b>reference</b> to that cell
    /// (<see cref="CircuitRF.Design.RailRf.RailDocument.ArtworkCellRef"/>) rather than a private copy
    /// of the geometry. Unchecking it gives the throwaway behaviour, for a quick look and nothing
    /// else.</para></summary>
    public bool LandInWorkspace { get; init; } = true;

    /// <summary>The placement file, or null.</summary>
    public string? PlacementPath { get; init; }

    /// <summary>
    /// <b>Nothing pre-selected, and a null here is a REFUSAL rather than a default</b> (Q-14,
    /// R-rail7-7). There is no house convention to learn and a default here is the guess the refusal
    /// exists to prevent: three quarters of a millimetre on an 0402 is the difference between landing
    /// on the part's own pad and landing on its neighbour's.
    ///
    /// <para>Null is also the ordinary state where <see cref="PlacementPath"/> is null (nothing to ask
    /// about) or where the file DECLARES its own origin — <see cref="PlacementTable.OriginEvidence"/>
    /// settles that, and the dialog only asks where it comes back
    /// <see cref="PlacementOriginEvidence.Unstated"/>.</para></summary>
    public PlacementOrigin? PlacementOrigin { get; init; }

    /// <summary>The BOM, or null.</summary>
    public string? BomPath { get; init; }

    /// <summary>The board netlist, or null.</summary>
    public string? BoardNetlistPath { get; init; }

    /// <summary>The part library (<c>.crlib</c>), or null.</summary>
    public string? PartLibraryPath { get; init; }

    /// <summary>
    /// Where the cell lands when <see cref="LandInWorkspace"/> is set: the open workspace's own
    /// folder. Null means there is no workspace open — which is an OFFER to create one
    /// (<c>WorkspaceCreate.Create</c>, the same function the GUI's own New Workspace command calls)
    /// rather than a silent fall back to the throwaway path.
    /// </summary>
    public string? WorkspaceDir { get; init; }

    /// <summary>
    /// True when this import cannot proceed as stated because the placement origin was not answered.
    /// <see cref="OriginRefusal"/> is the sentence.
    /// </summary>
    public bool NeedsPlacementOrigin => PlacementPath is { Length: > 0 } && PlacementOrigin is null;

    /// <summary>
    /// True when nothing has been named for the artwork yet (R-rail22-1a). <b>Nothing is
    /// pre-selected</b>, because neither answer is the common one: a Gerber set is a FOLDER of files
    /// that belong together, and a <c>.kicad_pcb</c> is one file.
    /// </summary>
    public bool NeedsArtwork => ArtworkPath is not { Length: > 0 };

    /// <summary>The sentence for <see cref="NeedsArtwork"/>, naming both buttons that answer it.</summary>
    public const string ArtworkRefusal =
        "Nothing is chosen for the artwork. A Gerber set is a folder of files that belong together, "
      + "so Folder… is the usual answer; File… is for a single Gerber, a drill file or a "
      + ".kicad_pcb. railRF does not pre-select one — what a file IS is settled by its content, not "
      + "by which button opened it.";

    /// <summary>
    /// Why a <c>.pdf</c> is not a bill of materials railRF will read, or null for anything else.
    /// </summary>
    /// <remarks>
    /// <b>A refusal rather than a reader, and the same class of decision as the Excellon suppression
    /// question</b> (R-rail22-3). A PDF bill of materials is a RENDERING of a table rather than a
    /// table: column boundaries would have to be inferred from glyph positions, a wrapped cell is
    /// indistinguishable from two rows, and a reference list that spans a line break silently loses
    /// members. The failure is quiet and plausible — a BOM read with nine of thirteen parts produces
    /// a completely believable railRF answer for the wrong board — so railRF does not guess, exactly
    /// as <c>convert</c> does not guess leading versus trailing suppression.
    ///
    /// <para><b>The sentence names the file the user already has.</b> The same tool that printed the
    /// PDF exports the table itself, and someone reaching for the PDF usually has the CSV beside it
    /// and does not know it is the one to point at. <c>XLSX is deliberately NOT offered</c>: this
    /// reader parses delimited text (<c>DelimitedTables.Parse</c>) and nothing in circuitRF opens a
    /// workbook, so naming it would be a second wrong file to try.</para>
    /// </remarks>
    public static string? BomRefusal(string? bomPath)
    {
        if (bomPath is not { Length: > 0 }) return null;
        if (!bomPath.EndsWith(".pdf", System.StringComparison.OrdinalIgnoreCase)) return null;

        return $"\u201c{System.IO.Path.GetFileName(bomPath)}\u201d is a PDF, which is a PICTURE of a "
             + "bill of materials rather than the table itself: its columns would have to be guessed "
             + "from where the glyphs landed, and a reference list that wraps across a line break "
             + "would lose members silently — nine parts of thirteen reads as a perfectly believable "
             + "board. railRF does not guess it. Supply the CSV or tab-separated export the same tool "
             + "produces; grouped cells such as \u201cC3, C5, C7\u201d and \u201cC1-C9\u201d are "
             + "expanded, and what could not be is reported.";
    }

    /// <summary>True when the bill of materials named here is one railRF refuses to read.</summary>
    public bool NeedsReadableBom => BomRefusal(BomPath) is not null;

    /// <summary>
    /// The sentence R-rail7-4 puts in the status strip, <b>with the row count in it</b> — the one
    /// number an import dialog has before it can ask the question at all, which is why
    /// <see cref="PlacementTable.ParsedRowCount"/> is filled even on a refusal.
    /// </summary>
    /// <param name="parsedRowCount">How many placement rows were read.</param>
    public static string OriginRefusal(string fileName, int parsedRowCount) =>
        $"{fileName} holds {parsedRowCount} placement row(s) and does not state its coordinate " +
        "origin; pass --origin or set it here. It is a choice made at export — the footprint's symbol " +
        "origin, the part body's centre, or pin 1 — and three quarters of a millimetre on an 0402 is " +
        "the difference between landing on the part's own pad and landing on its neighbour's, so " +
        "railRF does not guess it.";
}

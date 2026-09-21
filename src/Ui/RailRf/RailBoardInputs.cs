using System.Collections.Generic;
using CircuitRF.Design.Layout;
using CircuitRF.Design.RailRf;
using CircuitRF.Design.Layout.Pdn;
using Clipper2Lib;

namespace CircuitRF.Ui.RailRf;

/// <summary>
/// The board-level inputs one railRF window is working over — everything a <see cref="RailDcRequest"/>
/// needs that is not the document itself.
/// </summary>
/// <remarks>
/// <b>Stated once, and the rail is what varies.</b> That is <see cref="RailDcRequest"/>'s own shape
/// and this mirrors it deliberately: the import fills this in once, the rail selector moves, and
/// nothing re-supplies the artwork per rail.
///
/// <para><b>It is also the seam that keeps this view model testable.</b> A window with no board is an
/// ordinary state — Tools ▸ railRF opens one — and it is the state that says "import a board" rather
/// than the state that crashes. A test drives the Fast loop by supplying a board and a stub solve;
/// neither needs an application host.</para>
/// </remarks>
public sealed record RailBoardInputs
{
    /// <summary>The artwork, flattened to shapes in DBU.</summary>
    public required IReadOnlyList<LayoutShape> Shapes { get; init; }

    /// <summary>
    /// The layout this artwork came from, where railRF is looking at a <c>.clay</c> somebody can have
    /// open — so the board panel can show it LIVE rather than as a snapshot.
    /// </summary>
    /// <remarks>
    /// <b>The same object the layout session holds, deliberately</b> — the board shown here is meant
    /// to be a live view of the <c>.clay</c>, not a snapshot of it (owner, 2026-09-18). An edit in
    /// that document mutates this <see cref="LayoutView"/> and raises its <c>Changed</c> event, which
    /// is what the railRF window repaints on. Nothing is copied and nothing is polled.
    ///
    /// <para><b>Set by every path that read a <c>.clay</c></b> — the import, the bare-layout open and
    /// the <c>.crail</c> open — and swapped for the SESSION's object when one turns out to be open on
    /// the same file. It was set only by that swap until 2026-09-20, which made it null on every
    /// ordinary open, and <see cref="LengthFormat"/> then fell back to raw DBU: the whole window read
    /// in database units, and a rail made by clicking the pour carried one in its NAME, in the saved
    /// document. Null only where nothing read a layout at all.</para>
    ///
    /// <para><see cref="Shapes"/> is still the artwork either way, and it is what the EXTRACTION
    /// reads. This is what the picture is drawn from, and what the units come off.</para>
    /// </remarks>
    public LayoutView? View { get; init; }

    /// <summary>The stackup.</summary>
    public required Technology Technology { get; init; }

    /// <summary>
    /// The <c>.ctech</c> the stackup was read from, absolute, or null where the resolution produced no
    /// path (an in-memory technology — the import path's own).
    /// </summary>
    /// <remarks>
    /// Carried so the window can OPEN it (owner, 2026-09-19: the layer whose <c>Vis</c> needs turning
    /// off is in that file, and hunting for it in the project tree is a detour out of the window the
    /// question was asked in). Nothing here reads the file; this is the address.
    /// </remarks>
    public string? TechPath { get; init; }

    /// <summary>The artwork's DBU resolution.</summary>
    public int DbuPerMicron { get; init; } = LayoutUnits.DefaultDbuPerMicron;

    /// <summary>
    /// The unit every coordinate and every length off this board READS in — the layout's own display
    /// unit. Every coordinate readout and every result on this window reads in the board file's own
    /// units rather than in DBU (owner, 2026-09-18).
    /// </summary>
    /// <remarks>
    /// Taken from <see cref="View"/> where there is one, so it is the unit the layout editor is
    /// showing the very same artwork in — two windows on one board disagreeing about its units would
    /// be worse than either choice. <see cref="RailLengthFormat.Dbu"/> with no artwork, which prints
    /// the integer and says "DBU" rather than picking a unit nobody stated.
    /// </remarks>
    public RailLengthFormat LengthFormat =>
        View is { } v ? RailLengthFormat.For(v) : RailLengthFormat.Dbu;

    /// <summary>The board's pads, as the netlist or a placement join knows them.</summary>
    public IReadOnlyList<PdnPad> Pads { get; init; } = [];

    /// <summary>What the board netlist knows about net identity.</summary>
    public IReadOnlyList<PdnNetPoint> NetPoints { get; init; } = [];

    /// <summary>The reference return's net, where one is named.</summary>
    public string? ReferenceNet { get; init; }

    /// <summary>
    /// Every net name this board RESOLVED, from all three claims together — the board netlist, the
    /// schematic beside the artwork, and whatever a user stamped on the copper (R-ab2-4a).
    /// </summary>
    /// <remarks>
    /// <b>The pick list is built from here and not from <c>BoardNetlist.Nets</c> alone.</b> A board
    /// somebody DREW has always had net names and the window has never offered them — and then told
    /// the user, in a sentence that nothing fails when it is wrong, that no board netlist had named
    /// any and to click the pour instead.
    /// </remarks>
    public IReadOnlyList<string> Nets { get; init; } = [];

    /// <summary>Which of the three claims named them — what the strip says (R-ab2-4d).</summary>
    public PdnNetOrigin NetOrigin { get; init; } = PdnNetOrigin.None;

    /// <summary>The board outline. Required by <c>FilledToOutline</c>.</summary>
    public Paths64? BoardOutline { get; init; }

    /// <summary>What bridges the gaps the copper leaves at every pad.</summary>
    public IReadOnlyList<PdnSeriesElement> SeriesElements { get; init; } = [];

    /// <summary>The decoupling. In the netlist, carrying no DC path.</summary>
    public IReadOnlyList<PdnShuntPart> ShuntParts { get; init; } = [];

    /// <summary>The cell the artwork lives in, as the <c>.crail</c> refers to it, or null for the
    /// throwaway path. Shown on the window so a user can tell the two apart at a glance.</summary>
    public string? ArtworkCellRef { get; init; }
}

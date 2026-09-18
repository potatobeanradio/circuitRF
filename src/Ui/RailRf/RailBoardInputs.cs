using System.Collections.Generic;
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

    /// <summary>The stackup.</summary>
    public required Technology Technology { get; init; }

    /// <summary>The artwork's DBU resolution.</summary>
    public int DbuPerMicron { get; init; } = LayoutUnits.DefaultDbuPerMicron;

    /// <summary>The board's pads, as the netlist or a placement join knows them.</summary>
    public IReadOnlyList<PdnPad> Pads { get; init; } = [];

    /// <summary>What the board netlist knows about net identity.</summary>
    public IReadOnlyList<PdnNetPoint> NetPoints { get; init; } = [];

    /// <summary>The reference return's net, where one is named.</summary>
    public string? ReferenceNet { get; init; }

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

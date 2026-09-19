// Step 1 and step 2 of §2.3 — the import, the rail picking, and the regulator offer
// (brief-railrf-7-window.md R-rail7-6, R-rail7-7, R-rail7-8).

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.Layout.Pdn;
using CircuitRF.Design.RailRf;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.RailRf;

/// <summary>
/// A rail railRF is OFFERING to create, because a regulator in the BOM makes a second voltage.
/// </summary>
/// <param name="RailName">What the offered rail would be called — the regulator's output net.</param>
/// <param name="Refdes">The part that makes it: a load on the rail above, a source on the new one.</param>
/// <param name="UpstreamRail">The rail it would be a load on.</param>
public sealed record RailRegulatorOffer(string RailName, string Refdes, string UpstreamRail)
{
    /// <summary>What the offer reads on the window.</summary>
    public string Summary =>
        $"{Refdes} makes '{RailName}'. Add it as the next rail, and {Refdes} as a load on " +
        $"'{UpstreamRail}'?";
}

public sealed partial class RailRfViewModel
{
    // ── Step 1: the import (§2.3, R-rail7-6 / R-rail7-7) ───────────────────────────────────────

    /// <summary>
    /// Applies what the import dialog settled, having already read the companion files.
    /// </summary>
    /// <remarks>
    /// <b>The artwork import itself is not here.</b> That is <c>GerberImportEntry.Run</c> — the same
    /// funnel File ▸ Import ▸ Gerber goes through, including its layer-mapping and drill-format
    /// prompts — and the window calls it rather than re-implementing it, on exactly the terms
    /// <c>Authoring.cs</c> states for the headless verbs: an operation that exists twice diverges
    /// silently. What lands here is the RESULT: the cell the artwork went into, and the three
    /// companion tables.
    /// </remarks>
    /// <param name="options">What the dialog settled.</param>
    /// <param name="board">The artwork, resolved.</param>
    /// <param name="placement">The placement table, or null.</param>
    /// <param name="bom">The BOM, or null.</param>
    /// <param name="netlist">The board netlist, or null.</param>
    /// <param name="library">The part library, or null.</param>
    public void ApplyImport(
        RailImportOptions options,
        RailBoardInputs board,
        PlacementTable? placement = null,
        BomTable? bom = null,
        BoardNetlist? netlist = null,
        PartLibrary? library = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(board);

        _document.ArtworkCellRef = board.ArtworkCellRef;

        // ── THE COMPANIONS ARE RECORDED ON THE DOCUMENT, NOT ONLY ON THE SESSION ────────────────
        //
        // The netlist and the placement are what make a REFDES resolve to copper, and until they
        // were persisted an import that read them and was then SAVED lost them: the reopened
        // document had no pads, so every anchor fell back to a coordinate and every mounting loop
        // fell back to its typed value, with nothing on any report to say a computed set had been
        // available. Recorded here for the same reason ArtworkCellRef is — the document holds a
        // reference to the file, never a copy of what was in it.
        _document.BoardNetlistRef = options.BoardNetlistPath;
        _document.PlacementRef    = options.PlacementPath;

        Board = board with
        {
            Pads         = PdnBoardPads.PadsOf(netlist),
            NetPoints    = PdnBoardPads.NetPointsOf(netlist),
            ReferenceNet = _document.ReferenceNet,
        };
        Placement = placement;
        Bom = bom;
        PartLibrary = library;
        BoardNetlist = netlist;

        // R-rail7-7: an unanswered origin is a refusal that SURVIVES the dialog closing, because it
        // stays true until the import is redone. It names the control that answers it and it carries
        // the row count, which is the number that makes it actionable.
        PendingImportRefusal =
            placement is { Refusal: null, OriginEvidence: PlacementOriginEvidence.Unstated }
                ? new RailRefusal(
                    RailImportOptions.OriginRefusal(placement.FileName, placement.ParsedRowCount),
                    RailRefusalControl.PlacementOrigin)
                : placement is { Refusal: { } why }
                    ? RailRefusals.Classify(why)
                    : null;

        RebuildAvailableNets();
        RebuildRegulatorOffers();
        RefreshRunGate();
    }

    /// <summary>The board netlist this document was imported with, or null.</summary>
    [ObservableProperty]
    private BoardNetlist? _boardNetlist;

    // ── Step 2: picking the rail (§2.3, R-rail7-8) ─────────────────────────────────────────────

    /// <summary>
    /// Every net a board file or a board netlist named — what the pick list offers.
    /// </summary>
    /// <remarks>
    /// <b>Empty is not an error.</b> On the assisted-Gerber path there is no netlist and geometry
    /// alone has no net in it, so the pick is made by CLICKING THE POUR instead —
    /// <see cref="PickRailAt"/>. Both routes end in the same place: a rail, whose copper
    /// <c>PdnRailRegions</c> walks and brief 8 highlights, so a user sees straight away whether it is
    /// one region or three islands joined by a 20 mil neck.
    /// </remarks>
    public ObservableCollection<string> AvailableNets { get; } = [];

    private void RebuildAvailableNets()
    {
        AvailableNets.Clear();
        if (BoardNetlist is { Refusal: null } n)
            foreach (string net in n.Nets.OrderBy(x => x, StringComparer.Ordinal)) AvailableNets.Add(net);

        SelectedNet = null;
        OnPropertyChanged(nameof(HasPickableNets));
        OnPropertyChanged(nameof(HasNoPickableNets));
    }

    /// <summary>The net highlighted in the pick list, or null.</summary>
    [ObservableProperty]
    private string? _selectedNet;

    /// <summary>True while there is a list to pick from — a board file or a board netlist named the
    /// nets.</summary>
    public bool HasPickableNets => AvailableNets.Count > 0;

    /// <summary>True once a board is loaded and NOTHING named a net, which is the assisted-Gerber
    /// path: the pick is made by clicking the pour instead, and the pane says so rather than showing
    /// an empty list.</summary>
    public bool HasNoPickableNets => Board is not null && AvailableNets.Count == 0;

    /// <summary>Makes the highlighted net a rail — the list route of §2.3 step 2.</summary>
    [RelayCommand]
    private void PickSelectedNet()
    {
        if (SelectedNet is { Length: > 0 } net) PickRail(net);
    }

    /// <summary>
    /// Makes <paramref name="net"/> a rail of this document, or selects the one it already is.
    /// </summary>
    /// <remarks>
    /// The rail takes the net's own name, because that is what the user picked and what every refusal
    /// about the solve order will call it. <b>It states no reference layer</b> — that is the next step
    /// and it is confirmed, never assumed.
    /// </remarks>
    public RailSpec PickRail(string net)
    {
        if (_document.Rail(net) is { } existing)
        {
            SelectedRailName = existing.Name;
            return existing;
        }

        var rail = new RailSpec { Name = net, NetName = net };
        _document.Rails.Add(rail);
        RebuildRails();
        SelectedRailName = rail.Name;
        RebuildRegulatorOffers();
        return rail;
    }

    /// <summary>
    /// The pour-clicking route: a rail with no net name, anchored by a coordinate on the copper.
    /// </summary>
    /// <remarks>
    /// <b>railRF will not guess a net name</b> (brief 1), so a rail made this way carries none — it
    /// carries a source anchored at the point instead, which is what <c>PdnRailRegions.Walk</c> seeds
    /// the connectivity walk from. The rail is NAMED after where it was picked rather than left
    /// unnamed, because every rail is named: the selector shows it and a refusal about the order has
    /// to say which two rails it is about.
    /// </remarks>
    public RailSpec PickRailAt(long xDbu, long yDbu, string? name = null)
    {
        string railName = name is { Length: > 0 } n ? n : $"rail at ({xDbu}, {yDbu})";

        var rail = new RailSpec { Name = railName };
        rail.Sources.Add(new RailSource { Anchor = new RailPortAnchor { Point = (xDbu, yDbu) } });

        _document.Rails.Add(rail);
        RebuildRails();
        SelectedRailName = rail.Name;
        return rail;
    }

    // ── Step 2's second half: the regulator offer (§2.3) ───────────────────────────────────────

    /// <summary>
    /// The rails railRF is offering to create, because a regulator was recognised from the BOM.
    /// </summary>
    /// <remarks>
    /// <b>Offered, not created.</b> §2.3 is explicit: <i>the rail chain is a modelling statement and
    /// it is the user's</i>. railRF can see that a part is a regulator; it cannot see which of its
    /// pins the design treats as an input on WHICH rail, and a chain created without being asked is a
    /// dependency the user never stated appearing in a solve order they did not write.
    /// </remarks>
    public ObservableCollection<RailRegulatorOffer> RegulatorOffers { get; } = [];

    private void RebuildRegulatorOffers()
    {
        RegulatorOffers.Clear();
        OnPropertyChanged(nameof(HasRegulatorOffers));

        if (Bom is not { Refusal: null } bom || SelectedRail is not { } rail) return;

        // A part is offered where it is ALREADY a load on this rail (so the input side is stated) and
        // is not yet a source on any rail (so the output side is not). That is exactly the shape
        // RailOrder reads — nothing else in the model links two rails — which is why the offer can be
        // computed from the document rather than from a notion of what a regulator is.
        var sourcing = new HashSet<string>(
            _document.Rails.SelectMany(r => r.Sources)
                           .Select(s => s.Anchor.Refdes)
                           .OfType<string>(),
            StringComparer.OrdinalIgnoreCase);

        foreach (var load in rail.Loads)
        {
            if (load.Anchor.Refdes is not { Length: > 0 } refdes) continue;
            if (sourcing.Contains(refdes)) continue;
            if (!IsRegulator(bom, refdes)) continue;

            string offered = OutputNetFor(refdes) ?? $"{refdes} output";
            if (_document.Rail(offered) is not null) continue;

            RegulatorOffers.Add(new RailRegulatorOffer(offered, refdes, rail.Name));
        }

        OnPropertyChanged(nameof(HasRegulatorOffers));
    }

    /// <summary>True while railRF has a next rail to offer. Bound rather than a count, because an
    /// int is not a bool and an empty card is a card the user still has to read past.</summary>
    public bool HasRegulatorOffers => RegulatorOffers.Count > 0;

    /// <summary>Accepts one offer: the new rail, and the row that makes the regulator a source on
    /// it. The load row on the rail above is already there — it is what produced the offer.</summary>
    [RelayCommand]
    private void AcceptRegulatorOffer(RailRegulatorOffer? offer)
    {
        if (offer is null || _document.Rail(offer.RailName) is not null) return;

        var rail = new RailSpec { Name = offer.RailName, NetName = offer.RailName };
        rail.Sources.Add(new RailSource { Anchor = new RailPortAnchor { Refdes = offer.Refdes } });

        _document.Rails.Add(rail);
        RebuildRails();
        SelectedRailName = rail.Name;
    }

    /// <summary>
    /// Whether the BOM describes <paramref name="refdes"/> as something that makes a voltage.
    /// </summary>
    /// <remarks>
    /// Read off the BOM's own description text, and deliberately generous: this produces an OFFER a
    /// user says yes or no to, so a false positive costs a declined offer and a false negative costs
    /// a rail the user has to make by hand. Neither creates anything.
    /// </remarks>
    private static bool IsRegulator(BomTable bom, string refdes)
    {
        foreach (var row in bom.RowsFor(refdes))
        {
            string text = $"{row.Description} {row.Value} {row.PartNumber}";
            if (text.Contains("regulator", StringComparison.OrdinalIgnoreCase)
                || text.Contains("ldo", StringComparison.OrdinalIgnoreCase)
                || text.Contains("converter", StringComparison.OrdinalIgnoreCase)
                || text.Contains("buck", StringComparison.OrdinalIgnoreCase)
                || text.Contains("boost", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// The net a regulator's output pin sits on, where the board netlist names one.
    /// </summary>
    /// <remarks>
    /// Null where there is no netlist, which is the assisted-Gerber path: the offer is still made and
    /// the rail is named after the part, because an offer a user can rename beats no offer at all.
    /// </remarks>
    private string? OutputNetFor(string refdes)
    {
        if (BoardNetlist is not { Refusal: null }) return null;

        // Nothing in a board netlist says which pin is an output, so this does not pretend to know.
        // The rail is named by the user or by the part; what the netlist buys is the NET LIST the
        // user picks from, which is AvailableNets and is already offered.
        _ = refdes;
        return null;
    }
}

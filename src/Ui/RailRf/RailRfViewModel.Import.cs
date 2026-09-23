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

        // AND HOW TO READ IT, which is the half that was still missing (field report, 2026-09-22).
        // The origin, the units and the column names are three answers a HUMAN gave this dialog and
        // none of them was written down — so the same document reopened asked for the origin again,
        // and threw away a column mapping somebody had just supplied for a headerless file. Cleared
        // rather than left where a row names nothing: a reading that outlived the file it was for
        // would be applied to whatever the row named next.
        _document.Placement = options.PlacementPath is { Length: > 0 }
            ? new RailPlacementReading
            {
                Origin  = options.PlacementOrigin,
                Units   = options.PlacementUnits,
                Columns = options.PlacementColumns is { Count: > 0 } c ? [.. c] : null,
            }
            : new RailPlacementReading();

        // R-ab1-5b. The one funnel, so an IMPORTED board whose parts are footprint instances
        // resolves its own pads for every refdes the netlist did not name.
        var resolvedPads = RailArtwork.PadsFor(
            board.View, board.ArtworkCellRef, board.Technology, netlist, null, board.Shapes);

        Board = board with
        {
            Pads         = resolvedPads.Pads,
            NetPoints    = resolvedPads.NetPoints,
            Nets         = resolvedPads.Nets,
            NetOrigin    = resolvedPads.NetOrigin,
            TurnedParts  = resolvedPads.Turned,
            ReferenceNet = _document.ReferenceNet,
        };
        Placement = placement;
        Bom = bom;
        PartLibrary = library;
        PartLibraryPath = library is null ? null : options.PartLibraryPath;

        // The library too — it was read for the session and never written down, so a design pointed
        // at another workspace's `.crlib` came back with no library on its next open (field report,
        // 2026-09-23). Save restates it document-relative with the other references. Only where one
        // was read: a re-import that names no library leaves the one the document already names.
        if (PartLibraryPath is { Length: > 0 }) _document.PartLibraryRef = PartLibraryPath;
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

        // R-rail22-3b: what the companion tables actually gave up, stated at the import rather than
        // left to be inferred from an odd answer later. Set unconditionally so a second import over
        // the first does not leave the previous board's counts standing.
        //
        // The NETLIST joined the BOM here on 2026-09-21: a netlist file that turned out not to be one
        // was read, refused by its own reader, and discarded without a word — see
        // RailImportReport.NetlistSummary for the report that found it.
        ImportSummary = RailImportReport.Summary(bom, netlist);

        RebuildAvailableNets();
        RebuildRegulatorOffers();
        RefreshRunGate();
    }

    /// <summary>
    /// What the last import read out of its companion tables — the BOM's three counts (R-rail22-3b).
    /// </summary>
    /// <remarks>
    /// <b>Not a refusal, and deliberately not on <see cref="PendingImportRefusal"/></b>, which gates
    /// the Run button: a board whose BOM has two unexpandable reference cells is a board that still
    /// solves, and blocking a run over a report would be worse than the silence it replaces. It sits
    /// on its own row of the status strip, under whatever the strip is otherwise saying.
    /// </remarks>
    [ObservableProperty]
    private string _importSummary = "";

    partial void OnImportSummaryChanged(string value) => OnPropertyChanged(nameof(HasImportSummary));

    /// <summary>True while the import has something to report. Bound rather than a length, because
    /// an empty row still takes a line of the strip.</summary>
    public bool HasImportSummary => ImportSummary.Length > 0;

    /// <summary>The board netlist this document was imported with, or opened with.</summary>
    /// <remarks>
    /// <b>The pick list is rebuilt from here rather than by each caller</b> (owner, 2026-09-19).
    /// It was rebuilt only by <see cref="AdoptImport"/>, so OPENING a <c>.crail</c> whose
    /// <c>BoardNetlistRef</c> resolved perfectly well left <see cref="AvailableNets"/> empty — and
    /// <see cref="HasNoPickableNets"/> is "a board is loaded and nothing named a net", so the
    /// window then told the user that no board netlist had named any nets and to click the pour
    /// instead. On the shipped Power Rail example, whose <c>.ipc</c> names <c>+3V3</c> and
    /// <c>GND</c>. A derived list that only one of two writers refreshes is a list that is wrong on
    /// the other path, silently, so the refresh belongs to the write.
    /// </remarks>
    [ObservableProperty]
    private BoardNetlist? _boardNetlist;

    partial void OnBoardNetlistChanged(BoardNetlist? value) => RebuildAvailableNets();

    // ── Step 2: picking the rail (§2.3, R-rail7-8) ─────────────────────────────────────────────

    /// <summary>
    /// Every net a board file or a board netlist named — what the pick list offers.
    /// </summary>
    /// <remarks>
    /// <b>Empty is not an error.</b> On the assisted-Gerber path there is no netlist and geometry
    /// alone has no net in it, so the pick is made by CLICKING THE POUR instead —
    /// <see cref="PickRailAt"/>. Both routes end in the same place: a rail, whose copper
    /// <c>Regions</c> walks and brief 8 highlights, so a user sees straight away whether it is
    /// one region or three islands joined by a 20 mil neck.
    /// </remarks>
    public ObservableCollection<RailNetRowViewModel> AvailableNets { get; } = [];

    private void RebuildAvailableNets()
    {
        AvailableNets.Clear();

        // ── R-ab2-4a: THE RESOLVED NET SET, NOT THE BOARD NETLIST ALONE ────────────────────────
        //
        // A drawn board with a schematic beside it now offers `+3V3` and `GND`, which is the whole
        // reason a user drew it. `Board.Nets` is RailArtwork's own union of the three claims — the
        // netlist, the schematic and whatever a user stamped on the copper — so this list and the
        // pads below it cannot disagree about what the board is called.
        //
        // BoardNetlist stays a contributor rather than the source, because OnBoardNetlistChanged
        // fires on an import before Board is assigned and a list that emptied itself in between
        // would flicker the pick pane through its no-nets state.
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string net in Board?.Nets ?? []) names.Add(net);
        if (BoardNetlist is { Refusal: null } n) foreach (string net in n.Nets) names.Add(net);

        foreach (string net in names) AvailableNets.Add(new RailNetRowViewModel(net));

        SelectedNet = null;
        OnPropertyChanged(nameof(HasPickableNets));
        OnPropertyChanged(nameof(HasNoPickableNets));
        OnPropertyChanged(nameof(NetOriginText));
        OnPropertyChanged(nameof(HasNetOriginText));
        RefreshNetMarks();

        // The gesture follows the sentence — see SyncPourPick's own note.
        SyncPourPick();
    }

    /// <summary>
    /// Where this board's net names came from — <i>"nets from the schematic"</i>, <i>"nets stated on
    /// the artwork"</i>, <i>"nets from the board netlist"</i> (R-ab2-4d). Empty where nothing named
    /// one.
    /// </summary>
    public string NetOriginText => PdnNetSummary.Describe(Board?.NetOrigin ?? PdnNetOrigin.None);

    /// <summary>True while there is an origin to state — bound rather than a length, because an
    /// empty row still takes a line of the strip.</summary>
    public bool HasNetOriginText => NetOriginText.Length > 0;

    /// <summary>The net highlighted in the pick list, or null.</summary>
    [ObservableProperty]
    private RailNetRowViewModel? _selectedNet;

    /// <summary>What that row is called, or null — every caller wants the name and not the row.</summary>
    public string? SelectedNetName => SelectedNet?.Name;

    /// <summary>Highlights the row for <paramref name="net"/>, by name. The window binds the ROW;
    /// a caller that has a name says it this way rather than hunting the list itself.</summary>
    public void SelectNet(string? net) =>
        SelectedNet = net is { Length: > 0 }
            ? AvailableNets.FirstOrDefault(r => string.Equals(r.Name, net, StringComparison.OrdinalIgnoreCase))
            : null;

    partial void OnSelectedNetChanged(RailNetRowViewModel? value)
    {
        PickSelectedNetCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(SelectedNetName));
        OnPropertyChanged(nameof(PickRailButtonText));
        OnPropertyChanged(nameof(WillShowExistingRail));

        // Escape reads HasSelection and the picked net is part of it — see that property's note. A
        // gate that is not re-published is a keystroke that does nothing on the one state it was
        // just given, which is exactly the shape the armed pour pick's own notification exists for.
        OnPropertyChanged(nameof(HasSelection));

        // R-rail19-2a: the pick is the moment a user needs to check they picked the right thing, and
        // on a board carrying +3V3, +3V3_A and VDD_IO the name is not enough. The board answers now
        // rather than after the rail has been committed and looked at.
        ShowNetPreview(value?.Name);

        // And the strip re-derives, so the reference-return refusal below does not outlive the row
        // it is about — a sentence naming 'GND' over a highlighted '+3V3' is a sentence nobody can
        // act on. Re-derived rather than cleared, which is the rule the run gate's own note states.
        RefreshRunGate();
    }

    /// <summary>True while there is a list to pick from — a board file or a board netlist named the
    /// nets.</summary>
    public bool HasPickableNets => AvailableNets.Count > 0;

    /// <summary>True once a board is loaded and NOTHING named a net, which is the assisted-Gerber
    /// path: the pick is made by clicking the pour instead, and the pane says so rather than showing
    /// an empty list.</summary>
    /// <remarks>
    /// <b>The sentence it gates is now CONDITIONAL, and that is R-ab2-4b</b>. It stays exactly as it
    /// is for a board that names nothing. It must not be printed over a board whose nets came from a
    /// schematic, because it is then a false statement about the model — and NOTHING FAILS WHEN A
    /// FALSE SENTENCE IS PRINTED, which is railRF brief 25's R-rail25-4b trap in its second
    /// instance. The condition needed no change: <see cref="AvailableNets"/> is built from the
    /// RESOLVED net set now, so a board with schematic nets is no longer empty here. What needed a
    /// test is the sentence's ABSENCE, and it would not have been missed any other way.
    ///
    /// <para><b>Click-the-pour keeps working either way</b> (R-ab2-4c). It is the gesture for
    /// <i>this copper here</i>, and a named board does not make it redundant — see
    /// <c>SyncPourPick</c>, which is what actually arms it.</para>
    /// </remarks>
    public bool HasNoPickableNets => Board is not null && AvailableNets.Count == 0;

    /// <summary>True while a net is highlighted, which is the only state the pick button can act in.</summary>
    /// <remarks>
    /// <b>The button used to be enabled with nothing selected and did nothing when pressed</b>
    /// (owner, 2026-09-19). A control that is live and silent is indistinguishable from a control
    /// that is broken, and the gate is one line — the list above it is the argument.
    /// </remarks>
    public bool CanPickSelectedNet => SelectedNet is not null;

    /// <summary>
    /// What the pick button says, which depends on whether the highlighted net is ALREADY a rail.
    /// </summary>
    /// <remarks>
    /// <b>The second half of the same report.</b> <see cref="PickRail"/> selects an existing rail
    /// rather than adding a second one of the same name, so pressing it on a net this document
    /// already carries — the shipped example's own, opened and clicked straight away — moved a
    /// selector that was already on that rail and looked exactly like a dead button. The button now
    /// says which of the two it will do before it is pressed.
    /// </remarks>
    public string PickRailButtonText => WillShowExistingRail ? "Show this rail" : "Make it a rail";

    /// <summary>
    /// True when pressing the button will SELECT a rail this document already has, rather than make
    /// a new one — <b>which of the two faces the glyph wears</b>.
    /// </summary>
    /// <remarks>
    /// <b>The button is a glyph now</b> (owner, 2026-09-20), and the two faces had to survive that.
    /// They exist because of a real report — pressing it on a net the document already carried moved
    /// a selector that was already where it was going and read as a dead button — so a single icon
    /// would have put the defect back with the fix's words removed. The eye and the plus say the
    /// same two things the two labels did, and the labels are still the tooltip.
    /// </remarks>
    public bool WillShowExistingRail =>
        SelectedNetName is { Length: > 0 } net && _document.Rail(net) is not null;

    /// <summary>Makes the highlighted net a rail — the list route of §2.3 step 2.</summary>
    [RelayCommand(CanExecute = nameof(CanPickSelectedNet))]
    private void PickSelectedNet()
    {
        if (SelectedNet is not { Name.Length: > 0 } row) return;

        // R-rail19-1d: the reference return is MARKED and still selectable, and picking it is
        // refused HERE rather than by leaving the row out of the list. A user who cannot find GND
        // and is told nothing is in exactly the position the dead Run button put him in; a row that
        // says why is an answer, a missing row is a second mystery.
        if (row.IsReferenceReturn && SelectedRail?.ReferenceLayer is { } layer)
        {
            // SELECTABLE rows only, for RebuildReferenceOptions' reason: a conductor with no drawing
            // layer is listed with `default` as its key and would answer for a rail naming 0/0.
            string name = ReferenceLayerOptions.FirstOrDefault(o => o.IsSelectable && o.Key == layer)?.Name
                       ?? $"{layer.Layer}/{layer.Datatype}";
            Refusal = new RailRefusal(
                ReferenceReturnRefusal(row.Name, name), RailRefusalControl.ReferenceLayer);
            return;
        }

        PickRail(row.Name);
        OnPropertyChanged(nameof(PickRailButtonText));
        OnPropertyChanged(nameof(WillShowExistingRail));
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

        // THE FUNNEL, so this is on the undo stack (owner, 2026-09-20). Adding a rail is a committed
        // edit and QueueResolve is the one place this view model records one — it was reached from
        // every typed value and from none of the three rail commands, which is why Ctrl+Z on a rail
        // that had just appeared took back the edit BEFORE it instead.
        QueueResolve();
        return rail;
    }

    /// <summary>
    /// Removes a rail, and everything that is only its — <b>the door out of R-rail19-1.</b>
    /// </summary>
    /// <remarks>
    /// <b>One mis-click used to disable Run, Compare and Export permanently.</b>
    /// <see cref="PickRail"/> adds a rail and nothing anywhere removed one: no command, no menu row,
    /// no context menu, no keystroke. A rail added by mistake states no reference layer, and
    /// <c>UnreferencedRail</c> — which is correct, because the rail set is solved together — then
    /// refused every run on the document forever, naming a remedy (confirm its reference) that the
    /// user does not want and that would leave a meaningless rail in the file. A window that can
    /// enter a state it cannot leave is not a window with a bug in one control; it is a window that
    /// can lose a session's work, and a first-time designer hit it within minutes of opening the
    /// shipped example.
    ///
    /// <para><b>Its sources, loads, targets, aggressors and parts go with it</b>, and there is
    /// nothing to write for that: they are fields of the <c>RailSpec</c> and nothing else in the
    /// document references them. What DOES need saying is that the rail is dropped from the document
    /// rather than emptied — a rail with no sources is still a rail the solve order has to place.</para>
    ///
    /// <para>The selector moves to whatever is left, which may be nothing: a document with no rails
    /// is an ordinary state and the run gate already has its own sentence for it.</para>
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanRemoveRail))]
    private void RemoveRail()
    {
        if (SelectedRail is not { } rail) return;

        _document.Rails.Remove(rail);

        // RebuildRails re-selects the first rail and moves the whole window with it, which is what
        // the selector's own contract says happens when it changes.
        RebuildRails();
        RebuildRegulatorOffers();
        RefreshNetMarks();
        OnPropertyChanged(nameof(PickRailButtonText));
        OnPropertyChanged(nameof(WillShowExistingRail));

        // The funnel — see PickRail's own note. Removing a rail is the edit a user is MOST likely to
        // want back: it takes the rail's sources, loads, targets and aggressors with it.
        QueueResolve();
    }

    /// <summary>True while there is a rail to remove.</summary>
    public bool CanRemoveRail => SelectedRail is not null;

    /// <summary>
    /// The pour-clicking route: a rail with no net name, anchored by a coordinate on the copper.
    /// </summary>
    /// <remarks>
    /// <b>railRF will not guess a net name</b> (brief 1), so a rail made this way carries none — it
    /// carries a source anchored at the point instead, which is what <c>Regions.Walk</c> seeds
    /// the connectivity walk from. The rail is NAMED after where it was picked rather than left
    /// unnamed, because every rail is named: the selector shows it and a refusal about the order has
    /// to say which two rails it is about.
    /// </remarks>
    public RailSpec PickRailAt(long xDbu, long yDbu, string? name = null)
    {
        string railName = name is { Length: > 0 } n ? n : $"rail at ({xDbu}, {yDbu})";

        var rail = new RailSpec { Name = railName };

        // R-rail27-4: through the SAME seeding every other add gesture uses. This added a bare
        // `RailSource` with no voltage, so a rail made by clicking a pour — the only route available
        // on a Gerber-only board — produced exactly the report `RailRfViewModel.Seeds.cs` was written
        // to prevent: "states no open-circuit voltage, so it contributes its impedance and no DC
        // level", and a column of zeros with nothing saying the document was the reason. The seeded
        // row is counted on the status strip, exactly as a dropped source is.
        //
        // R-rail34-2: and the copper it means — the topmost the board view is SHOWING under the
        // click, which is what the user aimed at. A pour with a pad of another net over it on the
        // far side is otherwise two rails' copper seeded as one.
        rail.Sources.Add(NewSeededSource(rail, WithShownLayer(new RailPortAnchor { Point = (xDbu, yDbu) })));

        _document.Rails.Add(rail);
        RebuildRails();
        SelectedRailName = rail.Name;

        // The funnel — see PickRail's own note.
        QueueResolve();
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

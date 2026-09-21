// The parts table and the three companion files behind it
// (brief-railrf-7-window.md R-rail7-9; railrf.md §2.3 step 3, §9).

using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.RailRf;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Pdn;
using CircuitRF.Render;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.RailRf;

public sealed partial class RailRfViewModel
{
    /// <summary>The BOM this document was imported with, or null.</summary>
    [ObservableProperty]
    private BomTable? _bom;

    /// <summary>The placement file, or null.</summary>
    [ObservableProperty]
    private PlacementTable? _placement;

    /// <summary>The part library, or null.</summary>
    [ObservableProperty]
    private PartLibrary? _partLibrary;

    partial void OnBomChanged(BomTable? value) => RebuildParts();
    partial void OnPlacementChanged(PlacementTable? value) => RebuildParts();
    partial void OnPartLibraryChanged(PartLibrary? value) => RebuildParts();

    /// <summary>
    /// The <c>.crlib</c> the library was read from, or null where none resolved.
    /// </summary>
    /// <remarks>
    /// Carried only so <see cref="RailProvenance"/> can say which library a coverage count is
    /// about — the same line <c>circuitrf rail</c> prints in its own banner. Nothing reads the file
    /// from here.
    /// </remarks>
    [ObservableProperty]
    private string? _partLibraryPath;

    /// <summary>
    /// §9's headline number: how many parts are modelled from a FILE rather than from a library row.
    /// </summary>
    /// <remarks>
    /// §9: <i>"the parts table's count of how many parts are modelled from a file is a headline number
    /// and not a detail"</i>. So it is on the status strip beside the bias-curve count, not a column
    /// somebody has to scroll a table to total up.
    /// </remarks>
    [ObservableProperty]
    private int _partsModelledFromFile;

    /// <summary>How many referenced part numbers carry no capacitance-versus-bias curve — the other
    /// half of the same strip. <see cref="PartLibraryCoverage"/> computes it; nothing here counts.</summary>
    [ObservableProperty]
    private int _partsWithoutBiasCurve;

    /// <summary>How many rows could not be resolved at all. Listed AS unresolved, and counted.</summary>
    [ObservableProperty]
    private int _partsUnresolved;

    /// <summary>What the part library covers, or null where there is no library.</summary>
    [ObservableProperty]
    private PartLibraryCoverage? _coverage;

    /// <summary>
    /// Rebuilds the parts table from <b>the selected rail's own part rows</b>, enriched by the BOM,
    /// the placement and the part library.
    /// </summary>
    /// <remarks>
    /// <b>R-rail18-5a. The rail's rows are the SUBJECT; the three files fill columns.</b> This read
    /// the BOM alone until then, and returned early without one — so the <c>Power Rail</c> example,
    /// whose thirteen typed capacitors drive every resonance on its curve, showed an empty pane.
    /// <c>BuildSweepRequest</c> resolves <c>rail.Parts</c> through <c>RailPartResolver</c>, so every
    /// anti-resonance, every removal ranking and every mask verdict already came out of parts the
    /// table did not list. R-rail11-8 makes that the ordinary P1 case rather than a corner: §6 makes
    /// P1 artwork-optional, so in P1 the mounting inductance is TYPED and the rows are typed with it.
    ///
    /// <para><b>It also stops the table being of the whole BOARD.</b> Listing every refdes the BOM
    /// names put another rail's decoupling under a header carrying the rail selector. A refdes the
    /// BOM names and the rail does not is not a row — it is not on this rail; a refdes the rail names
    /// and the BOM does not is a row with an unresolved part number, which is the state
    /// <see cref="RailPartRowViewModel.UnresolvedText"/> exists to say.</para>
    ///
    /// <para><b>The two headline counts are over the rail's distinct PART NUMBERS</b>, which is what
    /// <c>circuitrf rail</c> counts (<c>Rail.Provenance</c>). R-rail10-8: a verb and a window
    /// disagreeing about one document is the divergence this whole rule exists against, and this
    /// disagreed in both directions at once — the verb was moved onto <c>RailSpec.Parts</c> in review
    /// round 2 and the window was left on the BOM.</para>
    ///
    /// <para><b>Every electrical column is <c>RailPartResolver</c>'s own answer</b> — R-rail11-6:
    /// <i>"the individual models are what the parts table's rows read"</i>. It is the SAME call
    /// <see cref="BuildSweepRequest"/> makes, with the same rail voltage and the same computed
    /// mounting loops, so the table and the curve beside it cannot come to disagree about one part.
    /// Until 2026-09-19 this row read the library ROW instead, which is why the ESR column could
    /// only name a provenance (<i>stated</i>, beside a library stating 32 mΩ) and why the derated
    /// column read <i>unresolved</i> for every part of every document (owner, 2026-09-19). Resolving
    /// here is not a second opinion and it defaults nothing: a part the resolver cannot model comes
    /// back carrying <c>UnresolvedReason</c>, and the row prints §9's word for it.</para>
    /// </remarks>
    public void RebuildParts()
    {
        // The selection is kept by REFDES across a rebuild, not by reference. Every row object here
        // is new on every rebuild — and a rebuild happens on a solve, on a part edit and on the
        // placement or BOM arriving — so holding the old object would drop the user's selection, and
        // the board's mark with it, at moments that have nothing to do with the selection.
        string? wasSelected = SelectedPart?.Refdes;

        Parts.Clear();
        PartsModelledFromFile = 0;
        PartsUnresolved = 0;

        void Done()
        {
            // Re-seated where the refdes still exists; CLEARED where it does not, because a part that
            // was deleted is not a part that is still selected. Assigned through the property so the
            // highlight is recomputed either way.
            SelectedPart = wasSelected is { Length: > 0 } r
                ? Parts.FirstOrDefault(x => string.Equals(x.Refdes, r, StringComparison.OrdinalIgnoreCase))
                : null;

            // R-rail26-4a. The offer is recomputed on the SAME FUNNEL the table is, so a refdes
            // that has just been added is never offered again and nothing has to remember to
            // refresh it. R-rail26-5's sentence hangs off the same call for the same reason.
            RebuildPartOffer();

            OnPropertyChanged(nameof(StatusLine));
            OnPropertyChanged(nameof(RecognisedAggressors));
            OnPropertyChanged(nameof(HasRecognisedAggressors));
            OnPropertyChanged(nameof(UnmountedPartCount));
        }

        if (SelectedRail is not { } rail)
        {
            Coverage = null;
            PartsWithoutBiasCurve = 0;
            Done();
            return;
        }

        var bom = Bom is { Refusal: null } b ? b : null;
        var placedBy = Placement is { Refusal: null } p
            ? p.Rows.ToDictionary(r => r.Refdes, r => r, StringComparer.OrdinalIgnoreCase)
            : [];

        // The solve's own resolution, by refdes. Built once for the whole table rather than per row:
        // a part with an attached Touchstone file is READ here, and resolving each row separately
        // would read the same file once per instance of the part.
        var resolved = new RailPartResolver(PartLibrary ?? new PartLibrary())
            .ResolveAll(rail.Parts, rail.NominalVoltageV, ComputedMounting(rail))
            .Models
            .Where(m => m.Refdes is { Length: > 0 })
            .GroupBy(m => m.Refdes!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var part in rail.Parts)
        {
            if (string.IsNullOrWhiteSpace(part.Refdes)) continue;

            // A LIST per reference, not the first row (R-rail2-14 item 1): one internal part number
            // sits in front of a list of approved manufacturers, and taking the first silently is what
            // produces a plausible model from the wrong manufacturer's part. More than one is an
            // AMBIGUITY, and the row says so rather than choosing.
            var rows = bom?.RowsFor(part.Refdes) ?? [];
            var row = rows.Count == 1 ? rows[0] : null;

            string partNumber = part.PartNumber is { Length: > 0 } own ? own : row?.PartNumber ?? "";
            var model = partNumber.Length > 0 ? PartLibrary?.ResolveModel(partNumber) : null;

            string? position = placedBy.TryGetValue(part.Refdes, out var placement)
                ? BoardLengthFormat().Point(placement.X, placement.Y)
                  + (placement.Mirror ? " · bottom" : "")
                : null;

            resolved.TryGetValue(part.Refdes, out var element);

            // R-rail27-3b: the board's own land pattern, for the column the BOM would have filled.
            // The ROW view model applies the precedence — the BOM, then the library, then this — so
            // there is one place that decides which source speaks.
            _boardFootprints.TryGetValue(part.Refdes, out string? boardFootprint);

            var built = new RailPartRowViewModel(
                part, row, model,
                element?.MountingInductanceHenries ?? part.MountingInductanceHenries,
                position, element, boardFootprint);
            Parts.Add(built);

            if (built.IsUnresolved) PartsUnresolved++;
        }

        // The rail's own part numbers, distinct — the set `circuitrf rail` asks the library about.
        var referenced = rail.Parts
            .Select(x => x.PartNumber is { Length: > 0 } own
                ? own
                : (bom?.RowsFor(x.Refdes) is { Count: 1 } one ? one[0].PartNumber : null))
            .Where(pn => !string.IsNullOrWhiteSpace(pn))
            .Select(pn => pn!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        PartsModelledFromFile = PartLibrary is { } library
            ? referenced.Count(pn => library.ResolveModel(pn).Source == PartModelSource.AttachedFile)
            : 0;

        Coverage = PartLibrary is { } lib && referenced.Count > 0 ? lib.Coverage(referenced) : null;
        PartsWithoutBiasCurve = Coverage?.WithoutBiasCurve.Count ?? 0;

        Done();
    }

    /// <summary>
    /// The aggressor rows brief 2's BOM reader recognised, offered for the selected rail.
    /// </summary>
    /// <remarks>
    /// <b>Offered, not created</b> — the same rule the regulator chain follows. A pre-filled frequency
    /// nobody checked is exactly the one that will be wrong, which is why every row it adds carries
    /// <see cref="RailAggressorOrigin.Bom"/> and says so on its face.
    /// </remarks>
    public IReadOnlyList<RailAggressor> RecognisedAggressors =>
        Bom is { Refusal: null } b ? b.RecognisedAggressors : [];

    /// <summary>True while there is something to offer — the button appears only when there is.</summary>
    public bool HasRecognisedAggressors => RecognisedAggressors.Count > 0;

    // ══ MOUNT AND UNMOUNT (brief 23) ══════════════════════════════════════════════════════════
    //
    // Depopulating a board is the commonest what-if in power integrity, and until this existed it
    // cost an edit to the ARTWORK — destructive, not what the designer means, and it throws away
    // the mounting loop the geometry gave the part so it cannot be put back the way it was.
    //
    // NOTHING HERE TOUCHES THE .clay (R-rail23-1c). railRF SHOWS the board; the way to change the
    // geometry is to open the layout and edit it there, and depopulating is not a change to the
    // geometry — it is a statement about what is FITTED to it. The board still draws the part.

    /// <summary>
    /// Mounts or unmounts every named part on the selected rail, and re-solves ONCE.
    /// </summary>
    /// <remarks>
    /// <b>A BATCH, and that is R-rail23-2c</b> — <i>"unmount these four and re-run"</i> is the real
    /// gesture, and four separate calls would be four re-solves of a board the user is not looking
    /// at three of. So the rows are all rewritten, the table is rebuilt once, and
    /// <see cref="QueueResolve"/> runs once at the end.
    ///
    /// <para><b>Unmounting is an EDIT</b> (R-rail23-2d), so it goes through the same Fast loop every
    /// other committed row edit does rather than being a separate "apply" step — the numbers follow
    /// it exactly as they follow a typed ESR.</para>
    ///
    /// <para><b>A row already in the asked-for state is not rewritten</b>, so a menu row pressed on
    /// a mixed selection does not re-solve for the half that did not move, and pressing it twice is
    /// not two runs.</para>
    /// </remarks>
    /// <param name="refdeses">The instances to act on. Unknown names are ignored — the caller's
    /// list is a selection and a selection can outlive a rebuild.</param>
    /// <param name="mounted">True to fit, false to depopulate.</param>
    /// <returns>How many rows actually changed.</returns>
    public int SetPartsMounted(IEnumerable<string> refdeses, bool mounted)
    {
        ArgumentNullException.ThrowIfNull(refdeses);
        if (SelectedRail is not { } rail) return 0;

        var wanted = new HashSet<string>(refdeses, StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0) return 0;

        int changed = 0;
        for (int i = 0; i < rail.Parts.Count; i++)
        {
            var part = rail.Parts[i];
            if (part.Refdes is not { Length: > 0 } refdes || !wanted.Contains(refdes)) continue;
            if (part.Mounted == mounted) continue;

            // A RECORD `with`, so the refdes, the part number, the origin and — the number that
            // matters — the typed mounting inductance are carried across untouched. That is what
            // makes re-mounting give the answer it gave before, bit for bit (R-rail23-1b).
            rail.Parts[i] = part with { Mounted = mounted };
            changed++;
        }

        if (changed == 0) return 0;

        RebuildParts();
        QueueResolve();
        return changed;
    }

    /// <summary>One part, by refdes — the row's own checkbox and the board's context menu.</summary>
    public bool SetPartMounted(string refdes, bool mounted) =>
        SetPartsMounted([refdes], mounted) > 0;

    /// <summary>Whether the named part is fitted, or null where this rail has no such row.</summary>
    public bool? IsPartMounted(string refdes) =>
        SelectedRail?.Parts.FirstOrDefault(
            p => string.Equals(p.Refdes, refdes, StringComparison.OrdinalIgnoreCase))?.Mounted;

    /// <summary>How many of the selected rail's parts are not fitted. Zero on an ordinary
    /// document, which is why nothing is said about it until it is not.</summary>
    public int UnmountedPartCount =>
        SelectedRail is { } rail ? rail.Parts.Count(p => !p.Mounted) : 0;

    /// <summary>
    /// Points this document at a part library that has just been created for it, and reads it
    /// (brief-authored-board-4 R-ab4-4b).
    /// </summary>
    /// <remarks>
    /// <b>The reference is written document-relative</b>, like every other one a <c>.crail</c>
    /// carries, so an archived or moved workspace still resolves it. The library is read through
    /// <c>PartLibraryIo</c> straight away rather than on the next open, because the parts table's
    /// every electrical column is resolved against it and a window that had to be closed and
    /// reopened to see the rows it just created would be the same "nothing happened" report the
    /// editor itself came from.
    ///
    /// <para>The document is left in whatever saved state the caller puts it in — this method writes
    /// no file. The workspace writes the <c>.crail</c> and calls <see cref="NoteSaved"/>, because it
    /// is the half that knows where the file is.</para>
    /// </remarks>
    public void AdoptPartLibrary(string crlibPath)
    {
        ArgumentNullException.ThrowIfNull(crlibPath);
        if (DocumentPath is not { Length: > 0 } path) return;

        string dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!;
        Document.PartLibraryRef =
            CircuitRF.Core.RefPath.ToStored(System.IO.Path.GetRelativePath(dir, crlibPath));

        PartLibraryPath = crlibPath;
        try   { PartLibrary = PartLibraryIo.LoadFromFile(crlibPath); }
        catch { PartLibrary = null; }

        RefreshDirty();
    }

    /// <summary>Adds every recognised aggressor the selected rail does not already carry.</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    public void AcceptRecognisedAggressors()
    {
        if (SelectedRail is not { } rail) return;

        foreach (var a in RecognisedAggressors)
            if (!rail.Aggressors.Any(x => string.Equals(x.Name, a.Name, StringComparison.OrdinalIgnoreCase)))
                rail.Aggressors.Add(a);

        RebuildForSelectedRail();
    }

    // ══ THE PARTS ON THE BOARD (brief 26) ═════════════════════════════════════════════════════
    //
    // The table above had no PRODUCER. `new RailPart` appeared nowhere in src/ except the reader,
    // `RailPartOrigin.Bom` was assigned by nothing, and this window had no add-part gesture at all
    // — so the only way a part had ever appeared in railRF was somebody writing the JSON, and a
    // board with fifty-five placed footprints opened with column headings over nothing and no
    // sentence anywhere saying why.
    //
    // NOTHING HERE CLASSIFIES A BOARD. `RailPartDiscovery` does, in src/Design, so `circuitrf rail`
    // and this window cannot come to different conclusions about one board (R-rail26-1). What is
    // here is what turns its answer into an OFFER.

    /// <summary>
    /// What the board has to say about this rail's parts — the candidates, what was skipped and
    /// why. <b>Recomputed on <see cref="RebuildParts"/>' own funnel</b>, never on a timer.
    /// </summary>
    public RailDiscoveryResult PartOffer { get; private set; } =
        RailDiscoveryResult.None(RailDiscoveryState.NotExtracted);

    /// <summary>True while there is something to offer — the line appears only when there is.</summary>
    public bool HasPartOffer => PartOffer.HasOffer;

    /// <summary>R-rail26-4's headline: how many two-terminal parts sit between this rail and its
    /// reference and are not in the document.</summary>
    public string PartOfferText => PartOffer.OfferSentence;

    /// <summary>
    /// The other half — how many parts touch this rail and are NOT decoupling, broken down by why.
    /// </summary>
    /// <remarks>
    /// <b>Not decoration</b> (R-rail26-4). A designer who is told 24 were added and not that 12
    /// were skipped has no way to know whether the bulk capacitor they are looking for is one of
    /// the 12.
    /// </remarks>
    public string PartOfferSkippedText => PartOffer.SkippedSentence;

    /// <summary>True while anything was skipped.</summary>
    public bool HasPartOfferSkipped => PartOffer.SkippedSentence.Length > 0;

    /// <summary>The per-part reasons, for the offer line's tooltip — every skipped refdes named,
    /// because a count alone cannot answer "is my bulk capacitor one of them".</summary>
    public string PartOfferSkippedTooltip =>
        PartOffer.Skipped.Count == 0 ? "" : string.Join("\n", PartOffer.Skipped.Select(s => s.Sentence));

    /// <summary>
    /// The sentence an EMPTY parts table carries, or null while it has rows (R-rail26-5).
    /// </summary>
    /// <remarks>
    /// <b>This is the part of the report that is a defect on its own</b>: a pane that is empty and
    /// silent reads as a broken pane, and that is what was reported. It names the STATE it is in
    /// rather than saying one thing always — before a board, before a reference is confirmed, on a
    /// rail where nothing was found, and on one where something was.
    /// </remarks>
    public string? PartsEmptyText
    {
        get
        {
            if (Parts.Count > 0) return null;

            if (SelectedRail is null)
                return "No rail yet. Pick one on the board, or add one — a part row belongs to a " +
                       "rail, so there is nothing for it to sit on until there is one.";

            if (Board is null)
                return "No board yet. Open the layout this document names, or import one — with no " +
                       "artwork railRF has nothing to read parts off, and a part row is one you type.";

            if (PartOffer.State == RailDiscoveryState.NotExtracted)
                return "This rail's copper has not been extracted yet. Confirm its reference return " +
                       "and run, and railRF will look for the parts sitting between the rail and it.";

            if (PartOffer.HasOffer)
                return $"This rail has no part rows yet — add the {PartOffer.Offered.Count} railRF " +
                       "found on the board, or type them.";

            return "No two-terminal part sits between this rail and its reference, so there was " +
                   "nothing to offer. Rows can still be typed — §6 makes artwork optional.";
        }
    }

    /// <summary>True while <see cref="PartsEmptyText"/> has something to say.</summary>
    public bool HasPartsEmptyText => PartsEmptyText is not null;

    /// <summary>True while the table has rows — what brief 27's two gestures hang off, since both
    /// act on a selection and there is nothing to select until there is a row.</summary>
    public bool HasParts => Parts.Count > 0;

    /// <summary>
    /// Re-reads the board for this rail's parts.
    /// </summary>
    /// <remarks>
    /// <b>The regions come off the LAST EXTRACTION</b> (R-rail26-2a) — <see cref="SeriesRegions"/>,
    /// which reads them back off the DC result rather than walking the copper a second time. A
    /// second connectivity model beside the one the DC answer is built from is two answers to one
    /// question, and both halves of this predicate are that walk's own answer.
    /// </remarks>
    private void RebuildPartOffer()
    {
        PartOffer = SelectedRail is { } rail && Board is { } board
            ? RailPartDiscovery.Discover(new RailDiscoveryRequest
            {
                Rail         = rail,
                Pads         = board.Pads,
                Regions      = SeriesRegions(rail),
                Bom          = Bom,
                ReferenceNet = board.ReferenceNet ?? Document.ReferenceNet,

                // NO TECHNOLOGY AND NO SHAPES, deliberately. Those are what discovery computes a
                // candidate's MOUNTING LOOP from, and nothing on this window reads one: a row gets
                // its loop after it is added, from the solve's own ComputedMounting map, exactly as
                // every other row does (R-rail26-3a). The verb supplies them because its report is
                // what an agent writes rows from, and a loop it never printed would be a loop
                // nobody could use.
            })
            : RailDiscoveryResult.None(RailDiscoveryState.NotExtracted);

        OnPropertyChanged(nameof(PartOffer));
        OnPropertyChanged(nameof(HasPartOffer));
        OnPropertyChanged(nameof(PartOfferText));
        OnPropertyChanged(nameof(PartOfferSkippedText));
        OnPropertyChanged(nameof(HasPartOfferSkipped));
        OnPropertyChanged(nameof(PartOfferSkippedTooltip));
        OnPropertyChanged(nameof(PartsEmptyText));
        OnPropertyChanged(nameof(HasPartsEmptyText));
        OnPropertyChanged(nameof(HasParts));
        AcceptDiscoveredPartsCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Adds every offered row to the selected rail, and re-solves ONCE.
    /// </summary>
    /// <remarks>
    /// <b>ONE EDIT, and that is R-rail26-4b.</b> <c>SetPartsMounted</c> has already settled that a
    /// batch is the real gesture; 24 separate undo entries would be 24 presses to take back one
    /// button. The undo entry is <see cref="QueueResolve"/>'s, which this calls once at the end —
    /// the same funnel every other committed edit in this window goes through.
    ///
    /// <para><b>Idempotent by refdes</b> (R-rail26-4a): a refdes the rail already carries is never
    /// added twice, so pressing the button again adds nothing. Discovery filters on the same rule,
    /// so after the first press there is nothing left to offer and the line goes away.</para>
    ///
    /// <para><b>A discovered row is a row like any other</b> (R-rail26-6) — it saves, it mounts and
    /// unmounts, it resolves against the library and it is deletable. <c>Origin</c> is all that says
    /// where it came from.</para>
    /// </remarks>
    /// <returns>How many rows were added.</returns>
    public int AddDiscoveredParts()
    {
        if (SelectedRail is not { } rail) return 0;

        var already = new HashSet<string>(
            rail.Parts.Select(p => p.Refdes).Where(r => r.Length > 0),
            StringComparer.OrdinalIgnoreCase);

        int added = 0;
        foreach (var part in PartOffer.Parts)
            if (part.Refdes is { Length: > 0 } refdes && already.Add(refdes))
            {
                rail.Parts.Add(part);
                added++;
            }

        if (added == 0) return 0;

        RebuildParts();
        QueueResolve();
        return added;
    }

    /// <summary>The button on the offer line.</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand(CanExecute = nameof(HasPartOffer))]
    public void AcceptDiscoveredParts() => AddDiscoveredParts();

    // ══ WHICH PART IS IT? (brief 27) ══════════════════════════════════════════════════════════
    //
    // Brief 26's discovery refuses to invent a part number from a land pattern, and it is right to:
    // an 0402 land is a case size, not a capacitance. On a board with no bill of materials that is
    // every discovered row, all of them listed as unresolved, and the |Z| curve has no decoupling in
    // it. The table is right and the answer is still empty.
    //
    // The missing gesture is the one a designer expects: SELECT THE ROWS THAT ARE THE SAME PART AND
    // SAY WHICH PART THEY ARE. The parts list has been SelectionMode="Multiple" since brief 23's
    // batch unmount, so the selection already exists.
    //
    // THIS DOES NOT MAKE THE PARTS TABLE EDITABLE, and the distinction is the whole argument.
    // RailPartRowViewModel is read-only "deliberately… a row that could be edited here would be a
    // second place the same number lives" — and that rule is about the MODEL: capacitance, ESR, f0,
    // which belong to the part library. A part NUMBER is not a model value. It is the row's own field
    // on the document (RailPart.PartNumber), it is what the library is keyed BY, and choosing it is
    // choosing which library row applies. Every electrical column stays read-only and stays the
    // library's.

    /// <summary>
    /// Writes <paramref name="partNumber"/> onto the named rows, and re-solves ONCE.
    /// </summary>
    /// <remarks>
    /// <b>SetPartsMounted's shape exactly</b>, and for its reasons: a batch is the real gesture, a
    /// record <c>with</c> carries every other field across untouched, a row already carrying that
    /// number is not rewritten, and the single <see cref="QueueResolve"/> at the end is the one undo
    /// entry the coarse stack hangs off.
    ///
    /// <para><b>An empty part number CLEARS it</b>, which is the state a discovered row starts in —
    /// so an assignment made by mistake is taken back by the same gesture and not only by undo.</para>
    /// </remarks>
    /// <param name="refdeses">The instances to act on. Unknown names are ignored — the caller's list
    /// is a selection and a selection can outlive a rebuild.</param>
    /// <param name="partNumber">The internal part number the part library is keyed by.</param>
    /// <returns>How many rows actually changed.</returns>
    public int AssignPartNumber(IEnumerable<string> refdeses, string? partNumber)
    {
        ArgumentNullException.ThrowIfNull(refdeses);
        if (SelectedRail is not { } rail) return 0;

        var wanted = new HashSet<string>(refdeses, StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0) return 0;

        string assigned = (partNumber ?? "").Trim();

        int changed = 0;
        for (int i = 0; i < rail.Parts.Count; i++)
        {
            var part = rail.Parts[i];
            if (part.Refdes is not { Length: > 0 } refdes || !wanted.Contains(refdes)) continue;
            if (string.Equals(part.PartNumber ?? "", assigned, StringComparison.Ordinal)) continue;

            rail.Parts[i] = part with { PartNumber = assigned };
            changed++;
        }

        if (changed == 0) return 0;

        RebuildParts();
        QueueResolve();
        return changed;
    }

    /// <summary>The part numbers already known to this document and its library — what the assign
    /// gesture offers before free text, so twenty rows of one part are spelled one way.</summary>
    /// <remarks>
    /// <b>The library first, then the rail's own rows.</b> A number the library knows resolves to a
    /// model; a number only the rail carries is one somebody typed a moment ago and is about to type
    /// again on the next four rows, which is exactly what a list is for.
    /// </remarks>
    public IReadOnlyList<string> KnownPartNumbers
    {
        get
        {
            var known = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string pn in PartLibrary?.Rows.Select(r => r.PartNumber) ?? [])
                if (pn is { Length: > 0 } && seen.Add(pn)) known.Add(pn);

            foreach (var rail in _document.Rails)
                foreach (var part in rail.Parts)
                    if (part.PartNumber is { Length: > 0 } pn && seen.Add(pn)) known.Add(pn);

            known.Sort(StringComparer.OrdinalIgnoreCase);
            return known;
        }
    }

    /// <summary>
    /// R-rail27-3b's input — which land pattern each placed part sits on, by designator.
    /// </summary>
    /// <remarks>
    /// <b>No document field is added</b> and nothing is cached across boards: the artwork is still
    /// there on the next open, and the map is rebuilt with the rows. Rebuilt on the BOARD's own
    /// setter rather than inside <see cref="RebuildParts"/>, which runs on every solve and every row
    /// edit — the placements do not change between those.
    /// </remarks>
    private IReadOnlyDictionary<string, string> _boardFootprints =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private void RebuildBoardFootprints() =>
        _boardFootprints = PdnLayoutPads.FootprintsOf(Board?.View);
}

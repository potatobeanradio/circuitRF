// The parts table and the three companion files behind it
// (brief-railrf-7-window.md R-rail7-9; railrf.md §2.3 step 3, §9).

using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.RailRf;
using CircuitRF.Design.Layout;
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

            var built = new RailPartRowViewModel(
                part, row, model,
                element?.MountingInductanceHenries ?? part.MountingInductanceHenries,
                position, element);
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
}

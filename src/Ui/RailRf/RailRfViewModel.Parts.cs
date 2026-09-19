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

    // ── the selected part, and where it is on the board ────────────────────────────────────────

    /// <summary>
    /// The row picked in the parts table, or null for none — <b>the list box's own selection</b>,
    /// bound two ways.
    /// </summary>
    /// <remarks>
    /// <b>The table and the board are two views of one part.</b> Before this the parts table listed
    /// thirteen capacitors beside a picture of the board and there was no way to find any of them on
    /// it: the Position column says where C7 is in millimetres, and a coordinate is not something a
    /// person locates by eye on a 30 mm board (owner, 2026-09-19).
    ///
    /// <para><b>Null is an ordinary state and it is reachable</b> — Escape clears it
    /// (<see cref="ClearPartSelectionCommand"/>). A selection that could be made and not unmade
    /// leaves a mark on the board that outlives any reason for it, and the only way back would be to
    /// close the window.</para>
    /// </remarks>
    [ObservableProperty]
    private RailPartRowViewModel? _selectedPart;

    partial void OnSelectedPartChanged(RailPartRowViewModel? value)
    {
        OnPropertyChanged(nameof(PartHighlight));

        // Pushed at the overlay, the way every other board input on this view model is (Result,
        // Plane, Kind, HiddenLayers). It does NOT rebuild the scene — see RailLayoutOverlay.
        BoardOverlayLayer.PartHighlight = PartHighlight;
    }

    /// <summary>
    /// Where the selected part is on the board, or null when nothing is selected or the board does
    /// not place it.
    /// </summary>
    /// <remarks>
    /// <b>Resolved HERE, from the board netlist, and not by the overlay or the renderer.</b> Which
    /// pads belong to <c>C7</c> is a question about the netlist, and answering it below the firewall
    /// would be a second copy of <c>PdnAttachments</c>' resolution that could come to disagree with
    /// the one every port already uses.
    ///
    /// <para><b>A part the board does not place produces null and the picture does not change.</b>
    /// The parts table already says <i>not placed</i> on that row; a mark drawn at the placement
    /// centroid of a part with no pads, or at the origin, would say something that is not true.</para>
    /// </remarks>
    public RailPartHighlight? PartHighlight
    {
        get
        {
            if (SelectedPart is not { } row || Board is not { } board) return null;

            var pads = board.Pads
                .Where(p => string.Equals(p.Refdes, row.Refdes, StringComparison.OrdinalIgnoreCase))
                .Select(p => (p.X, p.Y))
                .ToList();

            // The body box where the placement states a centroid — it is what the part actually
            // COVERS, and on a bulk capacitor that is several millimetres wider than its two pads.
            Bbox? body = null;
            if (Placement is { Refusal: null } table &&
                table.Rows.FirstOrDefault(r =>
                    string.Equals(r.Refdes, row.Refdes, StringComparison.OrdinalIgnoreCase)) is
                    { Refdes: { Length: > 0 } } placed)
            {
                var box = new Bbox(placed.X, placed.Y, placed.X, placed.Y);
                foreach (var (x, y) in pads) box = box.Union(new Bbox(x, y, x, y));
                long reach = RailPartHighlight.PadReachDbu;
                body = new Bbox(box.MinX - reach, box.MinY - reach, box.MaxX + reach, box.MaxY + reach);
            }

            if (pads.Count == 0 && body is null) return null;

            return new RailPartHighlight(row.Refdes, pads, body);
        }
    }

    /// <summary>Clears the parts-table selection — what <b>Escape</b> is wired to.</summary>
    /// <remarks>
    /// A command rather than a setter call in the code-behind, so the keystroke and any menu row that
    /// ever wants it reach the same one thing, and so a test can drive it with no application host.
    /// </remarks>
    [RelayCommand]
    private void ClearPartSelection() => SelectedPart = null;

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
    /// <para><b>The derated value reads <i>unresolved</i> here by construction, and that is correct
    /// rather than pending</b>: derating from a bias curve is brief 11's and it happens in the solve.
    /// A plausible number here would be the defaulted one §9 exists to prevent.</para>
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

            var built = new RailPartRowViewModel(
                part, row, model, part.MountingInductanceHenries, position);
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

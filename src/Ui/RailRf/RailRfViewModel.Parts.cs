// The parts table and the three companion files behind it
// (brief-railrf-7-window.md R-rail7-9; railrf.md §2.3 step 3, §9).

using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.RailRf;
using CommunityToolkit.Mvvm.ComponentModel;

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

    /// <summary>
    /// Rebuilds the parts table from the BOM, the placement and the part library.
    /// </summary>
    /// <remarks>
    /// <b>Anything railRF could not resolve is listed as unresolved rather than defaulted</b>
    /// (§2.3 step 3). A reference the BOM names but the library does not still gets a ROW — omitting
    /// it is the failure, because <i>not on the board</i> and <i>on the board with no model</i> must
    /// not look the same, which is the same finding <see cref="RailPortDrop"/> records for an
    /// observation port.
    ///
    /// <para><b>The derated value and the mounting inductance read <i>unresolved</i> here by
    /// construction, and that is correct rather than pending.</b> Derating from a bias curve is brief
    /// 11's and the computed mounting loop is brief 13's; until they land, the honest thing for those
    /// columns to say is that railRF has not resolved them — which is exactly what
    /// <see cref="RailPartRowViewModel.UnresolvedText"/> says. A plausible number here would be the
    /// defaulted one §9 exists to prevent.</para>
    /// </remarks>
    public void RebuildParts()
    {
        Parts.Clear();
        PartsModelledFromFile = 0;
        PartsUnresolved = 0;

        if (Bom is not { Refusal: null } bom)
        {
            Coverage = null;
            PartsWithoutBiasCurve = 0;
            OnPropertyChanged(nameof(StatusLine));
            OnPropertyChanged(nameof(RecognisedAggressors));
            OnPropertyChanged(nameof(HasRecognisedAggressors));
            return;
        }

        var placedBy = Placement is { Refusal: null } p
            ? p.Rows.ToDictionary(r => r.Refdes, r => r, StringComparer.OrdinalIgnoreCase)
            : [];

        foreach (var refdes in bom.Rows.Select(r => r.Refdes).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // A LIST per reference, not the first row (R-rail2-14 item 1): one internal part number
            // sits in front of a list of approved manufacturers, and taking the first silently is what
            // produces a plausible model from the wrong manufacturer's part. More than one is an
            // AMBIGUITY, and the row says so rather than choosing.
            var rows = bom.RowsFor(refdes);
            var row = rows.Count == 1 ? rows[0] : null;

            var model = row?.PartNumber is { Length: > 0 } pn ? PartLibrary?.ResolveModel(pn) : null;

            string? position = placedBy.TryGetValue(refdes, out var placement)
                ? $"({placement.X}, {placement.Y}) DBU" + (placement.Mirror ? " · bottom" : "")
                : null;

            var part = new RailPartRowViewModel(refdes, row, model, mountingInductanceHenries: null, position);
            Parts.Add(part);

            if (model is { Source: PartModelSource.AttachedFile, Row: not null }) PartsModelledFromFile++;
            if (part.IsUnresolved) PartsUnresolved++;
        }

        Coverage = PartLibrary?.Coverage(bom.PartNumbers);
        PartsWithoutBiasCurve = Coverage?.WithoutBiasCurve.Count ?? 0;

        OnPropertyChanged(nameof(StatusLine));
        OnPropertyChanged(nameof(RecognisedAggressors));
        OnPropertyChanged(nameof(HasRecognisedAggressors));
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

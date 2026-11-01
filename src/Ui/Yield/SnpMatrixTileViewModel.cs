// ================================================================
//  SnpMatrixTileViewModel.cs  —  the SnP matrix tile under the ranked
//  list (brief-sens-3 R-sens3-6)
//
//  An N×N grid of one SnP instance's entries, each cell coloured by
//  that entry's share for the selected response on ONE sequential
//  scale (0 to the tile's maximum); the selected port's row and column
//  and the selected entry outlined; a tied instance colours S(i,j) and
//  S(j,i) as one. Two settings ride on it: Include (the same value as
//  the instance's S-parameter blocks row) and Tie reciprocal pairs,
//  which writes Reciprocal= on the instance.
// ================================================================

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CircuitRF.Design.Optimization;

namespace CircuitRF.Ui.Yield;

/// <summary>One cell of the tile: S(I,J) with its share and how far up the tile's scale it sits (0–1).</summary>
public sealed record SnpMatrixCell(int I, int J, double Share, double Intensity, bool InSelectedPort, bool IsSelectedEntry, string Tip,
                                   double Size, bool ShowLabel)
{
    public string Label => SnpMatrixTileViewModel.EntryText(I, J);
}

public sealed partial class SnpMatrixTileViewModel : ObservableObject
{
    private readonly YieldPanelViewModel _panel;
    private bool _syncing;

    internal SnpMatrixTileViewModel(YieldPanelViewModel panel, SnpInstanceInfo info, IReadOnlyDictionary<(int, int), double> shares,
                                    bool hasShares, int? port, (int I, int J)? entry)
    {
        _panel = panel;
        Instance = info.Instance;
        Bind(info, shares, hasShares, port, entry);
    }

    public string Instance { get; }

    [ObservableProperty] private int _ports;

    /// <summary>Row-major, (1,1) first.</summary>
    [ObservableProperty] private IReadOnlyList<SnpMatrixCell> _cells = [];

    /// <summary>Cells shrink above about 8 ports rather than scroll; the share is in each cell's tooltip.</summary>
    [ObservableProperty] private double _cellSize;

    public bool ShowCellLabels => CellSize >= 26;

    partial void OnCellSizeChanged(double value) => OnPropertyChanged(nameof(ShowCellLabels));

    /// <summary>The legend's top: the tile's largest share.</summary>
    [ObservableProperty] private string _legendMaxText = "";

    [ObservableProperty] private bool _hasShares;

    public int? SelectedPort { get; private set; }
    public (int I, int J)? SelectedEntry { get; private set; }

    /// <summary>Include — one value with the S-parameter blocks row's.</summary>
    [ObservableProperty] private bool _isIncluded;

    /// <summary>Disabled, keeping its tick, while the S-parameters master is off.</summary>
    [ObservableProperty] private bool _isIncludeEnabled;

    partial void OnIsIncludedChanged(bool value)
    {
        if (!_syncing) _panel.SetSnpIncluded(Instance, value);
    }

    /// <summary>Tie reciprocal pairs (S21 = S12).</summary>
    [ObservableProperty] private bool _isTied;

    /// <summary><c>auto: worst |S(i,j) − S(j,i)| = 0.0021</c> — the automatic state and its reason.</summary>
    [ObservableProperty] private string _tieTip = "";

    /// <summary>Only a top-level instance of the focused schematic can be written here.</summary>
    [ObservableProperty] private bool _canTie;

    partial void OnIsTiedChanged(bool value)
    {
        if (_syncing) return;
        _ = SetTiedAsync(value);
    }

    /// <summary>The setting as a task, so a caller (a test) can await the confirmation it may ask.</summary>
    public Task<bool> SetTiedAsync(bool tie) => SetTiedCore(tie);

    private async Task<bool> SetTiedCore(bool tie)
    {
        bool done = await _panel.SetSnpTied(Instance, tie);
        // Refused or cancelled: the box goes back to what the instance holds.
        if (!done && _panel.SnpInfo(Instance) is { Tied: { } now })
        {
            _syncing = true;
            IsTied = now;
            _syncing = false;
        }
        return done;
    }

    internal void Bind(SnpInstanceInfo info, IReadOnlyDictionary<(int, int), double> shares, bool hasShares, int? port, (int I, int J)? entry)
    {
        _syncing = true;
        Ports = info.Ports;
        SelectedPort = port;
        SelectedEntry = entry;
        HasShares = hasShares;
        double max = shares.Count == 0 ? 0 : shares.Values.Max();
        double size = Ports <= 4 ? 34 : Ports <= 8 ? 26 : System.Math.Max(8, 208.0 / Ports);
        var cells = new List<SnpMatrixCell>(Ports * Ports);
        for (int i = 1; i <= Ports; i++)
            for (int j = 1; j <= Ports; j++)
            {
                double s = shares.GetValueOrDefault((i, j));
                bool selected = entry is { } e && ((e.I == i && e.J == j) || (info.Tied == true && e.I == j && e.J == i));
                string tip = hasShares ? $"{EntryText(i, j)}: {Percent(s)}" : EntryText(i, j);
                cells.Add(new SnpMatrixCell(i, j, s, max > 0 ? s / max : 0, port is { } p && (i == p || j == p), selected, tip, size, size >= 26));
            }
        Cells = cells;
        CellSize = size;
        LegendMaxText = hasShares ? Percent(max) : "";
        IsIncluded = _panel.IsSnpIncluded(Instance);
        IsIncludeEnabled = _panel.SensSnp;
        IsTied = info.Tied == true;
        CanTie = info.Tied is not null && _panel.SnpComponent(Instance) is not null;
        bool autoTies = info.Reciprocity is { } rec && rec.Worst <= RfCore.SnpPerturbation.ReciprocalTolerance;
        string auto = info.Reciprocity is { } r
            ? $"auto: worst |S(i,j) − S(j,i)| = {r.Worst.ToString("G2", CultureInfo.InvariantCulture)} — {(autoTies ? "tied" : "untied")} on its own"
            : "auto";
        TieTip = (info.Mode == RfCore.SnpReciprocal.Auto ? auto : $"stated {(info.Mode == RfCore.SnpReciprocal.Tied ? "tied" : "untied")}; {auto}")
               + (CanTie ? "" : " — set Reciprocal= on the instance inside its cell");
        _syncing = false;
    }

    internal static string EntryText(int i, int j)
        => "S" + i.ToString(CultureInfo.InvariantCulture) + (i > 9 || j > 9 ? "," : "") + j.ToString(CultureInfo.InvariantCulture);

    private static string Percent(double share) => (100 * share).ToString("0.#", CultureInfo.InvariantCulture) + " %";
}

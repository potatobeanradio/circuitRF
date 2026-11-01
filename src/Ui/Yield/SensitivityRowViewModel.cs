// ================================================================
//  SensitivityRowViewModel.cs  —  one row of the Sensitivity panel's
//  ranked list (brief-sens-3 R-sens3-5)
//
//  A row is one roll-up of the result (R-sens2-6) — an entry, a port,
//  an instance — or, at Instance, one key under an expanded entry; or
//  the greyed row of an SnP instance the run left out. It carries what
//  the table shows (label, basis, signed Δ, share, a status glyph whose
//  tooltip is the reason) and which keys it stands for, so the row's
//  actions act on exactly those keys. Nothing here computes a number.
// ================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Design.Statistics;

namespace CircuitRF.Ui.Yield;

public sealed partial class SensitivityRowViewModel : ObservableObject
{
    private readonly YieldPanelViewModel _panel;
    private bool _syncing;

    private SensitivityRowViewModel(YieldPanelViewModel panel, string id) { _panel = panel; Id = id; }

    /// <summary>Stable across a regroup or a re-run, so a selection and an expansion survive.</summary>
    public string Id { get; }

    public string Label { get; private init; } = "";

    /// <summary><c>σ</c>, <c>1 %</c>, <c>0.1 dB · 1°</c> — what the row's keys were stepped by (D3); <c>excluded</c>.</summary>
    public string Basis { get; private init; } = "";

    /// <summary>The signed Δ (√ΣΔ² for a group of keys), in normalized margin or the measure's unit.</summary>
    public string DeltaText { get; private init; } = "";

    public double Share { get; private init; }

    public string ShareText => IsExcluded ? "" : (100 * Share).ToString(Share >= 0.1 ? "0" : "0.0", CultureInfo.InvariantCulture) + " %";

    public bool HasShare => !IsExcluded;

    /// <summary>The instance the row belongs to (an SnP block, a part, a variable).</summary>
    public string Instance { get; private init; } = "";

    /// <summary>The port, on a Port row.</summary>
    public int? Port { get; private init; }

    /// <summary>The S-parameter entry, on an SnP entry row or key row.</summary>
    public (int I, int J)? Entry { get; private init; }

    /// <summary>Every key the row stands for.</summary>
    public IReadOnlyList<string> Keys { get; private init; } = [];

    /// <summary>The keys stepped by a percent, dB or degrees — what Add tolerance… gives a tolerance.</summary>
    public IReadOnlyList<string> StepKeys { get; private init; } = [];

    /// <summary>The keys Tuning and the Optimizer may take: none of an SnP entry's (an offset is one number at every
    /// frequency — statistical only, tuning.snp.statistical-only).</summary>
    public IReadOnlyList<string> DesignKeys { get; private init; } = [];

    public bool CanAddTolerance => StepKeys.Count > 0;
    public bool CanTuneOrOptimize => DesignKeys.Count > 0;

    /// <summary>Indentation under an expanded Instance row.</summary>
    public int Depth { get; private init; }

    public double Indent => 14 * Depth;

    // ---- Status (D6) ---------------------------------------------------------------

    public SensitivityStatus Status { get; private init; }

    public bool IsNonlinear => Status == SensitivityStatus.Nonlinear;
    public bool IsOneSided  => Status == SensitivityStatus.OneSided;
    public bool IsFailed    => Status == SensitivityStatus.Failed;

    /// <summary>The glyph's tooltip: the reason, in the run's words.</summary>
    public string? StatusTip { get; private init; }

    // ---- Expand (Instance) -----------------------------------------------------------

    [ObservableProperty] private bool _canExpand;
    [ObservableProperty] private bool _isExpanded;

    internal void SetExpander(bool can, bool expanded) { CanExpand = can; IsExpanded = can && expanded; }

    [RelayCommand] private void ToggleExpand() => _panel.ToggleExpanded(this);

    // ---- An instance left out (R-sens3-5) ---------------------------------------------

    public bool IsExcluded { get; private init; }

    /// <summary>The Include box on the excluded row itself — the same setting as the S-parameter blocks row.</summary>
    [ObservableProperty] private bool _isIncluded;

    partial void OnIsIncludedChanged(bool value)
    {
        if (!_syncing && IsExcluded) _panel.SetSnpIncluded(Instance, value);
    }

    internal void SyncIncluded()
    {
        _syncing = true;
        IsIncluded = _panel.IsSnpIncluded(Instance);
        _syncing = false;
    }

    // ---- Actions ---------------------------------------------------------------------

    [RelayCommand] private void AddTolerance() => _panel.AddTolerance(this);
    [RelayCommand] private void SendToTuning() => _panel.SendToTuning(this);
    [RelayCommand] private void UseInOptimizer() => _panel.UseInOptimizer(this);

    // ---- Construction ----------------------------------------------------------------

    internal static SensitivityRowViewModel Of(YieldPanelViewModel panel, string id, SensitivityRollup r,
                                               IReadOnlyList<SensitivityContributor> members, int depth, string unit)
    {
        var snp = members.Select(c => c.Factor.Snp).OfType<CircuitRF.Design.Optimization.SnpEntryInfo>().ToList();
        (int, int)? entry = snp.Count > 0 && members.All(c => c.Factor.Snp is { } s && s.I == snp[0].I && s.J == snp[0].J)
            ? (snp[0].I, snp[0].J) : null;
        return new SensitivityRowViewModel(panel, id)
        {
            Label = r.Label, Basis = BasisOf(members), DeltaText = Delta(r.Delta, unit, signed: members.Count == 1), Share = r.Share,
            Instance = r.Instance, Port = r.Port, Entry = entry, Depth = depth,
            Keys = [.. members.Select(c => c.Factor.Key)],
            StepKeys = [.. members.Where(c => c.Factor.Basis != SensitivityBasis.Sigma).Select(c => c.Factor.Key)],
            DesignKeys = [.. members.Where(c => c.Factor.Snp is null).Select(c => c.Factor.Key)],
            Status = Worst(members), StatusTip = Tip(members),
        };
    }

    internal static SensitivityRowViewModel OfKey(YieldPanelViewModel panel, string id, SensitivityContributor c, int depth, string unit)
        => new(panel, id)
        {
            Label = c.Factor.Snp is { } s ? (s.Kind == RfCore.SnpOffsetKind.Db ? "dB" : "phase") : c.Factor.Label,
            Basis = c.Factor.BasisText, DeltaText = Delta(c.Delta, unit), Share = c.Share,
            Instance = c.Factor.Instance, Entry = c.Factor.Snp is { } e ? (e.I, e.J) : null, Depth = depth,
            Keys = [c.Factor.Key],
            StepKeys = c.Factor.Basis == SensitivityBasis.Sigma ? [] : [c.Factor.Key],
            DesignKeys = c.Factor.Snp is null ? [c.Factor.Key] : [],
            Status = c.Status, StatusTip = Tip([c]),
        };

    internal static SensitivityRowViewModel Excluded(YieldPanelViewModel panel, SensitivityExcluded x)
    {
        var row = new SensitivityRowViewModel(panel, "x:" + x.Instance)
        {
            Label = x.Instance, Basis = "excluded", Instance = x.Instance, IsExcluded = true,
            StatusTip = $"Left out of this run: {x.Factors} factor{(x.Factors == 1 ? "" : "s")} not stepped",
        };
        row.SyncIncluded();
        return row;
    }

    private static string BasisOf(IReadOnlyList<SensitivityContributor> members)
        => string.Join(" · ", members.OrderBy(c => c.Factor.Basis switch
                                     {
                                         SensitivityBasis.Sigma => 0, SensitivityBasis.Percent => 1, SensitivityBasis.Decibel => 2, _ => 3,
                                     })
                                     .Select(c => c.Factor.BasisText).Distinct(StringComparer.Ordinal));

    /// <summary>One key's Δ carries its sign; a group's is √ΣΔ², which has none.</summary>
    private static string Delta(double d, string unit, bool signed = true)
        => !double.IsFinite(d) ? "—"
         : (signed ? d > 0 ? "+" : d < 0 ? "−" : "" : "") + Math.Abs(d).ToString("G3", CultureInfo.InvariantCulture) + (unit.Length > 0 ? " " + unit : "");

    /// <summary>A group's standing: failed when every key failed, else one-sided when any is, else nonlinear when any is.</summary>
    private static SensitivityStatus Worst(IReadOnlyList<SensitivityContributor> members)
        => members.Count == 0 ? SensitivityStatus.Ok
         : members.All(c => c.Status == SensitivityStatus.Failed) ? SensitivityStatus.Failed
         : members.Any(c => c.Status is SensitivityStatus.OneSided or SensitivityStatus.Failed) ? SensitivityStatus.OneSided
         : members.Any(c => c.Status == SensitivityStatus.Nonlinear) ? SensitivityStatus.Nonlinear
         : SensitivityStatus.Ok;

    private static string? Tip(IReadOnlyList<SensitivityContributor> members)
    {
        var lines = members.Where(c => c.Status != SensitivityStatus.Ok).Select(c =>
        {
            string who = members.Count > 1 ? c.Factor.Key + ": " : "";
            return c.Status switch
            {
                SensitivityStatus.Nonlinear => $"{who}nonlinear — the + and − steps disagree (asymmetry {c.Asymmetry.ToString("0.00", CultureInfo.InvariantCulture)}); a DOE (ccf) or a Monte Carlo sees it",
                SensitivityStatus.OneSided  => $"{who}one-sided — {c.Reason}",
                _                           => $"{who}failed — {c.Reason}",
            };
        }).ToList();
        return lines.Count == 0 ? null : string.Join(Environment.NewLine, lines);
    }
}

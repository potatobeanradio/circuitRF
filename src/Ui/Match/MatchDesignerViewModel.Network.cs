using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CircuitRF.Core.Matching;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.Matching;

/// <summary>
/// Everything the status strip states, always (match.md §9.7). A refusal appears HERE, with its
/// numbers, and the affected termination turns red.
/// </summary>
/// <param name="Q1">Termination 1's Q at band centre.</param>
/// <param name="Q2">Termination 2's Q at band centre.</param>
/// <param name="WorstReturnLossDb">Worst in-band |S11| in dB (negative; -20 is a good match).</param>
/// <param name="InsertionLossDb">In-band insertion loss, dB positive.</param>
/// <param name="RippleDb">In-band |S21| ripple, dB.</param>
/// <param name="Achieved">The product of N² the transforms reached.</param>
/// <param name="Required">The product they had to reach.</param>
/// <param name="OnTarget">True when the two agree to a relative 1e-9.</param>
/// <param name="Refusal">MN-1's refusal, or null.</param>
public sealed record MatchStatus(
    double Q1, double Q2, double WorstReturnLossDb, double InsertionLossDb, double RippleDb,
    double Achieved, double Required, bool OnTarget, MatchRefusal? Refusal)
{
    /// <summary>Nothing to say yet.</summary>
    public static MatchStatus Empty { get; } = new(0, 0, 0, 0, 0, 1, 1, true, null);

    /// <summary>True when a refusal is in force and the numeric readouts are meaningless.</summary>
    public bool IsRefused => Refusal is not null;

    /// <summary>Which termination the refusal is about, or null.</summary>
    public int? FlaggedEnd => Refusal?.End;

    private static string F(double v, string fmt) => v.ToString(fmt, CultureInfo.InvariantCulture);

    /// <summary>The Q line.</summary>
    public string QText => $"Q1 {F(Q1, "0.###")}   Q2 {F(Q2, "0.###")}";

    /// <summary>The match line. Return loss is quoted positive, the way an RF engineer says it.</summary>
    public string ReturnLossText => IsRefused ? "" : $"worst RL {F(-WorstReturnLossDb, "0.00")} dB";

    /// <summary>The loss line.</summary>
    public string LossText => IsRefused
        ? ""
        : $"IL {F(InsertionLossDb, "0.000")} dB, ripple {F(RippleDb, "0.000")} dB";

    /// <summary>The transform-product line, with the tick or the cross §4.8 asks for.</summary>
    public string RatioText =>
        $"Π N² {F(Achieved, "0.###")} / {F(Required, "0.###")}  " + (OnTarget ? "✔ matched" : "✘ not reached");

    /// <summary>The whole strip as one line — what a test and a tooltip both read.</summary>
    public string Text => IsRefused
        ? $"{QText}   —   {Refusal!.Message}"
        : string.Join("   ", QText, ReturnLossText, LossText, RatioText);
}

public sealed partial class MatchDesignerViewModel
{
    /// <summary>The grid view's rows — one per element of the finished ladder.</summary>
    public ObservableCollection<MatchElementRowViewModel> Elements { get; } = [];

    /// <summary>The schematic view's geometry.</summary>
    [ObservableProperty] private MatchLadderLayout _ladder = MatchLadderLayout.Empty;

    /// <summary>The network pane's segmented control: false = schematic, true = value grid.</summary>
    [ObservableProperty] private bool _showGrid;

    /// <summary>The status strip.</summary>
    [ObservableProperty] private MatchStatus _status = MatchStatus.Empty;

    /// <summary>Everything the rebuild wanted to say that is not a refusal — dropped transforms,
    /// clamped N's, a fingerprint mismatch.</summary>
    public ObservableCollection<string> Notes { get; } = [];

    /// <summary>
    /// The legend the schematic view shows when anything is absorbed. One line, because the brief is
    /// explicit that the dimming must not be left to be inferred.
    /// </summary>
    public string LadderLegend => Ladder.HasAbsorbed
        ? "Dimmed elements are supplied by the external terminations — this component does not contain them."
        : "";

    /// <summary>The grid's clipboard form (§9.3), and the component-listing export's own rows.</summary>
    public string ElementsCsv => MatchElementRowViewModel.ToCsv(Elements);

    private void RefreshLadderAndGrid()
    {
        var network = _rebuild?.Network;

        Ladder = MatchLadderLayout.Build(network, _rebuild?.Applied, ValueTextFor);

        Elements.Clear();
        if (network is not null)
        {
            string instance = InstanceName.Length > 0 ? InstanceName : "MN1";
            foreach (var e in network.Elements)
            {
                var quantity = MatchValueFormat.QuantityOf(e.Type);
                var (text, unit) = MatchValueFormat.Format(
                    e.Value, quantity, Settings.UnitFor(quantity), Settings.SignificantDigits);
                Elements.Add(new MatchElementRowViewModel(
                    $"{instance}.{e.Name}", e.Name, e.Type, e.IsShunt, e.Value,
                    MatchLadderLayout.RoleOf(e), text, unit));
            }
        }

        // The payload error is NOT repeated here: it has its own line at the top of the
        // specification pane, where the thing it is about lives.
        Notes.Clear();
        foreach (string n in _rebuild?.Notes ?? []) Notes.Add(n);

        OnPropertyChanged(nameof(LadderLegend));
        OnPropertyChanged(nameof(ElementsCsv));
    }

    /// <summary>
    /// The grid's sort (§9.3). One click sorts ascending, a second on the same column reverses.
    /// </summary>
    /// <remarks>
    /// <b>Sorting is a display order and nothing more</b> — it never touches the ladder, which is
    /// ordered by position and whose order is the topology. That is why it re-sorts the row
    /// collection rather than the network.
    /// </remarks>
    public void SortElements(string column)
    {
        if (string.Equals(column, ElementSortColumn, StringComparison.Ordinal))
            ElementSortAscending = !ElementSortAscending;
        else
        {
            ElementSortColumn = column;
            ElementSortAscending = true;
        }

        Func<MatchElementRowViewModel, IComparable> key = column switch
        {
            "type"  => r => r.TypeText,
            "value" => r => r.Value,
            "unit"  => r => r.Unit,
            "note"  => r => r.Note,
            _       => r => r.Instance,
        };

        var sorted = ElementSortAscending
            ? Elements.OrderBy(key).ToList()
            : Elements.OrderByDescending(key).ToList();
        Elements.Clear();
        foreach (var r in sorted) Elements.Add(r);
    }

    /// <summary>Which column the grid is sorted on.</summary>
    [ObservableProperty] private string _elementSortColumn = "";

    /// <summary>Sort direction.</summary>
    [ObservableProperty] private bool _elementSortAscending = true;

    private string ValueTextFor(MatchElement e)
    {
        var quantity = MatchValueFormat.QuantityOf(e.Type);
        return MatchValueFormat.FormatWithUnit(
            e.Value, quantity, Settings.UnitFor(quantity), Settings.SignificantDigits);
    }

    private void RefreshStatus()
    {
        double om0 = _design.Omega0;
        double q1 = _design.Term1.QAt(om0), q2 = _design.Term2.QAt(om0);
        var network = _rebuild?.Network;

        if (network is null)
        {
            Status = new MatchStatus(q1, q2, 0, 0, 0, 1, 1, false, _rebuild?.Refusal);
        }
        else
        {
            double worst = MatchResponse.WorstReturnLossDb(network, _design.F1, _design.F2);
            var (il, ripple) = MatchResponse.InsertionLoss(network, _design.F1, _design.F2);
            Status = new MatchStatus(
                q1, q2, worst, il, ripple,
                _rebuild!.Achieved, _rebuild.Required, _rebuild.OnTarget, null);
        }

        // The affected termination turns red — and ONLY the affected one. A refusal about neither end
        // (no transformable pair, an invalid order) flags nothing, which is the honest rendering.
        int? flagged = Status.FlaggedEnd;
        if (flagged is null && network is not null && !Status.OnTarget)
        {
            // "The transform product cannot reach the target" is about the FAR end by construction —
            // the analysis end's resistance is never rescaled by a transform (§4.8).
            flagged = _rebuild!.Basis.AnalysisIsTerm1 ? 2 : 1;
        }
        Term1.IsFlagged = flagged == 1;
        Term2.IsFlagged = flagged == 2;
    }

    // ── Solutions ─────────────────────────────────────────────────────────────

    /// <summary>The solutions panel's rows, in MN-1's own order: fewest transforms, then position,
    /// then Q-adjust.</summary>
    public ObservableCollection<MatchSolutionRowViewModel> Solutions { get; } = [];

    /// <summary>Whether the docked solutions list is out.</summary>
    [ObservableProperty] private bool _solutionsPanelOpen;

    /// <summary>The footer's own line: "3 solutions · applied: 2-transform, Fano".</summary>
    [ObservableProperty] private string _solutionsSummary = "";

    /// <summary>Non-empty when the search has nothing to offer — "No solutions available for order 4",
    /// said plainly, with the numbers behind it.</summary>
    [ObservableProperty] private string _solutionsRefusal = "";

    private void RefreshSolutions()
    {
        Solutions.Clear();

        var set = MatchSolutionSearch.Search(
            _design, Settings.OfferQAdjustedSolutions, Settings.QMin);

        string current = MatchSolutionSearch.SolutionFingerprint(_design, _design.Transforms);
        foreach (var s in set.Solutions)
        {
            var badge =
                string.Equals(s.Fingerprint, current, StringComparison.Ordinal) ? MatchSolutionBadge.Current
                : _design.AppliedSolutions.Contains(s.Fingerprint, StringComparer.Ordinal)
                    ? MatchSolutionBadge.PreviouslyApplied
                    : MatchSolutionBadge.NeverApplied;
            Solutions.Add(new MatchSolutionRowViewModel(this, s, badge, _design.Response));
        }

        SolutionsRefusal = set.Refusal is { } r
            ? $"No solutions available for order {_design.Order}. {r.Message}"
            : "";

        var applied = Solutions.FirstOrDefault(s => s.Badge == MatchSolutionBadge.Current);
        string appliedText = applied is null
            ? _design.Transforms.Count == 0 ? "applied: none" : "applied: a hand-set transform set"
            : $"applied: {applied.CountText}, {ResponseShortName(applied.Response)}";

        SolutionsSummary = Solutions.Count == 0
            ? "no solutions"
            : $"{Solutions.Count} solution{(Solutions.Count == 1 ? "" : "s")} · {appliedText}";
    }

    private static string ResponseShortName(ResponseShape shape) => shape switch
    {
        ResponseShape.ChebyshevFano      => "Fano",
        ResponseShape.ChebyshevTwoEnded  => "two-ended",
        ResponseShape.Butterworth        => "Butterworth",
        _                                => "Bessel",
    };

    /// <summary>
    /// Applies one solution: its transforms, its Q-adjust, and its fingerprint into
    /// <see cref="MatchDesign.AppliedSolutions"/> — which is what makes the "previously applied" badge
    /// survive a reload.
    /// </summary>
    public void ApplySolution(MatchSolutionRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        _design.Transforms = [.. row.Solution.Transforms];
        _design.QAdjust = row.Solution.QAdjust;
        if (!_design.AppliedSolutions.Contains(row.Solution.Fingerprint, StringComparer.Ordinal))
            _design.AppliedSolutions.Add(row.Solution.Fingerprint);

        Refresh(specChanged: true);
        Commit();
    }

    // ── Response feasibility ──────────────────────────────────────────────────

    private void RefreshResponseOptions()
    {
        foreach (var option in ResponseOptions)
        {
            option.IsSelected = option.Shape == _design.Response;

            var probe = _design.Clone();
            probe.Response = option.Shape;
            var result = MatchSynthesis.Synthesize(probe);

            option.Refusal = result.Refusal;
            option.IsEnabled = result.Ok;
            // The refusal already carries its numbers (MN-1 makes that a rule); rendering them is all
            // that is left to do, and recomputing them here would be a second opinion nobody asked for.
            option.Tooltip = result.Ok ? option.Description : result.Refusal!.Message;
        }
        OnPropertyChanged(nameof(ResponseOptions));
    }

    // ── Flatten — MN-5 ────────────────────────────────────────────────────────

    /// <summary>
    /// Whether Flatten to Cell can run, and why not when it cannot. Recomputed on every
    /// <see cref="Refresh"/> rather than on every property read: it touches the filesystem (does the
    /// workspace root exist?) and re-synthesises the design, and a binding reads a property far more
    /// often than a design changes.
    /// </summary>
    public MatchFlattenAvailability FlattenAvailability { get; private set; } =
        new(false, "Flatten to Cell acts on a Match component.", null, "MN_match");

    /// <summary>True when the footer's Flatten button is live.</summary>
    public bool CanFlatten => FlattenAvailability.CanRun;

    /// <summary>What the Flatten button offers, or the one thing standing in its way.</summary>
    public string FlattenTooltip => FlattenAvailability.Reason;

    private void RefreshFlatten()
    {
        FlattenAvailability = MatchFlattenService.Availability(_schematicVm, _target);
        OnPropertyChanged(nameof(FlattenAvailability));
        OnPropertyChanged(nameof(CanFlatten));
        OnPropertyChanged(nameof(FlattenTooltip));
    }

    /// <summary>
    /// Runs one flatten against this Designer's own target — the single path both entry points take
    /// (brief §4), so the footer button and the schematic's context menu cannot diverge.
    /// </summary>
    public MatchFlattenService.RunResult Flatten(string parentDir, string cellName, bool replaceInPlace)
    {
        if (_schematicVm is null || _target is null)
            return new(false, "This Designer is not bound to a component.", null, null);

        var result = MatchFlattenService.Run(_schematicVm, _target, parentDir, cellName, replaceInPlace);
        if (result.Ok) _schematicVm.MessageSink?.Success(result.Message);
        else _schematicVm.MessageSink?.Warning(result.Message);
        RefreshFlatten();
        return result;
    }
}

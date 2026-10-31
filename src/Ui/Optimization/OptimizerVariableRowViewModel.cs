// ================================================================
//  OptimizerVariableRowViewModel.cs  —  one variable entry in the
//  Optimizer's list (brief-tuneopt-10 R-to10-2, R-to10-10)
//
//  The row is the SAME entry the Tuning panel shows (overview D4): its
//  check is the entry's opt flag, its range is the entry's range, and
//  a range edit here is a range edit there. What the row adds is the
//  run's view of it — the best value, marked on the range bar; its
//  sensitivity, once asked; and the railed mark (D17) with Widen.
//
//  A part of a complex value (D18) shows its view of the run's ONE
//  decoded complex value, so a part that only tunes still shows the
//  value the optimized parts gave it.
//
//  A placed vendor part (brief vendorlib-4 R-vl4-5b) reads as parts:
//  its candidate count beside the key, the candidates' value span as
//  its range, and the best part's number as its value.
// ================================================================

using System;
using System.Globalization;
using System.Linq;
using System.Numerics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Core.Design;
using CircuitRF.Design.Optimization;
using CircuitRF.Ui.Tuning;

namespace CircuitRF.Ui.Optimization;

public sealed partial class OptimizerVariableRowViewModel : ObservableObject
{
    private readonly OptimizerPanelViewModel _panel;
    private bool _syncing;

    internal OptimizerVariableRowViewModel(OptimizerPanelViewModel panel, string key)
    {
        _panel = panel;
        Key    = key;
    }

    public string Key { get; }

    public Tunable? Tunable { get; private set; }

    public string Location => Tunable is { Cell: not null } t ? t.Location : "";

    public bool HasLocation => Location.Length > 0;

    public string Unit => Tunable?.Unit ?? "";

    /// <summary>The part and its candidates when the row is a vendor part's <c>Part</c>; null otherwise.</summary>
    public CircuitRF.Design.VendorLibraries.VendorPartTunable? VendorPart => Tunable?.VendorPart;

    public bool IsPart => VendorPart is not null;

    public bool IsNumeric => !IsPart;

    /// <summary>What follows the best value: its unit, or nothing for a part (its number says it).</summary>
    public string UnitText => IsPart ? "" : Unit;

    /// <summary>The best part in full — number, value, Description — for the tooltip; null for a number row.</summary>
    public string? BestTip { get; private set; }

    private string? _bestPartText;

    /// <summary>Why the optimizer cannot move it (a parametric sweep sweeps it, or the key names
    /// nothing); null when it can.</summary>
    public string? DisabledReason { get; private set; }

    public double Min { get; private set; }
    public double Max { get; private set; }
    public TuneScale Scale { get; private set; }

    public bool IsLog => TunableValue.Effective(Scale, Min, Max) == TuneScale.Log;

    /// <summary>The entry's <c>discrete=</c>.</summary>
    public TuneDiscrete Discrete { get; private set; }

    /// <summary>The entry's step as written; null when it has none.</summary>
    public string? StepText { get; private set; }

    /// <summary>What values the run may give it, when not any: <c>E24</c>, <c>step 1 nH</c>, <c>integer</c>.
    /// Empty for a continuous value — the Discrete algorithm's refusal names the rows without this.</summary>
    public string DiscreteText => VendorPart is { } vp
        ? vp.Candidates.Count == 1 ? "1 part" : $"{vp.Candidates.Count:N0} parts"
        : Discrete switch
    {
        TuneDiscrete.None when StepText is { } st => $"step {st}",
        TuneDiscrete.None                         => "",
        TuneDiscrete.Integer                      => "integer",
        var d                                     => TunableValue.DiscreteLabel(d),
    };

    public bool HasDiscreteText => DiscreteText.Length > 0;

    public bool IsContinuous => Discrete == TuneDiscrete.None && StepText is null;
    public bool IsE6         => Discrete == TuneDiscrete.E6;
    public bool IsE12        => Discrete == TuneDiscrete.E12;
    public bool IsE24        => Discrete == TuneDiscrete.E24;
    public bool IsE48        => Discrete == TuneDiscrete.E48;
    public bool IsE96        => Discrete == TuneDiscrete.E96;
    public bool IsPreferred  => Discrete == TuneDiscrete.Preferred;
    public bool IsSeries     => Discrete == TuneDiscrete.Preferred || TunableValue.Series.Contains(Discrete);

    /// <summary>A series is offered: the row is a whole value, not a part (overview D18).</summary>
    public bool CanSeries => Tunable is { Part: null, IsVendorPart: false };

    /// <summary>The Preferences ladder is offered: the unit names a capacitance, inductance or resistance.</summary>
    public bool CanPreferred => Tunable is { } t && TunableValue.DiscreteChoices(t).Contains(TuneDiscrete.Preferred);

    // A part's range is its candidates' value span; a bound typed here bounds the stated value.
    public string MinText => VendorPart is { Candidates.Count: > 0 } vp ? vp.Candidates[0].ValueText : TuningRowViewModel.FormatValue(Min, "");
    public string MaxText => VendorPart is { Candidates.Count: > 0 } vp ? vp.Candidates[^1].ValueText : TuningRowViewModel.FormatValue(Max, "");

    /// <summary>The entry's opt flag.</summary>
    [ObservableProperty] private bool _isOptimized;

    partial void OnIsOptimizedChanged(bool value)
    {
        if (!_syncing) _panel.SetOptimized(this, value);
    }

    // ---- The run's view -------------------------------------------------------

    private double _best;

    /// <summary>The best point's value, in <see cref="Unit"/> — the schematic's before any run.</summary>
    public double Best => _best;

    public string BestText => _bestPartText ?? TuningDigits.Format(_best, "", _panel.Digits);

    /// <summary>Where <see cref="Best"/> sits on the range bar, 0..1.</summary>
    public double BestPosition => TuningSliderMapping.ToPosition(_best, Min, Max, Scale);

    /// <summary><c>s 0.41</c> — this variable's share of the cost's sensitivity; empty until asked.</summary>
    [ObservableProperty] private string _sensitivityText = "";

    /// <summary>The end of the range the best value has railed against (D17); null when it has not.</summary>
    [ObservableProperty] private RailEnd? _railed;

    /// <summary>Another part whose range holds this one at the edge (D18); null when it is its own.</summary>
    [ObservableProperty] private string? _railedAgainst;

    public bool IsRailed => Railed is not null;
    public bool IsRailedMin => Railed == RailEnd.Min;
    public bool IsRailedMax => Railed == RailEnd.Max;

    /// <summary><c>max</c>, or <c>max of mag(ZL)</c> when another part's range holds it.</summary>
    public string RailedText => Railed is not { } e ? "" :
        (e == RailEnd.Min ? "min" : "max") + (RailedAgainst is { } a ? $" of {a}" : "");

    partial void OnRailedChanged(RailEnd? value)
    {
        OnPropertyChanged(nameof(IsRailed));
        OnPropertyChanged(nameof(IsRailedMin));
        OnPropertyChanged(nameof(IsRailedMax));
        OnPropertyChanged(nameof(RailedText));
        OnPropertyChanged(nameof(ShowWiden));
        WidenCommand.NotifyCanExecuteChanged();
    }

    partial void OnRailedAgainstChanged(string? value) => OnPropertyChanged(nameof(RailedText));

    internal void Bind(TunableEntry entry, Tunable? tunable)
    {
        _entryMin = entry.Min;
        _entryMax = entry.Max;
        if (tunable?.VendorPart is { } part)
        {
            // The candidates' indices, in value order — what the run's best is placed on the bar by.
            Tunable = tunable;
            DisabledReason = tunable.DisabledReason;
            Min = 0;
            Max = Math.Max(1, part.Candidates.Count - 1);
            Scale = TuneScale.Lin;
            Discrete = TuneDiscrete.None;
            StepText = null;
            _syncing = true;
            IsOptimized = entry.Opt;
            _syncing = false;
            ShowBest(null);
            OnPropertyChanged(string.Empty);
            return;
        }
        Tunable = tunable;
        string u = tunable?.Unit ?? "";
        DisabledReason = tunable is null ? "not in the design" : tunable.DisabledReason;
        Min   = TunableValue.InUnit(entry.Min, u) ?? TunableValue.InUnit(tunable?.DefaultMin, u) ?? 0;
        Max   = TunableValue.InUnit(entry.Max, u) ?? TunableValue.InUnit(tunable?.DefaultMax, u) ?? 1;
        if (!(Max > Min)) Max = Min + 1;
        Scale = entry.Scale == TuneScale.Auto && tunable?.Part == ComplexPart.Phase ? TuneScale.Lin : entry.Scale;
        Discrete = entry.Discrete;
        StepText = string.IsNullOrWhiteSpace(entry.Step) ? null : entry.Step;
        _syncing = true;
        IsOptimized = entry.Opt;
        _syncing = false;
        OnPropertyChanged(string.Empty);
    }

    /// <summary>Shows the value this row takes at <paramref name="values"/> (value key → text): a part
    /// reads its view of the whole complex value; a key the values do not name shows the schematic's.</summary>
    internal void ShowBest(System.Collections.Generic.IReadOnlyDictionary<string, string>? values)
    {
        if (Tunable is not { } t) return;
        if (t.VendorPart is { } vp)
        {
            // The best part — or, while a continuous stage runs, the IDEAL value standing in for it.
            string partText = values?.GetValueOrDefault(t.ValueKey) ?? vp.PlacedCell;
            if (vp.CandidateOfCell(partText) is { } c)
            {
                _best = vp.Candidates.ToList().IndexOf(c);
                _bestPartText = c.Part;
                BestTip = CircuitRF.Design.VendorLibraries.VendorPartTunable.Describe(c.Part, c.ValueText, c.Description);
            }
            else
            {
                _best = vp.StartIndex;
                _bestPartText = $"ideal {CircuitRF.Design.VendorLibraries.VendorPartCandidates.ProxyDisplay(partText)}";
                BestTip = "An ideal component stands in for the part while the continuous stage runs; the run ends on a real part.";
            }
            OnPropertyChanged(nameof(Best));
            OnPropertyChanged(nameof(BestText));
            OnPropertyChanged(nameof(BestTip));
            OnPropertyChanged(nameof(BestPosition));
            return;
        }
        _bestPartText = null;
        BestTip = null;
        double v = t.Value;
        if (values is not null && values.TryGetValue(t.ValueKey, out var text))
        {
            if (t.Part is { } part)
            {
                if (ComplexValue.TryParse(text, out Complex z, out _, out _))
                    v = ComplexValue.Get(z, part, phaseNear: (Min + Max) / 2);
            }
            else if (TunableValue.InUnit(text, t.Unit) is { } n)
            {
                v = n;
            }
        }
        _best = v;
        OnPropertyChanged(nameof(Best));
        OnPropertyChanged(nameof(BestText));
        OnPropertyChanged(nameof(BestPosition));
    }

    internal void ShowRailed(RailedVariable? railed)
    {
        Railed        = railed?.End;
        RailedAgainst = railed?.Against;
    }

    /// <summary>Doubles the span on the railed side — by ratio on a log range — as one undo step.</summary>
    [RelayCommand(CanExecute = nameof(CanWiden))]
    private void Widen() => _panel.Widen(this);

    /// <summary>A railed number always widens; a railed part only while a value bound holds it there.</summary>
    private bool CanWiden() => IsRailed && (!IsPart || (Railed == RailEnd.Min ? _entryMin : _entryMax) is not null);

    private string? _entryMin, _entryMax;

    /// <summary>Widen is shown only when it can act — a part at the end of the library's own candidates has nothing
    /// to widen, and its railed mark says so alone.</summary>
    public bool ShowWiden => CanWiden();

    /// <summary>Widen's tooltip: what it does to this row.</summary>
    public string WidenTip => IsPart ? "Remove the value bound on this side, so the candidates reach further (undoable)"
                                     : "Double the range on this side (undoable)";

    [RelayCommand] private void Remove() => _panel.RemoveVariable(this);

    /// <summary>Any value in the range: no step, no series (an integer parameter stays integer).</summary>
    [RelayCommand]
    private void SetContinuous() => _panel.EditRange(this, e =>
    {
        e.Step = null;
        e.Discrete = Tunable?.IsInteger == true ? TuneDiscrete.Integer : TuneDiscrete.None;
    }, "Optimize continuously");

    /// <summary>Only the values of an E series, or of the Preferences ladder (<c>Preferred</c>) — the same entry
    /// the Tuning slider steps along.</summary>
    [RelayCommand]
    private void SetSeries(string token)
    {
        if (!Enum.TryParse<TuneDiscrete>(token, ignoreCase: true, out var d) || d is TuneDiscrete.None or TuneDiscrete.Integer) return;
        _panel.EditRange(this, e =>
        {
            e.Discrete = d;
            e.Step     = null;
            if (Min > 0) e.Scale = TuneScale.Log;
        }, $"Optimize over {TunableValue.DiscreteLabel(d)} values");
    }

    // ---- Range edits (the same entry the Tuning panel edits) -------------------

    public void CommitMin(string text)
    {
        if (IsPart) { CommitPartBound(text, (e, v) => e.Min = v); return; }
        if (TunableValue.InUnit(text, Unit) is { } v && v < Max)
            _panel.EditRange(this, e => e.Min = TuningRowViewModel.FormatValue(v, Unit), "Set tuning range");
        else OnPropertyChanged(nameof(MinText));
    }

    public void CommitMax(string text)
    {
        if (IsPart) { CommitPartBound(text, (e, v) => e.Max = v); return; }
        if (TunableValue.InUnit(text, Unit) is { } v && v > Min)
            _panel.EditRange(this, e => e.Max = TuningRowViewModel.FormatValue(v, Unit), "Set tuning range");
        else OnPropertyChanged(nameof(MaxText));
    }

    /// <summary>A part's bound: on its candidates' STATED value, in the part's unit; blank removes it.</summary>
    private void CommitPartBound(string text, Action<TunableEntry, string?> set)
    {
        string t = text.Trim();
        if (t.Length == 0) { _panel.EditRange(this, e => set(e, null), "Clear part bound"); return; }
        if (TunableValue.InUnit(t, VendorPart!.Unit) is { } v && v > 0)
            _panel.EditRange(this, e => set(e, TunableValue.TryParse(t, out _, out string u, out _) && u.Length > 0
                                                   ? t : TuningRowViewModel.FormatValue(v, VendorPart.Unit)), "Set part bound");
        else { OnPropertyChanged(nameof(MinText)); OnPropertyChanged(nameof(MaxText)); }
    }

    internal static string Share(double share) => "s " + share.ToString("0.00", CultureInfo.InvariantCulture);
}

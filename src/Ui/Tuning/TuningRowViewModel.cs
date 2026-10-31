// ================================================================
//  TuningRowViewModel.cs  —  one tune-enabled entry as a slider row
//  (brief-tuneopt-4 R-to4-4)
//
//  The VALUE is session state and lives here; the RANGE, scale and
//  step are the entry's and every change to them is handed back to
//  the panel as a document edit (R-to4-10). Values are in the
//  tunable's own unit — a pF row's 1.8 is 1.8 pF.
//
//  A row for one PART of a complex value (overview D18) does not own
//  its value: the panel holds the whole complex value, and every part
//  row of it shows its own view of that one number. Moving the row
//  asks the panel, which moves the value along this part's path and
//  stops it at the edge of the ranges of all its parts.
//
//  A placed vendor part's row (brief vendorlib-4 R-vl4-5a) steps
//  through its CANDIDATE parts in value order: its value is the
//  candidate's index, its text the part number, and its range is the
//  candidate list itself — chosen as the same kind or by a search,
//  narrowed by min/max on the stated value.
// ================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Optimization;

namespace CircuitRF.Ui.Tuning;

public sealed partial class TuningRowViewModel : ObservableObject
{
    private readonly TuningPanelViewModel _panel;
    private bool _syncing;

    public TuningRowViewModel(TuningPanelViewModel panel, string key)
    {
        _panel = panel;
        Key    = key;
    }

    /// <summary>The entry's key (overview D3).</summary>
    public string Key { get; }

    /// <summary>The tunable it resolves to; null when the key names nothing in the design now.</summary>
    public Tunable? Tunable { get; private set; }

    /// <summary><c>DUT · ×2</c> for a sub-cell row; empty at the top.</summary>
    public string Location => Tunable is { Cell: not null } t ? t.Location : "";

    public bool HasLocation => Location.Length > 0;

    public string Unit => Tunable?.Unit ?? "";

    // ---- A placed vendor part (brief vendorlib-4 R-vl4-5a) ---------------------

    /// <summary>The part and its candidates when this row is a vendor part's <c>Part</c>; null otherwise.</summary>
    public CircuitRF.Design.VendorLibraries.VendorPartTunable? VendorPart => Tunable?.VendorPart;

    public bool IsPart => VendorPart is not null;

    /// <summary>A number row — every row but a vendor part's.</summary>
    public bool IsNumeric => !IsPart;

    /// <summary>The candidate the slider rests on, or null.</summary>
    public CircuitRF.Design.VendorLibraries.VendorPartCandidate? Candidate
        => VendorPart is { Candidates.Count: > 0 } vp ? vp.Candidates[Math.Clamp((int)Math.Round(_value), 0, vp.Candidates.Count - 1)] : null;

    /// <summary>What sits after the number box: the unit, or for a part its stated value (<c>10 pF</c>).</summary>
    public string UnitText => IsPart ? Candidate?.ValueText ?? "" : Unit;

    /// <summary>The part's candidates in one line — <c>12 candidates, 1 pF – 47 pF, series …</c>.</summary>
    public string CandidatesText => VendorPart?.Summary ?? "";

    /// <summary>How the candidates are chosen: <c>same kind</c>, or the search.</summary>
    public string SelectionText => _entryParts is { } q ? $"search: {q}" : "same kind";

    /// <summary>The candidates are the same kind as the placed part (no search).</summary>
    public bool IsSameKind => _entryParts is null;

    /// <summary>The value box's width: a part number is longer than a number.</summary>
    public double ValueBoxWidth => IsPart ? 116 : 64;

    /// <summary>The candidate under the slider in full — number, value, Description — for the tooltip.</summary>
    public string? CandidateTip => Candidate is { } c
        ? CircuitRF.Design.VendorLibraries.VendorPartTunable.Describe(c.Part, c.ValueText, c.Description) : null;

    private string? _entryParts;

    /// <summary>The part search under the row, editable in place.</summary>
    [ObservableProperty] private string _queryText = "";
    [ObservableProperty] private bool   _isEditingQuery;

    public bool IsInteger { get; private set; }

    /// <summary>Why the slider cannot move (a parametric sweep sweeps it, or the key names nothing);
    /// null when it can.</summary>
    public string? DisabledReason { get; private set; }

    public bool IsDisabled => DisabledReason is not null;

    public bool IsEnabled => DisabledReason is null;

    /// <summary>Why Push skips this row; null when it can write it.</summary>
    public string? ReadOnlyReason => Tunable?.ReadOnlyReason is { } r ? $"Push unavailable: {r}" : null;

    public bool IsReadOnly => ReadOnlyReason is not null;

    public double Min { get; private set; }
    public double Max { get; private set; }
    public TuneScale Scale { get; private set; }
    public double? Step { get; private set; }

    /// <summary>The range is the zero-value guess (overview D4).</summary>
    public bool RangeGuessed { get; private set; }

    /// <summary>"log", "lin" or the series (<c>E24</c>) — what the slider is actually doing.</summary>
    public string ScaleLabel => IsPart ? SelectionText : IsSeries ? TunableValue.DiscreteLabel(Discrete) : IsLog ? "log" : "lin";

    public bool IsLog => TunableValue.Effective(Scale, Min, Max) == TuneScale.Log;

    /// <summary>The entry's <c>discrete=</c>.</summary>
    public TuneDiscrete Discrete { get; private set; }

    /// <summary>The values a preferred or E-series row steps along, in <see cref="Unit"/>; null otherwise.</summary>
    public IReadOnlyList<double>? Levels { get; private set; }

    /// <summary>The row steps along a preferred-value ladder or an E series.</summary>
    public bool IsSeries => Discrete == TuneDiscrete.Preferred || TunableValue.Series.Contains(Discrete);

    // The ⋮ menu's one choice of how the slider moves — Linear, Log, Step or a series — and its tick.
    public bool IsModeLinear => !IsSeries && Step is null && !IsLog;
    public bool IsModeLog    => !IsSeries && Step is null && IsLog;
    public bool IsModeStep   => !IsSeries && Step is not null;
    public bool IsE6         => Discrete == TuneDiscrete.E6;
    public bool IsE12        => Discrete == TuneDiscrete.E12;
    public bool IsE24        => Discrete == TuneDiscrete.E24;
    public bool IsE48        => Discrete == TuneDiscrete.E48;
    public bool IsE96        => Discrete == TuneDiscrete.E96;
    public bool IsPreferred  => Discrete == TuneDiscrete.Preferred;

    /// <summary>A series is offered: the row is a whole value, not a part (overview D18).</summary>
    public bool CanSeries => Tunable is { Part: null, IsVendorPart: false };

    /// <summary>The Preferences ladder is offered: the unit names a capacitance, inductance or resistance.</summary>
    public bool CanPreferred => Tunable is { } t && TunableValue.DiscreteChoices(t).Contains(TuneDiscrete.Preferred);

    [ObservableProperty] private string _minText = "";
    [ObservableProperty] private string _maxText = "";
    [ObservableProperty] private string _stepText = "";
    [ObservableProperty] private bool   _isEditingMin;
    [ObservableProperty] private bool   _isEditingMax;
    [ObservableProperty] private bool   _isEditingStep;

    /// <summary>The sliders are ahead of the display for this row (R-to4-6).</summary>
    [ObservableProperty] private bool _isLagging;

    private double _value;

    /// <summary>The tuned value, in <see cref="Unit"/>.</summary>
    public double Value => _value;

    /// <summary>What the schematic holds, in <see cref="Unit"/>.</summary>
    public double SchematicValue => Tunable?.Value ?? _value;

    /// <summary>A part row: the whole complex value the session holds, or null while it is the
    /// schematic's.</summary>
    internal Complex? WholeValue { get; private set; }

    /// <summary>The tuned value differs from the schematic's — for a part row, the WHOLE value does,
    /// since moving the real part changes what the magnitude row's parameter holds too.</summary>
    public bool DiffersFromSchematic => VendorPart is { } vp
        ? Candidate is { } c && !c.Part.Equals(vp.PlacedPart, StringComparison.OrdinalIgnoreCase)
        : Tunable is { Part: not null } p
        ? WholeValue is { } z && z != p.Whole
        : Tunable is not null && _value != Tunable.Value;

    /// <summary>The middle of the range — which turn a phase row reads its angle in.</summary>
    private double Centre => (Min + Max) / 2;

    /// <summary>The value as the session and Push spell it, to the panel's digits: <c>1.8 pF</c>.</summary>
    public string ValueText => IsPart ? PartValueText : Spell(_value, Unit);

    /// <summary>A part row's value as the session evaluates it and Push writes it: the candidate's cell (the
    /// instance is rebound to it), or its part number when it was not emitted.</summary>
    private string PartValueText => Candidate is { } c ? (c.Cell.Length > 0 ? c.Cell : c.Part) : "";

    private string Spell(double v, string unit) => TuningDigits.Format(v, unit, _panel.Digits);

    /// <summary>The number box. Typing changes nothing until Enter (<see cref="CommitValueText"/>).</summary>
    [ObservableProperty] private string _valueBoxText = "";

    /// <summary>The slider, 0..1.</summary>
    public double Position
    {
        get => TuningSliderMapping.ToPosition(_value, Min, Max, Scale);
        set
        {
            if (_syncing || IsDisabled) return;
            double v = TuningSliderMapping.Snap(
                TuningSliderMapping.FromPosition(value, Min, Max, Scale), Min, Max, IsInteger, Step, Levels);
            SetValue(v, final: false);
        }
    }

    /// <summary>The slider was released: the value it rests on is the final one (D7).</summary>
    public void Release() { if (!IsDisabled) _panel.OnRowValueChanged(this, final: true); }

    // ---- Refresh from the entry and the catalog --------------------------------

    /// <summary>Reads the entry's range and the tunable; <paramref name="value"/> is the tuned value to
    /// show, or null to start from the schematic's.</summary>
    internal void Bind(TunableEntry entry, Tunable? tunable, double? value, Complex? whole = null)
    {
        if (tunable?.VendorPart is { } vendorPart) { BindPart(entry, tunable, vendorPart, value); return; }
        Tunable   = tunable;
        string u  = tunable?.Unit ?? "";
        IsInteger = tunable?.IsInteger == true || entry.Discrete == TuneDiscrete.Integer;
        DisabledReason = tunable is null ? "not in the design" : tunable.DisabledReason;

        Min = InUnit(entry.Min, u) ?? InUnit(tunable?.DefaultMin, u) ?? 0;
        Max = InUnit(entry.Max, u) ?? InUnit(tunable?.DefaultMax, u) ?? 1;
        if (!(Max > Min)) Max = Min + 1;
        // An angle's range is not a ratio: a phase reads Auto as linear (overview D18).
        Scale        = entry.Scale == TuneScale.Auto && tunable?.Part == ComplexPart.Phase ? TuneScale.Lin : entry.Scale;
        Step         = InUnit(entry.Step, u) is > 0 and var s ? s : null;
        Discrete     = tunable?.Part is null ? entry.Discrete : TuneDiscrete.None;
        Levels       = TunableValue.DiscreteLevels(Discrete, u, Min, Max, _panel.Ladders?.Invoke()) is { Count: > 0 } lv ? lv : null;
        RangeGuessed = tunable?.RangeGuessed == true && entry.Min == tunable.DefaultMin && entry.Max == tunable.DefaultMax;

        WholeValue = tunable?.Part is not null ? whole : null;
        _value = tunable?.Part is { } part
            ? ComplexValue.Get(whole ?? tunable.Whole, part, phaseNear: Centre)
            : value ?? tunable?.Value ?? Min;
        _syncing = true;
        MinText      = FormatValue(Min, "");
        MaxText      = FormatValue(Max, "");
        StepText     = Step is { } st ? FormatValue(st, "") : "";
        ValueBoxText = Spell(_value, "");
        _syncing = false;
        OnPropertyChanged(string.Empty);
    }

    /// <summary>
    /// A vendor part's row: the slider runs over the candidates' indices, the value is an index, and the bounds
    /// under it are the entry's own value bounds on the stated value — blank for none.
    /// </summary>
    private void BindPart(TunableEntry entry, Tunable tunable, CircuitRF.Design.VendorLibraries.VendorPartTunable part, double? value)
    {
        Tunable   = tunable;
        IsInteger = true;
        int k = part.Candidates.Count;
        DisabledReason = k == 0 ? "no candidate part — widen the bounds or the search"
                       : tunable.DisabledReason;
        Min = 0;
        Max = Math.Max(1, k - 1);
        Scale = TuneScale.Lin;
        Step = null;
        Discrete = TuneDiscrete.None;
        Levels = null;
        RangeGuessed = false;
        WholeValue = null;
        _entryParts = entry.Parts;
        _value = Math.Clamp(Math.Round(value ?? part.StartIndex), 0, Math.Max(0, k - 1));
        _syncing = true;
        MinText      = entry.Min ?? "no min";
        MaxText      = entry.Max ?? "no max";
        StepText     = "";
        QueryText    = entry.Parts ?? "";
        ValueBoxText = Candidate?.Part ?? "";
        _syncing = false;
        OnPropertyChanged(string.Empty);
    }

    /// <summary>Returns to the schematic's value without sending anything (Revert, a Push's echo).</summary>
    internal void ResetToSchematic()
    {
        if (VendorPart is { } vp) { _value = vp.StartIndex; RaiseValue(); return; }
        if (Tunable is null) return;
        WholeValue = null;
        _value = Tunable.Part is { } part ? ComplexValue.Get(Tunable.Whole, part, phaseNear: Centre) : Tunable.Value;
        RaiseValue();
    }

    /// <summary>A part row: shows its view of the whole value <paramref name="z"/>, sending nothing.</summary>
    internal void ShowWhole(Complex z)
    {
        if (Tunable?.Part is not { } part) return;
        WholeValue = z;
        _value = ComplexValue.Get(z, part, phaseNear: Centre);
        RaiseValue();
    }

    private void SetValue(double v, bool final)
    {
        if (Tunable?.Part is not null)
        {
            _panel.OnPartMoved(this, v, final);
            return;
        }
        if (v == _value && !final) return;
        _value = v;
        RaiseValue();
        _panel.OnRowValueChanged(this, final);
    }

    private void RaiseValue()
    {
        _syncing = true;
        ValueBoxText = IsPart ? Candidate?.Part ?? "" : Spell(_value, "");
        _syncing = false;
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(ValueText));
        OnPropertyChanged(nameof(UnitText));
        OnPropertyChanged(nameof(CandidateTip));
        OnPropertyChanged(nameof(Position));
        OnPropertyChanged(nameof(DiffersFromSchematic));
    }

    // ---- The number box and the keyboard (overview §3) -----------------------

    /// <summary>Enter in the number box: a plain number is in the row's unit; a number with a unit is
    /// converted (<c>2000 fF</c> on a pF row is 2). Text that is not a number puts the box back.</summary>
    public void CommitValueText()
    {
        if (IsDisabled) return;
        if (VendorPart is { } vp)
        {
            // A part number, or a value — the candidate nearest it by value.
            if (PartIndexOf(vp, ValueBoxText) is { } at) SetValue(at, final: true);
            else RevertValueText();
            return;
        }
        if (ParseInUnit(ValueBoxText, Unit) is { } v)
            SetValue(IsInteger ? Math.Round(v) : Levels is { } lv ? CircuitRF.Design.Matching.PreferredValues.Snap(v, lv) : v, final: true);
        else
            RevertValueText();
    }

    /// <summary>Esc in the number box.</summary>
    public void RevertValueText() => ValueBoxText = IsPart ? Candidate?.Part ?? "" : Spell(_value, "");

    /// <summary>The candidate a typed part number names, or the one nearest a typed value; null for neither.</summary>
    private static int? PartIndexOf(CircuitRF.Design.VendorLibraries.VendorPartTunable vp, string text)
    {
        string t = text.Trim();
        for (int i = 0; i < vp.Candidates.Count; i++)
            if (vp.Candidates[i].Part.Equals(t, StringComparison.OrdinalIgnoreCase)) return i;
        double? want = CircuitRF.Design.VendorLibraries.VendorLibrarySearch.TryReadValue(t.Replace(" ", ""), out double v) ? v
                     : TunableValue.InUnit(t, vp.Unit) is { } n && vp.Unit.Length > 0 ? n * (Units.Scale(vp.Unit) ?? 1) : null;
        if (want is not { } w || w <= 0) return null;
        int best = -1;
        for (int i = 0; i < vp.Candidates.Count; i++)
            if (vp.Candidates[i].Value is > 0 and var cv
                && (best < 0 || Math.Abs(Math.Log(cv / w)) < Math.Abs(Math.Log(vp.Candidates[best].Value!.Value / w))))
                best = i;
        return best < 0 ? null : best;
    }

    /// <summary>An arrow, Shift+arrow or Page key on the focused slider.</summary>
    public void Nudge(int direction, TuningNudge size)
    {
        if (IsDisabled) return;
        if (IsPart)
        {
            // One candidate a step; ten for a large step; a tenth of the list a page.
            int by = size switch { TuningNudge.TenSteps => 10, TuningNudge.Page => Math.Max(1, (int)Math.Round((Max - Min) / 10)), _ => 1 };
            SetValue(Math.Clamp(_value + Math.Sign(direction) * by, Min, Max), final: true);
            return;
        }
        SetValue(TuningSliderMapping.Nudge(_value, direction, size, Min, Max, Scale, IsInteger, Step, Levels), final: true);
    }

    // ---- Range, scale, step — document edits (R-to4-10) ---------------------

    public void CommitMinText()
    {
        IsEditingMin = false;
        if (IsPart) { CommitPartBound(MinText, (e, v) => e.Min = v); return; }
        if (ParseInUnit(MinText, Unit) is { } v && v < Max) _panel.EditEntry(this, e => e.Min = FormatValue(v, Unit), "Set tuning range");
        else MinText = FormatValue(Min, "");
    }

    public void CommitMaxText()
    {
        IsEditingMax = false;
        if (IsPart) { CommitPartBound(MaxText, (e, v) => e.Max = v); return; }
        if (ParseInUnit(MaxText, Unit) is { } v && v > Min) _panel.EditEntry(this, e => e.Max = FormatValue(v, Unit), "Set tuning range");
        else MaxText = FormatValue(Max, "");
    }

    public void CommitStepText()
    {
        IsEditingStep = false;
        if (string.IsNullOrWhiteSpace(StepText)) { _panel.EditEntry(this, e => e.Step = null, "Clear tuning step"); return; }
        // A step is linear increments: it replaces Log and a series rather than hiding under them.
        if (ParseInUnit(StepText, Unit) is { } v && v > 0)
            _panel.EditEntry(this, e => { e.Step = FormatValue(v, Unit); e.Scale = TuneScale.Lin; ClearSeries(e); }, "Set tuning step");
        else StepText = Step is { } st ? FormatValue(st, "") : "";
    }

    /// <summary>
    /// A part's min or max: a bound on the candidates' STATED value, in the part's unit (<c>1 pF</c>; a bare
    /// number is in it). Blank removes the bound; anything else puts the box back.
    /// </summary>
    private void CommitPartBound(string text, Action<TunableEntry, string?> set)
    {
        string t = text.Trim();
        if (t.Length == 0 || t is "no min" or "no max") { _panel.EditEntry(this, e => set(e, null), "Clear part bound"); return; }
        if (ParseInUnit(t, VendorPart!.Unit) is { } v && v > 0)
        {
            string written = TunableValue.TryParse(t, out _, out string u, out _) && u.Length > 0 ? t : FormatValue(v, VendorPart.Unit);
            _panel.EditEntry(this, e => set(e, written), "Set part bound");
        }
        else CancelRangeEdit();
    }

    /// <summary>Enter in the search box: a search over the part's own library (<c>parts=</c>), or blank for the
    /// same kind.</summary>
    public void CommitQueryText()
    {
        IsEditingQuery = false;
        string q = QueryText.Trim();
        if (q == (_entryParts ?? "")) return;
        _panel.EditEntry(this, e => { e.Parts = q.Length == 0 ? null : q; e.PartKind = null; },
                         q.Length == 0 ? "Tune over parts of the same kind" : "Tune over a part search");
    }

    /// <summary>The ⋮ menu's Same kind: the candidates are the parts like the placed one (D1).</summary>
    [RelayCommand]
    private void SetSameKind()
    {
        if (_entryParts is null) return;
        _panel.EditEntry(this, e => { e.Parts = null; e.PartKind = null; }, "Tune over parts of the same kind");
    }

    [RelayCommand] private void EditQuery() => IsEditingQuery = true;

    /// <summary>Esc in a min, max or step box: the box closes and shows the entry's value again.</summary>
    public void CancelRangeEdit()
    {
        IsEditingQuery = false;
        if (IsPart)
        {
            IsEditingMin = IsEditingMax = false;
            var entry = _panel.EntryOf(Key);
            MinText = entry?.Min ?? "no min";
            MaxText = entry?.Max ?? "no max";
            QueryText = _entryParts ?? "";
            return;
        }
        IsEditingMin = IsEditingMax = IsEditingStep = false;
        MinText  = FormatValue(Min, "");
        MaxText  = FormatValue(Max, "");
        StepText = Step is { } st ? FormatValue(st, "") : "";
    }

    [RelayCommand] private void EditMin()  => IsEditingMin = true;
    [RelayCommand] private void EditMax()  => IsEditingMax = true;
    [RelayCommand] private void EditStep() => IsEditingStep = true;

    // Linear, Log, Step and a series are ONE choice of how the slider moves, so picking one clears the others:
    // a step left behind under Log kept every move in equal increments, and Log looked like it did nothing.
    [RelayCommand]
    private void SetLinear() => _panel.EditEntry(this, e => { e.Scale = TuneScale.Lin; e.Step = null; ClearSeries(e); }, "Tune on a linear scale");

    [RelayCommand]
    private void SetLog() => _panel.EditEntry(this, e => { e.Scale = TuneScale.Log; e.Step = null; ClearSeries(e); }, "Tune on a log scale");

    /// <summary>Steps along an E series, or the Preferences ladder (<c>Preferred</c>) — on a log slider, where a
    /// series' rungs sit evenly.</summary>
    [RelayCommand]
    private void SetSeries(string token)
    {
        if (!Enum.TryParse<TuneDiscrete>(token, ignoreCase: true, out var d) || d is TuneDiscrete.None or TuneDiscrete.Integer) return;
        _panel.EditEntry(this, e =>
        {
            e.Discrete = d;
            e.Step     = null;
            if (Min > 0) e.Scale = TuneScale.Log;
        }, $"Tune on {TunableValue.DiscreteLabel(d)} values");
    }

    private static void ClearSeries(TunableEntry e)
    {
        if (e.Discrete != TuneDiscrete.Integer) e.Discrete = TuneDiscrete.None;
    }

    /// <summary>Keeps the range's width — its ratio on a log row, its span on a linear one — and
    /// centres it on the current value, never below zero for an R, L or C.</summary>
    [RelayCommand]
    private void RecentreRange()
    {
        double lo, hi;
        if (IsLog && _value > 0)
        {
            double half = Math.Sqrt(Max / Min);
            (lo, hi) = (_value / half, _value * half);
        }
        else
        {
            double half = (Max - Min) / 2;
            (lo, hi) = (TunableValue.FloorAtZero(Tunable, Min, _value - half), _value + half);
        }
        _panel.EditEntry(this, e => { e.Min = FormatValue(lo, Unit); e.Max = FormatValue(hi, Unit); }, "Re-centre tuning range");
    }

    [RelayCommand]
    private void ResetRange()
    {
        if (IsPart) { _panel.EditEntry(this, e => { e.Min = null; e.Max = null; }, "Clear part bounds"); return; }
        if (Tunable is not { } t) return;
        _panel.EditEntry(this, e => { e.Min = t.DefaultMin; e.Max = t.DefaultMax; }, "Reset tuning range");
    }

    [RelayCommand] private void Remove() => _panel.SetTuned(Key, on: false);

    [RelayCommand] private void Reveal() => _panel.RevealRow(this);

    // ---- Text ---------------------------------------------------------------

    /// <summary>A value as rows, the session and Push spell it: six significant figures, then the unit.</summary>
    public static string FormatValue(double v, string unit)
    {
        string n = v.ToString("G6", CultureInfo.InvariantCulture);
        return unit.Length == 0 ? n : $"{n} {unit}";
    }

    /// <summary>Value text in <paramref name="unit"/>: a bare number is already in it.</summary>
    internal static double? ParseInUnit(string? text, string unit) => TunableValue.InUnit(text, unit);

    private static double? InUnit(string? text, string unit) => ParseInUnit(text, unit);
}

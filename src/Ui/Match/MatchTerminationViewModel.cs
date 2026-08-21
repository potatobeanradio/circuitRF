using System;
using System.Collections.Generic;
using System.Globalization;
using System.Collections.ObjectModel;
using CircuitRF.Core.Matching;
using CircuitRF.Engine.Matching;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.Matching;

/// <summary>
/// One termination group of the specification pane (match.md §9.2): topology, R, the reactance kind
/// and its value, the pictogram, and the disabled Probe button.
/// </summary>
/// <remarks>
/// <b>It owns no state.</b> Every property reads and writes the <see cref="MatchDesign"/> through the
/// owning Designer, which rebuilds and commits — brief §0.3's "never hold state that only exists in
/// the view-model", enforced structurally rather than remembered.
/// </remarks>
public sealed partial class MatchTerminationViewModel : ObservableObject
{
    private readonly MatchDesignerViewModel _owner;

    internal MatchTerminationViewModel(MatchDesignerViewModel owner, int end)
    {
        _owner = owner;
        End = end;
    }

    /// <summary>1 or 2 — which end of the design this is.</summary>
    public int End { get; }

    /// <summary>"Termination 1" / "Termination 2".</summary>
    public string Header => $"Termination {End}";

    /// <summary>The record this group edits.</summary>
    public Termination Value => End == 1 ? _owner.Design.Term1 : _owner.Design.Term2;

    // ── Topology ──────────────────────────────────────────────────────────────

    /// <summary>Series or parallel against R.</summary>
    public TerminationTopology Topology
    {
        get => Value.Topology;
        set
        {
            if (value == Topology) return;
            _owner.SetTermination(End, Value with { Topology = value });
        }
    }

    /// <summary>
    /// False when there is no reactance for the topology to arrange, and the selector is then
    /// disabled rather than left changing nothing.
    /// </summary>
    /// <remarks>
    /// <b>Owner-reported, 2026-08-20:</b> <i>"the series/parallel graphic indicator does not update
    /// when the selector is changed."</i> It does — <b>when there is something to draw</b>. With
    /// <c>ReactanceKind.None</c> the pictogram is a resistor on its own and there is no second
    /// element to put beside it or under it, so both topologies are the same picture. That is not a
    /// rendering bug: nothing downstream distinguishes them either. <c>Termination.CeqAt</c> and
    /// <c>QAt</c> both answer 0 for a reactance-free end, and <c>MatchOrders.ValidOrders</c>
    /// short-circuits on <c>!HasReactance</c> before it ever compares the two topologies — so on a
    /// bare R the choice changes the pictogram, the synthesis, the order parity and the ladder
    /// exactly not at all.
    ///
    /// <para>A selector that can be moved to no effect is the thing to fix, not the picture. It is
    /// disabled here, with <see cref="TopologyTooltip"/> saying why — the same treatment the ripple
    /// field got in the same round, and for the same reason.</para>
    /// </remarks>
    public bool TopologyEnabled => Kind != ReactanceKind.None;

    /// <summary>What the topology selector offers, or the one thing standing in its way.</summary>
    public string TopologyTooltip => TopologyEnabled
        ? "Whether the reactance sits in series with R or across it"
        : "Nothing to arrange: this termination has no reactance, so series and parallel describe the "
          + "same network. Set C or L first.";

    /// <summary>Two-state selector, series half.</summary>
    public bool IsSeries
    {
        get => Topology == TerminationTopology.Series;
        set { if (value) Topology = TerminationTopology.Series; }
    }

    /// <summary>Two-state selector, parallel half.</summary>
    public bool IsParallel
    {
        get => Topology == TerminationTopology.Parallel;
        set { if (value) Topology = TerminationTopology.Parallel; }
    }

    /// <summary>What the topology selector offers, in order.</summary>
    /// <remarks>
    /// <b>The Designer's selectors are <c>IconSelectButton</c>s, not radio buttons</b> (owner,
    /// 2026-08-19: "replace all radio UI selectors with the custom UI element we created for the
    /// trace card S/Z/Y selection"). That control is list-driven — it shows the current choice and
    /// opens the rest in a popup — so each selector needs its options as a list and its state as one
    /// value rather than as one boolean per option. The booleans above stay: they are what the design
    /// actually round-trips, and they are what the existing gate tests assert against.
    /// </remarks>
    public static IReadOnlyList<string> TopologyOptions { get; } = ["Series", "Parallel"];

    /// <summary>The topology as one of <see cref="TopologyOptions"/>.</summary>
    public string TopologyChoice
    {
        get => IsSeries ? TopologyOptions[0] : TopologyOptions[1];
        set => Topology = string.Equals(value, TopologyOptions[0], StringComparison.Ordinal)
            ? TerminationTopology.Series
            : TerminationTopology.Parallel;
    }

    // ── Resistance ────────────────────────────────────────────────────────────

    /// <summary>Port resistance, ohms.</summary>
    public double Resistance
    {
        get => Value.R;
        set
        {
            if (value == Resistance || !double.IsFinite(value)) return;
            _owner.SetTermination(End, Value with { R = value });
        }
    }

    /// <summary>
    /// The resistance field's unit — <b>the Settings flyout's, until the user types a different
    /// one</b>.
    /// </summary>
    /// <remarks>
    /// <b>Owner-reported, 2026-08-20:</b> <i>"after a probe, the units in the R, L and C do not match
    /// the units set in the Match Designer settings."</i>
    ///
    /// <para>They never did, and a probe is only where it shows: this field carried its OWN unit — a
    /// hard-coded "Ω" here and <see cref="MatchValueFormat.AutoUnit"/> for the reactance — with no
    /// connection to §9.9's per-dimension display units at all. As long as the user typed the values
    /// the two agreed by accident, because a typed unit pins this one and the eye reads only this
    /// field. A probe writes a MEASURED value nobody typed, Auto then picks the unit per value
    /// (0.34 pF arrives as "340 fF"), and the disagreement with the pF the Settings flyout says is
    /// suddenly visible.</para>
    ///
    /// <para>So the settings unit is the DEFAULT and the typed one is an override, rather than the
    /// other way round: <see cref="ResetDisplayUnits"/> drops the override whenever a probe supplies
    /// the value, which is precisely the case where there is no typed unit to respect.</para>
    /// </remarks>
    public string ResistanceUnit
    {
        get => _resistanceUnitOverride ?? Settings.ResistanceUnit;
        set
        {
            // A unit that IS the settings unit leaves the field following settings rather than
            // pinning a copy of it: the resistance field re-commits its unit on every edit, including
            // the ones where the user typed no unit at all, so pinning unconditionally would freeze
            // this field against the Settings flyout the first time anyone typed a number into it.
            string? pin = string.Equals(value, Settings.ResistanceUnit, StringComparison.Ordinal)
                ? null : value;
            if (pin == _resistanceUnitOverride) return;
            _resistanceUnitOverride = pin;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ResistanceText));
            OnPropertyChanged(nameof(ResistanceEntry));
        }
    }
    private string? _resistanceUnitOverride;

    /// <summary>The resistance as typed. A value that will not parse is held, not discarded.</summary>
    public string ResistanceText
    {
        get => _resistanceStaged ?? MatchValueFormat.Format(
            Resistance, MatchQuantity.Resistance, ResistanceUnit, MatchDesignerSettings.EntryDigits).Text;
        set
        {
            _resistanceStaged = value;
            OnPropertyChanged();
        }
    }
    private string? _resistanceStaged;

    /// <summary>Parses and commits the staged resistance; a no-op when nothing was typed.</summary>
    public void CommitResistance()
    {
        if (_resistanceStaged is null) return;
        string staged = _resistanceStaged;
        _resistanceStaged = null;
        if (MatchValueFormat.TryParse(staged, ResistanceUnit, out double r) && r > 0.0)
            Resistance = r;
        OnPropertyChanged(nameof(ResistanceText));
    }

    // ── Reactance ─────────────────────────────────────────────────────────────

    /// <summary>C, L or None.</summary>
    public ReactanceKind Kind
    {
        get => Value.Kind;
        set
        {
            if (value == Kind) return;
            // Switching TO a reactance from None needs a value the synthesis can use; 1 pF / 1 nH are
            // the same defaults the schematic's own C and L rows carry.
            double v = Value.Value > 0 ? Value.Value : value == ReactanceKind.L ? 1e-9 : 1e-12;
            _owner.SetTermination(End, Value with { Kind = value, Value = v });
        }
    }

    /// <summary>Three-state selector, capacitance.</summary>
    public bool IsC { get => Kind == ReactanceKind.C; set { if (value) Kind = ReactanceKind.C; } }

    /// <summary>Three-state selector, inductance.</summary>
    public bool IsL { get => Kind == ReactanceKind.L; set { if (value) Kind = ReactanceKind.L; } }

    /// <summary>Three-state selector, purely resistive.</summary>
    public bool IsNone { get => Kind == ReactanceKind.None; set { if (value) Kind = ReactanceKind.None; } }

    /// <summary>What the reactance-kind selector offers, in order. "–" is purely resistive.</summary>
    public static IReadOnlyList<string> KindOptions { get; } = ["C", "L", "–"];

    /// <summary>The reactance kind as one of <see cref="KindOptions"/>.</summary>
    public string KindChoice
    {
        get => Kind switch
        {
            ReactanceKind.C => KindOptions[0],
            ReactanceKind.L => KindOptions[1],
            _               => KindOptions[2],
        };
        set => Kind = value switch
        {
            "C" => ReactanceKind.C,
            "L" => ReactanceKind.L,
            _   => ReactanceKind.None,
        };
    }

    /// <summary>False when the end is purely resistive — the value field has nothing to hold.</summary>
    public bool HasReactance => Kind != ReactanceKind.None;

    /// <summary>The reactance's own quantity, which is what switches the unit list with the kind.</summary>
    public MatchQuantity ReactanceQuantity =>
        Kind == ReactanceKind.L ? MatchQuantity.Inductance : MatchQuantity.Capacitance;

    /// <summary>The unit options for the current kind.</summary>
    public IReadOnlyList<string> ReactanceUnitOptions =>
        Kind == ReactanceKind.L
            ? MatchDesignerSettings.InductanceUnitOptions
            : MatchDesignerSettings.CapacitanceUnitOptions;

    /// <summary>Farads or henries, per <see cref="Kind"/>.</summary>
    public double Reactance
    {
        get => Value.Value;
        set
        {
            if (value == Reactance || !double.IsFinite(value)) return;
            _owner.SetTermination(End, Value with { Value = value });
        }
    }

    /// <summary>
    /// The reactance field's unit: the Settings flyout's unit for the current KIND, unless the user
    /// has pinned one by typing it. See <see cref="ResistanceUnit"/> for why that order matters.
    /// </summary>
    public string ReactanceUnit
    {
        get => _reactanceUnitOverride ?? Settings.UnitFor(ReactanceQuantity);
        set
        {
            string? pin = string.Equals(value, Settings.UnitFor(ReactanceQuantity), StringComparison.Ordinal)
                ? null : value;
            if (pin == _reactanceUnitOverride) return;
            _reactanceUnitOverride = pin;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ReactanceText));
            OnPropertyChanged(nameof(ReactanceUnitShown));
            OnPropertyChanged(nameof(ReactanceEntry));
        }
    }
    private string? _reactanceUnitOverride;

    /// <summary>
    /// Drops both pinned units, so the fields fall back to the Settings flyout's.
    /// </summary>
    /// <remarks>
    /// Called on the two paths that write a value the user did not type — the probe's winning fit and
    /// an explicitly-applied one. A unit pinned against a hand-typed number has nothing to say about a
    /// measured one, and keeping it is what made a probed 0.34 pF read in fF while Settings said pF.
    /// </remarks>
    internal void ResetDisplayUnits()
    {
        if (_resistanceUnitOverride is null && _reactanceUnitOverride is null) return;
        _resistanceUnitOverride = null;
        _reactanceUnitOverride = null;
        OnPropertyChanged(nameof(ResistanceUnit));
        OnPropertyChanged(nameof(ReactanceUnit));
        OnPropertyChanged(nameof(ReactanceUnitShown));
        OnPropertyChanged(nameof(ResistanceText));
        OnPropertyChanged(nameof(ReactanceText));
        NotifyEntryText();
    }

    /// <summary>The reactance as typed.</summary>
    public string ReactanceText
    {
        get => _reactanceStaged ?? MatchValueFormat.Format(
            Reactance, ReactanceQuantity, ReactanceUnit, MatchDesignerSettings.EntryDigits).Text;
        set
        {
            _reactanceStaged = value;
            OnPropertyChanged();
        }
    }
    private string? _reactanceStaged;

    /// <summary>The unit actually shown beside the reactance box (resolves Auto).</summary>
    public string ReactanceUnitShown => ReactanceUnit == MatchValueFormat.AutoUnit
        ? MatchValueFormat.AutoUnitFor(Reactance, ReactanceQuantity)
        : ReactanceUnit;

    /// <summary>Parses and commits the staged reactance.</summary>
    public void CommitReactance()
    {
        if (_reactanceStaged is null) return;
        string staged = _reactanceStaged;
        _reactanceStaged = null;
        string unit = ReactanceUnit == MatchValueFormat.AutoUnit ? ReactanceUnitShown : ReactanceUnit;
        if (MatchValueFormat.TryParse(staged, unit, out double v) && v > 0.0)
            Reactance = v;
        OnPropertyChanged(nameof(ReactanceText));
    }

    // ── Inline-editor entry text (owner, 2026-08-19) ──────────────────────────
    //
    // The specification pane's fields are InlineEditText controls, which show and seed ONE string
    // carrying the value and its unit together. The staged/committed halves are the existing
    // ResistanceText/ReactanceText — these two properties only compose and decompose the unit, so
    // there is still exactly one place that parses and one place that writes the design.

    /// <summary>The resistance as "50 Ω" — what the inline editor shows and seeds from.</summary>
    public string ResistanceEntry
    {
        get => $"{ResistanceText} {ResistanceUnit}";
        set
        {
            if (!MatchValueFormat.TryParseWithUnit(
                    value, MatchQuantity.Resistance, ResistanceUnit, out double r, out string unit))
            {
                OnPropertyChanged();     // refuse it and put the field back
                return;
            }
            ResistanceUnit = unit;
            _resistanceStaged = MatchValueFormat.Format(
                r, MatchQuantity.Resistance, unit, MatchDesignerSettings.EntryDigits).Text;
            CommitResistance();
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// The reactance as "1 pF" / "1.5 nH" — and <b>the reactance KIND, typed as a unit</b>.
    /// </summary>
    /// <remarks>
    /// <b>Owner, 2026-08-20:</b> <i>"Allow the termination 'reactance' value to be always editable with
    /// the inline text editor (even when '–' is selected). Allow the user entered units to change
    /// whether the X is an L or C or none. If user sets it to a pH or nH (or any H) unit, then the X
    /// component becomes an L. If it's changed to any farad unit, then it becomes a C. If user enters
    /// 0, then it becomes a '–' component."</i>
    ///
    /// <para>So the typed unit is matched against BOTH ladders before a quantity is chosen, rather
    /// than against the current kind's — which is the one thing <c>TryParseWithUnit</c> cannot do,
    /// since it takes the quantity as an input. A bare number with no unit keeps the kind that is
    /// already selected, so typing over the digits (which is exactly what the inline editor
    /// pre-selects) never changes a C into an L behind the user's back.</para>
    ///
    /// <para>A zero clears the reactance to <see cref="ReactanceKind.None"/>, and the field stays
    /// live afterwards — it is the only way back to a reactance without touching the kind selector,
    /// and a disabled field would be a door that locks behind you. When None is selected the field
    /// shows "0" plus whichever unit the end last carried, so the unit the user types is the whole
    /// of the decision.</para>
    /// </remarks>
    public string ReactanceEntry
    {
        get => Kind == ReactanceKind.None
            ? $"0 {ReactanceUnitShown}"
            : $"{ReactanceText} {ReactanceUnitShown}";
        set
        {
            var (number, token) = MatchValueFormat.SplitTypedValue(value);

            // The typed unit picks the kind. No unit typed keeps the kind that is selected — or, when
            // that is None, whichever one the field is currently displaying in.
            ReactanceKind kind = Kind;
            string? unit = null;
            if (token.Length > 0)
            {
                if (MatchValueFormat.TryMatchUnit(token, MatchQuantity.Inductance) is { } henries)
                {
                    kind = ReactanceKind.L;
                    unit = henries;
                }
                else if (MatchValueFormat.TryMatchUnit(token, MatchQuantity.Capacitance) is { } farads)
                {
                    kind = ReactanceKind.C;
                    unit = farads;
                }
                else
                {
                    OnPropertyChanged();     // refuse it and put the field back
                    return;
                }
            }

            if (kind == ReactanceKind.None) kind = ReactanceKind.C;

            var quantity = kind == ReactanceKind.L
                ? MatchQuantity.Inductance
                : MatchQuantity.Capacitance;
            unit ??= ReactanceUnit == MatchValueFormat.AutoUnit
                ? MatchValueFormat.AutoUnitFor(Reactance, quantity)
                : ReactanceUnit;

            if (!double.TryParse(number.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture,
                                 out double raw) || !double.IsFinite(raw) || raw < 0)
            {
                OnPropertyChanged();
                return;
            }

            double v = raw * MatchValueFormat.Scale(unit);

            if (v == 0.0)
            {
                // Zero means "no reactance at this end". The VALUE is left standing so switching the
                // kind back with the selector does not have to invent one.
                if (Kind != ReactanceKind.None)
                    _owner.SetTermination(End, Value with { Kind = ReactanceKind.None });
                OnPropertyChanged();
                return;
            }

            // A unit typed by hand PINS the display unit; one that merely matched the Auto choice
            // leaves Auto alone, or every commit would quietly turn Auto off.
            if (token.Length > 0 && (ReactanceUnit != MatchValueFormat.AutoUnit || unit != ReactanceUnitShown))
                ReactanceUnit = unit;

            _reactanceStaged = null;
            _owner.SetTermination(End, Value with { Kind = kind, Value = v });
            OnPropertyChanged();
        }
    }

    /// <summary>Re-reads both entry strings — called after the design changes underneath.</summary>
    internal void NotifyEntryText()
    {
        OnPropertyChanged(nameof(ResistanceEntry));
        OnPropertyChanged(nameof(ReactanceEntry));
    }

    /// <summary>True when either field holds text that has not been parsed yet.</summary>
    public bool HasPendingText => _resistanceStaged is not null || _reactanceStaged is not null;

    // ── Derived readouts ──────────────────────────────────────────────────────

    /// <summary>This end's Q at band centre — one half of the status strip's Q1/Q2.</summary>
    public double Q => Value.QAt(_owner.Design.Omega0);

    /// <summary>
    /// True when a refusal names this end (match.md §9.7). The Designer sets it; nothing here
    /// decides what is wrong, because the numbers that say so come from MN-1's refusal.
    /// </summary>
    public bool IsFlagged
    {
        get => _isFlagged;
        internal set { if (value != _isFlagged) { _isFlagged = value; OnPropertyChanged(); } }
    }
    private bool _isFlagged;

    // ── Probe — MN-4 (match.md §10) ───────────────────────────────────────────

    /// <summary>The Designer's settings, for the fit rows' own formatting.</summary>
    internal MatchDesignerSettings Settings => _owner.Settings;

    /// <summary>
    /// Whether this pin can be probed, and if not, which of match.md §10.4's reasons it is. Recomputed
    /// whenever the schematic could have changed, never on a slider drag.
    /// </summary>
    internal MatchProbeAvailability Availability
    {
        get => _availability;
        set
        {
            _availability = value;
            OnPropertyChanged(nameof(CanProbe));
            OnPropertyChanged(nameof(ProbeTooltip));
            _probeCommand?.NotifyCanExecuteChanged();
        }
    }
    private MatchProbeAvailability _availability =
        new(MatchProbeBlock.NoSchematic, "This Match is not open in a schematic.", null, null, "", 0);

    /// <summary>True when the Probe button is live.</summary>
    public bool CanProbe => _availability.CanProbe && !_owner.IsProbing && !_owner.IsOrphaned;

    /// <summary>
    /// Why the Probe button is what it is — <b>the disabled state always says WHICH</b> (§10.4). A
    /// greyed button with no reason is the failure mode this tooltip exists to prevent.
    /// </summary>
    public string ProbeTooltip =>
        _owner.IsOrphaned ? _owner.OrphanNote
        : _owner.IsProbing && _availability.CanProbe ? "A probe is already running."
        : _availability.Reason;

    /// <summary>Runs the probe for this end.</summary>
    public IAsyncRelayCommand ProbeCommand => _probeCommand ??=
        new AsyncRelayCommand(() => _owner.ProbeAsync(End), () => CanProbe);
    private IAsyncRelayCommand? _probeCommand;

    /// <summary>
    /// match.md §10.3 — aim at the conjugate of what is measured rather than at the measurement.
    /// </summary>
    /// <remarks>
    /// <b>No longer on the specification pane</b> (owner, 2026-08-20: "remove the Conjugate checkbox
    /// (2 instance) — we should not need it; clicking Probe gets the answer of which R, L, C and
    /// topology to use"). It stays here, and on <c>MatchDesign</c>, for two reasons: an older design
    /// that carries the flag still decodes and still means what it said, and the probe still reads it.
    /// It is simply false for every design nobody set it on, which is every design made from now on.
    /// </remarks>
    public bool Conjugate
    {
        get => End == 1 ? _owner.Design.Term1Conjugate : _owner.Design.Term2Conjugate;
        set
        {
            if (value == Conjugate) return;
            _owner.SetConjugate(End, value);
        }
    }

    /// <summary>
    /// The sentence match.md §10.3 says the Designer must state once rather than leave the user to
    /// rediscover. It has no toggle to sit beside any more — see <see cref="Conjugate"/>.
    /// </summary>
    public static string ConjugateNote =>
        "A conjugate match is the right target for a small-signal stage, and generally the WRONG one "
        + "for a power amplifier's output — there the load should come from loadpull (Ropt), not from "
        + "the device's own output impedance.";

    /// <summary>Every candidate model from the last probe, best residual first.</summary>
    public ObservableCollection<MatchProbeFitRowViewModel> ProbeFits { get; } = [];

    /// <summary>§10.2's poor-fit warning from the last probe, or empty.</summary>
    [ObservableProperty] private string _probeFlag = "";

    /// <summary>Why the last probe produced nothing, or empty.</summary>
    [ObservableProperty] private string _probeError = "";

    /// <summary>True when the last probe produced something to show.</summary>
    public bool HasProbeResult => ProbeFits.Count > 0;

    /// <summary>Applies one listed fit — §10.2's "take the second-best when you know better".</summary>
    internal void ApplyProbeFit(MatchProbeFitRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!row.Fit.Physical) return;
        var chosen = row.Conjugate ? row.Fit.ConjugateAt(row.Omega0) : row.Fit;
        ResetDisplayUnits();
        _owner.ApplyProbedTermination(End, chosen.ToTermination(DateTime.UtcNow));
    }

    /// <summary>Takes one probe result: lists the four fits, and applies the winner.</summary>
    internal void ShowProbeResult(TerminationProbe.ProbeResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        ProbeFits.Clear();
        foreach (var fit in result.Fits)
            ProbeFits.Add(new MatchProbeFitRowViewModel(
                this, fit, result.Omega0, result.Conjugate, ReferenceEquals(fit, result.Best)));

        ProbeFlag = result.Flag;
        ProbeError = result.Refusal ?? "";
        OnPropertyChanged(nameof(HasProbeResult));

        if (result is { Ok: true, Termination: { } t })
        {
            ResetDisplayUnits();
            _owner.ApplyProbedTermination(End, t);
        }
    }

    /// <summary>Re-raises the two properties the running/idle state drives.</summary>
    internal void RefreshProbeState()
    {
        OnPropertyChanged(nameof(CanProbe));
        OnPropertyChanged(nameof(ProbeTooltip));
        _probeCommand?.NotifyCanExecuteChanged();
    }

    /// <summary>True when §10's probe supplied this termination rather than the user.</summary>
    public bool IsProbed => Value.Probed;

    /// <summary>
    /// Provenance line for a probed termination, or empty.
    /// </summary>
    /// <remarks>
    /// <b>No longer rendered</b> (owner, 2026-08-20: "remove the 'probed' date text that can
    /// sometimes appear next to the probe button"). It stays because it is the readable form of the
    /// provenance the DESIGN carries — <c>Termination.Probed</c> / <c>ProbedAtUtc</c>, which survive a
    /// save and reload and which match.md §10.5's "a hand edit clears the badge" rule is written
    /// against; <c>MatchProbeTests</c> reads it for exactly that.
    /// </remarks>
    public string ProbeProvenance => Value is { Probed: true, ProbedAtUtc: { } t }
        ? $"probed {t.ToLocalTime():yyyy-MM-dd HH:mm}"
        : "";

    // ── The pictogram ─────────────────────────────────────────────────────────

    /// <summary>What the little R-and-reactance drawing shows. The view draws it; this decides it.</summary>
    public MatchPictogram Pictogram => new(Kind, Topology);

    /// <summary>
    /// Which branch of a PARALLEL pictogram the resistor takes: left for termination 1, right for
    /// termination 2 (owner, 2026-08-19), so the two ends read as mirror images of each other rather
    /// than as two copies of the same drawing.
    /// </summary>
    public bool ResistorOnLeft => End == 1;

    /// <summary>Raises every derived property after the owning design changed underneath.</summary>
    internal void Refresh()
    {
        _resistanceStaged = null;
        _reactanceStaged = null;
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(Topology));
        OnPropertyChanged(nameof(IsSeries));
        OnPropertyChanged(nameof(IsParallel));
        OnPropertyChanged(nameof(TopologyChoice));
        OnPropertyChanged(nameof(TopologyEnabled));
        OnPropertyChanged(nameof(TopologyTooltip));
        OnPropertyChanged(nameof(Resistance));
        OnPropertyChanged(nameof(ResistanceUnit));
        OnPropertyChanged(nameof(ReactanceUnit));
        OnPropertyChanged(nameof(ResistanceText));
        OnPropertyChanged(nameof(Kind));
        OnPropertyChanged(nameof(IsC));
        OnPropertyChanged(nameof(IsL));
        OnPropertyChanged(nameof(IsNone));
        OnPropertyChanged(nameof(KindChoice));
        OnPropertyChanged(nameof(HasReactance));
        OnPropertyChanged(nameof(ReactanceQuantity));
        OnPropertyChanged(nameof(ReactanceUnitOptions));
        OnPropertyChanged(nameof(Reactance));
        OnPropertyChanged(nameof(ReactanceText));
        OnPropertyChanged(nameof(ReactanceUnitShown));
        OnPropertyChanged(nameof(Q));
        OnPropertyChanged(nameof(IsProbed));
        OnPropertyChanged(nameof(ProbeProvenance));
        OnPropertyChanged(nameof(Conjugate));
        OnPropertyChanged(nameof(Pictogram));
        NotifyEntryText();
    }
}

/// <summary>
/// The specification pane's pictogram: which reactive element, and how it sits against the resistor.
/// A record rather than a bitmap so a test can assert what is being drawn.
/// </summary>
/// <param name="Kind">C, L, or None — None draws the resistor alone.</param>
/// <param name="Topology">Series or parallel.</param>
public readonly record struct MatchPictogram(ReactanceKind Kind, TerminationTopology Topology)
{
    /// <summary>A one-line description, which is also the pictogram's tooltip.</summary>
    public string Description => Kind switch
    {
        ReactanceKind.None => "R alone — purely resistive, nothing to absorb",
        ReactanceKind.C when Topology == TerminationTopology.Series   => "R in series with C",
        ReactanceKind.C                                               => "R in parallel with C",
        ReactanceKind.L when Topology == TerminationTopology.Series   => "R in series with L",
        _                                                             => "R in parallel with L",
    };
}

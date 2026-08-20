using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CircuitRF.Core.Matching;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.Matching;

/// <summary>
/// One row of the transform rack (match.md §9.4): label, pi/T selector, numeric box, slider, lock,
/// and the names of the two elements it acts on.
/// </summary>
/// <remarks>
/// <b>The slider's bounds are the RECOMPUTED ones</b> (<c>AppliedTransform.Range</c>, which
/// <c>MatchRebuild</c> derives from the element values as they stand at that point in the sequence).
/// Nothing here stores a bound, and nothing may: a stored bound goes stale against the elements it
/// bounds, and a stale bound silently permits a negative element — see <c>TransformRecord</c>'s own
/// remarks on why NMin/NMax are absent from the persisted model.
/// </remarks>
public sealed partial class MatchTransformRowViewModel : ObservableObject
{
    private readonly MatchDesignerViewModel _owner;

    internal MatchTransformRowViewModel(MatchDesignerViewModel owner, int index)
    {
        _owner = owner;
        Index = index;
    }

    /// <summary>Position in the design's transform list, and the linkage's slot index.</summary>
    public int Index { get; internal set; }

    /// <summary>N1, N2, ... — the same label the ladder's bracket carries.</summary>
    public string Label => $"N{Index + 1}";

    /// <summary>The stored record.</summary>
    public TransformRecord Record => _owner.Design.Transforms[Index];

    // There is no "on (L2, L4)" row property any more (owner, 2026-08-20: "remove the '(C2, C3)'
    // indicator text that appears to the right of the locked button in the Transform group"). The
    // pair a transform acts on is already drawn, as a brace under those very elements, in the
    // schematic directly above this rack — see MatchTransformBracket.

    // ── N ─────────────────────────────────────────────────────────────────────

    /// <summary>The turns ratio.</summary>
    public double N
    {
        get => Record.N;
        set => _owner.SetTransformN(Index, value);
    }

    /// <summary>The numeric box's text.</summary>
    public string NText
    {
        get => _nStaged ?? N.ToString("0.#####", CultureInfo.InvariantCulture);
        set { _nStaged = value; OnPropertyChanged(); _owner.NotifyPendingEdits(); }
    }
    private string? _nStaged;

    /// <summary>Parses and commits the staged N.</summary>
    public void CommitN()
    {
        if (_nStaged is null) return;
        string staged = _nStaged;
        _nStaged = null;
        if (double.TryParse(staged, NumberStyles.Float, CultureInfo.InvariantCulture, out double n))
            N = n;
        OnPropertyChanged(nameof(NText));
    }

    /// <summary>True when the numeric box holds unparsed text.</summary>
    public bool HasPendingText => _nStaged is not null;

    /// <summary>The recomputed lower bound.</summary>
    public double NMin => Range?.Min ?? 1.0;

    /// <summary>The recomputed upper bound.</summary>
    public double NMax => Range?.Max ?? 1.0;

    /// <summary>The range in force at this point in the sequence, or null when the transform dropped.</summary>
    public TransformRange? Range { get; internal set; }

    /// <summary>True when the stored N had to be brought inside its range at the last rebuild.</summary>
    public bool WasClamped { get; internal set; }

    /// <summary>
    /// True when this row's pair no longer resolves — its element names are not in the current ladder.
    /// The row stays, greyed, because deleting it silently is how a design loses a transform it was
    /// told to keep.
    /// </summary>
    public bool IsDropped { get; internal set; }

    // ── pi / T ────────────────────────────────────────────────────────────────

    /// <summary>Which three-element equivalent this transform produces.</summary>
    public TransformForm Form
    {
        get => Record.Form;
        set
        {
            if (value == Form) return;
            _owner.SetTransformForm(Index, value);
        }
    }

    /// <summary>Two-state selector, pi half.</summary>
    public bool IsPi { get => Form == TransformForm.Pi; set { if (value) Form = TransformForm.Pi; } }

    /// <summary>Two-state selector, T half.</summary>
    public bool IsT  { get => Form == TransformForm.T;  set { if (value) Form = TransformForm.T;  } }

    /// <summary>What the form selector offers, in order — see <c>MatchTerminationViewModel.TopologyOptions</c>
    /// for why the Designer's selectors are list-driven rather than pairs of radio buttons.</summary>
    public static IReadOnlyList<string> FormOptions { get; } = ["π", "T"];

    /// <summary>The form as one of <see cref="FormOptions"/>.</summary>
    public string FormChoice
    {
        get => IsPi ? FormOptions[0] : FormOptions[1];
        set => Form = string.Equals(value, FormOptions[1], StringComparison.Ordinal)
            ? TransformForm.T
            : TransformForm.Pi;
    }

    // ── Lock ──────────────────────────────────────────────────────────────────

    /// <summary>A locked row is never written by the linkage.</summary>
    public bool Locked
    {
        get => Record.Locked;
        set
        {
            if (value == Locked) return;
            _owner.SetTransformLocked(Index, value);
        }
    }

    // ── Enablement ────────────────────────────────────────────────────────────

    /// <summary>
    /// False when the slider and the numeric box must not be touched: link on with exactly one
    /// transform fully determines N (match.md §4.8), and a control that snaps back is worse than one
    /// that is disabled. A locked or dropped row is likewise not draggable.
    /// </summary>
    public bool CanEditN =>
        !IsDropped && !Locked
        && !(_owner.Design.LinkTransforms && _owner.Design.Transforms.Count == 1);

    /// <summary>Why the slider is disabled, or empty when it is not.</summary>
    public string DisabledReason =>
        IsDropped ? $"{Record.ElementA}/{Record.ElementB} is no longer a pair in this ladder."
        : Locked  ? "Locked — unlock to move it."
        : _owner.Design.LinkTransforms && _owner.Design.Transforms.Count == 1
            ? "With Link on and one transform, N is fully determined by the required ratio."
            : "";

    /// <summary>True when the last redistribution parked this row on a range bound.</summary>
    public bool IsAtLimit { get; internal set; }

    /// <summary>Raises everything after a rebuild.</summary>
    internal void Refresh()
    {
        _nStaged = null;
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(Record));
        OnPropertyChanged(nameof(N));
        OnPropertyChanged(nameof(NText));
        OnPropertyChanged(nameof(NMin));
        OnPropertyChanged(nameof(NMax));
        OnPropertyChanged(nameof(Range));
        OnPropertyChanged(nameof(WasClamped));
        OnPropertyChanged(nameof(IsDropped));
        OnPropertyChanged(nameof(Form));
        OnPropertyChanged(nameof(IsPi));
        OnPropertyChanged(nameof(IsT));
        OnPropertyChanged(nameof(FormChoice));
        OnPropertyChanged(nameof(Locked));
        OnPropertyChanged(nameof(CanEditN));
        OnPropertyChanged(nameof(DisabledReason));
        OnPropertyChanged(nameof(IsAtLimit));
    }
}

/// <summary>One entry of the <c>+ add</c> menu: a transformable pair, offered by element name.</summary>
/// <param name="ElementA">First element.</param>
/// <param name="ElementB">Second element.</param>
public sealed record MatchAvailablePair(string ElementA, string ElementB)
{
    /// <summary>What the menu item reads.</summary>
    public string Display => $"{ElementA} / {ElementB}";
}

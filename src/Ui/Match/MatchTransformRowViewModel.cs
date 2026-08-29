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
    /// <remarks>
    /// <b>Two writes that are not edits, and both used to become one</b> (owner-reported, 2026-08-28:
    /// undoing repeatedly, or alternating undo and redo quickly, eventually loses the history
    /// altogether).
    ///
    /// <para>The slider this backs is <c>Value="{Binding N, Mode=TwoWay}"</c> with its
    /// <c>Minimum</c>/<c>Maximum</c> bound to <see cref="NMin"/>/<see cref="NMax"/>. Avalonia's
    /// <c>RangeBase</c> clamps <c>Value</c> into the current range on EVERY set — its own and the two
    /// bounds' — and a two-way binding writes each clamp straight back here. So a rebuild that moves
    /// this row published a value the user never typed, and this setter, having no guard at all,
    /// turned it into <c>SetTransformN</c> and therefore into a <c>SetParametersCommand</c> on the
    /// schematic's undo stack.</para>
    ///
    /// <para><b>During an UNDO that is fatal.</b> The write-back arrives inside
    /// <c>UndoRedoStack.Undo</c>'s own call to the command — the model change it raises reaches
    /// <c>MatchDesignerViewModel.OnModelChanged</c>, which refreshes these rows — so the undo pushed a
    /// NEW entry while it was still moving the old one. <c>Execute</c> clears the redo stack, so redo
    /// vanished; the history grew by one entry for every undo, so undoing never reached the bottom;
    /// and the stamp stack came out of lockstep with the command stack, which is what
    /// <c>TopUndoStamp</c> — and therefore the termination auto-solve's amend — reads. Measured on the
    /// order-4 fixture: 8 real edits took 14 undos to unwind, with 6 phantom entries injected.</para>
    ///
    /// <para>An ordinary edit hid it: those refreshes happen inside <c>AsOneEdit</c> with
    /// <c>_commitSuppressed</c> up, which absorbs the extra commit. An undo has no such gesture around
    /// it, which is why the report is about undo and redo and nothing else.</para>
    ///
    /// <para>So a write that changes nothing is dropped, and a write arriving while the view-model is
    /// PUBLISHING its own state to the view is dropped too — see
    /// <c>MatchDesignerViewModel.IsPublishing</c>. <see cref="Refresh"/> also publishes the bounds
    /// before the value now, so the common case never produces a clamp in the first place.</para>
    /// </remarks>
    public double N
    {
        get => Record.N;
        set
        {
            if (value == N) return;
            if (_owner.IsPublishing) return;
            _owner.SetTransformN(Index, value);
        }
    }

    /// <summary>
    /// N as the inline editor reads and writes it, <b>validated on commit and refused when bad</b>.
    /// </summary>
    /// <remarks>
    /// <b>Owner, 2026-08-20:</b> <i>"the textedit box of the N1/N2… transforms to text. Allow user to
    /// change it using the inline text editor. Its input must be validated/refused if bad input."</i>
    ///
    /// <para>There are two kinds of bad input and they are refused differently. <b>Not a number</b>
    /// (or zero, or negative, or infinite) is not a turns ratio at all and is thrown away.
    /// <b>Outside the range</b> is a number this transform cannot take at this point in the ladder —
    /// <c>MatchRebuild</c> derives <see cref="NMin"/>/<see cref="NMax"/> from the element values as
    /// they stand, and a value past either bound puts an element negative. It is refused with the
    /// bound NAMED rather than silently clamped into it: a field that answers a typed 4 with a 2.37
    /// and says nothing is a field the user stops believing.</para>
    ///
    /// <para>Either way the property re-raises itself, so the editor's own commit writes the stored
    /// value straight back over the rejected text — the same shape <c>OrderEntry</c> uses.</para>
    /// </remarks>
    public string NEntry
    {
        get => N.ToString("0.#####", CultureInfo.InvariantCulture);
        set
        {
            string typed = (value ?? "").Trim();
            if (!double.TryParse(typed, NumberStyles.Float, CultureInfo.InvariantCulture, out double n)
                || !double.IsFinite(n) || n <= 0)
            {
                _owner.SetTransformNote(
                    $"'{typed}' is not a turns ratio. {Label} takes a positive number.");
            }
            else if (InRange(n))
            {
                _owner.SetTransformNote("");
                // Clamped only across the DISPLAY SLACK below — a value the field itself printed
                // rounds to a hair outside its own bound, and refusing the number the user was just
                // shown is the one refusal nobody can act on.
                N = Math.Clamp(n, NMin, NMax);
            }
            else
            {
                // WHICH bound was hit decides what the user can do about it, so the sentence names
                // it. Past the positivity range, the transform's own products would go negative;
                // short of that but past the reachable range, it is the linkage holding it back and
                // the remedy is a different one entirely.
                bool positivity = Range is not { } r
                                  || n < r.Min - DisplaySlack || n > r.Max + DisplaySlack;
                _owner.SetTransformNote(
                    $"{Label} must be between {Fmt(NMin)} and {Fmt(NMax)} — "
                    + (positivity
                        ? "outside that this transform drives one of its own three products "
                          + "negative. "
                          + (_owner.Design.AllowNegativeComponents
                              ? "Lock another transform to move the bound."
                              : "Turn on \u201CAllow negative components\u201D to widen it.")
                        : "with Link on, the other transforms cannot absorb any more than that. "
                          + "Unlock one, or turn Link off, to reach further."));
            }
            OnPropertyChanged();
        }
    }

    /// <summary>What the inline editor's tooltip says — the range it will accept.</summary>
    public string NTooltip => CanEditN
        ? $"Double-click to edit. {Label} accepts {Fmt(NMin)} to {Fmt(NMax)}."
        : DisabledReason;

    // THE SLIDER CARRIES NO TOOLTIP AT ALL (owner, 2026-08-20: "completely remove the slider
    // tooltips"). It used to bind ToolTip.Tip to DisabledReason, which is "" for every ordinary
    // enabled row — and Avalonia's tooltip service opens on any tip that is not null, an empty
    // string included, so hovering a normal slider popped the tooltip panel's own bordered box with
    // nothing in it (owner-reported the same day: "some kind of transparent rectangle rendering over
    // the sliders... it has a black stroke outline"). Nothing is lost by dropping it: NTooltip, on
    // the N field immediately to its left, already states the accepted range when the row is live and
    // DisabledReason when it is not.
    //
    // AN EMPTY STRING IS NOT "NO TOOLTIP". Anything bound to ToolTip.Tip anywhere in this
    // application must be non-empty whenever it is set at all, or it renders as that rectangle.

    /// <summary>
    /// Slack on each end, sized to <b>the display quantum of the format this field prints with</b>.
    /// </summary>
    /// <remarks>
    /// <see cref="Fmt"/> is <c>"0.#####"</c> — five DECIMAL places, so a printed bound is up to
    /// 5e-6 away from the real one. A tolerance below that refuses the number the field itself just
    /// showed, which is the one refusal a user cannot act on. Anything inside the slack is clamped
    /// onto the bound rather than taken literally, so nothing outside the range is ever applied.
    /// </remarks>
    private const double DisplaySlack = 5e-6;

    private bool InRange(double n) => n >= NMin - DisplaySlack && n <= NMax + DisplaySlack;

    private static string Fmt(double v) => v.ToString("0.#####", CultureInfo.InvariantCulture);

    /// <summary>The lowest N this row can actually be dragged to.</summary>
    public double NMin => Reachable?.Min ?? Range?.Min ?? 1.0;

    /// <summary>The highest N this row can actually be dragged to.</summary>
    public double NMax => Reachable?.Max ?? Range?.Max ?? 1.0;

    /// <summary>The range in force at this point in the sequence, or null when the transform dropped.</summary>
    public TransformRange? Range { get; internal set; }

    /// <summary>
    /// The sub-range of <see cref="Range"/> this row's N can actually settle in, given the linkage and
    /// where the other transforms are. <b>This is what the slider is bounded by</b>, not
    /// <see cref="Range"/> — see <c>MatchDesignerViewModel.RefreshReachableRanges</c> for why the two
    /// differ and what went wrong when the slider used the wrong one.
    /// </summary>
    public TransformRange? Reachable { get; internal set; }

    // THERE IS NO "no travel" DISABLE. A collapsed reachable interval used to switch this row off,
    // and it took the whole rack down on any design whose required ratio is out of reach — which is
    // exactly the design the user is trying to rescue (owner, 2026-08-20). The narrowing now applies
    // only when it describes a real interval; see MatchDesignerViewModel.RefreshReachableRanges.

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
    /// <remarks>
    /// <b>The BOUNDS go out before the value, and that ordering is load-bearing.</b> The slider clamps
    /// whatever it is given into the range it holds at that moment, so publishing N first handed it to
    /// a control still carrying the PREVIOUS row's Minimum and Maximum — and a value outside those was
    /// clamped and written back through the two-way binding as though the user had dragged it. See
    /// <see cref="N"/> for what that cost on the undo path.
    ///
    /// <para><b>Under the publishing flag</b>, which is the belt to that ordering's braces: a rebuild
    /// can legitimately move a row's range past its own N — the far end of an unreachable ratio does
    /// exactly that — and the clamp is then real. It is still not an edit, and the flag is what says
    /// so at the one place every one of these notifications leaves the view-model.</para>
    /// </remarks>
    internal void Refresh()
    {
        _owner.BeginPublish();
        try
        {
            OnPropertyChanged(nameof(Label));
            OnPropertyChanged(nameof(Record));
            OnPropertyChanged(nameof(Range));
            OnPropertyChanged(nameof(Reachable));
            OnPropertyChanged(nameof(NMin));
            OnPropertyChanged(nameof(NMax));
            OnPropertyChanged(nameof(N));
            OnPropertyChanged(nameof(NEntry));
            OnPropertyChanged(nameof(NTooltip));
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
        finally { _owner.EndPublish(); }
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

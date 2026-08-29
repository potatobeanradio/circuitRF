using System;
using System.Globalization;
using System.Linq;
using CircuitRF.Core.Matching;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.Matching;

/// <summary>Which badge a solutions row carries (match.md §9.5).</summary>
public enum MatchSolutionBadge
{
    /// <summary>Never applied in this design's history.</summary>
    NeverApplied,

    /// <summary>Applied at some point — the fingerprint is in <c>MatchDesign.AppliedSolutions</c>.</summary>
    PreviouslyApplied,

    /// <summary>The one the design is on right now.</summary>
    Current,
}

/// <summary>
/// One row of the solutions panel: the badge, the family and order it belongs to, the transform count,
/// the pairs each transform acts on, the two things worth warning about (a Q-adjust, a negative
/// element), the response, and Apply.
/// </summary>
/// <remarks>
/// <b>"Previously applied" survives a reload</b> because it is read from
/// <see cref="MatchDesign.AppliedSolutions"/> — the fingerprints MN-1 emits — and not from anything
/// this window remembers.
///
/// <para><b>The row names its own order and family since 2026-08-28.</b> The panel used to list the
/// solutions of ONE order and ONE response — both picked in the specification pane — so neither was
/// worth repeating on a card. It now lists every order and every family at once (owner: "I want the
/// Solutions panel to list all the solutions for every filter response and order"), and a card that
/// did not say which it was would be unreadable.</para>
///
/// <para><b>Only the exceptions are stated.</b> A Q-adjust and a negative element each get a line;
/// their absence gets nothing (owner, same round: <i>"if the solution is not Q-adjusted then the
/// Solution card should say nothing about it… a user would assume that the component values are all
/// positive and only needs to be warned if a value is negative"</i>). A card that said "no Q-adjust,
/// all values positive" would spend two lines telling the reader what they already assumed.</para>
/// </remarks>
public sealed partial class MatchSolutionRowViewModel : ObservableObject
{
    private readonly MatchDesignerViewModel _owner;

    internal MatchSolutionRowViewModel(
        MatchDesignerViewModel owner, MatchSolution solution, MatchSolutionBadge badge,
        int order, ResponseShape response, NetworkForm form, int bandCount = 1)
    {
        _owner = owner;
        Solution = solution;
        _badge = badge;
        Order = order;
        Response = response;
        Form = form;
        BandCount = bandCount;
        // Read once from the network the search produced, not recomputed per binding read: a solution
        // is immutable, and this is what the "allow negative components" filter is keyed on.
        HasNegativeComponents = solution.Network?.Elements.Any(e => e.Value <= 0) ?? false;
    }

    /// <summary>The solution this row applies.</summary>
    public MatchSolution Solution { get; }

    /// <summary>The network order it was found at.</summary>
    public int Order { get; }

    /// <summary>The response family it was found under.</summary>
    public ResponseShape Response { get; }

    /// <summary>The network form it was found in (match.md §16).</summary>
    public NetworkForm Form { get; }

    /// <summary>How many bands it was found over (match.md §18). 1 for every single-band row.</summary>
    public int BandCount { get; }

    /// <summary>
    /// True when some element of the finished network is zero or negative.
    /// </summary>
    /// <remarks>
    /// <b>Read off the network, not off the flag that permitted it.</b> The search runs with
    /// <c>AllowNegativeComponents</c> ON for every combination — that is a strict superset, since the
    /// flag only ever widens a transform's range — and the negative ones are then identified by
    /// looking at what came out. Keying the filter on which pass found a solution would have needed
    /// the whole cross-product searched twice to say the same thing.
    /// </remarks>
    public bool HasNegativeComponents { get; }

    /// <summary>Current / previously applied / never applied.</summary>
    [ObservableProperty] private MatchSolutionBadge _badge;

    /// <summary>True for the solution the design is on right now.</summary>
    public bool IsCurrent => Badge == MatchSolutionBadge.Current;

    partial void OnBadgeChanged(MatchSolutionBadge value)
    {
        OnPropertyChanged(nameof(IsCurrent));
        OnPropertyChanged(nameof(BadgeGlyph));
        OnPropertyChanged(nameof(BadgeTooltip));
    }

    /// <summary>The badge as the glyph the list shows.</summary>
    /// <remarks>
    /// <b>The tick is bigger, bolder and green</b> (owner, 2026-08-28: <i>"the solution that is
    /// currently being viewed has a check mark indicator on its card. This needs to be more prominent
    /// and obvious"</i>). The glyph alone was never going to carry it at 12 px in the same foreground
    /// as everything around it, so three things move together and only for the current row — this
    /// glyph's colour and weight, the title's weight, and the card's own border. All three are
    /// styles keyed on the <c>current</c> class, so the row's appearance is decided in one place in
    /// the AXAML rather than by three bindings that could disagree.
    /// </remarks>
    public string BadgeGlyph => Badge switch
    {
        MatchSolutionBadge.Current           => "✓",
        MatchSolutionBadge.PreviouslyApplied => "○",
        _                                    => "",
    };

    /// <summary>The badge's tooltip, because a tick and a ring are not self-explaining.</summary>
    public string BadgeTooltip => Badge switch
    {
        MatchSolutionBadge.Current           => "This is the solution the design is on.",
        MatchSolutionBadge.PreviouslyApplied => "Applied at some point in this design's history.",
        _                                    => "Never applied.",
    };

    /// <summary>
    /// "Chebyshev (single-match) · lowpass · order 4" — the card's own heading.
    /// </summary>
    /// <remarks>
    /// <b>Bandpass rows carry the form word too</b> (match.md §16.7). The panel lists all three forms
    /// side by side; a card that named its form only when the form was unusual would read as though
    /// the unnamed ones were a different KIND of answer.
    /// </remarks>
    public string TitleText =>
        $"{ResponseName} · {ShapeWord} · order {Order.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// The word in the middle of the heading: the FORM for a single-band row, "dual-band" or
    /// "tri-band" for a multiband one.
    /// </summary>
    /// <remarks>
    /// <b>Not both.</b> While the design is multiband every row is bandpass (match.md §18.6), so the
    /// form word would be the same on every card and would say nothing; the band count is the thing
    /// that distinguishes this list from the single-band one.
    /// </remarks>
    public string ShapeWord => BandCount switch
    {
        >= 3 => "tri-band",
        2 => "dual-band",
        _ => FormName(Form),
    };

    /// <summary>The response family, short enough for a card in the specification column.</summary>
    public string ResponseName => FamilyName(Response, Form, BandCount);

    /// <summary>The form, lower-case, as the card and the footer spell it.</summary>
    public static string FormName(NetworkForm form) => form switch
    {
        NetworkForm.Lowpass  => "lowpass",
        NetworkForm.Highpass => "highpass",
        _                    => "bandpass",
    };

    /// <summary>
    /// One family's short name — <b>the single place it is spelled</b>, because the solution cards
    /// and the filter's own family lines have to agree or a user cannot tell which line hides which
    /// cards.
    /// </summary>
    /// <remarks>
    /// <b>"single-match" and "double-match" are the broadband-matching literature's own terms</b>
    /// (match.md §6.9): one reactive termination prescribed against both. They replace "(Fano)" and
    /// "(2-ended)", which named the derivation rather than the outcome and which a user could not
    /// choose between. Display only — the enum members and the serialized spelling are untouched, and
    /// a renamed enum value would break every saved design for no gain.
    /// </remarks>
    public static string FamilyName(ResponseShape shape) => FamilyName(shape, NetworkForm.Bandpass);

    /// <inheritdoc cref="FamilyName(ResponseShape)"/>
    /// <param name="shape">The family.</param>
    /// <param name="form">The form the row was found in.</param>
    /// <remarks>
    /// <b>The single/double distinction is bandpass-only.</b> match.md §16.2: with the ratio pinned
    /// at DC the family has ONE free parameter, so a second prescribed Q does not exist and
    /// <see cref="ResponseShape.ChebyshevTwoEnded"/> is never searched in these forms. A lowpass row
    /// reading "Chebyshev (single-match)" would be drawing a contrast with an option that is not on
    /// offer there.
    /// </remarks>
    public static string FamilyName(ResponseShape shape, NetworkForm form) =>
        FamilyName(shape, form, 1);

    /// <inheritdoc cref="FamilyName(ResponseShape, NetworkForm)"/>
    /// <param name="shape">The family.</param>
    /// <param name="form">The form the row was found in.</param>
    /// <param name="bandCount">How many bands it was found over (match.md §18).</param>
    /// <remarks>
    /// <b>Multiband drops the qualifier for the same reason the other two forms do</b>: match.md
    /// §18.2 does not offer the double-match Chebyshev over two or three bands (it is a 2-D solve in
    /// (K, eps^2) for both end elements), so a card reading "Chebyshev (single-match)" would be
    /// drawing a contrast with an option that is not on offer there.
    /// </remarks>
    public static string FamilyName(ResponseShape shape, NetworkForm form, int bandCount) => shape switch
    {
        ResponseShape.ChebyshevFano when form != NetworkForm.Bandpass || bandCount >= 2 => "Chebyshev",
        ResponseShape.ChebyshevFano     => "Chebyshev (single-match)",
        ResponseShape.ChebyshevTwoEnded => "Chebyshev (double-match)",
        ResponseShape.Butterworth       => "Butterworth",
        _                               => "Bessel",
    };

    /// <summary>"2 transforms" / "1 transform".</summary>
    public string CountText =>
        Solution.Transforms.Count == 1 ? "1 transform" : $"{Solution.Transforms.Count} transforms";

    /// <summary>"(L2, L4) · (C3, C5)" — every pair, by element name.</summary>
    public string PairsText =>
        string.Join(" · ", Solution.Transforms.Select(t => $"({t.ElementA}, {t.ElementB})"));

    /// <summary>The Q-adjust, or empty when the solution needed none.</summary>
    public string QAdjustText => Solution.QAdjust > 0
        ? $"Q-adjusted to {Solution.QAdjust.ToString("0.###", CultureInfo.InvariantCulture)}"
        : "";

    /// <summary>The warning beside a solution with a non-positive element, or empty.</summary>
    public string NegativeNote => HasNegativeComponents
        ? "Has a negative element — realizable only by absorbing it into a neighbour."
        : "";

    /// <summary>
    /// Worst in-band return loss this solution reaches, dB — <b>signed, as the quantity is</b>.
    /// </summary>
    /// <remarks>
    /// <b>"RL -10.51 dB", not "RL 10.51 dB"</b> (owner, 2026-08-28). Return loss is quoted both ways
    /// in the field — as a positive magnitude and as the negative |S11| it is read off a plot from —
    /// and this card sits beside the response plots, whose y axis is negative. Two spellings of one
    /// number in one window is the ambiguity worth removing, and the sign is what the underlying
    /// <see cref="MatchSolution.WorstReturnLossDb"/> has always carried; it was being negated here
    /// only for display.
    /// </remarks>
    public string ReturnLossText =>
        $"RL {Solution.WorstReturnLossDb.ToString("0.00", CultureInfo.InvariantCulture)} dB";

    /// <summary>
    /// True when some element came out above 1 H / 1 F or below 1e-24 — exact, response-preserving
    /// and unbuildable. The solution is still offered; saying so is the point.
    /// </summary>
    public bool ImplausibleValues => Solution.ImplausibleValues;

    /// <summary>The warning shown beside such a solution.</summary>
    public string ImplausibleNote => ImplausibleValues
        ? "One of its elements is outside 1e-24 .. 1 in SI units — exact, and not buildable."
        : "";

    /// <summary>Applies this solution to the design.</summary>
    /// <remarks>
    /// <b>There is no Apply / Applied button any more</b> (owner, 2026-08-28: it comes off the card,
    /// and clicking the card applies the solution). The card's own <c>ApplyText</c> went with it —
    /// the tick, the bold title and the green border already say which row the design is on, and a
    /// button reading "Applied" beside them was a fourth mark for the same fact. The entry point is
    /// unchanged and is still the only one: the window's list turns a click, and an arrow key, into a
    /// selection, and a selection calls this.
    /// </remarks>
    public void Apply() => _owner.ApplySolution(this);
}

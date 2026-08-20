using System;
using System.Collections.Generic;
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
/// One row of the solutions panel: the badge, the transform count, the pairs each transform acts on,
/// the Q-adjust when non-zero, the response, and Apply.
/// </summary>
/// <remarks>
/// <b>"Previously applied" survives a reload</b> because it is read from
/// <see cref="MatchDesign.AppliedSolutions"/> — the fingerprints MN-1 emits — and not from anything
/// this window remembers.
/// </remarks>
public sealed partial class MatchSolutionRowViewModel : ObservableObject
{
    private readonly MatchDesignerViewModel _owner;

    internal MatchSolutionRowViewModel(
        MatchDesignerViewModel owner, MatchSolution solution, MatchSolutionBadge badge, ResponseShape response)
    {
        _owner = owner;
        Solution = solution;
        Badge = badge;
        Response = response;
    }

    /// <summary>The solution this row applies.</summary>
    public MatchSolution Solution { get; }

    /// <summary>Current / previously applied / never applied.</summary>
    public MatchSolutionBadge Badge { get; internal set; }

    /// <summary>The response family it was found under.</summary>
    public ResponseShape Response { get; }

    /// <summary>The badge as the glyph the list shows.</summary>
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

    /// <summary>"2 transforms" / "1 transform".</summary>
    public string CountText =>
        Solution.Transforms.Count == 1 ? "1 transform" : $"{Solution.Transforms.Count} transforms";

    /// <summary>"(L2, L4) · (C3, C5)" — every pair, by element name.</summary>
    public string PairsText =>
        string.Join(" · ", Solution.Transforms.Select(t => $"({t.ElementA}, {t.ElementB})"));

    /// <summary>The Q-adjust, or empty when the solution needed none.</summary>
    public string QAdjustText => Solution.QAdjust > 0
        ? $"Q-adjust {Solution.QAdjust.ToString("0.###", CultureInfo.InvariantCulture)}"
        : "";

    /// <summary>Worst in-band return loss this solution reaches, dB.</summary>
    public string ReturnLossText =>
        $"RL {(-Solution.WorstReturnLossDb).ToString("0.00", CultureInfo.InvariantCulture)} dB";

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
    public void Apply() => _owner.ApplySolution(this);
}

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
/// <param name="GapMaxS11">The largest |S11| in the first gap, or 0 when there is no gap.</param>
/// <param name="GapLo">Lower edge of the first gap, Hz — band 1's upper edge.</param>
/// <param name="GapHi">Upper edge of the first gap, Hz — band 2's lower edge.</param>
/// <param name="Gap2MaxS11">The largest |S11| in the SECOND gap. Tri-band only; 0 otherwise.</param>
/// <param name="Gap2Lo">Lower edge of the second gap, Hz — band 2's upper edge.</param>
/// <param name="Gap2Hi">Upper edge of the second gap, Hz — band 3's lower edge.</param>
/// <param name="CeilingDb">The binding Fano ceiling over the effective bands, dB negative
/// (match.md §18.10). −∞ when neither end is reactive.</param>
/// <param name="CeilingEnd">Which termination sets it — 1, 2, or 0 for none.</param>
/// <param name="Ceiling1Db">Termination 1's own ceiling over the same bands.</param>
/// <param name="Ceiling2Db">Termination 2's own ceiling over the same bands.</param>
/// <param name="CeilingTypedDb">The binding ceiling over the bands AS TYPED, before §18.3's
/// widening — the difference from <paramref name="CeilingDb"/> is what mirroring cost.</param>
/// <param name="CeilingOverSpanDb">The binding ceiling over the single outer span — what a
/// prototype that does not exclude the gaps actually spends.</param>
/// <param name="GapRise">How far the prototype rises above its passband level in each gap at the
/// design's order, in gap order. Empty for a single band.</param>
/// <param name="GapOpensAtOrder">The smallest offered order at which every gap opens, or 0.</param>
public sealed record MatchStatus(
    double Q1, double Q2, double WorstReturnLossDb, double InsertionLossDb, double RippleDb,
    double Achieved, double Required, bool OnTarget, MatchRefusal? Refusal,
    double GapMaxS11 = 0.0, double GapLo = 0.0, double GapHi = 0.0,
    double Gap2MaxS11 = 0.0, double Gap2Lo = 0.0, double Gap2Hi = 0.0,
    double CeilingDb = double.NegativeInfinity, int CeilingEnd = 0,
    double Ceiling1Db = double.NegativeInfinity, double Ceiling2Db = double.NegativeInfinity,
    double CeilingTypedDb = double.NegativeInfinity,
    double CeilingOverSpanDb = double.NegativeInfinity,
    IReadOnlyList<double>? GapRise = null, int GapOpensAtOrder = 0)
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

    /// <summary>
    /// The gap line — <b>the number that says the design is working</b>, and empty for a single band.
    /// </summary>
    /// <remarks>
    /// match.md §18.4: a finite ladder buys its in-band return loss by leaving the gap unmatched, and
    /// the gap maximum RISES with order because a higher-degree polynomial is bigger there. A status
    /// strip that showed only in-band numbers would hide the mechanism, so this sits beside the worst
    /// in-band return loss rather than behind a tooltip.
    /// </remarks>
    public string GapText => GapLine(GapLo, GapHi, GapMaxS11, 0);

    /// <summary>
    /// The SECOND gap line — tri-band only, and empty otherwise.
    /// </summary>
    /// <remarks>
    /// <b>A separate line rather than one line naming both</b>, because the two gaps of a tri-band
    /// design are independent numbers a user compares against each other: mirroring makes them come
    /// out equal on a symmetric spec and unequal as soon as the middle band is off centre, and that
    /// difference is the first thing to read when the response is not what was wanted.
    /// </remarks>
    public string GapText2 => GapLine(Gap2Lo, Gap2Hi, Gap2MaxS11, 1);

    private string GapLine(double lo, double hi, double max, int index)
    {
        if (IsRefused || !(hi > lo) || !(max > 0)) return "";
        double db = 20.0 * Math.Log10(max);
        string line = $"gap {F(lo / 1e9, "0.###")}–{F(hi / 1e9, "0.###")} GHz: "
                      + $"max |S11| {F(max, "0.000")} ({F(db, "0.0")} dB)";

        // The PROTOTYPE's rise beside the network's own gap mismatch (match.md §18.10). They answer
        // different questions: the |S11| says how much is reflected there, the rise says whether the
        // polynomial excludes the gap AT ALL — and at low order on a narrow middle band it does not,
        // which is the one thing the |S11| number cannot tell anybody.
        if (GapRise is { Count: > 0 } r && index < r.Count && double.IsFinite(r[index]))
            line += $" · prototype rise ×{F(r[index], "0.00")}";
        return line;
    }

    /// <summary>
    /// The Fano ceiling line — <b>what the best possible network could do here</b> (match.md §18.10).
    /// </summary>
    /// <remarks>
    /// <b>Computed from the design alone, so it survives a refusal</b>, which is exactly when a user
    /// needs it: a synthesis that reaches nothing and a ceiling of −3 dB are the same fact stated
    /// twice, and only one of them says what to change.
    ///
    /// <para><b>"at the ceiling" fires against EITHER ceiling</b>, the one over the bands or the one
    /// over the outer span, because a network can be at a wall in two different ways. Within a dB of
    /// the band ceiling, nothing lossless does better over these bands. Within a dB of the outer-span
    /// ceiling, the network is spending its whole budget across the span instead of excluding the
    /// gaps — the owner's tri-band fixture sits at −2.60 dB against an outer-span ceiling of −3.11
    /// and a band ceiling of −6.43, and calling that "not at the ceiling" because it missed the
    /// unreachable number would be the wrong half of the truth. The gap-rise note says which of the
    /// two it is; the tooltip carries both figures.</para>
    /// </remarks>
    public string CeilingText
    {
        get
        {
            if (CeilingEnd == 0 || !double.IsFinite(CeilingDb)) return "";
            string line =
                $"Fano ceiling {F(-CeilingDb, "0.0")} dB (termination {CeilingEnd}, over the bands)";
            if (IsRefused || WorstReturnLossDb == 0.0 || !double.IsFinite(WorstReturnLossDb))
                return line;

            double slack = MatchFanoBound.AtCeilingSlackDb;
            bool atBands = WorstReturnLossDb - CeilingDb <= slack;
            bool atSpan = double.IsFinite(CeilingOverSpanDb)
                          && WorstReturnLossDb - CeilingOverSpanDb <= slack;
            return atBands || atSpan ? line + " — at the ceiling" : line;
        }
    }

    /// <summary>Both ends, both other band sets, and what the mirror widening cost.</summary>
    public string CeilingTip
    {
        get
        {
            if (CeilingEnd == 0 || !double.IsFinite(CeilingDb)) return "";
            var parts = new List<string>(5);
            if (double.IsFinite(Ceiling1Db)) parts.Add($"Termination 1: {F(Ceiling1Db, "0.0")} dB.");
            if (double.IsFinite(Ceiling2Db)) parts.Add($"Termination 2: {F(Ceiling2Db, "0.0")} dB.");
            if (double.IsFinite(CeilingTypedDb))
                parts.Add($"Over the bands as typed: {F(CeilingTypedDb, "0.0")} dB.");
            if (double.IsFinite(CeilingOverSpanDb))
                parts.Add($"Over the whole span: {F(CeilingOverSpanDb, "0.0")} dB.");
            if (double.IsFinite(CeilingTypedDb) && CeilingDb - CeilingTypedDb > 0.05)
                parts.Add($"Widening to mirror cost {F(CeilingDb - CeilingTypedDb, "0.0")} dB of "
                          + "ceiling.");
            return string.Join(" ", parts);
        }
    }

    /// <summary>The loss line.</summary>
    public string LossText => IsRefused
        ? ""
        : $"IL {F(InsertionLossDb, "0.000")} dB, ripple {F(RippleDb, "0.000")} dB";

    /// <summary>The transform-product line, with the tick or the cross §4.8 asks for.</summary>
    /// <remarks>
    /// <b>A cross must never sit beside two numbers that look equal</b> (owner-reported, 2026-08-20:
    /// "Π N² 10 / 10 ✘ not reached" on a network that was matched). Three decimals is the right
    /// density for the ✔ case and hides the whole disagreement in the ✘ one, so the shortfall is
    /// stated outright when there is one. The tolerance that decides the verdict is
    /// <c>MatchLinkage.RatioTolerance</c>; anything that fails it is now at least 0.0001 % off, which
    /// this line can show.
    /// </remarks>
    public string RatioText
    {
        get
        {
            string head = $"Π N² {F(Achieved, "0.###")} / {F(Required, "0.###")}  ";
            if (OnTarget) return head + "✔ matched";

            double off = Required != 0 ? (Achieved / Required - 1.0) * 100.0 : double.NaN;
            string by = double.IsFinite(off) ? $" ({off:+0.####;-0.####}%)" : "";
            return head + "✘ not reached" + by;
        }
    }

    /// <summary>The whole strip as one line — what a test and a tooltip both read.</summary>
    /// <remarks>
    /// <b>The ceiling line survives a refusal</b> — <c>Text</c> drops every other line but Q, because
    /// the numeric readouts are meaningless when nothing was synthesised, and the ceiling is not one
    /// of them: it is arithmetic on the specification and it is at its most useful precisely here.
    /// </remarks>
    public string Text => IsRefused
        ? string.Join("   ", new[] { QText, CeilingText }.Where(t => t.Length > 0))
          + $"   —   {Refusal!.Message}"
        : string.Join(
            "   ",
            new[] { QText, ReturnLossText, CeilingText, GapText, GapText2, LossText, RatioText }
                .Where(t => t.Length > 0));
}

public sealed partial class MatchDesignerViewModel
{
    /// <summary>The grid view's rows — one per element of the finished ladder.</summary>
    public ObservableCollection<MatchElementRowViewModel> Elements { get; } = [];

    /// <summary>The schematic view's geometry.</summary>
    [ObservableProperty] private MatchLadderLayout _ladder = MatchLadderLayout.Empty;

    /// <summary>The status strip.</summary>
    [ObservableProperty] private MatchStatus _status = MatchStatus.Empty;

    /// <summary>Everything the rebuild wanted to say that is not a refusal — dropped transforms,
    /// clamped N's, a fingerprint mismatch.</summary>
    public ObservableCollection<string> Notes { get; } = [];

    /// <summary>
    /// The legend the schematic view shows when anything is absorbed. One line, and it now NAMES the
    /// elements it is about.
    /// </summary>
    /// <remarks>
    /// <b>Nothing is dimmed any more</b> (owner, 2026-08-20), so a legend that said "dimmed elements
    /// are…" pointed at a treatment that no longer exists. Naming them is what is left, and it is
    /// strictly better than the wash was: the reader gets the answer without having to compare
    /// brightnesses across a drawing.
    /// </remarks>
    public string LadderLegend
    {
        get
        {
            var absorbed = Ladder.Elements
                .Where(e => e.Role == MatchElementRole.Absorbed)
                .Select(e => e.Name)
                .ToList();
            // ONE CLAUSE (owner, 2026-08-28). It used to add "drawn beside the termination that
            // supplies them — this component does not contain them", which is two more clauses saying
            // the same thing as the first in a shape that does not read: the drawing already puts the
            // element beside its termination, and "supplied by the external termination" already says
            // the component has not got it.
            return absorbed.Count == 0
                ? ""
                : $"{string.Join(", ", absorbed)} {(absorbed.Count == 1 ? "is" : "are")} supplied by "
                  + $"the external termination{(absorbed.Count == 1 ? "" : "s")}.";
        }
    }

    /// <summary>The grid's clipboard form (§9.3), and the component-listing export's own rows.</summary>
    public string ElementsCsv => MatchElementRowViewModel.ToCsv(Elements);

    private void RefreshLadderAndGrid()
    {
        var network = _rebuild?.Network;

        Ladder = MatchLadderLayout.Build(network, _rebuild?.Applied, ValueTextFor, OhmsTextFor);

        // ── The rows are REUSED, never replaced ───────────────────────────────
        //
        // The value column is an inline editor now (MatchElementRowViewModel.ValueEntry), and
        // committing one rebuilds the ladder — which is this method. Clearing the collection here, as
        // this used to, would destroy the ItemsControl container holding the very editor that was
        // mid-commit. So the collection is resized and each row is overwritten in place, exactly as
        // RefreshTransformRows does with the rack for the same reason.
        var display = network is not null
            ? MatchLadderLayout.DisplayOrder(network.Elements).ToList()
            : [];

        while (Elements.Count > display.Count) Elements.RemoveAt(Elements.Count - 1);
        while (Elements.Count < display.Count) Elements.Add(new MatchElementRowViewModel(this));

        for (int i = 0; i < display.Count; i++)
        {
            var e = display[i];
            var quantity = MatchValueFormat.QuantityOf(e.Type);
            var (text, unit) = MatchValueFormat.Format(
                e.Value, quantity, Settings.UnitFor(quantity), Settings.SignificantDigits);
            // The grid lists an element under the SAME name the schematic labels it with — "L1", not
            // "MN1.L1" (owner, 2026-08-20). The qualified spelling was a path to a component that
            // does not exist: a Match contains no sub-instances, and the one place the user reads
            // both views at once is this pane, where two names for one element is just a puzzle.
            // The same order the schematic draws in, so the two views of one network read down the
            // page together — see MatchLadderLayout.DisplayOrder for why that is not ladder order.
            Elements[i].Update(
                e.Name, e.Name, e.Type, e.IsShunt, e.Value,
                MatchLadderLayout.RoleOf(e), text, unit);
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

    /// <summary>
    /// One termination glyph's label: the ladder's own port reference, <b>and the declared one when
    /// they disagree</b>.
    /// </summary>
    /// <remarks>
    /// <b>Owner-reported, 2026-08-20:</b> <i>"I changed the TermG and it updated in the Termination 1
    /// specification, but did not update in the schematic (the old value was retained)."</i>
    ///
    /// <para>The glyph was not stale — it quotes <c>MatchNetwork.R1</c>/<c>R2</c>, the reference the
    /// plotted response is actually referenced to, and that is deliberate. But the FAR end's reference
    /// is the analysis end's scaled by Π N², so re-declaring it does not move it until the transforms
    /// are re-solved; the user typed 25 Ω into a field and the same glyph went on reading 15 Ω, with
    /// the only hint three panes away in the status strip. <see cref="SetTermination"/> now re-solves
    /// the linkage so the ladder reaches the new number whenever it can — and when it cannot (link
    /// off, every transform locked, the target outside their range) the glyph says both numbers rather
    /// than silently showing one the user did not type.</para>
    /// </remarks>
    private string OhmsTextFor(int end, double ladderR)
    {
        string unit = Settings.UnitFor(MatchQuantity.Resistance);
        int digits = Settings.SignificantDigits;
        string text = MatchValueFormat.FormatWithUnit(ladderR, MatchQuantity.Resistance, unit, digits);

        double declared = (end == 1 ? _design.Term1 : _design.Term2).R;
        if (!double.IsFinite(ladderR) || !double.IsFinite(declared)) return text;

        // ── The annotation is suppressed when it would repeat the number beside it ──
        //
        // Owner-reported, 2026-08-20: "Terminal 2 is matched and I get no warnings, but the schematic
        // instance still says 'Z = 50 Ω (target 50 Ω)'. It should just say 'Z = 50 Ω'."
        //
        // It did that because the guard was NUMERIC — a relative 1e-9 — while the label is RENDERED at
        // the readout's significant digits. A probed termination carries 49.999999999999993 and the
        // ladder reaches it to within a part in 10^10 but not a part in 10^9, so two numbers that are
        // the same number as far as anything on screen is concerned were reported as a disagreement,
        // in the one place a disagreement means "your network does not present what you asked for".
        //
        // The comparison is now on the rendered STRINGS, which is the same rule CommitInlineEdit's
        // RendersAsShown already applies to the same pane and for the same reason: two values that
        // render identically are one value to a field that can only show one of them. A real shortfall
        // is still stated — and the status strip's Π N² line carries the exact size of it, to a
        // precision this label was never going to have.
        string declaredText =
            MatchValueFormat.FormatWithUnit(declared, MatchQuantity.Resistance, unit, digits);
        if (string.Equals(text, declaredText, StringComparison.Ordinal)) return text;

        return text + " (target " + declaredText + ")";
    }

    private void RefreshStatus()
    {
        double om0 = _design.Omega0;
        double q1 = _design.Term1.QAt(om0), q2 = _design.Term2.QAt(om0);
        var network = _rebuild?.Network;

        // ── Feasibility, from the DESIGN alone (match.md §18.10) ──
        //
        // None of this waits for the rebuild and none of it is invalidated by a refusal: the Fano
        // ceiling is a theorem about the terminations and the bands, and the gap rise is a property
        // of the prototype the order asks for. Both are the numbers the owner's tri-band report was
        // missing — a correct synthesis that produced a single wideband match, with nothing on screen
        // saying that order 2 cannot exclude those gaps or that termination 2's wall is at −6.4 dB.
        var (c1, c2, binding) = MatchFanoBound.Of(_design);
        var typed = MatchFanoBound.OfTypedBands(_design).Binding;
        var span = MatchFanoBound.OfOuterSpan(_design).Binding;
        var effective = _design.Effective;
        var rise = MatchFanoBound.GapRise(effective, _design.Order);
        int opensAt = rise.Count > 0 ? MatchFanoBound.GapOpensAtOrder(effective) : 0;

        int ceilingEnd = binding.IsBounded ? binding.End : 0;
        double ceilingDb = binding.IsBounded ? binding.CeilingDb : double.NegativeInfinity;

        RefreshGapRiseNote(effective, rise, opensAt, span);

        if (network is null)
        {
            Status = new MatchStatus(
                q1, q2, 0, 0, 0, 1, 1, false, _rebuild?.Refusal,
                CeilingDb: ceilingDb, CeilingEnd: ceilingEnd,
                Ceiling1Db: c1.CeilingDb, Ceiling2Db: c2.CeilingDb,
                CeilingTypedDb: typed.CeilingDb, CeilingOverSpanDb: span.CeilingDb,
                GapRise: rise, GapOpensAtOrder: opensAt);
        }
        else
        {
            // Over the EFFECTIVE bands (match.md §18.3) — which for a single band is (F1, F2)
            // exactly as before. Insertion loss stays on the FIRST band: it is one number and a
            // ripple, and quoting it across a gap the network deliberately reflects would report the
            // gap's rejection as loss.
            var e = _design.Effective;
            double worst = MatchResponse.WorstReturnLossDb(network, _design.Bands);
            var (il, ripple) = MatchResponse.InsertionLoss(network, e.F1, e.F2);

            // One gap for a dual-band design, two for tri, none for one band — read off the design
            // rather than reconstructed here, so the strip and the ladder agree by construction.
            var gaps = _design.Gaps;
            var (g1Lo, g1Hi) = gaps.Count > 0 ? gaps[0] : (0.0, 0.0);
            var (g2Lo, g2Hi) = gaps.Count > 1 ? gaps[1] : (0.0, 0.0);

            Status = new MatchStatus(
                q1, q2, worst, il, ripple,
                _rebuild!.Achieved, _rebuild.Required, _rebuild.OnTarget, null,
                gaps.Count > 0 ? MatchResponse.GapMaxS11(network, g1Lo, g1Hi) : 0.0, g1Lo, g1Hi,
                gaps.Count > 1 ? MatchResponse.GapMaxS11(network, g2Lo, g2Hi) : 0.0, g2Lo, g2Hi,
                ceilingDb, ceilingEnd, c1.CeilingDb, c2.CeilingDb,
                typed.CeilingDb, span.CeilingDb, rise, opensAt);
        }

        RefreshFeasibilityHint();

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

    // ── Feasibility (match.md §18.10) ─────────────────────────────────────────

    /// <summary>
    /// The Frequency Band card's second note: what the chosen order does to the gaps, or empty when
    /// it excludes them.
    /// </summary>
    /// <remarks>
    /// <b>The sentence the owner's tri-band report was missing.</b> A prototype whose gap rise is 1
    /// is the single-band hull polynomial — the design is a wideband match over the outer span and
    /// the three bands are decoration. That is not a synthesis failure and there is no refusal to
    /// raise, because at that degree no polynomial does better; it is a fact about the band geometry
    /// and the order, and it belongs beside the bands that caused it.
    /// </remarks>
    [ObservableProperty] private string _gapRiseNote = "";

    /// <summary>
    /// The loosen hints, under the solutions panel — <b>a hint, never a refusal</b>.
    /// </summary>
    /// <remarks>
    /// Shown only when the ceiling is genuinely in the way (above <see cref="HintCeilingFloorDb"/>)
    /// AND the search has finished either empty or against that wall. Solutions that exist still
    /// list; this sits beside them and says what the wall is and what would move it.
    /// </remarks>
    [ObservableProperty] private string _feasibilityHint = "";

    /// <summary>
    /// The ceiling above which a hint is worth showing, dB.
    /// </summary>
    /// <remarks>
    /// <b>−10 dB is where a ceiling stops being an explanation and starts being an excuse.</b> A
    /// design whose ceiling is −20 dB and whose search reached −16 has not been stopped by physics,
    /// and telling it about Fano would be noise; a design whose ceiling is −6.4 dB has been stopped
    /// by nothing else.
    /// </remarks>
    public const double HintCeilingFloorDb = -10.0;

    private void RefreshGapRiseNote(
        EffectiveBands bands, IReadOnlyList<double> rise, int opensAt, FanoCeiling span)
    {
        if (rise.Count == 0 || !rise.Any(r => r <= 1.0 + 1e-6)) { GapRiseNote = ""; return; }

        string kind = bands.Count >= 3 ? "tri-band" : "dual-band";
        var (lo, hi) = bands.Outer;
        string ceiling = span.IsBounded
            ? $" (ceiling {span.CeilingDb.ToString("0.0", CultureInfo.InvariantCulture)} dB)"
            : "";
        string head =
            $"At order {_design.Order.ToString(CultureInfo.InvariantCulture)} the {kind} prototype "
            + "does not exclude the gaps — this is a single-band match over "
            + $"{Ghz(lo)}–{Ghz(hi)} GHz{ceiling}.";

        if (opensAt > 0)
        {
            var at = MatchFanoBound.GapRise(bands, opensAt);
            string factor = at.Count > 0
                ? $" (rise ×{at[0].ToString("0.#", CultureInfo.InvariantCulture)})"
                : "";
            GapRiseNote = head
                + $" The gaps open at order {opensAt.ToString(CultureInfo.InvariantCulture)}{factor}.";
            return;
        }

        GapRiseNote = head
            + " No offered order opens them for this band geometry; widen the middle band or move "
            + "the outer bands closer.";
    }

    private void RefreshFeasibilityHint()
    {
        var (_, _, binding) = MatchFanoBound.Of(_design);
        if (!binding.IsBounded || binding.CeilingDb <= HintCeilingFloorDb || !SolutionsComplete)
        {
            FeasibilityHint = "";
            return;
        }

        bool show;
        if (_allSolutions.Count == 0)
        {
            show = true;
        }
        else
        {
            // The BEST worst-RL any listed solution reaches — the most negative, since the quantity
            // is signed the way MatchResponse quotes it.
            double best = _allSolutions.Min(r => r.Solution.WorstReturnLossDb);
            double slack = MatchFanoBound.AtCeilingSlackDb;
            double spanDb = MatchFanoBound.OfOuterSpan(_design).Binding.CeilingDb;
            show = best - binding.CeilingDb <= slack
                   || (double.IsFinite(spanDb) && best - spanDb <= slack);
        }

        if (!show) { FeasibilityHint = ""; return; }

        var remedies = MatchFanoBound.Remedies(_design, MatchFanoBound.HintTargetDb);
        string bands = string.Join(
            " / ", _design.Bands.Select(b => $"{Ghz(b.Lo)}–{Ghz(b.Hi)}"));
        string head =
            $"The best any lossless network can do here is "
            + $"{binding.CeilingDb.ToString("0.0", CultureInfo.InvariantCulture)} dB, set by "
            + $"termination {binding.End.ToString(CultureInfo.InvariantCulture)} "
            + $"({Describe(binding.End == 1 ? _design.Term1 : _design.Term2)}) over {bands} GHz.";

        FeasibilityHint = remedies.Count == 0
            ? head
            : head
              + $" To reach {MatchFanoBound.HintTargetDb.ToString("0.#", CultureInfo.InvariantCulture)}"
              + " dB: " + string.Join("; or ", remedies.Select(r => r.Sentence)) + ".";
    }

    /// <summary>"1.25 Ω + 5 pF series" — the termination, as the hint's first sentence names it.</summary>
    private static string Describe(Termination t)
    {
        string r = MatchValueFormat.FormatWithUnit(t.R, MatchQuantity.Resistance, null, 3);
        if (!t.HasReactance) return r;

        var quantity = t.Kind == ReactanceKind.C ? MatchQuantity.Capacitance : MatchQuantity.Inductance;
        string x = MatchValueFormat.FormatWithUnit(t.Value, quantity, null, 3);
        string join = t.Topology == TerminationTopology.Parallel ? " ‖ " : " + ";
        string how = t.Topology == TerminationTopology.Parallel ? "" : " series";
        return r + join + x + how;
    }

    private static string Ghz(double hz) =>
        (hz / 1e9).ToString("0.###", CultureInfo.InvariantCulture);

    // ── Solutions ─────────────────────────────────────────────────────────────

    /// <summary>The solutions panel's rows, in MN-1's own order: fewest transforms, then position,
    /// then Q-adjust — <b>filtered</b>. Filled batch by batch as the background cross-product search
    /// finds them, and kept in step with <see cref="Filter"/>; the unfiltered list is
    /// <see cref="AllSolutions"/>. See <c>MatchDesignerViewModel.Analysis.cs</c>.</summary>
    public ObservableCollection<MatchSolutionRowViewModel> Solutions { get; } = [];

    /// <summary>The footer's own line: "7 solutions · applied: 2 transforms, Chebyshev (Fano) order 4".</summary>
    [ObservableProperty] private string _solutionsSummary = "";

    /// <summary>
    /// True when some listed row is the one the design is on — what the header's "scroll to the
    /// applied solution" button is enabled by.
    /// </summary>
    /// <remarks>
    /// False for a design carrying a hand-set transform set, which matches no row: there is nothing
    /// to scroll to, and a button that scrolls nowhere is worse than one that is plainly unavailable.
    /// </remarks>
    public bool HasAppliedSolution => Solutions.Any(r => r.IsCurrent);

    /// <summary>Non-empty when the whole cross-product came back empty — said plainly, with MN-1's own
    /// numbers behind it. See <c>LandSearchComplete</c>.</summary>
    [ObservableProperty] private string _solutionsRefusal = "";

    /// <summary>
    /// Applies one solution: <b>its order and its response family</b> as well as its transforms, its
    /// Q-adjust and its fingerprint into <see cref="MatchDesign.AppliedSolutions"/> — which is what
    /// makes the "previously applied" badge survive a reload.
    /// </summary>
    /// <remarks>
    /// <b>A card now carries the order and the family it was found at</b> (owner, 2026-08-28: the
    /// specification pane's Order and Filter Response cards are gone, and the list spans all of both),
    /// so applying one has to move the design onto them. Before that change every row shared the
    /// design's own order and family and there was nothing to carry.
    ///
    /// <para><c>AllowNegativeComponents</c> is set from what the SOLUTION contains rather than left
    /// where the user had it. The search runs with it on for every combination, so an applied row may
    /// need it — and a rebuild that clamped the N's back inside their positivity ranges would quietly
    /// produce a different network from the one the card described. A row with no negative element
    /// clears it for the same reason in reverse: the clamp is a no-op on a rack that is already
    /// inside its ranges, so leaving the flag on would only widen what a later drag may reach.</para>
    ///
    /// <para><b>This does not restart the solution search.</b> None of the five things it writes is
    /// part of <c>MatchSpecKey</c>, so <c>QueueSolutionSearch</c> leaves the list alone and only the
    /// badges move — see MatchDesignerViewModel.Analysis.cs.</para>
    /// </remarks>
    public void ApplySolution(MatchSolutionRowViewModel row)
    {
        // The user picked this row, so the badge move it causes is not a move to report — see
        // AppliedSolutionMoved. The auto-solve goes to the private overload and does report.
        ApplyingByClick = true;
        try { ApplySolution(row, 0); }
        finally { ApplyingByClick = false; }
    }

    /// <inheritdoc cref="ApplySolution(MatchSolutionRowViewModel)"/>
    /// <param name="row">The solution to apply.</param>
    /// <param name="amendStamp">
    /// Non-zero when this apply is the second half of one user gesture — the termination auto-solve —
    /// in which case its commit absorbs the gesture's earlier one rather than adding an undo entry
    /// beside it. See <c>CommitCore</c>.
    /// </param>
    private void ApplySolution(MatchSolutionRowViewModel row, long amendStamp)
    {
        ArgumentNullException.ThrowIfNull(row);
        AsOneEdit(() => ApplySolutionCore(row, amendStamp));
    }

    private void ApplySolutionCore(MatchSolutionRowViewModel row, long amendStamp)
    {

        _design.Order = row.Order;
        _design.Response = row.Response;
        _design.Form = row.Form;
        _design.AllowNegativeComponents = row.HasNegativeComponents;
        _design.Transforms = [.. row.Solution.Transforms];
        _design.QAdjust = row.Solution.QAdjust;
        if (!_design.AppliedSolutions.Contains(row.Solution.Fingerprint, StringComparer.Ordinal))
            _design.AppliedSolutions.Add(row.Solution.Fingerprint);

        Refresh(specChanged: true);
        CommitCore(amendStamp);
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
    public bool CanFlatten => FlattenAvailability.CanRun && !IsOrphaned;

    /// <summary>What the Flatten button offers, or the one thing standing in its way.</summary>
    public string FlattenTooltip => IsOrphaned ? OrphanNote : FlattenAvailability.Reason;

    /// <summary>
    /// What the last Flatten did, for a Designer that has no Messages region to post it to.
    /// </summary>
    /// <remarks>
    /// A schematic-bound Designer reports through <c>SchematicViewModel.MessageSink</c>, which is the
    /// workspace's Messages pane. A standalone one (Tools ▸ Match Designer) has no schematic and so no
    /// sink — and a flatten that writes a folder somewhere and says nothing is a flatten the user has
    /// no reason to believe happened.
    /// </remarks>
    [ObservableProperty] private string _flattenOutcome = "";

    /// <summary>True when <see cref="FlattenOutcome"/> is a refusal rather than a success.</summary>
    [ObservableProperty] private bool _flattenFailed;

    /// <summary>Records one flatten outcome for the footer to show.</summary>
    public void SetFlattenOutcome(string message, bool ok)
    {
        FlattenOutcome = message ?? "";
        FlattenFailed = !ok;
    }

    private void RefreshFlatten()
    {
        FlattenAvailability = _isStandalone
            ? MatchFlattenService.StandaloneAvailability(_rebuild, InstanceName, _standaloneRoot)
            : MatchFlattenService.Availability(_schematicVm, _target);
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
        // A standalone Designer writes the cell and stops: there is no schematic to put an instance
        // into, no undo stack to record the write on, and no workspace the destination has to be
        // inside of. See MatchDesignerViewModel.SetStandalone.
        if (_isStandalone)
        {
            var standalone = MatchFlattenService.RunStandalone(
                _rebuild, _design, InstanceName, parentDir, cellName,
                significantDigits: Settings.SignificantDigits);
            RefreshFlatten();
            return standalone;
        }

        if (_schematicVm is null || _target is null)
            return new(false, "This Designer is not bound to a component.", null, null);

        var result = MatchFlattenService.Run(
            _schematicVm, _target, parentDir, cellName, replaceInPlace,
            significantDigits: Settings.SignificantDigits);
        if (result.Ok) _schematicVm.MessageSink?.Success(result.Message);
        else _schematicVm.MessageSink?.Warning(result.Message);
        RefreshFlatten();
        return result;
    }
}

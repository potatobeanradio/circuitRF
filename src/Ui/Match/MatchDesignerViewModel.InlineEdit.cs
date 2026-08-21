using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CircuitRF.Core.Matching;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.Matching;

/// <summary>What one double-clicked label in the network pane turns out to be.</summary>
public enum MatchInlineEditKind
{
    /// <summary>A ladder element's value — reachable only through the transforms.</summary>
    ElementValue,

    /// <summary>A <c>TermG</c>'s resistance, which is a specification input and is written directly.</summary>
    TerminationResistance,

    /// <summary>
    /// An ABSORBED L or C — one termination's own reactance, which is likewise a specification input.
    /// </summary>
    /// <remarks>
    /// <b>Owner-reported, 2026-08-20:</b> <i>"I could not change the C in the schematic that was part
    /// of the specification. Any L or C that is part of the specification should always be editable
    /// using the inline text editor (just as the TermG component is today)."</i>
    ///
    /// <para>It was refused because it went down the ELEMENT path, which aims the transform rack at a
    /// target — and no transform can move an absorbed element, so the answer was truthfully "C1 is set
    /// by the synthesis. Add a transform…". Truthful and useless: it is not set by the synthesis at
    /// all. <c>Termination.Value</c> is where it comes from, the synthesis merely absorbs it, and that
    /// is a number the user owns exactly as much as they own the <c>TermG</c> beside it.</para>
    /// </remarks>
    TerminationReactance,
}

/// <summary>
/// One resolved inline edit: which quantity the typed text will be read as, and what it is about.
/// </summary>
/// <param name="Kind">Element value or termination resistance.</param>
/// <param name="Row">Which label row the editor opens ON — always the value row, whichever was hit.</param>
/// <param name="Name">The element's ladder name, or "" for a termination.</param>
/// <param name="End">1 or 2 for a termination, 0 for an element.</param>
/// <param name="Quantity">Which unit ladder the typed text is parsed against.</param>
/// <param name="Unit">The unit the label is currently displayed in — the fallback when none is typed.</param>
/// <param name="Current">The value as it stands, SI base units.</param>
/// <param name="SeedText">
/// What the editor OPENS WITH — the value and its unit, and nothing else.
/// </param>
/// <remarks>
/// <b><paramref name="SeedText"/> is computed from the value, never taken from the drawing</b>
/// (owner-reported, 2026-08-20: <i>"when I use the inline text editor on a TermG schematic component
/// that is not matched and has a 'target …' suffix, the inline editor includes the '(target…' suffix.
/// This makes it hard to change the TermG value because anything with that suffix gets
/// rejected."</i>).
///
/// <para>The window used to seed the box with the LABEL — which is what the canvas drew, annotations
/// and all. A termination's label carries "(target 50 Ω)" whenever the ladder does not present the
/// declared resistance, so the editor opened holding a string its own parser refuses, and the user's
/// way out of an unmatched design was the one field the unmatched design had broken. An annotation is
/// not part of the value, so it is not part of what the editor edits.</para>
/// </remarks>
public sealed record MatchInlineEditTarget(
    MatchInlineEditKind Kind, int Row, string Name, int End,
    MatchQuantity Quantity, string Unit, double Current, string SeedText = "");

/// <summary>
/// The network pane's inline value editor (owner, 2026-08-20).
/// </summary>
/// <remarks>
/// <b>An element value is a TARGET, not a setting.</b> Every value in a synthesised ladder is fixed by
/// the specification; the only thing that can move one without moving the response is where a Norton
/// transform sits. So a typed element value runs <see cref="MatchElementSolve"/> — the search for the
/// transform setting that comes closest to it with <c>Π N²</c> held on target — and what is written
/// back is the transform rack, not the element. The N sliders move because they are what actually
/// changed; if no combination reaches the value, the closest one is applied and
/// <see cref="InlineEditNote"/> says so with both numbers.
///
/// <para><b>A termination's R is the opposite case</b> and is written straight through: it is a
/// specification input, the same one the Specification pane's own R field edits, so a <c>TermG</c>
/// double-click is a second door onto one property rather than a second way of setting it. It must be
/// REAL — a termination's reactance is its own field, with its own kind selector, and a complex R
/// would have to guess which.</para>
///
/// <para>Both paths end in <see cref="MatchDesignerViewModel.Commit"/>, so both are one undoable step
/// on the owning schematic's stack exactly like every other Designer edit.</para>
/// </remarks>
public sealed partial class MatchDesignerViewModel
{
    /// <summary>What the last inline edit did, or empty. Shown under the network pane.</summary>
    [ObservableProperty] private string _inlineEditNote = "";

    /// <summary>
    /// Resolves a double-clicked COMPONENT to the one thing about it a user may set, or null when
    /// there is nothing.
    /// </summary>
    /// <remarks>
    /// <b>Which label row was hit is deliberately not an input.</b> Only the value row is editable —
    /// the type is what the synthesis produced, and the ladder name is the key every stored transform
    /// resolves through (<c>MatchRebuild.ApplySequence</c> keys on it by name, on purpose) — so the
    /// other two rows had no meaning of their own to compete with, and at this pane's zoom they were
    /// two thirds of a 16-pixel label block sitting on top of the third that does something. A
    /// double-click anywhere on a component therefore lands on its value; the editor opens on
    /// <see cref="MatchSchematicModel.ValueRow"/>, which is where the user can see what they are
    /// editing.
    ///
    /// <para>A component with nothing to set still resolves to nothing — a shunt arm's own
    /// <c>Ground</c> matches neither a termination nor an element, so it returns null and the
    /// double-click falls through to zoom-to-fit.</para>
    /// </remarks>
    public MatchInlineEditTarget? ResolveInlineEdit(string? componentId)
    {
        if (string.IsNullOrEmpty(componentId)) return null;

        foreach (var t in Ladder.Terminations)
        {
            if (!string.Equals(t.InstanceName, componentId, StringComparison.Ordinal)) continue;
            var term = t.End == 1 ? _design.Term1 : _design.Term2;
            return EditTarget(MatchInlineEditKind.TerminationResistance, "", t.End,
                          MatchQuantity.Resistance, term.R);
        }

        var element = Ladder.Elements.FirstOrDefault(
            e => string.Equals(e.Name, componentId, StringComparison.Ordinal));
        if (element is null) return null;

        var quantity = MatchValueFormat.QuantityOf(element.Type);

        // An absorbed element IS a termination's reactance — see MatchInlineEditKind.TerminationReactance.
        if (element.AbsorbedEnd is 1 or 2)
            return EditTarget(MatchInlineEditKind.TerminationReactance, element.Name, element.AbsorbedEnd,
                          quantity, element.Value);

        return EditTarget(MatchInlineEditKind.ElementValue, element.Name, 0, quantity, element.Value);
    }

    /// <summary>
    /// One target, with its seed text formatted the same way the canvas formats a VALUE — so the
    /// editor opens holding a string its own parser accepts, whatever the label around it says.
    /// </summary>
    private MatchInlineEditTarget EditTarget(
        MatchInlineEditKind kind, string name, int end, MatchQuantity quantity, double current)
    {
        string unit = Settings.UnitFor(quantity);
        return new MatchInlineEditTarget(
            kind, MatchSchematicModel.ValueRow, name, end, quantity, unit, current,
            MatchValueFormat.FormatWithUnit(current, quantity, unit, Settings.SignificantDigits));
    }

    /// <summary>
    /// Applies one inline edit. Returns true when something was written; false leaves the design alone
    /// and puts the reason in <see cref="InlineEditNote"/>.
    /// </summary>
    public bool CommitInlineEdit(MatchInlineEditTarget target, string? text)
    {
        ArgumentNullException.ThrowIfNull(target);
        InlineEditNote = "";

        if (string.IsNullOrWhiteSpace(text)) return false;

        if (LooksComplex(text))
        {
            InlineEditNote = target.Kind == MatchInlineEditKind.TerminationResistance
                ? "A termination's R must be real. Its reactance is the X field beside it, which is "
                  + "where a j term belongs."
                : "An element value must be real.";
            return false;
        }

        if (!MatchValueFormat.TryParseWithUnit(
                text, target.Quantity, target.Unit, out double value, out _))
        {
            InlineEditNote = $"\"{text.Trim()}\" is not a value this field can read.";
            return false;
        }

        if (!(value > 0))
        {
            InlineEditNote = "The value must be greater than zero.";
            return false;
        }

        // ── Nothing typed, nothing to do ─────────────────────────────────────
        //
        // Owner-reported, 2026-08-20: "if I double click on a component value in the LC ladder and
        // then close the inline text editor without changing anything, I get an error 'L1_N1 cannot
        // reach x pH; no transform in this rack moves it…'. I shouldn't get an error or warning if I
        // didn't change anything."
        //
        // The editor opens seeded with the label's text, and that text is ROUNDED to the display's
        // significant digits — "725 pH" for an element that is actually 724.83… pH. Committing it
        // unchanged therefore asked the rack to REACH 725 pH exactly, which it truthfully cannot, so
        // the refusal was correct about a question the user never asked.
        //
        // The comparison is at DISPLAY precision rather than at full precision, and that is the whole
        // point: two values that render to the same string are the same value as far as this field is
        // concerned, so re-stating one is not an edit. It is checked here rather than in
        // SetElementValue so it covers the termination-R door onto the same canvas too.
        if (RendersAsShown(value, target)) return false;

        return target.Kind switch
        {
            MatchInlineEditKind.TerminationResistance => SetTerminationResistance(target.End, value),
            MatchInlineEditKind.TerminationReactance  => SetTerminationReactance(target.End, value),
            _                                         => SetElementValue(target.Name, value),
        };
    }

    /// <summary>
    /// True when <paramref name="value"/> formats to the same text the label was already showing.
    /// </summary>
    /// <remarks>
    /// The formatting is <c>MatchDesignerViewModel.ValueTextFor</c>'s, argument for argument, because
    /// the string this compares against is the one the canvas drew and the editor seeded itself from.
    /// A comparison against the stored double instead would answer "different" for every unedited
    /// commit, which is the bug.
    /// </remarks>
    private bool RendersAsShown(double value, MatchInlineEditTarget target)
    {
        int digits = Settings.SignificantDigits;
        return string.Equals(
            MatchValueFormat.FormatWithUnit(target.Current, target.Quantity, target.Unit, digits),
            MatchValueFormat.FormatWithUnit(value, target.Quantity, target.Unit, digits),
            StringComparison.Ordinal);
    }

    /// <summary>True for anything carrying an imaginary part, however it is spelled.</summary>
    /// <remarks>
    /// Both spellings, and both positions: "50+j10" is how an RF engineer writes it and "50+10i" is
    /// how a mathematician does. A bare trailing "i"/"j" with digits in front ("10j") counts too. The
    /// unit tokens this field accepts contain neither letter, so there is nothing to collide with.
    /// </remarks>
    private static bool LooksComplex(string text)
    {
        string t = text.Trim();
        for (int i = 0; i < t.Length; i++)
        {
            char c = char.ToLowerInvariant(t[i]);
            if (c is not ('i' or 'j')) continue;
            bool digitBefore = i > 0 && char.IsDigit(t[i - 1]);
            bool digitAfter = i + 1 < t.Length && char.IsDigit(t[i + 1]);
            bool signBefore = i > 0 && (t[i - 1] == '+' || t[i - 1] == '-');
            if (digitBefore || digitAfter || signBefore) return true;
        }
        return false;
    }

    private bool SetTerminationResistance(int end, double r)
    {
        var term = end == 1 ? _design.Term1 : _design.Term2;
        if (Math.Abs(r - term.R) <= 1e-12 * Math.Max(1.0, term.R)) return false;
        SetTermination(end, term with { R = r });
        InlineEditNote = "";
        return true;
    }

    /// <summary>
    /// Writes one termination's reactance — the absorbed element's real home. The KIND is left alone:
    /// the drawing already says which one this is, and a value typed onto a capacitor is a
    /// capacitance.
    /// </summary>
    private bool SetTerminationReactance(int end, double v)
    {
        var term = end == 1 ? _design.Term1 : _design.Term2;
        // PURELY RELATIVE, with no absolute floor. SetTerminationResistance's "1e-12 * max(1, R)"
        // reads as a rounding tolerance because a resistance is tens of ohms; the same expression on
        // a capacitance is an absolute floor of ONE PICOFARAD, which silently swallowed 1 pF → 2 pF.
        // A reactance has no natural scale, so the only defensible tolerance is a relative one.
        if (Math.Abs(v - term.Value) <= 1e-12 * Math.Abs(term.Value)) return false;
        SetTermination(end, term with { Value = v });
        InlineEditNote = "";
        return true;
    }

    /// <summary>
    /// Aims the transform rack at one element value.
    /// </summary>
    /// <remarks>
    /// <b>The whole N vector is written at once, not one slider nudged.</b> The search's answer IS a
    /// vector — it drove one transform and let <see cref="MatchLinkage"/> redistribute the rest to
    /// hold <c>Π N²</c> — so applying it through <c>SetTransformN</c> would re-run the linkage against
    /// the design's own link setting and land somewhere else whenever that setting is off. Locked rows
    /// are skipped here as well as inside the search: a lock has to hold from both directions.
    /// </remarks>
    private bool SetElementValue(string name, double target)
    {
        if (_rebuild?.Basis is not { Ok: true } basis)
        {
            InlineEditNote = "This design is refused, so there is no ladder to aim at.";
            return false;
        }

        var ranges = new List<TransformRange?>(_design.Transforms.Count);
        for (int i = 0; i < _design.Transforms.Count; i++)
            ranges.Add(i < Transforms.Count ? Transforms[i].Range : null);

        // "Nothing may move" and "nothing CAN reach it" are different answers and must not share a
        // message. The search reports the second by returning the rack unchanged, which on its own
        // would read as "this value is unreachable" even when the real reason is that every slider is
        // locked — so eligibility is counted here, before the search runs.
        int eligible = 0;
        for (int i = 0; i < _design.Transforms.Count; i++)
            if (!_design.Transforms[i].Locked && ranges[i] is { IsUsable: true }) eligible++;

        if (eligible == 0)
        {
            InlineEditNote = _design.Transforms.Count == 0
                ? $"{name} is set by the synthesis. Add a transform (Transforms ▸ + add) to give the "
                  + "ladder a degree of freedom this value can move along."
                : "Every transform in this rack is locked or dropped, so no element value can move. "
                  + "Unlock one first.";
            return false;
        }

        var solution = MatchElementSolve.Solve(_design, basis, ranges, name, target);
        if (solution is null)
        {
            InlineEditNote = $"{name} is set by the synthesis. Add a transform (Transforms ▸ + add) "
                             + "to give the ladder a degree of freedom this value can move along.";
            return false;
        }

        // DrivenIndex < 0 means the search could not beat the rack as it stands. Nothing is written,
        // so a value nobody can reach does not leave every slider rearranged for no gain.
        if (solution.DrivenIndex >= 0)
        {
            for (int i = 0; i < _design.Transforms.Count; i++)
            {
                if (_design.Transforms[i].Locked) continue;
                _design.Transforms[i] = _design.Transforms[i] with { N = solution.N[i] };
            }

            Refresh(specChanged: false);
            Commit();
        }

        var quantity = MatchValueFormat.QuantityOf(
            Ladder.Elements.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.Ordinal))
                ?.Type ?? ElementType.C);
        string unit = Settings.UnitFor(quantity);
        int digits = Settings.SignificantDigits;

        if (solution.Exact) { InlineEditNote = ""; return true; }

        string asked = MatchValueFormat.FormatWithUnit(target, quantity, unit, digits);
        string got = MatchValueFormat.FormatWithUnit(solution.Achieved, quantity, unit, digits);
        InlineEditNote = solution.DrivenIndex >= 0
            ? $"{name} cannot reach {asked} with the transforms this ladder has; "
              + $"N{solution.DrivenIndex + 1} was moved to the closest it can get, {got}."
            : $"{name} cannot reach {asked}: no transform in this rack moves it any closer than the "
              + $"{got} it already is. Nothing was changed.";
        return solution.DrivenIndex >= 0;
    }

    /// <summary>The band and points boxes commit on Return as well as on focus loss (owner, 2026-08-20).
    /// Both re-run the response, which is what "the plots did not update" was about.</summary>
    public void CommitPlotWindow(double? bandPercent, int? points)
    {
        if (bandPercent is { } p && double.IsFinite(p)) PlotBandFraction = p / 100.0;
        if (points is { } n) PlotPoints = n;
        UpdatePlots();
    }

    /// <summary>Parses the "± band" box's own spelling ("10%", "10", "0.1 %").</summary>
    public static double? ParseBandPercent(string? text)
    {
        string t = (text ?? "").Trim().TrimEnd('%').Trim();
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v >= 0
            ? v
            : null;
    }

    /// <summary>Parses the "points" box.</summary>
    public static int? ParsePlotPoints(string? text) =>
        int.TryParse((text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
            ? v
            : null;
}

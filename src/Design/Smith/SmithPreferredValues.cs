using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using CircuitRF.Design.Matching;

namespace CircuitRF.Design.Smith;

/// <summary>
/// The discrete component values a Smith Chart design may be restricted to, and the snap onto them
/// (<c>docs/design/smith-chart.md</c> §5.6a).
/// </summary>
/// <remarks>
/// <b>A real matching network is built out of parts somebody can buy.</b> The sliders and the
/// grippers are continuous, so a two-element match lands on 2.37 nH and 1.64 pF — values that cannot
/// be ordered, and whose chart is therefore a picture of a circuit nobody will build. With
/// <see cref="SmithDesign.SnapToPreferredValues"/> on, every value an edit produces lands on one of
/// these ladders instead, so what the chart draws is what the bench will do.
///
/// <para><b>The shipped ladders are E12, the IEC 60063 preferred numbers</b> (owner decision,
/// 2026-09-21) — twelve values per decade, <c>1.0 1.2 1.5 1.8 2.2 2.7 3.3 3.9 4.7 5.6 6.8 8.2</c>,
/// which is the series a narrow RF part range is stocked on: close enough that snapping to it costs
/// little, coarse enough that what you land on is a value somebody carries. It is a published
/// international standard, so the whole table can be stated here without naming anything
/// proprietary — <c>CLAUDE.md</c>'s standing rule, which covers a shipped data table exactly as it
/// covers prose. <b>Anyone stocked on something else pastes their own list in</b>, which is what
/// makes the shipped choice cheap rather than a compromise — see the editor in
/// <c>src/Ui/Smith</c>.</para>
///
/// <para><b>Inductance and capacitance only</b> (owner decision, 2026-09-21). A resistance in this
/// vocabulary is as often a parasitic as a part: the <c>R</c> of an <c>SRLC</c>, an <c>SRL</c> or a
/// <c>PRC</c> is an ESR or a leakage term, something measured rather than ordered, and snapping it
/// to a preferred value states something untrue about the design. A line's Z₀, an electrical length
/// and a <c>Z1P</c>'s two parts are continuous quantities by construction and are not on any
/// ladder at all.</para>
/// </remarks>
public static class SmithPreferredValues
{
    /// <summary>The E12 mantissas — IEC 60063's twelve-per-decade series.</summary>
    public static readonly IReadOnlyList<double> E12 =
        [1.0, 1.2, 1.5, 1.8, 2.2, 2.7, 3.3, 3.9, 4.7, 5.6, 6.8, 8.2];

    /// <summary>
    /// Capacitance, <b>0.1 pF … 100 nF</b>.
    /// </summary>
    /// <remarks>
    /// The bottom is the owner's (2026-09-21): 0.1 pF is where an RF chip capacitor range starts, and
    /// below it a matching element stops being a part and becomes a layout feature. The top is a DC
    /// block — 100 nF is a decade past the largest value a narrowband match ever calls for, and the
    /// series capacitor's own slider range already runs to 1000 pF for that reason.
    /// </remarks>
    public static IReadOnlyList<double> ShippedCapacitorsFarad { get; } = Decades(-13, -8);

    /// <summary>
    /// Inductance, <b>0.1 nH … 100 µH</b>.
    /// </summary>
    /// <remarks>
    /// 0.1 nH is bond-wire scale and the smallest value a chip inductor range carries; 100 µH is an
    /// RF choke. 10 nH — the top of an inductor row's own slider, about 125 Ω at 2 GHz — sits in the
    /// middle of it, which is where the values a narrowband match actually uses live.
    /// </remarks>
    public static IReadOnlyList<double> ShippedInductorsHenry { get; } = Decades(-10, -5);

    /// <summary>
    /// The ladder for one parameter, or null for a parameter that is not on one.
    /// </summary>
    /// <remarks>
    /// <b>This is the one place the L-and-C rule is written down.</b> Every caller asks here rather
    /// than testing the parameter itself, so a vocabulary that grows a member cannot grow a snap
    /// nobody decided on.
    /// </remarks>
    public static IReadOnlyList<double>? LadderFor(
        SmithParameter p,
        IReadOnlyList<double> capacitorsFarad,
        IReadOnlyList<double> inductorsHenry) => p switch
    {
        SmithParameter.C => capacitorsFarad,
        SmithParameter.L => inductorsHenry,
        _                => null,
    };

    /// <summary>True when <paramref name="p"/> is a parameter this feature restricts at all.</summary>
    public static bool AppliesTo(SmithParameter p) => p is SmithParameter.L or SmithParameter.C;

    /// <summary>The quantity a ladder's numbers are — what formats and parses them.</summary>
    public static MatchQuantity QuantityOf(SmithParameter p)
        => p == SmithParameter.C ? MatchQuantity.Capacitance : MatchQuantity.Inductance;

    // ── the snap ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The entry of <paramref name="ladder"/> nearest <paramref name="value"/>, <b>measured as a
    /// RATIO and not as a difference</b>.
    /// </summary>
    /// <remarks>
    /// <b>Nearest in log space is the only nearest that means anything here.</b> A ladder is
    /// geometric — the gap from 1.0 to 1.2 pF is 0.2 pF and the gap from 68 to 82 pF is 14 pF — so a
    /// linear nearest would be dominated by whichever decade the value happens to be in and would
    /// pull 1.09 pF up to 1.2 rather than down to 1.0. The midpoint between two rungs is their
    /// geometric mean, which is what a component tolerance is symmetric about.
    ///
    /// <para><b>A value that is not strictly positive is returned untouched.</b> Zero henries is a
    /// wire and zero farads is an open circuit — both are meaningful entries in this vocabulary and
    /// neither is a small part, so neither is snapped to the bottom rung. An empty ladder likewise
    /// returns the value unchanged rather than having nothing to return.</para>
    ///
    /// <para><b>A value past either end lands ON that end.</b> Nearest is nearest, and the
    /// alternative — leaving out-of-range values alone — would mean a design whose toggle says every
    /// value is on the ladder while one of them is not. Turning the toggle on is a single undoable
    /// edit and reports how many values it moved, which is what makes that recoverable.</para>
    /// </remarks>
    public static double Snap(double value, IReadOnlyList<double>? ladder)
    {
        if (ladder is null || ladder.Count == 0) return value;
        if (!double.IsFinite(value) || value <= 0.0) return value;

        double best = ladder[0];
        double bestRatio = double.PositiveInfinity;

        foreach (double rung in ladder)
        {
            if (!(rung > 0) || !double.IsFinite(rung)) continue;
            double ratio = Math.Abs(Math.Log(value / rung));
            if (ratio < bestRatio) { bestRatio = ratio; best = rung; }
        }

        return double.IsFinite(bestRatio) ? best : value;
    }

    /// <summary>
    /// Snaps every snappable value in <paramref name="design"/> and returns how many MOVED — what
    /// turning the toggle on does.
    /// </summary>
    /// <remarks>
    /// <b>It walks the parameter table rather than the kinds</b>
    /// (<see cref="SmithComponentMap.Parameters"/>), so the eight RLC-family members are covered by
    /// the same three lines as the bare <c>L</c> and <c>C</c> and a kind added later needs nothing
    /// here. A disabled element is snapped too: it is still part of the design, it is drawn the
    /// moment it is re-enabled, and a value that changed the next time it was switched on would be
    /// the worst of both.
    /// </remarks>
    public static int SnapDesign(
        SmithDesign design,
        IReadOnlyList<double> capacitorsFarad,
        IReadOnlyList<double> inductorsHenry)
    {
        int moved = 0;

        foreach (var element in design.Elements)
        {
            foreach (var p in SmithComponentMap.Parameters(element.Kind))
            {
                if (LadderFor(p, capacitorsFarad, inductorsHenry) is not { } ladder) continue;

                double before = p == SmithParameter.C ? element.Values.CFarad : element.Values.LHenry;
                double after  = Snap(before, ladder);
                if (after == before) continue;

                if (p == SmithParameter.C) element.Values.CFarad = after;
                else                       element.Values.LHenry = after;
                moved++;
            }
        }

        return moved;
    }

    // ── the list as text (the editor's two modes read and write this) ────────

    /// <summary>
    /// The unit a BARE number in one of these lists is read as, and the smallest unit a formatted
    /// list is allowed to reach for.
    /// </summary>
    /// <remarks>
    /// <b>The floor is what makes the list read like a parts list.</b> The Auto ladder picks the
    /// largest prefix that leaves the value at or above 1, so the bottom of the capacitor ladder
    /// comes out as <c>100 fF … 820 fF</c> — arithmetically right, and not how anybody writes an RF
    /// capacitor. The owner's own floor is 0.1 pF, spelled that way. Above the floor Auto takes
    /// over, so the top is <c>100 nF</c> rather than <c>100000 pF</c>.
    /// </remarks>
    public static string BareUnit(MatchQuantity quantity)
        => quantity == MatchQuantity.Capacitance ? "pF" : "nH";

    /// <summary>
    /// A ladder as the editor shows it — <b>one value per line, each with its unit</b>.
    /// </summary>
    public static string Format(IEnumerable<double> values, MatchQuantity quantity)
    {
        var sb = new StringBuilder();
        foreach (double v in values)
            sb.AppendLine(FormatOne(v, quantity));
        return sb.ToString();
    }

    /// <summary>One value, with its unit — the spelling a row shows and the text mode writes. See
    /// <see cref="BareUnit"/> for why it does not simply hand the value to Auto.</summary>
    public static string FormatOne(double value, MatchQuantity quantity)
    {
        string floor = BareUnit(quantity);
        string unit  = Math.Abs(value) < MatchValueFormat.Scale(floor)
            ? floor
            : MatchValueFormat.AutoUnit;
        return MatchValueFormat.FormatWithUnit(value, quantity, unit, 5);
    }

    /// <summary>
    /// Reads a pasted or typed ladder.
    /// </summary>
    /// <remarks>
    /// <b>The comma is a SEPARATOR here and never a decimal point</b>, which is the one place this
    /// field departs from circuitRF's own decimal-comma rule — and the departure is that rule's own
    /// (<c>NumericText.NormalizeDecimalSeparator</c>: "the same holds for any field whose own grammar
    /// separates values with commas … which is why that text must never be passed through here").
    /// <c>1,2,3</c> in a list is either three values or two, and nothing in the text says which; a
    /// `.cnl`'s <c>Values=</c> list settled the same question the same way. So a value pasted out of
    /// a spreadsheet column, a comma-separated row, or one per line all read, and a decimal comma
    /// does not.
    ///
    /// <para><b>Tolerant about what it splits on, strict about what a field contains.</b> Newlines,
    /// commas, semicolons, tabs and blank entries all separate; a <c>#</c> comment runs to the end of
    /// its line. Each field is then the ordinary <c>"value unit"</c> an <c>InlineEditText</c> takes,
    /// with a bare number read as <paramref name="fallbackUnit"/> — and a unit from the WRONG ladder
    /// is a refusal rather than a token to discard, so <c>2.2 nH</c> in the capacitor list says so.
    /// </para>
    ///
    /// <para><b>The result is sorted and de-duplicated, and an EMPTY list is refused.</b> A ladder
    /// with nothing on it would leave the toggle switched on and doing nothing, with no symptom at
    /// all — which is worse than the refusal, and the refusal names the button that undoes it.</para>
    /// </remarks>
    public static bool TryParse(
        string? text, MatchQuantity quantity, string fallbackUnit,
        out IReadOnlyList<double> values, out string? error)
    {
        values = [];
        error  = null;

        var parsed = new List<double>();

        foreach (string rawLine in (text ?? "").Split('\n'))
        {
            string line = rawLine;
            int hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash];

            foreach (string field in line.Split([',', ';', '\t', '\r'], StringSplitOptions.TrimEntries))
            {
                if (field.Length == 0) continue;

                if (!MatchValueFormat.TryParseWithUnit(field, quantity, fallbackUnit, out double v, out _))
                {
                    error = $"\"{field}\" is not {Noun(quantity)}. Write a number with its unit " +
                            $"(\"{(quantity == MatchQuantity.Capacitance ? "2.2 pF" : "2.2 nH")}\"), " +
                            $"or a bare number, which is read as {fallbackUnit}.";
                    return false;
                }

                if (!(v > 0) || !double.IsFinite(v))
                {
                    error = $"\"{field}\" is not a value a part can have. Every entry has to be " +
                            "greater than zero.";
                    return false;
                }

                parsed.Add(v);
            }
        }

        if (parsed.Count == 0)
        {
            error = "A list with nothing in it would leave the toggle switched on and doing " +
                    "nothing, silently. Add a value, or press Revert to shipped values.";
            return false;
        }

        parsed.Sort();

        // De-duplicated by RATIO, for Snap's own reason: two entries a part in a million apart are
        // one rung typed twice, and formatting them would print the same string on two rows.
        var unique = new List<double>(parsed.Count);
        foreach (double v in parsed)
            if (unique.Count == 0 || v / unique[^1] > 1.0 + 1e-9)
                unique.Add(v);

        values = unique;
        return true;
    }

    private static string Noun(MatchQuantity q)
        => q == MatchQuantity.Capacitance ? "a capacitance" : "an inductance";

    // ── building a shipped ladder ────────────────────────────────────────────

    /// <summary>
    /// E12 across the decades <paramref name="first"/> … <paramref name="last"/>, <b>plus the next
    /// decade's first rung</b> so the range closes on a round number rather than on 8.2 of it.
    /// </summary>
    private static IReadOnlyList<double> Decades(int first, int last)
    {
        var list = new List<double>((last - first + 1) * E12.Count + 1);

        for (int decade = first; decade <= last; decade++)
        {
            double mag = Math.Pow(10.0, decade);
            foreach (double m in E12) list.Add(m * mag);
        }

        list.Add(Math.Pow(10.0, last + 1));
        return list;
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CircuitRF.Core.Matching;
using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.Matching;

/// <summary>
/// What the preview has to SAY about one element, as distinct from what it is. Kept as an enum rather
/// than a colour so a test can assert the meaning (brief §8, "assert the colour role, not pixels").
/// </summary>
public enum MatchElementRole
{
    /// <summary>An ordinary element of the network. Drawn in the schematic's own symbol colour.</summary>
    Normal,

    /// <summary>Supplied by an external termination — dimmed, and not stamped by the component.</summary>
    Absorbed,

    /// <summary>match.md §4.5's excess element (CFano/LFano) at the far end. Ours, and stamped.</summary>
    Excess,

    /// <summary>match.md §4.6's detune element (CDetune/LDetune) at the analysis end. Ours.</summary>
    Detune,

    /// <summary>A negative or out-of-range value: exact, response-preserving and unbuildable.</summary>
    OutOfRange,
}

/// <summary>One element as the preview places it. World units are the schematic's own (100 = a grid square).</summary>
/// <param name="Name">The element's ladder name, which is also what a <c>TransformRecord</c> keys on.</param>
/// <param name="Type">L or C — selects the built-in symbol.</param>
/// <param name="IsShunt">True when it hangs from the spine to ground.</param>
/// <param name="Value">Henries or farads.</param>
/// <param name="Role">What the preview says about it.</param>
/// <param name="X">Centre, world units.</param>
/// <param name="Y">Centre, world units.</param>
/// <param name="ValueText">The formatted value with its unit.</param>
public sealed record MatchLadderElement(
    string Name, ElementType Type, bool IsShunt, double Value,
    MatchElementRole Role, double X, double Y, string ValueText)
{
    /// <summary>
    /// 1 or 2 when this element IS one termination's own reactance, 0 otherwise.
    /// </summary>
    /// <remarks>
    /// <b>An absorbed element is a SPECIFICATION INPUT wearing a ladder element's clothes</b>, and the
    /// inline editor has to be able to tell (owner-reported, 2026-08-20: <i>"I could not change the C
    /// in the schematic that was part of the specification. Any L or C that is part of the
    /// specification should always be editable using the inline text editor, just as the TermG
    /// component is today"</i>). Its value is <c>Termination.Value</c> — the synthesis does not choose
    /// it, it absorbs it — so an edit belongs at the termination, not at the transform rack that
    /// cannot move it. Carried on the layout record rather than re-derived because
    /// <c>MatchElement.AbsorbedEnd</c> is the authority and the layout is the only thing the Designer's
    /// canvas and its hit-test can see. An <c>init</c> property rather than a positional parameter so
    /// the record's existing call sites — and its tests — are unchanged.
    /// </remarks>
    public int AbsorbedEnd { get; init; }

    /// <summary>
    /// The theme role the element's SYMBOL is drawn in — the single mapping from meaning to colour.
    /// </summary>
    /// <remarks>
    /// <b>Every element is drawn at full brightness, absorbed ones included</b> (owner, 2026-08-20:
    /// "do not render any component as dimmed — all components should render the same brightness
    /// (even the components that represent the absorbed parasitic)"). Round 2's dimming made the one
    /// part of the drawing a user most needs to read — the parasitic they are matching against — the
    /// hardest part to see. What an absorbed element IS is said in words, by the pane's own legend
    /// and by where it sits (always beside its termination), not by washing it out.
    ///
    /// <para><b>An out-of-range value does not recolour the glyph either</b> (owner, 2026-08-19).
    /// Only <see cref="ValueColorRoleKey"/> turns red — the capacitor is a perfectly ordinary
    /// capacitor, its VALUE is the unbuildable part.</para>
    /// </remarks>
    public string ColorRoleKey => ColorRole.SchematicSymbolLine;

    /// <summary>The theme role this element's VALUE text is drawn in.</summary>
    public string ValueColorRoleKey => Role == MatchElementRole.OutOfRange
        ? ColorRole.MatchNegative
        : ColorRole.SchematicParameterNameText;

    /// <summary>The theme role this element's NAME text is drawn in.</summary>
    public string NameColorRoleKey => ColorRole.SchematicInstanceNameText;
}

/// <summary>A transform bracket, spanning the products one Norton transform created.</summary>
/// <param name="Label">N1, N2, ... — the rack's own label for the same transform.</param>
/// <param name="TransformIndex">Index into the design's transform list.</param>
/// <param name="X0">Left edge, world units.</param>
/// <param name="X1">Right edge, world units.</param>
/// <param name="Row">
/// Stack row, 0 = nearest the ladder. Brackets that would overlap are pushed down a row rather than
/// drawn on top of each other.
/// </param>
public sealed record MatchTransformBracket(string Label, int TransformIndex, double X0, double X1, int Row)
{
    /// <summary>
    /// The brace itself — its curls, its horizontal run and its stem — in one role (owner,
    /// 2026-08-20: "the entire curly brace should be rendered in Schematic.ParameterNameText colour
    /// while the rendered text transform name should use Schematic.ComponentNameText").
    /// </summary>
    public string ColorRoleKey => ColorRole.SchematicParameterNameText;

    /// <summary>The N1/N2 label hanging off the brace's stem — a different role from the brace.</summary>
    public string LabelColorRoleKey => ColorRole.SchematicComponentNameText;
}

/// <summary>One end's grounded termination, as the pane places it.</summary>
/// <param name="End">1 or 2 — which end of the ladder.</param>
/// <param name="InstanceName">"Termination 1" / "Termination 2", the name the glyph is labelled with.</param>
/// <param name="ResistanceText">
/// What the glyph is labelled with: the ladder's own port reference at this end, and — when that is
/// not the resistance the specification declares — the declared one beside it.
/// </param>
/// <param name="X">The x its PIN lands on — the same point the spine ends at.</param>
public sealed record MatchLadderTermination(int End, string InstanceName, string ResistanceText, double X);

/// <summary>
/// The ladder preview's geometry (match.md §9.3): where each element sits, and where each transform
/// bracket goes.
/// </summary>
/// <remarks>
/// <b>Every element gets its own column, including the shunt ones.</b> A bandpass shunt arm is an L
/// and a C on the SAME node, so hanging both at the node's x would draw them on top of each other.
/// Giving each a column and letting the spine run through as a wire is electrically the same node and
/// is what the reference implementation's own preview does; the alternative (offsetting within one
/// column) needs a width that depends on the arm's size and still collides at order 6.
/// </remarks>
public sealed class MatchLadderLayout
{
    /// <summary>
    /// Horizontal spacing between element columns, world units.
    /// </summary>
    /// <remarks>
    /// <b>700, not 400</b> (owner, 2026-08-19: "the horizontal spacing between components in the
    /// network diagram is too small to support even 3 significant digits + units for adjacent
    /// parallel components"). A label's width in WORLD units is very nearly constant — the preview's
    /// font size is a fixed multiple of its own scale, so a glyph is ~90 world units wide however
    /// many elements there are — which is exactly why widening the pitch buys real clearance instead
    /// of shrinking everything by the same factor. "153 pH" plus its name needs roughly 300 world
    /// units; a shunt label starts 130 to the right of its column, so 400 left ~30 units of gap and
    /// 700 leaves ~330.
    /// </remarks>
    public const double Pitch = 700.0;

    /// <summary>The through-path's y.</summary>
    public const double SpineY = 0.0;

    /// <summary>
    /// A shunt element's centre y — <b>one lead-length below the spine, so its upper pin lands
    /// exactly ON it</b>.
    /// </summary>
    /// <remarks>
    /// <b>Owner, 2026-08-20:</b> <i>"the shunt component placement needs to move up such that the
    /// shunt components are exactly at the top horizontal wire; there should be no vertical wires
    /// rendered in the schematic."</i> A built-in two-terminal glyph carries its own leads out to
    /// ±<see cref="MatchSchematicGeometry.LeadHalf"/>, so placing the centre one lead-length down puts
    /// the pin on the spine and leaves nothing for a drop wire to span — the same reasoning that
    /// removed the ground wires, applied to the other end of the arm.
    /// </remarks>
    public const double ShuntY = SpineY + MatchSchematicGeometry.LeadHalf;

    /// <summary>
    /// Where a shunt element's own GND sits — exactly on its lower pin, so there is no wire.
    /// </summary>
    /// <remarks>
    /// <b>There is no ground RAIL any more</b> (owner, 2026-08-20: "remove all the 'ground' wires for
    /// the shunt components — ground each such component with its own GND component"). A rail plus a
    /// drop per arm is two wires and a reference pin saying what one GND glyph says, and it forced
    /// the two extra interface pins the same round removed. <c>Ground</c>'s own pin is at its local
    /// origin, so placing one here — a full lead-length below a shunt element's centre — lands it on
    /// the pin itself and no connecting wire exists to draw.
    /// </remarks>
    public const double ShuntGroundY = ShuntY + MatchSchematicGeometry.LeadHalf;

    /// <summary>The first bracket row's y.</summary>
    public const double BracketY = 900.0;

    /// <summary>
    /// Row-to-row spacing for stacked brackets — enough for a brace, its stem and its label.
    /// </summary>
    /// <remarks>
    /// <b>260, not 150.</b> Round 2's bracket was three straight lines and a label 120 below them;
    /// this one is a real brace with a curl at each end (<see cref="BraceCurl"/>), a stem down from
    /// the middle (<see cref="BraceStem"/>) and the label under THAT, so a row now runs
    /// curl + stem + label-drop + cap-height ≈ 263 deep and 150 would have stacked one brace onto
    /// the next one's text.
    /// </remarks>
    public const double BracketRowPitch = 300.0;

    /// <summary>Radius of the brace's four quarter-turns, world units.</summary>
    public const double BraceCurl = 50.0;

    /// <summary>
    /// Length of the stem from the brace's centre tip down to its label.
    /// </summary>
    /// <remarks>
    /// <b>26.25</b> — halved on 2026-08-20 from the 52.5 the previous round had already cut from 70,
    /// to the owner's "reduce the curly brace's vertical line length rendering (above the N1, N2
    /// text)". The stem is the brace's ONLY straight vertical run, so it is the whole of what that
    /// ask names; the four quarter-turns are <see cref="BraceCurl"/> and are not part of it.
    /// <see cref="BracketRowPitch"/> is deliberately left alone — a shorter stem cannot make two
    /// stacked braces collide, and shrinking the row pitch to match would.
    /// </remarks>
    public const double BraceStem = 26.25;

    /// <summary>Baseline of the brace's label, below the foot of the stem.</summary>
    public const double BraceLabelDrop = 80.0;

    /// <summary>The placed elements, in ladder order.</summary>
    public IReadOnlyList<MatchLadderElement> Elements { get; init; } = [];

    /// <summary>The transform brackets, already assigned to non-overlapping rows.</summary>
    public IReadOnlyList<MatchTransformBracket> Brackets { get; init; } = [];

    /// <summary>x of the port-1 connection.</summary>
    public double PortLeftX { get; init; }

    /// <summary>x of the port-2 connection.</summary>
    public double PortRightX { get; init; }

    /// <summary>
    /// The two grounded terminations the network runs between, end 1 first — what the pane draws in
    /// place of the interface pins it used to (owner, 2026-08-20: "remove the pins … instead place a
    /// TermG at each end of the network").
    /// </summary>
    /// <remarks>
    /// The resistance quoted is the LADDER's own port reference (<c>MatchNetwork.R1</c>/<c>R2</c>),
    /// not the declared termination's, for the same reason <c>MatchFlatten</c>'s annotation gives it:
    /// that is what the plotted response is referenced to. In a finished design the two are the same
    /// number — bringing them together is what the transforms are for.
    /// </remarks>
    public IReadOnlyList<MatchLadderTermination> Terminations { get; init; } = [];

    /// <summary>True when any element is absorbed — the one-line legend is shown only then.</summary>
    public bool HasAbsorbed => Elements.Any(e => e.Role == MatchElementRole.Absorbed);

    /// <summary>True when any element came out negative or outside the guard band.</summary>
    public bool HasOutOfRange => Elements.Any(e => e.Role == MatchElementRole.OutOfRange);

    /// <summary>An empty layout — what a refused design shows.</summary>
    public static MatchLadderLayout Empty { get; } = new();

    /// <summary>
    /// Places one network. <paramref name="applied"/> is the rebuild's own applied-transform list, in
    /// order; a transform whose products are not in the network (it was dropped) simply gets no
    /// bracket, which is the honest rendering of "that pair no longer exists".
    /// </summary>
    /// <param name="ohmsText">
    /// Formats one termination glyph's resistance, given which END it is and the resistance the
    /// LADDER actually presents there. Taking the end as well as the number is what lets a caller say
    /// when the two disagree — see <see cref="MatchLadderTermination.ResistanceText"/>. Optional so a
    /// caller that only wants the ladder geometry still compiles.
    /// </param>
    public static MatchLadderLayout Build(
        MatchNetwork? network,
        IReadOnlyList<AppliedTransform>? applied,
        Func<MatchElement, string> valueText,
        Func<int, double, string>? ohmsText = null)
    {
        ArgumentNullException.ThrowIfNull(valueText);
        if (network is null || network.Elements.Count == 0) return Empty;

        var placed = new List<MatchLadderElement>(network.Elements.Count);
        var xByName = new Dictionary<string, double>(StringComparer.Ordinal);

        double x = Pitch;
        foreach (var e in DisplayOrder(network.Elements))
        {
            placed.Add(new MatchLadderElement(
                e.Name, e.Type, e.IsShunt, e.Value, RoleOf(e),
                x, e.IsShunt ? ShuntY : SpineY, valueText(e))
            {
                AbsorbedEnd = e.AbsorbedEnd,
            });
            xByName[e.Name] = x;
            x += Pitch;
        }

        var brackets = BuildBrackets(applied, xByName);
        ohmsText ??= (_, r) => r.ToString("0.###", CultureInfo.InvariantCulture) + " Ω";

        return new MatchLadderLayout
        {
            Elements = placed,
            Brackets = brackets,
            PortLeftX = 0.0,
            PortRightX = x,
            Terminations =
            [
                new MatchLadderTermination(1, "Termination 1", ohmsText(1, network.R1), 0.0),
                new MatchLadderTermination(2, "Termination 2", ohmsText(2, network.R2), x),
            ],
        };
    }

    /// <summary>
    /// The ladder in the order the pane DRAWS it: an absorbed element is walked out to the end it
    /// belongs to, so it always sits next to its own termination.
    /// </summary>
    /// <remarks>
    /// <b>The owner's ask</b> (2026-08-20): "the absorbed parasitic component must always be placed
    /// adjacent to its corresponding R termination (now the TermG component)". It is not where the
    /// synthesis leaves it: <c>MatchSynthesis.Build</c> emits each arm as L-then-C, so an end arm
    /// whose absorbed half is the C has the arm's own L standing between the parasitic and the
    /// termination, and <c>WithEndSplits</c> then inserts the Fano/detune element further out still.
    ///
    /// <para><b>Only elements that provably commute are stepped over.</b> Two ADJACENT elements of a
    /// ladder share an arm exactly when they share an orientation — a run of shunt elements hangs off
    /// one node in parallel, a run of series elements is one chain — and reordering within an arm is
    /// the same circuit, element for element and net for net. So the walk stops the moment the
    /// orientation changes, which is the moment the next element is a different node. A blanket
    /// "move it to the front of the list" would be a different circuit whenever a Norton transform
    /// had left an absorbed element somewhere other than an end arm.</para>
    /// </remarks>
    public static IReadOnlyList<MatchElement> DisplayOrder(IReadOnlyList<MatchElement> elements)
    {
        ArgumentNullException.ThrowIfNull(elements);
        var order = new List<MatchElement>(elements);

        // End 2 first: walking one element to the right cannot disturb an index to its left, but
        // walking one to the left shifts everything after it.
        foreach (int end in (ReadOnlySpan<int>)[2, 1])
        {
            int i = order.FindIndex(e => e.AbsorbedEnd == end);
            if (i < 0) continue;

            var moving = order[i];
            int step = end == 1 ? -1 : +1;
            int j = i;
            while (j + step >= 0 && j + step < order.Count
                   && order[j + step].IsShunt == moving.IsShunt)
            {
                order[j] = order[j + step];
                j += step;
            }
            order[j] = moving;
        }

        return order;
    }

    /// <summary>
    /// What the preview says about one element. <b>Out-of-range wins over absorbed</b>: a negative
    /// value is a fact about buildability and dimming it would hide the one thing the user has to act
    /// on. An absorbed element cannot be negative in practice, so the precedence is stated rather
    /// than exercised.
    /// </summary>
    public static MatchElementRole RoleOf(MatchElement e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (!(e.Value > 0.0) || !double.IsFinite(e.Value)
            || Math.Abs(e.Value) > NortonTransform.GuardMaxValue
            || Math.Abs(e.Value) < NortonTransform.GuardMinValue)
            return MatchElementRole.OutOfRange;
        if (e.IsAbsorbed) return MatchElementRole.Absorbed;
        if (e.IsExcess)   return MatchElementRole.Excess;
        if (e.IsDetune)   return MatchElementRole.Detune;
        return MatchElementRole.Normal;
    }

    /// <summary>
    /// The products of the transform at one-based <c>ordinal</c> are named <c>{ElementA}_N{ordinal}_1..3</c>
    /// (<c>NortonTransform.Apply</c>), so the bracket is found by that prefix rather than by the
    /// pair's original names — which no longer exist once the transform has run.
    /// </summary>
    public static string ProductPrefix(string elementA, int ordinal) => $"{elementA}_N{ordinal}_";

    private static List<MatchTransformBracket> BuildBrackets(
        IReadOnlyList<AppliedTransform>? applied, Dictionary<string, double> xByName)
    {
        var result = new List<MatchTransformBracket>();
        if (applied is null) return result;

        var spans = new List<(int Index, double X0, double X1)>();
        for (int i = 0; i < applied.Count; i++)
        {
            string prefix = ProductPrefix(applied[i].Record.ElementA, i + 1);
            var xs = xByName.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal))
                            .Select(kv => kv.Value).ToList();
            if (xs.Count == 0) continue;
            spans.Add((i, xs.Min() - Pitch * 0.4, xs.Max() + Pitch * 0.4));
        }

        // Stacking, the way the reference does it: test intersection against everything already in a
        // row and push down until one fits. Ordering by left edge makes the assignment stable.
        var rows = new List<List<(double X0, double X1)>>();
        foreach (var span in spans.OrderBy(s => s.X0))
        {
            int row = 0;
            while (true)
            {
                if (row == rows.Count) rows.Add([]);
                if (!rows[row].Any(r => span.X0 < r.X1 && r.X0 < span.X1)) break;
                row++;
            }
            rows[row].Add((span.X0, span.X1));
            result.Add(new MatchTransformBracket(
                $"N{span.Index + 1}", span.Index, span.X0, span.X1, row));
        }

        return [.. result.OrderBy(b => b.TransformIndex)];
    }
}

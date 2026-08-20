using System;
using System.Collections.Generic;
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
    /// The theme role the element's SYMBOL is drawn in — the single mapping from meaning to colour.
    /// </summary>
    /// <remarks>
    /// <b>An out-of-range value no longer recolours the whole glyph</b> (owner, 2026-08-19: "a
    /// component that has an invalid value should have its value rendered in 'bad' colour, not the
    /// whole component"). The symbol stays the schematic's own symbol colour and only
    /// <see cref="ValueColorRoleKey"/> turns red, which is the half that is actually wrong — the
    /// capacitor is a perfectly ordinary capacitor, its VALUE is the unbuildable part.
    /// </remarks>
    public string ColorRoleKey => Role switch
    {
        MatchElementRole.Absorbed => ColorRole.MatchAbsorbed,
        _                         => ColorRole.SchematicSymbolLine,
    };

    /// <summary>The theme role this element's VALUE text is drawn in.</summary>
    public string ValueColorRoleKey => Role switch
    {
        MatchElementRole.OutOfRange => ColorRole.MatchNegative,
        MatchElementRole.Absorbed   => ColorRole.MatchAbsorbed,
        _                           => ColorRole.SchematicParameterNameText,
    };

    /// <summary>The theme role this element's NAME text is drawn in.</summary>
    public string NameColorRoleKey => Role == MatchElementRole.Absorbed
        ? ColorRole.MatchAbsorbed
        : ColorRole.SchematicInstanceNameText;
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
    /// <summary>Brackets are always drawn in one role; kept here so the view never picks a colour.</summary>
    public string ColorRoleKey => ColorRole.MatchBracket;
}

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

    /// <summary>A shunt element's centre y.</summary>
    public const double ShuntY = 400.0;

    /// <summary>The ground rail's y.</summary>
    public const double GroundY = 700.0;

    /// <summary>The first bracket row's y, and the spacing between stacked rows.</summary>
    public const double BracketY = 900.0;

    /// <summary>Row-to-row spacing for stacked brackets.</summary>
    public const double BracketRowPitch = 150.0;

    /// <summary>The placed elements, in ladder order.</summary>
    public IReadOnlyList<MatchLadderElement> Elements { get; init; } = [];

    /// <summary>The transform brackets, already assigned to non-overlapping rows.</summary>
    public IReadOnlyList<MatchTransformBracket> Brackets { get; init; } = [];

    /// <summary>x of the port-1 connection.</summary>
    public double PortLeftX { get; init; }

    /// <summary>x of the port-2 connection.</summary>
    public double PortRightX { get; init; }

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
    public static MatchLadderLayout Build(
        MatchNetwork? network,
        IReadOnlyList<AppliedTransform>? applied,
        Func<MatchElement, string> valueText)
    {
        ArgumentNullException.ThrowIfNull(valueText);
        if (network is null || network.Elements.Count == 0) return Empty;

        var placed = new List<MatchLadderElement>(network.Elements.Count);
        var xByName = new Dictionary<string, double>(StringComparer.Ordinal);

        double x = Pitch;
        foreach (var e in network.Elements)
        {
            placed.Add(new MatchLadderElement(
                e.Name, e.Type, e.IsShunt, e.Value, RoleOf(e),
                x, e.IsShunt ? ShuntY : SpineY, valueText(e)));
            xByName[e.Name] = x;
            x += Pitch;
        }

        var brackets = BuildBrackets(applied, xByName);

        return new MatchLadderLayout
        {
            Elements = placed,
            Brackets = brackets,
            PortLeftX = 0.0,
            PortRightX = x,
        };
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

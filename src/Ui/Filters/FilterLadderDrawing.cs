using System.Globalization;
using CircuitRF.Core.Matching;
using CircuitRF.Core.Systems;
using CircuitRF.Ui.Matching;

namespace CircuitRF.Ui.Filters;

/// <summary>
/// Turns a synthesised ladder into the Match Designer's layout. Series LC stubs share a column
/// (the capacitor sits under the inductor, the same stack Match uses for a DC block). Parallel LC
/// in the through path shares one series column so both pins land on the same two spine nodes —
/// consecutive series columns would put a wire between them and draw a series pair instead.
/// </summary>
internal static class FilterLadderDrawing
{
    /// <summary>The Match layout for <paramref name="elements"/>, terminated in <paramref name="rs"/> / <paramref name="rl"/>.</summary>
    public static MatchLadderLayout Build(
        IReadOnlyList<FilterElement> elements, double rs, double rl,
        Func<bool, double, string> valueText)
    {
        var placed = new List<MatchLadderElement>();
        var consumed = new bool[elements.Count];
        double x = MatchLadderLayout.Pitch;

        for (int i = 0; i < elements.Count; i++)
        {
            if (consumed[i]) continue;
            var e = elements[i];

            // Series LC to ground (lowpass / highpass tank), or a bandpass stub that is a series
            // resonator into a parallel tank to ground — both hang from one spine node.
            if (e.Shunt && e.NodeB != "0" && TryStub(elements, consumed, i, out var stubParts))
            {
                foreach (var p in stubParts) consumed[IndexOf(elements, p)] = true;
                PlaceShuntStack(placed, stubParts, x, valueText);
                x += MatchLadderLayout.Pitch;
                continue;
            }

            // Parallel LC in the through path: same two nodes, neither to ground.
            if (!e.Shunt
                && Find(elements, consumed, i, o => !o.Shunt && o.Inductor != e.Inductor
                                                    && SamePair(e, o)) is { } other)
            {
                consumed[i] = consumed[IndexOf(elements, other)] = true;
                var L = e.Inductor ? e : other;
                var C = e.Inductor ? other : e;
                placed.Add(El(L.Name, ElementType.L, false, L.Value, x, MatchLadderLayout.SpineY,
                              valueText(true, L.Value)));
                placed.Add(El(C.Name, ElementType.C, false, C.Value, x, MatchLadderLayout.SpineY,
                              valueText(false, C.Value)));
                x += MatchLadderLayout.Pitch;
                continue;
            }

            // Shunt parallel resonator (bandpass): L and C both to ground from the same spine node.
            if (e.Shunt && e.NodeB == "0"
                && Find(elements, consumed, i, o => o.Shunt && o.NodeB == "0" && o.Inductor != e.Inductor
                                                    && o.NodeA == e.NodeA) is { } shuntMate)
            {
                consumed[i] = consumed[IndexOf(elements, shuntMate)] = true;
                // Match's own bandpass drawing: two consecutive shunt columns, spine through —
                // electrically the same node.
                PlaceOne(placed, e, x, valueText);
                x += MatchLadderLayout.Pitch;
                PlaceOne(placed, shuntMate, x, valueText);
                x += MatchLadderLayout.Pitch;
                continue;
            }

            consumed[i] = true;
            PlaceOne(placed, e, x, valueText);
            x += MatchLadderLayout.Pitch;
        }

        return new MatchLadderLayout
        {
            Elements = placed,
            Brackets = [],
            PortLeftX = 0.0,
            PortRightX = x,
            Terminations =
            [
                new MatchLadderTermination(1, "Termination 1", Ohms(rs), 0.0),
                new MatchLadderTermination(2, "Termination 2", Ohms(rl), x),
            ],
        };
    }

    /// <summary>
    /// A stub chain from the spine to ground. Two elements (series LC) use the Match DC-block
    /// stack. Four (bandpass tank) stack further down the same column.
    /// </summary>
    private static void PlaceShuntStack(
        List<MatchLadderElement> placed, List<FilterElement> parts, double x,
        Func<bool, double, string> valueText)
    {
        double y = MatchLadderLayout.ShuntY;
        double step = 2.0 * MatchSchematicGeometry.LeadHalf;
        for (int k = 0; k < parts.Count; k++)
        {
            var p = parts[k];
            bool last = k == parts.Count - 1;
            var role = k == 0 ? MatchElementRole.Normal : MatchElementRole.DcBlock;
            // DcBlockEnd ≠ 0: no second spine tap. GroundYFor still grounds the lowest in the column.
            placed.Add(El(p.Name, p.Inductor ? ElementType.L : ElementType.C, true, p.Value,
                          x, y, valueText(p.Inductor, p.Value), role)
                       with { DcBlockEnd = last ? 0 : 1 });
            y += step;
        }
    }

    /// <summary>
    /// Walks a shunt-marked chain that ends at ground: either L→C→0, or L→C→(L∥C→0) for a
    /// bandpass-transformed series-LC stub.
    /// </summary>
    private static bool TryStub(
        IReadOnlyList<FilterElement> elements, bool[] consumed, int start,
        out List<FilterElement> parts)
    {
        parts = [];
        var first = elements[start];
        if (!first.Shunt || first.NodeB == "0") return false;

        // Gather every unused shunt element reachable from first.NodeA toward ground.
        var chain = new List<FilterElement> { first };
        string tip = first.NodeB;
        bool grew = true;
        while (grew)
        {
            grew = false;
            for (int j = 0; j < elements.Count; j++)
            {
                if (consumed[j] || j == start) continue;
                var e = elements[j];
                if (!e.Shunt) continue;
                if (e.NodeA == tip && e.NodeB != "0" && e.NodeB != first.NodeA
                    && chain.All(c => c.NodeB != e.NodeB))
                {
                    chain.Add(e);
                    tip = e.NodeB;
                    grew = true;
                    break;
                }
            }
        }
        // Elements from tip to ground (the parallel tank, or the final capacitor).
        var toGround = new List<FilterElement>();
        for (int j = 0; j < elements.Count; j++)
        {
            if (consumed[j] || j == start) continue;
            var e = elements[j];
            if (e.Shunt && e.NodeA == tip && e.NodeB == "0") toGround.Add(e);
        }
        if (toGround.Count == 0) return false;
        // A lone series LC is one inductor (or cap) plus one to-ground. A bandpass stub is a
        // series pair plus a parallel pair to ground.
        if (chain.Count == 1 && toGround.Count == 1)
        {
            parts = [chain[0], toGround[0]];
            return true;
        }
        if (chain.Count >= 1 && toGround.Count >= 1)
        {
            parts = [.. chain, .. toGround];
            return parts.Count >= 2;
        }
        return false;
    }

    private static void PlaceOne(
        List<MatchLadderElement> placed, FilterElement e, double x,
        Func<bool, double, string> valueText)
    {
        placed.Add(El(e.Name, e.Inductor ? ElementType.L : ElementType.C, e.Shunt, e.Value, x,
                      e.Shunt ? MatchLadderLayout.ShuntY : MatchLadderLayout.SpineY,
                      valueText(e.Inductor, e.Value)));
    }

    private static MatchLadderElement El(
        string name, ElementType type, bool shunt, double value, double x, double y, string text,
        MatchElementRole role = MatchElementRole.Normal) =>
        new(name, type, shunt, value, role, x, y, text);

    private static string Ohms(double r) =>
        r.ToString("0.###", CultureInfo.InvariantCulture) + " Ω";

    private static bool SamePair(FilterElement a, FilterElement b) =>
        (a.NodeA == b.NodeA && a.NodeB == b.NodeB)
        || (a.NodeA == b.NodeB && a.NodeB == b.NodeA);

    private static FilterElement? Find(
        IReadOnlyList<FilterElement> elements, bool[] consumed, int from,
        Func<FilterElement, bool> pred)
    {
        for (int j = from + 1; j < elements.Count; j++)
        {
            if (consumed[j]) continue;
            if (pred(elements[j])) return elements[j];
        }
        return null;
    }

    private static int IndexOf(IReadOnlyList<FilterElement> elements, FilterElement e)
    {
        for (int i = 0; i < elements.Count; i++)
            if (ReferenceEquals(elements[i], e) || elements[i] == e) return i;
        return -1;
    }
}

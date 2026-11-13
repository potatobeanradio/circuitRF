using System.Globalization;
using CircuitRF.Core.Matching;
using CircuitRF.Core.Systems;
using CircuitRF.Ui.Matching;

namespace CircuitRF.Ui.Filters;

/// <summary>
/// Turns a synthesised ladder into the Match Designer's layout. Series LC stubs share a column
/// (the capacitor sits under the inductor, the same stack Match uses for a DC block). Parallel LC
/// in the through path sits above the spine on vertical drops at both pins — consecutive series
/// columns would put a wire between them and draw a series pair instead. A bandpass series-parallel
/// arm (series resonator ∥ parallel resonator) keeps both paths across the same two spine nodes.
/// </summary>
internal static class FilterLadderDrawing
{
    private const double Lead = MatchSchematicGeometry.LeadHalf;
    private const double ParallelStep = 2.0 * Lead;

    /// <summary>The Match layout for <paramref name="elements"/>, terminated in <paramref name="rs"/> / <paramref name="rl"/>.</summary>
    public static MatchLadderLayout Build(
        IReadOnlyList<FilterElement> elements, double rs, double rl,
        Func<bool, double, string> valueText)
    {
        var placed = new List<MatchLadderElement>();
        var arms = new List<MatchParallelArm>();
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

            // Bandpass / bandstop series-parallel arm: series LC chain ∥ parallel LC, same outer nodes.
            if (!e.Shunt && TryBridged(elements, consumed, i, out var chain, out var tank))
            {
                foreach (var p in chain) consumed[IndexOf(elements, p)] = true;
                foreach (var p in tank) consumed[IndexOf(elements, p)] = true;
                PlaceBridged(placed, arms, chain, tank, x, valueText);
                x += MatchLadderLayout.Pitch * chain.Count;
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
                PlaceParallelTank(placed, arms, L, C, x, valueText);
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
            ParallelArms = arms,
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
    /// Series resonator on the spine and parallel resonator above it, both between the same outer
    /// nodes. The parallel path uses <see cref="MatchParallelArm"/> drops so the pane can draw the
    /// verticals Match otherwise refuses for shunt arms.
    /// </summary>
    private static void PlaceBridged(
        List<MatchLadderElement> placed, List<MatchParallelArm> arms,
        List<FilterElement> chain, List<FilterElement> tank, double x0,
        Func<bool, double, string> valueText)
    {
        for (int k = 0; k < chain.Count; k++)
            PlaceOne(placed, chain[k], x0 + k * MatchLadderLayout.Pitch, valueText);

        double left = x0 - Lead;
        double right = x0 + (chain.Count - 1) * MatchLadderLayout.Pitch + Lead;
        double xMid = (left + right) / 2.0;
        double y = MatchLadderLayout.SpineY - ParallelStep;
        foreach (var p in tank.OrderBy(t => t.Inductor ? 0 : 1))
        {
            placed.Add(El(p.Name, p.Inductor ? ElementType.L : ElementType.C, false, p.Value,
                          xMid, y, valueText(p.Inductor, p.Value)));
            arms.Add(new MatchParallelArm(left, right, y));
            y -= ParallelStep;
        }
    }

    /// <summary>L∥C in the through path: L on the spine, C above it on pin-end drops.</summary>
    private static void PlaceParallelTank(
        List<MatchLadderElement> placed, List<MatchParallelArm> arms,
        FilterElement L, FilterElement C, double x, Func<bool, double, string> valueText)
    {
        placed.Add(El(L.Name, ElementType.L, false, L.Value, x, MatchLadderLayout.SpineY,
                      valueText(true, L.Value)));
        double y = MatchLadderLayout.SpineY - ParallelStep;
        placed.Add(El(C.Name, ElementType.C, false, C.Value, x, y, valueText(false, C.Value)));
        arms.Add(new MatchParallelArm(x - Lead, x + Lead, y));
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
        double step = ParallelStep;
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
        var toGround = new List<FilterElement>();
        for (int j = 0; j < elements.Count; j++)
        {
            if (consumed[j] || j == start) continue;
            var e = elements[j];
            if (e.Shunt && e.NodeA == tip && e.NodeB == "0") toGround.Add(e);
        }
        if (toGround.Count == 0) return false;
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

    /// <summary>
    /// Series LC (a→mid→b) in parallel with L∥C (a→b). The mid node is private to the chain.
    /// </summary>
    private static bool TryBridged(
        IReadOnlyList<FilterElement> elements, bool[] consumed, int start,
        out List<FilterElement> chain, out List<FilterElement> tank)
    {
        chain = [];
        tank = [];
        var e = elements[start];
        if (e.Shunt || e.NodeB is "0" or null) return false;

        // Find the unique continuation mid → b (exactly one unused series element leaving NodeB).
        FilterElement? next = null;
        for (int j = 0; j < elements.Count; j++)
        {
            if (consumed[j] || j == start) continue;
            var o = elements[j];
            if (o.Shunt) continue;
            if (o.NodeA == e.NodeB && o.NodeB != e.NodeA && o.NodeB != "0")
            {
                if (next is not null) return false;
                next = o;
            }
            else if (o.NodeB == e.NodeB && o.NodeA != e.NodeA && o.NodeA != "0")
            {
                if (next is not null) return false;
                next = o;
            }
        }
        if (next is null) return false;

        string a = e.NodeA;
        string b = next.NodeA == e.NodeB ? next.NodeB : next.NodeA;
        if (b is "0" || b == a) return false;

        // Tank: unused series L and C both directly between a and b.
        FilterElement? tankL = null, tankC = null;
        for (int j = 0; j < elements.Count; j++)
        {
            if (consumed[j] || j == start) continue;
            var o = elements[j];
            if (o.Shunt || ReferenceEquals(o, next)) continue;
            if (!Touches(o, a, b)) continue;
            if (o.Inductor) { if (tankL is not null) return false; tankL = o; }
            else { if (tankC is not null) return false; tankC = o; }
        }
        if (tankL is null || tankC is null) return false;

        chain = [e, next];
        tank = [tankL, tankC];
        return true;
    }

    private static bool Touches(FilterElement o, string a, string b) =>
        (o.NodeA == a && o.NodeB == b) || (o.NodeA == b && o.NodeB == a);

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

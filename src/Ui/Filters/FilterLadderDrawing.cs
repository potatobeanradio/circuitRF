using System.Globalization;
using CircuitRF.Core.Matching;
using CircuitRF.Core.Systems;
using CircuitRF.Ui.Matching;

namespace CircuitRF.Ui.Filters;

/// <summary>
/// Turns a synthesised ladder into the Match Designer's layout.
///
/// <para>Series LC stubs share a column. A lowpass / highpass parallel tank in the through path is
/// a short rectangle: C on the spine, L on one rail above. A bandpass series-parallel arm
/// (series resonator ∥ parallel resonator) keeps the series L–C on the spine with leads abutting
/// (no gap wire through the box) and the parallel L∥C as one tight rectangle above — not a tall
/// multi-rail bridge with labels piled on the spine.</para>
/// </summary>
internal static class FilterLadderDrawing
{
    private const double Lead = MatchSchematicGeometry.LeadHalf;
    /// <summary>
    /// Vertical pitch between stacked horizontal glyphs. The series body is ±65, so ~0.8·Lead
    /// clears it with a small margin — tight enough that a bridged arm reads as one short tank
    /// cell rather than a tall three-rail box.
    /// </summary>
    private const double ParallelStep = 0.8 * Lead;
    /// <summary>
    /// Centres of two series glyphs that share one arm. Exactly 2·Lead makes the leads abut, so
    /// AddWiring draws no spine segment between them (that segment was the “third wire” through
    /// the bridged box).
    /// </summary>
    private const double PairHalf = Lead;
    /// <summary>Width of a bridged series-parallel arm.</summary>
    private const double BridgedSpan = 2.0 * PairHalf + 2.0 * Lead;

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
                PlaceBridged(placed, arms, chain, tank, x + BridgedSpan * 0.5, valueText);
                x += BridgedSpan + MatchLadderLayout.Pitch * 0.15;
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
    /// Series resonator on the spine, parallel resonator as one short rectangle above it.
    /// Series L–C leads abut (no spine gap wire). Parallel L and C share mid-X on two rails
    /// spaced just over one glyph body — a compact tank cell, not a tall multi-rail bridge.
    /// </summary>
    private static void PlaceBridged(
        List<MatchLadderElement> placed, List<MatchParallelArm> arms,
        List<FilterElement> chain, List<FilterElement> tank, double x0,
        Func<bool, double, string> valueText)
    {
        double xL = x0 - PairHalf;
        double xR = x0 + PairHalf;
        double left = xL - Lead;
        double right = xR + Lead;

        var series = chain.OrderBy(p => p.Inductor ? 0 : 1).ToList();
        if (series.Count >= 2)
        {
            PlaceOne(placed, series[0], xL, valueText);
            PlaceOne(placed, series[1], xR, valueText);
        }
        else
            PlaceOne(placed, series[0], x0, valueText);

        var tankL = tank.FirstOrDefault(t => t.Inductor) ?? tank[0];
        var tankC = tank.FirstOrDefault(t => !t.Inductor) ?? tank[^1];
        // Compact L∥C above the series pair: C nearest the spine, L one step further up.
        double yC = MatchLadderLayout.SpineY - ParallelStep;
        double yL = yC - ParallelStep;
        placed.Add(El(tankC.Name, ElementType.C, false, tankC.Value,
                      x0, yC, valueText(false, tankC.Value)));
        placed.Add(El(tankL.Name, ElementType.L, false, tankL.Value,
                      x0, yL, valueText(true, tankL.Value)));
        arms.Add(new MatchParallelArm(left, right, yC));
        arms.Add(new MatchParallelArm(left, right, yL));
    }

    /// <summary>
    /// L∥C in the through path as a short rectangle: C on the spine, L on one rail above.
    /// </summary>
    private static void PlaceParallelTank(
        List<MatchLadderElement> placed, List<MatchParallelArm> arms,
        FilterElement L, FilterElement C, double x, Func<bool, double, string> valueText)
    {
        placed.Add(El(C.Name, ElementType.C, false, C.Value, x, MatchLadderLayout.SpineY,
                      valueText(false, C.Value)));
        double y = MatchLadderLayout.SpineY - ParallelStep;
        placed.Add(El(L.Name, ElementType.L, false, L.Value, x, y, valueText(true, L.Value)));
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
            placed.Add(El(p.Name, p.Inductor ? ElementType.L : ElementType.C, true, p.Value,
                          x, y, valueText(p.Inductor, p.Value), role)
                       with { DcBlockEnd = last ? 0 : 1 });
            y += step;
        }
    }

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

    private static bool TryBridged(
        IReadOnlyList<FilterElement> elements, bool[] consumed, int start,
        out List<FilterElement> chain, out List<FilterElement> tank)
    {
        chain = [];
        tank = [];
        var e = elements[start];
        if (e.Shunt || e.NodeB is "0" or null) return false;

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

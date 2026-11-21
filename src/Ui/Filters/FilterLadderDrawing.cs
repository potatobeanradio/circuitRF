using System.Globalization;
using CircuitRF.Core.Matching;
using CircuitRF.Core.Systems;
using CircuitRF.Ui.Matching;

namespace CircuitRF.Ui.Filters;

/// <summary>
/// Turns a synthesised ladder into the Match Designer's layout, ARM BY ARM. The ladder says which
/// elements each arm became (<see cref="FilterRealization.Arms"/>); the drawing reads that arm's
/// own topology — which of its elements run straight between its two nodes, which form a chain —
/// and places one of a handful of pictures:
/// <list type="bullet">
/// <item>one through-path element, or a series chain of them, on the spine;</item>
/// <item>a parallel LC in the through path: C on the spine, L on one rail above;</item>
/// <item>a bandpass/bandstop series arm, (series L–C) ∥ L ∥ C: a box with the series pair on a
/// rail above, the L on the spine and the C on a rail below;</item>
/// <item>one shunt element, or a parallel L∥C to ground as two adjacent legs;</item>
/// <item>a stub to ground: a column of sections, each one element or a parallel pair drawn as two
/// legs either side of the column, joined by bars.</item>
/// </list>
/// </summary>
internal static class FilterLadderDrawing
{
    private const double Lead = MatchSchematicGeometry.LeadHalf;
    /// <summary>
    /// Column step: the Match pitch plus room for a shunt symbol's labels, which the filter always
    /// puts beside the symbol, to clear the next column's own labels.
    /// </summary>
    private const double Pitch = MatchLadderLayout.Pitch + 150.0;

    /// <summary>Rail height of a through-path tank's inductor.</summary>
    internal const double TankRailY = MatchLadderLayout.SpineY - 2.0 * Lead;

    /// <summary>Rail heights of the three-branch box: the series pair above, the C below.</summary>
    internal const double BoxTopY = MatchLadderLayout.SpineY - 2.5 * Lead;

    /// <inheritdoc cref="BoxTopY"/>
    internal const double BoxBottomY = MatchLadderLayout.SpineY + 2.0 * Lead;

    /// <summary>Half the spacing of the box's top pair — wide enough for two label blocks side by side.</summary>
    private const double BoxHalf = 2.0 * Lead;

    /// <summary>Half the spacing of a parallel pair in a stub column — one Pitch between the legs.</summary>
    private const double LegHalf = MatchLadderLayout.Pitch / 2.0 + 50.0;

    /// <summary>The Match layout for a realised ladder, terminated in <paramref name="rs"/> / <paramref name="rl"/>.</summary>
    public static MatchLadderLayout Build(
        IReadOnlyList<FilterElement> elements, IReadOnlyList<FilterArm> arms, double rs, double rl,
        Func<bool, double, string> valueText)
    {
        if (arms.Count == 0)
            arms = [.. elements.Select((e, i) => new FilterArm(
                e.Shunt ? FilterArmKind.Shunt : FilterArmKind.Series, [i]))];

        var ctx = new Context(valueText);
        double x = Pitch;
        string node = "p1";
        foreach (var arm in arms)
        {
            var parts = arm.Elements.Select(i => elements[i]).ToList();
            if (parts.Count == 0) continue;
            if (parts.All(p => p.Shunt))
                x = PlaceShunt(ctx, parts, node, x);
            else
            {
                string far = FarNode(parts, node);
                x = PlaceSeries(ctx, parts, node, far, x);
                node = far;
            }
        }

        return new MatchLadderLayout
        {
            Elements = ctx.Placed,
            Brackets = [],
            ParallelArms = ctx.Arms,
            ExtraWires = ctx.Wires,
            ExtraDots = ctx.Dots,
            PortLeftX = 0.0,
            PortRightX = x,
            Terminations =
            [
                new MatchLadderTermination(1, "Termination 1", Ohms(rs), 0.0),
                new MatchLadderTermination(2, "Termination 2", Ohms(rl), x),
            ],
        };
    }

    private sealed class Context(Func<bool, double, string> valueText)
    {
        public List<MatchLadderElement> Placed { get; } = [];
        public List<MatchParallelArm> Arms { get; } = [];
        public List<(double, double, double, double)> Wires { get; } = [];
        public List<(double, double)> Dots { get; } = [];
        public Func<bool, double, string> ValueText { get; } = valueText;

        public void Add(FilterElement e, double x, double y, bool shunt,
                        bool labelAbove = false, bool noGround = false) =>
            Placed.Add(new MatchLadderElement(
                e.Name, e.Inductor ? ElementType.L : ElementType.C, shunt, e.Value,
                MatchElementRole.Normal, x, y, ValueText(e.Inductor, e.Value))
            {
                LabelAbove = labelAbove,
                NoGround = noGround,
                LabelBeside = shunt,
            });
    }

    // ── through-path arms ─────────────────────────────────────────────────────

    /// <summary>
    /// The arm's far through-path node. Besides the entry, a series arm touches its far node and
    /// the private nodes of its chains; a private node joins exactly two elements, while the far
    /// node joins an odd number (one for a chain, three for a box) — or is the only other node.
    /// </summary>
    private static string FarNode(List<FilterElement> parts, string a)
    {
        var degree = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var p in parts)
        {
            degree[p.NodeA] = degree.GetValueOrDefault(p.NodeA) + 1;
            degree[p.NodeB] = degree.GetValueOrDefault(p.NodeB) + 1;
        }
        var others = degree.Keys.Where(k => k != a && k != "0").ToList();
        if (others.Count == 1) return others[0];
        return others.FirstOrDefault(k => degree[k] % 2 == 1) ?? others.LastOrDefault() ?? a;
    }

    private static double PlaceSeries(Context ctx, List<FilterElement> parts, string a, string b, double x)
    {
        var direct = parts.Where(p => Touches(p, a, b)).ToList();
        var chain = parts.Except(direct).ToList();
        if (chain.Count > 0) chain = ChainFrom(chain, a, out _);
        double y = MatchLadderLayout.SpineY;

        if (direct.Count == 2 && chain.Count == 2)
        {
            // (series L–C) ∥ L ∥ C: the series pair on a rail above, L on the spine, C below.
            double x0 = x + BoxHalf + Lead;
            var dl = direct.FirstOrDefault(d => d.Inductor) ?? direct[0];
            var dc = direct.First(d => !ReferenceEquals(d, dl));
            ctx.Add(chain[0], x0 - BoxHalf, BoxTopY, false, labelAbove: true);
            ctx.Add(chain[1], x0 + BoxHalf, BoxTopY, false, labelAbove: true);
            ctx.Add(dl, x0, y, false, labelAbove: true);
            ctx.Add(dc, x0, BoxBottomY, false);
            double left = x0 - BoxHalf - Lead, right = x0 + BoxHalf + Lead;
            ctx.Arms.Add(new MatchParallelArm(left, right, BoxTopY));
            ctx.Arms.Add(new MatchParallelArm(left, right, BoxBottomY));
            return right + Pitch;
        }

        if (direct.Count == 2 && chain.Count == 0)
        {
            // L∥C in the through path: C on the spine, L on a short rail above.
            var dl = direct.FirstOrDefault(d => d.Inductor) ?? direct[0];
            var dc = direct.First(d => !ReferenceEquals(d, dl));
            ctx.Add(dc, x, y, false);
            ctx.Add(dl, x, TankRailY, false, labelAbove: true);
            ctx.Arms.Add(new MatchParallelArm(x - Lead, x + Lead, TankRailY));
            return x + Pitch + Lead / 2.0;
        }

        // One element, or a chain of them: consecutive spine columns.
        foreach (var p in direct.Concat(chain))
        {
            ctx.Add(p, x, y, false);
            x += Pitch;
        }
        return x;
    }

    // ── shunt arms ────────────────────────────────────────────────────────────

    private static double PlaceShunt(Context ctx, List<FilterElement> parts, string a, double x)
    {
        var sections = Sections(parts, a);
        if (sections is null)
        {
            // Not a column this drawing knows: one column per element, which is at least every
            // element drawn once from the node it hangs on.
            foreach (var p in parts)
            {
                ctx.Add(p, x, MatchLadderLayout.ShuntY, true);
                x += Pitch;
            }
            return x;
        }

        // A single section of parallel elements straight to ground: adjacent legs on the spine.
        if (sections.Count == 1)
        {
            foreach (var p in sections[0])
            {
                ctx.Add(p, x, MatchLadderLayout.ShuntY, true);
                x += Pitch;
            }
            return x;
        }

        bool anyPair = sections.Any(sec => sec.Count > 1);
        double cx = anyPair ? x + LegHalf : x;
        double yc = MatchLadderLayout.ShuntY;
        for (int k = 0; k < sections.Count; k++)
        {
            var sec = sections[k];
            bool last = k == sections.Count - 1;
            if (sec.Count == 1)
                ctx.Add(sec[0], cx, yc, true, noGround: !last);
            else
            {
                double top = yc - Lead, bottom = yc + Lead;
                double xl = cx - LegHalf, xr = cx + LegHalf;
                ctx.Add(sec[0], xl, yc, true, noGround: !last);
                ctx.Add(sec[1], xr, yc, true, noGround: !last);
                if (k > 0)
                {
                    ctx.Wires.Add((xl, top, xr, top));
                    ctx.Dots.Add((cx, top));
                }
                if (!last)
                {
                    ctx.Wires.Add((xl, bottom, xr, bottom));
                    ctx.Dots.Add((cx, bottom));
                }
            }
            yc += 2.0 * Lead;
        }
        return anyPair ? x + 2.0 * Pitch : x + Pitch;
    }

    /// <summary>
    /// The stub from <paramref name="a"/> to ground as series sections, each one element or a
    /// parallel group sharing the same two nodes. Null when the arm is not such a column.
    /// </summary>
    private static List<List<FilterElement>>? Sections(List<FilterElement> parts, string a)
    {
        var left = new List<FilterElement>(parts);
        var sections = new List<List<FilterElement>>();
        string at = a;
        while (left.Count > 0)
        {
            var here = left.Where(p => p.NodeA == at || p.NodeB == at).ToList();
            if (here.Count == 0) return null;
            string Other(FilterElement p) => p.NodeA == at ? p.NodeB : p.NodeA;
            string next = Other(here[0]);
            if (here.Any(p => Other(p) != next)) return null;
            sections.Add(here.OrderBy(p => p.Inductor ? 0 : 1).ToList());
            foreach (var p in here) left.Remove(p);
            at = next;
            if (at == "0") break;
        }
        return left.Count == 0 && at == "0" ? sections : null;
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>The elements of <paramref name="parts"/> in chain order away from <paramref name="a"/>.</summary>
    private static List<FilterElement> ChainFrom(List<FilterElement> parts, string a, out string end)
    {
        var chain = new List<FilterElement>();
        var left = new List<FilterElement>(parts);
        end = a;
        while (true)
        {
            string at = end;
            var next = left.FirstOrDefault(p => p.NodeA == at || p.NodeB == at);
            if (next is null) break;
            chain.Add(next);
            left.Remove(next);
            end = next.NodeA == at ? next.NodeB : next.NodeA;
        }
        chain.AddRange(left);   // anything not on the walk still gets drawn
        return chain;
    }

    private static bool Touches(FilterElement o, string a, string b) =>
        (o.NodeA == a && o.NodeB == b) || (o.NodeA == b && o.NodeB == a);

    private static string Ohms(double r) =>
        r.ToString("0.###", CultureInfo.InvariantCulture) + " Ω";
}

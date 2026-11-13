// Does the circuit join what the copper joins? — recognition's check on itself (a board drawn as DXF outlines, 2026-11-12;
// R-as3-5, R-as5-6, overview D16).
//
// The copper is the truth. Every part terminal, port and kept via end on one signal island is joined to every other on it
// by copper (and by the vias the island was read through); the circuit must join them too, through its lines, junctions
// and VIAs — direct current only, so no part is ever crossed. Three readings of one board left the circuit split where the
// copper is one piece, and the report said nothing ("none was dropped"). So each difference is a finding with both places:
// an island the circuit splits, and a node the circuit joins across two islands. With none, one line says what was checked.
// It reads the line recognition's result before the emit drops stray lines, and changes nothing.

using CircuitRF.Design.RailRf;

namespace CircuitRF.Design.Layout.Recognition;

internal static class CircuitCopperCheck
{
    internal static void Check(IReadOnlyList<RecognizedVia> vias, LineRecognitionResult lines, RecognitionReport report, RailLengthFormat fmt)
    {
        var parent = new Dictionary<string, string>(StringComparer.Ordinal);
        string Find(string x)
        {
            while (parent.TryGetValue(x, out var p) && p != x) x = parent[x] = parent.GetValueOrDefault(p, p);
            return x;
        }
        void Union(string a, string b)
        {
            string ra = Find(a), rb = Find(b);
            if (ra != rb) parent[rb] = ra;
        }
        foreach (var e in lines.Elements)
            for (int i = 1; i < e.Nodes.Count; i++) Union(e.Nodes[0], e.Nodes[i]);
        for (int v = 0; v < vias.Count; v++)
            if (vias[v] is { Element: ViaElement.Via, Islands.Count: >= 2 })
                Union(lines.NodeOf($"V{v + 1}.1"), lines.NodeOf($"V{v + 1}.2"));

        var attached = lines.Attachments.Where(a => a.Island >= 0).ToList();
        string Root(LineNode a) => Find(lines.NodeOf(a.Name));
        string Where(LineNode a) => $"{a.Name} at {fmt.Point(a.X, a.Y)}";
        static RecognitionAnchor At(LineNode a) => new(a.X, a.Y, a.Layer);

        var splits = new List<(string Sentence, RecognitionAnchor[] At)>();
        foreach (var island in attached.GroupBy(a => a.Island))
        {
            var nodes = island.GroupBy(Root).ToList();
            var first = nodes[0].First();
            foreach (var other in nodes.Skip(1).Select(g => g.First()))
                splits.Add(($"copper joins {Where(first)} and {Where(other)}, the circuit does not", [At(first), At(other)]));
        }
        var joins = new List<(string Sentence, RecognitionAnchor[] At)>();
        foreach (var node in attached.GroupBy(Root))
        {
            var islands = node.GroupBy(a => a.Island).ToList();
            var first = islands[0].First();
            foreach (var other in islands.Skip(1).Select(g => g.First()))
                joins.Add(($"the circuit joins {Where(first)} and {Where(other)}, the copper does not", [At(first), At(other)]));
        }

        report.Add(RecognitionFindingClass.CircuitSplitsCopper, splits.Count,
            $"{Plural(splits.Count, "place where", "places where")} the circuit does not join what the copper joins — a line or a " +
            $"pad that was not modelled: {string.Join("; ", splits.Take(8).Select(s => s.Sentence))}{(splits.Count > 8 ? "; …" : "")}.",
            [.. splits.SelectMany(s => s.At)]);
        report.Add(RecognitionFindingClass.CircuitJoinsAcrossCopper, joins.Count,
            $"{Plural(joins.Count, "place where", "places where")} the circuit joins what the copper keeps apart: " +
            $"{string.Join("; ", joins.Take(8).Select(s => s.Sentence))}{(joins.Count > 8 ? "; …" : "")}.",
            [.. joins.SelectMany(s => s.At)]);
        // Not while a join across copper no line models stands in the report: the nodes then agree only because of it.
        if (splits.Count == 0 && joins.Count == 0 && attached.Count > 0 && report.Count(RecognitionFindingClass.CopperJoinedWithoutLine) == 0)
            report.Add(RecognitionFindingClass.CircuitAgreesWithCopper, attached.Count,
                $"The circuit joins what the copper joins: {Plural(attached.Count, "part terminal, port or via end", "part terminals, ports and via ends")} " +
                $"on {Plural(attached.Select(a => a.Island).Distinct().Count(), "island", "islands")} checked.");
    }

    private static string Plural(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";
}

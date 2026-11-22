using CircuitRF.Core.Matching;
using CircuitRF.Core.Systems;
using CircuitRF.Design.Schematic;
using CircuitRF.Ui.Filters;
using CircuitRF.Ui.Matching;
using Xunit;

namespace CircuitRF.Ui.Tests.Filters;

public class FilterLadderDrawingTests
{
    private static FilterRealization Real(FilterResponse response, NetworkForm form, int order,
                                          FilterFirstElement first)
    {
        var built = FilterLadder.Synthesize(response, form, order, 1e9, 0.8e9, 1.25e9, 0.1, 50,
                                            first, 40);
        Assert.True(built.Ok, built.Refusal?.Message);
        return built.Realization!;
    }

    private static MatchLadderLayout Layout(FilterRealization r) =>
        FilterLadderDrawing.Build(r.Elements, r.Arms, r.SourceOhms, r.LoadOhms,
                                  (ind, v) => ind ? $"{v * 1e9:0.####} nH" : $"{v * 1e12:0.####} pF");

    [Fact]
    public void AnEllipticLowpassStubSharesOneColumn()
    {
        var layout = Layout(Real(FilterResponse.Elliptic, NetworkForm.Lowpass, 3, FilterFirstElement.Series));

        // Series L, stub (L over C), series L: four symbols, the stub in one column.
        Assert.Equal(4, layout.Elements.Count);
        var stub = layout.Elements.Where(e => e.IsShunt).ToList();
        Assert.Equal(2, stub.Count);
        Assert.Equal(stub[0].X, stub[1].X);
        Assert.True(stub[1].Y > stub[0].Y);
    }

    [Fact]
    public void AParallelTankSharesOneSeriesColumnAboveTheSpine()
    {
        var layout = Layout(Real(FilterResponse.Elliptic, NetworkForm.Lowpass, 5, FilterFirstElement.Parallel));

        var tanks = layout.Elements.Where(e => !e.IsShunt).GroupBy(e => e.X)
            .Where(g => g.Count() == 2).ToList();
        Assert.Equal(2, tanks.Count);
        foreach (var tank in tanks)
        {
            Assert.Equal(ElementType.C, tank.Single(e => e.Y == MatchLadderLayout.SpineY).Type);
            Assert.Equal(ElementType.L, tank.Single(e => e.Y < MatchLadderLayout.SpineY).Type);
        }
        Assert.Equal(tanks.Count, layout.ParallelArms.Count);
    }

    /// <summary>
    /// A bandpass elliptic series arm is (series L–C) ∥ L ∥ C: the series pair on a rail above,
    /// the L on the spine, the C on a rail below — three branches between the same two drops.
    /// </summary>
    [Fact]
    public void AnEllipticBandpassSeriesArmIsAThreeBranchBox()
    {
        var real = Real(FilterResponse.Elliptic, NetworkForm.Bandpass, 5, FilterFirstElement.Parallel);
        var layout = Layout(real);

        var boxes = real.Arms.Where(a => a.Kind == FilterArmKind.SeriesTank).ToList();
        Assert.Equal(2, boxes.Count);
        foreach (var box in boxes)
        {
            var names = box.Elements.Select(i => real.Elements[i].Name).ToHashSet();
            var placed = layout.Elements.Where(e => names.Contains(e.Name)).ToList();
            Assert.Equal(4, placed.Count);
            var top = placed.Where(e => e.Y < MatchLadderLayout.SpineY).OrderBy(e => e.X).ToList();
            var spine = placed.Single(e => e.Y == MatchLadderLayout.SpineY);
            var bottom = placed.Single(e => e.Y > MatchLadderLayout.SpineY);
            Assert.Equal(2, top.Count);
            Assert.Equal(top[0].Y, top[1].Y);
            Assert.Equal(ElementType.L, spine.Type);
            Assert.Equal(ElementType.C, bottom.Type);
            Assert.True(top[0].X < spine.X && spine.X < top[1].X);
            Assert.Equal(spine.X, bottom.X);
        }
        // Two rails per box, each between the box's own drops.
        Assert.Equal(4, layout.ParallelArms.Count);
    }

    /// <summary>
    /// The topology gate. The schematic the window copies is extracted the way the schematic
    /// editor extracts any drawing, and its netlist has to BE the ladder: one consistent map from
    /// the ladder's nodes to the drawing's nets under which every element lands on its own two
    /// nodes and the terminations on the two ports. A rail that ran into the next arm, an element
    /// wired to nothing, or a ground in the middle of a stub all break the map.
    /// </summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public void TheCopiedSchematicIsTheLadder(FilterResponse response, NetworkForm form, int order,
                                              FilterFirstElement first)
    {
        var real = Real(response, form, order, first);
        var copy = MatchSchematicCopy.Build(Layout(real));
        var tb = NetExtractor.Extract(copy).TestBench;
        var byName = tb.Instances.ToDictionary(i => i.InstanceName, StringComparer.Ordinal);

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var used = new Dictionary<string, string>(StringComparer.Ordinal);
        void Bind(string node, string net, string why)
        {
            if (map.TryGetValue(node, out var had))
                Assert.True(had == net, $"{why}: node {node} is on net {had} and on net {net}");
            else
            {
                Assert.False(used.TryGetValue(net, out var other),
                    $"{why}: net {net} joins nodes {other} and {node}");
                map[node] = net;
                used[net] = node;
            }
        }

        // The terminations' own grounds name the ground net.
        string ground = byName[MatchSchematicCopy.TerminationName(1)].NetBindings[^1];
        Bind("0", ground, "ground");
        Bind("p1", byName[MatchSchematicCopy.TerminationName(1)].NetBindings[0], "T1");
        Bind(FilterLadder.LoadNode(real.Elements), byName[MatchSchematicCopy.TerminationName(2)].NetBindings[0], "T2");

        foreach (var e in real.Elements)
        {
            Assert.True(byName.TryGetValue(e.Name, out var inst), $"{e.Name} is not in the drawing");
            var nets = inst!.NetBindings;
            Assert.Equal(2, nets.Count);
            // Orientation is the drawing's choice; try the binding that agrees with what is known.
            bool straight = !(map.TryGetValue(e.NodeA, out var na) && na != nets[0])
                            && !(map.TryGetValue(e.NodeB, out var nb) && nb != nets[1]);
            Bind(e.NodeA, straight ? nets[0] : nets[1], e.Name);
            Bind(e.NodeB, straight ? nets[1] : nets[0], e.Name);
        }
    }

    /// <summary>
    /// Every label sits by its own glyph and on nothing else: no label crosses another label, a
    /// glyph or a wire, and each label block is nearer its own glyph than any other.
    /// </summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public void EveryLabelIsClearAndBesideItsOwnGlyph(FilterResponse response, NetworkForm form,
                                                      int order, FilterFirstElement first)
    {
        var model = MatchSchematicModel.Build(Layout(Real(response, form, order, first)));
        var glyphs = model.Components
            .Select(c => (c.Id, Box: (c.GlyphBbMinX, c.GlyphBbMinY, c.GlyphBbMaxX, c.GlyphBbMaxY)))
            .ToList();
        var labels = new List<(string Id, (double, double, double, double) Box)>();
        foreach (var c in model.Components)
        {
            var rows = new List<(double, double, double, double)>();
            for (int i = 0; i < c.Labels.Count; i++)
            {
                if (string.IsNullOrEmpty(c.Labels[i])) continue;
                var (dx, dy) = SchematicComponent.LabelOffsetAt(c.LabelOffsets, i);
                var (bx, by, _, _) = SchematicComponent.LabelRowGeometry(
                    c.X, c.Y, i, dx, dy, c.Symbol, c.Ports.Count / 2, c.GlyphBbMaxY - c.Y);
                // The text's measured width, by the same font the pane draws it with.
                double width = MatchShuntLabels.EstimateWidth([c.Labels[i]]);
                rows.Add((bx, by - SchematicComponent.LabelWorldHeight, bx + width, by + 18.0));
            }
            if (rows.Count == 0) continue;
            labels.Add((c.Id, (rows.Min(r => r.Item1), rows.Min(r => r.Item2),
                               rows.Max(r => r.Item3), rows.Max(r => r.Item4))));
        }

        static bool Overlap((double, double, double, double) a, (double, double, double, double) b) =>
            a.Item1 < b.Item3 && b.Item1 < a.Item3 && a.Item2 < b.Item4 && b.Item2 < a.Item4;
        // Centre to centre: a series glyph's box includes its two lead wires, which a beside label
        // may approach without being any less clearly its own symbol's.
        static double Dist((double, double, double, double) a, (double, double, double, double) b)
        {
            double dx = (a.Item1 + a.Item3) / 2 - (b.Item1 + b.Item3) / 2;
            double dy = (a.Item2 + a.Item4) / 2 - (b.Item2 + b.Item4) / 2;
            return Math.Sqrt(dx * dx + dy * dy);
        }
        static string Show((double, double, double, double) b) => $"[{b.Item1:0},{b.Item2:0} – {b.Item3:0},{b.Item4:0}]";
        var problems = new List<string>();
        for (int i = 0; i < labels.Count; i++)
        {
            var (id, box) = labels[i];
            for (int j = i + 1; j < labels.Count; j++)
                if (Overlap(box, labels[j].Box)) problems.Add($"{id}'s label {Show(box)} overlaps {labels[j].Id}'s {Show(labels[j].Box)}");
            foreach (var g in glyphs)
                if (Overlap(box, g.Box)) problems.Add($"{id}'s label {Show(box)} overlaps {g.Id}'s glyph {Show(g.Box)}");
            foreach (var w in model.Wires)
            {
                var (x0, y0) = w.Points[0];
                var (x1, y1) = w.Points[^1];
                var wb = (Math.Min(x0, x1) - 2, Math.Min(y0, y1) - 2, Math.Max(x0, x1) + 2, Math.Max(y0, y1) + 2);
                if (Overlap(box, wb)) problems.Add($"{id}'s label crosses a wire at ({x0},{y0})–({x1},{y1})");
            }
            if (id.EndsWith(MatchSchematicModel.GroundIdSuffix, StringComparison.Ordinal)) continue;
            var own = glyphs.Single(g => g.Id == id).Box;
            double mine = Dist(box, own);
            foreach (var g in glyphs.Where(g => g.Id != id && !g.Id.EndsWith(MatchSchematicModel.GroundIdSuffix, StringComparison.Ordinal)))
                if (Dist(box, g.Box) < mine) problems.Add($"{id}'s label is nearer {g.Id} than its own glyph");
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems.Distinct().Take(20)));
    }

    public static TheoryData<FilterResponse, NetworkForm, int, FilterFirstElement> Cases()
    {
        var data = new TheoryData<FilterResponse, NetworkForm, int, FilterFirstElement>();
        foreach (var form in new[] { NetworkForm.Lowpass, NetworkForm.Highpass, NetworkForm.Bandpass, NetworkForm.Bandstop })
            foreach (var first in new[] { FilterFirstElement.Series, FilterFirstElement.Parallel })
            {
                foreach (int n in new[] { 3, 4, 5, 6, 7, 9, 12, 15 })
                    data.Add(FilterResponse.Elliptic, form, n, first);
                data.Add(FilterResponse.Chebyshev, form, 5, first);
                data.Add(FilterResponse.Butterworth, form, 4, first);
            }
        return data;
    }
}

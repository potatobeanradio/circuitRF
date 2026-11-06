// The nets a layout names, counted — brief-ipx-1 R-ipx1-1b/1c and R-ipx1-3. The Nets panel and `explain --nets` both call
// Of, so the two cannot disagree.
//
// ── WHY ONLY THE OPEN CELL IS COUNTED (R-ipx1-1c) ──────────────────────────────────────────────────────────────
//
// A net does not reach copper inside a placed instance through anything an instance stores. LayoutInstance has no pin-to-net
// map, and a land pattern is one cell shared by every placement of it, so a Net inside it would put every placement on one
// net: the board reader drops a pad's net rather than store it there. What joins a pad to a net is a question about the board:
// the schematic's binding, the board netlist, or a walk of the copper it touches (CopperPieces over the FLATTENED artwork).
// That walk is a polygon union and a connected-component pass over the whole board, measured at seconds on a large one, which
// is not a list built on every edit. So the counts are the open cell's own shapes and Scope says so wherever copper was left out.

using CircuitRF.Design.Layout.Recognition;

namespace CircuitRF.Design.Layout;

/// <summary>One row of the list: a net, how many of the open cell's shapes carry it, and the layers those shapes reach.</summary>
/// <param name="Name">The net's name, exactly as the shapes spell it.</param>
/// <param name="ShapeCount">Shapes in the open cell carrying this name.</param>
/// <param name="Layers">Layer names, top of the technology first and drill layers last. A via adds the two conductors its
/// stackup entry joins.</param>
public sealed record LayoutNetRow(string Name, int ShapeCount, IReadOnlyList<string> Layers)
{
    /// <summary>The layers as one line, for a list row.</summary>
    public string LayersText => string.Join(", ", Layers);
}

/// <summary>What <see cref="LayoutNets.Of"/> found, and the way back from a row to its shapes without another scan.</summary>
public sealed class LayoutNetList
{
    private readonly Dictionary<string, int[]> _shapes;

    internal LayoutNetList(IReadOnlyList<LayoutNetRow> rows, Dictionary<string, int[]> shapes, int placedInstances)
    {
        Rows = rows;
        _shapes = shapes;
        PlacedInstances = placedInstances;
    }

    /// <summary>One row per net, in natural order, so <c>N2</c> precedes <c>N10</c>.</summary>
    public IReadOnlyList<LayoutNetRow> Rows { get; }

    /// <summary>How many instances the open cell places, whose copper is in no count.</summary>
    public int PlacedInstances { get; }

    /// <summary>What the counts leave out, or null where they leave out nothing.</summary>
    public string? Scope => PlacedInstances == 0
        ? null
        : $"Counts shapes drawn in this cell, not copper inside its {(PlacedInstances == 1 ? "placed part" : $"{PlacedInstances:N0} placed parts")}.";

    /// <summary>The indices into <c>LayoutView.Shapes</c> of the shapes carrying <paramref name="net"/>; empty for a name it does not hold.</summary>
    public IReadOnlyList<int> ShapesOf(string net) => _shapes.TryGetValue(net, out var indices) ? indices : [];
}

public static class LayoutNets
{
    /// <summary>The sentence shown where a layout names no net at all.</summary>
    public const string NoNamedNets = "This layout has no named nets.";

    /// <summary>
    /// The nets named by <paramref name="view"/>'s own shapes. One pass over the shapes; nothing is flattened and no instance
    /// is opened. A label or a bitmap is not copper and is not counted, whatever it carries.
    /// </summary>
    /// <param name="view">The open cell.</param>
    /// <param name="tech">Names and orders the layers, and gives a via its span. Null spells each layer by its key.</param>
    public static LayoutNetList Of(LayoutView view, Technology? tech)
    {
        ArgumentNullException.ThrowIfNull(view);

        var indices = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var reached = new Dictionary<string, HashSet<LayerKey>>(StringComparer.Ordinal);
        var viaReach = new Dictionary<LayerKey, LayerKey[]>();

        for (int i = 0; i < view.Shapes.Count; i++)
        {
            var shape = view.Shapes[i];
            if (shape is LabelShape or BitmapShape || shape.Net is not { Length: > 0 } net) continue;

            if (!indices.TryGetValue(net, out var list))
            {
                indices[net] = list = [];
                reached[net] = [];
            }
            list.Add(i);

            var layers = reached[net];
            layers.Add(shape.Layer);
            if (shape is ViaShape)
            {
                if (!viaReach.TryGetValue(shape.Layer, out var joined))
                    viaReach[shape.Layer] = joined = ViaConductors(shape.Layer, tech);
                layers.UnionWith(joined);
            }
        }

        var order = LayerOrder(tech);
        var rows = indices.Keys
            .OrderBy(n => n, PartsTable.NaturalOrder)
            .ThenBy(n => n, StringComparer.Ordinal)    // natural order ignores case and leading zeros; two such names are two nets
            .Select(n => new LayoutNetRow(n, indices[n].Count, NamesOf(reached[n], order)))
            .ToList();

        return new LayoutNetList(rows, indices.ToDictionary(e => e.Key, e => e.Value.ToArray(), StringComparer.Ordinal),
                                 view.Instances.Count);
    }

    private static LayerKey[] ViaConductors(LayerKey barrel, Technology? tech) =>
        ViaSpanResolver.Resolve(barrel, tech) is { } span
            ? [.. span.Top.DrawingLayers, .. span.Bottom.DrawingLayers]
            : [];

    private static Dictionary<LayerKey, (int Rank, string Name)> LayerOrder(Technology? tech)
    {
        var order = new Dictionary<LayerKey, (int, string)>();
        if (tech is null) return order;
        var drills = ViaSpanResolver.DrillLayerKeys(tech);
        int rank = 0;
        foreach (var layer in tech.Layers.OrderBy(l => drills.Contains(l.Key)).ThenByDescending(l => l.ZOrder))
            order[layer.Key] = (rank++, layer.Name.Length > 0 ? layer.Name : $"{layer.Key.Layer}/{layer.Key.Datatype}");
        return order;
    }

    private static string[] NamesOf(HashSet<LayerKey> keys, Dictionary<LayerKey, (int Rank, string Name)> order) =>
        [.. keys.Select(k => order.TryGetValue(k, out var known) ? known : (Rank: int.MaxValue, Name: $"{k.Layer}/{k.Datatype}"))
                .OrderBy(l => l.Rank)
                .ThenBy(l => l.Name, StringComparer.Ordinal)
                .Select(l => l.Name)];
}

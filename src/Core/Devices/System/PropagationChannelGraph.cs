using CircuitRF.Core.Elaboration;

namespace CircuitRF.Core.Devices;

/// <summary>
/// brief-pch-6 R-pch6-10 — how the Propagation Channels of a netlist connect the circuit's sides. A side ("island") is a set
/// of nets joined through any component other than a channel, not counting ground or a net a DC supply holds (each is an AC
/// ground: a PA and an LNA sharing a supply are not one circuit at RF). A channel is an edge from the side of its transmit
/// pin to the side of its receive pin.
/// </summary>
public static class PropagationChannelGraph
{
    /// <summary>The side each node is on (-1 for ground and for a supply-held net).</summary>
    public static int[] Islands(ElaboratedNetlist nl)
    {
        int n = nl.Nodes.Count + 1;
        var parent = Enumerable.Range(0, n).ToArray();
        int Find(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
        var acGround = new HashSet<int> { 0 };
        foreach (var ec in nl.Components)
            if (ec.Model is VdcModel)
                foreach (int node in ec.Nodes) acGround.Add(node);
        foreach (var ec in nl.Components)
        {
            if (ec.Model is PropagationChannelModel) continue;
            int first = -1;
            foreach (int node in ec.Nodes)
            {
                if (node <= 0 || node >= n || acGround.Contains(node)) continue;
                if (first < 0) first = node;
                else parent[Find(node)] = Find(first);
            }
        }
        var island = new int[n];
        for (int i = 0; i < n; i++) island[i] = acGround.Contains(i) ? -1 : Find(i);
        return island;
    }

    /// <summary>A channel's transmit and receive nodes: its pin, or (brief PCH-8) an element node of the array an instance end names.</summary>
    public static (int Tx, int Rx) EndNodes(ElaboratedComponent ec)
    {
        var m = (PropagationChannelModel)ec.Model;
        int Pin(int i) => i >= 0 && i < ec.Nodes.Length ? ec.Nodes[i] : 0;
        int tx = m.TxEnd is { } te ? (te.Nodes.Length > 0 ? te.Nodes[0] : 0) : Pin(m.TxPin);
        int rx = m.RxEnd is { } re ? (re.Nodes.Length > 0 ? re.Nodes[0] : 0) : Pin(m.RxPin);
        return (tx, rx);
    }

    private static IEnumerable<(ElaboratedComponent Ec, int Tx, int Rx)> Edges(ElaboratedNetlist nl, int[] island)
    {
        foreach (var ec in nl.Components)
            if (ec.Model is PropagationChannelModel)
            {
                var (tx, rx) = EndNodes(ec);
                yield return (ec, tx > 0 && tx < island.Length ? island[tx] : -1, rx > 0 && rx < island.Length ? island[rx] : -1);
            }
    }

    /// <summary>Every cycle of channels: the instances whose receive sides lead back to a transmit side of the same set, each
    /// cycle once, in the order they are met. A channel whose receive side is its own transmit side is a cycle of one.</summary>
    public static IReadOnlyList<IReadOnlyList<string>> Cycles(ElaboratedNetlist nl)
    {
        var island = Islands(nl);
        var edges = Edges(nl, island).Where(e => e.Tx >= 0 && e.Rx >= 0).ToList();
        var cycles = new List<IReadOnlyList<string>>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        // a path search from each edge back to its own transmit side; the channel counts are small
        foreach (var start in edges)
        {
            var stack = new Stack<(int Side, List<string> Path)>();
            stack.Push((start.Rx, [start.Ec.InstancePath]));
            while (stack.Count > 0)
            {
                var (side, path) = stack.Pop();
                if (side == start.Tx)
                {
                    string key = string.Join("|", path.Order(StringComparer.Ordinal));
                    if (seen.Add(key)) cycles.Add(path);
                    continue;
                }
                foreach (var e in edges)
                    if (e.Tx == side && !path.Contains(e.Ec.InstancePath) && path.Count < edges.Count)
                        stack.Push((e.Rx, [.. path, e.Ec.InstancePath]));
            }
        }
        return cycles;
    }

    /// <summary>Each receive net more than one channel drives, with those channels: a pin receive end takes one channel (an instance
    /// receive end takes any number, brief PCH-8 R-pch8-9).</summary>
    public static IReadOnlyList<(string Net, IReadOnlyList<string> Channels)> SharedReceivePins(ElaboratedNetlist nl)
        => [.. nl.Components.Where(c => c.Model is PropagationChannelModel m && m.RxPin >= 0 && m.RxPin < c.Nodes.Length && c.Nodes[m.RxPin] > 0)
                 .GroupBy(c => c.Nodes[((PropagationChannelModel)c.Model).RxPin]).Where(g => g.Count() > 1)
                 .Select(g => (nl.Nodes.NameOf(g.Key), (IReadOnlyList<string>)[.. g.Select(c => c.InstancePath)]))];

    /// <summary>The one channel whose receive side holds <paramref name="node"/>, or null (none, or more than one).</summary>
    public static ElaboratedComponent? ReceivingAt(ElaboratedNetlist nl, int node, int[]? island = null)
    {
        island ??= Islands(nl);
        if (node <= 0 || node >= island.Length || island[node] < 0) return null;
        var hits = Edges(nl, island).Where(e => e.Rx == island[node]).Select(e => e.Ec).ToList();
        return hits.Count == 1 ? hits[0] : null;
    }
}

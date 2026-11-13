using CircuitRF.Core.Elaboration;

namespace CircuitRF.Core.Devices.External;

/// <summary>
/// How strongly an external device's OWN thermal network holds each net its thermal nodes land on: the conductance from
/// that net back to the thermal ground through the device alone, read from the device's Jacobian at one point.
///
/// <para><b>Why a pin's own diagonal is not the answer (issue 67).</b> A compiled model routinely puts its network on an
/// INTERNAL node and joins the pin to it with a zero-temperature-difference branch. OSDI reports that pair as collapsed,
/// so the elaborator gives the internal node the pin's net (<see cref="ExternalNodeDescriptor.SlavedTo"/>) — and the
/// model's <c>1/Rth</c> then sits at the internal node's local index, while the pin's own entry is zero. Read alone, the
/// pin looked unreferenced, was held at the ambient, and a rise-type part ran 25 K hot. What the solve actually sees on a
/// net is the sum of every local entry that lands on it, so that is what is summed here.</para>
///
/// <para><b>Through thermal couplings, as a network.</b> A ladder puts its sections on further internal nodes, joined to
/// one another by conductances. The device's thermal nodes are grouped into the nets they land on, nets joined by any
/// non-zero thermal-to-thermal entry are one network, and each net reports its network's conductance to everything
/// outside it: the sum of the network's block of the Jacobian, which for a resistive network is exactly its conductance
/// to ground. A network that lands on ground itself is held by definition (<see cref="double.PositiveInfinity"/>).
/// Couplings to electrical nodes are left out: the dissipated power depending on a voltage holds no temperature.</para>
///
/// <para>The sign is the caller's to judge. A real path is a POSITIVE conductance; an electrothermal model's own
/// self-heating feedback is negative, and must not be read as a reference.</para>
/// </summary>
public static class ExternalThermalPaths
{
    /// <summary>Each non-ground net one of <paramref name="ec"/>'s thermal nodes lands on, with the conductance the device's
    /// own thermal network holds it by, from <paramref name="dg"/> (the device's Jacobian, local node by local node).</summary>
    public static Dictionary<int, double> HeldByDevice(ElaboratedComponent ec, ExternalDeviceModel ed, double[,] dg)
    {
        var local = ed.Descriptor.Nodes
            .Where(n => n.QuantityKind == NodeQuantityKind.Thermal
                        && n.Index < dg.GetLength(0) && n.Index < dg.GetLength(1))
            .Select(n => (Local: n.Index, Net: ec.Nodes.Length > 2 * n.Index ? ec.Nodes[2 * n.Index] : 0))
            .ToList();

        // nets joined by a thermal entry are one network
        var parent = new Dictionary<int, int>();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        foreach (var (_, net) in local) parent.TryAdd(net, net);
        foreach (var a in local)
            foreach (var b in local)
                if (a.Net != b.Net && (dg[a.Local, b.Local] != 0.0 || dg[b.Local, a.Local] != 0.0))
                    parent[Find(a.Net)] = Find(b.Net);

        var sum = new Dictionary<int, double>();
        foreach (var a in local)
            foreach (var b in local)
                if (Find(a.Net) == Find(b.Net))
                {
                    int root = Find(a.Net);
                    sum[root] = (sum.TryGetValue(root, out double s) ? s : 0.0) + dg[a.Local, b.Local];
                }

        var held = new Dictionary<int, double>();
        foreach (var net in parent.Keys)
        {
            if (net <= 0) continue;
            int root = Find(net);
            held[net] = parent.ContainsKey(0) && Find(0) == root ? double.PositiveInfinity
                      : sum.TryGetValue(root, out double g) ? g : 0.0;
        }
        return held;
    }
}

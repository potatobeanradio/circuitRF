using System.Globalization;
using System.Text;
using RfCore;

namespace CircuitRF.Core.Pdk;

/// <summary>One device the composed part places, and the two nets it spans.</summary>
/// <param name="ComponentType">circuitRF's component type, e.g. <c>Diode</c>.</param>
/// <param name="Designator">Instance name, unique within the part.</param>
/// <param name="NetA">Net at the device's first terminal.</param>
/// <param name="NetB">Net at the device's second terminal.</param>
public sealed record ComposedDevice(string ComponentType, string Designator, string NetA, string NetB);

/// <summary>A part built from a network and an inventory — enough to simulate.</summary>
/// <param name="Name">The part's name.</param>
/// <param name="Pins">External pin nets, in port order.</param>
/// <param name="NetworkPorts">Nets bound to the network's ports, in port order (pins then sites).</param>
/// <param name="Devices">One device per measured site.</param>
/// <param name="Derivation">How the topology was arrived at, for a user-facing report.</param>
public sealed record ComposedPart(
    string                          Name,
    IReadOnlyList<string>           Pins,
    IReadOnlyList<string>           NetworkPorts,
    IReadOnlyList<ComposedDevice>   Devices,
    string                          Derivation);

/// <summary>Why a part could not be composed. Never a partially-built part.</summary>
/// <param name="Reason">In words, for a user-facing report.</param>
public sealed record CompositionFailure(string Reason);

/// <summary>
/// Builds a simulatable part from a kit's own network file plus the inventory the kit's library
/// declared — with the topology MEASURED rather than assumed.
///
/// <para><b>What each input is, and where it legitimately comes from.</b> The network is the kit's
/// own data file. The inventory — how many devices of what kind — is what the library itself said
/// when asked to build the part. The mapping from the kit's name for a primitive to a circuitRF
/// component type is <b>runtime data supplied by the caller</b>, never a table in this file: a
/// primitive's name belongs to the kit on the user's machine, and nothing here may know it.</para>
///
/// <para><b>The cross-check is the point, not a formality.</b> Two entirely independent sources say
/// how many devices the part has: the library's component list, and the number of differential
/// sites the network physically exposes. When they disagree, one of the two readings is wrong and
/// this refuses rather than composing something. A part built past that disagreement would simulate
/// perfectly and be a different circuit — the failure mode this whole import path exists to
/// avoid.</para>
/// </summary>
public static class KitPartComposer
{
    /// <summary>Net name for the k-th network port that is a device site.</summary>
    private static string SiteNet(int port) => $"D{port}";

    /// <summary>Net name for the k-th external pin.</summary>
    private static string PinNet(int index) => $"P{index}";

    /// <summary>
    /// Composes <paramref name="partName"/> from its network and its declared inventory.
    /// </summary>
    /// <param name="partName">The part's name, as the kit declares it.</param>
    /// <param name="network">The kit's network file for this part.</param>
    /// <param name="deviceCounts">
    /// How many of each kit primitive the library asked for, keyed by the kit's own name for it.
    /// Only kinds present in <paramref name="primitiveMap"/> are placed; anything else is reported
    /// as unmapped rather than dropped silently.
    /// </param>
    /// <param name="primitiveMap">
    /// Kit primitive name -> circuitRF component type. Runtime data, supplied by the caller.
    /// </param>
    /// <param name="failure">Why, when the result is null.</param>
    public static ComposedPart? Compose(
        string                                  partName,
        SNP                                     network,
        IReadOnlyDictionary<string, int>        deviceCounts,
        IReadOnlyDictionary<string, string>     primitiveMap,
        out CompositionFailure?                 failure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partName);
        ArgumentNullException.ThrowIfNull(network);
        ArgumentNullException.ThrowIfNull(deviceCounts);
        ArgumentNullException.ThrowIfNull(primitiveMap);

        failure = null;

        // Which declared kinds are two-terminal devices we can place. A kind that maps to nothing is
        // named in the refusal: an unmapped primitive is a hole in the caller's map, and treating it
        // as absent would quietly build a part with components missing.
        var placeable = deviceCounts
            .Where(kv => kv.Value > 0 && primitiveMap.ContainsKey(kv.Key))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .ToList();

        var unmapped = deviceCounts
            .Where(kv => kv.Value > 0 && !primitiveMap.ContainsKey(kv.Key))
            .Select(kv => kv.Key)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        if (placeable.Count == 0)
        {
            failure = new($"none of the kit primitives this part uses " +
                          $"({string.Join(", ", deviceCounts.Keys.Order(StringComparer.Ordinal))}) " +
                          "are mapped to a circuitRF component type.");
            return null;
        }

        if (placeable.Count > 1)
        {
            // Several device kinds means the site-to-kind assignment is a real question, and
            // counting cannot answer it. Refuse rather than pick an order.
            failure = new($"this part declares more than one placeable device kind " +
                          $"({string.Join(", ", placeable.Select(p => p.Key))}); which site takes " +
                          "which kind is not decided by the network, so it cannot be composed by " +
                          "count alone.");
            return null;
        }

        var scan = DifferentialPortPairs.Scan(network);

        if (scan.Pairs.Count == 0)
        {
            failure = new("the network exposes no differential port pairs, so there is nowhere for " +
                          "a two-terminal device to sit. A part whose devices are shunt-mounted " +
                          "leaves one port each, not two, and needs a different reading.");
            return null;
        }

        var (kitKind, declared) = (placeable[0].Key, placeable[0].Value);

        if (declared != scan.Pairs.Count)
        {
            failure = new($"the library declared {declared} x '{kitKind}', but the network exposes " +
                          $"{scan.Pairs.Count} differential site(s). Those are two independent " +
                          "statements about the same part and they disagree, so composing either " +
                          "one would be a guess.");
            return null;
        }

        if (unmapped.Count > 0)
        {
            failure = new($"this part also uses {string.Join(", ", unmapped)}, which the supplied " +
                          "map does not name. Composing without them would leave components out.");
            return null;
        }

        // Nets: the unpaired ports are the pins, the paired ones are the sites. Both come from the
        // measurement — no port-ordering convention is assumed anywhere here.
        var netOfPort = new Dictionary<int, string>();
        var pins = new List<string>();

        for (int i = 0; i < scan.UnpairedPorts.Count; i++)
        {
            string net = PinNet(i + 1);
            pins.Add(net);
            netOfPort[scan.UnpairedPorts[i]] = net;
        }
        foreach (var p in scan.Pairs)
        {
            netOfPort[p.PortA] = SiteNet(p.PortA);
            netOfPort[p.PortB] = SiteNet(p.PortB);
        }

        var ports = new List<string>(network.Ports);
        for (int port = 1; port <= network.Ports; port++)
            ports.Add(netOfPort.TryGetValue(port, out var n) ? n : SiteNet(port));

        string type = primitiveMap[kitKind];
        var devices = scan.Pairs
            .Select((p, k) => new ComposedDevice(type, $"D{k + 1}",
                                                 SiteNet(p.PortA), SiteNet(p.PortB)))
            .ToList();

        return new ComposedPart(
            partName, pins, ports, devices,
            $"{network.Ports}-port network; {pins.Count} port(s) have no differential partner and " +
            $"are the pins; {scan.Pairs.Count} measured differential site(s) carry one '{kitKind}' " +
            $"each (worst cancellation {scan.WorstResidual:G3}), matching the {declared} the " +
            "library declared.");
    }

    /// <summary>
    /// Writes the part as a <c>.cnl</c> <c>define</c> block.
    /// </summary>
    /// <param name="part">The composed part.</param>
    /// <param name="networkFile">Network file path as the netlist should reference it.</param>
    /// <param name="deviceParameters">
    /// Parameters appended to every device line, verbatim (e.g. <c>Is=1e-6 N=1.05</c>). Empty leaves
    /// the component model's own defaults in place — device values are not derivable from a network
    /// and this composer does not invent them.
    /// </param>
    public static string ToCnl(ComposedPart part, string networkFile, string deviceParameters = "")
    {
        ArgumentNullException.ThrowIfNull(part);
        ArgumentException.ThrowIfNullOrWhiteSpace(networkFile);

        var sb = new StringBuilder();
        sb.Append("; GENERATED by circuitRF from the kit's own data — do not edit by hand.\n");
        sb.Append("; Topology is MEASURED from the network, not assumed from its port order:\n");
        sb.Append($";   {part.Derivation}\n");

        sb.Append(CultureInfo.InvariantCulture,
                  $"define {part.Name} ( {string.Join(" ", part.Pins)} )\n");

        sb.Append(CultureInfo.InvariantCulture,
                  $"  SnP:NET  {string.Join(" ", part.NetworkPorts)}  " +
                  $"NumPorts={part.NetworkPorts.Count} File=\"{networkFile}\"\n");

        string tail = string.IsNullOrWhiteSpace(deviceParameters) ? "" : "  " + deviceParameters.Trim();
        foreach (var d in part.Devices)
            sb.Append(CultureInfo.InvariantCulture,
                      $"  {d.ComponentType}:{d.Designator}  {d.NetA} {d.NetB}{tail}\n");

        sb.Append($"end {part.Name}\n");
        return sb.ToString();
    }
}

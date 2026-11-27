// Owner: PCD-4 (brief-pcd-0 §5, seam 8; written by PCD-1's seams commit). Framework-free.

using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Schematic;

namespace CircuitRF.Design.PCellDesigner;

public sealed record PinDropResult(SchematicEditModel? Schematic, string? InstanceName, string? Refusal);

/// <summary>brief-pcd-0 R-pcd4-2: layout-first wiring, a part dropped on a pin.</summary>
/// <remarks>brief-pcd-4 D7: the part is extracted beside the schematic, so its line is the one the extraction writes;
/// its pin goes onto the target's net and the text goes through <see cref="CellNetlistMerge"/>.</remarks>
public static class PinDrop
{
    /// <summary>part, as the Library palette makes it for placement, added with its pin partPin on targetInstance's pin
    /// targetPin's net; a new designator; through Merge. Never mutates current.</summary>
    public static PinDropResult Apply(SchematicEditModel current, EditableComponent part, int partPin,
                                      string targetInstance, int targetPin)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(part);

        var withPart = CellNetlistMerge.Copy(current);
        string name = SchematicEditModel.NextAvailableName(withPart.Components, ComponentTypeRegistry.InstancePrefix(part.Symbol));
        var dropped = part.Clone();
        dropped.InstanceName = name;
        // Far to the right of everything, so none of its pins touches the drawing: its own nets are its own.
        double right = withPart.Components.Select(c => c.X).Concat(withPart.Wires.SelectMany(w => w.Points.Select(p => p.X)))
                               .DefaultIfEmpty(0).Max();
        (dropped.X, dropped.Y) = (Math.Ceiling((right + 5000) / NetlistSchematic.Grid) * NetlistSchematic.Grid, 0);
        withPart.Components.Add(dropped);

        var extracted = SchematicCircuit.Extract(withPart, "cell");
        var tb = extracted.TestBench;
        var target = tb.Instances.FirstOrDefault(i => i.InstanceName == targetInstance);
        var line = tb.Instances.FirstOrDefault(i => i.InstanceName == name);
        if (target is null) return Refused($"{targetInstance} is not a part of this cell's netlist.");
        if (targetPin < 1 || targetPin > target.NetBindings.Count)
            return Refused($"{targetInstance} has no pin {targetPin}: it has {target.NetBindings.Count}.");
        if (line is null) return Refused($"The dropped {ComponentTypeRegistry.EngineReference(part.Symbol)} has no netlist line, so it cannot join a net.");
        if (partPin < 1 || partPin > line.NetBindings.Count)
            return Refused($"The dropped part has no pin {partPin}: it has {line.NetBindings.Count}.");

        // Ground is a reference, not a joint (D7). Any other pin on the net is a joint the drop would turn into a tee.
        string net = target.NetBindings[targetPin - 1];
        if (net != "0")
        {
            var joined = tb.Instances.Where(i => i.InstanceName != name)
                .SelectMany(i => i.NetBindings.Select((n, k) => (i.InstanceName, Pin: k + 1, Net: n)))
                .Where(p => p.Net == net && !(p.InstanceName == targetInstance && p.Pin == targetPin))
                .Select(p => $"{p.InstanceName}.{p.Pin}").ToList();
            if (joined.Count > 0)
                return Refused($"{targetInstance}.{targetPin} is already joined to {string.Join(", ", joined)}. A drop joins a "
                             + "free pin only: a third part on a joint would be a tee the schematic does not have.");
        }

        var bindings = line.NetBindings.ToArray();
        var quiet = bindings.Where((n, k) => k != partPin - 1 && n != "0").ToHashSet(StringComparer.Ordinal);
        bindings[partPin - 1] = net;
        tb.Instances[tb.Instances.IndexOf(line)] =
            new Instance(name, line.Reference, bindings, line.Overrides) { RefNetBinding = line.RefNetBinding };

        var merged = CellNetlistMerge.Merge(current, CnlWriter.Write(tb, extracted.Library, "extracted from cell"),
                                            new Dictionary<string, CellNetlistMerge.NewPart> { [name] = new(dropped, line) },
                                            quiet);
        return merged.Schematic is null
            ? Refused(string.Join(" ", merged.Refusals))
            : new PinDropResult(merged.Schematic, name, null);
    }

    private static PinDropResult Refused(string why) => new(null, null, why);
}

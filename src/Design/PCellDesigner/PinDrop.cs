// Owner: PCD-4 (brief-pcd-0 §5, seam 8; written by PCD-1's seams commit). Framework-free.

using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Layout.SchematicSync;
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

        // Ground is a reference, not a joint (D7). A pin joined to one other part is a joint: the drop makes it a tee
        // (brief-pcd-11 R-pcd11-3), or turns the tee already there into a cross.
        string net = target.NetBindings[targetPin - 1];
        string partNet = net;
        var newParts = new Dictionary<string, CellNetlistMerge.NewPart>(StringComparer.Ordinal);
        var quiet = new HashSet<string>(StringComparer.Ordinal);
        if (net != "0")
        {
            var joined = tb.Instances.Where(i => i.InstanceName != name)
                .SelectMany(i => i.NetBindings.Select((n, k) => (Inst: i, Pin: k + 1, Net: n)))
                .Where(p => p.Net == net && !(p.Inst.InstanceName == targetInstance && p.Pin == targetPin))
                .ToList();
            if (joined.Count > 1)
                return Refused($"{targetInstance}.{targetPin} is already a junction of {joined.Count + 1} pins "
                             + $"({string.Join(", ", joined.Select(j => $"{j.Inst.InstanceName}.{j.Pin}"))}): drop the part on a free pin.");
            if (joined.Count == 1)
            {
                if (Junction(withPart, tb, target, joined[0].Inst, net, line, newParts, quiet, out partNet) is { } refusal)
                    return Refused($"{targetInstance}.{targetPin} is joined to {joined[0].Inst.InstanceName}.{joined[0].Pin}, and {refusal}");
            }
        }

        var bindings = line.NetBindings.ToArray();
        foreach (var n in bindings.Where((n, k) => k != partPin - 1 && n != "0")) quiet.Add(n);
        bindings[partPin - 1] = partNet;
        // A port's second pin is its reference: on ground, as a placed port's is.
        if (PortPlacement.IsPort(dropped) && bindings.Length == 2 && partPin == 1) bindings[1] = "0";
        tb.Instances[tb.Instances.IndexOf(line)] =
            new Instance(name, line.Reference, bindings, line.Overrides) { RefNetBinding = line.RefNetBinding };
        newParts[name] = new(dropped, line);

        var merged = CellNetlistMerge.Merge(current, CnlWriter.Write(tb, extracted.Library, "extracted from cell"),
                                            newParts, quiet);
        return merged.Schematic is null
            ? Refused(string.Join(" ", merged.Refusals))
            : new PinDropResult(merged.Schematic, name, null);
    }

    /// <summary>
    /// brief-pcd-11 R-pcd11-3: the joint of <paramref name="target"/> and <paramref name="other"/> on <paramref name="net"/>
    /// becomes a junction with a free pin for the dropped part: an <c>MTEE</c> (the two parts on its through pins, their
    /// widths its <c>W1</c> and <c>W2</c>, the new part's its <c>W3</c>), or, when one of the two is already a tee, that
    /// tee turned into an <c>MCROSS</c>; <paramref name="free"/> is the junction's free net. Null when made;
    /// else why no junction fits.
    /// </summary>
    private static string? Junction(SchematicEditModel withPart, TestBench tb, Instance target, Instance other, string net,
                                    Instance part, Dictionary<string, CellNetlistMerge.NewPart> newParts, HashSet<string> quiet,
                                    out string free)
    {
        free = net;
        static ParameterAssignment? W(Instance i, string name = "W") => i.Overrides.FirstOrDefault(o => o.Name == name);
        var tee = target.Reference == "MTEE" ? target : other.Reference == "MTEE" ? other : null;
        var kind = tee is null ? SymbolKind.MTee : SymbolKind.MCross;
        var lines = tee is null ? new[] { target, other } : [tee];
        if (lines.FirstOrDefault(i => W(i, tee is null ? "W" : "W1") is null) is { } narrow)
            return $"{narrow.InstanceName} has no width, so no tee fits there: join the part to a free pin instead.";

        // The junction's own line comes from the extraction, substrate and all, as the dropped part's does.
        var placeholder = new EditableComponent
        {
            Symbol = kind,
            InstanceName = SchematicEditModel.NextAvailableName(withPart.Components, ComponentTypeRegistry.InstancePrefix(kind)),
        };
        foreach (var d in ComponentTypeRegistry.DefaultParameters(kind, kind == SymbolKind.MTee ? 3 : 4))
            placeholder.Parameters.Add(new EditableParameter { Name = d.Name, Expression = d.Expression, Unit = d.Unit, Dimension = d.Dimension });
        double right = withPart.Components.Select(c => c.X).DefaultIfEmpty(0).Max();
        (placeholder.X, placeholder.Y) = (Math.Ceiling((right + 5000) / NetlistSchematic.Grid) * NetlistSchematic.Grid, 0);
        withPart.Components.Add(placeholder);
        var junctionLine = SchematicCircuit.Extract(withPart, "cell").TestBench.Instances
                                           .FirstOrDefault(i => i.InstanceName == placeholder.InstanceName);
        withPart.Components.Remove(placeholder);
        if (junctionLine is null) return $"no {ComponentTypeRegistry.EngineReference(kind)} line could be made for the joint.";

        free = $"{placeholder.InstanceName}_{(tee is null ? 3 : 4)}";
        string[] nets;
        var widths = new List<ParameterAssignment>();
        var branch = W(part, "PinW[1]") ?? W(part) ?? W(target) ?? W(tee ?? target, "W1")!;   // drawn metal: its pin's width
        if (tee is null)
        {
            // The target keeps the joint's net on the tee's pin 1; the other part moves to pin 2.
            string through = $"{placeholder.InstanceName}_2";
            var moved = other.NetBindings.Select(n => n == net ? through : n).ToArray();
            tb.Instances[tb.Instances.IndexOf(other)] =
                new Instance(other.InstanceName, other.Reference, moved, other.Overrides) { RefNetBinding = other.RefNetBinding };
            nets = [net, through, free];
            widths.AddRange([Named("W1", W(target)!), Named("W2", W(other)!), Named("W3", branch)]);
            quiet.Add(through);
        }
        else
        {
            nets = [.. tee.NetBindings.Take(3), free];
            widths.AddRange(new[] { "W1", "W2", "W3" }.Select(n => W(tee, n)!).Append(Named("W4", branch)));
            tb.Instances.Remove(tee);
        }
        var overrides = junctionLine.Overrides.Where(o => !widths.Any(w => w.Name == o.Name)).Concat(widths);
        tb.Instances.Add(new Instance(placeholder.InstanceName, junctionLine.Reference, nets, overrides));
        newParts[placeholder.InstanceName] = new(placeholder, junctionLine);
        quiet.Add(free);
        return null;

        static ParameterAssignment Named(string name, ParameterAssignment w) => new(name, w.Expression, w.Unit);
    }

    private static PinDropResult Refused(string why) => new(null, null, why);
}

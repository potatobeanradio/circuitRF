// Framework-free. No Avalonia, no SkiaSharp.
//
// brief-icp-8 — an EM cell whose schematic places parts that are not copper: a footprint part, a kit device. Until now Path A
// refused them (SDL-6 R-sdl6-2): the EM result is only the copper, and putting a part back on it needs a component port at its
// pads or pins and its model across that port. The component-port builders (LCP for footprints, ICP for cell instances) make the
// ports; this makes the EM cell use them.
//
//   R-icp8-1 — the setup an evaluation runs is the EM view's own plus one entry per part that is not copper (ICP-0 D3's "*" set,
//              named one by one so a hand-placed part the schematic does not know is never swept in). A port that cannot be built
//              refuses the evaluation, naming the part and the builder's reason: a part left open is not the circuit drawn.
//   R-icp8-2 — the bench Path A runs puts each part back from the schematic being run (EmCoSimNetlist.Closures, the decision the
//              co-simulation netlist makes), with its overrides as written: they evaluate in the bench's own scope at every point,
//              so a parameter that moves no copper re-evaluates the netlist and solves nothing.
//
// The ports a result will have are planned once, at the schematic's own values, from the geometry an evaluation builds
// (EmEvaluation.Geometry) — the same path every point takes, so the N-port's terminals and the Touchstone's ports agree by
// construction. A point whose ports differ is refused by the provider's port count.

using CircuitRF.Core.Design;
using CircuitRF.Design.Layout.SchematicSync;
using CircuitRF.Design.Schematic;

namespace CircuitRF.Design.Layout.Em.ParametricEm;

public static class EmCellParts
{
    /// <summary>The prefix of every node a part's closure is wired on in a Path A bench, so none can meet a net of the schematic.</summary>
    public const string NodePrefix = "em_";

    /// <summary>
    /// R-icp8-1 — the instance names of <paramref name="model"/>'s parts that are not copper (<see cref="EmPartKinds"/>): the set
    /// <c>"*"</c> takes. Ports, grounds, VARs and parts marked Open or Short are not parts here, as the planner places none of them.
    /// </summary>
    public static IReadOnlyList<string> NotCopper(SchematicEditModel model, string? schematicDir) =>
        [.. model.Components
            .Where(c => SchematicLayoutPlanner.IsPhysical(c) && !PortPlacement.IsPort(c) && c.InstanceName.Length > 0
                        && EmPartKinds.Of(c, schematicDir) == EmPartKind.Part)
            .Select(c => c.InstanceName)];

    /// <summary>How many Ports <paramref name="target"/>'s schematic draws (0 when it cannot be read): a placed instance of the cell
    /// connects that many nets, a Path A bench with component ports more.</summary>
    public static int DrawnPorts(EmCellTarget target)
    {
        try
        {
            return SchematicPersistence.LoadFromFile(target.SchematicPath).model.Components
                .Count(c => PortPlacement.IsPort(c) && c.Disable is not (DisableState.Open or DisableState.Short));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
                                       or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            return 0;
        }
    }

    /// <summary>
    /// R-icp8-1 — <paramref name="setup"/> with a component-port entry for each part of <paramref name="model"/> that is not copper
    /// and that no entry of the setup already names (by its designator, or by <c>"*"</c>). The setup itself when nothing is added,
    /// so an all-copper cell's setup, and every key made from it, is exactly what it was.
    /// </summary>
    public static EmSetup RunSetup(EmSetup setup, SchematicEditModel model, string? schematicDir)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(model);
        var entries = setup.ComponentPorts.Where(e => e.Cell is not { Length: > 0 }).Select(e => e.Refdes.Trim()).ToList();
        if (entries.Contains("*")) return setup;
        var missing = NotCopper(model, schematicDir).Where(n => !entries.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
        if (missing.Count == 0) return setup;
        var run = setup.Clone();
        run.ComponentPorts = [.. setup.ComponentPorts, .. missing.Select(n => new EmComponentPort(n))];
        return run;
    }

    /// <summary>
    /// R-icp8-1 — why the parts of a planar build cannot all be put back on the EM result, or null: the build refused as a whole,
    /// or a part's port was not built. Each named with the builder's own sentence.
    /// </summary>
    public static string? Refusal(string cellName, EmComponentPorts.Result collected)
    {
        if (collected.Refusal is { } whole)
            return $"the parts of {cellName}'s schematic cannot be put back on its EM result: {whole}";
        var bad = collected.ComponentBuild?.Ports.Where(p => !p.Ok).Select(p => (p.Refdes, p.Name, Why: p.Refusal ?? "it was not built.")).ToList();
        return bad is { Count: > 0 } ? Sentence(cellName, bad) : null;
    }

    /// <summary>R-icp8-4 — <see cref="Refusal(string, EmComponentPorts.Result)"/> for a 3D view's build.</summary>
    public static string? Refusal(string cellName, CircuitRF.Design.ThreeD.C3dComponentPortBuild? build)
    {
        if (build is null) return null;
        if (build.Refusal is { } whole)
            return $"the parts of {cellName}'s schematic cannot be put back on its EM result: {whole}";
        var bad = build.Ports.Where(p => p.Refusal is not null)
                       .Select(p => (p.Refdes, Name: p.Pin is { Length: > 0 } pin ? $"{p.Refdes}.{pin}" : p.Refdes, Why: p.Refusal!)).ToList();
        return bad.Count > 0 ? Sentence(cellName, bad) : null;
    }

    private static string Sentence(string cellName, List<(string Refdes, string Name, string Why)> bad)
    {
        var parts = bad.Select(b => b.Refdes).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        string who = parts.Count == 1 ? parts[0] : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1];
        return $"{who} cannot be put back on the EM result of {cellName}'s layout, because {(parts.Count == 1 ? "its" : "their")} " +
               $"component port{(bad.Count == 1 ? "" : "s")} cannot be built: " +
               // The builder's sentence usually names its port already ("Q1.E: …"); it is not named twice.
               string.Join(" ", bad.Select(b => b.Why.TrimStart().StartsWith(b.Refdes, StringComparison.OrdinalIgnoreCase)
                                                ? b.Why.Trim() : $"{b.Name}: {b.Why.Trim()}")) +
               " Change the layout so the port can be built, or run this analysis by circuit models.";
    }

    /// <summary>
    /// R-icp8-2 — the parts the bench runs on <paramref name="ports"/>: each component port's part from <paramref name="models"/>
    /// (the schematic being run) as <see cref="EmCoSimNetlist.Closures"/> decides it, wired on <see cref="NodePrefix"/> nodes, and
    /// the terminals of the N-port in Touchstone order — a drawn port on the net of the schematic's Port of its number, a component
    /// port on its node. Null with <paramref name="refusal"/> when a port matches no Port, a Port no port, or a part has no model
    /// this wiring can read.
    /// </summary>
    public static (List<Instance> Parts, List<string> Terminals, List<string> Notes)? Close(
        IReadOnlyList<EmCoSimPort> ports, IReadOnlyDictionary<int, string> portNets, EmCoSimModels models, string cellName, out string? refusal)
    {
        refusal = null;
        string Node(int number) => NodePrefix + EmCoSimNetlist.Node(number);
        var terminals = new List<string>();
        foreach (var p in ports)
        {
            if (p.Component is { Connector: true } c)
            {
                refusal = $"{c.Refdes} is a connector: its port would be a Port of the circuit, and an EM cell's Ports are the schematic's.";
                return null;
            }
            if (p.Component is not null) { terminals.Add(Node(p.Number)); continue; }
            if (!portNets.TryGetValue(p.Num, out var net))
            {
                refusal = $"The EM result of {cellName}'s layout has a port {p.Num} ({p.Name}) that no Port of the schematic is numbered.";
                return null;
            }
            terminals.Add(net);
        }
        var drawn = ports.Where(p => p.Component is null).Select(p => p.Num).ToHashSet();
        if (portNets.Keys.FirstOrDefault(k => !drawn.Contains(k)) is var missing and > 0)
        {
            refusal = $"Port {missing} of the schematic is not a port of the EM result of {cellName}'s layout: its label is missing from the layout.";
            return null;
        }

        var parts = new List<Instance>();
        var notes = new List<string>();
        var placeholders = new List<string>();
        foreach (var close in EmCoSimNetlist.Closures(ports, models))
        {
            switch (close.Kind)
            {
                case EmCoSimCloseKind.Model:
                case EmCoSimCloseKind.Short:
                    var inst = close.Instance!;
                    parts.Add(new Instance(inst.InstanceName, inst.Reference, [.. inst.NetBindings.Select(Rename)], inst.Overrides)
                    { RefNetBinding = inst.RefNetBinding });
                    notes.AddRange(close.Warnings);
                    break;
                case EmCoSimCloseKind.Placeholder:
                    placeholders.Add(close.Comment ?? close.Refdes ?? close.Port.Name);
                    break;
                case EmCoSimCloseKind.Open:
                case EmCoSimCloseKind.PortLine:
                    break;
            }
        }
        if (placeholders.Count > 0)
        {
            refusal = $"A part of {cellName}'s schematic has no model that can be put back on its component port: " +
                      string.Join(" ", placeholders.Distinct(StringComparer.Ordinal));
            return null;
        }
        return (parts, terminals, notes.Distinct(StringComparer.Ordinal).ToList());

        static string Rename(string net) => net == "0" ? "0" : NodePrefix + net;
    }
}

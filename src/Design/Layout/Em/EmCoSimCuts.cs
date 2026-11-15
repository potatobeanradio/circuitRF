// brief-icp-5 — a cut instance's own model put back on its pins in the co-simulation netlist.
//
// A footprint part's model goes across its gap port by pin ORDER, because a footprint's pads and its model's terminals are
// numbered alike. A cut instance is different in four ways, and each is a rule here: it can be nested, so its model is in its
// parent cell's schematic with parameters bound in that parent's scope (D2: written as the values the design gives them, since
// carrying the parent's scope into a flat netlist would be string substitution); its pins are NAMED, so terminals map by name;
// some pins have no port (a reference pin, a grounded pin, a pin abutting another cut instance, a thermal terminal); and it may
// have no schematic counterpart at all (a PCell placed by hand), when the component its generator makes stands in for it.
//
// The decision is made once, here, and EmCoSimNetlist.Closures hands it to both the netlist and the cell drawn from it.

using System.Globalization;
using System.Numerics;
using System.Text;
using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Layout.Lvs;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Design.Schematic;

namespace CircuitRF.Design.Layout.Em;

/// <summary>One pin of a cut instance as the co-simulation netlist reads it.</summary>
/// <param name="Name">The pin's name, as a port names it.</param>
/// <param name="Number">The layout pin's number when its name is one.</param>
/// <param name="Class">Its class; null when the cut left it out (<paramref name="Why"/>).</param>
/// <param name="Abuts">For an abutting pin, <c>path.pin</c> of the pin it touches (or the instance's path when no pin was found).</param>
/// <param name="Why">Why the cut left it out; null when classified.</param>
public sealed record EmCoSimCutPin(string Name, int? Number, EmPinClass? Class, string? Abuts, string? Why)
{
    /// <summary>brief-icp-6 R-icp6-2 — the pin's face as ICP-1 projected it (layout metres); null when the projection had no pin.</summary>
    public EmPinFace? Face { get; init; }

    /// <summary>A floating pin that is a return by its entry (<c>Port: false</c>) with no port returning through it, not one nothing meets.</summary>
    public bool Unreturned { get; init; }

    /// <summary>A routed pin whose conductor reaches no other port and no ground (<see cref="EmCutPin.Unconnected"/>).</summary>
    public bool Unconnected { get; init; }
}

/// <summary>brief-icp-6 — a pin face: its middle (layout metres), width, outward direction (degrees) and drawing layer.</summary>
public sealed record EmPinFace(double X, double Y, double WidthM, double OutwardDeg, LayerKey Layer);

/// <summary>
/// brief-icp-5 R-icp5-1 — what the co-simulation netlist reads of one cut instance: its path, its cell, the schematic chain its
/// model is found by, and every pin with its class. One per instance, shared by its ports; both builders (Planar, 3D) carry it.
/// </summary>
/// <param name="Path">The instance's LVS path in its own layout.</param>
/// <param name="Cell">The cell folder's name.</param>
/// <param name="GeneratorId">The PCell generator a generated cell came from, or null.</param>
/// <param name="SchematicChain">The <c>SchematicId</c> of each placement from the top down (null where a level has none).</param>
/// <param name="Pins">Every pin, in its cell's pin order.</param>
public sealed record EmCoSimCut(string Path, string Cell, string? GeneratorId, IReadOnlyList<string?> SchematicChain,
                                IReadOnlyList<EmCoSimCutPin> Pins)
{
    /// <summary>A generated cell's parameters from its parent layout's snapshot (SI); null for any other cell.</summary>
    public IReadOnlyDictionary<string, PCellValue>? PCellParameters { get; init; }

    /// <summary>brief-icp-6 R-icp6-2 — how the entry chose it: <c>by "*"</c>, <c>by name</c>, <c>by "X1/*"</c> or <c>by its cell (Seg)</c>.</summary>
    public string Selected { get; init; } = "by name";

    /// <summary>brief-icp-6 R-icp6-2 — what left the problem with it: its own shapes (at its pins' faces), or the box and its size.</summary>
    public string LeftOut { get; init; } = "its own shapes";

    /// <summary>What a sentence calls its cell: the generator for a generated cell, else the folder.</summary>
    public string CellLabel => GeneratorId ?? Cell;

    /// <summary>Placed inside another cell: its model is in a sub-cell's schematic, which the top schematic does not contain.</summary>
    public bool Nested => Path.Contains('/');

    /// <summary>Each cut instance of <paramref name="cut"/>, by path.</summary>
    /// <param name="selections">The entries that chose each instance (<see cref="EmPartPads.Selection.Instances"/>), for how each was
    /// chosen and what left with it; null says neither.</param>
    public static IReadOnlyDictionary<string, EmCoSimCut> Of(EmCellCutoutResult cut, EmCellInstanceSet instances,
                                                             IReadOnlyList<EmCellSelection>? selections = null)
    {
        var map = new Dictionary<string, EmCoSimCut>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in cut.Pins.GroupBy(p => p.Path, StringComparer.OrdinalIgnoreCase))
        {
            var inst = instances.At(g.Key);
            var named = inst is null ? null : EmPinFingers.Named(inst.Pins);      // brief-icf-3 — a finger's pin is C[k] in the cut
            var pins = g.Where(p => p.Pin.Length > 0)
                        .Select(p =>
                        {
                            var ip = named?.FirstOrDefault(q => q.Name.Equals(p.Pin, StringComparison.OrdinalIgnoreCase));
                            return new EmCoSimCutPin(p.Pin, ip?.Number, p.Why is null ? p.Class : null, p.Abuts, p.Why)
                            {
                                Unreturned = p.Unreturned, Unconnected = p.Unconnected,
                                // brief-icp-10 — the face the cut built at (an area pin's is where the interconnect meets it), else
                                // the pin as projected.
                                Face = p.FaceAtM is { } m ? new EmPinFace(m.X, m.Y, p.WidthM, p.OutwardDeg, ip?.Layer ?? p.Layer)
                                     : ip is null ? null : new EmPinFace(ip.X, ip.Y, ip.WidthM, ip.OutwardDeg, ip.Layer),
                            };
                        })
                        .ToList();
            var selection = selections?.FirstOrDefault(c => c.Instance.Path.Equals(g.Key, StringComparison.OrdinalIgnoreCase));
            var entry = selection?.Entry;
            map[g.Key] = new EmCoSimCut(g.Key, inst?.Cell ?? g.First().Cell, inst?.GeneratorId, inst?.SchematicChain ?? [], pins)
            {
                PCellParameters = inst?.PCellParameters,
                // brief-icp-9 — the selection says; its entry's Refdes is the instance's own path by now, so it read "by name" always.
                Selected = selection?.Selected ?? "by name",
                LeftOut = entry?.PortAt != EmComponentPortAt.Cell ? "its own shapes"
                        : entry.KeepOutUm is { } k ? $"everything on the analysis levels inside its keep-out box, {Um((k.X1 - k.X0) * 1e-6)} × {Um((k.Y1 - k.Y0) * 1e-6)} µm"
                        : inst?.Box is { } b ? $"everything on the analysis levels inside its box, {Um(b.X1 - b.X0)} × {Um(b.Y1 - b.Y0)} µm"
                        : "everything on the analysis levels inside its box",
            };
        }
        return map;
    }

    private static string Um(double m) => CircuitRF.Design.ThreeD.C3dComponentPorts.Um(m);
}

internal static class EmCoSimCuts
{
    /// <summary>The model found for a cut instance: the instance to place, its terminals' names in net order, and what the
    /// netlist says about where it came from.</summary>
    private sealed record Found(Instance Model, IReadOnlyList<string> Terminals, string? Note);

    /// <summary>
    /// What closes one cut instance's ports: its model with each terminal on the node its pin of that name gives it, or a
    /// placeholder per port, naming why. <paramref name="mine"/> are its ports, in result order.
    /// </summary>
    public static IEnumerable<EmCoSimClose> Close(EmCoSimPart cp, IReadOnlyList<EmCoSimPort> mine, EmCoSimModels models, Names names)
    {
        var cut = cp.Cut!;
        string at = cp.Refdes;                      // the path as the run names it (a 3D view's layout prefix included)
        // brief-icf-2 R-icf2-1 — a cut instance with no port has none to anchor its close to: number 0 stands for "no port".
        var anchor = mine.Count > 0 ? mine[0] : new EmCoSimPort(0, at, "50", cp);
        string name = names.For(at);
        string label = name == at ? at : $"{name} is {at}";
        var warnings = new List<string>();

        var found = Model(cut, models, name, out string why);
        if (found is not null)
        {
            var terminals = found.Terminals;
            var pins = cut.Pins;
            bool Eq(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);
            // brief-icf-3 — a terminal takes the pin of its name, or that pin's fingers (C[1], C[2], …), the first standing for them.
            string Base(string n) => EmPinFingers.Base(n);
            var byTerminal = terminals.Select(t => pins.FirstOrDefault(p => Eq(p.Name, t)) ?? pins.FirstOrDefault(p => Eq(Base(p.Name), t))).ToList();
            // R-icp5-3 — a cell whose pins are Port numbers maps by number.
            if (byTerminal.All(p => p is null) && pins.Count > 0 && pins.All(p => Number(p) is not null))
                byTerminal = [.. terminals.Select((_, k) => pins.FirstOrDefault(p => Number(p) == k + 1))];
            List<EmCoSimCutPin> FingersOf(EmCoSimCutPin p)
                => Base(p.Name) == p.Name ? [p] : [.. pins.Where(q => Eq(Base(q.Name), Base(p.Name)))];
            var covered = byTerminal.OfType<EmCoSimCutPin>().SelectMany(FingersOf).ToHashSet();
            var unmatched = pins.Where(p => (mine.Any(q => Eq(q.Component!.Pin ?? "", p.Name)) || p.Class is EmPinClass.Abutting or EmPinClass.Grounded or EmPinClass.Return)
                                            && !covered.Contains(p)).ToList();
            if (byTerminal.All(p => p is null) || unmatched.Count > 0)
                why = $"its pins ({string.Join(", ", pins.Select(p => p.Name))}) do not match its model's terminals ({string.Join(", ", terminals)})";
            else
            {
                var notes = new List<string>();
                var joins = new List<EmCoSimClose>();
                EmCoSimPort? PortOf(EmCoSimCutPin p) => mine.FirstOrDefault(q => Eq(q.Component!.Pin ?? "", p.Name));

                // One pin's net for terminal k: its port's node, 0 for a reference or grounded pin, a shared node for an abutting one,
                // else an open node with a warning.
                string NetOf(EmCoSimCutPin? pin, int k, string bound)
                {
                    if (pin is null)
                    {
                        if (bound == "0") return "0";
                        string open = $"nc_{name}_{Legal(terminals[k])}";
                        warnings.Add($"{at}'s terminal {terminals[k]} has no pin of that name, so it is left open on {open}.");
                        return open;
                    }
                    var port = PortOf(pin);
                    var through = mine.Where(q => q.Component!.ReferencePin is { } rp && Eq(rp, pin.Name)).ToList();
                    if (port is not null) return EmCoSimNetlist.Node(port.Number);
                    if (through.Count > 0 || pin.Class == EmPinClass.Return)
                    {
                        // ICP-4 gate 4, here — the reference terminal on 0 is the N-port's reference, not ground in the chip.
                        notes.Add($"{at}.{pin.Name} is the reference of {(through.Count > 0 ? string.Join(" and ", through.Select(q => q.Name)) : "its ports")}: " +
                                  $"its terminal is on 0, tied to {EmCoSimNetlist.BoardInstance}'s reference, which is not the same as the pin being grounded in the chip.");
                        return "0";
                    }
                    if (pin.Class == EmPinClass.Grounded) return "0";
                    if (pin.Class == EmPinClass.Abutting && pin.Abuts is { } other && other.Contains('.'))
                    {
                        string self = $"{cut.Path}.{pin.Name}";
                        string first = string.CompareOrdinal(self, other) <= 0 ? self : other;
                        return "j_" + Legal(first.Replace('.', '_'));
                    }
                    if (bound == "0") return "0";
                    string nc = $"nc_{name}_{Legal(pin.Name)}";
                    warnings.Add($"{at}.{pin.Name} has no port{(pin.Why is not null ? " (it was left out of the run)" : "")}, so its model's terminal is " +
                                 $"left open on {nc}{(pin.Class == EmPinClass.Abutting ? ": the pin it touches was not found" : "")}.");
                    return nc;
                }
                string Bound(int k) => k < found.Model.NetBindings.Count ? found.Model.NetBindings[k] : "";

                // brief-icf-3 R-icf3-4 — Split: a model that declares its finger count, with every multi-face pin at the same k faces,
                // is written as k one-finger instances, each across its own finger's ports.
                var fingered = byTerminal.OfType<EmCoSimCutPin>().Select(FingersOf).Where(f => f.Count > 1).ToList();
                string? fingerParameter = FingerParameter(found.Model.Reference);
                int k0 = fingered.Count > 0 ? fingered[0].Count : 0;
                if (fingered.Count > 0 && fingerParameter is not null && fingered.All(f => f.Count == k0))
                {
                    var comment = new StringBuilder($"{label}: {cut.CellLabel} cut out of the layout, its {k0} fingers written as {k0} one-finger " +
                                                    $"instances ({fingerParameter}=1 each), each model's terminals on its own finger's pins by name.");
                    if (found.Note is not null) comment.Append(' ').Append(found.Note);
                    var placedAll = new List<Instance>();
                    for (int f = 0; f < k0; f++)
                    {
                        var nets = terminals.Select((_, k) => byTerminal[k] is { } p && FingersOf(p) is { Count: > 1 } fs ? NetOf(fs[f], k, Bound(k)) : NetOf(byTerminal[k], k, Bound(k))).ToList();
                        var overrides = found.Model.Overrides.Where(o => !Eq(o.Name, fingerParameter)).Append(new ParameterAssignment(fingerParameter, "1")).ToList();
                        placedAll.Add(new Instance($"{name}_f{f + 1}", found.Model.Reference, nets, overrides));
                    }
                    foreach (var n in notes.Distinct()) comment.Append(' ').Append(n);
                    foreach (var w in warnings.Distinct()) comment.Append(' ').Append(w);
                    yield return new EmCoSimClose(EmCoSimCloseKind.Model, anchor, at, placedAll[0], Comment: comment.ToString(),
                                                  SchematicName: cut.SchematicChain.Count == 1 ? cut.SchematicChain[0] : null) { Warnings = [.. warnings.Distinct()] };
                    foreach (var extra in placedAll.Skip(1))
                        yield return new EmCoSimClose(EmCoSimCloseKind.Model, anchor, at, extra);
                    yield break;
                }

                // brief-icf-3 R-icf3-3 — Tie: a pin's fingers join on the one terminal: the first finger with a port gives the node, every
                // other finger port is shorted to it.
                var tied = new List<string>();
                var netsTie = new List<string>();
                for (int k = 0; k < terminals.Count; k++)
                {
                    var pin = byTerminal[k];
                    var fingers = pin is null ? [] : FingersOf(pin);
                    var ported = fingers.Where(f => PortOf(f) is not null).ToList();
                    string net = NetOf(ported.Count > 0 ? ported[0] : pin, k, Bound(k));
                    netsTie.Add(net);
                    foreach (var f in ported.Skip(1))
                    {
                        var q = PortOf(f)!;
                        joins.Add(new EmCoSimClose(EmCoSimCloseKind.Short, q, at, new Instance($"{name}_{Legal(f.Name)}_tie", "Short", [EmCoSimNetlist.Node(q.Number), net], []),
                            Comment: $"{at}.{f.Name} is a finger of {Base(f.Name)}: its port {EmCoSimNetlist.Node(q.Number)} is joined to {net}, the model's terminal {terminals[k]}."));
                    }
                    if (fingers.Count > 1) tied.Add($"{Base(fingers[0].Name)} ({fingers.Count} faces)");
                }
                var placed = new Instance(name, found.Model.Reference, netsTie, found.Model.Overrides);
                var commentTie = new StringBuilder($"{label}: {cut.CellLabel} cut out of the layout, its model's terminals on its pins by name.");
                if (tied.Count > 0)
                    commentTie.Append($" Its fingers are joined on one terminal each ({string.Join(", ", tied)}), so phase differences between fingers " +
                                      "inside the device are not modelled" + (fingered.Count > 0 && fingerParameter is not null
                                          ? "; they cannot be written one instance per finger, since its pins are at different numbers of faces."
                                          : "."));
                if (found.Note is not null) commentTie.Append(' ').Append(found.Note);
                foreach (var n in notes) commentTie.Append(' ').Append(n);
                foreach (var w in warnings) commentTie.Append(' ').Append(w);
                if (mine.Count == 0)
                    commentTie.Append($" It has no port, so it reaches {EmCoSimNetlist.BoardInstance} only through ground.");
                yield return new EmCoSimClose(EmCoSimCloseKind.Model, anchor, at, placed, Comment: commentTie.ToString(),
                                              SchematicName: cut.SchematicChain.Count == 1 ? cut.SchematicChain[0] : null)
                { Warnings = warnings };
                foreach (var j in joins) yield return j;
                yield break;
            }
        }

        // brief-icf-2 R-icf2-2 — no port and no model: nothing to stand across, so nothing is placed, and the netlist says so.
        if (mine.Count == 0)
        {
            string none = $"{label}: {cut.CellLabel} is cut out of the layout with no port, and {why}, so nothing is placed for it.";
            yield return new EmCoSimClose(EmCoSimCloseKind.Open, anchor, at, Comment: none) { Warnings = [none] };
            yield break;
        }

        // R-icp5-2 step 3 — a placeholder per port, named in a comment with why (LCP-7's).
        foreach (var q in mine)
        {
            var (kind, pkey, value, unit) = EmCoSimNetlist.Placeholder(cut.Cell);
            string pn = mine.Count == 1 ? name : $"{name}_{Legal(q.Component!.Pin ?? q.Number.ToString(CultureInfo.InvariantCulture))}";
            string variable = pn + "_value";
            string sentence = $"{(pn == q.Name ? pn : $"{pn} ({q.Name})")}: {why}, so {variable} is a placeholder; set it to the part's value.";
            yield return new EmCoSimClose(EmCoSimCloseKind.Placeholder, q, at,
                new Instance(pn, kind, [EmCoSimNetlist.Node(q.Number), "0"], [new ParameterAssignment(pkey, variable)]),
                new Variable(variable, value, unit), sentence) { Warnings = [sentence] };
        }
    }

    /// <summary>brief-icf-3 R-icf3-4 — the parameter a model counts its fingers by, where it declares one: the composed FET's N, "the
    /// number of gate fingers". Null for every other model (a kit part declares none), which is then written with its fingers tied.</summary>
    internal static string? FingerParameter(string reference)
        => reference.Equals(CircuitRF.Core.Devices.ComponentModelFactory.ComposedFetTypeName, StringComparison.OrdinalIgnoreCase)
        || reference.Equals(CircuitRF.Core.Devices.ComponentModelFactory.ComposedCoreTypeName, StringComparison.OrdinalIgnoreCase) ? "N" : null;

    private static int? Number(EmCoSimCutPin p)
        => p.Number ?? (int.TryParse(p.Name, NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : null);

    // ── Which model (R-icp5-2) ─────────────────────────────────────────────────────────────────────

    private static Found? Model(EmCoSimCut cut, EmCoSimModels models, string name, out string why)
    {
        why = "the layout's schematic gives no model for it";
        var chain = cut.SchematicChain;
        if (chain.Count > 0 && chain.All(s => s is { Length: > 0 }))
        {
            if (Counterpart(cut, models, out why) is { } found) return found;
        }
        else if (chain.Count > 0 && chain.Any(s => s is { Length: > 0 }))
            why = $"a level above it in the layout is not linked to the schematic, so its schematic counterpart cannot be followed";

        // Step 2 — a PCell with no schematic counterpart: the component its generator makes, one to one.
        if (cut.GeneratorId is { } generator && cut.PCellParameters is { } parameters && DeviceTypes.TryGetSymbolKind(generator, out var kind))
        {
            int pins = cut.Pins.Count;
            var declared = ComponentTypeRegistry.DefaultParameters(kind, pins).Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
            var given = parameters.Where(p => declared.Contains(p.Key) && p.Value.Kind is PCellValueKind.Real or PCellValueKind.Int)
                                  .OrderBy(p => p.Key, StringComparer.Ordinal)
                                  .Select(p => new ParameterAssignment(p.Key, Num(p.Value.AsReal())))
                                  .ToList();
            var model = new Instance(name, ComponentTypeRegistry.EngineReference(kind, pins), [.. Enumerable.Repeat("", pins)], given);
            return new Found(model, [.. Enumerable.Range(1, pins).Select(k => k.ToString(CultureInfo.InvariantCulture))],
                             $"The model came from the cell: {generator}'s component at the generator's values ({string.Join(", ", given.Select(g => g.Name))}).");
        }
        if (cut.GeneratorId is { } g2 && !DeviceTypes.TryGetSymbolKind(g2, out _))
            why += $"; its generator {g2} makes no built-in component";
        return null;
    }

    /// <summary>Step 1 — the schematic counterpart: the top schematic's instance, or down the chain through each sub-cell's own.</summary>
    private static Found? Counterpart(EmCoSimCut cut, EmCoSimModels models, out string why)
    {
        var chain = cut.SchematicChain;
        why = "";
        if (!models.Instances.TryGetValue(chain[0]!, out var top))
        {
            why = $"the layout's schematic has no {chain[0]}";
            return null;
        }
        if (chain.Count == 1) return new Found(top, Terminals(top, models, topLevel: true), null);   // D1: overrides as written

        var levels = new List<(Instance Inst, Cell Cell)>();
        var current = top;
        for (int k = 1; k < chain.Count; k++)
        {
            if (models.Library?.Find(current.Reference) is not { } cell)
            {
                why = $"{current.InstanceName} in the schematic is not a cell with a schematic of its own, so {chain[k]} inside it cannot be found";
                return null;
            }
            levels.Add((current, cell));
            var next = cell.Instances.FirstOrDefault(i => i.InstanceName.Equals(chain[k], StringComparison.OrdinalIgnoreCase));
            if (next is null)
            {
                why = $"cell {cell.Name}'s schematic has no {chain[k]}";
                return null;
            }
            current = next;
        }
        if (Resolved(current, levels, models, out var written, out why) is not { } resolved) return null;
        string where = string.Join("/", chain);
        string note = written.Count == 0 ? $"Its schematic counterpart is {where}."
            : $"Its schematic counterpart is {where}, and {string.Join(", ", written)} {(written.Count == 1 ? "is" : "are")} written as the " +
              $"value{(written.Count == 1 ? "" : "s")} the design gives {(written.Count == 1 ? "it" : "them")} there: a VAR that drove " +
              $"{(written.Count == 1 ? "it" : "them")} no longer tunes {(written.Count == 1 ? "it" : "them")} here.";
        return new Found(resolved, Terminals(current, models, topLevel: false), note);
    }

    /// <summary>
    /// D2 — a nested instance with each parameter written as the value it has in the design: every level's overrides evaluated in
    /// its parent's scope and bound in the cell's, as elaboration binds them, by the one expression engine. Null with the reason when
    /// one does not evaluate.
    /// </summary>
    private static Instance? Resolved(Instance target, IReadOnlyList<(Instance Inst, Cell Cell)> levels, EmCoSimModels models,
                                      out List<string> written, out string why)
    {
        written = [];
        why = "";
        var evaluator = new Evaluator();
        var scope = new Scope("global");
        foreach (var v in models.Variables) scope.Bind(v.Name, v.Expression, v.Unit);
        string path = "";
        try
        {
            foreach (var (inst, cell) in levels)
            {
                path = path.Length == 0 ? inst.InstanceName : $"{path}.{inst.InstanceName}";
                var child = new Scope(path, scope) { InstancePath = path };
                foreach (var pd in cell.Parameters) child.Bind(pd.Name, pd.DefaultExpression, pd.Unit);
                foreach (var v in cell.Variables) child.Bind(v.Name, v.Expression, v.Unit);
                foreach (var ov in inst.Overrides)
                {
                    if (Verbatim(ov.Expression)) continue;
                    var value = evaluator.EvalParameter(ov.Expression, scope, ov.Unit, $"{inst.InstanceName}.{ov.Name}");
                    child.Bind(ov.Name, "__resolved__");
                    evaluator.InjectResolved(path, ov.Name, value);
                }
                scope = child;
            }
            var overrides = new List<ParameterAssignment>();
            foreach (var ov in target.Overrides)
            {
                if (Verbatim(ov.Expression) || double.TryParse(ov.Expression.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                {
                    overrides.Add(ov);
                    continue;
                }
                var value = evaluator.EvalParameter(ov.Expression, scope, ov.Unit, $"{target.InstanceName}.{ov.Name}");
                if (Text(value) is not { } text) { overrides.Add(ov); continue; }
                overrides.Add(new ParameterAssignment(ov.Name, text));
                written.Add(ov.Name);
            }
            return new Instance(target.InstanceName, target.Reference, target.NetBindings, overrides);
        }
        catch (ExpressionException e)
        {
            why = $"its parameters could not be read from the design at {string.Join("/", levels.Select(l => l.Inst.InstanceName).Append(target.InstanceName))}: {e.Message}";
            return null;
        }

        // A quoted word or a path is a value as written, never an expression (Elaborator's string-parameter rule).
        static bool Verbatim(string e) => e.TrimStart() is { Length: > 0 } t && (t[0] == '"' || t[0] == '/' || t[0] == '\\');
    }

    private static string? Text(Value v) => v.Kind switch
    {
        ValueKind.Real => Num(v.AsReal()),
        ValueKind.Complex => v.AsComplex() is var c && c.Imaginary == 0 ? Num(c.Real)
                           : $"({Num(c.Real)}{(c.Imaginary < 0 ? "-" : "+")}{Num(Math.Abs(c.Imaginary))}*j)",
        _ => null,
    };

    private static string Num(double x) => x.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>A model's terminal names in net order: a sub-cell's ports, the symbol's pin names, else their numbers.</summary>
    private static IReadOnlyList<string> Terminals(Instance model, EmCoSimModels models, bool topLevel)
    {
        int n = model.NetBindings.Count;
        if (models.Library?.Find(model.Reference) is { } cell && cell.Ports.Count == n) return cell.Ports;
        if (topLevel && models.PinNames.TryGetValue(model.InstanceName, out var named) && named.Count == n && named.All(p => p.Length > 0))
            return named;
        return [.. Enumerable.Range(1, n).Select(k => k.ToString(CultureInfo.InvariantCulture))];
    }

    // ── Names (R-icp5-4) ───────────────────────────────────────────────────────────────────────────

    /// <summary>A path as an instance or node name the <c>.cnl</c> reader accepts: letters, digits and underscores, starting with a
    /// letter (<c>X1/Q3</c> → <c>X1_Q3</c>, <c>@2</c> → <c>at2</c>).</summary>
    public static string Legal(string path)
    {
        var sb = new StringBuilder(path.Length + 2);
        foreach (char ch in path.Trim())
            sb.Append(ch == '@' ? "at" : char.IsAsciiLetterOrDigit(ch) || ch == '_' ? ch.ToString() : "_");
        if (sb.Length == 0 || !char.IsAsciiLetter(sb[0])) sb.Insert(0, 'X');
        return sb.ToString();
    }

    /// <summary>Each cut instance's name in one netlist: <see cref="Legal"/>, made unique against the board block, the Port lines
    /// (<c>P1</c>…) and every other name it gave.</summary>
    public sealed class Names
    {
        private readonly Dictionary<string, string> _byPath = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _used = new(StringComparer.OrdinalIgnoreCase) { EmCoSimNetlist.BoardInstance };

        public Names(IEnumerable<string> taken)
        {
            foreach (var t in taken) _used.Add(t);
        }

        public string For(string path)
        {
            if (_byPath.TryGetValue(path, out var known)) return known;
            string baseName = Legal(path), name = baseName;
            for (int k = 2; _used.Contains(name) || IsPortLine(name); k++) name = $"{baseName}_{k}";
            _used.Add(name);
            _byPath[path] = name;
            return name;
        }

        private static bool IsPortLine(string n) => n.Length > 1 && (n[0] is 'P' or 'p') && n.Skip(1).All(char.IsAsciiDigit);
    }
}

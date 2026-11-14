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
            var pins = g.Where(p => p.Pin.Length > 0)
                        .Select(p =>
                        {
                            var ip = inst?.Pins.FirstOrDefault(q => q.Name.Equals(p.Pin, StringComparison.OrdinalIgnoreCase));
                            return new EmCoSimCutPin(p.Pin, ip?.Number, p.Why is null ? p.Class : null, p.Abuts, p.Why)
                            {
                                Face = ip is null ? null : new EmPinFace(ip.X, ip.Y, ip.WidthM, ip.OutwardDeg, ip.Layer),
                            };
                        })
                        .ToList();
            var entry = selections?.FirstOrDefault(c => c.Instance.Path.Equals(g.Key, StringComparison.OrdinalIgnoreCase))?.Entry;
            map[g.Key] = new EmCoSimCut(g.Key, inst?.Cell ?? g.First().Cell, inst?.GeneratorId, inst?.SchematicChain ?? [], pins)
            {
                PCellParameters = inst?.PCellParameters,
                Selected = entry is null ? "by name"
                         : entry.Cell is { Length: > 0 } cell ? $"by its cell ({cell})"
                         : entry.Refdes.Trim() == "*" || entry.Refdes.TrimEnd().EndsWith("/*", StringComparison.Ordinal) ? $"by \"{entry.Refdes.Trim()}\""
                         : "by name",
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
        string name = names.For(at);
        string label = name == at ? at : $"{name} is {at}";
        var warnings = new List<string>();

        var found = Model(cut, models, name, out string why);
        if (found is not null)
        {
            var terminals = found.Terminals;
            var pins = cut.Pins;
            bool Eq(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);
            var byTerminal = terminals.Select(t => pins.FirstOrDefault(p => Eq(p.Name, t))).ToList();
            // R-icp5-3 — a cell whose pins are Port numbers maps by number.
            if (byTerminal.All(p => p is null) && pins.Count > 0 && pins.All(p => Number(p) is not null))
                byTerminal = [.. terminals.Select((_, k) => pins.FirstOrDefault(p => Number(p) == k + 1))];
            var unmatched = pins.Where(p => (mine.Any(q => Eq(q.Component!.Pin ?? "", p.Name)) || p.Class is EmPinClass.Abutting or EmPinClass.Grounded or EmPinClass.Return)
                                            && !byTerminal.Contains(p)).ToList();
            if (byTerminal.All(p => p is null) || unmatched.Count > 0)
                why = $"its pins ({string.Join(", ", pins.Select(p => p.Name))}) do not match its model's terminals ({string.Join(", ", terminals)})";
            else
            {
                var nets = new List<string>();
                var notes = new List<string>();
                for (int k = 0; k < terminals.Count; k++)
                {
                    var pin = byTerminal[k];
                    string bound = k < found.Model.NetBindings.Count ? found.Model.NetBindings[k] : "";
                    if (pin is null)
                    {
                        if (bound == "0") { nets.Add("0"); continue; }
                        string nc = $"nc_{name}_{Legal(terminals[k])}";
                        nets.Add(nc);
                        warnings.Add($"{at}'s terminal {terminals[k]} has no pin of that name, so it is left open on {nc}.");
                        continue;
                    }
                    var port = mine.FirstOrDefault(q => Eq(q.Component!.Pin ?? "", pin.Name));
                    var through = mine.Where(q => q.Component!.ReferencePin is { } rp && Eq(rp, pin.Name)).ToList();
                    if (port is not null) nets.Add(EmCoSimNetlist.Node(port.Number));
                    else if (through.Count > 0 || pin.Class == EmPinClass.Return)
                    {
                        nets.Add("0");
                        // ICP-4 gate 4, here — the reference terminal on 0 is the N-port's reference, not ground in the chip.
                        notes.Add($"{at}.{pin.Name} is the reference of {(through.Count > 0 ? string.Join(" and ", through.Select(q => q.Name)) : "its ports")}: " +
                                  $"its terminal is on 0, tied to {EmCoSimNetlist.BoardInstance}'s reference, which is not the same as the pin being grounded in the chip.");
                    }
                    else if (pin.Class == EmPinClass.Grounded) nets.Add("0");
                    else if (pin.Class == EmPinClass.Abutting && pin.Abuts is { } other && other.Contains('.'))
                    {
                        string self = $"{cut.Path}.{pin.Name}";
                        string first = string.CompareOrdinal(self, other) <= 0 ? self : other;
                        nets.Add("j_" + Legal(first.Replace('.', '_')));
                    }
                    else if (bound == "0") nets.Add("0");
                    else
                    {
                        string nc = $"nc_{name}_{Legal(pin.Name)}";
                        nets.Add(nc);
                        warnings.Add($"{at}.{pin.Name} has no port{(pin.Why is not null ? " (it was left out of the run)" : "")}, so its model's terminal is " +
                                     $"left open on {nc}{(pin.Class == EmPinClass.Abutting ? ": the pin it touches was not found" : "")}.");
                    }
                }
                var placed = new Instance(name, found.Model.Reference, nets, found.Model.Overrides);
                var comment = new StringBuilder($"{label}: {cut.Cell} cut out of the layout, its model's terminals on its pins by name.");
                if (found.Note is not null) comment.Append(' ').Append(found.Note);
                foreach (var n in notes) comment.Append(' ').Append(n);
                foreach (var w in warnings) comment.Append(' ').Append(w);
                yield return new EmCoSimClose(EmCoSimCloseKind.Model, mine[0], at, placed, Comment: comment.ToString(),
                                              SchematicName: cut.SchematicChain.Count == 1 ? cut.SchematicChain[0] : null)
                { Warnings = warnings };
                yield break;
            }
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

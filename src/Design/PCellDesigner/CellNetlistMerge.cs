// Owner: PCD-4 (brief-pcd-0 §5, seam 8; written by PCD-1's seams commit). Framework-free.

using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Schematic;

namespace CircuitRF.Design.PCellDesigner;

public sealed record CellNetlistMergeResult(SchematicEditModel? Schematic, IReadOnlyList<string> Refusals,
                                            IReadOnlyList<string> Notes);

/// <summary>brief-pcd-0 R-pcd4-1: the designer's text view merged back into its schematic.</summary>
/// <remarks>brief-pcd-4. The text is read by <see cref="CnlReader"/> and drawn by <see cref="NetlistSchematic.Build"/>,
/// the code behind <c>netlist --to-schematic</c>; the baseline is the schematic's own extraction, read the same way
/// (D1). Equal nets keep the wires; changed nets redraw them around the kept parts, pinned where they are (D4).</remarks>
public static class CellNetlistMerge
{
    private const string Bench = "cell";
    private const string Ground = "0";

    /// <summary>cnlText merged into a copy of current by designator (D-drawing). Never mutates current. Schematic null
    /// when refused.</summary>
    public static CellNetlistMergeResult Merge(SchematicEditModel current, string cnlText)
        => Merge(current, cnlText, null, null);

    /// <summary>A part new in the text that starts from a component (a pin drop's palette part) rather than from its
    /// line: its non-netlist fields and parameters are kept as a kept part's are, Extracted being its baseline line.</summary>
    internal sealed record NewPart(EditableComponent Component, Instance Extracted);

    /// <param name="newParts">By designator, the parts new in the text that start from a component.</param>
    /// <param name="quiet">Nets new in the text drawn without a label (a pin drop's own open pins).</param>
    internal static CellNetlistMergeResult Merge(SchematicEditModel current, string cnlText,
                                                 IReadOnlyDictionary<string, NewPart>? newParts,
                                                 IReadOnlySet<string>? quiet)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(cnlText);

        string baselineText = SchematicCircuit.CnlTextOf(current, Bench);
        if (Read(cnlText, current.SchematicDirectory) is not { } read) return Refused(ReadRefusal(cnlText, current));
        var (lib, tb) = read;
        var before = new CnlReader().Read(baselineText, Bench, current.SchematicDirectory).TestBench;
        var was = before.Instances.ToDictionary(i => i.InstanceName, StringComparer.Ordinal);

        var refusals = tb.Instances.GroupBy(i => i.InstanceName, StringComparer.Ordinal).Where(g => g.Count() > 1)
                                   .Select(g => $"{g.Key} is stated {g.Count()} times; a designator names one part.").ToList();
        foreach (var i in tb.Instances)
            if (was.TryGetValue(i.InstanceName, out var w) && !w.Reference.Equals(i.Reference, StringComparison.OrdinalIgnoreCase))
                refusals.Add($"{i.InstanceName}: its type would change from {w.Reference} to {i.Reference}. A part keeps its "
                           + "type; give the new part a designator of its own and remove the old one.");
        if (refusals.Count > 0) return Refused(refusals);

        var byName = current.Components.GroupBy(c => c.InstanceName, StringComparer.Ordinal)
                                       .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var kept = tb.Instances.Where(i => was.ContainsKey(i.InstanceName) && byName.ContainsKey(i.InstanceName))
                               .Select(i => byName[i.InstanceName]).ToList();

        // D5: a net the baseline had and no label names was never named by a person; it stays unlabelled.
        var labelled = current.NetLabels.Select(l => l.Name).ToHashSet(StringComparer.Ordinal);
        var quietNets = before.Instances.SelectMany(i => i.NetBindings).Where(n => n != Ground && !labelled.Contains(n))
                              .Concat(quiet ?? new HashSet<string>()).ToHashSet(StringComparer.Ordinal);

        // A cell instance draws as its cell's symbol: the pins come from the symbol the schematic already resolves.
        var cells = new Dictionary<string, ExactCell>(StringComparer.Ordinal);
        var defined = new Dictionary<string, ExactCell>(StringComparer.Ordinal);
        foreach (var c in kept.Concat(newParts?.Values.Select(n => n.Component) ?? []))
        {
            if (c.CellRef is null || CellPins(c, current) is not { } pins) continue;
            var cell = new ExactCell(c.CellRef, pins);
            cells[c.InstanceName] = cell;
            if (tb.Instances.FirstOrDefault(i => i.InstanceName == c.InstanceName) is { } inst && lib.Find(inst.Reference) is not null)
                defined[inst.Reference] = cell;
        }

        var exact = new ExactPlacement
        {
            Instances = kept.ToDictionary(c => c.InstanceName, c => new ExactInstance(c.X, c.Y, c.Rotation, c.MirrorX),
                                          StringComparer.Ordinal),
            Cells = cells, QuietNets = quietNets, AutoRoute = true,
        };
        var drawn = NetlistSchematic.Build(lib, tb, current.SchematicDirectory, exact: exact, existingCells: defined);
        if (drawn.Schematic is not { } drawing) return new CellNetlistMergeResult(null, drawn.Refusals, drawn.Notes);

        var notes = new List<string>(drawn.Notes);
        var model = Copy(current);
        var stated = tb.Instances.ToDictionary(i => i.InstanceName, StringComparer.Ordinal);
        var drawnPart = drawing.Components.Where(c => stated.ContainsKey(c.InstanceName))
                                          .ToDictionary(c => c.InstanceName, StringComparer.Ordinal);

        if (SameNets(before, tb))
        {
            foreach (var c in model.Components)
                if (stated.TryGetValue(c.InstanceName, out var inst) && was.TryGetValue(c.InstanceName, out var w))
                    MergeParameters(c, drawnPart[c.InstanceName], w, inst);
        }
        else
        {
            // D4: the drawing's wiring; each kept part the schematic's own, where the drawing put it.
            var mine = model.Components.GroupBy(c => c.InstanceName, StringComparer.Ordinal)
                                       .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            var parts = new List<EditableComponent>();
            foreach (var d in drawing.Components.Where(c => stated.ContainsKey(c.InstanceName)))
            {
                EditableComponent c;
                if (was.TryGetValue(d.InstanceName, out var w) && mine.TryGetValue(d.InstanceName, out var own))
                {
                    c = own;
                    MergeParameters(c, d, w, stated[d.InstanceName]);
                }
                else if (newParts?.GetValueOrDefault(d.InstanceName) is { } template)
                {
                    c = template.Component.Clone();
                    c.InstanceName = d.InstanceName;
                    MergeParameters(c, d, template.Extracted, stated[d.InstanceName]);
                }
                else c = d;
                (c.X, c.Y, c.Rotation, c.MirrorX) = (d.X, d.Y, d.Rotation, d.MirrorX);
                c.DetachedPorts.Clear();   // the drawing is the text's connectivity
                parts.Add(c);
            }
            var others = model.Components.Where(c => !was.ContainsKey(c.InstanceName) && c.Symbol != SymbolKind.Ground).ToList();
            model.Components.Clear();
            model.Components.AddRange(others);
            model.Components.AddRange(parts);
            model.Components.AddRange(drawing.Components.Where(c => c.Symbol == SymbolKind.Ground));
            model.Wires.Clear();     model.Wires.AddRange(drawing.Wires);
            model.NetLabels.Clear(); model.NetLabels.AddRange(drawing.NetLabels);
            model.Dots.Clear();      model.Dots.AddRange(drawing.Dots);
            foreach (string f in drawn.PlacementFallbacks)
                notes.Add($"{f} could not stay where it was without its pins landing on another part's, and was moved.");
        }

        // D6: variables and measurements by row name.
        MergeRows(model, drawing, SymbolKind.Var, before.GlobalVariables.Select(v => v.Name));
        MergeRows(model, drawing, SymbolKind.Meas, before.Measurements.Select(m => m.Name));
        if (!AnalysisLines(cnlText).SequenceEqual(AnalysisLines(baselineText)))
            notes.Add("Analysis lines are not merged from the text; edit the analyses in the schematic.");

        return new CellNetlistMergeResult(model, [], notes);
    }

    /// <summary>The text as the netlist reader reads it, or null when it does not read cleanly.</summary>
    private static (Library Lib, TestBench Tb)? Read(string text, string? dir)
    {
        try
        {
            var r = new CnlReader().Read(text, Bench, dir);
            return r.TestBench.ReadWarnings.Count == 0 ? r : null;
        }
        catch (CnlReadException) { return null; }
    }

    private static IReadOnlyList<string> ReadRefusal(string text, SchematicEditModel current)
    {
        try { return [.. new CnlReader().Read(text, Bench, current.SchematicDirectory).TestBench.ReadWarnings]; }
        catch (CnlReadException e) { return [e.Message]; }
    }

    private static CellNetlistMergeResult Refused(IReadOnlyList<string> why) => new(null, why, []);

    /// <summary>A deep copy, as the file holds it: the designer's model is the user's.</summary>
    internal static SchematicEditModel Copy(SchematicEditModel m)
    {
        var copy = SchematicPersistence.Deserialize(SchematicPersistence.Serialize(m), m.SchematicDirectory).model;
        copy.SchematicDirectory = m.SchematicDirectory;
        copy.HostWorkspaceRoot = m.HostWorkspaceRoot;
        return copy;
    }

    /// <summary>D2: the same designators, each with the same net on each pin, by name.</summary>
    private static bool SameNets(TestBench a, TestBench b)
    {
        static Dictionary<string, string> Of(TestBench t) => t.Instances.ToDictionary(
            i => i.InstanceName, i => string.Join("\u0001", i.NetBindings) + "\u0002" + i.RefNetBinding, StringComparer.Ordinal);
        var x = Of(a);
        var y = Of(b);
        return x.Count == y.Count && x.All(kv => y.TryGetValue(kv.Key, out var v) && v == kv.Value);
    }

    /// <summary>D3: a parameter the text states differently from the baseline replaces the schematic's expression and
    /// unit, in the drawing's spelling; one the baseline stated and the text no longer does goes back to its default;
    /// everything else (a value the extraction adds, a symbol-only setting) stays as the schematic has it.</summary>
    private static void MergeParameters(EditableComponent c, EditableComponent drawn, Instance was, Instance now)
    {
        var before = new Dictionary<string, ParameterAssignment>(StringComparer.Ordinal);
        foreach (var o in was.Overrides) before.TryAdd(o.Name, o);
        foreach (var o in now.Overrides)
        {
            if (before.TryGetValue(o.Name, out var b) && b.Expression == o.Expression && (b.Unit ?? "") == (o.Unit ?? "")) continue;
            if (drawn.Parameters.FirstOrDefault(p => p.Name == o.Name) is not { } p) continue;
            if (c.Parameters.FirstOrDefault(q => q.Name == o.Name) is { } own) { own.Expression = p.Expression; own.Unit = p.Unit; }
            else c.Parameters.Add(p.Clone());
        }
        var stated = now.Overrides.Select(o => o.Name).ToHashSet(StringComparer.Ordinal);
        foreach (string name in before.Keys)
            if (!stated.Contains(name)) c.Parameters.RemoveAll(p => p.Name == name);
    }

    /// <summary>D6: the rows of the VAR (or MEAS) blocks by name.</summary>
    private static void MergeRows(SchematicEditModel model, SchematicEditModel drawing, SymbolKind kind, IEnumerable<string> was)
    {
        var text = new Dictionary<string, EditableParameter>(StringComparer.Ordinal);
        foreach (var p in drawing.Components.Where(c => c.Symbol == kind).SelectMany(c => c.Parameters)) text.TryAdd(p.Name, p);
        var wasNames = was.ToHashSet(StringComparer.Ordinal);
        var blocks = model.Components.Where(c => c.Symbol == kind).ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var b in blocks)
        {
            b.Parameters.RemoveAll(p => wasNames.Contains(p.Name) && !text.ContainsKey(p.Name));
            foreach (var p in b.Parameters)
                if (text.TryGetValue(p.Name, out var t)) { p.Expression = t.Expression; p.Unit = t.Unit; seen.Add(p.Name); }
        }
        var added = text.Values.Where(p => !seen.Contains(p.Name)).ToList();
        if (added.Count == 0) return;
        if (blocks.Count > 0) blocks[0].Parameters.AddRange(added.Select(p => p.Clone()));
        else model.Components.AddRange(drawing.Components.Where(c => c.Symbol == kind));
    }

    private static IEnumerable<string> AnalysisLines(string text)
        => text.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("analysis ", StringComparison.Ordinal));

    /// <summary>A cell instance's symbol pins, local, in port order — what the canvas draws it with.</summary>
    private static IReadOnlyList<(float X, float Y)>? CellPins(EditableComponent c, SchematicEditModel m)
    {
        if (m.SchematicDirectory is null && !CellSymbolResolver.NeedsNoBaseDirectory(c.CellRef!)) return null;
        var symbol = CellSymbolResolver.Resolve(c.CellRef!, m.SchematicDirectory, m.HostWorkspaceRoot).Symbol;
        return symbol is { Pins.Count: > 0 }
            ? [.. symbol.Pins.OrderBy(p => p.PortIndex).Select(p => ((float)p.LocalX, (float)p.LocalY))]
            : null;
    }
}

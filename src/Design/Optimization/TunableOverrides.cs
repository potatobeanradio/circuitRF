using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Expressions;

namespace CircuitRF.Design.Optimization;

/// <param name="TestBench">The testbench to elaborate; null when <paramref name="Refusal"/> is set.</param>
/// <param name="Library">The library to elaborate with; null when refused.</param>
/// <param name="Notes">One line per key that resolved to nothing and was skipped (overview D3).</param>
/// <param name="Refusal">Why nothing was applied, or null.</param>
public sealed record TunedDesign(
    TestBench?            TestBench,
    Library?              Library,
    IReadOnlyList<string> Notes,
    string?               Refusal);

/// <summary>
/// Puts tuned values into a design in memory — the netlist a run elaborates, never a file.
///
/// <para><b>The gate is equality with typing.</b> Elaborating the result equals elaborating a design
/// in which the same text had been typed into the same rows: a top-level value replaces the
/// instance's assignment, a VAR value replaces the variable, a value inside a cell replaces it in the
/// cell DEFINITION (so every instance moves, overview D2), and an inherited cell parameter gains the
/// override Push would add.</para>
///
/// <para>The inputs are not modified. The copies share every object nothing changed — an
/// <see cref="Instance"/>, a <see cref="Variable"/> and a <see cref="Cell"/> are replaced, never
/// mutated, so the original keeps its own.</para>
///
/// <para><b>A complex value is written whole</b> (overview D18): its own key with the whole value's
/// text (<c>ZL</c> → <c>30+52j Ohm</c>) is what the tuning window and a preset hand in. A PART key
/// (<c>mag(ZL)</c>) is accepted too, for a headless caller: the parts given are composed with the
/// value the design holds by <see cref="ComplexValue.Compose"/> — the optimizer's own rule — and
/// written back in the form the design wrote it.</para>
/// </summary>
public static class TunableOverrides
{
    /// <param name="values">Key → value text (<c>R1.R</c> → <c>75 Ohm</c>).</param>
    /// <param name="setVariables">Variables a <c>--set</c> already replaced. <c>--set</c> applies
    /// first; a tuned value naming one of them is a refusal naming both, since one of the two would
    /// silently lose.</param>
    /// <param name="parts">The design's placed vendor parts (<see cref="Circuit.PreparedCircuit.PartTunables"/>): a
    /// drawn value of one (<c>C1.C</c> → <c>1.03e-11 F</c>) binds its cell's scale parameter to drawn ÷ nominal, the
    /// nominal being the stated value of whichever part the instance is bound to (brief vendorlib-5 R-vl5-3c/3d).
    /// Null skips that, and such a key is noted as naming no parameter.</param>
    public static TunedDesign Apply(
        TestBench tb, Library lib, IReadOnlyDictionary<string, string> values,
        IReadOnlyCollection<string>? setVariables = null,
        IReadOnlyList<CircuitRF.Design.VendorLibraries.VendorPartTunable>? parts = null)
    {
        var partOfValue = (parts ?? []).Where(p => p.ValueKey is not null)
                                       .GroupBy(p => p.ValueKey!, StringComparer.Ordinal)
                                       .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var notes   = new List<string>();
        var bench   = CopyOf(tb);
        var library = new Library(lib.Name);
        library.Cells.AddRange(lib.Cells);
        var copied  = new HashSet<string>(StringComparer.Ordinal);

        // A part's value is bound against the part the instance ends up bound to, so its Part key goes first.
        var ordered = WholeValues(tb, lib, values, notes)
            .OrderBy(kv => partOfValue.ContainsKey(kv.Key) ? 1 : 0).ToList();
        foreach (var (keyText, value) in ordered)
        {
            if (!TunableKey.TryParse(keyText, out var key))
            {
                notes.Add($"'{keyText}' is not a tunable key; skipped.");
                continue;
            }

            if (key.Cell is null && key.IsVariable && setVariables?.Contains(key.Name) == true)
                return new TunedDesign(null, null, notes,
                    $"'{key.Name}' is set both by --set and by a tuned value; remove one of the two.");

            List<Instance> instances;
            List<Variable> variables;
            if (key.Cell is null)
            {
                instances = bench.Instances;
                variables = bench.GlobalVariables;
            }
            else
            {
                int ci = library.Cells.FindIndex(c => c.Name == key.Cell);
                if (ci < 0) { notes.Add($"'{keyText}' names no cell '{key.Cell}'; skipped."); continue; }
                if (copied.Add(key.Cell)) library.Cells[ci] = CopyOf(library.Cells[ci]);
                instances = library.Cells[ci].Instances;
                variables = library.Cells[ci].Variables;
            }

            if (key.IsVariable)
            {
                int vi = variables.FindIndex(v => v.Name == key.Name);
                if (vi < 0 && key.Cell is null && key.Name == Temperature.AmbientGlobalName)
                {
                    // The ambient is readable by name whether or not the design states it (Elaborator), so a corner's
                    // temp (brief-yield-6) on a design that never wrote `temp` is the global typing it would add.
                    var (texpr, tunit) = TunableValue.Split(value, null);
                    variables.Add(new Variable(key.Name, texpr, tunit));
                    continue;
                }
                if (vi < 0) { notes.Add($"'{keyText}' names no variable; skipped."); continue; }
                var (expr, unit) = TunableValue.Split(value, variables[vi].Unit);
                variables[vi] = new Variable(key.Name, expr, unit);
                continue;
            }

            int ii = instances.FindIndex(i => i.InstanceName == key.Instance);
            if (ii < 0) { notes.Add($"'{keyText}' names no instance '{key.Instance}'; skipped."); continue; }
            var inst      = instances[ii];

            if (key.IsVendorPart && lib.Find(inst.Reference) is not null)
            {
                if (Rebind(inst, value, library) is { } rebound) instances[ii] = rebound;
                else notes.Add($"'{keyText}': '{value}' is neither a candidate part nor a capacitance or inductance; skipped.");
                continue;
            }

            if (partOfValue.TryGetValue(keyText, out var valueOf))
            {
                if (ScalePart(inst, value, valueOf, library) is { } scaled) instances[ii] = scaled;
                else notes.Add($"'{keyText}': '{value}' cannot be drawn on {inst.Reference}, which carries no " +
                               $"{CircuitRF.Design.Schematic.SpiceModelNetlist.PartScaleParameter} or states no value; skipped.");
                continue;
            }

            var overrides = inst.Overrides.ToList();
            int oi        = overrides.FindIndex(o => o.Name == key.Name);

            if (oi >= 0)
            {
                var (expr, unit) = TunableValue.Split(value, overrides[oi].Unit);
                overrides[oi] = new ParameterAssignment(key.Name, expr, unit);
            }
            else if (lib.Find(inst.Reference)?.Parameters.FirstOrDefault(p => p.Name == key.Name) is { } decl)
            {
                // An inherited cell parameter: the override Push would add.
                var (expr, unit) = TunableValue.Split(value, decl.Unit);
                overrides.Add(new ParameterAssignment(key.Name, expr, unit));
            }
            else
            {
                notes.Add($"'{keyText}': {key.Instance} has no parameter {key.Name}; skipped.");
                continue;
            }

            instances[ii] = new Instance(inst.InstanceName, inst.Reference, inst.NetBindings, overrides)
            {
                RefNetBinding = inst.RefNetBinding,
            };
        }

        return new TunedDesign(bench, library, notes, null);
    }

    /// <summary>
    /// A placed vendor part's <c>Part</c> value (brief vendorlib-4 R-vl4-2b): the instance's CELL is rebound, not
    /// a value written. A value naming a cell of the library binds the instance to it — a candidate part,
    /// emitted at extraction — keeping only the overrides that cell declares. A capacitance or an inductance
    /// (<c>1.2e-11 F</c>) is the IDEAL stand-in a mixed search's continuous stage evaluates in the part's place
    /// (R-vl4-3b): the primitive C or L on the part's two nets. Null for anything else.
    /// </summary>
    private static Instance? Rebind(Instance inst, string value, Library lib)
    {
        string v = value.Trim();
        if (lib.Find(v) is { } cell)
        {
            var declared = cell.Parameters.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return new Instance(inst.InstanceName, cell.Name, inst.NetBindings,
                                [.. inst.Overrides.Where(o => declared.Contains(o.Name))])
            {
                RefNetBinding = inst.RefNetBinding,
            };
        }
        if (inst.NetBindings.Count != 2 || !TunableValue.TryParse(v, out _, out string unit, out _)) return null;
        string? primitive = Units.BaseUnit(unit) switch { "F" => "C", "H" => "L", _ => null };
        if (primitive is null) return null;
        var (expr, u) = TunableValue.Split(v, null);
        return new Instance(inst.InstanceName, primitive, inst.NetBindings, [new ParameterAssignment(primitive, expr, u)]);
    }

    /// <summary>
    /// A drawn value of a vendor part (brief vendorlib-5 R-vl5-3c): the instance's cell's scale parameter bound to
    /// drawn ÷ the stated value of the part that cell is — the placed part, or the candidate an earlier Part key bound
    /// it to. Null when the cell carries no scale parameter or the part states no value.
    /// </summary>
    private static Instance? ScalePart(Instance inst, string value, CircuitRF.Design.VendorLibraries.VendorPartTunable part,
                                       Library lib)
    {
        const string scale = CircuitRF.Design.Schematic.SpiceModelNetlist.PartScaleParameter;
        if (lib.Find(inst.Reference) is not { } cell || !cell.Parameters.Any(p => p.Name == scale)) return null;
        if (part.EntryOfCell(inst.Reference)?.Header.Value is not { } nominal || nominal <= 0) return null;
        if (!TunableValue.TryParse(value, out _, out _, out double drawn)) return null;

        string ratio = (drawn / nominal).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        var overrides = inst.Overrides.Where(o => o.Name != scale).Append(new ParameterAssignment(scale, ratio)).ToList();
        return new Instance(inst.InstanceName, inst.Reference, inst.NetBindings, overrides) { RefNetBinding = inst.RefNetBinding };
    }

    /// <summary><paramref name="values"/> with every complex part folded into its whole value.</summary>
    private static List<KeyValuePair<string, string>> WholeValues(
        TestBench tb, Library lib, IReadOnlyDictionary<string, string> values, List<string> notes)
    {
        var result = new List<KeyValuePair<string, string>>();
        var parts  = new Dictionary<string, Dictionary<ComplexPart, string>>(StringComparer.Ordinal);
        foreach (var (k, v) in values)
        {
            if (TunableKey.TryParse(k, out var key) && key.Part is { } part)
            {
                string whole = key.Whole.ToString();
                if (!parts.TryGetValue(whole, out var map)) parts[whole] = map = [];
                map[part] = v;
            }
            else result.Add(new(k, v));
        }

        foreach (var (whole, map) in parts)
        {
            int given = result.FindIndex(kv => kv.Key == whole);
            string? start = given >= 0 ? result[given].Value : StoredText(tb, lib, whole);
            if (start is null || !ComplexValue.TryParse(start, out var z, out string unit, out var form))
            {
                notes.Add($"'{whole}' is not a complex value; its parts were skipped.");
                continue;
            }

            var numbers = new Dictionary<ComplexPart, double>();
            foreach (var (part, text) in map)
                if (TunableValue.InUnit(text, part == ComplexPart.Phase ? "deg" : unit) is { } n) numbers[part] = n;
                else notes.Add($"'{TunableKey.PartKey(whole, part)}': '{text}' is not a number; skipped.");

            if (ComplexValue.Compose(z, numbers) is not { } composed)
            {
                notes.Add(numbers.Count > 2
                    ? $"'{whole}': {numbers.Count} parts given, and a complex value has two; skipped."
                    : $"'{whole}': no complex value has those parts; skipped.");
                continue;
            }
            var entry = new KeyValuePair<string, string>(whole, ComplexValue.Format(composed, unit, form, "G15"));
            if (given >= 0) result[given] = entry; else result.Add(entry);
        }
        return result;
    }

    /// <summary>The value text the design holds for a key, or null when it names nothing.</summary>
    private static string? StoredText(TestBench tb, Library lib, string keyText)
    {
        if (!TunableKey.TryParse(keyText, out var key)) return null;
        var cell = key.Cell is null ? null : lib.Find(key.Cell);
        if (key.Cell is not null && cell is null) return null;
        var instances = cell?.Instances ?? tb.Instances;
        var variables = cell?.Variables ?? tb.GlobalVariables;

        if (key.IsVariable)
            return variables.FirstOrDefault(v => v.Name == key.Name) is { } v ? TunableValue.Text(v.Expression, v.Unit) : null;

        if (instances.FirstOrDefault(i => i.InstanceName == key.Instance) is not { } inst) return null;
        if (inst.Overrides.FirstOrDefault(o => o.Name == key.Name) is { } ov) return TunableValue.Text(ov.Expression, ov.Unit);
        return lib.Find(inst.Reference)?.Parameters.FirstOrDefault(p => p.Name == key.Name) is { } decl
            ? TunableValue.Text(decl.DefaultExpression, decl.Unit) : null;
    }

    private static TestBench CopyOf(TestBench tb)
    {
        var c = new TestBench(tb.Name) { Tuning = tb.Tuning };
        c.Instances.AddRange(tb.Instances);
        c.GlobalVariables.AddRange(tb.GlobalVariables);
        c.Functions.AddRange(tb.Functions);
        c.Analyses.AddRange(tb.Analyses);
        c.Measurements.AddRange(tb.Measurements);
        c.RawDirectives.AddRange(tb.RawDirectives);
        c.ReadWarnings.AddRange(tb.ReadWarnings);
        c.ReadNotes.AddRange(tb.ReadNotes);
        c.LabeledNets.UnionWith(tb.LabeledNets);
        return c;
    }

    private static Cell CopyOf(Cell cell)
    {
        var c = new Cell(cell.Name);
        c.Ports.AddRange(cell.Ports);
        c.Parameters.AddRange(cell.Parameters);
        c.Variables.AddRange(cell.Variables);
        c.Instances.AddRange(cell.Instances);
        return c;
    }
}

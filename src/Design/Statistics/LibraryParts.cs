using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;

namespace CircuitRF.Design.Statistics;

/// <summary>One distribution inside a placed library model: where it is written and what it says.</summary>
/// <param name="Site">The parameter (<c>Cg</c>) or element value (<c>C1.C</c>) it is written on, within the model.</param>
/// <param name="Expression">The text, as the model writes it.</param>
/// <param name="Unit">The unit it is read in, when it has one.</param>
public sealed record ModelDistribution(string Site, string Expression, string? Unit)
{
    /// <summary>The spread in the Yield panel's own notation (<c>± 5 % at 3σ</c>).</summary>
    public string Spread => ModelTolerance.Describe(Expression, Unit);
}

/// <summary>A library part a design places (brief vendorlib-13): its number, the model's top cell, every instance of it
/// by full path, the model's corners and every distribution inside it.</summary>
public sealed record PlacedLibraryPart(
    string                           Part,
    Cell                             Cell,
    IReadOnlyList<string>            Instances,
    IReadOnlyList<CellCorner>        Corners,
    IReadOnlyList<ModelDistribution> Distributions);

/// <summary>
/// The placed library parts of an EXTRACTED design (brief vendorlib-13 R-vl13-1c, R-vl13-2e/f/g), found by the
/// <c>part</c> line extraction writes into a placed model's top <c>define</c> — so a run of a <c>.cnl</c> finds them as
/// a run of the schematic does, and nothing here reads a library.
/// </summary>
public static class LibraryParts
{
    /// <summary>Every library part <paramref name="tb"/> places, in the order first met.</summary>
    public static IReadOnlyList<PlacedLibraryPart> Placed(TestBench tb, Library lib)
    {
        var paths = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var cells = new Dictionary<string, Cell>(StringComparer.Ordinal);
        Walk(tb.Instances, "", lib, 0, (path, cell) =>
        {
            if (cell.Part is not { } p) return;
            if (!paths.TryGetValue(p, out var list)) { paths[p] = list = []; cells[p] = cell; }
            list.Add(path);
        });
        return [.. paths.Select(kv => new PlacedLibraryPart(kv.Key, cells[kv.Key], kv.Value, cells[kv.Key].Corners,
                                                             DistributionsIn(cells[kv.Key], lib)))];
    }

    /// <summary>The full instance path of every instance of <paramref name="part"/> (<c>U1</c>, <c>X2.U1</c>).</summary>
    public static IReadOnlyList<string> InstancePaths(TestBench tb, Library lib, string part)
    {
        var list = new List<string>();
        Walk(tb.Instances, "", lib, 0, (path, cell) => { if (string.Equals(cell.Part, part, StringComparison.Ordinal)) list.Add(path); });
        return list;
    }

    /// <summary>The instance paths whose draws are held at nominal: every instance of each of <paramref name="parts"/>.</summary>
    public static IReadOnlyList<string> HeldPaths(TestBench tb, Library lib, IEnumerable<string> parts)
    {
        var set = new HashSet<string>(parts, StringComparer.Ordinal);
        if (set.Count == 0) return [];
        var list = new List<string>();
        Walk(tb.Instances, "", lib, 0, (path, cell) => { if (cell.Part is { } p && set.Contains(p)) list.Add(path); });
        return list;
    }

    private static void Walk(IEnumerable<Instance> instances, string prefix, Library lib, int depth, Action<string, Cell> visit)
    {
        if (depth > 64) return;   // a cycle is the elaborator's own error to report
        foreach (var inst in instances)
        {
            if (lib.Find(inst.Reference) is not { } cell) continue;
            string path = prefix + inst.InstanceName;
            visit(path, cell);
            if (cell.Part is null) Walk(cell.Instances, path + ".", lib, depth + 1, visit);
        }
    }

    /// <summary>Every distribution written inside <paramref name="cell"/> and the cells it places: its parameter defaults,
    /// its variables and its elements' values.</summary>
    public static IReadOnlyList<ModelDistribution> DistributionsIn(Cell cell, Library lib)
    {
        var list = new List<ModelDistribution>();
        void Visit(Cell c, string prefix, int depth)
        {
            if (depth > 16) return;
            foreach (var p in c.Parameters)
                if (Evaluator.ContainsStatisticalCall(p.DefaultExpression)) list.Add(new(prefix + p.Name, p.DefaultExpression, p.Unit));
            foreach (var v in c.Variables)
                if (Evaluator.ContainsStatisticalCall(v.Expression)) list.Add(new(prefix + v.Name, v.Expression, v.Unit));
            foreach (var inst in c.Instances)
            {
                foreach (var o in inst.Overrides)
                    if (Evaluator.ContainsStatisticalCall(o.Expression)) list.Add(new($"{prefix}{inst.InstanceName}.{o.Name}", o.Expression, o.Unit));
                if (lib.Find(inst.Reference) is { } sub) Visit(sub, $"{prefix}{inst.InstanceName}.", depth + 1);
            }
        }
        Visit(cell, "", 0);
        return list;
    }
}

/// <summary>
/// A bench corner's <c>part:</c> axis applied to a netlist (brief vendorlib-13 R-vl13-2e): every instance of the part,
/// wherever it sits, has its <c>Corner</c> selector replaced by the corner named — the one-lot convention. The inputs
/// are not modified; a cell holding such an instance is copied.
/// </summary>
public static class LibraryPartCorners
{
    public static (TestBench TestBench, Library Library) Apply(
        TestBench tb, Library lib, IEnumerable<KeyValuePair<string, string>> partCorners, List<string> notes)
    {
        var want = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, corner) in partCorners)
        {
            string part = key[CellCorner.PartPrefix.Length..];
            var cell = lib.Cells.FirstOrDefault(c => string.Equals(c.Part, part, StringComparison.Ordinal));
            if (cell is null) { notes.Add($"'{key}': the design places no part {part}; skipped."); continue; }
            string c = corner.Trim();
            if (!c.Equals(CellCorner.Nominal, StringComparison.OrdinalIgnoreCase)
                && !cell.Corners.Any(x => x.Name.Equals(c, StringComparison.OrdinalIgnoreCase)))
            {
                notes.Add($"'{key}': part {part} has no corner '{c}' (it offers " +
                          $"{string.Join(", ", new[] { CellCorner.Nominal }.Concat(cell.Corners.Select(x => x.Name)))}); skipped.");
                continue;
            }
            want[part] = c;
        }
        if (want.Count == 0) return (tb, lib);

        List<Instance> Rewrite(IReadOnlyList<Instance> instances, out bool changed)
        {
            changed = false;
            var list = new List<Instance>(instances.Count);
            foreach (var inst in instances)
            {
                if (lib.Find(inst.Reference)?.Part is { } p && want.TryGetValue(p, out var corner))
                {
                    var ov = inst.Overrides.Where(o => o.Name != CellCorner.SelectorParameter).ToList();
                    ov.Add(new ParameterAssignment(CellCorner.SelectorParameter, corner));
                    list.Add(new Instance(inst.InstanceName, inst.Reference, inst.NetBindings, ov) { RefNetBinding = inst.RefNetBinding });
                    changed = true;
                }
                else list.Add(inst);
            }
            return list;
        }

        var bench = new TestBench(tb.Name) { Tuning = tb.Tuning, Technology = tb.Technology };
        bench.Instances.AddRange(Rewrite(tb.Instances, out _));
        bench.GlobalVariables.AddRange(tb.GlobalVariables);
        bench.Functions.AddRange(tb.Functions);
        bench.Analyses.AddRange(tb.Analyses);
        bench.Measurements.AddRange(tb.Measurements);
        bench.RawDirectives.AddRange(tb.RawDirectives);
        bench.ReadWarnings.AddRange(tb.ReadWarnings);
        bench.ReadNotes.AddRange(tb.ReadNotes);
        bench.LabeledNets.UnionWith(tb.LabeledNets);

        var library = new Library(lib.Name);
        foreach (var cell in lib.Cells)
        {
            var rewritten = Rewrite(cell.Instances, out bool changed);
            if (!changed) { library.Cells.Add(cell); continue; }
            var copy = cell.CopyShallow(cell.Name);
            copy.Instances.Clear();
            copy.Instances.AddRange(rewritten);
            library.Cells.Add(copy);
        }
        return (bench, library);
    }
}

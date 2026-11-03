using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Schematic;

namespace CircuitRF.Design.VendorLibraries.ModelLibrary.Protect;

/// <summary>Which side of the compiled module an element lands on (brief vendorlib-16 R-vl16-1a).</summary>
public enum ProtectSide { Inside, Outside }

/// <summary>One level of the flattened hierarchy: the top cell, or one placed sub-cell. Its <see cref="Scope"/> is built
/// as the elaborator builds a cell's — defaults, variables, and each override bound as a resolved value — so a site unit
/// is applied to an element's value exactly as a run applies it.</summary>
public sealed class ProtectLevel
{
    /// <summary>The instance path, <c>.</c>-separated; empty at the top.</summary>
    public required string Path { get; init; }
    public required Cell Cell { get; init; }
    public required Scope Scope { get; init; }
    public ProtectLevel? Parent { get; init; }
    /// <summary>The instance that placed this level, null at the top.</summary>
    public Instance? PlacedBy { get; init; }
    public bool IsTop => Parent is null;
}

/// <summary>One element after flattening: its path (<c>X1.R2</c>), the instance as written, the level whose names its
/// values use, and its nets in flattened names.</summary>
public sealed record ProtectElement(string Path, Instance Instance, ProtectLevel Level, IReadOnlyList<string> Nets, ProtectSide Side)
{
    public string Type => Instance.Reference;
}

/// <summary>What Compile netlist would do with a cell (brief vendorlib-16 §2): what is compiled, what stays outside, the
/// module's terminals, and every refusal. Read, never written.</summary>
public sealed record ProtectPlan
{
    public required string Cell { get; init; }
    public required ProtectLevel Top { get; init; }
    /// <summary>The cell's pins, in port order — the module's first terminals.</summary>
    public IReadOnlyList<string> Pins { get; init; } = [];
    public IReadOnlyList<ProtectElement> Inside { get; init; } = [];
    public IReadOnlyList<ProtectElement> Outside { get; init; } = [];
    /// <summary>The nets that join an inside element to an outside one and are not pins: each an extra terminal
    /// (D2), in the order the outside elements first name them.</summary>
    public IReadOnlyList<string> Joins { get; init; } = [];
    /// <summary>Every flattened level, the top first.</summary>
    public IReadOnlyList<ProtectLevel> Levels { get; init; } = [];
    /// <summary>The folder the netlist's relative file references resolve against.</summary>
    public string? BaseDirectory { get; init; }
    public IReadOnlyList<string> Refusals { get; init; } = [];

    /// <summary>Pins then joins: the compiled module's terminals.</summary>
    public IEnumerable<string> Terminals => Pins.Concat(Joins);

    /// <summary>The dialog's and <c>--json</c>'s summary lines.</summary>
    public IEnumerable<string> Describe()
    {
        yield return Inside.Count == 0 ? "Compiled: nothing." : $"Compiled into one module: {string.Join(", ", Inside.Select(e => $"{e.Path} ({e.Type})"))}.";
        yield return Outside.Count == 0 ? "Outside: nothing." : $"Outside, in the wrapper: {string.Join(", ", Outside.Select(e => $"{e.Path} ({e.Type})"))}.";
        var inside = Inside.SelectMany(e => e.Nets).ToHashSet(StringComparer.Ordinal);
        int pins = Pins.Count(inside.Contains);
        bool ground = inside.Contains("0");
        var parts = new List<string> { $"{pins} of the cell's {Pins.Count} pins" };
        if (Joins.Count > 0) parts.Add($"{Joins.Count} added where outside elements meet it ({string.Join(", ", Joins)})");
        if (ground) parts.Add("ground");
        int total = pins + Joins.Count + (ground ? 1 : 0);
        string list = parts.Count == 1 ? parts[0] : string.Join(", ", parts.SkipLast(1)) + " and " + parts[^1];
        yield return $"The module has {total} terminal{(total == 1 ? "" : "s")}: {list}.";
    }
}

/// <summary>
/// The partition (brief vendorlib-16 R-vl16-1a): the cell's netlist flattened — sub-cells inlined, instance paths as
/// names, values kept as expressions — and each element sorted inside or outside by one table. A type in neither column
/// is a refusal naming it, never a silent outside.
/// </summary>
public static class ProtectPartition
{
    /// <summary>Translated into the module: <c>VaNetwork</c>'s elements and the composed FET.</summary>
    public static readonly IReadOnlySet<string> InsideTypes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "R", "C", "L", "Mutual", "Short", "ComposedFET" };

    /// <summary>Left in the wrapper: S-parameter files, transmission lines and every technology part, and the devices
    /// that are themselves external models.</summary>
    public static IReadOnlySet<string> OutsideTypes => _outside.Value;

    private static readonly Lazy<HashSet<string>> _outside = new(() =>
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SnP", "TLIN", "CLIN", "SpiceModel", "VerilogA", "ExtDevice",
        };
        foreach (var kind in Enum.GetValues<SymbolKind>())
            if (TechnologyBound.Is(kind))
                try { set.Add(ComponentTypeRegistry.EngineReference(kind)); } catch { }
        return set;
    });

    /// <summary>The partition of <paramref name="cnlText"/>'s define <paramref name="cell"/>.</summary>
    public static ProtectPlan Partition(string cnlText, string cell, string? baseDirectory)
    {
        var refusals = new List<string>();
        Library lib;
        TestBench tb;
        try { (lib, tb) = new CnlReader().Read(cnlText, "protect", baseDirectory); }
        catch (Exception ex) { return Refused(cell, $"The cell's netlist could not be read: {ex.Message}"); }
        if (lib.Find(cell) is not { } top) return Refused(cell, $"The cell's netlist has no define named {cell}.");

        var global = new Scope("global");
        foreach (var v in tb.GlobalVariables) global.Bind(v.Name, v.Expression, v.Unit);
        var topScope = new Scope(cell, global);
        foreach (var p in top.Parameters) topScope.Bind(p.Name, p.DefaultExpression, p.Unit);
        foreach (var v in top.Variables) topScope.Bind(v.Name, v.Expression, v.Unit);
        var topLevel = new ProtectLevel { Path = "", Cell = top, Scope = topScope };

        var levels = new List<ProtectLevel> { topLevel };
        var elements = new List<ProtectElement>();
        Flatten(lib, topLevel, top.Ports.ToDictionary(p => p, p => p, StringComparer.Ordinal), elements, levels, refusals, depth: 0);

        var inside = elements.Where(e => e.Side == ProtectSide.Inside).ToList();
        var outside = elements.Where(e => e.Side == ProtectSide.Outside).ToList();
        var insideNets = inside.SelectMany(e => e.Nets).ToHashSet(StringComparer.Ordinal);
        var joins = new List<string>();
        foreach (var e in outside)
            foreach (string n in e.Nets)
                if (n != "0" && insideNets.Contains(n) && !top.Ports.Contains(n) && !joins.Contains(n)) joins.Add(n);

        // An outside element in a sub-cell keeps its values as written, in the wrapper's scope: a name only the sub-cell
        // binds would not resolve there.
        foreach (var e in outside.Where(e => !e.Level.IsTop))
            foreach (var ov in e.Instance.Overrides)
            {
                IEnumerable<string> refs;
                try { refs = AstWalker.CollectRefs(Parser.Parse(ov.Expression)); } catch { continue; }
                foreach (string r in refs)
                    if (BoundBelowTop(e.Level, r) is { } where)
                        refusals.Add($"{e.Path} ({e.Type}) stays outside the compiled module, and its {ov.Name}={ov.Expression} names {r}, " +
                                     $"which only the sub-cell {where} defines. Give it a value the cell itself can state.");
            }

        // R-vl16-1d: a composed FET's thermal pin is a terminal when it is wired to a pin, an internal node when it is
        // wired to nothing else; it cannot share a node with an electrical element.
        foreach (var fet in inside.Where(e => IsComposedFet(e.Type) && e.Nets.Count == 4))
        {
            string t = fet.Nets[3];
            if (t == "0") { refusals.Add($"{fet.Path}'s thermal pin is on ground; leave it unconnected, or wire it to a pin of the cell."); continue; }
            bool others = elements.Any(o => !ReferenceEquals(o, fet) && o.Nets.Contains(t));
            if (others) refusals.Add($"{fet.Path}'s thermal pin shares the net {t} with other elements; wire it only to a pin of the cell, or leave it unconnected.");
        }

        return new ProtectPlan
        {
            Cell = cell, Top = topLevel, Pins = [.. top.Ports], Inside = inside, Outside = outside, Joins = joins,
            Levels = levels, BaseDirectory = baseDirectory, Refusals = refusals.Distinct(StringComparer.Ordinal).ToList(),
        };
    }

    public static bool IsComposedFet(string type) => type.Equals("ComposedFET", StringComparison.OrdinalIgnoreCase);

    private static ProtectPlan Refused(string cell, string why) => new()
    {
        Cell = cell, Top = new ProtectLevel { Path = "", Cell = new Cell(cell), Scope = new Scope(cell) }, Refusals = [why],
    };

    private static void Flatten(Library lib, ProtectLevel level, Dictionary<string, string> netMap, List<ProtectElement> elements,
                                List<ProtectLevel> levels, List<string> refusals, int depth)
    {
        if (depth > 64) { refusals.Add($"The hierarchy under {level.Path} is more than 64 levels deep; is a cell placing itself?"); return; }
        string Net(string local)
        {
            if (local == "0") return "0";
            if (netMap.TryGetValue(local, out string? flat)) return flat;
            return netMap[local] = level.IsTop ? local : level.Path + "." + local;
        }
        string PathOf(Instance i) => level.IsTop ? i.InstanceName : level.Path + "." + i.InstanceName;

        foreach (var inst in level.Cell.Instances)
        {
            var nets = inst.NetBindings.Select(Net).ToList();
            if (lib.Find(inst.Reference) is { } sub)
            {
                if (sub.Ports.Count != nets.Count)
                {
                    refusals.Add($"{PathOf(inst)} places {sub.Name} with {nets.Count} nets, and {sub.Name} has {sub.Ports.Count} pins.");
                    continue;
                }
                string path = PathOf(inst);
                var scope = new Scope(path, level.Scope);
                foreach (var p in sub.Parameters) scope.Bind(p.Name, p.DefaultExpression, p.Unit);
                foreach (var v in sub.Variables) scope.Bind(v.Name, v.Expression, v.Unit);
                foreach (var ov in inst.Overrides) scope.Bind(ov.Name, "__resolved__");
                var child = new ProtectLevel { Path = path, Cell = sub, Scope = scope, Parent = level, PlacedBy = inst };
                levels.Add(child);
                var map = new Dictionary<string, string>(StringComparer.Ordinal);
                for (int k = 0; k < sub.Ports.Count; k++) map[sub.Ports[k]] = nets[k];
                Flatten(lib, child, map, elements, levels, refusals, depth + 1);
                continue;
            }
            ProtectSide side;
            if (InsideTypes.Contains(inst.Reference)) side = ProtectSide.Inside;
            else if (OutsideTypes.Contains(inst.Reference)) side = ProtectSide.Outside;
            else
            {
                refusals.Add($"{PathOf(inst)} is a {inst.Reference}, which Compile netlist neither writes as Verilog-A nor leaves outside the module.");
                continue;
            }
            elements.Add(new ProtectElement(PathOf(inst), inst, level, nets, side));
        }
    }

    /// <summary>The sub-cell path that binds <paramref name="name"/> below the top, walking up from
    /// <paramref name="level"/>; null when only the top (or nothing) binds it.</summary>
    private static string? BoundBelowTop(ProtectLevel level, string name)
    {
        for (var l = level; l is { IsTop: false }; l = l.Parent)
            if (l.Cell.Parameters.Any(p => p.Name == name) || l.Cell.Variables.Any(v => v.Name == name)
                || l.PlacedBy!.Overrides.Any(o => o.Name == name))
                return l.Path;
        return null;
    }
}

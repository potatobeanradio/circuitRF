// Owner: PCD-5 (brief-pcd-0 §5, seam 5; written by PCD-1's seams commit). Framework-free.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Design.Layout.PCells.Wire;
using CircuitRF.Design.Layout.SchematicSync;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Workspace;

namespace CircuitRF.Design.PCellDesigner;

/// <summary>brief-pcd-0 S3: resolves <c>"cpcell:&lt;Name&gt;"</c> to the designed cell of that name.</summary>
/// <remarks>
/// brief-pcd-5 R-pcd5-2. One resolver per registered workspace root, registered with <see cref="PCellRegistry"/> as a kit's
/// is, so everything above the registry (palette, Properties, handles, regeneration, both sync directions, LVS, EM) reaches
/// a designed cell exactly as it reaches a script's. Its generator is the planner at the instance's values
/// (<see cref="DesignedCellLayout.At"/>), flattened to the copper of every part it places.
/// </remarks>
public sealed class DesignedPCellResolver : IPCellGeneratorResolver
{
    // ── Registration (reference-counted per root) ─────────────────────────────

    private sealed class Entry(string root, DesignedPCellResolver resolver)
    {
        public string Root { get; } = root;
        public DesignedPCellResolver Resolver { get; } = resolver;
        public int Count { get; set; } = 1;
    }

    private static readonly List<Entry> _entries = [];
    private static readonly Lock _gate = new();

    /// <summary>
    /// Makes every designed cell under workspaceRootDir (and the folders its .cws references: LibraryRefs,
    /// ReferencedWorkspaces) resolvable as "cpcell:&lt;Name&gt;" until disposed. Reference-counted per root: nested calls
    /// are free. Registration starts nothing; the folders are read on the first Resolve.
    /// </summary>
    /// <remarks>The registry is told outside <c>_gate</c>: it calls <see cref="Resolve"/> while holding its own gate, so
    /// taking the two in the other order here could deadlock (brief-pcd-5 D5).</remarks>
    public static IDisposable Register(string workspaceRootDir)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRootDir));
        DesignedPCellResolver? added = null;
        lock (_gate)
        {
            if (_entries.FirstOrDefault(e => e.Root == root) is { } entry) entry.Count++;
            else _entries.Add(new Entry(root, added = new DesignedPCellResolver(root)));
        }
        if (added is not null) PCellRegistry.AddResolver(added);
        return new Registration(root);
    }

    private sealed class Registration(string root) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            DesignedPCellResolver? removed = null;
            lock (_gate)
            {
                if (_entries.FirstOrDefault(e => e.Root == root) is not { } entry) return;
                if (--entry.Count == 0) { _entries.Remove(entry); removed = entry.Resolver; }
            }
            if (removed is not null) PCellRegistry.RemoveResolver(removed);
        }
    }

    /// <summary>The cell folder a registered "cpcell:&lt;Name&gt;" id names, or null (also for a name found twice).</summary>
    public static string? CellDirOf(string generatorId)
    {
        DesignedPCellResolver[] all;
        lock (_gate) all = [.. _entries.Select(e => e.Resolver)];
        foreach (var resolver in all)
            if (resolver.Lookup(generatorId) is { } dirs) return dirs.Count == 1 ? dirs[0] : null;
        return null;
    }

    // ── Discovery ─────────────────────────────────────────────────────────────

    private readonly string _root;
    private readonly Lock _scanGate = new();
    private Dictionary<string, List<string>>? _cells;   // cell name → the folders holding a designed cell of that name

    private DesignedPCellResolver(string root) => _root = root;

    /// <summary>How deep under each folder a designed cell is looked for. A cell is not descended into.</summary>
    private const int ScanDepth = 4;

    /// <summary>The folders holding a designed cell named by <paramref name="generatorId"/>, or null when none does. A
    /// miss, or a hit no longer designed, reads the folders again: a cell made or moved since is found (D3).</summary>
    private IReadOnlyList<string>? Lookup(string generatorId)
    {
        if (!generatorId.StartsWith(PCellDesign.GeneratorPrefix, StringComparison.OrdinalIgnoreCase)) return null;
        string name = generatorId[PCellDesign.GeneratorPrefix.Length..];
        for (int pass = 0; pass < 2; pass++)
            if (Cells(rescan: pass == 1).TryGetValue(name, out var dirs) && dirs.All(PCellDesign.IsDesigned)) return dirs;
        return null;
    }

    private Dictionary<string, List<string>> Cells(bool rescan)
    {
        lock (_scanGate)
        {
            if (_cells is not null && !rescan) return _cells;
            var found = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string folder in SearchedFolders()) Scan(folder, 0, found, seen);
            return _cells = found;
        }
    }

    /// <summary>The root, its library folders and its referenced workspaces, in that order.</summary>
    private IEnumerable<string> SearchedFolders()
    {
        yield return _root;
        CwsFile? cws = null;
        try { if (File.Exists(Path.Combine(_root, ".cws"))) cws = WorkspacePersistence.LoadFromFile(Path.Combine(_root, ".cws")); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException) { }
        foreach (string lib in cws?.LibraryRefs ?? [])
            yield return Path.GetFullPath(Path.Combine(_root, lib));
        foreach (var r in cws?.ReferencedWorkspaces ?? [])
            if (ExternalCellRef.WorkspaceRootForAlias(_root, r.Alias) is { } other) yield return other;
    }

    private static void Scan(string dir, int depth, Dictionary<string, List<string>> found, HashSet<string> seen)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
        if (!Directory.Exists(full) || !seen.Add(full)) return;
        if (File.Exists(Path.Combine(full, CellFolder.CcellFileName)))
        {
            if (PCellDesign.IsDesigned(full))
            {
                string name = Path.GetFileName(full);
                if (!found.TryGetValue(name, out var dirs)) found[name] = dirs = [];
                dirs.Add(full);
            }
            return;
        }
        if (depth >= ScanDepth) return;
        string[] subs;
        try { subs = Directory.GetDirectories(full); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
        Array.Sort(subs, StringComparer.Ordinal);
        foreach (string sub in subs)
            if (!Path.GetFileName(sub).StartsWith('.')) Scan(sub, depth + 1, found, seen);
    }

    // ── IPCellGeneratorResolver ───────────────────────────────────────────────

    public PCellGenerator? Resolve(string generatorId)
        => Lookup(generatorId) is { Count: 1 } ? new PCellGenerator((p, t, _) => Generate(generatorId, p, t)) : null;

    public IReadOnlyCollection<string> KnownGeneratorIds =>
        [.. Cells(rescan: false).Where(kv => kv.Value.Count == 1).Select(kv => PCellDesign.GeneratorIdFor(kv.Key))
                                .Order(StringComparer.Ordinal)];

    public string Describe()
    {
        var twice = Cells(rescan: false).Where(kv => kv.Value.Count > 1).OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{PCellDesign.GeneratorIdFor(kv.Key)} is found in {kv.Value.Count} folders ({string.Join(", ", kv.Value)}), " +
                          "so it is not resolved; rename one");
        return string.Join("; ", new[] { $"designed cells (PCell Designer) under {_root} and the folders it references" }.Concat(twice));
    }

    public IReadOnlyDictionary<string, PCellValue>? DeclaredDefaults(string generatorId)
        => DeclaredParameters(generatorId) is { } declared
            ? declared.Where(p => p.Default is not null).ToDictionary(p => p.Name, p => p.Default!.Value, StringComparer.Ordinal)
            : null;

    public IReadOnlyList<PCellParameterInfo>? DeclaredParameters(string generatorId)
    {
        if (Lookup(generatorId) is not { Count: 1 } dirs || CellFolder.ReadCcell(dirs[0]) is not { } ccell) return null;
        var scope = CellScope(dirs[0], ccell);
        var evaluator = new Evaluator();
        return [.. ccell.Parameters.Select(p => new PCellParameterInfo(
            p.Name, PCellValueKind.Real,
            SchematicLayoutPlanner.TryResolveSiValue(p.DefaultExpression, p.Unit, scope, evaluator, out double v, out _) ? PCellValue.Real(v) : (PCellValue?)null,
            p.Description, null, p.Minimum, p.Maximum, false,
            p.Dimension switch { UnitDimension.Length => PCellDimension.Length, UnitDimension.Angle => PCellDimension.Angle, _ => PCellDimension.None }))];
    }

    public string? ContentKeyFor(string generatorId)
        => Lookup(generatorId) is { Count: 1 } dirs ? PCellPackage.ContentKeyOf(dirs[0]) : null;

    /// <summary>The scope a cell's parameter defaults evaluate in: its parameters and its VARs, as the planner binds them.</summary>
    private static Scope CellScope(string cellDir, CcellFile ccell)
    {
        var scope = new Scope("global");
        foreach (var p in ccell.Parameters) scope.Bind(p.Name, p.DefaultExpression, EngineUnit(p.Unit));
        foreach (var v in CellVariables.Of(cellDir))
            if (!ccell.Parameters.Any(p => p.Name == v.Name)) scope.Bind(v.Name, v.Expression, EngineUnit(v.Unit));
        return scope;
    }

    private static string? EngineUnit(string? unit) => UnitNormalizer.ToEngineUnit(unit) is { Length: > 0 } u ? u : null;

    // ── The generator ─────────────────────────────────────────────────────────

    // D4: the cells this thread is drawing, and the refusal every frame above a cycle rethrows.
    [ThreadStatic] private static List<string>? _drawing;
    [ThreadStatic] private static string? _cycle;

    private PCellResult Generate(string generatorId, IReadOnlyDictionary<string, PCellValue> parameters, Technology? technology)
    {
        string cellDir = Lookup(generatorId) is { Count: 1 } dirs ? dirs[0]
            : throw new InvalidOperationException($"{generatorId}: its designed cell is no longer found under {_root}.");
        if (technology is null)
            throw new InvalidOperationException($"{generatorId}: a designed cell is drawn on a technology, and none resolved.");

        var drawing = _drawing ??= [];
        if (drawing.Contains(cellDir, StringComparer.Ordinal))
            throw new InvalidOperationException(_cycle =
                $"{Path.GetFileName(cellDir)} places itself ({string.Join(" > ", drawing.Append(cellDir).Select(d => Path.GetFileName(d)))}), " +
                "so it cannot be drawn.");
        drawing.Add(cellDir);
        try
        {
            var ccell = CellFolder.ReadCcell(cellDir) ?? new CcellFile();
            var at = DesignedCellLayout.At(cellDir, ValuesFor(cellDir, ccell, parameters), _root, technology, TechIdentityOf(technology));
            if (_cycle is { } cycle) throw new InvalidOperationException(cycle);
            if (at.Layout is not { } layout) throw new InvalidOperationException(at.Refusal ?? $"{generatorId} could not be planned.");
            return Flatten(cellDir, ccell, layout, technology, at.Lines);
        }
        finally
        {
            drawing.RemoveAt(drawing.Count - 1);
            if (drawing.Count == 0) _cycle = null;
        }
    }

    /// <summary>The instance's values as <see cref="DesignedCellLayout.At"/> takes them: base SI. A parameter or VAR
    /// spelled in degrees arrives in degrees (the PCell convention) and leaves in radians (the expression engine's).</summary>
    internal static Dictionary<string, Value> ValuesFor(string cellDir, CcellFile ccell, IReadOnlyDictionary<string, PCellValue> parameters)
    {
        var units = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var v in CellVariables.Of(cellDir)) units[v.Name] = v.Unit;
        foreach (var p in ccell.Parameters) units[p.Name] = p.Unit;
        var values = new Dictionary<string, Value>(StringComparer.Ordinal);
        foreach (var (name, value) in parameters)
            if (value.Kind is PCellValueKind.Real or PCellValueKind.Int)
                values[name] = new Value(ToEngine(units.GetValueOrDefault(name), value.AsReal()));
        return values;
    }

    /// <summary>A PCell-convention value (m, deg) in the engine's base SI (m, rad).</summary>
    internal static double ToEngine(string? unit, double value)
        => UnitNormalizer.ToEngineUnit(unit) == "deg" ? value * Math.PI / 180.0 : value;

    /// <summary>D5: what nested cells are keyed on. A generator is handed the technology, not its file, so its content
    /// stands in for the file the store would otherwise read.</summary>
    private static string TechIdentityOf(Technology technology)
        => "cpcell-tech/" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(TechPersistence.Serialize(technology))))[..12];

    private static PCellResult Flatten(string cellDir, CcellFile ccell, LayoutView layout, Technology technology,
                                       IReadOnlyList<SchematicLayoutPlanner.ReportLine> lines)
    {
        // A generated cell is written at the default database unit; a cell planned at another would be drawn scaled.
        if (layout.DbuPerMicron != LayoutUnits.DefaultDbuPerMicron)
            throw new InvalidOperationException(
                $"{Path.GetFileName(cellDir)}'s layout is at {layout.DbuPerMicron} database units per micron; a designed cell is drawn at " +
                $"{LayoutUnits.DefaultDbuPerMicron}.");

        string layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        var shapes = new List<LayoutShape>();
        var pinsAt = new List<(double X, double Y, double Deg, LayoutPin Pin)>();
        var views = new Dictionary<string, (LayoutInstance Inst, LayoutView View)>(StringComparer.Ordinal);
        foreach (var inst in layout.Instances)
        {
            shapes.AddRange(LayoutFlatten.FlattenAllLevels(inst, layoutDir, technology).Shapes.Where(s => s is not LabelShape));
            if (CellLayoutResolver.Resolve(inst, layoutDir, technology).View is not { } view) continue;
            var pose = ChainPose.Of(inst);
            foreach (var pin in view.Pins)
            {
                var (x, y, deg) = pose.Apply(pin);
                pinsAt.Add((x, y, deg, pin));
            }
            if (inst.SchematicId is { Length: > 0 } sid) views.TryAdd(sid, (inst, view));
        }
        // The layout's own copper (ground vias), not its ground pour and not its labels.
        shapes.AddRange(layout.Shapes.Where(s => s is not LabelShape && s.Generated != GroundArtwork.PourTag).Select(LayoutGeometry.Clone));

        // One pin per Port label, named by its Num, on the part pin it was placed on (PortPlacement D1).
        var pins = new List<PCellPin>();
        var notes = lines.Where(l => l.Severity == SchematicLayoutPlanner.ReportSeverity.Warning).Select(l => l.Text).ToList();
        foreach (var label in layout.Shapes.OfType<LabelShape>().Where(l => l.IsPort)
                                     .OrderBy(l => int.TryParse(l.Text, out int n) ? n : int.MaxValue).ThenBy(l => l.Text, StringComparer.Ordinal))
        {
            var near = pinsAt.Where(p => Math.Abs(p.X - label.X) <= 2 && Math.Abs(p.Y - label.Y) <= 2)
                             .OrderBy(p => Math.Abs(p.X - label.X) + Math.Abs(p.Y - label.Y)).ToList();
            if (near.Count == 0) { notes.Add($"port {label.Text} is on no part's pin, so the cell has no pin {label.Text}"); continue; }
            var hit = near[0];
            pins.Add(new PCellPin(label.Text, (long)Math.Round(hit.X), (long)Math.Round(hit.Y), hit.Pin.Layer, hit.Pin.WidthDbu, hit.Deg));
        }

        return new PCellResult(shapes, pins, notes.Count > 0 ? notes : null, HandlesOf(cellDir, ccell, views, layout.DbuPerMicron));
    }

    /// <summary>One Length handle per <see cref="StretchBinding"/>: from the anchor vertex to the middle of the moving ones,
    /// along the binding's axis, all in the cell's frame. The vertices are the drawn part's own, as generated.</summary>
    private static IReadOnlyList<PCellHandle>? HandlesOf(string cellDir, CcellFile ccell,
                                                         Dictionary<string, (LayoutInstance Inst, LayoutView View)> views, int dbuPerMicron)
    {
        if (CellFolder.ResolvePrimary(cellDir, ViewType.Schematic).ResolvedName is not { } csch) return null;
        SchematicEditModel model;
        try { model = SchematicPersistence.LoadFromFile(Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Schematic), csch)).model; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException) { return null; }

        var handles = new List<PCellHandle>();
        double dbuPerMetre = dbuPerMicron * 1e6;
        foreach (var comp in model.Components.Where(c => c.Stretch is { Count: > 0 }))
        {
            if (!views.TryGetValue(comp.InstanceName, out var placed) || placed.View.PCellOrigin?.Parameters is not { } origin) continue;
            var pose = ChainPose.Of(placed.Inst);
            (double X, double Y)? Vertex(int i)
            {
                if (!origin.TryGetValue(DrawnMetal.X(i), out var x) || !origin.TryGetValue(DrawnMetal.Y(i), out var y)) return null;
                var (rx, ry) = ChainPose.Rotate((pose.Mirror ? -1 : 1) * x.AsReal() * dbuPerMetre, y.AsReal() * dbuPerMetre, pose.Deg);
                return (pose.X + rx, pose.Y + ry);
            }
            foreach (var binding in comp.Stretch!)
            {
                if (Vertex(binding.Anchor) is not { } anchor) continue;
                var moving = binding.Moving.Select(Vertex).OfType<(double X, double Y)>().ToList();
                if (moving.Count == 0) continue;
                var range = ccell.Parameters.FirstOrDefault(p => p.Name == binding.Parameter);
                handles.Add(new PCellHandle(
                    binding.Parameter, (long)Math.Round(anchor.X), (long)Math.Round(anchor.Y),
                    (long)Math.Round(moving.Average(m => m.X)), (long)Math.Round(moving.Average(m => m.Y)),
                    pose.Facing(binding.AxisDeg), Label: binding.Parameter, Min: range?.Minimum, Max: range?.Maximum,
                    Quantity: PCellHandleQuantity.Length));
            }
        }
        return handles.Count > 0 ? handles : null;
    }
}

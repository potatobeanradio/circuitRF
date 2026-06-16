using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// Turns a <see cref="SchematicEditModel"/> into a <see cref="TestBench"/> (Design model).
/// Headless, framework-free — no Avalonia, no Skia. Reuses ComputeConnectivityGeometry
/// for the geometric union (T-junctions, crossing predicate); applies same-name label union
/// on top (§2.1.6). Terminal order is the contract: NetBindings[k] = net at terminal k.
/// </summary>
public static class NetExtractor
{
    /// <param name="TestBench">The emitted Design model.</param>
    /// <param name="Conflicts">
    /// Non-fatal naming conflicts, e.g. two different label names on the same physical net.
    /// </param>
    public sealed record ExtractionResult(TestBench TestBench, IReadOnlyList<string> Conflicts)
    {
        /// <summary>
        /// Cell interface port names derived from Pin components, in ascending Num order.
        /// Differential pairs (Polarity=Plus/Minus sharing the same Num) each contribute two
        /// entries: "{base}+" then "{base}-". Single-ended Pins contribute one entry: Name or
        /// "P{Num}". Empty when no Pins are present (testbench-style schematics).
        /// These names are the values that must appear in <see cref="CircuitRF.Core.Design.Cell.Ports"/>
        /// for the elaborator's positional parentNetMap binding to work correctly.
        /// </summary>
        public IReadOnlyList<string> CellPorts { get; init; } = [];

        /// <summary>
        /// Library of Cell definitions built by recursively extracting cell-instance schematics.
        /// Empty for flat schematics (no cell instances) or when no resolver is provided.
        /// Cells are ordered leaf-first so <c>define</c>-before-use is satisfied.
        /// </summary>
        public Library Library { get; init; } = new("netlist");
    }

    public static ExtractionResult Extract(
        SchematicEditModel model, string testBenchName = "tb", ICellResolver? cells = null)
    {
        var lib        = new Library("netlist");
        var conflicts  = new List<string>();
        var inProgress = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var (instances, cellPorts, topVars) = ExtractModel(model, cells, lib, inProgress, conflicts);

        var tb = new TestBench(testBenchName);
        tb.Instances.AddRange(instances);
        tb.GlobalVariables.AddRange(topVars);

        // Analyses + measurements attach to the TOP testbench only (data-model §2.1 invariant).
        foreach (var analysis in model.Analyses)
            if (analysis.Enabled) tb.Analyses.Add(analysis);
        foreach (var measurement in model.Measurements)
            tb.Measurements.Add(measurement);

        return new ExtractionResult(tb, conflicts) { CellPorts = cellPorts, Library = lib };
    }

    // ── Per-model extraction pipeline (shared by top and sub-cells) ─────────

    private static (List<Instance> Instances, IReadOnlyList<string> CellPorts, List<Variable> Variables) ExtractModel(
        SchematicEditModel model,
        ICellResolver?      cells,
        Library             lib,
        HashSet<string>     inProgress,
        List<string>        conflicts)
    {
        double gs = model.GridSize;
        (long, long) QK(double x, double y) =>
            ((long)Math.Round(x / gs), (long)Math.Round(y / gs));

        var uf = new UnionFind();

        // Pre-resolve cell-ref symbol geometry for this model (mirrors BuildRenderModel.ResolveAllCellRefs).
        // Null when SchematicDirectory is not set; GetEffectivePortDefs falls back to SymbolPortDefs in that case.
        var cellRefResolutions = BuildCellRefResolutions(model);

        // Detached-port synthetic keys — each detached port gets a unique key that can never
        // be produced by QK() for any finite schematic coordinate, so it will never union with
        // the geometric P-cell it overlaps.  Its root is added to AssignNetNames explicitly so
        // it receives an auto-name like "n3" rather than silently falling through to "0".
        var detachedKeys = new Dictionary<(string CompId, int PortIndex), (long, long)>();
        long detachedSeq  = 0;
        void AddDetachedKey(string compId, int portIdx)
        {
            long s  = detachedSeq++;
            var dk  = (long.MinValue + s, s);   // x ≈ -9.2e18: unreachable by real P-cells
            detachedKeys[(compId, portIdx)] = dk;
            uf.Add(dk);
        }

        // ── Layer 1: union-find over on-P connection points ─────────────────

        // Seed: all component pins using cell-ref-aware port geometry.
        // Resolved cell-refs use .csym pin positions; unresolved fall back to SymbolPortDefs.
        foreach (var comp in model.Components)
        {
            foreach (var def in GetEffectivePortDefs(model, comp, cellRefResolutions))
            {
                if (comp.IsPortDetached(def.PortIndex)) { AddDetachedKey(comp.Id, def.PortIndex); continue; }
                var (px, py) = model.PortWorldOf(comp, def);
                uf.Add(QK(px, py));
            }
        }

        // Geometric connectivity (wire vertices + T-junctions + crossing dots), shared with the
        // editor's one-label-per-node rule so the two never disagree.
        AddGeometricUnions(model, QK, uf);

        // Short disable: union all non-detached terminal P-cells of shorted components.
        foreach (var comp in model.Components)
        {
            if (comp.Disable != DisableState.Short) continue;
            var defs = GetEffectivePortDefs(model, comp, cellRefResolutions);
            if (defs.Count < 2) continue;
            // Find the first non-detached port as the union anchor; skip all detached ports.
            (float LocalX, float LocalY, int PortIndex) firstNd = default;
            bool foundFirst = false;
            foreach (var d in defs)
            {
                if (!comp.IsPortDetached(d.PortIndex)) { firstNd = d; foundFirst = true; break; }
            }
            if (!foundFirst) continue;  // all ports detached — nothing to short
            var (x0, y0) = model.PortWorldOf(comp, firstNd);
            var key0 = QK(x0, y0);
            foreach (var d in defs)
            {
                if (d.PortIndex == firstNd.PortIndex) continue;
                if (comp.IsPortDetached(d.PortIndex)) continue;
                var (px, py) = model.PortWorldOf(comp, d);
                uf.Union(key0, QK(px, py));
            }
        }

        // Same-name net label union (§2.1.6): labels with identical names make one net,
        // even across physically-disjoint wires.
        var labelNetKeys = new Dictionary<EditableNetLabel, (long, long)?>();
        foreach (var lbl in model.NetLabels)
            labelNetKeys[lbl] = FindLabelNetKey(uf, QK, gs, model, lbl.X, lbl.Y);

        foreach (var grp in model.NetLabels.GroupBy(l => l.Name, StringComparer.Ordinal))
        {
            (long, long)? rep = null;
            foreach (var lbl in grp)
            {
                var k = labelNetKeys[lbl];
                if (k is null) continue;
                if (rep is null) { rep = k; continue; }
                uf.Union(rep.Value, k.Value);
                rep = k; // keep a reachable key for future unions in this group
            }
        }

        // ── Collect Pin info (before name assignment so Pin nets get port names) ───
        var pinInfos = CollectPinInfos(model, uf, QK);

        // Build pin→portName map: applied between net-label names and auto-names so that
        // the net at each Pin position is named after the port (enabling elaborator binding).
        var pinNetNameMap = BuildPinNetNameMap(pinInfos, uf);

        // ── Layer 2: assign stable net names ────────────────────────────────

        var netNames = AssignNetNames(uf, QK, gs, model, labelNetKeys, conflicts, detachedKeys.Values, pinNetNameMap, cellRefResolutions);

        // ── Compute CellPorts + conformance warnings ─────────────────────────
        var cellPorts = BuildCellPorts(pinInfos, conflicts);

        // ── Layer 3: emit instances ──────────────────────────────────────────

        var instances = new List<Instance>();

        // Validate Term Num uniqueness before emitting.
        var termNums = new Dictionary<int, string>(); // Num → first InstanceName
        foreach (var comp in model.Components)
        {
            if (comp.Symbol != SymbolKind.Term) continue;
            var numParam = comp.Parameters.FirstOrDefault(p => p.Name == "Num");
            if (numParam != null && int.TryParse(numParam.Expression, out int num))
            {
                if (termNums.TryGetValue(num, out var first))
                    conflicts.Add($"Duplicate Term Num={num} on \"{comp.InstanceName}\" (first: \"{first}\")");
                else
                    termNums[num] = comp.InstanceName;
            }
        }

        foreach (var comp in model.Components)
        {
            if (comp.Disable is DisableState.Open or DisableState.Short) continue;
            if (comp.Symbol == SymbolKind.Ground) continue;
            if (comp.Symbol == SymbolKind.Pin)    continue;
            if (comp.Symbol == SymbolKind.Var)    continue;  // VAR rows routed to Variables, not instances

            if (comp.CellRef is not null)
            {
                var ci = EmitCellInstance(comp, model, uf, QK, netNames, detachedKeys,
                                          cells, lib, inProgress, conflicts, cellRefResolutions);
                if (ci is not null) instances.Add(ci);
                continue;
            }

            var inst = EmitInstance(comp, model, cellRefResolutions, uf, QK, netNames, detachedKeys);
            if (inst is not null) instances.Add(inst);
        }

        // ── Collect VAR variable definitions for this frame ─────────────────
        var frameVars    = new List<Variable>();
        var varNamesSeen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var comp in model.Components)
        {
            if (comp.Disable is DisableState.Open or DisableState.Short) continue;
            if (comp.Symbol != SymbolKind.Var) continue;
            foreach (var p in comp.Parameters)
            {
                if (string.IsNullOrWhiteSpace(p.Name)) continue;
                var varName = p.Name.Trim();
                if (!varNamesSeen.Add(varName))
                {
                    conflicts.Add($"Variable '{varName}' defined more than once in this cell; first definition kept.");
                    continue;
                }
                string? unit = UnitNormalizer.ToEngineUnit(p.Unit) is { Length: > 0 } u ? u : null;
                frameVars.Add(new Variable(varName, p.Expression, unit));
            }
        }

        return (instances, cellPorts, frameVars);
    }

    // ── Cell instance emission ───────────────────────────────────────────────

    private static Instance? EmitCellInstance(
        EditableComponent comp,
        SchematicEditModel model,
        UnionFind uf,
        Func<double, double, (long, long)> QK,
        Dictionary<(long, long), string> netNames,
        Dictionary<(string CompId, int PortIndex), (long, long)> detachedKeys,
        ICellResolver? cells,
        Library lib,
        HashSet<string> inProgress,
        List<string> conflicts,
        Dictionary<string, CellSymbolResolution>? cellRefResolutions = null)
    {
        if (cells is null) return null;   // flat caller / no resolver — skip silently (back-compat)

        var res = cells.Resolve(comp, model);
        if (res is null)
        {
            conflicts.Add($"Cell instance '{comp.InstanceName}' (cell '{comp.CellRef}') has no " +
                          $"primary schematic; skipped.");
            return null;
        }

        var cellName = res.CellName;

        // Cycle guard: a cell currently being extracted up the stack instantiates itself.
        if (inProgress.Contains(cellName))
        {
            conflicts.Add($"Cell '{cellName}' instantiates itself (cycle); " +
                          $"instance '{comp.InstanceName}' skipped.");
            return null;
        }

        // Build the cell once (dedupe by name); children are added before parents → leaf-first lib.
        if (lib.Find(cellName) is null)
        {
            inProgress.Add(cellName);
            var (subInstances, subPorts, subVars) = ExtractModel(res.Schematic, cells, lib, inProgress, conflicts);
            var cell = new Cell(cellName);
            cell.Ports.AddRange(subPorts);
            cell.Instances.AddRange(subInstances);
            cell.Variables.AddRange(subVars);
            foreach (var p in res.Parameters) cell.Parameters.Add(p);
            lib.Cells.Add(cell);
            inProgress.Remove(cellName);
        }

        var cellDef = lib.Find(cellName)!;

        // Port geometry: use resolved .csym pins when available; fall back to SymbolPortDefs
        // for unresolvable cell-refs (SchematicDirectory not set or symbol not found).
        // Sorted by PortIndex so NetBindings[k] aligns with Cell.Ports[k].
        var pinDefs = GetEffectivePortDefs(model, comp, cellRefResolutions)
                          .OrderBy(d => d.PortIndex)
                          .ToList();

        // BINDING CONTRACT GUARD: resolved pin count must equal the cell's interface-pin count.
        if (cellDef.Ports.Count != pinDefs.Count)
        {
            conflicts.Add($"Cell '{cellName}' instance '{comp.InstanceName}': symbol exposes " +
                          $"{pinDefs.Count} port(s) but the cell defines {cellDef.Ports.Count} " +
                          $"interface pin(s); skipped.");
            return null;
        }

        // NetBindings = parent net at each pin, in PortIndex order (== Cell.Ports order).
        var nets = new List<string>(pinDefs.Count);
        foreach (var def in pinDefs)
        {
            var (px, py) = model.PortWorldOf(comp, def);
            nets.Add(NetForPort(comp, def.PortIndex, px, py, uf, QK, netNames, detachedKeys));
        }

        var overrides = comp.Parameters
            .Select(p =>
            {
                var unit = UnitNormalizer.ToEngineUnit(p.Unit);
                return new ParameterAssignment(p.Name, p.Expression, unit.Length > 0 ? unit : null);
            })
            .ToList();

        return new Instance(comp.InstanceName, cellName, nets, overrides);
    }

    // ── Shared geometric union helper ────────────────────────────────────────────

    /// <summary>
    /// Adds the GEOMETRIC connectivity unions to <paramref name="uf"/>: wire-vertex chains, T-junction
    /// auto-dots, and user-dot 4-way crossings — all from ComputeConnectivityGeometry, the single source
    /// of connectivity truth. Shared by ExtractModel and FindNodeLabel so the editor's one-label-per-node
    /// rule matches extraction. Does not seed component pins, shorts, or label unions; callers add those.
    /// </summary>
    private static void AddGeometricUnions(
        SchematicEditModel model, Func<double, double, (long, long)> QK, UnionFind uf)
    {
        // Wire vertices; consecutive vertices of one wire = one net.
        foreach (var wire in model.Wires)
        {
            var pts = wire.Points;
            if (pts.Count == 0) continue;
            var first = QK(pts[0].X, pts[0].Y);
            uf.Add(first);
            for (int i = 1; i < pts.Count; i++)
            {
                var next = QK(pts[i].X, pts[i].Y);
                uf.Add(next);
                uf.Union(first, next);
                first = next;
            }
        }

        var cg = model.ComputeConnectivityGeometry();

        // T-junction unions: an auto-dot key (a wire endpoint on another wire's interior) unions with it.
        foreach (var autoDotKey in cg.AutoDotKeys)
        {
            double wx = autoDotKey.Item1 * model.GridSize;
            double wy = autoDotKey.Item2 * model.GridSize;
            foreach (var wire in model.Wires)
            {
                var pts = wire.Points;
                for (int i = 0; i < pts.Count - 1; i++)
                {
                    if (SchematicGeometry.PointOnSegmentInterior(wx, wy,
                            pts[i].X, pts[i].Y, pts[i + 1].X, pts[i + 1].Y,
                            SchematicEditModel.ConnectTolerance))
                    {
                        uf.Union(autoDotKey, QK(pts[i].X, pts[i].Y));
                        break;
                    }
                }
            }
        }

        // User-dot crossing unions: a dot-gated 4-way crossing connects the wires through it.
        foreach (var dot in model.Dots)
        {
            if (!cg.IsCrossingAtDot(dot.X, dot.Y)) continue;
            (long, long)? firstKey = null;
            foreach (var wire in model.Wires)
            {
                var pts = wire.Points;
                bool onInterior = false;
                for (int i = 0; i < pts.Count - 1 && !onInterior; i++)
                    if (SchematicGeometry.PointOnSegmentInterior(dot.X, dot.Y,
                            pts[i].X, pts[i].Y, pts[i + 1].X, pts[i + 1].Y,
                            SchematicEditModel.ConnectTolerance))
                        onInterior = true;
                if (!onInterior) continue;
                var wireKey = QK(pts[0].X, pts[0].Y);
                if (firstKey is null) firstKey = wireKey;
                else uf.Union(firstKey.Value, wireKey);
            }
        }
    }

    /// <summary>
    /// Groups the model's net labels by physical node (wires connected via shared vertices, T-junctions,
    /// or dot crossings — the same connectivity as extraction). Returns ONLY nodes carrying more than one
    /// label — the merge set the editor must collapse. Each group is in NetLabels (creation) order, so
    /// element [0] is the label to keep.
    /// </summary>
    public static List<List<EditableNetLabel>> LabelsSharingNode(SchematicEditModel model)
    {
        var result = new List<List<EditableNetLabel>>();
        if (model.NetLabels.Count < 2) return result;

        double gs = model.GridSize;
        (long, long) QK(double x, double y) => ((long)Math.Round(x / gs), (long)Math.Round(y / gs));

        var uf = new UnionFind();
        AddGeometricUnions(model, QK, uf);

        var byRoot = new Dictionary<(long, long), List<EditableNetLabel>>();
        foreach (var lbl in model.NetLabels)
        {
            var k = FindLabelNetKey(uf, QK, gs, model, lbl.X, lbl.Y);
            if (k is null || !uf.Contains(k.Value)) continue;
            var root = uf.Find(k.Value);
            if (!byRoot.TryGetValue(root, out var list)) byRoot[root] = list = [];
            list.Add(lbl);
        }

        foreach (var g in byRoot.Values)
            if (g.Count > 1) result.Add(g);
        return result;
    }

    /// <summary>
    /// Returns a net label already present on the same electrical node as wire <paramref name="wireId"/>
    /// (connected via shared vertices, T-junctions, or dot crossings), excluding the label whose Id is
    /// <paramref name="exceptId"/>; null if the node carries no other label. Uses the same connectivity
    /// as extraction. The editor uses this to keep at most one label per node.
    /// </summary>
    public static EditableNetLabel? FindNodeLabel(
        SchematicEditModel model, string wireId, string? exceptId = null)
    {
        var target = model.FindWire(wireId);
        if (target is null || target.Points.Count == 0) return null;

        double gs = model.GridSize;
        (long, long) QK(double x, double y) => ((long)Math.Round(x / gs), (long)Math.Round(y / gs));

        var uf = new UnionFind();
        AddGeometricUnions(model, QK, uf);

        var targetKey = QK(target.Points[0].X, target.Points[0].Y);
        if (!uf.Contains(targetKey)) return null;
        var targetRoot = uf.Find(targetKey);

        foreach (var lbl in model.NetLabels)
        {
            if (exceptId is not null && lbl.Id == exceptId) continue;
            var k = FindLabelNetKey(uf, QK, gs, model, lbl.X, lbl.Y);
            if (k is null || !uf.Contains(k.Value)) continue;
            if (uf.Find(k.Value) == targetRoot) return lbl;
        }
        return null;
    }

    /// <summary>
    /// Returns every wire id on the same electrical node(s) as <paramref name="seedWireIds"/>: the full
    /// set of wires connected via shared vertices, T-junctions, and dot crossings (the same geometric
    /// connectivity as extraction). Seed ids that exist in the model are always included. A wire is one
    /// node end-to-end (AddGeometricUnions chains a wire's vertices into a single root), so this returns
    /// the connected-wire set for the touched net. Used by the crossing rubber-band to grab a whole net
    /// from a single touched wire.
    /// </summary>
    public static HashSet<string> ConnectedWireIds(SchematicEditModel model, IEnumerable<string> seedWireIds)
    {
        var result = new HashSet<string>();
        foreach (var id in seedWireIds)
            if (model.FindWire(id) is not null) result.Add(id);
        if (result.Count == 0 || model.Wires.Count == 0) return result;

        double gs = model.GridSize;
        (long, long) QK(double x, double y) => ((long)Math.Round(x / gs), (long)Math.Round(y / gs));

        var uf = new UnionFind();
        AddGeometricUnions(model, QK, uf);

        // A wire's first-vertex root identifies its net node; wires sharing a node share the root.
        var seedRoots = new HashSet<(long, long)>();
        foreach (var id in result)
        {
            var w = model.FindWire(id);
            if (w is null || w.Points.Count == 0) continue;
            var k = QK(w.Points[0].X, w.Points[0].Y);
            if (uf.Contains(k)) seedRoots.Add(uf.Find(k));
        }
        if (seedRoots.Count == 0) return result;

        foreach (var w in model.Wires)
        {
            if (w.Points.Count == 0) continue;
            var k = QK(w.Points[0].X, w.Points[0].Y);
            if (uf.Contains(k) && seedRoots.Contains(uf.Find(k)))
                result.Add(w.Id);
        }
        return result;
    }

    // ── Private helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Net name for port <paramref name="pi"/> of <paramref name="comp"/> at world (px, py).
    /// Detached ports use their synthetic key so they never share a net with their P-cell.
    /// Shared by EmitInstance (primitives) and EmitCellInstance.
    /// </summary>
    private static string NetForPort(
        EditableComponent comp, int pi, double px, double py,
        UnionFind uf,
        Func<double, double, (long, long)> QK,
        Dictionary<(long, long), string> netNames,
        Dictionary<(string CompId, int PortIndex), (long, long)> detachedKeys)
    {
        if (comp.IsPortDetached(pi) && detachedKeys.TryGetValue((comp.Id, pi), out var dk))
            return NetAtKey(uf, netNames, dk);
        return NetAt(uf, QK, netNames, px, py);
    }

    /// <summary>
    /// Find the union-find key for a net label at (lx, ly).
    /// First checks exact coincidence (label placed on a wire vertex or pin);
    /// then scans wire segments with gs/2 tolerance (label placed mid-span).
    /// Returns null if the label is not on any wire or pin.
    /// </summary>
    private static (long, long)? FindLabelNetKey(
        UnionFind uf,
        Func<double, double, (long, long)> QK,
        double gs,
        SchematicEditModel model,
        double lx, double ly)
    {
        var lKey = QK(lx, ly);
        if (uf.Contains(lKey)) return lKey;

        // Label not on a vertex — scan wire segments.
        double tol = gs / 2.0;
        foreach (var wire in model.Wires)
        {
            var pts = wire.Points;
            for (int i = 0; i < pts.Count - 1; i++)
            {
                if (SchematicGeometry.PointOnSegment(
                        lx, ly, pts[i].X, pts[i].Y, pts[i + 1].X, pts[i + 1].Y, tol))
                    return QK(pts[i].X, pts[i].Y);
            }
        }
        return null;
    }

    /// <summary>
    /// Assigns a stable name to each union-find root.
    /// Priority: ground → "0"; net-label name; auto-stable n1/n2/…
    /// </summary>
    private static Dictionary<(long, long), string> AssignNetNames(
        UnionFind uf,
        Func<double, double, (long, long)> QK,
        double gs,
        SchematicEditModel model,
        Dictionary<EditableNetLabel, (long, long)?> labelNetKeys,
        List<string> conflicts,
        IEnumerable<(long, long)>? extraRoots = null,
        IReadOnlyDictionary<(long, long), string>? pinNetNameMap = null,
        Dictionary<string, CellSymbolResolution>? cellRefResolutions = null)
    {
        var rootToName = new Dictionary<(long, long), string>();

        // Ground components → net "0".
        foreach (var comp in model.Components)
        {
            if (comp.Symbol != SymbolKind.Ground) continue;
            var (px, py) = comp.GetPortWorldCoord(0);
            var k = QK(px, py);
            if (!uf.Contains(k)) continue;
            rootToName[uf.Find(k)] = "0";
        }

        // Net labels — applied after ground so "0" always wins a ground-label conflict.
        foreach (var lbl in model.NetLabels)
        {
            var k = labelNetKeys[lbl];
            if (k is null || !uf.Contains(k.Value)) continue;
            var root = uf.Find(k.Value);
            if (rootToName.TryGetValue(root, out var existing))
            {
                if (existing != "0" && existing != lbl.Name)
                    conflicts.Add(
                        $"Net conflict: labels '{existing}' and '{lbl.Name}' on same net");
            }
            else
                rootToName[root] = lbl.Name;
        }

        // Pin port names — a Pin OWNS its net's name (beats a coincident label); ground still wins.
        if (pinNetNameMap is not null)
        {
            foreach (var (key, portName) in pinNetNameMap)
            {
                if (!uf.Contains(key)) continue;
                var root = uf.Find(key);
                if (rootToName.TryGetValue(root, out var existing) && existing == "0")
                {
                    // Pin sits on the ground net — interface can't bind to the parent. Warn, keep "0".
                    conflicts.Add($"Pin net '{portName}' is tied to ground inside the cell; " +
                                  $"its interface will not connect to the parent.");
                    continue;
                }
                rootToName[root] = portName;   // override any label/auto name
            }
        }

        // Auto-names: deterministic stable order — component list order, then PortIndex order.
        var seen = new HashSet<(long, long)>();
        var orderedRoots = new List<(long, long)>();

        foreach (var comp in model.Components)
        {
            foreach (var def in GetEffectivePortDefs(model, comp, cellRefResolutions))
            {
                var (px, py) = model.PortWorldOf(comp, def);
                var k = QK(px, py);
                if (!uf.Contains(k)) continue;
                var root = uf.Find(k);
                if (!rootToName.ContainsKey(root) && seen.Add(root))
                    orderedRoots.Add(root);
            }
        }
        foreach (var wire in model.Wires)
        {
            if (wire.Points.Count == 0) continue;
            var k = QK(wire.Points[0].X, wire.Points[0].Y);
            if (!uf.Contains(k)) continue;
            var root = uf.Find(k);
            if (!rootToName.ContainsKey(root) && seen.Add(root))
                orderedRoots.Add(root);
        }

        // Detached-port unique keys: their roots never appear in the component/wire P-cell scan
        // above, so add them explicitly to ensure they receive auto-names (not fall through to "0").
        if (extraRoots is not null)
        {
            foreach (var dk in extraRoots)
            {
                if (!uf.Contains(dk)) continue;
                var root = uf.Find(dk);
                if (!rootToName.ContainsKey(root) && seen.Add(root))
                    orderedRoots.Add(root);
            }
        }

        int idx = 1;
        foreach (var root in orderedRoots)
            rootToName[root] = $"n{idx++}";

        return rootToName;
    }

    /// <summary>
    /// Returns the name of the net at world position (x, y), or "0" if the point is
    /// not in the union-find (unconnected pin → treat as ground for safety).
    /// </summary>
    private static string NetAt(
        UnionFind uf,
        Func<double, double, (long, long)> QK,
        Dictionary<(long, long), string> names,
        double x, double y)
    {
        var k = QK(x, y);
        if (!uf.Contains(k)) return "0";
        var root = uf.Find(k);
        return names.TryGetValue(root, out var n) ? n : "0";
    }

    /// <summary>Net name for a specific union-find key, or "0" if the key is absent.</summary>
    private static string NetAtKey(UnionFind uf, Dictionary<(long, long), string> names, (long, long) key)
    {
        if (!uf.Contains(key)) return "0";
        var root = uf.Find(key);
        return names.TryGetValue(root, out var n) ? n : "0";
    }

    private static Instance? EmitInstance(
        EditableComponent comp,
        SchematicEditModel model,
        Dictionary<string, CellSymbolResolution>? cellRefResolutions,
        UnionFind uf,
        Func<double, double, (long, long)> QK,
        Dictionary<(long, long), string> netNames,
        Dictionary<(string CompId, int PortIndex), (long, long)> detachedKeys)
    {
        var reference = ComponentTypeRegistry.EngineReference(comp.Symbol, comp.PortCount);
        // Keep built-in portDefs for ZPort "ref" name detection; geometry goes through PortWorldOf.
        var portDefs  = SymbolPortDefs.For(comp.Symbol, comp.PortCount);
        var overrides = comp.Parameters
            .Select(p =>
            {
                var unit = UnitNormalizer.ToEngineUnit(p.Unit);
                return new ParameterAssignment(p.Name, p.Expression, unit.Length > 0 ? unit : null);
            })
            .ToList();

        // ZPort: N signal pins + 1 "ref" pin → NetBindings[0..N-1] + RefNetBinding.
        if (comp.Symbol == SymbolKind.ZPort)
        {
            var bindings = new List<string>();
            string? refNet = null;
            foreach (var def in GetEffectivePortDefs(model, comp, cellRefResolutions))
            {
                var (px, py) = model.PortWorldOf(comp, def);
                var net = NetForPort(comp, def.PortIndex, px, py, uf, QK, netNames, detachedKeys);
                if (portDefs[def.PortIndex].Name == "ref")
                    refNet = net == "0" ? null : net;
                else
                    bindings.Add(net);
            }
            return new Instance(comp.InstanceName, reference, bindings, overrides)
                   { RefNetBinding = refNet };
        }

        // All built-in primitives: emit terminals in PortIndex order.
        var nets = new List<string>(portDefs.Length);
        foreach (var def in GetEffectivePortDefs(model, comp, cellRefResolutions))
        {
            var (px, py) = model.PortWorldOf(comp, def);
            nets.Add(NetForPort(comp, def.PortIndex, px, py, uf, QK, netNames, detachedKeys));
        }
        return new Instance(comp.InstanceName, reference, nets, overrides);
    }

    // ── Cell-ref-aware port geometry helpers ─────────────────────────────────

    /// <summary>
    /// Builds the per-model cell-ref resolution map (mirrors BuildRenderModel.ResolveAllCellRefs).
    /// Returns null when <paramref name="model"/>.SchematicDirectory is not set.
    /// </summary>
    private static Dictionary<string, CellSymbolResolution>? BuildCellRefResolutions(
        SchematicEditModel model)
    {
        if (model.SchematicDirectory is null) return null;
        Dictionary<string, CellSymbolResolution>? result = null;
        foreach (var comp in model.Components)
        {
            if (comp.CellRef is null) continue;
            result ??= new Dictionary<string, CellSymbolResolution>(StringComparer.Ordinal);
            result[comp.Id] = CellSymbolResolver.Resolve(comp.CellRef, model.SchematicDirectory);
        }
        return result;
    }

    /// <summary>
    /// Cell-ref-aware port definitions for a component in PortIndex order.
    /// Uses resolved .csym pins when available (SchematicDirectory set + symbol found);
    /// falls back to built-in SymbolPortDefs for unresolvable cell-refs so existing
    /// schematics without SchematicDirectory set continue to extract correctly.
    /// For built-in (non-cell-ref) components, PortDefsOf already returns SymbolPortDefs.
    /// </summary>
    private static IReadOnlyList<(float LocalX, float LocalY, int PortIndex)> GetEffectivePortDefs(
        SchematicEditModel model, EditableComponent comp,
        Dictionary<string, CellSymbolResolution>? resolutions)
    {
        var defs = model.PortDefsOf(comp, resolutions);
        if (defs.Count > 0) return defs;

        // Fallback for unresolvable cell-refs: use built-in placeholder geometry.
        // This keeps pre-fix behavior for models without SchematicDirectory set.
        var portDefs = SymbolPortDefs.For(comp.Symbol, comp.PortCount);
        var result   = new (float LocalX, float LocalY, int PortIndex)[portDefs.Length];
        for (int i = 0; i < portDefs.Length; i++)
            result[i] = (portDefs[i].LocalX, portDefs[i].LocalY, i);
        return result;
    }

    // ── Pin helpers ──────────────────────────────────────────────────────────

    private readonly record struct PinInfo(int Num, string Name, string Polarity, (long, long) Key);

    /// <summary>
    /// Collects all valid Pin instances from the schematic: those with a positive Num and a
    /// connected (non-detached) port that is in the union-find.
    /// </summary>
    private static List<PinInfo> CollectPinInfos(
        SchematicEditModel model,
        UnionFind uf,
        Func<double, double, (long, long)> QK)
    {
        var infos = new List<PinInfo>();
        foreach (var comp in model.Components)
        {
            if (comp.Symbol != SymbolKind.Pin) continue;
            if (comp.IsPortDetached(0)) continue;

            var numParam  = comp.Parameters.FirstOrDefault(p => p.Name == "Num");
            var nameParam = comp.Parameters.FirstOrDefault(p => p.Name == "Name");
            var polParam  = comp.Parameters.FirstOrDefault(p => p.Name == "Polarity");

            if (!int.TryParse(numParam?.Expression ?? "0", out int num) || num <= 0) continue;

            var (px, py) = comp.GetPortWorldCoord(0);
            var k = QK(px, py);
            if (!uf.Contains(k)) continue;

            infos.Add(new PinInfo(
                num,
                nameParam?.Expression?.Trim() ?? "",
                polParam?.Expression?.Trim() ?? "",
                k));
        }
        return infos;
    }

    /// <summary>
    /// Builds a map from each Pin's union-find key to its port name string.
    /// Single-ended Pin: port name = Name (if non-empty) else "P{Num}".
    /// Differential pair (Polarity=Plus and Polarity=Minus, same Num): "{base}+" / "{base}-".
    /// </summary>
    private static IReadOnlyDictionary<(long, long), string> BuildPinNetNameMap(
        List<PinInfo> pinInfos,
        UnionFind uf)
    {
        var map = new Dictionary<(long, long), string>();
        foreach (var pin in pinInfos)
        {
            var root = uf.Find(pin.Key);
            if (map.ContainsKey(root)) continue; // first one wins for net-name priority

            string baseName = pin.Name.Length > 0 ? pin.Name : $"P{pin.Num}";
            string portName = pin.Polarity.Equals("Plus",  StringComparison.OrdinalIgnoreCase) ? baseName + "+" :
                              pin.Polarity.Equals("Minus", StringComparison.OrdinalIgnoreCase) ? baseName + "-" :
                              baseName;

            map[root] = portName;
        }
        return map;
    }

    /// <summary>
    /// Builds the ordered CellPorts list from PinInfos. Sorts by Num; differential pairs
    /// (Polarity Plus/Minus sharing a Num) contribute "{base}+" then "{base}-" in that order.
    /// Adds conformance conflict messages for duplicate non-differential Nums and Num gaps.
    /// </summary>
    private static IReadOnlyList<string> BuildCellPorts(List<PinInfo> pinInfos, List<string> conflicts)
    {
        if (pinInfos.Count == 0) return [];

        // Group by Num.
        var byNum = pinInfos
            .GroupBy(p => p.Num)
            .OrderBy(g => g.Key)
            .ToList();

        var ports = new List<string>();

        foreach (var grp in byNum)
        {
            int num = grp.Key;
            var items = grp.ToList();

            var plusItem  = items.FirstOrDefault(p => p.Polarity.Equals("Plus",  StringComparison.OrdinalIgnoreCase));
            var minusItem = items.FirstOrDefault(p => p.Polarity.Equals("Minus", StringComparison.OrdinalIgnoreCase));

            bool isDiff = plusItem.Key != default && minusItem.Key != default;

            if (isDiff)
            {
                string baseName = plusItem.Name.Length > 0 ? plusItem.Name :
                                  minusItem.Name.Length > 0 ? minusItem.Name :
                                  $"P{num}";
                // Strip trailing + or - from the Name if the user set it that way.
                if (baseName.EndsWith('+') || baseName.EndsWith('-'))
                    baseName = baseName[..^1];
                ports.Add(baseName + "+");
                ports.Add(baseName + "-");

                // Extra items beyond the pair = conflict.
                int extras = items.Count - 2;
                if (extras > 0)
                    conflicts.Add($"Pin Num={num} has {items.Count} Pins; differential pair expects exactly 2.");
            }
            else
            {
                if (items.Count > 1)
                    conflicts.Add($"Duplicate Pin Num={num} ({items.Count} Pins share this number).");

                var first = items[0];
                string portName = first.Name.Length > 0 ? first.Name : $"P{num}";
                ports.Add(portName);
            }
        }

        // Check for gaps in the Num sequence (1, 2, 3, …).
        var nums = byNum.Select(g => g.Key).ToHashSet();
        int maxNum = byNum[^1].Key;
        for (int i = 1; i < maxNum; i++)
        {
            if (!nums.Contains(i))
                conflicts.Add($"Pin Num={i} is missing (gap in sequence 1..{maxNum}).");
        }

        return ports;
    }

    // ── Union-Find ───────────────────────────────────────────────────────────────

    private sealed class UnionFind
    {
        private readonly Dictionary<(long, long), (long, long)> _parent = new();

        public void Add((long, long) key)
        {
            if (!_parent.ContainsKey(key)) _parent[key] = key;
        }

        public bool Contains((long, long) key) => _parent.ContainsKey(key);

        public (long, long) Find((long, long) x)
        {
            while (true)
            {
                var p = _parent[x];
                if (p == x) return x;
                var gp = _parent[p];
                _parent[x] = gp; // path compression (halving)
                x = p;
            }
        }

        public void Union((long, long) a, (long, long) b)
        {
            Add(a); Add(b);
            var ra = Find(a);
            var rb = Find(b);
            if (ra != rb) _parent[rb] = ra;
        }
    }
}

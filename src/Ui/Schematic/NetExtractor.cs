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
    public sealed record ExtractionResult(TestBench TestBench, IReadOnlyList<string> Conflicts);

    public static ExtractionResult Extract(SchematicEditModel model, string testBenchName = "tb")
    {
        double gs = model.GridSize;
        (long, long) QK(double x, double y) =>
            ((long)Math.Round(x / gs), (long)Math.Round(y / gs));

        var uf = new UnionFind();

        // ── Layer 1: union-find over on-P connection points ─────────────────

        // Seed: all component pins.
        foreach (var comp in model.Components)
        {
            var portDefs = SymbolPortDefs.For(comp.Symbol, comp.PortCount);
            for (int pi = 0; pi < portDefs.Length; pi++)
            {
                var (px, py) = comp.GetPortWorldCoord(pi);
                uf.Add(QK(px, py));
            }
        }

        // Seed + union: wire vertices; consecutive vertices of one wire = one net.
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

        // T-junctions and crossing predicate from the single source of connectivity truth.
        var cg = model.ComputeConnectivityGeometry();

        // T-junction unions: auto-dot key is a wire endpoint that lies on another wire's interior.
        // Union the auto-dot key with every wire whose interior it lies on.
        foreach (var autoDotKey in cg.AutoDotKeys)
        {
            double wx = autoDotKey.Item1 * gs;
            double wy = autoDotKey.Item2 * gs;
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

        // User-dot crossing unions: dot-gated 4-way crossing connects the two wires.
        foreach (var dot in model.Dots)
        {
            if (!cg.IsCrossingAtDot(dot.X, dot.Y)) continue;
            (long, long)? firstKey = null;
            foreach (var wire in model.Wires)
            {
                var pts = wire.Points;
                bool onInterior = false;
                for (int i = 0; i < pts.Count - 1 && !onInterior; i++)
                {
                    if (SchematicGeometry.PointOnSegmentInterior(dot.X, dot.Y,
                            pts[i].X, pts[i].Y, pts[i + 1].X, pts[i + 1].Y,
                            SchematicEditModel.ConnectTolerance))
                        onInterior = true;
                }
                if (!onInterior) continue;
                var wireKey = QK(pts[0].X, pts[0].Y);
                if (firstKey is null) firstKey = wireKey;
                else uf.Union(firstKey.Value, wireKey);
            }
        }

        // Short disable: union all terminal P-cells of shorted components.
        foreach (var comp in model.Components)
        {
            if (comp.Disable != DisableState.Short) continue;
            var portDefs = SymbolPortDefs.For(comp.Symbol, comp.PortCount);
            if (portDefs.Length < 2) continue;
            var (x0, y0) = comp.GetPortWorldCoord(0);
            var key0 = QK(x0, y0);
            for (int pi = 1; pi < portDefs.Length; pi++)
            {
                var (px, py) = comp.GetPortWorldCoord(pi);
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

        // ── Layer 2: assign stable net names ────────────────────────────────

        var conflicts = new List<string>();
        var netNames = AssignNetNames(uf, QK, gs, model, labelNetKeys, conflicts);

        // ── Layer 3: emit TestBench ──────────────────────────────────────────

        var tb = new TestBench(testBenchName);

        foreach (var comp in model.Components)
        {
            if (comp.Disable is DisableState.Open or DisableState.Short) continue;
            if (comp.Symbol == SymbolKind.Ground) continue;
            if (comp.CellRef is not null) continue; // hierarchical extraction deferred to step 2

            var inst = EmitInstance(comp, uf, QK, netNames);
            if (inst is not null) tb.Instances.Add(inst);
        }

        // ── Layer 4: carry enabled analyses + all measurements ──────────────────
        foreach (var analysis in model.Analyses)
            if (analysis.Enabled)
                tb.Analyses.Add(analysis);

        foreach (var measurement in model.Measurements)
            tb.Measurements.Add(measurement);

        return new ExtractionResult(tb, conflicts);
    }

    // ── Private helpers ──────────────────────────────────────────────────────

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
        List<string> conflicts)
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

        // Auto-names: deterministic stable order — component list order, then pin index.
        var seen = new HashSet<(long, long)>();
        var orderedRoots = new List<(long, long)>();

        foreach (var comp in model.Components)
        {
            var portDefs = SymbolPortDefs.For(comp.Symbol, comp.PortCount);
            for (int pi = 0; pi < portDefs.Length; pi++)
            {
                var (px, py) = comp.GetPortWorldCoord(pi);
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

    private static Instance? EmitInstance(
        EditableComponent comp,
        UnionFind uf,
        Func<double, double, (long, long)> QK,
        Dictionary<(long, long), string> netNames)
    {
        var reference = ComponentTypeRegistry.EngineReference(comp.Symbol, comp.PortCount);
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
            for (int pi = 0; pi < portDefs.Length; pi++)
            {
                var (px, py) = comp.GetPortWorldCoord(pi);
                var net = NetAt(uf, QK, netNames, px, py);
                if (portDefs[pi].Name == "ref")
                    refNet = net == "0" ? null : net;
                else
                    bindings.Add(net);
            }
            return new Instance(comp.InstanceName, reference, bindings, overrides)
                   { RefNetBinding = refNet };
        }

        // Port: schematic shows 1 pin (signal); engine needs 2 nets [signal, "0"].
        if (comp.Symbol == SymbolKind.Port)
        {
            var (px, py) = comp.GetPortWorldCoord(0);
            var sigNet = NetAt(uf, QK, netNames, px, py);
            return new Instance(comp.InstanceName, reference, [sigNet, "0"], overrides);
        }

        // All other built-in primitives: emit terminals in symbol order.
        var nets = new List<string>(portDefs.Length);
        for (int pi = 0; pi < portDefs.Length; pi++)
        {
            var (px, py) = comp.GetPortWorldCoord(pi);
            nets.Add(NetAt(uf, QK, netNames, px, py));
        }
        return new Instance(comp.InstanceName, reference, nets, overrides);
    }

    // ── Union-Find ───────────────────────────────────────────────────────────

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

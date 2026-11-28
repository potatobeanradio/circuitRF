// Owner: PCD-13 (brief-pcd-13 R-pcd13-3). Framework-free.
//
// The history applied to a planned designed cell, in DesignedCellLayout.At after the planner: every operand as Clipper2
// regions per layer, each operation a boolean or an offset on them (LayoutClipper's conversion, Clipper2's operations),
// then the result drawn as D5 says. Nothing is written but a shape-less copy of each changed part's generated cell.

using Clipper2Lib;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Extraction;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Design.Layout.SchematicSync;
using CircuitRF.Design.Schematic;
using ReportLine = CircuitRF.Design.Layout.SchematicSync.SchematicLayoutPlanner.ReportLine;
using ReportSeverity = CircuitRF.Design.Layout.SchematicSync.SchematicLayoutPlanner.ReportSeverity;

namespace CircuitRF.Design.PCellDesigner.Operations;

internal static class PCellOperationsEngine
{
    private const FillRule Rule = FillRule.NonZero;

    /// <summary>A placed part: its instance, its copper per layer as planned and as the history has it now, and its
    /// shapes no region holds (vias), which follow it unchanged.</summary>
    private sealed class Part(string id, LayoutInstance inst, bool tool)
    {
        public string Id { get; } = id;
        public LayoutInstance Inst { get; } = inst;
        public bool Tool { get; } = tool;
        public Dictionary<LayerKey, Paths64> Planned { get; } = [];
        public Dictionary<LayerKey, Paths64> Now { get; set; } = [];
        public List<LayoutShape> Points { get; } = [];
    }

    private sealed record Pin(string Part, string Name, double X, double Y, LayerKey Layer, long ProbeX, long ProbeY);

    /// <summary>
    /// Applies <paramref name="model"/>'s history to <paramref name="layout"/> (the planner's output for it, values already
    /// applied), adding its report lines to <paramref name="lines"/>. A cell with no history and no tool is left exactly as
    /// planned.
    /// </summary>
    public static void Apply(LayoutView layout, SchematicEditModel model, string layoutDir, string workspaceRootDir,
                             Technology technology, List<ReportLine> lines)
    {
        var ops = model.PCellOperations ?? [];
        var tools = model.Components.Where(PCellOperations.IsTool).Select(c => c.InstanceName).ToHashSet(StringComparer.Ordinal);
        if (ops.Count == 0 && tools.Count == 0) return;
        // A tool is drawn over the copper it cuts: that overlap is its purpose, not a placement fault (D2).
        lines.RemoveAll(l => l.Severity == ReportSeverity.Warning && (l.Text.Contains(" overlaps ") || l.Text.Contains(" touches "))
                             && tools.Any(t => l.InstanceName == t || l.Text.StartsWith(t + " ", StringComparison.Ordinal)
                                               || l.Text.Contains(" " + t + " ", StringComparison.Ordinal)));
        var steps = new List<PCellOperationStep>();
        var spans = new List<PCellOperationSpan>();

        var scope = new Scope("global");
        foreach (var v in NetExtractor.Extract(model, "tb", DiskCellResolver.Instance).TestBench.GlobalVariables)
            scope.Bind(v.Name, v.Expression, v.Unit);
        var evaluator = new Evaluator();
        double dbuPerMetre = layout.DbuPerMicron * 1e6;

        // ── The parts' copper, the pins and the ports, as planned ─────────────
        var parts = new List<Part>();
        var pins = new List<Pin>();
        foreach (var inst in layout.Instances)
        {
            if (inst.SchematicId is not { Length: > 0 } id) continue;
            var part = new Part(id, inst, tools.Contains(id));
            foreach (var s in LayoutFlatten.FlattenAllLevels(inst, layoutDir, technology).Shapes)
            {
                if (s is LabelShape) continue;
                if (!LayoutBooleans.IsClipperOperand(s)) { part.Points.Add(s); continue; }
                var paths = LayoutClipper.ToClipperPaths(s, LayoutFlattener.ResolveTolDbu(s, technology));
                part.Planned[s.Layer] = part.Planned.TryGetValue(s.Layer, out var had) ? [.. had, .. paths] : paths;
            }
            foreach (var key in part.Planned.Keys.ToList()) part.Planned[key] = Clipper.Union(part.Planned[key], Rule);
            part.Now = Copy(part.Planned);
            parts.Add(part);
            if (!part.Tool) pins.AddRange(PinsOf(inst, id, layoutDir, technology, layout.DbuPerMicron));
        }
        var ports = PortsOf(layout, pins);
        var vias = layout.Shapes.OfType<ViaShape>().Cast<LayoutShape>().Concat(parts.SelectMany(p => p.Points.OfType<ViaShape>())).ToList();

        var copper = new Dictionary<LayerKey, Paths64>();
        foreach (var part in parts.Where(p => !p.Tool))
            foreach (var (key, paths) in part.Planned) copper[key] = copper.TryGetValue(key, out var had) ? [.. had, .. paths] : paths;
        foreach (var key in copper.Keys.ToList()) copper[key] = Clipper.Union(copper[key], Rule);

        var coveredAtStart = pins.Where(p => Covers(copper, p)).ToList();
        var joinedAtStart = Joined(copper, vias, ports, technology);

        // ── The operations, in order ──────────────────────────────────────────
        foreach (var op in ops)
        {
            if (op.Suppressed) { steps.Add(new(op.Id, false, "suppressed", [])); continue; }
            string? why;
            Paths64? made = null;
            var trialCopper = Copy(copper);
            var trialNow = parts.ToDictionary(p => p, p => Copy(p.Now));
            var opSpans = new List<PCellOperationSpan>();
            try { why = Run(op, out made); }
            catch (ExpressionException e) { why = $"an expression does not evaluate: {e.Message}"; }

            if (why is null && coveredAtStart.FirstOrDefault(p => !Covers(trialCopper, p)) is { } lost)
                why = $"it removes the copper under {lost.Part} pin {lost.Name}; a part's pins stay where the part put them";
            if (why is null && Joined(trialCopper, vias, ports, technology) is var joined && joined != joinedAtStart)
                why = $"it changes which ports the copper joins (planned: {joinedAtStart}; now: {joined})";

            if (why is not null)
            {
                lines.Add(new ReportLine("", $"{op.Id} ({op.Kind}) was not applied: {why}.", ReportSeverity.Warning) { Ids = [op.Id] });
                steps.Add(new(op.Id, false, why, []));
                continue;
            }
            copper = trialCopper;
            foreach (var p in parts) p.Now = trialNow[p];
            spans.AddRange(opSpans);
            steps.Add(new(op.Id, true, null, made is null ? [] : Shapes(made, LayerOf(op.Kind == PCellOperationKind.Grow ? op.Onto ?? op.Layer : op.Layer))));

            // One operation on the trial state; null when done, else why not.
            string? Run(PCellOperation op, out Paths64? result)
            {
                result = null;
                var layer = LayerOf(op.Layer);
                if (Operand(op.A, layer, out var a) is { } badA) return badA;
                var all = trialCopper.GetValueOrDefault(layer) ?? [];
                switch (op.Kind)
                {
                    case PCellOperationKind.Grow:
                    {
                        double d = Si(op.Amount, op.Unit) * dbuPerMetre;
                        result = Clipper.InflatePaths(a, d, JoinType.Miter, EndType.Polygon);
                        var onto = op.Onto is { Length: > 0 } o ? LayerOf(o) : layer;
                        if (onto == layer)
                        {
                            trialCopper[layer] = Clipper.Union(Clipper.Difference(all, a, Rule), result, Rule);
                            foreach (var p in OperandParts(op.A))
                                if (trialNow[p].TryGetValue(layer, out var own))
                                    trialNow[p][layer] = Clipper.InflatePaths(own, d, JoinType.Miter, EndType.Polygon);
                        }
                        else trialCopper[onto] = Clipper.Union(trialCopper.GetValueOrDefault(onto) ?? [], result, Rule);
                        if (Names(op.Amount) && Clipper.GetBounds(a) is { } box && !box.IsEmpty())
                            opSpans.Add(new(op.Amount!.Trim(), op.Unit, box.right, (box.top + box.bottom) / 2,
                                            box.right + (long)Math.Round(d), (box.top + box.bottom) / 2));
                        break;
                    }
                    case PCellOperationKind.Repeat:
                    {
                        int rows = (int)Math.Round(Si(op.Rows, "")), cols = (int)Math.Round(Si(op.Columns, ""));
                        if (rows < 1 || cols < 1 || (long)rows * cols > 10_000) return $"Repeat makes {rows} x {cols} copies; each count is 1 or more, 10000 in all at most";
                        long px = (long)Math.Round(Si(op.PitchX, op.Unit) * dbuPerMetre), py = (long)Math.Round(Si(op.PitchY, op.Unit) * dbuPerMetre);
                        result = [];
                        for (int j = 0; j < rows; j++)
                            for (int i = 0; i < cols; i++)
                                if (i != 0 || j != 0) result.AddRange(Clipper.TranslatePaths(a, i * px, j * py));
                        result = Clipper.Union(result, Rule);
                        trialCopper[layer] = Clipper.Union(all, result, Rule);
                        if (Clipper.GetBounds(a) is { } box && !box.IsEmpty())
                        {
                            if (cols > 1 && Names(op.PitchX)) opSpans.Add(new(op.PitchX!.Trim(), op.Unit, box.left, box.bottom, box.left + px, box.bottom));
                            if (rows > 1 && Names(op.PitchY)) opSpans.Add(new(op.PitchY!.Trim(), op.Unit, box.left, box.top, box.left, box.top + py));
                        }
                        break;
                    }
                    default:
                    {
                        if (Operand(op.B ?? [], layer, out var b) is { } badB) return badB;
                        result = op.Kind switch
                        {
                            PCellOperationKind.Merge     => Clipper.Union(a, b, Rule),
                            PCellOperationKind.Subtract  => Clipper.Difference(a, b, Rule),
                            PCellOperationKind.Intersect => Clipper.Intersect(a, b, Rule),
                            _                            => Clipper.Xor(a, b, Rule),
                        };
                        trialCopper[layer] = Clipper.Union(Clipper.Difference(all, a, Rule), result, Rule);
                        break;
                    }
                }
                // Each part keeps what of its copper the layer still has (D4: the result replaces A).
                foreach (var p in parts.Where(p => !p.Tool))
                    foreach (var key in trialNow[p].Keys.ToList())
                        trialNow[p][key] = Clipper.Intersect(trialNow[p][key], trialCopper.GetValueOrDefault(key) ?? [], Rule);
                return null;
            }

            // The region the operands name on layer; a tool is its outline on any layer.
            string? Operand(IReadOnlyList<string> names, LayerKey layer, out Paths64 region)
            {
                region = [];
                foreach (string name in names)
                {
                    if (name.StartsWith(PCellOperations.LayerPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        string layerName = name[PCellOperations.LayerPrefix.Length..];
                        if (FindLayer(layerName) is not { } key) return $"the technology has no layer {layerName}";
                        region.AddRange(trialCopper.GetValueOrDefault(key) ?? []);
                        continue;
                    }
                    if (parts.FirstOrDefault(p => p.Id == name) is not { } part) return $"{name} is not a placed part of the cell";
                    if (part.Tool) foreach (var paths in part.Planned.Values) region.AddRange(paths);
                    else region.AddRange(trialNow[part].GetValueOrDefault(layer) ?? []);
                }
                region = Clipper.Union(region, Rule);
                return null;
            }

            IEnumerable<Part> OperandParts(IEnumerable<string> names) => parts.Where(p => !p.Tool && names.Contains(p.Id));
        }

        // ── Drawn as D5 says ──────────────────────────────────────────────────
        var drawn = new List<LayoutShape>();
        foreach (var part in parts)
        {
            if (!part.Tool && Same(part.Planned, part.Now)) continue;
            if (Hollow(part.Inst, layoutDir, workspaceRootDir) is { } refusal)
            {
                lines.Add(new ReportLine(part.Id, $"{part.Id}'s copper could not be changed: {refusal}", ReportSeverity.Warning));
                continue;
            }
            if (part.Tool) continue;
            foreach (var (key, paths) in part.Now.OrderBy(kv => kv.Key.Layer).ThenBy(kv => kv.Key.Datatype))
                drawn.AddRange(Shapes(paths, key, part.Id));
            drawn.AddRange(part.Points.Select(s => { var c = LayoutGeometry.Clone(s); c.Generated = PCellOperations.GeneratedTag; c.Component = part.Id; return c; }));
        }
        // Copper no part holds: what a grow, a copy or a merged tool made.
        foreach (var (key, paths) in copper.OrderBy(kv => kv.Key.Layer).ThenBy(kv => kv.Key.Datatype))
        {
            var held = new Paths64();
            foreach (var p in parts.Where(p => !p.Tool)) held.AddRange(p.Now.GetValueOrDefault(key) ?? []);
            drawn.AddRange(Shapes(Clipper.Difference(paths, Clipper.Union(held, Rule), Rule), key));
        }
        layout.Shapes.AddRange(drawn);
        PCellOperations.Record(layout, new PCellOperationTrace(steps, spans));
        if (ops.Count > 0)
            lines.Add(new ReportLine("", $"Operations: {steps.Count(s => s.Applied)} of {ops.Count} applied.", ReportSeverity.Info));

        // ── helpers over this cell ──
        double Si(string? expression, string unit)
        {
            if (string.IsNullOrWhiteSpace(expression)) return 0;
            // "20 um" as every value box reads it: the unit lifted off the text, else the operation's own (D11).
            var (e, u) = Optimization.TunableValue.Split(expression, unit);
            if (!SchematicLayoutPlanner.TryResolveSiValue(e, u ?? "", scope, evaluator, out double v, out string? error))
                throw new ExpressionException($"'{expression}': {error}");
            return v;
        }
        bool Names(string? expression)
        {
            if (string.IsNullOrWhiteSpace(expression)) return false;
            try { return AstWalker.CollectRefs(Parser.Parse(Optimization.TunableValue.Split(expression, null).Expression)).Count > 0; }
            catch (Exception) { return false; }
        }
        LayerKey? FindLayer(string name)
            => technology.Layers.FirstOrDefault(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Key;
        LayerKey LayerOf(string? name)
            => name is { Length: > 0 } n
                ? FindLayer(n) ?? throw new ExpressionException($"the technology has no layer {n}")
                : SubstrateResolver.ResolveSignalLayerKey(technology, PCellLayerSelection.Default, out _);
    }

    /// <summary>A part's pins where the plan put them, each with a probe point just inside its copper.</summary>
    private static IEnumerable<Pin> PinsOf(LayoutInstance inst, string id, string layoutDir, Technology technology, int dbuPerMicron)
    {
        foreach (var pin in CellLayoutResolver.Resolve(inst, layoutDir, technology).View?.Pins ?? [])
        {
            var (x, y, deg) = ChainPose.Of(inst).Apply(pin);
            double inward = Math.Max(1, Math.Min(pin.WidthDbu / 4.0, 10.0 * dbuPerMicron)), rad = (deg + 180) * Math.PI / 180;
            yield return new Pin(id, pin.Name, x, y, pin.Layer, (long)Math.Round(x + inward * Math.Cos(rad)), (long)Math.Round(y + inward * Math.Sin(rad)));
        }
    }

    /// <summary>Each Port label on the part pin it was placed on (PortPlacement D1).</summary>
    private static List<(string Name, Pin Pin)> PortsOf(LayoutView layout, List<Pin> pins)
        => [.. layout.Shapes.OfType<LabelShape>().Where(l => l.IsPort)
                  .Select(l => (Name: l.Text, Pin: pins.FirstOrDefault(p => Math.Abs(p.X - l.X) <= 2 && Math.Abs(p.Y - l.Y) <= 2)))
                  .Where(p => p.Pin is not null).Select(p => (p.Name, p.Pin!))];

    /// <summary>
    /// brief-pcd-13 D8: which ports each piece of a layout's copper joins ("1=2, 3"): every placed part's copper and the
    /// layout's own, labels and the ground pour left out. What the LVS of a cell with operations compares.
    /// </summary>
    internal static string PortsJoined(LayoutView layout, string layoutDir, Technology technology)
    {
        var copper = new Dictionary<LayerKey, Paths64>();
        var vias = new List<LayoutShape>();
        var pins = new List<Pin>();
        void Take(LayoutShape s)
        {
            if (s is LabelShape || s.Generated == GroundArtwork.PourTag) return;
            if (!LayoutBooleans.IsClipperOperand(s)) { if (s is ViaShape) vias.Add(s); return; }
            var paths = LayoutClipper.ToClipperPaths(s, LayoutFlattener.ResolveTolDbu(s, technology));
            copper[s.Layer] = copper.TryGetValue(s.Layer, out var had) ? [.. had, .. paths] : paths;
        }
        foreach (var inst in layout.Instances)
        {
            foreach (var s in LayoutFlatten.FlattenAllLevels(inst, layoutDir, technology).Shapes) Take(s);
            if (inst.SchematicId is { Length: > 0 } id) pins.AddRange(PinsOf(inst, id, layoutDir, technology, layout.DbuPerMicron));
        }
        foreach (var s in layout.Shapes) Take(s);
        foreach (var key in copper.Keys.ToList()) copper[key] = Clipper.Union(copper[key], Rule);
        return Joined(copper, vias, PortsOf(layout, pins), technology);
    }

    private static Dictionary<LayerKey, Paths64> Copy(Dictionary<LayerKey, Paths64> d)
        => d.ToDictionary(kv => kv.Key, kv => new Paths64(kv.Value));

    private static bool Same(Dictionary<LayerKey, Paths64> a, Dictionary<LayerKey, Paths64> b)
        => a.Keys.Union(b.Keys).All(k => Clipper.Area(Clipper.Xor(a.GetValueOrDefault(k) ?? [], b.GetValueOrDefault(k) ?? [], Rule)) < 1);

    private static bool Covers(Dictionary<LayerKey, Paths64> copper, Pin pin)
        => copper.TryGetValue(pin.Layer, out var paths)
           && Clipper.Intersect(paths, new Paths64 { Clipper.MakePath(new long[] { pin.ProbeX - 1, pin.ProbeY - 1, pin.ProbeX + 1, pin.ProbeY - 1, pin.ProbeX + 1, pin.ProbeY + 1, pin.ProbeX - 1, pin.ProbeY + 1 }) }, Rule).Count > 0;

    /// <summary>Which ports one piece of copper joins, as text ("1=2, 3"), so two states compare as strings.</summary>
    private static string Joined(Dictionary<LayerKey, Paths64> copper, List<LayoutShape> vias,
                                 List<(string Name, Pin Pin)> ports, Technology technology)
    {
        if (ports.Count < 2) return "";
        var shapes = copper.OrderBy(kv => kv.Key.Layer).ThenBy(kv => kv.Key.Datatype)
                           .SelectMany(kv => Shapes(kv.Value, kv.Key)).Concat(vias).ToList();
        var pieces = CopperPieces.Build(shapes, technology);
        return string.Join(", ", ports.Select((p, i) => (p.Name, Net: pieces.PieceAt(p.Pin.ProbeX, p.Pin.ProbeY, p.Pin.Layer) is var n and >= 0 ? n : -1 - i))
                                      .GroupBy(p => p.Net).Select(g => string.Join("=", g.Select(p => p.Name).Order(StringComparer.Ordinal)))
                                      .Order(StringComparer.Ordinal));
    }

    private static IReadOnlyList<LayoutShape> Shapes(Paths64 paths, LayerKey layer, string? component = null)
    {
        var tree = new PolyTree64();
        Clipper.BooleanOp(ClipType.Union, paths, new Paths64(), tree, Rule);
        var shapes = LayoutClipper.FromClipperTree(tree, layer, null);
        foreach (var s in shapes) { s.Generated = PCellOperations.GeneratedTag; s.Component = component; }
        return shapes;
    }

    /// <summary>D5: the instance moved onto a copy of its generated cell holding no shapes (pins, origin and identity kept).
    /// Null when done, else why not.</summary>
    private static string? Hollow(LayoutInstance inst, string layoutDir, string workspaceRootDir)
    {
        string cellDir = Path.GetFullPath(Path.Combine(layoutDir, inst.CellRef));
        string sourceLayoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        if (CellFolder.ResolvePrimary(cellDir, ViewType.Layout).ResolvedName is not { } clay
            || !File.Exists(Path.Combine(sourceLayoutDir, clay)))
            return $"its cell {Path.GetFileName(cellDir)} has no layout on disk to copy";
        var view = LayoutPersistence.LoadFromFile(Path.Combine(sourceLayoutDir, clay));
        view.Shapes.RemoveAll(s => s is not LabelShape);
        string variant = GeneratedCellStore.GetOrCreateVariant(workspaceRootDir, Path.GetFileName(cellDir), "ops", view, sourceLayoutDir);
        inst.CellRef = Core.RefPath.ToStored(Path.GetRelativePath(layoutDir, variant));
        return null;
    }
}

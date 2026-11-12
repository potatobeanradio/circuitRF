// What a DXF import does with a layer the Layer Mapping dialog has said is copper, a drill layer or the
// board's outline (brief DFR38-1). A vendor publishes a board as a drawing of outlines, one DXF layer per
// purpose; the mapping says what each layer is, and this file acts on it. Pure: rescaled structures and
// settled rows in, shapes and sentences out. DxfImport is what writes them, and Preview shows them first.
//
// ── Why a reading is a separate answer from a mapping ─────────────────────────────────────────────
// A layout circuitRF exported comes back with layers named exactly as the technology names them, so the
// mapping alone cannot mean "fill this". Its pad on a pour is two closed polylines, and an even-odd fill
// would punch the pad out of the pour; its via is two circles. What tells a drawing of outlines from
// one of ours is that circuitRF's writer draws copper with LWPOLYLINE, HATCH and CIRCLE, never with a
// LINE or an ARC (a ruler's LINE is on RULER). So a layer a NAME matched keeps its shapes unless the
// file strokes its copper; a layer the user mapped by hand reads as what they mapped it to. Either way
// the answer is a column in the dialog, changeable before anything is written.

using CircuitRF.Design.Layout.Em3d;

namespace CircuitRF.Design.Layout.Interchange;

/// <summary>How one mapped layer's entities become shapes.</summary>
public enum DxfLayerReading
{
    /// <summary>Each entity stays the shape it is. Every layer that is not copper, a drill or an outline.</summary>
    AsDrawn,
    /// <summary>Outlines chain into loops, filled even-odd with the outermost region copper.</summary>
    Filled,
    /// <summary>The same loops at the other parity: what the outermost loops enclose is the copper.</summary>
    FilledInverted,
    /// <summary>Each circle is a plated via across the layer's stack entry.</summary>
    Vias,
    /// <summary>Outlines chain into the polygon that is the board's edge.</summary>
    BoardOutline,
}

/// <summary>The dialog's question. <see cref="Rows"/> count what model space reaches.
/// <see cref="NeedsAnswer"/> is false when every row was settled by name and no layer strokes its copper,
/// which is what one of circuitRF's own exports looks like, so a caller with a window need not open it.</summary>
/// <param name="Preview">What the rows would import as, with this join tolerance in microns. Writes nothing.</param>
public sealed record DxfMappingRequest(
    IReadOnlyList<LayerMappingRow> Rows,
    bool NeedsAnswer,
    Func<IReadOnlyList<LayerMappingRow>, double, DxfBoardPreview> Preview);

/// <summary>The answer. Returning null from the callback instead abandons the import.</summary>
/// <param name="KeepBlocks">Nobody was asked (<see cref="DxfMappingRequest.NeedsAnswer"/> was false), so the file's
/// own hierarchy stands: a cell per block, as a layout exported from here went out. Otherwise the import is one
/// cell of flattened model space, whatever each layer reads as.</param>
public sealed record DxfMappingAnswer(
    IReadOnlyList<LayerMappingRow> Rows,
    double JoinToleranceMicrons = DxfBoardReading.DefaultJoinToleranceMicrons,
    bool KeepBlocks = false);

/// <summary>A preview: the flattened model space as read, and what would be reported.</summary>
public sealed record DxfBoardPreview(LayoutView View, IReadOnlyList<string> Messages);

public static class DxfBoardReading
{
    /// <summary>How far apart two dangling ends may be and still close. Further than that a closure could
    /// short two nets, so the outline is listed by coordinates and left open.</summary>
    public const double DefaultJoinToleranceMicrons = 1.0;

    /// <summary>The most open outlines one message names; the rest are counted.</summary>
    private const int MaxListedOutlines = 20;

    /// <summary>What a row mapped to <paramref name="target"/> may read as, the default first.</summary>
    public static IReadOnlyList<DxfLayerReading> Offered(Technology? tech, LayerKey? target)
    {
        if (tech is null || target is not { } key) return [DxfLayerReading.AsDrawn];
        if (IsConductor(tech, key)) return [DxfLayerReading.Filled, DxfLayerReading.FilledInverted, DxfLayerReading.AsDrawn];
        if (ViaSpanResolver.DrillLayerKeys(tech).Contains(key)) return [DxfLayerReading.Vias, DxfLayerReading.AsDrawn];
        if (Em3dGenerator.BoardOutlineLayers(tech).Contains(key)) return [DxfLayerReading.BoardOutline, DxfLayerReading.AsDrawn];
        return [DxfLayerReading.AsDrawn];
    }

    public static string Label(DxfLayerReading reading) => reading switch
    {
        DxfLayerReading.Filled => "Filled",
        DxfLayerReading.FilledInverted => "Filled, inverted",
        DxfLayerReading.Vias => "Vias",
        DxfLayerReading.BoardOutline => "Board outline",
        _ => "As drawn",
    };

    private static bool IsConductor(Technology tech, LayerKey key) =>
        tech.Stackup.Layers.Any(l => l.Kind == StackupKind.Conductor && l.DrawingLayers.Contains(key));

    private static bool IsBareStroke(DxfImportedShape s) => s.Entity is "LINE" or "ARC";

    // ── the flatten ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Every shape model space reaches, in model space's coordinates, each on its effective layer:
    /// an entity on layer 0 inside a block takes the layer of the INSERT placing it.</summary>
    internal sealed record Flattened(
        IReadOnlyList<DxfImportedShape> Shapes, IReadOnlyList<string> ReachedBlocks, IReadOnlyList<string> MissingBlocks);

    internal static Flattened Flatten(IReadOnlyList<DxfStructure> structures)
    {
        var byName = new Dictionary<string, DxfStructure>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in structures) byName.TryAdd(s.Name, s);

        var shapes = new List<DxfImportedShape>();
        var reached = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var onStack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Walk(DxfStructure s, LayoutInstance? placed, string? insertLayer)
        {
            if (!onStack.Add(s.Name)) return;   // a cyclic file: the cell-hierarchy guard's job elsewhere, a stop here
            foreach (var sh in s.Shapes)
            {
                string layer = insertLayer is not null && sh.LayerName == "0" ? insertLayer : sh.LayerName;
                foreach (var moved in Placed(sh.Shape, placed))
                    shapes.Add(sh with { Shape = moved, LayerName = layer });
            }
            for (int i = 0; i < s.Instances.Count; i++)
            {
                var inst = s.Instances[i];
                if (!byName.TryGetValue(inst.CellRef, out var target)) { missing.Add(inst.CellRef); continue; }
                reached.Add(target.Name);
                string own = i < s.InstanceLayers.Count ? s.InstanceLayers[i] : "0";
                string next = insertLayer is not null && own == "0" ? insertLayer : own;

                for (int row = 0; row < inst.Rows; row++)
                for (int col = 0; col < inst.Cols; col++)
                {
                    var (x, y) = LayoutInstanceTransform.ArrayCellOrigin(inst, row, col);
                    var cell = new LayoutInstance
                    {
                        CellRef = inst.CellRef, X = x, Y = y,
                        RotationDegrees = inst.RotationDegrees, MirrorX = inst.MirrorX, Mag = inst.Mag,
                    };
                    Walk(target, placed is null ? cell : LayoutInstanceTransform.ComposeInstances(placed, 0, 0, cell), next);
                }
            }
            onStack.Remove(s.Name);
        }

        if (byName.TryGetValue(DxfReader.ModelSpaceName, out var model)) Walk(model, null, null);
        return new Flattened(shapes, [.. reached], [.. missing]);
    }

    /// <summary>One shape through one placement. The same steps as <see cref="LayoutFlatten"/>'s, which works
    /// on cell folders: there are none yet.</summary>
    private static IEnumerable<LayoutShape> Placed(LayoutShape shape, LayoutInstance? placed)
    {
        var clone = LayoutGeometry.Clone(shape);
        if (placed is null) { yield return clone; yield break; }

        bool rotates = !LayoutAngle.TryCardinal(placed.RotationDegrees, out _);
        if (rotates)
        {
            if (LayoutRotationPromotion.CannotRotate(clone)) yield break;
            clone = LayoutRotationPromotion.Promote(clone);
        }
        if (placed.MirrorX && clone is LabelShape label)
        {
            yield return LayoutInstanceTransform.TransformLabel(label, placed, 0, 0);
            yield break;
        }
        LayoutCoordinateWalk.Transform(clone, new LayoutCoordinateTransform(
            (x, y) => LayoutInstanceTransform.TransformPoint(x, y, placed, 0, 0),
            m => (long)Math.Round(m * placed.Mag),
            RotatesAxes: rotates));
        if (placed.MirrorX) LayoutFlatten.FlipBulgeSigns(clone);
        if (rotates && clone is LabelShape turned) turned.RotationDegrees += placed.RotationDegrees;
        yield return clone;
    }

    // ── the rows ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>The dialog's rows. <paramref name="proposed"/> are <see cref="LayoutLayerMapping.Propose"/>'s
    /// over the flattened shapes; each gains its starting reading.</summary>
    internal static (IReadOnlyList<LayerMappingRow> Rows, bool NeedsAnswer) StartingRows(
        IReadOnlyList<LayerMappingRow> proposed, IReadOnlyList<DxfImportedShape> flat,
        IReadOnlyDictionary<string, LayerKey> keyByName, Technology tech)
    {
        var stroked = new HashSet<LayerKey>(flat.Where(IsBareStroke).Select(s => keyByName[s.LayerName]));

        // A name-matched row reads as drawn unless the file strokes its copper. See the file's header.
        bool strokesCopper = proposed.Any(r =>
            stroked.Contains(r.Source) && TargetOf(r) is { } t &&
            Offered(tech, t)[0] is DxfLayerReading.Filled or DxfLayerReading.BoardOutline);

        var rows = proposed
            .Select(r => r with { Reading = strokesCopper ? Offered(tech, TargetOf(r))[0] : DxfLayerReading.AsDrawn })
            .ToList();
        return (rows, LayoutLayerMapping.RequiresConfirmation(rows) || strokesCopper);
    }

    private static LayerKey? TargetOf(LayerMappingRow row) =>
        row.Choice.Action == LayoutFragment.LayerReconciliationAction.MapToExisting ? row.Choice.MapTarget : null;

    // ── the reading ──────────────────────────────────────────────────────────────────────────────

    internal sealed record BoardResult(
        List<LayoutShape> Shapes, List<LayerDef> LayersToAdd, List<string> Messages);

    /// <summary>Applies the settled <paramref name="rows"/> to the flattened shapes. Each shape is already on
    /// its row's source key.</summary>
    internal static BoardResult Read(
        IReadOnlyList<DxfImportedShape> flat, IReadOnlyList<LayerMappingRow> rows, IReadOnlyList<LayerDef> sourceLayers,
        Technology tech, int dbuPerMicron, double joinToleranceMicrons)
    {
        var messages = new List<string>();
        var output = new List<LayoutShape>();
        var rowOf = rows.ToDictionary(r => r.Source);
        var choices = LayoutLayerMapping.BuildChoices(rows);
        var nameOf = rows.ToDictionary(r => r.Source, r => r.SourceName ?? $"{r.Source.Layer}/{r.Source.Datatype}");
        long joinDbu = (long)Math.Round(joinToleranceMicrons * dbuPerMicron);

        DxfLayerReading ReadingOf(LayerKey key) =>
            rowOf.TryGetValue(key, out var r) && TargetOf(r) is not null ? r.Reading ?? DxfLayerReading.AsDrawn : DxfLayerReading.AsDrawn;

        var groups = flat.GroupBy(s => s.Shape.Layer).ToDictionary(g => g.Key, g => g.ToList());
        var asDrawn = new List<LayoutShape>();

        // Vias first: a CIRCLE on copper at a hole is that via's pad, and takes no further part in the fill.
        var padsTaken = new HashSet<LayoutShape>();
        var conductors = CopperTopToBottom(tech);

        foreach (var row in rows.Where(r => ReadingOf(r.Source) == DxfLayerReading.Vias))
            if (groups.TryGetValue(row.Source, out var group))
                output.AddRange(ViasOf(row, group, groups, rowOf, conductors, tech, padsTaken, asDrawn, messages, dbuPerMicron, joinDbu));

        foreach (var row in rows)
        {
            if (!groups.TryGetValue(row.Source, out var group)) continue;
            switch (ReadingOf(row.Source))
            {
                case DxfLayerReading.Vias:
                    break;

                case DxfLayerReading.Filled or DxfLayerReading.FilledInverted or DxfLayerReading.BoardOutline:
                    output.AddRange(LoopsOf(row, group, ReadingOf(row.Source), tech, joinDbu, padsTaken, asDrawn,
                                            messages, dbuPerMicron));
                    break;

                default:
                    asDrawn.AddRange(group.Select(s => s.Shape));
                    break;
            }
        }

        var reconciled = LayoutFragment.ApplyReconciliation(asDrawn, sourceLayers, choices);
        output.AddRange(reconciled.Shapes);

        var ignored = rows.Where(r => r.Choice.Action == LayoutFragment.LayerReconciliationAction.Ignore).ToList();
        if (ignored.Count > 0)
            messages.Add($"Ignored: {string.Join(", ", ignored.Select(r => $"{nameOf[r.Source]} ({r.ShapeCount})"))}.");

        return new BoardResult(output, [.. reconciled.LayersToAdd], messages);
    }

    private static List<LayerKey> CopperTopToBottom(Technology tech) =>
        [.. tech.Stackup.Layers.Where(l => l.Kind == StackupKind.Conductor && l.DrawingLayers.Count > 0)
                               .Select(l => l.DrawingLayers[0])];

    /// <summary>A closed outline on a drill layer is a hole when its box is square within this share of its width.</summary>
    public const double HoleSquareness = 0.05;

    /// <summary>... and it fills at least this share of its box: a circle fills π/4 of it, an octagon 0.83, a square all of
    /// it; a triangle or an L-shape half or less.</summary>
    public const double HoleFill = 0.7;

    /// <summary>... and it is no wider than the widest circle on the layer, or than this many microns where the circles are
    /// smaller (or there are none): a drawn pad, a frame or a part outline on the layer is not a hole.</summary>
    public const double HoleCapMicrons = 3000;

    /// <summary>A drill mark: where, how wide across, and what it was drawn as (for the message).</summary>
    private readonly record struct HoleMark(long X, long Y, long Diameter, string Kind);

    /// <summary>
    /// <paramref name="ring"/> read as a hole, or null: its box centre, and its box's width across, which for a square is the
    /// inscribed circle — the conservative reading (R-dfr38-1-4, revised: see <see cref="ViasOf"/>).
    /// </summary>
    private static HoleMark? HoleOf(long[] ring, long capDbu, string kind)
    {
        if (ring.Length < 6) return null;
        long x0 = long.MaxValue, y0 = long.MaxValue, x1 = long.MinValue, y1 = long.MinValue;
        double area = 0;
        for (int i = 0, n = ring.Length / 2; i < n; i++)
        {
            long x = ring[2 * i], y = ring[2 * i + 1];
            (x0, y0, x1, y1) = (Math.Min(x0, x), Math.Min(y0, y), Math.Max(x1, x), Math.Max(y1, y));
            int j = (i + 1) % n;
            area += (double)x * ring[2 * j + 1] - (double)ring[2 * j] * y;
        }
        long w = x1 - x0, h = y1 - y0;
        if (w <= 0 || h <= 0 || Math.Max(w, h) > capDbu || Math.Abs(w - h) > HoleSquareness * Math.Max(w, h)) return null;
        if (Math.Abs(area) / 2 < HoleFill * w * h) return null;
        return new HoleMark((x0 + x1) / 2, (y0 + y1) / 2, Math.Min(w, h), kind);
    }

    /// <summary>A shape drawn closed as a hole, or null: a circle as itself, a closed polyline or a closed outline with arcs
    /// by <see cref="HoleOf"/>. A circle wider than the cap is a hole too: its diameter is stated, not inferred.</summary>
    private static HoleMark? HoleOf(LayoutShape shape, long capDbu, Technology tech) => shape switch
    {
        CircleShape c => new HoleMark(c.Cx, c.Cy, 2 * c.R, "circle"),
        PolygonShape { Holes: null or { Count: 0 } } p => HoleOf(p.Xy, capDbu, p.Xy.Length == 8 ? "square" : "polygon"),
        CurveShape { Holes: null or { Count: 0 } } c => LayoutFlattener.Flatten(c, LayoutFlattener.ResolveTolDbu(c, tech)) is [var ring]
            ? HoleOf(ring, capDbu, "arc outline") : null,
        _ => null,
    };

    private static string Plural(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";

    /// <summary>
    /// A drill layer's marks as plated vias. A CIRCLE is a hole of its own diameter. So is a small closed outline whose box is
    /// square (<see cref="HoleSquareness"/>), which it fills (<see cref="HoleFill"/>), no wider than <see cref="HoleCapMicrons"/>
    /// or the layer's widest circle: a square, an n-gon, a two-bulge polyline, or LINEs and ARCs that chain into one loop
    /// as copper outlines chain (<see cref="DxfCopperLoops"/>). Its hole is the box's centre and width — for a square, the
    /// inscribed circle. This revises R-dfr38-1-4 ("a non-circular entity on the layer is reported, not guessed at"): many
    /// drawings mark drills as squares or polygons, and leaving them as drawing cut every pour they stitch from its plane.
    /// It is not <c>ImageTrace.RoundHole</c>'s rule (R-dfr30-3-2), which keeps a square out of a picture's holes on
    /// purpose: in a picture a square blob may be a pad; here the user has mapped the layer as the drill layer, which
    /// declares that what is on it is holes. Anything else on the layer is still left as drawn, and said.
    /// </summary>
    private static IEnumerable<LayoutShape> ViasOf(
        LayerMappingRow row, List<DxfImportedShape> group, Dictionary<LayerKey, List<DxfImportedShape>> groups,
        Dictionary<LayerKey, LayerMappingRow> rowOf, List<LayerKey> copper, Technology tech,
        HashSet<LayoutShape> padsTaken, List<LayoutShape> asDrawn, List<string> messages, int dbuPerMicron, long joinDbu)
    {
        var barrel = row.Choice.MapTarget!.Value;
        string name = row.SourceName ?? "";

        long widestCircle = group.Select(s => s.Shape is CircleShape c ? 2 * c.R : 0).DefaultIfEmpty(0).Max();
        long cap = Math.Max(widestCircle, (long)Math.Round(HoleCapMicrons * dbuPerMicron));

        var marks = new List<HoleMark>();
        var drawn = new List<LayoutShape>();
        var strokes = new List<(long[] Xy, LayoutShape Shape)>();
        foreach (var s in group)
        {
            if (HoleOf(s.Shape, cap, tech) is { } mark) marks.Add(mark);
            else if (s.Shape is PathShape { Width: <= 0 } path)
                strokes.Add((LayoutFlattener.FlattenOpenEdgeList(path.Xy, path.Edges, LayoutFlattener.ResolveTolDbu(path, tech)), path));
            else drawn.Add(s.Shape);
        }
        // Lines and arcs drawing a mark: chained as copper outlines are, and each closed loop read as the outlines above. A
        // stroke inside no loop that is a hole stays as drawn.
        if (strokes.Count > 0)
        {
            var holes = new List<Bbox>();
            foreach (var loop in DxfCopperLoops.Chain([.. strokes.Select(s => s.Xy)], [], joinDbu).Loops)
                if (HoleOf(loop, cap, loop.Length == 8 ? "square of lines" : "outline of lines") is { } mark)
                {
                    marks.Add(mark);
                    long r = mark.Diameter / 2 + joinDbu + 1;
                    holes.Add(new Bbox(mark.X - r, mark.Y - r, mark.X + r, mark.Y + r));
                }
            foreach (var (xy, shape) in strokes)
                if (!holes.Any(b => Enumerable.Range(0, xy.Length / 2).All(i => b.Contains(xy[2 * i], xy[2 * i + 1])))) drawn.Add(shape);
        }
        asDrawn.AddRange(drawn);

        var hits = marks.Select(m => new DrillHit(m.X, m.Y, 1, m.Diameter, true, "ViaDrill")).ToList();
        var kinds = marks.GroupBy(m => m.Kind).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
                         .Select(g => Plural(g.Count(), g.Key, g.Key.EndsWith(" of lines", StringComparison.Ordinal)
                                                                   ? g.Key[..^9] + "s of lines" : g.Key + "s")).ToList();
        if (marks.Any(m => m.Kind != "circle"))
            messages.Add($"{name}: {string.Join(", ", kinds)} read as holes; a closed outline is one when its box is square and no " +
                         $"wider than {Micron(cap, dbuPerMicron)} µm, the hole its box's width across.");
        if (drawn.Count > 0)
            messages.Add($"{name}: {Plural(drawn.Count, "entity is", "entities are")} not a circle or a small closed outline square " +
                         "to its box, so not a via. Left as drawn on the layer.");

        var span = DrillViaPairing.MapSpan(null, barrel, copper);

        // The candidates for a pad: circles, and outlines a hole could be, on the copper layer the via lands on, if that layer
        // is being filled — a square pad round a square drill is that via's pad as a round one is.
        var candidates = new List<GerberImportedShape>();
        var sourceOf = new Dictionary<LayoutShape, LayoutShape>();
        foreach (var (key, shapes) in groups)
        {
            if (!rowOf.TryGetValue(key, out var other) || TargetOf(other) != span.Landing) continue;
            if (other.Reading is not (DxfLayerReading.Filled or DxfLayerReading.FilledInverted)) continue;
            foreach (var s in shapes)
                if (HoleOf(s.Shape, cap, tech) is { } pad)
                {
                    var onTarget = new CircleShape { Cx = pad.X, Cy = pad.Y, R = pad.Diameter / 2, Layer = span.Landing!.Value };
                    sourceOf[onTarget] = s.Shape;
                    candidates.Add(new GerberImportedShape(onTarget, null, null, null));
                }
        }

        var paired = DrillViaPairing.Pair(candidates, DrillResult(hits), barrel, span.Landing, dbuPerMicron);
        var kept = new HashSet<LayoutShape>(paired.RemainingArtwork.Select(a => a.Shape));
        foreach (var c in candidates)
            if (!kept.Contains(c.Shape)) padsTaken.Add(sourceOf[c.Shape]);

        // A drawing of outlines states no pad. The hole's own size is the only one it does state, and the
        // barrel still meets the copper it passes through.
        var unpaired = paired.UnpairedHoles.Select(h => new ViaShape
        {
            X = h.Cx, Y = h.Cy, PadSize = h.R * 2, DrillSize = h.R * 2, Layer = barrel, LandingLayer = span.Landing,
        }).ToList();

        var result = new List<LayoutShape>(paired.Vias);
        result.AddRange(unpaired);

        var via = ViaSpanResolver.Resolve(barrel, tech);
        string across = via is null
            ? (ViaSpanResolver.Explain(barrel, tech) ?? "")
            : $"{via.Top.Name} to {via.Bottom.Name}";
        messages.Add($"{name}: {result.Count} plated via(s), {across}; {paired.Vias.Count} took a pad from a circle on " +
                     $"the copper, {unpaired.Count} have a pad the size of the hole.");
        return result;
    }

    private static ExcellonReadResult DrillResult(List<DrillHit> hits) => new()
    {
        Hits = hits,
        // These hits were read from a drawing, so no drill format was involved. The record requires one.
        Format = new DrillFormatInference
        {
            Unit = GerberUnit.Millimetres, UnitEvidence = DrillFormatEvidence.Override,
            IntegerDigits = 0, DecimalDigits = 0, DigitsEvidence = DrillFormatEvidence.Override,
            ZeroOmission = GerberZeroOmission.Leading, ZeroOmissionEvidence = DrillFormatEvidence.Override,
            DecimalCoordinates = true, ZeroSuppressionApplies = false, Evidence = [],
        },
    };

    private static IEnumerable<LayoutShape> LoopsOf(
        LayerMappingRow row, List<DxfImportedShape> group, DxfLayerReading reading, Technology tech, long joinDbu,
        HashSet<LayoutShape> padsTaken, List<LayoutShape> asDrawn, List<string> messages, int dbuPerMicron)
    {
        var target = row.Choice.MapTarget!.Value;
        string name = row.SourceName ?? "";

        var polylines = new List<long[]>();
        var rings = new List<long[]>();
        foreach (var s in group)
        {
            if (padsTaken.Contains(s.Shape)) continue;
            long tol = LayoutFlattener.ResolveTolDbu(s.Shape, tech);
            switch (s.Shape)
            {
                case PathShape { Width: <= 0 } path:
                    polylines.Add(LayoutFlattener.FlattenOpenEdgeList(path.Xy, path.Edges, tol));
                    break;

                // A hatch or a solid states its own fill; the fill of its neighbours is no business of it.
                case PolygonShape or CurveShape when s.Entity is "HATCH" or "SOLID":
                    asDrawn.Add(s.Shape);
                    break;

                case PolygonShape { Holes: null or { Count: 0 } } or CurveShape { Holes: null or { Count: 0 } } or CircleShape:
                    rings.AddRange(LayoutFlattener.Flatten(s.Shape, tol));
                    break;

                // A path with a width is copper as drawn, and so is a label: text as copper is not read.
                default:
                    asDrawn.Add(s.Shape);
                    break;
            }
        }

        var chained = DxfCopperLoops.Chain(polylines, rings, joinDbu);
        var polygons = DxfCopperLoops.Fill(chained.Loops, reading == DxfLayerReading.FilledInverted);

        var result = new List<LayoutShape>(polygons.Count + chained.Open.Count);
        foreach (var p in polygons) { p.Layer = target; result.Add(p); }
        // One path per edge, as an import of LINEs always gave: a zero-width path of three points or more
        // draws as the area it encloses, which would show a gap as copper.
        foreach (var open in chained.Open)
            for (int i = 0; i + 3 < open.Xy.Length; i += 2)
                result.Add(new PathShape { Xy = open.Xy[i..(i + 4)], Width = 0, End = PathEndStyle.Flush, Layer = target });

        int holes = polygons.Sum(p => p.Holes?.Count ?? 0);
        string what = reading == DxfLayerReading.BoardOutline ? "board outline" : "filled";
        messages.Add($"{name}: {what}, {polygons.Count} polygon(s) with {holes} hole(s).");
        if (chained.OverlapsResolved > 0)
            messages.Add($"{name}: {chained.OverlapsResolved} overlapping collinear segment(s) resolved.");
        if (chained.SharedEdgesRemoved > 0)
            messages.Add($"{name}: {chained.SharedEdgesRemoved} edge(s) between two loops removed, so those loops are one polygon.");
        if (chained.EndsJoined > 0)
            messages.Add($"{name}: {chained.EndsJoined} gap(s) closed within the tolerance, the widest " +
                         $"{Micron(chained.LargestJoinDbu, dbuPerMicron)} µm.");
        if (chained.Open.Count > 0)
        {
            var listed = chained.Open.Take(MaxListedOutlines).Select(o => o.OpenEnds.Count > 0
                ? string.Join(" and ", o.OpenEnds.Select(e => Point(e, dbuPerMicron)))
                : $"a line from {Point((o.Xy[0], o.Xy[1]), dbuPerMicron)}");
            string more = chained.Open.Count > MaxListedOutlines ? $" and {chained.Open.Count - MaxListedOutlines} more" : "";
            messages.Add($"{name}: {chained.Open.Count} outline(s) did not close, left as outlines. Open at " +
                         $"{string.Join("; ", listed)}{more} (mm).");
        }
        return result;
    }

    private static string Point((long X, long Y) p, int dbuPerMicron) => string.Create(
        System.Globalization.CultureInfo.InvariantCulture,
        $"({p.X / (dbuPerMicron * 1000.0):0.####}, {p.Y / (dbuPerMicron * 1000.0):0.####})");

    private static string Micron(long dbu, int dbuPerMicron) =>
        (dbu / (double)dbuPerMicron).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    // ── the same answer from a command line ──────────────────────────────────────────────────────

    /// <summary>
    /// <c>PATTERN=Top Copper,HOLE=Drill,PARTS=ignore</c> applied to the dialog's rows: <c>ignore</c>,
    /// <c>add</c> or a layer of the technology, which may end <c>:invert</c> or <c>:drawn</c>. A layer the
    /// map does not name is a refusal listing the layers, as the dialog would have shown them.
    /// </summary>
    public static (IReadOnlyList<LayerMappingRow>? Rows, string? Refusal) ApplyMap(
        string map, IReadOnlyList<LayerMappingRow> rows, Technology tech)
    {
        var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in map.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0 || eq == part.Length - 1)
                return (null, $"'{part}' is not layer=target in the layer map.");
            entries[part[..eq].Trim()] = part[(eq + 1)..].Trim();
        }

        string layers = string.Join(", ", rows.Select(r => r.SourceName));
        var names = rows.Select(r => r.SourceName ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknown = entries.Keys.Where(k => !names.Contains(k)).ToList();
        if (unknown.Count > 0)
            return (null, $"The layer map names {string.Join(", ", unknown)}, which model space does not reach. " +
                          $"The layers it reaches: {layers}.");
        var missing = rows.Where(r => !entries.ContainsKey(r.SourceName ?? "")).Select(r => r.SourceName).ToList();
        if (missing.Count > 0)
            return (null, $"The layer map does not name {string.Join(", ", missing)}. Every layer needs an answer; " +
                          $"the layers model space reaches: {layers}.");

        var settled = new List<LayerMappingRow>(rows.Count);
        foreach (var row in rows)
        {
            string answer = entries[row.SourceName ?? ""];
            if (answer.Equals("ignore", StringComparison.OrdinalIgnoreCase))
            {
                settled.Add(row with { Choice = new(LayoutFragment.LayerReconciliationAction.Ignore), Reading = DxfLayerReading.AsDrawn });
                continue;
            }
            if (answer.Equals("add", StringComparison.OrdinalIgnoreCase))
            {
                settled.Add(row with { Choice = new(LayoutFragment.LayerReconciliationAction.AddToTechnology), Reading = DxfLayerReading.AsDrawn });
                continue;
            }

            string target = answer;
            string? modifier = null;
            int colon = answer.LastIndexOf(':');
            if (colon > 0 && answer[(colon + 1)..].Trim().ToLowerInvariant() is "invert" or "drawn")
            {
                modifier = answer[(colon + 1)..].Trim().ToLowerInvariant();
                target = answer[..colon].Trim();
            }

            var layer = tech.Layers.FirstOrDefault(l => string.Equals(l.Name, target, StringComparison.OrdinalIgnoreCase));
            if (layer is null)
                return (null, $"'{target}' is not a layer of {tech.Name}. Its layers: {string.Join(", ", tech.Layers.Select(l => l.Name))}; " +
                              "or ignore, or add.");

            var offered = Offered(tech, layer.Key);
            if (modifier == "invert" && !offered.Contains(DxfLayerReading.FilledInverted))
                return (null, $"{row.SourceName}={answer}: only a conductor can be inverted, and {layer.Name} is not one.");

            var reading = modifier switch
            {
                "invert" => DxfLayerReading.FilledInverted,
                "drawn" => DxfLayerReading.AsDrawn,
                _ => offered[0],
            };
            settled.Add(row with
            {
                Choice = new(LayoutFragment.LayerReconciliationAction.MapToExisting, layer.Key),
                Reading = reading,
            });
        }
        return (settled, null);
    }
}

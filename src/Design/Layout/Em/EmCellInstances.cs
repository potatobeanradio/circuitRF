// brief-icp-1 R-icp1-1 — every placed cell instance a component port could cut out, at any depth, read off a layout.
//
// EmPartPads.Project answers the same question for a board: footprint placements, their pads, the top level only. A chip's parts
// are cell instances — a PCell, a device cell, a sub-block Update Layout placed — and they nest. This walks the whole instance
// tree once and records, per instance, what every later step needs: its path, its cell, its pins as faces in world metres, its
// schematic counterpart, what it is to an EM result, and the box its own shapes fill.
//
// THE NAMES ARE LVS'S. A path segment is LayoutDesignFlatten.PathOf — the designator, else "@n" by position in the parent's
// Instances — and segments join with "/". One spelling for LVS, explain, the Touchstone header and the co-simulation's comments,
// so a device can be cross-referenced between them.
//
// NOTHING IS BUILT HERE. The walk reads files and writes nothing; leaving geometry out and putting ports on pins are brief-icp-2.

using System.Globalization;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout.Footprints;
using CircuitRF.Design.Layout.Pdn;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.ThreeD;

namespace CircuitRF.Design.Layout.Em;

/// <summary>What a placed instance is to a component port (ICP-0 D1, D3).</summary>
public enum EmInstanceKind
{
    /// <summary>A footprint part: its pads are copper and stay; a component port is LCP's gap across them.</summary>
    Footprint,
    /// <summary>A device: the solver cannot represent it as metal. <c>"*"</c> takes it.</summary>
    Part,
    /// <summary>Metal the solver can solve (a PCell, a line, a spiral, a sub-cell's drawn layout): cut out only when named.</summary>
    Copper,
}

/// <summary>One pin of a placed instance: a face, in world metres.</summary>
/// <param name="Name">The pin's name as a port names it: the layout pin's, or the symbol's where the layout pin is a Port number the
/// symbol names (R-icp1-5).</param>
/// <param name="LayoutName">The layout pin's own name.</param>
/// <param name="Number">The layout pin's number when its name is one; what a symbol-named pin is mapped by.</param>
/// <param name="X">The face's middle, world metres.</param>
/// <param name="Y">The face's middle, world metres.</param>
/// <param name="WidthM">The face's width.</param>
/// <param name="OutwardDeg">The direction the face looks out of the cell, degrees counter-clockwise in the world frame, [0, 360).</param>
/// <param name="Layer">The pin's drawing layer.</param>
public sealed record EmInstancePin(string Name, string LayoutName, int? Number, double X, double Y, double WidthM, double OutwardDeg,
                                   LayerKey Layer);

/// <summary>One placed instance a component port could name.</summary>
/// <param name="Path">Its path: LVS's segments joined by <c>/</c>.</param>
/// <param name="Cell">The cell folder's name.</param>
/// <param name="GeneratorId">The PCell generator a generated cell came from, or null.</param>
/// <param name="Kind">What it is to a component port.</param>
/// <param name="Pins">Its pins in its cell's pin order, world metres.</param>
/// <param name="SchematicChain">The <c>SchematicId</c> of each placement from the top down to this one (null where a level has none).</param>
/// <param name="Why">Why no component port can be built on it, or null.</param>
public sealed record EmCellInstance(string Path, string Cell, string? GeneratorId, EmInstanceKind Kind, IReadOnlyList<EmInstancePin> Pins,
                                    IReadOnlyList<string?> SchematicChain, string? Why)
{
    private Lazy<(double X0, double Y0, double X1, double Y1)?>? _box;

    /// <summary>
    /// The bounding box of its own shapes (on the analysis levels when asked), world metres; null when it draws nothing there or
    /// its cell does not resolve. <b>Computed on first read, and once per CELL</b>: only a cut at the cell edge or a keep-out box
    /// needs it, and the cell's own box is flattened once and carried through each placement, so a chip placing one device a
    /// hundred times flattens it once.
    /// </summary>
    public (double X0, double Y0, double X1, double Y1)? Box => _box?.Value;

    internal EmCellInstance WithBox(Func<(double X0, double Y0, double X1, double Y1)?> box)
    {
        _box = new Lazy<(double X0, double Y0, double X1, double Y1)?>(box, LazyThreadSafetyMode.ExecutionAndPublication);
        return this;
    }

    /// <summary>brief-icp-2 — this placement composed into the root layout's frame (DBU), its <c>CellRef</c> rebased onto the root's
    /// layout folder: what a keep-out box stated in the instance's own frame is carried through.</summary>
    public LayoutInstance? Placement { get; init; }

    /// <summary>The path of the instance that places this one, or null at the top level.</summary>
    public string? Parent => Path.LastIndexOf('/') is var at and > 0 ? Path[..at] : null;

    /// <summary>How deep it is: 0 at the top level.</summary>
    public int Depth => Path.Count(ch => ch == '/');

    /// <summary>What a sentence calls its cell: the generator for a generated cell, else the folder.</summary>
    public string CellLabel => GeneratorId ?? Cell;

    /// <summary>Whether <paramref name="name"/> is this instance's cell (folder name or generator id, case-insensitive).</summary>
    public bool IsCell(string name)
        => Cell.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)
        || GeneratorId is { } g && g.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>The schematic instance this placement is in its own parent's schematic, or null.</summary>
    public string? SchematicId => SchematicChain.Count > 0 ? SchematicChain[^1] : null;
}

/// <summary>Every instance <see cref="EmCellInstances.Project"/> found, in walk order (each parent before its children), and its notes.</summary>
public sealed record EmCellInstanceSet(IReadOnlyList<EmCellInstance> Instances, IReadOnlyList<string> Notes)
{
    public static readonly EmCellInstanceSet Empty = new([], []);

    private Dictionary<string, EmCellInstance>? _byPath;

    /// <summary>The instance at <paramref name="path"/> (case-insensitive), or null.</summary>
    public EmCellInstance? At(string path)
    {
        _byPath ??= Instances.GroupBy(i => i.Path, StringComparer.OrdinalIgnoreCase)
                             .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        return _byPath.GetValueOrDefault(path.Trim().Trim('/'));
    }

    /// <summary>The instances placed directly by <paramref name="parent"/> (null: the top level), in placement order.</summary>
    public IEnumerable<EmCellInstance> ChildrenOf(string? parent)
        => Instances.Where(i => string.Equals(i.Parent, parent, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether <paramref name="inner"/> lies inside <paramref name="outer"/>'s subtree (and is not it).</summary>
    public static bool IsInside(string inner, string outer)
        => inner.Length > outer.Length + 1 && inner.StartsWith(outer + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>The sentence a path that names no placed instance is left out with, listing what is placed at that level.</summary>
    public string NamesNothing(string path)
    {
        string trimmed = path.Trim().Trim('/');
        string? parent = trimmed.LastIndexOf('/') is var at and > 0 ? trimmed[..at] : null;
        if (parent is not null && At(parent) is null)
            return $"{trimmed} names no placed instance: nothing is placed at {parent}.";
        var here = ChildrenOf(parent).Select(i => i.Path[(parent is null ? 0 : parent.Length + 1)..]).ToList();
        string where = parent is null ? "this layout" : parent;
        if (here.Count == 0) return $"{trimmed} names no placed instance: {where} places no cell instance.";
        return $"{trimmed} names no placed instance ({where} places {string.Join(", ", here.Take(20))}" +
               $"{(here.Count > 20 ? $" and {here.Count - 20} more" : "")}).";
    }
}

public static class EmCellInstances
{
    /// <summary>
    /// Every instance placed on <paramref name="view"/>, at every depth to <see cref="CellHierarchy.MaxDepth"/>, with its path,
    /// cell, pins (world metres, through the whole chain of placements and then <paramref name="world"/>), schematic counterpart,
    /// kind and box, or why no component port can be built on it. Reads the cells' and schematics' files; writes nothing.
    /// </summary>
    /// <param name="world">Layout metres to world metres: the identity on a layout's own run, a 3D view's instance frame otherwise.</param>
    /// <param name="levels">The drawing layers of the setup's analysis levels, which the box is taken over; null takes every shape.</param>
    public static EmCellInstanceSet Project(LayoutView view, string clayPath, Technology? tech, C3dTransform world,
                                            IReadOnlySet<LayerKey>? levels = null)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(clayPath);
        if (view.Instances.Count == 0) return EmCellInstanceSet.Empty;

        string full = Path.GetFullPath(clayPath);
        string rootBaseDir = Path.GetDirectoryName(full) ?? "";
        string rootCellDir = Path.GetDirectoryName(rootBaseDir) ?? rootBaseDir;
        double per = C3dLowering.Metres(1, view.DbuPerMicron);
        var w = world;
        bool upright = Math.Abs(w.M20) < 1e-12 && Math.Abs(w.M21) < 1e-12 && Math.Abs(w.M02) < 1e-12 && Math.Abs(w.M12) < 1e-12;
        (double X, double Y) World(double x, double y) => (w.M00 * x * per + w.M01 * y * per + w.Tx, w.M10 * x * per + w.M11 * y * per + w.Ty);
        double WorldAngle(double vx, double vy)
            => LayoutAngle.Normalize(Math.Atan2(w.M10 * vx + w.M11 * vy, w.M00 * vx + w.M01 * vy) * 180.0 / Math.PI);

        var schematics = new Dictionary<string, CellSchematic>(StringComparer.OrdinalIgnoreCase);
        CellSchematic SchematicOf(string cellDir)
        {
            if (!schematics.TryGetValue(cellDir, out var s)) schematics[cellDir] = s = CellSchematic.Read(cellDir);
            return s;
        }

        var found = new List<EmCellInstance>();
        var notes = new List<string>();
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // A cell's own box in its own frame, flattened at most once per cell and face, and only when some instance's box is read.
        var boxes = new Dictionary<(string CellDir, PlacementSide Side), Lazy<Bbox?>>();

        // parentView: the view whose Instances hold `own` (for its PCell snapshots); placed: `own` composed into the root frame,
        // its CellRef rebased onto the root's layout folder; parentCellDir: the cell folder whose schematic `own`'s SchematicId names.
        void Walk(LayoutView parentView, string? parentPath, string parentCellDir, IReadOnlyList<string?> chain, int depth,
                  IReadOnlyList<(LayoutInstance Own, LayoutInstance Placed)> placements)
        {
            var paths = placements.Select((p, i) => LayoutDesignFlatten.PathOf(p.Own, i)).ToList();
            var shared = paths.GroupBy(p => p, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key)
                              .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var schematic = SchematicOf(parentCellDir);
            for (int i = 0; i < placements.Count; i++)
            {
                var (own, placed) = placements[i];
                string path = parentPath is null ? paths[i] : $"{parentPath}/{paths[i]}";
                string cell = Path.GetFileName(own.CellRef.TrimEnd('/', '\\'));
                var thisChain = chain.Append(own.SchematicId is { Length: > 0 } sid ? sid : null).ToList();
                string? generator = parentView.PCellSnapshots.TryGetValue(cell, out var snap) ? snap.GeneratorId : null;

                EmCellInstance Record(EmInstanceKind kind, IReadOnlyList<EmInstancePin> pins, string? why)
                {
                    var rec = new EmCellInstance(path, cell, generator, kind, pins, thisChain, why) { Placement = placed };
                    found.Add(rec);
                    return rec;
                }

                var component = schematic.Component(own.SchematicId);
                if (shared.Contains(paths[i]))
                {
                    Record(KindOf(own, component, schematic, generator), [], EmPartPads.PlacedMoreThanOnce(path));
                    continue;
                }
                if (own.Rows > 1 || own.Cols > 1)
                {
                    Record(KindOf(own, component, schematic, generator), [],
                           $"{path} is an arrayed placement; a component port is built on one instance's pins.");
                    continue;
                }
                var res = CellLayoutResolver.Resolve(placed, rootBaseDir, tech);
                if (res is not { State: CellLayoutState.Resolved, View: { } sub, ResolvedCellDir: { } cellDir })
                {
                    Record(KindOf(own, component, schematic, generator), [],
                           $"{path}'s cell does not resolve: {res.Refusal ?? LayoutDesignFlatten.UnresolvedNote(own.CellRef)}");
                    continue;
                }
                generator ??= sub.PCellOrigin?.GeneratorId;
                var kind = KindOf(own, component, schematic, generator);
                if (depth + 1 >= CellHierarchy.MaxDepth || !visiting.Add(cellDir))
                {
                    Record(kind, [], $"{path} places its own cell again (or nests deeper than {CellHierarchy.MaxDepth} levels).");
                    continue;
                }

                // Pins: the cell's own, each a face carried through the composed placement and then the world transform.
                var part = schematic.Part(own.SchematicId);
                var pins = new List<EmInstancePin>();
                var (c, s) = LayoutAngle.CosSin(placed.RotationDegrees);
                foreach (var pin in CellPins.Resolve(sub, tech))
                {
                    var (px, py) = LayoutInstanceTransform.TransformPoint(pin.X, pin.Y, placed, 0, 0);
                    var (wx, wy) = World(px, py);
                    var (lc, ls) = LayoutAngle.CosSin(pin.OutwardDeg);
                    double mx = (placed.MirrorX ? -1 : 1) * lc, my = ls;
                    double outward = WorldAngle(mx * c - my * s, mx * s + my * c);
                    int? number = int.TryParse(pin.Name.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n > 0 ? n : null;
                    string name = number is { } k && part is { PortNames.Count: var count } && k <= count && part.PortNames[k - 1] is { Length: > 0 } named
                        ? named : pin.Name;
                    pins.Add(new EmInstancePin(name, pin.Name, number, wx, wy, pin.WidthDbu * placed.Mag * per, outward, pin.Layer));
                }

                string? why = null;
                if (!upright) why = $"{path}'s layout is placed tilted in this 3D view; component ports are built on a layout lying flat.";
                else if (placed.RotationDegrees % 90 != 0)
                    why = $"{path} is placed at {placed.RotationDegrees.ToString("G6", CultureInfo.InvariantCulture)}°; a component port is " +
                          "built on faces along x or y, so the instance must be placed at a multiple of 90°.";
                else if (pins.Count == 0) why = $"{path} ({generator ?? cell}) has no layout pins, so it has nothing a port could sit on.";

                var rec = Record(kind, pins, why);
                var localBox = boxes.TryGetValue((cellDir, placed.Side), out var known) ? known
                             : boxes[(cellDir, placed.Side)] = LocalBoxOf(placed, rootBaseDir, tech, levels);
                var placedForBox = placed;
                rec.WithBox(() => WorldBox(localBox.Value, placedForBox, World));

                // Its children, composed into the root frame. A footprint's insides are its own business (PlacedPins' rule).
                if (kind != EmInstanceKind.Footprint && res.SourceView is { Instances.Count: > 0 } source)
                {
                    string subBaseDir = CellHierarchy.LayoutBaseDirOf(cellDir);
                    var children = source.Instances.Select(inner =>
                    {
                        var composed = LayoutInstanceTransform.ComposeInstances(placed, 0, 0, inner);
                        composed.CellRef = LayoutFlatten.RebaseCellRef(inner.CellRef, subBaseDir, rootBaseDir);
                        return (inner, composed);
                    }).ToList();
                    Walk(source, rec.Path, cellDir, thisChain, depth + 1, children);
                }
                visiting.Remove(cellDir);
            }
        }

        Walk(view, null, rootCellDir, [], 0, view.Instances.Select(i => (i, i)).ToList());
        notes.AddRange(schematics.Values.SelectMany(sch => sch.Notes));
        return new EmCellInstanceSet(found, notes);
    }

    /// <summary>
    /// ICP-0 D1/D3 — what an instance is. With a schematic counterpart, the schematic says: a part with a footprint is a footprint
    /// part, one <see cref="EmPartKinds"/> calls copper is copper, anything else is a device. With none: a generated cell is copper
    /// (a land-pattern generator's is a footprint), a placement carrying its own designator is a footprint part (a land pattern
    /// placed by hand or read out of a board file), and anything else is copper — the solver can solve what it can see.
    /// </summary>
    internal static EmInstanceKind KindOf(LayoutInstance own, EditableComponent? component, CellSchematic schematic, string? generator)
    {
        if (component is not null)
        {
            if (component.Footprint is { Length: > 0 }) return EmInstanceKind.Footprint;
            return EmPartKinds.Of(component, schematic.Directory) == EmPartKind.Copper ? EmInstanceKind.Copper : EmInstanceKind.Part;
        }
        if (generator is not null)
            return ChipLandPatternGenerator.GeneratorIds.Contains(generator, StringComparer.OrdinalIgnoreCase)
                ? EmInstanceKind.Footprint : EmInstanceKind.Copper;
        if (own.PartKind is { Length: > 0 } || own.RefDes is { Length: > 0 }) return EmInstanceKind.Footprint;
        return EmInstanceKind.Copper;
    }

    /// <summary>
    /// A cell's own box in its own DBU frame, through every level, over <paramref name="levels"/> (null: every drawn shape): the
    /// cell flattened once through an untransformed placement on <paramref name="placed"/>'s face, so a bottom placement's swapped
    /// layers are the ones filtered. Lazy: nothing is read until a box is asked for.
    /// </summary>
    private static Lazy<Bbox?> LocalBoxOf(LayoutInstance placed, string rootBaseDir, Technology? tech, IReadOnlySet<LayerKey>? levels)
    {
        var at = new LayoutInstance { CellRef = placed.CellRef, Side = placed.Side, Mag = 1.0 };
        return new Lazy<Bbox?>(() =>
        {
            long x0 = long.MaxValue, y0 = long.MaxValue, x1 = long.MinValue, y1 = long.MinValue;
            foreach (var shape in LayoutFlatten.FlattenAllLevels(at, rootBaseDir, tech).Shapes)
            {
                if (shape is LabelShape or BitmapShape || levels is not null && !levels.Contains(shape.Layer)) continue;
                var b = LayoutGeometry.BboxOf(shape);
                x0 = Math.Min(x0, b.MinX); y0 = Math.Min(y0, b.MinY); x1 = Math.Max(x1, b.MaxX); y1 = Math.Max(y1, b.MaxY);
            }
            return x1 >= x0 ? new Bbox(x0, y0, x1, y1) : null;
        }, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>A cell-frame box carried through a placement and the world transform. Exact at the multiples of 90° a component
    /// port is built at (a box's corners map onto the placed box's); a bound at any other angle, where the instance is refused.</summary>
    private static (double X0, double Y0, double X1, double Y1)? WorldBox(Bbox? local, LayoutInstance placed,
                                                                         Func<double, double, (double X, double Y)> world)
    {
        if (local is not { } b) return null;
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        foreach (var (cx, cy) in new[] { (b.MinX, b.MinY), (b.MaxX, b.MinY), (b.MaxX, b.MaxY), (b.MinX, b.MaxY) })
        {
            var (px, py) = LayoutInstanceTransform.TransformPoint(cx, cy, placed, 0, 0);
            var (wx, wy) = world(px, py);
            x0 = Math.Min(x0, wx); y0 = Math.Min(y0, wy); x1 = Math.Max(x1, wx); y1 = Math.Max(y1, wy);
        }
        return (x0, y0, x1, y1);
    }

    /// <summary>One cell folder's Primary schematic, read once: its components by name, and their port names in order.</summary>
    internal sealed class CellSchematic
    {
        private readonly Dictionary<string, EditableComponent> _components = new(StringComparer.OrdinalIgnoreCase);
        private PdnSchematicNets _nets = PdnSchematicNets.None;

        public string? Directory { get; private init; }
        public List<string> Notes { get; } = [];

        public EditableComponent? Component(string? id) => id is { Length: > 0 } && _components.TryGetValue(id, out var c) ? c : null;
        public PdnSchematicPart? Part(string? id) => _nets.For(id);

        public static CellSchematic Read(string cellDir)
        {
            if (EmCoSimNetlist.PrimarySchematic(cellDir) is not { } sch) return new CellSchematic();
            var result = new CellSchematic { Directory = Path.GetDirectoryName(sch) };
            try
            {
                var (model, _, _) = SchematicPersistence.LoadFromFile(sch);
                foreach (var c in model.Components)
                    if (c.InstanceName is { Length: > 0 } name) result._components.TryAdd(name, c);
                result._nets = PdnSchematicNets.Of(model, sch);
                result.Notes.AddRange(result._nets.Notes);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException
                                           or InvalidOperationException or System.Text.Json.JsonException)
            {
                result.Notes.Add($"The schematic '{Path.GetFileName(sch)}' did not read ({e.Message}), so the instances its layout " +
                                 "places are classified without it.");
            }
            return result;
        }
    }
}

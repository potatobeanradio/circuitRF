// brief-lcp-1 R-lcp1-1 — a placed part's pads, read off a layout with no 3D view in sight.
//
// The projection, the entry selection and the pad pairing were written for a .c3d (brief-emb-1) and read a
// C3dInstanceFrame. A component port on a layout's .cem needs exactly the same three answers — which parts, which pads,
// which pad is the negative end — so they live here, taking a LayoutView, its path and its technology, with the world
// transform an input: a .c3d passes its instance frame's, a layout passes the identity. One rule in one place, so a refdes
// means in a planar run what it means in a 3D one, and a refusal reads the same in both.

using System.Globalization;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.ThreeD;

namespace CircuitRF.Design.Layout.Em;

/// <summary>One pad of a placed part, in world metres: its footprint shape's box through the instance transform.</summary>
public sealed record PartPad(string Refdes, string Pin, string? Net, LayerKey? Layer, double Cx, double Cy,
                             double X0, double Y0, double X1, double Y1);

/// <summary>A placed part and its pads, or why no port can be built on it.</summary>
/// <param name="HasModel">Whether the board's schematic gives the part a value (null: not asked).</param>
public sealed record PlacedPart(string Refdes, IReadOnlyList<PartPad> Pads, string? Why, bool? HasModel)
{
    /// <summary>The placement's <c>SchematicId</c>.</summary>
    public string? SchematicId { get; init; }
}

/// <summary>brief-icp-1 R-icp1-6 — one cell instance a setup's component ports cut out, with the entry whose options it takes (its
/// <see cref="EmComponentPort.Refdes"/> the instance's path).</summary>
public sealed record EmCellSelection(EmComponentPort Entry, EmCellInstance Instance)
{
    /// <summary>brief-icp-9 — how the setup chose it: <c>by name</c>, <c>by its cell (Seg)</c>, <c>by "*"</c> or <c>by "X1/*"</c>.
    /// <see cref="Entry"/> is rewritten to the instance's own path, so it can no longer say.</summary>
    public string Selected { get; init; } = "by name";
}

/// <summary>One layout's placed parts, the projection's refusal when it could not be made, and its notes.</summary>
public sealed record PlacedParts(IReadOnlyList<PlacedPart> Parts, string? Refusal, IReadOnlyList<string> Notes);

public static class EmPartPads
{
    /// <summary>The nets read as a part's ground pin when nothing else says (case-insensitive).</summary>
    public static readonly IReadOnlyList<string> GroundNames = ["GND", "AGND", "DGND", "PGND", "GROUND", "VSS", "0"];

    /// <summary>
    /// The parts placed on <paramref name="view"/>, each pad's outline its footprint shape's box through the instance
    /// transform and then <paramref name="world"/> (layout metres to world metres; the identity on a layout's own run).
    /// The board projection <c>netlist --placement</c> uses (<see cref="BoardCompanions.Project"/>), so a refdes means here
    /// what it means there. Reads the footprints' files; writes nothing.
    /// </summary>
    public static PlacedParts Project(LayoutView view, string clayPath, Technology? tech, C3dTransform world)
    {
        var projection = BoardCompanions.Project(view, clayPath, tech);
        if (projection.Refusal is { } refusal) return new PlacedParts([], refusal, []);
        string layoutDir = Path.GetDirectoryName(Path.GetFullPath(clayPath)) ?? "";
        double per = C3dLowering.Metres(1, view.DbuPerMicron);
        var w = world;
        bool upright = Math.Abs(w.M20) < 1e-12 && Math.Abs(w.M21) < 1e-12 && Math.Abs(w.M02) < 1e-12 && Math.Abs(w.M12) < 1e-12;
        (double X, double Y) World(long x, long y) => (w.M00 * x * per + w.M01 * y * per + w.Tx, w.M10 * x * per + w.M11 * y * per + w.Ty);

        // GroupBy, not ToDictionary: two parts sharing a refdes (C1 and c1, or one copied) is a board to refuse by name, not a crash.
        var hasModel = projection.HasSchematic
            ? projection.Parts.GroupBy(p => p.Refdes, StringComparer.OrdinalIgnoreCase)
                              .ToDictionary(g => g.Key, g => g.Any(p => p.Value is not null || p.Description is not null), StringComparer.OrdinalIgnoreCase)
            : null;
        // A part the schematic marks Open or Short has no value to want: the netlist leaves its port open or shorts it.
        var marked = EmCoSimNetlist.ModelsFor(clayPath);
        var parts = new List<PlacedPart>();
        foreach (var group in projection.Pads.Where(p => p.Refdes is { Length: > 0 }).GroupBy(p => p.Refdes!, StringComparer.OrdinalIgnoreCase))
        {
            string rd = group.Key;
            bool? model = hasModel is null ? false : hasModel.TryGetValue(rd, out bool m) ? m : false;
            if (model == false && (marked.Open.Contains(rd) || marked.Shorted.Contains(rd))) model = true;
            var named = view.Instances.Where(i => string.Equals(i.DisplayRefDes, rd, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
            if (named.Count > 1) { parts.Add(new PlacedPart(rd, [], PlacedMoreThanOnce(rd), model)); continue; }
            var inst = named.FirstOrDefault();
            if (!upright) { parts.Add(new PlacedPart(rd, [], $"{rd}'s layout is placed tilted in this 3D view; component ports are built on a layout lying flat.", model)); continue; }
            if (inst is null) continue;
            if (inst.Rows > 1 || inst.Cols > 1) { parts.Add(new PlacedPart(rd, [], $"{rd} is an arrayed placement; a component port is built on one part's pads.", model)); continue; }
            var res = CellLayoutResolver.Resolve(inst, layoutDir, tech);
            if (res is not { State: CellLayoutState.Resolved, View: { } sub })
            {
                parts.Add(new PlacedPart(rd, [], $"{rd}'s footprint does not resolve: {res.Refusal ?? LayoutDesignFlatten.UnresolvedNote(inst.CellRef)}", model));
                continue;
            }
            var pins = CellPins.Resolve(sub, tech);
            var pads = new List<PartPad>();
            string? why = null;
            foreach (var pp in group)
            {
                // The footprint pin this pad is: the one the instance transform puts at the pad's point.
                LayoutPin? pin = pins.FirstOrDefault(p => LayoutInstanceTransform.TransformPoint(p.X, p.Y, inst, 0, 0) == (pp.X, pp.Y));
                var shape = pin is null ? null : PadShape(sub, pin);
                if (shape is null) { why ??= $"{rd}'s pad {pp.Pin} has no copper shape under its pin in its footprint."; continue; }
                var bb = LayoutGeometry.BboxOf(shape);
                var corners = new[] { (bb.MinX, bb.MinY), (bb.MaxX, bb.MinY), (bb.MaxX, bb.MaxY), (bb.MinX, bb.MaxY) }
                    .Select(c => LayoutInstanceTransform.TransformPoint(c.Item1, c.Item2, inst, 0, 0)).Select(c => World(c.X, c.Y)).ToList();
                var centre = World(pp.X, pp.Y);
                pads.Add(new PartPad(rd, pp.Pin ?? pin!.Name, pp.Net, pp.Layer ?? pin!.Layer, centre.X, centre.Y,
                                     corners.Min(c => c.X), corners.Min(c => c.Y), corners.Max(c => c.X), corners.Max(c => c.Y)));
            }
            if (inst.RotationDegrees % 90 != 0)
                why ??= $"{rd} is placed at {inst.RotationDegrees.ToString("G6", CultureInfo.InvariantCulture)}°; a component port is built " +
                        "across a gap along x or y, so the part must be placed at a multiple of 90°.";
            parts.Add(new PlacedPart(rd, pads, why, model) { SchematicId = inst.SchematicId });
        }
        return new PlacedParts(parts, null, [.. projection.Notes]);
    }

    /// <summary>Why a refdes that two placed parts share gets no component port.</summary>
    public static string PlacedMoreThanOnce(string refdes)
        => $"{refdes} is placed more than once on this layout, so which one is meant is not known.";

    /// <summary>The footprint shape a pin's pad is: on its layer, holding its point; the one named for the pin, else the smallest.</summary>
    private static LayoutShape? PadShape(LayoutView sub, LayoutPin pin)
    {
        var under = sub.Shapes.Where(s => s.Layer == pin.Layer && s is not LabelShape)
                              .Select(s => (Shape: s, Box: LayoutGeometry.BboxOf(s)))
                              .Where(t => t.Box.MinX <= pin.X && pin.X <= t.Box.MaxX && t.Box.MinY <= pin.Y && pin.Y <= t.Box.MaxY)
                              .ToList();
        return under.FirstOrDefault(t => t.Shape.Pin is { Length: > 0 } p && p == pin.Name).Shape
            ?? under.OrderBy(t => (double)(t.Box.MaxX - t.Box.MinX) * (t.Box.MaxY - t.Box.MinY)).FirstOrDefault().Shape;
    }

    // ── Which parts (R-emb1-2) ────────────────────────────────────────────────────────────────────

    /// <summary>The entries a setup's <see cref="EmSetup.ComponentPorts"/> asks for, one per part, or the refusal of the list.</summary>
    /// <param name="Wanted">The footprint parts, one entry each, refdes resolved — what LCP's gap is built on.</param>
    public sealed record Selection(IReadOnlyList<EmComponentPort> Wanted, string? Refusal)
    {
        /// <summary>brief-icp-1 R-icp1-4 — the cell instances to cut out, each with the entry whose options it takes. Empty when the
        /// caller handed no instances.</summary>
        public IReadOnlyList<EmCellSelection> Instances { get; init; } = [];

        /// <summary>brief-icp-1 R-icp1-4 — entries naming a path or a cell that matches no placed instance, each with its sentence:
        /// left out, the others run.</summary>
        public IReadOnlyList<(EmComponentPort Entry, string Why)> Unplaced { get; init; } = [];

        /// <summary>brief-icp-3 R-icp3-4 — the setup's own entries the instance side read (a named instance or path, a <c>Cell</c>,
        /// an <c>X/*</c>), as written; <c>"*"</c> is not among them, since it takes footprint parts too. What a 3D view's footprint
        /// build leaves to the cut.</summary>
        public IReadOnlyList<EmComponentPort> InstanceEntries { get; init; } = [];
    }

    /// <summary>
    /// The entries <paramref name="setup"/> names, against <paramref name="parts"/> by refdes. <c>"*"</c> takes every part with
    /// two pads, in reading order; a named entry beside <c>"*"</c> is that part's override (its Z0, Reference or width), not a
    /// second port; a part named twice is refused. A named part that is not placed is kept here: the caller refuses it by
    /// refdes. <paramref name="where"/> names what the parts are placed on, for the refusal when <c>"*"</c> finds none.
    /// </summary>
    public static Selection Select(EmSetup setup, IReadOnlyDictionary<string, PlacedPart> parts, string where)
        => Select(setup, parts, where, null);

    /// <summary>
    /// brief-icp-1 R-icp1-4 — the one selection rule for footprint parts and cell instances. With <paramref name="instances"/> null
    /// it is exactly the footprint rule above (a <c>Cell</c> entry, which only an instance can match, is set aside). With them:
    /// <list type="bullet">
    /// <item>a part whose top-level instance is not a footprint (a PCell placed with a designator) is an instance, not a part;</item>
    /// <item><c>"*"</c> also takes every device instance (<see cref="EmInstanceKind.Part"/>) with pins, walking down through copper
    /// instances and stopping at the first device on each branch; <c>X1/*</c> does the same inside <c>X1</c>;</item>
    /// <item>a <c>Cell</c> entry takes every instance of that cell, anywhere; a named path takes that instance;</item>
    /// <item>an instance takes the options of its named entry, else its cell entry, else its <c>*</c> entry; two entries of one rank
    /// that disagree are refused, as are an instance and one inside it (ICP-0 D8);</item>
    /// <item>a path or cell that matches nothing is <see cref="Selection.Unplaced"/>, with the sentence saying what is there.</item>
    /// </list>
    /// </summary>
    public static Selection Select(EmSetup setup, IReadOnlyDictionary<string, PlacedPart> parts, string where, EmCellInstanceSet? instances)
    {
        // ── Sort the entries ─────────────────────────────────────────────────────────────────────────────────────────
        static bool IsAll(EmComponentPort c) => c.Cell is null && c.Refdes.Trim() == EmSetup.AllComponents;
        static bool IsSubtreeAll(EmComponentPort c) => c.Cell is null && c.Refdes.Trim().EndsWith("/" + EmSetup.AllComponents, StringComparison.Ordinal);
        static string Key(EmComponentPort c) => c.Refdes.Trim().Trim('/');
        // A part whose top-level instance is not a footprint (a PCell placed with a designator) is an instance, not a part.
        bool IsPart(string rd) => parts.ContainsKey(rd)
                                  && (instances?.At(rd) is not { Depth: 0 } top || top.Kind == EmInstanceKind.Footprint);

        var footprintEntries = new List<EmComponentPort>();
        var instanceEntries = new List<EmComponentPort>();
        var unplaced = new List<(EmComponentPort, string)>();
        foreach (var c in setup.ComponentPorts)
        {
            if (c.Cell is not null || IsSubtreeAll(c)) continue;
            if (IsAll(c)) { footprintEntries.Add(c); continue; }
            string rd = Key(c);
            if (instances is null || IsPart(rd)) footprintEntries.Add(c);
            else if (instances.At(rd) is not null) instanceEntries.Add(c);
            else if (LooksLikePath(c)) unplaced.Add((c, instances.NamesNothing(rd)));
            else footprintEntries.Add(c);      // an unplaced refdes: the caller refuses it by refdes, as it always has
        }

        // ── Footprint parts: the rule as it always was, over the footprint entries ─────────────────────────────────────
        var overrides = new Dictionary<string, EmComponentPort>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in footprintEntries.Where(c => !IsAll(c)))
        {
            var rd = entry.Refdes.Trim();
            if (!overrides.TryAdd(rd, entry with { Refdes = rd })) return new([], $"ComponentPorts names {rd} twice; each part is one entry.");
        }
        var wanted = new List<EmComponentPort>();
        foreach (var entry in footprintEntries)
        {
            if (IsAll(entry))
            {
                foreach (var p in parts.Values.Where(p => p.Pads.Count == 2 && IsPart(p.Refdes)).OrderBy(p => p.Refdes, NaturalOrder.Instance))
                    if (!wanted.Any(w => w.Refdes.Equals(p.Refdes, StringComparison.OrdinalIgnoreCase)))
                        wanted.Add(overrides.GetValueOrDefault(p.Refdes) is { } own ? own with { Refdes = p.Refdes } : entry with { Refdes = p.Refdes });
                continue;
            }
            var rd = entry.Refdes.Trim();
            if (!wanted.Any(w => w.Refdes.Equals(rd, StringComparison.OrdinalIgnoreCase))) wanted.Add(overrides[rd]);
        }

        if (instances is null)
        {
            if (wanted.Count > 0) return new(wanted, null);
            var cells = setup.ComponentPorts.Where(c => c.Cell is not null).Select(c => c.Cell!).ToList();
            if (cells.Count > 0 && setup.ComponentPorts.All(c => c.Cell is not null))
                return new([], $"ComponentPorts selects the instances of {string.Join(", ", cells)}, and this run builds component ports on " +
                               "placed parts' pads only.");
            return new([], $"ComponentPorts asks for every placed two-terminal part, and no part placed on {where} has two pads.");
        }

        // ── Cell instances: named, by cell, then "*" ─────────────────────────────────────────────────────────────────
        var named = new Dictionary<string, EmComponentPort>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in instanceEntries)
        {
            var path = instances.At(Key(entry))!.Path;
            if (!named.TryAdd(path, entry)) return new([], $"ComponentPorts names {path} twice; each instance is one entry.");
        }

        var byCell = new Dictionary<string, EmComponentPort>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in setup.ComponentPorts.Where(c => c.Cell is not null))
        {
            var matches = instances.Instances.Where(i => i.IsCell(entry.Cell!)).ToList();
            if (matches.Count == 0) { unplaced.Add((entry, NoInstanceOf(entry.Cell!, instances))); continue; }
            foreach (var m in matches)
            {
                if (byCell.TryGetValue(m.Path, out var other) && !SameOptions(other, entry))
                    return new([], Disagree(m.Path, $"Cell {other.Cell}", $"Cell {entry.Cell}"));
                byCell.TryAdd(m.Path, entry);
            }
        }

        // "*" and "X/*": every device with pins, walking down through copper and stopping at the first device on each branch, or
        // at an instance a named or cell entry already took.
        var byAll = new Dictionary<string, EmComponentPort>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in setup.ComponentPorts.Where(c => IsAll(c) || IsSubtreeAll(c)))
        {
            string? root = null;
            if (IsSubtreeAll(entry))
            {
                string at = entry.Refdes.Trim()[..^2].Trim('/');
                if (instances.At(at) is not { } r) { unplaced.Add((entry, instances.NamesNothing(at))); continue; }
                root = r.Path;
            }
            var reached = new List<EmCellInstance>();
            void Down(string? parent)
            {
                foreach (var child in instances.ChildrenOf(parent))
                {
                    if (named.ContainsKey(child.Path) || byCell.ContainsKey(child.Path)) continue;
                    if (child.Kind == EmInstanceKind.Part) { if (child.Pins.Count > 0) reached.Add(child); }
                    else if (child.Kind == EmInstanceKind.Copper) Down(child.Path);
                }
            }
            Down(root);
            foreach (var i in reached)
            {
                if (byAll.TryGetValue(i.Path, out var other) && !SameOptions(other, entry))
                    return new([], Disagree(i.Path, other.Refdes.Trim(), entry.Refdes.Trim()));
                byAll.TryAdd(i.Path, entry);
            }
        }

        // The most specific entry wins: named, then cell, then "*".
        var chosen = new Dictionary<string, EmComponentPort>(byAll, StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in byCell) chosen[k] = v;
        foreach (var (k, v) in named) chosen[k] = v;
        string How(string path) => named.ContainsKey(path) ? "by name"
                                 : byCell.TryGetValue(path, out var c) ? $"by its cell ({c.Cell!.Trim()})"
                                 : $"by \"{byAll[path].Refdes.Trim()}\"";
        var list = instances.Instances.Where(i => chosen.ContainsKey(i.Path))
                            .Select(i => new EmCellSelection(chosen[i.Path] with { Refdes = i.Path, Cell = null }, i) { Selected = How(i.Path) })
                            .ToList();

        // ICP-0 D8: an instance inside a cut one is already out.
        foreach (var inner in list)
            if (list.FirstOrDefault(o => EmCellInstanceSet.IsInside(inner.Instance.Path, o.Instance.Path)) is { } outer)
                return new([], $"ComponentPorts selects {outer.Instance.Path} and {inner.Instance.Path}, which is inside it; an instance inside " +
                               "a cut one is already left out of the problem.");

        if (wanted.Count == 0 && list.Count == 0 && unplaced.Count == 0)
            return new([], NothingToTake(where, instances));
        return new(wanted, null)
        {
            Instances = list, Unplaced = unplaced,
            InstanceEntries = [.. setup.ComponentPorts.Where(c => !footprintEntries.Contains(c))],
        };
    }

    /// <summary>The refusal when <c>"*"</c> finds nothing: what is placed, and how to cut out a copper instance.</summary>
    private static string NothingToTake(string where, EmCellInstanceSet instances)
    {
        var copper = instances.Instances.Where(i => i.Kind == EmInstanceKind.Copper).ToList();
        string head = $"ComponentPorts asks for every placed part that is not copper, and no part placed on {where} is a two-pad part or a device";
        if (copper.Count == 0) return head + ".";
        bool one = copper.Count == 1;
        string listed = string.Join(", ", copper.Take(8).Select(i => $"{i.Path} ({i.CellLabel})")) +
                        (copper.Count > 8 ? $" and {copper.Count - 8} more" : "");
        return $"{head}; {listed} {(one ? "is" : "are")} copper the solver can solve: name {(one ? "it" : "them")}, or select " +
               $"{(one ? "its" : "their")} cell, to cut {(one ? "it" : "them")} out.";
    }

    private static string NoInstanceOf(string cell, EmCellInstanceSet instances)
    {
        var placed = instances.Instances.Select(i => i.CellLabel).Distinct(StringComparer.OrdinalIgnoreCase)
                              .OrderBy(n => n, NaturalOrder.Instance).ToList();
        if (placed.Count == 0) return $"No instance of {cell} is placed: this layout places no cell instance.";
        return $"No instance of {cell} is placed (the cells placed are {string.Join(", ", placed.Take(20))}" +
               $"{(placed.Count > 20 ? $" and {placed.Count - 20} more" : "")}).";
    }

    private static string Disagree(string path, string a, string b)
        => $"{path} is selected by two entries that disagree ({a} and {b}); state its options in one entry naming it.";

    /// <summary>Whether a named entry is spelled as an instance path rather than a refdes: it has a segment separator or is an "@n".</summary>
    private static bool LooksLikePath(EmComponentPort c) => c.Refdes.Contains('/') || c.Refdes.TrimStart().StartsWith('@');

    /// <summary>Two entries asking the same of an instance, the selector aside.</summary>
    private static bool SameOptions(EmComponentPort a, EmComponentPort b)
        => (a with { Refdes = "", Cell = null }) == (b with { Refdes = "", Cell = null });

    /// <summary>The parts a refusal lists when a named one is not placed.</summary>
    public static string KnownParts(IEnumerable<string> refdes)
    {
        var names = refdes.OrderBy(k => k, NaturalOrder.Instance).ToList();
        if (names.Count == 0) return " (it places no part with a reference designator)";
        return $" (it places {string.Join(", ", names.Take(20))}{(names.Count > 20 ? $" and {names.Count - 20} more" : "")})";
    }

    // ── Which pads pair (R-emb1-5) ────────────────────────────────────────────────────────────────

    /// <summary>
    /// R-emb1-5 — which of a part's landed pads form its ports: one port for two pads; one per signal pad against the part's
    /// own ground pad otherwise. <paramref name="reference"/> (the entry's <c>Reference</c>) names the negative end, by pin
    /// or by the conductor a pad lands on. Returns why no pairing exists, or null with <paramref name="pairs"/> filled.
    /// </summary>
    /// <param name="sameCopper">Whether a signal pad and a ground pad lie on one copper layer, so a gap port can join them.</param>
    public static string? Pair<T>(string rd, IReadOnlyList<T> lands, Func<T, PartPad> pad, Func<T, string> conductor,
                                  Func<T, bool> isGround, Func<T, T, bool> sameCopper, string? reference,
                                  out List<(T Pos, T Neg)> pairs)
    {
        pairs = [];
        var stated = reference is { Length: > 0 } refName
            ? lands.Where(l => string.Equals(pad(l).Pin, refName, StringComparison.OrdinalIgnoreCase) || conductor(l) == refName).ToList()
            : null;
        if (stated is { Count: 0 })
            return $"{rd}'s Reference '{reference}' is neither one of its pins ({string.Join(", ", lands.Select(l => pad(l).Pin))}) " +
                   "nor a conductor one of its pads lands on.";
        if (lands.Count == 2)
        {
            // The second pad is the negative end — unless Reference names the first, or the first alone is ground.
            var (a, b) = (lands[0], lands[1]);
            if (stated is [var st] ? ReferenceEquals(pad(st), pad(a)) : isGround(a) && !isGround(b)) (a, b) = (b, a);
            pairs.Add((a, b));
            return null;
        }
        if (lands.Count < 2)
            return $"{rd} has {lands.Count} pad{(lands.Count == 1 ? "" : "s")}; a component port is a gap between two of its pads.";
        var grounds = stated ?? lands.Where(isGround).ToList();
        if (grounds.Count == 0)
            return $"{rd} has {lands.Count} pads and none is its ground pin (no pad is on {string.Join("/", GroundNames.Take(3))} or the " +
                   "setup's ground): name the pin its signals return through with Reference.";
        foreach (var sig in lands.Where(l => !grounds.Any(gp => ReferenceEquals(pad(gp), pad(l)))))
        {
            // The ground pad that faces this signal pad most nearly, on the same copper.
            var facing = grounds.Where(gp => Facing(pad(sig), pad(gp)) is not null && sameCopper(sig, gp))
                                .OrderBy(gp => Dist(pad(sig), pad(gp))).ToList();
            if (facing.Count == 0)
            {
                pairs.Clear();
                return $"{rd}'s pad {pad(sig).Pin} faces none of its ground pads ({string.Join(", ", grounds.Select(gp => pad(gp).Pin))}) " +
                       "across one axis on its own copper, so no gap port can be built between them.";
            }
            pairs.Add((sig, facing[0]));
        }
        return null;
    }

    /// <summary>Whether two pads face each other across one axis: the axis (true: x) and the width they share, or null.</summary>
    public static (bool AlongX, double W0, double W1)? Facing(PartPad a, PartPad b)
    {
        bool alongX = Math.Abs(b.Cx - a.Cx) >= Math.Abs(b.Cy - a.Cy);
        double w0 = alongX ? Math.Max(a.Y0, b.Y0) : Math.Max(a.X0, b.X0);
        double w1 = alongX ? Math.Min(a.Y1, b.Y1) : Math.Min(a.X1, b.X1);
        bool apart = alongX ? a.X1 < b.X0 || b.X1 < a.X0 : a.Y1 < b.Y0 || b.Y1 < a.Y0;
        return w1 > w0 && apart ? (alongX, w0, w1) : null;
    }

    /// <summary>The sentence a pair of pads that do not face each other is refused with.</summary>
    public static string NotFacing(string label, PartPad pos, PartPad neg)
        => $"{label}: pads {pos.Pin} and {neg.Pin} do not face each other across one axis (they overlap, or share no width " +
           "across the line joining them), so no gap port can be built between them.";

    public static double Dist(PartPad a, PartPad b) => Math.Sqrt((a.Cx - b.Cx) * (a.Cx - b.Cx) + (a.Cy - b.Cy) * (a.Cy - b.Cy));
}

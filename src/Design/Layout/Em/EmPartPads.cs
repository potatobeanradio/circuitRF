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

        var hasModel = projection.HasSchematic
            ? projection.Parts.ToDictionary(p => p.Refdes, p => p.Value is not null || p.Description is not null, StringComparer.OrdinalIgnoreCase)
            : null;
        // A part the schematic marks Open or Short has no value to want: the netlist leaves its port open or shorts it.
        var marked = EmCoSimNetlist.ModelsFor(clayPath);
        var parts = new List<PlacedPart>();
        foreach (var group in projection.Pads.Where(p => p.Refdes is { Length: > 0 }).GroupBy(p => p.Refdes!, StringComparer.OrdinalIgnoreCase))
        {
            string rd = group.Key;
            bool? model = hasModel is null ? false : hasModel.TryGetValue(rd, out bool m) ? m : false;
            if (model == false && (marked.Open.Contains(rd) || marked.Shorted.Contains(rd))) model = true;
            var inst = view.Instances.FirstOrDefault(i => string.Equals(i.DisplayRefDes, rd, StringComparison.OrdinalIgnoreCase));
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
    public sealed record Selection(IReadOnlyList<EmComponentPort> Wanted, string? Refusal);

    /// <summary>
    /// The entries <paramref name="setup"/> names, against <paramref name="parts"/> by refdes. <c>"*"</c> takes every part with
    /// two pads, in reading order; a named entry beside <c>"*"</c> is that part's override (its Z0, Reference or width), not a
    /// second port; a part named twice is refused. A named part that is not placed is kept here: the caller refuses it by
    /// refdes. <paramref name="where"/> names what the parts are placed on, for the refusal when <c>"*"</c> finds none.
    /// </summary>
    public static Selection Select(EmSetup setup, IReadOnlyDictionary<string, PlacedPart> parts, string where)
    {
        var overrides = new Dictionary<string, EmComponentPort>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in setup.ComponentPorts.Where(c => c.Refdes.Trim() != EmSetup.AllComponents))
        {
            var rd = entry.Refdes.Trim();
            if (!overrides.TryAdd(rd, entry with { Refdes = rd })) return new([], $"ComponentPorts names {rd} twice; each part is one entry.");
        }
        var wanted = new List<EmComponentPort>();
        foreach (var entry in setup.ComponentPorts)
        {
            if (entry.Refdes.Trim() == EmSetup.AllComponents)
            {
                foreach (var p in parts.Values.Where(p => p.Pads.Count == 2).OrderBy(p => p.Refdes, NaturalOrder.Instance))
                    if (!wanted.Any(w => w.Refdes.Equals(p.Refdes, StringComparison.OrdinalIgnoreCase)))
                        wanted.Add(overrides.GetValueOrDefault(p.Refdes) is { } own ? own with { Refdes = p.Refdes } : entry with { Refdes = p.Refdes });
                continue;
            }
            var rd = entry.Refdes.Trim();
            if (!wanted.Any(w => w.Refdes.Equals(rd, StringComparison.OrdinalIgnoreCase))) wanted.Add(overrides[rd]);
        }
        if (wanted.Count == 0)
            return new([], $"ComponentPorts asks for every placed two-terminal part, and no part placed on {where} has two pads.");
        return new(wanted, null);
    }

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

using System.Globalization;

namespace CircuitRF.Design.Layout.PCells;

/// <summary>A schematic terminal tied to a ground symbol: the placed instance it belongs to, by its
/// <see cref="LayoutInstance.SchematicId"/>, and the terminal's 1-based number in the symbol's port order.</summary>
public readonly record struct GroundPin(string SchematicId, int Terminal);

/// <summary>
/// What <see cref="GroundArtwork.Plan"/> proposes: shapes to remove and shapes to add — as one undoable step, which the
/// caller builds — the ground-via keys the view's <see cref="LayoutView.GroundViaKeys"/> gains and loses, the pours, and
/// the counts the run reports.
/// </summary>
public sealed record GroundArtworkPlan(
    IReadOnlyList<LayoutShape> Remove, IReadOnlyList<LayoutShape> Add,
    IReadOnlyList<string> KeysAdded, IReadOnlyList<string> KeysRemoved,
    int ViasPlaced, int ViasFollowed, int ViasRemoved, int ViasLeftDeleted, int ViasLeftMoved,
    IReadOnlyList<GroundPour> Pours, IReadOnlyList<StackupLayer> PoursLeftAlone, IReadOnlyList<string> Notes)
{
    public static readonly GroundArtworkPlan None = new([], [], [], [], 0, 0, 0, 0, 0, [], [], []);

    /// <summary>Nothing to change in the view.</summary>
    public bool IsEmpty => Remove.Count == 0 && Add.Count == 0 && KeysAdded.Count == 0 && KeysRemoved.Count == 0;
}

/// <summary>
/// The ground a schematic states and a layout has to DRAW (designer feedback round 11): a via to the ground plane at
/// every pad whose schematic pin sits on a ground symbol, and the plane itself, poured on every ground conductor the
/// placed lines and those vias return through — an inner plane or an outer one alike, so a two-layer board's Bottom
/// Copper is poured as well.
///
/// <para><b>Managed, not stamped.</b> Update Layout places parts on a grid the designer then rearranges, so copper drawn
/// once at that moment would cover the grid, not the design (round 10's reason for offering the pour only on request).
/// Everything here carries <see cref="LayoutShape.Generated"/> and is REDRAWN by every Update Layout and Draw Ground
/// Pour from where the parts are then: a ground via follows its pad, and the pour is replaced. What the designer has
/// taken over is left alone — a ground via they moved stays where they put it, one they deleted is not put back
/// (<see cref="LayoutView.GroundViaKeys"/> remembers it was placed), and a ground layer carrying their own copper is not
/// poured over.</para>
///
/// <para><b>A drawn via, not a VIAGND component.</b> A VIAGND instance is a DEVICE to LVS, with no schematic
/// counterpart here, so every one would be an extra device; a drawn via is connectivity, which is what joining a pad
/// to its plane is. The via's size is the technology's default, else the via model's own (<see cref="ViaPCell"/>),
/// and its drill is the shortest via entry joining the pad's copper to the ground — <see
/// cref="SubstrateResolver.ResolveViaSpan"/>, the one a VIAGND resolves through.</para>
/// </summary>
public static class GroundArtwork
{
    /// <summary>The <see cref="LayoutShape.Generated"/> tag of a generated ground pour.</summary>
    public const string PourTag = "ground-pour";

    /// <summary>The tag prefix of a generated ground via and the copper tying it to its pad. The via's tag also
    /// records where it was placed (<c>@x,y</c>), which is how a via the designer moved is told from one that was not.</summary>
    public const string ViaTagPrefix = "ground-via ";

    /// <summary>Clear copper between the pad's edge and the via's pad, µm: the via sits beside the land, not in it, so
    /// solder is not drawn down the hole.</summary>
    public const double ViaGapMicrons = 150;

    /// <summary>The key of the ground via at <paramref name="pin"/> of the instance <paramref name="schematicId"/>.</summary>
    public static string KeyOf(string schematicId, string pin) => schematicId + ":" + pin;

    /// <summary>A ground via Update Layout placed.</summary>
    public static bool IsGroundVia(ViaShape via) => via.Generated?.StartsWith(ViaTagPrefix, StringComparison.Ordinal) == true;

    /// <summary>The key a generated ground-via shape (via or its tie) belongs to, or null.</summary>
    public static string? KeyOfShape(LayoutShape shape)
    {
        if (shape.Generated is not { } tag || !tag.StartsWith(ViaTagPrefix, StringComparison.Ordinal)) return null;
        string rest = tag[ViaTagPrefix.Length..];
        int at = rest.IndexOf(" @", StringComparison.Ordinal);
        return at < 0 ? rest : rest[..at];
    }

    /// <summary>Where a generated ground via was placed, as its tag records it.</summary>
    private static (long X, long Y)? PlacedAt(ViaShape via)
    {
        if (via.Generated is not { } tag) return null;
        int at = tag.LastIndexOf(" @", StringComparison.Ordinal);
        if (at < 0) return null;
        var xy = tag[(at + 2)..].Split(',');
        return xy.Length == 2 && long.TryParse(xy[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long x)
                              && long.TryParse(xy[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long y)
            ? (x, y) : null;
    }

    /// <summary>
    /// The plan for <paramref name="view"/>, whose instances will be <paramref name="instances"/> once the caller's own
    /// instance edits have run.
    /// </summary>
    /// <param name="grounded">Every ground-symbol pin in the schematic. Null refreshes only: the ground vias already
    /// placed follow their pads and the pour is redrawn, but none is added or removed — what Draw Ground Pour does,
    /// with no schematic to ask.</param>
    /// <param name="inSchematic">The schematic's instance names. A ground via whose instance is still in the schematic
    /// but whose pin is no longer on ground is removed (it would short that pin); one whose instance has left the
    /// schematic is left, as the instance itself is.</param>
    /// <param name="marginHeights">How far the pour reaches beyond what it covers, in substrate heights — the
    /// workspace's choice (R-dfr22-3-4).</param>
    public static GroundArtworkPlan Plan(LayoutView view, Technology? technology, string layoutBaseDir,
                                         IReadOnlyList<LayoutInstance> instances,
                                         IReadOnlyList<GroundPin>? grounded, IReadOnlySet<string>? inSchematic,
                                         double marginHeights = GroundPourPlanner.DefaultMarginHeights,
                                         IReadOnlyDictionary<string, PCellSnapshot>? snapshots = null)
    {
        if (technology is null) return GroundArtworkPlan.None;
        // The regeneration records to read cells by: the view's own, or — for a planner that has not written its
        // records into the view yet (SchematicLayoutPlanner) — the set as it will be once it has.
        snapshots ??= view.PCellSnapshots;

        var bySchematicId = new Dictionary<string, LayoutInstance>(StringComparer.Ordinal);
        foreach (var inst in instances)
            if (inst.SchematicId is { Length: > 0 } sid) bySchematicId.TryAdd(sid, inst);

        var existing = new Dictionary<string, List<LayoutShape>>(StringComparer.Ordinal);
        foreach (var s in view.Shapes)
            if (KeyOfShape(s) is { } key)
                (existing.TryGetValue(key, out var list) ? list : existing[key] = []).Add(s);

        var remove = new List<LayoutShape>();
        var add = new List<LayoutShape>();
        var keysAdded = new List<string>();
        var keysRemoved = new List<string>();
        var notes = new List<string>();
        var regions = new List<GroundPourRegion>();
        int placed = 0, followed = 0, removed = 0, leftDeleted = 0, leftMoved = 0;

        // What is wanted: a pin per key, from the schematic, or (refreshing) from the vias already there.
        var wanted = new List<(string Key, LayoutInstance Inst, LayoutView Cell, LayoutPin Pin)>();
        var wantedKeys = new HashSet<string>(StringComparer.Ordinal);
        if (grounded is not null)
        {
            foreach (var g in grounded)
            {
                if (!bySchematicId.TryGetValue(g.SchematicId, out var inst)) continue;
                if (CellOf(inst, layoutBaseDir, technology) is not { } cell) continue;
                var pins = CellPins.Resolve(cell, technology);
                if (SchematicLayoutOrientation.CellPinFor(g.Terminal, pins) is not { } pin) continue;
                string key = KeyOf(g.SchematicId, pin.Name);
                if (wantedKeys.Add(key)) wanted.Add((key, inst, cell, pin));
            }
        }
        else
        {
            foreach (string key in existing.Keys)
            {
                int colon = key.LastIndexOf(':');
                if (colon <= 0 || !bySchematicId.TryGetValue(key[..colon], out var inst)) continue;
                if (CellOf(inst, layoutBaseDir, technology) is not { } cell) continue;
                if (CellPins.Resolve(cell, technology).FirstOrDefault(p => p.Name == key[(colon + 1)..]) is not { } pin) continue;
                if (wantedKeys.Add(key)) wanted.Add((key, inst, cell, pin));
            }
        }

        foreach (var (key, inst, cell, pin) in wanted)
        {
            var built = Build(key, inst, cell, pin, technology, view.DbuPerMicron, notes);
            var have = existing.GetValueOrDefault(key);
            var haveVia = have?.OfType<ViaShape>().FirstOrDefault();

            if (have is { Count: > 0 })
            {
                if (haveVia is null || PlacedAt(haveVia) is not { } at || at != (haveVia.X, haveVia.Y))
                {
                    // The designer moved it (or took the via and kept the tie): theirs now.
                    leftMoved++;
                    if (haveVia is not null) Cover(haveVia, inst);
                    continue;
                }
                if (built is null) { Cover(haveVia, inst); continue; }
                if (built.Value.Via.X != haveVia.X || built.Value.Via.Y != haveVia.Y || !SameTie(have, built.Value.Tie))
                {
                    remove.AddRange(have);
                    add.Add(built.Value.Tie);
                    add.Add(built.Value.Via);
                    followed++;
                }
                Cover(built.Value.Via, inst, built.Value.Ground, built.Value.HeightMeters);
                if (!view.GroundViaKeys.Contains(key)) keysAdded.Add(key);
                continue;
            }

            // The designer deleted it — but only if its part was there to keep it: a part this run places afresh (the
            // designer deleted the part, or the whole layout) takes a fresh via, and the key it left is stale.
            if (view.GroundViaKeys.Contains(key) && WasPlaced(inst.SchematicId)) { leftDeleted++; continue; }
            if (built is null) continue;
            add.Add(built.Value.Tie);
            add.Add(built.Value.Via);
            keysAdded.Add(key);
            placed++;
            Cover(built.Value.Via, inst, built.Value.Ground, built.Value.HeightMeters);
        }

        if (grounded is not null && inSchematic is not null)
        {
            // A pin no longer on ground keeps no ground via: one left behind would short it to the plane.
            foreach (var (key, shapes) in existing)
            {
                if (wantedKeys.Contains(key)) continue;
                int colon = key.LastIndexOf(':');
                if (colon <= 0 || !inSchematic.Contains(key[..colon])) continue;
                var via = shapes.OfType<ViaShape>().FirstOrDefault();
                if (via is not null && PlacedAt(via) is { } at && at == (via.X, via.Y))
                {
                    remove.AddRange(shapes);
                    removed++;
                    if (view.GroundViaKeys.Contains(key)) keysRemoved.Add(key);
                }
                else
                    notes.Add($"The ground via at {key[..colon]} pin {key[(colon + 1)..]} was moved by hand and is left " +
                              "where it is, but that pin is no longer on ground in the schematic — delete it if it now " +
                              "shorts the pin to the plane.");
            }
            foreach (string key in view.GroundViaKeys)
            {
                int colon = key.LastIndexOf(':');
                if (!wantedKeys.Contains(key) && !existing.ContainsKey(key) && colon > 0 && inSchematic.Contains(key[..colon]))
                    keysRemoved.Add(key);
            }
        }

        // A VIAGND component is a ground via the schematic placed by name: the pour has to reach the plane it lands on.
        foreach (var inst in instances)
        {
            string cellName = Path.GetFileName(inst.CellRef.TrimEnd('/', '\\'));
            if (!snapshots.TryGetValue(cellName, out var snap) ||
                !string.Equals(snap.GeneratorId, ViaPCell.GroundGeneratorId, StringComparison.OrdinalIgnoreCase)) continue;
            if (ViaPCell.Resolve(snap.Parameters, technology, grounded: true).Span is not { } span) continue;
            var box = CellHierarchy.InstanceBbox(inst, layoutBaseDir);
            if (!box.IsEmpty) regions.Add(new GroundPourRegion(span.To, box, span.DielectricThicknessMeters));
        }

        // The pour, over the shapes as they will be.
        var removing = new HashSet<LayoutShape>(remove, ReferenceEqualityComparer.Instance);
        var after = view.Shapes.Where(s => !removing.Contains(s)).Concat(add).ToList();
        var pours = GroundPourPlanner.Plan(view, technology, layoutBaseDir, after, instances, regions,
                                           marginHeights: marginHeights, snapshots: snapshots);
        var planned = new HashSet<string>(pours.Pours.Select(p => p.Ground.Name), StringComparer.Ordinal);
        foreach (var pour in pours.Pours)
        {
            if (pour.Replaces is { Count: > 0 } old && SameShapes(old, pour.Shapes)) continue;   // unchanged
            remove.AddRange(pour.Replaces ?? []);
            add.AddRange(pour.Shapes);
        }
        // A generated pour on a ground nothing returns through any more is stale.
        var leftAlone = new HashSet<string>(pours.LeftAlone.Select(l => l.Name), StringComparer.Ordinal);
        foreach (var conductor in technology.Stackup.Layers.Where(l => l.Kind == StackupKind.Conductor))
        {
            if (planned.Contains(conductor.Name) || leftAlone.Contains(conductor.Name)) continue;
            remove.AddRange(view.Shapes.Where(s => s.Generated == PourTag && conductor.DrawingLayers.Contains(s.Layer)));
        }

        return new GroundArtworkPlan(remove, add, keysAdded, keysRemoved, placed, followed, removed, leftDeleted, leftMoved,
                                     pours.Pours.Where(p => !(p.Replaces is { Count: > 0 } o && SameShapes(o, p.Shapes))).ToList(),
                                     pours.LeftAlone, notes);

        bool WasPlaced(string? schematicId)
            => schematicId is { Length: > 0 } && view.Instances.Any(i => i.SchematicId == schematicId);

        void Cover(ViaShape via, LayoutInstance inst, StackupLayer? ground = null, double h = 0)
        {
            if (ground is null)
            {
                if (ViaSpanResolver.Resolve(via.Layer, technology) is not { } span) return;
                var conductors = technology.Stackup.Layers.Where(l => l.Kind == StackupKind.Conductor).ToList();
                int top = conductors.IndexOf(span.Top), bottom = conductors.IndexOf(span.Bottom);
                if (top < 0 || bottom < top) return;
                ground = conductors.Skip(top).Take(bottom - top + 1).FirstOrDefault(c => c.IsGroundReference);
                if (ground is null) return;
            }
            long r = Math.Max(via.PadSize, via.DrillSize) / 2;
            var box = new Bbox(via.X - r, via.Y - r, via.X + r, via.Y + r).Union(CellHierarchy.InstanceBbox(inst, layoutBaseDir));
            regions.Add(new GroundPourRegion(ground, box, h));
        }
    }

    private readonly record struct Built(ViaShape Via, PathShape Tie, StackupLayer Ground, double HeightMeters);

    /// <summary>The via beside <paramref name="pin"/>'s pad, on its outward side, and the copper tying the two.</summary>
    private static Built? Build(string key, LayoutInstance inst, LayoutView cell, LayoutPin pin, Technology technology,
                                int dbuPerMicron, List<string> notes)
    {
        var padConductor = technology.Stackup.Layers.FirstOrDefault(l =>
            l.Kind == StackupKind.Conductor && l.DrawingLayers.Contains(pin.Layer));
        if (padConductor is null)
        {
            notes.Add($"No ground via at {key}: its pad is on a layer the stackup does not name as a conductor.");
            return null;
        }
        var (span, failure, _) = SubstrateResolver.ResolveViaSpan(technology, padConductor.Name, null, toGround: true);
        if (span is null || span.DrillLayer is not { } drillLayer)
        {
            notes.Add($"No ground via at {key}: " + (failure?.Reason ??
                      $"no via layer in technology '{technology.Name}' joins '{padConductor.Name}' to its ground") + ".");
            return null;
        }

        long Dbu(double metres) => (long)Math.Round(metres * 1e6 * dbuPerMicron);
        long pad = Dbu(span.PadMeters ?? ViaDefaultsSi.Pad);
        long drill = Math.Min(Dbu(span.DrillMeters ?? ViaDefaultsSi.Drill), pad);

        var (vx, vy, tie) = Beside(inst, cell, pin, pad, dbuPerMicron);
        tie.Generated = ViaTagPrefix + key;
        var via = new ViaShape
        {
            Layer = drillLayer, X = vx, Y = vy, PadSize = pad, DrillSize = drill,
            Generated = ViaTagPrefix + key + string.Create(CultureInfo.InvariantCulture, $" @{vx},{vy}"),
        };
        return new Built(via, tie, span.To, span.DielectricThicknessMeters);
    }

    /// <summary>
    /// Where a via of pad <paramref name="padDbu"/> sits beside <paramref name="pin"/>'s pad — on its outward side,
    /// <see cref="ViaGapMicrons"/> clear of the land — and the copper tying the two, both in the layout's own
    /// coordinates. Shared by the generated ground via and by a VIAGND component Update Layout places beside the pad
    /// it grounds, so the two readings of one schematic land the via in the same place.
    /// </summary>
    public static (long X, long Y, PathShape Tie) Beside(LayoutInstance inst, LayoutView cell, LayoutPin pin, long padDbu,
                                                         int dbuPerMicron)
    {
        // How far the land reaches from the pin along its outward direction: its own copper, else half its width.
        double rad = pin.OutwardDeg * Math.PI / 180;
        double dx = Math.Cos(rad), dy = Math.Sin(rad);
        double reach = 0;
        bool any = false;
        foreach (var s in cell.Shapes)
        {
            if (s.Pin != pin.Name || s.Layer != pin.Layer) continue;
            var bb = LayoutGeometry.BboxOf(s);
            foreach (var (cx, cy) in new[] { (bb.MinX, bb.MinY), (bb.MaxX, bb.MinY), (bb.MaxX, bb.MaxY), (bb.MinX, bb.MaxY) })
                reach = Math.Max(reach, (cx - pin.X) * dx + (cy - pin.Y) * dy);
            any = true;
        }
        if (!any) reach = pin.WidthDbu / 2.0;

        double along = reach + ViaGapMicrons * dbuPerMicron + padDbu / 2.0;
        long lx = pin.X + (long)Math.Round(dx * along), ly = pin.Y + (long)Math.Round(dy * along);
        var (vx, vy) = LayoutInstanceTransform.TransformPoint(lx, ly, inst, 0, 0);
        var (px, py) = LayoutInstanceTransform.TransformPoint(pin.X, pin.Y, inst, 0, 0);

        // The tie is as wide as the via's pad and runs half a pad PAST the via's centre, so the via's whole annular ring sits
        // in pad-layer copper — best practice, and what the owner asked for (2026-10-04). Flush at BOTH ends, deliberately:
        // a round end at the pin reaches back past the land's inner edge into the gap between a part's two lands, and the
        // far end only has to cover the ring, which a square end does. It was the narrower of the pin and the via pad,
        // ending flush on the via's centre, which left half the ring outside the copper; nothing recorded a reason for that.
        // The CLEARANCE above is the deliberate part and is unchanged: the via stays out of the land.
        var (ex, ey) = LayoutInstanceTransform.TransformPoint(pin.X + (long)Math.Round(dx * (along + padDbu / 2.0)),
                                                              pin.Y + (long)Math.Round(dy * (along + padDbu / 2.0)), inst, 0, 0);
        var tie = new PathShape { Layer = pin.Layer, Xy = [px, py, ex, ey], Width = padDbu, End = PathEndStyle.Flush };
        return (vx, vy, tie);
    }

    /// <summary>The placed cell — side-swapped for a bottom placement (brief-bsp-1), so a ground tie
    /// lands on the copper its pin is actually on.</summary>
    private static LayoutView? CellOf(LayoutInstance inst, string layoutBaseDir, Technology? technology)
        => CellLayoutResolver.Resolve(inst, layoutBaseDir, technology) is { State: CellLayoutState.Resolved, View: { } v } ? v : null;

    private static bool SameTie(List<LayoutShape> have, PathShape tie)
        => have.OfType<PathShape>().FirstOrDefault() is { } p && p.Layer == tie.Layer && p.Width == tie.Width && p.Xy.SequenceEqual(tie.Xy);

    /// <summary>Two pours alike shape for shape — so a run that would redraw the identical pour changes nothing.</summary>
    private static bool SameShapes(IReadOnlyList<LayoutShape> a, IReadOnlyList<LayoutShape> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i] is not PolygonShape pa || b[i] is not PolygonShape pb || pa.Layer != pb.Layer || !pa.Xy.SequenceEqual(pb.Xy))
                return false;
            var ha = pa.Holes ?? [];
            var hb = pb.Holes ?? [];
            if (ha.Count != hb.Count || ha.Zip(hb).Any(h => !h.First.SequenceEqual(h.Second))) return false;
        }
        return true;
    }
}

// brief-icp-2 R-icp2-1 … R-icp2-3, R-icp2-9 — a cell instance cut out of a Planar run, and an Uncalibrated Port at each pin face.
//
// WHAT LEAVES. Pin mode: the instance's own shapes at every level (the flatten skips its path) and nothing else; then the conductor
// that meets each pin's face from outside is trimmed back to the face across its own width, so a trace drawn onto the pin pad ends
// where the pin does. Cell mode: everything on the setup's analysis levels inside the keep-out box — the parent's metal and vias
// too — cut along the box edge; the conductor crossing the box edge on the side a pin faces, within the pin's span, is that pin's.
// Shapes on a net or drawing layer the entry keeps (Keep) are never cut.
//
// THE PORT IS AN UNCALIBRATED PORT (brief-icp-2a). A calibrated edge port cannot sit at a device's pins: its feed's neighbours are
// tens of µm away against a clearance of hundreds (src/Design/RESOLVED.md §brief-icp-2 — the measurement). So the label is an edge
// port at the face's middle pointing into the conductor, driving the whole end face (PortUseEdgeWidth), with PortUncalibrated set.
//
// IN THE SOLVED PROBLEM ONLY. The shapes and labels are returned, never written: the .clay is not touched (ICP-0 D4).
//
// EVERY PIN IS CLASSIFIED BEFORE MESHING (ICP-0 D7): routed (a port), abutting (its face touches only another cut instance: no port),
// floating (nothing meets it: no port, and a warning), or left out with why. An instance whose every pin is left out is left out;
// the run refuses only when no port is left at all (the caller's rule, EmPlanarComponentPorts).

using System.Globalization;
using Clipper2Lib;
using CircuitRF.Design.ThreeD;

namespace CircuitRF.Design.Layout.Em;

/// <summary>What a cut instance's pin is to the run (ICP-0 D7).</summary>
public enum EmPinClass
{
    /// <summary>One conductor meets its face: an Uncalibrated Port.</summary>
    Routed,
    /// <summary>Its face touches only another cut-out instance: no port; the two model terminals join directly (ICP-5).</summary>
    Abutting,
    /// <summary>Tied to ground by the entry (ICP-4): no port.</summary>
    Grounded,
    /// <summary>Nothing meets it: no port, and a warning — the model sees an open there.</summary>
    Floating,
}

/// <summary>One pin of one cut instance, classified, and its port when it has one.</summary>
/// <param name="Path">The instance's LVS path.</param>
/// <param name="Cell">What a sentence calls its cell (the generator for a generated cell).</param>
/// <param name="Pin">The pin's name (empty for a whole-instance refusal).</param>
/// <param name="Class">Its class; meaningful only when <paramref name="Why"/> is null.</param>
/// <param name="Label">The Uncalibrated Port's label, in layout DBU; null when it has no port.</param>
/// <param name="WidthM">The cut face's width, metres.</param>
/// <param name="OutwardDeg">The direction the face looks out of the instance, degrees.</param>
/// <param name="Layer">The pin's drawing layer.</param>
/// <param name="Abuts">For an abutting pin, the instance (and pin, when found) it touches.</param>
/// <param name="Why">Why it is left out, naming <c>path.pin</c> and what was found; null when classified.</param>
public sealed record EmCutPin(string Path, string Cell, string Pin, EmPinClass Class, LabelShape? Label, double WidthM, double OutwardDeg,
                              LayerKey Layer, string? Abuts, string? Why)
{
    /// <summary>The port's name: <c>path.pin</c>.</summary>
    public string Name => Pin.Length > 0 ? $"{Path}.{Pin}" : Path;

    /// <summary>Whether it carries a port.</summary>
    public bool HasPort => Why is null && Label is not null;
}

/// <summary>A setup's cut instances applied to its layout's flattened geometry.</summary>
/// <param name="Shapes">The flattened geometry with the cut applied: what every extractor sees on this setup's behalf.</param>
/// <param name="Pins">Every pin of every cut instance, in instance then pin order, classified or left out.</param>
/// <param name="Excluded">The paths the flatten left out (the instances actually cut).</param>
/// <param name="Notes">One line per note: the flatten's, and a component-declared port that left with its instance.</param>
/// <param name="GeneratorIds">The PCell generators the geometry came from (<see cref="EmGeometry.Result.GeneratorIds"/>).</param>
public sealed record EmCellCutoutResult(IReadOnlyList<LayoutShape> Shapes, IReadOnlyList<EmCutPin> Pins, IReadOnlySet<string> Excluded,
                                        IReadOnlyList<string> Notes, IReadOnlyList<string> GeneratorIds);

public static class EmCellCutout
{
    /// <summary>
    /// The cut. <paramref name="cuts"/> are the selection's cell instances (<see cref="EmPartPads.Selection.Instances"/>), in the order
    /// their ports are numbered; <paramref name="instances"/> is the projection they came from. Reads the layout's cells; writes
    /// nothing.
    /// </summary>
    public static EmCellCutoutResult Apply(EmSetup setup, EmLayoutSource source, IReadOnlyList<EmCellSelection> cuts,
                                           EmCellInstanceSet instances)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(source);
        var view = source.View;
        var tech = source.Technology;
        int dpm = source.DbuPerMicron > 0 ? source.DbuPerMicron : view.DbuPerMicron;
        double per = C3dLowering.Metres(1, dpm);
        long D(double m) => (long)Math.Round(m / per, MidpointRounding.AwayFromZero);
        long delta = Math.Max(2, (long)Math.Round(0.01 * dpm));          // 10 nm: how far outside a face a probe looks

        var notes = new List<string>();
        var pins = new List<EmCutPin>();

        // ── Which instances are cut: those a port can be built on ─────────────────────────────────────────────────────
        var cutting = new List<EmCellSelection>();
        foreach (var c in cuts)
        {
            if (c.Instance.Why is { } why)
                pins.Add(new EmCutPin(c.Instance.Path, c.Instance.CellLabel, "", EmPinClass.Routed, null, 0, 0, default, null, why));
            else cutting.Add(c);
        }
        var excluded = cutting.Select(c => c.Instance.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var flat = EmGeometry.Flatten(view, source.AbsolutePath, tech, excluded);
        notes.AddRange(flat.Notes);
        var shapes = new List<LayoutShape>(flat.Shapes);
        int leftWith = EmComponentPorts.Find(view, source.AbsolutePath, tech).Count -
                       EmComponentPorts.Find(view, source.AbsolutePath, tech, excluded).Count;
        if (leftWith > 0)
            notes.Add($"{leftWith} EM port(s) declared inside the cut instance(s) leave the problem with them.");

        // ── The setup's analysis levels, their drawing layers, and the vias between them ───────────────────────────────
        var stack = tech?.Stackup.Layers ?? [];
        var levels = stack.Where(l => l.Kind == StackupKind.Conductor && !l.IsGroundReference
                                      && (setup.AnalysisLevelNames.Count == 0 || setup.AnalysisLevelNames.Contains(l.Name, StringComparer.Ordinal)))
                          .ToList();
        var levelLayers = levels.SelectMany(l => l.DrawingLayers).ToHashSet();
        var viaLayers = stack.Where(l => l.Kind == StackupKind.Via).SelectMany(l => l.DrawingLayers).ToHashSet();
        StackupLayer? ConductorOf(LayerKey layer) => stack.FirstOrDefault(l => l.Kind == StackupKind.Conductor && l.DrawingLayers.Contains(layer));
        string LayerName(LayerKey k) => tech?.Layers.FirstOrDefault(d => d.Key == k)?.Name ?? $"{k.Layer}/{k.Datatype}";
        bool Kept(LayoutShape s, IReadOnlyList<string> keep)
            => keep.Count > 0 && (s.Net is { Length: > 0 } net && keep.Contains(net, StringComparer.OrdinalIgnoreCase)
                                  || keep.Contains(LayerName(s.Layer), StringComparer.OrdinalIgnoreCase));
        string rootBaseDir = Path.GetDirectoryName(Path.GetFullPath(source.AbsolutePath)) ?? "";
        string Mm(long dbu) => (dbu * per * 1e3).ToString("0.####", CultureInfo.InvariantCulture);
        string At(long x, long y) => $"({Mm(x)}, {Mm(y)}) mm";

        // ── Cell mode: everything on the analysis levels inside each box leaves, cut along its edge ─────────────────────
        var boxes = new Dictionary<string, Rect64>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in cutting)
        {
            if (c.Entry.PortAt != EmComponentPortAt.Cell) continue;
            if (BoxOf(c, D, dpm) is not { } box)
            {
                pins.Add(new EmCutPin(c.Instance.Path, c.Instance.CellLabel, "", EmPinClass.Routed, null, 0, 0, default, null,
                    $"{c.Instance.Path} draws nothing on this setup's analysis levels, so it has no box to cut at its edge; state KeepOutUm."));
                continue;
            }
            boxes[c.Instance.Path] = box;
            var keep = c.Entry.KeepNames;
            var clip = new Paths64 { box.AsPath() };
            for (int i = shapes.Count - 1; i >= 0; i--)
            {
                var s = shapes[i];
                if (s is LabelShape or BitmapShape || !levelLayers.Contains(s.Layer) && !viaLayers.Contains(s.Layer) || Kept(s, keep)) continue;
                var b = LayoutGeometry.BboxOf(s);
                if (b.MaxX <= box.left || b.MinX >= box.right || b.MaxY <= box.top || b.MinY >= box.bottom) continue;
                shapes.RemoveAt(i);
                shapes.InsertRange(i, Subtract(s, clip));
            }
        }

        // ── Per instance, per pin ─────────────────────────────────────────────────────────────────────────────────────
        foreach (var c in cutting)
        {
            var inst = c.Instance;
            bool cellMode = c.Entry.PortAt == EmComponentPortAt.Cell;
            if (cellMode && !boxes.ContainsKey(inst.Path)) continue;
            var keep = c.Entry.KeepNames;
            var label = inst.CellLabel;
            var instPins = new List<(EmInstancePin Pin, int Index)>();
            var ordered = inst.Pins.Select((p, i) => (p, i)).OrderBy(t => t.p.Number ?? int.MaxValue).ThenBy(t => t.i).ToList();
            var multi = inst.Pins.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key)
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // The instance's own metal, in the root frame: what pin-mode trimming must not leave touching, and what a parent via
            // must not land on.
            List<LayoutShape> own = inst.Placement is { } placed
                ? [.. LayoutFlatten.FlattenAllLevels(placed, rootBaseDir, tech).Shapes.Where(s => s is not LabelShape and not BitmapShape)]
                : [];
            var ownBox = inst.Box is { } ib ? new Rect64(D(ib.X0), D(ib.Y0), D(ib.X1), D(ib.Y1)) : (Rect64?)null;

            // A parent via landing on the instance's metal (pin mode: the via survives the cut, its landing does not).
            if (!cellMode && own.Count > 0)
            {
                var ownAll = Union(own.Where(s => levelLayers.Contains(s.Layer)));
                var lands = shapes.FirstOrDefault(s => viaLayers.Contains(s.Layer) && !Kept(s, keep)
                                                       && Overlaps(Clipper.Intersect(Paths(s), ownAll, FillRule.NonZero)));
                if (lands is not null)
                {
                    var b = LayoutGeometry.BboxOf(lands);
                    pins.Add(new EmCutPin(inst.Path, label, "", EmPinClass.Routed, null, 0, 0, default, null,
                        $"{inst.Path}: a via of the parent at {At((b.MinX + b.MaxX) / 2, (b.MinY + b.MaxY) / 2)} lands on {inst.Path}'s own " +
                        "metal, which the cut removes, so the via would end in nothing; Cell mode cuts the via with it."));
                    continue;
                }
            }

            var built = new List<(EmCutPin Pin, long Px, long Py, StackupLayer Conductor)>();
            foreach (var (pin, _) in ordered)
            {
                EmCutPin Out(string why) => new(inst.Path, label, pin.Name, EmPinClass.Routed, null, pin.WidthM, pin.OutwardDeg, pin.Layer, null, why);
                string pn = $"{inst.Path}.{pin.Name}";
                if (multi.Contains(pin.Name))
                { pins.Add(Out($"{pn}: the pin name {pin.Name} is at more than one face of {label} (several fingers), and one port per pin name is built in this series.")); continue; }
                double turns = pin.OutwardDeg / 90.0;
                if (Math.Abs(turns - Math.Round(turns)) > 1e-6)
                { pins.Add(Out($"{pn}: its face looks out at {pin.OutwardDeg.ToString("G6", CultureInfo.InvariantCulture)}°, and a Planar port's face lies along x or y.")); continue; }
                var conductor = ConductorOf(pin.Layer);
                if (conductor is null || conductor.IsGroundReference || !levels.Contains(conductor))
                { pins.Add(Out($"{pn}: its layer {LayerName(pin.Layer)} is not one of this setup's analysis levels.")); continue; }

                int q = ((int)Math.Round(turns) % 4 + 4) % 4;              // 0 +x, 1 +y, 2 −x, 3 −y
                bool alongX = q is 0 or 2;                                   // the normal runs along x
                int sign = q is 0 or 1 ? 1 : -1;
                long px = D(pin.X), py = D(pin.Y), half = Math.Max(1, D(pin.WidthM) / 2);
                long tPin = alongX ? py : px;
                long face = alongX ? px : py;
                if (cellMode)
                {
                    var box = boxes[inst.Path];
                    face = q switch { 0 => box.right, 1 => box.bottom, 2 => box.left, _ => box.top };
                }
                if (setup.SolveRegion is { } region)
                {
                    double fx = (alongX ? face : tPin) * per * 1e6, fy = (alongX ? tPin : face) * per * 1e6;
                    if (fx < region.XMinUm || fx > region.XMaxUm || fy < region.YMinUm || fy > region.YMaxUm)
                    { pins.Add(Out($"{pn}: its face lies outside this EM setup's solve region {region.Describe()}.")); continue; }
                }

                // The conductor meeting the face from outside: a probe a hair outside it, across the pin's span.
                var onConductor = conductor.DrawingLayers.ToHashSet();
                var pieces = Pieces(shapes.Where(s => s is not LabelShape and not BitmapShape && onConductor.Contains(s.Layer)));
                var hit = new HashSet<int>();
                for (int k = 0; k <= 8; k++)
                {
                    long t = tPin - half + (2 * half) * k / 8;
                    long nrm = face + sign * delta;
                    var pt = alongX ? new Point64(nrm, t) : new Point64(t, nrm);
                    for (int i = 0; i < pieces.Count; i++) if (Inside(pieces[i], pt)) hit.Add(i);
                }
                if (hit.Count > 1)
                { pins.Add(Out($"{pn}: {hit.Count} separate conductors meet its face at {At(alongX ? face : tPin, alongX ? tPin : face)}; a port drives one.")); continue; }
                if (hit.Count == 0)
                {
                    var probe = alongX ? new Point64(face + sign * delta, tPin) : new Point64(tPin, face + sign * delta);
                    // Abutting: the probe lies in another cut instance's box, or at one of its pins.
                    var other = cutting.Select(o => o.Instance).Where(o => o != inst && o.Box is { } ob
                                    && probe.X >= D(ob.X0) - delta && probe.X <= D(ob.X1) + delta && probe.Y >= D(ob.Y0) - delta && probe.Y <= D(ob.Y1) + delta)
                                   .FirstOrDefault();
                    if (other is not null)
                    {
                        var touching = other.Pins.Where(op => Math.Abs(D(op.X) - px) + Math.Abs(D(op.Y) - py) <= half + 2 * delta)
                                                 .Select(op => $"{other.Path}.{op.Name}").FirstOrDefault();
                        pins.Add(new EmCutPin(inst.Path, label, pin.Name, EmPinClass.Abutting, null, pin.WidthM, pin.OutwardDeg, pin.Layer,
                                              touching ?? other.Path, null));
                        continue;
                    }
                    // Through a via the cut removed: metal of another analysis level lies over the pin.
                    var pinPt = new Point64(px, py);
                    var under = levels.Where(l => l != conductor).FirstOrDefault(l =>
                        Pieces(shapes.Where(s => s is not LabelShape and not BitmapShape && l.DrawingLayers.Contains(s.Layer))).Any(p => Inside(p, pinPt)));
                    if (under is not null)
                    { pins.Add(Out($"{pn}: the metal at it is on '{under.Name}', reached through a via the cut removed, not on the pin's own layer {LayerName(pin.Layer)}.")); continue; }
                    pins.Add(new EmCutPin(inst.Path, label, pin.Name, EmPinClass.Floating, null, pin.WidthM, pin.OutwardDeg, pin.Layer, null, null));
                    continue;
                }

                // The conductor's own width at the face: the run of it just outside, through the probe.
                var piece = pieces[hit.First()];
                long n0 = Math.Min(face, face + sign * delta), n1 = Math.Max(face, face + sign * delta);
                long tLo = long.MinValue / 4, tHi = long.MaxValue / 4;
                var band = alongX ? new Rect64(n0, tLo, n1, tHi) : new Rect64(tLo, n0, tHi, n1);
                var cross = Clipper.Intersect(piece, new Paths64 { band.AsPath() }, FillRule.EvenOdd);
                long t0 = long.MaxValue, t1 = long.MinValue;
                foreach (var path in cross)
                {
                    var bb = Clipper.GetBounds(path);
                    long a = alongX ? bb.top : bb.left, b = alongX ? bb.bottom : bb.right;
                    if (a > tPin + half || b < tPin - half) continue;
                    t0 = Math.Min(t0, a); t1 = Math.Max(t1, b);
                }
                if (t1 - t0 < 2)
                { pins.Add(Out($"{pn}: the conductor meeting it leaves no width at the face.")); continue; }

                // Pin mode: trim what reaches past the face, across the conductor's width, back to the face — only shapes that CROSS
                // the face (a trace drawn onto the pin's pad); metal wholly inside stays, and the footprint check below says so.
                if (!cellMode)
                {
                    long depth = ownBox is { } ob2 ? Math.Max(ob2.right - ob2.left, ob2.bottom - ob2.top) + delta : 4 * half + delta;
                    long m0 = sign > 0 ? face - depth : face, m1 = sign > 0 ? face : face + depth;
                    var trim = alongX ? new Rect64(m0, t0, m1, t1) : new Rect64(t0, m0, t1, m1);
                    var trimPath = new Paths64 { trim.AsPath() };
                    for (int i = shapes.Count - 1; i >= 0; i--)
                    {
                        var s = shapes[i];
                        if (s is LabelShape or BitmapShape || !onConductor.Contains(s.Layer) || Kept(s, keep)) continue;
                        var bb = LayoutGeometry.BboxOf(s);
                        if (bb.MaxX <= trim.left || bb.MinX >= trim.right || bb.MaxY <= trim.top || bb.MinY >= trim.bottom) continue;
                        long lo = alongX ? bb.MinX : bb.MinY, hi = alongX ? bb.MaxX : bb.MaxY;
                        if (!(lo < face && hi > face)) continue;
                        shapes.RemoveAt(i);
                        shapes.InsertRange(i, Subtract(s, trimPath));
                    }
                }

                long mid = (t0 + t1) / 2;
                var portLabel = new LabelShape
                {
                    Layer = pin.Layer, PortLayer = pin.Layer, X = alongX ? face : mid, Y = alongX ? mid : face, Text = pn,
                    Height = Math.Max(1, (t1 - t0) / 2), IsPort = true, PortKind = Engine.Mom.PlanarPortKind.Edge,
                    PortDirection = q switch { 0 => LayoutRotation.R0, 1 => LayoutRotation.R90, 2 => LayoutRotation.R180, _ => LayoutRotation.R270 },
                    PortUseEdgeWidth = true, PortUncalibrated = true,
                };
                var cut = new EmCutPin(inst.Path, label, pin.Name, EmPinClass.Routed, portLabel, (t1 - t0) * per, q * 90.0, pin.Layer, null, null);
                built.Add((cut, alongX ? face + sign * delta : mid, alongX ? mid : face + sign * delta, conductor));
            }

            // R-icp2-9 and the footprint check, after every pin of this instance is trimmed: each routed pin's conductor is its own,
            // and (pin mode) none of them still lies on the instance's own metal.
            var refused = new Dictionary<EmCutPin, string>();
            foreach (var grp in built.GroupBy(b => b.Conductor))
            {
                var onConductor = grp.Key.DrawingLayers.ToHashSet();
                var pieces = Pieces(shapes.Where(s => s is not LabelShape and not BitmapShape && onConductor.Contains(s.Layer)));
                var byPiece = grp.Select(b => (b.Pin, Piece: pieces.FindIndex(p => Inside(p, new Point64(b.Px, b.Py))))).ToList();
                foreach (var same in byPiece.Where(b => b.Piece >= 0).GroupBy(b => b.Piece).Where(g => g.Count() > 1))
                {
                    string names = string.Join(" and ", same.Select(s => s.Pin.Name));
                    foreach (var s in same)
                        refused[s.Pin] = $"{names} meet one piece of metal after the cut: the device would be shorted. Keep the metal that " +
                                         "joins them out of the cut (Keep), or cut at the cell edge.";
                }
                if (cellMode || own.Count == 0) continue;
                var ownOn = Clipper.InflatePaths(Union(own.Where(s => onConductor.Contains(s.Layer))), -2 * delta, JoinType.Miter, EndType.Polygon);
                foreach (var (pin, idx) in byPiece)
                {
                    if (idx < 0 || refused.ContainsKey(pin)) continue;
                    var touch = Clipper.Intersect(pieces[idx], ownOn, FillRule.NonZero);
                    if (!Overlaps(touch)) continue;
                    var bb = Clipper.GetBounds(touch);
                    refused[pin] = $"{pin.Name}: after the cut its conductor still lies on {inst.Path}'s own metal at " +
                                   $"{At((bb.left + bb.right) / 2, (bb.top + bb.bottom) / 2)} — the parent's metal overlaps the cell beyond its " +
                                   "pin. Cell mode (PortAt: Cell) cuts it at the cell edge.";
                }
            }
            foreach (var (pin, _, _, _) in built)
                pins.Add(refused.TryGetValue(pin, out var why) ? pin with { Label = null, Why = why } : pin);
        }

        return new EmCellCutoutResult(shapes, pins, excluded, notes, flat.GeneratorIds);
    }

    /// <summary>A cell-mode cut's box, in root DBU: the entry's KeepOutUm carried through the placement, else the instance's own box.</summary>
    private static Rect64? BoxOf(EmCellSelection c, Func<double, long> d, int dbuPerMicron)
    {
        if (c.Entry.KeepOutUm is { } k && c.Instance.Placement is { } placed)
        {
            long x0 = long.MaxValue, y0 = long.MaxValue, x1 = long.MinValue, y1 = long.MinValue;
            foreach (var (ux, uy) in new[] { (k.X0, k.Y0), (k.X1, k.Y0), (k.X1, k.Y1), (k.X0, k.Y1) })
            {
                var (px, py) = LayoutInstanceTransform.TransformPoint((long)Math.Round(ux * dbuPerMicron), (long)Math.Round(uy * dbuPerMicron), placed, 0, 0);
                x0 = Math.Min(x0, px); y0 = Math.Min(y0, py); x1 = Math.Max(x1, px); y1 = Math.Max(y1, py);
            }
            return new Rect64(x0, y0, x1, y1);
        }
        return c.Instance.Box is { } b ? new Rect64(d(b.X0), d(b.Y0), d(b.X1), d(b.Y1)) : null;
    }

    private static Paths64 Paths(LayoutShape s) => LayoutClipper.ToClipperPaths(s, 1);

    private static Paths64 Union(IEnumerable<LayoutShape> shapes)
    {
        var all = new Paths64();
        foreach (var s in shapes) all.AddRange(Paths(s));
        return all.Count == 0 ? all : Clipper.Union(all, FillRule.NonZero);
    }

    private static bool Overlaps(Paths64 p) => p.Count > 0 && p.Any(x => Math.Abs(Clipper.Area(x)) > 0);

    /// <summary><paramref name="s"/> with <paramref name="clip"/> taken out, as the shapes that are left (its layer and net kept).</summary>
    private static IReadOnlyList<LayoutShape> Subtract(LayoutShape s, Paths64 clip)
    {
        var c = new Clipper64();
        c.AddSubject(Paths(s));
        c.AddClip(clip);
        var tree = new PolyTree64();
        c.Execute(ClipType.Difference, FillRule.NonZero, tree);
        return LayoutClipper.FromClipperTree(tree, s.Layer, s.Net);
    }

    /// <summary>The connected pieces of <paramref name="shapes"/>' metal, each its outline followed by its holes.</summary>
    private static List<Paths64> Pieces(IEnumerable<LayoutShape> shapes)
    {
        var subject = new Paths64();
        foreach (var s in shapes) subject.AddRange(Paths(s));
        var result = new List<Paths64>();
        if (subject.Count == 0) return result;
        var tree = new PolyTree64();
        var clipper = new Clipper64();
        clipper.AddSubject(subject);
        clipper.Execute(ClipType.Union, FillRule.NonZero, tree);
        void Walk(PolyPath64 node)
        {
            for (int i = 0; i < node.Count; i++)
            {
                var child = node[i];
                if (!child.IsHole)
                {
                    var piece = new Paths64 { child.Polygon! };
                    for (int k = 0; k < child.Count; k++) piece.Add(child[k].Polygon!);
                    result.Add(piece);
                }
                Walk(child);
            }
        }
        Walk(tree);
        return result;
    }

    /// <summary>Whether <paramref name="pt"/> lies in <paramref name="piece"/>'s metal (on its outline counts; strictly in a hole does not).</summary>
    private static bool Inside(Paths64 piece, Point64 pt)
    {
        if (Clipper.PointInPolygon(pt, piece[0]) == PointInPolygonResult.IsOutside) return false;
        for (int i = 1; i < piece.Count; i++)
            if (Clipper.PointInPolygon(pt, piece[i]) == PointInPolygonResult.IsInside) return false;
        return true;
    }
}

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
    /// <summary>brief-icp-4 R-icp4-1 — only the return of other pins' ports (their Reference, or <c>Port: false</c>): no port.</summary>
    Return,
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

    /// <summary>brief-icp-4 — the cut face, root DBU (x0, y0, x1, y1; x0 = x1 for a face along y); null where none was found.</summary>
    public (long X0, long Y0, long X1, long Y1)? Face { get; init; }

    /// <summary>brief-icp-4 R-icp4-1 — the pin of the same instance this pin's port returns through; null for the run's return.</summary>
    public string? Reference { get; init; }

    /// <summary>brief-icp-4 — a pin that is only a return (<c>Port: false</c>) and that no built port returns through: floating.</summary>
    public bool Unreturned { get; init; }
}

/// <summary>A setup's cut instances applied to its layout's flattened geometry.</summary>
/// <param name="Shapes">The flattened geometry with the cut applied: what every extractor sees on this setup's behalf.</param>
/// <param name="Pins">Every pin of every cut instance, in instance then pin order, classified or left out.</param>
/// <param name="Excluded">The paths the flatten left out (the instances actually cut).</param>
/// <param name="Notes">One line per note: the flatten's, and a component-declared port that left with its instance.</param>
/// <param name="GeneratorIds">The PCell generators the geometry came from (<see cref="EmGeometry.Result.GeneratorIds"/>).</param>
public sealed record EmCellCutoutResult(IReadOnlyList<LayoutShape> Shapes, IReadOnlyList<EmCutPin> Pins, IReadOnlySet<string> Excluded,
                                        IReadOnlyList<string> Notes, IReadOnlyList<string> GeneratorIds)
{
    /// <summary>brief-icp-7 R-icp7-3 — what left the problem with each cut instance, root DBU: its box in Cell mode, the outline of its
    /// own metal on the analysis levels in Pin mode. What the layout's marks hatch and the solver-geometry preview shades.</summary>
    public IReadOnlyDictionary<string, EmCutRegion> Regions { get; init; } = new Dictionary<string, EmCutRegion>();
}

/// <summary>brief-icp-7 — one cut instance's left-out region: <paramref name="Box"/> in Cell mode, and its outline polygons.</summary>
public sealed record EmCutRegion(bool Box, IReadOnlyList<PolygonShape> Outline);

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
                // A via is not cut in two: it leaves with the box when its centre is inside it, and stays whole otherwise.
                if (s is ViaShape v) { if (!(v.X > box.left && v.X < box.right && v.Y > box.top && v.Y < box.bottom)) shapes.Insert(i, s); continue; }
                shapes.InsertRange(i, Subtract(s, clip));
            }
        }

        // ── Per instance, per pin ─────────────────────────────────────────────────────────────────────────────────────
        var regions = new Dictionary<string, EmCutRegion>(StringComparer.OrdinalIgnoreCase);
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
            // brief-icp-7 — what leaves with it: the box, or its own metal on the analysis levels (its box when it draws none there).
            regions[inst.Path] = cellMode ? new EmCutRegion(true, [Polygon(boxes[inst.Path].AsPath())])
                : Union(own.Where(s => levelLayers.Contains(s.Layer))) is { Count: > 0 } outline ? new EmCutRegion(false, [.. outline.Select(Polygon)])
                : new EmCutRegion(false, ownBox is { } ob ? [Polygon(ob.AsPath())] : []);

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

            // ── brief-icp-4 R-icp4-1/5/6 — each pin's reference, return-only pins and grounded pins, read and checked ──────────
            var plan = PinPlan.Of(c.Entry, inst, label);
            if (plan.Refusal is { } planWhy)
            { pins.Add(new EmCutPin(inst.Path, label, "", EmPinClass.Routed, null, 0, 0, default, null, planWhy)); continue; }

            var built = new List<(EmCutPin Pin, long Px, long Py, StackupLayer Conductor)>();
            var vias = new Dictionary<EmCutPin, RectShape>();
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
                    // A grounded pin with nothing at its face has nothing to tie: its terminal goes to the reference in _cosim (ICP-5).
                    pins.Add(new EmCutPin(inst.Path, label, pin.Name, plan.Grounded.Contains(pin.Name) ? EmPinClass.Grounded : EmPinClass.Floating,
                                          null, pin.WidthM, pin.OutwardDeg, pin.Layer, null, null));
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
                var faceSeg = alongX ? (face, t0, face, t1) : (t0, face, t1, face);
                double widthM = (t1 - t0) * per;
                (long Px, long Py) inside = (alongX ? face + sign * delta : mid, alongX ? mid : face + sign * delta);

                // R-icp4-5 — a grounded pin: no port, a ground via the width of the face on the conductor just outside it.
                if (plan.Grounded.Contains(pin.Name))
                {
                    var viaEntry = stack.FirstOrDefault(l => l.Kind == StackupKind.Via && l.Plated != false && l.DrawingLayers.Count > 0 &&
                        (Joins(l, conductor.Name, out string? other) && stack.Any(g => g.Kind == StackupKind.Conductor && g.IsGroundReference && g.Name == other)));
                    if (viaEntry is null)
                    { pins.Add(Out($"{pn}: no via of this technology joins '{conductor.Name}' to a ground-reference conductor, so the pin cannot be tied to ground at its face. Give it a port instead.")); continue; }
                    long depth = t1 - t0;
                    long v0 = sign > 0 ? face : face - depth, v1 = sign > 0 ? face + depth : face;
                    var viaRect = alongX ? new RectShape { Layer = viaEntry.DrawingLayers[0], X1 = v0, Y1 = t0, X2 = v1, Y2 = t1 }
                                         : new RectShape { Layer = viaEntry.DrawingLayers[0], X1 = t0, Y1 = v0, X2 = t1, Y2 = v1 };
                    long far = face + sign * (depth - delta);
                    bool fits = new[] { t0 + delta, mid, t1 - delta }.All(t => Inside(piece, alongX ? new Point64(far, t) : new Point64(t, far))
                                                                            && Inside(piece, alongX ? new Point64(face + sign * delta, t) : new Point64(t, face + sign * delta)));
                    if (!fits)
                    { pins.Add(Out($"{pn}: the conductor at its face is shorter than the face is wide, so a ground via as wide as the face does not fit on it. Give it a port instead.")); continue; }
                    var grounded = new EmCutPin(inst.Path, label, pin.Name, EmPinClass.Grounded, null, widthM, q * 90.0, pin.Layer, null, null) { Face = faceSeg };
                    vias[grounded] = viaRect;
                    built.Add((grounded, inside.Px, inside.Py, conductor));
                    continue;
                }
                // R-icp4-1 — a pin that is only a return: its face is another pin's reference, no port of its own.
                if (plan.Returns.Contains(pin.Name))
                {
                    built.Add((new EmCutPin(inst.Path, label, pin.Name, EmPinClass.Return, null, widthM, q * 90.0, pin.Layer, null, null) { Face = faceSeg },
                               inside.Px, inside.Py, conductor));
                    continue;
                }
                var portLabel = new LabelShape
                {
                    Layer = pin.Layer, PortLayer = pin.Layer, X = alongX ? face : mid, Y = alongX ? mid : face, Text = pn,
                    Height = Math.Max(1, (t1 - t0) / 2), IsPort = true, PortKind = Engine.Mom.PlanarPortKind.Edge,
                    PortDirection = q switch { 0 => LayoutRotation.R0, 1 => LayoutRotation.R90, 2 => LayoutRotation.R180, _ => LayoutRotation.R270 },
                    PortUseEdgeWidth = true, PortUncalibrated = true,
                };
                var cut = new EmCutPin(inst.Path, label, pin.Name, EmPinClass.Routed, portLabel, widthM, q * 90.0, pin.Layer, null, null)
                    { Face = faceSeg, Reference = plan.ReferenceOf(pin.Name) };
                built.Add((cut, inside.Px, inside.Py, conductor));
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
            // R-icp4-2/3 — a pin referenced to another builds where an existing construction fits, and on both solvers that is a gap
            // between two faces that face each other across one axis on one layer: LCP's strip and delta gap on Planar
            // (EmPlanarComponentPorts), the 3D gap sheet (C3dCellCuts). A Planar edge port returning through drawn metal is refused by
            // the port extraction itself (its calibration standards are single conductors over the plane), so it is not one.
            var sheetsOn = new Dictionary<string, List<(long Lo, long Hi, string Pin)>>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < built.Count; i++)
            {
                var (pin, _, _, _) = built[i];
                if (pin.Reference is not { } refName || refused.ContainsKey(pin)) continue;
                string pn = $"{inst.Path}.{pin.Pin}";
                var r = built.Select(b => b.Pin).FirstOrDefault(b => b.Pin.Equals(refName, StringComparison.OrdinalIgnoreCase));
                if (r is null || refused.ContainsKey(r))
                {
                    var said = pins.FirstOrDefault(x => x.Path == inst.Path && x.Pin.Equals(refName, StringComparison.OrdinalIgnoreCase));
                    refused[pin] = $"{pn}: its Reference {refName} " + (r is not null ? $"is left out ({refused[r]})"
                        : said is { Why: { } w } ? $"is left out ({w})"
                        : said is { Class: EmPinClass.Abutting } ? $"touches only {said.Abuts}, so it has no face of its own to return through"
                        : "has nothing connected at its face to return through") +
                        $". With Reference {EmComponentPort.GroundReference} it builds.";
                    continue;
                }
                var (sx0, sy0, sx1, sy1) = pin.Face!.Value;
                var (rx0, ry0, rx1, ry1) = r.Face!.Value;
                bool sAlongX = sx0 == sx1, rAlongX = rx0 == rx1;     // a face along y has its normal along x
                long sAt = sAlongX ? sx0 : sy0, rAt = rAlongX ? rx0 : ry0;
                long sLo = sAlongX ? sy0 : sx0, sHi = sAlongX ? sy1 : sx1, rLo = rAlongX ? ry0 : rx0, rHi = rAlongX ? ry1 : rx1;
                bool sameLevel = ConductorOf(pin.Layer) == ConductorOf(r.Layer);
                int sq = (int)Math.Round(pin.OutwardDeg / 90.0) & 3, rq = (int)Math.Round(r.OutwardDeg / 90.0) & 3;
                long rMid = (rLo + rHi) / 2;
                var returnAt = rAlongX ? new LayoutPortReturn(rAt, rMid) : new LayoutPortReturn(rMid, rAt);
                {
                    long lo = Math.Max(sLo, rLo), hi = Math.Min(sHi, rHi);
                    int sign = sq is 0 or 1 ? 1 : -1;
                    bool facing = sameLevel && sAlongX == rAlongX && ((sq + 2) & 3) == rq && (rAt - sAt) * sign < 0 && hi - lo >= 2;
                    if (!facing)
                    {
                        refused[pin] = $"{pn}: its face and its Reference {refName}'s face do not face each other across one axis on one layer " +
                                       $"({FaceWords(pin, Mm)} against {FaceWords(r, Mm)}), so no gap port can be built between them. " +
                                       $"With Reference {EmComponentPort.GroundReference} it builds.";
                        continue;
                    }
                    var onR = sheetsOn.TryGetValue(r.Pin, out var have) ? have : sheetsOn[r.Pin] = [];
                    if (onR.FirstOrDefault(o => o.Lo < hi && lo < o.Hi) is { Pin: { } clash })
                    {
                        refused[pin] = $"{pn}: its gap port and {inst.Path}.{clash}'s land on {inst.Path}.{refName}'s face across the same width; " +
                                       "two may share a face only where they do not overlap.";
                        continue;
                    }
                    onR.Add((lo, hi, pin.Pin));
                }
                // The label is this build's own, made above and not yet handed out: marked with its reference face in place, which is what
                // the builders read (and what keeps the 3D generator from building an edge sheet at it); neither solves it as it stands.
                var two = pin.Label!;
                two.PortReference = LayoutPortReference.SecondConductor;
                two.PortReturn = returnAt;
                two.PortUncalibrated = false;
            }
            // A return pin no port returns through is an open the model sees, as a floating pin is.
            foreach (var (pin, _, _, _) in built)
                if (pin.Class == EmPinClass.Return && !refused.ContainsKey(pin) &&
                    !built.Any(b => !refused.ContainsKey(b.Pin) && b.Pin.Label is not null &&
                                    string.Equals(b.Pin.Reference, pin.Pin, StringComparison.OrdinalIgnoreCase)))
                    pins.Add(new EmCutPin(inst.Path, label, pin.Pin, EmPinClass.Floating, null, pin.WidthM, pin.OutwardDeg, pin.Layer, null, null)
                             { Face = pin.Face, Unreturned = true });
            foreach (var (pin, _, _, _) in built)
            {
                if (pin.Class == EmPinClass.Return && !refused.ContainsKey(pin) && pins.Any(x => x.Unreturned && x.Path == pin.Path && x.Pin == pin.Pin))
                    continue;
                if (refused.TryGetValue(pin, out var why)) { pins.Add(pin with { Label = null, Why = why }); continue; }
                if (vias.TryGetValue(pin, out var via)) shapes.Add(via);
                pins.Add(pin);
            }
        }

        return new EmCellCutoutResult(shapes, pins, excluded, notes, flat.GeneratorIds) { Regions = regions };
    }

    /// <summary>Whether a via entry spans <paramref name="conductor"/>, and the conductor at its other end.</summary>
    private static bool Joins(StackupLayer via, string conductor, out string? other)
    {
        other = null;
        if (string.Equals(via.SpanFromLayer, conductor, StringComparison.Ordinal)) other = via.SpanToLayer;
        else if (string.Equals(via.SpanToLayer, conductor, StringComparison.Ordinal)) other = via.SpanFromLayer;
        return other is not null;
    }

    /// <summary>brief-icp-4 — one cut instance's pins as its entry configures them: which are grounded, which are only returns, and
    /// each driven pin's reference; or why the configuration cannot be built.</summary>
    private sealed class PinPlan
    {
        public HashSet<string> Grounded { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Returns { get; } = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _reference = new(StringComparer.OrdinalIgnoreCase);
        public string? Refusal { get; private set; }

        /// <summary>The pin <paramref name="pin"/>'s port returns through, or null for the run's return.</summary>
        public string? ReferenceOf(string pin) => _reference.GetValueOrDefault(pin);

        public static PinPlan Of(EmComponentPort entry, EmCellInstance inst, string cell)
        {
            var plan = new PinPlan();
            var names = inst.Pins.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            string listed = string.Join(", ", names);
            var options = entry.PinOptions;
            foreach (var named in options.Keys.Concat(entry.GroundNames))
                if (!names.Contains(named, StringComparer.OrdinalIgnoreCase))
                    return plan.Refuse($"{inst.Path}: its entry names pin {named}, which {cell} does not have (its pins are {listed}).");
            foreach (var n in names)
                if (entry.GroundNames.Contains(n, StringComparer.OrdinalIgnoreCase) || options.GetValueOrDefault(n)?.Ground == true) plan.Grounded.Add(n);
            string? RefOf(string n) => options.GetValueOrDefault(n)?.Reference ?? entry.Reference;
            foreach (var n in names)
                if (options.GetValueOrDefault(n)?.Port == false && !plan.Grounded.Contains(n)) plan.Returns.Add(n);
            foreach (var n in names)
            {
                if (plan.Grounded.Contains(n) || plan.Returns.Contains(n)) continue;
                string? r = RefOf(n);
                if (r is null || r.Equals(EmComponentPort.GroundReference, StringComparison.OrdinalIgnoreCase)) continue;
                if (r.Equals(EmComponentPort.CommonReference, StringComparison.OrdinalIgnoreCase))
                    return plan.Refuse($"{inst.Path}: Reference {EmComponentPort.CommonReference} (one node shared by its ports) has no construction: a " +
                                       "pin-referenced port is a gap between two pins' faces on either solver, and a node of their own is not a face. " +
                                       $"With Reference {EmComponentPort.GroundReference}, or a pin of {inst.Path}, it builds.");
                var target = names.FirstOrDefault(x => x.Equals(r, StringComparison.OrdinalIgnoreCase));
                if (target is null)
                    return plan.Refuse($"{inst.Path}.{n}: its Reference {r} is not one of {cell}'s pins ({listed}), {EmComponentPort.GroundReference} or " +
                                       $"{EmComponentPort.CommonReference}.");
                if (target.Equals(n, StringComparison.OrdinalIgnoreCase)) { plan.Returns.Add(n); continue; }     // the entry's own Reference pin
                if (plan.Grounded.Contains(target))
                    return plan.Refuse($"{inst.Path}.{n}: its Reference {target} is tied to ground; a grounded pin is a reference only as " +
                                       $"{EmComponentPort.GroundReference}.");
                plan._reference[n] = target;
            }
            // A pin other pins return through is only a return — unless its entry gives it a port of its own, which it cannot be as well.
            foreach (var (n, target) in plan._reference)
            {
                var own = options.GetValueOrDefault(target);
                if (own?.Port == true || own?.Reference is { } stated && !stated.Equals(target, StringComparison.OrdinalIgnoreCase))
                    return plan.Refuse($"{inst.Path}.{target} is the Reference of {string.Join(" and ", plan._reference.Where(kv => kv.Value == target).Select(kv => $"{inst.Path}.{kv.Key}"))} " +
                                       "and is also given a port of its own; at one face a pin is the return of other ports or a driven port, not both.");
                plan.Returns.Add(target);
            }
            return plan;
        }

        private PinPlan Refuse(string why) { Refusal = why; return this; }
    }

    /// <summary>A face in a sentence: where it lies and which way it looks out of the instance.</summary>
    private static string FaceWords(EmCutPin p, Func<long, string> mm)
    {
        var (x0, y0, x1, y1) = p.Face!.Value;
        string axis = x0 == x1 ? "x" : "y";
        long at = x0 == x1 ? x0 : y0;
        string looks = ((int)Math.Round(p.OutwardDeg / 90.0) & 3) switch { 0 => "+x", 1 => "+y", 2 => "−x", _ => "−y" };
        return $"{p.Pin} at {axis} = {mm(at)} mm looking {looks}";
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

    /// <summary>A shape's outline as Clipper paths. A via is its barrel (its drill, or its pad when it states no drill), a circle:
    /// brief-icp-8 found the landing check handing a <see cref="ViaShape"/> to the polygon flattener, which takes no via and threw.</summary>
    private static Paths64 Paths(LayoutShape s)
    {
        if (s is not ViaShape v) return LayoutClipper.ToClipperPaths(s, 1);
        double r = Math.Max(v.DrillSize > 0 ? v.DrillSize : v.PadSize, 2) / 2.0;
        var circle = new Path64(32);
        for (int k = 0; k < 32; k++)
            circle.Add(new Point64(v.X + (long)Math.Round(r * Math.Cos(2 * Math.PI * k / 32)), v.Y + (long)Math.Round(r * Math.Sin(2 * Math.PI * k / 32))));
        return [circle];
    }

    private static PolygonShape Polygon(Path64 p) => new() { Xy = [.. p.SelectMany(q => new[] { q.X, q.Y })] };

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

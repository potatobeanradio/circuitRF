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

    /// <summary>brief-icp-10 — the middle of <see cref="Face"/> in layout metres, for a report; null where no face was found.</summary>
    public (double X, double Y)? FaceAtM { get; init; }

    /// <summary>brief-icp-4 R-icp4-1 — the pin of the same instance this pin's port returns through; null for the run's return.</summary>
    public string? Reference { get; init; }

    /// <summary>brief-icp-4 — a pin that is only a return (<c>Port: false</c>) and that no built port returns through: floating.</summary>
    public bool Unreturned { get; init; }

    /// <summary>Why this pin's port is not what its entry asked for: its Reference could not be built, so it is built against
    /// ground (or, for a reference pin no port returns through any more, it takes a port of its own). A warning; null otherwise.</summary>
    public string? FellBack { get; init; }

    /// <summary>A routed pin whose conductor, followed through the vias, reaches no other port and no ground: its port drives an
    /// isolated piece of metal and reads an open.</summary>
    public bool Unconnected { get; init; }
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

/// <summary>
/// brief-icf-3 R-icf3-1 — the port names of a multi-finger device's pins: a pin name at one face keeps it, a pin name at k faces is
/// <c>Name[1]</c> … <c>Name[k]</c> in the cell's own pin order (which no placement changes).
/// </summary>
public static class EmPinFingers
{
    /// <summary><paramref name="pins"/> with each finger named by its index.</summary>
    public static IReadOnlyList<EmInstancePin> Named(IReadOnlyList<EmInstancePin> pins)
    {
        var count = pins.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        if (count.Values.All(c => c == 1)) return pins;
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        return [.. pins.Select(p => count[p.Name] == 1 ? p
                                    : p with { Name = $"{p.Name}[{(seen[p.Name] = seen.GetValueOrDefault(p.Name) + 1).ToString(CultureInfo.InvariantCulture)}]" })];
    }

    /// <summary>A pin's own name with any finger index taken off: <c>C[2]</c> is <c>C</c>.</summary>
    public static string Base(string name)
        => name.Length > 3 && name[^1] == ']' && name.LastIndexOf('[') is var k and > 0
           && int.TryParse(name.AsSpan(k + 1, name.Length - k - 2), NumberStyles.None, CultureInfo.InvariantCulture, out _) ? name[..k] : name;

    /// <summary>The finger index of a finger's name, or null for a pin at one face.</summary>
    public static int? Index(string name)
        => Base(name) is var b && b.Length < name.Length ? int.Parse(name.AsSpan(b.Length + 1, name.Length - b.Length - 2), CultureInfo.InvariantCulture) : null;
}

public static class EmCellCutout
{
    /// <summary>
    /// The cut. <paramref name="cuts"/> are the selection's cell instances (<see cref="EmPartPads.Selection.Instances"/>), in the order
    /// their ports are numbered; <paramref name="instances"/> is the projection they came from. Reads the layout's cells; writes
    /// nothing.
    /// </summary>
    /// <param name="footprintPads">The pads of the footprint parts this build also gives a port. Their ports are built after the cut,
    /// so the cut is told where they are: copper that reaches one is not left dangling.</param>
    public static EmCellCutoutResult Apply(EmSetup setup, EmLayoutSource source, IReadOnlyList<EmCellSelection> cuts,
                                           EmCellInstanceSet instances, IReadOnlyList<PartPad>? footprintPads = null)
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
        // brief-icp-10 — a pin on a PIN-purpose layer ("Metal1.pin") is on the conductor its layer is a purpose of.
        StackupLayer? ConductorOf(LayerKey layer) => EmPinConductor.Of(tech, layer);
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
            // brief-icf-3 R-icf3-1 — a pin name at several faces (a multi-finger device) is a port per face, C[1], C[2], … in the cell's
            // own pin order, so the numbering does not depend on how the instance is placed.
            inst = inst with { Pins = EmPinFingers.Named(inst.Pins) };
            var ordered = inst.Pins.Select((p, i) => (p, i)).OrderBy(t => t.p.Number ?? int.MaxValue).ThenBy(t => t.i).ToList();

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
                // brief-icp-10 — a via lands only on the conductors it joins: a Metal1–Metal2 via over a device's diffusion ring lands on
                // nothing the cut removes. A via whose stackup entry names no span is judged against every level, as before.
                var spanned = new Dictionary<LayerKey, Paths64>();
                Paths64 OwnUnder(LayerKey viaLayer)
                {
                    if (spanned.TryGetValue(viaLayer, out var known)) return known;
                    var entry = stack.FirstOrDefault(l => l.Kind == StackupKind.Via && l.DrawingLayers.Contains(viaLayer));
                    var ends = entry is { SpanFromLayer: { } from, SpanToLayer: { } to }
                        ? stack.Where(l => l.Kind == StackupKind.Conductor && (l.Name == from || l.Name == to)).SelectMany(l => l.DrawingLayers).ToHashSet()
                        : null;
                    return spanned[viaLayer] = ends is { Count: > 0 } ? Union(own.Where(s => ends.Contains(s.Layer))) : ownAll;
                }
                var lands = shapes.FirstOrDefault(s => viaLayers.Contains(s.Layer) && !Kept(s, keep)
                                                       && Overlaps(Clipper.Intersect(Paths(s), OwnUnder(s.Layer), FillRule.NonZero)));
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
            var returnLabels = new Dictionary<EmCutPin, LabelShape>();
            var areaPins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);       // brief-icp-10 — faces read from the interconnect
            foreach (var (pin, _) in ordered)
            {
                EmCutPin Out(string why) => new(inst.Path, label, pin.Name, EmPinClass.Routed, null, pin.WidthM, pin.OutwardDeg, pin.Layer, null, why);
                string pn = $"{inst.Path}.{pin.Name}";
                double turns = pin.OutwardDeg / 90.0;
                if (Math.Abs(turns - Math.Round(turns)) > 1e-6)
                { pins.Add(Out($"{pn}: its face looks out at {pin.OutwardDeg.ToString("G6", CultureInfo.InvariantCulture)}°, and a Planar port's face lies along x or y.")); continue; }
                var conductor = ConductorOf(pin.Layer);
                if (conductor is null || conductor.IsGroundReference || !levels.Contains(conductor))
                { pins.Add(Out($"{pn}: its layer {LayerName(pin.Layer)} is not one of this setup's analysis levels.")); continue; }

                int q = ((int)Math.Round(turns) % 4 + 4) % 4;              // 0 +x, 1 +y, 2 −x, 3 −y
                long px = D(pin.X), py = D(pin.Y), half = Math.Max(1, D(pin.WidthM) / 2);
                // brief-icp-10 — an AREA pin (a box on a pin layer, its point the box's middle): the face is where the interconnect meets it.
                var area = AreaFace(pin.Layer, conductor, own, cellMode ? flat.Shapes : shapes, cellMode ? boxes[inst.Path] : null,
                                    px, py, q, delta, pn, At);
                if (area is { Why: { } areaWhy }) { pins.Add(Out(areaWhy)); continue; }
                if (area is { } af) { (q, px, py, half) = (af.Q, af.Px, af.Py, af.Half); areaPins.Add(pin.Name); }
                bool alongX = q is 0 or 2;                                   // the normal runs along x
                int sign = q is 0 or 1 ? 1 : -1;
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
                    // brief-icp-10 — only through a via OF THE INSTANCE joining the pin's level to that one: metal on another level over
                    // a device (a shield, a crossing line) is not reached through anything the cut removed.
                    bool JoinedByOwnVia(StackupLayer other) => stack.Any(v => v.Kind == StackupKind.Via
                        && (v.SpanFromLayer == conductor.Name && v.SpanToLayer == other.Name || v.SpanToLayer == conductor.Name && v.SpanFromLayer == other.Name)
                        && Pieces(own.Where(s => v.DrawingLayers.Contains(s.Layer))).Any(p => Inside(p, pinPt)));
                    var under = levels.Where(l => l != conductor && JoinedByOwnVia(l)).FirstOrDefault(l =>
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
                    { pins.Add(Out($"{pn} is marked Ground and interconnect meets its face, so it would be tied to ground there, but no via of this " +
                                    $"technology joins '{conductor.Name}' to a ground-reference conductor. Give it a port instead (unmark Ground).")); continue; }
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
                // brief-icp-10 — the port names its level by a layer the conductor draws on: a pin-purpose layer names none, so its port
                // was left to infer the level from geometry and read two levels where a shield or a crossing lies over it.
                var portLayer = conductor.DrawingLayers.Contains(pin.Layer) || conductor.DrawingLayers.Count == 0 ? pin.Layer : conductor.DrawingLayers[0];
                var portLabel = new LabelShape
                {
                    Layer = portLayer, PortLayer = portLayer, X = alongX ? face : mid, Y = alongX ? mid : face, Text = pn,
                    Height = Math.Max(1, (t1 - t0) / 2), IsPort = true, PortKind = Engine.Mom.PlanarPortKind.Edge,
                    PortDirection = q switch { 0 => LayoutRotation.R0, 1 => LayoutRotation.R90, 2 => LayoutRotation.R180, _ => LayoutRotation.R270 },
                    PortUseEdgeWidth = true, PortUncalibrated = true,
                };
                // R-icp4-1 — a pin that is only a return: its face is another pin's reference, no port of its own. Its port label is
                // kept aside: when no port returns through it after all (every reference to it fell back to ground), it takes one.
                if (plan.Returns.Contains(pin.Name))
                {
                    var ret = new EmCutPin(inst.Path, label, pin.Name, EmPinClass.Return, null, widthM, q * 90.0, pin.Layer, null, null) { Face = faceSeg };
                    returnLabels[ret] = portLabel;
                    built.Add((ret, inside.Px, inside.Py, conductor));
                    continue;
                }
                var cut = new EmCutPin(inst.Path, label, pin.Name, EmPinClass.Routed, portLabel, widthM, q * 90.0, pin.Layer, null, null)
                {
                    Face = faceSeg, Reference = plan.ReferenceOf(pin.Name),
                    FellBack = plan.Fallen.TryGetValue(pin.Name, out var fell) ? GroundSentence(fell.Why, pn, fell.Reference) : null,
                };
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
            // A reference that cannot be built is not a reason to lose the pin: it falls back to the run's ground, as Reference Ground
            // builds it (its label is still the plain Uncalibrated Port made above), and the run warns naming what was asked.
            var sheetsOn = new Dictionary<string, List<(long Lo, long Hi, string Pin)>>(StringComparer.OrdinalIgnoreCase);
            var fellTo = new List<(string Ref, string Pin)>();
            for (int i = 0; i < built.Count; i++)
            {
                var (pin, _, _, _) = built[i];
                if (pin.Reference is not { } refName || refused.ContainsKey(pin)) continue;
                string pn = $"{inst.Path}.{pin.Pin}";
                void FallBack(string why)
                {
                    fellTo.Add((refName, pn));
                    built[i] = built[i] with { Pin = pin with { Reference = null, FellBack = GroundSentence(why, pn, refName) } };
                }
                // brief-icf-1 R-icf1-1 — the solver could not build this pin's gap port after all (Planar's strip, the 3D gap sheet):
                // it falls back here, by the same rule as a reference the shared check cannot build.
                if (setup.CutGroundFallbacks.TryGetValue(pn, out var solverWhy)) { FallBack(solverWhy); continue; }
                var r = built.Select(b => b.Pin).FirstOrDefault(b => b.Pin.Equals(refName, StringComparison.OrdinalIgnoreCase));
                if (r is null || refused.ContainsKey(r))
                {
                    var said = pins.FirstOrDefault(x => x.Path == inst.Path && x.Pin.Equals(refName, StringComparison.OrdinalIgnoreCase));
                    FallBack($"{pn}: its Reference {refName} " + (r is not null ? $"is left out ({refused[r]})"
                        : said is { Why: { } w } ? $"is left out ({w})"
                        : said is { Class: EmPinClass.Abutting } ? $"touches only {said.Abuts}, so it has no face of its own to return through"
                        : "has nothing connected at its face to return through") + ".");
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
                        FallBack($"{pn}: its face and its Reference {refName}'s face do not face each other across one axis on one layer " +
                                 $"({FaceWords(pin, Mm)} against {FaceWords(r, Mm)}), so no gap port can be built between them.");
                        continue;
                    }
                    var onR = sheetsOn.TryGetValue(r.Pin, out var have) ? have : sheetsOn[r.Pin] = [];
                    if (onR.FirstOrDefault(o => o.Lo < hi && lo < o.Hi) is { Pin: { } clash })
                    {
                        FallBack($"{pn}: its gap port and {inst.Path}.{clash}'s land on {inst.Path}.{refName}'s face across the same width; " +
                                 "two may share a face only where they do not overlap.");
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
            foreach (var (built0, _, _, _) in built)
            {
                var pin = built0;
                if (refused.TryGetValue(pin, out var why)) { pins.Add(pin with { Label = null, Why = why }); continue; }
                if (pin.Class == EmPinClass.Return && !built.Any(b => !refused.ContainsKey(b.Pin) && b.Pin.Label is not null &&
                                                                     string.Equals(b.Pin.Reference, pin.Pin, StringComparison.OrdinalIgnoreCase)))
                {
                    var from = fellTo.Where(t => t.Ref.Equals(pin.Pin, StringComparison.OrdinalIgnoreCase)).Select(t => t.Pin).ToList();
                    pin = plan.ExplicitReturns.Contains(pin.Pin) || from.Count == 0
                        // Only a return by its entry (Port: false), and no port returns through it: an open the model sees, as a
                        // floating pin is — but its face is where it is, not where the projection put a pin drawn as an area.
                        ? new EmCutPin(inst.Path, label, pin.Pin, EmPinClass.Floating, null, pin.WidthM, pin.OutwardDeg, pin.Layer, null, null)
                              { Face = pin.Face, Unreturned = true }
                        // The return of pins that all fell back to ground: nothing returns through it now, so it is routed like them.
                        : pin with
                        {
                            Class = EmPinClass.Routed, Label = returnLabels[pin],
                            FellBack = $"{pin.Name} was the Reference of {string.Join(" and ", from)}, which {(from.Count == 1 ? "is" : "are")} built " +
                                       $"against {EmComponentPort.GroundReference} instead, so it takes a port of its own, against the run's ground.",
                        };
                }
                if (vias.TryGetValue(pin, out var via)) shapes.Add(via);
                pins.Add(pin.Face is { } f && areaPins.Contains(pin.Pin) ? pin with { FaceAtM = ((f.X0 + f.X1) / 2.0 * per, (f.Y0 + f.Y1) / 2.0 * per) } : pin);
            }
        }

        // A routed pin whose conductor, followed through the vias, reaches no other port and no ground: its port drives an isolated
        // piece of metal (a via stack that misses the metal above it is the usual case) and reads an open, which "routed" hides.
        if (pins.Any(p => p.HasPort))
            foreach (int i in Dangling(pins, shapes, levels, stack, tech,
                                       (footprintPads ?? []).Select(p => (p.Layer, new Point64((long)Math.Round(p.Cx / per), (long)Math.Round(p.Cy / per))))))
                pins[i] = pins[i] with { Unconnected = true };

        return new EmCellCutoutResult(shapes, pins, excluded, notes, flat.GeneratorIds) { Regions = regions };
    }

    /// <summary>
    /// The indices of <paramref name="pins"/> whose port's conductor, followed through the vias between this setup's analysis levels,
    /// reaches no other port (a drawn port label, another cut pin's port, a pad of a footprint part given a port) and no
    /// ground-reference conductor. A footprint part's port is built after the cut, so its pads come in as <paramref name="pads"/>: the
    /// Gysel divider's ground vias are cut cells wired only to a resistor's pad, and read as dangling without them.
    /// </summary>
    private static HashSet<int> Dangling(IReadOnlyList<EmCutPin> pins, IReadOnlyList<LayoutShape> shapes, List<StackupLayer> levels,
                                         IReadOnlyList<StackupLayer> stack, Technology? tech, IEnumerable<(LayerKey? Layer, Point64 At)> pads)
    {
        var metal = shapes.Where(s => s is not LabelShape and not BitmapShape).ToList();
        var pieces = levels.Select(l => Pieces(metal.Where(s => l.DrawingLayers.Contains(s.Layer)))).ToList();
        var first = new int[levels.Count + 1];
        for (int k = 0; k < levels.Count; k++) first[k + 1] = first[k] + pieces[k].Count;
        int ground = first[^1];
        var parent = Enumerable.Range(0, ground + 1).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        IEnumerable<int> At(int level, Point64 pt)
        {
            for (int i = 0; i < pieces[level].Count; i++) if (Inside(pieces[level][i], pt)) yield return first[level] + i;
        }

        // Each via joins the pieces under its middle on the two levels it spans (every level when its entry names no span, as the
        // landing check above reads it), and the ground when one end is a ground-reference conductor.
        foreach (var via in stack.Where(l => l.Kind == StackupKind.Via && l.DrawingLayers.Count > 0))
        {
            bool toGround = stack.Any(g => g.Kind == StackupKind.Conductor && g.IsGroundReference && (g.Name == via.SpanFromLayer || g.Name == via.SpanToLayer));
            var ends = via.SpanFromLayer is null || via.SpanToLayer is null ? [.. Enumerable.Range(0, levels.Count)]
                     : new[] { levels.FindIndex(l => l.Name == via.SpanFromLayer), levels.FindIndex(l => l.Name == via.SpanToLayer) }.Where(k => k >= 0).ToList();
            foreach (var v in metal.Where(m => via.DrawingLayers.Contains(m.Layer)))
            {
                var b = LayoutGeometry.BboxOf(v);
                var mid = new Point64((b.MinX + b.MaxX) / 2, (b.MinY + b.MaxY) / 2);
                var hit = ends.SelectMany(k => At(k, mid)).ToList();
                if (toGround && hit.Count > 0) hit.Add(ground);
                for (int k = 1; k < hit.Count; k++) parent[Find(hit[k])] = Find(hit[0]);
            }
        }

        int? PieceAt(LayerKey? layer, Point64 pt)
        {
            int k = layer is { } lk && EmPinConductor.Of(tech, lk) is { } on ? levels.IndexOf(on) : -1;
            return (k >= 0 ? At(k, pt) : Enumerable.Range(0, levels.Count).SelectMany(j => At(j, pt))).Select(x => (int?)Find(x)).FirstOrDefault();
        }
        int? PieceOf(LabelShape l) => PieceAt(l.PortLayer ?? l.Layer, new Point64(l.X, l.Y));
        var own = pins.Select(p => p.HasPort ? PieceOf(p.Label!) : null).ToList();
        var ports = shapes.OfType<LabelShape>().Where(l => l.IsPort).Select(PieceOf).Concat(own)
                          .Concat(pads.Select(p => PieceAt(p.Layer, p.At))).OfType<int>()
                          .GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
        var dangling = new HashSet<int>();
        for (int i = 0; i < pins.Count; i++)
            if (own[i] is { } piece && piece != Find(ground) && ports[piece] < 2) dangling.Add(i);
        return dangling;
    }

    /// <summary>brief-icp-10 — the face of an AREA pin, or null when the pin's face is where it says (or nothing meets it).</summary>
    /// <param name="Q">The side the face looks out of: 0 +x, 1 +y, 2 −x, 3 −y.</param>
    /// <param name="Px">A point on the face (Pin mode: the middle of the pin metal's side; Cell mode: the middle of the crossing).</param>
    /// <param name="Half">Half the span the face is probed across.</param>
    /// <param name="Why">Why the pin is left out, when the interconnect meets it in a way no single face describes.</param>
    private sealed record AreaPinFace(int Q, long Px, long Py, long Half, string? Why);

    /// <summary>
    /// brief-icp-10 — a kit that marks a pin with a box on a pin layer states no face: its point is the box's middle and its direction a
    /// default (0°). Where the stated face is not on the edge of the instance's own metal — the probe just outside it is still in that
    /// metal — the face is read from the interconnect instead: the one parent conductor that meets the pin's metal, and the side of that
    /// metal it crosses (Pin mode), or the side of the box it leaves through (Cell mode, read before the box was cut). A pin whose stated
    /// face IS on its metal's edge is untouched, so every edge-pinned cell builds exactly as before; nothing meeting the pin returns null
    /// too, and the ordinary classification (floating, abutting, through a via) follows.
    /// </summary>
    private static AreaPinFace? AreaFace(LayerKey pinLayer, StackupLayer conductor, IReadOnlyList<LayoutShape> own,
                                         IReadOnlyList<LayoutShape> parent, Rect64? box, long px, long py, int q, long delta, string pn,
                                         Func<long, long, string> at)
    {
        var onConductor = conductor.DrawingLayers.ToHashSet();
        var pinPt = new Point64(px, py);
        var region = Pieces(own.Where(s => onConductor.Contains(s.Layer) || s.Layer == pinLayer)).FirstOrDefault(p => Inside(p, pinPt));
        if (region is null) return null;
        var outside = q switch { 0 => new Point64(px + delta, py), 1 => new Point64(px, py + delta), 2 => new Point64(px - delta, py), _ => new Point64(px, py - delta) };
        if (!Inside(region, outside)) return null;                       // an edge pin: its face is where it says

        var grown = Clipper.InflatePaths(region, delta, JoinType.Miter, EndType.Polygon);
        var meeting = Pieces(parent.Where(s => s is not LabelShape and not BitmapShape && onConductor.Contains(s.Layer)))
                      .Where(p => Overlaps(Clipper.Intersect(p, grown, FillRule.NonZero))).ToList();
        if (meeting.Count == 0) return null;
        if (meeting.Count > 1)
            return new AreaPinFace(q, px, py, 0, $"{pn}: {meeting.Count} separate conductors meet its metal at {at(px, py)}; a port drives one.");
        var piece = meeting[0];

        // The four strips just outside a rectangle's sides, each as (side, strip).
        static (int Side, Rect64 Strip)[] Strips(Rect64 r, long d) =>
        [
            (0, new Rect64(r.right, r.top, r.right + d, r.bottom)), (1, new Rect64(r.left, r.bottom, r.right, r.bottom + d)),
            (2, new Rect64(r.left - d, r.top, r.left, r.bottom)),   (3, new Rect64(r.left, r.top - d, r.right, r.top)),
        ];
        var frame = box ?? Clipper.GetBounds(region[0]);
        var crossing = Strips(frame, delta)
            .Select(s => (s.Side, Cut: Clipper.Intersect(piece, new Paths64 { s.Strip.AsPath() }, FillRule.NonZero)))
            .Where(s => Overlaps(s.Cut)).ToList();
        string where = box is null ? "its metal" : "the keep-out box";
        if (crossing.Count == 0)
            return new AreaPinFace(q, px, py, 0, $"{pn}: the conductor meeting it does not cross a side of {where}, so it has no face to put a port at.");
        if (crossing.Count > 1)
            return new AreaPinFace(q, px, py, 0, $"{pn}: the conductor meeting it crosses {crossing.Count} sides of {where}; a port's face is one side.");
        var (side, cut) = crossing[0];
        bool alongX = side is 0 or 2;
        long faceAt = side switch { 0 => frame.right, 1 => frame.bottom, 2 => frame.left, _ => frame.top };
        // The span probed is the crossing itself, not the whole side: a 0.5 µm trace meeting an 8 µm plate falls between probes
        // spread over the side.
        var b = Clipper.GetBounds(cut);
        long lo = (alongX ? b.top : b.left) - delta, hi = (alongX ? b.bottom : b.right) + delta;
        long mid = (lo + hi) / 2;
        return new AreaPinFace(side, alongX ? faceAt : mid, alongX ? mid : faceAt, Math.Max(1, (hi - lo) / 2), null);
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
        /// <summary>The returns the entry made so itself (<c>Port: false</c>), not only by being another pin's Reference.</summary>
        public HashSet<string> ExplicitReturns { get; } = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _reference = new(StringComparer.OrdinalIgnoreCase);
        public string? Refusal { get; private set; }

        /// <summary>The pin <paramref name="pin"/>'s port returns through, or null for the run's return.</summary>
        public string? ReferenceOf(string pin) => _reference.GetValueOrDefault(pin);

        /// <summary>brief-icf-3 R-icf3-2 — a finger whose Reference is a pin at another number of faces: no face to pair it with, so it
        /// is built against ground; the sentence says why. By finger name.</summary>
        public Dictionary<string, (string Reference, string Why)> Fallen { get; } = new(StringComparer.OrdinalIgnoreCase);

        public static PinPlan Of(EmComponentPort entry, EmCellInstance inst, string cell)
        {
            var plan = new PinPlan();
            var ci = StringComparer.OrdinalIgnoreCase;
            // brief-icf-3 — the pins by their port names (a finger is C[1], C[2]); the entry names a pin by its base name, which takes
            // every finger of it, or a finger by its own.
            var names = inst.Pins.Select(p => p.Name).Distinct(ci).ToList();
            var bases = names.Select(EmPinFingers.Base).Distinct(ci).ToList();
            string listed = string.Join(", ", bases);
            var options = entry.PinOptions;
            EmPinOptions? Opt(string n) => options.GetValueOrDefault(n) ?? options.GetValueOrDefault(EmPinFingers.Base(n));
            bool InGround(string n) => entry.GroundNames.Contains(n, ci) || entry.GroundNames.Contains(EmPinFingers.Base(n), ci);
            foreach (var named in options.Keys.Concat(entry.GroundNames))
                if (!names.Contains(named, ci) && !bases.Contains(named, ci))
                    return plan.Refuse($"{inst.Path}: its entry names pin {named}, which {cell} does not have (its pins are {listed}).");
            foreach (var n in names)
                if (InGround(n) || Opt(n)?.Ground == true) plan.Grounded.Add(n);
            string? RefOf(string n) => Opt(n)?.Reference ?? entry.Reference;
            foreach (var n in names)
                if (Opt(n)?.Port == false && !plan.Grounded.Contains(n)) { plan.Returns.Add(n); plan.ExplicitReturns.Add(n); }
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
                if (target is null && bases.Contains(r, ci))
                {
                    // brief-icf-3 R-icf3-2 — a Reference with fingers: finger i against its finger i, when the two have as many faces.
                    var theirs = names.Where(x => ci.Equals(EmPinFingers.Base(x), r)).ToList();
                    var mine = names.Where(x => ci.Equals(EmPinFingers.Base(x), EmPinFingers.Base(n))).ToList();
                    if (theirs.Count == mine.Count) target = theirs[mine.IndexOf(n)];
                    else
                    {
                        plan.Fallen[n] = (r, $"{inst.Path}.{n}: its Reference {r} is at {theirs.Count} face(s) and {EmPinFingers.Base(n)} at " +
                                             $"{mine.Count}, so their fingers cannot be paired face to face.");
                        continue;
                    }
                }
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
                var own = Opt(target);
                if (own?.Port == true || own?.Reference is { } stated && !stated.Equals(target, StringComparison.OrdinalIgnoreCase)
                                                                      && !stated.Equals(EmPinFingers.Base(target), StringComparison.OrdinalIgnoreCase))
                    return plan.Refuse($"{inst.Path}.{target} is the Reference of {string.Join(" and ", plan._reference.Where(kv => kv.Value == target).Select(kv => $"{inst.Path}.{kv.Key}"))} " +
                                       "and is also given a port of its own; at one face a pin is the return of other ports or a driven port, not both.");
                plan.Returns.Add(target);
            }
            return plan;
        }

        private PinPlan Refuse(string why) { Refusal = why; return this; }
    }

    /// <summary>The one sentence a pin built against ground instead of its Reference carries.</summary>
    private static string GroundSentence(string why, string pin, string reference)
        => $"{why} {pin} is built against {EmComponentPort.GroundReference} instead of its Reference {reference}: an Uncalibrated Port " +
           "at its face, returning through the run's ground.";

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

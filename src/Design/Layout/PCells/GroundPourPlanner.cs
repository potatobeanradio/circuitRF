using Clipper2Lib;

namespace CircuitRF.Design.Layout.PCells;

/// <summary>One ground pour <see cref="GroundPourPlanner"/> proposes: the copper to draw on
/// <paramref name="DrawingLayer"/>, which is <paramref name="Ground"/>'s drawing layer.</summary>
/// <param name="Shapes">The pour, with a clearance hole around every via that passes through the plane. More than one
/// shape only when clearances split the rectangle. Each carries <see cref="GroundArtwork.PourTag"/>.</param>
/// <param name="Lines">How many placed microstrip instances return through this plane.</param>
/// <param name="ViaClearances">How many vias passing through the plane were given a clearance hole — drawn vias,
/// with <see cref="GroundPourPlanner.ViaClearanceMicrons"/> round their pad, and placed VIA components, with
/// their own antipad.</param>
/// <param name="ViasJoined">How many vias the designer drew END on the plane inside the pour, and so are joined to it.
/// A layout carries no nets to tell a ground via from a signal via landing on that layer, so these are counted for the
/// designer to check rather than guessed at.</param>
/// <param name="MarginDbu">How far the pour reaches beyond the lines' own artwork (zero when a board outline bounds it).</param>
/// <param name="Replaces">The generated pour already on this layer, which this one replaces.</param>
/// <param name="GroundVias">How many of Update Layout's own ground vias (<see cref="GroundArtwork"/>), and of the VIAGND
/// components landing on this plane, the pour joins — known to be ground, so never cut and never counted in <paramref name="ViasJoined"/>.</param>
/// <param name="BoundedByOutline">The pour fills the board outline rather than a margin round the artwork.</param>
/// <param name="MarginHeights">The margin it was asked for, in substrate heights (R-dfr22-3-4).</param>
public sealed record GroundPour(
    StackupLayer Ground, LayerKey DrawingLayer, IReadOnlyList<LayoutShape> Shapes,
    int Lines, int ViaClearances, int ViasJoined, long MarginDbu,
    IReadOnlyList<LayoutShape>? Replaces = null, int GroundVias = 0, bool BoundedByOutline = false,
    double MarginHeights = GroundPourPlanner.DefaultMarginHeights);

/// <summary>A region a ground conductor has to cover besides the microstrip lines — a ground via and the part it
/// grounds (<see cref="GroundArtwork"/>) — with the substrate height its margin is taken in.</summary>
public sealed record GroundPourRegion(StackupLayer Ground, Bbox Region, double SubstrateHeightMeters);

/// <summary>What a plan proposes, and the ground conductors it left alone because the designer has drawn on them.</summary>
public sealed record GroundPourOutcome(IReadOnlyList<GroundPour> Pours, IReadOnlyList<StackupLayer> LeftAlone);

/// <summary>
/// The ground plane a layout's microstrip lines return through, drawn as artwork (designer feedback round 10).
///
/// <para><b>Why a planner and not the line generator.</b> A microstrip's generated artwork is its LINE: the ground it
/// returns through is the stackup's plane, and no generator draws it. One plane per line would overlap its neighbours
/// and run straight through the via a layer change puts at a line's end, shorting it. So the plane is ONE pour per
/// ground conductor, covering every placed microstrip that returns through it, with a clearance hole cut around every
/// via that passes through the plane. It is computed from where the lines ARE, and since designer feedback round 11
/// it is GENERATED copper (<see cref="LayoutShape.Generated"/>) that every Update Layout and Draw Ground Pour redraws
/// (<see cref="GroundArtwork"/>) — Update Layout places new lines on a grid the designer then rearranges, and a
/// redrawn pour follows them where one drawn once would not.</para>
///
/// <para><b>What it does not know.</b> A via's clearance is <see cref="ViaClearanceMicrons"/> beyond its pad, a
/// default and said to be one; a via inside a placed cell is not seen (only the layout's own vias are); and a via
/// ending ON the plane is left joined to it and COUNTED, since without nets a ground via and a signal via landing on
/// that layer look the same.</para>
/// </summary>
public static class GroundPourPlanner
{
    /// <summary>The generators whose artwork is a microstrip line returning through a ground plane.</summary>
    public static readonly IReadOnlySet<string> MicrostripGenerators = new HashSet<string>(StringComparer.Ordinal)
    {
        MlinPCell.GeneratorId, CoupledLinePCells.McLinId, MBendPCell.GeneratorId, MCurvePCell.GeneratorId,
        MMiterPCell.GeneratorId, MTeePCell.GeneratorId,
        MCrossPCell.GeneratorId, MTaperPCell.GeneratorId, MKlopfPCell.GeneratorId,
    };

    /// <summary>The clearance ring around a via's pad where it passes through the plane, µm. A default: no
    /// technology states an antipad yet.</summary>
    public const double ViaClearanceMicrons = 250;

    /// <summary>How far the pour reaches beyond the lines, in substrate heights, unless the workspace says otherwise
    /// (R-dfr22-3-4, the <c>.cws</c>'s <c>GroundPourMarginHeights</c>): a microstrip's field is confined to a few h
    /// either side of the strip, so five keeps the plane's edge out of it.</summary>
    public const double DefaultMarginHeights = 5;

    /// <summary>How far a pour bounded by the board outline is pulled back from the board's edge, µm.</summary>
    public const double OutlinePullbackMicrons = 250;

    /// <summary>
    /// One pour per ground conductor that a placed microstrip returns through. <paramref name="onlyUndrawn"/> skips a
    /// conductor that carries the designer's own artwork: a plane the designer drew is theirs, and drawing another over
    /// it would fill the clearances they cut in it. Copper Update Layout generated (<see cref="LayoutShape.Generated"/>)
    /// is not the designer's, and is what a new pour replaces.
    /// </summary>
    public static IReadOnlyList<GroundPour> Plan(LayoutView view, Technology? technology, string layoutBaseDir,
                                                 bool onlyUndrawn = true, double marginHeights = DefaultMarginHeights)
        => Plan(view, technology, layoutBaseDir, view.Shapes, view.Instances, [], onlyUndrawn, marginHeights).Pours;

    /// <summary>
    /// The pours for <paramref name="shapes"/> and <paramref name="instances"/> as they WILL be — Update Layout plans
    /// before its command runs — plus the <paramref name="extra"/> regions its ground vias need covered (designer
    /// feedback round 11). A ground conductor named only by an extra region, with no line returning through it, is
    /// poured too: a ground via has to land on something. When the technology names a board outline and the layout
    /// draws one, the pour fills it, <see cref="OutlinePullbackMicrons"/> in from the edge; otherwise it reaches
    /// <paramref name="marginHeights"/> substrate heights beyond everything it covers — a workspace's choice, passed
    /// in, since a preference is an argument here.
    /// </summary>
    public static GroundPourOutcome Plan(LayoutView view, Technology? technology, string layoutBaseDir,
                                         IReadOnlyList<LayoutShape> shapes, IEnumerable<LayoutInstance> instances,
                                         IEnumerable<GroundPourRegion> extra, bool onlyUndrawn = true,
                                         double marginHeights = DefaultMarginHeights,
                                         IReadOnlyDictionary<string, PCellSnapshot>? snapshots = null)
    {
        if (technology is null) return new([], []);
        snapshots ??= view.PCellSnapshots;
        if (!(marginHeights >= 0) || !double.IsFinite(marginHeights)) marginHeights = DefaultMarginHeights;

        var byGround = new Dictionary<string, (StackupLayer Ground, Bbox Region, double MaxH, int Lines)>(StringComparer.Ordinal);
        var order = new List<string>();
        void Note(StackupLayer ground, Bbox bb, double h, int lines)
        {
            if (!byGround.TryGetValue(ground.Name, out var acc))
            {
                acc = (ground, Bbox.Empty, 0, 0);
                order.Add(ground.Name);
            }
            byGround[ground.Name] = (ground, acc.Region.Union(bb), Math.Max(acc.MaxH, h), acc.Lines + lines);
        }

        foreach (var inst in instances)
        {
            string cell = Path.GetFileName(inst.CellRef.TrimEnd('/', '\\'));
            if (!snapshots.TryGetValue(cell, out var snap) || !MicrostripGenerators.Contains(snap.GeneratorId))
                continue;
            var selection = new PCellLayerSelection(snap.SignalLayerNameOverride, snap.GroundLayerNameOverride);
            var (substrate, _, _) = SubstrateResolver.ResolveElectrical(technology, selection);
            if (substrate is null) continue;
            var ground = technology.Stackup.Layers.FirstOrDefault(l =>
                l.Kind == StackupKind.Conductor && l.Name == substrate.GroundConductorName);
            if (ground is null || ground.DrawingLayers.Count == 0) continue;

            var bb = CellHierarchy.InstanceBbox(inst, layoutBaseDir);
            if (bb.IsEmpty) continue;
            Note(ground, bb, substrate.HeightMeters, 1);
        }
        foreach (var r in extra)
            if (r.Ground.DrawingLayers.Count > 0 && !r.Region.IsEmpty) Note(r.Ground, r.Region, r.SubstrateHeightMeters, 0);

        var outline = OutlineBox(shapes, technology, view.DbuPerMicron);
        var pours = new List<GroundPour>();
        var leftAlone = new List<StackupLayer>();
        foreach (string name in order)
        {
            var (ground, region, maxH, lines) = byGround[name];
            if (onlyUndrawn && shapes.Any(s => s is not ViaShape && s.Generated is null && ground.DrawingLayers.Contains(s.Layer)))
            {
                leftAlone.Add(ground);
                continue;
            }

            long margin = (long)Math.Ceiling(marginHeights * maxH * 1e6 * view.DbuPerMicron);
            var outer = outline ?? new Bbox(region.MinX - margin, region.MinY - margin, region.MaxX + margin, region.MaxY + margin);
            var (clearances, joined, groundVias) = Clearances(view, snapshots, shapes, instances, technology, ground, outer);

            var layer = ground.DrawingLayers[0];
            var subject = new Paths64 { Rect(outer) };
            var tree = new PolyTree64();
            Clipper.BooleanOp(ClipType.Difference, subject, clearances, tree, FillRule.NonZero);
            var drawn = LayoutClipper.FromClipperTree(tree, layer, null);
            foreach (var shape in drawn) shape.Generated = GroundArtwork.PourTag;
            var replaces = shapes.Where(s => s.Generated == GroundArtwork.PourTag && ground.DrawingLayers.Contains(s.Layer)).ToList();
            pours.Add(new GroundPour(ground, layer, drawn, lines, clearances.Count, joined, outline is null ? margin : 0,
                                     replaces, groundVias, outline is not null, marginHeights));
        }
        return new(pours, leftAlone);
    }

    /// <summary>The board outline's extent, pulled back from the edge — or null when the technology names no outline
    /// layer or nothing is drawn on one. The outline layers are <see cref="Em3d.Em3dGenerator.BoardOutlineLayers"/>'s,
    /// the one place that names them.</summary>
    private static Bbox? OutlineBox(IReadOnlyList<LayoutShape> shapes, Technology technology, int dbuPerMicron)
    {
        var layers = Em3d.Em3dGenerator.BoardOutlineLayers(technology);
        if (layers.Count == 0) return null;
        var box = Bbox.Empty;
        foreach (var s in shapes)
            if (layers.Contains(s.Layer)) box = box.Union(LayoutGeometry.BboxOf(s));
        if (box.IsEmpty) return null;
        long pull = (long)Math.Round(OutlinePullbackMicrons * dbuPerMicron);
        var inset = new Bbox(box.MinX + pull, box.MinY + pull, box.MaxX - pull, box.MaxY - pull);
        return inset.IsEmpty ? box : inset;
    }

    /// <summary>A disc around every layout via whose barrel passes THROUGH <paramref name="ground"/> inside
    /// <paramref name="outer"/>. A via whose span is unstated goes through every layer, as everywhere else.</summary>
    private static (Paths64 Discs, int Joined, int GroundVias) Clearances(
        LayoutView view, IReadOnlyDictionary<string, PCellSnapshot> snapshots, IReadOnlyList<LayoutShape> shapes,
        IEnumerable<LayoutInstance> instances, Technology technology, StackupLayer ground, Bbox outer)
    {
        var layers = technology.Stackup.Layers;
        int g = IndexOf(layers, ground);
        var drill = ViaSpanResolver.DrillLayerKeys(technology);
        long ring = (long)Math.Round(ViaClearanceMicrons * view.DbuPerMicron);
        var discs = new Paths64();
        int joined = 0, groundVias = 0;
        // A placed VIA or VIAGND component (ViaPCell) is a via too, and the only one here that states
        // its own antipad: its clearance is that diameter, in every plane its drill passes and does not
        // land on — which is the same span its model was resolved against.
        foreach (var inst in instances)
        {
            string cell = Path.GetFileName(inst.CellRef.TrimEnd('/', '\\'));
            if (!snapshots.TryGetValue(cell, out var snap)) continue;
            if (string.Equals(snap.GeneratorId, TestPadPCell.GeneratorId, StringComparison.OrdinalIgnoreCase))
            {
                if (TestPadClearance(TestPadPCell.Resolve(snap.Parameters, technology), technology, ground, view.DbuPerMicron) is { } tr
                    && inst.X + tr >= outer.MinX && inst.X - tr <= outer.MaxX && inst.Y + tr >= outer.MinY && inst.Y - tr <= outer.MaxY)
                    discs.Add(Disc(inst.X, inst.Y, tr));
                continue;
            }
            bool grounded = string.Equals(snap.GeneratorId, ViaPCell.GroundGeneratorId, StringComparison.OrdinalIgnoreCase);
            if (!grounded && !string.Equals(snap.GeneratorId, ViaPCell.GeneratorId, StringComparison.OrdinalIgnoreCase)) continue;

            var r = ViaPCell.Resolve(snap.Parameters, technology, grounded);
            if (r.Span is not { } span) continue;
            long radius = (long)Math.Round(r.Antipad / 2 * 1e6 * view.DbuPerMicron);
            if (inst.X + radius < outer.MinX || inst.X - radius > outer.MaxX ||
                inst.Y + radius < outer.MinY || inst.Y - radius > outer.MaxY) continue;

            // A VIAGND landing here is KNOWN to be ground, as Update Layout's own ground via is: counted with those,
            // not among the vias a designer has to check for a signal one.
            if (grounded && ReferenceEquals(span.To, ground)) { groundVias++; continue; }
            if (ReferenceEquals(span.From, ground) || ReferenceEquals(span.To, ground)) { joined++; continue; }
            int from = IndexOf(layers, span.From), to = IndexOf(layers, span.To);
            int top = Math.Min(from, to), bottom = Math.Max(from, to);
            if (span.ViaEntry is { } entry && ViaSpanResolver.Resolve(entry, technology) is { } drillSpan)
                (top, bottom) = (IndexOf(layers, drillSpan.Top), IndexOf(layers, drillSpan.Bottom));
            if (!(top < g && g < bottom) && !(top == g || bottom == g)) continue;   // the drill never reaches it
            discs.Add(Disc(inst.X, inst.Y, radius));
        }

        foreach (var via in shapes.OfType<ViaShape>())
        {
            if (!drill.Contains(via.Layer)) continue;   // on no via entry: inert everywhere, so here too
            // Update Layout's own ground via is KNOWN to be ground: joined wherever its drill meets the plane, and
            // a through via to an inner plane is exactly that — so never cut, and not left for the designer to check.
            if (GroundArtwork.IsGroundVia(via))
            {
                if (outer.Contains(via.X, via.Y)) groundVias++;
                continue;
            }
            long radius = Math.Max(via.PadSize, via.DrillSize) / 2 + ring;
            if (via.X + radius < outer.MinX || via.X - radius > outer.MaxX ||
                via.Y + radius < outer.MinY || via.Y - radius > outer.MaxY) continue;
            if (ViaSpanResolver.Resolve(via.Layer, technology) is { } span)
            {
                int top = IndexOf(layers, span.Top), bottom = IndexOf(layers, span.Bottom);
                if (top == g || bottom == g) { joined++; continue; }   // ends on the plane: joined to it
                if (!(top < g && g < bottom)) continue;               // never reaches it
            }
            discs.Add(Disc(via.X, via.Y, radius));
        }
        return (discs, joined, groundVias);
    }

    /// <summary>
    /// brief-dfr26-7 R-dfr26-7-1 — the radius, DBU, a placed TESTPAD clears in <paramref name="ground"/>, or null for
    /// none: its declared cut-out (<c>ClearD</c>) on a plane named in its <c>ClearLayers</c>, and its via's antipad on
    /// a plane the via's drill passes, the larger where both apply. Centred on the instance's origin, which is the
    /// pad's centre and the via's axis.
    /// </summary>
    internal static long? TestPadClearance(TestPadPCell.Resolved r, Technology technology, StackupLayer ground, int dbuPerMicron)
    {
        double diameter = 0;
        if (r.ClearLayers.Any(c => string.Equals(c, ground.Name, StringComparison.OrdinalIgnoreCase))) diameter = r.ClearD;
        if (r.Stack?.Via is { } span)
        {
            var layers = technology.Stackup.Layers;
            int g = IndexOf(layers, ground);
            int from = IndexOf(layers, span.From), to = IndexOf(layers, span.To);
            int top = Math.Min(from, to), bottom = Math.Max(from, to);
            if (span.ViaEntry is { } entry && ViaSpanResolver.Resolve(entry, technology) is { } drillSpan)
                (top, bottom) = (IndexOf(layers, drillSpan.Top), IndexOf(layers, drillSpan.Bottom));
            bool joined = ReferenceEquals(span.From, ground) || ReferenceEquals(span.To, ground);
            if (!joined && top <= g && g <= bottom) diameter = Math.Max(diameter, r.ViaAntipad);
        }
        return diameter > 0 ? (long)Math.Round(diameter / 2 * 1e6 * dbuPerMicron) : null;
    }

    private static int IndexOf(IReadOnlyList<StackupLayer> layers, StackupLayer layer)
    {
        for (int i = 0; i < layers.Count; i++) if (ReferenceEquals(layers[i], layer)) return i;
        return -1;
    }

    private static Path64 Rect(Bbox b) => [new(b.MinX, b.MinY), new(b.MaxX, b.MinY), new(b.MaxX, b.MaxY), new(b.MinX, b.MaxY)];

    private static Path64 Disc(long x, long y, long r)
    {
        const int n = 48;
        var p = new Path64(n);
        for (int i = 0; i < n; i++)
        {
            double a = 2 * Math.PI * i / n;
            // Circumscribed, so the polygon's flats never come closer to the pad than the clearance says.
            double rr = r / Math.Cos(Math.PI / n);
            p.Add(new Point64(x + (long)Math.Round(rr * Math.Cos(a)), y + (long)Math.Round(rr * Math.Sin(a))));
        }
        return p;
    }
}

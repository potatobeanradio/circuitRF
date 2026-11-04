using CircuitRF.Core.Devices;

namespace CircuitRF.Design.Layout.PCells;

// The MMIC passives' artwork (brief-agent-authoring-overview.md AA-1): MIMCAP, SPIRAL, TFR and
// AIRBRIDGE, each keyed by its component's own token as MLIN is, drawn from the same parameters the
// model reads and on the layers MmicStackResolver resolves — the resolution the model's injected
// numbers come from, so the drawn part and the simulated part are the same part.
//
// Four rules every one of them keeps, learnt by the kit example's generators before them:
//   - Pin 1 sits at the cell origin, and a pin sits on its metal's own EDGE, never inside it.
//   - Every terminal is on the BASE metal (the one on the substrate), with clean single-level metal
//     beyond any via, so an EM port may stand on it: a port on a point carrying two conductor levels
//     is refused by name.
//   - A masked film is drawn (the capacitor's mask), because a layout without it is not manufacturable;
//     the stackup band it puts in place is not, because that would stack a second insulator.
//   - The smallest feature is the technology's own base-metal width/spacing rule, else 4 µm.

/// <summary>Shared arithmetic for the four MMIC generators.</summary>
internal static class MmicArt
{
    public const int DbuPerMicron = LayoutUnits.DefaultDbuPerMicron;

    public static long Dbu(double metres) => PCellUnits.MetresToDbu(metres, DbuPerMicron);

    /// <summary>The smallest feature: the larger of the base metal's min-width and min-spacing rules,
    /// else 4 µm (the shipped GaAs process's).</summary>
    public static long MinFeature(Technology? technology, LayerKey baseLayer)
    {
        long best = 0;
        if (technology is not null)
            foreach (var r in technology.DrcRules)
                if (r.Layer == baseLayer && r.Kind is DrcRuleKind.MinWidth or DrcRuleKind.MinSpacing)
                    best = Math.Max(best, r.ValueDbu);
        return best > 0 ? best : 4 * DbuPerMicron;
    }

    public static RectShape Rect(LayerKey layer, long x1, long y1, long x2, long y2)
        => new() { Layer = layer, X1 = Math.Min(x1, x2), Y1 = Math.Min(y1, y2), X2 = Math.Max(x1, x2), Y2 = Math.Max(y1, y2) };

    /// <summary>One sentence when the technology cannot supply <paramref name="part"/>, so the cell
    /// says it was drawn on the fallback layers rather than looking right and being wrong.</summary>
    public static List<string> Notes(Technology? technology, ResolvedMmicStack stack, MmicPart part)
    {
        var notes = new List<string>();
        if (technology is null)
            notes.Add("no technology resolved; drawn on the shipped GaAs process's layer numbers");
        else if (MmicStackResolver.Why(stack, part, technology.Name) is { } why)
            notes.Add($"{why}; drawn on the shipped GaAs process's layer numbers where the technology has none");
        return notes;
    }
}

/// <summary>
/// MIMCAP artwork. W×L is the TOP plate (the plate that sets C); the bottom plate and the capacitor
/// mask enclose it by one minimum feature. The top plate is carried up through a MIM via onto a strap
/// on the bridge metal, which comes back down through a bridge via onto a base-metal landing pad —
/// pin 1, at the origin. Pin 2 is the bottom plate's far edge.
/// </summary>
public static class MimCapPCell
{
    public const string GeneratorId = "MIMCAP";

    public static PCellResult Generate(IReadOnlyDictionary<string, PCellValue> parameters, Technology? technology,
                                       PCellLayerSelection layerSelection)
    {
        var stack = MmicStackResolver.Resolve(technology);
        var fb = MmicStackResolver.FallbackLayers.Base;
        var baseLayer = ResolvedMmicStack.Key(stack.BaseMetal, fb);
        var top       = ResolvedMmicStack.Key(stack.TopPlate, MmicStackResolver.FallbackLayers.TopPlate);
        var strap     = ResolvedMmicStack.Key(stack.BridgeMetal, MmicStackResolver.FallbackLayers.Bridge);
        var post      = ResolvedMmicStack.Key(stack.BridgeVia, MmicStackResolver.FallbackLayers.BridgeVia);
        var mimVia    = ResolvedMmicStack.Key(stack.TopPlateVia, MmicStackResolver.FallbackLayers.TopPlateVia);
        var mask      = stack.CapMask?.Key ?? MmicStackResolver.FallbackLayers.CapMask;

        long f = MmicArt.MinFeature(technology, baseLayer);
        long w = Math.Max(MmicArt.Dbu(parameters.Real("W", 50e-6)), f);
        long l = Math.Max(MmicArt.Dbu(parameters.Real("L", 50e-6)), f);
        long e = f;                                     // bottom plate / mask enclosure of the top plate
        long half = w / 2;

        // The strap is the top plate's width less the enclosure each side, so the MIM via under it can
        // cover most of the plate (a low-resistance contact) and the strap still encloses it.
        long sw = Math.Max(w - 2 * e, f);
        long sHalf = sw / 2;
        long lead = 2 * f, postLen = f;
        long a1 = lead + postLen;                       // the landing pad's inner end
        long xb = a1 + 2 * f;                           // the bottom plate, clear of the pad by 2 spacings
        long topX1 = xb + e, topX2 = xb + e + l;
        long xEnd = xb + l + 2 * e;

        // The MIM via: the top plate inset by one enclosure, or one minimum feature centred on a plate
        // too small for that.
        long vx1 = topX1 + e, vx2 = topX2 - e;
        if (vx2 - vx1 < f) { long c = (topX1 + topX2) / 2; vx1 = c - f / 2; vx2 = vx1 + f; }

        var shapes = new List<LayoutShape>
        {
            MmicArt.Rect(baseLayer, xb, -half - e, xEnd, w - half + e),                          // bottom plate
            MmicArt.Rect(mask,      xb, -half - e, xEnd, w - half + e),                          // capacitor mask
            MmicArt.Rect(top,       topX1, -half, topX2, w - half),                              // top plate
            MmicArt.Rect(mimVia,    vx1, -sHalf, vx2, sw - sHalf),
            MmicArt.Rect(strap,     a1 - postLen, -sHalf, vx2, sw - sHalf),
            MmicArt.Rect(post,      a1 - postLen, -sHalf, a1, sw - sHalf),
            MmicArt.Rect(baseLayer, 0, -sHalf, a1, sw - sHalf),                                  // landing pad
        };

        var pins = new[]
        {
            new PCellPin("1", 0,    0, baseLayer, sw,        180.0),
            new PCellPin("2", xEnd, 0, baseLayer, w + 2 * e,   0.0),
        };
        var notes = MmicArt.Notes(technology, stack, MmicPart.MimCap);
        return new PCellResult(shapes, pins, Diagnostics: notes.Count > 0 ? notes : null);
    }
}

/// <summary>
/// TFR artwork: the resistor film, W wide, with a contact over each end on the metal of the level the
/// film is on. N = 1 is a straight film whose contacts' edges are exactly L apart, so what lies between
/// them is the L×W the model counts; each contact overlaps the film by half a minimum feature.
///
/// <para>N &gt; 1 is the classic serpentine: N parallel runs of length L between bend centres, stepped in
/// +Y and joined by square-cornered links whose clear length between the runs' edges is Lc, as one polygon
/// (overlapping rectangles would leave seams a width check measures and an extraction meshes). An odd N
/// leaves pin 2 on the side opposite pin 1, and an even N on the same side, above it.</para>
/// </summary>
public static class TfrPCell
{
    public const string GeneratorId = "TFR";

    public static PCellResult Generate(IReadOnlyDictionary<string, PCellValue> parameters, Technology? technology,
                                       PCellLayerSelection layerSelection)
    {
        var stack = MmicStackResolver.Resolve(technology);
        var baseLayer = ResolvedMmicStack.Key(stack.ResistorLevel ?? stack.BaseMetal, MmicStackResolver.FallbackLayers.Base);
        var film = stack.ResistorLayer?.Key ?? MmicStackResolver.FallbackLayers.Resistor;

        long f = MmicArt.MinFeature(technology, baseLayer);
        long w = Math.Max(MmicArt.Dbu(parameters.Real("W", 10e-6)), 1);
        long l = Math.Max(MmicArt.Dbu(parameters.Real("L", 20e-6)), 1);
        int n = Math.Max((int)Math.Round(parameters.Real("N", 1.0)), 1);
        long link = Math.Max(MmicArt.Dbu(parameters.Real("Lc", 10e-6)), 1);
        long ov = Math.Max(f / 2, 1), c = 2 * f;
        long wc = w + f, half = w / 2, cHalf = wc / 2;
        long xEnd = 2 * c + l;

        // A serpentine's run is at least as long as it is wide: shorter, its two bends would overlap.
        if (n > 1) l = Math.Max(l, w);
        xEnd = 2 * c + l;

        var notes = MmicArt.Notes(technology, stack, MmicPart.Tfr);
        var shapes = new List<LayoutShape>();
        long pitch = w + link;
        long yLast = (n - 1) * pitch;
        bool sameSide = n % 2 == 0;

        if (n == 1) shapes.Add(MmicArt.Rect(film, c - ov, -half, c + l + ov, w - half));
        else
        {
            // The centreline. ThickPath carries each free end half a width past its point, so each end's
            // point is pulled in by that half, and the film still ends one overlap under its contact.
            long h = w / 2;
            var pts = new List<(long X, long Y)> { (c - ov + h, 0) };
            bool atFar = false;
            for (int i = 0; i < n - 1; i++)
            {
                long x = atFar ? c : c + l;
                pts.Add((x, i * pitch));
                pts.Add((x, (i + 1) * pitch));
                atFar = !atFar;
            }
            pts.Add(sameSide ? (c - ov + h, yLast) : (c + l + ov - h, yLast));
            shapes.Add(SpiralPCell.ThickPath(film, pts, w));
            if (link < f)
                notes.Add($"Lc = {link / (double)LayoutUnits.DefaultDbuPerMicron:G4} µm is under the metal's minimum feature, so a contact sits against the film's next run.");
        }

        long x2 = sameSide ? 0 : c + l;
        shapes.Add(MmicArt.Rect(baseLayer, 0, -cHalf, c, wc - cHalf));
        if (sameSide) shapes.Add(MmicArt.Rect(baseLayer, 0, yLast - cHalf, c, yLast + wc - cHalf));
        else shapes.Add(MmicArt.Rect(baseLayer, x2, yLast - cHalf, xEnd, yLast + wc - cHalf));

        var pins = new[]
        {
            new PCellPin("1", 0, 0, baseLayer, wc, 180.0),
            sameSide ? new PCellPin("2", 0, yLast, baseLayer, wc, 180.0)
                     : new PCellPin("2", xEnd, yLast, baseLayer, wc, 0.0),
        };
        return new PCellResult(shapes, pins, Diagnostics: notes.Count > 0 ? notes : null);
    }
}

/// <summary>
/// SPIRAL artwork: a square coil on the base metal, walked outward from its inner end — sides
/// +X, +Y, −X, −Y, each pair a pitch longer than the last — for <c>4·N</c> sides rounded to whole
/// quarter turns. The inner end escapes straight across the coil's first side's counterparts on the
/// bridge metal, between two bridge-via posts, onto a base-metal landing pad (pin 2). Pin 1 is the
/// outer end's metal edge, and the cell is translated so pin 1 sits at the origin.
///
/// <para>The escape crosses exactly <see cref="SpiralInductorModel.Crossings"/> turns, which is the
/// count the model's crossing capacitance uses: both are read from one function.</para>
/// </summary>
public static class SpiralPCell
{
    public const string GeneratorId = "SPIRAL";

    public static PCellResult Generate(IReadOnlyDictionary<string, PCellValue> parameters, Technology? technology,
                                       PCellLayerSelection layerSelection)
    {
        var stack = MmicStackResolver.Resolve(technology);
        var coil   = ResolvedMmicStack.Key(stack.BaseMetal, MmicStackResolver.FallbackLayers.Base);
        var bridge = ResolvedMmicStack.Key(stack.BridgeMetal, MmicStackResolver.FallbackLayers.Bridge);
        var post   = ResolvedMmicStack.Key(stack.BridgeVia, MmicStackResolver.FallbackLayers.BridgeVia);

        long f = MmicArt.MinFeature(technology, coil);
        double turns = parameters.Real("N", 2.5);
        long w   = Math.Max(MmicArt.Dbu(parameters.Real("W", 10e-6)), f);
        long s   = Math.Max(MmicArt.Dbu(parameters.Real("S", 10e-6)), f);
        long din = Math.Max(MmicArt.Dbu(parameters.Real("Din", 100e-6)), f);
        int sides = SpiralInductorModel.Sides(turns);
        int crossings = SpiralInductorModel.Crossings(turns);

        long p = w + s, half = w / 2;
        long a0 = din / 2 + half;                       // the innermost centreline's half-size

        // The centreline: the walk the model's inductance is summed over (SpiralWalk), in DBU. Every
        // input is a whole number of DBU, so every point is exact.
        var pts = SpiralWalk.Centreline(a0, p, sides, octagonal: false)
            .Select(q => ((long)Math.Round(q.X), (long)Math.Round(q.Y))).ToList();
        (long Dx, long Dy)[] dirs = [(1, 0), (0, 1), (-1, 0), (0, -1)];

        var shapes = new List<LayoutShape> { ThickPath(coil, pts, w) };

        // The escape: a post on the inner end, the bridge metal straight down (−Y) across every later
        // lap's bottom side, a second post clear of the lowest one by a spacing, and the landing pad.
        long ix = -a0;
        long yE = -a0 - crossings * p - w - s;          // the second post's centre
        long lead = 2 * w;
        shapes.Add(MmicArt.Rect(post,   ix - half, -a0 - half, ix - half + w, -a0 - half + w));
        shapes.Add(MmicArt.Rect(bridge, ix - half, yE - half,  ix - half + w, -a0 - half + w));
        shapes.Add(MmicArt.Rect(post,   ix - half, yE - half,  ix - half + w, yE - half + w));
        shapes.Add(MmicArt.Rect(coil,   ix - half, yE - half - lead, ix - half + w, yE - half + w));

        // Pin 1 on the outer end's metal edge, facing along the last side.
        var last = dirs[(sides - 1) % 4];
        var (ex, ey) = pts[^1];
        long p1x = ex + last.Dx * half, p1y = ey + last.Dy * half;
        double p1deg = (sides - 1) % 4 * 90.0;
        long p2x = ix, p2y = yE - half - lead;

        // Translate so pin 1 is the origin.
        foreach (var shape in shapes) Translate(shape, -p1x, -p1y);
        var pins = new[]
        {
            new PCellPin("1", 0, 0, coil, w, p1deg),
            new PCellPin("2", p2x - p1x, p2y - p1y, coil, w, 270.0),
        };

        var notes = MmicArt.Notes(technology, stack, MmicPart.Spiral);
        if (Math.Abs(4 * turns - sides) > 1e-9)
            notes.Add($"N = {turns:G6} is drawn as {sides / 4.0:G6} turns: the coil is drawn in whole quarter turns");
        return new PCellResult(shapes, pins, Diagnostics: notes.Count > 0 ? notes : null);
    }

    /// <summary>
    /// A rectilinear centreline of width <paramref name="w"/> as ONE polygon — the coil is one
    /// conductor, and overlapping rectangles would leave seams a width check measures and an extraction
    /// meshes. Each corner's offset is the sum of the two segments' half-width normals (the miter of a
    /// right angle); each free end is carried half a width past its point, so the inner end's post and
    /// the outer end's pin both sit inside metal / on its edge.
    /// </summary>
    internal static PolygonShape ThickPath(LayerKey layer, IReadOnlyList<(long X, long Y)> pts, long w)
    {
        long h = w / 2, h2 = w - h;
        int n = pts.Count;
        (long Dx, long Dy) Dir(int i)
        {
            long dx = Math.Sign(pts[i + 1].X - pts[i].X), dy = Math.Sign(pts[i + 1].Y - pts[i].Y);
            return (dx, dy);
        }
        var left = new List<long>();
        var right = new List<(long, long)>();
        for (int i = 0; i < n; i++)
        {
            var din = i > 0 ? Dir(i - 1) : Dir(0);
            var dout = i < n - 1 ? Dir(i) : Dir(n - 2);
            // Left normal of (dx, dy) is (−dy, dx).
            long lx = -din.Dy * h + -dout.Dy * h, ly = din.Dx * h + dout.Dx * h;
            long rx = din.Dy * h2 + dout.Dy * h2, ry = -din.Dx * h2 + -dout.Dx * h2;
            long px = pts[i].X, py = pts[i].Y;
            if (i == 0 || i == n - 1)
            {
                // A free end: one normal only, carried half a width past the point.
                var d = i == 0 ? dout : din;
                long sign = i == 0 ? -1 : 1;
                px += sign * d.Dx * h; py += sign * d.Dy * h;
                lx = -d.Dy * h; ly = d.Dx * h;
                rx = d.Dy * h2; ry = -d.Dx * h2;
            }
            left.Add(px + lx); left.Add(py + ly);
            right.Add((px + rx, py + ry));
        }
        for (int i = right.Count - 1; i >= 0; i--) { left.Add(right[i].Item1); left.Add(right[i].Item2); }
        return new PolygonShape { Layer = layer, Xy = [.. left] };
    }

    internal static void Translate(LayoutShape shape, long dx, long dy)
    {
        switch (shape)
        {
            case RectShape r: r.X1 += dx; r.X2 += dx; r.Y1 += dy; r.Y2 += dy; break;
            case PolygonShape poly:
                for (int i = 0; i + 1 < poly.Xy.Length; i += 2) { poly.Xy[i] += dx; poly.Xy[i + 1] += dy; }
                break;
        }
    }
}

/// <summary>
/// OSPIRAL artwork: <see cref="SpiralPCell"/>'s coil with its corners cut — the same stack, the same
/// escape, the same pins, and only the walk different (<see cref="SpiralWalk.Centreline"/>, which keeps
/// the spacing S exact on the diagonals as well as the flats — the walk the PDK example kit's octagonal
/// spiral makes). N is drawn in whole eighth turns.
///
/// <para><b>The inner end is the MIDDLE of the innermost flat, not its corner.</b> The escape leaves it
/// straight outward across every later lap's matching flat. From the corner, the escape lands within
/// a width of where the next lap's diagonal side meets its flat, and an outer lap ending on that
/// diagonal clips it; from the middle, it crosses each flat squarely, far from both ends. So N counts
/// from the middle of the innermost flat.</para>
/// </summary>
public static class OctSpiralPCell
{
    public const string GeneratorId = "OSPIRAL";

    public static PCellResult Generate(IReadOnlyDictionary<string, PCellValue> parameters, Technology? technology,
                                       PCellLayerSelection layerSelection)
    {
        var stack = MmicStackResolver.Resolve(technology);
        var coil   = ResolvedMmicStack.Key(stack.BaseMetal, MmicStackResolver.FallbackLayers.Base);
        var bridge = ResolvedMmicStack.Key(stack.BridgeMetal, MmicStackResolver.FallbackLayers.Bridge);
        var post   = ResolvedMmicStack.Key(stack.BridgeVia, MmicStackResolver.FallbackLayers.BridgeVia);

        long f = MmicArt.MinFeature(technology, coil);
        double turns = parameters.Real("N", 2.5);
        long w   = Math.Max(MmicArt.Dbu(parameters.Real("W", 10e-6)), f);
        long s   = Math.Max(MmicArt.Dbu(parameters.Real("S", 10e-6)), f);
        long din = Math.Max(MmicArt.Dbu(parameters.Real("Din", 100e-6)), f);
        int sides = SpiralInductorModel.OctagonalSides(turns);
        int crossings = SpiralInductorModel.OctagonalCrossings(turns);

        long p = w + s, half = w / 2;
        double a = din / 2 + half;                      // the innermost side's centreline from the centre

        // The walk the model's inductance is summed over (SpiralWalk), in DBU; its first point is the
        // middle of the innermost flat, rounded onto the grid for the escape that leaves it.
        var pts = SpiralWalk.Centreline(a, p, sides, octagonal: true);
        long ix = (long)Math.Round(pts[0].X);
        long a0 = (long)Math.Round(a);
        pts[0] = (ix, -a0);

        var shapes = new List<LayoutShape> { MiteredPath(coil, pts, w) };

        // The escape, exactly SpiralPCell's: a post on the inner end, the bridge metal straight down (−Y)
        // across every later lap's bottom flat, a second post clear of the lowest one by a spacing, and
        // the landing pad.
        long yE = -a0 - crossings * p - w - s;
        long lead = 2 * w;
        shapes.Add(MmicArt.Rect(post,   ix - half, -a0 - half, ix - half + w, -a0 - half + w));
        shapes.Add(MmicArt.Rect(bridge, ix - half, yE - half,  ix - half + w, -a0 - half + w));
        shapes.Add(MmicArt.Rect(post,   ix - half, yE - half,  ix - half + w, yE - half + w));
        shapes.Add(MmicArt.Rect(coil,   ix - half, yE - half - lead, ix - half + w, yE - half + w));

        // Pin 1 on the outer end's metal edge, facing along the last side — axial or diagonal.
        var (qx, qy) = pts[^1];
        var (px, py) = pts[^2];
        double len = Math.Sqrt((qx - px) * (qx - px) + (qy - py) * (qy - py));
        double ux = (qx - px) / len, uy = (qy - py) / len;
        long p1x = (long)Math.Round(qx + ux * w / 2.0), p1y = (long)Math.Round(qy + uy * w / 2.0);
        double p1deg = (Math.Atan2(uy, ux) * 180 / Math.PI + 360) % 360;
        p1deg = Math.Round(p1deg / 45) * 45 % 360;
        long p2x = ix, p2y = yE - half - lead;

        foreach (var shape in shapes) SpiralPCell.Translate(shape, -p1x, -p1y);
        var pins = new[]
        {
            new PCellPin("1", 0, 0, coil, w, p1deg),
            new PCellPin("2", p2x - p1x, p2y - p1y, coil, w, 270.0),
        };

        var notes = MmicArt.Notes(technology, stack, MmicPart.Spiral);
        if (Math.Abs(8 * turns - sides) > 1e-9)
            notes.Add($"N = {turns:G6} is drawn as {sides / 8.0:G6} turns: the coil is drawn in whole eighth turns");
        return new PCellResult(shapes, pins, Diagnostics: notes.Count > 0 ? notes : null);
    }

    /// <summary>
    /// A centreline of any directions, <paramref name="w"/> wide, as ONE polygon (the coil is one
    /// conductor; see <see cref="SpiralPCell.ThickPath"/>). Each corner's offset is the miter of its two
    /// segments' half-width normals, <c>(n₁ + n₂)/(1 + n₁·n₂)</c> — at 45° that reaches 1.08 half-widths,
    /// so the two pieces meet on the miter with nothing left over. Each free end is carried half a width
    /// past its point and cut square.
    /// </summary>
    internal static PolygonShape MiteredPath(LayerKey layer, IReadOnlyList<(double X, double Y)> pts, long w)
    {
        double h = w / 2.0;
        int n = pts.Count;
        (double X, double Y) Dir(int i)
        {
            double dx = pts[i + 1].X - pts[i].X, dy = pts[i + 1].Y - pts[i].Y, l = Math.Sqrt(dx * dx + dy * dy);
            return (dx / l, dy / l);
        }
        var left = new List<long>();
        var right = new List<(long, long)>();
        for (int i = 0; i < n; i++)
        {
            var din = i > 0 ? Dir(i - 1) : Dir(0);
            var dout = i < n - 1 ? Dir(i) : Dir(n - 2);
            double px = pts[i].X, py = pts[i].Y, ox, oy;
            if (i == 0 || i == n - 1)
            {
                var d = i == 0 ? dout : din;
                double sign = i == 0 ? -1 : 1;
                px += sign * d.X * h; py += sign * d.Y * h;
                ox = -d.Y * h; oy = d.X * h;            // the left normal, half a width
            }
            else
            {
                double n1x = -din.Y, n1y = din.X, n2x = -dout.Y, n2y = dout.X;
                double k = h / (1 + n1x * n2x + n1y * n2y);
                ox = (n1x + n2x) * k; oy = (n1y + n2y) * k;
            }
            left.Add((long)Math.Round(px + ox)); left.Add((long)Math.Round(py + oy));
            right.Add(((long)Math.Round(px - ox), (long)Math.Round(py - oy)));
        }
        for (int i = right.Count - 1; i >= 0; i--) { left.Add(right[i].Item1); left.Add(right[i].Item2); }
        return new PolygonShape { Layer = layer, Xy = [.. left] };
    }
}

/// <summary>
/// AIRBRIDGE artwork: a base-metal landing at each end, a bridge-via post on each, and the bridge
/// metal spanning between them, the clear span between the posts being L. Under the middle of the span
/// the crossed line is drawn as a base-metal segment Wu wide and two bridge widths long — the length the
/// model's crossed segment is — with pin 3 on its lower end and pin 4 on its upper end; the rest of
/// that line is the user's. Pins 1 and 2 are the landings' outer edges.
/// </summary>
public static class AirbridgePCell
{
    public const string GeneratorId = "AIRBRIDGE";

    public static PCellResult Generate(IReadOnlyDictionary<string, PCellValue> parameters, Technology? technology,
                                       PCellLayerSelection layerSelection)
    {
        var stack = MmicStackResolver.Resolve(technology);
        var baseLayer = ResolvedMmicStack.Key(stack.BaseMetal, MmicStackResolver.FallbackLayers.Base);
        var bridge    = ResolvedMmicStack.Key(stack.BridgeMetal, MmicStackResolver.FallbackLayers.Bridge);
        var post      = ResolvedMmicStack.Key(stack.BridgeVia, MmicStackResolver.FallbackLayers.BridgeVia);

        long f  = MmicArt.MinFeature(technology, baseLayer);
        long span = Math.Max(MmicArt.Dbu(parameters.Real("L", 30e-6)), f);
        long w  = Math.Max(MmicArt.Dbu(parameters.Real("W", 10e-6)), f);
        long wu = Math.Max(MmicArt.Dbu(parameters.Real("Wu", 10e-6)), f);
        long lead = 2 * f, pl = Math.Max(f, w / 2);
        long half = w / 2;
        long xA = lead + pl, xB = xA + span;            // the posts' inner edges
        long xEnd = xB + pl + lead;
        long cx = (xA + xB) / 2, uHalf = wu / 2;
        long ext = w;                                   // the crossed segment: ±W about the bridge's centreline

        var shapes = new List<LayoutShape>
        {
            MmicArt.Rect(baseLayer, 0,       -half, xA,      w - half),
            MmicArt.Rect(post,      lead,    -half, xA,      w - half),
            MmicArt.Rect(bridge,    lead,    -half, xB + pl, w - half),
            MmicArt.Rect(post,      xB,      -half, xB + pl, w - half),
            MmicArt.Rect(baseLayer, xB,      -half, xEnd,    w - half),
            MmicArt.Rect(baseLayer, cx - uHalf, -ext, cx - uHalf + wu, ext),     // the line crossed
        };
        var pins = new[]
        {
            new PCellPin("1", 0,    0,    baseLayer, w,  180.0),
            new PCellPin("2", xEnd, 0,    baseLayer, w,    0.0),
            new PCellPin("3", cx,   -ext, baseLayer, wu, 270.0),
            new PCellPin("4", cx,    ext, baseLayer, wu,  90.0),
        };
        var notes = MmicArt.Notes(technology, stack, MmicPart.Airbridge);
        if (span < wu + 2 * f)
            notes.Add("the span is too short to clear the crossed line by a minimum spacing on each side");
        return new PCellResult(shapes, pins, Diagnostics: notes.Count > 0 ? notes : null);
    }
}

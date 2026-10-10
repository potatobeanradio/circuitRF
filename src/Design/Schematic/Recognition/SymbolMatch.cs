// The template matcher — brief-img-8-schematic-symbols.md R-im8-3, R-im8-4; the one IImageSymbolClassifier.
//
// A region with n attachments is compared with every template with n pins:
//
//   region ─ hollowed (ink deeper than w from the paper removed, so a filled triangle reads as its outline)
//          ─ skeleton ─ its edges as centre lines
//   template × 8 orientations (mirror, then 4 turns — the schematic's own order) × every assignment of its pins to the
//          attachments whose directions agree within ± 20° (a pin's lead leaves the body the way the wire arrives)
//          ─ scaled and moved so its pins lie on the attachments (least squares; one pin: the body's size)
//          ─ the modified-Hausdorff distance to the region's centre lines, in units of w
//
// The distance is AS-10's (Glyph.Distance — the mean distance from each one's resampled centre lines to the other's,
// the larger way), on a stroke set rather than a glyph. A template is accepted within AcceptDistance and when clear of
// the best template of a DIFFERENT kind by KindMargin; two drawings of one kind never compete. The winning orientation
// and assignment give the pin order.
//
// A structure check (R-im8-4) settles what a distance cannot: a rectangle's two long sides are as close to a
// capacitor's plates as the plates are, and only the gap between them tells the two apart. The best template failing
// its check is set aside and the next taken — reported, never silent.

using CircuitRF.Design.Imaging;
using CircuitRF.Design.Layout.Recognition;
using CircuitRF.Design.Layout.Recognition.Silkscreen;

namespace CircuitRF.Design.Schematic.Recognition;

/// <summary>The template matcher's tolerances; distances in units of the stroke width w.</summary>
public sealed record SymbolMatchOptions
{
    /// <summary>A pin's lead and its wire agree within this many degrees.</summary>
    public double AngleToleranceDeg { get; init; } = 20;

    /// <summary>A match is accepted at or below this distance.</summary>
    public double AcceptDistance { get; init; } = 1.0;

    /// <summary>… and when its distance is at most (1 − this) of the best different kind's.</summary>
    public double KindMargin { get; init; } = 0.15;

    /// <summary>Two placements of one template within this distance are a tie, settled by orientation order.</summary>
    public double TieDistance { get; init; } = 0.05;

    /// <summary>Points each side's centre lines are resampled to.</summary>
    public int Samples { get; init; } = 128;
}

/// <summary>Names a region by the nearest template (R-im8-3, R-im8-4).</summary>
public sealed class TemplateSymbolClassifier : IImageSymbolClassifier
{
    public TemplateSymbolClassifier(IReadOnlyList<SymbolTemplate>? templates = null, SymbolMatchOptions? options = null)
    {
        Templates = templates ?? SymbolTemplates.All();
        Options = options ?? new SymbolMatchOptions();
    }

    public IReadOnlyList<SymbolTemplate> Templates { get; }
    public SymbolMatchOptions Options { get; }

    public SymbolClassification Classify(SymbolRegion region, double strokeWidth)
    {
        ArgumentNullException.ThrowIfNull(region);
        double w = Math.Max(1.0, strokeWidth);
        int n = region.Attachments.Count;
        if (n == 0 || n > 4) return SymbolClassification.None;
        var shape = RegionShape.Of(region, w, Options.Samples);
        var order = Rank(region, shape, w);
        if (order.Count == 0) return SymbolClassification.None;

        var demoted = new List<SymbolDemotion>();
        var failed = new HashSet<SymbolCandidate>();
        foreach (var c in order)
            if (!Structure.Passes(c, region, shape, w))
                failed.Add(c);
        SymbolCandidate? best = order.FirstOrDefault(c => !failed.Contains(c));
        foreach (var c in order)
        {
            if (c == best) break;
            demoted.Add(new SymbolDemotion(c, c.Template.Check));
        }
        var runner = order.FirstOrDefault(c => !failed.Contains(c) && (best is null || c.Kind != best.Kind));
        if (best is null) return new SymbolClassification(null, runner, demoted, PartConfidence.Low);

        bool clear = runner is null || best.Score <= (1 - Options.KindMargin) * runner.Score;
        if (best.Score > Options.AcceptDistance || !clear)
            return new SymbolClassification(null, runner ?? best, demoted, PartConfidence.Low);
        bool sure = best.Score <= Options.AcceptDistance / 2 && demoted.Count == 0
                    && (runner is null || best.Score <= (1 - 2 * Options.KindMargin) * runner.Score);
        return new SymbolClassification(best, runner, demoted, sure ? PartConfidence.High : PartConfidence.Medium);
    }

    /// <summary>Each template with the region's pin count, at its best placement, best first (ties in template order).</summary>
    internal List<SymbolCandidate> Rank(SymbolRegion region, RegionShape shape, double w)
    {
        var ranked = new List<(SymbolCandidate C, int Order)>();
        for (int k = 0; k < Templates.Count; k++)
        {
            var t = Templates[k];
            if (t.Pins.Count != region.Attachments.Count) continue;
            if (t.Fill == TemplateFill.Solid && !shape.Solid || t.Fill == TemplateFill.Hollow && shape.Solid) continue;
            if (Best(t, region, shape, w) is { } c) ranked.Add((c, k));
        }
        return [.. ranked.OrderBy(r => r.C.Score).ThenBy(r => r.Order).Select(r => r.C)];
    }

    /// <summary>The template's best placement on the region, or null when no orientation's pins agree with the
    /// attachments.</summary>
    private SymbolCandidate? Best(SymbolTemplate t, SymbolRegion region, RegionShape shape, double w)
    {
        double cosTol = Math.Cos(Options.AngleToleranceDeg * Math.PI / 180);
        var at = region.Attachments;
        int n = at.Count;
        SymbolCandidate? best = null;
        foreach (var o in SymbolOrientation.All)
        {
            var pins = t.Pins.Select(p => (P: o.Apply(p.X, p.Y), D: o.Apply(p.DirX, p.DirY))).ToList();
            foreach (var perm in Permutations(n))
            {
                bool agree = true;
                for (int i = 0; i < n && agree; i++)
                {
                    var a = at[perm[i]];
                    if (a.DirX == 0 && a.DirY == 0) continue;
                    agree = -(pins[i].D.X * a.DirX + pins[i].D.Y * a.DirY) >= cosTol;
                }
                if (!agree) continue;
                if (Fit(t, o, pins.Select(p => p.P).ToList(), perm, region, shape, w) is not { } fit) continue;
                var glyph = new Glyph(Place(t, o, fit.S, fit.Tx, fit.Ty), Options.Samples);
                double score = Glyph.Distance(shape.Glyph, glyph) / w;
                // A later orientation must be better by more than the pixels' own noise: a symmetric drawing fits two
                // orientations equally, and the first in SymbolOrientation.All's order is the one meant.
                if (best is null || score < best.Score - Options.TieDistance)
                    best = new SymbolCandidate(t, o, [.. perm], score);
            }
        }
        return best;
    }

    /// <summary>The scale and shift putting the oriented pins on their attachments (least squares), or null when they
    /// do not fit. One pin: the scale is the region's size over the body's.</summary>
    private static (double S, double Tx, double Ty)? Fit(SymbolTemplate t, SymbolOrientation o, List<(double X, double Y)> pins,
                                                         int[] perm, SymbolRegion region, RegionShape shape, double w)
    {
        var q = perm.Select(i => shape.Contacts[i]).ToList();
        if (pins.Count == 1)
        {
            var pts = t.Strokes.SelectMany(s => Points(s)).Select(p => o.Apply(p.X, p.Y)).ToList();
            double tw = pts.Max(p => p.X) - pts.Min(p => p.X), th = pts.Max(p => p.Y) - pts.Min(p => p.Y);
            double rw = Math.Max(1, region.Box.Width - w), rh = Math.Max(1, region.Box.Height - w);
            double s1 = Math.Max(rw, rh) / Math.Max(1e-9, Math.Max(tw, th));
            return (s1, q[0].X - s1 * pins[0].X, q[0].Y - s1 * pins[0].Y);
        }
        double px = pins.Average(p => p.X), py = pins.Average(p => p.Y), qx = q.Average(p => p.X), qy = q.Average(p => p.Y);
        double num = 0, den = 0;
        for (int i = 0; i < pins.Count; i++)
        {
            num += (pins[i].X - px) * (q[i].X - qx) + (pins[i].Y - py) * (q[i].Y - qy);
            den += (pins[i].X - px) * (pins[i].X - px) + (pins[i].Y - py) * (pins[i].Y - py);
        }
        if (den <= 0 || num <= 0) return null;
        double s = num / den, tx = qx - s * px, ty = qy - s * py;
        double allowed = Math.Max(2 * w, 0.2 * s);
        for (int i = 0; i < pins.Count; i++)
            if (Math.Sqrt(Sq(s * pins[i].X + tx - q[i].X) + Sq(s * pins[i].Y + ty - q[i].Y)) > allowed) return null;
        return (s, tx, ty);
    }

    /// <summary>The template's strokes on the picture.</summary>
    internal static List<double[]> Place(SymbolTemplate t, SymbolOrientation o, double s, double tx, double ty) =>
        [.. t.Strokes.Select(st =>
        {
            var r = new double[st.Length];
            for (int i = 0; i < st.Length; i += 2)
            {
                var (x, y) = o.Apply(st[i], st[i + 1]);
                r[i] = s * x + tx;
                r[i + 1] = s * y + ty;
            }
            return r;
        })];

    private static IEnumerable<(double X, double Y)> Points(double[] s) { for (int i = 0; i < s.Length; i += 2) yield return (s[i], s[i + 1]); }

    private static IEnumerable<int[]> Permutations(int n)
    {
        var a = Enumerable.Range(0, n).ToArray();
        return Permute(a, 0);

        static IEnumerable<int[]> Permute(int[] a, int k)
        {
            if (k == a.Length) { yield return (int[])a.Clone(); yield break; }
            for (int i = k; i < a.Length; i++)
            {
                (a[k], a[i]) = (a[i], a[k]);
                foreach (var p in Permute(a, k + 1)) yield return p;
                (a[k], a[i]) = (a[i], a[k]);
            }
        }
    }

    private static double Sq(double v) => v * v;
}

/// <summary>What the matcher reads off a region once: its centre lines, its ink and whether it is drawn filled.</summary>
/// <param name="Contacts">For each attachment, where its wire meets the region's ink: the attachment itself, or the first
/// ink straight ahead of it within 3 w — a wire cut away from a filled body ends short of it, and fitted there a
/// template is scaled to a span longer than the body.</param>
internal sealed record RegionShape(Glyph Glyph, IReadOnlyList<(double X, double Y)> Ink, IReadOnlyList<(double X, double Y)> Skeleton,
                                   bool Solid, IReadOnlyList<(double X, double Y)> Contacts)
{
    public static RegionShape Of(SymbolRegion region, double w, int samples)
    {
        var mask = region.Mask;
        int ox = region.Box.Left, oy = region.Box.Top;
        var dt = DistanceTransform.Compute(mask);

        // Filled: what survives an opening by a disc 1.5 w in radius — no stroke of the drawing's width does.
        var thick = Morphology.Open(mask, 1.5 * w);
        bool solid = thick.Count() >= Math.Max(4, 4 * w * w);

        // Hollowed: a filled area reduced to the one-pixel ring at its edge, every stroke left as it is — so a filled
        // shape is read by its outline, where a template draws it.
        var hollow = new BinaryImage(mask.Width, mask.Height);
        for (int i = 0; i < dt.Length; i++)
            if (mask.Pixels[i] != 0 && (thick.Pixels[i] == 0 || dt[i] <= 1.0)) hollow.Pixels[i] = 1;

        var g = SkeletonGraph.Build(hollow, new SkeletonGraphOptions { StrokeWidth = w });
        var strokes = new List<double[]>();
        var skel = new List<(double, double)>();
        foreach (var e in g.Edges)
        {
            strokes.Add([.. e.Polyline.SelectMany(p => new[] { p.x + ox, p.y + oy })]);
            foreach (var (x, y) in e.Pixels) skel.Add((x + 0.5 + ox, y + 0.5 + oy));
        }
        foreach (var nd in g.Nodes.Where(nd => nd.Degree == 0))
        {
            strokes.Add([nd.X + ox, nd.Y + oy, nd.X + ox, nd.Y + oy]);
            skel.Add((nd.X + ox, nd.Y + oy));
        }
        var ink = new List<(double, double)>();
        for (int y = 0; y < mask.Height; y++)
            for (int x = 0; x < mask.Width; x++)
                if (mask[x, y]) ink.Add((x + 0.5 + ox, y + 0.5 + oy));
        var contacts = region.Attachments.Select(a =>
        {
            for (double d = 0; d <= 3 * w; d += 0.5)
            {
                double x = a.X + d * a.DirX, y = a.Y + d * a.DirY;
                if (mask.At((int)Math.Floor(x) - ox, (int)Math.Floor(y) - oy)) return (x, y);
            }
            return (a.X, a.Y);
        }).ToList();
        return new RegionShape(new Glyph(strokes, samples), ink, skel, solid, contacts);
    }
}

/// <summary>The structure checks (R-im8-4), each in the frame of the candidate's attachments: along runs from the first
/// pin's attachment to the second (from the one attachment into the region, for a ground), across at right angles.</summary>
internal static class Structure
{
    public static bool Passes(SymbolCandidate c, SymbolRegion region, RegionShape shape, double w) => c.Template.Check switch
    {
        StructureCheck.Capacitor => Capacitor(c, shape, w),
        StructureCheck.ZigZag => ZigZag(c, shape, w),
        StructureCheck.Coil => Coil(c, shape, w),
        StructureCheck.Ground => Ground(region, shape, w),
        _ => true,
    };

    /// <summary>Two bars across the axis, each at least 2.5 w long, with a band of bare paper between them.</summary>
    private static bool Capacitor(SymbolCandidate c, RegionShape shape, double w)
    {
        if (Axis(c, shape) is not { } f) return false;
        var pts = shape.Ink.Select(p => f.Local(p)).ToList();
        int lo = (int)Math.Floor(pts.Min(p => p.T)), hi = (int)Math.Ceiling(pts.Max(p => p.T));
        var ink = new bool[hi - lo + 1];
        foreach (var p in pts) ink[(int)Math.Floor(p.T) - lo] = true;

        // The widest bare run whose middle lies between the two attachments.
        int bestStart = -1, bestLen = 0;
        for (int i = 0; i < ink.Length;)
        {
            if (ink[i]) { i++; continue; }
            int j = i;
            while (j < ink.Length && !ink[j]) j++;
            double mid = lo + (i + j) / 2.0;
            if (mid > 0 && mid < f.Length && j - i > bestLen) { bestStart = i; bestLen = j - i; }
            i = j;
        }
        if (bestLen == 0) return false;
        double cut = lo + bestStart;
        var before = pts.Where(p => p.T < cut).ToList();
        var after = pts.Where(p => p.T >= cut + bestLen).ToList();
        return Extent(before) >= 2.5 * w && Extent(after) >= 2.5 * w;
    }

    /// <summary>At least three alternating vertices: the centre line's mean offset across, binned along, swings past
    /// a third of its amplitude to each side at least three times.</summary>
    private static bool ZigZag(SymbolCandidate c, RegionShape shape, double w)
    {
        if (Axis(c, shape) is not { } f) return false;
        var bins = Bins(shape.Skeleton.Select(p => f.Local(p)).Where(p => p.T >= 0 && p.T <= f.Length), w, p => p.A, b => b.Average());
        double amp = bins.Count == 0 ? 0 : bins.Max(Math.Abs);
        if (amp < w) return false;
        int runs = 0, state = 0;
        foreach (var v in bins)
        {
            int s = v > amp / 3 ? 1 : v < -amp / 3 ? -1 : 0;
            if (s != 0 && s != state) { runs++; state = s; }
        }
        return runs >= 3;
    }

    /// <summary>At least three humps on one side of the axis: the centre line's NEAREST approach to the axis, binned
    /// along, comes within w/2 of its peak and drops a whole w below it three times — and the centre line reaches the
    /// other side by less than a third of that peak, which is what a zig-zag does not. The nearest, not the furthest:
    /// either side of a cusp two arcs share a bin. And the dip is a stroke width, not a fraction of the peak: where
    /// two arcs meet at a cusp their strokes run together, and the skeleton of a 6 px hump drawn 2 px wide comes no
    /// nearer the axis than 4 px.</summary>
    private static bool Coil(SymbolCandidate c, RegionShape shape, double w)
    {
        if (Axis(c, shape) is not { } f) return false;
        var pts = shape.Skeleton.Select(p => f.Local(p)).Where(p => p.T >= 0 && p.T <= f.Length).ToList();
        if (pts.Count == 0) return false;
        int side = pts.Sum(p => p.A) >= 0 ? 1 : -1;
        var bins = Bins(pts, 1, p => side * p.A, b => b.Min());
        double peak = bins.Count == 0 ? 0 : bins.Max();
        if (peak < 2 * w || pts.Max(p => -side * p.A) > peak / 3) return false;
        int humps = 0;
        bool up = false;
        foreach (var h in bins)
        {
            if (!up && h >= peak - w / 2) { humps++; up = true; }
            else if (up && h <= peak - w) up = false;
        }
        return humps >= 3;
    }

    /// <summary>One attachment and bars shrinking away from it: the ink's extent across, row by row going in, never
    /// grows by more than w and ends at least 2 w shorter than it starts.</summary>
    private static bool Ground(SymbolRegion region, RegionShape shape, double w)
    {
        if (region.Attachments.Count != 1) return false;
        var a = region.Attachments[0];
        var (cx, cy) = shape.Contacts[0];
        double ux = a.DirX, uy = a.DirY;
        if (ux == 0 && uy == 0)
        {
            double dx = region.Box.CentreX - cx, dy = region.Box.CentreY - cy, l = Math.Sqrt(dx * dx + dy * dy);
            if (l == 0) return false;
            (ux, uy) = (dx / l, dy / l);
        }
        var f = new Frame(cx, cy, ux, uy, 0);
        var rows = Bins(shape.Ink.Select(p => f.Local(p)).Where(p => p.T >= -w), 1, p => p.A, b => b.Max() - b.Min() + 1)
                   .Where(e => e >= 2 * w).ToList();
        if (rows.Count < 2 || rows[0] < rows[^1] + 2 * w) return false;
        double widest = rows[0];
        foreach (var e in rows)
        {
            if (e > widest + w) return false;
            widest = Math.Max(widest, e);
        }
        return true;
    }

    private readonly record struct Frame(double Ox, double Oy, double Ux, double Uy, double Length)
    {
        public (double T, double A) Local((double X, double Y) p)
        {
            double dx = p.X - Ox, dy = p.Y - Oy;
            return (dx * Ux + dy * Uy, -dx * Uy + dy * Ux);
        }
    }

    /// <summary>The frame from where the candidate's first pin's wire meets the region to where its second's does.</summary>
    private static Frame? Axis(SymbolCandidate c, RegionShape shape)
    {
        if (c.PinAttachments.Count != 2) return null;
        var a = shape.Contacts[c.PinAttachments[0]];
        var b = shape.Contacts[c.PinAttachments[1]];
        double dx = b.X - a.X, dy = b.Y - a.Y, l = Math.Sqrt(dx * dx + dy * dy);
        return l < 1 ? null : new Frame(a.X, a.Y, dx / l, dy / l, l);
    }

    /// <summary>Points grouped into bins of <paramref name="size"/> along, in order, each reduced; empty bins skipped.</summary>
    private static List<double> Bins(IEnumerable<(double T, double A)> pts, double size, Func<(double T, double A), double> value,
                                     Func<List<double>, double> reduce) =>
        [.. pts.GroupBy(p => (int)Math.Floor(p.T / size)).OrderBy(g => g.Key).Select(g => reduce([.. g.Select(value)]))];

    private static double Extent(List<(double T, double A)> pts) => pts.Count == 0 ? 0 : pts.Max(p => p.A) - pts.Min(p => p.A);
}

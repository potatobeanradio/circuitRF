// Two pads that are a chip part's land pattern — brief-artsch-4-parts-and-parts-table.md R-as4-1 (3);
// overview D10; docs/design/artwork-to-schematic.md §5.
//
// THE REFERENCE IS GENERATED, NEVER TABULATED. Every case in SmtCaseTable at every density is drawn
// once by ChipLandPatternGenerator — the generator a placed `smt-<case>@<density>` cell comes from — and
// its two lands and two mask openings are read back off the result. A second table of land dimensions
// here would drift from the generator the first time its fillet goals changed, and a board drawn with
// circuitRF's own footprints would stop matching them, with nothing to say so.
//
// WHAT IS COMPARED. A pad pair is two axis-aligned rectangles side by side along x or y (the part at 0°
// or 90°): each pad's extent along the pair's axis, its extent across it, and the gap between them.
// Each is within ±20 % of a case's reference or the case does not match; among the cases that do, the
// smallest RMS error wins and a different case within five points of it is named as the runner-up.
// A pad sitting in a ROW of like pads at the pair's own pitch is a multi-pin package's pin, never one
// end of a chip part.

using CircuitRF.Design.Layout.Footprints;
using CircuitRF.Design.Layout.PCells;

namespace CircuitRF.Design.Layout.Recognition;

/// <summary>Where a pad's outline was read from — which reference dimension it is compared with.</summary>
public enum PadOutlineSource
{
    /// <summary>A solder-mask opening: compared with the generated mask opening.</summary>
    Mask,

    /// <summary>A paste opening: compared with the generated land (paste is drawn on the land).</summary>
    Paste,

    /// <summary>Copper itself — a pad-shaped piece or a pad at a line end.</summary>
    Copper,
}

/// <summary>One pad outline found on the board, DBU, on the conductor drawing layer it sits on.</summary>
public sealed record PadOutline(Bbox Bounds, LayerKey Layer, PadOutlineSource Source)
{
    public long CentreX => (Bounds.MinX + Bounds.MaxX) / 2;
    public long CentreY => (Bounds.MinY + Bounds.MaxY) / 2;
    public long Width => Bounds.MaxX - Bounds.MinX;
    public long Height => Bounds.MaxY - Bounds.MinY;
}

/// <summary>A case a pad pair matches, with its fit — 0 is exact.</summary>
public sealed record LandPatternFit(SmtCase Case, DensityLevel Density, double Error)
{
    /// <summary><c>0402@N</c> — the case and the density the pads fit best.</summary>
    public string Display => $"{Case.Code}@{FootprintRef.CodeOf(Density)}";
}

/// <summary>Two pads read as one chip part.</summary>
/// <param name="A">Index into the pad list of the first pad (left, or lower).</param>
/// <param name="B">Index of the second.</param>
/// <param name="Vertical">Whether the part lies along y (90°).</param>
/// <param name="Best">The best-fitting case.</param>
/// <param name="RunnersUp">Other cases within <see cref="LandPatternMatch.AmbiguityBand"/> of it.</param>
public sealed record LandPatternCandidate(
    int A, int B, bool Vertical, LandPatternFit Best, IReadOnlyList<LandPatternFit> RunnersUp);

/// <summary>A pad read off a picture: its rectangle in pixels (y down) and the group — the layer — it was read on.
/// Pads in different groups never pair.</summary>
public sealed record PixelPad(double Left, double Top, double Right, double Bottom, int Group);

/// <summary>A scale at which a picture's pads are chip land patterns (brief-img-3 R-im3-2).</summary>
/// <param name="UmPerPixel">The scale.</param>
/// <param name="Support">How many pairs fit a case within <see cref="LandPatternMatch.ScaleFitTolerance"/> there.</param>
/// <param name="MeanError">Their mean RMS error.</param>
/// <param name="Cases">The cases found, most first.</param>
public sealed record LandPatternScale(double UmPerPixel, int Support, double MeanError, IReadOnlyList<(string Code, int Count)> Cases)
{
    /// <summary><c>4 × 0603, 2 × 0402</c>.</summary>
    public string CasesText => string.Join(", ", Cases.Select(c => $"{c.Count} × {c.Code}"));
}

/// <summary>Chip land patterns among a set of pad outlines.</summary>
public static class LandPatternMatch
{
    /// <summary>Each pad dimension and the gap must be within this of the reference.</summary>
    public const double Tolerance = 0.20;

    /// <summary>A different case whose error is within this of the best is named beside it.</summary>
    public const double AmbiguityBand = 0.05;

    /// <summary>
    /// Every two-pad land pattern among <paramref name="pads"/>. Overlapping candidates resolve by best
    /// fit, so a pad is in one candidate at most. Pads on different layers never pair.
    /// </summary>
    public static IReadOnlyList<LandPatternCandidate> Find(IReadOnlyList<PadOutline> pads, int dbuPerMicron)
    {
        ArgumentNullException.ThrowIfNull(pads);
        if (pads.Count < 2) return [];
        double umPerDbu = 1.0 / Math.Max(1, dbuPerMicron);
        long reach = (long)Math.Ceiling(References.MaxSpanUm * Math.Max(1, dbuPerMicron) * (1 + Tolerance));

        var order = Enumerable.Range(0, pads.Count).OrderBy(i => pads[i].CentreX).ToArray();
        var found = new List<LandPatternCandidate>();
        for (int oi = 0; oi < order.Length; oi++)
        {
            var a = pads[order[oi]];
            for (int oj = oi + 1; oj < order.Length; oj++)
            {
                var b = pads[order[oj]];
                if (b.CentreX - a.CentreX > reach) break;
                if (a.Layer != b.Layer || a.Source != b.Source) continue;
                if (Math.Abs(b.CentreY - a.CentreY) > reach) continue;
                if (Pair(pads, order[oi], order[oj], umPerDbu) is { } c) found.Add(c);
            }
        }

        // Best fit first; a pad is never in two parts.
        var used = new HashSet<int>();
        var kept = new List<LandPatternCandidate>();
        foreach (var c in found.OrderBy(c => c.Best.Error).ThenBy(c => c.A).ThenBy(c => c.B))
        {
            if (used.Contains(c.A) || used.Contains(c.B)) continue;
            used.Add(c.A);
            used.Add(c.B);
            kept.Add(c);
        }
        return kept;
    }

    /// <summary>The fit of two pads to every case, or null where none is within tolerance.</summary>
    private static LandPatternCandidate? Pair(IReadOnlyList<PadOutline> pads, int i, int j, double umPerDbu)
    {
        var p = pads[i];
        var q = pads[j];
        long dx = Math.Abs(q.CentreX - p.CentreX), dy = Math.Abs(q.CentreY - p.CentreY);

        // Side by side along x (0°) or along y (90°): the cross-axis offset is a small part of a pad.
        bool vertical;
        if (dy <= Tolerance * Math.Min(p.Height, q.Height) && dx > 0) vertical = false;
        else if (dx <= Tolerance * Math.Min(p.Width, q.Width) && dy > 0) vertical = true;
        else return null;

        double alongP = (vertical ? p.Height : p.Width) * umPerDbu, acrossP = (vertical ? p.Width : p.Height) * umPerDbu;
        double alongQ = (vertical ? q.Height : q.Width) * umPerDbu, acrossQ = (vertical ? q.Width : q.Height) * umPerDbu;
        double centres = (vertical ? dy : dx) * umPerDbu;
        double gap = centres - 0.5 * (alongP + alongQ);
        if (gap <= 0) return null;

        bool mask = p.Source == PadOutlineSource.Mask;
        var fits = new List<LandPatternFit>();
        foreach (var r in References.All)
        {
            double along = mask ? r.MaskAlongUm : r.AlongUm, across = mask ? r.MaskAcrossUm : r.AcrossUm;
            double gapRef = mask ? r.MaskGapUm : r.GapUm;
            if (!(gapRef > 0)) continue;
            double e1 = alongP / along - 1, e2 = acrossP / across - 1, e3 = alongQ / along - 1, e4 = acrossQ / across - 1;
            double e5 = gap / gapRef - 1;
            if (Math.Abs(e1) > Tolerance || Math.Abs(e2) > Tolerance || Math.Abs(e3) > Tolerance
                || Math.Abs(e4) > Tolerance || Math.Abs(e5) > Tolerance) continue;
            fits.Add(new LandPatternFit(r.Case, r.Density, Math.Sqrt((e1 * e1 + e2 * e2 + e3 * e3 + e4 * e4 + e5 * e5) / 5)));
        }
        if (fits.Count == 0) return null;
        if (InARow(pads, i, j, vertical)) return null;

        fits.Sort((x, y) => x.Error.CompareTo(y.Error));
        var best = fits[0];
        var runners = fits.Skip(1)
            .Where(f => f.Error <= best.Error + AmbiguityBand && !string.Equals(f.Case.Code, best.Case.Code, StringComparison.Ordinal))
            .GroupBy(f => f.Case.Code).Select(g => g.First()).ToList();

        bool firstIsP = vertical ? p.CentreY <= q.CentreY : p.CentreX <= q.CentreX;
        return new LandPatternCandidate(firstIsP ? i : j, firstIsP ? j : i, vertical, best, runners);
    }

    /// <summary>A third pad continues a row when it is this alike, and its gap to the nearer end is
    /// the pair's own gap to within this.</summary>
    public const double RowTolerance = 0.10;

    /// <summary>
    /// Whether a third pad, the same size, continues the pair's line with the pair's own gap — the next
    /// pin of a package row, which makes the pair two pins and not a part. Deliberately strict: two chip
    /// parts placed end to end are not a row unless they sit exactly one gap apart, and a pin row is
    /// exactly that.
    /// </summary>
    private static bool InARow(IReadOnlyList<PadOutline> pads, int i, int j, bool vertical)
    {
        var p = pads[i];
        var q = pads[j];
        double Along(PadOutline o) => vertical ? o.Height : o.Width;
        double gap = (vertical ? Math.Abs(q.CentreY - p.CentreY) : Math.Abs(q.CentreX - p.CentreX)) - 0.5 * (Along(p) + Along(q));
        for (int k = 0; k < pads.Count; k++)
        {
            if (k == i || k == j) continue;
            var c = pads[k];
            if (c.Layer != p.Layer || !Like(c, p)) continue;
            foreach (var end in new[] { p, q })
            {
                long off = vertical ? Math.Abs(c.CentreX - end.CentreX) : Math.Abs(c.CentreY - end.CentreY);
                if (off > Tolerance * (vertical ? end.Width : end.Height)) continue;
                double between = (vertical ? Math.Abs(c.CentreY - end.CentreY) : Math.Abs(c.CentreX - end.CentreX))
                                 - 0.5 * (Along(c) + Along(end));
                if (between > 0 && Math.Abs(between / gap - 1) <= RowTolerance) return true;
            }
        }
        return false;
    }

    private static bool Like(PadOutline a, PadOutline b) =>
        Math.Abs((double)a.Width / Math.Max(1, b.Width) - 1) <= RowTolerance &&
        Math.Abs((double)a.Height / Math.Max(1, b.Height) - 1) <= RowTolerance;

    // ── a scale search over a picture (brief-img-3 R-im3-2) ─────────────────────────────────────────

    /// <summary>A pair fits a case at a scale when its RMS error there is within this.</summary>
    public const double ScaleFitTolerance = 0.03;

    /// <summary>
    /// Every scale at which the pads of a PICTURE fit chip land patterns, strongest first. The pads are in
    /// pixels and the scale is unknown, so <see cref="Find"/> — this file's own fit, not a second one — is run
    /// over a log-spaced sweep of µm per pixel; at each step a candidate's support is the number of pairs it fits
    /// within <see cref="ScaleFitTolerance"/>. Each peak is refined to the support-weighted mean of the scales its
    /// pairs fit exactly (every dimension of a pair against its case, least squares in log scale), then counted
    /// again there. Peaks within 3 % of a stronger one are the same scale.
    /// </summary>
    public static IReadOnlyList<LandPatternScale> ScaleSearch(
        IReadOnlyList<PixelPad> pads, double minUmPerPixel = 0.5, double maxUmPerPixel = 500, double step = 1.01)
    {
        ArgumentNullException.ThrowIfNull(pads);
        if (pads.Count < 2 || !(minUmPerPixel > 0) || !(maxUmPerPixel > minUmPerPixel) || !(step > 1)) return [];

        var sweep = new List<(double Scale, int Support, double Error)>();
        for (double s = minUmPerPixel; s <= maxUmPerPixel; s *= step)
        {
            var (support, error, _) = FitAt(pads, s);
            if (support > 0) sweep.Add((s, support, error));
        }

        var found = new List<LandPatternScale>();
        foreach (var peak in sweep.OrderByDescending(p => p.Support).ThenBy(p => p.Error).ThenBy(p => p.Scale))
        {
            if (found.Any(f => SameScale(f.UmPerPixel, peak.Scale))) continue;
            double s = peak.Scale;
            for (int pass = 0; pass < 2; pass++)
            {
                var (_, _, fits) = FitAt(pads, s);
                if (fits.Count == 0) break;
                s = Math.Exp(fits.Average(f => Math.Log(BestScale(pads, f))));
            }
            var (support, error, kept) = FitAt(pads, s);
            if (support == 0) continue;
            if (found.Any(f => SameScale(f.UmPerPixel, s))) continue;
            var cases = kept.GroupBy(f => f.Best.Case.Code)
                            .Select(g => (Code: g.Key, Count: g.Count()))
                            .OrderByDescending(c => c.Count).ThenBy(c => c.Code, StringComparer.Ordinal).ToList();
            found.Add(new LandPatternScale(s, support, error, cases));
        }
        found.Sort((a, b) => a.Support != b.Support ? b.Support.CompareTo(a.Support) : a.MeanError.CompareTo(b.MeanError));
        return found;
    }

    private static bool SameScale(double a, double b) => Math.Abs(a / b - 1) <= 0.03;

    /// <summary>The pairs <see cref="Find"/> reads at <paramref name="umPerPixel"/>, those within
    /// <see cref="ScaleFitTolerance"/> kept: how many, and their mean error.</summary>
    private static (int Support, double MeanError, List<LandPatternCandidate> Fits) FitAt(IReadOnlyList<PixelPad> pads, double umPerPixel)
    {
        double k = umPerPixel * LayoutUnits.DefaultDbuPerMicron;
        var outlines = new List<PadOutline>(pads.Count);
        foreach (var p in pads)
            outlines.Add(new PadOutline(
                new Bbox((long)Math.Round(p.Left * k), (long)Math.Round(p.Top * k), (long)Math.Round(p.Right * k), (long)Math.Round(p.Bottom * k)),
                new LayerKey(p.Group, 0), PadOutlineSource.Copper));
        var fits = Find(outlines, LayoutUnits.DefaultDbuPerMicron).Where(c => c.Best.Error <= ScaleFitTolerance).ToList();
        return (fits.Count, fits.Count == 0 ? 0 : fits.Average(f => f.Best.Error), fits);
    }

    /// <summary>The µm per pixel at which one pair fits its case best: every dimension of the pair against the
    /// reference's, least squares in log scale.</summary>
    private static double BestScale(IReadOnlyList<PixelPad> pads, LandPatternCandidate c)
    {
        var r = References.All.First(x => x.Case == c.Best.Case && x.Density == c.Best.Density);
        var p = pads[c.A];
        var q = pads[c.B];
        double Along(PixelPad o) => c.Vertical ? o.Bottom - o.Top : o.Right - o.Left;
        double Across(PixelPad o) => c.Vertical ? o.Right - o.Left : o.Bottom - o.Top;
        double centres = c.Vertical ? Math.Abs((q.Top + q.Bottom) - (p.Top + p.Bottom)) / 2 : Math.Abs((q.Left + q.Right) - (p.Left + p.Right)) / 2;
        double gap = centres - 0.5 * (Along(p) + Along(q));
        double sum = Math.Log(r.AlongUm / Along(p)) + Math.Log(r.AcrossUm / Across(p)) + Math.Log(r.AlongUm / Along(q))
                     + Math.Log(r.AcrossUm / Across(q)) + Math.Log(r.GapUm / gap);
        return Math.Exp(sum / 5);
    }

    // ── the references ──────────────────────────────────────────────────────────────────────────────

    /// <summary>One case at one density, as the generator draws it, µm.</summary>
    internal sealed record Reference(
        SmtCase Case, DensityLevel Density,
        double AlongUm, double AcrossUm, double GapUm,
        double MaskAlongUm, double MaskAcrossUm, double MaskGapUm);

    /// <summary>Every case at every density, generated once.</summary>
    internal static class References
    {
        private static readonly Lazy<IReadOnlyList<Reference>> _all = new(Generate);

        public static IReadOnlyList<Reference> All => _all.Value;

        /// <summary>The widest span of any reference, µm — how far apart two pads may be and still pair.</summary>
        public static double MaxSpanUm => _maxSpan.Value;
        private static readonly Lazy<double> _maxSpan = new(() => All.Max(r => Math.Max(2 * r.AlongUm + r.GapUm, 2 * r.MaskAlongUm + r.MaskGapUm)));

        private static readonly LayerKey CopperKey = new(1, 0), MaskKey = new(2, 0);

        /// <summary>
        /// A minimal board technology the generator can draw on: a front copper on a laminate and a front
        /// solder mask. Its only job is to give the generator its copper and mask roles.
        /// </summary>
        private static Technology ReferenceTechnology()
        {
            var tech = new Technology
            {
                Name = "land-pattern reference",
                Layers =
                [
                    new LayerDef { Key = CopperKey, Name = "Top", Purpose = "conductor" },
                    new LayerDef { Key = MaskKey, Name = "Soldermask Top", Purpose = "soldermask" },
                ],
            };
            tech.Stackup.Layers =
            [
                new StackupLayer { Kind = StackupKind.Conductor, Name = "Top", DrawingLayers = [CopperKey], ThicknessDbu = 35_000 },
                new StackupLayer { Kind = StackupKind.Dielectric, Name = "Core", ThicknessDbu = 500_000, Epsr = 4.3 },
            ];
            return tech;
        }

        private static IReadOnlyList<Reference> Generate()
        {
            var tech = ReferenceTechnology();
            double umPerDbu = 1.0 / LayoutUnits.DefaultDbuPerMicron;
            var list = new List<Reference>();
            foreach (var c in SmtCaseTable.All)
                foreach (var d in new[] { DensityLevel.Most, DensityLevel.Nominal, DensityLevel.Least })
                {
                    var result = ChipLandPatternGenerator.Generate(FootprintRef.For(c, d), tech, PCellLayerSelection.Default);
                    var copper = result.Shapes.OfType<RectShape>().Where(s => s.Layer == CopperKey).OrderBy(s => s.X1).ToList();
                    var mask = result.Shapes.OfType<RectShape>().Where(s => s.Layer == MaskKey).OrderBy(s => s.X1).ToList();
                    if (copper.Count != 2 || mask.Count != 2) continue;
                    list.Add(new Reference(
                        c, d,
                        (copper[0].X2 - copper[0].X1) * umPerDbu, (copper[0].Y2 - copper[0].Y1) * umPerDbu,
                        (copper[1].X1 - copper[0].X2) * umPerDbu,
                        (mask[0].X2 - mask[0].X1) * umPerDbu, (mask[0].Y2 - mask[0].Y1) * umPerDbu,
                        (mask[1].X1 - mask[0].X2) * umPerDbu));
                }
            return list;
        }
    }
}

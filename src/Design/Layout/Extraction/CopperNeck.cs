// The narrowest place inside one piece of copper between two points on it —
// brief-lvs-8-findings.md R-lvs8-4b, asked from `LvsShortPath`.
//
// ── WHY THIS IS IN Extraction AND NOT IN Lvs ──────────────────────────────────────────────────
//
// R-lvs3-7: nothing under `src/Design/Layout/Lvs/` unions geometry, and a source scan holds it.
// That rule is not a formality — every forbidden call has exactly one implementation somewhere
// else, and a board whose connectivity the DRC, railRF and LVS disagree about is a bug none of
// them reports. "Where is this piece of metal at its narrowest between here and there" is a
// question about COPPER, not about a comparison, so it lives with the copper.
//
// ── IT IS THE DRC's OWN MINIMUM-WIDTH TEST, BISECTED ──────────────────────────────────────────
//
// `DrcEngine.CheckMinWidth` answers "which part of this conductor is narrower than w" by eroding
// by w/2 and dilating back. This asks the same question repeatedly, halving the interval, until it
// finds the largest w the two points survive together — which IS the narrowest metal on every
// route between them. Nothing new is invented; what is added is the bisection and the isolation of
// the bridge.

using Clipper2Lib;
using System.Linq;
using CircuitRF.Design.Layout.Drc;

namespace CircuitRF.Design.Layout.Extraction;

/// <summary>What the narrowest place between two points on one piece measures, and where.</summary>
/// <param name="WidthDbu">How wide the metal is there.</param>
/// <param name="Rings">The bridging metal, as the DRC marker convention spells a region.</param>
/// <param name="Bounds">Its bounding box — the finding's coordinate is this box's centre.</param>
public readonly record struct CopperNeckReading(long WidthDbu, long[][] Rings, Bbox Bounds);

/// <summary>Finding a constriction in one connected piece of copper.</summary>
public static class CopperNeck
{
    /// <summary>A piece with more outline than this is not measured at all — a report that took a
    /// minute to say where a short is would be one nobody waits for.</summary>
    public const int MaxVertices = 20_000;

    /// <summary>Clipper2's miter limit, the DRC engine's own.</summary>
    private const double MiterLimit = 2.0;

    /// <summary>
    /// The narrowest metal on any route between two points of one piece, or null where no opening
    /// up to half the piece separates them — which means there is no constriction worth naming.
    /// </summary>
    /// <param name="pieces">The partition the piece belongs to.</param>
    /// <param name="piece">Which piece.</param>
    /// <param name="ax">One point, DBU. Must be on the piece.</param>
    /// <param name="ay">DBU.</param>
    /// <param name="bx">The other, DBU.</param>
    /// <param name="by">DBU.</param>
    /// <param name="toleranceDbu">How finely to bisect. One tenth of a micron is what LVS asks
    /// for; the answer is reported to within twice this.</param>
    public static CopperNeckReading? Measure(
        CopperPieces pieces, int piece, long ax, long ay, long bx, long by, long toleranceDbu)
    {
        ArgumentNullException.ThrowIfNull(pieces);
        if (piece < 0 || piece >= pieces.Count) return null;

        var paths = pieces.PathsOfPiece(piece);
        if (paths.Sum(p => p.Count) > MaxVertices) return null;

        var bounds = pieces.BoundsOfPiece(piece);
        if (bounds.IsEmpty) return null;

        long cap = Math.Max(1, Math.Min(bounds.MaxX - bounds.MinX, bounds.MaxY - bounds.MinY) / 2 + 1);
        if (!Joined(paths, 0, ax, ay, bx, by, out _)) return null;   // not both on this piece

        // Up, then in. Doubling first keeps a wide piece cheap: a pour that never separates costs
        // log2(cap) tests and then stops, rather than a full bisection that was never going to
        // find anything.
        long lo = 0, hi = 0;
        for (long r = 1; r <= cap; r *= 2)
        {
            if (Joined(paths, r, ax, ay, bx, by, out _)) { lo = r; continue; }
            hi = r;
            break;
        }
        if (hi == 0) return null;

        long tolerance = Math.Max(1, toleranceDbu);
        while (hi - lo > tolerance)
        {
            long mid = lo + (hi - lo) / 2;
            if (Joined(paths, mid, ax, ay, bx, by, out _)) lo = mid; else hi = mid;
        }

        // At `hi` the two ends are apart, so the bridging metal is what the opening removed
        // between them. Isolating THAT rather than reporting everything the opening removed is
        // what keeps the marker on the spur instead of on every thin trace the piece also has.
        Joined(paths, hi, ax, ay, bx, by, out var opened);
        var region = Bridge(paths, opened, ax, ay, bx, by) ?? BoxAt((ax + bx) / 2, (ay + by) / 2, 2 * hi);

        return new CopperNeckReading(2 * hi, DrcRegions.ToRings(region), DrcRegions.BoundsOf(region));
    }

    /// <summary>
    /// Whether the two points are still one piece of metal once everything narrower than
    /// <c>2r</c> has been opened away.
    /// </summary>
    private static bool Joined(
        Paths64 paths, long r, long ax, long ay, long bx, long by, out List<Paths64> components)
    {
        components = r <= 0 ? DrcRegions.Components(paths) : Open(paths, r);

        int a = -1, b = -1;
        for (int i = 0; i < components.Count; i++)
        {
            if (a < 0 && Regions.Contains(components[i], ax, ay)) a = i;
            if (b < 0 && Regions.Contains(components[i], bx, by)) b = i;
        }
        return a >= 0 && a == b;
    }

    private static List<Paths64> Open(Paths64 paths, long r)
    {
        var eroded = Clipper.InflatePaths(paths, -r, JoinType.Miter, EndType.Polygon, MiterLimit);
        return eroded.Count == 0
            ? []
            : DrcRegions.Components(
                Clipper.InflatePaths(eroded, r, JoinType.Miter, EndType.Polygon, MiterLimit));
    }

    /// <summary>
    /// The metal that joins the two ends once the opening has pulled them apart: what the piece has
    /// and the opening does not, restricted to the one component of it that touches both ends.
    /// </summary>
    private static Paths64? Bridge(
        Paths64 paths, List<Paths64> opened, long ax, long ay, long bx, long by)
    {
        var a = opened.FirstOrDefault(c => Regions.Contains(c, ax, ay));
        var b = opened.FirstOrDefault(c => Regions.Contains(c, bx, by));

        // An end whose own metal is thinner than the neck has no surviving component, and then the
        // constriction IS that end — a pad narrower than everything it connects through.
        if (a is null || b is null) return null;

        var kept = Clipper.BooleanOp(ClipType.Union, a, b, LayoutClipper.Rule);
        var left = Clipper.BooleanOp(ClipType.Difference, paths, kept, LayoutClipper.Rule);

        foreach (var part in DrcRegions.Components(left))
        {
            var grown = Clipper.InflatePaths(part, 2.0, JoinType.Miter, EndType.Polygon, MiterLimit);
            if (Clipper.BooleanOp(ClipType.Intersection, grown, a, LayoutClipper.Rule).Count == 0) continue;
            if (Clipper.BooleanOp(ClipType.Intersection, grown, b, LayoutClipper.Rule).Count == 0) continue;
            return part;
        }

        return null;
    }

    private static Paths64 BoxAt(long x, long y, long size)
    {
        long half = Math.Max(1, size / 2);
        return [[new Point64(x - half, y - half), new Point64(x + half, y - half),
                 new Point64(x + half, y + half), new Point64(x - half, y + half)]];
    }
}

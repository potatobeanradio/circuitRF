// Test-only synthetic layout generator (docs/sonnet-briefs/brief-L2a-performance-harness.md §1).
// Never shipped — lives entirely under tests/Ui.Tests/, referenced by no production code.

using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.Tests.LayoutPerf;

/// <summary>Which shape population the generator draws from (§1 of the brief) — different content
/// stresses different renderer code paths.</summary>
public enum GeneratorProfile
{
    /// <summary>Rects and orthogonal polygons only — the PCB-ish baseline. No adaptive curve
    /// tessellation cost anywhere in this profile.</summary>
    Manhattan,

    /// <summary>Circles, rounded rects, arc-bearing curves AND arc-bearing paths — every shape pays
    /// the adaptive-tessellation cost the Manhattan profile never sees.</summary>
    CurveHeavy,

    /// <summary>A realistic blend: every basic kind, a polygon-with-holes population, labels, and a
    /// few bitmaps.</summary>
    Mixed,
}

/// <summary>
/// Deterministic, clustered synthetic-layout generator for the L2a performance harness.
///
/// <b>R-L2a-1 (determinism):</b> every decision — which region a shape lands in, its size, its exact
/// kind — is either a pure function of the shape's index <c>i</c> (kind, layer) or drawn from a single
/// <see cref="System.Random"/> seeded once and consumed in the same fixed order every run. Two calls
/// with the same arguments produce byte-identical output (via <see cref="LayoutPersistence.Serialize"/>),
/// and a save→load→save round trip reproduces the same bytes too.
///
/// <b>R-L2a-2 (clustered, not uniform):</b> a uniform random scatter is the WORST case for a spatial
/// index and nothing like a real layout. <see cref="BuildRegions"/> lays down 6 small dense clusters
/// (55% of shapes), 4 larger mid-density regions (30%), and a sparse background spanning the whole
/// extent (15%) — most of the extent is empty, exactly the distribution an R-tree (L2b) benefits from
/// and a uniform generator would fail to exercise.
/// </summary>
public static class SyntheticLayoutGenerator
{
    /// <summary>Half the overall design extent, in DBU, at the default 1000 DBU/µm resolution this
    /// generator always uses — a 100mm × 100mm square (-50mm..+50mm on each axis). Fixed regardless of
    /// <c>shapeCount</c> so "large empty stretches" stays true at every scale, including 500k.</summary>
    private const long ExtentHalf = 50_000_000;

    private const long SnapDbu = 1000; // 1 µm

    /// <summary>Standard 90°-arc bulge (tan(π/8)) — the same constant
    /// <c>LayoutRendererTests.ClosedCurve_OfFourQuarterArcs_FillsLikeACircle</c> uses to build a
    /// closed curve that fills exactly like a circle from 4 quarter-arcs.</summary>
    private const double QuarterArcBulge = 0.4142135623730951;

    public static LayoutView Generate(int shapeCount, int layerCount, int seed, GeneratorProfile profile)
    {
        layerCount = System.Math.Max(1, layerCount);
        var view = new LayoutView
        {
            DbuPerMicron = LayoutUnits.DefaultDbuPerMicron,
            DisplayUnit = LayoutUnit.Um,
            SnapDbu = SnapDbu,
            AngleMode = AngleMode.AnyAngle,
        };

        var rng = new System.Random(seed);
        var regions = BuildRegions(rng);

        // Fixed, index-driven reservations (not RNG-driven) so the shape-kind schedule never depends
        // on how many random draws a PREVIOUS shape happened to consume — a pure function of (i,
        // shapeCount, profile), which is what makes two runs with the same arguments byte-identical
        // regardless of any future change to how much randomness one particular shape kind consumes.
        int bitmapCount = profile == GeneratorProfile.Mixed ? System.Math.Clamp(shapeCount / 3000, 1, 8) : 0;
        int holeCount   = profile == GeneratorProfile.Mixed ? System.Math.Clamp(shapeCount / 300, 1, 400) : 0;

        for (int i = 0; i < shapeCount; i++)
        {
            var (cx, cy) = PickPoint(rng, regions);
            var layer = new LayerKey(1 + (i % layerCount), 0);

            LayoutShape shape = BuildShape(rng, profile, i, bitmapCount, holeCount, cx, cy);
            shape.Layer = layer;
            view.Shapes.Add(shape);
        }

        return view;
    }

    /// <summary>The <see cref="Technology"/> a generated <see cref="LayoutView"/> is measured against —
    /// <paramref name="layerCount"/> layers with distinct colors and evenly spread <c>ZOrder</c>. A
    /// separate call from <see cref="Generate"/> (not bundled into the return) so a caller can reuse one
    /// technology across several layouts of the same layer count, matching how a real workspace has one
    /// shared <c>.ctech</c> for many <c>.clay</c> files.</summary>
    public static Technology GenerateTechnology(int layerCount)
    {
        layerCount = System.Math.Max(1, layerCount);
        var tech = new Technology
        {
            Name = "Synthetic",
            DefaultDisplayUnit = LayoutUnit.Um,
            DefaultSnapDbu = SnapDbu,
            DefaultFlattenTolDbu = 500,
            DefaultLabelHeightDbu = 3000,
        };
        for (int i = 0; i < layerCount; i++)
        {
            double hue = 360.0 * i / layerCount;
            var (r, g, b) = HsvToRgb(hue, 0.55, 0.85);
            tech.Layers.Add(new LayerDef
            {
                Key = new LayerKey(1 + i, 0),
                Name = $"L{i + 1}",
                Color = new Rgba(r, g, b),
                FillOpacity = 0.35,
                ZOrder = i,
                Visible = true,
                Selectable = true,
            });
        }
        return tech;
    }

    // ── Clustering (R-L2a-2) ────────────────────────────────────────────────────

    private readonly record struct Region(double Cx, double Cy, double Radius, double Weight);

    private static List<Region> BuildRegions(System.Random rng)
    {
        var regions = new List<Region>(11);

        // 6 dense clusters — 55% of all shapes, small radius.
        for (int i = 0; i < 6; i++)
        {
            double cx = (rng.NextDouble() * 2 - 1) * ExtentHalf * 0.8;
            double cy = (rng.NextDouble() * 2 - 1) * ExtentHalf * 0.8;
            regions.Add(new Region(cx, cy, ExtentHalf * 0.01, 55.0 / 6));
        }

        // 4 mid-density regions — 30%, larger radius.
        for (int i = 0; i < 4; i++)
        {
            double cx = (rng.NextDouble() * 2 - 1) * ExtentHalf * 0.9;
            double cy = (rng.NextDouble() * 2 - 1) * ExtentHalf * 0.9;
            regions.Add(new Region(cx, cy, ExtentHalf * 0.04, 30.0 / 4));
        }

        // Sparse background spanning the whole extent — 15%. This is what makes "large empty
        // stretches" real: most of a 100mm x 100mm square is not within any dense/mid cluster.
        regions.Add(new Region(0, 0, ExtentHalf, 15.0));

        return regions;
    }

    private static (long X, long Y) PickPoint(System.Random rng, List<Region> regions)
    {
        double totalWeight = 0;
        foreach (var r in regions) totalWeight += r.Weight;

        double u = rng.NextDouble() * totalWeight;
        Region chosen = regions[^1];
        double acc = 0;
        foreach (var r in regions)
        {
            acc += r.Weight;
            if (u <= acc) { chosen = r; break; }
        }

        // Uniform sampling within a disk: radius ~ sqrt(u) * R, angle ~ uniform(0, 2*pi).
        double radius = System.Math.Sqrt(rng.NextDouble()) * chosen.Radius;
        double angle = rng.NextDouble() * 2 * System.Math.PI;
        double x = chosen.Cx + radius * System.Math.Cos(angle);
        double y = chosen.Cy + radius * System.Math.Sin(angle);

        long lx = (long)System.Math.Round(System.Math.Clamp(x, -ExtentHalf, ExtentHalf) / SnapDbu) * SnapDbu;
        long ly = (long)System.Math.Round(System.Math.Clamp(y, -ExtentHalf, ExtentHalf) / SnapDbu) * SnapDbu;
        return (lx, ly);
    }

    // ── Shape kind schedule (pure function of index — see the determinism note above) ──────────────

    private static LayoutShape BuildShape(System.Random rng, GeneratorProfile profile, int i, int bitmapCount, int holeCount, long cx, long cy)
    {
        // Small, realistic feature half-sizes (2..20 µm), independent of shapeCount so the
        // distribution stays the same shape at every scale.
        long halfW = 2_000 + (long)(rng.NextDouble() * 18_000);
        long halfH = 2_000 + (long)(rng.NextDouble() * 18_000);

        if (profile == GeneratorProfile.Mixed)
        {
            if (i < bitmapCount) return BuildBitmap(cx, cy, i);
            if (i < bitmapCount + holeCount) return BuildPolygonWithHole(cx, cy, System.Math.Max(halfW, halfH));
            if ((i - bitmapCount - holeCount) % 20 == 0) return BuildLabel(cx, cy, i);

            return (i % 5) switch
            {
                0 => BuildRect(cx, cy, halfW, halfH),
                1 => BuildLShape(cx, cy, halfW, halfH),
                2 => BuildRoundedRect(cx, cy, halfW, halfH),
                3 => BuildCircle(cx, cy, System.Math.Min(halfW, halfH)),
                _ => BuildArcCurve(cx, cy, System.Math.Min(halfW, halfH)),
            };
        }

        if (profile == GeneratorProfile.CurveHeavy)
        {
            long r = System.Math.Min(halfW, halfH);
            return (i % 4) switch
            {
                0 => BuildCircle(cx, cy, r),
                1 => BuildRoundedRect(cx, cy, halfW, halfH),
                2 => BuildArcCurve(cx, cy, r),
                _ => BuildArcPath(cx, cy, r),
            };
        }

        // Manhattan: rects and orthogonal polygons only.
        return (i % 2 == 0) ? BuildRect(cx, cy, halfW, halfH) : BuildLShape(cx, cy, halfW, halfH);
    }

    private static RectShape BuildRect(long cx, long cy, long halfW, long halfH) => new()
    {
        X1 = cx - halfW, Y1 = cy - halfH, X2 = cx + halfW, Y2 = cy + halfH,
    };

    private static RoundedRectShape BuildRoundedRect(long cx, long cy, long halfW, long halfH) => new()
    {
        X1 = cx - halfW, Y1 = cy - halfH, X2 = cx + halfW, Y2 = cy + halfH,
        CornerRadius = (long)(System.Math.Min(halfW, halfH) * 0.4),
    };

    private static CircleShape BuildCircle(long cx, long cy, long r) => new() { Cx = cx, Cy = cy, R = System.Math.Max(1, r) };

    /// <summary>A rectilinear (Manhattan) "L" hexagon — every edge axis-aligned, no diagonal segment
    /// anywhere, satisfying the Manhattan profile's "orthogonal polygons only" constraint.</summary>
    private static PolygonShape BuildLShape(long cx, long cy, long halfW, long halfH)
    {
        long w = halfW * 2, h = halfH * 2;
        long nw = (long)(w * 0.4), nh = (long)(h * 0.4);
        long x0 = cx - halfW, y0 = cy - halfH;

        long[] xy =
        [
            x0,          y0,
            x0 + w,      y0,
            x0 + w,      y0 + h - nh,
            x0 + w - nw, y0 + h - nh,
            x0 + w - nw, y0 + h,
            x0,          y0 + h,
        ];
        return new PolygonShape { Xy = xy };
    }

    /// <summary>A polygon whose Holes list carries one contained, non-intersecting inner ring (§3.1a
    /// R10b) — exercises hole-aware fill/hit-test/flatten without needing Clipper2 to derive it.</summary>
    private static PolygonShape BuildPolygonWithHole(long cx, long cy, long half)
    {
        long outerHalf = System.Math.Max(6_000, half * 2);
        long innerHalf = outerHalf / 3;

        long[] outer =
        [
            cx - outerHalf, cy - outerHalf,
            cx + outerHalf, cy - outerHalf,
            cx + outerHalf, cy + outerHalf,
            cx - outerHalf, cy + outerHalf,
        ];
        long[] hole =
        [
            cx - innerHalf, cy - innerHalf,
            cx + innerHalf, cy - innerHalf,
            cx + innerHalf, cy + innerHalf,
            cx - innerHalf, cy + innerHalf,
        ];
        return new PolygonShape { Xy = outer, Holes = [hole] };
    }

    /// <summary>A closed curve built from 4 quarter-arcs — fills like a circle, exercises the same
    /// adaptive-tessellation and arc-flattening cost path a real curved boundary pays.</summary>
    private static CurveShape BuildArcCurve(long cx, long cy, long r)
    {
        r = System.Math.Max(1, r);
        long[] xy = [cx + r, cy, cx, cy + r, cx - r, cy, cx, cy - r];
        return new CurveShape
        {
            Xy = xy,
            Edges =
            [
                new LayoutEdge { Kind = EdgeKind.Arc, Bulge = QuarterArcBulge },
                new LayoutEdge { Kind = EdgeKind.Arc, Bulge = QuarterArcBulge },
                new LayoutEdge { Kind = EdgeKind.Arc, Bulge = QuarterArcBulge },
                new LayoutEdge { Kind = EdgeKind.Arc, Bulge = QuarterArcBulge },
            ],
        };
    }

    /// <summary>A 2-vertex open Path whose one edge is an Arc — the "paths" half of CurveHeavy's
    /// "arc-bearing curves and paths" population (§1). Adaptive curve tessellation on an open
    /// centerline is a distinct code path from a closed Curve's (<c>BuildPathOutline</c> vs.
    /// <c>BuildShapePath</c>'s Curve case) and must be exercised too.</summary>
    private static PathShape BuildArcPath(long cx, long cy, long r)
    {
        r = System.Math.Max(1, r);
        return new PathShape
        {
            Xy = [cx - r, cy, cx + r, cy],
            Edges = [new LayoutEdge { Kind = EdgeKind.Arc, Bulge = 0.5 }],
            Width = System.Math.Max(500, r / 5),
            End = PathEndStyle.Round,
        };
    }

    private static LabelShape BuildLabel(long cx, long cy, int i) => new()
    {
        X = cx, Y = cy, Text = $"L{i % 10_000}", Height = 3_000, Rotation = LayoutRotation.R0,
    };

    /// <summary>A reference-image placeholder — the path deliberately never resolves (this is
    /// test-only synthetic geometry, not a real asset), so it always exercises
    /// <c>BitmapCache.DrawBrokenPlaceholder</c> rather than a real decode. That is a lighter draw cost
    /// than decoding a real bitmap would be — noted explicitly in the baseline report rather than
    /// silently assumed representative.</summary>
    private static BitmapShape BuildBitmap(long cx, long cy, int i)
    {
        long half = 40_000;
        return new BitmapShape
        {
            ImagePathRef = $"synthetic-nonexistent-{i}.png",
            X = cx - half, Y = cy - half, W = half * 2, H = half * 2,
            Opacity = 1.0,
        };
    }

    // ── Tiny deterministic HSV -> RGB (distinct layer colors, no external dependency) ───────────────

    private static (byte R, byte G, byte B) HsvToRgb(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        double c = v * s;
        double x = c * (1 - System.Math.Abs((h / 60.0 % 2) - 1));
        double m = v - c;
        var (r, g, b) = h switch
        {
            < 60  => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _     => (c, 0.0, x),
        };
        return ((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }
}

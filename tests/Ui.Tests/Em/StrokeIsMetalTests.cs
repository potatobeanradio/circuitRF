// ANT-1 — a width-bearing Path is metal (docs/sonnet-briefs/brief-antenna-1-stroked-path-is-metal.md).
//
// What this file is really about: until this brief the planar extractor discarded EVERY PathShape, on
// the stated premise that a Path "encloses no area". A stroke of non-zero width is how the Gerber and
// board readers represent every track, primitive for primitive — so an imported board was EM-simulated
// with its traces missing and nothing said so. The owner's patch board solved as a pure 1.6 pF
// capacitor (0.70 - j56.5 Ohm at 1.7 GHz, 2.3 % of the port's power leaving it), flat and monotonic
// across 1.55-1.90 GHz, and doubling the mesh with conformal cells switched on did not move a digit —
// which is what excludes mesh coarseness and leaves only "the conductor is not in the model".
//
// THE DECISIVE GATE IS AN INDEPENDENTLY-AUTHORED ORACLE, not a second circuitRF path agreeing with
// itself: the rectangle every comparison here is made against is written by hand from the stroke's own
// endpoints and width (a stroke from (0, 1.45 mm) to (4 mm, 1.45 mm) of width 2.9 mm covers exactly
// x in [0, 4 mm], y in [0, 2.9 mm]), and it is the SAME rectangle PlanarRunTests has always used. A
// stroke and that rectangle must extract to the same copper and solve to the same answer.

using System.Numerics;
using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Em;

namespace CircuitRF.Ui.Tests.Em;

public class StrokeIsMetalTests : IDisposable
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;

    private static readonly LayerKey TopCopper    = new(1, 0);
    private static readonly LayerKey BottomCopper = new(2, 0);   // ground-designated in Pcb2Layer
    private static readonly LayerKey Drill        = new(7, 0);   // bound to the through-hole via entry

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "crf-ant1-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private static long Mm(double mm) => (long)Math.Round(mm * 1000 * Dbu);

    private static PlanarExtractionResult Extract(params LayoutShape[] shapes)
        => PlanarExtractor.Extract(shapes, StarterTechnologies.Pcb2Layer(), Dbu, 5e9);

    /// <summary>The test's OWN area, from the metre-space polygons the problem carries — shoelace on
    /// the outer ring less each hole. Deliberately not the extractor's own helper: the whole point of
    /// the oracle is that two structures are measured the same way by something outside the code
    /// under test.</summary>
    private static double MetalAreaM2(PlanarProblem p)
    {
        double total = 0;
        foreach (var layer in p.Layers)
            foreach (var poly in layer.Polygons)
            {
                total += Shoelace(poly.Outer);
                foreach (var hole in poly.HoleRings) total -= Shoelace(hole);
            }
        return total;
    }

    private static double Shoelace(IReadOnlyList<EmPoint> ring)
    {
        double s = 0;
        for (int i = 0, n = ring.Count; i < n; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % n];
            s += a.X * b.Y - b.X * a.Y;
        }
        return Math.Abs(s) / 2.0;
    }

    private static (double MinX, double MinY, double MaxX, double MaxY) Extent(PlanarProblem p)
    {
        double x0 = double.PositiveInfinity, y0 = double.PositiveInfinity;
        double x1 = double.NegativeInfinity, y1 = double.NegativeInfinity;
        foreach (var layer in p.Layers)
            foreach (var poly in layer.Polygons)
            {
                var b = poly.Bounds();
                x0 = Math.Min(x0, b.MinX); y0 = Math.Min(y0, b.MinY);
                x1 = Math.Max(x1, b.MaxX); y1 = Math.Max(y1, b.MaxY);
            }
        return (x0, y0, x1, y1);
    }

    // ── The two layouts the oracle compares ──────────────────────────────────────────────────
    //
    // Identical but for HOW the line is drawn. The rectangle is PlanarRunTests.NewLayout()'s, which
    // is where the coarse-mesh planar run has always lived.

    private static LayoutView AsStroke(PathEndStyle end = PathEndStyle.Flush)
    {
        var v = new LayoutView { DbuPerMicron = Dbu };
        v.Shapes.Add(new PathShape
        {
            Layer = TopCopper,
            Xy    = [0, Mm(1.45), Mm(4), Mm(1.45)],
            Width = Mm(2.9),
            End   = end,
        });
        AddPorts(v);
        return v;
    }

    private static LayoutView AsRectangle()
    {
        var v = new LayoutView { DbuPerMicron = Dbu };
        v.Shapes.Add(new RectShape { Layer = TopCopper, X1 = 0, Y1 = 0, X2 = Mm(4), Y2 = Mm(2.9) });
        AddPorts(v);
        return v;
    }

    private static void AddPorts(LayoutView v)
    {
        v.Shapes.Add(new LabelShape
        {
            Layer = TopCopper, X = 0, Y = Mm(1.45), Text = "P1", Height = Mm(0.4), IsPort = true,
        });
        v.Shapes.Add(new LabelShape
        {
            Layer = TopCopper, X = Mm(4), Y = Mm(1.45), Text = "P2", Height = Mm(0.4), IsPort = true,
        });
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // THE ORACLE — the same copper, drawn two ways
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void AStroke_AndTheHandWrittenRectangleItCovers_ExtractToTheSameMetal()
    {
        var stroke = Extract([.. AsStroke().Shapes]);
        var rect   = Extract([.. AsRectangle().Shapes]);

        Assert.True(stroke.Ok, stroke.Refusal);
        Assert.True(rect.Ok, rect.Refusal);

        // 4 mm x 2.9 mm, said twice by two different documents.
        Assert.Equal(11.6e-6, MetalAreaM2(rect.Problem!),   12);
        Assert.Equal(11.6e-6, MetalAreaM2(stroke.Problem!), 12);
        Assert.Equal(Extent(rect.Problem!), Extent(stroke.Problem!));
    }

    /// <summary>
    /// The measurement the brief exists for. A flush stroke and the rectangle covering the same copper
    /// must SOLVE to the same answer, not merely extract to the same area — the failure being gated is
    /// that the conductor was absent from the solve, which an area assertion alone could not see if
    /// the polygons were dropped later.
    ///
    /// <para>Coarse mesh, one frequency, for PlanarRunTests' own reason: this is about whether the
    /// metal is there, not about mesh convergence.</para>
    /// </summary>
    [Fact]
    public void AStroke_AndTheHandWrittenRectangleItCovers_SolveToTheSameAnswer()
    {
        Directory.CreateDirectory(_dir);

        var fromStroke = Run(AsStroke(), "stroke");
        var fromRect   = Run(AsRectangle(), "rect");

        Assert.Equal(EmRunStatus.Ok, fromStroke.Status);
        Assert.Equal(EmRunStatus.Ok, fromRect.Status);

        var a = SMatrix(fromStroke);
        var b = SMatrix(fromRect);
        Assert.Equal(b.Length, a.Length);
        for (int i = 0; i < a.Length; i++)
            Assert.True(Complex.Abs(a[i] - b[i]) < 1e-9,
                        $"S[{i}] differs: stroke {a[i]}, hand-written rectangle {b[i]}");
    }

    private EmRunResult Run(LayoutView view, string name)
        => EmRunService.Run(
            PlanarRunTests.NewSetup(),
            new EmLayoutSource("layout.clay", view, StarterTechnologies.Pcb2Layer(), Dbu),
            Path.Combine(_dir, name));

    private static Complex[] SMatrix(EmRunResult r)
    {
        var data = r.Data!;
        string group = Assert.Single(data.Groups, g => data.CubesIn(g).ContainsKey("S"));
        var s = data.CubesIn(group)["S"];
        return [.. s.ComplexValues];
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // THE END STYLES — all four, asserted as EXTENTS
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <c>Flush</c> ends at the endpoint; <c>Round</c>, <c>Square</c> and <c>Extended</c> extend by
    /// half the width. The extent is asserted rather than a picture, and <c>Round</c> carries the one
    /// tolerance in the file: its cap is a polygonal approximation of a semicircle at the layout's own
    /// flatten tolerance (1 µm on this technology), so its extreme x is inside the true one by up to
    /// that much and can never exceed it.
    /// </summary>
    [Theory]
    [InlineData(PathEndStyle.Flush,    0.0)]
    [InlineData(PathEndStyle.Square,   1.45)]
    [InlineData(PathEndStyle.Extended, 1.45)]
    [InlineData(PathEndStyle.Round,    1.45)]
    public void EveryEndStyle_ExtendsTheOutlineByTheRuleItNames(PathEndStyle end, double overhangMm)
    {
        var r = Extract([.. AsStroke(end).Shapes]);
        Assert.True(r.Ok, r.Refusal);

        var (minX, minY, maxX, maxY) = Extent(r.Problem!);

        // Across the stroke, every style is the same: the width, centred on the centreline.
        Assert.Equal(0.0,      minY, 9);
        Assert.Equal(2.9e-3,   maxY, 9);

        double tolM = end == PathEndStyle.Round ? 2e-6 : 1e-9;
        Assert.Equal(-overhangMm * 1e-3,       minX, TolDigits(tolM));
        Assert.Equal(4e-3 + overhangMm * 1e-3, maxX, TolDigits(tolM));

        // …and a round cap may only ever fall SHORT of the true semicircle, never past it.
        if (end == PathEndStyle.Round)
        {
            Assert.True(minX >= -overhangMm * 1e-3 - 1e-12);
            Assert.True(maxX <= 4e-3 + overhangMm * 1e-3 + 1e-12);
        }
    }

    private static int TolDigits(double tolM) => (int)Math.Floor(-Math.Log10(tolM));

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // WHAT MUST STILL BE IGNORED
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A zero-width Path IS a centreline — it is what the DXF reader produces for an open
    /// polyline — and meshing it as a hairline would invent copper. It stays ignored, and the sentence
    /// that reports it must now say ZERO-WIDTH, because "a Path is a centreline" is the false premise
    /// this whole brief removed.</summary>
    [Fact]
    public void AZeroWidthPath_IsStillIgnored_AndTheSentenceIsTheZeroWidthOne()
    {
        var r = Extract(
            new RectShape { Layer = TopCopper, X1 = 0, Y1 = 0, X2 = Mm(4), Y2 = Mm(2.9) },
            new PathShape { Layer = TopCopper, Xy = [0, Mm(1.45), Mm(4), Mm(1.45)], Width = 0 });

        Assert.True(r.Ok, r.Refusal);

        // The rectangle alone — the centreline added nothing.
        Assert.Equal(11.6e-6, MetalAreaM2(r.Problem!), 12);
        Assert.Contains(r.Notes, n => n.Contains("ZERO-WIDTH Path is a centreline", StringComparison.Ordinal));
        Assert.DoesNotContain(r.Notes, n => n.Contains("were outlined into conductor artwork",
                                                       StringComparison.Ordinal));
    }

    /// <summary>The conversion must not smuggle a ground pour into the mesh. A stroke on a
    /// ground-designated conductor is ground artwork exactly as a rectangle there is, and the
    /// extractor refuses to mesh either — the ground plane is the laterally infinite return the
    /// Green's function handles analytically.
    ///
    /// <para><b>ANT-11 changed what "ignored" means here, and the distinction is the point.</b> The
    /// pour is still not MESHED — the metal area is the signal line's alone, which is what this test
    /// exists to hold — but its outline is now READ and carried as a described boundary so the run can
    /// report how large the real plane is. A width-bearing stroke is part of that outline like any
    /// other artwork on the plane: a plane is routinely stitched with thick tracks, and one shape
    /// cannot be copper for the purpose of measuring the plane and not copper for the purpose of
    /// drawing it.</para></summary>
    [Fact]
    public void AStrokeOnAGroundDesignatedConductor_IsNotMeshed_ButIsReadAsTheGroundOutline()
    {
        var r = Extract(
            new RectShape { Layer = TopCopper, X1 = 0, Y1 = 0, X2 = Mm(4), Y2 = Mm(2.9) },
            new PathShape
            {
                Layer = BottomCopper,
                Xy    = [0, Mm(1.45), Mm(4), Mm(1.45)],
                Width = Mm(2.9),
            });

        Assert.True(r.Ok, r.Refusal);
        Assert.Equal(11.6e-6, MetalAreaM2(r.Problem!), 12);   // the signal line only — NOT meshed
        Assert.Contains(r.Notes, n => n.Contains("ground-designated conductor layer and none of them " +
                                                 "is meshed", StringComparison.Ordinal));
        Assert.DoesNotContain(r.Notes, n => n.Contains("were outlined into conductor artwork",
                                                       StringComparison.Ordinal));

        // …and the same stroke IS the plane's outline, at its stroked extent.
        var outline = r.Problem!.GroundOutline;
        Assert.NotNull(outline);
        var (w, h) = outline.BoxSize();
        Assert.Equal(4e-3, w, 9);
        Assert.Equal(2.9e-3, h, 9);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // WHAT MUST BE REPORTED
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The reason this went unnoticed for as long as it did is that the only sentence about
    /// strokes was an ignored-count among twenty, saying nothing about what was lost. The note now
    /// names the CONVERSION and the metal it added.</summary>
    [Fact]
    public void TheReport_NamesTheConversionCount_AndTheMetalItAdded()
    {
        var r = Extract([.. AsStroke().Shapes]);
        Assert.True(r.Ok, r.Refusal);

        string note = Assert.Single(r.Notes, n => n.Contains("were outlined into conductor artwork",
                                                            StringComparison.Ordinal));
        Assert.Contains("1 width-bearing Path shape(s)", note, StringComparison.Ordinal);
        Assert.Contains("11.6 mm²", note, StringComparison.Ordinal);
    }

    /// <summary>An arc-bearing stroke flattens at the layout's own tolerance, and is counted in the
    /// flattening note rather than silently — that note is where the extractor says the tolerance is
    /// an ARTWORK decision the mesh does not inherit.</summary>
    [Fact]
    public void AnArcBearingStroke_IsFlattenedAndCountedInTheFlatteningNote()
    {
        var arc = new PathShape
        {
            Layer = TopCopper,
            Xy    = [0, Mm(1.45), Mm(4), Mm(1.45), Mm(8), Mm(1.45)],
            Edges =
            [
                new LayoutEdge { Kind = EdgeKind.Arc, Bulge = 0.4142 },
                new LayoutEdge { Kind = EdgeKind.Line },
            ],
            Width = Mm(2.9),
        };

        var r = Extract(arc);
        Assert.True(r.Ok, r.Refusal);

        // The arc really did subdivide: far more vertices than the four a straight stroke outlines to.
        var poly = Assert.Single(r.Problem!.Layers[0].Polygons);
        Assert.True(poly.Outer.Count > 20, $"{poly.Outer.Count} vertices — the arc did not flatten");

        Assert.Contains(r.Notes, n => n.Contains("curved shape(s) were flattened to polygons",
                                                 StringComparison.Ordinal));
        Assert.Contains(r.Notes, n => n.Contains("were outlined into conductor artwork",
                                                 StringComparison.Ordinal));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // THE VIA-BOUND LAYER — §3a, converted by the same rule
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A routed, plated slot is drawn as a stroke, and the same geometric argument applies one layer
    /// down: the alternative is that one shape means copper on a conductor layer and nothing on a via
    /// layer. MIM-1's own sentence for an ignored Path survives, narrowed to the zero-width case.
    /// </summary>
    [Fact]
    public void AWidthBearingStrokeOnAViaBoundLayer_BecomesAViaFootprint()
    {
        var r = Extract(
            new RectShape { Layer = TopCopper,    X1 = 0, Y1 = 0, X2 = Mm(4), Y2 = Mm(2.9) },
            new RectShape { Layer = BottomCopper, X1 = 0, Y1 = 0, X2 = Mm(4), Y2 = Mm(2.9) },
            new PathShape
            {
                Layer = Drill,
                Xy    = [Mm(1), Mm(1.45), Mm(3), Mm(1.45)],
                Width = Mm(0.5),
            });

        Assert.True(r.Ok, r.Refusal);
        var via = Assert.Single(r.Problem!.ViaList);
        var footprint = Assert.Single(via.Polygons);
        Assert.Equal(2e-3 * 0.5e-3, Shoelace(footprint.Outer), 12);

        Assert.Contains(r.Notes, n => n.Contains("were outlined into via footprints",
                                                 StringComparison.Ordinal));
    }
}

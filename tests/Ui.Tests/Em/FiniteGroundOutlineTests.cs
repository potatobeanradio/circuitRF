// ================================================================
//  FiniteGroundOutlineTests.cs — ANT-11 §2, the extraction half.
//
//  The extractor already FOUND the ground pour and threw it away with a one-line ignored-count whose
//  sentence ended "modelling one is not part of L9". The first half of that was true and still is
//  (R-fg-1: nothing meshes this, nothing stamps it, no matrix entry moves); the second half was the
//  whole of it, and it discarded the one thing that lets a run say how big the plane actually is in
//  wavelengths — which is the number that decides whether the infinite-plane assumption is defensible.
//
//  The interesting case is R-fg-2 and it needs a stackup with TWO designated planes, which is exactly
//  what the 4-layer starter has. On such a board "the ground layer" names two different conductors and
//  only one of them is THIS run's return plane; measuring the other would report a size for a plane the
//  fields never see. These tests hold that selection, and hold that reading the outline changed nothing.
// ================================================================

using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Em;

namespace CircuitRF.Ui.Tests.Em;

public class FiniteGroundOutlineTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private readonly Xunit.Abstractions.ITestOutputHelper _out = output;

    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;
    private const string FourLayer = "pcb-4layer_FR-4_62mil_1oz";

    private static long Mm(double m) => (long)Math.Round(m * 1000.0 * Dbu);

    private static Technology Tech() => ShippedTechnologies.Load(FourLayer);

    private static List<StackupLayer> Conductors(Technology t) =>
        [.. t.Stackup.Layers.Where(l => l.Kind == StackupKind.Conductor)];

    private static RectShape Rect(LayerKey layer, double x1, double y1, double x2, double y2) =>
        new() { Layer = layer, X1 = Mm(x1), Y1 = Mm(y1), X2 = Mm(x2), Y2 = Mm(y2) };

    /// <summary>
    /// A patch on the TOP conductor plus pours on BOTH designated planes. R-em-4 makes Inner 1 the
    /// return plane for top-layer metal (the other designated plane, Bottom Copper, sits below it), so
    /// Inner 1's pour is the one that may be measured.
    /// </summary>
    private static PlanarExtractionResult Extract(
        double innerSide = 70, double bottomSide = 120, bool drawBottom = true)
    {
        var tech = Tech();
        var c = Conductors(tech);
        var shapes = new List<LayoutShape>
        {
            Rect(c[0].DrawingLayers[0], 0, 0, 49.4, 41.3),                  // the patch, on top copper
            Rect(c[1].DrawingLayers[0], -(innerSide - 49.4) / 2, -(innerSide - 41.3) / 2,
                                        49.4 + (innerSide - 49.4) / 2,
                                        41.3 + (innerSide - 41.3) / 2),     // the pour, on Inner 1
        };
        if (drawBottom)
            shapes.Add(Rect(c[3].DrawingLayers[0], -20, -20, bottomSide, bottomSide));

        return PlanarExtractor.Extract(shapes, tech, Dbu, maxFrequencyHz: 1.74e9);
    }

    // ── §2 — the outline is READ, from the right plane ────────────────────────────────────────

    /// <summary>
    /// <b>The pour on THIS run's return plane becomes the problem's ground outline</b>, carrying that
    /// conductor's own name — so a caller reporting the plane's size never has to re-derive R-em-4.
    /// </summary>
    [Fact]
    public void ThePourOnTheReturnPlane_BecomesTheProblemsGroundOutline()
    {
        var r = Extract();
        Assert.True(r.Ok, r.Refusal);

        var outline = r.Problem!.GroundOutline;
        Assert.NotNull(outline);
        Assert.Equal(Conductors(Tech())[1].Name, outline.ConductorName);

        var (w, h) = outline.BoxSize();
        _out.WriteLine($"  '{outline.ConductorName}': {w * 1e3:F3} × {h * 1e3:F3} mm, " +
                       $"{outline.AreaM2 * 1e6:F1} mm², {outline.Polygons.Count} pour(s)");
        Assert.Equal(70e-3, w, 9);
        Assert.Equal(70e-3, h, 9);
        Assert.Equal(70e-3 * 70e-3, outline.AreaM2, 12);
    }

    /// <summary>
    /// <b>R-fg-2 — the OTHER designated plane's pour is not measured, and the note says why.</b> This is
    /// the easy mistake: both conductors are "the ground layer" in the technology, and pooling them
    /// would put a 140 mm bottom pour's size on a run whose fields return through a 70 mm inner one.
    /// Here the wrong answer would be nearly twice the right one.
    /// </summary>
    [Fact]
    public void APourOnADifferentGroundPlane_IsCountedButNotMeasured()
    {
        var r = Extract(innerSide: 70, bottomSide: 120);
        Assert.True(r.Ok, r.Refusal);

        var (w, _) = r.Problem!.GroundOutline!.BoxSize();
        Assert.Equal(70e-3, w, 9);                       // Inner 1's, not the bottom pour's 140 mm

        string note = Assert.Single(r.Notes, n => n.Contains("ground-designated conductor layer and"));
        _out.WriteLine(note);
        Assert.Contains("2 shape(s)", note);
        Assert.Contains("1 of them are on", note);
        Assert.Contains("The other 1 are on a different", note);
        Assert.Contains("in the way rather than in the structure", note);
        // The old sentence's promise is gone; the standing fact about the mesh is not.
        Assert.DoesNotContain("not part of L9", note);
        Assert.Contains("NOT MESHED", note);
    }

    /// <summary>
    /// A run whose only ground artwork is on some OTHER plane gets no outline at all, and is told that
    /// rather than being given a size for the wrong conductor.
    /// </summary>
    [Fact]
    public void GroundArtworkOnlyOnAnotherPlane_YieldsNoOutline_AndSaysSo()
    {
        var tech = Tech();
        var c = Conductors(tech);
        var r = PlanarExtractor.Extract(
            [Rect(c[0].DrawingLayers[0], 0, 0, 49.4, 41.3),
             Rect(c[3].DrawingLayers[0], -20, -20, 120, 120)],
            tech, Dbu, maxFrequencyHz: 1.74e9);

        Assert.True(r.Ok, r.Refusal);
        Assert.Null(r.Problem!.GroundOutline);

        string note = Assert.Single(r.Notes, n => n.Contains("ground-designated conductor layer and"));
        _out.WriteLine(note);
        Assert.Contains("None of them is on this run's own return plane", note);
    }

    /// <summary>No ground artwork anywhere: no outline and no sentence about one. An absent pour is not
    /// a measurement of a small plane, and a note on every single-conductor run would be noise.</summary>
    [Fact]
    public void NoGroundArtwork_YieldsNoOutlineAndNoNote()
    {
        var tech = Tech();
        var r = PlanarExtractor.Extract(
            [Rect(Conductors(tech)[0].DrawingLayers[0], 0, 0, 49.4, 41.3)],
            tech, Dbu, maxFrequencyHz: 1.74e9);

        Assert.True(r.Ok, r.Refusal);
        Assert.Null(r.Problem!.GroundOutline);
        Assert.DoesNotContain(r.Notes, n => n.Contains("ground-designated conductor layer and"));
    }

    // ── R-fg-1 — reading it changed nothing ───────────────────────────────────────────────────

    /// <summary>
    /// <b>The outline reaches the problem and reaches nothing else.</b> The conductor levels, the
    /// medium, the vias, the slab and the kernel choice are identical to the same extraction with the
    /// pour absent — which is the whole content of "a described boundary, never artwork".
    /// </summary>
    [Fact]
    public void TheOutlineChangesNoOtherPartOfTheExtractedProblem()
    {
        var tech = Tech();
        var c = Conductors(tech);
        var patch = Rect(c[0].DrawingLayers[0], 0, 0, 49.4, 41.3);
        var pour  = Rect(c[1].DrawingLayers[0], -10, -14, 59.4, 55.3);

        var without = PlanarExtractor.Extract([patch], tech, Dbu, maxFrequencyHz: 1.74e9);
        var with    = PlanarExtractor.Extract([patch, pour], tech, Dbu, maxFrequencyHz: 1.74e9);

        Assert.True(without.Ok, without.Refusal);
        Assert.True(with.Ok, with.Refusal);

        var a = without.Problem!;
        var b = with.Problem!;

        Assert.Null(a.GroundOutline);
        Assert.NotNull(b.GroundOutline);

        // Everything the engine solves with, unchanged — compared FIELD BY FIELD rather than with
        // record equality, which would pass for the wrong reason and fail for another: PlanarProblem's
        // Layers and Vias are arrays, so `Assert.Equal(a, b with { GroundOutline = null })` compares
        // them by reference and can never hold for two independent extractions.
        Assert.Equal(a.RequiresGeneralKernel, b.RequiresGeneralKernel);
        Assert.Equal(a.Slab, b.Slab);
        Assert.Equal(a.MaxFrequencyHz, b.MaxFrequencyHz);
        Assert.Equal(a.EffectiveStack.ToString(), b.EffectiveStack.ToString());
        Assert.Equal(a.GuidedWavelengthM, b.GuidedWavelengthM);
        Assert.Equal(a.ViaList.Count, b.ViaList.Count);
        Assert.Equal(a.MetalBounds(), b.MetalBounds());

        Assert.Equal(a.Layers.Count, b.Layers.Count);
        for (int i = 0; i < a.Layers.Count; i++)
        {
            Assert.Equal(a.Layers[i].Name,       b.Layers[i].Name);
            Assert.Equal(a.Layers[i].ZM,         b.Layers[i].ZM);
            Assert.Equal(a.Layers[i].SigmaSm,    b.Layers[i].SigmaSm);
            Assert.Equal(a.Layers[i].ThicknessM, b.Layers[i].ThicknessM);
            Assert.Equal(a.Layers[i].Polygons.Count, b.Layers[i].Polygons.Count);
            for (int k = 0; k < a.Layers[i].Polygons.Count; k++)
            {
                Assert.Equal(a.Layers[i].Polygons[k].Outer, b.Layers[i].Polygons[k].Outer);
                Assert.Equal(a.Layers[i].Polygons[k].Area(), b.Layers[i].Polygons[k].Area());
            }
        }
        _out.WriteLine($"  problem identical but for GroundOutline; slab {a.Slab}, " +
                       $"{a.Layers.Count} level(s), general kernel = {a.RequiresGeneralKernel}");
    }

    // ── §2's note, end to end through the extraction ──────────────────────────────────────────

    /// <summary>
    /// <b>§2's own number, on the board the antenna series was measured on: 0.406 λ₀.</b> The run's note
    /// states it, states the margin — which is SEVEN times smaller and is the electrically meaningful
    /// one — and states which way every published number is wrong.
    /// </summary>
    [Fact]
    public void TheExtractedOutline_ReportsTheMeasuredBoardsSizeInWavelengths()
    {
        var r = Extract(drawBottom: false);
        Assert.True(r.Ok, r.Refusal);

        var p = r.Problem!;
        var e = PlanarGroundExtent.Of(p.GroundOutline, p.MetalBounds(), 1.74e9);
        Assert.NotNull(e);

        _out.WriteLine($"  box {e.BoxWidthLambda:F4} × {e.BoxHeightLambda:F4} λ₀, " +
                       $"equal-area {e.EquivalentDiameterLambda:F4} λ₀, margin {e.MarginLambda:F4} λ₀");
        Assert.Equal(0.406, e.BoxWidthLambda, 3);
        Assert.True(e.MarginLambda > 0 && e.MarginLambda < 0.07,
                    $"margin {e.MarginLambda:F4} λ₀ is not the 10.3/11.35 mm the fixture draws");

        string note = PlanarGroundExtent.SweepNote(p.GroundOutline, p.MetalBounds(), 1.74e9, 1.74e9)!;
        _out.WriteLine("\n" + note);
        Assert.Contains("0.406", note);
        Assert.Contains("laterally INFINITE", note);
        Assert.Contains("margin is the number to read", note);
    }
}

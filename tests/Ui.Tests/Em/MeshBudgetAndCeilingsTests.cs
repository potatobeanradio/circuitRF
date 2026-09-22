// MIM-13 — the mesh budget, the two ceilings, and the one note that is a warning.
//
// Three things get conflated into "the mesh is bad" and none of them is a bad mesh: the COUNT, WHERE
// the count came from, and WHICH CEILING it is judged against. This file gates one claim each, plus
// the two false sentences the brief's first action turned up.
//
// WHAT THE FIRST ACTION FOUND. The brief asked for the refusal a user actually sees on a spiral with
// the edge mesh on, before deciding whether item 1 was code or wording. Run on the shipped
// three-turn 10/8 µm spiral (examples/PDK PCells), edge mesh on: N = 9,316 over two metal levels and
// one via, REFUSED against the DENSE 5,000 — and the refusal's closing clause read
//
//     "…the accelerated solve would not help either — this mesh is past its 12,000-unknown
//      ceiling too."
//
// about a mesh of 9,316. It is reached whenever `acceleratedWouldFit` is false, which is true BOTH
// when N really is past 12,000 and when the mesh is multi-level — where the wider ceiling is
// withheld by an open owner decision rather than because the accelerator cannot run the mesh (P12
// built PlanarBorderedAimOperator for exactly that class). The same run's other closing clause,
// "matrix compression, which is not built", has been false since M5 shipped AIM.
//
// The spiral itself cannot be the fixture — its PCell cell folder is content-addressed and
// git-ignored, so it does not exist on a fresh clone. The fixture below is the taper this directory
// already uses with an EXPLICIT MediumStack describing the identical medium: that is precisely
// PlanarProblem.RequiresGeneralKernel's own condition, it changes not one cell of the mesh, and it
// isolates the ceiling question from every other difference a second geometry would bring.

using CircuitRF.Engine;
using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Em;
using CircuitRF.Ui.Layout.PCells;

namespace CircuitRF.Ui.Tests.Em;

public class MeshBudgetAndCeilingsTests
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;

    private static PlanarMeshSettings Planar(
        int cellsPerLambda = 10, bool edge = true, int across = 4)
        => new(Auto: false, CellsPerWavelength: cellsPerLambda, EdgeMesh: edge,
               EdgeCells: edge ? 3 : 0, MinCellsAcrossConductor: across,
               BoundaryCells: PlanarBoundaryCells.Staircase);

    /// <summary>The 2026-08-14 taper, the shape this directory's other ceiling gates all use.</summary>
    private static PlanarProblem Taper()
    {
        Assert.True(PCellRegistry.TryGet("MKLOPF", out var gen));
        var tech = StarterTechnologies.Pcb2Layer();
        var shapes = gen(new Dictionary<string, PCellValue>(StringComparer.Ordinal)
        {
            ["Z1"] = PCellValue.Real(7.0),
            ["Z2"] = PCellValue.Real(100),
            ["GammaMax"] = PCellValue.Real(0.05),
            ["L"] = PCellValue.Real(0.030),
            ["Offset"] = PCellValue.Real(0),
            ["SmoothSteps"] = PCellValue.Real(1),
        }, tech, PCellLayerSelection.Default).Shapes;
        var x = PlanarExtractor.Extract(shapes, tech, Dbu, 5e9);
        Assert.True(x.Ok, x.Refusal);
        return x.Problem!;
    }

    /// <summary>A plain rectangle of the given size, one level — the low-rim population.</summary>
    private static PlanarProblem Plate(long sideDbu, double fMaxHz)
    {
        var tech = StarterTechnologies.Pcb2Layer();
        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.Add(new RectShape { Layer = new(1, 0), X1 = 0, Y1 = 0, X2 = sideDbu, Y2 = sideDbu });
        var x = PlanarExtractor.Extract(view.Shapes, tech, Dbu, fMaxHz);
        Assert.True(x.Ok, x.Refusal);
        return x.Problem!;
    }

    /// <summary>A long narrow trace — the mostly-rim population, and a spiral's own cross-section.</summary>
    private static PlanarProblem Trace(long widthDbu, long lengthDbu, double fMaxHz)
    {
        var tech = StarterTechnologies.Pcb2Layer();
        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.Add(new RectShape { Layer = new(1, 0), X1 = 0, Y1 = 0, X2 = widthDbu, Y2 = lengthDbu });
        var x = PlanarExtractor.Extract(view.Shapes, tech, Dbu, fMaxHz);
        Assert.True(x.Ok, x.Refusal);
        return x.Problem!;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Item 1 — the two ceilings, and the sentence that was FALSE about one of them
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>A multi-level mesh that fits UNDER the accelerated ceiling must not be told it is past
    /// it.</b> This is the defect the brief's first action turned up, in one assertion.
    ///
    /// <para>The two fixtures are the same taper and the same 6,543 unknowns — the only difference is
    /// an explicit <c>MediumStack</c>, which is <see cref="PlanarProblem.RequiresGeneralKernel"/>'s
    /// own condition — so the equality below is what proves the mesh is untouched and the WORDING is
    /// the whole of what changed.</para>
    /// </summary>
    [Fact]
    public void AMultiLevelMeshUnderTheAcceleratedCeiling_IsNotToldItIsPastIt()
    {
        var single = Taper();
        var multi  = single with { MediumStack = LayerStack.FromGroundedSlab(single.Slab) };
        Assert.False(single.RequiresGeneralKernel);
        Assert.True(multi.RequiresGeneralKernel);

        var a = SurfaceMesher.Mesh(single, Planar());
        var r = SurfaceMesher.Mesh(multi,  Planar());
        Assert.Equal(a.UnknownCount, r.UnknownCount);          // the mesh is identical; only the ceiling question moved
        Assert.Equal(PlanarBudgetVerdict.Refused, r.Verdict);

        int n = r.UnknownCount;
        Assert.InRange(n, SurfaceMesher.UnknownCeiling + 1, SurfaceMesher.AcceleratedUnknownCeiling);

        string refusal = r.Refusal!;

        // THE FALSE CLAUSE, by the words it was made of. A mesh under 12,000 may not be described as
        // past 12,000, however the sentence is spelled.
        Assert.DoesNotContain("ceiling too", refusal, StringComparison.Ordinal);
        Assert.DoesNotContain("past BOTH", refusal, StringComparison.Ordinal);

        // …and what it says instead: both numbers, the path each governs, and the real reason this
        // mesh does not get the wider one.
        Assert.Contains($"{SurfaceMesher.UnknownCeiling:N0}", refusal, StringComparison.Ordinal);
        Assert.Contains($"{SurfaceMesher.AcceleratedUnknownCeiling:N0}", refusal, StringComparison.Ordinal);
        Assert.Contains("under the accelerated one", refusal, StringComparison.Ordinal);
        Assert.Contains("more than one metal level or a via", refusal, StringComparison.Ordinal);

        // The accelerator RUNS this mesh (P12's bordered operator) and the refusal must not imply
        // otherwise — what it withholds is the ceiling, not the solver.
        Assert.Contains("DOES run a mesh like this one", refusal, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>No ceiling refusal anywhere says matrix compression is not built.</b> It was true before M5
    /// and has not been since; the accelerated refusal was even saying it about a run that had the
    /// accelerator turned ON. All four sites are asked at once because the clause was copied between
    /// them, which is how it survived M5 in the first place.
    /// </summary>
    [Fact]
    public void NoCeilingRefusal_SaysMatrixCompressionIsNotBuilt()
    {
        var single = Taper();
        var multi  = single with { MediumStack = LayerStack.FromGroundedSlab(single.Slab) };

        var messages = new List<string>
        {
            SurfaceMesher.Mesh(single, Planar()).Refusal!,                       // dense, would fit accelerated
            SurfaceMesher.Mesh(multi,  Planar()).Refusal!,                       // dense, multi-level
            SurfaceMesher.Mesh(Plate(40_000_000, 60e9), Planar(20)).Refusal!,    // past BOTH ceilings
            Assert.Throws<InvalidOperationException>(
                () => PlanarSystem.GuardCeiling(SurfaceMesher.UnknownCeiling + 1)).Message,
            Assert.Throws<InvalidOperationException>(
                () => SurfaceMesher.GuardCeiling(SurfaceMesher.UnknownCeiling + 1, accelerated: false)).Message,
            Assert.Throws<InvalidOperationException>(
                () => SurfaceMesher.GuardCeiling(SurfaceMesher.AcceleratedUnknownCeiling + 1, accelerated: true)).Message,
        };

        foreach (string m in messages)
        {
            Assert.All(new[] { "compression, which is not built", "which is not built" },
                       phrase => Assert.DoesNotContain(phrase, m, StringComparison.OrdinalIgnoreCase));

            // …and every one of them names BOTH numbers, so nobody has to infer the scale of an
            // overage from the single ceiling that happened to refuse.
            Assert.Contains($"{SurfaceMesher.UnknownCeiling:N0}", m, StringComparison.Ordinal);
            Assert.Contains($"{SurfaceMesher.AcceleratedUnknownCeiling:N0}", m, StringComparison.Ordinal);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Item 2 — the budget, before the solve
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The budget's bulk/fan split is a MEASUREMENT, and this is what makes it one.</b>
    /// <c>BulkGridLinesX/Y</c> are taken during an edge-meshed run by asking <c>BuildGridLines</c>
    /// for the same grid with grading off; the claim is that this equals the grid an edge-mesh-OFF
    /// run actually produces. An estimate (log_r of the pitch ratio × the attractor count) was the
    /// obvious alternative and is wrong wherever the marcher's rescale or the cap enforcement moved a
    /// line, which on real artwork is most of them.
    ///
    /// <para>The budget itself is asserted here too rather than in a test of its own: it is one
    /// string, and splitting "it exists" from "its numbers are right" gives two tests that fail
    /// together.</para>
    /// </summary>
    [Fact]
    public void TheBudget_SplitsBulkFromFan_AgainstAnActualEdgeMeshOffMesh()
    {
        var p = Taper();
        var on  = SurfaceMesher.Mesh(p, Planar(edge: true));
        var off = SurfaceMesher.Mesh(p, Planar(edge: false));

        Assert.True(on.GridLinesX > on.BulkGridLinesX, "the fixture must actually raise a fan in x");
        Assert.Equal(off.GridLinesX, on.BulkGridLinesX);
        Assert.Equal(off.GridLinesY, on.BulkGridLinesY);

        // With the edge mesh off there is no fan, so the two readings are the same grid.
        Assert.Equal(off.GridLinesX, off.BulkGridLinesX);
        Assert.Equal(off.GridLinesY, off.BulkGridLinesY);

        // It goes out FIRST, and it goes out on an Ok mesh as well as a refused one — seeing the
        // headroom before it runs out is most of why it exists.
        Assert.Equal(on.Budget, on.Notes[0]);
        Assert.Contains($"{SurfaceMesher.UnknownCeiling:N0}", on.Budget, StringComparison.Ordinal);
        Assert.Contains($"{SurfaceMesher.AcceleratedUnknownCeiling:N0}", on.Budget, StringComparison.Ordinal);
        Assert.Contains("edge-fan lines", on.Budget, StringComparison.Ordinal);

        var ok = SurfaceMesher.Mesh(Plate(10_000_000, 10e9), Planar(20, edge: false));
        Assert.Equal(PlanarBudgetVerdict.Ok, ok.Verdict);
        Assert.Equal(ok.Budget, ok.Notes[0]);
        Assert.Contains($"{SurfaceMesher.AcceleratedUnknownCeiling:N0}", ok.Budget, StringComparison.Ordinal);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Item 3 — the edge-mesh-off sentence, promoted where it is the whole answer
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>"loss and Z₀ will read low" is one caveat among thirty on a plate and the whole answer on a
    /// spiral, and the class now says which.</b>
    ///
    /// <para>The two populations are nowhere near the threshold from either side, which is the point:
    /// 10 µm of metal meshed 2 cells across measures <b>100%</b> rim (both cells across it touch an
    /// edge; there is no interior), the shipped three-turn spiral measures the same 100%, and this
    /// directory's wide-to-narrow taper measures <b>5.7%</b>. A threshold anywhere in 0.15–0.85 would
    /// classify both the same way.</para>
    /// </summary>
    [Fact]
    public void TheEdgeMeshOffSentence_IsAWarningOnMostlyRimArtwork_AndANoteOtherwise()
    {
        const string Sentence = "Edge mesh off";

        var rim = SurfaceMesher.Mesh(Trace(10_000, 500_000, 8e9), Planar(20, edge: false, across: 2));
        Assert.True(rim.RimFraction >= SurfaceMesher.MostlyRimFraction,
            $"the mostly-rim fixture measured {rim.RimFraction:P1}");
        var warned = Assert.Single(rim.AllFindings, f => f.Text.StartsWith(Sentence, StringComparison.Ordinal));
        Assert.True(warned.IsWarning, "on mostly-rim artwork this is the Q, not a caveat");
        Assert.Contains("the quantity it reads low is the Q", warned.Text, StringComparison.Ordinal);

        var bulk = SurfaceMesher.Mesh(Taper(), Planar(edge: false));
        Assert.True(bulk.RimFraction < SurfaceMesher.MostlyRimFraction,
            $"the bulk fixture measured {bulk.RimFraction:P1}");
        var noted = Assert.Single(bulk.AllFindings, f => f.Text.StartsWith(Sentence, StringComparison.Ordinal));
        Assert.False(noted.IsWarning);

        // Both quote the fraction: the class is a judgement, the number is what lets a reader make
        // their own on artwork neither fixture resembles.
        Assert.Contains("within one cell of a conductor edge", warned.Text, StringComparison.Ordinal);
        Assert.Contains("within one cell of a conductor edge", noted.Text, StringComparison.Ordinal);

        // …and the class has to SURVIVE the trip out of the mesher, which is what
        // EmRunService/PlanarKernel flattening it to a note undid.
        Assert.Contains(warned.Text, rim.Notes);
    }

}

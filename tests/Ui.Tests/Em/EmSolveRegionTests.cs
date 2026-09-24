// The EM solve region (round-7 designer feedback): a box in the .cem that bounds what is solved.
// One test per claim — the format, the clip, the port refusal, and that the clipped problem is
// smaller and still solves.

using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Tests.Em;

public class EmSolveRegionTests
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;
    private static readonly LayerKey TopCopper = new(1, 0);

    private static long Mm(double mm) => (long)Math.Round(mm * 1000 * Dbu);

    /// <summary>PlanarRunTests' 4 mm line with its two ports, plus a large pour 6 mm away standing in
    /// for "the rest of the board", plus a via beside the pour.</summary>
    private static LayoutView LineAndBoard(bool withVia = true)
    {
        var v = PlanarRunTests.NewLayout();
        v.Shapes.Add(new RectShape { Layer = TopCopper, X1 = Mm(-4), Y1 = Mm(9), X2 = Mm(8), Y2 = Mm(20) });
        if (withVia) v.Shapes.Add(new ViaShape { Layer = new LayerKey(7, 0), X = Mm(10), Y = Mm(10), PadSize = Mm(0.5), DrillSize = Mm(0.3) });
        return v;
    }

    /// <summary>Around the line, clear of the pour: the line's ends sit exactly on the region's
    /// left and right edges, which is where a cut line ends and where its ports are.</summary>
    private static EmSolveRegion AroundTheLine() => EmSolveRegion.FromCorners(0, -1000, 4000, 4000);

    [Fact]
    public void Persistence_RoundTripsTheRegion_AndASetupWithoutOneWritesNoField()
    {
        var setup = PlanarRunTests.NewSetup();
        string without = EmSetupPersistence.Serialize(setup);
        Assert.DoesNotContain("SolveRegion", without);
        Assert.Null(EmSetupPersistence.Deserialize(without).SolveRegion);

        // Corners given swapped come back normalised — a hand-edited file means the box it describes.
        setup.SolveRegion = new EmSolveRegion(4000, 4000, 0, -1000);
        var back = EmSetupPersistence.Deserialize(EmSetupPersistence.Serialize(setup)).SolveRegion;
        Assert.Equal(AroundTheLine(), back);
    }

    [Fact]
    public void Clip_KeepsTheLine_DropsThePourAndTheViaOutside_AndCutsWhatCrossesTheEdge()
    {
        var view = LineAndBoard();
        var flat = view.Shapes.ToList();

        var clip = EmSolveRegionClip.Apply(flat, view.Shapes, AroundTheLine(), Dbu, StarterTechnologies.Pcb2Layer());
        Assert.Null(clip.Refusal);
        Assert.DoesNotContain(clip.Shapes, s => s is ViaShape);
        var rects = clip.Shapes.Where(s => s is RectShape or PolygonShape).ToList();
        Assert.Single(rects);                                   // the line; the pour is gone
        Assert.Contains("2 outside it", clip.Note);             // the pour and the via

        // A region through the middle of the line cuts it at the edge.
        var half = EmSolveRegionClip.Apply(flat, [], EmSolveRegion.FromCorners(0, -1000, 2000, 4000),
                                           Dbu, StarterTechnologies.Pcb2Layer());
        Assert.Contains("1 cut at the region's edge", half.Note);
    }

    [Fact]
    public void APortOutsideTheRegion_IsRefusedByName_BeforeAnythingIsExtracted()
    {
        var setup = PlanarRunTests.NewSetup();
        setup.SolveRegion = EmSolveRegion.FromCorners(0, -1000, 2000, 4000);   // leaves P2 at x = 4 mm out

        var pre = EmRunService.Preflight(setup, PlanarRunTests.Source(LineAndBoard()));

        Assert.False(pre.Ok);
        Assert.Contains("Port 2 ('P2')", pre.Refusal);
        Assert.Contains("outside this EM setup's solve region", pre.Refusal);
    }

    [Fact]
    public void TheClippedProblem_HasFewerCells_AndStillSolves()
    {
        // No via: the whole board's via would be refused by the planar kernel's via-length bound at
        // this frequency, and the comparison needs both sides to mesh.
        var view = LineAndBoard(withVia: false);

        var whole = PlanarRunTests.NewSetup();
        var boxed = PlanarRunTests.NewSetup();
        boxed.SolveRegion = AroundTheLine();

        var wholePre = EmRunService.Preflight(whole, PlanarRunTests.Source(view));
        var boxedPre = EmRunService.Preflight(boxed, PlanarRunTests.Source(view));
        Assert.True(wholePre.Ok, wholePre.Refusal);
        Assert.True(boxedPre.Ok, boxedPre.Refusal);
        var wholeMesh = wholePre.PlanarMesh!;
        var boxedMesh = boxedPre.PlanarMesh!;
        Assert.True(boxedMesh.Mesh.Cells.Count < wholeMesh.Mesh.Cells.Count,
            $"{boxedMesh.Mesh.Cells.Count} cells boxed vs {wholeMesh.Mesh.Cells.Count} whole");

        // And it solves — on Auto, which the boxed line is a uniform cross-section for, so the cheap
        // kernel answers in milliseconds. The note says what the region did.
        boxed.AnalysisKind = EmAnalysisKind.Auto;
        string dir = Path.Combine(Path.GetTempPath(), "crf-solve-region-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var run = EmRunService.Run(boxed, PlanarRunTests.Source(view), Path.Combine(dir, "results"));
            Assert.Equal(EmRunStatus.Ok, run.Status);
            Assert.Contains(run.Notes, n => n.StartsWith("Solve region", StringComparison.Ordinal));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void TheCanvasDrag_HandsTheBoxBack_WithoutEditingTheLayout()
    {
        var view = PlanarRunTests.NewLayout();
        int shapes = view.Shapes.Count;
        var vm = new LayoutEditorViewModel(view);
        (long, long, long, long)? got = null;

        vm.ArmEmRegionPick("board.cem", (a, b, c, d) => got = (a, b, c, d));
        vm.OnPointerPressed(Mm(-1), Mm(-1), Avalonia.Input.KeyModifiers.None);
        vm.OnPointerMoved(Mm(5), Mm(4), leftDown: true, Avalonia.Input.KeyModifiers.None);
        vm.OnPointerReleased(Mm(5), Mm(4), Avalonia.Input.KeyModifiers.None);

        Assert.Equal((Mm(-1), Mm(-1), Mm(5), Mm(4)), got);
        Assert.False(vm.IsPickingEmRegion);
        Assert.Equal(shapes, view.Shapes.Count);
    }
}

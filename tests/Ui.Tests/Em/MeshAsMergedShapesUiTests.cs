// Brief PM-3 Part 3 — "Mesh as Merged Shapes": PM-2's rooftop merging as an ordinary setting, ON by
// default in the product while the engine's own default stays off (EdgePortReturn's pattern).
//
// One test per claim the brief's §3.7 names: a pre-phase .cem gains no byte and reads ON while an
// explicit off survives a round trip and Clone; the provenance hash moves both ways; the control is one
// undo entry plus InvalidateMesh without clearing Auto; it is greyed with a reason, never hidden, under
// the accelerated solve; the overlay draws fewer edges when on and exactly as many internal edges as
// the merge says are solved; and check, the panel and a run read one count.

using CircuitRF.Core.Design;
using CircuitRF.Engine.Mom;
using CircuitRF.Render;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Em;
using SkiaSharp;

namespace CircuitRF.Ui.Tests.Em;

public class MeshAsMergedShapesUiTests
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;
    private static long Mm(double mm) => (long)Math.Round(mm * 1e3 * Dbu);

    /// <summary>RooftopMergeTests' inset patch, drawn: a 10 mm patch fed 3 mm in through two 0.5 mm
    /// slots. The notch corners' fans cross the whole patch, which is what merging is for; a plain
    /// line merges nothing (its cells all sit in a port's end run), so a line cannot test this.</summary>
    private static LayoutView InsetPatchLayout(bool withPort)
    {
        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.Add(new PolygonShape
        {
            Layer = new(1, 0),
            Xy =
            [
                Mm(0), Mm(0),  Mm(4), Mm(0),  Mm(4), Mm(3),  Mm(4.5), Mm(3),  Mm(4.5), Mm(-5),  Mm(5.5), Mm(-5),
                Mm(5.5), Mm(3),  Mm(6), Mm(3),  Mm(6), Mm(0),  Mm(10), Mm(0),  Mm(10), Mm(10),  Mm(0), Mm(10),
            ],
        });
        if (withPort)
            view.Shapes.Add(new LabelShape
            {
                Layer = new(1, 0), X = Mm(5.0), Y = Mm(-5), Text = "P1", Height = Mm(0.4), IsPort = true,
            });
        return view;
    }

    /// <summary>Graded at RooftopMergeTests' settings, so the fixture genuinely merges.</summary>
    private static PlanarMeshSettings Graded(bool merge) =>
        new(Auto: false, CellsPerWavelength: 10, EdgeMesh: true, EdgeCells: 2, MinCellsAcrossConductor: 2,
            RooftopMerging: merge);

    private static LayoutView LineLayout()
    {
        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.Add(new RectShape { Layer = new(1, 0), X1 = 0, Y1 = 0, X2 = 20_000_000, Y2 = 2_900_000 });
        return view;
    }

    private static string TempDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "crf-merged-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    private static EmSetupEditorViewModel Editor(string dir, LayoutView view, EmSetup? seed = null)
    {
        string path  = Path.Combine(dir, "panel.cem");
        // On the shared grid: merged shapes act there, and PMS-7's product default (the local grid) greys them.
        var    setup = seed ?? new EmSetup
        {
            Name = "panel", LayoutRef = "a.clay", AnalysisKind = EmAnalysisKind.Planar,
            PlanarMesh = EmSetup.DefaultPlanarMesh with { LocalGrid = false },
        };
        EmSetupPersistence.SaveToFile(path, setup);
        var vm = new EmSetupEditorViewModel(path, setup)
        {
            ResolveLayout = _ => new EmLayoutSource(
                Path.Combine(dir, "a.clay"), view, StarterTechnologies.Pcb2Layer(), Dbu),
        };
        vm.Refresh();
        return vm;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The .cem — nullable, omitted at the PRODUCT default (on): AdaptiveSampling's polarity
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void APreThisPhaseCem_GainsNoByte_AndReadsON()
    {
        // A file with no PlanarMesh block at all…
        var plain = new EmSetup { Name = "hero", LayoutRef = "Amp/layout/Amp.clay" };
        string before = EmSetupPersistence.Serialize(plain);
        Assert.DoesNotContain("RooftopMerging", before, StringComparison.Ordinal);
        Assert.DoesNotContain("PlanarMesh", before, StringComparison.Ordinal);
        var reloaded = EmSetupPersistence.Deserialize(before);
        Assert.True(reloaded.PlanarMesh.RooftopMerging);
        Assert.Equal(before, EmSetupPersistence.Serialize(reloaded));

        // …and one WITH a PlanarMesh block that pre-dates the key (the test board's own shape): no
        // byte for the merge, reads ON, re-serialises byte-identically.
        var tuned = new EmSetup
        {
            Name = "tl", LayoutRef = "a.clay", AnalysisKind = EmAnalysisKind.Planar,
            PlanarMesh = EmSetup.DefaultPlanarMesh with { CurrentModel = PlanarCurrentModel.TransmissionLine },
        };
        string json = EmSetupPersistence.Serialize(tuned);
        Assert.Contains("PlanarMesh", json, StringComparison.Ordinal);
        Assert.DoesNotContain("RooftopMerging", json, StringComparison.Ordinal);
        var back = EmSetupPersistence.Deserialize(json);
        Assert.True(back.PlanarMesh.RooftopMerging);
        Assert.Equal(json, EmSetupPersistence.Serialize(back));

        // The engine's default is still off; the product's is on, in exactly one place.
        Assert.False(PlanarMeshSettings.Default.RooftopMerging);
        Assert.True(EmSetup.DefaultPlanarMesh.RooftopMerging);
        Assert.True(new EmSetup().PlanarMesh.RooftopMerging);
    }

    [Fact]
    public void AnExplicitOff_IsWritten_RoundTrips_AndSurvivesClone()
    {
        var setup = new EmSetup
        {
            Name = "planar", LayoutRef = "a.clay", AnalysisKind = EmAnalysisKind.Planar,
            PlanarMesh = EmSetup.DefaultPlanarMesh with { RooftopMerging = false },
        };
        string json = EmSetupPersistence.Serialize(setup);
        Assert.Contains("\"RooftopMerging\": false", json, StringComparison.Ordinal);
        Assert.False(EmSetupPersistence.Deserialize(json).PlanarMesh.RooftopMerging);
        Assert.False(setup.Clone().PlanarMesh.RooftopMerging);   // Clone drives the undo snapshots
        Assert.Equal(json, EmSetupPersistence.Serialize(EmSetupPersistence.Deserialize(json)));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Provenance — the hash moves both ways, so a stamped .snp reads as stale once, correctly
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void TogglingIt_ChangesTheMeshHash_BothWays()
    {
        var on  = EmSetup.DefaultPlanarMesh;
        var off = on with { RooftopMerging = false };
        Assert.NotEqual(EmSnpProvenance.MeshHash(on), EmSnpProvenance.MeshHash(off));
        // Off adds no term: it is the engine default's hash (with PMS-7's product stacked regions and local grid,
        // whose terms the product default carries), so a file solved elemental still reads current under an explicit off.
        Assert.Equal(EmSnpProvenance.MeshHash(PlanarMeshSettings.Default with { StackedRegions = true, LocalGrid = true }),
                     EmSnpProvenance.MeshHash(off));
        // …and the other controls still move it, so the term displaced none.
        Assert.NotEqual(EmSnpProvenance.MeshHash(on), EmSnpProvenance.MeshHash(on with { CellsPerWavelength = 40 }));
        Assert.NotEqual(EmSnpProvenance.MeshHash(on), EmSnpProvenance.MeshHash(on with { EdgeCells = 7 }));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The control — one undo entry, InvalidateMesh, Auto untouched; greyed, never hidden
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ChangingIt_IsOneUndoEntry_InvalidatesTheMesh_AndDoesNotClearAuto()
    {
        string dir = TempDir();
        var vm = Editor(dir, LineLayout());
        Assert.True(vm.MeshAsMergedShapes);                 // the product default, loaded from the file
        Assert.True(vm.Working.PlanarMesh.Auto);

        vm.BuildPlanarMesh();
        Assert.NotNull(vm.PlanarMeshReport);
        int undosBefore = 0;
        while (vm.UndoRedo.CanUndo) { vm.UndoRedo.Undo(); undosBefore++; }
        while (vm.UndoRedo.CanRedo) vm.UndoRedo.Redo();
        vm.BuildPlanarMesh();

        vm.MeshAsMergedShapes = false;

        Assert.Null(vm.PlanarMeshReport);                   // the count shown is the SOLVED count
        Assert.False(vm.Working.PlanarMesh.RooftopMerging);
        Assert.True(vm.Working.PlanarMesh.Auto);            // not a resolution: Auto survives

        int undosAfter = 0;
        while (vm.UndoRedo.CanUndo) { vm.UndoRedo.Undo(); undosAfter++; }
        Assert.Equal(undosBefore + 1, undosAfter);
        Assert.True(vm.MeshAsMergedShapes);

        // Same value pushes nothing.
        vm.MeshAsMergedShapes = vm.MeshAsMergedShapes;
        Assert.False(vm.UndoRedo.CanUndo);
        Directory.Delete(dir, true);
    }

    /// <summary>PMS-7 — the local grid already coarsens what merging would join, so while it is on (the product
    /// default) the checkbox is greyed with that reason, and turning it off restores the control.</summary>
    [Fact]
    public void UnderTheLocalGrid_ItIsGreyedWithTheReason()
    {
        string dir = TempDir();
        var vm = Editor(dir, LineLayout());
        Assert.Null(vm.MeshAsMergedShapesDisabledReason);
        vm.PlanarLocalGrid = true;
        Assert.Contains("local grid", vm.MeshAsMergedShapesDisabledReason!, StringComparison.Ordinal);
        Assert.Equal(vm.MeshAsMergedShapesDisabledReason, vm.MeshAsMergedShapesToolTip);
        vm.PlanarLocalGrid = false;
        Assert.Null(vm.MeshAsMergedShapesDisabledReason);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void UnderTheAcceleratedSolve_ItIsGreyedWithTheReason_NeverHidden()
    {
        string dir = TempDir();
        var vm = Editor(dir, LineLayout());
        Assert.Null(vm.MeshAsMergedShapesDisabledReason);
        Assert.DoesNotContain("accelerated", vm.MeshAsMergedShapesToolTip, StringComparison.Ordinal);

        vm.AcceleratedSolve = true;
        string? reason = vm.MeshAsMergedShapesDisabledReason;
        Assert.NotNull(reason);
        Assert.Contains("accelerated", reason!, StringComparison.Ordinal);
        Assert.Equal(reason, vm.MeshAsMergedShapesToolTip);     // the tooltip IS the reason, on the same control
        Assert.True(vm.MeshAsMergedShapes);                     // the stored value is untouched

        // The markup: inside the Surface-mesh group, enabled off the reason, and NOT gated by it —
        // nothing in the row shifts (owner rule: UI controls never shift).
        string axaml = File.ReadAllText(RepoFile("src/Ui/Views/Layout/EmSetupEditorView.axaml"));
        int group = axaml.IndexOf("Text=\"Surface mesh\"", StringComparison.Ordinal);
        int row   = axaml.IndexOf("Name=\"MeshAsMergedShapesCheck\"", StringComparison.Ordinal);
        Assert.True(group >= 0 && row > group, "the checkbox is not inside the Surface mesh group");
        string tag = axaml[axaml.LastIndexOf("<CheckBox", row, StringComparison.Ordinal)..axaml.IndexOf("/>", row, StringComparison.Ordinal)];
        Assert.Contains("MeshAsMergedShapesDisabledReason, Converter={x:Static ObjectConverters.IsNull}", tag);
        Assert.Contains("ToolTip.ShowOnDisabled=\"True\"", tag);
        Assert.DoesNotContain("IsVisible", tag);
        Directory.Delete(dir, true);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The overlay shows what is solved; check, the panel and a run quote one count
    // ══════════════════════════════════════════════════════════════════════════════════════════

    private static (SKSurface Surface, LayoutRenderResult Result) Render(LayoutView view, PlanarMeshReport mesh)
    {
        var bb = Bbox.Empty;
        foreach (var s in view.Shapes) bb = bb.Union(LayoutGeometry.BboxOf(s));
        var vp = LayoutViewport.ZoomToFit(bb, 900, 900, 0.05);
        var surface = SKSurface.Create(new SKImageInfo((int)vp.Width, (int)vp.Height));
        var opts = new LayoutRenderOptions
        {
            Theme = LayoutRenderTheme.Light, ShowGrid = false, ShowPlanarMesh = true, PlanarMesh = mesh,
        };
        return (surface, LayoutRenderer.Draw(surface.Canvas, view, null, vp, opts));
    }

    private static int MeshPixels(SKSurface surface)
    {
        var want = LayoutRenderTheme.Light.PlanarMeshCell;
        using var img = surface.Snapshot();
        using var bmp = SKBitmap.FromImage(img);
        int n = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                if (Math.Abs(c.Red - want.Red) < 45 && Math.Abs(c.Green - want.Green) < 45 && Math.Abs(c.Blue - want.Blue) < 45) n++;
            }
        return n;
    }

    [Fact]
    public void TheOverlay_DrawsOnlyTheSolvedFaces_WhenOn()
    {
        var view = InsetPatchLayout(withPort: false);
        var x = PlanarExtractor.Extract(view.Shapes, StarterTechnologies.Pcb2Layer(), Dbu, 6e9);
        Assert.True(x.Ok, x.Refusal);

        var off = SurfaceMesher.Mesh(x.Problem!, Graded(false));
        var on  = SurfaceMesher.Mesh(x.Problem!, Graded(true));
        var merge = on.Mesh.Merge;
        Assert.NotNull(merge);
        Assert.True(merge!.Count < off.UnknownCount, "the fixture merged nothing");
        Assert.False(LayoutRenderer.WouldDecimatePlanarMesh(on, 900.0 / 15e3));   // the per-edge branch

        // The gate: the internal edges the overlay draws are exactly the unknowns the merge solves.
        Assert.Equal(merge.Count, merge.SolvedFaceCount);
        Assert.Equal(merge.Count, LayoutRenderer.MergedOverlayInternalEdgeCount(on.Mesh, merge));
        Assert.Equal(on.UnknownCount, merge.Count);

        // …and the picture has fewer mesh pixels than the elemental one on the same grid.
        var (sOff, _) = Render(view, off);
        var (sOn,  _) = Render(view, on);
        int pOff = MeshPixels(sOff), pOn = MeshPixels(sOn);
        Assert.True(pOn < pOff, $"merged overlay painted {pOn} mesh pixels against {pOff} elemental");
        // Off: the merge is not attached, so the overlay takes the pre-PM-3 path (AddCell per cell).
        Assert.Null(off.Mesh.Merge);
    }

    [Fact]
    public void Check_ThePanel_AndARun_QuoteTheSameUnknownCount()
    {
        // Preflight is what `check` and the panel's count come out of; a run meshes through the same
        // SurfaceMesher.Mesh with the same setup.PlanarMesh. The report's UnknownCount is the SOLVED
        // count; `check` used to quote Bases.Count (the elemental one) two lines after it quoted
        // UnknownCount in its JSON.
        string dir = TempDir();
        var view = InsetPatchLayout(withPort: true);
        var setup = new EmSetup
        {
            Name = "patch", LayoutRef = "a.clay", AnalysisKind = EmAnalysisKind.Planar,
            Frequency = new FrequencySpec("6", "6", 1, SweepKind.Linear, "GHz", "GHz"),
            PlanarMesh = Graded(true),
        };
        var source = new EmLayoutSource(Path.Combine(dir, "a.clay"), view, StarterTechnologies.Pcb2Layer(), Dbu);
        var pre = EmRunService.Preflight(setup, source);
        Assert.True(pre.Ok, pre.Refusal);
        var report = pre.PlanarMesh!;
        Assert.NotNull(report.Mesh.Merge);
        Assert.Equal(report.Mesh.SolvedUnknownCount, report.UnknownCount);
        Assert.True(report.UnknownCount < report.Mesh.Bases.Count);

        string check = File.ReadAllText(RepoFile("src/Cli/Check.cs"));
        Assert.Contains("CheckEmWouldRun(\n            path, pre.KernelName, pre.PlanarMesh?.UnknownCount ?? 0)", check.Replace("\r\n", "\n"));
        Assert.DoesNotContain("pre.PlanarMesh?.Mesh.Bases.Count", check);
        Directory.Delete(dir, true);
    }

    private static string RepoFile(string relative)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "circuitRF.slnx"))) d = d.Parent;
        Assert.NotNull(d);
        return Path.Combine(d!.FullName, relative);
    }
}

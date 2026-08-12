// Tier M — the mesh button and the mesh overlay (brief-L6-L7-em-ui.md §7).
//
// The pixel oracle here is the load-bearing one: R-em-15 requires the overlay to be absent by
// construction from every export/one-shot render, and "the option defaults to false" is only worth
// something if the renderer actually honours it.

using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Em;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.Theming;
using SkiaSharp;

namespace CircuitRF.Ui.Tests.Em;

public class EmMeshOverlayTests
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;

    private static LayoutView HeroLayout()
    {
        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.Add(new RectShape
        { Layer = new(1, 0), X1 = 0, Y1 = 0, X2 = 20_000_000, Y2 = 2_900_000 });
        return view;
    }

    private static EmMeshReport HeroMesh()
    {
        var r = CrossSectionExtractor.Extract(
            HeroLayout().Shapes, StarterTechnologies.Pcb2Layer(), Dbu, null);
        Assert.True(r.Ok, r.Refusal);
        return new QuasiStaticKernel().Mesh(r.Problem!, EmMeshSettings.Default);
    }

    private static (SKSurface Surface, LayoutRenderResult Result) Render(
        LayoutView view, Technology? tech, bool showEmMesh, EmMeshReport? mesh)
    {
        var bb = Bbox.Empty;
        foreach (var s in view.Shapes) bb = bb.Union(LayoutGeometry.BboxOf(s));
        var vp = LayoutViewport.ZoomToFit(bb, 480, 360, 0.2);

        var surface = SKSurface.Create(new SKImageInfo((int)vp.Width, (int)vp.Height));
        var opts = new LayoutRenderOptions
        {
            Theme = LayoutRenderTheme.Light, ShowGrid = false,
            ShowEmMesh = showEmMesh, EmMesh = mesh,
        };
        var result = LayoutRenderer.Draw(surface.Canvas, view, tech, vp, opts);
        return (surface, result);
    }

    private static int CountMatching(SKSurface surface, Func<SKColor, bool> predicate)
    {
        using var img = surface.Snapshot();
        using var bmp = SKBitmap.FromImage(img);
        int n = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
                if (predicate(bmp.GetPixel(x, y))) n++;
        return n;
    }

    /// <summary>Matches the conductor colour closely. The panel paints an opaque backdrop, so
    /// nothing else in the picture can be this colour — an exact-ish match is safe here and is a
    /// stronger statement than a "red-dominant" heuristic (Top Copper's own fill is orange-brown).</summary>
    private static bool IsMeshConductorish(SKColor c)
    {
        var want = LayoutRenderTheme.Light.EmMeshConductor;
        return Math.Abs(c.Red - want.Red) < 40
            && Math.Abs(c.Green - want.Green) < 40
            && Math.Abs(c.Blue - want.Blue) < 40;
    }

    // ── R-em-14: Mesh computes the mesh only ─────────────────────────────────────────────────

    [Fact]
    public void TheMeshButton_ProducesAReport_AndNoSolve()
    {
        var vm = new EmSetupEditorViewModel(
            Path.Combine(Path.GetTempPath(), "unused.cem"),
            new EmSetup { Name = "x", LayoutRef = "a.clay" })
        {
            ResolveLayout = _ => EmSetupDocumentTests.HeroSource(),
        };
        vm.Refresh();
        Assert.Null(vm.MeshReport);

        vm.BuildMeshCommand.Execute(null);

        Assert.NotNull(vm.MeshReport);
        Assert.InRange(vm.MeshReport!.UnknownCount, 30, 600);
        // D2: the Mesh button never solves. The VM holds no DataSet at all — Simulate is the only
        // path that produces one, which is what makes "never solves" structural rather than a claim.
        Assert.DoesNotContain(typeof(EmSetupEditorViewModel).GetProperties(),
            p => p.PropertyType.Name == "DataSet");
    }

    [Fact]
    public void EveryNumberInTheReport_ReachesThePanelUnmodified()
    {
        // R-em-16: surface the engine's own report verbatim; do not re-word it.
        var vm = new EmSetupEditorViewModel(
            Path.Combine(Path.GetTempPath(), "unused.cem"),
            new EmSetup { Name = "x", LayoutRef = "a.clay" })
        {
            ResolveLayout = _ => EmSetupDocumentTests.HeroSource(),
        };
        vm.Refresh();
        vm.BuildMeshCommand.Execute(null);

        var engineReport = HeroMesh();
        Assert.Equal(engineReport.UnknownCount,         vm.MeshReport!.UnknownCount);
        Assert.Equal(engineReport.MinCellLength,        vm.MeshReport.MinCellLength, 12);
        Assert.Equal(engineReport.MaxCellLength,        vm.MeshReport.MaxCellLength, 12);
        Assert.Equal(engineReport.TruncationHalfExtent, vm.MeshReport.TruncationHalfExtent, 12);
        Assert.Equal(engineReport.Notes, vm.MeshNotes);
    }

    [Fact]
    public void TheSegmentCount_EqualsTheUnknownCount()
    {
        var report = HeroMesh();
        Assert.Equal(report.UnknownCount, report.Mesh.Segments.Count);
    }

    // ── R-em-15: never drawn unless asked, and never reachable by an exporter ────────────────

    [Fact]
    public void WithTheToggleOff_NoMeshPixelIsPainted()
    {
        var view = HeroLayout();
        var tech = StarterTechnologies.Pcb2Layer();
        var mesh = HeroMesh();

        var (offSurface, _) = Render(view, tech, showEmMesh: false, mesh);
        using (offSurface)
            Assert.Equal(0, CountMatching(offSurface, IsMeshConductorish));

        var (onSurface, _) = Render(view, tech, showEmMesh: true, mesh);
        using (onSurface)
            Assert.True(CountMatching(onSurface, IsMeshConductorish) > 20,
                "the overlay must actually paint when it IS asked for — otherwise the off-case " +
                "assertion above passes for the wrong reason");
    }

    [Fact]
    public void AnExportStyleRenderNeverIncludesIt_Structurally()
    {
        // Every export / one-shot render call site constructs LayoutRenderOptions without touching
        // ShowEmMesh, so it is default(bool) = false. That is the SAME by-construction argument
        // R-bmp-3/R-L5g-13 already rest on for the pin overlay, so pin it the same way.
        var opts = new LayoutRenderOptions { Theme = LayoutRenderTheme.Light };
        Assert.False(opts.ShowEmMesh);
        Assert.Null(opts.EmMesh);
    }

    [Fact]
    public void TheOverlayContributesToNoGeometryCounter()
    {
        var view = HeroLayout();
        var tech = StarterTechnologies.Pcb2Layer();
        var mesh = HeroMesh();

        var (offSurface, off) = Render(view, tech, showEmMesh: false, mesh);
        var (onSurface,  on)  = Render(view, tech, showEmMesh: true,  mesh);
        using (offSurface) using (onSurface)
        {
            Assert.Equal(off.ShapesExamined,   on.ShapesExamined);
            Assert.Equal(off.ShapesDrawn,      on.ShapesDrawn);
            Assert.Equal(off.PathsConstructed, on.PathsConstructed);
            Assert.Equal(off.DrawCalls,        on.DrawCalls);
            Assert.Equal(off.LayersVisited,    on.LayersVisited);
        }
    }

    [Fact]
    public void BothSegmentKinds_AreDrawnInDifferentColours()
    {
        // They are different unknowns — free vs. bound charge — and a user reading a mesh needs to
        // see which is which (R-em-15).
        var mesh = HeroMesh();
        Assert.Contains(mesh.Mesh.Segments, s => s.Kind == EmSegmentKind.Conductor);
        Assert.Contains(mesh.Mesh.Segments, s => s.Kind == EmSegmentKind.DielectricInterface);
        Assert.NotEqual(LayoutRenderTheme.Light.EmMeshConductor, LayoutRenderTheme.Light.EmMeshInterface);
        Assert.NotEqual(LayoutRenderTheme.Dark.EmMeshConductor,  LayoutRenderTheme.Dark.EmMeshInterface);

        var view = HeroLayout();
        var (surface, _) = Render(view, StarterTechnologies.Pcb2Layer(), showEmMesh: true, mesh);
        using (surface)
        {
            var want = LayoutRenderTheme.Light.EmMeshInterface;
            int interfacePixels = CountMatching(surface, c =>
                Math.Abs(c.Red - want.Red) < 40 && Math.Abs(c.Green - want.Green) < 40
                                                && Math.Abs(c.Blue - want.Blue) < 40);
            Assert.True(interfacePixels > 20, "dielectric-interface segments must paint too");
        }
    }

    // ── R-em-17: an edited layout CLEARS the displayed mesh ──────────────────────────────────

    [Fact]
    public void EditingTheLayout_ClearsTheDisplayedMesh_RatherThanLeavingItStale()
    {
        var view = HeroLayout();
        var vm = new LayoutEditorViewModel(view) { EmMeshReport = HeroMesh() };
        Assert.NotNull(vm.EmMeshReport);

        view.Shapes.Add(new RectShape
        { Layer = new(1, 0), X1 = 0, Y1 = 5_000_000, X2 = 1_000_000, Y2 = 6_000_000 });
        view.NotifyChanged();

        Assert.Null(vm.EmMeshReport);
    }

    [Fact]
    public void ChangingAMeshSetting_InvalidatesTheDisplayedMesh()
    {
        var vm = new EmSetupEditorViewModel(
            Path.Combine(Path.GetTempPath(), "unused.cem"),
            new EmSetup { Name = "x", LayoutRef = "a.clay" })
        {
            ResolveLayout = _ => EmSetupDocumentTests.HeroSource(),
        };
        vm.Refresh();
        vm.BuildMeshCommand.Execute(null);
        Assert.NotNull(vm.MeshReport);

        vm.TruncationHeightsText = "30";
        vm.CommitMeshField(nameof(EmMeshSettings.TruncationHeights));

        Assert.Null(vm.MeshReport);
    }
}

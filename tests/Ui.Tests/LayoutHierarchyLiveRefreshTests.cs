using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.Schematic;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests;

// ──────────────────────────────────────────────────────────────────────────────
//  Phase L3b — R-L3b-1: editing a sub-cell invalidates the cached instance geometry in every open
//  layout that references it. This is the brief's own "headline test" (§gate 5): with a parent open
//  showing an instance of a sub-cell, edit the sub-cell and assert the parent's rendering changes
//  WITHOUT a reload — a stale cache renders the OLD geometry convincingly, so nothing else catches it.
// ──────────────────────────────────────────────────────────────────────────────

[Collection(LayoutTextOutlineTypefaceCollection.Name)]
public sealed class LayoutHierarchyLiveRefreshTests : IDisposable
{
    private readonly string _workspaceDir;
    private static readonly LayerKey LayerA = new(1, 0);

    public LayoutHierarchyLiveRefreshTests()
    {
        _workspaceDir = Path.Combine(Path.GetTempPath(), "crfHierLiveRefreshTest_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_workspaceDir);
        CellLayoutResolver.InvalidateUnder(_workspaceDir);
        LayoutTextOutline.TestOverrideTypeface = SKTypeface.Default;
    }

    public void Dispose()
    {
        LayoutTextOutline.TestOverrideTypeface = null;
        CellLayoutResolver.InvalidateUnder(_workspaceDir);
        if (Directory.Exists(_workspaceDir))
            Directory.Delete(_workspaceDir, recursive: true);
    }

    private static Technology MakeTech() => new()
    {
        Name = "Test", DefaultDisplayUnit = LayoutUnit.Um, DefaultSnapDbu = 1000,
        Layers = [new LayerDef { Key = LayerA, Name = "L1", Color = new CircuitRF.Ui.Theming.Rgba(255, 0, 0), FillOpacity = 1.0, ZOrder = 0, Visible = true, Selectable = true }],
    };

    private static LayoutView MakeView() => new() { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000 };

    private string CreateCell(string name, Action<LayoutView> populate)
    {
        var cellDir = CellFolder.CreateCellFolder(_workspaceDir, name);
        string layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        var view = MakeView();
        populate(view);
        LayoutPersistence.SaveToFile(Path.Combine(layoutDir, "main.clay"), view);
        return Path.Combine(layoutDir, "main.clay");
    }

    private static byte[] RenderPixels(LayoutView view, Technology tech, LayoutViewport vp, string baseDir)
    {
        using var surface = SKSurface.Create(new SKImageInfo((int)vp.Width, (int)vp.Height));
        var opts = new LayoutRenderOptions { Theme = LayoutRenderTheme.Light, ShowGrid = false, BaseDir = baseDir };
        LayoutRenderer.Draw(surface.Canvas, view, tech, vp, opts);
        using var img = surface.Snapshot();
        using var bmp = SKBitmap.FromImage(img);
        return bmp.Bytes;
    }

    [Fact]
    public void EditingSubCell_ThroughSetLivePlusCacheEviction_ChangesParentRenderingWithNoReload()
    {
        var leafClayPath = CreateCell("Leaf", v =>
            v.Shapes.Add(new RectShape { Layer = LayerA, X1 = 0, Y1 = 0, X2 = 100, Y2 = 100 }));
        var tech = MakeTech();

        var parent = MakeView();
        parent.Instances.Add(new LayoutInstance { CellRef = "Leaf", X = 0, Y = 0, Mag = 1.0 });
        var vp = new LayoutViewport(-200, -200, 1.0, 400, 400);

        // Frame 1: parent renders the leaf's ORIGINAL 100x100 rect (compiles+caches it).
        var pixelsBefore = RenderPixels(parent, tech, vp, _workspaceDir);

        // Get the EXACT LayoutView reference the resolver/renderer are using for "Leaf" (a real
        // push-in session would be this SAME reference — mutated in place across edits, never
        // swapped for a new object, which is exactly the case the compiled-geometry cache does not
        // self-heal from without an explicit eviction).
        var liveView = CellLayoutResolver.Resolve("Leaf", _workspaceDir).View!;
        Assert.Single(liveView.Shapes);

        // Simulate an in-session edit: grow the rect in place (same LayoutView object, same Shapes
        // list — exactly how a real AddInstanceCommand/ReplaceShapeCommand mutates a session's model).
        ((RectShape)liveView.Shapes[0]).X2 = 300;
        ((RectShape)liveView.Shapes[0]).Y2 = 300;

        // The production path for "an open sub-cell session just edited itself": SetLive (bumps
        // Generation, fires LiveViewChanged) + InvalidateCompiledGeometry (evicts the renderer's
        // stale per-cell compiled paths for this exact, still-same, reference).
        CellLayoutResolver.SetLive(leafClayPath, liveView);
        LayoutRenderer.InvalidateCompiledGeometry(liveView);

        // Frame 2: SAME parent object, SAME instance, NO reload of anything — must show the grown rect.
        var pixelsAfter = RenderPixels(parent, tech, vp, _workspaceDir);

        Assert.NotEqual(pixelsBefore, pixelsAfter);

        // And it must match a layout that was DIRECTLY built with the grown rect from the start —
        // proving the new pixels are the CORRECT new geometry, not just "different, somehow".
        var directParent = MakeView();
        directParent.Instances.Add(new LayoutInstance { CellRef = "Leaf", X = 0, Y = 0, Mag = 1.0 });
        var directLeafPath = CreateCell("LeafGrown", v =>
            v.Shapes.Add(new RectShape { Layer = LayerA, X1 = 0, Y1 = 0, X2 = 300, Y2 = 300 }));
        directParent.Instances[0].CellRef = "LeafGrown";
        var pixelsDirect = RenderPixels(directParent, tech, vp, _workspaceDir);

        Assert.Equal(pixelsDirect, pixelsAfter);
        _ = directLeafPath;
    }

    [Fact]
    public void WithoutCacheEviction_SameReferenceMutatedInPlace_RendersStale_ProvingEvictionIsNecessary()
    {
        // The negative control: SetLive alone (no InvalidateCompiledGeometry) does NOT fix a
        // same-reference in-place mutation — this is exactly the gap the L3a research flagged and
        // R-L3b-1's second half exists to close. If this test ever starts failing (i.e. stale
        // rendering stops happening without eviction), the renderer's compile-cache identity or
        // CellLayoutResolver's live-reference behaviour changed — re-examine whether
        // InvalidateCompiledGeometry is still needed before deleting this test.
        var leafClayPath = CreateCell("Leaf", v =>
            v.Shapes.Add(new RectShape { Layer = LayerA, X1 = 0, Y1 = 0, X2 = 100, Y2 = 100 }));
        var tech = MakeTech();

        var parent = MakeView();
        parent.Instances.Add(new LayoutInstance { CellRef = "Leaf", X = 0, Y = 0, Mag = 1.0 });
        var vp = new LayoutViewport(-200, -200, 1.0, 400, 400);

        var pixelsBefore = RenderPixels(parent, tech, vp, _workspaceDir);
        var liveView = CellLayoutResolver.Resolve("Leaf", _workspaceDir).View!;

        ((RectShape)liveView.Shapes[0]).X2 = 300;
        ((RectShape)liveView.Shapes[0]).Y2 = 300;
        CellLayoutResolver.SetLive(leafClayPath, liveView);
        // Deliberately NOT calling LayoutRenderer.InvalidateCompiledGeometry here.

        var pixelsAfterNoEviction = RenderPixels(parent, tech, vp, _workspaceDir);

        Assert.Equal(pixelsBefore, pixelsAfterNoEviction);   // stale — proves eviction is load-bearing
    }

    // ── R-L3b-1's L2b spatial-index half — "a stale bbox means an instance that culls wrongly or
    //    cannot be clicked" ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GrowingSubCellExtent_GrowsTheInstancesQueriedBbox_ViaGenerationAlone_NoExplicitReindex()
    {
        CreateCell("Leaf", v => v.Shapes.Add(new RectShape { Layer = LayerA, X1 = 0, Y1 = 0, X2 = 100, Y2 = 100 }));

        var parent = MakeView();
        parent.Instances.Add(new LayoutInstance { CellRef = "Leaf", X = 0, Y = 0, Mag = 1.0 });
        var index = new LayoutSpatialIndex();

        Bbox InstanceBboxOf(LayoutInstance inst) => CellHierarchy.InstanceBbox(inst, _workspaceDir);

        // Query 1: a small rect just around the instance's ORIGINAL (100x100) extent — hits.
        var smallQueryRect = new Bbox(0, 0, 150, 150);
        var hits1 = index.QueryIntersecting(
            parent.Shapes, parent.Instances, InstanceBboxOf, CellLayoutResolver.Generation, smallQueryRect);
        Assert.Contains(hits1, e => e.Kind == SpatialEntryKind.Instance && e.Index == 0);

        // A query FAR outside the original extent but inside where it will grow to — currently misses.
        var farQueryRect = new Bbox(250, 250, 260, 260);
        var missBefore = index.QueryIntersecting(
            parent.Shapes, parent.Instances, InstanceBboxOf, CellLayoutResolver.Generation, farQueryRect);
        Assert.DoesNotContain(missBefore, e => e.Kind == SpatialEntryKind.Instance && e.Index == 0);

        // In-session edit: the sub-cell's rect grows to 300x300 — via SetLive, no other change to
        // the parent's own Instances list at all (still one instance, unchanged fields).
        var liveView = CellLayoutResolver.Resolve("Leaf", _workspaceDir).View!;
        ((RectShape)liveView.Shapes[0]).X2 = 300;
        ((RectShape)liveView.Shapes[0]).Y2 = 300;
        CellLayoutResolver.SetLive(Path.Combine(CellFolder.SubFolderPath(
            Path.Combine(_workspaceDir, "Leaf"), ViewType.Layout), "main.clay"), liveView);

        // Same query, same index instance, same Instances list reference — the ONLY thing that
        // changed is CellLayoutResolver.Generation (passed fresh below): the previously-missed point
        // now hits, proving the index re-derived the instance's bbox from the grown sub-cell.
        var hitsAfter = index.QueryIntersecting(
            parent.Shapes, parent.Instances, InstanceBboxOf, CellLayoutResolver.Generation, farQueryRect);
        Assert.Contains(hitsAfter, e => e.Kind == SpatialEntryKind.Instance && e.Index == 0);
    }

    // ── The negative control for the test above, and the reason it needs one ───────────────────
    //
    // A sub-cell's own shapes are MEASURED once per resolved view and memoized on that view's
    // reference (CellHierarchy.InvalidateShapesBbox is the eviction), because InstanceBbox is called
    // per placement per FRAME to size, cull and LOD each one: a generated cell holding a six-figure
    // via field, placed a couple of dozen times, was otherwise re-unioning millions of rectangles
    // every frame of every pan.
    //
    // That memo is what SetLive above has to evict, and this is what proves the memo is really there:
    // the identical in-place edit, with nothing published, leaves the measurement exactly as it was.
    // Delete the eviction from SetLive and this test still passes while the one above goes red —
    // which is the pair working as intended.

    [Fact]
    public void MutatingALiveSubCellWithoutPublishingIt_LeavesTheInstanceBboxAsItWas()
    {
        CreateCell("Leaf", v => v.Shapes.Add(new RectShape { Layer = LayerA, X1 = 0, Y1 = 0, X2 = 100, Y2 = 100 }));

        var inst = new LayoutInstance { CellRef = "Leaf", X = 0, Y = 0, Mag = 1.0 };
        var before = CellHierarchy.InstanceBbox(inst, _workspaceDir);
        Assert.Equal(100, before.MaxX);

        var liveView = CellLayoutResolver.Resolve("Leaf", _workspaceDir).View!;
        ((RectShape)liveView.Shapes[0]).X2 = 300;
        ((RectShape)liveView.Shapes[0]).Y2 = 300;
        // Deliberately NOT calling CellLayoutResolver.SetLive here.

        Assert.Equal(before, CellHierarchy.InstanceBbox(inst, _workspaceDir));
    }
}

using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Renderers;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests;

// ──────────────────────────────────────────────────────────────────────────────
//  RF1 — a flat document must not pay to be told it has no instances.
//  docs/sonnet-briefs/brief-rasterfill-1-instance-query-on-flat-documents.md
//
//  LayoutRenderer.Draw queried the spatial index TWICE per frame. The second call is the combined
//  overload, which returns every entry in the rect — SHAPES INCLUDED — into a list that was never
//  pre-sized and then sorted the lot. On an imported board (52,230 shapes, zero instances, which is
//  the normal shape of a Gerber import) that second traversal built and threw away a 51,378-entry
//  list every frame, 1,026 KB of it, to produce the number zero.
//
//  Per the repo's standing rule, NOT ONE of these asserts a millisecond figure — a timing gate
//  measures the machine and flakes. Each asserts the structural property that produced the time:
//  the query count, the eviction the naive guard would have broken, the untouched instance path,
//  the frame-one extent, the allocation counter, and the ordering contract.
// ──────────────────────────────────────────────────────────────────────────────

// CellStatGlobalsCollection, not the typeface one: these fixtures carry no LabelShape (so nothing
// here touches LayoutTextOutline), but every render below RESOLVES placed cells and each test
// invalidates the resolver — which is the traffic that collection exists to serialize. Left outside
// it, this class is one more concurrent stat source for the classes that ASSERT an exact
// CellStat.Calls, and BrokenInstanceVisibilityTests' own note records what that looks like.
[Collection(CellStatGlobalsCollection.Name)]
public sealed class LayoutFlatDocumentInstanceQueryTests : IDisposable
{
    private readonly string _workspaceDir;
    private static readonly LayerKey LayerA = new(1, 0);

    public LayoutFlatDocumentInstanceQueryTests()
    {
        _workspaceDir = Path.Combine(Path.GetTempPath(), "crfRf1Test_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_workspaceDir);
        CellLayoutResolver.InvalidateUnder(_workspaceDir);
    }

    public void Dispose()
    {
        CellLayoutResolver.InvalidateUnder(_workspaceDir);
        if (Directory.Exists(_workspaceDir)) Directory.Delete(_workspaceDir, recursive: true);
    }

    private static Technology MakeTech() => new()
    {
        Name = "Test", DefaultDisplayUnit = LayoutUnit.Um, DefaultSnapDbu = 1000,
        Layers =
        [
            new LayerDef
            {
                Key = LayerA, Name = "L1", Color = new CircuitRF.Design.Theming.Rgba(255, 0, 0),
                FillOpacity = 1.0, ZOrder = 0, Visible = true, Selectable = true,
            },
        ],
    };

    private static LayoutView MakeView() => new() { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000 };

    /// <summary>A flat document of disjoint squares — no instances, which is what an imported board is.</summary>
    private static LayoutView FlatView(int side, long pitch = 4000, long size = 2000)
    {
        var v = MakeView();
        for (int r = 0; r < side; r++)
        for (int c = 0; c < side; c++)
            v.Shapes.Add(new RectShape
            {
                Layer = LayerA,
                X1 = c * pitch, Y1 = r * pitch, X2 = c * pitch + size, Y2 = r * pitch + size,
            });
        return v;
    }

    private string CreateCell(string name, Action<LayoutView> populate)
    {
        var cellDir = CellFolder.CreateCellFolder(_workspaceDir, name);
        var view = MakeView();
        populate(view);
        LayoutPersistence.SaveToFile(Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Layout), "main.clay"), view);
        return cellDir;
    }

    private LayoutInstance InstanceOf(string cellDir, long x = 0, long y = 0) => new()
    {
        CellRef = Path.GetRelativePath(_workspaceDir, cellDir),
        X = x, Y = y, Rot = LayoutRotation.R0, Mag = 1, Rows = 1, Cols = 1,
    };

    private static LayoutRenderResult Render(LayoutView view, Technology tech, LayoutViewport vp, string? baseDir,
                                             out SKColor[] pixels)
    {
        using var surface = SKSurface.Create(new SKImageInfo((int)vp.Width, (int)vp.Height));
        var opts = new LayoutRenderOptions { Theme = LayoutRenderTheme.Light, ShowGrid = false, BaseDir = baseDir };
        var result = LayoutRenderer.Draw(surface.Canvas, view, tech, vp, opts);
        using var img = surface.Snapshot();
        using var bmp = SKBitmap.FromImage(img);
        pixels = bmp.Pixels;
        return result;
    }

    private static LayoutRenderResult Render(LayoutView view, Technology tech, LayoutViewport vp, string? baseDir) =>
        Render(view, tech, vp, baseDir, out _);

    private static readonly Bbox Everywhere = new(-10_000_000, -10_000_000, 10_000_000, 10_000_000);

    private static LayoutViewport Fit(LayoutView view, int w = 400, int h = 400)
    {
        var extent = DocumentExtents.LayoutBox(view, null, "");
        return LayoutViewport.ZoomToFit(extent.IsEmpty ? new Bbox(0, 0, 100_000, 100_000) : extent, w, h, marginFrac: 0.05);
    }

    // ── Gate 1 — the whole brief in one assertion ──────────────────────────────────────────────
    //
    // A document with shapes and no instances issues exactly ONE spatial query per frame. Asserted
    // on the index's own call counters, never on a clock.

    [Fact]
    public void FlatDocument_SteadyStateFrame_IssuesExactlyOneSpatialQuery()
    {
        var view = FlatView(40);            // 1,600 shapes, zero instances
        var tech = MakeTech();
        var vp = Fit(view);

        // The FIRST frame legitimately makes both calls: the index has never refreshed its instance
        // side, so it cannot yet agree that the side is empty. That is the state the guard requires
        // and it is reached by asking once.
        Render(view, tech, vp, _workspaceDir);
        Assert.Equal(1, view.SpatialIndex.CombinedQueryCount);

        int shapeQueriesBefore = view.SpatialIndex.ShapeQueryCount;
        Render(view, tech, vp, _workspaceDir);

        Assert.Equal(1, view.SpatialIndex.ShapeQueryCount - shapeQueriesBefore);
        Assert.Equal(1, view.SpatialIndex.CombinedQueryCount);   // still 1 — the second frame skipped it
    }

    // ── Gate 2 — written BEFORE the guard, because it is the test the naive guard fails ────────
    //
    // `view.Instances.Count == 0` alone is NOT a sufficient condition to skip. A document whose last
    // instance was just deleted has an empty live list and a DIRTY index, and a one-part guard would
    // skip the refresh that evicts the entry — leaving it in the tree to be drawn and to widen
    // Extent. The failure is silent and appears only on the frame after the delete.

    [Fact]
    public void LastInstanceDeleted_IsEvictedOnThatVeryFrame()
    {
        // The placed cell sits far from the top-level shapes, so its entry is separable in Extent.
        var cellDir = CreateCell("Placed", v => v.Shapes.Add(new RectShape
        {
            Layer = LayerA, X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000,
        }));

        var view = MakeView();
        view.Shapes.Add(new RectShape { Layer = LayerA, X1 = 0, Y1 = 0, X2 = 5_000, Y2 = 5_000 });
        view.Instances.Add(InstanceOf(cellDir, x: 500_000, y: 500_000));

        var tech = MakeTech();
        var vp = new LayoutViewport(-10_000, -10_000, 400.0 / 600_000, 400, 400);

        var withInstance = Render(view, tech, vp, _workspaceDir);
        Assert.Equal(1, withInstance.InstancesExamined);
        Assert.True(withInstance.InstancesDrawn > 0, "the instance was never drawn, so its removal proves nothing.");

        Assert.True(view.SpatialIndex.Extent.MaxX >= 500_000,
            "the instance's entry was never in the index to begin with.");

        // Delete the last instance — exactly the state the one-part guard mishandles.
        view.Instances.Clear();
        view.NotifyChanged(LayoutChangeInfo.InstancesOnly);

        int refreshesBefore = view.SpatialIndex.InstanceRefreshCount;
        var afterDelete = Render(view, tech, vp, _workspaceDir);

        Assert.Equal(0, afterDelete.InstancesExamined);
        Assert.Equal(0, afterDelete.InstancesDrawn);

        // ── THIS is the assertion the one-part guard fails ────────────────────────────────────
        // `view.Instances.Count == 0` is true here, so a naive guard skips — and skipping means the
        // index is never told, so the entry the deleted placement owned is never evicted and
        // _instancesDirty stays set indefinitely. The two-part guard asks the index instead, the
        // index says "dirty", the query runs, and RefreshInstances does the eviction on THIS frame.
        //
        // Note what does NOT discriminate, because both were tried: InstancesDrawn is 0 either way
        // (the renderer draws what the query returned, and the naive guard returns nothing), and
        // querying the index directly to look for the stale entry REPAIRS it — the combined query
        // refreshes on the way in, so the probe destroys the state it came to observe.
        Assert.Equal(refreshesBefore + 1, view.SpatialIndex.InstanceRefreshCount);

        // …and with the eviction already done, asking directly is now a true observation rather than
        // a repair: the live list is empty and the index is synced to empty, so this refreshes
        // nothing and what it returns IS the stored state.
        var stillIndexed = view.SpatialIndex
            .QueryIntersecting(view.Shapes, view.Instances, _ => Bbox.Empty, CellLayoutResolver.Generation, Everywhere)
            .Where(e => e.Kind == SpatialEntryKind.Instance)
            .ToList();
        Assert.Empty(stillIndexed);

        // NOTE — the brief asks here for "Extent must not include it", and that is NOT this index's
        // behaviour, before RF1 or after: RemoveEntry deliberately never shrinks an ancestor node's
        // bounds (LayoutSpatialIndex.cs, "no bounds-shrinking / rebalancing here"), because an
        // over-large bbox costs query efficiency and never correctness, and R-L2b-2's churn-triggered
        // rebuild is the backstop. Extent therefore still spans the deleted placement until a
        // rebuild, and asserting otherwise fails identically with this guard reverted — verified.
        // What the guard actually has to protect is the EVICTION above.

        // And only NOW may the frame stop asking: the index has been refreshed to an empty,
        // clean instance side.
        int combinedBefore = view.SpatialIndex.CombinedQueryCount;
        Render(view, tech, vp, _workspaceDir);
        Assert.Equal(combinedBefore, view.SpatialIndex.CombinedQueryCount);
    }

    // ── Gate 3 — a document that HAS instances is untouched ────────────────────────────────────

    [Fact]
    public void DocumentWithInstances_StillQueries_AndItsCountersAreUnchanged()
    {
        var cellDir = CreateCell("Placed", v => v.Shapes.Add(new RectShape
        {
            Layer = LayerA, X1 = 0, Y1 = 0, X2 = 8_000, Y2 = 8_000,
        }));

        var view = MakeView();
        view.Shapes.Add(new RectShape { Layer = LayerA, X1 = 0, Y1 = 0, X2 = 4_000, Y2 = 4_000 });
        view.Instances.Add(InstanceOf(cellDir, x: 20_000, y: 0));
        view.Instances.Add(new LayoutInstance                       // …including an array
        {
            CellRef = Path.GetRelativePath(_workspaceDir, cellDir),
            X = 60_000, Y = 0, Rot = LayoutRotation.R0, Mag = 1,
            Rows = 2, Cols = 3, PitchX = 12_000, PitchY = 12_000,
        });

        var tech = MakeTech();
        var vp = new LayoutViewport(-5_000, -5_000, 400.0 / 140_000, 400, 400);

        var first = Render(view, tech, vp, _workspaceDir, out var firstPixels);
        int combinedAfterFirst = view.SpatialIndex.CombinedQueryCount;

        var second = Render(view, tech, vp, _workspaceDir, out var secondPixels);

        // The guard must never fire here — the query is still made on every frame.
        Assert.False(view.SpatialIndex.InstanceSideIsEmptyAndClean(view.Instances, CellLayoutResolver.Generation));
        Assert.Equal(combinedAfterFirst + 1, view.SpatialIndex.CombinedQueryCount);

        Assert.Equal(2, first.InstancesExamined);
        Assert.Equal(first.InstancesExamined, second.InstancesExamined);
        Assert.Equal(first.InstancesDrawn, second.InstancesDrawn);
        Assert.True(first.InstancesDrawn > 0, "nothing was drawn, so this compares two empty frames.");
        Assert.Equal(firstPixels, secondPixels);
    }

    // ── Gate 4 — the flicker regression §3 names ───────────────────────────────────────────────
    //
    // A schematic-generated layout has NO top-level shapes at all: every pixel is in a placed cell.
    // Queried below the layer loop, frame one asked SpatialIndex.Extent before anything had put the
    // instances into it, got "empty", and decided CanAffordOutlines differently from every frame
    // after. Two successive renders of one viewport must be pixel-identical.

    [Fact]
    public void LayoutWhoseGeometryIsAllInPlacedCells_RendersFrameOneIdenticallyToFrameTwo()
    {
        var cellDir = CreateCell("Sub", v =>
        {
            for (int i = 0; i < 40; i++)
                v.Shapes.Add(new RectShape
                {
                    Layer = LayerA, X1 = i * 1_000, Y1 = 0, X2 = i * 1_000 + 600, Y2 = 3_000,
                });
        });

        var view = MakeView();                       // deliberately NO top-level shapes
        view.Instances.Add(InstanceOf(cellDir));
        Assert.Empty(view.Shapes);

        var tech = MakeTech();
        var vp = new LayoutViewport(-2_000, -2_000, 400.0 / 46_000, 400, 400);

        Render(view, tech, vp, _workspaceDir, out var frameOne);
        Render(view, tech, vp, _workspaceDir, out var frameTwo);

        Assert.Equal(frameOne, frameTwo);
    }

    // ── Gate 5 — allocation, as a counter and not a clock ──────────────────────────────────────
    //
    // Allocation is deterministic in a way wall-clock is not, which is the whole reason this one is
    // safe to pin. MEASURED on this fixture at the time of writing: 1.02 MB per steady-state frame
    // before RF1 and 0.72 MB after, so the ceiling below carries better than 2x headroom over the
    // measured figure while still sitting well under the pre-RF1 cost. It is a regression tripwire,
    // not a target.

    [Fact]
    public void FlatDocument_SteadyStateFrame_StaysUnderItsAllocationCeiling()
    {
        var view = FlatView(60);            // 3,600 shapes, zero instances
        var tech = MakeTech();
        var vp = Fit(view);

        using var surface = SKSurface.Create(new SKImageInfo((int)vp.Width, (int)vp.Height));
        var opts = new LayoutRenderOptions { Theme = LayoutRenderTheme.Light, ShowGrid = false, BaseDir = _workspaceDir };

        // Warm: the first frames build the index, the path cache and every one-time JIT'd path.
        for (int i = 0; i < 3; i++) LayoutRenderer.Draw(surface.Canvas, view, tech, vp, opts);

        const int Frames = 5;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Frames; i++) LayoutRenderer.Draw(surface.Canvas, view, tech, vp, opts);
        long perFrame = (GC.GetAllocatedBytesForCurrentThread() - before) / Frames;

        const long CeilingBytes = 1_600_000;
        Assert.True(perFrame < CeilingBytes,
            $"a steady-state frame allocated {perFrame:N0} bytes, over the {CeilingBytes:N0} ceiling. " +
            "Something on the per-frame path started allocating per candidate again (RF1).");
    }

    // ── Gate 6 — the ordering contract survives the pre-sizing ─────────────────────────────────
    //
    // Ascending Index, ties broken by Kind. Consumers rely on it; R-rf1-3 changed how the result
    // list is sized and R-rf1-4 was measured and rejected, so the order must be re-proved either way.

    [Theory]
    [InlineData(7)]
    [InlineData(1234)]
    [InlineData(99999)]
    public void CombinedQuery_ResultIsOrderedByIndexThenKind(int seed)
    {
        var rng = new Random(seed);
        var view = MakeView();

        for (int i = 0; i < 250; i++)
        {
            long x = rng.Next(0, 200_000), y = rng.Next(0, 200_000);
            view.Shapes.Add(new RectShape { Layer = LayerA, X1 = x, Y1 = y, X2 = x + 3_000, Y2 = y + 3_000 });
        }
        for (int i = 0; i < 40; i++)
            view.Instances.Add(new LayoutInstance
            {
                CellRef = $"Cell{i}", X = rng.Next(0, 200_000), Y = rng.Next(0, 200_000), Mag = 1.0,
            });

        static Bbox BoxOf(LayoutInstance inst) => new(inst.X - 2_000, inst.Y - 2_000, inst.X + 2_000, inst.Y + 2_000);

        // Several rects, including ones that match everything and nothing, so the sized-from-last-call
        // estimate is exercised across wildly different selectivities.
        Bbox[] rects =
        [
            new(-1_000_000, -1_000_000, 1_000_000, 1_000_000),
            new(0, 0, 50_000, 50_000),
            new(90_000, 90_000, 92_000, 92_000),
            new(-1_000_000, -1_000_000, 1_000_000, 1_000_000),
            new(500_000, 500_000, 500_100, 500_100),
        ];

        foreach (var rect in rects)
        {
            var result = view.SpatialIndex.QueryIntersecting(view.Shapes, view.Instances, BoxOf, 1, rect);

            for (int i = 1; i < result.Count; i++)
            {
                var prev = result[i - 1];
                var cur = result[i];
                Assert.True(prev.Index < cur.Index || (prev.Index == cur.Index && prev.Kind <= cur.Kind),
                    $"result is out of order at {i}: ({prev.Kind},{prev.Index}) then ({cur.Kind},{cur.Index}).");
            }

            // …and the pre-sizing must not have changed WHAT is returned.
            var expected = new List<LayoutSpatialEntry>();
            for (int i = 0; i < view.Shapes.Count; i++)
                if (LayoutGeometry.BboxOf(view.Shapes[i]).Intersects(rect))
                    expected.Add(new LayoutSpatialEntry(SpatialEntryKind.Shape, i));
            for (int i = 0; i < view.Instances.Count; i++)
                if (BoxOf(view.Instances[i]).Intersects(rect))
                    expected.Add(new LayoutSpatialEntry(SpatialEntryKind.Instance, i));
            expected.Sort((a, b) => a.Index != b.Index ? a.Index.CompareTo(b.Index) : a.Kind.CompareTo(b.Kind));

            Assert.Equal(expected, result);
        }
    }
}

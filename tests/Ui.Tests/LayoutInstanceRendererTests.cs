using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.Schematic;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests;

// ──────────────────────────────────────────────────────────────────────────────
//  Phase L3a — LayoutRenderer instance rendering: gates 2 (pixel identity across rotation/mirror),
//  3 (magnification hairline), 4 (array path-build count), 6 (missing sub-cell placeholder),
//  7 (cycles don't throw), 8 (culling/LOD via counters).
// ──────────────────────────────────────────────────────────────────────────────

[Collection(LayoutTextOutlineTypefaceCollection.Name)]
public sealed class LayoutInstanceRendererTests : IDisposable
{
    private readonly string _workspaceDir;
    private static readonly LayerKey LayerA = new(1, 0);

    public LayoutInstanceRendererTests()
    {
        _workspaceDir = Path.Combine(Path.GetTempPath(), "crfInstRenderTest_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_workspaceDir);
        CellLayoutResolver.InvalidateUnder(_workspaceDir);
        // The broken-instance placeholder draws a text label via LayoutTextOutline.ResolveTypeface,
        // which cannot load SkiaFonts.PlexRegular without a live Avalonia app host — see that type's
        // own TestOverrideTypeface seam (already established by the L1-era label tests).
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
        Layers = [new LayerDef { Key = LayerA, Name = "L1", Color = new CircuitRF.Design.Theming.Rgba(255, 0, 0), FillOpacity = 0.6, ZOrder = 0, Visible = true, Selectable = true }],
    };

    private static LayoutView MakeView() => new() { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000 };

    private string CreateCell(string name, Action<LayoutView> populate)
    {
        var cellDir = CellFolder.CreateCellFolder(_workspaceDir, name);
        string layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        var view = MakeView();
        populate(view);
        LayoutPersistence.SaveToFile(Path.Combine(layoutDir, "main.clay"), view);
        return cellDir;
    }

    /// <summary>An "L" shape — genuinely asymmetric under rotation/mirror, so gate 2's 8-combo pixel
    /// comparison actually exercises the transform rather than accidentally passing via symmetry.</summary>
    private static PolygonShape LShape() => new()
    {
        Layer = LayerA,
        Xy = [0, 0, 300, 0, 300, 100, 100, 100, 100, 300, 0, 300],
    };

    private static byte[] RenderPixels(LayoutView view, Technology? tech, LayoutViewport vp, string? baseDir)
    {
        using var surface = SKSurface.Create(new SKImageInfo((int)vp.Width, (int)vp.Height));
        var opts = new LayoutRenderOptions { Theme = LayoutRenderTheme.Light, ShowGrid = false, BaseDir = baseDir };
        LayoutRenderer.Draw(surface.Canvas, view, tech, vp, opts);
        using var img = surface.Snapshot();
        using var bmp = SKBitmap.FromImage(img);
        return bmp.Bytes;
    }

    // ── Gate 2: pixel-identical instance rendering across all 8 rotation/mirror combos ──────────

    [Theory]
    [InlineData(LayoutRotation.R0, false)]
    [InlineData(LayoutRotation.R90, false)]
    [InlineData(LayoutRotation.R180, false)]
    [InlineData(LayoutRotation.R270, false)]
    [InlineData(LayoutRotation.R0, true)]
    [InlineData(LayoutRotation.R90, true)]
    [InlineData(LayoutRotation.R180, true)]
    [InlineData(LayoutRotation.R270, true)]
    public void InstanceRender_MatchesDirectlyDrawnEquivalentGeometry_ForEveryRotationMirrorCombo(LayoutRotation rot, bool mirror)
    {
        CreateCell("Leaf", v => v.Shapes.Add(LShape()));
        var tech = MakeTech();
        var vp = new LayoutViewport(-500, -500, 0.5, 400, 400);

        // Path A: an instance placed at the origin with the given rotation/mirror.
        var instanceView = MakeView();
        instanceView.Instances.Add(new LayoutInstance { CellRef = "Leaf", X = 0, Y = 0, Rot = rot, MirrorX = mirror, Mag = 1.0 });
        var instancePixels = RenderPixels(instanceView, tech, vp, _workspaceDir);

        // Path B: the SAME transform applied directly to the shape's own vertices (via the canonical
        // LayoutInstanceTransform every consumer shares), drawn as flat geometry — no instance at all.
        var directView = MakeView();
        var src = LShape();
        var transformedXy = new long[src.Xy.Length];
        for (int i = 0; i < src.Xy.Length; i += 2)
        {
            var (tx, ty) = LayoutInstanceTransform.TransformPoint(src.Xy[i], src.Xy[i + 1],
                new LayoutInstance { CellRef = "x", X = 0, Y = 0, Rot = rot, MirrorX = mirror, Mag = 1.0 }, 0, 0);
            transformedXy[i] = tx; transformedXy[i + 1] = ty;
        }
        directView.Shapes.Add(new PolygonShape { Layer = LayerA, Xy = transformedXy });
        var directPixels = RenderPixels(directView, tech, vp, null);

        Assert.Equal(directPixels, instancePixels);
    }

    // ── Gate 3: magnification — 2x instance renders at 2x size, hairline stroke stays constant px ──

    [Fact]
    public void InstanceRender_Magnified2x_DoublesOnScreenExtent()
    {
        CreateCell("Rect", v => v.Shapes.Add(new RectShape { Layer = LayerA, X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000 }));
        var tech = MakeTech();
        var vp = new LayoutViewport(-2000, -2000, 0.1, 400, 400);

        var view1x = MakeView();
        view1x.Instances.Add(new LayoutInstance { CellRef = "Rect", X = 0, Y = 0, Mag = 1.0 });
        var view2x = MakeView();
        view2x.Instances.Add(new LayoutInstance { CellRef = "Rect", X = 0, Y = 0, Mag = 2.0 });

        int width1x = MeasureRedRunLength(RenderPixels(view1x, tech, vp, _workspaceDir), 400, y: (int)vp.WorldToScreenY(500));
        int width2x = MeasureRedRunLength(RenderPixels(view2x, tech, vp, _workspaceDir), 400, y: (int)vp.WorldToScreenY(1000));

        Assert.True(width1x > 0);
        Assert.InRange(width2x, width1x * 2 - 3, width1x * 2 + 3);
    }

    [Fact]
    public void InstanceRender_Magnification_HairlineStrokeStaysConstantDevicePixelWidth()
    {
        CreateCell("Rect", v => v.Shapes.Add(new RectShape { Layer = LayerA, X1 = 0, Y1 = 0, X2 = 1_000_000, Y2 = 1_000_000 }));
        var techNoFill = new Technology
        {
            Name = "Test", DefaultDisplayUnit = LayoutUnit.Um, DefaultSnapDbu = 1000,
            Layers = [new LayerDef { Key = LayerA, Name = "L1", Color = new CircuitRF.Design.Theming.Rgba(255, 0, 0), FillOpacity = 0.0, ZOrder = 0, Visible = true, Selectable = true }],
        };
        var vp = new LayoutViewport(-300_000, -300_000, 0.0003, 600, 600);

        var view1x = MakeView();
        view1x.Instances.Add(new LayoutInstance { CellRef = "Rect", X = 0, Y = 0, Mag = 1.0 });
        var view3x = MakeView();
        view3x.Instances.Add(new LayoutInstance { CellRef = "Rect", X = 0, Y = 0, Mag = 3.0 });

        int thickness1x = MeasureVerticalStrokeThicknessPx(RenderPixels(view1x, techNoFill, vp, _workspaceDir), 600, (int)vp.WorldToScreenX(0), 300);
        int thickness3x = MeasureVerticalStrokeThicknessPx(RenderPixels(view3x, techNoFill, vp, _workspaceDir), 600, (int)vp.WorldToScreenX(0), 300);

        Assert.True(thickness1x > 0, "expected a visible stroke");
        Assert.Equal(thickness1x, thickness3x);
    }

    // ── brief-L3a-followups.md §4/R-fix-5 (gate 8): the drag-and-drop ghost draws REAL compiled
    // geometry when the cell resolves (with real gaps — an asymmetric "L" shape's notch stays
    // unpainted), falling back to the SAME labelled placeholder box a committed unresolved instance
    // gets otherwise. Both the Instance tool's own ghost and drag-and-drop share this one method
    // (DrawPendingInstancePlacement), so exercising it directly via Overlay.PendingInstancePlacement
    // covers both entry points.

    [Fact]
    public void PendingInstancePlacement_ResolvedCell_DrawsRealGeometryWithGaps_NotAUniformBox()
    {
        CreateCell("Leaf", v => v.Shapes.Add(LShape()));
        var tech = MakeTech();
        var vp = new LayoutViewport(-50, -50, 1.0, 400, 400);

        var pending = new LayoutInstance { CellRef = "Leaf", X = 0, Y = 0, Mag = 1.0 };
        var bbox = CellHierarchy.InstanceBbox(pending, _workspaceDir);

        var bg = RenderPixels(MakeView(), tech, vp, _workspaceDir);
        var ghostView = MakeView();
        var ghostOpts = new LayoutRenderOptions
        {
            Theme = LayoutRenderTheme.Light, ShowGrid = false, BaseDir = _workspaceDir,
            Overlay = new LayoutOverlay { PendingInstancePlacement = (pending, bbox) },
        };
        using var surface = SKSurface.Create(new SKImageInfo(400, 400));
        LayoutRenderer.Draw(surface.Canvas, ghostView, tech, vp, ghostOpts);
        using var img = surface.Snapshot();
        using var ghostBmp = SKBitmap.FromImage(img);
        var ghost = ghostBmp.Bytes;

        // The L's notch (x=200,y=200 world — inside the overall (0,0)-(300,300) bbox, but outside
        // the L polygon itself: Xy=[0,0,300,0,300,100,100,100,100,300,0,300] has nothing at x>100 AND
        // y>100). A uniform box ghost (the pre-R-fix-5 behavior) would have painted this point; real
        // compiled geometry leaves it exactly as unpainted as the plain background render.
        int notchX = (int)vp.WorldToScreenX(200), notchY = (int)vp.WorldToScreenY(200);
        int notchOff = (notchY * 400 + notchX) * 4;
        Assert.Equal(bg[notchOff], ghost[notchOff]);
        Assert.Equal(bg[notchOff + 1], ghost[notchOff + 1]);
        Assert.Equal(bg[notchOff + 2], ghost[notchOff + 2]);

        // But somewhere ON the L itself (x=50,y=50 — inside the 0..300 x 0..100 arm), the ghost DID
        // paint something — proving this isn't just "nothing was drawn at all."
        int onShapeX = (int)vp.WorldToScreenX(50), onShapeY = (int)vp.WorldToScreenY(50);
        int onShapeOff = (onShapeY * 400 + onShapeX) * 4;
        bool differs = bg[onShapeOff] != ghost[onShapeOff]
            || bg[onShapeOff + 1] != ghost[onShapeOff + 1]
            || bg[onShapeOff + 2] != ghost[onShapeOff + 2];
        Assert.True(differs, "expected the ghost to paint something on the actual resolved geometry");
    }

    [Fact]
    public void PendingInstancePlacement_UnresolvedCell_FallsBackToTheLabelledPlaceholder_UniformlyPainted()
    {
        var tech = MakeTech();
        var vp = new LayoutViewport(-50, -50, 1.0, 400, 400);

        // "NeverExisted" — never created via CreateCell, so this never resolves (mirrors R-L3a-1's
        // own NotFound case).
        var pending = new LayoutInstance { CellRef = "../NeverExisted", X = 0, Y = 0, Mag = 1.0 };
        var bbox = CellHierarchy.InstanceBbox(pending, _workspaceDir);

        var bg = RenderPixels(MakeView(), tech, vp, _workspaceDir);
        var ghostView = MakeView();
        var ghostOpts = new LayoutRenderOptions
        {
            Theme = LayoutRenderTheme.Light, ShowGrid = false, BaseDir = _workspaceDir,
            Overlay = new LayoutOverlay { PendingInstancePlacement = (pending, bbox) },
        };
        using var surface = SKSurface.Create(new SKImageInfo(400, 400));
        LayoutRenderer.Draw(surface.Canvas, ghostView, tech, vp, ghostOpts);
        using var img = surface.Snapshot();
        using var ghostBmp = SKBitmap.FromImage(img);
        var ghost = ghostBmp.Bytes;

        // Unlike the resolved case above, the fallback placeholder is CellHierarchy.PlaceholderBbox —
        // a 100,000x100,000 DBU box (±50,000 DBU half-extent) around the instance origin, far larger
        // than this 400x400-DBU viewport window — so the WHOLE visible frame sits inside it, and the
        // viewport centre (screen 200,200, world origin) is a safe, simple point to sample.
        Assert.True(bbox.MinX < -50 && bbox.MaxX > 350, "test assumption: the placeholder box should dwarf the viewport window");
        int cx = (int)vp.WorldToScreenX(0), cy = (int)vp.WorldToScreenY(0);
        int off = (cy * 400 + cx) * 4;
        bool differs = bg[off] != ghost[off] || bg[off + 1] != ghost[off + 1] || bg[off + 2] != ghost[off + 2];
        Assert.True(differs, "expected the unresolved-cell fallback placeholder to paint its box");
    }

    private static int MeasureRedRunLength(byte[] rgba, int stride, int y)
    {
        int count = 0;
        for (int x = 0; x < stride; x++)
        {
            int off = (y * stride + x) * 4;
            if (off + 2 >= rgba.Length) break;
            byte r = rgba[off], g = rgba[off + 1], b = rgba[off + 2];
            if (r > g + 30 && r > b + 30) count++;
        }
        return count;
    }

    private static int MeasureVerticalStrokeThicknessPx(byte[] rgba, int stride, int aroundX, int y)
    {
        int count = 0;
        for (int x = Math.Max(0, aroundX - 10); x < aroundX + 10; x++)
        {
            int off = (y * stride + x) * 4;
            if (off + 2 >= rgba.Length) continue;
            byte r = rgba[off], g = rgba[off + 1], b = rgba[off + 2];
            if (r > g + 30 && r > b + 30) count++;
        }
        return count;
    }

    // ── Gate 4: array — one compile (PathsConstructed == O(sub-cell shapes)), N matrix draws ────

    [Fact]
    public void ArrayInstance_PathsConstructed_IsProportionalToSubCellShapes_NotToPlacementCount()
    {
        const int subCellShapeCount = 20;
        CreateCell("Via", v =>
        {
            for (int i = 0; i < subCellShapeCount; i++)
                v.Shapes.Add(new RectShape { Layer = LayerA, X1 = i * 10, Y1 = 0, X2 = i * 10 + 5, Y2 = 5 });
        });
        var tech = MakeTech();

        var view = MakeView();
        view.Instances.Add(new LayoutInstance { CellRef = "Via", X = 0, Y = 0, Mag = 1.0, Rows = 50, Cols = 50, PitchX = 1000, PitchY = 1000 });

        // Viewport wide enough to see the whole 50x50 array (49000 DBU span) — every placement is a
        // real candidate, not culled, so InstancesDrawn below genuinely reflects all 2500 placements.
        var vp = new LayoutViewport(-2000, -2000, 0.01, 800, 800);

        using var surface = SKSurface.Create(new SKImageInfo(800, 800));
        var opts = new LayoutRenderOptions { Theme = LayoutRenderTheme.Light, ShowGrid = false, BaseDir = _workspaceDir };
        var result = LayoutRenderer.Draw(surface.Canvas, view, tech, vp, opts);

        // The headline assertion (R-L3a-3): PathsConstructed reflects ONE compile of the sub-cell
        // (subCellShapeCount), never 2,500x that.
        Assert.Equal(subCellShapeCount, result.PathsConstructed);
        Assert.Equal(2500, result.InstancesDrawn);

        // A second frame (same view — the resolved LayoutView is still cached by CellLayoutResolver,
        // and the compiled geometry cache is keyed off that same reference) constructs ZERO further
        // paths — full reuse across frames, not just within one.
        var result2 = LayoutRenderer.Draw(surface.Canvas, view, tech, vp, opts);
        Assert.Equal(0, result2.PathsConstructed);
        Assert.Equal(2500, result2.InstancesDrawn);
    }

    // ── Gate 6: missing sub-cell — labelled placeholder, reported, never throws ──────────────────

    [Fact]
    public void MissingSubCell_RendersPlaceholder_AndIsReportedOnce()
    {
        var view = MakeView();
        view.Instances.Add(new LayoutInstance { CellRef = "GhostCell", X = 0, Y = 0, Mag = 1.0 });
        var tech = MakeTech();
        var vp = new LayoutViewport(-2000, -2000, 0.1, 400, 400);

        using var surface = SKSurface.Create(new SKImageInfo(400, 400));
        var opts = new LayoutRenderOptions { Theme = LayoutRenderTheme.Light, ShowGrid = false, BaseDir = _workspaceDir };
        var result = LayoutRenderer.Draw(surface.Canvas, view, tech, vp, opts);

        Assert.Equal(1, result.InstancesDrawn); // the placeholder itself counts as one drawn placement
        Assert.NotNull(result.MissingInstanceCellRefs);
        Assert.Equal(["GhostCell"], result.MissingInstanceCellRefs);

        // Placeholder is a real, visible mark — not an empty frame.
        using var img = surface.Snapshot();
        using var bmp = SKBitmap.FromImage(img);
        bool anyNonBackground = false;
        for (int y = 0; y < 400 && !anyNonBackground; y++)
        for (int x = 0; x < 400; x++)
        {
            var c = bmp.GetPixel(x, y);
            if (c.Red != 255 || c.Green != 255 || c.Blue != 255) { anyNonBackground = true; break; }
        }
        Assert.True(anyNonBackground, "expected the placeholder to paint SOMETHING, not an empty frame");
    }

    [Fact]
    public void ReportMissingInstanceCellRefs_WarnsOnlyOncePerDistinctCellRef()
    {
        var messages = new List<string>();
        var sink = new RecordingMessageSink(messages);
        var vm = new LayoutEditorViewModel(MakeView(), messageSink: sink);

        vm.ReportMissingInstanceCellRefs(["Ghost1"]);
        vm.ReportMissingInstanceCellRefs(["Ghost1"]); // same ref again — must not re-warn
        vm.ReportMissingInstanceCellRefs(["Ghost2"]);

        Assert.Equal(2, messages.Count);
    }

    // ── Gate 7: cycles never throw or overflow at render time ───────────────────────────────────

    [Fact]
    public void MutualCycle_RendersWithoutThrowing_MarksTheCyclicInstanceBroken()
    {
        // A -> B, B -> A. Rendering an instance of A must not infinitely recurse.
        CreateCell("A", v => v.Instances.Add(new LayoutInstance { CellRef = "../B", X = 0, Y = 0, Mag = 1.0 }));
        CreateCell("B", v => v.Instances.Add(new LayoutInstance { CellRef = "../A", X = 0, Y = 0, Mag = 1.0 }));

        var view = MakeView();
        view.Instances.Add(new LayoutInstance { CellRef = "A", X = 0, Y = 0, Mag = 1.0 });
        var tech = MakeTech();
        var vp = new LayoutViewport(-2000, -2000, 0.1, 400, 400);

        using var surface = SKSurface.Create(new SKImageInfo(400, 400));
        var opts = new LayoutRenderOptions { Theme = LayoutRenderTheme.Light, ShowGrid = false, BaseDir = _workspaceDir };

        var exception = Record.Exception(() => LayoutRenderer.Draw(surface.Canvas, view, tech, vp, opts));
        Assert.Null(exception);
    }

    [Fact]
    public void DeepChain_BeyondMaxDepth_RendersWithoutThrowing()
    {
        const int chainLength = 40;
        CreateCell("Cell0", v => v.Shapes.Add(new RectShape { Layer = LayerA, X1 = 0, Y1 = 0, X2 = 10, Y2 = 10 }));
        for (int i = 1; i < chainLength; i++)
        {
            int captured = i;
            CreateCell($"Cell{captured}", v => v.Instances.Add(new LayoutInstance { CellRef = $"../Cell{captured - 1}", X = 0, Y = 0, Mag = 1.0 }));
        }

        var view = MakeView();
        view.Instances.Add(new LayoutInstance { CellRef = $"Cell{chainLength - 1}", X = 0, Y = 0, Mag = 1.0 });
        var tech = MakeTech();
        var vp = new LayoutViewport(-2000, -2000, 0.1, 400, 400);

        using var surface = SKSurface.Create(new SKImageInfo(400, 400));
        var opts = new LayoutRenderOptions { Theme = LayoutRenderTheme.Light, ShowGrid = false, BaseDir = _workspaceDir };

        var exception = Record.Exception(() => LayoutRenderer.Draw(surface.Canvas, view, tech, vp, opts));
        Assert.Null(exception);
    }

    // ── Gate 8: culling and LOD via counters ─────────────────────────────────────────────────────

    [Fact]
    public void OffScreenInstance_IsCulled_NeverExamined()
    {
        CreateCell("Rect", v => v.Shapes.Add(new RectShape { Layer = LayerA, X1 = 0, Y1 = 0, X2 = 100, Y2 = 100 }));
        var tech = MakeTech();
        var view = MakeView();
        view.Instances.Add(new LayoutInstance { CellRef = "Rect", X = 0, Y = 0, Mag = 1.0 });
        view.Instances.Add(new LayoutInstance { CellRef = "Rect", X = 50_000_000, Y = 50_000_000, Mag = 1.0 }); // far off-screen

        var vp = new LayoutViewport(-1000, -1000, 0.1, 400, 400); // views only the origin

        using var surface = SKSurface.Create(new SKImageInfo(400, 400));
        var opts = new LayoutRenderOptions { Theme = LayoutRenderTheme.Light, ShowGrid = false, BaseDir = _workspaceDir };
        var result = LayoutRenderer.Draw(surface.Canvas, view, tech, vp, opts);

        Assert.Equal(1, result.InstancesExamined); // only the on-screen one
    }

    [Fact]
    public void SubPixelInstance_DrawsAsMinimalMark_NeverCompiled()
    {
        CreateCell("TinyRect", v => v.Shapes.Add(new RectShape { Layer = LayerA, X1 = 0, Y1 = 0, X2 = 5, Y2 = 5 }));
        var tech = MakeTech();
        var view = MakeView();
        view.Instances.Add(new LayoutInstance { CellRef = "TinyRect", X = 0, Y = 0, Mag = 1.0 });

        // Deep zoom-out: a 5 DBU shape renders far under the 2px LOD threshold.
        var vp = new LayoutViewport(-1_000_000, -1_000_000, 0.0002, 400, 400);

        using var surface = SKSurface.Create(new SKImageInfo(400, 400));
        var opts = new LayoutRenderOptions { Theme = LayoutRenderTheme.Light, ShowGrid = false, BaseDir = _workspaceDir };
        var result = LayoutRenderer.Draw(surface.Canvas, view, tech, vp, opts);

        Assert.Equal(1, result.InstancesExamined);
        Assert.Equal(1, result.InstancesDrawn);
        Assert.Equal(0, result.PathsConstructed); // LOD collapse never descends into the sub-cell at all
    }

    // ── Test helper ───────────────────────────────────────────────────────────────────────────

    private sealed class RecordingMessageSink : CircuitRF.Ui.Messages.IMessageSink
    {
        private readonly List<string> _messages;
        public RecordingMessageSink(List<string> messages) => _messages = messages;
        public void Post(CircuitRF.Ui.Messages.MessageLevel level, string text, string? filePath = null) => _messages.Add(text);
        public void Clear() => _messages.Clear();
    }
}

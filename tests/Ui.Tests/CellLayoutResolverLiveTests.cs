using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

// ──────────────────────────────────────────────────────────────────────────────
//  Phase L3b — CellLayoutResolver's live-override seam (R-L3b-1's in-session-edit path), mirroring
//  TechnologyCache's SetLive/ClearLive/HasLiveOverride exactly (docs/sonnet-briefs/brief-L3b-
//  hierarchy-navigation.md §2). Framework-free.
// ──────────────────────────────────────────────────────────────────────────────

public sealed class CellLayoutResolverLiveTests : IDisposable
{
    private readonly string _workspaceDir;
    private static readonly LayerKey LayerA = new(1, 0);

    public CellLayoutResolverLiveTests()
    {
        _workspaceDir = Path.Combine(Path.GetTempPath(), "crfCellLayoutLiveTest_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_workspaceDir);
        CellLayoutResolver.InvalidateAll();
    }

    public void Dispose()
    {
        CellLayoutResolver.InvalidateAll();
        if (Directory.Exists(_workspaceDir))
            Directory.Delete(_workspaceDir, recursive: true);
    }

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

    [Fact]
    public void Resolve_NoLiveOverride_ReturnsDiskContent()
    {
        CreateCell("Leaf", v => v.Shapes.Add(new RectShape { Layer = LayerA, X1 = 0, Y1 = 0, X2 = 100, Y2 = 100 }));

        var res = CellLayoutResolver.Resolve("Leaf", _workspaceDir);

        Assert.Equal(CellLayoutState.Resolved, res.State);
        Assert.Single(res.View!.Shapes);
    }

    [Fact]
    public void SetLive_MakesResolveReturnTheLiveView_NotDiskContent()
    {
        var clayPath = CreateCell("Leaf", v => v.Shapes.Add(new RectShape { Layer = LayerA, X1 = 0, Y1 = 0, X2 = 100, Y2 = 100 }));

        var liveView = MakeView();
        liveView.Shapes.Add(new RectShape { Layer = LayerA, X1 = 0, Y1 = 0, X2 = 1, Y2 = 1 });
        liveView.Shapes.Add(new RectShape { Layer = LayerA, X1 = 5, Y1 = 5, X2 = 6, Y2 = 6 });
        CellLayoutResolver.SetLive(clayPath, liveView);

        var res = CellLayoutResolver.Resolve("Leaf", _workspaceDir);

        Assert.Equal(CellLayoutState.Resolved, res.State);
        Assert.Same(liveView, res.View);
        Assert.Equal(2, res.View!.Shapes.Count);   // the LIVE (2-shape) content, not disk's 1-shape file
    }

    [Fact]
    public void SetLive_BumpsGeneration_AndFiresLiveViewChanged_EveryCall_EvenSameReference()
    {
        var clayPath = CreateCell("Leaf", v => { });
        var liveView = MakeView();

        long genBefore = CellLayoutResolver.Generation;
        var firedPaths = new List<string>();
        CellLayoutResolver.LiveViewChanged += OnChanged;
        try
        {
            CellLayoutResolver.SetLive(clayPath, liveView);
            CellLayoutResolver.SetLive(clayPath, liveView);   // SAME reference — an in-session session
                                                                // mutates in place across edits.
        }
        finally
        {
            CellLayoutResolver.LiveViewChanged -= OnChanged;
        }

        Assert.True(CellLayoutResolver.Generation > genBefore + 1);   // bumped on EVERY call
        Assert.Equal(2, firedPaths.Count);
        Assert.All(firedPaths, p => Assert.Equal(Path.GetFullPath(clayPath), p, ignoreCase: true));

        void OnChanged(string path) => firedPaths.Add(path);
    }

    [Fact]
    public void ClearLive_FallsBackToDiskContent_NoEventWhenNothingWasInstalled()
    {
        var clayPath = CreateCell("Leaf", v => v.Shapes.Add(new RectShape { Layer = LayerA, X1 = 0, Y1 = 0, X2 = 100, Y2 = 100 }));
        var liveView = MakeView();
        CellLayoutResolver.SetLive(clayPath, liveView);
        Assert.True(CellLayoutResolver.HasLiveOverride(clayPath));

        CellLayoutResolver.ClearLive(clayPath);
        Assert.False(CellLayoutResolver.HasLiveOverride(clayPath));

        var res = CellLayoutResolver.Resolve("Leaf", _workspaceDir);
        Assert.NotSame(liveView, res.View);
        Assert.Single(res.View!.Shapes);   // back to the on-disk (1-shape) content

        int fireCount = 0;
        CellLayoutResolver.LiveViewChanged += OnChanged;
        try { CellLayoutResolver.ClearLive(clayPath); }   // already cleared — no-op, no event
        finally { CellLayoutResolver.LiveViewChanged -= OnChanged; }
        Assert.Equal(0, fireCount);

        void OnChanged(string path) => fireCount++;
    }

    [Fact]
    public void Invalidate_ClearsBothPlainCacheAndLiveOverride_ForThatCell()
    {
        var clayPath = CreateCell("Leaf", v => v.Shapes.Add(new RectShape { Layer = LayerA, X1 = 0, Y1 = 0, X2 = 100, Y2 = 100 }));
        var cellDir  = Path.Combine(_workspaceDir, "Leaf");

        // Prime the plain cache.
        var cached = CellLayoutResolver.Resolve("Leaf", _workspaceDir).View;
        // Install a live override too.
        var liveView = MakeView();
        CellLayoutResolver.SetLive(clayPath, liveView);
        Assert.True(CellLayoutResolver.HasLiveOverride(clayPath));

        CellLayoutResolver.Invalidate(cellDir);

        Assert.False(CellLayoutResolver.HasLiveOverride(clayPath));
        // Next Resolve reloads from disk fresh (a new View instance, not the previously cached one).
        var res = CellLayoutResolver.Resolve("Leaf", _workspaceDir);
        Assert.NotSame(cached, res.View);
        Assert.NotSame(liveView, res.View);
    }

    [Fact]
    public void InvalidateAll_ClearsEveryLiveOverride()
    {
        var pathA = CreateCell("A", v => { });
        var pathB = CreateCell("B", v => { });
        CellLayoutResolver.SetLive(pathA, MakeView());
        CellLayoutResolver.SetLive(pathB, MakeView());

        CellLayoutResolver.InvalidateAll();

        Assert.False(CellLayoutResolver.HasLiveOverride(pathA));
        Assert.False(CellLayoutResolver.HasLiveOverride(pathB));
    }
}

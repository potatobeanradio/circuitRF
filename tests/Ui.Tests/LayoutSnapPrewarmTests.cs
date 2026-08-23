using System;
using System.IO;
using System.Threading.Tasks;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// The open-time snap-index prewarm (<c>LayoutEditorViewModel.PrewarmPlacedCellSnapIndices</c>).
///
/// <para>The index is a per-cell cost paid once; what was wrong with paying it lazily is WHEN it
/// landed — inside the snap query, on whichever pointer move arrived first, which for a generated
/// cell carrying a six-figure via field is a visible hitch on an input event. These tests assert the
/// mechanism (the index is built, for every distinct placed cell, before any pointer has moved) and
/// never a duration: <c>SnapPrewarm</c> is awaited, so there is no deadline to flake on.</para>
/// </summary>
public sealed class LayoutSnapPrewarmTests : IDisposable
{
    private readonly string _workspaceDir;
    private static readonly LayerKey LayerA = new(1, 0);

    public LayoutSnapPrewarmTests()
    {
        _workspaceDir = Path.Combine(Path.GetTempPath(), "crfSnapPrewarm_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_workspaceDir);
        CellLayoutResolver.InvalidateUnder(_workspaceDir);
    }

    public void Dispose()
    {
        CellLayoutResolver.InvalidateUnder(_workspaceDir);
        if (Directory.Exists(_workspaceDir)) Directory.Delete(_workspaceDir, recursive: true);
    }

    /// <summary>A resolution with no technology — the "workspace has no .ctech" case, and all these
    /// tests need, since none of them turns on a layer table.</summary>
    private static readonly TechResolution NoTech = new(null, null, TechResolutionSource.None, []);

    private static LayoutView FreshModel() => new() { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000 };

    private string CreateCell(string name, Action<LayoutView> populate)
    {
        var cellDir = CellFolder.CreateCellFolder(_workspaceDir, name);
        var view = FreshModel();
        populate(view);
        LayoutPersistence.SaveToFile(
            Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Layout), "main.clay"), view);
        return cellDir;
    }

    private static void AddField(LayoutView v, int side, long pitch, long size)
    {
        for (int r = 0; r < side; r++)
        for (int c = 0; c < side; c++)
            v.Shapes.Add(new RectShape
            {
                Layer = LayerA,
                X1 = c * pitch, Y1 = r * pitch, X2 = c * pitch + size, Y2 = r * pitch + size,
            });
    }

    /// <summary>Opening the document warms every placed cell's index — including one reached only
    /// through another cell, since a nested placement's features are just as reachable by a snap
    /// query and just as expensive to index on demand.</summary>
    [Fact]
    public async Task OpeningALayout_WarmsEveryPlacedCellsIndex_BeforeAnyPointerMove()
    {
        string leaf = CreateCell("Leaf", v => AddField(v, 20, pitch: 400, size: 100));
        // "../../Leaf": a nested reference resolves against the PARENT CELL'S LAYOUT FOLDER
        // (CellHierarchy.LayoutBaseDirOf), so reaching a sibling cell climbs out of both `layout/`
        // and the cell folder — one level more than the top-level instance below needs.
        CreateCell("Mid", v => v.Instances.Add(new LayoutInstance { CellRef = "../../Leaf", X = 0, Y = 0, Mag = 1.0 }));

        var model = FreshModel();
        model.Instances.Add(new LayoutInstance { CellRef = "Mid", X = 0, Y = 0, Mag = 1.0 });

        string clayPath = Path.Combine(_workspaceDir, "top.clay");
        LayoutPersistence.SaveToFile(clayPath, model);

        var leafView = CellLayoutResolver.Resolve("Leaf", _workspaceDir).View!;
        Assert.NotNull(leafView);
        Assert.False(LayoutSnapFeatureIndex.IsCached(leafView), "nothing has asked for the index yet");

        var vm = new LayoutEditorViewModel(model, clayPath);
        vm.ApplyTechResolution(NoTech);   // every real call site applies one right after construction
        Assert.NotNull(vm.SnapPrewarm);
        await vm.SnapPrewarm!;

        Assert.True(LayoutSnapFeatureIndex.IsCached(leafView),
            "the nested cell's index should be built by open, not by the first pointer move");

        var midView = CellLayoutResolver.Resolve("Mid", _workspaceDir).View!;
        Assert.NotNull(midView);
        Assert.True(LayoutSnapFeatureIndex.IsCached(midView));

        // Zero pointer moves have happened, and the query the first one would run is already answerable.
        Assert.Equal(0, vm.SnapQueryRunCount);
        Assert.True(LayoutSnapFeatureIndex.Get(leafView, null).FeatureCount > 0);
    }

    /// <summary>The prewarm never touches the ACTIVELY EDITED document's own index. That one is
    /// invalidated on every change, so a background build racing an edit could store a snapshot taken
    /// before it — a silently stale snap index. It stays lazy, on the thread that owns the model.</summary>
    [Fact]
    public async Task ThePrewarm_LeavesTheEditedDocumentsOwnIndexAlone()
    {
        CreateCell("Leaf", v => AddField(v, 20, pitch: 400, size: 100));

        var model = FreshModel();
        AddField(model, 10, pitch: 400, size: 100);
        model.Instances.Add(new LayoutInstance { CellRef = "Leaf", X = 0, Y = 0, Mag = 1.0 });

        string clayPath = Path.Combine(_workspaceDir, "top.clay");
        LayoutPersistence.SaveToFile(clayPath, model);

        var vm = new LayoutEditorViewModel(model, clayPath);
        vm.ApplyTechResolution(NoTech);
        await vm.SnapPrewarm!;

        var leafView = CellLayoutResolver.Resolve("Leaf", _workspaceDir).View!;
        Assert.NotNull(leafView);
        Assert.True(LayoutSnapFeatureIndex.IsCached(leafView), "the PLACED cell is warmed");
        Assert.False(LayoutSnapFeatureIndex.IsCached(model), "the edited document's own index is not");
    }

    /// <summary>A layout that places nothing has nothing to warm, and must not spin up a task to
    /// discover that — the overwhelmingly common case is a document with no instances at all.</summary>
    [Fact]
    public void ALayoutWithNoInstances_StartsNoPrewarm()
    {
        var model = FreshModel();
        AddField(model, 10, pitch: 400, size: 100);

        var vm = new LayoutEditorViewModel(model, Path.Combine(_workspaceDir, "flat.clay"));
        vm.ApplyTechResolution(NoTech);

        Assert.Null(vm.SnapPrewarm);
    }

    /// <summary>One cell placed many times is ONE index build. The walk dedups on the resolved cell
    /// directory, which is what keeps a 24-placement design from paying 24 times for the same cell —
    /// asserted through the resolver's own identity: every placement resolves to the same
    /// <see cref="LayoutView"/> instance, and the index is keyed by that reference.</summary>
    [Fact]
    public async Task ManyPlacementsOfOneCell_ShareASingleIndex()
    {
        CreateCell("Leaf", v => AddField(v, 20, pitch: 400, size: 100));

        var model = FreshModel();
        for (int i = 0; i < 24; i++)
            model.Instances.Add(new LayoutInstance { CellRef = "Leaf", X = i * 100_000, Y = 0, Mag = 1.0 });

        string clayPath = Path.Combine(_workspaceDir, "top.clay");
        LayoutPersistence.SaveToFile(clayPath, model);

        var vm = new LayoutEditorViewModel(model, clayPath);
        vm.ApplyTechResolution(NoTech);
        await vm.SnapPrewarm!;

        var a = CellLayoutResolver.Resolve("Leaf", _workspaceDir).View!;
        var b = CellLayoutResolver.Resolve("Leaf", _workspaceDir).View!;
        Assert.NotNull(a);
        Assert.Same(a, b);
        Assert.Same(LayoutSnapFeatureIndex.Get(a, null), LayoutSnapFeatureIndex.Get(b, null));
    }
}

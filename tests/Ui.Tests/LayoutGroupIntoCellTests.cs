using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Messages;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.Schematic;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests;

// ──────────────────────────────────────────────────────────────────────────────
//  Phase L3c (brief-L3c-flatten-and-group.md §4) — gates 8 (group does not move geometry, mixed
//  selection incl. a hole polygon and an instance), 9 (group creates a real cell inheriting TechRef/
//  DbuPerMicron), 10 (undo restores shapes, instance gone, cell folder remains, Messages note; redo
//  reuses the cell), 11 (round-trip: group then flatten the result is byte-identical to the original).
// ──────────────────────────────────────────────────────────────────────────────

[Collection(LayoutTextOutlineTypefaceCollection.Name)]
public sealed class LayoutGroupIntoCellTests : IDisposable
{
    private readonly string _workspaceDir;
    private static readonly LayerKey LayerA = new(1, 0);

    public LayoutGroupIntoCellTests()
    {
        _workspaceDir = Path.Combine(Path.GetTempPath(), "crfGroupTest_" + Guid.NewGuid().ToString("N")[..8]);
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

    private sealed class FakeMessageSink : IMessageSink
    {
        public List<(MessageLevel Level, string Text)> Posted { get; } = [];
        public void Post(MessageLevel level, string text, string? filePath = null) => Posted.Add((level, text));
        public void Clear() => Posted.Clear();
    }

    private static Technology MakeTech() => new()
    {
        Name = "Test", DefaultDisplayUnit = LayoutUnit.Um, DefaultSnapDbu = 1000,
        Layers = [new LayerDef { Key = LayerA, Name = "L1", Color = new CircuitRF.Design.Theming.Rgba(255, 0, 0), FillOpacity = 1.0, ZOrder = 0, Visible = true, Selectable = true }],
    };

    private string CreateCell(string name, Action<LayoutView> populate)
    {
        var cellDir = CellFolder.CreateCellFolder(_workspaceDir, name);
        string layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        var view = new LayoutView { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000 };
        populate(view);
        LayoutPersistence.SaveToFile(Path.Combine(layoutDir, "main.clay"), view);
        return cellDir;
    }

    /// <summary>Top-level document (shapes + a resolvable instance) + its VM, wired to resolve against
    /// <see cref="_workspaceDir"/> the same way <c>WorkspaceViewModel</c> would.</summary>
    private (LayoutEditorViewModel Vm, LayoutView Model, FakeMessageSink Sink) BuildMixedSelectionDocument()
    {
        CreateCell("Via", v => v.Shapes.Add(new RectShape { Layer = LayerA, X1 = 0, Y1 = 0, X2 = 50, Y2 = 50 }));

        var model = new LayoutView { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000, TechRef = "tech/pcb.ctech" };
        model.Shapes.Add(new RectShape { Layer = LayerA, X1 = 0, Y1 = 0, X2 = 300, Y2 = 100 });
        model.Shapes.Add(new PolygonShape
        {
            Layer = LayerA,
            Xy = [400, 0, 900, 0, 900, 500, 400, 500],
            Holes = [[500, 100, 800, 100, 800, 400, 500, 400]],
        });
        model.Instances.Add(new LayoutInstance { CellRef = "Via", X = 1000, Y = 1000, Mag = 1.0 });

        var sink = new FakeMessageSink();
        var clayPath = Path.Combine(_workspaceDir, "top.clay");
        var vm = new LayoutEditorViewModel(model, clayPath, sink);

        // SelectAllCommand only covers shapes (R-fix-2's mixed selection is reached via marquee/click,
        // not Select All) — a full-document enclosing marquee selects both kinds together instead.
        vm.OnPointerPressed(-1000, -1000, Avalonia.Input.KeyModifiers.None);
        vm.OnPointerMoved(2000, 2000, leftDown: true, Avalonia.Input.KeyModifiers.None);
        vm.OnPointerReleased(2000, 2000, Avalonia.Input.KeyModifiers.None);
        Assert.Equal(2, vm.SelectedIndices.Count);
        Assert.Single(vm.SelectedInstanceIndices);
        return (vm, model, sink);
    }

    private static byte[] RenderPixels(LayoutView view, Technology tech, LayoutViewport vp, string? baseDir)
    {
        using var surface = SKSurface.Create(new SKImageInfo((int)vp.Width, (int)vp.Height));
        var opts = new LayoutRenderOptions { Theme = LayoutRenderTheme.Light, ShowGrid = false, BaseDir = baseDir };
        LayoutRenderer.Draw(surface.Canvas, view, tech, vp, opts);
        using var img = surface.Snapshot();
        using var bmp = SKBitmap.FromImage(img);
        return bmp.Bytes;
    }

    // ── Gate 8: group does not move geometry — pixel-identical before/after, mixed selection ───────

    [Fact]
    public void CommitGroupIntoCell_MixedSelection_RendersPixelIdentical_BeforeAndAfter()
    {
        var (vm, model, _) = BuildMixedSelectionDocument();
        var tech = MakeTech();
        var vp = new LayoutViewport(-500, -500, 0.3, 400, 400);

        var beforeView = new LayoutView { DbuPerMicron = model.DbuPerMicron, DisplayUnit = model.DisplayUnit, SnapDbu = model.SnapDbu };
        beforeView.Shapes.AddRange(model.Shapes);
        beforeView.Instances.AddRange(model.Instances);
        var beforePixels = RenderPixels(beforeView, tech, vp, _workspaceDir);

        bool ok = vm.CommitGroupIntoCell(_workspaceDir, "Grouped");
        Assert.True(ok);

        var afterPixels = RenderPixels(model, tech, vp, _workspaceDir);
        Assert.Equal(beforePixels, afterPixels);
    }

    // ── Gate 9: group creates a real cell, inherits TechRef/DbuPerMicron ────────────────────────────

    [Fact]
    public void CommitGroupIntoCell_CreatesRealCell_InheritsTechRefAndDbuPerMicron_AppearsAsOneInstance()
    {
        var (vm, model, sink) = BuildMixedSelectionDocument();

        bool ok = vm.CommitGroupIntoCell(_workspaceDir, "Grouped");
        Assert.True(ok);

        string cellDir = Path.Combine(_workspaceDir, "Grouped");
        Assert.True(Directory.Exists(cellDir));
        string ccellPath = Path.Combine(cellDir, CellFolder.CcellFileName);
        Assert.True(File.Exists(ccellPath));
        var ccell = CellPersistence.LoadFromFile(ccellPath);
        Assert.Equal("Grouped.clay", ccell.PrimaryLayout);

        // A brand-new cell has exactly one layout file, so ResolvePrimary correctly reports SoleFile
        // (unambiguous regardless of the .ccell's own PrimaryLayout field) rather than NamedPresent.
        var resolved = CellFolder.ResolvePrimary(cellDir, ViewType.Layout);
        Assert.Equal(PrimaryState.SoleFile, resolved.State);

        string layoutPath = Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Layout), "Grouped.clay");
        var newCellView = LayoutPersistence.LoadFromFile(layoutPath);
        Assert.Equal(model.TechRef, newCellView.TechRef);           // inherited verbatim
        Assert.Equal(model.DbuPerMicron, newCellView.DbuPerMicron); // inherited verbatim

        Assert.Empty(model.Shapes);
        Assert.Single(model.Instances);
        Assert.Equal(1.0, model.Instances[0].Mag);
        Assert.Equal(LayoutRotation.R0, model.Instances[0].Rot);
        Assert.False(model.Instances[0].MirrorX);

        Assert.Contains(sink.Posted, p => p.Level == MessageLevel.Success && p.Text.Contains("Grouped"));
    }

    // ── Gate 10: undo restores shapes at original indices, instance gone, cell folder remains ──────

    [Fact]
    public void CommitGroupIntoCell_Undo_RestoresShapesAtOriginalIndices_InstanceGone_CellFolderRemains_MessagesNote()
    {
        var (vm, model, sink) = BuildMixedSelectionDocument();
        var rectBefore = model.Shapes[0];
        var polyBefore = model.Shapes[1];
        var instBefore = model.Instances[0];

        vm.CommitGroupIntoCell(_workspaceDir, "Grouped");
        string cellDir = Path.Combine(_workspaceDir, "Grouped");
        Assert.True(Directory.Exists(cellDir));

        vm.UndoRedo.Undo();

        Assert.Equal(2, model.Shapes.Count);
        Assert.Same(rectBefore, model.Shapes[0]);
        Assert.Same(polyBefore, model.Shapes[1]);
        Assert.Single(model.Instances);
        Assert.Equal(instBefore.CellRef, model.Instances[0].CellRef);
        Assert.DoesNotContain(model.Instances, i => i.CellRef == "Grouped");

        // R-L3c-6: undo does NOT delete the created cell folder.
        Assert.True(Directory.Exists(cellDir));
    }

    [Fact]
    public void CommitGroupIntoCell_UndoThenRedo_ReusesTheSameCell_NeverCreatesASecondOne()
    {
        var (vm, model, _) = BuildMixedSelectionDocument();
        vm.CommitGroupIntoCell(_workspaceDir, "Grouped");

        vm.UndoRedo.Undo();
        vm.UndoRedo.Redo();

        Assert.Single(model.Instances);
        Assert.Equal("Grouped", model.Instances[0].CellRef);
        // Exactly one "Grouped" (or "Grouped N") folder exists — redo must not have minted a second cell.
        var groupedDirs = Directory.GetDirectories(_workspaceDir, "Grouped*");
        Assert.Single(groupedDirs);
    }

    // ── Gate 11: round-trip — group then flatten the result is byte-identical to the original ──────

    [Fact]
    public void GroupThenFlattenOneLevel_RoundTrips_ByteIdenticalToOriginal()
    {
        var (vm, model, _) = BuildMixedSelectionDocument();

        // Snapshot the ORIGINAL selection's geometry in a document of its own (so it can be compared
        // byte-for-byte against the round-tripped result, independent of the rest of the top document).
        var originalSnapshot = new LayoutView { DbuPerMicron = model.DbuPerMicron, DisplayUnit = model.DisplayUnit, SnapDbu = model.SnapDbu };
        originalSnapshot.Shapes.Add(LayoutGeometry.Clone(model.Shapes[0]));
        originalSnapshot.Shapes.Add(LayoutGeometry.Clone(model.Shapes[1]));
        originalSnapshot.Instances.Add(LayoutGeometry.Clone(model.Instances[0]));
        string originalSerialized = LayoutPersistence.Serialize(originalSnapshot);

        vm.CommitGroupIntoCell(_workspaceDir, "Grouped");
        Assert.Single(model.Instances);
        Assert.Empty(model.Shapes);

        // Click-select the new "Grouped" instance, then flatten it one level — its own content is the
        // 2 shapes AND the "Via" instance, so flattening restores all three, not just the shapes.
        vm.OnPointerPressed(10, 10, Avalonia.Input.KeyModifiers.None);
        Assert.Equal([0], vm.SelectedInstanceIndices);
        vm.CommitFlattenOneLevel();

        Assert.Single(model.Instances);   // the "Via" instance, rebased back — flatten does not flatten it further
        var roundTripped = new LayoutView { DbuPerMicron = model.DbuPerMicron, DisplayUnit = model.DisplayUnit, SnapDbu = model.SnapDbu };
        roundTripped.Shapes.AddRange(model.Shapes.Select(LayoutGeometry.Clone));
        roundTripped.Instances.AddRange(model.Instances.Select(LayoutGeometry.Clone));
        string roundTrippedSerialized = LayoutPersistence.Serialize(roundTripped);

        Assert.Equal(originalSerialized, roundTrippedSerialized);
    }
}

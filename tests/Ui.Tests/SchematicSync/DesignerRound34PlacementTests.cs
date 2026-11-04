using Avalonia.Input;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Messages;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests.SchematicSync;

/// <summary>
/// brief-dfr34-5 §2: a cell cropped from a board keeps the board's coordinates, so Update Layout's new parts landed at
/// the origin 160 mm away (R-dfr34-5-3), and a ground via placed beside one stayed there when the part was dragged onto
/// the board (R-dfr34-5-4).
/// </summary>
public sealed class DesignerRound34PlacementTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-dfr34-5-" + Guid.NewGuid().ToString("N")[..8]);

    public DesignerRound34PlacementTests()
    {
        Directory.CreateDirectory(_root);
        CellLayoutResolver.InvalidateUnder(_root);
    }

    public void Dispose()
    {
        CellLayoutResolver.InvalidateUnder(_root);
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static EditableComponent Mlin(string name)
    {
        var comp = new EditableComponent { InstanceName = name, Symbol = SymbolKind.Mlin };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(SymbolKind.Mlin, 0))
            comp.Parameters.Add(new EditableParameter
            {
                Name = dp.Name, Expression = dp.Expression, Unit = dp.Unit,
                ShowOnSchematic = dp.ShowOnSchematic, Dimension = dp.Dimension,
            });
        return comp;
    }

    [Fact]
    public void NewParts_GoUnderTheCroppedBoardsArtwork_NotAtTheOrigin()
    {
        string cellDir = CellFolder.CreateCellFolder(_root, "Crop");
        string schematicDir = CellFolder.SubFolderPath(cellDir, ViewType.Schematic);
        string layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        var model = new SchematicEditModel { SchematicDirectory = schematicDir };   // no ArtworkSource
        model.Components.Add(Mlin("TL1"));
        model.Components.Add(Mlin("TL2"));

        // The cropped board: 20 × 10 mm of copper at 160 mm from the origin.
        var target = new LayoutView();
        long mm = 1000L * target.DbuPerMicron;
        var board = new Bbox(160 * mm, 150 * mm, 180 * mm, 160 * mm);
        target.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = board.MinX, Y1 = board.MinY, X2 = board.MaxX, Y2 = board.MaxY });

        var run = SchematicToLayoutGenerator.Run(model, target, schematicDir, _root, layoutDir, null, null, null);
        run.Command!.Execute();

        var placed = target.Instances.Select(i => CellHierarchy.InstanceBbox(i, layoutDir)).ToList();
        Assert.Equal(2, placed.Count);
        long row = placed.Max(b => b.MaxY - b.MinY);
        long clear = (board.MaxY - board.MinY) / 10;
        var allowed = new Bbox(board.MinX, board.MinY - clear - row, board.MaxX + placed.Sum(b => b.MaxX - b.MinX), board.MaxY);
        foreach (var bb in placed)
        {
            Assert.True(allowed.Contains(bb.MinX, bb.MinY) && allowed.Contains(bb.MaxX, bb.MaxY), $"{bb} is outside {allowed}");
            Assert.True(bb.MaxY <= board.MinY, "placed over the board rather than under it");
        }
    }

    private sealed class NullSink : IMessageSink
    {
        public void Post(MessageLevel level, string text, string? filePath = null) { }
        public void Clear() { }
    }

    [Fact]
    public void DraggingAPart_CarriesItsGroundVia_ButNotOneMovedByHand()
    {
        var leafDir = CellFolder.SubFolderPath(CellFolder.CreateCellFolder(_root, "Leaf"), ViewType.Layout);
        var leaf = new LayoutView { DbuPerMicron = 1000, SnapDbu = 1000 };
        leaf.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000 });
        LayoutPersistence.SaveToFile(Path.Combine(leafDir, "main.clay"), leaf);

        var vm = new LayoutEditorViewModel(new LayoutView { DbuPerMicron = 1000, SnapDbu = 1000 },
                                           Path.Combine(_root, "Root", "layout", "root.clay"), new NullSink());
        var drill = new LayerKey(2, 0);
        vm.Model.Instances.Add(new LayoutInstance { CellRef = "../../Leaf", SchematicId = "C1", Mag = 1.0 });
        var via = new ViaShape { Layer = drill, X = 2000, Y = 500, PadSize = 300, DrillSize = 150,
                                 Generated = GroundArtwork.ViaTagPrefix + "C1:1 @2000,500" };
        var tie = new PathShape { Layer = new LayerKey(1, 0), Xy = [1000, 500, 2000, 500], Width = 200,
                                  Generated = GroundArtwork.ViaTagPrefix + "C1:1" };
        var mine = new ViaShape { Layer = drill, X = 3000, Y = 3000, PadSize = 300, DrillSize = 150,
                                  Generated = GroundArtwork.ViaTagPrefix + "C1:2 @4000,500" };   // the designer's now
        vm.Model.Shapes.AddRange([via, tie, mine]);

        vm.OnPointerPressed(500, 500, KeyModifiers.None, hitTolDbu: 10);
        vm.OnPointerMoved(10_500, 20_500, leftDown: true, KeyModifiers.None, hitTolDbu: 10);
        vm.OnPointerReleased(10_500, 20_500, KeyModifiers.None);

        Assert.Equal((12_000L, 20_500L), (via.X, via.Y));
        Assert.Equal([11_000L, 20_500, 12_000, 20_500], tie.Xy);
        Assert.Equal(GroundArtwork.ViaTagPrefix + "C1:1 @12000,20500", via.Generated);   // still Update Layout's
        Assert.Equal((3000L, 3000L), (mine.X, mine.Y));

        vm.UndoRedo.Undo();   // one entry
        Assert.Equal((2000L, 500L, 0L), (via.X, via.Y, vm.Model.Instances[0].X));
        Assert.Equal(GroundArtwork.ViaTagPrefix + "C1:1 @2000,500", via.Generated);
    }
}

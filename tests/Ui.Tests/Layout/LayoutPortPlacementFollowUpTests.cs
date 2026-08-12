// Owner follow-up, 2026-08-09, four reports after trying the Port work:
//
//   (A) "change the pin rendering geometry from circle to square (for layout). This matches the pin
//       shape for the symbols in the schematic editor."
//   (B) "Bug: placing a port does not set a direction, when I placed it by clicking on the metal."
//   (C) "Bug: in place-port mode, when I clicked away from the metal, a port was created. I think we
//       should force user to click on an edge (or inside) of metal to create a port."
//   (D) "geometry snap should be used so that its easy for user to place a port exactly at the
//       edge/midpoint of a geometry."
//   (E) "I can't tell from the port glyph where the actual reference plane is."
//
// (C) lives in LayoutPortDirectionTests (it replaced the test that asserted the old behaviour). The
// rest are here. (B) is the load-bearing one and its cause is worth stating: a layout produced by
// "Update Layout from Schematic" holds NO top-level shapes at all — every piece of metal is inside a
// placed PCell instance — and the conductor lookup only ever walked LayoutView.Shapes. So the tool
// found nothing beneath artwork the user could plainly see, and silently seeded no direction.

using Avalonia.Input;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Messages;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Tests.Layout;

public class LayoutPortPlacementFollowUpTests : IDisposable
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;
    private static readonly LayerKey TopCopper = new(1, 0);

    private readonly string _workspaceDir;

    public LayoutPortPlacementFollowUpTests()
    {
        _workspaceDir = Path.Combine(Path.GetTempPath(), "crfPortPlace_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_workspaceDir);
        CellLayoutResolver.InvalidateUnder(_workspaceDir);
    }

    public void Dispose()
    {
        CellLayoutResolver.InvalidateUnder(_workspaceDir);
        if (Directory.Exists(_workspaceDir)) Directory.Delete(_workspaceDir, recursive: true);
    }

    // ── (B) A port placed on an INSTANCE's artwork gets a direction ────────────────────────────

    /// <summary>A cell whose layout is one 20 × 2.9 mm run of metal — §10.7's own hero footprint.</summary>
    private string CreateLineCell(string name)
    {
        var cellDir = CellFolder.CreateCellFolder(_workspaceDir, name);
        string layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        var view = new LayoutView { DbuPerMicron = Dbu, DisplayUnit = LayoutUnit.Um, SnapDbu = 0 };
        view.Shapes.Add(new RectShape { Layer = TopCopper, X1 = 0, Y1 = 0, X2 = 20_000 * Dbu, Y2 = 2_900 * Dbu });
        LayoutPersistence.SaveToFile(Path.Combine(layoutDir, "main.clay"), view);
        return cellDir;
    }

    [Fact]
    public void APortPlacedOnAPlacedInstancesArtwork_GetsADirection()
    {
        CreateLineCell("Line");

        // The parent layout owns NOTHING at top level — exactly the shape "Update Layout from
        // Schematic" produces, and exactly the case the shapes-only lookup could never see.
        var parentDir = CellFolder.CreateCellFolder(_workspaceDir, "Top");
        string parentLayoutDir = CellFolder.SubFolderPath(parentDir, ViewType.Layout);
        string parentClay = Path.Combine(parentLayoutDir, "main.clay");

        var view = new LayoutView { DbuPerMicron = Dbu, DisplayUnit = LayoutUnit.Um, SnapDbu = 0 };
        view.Instances.Add(new LayoutInstance
        {
            CellRef = Path.Combine("..", "..", "Line"), X = 0, Y = 0, Mag = 1.0, Rows = 1, Cols = 1,
        });
        LayoutPersistence.SaveToFile(parentClay, view);

        var vm = new LayoutEditorViewModel(view, parentClay) { ActiveTool = LayoutEditorViewModel.Tool.Port };

        // Click on the low-x end of the instanced metal.
        vm.OnPointerPressed(0, 1_450 * Dbu, default);

        var port = Assert.Single(view.Shapes.OfType<LabelShape>(), l => l.IsPort);
        Assert.Equal(LayoutRotation.R0, port.PortDirection);   // low-x end -> current flows +x̂
    }

    [Fact]
    public void APortPlacedOffAnInstancesArtwork_IsStillRefused()
    {
        CreateLineCell("Line");
        var parentDir = CellFolder.CreateCellFolder(_workspaceDir, "Top2");
        string parentClay = Path.Combine(CellFolder.SubFolderPath(parentDir, ViewType.Layout), "main.clay");

        var view = new LayoutView { DbuPerMicron = Dbu, DisplayUnit = LayoutUnit.Um, SnapDbu = 0 };
        view.Instances.Add(new LayoutInstance
        {
            CellRef = Path.Combine("..", "..", "Line"), X = 0, Y = 0, Mag = 1.0, Rows = 1, Cols = 1,
        });
        LayoutPersistence.SaveToFile(parentClay, view);

        var vm = new LayoutEditorViewModel(view, parentClay) { ActiveTool = LayoutEditorViewModel.Tool.Port };
        vm.OnPointerPressed(50_000 * Dbu, 50_000 * Dbu, default);   // far off the metal

        Assert.Empty(view.Shapes.OfType<LabelShape>());
    }

    // ── (D) Geometry snap lands the port exactly on a feature ─────────────────────────────────

    [Fact]
    public void APortClickedNearAConductorCorner_LandsExactlyOnIt_NotOnTheGrid()
    {
        // The grid is deliberately COARSE (1 mm) and the corner deliberately off it, so grid snap and
        // geometry snap give provably different answers and the test cannot pass by accident.
        var view = new LayoutView { DbuPerMicron = Dbu, DisplayUnit = LayoutUnit.Um, SnapDbu = 1_000 * Dbu };
        view.Shapes.Add(new RectShape { Layer = TopCopper, X1 = 300 * Dbu, Y1 = 200 * Dbu, X2 = 20_300 * Dbu, Y2 = 3_100 * Dbu });

        var vm = new LayoutEditorViewModel(view)
        {
            ActiveTool = LayoutEditorViewModel.Tool.Port,
            GeometrySnapEnabled = true,
        };

        // A few microns off the (300, 200) corner, well inside a 100 µm snap tolerance.
        vm.OnPointerMoved(305 * Dbu, 207 * Dbu, leftDown: false, default, snapTolDbu: 100 * Dbu);
        vm.OnPointerPressed(305 * Dbu, 207 * Dbu, default, 1, 0, 0, 100 * Dbu);

        var port = Assert.Single(view.Shapes.OfType<LabelShape>(), l => l.IsPort);
        Assert.Equal(300 * Dbu, port.X);
        Assert.Equal(200 * Dbu, port.Y);
    }

    [Fact]
    public void WithNothingNearby_ThePortStillLandsOnTheGrid()
    {
        // The non-vacuity guard for the test above: without it, a port that simply followed the raw
        // cursor would pass the geometry-snap case for the wrong reason.
        var view = new LayoutView { DbuPerMicron = Dbu, DisplayUnit = LayoutUnit.Um, SnapDbu = 1_000 * Dbu };
        view.Shapes.Add(new RectShape { Layer = TopCopper, X1 = 0, Y1 = 0, X2 = 20_000 * Dbu, Y2 = 2_900 * Dbu });

        var vm = new LayoutEditorViewModel(view)
        {
            ActiveTool = LayoutEditorViewModel.Tool.Port,
            GeometrySnapEnabled = true,
        };

        vm.OnPointerMoved(10_400 * Dbu, 1_600 * Dbu, leftDown: false, default, snapTolDbu: 5 * Dbu);
        vm.OnPointerPressed(10_400 * Dbu, 1_600 * Dbu, default, 1, 0, 0, 5 * Dbu);

        var port = Assert.Single(view.Shapes.OfType<LabelShape>(), l => l.IsPort);
        Assert.Equal(10_000 * Dbu, port.X);
        Assert.Equal(2_000 * Dbu, port.Y);
    }

    // ── (E) The reference plane is at the conductor END, not at the label anchor ───────────────

    [Theory]
    [InlineData(LayoutRotation.R0,   0L,      1_450L)]   // current +x̂ -> the LOW-x edge
    [InlineData(LayoutRotation.R180, 20_000L, 1_450L)]   // current −x̂ -> the HIGH-x edge
    [InlineData(LayoutRotation.R90,  10_000L, 0L)]       // current +ŷ -> the LOW-y edge
    [InlineData(LayoutRotation.R270, 10_000L, 2_900L)]   // current −ŷ -> the HIGH-y edge
    public void PlaneOf_NamesTheEdgeOppositeTheCurrentDirection(LayoutRotation dir, long xUm, long yUm)
    {
        var bb = new Bbox(0, 0, 20_000 * Dbu, 2_900 * Dbu);
        var (px, py) = LayoutPortDirection.PlaneOf(bb, dir);
        Assert.Equal(xUm * Dbu, px);
        Assert.Equal(yUm * Dbu, py);
    }

    [Fact]
    public void Resolve_PutsThePlaneAtTheConductorEnd_EvenWhenTheLabelSitsWellInside()
    {
        // The owner's own question — "is it the edge of the arrow, or where the line is?" — had no
        // readable answer while the width bar was drawn at whatever point the user happened to click.
        var shapes = new List<LayoutShape>
        {
            new RectShape { Layer = TopCopper, X1 = 0, Y1 = 0, X2 = 20_000 * Dbu, Y2 = 2_900 * Dbu },
        };
        var label = new LabelShape
        {
            Layer = TopCopper, X = 4_000 * Dbu, Y = 1_450 * Dbu, Text = "P1",
            Height = 500 * Dbu, IsPort = true, PortDirection = LayoutRotation.R0,
        };

        var hint = Assert.NotNull(LayoutPortDirection.Resolve(shapes, label));

        Assert.Equal(0, hint.PlaneX);                     // the conductor's low-x edge...
        Assert.Equal(1_450 * Dbu, hint.PlaneY);           // ...centred across its width
        Assert.NotEqual(label.X, hint.PlaneX);            // ...and NOT where the label sits
        Assert.Equal(2_900 * Dbu, hint.WidthDbu);
    }
}

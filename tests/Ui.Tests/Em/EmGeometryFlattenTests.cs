// Owner report, 2026-08-09: "My EM setup in my workspace currently says 'This EM setup is pointed at
// geometry with nothing on a layer bound to a signal conductor...' I don't know why."
//
// His layout (MoMTest/MLin/layout/MLin.clay) held exactly two port labels and ONE instance of a
// generated MLIN cell — and no top-level metal at all, which is what "Update Layout from Schematic"
// produces by construction. Both extractors were handed view.Shapes directly, so they saw two labels,
// classified both as annotation, found zero conductor shapes, and refused. The artwork was on screen
// the whole time; nothing that read it could see it.
//
// The fixture below is that layout's own shape: an empty parent + one instance whose cell holds the
// metal. The negative control beside it is what actually gives the gate teeth — it proves the
// refusal really does fire without the flatten, so this cannot pass for the wrong reason.

using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Em;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Tests.Em;

public class EmGeometryFlattenTests : IDisposable
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;
    private static readonly LayerKey TopCopper = new(1, 0);

    private readonly string _root;

    public EmGeometryFlattenTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crfEmFlatten_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        CellLayoutResolver.InvalidateUnder(_root);
    }

    public void Dispose()
    {
        CellLayoutResolver.InvalidateUnder(_root);
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>The owner's own shape: a cell holding one run of metal, instanced by an otherwise
    /// empty parent that carries only port labels.</summary>
    private (LayoutView Parent, string ParentClay) BuildInstancedLayout()
    {
        var cellDir = CellFolder.CreateCellFolder(_root, "MLIN_gen");
        var cellView = new LayoutView { DbuPerMicron = Dbu, DisplayUnit = LayoutUnit.Mil, SnapDbu = 0 };
        cellView.Shapes.Add(new RectShape { Layer = TopCopper, X1 = 0, Y1 = -533_400, X2 = 10_185_400, Y2 = 533_400 });
        LayoutPersistence.SaveToFile(Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Layout), "main.clay"), cellView);

        var parentDir = CellFolder.CreateCellFolder(_root, "MLin");
        string parentClay = Path.Combine(CellFolder.SubFolderPath(parentDir, ViewType.Layout), "MLin.clay");

        var parent = new LayoutView { DbuPerMicron = Dbu, DisplayUnit = LayoutUnit.Mil, SnapDbu = 0 };
        parent.Shapes.Add(new LabelShape
        {
            Layer = TopCopper, X = 20_711_200, Y = -330_200, Text = "P1",
            Height = 1_016_000, IsPort = true, PortDirection = LayoutRotation.R0,
        });
        parent.Instances.Add(new LayoutInstance
        {
            CellRef = Path.Combine("..", "..", "MLIN_gen"),
            X = 20_711_200, Y = -330_200, Mag = 1.0, Rows = 1, Cols = 1,
        });
        LayoutPersistence.SaveToFile(parentClay, parent);
        return (parent, parentClay);
    }

    [Fact]
    public void AnInstanceOnlyLayout_FlattensToRealConductorGeometry()
    {
        var (parent, parentClay) = BuildInstancedLayout();

        var flat = EmGeometry.Flatten(parent, parentClay);

        // The label survives (ports are top-level), and the instanced metal now exists as a real shape.
        Assert.Contains(flat.Shapes, s => s is LabelShape { IsPort: true });
        var rect = Assert.Single(flat.Shapes.OfType<RectShape>());

        // Placed at the instance origin — the flatten applies the instance transform, it does not
        // just splice the cell's own local coordinates in.
        var bb = LayoutGeometry.BboxOf(rect);
        Assert.Equal(20_711_200, bb.MinX);
        Assert.Equal(20_711_200 + 10_185_400, bb.MaxX);

        Assert.Contains(flat.Notes, n => n.Contains("placed instance", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheUnflattenedShapeList_HasNoConductorAtAll_WhichIsWhyTheSetupRefused()
    {
        // The negative control. Without it the test above could pass against a flatten that did
        // nothing useful, because it would still find the label.
        var (parent, _) = BuildInstancedLayout();

        Assert.DoesNotContain(parent.Shapes, s => s is not LabelShape);
        Assert.NotEmpty(parent.Instances);
    }

    [Fact]
    public void ALayoutWithNoInstances_IsReturnedUntouched_AndSaysNothing()
    {
        var view = new LayoutView { DbuPerMicron = Dbu, SnapDbu = 0 };
        view.Shapes.Add(new RectShape { Layer = TopCopper, X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000 });

        var flat = EmGeometry.Flatten(view, Path.Combine(_root, "x.clay"));

        Assert.Same(view.Shapes, flat.Shapes);   // no copy, no work
        Assert.Empty(flat.Notes);
    }

    [Fact]
    public void AnUnresolvableInstance_IsReportedRatherThanSilentlyContributingNothing()
    {
        var parentDir = CellFolder.CreateCellFolder(_root, "Broken");
        string parentClay = Path.Combine(CellFolder.SubFolderPath(parentDir, ViewType.Layout), "main.clay");

        var parent = new LayoutView { DbuPerMicron = Dbu, SnapDbu = 0 };
        parent.Instances.Add(new LayoutInstance
        {
            CellRef = Path.Combine("..", "..", "NoSuchCell"), X = 0, Y = 0, Mag = 1.0, Rows = 1, Cols = 1,
        });
        LayoutPersistence.SaveToFile(parentClay, parent);

        var flat = EmGeometry.Flatten(parent, parentClay);

        Assert.Empty(flat.Shapes);
        Assert.Contains(flat.Notes, n => n.Contains("could not be resolved", StringComparison.OrdinalIgnoreCase));
    }
}

// Owner report, 2026-08-09: "when I flatten an MLIN component, ports appear in the flatten geometry
// area and it says 3 shapes. This should not happen. Flattening only creates geometry, not any
// labels or ports."
//
// A generated PCell cell carries one IsPort LabelShape per pin (GeneratedCellStore writes the pin
// AND a visible label beside it), so flattening a 1-rect MLIN produced 1 rect + 2 port labels = the
// reported 3. The port labels are the CELL's own terminals; dissolving the cell dissolves them.
//
// The damage is not cosmetic: EmPortExtraction reads the top level's own IsPort labels, so those two
// would have become EM ports of the parent, named "1" and "2" after the PCell's pins, colliding with
// the parent's own P1/P2 numbering — refused by name at Simulate, a long way from the Flatten that
// caused it.

using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Tests.Layout;

public class LayoutFlattenDropsPortsTests : IDisposable
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;
    private static readonly LayerKey TopCopper = new(1, 0);

    private readonly string _root;

    public LayoutFlattenDropsPortsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crfFlattenPorts_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        CellLayoutResolver.InvalidateUnder(_root);
    }

    public void Dispose()
    {
        CellLayoutResolver.InvalidateUnder(_root);
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>A generated-PCell-shaped cell: one run of metal plus one IsPort label per pin, which
    /// is exactly what <c>GeneratedCellStore</c> writes for an MLIN.</summary>
    private string BuildMlinLikeCell(bool withOrdinaryLabel = false)
    {
        var cellDir = CellFolder.CreateCellFolder(_root, "MLIN_gen");
        var view = new LayoutView { DbuPerMicron = Dbu, SnapDbu = 0 };
        view.Shapes.Add(new RectShape { Layer = TopCopper, X1 = 0, Y1 = -500 * Dbu, X2 = 10_000 * Dbu, Y2 = 500 * Dbu });
        view.Shapes.Add(new LabelShape { Layer = TopCopper, X = 0, Y = 0, Text = "1", Height = 200 * Dbu, IsPort = true });
        view.Shapes.Add(new LabelShape { Layer = TopCopper, X = 10_000 * Dbu, Y = 0, Text = "2", Height = 200 * Dbu, IsPort = true });
        if (withOrdinaryLabel)
            view.Shapes.Add(new LabelShape { Layer = TopCopper, X = 5_000 * Dbu, Y = 0, Text = "R1", Height = 200 * Dbu });
        LayoutPersistence.SaveToFile(Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Layout), "main.clay"), view);
        return cellDir;
    }

    private (LayoutView Parent, string BaseDir, LayoutInstance Inst) Placed(bool withOrdinaryLabel = false)
    {
        BuildMlinLikeCell(withOrdinaryLabel);
        var parentDir = CellFolder.CreateCellFolder(_root, "Top");
        string baseDir = CellFolder.SubFolderPath(parentDir, ViewType.Layout);

        var parent = new LayoutView { DbuPerMicron = Dbu, SnapDbu = 0 };
        var inst = new LayoutInstance
        {
            CellRef = Path.Combine("..", "..", "MLIN_gen"),
            X = 0, Y = 0, Mag = 1.0, Rows = 1, Cols = 1,
        };
        parent.Instances.Add(inst);
        LayoutPersistence.SaveToFile(Path.Combine(baseDir, "Top.clay"), parent);
        return (parent, baseDir, inst);
    }

    [Fact]
    public void FlattenOneLevel_DropsThePortLabels_LeavingOnlyGeometry()
    {
        var (_, baseDir, inst) = Placed();

        var flat = LayoutFlatten.FlattenOneLevel(inst, baseDir);

        Assert.NotNull(flat);
        Assert.Single(flat!.Shapes);                                  // the reported "3 shapes" is now 1
        Assert.IsType<RectShape>(flat.Shapes[0]);
        Assert.Empty(flat.Shapes.OfType<LabelShape>());
    }

    [Fact]
    public void FlattenAllLevels_DropsThePortLabelsToo()
    {
        var (_, baseDir, inst) = Placed();

        var flat = LayoutFlatten.FlattenAllLevels(inst, baseDir);

        Assert.Single(flat.Shapes);
        Assert.Empty(flat.Shapes.OfType<LabelShape>());
    }

    [Fact]
    public void ThePreviewedCount_MatchesWhatFlattenActuallyProduces()
    {
        // The outcome count is baked into the menu item's own label, so a count that promised three
        // and delivered one would be a worse bug than the one being fixed.
        var (_, baseDir, inst) = Placed();

        long previewed = LayoutFlatten.CountResultingShapes(inst, baseDir);
        var actual = LayoutFlatten.FlattenAllLevels(inst, baseDir);

        Assert.Equal(actual.Shapes.Count, previewed);
        Assert.Equal(1, previewed);
    }

    [Fact]
    public void TheOneLevelMenuPreview_MatchesWhatFlattenOneLevelProduces()
    {
        // Owner report, 2026-08-09: "the Flatten Hierarchy context menu for MLIN says 3 shapes;
        // Flatten All says 1." The preview read the sub-cell's raw Shapes.Count while the emit had
        // learned to drop the cell's own port labels — two counts, one of them stale.
        var (_, baseDir, inst) = Placed();

        long? previewed = LayoutFlatten.CountOneLevelShapes(inst, baseDir);
        var actual = LayoutFlatten.FlattenOneLevel(inst, baseDir);

        Assert.Equal(actual!.Shapes.Count, previewed);
        Assert.Equal(1L, previewed);
    }

    [Fact]
    public void TheTwoMenuPreviews_AgreeWithEachOther_ForASingleLevelCell()
    {
        // The two numbers the user actually sees side by side in one menu.
        var (_, baseDir, inst) = Placed();

        Assert.Equal(LayoutFlatten.CountResultingShapes(inst, baseDir),
                     LayoutFlatten.CountOneLevelShapes(inst, baseDir));
    }

    [Fact]
    public void TheOneLevelPreview_IsNull_ForAnArray_BecauseThatPreviewCountsInstances()
    {
        var (_, baseDir, inst) = Placed();
        inst.Rows = 5; inst.Cols = 5; inst.PitchX = 20_000 * Dbu; inst.PitchY = 2_000 * Dbu;

        Assert.Null(LayoutFlatten.CountOneLevelShapes(inst, baseDir));
    }

    [Fact]
    public void AnOrdinaryLabel_STILLSurvives_BecauseItIsRealAnnotation()
    {
        // The scope fence. An ordinary label is artwork the author drew, and GerberExport turns one
        // into silkscreen — dropping it here would silently delete silkscreen text from every
        // sub-cell on every Gerber export, which is worse than the bug being fixed.
        var (_, baseDir, inst) = Placed(withOrdinaryLabel: true);

        var flat = LayoutFlatten.FlattenAllLevels(inst, baseDir);

        var label = Assert.Single(flat.Shapes.OfType<LabelShape>());
        Assert.Equal("R1", label.Text);
        Assert.False(label.IsPort);
    }

    [Fact]
    public void APortLabelDrawnAtTheTOPLevel_IsUntouched()
    {
        // Flatten dissolves a CELL. A port the user placed in this layout is this layout's own port
        // and has nothing to do with the instance being flattened.
        var (parent, baseDir, inst) = Placed();
        parent.Shapes.Add(new LabelShape { Layer = TopCopper, X = 0, Y = 0, Text = "P1", Height = 200 * Dbu, IsPort = true });

        var flat = LayoutFlatten.FlattenAllLevels(inst, baseDir);

        Assert.Empty(flat.Shapes.OfType<LabelShape>());               // nothing came UP from the cell
        Assert.Single(parent.Shapes.OfType<LabelShape>());            // the user's own port is untouched
    }
}

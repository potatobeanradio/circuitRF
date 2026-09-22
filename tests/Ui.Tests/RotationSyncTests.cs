using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Update Layout from Schematic and Update Schematic from Layout carry a component's ROTATION across,
/// but only from the side that was turned since the last sync (<see cref="SchematicLayoutOrientation"/>).
/// </summary>
public sealed class RotationSyncTests : IDisposable
{
    private readonly string _root;
    private readonly string _schematicDir, _layoutDir;

    public RotationSyncTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf-rotsync-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        string cellDir = CellFolder.CreateCellFolder(_root, "Amp");
        _schematicDir = CellFolder.SubFolderPath(cellDir, ViewType.Schematic);
        _layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
    }

    public void Dispose()
    {
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

    private SchematicToLayoutGenerator.GenerationResult Forward(SchematicEditModel m, LayoutView l)
    {
        var r = SchematicToLayoutGenerator.Run(m, l, _schematicDir, _root, _layoutDir, null, null, null);
        r.Command?.Execute();
        return r;
    }

    private LayoutToSchematicGenerator.GenerationResult Back(LayoutView l, SchematicEditModel m)
    {
        var r = LayoutToSchematicGenerator.Run(l, m, _layoutDir);
        r.Command?.Execute();
        return r;
    }

    /// <summary>The schematic is Y-down and the layout Y-up, so a symbol at R90 (a clockwise quarter
    /// turn on screen) LOOKS like a placement at 270°. Mirror composes as a reflection, not a flag.</summary>
    [Fact]
    public void Frames_AreRelatedByTheYFlip_AndACarriedMirrorComposesAsAReflection()
    {
        Assert.Equal(270.0, SchematicLayoutOrientation.FromSchematic(90, false).Deg);

        // A placement at 30° whose symbol is then flipped left-right at R0: the placement is flipped
        // in the same world frame — mirrored, and its angle negated.
        var carried = SchematicLayoutOrientation.Carry(
            SchematicLayoutOrientation.FromSchematic(0, true),
            SchematicLayoutOrientation.FromSchematic(0, false),
            new VisualOrientation(false, 30));
        Assert.True(carried.SameAs(new VisualOrientation(true, 330)));
    }

    /// <summary>
    /// A resistor symbol stands upright (pins top and bottom) and its land pattern lies flat (pads left
    /// and right), so "the same angle" put a north–south part east–west. Placement lines the PINS up:
    /// the direction from pin 1 to pin 2 is the same on the board as on the sheet, both ways round.
    /// </summary>
    [Theory]
    [InlineData(SymbolRotation.R0)]
    [InlineData(SymbolRotation.R90)]
    public void APlacedPart_PointsItsPinsTheWayTheSymbolDoes_BothDirections(SymbolRotation rot)
    {
        var tech = ShippedTechnologies.Load("pcb-2layer_FR-4_70mil_1oz");
        Directory.CreateDirectory(Path.Combine(_root, "tech"));
        TechPersistence.SaveToFile(Path.Combine(_root, "tech", "t.ctech"), tech);
        WorkspacePersistence.SaveToFile(Path.Combine(_root, ".cws"), new CwsFile { DefaultTechRef = "tech/t.ctech" });

        var schematic = new SchematicEditModel { SchematicDirectory = _schematicDir };
        var r1 = new EditableComponent { InstanceName = "R1", Symbol = SymbolKind.Resistor, Rotation = rot };
        r1.Parameters.Add(new EditableParameter { Name = "Footprint", Expression = "smt:0402@N" });
        schematic.Components.Add(r1);
        var layout = new LayoutView();
        SchematicToLayoutGenerator.Run(schematic, layout, _schematicDir, _root, _layoutDir, tech,
                                       Path.Combine(_root, "tech", "t.ctech"), null).Command!.Execute();

        var (sx, sy) = SchematicPinDirection(r1);
        var (lx, ly) = LayoutPinDirection(layout.Instances[0], tech);
        Assert.Equal((Math.Sign(sx), -Math.Sign(sy)), (Math.Sign(lx), Math.Sign(ly)));   // the sheet is Y-down

        // …and the reverse: a component created from that placement faces the way the symbol did.
        // Unlinked, and declared a resistor the way a part dropped from the Library palette is — a
        // bare land pattern is artwork and creates nothing.
        layout.Instances[0].SchematicId = null;
        layout.Instances[0].PartKind = nameof(SymbolKind.Resistor);
        var fresh = new SchematicEditModel { SchematicDirectory = _schematicDir };
        LayoutToSchematicGenerator.Run(layout, fresh, _layoutDir, tech).Command!.Execute();
        Assert.Equal(rot, fresh.Components.Single().Rotation);
    }

    private static (double X, double Y) SchematicPinDirection(EditableComponent c)
    {
        var ports = c.ToRenderComponent().Ports;
        var a = SchematicGeometry.LocalToWorld(ports[0].LocalX, ports[0].LocalY, c.X, c.Y, c.Rotation, c.MirrorX);
        var b = SchematicGeometry.LocalToWorld(ports[1].LocalX, ports[1].LocalY, c.X, c.Y, c.Rotation, c.MirrorX);
        return (b.X - a.X, b.Y - a.Y);
    }

    private (long X, long Y) LayoutPinDirection(LayoutInstance inst, Technology tech)
    {
        var pins = CellPins.Resolve(CellLayoutResolver.Resolve(inst.CellRef, _layoutDir).View!, tech);
        var p1 = pins.Single(p => p.Name == "1");
        var p2 = pins.Single(p => p.Name == "2");
        var a = LayoutInstanceTransform.TransformPoint(p1.X, p1.Y, inst, 0, 0);
        var b = LayoutInstanceTransform.TransformPoint(p2.X, p2.Y, inst, 0, 0);
        return (b.X - a.X, b.Y - a.Y);
    }

    [Fact]
    public void ATurnOnEitherSide_IsCarried_AndATurnOnTheOtherSideIsNotReverted()
    {
        var schematic = new SchematicEditModel { SchematicDirectory = _schematicDir };
        var comp = Mlin("MLIN1");
        comp.Rotation = SymbolRotation.R90;
        schematic.Components.Add(comp);
        var layout = new LayoutView();

        // Placed facing the symbol.
        Forward(schematic, layout);
        Assert.Equal(270.0, layout.Instances[0].RotationDegrees);

        // Turned in the schematic → carried to the layout, as one undoable edit.
        comp.Rotation = SymbolRotation.R180;
        var turned = Forward(schematic, layout);
        Assert.Equal(180.0, layout.Instances[0].RotationDegrees);
        Assert.Contains(turned.Lines, l => l.Text.Contains("rotation changed from 270° to 180° (from schematic)"));
        turned.Command!.Undo();
        Assert.Equal(270.0, layout.Instances[0].RotationDegrees);
        turned.Command!.Execute();

        // Turned only in the layout → Update Layout leaves it alone…
        layout.Instances[0].RotationDegrees = 90;
        var pushed = Forward(schematic, layout);
        Assert.Null(pushed.Command);
        Assert.Equal(90.0, layout.Instances[0].RotationDegrees);

        // …and Update Schematic carries it back: 180° → 90° is a clockwise quarter turn, R180 → R270.
        var back = Back(layout, schematic);
        Assert.Equal(SymbolRotation.R270, comp.Rotation);
        Assert.Contains(back.Lines, l => l.Text.Contains("rotation changed from R180 to R270 (from layout)"));

        // Nothing left to carry either way.
        Assert.Null(Forward(schematic, layout).Command);
        Assert.Null(Back(layout, schematic).Command);
    }

    /// <summary>A placement made before rotations were linked: nothing says which side's orientation
    /// is intended, so neither turns — the link is recorded and the next turn is carried.</summary>
    [Fact]
    public void AnUnlinkedPair_IsLinkedWithoutTurningEitherSide()
    {
        var schematic = new SchematicEditModel { SchematicDirectory = _schematicDir };
        var comp = Mlin("MLIN1");
        schematic.Components.Add(comp);
        var layout = new LayoutView();
        Forward(schematic, layout);

        layout.Instances[0].OrientationLink = null;          // as an older .clay has it
        comp.Rotation = SymbolRotation.R90;

        var first = Forward(schematic, layout);
        Assert.Equal(0.0, layout.Instances[0].RotationDegrees);
        Assert.True(first.OrientationLinksRecorded);
        Assert.Contains(first.Lines, l => l.InstanceName == "" && l.Text.Contains("never been synced"));

        // The link is what a later session carries from, so it has to survive the .clay.
        string path = Path.Combine(_layoutDir, "Amp.clay");
        LayoutPersistence.SaveToFile(path, layout);
        layout = LayoutPersistence.LoadFromFile(path);
        Assert.Equal(90, layout.Instances[0].OrientationLink!.SchematicDeg);

        comp.Rotation = SymbolRotation.R180;
        Forward(schematic, layout);
        Assert.Equal(270.0, layout.Instances[0].RotationDegrees);   // 0° turned a further quarter clockwise
    }
}

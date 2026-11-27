// Owner: PCD-6 (brief-pcd-6 §4). The gestures, through the view model, on the other lanes' real bodies.

using System.Text.RegularExpressions;
using CircuitRF.Design.PCellDesigner;
using CircuitRF.Ui.PCellDesigner;
using CircuitRF.Ui.Tests.SchematicSync;

namespace CircuitRF.Ui.Tests.PCellDesigner;

public sealed class Pcd6GestureTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "crf-pcd6g-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { /* best-effort */ }
    }

    private static string Expression(SchematicEditModel m, string part, string name) =>
        m.Components.Single(c => c.InstanceName == part).Parameters.Single(p => p.Name == name).Expression;

    /// <summary>R-pcd6-4: Apply merges the text by designator as one undo step; a refused text changes nothing.</summary>
    [Fact]
    public void TextApply_MergesByDesignator_AndARefusalChangesNothing()
    {
        var (vm, _) = Pcd6Fixture.Open(_scratch);
        string text = vm.CnlText;
        Assert.Contains("MLIN:TL3", text);

        vm.CnlText = Regex.Replace(text, @"^(MLIN:TL3\b.*?\bL=)7( mm)", "${1}9${2}", RegexOptions.Multiline);
        Assert.True(vm.ApplyText(), vm.Status);
        Assert.Equal("9", Expression(vm.Schematic, "TL3", "L"));
        Assert.True(vm.CanUndo);

        var before = SchematicPersistence.Serialize(vm.Schematic);
        vm.CnlText = Regex.Replace(vm.CnlText, @"^MLIN:TL3\s+(\S+)\s+(\S+).*$", "R:TL3 $1 $2 R=50 Ohm", RegexOptions.Multiline);
        Assert.False(vm.ApplyText());
        Assert.StartsWith("Apply:", vm.Status);
        Assert.Equal(before, SchematicPersistence.Serialize(vm.Schematic));

        vm.Undo();
        Assert.Equal("7", Expression(vm.Schematic, "TL3", "L"));
        vm.Redo();
        Assert.Equal("9", Expression(vm.Schematic, "TL3", "L"));
    }

    /// <summary>R-pcd6-2: a Library part dropped on a free pin joins it (PinDrop), is planned onto the canvas, and is one
    /// undo step.</summary>
    [Fact]
    public async Task DropOnAPin_AddsThePartJoined_AsOneUndoStep()
    {
        var (vm, _) = Pcd6Fixture.Open(_scratch, edit: m => m.Components.RemoveAll(c => c.InstanceName is "P2" or "GND2"));
        await vm.DesignAsync(0);
        var part = vm.PartFor(SymbolKind.Mlin, 2);

        Assert.True(vm.DropPart(part, 1, "TL3", 2), vm.Status);
        Assert.Contains(vm.Schematic.Components, c => c.InstanceName == part.InstanceName);
        await vm.DesignAsync(0);
        var end = vm.PinPoseOf("TL3", 2)!.Value;
        var start = vm.PinPoseOf(part.InstanceName, 1)!.Value;
        Assert.Equal((end.X, end.Y), (start.X, start.Y));

        vm.Undo();
        Assert.DoesNotContain(vm.Schematic.Components, c => c.InstanceName == part.InstanceName);
        Assert.False(vm.CanUndo);
    }

    /// <summary>R-pcd6-2: dragging an offset part writes the change into its offset's expressions, keeping the parameter
    /// term, and the re-plan puts its pin where it was dropped.</summary>
    [Fact]
    public async Task OffsetDrag_WritesAnExpression_AndThePinLandsThere()
    {
        var (vm, _) = Pcd6Fixture.Open(_scratch, "O1-offset-gap");
        await vm.DesignAsync(0);
        var now = vm.PinPoseOf("TL2", 1)!.Value;
        long um = vm.Layout!.DbuPerMicron;
        var anchor = vm.PinPoseOf("TL1", 2)!.Value;
        double rad = anchor.OutwardDeg * Math.PI / 180;
        long toX = now.X + (long)Math.Round(200 * um * Math.Cos(rad)), toY = now.Y + (long)Math.Round(200 * um * Math.Sin(rad));

        Assert.True(vm.DragOffset("TL2", toX, toY), vm.Status);
        var offset = vm.Schematic.Components.Single(c => c.InstanceName == "TL2").Offset!;
        Assert.StartsWith("g", offset.Dx);
        Assert.NotEqual("g", offset.Dx);
        await vm.DesignAsync(0);
        var landed = vm.PinPoseOf("TL2", 1)!.Value;
        Assert.Equal((toX, toY), (landed.X, landed.Y));
        Assert.True(vm.CanUndo);
    }

    /// <summary>R-pcd6-2: Stretch by Parameter on selected MSHAPE vertices binds them to the parameter (the canvas follows
    /// its value), and a vertex drag adds its change to the vertex's expressions; each is one undo step.</summary>
    [Fact]
    public async Task StretchAndVertexDrag_WriteExpressions_TheCanvasFollows()
    {
        var (vm, _) = Pcd6Fixture.Open(_scratch, edit: m =>
        {
            m.Components.RemoveAll(c => c.InstanceName is "P2" or "GND2");
            var pad = new EditableComponent { Symbol = SymbolKind.MShape, InstanceName = "SH1",
                                              Offset = new OffsetPlacement { Anchor = "TL3.2", Pin = 1 } };
            foreach (var d in ComponentTypeRegistry.DefaultParameters(SymbolKind.MShape, 1))
                pad.Parameters.Add(new EditableParameter { Name = d.Name, Expression = d.Expression, Unit = d.Unit, Dimension = d.Dimension });
            m.Components.Add(pad);
        });
        vm.AddParameter();   // P1 = 100 um
        await vm.DesignAsync(0);
        long um = vm.Layout!.DbuPerMicron;
        static double Span((int Vertex, long X, long Y) a, (int Vertex, long X, long Y) b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
        var before = vm.VerticesOf("SH1");
        Assert.True(before.Count == 4, vm.LayoutNote);

        Assert.True(vm.StretchByParameter("SH1", [2, 3], "P1", 0), vm.Status);
        Assert.Equal("P1", Assert.Single(vm.Schematic.Components.Single(c => c.InstanceName == "SH1").Stretch!).Parameter);
        vm.Parameters.Single(p => p.Name == "P1").Value = "150 um";
        await vm.DesignAsync(0);
        var stretched = vm.VerticesOf("SH1");
        Assert.Equal(Span(before[0], before[1]) + 50 * um, Span(stretched[0], stretched[1]), 0);
        Assert.Equal((before[0].X, before[0].Y), (stretched[0].X, stretched[0].Y));   // the anchor stays

        var v4 = stretched[3];
        Assert.True(vm.DragVertex("SH1", 4, v4.X, v4.Y, v4.X + 10 * um, v4.Y + 20 * um), vm.Status);
        await vm.DesignAsync(0);
        var dragged = vm.VerticesOf("SH1")[3];
        Assert.Equal((v4.X + 10 * um, v4.Y + 20 * um), (dragged.X, dragged.Y));

        vm.Undo();
        vm.Undo();
        Assert.Null(vm.Schematic.Components.Single(c => c.InstanceName == "SH1").Stretch);
    }

    /// <summary>R-pcd6-8 and R-pcd6-1: Export saves and packages the cell; Import puts it into another workspace as a
    /// designed cell; Tools ▸ PCell Designer… makes PCell1, then PCell2.</summary>
    [Fact]
    public void ExportImport_AndANewCell()
    {
        var (vm, _) = Pcd6Fixture.Open(Path.Combine(_scratch, "a"));
        string package = Path.Combine(_scratch, "C1.cpcell");
        Assert.StartsWith("Exported", vm.Export(package));
        Assert.True(File.Exists(package));

        string other = SdlChecker.Prepare("C4-bends", Path.Combine(_scratch, "b")).Workspace;
        var (cell, status) = PCellDesignerViewModel.Import(package, other);
        Assert.True(cell is not null, status);
        Assert.True(PCellDesign.IsDesigned(cell));
        Assert.Equal(vm.CellName, Path.GetFileName(cell));

        Assert.Equal("PCell1", Path.GetFileName(PCellDesignerViewModel.NewCell(other).CellDir));
        var second = PCellDesignerViewModel.NewCell(other).CellDir;
        Assert.Equal("PCell2", Path.GetFileName(second));
        Assert.True(PCellDesign.IsDesigned(second));
        Assert.Empty(new PCellDesignerViewModel(second, other).Parameters);
    }
}

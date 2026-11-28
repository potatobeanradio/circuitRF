// Owner: PCD-11 (brief-pcd-11 §3). Headless, through the view model.

using CircuitRF.Design.PCellDesigner;
using CircuitRF.Ui.PCellDesigner;
using CircuitRF.Ui.Tests.SchematicSync;
using CircuitRF.Ui.Tests.Layout.PCells;

namespace CircuitRF.Ui.Tests.PCellDesigner;

[Collection(PCellResolverCollection.Name)]
public sealed class Pcd11AuthoringTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "crf-pcd11-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { /* best-effort */ }
    }

    private static void Near((long X, long Y) want, PinPose got, long tolerance = 20) =>
        Assert.True(Math.Abs(got.X - want.X) <= tolerance && Math.Abs(got.Y - want.Y) <= tolerance, $"want {want}, got ({got.X}, {got.Y})");

    /// <summary>R-pcd11-1: a new cell has Port 1 and its ground; the first drop joins it; + Port adds Port 2 on the free end.</summary>
    [Fact]
    public async Task ANewCell_HasPort1_TheFirstDropJoinsIt_AndPlusPortAddsPort2()
    {
        string ws = SdlChecker.Prepare("C1-through-line", _scratch).Workspace;
        var cell = PCellDesign.Create(ws, "Fresh").CellDir;
        var vm = new PCellDesignerViewModel(cell, ws);
        Assert.Contains(vm.Schematic.Components, c => c.Symbol == SymbolKind.Term && c.InstanceName == "P1");
        Assert.Contains(vm.Schematic.Components, c => c.Symbol == SymbolKind.Ground);

        await vm.DesignAsync(0);
        Assert.True(vm.DropPartAt(vm.PartFor(SymbolKind.Mlin, 2), 0, 0), vm.Status);
        Assert.Empty(PCellAuthoring.FreePins(vm.Schematic, ports: true));   // P1 is joined

        Assert.True(vm.AddPort(), vm.Status);
        Assert.Equal([1, 2], PCellDesign.TerminalsOf(vm.Schematic).Select(t => t.Port));
        Assert.Empty(PCellAuthoring.FreePins(vm.Schematic));
        await vm.DesignAsync(0);
        Assert.NotNull(vm.Layout);
    }

    /// <summary>R-pcd11-2: a part dropped on empty canvas is placed by an offset from the nearest pin, in literals of the
    /// cell's length unit, and lands where it was dropped.</summary>
    [Fact]
    public async Task ADropOnEmptyCanvas_IsAnOffsetFromTheNearestPin()
    {
        var (vm, _) = Pcd6Fixture.Open(_scratch);
        await vm.DesignAsync(0);
        long um = vm.Layout!.DbuPerMicron;
        var end = vm.PinPoseOf("TL3", 2)!.Value;
        (long X, long Y) at = (end.X + 1500 * um, end.Y + 700 * um);

        var part = vm.PartFor(SymbolKind.Mlin, 2);
        Assert.True(vm.DropPartAt(part, at.X, at.Y), vm.Status);
        Assert.Contains("TL3 pin 2", vm.Status);
        var offset = vm.Schematic.Components.Single(c => c.InstanceName == part.InstanceName).Offset!;
        Assert.Equal(("TL3.2", "mm", "0"), (offset.Anchor, offset.Unit, offset.Angle));
        Assert.True(double.TryParse(offset.Dx, System.Globalization.CultureInfo.InvariantCulture, out _));

        await vm.DesignAsync(0);
        // The plan may move as a whole when a part is added; the drop is where it was relative to its anchor.
        var anchor = vm.PinPoseOf("TL3", 2)!.Value;
        Near((anchor.X + at.X - end.X, anchor.Y + at.Y - end.Y), vm.PinPoseOf(part.InstanceName, 1)!.Value);
        vm.Undo();
        Assert.DoesNotContain(vm.Schematic.Components, c => c.InstanceName == part.InstanceName);
    }

    /// <summary>R-pcd11-3: a drop on a joint makes a tee (the two lines on its through pins, the new part on its branch); a
    /// second drop there makes it a cross; a joint with a port is refused with a sentence.</summary>
    [Fact]
    public async Task ADropOnAJoint_MakesATee_ThenACross_AndAPortJointIsRefused()
    {
        var (vm, _) = Pcd6Fixture.Open(_scratch);
        await vm.DesignAsync(0);

        var stub = vm.PartFor(SymbolKind.Mlin, 2);
        Assert.True(vm.DropPart(stub, 1, "TL1", 2), vm.Status);
        var tee = Assert.Single(vm.Schematic.Components, c => c.Symbol == SymbolKind.MTee);
        await vm.DesignAsync(0);
        Assert.NotNull(vm.Layout);
        var branch = vm.PinPoseOf(tee.InstanceName, 3)!.Value;
        Near((branch.X, branch.Y), vm.PinPoseOf(stub.InstanceName, 1)!.Value, 2);

        Assert.True(vm.DropPart(vm.PartFor(SymbolKind.Mlin, 2), 1, "TL1", 2), vm.Status);
        Assert.DoesNotContain(vm.Schematic.Components, c => c.Symbol == SymbolKind.MTee);
        Assert.Single(vm.Schematic.Components, c => c.Symbol == SymbolKind.MCross);

        var before = SchematicPersistence.Serialize(vm.Schematic);
        Assert.False(vm.DropPart(vm.PartFor(SymbolKind.Mlin, 2), 1, "TL1", 1));
        Assert.Contains("no width", vm.Status);
        Assert.Equal(before, SchematicPersistence.Serialize(vm.Schematic));
    }

    /// <summary>R-pcd11-4: corners clicked from a free pin make an MSHAPE joined to it, its corners relative to pin 1 and
    /// planned where they were clicked, its Model "open"; started away from every pin, it is placed by an offset.</summary>
    [Fact]
    public async Task ADrawnShape_JoinsTheNearestPin_OrIsPlacedByOffset()
    {
        var (vm, _) = Pcd6Fixture.Open(_scratch, edit: m => m.Components.RemoveAll(c => c.InstanceName is "P2" or "GND2"));
        await vm.DesignAsync(0);
        long um = vm.Layout!.DbuPerMicron;
        var end = vm.PinPoseOf("TL3", 2)!.Value;
        double rad = end.OutwardDeg * Math.PI / 180;
        (long, long) At(double along, double across) =>
            (end.X + (long)Math.Round((along * Math.Cos(rad) - across * Math.Sin(rad)) * um),
             end.Y + (long)Math.Round((along * Math.Sin(rad) + across * Math.Cos(rad)) * um));
        var corners = new[] { At(0, -400), At(300, -400), At(300, 400), At(0, 400) };

        Assert.True(vm.DrawShape([At(0, 0), .. corners], 10 * um), vm.Status);
        var shape = Assert.Single(vm.Schematic.Components, c => c.Symbol == SymbolKind.MShape);
        Assert.Null(shape.Offset);
        Assert.Equal("\"open\"", shape.Parameters.Single(p => p.Name == DrawnMetal.Model).Expression);
        Assert.Equal("mm", shape.Parameters.Single(p => p.Name == "X[2]").Unit);
        await vm.DesignAsync(0);
        var planned = vm.VerticesOf(shape.InstanceName);
        foreach (var (want, got) in new[] { At(0, 0) }.Concat(corners).Zip(planned))
            Near(want, new PinPose(got.X, got.Y, 0));

        Assert.True(vm.DrawShape([At(2000, 0), At(2300, 0), At(2300, 300)], 10 * um), vm.Status);
        Assert.Contains(vm.Schematic.Components, c => c.Symbol == SymbolKind.MShape && c.Offset is not null);
    }
}

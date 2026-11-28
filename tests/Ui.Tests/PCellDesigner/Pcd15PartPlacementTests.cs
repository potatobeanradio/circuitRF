// Owner: PCD-15. The Part pane: the selected part's offset and flip, set in the designer instead of in the .csch.

using CircuitRF.Ui.Tests.Layout.PCells;

namespace CircuitRF.Ui.Tests.PCellDesigner;

[Collection(PCellResolverCollection.Name)]
public sealed class Pcd15PartPlacementTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "crf-pcd15-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task ThePartPane_SetsTheOffsetAndTheFlip_AsOneUndoStepEach_AndThePlanFollows()
    {
        var (vm, _) = Pcd6Fixture.Open(_scratch, edit: m =>
        {
            var pad = new EditableComponent
            {
                Symbol = SymbolKind.MShape, InstanceName = "SH1", X = 9000, Y = 3000,   // clear of every wire: no net joins it
                Offset = new OffsetPlacement { Anchor = "TL3.2", Pin = 1, Dx = "-3", Unit = "mm" },
            };
            foreach (var d in ComponentTypeRegistry.DefaultParameters(SymbolKind.MShape, 1))
                pad.Parameters.Add(new EditableParameter { Name = d.Name, Expression = d.Expression, Unit = d.Unit, Dimension = d.Dimension });
            m.Components.Add(pad);
        });
        await vm.DesignAsync(0);
        var before = vm.Layout!.Instances.Single(i => i.SchematicId == "SH1");

        vm.Selection.Add("SH1");
        Assert.Equal(("SH1", true, "-3", "mm"), (vm.PartName, vm.PartHasOffset, vm.PartDx, vm.PartOffsetUnit));
        vm.PartDx = "-3 + L1/5";   // L1 = 5 mm: 1 mm on
        Assert.True(vm.ApplyPart(), vm.Status);
        Assert.Equal("-3 + L1/5", vm.Schematic.Components.Single(c => c.InstanceName == "SH1").Offset!.Dx);
        await vm.DesignAsync(0);
        var after = vm.Layout!.Instances.Single(i => i.SchematicId == "SH1");
        long um = vm.Layout.DbuPerMicron;
        long moved = Math.Abs(after.X - before.X) + Math.Abs(after.Y - before.Y);
        Assert.True(moved == 1000 * um, $"moved {moved}: {vm.LayoutNote} / {vm.Status}");

        vm.PartDy = "1 +";
        Assert.False(vm.ApplyPart());   // not an expression: refused, nothing pushed

        vm.Selection.Clear();
        vm.Selection.Add("TL2");
        Assert.False(vm.PartHasOffset);
        vm.PartMirror = true;
        Assert.True(vm.ApplyPart(), vm.Status);
        Assert.True(vm.Schematic.Components.Single(c => c.InstanceName == "TL2").MirrorX);

        vm.Undo();
        Assert.False(vm.PartMirror);
        vm.Undo();
        vm.Selection.Clear();
        vm.Selection.Add("SH1");
        Assert.Equal("-3", vm.PartDx);
    }
}

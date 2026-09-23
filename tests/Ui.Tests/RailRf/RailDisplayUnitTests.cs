// ================================================================
//  RailDisplayUnitTests.cs — the `.crail`'s own display unit (2026-09-23)
//
//  railRF printed every length in the `.clay`'s display unit, so reading a rail report in another
//  unit meant changing a layout document. The unit is now the `.crail`'s: seeded from the artwork,
//  persisted, and never written back to the layout the board canvas shares with the layout editor.
// ================================================================

using CircuitRF.Design.Layout;
using CircuitRF.Design.RailRf;
using CircuitRF.Ui.RailRf;
using Xunit;
using static CircuitRF.Ui.Tests.RailRf.RailLayerVisibilityTests;

namespace CircuitRF.Ui.Tests.RailRf;

public class RailDisplayUnitTests
{
    /// <summary>An unseeded document takes the artwork's unit, and that is not an edit.</summary>
    [Fact]
    public void AnUnseededDocumentTakesTheArtworksUnitWithoutBecomingDirty()
    {
        var vm = Window(OneRail());
        Assert.False(vm.IsDirty);

        vm.Board = Board(new LayoutView { DisplayUnit = LayoutUnit.Mil });

        Assert.Equal(LayoutUnit.Mil, vm.DisplayUnit);
        Assert.Equal(LayoutUnit.Mil, vm.BoardLengthFormat().Unit);
        Assert.False(vm.IsDirty);
    }

    /// <summary>
    /// Picking a unit re-spells the window, dirties and persists the <c>.crail</c> — and the
    /// <c>.clay</c>'s model, shared with the layout editor through the board canvas, keeps its own.
    /// </summary>
    [Fact]
    public void PickingAUnitIsTheDocumentsAndNeverReachesTheLayout()
    {
        var view = new LayoutView { DisplayUnit = LayoutUnit.Mil };
        var vm = Window(OneRail());
        vm.Board = Board(view);

        vm.DisplayUnit = LayoutUnit.Mm;

        Assert.Equal("1 mm", vm.BoardLengthFormat().Length(1_000_000));
        Assert.Equal(LayoutUnit.Mm, vm.BoardLayout!.DisplayUnit);   // the rulers and cursor readout
        Assert.Equal(LayoutUnit.Mil, view.DisplayUnit);             // the .clay is untouched
        Assert.False(vm.BoardLayout.IsDirty);
        Assert.True(vm.IsDirty);

        var reread = RailDocumentIo.Deserialize(RailDocumentIo.Serialize(vm.Document));
        Assert.Equal(LayoutUnit.Mm, reread.DisplayUnit);

        // …and once stated, a unit picked in the layout editor changes nothing here.
        view.DisplayUnit = LayoutUnit.Um;
        vm.RefreshIfUnitChanged();
        Assert.Equal(LayoutUnit.Mm, vm.BoardLengthFormat().Unit);
    }

    private static RailBoardInputs Board(LayoutView view) =>
        new() { Shapes = [], Technology = TwoLayerTech(), View = view, DbuPerMicron = view.DbuPerMicron };
}

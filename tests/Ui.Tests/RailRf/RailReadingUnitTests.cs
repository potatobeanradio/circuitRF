// ================================================================
//  RailReadingUnitTests.cs — the shipped example, opened.
//
//  Two reports on one document (owner, 2026-09-20):
//
//    1. The reference combo on `Sensor board.crail` is outlined in the warning colour whatever the
//       user sets it to.
//    2. The sentence beside it names a rail called `rail at (30058230, 12324568) DBU` — a coordinate
//       in a STORAGE unit, in a window whose rule is that nothing reads in DBU.
//
//  They are one story: the open path never set `RailBoardInputs.View`, so the window had no display
//  unit and fell back to raw DBU; and the gate's refusal about ANOTHER rail flagged the combo of the
//  rail on screen, which was correctly set.
//
//  One test per claim.
// ================================================================

using System;
using System.IO;
using System.Linq;
using CircuitRF.Design.Layout;
using CircuitRF.Design.RailRf;
using CircuitRF.Ui.RailRf;
using Xunit;

namespace CircuitRF.Ui.Tests.RailRf;

public sealed class RailReadingUnitTests
{
    /// <summary>The opened example reads in the ARTWORK's own unit, never in DBU.</summary>
    [Fact]
    public void OpeningACrailTakesTheArtworksDisplayUnit()
    {
        var vm = Example();

        Assert.NotNull(vm.Board);
        Assert.NotNull(vm.Board!.View);
        Assert.False(vm.BoardLengthFormat().IsRawDbu,
                     "the window fell back to DBU although the document names artwork that states a unit.");
        Assert.Equal(LayoutUnit.Um, vm.BoardLengthFormat().Unit);

        // The name a pour pick would bake into the document is the reading, not the storage.
        string name = $"rail at {vm.BoardLengthFormat().Point(30058230, 12324568)}";
        Assert.DoesNotContain("DBU", name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A rail the window is NOT showing turns the rail selector red, not the reference combo.
    /// </summary>
    [Fact]
    public void AnUnreferencedRailElsewhereFlagsTheSelector_NotTheCombo()
    {
        var vm = Example();
        vm.PickRail("+3V3");
        vm.ConfirmReferenceCommand.Execute(null);
        Assert.True(vm.IsReferenceConfirmed);

        vm.PickRailAt(30058230, 12324568);                 // a pour pick: states no reference layer
        vm.SelectedRailName = "+3V3";                      // …and the user is looking at the good one

        Assert.False(vm.CanRun);
        Assert.True(vm.IsRailSelectorFlagged,
                    "the remedy is in the selector — show that rail, or remove it.");
        Assert.False(vm.IsReferenceLayerFlagged,
                     "the combo on screen belongs to the selected rail and is correctly set.");
        Assert.DoesNotContain("DBU", vm.RunBlockedReason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The rail on screen with an unconfirmed reference DOES flag the combo.</summary>
    [Fact]
    public void TheSelectedRailsOwnUnconfirmedReferenceFlagsTheCombo()
    {
        var vm = Example();
        vm.PickRailAt(30058230, 12324568);                 // selected, and states no reference layer

        Assert.False(vm.IsReferenceConfirmed);
        Assert.False(vm.CanRun);
        Assert.True(vm.IsReferenceLayerFlagged);
        Assert.False(vm.IsRailSelectorFlagged);
    }

    private static RailRfViewModel Example()
    {
        string crail = Path.Combine(
            RepoRoot(), "examples", "Power Rail", "Sensor board", "Sensor board.crail");
        Assert.True(File.Exists(crail), $"The shipped example is not at {crail}.");

        var vm = new RailRfViewModel(RailDocumentIo.LoadFromFile(crail), crail);
        Assert.Empty(vm.LoadDocumentReferences());
        vm.RebuildParts();
        return vm;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}

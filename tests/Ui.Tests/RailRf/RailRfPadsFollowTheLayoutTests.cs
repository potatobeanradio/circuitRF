// ================================================================
//  RailRfPadsFollowTheLayoutTests.cs
//
//  docs/sonnet-briefs/brief-railrf-28-pads-follow-the-layout.md. An edit in the layout window next
//  door re-read the shapes and never the pads, so a part moved or turned there left railRF seeding
//  its walks from where the pins used to be. One test per claim, on field report 4's board.
//
//  Every edit here goes through NotifyArtworkChanged — the signal the layout editor raises — and
//  never through `Board = …`, which is a record and short-circuits on an equal value.
// ================================================================

using System;
using System.Linq;
using System.Threading.Tasks;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Extraction;
using CircuitRF.Design.Schematic;
using CircuitRF.Ui.RailRf;
using Xunit;

namespace CircuitRF.Ui.Tests.RailRf;

public sealed class RailRfPadsFollowTheLayoutTests : IDisposable
{
    private readonly RailRfFieldReport4Tests _board = new();

    public void Dispose() => _board.Dispose();

    private static long Mm(double v) => RailRfFieldReport4Tests.Mm(v);

    /// <summary>A window on the board whose settle the test releases by hand.</summary>
    private (RailRfViewModel Vm, TaskCompletionSource Settle) Open(bool turnThird)
    {
        var vm = RailRfFieldReport4Tests.OpenWindowOn(_board.ThreeCapBoard(SymbolKind.Capacitor, turnThird));
        var settle = new TaskCompletionSource();
        vm.SettlePadRead = (_, _) => settle.Task;
        return (vm, settle);
    }

    /// <summary>
    /// <b>A part moved in the layout moves its pads, once the edit settles</b> — and the pick survives
    /// the refresh, because its net still exists (R-rail28-2).
    /// </summary>
    [Fact]
    public async Task AMovedPartsPadsFollowItOnceTheEditSettles()
    {
        var (vm, settle) = Open(turnThird: false);
        var before = Assert.Single(vm.Board!.Pads, p => p.Refdes == "C1" && p.Pin == "1");
        vm.SelectNet("+3V3");

        vm.Board.View!.Instances[0].X += Mm(1);
        vm.NotifyArtworkChanged(LayoutChangeInfo.InstancesOnly);

        // Pending: the old pads stand, and the strip says they are being re-read.
        Assert.True(vm.IsReadingParts);
        Assert.Contains("re-reading the board's parts", vm.StatusLine, StringComparison.Ordinal);
        Assert.Contains(vm.Board.Pads, p => p == before);

        settle.SetResult();
        await vm.PadRead!;

        var after = Assert.Single(vm.Board!.Pads, p => p.Refdes == "C1" && p.Pin == "1");
        Assert.Equal((before.X + Mm(1), before.Y), (after.X, after.Y));
        Assert.False(vm.IsReadingParts);
        Assert.Equal("+3V3", vm.SelectedNetName);
    }

    /// <summary>
    /// <b>A part turned by hand in the layout leaves the turned-parts list</b>, with no Turn press —
    /// and the Turn button is held while the re-read is pending, since the list it would act on still
    /// names the part the user has just turned.
    /// </summary>
    [Fact]
    public async Task APartTurnedByHandLeavesTheTurnedList()
    {
        var (vm, settle) = Open(turnThird: true);
        var turned = Assert.Single(vm.PartsReadAsTurned);
        var view = vm.Board!.View!;

        view.Instances[turned.InstanceIndex] = TurnedParts.HalfTurn(view.Instances[turned.InstanceIndex], turned);
        vm.NotifyArtworkChanged(LayoutChangeInfo.InstancesOnly);
        Assert.False(vm.TurnPartsCommand.CanExecute(null));

        settle.SetResult();
        await vm.PadRead!;

        Assert.False(vm.HasPartsReadAsTurned);
    }

    /// <summary><b>A burst of ten instance edits costs one pad read.</b></summary>
    [Fact]
    public async Task ABurstOfInstanceEditsCostsOnePadRead()
    {
        var (vm, settle) = Open(turnThird: false);
        var inst = vm.Board!.View!.Instances[0];

        for (int i = 0; i < 10; i++)
        {
            inst.X += Mm(0.1);
            vm.NotifyArtworkChanged(LayoutChangeInfo.InstancesOnly);
        }

        settle.SetResult();
        await vm.PadRead!;

        Assert.Equal(1, vm.PadReadsPerformed);
    }

    /// <summary>
    /// <b>A shape-only edit costs none</b> — under its own kind, and under <c>Full</c>, which a
    /// shape delete raises too and which is settled by comparing the placements.
    /// </summary>
    [Fact]
    public void AShapeOnlyEditCostsNoPadRead()
    {
        var (vm, _) = Open(turnThird: false);
        var view = vm.Board!.View!;

        view.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = Mm(1), Y2 = Mm(1) });
        vm.NotifyArtworkChanged(LayoutChangeInfo.Appended(view.Shapes.Count - 1, 1));

        view.Shapes.RemoveAt(view.Shapes.Count - 1);
        vm.NotifyArtworkChanged();

        Assert.False(vm.IsReadingParts);
        Assert.Null(vm.PadRead);
        Assert.Equal(0, vm.PadReadsPerformed);
    }
}

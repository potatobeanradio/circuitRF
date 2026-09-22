// Owner instruction, 2026-09-14: a port's context menu in the layout editor shows the port type rows
// and nothing else.
//
// A port is the one thing under the pointer that is entirely about itself. It is not an edge to
// convert and not a vertex to delete, and the wire commands a wBond overlay contributes are about
// something else that happens to be nearby — so `BuildContextMenuItems` returns the three type rows
// and nothing else, BEFORE the overlay is asked and before any other click-target section runs.
//
// The real control over a real view model, with only Avalonia's own popup bypassed — the seam
// StackupContextMenuTests uses for the same job.

using Avalonia.Controls;
using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Controls;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Tests.Layout;

public class PortContextMenuTests
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;
    private static readonly LayerKey TopCopper = new(1, 0);

    private static long Mm(double mm) => (long)Math.Round(mm * 1000 * Dbu);

    /// <summary>A trace with one port in the middle of it and a POLYGON beside the port, so the
    /// vertex and edge sections of the menu have something real to offer at the same point — without
    /// that, "only the port's rows" would pass for want of anything else to show.</summary>
    private static (LayoutCanvas Canvas, LayoutEditorViewModel Vm, LabelShape Port) Fixture()
    {
        var view = new LayoutView { DbuPerMicron = Dbu, DisplayUnit = LayoutUnit.Um, SnapDbu = 100 * Dbu };
        view.Shapes.Add(new PolygonShape
        {
            Layer = TopCopper,
            Xy = [0, 0, Mm(20), 0, Mm(20), Mm(2.9), 0, Mm(2.9)],
        });

        var port = new LabelShape
        {
            Layer = TopCopper, X = Mm(10), Y = Mm(1.45), Text = "P1", Height = Mm(0.5),
            IsPort = true, PortDirection = LayoutRotation.R0, PortKind = PlanarPortKind.Internal,
        };
        view.Shapes.Add(port);

        var vm = new LayoutEditorViewModel(view);
        var canvas = new LayoutCanvas { ViewModel = vm };
        return (canvas, vm, port);
    }

    private static List<string> Headers(IEnumerable<object> items) =>
        [.. items.OfType<MenuItem>().Select(m => m.Header?.ToString() ?? "")];

    [Fact]
    public void RightClickingAPort_OffersItsThreeTypesAndNothingElse()
    {
        var (canvas, _, port) = Fixture();

        var items = canvas.BuildContextMenuItems(port.X, port.Y);

        Assert.Equal(
            ["Port Type: Edge", "Port Type: Internal delta gap", "Port Type: Internal (to ground)"],
            Headers(items));

        // …and no separators either: three rows is the whole menu, not a section of one.
        Assert.Empty(items.OfType<Separator>());
        Assert.Equal(3, items.Count);
    }

    [Fact]
    public void TheCurrentTypeIsTheTickedRow()
    {
        var (canvas, vm, port) = Fixture();

        var ticked = Assert.Single(canvas.BuildContextMenuItems(port.X, port.Y)
                                         .OfType<MenuItem>(), m => m.IsChecked);
        Assert.Equal("Port Type: Internal (to ground)", ticked.Header);

        // Radio, not a checkbox: the user is choosing among three states.
        Assert.All(canvas.BuildContextMenuItems(port.X, port.Y).OfType<MenuItem>(),
                   m => Assert.Equal(MenuItemToggleType.Radio, m.ToggleType));

        // A port that states nothing falls back to what the ARTWORK says, so the tick is always on
        // the row the drawing is showing.
        port.PortKind = null;
        vm.Model.NotifyChanged();
        var inferred = Assert.Single(canvas.BuildContextMenuItems(port.X, port.Y)
                                           .OfType<MenuItem>(), m => m.IsChecked);
        Assert.Equal("Port Type: Internal (to ground)", inferred.Header);   // mid-metal
    }

    [Fact]
    public void ClickingARowMakesTheEdit()
    {
        var (canvas, vm, port) = Fixture();

        var gap = canvas.BuildContextMenuItems(port.X, port.Y)
                        .OfType<MenuItem>().Single(m => m.Header!.ToString() == "Port Type: Internal delta gap");
        gap.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));

        Assert.Equal(PlanarPortKind.InternalDeltaGap, port.PortKind);

        // One undoable LAYOUT edit — the drawing is where the type lives.
        vm.UndoCommand.Execute(null);
        Assert.Equal(PlanarPortKind.Internal, port.PortKind);
    }

    [Fact]
    public void AwayFromEveryPort_TheOrdinaryMenuIsUnchanged()
    {
        // The other half of the rule: a port owning its own menu must not have taken the menu away
        // from everything else. Well clear of the port's pick region the ordinary menu is intact —
        // and carries no port rows, so the two never mix.
        var (canvas, _, _) = Fixture();

        var headers = Headers(canvas.BuildContextMenuItems(Mm(2), Mm(2.8)));

        Assert.Contains("Rotate 90° CW", headers);
        Assert.Contains("Duplicate…", headers);
        Assert.DoesNotContain(headers, h => h.StartsWith("Port Type:", StringComparison.Ordinal));
    }
}

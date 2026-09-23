// ================================================================
//  PartsTableSortTests.cs — the parts table's click-to-sort headers (owner, 2026-09-23)
//
//  A header click cycles ascending → descending → the document's own order. One test per claim:
//  the cycle, and the rule that a row with nothing in the sorted column stays LAST both ways.
//  Driven on the shipped Power Rail example, as MountUnmountTests is.
// ================================================================

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CircuitRF.Design.RailRf;
using CircuitRF.Ui.RailRf;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests.RailRf;

public sealed class PartsTableSortTests
{
    /// <summary>
    /// <b>Three clicks on one header: ascending, descending, and back to the .crail's order</b> —
    /// with the refdes read as a person reads it, so C2 comes before C10.
    /// </summary>
    [Fact]
    public void OneHeader_ThreeClicks_AscendingThenDescendingThenTheDocumentOrder()
    {
        var vm = Example();
        var document = Refdeses(vm);
        var natural = document.Order(SchematicSortPlacement.NaturalNameComparer.Instance).ToList();
        Assert.NotEqual(natural, document.Order(StringComparer.Ordinal).ToList()); // C10 vs C2 matters here

        vm.SortParts(RailPartsSortColumn.Refdes);
        Assert.Equal(natural, Refdeses(vm));

        vm.SortParts(RailPartsSortColumn.Refdes);
        Assert.Equal(Enumerable.Reverse(natural), Refdeses(vm));

        vm.SortParts(RailPartsSortColumn.Refdes);
        Assert.Equal(RailPartsSortColumn.None, vm.PartsSortColumn);
        Assert.Equal(document, Refdeses(vm));
    }

    /// <summary>
    /// <b>A numeric column sorts by the NUMBER, and a row with none stays last in both directions</b>
    /// — and the order survives the rebuild that adding a row causes, since every solve rebuilds.
    /// </summary>
    [Fact]
    public void ByCapacitance_UnresolvedRowStaysLast_BothWays_AcrossARebuild()
    {
        var vm = Example();
        vm.SortParts(RailPartsSortColumn.Capacitance);
        Assert.Equal(1, vm.AddParts(["C99"])); // no part number: nothing to sort it by

        // C99 has no part number and FB1 is a series element, whose C cell is an impedance —
        // neither has a capacitance, so both sit below every row that does, in either direction.
        var up = vm.Parts.ToList();
        var values = Keyed(up);
        Assert.Equal(values.Order(), values);
        Assert.True(values[0] < values[^1], "The example should hold more than one capacitance.");
        Assert.Equal(["FB1", "C99"], up.Skip(values.Count).Select(r => r.Refdes));

        vm.SortParts(RailPartsSortColumn.Capacitance);
        var down = vm.Parts.ToList();
        Assert.Equal(values.OrderDescending(), Keyed(down));
        Assert.Equal(["FB1", "C99"], down.Skip(values.Count).Select(r => r.Refdes));
    }

    /// <summary>The leading run of rows that HAVE a capacitance — every one of them, or the keyless
    /// rows were not all last.</summary>
    private static System.Collections.Generic.List<double> Keyed(System.Collections.Generic.List<RailPartRowViewModel> rows)
    {
        var lead = rows.TakeWhile(r => r.CapacitanceSortKey is not null).Select(r => r.CapacitanceSortKey!.Value).ToList();
        Assert.Equal(rows.Count(r => r.CapacitanceSortKey is not null), lead.Count);
        return lead;
    }

    private static System.Collections.Generic.List<string> Refdeses(RailRfViewModel vm) =>
        [.. vm.Parts.Select(r => r.Refdes)];

    private static RailRfViewModel Example()
    {
        string crail = Path.Combine(
            RepoRoot(), "examples", "Power Rail", "Sensor board", "Sensor board.crail");
        var vm = new RailRfViewModel(RailDocumentIo.LoadFromFile(crail), crail)
        {
            PostToUi     = a => a(),
            RunOffThread = (work, _) => Task.FromResult(work()),
        };
        Assert.Empty(vm.LoadDocumentReferences());
        vm.RebuildParts();
        return vm;
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "circuitrf.slnx"))) d = d.Parent;
        Assert.NotNull(d);
        return d!.FullName;
    }
}

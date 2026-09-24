// ================================================================
//  SourceImpedanceEntryTests.cs — a source's output R and L can be typed in the window
//
//  Field report, 2026-09-23: the |Z| sweep refuses an ideal source and tells the user to state its
//  series resistance, and the Sources card had no column to state it in — only the anchor and the
//  voltage. The view model had the two entries all along; nothing bound them.
// ================================================================

using System;
using System.IO;
using System.Linq;
using CircuitRF.Design.RailRf;
using CircuitRF.Ui.RailRf;
using Xunit;

namespace CircuitRF.Ui.Tests.RailRf;

public sealed class SourceImpedanceEntryTests
{
    [Fact]
    public void TheSourcesCardHasRAndLColumns_AndTheyStoreOnlyWhatReadsAsAValue()
    {
        // The window binds both, inside the Sources row template.
        string xaml = File.ReadAllText(Path.Combine(RepoRoot(), "src/Ui/Views/RailRf/RailRfWindow.axaml"));
        int start = xaml.IndexOf("x:DataType=\"rvm:RailSourceRowViewModel\"", StringComparison.Ordinal);
        int end = xaml.IndexOf("</DataTemplate>", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "the Sources row template was not found");
        string template = xaml[start..end];
        Assert.Contains("{Binding ResistanceEntry, Mode=TwoWay}", template, StringComparison.Ordinal);
        Assert.Contains("{Binding InductanceEntry, Mode=TwoWay}", template, StringComparison.Ordinal);

        var rail = new RailSpec { Name = "VDD" };
        rail.Sources.Add(new RailSource { Anchor = new RailPortAnchor { Refdes = "U1", Pin = "1" }, OpenCircuitVoltageV = 3.3 });
        var row = new RailSourceRowViewModel(rail, rail.Sources[0]);
        RailSource Stored() => rail.Sources.Single();

        row.ResistanceEntry = "50 mΩ";
        row.InductanceEntry = "2 nH";
        Assert.Equal(0.050, Stored().SeriesResistanceOhms!.Value, 12);
        Assert.Equal(2.0, Stored().SeriesInductanceHenries!.Value * 1e9, 9);

        // A bare inductance is a guess at its scale, and a typo is not a request to clear: both keep
        // what is stored.
        row.InductanceEntry = "2";
        row.ResistanceEntry = "fifty";
        Assert.Equal(2.0, Stored().SeriesInductanceHenries!.Value * 1e9, 9);
        Assert.Equal(0.050, Stored().SeriesResistanceOhms!.Value, 12);

        // Empty does clear.
        row.InductanceEntry = "";
        Assert.Null(Stored().SeriesInductanceHenries);
    }

    /// <summary>
    /// A source typed as 0 Ω and 0 H is as ideal as a blank one, and is refused the same way. It used
    /// to leave the sweep silently — a zero impedance's admittance is taken as zero — so the curve was
    /// the capacitors alone, labelled as the rail's.
    /// </summary>
    [Theory]
    [InlineData(null, null)]
    [InlineData(0.0, 0.0)]
    public void ASourceWithNoOutputImpedance_BlankOrZero_IsRefusedByTheSweep(double? ohms, double? henries)
    {
        var rail = SeriesElementTests.TypedRail();
        var request = new PdnSweepRequest
        {
            Rail = rail,
            Parts = new RailPartResolver(SeriesElementTests.Library()).ResolveAll(rail.Parts, railVoltageV: 3.6),
            Sources = [new RailSourceModel(0, "BT1", RailSourceBasis.Rl, ohms, henries, 3.6)],
            Series = [RailSeriesModel.Of(rail.SeriesElements[0])],
            Partition = RailSeriesPartition.Typed(rail),
            RankRemovals = false,
        };

        var sweep = PdnSweep.Run(request);
        Assert.NotNull(sweep.Refusal);
        Assert.Contains("ideal source", sweep.Refusal, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}

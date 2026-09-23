// ================================================================
//  PartsTableColumnGripperTests.cs — the parts table's column grippers (owner, 2026-09-23)
//
//  Double-clicking a gripper fits its column to the widest entry, measured from the row view
//  models rather than from the cells on screen (the list is virtualized). So the fit reads text
//  through RailRfWindow.PartsColumns, a second statement of which property each column shows.
//  This holds that table to the row template: a fit that measured the wrong property would size
//  the column to some other column's text, and nothing would look wrong until it was too narrow.
// ================================================================

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CircuitRF.Ui.Views.RailRf;
using Xunit;

namespace CircuitRF.Ui.Tests.RailRf;

public sealed class PartsTableColumnGripperTests
{
    [Fact]
    public void FitMeasuresTheProperty_EachColumnsCellDisplays()
    {
        string xaml = File.ReadAllText(Path.Combine(RepoRoot(), "src/Ui/Views/RailRf/RailRfWindow.axaml"));
        int start = xaml.IndexOf("<DataTemplate x:DataType=\"rvm:RailPartRowViewModel\">", StringComparison.Ordinal);
        int end   = xaml.IndexOf("</DataTemplate>", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "The parts row template was not found.");

        var cells = Regex.Matches(xaml[start..end],
                @"<TextBlock Grid\.Column=""(\d+)""[^>]*?Text=""\{Binding (\w+)\}""", RegexOptions.Singleline)
            .Select(m => (int.Parse(m.Groups[1].Value), m.Groups[2].Value))
            .ToList();

        Assert.Equal(cells, RailRfWindow.PartsColumns.Select(c => (c.Column, c.Binding)));
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "circuitrf.slnx"))) d = d.Parent;
        Assert.NotNull(d);
        return d!.FullName;
    }
}

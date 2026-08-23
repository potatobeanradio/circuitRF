using System.Text.RegularExpressions;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// The .ctech editor's Layers tab is THREE grids sharing one column layout — the filter bar with its
/// bulk toggles, the header row, and the row template — and they line up only because all three
/// declare the same columns in the same order.
///
/// <para><b>Nothing enforces that at runtime and nothing looks wrong until you read a value off the
/// wrong header.</b> Editing one grid and not the others leaves a table where the "Vis" heading sits
/// over the datatype boxes and the bulk visibility toggle sweeps a column it is not above. Every one
/// of those is a silent misread rather than a crash, which is why this is scanned.</para>
/// </summary>
public sealed class TechEditorLayerColumnLayoutTests
{
    /// <summary>The Layers tab's columns, left to right. The widths travel with the columns — a
    /// reorder that leaves the widths behind gives the 30 px checkbox column to a 4-digit number.</summary>
    private static readonly (string Header, string Width)[] Columns =
    [
        ("Name",     "Width=\"*\" MinWidth=\"110\""),
        ("Vis",      "Width=\"30\""),
        ("Sel",      "Width=\"30\""),
        ("Color",    "Width=\"48\""),
        ("Pattern",  "Width=\"104\""),
        ("Fill",     "Width=\"44\""),
        ("Z",        "Width=\"70\""),
        ("Purpose",  "Width=\"110\""),
        ("Layer",    "Width=\"48\""),
        ("Datatype", "Width=\"48\""),
        // The actions column carries no header — nothing to label a Dup/remove pair with.
        (null!,      "Width=\"82\""),
    ];

    /// <summary>Each row cell, by something that identifies it and nothing else, and the column it
    /// belongs in.</summary>
    private static readonly (string Anchor, int Column)[] Cells =
    [
        ("Tag=\"Name\"",                       0),
        ("IsChecked=\"{Binding Visible}\"",    1),
        ("IsChecked=\"{Binding Selectable}\"", 2),
        ("PickColorCommand",                   3),
        ("FillPatternChoices",                 4),
        ("Tag=\"FillOpacity\"",                5),
        ("Tag=\"ZOrder\"",                     6),
        ("Tag=\"Purpose\"",                    7),
        ("Tag=\"LayerNumber\"",                8),
        ("Tag=\"Datatype\"",                   9),
        ("DuplicateCommand",                  10),
    ];

    [Fact]
    public void TheThreeGridsDeclareTheSameColumns_InTheSameOrder()
    {
        var grids = ColumnBlocks();

        Assert.Equal(3, grids.Count);
        foreach (var widths in grids)
            Assert.Equal(Columns.Select(c => c.Width), widths);
    }

    [Fact]
    public void EachHeaderSitsOverItsOwnColumn()
    {
        string axaml = Axaml();

        foreach (var (header, _) in Columns)
        {
            if (header is null) continue;
            int expected = Array.FindIndex(Columns, c => c.Header == header);
            var m = Regex.Match(axaml,
                $@"<TextBlock Grid\.Column=""(\d+)""\s+Classes=""colhdr"" Text=""{header}""");
            Assert.True(m.Success, $"the Layers tab has no \"{header}\" header any more");
            Assert.Equal(expected, int.Parse(m.Groups[1].Value));
        }
    }

    /// <summary>
    /// The editable control for each column is in that column. This is the half that actually moves
    /// data: a checkbox left behind in the datatype column edits visibility under a "Datatype"
    /// heading, and reads as a wrong VALUE rather than as a wrong layout.
    /// </summary>
    [Fact]
    public void EachRowCellSitsInItsOwnColumn()
    {
        string row = RowTemplate();

        foreach (var (anchor, expected) in Cells)
        {
            int at = row.IndexOf(anchor, StringComparison.Ordinal);
            Assert.True(at >= 0, $"the row template no longer contains '{anchor}'");

            // The nearest Grid.Column BEFORE the anchor is the one the anchor's element declares.
            var columns = Regex.Matches(row[..at], @"Grid\.Column=""(\d+)""");
            Assert.True(columns.Count > 0, $"'{anchor}' is in no column");
            Assert.Equal(expected, int.Parse(columns[^1].Groups[1].Value));
        }
    }

    /// <summary>
    /// The two bulk toggles sit directly above the checkbox columns they sweep. Their whole reason
    /// for being in that grid rather than in a button bar is that adjacency — a control acting on a
    /// column several hundred pixels away is the version of this that has to be read twice.
    /// </summary>
    [Fact]
    public void TheBulkTogglesSitAboveTheColumnsTheySweep()
    {
        string axaml = Axaml();

        foreach (var (property, header) in new[]
                 {
                     ("AllShownLayersVisible", "Vis"),
                     ("AllShownLayersSelectable", "Sel"),
                 })
        {
            int expected = Array.FindIndex(Columns, c => c.Header == header);
            var m = Regex.Match(axaml,
                $@"<ToggleButton Grid\.Column=""(\d+)"" Classes=""coltoggle""\s+IsChecked=""\{{Binding ViewModel\.{property}");
            Assert.True(m.Success, $"the {header} bulk toggle is gone");
            Assert.Equal(expected, int.Parse(m.Groups[1].Value));
        }
    }

    /// <summary>
    /// The stripped cell template is only ever put on a field that cannot outgrow its box.
    ///
    /// <para>A row of these tables is realized whole every time the list page-scrolls, and a stock
    /// <c>TextBox</c> is expensive to realize — its template carries a ScrollViewer, that
    /// ScrollViewer's two ScrollBars and a validation wrapper, about twenty visual nodes for a
    /// single-line field. Measured on a 377-layer technology, replacing it on the short numeric cells
    /// took a layer row from 199 visual nodes to 123 and a page-scroll from ~185 ms to ~133 ms.</para>
    ///
    /// <para><b>What it costs is the ScrollViewer, and the ScrollViewer is what scrolls a long value
    /// sideways to keep the caret visible while it is being typed.</b> On a fixed-width field holding
    /// four digits that is free; on a free-text name it silently clips what the user is typing. The
    /// template also drops the watermark. So: an explicit width, and no placeholder — anything else
    /// keeps the stock template, and this is the check that says so before someone discovers it by
    /// typing a long layer name into a clipped box.</para>
    /// </summary>
    [Fact]
    public void TheCompactCellTemplate_IsOnlyOnFixedWidthFieldsWithNoWatermark()
    {
        string axaml = Axaml();

        var compact = Regex.Matches(axaml, @"<TextBox\b[^>]*Classes=""cell compact""[^>]*/>",
                                    RegexOptions.Singleline);
        Assert.True(compact.Count > 0, "no cell uses the compact template any more");

        foreach (System.Text.RegularExpressions.Match m in compact)
        {
            string tag = Regex.Match(m.Value, @"Tag=""(\w+)""").Groups[1].Value;

            Assert.True(Regex.IsMatch(m.Value, @"\bWidth=""\d"),
                $"the '{tag}' cell uses the compact template but declares no fixed Width. Without the " +
                "ScrollViewer that template drops, a value wider than the box is clipped with no way " +
                "to scroll to the caret.");

            Assert.False(m.Value.Contains("PlaceholderText", StringComparison.Ordinal),
                $"the '{tag}' cell uses the compact template and a watermark. The template has no " +
                "placeholder presenter, so the watermark silently never appears.");
        }
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static string Axaml() => File.ReadAllText(
        Path.Combine(RepoRoot(), "src", "Ui", "Views", "Layout", "TechEditorView.axaml"));

    /// <summary>The three column lists of the Layers tab, in file order. Matched by their column
    /// COUNT: the Stackup, DRC and Interchange tabs have grids of their own and none of them has
    /// eleven.</summary>
    private static List<List<string>> ColumnBlocks()
    {
        var blocks = new List<List<string>>();
        foreach (System.Text.RegularExpressions.Match block in Regex.Matches(Axaml(),
                     @"<Grid\.ColumnDefinitions>(.*?)</Grid\.ColumnDefinitions>", RegexOptions.Singleline))
        {
            var widths = Regex.Matches(block.Groups[1].Value, @"<ColumnDefinition ([^/]*?)\s*/>")
                              .Select(m => m.Groups[1].Value.Trim()).ToList();
            if (widths.Count == Columns.Length) blocks.Add(widths);
        }
        return blocks;
    }

    private static string RowTemplate()
    {
        string axaml = Axaml();
        int start = axaml.IndexOf("<DataTemplate x:DataType=\"lay:LayerRowViewModel\">", StringComparison.Ordinal);
        Assert.True(start >= 0, "the layer row template is gone");
        int end = axaml.IndexOf("</DataTemplate>", start, StringComparison.Ordinal);
        return axaml[start..end];
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}

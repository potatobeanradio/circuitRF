using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace CircuitRF.Ui.Tests.Layout;

/// <summary>
/// <b>The two file names in the .cem header shrink and ellipsize as the window narrows; they never
/// run under the Mesh/Simulate cluster.</b> Owner report, 2026-09-09: with a long name and a narrow
/// .cem window, the buttons render on top of the setup name and the layout reference on the row
/// below it.
///
/// <para>Both texts were in containers that CANNOT squeeze. The name sat in an <c>Auto</c> grid
/// column, and an Auto column takes its content's full desired width however little is left; the
/// layout reference sat in a horizontal <c>StackPanel</c>, which hands its children UNBOUNDED width
/// along its own axis, so its <c>TextTrimming</c> could never engage below its <c>MaxWidth</c>. The
/// identity block therefore overflowed its own cell in the outer <c>*,Auto</c> grid, and the button
/// cluster — laid out independently in that Auto column — drew over it. Measured headless, before
/// and after: the name's arranged width was <b>672 px at every window width from 1400 down to
/// 300</b>; it is 504 / 108 / 0 across the same range now, and the identity block's right edge no
/// longer crosses the cluster's left edge at any width.</para>
///
/// <para><b>Scanned rather than measured, because this test project has no Avalonia platform</b> —
/// no window, no layout pass (the same reason <c>DockWindowBehaviourTests</c> pins mechanisms rather
/// than geometry). What is asserted is exactly the structure the fix rests on, and it is the
/// structure a regression would undo: the row is a Grid, the text is in a STAR column, and it
/// trims.</para>
/// </summary>
public sealed class EmSetupHeaderShrinksTests
{
    private const string View = "src/Ui/Views/Layout/EmSetupEditorView.axaml";

    /// <summary>The two texts, by the binding that identifies each.</summary>
    public static TheoryData<string> FileNameTexts() =>
    [
        "{Binding ViewModel.Working.Name}",   // the .cem's own name
        "{Binding ViewModel.LayoutStatus}",   // the .clay it references
    ];

    [Theory]
    [MemberData(nameof(FileNameTexts))]
    public void EachFileNameIsInAStarColumn_AndTrims(string binding)
    {
        var (row, text) = RowCarrying(binding);

        Assert.Equal("CharacterEllipsis", text.Attribute("TextTrimming")?.Value);

        // The star column is the whole point: an Auto column would take the text's full width back.
        var widths = ColumnWidths(row);
        int column = int.Parse(text.Attribute("Grid.Column")?.Value ?? "0");
        Assert.True(column < widths.Length,
            $"the text is in column {column} but the row declares {widths.Length}");
        Assert.Equal("*", widths[column]);
    }

    /// <summary>
    /// A horizontal StackPanel is how the layout-reference row came to overflow, so neither row may
    /// be one — nor may either text sit inside one at any depth, which would reintroduce the
    /// unbounded measure just as effectively.
    /// </summary>
    [Theory]
    [MemberData(nameof(FileNameTexts))]
    public void NeitherFileNameSitsInsideAHorizontalStackPanel(string binding)
    {
        var (_, text) = RowCarrying(binding);

        foreach (var ancestor in text.Ancestors())
        {
            if (ancestor.Name.LocalName == "Border") break;   // the header Border: far enough up
            if (ancestor.Name.LocalName != "StackPanel") continue;

            // Vertical is fine — it constrains width normally. Horizontal is not.
            string orientation = ancestor.Attribute("Orientation")?.Value ?? "Vertical";
            Assert.Equal("Vertical", orientation);
        }
    }

    /// <summary>
    /// <c>HorizontalAlignment="Left"</c> is what stops the star column becoming a wide-width
    /// regression: left-aligned, the Grid arranges at its own desired width instead of filling the
    /// cell, so the star column settles at the text's natural width and no gap opens between the
    /// name and "Output file:". Stretched, the star column would swallow every surplus pixel and the
    /// output-file cluster would drift to the far side of the identity block — which is the 2026-08-14
    /// complaint about the picker parking itself against the Mesh button.
    /// </summary>
    [Theory]
    [MemberData(nameof(FileNameTexts))]
    public void TheRowIsLeftAligned_SoTheStarColumnTakesNoSurplus(string binding)
    {
        var (row, _) = RowCarrying(binding);
        Assert.Equal("Left", row.Attribute("HorizontalAlignment")?.Value);
    }

    /// <summary>
    /// Exactly one star column per row. Two would split the surplus between them, which is both a
    /// gap where none belongs and a squeeze that starts on the text before the spacer has given up
    /// its own width.
    /// </summary>
    [Theory]
    [MemberData(nameof(FileNameTexts))]
    public void EachRowHasExactlyOneStarColumn(string binding)
    {
        var (row, _) = RowCarrying(binding);
        Assert.Single(ColumnWidths(row), w => w.EndsWith('*'));
    }

    /// <summary>The full name is still reachable once it is elided — the tooltip carries it.</summary>
    [Theory]
    [MemberData(nameof(FileNameTexts))]
    public void AnElidedNameIsStillReadableFromItsTooltip(string binding)
    {
        var (_, text) = RowCarrying(binding);
        Assert.Equal(binding, text.Attribute("ToolTip.Tip")?.Value);
    }

    /// <summary>
    /// <b>The header row is the panel, and the identity block declares its own floor.</b> Second half
    /// of the same day's report: with the names elided the block still cannot shrink past the
    /// Output-file label, its 120 px box and the "…" picker, and a Grid then laid the Mesh/Simulate
    /// cluster on top of them. <c>ShrinkThenOverflowPanel</c> puts the cluster after the content
    /// instead — off the right edge, where the window clips it — but only because the block states a
    /// floor Avalonia's own <c>DesiredSize</c> will not report (it clamps to whatever was offered).
    /// A block with no floor is the bug back again, silently, so it is the presence of the number
    /// that is asserted here rather than the number itself.
    /// </summary>
    [Fact]
    public void TheHeaderRowIsThePanel_AndTheIdentityBlockDeclaresItsFloor()
    {
        var doc = XDocument.Load(Path.Combine(RepoRoot(), View));

        var panel = doc.Descendants()
            .SingleOrDefault(e => e.Name.LocalName == "ShrinkThenOverflowPanel");
        Assert.NotNull(panel);

        // Content first, buttons second — the panel's own contract, and the order decides which one
        // shrinks and which one leaves the window.
        var children = panel!.Elements().ToList();
        Assert.Equal(2, children.Count);

        var identity = children[0];
        Assert.Equal("StackPanel", identity.Name.LocalName);
        Assert.Equal("Vertical", identity.Attribute("Orientation")?.Value);

        Assert.True(double.TryParse(identity.Attribute("MinWidth")?.Value, out double floor)
                    && floor > 0,
                    "the identity block declares no MinWidth, so the panel has no floor to place the "
                    + "buttons after and they land back on top of the Output file row");

        // The block whose floor that is: the Output-file row's own fixed parts have to be inside it.
        Assert.Contains(identity.Descendants(),
            e => e.Attribute("Name")?.Value == "SnpOutputPathBox");
    }

    // ── Reading the view ────────────────────────────────────────────────────────────────────

    /// <summary>The Grid holding the text element whose Text is <paramref name="binding"/>, and that
    /// element.
    ///
    /// <para><b>Either kind of text element counts.</b> The layout reference became a
    /// <c>SelectableTextBlock</c> on 2026-09-15 (it names a file, so it has to be copyable and it
    /// carries a context menu into that file), and every structural claim below is about the ROW —
    /// star column, trimming, tooltip, no horizontal StackPanel — none of which the element's kind
    /// changes. Matching on <c>TextBlock</c> alone would have turned that unrelated change into five
    /// red tests saying nothing about the overflow this file exists to hold shut.</para></summary>
    private static (XElement Row, XElement Text) RowCarrying(string binding)
    {
        var doc = XDocument.Load(Path.Combine(RepoRoot(), View));

        var text = doc.Descendants()
            .Where(e => e.Name.LocalName is "TextBlock" or "SelectableTextBlock")
            .SingleOrDefault(e => e.Attribute("Text")?.Value == binding);
        Assert.NotNull(text);

        var row = text!.Ancestors().FirstOrDefault(e => e.Name.LocalName == "Grid");
        Assert.NotNull(row);
        Assert.NotNull(row!.Attribute("ColumnDefinitions"));   // a row, not the outer header grid
        return (row, text!);
    }

    private static string[] ColumnWidths(XElement grid) =>
        grid.Attribute("ColumnDefinitions")!.Value.Split(',').Select(s => s.Trim()).ToArray();

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}

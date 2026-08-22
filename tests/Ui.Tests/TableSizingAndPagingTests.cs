// ================================================================
//  TableSizingAndPagingTests.cs
//
//  1. Table box sizing — a Table's minimum width is its COLUMN width, not the
//     flat 200 px floor every other plot type gets. One floor caused both
//     reported symptoms: the gripper drag stopped short of the single column's
//     width, and the gripper DOUBLE-CLICK auto-fit computed the right target
//     (TotalColumnWidth) only to have ResizeTo clamp it straight back to 200.
//
//  2. Page Up / Page Down — scrolls the SELECTED table by one whole page;
//     Home / End — jumps it to the first row / the last page.
// ================================================================

using System;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class TableSizingAndPagingTests
{
    private const double CanvasW = 1200, CanvasH = 800;

    private static DataDisplayViewModel MakeDisplay()
    {
        var ddvm = new DataDisplayViewModel(new DataSourceLibraryViewModel(), addEmptyPlot: false);
        ddvm.CanvasSizeProvider = () => (CanvasW, CanvasH);
        return ddvm;
    }

    private static Trace MakeTrace(double columnWidth = 115) =>
        new Trace(new SNP(new[] { 1e9 }, 2), MatrixType.S, 0, 0, DependentVarFormat.Db)
        {
            ColumnWidth = columnWidth,
        };

    /// <summary>A Table plot holding one rank-0 (1x1) trace — one column, one row.</summary>
    private static PlotContainerViewModel MakeScalarTable(DataDisplayViewModel ddvm)
    {
        var container = ddvm.AddPlot(PlotType.Table);
        var plot      = container.PlotVM.Plot;
        plot.FontSize    = 12;
        plot.ColumnWidth = 115;

        var t = MakeTrace();
        t.CubeName  = "PDC";
        t.Slice     = Array.Empty<AxisSlice>();
        t.Transform = CubeTransform.None;
        t.SetScalarCubeData(complexValue: null, realValue: 0.042, PlotType.Table, FreqUnit.GHz);
        plot.Traces.Add(t);

        return container;
    }

    /// <summary>A Table plot holding one swept trace of <paramref name="rows"/> rows (X + value columns).</summary>
    private static PlotContainerViewModel MakeSweptTable(DataDisplayViewModel ddvm, int rows)
    {
        var container = ddvm.AddPlot(PlotType.Table);
        var plot      = container.PlotVM.Plot;
        plot.FontSize    = 12;
        plot.ColumnWidth = 115;

        var xs = new double[rows];
        var ys = new double[rows];
        for (int i = 0; i < rows; i++) { xs[i] = i; ys[i] = i * 0.1; }

        var t = MakeTrace();
        t.CubeName  = "PAE";
        t.Slice     = new[] { new AxisSlice("Pin", AxisRole.KeepAsX, 0) };
        t.Transform = CubeTransform.None;
        t.SetCubeData(xs, complexValues: null, ys, "Pin", "dBm", PlotType.Table, FreqUnit.GHz);
        plot.Traces.Add(t);

        return container;
    }

    // ============================================================
    //  1. Box sizing
    // ============================================================

    /// <summary>The gripper drag: a one-column table must shrink to its column width, not stop at 200.</summary>
    [Fact]
    public void ScalarTable_GripperDrag_ShrinksToColumnWidth()
    {
        var ddvm      = MakeDisplay();
        var container = MakeScalarTable(ddvm);
        var plot      = container.PlotVM.Plot;

        double natural = TableRenderer.TotalColumnWidth(plot);
        Assert.Equal(115, natural, 3);          // one 115-px column, nothing else

        // Drag the gripper far to the left — well past the column width.
        container.ResizeTo(20, container.Height);

        Assert.Equal(natural, container.Width, 3);
        Assert.True(container.Width < 200, "a one-column table must not be floored at 200 px");
    }

    /// <summary>
    /// The gripper DOUBLE-CLICK: PlotContainerView computes the fit target as
    /// min(TotalColumnWidth, viewable width) and hands it to ResizeTo. Reproduced here, since the
    /// defect was the clamp inside ResizeTo, not the target.
    /// </summary>
    [Fact]
    public void ScalarTable_GripperDoubleClick_FitsExactlyOneColumn()
    {
        var ddvm      = MakeDisplay();
        var container = MakeScalarTable(ddvm);
        var plot      = container.PlotVM.Plot;

        float  totalW        = TableRenderer.TotalColumnWidth(plot);
        double viewableRight = ddvm.GetViewableRightEdge(CanvasW);
        double maxW          = Math.Max(200, viewableRight - container.Left);
        double targetW       = Math.Min(totalW, maxW);
        double targetH       = TableRenderer.RequiredCanvasHeight(plot, 1f) + 1.0;

        container.ResizeTo(targetW, targetH);

        Assert.Equal(totalW, container.Width, 3);
        Assert.Equal(targetH, container.Height, 3);   // header + exactly one data row
    }

    /// <summary>A multi-column table keeps the ordinary 200 px floor — it may still be dragged
    /// narrower than its columns and clip, exactly as before.</summary>
    [Fact]
    public void WideTable_KeepsThe200Floor()
    {
        var ddvm      = MakeDisplay();
        var container = MakeSweptTable(ddvm, rows: 10);   // X + value = 230 px of columns

        Assert.True(TableRenderer.TotalColumnWidth(container.PlotVM.Plot) > 200);

        container.ResizeTo(20, container.Height);
        Assert.Equal(200, container.Width, 3);
    }

    /// <summary>An empty Table has no columns to fit, so it keeps the 200 px floor rather than
    /// collapsing to the renderer's per-column minimum.</summary>
    [Fact]
    public void EmptyTable_KeepsThe200Floor()
    {
        var ddvm      = MakeDisplay();
        var container = ddvm.AddPlot(PlotType.Table);

        container.ResizeTo(20, container.Height);
        Assert.Equal(200, container.Width, 3);
    }

    /// <summary>Non-table plot types are untouched by the table-specific floor.</summary>
    [Fact]
    public void RectPlot_KeepsThe200Floor()
    {
        var ddvm      = MakeDisplay();
        var container = ddvm.AddPlot(PlotType.Rect);

        container.ResizeTo(20, container.Height);
        Assert.Equal(200, container.Width, 3);
    }

    // ============================================================
    //  2. Page Up / Page Down
    // ============================================================

    [Fact]
    public void PageDownAndUp_ScrollSelectedTableByOneWholePage()
    {
        var ddvm      = MakeDisplay();
        var container = MakeSweptTable(ddvm, rows: 50);
        var plot      = container.PlotVM.Plot;

        container.ResizeTo(container.Width, 200);
        container.IsSelected = true;

        var (pageRows, maxScroll) = TableRenderer.ScrollMetrics(
            plot, (container.ViewWidth, container.ViewHeight), 1f);
        Assert.True(pageRows > 1, "the fixture must be tall enough for a multi-row page");
        Assert.True(maxScroll > 2 * pageRows, "the fixture must be longer than two pages");

        ddvm.ScrollSelectedTable(+1);
        Assert.Equal(pageRows, plot.TableViewScrollIndex);

        ddvm.ScrollSelectedTable(+1);
        Assert.Equal(2 * pageRows, plot.TableViewScrollIndex);

        ddvm.ScrollSelectedTable(-1);
        Assert.Equal(pageRows, plot.TableViewScrollIndex);
    }

    [Fact]
    public void PageScroll_ClampsAtBothEnds()
    {
        var ddvm      = MakeDisplay();
        var container = MakeSweptTable(ddvm, rows: 50);
        var plot      = container.PlotVM.Plot;

        container.ResizeTo(container.Width, 200);
        container.IsSelected = true;

        var (_, maxScroll) = TableRenderer.ScrollMetrics(
            plot, (container.ViewWidth, container.ViewHeight), 1f);

        for (int i = 0; i < 20; i++) ddvm.ScrollSelectedTable(+1);
        Assert.Equal(maxScroll, plot.TableViewScrollIndex);

        for (int i = 0; i < 20; i++) ddvm.ScrollSelectedTable(-1);
        Assert.Equal(0, plot.TableViewScrollIndex);
    }

    /// <summary>The keystroke is scoped to the selection: an unselected table never scrolls.</summary>
    [Fact]
    public void PageScroll_LeavesUnselectedTablesAlone()
    {
        var ddvm       = MakeDisplay();
        var selected   = MakeSweptTable(ddvm, rows: 50);
        var unselected = MakeSweptTable(ddvm, rows: 50);

        selected.ResizeTo(selected.Width, 200);
        unselected.ResizeTo(unselected.Width, 200);
        selected.IsSelected   = true;
        unselected.IsSelected = false;

        ddvm.ScrollSelectedTable(+1);

        Assert.True(selected.PlotVM.Plot.TableViewScrollIndex > 0);
        Assert.Equal(0, unselected.PlotVM.Plot.TableViewScrollIndex);
    }

    /// <summary>A selected NON-table plot is not a table — the keystroke does nothing at all.</summary>
    [Fact]
    public void PageScroll_IgnoresNonTablePlots()
    {
        var ddvm      = MakeDisplay();
        var container = ddvm.AddPlot(PlotType.Rect);
        container.IsSelected = true;

        ddvm.ScrollSelectedTable(+1);   // must not throw
        Assert.Equal(0, container.PlotVM.Plot.TableViewScrollIndex);
    }

    [Fact]
    public void HomeAndEnd_JumpSelectedTableToFirstRowAndLastPage()
    {
        var ddvm      = MakeDisplay();
        var container = MakeSweptTable(ddvm, rows: 50);
        var plot      = container.PlotVM.Plot;

        container.ResizeTo(container.Width, 200);
        container.IsSelected = true;

        var (_, maxScroll) = TableRenderer.ScrollMetrics(
            plot, (container.ViewWidth, container.ViewHeight), 1f);
        Assert.True(maxScroll > 0, "the fixture must be longer than one page");

        ddvm.ScrollSelectedTableToEdge(toEnd: true);
        Assert.Equal(maxScroll, plot.TableViewScrollIndex);

        ddvm.ScrollSelectedTableToEdge(toEnd: false);
        Assert.Equal(0, plot.TableViewScrollIndex);
    }

    /// <summary>End stops at the LAST PAGE, not the last row — the final row must not sit alone at
    /// the top of an otherwise blank table.</summary>
    [Fact]
    public void End_LandsOnTheLastPage_NotTheLastRow()
    {
        var ddvm      = MakeDisplay();
        var container = MakeSweptTable(ddvm, rows: 50);
        var plot      = container.PlotVM.Plot;

        container.ResizeTo(container.Width, 200);
        container.IsSelected = true;

        ddvm.ScrollSelectedTableToEdge(toEnd: true);

        int rows = TableRenderer.RowCount(TableRenderer.BuildColumns(plot));
        Assert.True(plot.TableViewScrollIndex < rows - 1,
            "End must leave a full page of rows on screen, not scroll to the final row");

        // Page Down from the end is a no-op — the two agree on where the bottom is.
        int atEnd = plot.TableViewScrollIndex;
        ddvm.ScrollSelectedTable(+1);
        Assert.Equal(atEnd, plot.TableViewScrollIndex);
    }

    /// <summary>Home / End are scoped to the selection, like the page keys.</summary>
    [Fact]
    public void HomeAndEnd_LeaveUnselectedTablesAlone()
    {
        var ddvm       = MakeDisplay();
        var selected   = MakeSweptTable(ddvm, rows: 50);
        var unselected = MakeSweptTable(ddvm, rows: 50);

        selected.ResizeTo(selected.Width, 200);
        unselected.ResizeTo(unselected.Width, 200);
        selected.IsSelected   = true;
        unselected.IsSelected = false;

        ddvm.ScrollSelectedTableToEdge(toEnd: true);

        Assert.True(selected.PlotVM.Plot.TableViewScrollIndex > 0);
        Assert.Equal(0, unselected.PlotVM.Plot.TableViewScrollIndex);
    }

    // ---- Wiring: the command and the keystroke that reaches it ----

    /// <summary>
    /// The window-level command the Page Up / Page Down key bindings invoke. Both halves of the
    /// feature were already written and NOTHING called them — this gates the command end of the
    /// wiring, <see cref="DataDisplayView_BindsPageAndEdgeGestures"/> the gesture end.
    /// </summary>
    [Fact]
    public void WindowCommands_PageAndEdgeKeys_ScrollTheSelectedTable()
    {
        var window  = new DisplayWindowViewModel();
        var display = window.ActiveTab!.DataDisplay;

        var container = MakeSweptTable(display, rows: 50);
        container.ResizeTo(container.Width, 200);
        container.IsSelected = true;
        var plot = container.PlotVM.Plot;

        window.ScrollTablePageDownCommand.Execute(null);
        int afterDown = plot.TableViewScrollIndex;
        Assert.True(afterDown > 0, "Page Down must advance the scroll index");

        window.ScrollTablePageUpCommand.Execute(null);
        Assert.Equal(0, plot.TableViewScrollIndex);

        window.ScrollTableToBottomCommand.Execute(null);
        Assert.True(plot.TableViewScrollIndex >= afterDown, "End must reach at least the first page down");

        window.ScrollTableToTopCommand.Execute(null);
        Assert.Equal(0, plot.TableViewScrollIndex);
    }

    /// <summary>
    /// With no Table selected the four keys must report themselves NOT executable, so the key binding
    /// leaves the keystroke unhandled instead of consuming it for a scroll that cannot happen.
    /// </summary>
    [Fact]
    public void ScrollCommands_AreGatedOnATableBeingSelected()
    {
        var window  = new DisplayWindowViewModel();
        var display = window.ActiveTab!.DataDisplay;

        var rect = display.AddPlot(PlotType.Rect);
        rect.IsSelected = true;
        Assert.False(display.HasSelectedTable);
        Assert.False(window.ScrollTablePageDownCommand.CanExecute(null));
        Assert.False(window.ScrollTableToBottomCommand.CanExecute(null));

        var table = MakeSweptTable(display, rows: 50);
        table.IsSelected = true;
        Assert.True(display.HasSelectedTable);
        Assert.True(window.ScrollTablePageUpCommand.CanExecute(null));
        Assert.True(window.ScrollTableToTopCommand.CanExecute(null));
    }

    /// <summary>The document binds both gestures. A source scan, because the KeyBindings live in
    /// XAML and nothing else would notice them going missing.</summary>
    [Fact]
    public void DataDisplayView_BindsPageAndEdgeGestures()
    {
        string xaml = System.IO.File.ReadAllText(
            System.IO.Path.Combine(RepoRoot(), "src", "Ui", "Views", "DataDisplay", "DataDisplayView.axaml"));

        Assert.Contains("Gesture=\"PageUp\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ScrollTablePageUpCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("Gesture=\"PageDown\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ScrollTablePageDownCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("Gesture=\"Home\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ScrollTableToTopCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("Gesture=\"End\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ScrollTableToBottomCommand", xaml, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>A page step counts WHOLE visible rows only, so it never skips a row the user
    /// could only half see.</summary>
    [Fact]
    public void PageRows_CountsWholeRowsOnly()
    {
        var ddvm      = MakeDisplay();
        var container = MakeSweptTable(ddvm, rows: 50);
        var plot      = container.PlotVM.Plot;

        container.ResizeTo(container.Width, 200);

        double fs         = plot.FontSize;
        double rowH       = fs * (1 + TableRenderer.RowPaddingFraction);
        double headerH    = fs * (1 + TableRenderer.RowPaddingFraction * 2);
        double dataStartY = headerH + TableRenderer.HeaderToDataRowPadding;
        int    expected   = (int)((container.ViewHeight - dataStartY) / rowH);

        var (pageRows, _) = TableRenderer.ScrollMetrics(
            plot, (container.ViewWidth, container.ViewHeight), 1f);

        Assert.Equal(expected, pageRows);
    }
}

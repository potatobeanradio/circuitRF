// The parts table's header: it scrolls with the rows, it sorts them, its columns resize, and a row's
// location frames the part on the board (owner, 2026-09-23).
//
// This file owns no state and no rule, on RailRfWindow.Mount.cs's terms: the order is
// RailRfViewModel.SortParts's and the camera move is RailRfViewModel.ShowPartOnBoard's. What is here
// is what only a view can do: read a ScrollViewer's offset, keep a MULTIPLE selection (the view
// model holds one primary row), wait for a panel it just showed to have a size, and measure text.
//
// ── ONE WIDTH PER COLUMN, WRITTEN TO EVERY GRID THAT DRAWS IT ──────────────────────────────────
//
// The header and each row are separate Grids, so nothing makes them agree: this file does. The
// header's ColumnDefinitions hold the current widths; a resize writes the header and every row
// Grid that is on screen, and a row Grid that arrives later (the list is virtualized) copies them
// when it loads. The widths live for the window's lifetime and are not saved with the document —
// they are how this reader wants to look at the table, not a fact about the rail.

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using CircuitRF.Ui.RailRf;

namespace CircuitRF.Ui.Views.RailRf;

public partial class RailRfWindow
{
    /// <summary>Each header button's own label, read once from the AXAML so the sort mark is added
    /// to the heading the file declares rather than to a second copy of it here.</summary>
    private readonly Dictionary<Button, string> _partsHeaderLabels = [];

    /// <summary>
    /// Each resizable column: its index in both Grids, the property its cell binds, and that cell's
    /// text for a row. <b>A test holds the index-to-binding pairs to the AXAML's row template</b>
    /// (<c>PartsTableColumnGripperTests</c>): the double-click fit measures THIS text, and a table that
    /// measured one column's text to size another would fit nothing, silently.
    /// </summary>
    internal static readonly (int Column, string Binding, Func<RailPartRowViewModel, string> Text)[] PartsColumns =
    [
        (1, nameof(RailPartRowViewModel.Refdes),            r => r.Refdes),
        (2, nameof(RailPartRowViewModel.PartNumber),        r => r.PartNumber),
        (3, nameof(RailPartRowViewModel.FootprintText),     r => r.FootprintText),
        (4, nameof(RailPartRowViewModel.CapacitanceText),   r => r.CapacitanceText),
        (5, nameof(RailPartRowViewModel.EsrText),           r => r.EsrText),
        (6, nameof(RailPartRowViewModel.SelfResonanceText), r => r.SelfResonanceText),
        (7, nameof(RailPartRowViewModel.InductanceText),    r => r.InductanceText),
        (8, nameof(RailPartRowViewModel.ModelSourceText),   r => r.ModelSourceText),
        (9, nameof(RailPartRowViewModel.PositionText),      r => r.PositionText),
    ];

    /// <summary>The narrowest a drag can make a column — about two characters, so a column is never
    /// dragged to nothing and lost behind its neighbour's gripper.</summary>
    private const double PartsColumnMinWidth = 16;

    /// <summary>What a fitted column adds to its widest text: the 6 px inset from the column's left
    /// edge (headings and cells alike), the cell style's 6 px right margin (<c>TextBlock.cell</c>),
    /// and one pixel for the text's sub-pixel width rounding up.</summary>
    private const double PartsColumnFitSlack = 13;

    /// <summary>Every row Grid currently loaded — the ones a resize has to reach.</summary>
    private readonly HashSet<Grid> _partRowGrids = [];

    /// <summary>The drag in progress: the column, where the pointer started (window coordinates, since
    /// the header itself can scroll under the pointer as the table's extent changes), and the width
    /// the column had then. Column is -1 when no drag is in progress.</summary>
    private (int Column, double StartX, double StartWidth) _partsGrip = (-1, 0, 0);

    /// <summary>Wires the header to the list. Called once, from the constructor.</summary>
    private void WirePartsTable()
    {
        foreach (var button in PartsHeader.Children.OfType<Button>())
            if (button.Content is string label) _partsHeaderLabels[button] = label;

        // Declared AFTER the heading buttons, so a gripper is on top where it overlaps the next one.
        foreach (var (column, _, _) in PartsColumns)
            PartsHeader.Children.Add(PartsColumnGripper(column));

        // The list's ScrollViewer is a template part, so it exists only once the template is applied.
        PartsList.TemplateApplied += (_, e) =>
        {
            if (e.NameScope.Find<ScrollViewer>("PART_ScrollViewer") is not { } rows) return;
            rows.ScrollChanged += (_, _) => PartsHeaderScroll.Offset = new Vector(rows.Offset.X, 0);
        };
    }

    // ── Column grippers ───────────────────────────────────────────────────────────────────────

    private Border PartsColumnGripper(int column)
    {
        var grip = new Border
        {
            Classes             = { "partsgrip" },
            Width               = 7,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            // Straddles the boundary: 3 px inside this column, 4 past it.
            Margin              = new Thickness(0, 0, -4, 0),
            Child               = new Border(),
        };
        Grid.SetColumn(grip, column);
        ToolTip.SetTip(grip, "Drag to resize this column. Double-click to fit it to its widest entry.");

        grip.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed) return;
            e.Handled = true;

            if (e.ClickCount >= 2)
            {
                _partsGrip = (-1, 0, 0);
                FitPartsColumn(column);
                return;
            }

            _partsGrip = (column, e.GetPosition(this).X, PartsHeader.ColumnDefinitions[column].ActualWidth);
            e.Pointer.Capture(grip);
        };
        grip.PointerMoved += (_, e) =>
        {
            if (_partsGrip.Column != column) return;
            double width = _partsGrip.StartWidth + e.GetPosition(this).X - _partsGrip.StartX;
            SetPartsColumnWidth(column, Math.Max(PartsColumnMinWidth, width));
        };
        grip.PointerReleased     += (_, e) => { _partsGrip = (-1, 0, 0); e.Pointer.Capture(null); };
        grip.PointerCaptureLost  += (_, _) => _partsGrip = (-1, 0, 0);
        return grip;
    }

    /// <summary>One column, one width, on the header and on every row on screen.</summary>
    private void SetPartsColumnWidth(int column, double width)
    {
        var length = new GridLength(Math.Round(width));
        PartsHeader.ColumnDefinitions[column].Width = length;
        foreach (var row in _partRowGrids)
            if (row.ColumnDefinitions.Count > column) row.ColumnDefinitions[column].Width = length;
    }

    /// <summary>
    /// The narrowest width that shows the whole of every entry in the column — over EVERY row of the
    /// table, not only the ones on screen, since the list is virtualized and the widest entry is as
    /// likely to be scrolled away as not — and of the heading itself.
    /// </summary>
    /// <remarks>
    /// Measured in the cells' own font: the family and size are read off a row cell on screen where
    /// there is one, falling back to the heading's, and an indicative ESR is measured ITALIC because
    /// that is how it is drawn (and italic runs wider).
    /// </remarks>
    private void FitPartsColumn(int column)
    {
        if (Vm is not { } vm) return;
        var entry = PartsColumns.First(c => c.Column == column);

        var heading = PartsHeader.Children.OfType<Button>().FirstOrDefault(b => Grid.GetColumn(b) == column);
        var cell = _partRowGrids.SelectMany(g => g.Children.OfType<TextBlock>()).FirstOrDefault();
        var family = cell?.FontFamily ?? heading?.FontFamily ?? FontFamily;
        double cellSize = cell?.FontSize ?? heading?.FontSize ?? 10;

        double Measure(string text, double size, FontWeight weight, FontStyle style)
        {
            if (text.Length == 0) return 0;
            using var layout = new TextLayout(text, new Typeface(family, style, weight), size, null);
            return layout.WidthIncludingTrailingWhitespace;
        }

        double widest = heading?.Content is string label
            ? Measure(label, heading.FontSize, heading.FontWeight, FontStyle.Normal)
            : 0;

        foreach (var row in vm.Parts)
        {
            var style = column == 5 && row.IsEsrIndicative ? FontStyle.Italic : FontStyle.Normal;
            widest = Math.Max(widest, Measure(entry.Text(row), cellSize, FontWeight.Normal, style));
        }

        SetPartsColumnWidth(column, Math.Max(PartsColumnMinWidth, Math.Ceiling(widest + PartsColumnFitSlack)));
    }

    /// <summary>A row Grid has arrived — copy the current widths onto it.</summary>
    private void OnPartRowGridLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not Grid row) return;
        _partRowGrids.Add(row);

        var header = PartsHeader.ColumnDefinitions;
        for (int i = 0; i < header.Count && i < row.ColumnDefinitions.Count; i++)
            if (row.ColumnDefinitions[i].Width != header[i].Width)
                row.ColumnDefinitions[i].Width = header[i].Width;
    }

    private void OnPartRowGridUnloaded(object? sender, RoutedEventArgs e)
    {
        if (sender is Grid row) _partRowGrids.Remove(row);
    }

    /// <summary>A header click — one step of ascending, descending, document order.</summary>
    private void OnPartsHeaderClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || sender is not Button { Tag: string tag }
            || !Enum.TryParse<RailPartsSortColumn>(tag, out var column))
            return;

        // The view model re-seats its ONE primary row; the rest of a multiple selection is the
        // ListBox's alone, and clearing the collection takes it — so it is put back here.
        var selected = PartsList.SelectedItems?.OfType<RailPartRowViewModel>().ToList() ?? [];

        vm.SortParts(column);

        if (selected.Count > 1 && PartsList.SelectedItems is { } items)
            foreach (var row in selected)
                if (!items.Contains(row)) items.Add(row);

        MarkPartsSort(vm);
    }

    /// <summary>▲ or ▼ after the sorted column's heading, and nothing after the others — without it
    /// the third click's return to the document's order looks like a click that did nothing.</summary>
    private void MarkPartsSort(RailRfViewModel vm)
    {
        foreach (var (button, label) in _partsHeaderLabels)
            button.Content = Equals(button.Tag, vm.PartsSortColumn.ToString())
                ? label + (vm.PartsSortDescending ? " ▼" : " ▲")
                : label;
    }

    /// <summary>Double-click on a row's location: frame that part on the board.</summary>
    private void OnPartLocationDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Vm is not { } vm || (sender as Control)?.DataContext is not RailPartRowViewModel row) return;
        e.Handled = true;

        if (vm.ShowBoard)
        {
            vm.ShowPartOnBoard(row);
            return;
        }

        // A hidden board is SHOWN — the gesture asks to see the part, and a camera move on a panel
        // nobody can see is a double-click that did nothing. The canvas has no size until the next
        // layout pass, and ZoomToRegion does nothing without one, so the move waits for it.
        vm.ShowBoard = true;
        Dispatcher.UIThread.Post(() => vm.ShowPartOnBoard(row), DispatcherPriority.Loaded);
    }
}

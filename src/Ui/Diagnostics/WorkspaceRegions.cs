using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using CircuitRF.Ui.Theming;
using Dock.Avalonia.Controls;
using CircuitRF.Ui.Views.Messages;
using CircuitRF.Ui.Views.Palette;
using CircuitRF.Ui.Views.ProjectTree;
using CircuitRF.Ui.Views.Properties;

namespace CircuitRF.Ui.Diagnostics;

/// <summary>
/// The parts of the workspace window a reader has to be able to name, numbered — the legend of the
/// <c>workspace-regions</c> figure and the rows of the table beside it, from one list.
///
/// <para><b>Every region is located by finding its real control</b>, never by a coordinate. A dot
/// placed at "somewhere around x=180" is a screenshot with extra steps: it is right until any panel
/// changes width, and then it is wrong without saying so. Here a region that cannot be found fails
/// the documentation run and names itself, which is the same contract the rest of the factory
/// runs on.</para>
///
/// <para>The order is the order a reader meets them: the chrome across the top, then round the
/// panels, ending in the document they all point at.</para>
/// </summary>
public static class WorkspaceRegions
{
    /// <summary>Which corner of the region's own box the number is pinned inside.</summary>
    public enum Pin { TopLeft, TopRight, BottomLeft, BottomRight }

    /// <summary>One numbered region.</summary>
    /// <param name="Index">Its number, in the figure and in the table.</param>
    /// <param name="Title">What it is called in the application.</param>
    /// <param name="What">One sentence on what it is for.</param>
    /// <param name="Locate">Finds the control the number is pinned to, or returns null.</param>
    /// <param name="At">Which corner of that control the number sits in.</param>
    public sealed record Region(
        int Index, string Title, string What, Func<Control, Visual?> Locate, Pin At);

    private static Func<Control, Visual?> ByType<T>() where T : Visual
        => root => root.GetVisualDescendants().OfType<T>().FirstOrDefault();

    public static readonly IReadOnlyList<Region> Catalog =
    [
        new(1, "Menu bar",
            "Every command in the application. On macOS these are in the system menu bar at the top "
          + "of the screen instead of in the window.",
            ByType<Menu>(), Pin.TopRight),

        new(2, "Toolbar",
            "The commands used most often: new, open and save, cut/copy/paste, undo/redo, run and "
          + "stop, and the three buttons that show and hide the tool panels.",
            root => root.GetVisualDescendants().OfType<StackPanel>()
                        .FirstOrDefault(p => p.Classes.Contains("Toolbar")),
            Pin.TopRight),

        new(3, "Project panel",
            "The workspace on disk: its cells, and the schematic, symbol and layout views inside "
          + "each one. Double-click a view to open it.",
            ByType<ProjectTreeView>(), Pin.BottomLeft),

        new(4, "Properties panel",
            "The parameters of whatever is selected in the document, editable in place. The Analyses "
          + "tab beside it holds the analyses the open test bench will run.",
            ByType<PropertiesView>(), Pin.BottomLeft),

        // Pinned to the document dock rather than to the open schematic: the number belongs beside
        // the TAB STRIP, which is what makes "one document per tab" readable, and the top-left of the
        // schematic itself is its own toolbar's first button.
        new(5, "Document area",
            "The documents you have open, one per tab — schematics, symbols, layouts, data displays. "
          + "Tabs can be split, or dragged out into a window of their own.",
            ByType<DocumentControl>(), Pin.TopRight),

        new(6, "Library panel",
            "Every component you can place, filtered by category and searchable. Click a tile to arm "
          + "it, then click on the canvas to drop it.",
            ByType<PaletteToolView>(), Pin.BottomRight),

        new(7, "Messages panel",
            "What the application did and what it thinks of it: files opened, netlists elaborated, "
          + "runs finished, and any warning or error, each linking to what it is about.",
            ByType<MessagesView>(), Pin.TopRight),
    ];

    // ── Placing the numbers ───────────────────────────────────────────────────

    /// <summary>Inset from the region's own corner, in device-independent pixels.</summary>
    private const double Inset = 5;

    private const double Diameter = 22;

    /// <summary>
    /// Put <paramref name="workspace"/> under a transparent overlay the numbers will be drawn on.
    ///
    /// <para>An overlay rather than an adorner, and a separate <see cref="Fill"/> rather than one
    /// call, because the numbers can only be placed once every panel has been arranged: this is the
    /// same two-step the toolbar's own indexed figure uses. The overlay is a single-cell
    /// <see cref="Grid"/>, so the workspace is measured and arranged exactly as it would be on its
    /// own — a figure whose panels moved because it was being annotated would be no use.</para>
    /// </summary>
    public static Control Overlay(Control workspace)
    {
        var grid = new Grid();
        grid.Children.Add(workspace);
        grid.Children.Add(new Canvas { ClipToBounds = false, IsHitTestVisible = false });
        return grid;
    }

    /// <summary>
    /// Draw a numbered dot on each region of the now-laid-out <paramref name="overlay"/> built by
    /// <see cref="Overlay"/>. Call from the fixture's <c>AfterLayout</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">A region's control is missing or unarranged.</exception>
    public static void Fill(Control overlay)
    {
        if (overlay is not Grid grid || grid.Children.Count != 2 || grid.Children[1] is not Canvas canvas)
            throw new InvalidOperationException(
                "the workspace figure was not wrapped by WorkspaceRegions.Overlay, so there is nowhere "
              + "to draw the region numbers.");

        var root    = (Control)grid.Children[0];
        var variant = ThemeService.CurrentVariant;

        foreach (var region in Catalog)
        {
            var target = region.Locate(root)
                ?? throw new InvalidOperationException(
                    $"workspace region {region.Index} ('{region.Title}') is not in the captured window. "
                  + "The figure's numbers are pinned to real controls, so either the control has been "
                  + "renamed or replaced — in which case update WorkspaceRegions — or the fixture no "
                  + "longer opens the panel it lives in.");

            var box = BoundsIn(target, root)
                ?? throw new InvalidOperationException(
                    $"workspace region {region.Index} ('{region.Title}') has no position relative to the "
                  + "captured window — it is in the tree but was never arranged.");

            if (box.Width < Diameter || box.Height < Diameter)
                throw new InvalidOperationException(
                    $"workspace region {region.Index} ('{region.Title}') arranged at {box.Width:F0}x"
                  + $"{box.Height:F0}, too small to carry its number. It is collapsed or hidden in this "
                  + "capture.");

            var dot = CalloutDot.Build(region.Index, variant, Diameter);
            var (x, y) = region.At switch
            {
                Pin.TopLeft    => (box.Left  + Inset,             box.Top    + Inset),
                Pin.TopRight   => (box.Right - Inset - Diameter,  box.Top    + Inset),
                Pin.BottomLeft => (box.Left  + Inset,             box.Bottom - Inset - Diameter),
                _              => (box.Right - Inset - Diameter,  box.Bottom - Inset - Diameter),
            };
            Canvas.SetLeft(dot, Math.Round(x));
            Canvas.SetTop(dot, Math.Round(y));
            canvas.Children.Add(dot);
        }
    }

    /// <summary>Where <paramref name="visual"/> sits inside <paramref name="root"/>, or null.</summary>
    private static Rect? BoundsIn(Visual visual, Visual root)
    {
        var origin = visual.TranslatePoint(default, root);
        return origin is { } p ? new Rect(p, visual.Bounds.Size) : null;
    }
}

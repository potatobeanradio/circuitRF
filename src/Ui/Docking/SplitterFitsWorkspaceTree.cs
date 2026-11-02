using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dock.Controls.ProportionalStackPanel;
using Dock.Model.Controls;
using CircuitRF.Ui.ViewModels.Dock;

namespace CircuitRF.Ui.Docking;

/// <summary>
/// A double-click on the splitter beside the Workspace tree sizes the tree's pane to its rows: the height of what is
/// listed (the rows the filter and search leave, expanded as they stand) plus the pane's own chrome. With few rows the pane SHRINKS, which is the
/// point — the space goes to the Inspector-type panel on the other side of the splitter. With many it grows, but never
/// past what leaves the other pane <see cref="OtherPaneShare"/> of the two and at least <see cref="OtherPaneMinPx"/>.
///
/// <para>Only the two panes the splitter divides move; a third pane in the same column keeps its height. The proportion
/// is written on Dock's PRESENTER at local value, as <see cref="Views.Palette.PaletteColumnPin"/> writes one and for its
/// reason: the theme binds it two-way to the dockable's, so the change is what a save records, and writing the dockable
/// instead would not move the layout.</para>
/// </summary>
internal static class SplitterFitsWorkspaceTree
{
    /// <summary>The share of the two panes the other one always keeps.</summary>
    internal const double OtherPaneShare = 0.4;

    /// <summary>The height the other pane always keeps, when the two have room for it.</summary>
    internal const double OtherPaneMinPx = 120;

    /// <summary>The smallest tree pane: its chrome and about one row.</summary>
    internal const double OneRowPx = 24;

    private static bool _installed;

    internal static void Install()
    {
        if (_installed) return;
        _installed = true;
        // handledEventsToo: the splitter handles its own press (it starts a drag), and a double-click is two presses
        // with no movement between them, so the drag it starts moves nothing.
        InputElement.PointerPressedEvent.AddClassHandler<ProportionalStackPanelSplitter>((splitter, e) =>
        {
            if (e.ClickCount != 2 || !e.GetCurrentPoint(splitter).Properties.IsLeftButtonPressed) return;
            Dispatcher.UIThread.Post(() => Fit(splitter), DispatcherPriority.Background);
        }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private static void Fit(ProportionalStackPanelSplitter splitter)
    {
        if (splitter.FindAncestorOfType<ProportionalStackPanel>() is not { Orientation: Orientation.Vertical } panel) return;
        var children = panel.Children.ToList();
        int at = children.FindIndex(c => ReferenceEquals(c, splitter) || c.GetVisualDescendants().Contains(splitter));
        if (at <= 0 || at >= children.Count - 1) return;
        var above = children[at - 1];
        var below = children[at + 1];
        if (IsSplitter(above) || IsSplitter(below)) return;

        bool treeAbove = TreeOf(above) is not null;
        var  treePane  = treeAbove ? above : TreeOf(below) is not null ? below : null;
        if (treePane is null || TreeOf(treePane) is not { IsVisible: true } scroll) return;
        var otherPane = treeAbove ? below : above;

        // The rows' own height — the TreeView's DESIRED height, which the scroller measured with unbounded height and
        // which follows the filter, the search and what is expanded. NOT the scroller's Extent: the TreeView is stretched
        // to fill the viewport, so a tree shorter than its pane (a filtered one, nearly always) reports the pane's own
        // height there, and the pane never shrank.
        double rows    = scroll.Content is Control content ? content.DesiredSize.Height : scroll.Extent.Height;
        double chrome  = treePane.Bounds.Height - scroll.Viewport.Height;
        double wanted  = chrome + rows + 2;
        double minTree = chrome + OneRowPx;

        double pTree  = ProportionalStackPanel.GetProportion(treePane);
        double pOther = ProportionalStackPanel.GetProportion(otherPane);
        if (!Fitted(pTree, pOther, treePane.Bounds.Height, otherPane.Bounds.Height, wanted, minTree, out double nTree, out double nOther))
            return;
        ProportionalStackPanel.SetProportion(treePane, nTree);
        ProportionalStackPanel.SetProportion(otherPane, nOther);
    }

    /// <summary>
    /// The two proportions that give the tree pane <paramref name="wanted"/> px out of the pair's height, clamped to at
    /// least <paramref name="minTree"/> and to what leaves the other pane its share; the pair's total proportion is kept,
    /// so no other pane in the column moves. False when the inputs give nothing to fit (no height, a NaN proportion).
    /// </summary>
    internal static bool Fitted(double pTree, double pOther, double hTree, double hOther, double wanted, double minTree,
                                out double newTree, out double newOther)
    {
        newTree = pTree; newOther = pOther;
        double pair  = pTree + pOther;
        double pairH = hTree + hOther;
        if (!double.IsFinite(pair) || pair <= 0 || !double.IsFinite(pairH) || pairH <= 0 || !double.IsFinite(wanted)) return false;

        double keepOther = Math.Min(Math.Max(OtherPaneShare * pairH, OtherPaneMinPx), pairH);
        double maxTree   = Math.Max(pairH - keepOther, 0);
        double treeH     = Math.Clamp(wanted, Math.Min(minTree, maxTree), maxTree);
        newTree  = pair * treeH / pairH;
        newOther = pair - newTree;
        return true;
    }

    /// <summary>The Workspace tree's scroller in <paramref name="pane"/>, when the pane is showing the Workspace tree.</summary>
    private static ScrollViewer? TreeOf(Control pane)
    {
        if (pane.DataContext is not IToolDock { ActiveDockable: ProjectTreeTool }) return null;
        return pane.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault(s => s.Name == "TreeScroll");
    }

    /// <summary>Dock wraps every child of the column in a presenter, splitters included.</summary>
    private static bool IsSplitter(Control control)
        => control is ProportionalStackPanelSplitter
        || control is ContentPresenter { Child: ProportionalStackPanelSplitter };
}

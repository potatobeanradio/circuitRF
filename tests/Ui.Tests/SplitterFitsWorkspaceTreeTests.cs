using CircuitRF.Ui.Docking;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>The double-click on the splitter beside the Workspace tree: the arithmetic that sizes the pane.</summary>
public sealed class SplitterFitsWorkspaceTreeTests
{
    // Two panes of 400 px each, at 0.3 + 0.3 of a column that also holds a third pane.
    private const double P = 0.3, H = 400;

    [Fact]
    public void AShortTree_ShrinksItsPane_AndTheOtherTakesTheRest_TheColumnsOtherPaneUnmoved()
    {
        Assert.True(SplitterFitsWorkspaceTree.Fitted(P, P, H, H, wanted: 200, minTree: 60, out double tree, out double other));
        Assert.Equal(P * 2 * 200 / 800, tree, 12);
        Assert.Equal(P * 2, tree + other, 12);                         // the pair's total: a third pane keeps its height
    }

    [Fact]
    public void ALongTree_GrowsOnlyUntilTheOtherPaneKeepsItsShare()
    {
        Assert.True(SplitterFitsWorkspaceTree.Fitted(P, P, H, H, wanted: 5000, minTree: 60, out double tree, out double other));
        Assert.Equal(P * 2 * (1 - SplitterFitsWorkspaceTree.OtherPaneShare), tree, 12);
        Assert.Equal(P * 2, tree + other, 12);
    }

    [Fact]
    public void AnEmptyTree_KeepsItsChromeAndARow()
    {
        Assert.True(SplitterFitsWorkspaceTree.Fitted(P, P, H, H, wanted: 10, minTree: 60, out double tree, out _));
        Assert.Equal(P * 2 * 60 / 800, tree, 12);
    }

    [Fact]
    public void NothingToFit_IsRefused()
    {
        Assert.False(SplitterFitsWorkspaceTree.Fitted(double.NaN, P, H, H, 200, 60, out _, out _));
        Assert.False(SplitterFitsWorkspaceTree.Fitted(P, P, 0, 0, 200, 60, out _, out _));
    }
}

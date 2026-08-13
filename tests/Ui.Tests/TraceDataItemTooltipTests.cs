// ================================================================
//  TraceDataItemTooltipTests.cs  —  brief-dd-network-params-and-stability.md §3
//
//  ToolTip.Tip was bound as `{Binding DisabledReason, TargetNullValue={Binding Label}}`. Avalonia
//  does not evaluate a `{Binding}` markup extension used as TargetNullValue — the binding OBJECT
//  itself becomes the fallback value, rendered via ToString(), which is literally
//  "Avalonia.Data.CompiledBinding". That showed on every enabled item (the ones whose
//  DisabledReason is null). Fixed by a plain-string VM property, TooltipText.
// ================================================================

using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class TraceDataItemTooltipTests
{
    [Fact]
    public void EnabledMatrixElementItem_TooltipText_IsLabel_NeverATypeName()
    {
        var item = new TraceDataItem(null!, MatrixType.S, 0, 0, omitFilePrefix: true);

        Assert.True(item.IsEnabled);
        Assert.Null(item.DisabledReason);
        Assert.Equal(item.Label, item.TooltipText);
        Assert.DoesNotContain("Binding", item.TooltipText);
        Assert.DoesNotContain("Avalonia", item.TooltipText);
    }

    [Fact]
    public void EnabledDerivedItem_TooltipText_IsLabel()
    {
        var item = new TraceDataItem(null!, DerivedParameters.Mu, PlotType.Rect, omitFilePrefix: true);

        Assert.True(item.IsEnabled);
        Assert.Null(item.DisabledReason);
        Assert.Equal(item.Label, item.TooltipText);
    }

    [Fact]
    public void DisabledDerivedItem_TooltipText_IsTheDisabledReason_NotTheLabel()
    {
        // A scalar-vs-frequency metric is disabled on a Smith plot (R-stb-5).
        var item = new TraceDataItem(null!, DerivedParameters.Mu, PlotType.Smith, omitFilePrefix: true);

        Assert.False(item.IsEnabled);
        Assert.NotNull(item.DisabledReason);
        Assert.Equal(item.DisabledReason, item.TooltipText);
        Assert.NotEqual(item.Label, item.TooltipText);
    }

    [Fact]
    public void CubeBoundItem_TooltipText_IsLabel()
    {
        var item = new TraceDataItem(null!, "S", System.Array.Empty<AxisSlice>(), "S(1,1)");

        Assert.True(item.IsEnabled);
        Assert.Null(item.DisabledReason);
        Assert.Equal("S(1,1)", item.TooltipText);
    }
}

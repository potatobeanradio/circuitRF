// Escape in the Data Display: the first press disarms every armed tool, a press with none armed drops the selection; and the
// view takes Escape back from the workspace window's own Escape binding, which claims the key before it is routed.

using System.IO;
using System.Text.RegularExpressions;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class DataDisplayEscapeTests
{
    [Fact]
    public void FirstEscapeDisarmsTheTools_TheSecondDropsTheSelection()
    {
        var window = new DisplayWindowViewModel();
        var display = window.DataDisplay!;
        display.SelectAll();
        Assert.True(display.HasAnySelection);

        display.ArmMarkerTool(MarkerFunction.Maximum);
        Assert.True(display.AnyToolArmed);
        window.DeselectAllCommand.Execute(null);
        Assert.False(display.AnyToolArmed);
        Assert.True(display.HasAnySelection);           // the tool went, the selection stayed

        display.ToggleZoomBox();
        window.DeselectAllCommand.Execute(null);
        Assert.False(display.AnyToolArmed);
        Assert.True(display.HasAnySelection);

        window.DeselectAllCommand.Execute(null);
        Assert.False(display.HasAnySelection);
    }

    [Fact]
    public void TheViewTakesEscapeBackFromTheWindowBinding()
    {
        string dir = Directory.GetCurrentDirectory();
        while (!File.Exists(Path.Combine(dir, "circuitRF.slnx"))) dir = Path.GetDirectoryName(dir)!;
        string code = File.ReadAllText(Path.Combine(dir, "src", "Ui", "Views", "DataDisplay", "DataDisplayView.axaml.cs"));
        code = Regex.Replace(Regex.Replace(code, @"/\*.*?\*/", " ", RegexOptions.Singleline), @"//[^\n]*", " ");

        Assert.Matches(@"AddHandler\(\s*KeyDownEvent\s*,\s*OnEscapeTunnel\s*,\s*RoutingStrategies\.Tunnel\s*,\s*handledEventsToo:\s*true\s*\)", code);
        Assert.Matches(@"Key\.Escape[^;]*IsKeyboardFocusWithin\)\s*e\.Handled\s*=\s*false", code);
    }
}

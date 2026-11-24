using CircuitRF.Ui.Filters;
using Xunit;

namespace CircuitRF.Ui.Tests.Filters;

/// <summary>The Designer's Source and Load are resistances: a complex value, a unit or a name is refused as typed.</summary>
public sealed class LcFilterImpedanceInputTests
{
    [Theory]
    [InlineData("50+j10")]
    [InlineData("50-10j")]
    [InlineData("j25")]
    [InlineData("50 Ohm")]
    [InlineData("Rs")]
    [InlineData("(50,10)")]
    public void ANonRealImpedanceIsRefused_AndTheBoxKeepsItsValue(string typed)
    {
        var vm = new LcFilterDesignerViewModel { SourceText = "75", LoadText = "25" };
        vm.SourceText = typed;
        vm.LoadText = typed;
        Assert.Equal("75", vm.SourceText);
        Assert.Equal("25", vm.LoadText);
    }

    [Theory]
    [InlineData("50")]
    [InlineData("12.5")]
    [InlineData("1e2")]
    [InlineData("")]
    [InlineData("5")]
    public void ARealNumberIsTaken(string typed)
    {
        var vm = new LcFilterDesignerViewModel();
        vm.SourceText = typed;
        vm.LoadText = typed;
        Assert.Equal(typed, vm.SourceText);
        Assert.Equal(typed, vm.LoadText);
    }
}

using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.Tests;

// ── Phase L0c gate 6: FallbackPalette — deterministic, golden-value colors ──

public class FallbackPaletteTests
{
    [Theory]
    [InlineData(0, 0, 98, 135, 217)]
    [InlineData(1, 0, 217, 98, 217)]
    [InlineData(2, 0, 98, 147, 217)]
    [InlineData(0, 1, 217, 181, 98)]
    [InlineData(5, 3, 98, 217, 115)]
    [InlineData(21, 4, 121, 217, 98)]
    [InlineData(100, 1, 109, 98, 217)]
    [InlineData(-1, 0, 98, 141, 217)]
    public void For_GoldenColorValues(int layer, int datatype, byte r, byte g, byte b)
    {
        var def = FallbackPalette.For(new LayerKey(layer, datatype));
        Assert.Equal(new Rgba(r, g, b), def.Color);
    }

    [Fact]
    public void For_SameKeyTwice_YieldsIdenticalColor()
    {
        var key = new LayerKey(7, 2);
        var a = FallbackPalette.For(key);
        var b = FallbackPalette.For(key);
        Assert.Equal(a.Color, b.Color);
    }

    [Fact]
    public void For_SameKeyAcrossSeparateCalls_IsStableAcrossASimulatedReload()
    {
        // "Reload" is simulated by calling For() again in a fresh scope — there is no cached
        // state in FallbackPalette (it is explicitly stateless), so this also guards against a
        // future regression that accidentally introduces per-instance or per-process state.
        var key = new LayerKey(1000, 999);
        var first  = FallbackPalette.For(key).Color;
        var second = FallbackPalette.For(key).Color;
        Assert.Equal(first, second);
    }

    [Fact]
    public void For_SetsExpectedNonColorFields()
    {
        var key = new LayerKey(3, 7);
        var def = FallbackPalette.For(key);

        Assert.Equal(key, def.Key);
        Assert.Equal("L3/7", def.Name);
        Assert.Equal(0.35, def.FillOpacity);
        Assert.Equal(3 * 1000 + 7, def.ZOrder);
        Assert.True(def.Visible);
        Assert.True(def.Selectable);
    }
}

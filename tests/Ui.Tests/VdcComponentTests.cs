using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Gate tests for the Vdc component UI layer: registry, palette, and ToneSource Vdc param.
/// </summary>
public class VdcComponentTests
{
    // ── Test 5: ToneSource_VdcHidden ────────────────────────────────────────

    [Fact]
    public void ToneSource_DefaultParameters_IncludesHiddenVdc()
    {
        var ps = ComponentTypeRegistry.DefaultParameters(SymbolKind.ToneSource, 0);
        Assert.Contains(ps, p => p.Name == "Vdc");
        Assert.False(ps.First(p => p.Name == "Vdc").ShowOnSchematic);
    }

    // ── Test 7: Vdc_Displayed/Palette ───────────────────────────────────────

    [Fact]
    public void Vdc_DefaultParameters_ShowsVdc()
    {
        var ps = ComponentTypeRegistry.DefaultParameters(SymbolKind.Vdc, 0);
        Assert.Single(ps);
        Assert.Equal("Vdc", ps[0].Name);
        Assert.True(ps[0].ShowOnSchematic);
    }

    [Fact]
    public void Vdc_EngineReference_IsVdc()
    {
        Assert.Equal("Vdc", ComponentTypeRegistry.EngineReference(SymbolKind.Vdc));
    }

    [Fact]
    public void Palette_HasNoLegacyVComponent()
    {
        // The old "V" SymbolKind (VoltageSource) must not appear anywhere.
        var allKinds = Enum.GetValues<SymbolKind>();
        Assert.DoesNotContain(allKinds, k => k.ToString() == "VoltageSource");
    }

    // ── Test 8: No_V_Component ───────────────────────────────────────────────

    [Fact]
    public void TryParseCode_V_ResolvesToVdc()
    {
        bool ok = ComponentTypeRegistry.TryParseCode("V", out var kind, out _);
        Assert.True(ok);
        Assert.Equal(SymbolKind.Vdc, kind);
    }

    [Fact]
    public void TryParseCode_VDC_ResolvesToVdc()
    {
        bool ok = ComponentTypeRegistry.TryParseCode("VDC", out var kind, out _);
        Assert.True(ok);
        Assert.Equal(SymbolKind.Vdc, kind);
    }
}

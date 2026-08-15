// ================================================================
//  HarmonicaInputsRgsTests.cs  —  R8C §3.4
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CircuitRF.Harmonica;
using CircuitRF.Ui.Harmonica;
using CircuitRF.Ui.Views.Harmonica;
using Xunit;

namespace CircuitRF.Ui.Tests.Harmonica;

public sealed class HarmonicaInputsRgsTests
{
    private static CircuitModel SddModel(DutCapacitances? caps = null) => new()
    {
        Dut = new DutSpec
        {
            Kind = DutKind.Sdd, TypeName = "SDD",
            Parameters = new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["I[1,0]"] = "_v1/1e6",
                ["I[2,0]"] = "0.08*(_v1+3)*(_v1+3)*tanh(0.4*_v2)",
            },
            Capacitances = caps ?? DutCapacitances.None,
        },
        Bias     = new BiasSpec { Vgs = -1.5, Vds = 10 },
        Settings = new HarmonicaSettings { HarmonicCount = 3, FrequencyHz = 2e9 },
    };

    private static CircuitModel NativeFetModel() => new()
    {
        Dut = new DutSpec { Kind = DutKind.NativeFet, TypeName = "FET_Angelov" },
        Bias     = new BiasSpec { Vgs = -1.0, Vds = 5 },
        Settings = new HarmonicaSettings { HarmonicCount = 2, FrequencyHz = 1e9 },
    };

    [Fact]
    public void SddDut_RgsRowSitsImmediatelyBeforeCgs()
    {
        var keys = HarmonicaInputs.Build(SddModel()).Select(i => i.Key).ToArray();
        int rgsIndex = Array.IndexOf(keys, HarmonicaInputs.KeyRgs);
        int cgsIndex = Array.IndexOf(keys, HarmonicaInputs.KeyCgs);

        Assert.True(rgsIndex >= 0, "dut.rgs is missing from an SDD DUT's input list");
        Assert.Equal(cgsIndex - 1, rgsIndex);
    }

    [Fact]
    public void NativeFetDut_HasNeitherRgsNorCgs()
    {
        var keys = HarmonicaInputs.Build(NativeFetModel()).Select(i => i.Key).ToArray();
        Assert.DoesNotContain(HarmonicaInputs.KeyRgs, keys);
        Assert.DoesNotContain(HarmonicaInputs.KeyCgs, keys);
    }

    [Fact]
    public void Apply_ValidValue_SetsRgsOhms()
    {
        var model = HarmonicaInputs.Apply(SddModel(), HarmonicaInputs.KeyRgs, "25", out string? error);
        Assert.Null(error);
        Assert.NotNull(model);
        Assert.Equal(25.0, model!.Dut.Capacitances.RgsOhms);
    }

    [Fact]
    public void Apply_Negative_IsRefused_AndLeavesTheModelUntouched()
    {
        var original = SddModel();
        var result = HarmonicaInputs.Apply(original, HarmonicaInputs.KeyRgs, "-1", out string? error);

        Assert.Null(result);
        Assert.NotNull(error);
        Assert.Equal(0.0, original.Dut.Capacitances.RgsOhms);
    }

    [Fact]
    public void RgsRow_IsEditable_AndNotLocked()
    {
        // ReadoutStripView's inline editor seeds from HarmonicaInput.EditValue (EditText ?? Text),
        // never EditText directly — the same contract every other plain Number row (Vds, Freq, …)
        // relies on without setting EditText itself. Locked=false plus a non-null EditValue is what
        // actually gates the editor (BuildSettingsColumnRow's own DoubleTapped guard).
        var row = HarmonicaInputs.Build(SddModel()).Single(i => i.Key == HarmonicaInputs.KeyRgs);
        Assert.NotNull(row.EditValue);
        Assert.False(row.Locked);
        Assert.True(row.Structural);
        Assert.Equal(HarmonicaInputEntry.Number, row.Entry);
    }

    [Fact]
    public void RgsRow_ReflectsTheModelsCurrentValue()
    {
        var caps  = DutCapacitances.None with { RgsOhms = 12.5 };
        var row = HarmonicaInputs.Build(SddModel(caps)).Single(i => i.Key == HarmonicaInputs.KeyRgs);
        Assert.Equal("12.5", row.Text);
        Assert.Equal("Ω", row.Unit);
    }

    // ══ R9A §2 — rgs moves into the ReadoutStripView Settings/Capacitance chunk ═══════════════════
    // ReadoutStripView cannot be instantiated headlessly, so these reach its private static members
    // through reflection, the pattern HarmonicaR3cStripTests already uses.

    private static readonly Type StripType = typeof(ReadoutStripView);

    private static string[] SettingsColumnKeys()
        => (string[])StripType
            .GetField("SettingsColumnKeys", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

    private static string[] EffectiveSettingsColumnKeys(IReadOnlyDictionary<string, HarmonicaInput> named)
    {
        var method = StripType.GetMethod("EffectiveSettingsColumnKeys",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (string[])method!.Invoke(null, [named])!;
    }

    private static string SettingsWorstCaseValueText(string key)
    {
        var method = StripType.GetMethod("SettingsWorstCaseValueText",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (string)method!.Invoke(null, [key])!;
    }

    private static bool IsCapacitanceKey(string key)
    {
        var method = StripType.GetMethod("IsCapacitanceKey",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (bool)method!.Invoke(null, [key])!;
    }

    [Fact]
    public void SettingsColumnKeys_PlacesRgsImmediatelyBeforeCgs()
    {
        var keys = SettingsColumnKeys();
        int rgsIndex = Array.IndexOf(keys, HarmonicaInputs.KeyRgs);
        int cgsIndex = Array.IndexOf(keys, HarmonicaInputs.KeyCgs);

        Assert.True(rgsIndex >= 0);
        Assert.Equal(cgsIndex - 1, rgsIndex);
    }

    [Fact]
    public void EffectiveSettingsColumnKeys_IncludesRgs_OnlyWhenCgsIsPresent()
    {
        var sddNamed = HarmonicaInputs.Build(SddModel()).ToDictionary(i => i.Key, i => i, StringComparer.Ordinal);
        var fetNamed = HarmonicaInputs.Build(NativeFetModel()).ToDictionary(i => i.Key, i => i, StringComparer.Ordinal);

        Assert.Contains(HarmonicaInputs.KeyRgs, EffectiveSettingsColumnKeys(sddNamed));
        Assert.DoesNotContain(HarmonicaInputs.KeyRgs, EffectiveSettingsColumnKeys(fetNamed));
        Assert.DoesNotContain(HarmonicaInputs.KeyCgs, EffectiveSettingsColumnKeys(fetNamed));
    }

    [Fact]
    public void SettingsWorstCaseValueText_ForRgs_IsNotTheDefaultFallback()
    {
        Assert.NotEqual("0000000000", SettingsWorstCaseValueText(HarmonicaInputs.KeyRgs));
    }

    [Fact]
    public void IsCapacitanceKey_IsFalseForRgs()
    {
        Assert.False(IsCapacitanceKey(HarmonicaInputs.KeyRgs));
    }
}

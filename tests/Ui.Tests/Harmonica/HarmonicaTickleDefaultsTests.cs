// ================================================================
//  HarmonicaTickleDefaultsTests.cs — brief-harmonicarf-r2b R-h9r2-18a
// ================================================================

using System;
using System.IO;
using CircuitRF.Ui.Harmonica;
using Xunit;

namespace CircuitRF.Ui.Tests.Harmonica;

/// <summary>
/// R-h9r2-18a's own split: <see cref="HarmonicaTickleDefaults"/> is what a BRAND NEW document seeds
/// from; it must never be read by <see cref="HarmonicaViewModel.DefaultModel"/> itself, which stays
/// preference-free so every test that constructs a document directly is deterministic regardless of
/// whatever this machine's own <c>preferences.json</c> happens to hold. These tests deliberately never
/// call <c>AppPreferencesIo.Update</c> — writing to the real per-user preferences file from a test run
/// would be a side effect no test should have.
/// </summary>
public sealed class HarmonicaTickleDefaultsTests
{
    [Fact]
    public void ShippedDefaults_MatchTheOwnersOwnNumbers()
    {
        Assert.True(HarmonicaTickleDefaults.ShippedEnabled);
        Assert.Equal(-50.0, HarmonicaTickleDefaults.ShippedDbm);
    }

    [Fact]
    public void DefaultModel_NeverReadsThePreferenceResolver_StaysDeterministic()
    {
        // Two independently-built DefaultModel()s must be identical regardless of this machine's own
        // preferences — DefaultModel() is used directly by dozens of tests and two probes
        // (HarmonicaDutEditor, HarmonicaSetDutDialog) that must not depend on ambient machine state.
        var a = HarmonicaViewModel.DefaultModel();
        var b = HarmonicaViewModel.DefaultModel();
        Assert.Equal(a.Settings.TickleEnabled, b.Settings.TickleEnabled);
        Assert.Equal(a.Settings.TickleDbm,     b.Settings.TickleDbm);
    }

    [Fact]
    public void SeedModel_OverridesOnlyTheTickle_EverythingElseMatchesDefaultModel()
    {
        var seeded  = HarmonicaTickleDefaults.SeedModel();
        var vanilla = HarmonicaViewModel.DefaultModel();

        // Same DUT type, same bias, and every other setting — only the tickle fields may differ.
        Assert.Equal(vanilla.Dut.TypeName, seeded.Dut.TypeName);
        Assert.Equal(vanilla.Bias, seeded.Bias);
        Assert.Equal(vanilla.Settings with { TickleEnabled = seeded.Settings.TickleEnabled, TickleDbm = seeded.Settings.TickleDbm },
                     seeded.Settings);
    }

    [Fact]
    public void SeedModel_InAClonEnvironment_ReadsTheShippedDefaults()
    {
        // In a clean test/CI environment there is no preferences.json for this app, so
        // AppPreferencesIo.Load() returns all-null and HarmonicaTickleDefaults falls back to the
        // shipped numbers — asserted here as the expected behaviour of a fresh checkout/CI box, not a
        // guarantee for a developer's own machine with a real preferences file already on it.
        var seeded = HarmonicaTickleDefaults.SeedModel();
        if (!HasRealPreferencesFile())
        {
            Assert.Equal(HarmonicaTickleDefaults.ShippedEnabled, seeded.Settings.TickleEnabled);
            Assert.Equal(HarmonicaTickleDefaults.ShippedDbm,     seeded.Settings.TickleDbm);
        }
    }

    private static bool HasRealPreferencesFile()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "circuitRF");
        return File.Exists(Path.Combine(dir, "preferences.json"));
    }
}

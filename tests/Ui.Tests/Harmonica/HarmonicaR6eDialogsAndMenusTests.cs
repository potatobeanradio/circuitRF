// ================================================================
//  HarmonicaR6eDialogsAndMenusTests.cs — §5 of
//  brief-harmonicarf-r6e-plot-axis-limits-and-autoscale.md
//
//  §3   the DCIV dialog's Axis limits section and the new HarmonicaPowerSweepAxesDialog:
//       min < max validation, reject-and-keep, the Autoscale checkbox.
//  §4   the fly-menu entries: loadline/DCIV panel's own Autoscale item, and the power-sweep
//       panel's title menu's Autoscale + Axis Limits… items — one property, two surfaces.
// ================================================================

using System;
using System.IO;
using CircuitRF.Harmonica;
using CircuitRF.Ui.Harmonica;
using Xunit;

namespace CircuitRF.Ui.Tests.Harmonica;

public sealed class HarmonicaR6eDialogsAndMenusTests
{
    private static string ReadSource(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitRF.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        string path = Path.Combine([dir!.FullName, .. parts]);
        Assert.True(File.Exists(path), $"source not found at {path}");
        return File.ReadAllText(path);
    }

    private static string ViewSource() =>
        ReadSource("src", "Ui", "Views", "Harmonica", "HarmonicaView.axaml.cs");

    // ══ §3.1/§3.2 — HarmonicaViewModel's write-back methods, tested directly (no Window needed) ══

    private static HarmonicaViewModel NewVm() => new();

    [Theory]
    [InlineData(5.0, 5.0, 0.0, 1.0)]   // X min == X max
    [InlineData(0.0, 1.0, 2.0, 1.0)]   // Y min > Y max
    [InlineData(double.NaN, 1.0, 0.0, 1.0)]
    public void ApplyDcivAxisLimits_RejectsBadInput_LeavingTheStoredWindowUntouched(
        double xMin, double xMax, double yMin, double yMax)
    {
        var vm = NewVm();
        Assert.True(vm.ApplyDcivAxisLimits(-1, 1, -1, 1));   // a known-good baseline first
        var before = vm.Model.Settings;

        Assert.False(vm.ApplyDcivAxisLimits(xMin, xMax, yMin, yMax));
        Assert.Equal(before, vm.Model.Settings);
    }

    [Fact]
    public void ApplyDcivAxisLimits_AcceptsAValidPair_AndStoresItVerbatim()
    {
        var vm = NewVm();
        Assert.True(vm.ApplyDcivAxisLimits(-2.5, 9.5, 0.0, 1.2));
        Assert.Equal(-2.5, vm.Model.Settings.DcivXMin);
        Assert.Equal(9.5, vm.Model.Settings.DcivXMax);
        Assert.Equal(0.0, vm.Model.Settings.DcivYMin);
        Assert.Equal(1.2, vm.Model.Settings.DcivYMax);
    }

    [Fact]
    public void SetDcivAutoscale_IsOneProperty_ReadByBothTheDialogCheckboxAndTheFlyMenuItem()
    {
        var vm = NewVm();
        Assert.False(vm.Model.Settings.DcivAutoscale);
        vm.SetDcivAutoscale(true);
        Assert.True(vm.Model.Settings.DcivAutoscale);
        vm.SetDcivAutoscale(false);
        Assert.False(vm.Model.Settings.DcivAutoscale);
    }

    [Fact]
    public void ApplyPowerSweepAxisLimits_RejectsBadInput_AcceptsGood()
    {
        var vm = NewVm();
        Assert.False(vm.ApplyPowerSweepAxisLimits(5, 5, 0, 1, 0, 1));
        Assert.True(vm.ApplyPowerSweepAxisLimits(-20, 20, 0, 15, 5, 40));
        Assert.Equal(-20, vm.Model.Settings.PowerSweepXMin);
        Assert.Equal(20, vm.Model.Settings.PowerSweepXMax);
        Assert.Equal(0, vm.Model.Settings.PowerSweepYMin);
        Assert.Equal(15, vm.Model.Settings.PowerSweepYMax);
        Assert.Equal(5, vm.Model.Settings.PowerSweepY2Min);
        Assert.Equal(40, vm.Model.Settings.PowerSweepY2Max);
    }

    [Fact]
    public void ApplyTimeDomainAxisLimits_IsIndependentOfPowerSweepAxisLimits()
    {
        var vm = NewVm();
        Assert.True(vm.ApplyPowerSweepAxisLimits(-20, 20, 0, 15, 5, 40));
        Assert.True(vm.ApplyTimeDomainAxisLimits(0, 0.5, -1, 49, -0.1, 0.9));

        // Switching modes must not corrupt the other mode's axes (§4's own rule) — both sets must
        // survive side by side.
        Assert.Equal(-20, vm.Model.Settings.PowerSweepXMin);
        Assert.Equal(20, vm.Model.Settings.PowerSweepXMax);
        Assert.Equal(0, vm.Model.Settings.TimeDomainXMin);
        Assert.Equal(0.5, vm.Model.Settings.TimeDomainXMax);

        vm.SetTimeDomainAutoscale(true);
        Assert.True(vm.Model.Settings.TimeDomainAutoscale);
        Assert.False(vm.Model.Settings.PowerSweepAutoscale);   // untouched by the OTHER mode's toggle
    }

    // ══ §4 — the fly-menu entries, pinned from source (Window cannot be instantiated headlessly,
    //         same constraint every other dialog/menu test in this folder already works around) ═

    [Fact]
    public void LoadlinePanelMenu_OffersCopy_ThenAutoscale_ThenDcivSweeps()
    {
        string src = ViewSource();
        int m = src.IndexOf("else if (panelId == HarmonicaPanelId.Loadline)", StringComparison.Ordinal);
        Assert.True(m >= 0);
        int mEnd = src.IndexOf("\n        else if (!string.IsNullOrEmpty(panelId)", m, StringComparison.Ordinal);
        Assert.True(mEnd >= 0);
        string body = src[m..mEnd];

        int copy = body.IndexOf("BuildCopyMenuItem(panelId)", StringComparison.Ordinal);
        int autoscale = body.IndexOf("Header = \"Autoscale\"", StringComparison.Ordinal);
        int dciv = body.IndexOf("Header = \"DCIV Sweeps…\"", StringComparison.Ordinal);
        Assert.True(copy >= 0 && autoscale >= 0 && dciv >= 0);
        Assert.True(copy < autoscale && autoscale < dciv,
            "expected order: Copy, Autoscale, DCIV Sweeps…");

        Assert.Contains("h.SetDcivAutoscale(!h.Model.Settings.DcivAutoscale)", body, StringComparison.Ordinal);
        Assert.Contains("IsChecked = h.Model.Settings.DcivAutoscale", body, StringComparison.Ordinal);
    }

    [Fact]
    public void PowerSweepTitleMenu_OffersAutoscaleAndAxisLimits_AfterASeparator_GatedOnTheActiveMode()
    {
        string src = ViewSource();
        int m = src.IndexOf("private void BuildPowerSweepTitleMenu(", StringComparison.Ordinal);
        Assert.True(m >= 0);
        int mEnd = src.IndexOf("\n    // ── §4 (R2A)", m, StringComparison.Ordinal);
        Assert.True(mEnd >= 0);
        string body = src[m..mEnd];

        int separator = body.IndexOf("new Separator()", StringComparison.Ordinal);
        int autoscale = body.IndexOf("Header = \"Autoscale\"", StringComparison.Ordinal);
        int axisLimits = body.IndexOf("Header = \"Axis Limits…\"", StringComparison.Ordinal);
        Assert.True(separator >= 0 && autoscale >= 0 && axisLimits >= 0);
        Assert.True(separator < autoscale && autoscale < axisLimits);

        // Gated on the mode actually on screen — never hardcoded to power-sweep's own settings.
        Assert.Contains("timeDomainMode = h.ShowPowerSweepTimeDomain", body, StringComparison.Ordinal);
        Assert.Contains("h.Model.Settings.TimeDomainAutoscale", body, StringComparison.Ordinal);
        Assert.Contains("h.Model.Settings.PowerSweepAutoscale", body, StringComparison.Ordinal);
        Assert.Contains("h.SetTimeDomainAutoscale(", body, StringComparison.Ordinal);
        Assert.Contains("h.SetPowerSweepAutoscale(", body, StringComparison.Ordinal);
        Assert.Contains("ShowPowerSweepAxesDialogAsync(timeDomainMode)", body, StringComparison.Ordinal);
    }

    // ══ §3.1/§3.2 — the two dialogs' own reject-and-keep source ══════════════════════════════

    [Fact]
    public void DcivDialog_AxisLimits_CommitsThroughApplyDcivAxisLimits_RejectAndKeepOnFailure()
    {
        string src = ReadSource("src", "Ui", "Views", "Dialogs", "HarmonicaDcivSweepsDialog.axaml.cs");
        Assert.Contains("_vm.ApplyDcivAxisLimits(xMin, xMax, yMin, yMax)", src, StringComparison.Ordinal);
        Assert.Contains("_vm.SetDcivAutoscale(on)", src, StringComparison.Ordinal);
        // Reject-and-keep: the error path returns without ever calling ApplyDcivAxisLimits again or
        // silently substituting a value.
        Assert.Contains("ShowError(\"min must be less than max on both axes.\");\n            return;",
            src, StringComparison.Ordinal);
    }

    [Fact]
    public void PowerSweepAxesDialog_CommitsThroughTheModeAppropriateApply()
    {
        string src = ReadSource("src", "Ui", "Views", "Dialogs", "HarmonicaPowerSweepAxesDialog.axaml.cs");
        Assert.Contains("_vm.ApplyTimeDomainAxisLimits(xMin, xMax, yMin, yMax, y2Min, y2Max)",
            src, StringComparison.Ordinal);
        Assert.Contains("_vm.ApplyPowerSweepAxisLimits(xMin, xMax, yMin, yMax, y2Min, y2Max)",
            src, StringComparison.Ordinal);
        Assert.Contains("_vm.SetTimeDomainAutoscale(on)", src, StringComparison.Ordinal);
        Assert.Contains("_vm.SetPowerSweepAutoscale(on)", src, StringComparison.Ordinal);
        Assert.Contains("ShowError(\"min must be less than max on every axis.\");\n            return;",
            src, StringComparison.Ordinal);
    }
}

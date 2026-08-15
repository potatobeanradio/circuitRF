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
using System.Linq;
using System.Numerics;
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
    public void LoadlinePanelMenu_OffersAutoscaleLockedThenDcivSweeps_ThenCopyLast()
    {
        // R7A §2.3 — Autoscale/Locked moved into the shared AddAutoscaleLockedItems helper (dynamic
        // icon, no ToggleType — see that helper's own remark on the Fluent MenuItem Icon/checkmark
        // trap), so this now checks for ONE call rather than two inline MenuItems.
        string src = ViewSource();
        int m = src.IndexOf("else if (panelId == HarmonicaPanelId.Loadline)", StringComparison.Ordinal);
        Assert.True(m >= 0);
        int mEnd = src.IndexOf("\n        else if (!string.IsNullOrEmpty(panelId)", m, StringComparison.Ordinal);
        Assert.True(mEnd >= 0);
        string body = src[m..mEnd];

        int autoscaleLocked = body.IndexOf("AddAutoscaleLockedItems(items, dcivAutoscaleOn,", StringComparison.Ordinal);
        int dciv = body.IndexOf("Item(\"DCIV Sweeps…\"", StringComparison.Ordinal);
        int copy = body.IndexOf("BuildCopyMenuItem(panelId)", StringComparison.Ordinal);
        Assert.True(autoscaleLocked >= 0 && dciv >= 0 && copy >= 0);
        // R-hui-1 — Copy is always LAST, with a separator above it.
        Assert.True(autoscaleLocked < dciv && dciv < copy,
            "expected order: Autoscale/Locked, DCIV Sweeps…, Copy");

        int separator = body.LastIndexOf("new Separator()", copy, StringComparison.Ordinal);
        Assert.True(separator > dciv && separator < copy, "Copy must be preceded by a separator");

        Assert.Contains("h.SetDcivAutoscale(true); Refresh();", body, StringComparison.Ordinal);
        Assert.Contains("h.LockDcivAxes(); Refresh();", body, StringComparison.Ordinal);
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
        int autoscaleLocked = body.IndexOf("AddAutoscaleLockedItems(items, autoscaleOn,", StringComparison.Ordinal);
        int axisLimits = body.IndexOf("Item(\"Axis Limits…\"", StringComparison.Ordinal);
        Assert.True(separator >= 0 && autoscaleLocked >= 0 && axisLimits >= 0);
        Assert.True(separator < autoscaleLocked && autoscaleLocked < axisLimits);

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

    // ══ R-hui-6, owner-reported — "Locked" must freeze exactly what is on screen, never a stale ═══
    // ══ value; a bare Autoscale-off is not the same thing (see LockDcivAxes's own remark)      ═══

    [Fact]
    public void LockDcivAxes_TurnsAutoscaleOff_AndStoresTheCurrentFramesOwnNaturalFit()
    {
        var vm = NewVm();
        vm.SetDcivAutoscale(true);
        vm.SolveFrame(new HarmonicaSolver.Options { Rings = 2, Spokes = 6, MaxGamma = 0.6 });
        Assert.True(vm.Model.Settings.DcivAutoscale);

        // The independently-computed expected fit — the SAME function CaptureAxisWindows' own probe
        // uses, applied to THIS frame — so the test does not just assert "whatever LockDcivAxes wrote".
        var expected = CircuitRF.Ui.Harmonica.Renderers.HarmonicaPanelRenderer.BuildLoadlinePlot(
            vm.Frame.Loadline, vm.RenderTheme,
            CircuitRF.Ui.Harmonica.Renderers.HarmonicaPanelRenderer.DcivLimits(vm.Model.Settings)
                with { Autoscale = true }).Axes.Window;

        vm.LockDcivAxes();

        Assert.False(vm.Model.Settings.DcivAutoscale);
        Assert.Equal(expected.X, vm.Model.Settings.DcivXMin);
        Assert.Equal(expected.X + expected.Width, vm.Model.Settings.DcivXMax);
        Assert.Equal(expected.Y, vm.Model.Settings.DcivYMin);
        Assert.Equal(expected.Y + expected.Height, vm.Model.Settings.DcivYMax);

        // And it genuinely holds across a further solve, exactly like plain Autoscale-off already did.
        vm.SetMarkerImpedance(vm.Markers.First(m => m.Band == 1 && m.Side == TerminationSideKind.Load),
                              new Complex(150, 40));
        vm.SolveFrame(new HarmonicaSolver.Options { Rings = 2, Spokes = 6, MaxGamma = 0.6 });
        Assert.Equal(expected.X, vm.Model.Settings.DcivXMin);
    }

    [Fact]
    public void LockPowerSweepAxes_TurnsAutoscaleOff_AndStoresTheCurrentFramesOwnNaturalFit()
    {
        var vm = NewVm();
        vm.SetPowerSweepAutoscale(true);
        vm.SolveFrame(new HarmonicaSolver.Options { Rings = 2, Spokes = 6, MaxGamma = 0.6 });

        var expectedAxes = CircuitRF.Ui.Harmonica.Renderers.HarmonicaPanelRenderer.BuildPowerSweepPlot(
            vm.Frame.PowerSweep, vm.RenderTheme,
            CircuitRF.Ui.Harmonica.Renderers.HarmonicaPanelRenderer.PowerSweepLimits(vm.Model.Settings)
                with { Autoscale = true }).Axes;

        vm.LockPowerSweepAxes();

        Assert.False(vm.Model.Settings.PowerSweepAutoscale);
        Assert.Equal(expectedAxes.Window.X, vm.Model.Settings.PowerSweepXMin);
        Assert.Equal(expectedAxes.Window.X + expectedAxes.Window.Width, vm.Model.Settings.PowerSweepXMax);
        Assert.Equal(expectedAxes.WindowSecondary.Y, vm.Model.Settings.PowerSweepY2Min);
        Assert.Equal(expectedAxes.WindowSecondary.Y + expectedAxes.WindowSecondary.Height,
                     vm.Model.Settings.PowerSweepY2Max);
    }

    [Fact]
    public void LockTimeDomainAxes_TurnsAutoscaleOff_AndStoresTheCurrentFramesOwnNaturalFit()
    {
        var vm = NewVm();
        vm.SetTimeDomainAutoscale(true);
        vm.SolveFrame(new HarmonicaSolver.Options { Rings = 2, Spokes = 6, MaxGamma = 0.6 });

        var expectedAxes = CircuitRF.Ui.Harmonica.Renderers.HarmonicaPanelRenderer.BuildTimeDomainPlot(
            vm.Frame.Loadline, vm.RenderTheme,
            CircuitRF.Ui.Harmonica.Renderers.HarmonicaPanelRenderer.TimeDomainLimits(vm.Model.Settings)
                with { Autoscale = true }).Axes;

        vm.LockTimeDomainAxes();

        Assert.False(vm.Model.Settings.TimeDomainAutoscale);
        Assert.Equal(expectedAxes.Window.X, vm.Model.Settings.TimeDomainXMin);
        Assert.Equal(expectedAxes.Window.X + expectedAxes.Window.Width, vm.Model.Settings.TimeDomainXMax);
    }

    [Fact]
    public void LockedMenuItems_CallLockAxes_NeverABareAutoscaleOff()
    {
        // Owner-reported regression guard: the Locked menu items must go through the Lock*Axes
        // methods (which capture the CURRENT frame's fit), never SetXxxAutoscale(false) alone (which
        // only turns the flag off and can freeze at a stale stored value — see LockDcivAxes's remark).
        string src = ViewSource();

        int dcivStart = src.IndexOf("else if (panelId == HarmonicaPanelId.Loadline)", StringComparison.Ordinal);
        int dcivEnd = src.IndexOf("else if (!string.IsNullOrEmpty(panelId)", dcivStart, StringComparison.Ordinal);
        string dcivBody = src[dcivStart..dcivEnd];
        Assert.Contains("onLockedClick:    () => { h.LockDcivAxes(); Refresh(); });", dcivBody, StringComparison.Ordinal);
        Assert.DoesNotContain("h.SetDcivAutoscale(false)", dcivBody, StringComparison.Ordinal);

        int psStart = src.IndexOf("private void BuildPowerSweepTitleMenu(", StringComparison.Ordinal);
        int psEnd = src.IndexOf("\n    // ── §4 (R2A)", psStart, StringComparison.Ordinal);
        string psBody = src[psStart..psEnd];
        Assert.Contains("if (timeDomainMode) h.LockTimeDomainAxes(); else h.LockPowerSweepAxes();",
            psBody, StringComparison.Ordinal);
        Assert.DoesNotContain("SetTimeDomainAutoscale(false)", psBody, StringComparison.Ordinal);
        Assert.DoesNotContain("SetPowerSweepAutoscale(false)", psBody, StringComparison.Ordinal);

        // R7A §2.3 — the shared helper itself must route BOTH callers' "Locked" click through the
        // caller-supplied onLockedClick, never a bare toggle of its own. R9A §10 — both rows now go
        // through the shared Toggle(...) helper (see Toggle's own Click wiring), rather than a
        // hand-built MenuItem with its own Click lambda.
        int helperStart = src.IndexOf("private static void AddAutoscaleLockedItems(", StringComparison.Ordinal);
        Assert.True(helperStart >= 0);
        int helperEnd = src.IndexOf("\n    // ── §4 (R2A)", helperStart, StringComparison.Ordinal);
        string helperBody = src[helperStart..helperEnd];
        Assert.Contains("Toggle(\"Locked\", !autoscaleOn, onLockedClick)", helperBody, StringComparison.Ordinal);
        Assert.Contains("Toggle(\"Autoscale\", autoscaleOn, onAutoscaleClick)", helperBody, StringComparison.Ordinal);
    }

    // ══ R8B §5.4 — the icon-slot convention is enforced repo-wide in this one file ══════════════

    [Fact]
    public void HarmonicaView_NeverUsesToggleType_EveryToggleGoesThroughTheSharedHelper()
    {
        // R8B §5 finished what R7A §2.3 started: the Fluent MenuItem template's check glyph and Icon
        // share one leading slot, so ToggleType never appears anywhere in this file any more — every
        // toggle row is built through Toggle(...) (the icon slot carries the state), matching Data
        // Display's own loadpull marker menu.
        string src = ViewSource();
        Assert.Empty(System.Text.RegularExpressions.Regex.Matches(src, "MenuItemToggleType"));
    }
}

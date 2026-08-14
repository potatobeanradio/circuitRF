// ================================================================
//  HarmonicaPowerSweepFlyMenuTests.cs — brief-harmonicarf-r6d §4/§5/§6
//
//  The power-sweep panel's title fly menu (Power Sweep | Time Domain), the mode's persistence, and
//  the Copy item every panel's own fly menu now offers. Same shape as HarmonicaSmithFlyMenuTests: no
//  headless-Avalonia harness for a live ContextMenu exists in this repo, so the DISPATCH/MENU-SHAPE
//  half is pinned from source and the MODE/PERSISTENCE half is tested directly against the real
//  view-model and CharmIo round trip.
// ================================================================

using System;
using System.IO;
using CircuitRF.Harmonica;
using CircuitRF.Ui.Harmonica;
using Xunit;

namespace CircuitRF.Ui.Tests.Harmonica;

public sealed class HarmonicaPowerSweepFlyMenuTests
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

    // ══ §4 — the title fly menu ═════════════════════════════════════════════════════════════════

    [Fact]
    public void PowerSweepTitleMenu_OffersBothItems_CheckableAndMutuallyExclusive()
    {
        string src = ViewSource();
        int m = src.IndexOf("private void BuildPowerSweepTitleMenu(", StringComparison.Ordinal);
        Assert.True(m >= 0);
        int mEnd = src.IndexOf("\n    private void OnCanvasContextMenuOpening(", StringComparison.Ordinal);
        Assert.True(mEnd < 0 || mEnd < m, "OnCanvasContextMenuOpening must come BEFORE this helper");
        mEnd = src.IndexOf("\n    // ── §4 (R2A)", m, StringComparison.Ordinal);
        Assert.True(mEnd >= 0);
        string body = src[m..mEnd];

        Assert.Contains("Header = \"Power Sweep\"", body, StringComparison.Ordinal);
        Assert.Contains("Header = \"Time Domain\"", body, StringComparison.Ordinal);
        Assert.Contains("ToggleType.CheckBox", body, StringComparison.Ordinal);
        Assert.Contains("menus.SetPowerSweepModeCommand.Execute(\"PowerSweep\")", body, StringComparison.Ordinal);
        Assert.Contains("menus.SetPowerSweepModeCommand.Execute(\"TimeDomain\")", body, StringComparison.Ordinal);
        // The checked state reads the SAME flag the mode is switched with, so the menu doubles as a
        // readout rather than drifting from what is actually drawn.
        Assert.Contains("IsChecked = !h.ShowPowerSweepTimeDomain", body, StringComparison.Ordinal);
        Assert.Contains("IsChecked = h.ShowPowerSweepTimeDomain", body, StringComparison.Ordinal);
    }

    [Fact]
    public void PowerSweepDispatch_ResolvesTitleBeforeXLabel_AndGatesXUnitOnPowerSweepModeOnly()
    {
        string src = ViewSource();
        int m = src.IndexOf("if (panelId == HarmonicaPanelId.PowerSweep)", StringComparison.Ordinal);
        Assert.True(m >= 0);
        int mEnd = src.IndexOf("else if (panelId == HarmonicaPanelId.Loadline)", m, StringComparison.Ordinal);
        Assert.True(mEnd >= 0);
        string body = src[m..mEnd];

        int title  = body.IndexOf("rects.Title.Contains", StringComparison.Ordinal);
        int xLabel = body.IndexOf("rects.XLabel.Contains", StringComparison.Ordinal);
        Assert.True(title >= 0 && xLabel >= 0 && title < xLabel,
            "the title band must resolve BEFORE the X-label band");

        // §5 — the X-unit menu is gated on power-sweep mode; it must not appear in time-domain mode.
        Assert.Contains("!h.ShowPowerSweepTimeDomain && rects.XLabel.Contains", body, StringComparison.Ordinal);

        // §6 — Copy is offered in the fallback (neither title nor X-label), in both modes.
        Assert.Contains("BuildCopyMenuItem(panelId)", body, StringComparison.Ordinal);
    }

    // ══ §6 — Copy on every panel ════════════════════════════════════════════════════════════════

    [Fact]
    public void LoadlineDispatch_OffersCopy_AboveDcivSweeps()
    {
        string src = ViewSource();
        int m = src.IndexOf("else if (panelId == HarmonicaPanelId.Loadline)", StringComparison.Ordinal);
        Assert.True(m >= 0);
        int mEnd = src.IndexOf("else if (!string.IsNullOrEmpty(panelId)", m, StringComparison.Ordinal);
        Assert.True(mEnd >= 0);
        string body = src[m..mEnd];

        int copy = body.IndexOf("BuildCopyMenuItem(panelId)", StringComparison.Ordinal);
        int dciv = body.IndexOf("Header = \"DCIV Sweeps…\"", StringComparison.Ordinal);
        Assert.True(copy >= 0 && dciv >= 0 && copy < dciv, "Copy must be added ABOVE DCIV Sweeps…");
    }

    [Fact]
    public void PickedTracePanelDispatch_OffersCopy_ButNotForTheReadoutStrip()
    {
        string src = ViewSource();
        int m = src.IndexOf("else if (!string.IsNullOrEmpty(panelId)", StringComparison.Ordinal);
        Assert.True(m >= 0);
        int mEnd = src.IndexOf("\n\n        if (items.Count == 0) { e.Cancel = true; return; }", m, StringComparison.Ordinal);
        Assert.True(mEnd >= 0);
        string body = src[m..mEnd];

        Assert.Contains("panelId != HarmonicaPanelId.ReadoutStrip", body, StringComparison.Ordinal);
        Assert.Contains("BuildCopyMenuItem(panelId)", body, StringComparison.Ordinal);
    }

    // ══ §4 — the mode itself: view-model wiring and persistence ════════════════════════════════

    [Fact]
    public void SetPowerSweepModeCommand_TogglesTheFlag_AndWritesAppearance()
    {
        var vm = new HarmonicaViewModel();
        var menus = new HarmonicaMenuViewModel(vm);
        Assert.False(vm.ShowPowerSweepTimeDomain);
        Assert.Null(vm.Appearance.ShowPowerSweepTimeDomain);

        menus.SetPowerSweepModeCommand.Execute("TimeDomain");
        Assert.True(vm.ShowPowerSweepTimeDomain);
        Assert.True(vm.Appearance.ShowPowerSweepTimeDomain);

        menus.SetPowerSweepModeCommand.Execute("PowerSweep");
        Assert.False(vm.ShowPowerSweepTimeDomain);
        Assert.False(vm.Appearance.ShowPowerSweepTimeDomain);
    }

    [Fact]
    public void PowerSweepMode_SurvivesSaveAndReopen()
    {
        var a = new HarmonicaViewModel();
        var menus = new HarmonicaMenuViewModel(a);
        menus.SetPowerSweepModeCommand.Execute("TimeDomain");

        string json = a.ToCharmJson();

        var b = new HarmonicaViewModel();
        var unresolved = b.LoadCharm(json, baseDirectory: null);
        Assert.Empty(unresolved);

        Assert.True(b.ShowPowerSweepTimeDomain);
    }

    [Fact]
    public void PowerSweepMode_AbsentFromAnUntouchedCharm_DefaultsToPowerSweep()
    {
        var a = new HarmonicaViewModel();
        string json = a.ToCharmJson();

        var b = new HarmonicaViewModel();
        b.LoadCharm(json, baseDirectory: null);

        Assert.False(b.ShowPowerSweepTimeDomain);
    }

    [Fact]
    public void CharmAppearance_IsDefault_IncludesShowPowerSweepTimeDomain()
    {
        Assert.True(CharmAppearance.Default.IsDefault);
        Assert.False((new CharmAppearance { ShowPowerSweepTimeDomain = true }).IsDefault);
    }

    [Fact]
    public void CharmIo_RoundTrips_ShowPowerSweepTimeDomain()
    {
        var vm = new HarmonicaViewModel();
        var appearance = CharmAppearance.Default with { ShowPowerSweepTimeDomain = true };
        string json = CharmIo.Write(vm.Model, vm.Terminations, appearance, CharmLayout.Default,
                                    [], [], []);

        var c = CharmIo.ReadAll(json, null);
        Assert.True(c.Appearance.ShowPowerSweepTimeDomain);
    }
}

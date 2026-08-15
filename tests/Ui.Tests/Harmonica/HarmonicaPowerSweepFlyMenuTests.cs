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
    public void PowerSweepTitleMenu_OffersBothItems_AsARadioGroupUnderOneModeSubmenu()
    {
        // R8B §5.3 — "Power Sweep" and "Time Domain" are grouped under one "Mode ▸" submenu row,
        // each a RADIO (never ToggleType — see PowerSweepTitleMenu_NeverUsesToggleType below), rather
        // than two loose top-level checkboxes that were secretly mutually exclusive.
        string src = ViewSource();
        int m = src.IndexOf("private void BuildPowerSweepTitleMenu(", StringComparison.Ordinal);
        Assert.True(m >= 0);
        int mEnd = src.IndexOf("\n    private void OnCanvasContextMenuOpening(", StringComparison.Ordinal);
        Assert.True(mEnd < 0 || mEnd < m, "OnCanvasContextMenuOpening must come BEFORE this helper");
        mEnd = src.IndexOf("\n    // ── §4 (R2A)", m, StringComparison.Ordinal);
        Assert.True(mEnd >= 0);
        string body = src[m..mEnd];

        Assert.Contains("\"Power Sweep\"", body, StringComparison.Ordinal);
        Assert.Contains("\"Time Domain\"", body, StringComparison.Ordinal);
        Assert.Contains("glyph: MenuGlyph.Radio", body, StringComparison.Ordinal);
        Assert.Contains("menus.SetPowerSweepModeCommand.Execute(\"PowerSweep\")", body, StringComparison.Ordinal);
        Assert.Contains("menus.SetPowerSweepModeCommand.Execute(\"TimeDomain\")", body, StringComparison.Ordinal);
        // Grouped under one "Mode: <current>" submenu row, which carries the state so the menu doubles
        // as a readout rather than drifting from what is actually drawn.
        Assert.Contains("Header = $\"Mode: ", body, StringComparison.Ordinal);
        Assert.Contains("!timeDomainMode", body, StringComparison.Ordinal);
        Assert.Contains("ItemsSource = new object[] { powerSweep, timeDomain }", body, StringComparison.Ordinal);
    }

    [Fact]
    public void PowerSweepTitleMenu_NeverUsesToggleType()
    {
        string src = ViewSource();
        Assert.Empty(System.Text.RegularExpressions.Regex.Matches(src, "MenuItemToggleType"));
    }

    [Fact]
    public void PowerSweepDispatch_TitleAndXLabel_ResolveToDIFFERENTMenus()
    {
        // R-hui-2, owner-reported — R-hui-1 briefly merged the title band and the X-axis label into
        // ONE menu, which wrongly put Copy/mode-toggle/Autoscale on the axis-label menu. They are
        // separate again: title -> BuildPowerSweepTitleMenu, X-label -> BuildPowerSweepXUnitMenu,
        // and the X-label branch is gated on power-sweep mode only (a plain time axis has no unit).
        string src = ViewSource();
        int m = src.IndexOf("if (panelId == HarmonicaPanelId.PowerSweep)", StringComparison.Ordinal);
        Assert.True(m >= 0);
        int mEnd = src.IndexOf("else if (panelId == HarmonicaPanelId.Loadline)", m, StringComparison.Ordinal);
        Assert.True(mEnd >= 0);
        string body = src[m..mEnd];

        int title      = body.IndexOf("rects.Title.Contains", StringComparison.Ordinal);
        int titleMenu  = body.IndexOf("BuildPowerSweepTitleMenu(items, h)", StringComparison.Ordinal);
        int xLabel     = body.IndexOf("!h.ShowPowerSweepTimeDomain && rects.XLabel.Contains", StringComparison.Ordinal);
        int xLabelMenu = body.IndexOf("BuildPowerSweepXUnitMenu(items, h)", StringComparison.Ordinal);
        Assert.True(title >= 0 && titleMenu >= 0 && xLabel >= 0 && xLabelMenu >= 0);
        Assert.True(title < titleMenu && titleMenu < xLabel && xLabel < xLabelMenu,
            "expected order: title hit-test -> BuildPowerSweepTitleMenu, then X-label hit-test -> BuildPowerSweepXUnitMenu");

        // R-hui-3, owner-reported — right-clicking anywhere ELSE in the panel body (the fallback
        // branch, neither title nor X-label) must show the SAME menu as the title, not a bare Copy.
        int fallbackMenu = body.LastIndexOf("BuildPowerSweepTitleMenu(items, h)", StringComparison.Ordinal);
        Assert.True(fallbackMenu > xLabelMenu, "the fallback branch must also call BuildPowerSweepTitleMenu");
    }

    [Fact]
    public void PowerSweepTitleMenu_OffersNoXUnitItems_AndPutsCopyLast()
    {
        string src = ViewSource();
        int m = src.IndexOf("private void BuildPowerSweepTitleMenu(", StringComparison.Ordinal);
        Assert.True(m >= 0);
        int mEnd = src.IndexOf("\n    /// <summary>", m + 1, StringComparison.Ordinal);
        Assert.True(mEnd >= 0);
        string body = src[m..mEnd];

        // Owner-reported — the title menu must NOT offer the X-unit cycle; that is
        // BuildPowerSweepXUnitMenu's own, separate menu.
        Assert.DoesNotContain("PowerSweepXUnit unit in Enum.GetValues", body, StringComparison.Ordinal);

        // R7A §2.3 — Autoscale/Locked now come from the shared AddAutoscaleLockedItems helper call.
        int autoscaleLocked = body.IndexOf("AddAutoscaleLockedItems(items, autoscaleOn,", StringComparison.Ordinal);
        int copy = body.LastIndexOf("BuildCopyMenuItem(HarmonicaPanelId.PowerSweep)", StringComparison.Ordinal);
        Assert.True(autoscaleLocked >= 0 && copy >= 0 && autoscaleLocked < copy, "Copy must be the LAST item");

        int separator = body.LastIndexOf("new Separator()", copy, StringComparison.Ordinal);
        Assert.True(separator > autoscaleLocked && separator < copy, "Copy must be preceded by a separator");
    }

    [Fact]
    public void PowerSweepXUnitMenu_OffersOnlyTheFourUnits_EachCheckmarkedAgainstTheCurrentOne()
    {
        string src = ViewSource();
        int m = src.IndexOf("private void BuildPowerSweepXUnitMenu(", StringComparison.Ordinal);
        Assert.True(m >= 0);
        // R7A §2 — the shared icon helpers now follow this method, before "§4 (R2A)"'s own comment,
        // so the boundary is the R7A §2 section comment rather than that later marker.
        int mEnd = src.IndexOf("\n    // ── R7A §2", m, StringComparison.Ordinal);
        Assert.True(mEnd >= 0);
        string body = src[m..mEnd];

        // Owner-reported — a checkmark beside the option currently used for the X axis. R8B §5 —
        // dynamic icon (Toggle/MenuGlyph.Radio), never ToggleType.
        Assert.Contains("h.PowerSweepXUnit == unit", body, StringComparison.Ordinal);
        Assert.Contains("glyph: MenuGlyph.Radio", body, StringComparison.Ordinal);
        Assert.Contains("h.SetPowerSweepXUnitCommand.Execute(unit)", body, StringComparison.Ordinal);

        // Nothing else — no Copy, no mode toggle, no Autoscale/Locked/Axis Limits….
        Assert.DoesNotContain("BuildCopyMenuItem", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Header = \"Power Sweep\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Header = \"Time Domain\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Header = \"Autoscale\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Header = \"Locked\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Header = \"Axis Limits…\"", body, StringComparison.Ordinal);
    }

    // ══ §6 — Copy on every panel ════════════════════════════════════════════════════════════════

    [Fact]
    public void LoadlineDispatch_OffersCopy_LastWithASeparatorAboveIt()
    {
        // R-hui-1 — Copy moved to the BOTTOM of every plot's fly menu, with a separator above it.
        string src = ViewSource();
        int m = src.IndexOf("else if (panelId == HarmonicaPanelId.Loadline)", StringComparison.Ordinal);
        Assert.True(m >= 0);
        int mEnd = src.IndexOf("else if (!string.IsNullOrEmpty(panelId)", m, StringComparison.Ordinal);
        Assert.True(mEnd >= 0);
        string body = src[m..mEnd];

        int copy = body.IndexOf("BuildCopyMenuItem(panelId)", StringComparison.Ordinal);
        int dciv = body.IndexOf("Item(\"DCIV Sweeps…\"", StringComparison.Ordinal);
        Assert.True(copy >= 0 && dciv >= 0 && dciv < copy, "Copy must be added BELOW DCIV Sweeps…");

        int separator = body.LastIndexOf("new Separator()", copy, StringComparison.Ordinal);
        Assert.True(separator > dciv && separator < copy, "Copy must be preceded by a separator");
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

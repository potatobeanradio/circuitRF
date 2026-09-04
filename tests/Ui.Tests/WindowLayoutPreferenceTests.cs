using System.IO;
using System.Linq;
using System.Text.Json;
using CircuitRF.Ui.Docking;
using CircuitRF.Ui.Theming;
using CircuitRF.Ui.ViewModels.Dock;
using Dock.Model.Core;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Settings ▸ On Launch ▸ <b>Window Layout</b> (owner request, 2026-08-15) — the setting formerly
/// called "Focus Pane", now with a third option that is a genuinely different arrangement, and now
/// the single source of truth for what View ▸ Reset Layout resets to.
///
/// <para><c>WorkspaceViewModel</c> cannot be constructed headlessly (its ctor touches the
/// Dispatcher), so the assertions that live only there are pinned by source scan — this codebase's
/// established fallback, see <c>DockLayoutPersistenceTests</c>.</para>
/// </summary>
public sealed class WindowLayoutPreferenceTests
{
    private static CwsDockPanel Panel(CwsDockLayout l, string id) =>
        Assert.Single(l.Panels, p => p.Id == id);

    private static string ReadSource(string relative) =>
        File.ReadAllText(Path.Combine(SourceRoot(), relative));

    private static string SourceRoot()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    // ── The preset itself ─────────────────────────────────────────────────────

    [Fact]
    public void ProjectTreeAndLibrary_PutsTheLibraryBesideTheDocuments_NotUnderThem()
    {
        var layout = DockLayoutDefaults.For(WindowLayout.ProjectTreeAndLibrary);

        Assert.Equal(DockSide.Right, Panel(layout, DockPanelIds.Palette).Side);
        Assert.Equal(DockSide.Left,  Panel(layout, DockPanelIds.ProjectTree).Side);
        Assert.Equal(DockSide.Left,  Panel(layout, DockPanelIds.Properties).Side);
        // …and the Project Tree is NOT tabbed with the Library any more, which is the whole point.
        Assert.NotEqual(Panel(layout, DockPanelIds.ProjectTree).Group,
                        Panel(layout, DockPanelIds.Properties).Group);
        Assert.True(Panel(layout, DockPanelIds.Palette).Open);
        Assert.True(Panel(layout, DockPanelIds.Palette).Active);

        // Messages/DRC stay where they have always been.
        Assert.Equal(DockSide.Bottom, Panel(layout, DockPanelIds.Messages).Side);
        Assert.Equal(DockSide.Bottom, Panel(layout, DockPanelIds.Drc).Side);
    }

    [Fact]
    public void ProjectTreeAndLibrary_MatchesTheOwnersOwnLayout_ARightColumnHoldingOnlyTheLibrary()
    {
        var factory = new CircuitRfDockFactory();
        var root    = factory.CreateLayoutFromState(DockLayoutDefaults.For(WindowLayout.ProjectTreeAndLibrary));

        // Through the REAL builder: the Library must end up in the right column, i.e. after the
        // document column in the outer horizontal row — not in the document column's bottom slot.
        var captured = DockLayoutCapture.Capture(root, []);
        Assert.Equal(DockSide.Right, Panel(captured, DockPanelIds.Palette).Side);
        Assert.Equal(DockLayoutDefaults.LibraryColumnProportion,
                     Assert.Single(captured.Sides, s => s.Side == DockSide.Right).Proportion, 6);
    }

    [Theory]
    [InlineData(WindowLayout.ProjectTreeFocus)]
    [InlineData(WindowLayout.LibraryFocus)]
    public void BothFocusPresets_AreTheOldTabbedArrangement(WindowLayout preset)
    {
        var layout = DockLayoutDefaults.For(preset);

        // Focus is which TAB is on top — not a different arrangement, so both map to the layout
        // circuitRF has always opened with (Project Tree and Library tabbed together on the left).
        Assert.Equal(DockSide.Left, Panel(layout, DockPanelIds.Palette).Side);
        Assert.Equal(Panel(layout, DockPanelIds.ProjectTree).Group,
                     Panel(layout, DockPanelIds.Palette).Group);
    }

    // ── The preference ────────────────────────────────────────────────────────

    [Fact]
    public void AFreshInstall_WithNoPreferencesFile_GetsProjectTreeAndLibrary()
    {
        // "The default if circuitRF can't find its settings file" — Load() returns a blank
        // AppPreferences in exactly that case, so the fallback every caller applies is the contract.
        var blank = new AppPreferences();
        Assert.Null(blank.WindowLayout);

        foreach (var source in new[] { "src/Ui/App.axaml.cs", "src/Ui/ViewModels/WorkspaceViewModel.cs" })
            Assert.DoesNotContain("WindowLayout ?? WindowLayout.ProjectTreeFocus", ReadSource(source));

        Assert.Contains("prefs.WindowLayout ?? WindowLayout.ProjectTreeAndLibrary",
                        ReadSource("src/Ui/App.axaml.cs"));
        Assert.Contains("WindowLayout ?? WindowLayout.ProjectTreeAndLibrary",
                        ReadSource("src/Ui/ViewModels/WorkspaceViewModel.cs"));
    }

    [Fact]
    public void ThePreRenameSetting_SurvivesTheRename()
    {
        // A preferences.json written before 2026-08-15 says "launch_pane". Its ordinals were kept by
        // the rename, so 1 (the old "Palette") must still mean the Library.
        var prefs = JsonSerializer.Deserialize<AppPreferences>("""{"launch_pane": 1}""")!;
        Assert.Null(prefs.WindowLayout);

        AppPreferencesIo.Migrate(prefs);

        Assert.Equal(WindowLayout.LibraryFocus, prefs.WindowLayout);
        // …and the retired key is not written back out, so it disappears on the next save.
        Assert.DoesNotContain("launch_pane", JsonSerializer.Serialize(prefs));
    }

    [Fact]
    public void AnExplicitNewSetting_IsNotOverwrittenByTheRetiredKey()
    {
        var prefs = JsonSerializer.Deserialize<AppPreferences>(
            """{"launch_pane": 1, "window_layout": 2}""")!;

        AppPreferencesIo.Migrate(prefs);

        Assert.Equal(WindowLayout.ProjectTreeAndLibrary, prefs.WindowLayout);
    }

    [Fact]
    public void Migrate_IsIdempotent()
    {
        var prefs = new AppPreferences { WindowLayout = WindowLayout.ProjectTreeFocus };

        AppPreferencesIo.Migrate(AppPreferencesIo.Migrate(prefs));

        Assert.Equal(WindowLayout.ProjectTreeFocus, prefs.WindowLayout);
    }

    // ── Reset Layout reads the preference, and offers nothing of its own ───────

    [Fact]
    public void ResetLayout_ResetsToTheConfiguredWindowLayout()
    {
        var vm = ReadSource("src/Ui/ViewModels/WorkspaceViewModel.cs");

        var reset = vm[vm.IndexOf("private void PerformLayoutReset")..];
        reset = reset[..reset.IndexOf("\n    }")];

        Assert.Contains("AppPreferencesIo.Load().WindowLayout", reset);
        Assert.Contains("DockLayoutDefaults.For(preset)", reset);
        // The bug this guards: resetting to the hard-coded default regardless of the setting.
        Assert.DoesNotContain("CreateLayoutPreservingContent()", reset);
    }

    [Fact]
    public void TheResetLayoutMenu_GainedNoOptionsOfItsOwn()
    {
        // "Don't add any new options to the Reset Layout menu" — it stays one command, and the
        // Settings combo stays the only place a layout is chosen.
        var xaml = ReadSource("src/Ui/Views/WorkspaceWindow.axaml");

        Assert.Equal(2, xaml.Split("ResetLayoutCommand").Length - 1);   // native menu + toolbar
        Assert.DoesNotContain("ResetLayoutTo", xaml);
    }

    [Fact]
    public void TheSettingIsCalledWindowLayout_InTheUiAndInTheCode()
    {
        var xaml = ReadSource("src/Ui/Views/Dialogs/SettingsView.axaml");
        Assert.Contains("Window Layout:", xaml);
        Assert.DoesNotContain("Focus pane", xaml);

        var code = ReadSource("src/Ui/Views/Dialogs/SettingsView.axaml.cs");
        Assert.Contains("\"Project Tree Focus\", \"Library Focus\", \"Project Tree & Library\"", code);
    }

    // ── New Workspace / Switch Workspace / Close Workspace also read the preference ───

    /// <summary>
    /// The bug this guards (owner report, 2026-08-16): these three clean-slate rebuilds all called
    /// the parameterless <c>_factory.CreateDefaultLayout()</c>, which is hardcoded to
    /// <see cref="DockLayoutDefaults.Default"/> — so a workspace created (or switched into, or closed
    /// back out of) mid-session silently reverted to the old tabbed arrangement regardless of the
    /// configured Window Layout, even though <see cref="ApplyWindowLayout"/> had set it correctly at
    /// launch a moment before.
    /// </summary>
    [Theory]
    [InlineData("private async Task NewWorkspace")]
    [InlineData("private async Task SwitchToWorkspace")]
    [InlineData("private void ResetToBlankShell")]
    public void CleanSlateRebuilds_HonorTheConfiguredWindowLayout(string methodSignature)
    {
        var vm = ReadSource("src/Ui/ViewModels/WorkspaceViewModel.cs");

        var body = vm[vm.IndexOf(methodSignature)..];
        body = body[..body.IndexOf("\n    }")];

        Assert.Contains("AppPreferencesIo.Load().WindowLayout", body);
        Assert.Contains("DockLayoutDefaults.For(", body);
        // The bug: rebuilding via the parameterless overload, which ignores the preference entirely.
        Assert.DoesNotContain("_factory.CreateDefaultLayout();", body);
    }

    [Fact]
    public void TheComboBoxOrder_MatchesTheEnumOrdinals_BecauseTheOrdinalsAreSerialized()
    {
        var code  = ReadSource("src/Ui/Views/Dialogs/SettingsView.axaml.cs");
        var items = code[code.IndexOf("WindowLayoutCombo.ItemsSource")..];
        items = items[..items.IndexOf(';')];

        // SelectedIndex is cast straight to the enum, so a reordered list silently rewrites what
        // every saved preferences.json means.
        Assert.True(items.IndexOf("Project Tree Focus") < items.IndexOf("Library Focus"));
        Assert.True(items.IndexOf("Library Focus") < items.IndexOf("Project Tree & Library"));
        Assert.Equal(0, (int)WindowLayout.ProjectTreeFocus);
        Assert.Equal(1, (int)WindowLayout.LibraryFocus);
        Assert.Equal(2, (int)WindowLayout.ProjectTreeAndLibrary);
    }
}

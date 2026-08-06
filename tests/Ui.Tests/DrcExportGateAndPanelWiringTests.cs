using System.IO;
using System.Linq;
using CircuitRF.Ui.Docking;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Drc;
using CircuitRF.Ui.Theming;
using CircuitRF.Ui.ViewModels.Dock;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// L5b wiring. The pieces that live in a <c>Window</c> or a <c>UserControl</c> (the Design-menu item,
/// the toolbar button, the pre-export prompt itself) cannot be constructed headlessly — this project's
/// standing constraint — so those are pinned by SOURCE SCAN, the same fallback this codebase already
/// uses for menu structure and AXAML wiring. Everything reachable from a view model is driven directly.
/// </summary>
public class DrcExportGateAndPanelWiringTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Read(string relative) => File.ReadAllText(Path.Combine(RepoRoot(), relative));

    // ── The panel ────────────────────────────────────────────────────────────

    [Fact]
    public void DrcTool_FollowsTheActiveLayout_AndSaysSoWhenThereIsNone()
    {
        var tool = new DrcTool();

        Assert.False(tool.IsLayoutActive);
        Assert.Null(tool.EditorVm);
        Assert.Equal(DockPanelIds.Drc, tool.Id);

        var vm = new LayoutEditorViewModel(new LayoutView());
        tool.SetActiveLayout(vm);
        Assert.True(tool.IsLayoutActive);
        Assert.Same(vm, tool.EditorVm);

        tool.SetActiveLayout(null);
        Assert.False(tool.IsLayoutActive);
    }

    [Fact]
    public void TheDrcPanel_IsARegisteredDockPanel_SoASavedLayoutCanCarryIt()
    {
        Assert.Contains(DockPanelIds.Drc, DockPanelIds.All);

        // A .cws written before this build never heard of it; the default placement fills in.
        var filled = DockLayoutDefaults.WithMissingPanelsFilled(new CwsDockLayout());
        Assert.Contains(filled.Panels, p => p.Id == DockPanelIds.Drc);
    }

    // ── Entry points ─────────────────────────────────────────────────────────

    /// <summary>
    /// Four ways in, on purpose: a menu item for discoverability, a keyboard shortcut and a toolbar
    /// button for the repeat-while-fixing loop, and the panel's own Check button for when the panel is
    /// already what you are looking at.
    /// </summary>
    [Fact]
    public void EveryDrcEntryPoint_IsWired()
    {
        string window = Read("src/Ui/Views/WorkspaceWindow.axaml");

        // Design menu, both hand-mirrored surfaces (macOS NativeMenu + the in-window Menu).
        Assert.Contains("Header=\"Check Design Rules\"", window);
        Assert.Contains("Header=\"_Check Design Rules\"", window);

        // A displayed InputGesture only DRAWS the shortcut; a window KeyBinding is what fires it.
        Assert.Contains("Gesture=\"Ctrl+Shift+K\"  Command=\"{Binding CheckDesignRulesCommand}\"", window);
        Assert.Contains("Gesture=\"Meta+Shift+K\"  Command=\"{Binding CheckDesignRulesCommand}\"", window);

        // View ▸ Panels, both surfaces.
        Assert.Contains("CommandParameter=\"Drc\"", window);

        // Layout toolbar.
        Assert.Contains("Click=\"OnCheckDesignRules\"", Read("src/Ui/Views/Layout/LayoutEditorView.axaml"));

        // The panel's own button.
        Assert.Contains("Click=\"OnRunClick\"", Read("src/Ui/Views/Drc/DrcToolView.axaml"));
    }

    /// <summary>
    /// Ctrl/⌘+Shift+K must be this command's alone. A duplicate binding does not error — it silently
    /// fires whichever the framework reaches first, which is the kind of thing nobody notices until
    /// the wrong command runs.
    /// </summary>
    [Fact]
    public void TheDrcShortcut_IsNotSharedWithAnyOtherCommand()
    {
        string window = Read("src/Ui/Views/WorkspaceWindow.axaml");

        // Every mention of the gesture — the KeyBinding that fires it, the in-window menu item's
        // displayed InputGesture, and the macOS NativeMenuItem's own Gesture — must belong to this
        // one command. Counting occurrences would only pin today's arrangement; what matters is that
        // no OTHER command ever claims the same keystroke.
        foreach (string chord in new[] { "Ctrl+Shift+K", "Meta+Shift+K" })
        {
            int at = 0, seen = 0;
            while ((at = window.IndexOf($"Gesture=\"{chord}\"", at, System.StringComparison.Ordinal)) >= 0)
            {
                // The command sits on the same element — within a line or two either way in this file.
                int from = System.Math.Max(0, at - 300);
                int len  = System.Math.Min(window.Length - from, 600);
                Assert.Contains("CheckDesignRulesCommand", window.Substring(from, len));
                seen++;
                at++;
            }
            Assert.True(seen >= 2, $"{chord} should be both bound and displayed; found {seen} mention(s)");
        }
    }

    // ── R16d: export offers to run DRC first ─────────────────────────────────

    [Fact]
    public void AllThreeExportPaths_RunTheSameCheck_RatherThanThreeOfTheirOwn()
    {
        string view = Read("src/Ui/Views/Layout/LayoutEditorView.axaml.cs");

        Assert.Contains("ConfirmDesignRulesBeforeExportAsync(vm, owner, \"GDSII\")", view);
        Assert.Contains("ConfirmDesignRulesBeforeExportAsync(vm, owner, \"DXF\")", view);
        Assert.Contains("ConfirmDesignRulesBeforeExportAsync(vm, owner, \"Gerber\")", view);

        // One implementation — three callers, never three copies that can drift on what "checked
        // before writing" means.
        Assert.Equal(1, view.Split("private async Task<bool> ConfirmDesignRulesBeforeExportAsync").Length - 1);
    }

    /// <summary>
    /// R16d's "not mandatory, not silent" — default ON, and reachable from two places: the prompt
    /// itself (where the setting is in front of you at the moment it costs you something) and
    /// Settings ▸ General (where a user whose designs are clean can still find it).
    /// </summary>
    [Fact]
    public void TheExportCheck_DefaultsOn_AndIsReachableFromSettings()
    {
        Assert.True(new AppPreferences().CheckDrcOnExport ?? true);

        Assert.Contains("CheckDrcOnExportCheck", Read("src/Ui/Views/Dialogs/SettingsView.axaml"));
        Assert.Contains("KeepCheckingCheck", Read("src/Ui/Views/Dialogs/DrcExportGateDialog.axaml"));
    }

    /// <summary>
    /// A "no violations" modal before every export is exactly the dialog people learn to dismiss
    /// unread — which then also gets dismissed on the export that mattered. The gate returns without
    /// prompting on a clean design, the same reasoning the GDSII/Gerber fidelity dialogs already apply.
    /// </summary>
    [Fact]
    public void ACleanDesign_IsNotPromptedBeforeExport()
    {
        string view = Read("src/Ui/Views/Layout/LayoutEditorView.axaml.cs");
        Assert.Contains("if (result.IsClean) return true;", view);
    }

    // ── Preference round trip ────────────────────────────────────────────────

    [Fact]
    public void TheExportCheckPreference_IsPerUser_AndOmittedFromTheFileWhenUnset()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new AppPreferences());
        Assert.DoesNotContain("check_drc_on_export", json);

        var set = System.Text.Json.JsonSerializer.Serialize(new AppPreferences { CheckDrcOnExport = false });
        Assert.Contains("check_drc_on_export", set);
    }
}

using System;
using System.IO;
using CircuitRF.Ui.Docking;
using CircuitRF.Ui.ViewModels.Dock;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// <b>The workspace toolbar's three panel toggles</b> (owner, 2026-08-18): Library, Properties
/// inspector and Messages, each pressing to show the panel where the user last had it in THIS
/// workspace, pressing again to close it, and round again.
///
/// <para>Not new machinery — the wBond round-5/6 work already built the two-state toggle, the
/// "put it back where it was" restore and the checked-state-from-the-dock-tree rule, and paid for each
/// of them with a reported glitch. These buttons are that same command with a different panel id, so
/// what these tests guard is that nothing here quietly grew a SECOND way to do it: the toolbar's own
/// weaker Messages command is gone, the checked state is read one way from the tree, and the buttons
/// carry no local Background that would make the checked state invisible.</para>
/// </summary>
public class WorkspaceToolbarPanelTogglesTests
{
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "circuitrf.slnx")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return dir!;
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine([RepoRoot(), .. parts]));

    private static string Toolbar()
    {
        var xaml = Read("src", "Ui", "Views", "WorkspaceWindow.axaml");
        int at = xaml.IndexOf("<!-- ── Workspace toolbar", StringComparison.Ordinal);
        Assert.True(at >= 0, "the workspace toolbar comment banner moved");
        return xaml[at..];
    }

    /// <summary>The three toggles are in the toolbar, in the owner's order: the two new ones to the LEFT
    /// of Messages.</summary>
    [Fact]
    public void TheToolbar_CarriesLibraryThenPropertiesThenMessages()
    {
        var toolbar = Toolbar();

        int library    = toolbar.IndexOf("CommandParameter=\"Palette\"", StringComparison.Ordinal);
        int properties = toolbar.IndexOf("CommandParameter=\"Properties\"", StringComparison.Ordinal);
        int messages   = toolbar.IndexOf("CommandParameter=\"Messages\"", StringComparison.Ordinal);

        Assert.True(library >= 0 && properties >= 0 && messages >= 0);
        Assert.True(library < properties, "Library comes first");
        Assert.True(properties < messages, "both new buttons sit to the LEFT of Messages");
    }

    /// <summary>
    /// All three run <c>ToggleToolPanelCommand</c> — the two-state toggle the wBond panels' P and A keys
    /// use — and not <c>ShowToolPanelCommand</c>, which cannot close what it opened.
    /// </summary>
    [Fact]
    public void ThePanelToggles_UseTheTwoStateToggleCommand()
    {
        var toolbar = Toolbar();

        foreach (var panel in new[] { "Palette", "Properties", "Messages" })
        {
            int at = toolbar.IndexOf($"CommandParameter=\"{panel}\"", StringComparison.Ordinal);
            int open = toolbar.LastIndexOf('<', at);
            var element = toolbar[open..toolbar.IndexOf('>', at)];

            Assert.StartsWith("<ToggleButton", element);
            Assert.Contains("ToggleToolPanelCommand", element, StringComparison.Ordinal);
            Assert.DoesNotContain("ShowToolPanelCommand", element, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// View ▸ Panels keeps the plain SHOW command. A menu item named after a panel that closed the panel
    /// would be a trap — the toggle is for the surface that reads as a state.
    /// </summary>
    [Fact]
    public void TheViewPanelsMenu_StillOnlyShows()
    {
        var xaml = Read("src", "Ui", "Views", "WorkspaceWindow.axaml");
        int menu = xaml.IndexOf("<MenuItem Header=\"_Panels\">", StringComparison.Ordinal);
        int end  = xaml.IndexOf("<!-- ── Workspace toolbar", StringComparison.Ordinal);

        Assert.True(menu >= 0 && menu < end);
        Assert.DoesNotContain("ToggleToolPanelCommand", xaml[menu..end], StringComparison.Ordinal);
    }

    /// <summary>
    /// The checked state is bound ONE WAY from the view model, which computes it from the dock tree.
    /// A two-way binding would make the button its own source of truth, and the truth is the tree — a
    /// panel is also closed by its own tab X, dragged into a float, or replaced by a layout restore.
    /// </summary>
    [Fact]
    public void ThePanelToggles_ReadTheirCheckedStateOneWayFromTheDockTree()
    {
        var toolbar = Toolbar();

        foreach (var property in new[] { "IsLibraryPanelShowing", "IsPropertiesPanelShowing", "IsMessagesPanelShowing" })
            Assert.Contains($"IsChecked=\"{{Binding {property}, Mode=OneWay}}\"", toolbar, StringComparison.Ordinal);

        // Computed on every read, never stored — and renotified from the one event every route that can
        // change what is on screen already raises.
        var vm = Read("src", "Ui", "ViewModels", "WorkspaceViewModel.Docking.cs");
        Assert.Contains("public bool IsLibraryPanelShowing => IsToolPanelShowing(DockPanelIds.Palette);", vm, StringComparison.Ordinal);
        Assert.Contains("public bool IsPropertiesPanelShowing => IsToolPanelShowing(DockPanelIds.Properties);", vm, StringComparison.Ordinal);
        Assert.Contains("public bool IsMessagesPanelShowing => IsToolPanelShowing(DockPanelIds.Messages);", vm, StringComparison.Ordinal);

        int raise = vm.IndexOf("private void RaiseToolPanelVisibilityChanged()", StringComparison.Ordinal);
        int body  = vm.IndexOf("ToolPanelVisibilityChanged?.Invoke();", raise);
        Assert.True(raise >= 0 && body > raise);

        foreach (var property in new[] { "IsLibraryPanelShowing", "IsPropertiesPanelShowing", "IsMessagesPanelShowing" })
            Assert.Contains($"OnPropertyChanged(nameof({property}))", vm[raise..body], StringComparison.Ordinal);
    }

    /// <summary>
    /// No local <c>Background</c> on the toggles. A local value outranks a style setter, so it would beat
    /// the theme's own <c>:checked</c> fill as well — and a toggle that never looks checked is the exact
    /// complaint (owner, 2026-08-17, on the wBond pair) these buttons exist to avoid repeating.
    /// </summary>
    [Fact]
    public void ThePanelToggles_LeaveTheirChromeToTheStyle()
    {
        var toolbar = Toolbar();

        foreach (var panel in new[] { "Palette", "Properties", "Messages" })
        {
            int at = toolbar.IndexOf($"CommandParameter=\"{panel}\"", StringComparison.Ordinal);
            var element = toolbar[toolbar.LastIndexOf('<', at)..toolbar.IndexOf('>', at)];

            Assert.Contains("Classes=\"PanelToggle\"", element, StringComparison.Ordinal);
            Assert.DoesNotContain("Background=", element, StringComparison.Ordinal);
        }

        var styles = Read("src", "Ui", "Styles", "CircuitRfStyles.axaml");
        int flat    = styles.IndexOf("Selector=\"ToggleButton.PanelToggle\"", StringComparison.Ordinal);
        int icon    = styles.IndexOf("Selector=\"StackPanel.Toolbar mi|MaterialIcon\"", StringComparison.Ordinal);
        int checkedIcon = styles.IndexOf("Selector=\"ToggleButton.PanelToggle:checked mi|MaterialIcon\"", StringComparison.Ordinal);

        Assert.True(flat >= 0 && icon >= 0 && checkedIcon >= 0);
        Assert.True(icon < checkedIcon,
            "the checked icon rule must come after the 60%-grey toolbar icon rule — with both active, the later style wins");
    }

    /// <summary>
    /// The toolbar's old Messages-only command is GONE, not left beside the toggle. It could only make
    /// the panel active, so pressing the button twice did nothing the second time — a second, weaker way
    /// to show one panel is what made the button read as broken.
    /// </summary>
    [Fact]
    public void TheOldMessagesRegionCommand_IsGone()
    {
        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories)
                    .Concat(Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.axaml", SearchOption.AllDirectories)))
        {
            var text = File.ReadAllText(file);
            int at = text.IndexOf("ToggleMessagesRegion", StringComparison.Ordinal);

            // Named in the comment that records why it went — but never bound, and never declared.
            Assert.True(at < 0 || text.Contains("used to run a ToggleMessagesRegion command", StringComparison.Ordinal),
                $"{Path.GetFileName(file)} still references ToggleMessagesRegion");
        }
    }

    /// <summary>
    /// <b>A closed panel's remembered place is written OVER the default-placement entry, not skipped.</b>
    ///
    /// <para>The regression this pins: <c>DockLayoutCapture</c> already writes an <c>Open = false</c> entry
    /// at the DEFAULT placement for every default-layout panel it cannot find in the tree. The two wBond
    /// panels are in no default layout, so the persistence pass only ever had to ADD — and adding alone
    /// would silently discard the user's own placement for Library, Properties and Messages, which are.</para>
    /// </summary>
    [Fact]
    public void APanelsRememberedPlace_OverwritesTheDefaultPlacementCaptureLeftBehind()
    {
        var layout = new CwsDockLayout
        {
            Panels =
            {
                // What Capture writes for a panel it did not find: the DEFAULT placement.
                new CwsDockPanel { Id = DockPanelIds.Properties, Open = false, Side = DockSide.Left, Group = 1, Order = 0, Proportion = 0.5 },
            },
        };

        WorkspaceViewModel.RecordClosedPanelPlacement(layout, DockPanelIds.Properties, new CwsDockPanel
        {
            Id = DockPanelIds.Properties, Side = DockSide.Right, Group = 2, Order = 3, Inboard = true, Proportion = 0.25,
        });

        var entry = Assert.Single(layout.Panels, p => p.Id == DockPanelIds.Properties);
        Assert.False(entry.Open);
        Assert.Equal(DockSide.Right, entry.Side);
        Assert.Equal(2, entry.Group);
        Assert.Equal(3, entry.Order);
        Assert.True(entry.Inboard);
        Assert.Equal(0.25, entry.Proportion);
    }

    /// <summary>A panel with no entry at all — the wBond case — is still ADDED, closed.</summary>
    [Fact]
    public void APanelTheLayoutDoesNotName_IsAddedAsAClosedEntry()
    {
        var layout = new CwsDockLayout();

        WorkspaceViewModel.RecordClosedPanelPlacement(layout, DockPanelIds.WBondInductance, new CwsDockPanel
        {
            Id = DockPanelIds.WBondInductance, Side = DockSide.Left, Group = 0, Order = 0, Inboard = true, Proportion = 0.19,
        });

        var entry = Assert.Single(layout.Panels);
        Assert.Equal(DockPanelIds.WBondInductance, entry.Id);
        Assert.False(entry.Open);
        Assert.True(entry.Inboard);
    }

    /// <summary>
    /// A panel that is OPEN in the live arrangement is left exactly as captured. The remembered record is
    /// then stale — it was restored after being remembered — and overwriting would close it on disk.
    /// </summary>
    [Fact]
    public void AnOpenPanel_IsNeverRewrittenFromAStaleRecord()
    {
        var layout = new CwsDockLayout
        {
            Panels = { new CwsDockPanel { Id = DockPanelIds.Palette, Open = true, Side = DockSide.Right, Group = 0, Order = 0, Proportion = 1.0 } },
        };

        WorkspaceViewModel.RecordClosedPanelPlacement(layout, DockPanelIds.Palette, new CwsDockPanel
        {
            Id = DockPanelIds.Palette, Side = DockSide.Left, Group = 9, Order = 9, Proportion = 0.1,
        });

        var entry = Assert.Single(layout.Panels);
        Assert.True(entry.Open);
        Assert.Equal(DockSide.Right, entry.Side);
        Assert.Equal(0, entry.Group);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  The launch bug: a visible Library, an unlit button (owner, 2026-08-18)
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The cause, as data.</b> The shell is BUILT with the Library tabbed behind the Project Tree —
    /// where it genuinely is not in view — and then rebuilt into the Window Layout preset a moment later,
    /// where the Library is alone in its own column and plainly is. So the answer for one panel changes
    /// during launch, which is why the button could be left saying the opposite of the screen.
    ///
    /// <para>Properties and Messages are the front tab of their group in BOTH, which is exactly why the
    /// owner saw those two lit and only the Library wrong.</para>
    /// </summary>
    [Fact]
    public void TheLaunchRebuild_ChangesWhetherTheLibraryIsTheFrontTab()
    {
        static (bool Front, int Siblings) FrontTab(CwsDockLayout state, string panelId)
        {
            var factory = new CircuitRfDockFactory();
            var root = factory.CreateLayoutFromState(state);
            factory.InitLayout(root);

            var tool = factory.ToolById(panelId);
            Assert.NotNull(tool);
            Assert.True(factory.TryFindTool(tool!, out var parent, out _));
            Assert.NotNull(parent);

            return (ReferenceEquals(parent!.ActiveDockable, tool), parent.VisibleDockables?.Count ?? 0);
        }

        // As built: tabbed with the Project Tree, and behind it.
        var built = FrontTab(DockLayoutDefaults.Default(), DockPanelIds.Palette);
        Assert.False(built.Front);
        Assert.True(built.Siblings > 1);

        // As rebuilt for the shipped Window Layout preset: its own column, nothing in front of it.
        var rebuilt = FrontTab(DockLayoutDefaults.ProjectTreeAndLibrary(), DockPanelIds.Palette);
        Assert.True(rebuilt.Front);

        // …and the two that were right all along are the front tab either way.
        foreach (var state in new[] { DockLayoutDefaults.Default(), DockLayoutDefaults.ProjectTreeAndLibrary() })
            foreach (var panel in new[] { DockPanelIds.Properties, DockPanelIds.Messages })
                Assert.True(FrontTab(state, panel).Front, $"{panel} should be the front tab of its group");
    }

    /// <summary>
    /// Half the fix: a whole-tree rebuild renotifies. <c>ApplyDockLayout</c> already did; the Window Layout
    /// preset and Reset Layout go through <c>RebuildLayoutFrom</c> instead, which did not.
    /// </summary>
    [Fact]
    public void AWholeTreeRebuild_RenotifiesTheToolbar()
    {
        var code = Read("src", "Ui", "ViewModels", "WorkspaceViewModel.cs");

        int at  = code.IndexOf("private void RebuildLayoutFrom(", StringComparison.Ordinal);
        Assert.True(at >= 0);
        int end = code.IndexOf("\n    }", at);

        Assert.Contains("RaiseToolPanelVisibilityChanged();", code[at..end], StringComparison.Ordinal);
    }

    /// <summary>
    /// The other half: which tab is IN FRONT is part of the answer now, and a tab switch changes it for two
    /// panels without any panel being added, removed or moved. Nothing else the view model subscribes to
    /// fires for that — and this one must NOT be routed to <c>OnDockArrangementChanged</c>, which would arm
    /// a `.cws` write on every click.
    /// </summary>
    [Fact]
    public void ATabSwitch_RenotifiesTheToolbarWithoutArmingASave()
    {
        var code = Read("src", "Ui", "ViewModels", "WorkspaceViewModel.Docking.cs");

        Assert.Contains("_factory.ActiveDockableChanged += (_, _) => RaiseToolPanelVisibilityChanged();",
                        code, StringComparison.Ordinal);
        Assert.DoesNotContain("ActiveDockableChanged += (_, _) => OnDockArrangementChanged()",
                              code, StringComparison.Ordinal);
    }

    /// <summary>
    /// …and the event really does fire when a tab is brought forward, rather than being a name that only
    /// looks right. Driven through the factory the shell actually uses.
    /// </summary>
    [Fact]
    public void TheFactoryRaisesActiveDockableChanged_WhenATabIsBroughtForward()
    {
        var factory = new CircuitRfDockFactory();
        var root = factory.CreateLayoutFromState(DockLayoutDefaults.Default());
        factory.InitLayout(root);

        var palette = factory.ToolById(DockPanelIds.Palette);
        Assert.NotNull(palette);
        Assert.True(factory.TryFindTool(palette!, out var parent, out _));
        Assert.False(ReferenceEquals(parent!.ActiveDockable, palette), "the Library starts behind the Project Tree");

        int raised = 0;
        factory.ActiveDockableChanged += (_, _) => raised++;

        factory.SetActiveDockable(palette!);

        Assert.True(ReferenceEquals(parent.ActiveDockable, palette), "the Library is the front tab now");
        Assert.True(raised > 0, "bringing a tab forward must raise ActiveDockableChanged");
    }

    /// <summary>
    /// Reopening a panel nothing remembers falls back to the arrangement's OWN closed entry rather than
    /// floating a window over the canvas — and reuses that entry instead of naming the panel twice
    /// (R-dock-1: the id is the identity).
    /// </summary>
    [Fact]
    public void ReopeningAPanel_FallsBackToTheArrangementsOwnPlacement()
    {
        var vm = Read("src", "Ui", "ViewModels", "WorkspaceViewModel.Docking.cs");
        int at = vm.IndexOf("private bool RestorePanelToItsHome(", StringComparison.Ordinal);
        int end = vm.IndexOf("private void OnToolPanelClosing(", StringComparison.Ordinal);
        Assert.True(at >= 0 && end > at);

        var body = vm[at..end];
        Assert.Contains("home.Docked ?? live.Panels.FirstOrDefault(p => p.Id == panelId && !p.Open)", body, StringComparison.Ordinal);
        Assert.Contains("if (live.Panels.Contains(d))", body, StringComparison.Ordinal);
        Assert.DoesNotContain("if (!_panelHomes.TryGetValue(panelId, out var home)) return false;", body, StringComparison.Ordinal);
    }
}

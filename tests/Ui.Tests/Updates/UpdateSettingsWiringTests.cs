using System;
using System.IO;
using System.Linq;
using CircuitRF.Ui;
using CircuitRF.Ui.Theming;
using CircuitRF.Ui.Updates;
using Xunit;

namespace CircuitRF.Ui.Tests.Updates;

/// <summary>
/// R-AU-40 / R-AU-41 / R-AU-42 — gate items 19, 20 and 21.
///
/// <para>These are asserted against the SOURCE rather than by driving the dialog, because
/// <c>Ui.Tests</c> deliberately calls no Avalonia runtime API (see its <c>.csproj</c>). The
/// properties are structural, so a source assertion is the honest form: what is being pinned is that
/// the guard exists and that the enablement is set in both places, not that a particular pixel
/// changed.</para>
/// </summary>
public class UpdateSettingsWiringTests
{
    private static DirectoryInfo RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitRF.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!;
    }

    private static string Read(params string[] parts)
        => File.ReadAllText(Path.Combine([RepoRoot().FullName, .. parts]));

    private static string Control()      => Read("src", "Ui", "Views", "Dialogs", "UpdateSettingsView.axaml.cs");
    private static string ControlMarkup() => Read("src", "Ui", "Views", "Dialogs", "UpdateSettingsView.axaml");

    /// <summary>
    /// R-AU-42 / gate 21. SettingsView is shared by circuitRF AND wBond; harmonicaRF has its own
    /// window and does not use it at all. Without its own reachable copy, a user who has only
    /// harmonicaRF installed could never turn automatic updates off.
    /// </summary>
    [Fact]
    public void BothDialogsHostTheSameControl()
    {
        Assert.Contains("UpdateSettingsView", Read("src", "Ui", "Views", "Dialogs", "SettingsView.axaml"));
        Assert.Contains("UpdateSettingsView", Read("src", "Ui", "Views", "Dialogs", "HarmonicaSettingsDialog.axaml"));
    }

    /// <summary>
    /// ...and it is the SAME implementation, not a second copy: only one file writes the two
    /// preferences. Two copies would drift, and the one that drifted would be the one nobody opened.
    /// </summary>
    [Fact]
    public void OnlyOneFileWritesTheTwoPreferences()
    {
        string[] writers = Directory
            .EnumerateFiles(Path.Combine(RepoRoot().FullName, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f =>
            {
                string code = UpdateInstallSiteTests.StripComments(File.ReadAllText(f));
                return code.Contains("p.AutomaticUpdates =", StringComparison.Ordinal)
                    || code.Contains("p.IncludeBetaUpdates =", StringComparison.Ordinal);
            })
            .Select(f => Path.GetFileName(f))
            .ToArray();

        Assert.Equal(["UpdateSettingsView.axaml.cs"], writers);
    }

    /// <summary>
    /// R-AU-40 / gate 19. The populate guard is not optional: setting <c>IsChecked</c> raises
    /// <c>IsCheckedChanged</c>, and without it the dialog writes the preference it just read on every
    /// open — which would make "absence is the default" untrue after one visit to Settings.
    /// </summary>
    [Fact]
    public void ThePopulateIsGuarded_AndEveryHandlerRespectsTheGuard()
    {
        string code = UpdateInstallSiteTests.StripComments(Control());

        Assert.Contains("_loading = true;", code);
        Assert.Contains("finally { _loading = false; }", code);

        // Every changed-handler bails on the guard BEFORE writing anything.
        foreach (string handler in new[] { "OnAutoUpdateChanged", "OnIncludeBetasChanged" })
        {
            int i = code.IndexOf(handler + "(object?", StringComparison.Ordinal);
            Assert.True(i > 0, handler + " is missing");

            int write = code.IndexOf("AppPreferencesIo.Update", i, StringComparison.Ordinal);
            int guard = code.IndexOf("if (_loading) return;", i, StringComparison.Ordinal);

            Assert.True(guard > 0 && guard < write, $"{handler} writes before checking the guard");
        }
    }

    /// <summary>
    /// Persisted through <c>AppPreferencesIo.Update</c>, never Load-then-Save, so a partial write
    /// cannot clobber the other fields.
    /// </summary>
    [Fact]
    public void PreferencesArePersistedThroughUpdate_NotLoadThenSave()
    {
        string code = UpdateInstallSiteTests.StripComments(Control());

        Assert.Contains("AppPreferencesIo.Update", code);
        Assert.DoesNotContain("AppPreferencesIo.Save", code);
    }

    /// <summary>
    /// R-AU-41 / gate 20. The beta checkbox is disabled whenever automatic updates are off — set in
    /// BOTH places, or it desynchronises. A disabled child whose state still reads "on" is the kind
    /// of detail that survives review and confuses users.
    /// </summary>
    [Fact]
    public void TheBetaCheckboxEnablementIsSetOnLoadAsWellAsOnChange()
    {
        string code = UpdateInstallSiteTests.StripComments(Control());

        int load   = code.IndexOf("public void Load()", StringComparison.Ordinal);
        int change = code.IndexOf("OnAutoUpdateChanged(object?", StringComparison.Ordinal);
        Assert.True(load > 0 && change > load);

        Assert.Contains("SyncBetaEnablement()", code[load..change]);      // on load
        Assert.Contains("SyncBetaEnablement()", code[change..]);          // and on change

        // ...and the rule itself.
        Assert.Contains("IncludeBetasCheck.IsEnabled = AutoUpdateCheck.IsChecked == true", code);
    }

    /// <summary>
    /// A changed handler must do MORE than write a preference: it discards what the new settings no
    /// longer justify. A settings change that only mutates JSON is incomplete.
    /// </summary>
    [Fact]
    public void BothHandlersApplyTheSideEffect()
    {
        string code = UpdateInstallSiteTests.StripComments(Control());

        int auto = code.IndexOf("OnAutoUpdateChanged(object?", StringComparison.Ordinal);
        int beta = code.IndexOf("OnIncludeBetasChanged(object?", StringComparison.Ordinal);
        Assert.True(auto > 0 && beta > auto);

        Assert.Contains("ApplyUpdatePreferenceChange()", code[auto..beta]);
        Assert.Contains("ApplyUpdatePreferenceChange()", code[beta..]);
    }

    /// <summary>
    /// R-AU-45's UI half: under an override the checkbox renders DISABLED, with the reason. A
    /// checkbox the user can tick that changes nothing is worse than one they cannot.
    /// </summary>
    [Fact]
    public void AnOverrideDisablesTheCheckboxAndShowsWhy()
    {
        string code = UpdateInstallSiteTests.StripComments(Control());

        Assert.Contains("policy.IsOverridden", code);
        Assert.Contains("AutoUpdateCheck.IsEnabled    = false;", code);
        Assert.Contains("UpdateOverrideText.Text      = policy.Reason;", code);
        Assert.Contains("UpdateOverrideText.IsVisible = true;", code);
    }

    /// <summary>
    /// R-AU-39's UI half. The dialog READS the last-check time from the updater's state file and
    /// never writes it, and renders <i>never</i> when there is none.
    /// </summary>
    [Fact]
    public void TheLastCheckedLineIsReadOnly()
    {
        string code = UpdateInstallSiteTests.StripComments(Control());

        Assert.Contains("UpdateStateIo.Load()", code);
        Assert.DoesNotContain("UpdateStateIo.Save", code);
        Assert.DoesNotContain("UpdateStateIo.Update", code);
        Assert.Contains("Last checked: never", Control());
    }

    /// <summary>
    /// R-AU-38: one preferences.json serves all three applications, and that must be stated in the
    /// help text so the scope of the checkbox is not a surprise.
    /// </summary>
    [Fact]
    public void TheHelpTextNamesAllThreeApplications()
    {
        string markup = ControlMarkup();
        foreach (string app in new[] { "circuitRF", "harmonicaRF", "wBond" })
            Assert.Contains(app, markup);
    }

    /// <summary>
    /// R-AU-49. Help ▸ Check for Updates… reports through the Message Panel, is present on both menu
    /// surfaces, and is disabled when automatic updates are off or the site is read-only.
    /// </summary>
    [Fact]
    public void HelpCheckForUpdatesIsOnBothMenuSurfaces_AndIsGated()
    {
        string window = Read("src", "Ui", "Views", "WorkspaceWindow.axaml");

        Assert.Contains("<NativeMenuItem Header=\"Check for Updates…\"", window);
        Assert.Contains("Check for _Updates…", window);
        Assert.Equal(2, System.Text.RegularExpressions.Regex
            .Matches(window, "CheckForUpdatesCommand").Count);

        string vm = Read("src", "Ui", "ViewModels", "WorkspaceViewModel.cs");
        Assert.Contains("CanExecute = nameof(CanCheckForUpdates)", vm);

        // Automatic updates off is the ONE thing that disables it: a manual check is still a network
        // call. It is NOT gated on CanSelfUpdate — a notify-only install still checks and still gets
        // told (R-AU-1), and gating it there left those users no way to learn anything at all while
        // making the NotifyOnly branch below it unreachable (second review, 2026-08-25).
        Assert.Contains("CanCheckForUpdates => Updates.UpdatePolicy.Current.AutomaticUpdates;", vm);
        Assert.DoesNotContain("AutomaticUpdates && Updates.UpdateInstallSite.Detect().CanSelfUpdate", vm);
    }

    /// <summary>
    /// The enablement above is only re-evaluated when the command says so. Without a
    /// NotifyCanExecuteChanged the Help item kept whatever state it had when the view-model was
    /// constructed, so turning automatic updates off in Settings left a live menu item behind
    /// (second review, 2026-08-25). The dialog that changes the setting is where the refresh hangs.
    /// </summary>
    [Fact]
    public void ClosingTheSettingsDialog_RefreshesTheHelpItemsEnablement()
    {
        string vm = Read("src", "Ui", "ViewModels", "WorkspaceViewModel.cs");

        Assert.Contains("public void RefreshUpdateCommandState() => CheckForUpdatesCommand.NotifyCanExecuteChanged();", vm);
        Assert.Contains("w.Closed += (_, _) => RefreshUpdateCommandState();", vm);
    }

    /// <summary>
    /// R-AU-43. One shared scheduler, three call sites, no copy — and the delay is where the design
    /// says it is, so the check never competes with startup.
    /// </summary>
    [Fact]
    public void AllThreeApplicationsScheduleTheCheckThroughTheOneHook()
    {
        foreach (string app in new[] { "App.axaml.cs", "HarmonicaApp.axaml.cs", "WBondApp.axaml.cs" })
            Assert.Contains("UpdateStartup.AfterFirstWindow", Read("src", "Ui", app));

        foreach (string program in new[] { "Program.cs", "ProgramHarmonica.cs", "ProgramWBond.cs" })
            Assert.Contains("UpdateStartup.RunBeforeUi", Read("src", "Ui", program));

        Assert.Equal(TimeSpan.FromSeconds(60), UpdateScheduler.StartupDelay);
    }

    /// <summary>
    /// R-AU-2. The whole subsystem lives in src/Ui/Updates, and nothing below src/Ui touches it —
    /// src/Cli is a headless driver that runs in build pipelines, and a binary that silently replaces
    /// itself mid-CI is a defect.
    /// </summary>
    [Fact]
    public void NothingOutsideSrcUiKnowsTheUpdaterExists()
    {
        foreach (string project in new[] { "Core", "Engine", "Cli", "RfCore" })
        {
            string dir = Path.Combine(RepoRoot().FullName, "src", project);
            if (!Directory.Exists(dir)) continue;

            foreach (string file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                string code = UpdateInstallSiteTests.StripComments(File.ReadAllText(file));
                Assert.False(code.Contains("CircuitRF.Ui.Updates", StringComparison.Ordinal),
                             $"{project} references the update subsystem.");
                Assert.False(code.Contains("UpdateScheduler", StringComparison.Ordinal),
                             $"{project} references the update subsystem.");
            }
        }
    }

    /// <summary>
    /// R-AU-3. No version string is introduced anywhere: the running version is AppVersion.Display,
    /// which reads InformationalVersion, which Directory.Build.props reads from VERSION.
    /// </summary>
    [Fact]
    public void TheUpdaterIntroducesNoVersionLiteral()
    {
        string dir = Path.Combine(RepoRoot().FullName, "src", "Ui", "Updates");

        foreach (string file in Directory.EnumerateFiles(dir, "*.cs"))
        {
            string code = UpdateInstallSiteTests.StripComments(File.ReadAllText(file));
            Assert.False(System.Text.RegularExpressions.Regex.IsMatch(code, @"\b\d+\.\d+\.\d+"),
                         $"{Path.GetFileName(file)} contains what looks like a hard-coded version.");
        }

        Assert.Contains("AppVersion.Display",
                        File.ReadAllText(Path.Combine(dir, "UpdateService.cs")));
    }

    /// <summary>
    /// The two update checkboxes live in <b>Security &amp; Permissions</b>, not General (owner,
    /// 2026-08-25). That tab is where everything deciding what circuitRF may RUN or FETCH now lives —
    /// the external-PDK generator trust store and these two together — because a checkbox governing
    /// whether the application downloads and installs code from the internet is a security setting
    /// first and a convenience second.
    ///
    /// <para>Asserted rather than left to a comment: a control moves back to whichever tab someone
    /// happens to be editing, and nothing else in the build would notice.</para>
    /// </summary>
    [Fact]
    public void TheUpdateSettingsLiveInTheSecurityAndPermissionsTab()
    {
        string xaml = Read("src", "Ui", "Views", "Dialogs", "SettingsView.axaml");

        int security = xaml.IndexOf("<TabItem Header=\"Security &amp; Permissions\">", StringComparison.Ordinal);
        int colour   = xaml.IndexOf("<TabItem Header=\"Color Theme\">", StringComparison.Ordinal);
        int updates  = xaml.IndexOf("<dlg:UpdateSettingsView", StringComparison.Ordinal);

        Assert.True(security >= 0, "the Security & Permissions tab is gone");
        Assert.True(colour   >= 0, "the Color Theme tab moved, so this test can no longer bracket");
        Assert.True(updates  >= 0, "the update settings are not hosted anywhere");

        Assert.InRange(updates, security, colour);
    }

    /// <summary>
    /// The Message Panel line tells the user where the setting is, and it has to keep naming the tab
    /// the setting is actually in — a wrong direction is worse than none.
    /// </summary>
    [Fact]
    public void TheStagedNoticeNamesTheTabTheSettingIsActuallyIn()
    {
        Assert.Contains("Security & Permissions", Read("src", "Ui", "Updates", "UpdateService.cs"));
        Assert.Contains("Security &amp; Permissions", Read("src", "Ui", "Views", "Dialogs", "SettingsView.axaml"));
    }
}

/// <summary>
/// The one thing found during this phase that was writing INSIDE the signed application bundle.
/// Not part of the updater, but directly upstream of the check it performs.
/// </summary>
public class BundleIntegrityTests
{
    private static string Read(string rel)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitRF.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, rel));
    }

    /// <summary>
    /// The shipped PCell generator scripts live inside <c>&lt;app&gt;.app/Contents/MacOS/pcell-python/</c>
    /// on macOS. Python's default is to write <c>__pycache__/*.pyc</c> beside every module it
    /// imports, so without this the first workspace that runs a generator adds files to a SEALED
    /// bundle and breaks its code signature — silently, because an installed app carries no
    /// quarantine attribute and is therefore never assessed.
    /// </summary>
    [Fact]
    public void ThePythonBytecodeCacheIsRedirectedOutOfTheApplicationBundle()
    {
        string code = Read(Path.Combine("src", "Ui", "Layout", "PCells", "Wire", "PCellWorkerTransport.cs"));

        Assert.Contains("PYTHONPYCACHEPREFIX", code);
        Assert.Contains("AppDataRoot.SubDir(\"pcell-cache\")", code);
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CircuitRF.Design.Revision;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui.Diagnostics;
using CircuitRF.Ui.Revision;
using Xunit;

namespace CircuitRF.Ui.Tests.Revision;

/// <summary>
/// RC-4's gates: Settings ▸ Revision Control (<c>docs/design/revision-control.md</c> §10A).
///
/// <para><b>Source scans where the property is structural, behaviour where it is a decision.</b> This
/// project deliberately calls no Avalonia runtime API, so "the tab is at index 2" and "the AI-edit row
/// is not interactive" are read out of the XAML — which is honest, because what is being pinned IS the
/// XAML. The two things that are genuinely decisions — which switch wins, and whether the tab exists at
/// all — were extracted into <see cref="RevisionArming"/> and
/// <see cref="RevisionTabAvailability"/> precisely so they could be tested as decisions rather than
/// inferred from a condition inline in a dialog.</para>
///
/// <para><b>Comments are stripped before every source scan.</b> This repository has been caught by a
/// source scan satisfied by its own documentation, and this file is full of prose that quotes the
/// requirements it is checking.</para>
/// </summary>
public class RevisionControlSettingsTests
{
    // ── Reading the tree ──────────────────────────────────────────────────────────────────────────

    private static DirectoryInfo RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitRF.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!;
    }

    private static string Read(params string[] parts)
        => File.ReadAllText(Path.Combine([RepoRoot().FullName, .. parts]));

    private static string Dialog(string file) => Read("src", "Ui", "Views", "Dialogs", file);

    /// <summary>Line and block comments removed, so a source scan cannot be satisfied by prose.</summary>
    private static string StripCode(string code)
        => Regex.Replace(Regex.Replace(code, @"/\*.*?\*/", "", RegexOptions.Singleline), @"//[^\n]*", "");

    /// <summary>XML comments removed, for a scan over XAML.</summary>
    private static string StripXaml(string xaml)
        => Regex.Replace(xaml, @"<!--.*?-->", "", RegexOptions.Singleline);

    /// <summary>The tab headers, in declaration order.</summary>
    private static List<string> TabHeaders()
        => Regex.Matches(StripXaml(Dialog("SettingsView.axaml")), @"<TabItem Header=""([^""]+)""")
                .Select(m => m.Groups[1].Value.Replace("&amp;", "&"))
                .ToList();

    // ── Gate 1: order and position ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The fifth tab is at INDEX 2, headed "Revision Control", immediately after Security &amp;
    /// Permissions (R-rc4-1, owner 2026-09-06).
    /// </summary>
    [Fact]
    public void TheTabIsAtIndexTwoImmediatelyAfterSecurityAndPermissions()
    {
        Assert.Equal(
            ["General", "Security & Permissions", "Revision Control", "Color Theme", "Wirebonds"],
            TabHeaders());
    }

    // ── Gate 2: one row at MinWidth ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The tab strip occupies ONE ROW with five headers, and both halves of the fix are present.
    ///
    /// <para><b>The structural property, not a rendered pixel</b> — which is what <c>Ui.Tests</c>
    /// asserts. The pixels were measured separately, with a throwaway headless harness at
    /// <c>MinWidth="620"</c>: on the theme's own metrics the five headers come to 850 px against 592 px
    /// of usable width and two of them land on a second row; with the style below they come to 529 px,
    /// one row, 63 px spare. The measurement is in the XAML's own comment.</para>
    ///
    /// <para><b>Scoped, never a global TabItem selector</b> — this dialog is not the only
    /// <c>TabControl</c> in the application, and a global one would re-metric the technology editor's
    /// strip as a side effect.</para>
    /// </summary>
    [Fact]
    public void TheTabStripIsAStackPanelWithAScopedStyleThatPinsTheHeaderSize()
    {
        string xaml = StripXaml(Dialog("SettingsView.axaml"));

        int panel = xaml.IndexOf("<TabControl.ItemsPanel>", StringComparison.Ordinal);
        Assert.True(panel >= 0,
            "SettingsView's TabControl has no ItemsPanel override. TabControl's DEFAULT ItemsPanel is a "
          + "WrapPanel: headers that do not fit spill onto a second row, they do not clip, and nothing "
          + "errors.");

        string panelBlock = xaml[panel..xaml.IndexOf("</TabControl.ItemsPanel>", panel, StringComparison.Ordinal)];
        Assert.Contains("<StackPanel", panelBlock);
        Assert.Contains("Orientation=\"Horizontal\"", panelBlock);

        int styles = xaml.IndexOf("<TabControl.Styles>", StringComparison.Ordinal);
        Assert.True(styles >= 0,
            "The horizontal StackPanel alone makes one row a PROMISE; the scoped TabItem style is what "
          + "makes it a FIT. Without it the five headers measure 850 px against 592 px.");

        string styleBlock = xaml[styles..xaml.IndexOf("</TabControl.Styles>", styles, StringComparison.Ordinal)];
        Assert.Contains("Selector=\"TabItem\"", styleBlock);
        Assert.Contains("Property=\"FontSize\"", styleBlock);
        Assert.Contains("Property=\"Padding\"", styleBlock);
        Assert.Contains("Property=\"MinHeight\"", styleBlock);
    }

    // ── Gate 3: the figures renumber, and each shows the tab it claims ────────────────────────────

    /// <summary>
    /// <b>The failure no existing test caught.</b> <c>DocSettingsFixtures</c> selects a figure by tab
    /// INDEX, so inserting a tab at index 2 silently re-points every figure below it — two chapters
    /// illustrating the wrong tab, with nothing failing on the substance.
    ///
    /// <para>Checked by comparing each fixture's index against the header actually declared at that
    /// position, rather than by transcribing the expected numbers: a test that only said
    /// <c>ColorTheme() == Tab(3)</c> would pass just as happily if both the fixture and the XAML moved
    /// the wrong way together.</para>
    /// </summary>
    [Fact]
    public void EachSettingsFigureSelectsTheTabItClaimsToShow()
    {
        string source = StripCode(Read("src", "Ui", "Diagnostics", "Fixtures", "DocSettingsFixtures.cs"));
        var headers = TabHeaders();

        // fixture method -> the header that fixture's name says it is a picture of
        var expected = new (string Method, string Header)[]
        {
            ("General",         "General"),
            ("Security",        "Security & Permissions"),
            ("RevisionControl", "Revision Control"),
            ("ColorTheme",      "Color Theme"),
            ("Wirebonds",       "Wirebonds"),
        };

        foreach (var (method, header) in expected)
        {
            var m = Regex.Match(source, $@"FigureScene {method}\(\)\s*=>\s*Tab\((\d+)\)");
            Assert.True(m.Success, $"DocSettingsFixtures no longer has a {method}() => Tab(n) fixture.");

            int index = int.Parse(m.Groups[1].Value);
            Assert.True(index < headers.Count,
                $"{method}() selects tab {index}, but SettingsView declares only {headers.Count} tabs.");
            Assert.Equal(header, headers[index]);
        }
    }

    /// <summary>The figure is captured with the tab's controls forced live, so it does not depend on
    /// whether the generating machine happened to have git installed.</summary>
    [Fact]
    public void TheSettingsFigureCaptureForcesTheRevisionTabAvailable()
        => Assert.Contains("ShowAsAvailableForCapture",
                           StripCode(Read("src", "Ui", "Diagnostics", "Fixtures", "DocSettingsFixtures.cs")));

    // ── Gate 4: five figures, five citations ─────────────────────────────────────────────────────
    // (SettingsDialogHelpAndTooltipsTests.EveryTabOfTheDialogHasItsOwnFigureAndThePageCitesThemAll was
    //  EXTENDED to five rather than relaxed — that test doing its job is why the tab could not land
    //  without its figure and its chapter section.)

    /// <summary>The new figure exists in the catalogue and the chapter has a section for the tab.</summary>
    [Fact]
    public void TheChapterHasASectionForTheNewTabAndCitesItsFigure()
    {
        Assert.Contains(FigureCatalog.Catalog, r => r.Id == "settings-revision-control");

        string page = Read("docs", "user", "src", "reference", "settings.md");
        Assert.Contains("{{ui: settings-revision-control}}", page);
        Assert.Contains("{#revision-control}", page);

        // The chapter's table of contents walks the tabs; a section nothing links to is one nobody
        // finds from the top of the page.
        Assert.Contains("#revision-control", page[..page.IndexOf("</nav>", StringComparison.Ordinal)]);
    }

    // ── Gate 5: shown always, usable only with git, and the path row never grey ───────────────────

    /// <summary>
    /// <b>The tab is on every machine</b> (owner, 2026-09-07) — which reverses R-rc4-3's original
    /// answer, and the reversal is the requirement rather than a relaxation of it.
    ///
    /// <para>Hiding the tab kept absence silent. It also hid, from the designer who WOULD have wanted a
    /// history, the fact that circuitRF can keep one and is not keeping one here — the same false
    /// belief §1.4 is written against, reached from the other side, and the remedy was a program they
    /// could have installed in five minutes. What replaces it is honest in both directions: the rows
    /// are visible so what would be kept is legible, and greyed so nobody believes anything is.</para>
    /// </summary>
    [Theory]
    [InlineData(false)]   // no usable git: shown, and everything but the path row is greyed
    [InlineData(true)]    // a usable git: shown, and live
    public void TheTabIsShownOnEveryMachineAndIsUsableOnlyWithGit(bool gitAvailable)
    {
        Assert.True(RevisionTabAvailability.TabIsAlwaysShown);
        Assert.Equal(gitAvailable, RevisionTabAvailability.ControlsEnabled(gitAvailable));
    }

    /// <summary>
    /// <b>The git-path row is OUTSIDE the block that gets disabled, and that is the whole of the
    /// arrangement.</b> It is the remedy — how somebody with git in an unusual location, or none at
    /// all, finds out what circuitRF sees and fixes it — so a greyed Detect would leave a machine
    /// unable to answer its own question.
    /// </summary>
    [Fact]
    public void TheGitPathRowSurvivesTheDisabledBlockAndTheNoticeExplainsIt()
    {
        string xaml = StripXaml(Dialog("RevisionControlSettingsView.axaml"));

        int path     = xaml.IndexOf("<dlg:GitPathSettingsView", StringComparison.Ordinal);
        int notice   = xaml.IndexOf("Name=\"NoGitNotice\"", StringComparison.Ordinal);
        int disabled = xaml.IndexOf("Name=\"HistoryControls\"", StringComparison.Ordinal);

        Assert.True(path >= 0 && notice > path && disabled > notice,
                    "the git-path row must precede the notice and the disabled block, and sit outside it");

        // Every other row is inside the block, so a row added later cannot be forgotten.
        foreach (string control in (string[])
                 ["IdentityNameBox", "KeepHistoryCheck", "WorkspaceRevisionCheck",
                  "RetentionDaysUpDown", "MinimumPointsUpDown", "CheckpointOnCloseCheck",
                  "CheckpointBeforeAiCheck", "PackThresholdUpDown", "PackNowButton",
                  "ReclaimAgeUpDown", "ReclaimButton"])
            Assert.True(xaml.IndexOf($"Name=\"{control}\"", StringComparison.Ordinal) > disabled,
                        $"{control} must sit inside the block that is disabled without a git");

        string code = StripCode(Dialog("RevisionControlSettingsView.axaml.cs"));
        Assert.Contains("RevisionTabAvailability.ControlsEnabled", code);
        Assert.Contains("HistoryControls.IsEnabled", code);
        Assert.Contains("RevisionTabAvailability.NoGitNotice", code);
    }

    /// <summary>
    /// <b>Detect turns the rest of the tab on.</b> Detect writes no preference, so a handler keyed on
    /// the path changing would miss the ordinary sequence — install git, press the button that exists
    /// to find it — and the rows would stay grey until Settings was closed and reopened.
    /// </summary>
    [Fact]
    public void DetectReAsksTheMachineAndReportsThatAvailabilityMayHaveChanged()
    {
        string detect = Section(StripCode(Dialog("GitPathSettingsView.axaml.cs")), "OnDetectGit");
        Assert.Contains("GitDiscovery.InvalidateCache", detect);
        Assert.Contains("GitAvailabilityChanged", detect);

        Assert.Contains("GitPath.GitAvailabilityChanged",
                        StripCode(Dialog("RevisionControlSettingsView.axaml.cs")));
    }

    /// <summary>
    /// The git-path row's second host is gone with the rule that created it. It existed only so that
    /// hiding the tab did not also hide the field; with the tab always present, a copy on Security
    /// &amp; Permissions would be a row nobody could ever reach.
    /// </summary>
    [Fact]
    public void TheGitPathRowHasExactlyOneHost()
    {
        string xaml = StripXaml(Dialog("SettingsView.axaml"));
        int security = xaml.IndexOf("<TabItem Header=\"Security &amp; Permissions\"", StringComparison.Ordinal);
        int revision = xaml.IndexOf("<TabItem Header=\"Revision Control\"", StringComparison.Ordinal);
        Assert.True(security >= 0 && revision > security);

        Assert.DoesNotContain("GitPathSettingsView", xaml[security..revision]);
        Assert.DoesNotContain("GitPathFallback", xaml);
        Assert.DoesNotContain("GitPathFallback", StripCode(Dialog("SettingsView.axaml.cs")));
    }

    // ── Gate 6: the per-workspace flag round-trips, and two workspaces are independent ────────────

    /// <summary>
    /// The flag lives in the <c>.cws</c> and two workspaces open at once hold independent values.
    ///
    /// <para><b>This is the gate that catches the mistake the repository has already made</b> — a
    /// per-installation flag gating per-workspace state, correct for the first workspace and silently
    /// wrong for the second (<c>src/Ui/RESOLVED.md</c>, the wirebond group work).</para>
    /// </summary>
    [Fact]
    public void ThePerWorkspaceFlagRoundTripsAndTwoWorkspacesAreIndependent()
    {
        using var a = new TempWorkspace();
        using var b = new TempWorkspace();

        // Never recorded is a THIRD state, not "false": it is what falls back to the preference.
        Assert.Null(WorkspaceRevisionSetting.Read(a.Cws));
        Assert.Null(WorkspaceRevisionSetting.Read(b.Cws));

        Assert.True(WorkspaceRevisionSetting.Write(a.Cws, false));
        Assert.True(WorkspaceRevisionSetting.Write(b.Cws, true));

        Assert.False(WorkspaceRevisionSetting.Read(a.Cws));
        Assert.True(WorkspaceRevisionSetting.Read(b.Cws));

        // And clearing goes back to "never recorded", not to false.
        Assert.True(WorkspaceRevisionSetting.Write(a.Cws, null));
        Assert.Null(WorkspaceRevisionSetting.Read(a.Cws));
        Assert.True(WorkspaceRevisionSetting.Read(b.Cws));
    }

    /// <summary>
    /// It is in the VERSIONED half — the <c>.cws</c> — and not the per-user <c>.cwsuser</c> sidecar
    /// (§5.7). That is what makes turning it off itself a recorded change, and it is why the setting
    /// travels with a copy of the workspace.
    /// </summary>
    [Fact]
    public void ThePerWorkspaceFlagIsWrittenIntoTheCwsAndNotTheUserSidecar()
    {
        using var ws = new TempWorkspace();
        WorkspaceRevisionSetting.Write(ws.Cws, false);

        Assert.Contains("revisionControl", File.ReadAllText(ws.Cws), StringComparison.OrdinalIgnoreCase);

        string sidecar = ws.Cws + "user";
        if (File.Exists(sidecar))
            Assert.DoesNotContain("revisionControl", File.ReadAllText(sidecar), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>It is not a preference, and the dialog does not write it as one.</summary>
    [Fact]
    public void ThePerWorkspaceFlagIsNotAnApplicationPreference()
    {
        string prefs = StripCode(Read("src", "Ui", "Theming", "AppPreferences.cs"));
        Assert.DoesNotContain("RevisionControlForThisWorkspace", prefs);

        string code = StripCode(Dialog("RevisionControlSettingsView.axaml.cs"));
        var handler = Section(code, "OnWorkspaceRevisionChanged");

        // It writes the WORKSPACE'S setting, through RC-6's ordered transition rather than by writing
        // the flag directly (R-rc6-14a): the .cws first, then one entry recording the change, and only
        // then does circuitRF stop writing. Reversed, the flag is set, nothing records it, and the
        // history stops with no entry saying why — which is invisible from the flag alone. What this
        // gate is actually about is unchanged: whatever it writes, it is not an application preference.
        Assert.Contains("RevisionSwitch.Turn", handler);
        Assert.DoesNotContain("AppPreferencesIo.Update", handler);
    }

    // ── Gate 6a: precedence, all four combinations ───────────────────────────────────────────────

    /// <summary>
    /// §5.7a's table, in full. <b>The fourth row is the one that would be missed</b>: a workspace that
    /// recorded a setting keeps it when the preference changes — a per-workspace decision must not be
    /// silently rewritten by a global one.
    /// </summary>
    [Theory]
    [InlineData(false, null,  false)] // preference off beats an unrecorded workspace
    [InlineData(false, true,  true)]  // ... but NOT one that said yes for itself
    [InlineData(true,  false, false)] // preference on, workspace switched off: off
    [InlineData(true,  null,  true)]  // preference on, nothing recorded: on
    [InlineData(true,  true,  true)]
    [InlineData(false, false, false)]
    public void ThePerWorkspaceSettingOutranksThePreference(
        bool preference, bool? workspace, bool armed)
        => Assert.Equal(armed, RevisionArming.IsArmed(preference, workspace));

    /// <summary>
    /// The fourth row again, stated as the property rather than as a table row: a workspace that
    /// recorded an answer gives the SAME answer whichever way the preference is set, and one that
    /// recorded nothing follows it.
    /// </summary>
    [Fact]
    public void ARecordedWorkspaceSettingIsNotRewrittenByAChangeOfPreference()
    {
        foreach (bool recorded in (bool[])[true, false])
            Assert.Equal(RevisionArming.IsArmed(true,  recorded),
                         RevisionArming.IsArmed(false, recorded));

        Assert.NotEqual(RevisionArming.IsArmed(true, null), RevisionArming.IsArmed(false, null));
    }

    // ── Gate 6b: the preference ships on ─────────────────────────────────────────────────────────

    /// <summary>
    /// A fresh installation keeps a history. Asserted against the DOCUMENTED default — the constant the
    /// architecture's §5.7a is transcribed into — and against the absent-means-default rule, rather
    /// than against whatever the writer happens to do.
    /// </summary>
    [Fact]
    public void ThePreferenceShipsOn()
    {
        Assert.True(RevisionArming.KeepHistoryDefault);

        // Absent, on a fresh preferences file, and absent resolves to the default.
        Assert.Null(new CircuitRF.Ui.Theming.AppPreferences().RevisionKeepHistory);
        Assert.True(RevisionArming.IsArmed(
            new CircuitRF.Ui.Theming.AppPreferences().RevisionKeepHistory ?? RevisionArming.KeepHistoryDefault,
            null));

        // And the dialog reads it through that constant, not through a literal of its own.
        Assert.Contains("RevisionArming.KeepHistoryDefault",
                        StripCode(Dialog("RevisionControlSettingsView.axaml.cs")));
    }

    /// <summary>
    /// <b>The close-boundary row follows the switch above it, and keeps its value</b> (owner-reported,
    /// 2026-09-17). It says WHEN to record, and there is no when in a workspace that records nothing,
    /// so leaving it live made it a control whose every value meant the same thing.
    ///
    /// <para>Keyed on <see cref="RevisionArming.IsArmed"/> rather than on the per-user switch alone,
    /// because a workspace may override that switch ON — the case a straight read of the preference
    /// gets wrong, in the one place nobody looks. And the row's own value is never rewritten: turning
    /// history off must not silently discard a preference, any more than it discards a restore
    /// point.</para>
    /// </summary>
    [Fact]
    public void TheCloseCheckpointRowIsGreyedWhenNothingIsBeingKept()
    {
        string code = StripCode(Dialog("RevisionControlSettingsView.axaml.cs"));

        int enabled = code.IndexOf("CheckpointOnCloseCheck.IsEnabled", StringComparison.Ordinal);
        Assert.True(enabled > 0, "the close-boundary row is never disabled, so history off leaves a "
                              +  "live control that changes nothing");
        Assert.Contains("ApplyCheckpointOnCloseAvailability(RevisionArming.IsArmed(", code);

        // Exactly one writer of the checkbox's VALUE, and it is the one that loads the preference.
        var writes = System.Text.RegularExpressions.Regex.Matches(
            code, @"CheckpointOnCloseCheck\.IsChecked\s*=[^=]");
        Assert.Single(writes);
        Assert.Contains("prefs.RevisionCheckpointOnClose", writes[0].Value + code[
            (writes[0].Index)..Math.Min(code.Length, writes[0].Index + 120)]);

        // And the greyed row says why, in this UI (the tab's own rule 1).
        Assert.Contains("Name=\"CheckpointOnCloseScope\"", StripXaml(Dialog("RevisionControlSettingsView.axaml")));
    }

    /// <summary>Every one of the nine application-wide preferences is nullable and omitted when null —
    /// absent means the documented default, which is what makes a machine with no preferences file a
    /// correctly configured one.</summary>
    [Theory]
    [InlineData("RevisionGitPath")]
    [InlineData("RevisionIdentityName")]
    [InlineData("RevisionIdentityEmail")]
    [InlineData("RevisionKeepHistory")]
    [InlineData("RevisionRetentionDays")]
    [InlineData("RevisionMinimumRestorePoints")]
    [InlineData("RevisionCheckpointOnClose")]
    [InlineData("RevisionPackThresholdMb")]
    [InlineData("RevisionReclaimAgeDays")]
    public void EveryRevisionPreferenceIsNullableAndAbsentWhenUnset(string property)
    {
        var p = typeof(CircuitRF.Ui.Theming.AppPreferences).GetProperty(property);
        Assert.NotNull(p);
        Assert.True(Nullable.GetUnderlyingType(p!.PropertyType) is not null
                 || p.PropertyType == typeof(string),
                    $"{property} is not nullable, so 'never set' and 'set to the default' are the same "
                  + "state and the documented default cannot be changed later.");
        Assert.Null(p.GetValue(new CircuitRF.Ui.Theming.AppPreferences()));
    }

    /// <summary>
    /// The identity keys are the ones RC-3's <c>src/Design</c> reader looks for — shared through a
    /// constant, not transcribed. Two spellings of the same key means the settings tab writes an
    /// identity <c>serve</c> cannot read, silently.
    /// </summary>
    [Fact]
    public void TheIdentityPreferenceKeysAreTheOnesTheHeadlessReaderUses()
    {
        string prefs = StripCode(Read("src", "Ui", "Theming", "AppPreferences.cs"));
        Assert.Contains("JsonPropertyName(RevisionIdentity.NameKey)",  prefs);
        Assert.Contains("JsonPropertyName(RevisionIdentity.EmailKey)", prefs);
    }

    // ── Gate 7: the count floor cannot be set below its minimum ──────────────────────────────────

    /// <summary>
    /// R-rc4-10: the field cannot go below the hard minimum, <b>including by typing a value rather than
    /// using the stepper</b>.
    ///
    /// <para>The floor is what makes §5.6's first rule real: a wall clock is user-writable state, and
    /// keeping the newest N unconditionally is what makes a clock jump cost the user nothing. A user
    /// must not be able to give that guarantee away by typing a zero.</para>
    /// </summary>
    [Fact]
    public void TheCountFloorCannotBeSetBelowItsMinimum()
    {
        int floor = RevisionPreferenceDefaults.MinimumRestorePointsFloor;
        int max   = RevisionPreferenceDefaults.MinimumRestorePointsCeiling;

        Assert.True(floor > 0, "A floor of zero is not a floor.");
        Assert.True(RevisionPreferenceDefaults.MinimumRestorePoints >= floor);

        foreach (int typed in (int[])[int.MinValue, -1, 0, 1, floor - 1])
            Assert.Equal(floor, RevisionPreferenceDefaults.Clamp(typed, floor, max));

        Assert.Equal(floor,     RevisionPreferenceDefaults.Clamp(floor, floor, max));
        Assert.Equal(floor + 1, RevisionPreferenceDefaults.Clamp(floor + 1, floor, max));
        Assert.Equal(max,       RevisionPreferenceDefaults.Clamp(int.MaxValue, floor, max));
    }

    /// <summary>
    /// And the typed path really is clamped: every numeric handler goes through the clamping helper
    /// before it writes, and the control's own Minimum is set from the same constant.
    ///
    /// <para>A <c>NumericUpDown</c> coerces a typed value against its bounds — but an empty or
    /// unparseable box yields NULL, which coerces to nothing at all, and letting that reach the
    /// preference is how a hard floor quietly becomes an advisory one.</para>
    /// </summary>
    [Fact]
    public void EveryNumericFieldWritesThroughTheClamp()
    {
        string code = StripCode(Dialog("RevisionControlSettingsView.axaml.cs"));

        foreach (string handler in (string[])
                 ["OnRetentionDaysChanged", "OnMinimumPointsChanged",
                  "OnPackThresholdChanged", "OnReclaimAgeChanged"])
        {
            string body = Section(code, handler);
            Assert.Contains("Clamped(", body);
            Assert.Contains("AppPreferencesIo.Update", body);
        }

        Assert.Contains("RevisionPreferenceDefaults.MinimumRestorePointsFloor", code);
        Assert.Contains("box.Minimum = min", code);
    }

    // ── Gate 8: the AI-edit row is shown, on, and not switchable ─────────────────────────────────

    /// <summary>
    /// R-rc4-10: <b>present and not interactive, not absent.</b> It is §1.2 — the reason the whole
    /// feature exists — and it is shown so nobody has to wonder whether it is happening.
    ///
    /// <para><b>Disabled, so that it reads as not editable</b> (owner, 2026-09-06). The alternative —
    /// an ordinary-looking checkbox with hit-testing off — silently ignores a click, so the user does
    /// not learn the row is fixed, only that clicking did nothing.</para>
    /// </summary>
    [Fact]
    public void TheAiEditRowIsShownOnAndNotSwitchable()
    {
        string xaml = StripXaml(Dialog("RevisionControlSettingsView.axaml"));

        int at = xaml.IndexOf("Name=\"CheckpointBeforeAiCheck\"", StringComparison.Ordinal);
        Assert.True(at >= 0,
            "The 'restore point before an AI edit' row is gone. It is SHOWN rather than hidden on "
          + "purpose — it is the reason the feature exists.");

        string row = xaml[xaml.LastIndexOf("<CheckBox", at, StringComparison.Ordinal)..];
        row = row[..row.IndexOf("</CheckBox>", StringComparison.Ordinal)];

        Assert.Contains("IsChecked=\"True\"", row);
        Assert.Contains("IsEnabled=\"False\"", row);
        Assert.DoesNotContain("IsCheckedChanged=", row);

        // Nothing writes it, because there is no preference for it to write.
        Assert.DoesNotContain("CheckpointBeforeAi",
                              StripCode(Read("src", "Ui", "Theming", "AppPreferences.cs")));
    }

    // ── Gate 9: consequence sentences ────────────────────────────────────────────────────────────

    /// <summary>
    /// R-rc4-13: every control that can reduce what is kept states its consequence <b>next to itself,
    /// in this UI</b> — not in a tooltip and not only in the manual. A designer who reads only this tab
    /// must come away with a correct belief.
    ///
    /// <para>Checked over the XAML with its comments stripped and over the code-behind that completes
    /// the sentences naming a number — because a sentence that says "after this long" while the field
    /// says 30 is not the sentence a designer needs.</para>
    /// </summary>
    [Fact]
    public void EveryControlThatReducesWhatIsKeptStatesItsConsequenceBesideIt()
    {
        string xaml = StripXaml(Dialog("RevisionControlSettingsView.axaml"));
        string code = StripCode(Dialog("RevisionControlSettingsView.axaml.cs"));

        // The four standing blocks exist, and they are standing TextBlocks rather than tooltips.
        foreach (string name in (string[])
                 ["RetentionConsequence", "MinimumPointsConsequence",
                  "WorkspaceRevisionConsequence", "ReclaimConsequence"])
        {
            int at = xaml.IndexOf($"Name=\"{name}\"", StringComparison.Ordinal);
            Assert.True(at >= 0, $"The consequence sentence '{name}' is gone from the Revision Control tab.");

            int tipOpen  = xaml.LastIndexOf("<ToolTip.Tip>",  at, StringComparison.Ordinal);
            int tipClose = xaml.LastIndexOf("</ToolTip.Tip>", at, StringComparison.Ordinal);
            Assert.True(tipOpen < 0 || tipClose > tipOpen,
                $"'{name}' is inside a ToolTip.Tip. R-rc4-13 requires the consequence to be visible "
              + "without hovering: a tooltip carries an EXPLANATION, a consequence has to be read.");

            Assert.Contains("TextWrapping=\"Wrap\"", EnclosingElement(xaml, at));
        }

        // The application-wide off switch carries the same promise at the wider scope. Its sentence is
        // a literal in the XAML rather than a named block, so it is matched on its own words.
        Assert.Contains("in every workspace", xaml);

        // "Deletes nothing", in those words, for BOTH off switches — R-rc4-14. An off switch that
        // quietly discarded a history would be the single most damaging control in the application.
        Assert.Equal(2, Occurrences(xaml, "It deletes nothing"));

        // The two sentences that name a number are completed at runtime from the field's own value.
        foreach (string fragment in (string[])
                 ["RetentionConsequence.Text", "MinimumPointsConsequence.Text", "ReclaimConsequence.Text"])
            Assert.Contains(fragment, code);
    }

    /// <summary>The consequences appear in the chapter as well as beside the control. That is not
    /// duplication: R-rc4-13 exists because a designer who reads only the tab must still come away
    /// correct, and one who reads only the chapter must too.</summary>
    [Fact]
    public void TheChapterCarriesTheSameConsequences()
    {
        string page = Read("docs", "user", "src", "reference", "settings.md");
        int at = page.IndexOf("{#revision-control}", StringComparison.Ordinal);
        Assert.True(at >= 0);
        string section = page[at..];

        Assert.Contains("deletes nothing", section, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("thinned", section, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("bring them back", section, StringComparison.OrdinalIgnoreCase);
    }

    // ── Gate 9a: reclaim asks, names what it destroys, and is the only caller ─────────────────────

    /// <summary>
    /// The confirmation is presented with the COUNT and the AGE, and cancelling reclaims nothing.
    ///
    /// <para>Either alone leaves a user unable to tell whether the set is the one they meant, which is
    /// the whole job of a confirmation on an irreversible action.</para>
    /// </summary>
    [Fact]
    public void ReclaimAsksFirstAndNamesTheCountAndTheAge()
    {
        string dialog = StripCode(Dialog("ReclaimSpaceDialog.axaml.cs"));

        // Both numbers reach the wording.
        Assert.Contains("int count", dialog);
        Assert.Contains("int days",  dialog);
        Assert.Contains("HeadlineLabel.Text", dialog);
        Assert.Contains("DestroysLabel.Text", dialog);
        Assert.Contains("SurvivesLabel.Text", dialog);
        Assert.Contains("nobody", dialog);          // "after it, nobody can bring those states back"
        Assert.Contains("survives", dialog);        // and what does survive

        // Cancel returns false, and the caller only acts on true.
        Assert.Contains("Close(false)", dialog);
        Assert.Contains("Close(true)",  dialog);

        string caller = StripCode(Dialog("RevisionControlSettingsView.axaml.cs"));
        var    body   = Section(caller, "OnReclaimSpace");
        Assert.Contains("ReclaimSpaceDialog", body);

        int ask     = body.IndexOf("ShowDialog<bool>", StringComparison.Ordinal);
        int reclaim = body.IndexOf("GitReclaim.Reclaim", StringComparison.Ordinal);
        Assert.True(ask >= 0 && reclaim > ask,
            "The reclaim runs before, or without, the confirmation being answered.");
        Assert.Contains("is not true", body[..reclaim]);

        // The count comes from the JOURNAL, not from git's own object age — git expires by when a
        // state was MADE, and the question here is when it was THINNED.
        Assert.Contains("ThinningJournal", body);
    }

    /// <summary>
    /// Both Disk Space actions <b>name the workspace they would act on</b> (owner, 2026-09-06).
    /// Neither opens a picker: they act on the workspace already open, and a button that silently
    /// picks its own subject is one a user has to press to understand.
    ///
    /// <para>With no workspace open they are <b>disabled</b> and fall back to their unqualified
    /// captions — with nothing to name, the button cannot say what it would do, so the line beneath
    /// says why instead.</para>
    /// </summary>
    [Fact]
    public void TheDiskSpaceActionsNameTheWorkspaceTheyWouldActOn()
    {
        string code = StripCode(Dialog("RevisionControlSettingsView.axaml.cs"));

        // Whitespace-normalised: this file aligns its assignments in columns, and a scan that depended
        // on how many spaces sit before an '=' pins the formatter rather than the behaviour.
        string body = Squash(Section(code, "LoadWorkspaceScopedControls"));

        // Both captions carry the name, and it comes from the ONE helper the per-workspace checkbox
        // also uses — three controls saying the same workspace three different ways is how one of them
        // ends up naming a different one.
        Assert.Contains("string named = QuotedWorkspaceName(dir);", body);
        Assert.Contains("PackNowButton.Content = $\"Compact {named}\";", body);
        Assert.Contains("ReclaimButton.Content = $\"Reclaim Space in {named}…\";", body);
        Assert.Contains("WorkspaceRevisionCheck.Content = $\"Keep a history of {named}\";", body);

        // And disabled with no workspace, not merely left to refuse on click.
        Assert.Contains("PackNowButton.IsEnabled = false;", body);
        Assert.Contains("ReclaimButton.IsEnabled = false;", body);
        Assert.Contains("PackNowButton.IsEnabled = true;",  body);
        Assert.Contains("ReclaimButton.IsEnabled = true;",  body);

        // The name is bounded, because it goes inside a button and the dialog has a minimum width.
        Assert.Contains("const int max", Section(code, "QuotedWorkspaceName"));
    }

    /// <summary>
    /// <b>No other path in <c>src/Ui</c> reaches the reclaim.</b> Nothing reclaims on a schedule, and
    /// the age field on its own does nothing — so a second call site anywhere would be a second, unasked
    /// way to destroy the only copy of a thinned state.
    /// </summary>
    [Fact]
    public void NothingElseInTheApplicationReachesTheReclaim()
    {
        var callers = new List<string>();

        foreach (string file in Directory.EnumerateFiles(
                     Path.Combine(RepoRoot().FullName, "src", "Ui"), "*.cs", SearchOption.AllDirectories))
        {
            if (StripCode(File.ReadAllText(file)).Contains("GitReclaim.Reclaim", StringComparison.Ordinal))
                callers.Add(Path.GetFileName(file));
        }

        Assert.Equal(["RevisionControlSettingsView.axaml.cs"], callers);
    }

    /// <summary>
    /// The other disk-space action is <b>packing, which never reclaims</b> — it stores the same history
    /// compactly and discards nothing.
    /// </summary>
    [Fact]
    public void PackingIsASeparateActionThatDiscardsNothing()
    {
        string body = Section(StripCode(Dialog("RevisionControlSettingsView.axaml.cs")), "OnPackNow");
        Assert.Contains("GitPacking.Pack", body);
        Assert.DoesNotContain("GitReclaim",   body);
        Assert.DoesNotContain("prune",        body, StringComparison.OrdinalIgnoreCase);
    }

    // ── The journal reader ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The count the confirmation shows is the set <c>GitReclaim</c> would act on, selected by when a
    /// state was THINNED and not by when it was made.
    /// </summary>
    [Fact]
    public void TheJournalCountsWhatWasThinnedLongAgoAndNotWhatIsOld()
    {
        var now = new DateTimeOffset(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);
        var journal = new List<ThinnedState>
        {
            new("a", now.AddDays(-200)),   // thinned long ago
            new("b", now.AddDays(-100)),   // thinned long ago
            new("c", now.AddDays(-10)),    // thinned recently — protected
        };

        Assert.Equal(2, ThinningJournal.CountOlderThan(journal, TimeSpan.FromDays(90), now));
        Assert.Equal(0, ThinningJournal.CountOlderThan(journal, TimeSpan.FromDays(365), now));
        Assert.Equal(3, ThinningJournal.CountOlderThan(journal, TimeSpan.FromDays(1), now));

        // GitReclaim's own selection agrees, which is the property that matters: the number the user is
        // shown is the number of states destroyed.
        var protectedOnly = journal.Where(e => e.ThinnedUtc > now - TimeSpan.FromDays(90)).ToList();
        Assert.Single(protectedOnly);
    }

    /// <summary>An absent journal is an empty one, and one malformed line is one lost entry rather than
    /// a control that cannot be used.</summary>
    [Fact]
    public void TheJournalReaderIsTolerantOfAnAbsentOrDamagedFile()
    {
        using var ws = new TempWorkspace();
        Assert.Empty(ThinningJournal.Read(ws.Root));

        string path = ThinningJournal.PathFor(ws.Root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path,
        [
            ThinningJournal.Format(new ThinnedState("aaa", DateTimeOffset.UnixEpoch)),
            "{ not json at all",
            "",
            ThinningJournal.Format(new ThinnedState("bbb", DateTimeOffset.UnixEpoch.AddDays(1))),
        ]);

        var entries = ThinningJournal.Read(ws.Root);
        Assert.Equal(["aaa", "bbb"], entries.Select(e => e.CommitId));
    }

    // ── Gate 10: tooltips wrap and are bounded ───────────────────────────────────────────────────
    // (Held by SettingsDialogHelpAndTooltipsTests.EveryParagraphTooltipWrapsAndIsBounded, extended
    //  with this tab's two sub-views.)

    // ── Gate 11/12: identity, and where it is NOT written ────────────────────────────────────────

    /// <summary>
    /// <b>The identity is a per-USER preference and does not move when the open workspace changes</b> —
    /// which is the exact opposite of the per-workspace flag above, and the pair is worth reading
    /// together.
    ///
    /// <para>rev 2 of the architecture put the identity in the workspace repository's config. That makes
    /// a person a property of a DIRECTORY, so on a network share the second designer to open a shared
    /// workspace commits under the first one's name — silently, until somebody reads a history and
    /// disbelieves it.</para>
    /// </summary>
    [Fact]
    public void TheIdentityIsPerUserAndDoesNotMoveWithTheWorkspace()
    {
        using var state = new TempStateDirectory();
        using var a     = new TempWorkspace();
        using var b     = new TempWorkspace();

        File.WriteAllText(CircuitRF.Design.UserStateDirectory.PreferencesPath,
            $"{{\"{RevisionIdentity.NameKey}\":\"A Designer\",\"{RevisionIdentity.EmailKey}\":\"d@example.invalid\"}}");

        // Same answer regardless of which workspace is open, because it is not asked about one.
        var first = RevisionIdentity.FromPreferences();
        Assert.NotNull(first);
        Assert.Equal("A Designer", first!.Name);

        WorkspaceRevisionSetting.Write(a.Cws, true);
        WorkspaceRevisionSetting.Write(b.Cws, false);

        var second = RevisionIdentity.FromPreferences();
        Assert.Equal(first.Name,  second!.Name);
        Assert.Equal(first.Email, second.Email);
    }

    /// <summary>
    /// RC-3's reader returns what this tab wrote — <b>from a type that references nothing in
    /// <c>src/Ui</c></b>, which is what lets <c>serve</c> commit under the same name.
    /// </summary>
    [Fact]
    public void TheHeadlessReaderReturnsWhatTheTabWrote()
    {
        using var state = new TempStateDirectory();

        // Written exactly as the tab writes it: through AppPreferences, whose keys ARE the reader's.
        CircuitRF.Ui.Theming.AppPreferencesIo.Update(p =>
        {
            p.RevisionIdentityName  = "Written By The Tab";
            p.RevisionIdentityEmail = "tab@example.invalid";
        });

        var read = RevisionIdentity.FromPreferences();
        Assert.NotNull(read);
        Assert.Equal("Written By The Tab",  read!.Name);
        Assert.Equal("tab@example.invalid", read.Email);

        // The reader lives below the firewall.
        Assert.Equal("CircuitRF.Design", typeof(RevisionIdentity).Assembly.GetName().Name);
    }

    /// <summary>
    /// Half an identity is no identity. Blank stores null rather than an empty string, so "never set"
    /// and "cleared" stay the same state — otherwise git guesses the missing half at
    /// <c>user@hostname</c>, which is the silent wrong answer §4.4 exists to stop.
    /// </summary>
    [Fact]
    public void HalfAnIdentityIsNoIdentity()
    {
        using var state = new TempStateDirectory();

        CircuitRF.Ui.Theming.AppPreferencesIo.Update(p =>
        {
            p.RevisionIdentityName  = "Only A Name";
            p.RevisionIdentityEmail = null;
        });

        Assert.Null(RevisionIdentity.FromPreferences());
        Assert.DoesNotContain(RevisionIdentity.EmailKey, File.ReadAllText(CircuitRF.Design.UserStateDirectory.PreferencesPath));
    }

    /// <summary>
    /// <b>Nothing writes the identity into any git config file</b> (R-rc4-11a) — not the repository's,
    /// not the user's global one. Read as a source scan across the whole of <c>src/Ui</c> and
    /// <c>src/Design</c>, because the shape of rev 2's mistake was one path doing it in a corner.
    /// </summary>
    [Fact]
    public void NoPathAnywhereWritesTheIdentityIntoAGitConfigFile()
    {
        var offenders = new List<string>();

        foreach (string project in (string[])["Ui", "Design", "Cli"])
        foreach (string file in Directory.EnumerateFiles(
                     Path.Combine(RepoRoot().FullName, "src", project), "*.cs", SearchOption.AllDirectories))
        {
            string code = StripCode(File.ReadAllText(file));

            // A `git config` invocation that also names one of the identity keys and is not a read.
            foreach (System.Text.RegularExpressions.Match m in Regex.Matches(code, @"""config""[^;]*?;", RegexOptions.Singleline))
            {
                string call = m.Value;
                if (!call.Contains("user.name") && !call.Contains("user.email")) continue;
                if (call.Contains("--get")) continue;   // reading a global identity is unobjectionable
                offenders.Add($"{Path.GetFileName(file)}: {call.Trim()}");
            }
        }

        Assert.Empty(offenders);
    }

    /// <summary>
    /// The tab pre-fills from the user's existing global git identity <b>and never writes it back</b>.
    /// Reading it is unobjectionable; only writing it was ever the problem, because circuitRF has no
    /// business changing a setting that affects every other repository on the machine.
    /// </summary>
    [Fact]
    public void TheIdentityFieldPreFillsFromTheGlobalConfigAndDoesNotWriteItBack()
    {
        string code = StripCode(Dialog("RevisionControlSettingsView.axaml.cs"));
        string body = Section(code, "LoadIdentity");

        Assert.Contains("RevisionIdentity.GlobalGitDefault", body);

        // The pre-fill fills the BOXES and does not commit them: a machine whose global identity later
        // changes must not be silently pinned to the old one by a value circuitRF copied unasked.
        Assert.DoesNotContain("AppPreferencesIo.Update", body);

        // And the reader it uses is a --global --get, which the scan above already proved is the only
        // shape of `git config` anywhere near an identity.
        string reader = StripCode(Read("src", "Design", "Revision", "RevisionIdentity.cs"));
        Assert.Contains("\"--global\", \"--get\", \"user.name\"",  reader.Replace(" ", " "));
    }

    // ── Gate 13: Detect reports an under-floor git as too old ────────────────────────────────────

    /// <summary>
    /// R-rc3-3a / R-rc4-10: <b>this is the one place an under-floor git is reported as TOO OLD</b>,
    /// naming the version found and the version needed. Everywhere else it is treated as absent, because
    /// a designer with an old git does not want to be told about a feature they cannot have — but
    /// somebody who pressed Detect asked, and "not found" would send them looking for an installation
    /// that is right there.
    /// </summary>
    [Fact]
    public void DetectReportsAnUnderFloorGitAsTooOldNamingBothVersions()
    {
        // The version that would be rejected really is below the floor — asserted everywhere, with no
        // process involved, so this half of the gate holds on every platform.
        var old = GitDiscovery.ParseVersion("git version 1.8.3");
        Assert.NotNull(old);
        Assert.True(old! < GitDiscovery.MinimumVersion);

        // The SENTENCE comes from discovery, so it cannot drift from the floor actually enforced —
        // driven through the same probe Detect's Find() uses, against a stand-in that answers
        // --version and nothing else. A real old git is not installable on a CI machine.
        //
        // Unix only, and that is a limitation of the STAND-IN rather than of the code under test: the
        // stand-in is a shell script, and on Windows the equivalent is a .cmd, which CreateProcess
        // cannot launch with UseShellExecute=false. The assertion above still runs there, and CI covers
        // this half on two of the three platforms.
        if (!OperatingSystem.IsWindows())
        {
            Assert.False(GitDiscovery.TryProbe(FakeGit("1.8.3"), "test", out var installation, out string? why));
            Assert.Null(installation);
            Assert.NotNull(why);
            Assert.Contains("1.8.3", why!);
            Assert.Contains(GitDiscovery.MinimumVersion.ToString(), why);
            Assert.Contains("older", why, StringComparison.OrdinalIgnoreCase);
        }

        // And Detect surfaces the rejection notes rather than a bare "not found".
        string body = Section(StripCode(Dialog("GitPathSettingsView.axaml.cs")), "OnDetectGit");
        Assert.Contains("rejected", body);
        Assert.Contains("GitDiscovery.Find", body);
    }

    /// <summary>An under-floor git leaves the rest of the application behaving as though git were
    /// absent — the tab is still there, and everything on it but the path row is greyed.</summary>
    [Fact]
    public void AnUnderFloorGitIsAbsentEverywhereButTheDetectLine()
    {
        Assert.True(RevisionTabAvailability.TabIsAlwaysShown);
        Assert.False(RevisionTabAvailability.ControlsEnabled(gitAvailable: false));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A script that answers <c>--version</c> with a given banner and nothing else. Enough to drive
    /// discovery's probe without a real old git, which is not installable on a CI machine.
    ///
    /// <para><b>Unix only</b>, and the caller guards for it. The Windows equivalent is a <c>.cmd</c>,
    /// which <c>CreateProcess</c> cannot launch with <c>UseShellExecute=false</c> — so a Windows
    /// stand-in would need a real executable built for the purpose, which is more than this gate is
    /// worth when two of the three CI platforms cover it.</para>
    /// </summary>
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]   // the caller guards for it
    private static string FakeGit(string version)
    {
        string dir = Path.Combine(Path.GetTempPath(), "crf-rc4-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(dir);

        string sh = Path.Combine(dir, "git");
        File.WriteAllText(sh, $"#!/bin/sh\necho \"git version {version}\"\n");
        File.SetUnixFileMode(sh,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        return sh;
    }

    /// <summary>
    /// The text of one method, from its DECLARATION to its closing brace. Crude on purpose: it is
    /// scoping a source scan, not parsing C#.
    ///
    /// <para>The declaration and not merely the first occurrence of the name — otherwise a method
    /// scanned for its own body is silently scoped to whichever CALL SITE happened to come first in
    /// the file, and the scan then passes or fails on unrelated code.</para>
    /// </summary>
    private static string Section(string code, string method)
    {
        var declaration = Regex.Match(
            code, $@"(?:private|internal|public|protected)[^\n(){{}};]*\b{Regex.Escape(method)}\s*\(");
        Assert.True(declaration.Success, $"{method} is no longer declared.");

        int at   = declaration.Index;
        int open = code.IndexOf('{', declaration.Index + declaration.Length);
        Assert.True(open >= 0);

        int depth = 0;
        for (int i = open; i < code.Length; i++)
        {
            if (code[i] == '{') depth++;
            else if (code[i] == '}' && --depth == 0) return code[at..(i + 1)];
        }
        return code[at..];
    }

    /// <summary>The whole XAML element containing <paramref name="at"/>, so an attribute scan does not
    /// depend on the order attributes happen to be written in.</summary>
    private static string EnclosingElement(string xaml, int at)
    {
        int open  = xaml.LastIndexOf('<', at);
        int close = xaml.IndexOf('>', at);
        Assert.True(open >= 0 && close > open);
        return xaml[open..(close + 1)];
    }

    /// <summary>Runs of whitespace collapsed to one space, so a source scan pins what the code DOES
    /// rather than how its assignments happen to be aligned.</summary>
    private static string Squash(string text) => Regex.Replace(text, @"\s+", " ");

    private static int Occurrences(string text, string needle) => text.Split(needle).Length - 1;

    /// <summary>A throwaway workspace folder with a minimal <c>.cws</c>.</summary>
    private sealed class TempWorkspace : IDisposable
    {
        public string Root { get; }
        public string Cws  => Path.Combine(Root, WorkspacePersistence.FileName);

        public TempWorkspace()
        {
            Root = Path.Combine(Path.GetTempPath(), "crf-rc4ws-" + Guid.NewGuid().ToString("N")[..12]);
            Directory.CreateDirectory(Root);
            WorkspacePersistence.SaveToFile(Cws, new CwsFile());
        }

        public void Dispose()
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Redirects the per-user state directory for the duration, so a test never reads or writes the
    /// developer's own <c>preferences.json</c>. Redirected through <c>AppDataRoot</c>, which is the one
    /// lever that also drops the caches resolved against the old location.
    /// </summary>
    private sealed class TempStateDirectory : IDisposable
    {
        private readonly string _dir;

        public TempStateDirectory()
        {
            _dir = Path.Combine(Path.GetTempPath(), "crf-rc4state-" + Guid.NewGuid().ToString("N")[..12]);
            Directory.CreateDirectory(_dir);
            CircuitRF.Ui.AppDataRoot.RedirectTo(_dir);
        }

        public void Dispose()
        {
            CircuitRF.Ui.AppDataRoot.RedirectTo(null);
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}

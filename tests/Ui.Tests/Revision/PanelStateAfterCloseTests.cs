using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using CircuitRF.Design.Revision;
using CircuitRF.Ui.Messages;
using CircuitRF.Ui.Revision;
using CircuitRF.Ui.ViewModels.Dock;
using Xunit;

namespace CircuitRF.Ui.Tests.Revision;

/// <summary>
/// <b>What the revision surfaces say when there is nothing for them to be about</b> — owner-reported,
/// 2026-09-07: a workspace was closed and the foot of its window went on reading <i>History failing —
/// circuitRF tried to record this workspace and could not.</i>
///
/// <para>Two defects met in that one sentence, and they are worth naming separately because each is
/// enough to produce it on its own.</para>
///
/// <list type="number">
/// <item><b>Nothing refreshed the surfaces on the way to the blank shell.</b> Opening a workspace
/// refreshes the indicator and both panels; closing to no workspace went through no such path, so all
/// three kept the last workspace's answer. Opening a SECOND workspace hid this, which is why it
/// survived: the failure needs a close with nothing after it.</item>
/// <item><b>The failed-boundary flag outlived the workspace.</b> One window builds one
/// <see cref="WorkspaceHistoryService"/> and every workspace opened in it shares that instance, so a
/// boundary that failed in one workspace reported the next one as failing too.</item>
/// </list>
///
/// <para><b>A permanent indicator is only worth having if it is true.</b> §1.4 is written against a
/// designer who believes they are protected and is not; an indicator caught saying something plainly
/// false is how they learn to stop reading it, which costs the guarantee in both directions.</para>
///
/// <para>In <see cref="AppDataRootCollection"/> for the reason the other gates here are: these tests
/// redirect the per-user state directory, and that redirection is process-wide.</para>
/// </summary>
[Collection(AppDataRootCollection.Name)]
public class PanelStateAfterCloseTests
{
    // ══ 1. The latched failure ends with the workspace ════════════════════════════════════════════

    /// <summary>
    /// <b>A boundary that failed in one workspace does not follow the window into the next one.</b>
    ///
    /// <para>The failure is produced the way the application produces it rather than by reaching into
    /// the flag: a machine that cannot name a committer refuses at §4.4's check, which is precisely the
    /// failure that would otherwise be invisible — and the one a fresh installation actually hits.</para>
    /// </summary>
    [GitFact]
    public void AFailedBoundaryDoesNotFollowTheWindowIntoTheNextWorkspace()
    {
        using var state = new AppDataRootScope();
        using var ws    = new GitWorkspace();          // an EMPTY global config: no identity anywhere

        var service = new WorkspaceHistoryService(new RecordingSink());
        service.NoteWorkspaceWrite();

        Assert.False(service.TakeSavePoint(ws.Root, "a state worth keeping"));
        Assert.Equal(RecordingState.Failed, service.State(ws.Root));

        // A new workspace is a new session. Nothing has been tried here, so nothing has failed here.
        service.ResetForWorkspace();
        Assert.Equal(RecordingState.On, service.State(ws.Root));
    }

    /// <summary>The indicator's own text follows the state, which is what the owner actually saw.</summary>
    [Fact]
    public void TheIndicatorIsEmptyWhenRecordingIsNormal()
    {
        Assert.Equal("", HoldMessages.IndicatorFor(RecordingState.On));
        Assert.Equal("", HoldMessages.IndicatorDetailFor(RecordingState.On));
        Assert.Equal("History failing", HoldMessages.IndicatorFor(RecordingState.Failed));
    }

    /// <summary>
    /// With no workspace open at all, the state is <see cref="RecordingState.On"/> — which renders as
    /// no indicator. This is what makes refreshing on close sufficient: the close path does not have to
    /// know it should blank the strip, only that it should ask again.
    /// </summary>
    [Fact]
    public void NoWorkspaceMeansNoIndicator()
    {
        var service = new WorkspaceHistoryService(new RecordingSink());

        Assert.Equal(RecordingState.On, service.State(null));
        Assert.Equal("", HoldMessages.IndicatorFor(service.State(null)));
        Assert.Empty(service.ListIncludingThinned(null));
        Assert.Empty(service.VersionRows(null));
    }

    // ══ 2. The close path asks again ══════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Source-scanned, and deliberately.</b> The property is that the teardown to the blank shell
    /// reaches the revision refresh at all — and reaches it AFTER the dock layout has been rebuilt,
    /// because the rebuild replaces the panel instances a refresh performed earlier would have filled.
    ///
    /// <para>Driving a real close would need a window and a live dispatcher, which this project's tests
    /// do not run; and it would assert the same one line of ordering far more expensively. What can go
    /// wrong here is the call being dropped or moved, and both are visible in the source.</para>
    /// </summary>
    [Fact]
    public void ResetToBlankShell_RefreshesTheRevisionSurfaces_AfterTheLayoutIsRebuilt()
    {
        string source = Strip(File.ReadAllText(SourcePath("ViewModels/WorkspaceViewModel.cs")));

        int reset   = source.IndexOf("private void ResetToBlankShell()", StringComparison.Ordinal);
        int refresh = source.IndexOf("OnWorkspaceClosedForRevision()", reset, StringComparison.Ordinal);
        int rebuild = source.IndexOf("WhileRebuildingLayout(", reset, StringComparison.Ordinal);

        Assert.True(reset   >= 0, "ResetToBlankShell has been renamed; this gate must follow it.");
        Assert.True(refresh > reset,
            "Closing a workspace no longer refreshes the revision surfaces, so the recording indicator "
          + "and both history panels keep describing the workspace that has just gone.");
        Assert.True(refresh > rebuild,
            "The revision refresh runs BEFORE the dock layout is rebuilt, so it fills the panels the "
          + "rebuild is about to discard and leaves the new ones holding the previous workspace's list.");
    }

    /// <summary>
    /// The counterpart on the revision partial: closing resets the SESSION as well as the surfaces.
    /// Refreshing without resetting would blank the strip and leave the latched failure to reappear on
    /// the next workspace — half a fix, and the half that is invisible.
    /// </summary>
    [Fact]
    public void ClosingResetsTheSession_NotOnlyTheDisplay()
    {
        string source = Strip(File.ReadAllText(SourcePath("ViewModels/WorkspaceViewModel.Revision.cs")));

        int closed = source.IndexOf("private void OnWorkspaceClosedForRevision()", StringComparison.Ordinal);
        Assert.True(closed >= 0, "OnWorkspaceClosedForRevision has been renamed or removed.");

        int end = source.IndexOf("\n    }", closed, StringComparison.Ordinal);
        string body = source[closed..end];

        Assert.Contains("History.ResetForWorkspace()", body, StringComparison.Ordinal);
        Assert.Contains("RefreshRecordingIndicator()", body, StringComparison.Ordinal);

        // ONE panel since RC-10 (§5.10), so one call. What is asserted is unchanged: the surfaces that
        // describe a workspace are emptied when there is no longer a workspace for them to be about.
        Assert.Contains("RefreshHistoryPanel()", body, StringComparison.Ordinal);
    }

    // ══ 3. A settings change reaches the window it describes ══════════════════════════════════════

    /// <summary>
    /// <b>Turning history on with the workspace open must change what the window says.</b>
    /// Owner-reported, 2026-09-07: the foot of the window went on reading <i>History off</i>.
    ///
    /// <para>The state itself was never in doubt — <see cref="RevisionSwitch"/> writes the flag and
    /// <see cref="WorkspaceHistoryService.State"/> reads it correctly, which this half asserts. What
    /// was missing was anyone telling the window, and that half is below.</para>
    /// </summary>
    [GitFact]
    public void TurningHistoryOnChangesWhatTheIndicatorWouldSay()
    {
        using var state = new AppDataRootScope();
        using var ws    = new GitWorkspace();

        var service = new WorkspaceHistoryService(new RecordingSink());

        RevisionSwitch.TurnOff(ws.Root, keepHistoryPreference: true);
        Assert.Equal(RecordingState.Off, service.State(ws.Root));
        Assert.Equal("History off", HoldMessages.IndicatorFor(service.State(ws.Root)));

        RevisionSwitch.TurnOn(ws.Root, keepHistoryPreference: true);
        Assert.Equal(RecordingState.On, service.State(ws.Root));
        Assert.Equal("", HoldMessages.IndicatorFor(service.State(ws.Root)));
    }

    /// <summary>
    /// <b>Every handler in the Revision Control tab that changes what a window says, says so.</b>
    ///
    /// <para>Source-scanned, and this is the right instrument for it: what went wrong was a MISSING
    /// call, in a non-modal dialog, on a UI thread these tests do not run — and the defect is not that
    /// the notification does the wrong thing but that four handlers each had to remember to make it.
    /// Naming the four here is what makes a fifth one's omission visible.</para>
    /// </summary>
    [Fact]
    public void EverySettingThatChangesWhatAWindowSays_TellsTheOpenWorkspaces()
    {
        string source = Strip(File.ReadAllText(
            SourcePath("Views/Dialogs/RevisionControlSettingsView.axaml.cs")));

        foreach (string handler in (string[])
                 ["ApplyGitAvailability",          // git named, found, or lost
                  "OnKeepHistoryChanged",          // the per-user switch
                  "OnWorkspaceRevisionChanged",    // the per-workspace switch
                  "OnReclaimSpace"])               // destroys thinned states the panel still offers
        {
            // The DECLARATION, not the first mention: one of these is also invoked from a lambda in
            // the constructor, and scoping the search to that line would assert nothing.
            int start = source.IndexOf("void " + handler + "(", StringComparison.Ordinal);
            Assert.True(start >= 0, $"{handler} has been renamed; this gate must follow it.");

            int next = NextHandlerAfter(source, start);
            Assert.True(
                source[start..next].Contains("TellTheOpenWorkspaces()", StringComparison.Ordinal),
                $"{handler} changes what an open workspace's revision surfaces should say and no longer "
              + "tells them. The dialog is not modal, so the workspace it describes is on screen while "
              + "the setting is changed.");
        }
    }

    /// <summary>
    /// And the broadcast reaches <b>every</b> surface. Refreshing the indicator alone would fix the
    /// reported sentence and leave the list stating the old answer beside it.
    /// </summary>
    [Fact]
    public void TheBroadcastRefreshesAllThreeSurfaces()
    {
        string source = Strip(File.ReadAllText(SourcePath("ViewModels/WorkspaceViewModel.Revision.cs")));

        int start = source.IndexOf("public void RefreshRevisionSurfaces()", StringComparison.Ordinal);
        Assert.True(start >= 0, "RefreshRevisionSurfaces has been renamed or removed.");

        // The indicator, plus the list. RC-10 merged the two panels, so "all three surfaces" is now
        // two calls — and the pairing bug this gate was written after is unreachable rather than
        // merely fixed (OpeningAWorkspaceFillsTheHistoryPanel below asserts that directly).
        string body = source[start..source.IndexOf("\n    }", start, StringComparison.Ordinal)];
        Assert.Contains("RefreshRecordingIndicator()", body, StringComparison.Ordinal);
        Assert.Contains("RefreshHistoryPanel()",       body, StringComparison.Ordinal);

        // And it goes to every open workspace: the per-user switch changes what all of them say, and
        // the dialog is not modal, so the others are on screen at the time.
        Assert.Contains("WorkspaceLocator.AllWindows()", source, StringComparison.Ordinal);
    }

    /// <summary>The end of the method starting at <paramref name="start"/>, taken as the next member
    /// declaration — enough to scope a search without parsing C#.</summary>
    private static int NextHandlerAfter(string source, int start)
    {
        var m = Regex.Match(source[(start + 1)..], @"\n    (?:private|public|internal|protected)\s");
        return m.Success ? start + 1 + m.Index : source.Length;
    }

    // ══ 4. The panels with no workspace ═══════════════════════════════════════════════════════════

    /// <summary>
    /// <b>An empty History panel says which empty it is.</b> "Nothing has been kept for this workspace
    /// yet" is a confident claim about a workspace, and with none open there is no workspace for it to
    /// be about — the same defect as the indicator, one panel over.
    ///
    /// <para>RE-POINTED at RC-10's merged panel, not relaxed: the assertion is the same one and the
    /// sentences are the merged panel's.</para>
    /// </summary>
    [Fact]
    public void TheHistoryPanelDoesNotTalkAboutAWorkspaceThatIsNotOpen()
    {
        var tool = new HistoryTool();

        tool.SetRows(Empty, hasWorkspace: false);
        Assert.Equal(HistoryMessages.NoWorkspaceOpenForHistory, tool.EmptyText);

        tool.SetRows(Empty, hasWorkspace: true);
        Assert.Equal(HistoryMessages.NothingRecordedYet, tool.EmptyText);
    }

    /// <summary>An empty result, spelled once.</summary>
    private static HistoryList.Result Empty => new([], 0);

    /// <summary>
    /// The row actions that need a row have one before they are live. <b>This is not the greying
    /// R-rc6-8 forbids</b>: that rule is about held, off and failing, where a greyed control says
    /// nothing about why. "Nothing is selected" needs no sentence — the list is beside the menu.
    /// </summary>
    [Fact]
    public void TheRowActionsThatNeedARowAreLiveOnlyWithOne()
    {
        var tool = new HistoryTool();
        Assert.False(tool.HasSelection);
        Assert.False(tool.CanKeepPermanently);

        tool.SetRows(Rows(Point(1, kept: false), Point(2, kept: true)), hasWorkspace: true);

        tool.Selected = tool.Rows.First(r => r.Entry.Sequence == 1);
        Assert.True(tool.HasSelection);
        Assert.True(tool.CanKeepPermanently);

        // Already pinned: the operation would do nothing, so neither does the menu item.
        tool.Selected = tool.Rows.First(r => r.Entry.Sequence == 2);
        Assert.True(tool.HasSelection);
        Assert.False(tool.CanKeepPermanently);
    }

    /// <summary>Restore points as the merged list would carry them, with no versions and no gaps.</summary>
    private static HistoryList.Result Rows(params RestorePoint[] points)
        => HistoryList.Build([], points, SharedVersions.Local, HistoryFilter.Default);

    /// <summary>
    /// <b>Opening a workspace fills the history panel, and it once did not.</b>
    ///
    /// <para>Owner-reported, 2026-09-07: the Versions panel said <i>No workspace open</i> with a
    /// workspace open. The wrong sentence was the visible half; the consequential half is that
    /// <c>HasWorkspace</c> starts false and is set only by <c>SetRows</c> — so the panel's own
    /// <b>Keep this version</b> button was disabled on every freshly opened workspace, and the only
    /// thing that would have enabled it was keeping a version, which is what the button does. File ▸
    /// Keep This Version… still worked, which is why nobody caught it.</para>
    ///
    /// <para><b>RC-10 removed the class of defect rather than the instance.</b> The bug was always one
    /// of a PAIR of panels left out of a refresh; there is one panel now, so there is one call.</para>
    /// </summary>
    [Fact]
    public void OpeningAWorkspaceFillsTheHistoryPanel()
    {
        string source = Strip(File.ReadAllText(SourcePath("ViewModels/WorkspaceViewModel.Revision.cs")));

        int opened = source.IndexOf("private void OnWorkspaceOpenedForRevision()", StringComparison.Ordinal);
        Assert.True(opened >= 0, "OnWorkspaceOpenedForRevision has been renamed; this gate must follow it.");

        string body = source[opened..source.IndexOf("\n    }", opened, StringComparison.Ordinal)];
        Assert.Contains("RefreshHistoryPanel()", body, StringComparison.Ordinal);

        // And there is exactly ONE panel to reach, which is what makes the pairing bug unreachable
        // rather than merely fixed.
        Assert.DoesNotContain("RefreshRestorePointsPanel", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RefreshVersionHistoryPanel", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// Why the missing refresh was fatal rather than cosmetic: nothing else can turn the panel's
    /// commands on. <b>The default is off and one method is the only way out of it.</b>
    /// </summary>
    [Fact]
    public void TheHistoryPanelStartsWithNoWorkspaceAndOnlyARefreshChangesThat()
    {
        var tool = new HistoryTool();

        Assert.False(tool.HasWorkspace);
        Assert.Equal(HistoryMessages.NoWorkspaceOpenForHistory, tool.EmptyText);

        tool.SetRows(Empty, hasWorkspace: true);
        Assert.True(tool.HasWorkspace);
        Assert.Equal(HistoryMessages.NothingRecordedYet, tool.EmptyText);
    }

    /// <summary>
    /// <b>And CREATING a workspace fills it too</b> — owner-reported, 2026-09-09: a workspace was made
    /// with File ▸ New Workspace, a Gerber imported into it, and the History panel said <i>No workspace
    /// is open</i> about the workspace on screen.
    ///
    /// <para>The same defect as <see cref="OpeningAWorkspaceFillsTheHistoryPanel"/>, one route over and
    /// missed when that one was fixed. New Workspace rebuilds the dock from scratch, which builds a
    /// FRESH panel whose <c>HasWorkspace</c> starts false, and it was one of two routes to an open
    /// workspace that never reached <c>OnWorkspaceOpenedForRevision</c> — the other being a save that
    /// creates the workspace out of scratch documents. The wrong sentence was the visible half; the
    /// consequential half is that <c>CanKeep</c> is <c>HasWorkspace &amp;&amp; CanKeepAnything</c>, so
    /// both keep actions were dead in the panel of a brand-new workspace, which is the state a designer
    /// is most likely to want to record.</para>
    ///
    /// <para>Written over EVERY route rather than over New Workspace, because that is the shape this
    /// defect keeps returning in: a path that arrives at an open workspace without telling the revision
    /// surfaces. A future one is covered without anyone having to remember this.</para>
    /// </summary>
    [Fact]
    public void EveryRouteToAnOpenWorkspaceFillsTheHistoryPanel()
    {
        string source = Strip(File.ReadAllText(SourcePath("ViewModels/WorkspaceViewModel.cs")));

        // An ASSIGNMENT: `=` not followed by a second `=` (a comparison such as the autosave's `CurrentWorkspacePath ==
        // cws` opens nothing).
        var sites = Regex.Matches(source, @"CurrentWorkspacePath\s*=(?!=)\s*(?!null)\S");
        Assert.True(sites.Count >= 3,
            "A route that opens a workspace has gone; this gate must follow the assignment.");

        foreach (System.Text.RegularExpressions.Match site in sites)
        {
            string rest = source[site.Index..NextHandlerAfter(source, site.Index)];
            Assert.True(rest.Contains("OnWorkspaceOpenedForRevision()", StringComparison.Ordinal),
                "A route that sets CurrentWorkspacePath does not tell the revision surfaces about it, "
              + "so its History panel reports 'No workspace is open' with one open and both keep "
              + "actions stay dead. Near: " + OneLineAt(source, site.Index));
        }
    }

    /// <summary>
    /// <b>And the workspace being LEFT gets §5.3's close boundary on every route</b>, which New
    /// Workspace also skipped: it wrote the outgoing session and then abandoned it, so the last stretch
    /// of work in that workspace was never recorded and nothing about the result said so.
    ///
    /// <para>Gated on the pairing rather than on the assignment, because the outgoing session write is
    /// exactly the set of routes that leave a workspace — a save that CREATES one leaves nothing, and
    /// owes no boundary.</para>
    /// </summary>
    [Fact]
    public void EveryRouteAwayFromAWorkspaceTakesItsCloseBoundary()
    {
        string source = Strip(File.ReadAllText(SourcePath("ViewModels/WorkspaceViewModel.cs")));

        var leaving = Regex.Matches(source, @"PersistOutgoingWorkspaceSession\(\);");
        Assert.True(leaving.Count >= 3, "A route that leaves a workspace has gone.");

        foreach (System.Text.RegularExpressions.Match site in leaving)
        {
            string rest = source[site.Index..NextHandlerAfter(source, site.Index)];
            Assert.True(rest.Contains("TakeCloseCheckpoint(CurrentWorkspacePath)", StringComparison.Ordinal),
                "A route that writes the outgoing workspace's session does not then record it, so the "
              + "work done in it since the last entry is kept nowhere. Near: "
              + OneLineAt(source, site.Index));
        }
    }

    /// <summary>The line containing <paramref name="index"/>, for a failure that names where.</summary>
    private static string OneLineAt(string source, int index)
    {
        // Built EAGERLY by every caller — Assert.True's message argument is evaluated whether or not
        // the assertion holds — so it has to be right for a passing site too, not only a failing one.
        int start = source.LastIndexOf('\n', index) + 1;
        int end   = source.IndexOf('\n', start);
        return source[start..(end < 0 ? source.Length : end)].Trim();
    }

    // ══ 5. Closing the WINDOW, not the workspace ══════════════════════════════════════════════════

    /// <summary>
    /// <b>A floated panel does not outlive the workspace window it belongs to.</b>
    ///
    /// <para>Owner-reported, 2026-09-07: File ▸ Close Workspace Window left a floated Restore Points
    /// panel on screen still listing the closed workspace. Closing the WORKSPACE goes through
    /// <c>ResetToBlankShell</c>, which now empties both panels — and a floated one is the same tool
    /// instance, so it empties too. Closing the WINDOW goes through neither: the window is destroyed
    /// and every float it owned is orphaned, with live buttons behind a view model whose window has
    /// gone.</para>
    ///
    /// <para>Source-scanned: the property is that the window's own <c>OnClosed</c> reaches the sweep,
    /// and driving a real multi-window float teardown needs a display this project's tests do not
    /// have.</para>
    /// </summary>
    [Fact]
    public void ClosingTheWorkspaceWindowTakesItsFloatingPanelsWithIt()
    {
        string window = Strip(File.ReadAllText(SourcePath("Views/WorkspaceWindow.axaml.cs")));

        int closed = window.IndexOf("protected override void OnClosed(", StringComparison.Ordinal);
        Assert.True(closed >= 0, "WorkspaceWindow.OnClosed has moved; this gate must follow it.");

        string body = window[closed..window.IndexOf("\n    }", closed, StringComparison.Ordinal)];
        Assert.Contains("CloseFloatingToolWindows()", body, StringComparison.Ordinal);

        // NOT from OnCleanExit. Quit asks every window before closing any of them, so a cancel at the
        // second leaves the first open — and OnCleanExit has already run on it. Closing its panels
        // there would take them from a window the user had just chosen to keep.
        string vm = Strip(File.ReadAllText(SourcePath("ViewModels/WorkspaceViewModel.cs")));
        int clean = vm.IndexOf("public void OnCleanExit()", StringComparison.Ordinal);
        Assert.True(clean >= 0, "OnCleanExit has been renamed; this gate must follow it.");

        string cleanBody = vm[clean..vm.IndexOf("\n    }", clean, StringComparison.Ordinal)];
        Assert.DoesNotContain("CloseFloatingToolWindows", cleanBody, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Tool panels only.</b> What happens to a torn-off DOCUMENT when its window closes is a
    /// separate, already-settled question (R-fgn-1/-2), and answering it as a side effect of this fix
    /// would change an owner decision nobody asked about.
    /// </summary>
    [Fact]
    public void TheSweepLeavesTornOffDocumentWindowsAlone()
    {
        string source = Strip(File.ReadAllText(SourcePath("ViewModels/WorkspaceViewModel.Docking.cs")));

        int start = source.IndexOf("internal void CloseFloatingToolWindows()", StringComparison.Ordinal);
        Assert.True(start >= 0, "CloseFloatingToolWindows has been renamed or removed.");

        string body = source[start..source.IndexOf("\n    }\n", start, StringComparison.Ordinal)];

        Assert.Contains("WindowFloatsATool(window)",        body, StringComparison.Ordinal);
        Assert.Contains("FindAnyDocumentInWindow(window)",  body, StringComparison.Ordinal);
        // MW1 R-mw1-11: every workspace view model scans the whole process's window list, so without
        // the ownership check each would close the OTHER window's panels.
        Assert.Contains("OwningWorkspace", body, StringComparison.Ordinal);
    }

    // ══ 6. The toolbar's two buttons ══════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The three conditions the owner named</b> (2026-09-07): a workspace is open, there is a usable
    /// git, and <i>keep a history</i> is on for this workspace. The buttons are absent otherwise —
    /// R-rc3-3's silence, on the toolbar.
    /// </summary>
    [GitFact]
    public void TheToolbarButtonsAppearOnlyWhereAHistoryIsBeingKept()
    {
        using var state = new AppDataRootScope();
        using var ws    = new GitWorkspace();

        // No workspace at all — the blank shell, and the state the owner's close left behind.
        Assert.False(WorkspaceHistoryService.KeepingHistoryHere(null));
        Assert.False(WorkspaceHistoryService.KeepingHistoryHere(""));
        Assert.False(WorkspaceHistoryService.KeepingHistoryHere(
            Path.Combine(ws.Root, "no-such-folder")));

        // A workspace, git present, and the preference at its shipped default: on.
        Assert.True(WorkspaceHistoryService.KeepingHistoryHere(ws.Root));

        // Switched off for THIS workspace: off, whatever the per-user preference says.
        RevisionSwitch.TurnOff(ws.Root, keepHistoryPreference: true);
        Assert.False(WorkspaceHistoryService.KeepingHistoryHere(ws.Root));

        RevisionSwitch.TurnOn(ws.Root, keepHistoryPreference: true);
        Assert.True(WorkspaceHistoryService.KeepingHistoryHere(ws.Root));
    }

    /// <summary>
    /// <b>It must not run git.</b> This is re-read on every window activation, so a subprocess here
    /// would be paid on every alt-tab for the life of the session. Source-scanned because the cost is
    /// the property, and a timing assertion would measure the machine.
    /// </summary>
    [Fact]
    public void TheToolbarVisibilityCheckRunsNoGit()
    {
        string source = Strip(File.ReadAllText(SourcePath("Revision/WorkspaceHistoryService.cs")));

        int start = source.IndexOf("public static bool KeepingHistoryHere", StringComparison.Ordinal);
        Assert.True(start >= 0, "KeepingHistoryHere has been renamed; this gate must follow it.");

        int end  = source.IndexOf("\n    }", start, StringComparison.Ordinal);
        string body = source[start..end];

        Assert.DoesNotContain("GitCommand.For",             body, StringComparison.Ordinal);
        Assert.DoesNotContain("Situation(",                 body, StringComparison.Ordinal);
        Assert.DoesNotContain("EnclosingRepository",        body, StringComparison.Ordinal);
        Assert.DoesNotContain(".Run(",                      body, StringComparison.Ordinal);
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────

    private static RestorePoint Point(long sequence, bool kept) => new(
        Sequence:  sequence,
        CommitId:  new string('a', 40),
        TreeId:    new string('b', 40),
        Reference: $"refs/circuitrf/restore/{sequence}",
        TakenUtc:  DateTimeOffset.UnixEpoch.AddDays(sequence),
        Origin:    CheckpointOrigin.SavePoint,
        Intent:    $"entry {sequence}",
        Label:     $"entry {sequence}",
        Kept:      kept,
        LeftOut:   []);

    /// <summary>A file under <c>src/Ui</c>, found from this assembly rather than from a build-time
    /// constant, so the gate still works from any working directory.</summary>
    private static string SourcePath(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Ui")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "Ui",
                            relative.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>
    /// Comments out. Every source-scan gate in this repository strips them first: these files explain
    /// themselves at length, and a gate that matched prose would pass on a call that had been described
    /// and deleted.
    /// </summary>
    private static string Strip(string source)
    {
        source = Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline);
        return Regex.Replace(source, @"^[ \t]*//.*$", "", RegexOptions.Multiline);
    }

    private sealed class RecordingSink : IMessageSink
    {
        public List<string> Texts { get; } = [];
        public void Post(MessageLevel level, string text, string? filePath = null) => Texts.Add(text);
        public void Clear() => Texts.Clear();
    }
}

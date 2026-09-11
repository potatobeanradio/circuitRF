using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CircuitRF.Design.Revision;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui.Docking;
using CircuitRF.Ui.Messages;
using CircuitRF.Ui.Revision;
using CircuitRF.Ui.ViewModels.Dock;
using Dock.Model.Core;
using Dock.Model.Controls;
using Xunit;

namespace CircuitRF.Ui.Tests.Revision;

/// <summary>
/// RC-10's gates — <c>docs/sonnet-briefs/brief-revision-control-10-one-history-panel.md</c> §6,
/// <c>docs/design/revision-control.md</c> §5.10.
///
/// <para><b>Every fixture path here is the SHAPE of a path, never a real one.</b> A workspace name
/// from a real machine must not reach this repository.</para>
///
/// <para>In <see cref="AppDataRootCollection"/> for the reason RC-3's, RC-5's, RC-6's and RC-7's own
/// gates are: the identity path redirects the per-user state directory, and the git-environment
/// isolation these tests install is process-wide, so two collections that both touch a process global
/// still clobber each other.</para>
/// </summary>
[Collection(AppDataRootCollection.Name)]
public class OneHistoryPanelTests
{
    // ══ 1. One panel, and both retired ids reach it (R-rc10-3) ════════════════════════════════════

    /// <summary>
    /// <b>A saved layout naming a panel that no longer exists opens correctly, and one naming BOTH
    /// collapses to a single instance.</b>
    ///
    /// <para><c>RestorePoints</c> and <c>VersionHistory</c> are written into every <c>.cwsuser</c> in
    /// existence, so this is the one place the merge can break a workspace somebody already has. The
    /// third case is the one that needed a type rather than a rename: the builder maps an id to a
    /// single tool INSTANCE, so two surviving entries would put one dockable into two docks — which is
    /// a tree the docking library has no correct behaviour for.</para>
    ///
    /// <para>Asserted on the restored dock tree, never on a screenshot.</para>
    /// </summary>
    [Theory]
    [InlineData("RestorePoints")]
    [InlineData("VersionHistory")]
    public void ALayoutNamingOneRetiredPanelOpensTheMergedOne(string retired)
    {
        var factory = new CircuitRfDockFactory();
        var root    = factory.CreateLayoutFromState(new CwsDockLayout
        {
            Sides  = [new CwsDockSide { Side = DockSide.Right, Proportion = 0.25 }],
            Panels = [new CwsDockPanel { Id = retired, Side = DockSide.Right, Group = 0, Order = 0,
                                         Active = true, Proportion = 1.0 }],
        });

        var found = ToolsIn(root).Where(t => t is HistoryTool).ToList();
        Assert.Single(found);
        Assert.Equal(DockPanelIds.History, found[0].Id);

        // And nothing under the retired name survives anywhere in the tree.
        Assert.DoesNotContain(ToolsIn(root), t => t.Id is "RestorePoints" or "VersionHistory");
    }

    /// <summary>
    /// <b>Both at once collapses to one instance</b> — not two tabs of the same panel, and not an
    /// empty pane. The panel lands where the FIRST of the two was, deliberately: a designer with both
    /// open is most likely looking at whichever is in front, and a fixed preference for one of the two
    /// ids would move the panel for half of them for no reason anybody could see.
    /// </summary>
    [Fact]
    public void ALayoutNamingBothRetiredPanelsCollapsesToOneInstance()
    {
        var factory = new CircuitRfDockFactory();
        var root    = factory.CreateLayoutFromState(new CwsDockLayout
        {
            Sides = [new CwsDockSide { Side = DockSide.Right, Proportion = 0.25 }],
            Panels =
            [
                new CwsDockPanel { Id = "RestorePoints",  Side = DockSide.Right,  Group = 0, Order = 0, Active = true,  Proportion = 1.0 },
                new CwsDockPanel { Id = "VersionHistory", Side = DockSide.Bottom, Group = 0, Order = 0, Active = false, Proportion = 0.3 },
            ],
        });

        var tools = ToolsIn(root).ToList();
        Assert.Single(tools, t => t is HistoryTool);

        // Not an empty pane either: every tool dock in the tree has something in it.
        foreach (var dock in DocksIn(root).OfType<Dock.Model.Core.IDock>())
            if (dock.VisibleDockables is { } children && dock.Id.Contains("ToolDock", StringComparison.Ordinal))
                Assert.NotEmpty(children);
    }

    /// <summary>
    /// <b>Both retired ids resolve, and neither is offered to anything that writes a layout.</b>
    /// Capture filters on <see cref="DockPanelIds.All"/>, so a file saved after this build names only
    /// the merged panel and the mapping exists for old files alone.
    /// </summary>
    [Fact]
    public void TheRetiredIdsResolveAndAreNotInTheWritableSet()
    {
        Assert.Equal(DockPanelIds.History, DockPanelIds.Resolve("RestorePoints"));
        Assert.Equal(DockPanelIds.History, DockPanelIds.Resolve("VersionHistory"));
        Assert.Equal(DockPanelIds.History, DockPanelIds.Resolve(DockPanelIds.History));

        Assert.Contains(DockPanelIds.History, DockPanelIds.All);
        Assert.DoesNotContain("RestorePoints",  DockPanelIds.All);
        Assert.DoesNotContain("VersionHistory", DockPanelIds.All);

        // The View menu, the toolbar and the panel toggle each name it ONCE, and the retired names
        // appear in no user-facing surface at all.
        string window = RestorePointsTests.ReadSource("src/Ui/Views/WorkspaceWindow.axaml");
        Assert.DoesNotContain("CommandParameter=\"RestorePoints\"",  window, StringComparison.Ordinal);
        Assert.DoesNotContain("CommandParameter=\"VersionHistory\"", window, StringComparison.Ordinal);
        Assert.Contains("CommandParameter=\"History\"", window, StringComparison.Ordinal);
    }

    // ══ 2. Both kinds in one list, and one mark is the only difference (R-rc10-2) ═════════════════

    /// <summary>
    /// <b>One ordered list, with the version rows marked and no other row marked.</b>
    ///
    /// <para>The mark says <i>titled, permanent, travels</i> — three promises the unmarked rows do not
    /// make. It is not an importance badge, and there is deliberately no second colour scheme: a
    /// second one is noise on a list whose entire problem is noise.</para>
    /// </summary>
    [GitFact]
    public void BothKindsAppearInOneListAndOnlyTheVersionsCarryTheMark()
    {
        using var ws = Armed();
        var git = ws.Git();

        ws.Write("cells/a/thing.csch", "v1");
        Assert.True(WorkspaceCheckpoints.Take(git, CheckpointOrigin.SavePoint, "the working match").Recorded);

        ws.Write("cells/a/thing.csch", "v2");
        Assert.True(WorkspaceCommit.Commit(git, "Output match retuned").Ok);

        var result = HistoryList.Read(git, HistoryFilter.Default);

        Assert.Contains(result.Rows, r => r.IsVersion);
        Assert.Contains(result.Rows, r => r.IsPoint);

        var tool = new HistoryTool();
        tool.SetRows(result, hasWorkspace: true);

        foreach (var row in tool.Rows)
            Assert.Equal(row.IsVersion, row.HasVersionMark);

        Assert.Single(tool.Rows, r => r.HasVersionMark);
        Assert.Contains(tool.Rows, r => r is { IsPoint: true, HasVersionMark: false });

        // Newest first, over both kinds — the merge is an ordering, not two lists stacked.
        for (int i = 1; i < result.Rows.Count; i++)
            Assert.True(result.Rows[i - 1].WhenUtc >= result.Rows[i].WhenUtc);
    }

    // ══ 3. The default view hides the close entries and nothing else (R-rc10-5) ═══════════════════

    /// <summary>
    /// <b>Every entry somebody stated an intent for is shown; the workspace-close one is not, and one
    /// toggle reveals it.</b>
    ///
    /// <para>All six origins plus a version, so the assertion is about the whole enum rather than the
    /// two obvious members. The three with no checkbox of their own — before going back, and the two
    /// ends of an off period — are always shown: none of them is noise, and a checkbox nobody would
    /// think to tick is how R-rc10-20's promise leaves the place the promise was made.</para>
    /// </summary>
    [GitFact]
    public void TheDefaultViewHidesTheCloseEntryAndNothingElse()
    {
        using var ws = Armed();
        var git = ws.Git();

        Write(ws, git, CheckpointOrigin.SavePoint,       "the working match");
        Write(ws, git, CheckpointOrigin.BeforeBatch,     "widen the output match");
        Write(ws, git, CheckpointOrigin.BeforeRestore,   null);
        Write(ws, git, CheckpointOrigin.RecordingOff,    null);
        Write(ws, git, CheckpointOrigin.RecordingOn,     null);
        Write(ws, git, CheckpointOrigin.WorkspaceClosed, null);

        ws.Write("cells/a/thing.csch", "for a version");
        Assert.True(WorkspaceCommit.Commit(git, "Output match retuned").Ok);

        var shown = HistoryList.Read(git, HistoryFilter.Default).Rows;

        Assert.Contains(shown, r => r.IsVersion);
        foreach (var origin in new[] { CheckpointOrigin.SavePoint, CheckpointOrigin.BeforeBatch,
                                       CheckpointOrigin.BeforeRestore, CheckpointOrigin.RecordingOff,
                                       CheckpointOrigin.RecordingOn })
            Assert.Contains(shown, r => r.Point?.Origin == origin);

        Assert.DoesNotContain(shown, r => r.Point?.Origin == CheckpointOrigin.WorkspaceClosed);

        // One toggle, and it is the only one that changes the answer.
        var revealed = HistoryList.Read(git, HistoryFilter.Default with { Automatic = true }).Rows;
        Assert.Contains(revealed, r => r.Point?.Origin == CheckpointOrigin.WorkspaceClosed);
        Assert.Equal(shown.Count + 1, revealed.Count);
    }

    /// <summary>
    /// R-rc10-5's own completion question. <b>A workspace whose only history is close entries opens on
    /// an empty list</b> — and it is exactly the workspace §1 is written for, since its owner never
    /// thought about history at all. The empty line therefore counts them and names the control that
    /// reveals them, rather than claiming nothing has been kept.
    /// </summary>
    [Fact]
    public void AWorkspaceWithOnlyCloseEntriesSaysSoRatherThanClaimingNothingWasKept()
    {
        var tool = new HistoryTool();
        tool.SetRows(new HistoryList.Result([], 0), hasWorkspace: true, hiddenAutomatic: 3);

        Assert.Empty(tool.Rows);
        Assert.Equal(HistoryMessages.OnlyAutomaticEntries(3), tool.EmptyText);
        Assert.Contains("3 entries", tool.EmptyText, StringComparison.Ordinal);
        Assert.DoesNotContain("Nothing has been kept", tool.EmptyText, StringComparison.Ordinal);
    }

    // ══ 4. The close checkpoint is still taken (R-rc10-6) ═════════════════════════════════════════

    /// <summary>
    /// <b>Quietening a list is a display decision and must not become a capture decision.</b>
    ///
    /// <para>§5.3's argument for the close boundary is the strongest sentence in that section — it is
    /// the one boundary that reliably exists in every session, including the ones where the designer
    /// never thought about history, which is precisely the designer §1 is written for. Stopping the
    /// capture to quieten a list would remove the safety net from exactly the population it exists
    /// for.</para>
    /// </summary>
    [GitFact]
    public void TheCloseCheckpointIsStillTakenUnderTheDefaultFilter()
    {
        using var state = new AppDataRootScope();
        using var ws    = Armed();
        var git = ws.Git();

        var service = new WorkspaceHistoryService(new SilentSink());
        service.NoteWorkspaceWrite();

        ws.Write("cells/a/thing.csch", "v1");
        Assert.True(service.TakeCloseCheckpoint(ws.Root));

        // On its own reference, exactly as RC-5 wrote it — the default filter hides the ROW and
        // touches nothing underneath.
        Assert.Contains(CheckpointReferences.List(git),
                        r => RestorePoints.List(git).Any(
                                 p => p.Reference == r.Reference
                                   && p.Origin    == CheckpointOrigin.WorkspaceClosed));

        Assert.DoesNotContain(HistoryList.Read(git, HistoryFilter.Default).Rows,
                              r => r.Point?.Origin == CheckpointOrigin.WorkspaceClosed);
    }

    /// <summary>
    /// The other half, source-scanned: <b>this brief's paths contain no change to the close
    /// boundary.</b> A filter that quietly became a capture decision would pass the test above on the
    /// day it landed and fail nobody afterwards.
    /// </summary>
    [Fact]
    public void NothingInThisBriefTouchesTheCloseBoundary()
    {
        foreach (string path in RcTenSources())
        {
            string source = RestorePointsTests.StripComments(RestorePointsTests.ReadSource(path));

            // Nothing here TAKES a boundary. Naming the origin is what a row classifier does and is
            // exactly the change this brief is allowed to make; calling the boundary is not.
            Assert.DoesNotContain("TakeCloseCheckpoint",       source, StringComparison.Ordinal);
            Assert.DoesNotContain("WorkspaceCheckpoints.Take", source, StringComparison.Ordinal);
            Assert.DoesNotContain("WorkspaceArming.Arm",       source, StringComparison.Ordinal);
        }

        // And the close path itself is untouched — it still reaches the one boundary function, from
        // the one place that has ever called it.
        string close = RestorePointsTests.StripComments(
            RestorePointsTests.ReadSource("src/Ui/ViewModels/WorkspaceViewModel.Revision.cs"));
        Assert.Contains("History.TakeCloseCheckpoint(root)", close, StringComparison.Ordinal);
    }

    // ══ 5. The filter is per-user view state (R-rc10-8) ═══════════════════════════════════════════

    /// <summary>
    /// <b>The filter and the search round-trip through the <c>.cwsuser</c>, and appear in no
    /// preference file.</b>
    ///
    /// <para>Every row on §10A's Settings tab changes what is KEPT. A filter a designer flips while
    /// hunting for something is not a preference about what exists, and putting it there is the
    /// category error that tab is most exposed to.</para>
    /// </summary>
    [Fact]
    public void TheFilterRoundTripsThroughTheUserFileAndIsInNoPreference()
    {
        string dir = Path.Combine(Path.GetTempPath(), "crf-rc10-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(dir);
        try
        {
            string cws = Path.Combine(dir, "shape-of-a-name.cws");

            var file = new CwsFile
            {
                HistoryFilter = new CwsHistoryFilter
                {
                    Versions = true, SavePoints = false, AiBatches = true,
                    Automatic = true, TidiedAway = false, Search = "match network",
                },
            };
            WorkspacePersistence.SaveToFileAtomic(cws, file);

            // In the SIDECAR, and nowhere in the versioned document.
            string sidecar = File.ReadAllText(WorkspaceUserPersistence.PathFor(cws));
            Assert.Contains("match network", sidecar, StringComparison.Ordinal);
            Assert.DoesNotContain("match network", File.ReadAllText(cws), StringComparison.Ordinal);
            Assert.DoesNotContain("HistoryFilter",  File.ReadAllText(cws), StringComparison.Ordinal);

            var read = WorkspacePersistence.LoadFromFile(cws).HistoryFilter;
            Assert.NotNull(read);
            Assert.True(read!.Versions);
            Assert.False(read.SavePoints);
            Assert.True(read.Automatic);
            Assert.False(read.TidiedAway);
            Assert.Equal("match network", read.Search);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }

        // And no preference carries it: the Settings tab's rows are all about what is KEPT.
        string prefs = RestorePointsTests.StripComments(
            RestorePointsTests.ReadSource("src/Ui/Theming/AppPreferences.cs"));
        Assert.DoesNotContain("HistoryFilter", prefs, StringComparison.Ordinal);

        string tab = RestorePointsTests.StripComments(
            RestorePointsTests.ReadSource("src/Ui/Views/Dialogs/RevisionControlSettingsView.axaml.cs"));
        Assert.DoesNotContain("HistoryFilter", tab, StringComparison.Ordinal);
    }

    /// <summary>
    /// The panel's own half: <b>the stored view is applied without being treated as a change</b>, and a
    /// change to it raises exactly one notification for the host to act on.
    /// </summary>
    [Fact]
    public void ApplyingAStoredFilterIsNotItselfAChange()
    {
        var tool  = new HistoryTool();
        int fired = 0;
        tool.FilterChanged += () => fired++;

        tool.ApplyStoredFilter(new HistoryFilter(true, false, true, true, false, "match"));
        Assert.Equal(0, fired);
        Assert.False(tool.ShowSavePoints);
        Assert.True(tool.ShowAutomatic);
        Assert.True(tool.IsSearchOpen);           // a stored search opens the field it belongs to
        Assert.Equal("match", tool.SearchText);

        tool.ShowAutomatic = false;
        Assert.Equal(1, fired);

        tool.ShowAutomatic = false;               // no change is no event
        Assert.Equal(1, fired);
    }

    // ══ 6. Search, and the incomplete answer (R-rc10-9, R-rc10-10) ═══════════════════════════════

    /// <summary>
    /// <b>Search is over what a person wrote</b> — titles, batch intents and the author — <b>and an
    /// answer that is incomplete says so on a line of its own.</b>
    ///
    /// <para>RC-6's journal already knows the entries retention thinned, so returning fewer results
    /// than exist would be a wrong answer rather than a short one, and the designer would have no way
    /// to tell the two apart.</para>
    /// </summary>
    [GitFact]
    public void SearchMatchesWhatAPersonWroteAndReportsTheThinnedOnesSeparately()
    {
        using var ws = Armed();
        var git = ws.Git();

        Write(ws, git, CheckpointOrigin.SavePoint,   "the output match network");
        Write(ws, git, CheckpointOrigin.BeforeBatch, "widen the match network");
        Write(ws, git, CheckpointOrigin.SavePoint,   "something else entirely");

        ws.Write("cells/a/thing.csch", "for a version");
        Assert.True(WorkspaceCommit.Commit(git, "Bias tee reworked").Ok);

        // The title.
        var byTitle = HistoryList.Read(git, HistoryFilter.Default with { Search = "match network" }).Rows;
        Assert.Equal(2, byTitle.Count(r => !r.IsGap));
        Assert.DoesNotContain(byTitle, r => r.Title.Contains("entirely", StringComparison.Ordinal));

        // The batch's own stated intent, which is not the same string as its label.
        var byIntent = HistoryList.Read(git, HistoryFilter.Default with { Search = "widen" }).Rows;
        Assert.Single(byIntent.Where(r => !r.IsGap));

        // The author — the one field a version has and a restore point does not.
        var byAuthor = HistoryList.Read(git, HistoryFilter.Default with { Search = "A Designer" }).Rows;
        Assert.Single(byAuthor.Where(r => !r.IsGap));
        Assert.True(byAuthor.Single(r => !r.IsGap).IsVersion);

        // ── The thinned entry, and the line that stops the answer being wrong ─────────────────────
        var target = RestorePoints.List(git).First(p => p.Label.Contains("output match", StringComparison.Ordinal));
        Assert.Equal(0, ws.Raw("update-ref", "-d", target.Reference).Code);
        Assert.True(ThinningJournal.Append(ws.Root,
            new ThinnedState(target.CommitId, DateTimeOffset.UtcNow,
                             target.Reference, target.Sequence, target.Label)));

        // With "tidied away" ON — the panel's default — the entry is in the list, marked, and the
        // answer is complete, so there is no line.
        var complete = HistoryList.Read(git, HistoryFilter.Default with { Search = "output match" });
        Assert.Contains(complete.Rows, r => r.Thinned);
        Assert.Equal(0, complete.ThinnedMatchesNotShown);

        // Switched off, the answer is INCOMPLETE and says so with a count, rather than silently
        // returning fewer results than exist.
        var incomplete = HistoryList.Read(git, HistoryFilter.Default with
        {
            Search = "output match", TidiedAway = false,
        });
        Assert.DoesNotContain(incomplete.Rows, r => r.Thinned);
        Assert.Equal(1, incomplete.ThinnedMatchesNotShown);

        var tool = new HistoryTool();
        tool.SetRows(incomplete, hasWorkspace: true);
        Assert.True(tool.HasThinnedMatches);
        Assert.Contains("tidied away", tool.ThinnedMatchText, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Escape closes the search field, and closing it clears the query</b> — reported as a bug:
    /// pressing Escape with the caret in the History panel's search box did nothing at all.
    ///
    /// <para>The clear lives on <c>IsSearchOpen</c> rather than in <c>ToggleSearch</c> because the
    /// magnifier is no longer the only way the field is put away. A filter still applied with nothing
    /// on screen to say so is the worst state this panel can be in: rows are hidden and the affordance
    /// that would explain it has just gone.</para>
    ///
    /// <para><c>handledEventsToo: true</c> is LOAD-BEARING and its absence is a silent no-op — which is
    /// exactly how the project tree's own Escape shipped broken once. WorkspaceWindow.axaml binds
    /// Escape to DisarmPlacementCommand, and Window.KeyBindings are processed before visual-tree
    /// routing and always mark the event Handled, so a handler that skips handled events never sees
    /// Escape at all.</para>
    /// </summary>
    [Fact]
    public void EscapeClosesTheSearchField_AndClosingItClearsTheQuery()
    {
        var tool = new HistoryTool();

        tool.ToggleSearch();
        tool.SearchText = "match network";
        Assert.True(tool.IsSearchOpen);

        // What the view's Escape handler does.
        tool.IsSearchOpen = false;

        Assert.Equal("", tool.SearchText);
        Assert.False(tool.HasSearchText);

        // The magnifier's own close goes through the same door, so the two cannot diverge.
        tool.ToggleSearch();
        tool.SearchText = "match network";
        tool.ToggleSearch();
        Assert.False(tool.IsSearchOpen);
        Assert.Equal("", tool.SearchText);
    }

    [Fact]
    public void TheViewRoutesEscapeToClosingTheSearch_AndClaimsTheAlreadyHandledKey()
    {
        string cs = RestorePointsTests.ReadSource("src/Ui/Views/Revision/HistoryToolView.axaml.cs");
        Assert.Contains("Key.Escape", cs, StringComparison.Ordinal);
        Assert.Contains("tool.IsSearchOpen = false", cs, StringComparison.Ordinal);
        Assert.Contains(
            "AddHandler(KeyDownEvent, OnSearchKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true)",
            cs, StringComparison.Ordinal);

        // The window binding this has to out-rank. If it is ever removed, the argument above stops
        // applying and this test should be revisited rather than silently kept.
        string window = RestorePointsTests.ReadSource("src/Ui/Views/WorkspaceWindow.axaml");
        Assert.Contains("Escape", window, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The panel's callbacks are wired PER INSTANCE, not once per session.</b>
    ///
    /// <para>Reported as three separate bugs — "come forward again" did nothing, "compare with the
    /// workspace as it is now" did nothing, and "copy the identifier" left the clipboard holding what
    /// it held before — which were one defect. A workspace switch goes through
    /// <c>CircuitRfDockFactory.CreateDefaultLayout</c>, which builds a FRESH <c>HistoryTool</c>, and
    /// the restore performed by going back IS such a switch. A session-wide <c>bool</c> guard stayed
    /// true across the replacement, so every callback on the new panel was left null while its rows
    /// and its way-forward line were filled in as usual: each button drew, and did nothing.</para>
    ///
    /// <para>Comparing the instance is what makes this correct for the paths that do not exist yet —
    /// any future caller replacing the tools is covered without having to remember a flag.</para>
    /// </summary>
    [Fact]
    public void TheHistoryPanelIsWiredPerInstance_SoAWorkspaceSwitchDoesNotLeaveADeadPanel()
    {
        string cs = RestorePointsTests.StripComments(
            RestorePointsTests.ReadSource("src/Ui/ViewModels/WorkspaceViewModel.Revision.cs"));

        // The guard is the identity of the tool, not a bool that a replacement cannot clear.
        Assert.Contains("ReferenceEquals(_wiredHistoryTool, tool)", cs, StringComparison.Ordinal);
        Assert.DoesNotContain("_historyPanelWired", cs, StringComparison.Ordinal);

        // And the three callbacks the reports named are still attached inside that block.
        Assert.Contains("ComeForwardRequested", cs, StringComparison.Ordinal);
        Assert.Contains("CompareRequested", cs, StringComparison.Ordinal);
        Assert.Contains("CopyIdentityRequested", cs, StringComparison.Ordinal);

        // The factory really does hand back a new instance on the path a restore takes, which is what
        // makes the paragraph above a defect rather than a precaution.
        string factory = RestorePointsTests.StripComments(
            RestorePointsTests.ReadSource("src/Ui/ViewModels/Dock/CircuitRfDockFactory.cs"));
        Assert.Contains("HistoryTool = new HistoryTool();", factory, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>A comparison that finds nothing says so.</b> An empty list under a changed heading is
    /// indistinguishable from a menu item that did nothing at all — which is exactly how the dead
    /// panel above was reported. The line appears ONLY for an explicit comparison: a restore point
    /// selected in the ordinary way has no per-version change list, and calling that "no differences"
    /// would answer a question nobody asked.
    /// </summary>
    [Fact]
    public void AComparisonThatFindsNothingSaysSo_AndOnlyForAnExplicitComparison()
    {
        var tool = new HistoryTool();

        // The panel's own default: a selection, no comparison run. Empty says nothing.
        tool.SetChanges([]);
        Assert.False(tool.NothingDiffers);

        var version = new HistoryVersion("0123456789abcdef0123", "tree", DateTimeOffset.UtcNow,
                                         "Output match retuned", true, null, "A Designer");
        var entry   = new HistoryEntry(HistoryEntryKind.Version, version, null, null, version.WhenUtc);
        tool.SetRows(new HistoryList.Result([entry], 0), hasWorkspace: true);
        tool.Selected = tool.Rows[0];

        HistoryEntry? asked = null;
        tool.CompareRequested = e => { asked = e; tool.SetChanges([]); };

        tool.CompareWithWorkspace();

        Assert.NotNull(asked);
        Assert.True(tool.NothingDiffers);
        Assert.Contains("workspace", tool.ComparisonTitle, StringComparison.OrdinalIgnoreCase);

        // Selecting again is a new question, so the answer to the old one is withdrawn.
        tool.Selected = null;
        Assert.False(tool.NothingDiffers);
    }

    /// <summary>
    /// <b>Only what a person wrote is quoted, and circuitRF's own wording is rendered afresh</b>
    /// (owner, 2026-09-07).
    ///
    /// <para>Reported together: an entry named "before going back", carrying a menu item that read
    /// "Go back to 'before going back'". Two defects in one row. The name described a MOMENT and not a
    /// thing, so the list read as an instruction; and quotation marks say <i>these are your words</i>,
    /// which on a generated label is untrue.</para>
    ///
    /// <para>The re-render is what carries the reword to workspaces that already exist. A rename
    /// writes the corrected words to BOTH the subject and the intent — deliberately, and this is what
    /// that buys — so "a person wrote this" stays answerable from the entry alone and a renamed entry
    /// is never re-rendered over.</para>
    /// </summary>
    [Fact]
    public void OnlyWhatAPersonWroteIsQuoted_AndGeneratedLabelsAreRenderedAfresh()
    {
        var now = DateTimeOffset.UtcNow;

        RestorePoint Point(CheckpointOrigin origin, string label, string? intent) =>
            new("refs/x", "c0", "t0", 1, now, origin, label, intent, false, []);

        // The entry a restore takes of the state it is about to replace. Nobody wrote its label.
        var before = new HistoryEntry(
            HistoryEntryKind.RestorePoint, null,
            Point(CheckpointOrigin.BeforeRestore, "your work before you went back", null), null, now);

        var beforeRow = new HistoryRowItem(before, now, showAuthor: false);

        Assert.False(beforeRow.HasWrittenTitle);

        // NOTHING IS QUOTED, and since 2026-09-08 the destination is named by its IDENTITY rather than
        // by "this state" — which read the same on every row, on exactly the rows whose generated labels
        // already read alike. See TheGoBackButtonNamesItsDestinationByIdentityWhenNobodyTitledIt.
        Assert.Equal("Go to c0", beforeRow.GoBackText);
        Assert.DoesNotContain("\u201C", beforeRow.GoBackText);

        // The stored label is the OLD wording — this entry is one a designer already has — and the row
        // shows the current wording anyway.
        Assert.Equal("before going back", beforeRow.Title);
        Assert.Equal("before going back",
                     CheckpointMessage.SubjectFor(CheckpointOrigin.BeforeRestore, null));

        // A save-point the designer named is theirs, is quoted, and is never re-rendered.
        var named = new HistoryEntry(
            HistoryEntryKind.RestorePoint, null,
            Point(CheckpointOrigin.SavePoint, "match retuned", "match retuned"), null, now);

        var namedRow = new HistoryRowItem(named, now, showAuthor: false);
        Assert.True(namedRow.HasWrittenTitle);
        Assert.Equal("match retuned", namedRow.Title);
        Assert.Contains("match retuned", namedRow.GoBackText, StringComparison.Ordinal);

        // A RENAMED entry of an automatic origin: the rename wrote both halves, so the words survive.
        var renamed = new HistoryEntry(
            HistoryEntryKind.RestorePoint, null,
            Point(CheckpointOrigin.WorkspaceClosed, "friday, before the demo", "friday, before the demo"),
            null, now);

        var renamedRow = new HistoryRowItem(renamed, now, showAuthor: false);
        Assert.True(renamedRow.HasWrittenTitle);
        Assert.Equal("friday, before the demo", renamedRow.Title);

        // A version is always somebody's words.
        var version = new HistoryVersion("0123456789abcdef0123", "tree", now, "Output match retuned",
                                         true, null, "A Designer");
        var versionRow = new HistoryRowItem(
            new HistoryEntry(HistoryEntryKind.Version, version, null, null, now), now, showAuthor: false);

        Assert.True(versionRow.HasWrittenTitle);
        Assert.Contains("Output match retuned", versionRow.GoBackText, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>A corrected title does not survive in the copy bound for the next commit</b> (owner,
    /// 2026-09-07).
    ///
    /// <para>Reported: correct a version's title, edit on, press Keep This Version — and the dialog
    /// offers "this version was brought back from '&lt;the wording you deleted&gt;'", then writes it
    /// into the new commit, where it is permanent. The label in <c>restored-from.json</c> is a COPY
    /// taken at restore time, and a copy nothing updates is a copy that outlives its original.</para>
    ///
    /// <para><b>Matched on identity, never on the text.</b> Matching the old label would rewrite an
    /// unrelated entry that happened to share a title, and titles collide constantly — <i>save-point</i>,
    /// <i>workspace closed</i>. A file predating the identity field names nothing and is left alone,
    /// which is the safe direction to fail in.</para>
    /// </summary>
    [Fact]
    public void ACorrectedTitleDoesNotSurviveInThePendingRestoreProvenance()
    {
        string dir = Path.Combine(Path.GetTempPath(), "crf-prov-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            RestoreProvenance.Write(dir, new RestoredState("the careless title", DateTimeOffset.UtcNow, "aaa"));

            // The entry it names, corrected — the wording follows and the link is re-pointed.
            Assert.True(RestoreProvenance.Retitle(dir, "aaa", "bbb", "Output match retuned"));

            var after = RestoreProvenance.Read(dir);
            Assert.NotNull(after);
            Assert.Equal("Output match retuned", after!.Label);
            Assert.Equal("bbb", after.CommitId);

            // A second correction still finds it, which is the whole point of re-pointing.
            Assert.True(RestoreProvenance.Retitle(dir, "bbb", "ccc", "Output match retuned for 3.5 GHz"));
            Assert.Equal("Output match retuned for 3.5 GHz", RestoreProvenance.Read(dir)!.Label);

            // Some OTHER entry being corrected leaves this alone.
            Assert.False(RestoreProvenance.Retitle(dir, "zzz", "yyy", "something else entirely"));
            Assert.Equal("Output match retuned for 3.5 GHz", RestoreProvenance.Read(dir)!.Label);

            // A file written before the identity field names nothing, so nothing matches it — and an
            // empty id must never match an empty id, or every correction would rewrite it.
            RestoreProvenance.Write(dir, new RestoredState("older file", DateTimeOffset.UtcNow));
            Assert.False(RestoreProvenance.Retitle(dir, "", "bbb", "nope"));
            Assert.Equal("older file", RestoreProvenance.Read(dir)!.Label);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>
    /// The same leak in the panel's own sentence. R-rc10-18's line holds a COPY of the title it names,
    /// and it is displayed in the very panel a correction is made in — the most conspicuous place for
    /// deleted wording to reappear.
    /// </summary>
    [Fact]
    public void ACorrectedTitleDoesNotSurviveInTheWayForwardSentence()
    {
        var point = new RestorePoint("refs/x", "kept-1", "t0", 1, DateTimeOffset.UtcNow,
                                     CheckpointOrigin.BeforeRestore, "before going back",
                                     null, true, []);

        var way = new WayForward("the careless title", point, "aaa");

        var fixedUp = way.Retitled("aaa", "Output match retuned");
        Assert.Equal("Output match retuned", fixedUp.WentBackTo);

        // Some other entry: the same instance back, so a caller can tell nothing happened.
        Assert.Same(way, way.Retitled("zzz", "something else"));

        // Unknown identity matches nothing, including another unknown one.
        var unknown = new WayForward("t", point);
        Assert.Same(unknown, unknown.Retitled("", "x"));
    }

    /// <summary>
    /// <b>Both keep dialogs hand the keyboard back to this panel</b> (owner, 2026-09-07) — kept or
    /// cancelled alike. The gesture started in the panel, so that is where the next keystroke belongs;
    /// a cancelled dialog dropping focus onto the workspace was what got reported.
    ///
    /// <para>Only the PANEL's buttons come through this path. The File menu calls the same two dialogs
    /// directly and has no panel to go back to.</para>
    /// </summary>
    [Fact]
    public void BothKeepDialogsReturnFocusToThePanel()
    {
        var tool  = new HistoryTool();
        int asked = 0;
        tool.ActivationFocusRequested += () => asked++;

        tool.RequestActivationFocus();
        Assert.Equal(1, asked);

        // The pending half: a view that binds later consumes the request rather than losing it.
        Assert.True(tool.ConsumeActivationFocus());
        Assert.False(tool.ConsumeActivationFocus());

        string cs = RestorePointsTests.StripComments(
            RestorePointsTests.ReadSource("src/Ui/ViewModels/WorkspaceViewModel.Revision.cs"));
        Assert.Contains("ThenFocusThePanel(tool, KeepThisState)", cs, StringComparison.Ordinal);
        Assert.Contains("ThenFocusThePanel(tool, KeepThisVersion)", cs, StringComparison.Ordinal);
        Assert.Contains("tool.RequestActivationFocus();", cs, StringComparison.Ordinal);

        // Awaited, not fire-and-forget: focus must be asked for AFTER the dialog closes.
        Assert.Contains("await keep(", cs, StringComparison.Ordinal);

        string view = RestorePointsTests.StripComments(
            RestorePointsTests.ReadSource("src/Ui/Views/Revision/HistoryToolView.axaml.cs"));
        Assert.Contains("ActivationFocusRequested", view, StringComparison.Ordinal);
        Assert.Contains("ConsumeActivationFocus()", view, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>A multi-line <c>Text="…"</c> attribute keeps its own indentation as literal spaces.</b>
    ///
    /// <para>XML attribute-value normalisation turns the newline into a space and leaves every space of
    /// the continuation line's indentation in place — so a sentence wrapped in the source renders with
    /// a twenty-character gap in the middle of it. Reported against the Keep This Version dialog
    /// ("huge white space gap between the words 'you' and 'come'"); five places in the repo had it and
    /// all five were in this feature.</para>
    /// </summary>
    [Fact]
    public void NoDialogWrapsASentenceAcrossLinesInsideATextAttribute()
    {
        foreach (string path in (string[])
                 ["src/Ui/Views/Dialogs/KeepThisVersionDialog.axaml",
                  "src/Ui/Views/Dialogs/KeepThisStateDialog.axaml",
                  "src/Ui/Views/Revision/HistoryToolView.axaml"])
        {
            string axaml = RestorePointsTests.ReadSource(path);

            foreach (string line in axaml.ReplaceLineEndings("\n").Split('\n'))
            {
                int at = line.IndexOf("Text=\"", StringComparison.Ordinal);
                if (at < 0) continue;

                Assert.True(line.IndexOf('"', at + 6) > 0,
                            $"{path}: a Text=\"…\" attribute is left open at the end of a line, which "
                          + $"renders as a run of literal spaces mid-sentence: {line.Trim()}");
            }
        }
    }

    /// <summary>
    /// <b>Tidying an entry away redraws from what the panel holds; it does not re-read the
    /// repository</b> (owner, 2026-09-07: "Tidy this away blocks the UI thread momentarily").
    ///
    /// <para>Measured with a scratch harness on a sixty-entry workspace: the reference write is
    /// <b>18 ms</b> and the <c>ReadSources</c> that followed it is <b>180 ms</b>, growing with the
    /// list — so nine tenths of the stall was work that could not have changed anything. Neither
    /// tidying away nor bringing back touches the versions, the incoming set, the sharing set or the
    /// annotations; what changes is a boolean the panel already has.</para>
    ///
    /// <para><b>A transition, not a guess</b>: applied only after the write reported success, and the
    /// failing path still refreshes for real, because a panel disagreeing with the repository is the
    /// one that is wrong. Same rule RC-11 set for the filter checkboxes.</para>
    /// </summary>
    [Fact]
    public void TidyingAwayAndBringingBackRedrawWithoutReadingTheRepository()
    {
        var now = DateTimeOffset.UtcNow;

        RestorePoint Pt(string id, long seq) =>
            new("refs/" + id, id, "tree-" + id, seq, now, CheckpointOrigin.SavePoint,
                "save-point " + seq, "save-point " + seq, false, []);

        var sources = new HistorySources(
            [], [Pt("aaa", 1), Pt("bbb", 2)], [], SharedVersions.Local,
            new Dictionary<string, VersionCorrection>(StringComparer.Ordinal), 0);

        // Pure, and it does not touch the entry it was not asked about.
        var tidied = sources.WithThinned("aaa", true);
        Assert.NotSame(sources, tidied);
        Assert.True(tidied.Points.Single(p => p.CommitId == "aaa").Thinned);
        Assert.False(tidied.Points.Single(p => p.CommitId == "bbb").Thinned);
        Assert.False(sources.Points.Single(p => p.CommitId == "aaa").Thinned);   // the original stands

        // Nothing to change, nothing named, and no such entry all answer "the same record back", so a
        // caller cannot silently redraw a list it did not change.
        Assert.Same(tidied,  tidied.WithThinned("aaa", true));
        Assert.Same(sources, sources.WithThinned("zzz", true));
        Assert.Same(sources, sources.WithThinned("", true));

        // And it comes back the same way.
        Assert.False(tidied.WithThinned("aaa", false).Points.Single(p => p.CommitId == "aaa").Thinned);

        // The panel's half: the row is redrawn from what it already had.
        var tool = new HistoryTool();
        tool.SetSources(sources, hasWorkspace: true);
        Assert.Equal(2, tool.Rows.Count);
        Assert.DoesNotContain(tool.Rows, r => r.Entry.Thinned);

        tool.MarkTidiedAway("aaa", true);
        Assert.Contains(tool.Rows, r => r.Entry.Thinned && r.Entry.Identity == "aaa");

        // Only the FAILING write refreshes for real — the successful one never does.
        string cs = RestorePointsTests.StripComments(
            RestorePointsTests.ReadSource("src/Ui/ViewModels/WorkspaceViewModel.Revision.cs"));
        Assert.Contains("if (History.Forget(WorkspaceRootDir, point)) tool.MarkTidiedAway(point.CommitId, true);",
                        cs, StringComparison.Ordinal);
        Assert.Contains("tool.MarkTidiedAway(point.CommitId, false);", cs, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>A long title is trimmed, not drawn over the mark beside it</b> (owner, 2026-09-07).
    ///
    /// <para>The title carried <c>TextTrimming="CharacterEllipsis"</c> all along and it never engaged,
    /// because it sat in a HORIZONTAL StackPanel — which measures its children with infinite width
    /// along its own orientation. There was no width for the ellipsis to bite on, so a long note name
    /// ran straight over "tidied away" in the next column. A Grid with a star column is what gives it
    /// one.</para>
    /// </summary>
    [Fact]
    public void ALongTitleIsTrimmedRatherThanDrawnOverTheMarkBesideIt()
    {
        string axaml = RestorePointsTests.ReadSource("src/Ui/Views/Revision/HistoryToolView.axaml");

        int at = axaml.IndexOf("Text=\"{Binding Title}\"", StringComparison.Ordinal);
        Assert.True(at > 0, "the row no longer binds a Title at all");

        // The nearest layout ancestor of the title must be the constraining Grid, not a StackPanel.
        // Matched on the STAR column rather than on the whole column list: the row gained a column for
        // the short identity on 2026-09-08, and what makes the ellipsis engage is that the title's own
        // column is the star one — not how many fixed columns sit to its left.
        string before = axaml[..at];
        int grid  = before.LastIndexOf("<Grid ColumnDefinitions=\"Auto,", StringComparison.Ordinal);
        int stack = before.LastIndexOf("<StackPanel Orientation=\"Horizontal\"", StringComparison.Ordinal);

        Assert.True(grid > 0, "the title is no longer inside a Grid at all.");

        // The COLUMN LIST IS NOT SPELLED OUT HERE and the count is not the invariant (§5.12 added a
        // fourth column, for the mark saying an entry carries a longer note). What makes the ellipsis
        // engage is that the title occupies the STAR column, so that is what is asserted: the last
        // column is the star one, and the title's own index is the last index.
        int listStart = grid + "<Grid ColumnDefinitions=\"".Length;
        int listEnd   = axaml.IndexOf('"', listStart);
        string[] columns = axaml[listStart..listEnd].Split(',');

        Assert.Equal("*", columns[^1].Trim());
        Assert.Contains($"Grid.Column=\"{columns.Length - 1}\"",
                        axaml[(at - 200)..at], StringComparison.Ordinal);

        Assert.True(grid > stack,
                    "the title is back inside a horizontal StackPanel, which measures with infinite "
                  + "width — its ellipsis will never engage and a long title will overlap the mark.");

        Assert.Contains("TextTrimming=\"CharacterEllipsis\"", axaml[at..(at + 220)], StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The way back is withdrawn when it would name the state the workspace is already in</b>
    /// (owner, 2026-09-08: the panel read <i>Now at f15b942</i> over a button reading <i>Go back to
    /// f15b942</i>).
    ///
    /// <para>A restore whose target holds the content the workspace already had replaced nothing, and
    /// <c>WorkspaceRestore</c> then resolves the replaced state to an entry holding that same content
    /// — correctly, because that is where the workspace was. What stops being meaningful is the way
    /// BACK, so the action goes and the line says only where the workspace is.</para>
    ///
    /// <para><b>Content decides it, not identity.</b> Two entries over one tree are two identities and
    /// one state, so an identity comparison alone would still have offered the second of them.</para>
    /// </summary>
    [Fact]
    public void TheWayBackIsWithdrawnWhenItNamesTheStateAlreadyLoaded()
    {
        var now = DateTimeOffset.UtcNow;

        RestorePoint Point(string commit, string tree) =>
            new("refs/x", commit, tree, 1, now, CheckpointOrigin.BeforeRestore, "before going back",
                null, true, []);

        // The entry the restore reported as replaced IS the entry it went to.
        var same = Point("f15b942aaaa", "tree-1");
        var tool = new HistoryTool { WayForward = new WayForward("t", same, same.CommitId, now, same.TreeId) };

        Assert.True(tool.HasWayForward);                    // the line still says where the workspace is
        Assert.False(tool.CanComeForward);                  // …and offers nothing
        Assert.Contains("f15b942", tool.WayForwardText, StringComparison.Ordinal);
        Assert.DoesNotContain("is kept as", tool.WayForwardText, StringComparison.Ordinal);

        // A DIFFERENT identity over the SAME content is the same state, and is withdrawn too.
        var twin = Point("bbbbbbbbbbbb", "tree-1");
        var over = new HistoryTool { WayForward = new WayForward("t", twin, "f15b942aaaa", now, "tree-1") };
        Assert.False(over.CanComeForward);

        // Genuinely different content: the way back stands.
        var other = Point("cccccccccccc", "tree-2");
        var real  = new HistoryTool { WayForward = new WayForward("t", other, "f15b942aaaa", now, "tree-1") };
        Assert.True(real.CanComeForward);
        Assert.Contains("is kept as", real.WayForwardText, StringComparison.Ordinal);
        Assert.Contains(HistoryIds.Short(other.CommitId), real.ComeForwardText, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Going back is a BUTTON in the panel, not only a right-click menu item</b> (owner,
    /// 2026-09-08: there was no go-back button in the History window).
    ///
    /// <para>R-rc10-17 moved every row action to the row's own menu, which is right for the other
    /// seven — and left this window's one primary action behind a gesture a designer has to guess at,
    /// at the moment they are least inclined to go hunting. The menu item stays: one action, two
    /// sites, and <see cref="HistoryTool.GoBack"/> is still the only implementation.</para>
    ///
    /// <para><b>And there is no button when it would land where the workspace already is</b> (owner,
    /// 2026-09-08) — the same withdrawal <see cref="WayForward.LeadsSomewhereElse"/> makes for the way
    /// back, applied to the selection.</para>
    /// </summary>
    [Fact]
    public void GoingBackIsAButtonOnTheSurface_AndIsAbsentForTheStateAlreadyLoaded()
    {
        var now  = DateTimeOffset.UtcNow;
        var tool = new HistoryTool();

        var earlier = new RestorePoint("refs/x", new string('a', 40), new string('t', 40), 1, now,
                                       CheckpointOrigin.SavePoint, "the working match",
                                       "the working match", false, []);
        var here    = new RestorePoint("refs/y", new string('b', 40), new string('u', 40), 2, now,
                                       CheckpointOrigin.BeforeRestore, "before going back", null,
                                       false, []);

        tool.SetRows(new HistoryList.Result(
            [new HistoryEntry(HistoryEntryKind.RestorePoint, null, here,    null, now),
             new HistoryEntry(HistoryEntryKind.RestorePoint, null, earlier, null, now)], 0),
            hasWorkspace: true);

        Assert.False(tool.CanGoBackToSelection);            // nothing selected, nothing to name

        tool.Selected = tool.Rows[1];
        Assert.True(tool.CanGoBackToSelection);
        Assert.Contains("the working match", tool.Selected!.GoBackText, StringComparison.Ordinal);

        // A restore has happened and the workspace sits on `here`: that row offers no button, and
        // every other row still does.
        tool.WayForward = new WayForward("the working match", earlier, here.CommitId, now, here.TreeId);

        tool.Selected = tool.Rows[0];
        Assert.False(tool.CanGoBackToSelection);
        tool.Selected = tool.Rows[1];
        Assert.True(tool.CanGoBackToSelection);

        // The button is really in the window, bound to that property and to the row's own wording.
        string axaml = RestorePointsTests.ReadSource("src/Ui/Views/Revision/HistoryToolView.axaml");
        Assert.Contains("x:Name=\"GoBackButton\"", axaml, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding CanGoBackToSelection}\"", axaml, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Going back and forth between two states adds no entries after the first pass</b> (owner,
    /// 2026-09-07: doing it "a bunch of times" grew "a whole bunch of new history states").
    ///
    /// <para>R-rc5-12a's "unconditionally" is about the PROMISE — the state being replaced must be
    /// recoverable — not about writing a duplicate of a state the history already holds. §5.3's tree
    /// test was there, and it compared against the NEWEST entry only, which on a ping-pong is the
    /// wrong entry every single time: the tree being replaced is always the one BEFORE the newest, so
    /// the test said "changed" on every toggle.</para>
    ///
    /// <para><b>The way forward still names a real entry</b>, and that is the half that makes this
    /// safe rather than merely tidy. When nothing is written, <c>PreRestore</c> resolves to the entry
    /// that ALREADY holds the replaced state — the original row, which is a better answer than a fresh
    /// duplicate. A null there would have been silent and severe: <c>GoBackTo</c> pattern-matches on
    /// it, so the workspace would have been written and then never reloaded.</para>
    /// </summary>
    [GitFact]
    public void GoingBackAndForthDoesNotGrowTheList()
    {
        using var ws = Armed();
        var git = ws.Git();

        ws.Write("cells/a/thing.csch", "state A");
        Assert.True(WorkspaceCommit.Commit(git, "Version A").Ok);
        var a = HistoryBrowser.Versions(git)[0];

        ws.Write("cells/a/thing.csch", "state B");
        Assert.True(WorkspaceCommit.Commit(git, "Version B").Ok);
        var b = HistoryBrowser.Versions(git)[0];

        int atStart = RestorePoints.List(git).Count;
        List<int> counts = [];

        // The first two protect a state each — both genuinely new.
        for (int i = 1; i <= 8; i++)
        {
            var target = i % 2 == 1 ? a : b;
            var result = WorkspaceRestore.Restore(git, HistoryBrowser.AsRestorePoint(target));

            Assert.True(result.Ok, $"toggle {i} failed");

            // The way forward is never absent. GoBackTo returns early on a null PreRestore, so this
            // being null would leave the workspace written and never reloaded — silent, and severe.
            Assert.NotNull(result.PreRestore);

            Assert.Equal(i % 2 == 1 ? "state A" : "state B",
                         File.ReadAllText(Path.Combine(ws.Root, "cells", "a", "thing.csch")));

            counts.Add(RestorePoints.List(git).Count);
        }

        string trail = string.Join(", ", counts);

        // THE LIST CONVERGES. One entry per distinct state the toggling actually passes through, and
        // then nothing — where before it was one per toggle, without limit.
        //
        // Three rather than two, and the third is not a defect: preserving the workspace's own
        // recording setting across a restore (R-rc5-12c rule 4) rewrites that field in the `.cws`, so
        // the tree the workspace STARTED in differs by one line from the tree a restore puts back. It
        // is a genuinely different state the first time it appears and a repeat every time after,
        // which is exactly what the test is asserting about.
        Assert.True(counts.Skip(2).Distinct().Count() == 1,
                    $"the list is still growing — start {atStart}, after each toggle: {trail}");

        Assert.True(RestorePoints.List(git).Count <= atStart + 3,
                    $"expected one entry per distinct state, got {RestorePoints.List(git).Count}: {trail}");

        // …and an actually-new state is still protected, which is the property the count must not buy.
        int settled = RestorePoints.List(git).Count;
        ws.Write("cells/a/thing.csch", "state C");
        Assert.True(WorkspaceRestore.Restore(git, HistoryBrowser.AsRestorePoint(a)).Ok);
        Assert.Equal(settled + 1, RestorePoints.List(git).Count);
    }

    // ══ 7. The year (R-rc10-13) ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Inside the last day a row says how long ago</b> (owner, 2026-09-07); past a day it says the
    /// clock time and lets <c>Day</c> beside it carry the date.
    ///
    /// <para>Within a day the elapsed time is what a designer is reading for — the entry they want is
    /// the one from before the change they now regret, and <i>20 minutes ago</i> answers that where
    /// <i>14:32</i> makes them work out what time it was when they started.</para>
    ///
    /// <para><b>An entry stamped in the FUTURE falls back to the clock time</b> — a clock moved back,
    /// or a workspace off a machine set differently. A relative phrase there would be a statement
    /// rather than a rendering, and this panel's labels never claim more than they know: the ordering
    /// is circuitRF's own sequence precisely because a wall clock is user-writable state.</para>
    /// </summary>
    [Fact]
    public void InsideTheLastDayARowSaysHowLongAgo()
    {
        var now = new DateTimeOffset(2026, 3, 17, 14, 32, 0, TimeSpan.Zero);

        Assert.Equal("just now",       HistoryDates.Time(now.AddSeconds(-5),  now));
        Assert.Equal("just now",       HistoryDates.Time(now,                 now));
        Assert.Equal("1 minute ago",   HistoryDates.Time(now.AddMinutes(-1),  now));
        Assert.Equal("7 minutes ago",  HistoryDates.Time(now.AddMinutes(-7),  now));
        Assert.Equal("59 minutes ago", HistoryDates.Time(now.AddSeconds(-3599), now));
        Assert.Equal("1 hour ago",     HistoryDates.Time(now.AddHours(-1),    now));
        Assert.Equal("3 hours ago",    HistoryDates.Time(now.AddHours(-3),    now));
        Assert.Equal("23 hours ago",   HistoryDates.Time(now.AddHours(-23),   now));

        // A day and over is the clock time again — and the singular/plural boundary is exact rather
        // than rounded, so nothing ever reads "1 hours ago".
        string aDayOld = HistoryDates.Time(now.AddHours(-24), now);
        Assert.DoesNotContain("ago", aDayOld, StringComparison.Ordinal);
        Assert.Contains(":", aDayOld, StringComparison.Ordinal);

        // Stamped in the future: rendered, never asserted.
        string future = HistoryDates.Time(now.AddHours(2), now);
        Assert.DoesNotContain("ago", future, StringComparison.Ordinal);
        Assert.Contains(":", future, StringComparison.Ordinal);

        // And the row itself reads it, measured against the moment the list was built.
        var version = new HistoryVersion("0123456789abcdef0123", "tree", now.AddMinutes(-20),
                                         "Output match retuned", true, null, "A Designer");
        var entry   = new HistoryEntry(HistoryEntryKind.Version, version, null, null, now.AddMinutes(-20));

        Assert.Equal("20 minutes ago", new HistoryRowItem(entry, now, showAuthor: false).When);
    }

    /// <summary>
    /// <b>Every card the same width, and a very subtle band on alternate rows</b> (owner, 2026-09-07).
    ///
    /// <para>Each row was sizing to its own title, so a list of one-word and one-sentence entries read
    /// as a ragged stack of different objects rather than one column of the same kind of thing. Both
    /// halves are needed — the item has to be GIVEN the panel's width, and the Expander inside it has
    /// to SPEND it rather than shrink back to its header's content.</para>
    ///
    /// <para>The band targets the ContentPresenter because that is where this theme's ListBoxItem
    /// paints its background, and excludes <c>:selected</c> and <c>:pointerover</c> so it sits UNDER
    /// those rather than over them — a style declared on the ListBox outranks the control theme's, so
    /// without the exclusion the even rows would lose their selection highlight. The same shape as
    /// ComponentImportChooserDialog's, which is where the argument was worked out.</para>
    /// </summary>
    [Fact]
    public void EveryCardIsTheSameWidth_AndAlternateRowsCarryASubtleBand()
    {
        string axaml = RestorePointsTests.ReadSource("src/Ui/Views/Revision/HistoryToolView.axaml");

        Assert.Contains("<Style Selector=\"ListBoxItem\">", axaml, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"HorizontalContentAlignment\" Value=\"Stretch\"/>",
                        axaml, StringComparison.Ordinal);
        Assert.Contains("HorizontalAlignment=\"Stretch\" HorizontalContentAlignment=\"Stretch\"",
                        axaml, StringComparison.Ordinal);

        Assert.Contains(
            "ListBoxItem:nth-child(2n):not(:selected):not(:pointerover) /template/ ContentPresenter",
            axaml, StringComparison.Ordinal);
    }


    /// <summary>
    /// <b>The year appears exactly when the entry is not from the current year</b>, on the row's date
    /// AND on the restored-from line.
    ///
    /// <para>This was a defect rather than a sparseness: the two panels rendered <c>ddd d MMM</c> and
    /// <c>d MMM HH:mm</c>, so a version kept in December read in January as one kept this week — the
    /// list's own date saying something false about the entry it labelled.</para>
    /// </summary>
    [Fact]
    public void TheYearAppearsExactlyWhenItIsNotTheCurrentYear()
    {
        var now      = new DateTimeOffset(2026, 3, 17, 10, 0, 0, TimeSpan.Zero);
        var thisYear = new DateTimeOffset(2026, 1, 8, 9, 30, 0, TimeSpan.Zero);
        var lastYear = new DateTimeOffset(2025, 12, 19, 16, 45, 0, TimeSpan.Zero);

        Assert.DoesNotContain("2026", HistoryDates.Day(thisYear, now), StringComparison.Ordinal);
        Assert.Contains("2025", HistoryDates.Day(lastYear, now), StringComparison.Ordinal);

        Assert.DoesNotContain("2026", HistoryDates.DayAndTime(thisYear, now), StringComparison.Ordinal);
        Assert.Contains("2025", HistoryDates.DayAndTime(lastYear, now), StringComparison.Ordinal);

        // Relative labels for the recent entries, which §5.6 rule 2 makes safe: the wall clock
        // supplies the label and never the ordering.
        Assert.Equal("today",     HistoryDates.Day(now, now));
        Assert.Equal("yesterday", HistoryDates.Day(now.AddDays(-1), now));

        // And on the rendered rows, which is where a designer actually reads them.
        var old = new HistoryEntry(HistoryEntryKind.Version,
            new HistoryVersion("0123456789abcdef0123", "tree", lastYear, "Output match retuned", true,
                               new RestoredFrom("the working match", lastYear.AddDays(-2)), "A Designer"),
            null, null, lastYear);

        var row = new HistoryRowItem(old, now, showAuthor: true);
        Assert.Contains("2025", row.Day, StringComparison.Ordinal);
        Assert.Contains("2025", row.RestoredFrom, StringComparison.Ordinal);

        var recent = new HistoryRowItem(old with { WhenUtc = thisYear }, now, showAuthor: true);
        Assert.DoesNotContain("2026", recent.Day, StringComparison.Ordinal);
    }

    // ══ 8. The go-back glyph is not the undo glyph (R-rc10-16) ═══════════════════════════════════

    /// <summary>
    /// <b>The action that goes back may not wear the application's own undo arrow</b> — on the one
    /// action every line of the design note and both user chapters is at pains to distinguish from an
    /// undo (§5.4).
    ///
    /// <para>Three things, and the third is what stops the fix being cosmetic: the glyph is not
    /// <c>ArrowULeftTop</c>, it is the same glyph everywhere the action appears, and it resolves in the
    /// pinned icon package rather than rendering as nothing.</para>
    /// </summary>
    [Fact]
    public void TheGoBackGlyphIsNotTheUndoGlyphAndResolves()
    {
        string view = RestorePointsTests.ReadSource("src/Ui/Views/Revision/HistoryToolView.axaml");

        var kinds = Regex.Matches(view, @"Kind=""(\w+)""").Select(m => m.Groups[1].Value).ToList();
        Assert.DoesNotContain("ArrowULeftTop", kinds);

        // The action appears once, on the row's own menu, and it is this glyph.
        int at = view.IndexOf("Click=\"OnGoBackClick\"", StringComparison.Ordinal);
        Assert.True(at > 0, "the go-back action is not in the view at all");

        var used = Regex.Matches(view[at..], @"Kind=""(\w+)""")
                        .Select(m => m.Groups[1].Value).First();
        Assert.NotEqual("ArrowULeftTop", used);

        // It resolves in the pinned Material.Icons.Avalonia — a Kind that does not draws nothing, with
        // no error anywhere.
        Assert.True(Enum.TryParse(typeof(Material.Icons.MaterialIconKind), used, out _),
                    $"'{used}' is not a Kind in the pinned icon package");

        // And it reads distinctly from the panel's own tab and menu glyph — history and restore are
        // near-twins in that set, so the pair is asserted rather than assumed.
        string window = RestorePointsTests.ReadSource("src/Ui/Views/WorkspaceWindow.axaml");
        int menu = window.IndexOf("CommandParameter=\"History\"", StringComparison.Ordinal);
        Assert.True(menu > 0);
        string tabGlyph = Regex.Matches(window[menu..], @"Kind=""(\w+)""")
                               .Select(m => m.Groups[1].Value).First();
        Assert.NotEqual(tabGlyph, used);
    }

    // ══ 9. The expander names the identity, and the vocabulary scan still passes (R-rc10-14/15) ══

    /// <summary>
    /// <b>The identity appears in the expander and nowhere else</b>, and every other user-visible
    /// string in the merged panel is still free of git vocabulary.
    ///
    /// <para>R-rc10-15: nothing git-shaped appears unbidden, and what an EXPLICIT action produces may
    /// be named precisely — opening an expander is that action. The exemption is asserted BY NAME so it
    /// cannot spread, the same shape as R-rc7-7's.</para>
    /// </summary>
    [Fact]
    public void TheExpanderNamesTheIdentityAndTheVocabularyScanStillPasses()
    {
        string[] forbidden = ["branch", "checkout", "HEAD", "detached", "stash", "merge", "rebase"];

        foreach (string path in (string[])
                 ["src/Ui/ViewModels/Dock/HistoryTool.cs",
                  "src/Ui/Views/Revision/HistoryToolView.axaml",
                  "src/Design/Revision/HistoryList.cs",
                  "src/Design/Revision/HistoryDates.cs"])
        {
            string source = RestorePointsTests.StripComments(RestorePointsTests.ReadSource(path));

            foreach (System.Text.RegularExpressions.Match literal in
                     Regex.Matches(source, path.EndsWith(".axaml", StringComparison.Ordinal)
                                           ? @"(?<=\w=)""[^""]*"""
                                           : @"""((?:[^""\\\n]|\\.)*)"""))
            {
                string text = literal.Value;
                if (text.StartsWith("\"{", StringComparison.Ordinal)) continue;   // a binding is an address

                foreach (string word in forbidden)
                    Assert.False(Regex.IsMatch(text, $@"\b{Regex.Escape(word)}\b", RegexOptions.IgnoreCase),
                                 $"{path} shows a designer the word '{word}': {text}");
            }
        }

        // The one permitted appearance, asserted BY NAME. Without this the scan above would pass just
        // as well on a panel that named nothing at all, which is the opposite failure.
        var entry = new HistoryEntry(HistoryEntryKind.Version,
            new HistoryVersion("0123456789abcdef0123", "tree", DateTimeOffset.UtcNow,
                               "Output match retuned", true, null, "A Designer"),
            null, null, DateTimeOffset.UtcNow);

        var row = new HistoryRowItem(entry, DateTimeOffset.UtcNow, showAuthor: false);
        // The WHOLE identity, and the same string the clipboard gets — showing a shortened one next to
        // a forty-character paste is what made a designer doubt the copy (owner, 2026-09-07).
        Assert.Equal("0123456789abcdef0123", row.Identity);
        Assert.Equal(entry.Identity, row.Identity);
        Assert.True(row.HasIdentity);

        // And the expander carries the rest of R-rc10-14 — what the ROW cannot say.
        Assert.NotEqual("", row.FullWhen);
        Assert.NotEqual("", row.Origin);
        Assert.NotEqual("", row.Reach);

        // A restore point never travelled, and says so: that is the fact §5.11 turns on and the one a
        // designer most often assumes the other way round.
        var point = new HistoryEntry(HistoryEntryKind.RestorePoint, null,
            Point(7, kept: true), null, DateTimeOffset.UtcNow);
        Assert.Contains("this machine only",
                        new HistoryRowItem(point, DateTimeOffset.UtcNow, false).Reach,
                        StringComparison.OrdinalIgnoreCase);
    }

    // ══ 10. The way forward (R-rc10-18) ══════════════════════════════════════════════════════════

    /// <summary>
    /// <b>A restore reports itself in the panel by name, and following the line returns the state.</b>
    ///
    /// <para>This is the two-panel split producing a safety failure rather than clutter: §5.8's restore
    /// takes a checkpoint of the current state first and files it among the restore points, so a
    /// restore begun from the Versions panel left the designer looking at a window with no evidence
    /// that the afternoon they had just replaced still existed. The reassurance was implemented,
    /// correct, and in the room the designer was not in.</para>
    ///
    /// <para><b>It creates nothing</b> — the entry already exists — and coming forward is an ordinary
    /// restore taking an ordinary checkpoint of its own, which is RC-5 gate 15's assertion reached from
    /// this entry point.</para>
    /// </summary>
    [GitFact]
    public void ARestoreReportsTheWayForwardAndFollowingItReturnsTheState()
    {
        using var ws = Armed();
        var git = ws.Git();

        ws.Write("cells/a/thing.csch", "tuesday");
        var tuesday = WorkspaceCheckpoints.Take(git, CheckpointOrigin.SavePoint, "the working match");
        Assert.True(tuesday.Recorded);

        ws.Write("cells/a/thing.csch", "thursday afternoon");

        int before = CheckpointReferences.List(git).Count;

        var back = WorkspaceRestore.Restore(git, tuesday.Point!);
        Assert.True(back.Ok);
        Assert.NotNull(back.PreRestore);
        Assert.Equal("tuesday", File.ReadAllText(ws.File_("cells/a/thing.csch")));

        // The line names BOTH entries — where it went, and what the previous state was kept as — and it
        // names them by IDENTITY (owner, 2026-09-08). It used to quote their labels, which on the two
        // entries a restore produces are circuitRF's own generated wording, so the sentence read "went
        // back to 'workspace closed'. What you had was kept as 'your work before you went back'" and
        // named neither state in a way anybody could act on.
        var forward = new WayForward(tuesday.Point!.Label, back.PreRestore!,
                                     tuesday.Point.CommitId, tuesday.Point.TakenUtc,
                                     tuesday.Point.TreeId);
        var tool    = new HistoryTool { WayForward = forward };

        Assert.True(tool.HasWayForward);
        Assert.True(tool.CanComeForward);
        Assert.Contains(HistoryIds.Short(tuesday.Point.CommitId),    tool.WayForwardText, StringComparison.Ordinal);
        Assert.Contains(HistoryIds.Short(back.PreRestore!.CommitId), tool.WayForwardText, StringComparison.Ordinal);

        // And the BUTTON names where it goes, rather than a direction that could be pressed for ever.
        Assert.Contains(HistoryIds.Short(back.PreRestore!.CommitId), tool.ComeForwardText, StringComparison.Ordinal);

        // Neither generated label reaches the sentence at all now, which is what stops two different
        // states being named by the same three words.
        Assert.DoesNotContain(back.PreRestore!.Label, tool.WayForwardText, StringComparison.Ordinal);

        // Following it is an ordinary restore, and it returns the tree — byte for byte.
        var forwardAgain = WorkspaceRestore.Restore(git, forward.KeptAs);
        Assert.True(forwardAgain.Ok);
        Assert.Equal("thursday afternoon", File.ReadAllText(ws.File_("cells/a/thing.csch")));

        // And a fresh pre-restore checkpoint was taken on the way — RC-5 gate 15's assertion, reached
        // from this entry point. Two restores, two new entries.
        Assert.Equal(before + 2, CheckpointReferences.List(git).Count);
        Assert.NotEqual(back.PreRestore!.CommitId, forwardAgain.PreRestore!.CommitId);

        // It creates nothing of its own: the entry the line points at is the one the restore already
        // wrote, not a second copy of it.
        Assert.Contains(RestorePoints.List(git), p => p.CommitId == back.PreRestore!.CommitId);
    }

    // ══ 11. The pre-restore checkpoint is kept (R-rc10-20) ═══════════════════════════════════════

    /// <summary>
    /// <b>A pre-restore checkpoint survives a sweep that thins every unkept entry.</b>
    ///
    /// <para>It is the entry that makes <i>going back is never a one-way door</i> true, and
    /// <c>IsAlwaysKept</c> omitted it — so the one entry whose entire purpose is to undo a destructive
    /// operation aged out of the list on its own while the designer kept working. Not data loss (§4.5
    /// keeps the objects, RC-6's journal brings the entry back); the promise leaving the place the
    /// promise was made.</para>
    ///
    /// <para><b>This is RC-10's one storage-visible change, and it is a keep rather than a
    /// delete.</b></para>
    /// </summary>
    [GitFact]
    public void APreRestoreCheckpointSurvivesASweepThatWouldHaveThinnedIt()
    {
        Assert.True(WorkspaceCheckpoints.IsAlwaysKept(CheckpointOrigin.BeforeRestore));

        using var ws = Armed();
        var git = ws.Git();

        // Enough unkept entries that the sweep has plenty to take, and a target to go back to.
        ws.Write("cells/a/thing.csch", "tuesday");
        var tuesday = WorkspaceCheckpoints.Take(git, CheckpointOrigin.SavePoint, "the working match");

        for (int i = 0; i < 12; i++)
        {
            ws.Write("cells/a/thing.csch", $"afternoon {i}");
            Assert.True(WorkspaceCheckpoints.Take(git, CheckpointOrigin.WorkspaceClosed, null,
                                                  attended: false).Recorded);
        }

        // The afternoon since the last boundary — without it the pre-restore boundary finds the tree
        // already recorded and writes nothing, so there would be no entry to keep.
        ws.Write("cells/a/thing.csch", "thursday afternoon");

        var back = WorkspaceRestore.Restore(git, tuesday.Point!);
        Assert.True(back.Ok);
        Assert.NotNull(back.PreRestore);
        Assert.True(back.PreRestore!.Kept, "the pre-restore entry was written without the kept mark");

        // Drive retention as hard as it goes: nothing survives on age.
        var swept = RetentionSweep.Run(git, new RetentionPolicy(RetentionDays: 0,
                                                                MinimumRestorePoints: 1,
                                                                MaxFraction: 1.0));
        Assert.False(swept.Plan.Refused, "the sweep refused, so it proves nothing");
        Assert.True(swept.Thinned.Count > 0, "the sweep took nothing, so it proves nothing");

        var live = RestorePoints.List(git);
        Assert.Contains(live, p => p.CommitId == back.PreRestore!.CommitId);
        Assert.Contains(live, p => p.Origin   == CheckpointOrigin.BeforeRestore);
    }

    // ══ 12. `history list` shows what the panel shows (R-rc10-21, R-rc10-22) ═════════════════════

    /// <summary>
    /// <b>The verb run as a PROCESS against the in-process query — same filter, same order, byte for
    /// byte on the reported fields.</b>
    ///
    /// <para>A verb that cannot express what the window shows means an agent and a designer are reading
    /// two different histories, and two defaults that differ silently is the defect R-rc10-22 exists to
    /// prevent. The two render from one function, which is what makes byte identity a property rather
    /// than a coincidence.</para>
    /// </summary>
    [GitFact]
    public void HistoryListShowsWhatThePanelShows()
    {
        if (CliUnderTest.Dll is not { } dll)
        {
            Assert.True(true, "the CLI has not been built in this tree");
            return;
        }

        using var ws = Armed();
        var git = ws.Git();

        Write(ws, git, CheckpointOrigin.SavePoint,       "the working match");
        Write(ws, git, CheckpointOrigin.BeforeBatch,     "widen the output match");
        Write(ws, git, CheckpointOrigin.WorkspaceClosed, null);

        ws.Write("cells/a/thing.csch", "for a version");
        Assert.True(WorkspaceCommit.Commit(git, "Output match retuned").Ok);

        // The DEFAULT, which must be the panel's.
        Compare(HistoryFilter.Default, []);

        // Every flag R-rc10-21 asks for.
        Compare(HistoryFilter.Default with { Automatic = true }, ["--include-automatic"]);
        Compare(new HistoryFilter(true, false, false, false, false), ["--kinds", "versions"]);
        Compare(HistoryFilter.Default with { Search = "match" }, ["--search", "match"]);

        void Compare(HistoryFilter filter, string[] flags)
        {
            var expected = HistoryList.Read(git, filter).Rows.Select(HistoryList.Line).ToList();

            var run = RunCli(dll, ws.HomeDir, ws.Root, ["history", "list", ws.Root, .. flags]);
            Assert.Equal(0, run.Code);

            var actual = run.Out.Replace("\r\n", "\n")
                                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                                .ToList();

            Assert.Equal(expected, actual);
        }
    }

    // ══ 13. Nothing about storage moved (R-rc10-4) ═══════════════════════════════════════════════

    /// <summary>
    /// <b>Checkpoints stay on their own references, versions stay on the line of work, and retention's
    /// scope is unchanged.</b>
    ///
    /// <para>The moment the two are stored alike, §5.2a's travel table stops being true and §5.6 has a
    /// human-written commit in its scope. This brief is a presentation change and may not become a
    /// storage one — §5.10 rule 6.</para>
    /// </summary>
    [GitFact]
    public void NothingAboutStorageMoved()
    {
        using var ws = Armed();
        var git = ws.Git();

        Write(ws, git, CheckpointOrigin.SavePoint, "the working match");
        ws.Write("cells/a/thing.csch", "for a version");
        Assert.True(WorkspaceCommit.Commit(git, "Output match retuned").Ok);

        // Exercise the panel — the whole point is that reading changes nothing.
        var tool = new HistoryTool();
        tool.SetRows(HistoryList.Read(git, HistoryFilter.Default), hasWorkspace: true);
        tool.ShowAutomatic = true;
        tool.SearchText    = "match";
        tool.SetRows(HistoryList.Read(git, tool.Filter), hasWorkspace: true);

        // The checkpoint is on its own reference and is NOT on the designer's line of work.
        var points = CheckpointReferences.List(git);
        Assert.Single(points);

        var onTheLine = HistoryBrowser.Versions(git);
        Assert.Single(onTheLine);
        Assert.DoesNotContain(onTheLine, v => points.Any(p => p.CommitId == v.CommitId));

        // Retention's scope is the checkpoints and nothing else (§5.6 rule 5).
        RetentionSweep.Run(git, new RetentionPolicy(RetentionDays: 0, MinimumRestorePoints: 1,
                                                    MaxFraction: 1.0));
        Assert.Single(HistoryBrowser.Versions(git));
    }

    /// <summary>
    /// The source half: <b>this brief added no call that writes a reference or a commit</b>, the single
    /// exception being R-rc10-20's kept mark, named.
    /// </summary>
    [Fact]
    public void ThisBriefAddedNoCallThatWritesAReferenceOrACommit()
    {
        foreach (string path in RcTenSources())
        {
            string source = RestorePointsTests.StripComments(RestorePointsTests.ReadSource(path));

            foreach (string forbidden in (string[])
                     ["commit-tree", "write-tree", "update-ref", "CheckpointMessage.Build",
                      "WorkspaceCheckpoints.Take", "WorkspaceCommit.Commit"])
                Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
        }

        // THE ONE EXCEPTION, named. RC-10's only storage-visible change is a keep, never a delete —
        // and it is a mark on an entry the restore was already writing, not a new write.
        Assert.True(WorkspaceCheckpoints.IsAlwaysKept(CheckpointOrigin.BeforeRestore));
        Assert.True(WorkspaceCheckpoints.IsAlwaysKept(CheckpointOrigin.SavePoint));
        Assert.True(WorkspaceCheckpoints.IsAlwaysKept(CheckpointOrigin.RecordingOff));
        Assert.True(WorkspaceCheckpoints.IsAlwaysKept(CheckpointOrigin.RecordingOn));
        Assert.False(WorkspaceCheckpoints.IsAlwaysKept(CheckpointOrigin.WorkspaceClosed));
        Assert.False(WorkspaceCheckpoints.IsAlwaysKept(CheckpointOrigin.BeforeBatch));
    }

    // ── Shared ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The files RC-10 wrote or rewrote. Named rather than globbed, so a file added to the
    /// merge and not to this list is a visible omission rather than a silent one.</summary>
    private static IEnumerable<string> RcTenSources()
    {
        yield return "src/Design/Revision/HistoryList.cs";
        yield return "src/Design/Revision/HistoryDates.cs";
        yield return "src/Ui/ViewModels/Dock/HistoryTool.cs";
        yield return "src/Ui/Views/Revision/HistoryToolView.axaml";
        yield return "src/Ui/Views/Revision/HistoryToolView.axaml.cs";
        yield return "src/Ui/Docking/DockLayoutRetirement.cs";
    }

    // ══ Nothing to record: the two keep actions, and the way back (owner, 2026-09-08) ═════════════

    /// <summary>
    /// <b>The free signal</b> — <c>docs/sonnet-briefs/brief-history-keep-when-nothing-changed.md</c> §4,
    /// gates 1 and 2.
    ///
    /// <para>Both keep actions used to open their dialog, take a title, and then create nothing: the
    /// tree test that declines a duplicate lives in <c>GitCheckpoint.Record</c>, after the dialog. The
    /// enabled state comes from <i>has anything been written since the last entry</i>, which is a latch
    /// this window already sets on every write — <b>and there is no repository in this fixture at all</b>,
    /// which is the assertion that no git runs to answer it.</para>
    ///
    /// <para><b>Per boundary, not per session</b>, and that is why it is a second field:
    /// <c>CircuitRfWroteAFileThisSession</c> is RC-5's arming guard and must stay true for the rest of
    /// the session, or a workspace that was edited and then recorded would stop arming its own close.</para>
    /// </summary>
    [Fact]
    public void NothingWrittenSinceTheLastEntryIsAnsweredWithNoRepositoryAtAll()
    {
        var service = new WorkspaceHistoryService(new SilentSink());

        // A fresh session over a workspace that was recorded on the way out: nothing to keep.
        service.ResetForWorkspace();
        Assert.False(service.WrittenSinceLastEntry);
        Assert.False(service.CircuitRfWroteAFileThisSession);

        // One write brings both back.
        service.NoteWorkspaceWrite();
        Assert.True(service.WrittenSinceLastEntry);
        Assert.True(service.CircuitRfWroteAFileThisSession);

        // A boundary that recorded clears ONLY the per-boundary one. There is no repository here, so
        // TakeSavePoint records nothing and the latch must stand — the flag tracks what was recorded,
        // never what was attempted.
        Assert.False(service.TakeSavePoint(Path.Combine(Path.GetTempPath(), "no-such-workspace-crf"), "x"));
        Assert.True(service.WrittenSinceLastEntry);

        // Somebody else's process writing in is not circuitRF editing a design: it must not arm the
        // repository (R-rc5-4a), and it must bring the keep actions back.
        service.ResetForWorkspace();
        service.NoteWorkspaceChangedUnderneath();
        Assert.True(service.WrittenSinceLastEntry);
        Assert.False(service.CircuitRfWroteAFileThisSession);
    }

    /// <summary>
    /// <b>A real boundary clears it, and a restore sets it again.</b> §4 gate 1's other half, over a
    /// repository this time — the flag has to follow what actually happened rather than what was asked
    /// for.
    ///
    /// <para>The restore case is the one that would be silent if missed: a restore replaces the
    /// workspace's files, and the entry it took on the way in holds what was REPLACED. So there is
    /// something to keep again, and if the flag stayed clear both keep actions would grey out at exactly
    /// the moment §1.4 is written about.</para>
    /// </summary>
    [GitFact]
    public void ABoundaryClearsTheSignalAndARestoreSetsItAgain()
    {
        using var ws  = Armed();
        var git       = ws.Git();
        var service   = new WorkspaceHistoryService(new SilentSink());

        ws.Write("cells/a/thing.csch", "monday");
        service.NoteWorkspaceWrite();
        Assert.True(service.TakeSavePoint(ws.Root, "monday"));
        Assert.False(service.WrittenSinceLastEntry);          // the newest entry now holds what is on disk

        ws.Write("cells/a/thing.csch", "tuesday");
        service.NoteWorkspaceWrite();
        Assert.True(service.KeepVersion(ws.Root, "tuesday").Ok);
        Assert.False(service.WrittenSinceLastEntry);          // a version is a boundary too

        var monday = RestorePoints.List(git).Single(p => p.Intent == "monday");
        Assert.True(service.Restore(ws.Root, monday) is { Ok: true });
        Assert.True(service.WrittenSinceLastEntry);           // the restored state is no entry's content
    }

    /// <summary>
    /// <b>The panel's two header buttons carry the reason on them</b> — §4 gate 3's shape, and the
    /// R-rc6-8 argument one control over: a greyed control with nothing said about it is
    /// indistinguishable from a feature that was never built.
    /// </summary>
    [Fact]
    public void TheKeepButtonsGreyOutWithTheReasonOnThem()
    {
        var tool = new HistoryTool();

        // True by default, so a panel nobody has told anything is the panel that was here before.
        Assert.True(tool.CanKeepAnything);

        tool.SetRows(new HistoryList.Result([], 0), hasWorkspace: true);
        Assert.True(tool.CanKeep);
        Assert.DoesNotContain(HistoryMessages.NothingToKeepYet, tool.SavePointTooltip, StringComparison.Ordinal);

        tool.CanKeepAnything = false;
        Assert.False(tool.CanKeep);
        Assert.Equal(HistoryMessages.NothingToKeepYet, tool.SavePointTooltip);
        Assert.Equal(HistoryMessages.NothingToKeepYet, tool.KeepVersionTooltip);

        // And it says what would bring them back, not only that they are off.
        Assert.Contains("already kept",       HistoryMessages.NothingToKeepYet, StringComparison.Ordinal);
        Assert.Contains("Change something",   HistoryMessages.NothingToKeepYet, StringComparison.Ordinal);

        // With no workspace the pair is off whatever the signal says.
        tool.CanKeepAnything = true;
        tool.SetRows(new HistoryList.Result([], 0), hasWorkspace: false);
        Assert.False(tool.CanKeep);

        string axaml = RestorePointsTests.ReadSource("src/Ui/Views/Revision/HistoryToolView.axaml");
        Assert.Contains("IsEnabled=\"{Binding CanKeep}\"",              axaml, StringComparison.Ordinal);
        Assert.Contains("ToolTip.Tip=\"{Binding SavePointTooltip}\"",   axaml, StringComparison.Ordinal);
        Assert.Contains("ToolTip.Tip=\"{Binding KeepVersionTooltip}\"", axaml, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The four terms of the enabled state, and the three that force it TRUE</b> — §3 step 2 and §4
    /// gates 3 and 5, held by reading the predicate itself.
    ///
    /// <para>A test that could construct a whole <c>WorkspaceViewModel</c> would assert this by running
    /// it; this one asserts the terms are all present, which is what the gates are actually about — the
    /// two that would be dangerous to lose are <b>unsaved work</b> (trap 2: the one action a designer
    /// with an unsaved schematic reaches for must not be the one that is unavailable) and <b>the held
    /// and off states</b>, which stay visible and refuse out loud rather than greying.</para>
    /// </summary>
    [Fact]
    public void TheEnabledStateIsTheFreeSignalPlusTheThreeStatesThatForceItOn()
    {
        string source = RestorePointsTests.StripComments(
            RestorePointsTests.ReadSource("src/Ui/ViewModels/WorkspaceViewModel.Revision.cs"));

        int predicate = source.IndexOf("private bool CanKeepAnything()", StringComparison.Ordinal);
        Assert.True(predicate > 0, "CanKeepAnything is what both commands report through");

        string body = source[predicate..(predicate + 500)];
        Assert.Contains("!_recordingIsOn",            body, StringComparison.Ordinal);
        Assert.Contains("!_workspaceHasAHistory",     body, StringComparison.Ordinal);
        Assert.Contains("History.WrittenSinceLastEntry", body, StringComparison.Ordinal);
        Assert.Contains("HasAnyDirtyWork",            body, StringComparison.Ordinal);

        // Both commands report through it, and neither still reports through the workspace-open test.
        Assert.Contains("[RelayCommand(CanExecute = nameof(CanKeepAnything))]", source, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(source, @"CanExecute = nameof\(CanKeepAnything\)").Count);

        // §3 step 1. A KEEP SAVES DIRTY DOCUMENTS FIRST, through the prompt a close, an archive and a
        // workspace copy already use — never a second save path — and BEFORE the dialog that takes the
        // title. A save-point records what is on disk, so with a schematic unsaved it recorded the
        // previous content under the title just written.
        foreach (string keep in new[] { "KeepThisState", "KeepThisVersion" })
        {
            int at     = source.IndexOf("private async Task " + keep + "(", StringComparison.Ordinal);
            Assert.True(at > 0, keep);

            string method = source[at..(at + 900)];
            int    prompt = method.IndexOf("PromptSaveBeforeClose", StringComparison.Ordinal);
            int    dialog = method.IndexOf("new " + keep.Replace("KeepThis", "KeepThis") + "Dialog",
                                           StringComparison.Ordinal);

            Assert.True(prompt > 0, keep + " must offer up unsaved work first");
            Assert.True(dialog > prompt, keep + " must save BEFORE it takes a title");
            Assert.Contains("HasAnyDirtyWork(includeFloated: false)", method, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// <b>The go-back button names the entry it will act on, by identity when nobody titled it</b>
    /// (owner, 2026-09-08).
    ///
    /// <para><i>Go back to this state</i> read the same on every row — and the rows it appeared on are
    /// exactly the ones whose generated labels already read alike, so the button under the list said
    /// nothing at all about which of forty entries it would act on. The wording is the way-forward
    /// button's, which is the point: two buttons that perform one action say it one way. A title a
    /// PERSON wrote is still what is shown when there is one, and still the only thing quoted.</para>
    /// </summary>
    [Fact]
    public void TheGoBackButtonNamesItsDestinationByIdentityWhenNobodyTitledIt()
    {
        var now = DateTimeOffset.UtcNow;

        RestorePoint Point(string commit, string? intent) =>
            new("refs/x", commit, new string('t', 40), 1, now,
                intent is null ? CheckpointOrigin.WorkspaceClosed : CheckpointOrigin.SavePoint,
                intent ?? "closed", intent, false, []);

        var untitled = new HistoryRowItem(
            new HistoryEntry(HistoryEntryKind.RestorePoint, null, Point("3c4d8ba9f1e2d3c4b5a6", null),
                             null, now), now, false);

        Assert.Equal("Go to 3c4d8ba", untitled.GoBackText);
        Assert.Equal(HistoryMessages.GoBackToId("3c4d8ba9f1e2d3c4b5a6"), untitled.GoBackText);

        var titled = new HistoryRowItem(
            new HistoryEntry(HistoryEntryKind.RestorePoint, null,
                             Point("aaaaaaaaaaaabbbbbbbb", "the working match"), null, now), now, false);

        Assert.Equal("Go to “the working match”", titled.GoBackText);
    }

    /// <summary>
    /// <b>And it is withdrawn for the state the workspace already holds, with no restore having
    /// happened</b> (owner, 2026-09-08).
    ///
    /// <para>The existing withdrawal knew only where THIS SESSION'S last restore had put the workspace,
    /// so on a freshly opened workspace the newest entry — whose content is exactly what is on disk —
    /// still offered a button that reloads everything to no effect, which is how a designer learns that
    /// a button does not work. The second fact costs nothing either: with nothing written since the
    /// newest entry was recorded, that entry's tree IS what the workspace holds.</para>
    ///
    /// <para><b>Matched on the TREE, so the second of two entries over one state goes too</b>, and
    /// <b>unknown offers every row</b> — a button withheld when it was needed is the failure §1.4 is
    /// about, and one offered needlessly costs a reload.</para>
    /// </summary>
    [Fact]
    public void TheGoBackButtonIsWithdrawnForTheContentTheWorkspaceAlreadyHolds()
    {
        var now  = DateTimeOffset.UtcNow;
        var tool = new HistoryTool();

        RestorePoint Point(string commit, string tree, string intent) =>
            new("refs/" + commit, commit, tree, 1, now, CheckpointOrigin.SavePoint, intent, intent,
                false, []);

        var onDisk = Point(new string('a', 40), new string('1', 40), "where we are");
        var twin   = Point(new string('b', 40), new string('1', 40), "the same state, kept twice");
        var older  = Point(new string('c', 40), new string('2', 40), "monday");

        tool.SetRows(new HistoryList.Result(
            [new HistoryEntry(HistoryEntryKind.RestorePoint, null, onDisk, null, now),
             new HistoryEntry(HistoryEntryKind.RestorePoint, null, twin,   null, now),
             new HistoryEntry(HistoryEntryKind.RestorePoint, null, older,  null, now)], 0),
            hasWorkspace: true);

        // UNKNOWN: every row is a way back, which is the direction that matters.
        tool.Selected = tool.Rows[0];
        Assert.True(tool.CanGoBackToSelection);

        tool.WorkspaceTreeId = onDisk.TreeId;

        tool.Selected = tool.Rows[0];
        Assert.False(tool.CanGoBackToSelection);          // the entry whose content is on disk
        tool.Selected = tool.Rows[1];
        Assert.False(tool.CanGoBackToSelection);          // and the other entry over that same content
        tool.Selected = tool.Rows[2];
        Assert.True(tool.CanGoBackToSelection);           // genuinely different content: the button stands

        // The host decides it from the free signal and the read it was refreshed by — never by hashing
        // the workspace, which is a whole checkpoint's work in front of somebody looking at a panel.
        string source = RestorePointsTests.StripComments(
            RestorePointsTests.ReadSource("src/Ui/ViewModels/WorkspaceViewModel.Revision.cs"));
        Assert.Contains("tool.WorkspaceTreeId = History.WrittenSinceLastEntry ? \"\" : sources.Newest?.TreeId",
                        source, StringComparison.Ordinal);
        Assert.DoesNotContain("HasSomethingToKeep", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The newest entry over both lists</b>, which is what the workspace on disk matches while
    /// nothing has been written since. Restore points are ordered by RC-5's own sequence and versions by
    /// the line of work, so the clock is the only term the two share — stated in
    /// <see cref="HistorySources.Newest"/> and gated here.
    /// </summary>
    [Fact]
    public void TheNewestEntryIsTakenOverBothLists()
    {
        var t0 = new DateTimeOffset(2026, 9, 8, 9, 0, 0, TimeSpan.Zero);

        var version = new HistoryVersion(new string('v', 40), new string('V', 40), t0.AddHours(1),
                                         "tuesday", true, null, "A Designer");
        var point   = new RestorePoint("refs/p", new string('p', 40), new string('P', 40), 3,
                                       t0.AddHours(2), CheckpointOrigin.WorkspaceClosed, "closed",
                                       null, false, []);

        Assert.Null(HistorySources.Nothing.Newest);

        var pointIsNewer = HistorySources.Nothing with { Versions = [version], Points = [point] };
        Assert.Equal(point.TreeId, pointIsNewer.Newest!.TreeId);

        var versionIsNewer = HistorySources.Nothing with
        {
            Versions = [version with { WhenUtc = t0.AddHours(3) }],
            Points   = [point],
        };
        Assert.Equal(version.TreeId, versionIsNewer.Newest!.TreeId);

        // One list only, either way round.
        Assert.Equal(point.TreeId,   (HistorySources.Nothing with { Points   = [point]   }).Newest!.TreeId);
        Assert.Equal(version.TreeId, (HistorySources.Nothing with { Versions = [version] }).Newest!.TreeId);
    }

    private static GitWorkspace Armed()
    {
        var ws = new GitWorkspace();
        File.WriteAllText(ws.GlobalConfig,
            "[user]\n\tname = A Designer\n\temail = designer@example.invalid\n");

        var armed = WorkspaceArming.Arm(ws.Root, CheckpointOrigin.SavePoint, true, null, true);
        Assert.True(armed.Armed);
        return ws;
    }

    /// <summary>One entry of each origin, each over content that actually changed — a boundary whose
    /// tree matches the newest entry records nothing, which would leave the fixture short.</summary>
    private static void Write(GitWorkspace ws, GitCommand git, CheckpointOrigin origin, string? label)
    {
        ws.Write("cells/a/thing.csch", $"{origin} {Guid.NewGuid():N}");
        var taken = WorkspaceCheckpoints.Take(git, origin, label, attended: false,
                                              forceRecord: true);
        Assert.True(taken.Recorded, $"{origin} recorded nothing");
    }

    /// <summary>A sink that keeps what it was told, so a gate can assert on it. Local rather than
    /// shared, exactly as the other revision gates' own sinks are.</summary>
    private sealed class SilentSink : IMessageSink
    {
        public List<string> Texts { get; } = [];
        public void Post(MessageLevel level, string text, string? filePath = null) => Texts.Add(text);
        public void Clear() => Texts.Clear();
    }

    private static RestorePoint Point(long sequence, bool kept) => new(
        Reference: CheckpointReferences.NameFor(sequence),
        CommitId:  new string('a', 40),
        TreeId:    new string('b', 40),
        Sequence:  sequence,
        TakenUtc:  DateTimeOffset.UtcNow,
        Origin:    CheckpointOrigin.SavePoint,
        Label:     $"entry {sequence}",
        Intent:    null,
        Kept:      kept,
        LeftOut:   []);

    private static IEnumerable<ITool> ToolsIn(IDock dock)
    {
        foreach (var child in dock.VisibleDockables ?? [])
        {
            if (child is ITool tool) yield return tool;
            if (child is IDock inner)
                foreach (var t in ToolsIn(inner)) yield return t;
        }

        if (dock is IRootDock root)
            foreach (var window in root.Windows ?? [])
                if (window.Layout is { } layout)
                    foreach (var t in ToolsIn(layout)) yield return t;
    }

    private static IEnumerable<IDockable> DocksIn(IDock dock)
    {
        foreach (var child in dock.VisibleDockables ?? [])
        {
            yield return child;
            if (child is IDock inner)
                foreach (var d in DocksIn(inner)) yield return d;
        }
    }

    /// <summary>The built CLI, or null on a tree nobody has built yet.</summary>
    private static class CliUnderTest
    {
        public static string? Dll { get; } = Find();

        private static string? Find()
        {
            var dir = AppContext.BaseDirectory;
            while (dir is not null)
            {
                string candidate = Path.Combine(dir, "src", "Cli", "bin", "Debug", "net10.0", "CircuitRF.Cli.dll");
                if (File.Exists(candidate)) return candidate;
                dir = Path.GetDirectoryName(dir);
            }
            return null;
        }
    }

    private static (int Code, string Out, string Err) RunCli(
        string dll, string homeDir, string workingDir, string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory       = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        psi.ArgumentList.Add(dll);
        foreach (string a in args) psi.ArgumentList.Add(a);

        // The same throwaway identity and configuration the in-process side is reading, so the two
        // are comparing one repository rather than one repository and the developer's own git.
        psi.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(homeDir, "gitconfig");
        psi.Environment["GIT_CONFIG_SYSTEM"] = Path.Combine(homeDir, "no-such-system-config");
        psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        psi.Environment["HOME"] = homeDir;
        psi.Environment["USERPROFILE"] = homeDir;
        psi.Environment[CircuitRF.Design.UserStateDirectory.EnvironmentVariable] = homeDir;

        using var p = Process.Start(psi)!;
        string o = p.StandardOutput.ReadToEnd();
        string e = p.StandardError.ReadToEnd();
        p.WaitForExit(180_000);
        return (p.ExitCode, o, e);
    }
}

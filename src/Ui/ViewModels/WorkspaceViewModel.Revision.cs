using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Dock.Model.Core;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Design.Revision;
using CircuitRF.Ui.Messages;
using CircuitRF.Ui.Revision;
using CircuitRF.Ui.Views.Dialogs;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// <b>The window half of RC-5, RC-6 and RC-7</b> — the affordances, the two automatic boundaries,
/// going back, and the explicit commit (<c>docs/design/revision-control.md</c> §5.2, §5.3, §5.8;
/// R-rc5-4c, R-rc5-12, R-rc5-21, R-rc5-22, R-rc7-1, R-rc7-17).
///
/// <para><b>Two commands and ONE panel</b> (RC-10 R-rc10-1, §5.10 — superseding R-rc7-9's two).
/// Keep This State… writes a restore point; Keep This Version… writes a version the designer titled.
/// They look similar and are not: one is a safety-net entry nobody else ever sees, the other is what
/// gets shared, and merging the COMMANDS would merge the histories, which §5 exists to prevent. The
/// two kinds of ENTRY stay distinct in one list, under one mark — which is the distinction §5 was
/// always about, and which rev 2 spent as an argument for two windows.</para>
///
/// <para><b>Kept in its own file because none of it is about editing a design.</b> Everything that
/// decides anything lives below the firewall in <see cref="WorkspaceHistoryService"/> and the
/// <c>src/Design</c> types under it — the same functions <c>circuitrf history</c> calls, which is
/// what stops a headless run and a window disagreeing about what a boundary does.</para>
/// </summary>
public partial class WorkspaceViewModel
{
    private WorkspaceHistoryService? _history;

    /// <summary>
    /// The service, built on first use so a workspace nobody ever asks about costs nothing.
    /// </summary>
    private WorkspaceHistoryService History
    {
        get
        {
            if (_history is null)
            {
                _history = new WorkspaceHistoryService(Messages);

                // ONE panel since RC-10, which is also the end of a whole class of defect: several of
                // the operations that raise this change both kinds of entry — turning recording off and
                // on puts a gap row in the list, and a restore adds the entry it took first — and every
                // bug in this area so far was one of the two panels left out of a refresh.
                _history.Changed += RefreshHistoryPanel;
            }
            return _history;
        }
    }

    /// <summary>The workspace folder, or null when there is no workspace open.</summary>
    private string? WorkspaceRootDir
        => CurrentWorkspacePath is { } cws ? Path.GetDirectoryName(cws) : null;

    // ── Nothing to record (owner, 2026-09-08) ─────────────────────────────────────────────────────

    /// <summary>
    /// <b>Whether either keep action would record anything.</b> Reported by the two commands' own
    /// <c>CanExecute</c> and by the panel's two header buttons, which is the whole of the fix: both used
    /// to open their dialog, take a title, and then create nothing, because the tree test that declines
    /// a duplicate lives in <c>GitCheckpoint.Record</c> — after the dialog.
    ///
    /// <para><b>Four terms, and three of them force it TRUE.</b> The signal on its own is
    /// <c>WrittenSinceLastEntry</c>, which costs no git (see its own note for why a tree comparison was
    /// not an option); the other three are the states in which it would say the wrong thing:</para>
    ///
    /// <list type="bullet">
    ///   <item><description><b>Held, off, or failing.</b> Those already have their own answers and are
    ///   deliberately not this one — held stays visible and refuses out loud (R-rc6-8), and with no git
    ///   the whole affordance is hidden instead. Conflating <i>nothing to record</i> with <i>not allowed
    ///   to record</i> would undo both.</description></item>
    ///   <item><description><b>A workspace with no history yet.</b> The first entry always records, and
    ///   there is no newest entry for a write to have happened since.</description></item>
    ///   <item><description><b>Unsaved work anywhere.</b> This is the case that would otherwise be
    ///   dangerous: nothing has been written into the workspace, so the signal is false, and the one
    ///   action a designer with an unsaved schematic reaches for would be unavailable at the moment they
    ///   most want it. A keep now SAVES those documents first, so it genuinely will record
    ///   something.</description></item>
    /// </list>
    /// </summary>
    private bool CanKeepAnything()
        => CurrentWorkspacePath is not null
        && (!_recordingIsOn
         || !_workspaceHasAHistory
         || History.WrittenSinceLastEntry
         || HasAnyDirtyWork(includeFloated: false));

    /// <summary>Whether circuitRF is recording into this workspace at all. Cached from the read
    /// <see cref="RefreshHistoryPanel"/> already performs — this must never make a control's enabled
    /// state cost a subprocess.</summary>
    private bool _recordingIsOn = true;

    /// <summary>
    /// Whether this workspace has any recorded entry. Cached for the same reason, and asked only where
    /// the answer can have changed: <b>a write cannot create the first entry</b>, so the save path does
    /// not ask.
    /// </summary>
    private bool _workspaceHasAHistory;

    /// <summary>
    /// Brings the two keep affordances up to date — the File menu's commands and, when it exists, the
    /// panel's pair of header buttons. <b>No repository is read here</b>; everything it needs is either
    /// in hand or a field.
    /// </summary>
    private void RefreshKeepAffordances()
    {
        bool can = CanKeepAnything();

        KeepThisStateCommand.NotifyCanExecuteChanged();
        KeepThisVersionCommand.NotifyCanExecuteChanged();

        OnPropertyChanged(nameof(KeepThisStateTooltip));
        OnPropertyChanged(nameof(KeepThisVersionTooltip));

        if (_factory.HistoryTool is { } tool) tool.CanKeepAnything = can;
    }

    /// <summary>
    /// What the File menu's two items say. <b>The reason is on the control</b> — a greyed item with
    /// nothing said about it is indistinguishable from a feature that was never built (R-rc6-8's
    /// argument, one control over).
    /// </summary>
    public string KeepThisStateTooltip => CanKeepAnything()
        ? "Keep this workspace as it is now, with a line saying what it is, so you can come back to it."
        : HistoryMessages.NothingToKeepYet;

    /// <summary>The same, for the version.</summary>
    public string KeepThisVersionTooltip => CanKeepAnything()
        ? "Record this workspace as a version, under a title you write — what you come back to, and what you send out."
        : HistoryMessages.NothingToKeepYet;

    /// <summary>
    /// R-rc5-4a. Called by every path that writes a file into the open workspace.
    ///
    /// <para><b>This is what keeps a colleague's glance from creating a repository on a share.</b>
    /// An unarmed workspace arms on close only if circuitRF itself wrote something during the
    /// session, and that fact has to come from the edit session rather than from the disk — a file
    /// manager touching the folder is not circuitRF editing a design.</para>
    /// </summary>
    public void NoteWorkspaceWrite()
    {
        // Only the FIRST write after a boundary changes anything a control is showing — the signal is a
        // latch that a boundary clears — so the notification is guarded rather than raised on every save
        // of every document. This runs on the save path of every document kind in the application.
        bool alreadyKnown = History.WrittenSinceLastEntry;

        History.NoteWorkspaceWrite();

        if (!alreadyKnown) RefreshKeepAffordances();
    }

    /// <summary>
    /// <b>Somebody else's process wrote into this workspace</b> — an agent's batch through
    /// <c>circuitrf serve</c>. It is not circuitRF editing a design, so it must not arm the repository
    /// (R-rc5-4a); it does change what a keep would find, so the two keep actions come back.
    /// </summary>
    private void NoteWorkspaceChangedUnderneath()
    {
        History.NoteWorkspaceChangedUnderneath();
        RefreshKeepAffordances();
    }

    // ── The explicit save-point (R-rc5-4c, §5.3's second boundary) ────────────────────────────────

    /// <summary>
    /// File ▸ <b>Keep This State…</b>
    ///
    /// <para><b>Beside the save commands, because that is where a user looking for it will be</b>
    /// (R-rc5-4c) — §5.3 describes it as something the user asks for. It is not a save: it keeps the
    /// state the workspace is in so the designer can come back to it.</para>
    ///
    /// <para><b>It is the interactive moment §8.2's guard is asked at</b> (R-rc5-15a). A close is at
    /// the moment the designer asked to leave and a batch is headless, so both of those leave an
    /// unexpectedly large file out and record that they did; this is where the question finally gets
    /// put to somebody.</para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanKeepAnything))]
    private async Task KeepThisState(Window? owner)
    {
        if (WorkspaceRootDir is not { } root) return;

        // R-rc5-21's claim, and stronger here than at a close: THE DESIGNER IS NAMING THIS STATE
        // (owner, 2026-09-08). What a save-point records is what is on disk, so with a schematic
        // unsaved this recorded the PREVIOUS content under the title just written — an entry a
        // designer believes holds this afternoon and which holds this morning. Nothing about the
        // result says so, and going back to it later is how they find out.
        //
        // The same prompt a close, an archive and a workspace copy already use — not a second save
        // path — and a cancelled prompt records nothing at all.
        if (owner is not null && HasAnyDirtyWork(includeFloated: false)
            && !await PromptSaveBeforeClose(owner, "keeping this state", includeFloated: false))
            return;

        var dialog = new KeepThisStateDialog();
        dialog.Present(LargeFilesAwaitingAnAnswer(root), IsFirstRecording(root));

        var choice = owner is null
            ? new KeepThisStateChoice(null, [], [])
            : await dialog.ShowDialog<KeepThisStateChoice?>(owner);

        if (choice is null) return;

        // The pattern goes in FIRST, so this recording already honours it and the next boundary does
        // not ask again. Appended, never rewritten (R-rc3-11b) — the file belongs to the workspace.
        foreach (string pattern in choice.NeverInclude)
            LargeFileGuard.AppendIgnorePattern(root, pattern);

        History.TakeSavePoint(root, choice.Label, choice.Note);
        RefreshHistoryPanel();
    }

    /// <summary>
    /// What the guard has to ask about at this moment: over the threshold, not already kept, and not
    /// already excluded. An empty answer is the ordinary case and leaves the dialog one field.
    /// </summary>
    private IReadOnlyList<LargeFile> LargeFilesAwaitingAnAnswer(string root)
    {
        if (GitCommand.For(root) is not { } git || !git.IsRepositoryRoot()) return [];
        return LargeFileGuard.Find(git, RestorePoints.Newest(git)?.TreeId);
    }

    /// <summary>
    /// R-rc5-17a. Whether this is the FIRST recording into a workspace that already exists — which is
    /// a different operation from every later one, and is why the dialog summarises by pattern rather
    /// than naming hundreds of files.
    /// </summary>
    private static bool IsFirstRecording(string root)
        => GitCommand.For(root) is { } git
        && (!git.IsRepositoryRoot() || CheckpointReferences.List(git).Count == 0);

    // ── The close boundary (§5.3's third, R-rc5-21, R-rc5-22) ─────────────────────────────────────

    /// <summary>
    /// R-rc5-4's third boundary — <b>the one that reliably exists in every session</b>, including the
    /// ones where the designer never thought about history.
    ///
    /// <para><b>Called AFTER the workspace file has been written</b> (R-rc5-21), or the entry keeps a
    /// workspace file one save out of date. That is ordering, it costs nothing, and it is invisible
    /// when wrong: the restored workspace simply comes up with slightly stale configuration and
    /// nobody connects it to the close.</para>
    ///
    /// <para><b>It must not make quitting feel broken</b> (R-rc5-22). This is the one boundary that
    /// sits in front of the user, and it is a whole-workspace recording over what may be several
    /// multi-megabyte layouts. It is measured rather than asserted: the elapsed time goes to the
    /// trace listener where a build can read it, and the process does not exit until the recording has
    /// completed or failed — a failure still reports (R-rc5-10).</para>
    /// </summary>
    private void TakeCloseCheckpoint(string? cwsPath)
    {
        if (cwsPath is null) return;
        if (Path.GetDirectoryName(cwsPath) is not { Length: > 0 } root) return;

        var timer = Stopwatch.StartNew();
        History.TakeCloseCheckpoint(root);
        timer.Stop();

        LastCloseCheckpointMs = timer.Elapsed.TotalMilliseconds;
        System.Diagnostics.Trace.WriteLine($"[circuitRF] close restore point: {timer.Elapsed.TotalMilliseconds:F0} ms");

        // RC-6 R-rc6-4a. The one housekeeping pass — a retention sweep and then packing — AFTER the
        // close entry and in the same window. It is here rather than at each of this method's three
        // callers because "at most once per session" is a property of the session, and three callers
        // agreeing about it is how it becomes true in two of them.
        //
        // A session that recorded nothing does neither (§12 Q24): a colleague's glance at a shared
        // workspace must not run a sweep under the reader's retention preference over the owner's
        // restore points.
        LastCloseHousekeeping = History.CloseHousekeeping(root);
    }

    /// <summary>What the close-time sweep and pack did, if anything. R-rc0-8's measurement, read by
    /// the write-up rather than asserted in a timing test.</summary>
    public HousekeepingResult? LastCloseHousekeeping { get; private set; }

    /// <summary>What the last close boundary cost, in milliseconds. R-rc0-8's measurement, read by
    /// the write-up rather than asserted in a timing test.</summary>
    public double LastCloseCheckpointMs { get; private set; }

    // ── The panel (R-rc5-4c) ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Rebuilds the restore-point list. <b>Called on every boundary and every workspace switch</b> —
    /// the panel holds no list of its own, because a stale one would offer a designer a way back to a
    /// state that is no longer there.
    /// </summary>
    private void RefreshHistoryPanel()
    {
        // FIRST, and OUTSIDE the panel guard below. Whether there is anything to keep is a fact about
        // the workspace, and the two keep actions are on the File menu as well as in this panel — a
        // designer with every tool panel closed still reaches them. Both reads are ones this method was
        // already paying for when the panel existed.
        var recording         = History.State(WorkspaceRootDir);
        _recordingIsOn        = recording == RecordingState.On;
        _workspaceHasAHistory = WorkspaceHistoryService.HasHistory(WorkspaceRootDir);
        RefreshKeepAffordances();

        if (_factory.HistoryTool is not { } tool) return;

        // Wired PER INSTANCE, not once per session. A workspace switch goes through
        // CircuitRfDockFactory.CreateDefaultLayout, which builds a FRESH HistoryTool — and the restore
        // below performs exactly such a switch on its way back. A `bool _historyPanelWired` therefore
        // stayed true across the replacement and left every callback on the new panel null, so each of
        // its buttons became a control that draws and does nothing: reported against "come forward
        // again", which is simply the one a designer is looking straight at the moment the reload ends.
        //
        // Comparing the instance is what makes this correct for the paths that do not exist yet, since
        // any future caller that replaces the tools is covered without having to remember a flag.
        if (!ReferenceEquals(_wiredHistoryTool, tool))
        {
            _wiredHistoryTool = tool;

            // Both dialogs hand the keyboard BACK to this panel when they close — kept or cancelled
            // alike (owner, 2026-09-07). The gesture started here, so this is where the next keystroke
            // should land; a cancelled dialog dropping focus onto the workspace is the version that was
            // reported, and confirming has exactly the same claim on it.
            tool.SavePointRequested   = () => _ = ThenFocusThePanel(tool, KeepThisState);
            tool.KeepVersionRequested = () => _ = ThenFocusThePanel(tool, KeepThisVersion);

            // ONE go-back over both kinds of row. A version goes back through RC-5's restore, so there
            // is no second restore implementation and everything R-rc5-12c guarantees applies to both.
            tool.GoBackRequested = entry =>
            {
                if (entry.Version is { } version)  _ = GoBackToVersion(version);
                else if (entry.Point is { } point) _ = GoBackTo(point);
            };

            tool.KeepRequested      = point => { History.Keep(WorkspaceRootDir, point); };

            // Tidying away and bringing back are ONE reference write each, and both used to be followed
            // by a full re-read of the repository on the UI thread — 18 ms of work behind 180 ms of
            // stall, on a sixty-entry workspace, growing with the list. Neither can have changed the
            // versions, the incoming set, the sharing set or the annotations; what changed is a boolean
            // the panel is already holding. So the panel redraws itself, exactly as a filter checkbox
            // does, and only a FAILED write takes the long way round.
            tool.BringBackRequested = point =>
            {
                if (History.BringBackThinned(WorkspaceRootDir, point)) tool.MarkTidiedAway(point.CommitId, false);
                else RefreshHistoryPanel();
            };
            tool.ComeForwardRequested  = point => _ = GoBackTo(point);
            tool.CopyIdentityRequested = CopyToClipboard;

            tool.SelectionChanged += version =>
                tool.SetChanges(version is null ? [] : History.ChangesIn(WorkspaceRootDir, version));

            tool.CompareRequested = entry =>
                tool.SetChanges(History.CompareWithWorkspace(WorkspaceRootDir, entry));

            // §5.11's three corrections, on the menu R-rc10-17 built for them.
            tool.RenameRequested  = point => _ = RenameRestorePoint(point);
            tool.LetGoRequested   = point => LetRestorePointGo(tool, point);
            tool.CorrectRequested = version => _ = CorrectWhatYouWrote(version);

            // R-rc10-8. The filter is per-USER view state, so a change to it is a change to the
            // `.cwsuser` — written through the one workspace-save path, never by a second writer.
            //
            // RC-11: it does NOT re-read the repository. The panel has already re-filtered what it
            // holds by the time this runs (HistoryTool.Redraw), so all that is left here is the save —
            // which is what turned a checkbox that started a dozen git subprocesses into one that
            // does not touch the repository at all.
            tool.FilterChanged += ScheduleCwsSave;

            tool.ApplyStoredFilter(_storedHistoryFilter);
        }

        // RC-6 R-rc6-4. The list carries the entries retention TIDIED AWAY as well as the live ones,
        // each marked. A state that silently vanished from the list is indistinguishable to a designer
        // from one that was destroyed — and this one has not been, and can be brought back.
        //
        // R-rc6-8/R-rc6-10: the actions stay visible whatever the state, and the sentence is what makes
        // a hold or an off state legible instead of looking like a feature that was never built.
        //
        // RC-11: ONE read, handed to the panel whole. The panel filters it itself, so this runs at a
        // boundary and a workspace switch and nowhere else — a filter toggle used to come through here
        // and re-read everything, including a second full pass over the restore points for the empty
        // line's count.
        // Read ONCE and reused: the recording state was already established above, and asking git for
        // it a second time in one refresh is the shape of cost RC-11 took out of this panel.
        var sources = History.ReadEntries(WorkspaceRootDir);

        tool.SetSources(
            sources,
            WorkspaceRootDir is not null,
            _recordingIsOn ? "" : HoldMessages.IndicatorDetailFor(recording));

        // WHAT THE WORKSPACE HOLDS, when that is known for nothing (owner, 2026-09-08). With nothing
        // written since the newest entry was recorded, the files on disk ARE that entry's content — so
        // going back to it, or to any other entry over the same tree, is a whole reload that changes
        // nothing, and the panel withdraws the offer. Empty means unknown, and unknown offers every row:
        // a button withheld when it was needed is the failure §1.4 is about, and one offered needlessly
        // costs a reload.
        tool.WorkspaceTreeId = History.WrittenSinceLastEntry ? "" : sources.Newest?.TreeId ?? "";

        // R-rc10-18. The way forward, reported on arrival and by name. Cleared when the workspace
        // changes, because it is a sentence about one restore in one session.
        tool.WayForward = _wayForward;

        RefreshRecordingIndicator();
    }

    /// <summary>
    /// The panel instance whose callbacks are already attached. <b>Which instance</b>, rather than
    /// <b>whether</b> — handlers added on every refresh would fire once per refresh, which is silent
    /// and gets worse the longer the session runs, while a session-wide flag misses the tool the next
    /// workspace switch builds in its place.
    /// </summary>
    private ViewModels.Dock.HistoryTool? _wiredHistoryTool;

    /// <summary>
    /// Runs one of the two keep dialogs on this workspace's window and returns keyboard focus to the
    /// History panel afterwards. <b>Only the panel's own buttons come through here</b> — the File menu
    /// calls the same dialogs directly, and a menu command has no panel to go back to.
    /// </summary>
    private async Task ThenFocusThePanel(ViewModels.Dock.HistoryTool tool, Func<Window?, Task> keep)
    {
        await keep(Views.WorkspaceLocator.WindowFor(this));
        tool.RequestActivationFocus();
    }

    /// <summary>
    /// R-rc10-8's stored filter, read out of the <c>.cwsuser</c> on open and handed to the panel when
    /// it is first wired. Held here because the panel instance outlives a workspace switch while the
    /// filter does not.
    /// </summary>
    private HistoryFilter _storedHistoryFilter = HistoryFilter.Default;

    /// <summary>
    /// R-rc10-18's line, for this session and this workspace only. <b>Written nowhere</b> — a restore
    /// is a moment rather than a state, and R-rc10-4 forbids this brief from changing what is stored
    /// in any case.
    /// </summary>
    private WayForward? _wayForward;

    /// <summary>
    /// <b>A corrected title must not survive in this session's own sentence</b> (owner, 2026-09-07).
    ///
    /// <para>The way-forward line is built from a copy of the title taken at restore time. Correct that
    /// title and the copy is an orphan — and it is displayed in the very panel the correction was made
    /// in, which is the most conspicuous place for deleted wording to reappear. The record matches on
    /// identity and returns itself unchanged when the correction is about some other entry.</para>
    ///
    /// <para><b>Re-pointed at the new identity as well</b>, because both correction paths that rewrite
    /// a commit change it; a link left on the old id would silently stop matching, and the leak would
    /// come back on the second correction rather than the first.</para>
    /// </summary>
    private void RetitleTheWayForward(string wasAt, string? nowAt, string title)
    {
        if (nowAt is null || _wayForward is not { } way) return;

        var next = way.Retitled(wasAt, title);
        if (ReferenceEquals(next, way)) return;

        _wayForward = next with { WentBackToId = nowAt };
    }

    /// <summary>Which workspace <see cref="_wayForward"/> is about. A sentence describing one
    /// afternoon in one workspace must not follow the designer into the next one.</summary>
    private string? _wayForwardRoot;

    /// <summary>
    /// R-rc10-8. What the <c>.cwsuser</c> says the filter was, or the default — which is what a
    /// workspace opened for the first time shows, and what every file written before this existed
    /// means.
    /// </summary>
    private HistoryFilter ReadStoredHistoryFilter()
    {
        if (CurrentWorkspacePath is not { } cws) return HistoryFilter.Default;

        var stored = TryLoadCws(cws).HistoryFilter;
        return stored is null
            ? HistoryFilter.Default
            : new HistoryFilter(stored.Versions, stored.SavePoints, stored.AiBatches,
                                stored.Automatic, stored.TidiedAway, stored.Search ?? "");
    }

    /// <summary>The panel's current filter, for the workspace write. Null when it is the default, so a
    /// designer who never touched it gets no row in the file — the same rule every other per-user field
    /// follows.</summary>
    internal Design.Workspace.CwsHistoryFilter? HistoryFilterToPersist()
    {
        if (_factory.HistoryTool?.Filter is not { } f || f == HistoryFilter.Default) return null;

        return new Design.Workspace.CwsHistoryFilter
        {
            Versions   = f.Versions,
            SavePoints = f.SavePoints,
            AiBatches  = f.AiBatches,
            Automatic  = f.Automatic,
            TidiedAway = f.TidiedAway,
            Search     = f.Search.Length > 0 ? f.Search : null,
        };
    }

    /// <summary>
    /// R-rc10-17. The identity, on the clipboard.
    ///
    /// <para>The one string a designer can hand to somebody helping them when circuitRF's own window
    /// cannot answer the question (§4.1). Silent on success: a message saying "copied" is a message
    /// nobody needs twice.</para>
    /// </summary>
    private void CopyToClipboard(string text)
    {
        // THIS workspace's window first. The identifier being copied belongs to this workspace's
        // history, and with two workspace windows open the first one the locator happens to enumerate
        // is not reliably the one the designer right-clicked in. The fallback keeps the single-window
        // case working before the window is located.
        var clipboard = Views.WorkspaceLocator.WindowFor(this)?.Clipboard
                     ?? Views.WorkspaceLocator.AllWindows().FirstOrDefault()?.Clipboard;

        if (clipboard is not null) _ = clipboard.SetTextAsync(text);
    }

    // ── RC-7: keeping a version, and the browser (§5.2, §5.5, §6.3) ───────────────────────────────

    /// <summary>
    /// File ▸ <b>Keep This Version…</b> — R-rc7-1's explicit commit.
    ///
    /// <para><b>Beside Keep This State…, and the pair is deliberate.</b> They are different operations
    /// with different audiences: a save-point is a safety net entry nobody else ever sees, and a
    /// version is what a designer writes down on purpose and sends out. Merging the two commands would
    /// merge the two histories, which §5 exists to prevent.</para>
    ///
    /// <para><b>Two things the dialog says before the button is pressed.</b> Whether this version will
    /// record having been brought back from an earlier state (R-rc7-6) — discovering that in the list
    /// afterwards is how a history comes to read as a change of mind — and §8.3's sentence about
    /// rewriting (R-rc7-21), which is stated and never offered.</para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanKeepAnything))]
    private async Task KeepThisVersion(Window? owner)
    {
        if (WorkspaceRootDir is not { } root) return;

        // The same first step as Keep This State…, and with more at stake: a version is what gets
        // SENT, so one recorded a save out of date is wrong on somebody else's machine too.
        if (owner is not null && HasAnyDirtyWork(includeFloated: false)
            && !await PromptSaveBeforeClose(owner, "keeping this version", includeFloated: false))
            return;

        var dialog = new KeepThisVersionDialog();
        dialog.Present(RestoreProvenance.Read(root) is { } state
                           ? new RestoredFrom(state.Label, state.TakenUtc)
                           : null);

        var choice = owner is null
            ? new KeepThisVersionChoice(null)
            : await dialog.ShowDialog<KeepThisVersionChoice?>(owner);

        if (choice is null) return;

        History.KeepVersion(root, choice.Title, note: choice.Note);
        RefreshHistoryPanel();
    }

    // ── RC-11: correcting what you wrote (§5.11) ─────────────────────────────────────────────────

    /// <summary>
    /// §5.11 case (a). <b>A restore point's label is its author's to correct</b> (R-rc11-3).
    ///
    /// <para>Not §8.3's rewriting in any sense, and the mechanism already exists for another purpose:
    /// a checkpoint is parentless, nothing chains to it, no copy takes it and no send carries it — so
    /// a rename is a new parentless commit over the identical tree and one reference update, exactly
    /// what marking one <i>keep</i> has done since RC-5.</para>
    /// </summary>
    /// <param name="over">
    /// What the dialog is modal over. Null takes this view model's own window, which is every caller
    /// but §5.11's review — that one is <b>already</b> modal over the window, so a correction opened
    /// against the window would be a modal behind the one blocking it.
    /// </param>
    private async Task RenameRestorePoint(RestorePoint point, Window? over = null)
    {
        if ((over ?? Views.WorkspaceLocator.WindowFor(this)) is not { } owner) return;

        var dialog = new CorrectWhatYouWroteDialog(CorrectionCase.RestorePointLabel, point.Label, null,
                                                   point.Note);
        if (await dialog.ShowDialog<CorrectionChoice?>(owner) is not { } choice) return;

        // R-rc10-18's sentence holds its own COPY of the title, so it has to follow the correction or
        // it goes on reading the deleted wording back to the designer in the same panel they corrected
        // it in. See RestoreProvenance.Retitle for the copy bound for disk.
        RetitleTheWayForward(point.CommitId,
                             History.Rename(WorkspaceRootDir, point, choice.Text, choice.Note),
                             choice.Text);

        RefreshHistoryPanel();
    }

    /// <summary>
    /// §5.11 case (a). <b>Letting an entry go is exactly what tidying up does</b> (R-rc11-5) —
    /// journalled, reversible by the same <i>bring this back</i>, and freeing nothing until §5.6a's
    /// explicit reclaim.
    ///
    /// <para><b>There is no confirmation dialog and that is deliberate.</b> A prompt would be asking a
    /// designer to steel themselves for something that is not destructive: the entry stays in the list
    /// under <i>tidied away</i>, the state is still there, and the message says so. A confirmation
    /// here would teach the wrong thing about what the action does.</para>
    /// </summary>
    private void LetRestorePointGo(ViewModels.Dock.HistoryTool tool, RestorePoint point)
    {
        // See the wiring note beside BringBackRequested: the write is the cheap half and the refresh
        // was the whole of the stall. On success the panel redraws from what it holds; on failure it
        // re-reads, because a panel that disagrees with the repository is the one that is wrong.
        if (History.Forget(WorkspaceRootDir, point)) tool.MarkTidiedAway(point.CommitId, true);
        else RefreshHistoryPanel();
    }

    /// <summary>
    /// §5.11 cases (b) and (c). <b>One action, and the computation decides which of the two it
    /// is</b> (R-rc11-2, R-rc11-7, R-rc11-13).
    ///
    /// <para>A version nobody else has seen is retitled outright; one that has left the machine takes
    /// a correction instead, with the sentence that may not be softened on the face of the dialog. The
    /// designer is not asked to know which — <see cref="VersionSharing"/> is, and where it cannot
    /// answer it says <i>shared</i>, because a correction refused is recoverable and an erasure
    /// believed is not.</para>
    /// </summary>
    /// <inheritdoc cref="RenameRestorePoint" path="/param[@name='over']"/>
    private async Task CorrectWhatYouWrote(HistoryVersion version, Window? over = null)
    {
        if ((over ?? Views.WorkspaceLocator.WindowFor(this)) is not { } owner) return;
        if (WorkspaceRootDir is not { } root) return;

        bool canRetitle = History.CanCorrectTitle(root, version);

        // The correction already in place, if there is one — so case (c) opens on it rather than on a
        // blank field, and shows the wording underneath it.
        var existing = History.CorrectionOn(root, version);

        var dialog = canRetitle
            ? new CorrectWhatYouWroteDialog(CorrectionCase.UnsharedTitle, version.Title, null,
                                            version.Note)
            : new CorrectWhatYouWroteDialog(CorrectionCase.SharedTitle,
                                            existing?.Title ?? version.Title,
                                            existing is null ? null : version.Title,
                                            // §5.12. The field opens on what the ROW is showing — the
                                            // corrected note where there is one, the recorded note
                                            // where there is not — so refining a correction never
                                            // starts from a blank field.
                                            existing?.Note is { Length: > 0 } corrected
                                                ? corrected : version.Note,
                                            existing is null ? null : version.Note);

        if (await dialog.ShowDialog<CorrectionChoice?>(owner) is not { } choice) return;

        // An annotation leaves the commit alone, so the entry keeps its identity; a correction rewrites
        // it and hands back the new one.
        string? nowAt = canRetitle
            ? History.CorrectTitle(root, version, choice.Text, choice.Note)
            : History.Annotate(root, version, choice.Text, choice.Note) ? version.CommitId : null;

        RetitleTheWayForward(version.CommitId, nowAt, choice.Text);

        RefreshHistoryPanel();
    }

    /// <summary>
    /// R-rc7-17. <b>Going back to a version is RC-5's restore with that version's tree as the
    /// source</b> — there is no second restore implementation, so everything R-rc5-12c guarantees
    /// applies unchanged, including the checkpoint of the state being replaced.
    ///
    /// <para>Both halves of R-rc5-12b belong here for the same reason they do for a restore point:
    /// unsaved work is offered up first, and the open documents are reloaded with their undo stacks
    /// discarded afterwards.</para>
    /// </summary>
    public async Task GoBackToVersion(HistoryVersion version)
    {
        var window = Views.WorkspaceLocator.WindowFor(this);
        if (WorkspaceRootDir is not { } root) return;

        if (window is not null && HasAnyDirtyWork(includeFloated: false)
            && !await PromptSaveBeforeClose(window, "going back to an earlier version", includeFloated: false))
            return;

        // The busy cursor covers the RESTORE as well as the reload: the write and its pre-restore
        // checkpoint are synchronous git work on the UI thread, and they are most of the wait.
        using var busy = await Views.BusyCursorScope.WhileAsync(window);

        if (History.GoBackToVersion(root, version) is not { Ok: true, PreRestore: { } kept } outcome)
            return;

        // R-rc10-18. Recorded before the reload — the refresh at the end of it is what puts it on
        // screen, and the narrow reload leaves the panel instance it goes to alone.
        _wayForward     = new WayForward(version.Title, kept, version.CommitId, version.WhenUtc,
                                         version.TreeId);
        _wayForwardRoot = root;

        await ReloadChangedDocuments(outcome.ChangedPaths);
    }

    // ── Going back (§5.8) ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Puts the workspace back to one restore point.
    ///
    /// <para><b>Two halves, and the second is the one that is silent if missed</b> (R-rc5-12b):</para>
    /// <list type="bullet">
    ///   <item><description><b>Unsaved work is offered up first</b>, through the prompt that already
    ///   guards a close, an archive and a workspace copy — reused, not rewritten. A restore is built
    ///   from what is on disk; performed on top of dirty documents it produces a workspace matching
    ///   neither state.</description></item>
    ///   <item><description><b>Open documents are reloaded and their undo stacks discarded.</b> An
    ///   undo after a restore would re-apply the last few minutes of the REPLACED state onto the
    ///   RESTORED file, producing a document that existed at no moment ever: well-formed, openable,
    ///   and wrong. §1.3 names that as the failure this whole feature is written against, and it
    ///   would be this feature causing it.</description></item>
    /// </list>
    /// </summary>
    public async Task GoBackTo(RestorePoint point)
    {
        var window = Views.WorkspaceLocator.WindowFor(this);
        if (WorkspaceRootDir is not { } root) return;

        if (window is not null && HasAnyDirtyWork(includeFloated: false)
            && !await PromptSaveBeforeClose(window, "going back to an earlier state", includeFloated: false))
            return;

        // As in GoBackToVersion: the restore itself is synchronous git work on the UI thread, so the
        // cursor goes on before it rather than before the reload.
        using var busy = await Views.BusyCursorScope.WhileAsync(window);

        if (History.Restore(root, point) is not { Ok: true, PreRestore: { } kept } outcome) return;

        // R-rc10-18, §12 Q35. THE WAY FORWARD, offered where the way back was taken. It creates
        // nothing — the entry already exists, because §5.8's restore takes it before it writes a single
        // file — and going to it is an ordinary restore with an ordinary checkpoint of its own.
        //
        // Before RC-10 this reassurance existed, was correct, and was filed in the panel the designer
        // had not opened: a restore begun from the Versions panel left them looking at a window with no
        // evidence that the afternoon they had just replaced still existed.
        _wayForward     = new WayForward(point.Label, kept, point.CommitId, point.TakenUtc,
                                         point.TreeId);
        _wayForwardRoot = root;

        await ReloadChangedDocuments(outcome.ChangedPaths);
    }

    /// <summary>
    /// <b>The fallback under <see cref="ReloadChangedDocuments"/></b>, and nothing calls it directly
    /// any more: reopening the whole workspace is what a restore did for every change until
    /// 2026-09-08, and it is right only when what changed is not known.
    ///
    /// <para>Reopening the workspace is what discards the undo stacks, because the edit-session
    /// registry is cleared as part of the switch. Nothing here reaches into a stack to trim it — an
    /// undo stack describing a file that no longer contains what the stack describes has no correct
    /// contents, only an absence.</para>
    /// </summary>
    private async Task ReloadWorkspaceAfterFilesChangedUnderneath()
    {
        if (CurrentWorkspacePath is not { } cws) return;

        // Discarded rather than retired: by this point the designer has been prompted and answered,
        // so anything still dirty is deliberately gone. Retiring would refuse to drop it and the
        // reopened document would come back carrying edits to a file that no longer has them.
        _registry.Clear();
        _layoutRegistry.Clear();

        await SwitchToWorkspaceReporting(cws);
    }

    /// <summary>
    /// R-rc5-12b's second half, done to <b>the documents that actually changed and to nothing
    /// else</b> — one implementation shared by a restore, a version restore, an interrupted restore
    /// finished, and an agent batch's close.
    ///
    /// <para><b>Why this is not <see cref="ReloadWorkspaceAfterFilesChangedUnderneath"/>.</b> That one
    /// reopens the workspace, which is correct for opening a DIFFERENT workspace and enormously wrong
    /// for going back to an earlier state of the one already open: it drops every registry, replaces
    /// the whole dock tree with a fresh default, empties the Messages panel, regenerates every PCell
    /// and re-reads every open tab off disk. Owner report, 2026-09-08: the window stalled and then
    /// flashed as all of that was rebuilt, for a restore that had changed one schematic. The scope was
    /// the defect, not any one of those steps — each is right for its own purpose.</para>
    ///
    /// <para><b>What is left alone, because it is already correct:</b> the dock tree and every panel
    /// in it, the project tree, the Messages panel, the technology cache except the entries whose file
    /// changed, the generated-cell cache, and every open document the restore did not touch. What is
    /// NOT left alone is the undo stack of a document that did change — R-rc5-12b's requirement is per
    /// document and is met in full by applying it to the documents that changed, which is what closing
    /// and reopening the tab does.</para>
    ///
    /// <para><b>A null changed-set means "everything", never "nothing"</b>, and falls back to the full
    /// reopen. A restore whose diff could not be produced is slow; a restore that reloaded nothing
    /// would leave every tab showing the state that was just replaced, with no error attached.</para>
    ///
    /// <para><b>The workspace file is never re-read here and the arrangement is never re-applied.</b>
    /// A consequence worth stating: going back to a state where a different set of tabs was open does
    /// not reopen them. The alternative is the rebuild this exists to avoid, and the earlier
    /// arrangement was not being restored before either — the switch wrote the CURRENT session over
    /// the restored <c>.cws</c> before anything read it.</para>
    /// </summary>
    private async Task ReloadChangedDocuments(IReadOnlyList<RestoredPath>? changed)
    {
        if (CurrentWorkspacePath is null) return;
        if (changed is null || WorkspaceRootDir is not { } root)
        {
            await ReloadWorkspaceAfterFilesChangedUnderneath();
            return;
        }

        var changedAbs = new List<string>(changed.Count);
        foreach (var entry in changed)
        {
            try
            {
                changedAbs.Add(Path.GetFullPath(Path.Combine(
                    root, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar))));
            }
            catch (ArgumentException) { }
        }

        // A technology whose file came back is a different technology, and the cache hands out the one
        // it read. Invalidated per PATH — a blanket ResetTechCache would drop every entry, including
        // the live overrides of technologies this restore never touched.
        foreach (string abs in changedAbs)
            if (abs.EndsWith(".ctech", StringComparison.OrdinalIgnoreCase))
                _techCache.Invalidate(abs);

        var affected = OpenDocumentsAffectedBy(_openDocsByPath.Keys, changedAbs);

        // Captured BEFORE anything closes: closing removes the dockable from its dock, so its place in
        // the tab strip is unreadable a line later. A reopened tab that lands at the end of the strip
        // is a rearrangement the designer did not ask for.
        var reopen = new List<(string Path, string Kind, IDock? Dock, int Index, bool WasActive)>();
        var active = _factory.DocumentDock?.ActiveDockable;

        foreach (string key in affected)
        {
            if (!_openDocsByPath.TryGetValue(key, out var dockable)) continue;
            if (DocumentPathAndKind(dockable) is not ({ } docPath, { } kind)) continue;

            var dock  = dockable.Owner as IDock;
            int index = dock?.VisibleDockables?.IndexOf(dockable) ?? -1;

            reopen.Add((docPath, kind, dock, index, ReferenceEquals(active, dockable)));

            // Force, because the prompt has already happened: GoBackTo offers unsaved work up before
            // it writes a file, and a batch is refused outright while anything is dirty.
            try { _factory.ForceCloseDockable(dockable); }
            catch (Exception ex) { Messages.Warning($"Could not reload '{Path.GetFileName(docPath)}': {ex.Message}"); }
        }

        // DISCARD, not retire, and over the CHANGED PATHS rather than the closed tabs — an edit session
        // routinely outlives its tab, because closing a dirty schematic keeps its session precisely so
        // reopening restores the edit. That is right while the file still holds what the session was
        // edited against, and it is §1.3's failure the moment a restore replaces it: an undo would
        // re-apply the last few minutes of the state that was just replaced onto the restored file,
        // producing a document that existed at no moment ever. Unreferenced-guarded, so a session a
        // torn-off window is still showing is not ours to drop.
        foreach (string abs in changedAbs)
        {
            if (abs.EndsWith(".csch", StringComparison.OrdinalIgnoreCase))      DiscardSessionIfUnreferenced(abs);
            else if (abs.EndsWith(".clay", StringComparison.OrdinalIgnoreCase)) DiscardLayoutSessionIfUnreferenced(abs);
        }

        foreach (var (docPath, kind, dock, index, wasActive) in reopen)
        {
            // Gone with the restore — the tab closed above and there is nothing to put back.
            bool exists = string.Equals(kind, "cell", StringComparison.OrdinalIgnoreCase)
                        ? Directory.Exists(docPath)
                        : File.Exists(docPath);
            if (!exists) continue;

            switch (kind)
            {
                case "schematic":   OpenOrActivateSchematic(docPath); break;
                case "symbol":      OpenOrActivateSymbol(docPath); break;
                case "cell":        OpenOrActivateCellPlaceholder(docPath, Path.GetFileName(docPath)); break;
                case "datadisplay": OpenOrActivateDataDisplay(docPath); break;
                // Asynchronous on purpose: a 27 MB board read on the UI thread is the freeze this
                // whole change is about, and the ordinary open path already does it off-thread.
                case "layout":      await OpenOrActivateLayoutAsync(docPath); break;
                case "tech":        OpenOrActivateTech(docPath); break;
                case "emsetup":     OpenOrActivateEmSetup(docPath); break;
                default: continue;
            }

            // Not every open path lands in the map fully normalized — the technology document's own
            // lookup already does this two-step for the same reason.
            if (_openDocsByPath.TryGetValue(Path.GetFullPath(docPath), out var reopened)
                || _openDocsByPath.TryGetValue(docPath, out reopened))
                RestoreTabPosition(reopened, dock, index, wasActive);
        }

        // Cheap, and the only two surfaces that genuinely have to be told: the tree because files came
        // and went, the History panel because it is where the way forward is offered.
        _factory.ProjectTreeTool?.Refresh();
        RefreshHistoryPanel();
    }

    /// <summary>
    /// Puts a reopened document back where its tab was. Best effort by design — a dock that has since
    /// gone, or a document that reopened somewhere else entirely, simply keeps where it landed, which
    /// is what every other open in the application already does.
    /// </summary>
    private void RestoreTabPosition(IDockable reopened, IDock? dock, int index, bool wasActive)
    {
        try
        {
            if (dock?.VisibleDockables is { } siblings && index >= 0)
            {
                int now = siblings.IndexOf(reopened);
                int at  = Math.Min(index, siblings.Count - 1);
                if (now >= 0 && at >= 0 && now != at)
                    _factory.MoveDockable(dock, reopened, siblings[at]);
            }

            if (wasActive) _factory.SetActiveDockable(reopened);
        }
        catch (Exception ex)
        {
            Messages.Warning($"Could not restore the tab order after reloading: {ex.Message}");
        }
    }

    /// <summary>
    /// <b>Which open documents a set of changed files reaches.</b> A document key is usually the file
    /// itself; a cell's key is its FOLDER, so a changed file anywhere inside it is a change to that
    /// document — which is why this is at-or-under rather than equality.
    ///
    /// <para><c>internal static</c> so the gate can exercise the decision directly:
    /// <see cref="WorkspaceViewModel"/> cannot be built with real documents in it headlessly, and the
    /// rule worth holding is this one — a restore that changed one of five open documents reloads
    /// exactly one.</para>
    /// </summary>
    internal static List<string> OpenDocumentsAffectedBy(
        IEnumerable<string> openDocumentKeys, IReadOnlyCollection<string> changedAbsolutePaths)
    {
        // The keys are compared NORMALIZED and returned AS THEY WERE: the map is not uniformly
        // normalized, so a normalized key would not index it back.
        List<string> affected = [];
        foreach (string key in openDocumentKeys)
        {
            string normalized;
            try   { normalized = Path.GetFullPath(key); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
                  { normalized = key; }

            if (changedAbsolutePaths.Any(c => IsPathOrUnder(c, normalized)))
                affected.Add(key);
        }
        return affected;
    }

    /// <summary>
    /// R-rc5-7b. What a batch's close asks the window to do, over R-rc5-7c's channel.
    ///
    /// <para><b>The same path a restore uses, and that is the requirement</b> rather than a
    /// convenience: an agent's edits through a batch are exactly the situation §5.8 works out in full
    /// for a restore — the window showing the old content over an undo stack describing edits the file
    /// no longer contains, and the window's next save discarding everything the batch did. Two reload
    /// implementations that drift is the shape of defect §5.8 is written against.</para>
    ///
    /// <para><b>Nothing was unsaved when the batch opened</b> (R-rc5-7a refused it otherwise), so the
    /// reload cannot lose anything — which is why that refusal comes first and why nothing is prompted
    /// for here.</para>
    /// </summary>
    public async Task ReloadAfterExternalChange(IReadOnlyList<string> relativePaths)
    {
        if (relativePaths.Count == 0) return;

        // Somebody else's process wrote into this workspace — an agent's batch is the case this path
        // exists for. So there is something to keep again, and the two keep actions must not stay greyed
        // out on the strength of a flag that only this window's own save paths set.
        NoteWorkspaceChangedUnderneath();

        // A batch already knows what it changed — it names the paths over R-rc5-7c's channel — and
        // until now that list was thrown away and the whole workspace reopened. Same reload, same
        // rules, and the batch gets the narrow one for free.
        var root = WorkspaceRootDir;
        await ReloadChangedDocuments(root is null ? null : [.. relativePaths.Select(rel =>
        {
            string abs = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
            return new RestoredPath(rel, File.Exists(abs) || Directory.Exists(abs)
                                         ? RestoredPathKind.Written : RestoredPathKind.Removed);
        })]);
    }

    // ── Opening a workspace (R-rc5-12c's last rule, R-rc5-4c) ─────────────────────────────────────

    /// <summary>
    /// What RC-5 does when a workspace opens: <b>nothing that creates anything</b> (R-rc5-4a — opening
    /// a workspace to look at it creates nothing), and two things that only read.
    /// </summary>
    private void OnWorkspaceOpenedForRevision()
    {
        History.ResetForWorkspace();

        // R-rc10-18. The way forward survives a restore — which no longer comes through here at all,
        // and reaches this only on ReloadChangedDocuments' fallback, where the workspace being reopened
        // is still the same one. It must not survive opening a DIFFERENT workspace, where it would name
        // two entries that are not in the list.
        if (!string.Equals(_wayForwardRoot, WorkspaceRootDir, StringComparison.Ordinal))
        {
            _wayForward     = null;
            _wayForwardRoot = null;
        }

        // R-rc10-8. The panel's filter is per-user view state, restored with everything else about how
        // this designer had their view arranged.
        _storedHistoryFilter = ReadStoredHistoryFilter();
        _factory.HistoryTool?.ApplyStoredFilter(_storedHistoryFilter);

        // RC-6 R-rc6-9's first cadence, and R-rc6-14c's. One message saying what is NOT being kept and
        // why — for the ancestor row, the declined row, and a workspace whose own setting travelled
        // here switched off. It reads only.
        var situation = History.ReportStateOnOpen(WorkspaceRootDir);
        RefreshRecordingIndicator();

        // R-rc6-7a. The workspace-root row is the one that is a QUESTION rather than a report, and it
        // is asked ONCE — the answer goes in the repository's own marker, so a reopen does not ask
        // again. Deferred off the open path so a dialog never sits in front of the thing the designer
        // actually asked for.
        if (situation.NeedsAnAnswer) _ = AskAboutExistingHistory();

        // R-rc5-12c. A restore over thousands of files on a share can be cut off by a crash or a
        // dropped connection, and what it leaves is §1.3's failure exactly: a workspace that opens, is
        // well-formed, and is half of two states. Detected, never simulated.
        InterruptedRestore = History.ReportInterruptedRestore(WorkspaceRootDir);

        // BOTH panels on open, and the Versions one was missing — a defect with a consequence well
        // past the list itself (owner-reported, 2026-09-07). VersionHistoryTool.HasWorkspace starts
        // false and is set only by SetRows, and SetRows is only reached from here; before this line
        // existed, the only callers were "a version was just kept" and "changes were just brought in".
        // So on a freshly opened workspace the panel's own Keep-this-version button was DISABLED, and
        // the one thing that would have enabled it was keeping a version — which is what the button
        // does. File ▸ Keep This Version… still worked, which is why it went unnoticed.
        RefreshHistoryPanel();

        // RC-9 R-rc9-12/-16. Reads only, and reaches no network: the pin's state is a property of the
        // referenced workspace's repository AS IT ALREADY IS on this machine. Nothing in this series
        // contacts a network without being asked (R-rc9-6), and an automatic fetch here would silently
        // change what a design resolves against — the failure §7A.4 is written to prevent.
        OnWorkspaceOpenedForSharing();
    }

    /// <summary>
    /// What RC-5 does when a workspace CLOSES and no other one takes its place — the counterpart of
    /// <see cref="OnWorkspaceOpenedForRevision"/>, and its absence was a defect.
    ///
    /// <para><b>Every one of these surfaces is a statement about a workspace, and there is no longer a
    /// workspace for it to be about.</b> Opening a second workspace happened to hide the problem,
    /// because the open path resets all three; closing to the blank shell went through no such path,
    /// so the foot of the window went on saying <i>History failing — circuitRF tried to record this
    /// workspace and could not</i> about a workspace that was no longer open, and the two panels went
    /// on listing its restore points and its versions. A permanent indicator is only worth having if
    /// it is true, and the one thing that reliably teaches a designer to stop reading it is catching
    /// it saying something false (§1.4).</para>
    ///
    /// <para><b>Called after the dock layout has been rebuilt</b>, never before: the rebuild replaces
    /// the panel instances, so a refresh performed first would populate the tools that are about to be
    /// discarded and leave the new, empty ones holding the previous workspace's list.</para>
    /// </summary>
    private void OnWorkspaceClosedForRevision()
    {
        // The session ends with the workspace. This is what clears a latched failed boundary, so the
        // blank shell — and the next workspace opened in this window — is not reported as failing.
        History.ResetForWorkspace();

        InterruptedRestore = null;

        // With no workspace open all three read empty: History.State(null) is On, and both lists are
        // empty. Nothing here decides anything of its own — see the note on this partial's header.
        // RefreshRecordingIndicator also takes the toolbar's two history buttons away with it.
        RefreshRecordingIndicator();
        RefreshHistoryPanel();

        // R-rc10-18. The sentence is about one restore in one session, so it goes with the workspace.
        _wayForward     = null;
        _wayForwardRoot = null;
    }

    /// <summary>The interrupted restore found on open, or null. Held so the two ways out — finish it,
    /// or go back to where it started — can act on it.</summary>
    public RestoreInFlight? InterruptedRestore { get; private set; }

    /// <summary>Carries an interrupted restore through to where it was going.</summary>
    public async Task FinishInterruptedRestore()
    {
        if (InterruptedRestore is not { } inFlight) return;

        var outcome = History.FinishInterruptedRestore(WorkspaceRootDir, inFlight);
        InterruptedRestore = null;

        // The checkpoint it takes first sees the HALF-written workspace, so the diff against the
        // target is still exactly what is left to do. A failure hands back no changed set and falls
        // back to the full reopen, which is the right answer for a restore that did not complete.
        await ReloadChangedDocuments(outcome is { Ok: true } ? outcome.ChangedPaths : null);
    }

    // ── RC-6: the indicator, the question, and switching recording off (§5.6, §5.7, §12 Q4) ───────

    /// <summary>
    /// <b>The persistent, non-scrolling indicator</b> (R-rc6-9, R-rc6-10) — the measure most likely to
    /// actually prevent the false belief.
    ///
    /// <para>A scrolling log is read once and then trained against; the state is permanent for the
    /// session and is therefore displayed permanently, in the workspace window's own status strip.
    /// <b>One indicator, three reasons</b> — off, held, and a boundary that failed — because all three
    /// mean the same thing to a designer: nothing is being recorded right now. Three separate
    /// indicators would be three things to notice.</para>
    ///
    /// <para>Empty when recording is normal. A badge that is always there is a badge nobody reads, and
    /// on a machine with no git the feature does not exist at all (R-rc3-3).</para>
    /// </summary>
    public string RecordingIndicator
    {
        get => _recordingIndicator;
        private set { if (_recordingIndicator != value) { _recordingIndicator = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasRecordingIndicator)); } }
    }
    private string _recordingIndicator = "";

    /// <summary>The sentence behind the indicator, which is where the reason lives.</summary>
    public string RecordingIndicatorDetail
    {
        get => _recordingIndicatorDetail;
        private set { if (_recordingIndicatorDetail != value) { _recordingIndicatorDetail = value; OnPropertyChanged(); } }
    }
    private string _recordingIndicatorDetail = "";

    /// <summary>Whether the strip shows anything at all.</summary>
    public bool HasRecordingIndicator => RecordingIndicator.Length > 0;

    // ── The workspace toolbar's two revision buttons (owner, 2026-09-07) ──────────────────────────

    /// <summary>
    /// Whether the toolbar shows the <b>Restore Points</b> and <b>Versions</b> buttons at all.
    ///
    /// <para><b>Three conditions, all of them required</b>: a workspace is open, there is a usable git,
    /// and <i>keep a history</i> is on for this workspace. Otherwise the buttons are absent rather than
    /// greyed — R-rc3-3's silence, on the surface where it matters most. The toolbar is the one strip a
    /// designer's eye crosses every few seconds, and a control that is permanently dead there is paid
    /// for on every one of those glances by everybody who will never turn the feature on.</para>
    ///
    /// <para><b>The panels themselves stay reachable from the View menu whatever this says</b>, which
    /// is what makes hiding the buttons safe: a designer who has just switched history off can still
    /// open the list and look at what was kept before they did.</para>
    /// </summary>
    public bool CanShowRevisionButtons => _canShowRevisionButtons;
    private bool _canShowRevisionButtons;

    /// <summary>
    /// Re-reads that answer. <b>Called wherever the answer can change</b> — a workspace opening or
    /// closing, the per-workspace switch being flipped — and on window activation, which is what
    /// catches a change made over in Settings: that dialog writes preferences directly and tells no
    /// window about it, so coming back to the workspace is the moment the toolbar can notice.
    /// </summary>
    public void RefreshRevisionButtonAvailability()
    {
        bool now = WorkspaceHistoryService.KeepingHistoryHere(WorkspaceRootDir);
        if (now == _canShowRevisionButtons) return;

        _canShowRevisionButtons = now;
        OnPropertyChanged(nameof(CanShowRevisionButtons));
    }

    /// <summary>
    /// <b>Everything this window says about revision control, re-read.</b> The indicator at the foot,
    /// the Restore Points panel and the Versions panel — the three surfaces that describe the same
    /// underlying state and had, between them, three sets of call sites.
    /// </summary>
    public void RefreshRevisionSurfaces()
    {
        RefreshRecordingIndicator();
        RefreshHistoryPanel();
    }

    /// <summary>
    /// The same, for <b>every open workspace</b> — what Settings ▸ Revision Control calls when
    /// something it changed makes those surfaces wrong (owner-reported, 2026-09-07: history was turned
    /// on with the workspace open and the foot of the window went on saying <i>History off</i>).
    ///
    /// <para><b>A settings change is an EVENT, and this is the only thing that treats it as one.</b>
    /// That dialog writes preferences and the <c>.cws</c> directly and tells no window; nothing else
    /// re-read the state until the next workspace open, so a designer who turned history on was told,
    /// permanently and in the one place built to be believed, that it was off. Every remedy that works
    /// by asking again later — a poll, a refresh on activation — is either too expensive to run at that
    /// cadence (<see cref="WorkspaceHistoryService.State"/> runs git) or arrives after the user has
    /// already read the wrong answer.</para>
    ///
    /// <para><b>Every open workspace, not the one the dialog was opened from.</b> The two switches that
    /// matter here have different scopes — one is per-user and one is per-workspace — and the per-user
    /// one changes what every window should be saying. The dialog is also non-modal, so the others are
    /// on screen at the time.</para>
    ///
    /// <para><b>A broadcast rather than a subscription</b>, deliberately: a static event would hold a
    /// reference to every workspace view model that ever subscribed, and workspaces open and close
    /// throughout a session.</para>
    /// </summary>
    public static void RefreshRevisionSurfacesEverywhere()
    {
        foreach (var window in Views.WorkspaceLocator.AllWindows())
            if (window.DataContext is WorkspaceViewModel vm)
                vm.RefreshRevisionSurfaces();
    }

    /// <summary>Re-reads the recording state. Cheap, and called wherever it could have changed.</summary>
    public void RefreshRecordingIndicator()
    {
        var state = History.State(WorkspaceRootDir);
        RecordingIndicator       = HoldMessages.IndicatorFor(state);
        RecordingIndicatorDetail = HoldMessages.IndicatorDetailFor(state);

        // The toolbar's two buttons answer a different question from the indicator's (see
        // CanShowRevisionButtons), but every path that can change one can change the other, so they
        // are refreshed together rather than from two sets of call sites that would drift apart.
        RefreshRevisionButtonAvailability();
    }

    /// <summary>
    /// R-rc6-7a. <b>The workspace-root case is a QUESTION, not an offer.</b>
    ///
    /// <para>rev 3 offered adoption as a one-click action and never said what the user was choosing
    /// between. They are asked, told what keeping their own configuration costs — specifically, in
    /// recoverability terms rather than tidiness ones — and encouraged to adopt circuitRF's. Three
    /// answers, not two.</para>
    ///
    /// <para><b>Cancelling is not a fourth answer.</b> A closed dialog records nothing, so the question
    /// is put again next time the workspace opens — which is right: an unanswered question is not an
    /// answer, and the alternative is a workspace held forever because somebody pressed Escape.</para>
    /// </summary>
    public async Task AskAboutExistingHistory()
    {
        if (WorkspaceRootDir is not { } root) return;
        if (Views.WorkspaceLocator.WindowFor(this) is not { } owner) return;

        var answer = await new AdoptExistingHistoryDialog(Path.GetFileName(root)).ShowDialog<AdoptionAnswer?>(owner);
        if (answer is not { } chosen) return;

        History.AnswerAdoption(root, chosen);

        // All three, not two. Answering this question changes the HOLD state, and the hold is the line
        // both panels carry — the Versions panel included, which was left out and went on showing the
        // old one.
        RefreshRevisionSurfaces();
    }

    /// <summary>
    /// R-rc6-11. Stops recording for this workspace, or starts it again — <b>through the ordered
    /// transition</b>, never by writing the flag alone.
    ///
    /// <para>Reverse the order and the flag is set, circuitRF is already off, nothing is recorded, and
    /// the history simply stops with no entry saying why (R-rc6-14a). That is invisible from the flag,
    /// which is why the ordering lives below the firewall in one function rather than at each
    /// caller.</para>
    /// </summary>
    public void SetRecordingForThisWorkspace(bool on)
    {
        if (WorkspaceRootDir is null) return;

        if (on) History.TurnOn(WorkspaceRootDir);
        else    History.TurnOff(WorkspaceRootDir);

        // All three. An off period is a ROW in the versions list with its reason on it (R-rc7-10), so
        // the panel that was left out here is the one this transition most visibly changes.
        RefreshRevisionSurfaces();
    }
}

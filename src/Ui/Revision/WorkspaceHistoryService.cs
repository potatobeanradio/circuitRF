using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Design.Revision;
using CircuitRF.Diagnostics;
using CircuitRF.Ui.Messages;
using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.Revision;

/// <summary>
/// <b>The window's half of the whole feature</b> — RC-5's three boundaries, the restore-point list
/// and going back; RC-6's hold, off/on and retention; and RC-7's narrative, which is the versions a
/// designer keeps deliberately (<c>docs/design/revision-control.md</c> §5.2, §5.3, §5.7a, §5.8;
/// R-rc5-4 … R-rc5-22, R-rc6-4 … R-rc6-15, R-rc7-1 … R-rc7-17).
///
/// <para><b>The two histories stay separate all the way up</b> (R-rc7-9). <see cref="List"/> and
/// <see cref="Versions"/> read different things and are never combined: the safety net is dense,
/// automatic and local; the narrative is sparse, deliberate and shared. Merging them produces a log
/// no human will read, which then makes the safety net useless too because nobody looks at it.</para>
///
/// <para><b>It owns no revision-control logic.</b> Every decision is made below the firewall by
/// <see cref="WorkspaceArming"/>, <see cref="WorkspaceCheckpoints"/>, <see cref="RestorePoints"/> and
/// <see cref="WorkspaceRestore"/> — the same functions <c>circuitrf history</c> calls, which is what
/// stops the two spellings drifting. What is here is what only a window has: the preference, the
/// Messages sink, and the knowledge that circuitRF wrote a file during this session.</para>
///
/// <para><b>Automatic boundaries post nothing on success</b> (R-rc5-11). They would drown the panel
/// and defeat the readability §5.3 exists for; they appear in the restore-point list, which is where
/// someone looking for one looks. <b>Failure is the exception</b> (R-rc5-10) — §1.4 forbids a designer
/// believing they are protected when they are not, and one failing quietly is the purest form of that
/// failure. The other exception is the very first arming (R-rc5-4b), which is not a restore point at
/// all: it is the creation of the thing restore points go into.</para>
/// </summary>
public sealed class WorkspaceHistoryService
{
    private readonly IMessageSink _messages;

    /// <summary>
    /// R-rc5-4a. Whether circuitRF itself has written a file into the workspace folder this session.
    ///
    /// <para><b>This is what makes a colleague's glance create nothing.</b> "Would this boundary
    /// record something" has to be answerable BEFORE a repository exists, because before one exists
    /// there is nothing to diff against — and the answer must not come from the filesystem, which
    /// would report a file manager touching the folder as though circuitRF had edited a design. The
    /// edit session already knows; this is where it says so.</para>
    /// </summary>
    public bool CircuitRfWroteAFileThisSession { get; private set; }

    /// <summary>Set by every path that writes into the open workspace.</summary>
    public void NoteWorkspaceWrite()
    {
        CircuitRfWroteAFileThisSession = true;
        WrittenSinceLastEntry          = true;
    }

    /// <summary>
    /// <b>Whether anything has been written into the workspace since the last entry was recorded</b>
    /// (owner, 2026-09-08: both keep actions opened their dialog, took a title, and then created
    /// nothing).
    ///
    /// <para><b>This is the free signal, and the reason a cheap one was needed.</b> "Would a keep record
    /// anything" is <i>the computed tree differs from the newest entry's</i>, and computing that is
    /// <c>git add --all</c> plus <c>write-tree</c> over the whole workspace — the cost measured as
    /// <c>LastCloseCheckpointMs</c>, re-paid in full on every call because
    /// <see cref="GitCheckpoint"/> discards its private index at each end deliberately. Asking it
    /// whenever a panel refreshes puts a whole checkpoint's work in front of somebody who is only
    /// looking at the panel, which is precisely the cost RC-11 removed from the filter checkboxes.</para>
    ///
    /// <para><b>It errs towards enabled.</b> A write that happened to produce identical bytes leaves it
    /// true, the action stays available and the recording still declines — which is the behaviour that
    /// was reported, and no worse than it. The direction matters: a greyed control is
    /// indistinguishable from a feature that was never built (R-rc6-8), so being wrong in the other
    /// direction would cost more than the bug does.</para>
    ///
    /// <para><b>Per BOUNDARY, not per session</b> — which is the whole difference from
    /// <see cref="CircuitRfWroteAFileThisSession"/>, and why they are two fields rather than one. That
    /// one is RC-5's arming guard and must stay true for the rest of the session, or a workspace that
    /// was edited and then recorded would stop arming its own close.</para>
    /// </summary>
    public bool WrittenSinceLastEntry { get; private set; }

    /// <summary>
    /// <b>Somebody else's process wrote into this workspace</b> — an agent's batch through
    /// <c>circuitrf serve</c>, which reaches the window as
    /// <c>App.WorkspaceDocumentsChangedUnderneath</c>.
    ///
    /// <para><b>Deliberately NOT <see cref="NoteWorkspaceWrite"/>.</b> That one arms the repository, and
    /// arming is a statement about circuitRF having edited a design: a file manager or another process
    /// touching a shared folder must not cause a colleague's glance to create a repository there
    /// (R-rc5-4a). What an external write does change is what a keep would find, so it clears exactly
    /// the one signal that is about the last entry.</para>
    /// </summary>
    public void NoteWorkspaceChangedUnderneath() => WrittenSinceLastEntry = true;

    /// <summary>
    /// R-rc6-4a. Whether a boundary in this session actually WROTE an entry.
    ///
    /// <para><b>This is what stops a colleague's glance running housekeeping</b> (§12 Q24). RC-5's
    /// arming guard keeps that glance from creating a repository; on its own it did not keep it from
    /// running a retention sweep, under the READER'S preference, over the OWNER'S restore points — a
    /// per-user setting acting on a shared artifact, which is §4.4's identity mistake in a third file.
    /// Not "was a boundary attempted" and not "is there a repository": a session that only looked
    /// leaves the repository exactly as it found it, down to the bytes.</para>
    /// </summary>
    public bool RecordedSomethingThisSession { get; private set; }

    /// <summary>
    /// A new workspace is a new session's worth of that knowledge.
    ///
    /// <para><b><see cref="_lastBoundaryFailed"/> is reset here too, and leaving it out was a defect.</b>
    /// The service outlives the workspace — one window builds it once and every workspace opened in
    /// that window shares it — so a boundary that failed in workspace A left
    /// <see cref="RecordingState.Failed"/> latched, and workspace B was reported as failing when
    /// nothing about it had been tried. Worse in the direction that matters: the indicator says
    /// <i>nothing since then is in the history</i>, which about B is simply untrue, and a designer who
    /// checks and finds it false learns to disregard the one indicator §1.4 exists to be believed.
    /// The failure is a property of a session over one workspace, so it ends when that session
    /// does.</para>
    /// </summary>
    public void ResetForWorkspace()
    {
        CircuitRfWroteAFileThisSession = false;
        RecordedSomethingThisSession   = false;

        // A workspace that was closed properly was recorded on the way out, so its files match its
        // newest entry and there is nothing to keep until something is written. That is the ordinary
        // case, and it is the one the reported bug is about.
        WrittenSinceLastEntry          = false;
        _housekeeping.ResetForWorkspace();
        _reportedOnOpen                = false;
        _lastBoundaryFailed            = false;
    }

    /// <summary>R-rc6-4a's once-per-session pass. Held here because a session is what a window is.</summary>
    private readonly SessionHousekeeping _housekeeping = new();

    /// <summary>R-rc6-9's first cadence fires once per workspace, not once per boundary.</summary>
    private bool _reportedOnOpen;

    public WorkspaceHistoryService(IMessageSink messages) => _messages = messages;

    // ── The three boundaries ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// R-rc5-4's second boundary — <b>the explicit save-point, which is a request and therefore always
    /// arms</b> (R-rc5-4a) and is always kept (R-rc5-1f): the user's judgement about what matters
    /// beats any heuristic, and thinning it would discard exactly that judgement.
    /// </summary>
    /// <param name="label">The one optional line the dialog asked for. R-rc5-6b's reasoning applies
    /// here too — the intent is the whole value of the entry — and an entry with none shows
    /// <i>save-point</i> with its time, never a bare time (R-rc5-5a).</param>
    /// <param name="note">§5.12's longer note, or null — what the one line has no room for.</param>
    public bool TakeSavePoint(string? workspaceRoot, string? label, string? note = null)
        => Reach(workspaceRoot, CheckpointOrigin.SavePoint, label, attended: true, note: note);

    /// <summary>
    /// R-rc5-4's third boundary — <b>the one that reliably exists in every session</b>, including the
    /// ones where the designer never thought about history. Switchable (RC-4).
    ///
    /// <para><b>The workspace file is written BEFORE this runs</b> (R-rc5-21), or the entry keeps a
    /// workspace file one save out of date. That is ordering, it costs nothing, and it is invisible
    /// when wrong: the restored workspace simply comes up with slightly stale configuration and
    /// nobody connects it to the close. The caller does the write; this asserts nothing about it,
    /// because there is nothing here that could.</para>
    /// </summary>
    public bool TakeCloseCheckpoint(string? workspaceRoot)
    {
        var prefs = AppPreferencesIo.Load();
        if (prefs.RevisionCheckpointOnClose == false) return false;

        // R-rc5-15a. Nobody is at a close — it is the moment the designer asked to leave — so an
        // unexpectedly large new file is left out rather than asked about, and the entry says so.
        return Reach(workspaceRoot, CheckpointOrigin.WorkspaceClosed, label: null, attended: false);
    }

    /// <summary>
    /// The common path. <b>Everything that could stop a boundary is answered here, in one place</b>,
    /// so the three callers cannot each answer it differently.
    /// </summary>
    private bool Reach(string? workspaceRoot, CheckpointOrigin origin, string? label, bool attended,
                       string? note = null)
    {
        if (workspaceRoot is not { Length: > 0 }) return false;

        var prefs   = AppPreferencesIo.Load();
        var setting = WorkspaceRevisionSetting.Read(WorkspaceRevisionSetting.CwsPathFor(workspaceRoot));

        var armed = WorkspaceArming.Arm(
            workspaceRoot, origin,
            prefs.RevisionKeepHistory ?? RevisionArming.KeepHistoryDefault,
            setting,
            CircuitRfWroteAFileThisSession);

        // R-rc5-4b, and it is the ONE exception to the silence above.
        if (armed.Announcement is { } announcement) _messages.PostDiagnostic(announcement);

        if (!armed.Armed || armed.Git is null)
        {
            // Absence is silent; a refusal is not (R-rc5-10). The only thing that reaches here with a
            // refusal is identity, which is precisely the failure §4.4 says would otherwise be
            // invisible on a fresh machine.
            if (armed.Refusal is { } why)
            {
                _lastBoundaryFailed = true;
                _messages.PostDiagnostic(RestorePointMessages.CouldNotTake(
                    WorkspaceCheckpoints.Describe(origin), why.Render()));
            }
            return false;
        }

        var outcome = WorkspaceCheckpoints.Take(armed.Git, origin, label, attended, note: note);

        foreach (var d in outcome.Diagnostics)
        {
            switch (d.Severity)
            {
                case DiagnosticSeverity.Error:
                    // R-rc6-10's third reason. A failed boundary means nothing since then is in the
                    // history, which is the same thing to a designer as off or held — so it lights the
                    // same indicator rather than a third one nobody would notice.
                    _lastBoundaryFailed = true;
                    _messages.PostDiagnostic(RestorePointMessages.CouldNotTake(
                        WorkspaceCheckpoints.Describe(origin), d.Render()));
                    break;

                // A warning is a nested repository left out, or a large file left out at an unattended
                // boundary. Both change what the entry CONTAINS, so both are said.
                case DiagnosticSeverity.Warning:
                    _messages.PostDiagnostic(d);
                    break;

                // Info here is "nothing had changed" (R-rc5-5a), which an automatic boundary drops and
                // a save-point reports, because the person who pressed it is owed an answer.
                case DiagnosticSeverity.Info when attended:
                    _messages.PostDiagnostic(d);
                    break;
            }
        }

        if (outcome.Recorded)
        {
            RecordedSomethingThisSession = true;

            // The newest entry now holds what is on disk, so there is nothing further to keep until
            // something is written. Cleared HERE rather than at the three callers, for the reason the
            // whole of this method exists: three callers agreeing about it is how it becomes true in
            // two of them.
            WrittenSinceLastEntry = false;
        }

        Changed?.Invoke();
        return outcome.Recorded;
    }

    /// <summary>Raised whenever the list may have changed, so the panel refreshes without polling.</summary>
    public event Action? Changed;

    // ── The list ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The restore points, newest first, or an empty list. <b>Never throws and never blocks on a
    /// writer</b> — every read below the firewall takes no optional locks, which is what lets a panel
    /// refresh while a boundary is being taken.
    /// </summary>
    public IReadOnlyList<RestorePoint> List(string? workspaceRoot)
        => Bind(workspaceRoot) is { } git ? RestorePoints.List(git) : [];

    /// <summary>
    /// R-rc5-1f's <b>keep</b> action, on any entry — §10B.3's "make it permanent", which exists here
    /// in Stage 2 before there is a Commit to turn anything into.
    /// </summary>
    public bool Keep(string? workspaceRoot, RestorePoint point)
    {
        if (Bind(workspaceRoot) is not { } git) return false;

        var outcome = RestorePoints.MarkKept(git, point);
        if (outcome.Diagnostic is { } d) _messages.PostDiagnostic(d);

        Changed?.Invoke();
        return outcome.Ok;
    }

    // ── Going back ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Puts the workspace back to one entry.
    ///
    /// <para><b>The caller must have offered up unsaved work first and must reload the open documents
    /// afterwards</b> (R-rc5-12b). Both belong to the window, and neither can be done from here;
    /// <c>WorkspaceViewModel</c>'s own restore command is the one caller and does both.</para>
    /// </summary>
    public RestoreResult? Restore(string? workspaceRoot, RestorePoint point)
    {
        if (Bind(workspaceRoot) is not { } git) return null;

        var result = WorkspaceRestore.Restore(git, point);
        foreach (var d in result.Diagnostics) _messages.PostDiagnostic(d);

        if (result.Ok)
        {
            // A restore REPLACES the workspace's files, and the entry it took on the way in holds what
            // was replaced rather than what is now there. So there is something to keep again — the
            // restored state is named nowhere as the newest entry — and the free signal has to say so,
            // or both keep actions would grey out at the one moment §1.4 is written about.
            WrittenSinceLastEntry = true;

            _messages.Info(RestorePointMessages.RestoreReferenceCaveat(
                WorkspacePins.Survey(workspaceRoot!).Any(p => p.Pin is not null)));
            _messages.Info(RestorePointMessages.RestoreLeavesResultsAlone);
        }

        Changed?.Invoke();
        return result;
    }

    /// <summary>
    /// R-rc5-12c's last rule, on workspace open. <b>An interrupted restore is DETECTED, not
    /// simulated</b> — what a crash mid-restore leaves is §1.3's failure exactly: a workspace that
    /// opens, is well-formed, and is half of two states.
    /// </summary>
    public RestoreInFlight? ReportInterruptedRestore(string? workspaceRoot)
    {
        if (workspaceRoot is not { Length: > 0 }) return null;
        if (RestoreMarker.Read(workspaceRoot) is not { } inFlight) return null;

        // A marker the crash truncated names no states, and the ordinary sentence would then quote two
        // empty labels — which reads as a defect rather than as the interruption it is.
        _messages.PostDiagnostic(inFlight.Target.CommitId.Length == 0
            ? RestorePointMessages.RestoreWasInterruptedUnnamed()
            : RestorePointMessages.RestoreWasInterrupted(
                  inFlight.Target.Label, inFlight.Fallback.Label));

        return inFlight;
    }

    /// <summary>Carries an interrupted restore through to where it was going.</summary>
    public RestoreResult? FinishInterruptedRestore(string? workspaceRoot, RestoreInFlight inFlight)
    {
        if (Bind(workspaceRoot) is not { } git) return null;

        var result = WorkspaceRestore.Finish(git, inFlight);
        foreach (var d in result.Diagnostics) _messages.PostDiagnostic(d);

        // The same reasoning as Restore's: what is on disk afterwards is named by no newest entry.
        if (result.Ok) WrittenSinceLastEntry = true;

        Changed?.Invoke();
        return result;
    }

    // ── RC-6: the hold, off/on, retention (§5.6, §5.7, §12 Q4) ────────────────────────────────────

    /// <summary>Which of §12 Q4's four situations this workspace is in (R-rc6-7).</summary>
    public static RepositorySituation Situation(string? workspaceRoot)
        => EnclosingRepository.Detect(workspaceRoot);

    /// <summary>
    /// <b>What the persistent indicator says</b> (R-rc6-10) — one indicator, three reasons, and the
    /// reason is in its text. Held, off and a failure all mean the same thing to a designer:
    /// <i>nothing is being recorded right now.</i>
    ///
    /// <para><b>A machine with no git answers <see cref="RecordingState.On"/> and shows nothing</b>,
    /// which is not a lie by omission but R-rc3-3's silence: absence is harmless, and a designer who
    /// never had this feature must not be given a badge about it.</para>
    /// </summary>
    public RecordingState State(string? workspaceRoot)
    {
        if (workspaceRoot is not { Length: > 0 } || !Directory.Exists(workspaceRoot)) return RecordingState.On;
        if (GitCommand.For(workspaceRoot) is null) return RecordingState.On;

        if (Situation(workspaceRoot).IsHeld) return RecordingState.Held;

        var prefs   = AppPreferencesIo.Load();
        var setting = WorkspaceRevisionSetting.Read(WorkspaceRevisionSetting.CwsPathFor(workspaceRoot));

        if (!RevisionArming.IsArmed(prefs.RevisionKeepHistory ?? RevisionArming.KeepHistoryDefault, setting))
            return RecordingState.Off;

        return _lastBoundaryFailed ? RecordingState.Failed : RecordingState.On;
    }

    /// <summary>R-rc6-10's third reason. Set by the one place that reports a failed boundary.</summary>
    private bool _lastBoundaryFailed;

    /// <summary>
    /// <b>Whether this workspace is one circuitRF is keeping a history for at all</b> — the question
    /// the workspace toolbar's two revision buttons are shown or hidden on (owner, 2026-09-07).
    ///
    /// <para>Three conditions, and they are exactly the three a designer would name: there is a
    /// workspace open, there is a usable git for circuitRF to run, and <i>keep a history</i> is on for
    /// this workspace. Any one of them false and the buttons are simply not there — R-rc3-3's silence
    /// applied to the toolbar, which is the surface a designer looks at most and the one where a
    /// permanently dead control is most expensive.</para>
    ///
    /// <para><b>Deliberately NOT <see cref="State"/>.</b> That answers what the indicator SAYS and
    /// folds two states this question must keep apart: with no git it answers
    /// <see cref="RecordingState.On"/> — correctly, because the feature is meant to be invisible
    /// there — which as a visibility test would put the buttons on the one machine that has nothing to
    /// show behind them.</para>
    ///
    /// <para><b>And deliberately cheap.</b> This is re-read whenever the window is activated, so that a
    /// change made in Settings is reflected when the designer comes back to the workspace. It reads the
    /// cached git discovery and two small files, and — unlike <see cref="State"/> — never runs git: a
    /// subprocess per window activation would be paid on every alt-tab, forever, to answer a question
    /// whose answer changes about twice in a workspace's life.</para>
    /// </summary>
    public static bool KeepingHistoryHere(string? workspaceRoot)
    {
        if (workspaceRoot is not { Length: > 0 } || !Directory.Exists(workspaceRoot)) return false;
        if (GitDiscovery.Find(out _) is null) return false;

        var prefs   = AppPreferencesIo.Load();
        var setting = WorkspaceRevisionSetting.Read(WorkspaceRevisionSetting.CwsPathFor(workspaceRoot));

        return RevisionArming.IsArmed(prefs.RevisionKeepHistory ?? RevisionArming.KeepHistoryDefault, setting);
    }

    /// <summary>
    /// R-rc6-9's <b>first cadence</b>: one message on workspace open, saying what is not being kept,
    /// why, and how to remedy it — <b>once per workspace</b>, never per boundary.
    ///
    /// <para>Returns the situation so the caller can put R-rc6-7a's question, which is the one row
    /// this does not report: the workspace-root case is a QUESTION, and a message about it would be
    /// answered by a dialog the user is already looking at.</para>
    /// </summary>
    public RepositorySituation ReportStateOnOpen(string? workspaceRoot)
    {
        var situation = Situation(workspaceRoot);
        if (_reportedOnOpen || workspaceRoot is not { Length: > 0 }) return situation;
        _reportedOnOpen = true;

        if (EnclosingRepository.OpenReportFor(situation) is { } held)
        {
            _messages.PostDiagnostic(held);
            return situation;
        }

        // R-rc6-14c. The flag lives in the .cws, so it TRAVELS: a clone or an archive of a workspace
        // that was switched off arrives switched off, and the recipient's own preference does not
        // override it. Reported at their first boundary rather than silently doing nothing — and only
        // when the workspace itself recorded the answer, because "the preference is off everywhere" is
        // a state the user set on this machine and does not need to be told about per workspace.
        if (WorkspaceRevisionSetting.Read(WorkspaceRevisionSetting.CwsPathFor(workspaceRoot)) == false)
            _messages.PostDiagnostic(HoldMessages.ArrivedSwitchedOff(
                RevisionSwitch.WorkspaceName(workspaceRoot)));

        return situation;
    }

    /// <summary>
    /// R-rc6-9's <b>second cadence</b>: a refusal on an attempt, saying why <b>and not restating the
    /// remedy</b> — the user has been told once, and repeating it on every attempt is how a message
    /// becomes noise.
    ///
    /// <para>Returns true when it refused, so the caller stops. <b>The affordance stays visible</b>
    /// (R-rc6-8): a hidden button is indistinguishable from a feature that was never there, and the
    /// failure this whole feature guards against is a designer who believes they are protected and is
    /// not.</para>
    /// </summary>
    public bool RefusedBecauseHeld(string? workspaceRoot)
    {
        if (!Situation(workspaceRoot).IsHeld) return false;
        _messages.PostDiagnostic(HoldMessages.HeldRefusal());
        return true;
    }

    /// <summary>
    /// R-rc6-7a. Records one answer to <i>"this workspace already keeps a history of its own"</i>, and
    /// the marker it writes is what makes the question asked <b>once</b> rather than on every open.
    /// </summary>
    public bool AnswerAdoption(string? workspaceRoot, AdoptionAnswer answer)
    {
        if (workspaceRoot is not { Length: > 0 } || GitCommand.For(workspaceRoot) is not { } git)
            return false;

        git.Identity ??= RevisionIdentity.Resolve(git);

        var outcome = RepositoryAdoption.Apply(git, answer);
        if (outcome.Diagnostic is { } d) _messages.PostDiagnostic(d);

        Changed?.Invoke();
        return outcome.Ok;
    }

    /// <summary>
    /// R-rc6-11/R-rc6-14a. Stops recording for this workspace — <b>writing the setting, then one final
    /// entry that records the change, and only then stopping.</b> Deletes nothing.
    /// </summary>
    public RevisionSwitchResult TurnOff(string? workspaceRoot) => Flip(workspaceRoot, on: false);

    /// <summary>R-rc6-11's symmetry. Resumes the existing history and records the resumption, which is
    /// the far end of R-rc6-13's gap.</summary>
    public RevisionSwitchResult TurnOn(string? workspaceRoot) => Flip(workspaceRoot, on: true);

    private RevisionSwitchResult Flip(string? workspaceRoot, bool on)
    {
        if (workspaceRoot is not { Length: > 0 }) return new RevisionSwitchResult(false, null, []);

        bool preference = AppPreferencesIo.Load().RevisionKeepHistory ?? RevisionArming.KeepHistoryDefault;

        var result = on ? RevisionSwitch.TurnOn(workspaceRoot, preference)
                        : RevisionSwitch.TurnOff(workspaceRoot, preference);

        foreach (var d in result.Diagnostics) _messages.PostDiagnostic(d);
        if (result.Transition is not null) RecordedSomethingThisSession = true;

        Changed?.Invoke();
        return result;
    }

    /// <summary>
    /// R-rc6-15. <b>An AI edit requested while recording is off says so before anything is modified,
    /// and offers to turn it back on.</b>
    ///
    /// <para>Switching revision control off switches off the one checkpoint RC-4 makes non-switchable,
    /// because it is §1.2 — the reason the feature exists. That is a legitimate thing to choose and an
    /// illegitimate thing to stumble into, so the conflict is resolved out loud, at the moment it
    /// matters. <b>The remedy is OFFERED rather than named</b> (§7A.3), which the Messages panel can
    /// carry; the sentence still reads correctly with no button on the end, because a headless sink
    /// drops the action.</para>
    /// </summary>
    public void ReportAiEditRefusedBecauseOff(string? workspaceRoot)
        => _messages.PostAction(
               MessageLevel.Warning,
               HoldMessages.AiEditWhileOff().Render(),
               HoldMessages.TurnRecordingOnAction,
               () => { TurnOn(workspaceRoot); return System.Threading.Tasks.Task.CompletedTask; });

    /// <summary>
    /// R-rc6-4a. <b>The one housekeeping pass, at close, after the close checkpoint.</b> A sweep and
    /// then a pack, and neither runs in a session that recorded nothing.
    /// </summary>
    public HousekeepingResult CloseHousekeeping(string? workspaceRoot)
    {
        if (Bind(workspaceRoot) is not { } git) return HousekeepingResult.Skipped;

        var prefs = AppPreferencesIo.Load();

        var result = _housekeeping.OnClose(
            git,
            RecordedSomethingThisSession,
            new RetentionPolicy(
                prefs.RevisionRetentionDays        ?? RevisionPreferenceDefaults.RetentionDays,
                prefs.RevisionMinimumRestorePoints ?? RevisionPreferenceDefaults.MinimumRestorePoints),
            (long)(prefs.RevisionPackThresholdMb ?? RevisionPreferenceDefaults.PackThresholdMb)
                * 1024 * 1024);

        foreach (var d in result.Diagnostics) _messages.PostDiagnostic(d);
        return result;
    }

    /// <summary>
    /// R-rc6-4. The restore points <b>including the ones retention thinned</b>, each thinned one
    /// marked — because a state that silently vanished from the list is indistinguishable from one
    /// that was destroyed.
    /// </summary>
    public IReadOnlyList<RestorePoint> ListIncludingThinned(string? workspaceRoot)
        => Bind(workspaceRoot) is { } git ? RestorePoints.ListIncludingThinned(git) : [];

    /// <summary>R-rc6-4's way back: one reference update, from the journal.</summary>
    public bool BringBackThinned(string? workspaceRoot, RestorePoint point)
    {
        if (Bind(workspaceRoot) is not { } git) return false;

        var outcome = RestorePoints.RestoreThinned(git, point);
        if (outcome.Diagnostic is { } d) _messages.PostDiagnostic(d);

        Changed?.Invoke();
        return outcome.Ok;
    }

    /// <summary>R-rc6-13's data, for the browser below.</summary>
    public IReadOnlyList<RevisionGap> Gaps(string? workspaceRoot)
        => RevisionGaps.Find(ListIncludingThinned(workspaceRoot));

    // ── RC-7: the narrative — keeping a version, and browsing them (§5.2, §5.5, §6.1, §6.3) ───────

    /// <summary>
    /// <b>The explicit commit</b> (R-rc7-1, R-rc7-7): an ordinary version on the ordinary line of
    /// work, created deliberately, with a title the designer wrote.
    ///
    /// <para><b>It arms exactly as a save-point does</b> (R-rc5-4a) — it is a request, so a workspace
    /// with no history yet gains one here rather than refusing. And it refuses in the three ways
    /// R-rc7-2 requires: hidden entirely when git is absent (the caller never gets this far),
    /// <b>visible and refusing when held</b>, and refusing when recording is off for this
    /// workspace.</para>
    ///
    /// <para><b>Unlike a checkpoint, this one always reports</b> (R-rc7-7). The person reading the
    /// entry pressed the button that made the thing being named, which is exactly the qualification
    /// R-rc7-4 states — and the identity in it is what makes §4.1's escape hatch usable.</para>
    /// </summary>
    public CommitResult KeepVersion(string? workspaceRoot, string? title,
                                    IReadOnlyList<string>? leaveOut = null,
                                    string?                note     = null)
    {
        if (workspaceRoot is not { Length: > 0 })
            return CommitResult.Refused(HistoryMessages.NoHistoryToKeepAVersionIn(""));

        string name = RevisionSwitch.WorkspaceName(workspaceRoot);

        // R-rc6-8. Held is loud: the affordance stayed visible, so the refusal has to say what the
        // state is rather than the button quietly doing nothing.
        if (Situation(workspaceRoot).IsHeld)
        {
            var held = HistoryMessages.CannotKeepAVersionHeld();
            _messages.PostDiagnostic(held);
            return CommitResult.Refused(held);
        }

        var prefs   = AppPreferencesIo.Load();
        var setting = WorkspaceRevisionSetting.Read(WorkspaceRevisionSetting.CwsPathFor(workspaceRoot));

        if (!RevisionArming.IsArmed(prefs.RevisionKeepHistory ?? RevisionArming.KeepHistoryDefault, setting))
        {
            var off = HistoryMessages.CannotKeepAVersionOff();
            _messages.PostDiagnostic(off);
            return CommitResult.Refused(off);
        }

        // A save-point's own arming path, and for the same reason: this is a request, so a workspace
        // that has never recorded anything gains a history here. The announcement is R-rc5-4b's and
        // fires once, exactly as it does for a save-point.
        var armed = WorkspaceArming.Arm(
            workspaceRoot, CheckpointOrigin.SavePoint,
            prefs.RevisionKeepHistory ?? RevisionArming.KeepHistoryDefault,
            setting, CircuitRfWroteAFileThisSession);

        if (armed.Announcement is { } announcement) _messages.PostDiagnostic(announcement);

        if (!armed.Armed || armed.Git is null)
        {
            var why = armed.Refusal ?? HistoryMessages.NoHistoryToKeepAVersionIn(name);
            _messages.PostDiagnostic(why);
            return CommitResult.Refused(why);
        }

        var result = WorkspaceCommit.Commit(armed.Git, title, leaveOut, note);
        foreach (var d in result.Diagnostics) _messages.PostDiagnostic(d);

        if (result.Ok)
        {
            RecordedSomethingThisSession = true;
            WrittenSinceLastEntry        = false;
        }

        Changed?.Invoke();
        return result;
    }

    /// <summary>
    /// Whether keeping a version would record anything. <b>Asked before the dialog opens</b>, so a
    /// designer is not given a field to fill in for an operation that can only answer "nothing has
    /// changed".
    /// </summary>
    public bool HasSomethingToKeep(string? workspaceRoot)
        => Bind(workspaceRoot) is { } git && WorkspaceCommit.HasSomethingToKeep(git);

    /// <summary>
    /// R-rc7-9. <b>The narrative, which is a different list from the restore points and is never
    /// merged with them.</b>
    /// </summary>
    public IReadOnlyList<HistoryVersion> Versions(string? workspaceRoot, int limit = 0)
        => Bind(workspaceRoot) is { } git ? HistoryBrowser.Versions(git, limit) : [];

    /// <summary>
    /// R-rc7-10. The browser's rows: the versions, with each off period placed among them as a gap
    /// carrying its reason — because rendering it as an ordinary interval between two versions is the
    /// false-belief failure in its purest form.
    /// </summary>
    public IReadOnlyList<HistoryRow> VersionRows(string? workspaceRoot, int limit = 0)
    {
        if (Bind(workspaceRoot) is not { } git) return [];

        // R-rc9-6. What a Pull brought in FIRST, then this workspace's own — and the two lists never
        // merge into one undifferentiated history, because a version on the other copy is not part of
        // this workspace until somebody chooses it.
        //
        // Not sorted together by time either: an incoming version is newer than everything here almost
        // by definition, so time ordering would put them on top anyway and would occasionally not,
        // leaving one stranded mid-list under a mark nobody would look for there.
        var incoming = HistoryBrowser.Incoming(git, limit);
        var mine     = HistoryBrowser.Rows(HistoryBrowser.Versions(git, limit),
                                           RestorePoints.ListIncludingThinned(git));

        if (incoming.Count == 0) return mine;

        List<HistoryRow> rows = [.. incoming.Select(v => new HistoryRow(v, null))];
        rows.AddRange(mine);
        return rows;
    }

    /// <summary>
    /// <b>RC-10's one list</b> (§5.10; R-rc10-1, R-rc10-5, R-rc10-21) — versions, restore points and
    /// off periods in one ordering, filtered as the panel is filtering.
    ///
    /// <para>It reads through <see cref="HistoryList.Read"/> and holds no logic of its own, which is
    /// what makes <c>circuitrf history list</c> and the panel the same answer rather than two
    /// implementations that agree today.</para>
    /// </summary>
    public HistoryList.Result Entries(string? workspaceRoot, HistoryFilter filter)
        => ReadEntries(workspaceRoot).Under(filter);

    /// <summary>
    /// <b>The repository read, once, with the filter left out of it</b> — and the reason it is a
    /// separate step is that a filter toggle is not a question about the repository.
    ///
    /// <para>Every checkbox in §5.10's flyout used to re-read the whole workspace: the versions, the
    /// restore points and the thinning journal, the sharing set, the corrections and the incoming
    /// versions, plus <see cref="HiddenAutomaticCount"/>'s second full read of the same restore-point
    /// list — <b>a dozen git subprocesses on the UI thread for a decision that changes nothing but
    /// which rows are shown</b>. A checkbox that pauses is a checkbox a designer stops trusting, and
    /// on a large history it was well past the threshold where a control stops feeling attached to
    /// the pointer.</para>
    ///
    /// <para><b>Moving it off the UI thread would have been the wrong fix</b> and is worth saying so
    /// here rather than in a note: it would still have been a dozen subprocesses and a visible delay
    /// before the list settled, with a repository another process may be holding, in exchange for
    /// threading a panel that refreshes at every boundary. What the filter actually needs is
    /// <see cref="HistoryList.Build"/>, which is pure — so the read happens when the history CHANGES
    /// and the filter runs over what is already in hand.</para>
    ///
    /// <para><b>It is deliberately not a cache with a lifetime.</b> The caller re-reads on every
    /// boundary and every workspace switch, exactly as it did before; this type holds nothing between
    /// calls, because a stale list would offer a designer a way back to a state that is no longer
    /// there.</para>
    /// </summary>
    public HistorySources ReadEntries(string? workspaceRoot)
        => Bind(workspaceRoot) is { } git ? HistoryList.ReadSources(git) : HistorySources.Nothing;

    /// <summary>
    /// R-rc10-5's empty-state arithmetic: <b>how many entries the default filter is hiding</b> on a
    /// workspace whose history is nothing but workspace-close entries.
    ///
    /// <para>That workspace opens on an empty list, and it is exactly the one §1 is written for — its
    /// owner never thought about history at all. A panel that said "nothing kept yet" there would be
    /// saying something false about a workspace that has a fortnight of recoverable states in it.</para>
    /// </summary>
    /// <para><b>The panel does not call this</b> — it reads
    /// <see cref="HistorySources.AutomaticCount"/> off the list it already has, which is the same
    /// number counted from the same read rather than from a second one. This spelling stays for a
    /// caller that wants the count alone.</para>
    public int HiddenAutomaticCount(string? workspaceRoot) => ReadEntries(workspaceRoot).AutomaticCount;

    /// <summary>
    /// How many versions a Pull has brought in that are not here yet — <b>what the panel says above the
    /// list</b>, so the count is legible without counting marked rows.
    /// </summary>
    public int IncomingCount(string? workspaceRoot)
        => Bind(workspaceRoot) is { } git ? HistoryBrowser.Incoming(git).Count : 0;

    /// <summary>
    /// RC-10 R-rc10-17. <b>What one entry holds that the workspace does not</b> — the comparison the
    /// row's own menu offers, and the one a designer wants when deciding whether to go back at all.
    /// Writes nothing; see <see cref="HistoryBrowser.CompareWithWorkspace"/> for why that mattered.
    /// </summary>
    public IReadOnlyList<DocumentChange> CompareWithWorkspace(string? workspaceRoot, HistoryEntry entry)
    {
        if (Bind(workspaceRoot) is not { } git) return [];

        string? tree = entry.Version?.TreeId ?? entry.Point?.TreeId;
        return tree is { Length: > 0 } ? HistoryBrowser.CompareWithWorkspace(git, tree) : [];
    }

    // ── RC-11: correcting what a person wrote (§5.11) ─────────────────────────────────────────────

    /// <summary>
    /// §5.11 case (a). <b>Renames a restore point</b> — a new parentless commit over the identical
    /// tree, one reference update, and every trailer but the label preserved (R-rc11-3, R-rc11-4).
    /// </summary>
    /// <returns>
    /// The entry's identity AFTER the rename, or null if it did not happen. A rename rewrites the
    /// commit, so the caller cannot assume the id it passed in still names anything — and the one
    /// caller that holds a copy of the old title needs the new id to keep its link alive.
    /// </returns>
    public string? Rename(string? workspaceRoot, RestorePoint point, string? label, string? note = null)
    {
        if (Bind(workspaceRoot) is not { } git) return null;

        var outcome = RestorePoints.Rename(git, point, label, note);
        _messages.PostDiagnostic(outcome.Diagnostic ?? HistoryMessages.RestorePointRenamed(label ?? ""));

        Changed?.Invoke();
        return outcome.Ok ? outcome.CommitId : null;
    }

    /// <summary>
    /// §5.11 case (a). <b>Lets a restore point go, through RC-6's thinning journal rather than around
    /// it</b> (R-rc11-5) — so it is still listed, still comes back, and frees nothing until §5.6a's
    /// explicit reclaim.
    /// </summary>
    public bool Forget(string? workspaceRoot, RestorePoint point)
    {
        if (Bind(workspaceRoot) is not { } git) return false;

        var outcome = RestorePoints.Forget(git, point);
        _messages.PostDiagnostic(outcome.Diagnostic ?? HistoryMessages.RestorePointLetGo(point.Label));

        Changed?.Invoke();
        return outcome.Ok;
    }

    /// <summary>
    /// §5.11 case (b). <b>Corrects the newest unshared version's title</b>, and refuses by naming why
    /// on anything else (R-rc11-7, R-rc11-8).
    /// </summary>
    /// <inheritdoc cref="Rename" path="/returns"/>
    public string? CorrectTitle(string? workspaceRoot, HistoryVersion version, string? title,
                                string? note = null)
    {
        if (Bind(workspaceRoot) is not { } git) return null;

        var outcome = VersionCorrections.CorrectTitle(git, version, title, note);
        _messages.PostDiagnostic(outcome.Diagnostic);

        Changed?.Invoke();
        return outcome.Ok ? outcome.Version?.CommitId : null;
    }

    /// <summary>
    /// R-rc11-2's predicate, asked once for the entry a designer just right-clicked. <b>Whether this
    /// version can be retitled in place</b> — which decides which of §5.11's two version dialogs
    /// opens, and is never a question put to the designer.
    /// </summary>
    public bool CanCorrectTitle(string? workspaceRoot, HistoryVersion version)
        => Bind(workspaceRoot) is { } git
        && VersionCorrections.CanCorrectTitle(git, version, VersionSharing.Compute(git));

    /// <summary>The correction already on a version, or null. What case (c)'s dialog opens on, so a
    /// designer refining one does not have to retype it.</summary>
    public VersionCorrection? CorrectionOn(string? workspaceRoot, HistoryVersion version)
        => Bind(workspaceRoot) is { } git
        && VersionCorrections.Annotations(git).TryGetValue(version.CommitId, out var c)
            ? c : null;

    /// <summary>
    /// §5.11 case (c). <b>Adds a correction to a version, without altering it</b> (R-rc11-13) —
    /// available for any version at all, which is what lets the two refusals above name it.
    /// </summary>
    public bool Annotate(string? workspaceRoot, HistoryVersion version, string? correction,
                         string? note = null)
    {
        if (Bind(workspaceRoot) is not { } git) return false;

        var outcome = VersionCorrections.Annotate(git, version, correction, note);
        _messages.PostDiagnostic(outcome.Diagnostic);

        Changed?.Invoke();
        return outcome.Ok;
    }

    /// <summary>
    /// §5.11's review (R-rc11-16, §12 Q36). <b>The version titles one journey is about to take off
    /// this machine</b>, from the one function all three call.
    /// </summary>
    public IReadOnlyList<LeavingTitle> TitlesLeaving(string? workspaceRoot, LeavingJourney journey)
        => Bind(workspaceRoot) is { } git ? Design.Revision.TitlesLeaving.For(git, journey) : [];

    /// <summary>R-rc7-11. What differs between two versions, at the granularity of documents.</summary>
    public IReadOnlyList<DocumentChange> Compare(string? workspaceRoot, HistoryVersion from,
                                                 HistoryVersion to)
        => Bind(workspaceRoot) is { } git ? HistoryBrowser.Compare(git, from.CommitId, to.CommitId) : [];

    /// <summary>
    /// What one version changed against the one before it — the ordinary question the browser asks of
    /// a single selected row. An initial version has nothing before it and reports no changes rather
    /// than every file it holds.
    /// </summary>
    public IReadOnlyList<DocumentChange> ChangesIn(string? workspaceRoot, HistoryVersion version)
    {
        if (Bind(workspaceRoot) is not { } git) return [];

        var parent = git.Run(["rev-parse", "--verify", "--quiet", version.CommitId + "^"],
                             new GitRunOptions(ReadOnly: true));
        return parent.Ok && parent.Line.Length > 0
            ? HistoryBrowser.Compare(git, parent.Line, version.CommitId)
            : [];
    }

    /// <summary>
    /// R-rc7-17. <b>Going back to a version uses RC-5's restore, with that version's tree as the
    /// source — there is no second restore implementation.</b>
    ///
    /// <para>Everything R-rc5-12c guarantees therefore applies unchanged: the state being replaced is
    /// kept first, files added since are taken away, ignored files are left alone, the recording flag
    /// and the policy files survive, and an interruption is detectable. The one difference is what the
    /// following version's line names.</para>
    ///
    /// <para><b>The caller must still offer up unsaved work first and reload the open documents
    /// afterwards</b> (R-rc5-12b), exactly as for a restore point.</para>
    /// </summary>
    public RestoreResult? GoBackToVersion(string? workspaceRoot, HistoryVersion version)
        => Restore(workspaceRoot, HistoryBrowser.AsRestorePoint(version));

    /// <summary>R-rc7-13. Documents changed in two places at once, awaiting a choice.</summary>
    public IReadOnlyList<DocumentClash> Clashes(string? workspaceRoot)
        => Bind(workspaceRoot) is { } git ? DocumentClashes.Find(git) : [];

    /// <summary>
    /// R-rc7-13. <b>Keeps one side whole.</b> There is no third content and no path that could produce
    /// one — a merged design that is silently wrong is worse than a clash, because the clash is at
    /// least visible.
    /// </summary>
    public bool KeepSide(string? workspaceRoot, DocumentClash clash, ClashSide side)
    {
        if (Bind(workspaceRoot) is not { } git) return false;

        var outcome = DocumentClashes.Keep(git, clash, side);
        if (outcome.Diagnostic is { } d) _messages.PostDiagnostic(d);
        else _messages.PostDiagnostic(
                 HistoryMessages.ChoiceKept(clash.RelativePath, DocumentClashes.Describe(side)));

        Changed?.Invoke();
        return outcome.Ok;
    }

    // ── Shared ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A driver bound to the workspace, or null. <b>Absence is silent</b> (R-rc3-3): no git and no
    /// repository both answer null and say nothing, because a designer who does not want this feature
    /// should never learn it exists.
    /// </summary>
    private static GitCommand? Bind(string? workspaceRoot)
    {
        if (workspaceRoot is not { Length: > 0 } || !Directory.Exists(workspaceRoot)) return null;
        if (GitCommand.For(workspaceRoot) is not { } git) return null;
        if (!git.IsRepositoryRoot()) return null;

        git.Identity ??= RevisionIdentity.Resolve(git);
        return git;
    }

    /// <summary>
    /// Whether this workspace has a history at all — what the Save As report asks before adding
    /// R-rc5-19's sentence, because a workspace with nothing to lose must not be told it lost it.
    ///
    /// <para><b>"Has a history" is TWO questions, not one</b>, and counting restore points alone
    /// answered the wrong one. A designer who has only ever kept VERSIONS has no restore points at all
    /// — so a Save As from their workspace said nothing, and the copy silently began a fresh history
    /// while the sentence that exists to stop exactly that discovery was withheld. This is the same
    /// mistake RC-7 found in the off/on transition, in a second place: see
    /// <c>RevisionSwitch.ExistingRepository</c>, which asks both questions for the same reason.</para>
    /// </summary>
    public static bool HasHistory(string? workspaceRoot)
        => Bind(workspaceRoot) is { } git
           && (CheckpointReferences.List(git).Count > 0
            || WorkspaceCommit.CurrentVersionId(git) is not null);
}

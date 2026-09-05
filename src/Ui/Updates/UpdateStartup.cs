using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace CircuitRF.Ui.Updates;

/// <summary>
/// The two calls the applications make: one in <c>Main</c> before Avalonia, one when the first
/// window has actually appeared.
///
/// <para>Everything here is wrapped so that no failure in the update subsystem can stop the
/// application starting. An updater that can prevent a launch is worse than no updater.</para>
/// </summary>
public static class UpdateStartup
{
    /// <summary>
    /// The one call the three <c>Application</c> classes make once their first window is on screen:
    /// confirm the launch, post anything the pre-UI phase produced, and schedule the check.
    ///
    /// <para><b>harmonicaRF and wBond standalone have no Message Panel</b> — <c>MessagesTool</c> is a
    /// docking tool of circuitRF's workspace and neither shell has one. So <paramref name="messages"/>
    /// is null there and the update still stages silently, which is the honest behaviour given the
    /// surface that exists. Recorded rather than worked around: inventing a toast for those two apps
    /// would be a second notification mechanism, and R-AU-48 is emphatic about not growing UI here.</para>
    /// </summary>
    public static void AfterFirstWindow(Messages.IMessageSink? messages)
    {
        try
        {
            string? notice = NoteFirstWindowShown();
            if (notice is not null) messages?.Warning(notice);

            // A disk image this application left mounted when it was force-quit mid-stage. Swept here
            // rather than in RunBeforeUi because it spawns a process and nothing about a launch may
            // wait on one; never awaited, because nothing about a window depends on the answer. It is
            // the only update debris outside our own directories, so nothing else can reclaim it.
            if (OperatingSystem.IsMacOS())
                _ = Task.Run(() => UpdateStager.ReclaimAbandonedMountsAsync(CancellationToken.None));

            UpdateScheduler.ScheduleFirstCheck(messages);
        }
        catch (Exception) { /* never the reason a window fails to finish opening */ }
    }

    /// <summary>
    /// Called early in <c>Program.Main</c>, before Avalonia is initialised — but AFTER
    /// <c>CrashReporter.Install</c>, which is why the exec below has to hand the session file off
    /// rather than abandon it.
    ///
    /// <para>It reclaims debris, resolves the outstanding startup attempt (raising it, or reverting a
    /// version that has failed to start twice), and applies anything staged. On macOS both an applied
    /// update and a rollback hand this launch to the resulting bundle through Launch Services and
    /// this call never returns; everywhere else the pointer has been flipped before the stub started
    /// anything, so there is nothing to re-exec. <see cref="HandOverTo"/> has the mechanisms and the
    /// order they are tried in.</para>
    /// </summary>
    public static void RunBeforeUi(string[] args)
    {
        try
        {
            InstallSite site  = UpdateInstallSite.Detect();
            UpdateState state = UpdateStateIo.Load();
            string running    = UpdateReclaimer.RunningDirectoryName();

            SwapResult result = UpdateSwap.ApplyAtLaunch(site, state, UpdatePaths.Root, running);

            switch (result.Outcome)
            {
                case SwapOutcome.AttemptRecorded:
                    // This launch IS the swapped-in version proving it starts. Raise the counter
                    // BEFORE any of it runs; NoteFirstWindowShown clears it once a window appears,
                    // and a launch that never gets that far leaves the count behind on purpose.
                    UpdateStateIo.Update(s => s.LaunchAttempts = (s.LaunchAttempts ?? 0) + 1);
                    return;

                case SwapOutcome.RolledBack:
                    UpdateStateIo.Update(s =>
                    {
                        if (s.PendingVersion is not null) s.Blacklist_Add(s.PendingVersion);
                        s.PendingVersion = null;
                        s.PendingPath    = null;
                        s.LaunchAttempts = null;
                        s.PendingNotice  =
                            $"{UpdateApp.Name} could not start after updating to {result.Detail}, so the "
                            + "previous version was restored. The failed version will not be offered "
                            + "again; a crash report may have been written.";
                    });

                    // This process IS the version that does not work — macOS exchanged the bundle back
                    // under its feet, and the pointer layout flipped `current` back after the stub had
                    // already read it. Either way it must not carry on as that version, so it hands
                    // over to the restored one.
                    if (result.NewExecutable is not null && File.Exists(result.NewExecutable))
                        HandOverTo(result.NewExecutable, args);
                    return;

                case SwapOutcome.SwapAlreadyApplied:
                    // An earlier launch swapped and was killed before it could record it. This process
                    // IS the new version, so there is nothing to exec — only the bookkeeping to
                    // finish. On macOS, without it the next launch would exchange the pair back and
                    // silently downgrade the user; in the versioned layout a re-flip is idempotent, so
                    // what it saves there is the rollback record rather than the version.
                    //
                    // The previous version is recorded as a PATH and no version string: RecordSwap's
                    // AppVersion.Display is right on the ordinary path, where that call is made by the
                    // OLD version before it execs, and wrong here, where it is made by the new one.
                    // macOS rolls back by path (Revert reads updates/previous, never the string), so
                    // no version at all is the honest record rather than a confidently wrong one.
                    RecordSwap(state, result.Detail, previousDirectoryName: null, previousVersion: null);
                    return;

                case SwapOutcome.PointerFlipped:
                    // The flip is for the NEXT launch, and this session is still the OLD tree — the
                    // stub resolved `current` before this process existed. So it records what to go
                    // back to and raises no attempt counter (counting one here is what used to make
                    // rollback inert), and then HANDS OVER to the version it just pointed at.
                    //
                    // Without that hand-over the update only appears at the launch AFTER this one:
                    // the user relaunched exactly as the Message Panel asked, still got the old
                    // version, and had to launch a SECOND time (owner-reported on Windows,
                    // 2026-09-04). macOS never showed it because a bundle swap execs. The design's
                    // claim that "the stub has not started the app yet, so there is nothing to
                    // re-exec" was simply false — the swap is made by the app the stub already
                    // started, not by the stub.
                    RecordSwap(state, result.Detail, result.PreviousDirectoryName, AppVersion.Display);

                    if (result.NewExecutable is not null && File.Exists(result.NewExecutable))
                        HandOverTo(result.NewExecutable, args);

                    // Only reached if the hand-over failed. `current` is flipped and the record is
                    // durable, so this session finishes as the old version and the next launch is the
                    // new one — which is exactly the behaviour this case used to have unconditionally.
                    return;

                case SwapOutcome.BundleSwapped:
                    RecordSwap(state, result.Detail, previousDirectoryName: null,
                               previousVersion: AppVersion.Display);
                    if (result.NewExecutable is not null && File.Exists(result.NewExecutable))
                        HandOverTo(result.NewExecutable, args);
                    // Only reached if execv failed; the swap already happened, so carrying on runs
                    // the OLD process image against the NEW tree for this session only. Harmless,
                    // and better than refusing to start.
                    return;

                default:
                    return;
            }
        }
        catch (Exception)
        {
            // An updater that can prevent a launch is worse than no updater.
        }
    }

    /// <summary>
    /// Makes <paramref name="executable"/> this launch, closing this session's crash-report file
    /// first. Returns only if the hand-over did not happen; on every other path this process is gone.
    ///
    /// <para><b>The close is the point.</b> An exec keeps the pid and discards the runtime, so
    /// <c>ProcessExit</c> never fires and the crash reporter never learns the session ended — it
    /// simply stops existing mid-session. The replacement image starts two seconds later, sweeps the
    /// report directory, finds that session file owned by nobody, and announces to the user that
    /// circuitRF "did not shut down cleanly last time". It shut down perfectly; it updated. Telling
    /// the reporter BEFORE the hand-over is what makes an update look like the clean handoff it is.
    /// A hand-over that then fails re-arms the reporter rather than leaving this session blind for
    /// the rest of its run.</para>
    ///
    /// <para><b>Three mechanisms, in a deliberate order.</b> macOS goes through Launch Services —
    /// <see cref="AppRelaunch"/> has the whole reason, and it is not a stylistic one: an
    /// <c>execv</c> keeps the launch-time application attribution macOS resolves a protected-folder
    /// grant against, and the update has just exchanged the bundle that attribution names, so the
    /// updated session is denied <c>~/Documents</c> until the user quits and launches again. Linux
    /// keeps <c>execv</c>: it keeps the pid, the process clock and the parent's handle on this
    /// process, so nothing outside notices the swap at all, and there is no TCC to go stale. On
    /// Windows, which has no <c>execv</c>, the successor is STARTED and this process exits, which the
    /// stub sees as its child finishing — so the stub exits too and the new version runs with no
    /// parent. That is the one visible difference, it lasts for one launch per update, and the
    /// process itself is byte-for-byte the one the stub would have created from the flipped pointer a
    /// launch later.</para>
    ///
    /// <para><b>Each mechanism falls through to the next</b>, so no route being available can leave
    /// the user with no application. A Launch Services request that is refused still reaches
    /// <c>execv</c>, which is precisely the behaviour this method had before — a stale privacy
    /// attribution for one session is a bad outcome; not starting at all is a much worse one.</para>
    ///
    /// <para><b>Nothing is at risk in any of them.</b> This runs in <c>Main</c> before Avalonia, so
    /// there is no window, no open workspace and nothing unsaved — which is why it is not the
    /// "Relaunch" button §10 refuses to grow, even though it relaunches.</para>
    /// </summary>
    private static void HandOverTo(string executable, string[] args)
    {
        Diagnostics.CrashReporter.HandOffToExec();

        // macOS first, and only macOS: launchd must be the one that spawns the successor, or the
        // updated session inherits an attribution pointing at the bundle this update just replaced.
        if (AppRelaunch.TryRelaunchBundle(executable, args)) Environment.Exit(0);

        // Returns only on failure; on success this process has already become the new one.
        UpdateSwap.ExecReplace(executable, args);

        if (StartSuccessor(executable, args)) Environment.Exit(0);

        Diagnostics.CrashReporter.ResumeAfterExec();
    }

    /// <summary>
    /// Starts <paramref name="executable"/> as an ordinary child and reports whether it began. Used
    /// only where <c>execv</c> does not exist, and deliberately not redirecting anything: the child
    /// must outlive this process, so it is given the same console, environment and arguments it would
    /// have been given by the stub.
    /// </summary>
    private static bool StartSuccessor(string executable, string[] args)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(executable)
            {
                UseShellExecute  = false,
                WorkingDirectory = Path.GetDirectoryName(executable) ?? string.Empty,
            };
            foreach (string a in args) psi.ArgumentList.Add(a);

            return System.Diagnostics.Process.Start(psi) is not null;
        }
        catch (Exception)
        {
            // A launch that cannot start its successor still has a working old version to be.
            return false;
        }
    }

    /// <summary>
    /// Moves a staged version to pending and records what to fall back to. <b>The previous version is
    /// identified by DIRECTORY NAME, not by version string</b>, because those two are not the same
    /// text: <c>AppVersion.Display</c> normalises a <c>1.0</c> tag to <c>1.0.0</c> while the directory
    /// is named after the tag the packaging script interpolated.
    /// </summary>
    private static void RecordSwap(UpdateState before, string pendingPath, string? previousDirectoryName,
                                   string? previousVersion)
        => UpdateStateIo.Update(s =>
        {
            s.PendingVersion = before.StagedVersion;
            s.PendingPath    = pendingPath;

            s.PreviousVersion = previousVersion;
            s.PreviousPath    = previousDirectoryName;

            s.LaunchAttempts = 0;

            s.StagedVersion      = null;
            s.StagedPath         = null;
            s.StagedIsPreRelease = null;

            // This record supersedes the in-progress marker, whichever outcome wrote it.
            s.SwapInProgress = null;
        });

    /// <summary>
    /// Called once the first window is actually on screen. This is what turns "it started" from a
    /// guess into a fact, and it is where the retained previous version is finally released. Returns
    /// a line to post, or null.
    ///
    /// <para><b>Only the pending version's OWN launch confirms anything.</b> In the versioned layout
    /// the session that flipped the pointer is still running the old tree; letting its window clear
    /// the counter is precisely how a broken release used to escape the rollback.</para>
    ///
    /// <para><b>The previous version is deleted only here</b>, and only after that confirmed launch:
    /// until this point it is the rollback the whole design's insurance rests on, and the reclaim
    /// order refuses to touch it for the same reason.</para>
    /// </summary>
    public static string? NoteFirstWindowShown()
    {
        try
        {
            UpdateState state = UpdateStateIo.Load();

            // A notice survives in the state file precisely because the version that earned it could
            // not stay up long enough to show one. Post it at the first window that does open.
            string? notice = state.PendingNotice;
            if (notice is not null) UpdateStateIo.Update(s => s.PendingNotice = null);

            if (state.PendingVersion is null) return notice;

            InstallSite site = UpdateInstallSite.Detect();
            string running   = UpdateReclaimer.RunningDirectoryName();

            if (!UpdateSwap.LaunchBelongsToPending(site, state, running)) return notice;

            UpdateStateIo.Update(s =>
            {
                s.LaunchAttempts = null;
                s.PendingVersion = null;
                s.PendingPath    = null;
            });

            ReleasePreviousVersion(site, state, running);
            return notice;
        }
        catch (Exception) { return null; /* never the reason a window fails to finish opening */ }
    }

    /// <summary>
    /// Gives back the one retained generation. Steady-state disk footprint is zero: exactly one
    /// previous version is ever kept, never a history of them.
    /// </summary>
    private static void ReleasePreviousVersion(InstallSite site, UpdateState state, string runningDirectoryName)
    {
        var reclaimer = new UpdateReclaimer(
            UpdatePaths.Root,
            site.Shape == InstallShape.VersionedPointer ? site.Root : null,
            runningDirectoryName,
            state.PreviousPath);

        var keep = new List<string>();
        if (site.Shape == InstallShape.VersionedPointer)
        {
            string? current = UpdateSwap.ReadCurrent(site.Root);
            if (current is not null) keep.Add(current);
        }

        // enough: () => false runs every step; previousVersionReleasable: true is the whole point of
        // being called from here — the version we were insuring against has now started.
        reclaimer.ReclaimUntil(() => false, previousVersionReleasable: true, runningVersionDirs: keep);

        UpdateStateIo.Update(s =>
        {
            s.PreviousVersion = null;
            s.PreviousPath    = null;
        });
    }
}

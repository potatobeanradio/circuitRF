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
    /// update and a rollback <c>execv</c> the resulting executable and this call never returns;
    /// everywhere else the pointer has been flipped before the stub started anything, so there is
    /// nothing to re-exec.</para>
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

                    // macOS exchanged the bundle back under this process's feet, so it must not carry
                    // on as the version that does not work. The pointer layout needs no re-exec: the
                    // stub reads `current` at the start of the NEXT launch.
                    if (result.NewExecutable is not null && File.Exists(result.NewExecutable))
                        ExecReplacingThisProcess(result.NewExecutable, args);
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
                    // The flip is for the NEXT launch. This session keeps running the OLD tree, so it
                    // records what to go back to and raises nothing — counting an attempt here is what
                    // used to make rollback inert.
                    RecordSwap(state, result.Detail, result.PreviousDirectoryName, AppVersion.Display);
                    return;

                case SwapOutcome.BundleSwapped:
                    RecordSwap(state, result.Detail, previousDirectoryName: null,
                               previousVersion: AppVersion.Display);
                    if (result.NewExecutable is not null && File.Exists(result.NewExecutable))
                        ExecReplacingThisProcess(result.NewExecutable, args);
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
    /// <c>execv</c>s <paramref name="executable"/>, closing this session's crash-report file first.
    ///
    /// <para><b>The close is the point.</b> An exec keeps the pid and discards the runtime, so
    /// <c>ProcessExit</c> never fires and the crash reporter never learns the session ended — it
    /// simply stops existing mid-session. The replacement image starts two seconds later, sweeps the
    /// report directory, finds that session file owned by nobody, and announces to the user that
    /// circuitRF "did not shut down cleanly last time". It shut down perfectly; it updated. Telling
    /// the reporter BEFORE the exec is what makes an update look like the clean handoff it is.</para>
    ///
    /// <para><c>execv</c> returns only on failure, and this session then carries on running — so the
    /// reporter is re-armed on that path rather than left blind for the rest of the run.</para>
    /// </summary>
    private static void ExecReplacingThisProcess(string executable, string[] args)
    {
        Diagnostics.CrashReporter.HandOffToExec();
        UpdateSwap.ExecReplace(executable, args);
        Diagnostics.CrashReporter.ResumeAfterExec();
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

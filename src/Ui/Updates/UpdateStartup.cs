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

            // BEFORE the exchange, and this write is why it is here rather than beside its partner
            // below. Setting the flag touches CircuitRF.Diagnostics, and a session that has already
            // exchanged its own bundle cannot LOAD an assembly for the first time — the bundle it
            // would read it from has just been replaced (NativeLaunch carries the measurement). This
            // launch is about to replace the bundle it was started from, so it says so now and
            // corrects itself below; nothing reads the flag in between, and nothing is refused this
            // early.
            if (site.Shape == InstallShape.MacOsBundle)
                CircuitRF.Diagnostics.FileAccessDiagnostics.AppBundleReplacedThisSession = true;

            SwapResult result = UpdateSwap.ApplyAtLaunch(site, state, UpdatePaths.Root, running);

            // From here on this process may no longer be trusted to touch a protected folder on
            // macOS: the exchange has moved the bundle it was LAUNCHED from, and that is the identity
            // the system resolves a Documents/Desktop grant against. HandOverTo leaves rather than let
            // that session continue — this flag is the belt for anything that gets past it, so a
            // refusal explains itself instead of sending the user to Privacy & Security.
            CircuitRF.Diagnostics.FileAccessDiagnostics.AppBundleReplacedThisSession =
                ThisSessionReplacedItsOwnBundle(site, result);

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

                    // Reached only when the restored executable is not where the exchange put it —
                    // HandOverTo itself never returns on a macOS bundle. The notice is already
                    // recorded, so this launch simply ends; see LeaveRatherThanOutliveTheExchange.
                    LeaveRatherThanOutliveTheExchange(site, result, notice: null);
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

                    // Reached only when the installed executable is not where the exchange put it —
                    // HandOverTo itself never returns on a macOS bundle. Carrying on used to be
                    // described here as "harmless": it is not, and see
                    // LeaveRatherThanOutliveTheExchange for what it actually costs.
                    LeaveRatherThanOutliveTheExchange(site, result,
                        "the new version's executable was not where the exchange put it");
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
    /// <para><b>A macOS bundle has exactly ONE mechanism and no fall-back at all.</b> The successor is
    /// asked for through Launch Services, and if Launch Services will not take it this process
    /// LEAVES — see <see cref="LeaveTheUpdateForTheNextLaunch"/>. <see cref="AppRelaunch"/> carries
    /// the evidence; the rule it produces is that once the bundle has been exchanged, every process
    /// that outlives the exchange is denied every protected folder with no prompt, <c>execv</c>'d or
    /// not. So there is nothing for a fall-back to preserve: the choice is not between an application
    /// and no application, it is between an application that cannot open the user's workspaces and
    /// one more launch. It was written as a preference once, it fell back exactly as instructed, and
    /// that is how the same bug reached the owner twice.</para>
    ///
    /// <para><b>Everywhere else keeps what it had, because nothing else has a TCC identity to lose.</b>
    /// Linux keeps <c>execv</c>: it keeps the pid, the process clock and the parent's handle on this
    /// process, so nothing outside notices the swap at all. On Windows, which has no <c>execv</c>,
    /// the successor is STARTED and this process exits, which the stub sees as its child finishing —
    /// so the stub exits too and the new version runs with no parent. That is the one visible
    /// difference, it lasts for one launch per update, and the process itself is byte-for-byte the
    /// one the stub would have created from the flipped pointer a launch later. The macOS
    /// VERSIONED-POINTER layout is on this side of the line too: it replaces no bundle, so the
    /// launch-time identity it was given still names the application on disk.</para>
    ///
    /// <para><b>Nothing is at risk in any of them.</b> This runs in <c>Main</c> before Avalonia, so
    /// there is no window, no open workspace and nothing unsaved — which is why it may exec freely,
    /// unlike the Messages panel's Relaunch button (§10.2.1), which starts from a live GUI with
    /// windows to ask about and therefore leaves by the ordinary Quit. See
    /// <see cref="AppRelaunch.StartSuccessor"/> for that route and how the two differ.</para>
    /// </summary>
    private static void HandOverTo(string executable, string[] args)
    {
        Diagnostics.CrashReporter.HandOffToExec();

        if (HandsOverThroughLaunchServicesOnly(executable))
        {
            // NativeLaunch.Exit, not Environment.Exit: the bundle this process reads its own
            // assemblies from has just been exchanged, so raising ProcessExit — a hook any part of
            // the application may have taken — is a managed call this session can no longer make
            // safely. See NativeLaunch for the measurement that establishes it.
            if (AppRelaunch.TryRelaunchBundle(executable, args, out string? refusal))
                NativeLaunch.Exit(0);

            LeaveTheUpdateForTheNextLaunch(refusal);   // never returns
            return;
        }

        // Returns only on failure; on success this process has already become the new one.
        UpdateSwap.ExecReplace(executable, args);

        // Windows has no `_exit` to call and NativeLaunch falls back to Environment.Exit there, which
        // is the right answer: nothing has replaced the file this process reads its assemblies from.
        // Linux after a pointer flip HAS, so it leaves the same way macOS does.
        if (StartSuccessor(executable, args)) NativeLaunch.Exit(0);

        Diagnostics.CrashReporter.ResumeAfterExec();
    }

    /// <summary>
    /// Whether <paramref name="executable"/> is one this process may only hand over to through Launch
    /// Services — a macOS <c>.app</c>, which is the only layout where applying the update EXCHANGES
    /// the bundle the running process was launched from.
    ///
    /// <para>Separate and named so the rule is one testable expression rather than a condition spelled
    /// out inside the method that acts on it.</para>
    /// </summary>
    internal static bool HandsOverThroughLaunchServicesOnly(string executable)
        => OperatingSystem.IsMacOS() && AppRelaunch.BundleRootOf(executable) is not null;

    /// <summary>
    /// Ends this launch with the update installed and unstarted, after Launch Services would not
    /// start it. Never returns.
    ///
    /// <para><b>Leaving is the correct outcome, not a surrender.</b> The exchange is already done and
    /// already durable, so the version on disk IS the new one and the next ordinary launch — a Dock
    /// click, a double-clicked workspace, a login item — is spawned by launchd with an identity that
    /// names it. Carrying on instead would produce a session that is denied <c>~/Documents</c> and
    /// <c>~/Desktop</c> with no prompt and no way for the user to tell why.</para>
    ///
    /// <para><b>The notice is written where a launch that never showed a window can still be heard
    /// from.</b> <see cref="NoteFirstWindowShown"/> posts it at the first window that does open, which
    /// is the next launch — and it CARRIES THE REFUSAL, because the whole cost of this bug the second
    /// time was not knowing whether <c>open</c> had run.</para>
    /// </summary>
    private static void LeaveTheUpdateForTheNextLaunch(string? refusal)
    {
        try
        {
            UpdateStateIo.Update(s => s.PendingNotice =
                $"{UpdateApp.Name} has finished installing its update, but the new version could not "
                + "be started automatically"
                + (string.IsNullOrWhiteSpace(refusal) ? "" : $" ({refusal})")
                + $", so the previous session closed instead. This launch IS the new version — "
                + "nothing was lost and nothing needs to be repaired.");
        }
        catch (Exception) { /* a notice is not worth failing an exit over */ }

        NativeLaunch.Exit(0);
    }

    /// <summary>
    /// Whether this launch is one that EXCHANGED the macOS <c>.app</c> it was started from — the one
    /// state in which the process may not be trusted with a protected folder, because macOS resolves
    /// the grant against the bundle the process was launched from and that bundle has just been moved
    /// to <c>updates/previous</c> (and is deleted as soon as a window confirms the new version).
    ///
    /// <para>One expression, used twice: it selects the remedy a refusal offers
    /// (<c>FileAccessDiagnostics.AppBundleReplacedThisSession</c>) and it decides whether this launch
    /// may continue at all (<see cref="LeaveRatherThanOutliveTheExchange"/>). Those two must never
    /// disagree.</para>
    /// </summary>
    private static bool ThisSessionReplacedItsOwnBundle(InstallSite site, SwapResult result)
        => site.Shape == InstallShape.MacOsBundle
        && result.Outcome is SwapOutcome.BundleSwapped or SwapOutcome.RolledBack;

    /// <summary>
    /// Ends a launch that has exchanged its own bundle and has no successor to hand to. Returns only
    /// when this is not that launch.
    ///
    /// <para><b>The last way a denied session could still exist, and it was described here as
    /// "harmless".</b> <see cref="HandOverTo"/> never returns on a macOS bundle — it either hands to
    /// Launch Services or leaves — so the only way past it is the executable not being where the
    /// exchange put it, and that branch used to fall through to "carrying on runs the OLD process
    /// image against the NEW tree for this session only. Harmless." It is not harmless: that session
    /// is denied <c>~/Downloads</c>, <c>~/Documents</c> and <c>~/Desktop</c> with NO prompt, because
    /// the identity the grant is resolved against names a bundle that is no longer installed. The
    /// user is then told to switch on a privacy setting that was never off. Leaving costs one more
    /// launch and the exchange is already durable, so the version on disk is the new one either
    /// way.</para>
    ///
    /// <para><paramref name="notice"/> is null where the caller has already recorded one — a rollback
    /// explains itself and must not have that explanation overwritten.</para>
    /// </summary>
    private static void LeaveRatherThanOutliveTheExchange(InstallSite site, SwapResult result,
                                                          string? notice)
    {
        if (!ThisSessionReplacedItsOwnBundle(site, result)) return;

        if (notice is not null) LeaveTheUpdateForTheNextLaunch(notice);   // never returns
        NativeLaunch.Exit(0);
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

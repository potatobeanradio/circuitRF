using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace CircuitRF.Ui.Updates;

/// <summary>What the launch-time swap did.</summary>
public enum SwapOutcome
{
    /// <summary>Nothing was staged. The overwhelmingly common case, and it costs one file check.</summary>
    Nothing,

    /// <summary>The pointer was re-written. The stub has not started anything, so there is nothing to re-exec.</summary>
    PointerFlipped,

    /// <summary>The bundle was exchanged. The caller must now <c>execv</c> the new executable.</summary>
    BundleSwapped,

    /// <summary>The staged version was rolled back to the previous one after repeated startup failure.</summary>
    RolledBack,

    /// <summary>
    /// This launch IS the swapped-in version's attempt to prove it starts, and the counter was raised
    /// for it. Nothing moved on disk.
    /// </summary>
    AttemptRecorded,

    /// <summary>
    /// The swap had already happened when an earlier launch was killed before it could record it.
    /// This process IS the new version, so there is nothing to move and nothing to <c>execv</c> —
    /// only the bookkeeping to finish. Both install shapes reach this, by different routes: a macOS
    /// bundle exchange that completed, or a pointer that already names the staged version.
    /// </summary>
    SwapAlreadyApplied,

    Failed,
}

/// <summary>The outcome, plus what the caller needs to finish the job.</summary>
public sealed record SwapResult(SwapOutcome Outcome, string? NewExecutable, bool WasAtomic, string Detail)
{
    /// <summary>
    /// The <c>app-&lt;ver&gt;</c> directory this process is running out of, on a pointer flip — which
    /// is what the rollback goes BACK to, and is not derivable from the version string
    /// (<c>AppVersion.Display</c> normalises <c>1.0</c> to <c>1.0.0</c> while the directory is named
    /// after the tag). Null for every other outcome.
    /// </summary>
    public string? PreviousDirectoryName { get; init; }
}

/// <summary>
/// Puts a staged version in place — <b>at the next launch, in <c>Program.Main</c>, before Avalonia
/// initialises</b>.
///
/// <para><b>Why launch and not quit.</b> Quitting looks tempting but needs a detached helper process
/// to act after the app is gone, and it loses the race against a force-quit or a crash. Doing it in
/// <c>Main</c> before any framework is up means no helper, no race, and the app tree is
/// <i>provably</i> not in use. On Windows and Linux the stub model removes even the re-exec: the
/// swap is one text file or one symlink, flipped before the real process starts.</para>
///
/// <para><b>Never mid-session.</b> A self-contained .NET app does not load every assembly eagerly
/// and Avalonia resolves some resources lazily; replacing the tree underneath a running process is
/// a class of bug that reproduces on someone else's machine, once, six weeks later.</para>
/// </summary>
public static class UpdateSwap
{
    /// <summary>Two failed startups revert. Cheap insurance, and the only insurance a user has.</summary>
    public const int MaxFailedStartups = 2;

    /// <summary>
    /// The whole launch-time sequence: reclaim debris, resolve the outstanding startup attempt (raise
    /// it, or revert if it has failed too often), then apply anything staged.
    ///
    /// <para><paramref name="runningDirectoryName"/> is the <c>app-&lt;ver&gt;</c> directory this
    /// process is executing from. It is what decides <b>whose</b> startup attempt this launch is, and
    /// it is a parameter only so a test can drive it.</para>
    /// </summary>
    /// <param name="runningVersion">The version of the application THIS process is, which on macOS is
    /// also the version sitting at the install path. It decides whether an interrupted exchange had
    /// already happened, and it is a parameter only so a test can drive it.</param>
    /// <param name="persist">How to make a state change durable. Defaults to
    /// <see cref="UpdateStateIo.Update"/>; a test supplies its own. This class writes state through
    /// exactly one field — see <see cref="UpdateState.SwapInProgress"/> — and everything else about
    /// the state file remains <c>UpdateStartup</c>'s.</param>
    public static SwapResult ApplyAtLaunch(
        InstallSite site, UpdateState state, string updatesRoot, string? runningDirectoryName = null,
        string? runningVersion = null, Action<Action<UpdateState>>? persist = null)
    {
        string running = runningDirectoryName ?? UpdateReclaimer.RunningDirectoryName();
        runningVersion ??= AppVersion.Display;
        persist        ??= UpdateStateIo.Update;

        // 1. Debris first, unconditionally — staging/ and every .partial tree. Safe without asking
        //    anything, because nothing incomplete has ever been given a real name. This is what makes
        //    a disk-full event self-limiting rather than cumulative.
        try
        {
            new UpdateReclaimer(
                    updatesRoot,
                    site.Shape == InstallShape.VersionedPointer ? site.Root : null,
                    running,
                    state.PreviousPath)
                .ReclaimDebris();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* next launch */ }

        // 1b. The one piece of update debris that lives OUTSIDE everything UpdateReclaimer is allowed
        //     to touch, which is why it is handled here instead: a `.swapaside-` bundle sits beside
        //     the installed application (/Applications), not under updates/, and the reclaimer's rule
        //     with teeth is that it never leaves our own directories.
        try { ReclaimSwapAside(site, updatesRoot); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* next launch */ }

        // 1c. An exchange that a kill interrupted, before the outstanding-attempt logic can act on a
        //     state file that does not match the disk. Mutually exclusive with step 2 in practice:
        //     the record that sets PendingVersion is the same one that clears this marker.
        SwapResult? interrupted = ResolveInterruptedSwap(site, state, updatesRoot, runningVersion, persist);
        if (interrupted is not null) return interrupted;

        // 2. The outstanding attempt, before anything new is applied — a version that has failed to
        //    start twice must not be given a third chance while a working one sits beside it.
        if (state.PendingVersion is not null && LaunchBelongsToPending(site, state, running))
        {
            if ((state.LaunchAttempts ?? 0) >= MaxFailedStartups)
                return Revert(site, state, updatesRoot);

            return new SwapResult(SwapOutcome.AttemptRecorded, null, false, state.PendingVersion);
        }

        if (state.StagedVersion is null || state.StagedPath is null)
            return new SwapResult(SwapOutcome.Nothing, null, false, "nothing staged");

        return site.Shape switch
        {
            InstallShape.VersionedPointer => FlipPointer(site, state, running),
            InstallShape.MacOsBundle      => SwapBundle(site, state, updatesRoot, persist),
            _ => new SwapResult(SwapOutcome.Failed, null, false, "this install shape cannot self-update"),
        };
    }

    /// <summary>
    /// Whether THIS launch is the pending version's own attempt to start.
    ///
    /// <para><b>The two shapes answer differently, and that asymmetry is the whole bug fix.</b> On
    /// macOS the swap and the new version's first run happen in one launch — the bundle is exchanged
    /// and then <c>execv</c>ed — so once something is pending, every launch is the pending version's.
    /// In the versioned layout the swap is a pointer flip made by the OLD version for the NEXT
    /// launch, so the flipping session must NOT count: it is still running the previous tree, and
    /// counting it there is exactly what cleared the counter before the new version ever ran.</para>
    /// </summary>
    public static bool LaunchBelongsToPending(InstallSite site, UpdateState state, string runningDirectoryName)
        => site.Shape != InstallShape.VersionedPointer
           || (state.PendingPath is not null
               && string.Equals(state.PendingPath, runningDirectoryName, StringComparison.Ordinal));

    // ── Windows and Linux: one pointer ───────────────────────────────────────────────────────

    private static SwapResult FlipPointer(InstallSite site, UpdateState state, string runningDirectoryName)
    {
        string versionDir = state.StagedPath!;
        if (!UpdateInstallSite.IsSafeVersionDirectoryName(versionDir))
            return new SwapResult(SwapOutcome.Failed, null, false, $"'{versionDir}' is not a version directory");

        if (!Directory.Exists(Path.Combine(site.Root, versionDir)))
            return new SwapResult(SwapOutcome.Failed, null, false, $"{versionDir} is not there");

        // The same two-operation gap the macOS path has — the pointer write and the record of it are
        // separate — but with a different consequence, because re-writing `current` with the value it
        // already holds is idempotent and cannot downgrade anyone. What a re-flip DOES get wrong is
        // the rollback: it reports the running directory as the previous version, and on this second
        // launch the running directory IS the new version. `Revert` would then restore the failing
        // version to itself and report success — precisely the bug the macOS half of Revert had.
        //
        // Both halves of the test are needed. `current` naming the staged version is also the ordinary
        // state between a flip and the next launch, when this process is still the OLD tree and has
        // every reason to record itself as the rollback; only the running directory separates the two.
        if (string.Equals(ReadCurrent(site.Root), versionDir, StringComparison.Ordinal)
            && string.Equals(runningDirectoryName, versionDir, StringComparison.Ordinal))
        {
            // No previous directory name, deliberately. Which app-<ver> was the predecessor is not
            // recoverable after the fact — several may be on disk and none of them says so — and a
            // refusal from Revert ("the previous version is gone") is worth far more than a rollback
            // that silently does nothing.
            return new SwapResult(SwapOutcome.SwapAlreadyApplied, null, true, versionDir);
        }

        try
        {
            WriteCurrent(site.Root, versionDir);
            return new SwapResult(SwapOutcome.PointerFlipped, null, true, versionDir)
            {
                PreviousDirectoryName = runningDirectoryName,
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new SwapResult(SwapOutcome.Failed, null, false, e.Message);
        }
    }

    /// <summary>
    /// Re-points <c>current</c> — <b>never in place</b>.
    ///
    /// <para>The classic disaster is truncate-then-write: the file is opened for truncation, the
    /// write fails with ENOSPC, and <c>current</c> is now empty. The stub no longer knows what to
    /// run and the application will not start at all — a full disk turned into an uninstallation,
    /// and nobody would ever connect the two. Writing <c>current.tmp</c> and renaming over the
    /// original makes that impossible, and costs nothing: if the temp write fails there is nothing
    /// to clean up and <c>current</c> was never touched.</para>
    /// </summary>
    public static void WriteCurrent(string root, string versionDirectoryName)
    {
        // The last line of defence, here rather than only in the callers, so no future one can put
        // something into `current` that the stub will refuse to launch — or, worse, will not.
        if (!UpdateInstallSite.IsSafeVersionDirectoryName(versionDirectoryName))
            throw new ArgumentException($"'{versionDirectoryName}' is not a version directory name.",
                                        nameof(versionDirectoryName));

        string pointer = Path.Combine(root, UpdateInstallSite.CurrentPointerName);

        // A symlink on Linux, a text file on Windows — and both are re-pointed the same way.
        if (AtomicFile.IsSymlink(pointer))
            AtomicFile.WriteSymlinkAtomic(pointer, versionDirectoryName);
        else
            AtomicFile.WriteAllTextAtomic(pointer, versionDirectoryName);
    }

    /// <summary>Reads <c>current</c>, whichever form it takes. Null when it is missing or unreadable.</summary>
    public static string? ReadCurrent(string root)
    {
        string pointer = Path.Combine(root, UpdateInstallSite.CurrentPointerName);
        try
        {
            FileSystemInfo? link = File.ResolveLinkTarget(pointer, returnFinalTarget: false);
            if (link is not null) return Path.GetFileName(Path.TrimEndingDirectorySeparator(link.FullName));

            string text = File.ReadAllText(pointer).Trim();
            return text.Length == 0 ? null : text;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    // ── macOS: the bundle IS the launch path ─────────────────────────────────────────────────

    private static SwapResult SwapBundle(InstallSite site, UpdateState state, string updatesRoot,
                                         Action<Action<UpdateState>> persist)
    {
        string staged = state.StagedPath!;

        // THE SAME RULE THE VERSIONED PATH APPLIES TO ITS OWN POINTER, and it was missing here.
        // `staged_path` arrives from state.json — ordinary JSON in the user's application-data
        // directory — and on this path it is handed straight to a directory EXCHANGE with the
        // installed application, which on a shared Mac is /Applications/<app>.app and is what every
        // account on the machine launches. So the one thing that must be true of it is checked at
        // the line that acts on it: it has to be a bundle this updater itself staged, under
        // updates/staged/ (security review, 2026-08-25).
        if (!UpdatePaths.IsUnder(staged, Path.Combine(updatesRoot, "staged")))
            return new SwapResult(SwapOutcome.Failed, null, false,
                                  "the staged bundle is not one this updater staged");

        if (!Directory.Exists(staged))
            return new SwapResult(SwapOutcome.Failed, null, false, "the staged bundle is gone");

        string previous = Path.Combine(updatesRoot, "previous");

        try
        {
            // The replaced bundle is KEPT, under <AppData>/updates/previous/, until the new one has
            // launched successfully once. One release's worth of disk, and the single best piece of
            // insurance in the design.
            if (Directory.Exists(previous)) Directory.Delete(previous, true);
            Directory.CreateDirectory(Path.GetDirectoryName(previous)!);

            // Durable BEFORE the first thing that moves. The exchange and the record of it are two
            // operations, and until this line existed a kill between them left the state file saying
            // "staged" while the disk said "installed" — so the next launch exchanged the pair back
            // and silently downgraded the user. ResolveInterruptedSwap is what reads this.
            persist(s => s.SwapInProgress = staged);

            AtomicFile.SwapDirectories(site.Root, staged, out bool atomic);

            // After the exchange, `staged` holds what used to be installed.
            Directory.Move(staged, previous);

            string exe = Path.Combine(site.Root, "Contents", "MacOS", UpdateApp.Name);
            return new SwapResult(SwapOutcome.BundleSwapped, exe, atomic, staged);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new SwapResult(SwapOutcome.Failed, null, false, e.Message);
        }
    }

    /// <summary>
    /// Finishes, or clears, a non-atomic bundle exchange that a kill interrupted.
    ///
    /// <para><b>Where this comes from.</b> <see cref="AtomicFile.SwapDirectories"/> prefers
    /// <c>renamex_np(RENAME_SWAP)</c>, which is a true exchange and leaves nothing to reclaim; every
    /// Mac with an APFS or HFS+ volume takes that path, so what follows is the fallback's problem
    /// only. The fallback is three renames — original aside, staged into place, original into the
    /// staged slot — and a process killed after the second one leaves the displaced bundle stranded
    /// at <c>&lt;app&gt;.app.swapaside-&lt;id&gt;</c>. Nothing removed it, and nothing could: it is a
    /// sibling of the installed application, outside the updater's own tree.</para>
    ///
    /// <para><b>It is ADOPTED, not deleted, and that distinction is the whole point.</b> That
    /// stranded directory is the version the user was running a moment ago — the rollback §14 calls
    /// the single best piece of insurance in the design. Deleting it would leave the new version
    /// installed with nothing to revert TO, so a new version that then failed to start twice would
    /// find <c>updates/previous</c> empty and strand the user on a build that does not run. Moving it
    /// to <c>previous</c> is precisely the rename the kill interrupted, so this completes the
    /// operation rather than tidying up after it. Only when <c>previous</c> is already populated —
    /// which <see cref="SwapBundle"/>'s own ordering says cannot happen — is the stranded copy
    /// redundant, and only then is it deleted.</para>
    ///
    /// <para><b>The one case it must not touch.</b> A kill between the FIRST and second rename leaves
    /// nothing at the launch path at all, and the aside is then the user's only copy of the
    /// application. That state cannot reach this method (an application that is not there does not
    /// start), but the guard is written down rather than assumed, because the cost of being wrong is
    /// deleting somebody's only installed copy.</para>
    /// </summary>
    internal static void ReclaimSwapAside(InstallSite site, string updatesRoot)
    {
        if (site.Shape != InstallShape.MacOsBundle) return;
        if (!Directory.Exists(site.Root)) return;

        string previous = Path.Combine(updatesRoot, "previous");

        foreach (string aside in AtomicFile.SwapAsidesOf(site.Root))
        {
            try
            {
                if (!Directory.Exists(previous))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(previous)!);
                    Directory.Move(aside, previous);
                }
                else
                {
                    Directory.Delete(aside, recursive: true);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A directory something else is holding is not worth failing a launch over; the next
                // one tries again, which is what makes this self-limiting rather than fatal.
            }
        }
    }

    /// <summary>
    /// Settles a macOS exchange that a kill interrupted. Returns the outcome when this launch has
    /// nothing further to do, or null to let the ordinary path proceed.
    ///
    /// <para><b>The question is only ever "did the exchange happen", and the running version answers
    /// it.</b> On macOS the bundle IS the launch path, so this process's own version is the version
    /// sitting at the install path. If it matches the staged one, the exchange completed and this
    /// process is already the new build; if it does not, nothing moved. No file needs to be probed
    /// and — importantly — no sentinel may be written INTO the bundle to find out, because adding a
    /// file to a signed <c>.app</c> breaks its seal and Gatekeeper then refuses to launch it.</para>
    ///
    /// <para>The two spellings of one version are compared as VERSIONS, not as text:
    /// <c>AppVersion.Display</c> normalises a <c>1.0</c> tag to <c>1.0.0</c> while
    /// <c>staged_version</c> is the tag the release carried — the same trap
    /// <see cref="SwapResult.PreviousDirectoryName"/> documents for directory names.</para>
    ///
    /// <para><b>Nothing moved is the recoverable case and it is left recoverable.</b> The marker is
    /// cleared and the ordinary path runs again, so a genuine transient failure — a permissions
    /// error, a locked file — still gets its retry. That is much the likelier way to reach this
    /// method than a kill in the microseconds between two renames, and it would have been a poor
    /// trade to fix the rare case by breaking the common one.</para>
    /// </summary>
    private static SwapResult? ResolveInterruptedSwap(
        InstallSite site, UpdateState state, string updatesRoot,
        string runningVersion, Action<Action<UpdateState>> persist)
    {
        string? staged = state.SwapInProgress;
        if (staged is null) return null;

        if (site.Shape != InstallShape.MacOsBundle || !IsTheSameVersion(runningVersion, state.StagedVersion))
        {
            persist(s => s.SwapInProgress = null);
            return null;
        }

        // It happened. The only step the kill can still have taken away is retaining the displaced
        // bundle as the rollback — which ReclaimSwapAside may already have done on the fallback path,
        // hence the check rather than an unconditional move.
        string previous = Path.Combine(updatesRoot, "previous");
        try
        {
            if (!Directory.Exists(previous)
                && Directory.Exists(staged)
                && UpdatePaths.IsUnder(staged, Path.Combine(updatesRoot, "staged")))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(previous)!);
                Directory.Move(staged, previous);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // No rollback for this generation, which is a smaller harm than the downgrade this whole
            // method exists to prevent. The new version is installed and running either way.
        }

        return new SwapResult(SwapOutcome.SwapAlreadyApplied, null, false, staged);
    }

    /// <summary>
    /// Whether two version strings name the same version, by value and not by spelling. False for
    /// anything unparseable, which keeps the failure direction right: an unrecognised version means
    /// "assume the exchange did not happen", and that path is the recoverable one.
    /// </summary>
    private static bool IsTheSameVersion(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        if (string.Equals(a, b, StringComparison.Ordinal)) return true;

        return SemanticVersion.TryParse(a, out SemanticVersion? va)
            && SemanticVersion.TryParse(b, out SemanticVersion? vb)
            && va!.Equals(vb);
    }

    // ── rollback ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Puts the previous version back after <see cref="MaxFailedStartups"/> failed startups.
    ///
    /// <para><b>The macOS half of this used to do nothing at all</b> and still return
    /// <see cref="SwapOutcome.RolledBack"/>, which made the application tell the user its previous
    /// version had been restored when it had not (found in review, 2026-08-25). The bundle is at
    /// <c>updates/previous/&lt;app&gt;.app</c> and has to be exchanged back the same way it was
    /// exchanged out.</para>
    /// </summary>
    private static SwapResult Revert(InstallSite site, UpdateState state, string updatesRoot)
    {
        try
        {
            if (site.Shape == InstallShape.VersionedPointer)
            {
                string? previousDir = state.PreviousPath;
                if (!UpdateInstallSite.IsSafeVersionDirectoryName(previousDir))
                    return new SwapResult(SwapOutcome.Failed, null, false, "the previous version is gone");

                if (previousDir is null || !Directory.Exists(Path.Combine(site.Root, previousDir)))
                    return new SwapResult(SwapOutcome.Failed, null, false, "the previous version is gone");

                WriteCurrent(site.Root, previousDir);

                // Nothing is re-executed here: the stub reads `current` at the START of a launch, so
                // this process is still the failing version and simply finishes its own session. The
                // next launch is the restored one, and the notice is persisted so it survives a
                // crash in between — which is the likely way this session ends.
                return new SwapResult(SwapOutcome.RolledBack, null, true, state.PendingVersion ?? previousDir);
            }

            string previousBundle = Path.Combine(updatesRoot, "previous");
            if (!Directory.Exists(previousBundle))
                return new SwapResult(SwapOutcome.Failed, null, false, "the previous bundle is gone");

            // Exchange the retained bundle back into the launch path, then hand the caller the
            // executable to exec — this process is the failing version and must not carry on as it.
            AtomicFile.SwapDirectories(site.Root, previousBundle, out bool atomic);

            // After the exchange `previous/` holds the bundle that would not start. It is not
            // insurance any more — the thing it was insuring against has happened — so it goes now
            // rather than waiting for a reclaim step that this path never reaches.
            try { Directory.Delete(previousBundle, true); } catch { /* the reclaim takes it */ }

            string exe = Path.Combine(site.Root, "Contents", "MacOS", UpdateApp.Name);
            return new SwapResult(SwapOutcome.RolledBack, File.Exists(exe) ? exe : null, atomic,
                                  state.PendingVersion ?? "the previous version");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new SwapResult(SwapOutcome.Failed, null, false, e.Message);
        }
    }

    /// <summary>
    /// Replaces this process with <paramref name="executable"/>. Only reached on macOS, immediately
    /// after a bundle swap and before Avalonia has been initialised, so nothing is open to lose.
    /// </summary>
    public static bool ExecReplace(string executable, IReadOnlyList<string> args)
        => NativeExec.Exec(executable, args);
}

/// <summary>The <c>execv</c> primitive. A byte-mover, not a policy.</summary>
internal static class NativeExec
{
#pragma warning disable SYSLIB1054
    [DllImport("libc", EntryPoint = "execv", SetLastError = true)]
    private static extern int Execv([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr argv);
#pragma warning restore SYSLIB1054

    internal static bool Exec(string path, IReadOnlyList<string> args)
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux()) return false;

        // argv[0] is the program itself, and the array must be NULL-terminated.
        var ptrs = new IntPtr[args.Count + 2];
        try
        {
            ptrs[0] = Marshal.StringToHGlobalAnsi(path);
            for (int i = 0; i < args.Count; i++) ptrs[i + 1] = Marshal.StringToHGlobalAnsi(args[i]);
            ptrs[^1] = IntPtr.Zero;

            IntPtr argv = Marshal.AllocHGlobal(IntPtr.Size * ptrs.Length);
            Marshal.Copy(ptrs, 0, argv, ptrs.Length);

            // execv only RETURNS on failure; on success this process has already become the new one.
            Execv(path, argv);
            return false;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
        finally
        {
            foreach (IntPtr p in ptrs) if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
        }
    }
}

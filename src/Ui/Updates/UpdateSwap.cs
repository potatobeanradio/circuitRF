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
    public static SwapResult ApplyAtLaunch(
        InstallSite site, UpdateState state, string updatesRoot, string? runningDirectoryName = null)
    {
        string running = runningDirectoryName ?? UpdateReclaimer.RunningDirectoryName();

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
            InstallShape.MacOsBundle      => SwapBundle(site, state, updatesRoot),
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

    private static SwapResult SwapBundle(InstallSite site, UpdateState state, string updatesRoot)
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

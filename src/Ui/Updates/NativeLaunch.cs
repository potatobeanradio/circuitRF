using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace CircuitRF.Ui.Updates;

/// <summary>
/// Starting a process, and ending this one, using nothing but <c>libc</c> and the runtime's own core
/// library — the two things a session that has just exchanged its own application bundle can still do.
///
/// <para><b>Why this exists (owner report, 2026-09-11, beta.17 to beta.18).</b> The update installed,
/// the user pressed Relaunch, and macOS reported that circuitRF had quit unexpectedly. The session
/// that applied the exchange died of <c>SIGABRT</c> 115 ms into its launch, having never started the
/// new version — the crash report shows an unhandled managed exception dispatched from
/// <c>PreStubWorker</c>, which is the runtime preparing a method for the first time.</para>
///
/// <para><b>The cause is the shape of the application, not the updater's logic.</b> circuitRF ships as
/// a .NET SINGLE-FILE bundle: every managed assembly lives inside the executable, and the runtime
/// opens that executable BY PATH, lazily, the first time each assembly is needed. The exchange
/// replaces <c>/Applications/circuitRF.app</c> while this process is running, so from that instant the
/// path the runtime re-opens is a DIFFERENT file with a different internal layout. Every assembly the
/// process has not already loaded then fails to load:</para>
///
/// <code>
/// System.IO.FileNotFoundException: Could not load file or assembly
///   'System.Diagnostics.Process, Version=10.0.0.0, …'. The system cannot find the file specified.
/// </code>
///
/// <para>Measured directly, on a pair of single-file bundles exchanged with the same
/// <c>renamex_np(RENAME_SWAP)</c> the updater uses: file I/O, reflection and <c>libc</c> P/Invoke all
/// keep working after the exchange — including entry points never called before it, so the
/// marshalling stub the runtime builds on the spot costs nothing. Anything in an assembly that had
/// not yet been loaded does not. <c>Process.Start</c> is in one of those assemblies, which is why
/// <c>open</c> was never spawned and why the unified log showed no trace of it — the same silence the
/// 2026-09-10 investigation spent a day failing to explain.</para>
///
/// <para><b>The rule that follows, and it is the whole point of this file: after the exchange, this
/// process may run only code whose assembly is already loaded.</b> The hand-over is the one thing it
/// still has to do, so the hand-over may not depend on an assembly load — hence <c>posix_spawn</c>
/// rather than <c>Process.Start</c>, and <c>_exit</c> rather than <c>Environment.Exit</c>, whose
/// <c>ProcessExit</c> handlers are arbitrary managed code from anywhere in the application.</para>
///
/// <para><b>It is deliberately the ONLY spawn on this path, used before the exchange as well as
/// after.</b> A primitive that is only exercised in the one situation that is hard to reach is a
/// primitive that rots; this one is what the Messages panel's Relaunch button uses too, so it runs
/// on every update anyone performs.</para>
/// </summary>
internal static class NativeLaunch
{
#pragma warning disable SYSLIB1054   // DllImport, not LibraryImport: see NativeFileOps for why.
    [DllImport("libc", EntryPoint = "posix_spawn", SetLastError = false)]
    private static extern int PosixSpawn(out int pid,
                                         [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
                                         IntPtr fileActions, IntPtr attributes,
                                         IntPtr argv, IntPtr envp);

    [DllImport("libc", EntryPoint = "waitpid", SetLastError = true)]
    private static extern int WaitPid(int pid, out int status, int options);

    [DllImport("libc", EntryPoint = "usleep")]
    private static extern int USleep(uint microseconds);

    [DllImport("libc", EntryPoint = "_exit")]
    private static extern void UnderscoreExit(int code);

    /// <summary>macOS keeps <c>environ</c> behind an accessor rather than exporting the symbol.</summary>
    [DllImport("libc", EntryPoint = "_NSGetEnviron")]
    private static extern IntPtr NSGetEnviron();
#pragma warning restore SYSLIB1054

    private const int WNOHANG = 1;

    /// <summary>
    /// Starts <paramref name="path"/> with <paramref name="args"/> as an independent child and answers
    /// its pid, or the <c>errno</c> <c>posix_spawn</c> refused with. Zero is the success answer, which
    /// is <c>posix_spawn</c>'s own convention: it RETURNS the error rather than setting <c>errno</c>.
    ///
    /// <para>The child gets this process's environment, because the one caller is <c>/usr/bin/open</c>
    /// and an application's environment is part of what a user expects a relaunch to preserve.</para>
    /// </summary>
    internal static int TrySpawn(string path, IReadOnlyList<string> args, out int pid)
    {
        pid = 0;

        // argv[0] is the program itself and the vector is NULL-terminated — the same shape NativeExec
        // builds for execv, and for the same reason: this is a C array, not a managed one.
        var owned = new IntPtr[args.Count + 2];
        IntPtr argv = IntPtr.Zero;

        try
        {
            owned[0] = Marshal.StringToHGlobalAnsi(path);
            for (int i = 0; i < args.Count; i++) owned[i + 1] = Marshal.StringToHGlobalAnsi(args[i]);
            owned[^1] = IntPtr.Zero;

            argv = Marshal.AllocHGlobal(IntPtr.Size * owned.Length);
            Marshal.Copy(owned, 0, argv, owned.Length);

            return PosixSpawn(out pid, path, IntPtr.Zero, IntPtr.Zero, argv, Environ());
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            // ENOSYS: there is no such call here. Named rather than thrown, because every caller's
            // answer to "the successor could not be started" is the same whatever the reason.
            return 78;
        }
        finally
        {
            foreach (IntPtr p in owned) if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
            if (argv != IntPtr.Zero) Marshal.FreeHGlobal(argv);
        }
    }

    /// <summary>
    /// Waits up to <paramref name="timeoutMs"/> for <paramref name="pid"/> and reports whether it
    /// finished, handing back its exit code.
    ///
    /// <para>Polled rather than blocking, because the timeout is the point: the caller has to be able
    /// to tell a refused launch from an accepted one WITHOUT being able to hang on a child that never
    /// returns. <c>WNOHANG</c> plus a short sleep is the whole mechanism, and a false answer means
    /// "still running", which every caller reads as accepted.</para>
    /// </summary>
    internal static bool TryWaitForExit(int pid, int timeoutMs, out int exitCode)
    {
        exitCode = 0;
        const uint stepUs = 20_000;
        int waited = 0;

        try
        {
            while (true)
            {
                int reaped = WaitPid(pid, out int status, WNOHANG);

                // Reaped by somebody else, or not ours at all: there is nothing left to wait for, and
                // reporting a failure here would turn a successful launch into a refusal.
                if (reaped < 0) return false;

                if (reaped == pid)
                {
                    // WIFEXITED / WEXITSTATUS, spelled out: the low seven bits are the signal that
                    // killed it (zero when it exited of its own accord) and the next eight are the code.
                    if ((status & 0x7f) != 0) { exitCode = 128 + (status & 0x7f); return true; }
                    exitCode = (status >> 8) & 0xff;
                    return true;
                }

                if (waited >= timeoutMs) return false;

                USleep(stepUs);
                waited += (int)(stepUs / 1000);
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// Ends this process now, running no managed shutdown at all.
    ///
    /// <para><b>Not <c>Environment.Exit</c>, on this path.</b> That one raises <c>ProcessExit</c>,
    /// which is a hook anything in the application may have taken — and a session that has exchanged
    /// its own bundle cannot run code from an assembly it has not already loaded. The crash reporter
    /// has been told the session is over before the hand-over, so there is nothing left that a managed
    /// shutdown would do for us. Falls back where there is no <c>libc</c> to call.</para>
    /// </summary>
    internal static void Exit(int code)
    {
        try { UnderscoreExit(code); }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            Environment.Exit(code);
        }
    }

    private static IntPtr Environ()
    {
        try
        {
            IntPtr slot = NSGetEnviron();
            return slot == IntPtr.Zero ? IntPtr.Zero : Marshal.ReadIntPtr(slot);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return IntPtr.Zero;   // an empty environment still reaches Launch Services
        }
    }
}

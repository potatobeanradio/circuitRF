using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace CircuitRF.Ui.Updates;

/// <summary>
/// How an applied update starts the version it has just installed — on macOS, through Launch
/// Services rather than <c>execv</c>.
///
/// <para><b>The bug this exists to remove (owner report, 2026-09-04).</b> After an automatic update
/// on macOS, every workspace under <c>~/Documents</c> refused to open with the protected-folder
/// diagnostic, while a workspace elsewhere opened normally. Quitting circuitRF and launching it again
/// fixed it completely, with nothing else changed — no setting touched, no permission granted. The
/// kernel's own record names the cause exactly:</para>
///
/// <code>
/// System Policy: circuitRF(61022) deny(1) file-read-data …/Documents/&lt;workspace&gt;
/// System Policy: circuitRF(61022) deny(1) file-read-data …/Documents/&lt;workspace&gt;/.cws
/// System Policy: circuitRF(61022) deny(1) file-write-unlink …/Documents/&lt;workspace&gt;/.cws
/// </code>
///
/// <para><b>Why the grant stops applying, and only for that one session.</b> macOS resolves a
/// protected-folder grant against the RESPONSIBLE process — the application identity the system
/// established when the process was launched. An <c>execv</c> keeps the process id and the process
/// clock (which is what <c>CrashReporter.IsOwnExecPredecessor</c> relies on), and it therefore keeps
/// that launch-time attribution too. The update has meanwhile exchanged the bundle underneath it, so
/// the identity the attribution points at is no longer the application on disk, and the System Policy
/// check has nothing that satisfies the stored grant. It does not prompt, because from TCC's point of
/// view there is no unanswered question — it simply denies. The next ordinary launch is spawned by
/// launchd with a fresh attribution and everything works, which is exactly the "quit and relaunch and
/// it is fine" the report describes.</para>
///
/// <para><b>The fix is to hand over the way the user would.</b> <c>open -n -a &lt;bundle&gt;</c> asks
/// Launch Services to start the application, so launchd spawns it and the new process is attributed
/// to the bundle that is actually installed. It costs one short-lived child process on the one launch
/// per update that applies a swap, and it is the only mechanism that produces the same process a
/// double-click would.</para>
///
/// <para><b>It is macOS-only and it is a preference, never a requirement.</b> Linux has no TCC and
/// keeps <c>execv</c>; Windows has no <c>execv</c> and already starts a successor. And when Launch
/// Services cannot be reached for any reason the caller falls straight back to <c>execv</c>: an
/// update that leaves the user with a working application and a stale privacy attribution is bad, and
/// an update that leaves them with no application at all is very much worse.</para>
/// </summary>
public static class AppRelaunch
{
    /// <summary>
    /// How long to wait for <c>open</c> to hand the request to Launch Services. It normally returns in
    /// well under a second; the wait exists to read its exit code, which is the only way to tell a
    /// refused launch from an accepted one. A timeout is treated as ACCEPTED — <c>open</c> is a child
    /// of this process, not its parent, so exiting does not cancel it, and the failure direction that
    /// matters is never starting the successor at all.
    /// </summary>
    private const int LaunchServicesTimeoutMs = 10_000;

    /// <summary>
    /// The seam a test drives, since a test host must not actually launch the application. Receives
    /// the resolved <c>.app</c> bundle and the arguments, and returns whether the launch was accepted.
    /// Null (the default) runs the real <c>open</c>.
    /// </summary>
    internal static Func<string, IReadOnlyList<string>, bool>? Launcher { get; set; }

    /// <summary>
    /// The <c>.app</c> bundle <paramref name="executable"/> is the main executable of, or null when it
    /// is not one — a versioned-pointer install, a <c>dotnet build</c> host, anything else.
    ///
    /// <para>Matched STRUCTURALLY, on the <c>&lt;name&gt;.app/Contents/MacOS/&lt;exe&gt;</c> shape that
    /// makes a bundle a bundle, rather than by looking for <c>.app</c> anywhere in the string. A
    /// directory called <c>foo.app</c> somewhere up a build tree is not a bundle root, and handing one
    /// to <c>open</c> produces a refusal the caller would then have to interpret.</para>
    /// </summary>
    public static string? BundleRootOf(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable)) return null;

        try
        {
            string? macOsDir  = Path.GetDirectoryName(Path.GetFullPath(executable));
            string? contents  = Path.GetDirectoryName(macOsDir);
            string? bundle    = Path.GetDirectoryName(contents);

            if (macOsDir is null || contents is null || bundle is null) return null;

            return string.Equals(Path.GetFileName(macOsDir), "MacOS", StringComparison.Ordinal)
                && string.Equals(Path.GetFileName(contents), "Contents", StringComparison.Ordinal)
                && Path.GetFileName(bundle).EndsWith(".app", StringComparison.OrdinalIgnoreCase)
                    ? bundle
                    : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Asks Launch Services to start the bundle <paramref name="executable"/> belongs to, and reports
    /// whether the request was accepted. False means the caller should fall back to <c>execv</c>.
    ///
    /// <para><c>-n</c> is not optional. This process is still alive when the request is made — it exits
    /// a moment later — and without <c>-n</c> Launch Services would see the application already running
    /// and simply activate this instance, which is then the one that exits. The result would be an
    /// update that quietly closes the application instead of restarting it.</para>
    /// </summary>
    public static bool TryRelaunchBundle(string executable, IReadOnlyList<string> args)
    {
        if (!OperatingSystem.IsMacOS()) return false;
        if (BundleRootOf(executable) is not { } bundle) return false;

        if (Launcher is { } seam)
        {
            try   { return seam(bundle, args); }
            catch { return false; }
        }

        return OpenNewInstance(bundle, args);
    }

    private static bool OpenNewInstance(string bundle, IReadOnlyList<string> args)
    {
        try
        {
            var psi = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
            psi.ArgumentList.Add("-n");
            psi.ArgumentList.Add("-a");
            psi.ArgumentList.Add(bundle);

            // `--args` with nothing after it is accepted but pointless, and an empty argument list is
            // the overwhelmingly common case — a launch from the Dock or from Finder.
            if (args.Count > 0)
            {
                psi.ArgumentList.Add("--args");
                foreach (string a in args) psi.ArgumentList.Add(a);
            }

            using Process? p = Process.Start(psi);
            if (p is null) return false;

            return !p.WaitForExit(LaunchServicesTimeoutMs) || p.ExitCode == 0;
        }
        catch (Exception)
        {
            // No /usr/bin/open, no permission to spawn, a full process table — every one of them means
            // the same thing here: this route is unavailable, use the one that does not need it.
            return false;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace CircuitRF.Ui.Updates;

/// <summary>What a helper program did.</summary>
/// <param name="ExitCode">-1 when it could not be started at all.</param>
public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;
}

/// <summary>
/// Runs a platform tool and collects its output. Used ONLY for the primitives that move bytes or
/// read a signature — <c>hdiutil</c>, <c>ditto</c>, <c>tar</c>, <c>codesign</c>, <c>xcrun</c>.
///
/// <para><b>Never for downloading.</b> R-AU-28: no shell-out to <c>curl</c>, <c>open</c>,
/// <c>Invoke-WebRequest</c> or a browser anywhere in the download path. The absence of a
/// <c>com.apple.quarantine</c> attribute on macOS and of the Mark of the Web on Windows is what
/// suppresses the Gatekeeper and SmartScreen prompts, and it holds precisely because
/// <c>HttpClient</c> writes the file itself. A helpful-looking refactor to a shell downloader would
/// reintroduce both prompts, silently, and only on a real user's machine.</para>
/// </summary>
public static class ProcessRunner
{
    /// <summary>Every program this class is permitted to start. A short, closed list, deliberately.</summary>
    public static readonly string[] Allowed = ["hdiutil", "ditto", "tar", "codesign", "xcrun", "chmod"];

    /// <summary>
    /// Where each permitted tool actually lives, in preference order. <b>A bare name is resolved
    /// through <c>PATH</c>, and <c>PATH</c> is attacker-influenced on exactly the platform this
    /// channel targets</b> (found in the security review, 2026-08-25): the Linux user-local install
    /// puts its own launcher in <c>~/.local/bin</c>, which most distributions place AHEAD of
    /// <c>/usr/bin</c>, so a file called <c>tar</c> dropped there by anything the user has ever run
    /// would be started by the updater — with an archive of our choosing and a destination inside
    /// the install tree. The same reasoning covers <c>codesign</c> on macOS, where substituting it
    /// substitutes the entire verification step.
    ///
    /// <para>So every tool is addressed by absolute path, and the bare name is used only when no
    /// candidate exists (a distribution that puts <c>tar</c> somewhere unusual), which is a
    /// degradation rather than a hole: the alternative there is no update at all.</para>
    /// </summary>
    private static readonly Dictionary<string, string[]> ToolPaths = new(StringComparer.Ordinal)
    {
        ["hdiutil"]  = ["/usr/bin/hdiutil"],
        ["ditto"]    = ["/usr/bin/ditto"],
        ["codesign"] = ["/usr/bin/codesign"],
        ["xcrun"]    = ["/usr/bin/xcrun"],
        ["tar"]      = ["/usr/bin/tar", "/bin/tar"],
        ["chmod"]    = ["/bin/chmod", "/usr/bin/chmod"],
    };

    /// <summary>
    /// The absolute path a permitted tool is started from, or the bare name when none of the known
    /// locations holds it. Exposed so a test can assert the resolution rather than infer it.
    /// </summary>
    public static string Resolve(string fileName)
    {
        if (!ToolPaths.TryGetValue(fileName, out string[]? candidates)) return fileName;

        foreach (string c in candidates)
        {
            try { if (File.Exists(c)) return c; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return fileName;
    }

    public static async Task<ProcessResult> RunAsync(
        string fileName, string[] arguments, CancellationToken ct, TimeSpan? timeout = null)
    {
        if (Array.IndexOf(Allowed, fileName) < 0)
            throw new ArgumentException($"'{fileName}' is not one of the update subsystem's permitted tools.",
                                        nameof(fileName));

        var psi = new ProcessStartInfo(Resolve(fileName))
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        foreach (string a in arguments) psi.ArgumentList.Add(a);

        using var p = new Process { StartInfo = psi };

        try { if (!p.Start()) return new ProcessResult(-1, "", "could not start"); }
        catch (Exception e) { return new ProcessResult(-1, "", e.Message); }

        Task<string> outTask = p.StandardOutput.ReadToEndAsync(ct);
        Task<string> errTask = p.StandardError.ReadToEndAsync(ct);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is not null) cts.CancelAfter(timeout.Value);

        try
        {
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return new ProcessResult(-1, "", "timed out");
        }

        return new ProcessResult(p.ExitCode,
                                 await outTask.ConfigureAwait(false),
                                 await errTask.ConfigureAwait(false));
    }
}

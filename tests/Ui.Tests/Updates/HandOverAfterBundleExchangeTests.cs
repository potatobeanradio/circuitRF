using System.Text.RegularExpressions;
using CircuitRF.Ui.Updates;

namespace CircuitRF.Ui.Tests.Updates;

/// <summary>
/// What the session that applied a macOS update may still do after it has exchanged its own bundle
/// (owner report, 2026-09-11, beta.17 to beta.18: the Relaunch button closed the application and
/// macOS reported that it had quit unexpectedly).
///
/// <para><b>The finding these hold.</b> circuitRF ships as a .NET single-file bundle, so the runtime
/// re-opens the application executable BY PATH every time it loads an assembly for the first time.
/// The exchange replaces that path's file, so from that moment every not-yet-loaded assembly fails
/// with <c>FileNotFoundException</c> — including <c>System.Diagnostics.Process</c>, which is what the
/// hand-over used to spawn <c>/usr/bin/open</c> with. The exception escaped into
/// <c>RunBeforeUi</c>'s catch-all, the launch carried on into <c>Main</c>, and the next call it made
/// needed another assembly and aborted the process. No successor was ever started and nothing was
/// written down, which is also why the 2026-09-10 investigation could find no <c>open</c> process in
/// the log and no reason for its absence.</para>
///
/// <para><b>So the rule is: after the exchange this process runs only already-loaded code</b> — the
/// runtime's own core library, and <c>libc</c>. Measured, not assumed: on a pair of single-file
/// bundles exchanged with the updater's own <c>renamex_np(RENAME_SWAP)</c>, file I/O and <c>libc</c>
/// P/Invoke keep working afterwards (entry points never called before the exchange included), while
/// <c>Process.Start</c> and <c>System.Text.Json</c> both throw.</para>
/// </summary>
public sealed class HandOverAfterBundleExchangeTests
{
    // ── the primitive itself ────────────────────────────────────────────────────────────────────

    /// <summary>The success answer, and the exit code, both come back — this is what tells a refused
    /// Launch Services request from an accepted one.</summary>
    [Fact]
    public void ASpawnedChildsExitCodeComesBack()
    {
        if (Bin("true") is not { } yes || Bin("false") is not { } no) return;

        Assert.Equal(0, NativeLaunch.TrySpawn(yes, [], out int okPid));
        Assert.True(NativeLaunch.TryWaitForExit(okPid, 10_000, out int okCode));
        Assert.Equal(0, okCode);

        Assert.Equal(0, NativeLaunch.TrySpawn(no, [], out int badPid));
        Assert.True(NativeLaunch.TryWaitForExit(badPid, 10_000, out int badCode));
        Assert.Equal(1, badCode);
    }

    /// <summary>
    /// The arguments actually reach the child. <c>open -n -a &lt;bundle&gt; --args …</c> is the whole
    /// hand-over, so an argument vector that is built but not delivered would relaunch the
    /// application without the one flag that keeps a successor from racing its predecessor.
    /// </summary>
    [Fact]
    public void TheArgumentVectorReachesTheChild()
    {
        if (Bin("touch") is not { } touch) return;

        string marker = Path.Combine(Path.GetTempPath(), $"crf-spawn-{Guid.NewGuid():N}");

        Assert.Equal(0, NativeLaunch.TrySpawn(touch, [marker], out int pid));
        Assert.True(NativeLaunch.TryWaitForExit(pid, 10_000, out int code));
        Assert.Equal(0, code);
        Assert.True(File.Exists(marker), $"the child did not receive '{marker}' as an argument");

        File.Delete(marker);
    }

    /// <summary>
    /// A program that is not there is a REFUSAL with a reason, not an exception and not a silent
    /// success. <c>posix_spawn</c> returns the error rather than setting <c>errno</c>, which is the
    /// one convention a caller has to get right.
    /// </summary>
    [Fact]
    public void AProgramThatIsNotThereIsRefusedWithAnErrno()
    {
        if (Bin("true") is null) return;   // no POSIX spawn to test at all

        int errno = NativeLaunch.TrySpawn("/nonexistent/not-a-program", [], out _);

        Assert.NotEqual(0, errno);
        Assert.Equal(2, errno);   // ENOENT
    }

    // ── the rule, held in the source ────────────────────────────────────────────────────────────

    /// <summary>
    /// The hand-over may not go through <c>System.Diagnostics.Process</c>: its assembly is exactly
    /// the one that cannot be loaded any more by the time this code runs. A comment saying so does
    /// not satisfy the rule, so the source is read with its comments stripped.
    /// </summary>
    [Fact]
    public void TheLaunchServicesRequestDoesNotGoThroughSystemDiagnosticsProcess()
    {
        string body = Body(Source("AppRelaunch.cs"), "private static bool OpenNewInstance(");

        Assert.DoesNotContain("Process", body, StringComparison.Ordinal);
        Assert.Contains("NativeLaunch.TrySpawn", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// And it may not leave through <c>Environment.Exit</c>, which raises <c>ProcessExit</c> — a hook
    /// any part of the application may have taken, in an assembly this session may no longer be able
    /// to load.
    /// </summary>
    [Theory]
    [InlineData("private static void HandOverTo(")]
    [InlineData("private static void LeaveTheUpdateForTheNextLaunch(")]
    [InlineData("private static void LeaveRatherThanOutliveTheExchange(")]
    public void NothingOnThePathOutOfAnExchangedBundleCallsEnvironmentExit(string signature)
    {
        string body = Body(Source("UpdateStartup.cs"), signature);

        Assert.DoesNotContain("Environment.Exit", body, StringComparison.Ordinal);
        Assert.Contains("NativeLaunch.Exit", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The one flag the refusal diagnostics read lives in ANOTHER assembly, so it is written before
    /// the exchange rather than after it. Ordering is the whole content of this test: the same two
    /// statements in the other order are the crash.
    /// </summary>
    [Fact]
    public void TheReplacedBundleFlagIsSetBeforeTheExchangeNotOnlyAfterIt()
    {
        string run = Body(Source("UpdateStartup.cs"), "public static void RunBeforeUi(");

        int first = run.IndexOf("AppBundleReplacedThisSession", StringComparison.Ordinal);
        int swap  = run.IndexOf("UpdateSwap.ApplyAtLaunch", StringComparison.Ordinal);

        Assert.True(first >= 0 && swap >= 0, "RunBeforeUi no longer has the statements this pins.");
        Assert.True(first < swap,
                    "CircuitRF.Diagnostics is loaded for the first time by this write. After the "
                    + "exchange it cannot be loaded at all, so the write has to happen before the "
                    + "swap — see NativeLaunch for the measurement.");
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Where a standard utility actually is, or null on a platform that has no such thing —
    /// these tests exercise a real spawn, so they need a real program to spawn.</summary>
    private static string? Bin(string name)
        => new[] { $"/usr/bin/{name}", $"/bin/{name}" }.FirstOrDefault(File.Exists);

    private static string Source(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;

        Assert.True(dir is not null, "Could not locate the repository root from the test output directory.");

        return WithoutComments(
            File.ReadAllText(Path.Combine(dir!.FullName, "src", "Ui", "Updates", file)));
    }

    /// <summary>A file with its comments removed — a note ABOUT a rule must not satisfy the rule.</summary>
    private static string WithoutComments(string text)
    {
        text = Regex.Replace(text, @"/\*.*?\*/", "", RegexOptions.Singleline);
        return string.Join("\n", text.Split('\n').Select(line =>
        {
            int slash = line.IndexOf("//", StringComparison.Ordinal);
            return slash < 0 ? line : line[..slash];
        }));
    }

    /// <summary>One method's text, from its signature to the closing brace at method indentation.</summary>
    private static string Body(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{signature}' is not in the source any more — repoint this test.");

        int end = source.IndexOf("\n    }", start, StringComparison.Ordinal);
        Assert.True(end > start, $"could not find the end of '{signature}'.");

        return source[start..end];
    }
}

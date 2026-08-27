using System;
using System.IO;
using System.Linq;
using CircuitRF.Ui;
using CircuitRF.Ui.Diagnostics;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// The crash-report mechanism: what survives a crash, what a clean exit removes, and the one thing
/// that must never happen — a report invented for a session that is still running.
///
/// <para>Serialized as a collection because <see cref="AppDataRoot"/> and
/// <see cref="CrashReporter"/> are both process-global: a parallel test redirecting the state
/// directory underneath another one would make every assertion here nondeterministic.</para>
/// </summary>
[Collection(AppDataRootCollection.Name)]
public sealed class CrashReporterTests : IDisposable
{
    private readonly string _root;

    public CrashReporterTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf-crash-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        CrashReporter.ResetForTests();
        AppDataRoot.RedirectTo(_root);
    }

    public void Dispose()
    {
        CrashReporter.ResetForTests();
        AppDataRoot.RedirectTo(null);
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string ReportDir => Path.Combine(_root, CrashReporter.DirName);

    private string[] Reports()
        => Directory.Exists(ReportDir) ? Directory.GetFiles(ReportDir, "crash-*.log") : Array.Empty<string>();

    private string[] Sessions()
        => Directory.Exists(ReportDir) ? Directory.GetFiles(ReportDir, "session-*.running") : Array.Empty<string>();

    /// <summary>Writes a session file exactly as an earlier, now-dead process would have left it.</summary>
    private string WriteAbandonedSession(string name, string body)
    {
        Directory.CreateDirectory(ReportDir);
        string path = Path.Combine(ReportDir, name);
        File.WriteAllText(path, body);
        return path;
    }

    // ── The clean case ───────────────────────────────────────────────────────

    [Fact]
    public void CleanExit_LeavesNothingBehind_SoTheNextLaunchIsQuiet()
    {
        CrashReporter.Install("circuitRF");
        CrashReporter.Note("run: begin 'SP1'");
        Assert.Single(Sessions());          // the session file exists WHILE the process runs

        CrashReporter.MarkCleanExit();

        Assert.Empty(Sessions());
        Assert.Empty(Reports());
    }

    // ── The case the whole design exists for ─────────────────────────────────

    [Fact]
    public void AbandonedSession_BecomesAReport_CarryingTheBreadcrumbsAndSayingWhyThereIsNoStack()
    {
        // A session that died with no managed exception: exactly what a stack overflow, the OOM
        // killer or a native fault leaves — the only record is the trail.
        WriteAbandonedSession("session-20260824-101500-4242.running",
            "circuitRF crash report\nversion     : 9.9.9-test\n--- trail ---\n" +
            "[10:15:03] run: begin 'SP1' (2001 work unit(s))\n" +
            "[10:15:13] run: 'TB1' at 1840 / 2001\n");

        CrashReporter.Install("circuitRF");

        string report = Assert.Single(Reports());
        Assert.Equal("crash-20260824-101500-4242.log", Path.GetFileName(report));

        string text = File.ReadAllText(report);
        Assert.Contains("WITHOUT a clean exit", text);          // says what happened
        Assert.Contains("no stack trace exists", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("run: 'TB1' at 1840 / 2001", text);     // and how far the run had got
        Assert.Contains("9.9.9-test", text);                    // the original header is preserved

        // The pending list is what a UI surface announces, and reading it clears it so exactly one
        // surface speaks however many windows open afterwards.
        Assert.Equal(new[] { report }, CrashReporter.TakePendingReports());
        Assert.Empty(CrashReporter.TakePendingReports());
    }

    [Fact]
    public void ASessionSomebodyStillOwns_IsNotPromoted()
    {
        // A second copy of harmonicaRF or wBond is a legitimate thing to be running, so an existing
        // session file cannot by itself mean a crash. The owner holds it open; that is the test.
        Directory.CreateDirectory(ReportDir);
        string live = Path.Combine(ReportDir, "session-20260824-110000-777.running");

        using (var held = new FileStream(live, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            held.Write("--- trail ---\n"u8);
            held.Flush();

            CrashReporter.Install("circuitRF");

            Assert.Empty(Reports());
            Assert.Empty(CrashReporter.TakePendingReports());
            Assert.Contains(Sessions(), p => Path.GetFileName(p) == Path.GetFileName(live));
        }
    }

    [Fact]
    public void OnceTheOwnerIsGone_TheSameFileIsPromoted()
    {
        // The other half of the pair above: same file, same launch logic, only the holder differs.
        // Without this, "not promoted" could just as well mean "promotion never works".
        Directory.CreateDirectory(ReportDir);
        string dead = Path.Combine(ReportDir, "session-20260824-110000-777.running");
        File.WriteAllText(dead, "--- trail ---\n[11:00:01] run: begin 'SP1'\n");

        CrashReporter.Install("circuitRF");

        string report = Assert.Single(Reports());
        Assert.Contains("run: begin 'SP1'", File.ReadAllText(report));
    }

    // ── The managed half ─────────────────────────────────────────────────────

    // ── The exec hand-off (an applied auto-update) ───────────────────────────

    /// <summary>
    /// The bug this pins, seen in the wild on macOS: an applied update <c>execv</c>s the new
    /// executable, which keeps the pid and discards the runtime, so <c>ProcessExit</c> never fires.
    /// The session file was therefore left behind, the replacement image swept the directory two
    /// seconds later, found it owned by nobody and promoted it — and the user was told circuitRF
    /// "did not shut down cleanly last time" about an update they had just chosen. The empty trail
    /// was the tell.
    /// </summary>
    [Fact]
    public void AnExecHandoff_LeavesNothingForTheReplacementImageToPromote()
    {
        CrashReporter.Install("circuitRF");
        Assert.Single(Sessions());

        CrashReporter.HandOffToExec();
        Assert.Empty(Sessions());

        // The replacement image: same pid, seconds later, sweeping the same directory.
        CrashReporter.ResetForTests();
        CrashReporter.Install("circuitRF");

        Assert.Empty(CrashReporter.TakePendingReports());
        Assert.Empty(Reports());
    }

    /// <summary>
    /// <c>execv</c> returns only when it FAILS, and the update path then carries on running this
    /// image. A session that is still alive must still be reportable, so the hand-off is re-armed
    /// rather than leaving the rest of the run blind.
    /// </summary>
    [Fact]
    public void WhenTheExecFails_TheSessionIsReArmed_SoTheRestOfTheRunIsStillReported()
    {
        CrashReporter.Install("circuitRF");
        CrashReporter.HandOffToExec();
        CrashReporter.ResumeAfterExec();

        Assert.Single(Sessions());

        CrashReporter.Note("opened a workspace after the exec failed");
        string session = Sessions().Single();
        Assert.Contains("opened a workspace after the exec failed", File.ReadAllText(session));
    }

    /// <summary>
    /// A stray <see cref="CrashReporter.ResumeAfterExec"/> must not give one process two session
    /// files — the second would outlive the clean exit that only removes the first, which is the very
    /// false report this whole mechanism exists to avoid.
    /// </summary>
    [Fact]
    public void ResumeWithoutAHandoff_DoesNotOpenASecondSessionFile()
    {
        CrashReporter.Install("circuitRF");
        CrashReporter.ResumeAfterExec();
        Assert.Single(Sessions());

        CrashReporter.MarkCleanExit();
        Assert.Empty(Sessions());
    }

    /// <summary>
    /// The update path must actually USE the hand-off. Both <c>execv</c> sites in
    /// <c>UpdateStartup</c> — an applied update and a rollback — go through the one helper that
    /// closes the session file first; a third site added later without it would silently reintroduce
    /// the false crash report.
    /// </summary>
    [Fact]
    public void TheUpdatePath_ClosesTheSessionBeforeEveryExec()
    {
        string source = File.ReadAllText(Path.Combine(RepoRoot(), "src/Ui/Updates/UpdateStartup.cs"));

        Assert.Contains("CrashReporter.HandOffToExec()", source);
        Assert.Contains("CrashReporter.ResumeAfterExec()", source);

        // Every ExecReplace in the update-startup path is the one inside the helper.
        Assert.Equal(1, CountOutsideComments(source, "UpdateSwap.ExecReplace("));
    }

    private static int CountOutsideComments(string source, string needle)
    {
        int n = 0;
        foreach (string raw in source.Split('\n'))
        {
            string line = raw.TrimStart();
            if (line.StartsWith("//", StringComparison.Ordinal) ||
                line.StartsWith("///", StringComparison.Ordinal) ||
                line.StartsWith("*", StringComparison.Ordinal)) continue;
            int i = 0;
            while ((i = line.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        }
        return n;
    }

    [Fact]
    public void ReportFatal_WritesTheWholeExceptionChain_AndTheReportExistsImmediately()
    {
        CrashReporter.Install("circuitRF");
        CrashReporter.Note("run: begin 'SP1' (2001 work unit(s))");

        Exception thrown;
        try { throw new InvalidOperationException("outer", new DivideByZeroException("inner")); }
        catch (Exception ex) { thrown = ex; }

        string? path = CrashReporter.ReportFatal("test", thrown);

        Assert.NotNull(path);
        Assert.True(File.Exists(path));
        Assert.Empty(Sessions());               // promoted by rename: no leftover to promote twice
        Assert.Single(Reports());

        string text = File.ReadAllText(path!);
        Assert.Contains("System.InvalidOperationException: outer", text);
        Assert.Contains("System.DivideByZeroException: inner", text);
        Assert.Contains("ReportFatal_WritesTheWholeExceptionChain", text);   // a real stack trace
        Assert.Contains("run: begin 'SP1'", text);                           // and the trail before it
    }

    [Fact]
    public void AfterACrash_ACleanExitDoesNotDeleteTheReport()
    {
        CrashReporter.Install("circuitRF");
        CrashReporter.ReportFatal("test", new InvalidOperationException("boom"));

        // A dispatcher exception can be reported and the process still reach ProcessExit. The report
        // is the artifact the user was told about; shutdown must not take it away again.
        CrashReporter.MarkCleanExit();

        Assert.Single(Reports());
    }

    [Fact]
    public void ASecondFatal_AppendsToTheSameReport_RatherThanLosingIt()
    {
        CrashReporter.Install("circuitRF");
        CrashReporter.ReportFatal("first", new InvalidOperationException("one"));
        CrashReporter.ReportFatal("second", new NotSupportedException("two"));

        string report = Assert.Single(Reports());
        string text = File.ReadAllText(report);
        Assert.Contains("one", text);
        Assert.Contains("two", text);
    }

    // ── Housekeeping ─────────────────────────────────────────────────────────

    [Fact]
    public void ReportsArePruned_SoTheStateDirectoryCannotGrowForever()
    {
        Directory.CreateDirectory(ReportDir);
        for (int i = 0; i < 30; i++)
        {
            string p = Path.Combine(ReportDir, $"crash-2026080{i / 10}-{i:000000}-1.log");
            File.WriteAllText(p, "old");
            File.SetLastWriteTimeUtc(p, new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i));
        }

        CrashReporter.Install("circuitRF");

        Assert.Equal(20, Reports().Length);
        // Newest kept, oldest dropped — a report nobody has read yet is the one that matters.
        Assert.Contains(Reports(), p => Path.GetFileName(p).Contains("-000029-"));
        Assert.DoesNotContain(Reports(), p => Path.GetFileName(p).Contains("-000000-"));
    }

    [Fact]
    public void NoteBeforeInstall_IsANoOp_NotAThrow()
    {
        // Diagnostics must never be able to take down what they are diagnosing.
        CrashReporter.Note("before anything is set up");
        Assert.Null(Record.Exception(() => CrashReporter.NoteHandled("nowhere", new Exception("x"))));
        Assert.Empty(Reports());
    }

    [Fact]
    public void AnUnwritableStateDirectory_DoesNotFailStartup()
    {
        // A file where the directory should be: Directory.CreateDirectory throws, and Install must
        // swallow it — no reports is an acceptable outcome, a refusal to launch is not.
        AppDataRoot.RedirectTo(null);
        CrashReporter.ResetForTests();

        string blocked = Path.Combine(_root, "blocked");
        File.WriteAllText(blocked, "not a directory");
        AppDataRoot.RedirectTo(Path.Combine(blocked, "state"));

        Assert.Null(Record.Exception(() => CrashReporter.Install("circuitRF")));
        Assert.Null(Record.Exception(() => CrashReporter.Note("still fine")));
        Assert.Null(Record.Exception(CrashReporter.MarkCleanExit));
    }

    // ── Wiring ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("src/Ui/Program.cs",          "circuitRF")]
    [InlineData("src/Ui/ProgramHarmonica.cs", "harmonicaRF")]
    [InlineData("src/Ui/ProgramWBond.cs",     "wBond")]
    public void EveryEntryPoint_InstallsTheReporter(string relativePath, string appName)
    {
        // All three applications write into the same per-user directory, so all three must install —
        // and each must name itself, or a report cannot say which one produced it.
        string source = File.ReadAllText(Path.Combine(RepoRoot(), relativePath));
        Assert.Contains($"CrashReporter.Install(\"{appName}\")", source);
    }

    [Fact]
    public void TheRunPath_LeavesBreadcrumbs()
    {
        // The user-visible symptom that started this was "it crashed while running the s-parameter
        // simulation". A report with no trail through the run would not answer that.
        string run = File.ReadAllText(Path.Combine(RepoRoot(), "src/Ui/Schematic/SchematicRunService.cs"));
        Assert.Contains("CrashReporter.Note(", run);

        string vm = File.ReadAllText(Path.Combine(RepoRoot(), "src/Ui/ViewModels/WorkspaceViewModel.cs"));
        Assert.Contains("CrashReporter.Note(", vm);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}

/// <summary>
/// Every test that redirects <see cref="AppDataRoot"/> shares THIS collection — not one collection
/// each. <c>DisableParallelization</c> only serializes tests WITHIN a collection, so two collections
/// that both redirect a process-global directory still run concurrently and still clobber each
/// other. That failed once, under full-solution load only (the crash-report tests and the update
/// tests, 2026-08-25), and it is exactly the shape isolated repetition never reproduces.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AppDataRootCollection
{
    public const string Name = "AppDataRoot";
}

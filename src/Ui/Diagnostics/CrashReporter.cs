using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace CircuitRF.Ui.Diagnostics;

/// <summary>
/// Persists what a crash looked like, so a user who hits one has something to send back.
///
/// <para><b>Why a session file and not only exception handlers.</b> A managed handler
/// (<see cref="AppDomain.UnhandledException"/>) sees a managed exception and nothing else — it never
/// runs for the three ways a desktop application most often dies while a long simulation is in
/// flight: a <c>StackOverflowException</c> (the runtime kills the process without unwinding), the
/// OS out-of-memory killer, and a fault inside native code (SkiaSharp, a device worker's model
/// library). Those are precisely the crashes worth reporting and precisely the ones a handler
/// cannot report. So the primary mechanism here is a <b>session file</b>: every run opens
/// <c>session-*.running</c>, writes an environment header and a running breadcrumb trail to it with
/// autoflush, and <b>deletes it on a clean exit</b>. A file that is still there at the next launch
/// is, by construction, a session that did not exit cleanly — and it already holds the trail of what
/// the application was doing when it stopped. The managed handlers are the second mechanism: when
/// one does fire, it appends the full exception chain to that same file and promotes it to a report
/// immediately.</para>
///
/// <para><b>Liveness, not PIDs.</b> Two copies of harmonicaRF or wBond can legitimately run at once,
/// so "an old <c>.running</c> file exists" cannot mean "someone crashed". The owner holds its own
/// session file open with <see cref="FileShare.Read"/> for the life of the process; a probe that can
/// open a session file with <see cref="FileShare.None"/> therefore proves nobody owns it. This is
/// the same share-mode/flock exclusion <c>Program.Main</c> already relies on for the Linux
/// single-instance lock, rather than a second scheme built on process ids — a recycled pid would
/// make a pid check quietly wrong.</para>
///
/// <para><b>Nothing here may throw.</b> A diagnostic that can take down the application it is
/// diagnosing is worse than no diagnostic, so every operation is wrapped and failure is silent: no
/// disk, a read-only state directory or a full volume simply means no report.</para>
/// </summary>
public static class CrashReporter
{
    /// <summary>Sub-directory of the per-user state directory that holds the reports.</summary>
    public const string DirName = "crash-reports";

    /// <summary>How many <c>crash-*.log</c> files are kept; the oldest beyond this are pruned.</summary>
    private const int KeepReports = 20;

    private static readonly Lock _gate = new();

    private static string? _dir;              // resolved once at Install, so a later redirect cannot split the session
    private static string? _path;             // the session file, or the promoted report once one exists
    private static StreamWriter? _writer;
    private static bool _installed;
    private static bool _promoted;            // this session has already produced a crash report
    private static string _appName = "circuitRF";
    private static List<string> _pending = new();

    /// <summary>Where reports live. Stable for the life of the process once <see cref="Install"/> has run.</summary>
    public static string Dir => _dir ?? AppDataRoot.SubDir(DirName);

    /// <summary>
    /// Starts crash reporting for this process. Call it as the FIRST statement of <c>Main</c>, before
    /// any Avalonia setup — a crash while the toolkit is coming up is a crash worth having a report
    /// for too. Idempotent; a second call does nothing.
    /// </summary>
    /// <param name="appName">Which of the three applications this process is — it goes in the report
    /// header, because all three write into the same per-user directory.</param>
    public static void Install(string appName)
    {
        lock (_gate)
        {
            if (_installed) return;
            _installed = true;
            _appName   = appName;

            try
            {
                _dir = AppDataRoot.SubDir(DirName);
                Directory.CreateDirectory(_dir);

                // Order matters: sweep BEFORE opening this session's own file, so this session's file
                // is not a candidate for promotion and no liveness probe has to special-case it.
                _pending = PromoteAbandonedSessions(_dir);
                Prune(_dir);

                OpenSession();
            }
            catch { Close(); }   // no disk, no reports — never a reason to fail startup
        }

        // Clean exit is the ONLY thing that removes the session file. .NET does not raise ProcessExit
        // after an unhandled exception, which is exactly the asymmetry this design wants.
        try
        {
            AppDomain.CurrentDomain.ProcessExit        += (_, _) => MarkCleanExit();
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            TaskScheduler.UnobservedTaskException      += OnUnobservedTaskException;
        }
        catch { /* nothing to do if the runtime refuses the subscription */ }
    }

    /// <summary>
    /// Opens this process's session file and writes the header. Caller holds <see cref="_gate"/> and
    /// has already resolved <see cref="_dir"/>.
    /// </summary>
    private static void OpenSession()
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        _path = Path.Combine(_dir!, $"session-{stamp}-{Environment.ProcessId}.running");

        // FileShare.Read, deliberately: readable by a probe or by the user, but an exclusive
        // (FileShare.None) open fails while this process lives, which is what makes
        // "abandoned" decidable without pid arithmetic.
        var fs = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.Read);
        _writer = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = true };
        _writer.Write(Header());
    }

    // ── Breadcrumbs ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Records one line of "what the application was doing". Cheap (an autoflushed append) and safe
    /// from any thread. Keep the text short and factual — this is what a report is read for when no
    /// stack trace exists, which is the case for every native or out-of-memory death.
    /// </summary>
    public static void Note(string message)
    {
        // The thread is part of "what the application was doing". A burst of identical notes is a
        // different event depending on whether one thread produced it or several did, and a note from
        // OFF the UI thread on a path that is supposed to be UI-only is itself the finding — the Data
        // Display's `DataSet` group maps are ordinary dictionaries, so a torn read there is
        // indistinguishable from an impossible index (src/Ui/DataDisplay/RESOLVED.md, round 5).
        string line = $"[{DateTime.Now:HH:mm:ss.fff} {ThreadTag()}] {message}";
        lock (_gate)
        {
            try { _writer?.WriteLine(line); } catch { /* diagnostics never throw */ }
        }
    }

    /// <summary>
    /// Records the calling thread as the UI thread, so <see cref="Note"/> can mark notes that arrive
    /// from anywhere else. Call it from the framework's own initialization, which by definition runs
    /// there.
    ///
    /// <para><b>An id, deliberately, and not <c>Dispatcher.UIThread.CheckAccess()</c>.</b> Reading
    /// that property is not free of side effects — the first access CREATES the dispatcher, bound to
    /// whichever thread asked — so a diagnostic that consulted it could bind the UI thread to a worker
    /// merely by noting something early. An integer captured once and compared is exact, costs
    /// nothing, and keeps this file free of any Avalonia reference.</para>
    /// </summary>
    public static void MarkUiThread() => _uiThreadId = Environment.CurrentManagedThreadId;

    private static int _uiThreadId;

    /// <summary>
    /// The current thread as one short token: <c>t7</c>, or <c>t7!ui</c> when it is not the UI thread.
    /// The bang is deliberately ugly — it marks the case worth noticing in a wall of trail lines.
    /// Before <see cref="MarkUiThread"/> has run there is no UI thread to be off, so an early note is
    /// unannotated rather than wrongly flagged.
    /// </summary>
    private static string ThreadTag()
    {
        int id = Environment.CurrentManagedThreadId;
        return _uiThreadId == 0 || id == _uiThreadId ? $"t{id}" : $"t{id}!ui";
    }

    /// <summary>
    /// Records a non-fatal exception that was caught and handled. Not a crash — but the exception
    /// that precedes a crash is usually the one that explains it, and by definition it is not in the
    /// fatal report.
    /// </summary>
    public static void NoteHandled(string where, Exception ex)
        => Note($"handled in {where}: {ex.GetType().Name}: {ex.Message}");

    // ── Reports ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reports left by earlier sessions that this launch has not yet shown anyone. Reading it CLEARS
    /// it, so exactly one surface announces a given crash however many windows open afterwards.
    /// </summary>
    public static IReadOnlyList<string> TakePendingReports()
    {
        lock (_gate)
        {
            var taken = _pending;
            _pending = new List<string>();
            return taken;
        }
    }

    /// <summary>Every report on disk, newest first. Empty when the directory does not exist.</summary>
    public static IReadOnlyList<string> AllReports()
    {
        try
        {
            if (!Directory.Exists(Dir)) return Array.Empty<string>();
            return Directory.GetFiles(Dir, "crash-*.log")
                            .OrderByDescending(File.GetLastWriteTimeUtc)
                            .ToList();
        }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>
    /// Writes a report for <paramref name="ex"/> and returns its path (null if nothing could be
    /// written). Public so a surface that catches something it considers unrecoverable can produce
    /// the same artifact the automatic handlers do.
    /// </summary>
    public static string? ReportFatal(string origin, Exception? ex, bool terminating = true)
    {
        lock (_gate)
        {
            try
            {
                if (_writer is null || _path is null) return null;

                _writer.WriteLine();
                _writer.WriteLine("=== FATAL =========================================================");
                _writer.WriteLine($"when       : {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}");
                _writer.WriteLine($"origin     : {origin}");
                _writer.WriteLine($"terminating: {terminating}");
                _writer.WriteLine($"managed mem: {GC.GetTotalMemory(false) / (1024 * 1024)} MB");
                _writer.WriteLine();
                _writer.WriteLine(Describe(ex));
                _writer.WriteLine("=== END FATAL =====================================================");
                _writer.Flush();

                if (_promoted) return _path;   // a second fatal appends to the report the first made

                // Promote by RENAME, not copy: the report is complete the instant this returns, which
                // matters because the process is usually seconds from death. Renaming also removes the
                // .running extension, so the next launch does not re-promote it.
                string target = Path.Combine(Dir, Path.GetFileNameWithoutExtension(_path)
                                                      .Replace("session-", "crash-", StringComparison.Ordinal) + ".log");
                _writer.Dispose();
                _writer = null;
                File.Move(_path, target, overwrite: true);
                _path = target;
                _promoted = true;

                // Reopened in append mode so anything logged during the death rattle still lands.
                var fs = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
                _writer = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = true };
                return _path;
            }
            catch { return null; }
        }
    }

    /// <summary>
    /// Ends the session cleanly: the session file is removed, so the next launch does not read it as
    /// a crash. Wired to <see cref="AppDomain.ProcessExit"/>; also safe to call directly from a
    /// shutdown path.
    /// </summary>
    public static void MarkCleanExit()
    {
        lock (_gate)
        {
            if (_path is null) { Close(); return; }
            string path = _path;
            bool promoted = _promoted;
            Close();
            if (promoted) return;                       // this session DID crash earlier; keep its report
            try { File.Delete(path); } catch { /* a leftover file only costs a spurious report */ }
        }
    }

    /// <summary>
    /// Hands this session off to a process image that is about to REPLACE this one via <c>execv</c>,
    /// which is how an applied auto-update relaunches on macOS and Linux.
    ///
    /// <para><b>An exec is not an exit, and that is exactly the problem.</b> The pid survives, the
    /// runtime does not, and <see cref="AppDomain.ProcessExit"/> therefore never fires — so
    /// <see cref="MarkCleanExit"/> never runs and the session file is left behind. The replacement
    /// image then starts, sweeps the directory seconds later, finds a session file nobody owns
    /// (the exec closed the handle) and promotes it into a crash report announcing that the previous
    /// session "was killed rather than throwing". Nothing crashed; the user updated. The trail in
    /// such a report is EMPTY, which is the tell — a real death has breadcrumbs.</para>
    ///
    /// <para>So the update path says so explicitly, immediately before it execs. If the exec returns
    /// — it only returns on failure — call <see cref="ResumeAfterExec"/>, because a session that
    /// carries on running still deserves a report if it dies.</para>
    /// </summary>
    public static void HandOffToExec() => MarkCleanExit();

    /// <summary>
    /// Re-arms reporting after a <see cref="HandOffToExec"/> whose <c>execv</c> failed and returned.
    /// A no-op unless the reporter is installed and its session file is closed, so calling it on any
    /// other path cannot produce a second session file for one process.
    /// </summary>
    public static void ResumeAfterExec()
    {
        lock (_gate)
        {
            if (!_installed || _dir is null || _writer is not null) return;
            try { OpenSession(); }
            catch { Close(); }   // same bargain as Install: no disk, no reports, never a failure
        }
    }

    // ── Handlers ─────────────────────────────────────────────────────────────────

    private static void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
        => ReportFatal("AppDomain.UnhandledException", e.ExceptionObject as Exception, e.IsTerminating);

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        // Not fatal in .NET (the default is to swallow it), so this is a breadcrumb rather than a
        // report — but it is the trail that explains a run which produced no results and no message.
        Note($"unobserved task exception: {Flatten(e.Exception)}");
        e.SetObserved();
    }

    /// <summary>
    /// Records dispatcher exceptions that nothing else handled. Call AFTER any
    /// <c>Dispatcher.UIThread.UnhandledException</c> backstop the application installs — subscription
    /// order is invocation order, so subscribing last is what lets this read
    /// <c>e.Handled</c> and stay quiet about an exception a backstop deliberately swallowed.
    /// </summary>
    public static void WireDispatcherLogging()
    {
        try
        {
            Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                if (e.Handled) return;
                ReportFatal("Dispatcher.UIThread.UnhandledException", e.Exception);
            };
        }
        catch { /* no dispatcher yet — the AppDomain handler still covers the same exception */ }
    }

    // ── Internals ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Turns every session file nobody owns into a report. Returns the reports it made, newest last.
    /// </summary>
    private static List<string> PromoteAbandonedSessions(string dir)
    {
        var made = new List<string>();
        string[] stale;
        try { stale = Directory.GetFiles(dir, "session-*.running"); }
        catch { return made; }

        foreach (string path in stale.OrderBy(p => p, StringComparer.Ordinal))
        {
            try
            {
                // An image this same process EXECed away from is not a crash. HandOffToExec covers
                // the outgoing side, but only for a version that already has it — an update FROM an
                // older build execs without saying so, and the file it leaves is swept by the NEW
                // build seconds later. So the incoming side recognises it too: see IsOwnExecPredecessor.
                if (IsOwnExecPredecessor(path))
                {
                    try { File.Delete(path); } catch { /* a leftover only costs a spurious report */ }
                    continue;
                }

                // The liveness probe. An exclusive open succeeds only if no process holds the file,
                // and the handle is closed again immediately — the read below reopens it shared.
                try
                {
                    using var probe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                }
                catch (IOException) { continue; }              // a live sibling instance owns it
                catch (UnauthorizedAccessException) { continue; }

                string body = File.ReadAllText(path);
                bool hadManagedFatal = body.Contains("=== FATAL ===", StringComparison.Ordinal);

                var sb = new StringBuilder();
                if (!hadManagedFatal)
                    sb.AppendLine(
                        "This session ended WITHOUT a clean exit and WITHOUT a managed exception." + Environment.NewLine +
                        "That combination means the process was killed rather than throwing: a stack" + Environment.NewLine +
                        "overflow, the out-of-memory killer, a fault in native code, or a force-quit." + Environment.NewLine +
                        "No stack trace exists for any of those; the breadcrumb trail below is the" + Environment.NewLine +
                        "record of what the application was doing when it stopped." + Environment.NewLine);
                sb.Append(body);

                string target = Path.Combine(dir, Path.GetFileNameWithoutExtension(path)
                                                      .Replace("session-", "crash-", StringComparison.Ordinal) + ".log");
                File.WriteAllText(target, sb.ToString());
                File.Delete(path);
                made.Add(target);
            }
            catch { /* one unreadable leftover must not stop the others being promoted */ }
        }

        return made;
    }

    /// <summary>
    /// True when <paramref name="path"/> is the session file of the process image THIS process
    /// replaced via <c>execv</c> — an applied auto-update, not a crash.
    ///
    /// <para><b>Why the incoming side needs its own test.</b> <see cref="HandOffToExec"/> closes the
    /// session before the exec, which settles it for every version that has that call. It cannot
    /// settle an update <i>from</i> a version released before it: 1.0.0-beta.3 execed straight into
    /// the new bundle, so its session file was still lying there when beta.4 swept the directory two
    /// seconds later, and the user was told beta.3 "did not shut down cleanly". It shut down
    /// perfectly. The outgoing fix cannot be applied retroactively to a build already in the field,
    /// so the receiving build has to be the one that knows.</para>
    ///
    /// <para><b>The discriminator is exact, not a heuristic.</b> An <c>execv</c> keeps the process id
    /// and does NOT restart the kernel's process clock, so the predecessor's session — whose id and
    /// start second are both in its file NAME — reports our own pid and a start at or after our own
    /// OS-reported start time. Nothing else can: a live sibling cannot hold our pid because we hold
    /// it, and a stale file from a recycled pid necessarily started before this process did.</para>
    ///
    /// <para>Every uncertain answer is <c>false</c> — an unparseable name, an unavailable start time,
    /// a platform with no exec at all. That keeps the failure direction right: the worst this guard
    /// can do is decline to suppress, which is exactly the behaviour it replaced.</para>
    /// </summary>
    private static bool IsOwnExecPredecessor(string path)
    {
        try
        {
            // session-{yyyyMMdd}-{HHmmss}-{pid}.running
            string[] parts = Path.GetFileNameWithoutExtension(path).Split('-');
            if (parts.Length != 4 || !string.Equals(parts[0], "session", StringComparison.Ordinal)) return false;

            if (!int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out int pid)) return false;
            if (pid != Environment.ProcessId) return false;

            if (!DateTime.TryParseExact(parts[1] + "-" + parts[2], "yyyyMMdd-HHmmss",
                                        CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime started))
                return false;

            // Truncated to the second, because the file name is: our own start carries a fraction the
            // predecessor's stamp never had, and without this the comparison fails by milliseconds.
            DateTime start = Process.GetCurrentProcess().StartTime;
            start = start.AddTicks(-(start.Ticks % TimeSpan.TicksPerSecond));

            return started >= start;
        }
        catch { return false; }
    }

    private static void Prune(string dir)
    {
        try
        {
            var reports = Directory.GetFiles(dir, "crash-*.log")
                                   .OrderByDescending(File.GetLastWriteTimeUtc)
                                   .Skip(KeepReports);
            foreach (string old in reports)
                try { File.Delete(old); } catch { /* best effort */ }
        }
        catch { /* best effort */ }
    }

    private static string Header()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{_appName} crash report");
        sb.AppendLine("Send this file to the developers. It records what the application was doing;");
        sb.AppendLine("it can therefore contain workspace, document and kit NAMES and PATHS.");
        sb.AppendLine();
        sb.AppendLine($"application : {_appName}");
        sb.AppendLine($"version     : {AppVersion.Display}");
        sb.AppendLine($"started     : {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine($"process id  : {Environment.ProcessId}");
        sb.AppendLine($"os          : {RuntimeInformation.OSDescription}");
        sb.AppendLine($"os arch     : {RuntimeInformation.OSArchitecture}");
        sb.AppendLine($"process arch: {RuntimeInformation.ProcessArchitecture}");
        sb.AppendLine($"runtime     : {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"processors  : {Environment.ProcessorCount}");
        sb.AppendLine($"64-bit proc : {Environment.Is64BitProcess}");
        try
        {
            var mem = GC.GetGCMemoryInfo();
            sb.AppendLine($"total ram   : {mem.TotalAvailableMemoryBytes / (1024 * 1024)} MB");
        }
        catch { /* not available everywhere */ }
        sb.AppendLine($"state dir   : {Dir}");
        AppendExecutionEnvironment(sb);
        sb.AppendLine();
        sb.AppendLine("--- trail ---------------------------------------------------------");
        return sb.ToString();
    }

    /// <summary>
    /// How this process's CODE IS BEING EXECUTED, as opposed to what it is executing on. Everything
    /// above describes the machine; these lines describe the runtime's own configuration and anything
    /// that has been injected into the process to alter it.
    ///
    /// <para><b>Why it is worth four lines.</b> A managed exception whose own arithmetic says it was
    /// unreachable — an in-range array index that faulted anyway — has no explanation left in the
    /// application's state, and five rounds of one field report have now spent them all
    /// (<c>src/RfCore/RESOLVED.md</c>). What remains is the layer underneath: a CLR profiler injected
    /// by endpoint-protection or APM software rejitting methods, a debugger attached, or non-default
    /// codegen. Those are invisible in every report so far and free to record. A report that says
    /// "no profiler, tiering default, no debugger" narrows the field just as usefully as one that
    /// names an injected module.</para>
    ///
    /// <para>Names only, never paths: which DLLs are in the process is diagnostic, where they live on
    /// the reporter's disk is not ours to collect.</para>
    /// </summary>
    private static void AppendExecutionEnvironment(StringBuilder sb)
    {
        try
        {
            sb.AppendLine($"debugger    : {(Debugger.IsAttached ? "ATTACHED" : "no")}");
            sb.AppendLine($"gc          : {(System.Runtime.GCSettings.IsServerGC ? "server" : "workstation")}"
                        + $", {System.Runtime.GCSettings.LatencyMode}");

            // A CLR profiler can rewrite IL and force rejits, so its presence changes what "the same
            // code" means. Both the modern and the legacy variable names, because injectors set either.
            var prof = new List<string>();
            foreach (string v in new[] { "CORECLR_ENABLE_PROFILING", "CORECLR_PROFILER",
                                         "COR_ENABLE_PROFILING",     "COR_PROFILER" })
                if (Environment.GetEnvironmentVariable(v) is { Length: > 0 } val)
                    prof.Add($"{v}={val}");
            sb.AppendLine($"profiler    : {(prof.Count == 0 ? "none" : string.Join(" ", prof))}");

            // Only the knobs that change CODEGEN. Unset is the shipping configuration, and saying so
            // explicitly is the point — it is what makes a set one stand out.
            var jit = new List<string>();
            foreach (string v in new[] { "DOTNET_TieredCompilation", "DOTNET_TieredPGO",
                                         "DOTNET_TC_QuickJitForLoops", "DOTNET_ReadyToRun",
                                         "DOTNET_JitMinOpts", "DOTNET_ZapDisable" })
                if (Environment.GetEnvironmentVariable(v) is { Length: > 0 } val)
                    jit.Add($"{v}={val}");
            sb.AppendLine($"codegen env : {(jit.Count == 0 ? "all default" : string.Join(" ", jit))}");

            AppendForeignModules(sb);
        }
        catch { /* best effort, like every other line here */ }
    }

    /// <summary>
    /// Native modules loaded into the process from neither the operating system nor the application's
    /// own directory — i.e. injected. Classified by path, reported by NAME.
    /// </summary>
    private static void AppendForeignModules(StringBuilder sb)
    {
        try
        {
            string appDir = AppContext.BaseDirectory;
            string sys    = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var foreign   = new List<string>();
            int total     = 0;

            foreach (ProcessModule m in Process.GetCurrentProcess().Modules)
            {
                total++;
                string? dir = null;
                try { dir = Path.GetDirectoryName(m.FileName); } catch { /* denied: treat as unknown */ }
                bool ours =
                    dir is not null &&
                    ((appDir.Length > 0 && dir.StartsWith(appDir.TrimEnd(Path.DirectorySeparatorChar),
                                                          StringComparison.OrdinalIgnoreCase))
                     || (sys.Length > 0 && dir.StartsWith(sys, StringComparison.OrdinalIgnoreCase))
                     || dir.StartsWith("/usr/", StringComparison.Ordinal)
                     || dir.StartsWith("/System/", StringComparison.Ordinal));
                if (!ours && foreign.Count < 12) foreign.Add(m.ModuleName);
            }

            sb.AppendLine($"modules     : {total} loaded, "
                        + (foreign.Count == 0
                            ? "none from outside the OS or the app directory"
                            : $"from elsewhere: {string.Join(", ", foreign)}"));
        }
        catch { /* Modules is denied or unavailable on some hosts */ }
    }

    /// <summary>Full detail of an exception: type, message, stack, and every inner one.</summary>
    private static string Describe(Exception? ex)
    {
        if (ex is null) return "(the runtime reported a non-Exception object; no detail is available)";

        var sb = new StringBuilder();
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            sb.AppendLine($"{e.GetType().FullName}: {e.Message}");
            foreach (var kv in e.Data.Keys.Cast<object>())
                sb.AppendLine($"  data[{kv}] = {e.Data[kv]}");
            sb.AppendLine(e.StackTrace ?? "  (no stack trace)");
            if (e is AggregateException agg)
                foreach (var sub in agg.Flatten().InnerExceptions)
                    sb.AppendLine("  --- aggregated ---" + Environment.NewLine + Describe(sub));
            if (e.InnerException is not null) sb.AppendLine("--- inner ---");
        }
        return sb.ToString();
    }

    private static string Flatten(AggregateException agg)
        => string.Join(" | ", agg.Flatten().InnerExceptions.Select(e => $"{e.GetType().Name}: {e.Message}"));

    private static void Close()
    {
        try { _writer?.Dispose(); } catch { /* best effort */ }
        _writer = null;
        _path   = null;
    }

    // ── Test seam ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Tears the reporter down so a test can install it again against another directory. Test-only:
    /// the application installs once and lives with it.
    /// </summary>
    internal static void ResetForTests()
    {
        lock (_gate)
        {
            try { AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException; } catch { }
            try { TaskScheduler.UnobservedTaskException      -= OnUnobservedTaskException; } catch { }
            Close();
            _installed  = false;
            _promoted   = false;
            _dir        = null;
            _uiThreadId = 0;
            _pending.Clear();
        }
    }
}

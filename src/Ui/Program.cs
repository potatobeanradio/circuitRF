using Avalonia;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CircuitRF.Design.Revision;

namespace CircuitRF.Ui;

sealed class Program
{
    // Windows single-instance: named pipe forwards workspace paths from a second instance.
    //
    // RC-5 R-rc5-7c: the SAME channel now also carries two requests from a headless `serve` — does
    // the window hold unsaved changes to this workspace, and here are the documents a batch just
    // changed. The name is CircuitRF.Design's, because the client half lives below the firewall
    // where src/Cli can reach it and the two halves must not each spell it for themselves.
    private const string PipeName = CircuitRF.Design.Revision.WindowChannel.EndpointName;

    [STAThread]
    public static void Main(string[] args)
    {
        // THE COMMAND LINE, BEFORE ANYTHING ELSE (brief-automation-13-installed-cli.md R-aut13-1).
        // The installed circuitRF executable IS the CLI: `circuitRF check .` and `circuitRF serve
        // --root <dir>` run the same CliEntry.Run that `dotnet run --project src/Cli` does, and exit.
        //
        // FIRST, and every line below this one is wrong for a CLI call — each silently:
        //   * CrashReporter.Install writes the GUI's session file; a CLI exit is not a GUI session.
        //   * AppRelaunch / ReleaseNotesGate would record this call as a launch of the application.
        //   * UpdateStartup.RunBeforeUi applies a staged update and HANDS THIS PROCESS OVER (execv on
        //     Linux), so `circuitrf check` could turn into a GUI launch of the new version.
        //   * The Windows mutex and the Linux lock would send a CLI call made while the window is open
        //     down the "not first" branch, forwarding its arguments to the window as files to open.
        //   * ExternalWorkerPolicy is left out too: the installed CLI behaves exactly as src/Cli's own
        //     executable, which installs no consent hook and runs workers (the setting's default).
        // Avalonia is never initialised on this path — no AppBuilder, and on macOS no NSApplication,
        // so no Dock icon and no window. src/Ui names no verb: CliEntry.IsVerb is answered by the
        // same switch Run dispatches on. A double-click delivers a full path or, on macOS, an Apple
        // Event with no argument at all, so opening a document never reaches this branch.
        // ProgramHarmonica and ProgramWBond deliberately do not do this (out of AUT-13's scope).
        if (args.Length > 0 && CircuitRF.Cli.CliEntry.IsVerb(args[0]))
            Environment.Exit(CircuitRF.Cli.CliEntry.Run(args));

        // FIRST, before Avalonia: a crash while the toolkit is coming up is still a crash the user
        // needs a report for. See Diagnostics/CrashReporter for why the session file, and not the
        // exception handlers, is the part that catches a simulation death.
        Diagnostics.CrashReporter.Install("circuitRF");

        // BEFORE the single-instance guard below, and before the staged update is applied: a
        // successor started by the Relaunch action must not exist as a second instance while the
        // session that started it is still shutting down.
        //
        // On Windows that guard is a Mutex and on Linux a flock()ed file, both held for the whole
        // life of the process — so a successor that raced its predecessor would take the
        // "not first" branch, forward its arguments to the instance that is on its way out, and
        // return without ever showing a window. The user clicks Relaunch and circuitRF closes.
        // macOS has no such guard, but is given the same argument for uniformity.
        //
        // Stripped from `args` here, so nothing downstream — the startup file scan, Avalonia itself —
        // ever sees it. See AppRelaunch.StartSuccessor for the other half.
        if (Updates.AppRelaunch.TakeWaitForPid(ref args) is { } predecessor)
            Updates.AppRelaunch.WaitForProcessExit(predecessor);

        // BEFORE the line below, which writes state.json on every path that applies an update: settle
        // whether this installation existed at all before this launch. That single fact is what tells
        // a brand new installation (which must never open with release notes) apart from an existing
        // one running a build that has just gained the feature. circuitRF only — see ReleaseNotesGate.
        Updates.ReleaseNotesGate.CaptureAtStartup();

        // BEFORE Avalonia, and before anything opens a file: reclaim update debris, revert a
        // version that has failed to start twice, and apply a staged update. An applied update
        // hands this launch over to the new version and this call does not return — through Launch
        // Services on macOS (UpdateStartup.HandOverTo says why it may not be execv there), by
        // execv() on Linux, and by starting it and exiting on Windows, which has no execv. Never
        // mid-session, for the reasons in docs/design/auto-update.md §3.
        Updates.UpdateStartup.RunBeforeUi(args);

        // The consent gate for external device workers, installed BEFORE anything can resolve a
        // device. src/Core cannot read AppPreferences — that is the UI firewall — so the policy is
        // a hook, and a build that never installs one runs workers, which is this setting's stated
        // default. Installed in all three entry points; ExternalWorkerConsentTests pins that.
        Security.ExternalWorkerPolicy.Install();

        // Dev tool: regenerate the User-Documentation component artwork from the live drawing engine,
        // then exit. No GUI window opens. Usage:
        //   dotnet run --project src/Ui -- --generate-symbols docs/user/assets/symbols
        if (args.Length >= 1 && args[0] == "--generate-symbols")
        {
            string outDir = args.Length >= 2
                ? args[1]
                : Path.Combine(AppContext.BaseDirectory, "symbols-out");
            BuildAvaloniaApp().SetupWithoutStarting();   // registers the asset loader so fonts resolve
            var files = Diagnostics.SymbolArtworkGenerator.GenerateAll(outDir);
            Console.WriteLine($"Wrote {files.Count} symbol SVG files to {Path.GetFullPath(outDir)}");
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            using var mutex = new Mutex(true, $"Local\\{PipeName}", out bool isFirst);

            if (!isFirst)
            {
                TrySendFilesToPipe(args);
                return;
            }

            var cts = new CancellationTokenSource();
            _ = Task.Run(() => RunPipeServerAsync(cts.Token));

            try   { BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
            finally { cts.Cancel(); }

            return;
        }

        // Linux: the same single-instance forwarding the Windows branch above does, over a Unix
        // domain socket because there is no named pipe and no cross-process Mutex.
        //
        // WHY IT IS NEEDED AT ALL, given that circuitRF ran on Linux for a long time without it: the
        // desktop's answer to a double-click is to EXEC the .desktop entry's Exec= line, every time.
        // With only three registered types that was rare enough to live with; now that every document
        // type opens by double-click, without this a user inspecting three files gets three whole
        // copies of the application, each with its own workspace — and the second copy's "is this file
        // part of the open workspace?" answer is no, because that copy has no workspace open.
        //
        // macOS needs none of this: Launch Services delivers an Apple Event to the running app, which
        // App.OnActivated handles.
        if (OperatingSystem.IsLinux())
        {
            // The lock, not the socket, is what decides who is first — two launches racing can both
            // find no socket to connect to, and would then both bind one. .NET implements
            // FileShare.None on Unix with flock(), so this is an actual cross-process exclusion.
            FileStream? instanceLock = null;
            try   { instanceLock = new FileStream(LinuxLockPath(), FileMode.OpenOrCreate,
                                                 FileAccess.ReadWrite, FileShare.None); }
            catch (IOException)        { /* held by the running instance */ }
            catch (UnauthorizedAccessException) { /* unwritable runtime dir — fall through and just run */ }

            if (instanceLock is null && TrySendFilesToSocket(args))
                return;

            // Either we are the first instance, or we could not reach the one that is (it may still be
            // starting, or be wedged). Running normally is the right fallback: the user asked to see a
            // file, and a second window showing it beats no window at all.
            using (instanceLock)
            {
                var linuxCts = new CancellationTokenSource();
                if (instanceLock is not null)
                    _ = Task.Run(() => RunSocketServerAsync(linuxCts.Token));

                try   { BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
                finally
                {
                    linuxCts.Cancel();
                    if (instanceLock is not null)
                        try { File.Delete(LinuxSocketPath()); } catch { /* best effort */ }
                }
            }

            return;
        }

        // macOS. Launch Services still delivers "open file" Apple Events to the running instance via
        // IActivatableLifetime.Activated, so nothing here forwards a path — but RC-5's channel has a
        // second job that the OS does not do for us (R-rc5-7c): a headless `serve` has to be able to
        // ask this window whether it holds unsaved changes, and to tell it what a batch changed. So
        // the Unix socket comes up here too, listening only.
        //
        // Deliberately WITHOUT the instance lock: on macOS a second copy of the application is not
        // the ordinary state, and taking a lock here would change launch behaviour on the one
        // platform this feature has no business changing it on. A bind that fails because another
        // copy already holds the path is simply a copy that does not listen, which is
        // WindowChannel's "no window to protect" and not an error.
        var macCts = new CancellationTokenSource();
        _ = Task.Run(() => RunSocketServerAsync(macCts.Token));

        try   { BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
        finally { macCts.Cancel(); }
    }

    // ---- Linux single-instance (Unix domain socket) --------------------------------

    /// <summary>The runtime directory both endpoints agree on. <c>XDG_RUNTIME_DIR</c> when the session
    /// has one (per-user, cleaned up at logout, and short — a Unix socket path is capped near 104
    /// bytes); <c>/tmp</c> otherwise — which is shared, so the user name is in the socket's own name
    /// there and two users on one machine do not collide on it.</summary>
    private static string LinuxRuntimeDir()
    {
        string? xdg = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (!string.IsNullOrEmpty(xdg) && Directory.Exists(xdg)) return xdg;
        return Path.GetTempPath();
    }

    private static string LinuxSocketPath() => CircuitRF.Design.Revision.WindowChannel.UnixSocketPath();

    private static string LinuxLockPath() => CircuitRF.Design.Revision.WindowChannel.UnixLockPath();

    /// <summary>Second instance: hand the paths to the running one. Returns false when there is
    /// nobody to hand them to, so the caller can fall back to starting normally.</summary>
    private static bool TrySendFilesToSocket(string[] args)
    {
        var filePaths = args.Where(File.Exists).ToArray();

        try
        {
            using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            client.Connect(new UnixDomainSocketEndPoint(LinuxSocketPath()));

            // Connected with nothing to send — a bare re-launch of an already-running circuitRF. Still
            // a success: exiting quietly is what the user wants, not a second copy of the application.
            if (filePaths.Length > 0)
            {
                byte[] payload = Encoding.UTF8.GetBytes(string.Join('\n', filePaths) + '\n');
                client.Send(payload);
            }
            client.Shutdown(SocketShutdown.Both);
            return true;
        }
        catch { return false; }
    }

    /// <summary>First instance: accept forwarded paths and open them. Mirrors
    /// <see cref="RunPipeServerAsync"/>, including swallowing per-connection errors rather than
    /// letting one bad client end the loop.</summary>
    private static async Task RunSocketServerAsync(CancellationToken ct)
    {
        string path = LinuxSocketPath();

        // A socket file outlives the process that made it, so a crash or a kill leaves one behind that
        // nothing is listening on. It is safe to remove HERE and only here: the instance lock is held,
        // so no live instance owns it.
        try { File.Delete(path); } catch { /* best effort */ }

        Socket listener;
        try
        {
            listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(4);
        }
        catch { return; }   // no forwarding available; the app itself is unaffected

        using (listener)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var conn = await listener.AcceptAsync(ct);
                    using var stream = new NetworkStream(conn, ownsSocket: false);
                    using var reader = new StreamReader(stream, Encoding.UTF8);

                    var paths = new List<string>();
                    string? line;
                    string? answer = null;

                    while ((line = await reader.ReadLineAsync(ct)) is not null)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;

                        // RC-5 R-rc5-7c. A line carrying the request prefix is one of the channel's
                        // two questions; anything else is a path to open, exactly as before.
                        if (WindowChannel.Parse(line) is { } request) answer ??= Answer(request);
                        else paths.Add(line);
                    }

                    if (answer is not null)
                    {
                        byte[] reply = Encoding.UTF8.GetBytes(answer + "\n");
                        await conn.SendAsync(reply, ct);
                    }

                    if (paths.Count > 0)
                    {
                        string[] arr = paths.ToArray();
                        Avalonia.Threading.Dispatcher.UIThread.Post(() => App.HandleExternalFiles(arr));
                    }
                }
                catch (OperationCanceledException) { break; }
                catch { /* Swallow per-connection errors; restart loop. */ }
            }
        }

        try { File.Delete(path); } catch { /* best effort */ }
    }

    private static async Task RunPipeServerAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // InOut rather than In, since RC-5 R-rc5-7c's first message is a QUESTION and a
                // one-way pipe has nowhere to put the answer. A client that only forwards paths
                // still connects with PipeDirection.Out and reads nothing back, so the older
                // behaviour is unchanged.
                using var server = new NamedPipeServerStream(
                    PipeName, PipeDirection.InOut,
                    maxNumberOfServerInstances: 1,
                    transmissionMode: PipeTransmissionMode.Byte,
                    options: PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(ct);

                using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
                var paths = new List<string>();
                string? line;
                string? answer = null;

                while ((line = await reader.ReadLineAsync(ct)) is not null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (WindowChannel.Parse(line) is { } request) answer ??= Answer(request);
                    else paths.Add(line);

                    // A request is one line and the client is waiting: reading on would block until
                    // it closed its end, which it cannot do before it has the answer.
                    if (answer is not null) break;
                }

                if (answer is not null)
                {
                    using var writer = new StreamWriter(server, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };
                    await writer.WriteLineAsync(answer);
                }

                if (paths.Count > 0)
                {
                    string[] arr = paths.ToArray();
                    Avalonia.Threading.Dispatcher.UIThread.Post(
                        () => App.HandleExternalFiles(arr));
                }
            }
            catch (OperationCanceledException) { break; }
            catch { /* Swallow per-connection errors; restart loop. */ }
        }
    }

    /// <summary>
    /// Answers one of RC-5's two channel requests (R-rc5-7a, R-rc5-7b).
    ///
    /// <para><b>The unsaved question is answered SYNCHRONOUSLY, on the UI thread</b>, because a batch
    /// is waiting on it and an answer that arrived after the batch had already started would protect
    /// nothing. The modified notice is posted and not waited on: the window's reload is its own
    /// business and the batch has nothing to do with the outcome.</para>
    ///
    /// <para><b>Anything that goes wrong answers "no"</b> — which is the honest reading: this process
    /// could not establish that a window holds unsaved changes, and "no window to protect" is a real
    /// state rather than a failure.</para>
    /// </summary>
    private static string Answer(WindowRequest request)
    {
        try
        {
            switch (request.Verb)
            {
                case WindowChannel.AskUnsaved:
                {
                    bool dirty = Avalonia.Threading.Dispatcher.UIThread
                        .InvokeAsync(() => App.WorkspaceHasUnsavedChanges(request.WorkspaceRoot))
                        .GetTask()
                        .WaitAsync(WindowChannel.Timeout)
                        .GetAwaiter().GetResult();

                    return dirty ? WindowChannel.Yes : WindowChannel.No;
                }

                case WindowChannel.TellModified:
                {
                    var root  = request.WorkspaceRoot;
                    var paths = request.Paths;
                    Avalonia.Threading.Dispatcher.UIThread.Post(
                        () => App.WorkspaceDocumentsChangedUnderneath(root, paths));
                    return "ok";
                }

                default:
                    return WindowChannel.No;
            }
        }
        catch (Exception e) when (e is TimeoutException or InvalidOperationException or OperationCanceledException)
        {
            return WindowChannel.No;
        }
    }

    private static void TrySendFilesToPipe(string[] args)
    {
        var filePaths = args.Where(File.Exists).ToArray();
        if (filePaths.Length == 0) return;

        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(2000);
            using var writer = new StreamWriter(client, Encoding.UTF8, leaveOpen: true);
            foreach (var path in filePaths)
                writer.WriteLine(path);
        }
        catch { /* Fail silently if first instance is unreachable. */ }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}

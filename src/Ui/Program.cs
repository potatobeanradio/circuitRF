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

namespace CircuitRF.Ui;

sealed class Program
{
    // Windows single-instance: named pipe forwards workspace paths from a second instance.
    private const string PipeName = "circuitRF_workspace_v1";

    [STAThread]
    public static void Main(string[] args)
    {
        // FIRST, before Avalonia: a crash while the toolkit is coming up is still a crash the user
        // needs a report for. See Diagnostics/CrashReporter for why the session file, and not the
        // exception handlers, is the part that catches a simulation death.
        Diagnostics.CrashReporter.Install("circuitRF");

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

        // macOS: no socket needed — Launch Services delivers "open file" Apple Events to the running
        // instance via IActivatableLifetime.Activated.
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
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

    private static string LinuxSocketPath()
        => Path.Combine(LinuxRuntimeDir(), $"{PipeName}-{Environment.UserName}.sock");

    private static string LinuxLockPath()
        => Path.Combine(LinuxRuntimeDir(), $"{PipeName}-{Environment.UserName}.lock");

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
                    while ((line = await reader.ReadLineAsync(ct)) is not null)
                        if (!string.IsNullOrWhiteSpace(line)) paths.Add(line);

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
                using var server = new NamedPipeServerStream(
                    PipeName, PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    transmissionMode: PipeTransmissionMode.Byte,
                    options: PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(ct);

                using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
                var paths = new List<string>();
                string? line;
                while ((line = await reader.ReadLineAsync(ct)) is not null)
                    if (!string.IsNullOrWhiteSpace(line)) paths.Add(line);

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

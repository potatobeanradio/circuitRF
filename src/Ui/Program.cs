using Avalonia;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
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

        // macOS / Linux: no named-pipe single-instance logic.
        // macOS delivers "open file" Apple Events via IActivatableLifetime.Activated.
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
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

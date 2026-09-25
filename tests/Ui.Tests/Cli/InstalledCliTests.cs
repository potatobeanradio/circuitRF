// ================================================================
//  InstalledCliTests.cs — the circuitRF executable IS the command line
//  (brief-automation-13-installed-cli.md).
//
//  Every release through 1.0.0-beta.32 shipped a GUI and no command-line driver at all, and every CLI
//  gate stayed green, because they all launch src/Cli/bin. The gate that runs what an INSTALLER holds
//  is tools/CliSmoke, which each packaging script runs; these are the source-tree halves of it:
//
//    * the dispatch is the FIRST thing Program.Main does — every line after it is wrong for a CLI
//      call, silently (CrashReporter, AppRelaunch, ReleaseNotesGate, the staged update, the
//      single-instance mutex/lock/socket);
//    * IsVerb is false for every shape a double-click delivers, so opening a document is untouched;
//    * `check --json` through the application executable is byte-identical to CircuitRF.Cli.dll's;
//    * a `serve` whose installation is replaced under it answers with the reason and leaves.
// ================================================================

using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CircuitRF.Cli;
using CircuitRF.Cli.Serve;

namespace CircuitRF.Ui.Tests.Cli;

public sealed class InstalledCliTests
{
    // ── dispatch order ───────────────────────────────────────────────────────

    /// <summary>
    /// R-aut13-1: in circuitRF's Program.Main the verb dispatch precedes everything that is wrong for
    /// a CLI call. Each of these fails SILENTLY if it runs first — the staged update hands the process
    /// over, and the mutex/lock/socket forward the arguments to an open window as files to open.
    /// </summary>
    [Fact]
    public void ProgramMain_DispatchesTheCommandLine_BeforeAnythingOfTheGuisRuns()
    {
        string source = StripComments(File.ReadAllText(RepoFile("src/Ui/Program.cs")));
        int main = source.IndexOf("public static void Main(string[] args)", StringComparison.Ordinal);
        Assert.True(main >= 0, "Program.Main not found");
        string body = source[source.IndexOf('{', main)..];

        int dispatch = body.IndexOf("CliEntry.IsVerb(args[0])", StringComparison.Ordinal);
        Assert.True(dispatch >= 0, "Program.Main no longer dispatches to CliEntry.IsVerb");
        Assert.Contains("Environment.Exit(CircuitRF.Cli.CliEntry.Run(args))", body[dispatch..]);

        // The FIRST statement, not merely an early one.
        string firstStatement = body[1..].TrimStart();
        Assert.StartsWith("if (args.Length > 0 && CircuitRF.Cli.CliEntry.IsVerb(args[0]))", firstStatement);

        foreach (string later in new[]
                 {
                     "CrashReporter.Install", "AppRelaunch", "ReleaseNotesGate", "UpdateStartup.RunBeforeUi",
                     "ExternalWorkerPolicy.Install", "new Mutex", "FileShare.None", "Socket", "BuildAvaloniaApp",
                 })
        {
            int at = body.IndexOf(later, StringComparison.Ordinal);
            Assert.True(at > dispatch, $"'{later}' must come after the CLI dispatch in Program.Main (found at {at}, dispatch at {dispatch})");
        }
    }

    // ── IsVerb ───────────────────────────────────────────────────────────────

    [Fact]
    public void IsVerb_IsTrueForEveryVerbAndTheVersionFlag()
    {
        string[] verbs =
        [
            "sparam", "dc", "hb", "lp", "loadpull", "lpp", "loadpull_pursuit", "pursuit", "em", "rail",
            "smith", "convert", "new", "import", "check", "history", "explain", "lvs", "impedance", "render", "read",
            "netlist", "plot", "find", "reference", "serve", "elab", "--version",
            // Run lower-cases the verb, so IsVerb must agree with it.
            "Check", "SERVE",
        ];
        foreach (string v in verbs) Assert.True(CliEntry.IsVerb(v), $"'{v}' is a verb Run dispatches, and IsVerb says it is not");
    }

    /// <summary>
    /// What a double-click delivers is fixed by each platform: Windows' associations pass "%1" (a full
    /// path), Linux's .desktop passes %F (full paths), macOS passes no argument at all. None of those
    /// may start the CLI — including a file literally NAMED after a verb, because the comparison is
    /// against the WHOLE first argument and never its file name.
    /// </summary>
    [Fact]
    public void IsVerb_IsFalseForEveryShapeADoubleClickDelivers()
    {
        var extensions = Regex.Matches(File.ReadAllText(RepoFile("packaging/windows/circuitRF.wxs")),
                                       "<Extension Id=\"([^\"]+)\"")
                              .Select(m => m.Groups[1].Value).ToList();
        Assert.Contains("csch", extensions);   // the regex still reads the file
        Assert.Contains("clay", extensions);

        var delivered = new List<string>();
        foreach (string ext in extensions)
        {
            delivered.Add($"/home/someone/My Designs/amp.{ext}");
            delivered.Add($@"C:\Users\someone\My Designs\amp.{ext}");
        }
        delivered.AddRange(
        [
            "/home/someone/check", "/Users/someone/serve", @"C:\x\sparam", @"C:\x\check.csch",
            "/tmp/render/", "", "-psn_0_12345",
        ]);

        foreach (string arg in delivered) Assert.False(CliEntry.IsVerb(arg), $"'{arg}' would start the CLI instead of opening the document");
    }

    // ── parity ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The application executable answers exactly as CircuitRF.Cli.dll does — the byte-identity
    /// pattern of RenderCliVerbTests and EmCliVerbTests, applied to the new front door. Both run as
    /// PROCESSES: a same-process call cannot show what only a second process can differ in, and the
    /// application's module initializers (which run before its Main) are exactly such a thing.
    /// </summary>
    [Fact]
    public void CheckThroughTheApplicationExecutable_IsByteIdenticalToTheCli()
    {
        string fixture = RepoFile("examples/LVS");
        var viaCli = Run(CliDll(), "check", fixture, "--json");
        var viaApp = Run(AppDll(), "check", fixture, "--json");

        Assert.Equal(viaCli.ExitCode, viaApp.ExitCode);
        Assert.Equal(viaCli.Stdout, viaApp.Stdout);
        Assert.Equal(viaCli.Stderr, viaApp.Stderr);
        Assert.Contains("\"verb\": \"check\"", viaApp.Stdout);

        var version = Run(AppDll(), "--version");
        Assert.Equal(0, version.ExitCode);
        Assert.Equal(File.ReadAllText(RepoFile("VERSION")).Trim(), version.Stdout.TrimEnd('\r', '\n'));
    }

    // ── serve across an update ───────────────────────────────────────────────

    /// <summary>
    /// R-aut13-4: once the executable a server started from is replaced (macOS exchanges the bundle)
    /// or removed (a reclaimed Linux app-&lt;ver&gt; directory), the next call is answered with a JSON-RPC
    /// error naming the reason and the server exits — never a server that answers the tools whose code
    /// it had loaded and fails the rest with "Could not load file or assembly", which is what was
    /// measured without this.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Serve_AnswersAndLeaves_WhenItsInstallationChangesUnderIt(bool removed)
    {
        string dir = Path.Combine(Path.GetTempPath(), "crf-guard-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            string exe = Path.Combine(dir, "circuitRF");
            File.WriteAllText(exe, "version one");
            var guard = new InstallationGuard(exe);
            Assert.False(guard.Changed(out _));

            if (removed) File.Delete(exe);
            else File.WriteAllText(exe, "version two, which is longer");

            var input = new MemoryStream(Encoding.UTF8.GetBytes(
                """{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"find","arguments":{"path":"."}}}""" + "\n" +
                """{"jsonrpc":"2.0","id":8,"method":"tools/call","params":{"name":"find","arguments":{"path":"."}}}""" + "\n"));
            var output = new MemoryStream();
            var root   = PathRoot.Open(dir, out _)!;

            int code;
            using (var rpc = new JsonRpc(input, output))
                code = new McpServer(rpc, root, guard).Serve();

            Assert.Equal(1, code);
            var frames = Encoding.UTF8.GetString(output.ToArray())
                                 .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                                 .Select(l => JsonNode.Parse(l)!.AsObject()).ToList();
            var only = Assert.Single(frames);                       // id 8 is never answered: it left
            Assert.Equal(7, only["id"]!.GetValue<int>());
            Assert.Equal(JsonRpc.InstallationChanged, only["error"]!["code"]!.GetValue<int>());
            Assert.Contains(removed ? "no longer exists" : "has been replaced", only["error"]!["message"]!.GetValue<string>());
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ } }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static (int ExitCode, string Stdout, string Stderr) Run(string dll, params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        psi.ArgumentList.Add(dll);
        foreach (string a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        // Both pipes drained CONCURRENTLY: reading one to the end and only then the other deadlocks
        // the moment the child fills the pipe it is not being read from.
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }

    private static string CliDll()
    {
        string cliDir = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(InstalledCliTests).Assembly)
            .First(a => a.Key == "CliDir").Value!;
        string path = Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll"));
        Assert.True(File.Exists(path), $"the CLI was not built beside these tests: {path}");
        return path;
    }

    /// <summary>The application — CircuitRF.Ui.dll with its own runtimeconfig and deps, which the
    /// build copies beside these tests because they reference src/Ui.</summary>
    private static string AppDll()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "CircuitRF.Ui.dll");
        Assert.True(File.Exists(Path.ChangeExtension(path, ".runtimeconfig.json")),
                    $"the application is not runnable beside these tests: {path}");
        return path;
    }

    private static string RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
    }

    private static string StripComments(string src)
    {
        src = Regex.Replace(src, @"/\*.*?\*/", "", RegexOptions.Singleline);
        return Regex.Replace(src, @"//[^\n]*", "");
    }
}

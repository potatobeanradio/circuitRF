// ================================================================
//  ExamplesCliTests.cs — the shipped example workspaces, as an agent reaches them:
//  `circuitrf reference examples [name]` (the MCP resource circuitrf://reference/examples and the
//  `reference` tool) and `circuitrf new example <dir> <name>` (MCP `create what=example`).
//
//  What is held here:
//
//   1. NOTHING HAS TO BE LISTED TWICE. The topic reads the index File ▸ New ▸ Examples reads, so an
//      example added to examples/ and examples.json reaches an agent with no other edit. Asserted
//      against the DISK, not against ExampleWorkspaces.All, which would be the function agreeing with
//      itself: every folder holding a .cws is in the topic, in the index's order.
//
//   2. ONE EXAMPLE IS ENOUGH TO START FROM — its README verbatim, the files a copy carries, and its
//      cells with their declared analyses as `find` reports them.
//
//   3. THE HEADLESS COPY IS THE GUI'S COPY. The CLI copies with WorkspaceTree.CopyFiles; the GUI's
//      install also runs WorkspaceCopy's reference repair, which needs src/Ui. For a shipped example
//      that repair must write nothing, so every example is installed both ways and compared BYTE FOR
//      BYTE. An example that fails here stores a reference in a spelling the repair re-writes: store it
//      the way the repair writes it, and both copies agree again.
// ================================================================

using System.Diagnostics;
using System.Text.Json;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui.Schematic;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Examples;

public sealed class ExamplesCliTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _tmp = Path.Combine(
        Path.GetTempPath(), "crf-examples-cli-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { /* best effort */ } }

    /// <summary>Claim 1: every workspace folder on disk is listed, in the index's order, with the
    /// index's title and summary — and the topic is advertised in the topic list.</summary>
    [Fact]
    public void EveryExampleOnDiskIsListed_InIndexOrder()
    {
        string root = SourceExamplesRoot();
        var onDisk = Directory.GetDirectories(root)
            .Where(d => File.Exists(Path.Combine(d, ".cws")))
            .Select(Path.GetFileName)
            .ToHashSet(StringComparer.Ordinal);

        var run = RunCli("reference", "examples", "--json");
        Assert.Equal(0, run.ExitCode);
        var rows = Payload(run.StdOut).GetProperty("reference").GetProperty("examples").EnumerateArray().ToArray();

        Assert.Equal(onDisk.OrderBy(x => x, StringComparer.Ordinal),
                     rows.Select(r => r.GetProperty("name").GetString()!).OrderBy(x => x, StringComparer.Ordinal));

        using var index = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "examples.json")));
        var indexed = index.RootElement.GetProperty("Examples").EnumerateArray().ToArray();
        Assert.Equal(indexed.Select(e => e.GetProperty("Folder").GetString()),
                     rows.Select(r => r.GetProperty("name").GetString()));
        Assert.Equal(indexed.Select(e => e.GetProperty("Summary").GetString()),
                     rows.Select(r => r.GetProperty("summary").GetString()));

        var topics = Payload(RunCli("reference", "--json").StdOut).GetProperty("reference").GetProperty("topics");
        Assert.Contains(topics.EnumerateArray(), t => t.GetProperty("topic").GetString() == "examples");
    }

    /// <summary>Claim 2: one example, named by folder or by title, comes back with its README, the
    /// files a copy carries and its cells' analyses; a name that is not one is refused listing them.</summary>
    [Fact]
    public void OneExample_CarriesItsReadmeFilesAndAnalyses()
    {
        var example = ExampleWorkspaces.All(SourceExamplesRoot()).Single(e => e.Folder == "Loadpull");

        var byTitle = Payload(RunCli("reference", "examples", example.Title, "--json").StdOut)
            .GetProperty("reference").GetProperty("examples").EnumerateArray().Single();
        Assert.Equal("Loadpull", byTitle.GetProperty("name").GetString());

        Assert.Equal(File.ReadAllText(Path.Combine(example.Directory, "README.md")),
                     byTitle.GetProperty("readme").GetString());

        var files = byTitle.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("path").GetString()).ToList();
        Assert.Contains("Loadpull/schematic/load_grid.gam", files);
        Assert.DoesNotContain(files, f => f!.Split('/').Any(s => s.StartsWith(".crf-", StringComparison.Ordinal)));

        var cells = byTitle.GetProperty("workspace").GetProperty("cells").EnumerateArray().ToArray();
        var lp = cells.Single(c => c.GetProperty("name").GetString() == "Loadpull");
        Assert.Contains("LP1", lp.GetProperty("analyses").EnumerateArray().Select(a => a.GetString()));

        var refused = RunCli("reference", "examples", "No Such Example", "--json");
        Assert.Equal(1, refused.ExitCode);
        var d = JsonDocument.Parse(refused.StdOut).RootElement.GetProperty("diagnostics").EnumerateArray().Single();
        Assert.Equal("reference.example.unknown", d.GetProperty("id").GetString());
        Assert.Contains("Loadpull", d.GetProperty("message").GetString()!, StringComparison.Ordinal);
    }

    /// <summary>Claim 3: for every example, the headless install and File ▸ New ▸ Examples produce
    /// the same files with the same bytes.</summary>
    [Fact]
    public void TheHeadlessCopyOfEveryExampleIsTheGuisCopy_ByteForByte()
    {
        string gui = Path.Combine(_tmp, "gui"), cli = Path.Combine(_tmp, "cli");
        Directory.CreateDirectory(gui);
        Directory.CreateDirectory(cli);

        var mismatches = new List<string>();
        var examples = ExampleWorkspaces.All(SourceExamplesRoot());
        foreach (var example in examples)
        {
            var g = ExampleWorkspaceInstall.Run(example, gui);
            var c = ExampleWorkspaces.Install(example, cli);
            Assert.Empty(g.Failures);
            Assert.Empty(c.Failures);

            string gDir = Path.Combine(gui, example.Folder), cDir = Path.Combine(cli, example.Folder);
            var gFiles = Files(gDir);
            Assert.Equal(gFiles, Files(cDir));
            foreach (string rel in gFiles)
            {
                byte[] a = File.ReadAllBytes(Path.Combine(gDir, rel)), b = File.ReadAllBytes(Path.Combine(cDir, rel));
                if (a.AsSpan().SequenceEqual(b)) continue;
                mismatches.Add($"{example.Folder}/{rel}");
                output.WriteLine($"── {example.Folder}/{rel}\nGUI:\n{System.Text.Encoding.UTF8.GetString(a)}\nCLI:\n{System.Text.Encoding.UTF8.GetString(b)}");
            }
        }

        Assert.True(examples.Count >= 30, $"only {examples.Count} example(s)");
        Assert.True(mismatches.Count == 0,
            "The GUI's install re-wrote a reference the headless copy did not; store it as the GUI writes it: "
          + string.Join(", ", mismatches));
    }

    /// <summary>The verb as a process: it lands at &lt;dir&gt;/&lt;name&gt;, says where, makes a missing
    /// parent, and refuses — writing nothing — when that folder already exists.</summary>
    [Fact]
    public void NewExample_LandsAtDirSlashName_AndRefusesAnExistingFolder()
    {
        string parent = Path.Combine(_tmp, "made", "here");

        var run = RunCli("new", "example", parent, "S-Parameters", "--json");
        Assert.Equal(0, run.ExitCode);
        string dest = Path.Combine(parent, "S-Parameters");
        var outputs = JsonDocument.Parse(run.StdOut).RootElement.GetProperty("outputs").EnumerateArray()
            .ToDictionary(o => o.GetProperty("kind").GetString()!, o => o.GetProperty("path").GetString()!);
        Assert.Equal(Path.Combine(dest, ".cws"), outputs["cws"]);
        Assert.True(File.Exists(outputs["readme"]));

        string marker = Path.Combine(dest, "README.md");
        File.WriteAllText(marker, "mine");
        var again = RunCli("new", "example", parent, "S-Parameters", "--json");
        Assert.Equal(1, again.ExitCode);
        Assert.Equal("mine", File.ReadAllText(marker));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static List<string> Files(string root)
        => [.. Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                        .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
                        .OrderBy(x => x, StringComparer.Ordinal)];

    private static JsonElement Payload(string stdout)
        => JsonDocument.Parse(stdout).RootElement.GetProperty("result");

    private readonly record struct CliRun(int ExitCode, string StdOut, string StdErr);

    private static CliRun RunCli(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory       = RepoRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        psi.ArgumentList.Add(CliDll());
        foreach (string a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return new CliRun(proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }

    private static string CliDll()
    {
        string cliDir = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(ExamplesCliTests).Assembly)
            .First(a => a.Key == "CliDir").Value!;
        return Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll"));
    }

    private static string RepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is { Length: > 0 } && !File.Exists(Path.Combine(dir, "circuitRF.slnx")))
            dir = Path.GetDirectoryName(dir) ?? "";
        return dir.Length > 0 ? dir : AppContext.BaseDirectory;
    }

    private static string SourceExamplesRoot() => Path.Combine(RepoRoot(), "examples");
}

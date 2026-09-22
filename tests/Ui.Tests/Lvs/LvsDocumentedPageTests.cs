// ================================================================
//  LvsDocumentedPageTests.cs — brief-lvs-15-docs-and-example.md gates 1 and 4.
//
//  "The user page exists, links to the example, and every command in it runs as written. A doc
//   whose commands are stale is worse than no doc — execute them in a test."
//
//  ── WHY IT READS THE PAGE RATHER THAN RESTATING IT ────────────────────────────────────────────
//
//  A test that re-typed the walkthrough would prove that a sequence somebody once wrote still
//  works. It would say nothing about the PAGE, which is the artefact a reader actually runs — and
//  a page and a test that each keep their own copy of a command line drift silently, in the
//  direction where the page is wrong and everything is green.
//
//  So the commands AND the expected transcript come out of `docs/user/src/reference/lvs.md`
//  itself, exactly as `Render/DocumentedWalkthroughTests` reads the CLI chapter. Edit the worked
//  example into something that does not run and this fails; add a command to it and the command is
//  run too, with nothing to remember.
//
//  ── THE ONE DIFFERENCE FROM THE RENDER WALKTHROUGH: A DOCUMENTED FAILURE ──────────────────────
//
//  That chapter's every command exits 0. This one's cannot: the whole point of the example is a
//  board with six faults in it, and `lvs` exits 1 when it finds an error. So the exit code is
//  asserted to be 0 or 1 — never a refusal, a crash or a usage error — and the two headline
//  commands are then pinned to their exact codes by name, because "the broken board exits 1" is
//  the contract a CI script is written against and an assertion that allowed either would not
//  hold it.
//
//  ── AND IT RUNS ON A COPY ─────────────────────────────────────────────────────────────────────
//
//  The reader's own copy of the example comes out of Tools ▸ Examples into a folder they chose, so
//  the commands are relative and the working directory is that folder. The test copies
//  `examples/LVS` into a temp directory and runs there — which also means a verb that wrote
//  something it should not cannot dirty the repository. `lvs` writes nothing without `-o`, and
//  `LvsCliVerbTests` is what proves that; here it is simply the safe way to run.
//
//  NOT tagged Benchmark, measured rather than assumed: four CLI process launches, ~4 s together.
// ================================================================

using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Lvs;

public sealed class LvsDocumentedPageTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "crf-lvs15-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* a temp folder that outlives the run is not a failure */ }
    }

    // ══ gate 1 — every command in the page's worked example runs, and prints what it says ═══════

    [Fact]
    public void TheWorkedExample_RunsAsWritten_AndPrintsWhatTheChapterSaysItDoes()
    {
        string example = Section(File.ReadAllText(ChapterPath()), "lvs-example");
        var    blocks  = CommandBlocks(example);
        Assert.True(blocks.Count >= 3, $"only {blocks.Count} command blocks parsed out of the worked example");

        CopyExample();

        int commandsRun = 0, linesCompared = 0;
        var exits = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var (commands, documented) in blocks)
        {
            string transcript = "";

            foreach (string command in commands)
            {
                Assert.StartsWith("circuitrf lvs ", command, StringComparison.Ordinal);

                var (exit, stdout, stderr) = RunCli(Words(command));
                commandsRun++;
                exits[command] = exit;
                output.WriteLine($"$ {command}  → {exit}\n{stdout}{stderr}");

                // 0 = clean, 1 = findings at or above the severity. Anything else is a refusal, a
                // cancellation or a crash, and the page documents none of those.
                Assert.True(exit is 0 or 1, $"'{command}' exited {exit}\n{stderr}{stdout}");
                transcript = Normalize(stdout + stderr);
            }

            foreach (string line in documented)
            {
                string want = line.TrimEnd();
                if (want.Length == 0 || want.Trim() == "…") continue;

                Assert.True(transcript.Contains(want, StringComparison.Ordinal),
                            $"the page prints a line the command does not:\n  {want}\n\nactual:\n{transcript}");
                linesCompared++;
            }
        }

        // The two headline exit codes, by name. "0 or 1" above is the floor; a CI script is written
        // against these two exactly, and the page's own exit-code section states them.
        Assert.Equal(0, exits["circuitrf lvs Attenuator"]);
        Assert.Equal(1, exits["circuitrf lvs \"Attenuator broken\""]);

        // Neither half vacuous: a transcript that failed to parse, or a block list with no output in
        // it, would satisfy every assertion above.
        Assert.True(commandsRun   >= 4,  $"only {commandsRun} commands were run");
        Assert.True(linesCompared >= 14, $"only {linesCompared} documented lines were compared");
    }

    // ══ gate 4 — every option the verb advertises is written down ═══════════════════════════════
    //
    // Checked against the verb's OWN usage text rather than against a brief, because the code is
    // the contract and a brief is a plan. It is the cheap half of documentation rot and the half
    // nobody notices: a flag added in a later round works, is reachable, and is invisible to
    // everyone who has only read the manual.
    //
    // Both pages have to carry it — the LVS chapter, which is where a designer looks, and the CLI
    // chapter's §lvs, which is where somebody writing a script looks. A flag documented in one and
    // not the other is a flag half the readers cannot find.

    [Fact]
    public void EveryOptionTheVerbAdvertises_IsWrittenDownOnBothPages()
    {
        string page = File.ReadAllText(ChapterPath());
        string cli  = File.ReadAllText(CliChapterPath());

        // With no arguments the verb prints its own usage and exits 1 — a refusal, which is the
        // point: nothing is read, run or written.
        var (_, stdout, stderr) = RunCli("lvs");
        string usage = stdout + stderr;
        output.WriteLine(usage);

        var flags = Regex.Matches(usage, @"(?<![\w-])--[a-z][a-z-]*")
                         .Select(m => m.Value).Distinct().ToList();

        var missing = new List<string>();
        foreach (string flag in flags)
        {
            if (!page.Contains(flag, StringComparison.Ordinal)) missing.Add($"lvs.md {flag}");
            if (!cli .Contains(flag, StringComparison.Ordinal)) missing.Add($"cli.md {flag}");
        }

        Assert.True(missing.Count == 0, "never mentioned: " + string.Join(", ", missing));

        // Not vacuous: a usage string that failed to print would satisfy the assertion above.
        Assert.True(flags.Count >= 7, $"only {flags.Count} options were found in the usage text");

        // And the synopsis the design note publishes is the one the verb prints. `--json` is the
        // one deliberate difference: it is a GLOBAL option, stripped before the verb ever parses,
        // so it is in the documented synopsis and not in the usage line — the same split every
        // other verb has.
        string note = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "design", "cli.md"));
        foreach (string flag in flags)
            Assert.True(note.Contains(flag, StringComparison.Ordinal),
                        $"docs/design/cli.md §19 never mentions {flag}");
    }

    // ══ the page is reachable, and it points at the example rather than restating it ════════════
    //
    // R-lvs15-1b/1c. A documentation page whose examples the reader cannot open is a page nobody
    // finishes, and two copies of the fault list drift — the one in the workspace being the one a
    // reader has open.

    [Fact]
    public void ThePageIsInTheNavigation_AndSendsTheReaderToTheExamplesOwnFaultList()
    {
        string nav = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "user", "src", "_nav.txt"));
        Assert.Contains("reference/lvs.html", nav, StringComparison.Ordinal);

        string page = File.ReadAllText(ChapterPath());
        Assert.Contains("Tools &rsaquo; Examples &rsaquo; Layout versus schematic", page, StringComparison.Ordinal);
        Assert.Contains("README.md", page, StringComparison.Ordinal);

        // The example it names is the one that ships, offered under that title.
        string manifest = File.ReadAllText(Path.Combine(RepoRoot(), "examples", "examples.json"));
        Assert.Contains("\"Title\": \"Layout versus schematic\"", manifest, StringComparison.Ordinal);
        Assert.Contains("\"Folder\": \"LVS\"", manifest, StringComparison.Ordinal);

        // …and the fault list lives THERE, not here. Six rows, each naming its fault.
        string readme = File.ReadAllText(Path.Combine(RepoRoot(), "examples", "LVS", "README.md"));
        foreach (string fault in new[] { "F1", "F2", "F3", "F4", "F5", "F6" })
            Assert.Contains($"**{fault}**", readme, StringComparison.Ordinal);

        // The page must not have grown its own copy of it — R-lvs15-1c, and the cheapest possible
        // check for the thing that actually goes wrong.
        foreach (string fault in new[] { "F1", "F2", "F3", "F5", "F6" })
            Assert.DoesNotContain($"**{fault}**", page, StringComparison.Ordinal);
    }

    // ── reading the page ─────────────────────────────────────────────────────

    private static string ChapterPath()
    {
        string path = Path.Combine(RepoRoot(), "docs", "user", "src", "reference", "lvs.md");
        Assert.True(File.Exists(path), $"the chapter is not there: {path}");
        return path;
    }

    private static string CliChapterPath()
        => Path.Combine(RepoRoot(), "docs", "user", "src", "reference", "cli.md");

    /// <summary>One <c>&lt;h3 id="…"&gt;</c> section, up to the next <c>##</c> heading.</summary>
    private static string Section(string chapter, string id)
    {
        int start = chapter.IndexOf($"<h3 id=\"{id}\">", StringComparison.Ordinal);
        Assert.True(start >= 0, $"the chapter has no section '{id}'");

        int end = chapter.IndexOf("\n## ", start, StringComparison.Ordinal);
        return end < 0 ? chapter[start..] : chapter[start..end];
    }

    /// <summary>
    /// Every <c>&lt;pre&gt;&lt;code class="cmd"&gt;</c> block, as (the commands in it, the
    /// transcript it documents). A <c>$</c> prompt starts a command; what follows in a
    /// <c>&lt;span class="output"&gt;</c> is the transcript.
    /// </summary>
    private static List<(List<string> Commands, string[] Output)> CommandBlocks(string section)
    {
        var blocks = new List<(List<string>, string[])>();

        foreach (System.Text.RegularExpressions.Match block in Regex.Matches(section, "<pre><code class=\"cmd\">(.*?)</code></pre>",
                                              RegexOptions.Singleline))
        {
            string body     = block.Groups[1].Value;
            int    outputAt = body.IndexOf("<span class=\"output\">", StringComparison.Ordinal);

            var commands = new List<string>();
            foreach (string raw in (outputAt < 0 ? body : body[..outputAt]).Split('\n'))
            {
                // The prompt is MARKUP, not text, so reading it from the raw line is what keeps a
                // continuation that happens to begin with a dollar sign from starting a command.
                bool starts = raw.Contains("<span class=\"prompt\">$ </span>", StringComparison.Ordinal);

                string line = Strip(Regex.Replace(raw, "<span class=\"prompt\">.*?</span>", "")).Trim();
                if (line.Length == 0) continue;

                if (starts)                  commands.Add(line);
                else if (commands.Count > 0) commands[^1] = commands[^1].TrimEnd('\\').TrimEnd() + " " + line;
            }

            // A block with no command in it is the report ANATOMY, shown without a prompt. It
            // documents nothing to run, so it is not a walkthrough step.
            if (commands.Count > 0)
                blocks.Add((commands, [.. Strip(outputAt < 0 ? "" : body[outputAt..]).Split('\n')]));
        }

        return blocks;
    }

    /// <summary>The markup off, and the entities back.</summary>
    private static string Strip(string html)
        => WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]*>", "")).Trim('\n').TrimEnd();

    /// <summary>Splits a command line on spaces, honouring the quotes the page needs for the cell
    /// folders whose names have a space in them. The leading <c>circuitrf</c> is dropped: the CLI
    /// assembly IS the executable.</summary>
    private static string[] Words(string command)
        => [.. Regex.Matches(command, @"""([^""]*)""|(\S+)")
                    .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)
                    .Skip(1)];

    private static string Normalize(string s) => s.Replace("\r\n", "\n", StringComparison.Ordinal);

    // ── the example, on a copy, and the CLI as a process inside it ───────────

    private void CopyExample()
    {
        string source = Path.Combine(RepoRoot(), "examples", "LVS");
        foreach (string dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(_root, Path.GetRelativePath(source, dir)));
        Directory.CreateDirectory(_root);
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(_root, Path.GetRelativePath(source, file)), overwrite: true);
    }

    private (int ExitCode, string StdOut, string StdErr) RunCli(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            // The page's paths are RELATIVE, which is the whole shape of it — "from the folder you
            // copied the example into". So the child's working directory is that folder and
            // nothing is rewritten.
            WorkingDirectory       = Directory.Exists(_root) ? _root : RepoRoot(),
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
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }

    private static string CliDll()
    {
        string cliDir = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(LvsDocumentedPageTests).Assembly)
            .First(a => a.Key == "CliDir").Value!;
        return Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll"));
    }

    private static string RepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is { Length: > 0 } && !File.Exists(Path.Combine(dir, "circuitRF.slnx")))
            dir = Path.GetDirectoryName(dir) ?? "";
        return dir;
    }
}

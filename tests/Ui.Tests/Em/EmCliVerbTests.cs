// ================================================================
//  EmCliVerbTests.cs — brief-cli-em-verb.md gate 3, THE acceptance test
//
//  "`circuitrf em` on an existing `.cem` from a real workspace produces a `.sNp` byte-identical to
//  the one the GUI's Simulate writes for the same setup. This is the acceptance test and it is worth
//  more than any unit test in this brief: it proves the move changed no physics."
//
//  So it is written exactly that way and no other: a REAL workspace on disk (a `.cws`, a `.ctech`, a
//  `.clay`, a `.cem`), the REAL `Cli em` verb as a separate process, and a byte comparison against
//  what EmRunService.Run — the call the Simulate button makes — writes for the same setup. Anything
//  weaker (asserting exit code 0, comparing S-parameters to a tolerance) would pass just as happily
//  if the two paths had drifted onto different geometry, a different technology, or a different
//  filename, which are the three failures this whole project could plausibly have introduced.
//
//  NOT tagged Benchmark, measured rather than assumed: 3 tests, 577 ms together. It launches the
//  ALREADY-BUILT `CircuitRF.Cli.dll` instead of `dotnet run --project src/Cli`, so there is no nested
//  build to pay for — see RunCli, where the nested build turned out to be a hang rather than merely a
//  cost. Well under the ~5 s threshold, and it is the acceptance test, so it belongs in the gate
//  everybody runs.
// ================================================================

using System.Diagnostics;
using CircuitRF.Core.Design;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui.Layout;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Em;

public sealed class EmCliVerbTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "crf-emcli-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;

    private const string WriteStampMarker = "circuitRF-EM written:";

    private static string[] WithoutWriteStamp(IEnumerable<string> lines)
        => [.. lines.Where(l => !l.Contains(WriteStampMarker, StringComparison.Ordinal))];

    /// <summary>
    /// Runs `circuitrf &lt;args&gt;` and returns (exit code, stdout, stderr).
    ///
    /// <para><b>It launches the BUILT `CircuitRF.Cli.dll`, never `dotnet run --project src/Cli`.</b>
    /// A nested `dotnet run` starts an MSBuild inside a `dotnet test` that already holds this
    /// repository's build locks, and it does not finish: no CPU anywhere, no child process,
    /// indistinguishable from a slow full-wave solve. <c>Engine.Tests</c> learned this first — see
    /// <c>MatchStampTests.ACnlContainingAMatch_RunsHeadlessUnderCliSparam</c> — and this is its
    /// pattern reused verbatim, down to the <c>CliDir</c> assembly metadata: a
    /// <c>ReferenceOutputAssembly="false"</c> project reference guarantees the CLI is already built,
    /// so there is nothing left to build and the launch is cheap.</para>
    ///
    /// <para><b>Both pipes are drained CONCURRENTLY, and that is not a style choice.</b> Reading
    /// stdout to the end and only then reading stderr deadlocks the moment the child fills stderr's
    /// pipe buffer: the child blocks writing, the parent blocks reading a stdout that will never
    /// close. An `em` run writes its notes AND its per-point progress to stderr (§3.1's split), so it
    /// reaches that buffer easily where the shorter-output CLI tests never do.</para>
    /// </summary>
    private static (int ExitCode, string StdOut, string StdErr) RunCli(string repo, params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory       = repo,
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

    /// <summary>The built CLI, in whichever configuration this test assembly was built in — read from
    /// the <c>CliDir</c> assembly metadata the `.csproj` stamps, so a Release test run cannot silently
    /// exercise a stale Debug CLI.</summary>
    private static string CliDll()
    {
        string cliDir = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(EmCliVerbTests).Assembly)
            .First(a => a.Key == "CliDir").Value!;
        string path = Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll"));
        Assert.True(File.Exists(path),
            $"the CLI was not built beside these tests: {path} — the ReferenceOutputAssembly=\"false\" " +
            "project reference in CircuitRF.Ui.Tests.csproj is what guarantees it, so check that first");
        return path;
    }

    private static string RepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is { Length: > 0 } && !File.Exists(Path.Combine(dir, "circuitRF.slnx")))
            dir = Path.GetDirectoryName(dir) ?? "";
        return dir;
    }

    /// <summary>A workspace laid out the way the GUI lays one out: a <c>.cws</c> at the root naming a
    /// default technology, and a cell folder holding the <c>.clay</c>. The <c>.cem</c> sits beside the
    /// <c>.cws</c> and names its layout WORKSPACE-relative, which is the reference form R-emcli-5 is
    /// about — a `.cem` that named an absolute path would resolve without exercising the walk-up at
    /// all, and would prove nothing.</summary>
    private (string CemPath, EmSetup Setup) BuildWorkspace()
    {
        string cellLayoutDir = Path.Combine(_root, "Line", "layout");
        Directory.CreateDirectory(cellLayoutDir);

        string techPath = Path.Combine(_root, "pcb.ctech");
        TechPersistence.SaveToFile(techPath, StarterTechnologies.Pcb2Layer());

        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.Add(new RectShape
        { Layer = new(1, 0), X1 = 0, Y1 = 0, X2 = 20_000_000, Y2 = 2_900_000 });
        string clayPath = Path.Combine(cellLayoutDir, "Line.clay");
        LayoutPersistence.SaveToFile(clayPath, view);

        // TechRef is deliberately left null on the .clay — that is the NORMAL case (a layout only
        // stores one when it deviates), so this is also the path that has to read DefaultTechRef out
        // of the .cws. A layout carrying its own TechRef would never touch the workspace at all.
        WorkspacePersistence.SaveToFile(
            Path.Combine(_root, ".cws"), new CwsFile { DefaultTechRef = "pcb.ctech" });

        var setup = new EmSetup
        {
            Name      = "line",
            LayoutRef = Path.Combine("Line", "layout", "Line.clay"),
            Frequency = new FrequencySpec("1", "10", 3, SweepKind.Linear, "GHz", "GHz"),
        };

        string cemPath = Path.Combine(_root, "line.cem");
        EmSetupPersistence.SaveToFile(cemPath, setup);
        return (cemPath, setup);
    }

    [Fact]
    public void CircuitrfEm_WritesTheSameSnpTheSimulateButtonWrites_ByteForByte()
    {
        string repo = RepoRoot();
        Assert.False(string.IsNullOrEmpty(repo), "could not locate the repository root");

        var (cemPath, setup) = BuildWorkspace();
        string resultsRoot = Path.Combine(_root, "results");

        // ── the GUI's own path ────────────────────────────────────────────────────────────────
        // EmSetupResolver.Resolve + EmRunService.Run is literally what RunEmSetupAsync does once the
        // dispatcher work is stripped out, so this IS the Simulate answer.
        var resolved = EmSetupResolver.Resolve(
            cemPath, setup.LayoutRef, Path.Combine(_root, ".cws"), new TechnologyCache());
        Assert.NotNull(resolved.Source);
        Assert.NotNull(resolved.Source!.Technology);

        var gui = EmRunService.Run(setup, resolved.Source, resultsRoot);
        Assert.Equal(EmRunStatus.Ok, gui.Status);
        Assert.NotNull(gui.SnpPath);

        string[] fromSimulate = File.ReadAllLines(gui.SnpPath!);
        string guiSnpPath     = gui.SnpPath!;

        // Move it aside rather than deleting: if the CLI writes nothing at all, the assertion below
        // must fail on a missing file rather than quietly pass on the one already sitting there.
        string stashed = guiSnpPath + ".simulate";
        File.Move(guiSnpPath, stashed);

        // ── the headless path ─────────────────────────────────────────────────────────────────
        var (exitCode, stdout, stderr) = RunCli(repo, "em", cemPath);

        output.WriteLine("stdout:\n" + stdout);
        output.WriteLine("stderr:\n" + (stderr.Length > 4000 ? stderr[..4000] + " …" : stderr));

        Assert.Equal(0, exitCode);

        // R-emcli-7 — the same PATH, not merely the same content. A headless run that minted its own
        // filename would orphan every schematic SnP reference, and would still pass a bytes-only test.
        Assert.True(File.Exists(guiSnpPath),
            $"`circuitrf em` did not write '{guiSnpPath}'. stdout:\n{stdout}\nstderr:\n{stderr}");

        // "Byte-identical" MODULO ONE LINE, and the exception is in the file by design:
        // EmSnpProvenance stamps the wall-clock time the `.sNp` was written, so two runs a second
        // apart can never produce identical bytes and no amount of correctness would make them.
        // Every other line must match exactly — INCLUDING the three provenance hashes, which is the
        // strongest part of this assertion: geometry, mesh and ports each hash to the same value, so
        // both paths demonstrably resolved the same layout, the same stackup and the same ports.
        Assert.Equal(WithoutWriteStamp(fromSimulate), WithoutWriteStamp(File.ReadAllLines(guiSnpPath)));

        // …and the stamp line must still BE there, or the comparison above would be quietly ignoring
        // a header that had gone missing.
        Assert.Contains(File.ReadAllLines(guiSnpPath), l => l.Contains(WriteStampMarker, StringComparison.Ordinal));

        // R-emcli-6 — the summary is on stdout, where a pipe can take it; the run's own notes are not.
        Assert.Contains("Wrote ", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("note:", stdout, StringComparison.Ordinal);

        File.Delete(stashed);
    }

    [Trait("Category", "Benchmark")]
    [Fact]
    public void MinusO_MovesTheTouchstoneAndNothingElse()
    {
        string repo = RepoRoot();
        var (cemPath, setup) = BuildWorkspace();

        string chosen = Path.Combine(_root, "elsewhere", "line.s2p");
        Directory.CreateDirectory(Path.GetDirectoryName(chosen)!);

        var (exitCode, stdout, stderr) = RunCli(repo, "em", cemPath, "-o", chosen);

        output.WriteLine("stdout:\n" + stdout);
        output.WriteLine("stderr:\n" + (stderr.Length > 4000 ? stderr[..4000] + " …" : stderr));

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(chosen), $"-o did not write '{chosen}'. stderr:\n{stderr}");

        // "-o overrides the DESTINATION only": the `.npy` still lands under results/ with its own
        // `_em`-suffixed key, because it carries the diagnostics group the Touchstone cannot.
        string npy = Path.Combine(_root, "results", EmRunService.ResolveNpyKey(setup) + ".npy");
        Assert.True(File.Exists(npy), $"the .npy moved with -o; it should not have. Expected '{npy}'.");
    }

    [Fact]
    public void ARefusalStaysARefusal_AndExplainsItself()
    {
        string repo = RepoRoot();
        Directory.CreateDirectory(_root);

        // A `.cem` naming a layout that is not there: R-emcli-8's NoLayout, which must come back as a
        // written sentence and a non-zero exit rather than as "EM failed".
        string cemPath = Path.Combine(_root, "orphan.cem");
        EmSetupPersistence.SaveToFile(cemPath, new EmSetup { Name = "orphan", LayoutRef = "nope.clay" });

        var (exitCode, stdout, stderr) = RunCli(repo, "em", cemPath);

        output.WriteLine("stdout:\n" + stdout);
        output.WriteLine("stderr:\n" + stderr);

        Assert.Equal(1, exitCode);
        Assert.Contains("No layout", stderr, StringComparison.Ordinal);
        // The run service's own sentence, not a re-wording of it.
        Assert.Contains("nope.clay", stderr, StringComparison.Ordinal);
    }

    // ── --json (brief-automation-1-structured-output.md §6.2) ─────────────────
    //
    // `em`'s share of the structured-output gates lives here rather than beside the other verbs'
    // because the workspace this file already knows how to build is what the verb needs, and a
    // second copy of BuildWorkspace would be a second thing to keep in step.

    /// <summary>
    /// Both files, and both kinds. <c>cli.md</c> §8.2: the <c>.sNp</c> and the <c>_em.npy</c> are not
    /// redundant — the Touchstone is the network, the <c>.npy</c> carries the diagnostics group that
    /// makes a wrong answer diagnosable — so a document listing only one of them would be telling a
    /// caller the run produced half of what it produced.
    /// </summary>
    [Fact]
    public void CircuitrfEm_Json_ListsBothFilesAndCarriesTheResult()
    {
        string repo = RepoRoot();
        var (cemPath, _) = BuildWorkspace();

        var (exitCode, stdout, stderr) = RunCli(repo, "em", cemPath, "--json");
        output.WriteLine("stderr:\n" + (stderr.Length > 4000 ? stderr[..4000] + " …" : stderr));

        Assert.Equal(0, exitCode);

        var doc = System.Text.Json.JsonDocument.Parse(stdout).RootElement;
        Assert.Equal("ok", doc.GetProperty("status").GetString());
        Assert.Equal("em", doc.GetProperty("circuitrf").GetProperty("verb").GetString());

        var outputs = doc.GetProperty("outputs").EnumerateArray()
            .Select(o => (Kind: o.GetProperty("kind").GetString(), Path: o.GetProperty("path").GetString()))
            .ToArray();
        Assert.Contains(outputs, o => o.Kind == "touchstone" && File.Exists(o.Path!));
        Assert.Contains(outputs, o => o.Kind == "npy" && File.Exists(o.Path!));

        // An EM DataSet carries S alongside a diagnostics group, and both must survive into the
        // document — the diagnostics group is the half that explains the other half.
        var groups = doc.GetProperty("result").GetProperty("groups");
        Assert.Contains("S", groups.GetProperty("").EnumerateObject().Select(p => p.Name));
        Assert.True(groups.EnumerateObject().Count() > 1, "the diagnostics group did not reach the document");

        // §6.2.2 — the document and nothing else. `em` is the chattiest verb there is, and every one
        // of those lines belongs on stderr.
        Assert.Equal('{', stdout.TrimStart()[0]);
        Assert.DoesNotContain("Wrote ", stdout, StringComparison.Ordinal);
    }

    /// <summary>
    /// A refusal is the payload. R-aut1-3 — a failed run still emits a document — and R-aut1-6: the
    /// refusal arrives with the id <c>EmRunService</c> has carried alongside the string since
    /// brief-localization-groundwork's R-loc-5, which the CLI used to discard.
    /// </summary>
    [Fact]
    public void CircuitrfEm_Json_CarriesTheRefusalAsAStructuredDiagnostic()
    {
        string repo = RepoRoot();
        Directory.CreateDirectory(_root);

        string cemPath = Path.Combine(_root, "orphan.cem");
        EmSetupPersistence.SaveToFile(cemPath, new EmSetup { Name = "orphan", LayoutRef = "nope.clay" });

        var (exitCode, stdout, stderr) = RunCli(repo, "em", cemPath, "--json");
        output.WriteLine("stderr:\n" + stderr);

        Assert.Equal(1, exitCode);

        var doc = System.Text.Json.JsonDocument.Parse(stdout).RootElement;
        Assert.Equal("failed", doc.GetProperty("status").GetString());
        Assert.Equal(1, doc.GetProperty("exitCode").GetInt32());

        var refusal = doc.GetProperty("diagnostics").EnumerateArray()
            .Single(d => d.GetProperty("id").GetString() == "em.layout.not-found");
        Assert.Equal("error", refusal.GetProperty("severity").GetString());
        // The typed half: the layout reference, read rather than parsed back out of the sentence.
        Assert.Equal("nope.clay", refusal.GetProperty("arguments").GetProperty("layoutRef").GetString());
        // …and stderr still says exactly what it said before, which is the contract cli.md §8 keeps.
        Assert.Contains("nope.clay", stderr, StringComparison.Ordinal);
    }

    /// <summary>
    /// CAP-1 follow-up — `circuitrf em` (and the MCP `run analysis=em`, which runs it) reaches the planar size
    /// check through <c>EmRunService</c>: with <c>--memory-limit</c> standing for the machine-wide limit, a run
    /// past it is not refused at the mesh but asked about, and headless, with no <c>--force</c> to say yes, it is
    /// declined — the panel's Simulate Anyway is <c>--force</c> here. Nothing is filled.
    /// </summary>
    [Fact]
    public void CircuitrfEm_PastTheMachinesMemoryLimit_IsDeclinedWithoutForce()
    {
        string repo = RepoRoot();
        // A copy of the committed workspace, so the fixture is untouched and nothing lands in testdata.
        // Its setup runs accelerated, which this tiny limit leaves at the shipped 12,000-unknown floor, so the
        // copy runs dense, whose size the limit is checked against.
        string src = Path.Combine(repo, "testdata", "portcal");
        foreach (string file in new[] { ".cws" }
                     .Concat(Directory.GetFiles(Path.Combine(src, "tech")).Select(f => Path.Combine("tech", Path.GetFileName(f))))
                     .Concat(Directory.GetFiles(Path.Combine(src, "separated-pair"), "*", SearchOption.AllDirectories)
                                      .Select(f => Path.GetRelativePath(src, f))))
        {
            string to = Path.Combine(_root, file);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(Path.Combine(src, file), to);
        }
        string cem = Path.Combine(_root, "separated-pair", "em", "separated-pair.cem");
        var setup = EmSetupPersistence.LoadFromFile(cem);
        setup.AcceleratedSolve = false;
        EmSetupPersistence.SaveToFile(cem, setup);

        var (exitCode, stdout, stderr) = RunCli(repo, "em", cem, "--memory-limit", "0.0001", "--json");
        output.WriteLine("stderr:\n" + (stderr.Length > 4000 ? stderr[..4000] + " …" : stderr));

        Assert.Equal(1, exitCode);
        var doc = System.Text.Json.JsonDocument.Parse(stdout).RootElement;
        var refusal = doc.GetProperty("diagnostics").EnumerateArray()
            .Single(d => d.GetProperty("id").GetString() == "em.refused.memory");
        Assert.Contains("--force", refusal.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>em --component</c> is a planar run too, and reaches the same size check: <c>--memory-limit</c> is the
    /// memory it is sized on, so a run past it is declined headless — and the refusal's own <c>--force</c> is a
    /// flag this spelling honours. Until the flags were handed through, a component run was sized on this
    /// machine's physical memory whatever was passed, and a refusal naming <c>--force</c> named a flag it ignored.
    /// </summary>
    [Fact]
    public void CircuitrfEmComponent_IsSizedOnTheMemoryLimit_AndForceStartsIt()
    {
        string repo = RepoRoot();
        Directory.CreateDirectory(_root);
        string ws = WorkspaceCreate.Create(_root, "mmic-GaAs_2LM_100um", "mmic-GaAs_2LM_100um").WorkspaceDir;
        string snp = Path.Combine(_root, "c1.s2p");
        string[] run = ["em", ws, "--component", "MLIN W=70 um L=6000 um", "--freq", "20GHz:20GHz:1GHz", "-o", snp,
                        "--memory-limit", "0.0001", "--json"];

        var (declined, stdout, stderr) = RunCli(repo, run);
        output.WriteLine("stderr:\n" + (stderr.Length > 4000 ? stderr[..4000] + " …" : stderr));
        Assert.Equal(1, declined);
        var refusal = System.Text.Json.JsonDocument.Parse(stdout).RootElement.GetProperty("diagnostics")
            .EnumerateArray().Single(d => d.GetProperty("id").GetString() == "em.refused.memory");
        Assert.Contains("--force", refusal.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.False(File.Exists(snp));

        var (forced, _, forcedErr) = RunCli(repo, [.. run, "--force"]);
        output.WriteLine("forced stderr:\n" + (forcedErr.Length > 4000 ? forcedErr[..4000] + " …" : forcedErr));
        Assert.Equal(0, forced);
        Assert.True(File.Exists(snp));
    }
}

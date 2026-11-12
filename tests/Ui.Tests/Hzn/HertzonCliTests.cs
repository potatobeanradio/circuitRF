using System.Diagnostics;
using CircuitRF.Design.Interchange.Hzn;

namespace CircuitRF.Ui.Tests.Hzn;

/// <summary>Runs against the stub hertzon-cli in <c>Hzn/Stub</c> (a shell script), so it skips where there is no <c>sh</c>.</summary>
public sealed class StubFactAttribute : FactAttribute
{
    public StubFactAttribute()
    {
        if (OperatingSystem.IsWindows()) Skip = "The stub hertzon-cli is a shell script; there is no sh here.";
    }
}

/// <summary>The theory form of <see cref="StubFactAttribute"/>.</summary>
public sealed class StubTheoryAttribute : TheoryAttribute
{
    public StubTheoryAttribute()
    {
        if (OperatingSystem.IsWindows()) Skip = "The stub hertzon-cli is a shell script; there is no sh here.";
    }
}

/// <summary>Runs against an installed Hertzon named by <c>CIRCUITRF_HERTZON_HOME</c>; reported Skipped with the reason without one.</summary>
public sealed class HertzonInstalledFactAttribute : FactAttribute
{
    public HertzonInstalledFactAttribute()
    {
        string? home = Environment.GetEnvironmentVariable(HertzonDiscovery.HomeVariable);
        if (string.IsNullOrWhiteSpace(home) || !Directory.Exists(home))
            Skip = $"{HertzonDiscovery.HomeVariable} is not set to a Hertzon installation folder; point it at one to run a real solve.";
        else if (!new HertzonDiscovery().InsideHome(home).Any(File.Exists))
            Skip = $"{home} holds no hertzon-cli.";
    }
}

/// <summary>
/// HZN-8 — finding hertzon-cli, the schema gate, running a design and reading the Touchstone back. Everything but the last test
/// runs against <c>Hzn/Stub/hertzon-cli</c>, which prints recorded outputs and exit codes: discovery, the schema gate, every
/// progress event, every exit code, cancel, and reading the file. The last runs a real Hertzon when one is installed.
/// </summary>
public sealed class HertzonCliTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-hzn8-" + Guid.NewGuid().ToString("N")[..10]);

    public HertzonCliTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>A copy of the stub in its own folder (it keeps its call log and schema file beside it).</summary>
    private string Stub(int schema = 1, string name = "stub")
    {
        string dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        string src = FixturePaths.Require("tests/Ui.Tests/Hzn/Stub");
        foreach (string f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dir, Path.GetFileName(f)), true);
        string exe = Path.Combine(dir, "hertzon-cli");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        if (schema != 1) File.WriteAllText(Path.Combine(dir, "schema"), schema.ToString());
        return exe;
    }

    private static HertzonDiscovery Discovery(Func<string?>? preferred = null, Func<string, string?>? env = null, string[]? dirs = null) => new()
    {
        PreferredCommand = preferred,
        ReadEnvironment = env ?? (_ => null),
        SearchDirectories = dirs ?? [],
    };

    private HertzonInstallation Ready(string exe)
    {
        var ready = Discovery(() => exe).Check();
        Assert.True(ready.Ready, ready.Refusal);
        return ready.Installation!;
    }

    private string Project()
    {
        string path = Path.Combine(_root, "stub.hzn");
        File.WriteAllText(path, "{}");
        return path;
    }

    // ── discovery ────────────────────────────────────────────────────────────────────────────

    [StubFact]
    public void Discovery_TakesSettingsThenEnvironmentThenPathThenTheInstallFolders_AndAsksTheVersionOncePerProgram()
    {
        string settings = Stub(name: "a"), named = Stub(name: "b"), onPath = Stub(name: "c"), folder = Stub(name: "d");
        string Env(string k) => k switch
        {
            HertzonDiscovery.EnvironmentVariable => named,
            "PATH" => Path.GetDirectoryName(onPath)!,
            _ => "",
        };
        var all = Discovery(() => settings, Env, [Path.GetDirectoryName(folder)!]);
        var first = all.Check();
        Assert.Equal(HertzonHowFound.Settings, first.Installation!.HowFound);
        Assert.Equal(settings, first.Installation.Path);
        Assert.Equal("9.9.9", first.Installation.Version);

        all.Check();
        all.Check();
        Assert.Equal(1, all.ProbesStarted);                  // asked once, kept for the session

        Assert.Equal(HertzonHowFound.Environment, Discovery(null, Env, [Path.GetDirectoryName(folder)!]).Check().Installation!.HowFound);
        Assert.Equal(HertzonHowFound.Path, Discovery(null, k => k == "PATH" ? Env(k) : null, [Path.GetDirectoryName(folder)!]).Check().Installation!.HowFound);
        var last = Discovery(null, _ => null, [Path.GetDirectoryName(folder)!]).Check();
        Assert.Equal(HertzonHowFound.DefaultDirectory, last.Installation!.HowFound);
        Assert.Equal(folder, last.Installation.Path);
    }

    [StubFact]
    public void ANamedProgramThatDoesNotWork_IsReported_AndNeverReplacedByOneFoundElsewhere()
    {
        string working = Stub(name: "w");
        string broken = Path.Combine(_root, "broken", "hertzon-cli");
        Directory.CreateDirectory(Path.GetDirectoryName(broken)!);
        File.WriteAllText(broken, "#!/bin/sh\nexit 7\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(broken, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var ready = Discovery(() => broken, k => k == "PATH" ? Path.GetDirectoryName(working) : null).Check();
        Assert.False(ready.Ready);
        Assert.Null(ready.Installation);
        Assert.Contains("exit code 7", ready.Refusal);
        Assert.Contains(broken, ready.Refusal);

        var none = Discovery().Check();
        Assert.False(none.Ready);
        Assert.Contains("hertzon-cli was not found", none.Refusal);
    }

    [StubFact]
    public void AnotherCommandLineSchema_TurnsRunOff_WithASentenceNamingBothVersions()
    {
        var ready = Discovery(() => Stub(schema: 2)).Check();
        Assert.False(ready.Ready);
        Assert.NotNull(ready.Installation);                       // it is found, and said to be unusable
        Assert.Contains("schema 2", ready.Refusal);
        Assert.Contains("schema 1", ready.Refusal);
        Assert.Contains("9.9.9", ready.Refusal);
    }

    // ── describe ─────────────────────────────────────────────────────────────────────────────

    [StubFact]
    public void Describe_ListsTheDesignsSetupsSweepsPortsAndVariables_AndRowsCountEveryMode()
    {
        var d = HertzonRun.Describe(Ready(Stub()), Project());
        Assert.True(d.Ok, d.Error);
        var ok = Assert.Single(d.Designs, x => x.Name == "Ok");
        Assert.True(ok.IsThreeD);
        Assert.Equal("Sweep", Assert.Single(Assert.Single(ok.Setups).Sweeps).Name);
        Assert.Equal(["w", "$eps"], ok.Variables.Select(v => v.Name));
        Assert.Equal("project", ok.Variables[1].Scope);
        Assert.False(d.Designs.Single(x => x.Name == "Sch").IsThreeD);

        var wave = HertzonRun.Parse("1", """{"designs":[{"name":"W","kind":"3d","ports":[{"number":1,"name":"1","kind":"Wave Port","modes":2,"z0_ohm":null},{"number":2,"name":"2","kind":"Lumped Port","modes":1,"z0_ohm":50}]}]}""");
        Assert.Equal(3, wave.Designs[0].Rows);
    }

    // ── solve ────────────────────────────────────────────────────────────────────────────────

    private HertzonRunRequest Request(string design, string? sweep = null, params (string, string)[] sets)
        => new(Project(), design, "Setup1", sweep, [.. sets.Select(s => new KeyValuePair<string, string>(s.Item1, s.Item2))],
               Path.Combine(_root, "out", design + ".s2p"));

    [StubFact]
    public void Solve_PassesTheDocumentedArguments_StreamsEveryEventKind_AndReadsTheTouchstoneBack()
    {
        string exe = Stub();
        var events = new List<HertzonEvent>();
        var request = Request("Ok", "Sweep", ("w", "2.9mm"), ("$eps", "4"));
        var result = HertzonRun.Solve(Ready(exe), request, events.Add);

        Assert.Equal(HertzonOutcome.Done, result.Outcome);
        Assert.True(result.HasResult);
        Assert.Null(result.Sentence);
        Assert.Equal(request.Output, result.TouchstonePath);
        Assert.Equal((true, 2, 8676L, 4.5), (result.Converged, result.Passes, result.Tetrahedra, result.Seconds));
        Assert.Equal("Setup1", result.Setup);

        string call = File.ReadAllLines(Path.Combine(Path.GetDirectoryName(exe)!, "calls.txt")).Single(l => l.StartsWith("solve"));
        Assert.Contains($"--design Ok --setup Setup1 --sweep Sweep --set w=2.9mm --set $eps=4 -o {request.Output} --progress json --json", call);

        Assert.Contains(events, e => e is { Kind: HertzonEventKind.Progress, Percent: 12.0 } && e.Message == "Adaptive pass 1 of max 6");
        Assert.Contains(events, e => e is { Kind: HertzonEventKind.Pass, Pass: 2, Tetrahedra: 8676, DeltaS: 0.0031 });
        Assert.Contains(events, e => e is { Kind: HertzonEventKind.Pass, Pass: 1, DeltaS: null });
        Assert.Contains(events, e => e is { Kind: HertzonEventKind.Log, Level: "warning" });
        Assert.Contains(events, e => e is { Kind: HertzonEventKind.Log, Level: "info" });
        Assert.Contains(events, e => e is { Kind: HertzonEventKind.Progress, Percent: 100 });

        var data = HertzonResults.Build(result.TouchstonePath!, result);
        Assert.Equal(2, data["S"].Axes[0].Values.Length);
        Assert.Equal(0.8, data["S"].ComplexValues[1 * 4 + 1].Real, 12);   // [freq 2, S12]
        Assert.Equal(2.0, data.CubesIn(HertzonResults.Group)["Passes"].RealValues[0]);
    }

    [StubTheory]
    [InlineData("Fail1", HertzonOutcome.SolverFailed, 1, "solver.log", "degenerate tetrahedron", false)]
    [InlineData("Bad3", HertzonOutcome.BadInput, 3, "rejected the input", "No variable 'zz'", false)]
    [InlineData("Check4", HertzonOutcome.ModelCheckFailed, 4, "model check failed", "Port 2 touches no face", false)]
    [InlineData("NoConv5", HertzonOutcome.NotConverged, 5, "did not converge", "", true)]
    public void EveryExitCode_BecomesItsOwnSentence_AndANotConvergedRunStillHasItsTouchstone(
        string design, HertzonOutcome outcome, int code, string sentence, string quoted, bool hasResult)
    {
        var result = HertzonRun.Solve(Ready(Stub()), Request(design));
        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(code, result.ExitCode);
        Assert.Contains(sentence, result.Sentence);
        Assert.Contains(quoted, result.Sentence);
        Assert.Equal(hasResult, result.HasResult);
        if (hasResult) Assert.Equal(false, result.Converged);
    }

    [StubFact]
    public void Cancel_InterruptsHertzon_SoItExits130_AndNoResultIsRead()
    {
        using var cts = new CancellationTokenSource();
        var started = new ManualResetEventSlim();
        var sw = Stopwatch.StartNew();
        var task = Task.Run(() => HertzonRun.Solve(Ready(Stub()), Request("Slow"), e => { if (e.Percent == 5.0) started.Set(); }, cts.Token));
        Assert.True(started.Wait(TimeSpan.FromSeconds(20)), "the stub never reported progress");
        cts.Cancel();
        var result = task.GetAwaiter().GetResult();
        Assert.Equal(HertzonOutcome.Cancelled, result.Outcome);
        Assert.Equal(130, result.ExitCode);
        Assert.False(result.HasResult);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30));
    }

    // ── em --solver hertzon ──────────────────────────────────────────────────────────────────

    [StubFact]
    public void TheEmVerb_RunsTheSameCall_AndReportsHertzonsSummaryFieldsInItsJson()
    {
        string exe = Stub();
        string project = Project();
        string touchstone = Path.Combine(_root, "headless.s2p");
        var (code, stdout, stderr) = RunCli(exe, "em", project, "--design", "Ok", "--solver", "hertzon", "--set", "w=2.9mm", "-o", touchstone, "--json");
        Assert.True(code == 0, stderr + stdout);
        Assert.True(File.Exists(touchstone));
        using var doc = System.Text.Json.JsonDocument.Parse(stdout);
        var h = doc.RootElement.GetProperty("result").GetProperty("hertzon");
        Assert.Equal("9.9.9", h.GetProperty("hertzonVersion").GetString());
        Assert.Equal(1, h.GetProperty("cliSchema").GetInt32());
        Assert.True(h.GetProperty("converged").GetBoolean());
        Assert.Equal(2, h.GetProperty("passes").GetInt32());
        Assert.Equal(8676, h.GetProperty("tetrahedra").GetInt64());
        Assert.Equal(4.5, h.GetProperty("seconds").GetDouble());
        Assert.Equal("2.9mm", h.GetProperty("variables").GetProperty("w").GetString());
        Assert.Contains(doc.RootElement.GetProperty("outputs").EnumerateArray(), o => o.GetProperty("kind").GetString() == "touchstone");

        // A schema Hertzon speaks differently is a refusal naming both, before anything is run.
        var (refused, _, refusedErr) = RunCli(Stub(schema: 2, name: "s2"), "em", project, "--design", "Ok", "--solver", "hertzon");
        Assert.Equal(1, refused);
        Assert.Contains("schema 2", refusedErr);

        // A run that did not converge exits 1 and still wrote its file.
        string noconv = Path.Combine(_root, "noconv.s2p");
        var (nc, _, ncErr) = RunCli(exe, "em", project, "--design", "NoConv5", "--solver", "hertzon", "-o", noconv);
        Assert.Equal(1, nc);
        Assert.True(File.Exists(noconv));
        Assert.Contains("did not converge", ncErr);
    }

    private static (int ExitCode, string StdOut, string StdErr) RunCli(string hertzon, params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = AppContext.BaseDirectory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        psi.Environment[HertzonDiscovery.EnvironmentVariable] = hertzon;
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
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(HertzonCliTests).Assembly)
            .First(a => a.Key == "CliDir").Value!;
        string path = Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll"));
        Assert.True(File.Exists(path), $"the CLI was not built beside these tests: {path}");
        return path;
    }

    // ── the real thing ───────────────────────────────────────────────────────────────────────

    [HertzonInstalledFact]
    public void AnInstalledHertzon_SolvesAShippedExampleAtItsAdaptiveFrequency_AndItsTouchstoneReadsBack()
    {
        string home = Environment.GetEnvironmentVariable(HertzonDiscovery.HomeVariable)!;
        string example = Path.Combine(home, "examples", "Coax.hzn");
        if (!File.Exists(example)) return;                         // an installation without its examples has nothing to run
        string project = Path.Combine(_root, "Coax.hzn");
        File.Copy(example, project);                                // Hertzon keeps its solution beside the project it solves

        var ready = new HertzonDiscovery { ReadEnvironment = Environment.GetEnvironmentVariable, SearchDirectories = [] }.Check();
        Assert.True(ready.Ready, ready.Refusal);
        var cli = ready.Installation!;
        var described = HertzonRun.Describe(cli, project);
        Assert.True(described.Ok, described.Error);
        var design = Assert.Single(described.Designs, d => d.IsThreeD);

        var request = new HertzonRunRequest(project, design.Name, null, "LastAdaptive", [], Path.Combine(_root, "real.s" + design.Rows + "p"));
        var events = new List<HertzonEvent>();
        var result = HertzonRun.Solve(cli, request, events.Add);
        Assert.True(result.HasResult, result.Sentence);
        Assert.Equal(HertzonOutcome.Done, result.Outcome);
        Assert.Contains(events, e => e.Kind == HertzonEventKind.Pass);
        var data = HertzonResults.Build(result.TouchstonePath!, result);
        Assert.Equal(design.Rows, data["S"].Axes[1].Values.Length);
        Assert.False(Directory.Exists(Path.Combine(_root, "results")), "circuitRF wrote nothing but the file -o named");
    }
}

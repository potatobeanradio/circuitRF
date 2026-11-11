using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CircuitRF.Cli;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Plugins;
using CircuitRF.Design.Revision;
using CircuitRF.Ui.Tests.Layout.PCells;
using CircuitRF.Ui.Tests.Revision;
using Xunit;

namespace CircuitRF.Ui.Tests.Plugins;

/// <summary>
/// brief-plg-4 gates 1–6: a plugin's host calls are circuitRF's own verbs and models — <c>simulate</c> writes what
/// <c>circuitRF sparam</c> writes, <c>lineModel</c> is the elaborated part — every refusal leaves the session alive, a
/// cancelled request stops its run, and a <c>window</c> plugin places a cell and runs once at a time.
/// </summary>
[Collection(AppDataRootCollection.Name)]
public class PluginHostServicesTests
{
    private static string RepoRoot()
    {
        for (string? d = AppContext.BaseDirectory; d is not null; d = Path.GetDirectoryName(d))
            if (File.Exists(Path.Combine(d, "VERSION")) && Directory.Exists(Path.Combine(d, "src"))) return d;
        throw new DirectoryNotFoundException("the repository root");
    }

    private static string CopyOfPatchAntenna(string scratch)
    {
        string from = Path.Combine(RepoRoot(), "examples", "Patch Antenna"), ws = Path.Combine(scratch, "ws");
        foreach (string f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            string dest = Path.Combine(ws, Path.GetRelativePath(from, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(f, dest);
        }
        return ws;
    }

    /// <summary>The host example, started with circuitRF's verbs: each update sends the host call its <c>call</c> holds.</summary>
    private static async Task<PluginSession> HostExample(string scratch, string? workspace = null)
    {
        var entry = PluginTestKit.Example("host");
        var started = PluginSession.Start(entry, PluginTestKit.Allowed(entry, scratch) with { WorkspaceDir = workspace, Verbs = PluginVerbs.Instance });
        Assert.True(started.Ok, started.Failure?.Sentence);
        Assert.True((await started.Value!.HelloAsync()).Ok);
        return started.Value;
    }

    /// <summary>Sends <paramref name="call"/> inside an update and returns the hostReply the plugin received.</summary>
    private static async Task<JsonObject> Call(PluginSession session, JsonObject call)
    {
        var reply = await session.UpdateAsync(new JsonObject { ["call"] = call.ToJsonString() });
        Assert.True(reply.Ok, reply.Failure?.Sentence);
        return JsonNode.Parse((string)reply.Value!.Readouts["answer"]!)!.AsObject();
    }

    // ── gate 1: simulate is `circuitRF sparam` ─────────────────────────────────

    [PythonFact]
    public async Task Gate1_TheStubsSimulatedTouchstone_IsByteIdenticalToTheCliRunOnTheSameFile()
    {
        string scratch = PluginTestKit.NewScratch();
        string ws = CopyOfPatchAntenna(scratch);
        var entry = PluginTestKit.Example("stub");
        var started = PluginSession.Start(entry, PluginTestKit.Allowed(entry, scratch) with { WorkspaceDir = ws, Verbs = PluginVerbs.Instance });
        Assert.True(started.Ok, started.Failure?.Sentence);
        await using var session = started.Value!;
        Assert.True((await session.HelloAsync()).Ok);
        var texts = entry.Manifest!.Params.Where(p => p.Kind != PluginParamKind.Readout)
            .ToDictionary(p => p.Name, p => PluginParams.StartingText(p, null, ws));
        var (values, errors) = PluginParams.Resolve(entry.Manifest.Params, texts, new PluginParamContext(ws));
        Assert.Empty(errors);

        var reply = await session.ActionAsync("simulate", values!);
        Assert.True(reply.Ok, reply.Failure?.Sentence);
        Assert.DoesNotContain(reply.Value!.Messages, m => m.IsError);
        Assert.Contains(reply.Value.Messages, m => m.Text.StartsWith("circuitRF simulated the circuit"));
        // Drawn beside the closed form's trace, from circuitRF's own result.
        Assert.Contains("S11_circuitRF", reply.Value.Views["response"]!["data"]!["cubes"]!.AsObject().Select(c => c.Key));

        string staged = Path.Combine(session.StagingDirectory, "circuit.s1p");
        string cli = Path.Combine(scratch, "cli.s1p");
        var (exit, stderr) = RunCli("sparam", Path.Combine(session.StagingDirectory, "circuit.cnl"), "-o", cli, "--workspace", ws);
        Assert.True(exit == 0, stderr);
        Assert.Equal(File.ReadAllBytes(cli), File.ReadAllBytes(staged));
        Assert.True(File.Exists(Path.Combine(session.StagingDirectory, "circuit.SP1.npy")));
    }

    // ── gate 2: lineModel is the part ──────────────────────────────────────────

    [PythonFact]
    public async Task Gate2_LineModel_IsTheElaboratedMicrostripTheLineCalculatorReports()
    {
        string scratch = PluginTestKit.NewScratch();
        string ws = CopyOfPatchAntenna(scratch);
        await using var session = await HostExample(scratch, ws);
        string substrate = PluginParams.SubstrateChoices(ws)[0];
        int sep = substrate.IndexOf(" : ", StringComparison.Ordinal);
        var tech = TechnologyResolver.LoadForPath(Path.Combine(ws, substrate[(sep + 3)..]))!;
        double width = 1.5e-3;
        double[] freqs = [1e9, 10e9];

        var answer = await Call(session, new JsonObject
        {
            ["call"] = "lineModel", ["line"] = "microstrip", ["substrate"] = substrate, ["width"] = width,
            ["freq"] = new JsonArray(freqs[0], freqs[1]),
        });
        Assert.Null(answer["error"]);

        for (int i = 0; i < freqs.Length; i++)
        {
            var calc = LineCalculator.Calculate(tech, new LineCalcRequest(substrate[..sep], [width], [], null, freqs[i]));
            Assert.Equal("MLIN", calc.ModelComponent);
            var line = calc.Rows.Single().Model!.Line;
            Assert.Equal(line.Z0,   (double)answer["z0"]![i]!,   1e-9 * line.Z0);
            Assert.Equal(line.Eeff, (double)answer["eeff"]![i]!, 1e-9 * line.Eeff);
        }
    }

    // ── gate 3: the adapter holds no logic ─────────────────────────────────────

    [Fact]
    public void Gate3_PluginHostServices_HoldsNoModelEquationNoResolverWalkAndNoEngineSetup()
    {
        string code = Regex.Replace(Regex.Replace(
            File.ReadAllText(Path.Combine(RepoRoot(), "src", "Design", "Plugins", "PluginHostServices.cs")),
            @"/\*.*?\*/", "", RegexOptions.Singleline), @"//[^\n]*", "");
        foreach (string forbidden in new[]
                 {
                     "Elaborat", "Engine", "Stamp(", "Math.", "Hammerstad", "Kirschning", "SubstrateResolver",
                     "Injection", "Directory.GetParent", ".Parent", "while", "for (", "CnlReader",
                 })
            Assert.False(code.Contains(forbidden, StringComparison.Ordinal), $"PluginHostServices.cs contains '{forbidden}'.");
    }

    // ── gate 4: refusals, and the session lives on ─────────────────────────────

    [PythonFact]
    public async Task Gate4_EachRefusalIsAHostReplyErrorWithASentence_AndTheSessionAnswersAfterwards()
    {
        string scratch = PluginTestKit.NewScratch();
        await using var session = await HostExample(scratch);
        string staging = session.StagingDirectory;
        File.WriteAllText(Path.Combine(staging, "em.cnl"),
            "Port:P1 in 0 Num=1 Z=50 Ohm\nR:R1 in 0 R=50 Ohm\nanalysis SP1 type=sparam start=1 stop=2 npts=3 Unit=GHz model=em\n");
        File.WriteAllText(Path.Combine(staging, "lp.cnl"),
            "Port:P1 in 0 Num=1 Z=50 Ohm\nR:R1 in 0 R=50 Ohm\nanalysis LP1 type=loadpull Tone=1 ToneUnit=GHz LoadTuner=TL SourceTuner=TS Grid=g.gam\n");
        File.WriteAllText(Path.Combine(staging, "board.cem"), "{}");
        string outside = Path.Combine(scratch, "outside.cnl");
        File.WriteAllText(outside, "Port:P1 in 0 Num=1 Z=50 Ohm\nR:R1 in 0 R=50 Ohm\nanalysis SP1 type=sparam start=1 stop=2 npts=3 Unit=GHz\n");

        var cases = new (JsonObject Call, string Expected)[]
        {
            (new() { ["call"] = "simulate", ["cnl"] = "em.cnl" },    PluginHostServices.EmRefused),
            (new() { ["call"] = "simulate", ["cnl"] = "board.cem" }, PluginHostServices.EmRefused),
            (new() { ["call"] = "simulate", ["cnl"] = "lp.cnl" },    PluginHostServices.LoadpullRefused),
            (new() { ["call"] = "simulate", ["cnl"] = outside },     "outside its staging folder"),
            (new() { ["call"] = "teleport" },                        "'teleport' is not a host call"),
        };
        foreach (var (call, expected) in cases)
        {
            var answer = await Call(session, call);
            Assert.Contains(expected, (string?)answer["error"] ?? "(no error)");
            Assert.Null(session.Fatal);
        }

        var after = await Call(session, new JsonObject { ["call"] = "evaluate", ["expr"] = "2 * 2.4 GHz", ["unit"] = "Hz" });
        Assert.Equal(4.8e9, (double)after["value"]!, 1e-3);
    }

    // ── gate 5: cancel stops the run ───────────────────────────────────────────

    [PythonFact]
    public async Task Gate5_CancellingDuringASimulate_StopsTheEngineRun_AndTheNextUpdateIsAnswered()
    {
        string scratch = PluginTestKit.NewScratch();
        await using var session = await HostExample(scratch);
        // 200 000 sweep points: seconds of engine work, cancelled the moment the busy bar says it started.
        File.WriteAllText(Path.Combine(session.StagingDirectory, "long.cnl"),
            "RX = 50\nPort:P1 in 0 Num=1 Z=50 Ohm\nR:R1 in 0 R=RX\n" +
            "analysis SP1 type=sparam start=1 stop=2 npts=3 Unit=GHz\n" +
            "analysis SW1 type=parametric_sweep Var=RX Inner=SP1 Start=1 Stop=1000 Npts=200000\n");

        using var cts = new CancellationTokenSource();
        string? busy = null;
        session.Progress += p => { if (p.Text is { } t) { busy = t; cts.Cancel(); } };
        var call = new JsonObject { ["call"] = "simulate", ["cnl"] = "long.cnl" };
        var cancelled = await session.UpdateAsync(new JsonObject { ["call"] = call.ToJsonString() }, cts.Token);
        Assert.Equal(PluginFailureKind.Cancelled, cancelled.Failure?.Kind);
        Assert.Equal(PluginHostServices.SimulatingText, busy);

        // The next update is answered, and the plugin reports the reply its cancelled call got: the run was stopped.
        var next = await session.UpdateAsync(new JsonObject { ["call"] = "" });
        Assert.True(next.Ok, next.Failure?.Sentence);
        var last = JsonNode.Parse((string)next.Value!.Readouts["last"]!)!.AsObject();
        Assert.Equal("The simulation was stopped.", (string?)last["error"]);
        Assert.False(File.Exists(Path.Combine(session.StagingDirectory, "long.SW1.npy")));
    }

    [PythonFact]
    public async Task ASimulatePastItsUpdatesDeadline_IsCutOffAndReported_AndTheSessionLives()
    {
        string scratch = PluginTestKit.NewScratch();
        var entry = PluginTestKit.Example("host");
        var started = PluginSession.Start(entry, PluginTestKit.Allowed(entry, scratch) with
        {
            Verbs = PluginVerbs.Instance, Deadlines = new PluginDeadlines { Simulate = TimeSpan.FromMilliseconds(200) },
        });
        await using var session = started.Value!;
        Assert.True((await session.HelloAsync()).Ok);
        File.WriteAllText(Path.Combine(session.StagingDirectory, "long.cnl"),
            "RX = 50\nPort:P1 in 0 Num=1 Z=50 Ohm\nR:R1 in 0 R=RX\n" +
            "analysis SP1 type=sparam start=1 stop=2 npts=3 Unit=GHz\n" +
            "analysis SW1 type=parametric_sweep Var=RX Inner=SP1 Start=1 Stop=1000 Npts=200000\n");

        var answer = await Call(session, new JsonObject { ["call"] = "simulate", ["cnl"] = "long.cnl" });
        Assert.Contains("did not finish within 0.2 s", (string?)answer["error"] ?? "(no error)");
        Assert.Null(session.Fatal);
    }

    // ── gate 6: a window plugin ────────────────────────────────────────────────

    [PythonAndGitFact]
    public async Task Gate6_AWindowPluginPlacesItsCell_AndASecondStartIsRefusedWhileItRuns()
    {
        using var ws = new GitWorkspace();
        File.WriteAllText(ws.GlobalConfig, "[user]\n\tname = A Designer\n\temail = designer@example.invalid\n");
        Assert.True(WorkspaceArming.Arm(ws.Root, CheckpointOrigin.SavePoint, true, null, true).Armed);

        var entry = PluginTestKit.Example("window");
        Assert.False(entry.Manifest!.HasHeadlessForm);
        var placedCell = new TaskCompletionSource<PluginPlacementResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = PluginTestKit.Allowed(entry, PluginTestKit.NewScratch(), "stay") with
        {
            WorkspaceDir = ws.Root,
            WindowPlaced = placed => Task.Run(() =>
            {
                var result = PluginPlacement.Place(new PluginPlacementRequest(ws.Root, entry.Manifest, placed.Cells.Single(), placed.Messages));
                placedCell.TrySetResult(result);
                return new PluginWindowAnswer([result.CellName], result.Outcome == PluginPlacementOutcome.Placed ? null : string.Join(" ", result.Refusals));
            }),
        };
        try
        {
            var started = PluginWindows.Start(entry, options);
            Assert.True(started.Ok, started.Failure?.Sentence);
            Assert.True((await started.Value!.HelloAsync()).Ok);

            var result = await placedCell.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(PluginPlacementOutcome.Placed, result.Outcome);
            Assert.True(File.Exists(Path.Combine(ws.Root, "attenuator", "schematic", "attenuator.cnl")));

            var again = PluginWindows.Start(entry, options);
            Assert.Equal(PluginFailureKind.AlreadyRunning, again.Failure?.Kind);
            Assert.Contains("already running", again.Failure!.Sentence);
        }
        finally
        {
            PluginWindows.CloseAll();
        }
        Assert.False(PluginWindows.IsRunning(entry.Manifest.Id));
    }

    [Fact]
    public void Gate6_AWindowPluginIsListedRunnable_WithTheOwnWindowNoteInItsTooltip()
    {
        var entry = PluginTestKit.Example("window");
        Assert.Null(CircuitRF.Ui.Plugins.PluginHost.UnavailableReason(entry));
        Assert.EndsWith(CircuitRF.Ui.Plugins.PluginsMenu.OwnWindowNote, CircuitRF.Ui.Plugins.PluginsMenu.Tip(entry));
        Assert.DoesNotContain(CircuitRF.Ui.Plugins.PluginsMenu.OwnWindowNote, CircuitRF.Ui.Plugins.PluginsMenu.Tip(PluginTestKit.Example("stub")));
    }

    private static (int ExitCode, string StdErr) RunCli(params string[] args)
    {
        string cliDir = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(PluginHostServicesTests).Assembly)
            .First(a => a.Key == "CliDir").Value!;
        string dll = Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll"));
        Assert.True(File.Exists(dll), $"{dll} is not built.");
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add(dll);
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string err = p.StandardError.ReadToEnd();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, err);
    }
}

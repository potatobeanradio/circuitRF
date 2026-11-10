using System;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using CircuitRF.Design.Plugins;
using CircuitRF.Ui.Tests.Layout.PCells;
using Xunit;

namespace CircuitRF.Ui.Tests.Plugins;

/// <summary>
/// brief-plg-1 gate 3 — <b>a plugin cannot take circuitRF down</b>. One test per way <c>tools/plugin-examples/broken</c>
/// misbehaves; each ends in the named <see cref="PluginFailure"/>, the caller is answered, and no process is left.
/// </summary>
public class PluginFailureTests
{
    private static readonly PluginDeadlines Short = new(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));

    private static PluginSession Start(string mode, PluginDeadlines? deadlines = null)
    {
        var entry = PluginTestKit.Example("broken");
        var started = PluginSession.Start(entry, PluginTestKit.Allowed(entry, PluginTestKit.NewScratch(), mode) with { Deadlines = deadlines ?? Short });
        Assert.True(started.Ok, started.Failure?.Sentence);
        return started.Value!;
    }

    private static async Task<PluginFailure> FailsAtHello(string mode, PluginDeadlines? deadlines = null)
    {
        var session = Start(mode, deadlines);
        var hello = await session.HelloAsync();
        Assert.False(hello.Ok);
        Assert.True(session.HasExited, "the plugin process is still running");
        await session.ByeAsync();
        return hello.Failure!;
    }

    [PythonFact]
    public async Task ExitsAtOnce()
    {
        var f = await FailsAtHello("exit");
        Assert.Equal(PluginFailureKind.Exited, f.Kind);
        Assert.Contains("broken plugin, mode exit", string.Join("\n", f.StderrTail));
    }

    [PythonFact]
    public async Task NeverAnswersIsATimeoutAndTheWholeTreeIsKilled()
    {
        var session = Start("hang", new PluginDeadlines(Hello: TimeSpan.FromSeconds(3)));
        var hello = await session.HelloAsync();
        Assert.Equal(PluginFailureKind.Timeout, hello.Failure!.Kind);
        Assert.True(session.HasExited);

        string? childLine = session.StderrTail.FirstOrDefault(l => l.StartsWith("child "));
        Assert.NotNull(childLine);
        int child = int.Parse(childLine!["child ".Length..]);
        bool alive;
        try { using var p = Process.GetProcessById(child); alive = !p.WaitForExit(2000); }
        catch (ArgumentException) { alive = false; }
        Assert.False(alive, "the plugin's child outlived it");
        await session.ByeAsync();
    }

    [PythonFact]
    public async Task ALineOnStdoutIsAProtocolFailureQuotingIt()
    {
        var f = await FailsAtHello("text");
        Assert.Equal(PluginFailureKind.Protocol, f.Kind);
        Assert.Contains("debug: the plugin is starting", f.Sentence);
    }

    [PythonFact]
    public async Task AMalformedFrameIsAProtocolFailure()
    {
        var f = await FailsAtHello("malformed");
        Assert.Equal(PluginFailureKind.Protocol, f.Kind);
        Assert.Contains("this is not json", f.Sentence);
    }

    [PythonFact]
    public async Task AReplyToAnIdNeverSentIsAProtocolFailure()
    {
        var session = Start("wrongid");
        Assert.True((await session.HelloAsync()).Ok);
        var update = await session.UpdateAsync(new JsonObject());
        Assert.Equal(PluginFailureKind.Protocol, update.Failure!.Kind);
        Assert.True(session.HasExited);
        await session.ByeAsync();
    }

    [PythonFact]
    public async Task AFileOutsideStagingIsRefusedByName()
    {
        var session = Start("outside");
        Assert.True((await session.HelloAsync()).Ok);
        var placed = await session.CommitAsync(new JsonObject());
        Assert.Equal(PluginFailureKind.OutsideStaging, placed.Failure!.Kind);
        Assert.Contains("escaped.cnl", placed.Failure.Sentence);
        await session.ByeAsync();
        Assert.True(session.HasExited);
    }

    [PythonFact]
    public async Task TenMegabytesOnStderrNeitherBlocksNorGrowsTheRing()
    {
        var session = Start("stderr", new PluginDeadlines(Hello: TimeSpan.FromSeconds(60), Update: TimeSpan.FromSeconds(10)));
        Assert.True((await session.HelloAsync()).Ok);
        Assert.True((await session.UpdateAsync(new JsonObject())).Ok);
        Assert.InRange(session.StderrTail.Count, 1, 200);
        await session.ByeAsync();
        Assert.True(session.HasExited);
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CircuitRF.Design.Plugins;
using CircuitRF.Ui.Tests.Layout.PCells;
using Xunit;

namespace CircuitRF.Ui.Tests.Plugins;

/// <summary>
/// brief-plg-2 gates 2–5 — the panel's update loop, with no window. Gates 2–4 use a fake session, so what the plugin
/// does and when is the test's to decide; gate 5 runs <c>tools/plugin-examples/broken</c> for real.
/// </summary>
public class PluginPanelControllerTests
{
    private static readonly PluginManifest Manifest = PluginManifestReader.Read("""
        { "apiVersion": 1, "id": "t.panel", "name": "Panel Test", "version": "1",
          "run": { "python": { "entry": "x.py" } }, "kind": "panel",
          "params": [
            { "name": "f0", "kind": "quantity", "unit": "Hz", "default": "1 GHz", "max": "10 GHz" },
            { "name": "n",  "kind": "int", "default": 1 },
            { "name": "out", "kind": "readout" }
          ] }
        """).Manifest!;

    private static PluginReply Reply(string tag, JsonObject? set = null)
        => new(new JsonObject { ["out"] = tag }, new JsonObject(), [new PluginMessage("info", tag)], new JsonObject(), set);

    /// <summary>A session whose answers the test supplies: <see cref="Answer"/> returns each request's reply task.</summary>
    private sealed class FakeSession : IPluginPanelSession
    {
        public readonly List<(string Type, JsonObject Params, CancellationToken Cancel)> Requests = [];
        public Func<int, CancellationToken, Task<PluginOutcome<PluginReply>>> Answer = (_, _) => Task.FromResult(PluginOutcome<PluginReply>.Of(Reply("r")));
        public bool AnswersCancel = true;
        public bool SaidBye;

        public event Action<PluginProgress>? Progress { add { } remove { } }
        public event Action<PluginFailure>? Ended { add { } remove { } }
        public PluginFailure? Fatal => null;

        public Task<PluginOutcome<PluginHello>> HelloAsync(CancellationToken ct = default) => Task.FromResult(PluginOutcome<PluginHello>.Of(new PluginHello(1)));

        public Task<PluginOutcome<PluginReply>> UpdateAsync(JsonObject parameters, CancellationToken ct = default)
        {
            int n;
            lock (Requests) { Requests.Add(("update", parameters, ct)); n = Requests.Count; }
            return Answer(n, ct);
        }

        public Task<PluginOutcome<PluginReply>> ActionAsync(string name, JsonObject parameters, CancellationToken ct = default) => UpdateAsync(parameters, ct);
        public Task<PluginOutcome<PluginPlaced>> CommitAsync(JsonObject parameters, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> SettledAsync(TimeSpan within) => Task.FromResult(AnswersCancel);
        public Task ByeAsync() { SaidBye = true; return Task.CompletedTask; }
        public ValueTask DisposeAsync() => new(ByeAsync());

        public int Count { get { lock (Requests) return Requests.Count; } }
        public (string Type, JsonObject Params, CancellationToken Cancel) this[int i] { get { lock (Requests) return Requests[i]; } }
    }

    private static PluginPanelController Controller(Func<FakeSession> make, PluginPanelOptions? options = null)
        => new(Manifest, () => PluginOutcome<IPluginPanelSession>.Of(make()), options);

    private static async Task Until(Func<bool> condition, string what)
    {
        for (int i = 0; i < 400 && !condition(); i++) await Task.Delay(25);
        Assert.True(condition(), $"timed out waiting for {what}");
    }

    /// <summary>A cancelled request answers Cancelled at once, as the real session does.</summary>
    private static Task<PluginOutcome<PluginReply>> CancelledWhen(CancellationToken ct)
    {
        var done = new TaskCompletionSource<PluginOutcome<PluginReply>>(TaskCreationOptions.RunContinuationsAsynchronously);
        ct.Register(() => done.TrySetResult(PluginOutcome<PluginReply>.Fail(new PluginFailure(PluginFailureKind.Cancelled, "Stopped.", []))));
        return done.Task;
    }

    [Fact]
    public async Task Gate2_OnlyTheNewestReplyIsApplied_AndTheSupersededRequestWasCancelled()
    {
        var first = new TaskCompletionSource<PluginOutcome<PluginReply>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<PluginOutcome<PluginReply>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new FakeSession { Answer = (n, _) => n == 1 ? first.Task : second.Task };
        await using var c = Controller(() => session, new PluginPanelOptions { Debounce = TimeSpan.FromMilliseconds(10) });

        c.Open();
        await Until(() => session.Count == 1, "request 1");
        c.SetValue("n", "2");
        await Until(() => session.Count == 2, "request 2");

        second.SetResult(PluginOutcome<PluginReply>.Of(Reply("two")));
        await Until(() => c.State.Reply is not null, "reply 2 applied");
        first.SetResult(PluginOutcome<PluginReply>.Of(Reply("one")));   // a plugin that ignored the cancel
        await Task.Delay(200);

        Assert.True(session[0].Cancel.IsCancellationRequested, "request 1 was not cancelled");
        Assert.Equal("two", c.State.Readouts["out"].Text);
        Assert.Equal("two", Assert.Single(c.State.Messages).Text);
    }

    [Fact]
    public async Task Gate3_FiveChangesWithinTheDebounceSendOneUpdateWithTheFinalValues()
    {
        var session = new FakeSession();
        await using var c = Controller(() => session);
        c.Open();
        await Until(() => c.State.Reply is not null, "the opening reply");

        for (int i = 2; i <= 6; i++) c.SetValue("n", i.ToString());
        await Task.Delay(600);

        Assert.Equal(2, session.Count);
        Assert.Equal(6, session[1].Params["n"]!.GetValue<long>());
    }

    [Fact]
    public async Task Gate4_APluginThatNeverAnswersNeverBlocks_TheTimeoutIsShown_AndStopRestartsIt()
    {
        var made = new List<FakeSession>();
        await using var c = Controller(() =>
        {
            var s = new FakeSession { Answer = (_, ct) => CancelledWhen(ct), AnswersCancel = false };
            made.Add(s);
            return s;
        }, new PluginPanelOptions { UpdateDeadline = TimeSpan.FromMilliseconds(300), StopGrace = TimeSpan.FromMilliseconds(100), Debounce = TimeSpan.FromMilliseconds(10) });

        // Every call the window makes returns although nothing is ever answered.
        c.Open();
        c.SetValue("n", "5");
        await Until(() => c.State.Host?.Kind == PluginHostLineKind.NoAnswer, "the no-answer line");
        Assert.Equal("No answer in 300 ms.", c.State.Host!.Text);
        Assert.True(c.State.Host.OffersStop);
        Assert.True(c.State.InFlight);

        c.Stop();
        await Until(() => c.Starts == 2, "the restart");
        Assert.True(made[0].SaidBye, "the stuck process was not closed");
        Assert.False(c.State.InFlight);
    }

    [PythonFact]
    public async Task Gate5_ACrashKeepsTheLastReply_SaysSo_AndTheNextChangeStartsANewProcess()
    {
        var entry = PluginTestKit.Example("broken");
        string scratch = PluginTestKit.NewScratch();
        await using var c = new PluginPanelController(entry.Manifest!, () =>
        {
            var started = PluginSession.Start(entry, PluginTestKit.Allowed(entry, scratch, "exit-after-first-reply") with
            {
                Deadlines = new PluginDeadlines(TimeSpan.FromSeconds(20), Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan),
            });
            return started.Ok ? PluginOutcome<IPluginPanelSession>.Of(started.Value!) : PluginOutcome<IPluginPanelSession>.Fail(started.Failure!);
        }, new PluginPanelOptions { Debounce = TimeSpan.FromMilliseconds(10) });

        c.Open();
        await Until(() => c.State.Host?.Kind == PluginHostLineKind.Stopped, "the stopped line");
        Assert.Equal(PluginHostLine.StoppedText, c.State.Host!.Text);
        Assert.Equal("answered n=1", Assert.Single(c.State.Messages).Text);     // the last reply stays shown

        c.SetValue("n", "2");
        await Until(() => c.State.Messages.Any(m => m.Text == "answered n=2"), "the reply from the new process");
        Assert.Equal(2, c.Starts);
    }

    [Fact]
    public async Task AValueAReplySetsReachesItsControlWithoutAnotherUpdate()
    {
        var session = new FakeSession { Answer = (n, _) => Task.FromResult(PluginOutcome<PluginReply>.Of(Reply("r", n == 1 ? new JsonObject { ["f0"] = 2.4e9 } : null))) };
        await using var c = Controller(() => session);
        c.Open();
        await Until(() => c.State.Reply is not null, "the reply");

        Assert.Equal("2.4 GHz", c.Value("f0"));
        Assert.Equal("2.4 GHz", c.State.Set["f0"]);
        c.SetValue("f0", "2.4 GHz");                 // the control echoing what it was given
        await Task.Delay(400);
        Assert.Equal(1, session.Count);
    }

    [Fact]
    public async Task AQuantityThatDoesNotEvaluateOrIsOutOfRangeIsMarkedAndNotSent_AndTheLastReplyStays()
    {
        var session = new FakeSession();
        await using var c = Controller(() => session, new PluginPanelOptions { Debounce = TimeSpan.FromMilliseconds(10) });
        c.Open();
        await Until(() => c.State.Reply is not null, "the reply");

        c.SetValue("f0", "12 GHz");
        await Until(() => c.State.FieldErrors.ContainsKey("f0"), "the out-of-range mark");
        Assert.Contains("above the maximum", c.State.FieldErrors["f0"]);
        c.SetValue("f0", "2 nH");
        await Until(() => c.State.FieldErrors.GetValueOrDefault("f0")?.Contains("not a Hz unit") == true, "the wrong-unit mark");

        Assert.Equal(1, session.Count);
        Assert.Equal("r", c.State.Readouts["out"].Text);
    }
}

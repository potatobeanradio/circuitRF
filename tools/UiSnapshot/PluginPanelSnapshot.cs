using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using CircuitRF.Design.Plugins;
using CircuitRF.Ui.Plugins;

/// <summary>
/// brief-plg-2 — a <c>panel</c> plugin's window, with the plugin really running (<c>--plugin-panel &lt;folder&gt;</c>).
/// <c>--plugin-state</c> picks what to show: <c>idle</c> (the first reply), <c>error</c> (the values
/// <c>--plugin-set</c> gives, from the start), <c>busy</c> (a second update that is never answered, with progress),
/// <c>stopped</c> (the process ended after its first reply) or <c>disabled</c> (a second reply that disables every
/// control). <c>--plugin-shift-check</c> measures every control before and after the disabling reply and fails if one
/// moved — gate 6. Only <c>busy</c>, <c>stopped</c> and <c>disabled</c> alter what the plugin said; each says how.
/// </summary>
static class PluginPanelSnapshot
{
    public static int Run(string pluginDir, string state, IReadOnlyDictionary<string, string> sets, string? workspace,
                          bool shiftCheck, double? width, double? height, string output)
    {
        var entry = PluginCatalog.Read(pluginDir, PluginSource.Linked);
        if (entry.Manifest is not { } manifest) { Console.Error.WriteLine(string.Join("\n", entry.Diagnostics)); return 1; }
        var (layout, diagnostics) = PluginLayoutReader.Read(manifest);
        if (layout is null) { Console.Error.WriteLine(string.Join("\n", diagnostics)); return 1; }
        if (shiftCheck) state = "disabled";

        string scratch = Path.Combine(Path.GetTempPath(), "crf-plg-snap-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(scratch);
        var controller = new PluginPanelController(manifest, () =>
        {
            var started = PluginSession.Start(entry, new PluginStartOptions
            {
                Trust = new PluginTrustStore([new KeyValuePair<string, bool>(entry.Directory, true)]),
                Policy = PluginPolicy.Resolve(null, null, _ => null),
                ScratchRoot = scratch, WorkspaceDir = workspace,
                Deadlines = new PluginDeadlines(TimeSpan.FromSeconds(20), Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan),
            });
            return started.Ok
                ? PluginOutcome<IPluginPanelSession>.Of(new Scripted(started.Value!, state, manifest))
                : PluginOutcome<IPluginPanelSession>.Fail(started.Failure!);
        }, new PluginPanelOptions
        {
            Context = new PluginParamContext(workspace),
            Initial = state == "error" ? sets : null,
            Post = a => Dispatcher.UIThread.Post(a),
        });

        var window = new PluginWindow(layout, controller, workspace, null);
        if (width is { } w) window.Width = w;
        if (height is { } h) window.Height = h;
        window.Show();
        controller.Open();
        // Text that cannot be sent is marked and never sent, so an error state may have no reply to wait for.
        Pump(window, () => controller.State.Reply is not null || controller.State.FieldErrors.Count > 0, "the first reply");

        Dictionary<string, Rect>? before = null;
        switch (state)
        {
            case "busy":
                Change(window, controller, manifest);
                Pump(window, () => controller.State.Busy && controller.State.Progress is not null, "the busy bar");
                break;
            case "stopped":
                Pump(window, () => controller.State.Host?.Kind == PluginHostLineKind.Stopped, "the stopped line");
                break;
            case "disabled":
                before = Measure(window);
                var first = controller.State.Reply;
                Change(window, controller, manifest);
                Pump(window, () => !ReferenceEquals(controller.State.Reply, first), "the disabling reply");
                break;
        }
        for (int k = 0; k < 6; k++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); window.UpdateLayout(); }
        window.CaptureRenderedFrame()!.Save(output);
        Console.WriteLine($"wrote {output}");

        int exit = 0;
        if (before is not null && shiftCheck)
        {
            var after = Measure(window);
            int disabled = window.ParamControls.Values.Count(c => !c.Editor.IsEnabled) + window.Buttons.Count(b => !b.IsEnabled);
            Console.WriteLine($"{disabled} controls disabled; {after.Count} measured");
            foreach (var (name, r) in before)
            {
                bool same = after.TryGetValue(name, out var a) && a == r;
                if (!same) exit = 1;
                Console.WriteLine($"  {(same ? "same " : "MOVED")} {name,-24} {r}{(same ? "" : $" -> {a}")}");
            }
        }
        _ = controller.CloseAsync();
        return exit;
    }

    /// <summary>Changes the first quantity (or int) by retyping it — what a user does — so a second update goes out.</summary>
    private static void Change(PluginWindow window, PluginPanelController controller, PluginManifest manifest)
    {
        var p = manifest.Params.First(x => x.Kind is PluginParamKind.Quantity or PluginParamKind.Int);
        string text = controller.Value(p.Name) + " ";      // the same value, a different text: a change, and still valid
        window.ParamControls[p.Name].Write(text);
        controller.SetValue(p.Name, text);
    }

    /// <summary>Every parameter control, its trailing piece, every button and Stop, in window coordinates.</summary>
    private static Dictionary<string, Rect> Measure(PluginWindow window)
    {
        for (int k = 0; k < 3; k++) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
        var m = new Dictionary<string, Rect>();
        void Put(string name, Control c)
        {
            if (c.TranslatePoint(new Point(0, 0), window) is { } at) m[name] = new Rect(at, c.Bounds.Size);
        }
        foreach (var (name, c) in window.ParamControls)
        {
            Put(name, c.Editor);
            if (c.Trailing is not null) Put(name + ".unit", c.Trailing);
        }
        for (int i = 0; i < window.Buttons.Count; i++) Put($"button[{i}]", window.Buttons[i]);
        Put("stop", window.StopButton);
        return m;
    }

    private static void Pump(Window window, Func<bool> done, string what)
    {
        var until = DateTime.UtcNow.AddSeconds(30);
        while (!done())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException($"timed out waiting for {what}");
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(20);
        }
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    /// <summary>The real session, with the one alteration a state needs after the first reply.</summary>
    private sealed class Scripted(PluginSession inner, string state, PluginManifest manifest) : IPluginPanelSession
    {
        private int _updates;
        private PluginFailure? _fatal;

        public event Action<PluginProgress>? Progress;
        public event Action<PluginFailure>? Ended;
        public PluginFailure? Fatal => _fatal ?? inner.Fatal;

        public Task<PluginOutcome<PluginHello>> HelloAsync(CancellationToken ct = default) => inner.HelloAsync(ct);

        public async Task<PluginOutcome<PluginReply>> UpdateAsync(JsonObject parameters, CancellationToken ct = default)
        {
            int n = Interlocked.Increment(ref _updates);
            if (n == 1)
            {
                var first = await inner.UpdateAsync(parameters, ct);
                if (state == "stopped")
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(100);
                        _fatal = new PluginFailure(PluginFailureKind.Exited, "The plugin exited (exit code 1) before it answered.", ["Traceback (most recent call last):", "ZeroDivisionError: float division by zero"]);
                        Ended?.Invoke(_fatal);
                    });
                return first;
            }
            if (state == "busy")
            {
                Progress?.Invoke(new PluginProgress(n, 0.4, "Solving section 2 of 5…"));
                await Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => { });
                return PluginOutcome<PluginReply>.Fail(new PluginFailure(PluginFailureKind.Cancelled, "Stopped.", []));
            }
            var reply = await inner.UpdateAsync(parameters, ct);
            if (state == "disabled" && reply.Value is { } r)
            {
                var enabled = new JsonObject();
                foreach (var p in manifest.Params) enabled[p.Name] = false;
                enabled["commit"] = false;
                return PluginOutcome<PluginReply>.Of(r with { Enabled = enabled });
            }
            return reply;
        }

        public Task<PluginOutcome<PluginReply>> ActionAsync(string name, JsonObject parameters, CancellationToken ct = default) => inner.ActionAsync(name, parameters, ct);
        public Task<PluginOutcome<PluginPlaced>> CommitAsync(JsonObject parameters, CancellationToken ct = default) => inner.CommitAsync(parameters, ct);
        public Task<bool> SettledAsync(TimeSpan within) => inner.SettledAsync(within);
        public Task ByeAsync() => inner.ByeAsync();
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}

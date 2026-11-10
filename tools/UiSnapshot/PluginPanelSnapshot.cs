using Avalonia.VisualTree;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using CircuitRF.Design.Plugins;
using CircuitRF.Ui.Plugins;
using CircuitRF.Ui.Plugins.Views;

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
    /// <summary>brief-plg-3: <paramref name="tab"/> selects that tab of the panel's tab strip;
    /// <paramref name="dragMarker"/> drops the first plot's bound marker 0 at that x (base SI) and
    /// <paramref name="dragHandle"/> drags the first layout's first grip that far along its axis (metres), each as the
    /// gesture's release does, and waits for the reply that follows — gate 3's drag, in the harness.</summary>
    public static int Run(string pluginDir, string state, IReadOnlyDictionary<string, string> sets, string? workspace,
                          bool shiftCheck, double? width, double? height, string output,
                          int? tab = null, double? dragMarker = null, double? dragHandle = null)
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
            Initial = sets.Count > 0 ? sets : null,
            Post = a => Dispatcher.UIThread.Post(a),
        });

        var window = new PluginWindow(layout, controller, workspace, null);
        if (width is { } w) window.Width = w;
        if (height is { } h) window.Height = h;
        window.Show();
        controller.Open();
        // Text that cannot be sent is marked and never sent, so an error state may have no reply to wait for.
        Pump(window, () => controller.State.Reply is not null || controller.State.FieldErrors.Count > 0, "the first reply");

        if (tab is { } t)
            foreach (var strip in window.GetVisualDescendants().OfType<TabControl>().Take(1)) strip.SelectedIndex = t;
        Settle(window);
        if (dragMarker is { } mx)
        {
            var plot = window.Views.Select(v => v.ViewControl).OfType<PluginPlotViewControl>().First();
            var sent = controller.State.Reply;
            if (!plot.DragBoundMarker(0, mx)) throw new InvalidOperationException("the plot has no bound marker 0");
            Pump(window, () => !ReferenceEquals(controller.State.Reply, sent) && !controller.State.InFlight, "the reply to the marker drag");
            var f0 = PluginViews.NumberOf(controller.State.ViewBodies.Values.First().Params[plot.Plot!.Traces[0].Markers[0].Name]);
            Console.WriteLine($"marker dropped at {mx:R}; the reply answered f0 = {f0:R}");
        }
        if (dragHandle is { } hd)
        {
            var lay = window.Views.Select(v => v.ViewControl).OfType<PluginLayoutViewControl>().First();
            var grip = lay.Overlay.Grips[0];
            double d = hd * lay.View!.Layout.DbuPerMicron * 1e6, r = grip.AxisDeg * Math.PI / 180;
            var sent = controller.State.Reply;
            if (!lay.Overlay.DragTo(0, grip.X + d * Math.Cos(r), grip.Y + d * Math.Sin(r))) throw new InvalidOperationException("the grip did not solve");
            Pump(window, () => !ReferenceEquals(controller.State.Reply, sent) && !controller.State.InFlight, "the reply to the handle drag");
            Console.WriteLine($"handle dragged {hd:R} m; {grip.Parameter} = {controller.Value(grip.Parameter)}");
        }

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

    private static void Settle(Window window)
    {
        for (int k = 0; k < 6; k++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); window.UpdateLayout(); }
    }

    /// <summary>
    /// brief-plg-3 gate 6's image view: a one-view panel whose plugin is scripted here — it stages
    /// <paramref name="image"/> and answers every update with it as a clickable <c>image</c> view.
    /// <paramref name="click"/> clicks the picture at that fraction of the frame and prints the event it sent.
    /// </summary>
    public static int RunImage(string image, double? width, double? height, string output, (double X, double Y)? click)
    {
        var manifest = PluginManifestReader.Read("""
            { "apiVersion": 1, "id": "snapshot.image", "name": "Image View", "version": "1",
              "run": { "python": { "entry": "none.py" } }, "kind": "panel",
              "params": [ { "name": "n", "label": "Points", "kind": "int", "default": 64 } ],
              "ui": { "title": "Image View", "size": [760, 520], "layout": { "split": "horizontal", "sizes": [1, 3], "children": [
                { "column": [ { "param": "n" }, { "messages": true } ] },
                { "view": "chart", "kind": "image" } ] } } }
            """).Manifest!;
        var (layout, _) = PluginLayoutReader.Read(manifest);
        string staging = Path.Combine(Path.GetTempPath(), "crf-plg-img-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(staging);
        File.Copy(image, Path.Combine(staging, Path.GetFileName(image)));
        var session = new ImageSession(staging, Path.GetFileName(image));
        var controller = new PluginPanelController(manifest, () => PluginOutcome<IPluginPanelSession>.Of(session),
            new PluginPanelOptions { Post = a => Dispatcher.UIThread.Post(a) });
        var window = new PluginWindow(layout!, controller, null, null);
        if (width is { } w) window.Width = w;
        if (height is { } h) window.Height = h;
        window.Show();
        controller.Open();
        Pump(window, () => controller.State.Reply is not null, "the first reply");
        Settle(window);
        if (click is { } c)
        {
            var img = window.Views.Select(v => v.ViewControl).OfType<PluginImageViewControl>().First();
            var at = new Point(img.Bounds.Width * c.X, img.Bounds.Height * c.Y);
            Console.WriteLine(img.Click(at) ? $"clicked; the plugin was sent {session.LastEvent}" : "the click was not on the picture");
            Pump(window, () => session.Events > 0 && !controller.State.InFlight, "the click's reply");
            Settle(window);
        }
        window.CaptureRenderedFrame()!.Save(output);
        Console.WriteLine($"wrote {output}");
        _ = controller.CloseAsync();
        return 0;
    }

    /// <summary>The image panel's plugin, played here.</summary>
    private sealed class ImageSession(string staging, string file) : IPluginPanelSession
    {
        public int Events;
        public string LastEvent = "";
        public event Action<PluginProgress>? Progress { add { } remove { } }
        public event Action<PluginFailure>? Ended { add { } remove { } }
        public PluginFailure? Fatal => null;
        public string StagingDirectory => staging;
        public Task<PluginOutcome<PluginHello>> HelloAsync(CancellationToken ct = default) => Task.FromResult(PluginOutcome<PluginHello>.Of(new PluginHello(1)));
        private Task<PluginOutcome<PluginReply>> Reply(string? note) => Task.FromResult(PluginOutcome<PluginReply>.Of(new PluginReply(
            new JsonObject(), new JsonObject { ["chart"] = new JsonObject { ["file"] = file, ["clickable"] = true } },
            note is null ? [] : [new PluginMessage("info", note)], new JsonObject(), null)));
        public Task<PluginOutcome<PluginReply>> UpdateAsync(JsonObject parameters, CancellationToken ct = default) => Reply(null);
        public Task<PluginOutcome<PluginReply>> ActionAsync(string name, JsonObject parameters, CancellationToken ct = default) => Reply(null);
        public Task<PluginOutcome<PluginReply>> EventAsync(string view, string kind, JsonObject fields, JsonObject parameters, CancellationToken ct = default)
        {
            Events++;
            LastEvent = $"event {{view: {view}, kind: {kind}, x: {fields["x"]}, y: {fields["y"]}}}";
            return Reply($"Clicked at ({fields["x"]}, {fields["y"]}) in the picture's pixels.");
        }
        public Task<PluginOutcome<PluginPlaced>> CommitAsync(JsonObject parameters, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> SettledAsync(TimeSpan within) => Task.FromResult(true);
        public Task ByeAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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
        public Task<PluginOutcome<PluginReply>> EventAsync(string view, string kind, JsonObject fields, JsonObject parameters, CancellationToken ct = default) => inner.EventAsync(view, kind, fields, parameters, ct);
        public string StagingDirectory => inner.StagingDirectory;
        public Task<PluginOutcome<PluginPlaced>> CommitAsync(JsonObject parameters, CancellationToken ct = default) => inner.CommitAsync(parameters, ct);
        public Task<bool> SettledAsync(TimeSpan within) => inner.SettledAsync(within);
        public Task ByeAsync() => inner.ByeAsync();
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}

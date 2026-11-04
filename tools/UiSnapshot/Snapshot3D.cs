using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Headless;
using System.Globalization;
using System.Numerics;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Render.Scene3D;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.Viewer3D;

// UiSnapshot --c3d <file.c3d> -o <out.png> [--size WxH] [--realistic] [--fit] [--ortho|--perspective]
//            [--yaw deg] [--pitch deg] [--distance mm] [--target x,y,z] [--only a+b] [--hide a+b] [--list]
//            [--hover name] [--live] [--select row]
//
// Opens a .c3d in the 3D editor's own view model, on the platform's own GPU backend (Metal, D3D11 or Vulkan, as
// Viewer3DBackends.Create picks for the application), and writes what Export Picture writes: Viewer3DViewModel's
// CapturePicture, the same plan and read-back. No Avalonia window is involved — the editor's view model needs only a
// dispatcher to post to, which is the queue this pumps.
//
// The camera starts where the owner left it: the workspace's .cwsuser, as OpenC3dEditor restores it. --fit frames the
// scene instead; --yaw/--pitch/--distance/--target then move it, in the camera's own terms (degrees, millimetres, the
// scene-local frame --list prints). --only/--hide name objects by substring, '+' between several.
//
// --select names a tree row (a field plot, an object) to select first; the picture then carries the 2D overlay — the
// selection's chrome, a selected clip-plane plot's plane (brief-dfr31-3) — laid over the GPU's pixels exactly as Copy and
// Export Picture lay it (Viewer3DOverlay.PictureLayer). That overlay is drawn by Avalonia, so only this option starts the
// headless platform.
//
// --section x,y,z:x,y,z (brief-dfr34-3) runs Section Through… as a person does: each point (world millimetres) is projected to
// the view, hovered — the viewer's own CPU snap resolves there, and what it snapped to is printed — and clicked through the
// editor's DrawClick. Before the second click the pick is written to <out>.pick.png (the rubber band from the first point to the
// second, over the GPU's pixels); the final picture is the view the command leaves: the plot, the clip plane and the camera.
// --undo then undoes once more and writes <out>.undo.png. Like --select it starts the headless platform for the overlay.
internal static class Snapshot3D
{
    public static int Run(string[] args)
    {
        string? path = null, output = null, only = null, hide = null, hover = null, select = null, section = null;
        bool undo = false;
        bool live = false;
        int w = 1200, h = 800;
        bool realistic = false, fit = false, list = false;
        bool? ortho = null;
        float? yaw = null, pitch = null, distance = null;
        Vector3? target = null;
        static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--c3d" when i + 1 < args.Length: path = Path.GetFullPath(args[++i]); break;
                case "-o" when i + 1 < args.Length: output = args[++i]; break;
                case "--size" when i + 1 < args.Length:
                    var wh = args[++i].Split('x');
                    (w, h) = (int.Parse(wh[0]), int.Parse(wh[1]));
                    break;
                case "--realistic": realistic = true; break;
                case "--fit": fit = true; break;
                case "--ortho": ortho = true; break;
                case "--perspective": ortho = false; break;
                case "--yaw" when i + 1 < args.Length: yaw = F(args[++i]) * MathF.PI / 180; break;
                case "--pitch" when i + 1 < args.Length: pitch = F(args[++i]) * MathF.PI / 180; break;
                case "--distance" when i + 1 < args.Length: distance = F(args[++i]) * 1e-3f; break;
                case "--target" when i + 1 < args.Length:
                    var t = args[++i].Split(',').Select(v => F(v) * 1e-3f).ToArray();
                    target = new Vector3(t[0], t[1], t[2]);
                    break;
                case "--only" when i + 1 < args.Length: only = args[++i]; break;
                case "--hide" when i + 1 < args.Length: hide = args[++i]; break;
                case "--list": list = true; break;
                case "--hover" when i + 1 < args.Length: hover = args[++i]; break;
                case "--live": live = true; break;
                case "--select" when i + 1 < args.Length: select = args[++i]; break;
                case "--section" when i + 1 < args.Length: section = args[++i]; break;
                case "--undo": undo = true; break;
                default: Console.Error.WriteLine($"unknown option {args[i]}"); return 2;
            }
        }
        if (path is null || (output is null && !list))
        {
            Console.Error.WriteLine("usage: UiSnapshot --c3d <file.c3d> -o <out.png> [--size WxH] [--realistic] [--fit] [--ortho|--perspective]\n"
                                  + "                  [--yaw deg] [--pitch deg] [--distance mm] [--target x,y,z] [--only a+b] [--hide a+b] [--list]");
            return 2;
        }

        if (select is not null || section is not null)
            AppBuilder.Configure<SnapshotApp>().UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).WithInterFont().SetupWithoutStarting();

        var posted = new ConcurrentQueue<Action>();
        void Pump(Func<bool> until, string what)
        {
            if (!SpinWait.SpinUntil(() => { while (posted.TryDequeue(out var a)) a(); return until(); }, TimeSpan.FromMinutes(2)))
                throw new TimeoutException($"the 3D editor never {what}");
        }

        string? cws = FindWorkspace(path);
        var vm = new C3dEditorViewModel(path, C3dPersistence.LoadFromFile(path), Viewer3DBackends.Create, () => cws, posted.Enqueue)
        {
            // Where the workspace's runs are (WorkspaceViewModel.EmResultsRoot): a solved setup's field plots draw as they do there.
            ResultsRootProvider = cws is null ? null : () => Path.Combine(Path.GetDirectoryName(cws)!, "results"),
        };
        try
        {
            // OpenC3dEditor's own order: orthographic, then the stored camera and setup, which win.
            vm.Viewer.IsPerspective = false;
            var user = cws is null ? null : WorkspacePersistence.LoadFromFile(cws);
            string? key = cws is null ? null : Path.GetRelativePath(Path.GetDirectoryName(cws)!, path).Replace('\\', '/');
            if (!fit && key is not null && user?.Viewer3DCameras?.GetValueOrDefault(key) is { } camera) vm.Viewer.RestoreCamera(camera);
            vm.RestoreActiveSetup(key is not null ? user?.C3dActiveSetups?.GetValueOrDefault(key) : null);
            vm.Viewer.Resized(w, h);
            vm.Viewer.Session.EnsureBackend();
            vm.Start();
            Pump(() => vm.AdoptedGeneration == vm.Viewer.Source.Requested && vm.Viewer.Scene.Objects.Length > 0, "built a scene");
            var scene = vm.Viewer.Scene;
            if (list)
            {
                foreach (var o in scene.Objects)
                    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                        $"{o.Name}\t{o.Kind}\t{o.Min.X * 1e3:F4},{o.Min.Y * 1e3:F4},{o.Min.Z * 1e3:F4} .. {o.Max.X * 1e3:F4},{o.Max.Y * 1e3:F4},{o.Max.Z * 1e3:F4} mm"));
                if (output is null) return 0;
            }

            var view = vm.Viewer.View;
            if (only is not null || hide is not null)
                foreach (var o in scene.Objects)
                {
                    bool named(string? keys) => keys?.Split('+').Any(k => o.Name.Contains(k, StringComparison.Ordinal)) == true;
                    if ((only is not null && !named(only)) || named(hide)) view.Visible[o.Id - 1] = false;
                }
            if (ortho is { } o2)
            {
                vm.Viewer.IsPerspective = !o2;
                view.Camera.Projection = o2 ? Projection3D.Orthographic : Projection3D.Perspective;
            }
            if (yaw is { } y) view.Camera.Yaw = y;
            if (pitch is { } p) view.Camera.Pitch = p;
            if (distance is { } d) view.Camera.Distance = d;
            if (target is { } tg) view.Camera.Target = tg;
            if (realistic)
            {
                vm.Viewer.IsRealistic = true;
                Pump(() => view.Environment is not null, "made the realistic view's environment");
            }

            if (hover is not null)
            {
                var hovered = scene.Objects.FirstOrDefault(o => o.Name.Contains(hover, StringComparison.Ordinal));
                if (hovered is null) { Console.Error.WriteLine($"no object named like '{hover}' (--list names them)"); return 2; }
                view.Hovered = hovered.Id;
                Console.WriteLine($"Hovering {hovered.Name} ({hovered.Kind})");
            }

            if (select is not null)
            {
                static IEnumerable<C3dTreeItem> Walk(C3dTreeItem t) => t.Children.SelectMany(Walk).Prepend(t);
                var row = vm.Tree.SelectMany(g => g.Items).SelectMany(Walk).FirstOrDefault(t => t.Name == select);
                if (row is null) { Console.Error.WriteLine($"no tree row named '{select}'"); return 2; }
                vm.SelectedTreeItem = row;
                // A drawn plot's field is read and built off the UI thread: wait until each layer has drawn or said why not.
                Pump(() => vm.Viewer.FieldLayers.All(l => !l.Building && (l.Builds > 0 || l.Problem is not null)),
                     "finished drawing the plots");
                Console.WriteLine($"Selected {row.Name} ({row.Kind})");
                if (vm.FieldPlot(row.Name) is { } plot && vm.FieldPlotProblem(plot) is { } why) Console.WriteLine($"  draws nothing: {why}");
            }

            bool overlay = select is not null || section is not null;
            bool Capture(string to)
            {
                var picture = vm.Viewer.CapturePicture(w, h, 1, out string? why, supersample: 1, live: live || section is not null);
                if (picture is null) { Console.Error.WriteLine($"no picture: {why}"); return false; }
                if (overlay) picture = picture with { Layer = Viewer3DOverlay.PictureLayer(vm.Viewer, w, picture.Width, picture.Height) };
                File.WriteAllBytes(to, picture.Png());
                return true;
            }

            if (section is not null)
            {
                var ends = section.Split(':').Select(e => e.Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture) * 1e-3).ToArray()).ToArray();
                if (ends.Length != 2 || ends.Any(e => e.Length != 3)) { Console.Error.WriteLine("--section x,y,z:x,y,z (world mm)"); return 2; }
                var onPicked = typeof(Viewer3DViewModel).GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    .Single(m => m.Name == "OnPicked" && m.GetParameters().Length == 5);
                void HoverAt(double[] world)
                {
                    var (px, py, front) = view.Camera.Project(scene.ToLocal(world[0], world[1], world[2]), w, h);
                    if (!front) throw new InvalidOperationException("a --section point is behind the camera");
                    vm.Viewer.Hover(px, py);
                    var (id, face) = Scene3DPicking.PairAtPixel(scene, view.Camera, px, py, w, h, view.Visible);
                    onPicked.Invoke(vm.Viewer, [id, face, Vector3.Zero, id != 0, null]);
                    Console.WriteLine($"  hovered ({px:F0}, {py:F0}) over {(id == 0 ? "nothing" : scene.Object(id)?.Name)}: {vm.Viewer.SnapText}");
                }
                vm.SectionThrough();
                Console.WriteLine($"Armed: {vm.ToolPrompt}");
                HoverAt(ends[0]);
                vm.DrawClick(Avalonia.Input.KeyModifiers.None, 1);
                Console.WriteLine($"  first click: {vm.ToolPrompt} {vm.StatusMessage}");
                HoverAt(ends[1]);
                string pick = Path.ChangeExtension(output!, ".pick.png");
                if (!Capture(pick)) return 1;
                Console.WriteLine($"Wrote {pick}");
                vm.DrawClick(Avalonia.Input.KeyModifiers.None, 1);
                vm.Viewer.Leave();
                Console.WriteLine($"  second click: {vm.StatusMessage}");
                Pump(() => vm.Viewer.FieldLayers.All(l => !l.Building && (l.Builds > 0 || l.Problem is not null)), "finished drawing the plots");
                var made = vm.Document.FieldPlots[^1];
                Console.WriteLine($"  plot {made.Name}: {System.Text.Json.JsonSerializer.Serialize(new[] { made.Through?.From, made.Through?.To })}; " +
                                  $"clip {vm.Viewer.ClipAxis} enabled={vm.Viewer.ClipEnabled} flip={vm.Viewer.ClipFlip}; undo: {vm.UndoRedo.UndoDescription}");
                if (vm.FieldPlotProblem(made) is { } none) Console.WriteLine($"  draws nothing: {none}");
            }

            if (!Capture(output!)) return 1;
            if (section is not null && undo)
            {
                vm.UndoRedo.Undo();
                Pump(() => vm.Viewer.FieldLayers.All(l => !l.Building), "settled after the undo");
                string after = Path.ChangeExtension(output!, ".undo.png");
                if (!Capture(after)) return 1;
                Console.WriteLine($"Wrote {after}: {vm.Document.FieldPlots.Count} plot(s), clip {vm.Viewer.ClipAxis} enabled={vm.Viewer.ClipEnabled}");
            }
            var c = view.Camera;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"Wrote {output} ({w}x{h}, {(realistic ? "realistic" : "plain")}, {c.Projection}) — yaw {c.Yaw * 180 / MathF.PI:F1}°, " +
                $"pitch {c.Pitch * 180 / MathF.PI:F1}°, distance {c.Distance * 1e3:F4} mm, target {c.Target.X * 1e3:F4},{c.Target.Y * 1e3:F4},{c.Target.Z * 1e3:F4} mm"));
            return 0;
        }
        finally { vm.Dispose(); }
    }

    /// <summary>The nearest .cws above the document — the workspace the GUI would have open around it.</summary>
    private static string? FindWorkspace(string path)
    {
        for (var dir = Path.GetDirectoryName(path); dir is not null; dir = Path.GetDirectoryName(dir))
            if (File.Exists(Path.Combine(dir, ".cws"))) return Path.Combine(dir, ".cws");
        return null;
    }
}

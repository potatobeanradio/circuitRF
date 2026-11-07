using System.Collections.Concurrent;
using System.Globalization;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render;
using CircuitRF.Ui.Viewer3D;

// UiSnapshot --cem-3d <file.cem> -o <out.png> [--size WxH] [--yaw deg] [--pitch deg] [--list]
//
// A .cem's Show 3D: the setup panel's own viewer (Viewer3DViewModel, fed the setup and the layout the .cem resolves, as the
// workspace feeds it), drawn on the platform's GPU backend and written through Export Picture's CapturePicture, as --c3d is.
// The scene's notes are printed, first to last. --list prints every object's name, kind and bounds (mm). brief-lcp-5 — a setup
// naming component ports draws their sheets and landings.
internal static class SnapshotCem3D
{
    public static int Run(string[] args)
    {
        string? path = null, output = null;
        int w = 1200, h = 800;
        float? yaw = null, pitch = null;
        bool list = false;
        static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--cem-3d" when i + 1 < args.Length: path = Path.GetFullPath(args[++i]); break;
                case "-o" when i + 1 < args.Length: output = args[++i]; break;
                case "--size" when i + 1 < args.Length:
                    var wh = args[++i].Split('x');
                    (w, h) = (int.Parse(wh[0]), int.Parse(wh[1]));
                    break;
                case "--yaw" when i + 1 < args.Length: yaw = F(args[++i]) * MathF.PI / 180; break;
                case "--pitch" when i + 1 < args.Length: pitch = F(args[++i]) * MathF.PI / 180; break;
                case "--list": list = true; break;
                default: Console.Error.WriteLine($"unknown option {args[i]}"); return 2;
            }
        }
        if (path is null || (output is null && !list))
        {
            Console.Error.WriteLine("usage: UiSnapshot --cem-3d <file.cem> -o <out.png> [--size WxH] [--yaw deg] [--pitch deg] [--list]");
            return 2;
        }

        var setup = EmSetupPersistence.LoadFromFile(path);
        string? cws = CircuitRF.Design.Workspace.WorkspaceRootFinder.FindAncestorCws(Path.GetDirectoryName(path));
        var resolved = EmSetupResolver.Resolve(path, setup.LayoutRef, cws, new TechnologyCache());
        var inputs = new Viewer3DInputs(setup, resolved.Source,
                                        resolved.Source is null ? string.Join(" ", resolved.Diagnostics) : null,
                                        ColorTheme.BuiltIn, ColorVariant.Light);

        var posted = new ConcurrentQueue<Action>();
        void Pump(Func<bool> until, string what)
        {
            if (!SpinWait.SpinUntil(() => { while (posted.TryDequeue(out var a)) a(); return until(); }, TimeSpan.FromMinutes(2)))
                throw new TimeoutException($"the viewer never {what}");
        }
        string? results = cws is null ? null : Path.Combine(Path.GetDirectoryName(cws)!, "results");
        using var vm = new Viewer3DViewModel(path, () => inputs, Viewer3DBackends.Create, () => results, posted.Enqueue);
        vm.Resized(w, h);
        vm.Session.EnsureBackend();
        vm.Regenerate();
        Pump(() => !vm.IsRegenerating && vm.Scene.Generation >= 1, "built a scene");
        var scene = vm.Scene;
        foreach (string note in scene.Notes) Console.WriteLine("note: " + note);
        if (list)
        {
            foreach (var o in scene.Objects)
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{o.Name}\t{o.Kind}\t{o.Min.X * 1e3:F4},{o.Min.Y * 1e3:F4},{o.Min.Z * 1e3:F4} .. {o.Max.X * 1e3:F4},{o.Max.Y * 1e3:F4},{o.Max.Z * 1e3:F4} mm"));
            if (output is null) return 0;
        }
        if (scene.Objects.Length == 0) { Console.Error.WriteLine("the scene is empty"); return 1; }
        if (yaw is { } y) vm.View.Camera.Yaw = y;
        if (pitch is { } p) vm.View.Camera.Pitch = p;
        var picture = vm.CapturePicture(w, h, 1, out string? why, supersample: 1);
        if (picture is null) { Console.Error.WriteLine($"no picture: {why}"); return 1; }
        File.WriteAllBytes(output!, picture.Png());
        Console.WriteLine($"Wrote {output} ({w}x{h}): {scene.Objects.Length} object(s), {scene.Objects.Count(o => o.Kind == Scene3DKind.Port)} port(s)");
        return 0;
    }
}

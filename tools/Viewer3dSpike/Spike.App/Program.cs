using Avalonia;

namespace Viewer3dSpike.App;

/// <summary>
/// Usage: Viewer3dSpike --route gl|metal|wgpu [--msh case.msh] [--tris 1000000]
///                      [--compositor default|metal|gl]   (macOS: Avalonia's own backend; default = Metal,
///                                                        except route gl, which needs gl)
/// Route C (gl) runs on every OS. Routes A (metal) and B (wgpu) are hosted through composition GPU
/// interop, implemented for macOS only in this spike; elsewhere the pane says so.
/// On exit the counter summaries are printed to stdout — paste them into the findings.
/// </summary>
static class Program
{
    public static string Route = "gl", Compositor = "default";
    public static string? Msh;
    public static int Tris = 1_000_000;

    [STAThread]
    public static int Main(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
            switch (args[i])
            {
                case "--route": Route = args[++i]; break;
                case "--msh": Msh = args[++i]; break;
                case "--tris": Tris = int.Parse(args[++i]); break;
                case "--compositor": Compositor = args[++i]; break;
                default: Console.Error.WriteLine($"unknown argument {args[i]}"); return 2;
            }
        // Avalonia 12's real macOS default is Metal, OpenGl, Software (its XML doc still says OpenGL), and
        // under the Metal compositor OpenGlControlBase cannot initialise ("Unable to locate
        // IPlatformGraphicsOpenGlContextFactory"). Route C therefore needs the WHOLE application on
        // Avalonia's OpenGL compositor — chosen here unless --compositor says otherwise.
        if (OperatingSystem.IsMacOS() && Route == "gl" && Compositor == "default") Compositor = "gl";
        // Avalonia's own warnings to the terminal: a GL control that fails to initialise says why here
        var b = AppBuilder.Configure<App>().UsePlatformDetect().LogToTextWriter(Console.Error, Avalonia.Logging.LogEventLevel.Warning);
        if (OperatingSystem.IsMacOS() && Compositor != "default")
            b = b.With(new AvaloniaNativePlatformOptions
            {
                RenderingMode = Compositor == "metal"
                    ? [AvaloniaNativeRenderingMode.Metal, AvaloniaNativeRenderingMode.OpenGl, AvaloniaNativeRenderingMode.Software]
                    : [AvaloniaNativeRenderingMode.OpenGl, AvaloniaNativeRenderingMode.Software],
            });
        return b.StartWithClassicDesktopLifetime(args);
    }
}

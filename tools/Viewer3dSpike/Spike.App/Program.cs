using Avalonia;

namespace Viewer3dSpike.App;

/// <summary>
/// Usage: Viewer3dSpike --route gl|metal|wgpu|d3d11|vulkan [--msh case.msh] [--tris 1000000]
///                      [--compositor default|metal|gl|vulkan|glx]
///                      [--exit-after seconds]   (closes the window itself and prints the summaries)
/// --compositor on macOS picks Avalonia's own backend (default = Metal, except route gl, which needs gl);
/// on Linux, vulkan puts the window on Avalonia's Vulkan compositor (X11RenderingMode.Vulkan) instead of
/// its default GLX. Route C (gl) runs on every OS. Route A is hosted through composition GPU interop:
/// metal on macOS, d3d11 on Windows, vulkan on Linux (brief em3d-28 step 0); route B (wgpu) on macOS only.
/// On exit the counter summaries are printed to stdout — paste them into the findings.
/// </summary>
static class Program
{
    public static string Route = "gl", Compositor = "default";
    public static string? Msh;
    public static int Tris = 1_000_000;
    public static double ExitAfter;

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
                case "--exit-after": ExitAfter = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
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
        if (OperatingSystem.IsLinux() && Compositor != "default")
            b = b.With(new X11PlatformOptions
            {
                RenderingMode = Compositor == "vulkan"
                    ? [X11RenderingMode.Vulkan, X11RenderingMode.Glx, X11RenderingMode.Software]
                    : [X11RenderingMode.Glx, X11RenderingMode.Software],
            });
        return b.StartWithClassicDesktopLifetime(args);
    }
}

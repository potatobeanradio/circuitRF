// ================================================================
//  Pattern3DCliTests.cs — ANT-10 §6, the headless half.
//
//  "The same picture from `Cli render`/`plot` as from the app, byte for byte, which is the standing
//  gate for anything drawn in `src/Render` and the whole reason it lives there."
//
//  The gate is the one that contract already has and that ANT-7's own CLI tests use: byte identity
//  between the picture `plot` draws and the picture `render` draws from the document `plot` itself
//  wrote — the two halves being a process and a process, with the camera in between them carried in
//  the `.cdd` rather than on a flag either one of them re-reads.
//
//  Run as a PROCESS, like every other CLI gate in this folder.
// ================================================================

using System.Diagnostics;
using System.Text.Json;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.Tests.DataDisplay;
using RfCore;
using RfCore.Data;
using RfCore.Export;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Cli;

public sealed class Pattern3DCliTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "crf-ant10-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private string Dir(string name)
    {
        string d = Path.Combine(_root, name);
        Directory.CreateDirectory(d);
        return d;
    }

    private string Result(string dir)
    {
        string npy = Path.Combine(dir, "run.npy");
        DataSetExporter.Export(PatternFixture.Data, npy, ExportFormat.Npy);
        return npy;
    }

    private const string Trace = "cube=farfield.U,cut=all,port=1,y=db10";

    // ══ the standing gate ════════════════════════════════════════════════════

    /// <summary>
    /// <b>One picture, drawn once.</b> `plot` writes the document and renders it; `render` reads
    /// that document and renders it again; the bytes are the same. The camera is carried in the
    /// document, so this also gates the persistence: a `.cdd` that lost the view would draw the
    /// default isometric one here and the two files would differ.
    /// </summary>
    [Theory]
    [InlineData("iso")]
    [InlineData("broadside")]
    [InlineData("phi0")]
    [InlineData("phi90")]
    public void ASurfacePlot_IsTheSameBytesAsRenderingTheDocumentItWrote(string view)
    {
        string dir       = Dir("bytes-" + view);
        string npy       = Result(dir);
        string viaPlot   = Path.Combine(Dir("bytes-" + view + "-a"), "one.svg");
        string viaRender = Path.Combine(Dir("bytes-" + view + "-b"), "one.svg");
        string cdd       = Path.Combine(dir, "one.cdd");

        var plot = RunCli("plot", npy, "-o", viaPlot, "--write-cdd", cdd,
                          "--type", "surface", "--view", view, "--db-unit", "dB(W/sr)",
                          "--trace", Trace);
        Assert.True(plot.ExitCode == 0, plot.StdErr + plot.StdOut);

        var render = RunCli("render", cdd, "--data", npy, "-o", viaRender);
        Assert.True(render.ExitCode == 0, render.StdErr + render.StdOut);

        Assert.Equal(File.ReadAllBytes(viaPlot), File.ReadAllBytes(viaRender));
        output.WriteLine($"--view {view}: {new FileInfo(viaPlot).Length} bytes, identical both ways");
    }

    /// <summary>A rotation nobody named a view for round-trips the same way — two angles and a zoom,
    /// which is the whole camera.</summary>
    [Fact]
    public void AnArbitraryRotationAndZoom_RoundTripThroughTheDocument()
    {
        string dir       = Dir("rot");
        string npy       = Result(dir);
        string viaPlot   = Path.Combine(Dir("rot-a"), "one.svg");
        string viaRender = Path.Combine(Dir("rot-b"), "one.svg");
        string cdd       = Path.Combine(dir, "one.cdd");

        Assert.Equal(0, RunCli("plot", npy, "-o", viaPlot, "--write-cdd", cdd,
                               "--type", "surface", "--rotate", "-127.5,-18", "--zoom", "1.8",
                               "--trace", Trace).ExitCode);
        Assert.Equal(0, RunCli("render", cdd, "--data", npy, "-o", viaRender).ExitCode);

        var pc = JsonSerializer.Deserialize<DataDisplayConfig>(File.ReadAllText(cdd))!.Tabs[0].Plots[0];
        Assert.Equal(PlotType.Surface3D, pc.PlotType);
        Assert.Equal(232.5, pc.SurfaceAzimuthDeg, 9);        // wrapped to [0, 360)
        Assert.Equal(-18.0, pc.SurfaceElevationDeg, 9);
        Assert.Equal(1.8,   pc.SurfaceZoom, 9);

        Assert.Equal(File.ReadAllBytes(viaPlot), File.ReadAllBytes(viaRender));
    }

    /// <summary>
    /// The document is not an empty picture agreeing with another empty picture: it carries the
    /// slice, and the slice leaves BOTH angle axes open.
    /// </summary>
    [Fact]
    public void TheDocumentItWrites_LeavesBothAngleAxesOpen()
    {
        string dir = Dir("doc");
        string cdd = Path.Combine(dir, "one.cdd");
        string npy = Result(dir);

        Assert.Equal(0, RunCli("plot", npy, "-o", Path.Combine(dir, "p.svg"), "--write-cdd", cdd,
                               "--type", "surface", "--trace", Trace).ExitCode);

        var config = JsonSerializer.Deserialize<DataDisplayConfig>(File.ReadAllText(cdd))!;
        var slice  = config.Tabs[0].Plots[0].Traces[0].CubeSlice;
        Assert.Equal(["freq", "theta", "phi", "port"], slice.Select(a => a.AxisName).ToArray());
        Assert.Equal(AxisRole.PinToIndex, slice[0].Role);
        Assert.NotEqual(AxisRole.PinToIndex, slice[1].Role);   // theta open
        Assert.NotEqual(AxisRole.PinToIndex, slice[2].Role);   // phi open
        Assert.Equal(AxisRole.PinToIndex, slice[3].Role);

        // And the WINDOW's own loader resolves that document into a surface — the grid, on the
        // cube's own axes, with the plot-wide scale already built.
        var plot = PlotConfigLoader.LoadPlot(config.Tabs[0].Plots[0], new OneFile(npy));
        Assert.True(plot.IsPatternPlot);
        Assert.False(plot.IsPolarPattern);

        var cube = PatternFixture.Data["farfield.U"];
        var grid = plot.Traces[0].SurfaceGrid!;
        Assert.Equal(cube.Axes[1].Length, grid.ThetaCount);
        Assert.Equal(cube.Axes[2].Length, grid.PhiCount);
        Assert.Equal("theta", grid.ThetaAxisName);
        Assert.Equal("phi",   grid.PhiAxisName);

        for (int it = 0; it < grid.ThetaCount; it++)
            for (int ip = 0; ip < grid.PhiCount; ip++)
                Assert.Equal(DbFloor.Db10(cube[0, it, ip, 0].RealValue!.Value), grid.At(it, ip), 9);

        output.WriteLine($"{grid.ThetaCount} × {grid.PhiCount} grid; {plot.PatternScale!.ReferenceCaption()}");
    }

    // ══ both themes, and a small pane ════════════════════════════════════════

    /// <summary>
    /// <b>Both themes, like every other plot</b> — and they are genuinely different pictures, not
    /// one picture drawn twice with a theme that never reached the canvas.
    /// </summary>
    [Fact]
    public void ItDrawsInBothThemes_AndTheyAreDifferentPictures()
    {
        string dir = Dir("themes");
        string npy = Result(dir);
        string light = Path.Combine(dir, "light.svg");
        string dark  = Path.Combine(dir, "dark.svg");

        Assert.Equal(0, RunCli("plot", npy, "-o", light, "--type", "surface",
                               "--variant", "light", "--trace", Trace).ExitCode);
        Assert.Equal(0, RunCli("plot", npy, "-o", dark, "--type", "surface",
                               "--variant", "dark", "--trace", Trace).ExitCode);

        var l = File.ReadAllBytes(light);
        var d = File.ReadAllBytes(dark);
        Assert.True(l.Length > 2000, $"the light picture is empty: {l.Length} bytes");
        Assert.True(d.Length > 2000, $"the dark picture is empty: {d.Length} bytes");
        Assert.NotEqual(l, d);
        output.WriteLine($"light {l.Length} bytes, dark {d.Length} bytes");
    }

    /// <summary>
    /// <b>A small pane.</b> Phone width is not applicable here, but the plot has to survive being
    /// squeezed — the layout subtracts a title, a caption block and a colour bar from the canvas
    /// before it has a scene, and each of those can take more than there is.
    /// </summary>
    [Theory]
    [InlineData("140x110")]
    [InlineData("90x220")]
    [InlineData("400x60")]
    public void ItSurvivesASmallPane(string size)
    {
        string dir = Dir("small-" + size.Replace('x', '-'));
        string svg = Path.Combine(dir, "s.svg");
        var r = RunCli("plot", Result(dir), "-o", svg, "--type", "surface", "--size", size,
                       "--title", "A pattern with a fairly long title on it", "--trace", Trace);
        Assert.True(r.ExitCode == 0, r.StdErr + r.StdOut);
        Assert.True(new FileInfo(svg).Length > 500, "nothing was drawn");
    }

    // ══ the refusals ═════════════════════════════════════════════════════════

    [Fact]
    public void AViewFlagOnANonSurfacePlot_IsRefusedRatherThanIgnored()
    {
        string dir = Dir("refuse-view");
        var r = RunCli("plot", Result(dir), "-o", Path.Combine(dir, "p.svg"),
                       "--type", "polar", "--radial", "db", "--view", "broadside",
                       "--trace", "cube=farfield.U,cut=0,y=db10");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("--type surface", r.StdErr);
        output.WriteLine(r.StdErr.Trim());
    }

    [Fact]
    public void AnUnknownView_IsRefusedListingTheOnesThereAre()
    {
        string dir = Dir("refuse-unknown");
        var r = RunCli("plot", Result(dir), "-o", Path.Combine(dir, "p.svg"),
                       "--type", "surface", "--view", "e-plane", "--trace", Trace);
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("iso, broadside, phi0, phi90", r.StdErr);
    }

    [Fact]
    public void AMalformedRotation_IsRefusedWithTheSpellingItWanted()
    {
        string dir = Dir("refuse-rot");
        var r = RunCli("plot", Result(dir), "-o", Path.Combine(dir, "p.svg"),
                       "--type", "surface", "--rotate", "35", "--trace", Trace);
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("--rotate 35,25", r.StdErr);
    }

    /// <summary>A surface has no x and no y, so a window flag is refused for the reason every other
    /// inert flag in this verb is: a picture a caller cannot tell from the one it asked for.</summary>
    [Fact]
    public void AWindowFlagOnASurface_IsRefused()
    {
        string dir = Dir("refuse-window");
        var r = RunCli("plot", Result(dir), "-o", Path.Combine(dir, "p.svg"),
                       "--type", "surface", "--x", "0:90", "--trace", Trace);
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("--view/--rotate/--zoom", r.StdErr.Replace("\n", " "));
    }

    // ══ plumbing ═════════════════════════════════════════════════════════════

    private sealed class OneFile(string path) : IPlotDataSources
    {
        private readonly DataSet _data = DataSetImporter.Import(path).DataSet;

        public string? ResolveAbs(string? sourceRef)  => path;
        public bool    Contains(string absPath)       => true;
        public SNP?    NetworkFor(string absPath)     => null;
        public DataSet? DataFor(string absPath)       => _data;
        public string? AliasFor(string absPath)       => null;
        public string? DisplayNameFor(string absPath) => Path.GetFileName(path);
        public bool     HasMultipleSources            => false;
        public DataSet? SelectedData                  => _data;
    }

    private static (int ExitCode, string StdOut, string StdErr) RunCli(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory       = RepoRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
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
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(
                typeof(Pattern3DCliTests).Assembly)
            .First(a => a.Key == "CliDir").Value!;
        string path = Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll"));
        Assert.True(File.Exists(path), $"the CLI was not built beside these tests: {path}");
        return path;
    }

    private static string RepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is { Length: > 0 } && !File.Exists(Path.Combine(dir, "circuitRF.slnx")))
            dir = Path.GetDirectoryName(dir) ?? "";
        return dir.Length > 0 ? dir : AppContext.BaseDirectory;
    }
}

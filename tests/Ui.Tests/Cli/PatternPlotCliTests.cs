// ================================================================
//  PatternPlotCliTests.cs — ANT-7 §3/§5, the CLI half.
//
//  "Whatever the Data Display gains, the CLI `plot` verb gains the same, because `plot` builds the
//  document `render --data` consumes and hands it to the same composer." So the gate is the one
//  that contract already has: byte identity between the picture `plot` draws and the picture
//  `render` draws from the document `plot` itself wrote — plus the document's own contents, so the
//  two are not agreeing about an empty plot.
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

public sealed class PatternPlotCliTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "crf-ant7-plot-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private string Dir(string name)
    {
        string d = Path.Combine(_root, name);
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>The real far-field run, on disk, as the result file a caller would have.</summary>
    private string Result(string dir)
    {
        string npy = Path.Combine(dir, "run.npy");
        DataSetExporter.Export(PatternFixture.Data, npy, ExportFormat.Npy);
        return npy;
    }

    // ══ the contract: one plotting path ══════════════════════════════════════

    [Theory]
    [InlineData("0")]
    [InlineData("all")]
    public void APatternPlot_IsTheSameBytesAsRenderingTheDocumentItWrote(string cut)
    {
        string dir = Dir("bytes-" + cut);
        string npy = Result(dir);
        string viaPlot   = Path.Combine(Dir("bytes-" + cut + "-a"), "one.svg");
        string viaRender = Path.Combine(Dir("bytes-" + cut + "-b"), "one.svg");
        string cdd       = Path.Combine(dir, "one.cdd");

        var plot = RunCli("plot", npy, "-o", viaPlot, "--write-cdd", cdd,
                          "--type", "polar", "--radial", "db", "--db-unit", "dB(W/sr)",
                          "--trace", $"cube=farfield.U,cut={cut},port=1,y=db10");
        Assert.True(plot.ExitCode == 0, plot.StdErr + plot.StdOut);

        var render = RunCli("render", cdd, "--data", npy, "-o", viaRender);
        Assert.True(render.ExitCode == 0, render.StdErr + render.StdOut);

        Assert.Equal(File.ReadAllBytes(viaPlot), File.ReadAllBytes(viaRender));
        output.WriteLine($"cut={cut}: {new FileInfo(viaPlot).Length} bytes, identical both ways");
    }

    /// <summary>
    /// The document is not an empty picture agreeing with another empty picture: it carries the
    /// radial mode and the cut, spelled over the SAME general slice mechanism a hand-authored `.cdd`
    /// uses.
    /// </summary>
    [Fact]
    public void TheDocumentItWrites_CarriesTheRadialModeAndTheCut()
    {
        string dir = Dir("doc");
        string cdd = Path.Combine(dir, "one.cdd");

        var run = RunCli("plot", Result(dir), "-o", Path.Combine(dir, "p.svg"), "--write-cdd", cdd,
                         "--type", "polar", "--radial", "db",
                         "--db-floor", "-30", "--db-ring", "5", "--db-ref", "3.5", "--db-unit", "dBi",
                         "--trace", "cube=farfield.U,cut=90,port=2,freq=5G,y=db10");
        Assert.True(run.ExitCode == 0, run.StdErr + run.StdOut);

        var pc = JsonSerializer.Deserialize<DataDisplayConfig>(File.ReadAllText(cdd))!.Tabs[0].Plots[0];

        Assert.Equal(PolarRadialMode.Db,            pc.PolarRadial);
        Assert.Equal(PolarDbReferenceMode.Absolute, pc.PolarDbReference);
        Assert.Equal(3.5,   pc.PolarDbReferenceValue);
        Assert.Equal(-30.0, pc.PolarDbFloor);
        Assert.Equal(5.0,   pc.PolarDbRingStep);
        Assert.Equal("dBi", pc.PolarDbUnit);

        var slice = pc.Traces[0].CubeSlice;
        Assert.Equal(["freq", "theta", "phi", "port"], slice.Select(a => a.AxisName).ToArray());
        Assert.Equal(AxisRole.PinToIndex, slice[0].Role);                    // freq pinned
        Assert.Equal(AxisRole.KeepAsX,    slice[1].Role);                    // theta swept
        Assert.Equal(AxisRole.PinToIndex, slice[2].Role);                    // phi = the cut
        Assert.Equal(2,                   slice[2].Index);                   // …at 90°, index 2
        Assert.Equal(AxisRole.PinToIndex, slice[3].Role);
        Assert.Equal(1,                   slice[3].Index);                   // port 2 → index 1
        Assert.Equal(CubeTransform.dB10,  pc.Traces[0].CubeTransform);

        // The verb SAYS what it pinned — a cut silently moved to a neighbouring sample is a plot of
        // a different plane.
        Assert.Contains("cut at phi = 90", run.StdErr);
        Assert.Contains("freq pinned to 5", run.StdErr);
        output.WriteLine(run.StdErr.Trim());
    }

    /// <summary>
    /// <b>cut=all is the whole pattern</b>: θ swept, φ a curve family, everything else pinned — the
    /// input to a contour, and in ANT-10 to the 3D surface.
    /// </summary>
    [Fact]
    public void CutAll_KeepsEveryPhiAsAFamily()
    {
        string dir = Dir("family");
        string cdd = Path.Combine(dir, "one.cdd");

        Assert.Equal(0, RunCli("plot", Result(dir), "-o", Path.Combine(dir, "p.svg"), "--write-cdd", cdd,
                               "--type", "polar", "--radial", "db",
                               "--trace", "cube=farfield.U,cut=all,y=db10").ExitCode);

        var slice = JsonSerializer.Deserialize<DataDisplayConfig>(File.ReadAllText(cdd))!
            .Tabs[0].Plots[0].Traces[0].CubeSlice;

        Assert.Equal(AxisRole.KeepAsX,       slice[1].Role);   // theta
        Assert.Equal(AxisRole.FamilyIterate, slice[2].Role);   // phi
        Assert.Equal(AxisRole.PinToIndex,    slice[3].Role);   // port, at the first one the run holds
    }

    /// <summary>
    /// <b>The Data Display resolves the CLI's own document, and gets the same curve.</b> `render`
    /// proves the two draw identically; this proves the thing drawn is the pattern — the loader the
    /// WINDOW uses, over the document the VERB wrote, against the cube read directly.
    /// </summary>
    [Fact]
    public void TheDataDisplay_ResolvesTheDocumentTheVerbWrote()
    {
        string dir = Dir("resolve");
        string npy = Result(dir);
        string cdd = Path.Combine(dir, "one.cdd");

        Assert.Equal(0, RunCli("plot", npy, "-o", Path.Combine(dir, "p.svg"), "--write-cdd", cdd,
                               "--type", "polar", "--radial", "db",
                               "--trace", "cube=farfield.U,cut=0,port=1,y=db10").ExitCode);

        var config = JsonSerializer.Deserialize<DataDisplayConfig>(File.ReadAllText(cdd))!;
        var plot   = PlotConfigLoader.LoadPlot(config.Tabs[0].Plots[0], new OneFile(npy));

        Assert.True(plot.IsPolarPattern);
        var scale = plot.PatternScale!;
        Assert.True(scale.Normalised);

        var cube = PatternFixture.Data["farfield.U"];
        var t    = plot.Traces[0];
        Assert.Equal(cube.Axes[1].Length, t.Points.Count);

        // Each point is the compass mapping of the cube's own dB value: 0° at the top, clockwise.
        for (int i = 0; i < t.Points.Count; i++)
        {
            double db = DbFloor.Db10(cube[0, i, 0, 0].RealValue!.Value);
            double r  = scale.Radius(db);
            double a  = (90.0 - cube.Axes[1].Values[i]) * Math.PI / 180.0;
            Assert.Equal(r * Math.Cos(a), t.Points[i].X, 4);
            Assert.Equal(r * Math.Sin(a), t.Points[i].Y, 4);
        }
        output.WriteLine("resolved through PlotConfigLoader: " + scale.ReferenceCaption());
    }

    // ══ the refusals ═════════════════════════════════════════════════════════

    [Fact]
    public void ADbRadialOnANonPolarPlot_IsRefused()
    {
        string dir = Dir("refuse-type");
        var r = RunCli("plot", Result(dir), "-o", Path.Combine(dir, "p.svg"),
                       "--radial", "db", "--trace", "cube=farfield.U,cut=0,y=db10");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("--radial db", r.StdErr);
        Assert.Contains("--type polar", r.StdErr);
    }

    [Fact]
    public void ADbOptionWithoutTheMode_IsRefusedRatherThanIgnored()
    {
        string dir = Dir("refuse-inert");
        var r = RunCli("plot", Result(dir), "-o", Path.Combine(dir, "p.svg"), "--type", "polar",
                       "--db-floor", "-20", "--trace", "cube=farfield.Etheta[0,:,0,1]");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("--db-floor", r.StdErr);
        Assert.Contains("would do nothing", r.StdErr);
    }

    /// <summary>The floor is measured relative to the outer ring, so a positive one is a sign error
    /// and is named as one rather than silently flipped.</summary>
    [Fact]
    public void APositiveFloor_IsRefusedAndTheSignConventionIsStated()
    {
        string dir = Dir("refuse-floor");
        var r = RunCli("plot", Result(dir), "-o", Path.Combine(dir, "p.svg"),
                       "--type", "polar", "--radial", "db", "--db-floor", "40",
                       "--trace", "cube=farfield.U,cut=0,y=db10");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("RELATIVE TO THE OUTER RING", r.StdErr);
    }

    [Fact]
    public void ACutOnACubeWithNoPatternAxes_IsRefusedByName()
    {
        string dir = Dir("refuse-cut");
        var r = RunCli("plot", Result(dir), "-o", Path.Combine(dir, "p.svg"),
                       "--type", "polar", "--radial", "db",
                       "--trace", "cube=farfield.GainDbi,cut=0");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("'theta'", r.StdErr);
        Assert.Contains("freq, port", r.StdErr);
    }

    /// <summary>A port the run does not have is refused in the slice parser's own sentence, which
    /// lists the ports it does — never resolved to a neighbouring index.</summary>
    [Fact]
    public void APortTheRunDoesNotHave_IsRefusedListingTheOnesItDoes()
    {
        string dir = Dir("refuse-port");
        var r = RunCli("plot", Result(dir), "-o", Path.Combine(dir, "p.svg"),
                       "--type", "polar", "--radial", "db",
                       "--trace", "cube=farfield.U,cut=0,port=7,y=db10");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("Port 7", r.StdErr);
        output.WriteLine(r.StdErr.Trim());
    }

    // ══ plumbing ═════════════════════════════════════════════════════════════

    /// <summary>One result file, as the resolution seam sees it — the smallest thing that satisfies
    /// what a `.cdd` needs and is not in the `.cdd`.</summary>
    private sealed class OneFile(string path) : IPlotDataSources
    {
        private readonly DataSet _data = DataSetImporter.Import(path).DataSet;

        public string? ResolveAbs(string? sourceRef) => path;
        public bool    Contains(string absPath)      => true;
        public SNP?    NetworkFor(string absPath)    => null;
        public DataSet? DataFor(string absPath)      => _data;
        public string? AliasFor(string absPath)      => null;
        public string? DisplayNameFor(string absPath) => Path.GetFileName(path);
        public bool     HasMultipleSources           => false;
        public DataSet? SelectedData                 => _data;
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
                typeof(PatternPlotCliTests).Assembly)
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

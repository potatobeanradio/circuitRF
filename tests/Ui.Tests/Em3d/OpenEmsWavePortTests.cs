// ================================================================
//  OpenEmsWavePortTests.cs — the gate for brief-em3d-116: a wave port on openEMS, as a fed and probed line.
//  Gates 1-5 and 7 need nothing installed. Gate 6 is OpenEmsBackendTests.Gate4 (the lumped goldens, byte for byte).
//  Gates 8 and 9 run openEMS (Benchmark tier) and skip without a validated one.
//
//  Golden: testdata/em3d/openems-goldens/coax-wave/p1.xml. To rewrite it after a DELIBERATE writer change, run gate 1 with
//  CRF_WRITE_OPENEMS_GOLDENS=1 and review the diff.
// ================================================================

using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Em3d;

[Collection(SkiaFontsTypefaceCollection.Name)]
public sealed class OpenEmsWavePortTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-owave-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private const long Um = 1000;                 // DBU per µm
    private const double C0 = 299_792_458.0;

    // ── 1. the coax's XML ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate1_Coax_GrowsTheBoxByTheFeed_ExtrudesThroughIt_AndWritesTheRadialSourceAndFiveProbesOnTheGrid()
    {
        var (problem, grid, low) = Lower(Coax(), CoaxSetup(cellsPerWavelength: 400));
        var feed = Assert.Single(grid.WavePorts.Feeds);
        var z = grid.Z.Lines;
        output.WriteLine($"feed {feed.FeedM * 1e3:F4} mm in cells of {feed.CellM * 1e6:F2} µm; extension {feed.ExtensionM * 1e3:F4} mm; {feed.LengthSetBy}");
        foreach (var n in low.Notes) output.WriteLine("note: " + n);

        // the box grown on zmin by the feed, PML behind it
        Assert.Equal(feed.OuterAtM, z[8]);
        Assert.Equal(-(1 - feed.SourceIndex) * feed.CellM, z[8], 12);
        Assert.True(feed.FeedM >= FdtdWavePorts.FeedSMax * feed.SMaxM - 1e-12 && feed.ReferenceIndex - feed.SourceIndex >= 10);
        var doc = XDocument.Parse(low.PortFiles[0]);
        Assert.Equal("PML_8", doc.Descendants("BoundaryCond").Single().Attribute("zmin")!.Value);
        Assert.Contains(low.Notes, n => n.Contains("The zmin face carries wave port 'P1', so openEMS terminates it in PML behind a") &&
                                       n.Contains("the setup's PEC applies to Palace only"));

        // the pin (a cylinder), the PTFE and the shield run from the grid's edge
        var pin = doc.Descendants("Metal").Single(e => e.Attribute("Name")!.Value == "pin").Descendants("P1").Single();
        Assert.Equal(z[0], D(pin, "Z"));
        Assert.Equal(z[0], D(doc.Descendants("Material").Single(e => e.Attribute("Name")!.Value == "ptfe").Descendants("P1").Single(), "Z"));
        Assert.Equal(z[0], D(doc.Descendants("Metal").Single(e => e.Attribute("Name")!.Value == "shield").Descendants("LinPoly").Single(), "Elevation"));

        // the weighted radial source, in run 1's file only
        Assert.DoesNotContain("<Excitation ID", low.Model);
        var exc = doc.Descendants("Excitation").Single(e => e.Parent!.Name == "Properties");
        Assert.Equal("port1_excite", exc.Attribute("Name")!.Value);
        var w = exc.Element("Weight")!;
        Assert.Equal("0", w.Attribute("Z")!.Value);
        Assert.Contains("sqrt", w.Attribute("X")!.Value);
        Assert.Contains("sqrt", w.Attribute("Y")!.Value);
        Assert.All(exc.Descendants("P1").Concat(exc.Descendants("P2")), p => Assert.Equal(feed.SourceAtM, D(p, "Z")));

        // three voltage planes a cell apart, the middle on the reference plane (the face); currents between them
        double h = feed.CellM;
        double Zof(string name) => D(Probe(doc, name).Descendants("P1").Single(), "Z");
        Assert.Equal((-h, 0, h), (Zof("port1_ua"), Zof("port1_u"), Zof("port1_uc")));
        Assert.Equal((-h / 2, h / 2), (Zof("port1_ia"), Zof("port1_ib")));
        Assert.Equal(("1", "2"), (Probe(doc, "port1_ia").Attribute("Weight")!.Value, Probe(doc, "port1_ia").Attribute("NormDir")!.Value));

        // the current box clear of the shield by a cell, corners included
        var t = Assert.Single(low.WaveTerminals);
        Assert.Equal(FdtdTerminalShape.Coaxial, t.Terminal.Shape);
        Assert.Equal("shield", t.NearestConductor);
        Assert.True(t.ClearanceCells >= 1, $"clearance {t.ClearanceCells} cells");
        double corner = Math.Sqrt(t.CurrentBox.U1 * t.CurrentBox.U1 + t.CurrentBox.V1 * t.CurrentBox.V1);
        Assert.True(BoreR - corner >= Cell(grid.X.Lines, corner / Math.Sqrt(2)), $"corner at {corner * 1e6:F1} µm");

        // every source and probe coordinate an exact written grid line or the midpoint of two
        foreach (var e in doc.Descendants("Properties").Elements().Where(e => e.Name == "Excitation" || (e.Name == "ProbeBox" && e.Attribute("Name")!.Value.StartsWith("port1_"))))
            foreach (var p in e.Descendants().Where(x => x.Name == "P1" || x.Name == "P2"))
                foreach (var (axis, lines) in new[] { ("X", grid.X.Lines), ("Y", grid.Y.Lines), ("Z", grid.Z.Lines) })
                    Assert.True(OnGrid(lines, D(p, axis)), $"{e.Attribute("Name")!.Value} {axis} = {p.Attribute(axis)!.Value} is on no line or dual line");

        // the 3D view's grid overlay marks the source plane and the reference plane (R-em3d116-5)
        var drawing = CircuitRF.Render.Scene3D.FdtdGridOverlay.Build(grid, CircuitRF.Render.Scene3D.Scene3DBuilder.Build(problem, 1), default);
        Assert.Equal([$"openEMS source of 'P1' at z = {FdtdGrid.FormatLength(feed.SourceAtM)}", "reference plane of 'P1' at z = 0 µm"],
                     drawing.FeedLabels.Select(l => l.Text));

        string file = Path.Combine(PalaceBackendTests.RepoRoot(), "testdata", "em3d", "openems-goldens", "coax-wave", "p1.xml");
        if (Environment.GetEnvironmentVariable("CRF_WRITE_OPENEMS_GOLDENS") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, System.Text.Encoding.UTF8.GetBytes(low.PortFiles[0]));
        }
        Assert.Equal(File.ReadAllText(file), low.PortFiles[0]);
    }

    // ── 2. the stripline pair's XML against 113-a's ─────────────────────────────────────────────

    /// <summary>
    /// Four terminals (two two-terminal ports), each with two-sided sources and up/down voltage halves, against 113-a's
    /// pair-a/exc1 (written in this writer's shape and replayed against openEMS's own interface): the same element kinds and
    /// attribute names, and the same placement rules — checked by ONE rule checker run on both files.
    /// </summary>
    [Fact]
    public void Gate2_Pair_FourTerminals_HaveTheElementKindsAttributesAndPlacementOf113a()
    {
        var (_, grid, low) = Lower(Pair(), PairSetup());
        Assert.Equal([1, 2, 3, 4], low.Ports);
        Assert.All(low.WaveTerminals, t => Assert.Equal(FdtdTerminalShape.Stripline, t.Terminal.Shape));
        var ours = XDocument.Parse(low.PortFiles[0]);
        var theirs = XDocument.Load(Path.Combine(PalaceBackendTests.RepoRoot(), "testdata", "em3d", "terminal", "openems", "pair-a", "exc1", "model.xml"));

        static SortedSet<string> Kinds(XDocument d) => new(d.Descendants("Properties").Elements().Select(e => e.Name.LocalName), StringComparer.Ordinal);
        Assert.Equal(Kinds(theirs), Kinds(ours));
        foreach (string kind in new[] { "ProbeBox", "Excitation" })
            Assert.Equal(Attributes(theirs, kind), Attributes(ours, kind));

        // ours: port<k>_{ua,u,uc}_{up,dn}, port<k>_{ia,ib}, port<k>_excite_{up,dn}; theirs: port_ut_<k>{A,B,C}{1,2}, port_it_<k>{A,B}, port_excite_{1,2}_<k>
        for (int k = 1; k <= 4; k++)
        {
            Rules(ours, [.. new[] { "ua", "u", "uc" }.Select(p => new[] { $"port{k}_{p}_up", $"port{k}_{p}_dn" })], [$"port{k}_ia", $"port{k}_ib"],
                  k == 1 ? [$"port{k}_excite_up", $"port{k}_excite_dn"] : null, k % 2 == 1 ? 1 : -1);
            Rules(theirs, [.. "ABC".Select(p => new[] { $"port_ut_{k}{p}1", $"port_ut_{k}{p}2" })], [$"port_it_{k}A", $"port_it_{k}B"],
                  k == 1 ? ["port_excite_1_1", "port_excite_2_1"] : null, k % 2 == 1 ? 1 : -1);
        }
        _ = grid;
    }

    /// <summary>The placement rules of a line port's elements, in either file's names.</summary>
    private void Rules(XDocument d, string[][] voltage, string[] current, string[]? sources, int inward)
    {
        // three planes, equally spaced, ordered from the outside in; each plane's two halves at one point across the strip,
        // one up from the strip and one down, both reading V(strip) − V(ground)
        var planes = voltage.Select(pair => pair.Select(n => Box(d, n)).ToArray()).ToArray();
        double[] x = [.. planes.Select(p => p[0].P1.X)];
        Assert.Equal(x[1] - x[0], x[2] - x[1], 9);
        Assert.Equal(inward, Math.Sign(x[2] - x[0]));
        foreach (var pair in voltage)
        {
            var halves = pair.Select(n => (Box: Box(d, n), Weight: double.Parse(Probe(d, n).Attribute("Weight")!.Value))).ToArray();
            Assert.Equal(halves[0].Box.P1.Y, halves[1].Box.P1.Y);
            // orientation: +1 when the probe reads V(strip) − V(ground); the strip is the end at z = 0
            int Orientation((Point3 P1, Point3 P2) b, double weight) => Math.Sign(weight) * (Math.Abs(b.P1.Z) < Math.Abs(b.P2.Z) ? 1 : -1);
            Assert.All(halves, hv => Assert.Equal(1, Orientation(hv.Box, hv.Weight)));
            Assert.Equal(-1, Math.Sign(halves[0].Box.P1.Z + halves[0].Box.P2.Z) * Math.Sign(halves[1].Box.P1.Z + halves[1].Box.P2.Z));
        }
        // two current planes at the midpoints, normal to x, I into the device, the strip inside the loop with room around it
        var ia = Box(d, current[0]);
        var ib = Box(d, current[1]);
        Assert.Equal((x[0] + x[1]) / 2, ia.P1.X, 12);
        Assert.Equal((x[1] + x[2]) / 2, ib.P1.X, 12);
        foreach (string c in current)
        {
            var p = Probe(d, c);
            Assert.Equal(("1", "0", inward), (p.Attribute("Type")!.Value, p.Attribute("NormDir")!.Value, int.Parse(p.Attribute("Weight")!.Value)));
            var b = Box(d, c);
            Assert.True(b.P1.Z < 0 && b.P2.Z > 0, "the loop straddles the strip");
            Assert.True(b.P1.Y < planes[1][0].P1.Y && b.P2.Y > planes[1][0].P1.Y, "the loop encloses the strip's centre");
        }
        if (sources is null) return;
        // two sheets behind the reference plane, across the strip, strip to each ground, the field pointing away from the strip
        foreach (string s in sources)
        {
            var e = d.Descendants("Excitation").Single(x => x.Parent!.Name == "Properties" && x.Attribute("Name")!.Value == s);
            var b = Prim(e.Descendants("Box").Single());
            Assert.Equal(b.P1.X, b.P2.X);
            Assert.True(inward * (x[0] - b.P1.X) > 0, "the source is behind the voltage planes");
            Assert.Equal(0, Math.Min(Math.Abs(b.P1.Z), Math.Abs(b.P2.Z)));
            double far = Math.Abs(b.P1.Z) > Math.Abs(b.P2.Z) ? b.P1.Z : b.P2.Z;
            var ex = e.Attribute("Excite")!.Value.Split(',').Select(double.Parse).ToArray();
            Assert.Equal((0.0, 0.0, (double)Math.Sign(far)), (ex[0], ex[1], ex[2]));
            // across the strip: over its centre, and its width to within a cell (theirs: exactly the strip; ours: the lines inside it)
            Assert.InRange(planes[1][0].P1.Y, Math.Min(b.P1.Y, b.P2.Y), Math.Max(b.P1.Y, b.P2.Y));
            Assert.InRange(Math.Abs(b.P2.Y - b.P1.Y), StripW - 2 * 150e-6, StripW + 1e-12);
        }
    }

    // ── 3, 4. 113-a's pair through OpenEmsRun's reader and the unchanged transform, and against its 2D reference ─────────

    [Fact]
    public void Gate3_PairReplay_ThroughTheReaderAndSolve_Is113asRecordedMatrix()
    {
        var s = PairReplay();
        var s21 = s[1, 0];
        output.WriteLine($"S21 {Db(s21):F4} dB / {Deg(s21):F3}°");
        Assert.Equal(-1.3013, Db(s21), 3);
        Assert.InRange(Deg(s21), -89.029 - 0.01, -89.029 + 0.01);
    }

    [Fact]
    public void Gate4_PairReplay_AgainstTheIdealCoupledLineOf113asTwoDimensionalSolve()
    {
        var s = PairReplay();
        var r = CoupledLine(104.08, 73.58, PairPlanesM, 5e9, 50);
        double worst = 0;
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++) worst = Math.Max(worst, (s[i, j] - r[i, j]).Magnitude);
        foreach (var (name, i, j, db) in new[] { ("thru", 1, 0, 0.1), ("thru", 3, 2, 0.1), ("near-end", 2, 0, 0.5), ("near-end", 3, 1, 0.5),
                                                 ("far-end", 3, 0, 0.5), ("far-end", 2, 1, 0.5) })
        {
            output.WriteLine($"{name} S{i + 1}{j + 1}: {Db(s[i, j]):F3} dB {Deg(s[i, j]):F2}°  ref {Db(r[i, j]):F3} dB {Deg(r[i, j]):F2}°");
            Assert.True(Math.Abs(Db(s[i, j]) - Db(r[i, j])) <= db, $"{name} magnitude");
            Assert.True(Math.Abs(Phase(s[i, j] / r[i, j])) <= 1, $"{name} phase");
        }
        output.WriteLine($"max |ΔS| {worst:F4}");
        Assert.True(worst <= 0.015);
    }

    // ── 5. refusals, before any process ─────────────────────────────────────────────────────────

    [Fact]
    public void Gate5_AHollowWaveguide_IsRefusedByTheLowering_NamingTheSolversThatRunIt()
    {
        var problem = PalaceEigenTests.Wr90(50);
        var gs = OpenEmsGridSettings.Default;
        var grid = FdtdGrid.Build(problem, gs, long.MaxValue);
        var low = CsxcadWriter.Write(problem, grid, gs, CemOpenEms.ResolveRun(null));
        Assert.Equal("Port 1 is a wave port met by one conductor (a hollow waveguide). openEMS needs a mode-matching port for that, " +
                     "which circuitRF does not build yet; EMerge and Palace run it. Set the setup's Solver3D to EMerge or Palace, or run " +
                     "it with `circuitrf em --solver emerge`.", low.Refusal);
    }

    [Fact]
    public void Gate5_ACurrentBoxTouchingTheOtherStrip_IsRefused_NamingItAndTheClearance()
    {
        var (problem, grid) = Problem(Pair(gapUm: 100), PairSetup(cellsPerWavelength: 60));
        var low = CsxcadWriter.Write(problem, grid, OpenEmsGridSettings.Default with { CellsPerWavelength = 60 }, CemOpenEms.ResolveRun(null));
        output.WriteLine(low.Refusal);
        Assert.Matches("^Terminal P1's current probe on the xmin face would (touch|come within .* of) 'strip_b'", low.Refusal);
        Assert.Contains("It needs a clearance of one cell", low.Refusal);
    }

    // ── explain (R-em3d116-3) ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Explain_OnOpenEms_PrintsEachFeed_ItsPlanes_AndEachTerminalsSourceAndCurrentLoop()
    {
        var doc = Pair();
        doc.Setups = [EmSetupPersistence.ToEmbedded(PairSetup())];
        var (code, stdout, _) = RunCli("explain", WriteC3d(Workspace(), "Pair", doc));
        Assert.Equal(0, code);
        Assert.Contains("  openEMS wave ports", stdout);
        // 37 cells of λ/300 at 8 GHz (124.914 µm) is the first whole number of cells past 4.5 × the 1 mm strip-to-plane distance
        Assert.Contains("xmin face ('Left'): feed 4.6218 mm from the source to the reference plane, 4.5 × s_max (1 mm, ", stdout);
        Assert.Contains("source plane at x = -4.6218 mm; voltage planes at x = -124.914 µm, 0 µm (the reference plane) and 124.914 µm.", stdout);
        Assert.Matches(@"terminal P1 \('strip_a', port 1\): a strip between two reference planes: .*current planes at x = -62\.457 µm and 62\.457 µm, " +
                       @"the loop y .* clear of 'strip_b' by 2\.12 cells", stdout);
        Assert.DoesNotContain("The faces beside", stdout);      // the PEC top and bottom are the strips' own reference planes
    }

    // ── 7. the transform is untouched ───────────────────────────────────────────────────────────

    [Fact]
    public void Gate7_FdtdPortTransform_IsUnchanged()
    {
        string path = Path.Combine(PalaceBackendTests.RepoRoot(), "src", "Engine", "Em3d", "FdtdPortTransform.cs");
        string text = File.ReadAllText(path).ReplaceLineEndings("\n");
        // The file as brief-em3d-9 left it: brief 116 assembles its S without one edit here (R-em3d116-2d).
        Assert.Equal("cdabd13a306d5a98e121beb7f9daf972d20676297b94157f9a70a95de8d96f03",
                     Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))));
    }

    // ── 8. the coax end to end, through `circuitrf em` ──────────────────────────────────────────

    /// <summary>
    /// The coax at r_i/10 (20 µm cells) on a Cartesian grid: the line's measured Z within 0.6 Ω of the closed form with η₀
    /// (50.021 Ω; 113-a measured 50.39–50.56 at r_i/10), and the run report carrying its ε_eff. The 1° phase criterion is
    /// brief 120's: a Cartesian staircase runs the line slow (113-a: 1–2.4 %).
    /// </summary>
    [OpenEmsFact]
    [Trait("Category", "Benchmark")]
    public void Gate8_Coax_EndToEnd_TheLineMeasuresWithin0p6OhmOfItsClosedForm()
    {
        var doc = Coax();
        doc.Setups = [EmSetupPersistence.ToEmbedded(CoaxSetup(cellsPerWavelength: 575))];
        string path = WriteC3d(Workspace(), "Coax", doc);
        var watch = Stopwatch.StartNew();
        var (code, stdout, stderr) = RunCli("em", path);
        output.WriteLine($"{watch.Elapsed.TotalSeconds:F1} s");
        Assert.True(code == 0, stdout + stderr);
        var m = Regex.Match(stderr, @"note: Terminal P1: the line measured Z ([0-9.]+) Ω, ε_eff ([0-9.]+) at ([0-9.]+) GHz\.");
        Assert.True(m.Success, stderr);
        double z = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        double closed = 376.730313668 / (2 * Math.PI * Math.Sqrt(2.1)) * Math.Log(BoreR / PinR);
        output.WriteLine($"Z {z} Ω against {closed:F3} Ω; ε_eff {m.Groups[2].Value} at {m.Groups[3].Value} GHz");
        Assert.InRange(z, closed - 0.6, closed + 0.6);
        Assert.InRange(double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture), 2.0, 2.5);
    }

    // ── 9. the pair end to end ──────────────────────────────────────────────────────────────────

    /// <summary>Gate 4's stripline pair drawn as a .c3d and run, against the same 2D reference (the reference planes 15 mm
    /// apart), at 2, 5 and 8 GHz: thru within 0.1 dB / 1°, near- and far-end within 0.5 dB / 1°, max |ΔS| ≤ 0.015.</summary>
    [OpenEmsFact]
    [Trait("Category", "Benchmark")]
    public void Gate9_Pair_EndToEnd_AgainstTheIdealCoupledLine()
    {
        var doc = Pair();
        string ws = Workspace();
        string path = WriteC3d(ws, "Pair", doc);
        var watch = Stopwatch.StartNew();
        var run = EmRunService.RunThreeDView(PairSetup(), doc, path, Path.Combine(ws, ".cws"), Path.Combine(_root, "results"));
        output.WriteLine($"{watch.Elapsed.TotalSeconds:F1} s");
        foreach (string n in run.Notes ?? []) output.WriteLine("note: " + n);
        foreach (string w in run.Warnings) output.WriteLine("warning: " + w);
        Assert.True(run.Status == EmRunStatus.Ok, run.Error);
        var snp = RfCore.TouchstoneIO.ReadFile(run.SnpPath!);
        for (int k = 0; k < snp.Frequencies.Length; k++)
        {
            var r = CoupledLine(104.08, 73.58, 15e-3, snp.Frequencies[k], 50);
            double worst = 0;
            for (int i = 0; i < 4; i++)
                for (int j = 0; j < 4; j++) worst = Math.Max(worst, (snp[k][i, j] - r[i, j]).Magnitude);
            output.WriteLine($"{snp.Frequencies[k] / 1e9} GHz: max |ΔS| {worst:F4}");
            foreach (var (name, i, j, db) in new[] { ("thru", 1, 0, 0.1), ("thru", 3, 2, 0.1), ("near-end", 2, 0, 0.5), ("near-end", 3, 1, 0.5),
                                                     ("far-end", 3, 0, 0.5), ("far-end", 2, 1, 0.5) })
            {
                var sv = snp[k][i, j];
                output.WriteLine($"  {name} S{i + 1}{j + 1}: {Db(sv):F3} dB {Deg(sv):F2}°  ref {Db(r[i, j]):F3} dB {Deg(r[i, j]):F2}°");
                Assert.True(Math.Abs(Db(sv) - Db(r[i, j])) <= db, $"{name} S{i + 1}{j + 1} magnitude at {snp.Frequencies[k] / 1e9} GHz");
                Assert.True(Math.Abs(Phase(sv / r[i, j])) <= 1, $"{name} S{i + 1}{j + 1} phase at {snp.Frequencies[k] / 1e9} GHz");
            }
            Assert.True(worst <= 0.015, $"max |ΔS| {worst} at {snp.Frequencies[k] / 1e9} GHz");
        }
    }

    // ── 10. brief-em3d-122: the pair whose thirds pairs the fill used to split ────────────────

    /// <summary>
    /// Brief 117's first Pair section — the shipped example's .c3d with W 1.6 mm and S 0.3 mm, its own openEMS setup (2-18 GHz
    /// in 33 points, 90 cells per wavelength, MinCellUm 10, PMC sides 10·W out, PEC top and bottom) — against Cohn's coupled
    /// line (Z₀e 59.80, Z₀o 41.89 Ω, 15 mm): thru within 0.1 dB / 1°, near- and far-end within 0.5 dB / 1° where the ideal
    /// entry is above −30 dB, max |ΔS| ≤ 0.015 at every frequency. With the gap's middle halved it was 0.26 and the far end
    /// −12.6 dB at 18 GHz against −75 dB.
    /// </summary>
    [OpenEmsFact]
    [Trait("Category", "Benchmark")]
    public void Gate10_TheS0p3Pair_IsCohnsCoupledLine()
    {
        string example = Path.Combine(ExampleWorkspaces.ResolveRoot(PalaceBackendTests.RepoRoot()) ?? throw new InvalidOperationException("no examples/"),
                                      "3D Wave Ports", "Pair", "3d", "Pair.c3d");
        var doc = C3dPersistence.LoadFromFile(example);
        doc.Variables.Single(v => v.Name == "w").Expression = "1.6";
        doc.Variables.Single(v => v.Name == "s").Expression = "0.3";
        foreach (var port in doc.Ports) port.Rect = Rect(-17750, -1000, 35500, 2000);     // ±(s/2 + 11·w), the fill's sides
        string ws = Workspace();
        string path = WriteC3d(ws, "Pair", doc);
        var (embedded, why) = C3dSetups.Select(doc, "openEMS");
        Assert.True(embedded is not null, why);
        var watch = Stopwatch.StartNew();
        var run = EmRunService.RunThreeDView(C3dSetups.ForRun(embedded!, path), doc, path, Path.Combine(ws, ".cws"),
                                             Path.Combine(_root, "results"), confirmMemory: _ => true);
        output.WriteLine($"{watch.Elapsed.TotalSeconds:F1} s");
        Assert.True(run.Status == EmRunStatus.Ok, run.Error);

        var (ze, zo) = Examples.Em3dWavePortsExampleTests.Cohn(1.6, 0.3, 2, 2.1);
        output.WriteLine($"Cohn: Z0e {ze:F2} Ω, Z0o {zo:F2} Ω");
        Assert.Equal((59.80, 41.89), (Math.Round(ze, 2), Math.Round(zo, 2)));
        var snp = RfCore.TouchstoneIO.ReadFile(run.SnpPath!);
        var bad = new List<string>();
        for (int k = 0; k < snp.Frequencies.Length; k++)
        {
            double f = snp.Frequencies[k];
            var c = Examples.Em3dWavePortsExampleTests.CoupledLine(ze, zo, 15, 2.1, f, 50);   // refl, near, thru, far
            // 1, 2 at x = 0 (strips a, b); 3, 4 at x = 15 mm.
            int[,] which = { { 0, 1, 2, 3 }, { 1, 0, 3, 2 }, { 2, 3, 0, 1 }, { 3, 2, 1, 0 } };
            double worst = 0;
            for (int i = 0; i < 4; i++)
                for (int j = 0; j < 4; j++) worst = Math.Max(worst, (snp[k][i, j] - c[which[i, j]]).Magnitude);
            output.WriteLine($"{f / 1e9,5} GHz max |ΔS| {worst:F4}  far-end {Db(snp[k][3, 0]):F1} dB (ideal {Db(c[3]):F1})");
            if (worst > 0.015) bad.Add($"max |ΔS| {worst:F4} at {f / 1e9} GHz");
            foreach (var (name, i, tol) in new[] { ("near-end S21", 1, 0.5), ("thru S31", 2, 0.1), ("far-end S41", 3, 0.5) })
            {
                if (Db(c[i]) < -30) continue;
                var sv = snp[k][i, 0];
                if (Math.Abs(Db(sv) - Db(c[i])) > tol || Math.Abs(Phase(sv / c[i])) > 1)
                    bad.Add($"{name} at {f / 1e9} GHz: {Db(sv) - Db(c[i]):+0.000;-0.000} dB, {Phase(sv / c[i]):+0.00;-0.00}°");
            }
        }
        Assert.True(bad.Count == 0, string.Join("\n", bad));
    }

    private (int ExitCode, string StdOut, string StdErr) RunCli(params string[] args)
    {
        string cliDir = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(OpenEmsWavePortTests).Assembly)
            .First(a => a.Key == "CliDir").Value!;
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add(Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll")));
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        string stdout = outTask.GetAwaiter().GetResult(), stderr = errTask.GetAwaiter().GetResult();
        output.WriteLine($"$ {string.Join(' ', args)}\n{stdout}{stderr}");
        return (proc.ExitCode, stdout, stderr);
    }

    // ══ fixtures ════════════════════════════════════════════════════════════════════════════════

    private const double PinR = 200e-6, BoreR = 670e-6, StripW = 1.2e-3;

    /// <summary>The 3D Connector's coax along z: pin ⌀ 0.4 mm, PTFE bore ⌀ 1.34 mm in a 1.4 mm square shield, 1 mm long, a wave
    /// port on the zmin face with its path from the shield to the pin.</summary>
    internal static C3dDocument Coax() => new()
    {
        Objects =
        [
            new C3dPrism
            {
                Name = "shield", Material = "Copper", Plane = C3dPlane.XY, Offset = 0, Height = 1000 * Um,
                Outline = [P(-700, -700), P(700, -700), P(700, 700), P(-700, 700)],
                Holes = [[.. Enumerable.Range(0, 64).Select(k => new C3dPoint2((long)Math.Round(670 * Um * Math.Cos(2 * Math.PI * k / 64)),
                                                                              (long)Math.Round(670 * Um * Math.Sin(2 * Math.PI * k / 64))))]],
            },
            new C3dCylinder { Name = "ptfe", Material = "PTFE", Base = new C3dPoint3(0, 0, 0), Axis = C3dAxis.Z, Length = 1000 * Um, Radius = 670 * Um },
            new C3dCylinder { Name = "pin", Material = "Copper", Base = new C3dPoint3(0, 0, 0), Axis = C3dAxis.Z, Length = 1000 * Um, Radius = 200 * Um },
        ],
        Ports =
        [
            new C3dPort
            {
                Number = 1, Name = "P1", Kind = Em3dPortKind.Wave, Plane = C3dPlane.XY, Offset = 0, Rect = Rect(-700, -700, 1400, 1400),
                Positive = "pin", Negative = "shield",
                VoltagePath = new C3dVoltagePath { From = P(670, 0), To = P(200, 0) },
            },
        ],
    };

    /// <summary>The coax's setup: PEC walls on the shield, the port face stated PEC (D9 lowers it absorbing), the far end absorbing.</summary>
    internal static EmSetup CoaxSetup(double cellsPerWavelength) => new()
    {
        Name = "S1", Solver3D = Em3dSolverId.OpenEms,
        Frequency = new CircuitRF.Core.Design.FrequencySpec("2", "18", 5, CircuitRF.Core.Design.SweepKind.Linear, "GHz", "GHz"),
        AirBox = new EmAirBox(new EmAirBoxFace(0, Em3dBoundaryKind.Pec), new EmAirBoxFace(0, Em3dBoundaryKind.Pec),
                              new EmAirBoxFace(0, Em3dBoundaryKind.Pec), new EmAirBoxFace(0, Em3dBoundaryKind.Pec),
                              new EmAirBoxFace(0, Em3dBoundaryKind.Pec), new EmAirBoxFace(0, Em3dBoundaryKind.Absorbing)),
        OpenEms = new CemOpenEms { CellsPerWavelength = cellsPerWavelength, MinCellUm = 2, SaveFieldsGHz = [] },
    };

    /// <summary>
    /// 113-a's geometry A as a .c3d: an air stripline pair, t = 0 (PEC sheets), W 1.2 mm, gap S, 15 mm long, between the box's
    /// PEC top and bottom 1 mm above and below, PMC sides at ±(10·W + (S+W)/2) (113-a's openEMS box). A two-terminal wave port
    /// at each end, numbered as 113-a's pair-a: 1 and 3 on the left, 2 and 4 on the right.
    /// </summary>
    private static C3dDocument Pair(long gapUm = 400)
    {
        long half = gapUm / 2;
        C3dSheet Strip(string name, long y0) => new()
        {
            Name = name, Material = "PEC", Plane = C3dPlane.XY, Offset = 0, Rect = Rect(0, y0, 15000, 1200),
        };
        C3dPort End(string name, long x, int a, int b) => new()
        {
            Name = name, Kind = Em3dPortKind.Wave, Plane = C3dPlane.YZ, Offset = x * Um, Rect = Rect(-SideUm(gapUm), -1000, 2 * SideUm(gapUm), 2000),
            Reference = "airbox/zmin",
            Terminals = [new C3dTerminal { Number = a, Name = $"P{a}", Conductor = "strip_a", Z0 = "50" },
                         new C3dTerminal { Number = b, Name = $"P{b}", Conductor = "strip_b", Z0 = "50" }],
        };
        return new C3dDocument
        {
            Objects = [Strip("strip_a", -half - 1200), Strip("strip_b", half)],
            Ports = [End("Left", 0, 1, 3), End("Right", 15000, 2, 4)],
        };
    }

    private static long SideUm(long gapUm) => 12000 + (gapUm + 1200) / 2;

    private static EmSetup PairSetup(double cellsPerWavelength = 300, long gapUm = 400)
    {
        double side = SideUm(gapUm) - (gapUm / 2 + 1200);
        return new EmSetup
        {
            Name = "S1", Solver3D = Em3dSolverId.OpenEms,
            Frequency = new CircuitRF.Core.Design.FrequencySpec("2", "8", 3, CircuitRF.Core.Design.SweepKind.Linear, "GHz", "GHz"),
            AirBox = new EmAirBox(new EmAirBoxFace(0, Em3dBoundaryKind.Absorbing), new EmAirBoxFace(0, Em3dBoundaryKind.Absorbing),
                                  new EmAirBoxFace(side, Em3dBoundaryKind.Pmc), new EmAirBoxFace(side, Em3dBoundaryKind.Pmc),
                                  new EmAirBoxFace(1000, Em3dBoundaryKind.Pec), new EmAirBoxFace(1000, Em3dBoundaryKind.Pec)),
            OpenEms = new CemOpenEms { CellsPerWavelength = cellsPerWavelength, MinCellUm = 5, SaveFieldsGHz = [] },
        };
    }

    /// <summary>113-a pair-a's reference planes: port 1's middle voltage plane at x = −7.4013 mm, port 2's mirrored.</summary>
    private const double PairPlanesM = 2 * 0.007401315789473689;

    /// <summary>113-a's pair-a probe files, each port's U the mean of its middle plane's two halves and I the mean of its two
    /// current planes (OpenEmsRun.ReadPort), all four runs through FdtdPortTransform.Solve at 5 GHz, 50 Ω.</summary>
    private static Complex[,] PairReplay()
    {
        string dir = Path.Combine(PalaceBackendTests.RepoRoot(), "testdata", "em3d", "terminal", "openems", "pair-a");
        var runs = new List<IReadOnlyList<FdtdPortProbes>>();
        for (int k = 1; k <= 4; k++)
        {
            var ports = new List<FdtdPortProbes>();
            for (int i = 1; i <= 4; i++)
            {
                var names = new OpenEmsProbeNames(i, [$"port_ut_{i}B1", $"port_ut_{i}B2"], [$"port_it_{i}A", $"port_it_{i}B"]);
                var read = OpenEmsRun.ReadPort(Path.Combine(dir, $"exc{k}"), names, out string? error);
                Assert.True(read is not null, error);
                ports.Add(read!);
            }
            runs.Add(ports);
        }
        var r = FdtdPortTransform.Solve(runs, [5e9], [1, 2, 3, 4], [50, 50, 50, 50]);
        Assert.Null(r.Error);
        var s = new Complex[4, 4];
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++) s[i, j] = r.S[0][i, j];
        return s;
    }

    /// <summary>
    /// The ideal symmetric coupled line in air, terminated in <paramref name="z0"/> on every strip, ports numbered 1/2 on one
    /// strip's two ends and 3/4 on the other's: the even and odd two-ports, combined.
    /// </summary>
    internal static Complex[,] CoupledLine(double zEven, double zOdd, double lengthM, double f, double z0)
    {
        (Complex S11, Complex S21) Line(double zc)
        {
            double th = 2 * Math.PI * f * lengthM / C0;
            var den = 2 * zc * z0 * Math.Cos(th) + Complex.ImaginaryOne * (zc * zc + z0 * z0) * Math.Sin(th);
            return (Complex.ImaginaryOne * (zc * zc - z0 * z0) * Math.Sin(th) / den, 2 * zc * z0 / den);
        }
        var (e11, e21) = Line(zEven);
        var (o11, o21) = Line(zOdd);
        Complex refl = (e11 + o11) / 2, near = (e11 - o11) / 2, thru = (e21 + o21) / 2, far = (e21 - o21) / 2;
        // rows/columns: 1 = strip a left, 2 = strip a right, 3 = strip b left, 4 = strip b right
        return new[,]
        {
            { refl, thru, near, far },
            { thru, refl, far, near },
            { near, far, refl, thru },
            { far, near, thru, refl },
        };
    }

    // ── lowering ────────────────────────────────────────────────────────────────────────────────

    private (Em3dProblem Problem, FdtdGridResult Grid, CsxcadLowering Low) Lower(C3dDocument doc, EmSetup setup)
    {
        var (problem, grid) = Problem(doc, setup);
        var low = CsxcadWriter.Write(problem, grid, CemOpenEms.ResolveGrid(setup.OpenEms), CemOpenEms.ResolveRun(setup.OpenEms));
        Assert.True(low.Ok, low.Refusal);
        return (problem, grid, low);
    }

    private (Em3dProblem Problem, FdtdGridResult Grid) Problem(C3dDocument doc, EmSetup setup)
    {
        string ws = Workspace();
        string path = WriteC3d(ws, "Cell", doc);
        var r = C3dProblemAssembly.Assemble(setup, doc, path, Path.Combine(ws, ".cws"), new C3dElaborator());
        Assert.True(r.Ok, r.Refusal);
        var grid = FdtdGrid.Build(r.Problem!, CemOpenEms.ResolveGrid(setup.OpenEms), long.MaxValue);
        output.WriteLine($"grid {grid.X.Lines.Count} × {grid.Y.Lines.Count} × {grid.Z.Lines.Count} = {grid.Cells:N0} cells");
        return (r.Problem!, grid);
    }

    /// <summary>A workspace whose default technology holds Copper, PEC, PTFE and Air.</summary>
    private string Workspace()
    {
        string ws = Path.Combine(_root, "ws" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech",
            Materials =
            [
                new TechMaterial { Name = "Copper", Sigma20 = 5.8e7 }, new TechMaterial { Name = "PEC", Sigma20 = 1e30 },
                new TechMaterial { Name = "PTFE", Epsr = 2.1 }, new TechMaterial { Name = "Air", Epsr = 1 },
            ],
        });
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        return ws;
    }

    private static string WriteC3d(string ws, string cell, C3dDocument doc)
    {
        string dir = Path.Combine(ws, cell, "3d");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, cell + ".c3d");
        C3dPersistence.SaveToFile(path, doc);
        return path;
    }

    // ── XML ─────────────────────────────────────────────────────────────────────────────────────

    private static XElement Probe(XDocument d, string name) => d.Descendants("ProbeBox").Single(e => e.Attribute("Name")!.Value == name);

    private static (Point3 P1, Point3 P2) Box(XDocument d, string name) => Prim(Probe(d, name).Descendants("Box").Single());

    private static (Point3 P1, Point3 P2) Prim(XElement box)
    {
        static Point3 Pt(XElement p) => new(D(p, "X"), D(p, "Y"), D(p, "Z"));
        return (Pt(box.Element("P1")!), Pt(box.Element("P2")!));
    }

    private static SortedSet<string> Attributes(XDocument d, string kind)
        => new(d.Descendants(kind).Where(e => e.Parent!.Name == "Properties").SelectMany(e => e.Attributes().Select(a => a.Name.LocalName)),
               StringComparer.Ordinal);

    private static double D(XElement e, string attr) => double.Parse(e.Attribute(attr)!.Value, System.Globalization.CultureInfo.InvariantCulture);

    private static bool OnGrid(IReadOnlyList<double> lines, double v)
    {
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i] == v) return true;
            if (i > 0 && (lines[i - 1] + lines[i]) / 2 == v) return true;
        }
        return false;
    }

    private static double Cell(IReadOnlyList<double> lines, double at)
    {
        for (int i = 1; i < lines.Count; i++) if (lines[i] >= at) return lines[i] - lines[i - 1];
        return lines[^1] - lines[^2];
    }

    private static C3dPoint2 P(long u, long v) => new(u * Um, v * Um);
    private static C3dRect Rect(long u, long v, long du, long dv) => new() { Min = P(u, v), Size = P(du, dv) };

    private static double Db(Complex s) => 20 * Math.Log10(s.Magnitude);
    private static double Deg(Complex s) => s.Phase * 180 / Math.PI;
    private static double Phase(Complex ratio) => ratio.Phase * 180 / Math.PI;
}

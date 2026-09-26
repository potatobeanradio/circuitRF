using System.Diagnostics;
using System.Numerics;
using System.Xml.Linq;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Engine.Em3d;
using RfCore;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Em3d;

// ══════════════════════════════════════════════════════════════════════════════════════════════
//  brief-em3d-9 §7 — the openEMS backend: problem + grid -> CSXCAD XML -> openEMS per port -> .sNp.
//  Gates 1-3 (the transform) are tests/Engine.Tests/Em3d/FdtdPortTransformTests.cs.
//
//  Writer gates (4, 5, 6, 11) need nothing installed. Run gates (7-10) skip unless discovery finds a
//  validated openEMS (overview §1i) — on the F0 machine it is found in ~/opt/openEMS/bin. Anything
//  over ~5 s is Category=Benchmark.
//
//  Goldens: testdata/em3d/openems-goldens/<case>/model.xml. To rewrite them after a DELIBERATE writer
//  change, run gate 4 with CRF_WRITE_OPENEMS_GOLDENS=1 and review the diff; nothing rewrites them
//  otherwise.
// ══════════════════════════════════════════════════════════════════════════════════════════════

// Em3dRenderExplainTests' collection, as PalaceBackendTests': its gate 6 reads the process-wide solver
// counter these runs advance.
[Collection(SkiaFontsTypefaceCollection.Name)]
public sealed class OpenEmsBackendTests(ITestOutputHelper output) : IDisposable
{
    private const double Um = 1e-6;
    private const double C0 = 299_792_458.0;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-openems-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    // ── 4. Writer goldens ───────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("microstrip")]
    [InlineData("via")]
    public void Gate4_TheWritersModel_IsByteForByteTheCommittedGolden(string name)
    {
        var low = Lowered(name);
        Assert.Equal(low.Model, Lowered(name).Model);        // determinism in-process as well
        Assert.DoesNotContain('\r', low.Model!);

        string file = Path.Combine(PalaceBackendTests.RepoRoot(), "testdata", "em3d", "openems-goldens", name, CsxcadWriter.ModelFile);
        if (Environment.GetEnvironmentVariable("CRF_WRITE_OPENEMS_GOLDENS") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, System.Text.Encoding.UTF8.GetBytes(low.Model!));
        }
        Assert.Equal(File.ReadAllBytes(file), System.Text.Encoding.UTF8.GetBytes(low.Model!));
    }

    // ── 5. Structure against F0's upstream-written XML ──────────────────────────────────────────

    /// <summary>
    /// Case B as circuitRF writes it against case B as openEMS's own interface wrote it during F0: the
    /// same property kinds, the same port and probe elements with the same attributes and the same
    /// type/weight/direction values, every attribute name F0's elements carry, and F0's primitive kinds.
    /// The one difference is recorded, not tolerated silently: the generator merges a pad and its line
    /// into one outline (with the ground plane's antipad as a hole), which CSXCAD states as LinPoly where
    /// F0's hand model used a Box and a Cylinder.
    /// </summary>
    [Fact]
    public void Gate5_CaseB_HasF0sPropertyPrimitivePortAndProbeKinds()
    {
        var (setup, source) = Em3dGeneratorTests.CaseB(plated: false);
        setup.AirBox = PalaceBackendTests.Padded(1500);
        setup.Solver3D = Em3dSolver.OpenEms;
        // F0's hand-written case dumps no field; brief 29's DumpBox is compared with fields off.
        setup.OpenEms = new CemOpenEms { SaveFieldsGHz = [] };
        var ours = XDocument.Parse(Lower(setup, source).PortFiles[0]);
        var f0 = XDocument.Load(Path.Combine(PalaceBackendTests.RepoRoot(), "testdata", "em3d", "f0", "B-via", "openems", "dw25-lossy", "case.xml"));

        static SortedSet<string> PropertyKinds(XDocument d) =>
            new(d.Descendants("Properties").Elements().Select(e => e.Name.LocalName), StringComparer.Ordinal);
        static SortedSet<string> PrimitiveKinds(XDocument d) =>
            new(d.Descendants("Primitives").Elements().Select(e => e.Name.LocalName), StringComparer.Ordinal);
        Assert.Equal(PropertyKinds(f0), PropertyKinds(ours));
        Assert.Subset(PrimitiveKinds(ours), PrimitiveKinds(f0));
        Assert.Equal(["LinPoly"], PrimitiveKinds(ours).Except(PrimitiveKinds(f0)));

        // Every element path F0 has, ours has with the same attribute names.
        var theirs = Paths(f0);
        var mine = Paths(ours);
        foreach (var (path, attributes) in theirs)
        {
            Assert.True(mine.ContainsKey(path), $"missing {path}");
            Assert.Equal(attributes, mine[path]);
        }

        // Ports and probes, value for value (F0's names -> ours).
        foreach (int port in new[] { 1, 2 })
        {
            foreach (var (f0Name, ourName) in new[] { ($"port_ut_{port}", $"port{port}_u"), ($"port_it_{port}", $"port{port}_i") })
            {
                var a = Probe(f0, f0Name);
                var b = Probe(ours, ourName);
                foreach (string attr in new[] { "Type", "Weight", "NormDir" })
                    Assert.Equal(a.Attribute(attr)!.Value, b.Attribute(attr)!.Value);
            }
            var ra = f0.Descendants("LumpedElement").Single(e => e.Attribute("Name")!.Value == $"port_resist_{port}");
            var rb = ours.Descendants("LumpedElement").Single(e => e.Attribute("Name")!.Value == $"port{port}_resist");
            Assert.Equal(ra.Attribute("Direction")!.Value, rb.Attribute("Direction")!.Value);
            Assert.Equal(double.Parse(ra.Attribute("R")!.Value), double.Parse(rb.Attribute("R")!.Value));
        }
        var ea = f0.Descendants("Excitation").Single(e => e.Parent!.Name == "Properties");
        var eb = ours.Descendants("Excitation").Single(e => e.Parent!.Name == "Properties");
        Assert.Equal(ea.Attribute("Excite")!.Value.Split(',').Select(double.Parse), eb.Attribute("Excite")!.Value.Split(',').Select(double.Parse));
        Assert.Equal(ea.Attribute("Type")!.Value, eb.Attribute("Type")!.Value);
    }

    // ── 6. One model, N files ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate6_ThePortFilesDifferFromTheModel_InTheExcitationElementOnly()
    {
        var low = Lowered("via");
        Assert.Equal(2, low.PortFiles.Count);
        var blocks = new List<string>();
        foreach (string file in low.PortFiles)
        {
            int start = file.IndexOf("            <Excitation ID=", StringComparison.Ordinal);
            int end = file.IndexOf("</Excitation>\n", start, StringComparison.Ordinal) + "</Excitation>\n".Length;
            Assert.True(start > 0 && end > start);
            string block = file[start..end];
            Assert.Equal(low.Model, file.Remove(start, end - start));
            blocks.Add(block);
        }
        Assert.NotEqual(blocks[0], blocks[1]);
        Assert.Contains("Name=\"port1_excite\"", blocks[0]);
        Assert.Contains("Name=\"port2_excite\"", blocks[1]);
    }

    // ── 11. What FDTD cannot say is said ────────────────────────────────────────────────────────

    /// <summary>
    /// A lossy substrate, two solid pads and a 25 µm bond wire on a grid far coarser than it: the notes
    /// state the frequency the loss is exact at, name the pads and the wire as perfect conductors, and the wire as a
    /// thin conductor — and the XML says the same thing the notes do.
    /// </summary>
    [Fact]
    public void Gate11_TheNotes_NameTheLossFitFrequency_ThePecSolids_AndTheSubCellWire()
    {
        var problem = WireProblem();
        Assert.Empty(problem.Validate());
        var grid = FdtdGrid.Build(problem, OpenEmsGridSettings.Default, long.MaxValue);
        var low = CsxcadWriter.Write(problem, grid, OpenEmsGridSettings.Default, OpenEmsRunSettings.Default);
        Assert.True(low.Ok, low.Refusal);
        foreach (string n in low.Notes) output.WriteLine(n);

        Assert.Equal(5.5e9, low.DielectricFitHz);
        Assert.Contains(low.Notes, n => n.Contains("5.5 GHz") && n.Contains("'Sub'") && n.Contains("Palace holds tanδ constant"));
        Assert.Equal(["pad1", "pad2", "wire"], low.PecSolids);
        Assert.Contains(low.Notes, n => n.Contains("'pad1', 'pad2', 'wire'") && n.Contains("perfect conductors"));
        Assert.Equal(["wire"], low.SubCellWires);
        Assert.Contains(low.Notes, n => n.Contains("'wire'") && n.Contains("thin conductor"));

        var xml = XDocument.Parse(low.Model!);
        Assert.Single(xml.Descendants("Curve"));
        Assert.Equal(2, xml.Descendants("Metal").Count(e => e.Attribute("Name")!.Value.StartsWith("pad", StringComparison.Ordinal)));
        double kappa = double.Parse(xml.Descendants("Material").Single(e => e.Attribute("Name")!.Value == "substrate")
                                       .Element("Property")!.Attribute("Kappa")!.Value.Split(',')[0]);
        Assert.Equal(2 * Math.PI * 5.5e9 * CsxcadWriter.Epsilon0 * 4.4 * 0.02, kappa, 1e-12);
    }

    // ── 7. Closed form ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Brief 7 gate 6's homogeneous stripline through openEMS: the difference in S21's group delay
    /// between a 10 mm and a 30 mm line is Δℓ·√εr/c within 1 % — the lumped ports' own parasitics are the
    /// same at both lengths and cancel. The expected value is the formula, never a circuitRF run. About
    /// 2 s for both lengths (measured 2026-09-25), so it is in the routine tier.
    /// </summary>
    [OpenEmsFact]
    public void Gate7_HomogeneousStripline_GroupDelayMatchesTheClosedForm()
    {
        const double b = 1000 * Um, w = 800 * Um, er = 2.2, len1 = 10e-3, len2 = 30e-3;
        var taus = new List<double>();
        foreach (double len in new[] { len1, len2 })
        {
            var (setup, source) = PalaceBackendTests.Stripline(w, b, len, er);
            setup.Solver3D = Em3dSolver.OpenEms;
            setup.Palace = null;
            var result = EmRunService.Run(setup, source, Path.Combine(_root, $"results-{len * 1e3:F0}mm"));
            Assert.True(result.Status == EmRunStatus.Ok, result.Error);
            Assert.DoesNotContain(result.Warnings, x => x.Contains("NOT converged", StringComparison.Ordinal));
            foreach (string n in result.Notes ?? []) output.WriteLine($"{len * 1e3} mm: {n}");
            foreach (string l in File.ReadLines(result.SnpPath!).Where(l => l.Contains("openEMS run port", StringComparison.Ordinal)))
                output.WriteLine(l);
            var snp = TouchstoneIO.ReadFile(result.SnpPath!);
            double[] omega = [.. snp.Frequencies.Select(x => 2 * Math.PI * x)];
            taus.Add(-PalaceBackendTests.Slope(omega, PalaceBackendTests.Unwrap([.. Enumerable.Range(0, omega.Length).Select(i => snp[i][1, 0].Phase)])));
            for (int i = 0; i < omega.Length; i++)
                output.WriteLine($"{len * 1e3} mm {snp.Frequencies[i] / 1e9:F2} GHz |S11| {20 * Math.Log10(snp[i][0, 0].Magnitude):F2} dB " +
                                 $"|S21| {20 * Math.Log10(snp[i][1, 0].Magnitude):F3} dB");
        }
        double dTau = taus[1] - taus[0], expected = (len2 - len1) * Math.Sqrt(er) / C0;
        output.WriteLine($"group delay difference {dTau * 1e12:F3} ps, expected {expected * 1e12:F3} ps");
        Assert.InRange(dTau / expected, 0.99, 1.01);
    }

    // ── 8. F0 reference ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// circuitRF's generated case B against F0's openEMS run of the same geometry
    /// (testdata/em3d/f0/B-via/openems/dw25-lossy/reference.s2p — κ fitted at 10 GHz, PEC copper, as this
    /// backend writes it): |S21| within 0.1 dB and ∠S21 within 2°. The grids differ (F0's 25 µm patch
    /// over the via against brief 8's generator), and F0's own grid effect on this case was 0.6 % of
    /// electrical length against Palace, which is 2° at 20 GHz.
    ///
    /// <para>F0's board is 10 × 5 mm with 1.5 mm of AIR around it, and a generated problem matches it only
    /// if it makes the same choice (testdata/em3d/f0/README.md): without an outline the generator carries
    /// every dielectric to the air box, which puts dielectric beyond both port sheets and read as a phase
    /// error growing linearly to −12° at 20 GHz (measured 2026-09-25). So the fixture draws the board's
    /// outline on an Edge.Cuts layer, the generator's own outline rule (R-em3d3-5c).</para>
    /// </summary>
    [OpenEmsFact]
    [Trait("Category", "Benchmark")]
    public void Gate8_TheGeneratedViaTransition_MatchesF0sOpenEmsRun()
    {
        var (setup, source) = Em3dGeneratorTests.CaseB(plated: false);
        var edge = new LayerKey(90, 0);
        source.Technology!.Layers.Add(new LayerDef
        {
            Key = edge, Name = "Board outline", Interchange = new InterchangeMapping(null, null, null, null, null, "Edge.Cuts"),
        });
        long um = source.View.DbuPerMicron;
        source.View.Shapes.Add(new RectShape { Layer = edge, X1 = -5000 * um, Y1 = -2500 * um, X2 = 5000 * um, Y2 = 2500 * um });
        setup.AirBox = PalaceBackendTests.Padded(1500);
        setup.Solver3D = Em3dSolver.OpenEms;
        var result = EmRunService.Run(setup, source, Path.Combine(_root, "results"));
        Assert.True(result.Status == EmRunStatus.Ok, result.Error);
        foreach (string n in result.Notes ?? []) output.WriteLine(n);
        foreach (string n in result.Warnings) output.WriteLine("WARNING " + n);

        foreach (string l in File.ReadLines(result.SnpPath!).Where(l => l.Contains("openEMS run port", StringComparison.Ordinal)))
            output.WriteLine(l);
        var ours = TouchstoneIO.ReadFile(result.SnpPath!);
        var f0 = TouchstoneIO.ReadFile(Path.Combine(PalaceBackendTests.RepoRoot(), "testdata", "em3d", "f0", "B-via", "openems",
                                                    "dw25-lossy", "reference.s2p"));
        Assert.Equal(f0.Frequencies.Length, ours.Frequencies.Length);
        double worstDb = 0, worstDeg = 0;
        for (int i = 0; i < f0.Frequencies.Length; i++)
        {
            Assert.Equal(f0.Frequencies[i], ours.Frequencies[i], 1e-3 * f0.Frequencies[i]);
            Complex a = ours[i][1, 0], r = f0[i][1, 0];
            worstDb  = Math.Max(worstDb, Math.Abs(20 * Math.Log10(a.Magnitude / r.Magnitude)));
            worstDeg = Math.Max(worstDeg, Math.Abs((a / r).Phase) * 180 / Math.PI);
            if (i % 20 == 19)
                output.WriteLine($"{f0.Frequencies[i] / 1e9,5:F1} GHz  |S21| {20 * Math.Log10(a.Magnitude):F4} vs {20 * Math.Log10(r.Magnitude):F4} dB, " +
                                 $"angle diff {(a / r).Phase * 180 / Math.PI:F3} deg, |S11| {20 * Math.Log10(ours[i][0, 0].Magnitude):F1} vs {20 * Math.Log10(f0[i][0, 0].Magnitude):F1} dB");
        }
        output.WriteLine($"worst |S21| {worstDb:F4} dB, worst angle {worstDeg:F3} deg");
        Assert.True(worstDb < 0.1, $"{worstDb} dB");
        Assert.True(worstDeg < 2, $"{worstDeg} deg");
    }

    // ── 9. An unconverged run ───────────────────────────────────────────────────────────────────

    [OpenEmsFact]
    public void Gate9_ARunStoppedByMaxTimeSteps_IsWrittenWithTheWarning()
    {
        var (setup, source) = SmallMicrostrip();
        setup.OpenEms = new CemOpenEms { MaxTimeSteps = 300 };
        var result = EmRunService.Run(setup, source, Path.Combine(_root, "results"));
        Assert.True(result.Status == EmRunStatus.Ok, result.Error);
        Assert.True(File.Exists(result.SnpPath));
        var warning = Assert.Single(result.Warnings, x => x.Contains("NOT converged", StringComparison.Ordinal) &&
                                                          x.Contains("port 1", StringComparison.Ordinal));
        output.WriteLine(warning);
        Assert.Contains("limit of 300 time steps", warning);
        Assert.Contains("against an end criterion of 50 dB", warning);
        Assert.Contains("NOT converged", File.ReadAllText(result.SnpPath!));
    }

    // ── 10. Cancellation ────────────────────────────────────────────────────────────────────────

    [OpenEmsFact]
    public void Gate10_CancellingMidRun_WritesNothing_AndLeavesNoProcess()
    {
        var (setup, source) = Em3dGeneratorTests.CaseB(plated: true);
        setup.AirBox = PalaceBackendTests.Padded(1500);
        setup.Solver3D = Em3dSolver.OpenEms;
        string results = Path.Combine(_root, "results");

        using var cts = new CancellationTokenSource();
        var started = new List<int>();
        void OnStart(Process p)
        {
            lock (started) started.Add(p.Id);
            cts.CancelAfter(TimeSpan.FromSeconds(1.5));
        }
        OpenEmsRun.ProcessStarted += OnStart;
        EmRunResult result;
        try { result = EmRunService.Run(setup, source, results, cts.Token); }
        finally { OpenEmsRun.ProcessStarted -= OnStart; }

        Assert.Equal(EmRunStatus.Cancelled, result.Status);
        Assert.Null(result.SnpPath);
        Assert.Empty(Directory.EnumerateFiles(results, "*.s*p"));
        int pid = Assert.Single(started);
        Assert.False(PalaceBackendTests.Alive(pid), $"openEMS process {pid} is still running");
        Assert.False(File.Exists(Path.Combine(Em3dRunService.RunDirectory(results, setup, Em3dSolver.OpenEms), "p1", OpenEmsRun.AbortFile)));
    }

    // ══ fixtures ════════════════════════════════════════════════════════════════════════════════

    /// <summary>The problems the writer gates lower: <c>microstrip</c> is brief 3 gate 1's, <c>via</c> the
    /// plated via transition with F0's 1500 µm of air on every side — PalaceBackendTests' two.</summary>
    private static CsxcadLowering Lowered(string name)
    {
        var (setup, source) = name == "via" ? Em3dGeneratorTests.CaseB(plated: true) : Em3dGeneratorTests.Microstrip();
        if (name == "via") setup.AirBox = PalaceBackendTests.Padded(1500);
        setup.Solver3D = Em3dSolver.OpenEms;
        return Lower(setup, source);
    }

    private static CsxcadLowering Lower(EmSetup setup, EmLayoutSource source)
    {
        var r = Em3dGenerator.Generate(setup, source, source.Technology!);
        Assert.True(r.Ok, r.Refusal);
        var gs = CemOpenEms.ResolveGrid(setup.OpenEms);
        var grid = FdtdGrid.Build(r.Problem!, gs, long.MaxValue);
        var low = CsxcadWriter.Write(r.Problem!, grid, gs, CemOpenEms.ResolveRun(setup.OpenEms));
        Assert.True(low.Ok, low.Refusal);
        return low;
    }

    /// <summary>The microstrip in a 2 mm box — seconds of openEMS.</summary>
    private static (EmSetup, EmLayoutSource) SmallMicrostrip()
    {
        var (setup, source) = Em3dGeneratorTests.Microstrip();
        var face = new EmAirBoxFace(2000, null);
        setup.AirBox = new EmAirBox(face, face, face, face, null, face);
        setup.Solver3D = Em3dSolver.OpenEms;
        return (setup, source);
    }

    /// <summary>A lossy substrate, two solid pads, and a 25 µm wire between them — built directly, so the
    /// gate depends on nothing but the writer and the grid.</summary>
    private static Em3dProblem WireProblem()
    {
        const double mm = 1e-3;
        var sq = new[] { (-1.0, -1.0), (1.0, -1.0), (1.0, 1.0), (-1.0, 1.0) };
        var path = new List<Point3> { new(-0.5 * mm, 0, 0.235 * mm), new(0, 0, 0.4 * mm), new(0.5 * mm, 0, 0.235 * mm) };
        var rings = path.Select(q => (IReadOnlyList<Point3>)[.. sq.Select(d => new Point3(q.X, q.Y + d.Item1 * 12.5 * Um, q.Z + d.Item2 * 12.5 * Um))]).ToList();
        var materials = new[]
        {
            new Em3dMaterial("Sub", 4.4, null, 0.02, 1, 0),
            new Em3dMaterial("Air", 1, null, 0, 1, 0),
            new Em3dMaterial("Cu", 1, null, 0, 1, 5.8e7),
            new Em3dMaterial("Au", 1, null, 0, 1, 4.1e7),
        };
        var solids = new[]
        {
            new Em3dSolid("substrate", "Sub", Em3dRole.Dielectric, new Em3dBox(new(-1 * mm, -1 * mm, 0), new(1 * mm, 1 * mm, 0.2 * mm)), 1),
            new Em3dSolid("air", "Air", Em3dRole.Air, new Em3dBox(new(-1 * mm, -1 * mm, 0.2 * mm), new(1 * mm, 1 * mm, 1 * mm)), 2),
            new Em3dSolid("pad1", "Cu", Em3dRole.Conductor, new Em3dBox(new(-0.6 * mm, -0.1 * mm, 0.2 * mm), new(-0.4 * mm, 0.1 * mm, 0.235 * mm)), 3),
            new Em3dSolid("pad2", "Cu", Em3dRole.Conductor, new Em3dBox(new(0.4 * mm, -0.1 * mm, 0.2 * mm), new(0.6 * mm, 0.1 * mm, 0.235 * mm)), 4),
            new Em3dSolid("wire", "Au", Em3dRole.Conductor, new Em3dSweep(path, Em3dSection.Hexagon, 25 * Um, rings), 5),
        };
        Em3dPort Port(int n, double x, string pad) => new(n, $"port/{n}", pad, "airbox/zmin",
            new(x, -0.1 * mm, 0), new(x, 0.1 * mm, 0.2 * mm), new(0, 0, 1), 50,
            new Em3dReferencePlane(new(x, 0, 0), new(0, 0, 1), 0));
        var box = new Em3dAirBox(new(-1 * mm, -1 * mm, 0), new(1 * mm, 1 * mm, 1 * mm),
            new Em3dFaces(Em3dBoundaryKind.Absorbing, Em3dBoundaryKind.Absorbing, Em3dBoundaryKind.Absorbing,
                          Em3dBoundaryKind.Absorbing, Em3dBoundaryKind.Pec, Em3dBoundaryKind.Absorbing));
        return new Em3dProblem(solids, [], materials, [Port(1, -0.6 * mm, "pad1"), Port(2, 0.6 * mm, "pad2")], box,
                               new Em3dFrequency(1e9, 10e9, 10, Em3dSweepKind.Linear), 20);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Each element's path from the root (tag names) and its sorted attribute names — the
    /// union over every element at that path. A primitive's path does not name the property kind that
    /// holds it (<c>Properties/*/Primitives/Box</c>): which solid is a Box is the geometry's business,
    /// and gate 5 compares kinds of primitive separately.</summary>
    private static Dictionary<string, SortedSet<string>> Paths(XDocument d)
    {
        var map = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var e in d.Descendants())
        {
            var names = e.AncestorsAndSelf().Reverse().Select(a => a.Name.LocalName).ToList();
            int k = names.IndexOf("Primitives");
            if (k > 0 && names[k - 2] == "Properties") names[k - 1] = "*";
            string path = string.Join("/", names);
            if (!map.TryGetValue(path, out var set)) map[path] = set = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var a in e.Attributes()) set.Add(a.Name.LocalName);
        }
        return map;
    }

    private static XElement Probe(XDocument d, string name)
        => d.Descendants("ProbeBox").Single(e => e.Attribute("Name")!.Value == name);
}

/// <summary>Skips, naming what is missing, unless an openEMS run would proceed — openEMS found and
/// validated (the run's own ReadinessFor).</summary>
public sealed class OpenEmsFactAttribute : FactAttribute
{
    private static readonly Lazy<string?> Reason = new(() =>
        SolverDiscovery.ReadinessFor(Em3dSolver.OpenEms).FirstOrDefault(r => !r.Proceeds) is { } r
            ? $"needs a validated openEMS: {r.Refusal}" : null);

    public OpenEmsFactAttribute()
    {
        if (Reason.Value is { } why) Skip = why;
    }
}

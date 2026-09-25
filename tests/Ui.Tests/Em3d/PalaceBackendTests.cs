using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;
using CircuitRF.Core.Design;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine;
using CircuitRF.Engine.Em3d;
using RfCore;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Em3d;

// ══════════════════════════════════════════════════════════════════════════════════════════════
//  brief-em3d-7 §7 — the Palace backend: .geo -> Gmsh -> Palace -> .sNp.
//
//  Writer gates (1, 2, 8, 11a, 12) need nothing installed. Gmsh gates (3, 4, 5) skip without a
//  validated Gmsh; Palace gates (6, 7, 9, 10, 11b) skip unless discovery finds a validated, probed
//  Palace AND Gmsh (overview §1i) — on the F0 machine that means CIRCUITRF_PALACE, since Palace
//  lives in a Spack prefix nothing puts on PATH. The runs are kept small; anything over ~5 s is
//  Category=Benchmark.
//
//  Goldens: testdata/em3d/palace-goldens/<case>/. To rewrite them after a DELIBERATE writer change,
//  run gate 1 with CRF_WRITE_PALACE_GOLDENS=1 and review the diff; nothing rewrites them otherwise.
// ══════════════════════════════════════════════════════════════════════════════════════════════

// Em3dRenderExplainTests' collection: its gate 6 reads the process-wide mesher/solver counter these
// gates advance, so the two classes never run at the same time.
[Collection(SkiaFontsTypefaceCollection.Name)]
public sealed class PalaceBackendTests(ITestOutputHelper output) : IDisposable
{
    private const double Um = 1e-6;
    private const double C0 = 299_792_458.0;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-palace-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    // ── 1. Writer goldens ───────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("microstrip")]
    [InlineData("via")]
    public void Gate1_TheWritersOutput_IsByteForByteTheCommittedGolden(string name)
    {
        var (problem, settings) = Lowerable(name);
        var low = GmshGeoWriter.Write(problem, settings);
        Assert.True(low.Ok, low.Refusal);
        var cfg = PalaceConfigWriter.Write(problem, low.Groups, settings);
        Assert.True(cfg.Ok, cfg.Refusal);

        // Determinism in-process as well: a second lowering is the same bytes.
        Assert.Equal(low.Geo, GmshGeoWriter.Write(problem, settings).Geo);

        string dir = Path.Combine(RepoRoot(), "testdata", "em3d", "palace-goldens", name);
        var files = new[] { (GmshGeoWriter.GeoFile, low.Geo!), (GmshGeoWriter.GroupsFile, low.GroupsJson!),
                            (PalaceConfigWriter.ConfigFile, cfg.Json!) };
        if (Environment.GetEnvironmentVariable("CRF_WRITE_PALACE_GOLDENS") == "1")
        {
            Directory.CreateDirectory(dir);
            foreach (var (file, text) in files) File.WriteAllBytes(Path.Combine(dir, file), System.Text.Encoding.UTF8.GetBytes(text));
        }
        foreach (var (file, text) in files)
            Assert.Equal(File.ReadAllBytes(Path.Combine(dir, file)), System.Text.Encoding.UTF8.GetBytes(text));
        Assert.DoesNotContain('\r', low.Geo);
    }

    // ── 2. Schema ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate2_BothConfigGoldens_ValidateAgainstTheCommittedPalaceSchema_AndAPlantedKeyDoesNot()
    {
        using var schemaDoc = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(RepoRoot(), "testdata", "em3d", "palace-schema", "0.18.1.json")));
        var schema = new DraftSevenSchema(schemaDoc.RootElement);
        Assert.Equal("urn:palace:schema:1-7-0", schemaDoc.RootElement.GetProperty("$id").GetString());

        foreach (string name in new[] { "microstrip", "via" })
        {
            string text = File.ReadAllText(Path.Combine(RepoRoot(), "testdata", "em3d", "palace-goldens", name,
                                                         PalaceConfigWriter.ConfigFile));
            using var doc = JsonDocument.Parse(text);
            Assert.Empty(schema.Validate(doc.RootElement));

            // The validator bites: an unknown key, a wrong type, a bad enum, a missing requirement.
            foreach (var (from, to) in new[]
                     {
                         ("\"Type\": \"Driven\"", "\"Type\": \"Driven\", \"Bogus\": 1"),
                         ("\"Order\": 2", "\"Order\": \"two\""),
                         ("\"Direction\": \"", "\"Direction\": \"W"),
                         ("\"Mesh\": \"model.msh\",", ""),
                     })
            {
                Assert.Contains(from, text);
                using var planted = JsonDocument.Parse(text.Replace(from, to));
                Assert.NotEmpty(schema.Validate(planted.RootElement));
            }
        }
    }

    // ── 3. Entity check, planted ────────────────────────────────────────────────────────────────

    [GmshFact]
    public void Gate3_APortSheetThatSelectsNothing_IsRefusedNamingThePort()
    {
        var (problem, settings) = Lowerable("small-microstrip");
        var low = GmshGeoWriter.Write(problem, settings);
        var port2 = low.Groups.Single(g => g.Name == "port/2");

        // Move port 2's selection box 1 mm off its sheet: the script still runs, and selects nothing.
        var edited = Regex.Replace(low.Geo!, @"(// port/2\nq1\[\] = Surface In BoundingBox\{)(-?[\d.]+)",
                                   m => m.Groups[1].Value + (double.Parse(m.Groups[2].Value) + 1000));
        Assert.NotEqual(low.Geo, edited);

        var step = PalaceRun.Mesh(Dir("planted"), low with { Geo = edited }, Gmsh(), null, default);
        Assert.True(step.Refused, step.Message);
        Assert.Contains("'port/2'", step.Message);
        Assert.Contains("port 2's sheet", step.Message);
        Assert.Contains("selected nothing", step.Message);
        Assert.Equal(1, port2.Expected);
    }

    // ── 4. Entity check, real ───────────────────────────────────────────────────────────────────

    [GmshFact]
    public void Gate4_TheViaTransitionMeshes_AndEveryNamedObjectGetsItsCount()
    {
        var (problem, settings) = Lowerable("via");
        // Counts are geometry, not mesh density: a coarse starting mesh keeps this in the routine tier.
        settings = settings with { MaxElementWavelengths = 0.5, EdgeRefinement = 1 };
        var low = GmshGeoWriter.Write(problem, settings);
        string dir = Dir("via");
        var step = PalaceRun.Mesh(dir, low, Gmsh(), null, default);
        Assert.True(step.Ok, step.Message);

        var table = GmshGeoWriter.ReadEntities(File.ReadAllText(Path.Combine(dir, GmshGeoWriter.EntitiesFile)))!;
        Assert.Equal(0, table.UnclassifiedSingleSided);
        Assert.Equal(table.AllVolumes, table.ClassifiedVolumes);
        foreach (var g in low.Groups)
            output.WriteLine($"{g.Attribute,3} {g.Name,-18} {g.Kind,-10} {table.Groups[g.Attribute].Count}");
        Assert.All(low.Groups.Where(g => g.Kind is Em3dGroupKind.Volume or Em3dGroupKind.Port),
                   g => Assert.Equal(1, table.Groups[g.Attribute].Count));
        Assert.All(low.Groups.Where(g => g.Kind is Em3dGroupKind.Conductor or Em3dGroupKind.Face),
                   g => Assert.True(table.Groups[g.Attribute].Count >= 1, g.Name));
        Assert.True(new FileInfo(Path.Combine(dir, GmshGeoWriter.MeshFile)).Length > 0);
    }

    // ── 5. Mesh reuse ───────────────────────────────────────────────────────────────────────────

    [GmshFact]
    public void Gate5_TwoRunsWithNoChange_InvokeGmshOnce_AndAChangedScriptMeshesAgain()
    {
        var (problem, settings) = Lowerable("small-microstrip");
        var low = GmshGeoWriter.Write(problem, settings);
        string dir = Dir("reuse");

        long before = PalaceRun.GmshInvocations;
        var first  = PalaceRun.Mesh(dir, low, Gmsh(), null, default);
        var second = PalaceRun.Mesh(dir, low, Gmsh(), null, default);
        Assert.True(first.Ok && !first.Reused, first.Message);
        Assert.True(second.Ok && second.Reused, second.Message);
        Assert.Equal(PalaceRun.Sha256(low.Geo!) + "\n", File.ReadAllText(Path.Combine(dir, PalaceRun.MeshHashFile)));

        var changed = GmshGeoWriter.Write(problem, settings with { Grading = 1.4 });
        var third = PalaceRun.Mesh(dir, changed, Gmsh(), null, default);
        Assert.True(third.Ok && !third.Reused, third.Message);
        // Counted across the process, so another test meshing concurrently can only add.
        Assert.True(PalaceRun.GmshInvocations - before >= 2);
    }

    // ── 6. Closed form ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A homogeneous stripline generated from a .clay: a thin strip centred between a PEC floor (the
    /// undrawn ground plane) and a PEC top face, in εr 2.2, with PEC side walls five plate spacings out
    /// that tie the planes together as a stripline's are. Two lengths, so the lumped ports' own
    /// parasitics — a reference-plane effect (em-3d.md §4.4) that is the same at both lengths — cancel:
    /// the DIFFERENCE in S21's group delay is Δℓ·√εr/c, and the line's own Z0, read from the ABCD
    /// matrix's C term (which a series port parasitic does not touch), is Cohn's exact
    /// Z0 = (30π/√εr)·K(k)/K(k'), k = sech(πw/2b), for a zero-thickness strip, within 8 % — the port's
    /// shunt parasitic is in that term too — and |S11| stays under −20 dB, which a line 20 % off 50 Ω
    /// breaks. Every expected value is computed here from the formulas, never from a circuitRF run.
    /// </summary>
    [PalaceFact]
    [Trait("Category", "Benchmark")]
    public void Gate6_HomogeneousStripline_MatchesTheClosedForms()
    {
        const double b = 1000 * Um, w = 800 * Um, er = 2.2, len1 = 10e-3, len2 = 30e-3;
        double k = 1 / Math.Cosh(Math.PI * w / (2 * b));
        double z0Cohn = 30 * Math.PI / Math.Sqrt(er) * EllipticK(k) / EllipticK(Math.Sqrt(1 - k * k));

        var runs = new List<(double Len, double[] F, Complex[][,] S)>();
        foreach (double len in new[] { len1, len2 })
        {
            var (setup, source) = Stripline(w, b, len, er);
            var result = EmRunService.Run(setup, source, Path.Combine(_root, $"results-{len * 1e3:F0}mm"));
            Assert.True(result.Status == EmRunStatus.Ok, result.Error);
            foreach (string n in (result.Notes ?? []).Where(n => n.StartsWith("Palace solved", StringComparison.Ordinal)))
                output.WriteLine($"{len * 1e3} mm: {n}");
            var snp = TouchstoneIO.ReadFile(result.SnpPath!);
            runs.Add((len, snp.Frequencies, [.. Enumerable.Range(0, snp.Frequencies.Length).Select(i =>
                new Complex[,] { { snp[i][0, 0], snp[i][0, 1] }, { snp[i][1, 0], snp[i][1, 1] } })]));
        }

        double[] f = runs[0].F;
        double[] omega = f.Select(x => 2 * Math.PI * x).ToArray();
        double Tau(int r) => -Slope(omega, Unwrap(runs[r].S.Select(s => s[1, 0].Phase).ToArray()));
        double dTau = Tau(1) - Tau(0), dTauExpected = (len2 - len1) * Math.Sqrt(er) / C0;
        output.WriteLine($"group delay {Tau(0) * 1e12:F3} ps at {len1 * 1e3} mm, {Tau(1) * 1e12:F3} ps at {len2 * 1e3} mm; " +
                         $"difference {dTau * 1e12:F3} ps, expected {dTauExpected * 1e12:F3} ps; Cohn Z0 {z0Cohn:F3} ohm");

        var z0s = new List<double>();
        for (int r = 0; r < 2; r++)
            for (int i = 0; i < f.Length; i++)
            {
                // C = S21-normalised ((1 - S11)(1 - S22) - S12·S21) / (2·S21·Z_ref), the ABCD shunt term.
                var s = runs[r].S[i];
                Complex c = ((1 - s[0, 0]) * (1 - s[1, 1]) - s[0, 1] * s[1, 0]) / (2 * s[1, 0] * 50);
                double theta = omega[i] * runs[r].Len * Math.Sqrt(er) / C0;
                double z0 = Math.Sin(theta) / c.Imaginary;
                output.WriteLine($"{runs[r].Len * 1e3} mm {f[i] / 1e9:F2} GHz: |S11| {20 * Math.Log10(s[0, 0].Magnitude):F2} dB, " +
                                 $"line Z0 from C {z0:F3} ohm");
                // The C term holds the ports' SHUNT parasitic as well, which dominates where sin θ is
                // small: read Z0 only on the first lobe. Measured (2026-09-25, one run): 47.9–52.3 Ω
                // there against Cohn's 51.2, and 33–59 Ω near θ = π.
                if (theta < Math.PI && Math.Sin(theta) > 0.5) z0s.Add(z0);
                Assert.True(s[0, 0].Magnitude < 0.1, $"|S11| at {f[i] / 1e9} GHz, {runs[r].Len * 1e3} mm");
            }
        Assert.InRange(dTau / dTauExpected, 0.99, 1.01);
        Assert.NotEmpty(z0s);
        Assert.All(z0s, z0 => Assert.InRange(z0 / z0Cohn, 0.92, 1.08));
    }

    // ── 7. F0 reference ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// circuitRF's generated case B against F0's hand-written Palace run
    /// (testdata/em3d/f0/B-via/palace/reference.s2p): |S21| within 0.05 dB and ∠S21 within 1°, the
    /// brief's figures. F0's own spread between its lossy run and openEMS was 0.1 dB, and its mesh
    /// refinement moved |S21| by 0.003 dB — so 0.05 dB is a mesh-to-mesh tolerance, not a solver one.
    /// </summary>
    [PalaceFact]
    [Trait("Category", "Benchmark")]
    public void Gate7_TheGeneratedViaTransition_MatchesF0sHandWrittenPalaceRun()
    {
        var (setup, source) = Em3dGeneratorTests.CaseB(plated: false);
        setup.AirBox = Padded(1500);
        setup.Palace = new CemPalace { AdaptiveMaxIterations = 0 };
        var result = EmRunService.Run(setup, source, Path.Combine(_root, "results"));
        Assert.True(result.Status == EmRunStatus.Ok, result.Error);

        var ours = TouchstoneIO.ReadFile(result.SnpPath!);
        var f0 = TouchstoneIO.ReadFile(Path.Combine(RepoRoot(), "testdata", "em3d", "f0", "B-via", "palace", "reference.s2p"));
        Assert.Equal(f0.Frequencies.Length, ours.Frequencies.Length);
        double worstDb = 0, worstDeg = 0;
        for (int i = 0; i < f0.Frequencies.Length; i++)
        {
            Assert.Equal(f0.Frequencies[i], ours.Frequencies[i], 1e-3 * f0.Frequencies[i]);
            Complex a = ours[i][1, 0], r = f0[i][1, 0];
            worstDb  = Math.Max(worstDb, Math.Abs(20 * Math.Log10(a.Magnitude / r.Magnitude)));
            worstDeg = Math.Max(worstDeg, Math.Abs((a / r).Phase) * 180 / Math.PI);
        }
        output.WriteLine($"worst |S21| {worstDb:F4} dB, worst angle {worstDeg:F3} deg");
        Assert.True(worstDb < 0.05, $"{worstDb} dB");
        Assert.True(worstDeg < 1, $"{worstDeg} deg");
    }

    // ── 8. The planar path does not move ────────────────────────────────────────────────────────

    [Fact]
    public void Gate8_EveryCemInTheRepo_KeepsItsPlanarPaths_AndA3DResultCarriesItsSolver()
    {
        string results = Path.Combine(_root, "results");
        foreach (string cem in RepoCems())
        {
            var setup = EmSetupPersistence.LoadFromFile(cem);
            // The rule as it stood before this brief, restated: the setup name, else the layout's stem.
            string key = Design.Results.ResultsWriter.SanitizeFileNameComponent(
                setup.Name is { Length: > 0 } n ? n : Path.GetFileNameWithoutExtension(setup.LayoutRef));
            string expected = setup.SnpOutputPathOverride is { Length: > 0 } ? EmRunService.ResolveSnpBasePath(results, setup)
                                                                            : Path.Combine(results, key);
            Assert.Equal(expected + ".s2p", EmRunService.ResolveSnpPath(results, setup, 2));
            Assert.Equal(key + "_em", EmRunService.ResolveNpyKey(setup));

            Assert.Equal(key + ".palace", Em3dRunService.ResultKey(setup, Em3dSolver.Palace));
            Assert.Equal(key + ".palace_em", Em3dRunService.NpyKey(setup, Em3dSolver.Palace));
            Assert.Equal(Path.Combine(results, key + ".palace"), Em3dRunService.RunDirectory(results, setup, Em3dSolver.Palace));
            // brief-em3d-9 R-em3d9-5a: openEMS lands by the same rule, beside Palace, never over it.
            Assert.Equal(key + ".openems", Em3dRunService.ResultKey(setup, Em3dSolver.OpenEms));
            Assert.Equal(key + ".openems_em", Em3dRunService.NpyKey(setup, Em3dSolver.OpenEms));
        }
    }

    // ── 9. CLI == in-process ────────────────────────────────────────────────────────────────────

    [PalaceFact]
    [Trait("Category", "Benchmark")]
    public void Gate9_TheEmVerbAsAProcess_WritesTheSameSnpAsEmRunService()
    {
        string cem = SmallWorkspace("inproc");
        string cemCli = SmallWorkspace("cli");

        var setup = EmSetupPersistence.LoadFromFile(cem);
        string cws = Path.Combine(Path.GetDirectoryName(cem)!, ".cws");
        var resolved = EmSetupResolver.Resolve(cem, setup.LayoutRef, cws, new TechnologyCache());
        var inProcess = EmRunService.Run(setup, resolved.Source, Path.Combine(Path.GetDirectoryName(cem)!, "results"));
        Assert.True(inProcess.Status == EmRunStatus.Ok, inProcess.Error);

        var (code, stdout, stderr) = CliProcess.Run(RepoRoot(), [], "em", cemCli);
        output.WriteLine(stdout);
        output.WriteLine(stderr);
        Assert.Equal(0, code);
        string cliSnp = Path.Combine(Path.GetDirectoryName(cemCli)!, "results", "ms.palace.s2p");
        Assert.Equal(Path.GetFileName(inProcess.SnpPath), Path.GetFileName(cliSnp));
        Assert.Equal(WithoutWriteStamp(File.ReadAllLines(inProcess.SnpPath!)), WithoutWriteStamp(File.ReadAllLines(cliSnp)));
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(cemCli)!, "results", "ms.palace_em.npy")));
    }

    // ── 10. Cancellation ────────────────────────────────────────────────────────────────────────

    [PalaceFact]
    [Trait("Category", "Benchmark")]
    public void Gate10_CancellingDuringGmshAndDuringPalace_WritesNothing_AndLeavesNoProcess()
    {
        var (setup, source) = Em3dGeneratorTests.CaseB(plated: true);
        setup.AirBox = Padded(1500);
        string results = Path.Combine(_root, "results");

        foreach (var kind in new[] { "gmsh", "palace" })
        {
            using var cts = new CancellationTokenSource();
            var started = new List<int>();
            void OnStart(Process p)
            {
                bool target = kind == "gmsh" ? p.StartInfo.ArgumentList.Contains(GmshGeoWriter.GeoFile)
                                             : p.StartInfo.ArgumentList.Contains(PalaceConfigWriter.ConfigFile);
                if (!target) return;
                // The id, not the object: the run disposes its Process before this test looks.
                lock (started) started.Add(p.Id);
                cts.CancelAfter(TimeSpan.FromSeconds(1.5));
            }
            PalaceRun.ProcessStarted += OnStart;
            EmRunResult result;
            try { result = EmRunService.Run(setup, source, results, cts.Token); }
            finally { PalaceRun.ProcessStarted -= OnStart; }

            Assert.Equal(EmRunStatus.Cancelled, result.Status);
            Assert.Null(result.SnpPath);
            Assert.Empty(Directory.EnumerateFiles(results, "*.s*p"));
            int pid = Assert.Single(started);
            Assert.False(Alive(pid), $"{kind} process {pid} is still running");
        }
    }

    // ── 11. Refusals before work ────────────────────────────────────────────────────────────────

    /// <summary>An unvalidated Palace is refused with brief 6's own sentence, and no mesher starts —
    /// run as a PROCESS so the fake program is named by an environment variable no other test sees.</summary>
    [Fact]
    public void Gate11a_AnUnvalidatedPalace_IsRefusedWithBrief6sText_AndNothingIsMeshed()
    {
        if (OperatingSystem.IsWindows()) return;   // the fake Palace is a shell script
        string fake = Path.Combine(_root, "fake-palace");
        Directory.CreateDirectory(_root);
        File.WriteAllText(fake, "#!/bin/sh\necho 'Palace version: 1234abc'\necho 'Schema version: 1-7-0'\n");
        File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        string cem = SmallWorkspace("unvalidated");
        var (code, _, stderr) = CliProcess.Run(RepoRoot(), [("CIRCUITRF_PALACE", fake)], "em", cem);
        Assert.Equal(1, code);

        var d = SolverDiscovery.Create(SolverTool.Palace);
        Assert.True(d.TryProbe(fake, SolverHowFound.Environment, "named by CIRCUITRF_PALACE", out var install, out _));
        string brief6 = d.DescribeUnvalidated(install!);
        Assert.Contains(brief6, stderr.Replace("\r", "").Replace("\n", " "));
        Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(cem)!, "results", "ms.palace")));
    }

    /// <summary>A missing Gmsh, with a working Palace: refused naming Gmsh, and Gmsh started 0 times.</summary>
    [PalaceFact]
    public void Gate11b_AMissingGmsh_IsRefusedNamingIt_AndGmshIsNeverStarted()
    {
        string cem = SmallWorkspace("nogmsh");
        var (code, _, stderr) = CliProcess.Run(RepoRoot(), [("CIRCUITRF_GMSH", Path.Combine(_root, "no-such-gmsh"))], "em", cem);
        Assert.Equal(1, code);
        Assert.Contains("Gmsh was not found", stderr);
        Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(cem)!, "results", "ms.palace")));
    }

    // ── 12. One door ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate12_NothingButEmRunService_CallsEm3dRunService()
    {
        var callers = new List<string>();
        foreach (string file in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            string code = StripComments(File.ReadAllText(file));
            if (Regex.IsMatch(code, @"\bEm3dRunService\s*\.\s*Run\s*\(")) callers.Add(Path.GetFileName(file));
        }
        Assert.Equal(["EmRunService.cs"], callers);
    }

    // ══ fixtures ════════════════════════════════════════════════════════════════════════════════

    /// <summary>The problems the writer gates lower. <c>microstrip</c> is brief 3 gate 1's, as it
    /// stands; <c>via</c> is gate 3's plated via transition with F0's 1500 µm of air on every side;
    /// <c>small-microstrip</c> is the microstrip in a 2 mm box at order 1, for the Gmsh gates.</summary>
    private static (Em3dProblem, PalaceSettings) Lowerable(string name)
    {
        var (setup, source) = name == "via" ? Em3dGeneratorTests.CaseB(plated: true) : Em3dGeneratorTests.Microstrip();
        if (name == "via") setup.AirBox = Padded(1500);
        if (name == "small-microstrip") Shrink(setup);
        var r = Em3dGenerator.Generate(setup, source, source.Technology!);
        Assert.True(r.Ok, r.Refusal);
        return (r.Problem!, PalaceSettings.Resolve(setup.Palace));
    }

    private static void Shrink(EmSetup setup)
    {
        var face = new EmAirBoxFace(2000, null);
        setup.AirBox = new EmAirBox(face, face, face, face, null, face);
        setup.Palace = new CemPalace { ElementOrder = 1, AdaptiveMaxIterations = 0 };
    }

    internal static EmAirBox Padded(double um)
    {
        var face = new EmAirBoxFace(um, null);
        return new EmAirBox(face, face, face, face, face, face);
    }

    /// <summary>A workspace holding the small microstrip — .cws, technology, cell layout, .cem — laid
    /// out as the GUI lays one out. Returns the .cem's path.</summary>
    private string SmallWorkspace(string name)
    {
        string ws = Path.Combine(_root, name);
        var (setup, source) = Em3dGeneratorTests.Microstrip();
        Shrink(setup);
        setup.Name = "ms";
        setup.LayoutRef = Path.Combine("ms", "layout", "ms.clay");
        Directory.CreateDirectory(Path.Combine(ws, "ms", "layout"));
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), source.Technology!);
        LayoutPersistence.SaveToFile(Path.Combine(ws, setup.LayoutRef), source.View);
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        string cem = Path.Combine(ws, "ms.cem");
        EmSetupPersistence.SaveToFile(cem, setup);
        return cem;
    }

    /// <summary>The stripline for gate 6: a strip of width <paramref name="w"/> and length
    /// <paramref name="len"/> as a 1 µm sheet centred in 1 mm of εr <paramref name="er"/>, over an
    /// undrawn ground plane (the PEC floor) and under a PEC top face.</summary>
    internal static (EmSetup, EmLayoutSource) Stripline(double w, double b, double len, double er)
    {
        long lower = (long)Math.Round(b / 2 / Um * 1000), upper = lower - 1000;
        string erText = er.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        string tech = $$"""
            {
              "FormatVersion": 1, "Name": "stripline",
              "Layers": [ { "Key": { "Layer": 1, "Datatype": 0 }, "Name": "Strip" } ],
              "Stackup": { "Top": "Open", "Bottom": "Open", "Layers": [
                { "Kind": "Dielectric", "Name": "Upper", "ThicknessDbu": {{upper}}, "Epsr": {{erText}}, "TanD": 0, "Mur": 1, "SigmaSm": 0, "DrawingLayers": [] },
                { "Kind": "Conductor", "Name": "Strip", "ThicknessDbu": 1000, "Epsr": 1, "TanD": 0, "Mur": 1, "SigmaSm": 58000000,
                  "DrawingLayers": [ { "Layer": 1, "Datatype": 0 } ] },
                { "Kind": "Dielectric", "Name": "Lower", "ThicknessDbu": {{lower}}, "Epsr": {{erText}}, "TanD": 0, "Mur": 1, "SigmaSm": 0, "DrawingLayers": [] },
                { "Kind": "Conductor", "Name": "Ground", "ThicknessDbu": 35000, "Epsr": 1, "TanD": 0, "Mur": 1, "SigmaSm": 58000000,
                  "DrawingLayers": [ { "Layer": 2, "Datatype": 0 } ], "IsGroundReference": true }
              ] }
            }
            """;
        long half = (long)Math.Round(w / 2 / Um * 1000), length = (long)Math.Round(len / Um * 1000);
        string clay = $$"""
            {
              "FormatVersion": 1, "DbuPerMicron": 1000, "DisplayUnit": "Mm", "SnapDbu": 10000,
              "Shapes": [
                { "$type": "Rect", "Layer": { "Layer": 1, "Datatype": 0 }, "X1": 0, "Y1": {{-half}}, "X2": {{length}}, "Y2": {{half}} },
                { "$type": "Label", "Layer": { "Layer": 1, "Datatype": 0 }, "X": 0, "Y": 0, "Text": "1", "Height": 400000,
                  "IsPort": true, "PortDirection": "R0" },
                { "$type": "Label", "Layer": { "Layer": 1, "Datatype": 0 }, "X": {{length}}, "Y": 0, "Text": "2", "Height": 400000,
                  "IsPort": true, "PortDirection": "R180" }
              ],
              "Instances": []
            }
            """;
        var view = LayoutPersistence.Deserialize(clay);
        var t = TechPersistence.Deserialize(tech);
        double padUm = 5 * b / Um;
        var side = new EmAirBoxFace(padUm, Em3dBoundaryKind.Absorbing);
        var wall = new EmAirBoxFace(padUm, Em3dBoundaryKind.Pec);
        var setup = new EmSetup
        {
            Name = "stripline", LayoutRef = "stripline.clay",
            Frequency = new FrequencySpec("1", "4", 7, SweepKind.Linear, "GHz", "GHz"),
            Solver3D = Em3dSolver.Palace,
            AirBox = new EmAirBox(side, side, wall, wall, null, new EmAirBoxFace(0, Em3dBoundaryKind.Pec)),
            // 125 µm at the strip — four elements through each half of the stack — and no adaptive
            // passes, so the run is one fixed mesh and the closed form is judged on it.
            Palace = new CemPalace { AdaptiveMaxIterations = 0, EdgeRefinement = 0.025 },
        };
        return (setup, new EmLayoutSource("/nowhere/stripline.clay", view, t, view.DbuPerMicron));
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    private string Dir(string name)
    {
        string d = Path.Combine(_root, name);
        Directory.CreateDirectory(d);
        return d;
    }

    private static string Gmsh() => SolverDiscovery.Gmsh.Find(out _)!.Path;

    internal static bool Alive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static string[] WithoutWriteStamp(IEnumerable<string> lines)
        => [.. lines.Where(l => !l.Contains("circuitRF-EM written:", StringComparison.Ordinal))];

    internal static double[] Unwrap(double[] p)
    {
        var o = (double[])p.Clone();
        for (int i = 1; i < o.Length; i++)
            while (o[i] - o[i - 1] > Math.PI) o[i] -= 2 * Math.PI;
        for (int i = 1; i < o.Length; i++)
            while (o[i] - o[i - 1] < -Math.PI) o[i] += 2 * Math.PI;
        return o;
    }

    internal static double Slope(double[] x, double[] y)
    {
        double mx = x.Average(), my = y.Average();
        return x.Zip(y, (a, c) => (a - mx) * (c - my)).Sum() / x.Sum(a => (a - mx) * (a - mx));
    }

    /// <summary>The complete elliptic integral of the first kind, K(k), by the arithmetic-geometric mean.</summary>
    private static double EllipticK(double k)
    {
        double a = 1, g = Math.Sqrt(1 - k * k);
        for (int i = 0; i < 40 && Math.Abs(a - g) > 1e-16; i++) (a, g) = ((a + g) / 2, Math.Sqrt(a * g));
        return Math.PI / (2 * a);
    }

    private static string StripComments(string code)
        => Regex.Replace(code, @"//[^\n]*|/\*.*?\*/", "", RegexOptions.Singleline);

    private static List<string> RepoCems()
        => [.. new[] { "examples", "testdata" }
               .SelectMany(d => Directory.EnumerateFiles(Path.Combine(RepoRoot(), d), "*.cem", SearchOption.AllDirectories))
               .Order(StringComparer.Ordinal)];

    internal static string RepoRoot()
    {
        for (string? dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
            if (File.Exists(Path.Combine(dir, "circuitrf.slnx"))) return dir;
        throw new InvalidOperationException("repo root not found");
    }
}

/// <summary>The built CLI as a process, both pipes drained concurrently (EmCliVerbTests' pattern and
/// its reasons), with extra environment variables for this one process.</summary>
internal static class CliProcess
{
    public static (int ExitCode, string StdOut, string StdErr) Run(
        string repo, IReadOnlyList<(string Name, string Value)> env, params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = repo, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        string cliDir = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(CliProcess).Assembly)
            .First(a => a.Key == "CliDir").Value!;
        psi.ArgumentList.Add(Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll")));
        foreach (string a in args) psi.ArgumentList.Add(a);
        foreach (var (name, value) in env) psi.Environment[name] = value;
        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }
}

/// <summary>Skips, naming what is missing, unless discovery finds a validated Gmsh.</summary>
public sealed class GmshFactAttribute : FactAttribute
{
    private static readonly Lazy<string?> Reason = new(() =>
        SolverDiscovery.Gmsh.Check(SolverDiscovery.CapabilitiesFor(SolverTool.Gmsh)) is { Proceeds: false } r
            ? $"needs a validated Gmsh: {r.Refusal}" : null);

    public GmshFactAttribute()
    {
        if (Reason.Value is { } why) Skip = why;
    }
}

/// <summary>Skips, naming what is missing, unless a Palace run would proceed — Palace found, validated
/// and probed, and Gmsh found and validated (the run's own ReadinessFor).</summary>
public sealed class PalaceFactAttribute : FactAttribute
{
    private static readonly Lazy<string?> Reason = new(() =>
        SolverDiscovery.ReadinessFor(Em3dSolver.Palace).FirstOrDefault(r => !r.Proceeds) is { } r
            ? $"needs Palace and Gmsh (set CIRCUITRF_PALACE on a machine with F0's installs): {r.Refusal}" : null);

    public PalaceFactAttribute()
    {
        if (Reason.Value is { } why) Skip = why;
    }
}

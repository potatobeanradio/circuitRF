// ================================================================
//  AntennaExampleTests.cs — ANT-12 §3a/§4/§5: the shipped example antenna IS a test
//
//  `testdata/antenna/` is a complete circuitRF workspace holding one inset-fed 5.8 GHz patch on the
//  shipped RO4350B 30 mil technology. It is what reference/antennas.html is written about, and it is
//  the only antenna in the tree that is known to be right — so the numbers the user page quotes have
//  to be re-derivable from it, headless, by the CLI.
//
//  TWO TIERS, and the split is the series' own standing rule ("take expensive measurements in a
//  scratch harness and report the number; gate the structural property"):
//
//    * ROUTINE — everything that is true of the FILES. The fixture is canonical, it asks for the
//      pattern, its mesh intent is the radiating sheet, its boundary cells are staircase (the far
//      field refuses a cut cell), `circuitrf check` passes on it, and its mesh is under the ceiling.
//      Milliseconds, and this is the tier that catches the example rotting.
//
//    * Category=Benchmark — the whole sweep through the `em` verb as a PROCESS, with the metrics
//      compared against the numbers on the page. ~4.5 min on the measuring machine, so it is opt-in
//      (`dotnet test --settings circuitrf.benchmark.runsettings`). A one-point variant would have been
//      cheaper and would not have gated the thing worth gating: the resonance is found by the
//      resonance search, over the sweep.
// ================================================================

using System.Diagnostics;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Engine.Mom;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Em;

public sealed class AntennaExampleTests(ITestOutputHelper output)
{
    // ── The numbers reference/antennas.html quotes, and the tolerances they are quoted to ─────────
    //
    // Each is a measured value with a band wide enough to survive a re-mesh that does not change the
    // physics, and narrow enough to fail if something does. The resonance band is the tightest because
    // it is the one compared against an INDEPENDENT oracle (the cavity model, 5.796 GHz) — the page
    // quotes the agreement, so the agreement is what is gated.
    private const double ResonanceGHz   = 5.8131, ResonanceTolGHz  = 0.010;
    private const double CavityModelGHz = 5.7968, CavityAgreementPc = 0.50;
    /// <summary>The requested grid point nearest the resonance — the one the page's table is about.</summary>
    private const double PageFreqGHz    = 5.85;
    private const double DirectivityDbi = 6.697, DirectivityTolDb = 0.15;
    private const double GainDbi        = 4.664, GainTolDb        = 0.15;
    private const double Efficiency     = 0.626, EfficiencyTol    = 0.030;
    private const double BeamwidthDeg   = 146.1, BeamwidthTolDeg  = 3.0;
    private const int    UnknownCount   = 1611,  UnknownTol       = 60;

    private static string RepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is { Length: > 0 } && !File.Exists(Path.Combine(dir, "circuitRF.slnx")))
            dir = Path.GetDirectoryName(dir) ?? "";
        Assert.False(string.IsNullOrEmpty(dir), "could not locate the repository root");
        return dir;
    }

    private static string ExampleRoot() => Path.Combine(RepoRoot(), "testdata", "antenna");
    private static string CemPath() =>
        Path.Combine(ExampleRoot(), "patch", "em", "patch-5p8GHz.cem");

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  ROUTINE — the fixture itself
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The committed <c>.cem</c> is the bytes circuitRF writes</b>, not bytes somebody typed that
    /// happen to load. Asserted by round-tripping it through the app's own serializer: a fixture that
    /// is not canonical is one the first GUI save would rewrite, which turns an unrelated commit into
    /// a diff on the example.
    /// </summary>
    [Fact]
    public void TheExamplesCemIsCanonical()
    {
        string path = CemPath();
        string onDisk = File.ReadAllText(path);
        string canonical = EmSetupPersistence.Serialize(EmSetupPersistence.LoadFromFile(path));
        Assert.Equal(canonical.ReplaceLineEndings("\n"), onDisk.ReplaceLineEndings("\n"));
    }

    /// <summary>
    /// <b>The three settings that make this an antenna run</b>, each of which the user page tells a
    /// reader to make and each of which silently changes the answer if it goes missing. The radiation
    /// pattern is the whole point; the radiating-sheet intent is what stops the mesher looking for a
    /// current direction on a wide patch; and staircase boundary cells are a REQUIREMENT, not a
    /// preference — the far field does not transform a conformal cell and refuses by name.
    /// </summary>
    [Fact]
    public void TheExampleAsksForThePattern_OnASheetMesh_WithStaircaseCells()
    {
        var setup = EmSetupPersistence.LoadFromFile(CemPath());

        Assert.True(setup.RadiationPattern);
        Assert.Equal(EmAnalysisKind.Planar, setup.AnalysisKind);
        Assert.Equal(PlanarCurrentModel.Sheet, setup.PlanarMesh.CurrentModel);
        Assert.Equal(PlanarBoundaryCells.Staircase, setup.PlanarMesh.BoundaryCells);
        Assert.True(setup.AdaptiveSampling);
        Assert.True(setup.ResonanceSearch);
    }

    /// <summary>
    /// <b><c>circuitrf check</c> passes on the workspace</b> — §4's own gate, and the cheapest proof
    /// that every reference in it resolves: the <c>.cws</c>'s technology, the <c>.cem</c>'s layout, the
    /// port onto metal, the stackup onto the drawing layers.
    /// </summary>
    [Fact]
    public void CircuitrfCheck_PassesOnTheExampleWorkspace()
    {
        var (exit, stdout, stderr) = RunCli(RepoRoot(), "check", ExampleRoot());
        output.WriteLine(stdout);
        output.WriteLine(stderr);
        Assert.Equal(0, exit);
    }

    /// <summary>
    /// <b>The mesh is under the ceiling, and it is the mesh the page quotes.</b> Extract and mesh
    /// only — no solve, so this is milliseconds — which is exactly the measurement the page's
    /// "the example uses 20 cells/λ; a design run wants 30" sentence rests on.
    /// </summary>
    [Fact]
    public void TheExampleMeshesToTheUnknownCountThePageQuotes()
    {
        var (setup, source) = Resolve();
        double fMax = setup.Frequency.Expand().Max();
        var extraction = PlanarExtractor.Extract(
            source.View.Shapes, source.Technology!, source.DbuPerMicron, fMax,
            setup.ToExtractionSettings());
        Assert.True(extraction.Ok, extraction.Refusal);

        // THROUGH THE KERNEL'S OWN Mesh, never SurfaceMesher directly: the kernel pins the edge
        // reference (LocalConductorWidth) that the GUI's Mesh button and the run both use, and meshing
        // past it reported 1,985 unknowns against the run's 1,611 — a test that would have been
        // measuring a mesh nothing builds.
        var mesh = new PlanarKernel().Mesh(extraction.Problem!, setup.PlanarMesh);
        output.WriteLine($"N = {mesh.Mesh.Bases.Count}, {mesh.Mesh.Cells.Count} cells");
        Assert.InRange(mesh.Mesh.Bases.Count, UnknownCount - UnknownTol, UnknownCount + UnknownTol);

        // Every basis horizontal and nothing attached to ground: that is the condition ANT-4's far
        // field requires, and it is a property of the ARTWORK (an edge-fed patch, no vias) rather
        // than of the mesh settings — so it belongs here rather than being discovered by a refusal
        // four minutes into a run.
        Assert.All(mesh.Mesh.Bases, b =>
        {
            Assert.NotEqual(PlanarBasisDirection.Z, b.Direction);
            Assert.False(b.AttachesToGround);
        });
    }

    /// <summary>
    /// <b>No personal path, workspace name or user name anywhere in the fixture</b> — ANT-12 §6, and
    /// the standing rule for every fixture in this repository. The fixture carries the SHAPE of a
    /// design, never its provenance.
    /// </summary>
    [Fact]
    public void TheFixtureCarriesNoPersonalPathOrName()
    {
        foreach (string f in Directory.EnumerateFiles(ExampleRoot(), "*", SearchOption.AllDirectories))
        {
            string text = File.ReadAllText(f);
            Assert.DoesNotContain("/Users/", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("C:\\Users", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("/home/", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  Category=Benchmark — the whole run, through the CLI, against the page's numbers
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>§5 — the example runs end to end through the CLI, headless, and its metrics sit inside
    /// stated tolerances.</b> The sweep is run on a COPY of the workspace so the repository gains no
    /// <c>results/</c> directory, and the verdict is read out of the <c>.npy</c> the run writes, which
    /// is the file a user would read.
    ///
    /// <para>Measured at ~4.5 min, which is why it is opt-in. What it proves that the routine tier
    /// cannot: the pattern cubes are produced at all, the resonance search finds f₀ where the cavity
    /// model says it is, and the directivity and efficiency are the numbers the page prints.</para>
    /// </summary>
    [Fact]
    [Trait("Category", "Benchmark")]
    public void TheExampleRunsHeadless_AndItsMetricsMatchTheUserPage()
    {
        string work = Path.Combine(Path.GetTempPath(), "crf-ant12-" + Guid.NewGuid().ToString("N")[..12]);
        try
        {
            CopyTree(ExampleRoot(), work);
            string cem = Path.Combine(work, "patch", "em", "patch-5p8GHz.cem");

            var sw = Stopwatch.StartNew();
            var (exit, stdout, stderr) = RunCli(work, "em", cem);
            sw.Stop();
            output.WriteLine($"`circuitrf em` took {sw.Elapsed.TotalSeconds:F1} s");
            output.WriteLine(stdout);
            Assert.Equal(0, exit);

            // The pattern was produced, and the ONE refusal the page names is the refusal that
            // fired — not a silent absence.
            Assert.Contains("Far field, port 1 driven", stderr);
            Assert.Contains("Power budget", stderr);
            Assert.Contains("FrontToBackDb is not published", stderr);

            // …and the realized gain is PUBLISHED, not refused: a sweep de-embeds a point before it
            // takes that point's pattern, so the mismatch factor reads the published S_11 rather
            // than the delta gap's own raw admittance (ANT-12's correction, wired).
            Assert.DoesNotContain("RealizedGainDbi is not published", stderr);

            // The resonance, against the cavity model the page compares it to.
            int at = stderr.IndexOf("f0 = ", StringComparison.Ordinal);
            Assert.True(at > 0, "the resonance search reported no resonance");
            double f0 = double.Parse(stderr[(at + 5)..].Split(' ')[0]);
            output.WriteLine($"f0 = {f0:F4} GHz (page says {ResonanceGHz:F4})");
            Assert.InRange(f0, ResonanceGHz - ResonanceTolGHz, ResonanceGHz + ResonanceTolGHz);

            // And the agreement with the INDEPENDENT oracle, which is the claim the page makes about
            // this antenna rather than a claim about the solver agreeing with itself.
            double err = 100.0 * (f0 - CavityModelGHz) / CavityModelGHz;
            output.WriteLine($"cavity model {CavityModelGHz:F4} GHz, solver {f0:F4} GHz, {err:+0.00;-0.00} %");
            Assert.InRange(Math.Abs(err), 0.0, CavityAgreementPc);

            // Every number the page's table prints, at the grid point it prints them for, out of the
            // written result rather than out of a sentence.
            var ds = ReadBackNpy(Path.Combine(work, "results"));
            int i = NearestFreq(ds, "DirectivityDbi", PageFreqGHz * 1e9);

            double d   = At(ds, "DirectivityDbi", i);
            double g   = At(ds, "GainDbi", i);
            // PERCENT since 2026-09-11 — the cube's own unit says so. Read back as a fraction here
            // because every identity below is written in fractions, and the dB cube published
            // beside it is asserted against the same number.
            double etaPc = At(ds, "RadiationEfficiency", i);
            double eta   = etaPc / 100.0;
            Assert.Equal("%", ds.CubesIn(PlanarFarField.Group)["RadiationEfficiency"].Unit);
            Assert.Equal(10.0 * Math.Log10(eta), At(ds, "RadiationEfficiencyDb", i), 9);
            double bw  = At(ds, "BeamwidthDeg", i);
            output.WriteLine($"at {PageFreqGHz} GHz: D {d:F2} dBi, G {g:F2} dBi, " +
                             $"eta {eta:P1}, E-plane beamwidth {bw:F1} deg");

            Assert.InRange(d,   DirectivityDbi - DirectivityTolDb, DirectivityDbi + DirectivityTolDb);
            Assert.InRange(g,   GainDbi        - GainTolDb,        GainDbi        + GainTolDb);
            Assert.InRange(eta, Efficiency     - EfficiencyTol,    Efficiency     + EfficiencyTol);
            Assert.InRange(bw,  BeamwidthDeg   - BeamwidthTolDeg,  BeamwidthDeg   + BeamwidthTolDeg);

            // G = D·eta, the identity that says the two are not two estimates of one thing.
            Assert.Equal(d + 10.0 * Math.Log10(eta), g, 6);

            // ANT-12's own fixes: the beamwidth cut axis is ONE plane and the Ludwig-3 pair is
            // published. Both were refused on this antenna before the round-off tolerances were
            // sized from the quantity they are tolerances of.
            Assert.Single(ds.CubesIn(PlanarFarField.Group)["BeamwidthDeg"].Axes[1].Values);
            Assert.True(ds.CubesIn(PlanarPolarization.Group).ContainsKey("CoPolLudwig3Db"));
        }
        finally { try { Directory.Delete(work, true); } catch { /* best effort */ } }
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────

    private static (EmSetup Setup, EmLayoutSource Source) Resolve()
    {
        string cem = CemPath();
        var setup = EmSetupPersistence.LoadFromFile(cem);
        var resolved = EmSetupResolver.Resolve(
            cem, setup.LayoutRef, Path.Combine(ExampleRoot(), ".cws"), new TechnologyCache());
        Assert.NotNull(resolved.Source);
        Assert.NotNull(resolved.Source!.Technology);
        return (setup, resolved.Source);
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string d in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, d)));
        foreach (string f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(f, Path.Combine(to, Path.GetRelativePath(from, f)), true);
    }

    /// <summary>The written result, read back through the importer that is the reader for what the
    /// run wrote — never a hand-rolled parse of the same bytes.</summary>
    private static RfCore.Data.DataSet ReadBackNpy(string resultsDir)
    {
        string npy = Directory.EnumerateFiles(resultsDir, "*_em.npy").Single();
        return RfCore.Export.DataSetImporter.Import(npy).DataSet;
    }

    /// <summary>The index on a far-field cube's own freq axis nearest a wanted frequency. The axis
    /// carries the points that were SOLVED, which the resonance search adds to, so it is looked up
    /// rather than assumed.</summary>
    private static int NearestFreq(RfCore.Data.DataSet ds, string cube, double wantHz)
    {
        var values = ds.CubesIn(PlanarFarField.Group)[cube].Axes[0].Values;
        int at = 0;
        for (int i = 1; i < values.Length; i++)
            if (Math.Abs(values[i] - wantHz) < Math.Abs(values[at] - wantHz)) at = i;
        return at;
    }

    /// <summary>One value of a one-port cube at a frequency index — the port axis is length 1 here and
    /// a beamwidth's cut axis is too, so the row is the first element of that frequency's block.</summary>
    private static double At(RfCore.Data.DataSet ds, string cube, int freqIndex)
    {
        var c = ds.CubesIn(PlanarFarField.Group)[cube];
        int stride = 1;
        for (int k = 1; k < c.Axes.Count; k++) stride *= c.Axes[k].Length;
        return c.RealValues[freqIndex * stride];
    }

    /// <summary>
    /// The built CLI as a process — EmCliVerbTests' own launcher, reused verbatim including the
    /// reasons: never `dotnet run --project src/Cli` (a nested MSBuild inside `dotnet test` hangs),
    /// and both pipes drained concurrently (an `em` run fills stderr).
    /// </summary>
    private static (int ExitCode, string StdOut, string StdErr) RunCli(string cwd, params string[] args)
    {
        string cliDir = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(
                typeof(AntennaExampleTests).Assembly)
            .First(a => a.Key == "CliDir").Value!;
        string dll = Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll"));
        Assert.True(File.Exists(dll), $"the CLI was not built beside these tests: {dll}");

        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory       = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        psi.ArgumentList.Add(dll);
        foreach (string a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        var o = proc.StandardOutput.ReadToEndAsync();
        var e = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, o.GetAwaiter().GetResult(), e.GetAwaiter().GetResult());
    }
}

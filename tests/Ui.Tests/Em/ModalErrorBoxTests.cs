// PCAL4 — "two ports on coupled conductors need a modal error box".
// `docs/sonnet-briefs/brief-portcal-4-modal-error-box.md`, gates 1-6.
//
// The algebra is `Engine.Tests/Mom/PlanarModalCalibrationTests.cs`, on synthetic data where the
// answer is CHOSEN rather than compared; the group formation and the standard's construction are
// `Engine.Tests/Mom/PlanarCalibrationGroupTests.cs`. What lives here is what neither can see: the
// committed fixtures, resolved exactly as the Simulate button resolves them, running END TO END
// where PCAL2 refused them, and the run's own notes carrying the per-frequency diagnostics gate 5
// asks for.
//
// The ACCURACY measurement — every fixture against the cross-section oracle, and the A-vs-B floor
// each is measured against — is in `src/Engine/Mom/RESOLVED.md`, §PCAL4. It needs two kernels at
// seven frequencies on five geometries and is a harness run, which is the standing rule about
// measuring outside the Benchmark tier.

using System.Diagnostics;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Layout.Em;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Em;

public sealed class ModalErrorBoxTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _results = Path.Combine(
        Path.GetTempPath(), "crf-pcal4-" + Guid.NewGuid().ToString("N")[..12], "results");

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_results)!, true); } catch { /* best effort */ }
    }

    private static string RepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is { Length: > 0 } && !File.Exists(Path.Combine(dir, "circuitRF.slnx")))
            dir = Path.GetDirectoryName(dir) ?? "";
        return dir;
    }

    private static string Portcal => Path.Combine(RepoRoot(), "testdata", "portcal");

    private static (EmSetup Setup, EmLayoutSource Source) Fixture(string name)
    {
        string cem = Path.Combine(Portcal, name, "em", name + ".cem");
        var setup  = EmSetupPersistence.LoadFromFile(cem);
        var r = EmSetupResolver.Resolve(
            cem, setup.LayoutRef, Path.Combine(Portcal, ".cws"), new TechnologyCache());
        Assert.NotNull(r.Source);
        Assert.NotNull(r.Source!.Technology);
        return (setup, r.Source!);
    }

    /// <summary>One frequency instead of seven. Everything gated here but the cost note is decided
    /// per point, and the bottom of the band is where PCAL1 measured the conditioning to be
    /// worst — so one point at 1 GHz is the hard case, not a cheap one.</summary>
    private static EmSetup OnePoint(EmSetup s)
    {
        var c = s.Clone();
        c.Frequency = new CircuitRF.Core.Design.FrequencySpec(
            "1", "1", 1, CircuitRF.Core.Design.SweepKind.Linear, "GHz", "GHz");

        // …and the COARSEST mesh that still resolves the ports. Everything gated here is a decision
        // (which ports group, what the notes say, how many modes) and none of it is a number the
        // mesh moves; what the mesh moves is the run's clock, and at the shipping mesh these gates
        // are 14-35 s each in the DEBUG build `dotnet test` makes — over the Benchmark threshold,
        // which would take the whole of PCAL4's end-to-end coverage out of the default gate. The
        // ACCURACY measurement is at the shipping mesh and is in RESOLVED.md, where it belongs.
        c.PlanarMesh = c.PlanarMesh with { CellsPerWavelength = 2, EdgeMesh = false };
        return c;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // GATE 1 — the case the series was opened on runs, as one group per reference plane
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <c>coupled-pair</c> is the geometry the whole series started from: two 254 µm microstrips
    /// 246 µm apart — 0.27 substrate heights against the 5 a driven neighbour needs — with a port at
    /// each of the four ends. PCAL2 refuses it and writes no file; PCAL4 calibrates each plane's two
    /// ports TOGETHER, and the run publishes.
    /// </summary>
    [Fact]
    public void Gate1_TheDrivenPairRuns_AsOneCalibrationGroupPerReferencePlane()
    {
        var (setup, source) = Fixture("coupled-pair");
        var r = EmRunService.Run(OnePoint(setup), source, _results);

        output.WriteLine(r.Error ?? "(no error)");
        Assert.Equal(EmRunStatus.Ok, r.Status);
        Assert.NotNull(r.SnpPath);

        var groups = r.Notes!.Where(n => n.Contains("CALIBRATION GROUP", StringComparison.Ordinal)).ToList();
        foreach (string g in groups) output.WriteLine(g);
        Assert.Equal(2, groups.Count);
        Assert.Contains(groups, g => g.Contains("Ports 1, 3", StringComparison.Ordinal));
        Assert.Contains(groups, g => g.Contains("Ports 2, 4", StringComparison.Ordinal));
        Assert.All(groups, g => Assert.Contains("2 modes", g, StringComparison.Ordinal));

        // The clearance breach that used to refuse the run is cleared by the grouping itself: the
        // neighbour is IN the standard now, so it is not a neighbour any more.
        Assert.DoesNotContain(r.Notes!, n => n.Contains("not isolated", StringComparison.Ordinal));

        // And the file declares nothing about VALIDITY, because the calibration is not being applied
        // outside its validity — it is a different calibration.
        var caveats = EmSnpProvenance.ReadCaveats(r.SnpPath!);
        foreach (string c in caveats) output.WriteLine("caveat: " + c);
        Assert.DoesNotContain(caveats, c => c.Contains("OUTSIDE", StringComparison.Ordinal));

        // R-pcal7-4 — what it DOES declare is a fact this run has always reported and the file has
        // never carried: this single point comes back with sigma_max just over 1, so it is not a
        // network, and the run says so. A `.sNp` carries no findings.
        //
        // Read off WARNINGS rather than notes since 2026-09-16 (EM-SEV R-emsev-1): a sentence whose
        // own words are "the s-parameters at those points should not be used" is the warning class
        // by definition, and it had been arriving as Info.
        bool notPassive = r.Warnings.Any(n => n.Contains("NOT PASSIVE", StringComparison.Ordinal));
        Assert.DoesNotContain(r.Notes! ?? [], n => n.Contains("NOT PASSIVE", StringComparison.Ordinal));
        Assert.Equal(notPassive, caveats.Any(c => c.Contains("NOT A PASSIVE NETWORK", StringComparison.Ordinal)));
    }

    /// <summary>Gate 1's other half: the CLI takes the same decision from the same code and exits
    /// zero, where it used to exit non-zero having written nothing.</summary>
    [Trait("Category", "Benchmark")]
    [Fact]
    public void Gate1_TheCliRunsTheSameGeometry_AndExitsZero()
    {
        string repo = RepoRoot();
        string cem  = Path.Combine(Portcal, "coupled-pair", "em", "coupled-pair.cem");

        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = repo, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (string a in new[] { "run", "-c", "Release", "--project", "src/Cli", "--", "em", cem })
            psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        string stdout = p.StandardOutput.ReadToEnd(), stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();

        output.WriteLine($"exit {p.ExitCode}\n{stdout}\n{stderr}");
        Assert.Equal(0, p.ExitCode);
        Assert.Contains("CALIBRATION GROUP", stdout + stderr, StringComparison.Ordinal);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // GATES 3 and 4 — a three-conductor group and an ASYMMETRIC pair, because a symmetric pair is
    // the one case an even/odd assumption would also pass
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <c>coupled-asym</c> is a 254 µm line beside a 508 µm one at the same 246 µm — two modes whose
    /// voltage patterns are NOT (1,1) and (1,−1), so nothing here can be passing because of a
    /// symmetry it assumed. The modes' ε_eff must come out distinct and their Z_c must differ.
    /// </summary>
    [Fact]
    public void Gate4_AnAsymmetricPairIsCalibratedAsAGroup_WithTwoDistinctModes()
    {
        var (setup, source) = Fixture("coupled-asym");
        var r = EmRunService.Run(OnePoint(setup), source, _results);

        Assert.Equal(EmRunStatus.Ok, r.Status);
        string line = Assert.Single(r.Notes!, n => n.Contains("ports 1+3", StringComparison.Ordinal));
        output.WriteLine(line);

        Assert.Contains("ε_eff", line, StringComparison.Ordinal);
        Assert.Contains("Z_c", line, StringComparison.Ordinal);
        Assert.Equal(2, line.Split('·').Length);              // exactly two modes reported
    }

    /// <summary><c>coupled-triple</c> is three conductors of three different widths at 246 µm — a
    /// group of three, three modes, one 6-port standard.</summary>
    [Fact]
    public void Gate3_ThreeCoupledConductorsAreOneGroupOfThree()
    {
        var (setup, source) = Fixture("coupled-triple");
        var r = EmRunService.Run(OnePoint(setup), source, _results);

        Assert.Equal(EmRunStatus.Ok, r.Status);
        var groups = r.Notes!.Where(n => n.Contains("CALIBRATION GROUP", StringComparison.Ordinal)).ToList();
        foreach (string g in groups) output.WriteLine(g);
        Assert.Equal(2, groups.Count);
        Assert.Contains(groups, g => g.Contains("Ports 1, 3, 5", StringComparison.Ordinal));
        Assert.All(groups, g => Assert.Contains("3 modes", g, StringComparison.Ordinal));

        string line = Assert.Single(r.Notes!, n => n.Contains("ports 1+3+5", StringComparison.Ordinal));
        output.WriteLine(line);
        Assert.Equal(3, line.Split('·').Length);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // GATE 5 — the mode separation and the discarded residuals are REPORTED, per frequency
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>R-pcal4-2 and R-pcal4-3 both ask for these to be reported, not merely acted on</b>, and
    /// PCAL1 measured why the summary has to name the frequency it happened at: the error is worst
    /// at the BOTTOM of the band in every case measured, so a sweep average hides exactly the point
    /// that decides whether the answer is usable.
    /// </summary>
    [Fact]
    public void Gate5_TheModeSeparationAndEveryDiscardedResidualAreInTheRunsNotes()
    {
        var (setup, source) = Fixture("coupled-pair");
        var r = EmRunService.Run(OnePoint(setup), source, _results);
        Assert.Equal(EmRunStatus.Ok, r.Status);

        string summary = Assert.Single(r.Notes!, n => n.StartsWith("MODAL CALIBRATION", StringComparison.Ordinal));
        output.WriteLine(summary);
        foreach (string k in new[] { "mode separation", "cascade residual", "modal-gauge residual",
                                     "null-space gap", "sign margin", "palindrome" })
            Assert.Contains(k, summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NOT an error bound", summary, StringComparison.Ordinal);

        // R-pcal4-4 — the reference impedance's accuracy is reported SEPARATELY from the
        // de-embedding's, because they are two different things and one figure of merit would hide
        // which of them a run is short on.
        string zc = Assert.Single(r.Notes!, n => n.StartsWith("MODAL REFERENCE IMPEDANCE", StringComparison.Ordinal));
        output.WriteLine(zc);
        Assert.Contains("quasi-static", zc, StringComparison.OrdinalIgnoreCase);

        // …and a per-point line for every group at every frequency.
        var perPoint = r.Notes!.Where(n => n.Contains("separation", StringComparison.Ordinal)
                                       && n.Contains("GHz, ports", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, perPoint.Count);                       // two groups × one frequency
        foreach (string line in perPoint) output.WriteLine(line);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // GATE 6 — what the group COST, against PCAL3's own baseline
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>R-pcal4-7 — a calibration group costs no more MESH than PCAL3's widened profile</b>,
    /// because the metal in the standard is the same metal; what it adds is N port excitations on a
    /// mesh that was going to be solved anyway, and a second electrostatic medium for [L]. The
    /// number is asserted rather than described so that a change to the standard's width cannot pass
    /// unnoticed: on this fixture the standards are 9.14× the DUT's unknowns, which is exactly
    /// PCAL3's own figure on the same geometry.
    /// </summary>
    [Trait("Category", "Benchmark")]
    [Fact]
    public void Gate6_TheGroupsCostIsReported_AndIsPcal3sOwnStandardCost()
    {
        var (setup, source) = Fixture("coupled-pair");
        var sw = Stopwatch.StartNew();
        var r  = EmRunService.Run(setup, source, _results);
        sw.Stop();

        Assert.Equal(EmRunStatus.Ok, r.Status);
        string cost = Assert.Single(r.Notes!, n => n.StartsWith("De-embedding costs", StringComparison.Ordinal));
        output.WriteLine($"{cost}\n7 points in {sw.Elapsed.TotalSeconds:F2} s");

        Assert.Contains("2 calibration(s) over 4 de-embedded port(s)", cost, StringComparison.Ordinal);
        Assert.Contains("9.14×", cost, StringComparison.Ordinal);
    }
}

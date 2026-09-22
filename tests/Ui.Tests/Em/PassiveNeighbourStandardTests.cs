// PCAL3 — "the calibration standard contains the neighbour the feed actually has".
// `docs/sonnet-briefs/brief-portcal-3-passive-neighbour.md`, gates 1-3 and 5.
//
// The engine half — which conductor joins the profile, what is declined and by what name, and that
// the standard is still built from the DUT's own gridlines — is
// `Engine.Tests/Mom/PlanarPassiveNeighbourTests.cs`. What lives here is what the engine cannot see:
// the committed fixture, resolved exactly as the Simulate button resolves it, running END TO END
// where PCAL2 refused it, writing a Touchstone with no caveat on it, and the same decision reached
// by the CLI as a process.
//
// The ACCURACY measurement is in `src/Engine/Mom/RESOLVED.md` and is deliberately not a test: it
// needs the cross-section oracle at seven frequencies and a harness run, which is the standing rule
// about measuring outside the Benchmark tier.

using System.Diagnostics;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Layout.Em;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Em;

public sealed class PassiveNeighbourStandardTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _results = Path.Combine(
        Path.GetTempPath(), "crf-pcal3-" + Guid.NewGuid().ToString("N")[..12], "results");

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

    /// <summary>One frequency instead of seven — the widening is decided at SETUP, so it is the same
    /// decision either way and the sweep is the only part that costs.</summary>
    private static EmSetup OnePoint(EmSetup s)
    {
        var c = s.Clone();
        c.Frequency = new CircuitRF.Core.Design.FrequencySpec(
            "1", "1", 1, CircuitRF.Core.Design.SweepKind.Linear, "GHz", "GHz");
        return c;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // GATE 1 — the fixture PCAL2 refuses now RUNS, and it says what it reproduced
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <c>coupled-pair-passive</c> is the series' own coupled pair with the neighbour's ports
    /// deleted: 243 µm of clearance, 0.27 substrate heights, against the 2 a portless neighbour
    /// needs. Until PCAL3 that was a refusal with no `.sNp`; the neighbour is reproduced in the
    /// standard instead and the run publishes.
    /// </summary>
    [Fact]
    public void Gate1_ThePassiveFixtureRuns_AndTheStandardSaysWhatItReproduced()
    {
        var (setup, source) = Fixture("coupled-pair-passive");
        var r = EmRunService.Run(OnePoint(setup), source, _results);

        foreach (string n in r.Notes!) output.WriteLine("note: " + n);
        Assert.Equal(EmRunStatus.Ok, r.Status);
        Assert.NotNull(r.SnpPath);

        // It was a REFUSAL before, so the decision has to be visible: the widening says so, and the
        // clearance margin that used to be the breach now reads clear.
        Assert.Contains(r.Notes!, n => n.Contains("neighbouring conductor(s) beside the feed",
                                                 StringComparison.Ordinal));
        Assert.Contains(r.Notes!, n => n.Contains("246 µm away", StringComparison.Ordinal)
                                   || n.Contains("243 µm away", StringComparison.Ordinal));
        Assert.DoesNotContain(r.Notes!, n => n.Contains("OUTSIDE the condition", StringComparison.Ordinal));

        // Nothing was published outside the calibration's validity, so the file declares nothing.
        Assert.Empty(EmSnpProvenance.ReadCaveats(r.SnpPath!));
    }

    /// <summary>The same decision, taken by the CLI as a process, exiting zero where it used to exit
    /// one. Gate 1's other half and the same code — there is no second copy of it.</summary>
    [Fact]
    public void Gate1_TheCliRunsTheSameGeometry_AndExitsZero()
    {
        string repo = RepoRoot();
        string cem  = Path.Combine(Portcal, "coupled-pair-passive", "em", "coupled-pair-passive.cem");

        var (exit, stdout, stderr) = RunCli(repo, "em", cem);
        output.WriteLine($"exit {exit}\nstdout:\n{stdout}\nstderr:\n{stderr}");

        Assert.Equal(0, exit);
        Assert.Contains("neighbouring conductor(s) beside the feed", stdout + stderr,
                        StringComparison.Ordinal);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // GATE 3 — the driven pair is STILL refused, because brief 4 is a different piece of algebra
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The same metal at the same 243 µm, with the neighbour's ports back on it, is PCAL2's refusal
    /// unchanged — reproducing a driven neighbour does not give D6's per-port scalar error box a
    /// second mode to describe. Without this the two fixtures could not tell a widening that works
    /// from one that quietly widened everything.
    /// </summary>
    ///
    /// <para><b>The fixture moved at PCAL4 and this gate's meaning did not.</b> `coupled-pair` is a
    /// calibration GROUP now and runs; what still reaches PCAL2's refusal by way of a driven
    /// neighbour the widening will not touch is `offset-pair`, whose neighbour carries ports at a
    /// different station and so shares no plane with this one.</para>
    [Fact]
    public void Gate3_ADrivenNeighbourIsStillRefused_AndSaysWhyItCannotBeWidened()
    {
        var (setup, source) = Fixture("offset-pair");
        var r = EmRunService.Run(OnePoint(setup), source, _results);

        output.WriteLine(r.Error ?? "(not refused)");
        Assert.Equal(EmRunStatus.Refused, r.Status);
        Assert.Null(r.SnpPath);
        Assert.Equal("em.refused.port-clearance", r.Diagnostic!.Id);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // GATE 5 — what the wider standard COST, on the committed fixture
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>R-pcal3-5 — a wider profile is a wider standard, and standards already dominate a
    /// de-embedded run.</b> The number is asserted rather than described so that a change to the
    /// profile's width cannot pass unnoticed: on this fixture the standards go from 4.57× the DUT's
    /// unknowns to 9.14×, and the run's own note is where a user reads it.
    /// </summary>
    [Trait("Category", "Benchmark")]
    [Fact]
    public void Gate5_TheWiderStandardsCostIsReported()
    {
        var (setup, source) = Fixture("coupled-pair-passive");
        var sw = Stopwatch.StartNew();
        var r  = EmRunService.Run(setup, source, _results);
        sw.Stop();

        Assert.Equal(EmRunStatus.Ok, r.Status);
        string cost = Assert.Single(r.Notes!, n => n.Contains("De-embedding costs", StringComparison.Ordinal));
        output.WriteLine(cost);
        output.WriteLine($"7-point sweep: {sw.Elapsed.TotalSeconds:F1} s");
        Assert.Contains("× the DUT's unknowns", cost, StringComparison.Ordinal);
    }

    private static (int Exit, string Stdout, string Stderr) RunCli(string repo, params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory       = repo,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("--project");
        psi.ArgumentList.Add(Path.Combine(repo, "src", "Cli"));
        psi.ArgumentList.Add("--no-build");
        psi.ArgumentList.Add("--");
        foreach (string a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        string so = p.StandardOutput.ReadToEnd();
        string se = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, so, se);
    }
}

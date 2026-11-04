using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using CircuitRF.Ui.Layout.Em;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Em3d;

// ══════════════════════════════════════════════════════════════════════════════════════════════
//  brief-emi-9 §5 — EMerge on a GPU: when a run uses it (a pure function), the CPU re-run of an Auto run whose GPU sweep did not
//  converge, the per-machine preference, and — with the GPU sparse solver installed only — the Cavity on the GPU against the
//  CPU. The recipe's GPU group and its bootstrap are SolverInstallTests gate 14.
// ══════════════════════════════════════════════════════════════════════════════════════════════

[Collection(SkiaFontsTypefaceCollection.Name)]
public sealed class EmergeGpuTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-emerge-gpu-" + Guid.NewGuid().ToString("N")[..12]);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    /// <summary>Test 2 — (problem, unknowns, installed, preference) → device.</summary>
    [Fact]
    public void TheDevice_IsAPureFunctionOfTheProblemItsSizeWhatIsInstalledAndThePreference()
    {
        const Em3dProblemType D = Em3dProblemType.Driven, E = Em3dProblemType.Eigenmode;
        long from = EmergeDevicePolicy.AutoGpuFromUnknowns;
        Assert.Equal(EmergeDevice.Gpu, EmergeDevicePolicy.Choose(D, from, true, EmergeDevice.Auto));
        Assert.Equal(EmergeDevice.Cpu, EmergeDevicePolicy.Choose(D, from - 1, true, EmergeDevice.Auto));
        Assert.Equal(EmergeDevice.Gpu, EmergeDevicePolicy.Choose(D, 1, true, EmergeDevice.Gpu));
        Assert.Equal(EmergeDevice.Cpu, EmergeDevicePolicy.Choose(D, 10 * from, true, EmergeDevice.Cpu));
        Assert.Equal(EmergeDevice.Cpu, EmergeDevicePolicy.Choose(D, 10 * from, false, EmergeDevice.Gpu));
        Assert.Equal(EmergeDevice.Cpu, EmergeDevicePolicy.Choose(E, 10 * from, true, EmergeDevice.Gpu));
        Assert.Null(EmergeDevicePolicy.GpuFromUnknowns(E, true, EmergeDevice.Auto));
    }

    /// <summary>Test 3 — a canned run.json on the GPU at its sample cap, not converged: an Auto run is solved exactly once more,
    /// on the CPU, and the Note names both sample counts; a forced GPU run, or a converged one, is not.</summary>
    [Fact]
    public void AnAutoGpuSweepThatDidNotConverge_IsSolvedOnceMoreOnTheCpu_AndTheNoteNamesBothCounts()
    {
        var gpu = Facts("gpu", """{"device": "gpu", "adaptive": true, "converged": false, "samples": 100, "error": 0.00881}""");
        var cpu = Facts("cpu", """{"device": "cpu", "adaptive": true, "converged": true, "samples": 10, "error": 2.7e-05}""");
        var asked = new List<long?>();
        (string, EmergeRunFacts?) Attempt(long? from) { asked.Add(from); return (from is null ? "cpu" : "gpu", from is null ? cpu : gpu); }

        var (kept, note) = Em3dRunService.WithCpuFallback(EmergeDevice.Auto, 40_000, Attempt);
        Assert.Equal([40_000, null], asked);
        Assert.Equal("cpu", kept);
        Assert.Contains("limit of 100 frequencies", note);
        Assert.Contains("converged with 10", note);

        asked.Clear();
        Assert.Equal(("gpu", (string?)null), Em3dRunService.WithCpuFallback(EmergeDevice.Gpu, 0, Attempt));
        Assert.Equal([0], asked);
        Assert.Null(Em3dRunService.WithCpuFallback(EmergeDevice.Auto, 40_000, _ => ("gpu", (EmergeRunFacts?)cpu)).Note);
    }

    /// <summary>
    /// brief-emw-3 — a GPU sweep the driver stopped once it stalled and solved again on the CPU in the same run: the driver's
    /// line turns the progress to the CPU, and run.json's account reads back, as a CPU run the whole-run fallback leaves alone.
    /// </summary>
    [Fact]
    public void AStalledGpuSweep_SwitchedToTheCpu_IsReadBack_AndNotSolvedAgain()
    {
        var control = new CircuitRF.Engine.RunControl();
        var tracker = new EmergeStageTracker(control, eigenmode: false);
        tracker.Line("""{"bestAt": 16, "bestError": 0.00305, "driver": 6, "samples": 24, "stage": "gpu-stalled"}""");
        Assert.Equal(EmergeStageTracker.CpuAgainLabel, control.Stage);

        var facts = Facts("stalled", """{"device": "cpu", "adaptive": true, "converged": true, "samples": 9, "error": 8.57e-05, "gpuStalled": {"samples": 24, "bestError": 0.00305, "bestAt": 16}}""");
        Assert.Equal(new EmergeGpuStall(24, 0.00305, 16), facts.GpuStalled);
        Assert.Null(Em3dRunService.WithCpuFallback(EmergeDevice.Auto, 40_000, _ => ("cpu", (EmergeRunFacts?)facts)).Note);
    }

    /// <summary>
    /// brief-dfr28-1 follow-up — a GPU sweep the driver stopped at EMerge's "broken on single precision" warning, not for a stall:
    /// the progress still turns to the CPU, and run.json's account keeps the reason.
    /// </summary>
    [Fact]
    public void AGpuSweepStoppedForAnUnusableFactorisation_IsReadBackWithItsReason()
    {
        var control = new CircuitRF.Engine.RunControl();
        var tracker = new EmergeStageTracker(control, eigenmode: false);
        tracker.Line("""{"bestAt": 0, "bestError": null, "driver": 7, "samples": 0, "stage": "gpu-stalled", "unusable": true}""");
        Assert.Equal(EmergeStageTracker.CpuAgainLabel, control.Stage);

        var facts = Facts("unusable", """{"device": "cpu", "adaptive": true, "converged": true, "samples": 8, "error": 8.5e-04, "gpuStalled": {"samples": 0, "bestError": null, "bestAt": 0, "unusable": true}}""");
        Assert.Equal(new EmergeGpuStall(0, null, 0, Unusable: true), facts.GpuStalled);
    }

    /// <summary>
    /// brief EMG-1 R-emg1-1 — a sweep of few frequencies, the point path, moved for either of the two reasons: the progress turns to
    /// the CPU, the note says the result is the CPU's and why, and the driver has no way left of publishing a flagged answer.
    /// </summary>
    [Fact]
    public void APointSweepMovedOffTheGpu_IsReadBackWithItsNote_AndTheDriverHasNoNoiseWarning()
    {
        var tracker = new EmergeStageTracker(new CircuitRF.Engine.RunControl(), eigenmode: false);
        Assert.False(tracker.LeftTheGpuUnusable);
        tracker.Line("""{"bestAt": 0, "bestError": null, "driver": 8, "samples": 0, "stage": "gpu-stalled", "unusable": true}""");
        Assert.True(tracker.LeftTheGpuUnusable);

        var flagged = Facts("flagged", """{"device": "cpu", "adaptive": false, "samples": 3, "gpuStalled": {"samples": 0, "bestError": null, "bestAt": 0, "unusable": true}}""");
        Assert.Contains("found its single-precision factorisation of this problem unusable", Em3dRunService.GpuStallNote(flagged.GpuStalled));
        var residual = Facts("residual", """{"device": "cpu", "adaptive": false, "samples": 1, "gpuStalled": {"samples": 0, "bestError": null, "bestAt": 0, "unusable": true, "residual": 0.0042}}""");
        Assert.Equal(0.0042, residual.GpuStalled!.Residual);
        Assert.Contains("missed its equations by 4.2e-3", Em3dRunService.GpuStallNote(residual.GpuStalled));
        Assert.Contains("This result is the CPU's", Em3dRunService.GpuStallNote(residual.GpuStalled));
        Assert.Null(Em3dRunService.GpuStallNote(null));
        Assert.DoesNotContain("may be noise", EmergeScriptWriter.Driver);
    }

    /// <summary>brief EMG-1 R-emg1-4 — the CPU cannot do it: a refusal, naming both facts, carrying no result.</summary>
    [Fact]
    public void WhenTheCpuCannotReplaceTheGpu_TheRunIsRefused_NamesBothFacts_AndHasNoResult()
    {
        var stop = new ProcessStop(ProcessStopReason.Memory, 9_000_000_000, 8_000_000_000, Meshing: false);
        var leg = Em3dRunService.StoppedLeg(EmergeSolver.Id, "EMerge", stop, new ProcessLimits(8_000_000_000, "this run's memory limit"), [], null,
                                            Em3dRunService.LeftTheGpuSentence);
        Assert.Equal(EmRunStatus.Refused, leg.Status);
        Assert.Contains("GPU solver could not solve this problem accurately", leg.Stop!.Render());
        Assert.Contains("the CPU could not finish it", leg.Stop.Render());
        Assert.Contains("Nothing was written", leg.Stop.Render());
        Assert.Null(leg.SnpPath);
        Assert.Null(leg.NpyPath);
        Assert.Null(leg.Data);
    }

    /// <summary>
    /// brief EMG-1 gate 2 — the reproduction, live: the metal-only 2×-thru at 1, 10 and 40 GHz on Auto flags at 1 GHz. The sweep moves,
    /// and an interrupted sweep leaves nothing behind: its S is a fresh CPU run's.
    /// </summary>
    [EmergeGpuFact, Trait("Category", "Benchmark")]
    public void TheThru_FlaggedAtOneGigahertz_MovesToTheCpu_AndEqualsACpuRun()
        => AssertMovedAndEqualsTheCpu("factorisation of this problem unusable", 1, 10, 40);

    /// <summary>
    /// brief EMG-1 — the thru at 9 and 40 GHz (the top frequency sizes the mesh): the solver flags neither and its S at 9 GHz is 2e-3
    /// from the CPU's. The check on the residual is what moves it (src/Design/RESOLVED.md, EMG-1).
    /// </summary>
    [EmergeGpuFact, Trait("Category", "Benchmark")]
    public void TheThru_UnflaggedAndWrongAtNineGigahertz_MovesToTheCpu_AndEqualsACpuRun()
        => AssertMovedAndEqualsTheCpu("missed its equations by", 9, 40);

    /// <summary>brief EMG-1 gate 4 — a mesh whose solves pass stays on the GPU, with no note: the Cavity at three frequencies.</summary>
    [EmergeGpuFact, Trait("Category", "Benchmark")]
    public void ACleanGpuPointSweep_StaysOnTheGpu_WithNoNote()
    {
        string ws = Path.Combine(_root, "ws");
        EmergeRunTests.CopyDirectory(Path.Combine(PalaceBackendTests.RepoRoot(), "examples", "3D Eigenmode"), ws);
        string path = Path.Combine(ws, "Cavity", "3d", "Cavity.c3d");
        var doc = C3dPersistence.LoadFromFile(path);
        var setup = C3dSetups.ForRun(C3dSetups.Read(doc).Single(s => s.Name == "Driven").Setup!, path);
        setup.Solver3D = EmergeSolver.Id;
        Frequencies(setup, 8, 10, 12);
        var r = EmRunService.RunThreeDView(setup, doc, path, Path.Combine(ws, ".cws"), Path.Combine(ws, "results"), emergeDevice: EmergeDevice.Gpu);
        Assert.True(r.Status == EmRunStatus.Ok, r.Error);
        Assert.Contains(r.Notes!, n => n.StartsWith("EMerge done in", StringComparison.Ordinal) && n.Contains("GPU sparse solver"));
        Assert.DoesNotContain(r.Notes!, n => n.Contains("This result is the CPU's"));
    }

    private static void Frequencies(EmSetup setup, params double[] ghz)
    {
        setup.Frequency = new FrequencySpec(ghz[0] + "e9", ghz[0] + "e9", 1);
        setup.AdditionalFrequencies = [.. ghz.Skip(1).Select(f => new FrequencySpec(f + "e9", f + "e9", 1))];
    }

    private void AssertMovedAndEqualsTheCpu(string why, params double[] ghz)
    {
        string ws = Path.Combine(_root, "ws");
        EmergeRunTests.CopyDirectory(Path.Combine(PalaceBackendTests.RepoRoot(), "testdata", "em-tfr", "zigzag"), ws);
        string cem = Path.Combine(ws, "thru", "em", "sheets.cem");
        EmRunResult Run(EmergeDevice device)
        {
            var setup = EmSetupPersistence.LoadFromFile(cem);
            setup.Solver3D = EmergeSolver.Id;
            Frequencies(setup, ghz);
            setup.Emerge = new CemEmerge { Quality = PalaceQuality.Standard, EdgeRefinement = 0.05 };
            var source = EmSetupResolver.Resolve(cem, setup.LayoutRef, Path.Combine(ws, ".cws"), new TechnologyCache()).Source;
            var r = EmRunService.Run(setup, source, Path.Combine(ws, "results-" + device.Id()), emergeDevice: device);
            foreach (string n in r.Notes ?? []) output.WriteLine($"{device.Id()} note: {n}");
            Assert.True(r.Status == EmRunStatus.Ok, r.Error);
            return r;
        }
        var auto = Run(EmergeDevice.Auto);
        var cpu = Run(EmergeDevice.Cpu);
        Assert.Contains(auto.Notes!, n => n.Contains(why) && n.Contains("This result is the CPU's"));
        Assert.DoesNotContain(auto.Notes!, n => n.StartsWith("EMerge done in", StringComparison.Ordinal) && n.Contains("GPU sparse solver"));
        Assert.DoesNotContain(auto.Warnings ?? [], w => w.Contains("may be noise"));
        Complex[] a = auto.Data!["S"].ComplexValues, c = cpu.Data!["S"].ComplexValues;
        double worst = a.Zip(c, (x, y) => (x - y).Magnitude).Max();
        output.WriteLine($"max |ΔS| {worst:E2}");
        Assert.True(worst <= 1e-9, $"max |ΔS| {worst:E2}");
    }

    /// <summary>Test 4 — the preference: absent is Automatic, and what is stored reads back.</summary>
    [Fact]
    public void ThePreference_AbsentIsAutomatic_AndRoundTrips()
    {
        Assert.Equal(EmergeDevice.Auto, EmEmergeDevicePreference.FromStored(null));
        Assert.Equal(EmergeDevice.Auto, EmEmergeDevicePreference.FromStored("tpu"));
        Assert.Null(EmEmergeDevicePreference.ToStored(EmergeDevice.Auto));
        foreach (var d in new[] { EmergeDevice.Cpu, EmergeDevice.Gpu })
            Assert.Equal(d, EmEmergeDevicePreference.FromStored(EmEmergeDevicePreference.ToStored(d)));
    }

    /// <summary>
    /// Test 5 — with the GPU sparse solver installed only: the 3D Eigenmode example's Cavity, driven, on the GPU matches the CPU
    /// within 1e-5 in S (measured: 3e-7 at 20k tets), and the run says it used the GPU.
    /// </summary>
    [EmergeGpuFact]
    public void TheCavity_OnTheGpu_MatchesTheCpu()
    {
        string ws = Path.Combine(_root, "ws");
        EmergeRunTests.CopyDirectory(Path.Combine(PalaceBackendTests.RepoRoot(), "examples", "3D Eigenmode"), ws);
        string path = Path.Combine(ws, "Cavity", "3d", "Cavity.c3d");
        var doc = C3dPersistence.LoadFromFile(path);
        var setup = C3dSetups.ForRun(C3dSetups.Read(doc).Single(s => s.Name == "Driven").Setup!, path);
        setup.Solver3D = EmergeSolver.Id;

        EmRunResult Run(EmergeDevice device)
        {
            var r = EmRunService.RunThreeDView(setup, doc, path, Path.Combine(ws, ".cws"), Path.Combine(ws, "results-" + device.Id()),
                                               emergeDevice: device);
            foreach (string n in r.Notes ?? []) output.WriteLine($"{device.Id()} note: {n}");
            Assert.True(r.Status == EmRunStatus.Ok, r.Error);
            return r;
        }
        var onGpu = Run(EmergeDevice.Gpu);
        var onCpu = Run(EmergeDevice.Cpu);
        Assert.Contains(onGpu.Notes!, n => n.StartsWith("EMerge done in", StringComparison.Ordinal) && n.Contains("GPU sparse solver"));
        Assert.DoesNotContain(onCpu.Notes!, n => n.Contains("GPU sparse solver"));
        Complex[] g = onGpu.Data!["S"].ComplexValues, c = onCpu.Data!["S"].ComplexValues;
        double worst = g.Zip(c, (a, b) => (a - b).Magnitude).Max();
        output.WriteLine($"max |ΔS| {worst:E2}");
        Assert.True(worst < 1e-5, $"max |ΔS| {worst:E2}");
    }

    private EmergeRunFacts Facts(string name, string json)
    {
        string dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, EmergeResults.RunFile), json);
        return EmergeResults.ReadFacts(dir);
    }
}

/// <summary>Skipped unless an EMerge is found whose probe reports the GPU sparse solver.</summary>
public sealed class EmergeGpuFactAttribute : FactAttribute
{
    public EmergeGpuFactAttribute()
    {
        if (SolverDiscovery.ReadinessFor(EmergeSolver.Id).FirstOrDefault(r => !r.Proceeds) is { } r)
            Skip = $"needs an EMerge: {r.Refusal}";
        else if (SolverDiscovery.Emerge.Find(out _)?.Emerge is not { Gpu: true })
            Skip = "needs EMerge's GPU sparse solver (circuitrf solver install emerge --gpu)";
    }
}

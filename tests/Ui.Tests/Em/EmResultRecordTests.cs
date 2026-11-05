// brief-cosim-pattern-1's gates: a run that does not finish changes no result, a finished one records how it was made, and the
// setup's Output name decides every file. The 3D gates run through EmRunService on a stand-in leg, so no solver is started.

using System.Numerics;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Em;
using CircuitRF.Ui.Tests.Em3d;
using RfCore;
using RfCore.Data;
using RfCore.Export;

namespace CircuitRF.Ui.Tests.Em;

public sealed class EmResultRecordTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-cemr-" + Guid.NewGuid().ToString("N")[..10]);
    private string Results => Path.Combine(_root, "results");

    public EmResultRecordTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        Em3dRunService.LegStandIn = null;
        EmResultStaging.BeforeRename = null;
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────────────

    private EmRunResult RunPlanar(EmSetup? setup = null, CancellationToken ct = default, EmRunTarget? target = null)
        => EmRunService.Run(setup ?? PlanarRunTests.NewSetup(), PlanarRunTests.Source(PlanarRunTests.NewLayout()), Results, ct, target: target);

    public enum Ending { Finishes, Throws, Cancels, FieldSetFails, TouchstoneFails }

    /// <summary>A solver that ends as asked, through the same publisher a real leg's result goes through.</summary>
    private sealed class StandInLeg(Em3dSolverDescriptor d, Func<Ending> ending) : Em3dRunService.SolverLeg(d)
    {
        public override void Configure(Em3dRunService.RunContext run) { }
        public override void Prepare(Em3dProblem problem, Em3dRunService.RunContext run) { }

        public override Em3dRunService.Leg Execute(Em3dProblem problem, string snpBase, Em3dRunService.RunContext run)
        {
            var end = ending();
            if (end == Ending.Throws) throw new InvalidOperationException("the solver fell over");
            if (end == Ending.Cancels) throw new OperationCanceledException();
            using var staging = EmResultStaging.Begin(run.Paths(Solver.Id));
            if (end == Ending.FieldSetFails)
            {
                // What a failed PortFieldSetWriter.Write leaves: part of a set, in the folder the leg handed it.
                Directory.CreateDirectory(staging.StagedFields);
                File.WriteAllText(Path.Combine(staging.StagedFields, "p1.f1.E.npy"), "part of a field");
                return Em3dRunService.Leg.Failed(Solver.Id, EmRunStatus.EngineError, EmDiagnostics.SolveFailed("the disk filled"));
            }
            var data = Data();
            var published = Em3dRunService.PublishDriven(run, Solver.Id, problem, data, "1.0", "cpu", new EmResultMesh(100, "elements", 400),
                (stagedBase, stamp) => end == Ending.TouchstoneFails ? "the disk filled" : Touchstone(data, stagedBase, stamp), staging);
            return new Em3dRunService.Leg(Solver.Id, EmRunStatus.Ok, null, data, published.Npy, published.Snp, Solver.DisplayName, RecordPath: published.Record);
        }

        private static DataSet Data()
        {
            var ds = new DataSet();
            var s = new Complex[4];
            s[0] = s[3] = new Complex(0.1, 0);
            s[1] = s[2] = new Complex(0.9, 0);
            ds.AddToGroup("palace", "S", new DataCube(
                [new Axis("freq", [1e9], "Hz"), new Axis("i", [1, 2], "port"), new Axis("j", [1, 2], "port")], s));
            return ds;
        }

        private static string? Touchstone(DataSet data, string stagedBase, string stamp)
        {
            var opts = new TouchstoneExportOptions(50, 10, 'g', MatrixFormat.RI,
                HeaderComments: EmSnpProvenance.BuildHeader3D("Palace", "g", "m", "p", "Palace 1.0", [], "ms", "ms.clay", DateTimeOffset.Now, stamp));
            var r = TouchstoneExporter.Export(data, "palace", opts, new Dictionary<string, int>(), false, stagedBase);
            return r.Status == TouchstoneExportStatus.Ok ? null : r.Status.ToString();
        }
    }

    private Ending _ending = Ending.Finishes;

    private EmRunResult Run3D(EmSetup setup, EmLayoutSource source, EmRunTarget? target = null)
    {
        Em3dRunService.LegStandIn = (root, d) => root.StartsWith(_root, StringComparison.Ordinal) ? new StandInLeg(d, () => _ending) : null;
        return EmRunService.Run(setup, source, Results, target: target);
    }

    private static (EmSetup, EmLayoutSource) Microstrip()
    {
        var (setup, source) = Em3dGeneratorTests.Microstrip();
        setup.Name = "ms";
        setup.Solver3D = Em3dSolverId.Palace;
        return (setup, source);
    }

    /// <summary>Every file under the results folder, as path → bytes. Not the solver's run directory: that is a working folder, whose
    /// status file records this very run (R-cp1-4).</summary>
    private Dictionary<string, byte[]> Snapshot()
        => Directory.EnumerateFiles(Results, "*", SearchOption.AllDirectories)
                    .Where(f => !Path.GetRelativePath(Results, f).StartsWith("ms.palace" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    .ToDictionary(f => Path.GetRelativePath(Results, f), File.ReadAllBytes);

    private void AssertUnchanged(Dictionary<string, byte[]> before)
    {
        var now = Snapshot();
        Assert.Equal(before.Keys.Order(), now.Keys.Order());
        foreach (var (name, bytes) in before) Assert.True(bytes.SequenceEqual(now[name]), $"{name} changed");
    }

    /// <summary>A whole previous 3D result, with a field set the run will be asked to replace.</summary>
    private (EmSetup, EmLayoutSource, Dictionary<string, byte[]>) PreviousResult()
    {
        var (setup, source) = Microstrip();
        var first = Run3D(setup, source);
        Assert.True(first.Status == EmRunStatus.Ok, first.Error);
        Assert.Empty(first.Errors ?? []);
        string fields = Path.Combine(Results, "ms.palace.fields");
        Directory.CreateDirectory(fields);
        File.WriteAllText(Path.Combine(fields, "fields.json"), "the previous run's set");
        return (setup, source, Snapshot());
    }

    // ── a run that does not finish ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(Ending.Throws)]
    [InlineData(Ending.Cancels)]
    [InlineData(Ending.FieldSetFails)]
    [InlineData(Ending.TouchstoneFails)]
    public void ARunThatDoesNotFinish_LeavesThePreviousResultByteIdentical(Ending ending)
    {
        var (setup, source, before) = PreviousResult();
        _ending = ending;
        try { Run3D(setup, source); }
        catch (InvalidOperationException) { }     // a solver's throw is the caller's to report, as it always was
        AssertUnchanged(before);
    }

    [Fact]
    public void ACancelledPlanarRun_LeavesThePreviousResultByteIdentical()
    {
        Assert.Equal(EmRunStatus.Ok, RunPlanar().Status);
        var before = Snapshot();
        Assert.Equal(EmRunStatus.Cancelled, RunPlanar(ct: new CancellationToken(canceled: true)).Status);
        AssertUnchanged(before);
    }

    [Fact]
    public void ARenameThatFails_PutsBackWhatTheEarlierRenamesReplaced()
    {
        var (setup, source, before) = PreviousResult();
        EmResultStaging.BeforeRename = target =>
        {
            if (target.EndsWith(".s2p", StringComparison.Ordinal) && target.StartsWith(_root, StringComparison.Ordinal))
                throw new IOException("the file is in use");
        };
        var r = Run3D(setup, source);
        Assert.Null(r.SnpPath);
        Assert.Contains(r.Errors!, e => e.Contains("the file is in use", StringComparison.Ordinal));
        AssertUnchanged(before);
    }

    [Fact]
    public void WhatAKilledRunLeft_IsRemovedByTheNextRunOfThatSetup()
    {
        var (setup, source, _) = PreviousResult();
        string leftover = Path.Combine(Results, ".staging-ms.palace", "new", "ms.palace_em.npy");
        Directory.CreateDirectory(Path.GetDirectoryName(leftover)!);
        File.WriteAllText(leftover, "killed");

        _ending = Ending.Throws;     // removed before the solve, so even a run that fails clears it
        try { Run3D(setup, source); } catch (InvalidOperationException) { }

        Assert.False(Directory.Exists(Path.Combine(Results, ".staging-ms.palace")));
    }

    // ── a finished run's record ────────────────────────────────────────────────────────────────

    [Fact]
    public void AFinishedRun_RecordsFilesWhoseStampsMatch_AndASetupEditLeavesThemAlone()
    {
        var setup = PlanarRunTests.NewSetup();
        var r = RunPlanar(setup);
        Assert.True(r.Status == EmRunStatus.Ok, r.Error);

        string recordPath = Path.Combine(Results, "planar.cemr");
        Assert.Equal(recordPath, r.RecordPath);
        var (record, refusal) = EmResultRecord.Load(recordPath);
        Assert.True(record is not null, refusal);
        Assert.Empty(record!.Verify(recordPath));
        Assert.Equal(record.Stamp, EmResultRecord.ReadSnpStamp(r.SnpPath!));
        Assert.Equal(record.Stamp, EmResultRecord.ReadNpyStamp(r.NpyPath!));
        Assert.Equal(["P1", "P2"], record.Ports.Select(p => p.Name));
        Assert.Equal(EmResultPort.Plane, record.Ports[0].Kind);
        Assert.Equal([5e9], record.Frequencies.SweepHz);
        Assert.NotNull(record.Geometry!.GeometryHash);
        Assert.True(record.Geometry.Unknowns > 0);
        Assert.Equal("planar", EmSetupPersistence.FromEmbedded(record.Setup!.Value).Name);

        var before = Snapshot();
        setup.Name = "edited";
        AssertUnchanged(before);
    }

    [Fact]
    public void AMovedFolder_StillResolves()
    {
        RunPlanar();
        string moved = Path.Combine(_root, "elsewhere");
        Directory.Move(Results, moved);
        string recordPath = Path.Combine(moved, "planar.cemr");
        var (record, _) = EmResultRecord.Load(recordPath);
        Assert.Empty(record!.Verify(recordPath));
        Assert.Equal(Path.Combine(moved, "planar.s2p"), EmResultRecord.Resolve(recordPath, record.Files.Snp));
    }

    [Fact]
    public void AFileFromAnotherRun_IsNamedByVerify()
    {
        var first = RunPlanar();
        string saved = Path.Combine(_root, "saved.npy");
        File.Copy(first.NpyPath!, saved);
        RunPlanar();
        File.Copy(saved, first.NpyPath!, overwrite: true);

        var (record, _) = EmResultRecord.Load(first.RecordPath!);
        var bad = Assert.Single(record!.Verify(first.RecordPath!));
        Assert.Equal(".npy", bad.What);
    }

    [Fact]
    public void A3DRun_RecordsTheSolverAndTheMeshItReported_AndARealRunDirectoryIsNotAResultFile()
    {
        var (setup, source) = Microstrip();
        var r = Run3D(setup, source);
        var (record, _) = EmResultRecord.Load(r.RecordPath!);
        Assert.Equal("Palace", record!.Solver.Name);
        Assert.Equal("cpu", record.Solver.Device);
        Assert.Equal(100, record.Geometry!.MeshSize);
        Assert.Equal(400, record.Geometry.Unknowns);
        Assert.Empty(record.Verify(r.RecordPath!));
    }

    // ── one name for the whole result ──────────────────────────────────────────────────────────

    private void AssertTwoNames(Func<string, EmRunResult> run, string first, string second)
    {
        var a = run(first);
        var b = run(second);
        Assert.NotEqual(a.RecordPath, b.RecordPath);
        foreach (var r in new[] { a, b })
        {
            var (record, _) = EmResultRecord.Load(r.RecordPath!);
            Assert.Empty(record!.Verify(r.RecordPath!));
        }
        Assert.Equal(Path.Combine(Results, first + EmResultNames.RecordExtension), a.RecordPath);
        Assert.Equal(Path.Combine(Results, first + "_em.npy"), a.NpyPath);
        Assert.Equal(Path.Combine(Results, first + ".s2p"), a.SnpPath);

        byte[] kept = File.ReadAllBytes(b.RecordPath!);
        byte[] was = File.ReadAllBytes(a.RecordPath!);
        var third = run(first);
        Assert.Equal(kept, File.ReadAllBytes(b.RecordPath!));
        Assert.NotEqual(was, File.ReadAllBytes(third.RecordPath!));
    }

    [Fact]
    public void TwoNames_LeaveTwoWholeResults_OnAPlanarSetup()
        => AssertTwoNames(name =>
        {
            var setup = PlanarRunTests.NewSetup();
            setup.SnpOutputPathOverride = name;
            return RunPlanar(setup);
        }, "first", "second");

    [Fact]
    public void TwoNames_LeaveTwoWholeResults_OnA3DSetup()
        => AssertTwoNames(name =>
        {
            var (setup, source) = Microstrip();
            setup.SnpOutputPathOverride = name;
            return Run3D(setup, source);
        }, "first", "second");

    [Fact]
    public void ATypedSuffix_IsRemoved()
    {
        Assert.Equal("x", EmResultNames.Clean("x.s2p"));
        Assert.Equal("x", EmResultNames.Clean("x.sNp"));
        Assert.Equal("x", EmResultNames.Clean("x.cemr"));
        Assert.Equal("x", EmResultNames.Clean("x_em.npy"));
        Assert.Equal("x", EmResultNames.Clean("x.npy"));
        Assert.Equal("v1.step", EmResultNames.Clean("v1.step"));
    }

    [Fact]
    public void TheCompanions_AreNamedAfterTheOutputName()
    {
        var setup = PlanarRunTests.NewSetup();
        Assert.Equal("planar_sim", EmSnpSchematic.CellNameFor(setup));
        setup.SnpOutputPathOverride = "sub/first.s2p";
        Assert.Equal("first_sim", EmSnpSchematic.CellNameFor(setup));
        Assert.Equal("first_cosim.cnl", EmCoSimNetlist.FileNameFor(setup));
    }

    [Fact]
    public void ATouchstoneDestination_MovesTheTouchstoneOnly()
    {
        string chosen = Path.Combine(_root, "elsewhere", "line.s2p");
        Directory.CreateDirectory(Path.GetDirectoryName(chosen)!);
        var setup = PlanarRunTests.NewSetup();
        var r = RunPlanar(setup, target: new EmRunTarget { TouchstoneDestination = chosen });

        Assert.Equal(chosen, r.SnpPath);
        Assert.Equal("", setup.SnpOutputPathOverride);
        Assert.Equal(Path.Combine(Results, "planar_em.npy"), r.NpyPath);
        Assert.Equal(Path.Combine(Results, "planar.cemr"), r.RecordPath);
        var (record, _) = EmResultRecord.Load(r.RecordPath!);
        Assert.Empty(record!.Verify(r.RecordPath!));
        Assert.Equal("../elsewhere/line.s2p", record.Files.Snp);
        Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(chosen)!, ".staging-line")));
    }

    // ── the box says so ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ThePlaceholder_IsTheStemABlankBoxsRunWrites()
    {
        var planar = PlanarRunTests.NewSetup();
        var planarRun = RunPlanar(planar);
        Assert.Equal(Path.GetFileNameWithoutExtension(planarRun.SnpPath),
                     new EmSetupEditorViewModel(Path.Combine(_root, "p.cem"), planar).SnpOutputPlaceholder);

        var (threeD, source) = Microstrip();
        var run3D = Run3D(threeD, source);
        var vm = new EmSetupEditorViewModel(Path.Combine(_root, "t.cem"), threeD);
        Assert.Equal("ms.palace", vm.SnpOutputPlaceholder);
        Assert.Equal(Path.GetFileNameWithoutExtension(run3D.SnpPath), vm.SnpOutputPlaceholder);
    }

    // ── reading it back, and an older result ───────────────────────────────────────────────────

    /// <summary>A workspace with a .cem beside it, run by the CLI, whose setup is edited after.</summary>
    private string RunWorkspace()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Line", "layout"));
        TechPersistence.SaveToFile(Path.Combine(_root, "pcb.ctech"), StarterTechnologies.Pcb2Layer());
        LayoutPersistence.SaveToFile(Path.Combine(_root, "Line", "layout", "Line.clay"), PlanarRunTests.NewLayout());
        WorkspacePersistence.SaveToFile(Path.Combine(_root, ".cws"), new CwsFile { DefaultTechRef = "pcb.ctech" });
        var setup = PlanarRunTests.NewSetup();
        setup.LayoutRef = Path.Combine("Line", "layout", "Line.clay");
        string cem = Path.Combine(_root, "line.cem");
        EmSetupPersistence.SaveToFile(cem, setup);
        var (code, _, stderr) = CliProcess.Run(PalaceBackendTests.RepoRoot(), [], "em", cem);
        Assert.True(code == 0, stderr);
        return cem;
    }

    [Fact]
    public void Explain_AfterTheSetupIsEdited_PrintsTheOldValueAndTheNew()
    {
        string cem = RunWorkspace();
        var setup = EmSetupPersistence.LoadFromFile(cem);
        setup.PlanarMesh = setup.PlanarMesh with { CellsPerWavelength = 9 };
        EmSetupPersistence.SaveToFile(cem, setup);

        string record = Path.Combine(Results, "planar.cemr");
        var (code, stdout, stderr) = CliProcess.Run(PalaceBackendTests.RepoRoot(), [], "explain", record);
        Assert.True(code == 0, stderr);          // a difference is information
        Assert.Contains("6 -> 9", stdout);
        Assert.Contains("CellsPerWavelength", stdout);

        var (jsonCode, json, _) = CliProcess.Run(PalaceBackendTests.RepoRoot(), [], "explain", record, "--json");
        Assert.Equal(0, jsonCode);
        Assert.Contains("6 -> 9", json.Replace("\\u003E", ">"));

        var (checkCode, _, checkErr) = CliProcess.Run(PalaceBackendTests.RepoRoot(), [], "check", record);
        Assert.True(checkCode == 0, checkErr);
    }

    [Fact]
    public void Adopt_WritesARecord_ReadBackAsNotRecorded_AndTouchesNoDataFile()
    {
        string cem = RunWorkspace();
        string record = Path.Combine(Results, "planar.cemr");
        File.Delete(record);
        var before = Snapshot();
        string snp = Path.Combine(Results, "planar.s2p");

        var (code, stdout, stderr) = CliProcess.Run(PalaceBackendTests.RepoRoot(), [], "em", "--adopt", snp);
        Assert.True(code == 0, stderr);
        Assert.Contains("planar.cemr", stdout);

        var (read, _) = EmResultRecord.Load(record);
        Assert.Null(read!.Setup);
        Assert.True(read.Adopted);
        Assert.Empty(read.Verify(record));
        Assert.Equal(["P1", "P2"], read.Ports.Select(p => p.Name));
        Assert.Equal(EmResultPort.Plane, read.Ports[0].Kind);
        foreach (var (name, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(Results, name)));

        var (_, explained, _) = CliProcess.Run(PalaceBackendTests.RepoRoot(), [], "explain", record);
        Assert.Contains("settings not recorded", explained);
        Assert.Contains("already has a result record", EmResultRecord.Adopt(snp).Refusal);
        Assert.False(string.IsNullOrEmpty(cem));
    }

    [Fact]
    public void Adopt_RefusesATouchstoneWithNoMatchingNpy()
    {
        var r = RunPlanar();
        File.Delete(r.RecordPath!);
        File.Delete(r.NpyPath!);
        var (path, refusal) = EmResultRecord.Adopt(r.SnpPath!);
        Assert.Null(path);
        Assert.Contains("No .npy matches", refusal);
        Assert.False(File.Exists(r.RecordPath!));
    }

    [Fact]
    public void TheRecord_IsNamedByCheckAndHasAReferenceTopic()
    {
        var (code, stdout, stderr) = CliProcess.Run(PalaceBackendTests.RepoRoot(), [], "reference", "em-result");
        Assert.True(code == 0, stderr);
        Assert.Contains("Stamp", stdout);
        Assert.Contains("SweepHz", stdout);
    }
}

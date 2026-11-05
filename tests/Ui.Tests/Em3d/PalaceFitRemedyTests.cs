using CircuitRF.Design.Em3d;
using CircuitRF.Engine.Em3d;
using Xunit;

namespace CircuitRF.Ui.Tests.Em3d;

// brief-dfr27-5 R-dfr27-5-5 — when Palace will not fit, the question offers what would, each with its estimate; when nothing
// does, it says so. Arithmetic only: the process scaling was measured once, on the round-26 board (Em3dSizeEstimate).

public sealed class PalaceFitRemedyTests
{
    private const double GB = 1e9;

    /// <summary>The process scaling lies on or above every one-excitation peak measured (1, 2, 4 and 8 processes: 2.5, 3.0, 3.5
    /// and 4.3 GB) and is exact at the two it was drawn through.</summary>
    [Fact]
    public void TheProcessFactor_LiesOnOrAboveEveryMeasuredPeak()
    {
        foreach (var (n, gb) in new[] { (1, 2.5), (2, 3.0), (4, 3.5), (8, 4.3) })
            Assert.True(Em3dSizeEstimate.PalaceProcessFactor(n) * 4.3 >= gb - 1e-9, $"{n} processes");
        Assert.Equal(1.0, Em3dSizeEstimate.PalaceProcessFactor(8), 12);
        Assert.Equal(2.5 / 4.3, Em3dSizeEstimate.PalaceProcessFactor(1), 12);
    }

    /// <summary>
    /// The designer's run: the board's 148,988 tetrahedra at Draft on eight processes, in a 3.8 GB Linux subsystem. One
    /// process is offered with its estimate and does not fit; the crop is sized; the question says no setting fits and only
    /// a smaller crop would, and its default is not to run.
    /// </summary>
    [Fact]
    public void TheDesignersRun_NoSettingFits_AndTheQuestionSaysSo()
    {
        var draft = PalaceSettings.Preset(PalaceQuality.Draft);
        var v = Em3dRunService.MeshMemoryVerdict(148_988, draft, (long)(3.8 * GB), processes: 8);
        Assert.Equal(Em3dMemoryLevel.Severe, v.Level);
        var fewer = Assert.Single(v.Remedies, r => r.Processes is not null);
        Assert.Equal(1, fewer.Processes);
        Assert.False(fewer.Fits(v.PhysicalBytes));
        Assert.Single(v.Remedies, r => r.Sized);
        Assert.True(v.NothingFits);
        Assert.Empty(v.Applicable);
        Assert.Contains("No setting fits it", v.Text);
        Assert.Contains("only a smaller crop would", v.Text);
    }

    /// <summary>
    /// On a machine where fewer processes do fit, the most that fit are offered as something the run takes itself; chosen,
    /// the run's gate holds that count and the run keeps its memory ceiling (it was not forced).
    /// </summary>
    [Fact]
    public void FewerProcessesThatFit_AreOffered_AndChosenTheRunTakesThem()
    {
        var draft = PalaceSettings.Preset(PalaceQuality.Draft);
        long e8 = Em3dSizeEstimate.PalaceMemoryBytesForMesh(148_988, 1, 0);
        long physical = (long)(e8 * 0.65);    // eight processes are past 150 % of it; one fits
        var v = Em3dRunService.MeshMemoryVerdict(148_988, draft, physical, processes: 8);
        var offered = Assert.Single(v.Applicable);
        Assert.True(offered.Processes < 8);
        Assert.True(offered.EstimateBytes <= physical);
        Assert.True(offered.Processes == 7 ||
                    e8 * Em3dSizeEstimate.PalaceProcessFactor(offered.Processes!.Value + 1) > physical);   // the most that fit
        Assert.False(v.NothingFits);

        var gate = new Em3dRunService.MemoryGate(q => { q.Choice.Remedy = q.Applicable[0]; return true; });
        var log = new Em3dRunService.RunLog();
        Assert.Null(gate.Admit(v, log));
        Assert.Equal(offered.Processes, gate.ProcessCap);
        Assert.False(gate.Forced);
        Assert.Contains("as chosen", Assert.Single(log.Warnings));
    }

    /// <summary>
    /// The via transition's Standard run on the direct solver: 29,042 tetrahedra reached past 8 GB on a machine that size, and
    /// the iterative solver solved it in about 3.7. Below 150 % the question is asked anyway, offering the iterative solver
    /// first; chosen, the run takes it and keeps its ceiling. With no one to ask (the CLI) it is a warning naming the remedy,
    /// and the run goes on as the setup says.
    /// </summary>
    [Fact]
    public void DirectThatWillNotFit_OffersTheIterativeSolver_AskedFirst_AndTheCliOnlyWarns()
    {
        var direct = PalaceSettings.Preset(PalaceQuality.Standard) with { LinearSolver = PalaceLinearSolver.Direct };
        long physical = (long)(8 * GB);
        var v = Em3dRunService.MeshMemoryVerdict(29_042, direct, physical);
        Assert.True(v.EstimateBytes > physical);
        Assert.NotEqual(Em3dMemoryLevel.Severe, v.Level);
        Assert.True(v.PastMemory);
        Assert.True(v.Applicable[0].Iterative);
        Assert.True(v.Applicable[0].EstimateBytes < physical);
        Assert.Contains("Palace.LinearSolver: Iterative", v.Text);

        var gate = new Em3dRunService.MemoryGate(q => { q.Choice.Remedy = q.Applicable[0]; return true; });
        var log = new Em3dRunService.RunLog();
        Assert.Null(gate.Admit(v, log));
        Assert.True(gate.UseIterative);
        Assert.False(gate.Forced);
        Assert.Contains("iterative linear solver, as chosen", Assert.Single(log.Warnings));

        var cli = new Em3dRunService.MemoryGate(null);
        var cliLog = new Em3dRunService.RunLog();
        Assert.Null(cli.Admit(v, cliLog));
        Assert.False(cli.UseIterative);
        Assert.Contains("Palace.LinearSolver: Iterative", Assert.Single(cliLog.Warnings));

        // The same mesh on the iterative solver fits: nothing is asked.
        Assert.False(Em3dRunService.MeshMemoryVerdict(29_042, PalaceSettings.Preset(PalaceQuality.Standard), physical).PastMemory);
    }

    private static Em3dMemoryVerdict OfShare(double share, params Em3dMemoryRemedy[] remedies)
        => Em3dMemoryVerdict.Evaluate((long)(share * 16 * GB), (long)(16 * GB), remedies);

    /// <summary>A run estimated past this machine's memory but below 150 % is asked about. Anyway lifts its ceiling, a remedy keeps it, and no
    /// does not run it. It was started with its ceiling and stopped at it, with no way to choose to let it swap.</summary>
    [Fact]
    public void PastMemoryBelow150Percent_IsAskedAbout_AndOnlyAnywayLiftsTheCeiling()
    {
        var two = new Em3dMemoryRemedy("2 MPI processes", (long)(10 * GB)) { Processes = 2 };
        var v = OfShare(1.4, two);
        Assert.Equal(Em3dMemoryLevel.Warning, v.Level);
        Assert.Contains("141 % of this machine's", OfShare(1.41).Text);
        Assert.Contains(", so it will probably be stopped before it finishes.", v.Lead);
        Assert.Equal(["2 MPI processes — about 10.0 GB, which fits"], v.Options);
        Assert.DoesNotContain("processes", v.Lead);

        var anyway = new Em3dRunService.MemoryGate(_ => true);
        Assert.Null(anyway.Admit(v, new Em3dRunService.RunLog()));
        Assert.True(anyway.Forced);

        var remedy = new Em3dRunService.MemoryGate(q => { q.Choice.Remedy = q.Applicable[0]; return true; });
        Assert.Null(remedy.Admit(OfShare(1.4, two), new Em3dRunService.RunLog()));
        Assert.Equal((false, (int?)2), (remedy.Forced, remedy.ProcessCap));

        Assert.NotNull(new Em3dRunService.MemoryGate(_ => false).Admit(v, new Em3dRunService.RunLog()));

        // With no one to ask it is a warning, and the ceiling stays.
        var headless = new Em3dRunService.MemoryGate(null);
        Assert.Null(headless.Admit(v, new Em3dRunService.RunLog()));
        Assert.False(headless.Forced);
    }

    /// <summary>A run within this machine's memory is not asked about.</summary>
    [Fact]
    public void WithinMemory_IsNotAskedAbout()
    {
        int asked = 0;
        var gate = new Em3dRunService.MemoryGate(_ => { asked++; return true; });
        Assert.Null(gate.Admit(OfShare(0.95), new Em3dRunService.RunLog()));
        Assert.Equal((0, false), (asked, gate.Forced));
    }

    /// <summary>The question says what starting anyway will ask of the disk, and how much more it needs when that is too much.</summary>
    [Fact]
    public void SwapRoom_SaysWhatTheDiskHasAndWhatIsShort()
    {
        long gb = 1_000_000_000;
        var space = new VolumeSpace(20 * gb, 500 * gb);
        Assert.Contains("lets the run swap about 8.0 GB. The startup disk has 20.0 GB free, and the run stops if that falls to 10.0 GB.",
                        DiskFloor.SwapRoom(25 * gb, 17 * gb, space));
        Assert.DoesNotContain("Free about", DiskFloor.SwapRoom(25 * gb, 17 * gb, space));
        Assert.Contains("Free about 5.0 GB more first.", DiskFloor.SwapRoom(32 * gb, 17 * gb, space));
        Assert.Null(DiskFloor.SwapRoom(16 * gb, 17 * gb, space));
        Assert.Null(DiskFloor.SwapRoom(25 * gb, 17 * gb, null));
    }
}

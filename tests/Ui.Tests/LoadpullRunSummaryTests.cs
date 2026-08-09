using System.IO;
using System.Linq;
using CircuitRF.Ui.Schematic;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Owner request: after a Loadpull or Loadpull-Pursuit run, the Messages window should say how many
/// points were simulated, how many reached compression, and how many failed to converge — the
/// compression count being the one that tells the user what Pin drive level to set next time.
///
/// The engine already publishes everything needed (a per-grid-point <c>StopCode</c> cube), so this
/// is a reporting gap, not a measurement one, and nothing in <c>src/Engine</c> changed.
/// </summary>
public sealed class LoadpullRunSummaryTests
{
    // StopCode, as LoadpullEngine.BuildLoadpullDataSet writes it.
    private const double PinMax = 0, Compression = 1, NonConvergence = 2, NoConvergedSeed = 3;

    [Fact]
    public void AMixedGrid_ReportsEveryCategory()
    {
        string s = Describe(Compression, Compression, Compression, PinMax, NonConvergence, NoConvergedSeed);

        Assert.Contains("6 point(s) simulated", s);
        Assert.Contains("3 reached compression", s);
        Assert.Contains("1 stopped at max drive", s);
        Assert.Contains("2 did not converge", s);   // NonConvergence and NoConvergedSeed are both "did not converge"
    }

    [Fact]
    public void ACleanGrid_MentionsNeitherMaxDriveNorNonConvergence()
    {
        // A sweep where everything behaved must read as one, not as a list of zeros to re-check.
        string s = Describe(Compression, Compression);

        Assert.Contains("2 point(s) simulated", s);
        Assert.Contains("2 reached compression", s);
        Assert.DoesNotContain("max drive", s);
        Assert.DoesNotContain("did not converge", s);
    }

    [Fact]
    public void NothingCompressing_SaysToRaiseTheDrive()
    {
        // The actionable case, and the reason the compression count was asked for.
        string s = Describe(PinMax, PinMax, PinMax);

        Assert.Contains("0 reached compression", s);
        Assert.Contains("raise the Pin drive level", s);
    }

    [Fact]
    public void EverythingCompressing_SaysToLowerTheStartingDrive()
    {
        string s = Describe(Compression, Compression, Compression);
        Assert.Contains("lower the starting Pin", s);
    }

    [Fact]
    public void ASingleCompressedPoint_GetsNoAdvice()
    {
        // A one-point grid that compressed says nothing about the drive-up range either way, so
        // "every point compressed" would be a claim about a sample of one.
        string s = Describe(Compression);
        Assert.DoesNotContain("lower the starting Pin", s);
        Assert.DoesNotContain("raise the Pin drive level", s);
    }

    [Fact]
    public void ANonLoadpullDataSet_ProducesNothing()
    {
        // Every other analysis type reaches this and must gain no message at all.
        var ds = new DataSet();
        ds.Add("S", new DataCube([new Axis("freq", [1e9, 2e9], "Hz")], new[] { 1.0, 2.0 }));

        Assert.Null(LoadpullRunSummary.Describe(ds));
        Assert.Null(LoadpullRunSummary.Describe(null));
        Assert.Null(LoadpullRunSummary.Describe(new DataSet()));
    }

    [Fact]
    public void APursuitResultCarryingTheFollowOnLoadpull_IsSummarisedTheSameWay()
    {
        // A Loadpull-Pursuit embeds the follow-on loadpull's cubes under their ORIGINAL names, so
        // it must reach exactly the same path rather than needing a second summariser.
        var ds = new DataSet();
        ds.Add("MXP_PoutDbm", DataCube.Scalar(31.4));
        ds.Add("MXE_Eff",     DataCube.Scalar(0.62));
        AddStopCode(ds, Compression, Compression, PinMax);

        string s = Assert.IsType<string>(LoadpullRunSummary.Describe(ds));
        Assert.Contains("3 point(s) simulated", s);
        Assert.Contains("2 reached compression", s);
    }

    /// <summary>
    /// THE reported bug. This test previously asserted the opposite — "no grid was swept, so there is
    /// nothing to count: a null, never a row of zeros" — and that reasoning is wrong, because the
    /// commonest way to reach it is the one case the user most needs explained: nothing reached
    /// compression, so nothing could be scored, so no optimum converged, so no follow-on loadpull ran.
    /// The run then finished in complete silence. Updated deliberately, not loosened.
    /// </summary>
    [Fact]
    public void APursuitWhereNothingCompressed_SaysSo_RatherThanFinishingInSilence()
    {
        var ds = new DataSet();
        ds.Add("MXP_Converged",   DataCube.Scalar(0.0));
        ds.Add("MXE_Converged",   DataCube.Scalar(0.0));
        ds.Add("CacheCount",      DataCube.Scalar(50.0));
        ds.Add("UnscorableCount", DataCube.Scalar(50.0));
        ds.Add("RecommTermCount", DataCube.Scalar(0.0));

        var s = LoadpullRunSummary.Describe(ds);

        Assert.NotNull(s);
        Assert.Contains("50 termination(s) queried", s);
        Assert.Contains("50 could not be scored", s);
        Assert.Contains("neither MXP nor MXE converged", s);
        Assert.Contains("raise the Pin drive level", s);
    }

    /// <summary>A pursuit whose optima converged but whose follow-on was switched off is a completely
    /// different situation from one that failed to score, and must not read like a failure.</summary>
    [Fact]
    public void APursuitWithTheFollowOnSwitchedOff_SaysThat_NotThatSomethingFailed()
    {
        var ds = new DataSet();
        ds.Add("MXP_Converged",   DataCube.Scalar(1.0));
        ds.Add("MXE_Converged",   DataCube.Scalar(1.0));
        ds.Add("CacheCount",      DataCube.Scalar(42.0));
        ds.Add("UnscorableCount", DataCube.Scalar(0.0));
        ds.Add("RecommTermCount", DataCube.Scalar(17.0));

        var s = LoadpullRunSummary.Describe(ds);

        Assert.NotNull(s);
        Assert.Contains("MXP and MXE both converged", s);
        Assert.Contains("17 recommended termination(s)", s);
        Assert.Contains("CreateLoadpullResult is off", s);
        Assert.DoesNotContain("raise the Pin drive level", s);
    }

    /// <summary>The third distinguishable reason: the optima converged but the search recommended no
    /// terminations, so there was no grid to sweep. Widening the search is the response, not the drive.</summary>
    [Fact]
    public void APursuitThatRecommendedNoTerminations_SaysThat()
    {
        var ds = new DataSet();
        ds.Add("MXP_Converged",   DataCube.Scalar(1.0));
        ds.Add("MXE_Converged",   DataCube.Scalar(1.0));
        ds.Add("CacheCount",      DataCube.Scalar(30.0));
        ds.Add("RecommTermCount", DataCube.Scalar(0.0));

        var s = LoadpullRunSummary.Describe(ds);

        Assert.NotNull(s);
        Assert.Contains("no recommended terminations", s);
    }

    /// <summary>Partial convergence still names which side failed — the two optima fail for different
    /// reasons and the user needs to know which one to chase.</summary>
    [Fact]
    public void APursuitWhereOnlyOneOptimumConverged_NamesWhichOne()
    {
        var ds = new DataSet();
        ds.Add("MXP_Converged",   DataCube.Scalar(1.0));
        ds.Add("MXE_Converged",   DataCube.Scalar(0.0));
        ds.Add("CacheCount",      DataCube.Scalar(20.0));
        ds.Add("UnscorableCount", DataCube.Scalar(3.0));

        var s = LoadpullRunSummary.Describe(ds);

        Assert.NotNull(s);
        Assert.Contains("MXP converged, MXE did not", s);
        // Only a MINORITY was unscorable, so compression is not the story here and must not be blamed.
        Assert.DoesNotContain("raise the Pin drive level", s);
    }

    /// <summary>
    /// The other half of the owner's report: the SAME defect must not exist for a plain Loadpull. It
    /// does not, and structurally cannot — the engine publishes one StopCode per grid point whatever
    /// the outcome, so the grid path always has something to count. Pinned so it stays that way.
    /// </summary>
    [Fact]
    public void APlainLoadpullWhereNothingCompressed_StillReports()
    {
        string s = Describe(PinMax, PinMax, PinMax, NonConvergence);

        Assert.Contains("4 point(s) simulated", s);
        Assert.Contains("0 reached compression", s);
        Assert.Contains("raise the Pin drive level", s);
    }

    /// <summary>
    /// The report is posted once per loadpull analysis in the run. <c>WorkspaceViewModel</c> cannot
    /// be constructed headlessly, so the call site is pinned by reading the source — the same
    /// approach this suite already uses elsewhere for WorkspaceViewModel-only wiring.
    /// </summary>
    [Fact]
    public void TheRunPostsOneSummaryPerAnalysis_OnSuccess()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);

        string src = File.ReadAllText(Path.Combine(dir!.FullName, "src/Ui/ViewModels/WorkspaceViewModel.cs"));
        int at = src.IndexOf("case RunStatus.Success:", System.StringComparison.Ordinal);
        Assert.True(at > 0);
        string body = src[at..(at + 900)];

        Assert.Contains("LoadpullRunSummary.Describe(ar.Data)", body);
        Assert.Contains("foreach (var ar in result.Results)", body);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string Describe(params double[] stopCodes)
    {
        var ds = new DataSet();
        AddStopCode(ds, stopCodes);
        return Assert.IsType<string>(LoadpullRunSummary.Describe(ds));
    }

    private static void AddStopCode(DataSet ds, params double[] stopCodes)
    {
        var grid = new Axis("gridPoint",
            Enumerable.Range(0, stopCodes.Length).Select(i => (double)i).ToArray(), "");
        ds.Add("StopCode", new DataCube([grid], stopCodes));
    }
}

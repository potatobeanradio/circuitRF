using System.Linq;
using System.Numerics;
using CircuitRF.Ui.DataDisplay;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Loadpull UI brief 08 gate: shape-based, group-aware recognition of a loadpull result DataSet.
/// A simulated LP run.npy (cubes under an analysis-name group) is recognized identically to an
/// ingested flat .spl (cubes at top level). HB/DC/S-param and near-misses are rejected.
/// </summary>
public class LoadpullRecognitionTests
{
    // ── Builders ──────────────────────────────────────────────────────────────

    private static Axis Grid(int n) => new("gridPoint", Enumerable.Range(0, n).Select(i => (double)i).ToArray());
    private static Axis Pin(int n)  => new("pinStep",   Enumerable.Range(0, n).Select(i => (double)i).ToArray());
    private static Axis Named(string name, int n) => new(name, Enumerable.Range(0, n).Select(i => (double)i).ToArray());

    private static DataCube Termination(string name, int nGrid) =>
        new(new[] { Grid(nGrid) }, new Complex[nGrid]);

    private static DataCube Fom(int nGrid, int nPin) =>
        new(new[] { Grid(nGrid), Pin(nPin) }, new double[nGrid * nPin]);

    // A canonical loadpull cube set (GammaLoad + ZLoad + Pout/Gt) added to a group (or top level).
    private static void AddLoadpullCubes(DataSet ds, string? group)
    {
        void Add(string name, DataCube c)
        {
            if (group is null) ds.Add(name, c);
            else ds.AddToGroup(group, name, c);
        }
        Add("GammaLoad", Termination("GammaLoad", 4));
        Add("ZLoad",     Termination("ZLoad", 4));
        Add("Pout_dBm",      Fom(4, 3));
        Add("Gt_dB",        Fom(4, 3));
    }

    // Frequency-swept loadpull: a leading "freq" axis precedes the canonical termination/FOM axes.
    private static DataCube FreqTermination(int nFreq, int nGrid) =>
        new(new[] { Named("freq", nFreq), Grid(nGrid) }, new Complex[nFreq * nGrid]);
    private static DataCube FreqFom(int nFreq, int nGrid, int nPin) =>
        new(new[] { Named("freq", nFreq), Grid(nGrid), Pin(nPin) }, new double[nFreq * nGrid * nPin]);

    // ── Frequency-swept loadpull (FreqSweptLoadpull brief, Layer G) ────────────

    [Fact]
    public void FreqSwept_Loadpull_RecognizedWithLeadingFreqAxis()
    {
        var ds = new DataSet();
        ds.Add("GammaLoad", FreqTermination(3, 4));
        ds.Add("ZLoad",     FreqTermination(3, 4));
        ds.Add("Pout_dBm",  FreqFom(3, 4, 5));
        ds.Add("Gt_dB",     FreqFom(3, 4, 5));

        Assert.True(LoadpullRecognition.IsLoadpull(ds));
        Assert.Null(Assert.Single(LoadpullRecognition.FindLoadpullViews(ds)).Group);
    }

    // A leading axis with an arbitrary name — built by Named(name, n).
    private static DataCube LeadTermination(string axis, int nLead, int nGrid) =>
        new(new[] { Named(axis, nLead), Grid(nGrid) }, new Complex[nLead * nGrid]);
    private static DataCube LeadFom(string axis, int nLead, int nGrid, int nPin) =>
        new(new[] { Named(axis, nLead), Grid(nGrid), Pin(nPin) }, new double[nLead * nGrid * nPin]);

    // Bug regression: a Loadpull Pursuit wrapped in a PARAMETRIC SWEEP over a variable (e.g. RFfreq)
    // prepends a leading axis named after the variable — NOT "freq". Recognition must key on the
    // trailing {gridPoint[,pinStep]} signature, so the "+Summary" gate stays enabled. (Bug: the
    // leading axis name was hardcoded to "freq", disabling +Summary for parametric-swept loadpull.)
    [Theory]
    [InlineData("RFfreq")]   // swept frequency variable (the reported case)
    [InlineData("Vds")]      // any other swept variable wrapping the pursuit
    public void ParametricSwept_Loadpull_RecognizedRegardlessOfLeadingAxisName(string sweptVar)
    {
        var ds = new DataSet();
        ds.AddToGroup("LPP1", "GammaLoad", LeadTermination(sweptVar, 3, 4));
        ds.AddToGroup("LPP1", "ZLoad",     LeadTermination(sweptVar, 3, 4));
        ds.AddToGroup("LPP1", "Pout_dBm",  LeadFom(sweptVar, 3, 4, 5));
        ds.AddToGroup("LPP1", "Gt_dB",     LeadFom(sweptVar, 3, 4, 5));

        Assert.True(LoadpullRecognition.IsLoadpull(ds));
        Assert.Equal("LPP1", Assert.Single(LoadpullRecognition.FindLoadpullViews(ds)).Group);
    }

    // ── Pursuit result: follow-on loadpull cubes + MXP/MXE search scalars coexist ──

    // The LPP follow-on loadpull is embedded under its ORIGINAL cube names (no LP_ prefix), so the
    // pursuit result is a recognizable loadpull surface; the pursuit's own MXP_*/MXE_*/*Count scalars
    // coexist in the same group and must not break recognition.
    [Fact]
    public void PursuitResult_FollowOnPlusSearchScalars_IsRecognized()
    {
        var ds = new DataSet();
        AddLoadpullCubes(ds, group: null);                 // GammaLoad/ZLoad/Pout_dBm/Gt_dB (follow-on)
        ds.Add("MXP_PoutDbm",    DataCube.Scalar(40.0));   // pursuit search digest — extra scalars
        ds.Add("MXE_Eff",        DataCube.Scalar(0.7));
        ds.Add("RecommTermCount", DataCube.Scalar(48.0));

        Assert.True(LoadpullRecognition.IsLoadpull(ds));
        Assert.Null(Assert.Single(LoadpullRecognition.FindLoadpullViews(ds)).Group);
    }

    // ── Flat (.spl-style) ─────────────────────────────────────────────────────

    [Fact]
    public void Flat_Loadpull_RecognizedAsTopLevelView()
    {
        var ds = new DataSet();
        AddLoadpullCubes(ds, group: null);

        Assert.True(LoadpullRecognition.IsLoadpull(ds));
        var views = LoadpullRecognition.FindLoadpullViews(ds);
        var v = Assert.Single(views);
        Assert.Null(v.Group);
    }

    // ── Grouped (LP run.npy-style) ──────────────────────────────────────────────

    [Fact]
    public void Grouped_Loadpull_RecognizedWithGroupName()
    {
        var ds = new DataSet();
        AddLoadpullCubes(ds, group: "LP1");

        var v = Assert.Single(LoadpullRecognition.FindLoadpullViews(ds));
        Assert.Equal("LP1", v.Group);
    }

    [Fact]
    public void TwoGroups_TwoViews()
    {
        var ds = new DataSet();
        AddLoadpullCubes(ds, "LP1");
        AddLoadpullCubes(ds, "LP2");

        var views = LoadpullRecognition.FindLoadpullViews(ds);
        Assert.Equal(2, views.Count);
        Assert.Contains(views, v => v.Group == "LP1");
        Assert.Contains(views, v => v.Group == "LP2");
    }

    // ── Z-only termination is accepted ──────────────────────────────────────────

    [Fact]
    public void ZLoadOnly_Recognized()
    {
        var ds = new DataSet();
        ds.Add("ZLoad", Termination("ZLoad", 4));   // no GammaLoad
        ds.Add("Pout_dBm",  Fom(4, 3));

        Assert.True(LoadpullRecognition.IsLoadpull(ds));
    }

    // ── Negatives: HB / DC / S-param ────────────────────────────────────────────

    [Fact]
    public void HbDataSet_NotLoadpull()
    {
        var ds = new DataSet();
        ds.Add("V", new DataCube(new[] { Named("node", 3), Named("harmonic", 8) }, new Complex[24]));

        Assert.False(LoadpullRecognition.IsLoadpull(ds));
        Assert.Empty(LoadpullRecognition.FindLoadpullViews(ds));
    }

    [Fact]
    public void DcDataSet_NotLoadpull()
    {
        var ds = new DataSet();
        ds.Add("V", new DataCube(new[] { Named("node", 3) }, new Complex[3]));

        Assert.False(LoadpullRecognition.IsLoadpull(ds));
    }

    [Fact]
    public void SParamDataSet_NotLoadpull()
    {
        var ds = new DataSet();
        ds.Add("S", new DataCube(new[] { Named("freq", 5), Named("i", 2), Named("j", 2) }, new Complex[20]));

        Assert.False(LoadpullRecognition.IsLoadpull(ds));
    }

    // ── Near-misses: signature requires BOTH, with the right axes ───────────────

    [Fact]
    public void FomWithoutTermination_NotLoadpull()
    {
        var ds = new DataSet();
        ds.Add("Pout_dBm", Fom(4, 3));   // FOM but no GammaLoad/ZLoad

        Assert.False(LoadpullRecognition.IsLoadpull(ds));
    }

    [Fact]
    public void TerminationOnWrongAxis_NotLoadpull()
    {
        var ds = new DataSet();
        // GammaLoad over a non-gridPoint axis → not a loadpull termination.
        ds.Add("GammaLoad", new DataCube(new[] { Named("freq", 4) }, new Complex[4]));
        ds.Add("Pout_dBm",      Fom(4, 3));

        Assert.False(LoadpullRecognition.IsLoadpull(ds));
    }

    [Fact]
    public void TerminationButNoFom_NotLoadpull()
    {
        var ds = new DataSet();
        ds.Add("GammaLoad", Termination("GammaLoad", 4));
        // No FOM cube at all.

        Assert.False(LoadpullRecognition.IsLoadpull(ds));
    }
}

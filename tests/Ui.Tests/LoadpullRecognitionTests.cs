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
        Add("Pout",      Fom(4, 3));
        Add("Gt",        Fom(4, 3));
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
        ds.Add("Pout",  Fom(4, 3));

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
        ds.Add("Pout", Fom(4, 3));   // FOM but no GammaLoad/ZLoad

        Assert.False(LoadpullRecognition.IsLoadpull(ds));
    }

    [Fact]
    public void TerminationOnWrongAxis_NotLoadpull()
    {
        var ds = new DataSet();
        // GammaLoad over a non-gridPoint axis → not a loadpull termination.
        ds.Add("GammaLoad", new DataCube(new[] { Named("freq", 4) }, new Complex[4]));
        ds.Add("Pout",      Fom(4, 3));

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

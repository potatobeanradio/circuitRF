// ================================================================
//  NarrowedSliceRoundTripTests.cs
//  A narrowed X range survives being written down — in the .cdd, and in the spec text.
//
//  AxisSlice carries six fields; the .cdd DTO carried three, and Trace.BuildPickerExpression
//  rendered every KeepAsX axis as a bare ":". So a trace the user typed as "SP1.S[10..50, 1, 1]"
//  quietly went back to the whole axis — on save/load, and mid-session the moment anything
//  regenerated the spec. It was invisible rather than merely wrong, because a trace with both
//  CubeName and Slice set resolves through the SLICE: the card kept showing the typed range while
//  the plot drew all 101 points.
// ================================================================

using System.Linq;
using System.Numerics;
using System.Text.Json;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class NarrowedSliceRoundTripTests
{
    private static DataSet SweptS(int nFreq = 101)
    {
        var axes = new[]
        {
            new Axis("freq", Enumerable.Range(0, nFreq).Select(k => 1e8 + k * 2.9e7).ToArray(), "Hz"),
            new Axis("i",    new double[] { 0 }),
            new Axis("j",    new double[] { 0 }),
        };
        var ds = new DataSet();
        ds.AddToGroup("SP1", "S", new DataCube(axes,
            Enumerable.Range(0, nFreq).Select(k => new Complex(k * 0.01, 0)).ToArray()));
        ds.AddToGroup("SP1", "Z0", DataSetBuilder.BuildZ0Cube(new[] { new Complex(50, 0) }));
        return ds;
    }

    private static Trace NarrowedTrace()
    {
        var t = new Trace(new SNP(new double[] { 1e9 }, 1), MatrixType.S, 0, 0,
                          DependentVarFormat.Complex);
        t.CubeName = "SP1.S";
        t.Slice = new[]
        {
            new AxisSlice("freq", AxisRole.KeepAsX, 0, RangeStart: 10, RangeEndExclusive: 50),
            new AxisSlice("i",    AxisRole.PinToIndex, 0),
            new AxisSlice("j",    AxisRole.PinToIndex, 0),
        };
        return t;
    }

    // ── The .cdd ──────────────────────────────────────────────────────────────

    [Fact]
    public void CddRoundTrip_KeepsTheNarrowedRange()
    {
        var config = DataDisplayViewModel.BuildTraceConfig(NarrowedTrace(), configDir: "");

        Assert.Equal(10, config.CubeSlice[0].RangeStart);
        Assert.Equal(50, config.CubeSlice[0].RangeEndExclusive);

        // …and through the file, not just the object graph.
        string json = JsonSerializer.Serialize(config, DataDisplayViewModel.JsonOpts);
        var back = JsonSerializer.Deserialize<TraceConfig>(json, DataDisplayViewModel.JsonOpts)!;
        var slice = back.CubeSlice.Select(s => s.ToSlice()).ToArray();

        Assert.True(slice[0].IsNarrowedRange);
        Assert.Equal(10, slice[0].RangeStart);
        Assert.Equal(50, slice[0].RangeEndExclusive);
    }

    [Fact]
    public void CddRoundTrip_KeepsAPinnedAxisLabel()
    {
        var t = NarrowedTrace();
        t.Slice = new[]
        {
            new AxisSlice("freq", AxisRole.KeepAsX, 0),
            new AxisSlice("node", AxisRole.PinToIndex, 3, Label: "X1.drain"),
        };

        var config = DataDisplayViewModel.BuildTraceConfig(t, configDir: "");
        string json = JsonSerializer.Serialize(config, DataDisplayViewModel.JsonOpts);
        var back = JsonSerializer.Deserialize<TraceConfig>(json, DataDisplayViewModel.JsonOpts)!;

        Assert.Equal("X1.drain", back.CubeSlice[1].ToSlice().Label);
    }

    /// <summary>
    /// Every .cdd written before these fields existed omits them, and an omitted
    /// RangeEndExclusive MUST read back as −1 (the whole axis). Zero is a legal EMPTY range, so
    /// getting this default wrong would load every such trace with no points at all — a worse
    /// failure than the one being fixed.
    /// </summary>
    [Fact]
    public void ACddWrittenBeforeTheseFieldsExisted_StillMeansTheWholeAxis()
    {
        const string legacy = """
        { "AxisName": "freq", "Role": "KeepAsX", "Index": 0 }
        """;
        var slice = JsonSerializer.Deserialize<AxisSliceConfig>(legacy, DataDisplayViewModel.JsonOpts)!
                                  .ToSlice();

        Assert.False(slice.IsNarrowedRange);
        Assert.Equal(-1, slice.RangeEndExclusive);
        Assert.Equal("", slice.Label);
    }

    // ── The spec text ─────────────────────────────────────────────────────────

    [Fact]
    public void RegeneratedSpecText_StillCarriesTheRange_AndParsesBackToIt()
    {
        var t = NarrowedTrace();

        string spec = t.BuildPickerExpression()!;
        Assert.Equal("SP1.S[10..50, 1, 1]", spec);

        var ds = SweptS();
        Assert.True(CubeTraceSpecParser.TryParse(spec, ds, out var cubeName, out var slice,
                                                 out _, out var err), err);
        Assert.Equal("SP1.S", cubeName);
        Assert.True(slice![0].IsNarrowedRange);
        Assert.Equal(10, slice[0].RangeStart);
        Assert.Equal(50, slice[0].RangeEndExclusive);
    }

    [Fact]
    public void AWholeAxisIsStillWrittenAsAColon()
    {
        var t = NarrowedTrace();
        t.Slice = new[]
        {
            new AxisSlice("freq", AxisRole.KeepAsX, 0),
            new AxisSlice("i",    AxisRole.PinToIndex, 0),
            new AxisSlice("j",    AxisRole.PinToIndex, 0),
        };
        Assert.Equal("SP1.S[:, 1, 1]", t.BuildPickerExpression());
    }

    // ── And the range is what actually gets plotted ────────────────────────────

    [Fact]
    public void TheRestoredRangeIsWhatTheTraceDraws()
    {
        var config = DataDisplayViewModel.BuildTraceConfig(NarrowedTrace(), configDir: "");
        string json = JsonSerializer.Serialize(config, DataDisplayViewModel.JsonOpts);
        var back = JsonSerializer.Deserialize<TraceConfig>(json, DataDisplayViewModel.JsonOpts)!;

        var t = NarrowedTrace();
        t.Slice = back.CubeSlice.Select(s => s.ToSlice()).ToArray();

        PlotInspectorViewModel.SetCubeDataFrom(t, SweptS(), PlotType.Smith, FreqUnit.GHz);

        Assert.Equal(40, t.Points.Count);   // [10, 50) — not all 101
    }
}

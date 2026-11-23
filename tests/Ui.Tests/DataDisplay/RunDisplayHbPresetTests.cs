// brief-run-display-hb-preset-and-units gates 1, 2 and 4: a display whose traces read none of its source is told apart
// from one that still draws, and the run's plots (for HB, one plain spectrum plot) are added without touching it; the HB preset's three plots, its output
// pick shared with the Transient preset's, and its waveform plot left out for tones with no common period; and a marker
// readout that names its unit when the unit is known, and only then.

using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore;
using RfCore.Data;
using RfCore.Export;
using Xunit;

namespace CircuitRF.Ui.Tests.DataDisplay;

[Collection(SkiaFontsTypefaceCollection.Name)]
public sealed class RunDisplayHbPresetTests : IDisposable
{
    private readonly string _results = Directory.CreateTempSubdirectory("run-display-hb-").FullName;
    public void Dispose() { try { Directory.Delete(_results, recursive: true); } catch { } }

    private static readonly string[] Nets = ["Vin", "Vout"];

    /// <summary>A transient run of a 50 Hz half-wave rectifier, shaped as the engine writes one: V[time, node].</summary>
    private static DataSet Transient()
    {
        const int n = 400;
        var t = Enumerable.Range(0, n + 1).Select(i => i * 0.02 / n).ToArray();
        var v = new double[t.Length * 2];
        for (int s = 0; s < t.Length; s++)
        {
            double vin = 2 * Math.Cos(2 * Math.PI * 50 * t[s]);
            v[s * 2] = vin;
            v[s * 2 + 1] = Math.Max(0, vin - 0.6);
        }
        var ds = new DataSet();
        ds.AddToGroup("TRAN1", "V", new DataCube([new Axis("time", t, "s"), new Axis("node", [0, 1], "", Nets)], v));
        ds.AddToGroup("TRAN1", "Tran.AcceptedSteps", new DataCube([], [n]));
        ds.AddToGroup("TRAN1", "__TranFundamental_Hz", new DataCube([], [50.0]));
        return ds;
    }

    /// <summary>A harmonic-balance run of the same bench: V[node, harmonic] over an internal node and the two labelled nets,
    /// with the bench's one-number measure.</summary>
    private static DataSet Hb(bool withProbe = false)
    {
        string[] nodes = ["__d1_int", "Vin", "Vout"];
        const int k = 8;
        var v = new Complex[nodes.Length * k];
        for (int n = 0; n < nodes.Length; n++)
            for (int h = 0; h < k; h++)
                v[n * k + h] = h == 0 ? new Complex(0.4 * n, 0) : Complex.FromPolarCoordinates((n + 1.0) / (h * h), 0.3 * h);
        var ds = new DataSet();
        ds.AddToGroup("HB1", "V", new DataCube([new Axis("node", [0, 1, 2], "", nodes),
            new Axis("harmonic", [.. Enumerable.Range(0, k).Select(h => (double)h)], "")], v));
        ds.AddToGroup("HB1", "ToneFreqs", new DataCube([new Axis("tone", [1.0], "")], [50.0]));
        ds.AddToGroup("HB1", "__LabeledNodes", new DataCube([new Axis("label", [0, 1], "", Nets)], [0.0, 0.0]));
        if (withProbe)
            ds.AddToGroup("HB1", "Pprobe_W", new DataCube([new Axis("branch", [0], "", ["Iout"]),
                new Axis("harmonic", [.. Enumerable.Range(0, k).Select(h => (double)h)], "")],
                [.. Enumerable.Range(0, k).Select(h => 1e-3 / (h + 1))]));
        ds.AddToGroup(DataSet.MeasurementsGroup, "Vdc", new DataCube([], [1.25]));
        return ds;
    }

    /// <summary>A two-tone result on a mixIndex axis at the given products.</summary>
    private static DataSet TwoTone(double[] hz)
    {
        var v = new Complex[2 * hz.Length];
        for (int i = 0; i < v.Length; i++) v[i] = new Complex(1.0 / (i + 1), 0.1);
        var ds = new DataSet();
        ds.AddToGroup("HB1", "V", new DataCube([new Axis("node", [0, 1], "", Nets), new Axis("mixIndex", hz, "Hz")], v));
        ds.AddToGroup("HB1", "ToneFreqs", new DataCube([new Axis("tone", [1.0, 2.0], "")], [hz[1], hz[2]]));
        return ds;
    }

    [Fact]
    public void HbPreset_OneTone_WaveformsSpectrumAndMeasures_OnTheTransientPresetsOutput()
    {
        var plots = HbPreset.Build(Hb(), DataSourceRef.Selected);
        Assert.Equal(["Waveforms", "Spectrum of V(Vout)", "Measures"], plots.Select(p => p.CustomTitle));
        // the labelled nets only — not the diode's internal node
        Assert.Equal(Nets, plots[0].Traces.Select(t => t.Label));
        Assert.All(plots.SelectMany(p => p.Traces), t => Assert.Equal(DataSourceRef.Selected, t.SourcePath));
        // the waveform expression evaluates, over one period of 50 Hz
        Assert.True(TraceExpression.TryEvaluate(plots[0].Traces[0].Expression!, Hb(), PlotType.Rect,
            out var x, out _, out _, out string axis, out _, out _, out string err), err);
        Assert.Equal("time", axis);
        Assert.Equal(0.02, x[^1], 12);
        // the same output the Transient preset reads on the same bench
        Assert.Equal("Spectrum of V(Vout), last period", TransientPreset.Build(Transient(), DataSourceRef.Selected)[1].CustomTitle);
        // an output probe makes it the power through the probe, as the Transient preset's does
        Assert.Equal("P(Iout)", HbPreset.Build(Hb(withProbe: true), DataSourceRef.Selected)[1].Traces.Single().Label);
    }

    [Fact]
    public void HbPreset_TwoTone_DrawsAWaveformOnlyWhenTheTonesShareAPeriod()
    {
        var common = HbPreset.Build(TwoTone([0, 50, 1000, 950, 1050]), DataSourceRef.Selected);
        Assert.Equal(["Waveforms", "Spectrum of V(Vout)"], common.Select(p => p.CustomTitle));
        Assert.Equal(FreqUnit.kHz, common[1].FreqUnit);   // a 1 kHz product reads in kHz, not as 1e-6 GHz

        double f2 = Math.Sqrt(2) * 1e9;
        var none = HbPreset.Build(TwoTone([0, 1e9, f2, f2 - 1e9, 2e9]), DataSourceRef.Selected);
        Assert.Equal(["Spectrum of V(Vout)"], none.Select(p => p.CustomTitle));
        Assert.Equal(0, none[0].Left);
    }

    private void Write(string name, DataSet ds) => DataSetExporter.Export(ds, Path.Combine(_results, name), ExportFormat.Npy);

    [Fact]
    public async Task DisplayShowingNothingOfItsSource_IsReported_AndTheRunsPlotsAreAddedBesideIt()
    {
        Write("rectifier3.npy", Transient());
        Write("rectifier3_hb.npy", Hb());
        var window = new DisplayWindowViewModel();
        var lib = window.DataSourceLibrary;
        lib.ResultsRootProvider = () => _results;
        lib.RefreshAvailableDataSources();
        await lib.SelectDataSourceAsync("rectifier3.npy");
        var display = window.DataDisplay!;
        await display.AddTransientAsync();
        Assert.False(window.ShowsNothingOfItsSource());

        // the bench now runs harmonic balance: every transient trace is <invalid>
        await lib.SelectDataSourceAsync("rectifier3_hb.npy");
        Assert.True(window.ShowsNothingOfItsSource());
        var before = display.Plots.SelectMany(p => p.PlotVM.Plot.Traces).Select(t => (t.CubeName, t.Expression, t.Label)).ToList();

        // an HB run gets ONE plain plot of its spectrum — no waveforms, no title of ours; the HB preset is opt-in
        var added = await display.AddRunPlotsAsync();
        var plain = Assert.Single(added).PlotVM.Plot;
        Assert.True(string.IsNullOrEmpty(plain.CustomTitle));
        var seeded = Assert.Single(plain.Traces);
        Assert.Equal("HB1.V", seeded.CubeName);
        Assert.True(seeded.IsHarmonicStem);
        Assert.Equal("Vin", seeded.Slice.Single(a => a.AxisName == "node").Label);   // a labelled net, not __d1_int at index 0
        Assert.DoesNotContain(plain.Traces, t => (t.Expression ?? "").Contains("waveform", StringComparison.Ordinal));
        // the existing traces are untouched, and the display draws again
        Assert.Equal(before, display.Plots.Except(added).SelectMany(p => p.PlotVM.Plot.Traces)
                                       .Select(t => (t.CubeName, t.Expression, t.Label)).ToList());
        Assert.False(window.ShowsNothingOfItsSource());

        // gate 4 on a live stem: the transient preset's spectrum, read at DC, in dBV
        await lib.SelectDataSourceAsync("rectifier3.npy");
        var spectrum = display.Plots.First(p => p.PlotVM.Plot.CustomTitle.StartsWith("Spectrum of V(Vout), last", StringComparison.Ordinal))
                              .PlotVM.Plot.Traces.Single();
        var m = new Marker(spectrum, freq: 0, isMulti: false, isDelta: false, index: 1)
        { PositionStatic = new System.Numerics.Vector2(0, 0) };
        string row = spectrum.GetStemValString(m, showFilePrefix: false);
        Assert.EndsWith(" dBV", row);
    }

    [Theory]
    [InlineData("dB(fft(TRAN1.V[:, 1], 50))", "dBV")]
    [InlineData("dBm(HB1.Pprobe_W[0, :])", "dBm")]
    [InlineData("dB(SP1.S[:, 1, 0])", "dB")]
    [InlineData("waveform(HB1.V[1, :], 50)", "V")]
    [InlineData("HB1.I(\"Iout\")", "A")]
    [InlineData("TRAN1.V[:, 0] * 2", null)]
    [InlineData("dB(HB1.Gain)", null)]
    public void ValueUnit_IsSaidOnlyWhenKnown(string expression, string? unit)
    {
        var t = new Trace(new SNP([1e9], 2), MatrixType.S, 0, 0, DependentVarFormat.Db) { Expression = expression };
        Assert.Equal(unit, t.ValueUnit);
    }

    [Fact]
    public void NetworkMarker_ReadsDb_OnAnSParameterInDb()
    {
        var snp = new SNP([1e9, 2e9], 2);
        var t = new Trace(snp, MatrixType.S, 1, 0, DependentVarFormat.Db);
        Assert.Equal("dB", t.ValueUnit);
        var m = new Marker(t, freq: 1e9, isMulti: false, isDelta: false, index: 1);
        Assert.EndsWith(" dB", t.GetMarkerValString(m, showFilePrefix: false));
    }
}

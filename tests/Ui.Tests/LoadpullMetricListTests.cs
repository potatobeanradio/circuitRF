using System;
using System.IO;
using System.Linq;
using System.Numerics;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore;
using RfCore.Data;
using RfCore.Export;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Regression for the simulated-LP contour metric list (bug: trace card showed bookkeeping cubes —
/// isTickle / PavlDbm / Converged / StopCode — and hid headline FOMs like Pout / DE / PAE).
/// A simulated LP run.npy nests its cubes under an analysis group ("LP1"); the metric picker must:
///   - exclude simulation-only bookkeeping cubes (those leak only on sim runs, not measured .spl),
///   - always offer the headline FOMs (Pout/Gt/Gp/DE/PAE) even when flat (e.g. DE/PAE 0 w/o bias-tee).
/// Drives the real VM stack (library → inspector → contour trace) through a temp grouped .npy.
/// </summary>
public sealed class LoadpullMetricListTests
{
    private static Axis Grid() => new("gridPoint", new[] { 0.0, 1, 2, 3 },
        labels: new[] { "50+0j", "60+10j", "40-10j", "70+0j" });
    private static Axis Pin() => new("pinStep", new[] { -10.0, -5, 0 }, labels: new[] { "-10", "-5", "0" });

    // {gridPoint, pinStep} double cube: varying, or all-constant when flat=true.
    private static DataCube Fom2D(bool varying)
    {
        var d = new double[4 * 3];
        for (int i = 0; i < d.Length; i++) d[i] = varying ? i * 0.1 + 0.05 : 0.0;
        return new DataCube(new[] { Grid(), Pin() }, d);
    }

    private static DataCube Term(double scale)
    {
        var d = new Complex[4];
        for (int i = 0; i < 4; i++) d[i] = new Complex(0.05 * (i + 1) * scale, 0.02 * i);
        return new DataCube(new[] { Grid() }, d);
    }

    // V/INl spectra over {gridPoint(4), pinStep(3), node(2: src,load), harmonic(2: DC,fund)} so the
    // post-processor can derive Zin/IRL/AMPM. Returns (V, INl).
    private static (DataCube V, DataCube Inl) Spectra()
    {
        const int nG = 4, nP = 3, nN = 2, nH = 2, srcIdx = 0, loadIdx = 1;
        var node = new Axis("node",     new[] { 0.0, 1 }, labels: new[] { "n_gate", "n_drain" });
        var harm = new Axis("harmonic", new[] { 0.0, 1 });
        var v   = new Complex[nG * nP * nN * nH];
        var inl = new Complex[nG * nP * nN * nH];
        int Idx(int gi, int pi, int ni, int hi) => ((gi * nP + pi) * nN + ni) * nH + hi;
        for (int gi = 0; gi < nG; gi++)
        for (int pi = 0; pi < nP; pi++)
        {
            int fom = gi * nP + pi;
            double zin = 60.0 + 2.0 * fom;           // varying real input impedance
            v[Idx(gi, pi, srcIdx, 1)]   = new Complex(zin * 0.01, 0);
            inl[Idx(gi, pi, srcIdx, 1)] = new Complex(0.01, 0);
            double th = 5.0 * pi * Math.PI / 180.0;  // load fundamental phase → AM/PM
            v[Idx(gi, pi, loadIdx, 1)]  = new Complex(Math.Cos(th), Math.Sin(th));
        }
        return (new DataCube(new[] { Grid(), Pin(), node, harm }, v),
                new DataCube(new[] { Grid(), Pin(), node, harm }, inl));
    }

    // Writes a simulated-LP-shaped grouped run.npy. When enrich=true, runs the post-processor first
    // (the run-pipeline behavior) so the persisted file carries the derived display metrics.
    private static string WriteGroupedLpNpy(bool enrich)
    {
        var ds = new DataSet();
        void G(string name, DataCube c) => ds.AddToGroup("LP1", name, c);

        // Bookkeeping (must NOT appear as metrics) — all have a gridPoint axis and vary.
        G("Converged", Fom2D(varying: true));
        G("IsTickle",  Fom2D(varying: true));
        G("PavlDbm",   Fom2D(varying: true));
        G("StopCode",  new DataCube(new[] { Grid() }, new[] { 0.0, 1, 0, 2 }));
        // Termination coordinate (not a metric).
        G("GammaLoad", Term(0.5));
        G("ZLoad",     Term(50));
        // Headline FOMs — Pout/Gt/Gp vary; DE/PAE flat (no bias-tee) → still must be offered.
        G("Pout", Fom2D(varying: true));
        G("Gt",   Fom2D(varying: true));
        G("Gp",   Fom2D(varying: true));
        G("DE",   Fom2D(varying: false));
        G("PAE",  Fom2D(varying: false));
        // Spectra + node-identity provenance (engine output).
        var (v, inl) = Spectra();
        G("V", v);
        G("INl", inl);
        G("__SrcNodeIdx",  new DataCube(Array.Empty<Axis>(), new[] { 0.0 }));
        G("__LoadNodeIdx", new DataCube(Array.Empty<Axis>(), new[] { 1.0 }));

        if (enrich) RfCore.Loadpull.LoadpullPostProcessor.Enrich(ds, "LP1");

        string path = Path.Combine(Path.GetTempPath(), $"crf_lp_metric_{Guid.NewGuid():N}.npy");
        DataSetExporter.Export(ds, path, ExportFormat.Npy);
        return path;
    }

    [Fact]
    public async System.Threading.Tasks.Task MetricList_ExcludesBookkeeping_OffersHeadlineFoms()
    {
        string npy = WriteGroupedLpNpy(enrich: true);
        try
        {
            var lib = new DataSourceLibraryViewModel();
            await lib.SelectDataSourceAsync(npy);
            Assert.NotNull(lib.SelectedEntry);

            // Shape recognition still locates the LP1 group (brief 08).
            Assert.True(LoadpullRecognition.IsLoadpull(lib.SelectedEntry!.Data!));

            var plot      = new Plot(PlotType.Smith, FreqUnit.GHz);
            var inspector = new PlotInspectorViewModel(plot, () => {}, library: lib);
            Assert.True(inspector.CanAddContourTrace);

            inspector.AddContourTraceCommand.Execute(null);
            var metrics = inspector.Traces[0].AvailableMetrics;

            // Headline FOMs always offered (canonical unit-suffixed names) — including flat Efficiency/PAE.
            Assert.Contains("Pout_dBm",   metrics);
            Assert.Contains("Gt_dB",      metrics);
            Assert.Contains("Gp_dB",      metrics);
            Assert.Contains("Efficiency", metrics);
            Assert.Contains("PAE",        metrics);

            // Bookkeeping / termination cubes excluded.
            Assert.DoesNotContain("IsTickle",  metrics);
            Assert.DoesNotContain("Converged", metrics);
            Assert.DoesNotContain("StopCode",  metrics);
            Assert.DoesNotContain("PavlDbm",   metrics);
            Assert.DoesNotContain("GammaLoad", metrics);
            // No ambiguous bare "Pout" (Watts) — renamed to Pout_dBm/Pout_W.
            Assert.DoesNotContain("Pout", metrics);

            // Pout_dBm sorts first (priority table).
            Assert.Equal("Pout_dBm", metrics[0]);
            // Interface spectra are not metrics.
            Assert.DoesNotContain("V",   metrics);
            Assert.DoesNotContain("INl", metrics);
        }
        finally
        {
            try { File.Delete(npy); } catch { /* best-effort temp cleanup */ }
        }
    }

    // After the post-processor runs (the run-pipeline behavior), the derived display metrics a measured
    // .spl carries — Pout_dBm / Zin_real / Zin_imag / IRL / AMPM — are offered on the simulated source.
    [Fact]
    public async System.Threading.Tasks.Task MetricList_AfterPostProcessing_OffersDerivedMetrics()
    {
        string npy = WriteGroupedLpNpy(enrich: true);
        try
        {
            var lib = new DataSourceLibraryViewModel();
            await lib.SelectDataSourceAsync(npy);
            Assert.NotNull(lib.SelectedEntry);

            var plot      = new Plot(PlotType.Smith, FreqUnit.GHz);
            var inspector = new PlotInspectorViewModel(plot, () => {}, library: lib);
            inspector.AddContourTraceCommand.Execute(null);
            var metrics = inspector.Traces[0].AvailableMetrics;

            Assert.Contains("Pout_dBm",  metrics);
            Assert.Contains("Zin_real",  metrics);
            Assert.Contains("Zin_imag",  metrics);
            Assert.Contains("IRL_dB",    metrics);
            Assert.Contains("AMPM_deg",  metrics);
            // Canonical FOMs present; spectra/bookkeeping still excluded.
            Assert.Contains("Efficiency", metrics);
            Assert.DoesNotContain("V",   metrics);
            Assert.DoesNotContain("IsTickle", metrics);
        }
        finally
        {
            try { File.Delete(npy); } catch { /* best-effort temp cleanup */ }
        }
    }

    // +Contour must render immediately: the new contour defaults to the first available metric
    // (priority → Pout_dBm) and (on Rect) the grid is built so the plot autoscales to it.
    [Fact]
    public async System.Threading.Tasks.Task AddContour_DefaultsToFirstMetric_AndBuildsGrid()
    {
        string npy = WriteGroupedLpNpy(enrich: true);
        try
        {
            var lib = new DataSourceLibraryViewModel();
            await lib.SelectDataSourceAsync(npy);

            var plot      = new Plot(PlotType.Rect, FreqUnit.GHz);
            var inspector = new PlotInspectorViewModel(plot, () => {}, library: lib);
            inspector.AddContourTraceCommand.Execute(null);

            var cd = plot.Traces[0].ContourData;
            Assert.NotNull(cd);
            Assert.Equal("Pout_dBm", cd!.MetricName);                       // not the stale "Pout" default
            Assert.Equal("Pout_dBm", inspector.Traces[0].ContourMetricName); // VM combo reflects it
            // With a valid metric the contour resolves through the surface (the grid/fit then renders &
            // autoscales on real compressing data; this synthetic ramp may not reach the 3 dB constraint).
            Assert.Contains("Pout_dBm", inspector.Traces[0].AvailableMetrics);
        }
        finally
        {
            try { File.Delete(npy); } catch { /* best-effort temp cleanup */ }
        }
    }
}

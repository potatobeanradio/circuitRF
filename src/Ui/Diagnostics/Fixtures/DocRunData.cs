using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using CircuitRF.Ui.Schematic;
using RfCore.Data;
using RfCore.Export;

namespace CircuitRF.Ui.Diagnostics.Fixtures;

/// <summary>
/// Real simulation results for the documentation figures that need data in them.
///
/// <para><b>A figure of an empty axis frame teaches a reader nothing</b> — it is the documentation
/// equivalent of a screenshot of a blank window. Every Data Display figure in the user docs therefore
/// shows a curve that came out of the actual engines, on this run, from a shipped schematic.</para>
///
/// <para><b>Nothing is cached and nothing is committed.</b> The four shipped schematic templates run
/// headlessly in well under a second each between them — S-parameters 11 ms, the harmonic-balance
/// drive sweep 79 ms, the load-pull under a second, measured — so a docs regeneration simply runs them.
/// A committed <c>.npy</c> would be a second copy of the engines' output that nothing re-derives, and
/// it would go stale silently: the figure would keep rendering, and would keep being wrong.</para>
///
/// <para>The path is the production one, seam for seam: the embedded <c>.csch</c> is read through
/// <c>SchematicPersistence</c>, extracted by <c>NetExtractor</c>, written by <c>CnlWriter</c>, and run
/// through <c>SchematicRunService.Prepare</c>/<c>Execute</c> — the same three calls the Run button
/// makes. A template that stops elaborating fails the docs build rather than quietly producing an
/// empty plot.</para>
/// </summary>
public static class DocRunData
{
    /// <summary>The scratch directory this process's doc runs write their netlists and results into.</summary>
    private static readonly Lazy<string> Scratch = new(() =>
    {
        var dir = Path.Combine(Path.GetTempPath(), "circuitrf-docgen-" + Environment.ProcessId);
        Directory.CreateDirectory(dir);
        return dir;
    });

    private static readonly Dictionary<string, string> _cache = new(StringComparer.Ordinal);

    /// <summary>
    /// The directory every doc results file lands in — what a fixture hands to
    /// <c>DataSourceLibraryViewModel.ResultsRootProvider</c>.
    /// </summary>
    public static string ResultsRoot => Path.Combine(Scratch.Value, "results");

    /// <summary>
    /// S-parameters of the shipped FET test bench, 1–10 GHz in 101 points.
    /// Returns the results file name, as the data-source library's own logical id.
    /// </summary>
    public static string SParameters() => Run("SParameters", tb => { });

    /// <summary>
    /// The shipped harmonic-balance drive sweep: HB at 2 GHz swept over available input power, which
    /// is the result a trace card is most often pointed at.
    /// </summary>
    public static string HarmonicBalance() => Run("HarmonicBalance", tb => { },
                                                  template: "FET_Harmonic_Balance_Sweep");

    /// <summary>
    /// A load-pull over a Γ-plane constellation — 73 terminations, six rings and the centre —
    /// which is what a contour trace interpolates between.
    ///
    /// <para><b>The analysis is built here rather than read from a template, and that is the one
    /// departure worth naming.</b> The shipped load-pull template carries a <em>pursuit</em>
    /// analysis, which searches for an optimum instead of sweeping a grid, so it produces a
    /// trajectory and not a surface — nothing to draw a contour through. The circuit, the device,
    /// the tuners and the bias are all the shipped template's; only the analysis card is swapped for
    /// the grid sweep, which is exactly the edit a user makes in the Analyses panel.</para>
    /// </summary>
    public static string Loadpull() => Run("Loadpull", tb =>
    {
        var pursuit = tb.Analyses.OfType<LoadpullPursuitAnalysis>().FirstOrDefault()
            ?? throw new InvalidOperationException(
                "The shipped load-pull template no longer declares a pursuit analysis, so the doc "
              + "fixture cannot borrow its tuner names. Fix the fixture rather than the template.");

        tb.Analyses.Clear();
        tb.Analyses.Add(new LoadpullAnalysis("LP1")
        {
            ToneExpr        = pursuit.ToneExpr,
            ToneUnit        = pursuit.ToneUnit,
            LoadTunerName   = pursuit.LoadTunerName,
            SourceTunerName = pursuit.SourceTunerName,
            GridPath        = GammaGrid(),
            PinStartExpr    = "-10",
            PinMaxExpr      = "25",
            PinStepExpr     = "2",
            MaxHarmonicExpr = "3",
        });
    }, template: "FET_Loadpull_Pursuit");

    /// <summary>
    /// The New User's Guide's second worked example, run for real: a series 2 nH and a shunt 0.8 pF
    /// between two 50 Ω Terms, swept 1-5 GHz.
    ///
    /// <para>The point of the figure it feeds is that a reader can build the schematic beside it and
    /// compare their own curve, so the curve has to come from that exact schematic — which is why the
    /// example is an authored <c>.csch</c> rather than a few lines of view-model.</para>
    /// </summary>
    public static string ExampleSParam()
        => Run("ExampleSParam", tb => { }, template: "Example_SParam_LC", docSchematic: true);

    // ── The run ───────────────────────────────────────────────────────────────

    private static string Run(string key, Action<TestBench> shape,
                              string template = DocFixtures.SchematicTemplateId,
                              bool docSchematic = false)
    {
        if (_cache.TryGetValue(key, out var cached)) return cached;

        string dir = Path.Combine(Scratch.Value, key);
        Directory.CreateDirectory(dir);

        var model = docSchematic
            ? ShippedSchematicTemplates.LoadDocSchematic(template)
            : ShippedSchematicTemplates.Load(template, dir);
        var extracted = NetExtractor.Extract(model, key);
        shape(extracted.TestBench);

        string cnl = Path.Combine(dir, "netlist.cnl");
        File.WriteAllText(cnl, CnlWriter.Write(extracted.TestBench, extracted.Library,
                                               $"generated for the user documentation from {template}"));

        var plan = SchematicRunService.Prepare(cnl, dir);
        if (plan.Status != RunStatus.Success)
            throw new InvalidOperationException(
                $"The documentation's '{key}' fixture could not be planned from the shipped '{template}' "
              + $"template: {plan.StatusMessage}. The figure would have been an empty plot frame, which "
              + "is why this is an error and not a warning.");

        var result = SchematicRunService.Execute(plan, new RunControl());
        if (result.Status != RunStatus.Success || result.GroupedResults is not { } grouped)
            throw new InvalidOperationException(
                $"The documentation's '{key}' fixture ran but produced no results: {result.StatusMessage}.");

        Directory.CreateDirectory(ResultsRoot);
        string file = key + ".npy";
        string path = Path.Combine(ResultsRoot, file);
        if (File.Exists(path)) File.Delete(path);
        DataSetExporter.Export(grouped, path, ExportFormat.Npy);

        _cache[key] = file;
        return file;
    }

    /// <summary>
    /// The reference impedance the documentation's load-pull grid is drawn in, and the one its
    /// figure is plotted in.
    ///
    /// <para><b>250 Ω, not 50.</b> The grid exists to show what a load-pull figure looks like, and a
    /// device is pulled around the impedance it actually wants — for the shipped FET that is a load
    /// of a few hundred ohms, not the measurement system's 50. Referencing the constellation to 50
    /// crowds every termination into one corner of the chart, which is a true picture of a badly
    /// chosen grid and a poor picture of load-pull (owner, 2026-08-20).</para>
    /// </summary>
    public const double LoadpullGridZ0 = 250.0;

    /// <summary>
    /// The load-pull termination grid: the origin plus six rings of twelve, out to |Γ| = 0.88,
    /// referenced to <see cref="LoadpullGridZ0"/>.
    /// Written in the ordinary <c>.gam</c> magnitude/angle form the grid reader takes.
    /// </summary>
    private static string GammaGrid()
    {
        string path = Path.Combine(Scratch.Value, "doc-loadpull-grid.gam");
        if (File.Exists(path)) return path;

        using var w = new StreamWriter(path);
        w.WriteLine($"# gamma Z0={LoadpullGridZ0.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} mag_ang");
        w.WriteLine("; Generated for the user documentation — a constellation dense enough to");
        w.WriteLine("; interpolate a contour through, and coarse enough to run in a fraction of a second.");
        w.WriteLine("0 0");
        foreach (double mag in (double[])[0.15, 0.30, 0.45, 0.60, 0.75, 0.88])
            for (int k = 0; k < 12; k++)
                w.WriteLine($"{mag.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)} {k * 30}");
        return path;
    }
}

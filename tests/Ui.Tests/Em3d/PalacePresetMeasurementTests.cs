using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout.Em;
using RfCore;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Em3d;

/// <summary>
/// brief-em3d-21 R-em3d21-4c — the ONE measurement behind the Palace presets: F0's cases A and B at
/// Draft, Standard and Accurate, through circuitRF, recording wall time, Palace's own peak memory and the
/// largest |ΔS21| and ∠ΔS21 against Accurate. It is not a gate: it takes most of an hour and ~12 GB, so
/// it is skipped unless <c>CRF_MEASURE_PALACE_PRESETS=1</c>, and it is run once, alone, on the machine
/// whose numbers PalacePresetTable prints. <c>CRF_MEASURE_PALACE_PRESETS_DIR</c> keeps the run folders
/// (every Gmsh and Palace log) and the table, <c>presets.md</c>, where it names.
/// </summary>
[Collection(SkiaFontsTypefaceCollection.Name)]
public sealed class PalacePresetMeasurementTests(ITestOutputHelper output)
{
    [MeasurePresetsFact]
    public void Measure_ThePresets_OnF0sCasesAAndB()
    {
        string root = Environment.GetEnvironmentVariable("CRF_MEASURE_PALACE_PRESETS_DIR") is { Length: > 0 } d
            ? d : Path.Combine(Path.GetTempPath(), "crf-presets-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        string table = Path.Combine(root, "presets.md");
        File.WriteAllText(table, "| case | preset | wall s | Palace s | peak GB (Palace) | Gmsh tets | unknowns | passes | samples | max dS21 dB | max dS21 deg |\n|---|---|---|---|---|---|---|---|---|---|---|\n");

        foreach (string c in new[] { "B", "A" })
        {
            var runs = new Dictionary<PalaceQuality, (double Wall, PalaceRunSummary S, long? Tets, SNP Snp)>();
            foreach (var q in new[] { PalaceQuality.Draft, PalaceQuality.Standard, PalaceQuality.Accurate })
            {
                string dir = Path.Combine(root, $"{c}-{q}");
                var (setup, source) = c == "A"
                    ? PalaceProgressTests.CaseA(Path.Combine(dir, "layout"))
                    : Em3dGeneratorTests.CaseB(plated: false);
                if (c == "B") setup.AirBox = PalaceBackendTests.Padded(1500);    // F0's box, as gate 7 of brief 7
                setup.Palace = q == PalaceQuality.Standard ? null : new CemPalace { Quality = q };

                var wall = Stopwatch.StartNew();
                var result = EmRunService.Run(setup, source, Path.Combine(dir, "results"), confirmMemory: _ => true);
                wall.Stop();
                Assert.True(result.Status == EmRunStatus.Ok, $"{c} {q}: {result.Error}");
                foreach (string w in result.Warnings) output.WriteLine($"{c} {q} warning: {w}");
                output.WriteLine($"{c} {q}: {result.Notes![^1]}");

                string runDir = Path.Combine(dir, "results", Em3dRunService.ResultKey(setup, Em3dSolver.Palace));
                var tracker = new PalaceStageTracker(PalaceSettings.Resolve(setup.Palace).AdaptiveMaxIterations, 0, null);
                foreach (string line in File.ReadLines(Path.Combine(runDir, PalaceRun.PalaceLogFile))) tracker.Line(line);
                var gmsh = new GmshLogProgress(null);
                foreach (string line in File.ReadLines(Path.Combine(runDir, PalaceRun.GmshLogFile))) gmsh.Line(line);
                runs[q] = (wall.Elapsed.TotalSeconds, tracker.Summary, gmsh.Tetrahedra, TouchstoneIO.ReadFile(result.SnpPath!));
            }

            var reference = runs[PalaceQuality.Accurate].Snp;
            foreach (var (q, r) in runs)
            {
                double db = 0, deg = 0;
                for (int i = 0; i < reference.Frequencies.Length; i++)
                {
                    Complex a = r.Snp[i][1, 0], b = reference[i][1, 0];
                    db  = Math.Max(db, Math.Abs(20 * Math.Log10(a.Magnitude / b.Magnitude)));
                    deg = Math.Max(deg, Math.Abs((a / b).Phase) * 180 / Math.PI);
                }
                string row = string.Create(CultureInfo.InvariantCulture,
                    $"| {c} | {q} | {r.Wall:0.0} | {r.S.PalaceSeconds:0.0} | {r.S.PeakMemoryBytes / 1e9:0.0} | {r.Tets} | " +
                    $"{r.S.Unknowns} | {r.S.RefinementPasses} | {r.S.SweepSamples} | {db:0.0000} | {deg:0.000} |\n");
                File.AppendAllText(table, row);
                output.WriteLine(row.TrimEnd());
            }
        }
        output.WriteLine(File.ReadAllText(table));
    }
}

/// <summary>Runs only when asked for by <c>CRF_MEASURE_PALACE_PRESETS=1</c>, and only with Palace.</summary>
public sealed class MeasurePresetsFactAttribute : FactAttribute
{
    public MeasurePresetsFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("CRF_MEASURE_PALACE_PRESETS") != "1")
            Skip = "the preset measurement (most of an hour) runs only with CRF_MEASURE_PALACE_PRESETS=1";
        else if (new PalaceFactAttribute().Skip is { } why)
            Skip = why;
    }
}

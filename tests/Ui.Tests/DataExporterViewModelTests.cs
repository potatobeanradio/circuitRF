// Gate tests for DataExporterViewModel (B5 in the data-exporter brief).
// All tests are headless — no Avalonia runtime needed.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore;
using RfCore.Data;
using RfCore.Export;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class DataExporterViewModelTests : IDisposable
{
    private readonly string _tmpDir;

    public DataExporterViewModelTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { /* best-effort */ }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string MakeResultsRoot() => _tmpDir;

    // Flat results/<schematic>.npy layout (brief-results-storage-and-data-display.md §1) — every
    // result file sits directly in the results root, no per-schematic subdirectory.
    private string MakeRunNpy(string schematic, DataSet? ds = null)
    {
        string path = Path.Combine(_tmpDir, schematic + ".npy");

        if (ds is null)
        {
            // Create a minimal DataSet with one group
            ds = new DataSet();
            var ax   = new Axis("x", new[] { 0.0, 1.0, 2.0 }, "V");
            var cube = new DataCube(new[] { ax }, new[] { 1.0, 2.0, 3.0 });
            ds.AddToGroup("SP1", "V", cube);
        }
        DataSetExporter.Export(ds, path, ExportFormat.Npy);
        return path;
    }

    private DataSet MakeSpDataSet()
    {
        var freqAx = new Axis("freq", new[] { 1e9, 2e9, 3e9 }, "Hz");
        var iAx    = new Axis("i",    new[] { 0.0, 1.0 }, "", new[] { "1", "2" });
        var jAx    = new Axis("j",    new[] { 0.0, 1.0 }, "", new[] { "1", "2" });

        var flat = new Complex[3 * 2 * 2];
        for (int f = 0; f < 3; f++)
        for (int i = 0; i < 2; i++)
        for (int j = 0; j < 2; j++)
            flat[f * 4 + i * 2 + j] = new Complex(0.1 * (f+1), 0);

        var sCube  = new DataCube(new[] { freqAx, iAx, jAx }, flat);
        var z0Cube = DataSetBuilder.BuildZ0Cube(new Complex[] { new(50, 0), new(50, 0) });

        var ds = new DataSet();
        ds.AddToGroup("SP1", "S",  sCube);
        ds.AddToGroup("SP1", "Z0", z0Cube);
        return ds;
    }

    // ── Test T1: No results root → AvailableSchematicNames is empty ──────────

    [Fact]
    public void T1_NoResultsRoot_NoSchematicNames()
    {
        var vm = new DataExporterViewModel(null);
        Assert.Empty(vm.AvailableSchematicNames);
        Assert.Null(vm.SelectedSchematic);
        Assert.False(vm.CanExport);
    }

    // ── Test T2: With results root → enumerates every flat .npy file ─────────

    [Fact]
    public void T2_ResultsRoot_EnumeratesSchematicsByRunNpy()
    {
        MakeRunNpy("Amp");
        MakeRunNpy("Filter");
        // A non-.npy file and an unrelated subdirectory — neither should appear.
        File.WriteAllText(Path.Combine(_tmpDir, "notes.txt"), "not a result");
        Directory.CreateDirectory(Path.Combine(_tmpDir, "Empty"));

        var vm = new DataExporterViewModel(_tmpDir);
        Assert.Contains("Amp",    vm.AvailableSchematicNames);
        Assert.Contains("Filter", vm.AvailableSchematicNames);
        Assert.DoesNotContain("Empty", vm.AvailableSchematicNames);
        Assert.DoesNotContain("notes", vm.AvailableSchematicNames);
    }

    // ── Test T3: Preselect respected ─────────────────────────────────────────

    [Fact]
    public void T3_Preselect_SetSelectedSchematic()
    {
        MakeRunNpy("Amp");
        MakeRunNpy("Filter");

        var vm = new DataExporterViewModel(_tmpDir, preselectSchematic: "Filter");
        Assert.Equal("Filter", vm.SelectedSchematic);
    }

    // ── Test T4: Default mode = Npy, CanExport when groups present ────────────

    [Fact]
    public void T4_DefaultMode_NpyAndCanExport()
    {
        MakeRunNpy("Amp");

        var vm = new DataExporterViewModel(_tmpDir);
        Assert.Equal(ExportMode.Npy, vm.ExportMode);
        // IncludeRows should have SP1 checked → CanExport should be true
        Assert.True(vm.CanExport, "CanExport should be true after loading a DataSet with groups");
    }

    // ── Test T5: Switching to Touchstone for non-S group → IncludeRows empty ─

    [Fact]
    public void T5_TouchstoneMode_NoSCube_EmptyIncludeRows()
    {
        MakeRunNpy("Amp");   // has SP1.V, not SP1.S

        var vm = new DataExporterViewModel(_tmpDir);
        vm.ExportMode = ExportMode.Touchstone;

        // No group has an S cube → IncludeRows empty → CanExport = false
        Assert.Empty(vm.IncludeRows);
        Assert.False(vm.CanExport);
    }

    // ── Test T6: ExportDataSet writes npy to disk ─────────────────────────────

    [Fact]
    public void T6_ExportDataSet_WritesNpyFile()
    {
        MakeRunNpy("Amp");

        var vm = new DataExporterViewModel(_tmpDir);
        vm.ExportMode = ExportMode.Npy;

        string outPath = Path.Combine(_tmpDir, "output.npy");
        vm.ExportDataSet(outPath);
        Assert.True(File.Exists(outPath));
        // Round-trip read should not throw
        var (ds2, _) = DataSetImporter.Import(outPath);
        Assert.True(ds2.Groups.Any());
    }

    // ── Test T7: ExportDataSet for mat writes mat file ────────────────────────

    [Fact]
    public void T7_ExportDataSet_WritesMatFile()
    {
        MakeRunNpy("Amp");

        var vm = new DataExporterViewModel(_tmpDir);
        vm.ExportMode = ExportMode.Mat;

        string outPath = Path.Combine(_tmpDir, "output.mat");
        vm.ExportDataSet(outPath);
        Assert.True(File.Exists(outPath));
    }

    // ── Test T8: ExportDataSet tsv writes txt file ────────────────────────────

    [Fact]
    public void T8_ExportDataSet_WritesTsvFile()
    {
        MakeRunNpy("Amp");

        var vm = new DataExporterViewModel(_tmpDir);
        vm.ExportMode = ExportMode.Tsv;

        string outPath = Path.Combine(_tmpDir, "output.txt");
        vm.ExportDataSet(outPath);
        Assert.True(File.Exists(outPath));
        string content = File.ReadAllText(outPath);
        Assert.True(content.Length > 0);
    }

    // ── Test T9: Touchstone export with S cube writes snp file ───────────────

    [Fact]
    public void T9_ExportTouchstone_WritesSnpFile()
    {
        var ds = MakeSpDataSet();
        MakeRunNpy("Amp", ds);

        var vm = new DataExporterViewModel(_tmpDir);
        vm.ExportMode = ExportMode.Touchstone;

        // Should have SP1 in IncludeRows (has S cube)
        Assert.True(vm.IncludeRows.Any(r => r.IsChecked),
            "Expected at least one Touchstone group to be available");

        string basePath = Path.Combine(_tmpDir, "output");
        var result = vm.ExportTouchstone(basePath);

        Assert.Equal(TouchstoneExportStatus.Ok, result.Status);
        Assert.True(result.WrittenPaths.Count > 0);
        Assert.True(File.Exists(result.WrittenPaths[0]));
    }

    // ── Test T10: SuggestedFileName matches mode extension ────────────────────

    [Fact]
    public void T10_SuggestedFileName_MatchesMode()
    {
        MakeRunNpy("MySchematic");
        var vm = new DataExporterViewModel(_tmpDir, "MySchematic");

        vm.ExportMode = ExportMode.Npy;
        Assert.EndsWith(".npy", vm.SuggestedFileName);

        vm.ExportMode = ExportMode.Mat;
        Assert.EndsWith(".mat", vm.SuggestedFileName);

        vm.ExportMode = ExportMode.Tsv;
        Assert.EndsWith(".txt", vm.SuggestedFileName);
    }

    // ── Loadpull (.spl / .lpcwave) export ──────────────────────────────────────

    // A canonical loadpull DataSet grouped under "LP1": GammaLoad over {gridPoint} +
    // FOMs over {gridPoint, pinStep} (the LoadpullRecognition signature).
    private DataSet MakeLoadpullDataSet()
    {
        const int ng = 3, np = 2;
        var grid = new Axis("gridPoint", new[] { 0.0, 1.0, 2.0 });
        var pin  = new Axis("pinStep",   new[] { -5.0, 0.0 });

        var gamma = new Complex[ng];
        for (int gi = 0; gi < ng; gi++)
            gamma[gi] = Complex.FromPolarCoordinates(0.3 + 0.1 * gi, gi);

        DataCube Fom(double b)
        {
            var buf = new double[ng * np];
            for (int gi = 0; gi < ng; gi++)
                for (int pi = 0; pi < np; pi++)
                    buf[gi * np + pi] = b + gi + pi;
            return new DataCube(new[] { grid, pin }, buf);
        }

        var ds = new DataSet();
        ds.AddToGroup("LP1", "GammaLoad",  new DataCube(new[] { grid }, gamma));
        ds.AddToGroup("LP1", "Pout_dBm",   Fom(30.0));
        ds.AddToGroup("LP1", "Gt_dB",      Fom(12.0));
        ds.AddToGroup("LP1", "Efficiency", Fom(40.0));
        ds.AddToGroup("LP1", "PAE",        Fom(35.0));
        ds.AddToGroup("LP1", "__Freq",
            new DataCube(new[] { new Axis("freq", new[] { 2.4e9 }, "Hz") }, new[] { 2.4e9 }));
        return ds;
    }

    // T11 — loadpull source enables the .spl/.lpcwave formats; a plain S-param source does not.
    [Fact]
    public void T11_IsLoadpullAvailable_OnlyForLoadpullSource()
    {
        MakeRunNpy("LP", MakeLoadpullDataSet());
        MakeRunNpy("SP", MakeSpDataSet());

        var lp = new DataExporterViewModel(_tmpDir, "LP");
        Assert.True(lp.IsLoadpullAvailable);

        var sp = new DataExporterViewModel(_tmpDir, "SP");
        Assert.False(sp.IsLoadpullAvailable);
    }

    // T12 — .spl mode lists loadpull groups (single-select) and can export.
    [Fact]
    public void T12_SplMode_ListsLoadpullGroup_CanExport()
    {
        MakeRunNpy("LP", MakeLoadpullDataSet());
        var vm = new DataExporterViewModel(_tmpDir, "LP") { ExportMode = ExportMode.Spl };

        Assert.Single(vm.IncludeRows);
        Assert.Equal("LP1", vm.IncludeRows[0].GroupName);
        Assert.NotNull(vm.SelectedLoadpullGroup);
        Assert.True(vm.CanExport);
        Assert.EndsWith(".spl", vm.SuggestedFileName);

        vm.ExportMode = ExportMode.Lpcwave;
        Assert.EndsWith(".lpcwave", vm.SuggestedFileName);
    }

    // T13 — ExportLoadpull(.spl) writes a file that reads back with matching FOMs.
    [Fact]
    public void T13_ExportSpl_RoundTrips()
    {
        MakeRunNpy("LP", MakeLoadpullDataSet());
        var vm = new DataExporterViewModel(_tmpDir, "LP") { ExportMode = ExportMode.Spl };

        string outPath = Path.Combine(_tmpDir, "LP.spl");
        vm.ExportLoadpull(outPath);
        Assert.True(File.Exists(outPath));

        var read = RfCore.Loadpull.SplReader.ReadSpl(outPath);
        var src  = MakeLoadpullDataSet();
        var a = src["LP1.Pout_dBm"].RealValues;
        var b = read["Pout_dBm"].RealValues;
        Assert.Equal(a.Length, b.Length);
        for (int i = 0; i < a.Length; i++) Assert.Equal(a[i], b[i], precision: 4);
    }

    // T14 — ExportLoadpull(.lpcwave) writes a file that reads back with matching FOMs.
    [Fact]
    public void T14_ExportLpcwave_RoundTrips()
    {
        MakeRunNpy("LP", MakeLoadpullDataSet());
        var vm = new DataExporterViewModel(_tmpDir, "LP") { ExportMode = ExportMode.Lpcwave };

        string outPath = Path.Combine(_tmpDir, "LP.lpcwave");
        vm.ExportLoadpull(outPath);
        Assert.True(File.Exists(outPath));

        var read = RfCore.Loadpull.LpcwaveReader.ReadLpcwave(outPath);
        var src  = MakeLoadpullDataSet();
        var a = src["LP1.Efficiency"].RealValues;
        var b = read["Efficiency"].RealValues;
        Assert.Equal(a.Length, b.Length);
        for (int i = 0; i < a.Length; i++) Assert.Equal(a[i], b[i], precision: 4);
    }
}

using System;
using System.IO;
using System.Linq;
using CircuitRF.Ui.DataDisplay;
using RfCore.Data;
using RfCore.Loadpull;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Loadpull UI brief 09 gate: a contour LoadpullSurface built from a GROUPED loadpull DataSet
/// (cubes under an analysis-name group, e.g. "LP1" — the simulated LP run.npy shape) is identical to
/// one built from the same cubes laid FLAT (the .spl/.lpcwave shape). "Grouping is the whole risk":
/// if the group read silently misses the cubes the surface is empty. This is the flat-vs-grouped
/// parity guard the surface construction (TraceRowViewModel.EnsureLoadpullSurface) now relies on.
/// </summary>
public class LoadpullContourGroupParityTests
{
    private static string? SplFile()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var cand = Path.Combine(dir, "testdata", "spl_test_data", "Ideal_GaN_FET_1p6_mm_1p8_GHz.spl");
            if (File.Exists(cand)) return cand;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    // Re-key every cube of the flat (default-group) DataSet under a named group — no data copy.
    private static DataSet ReGroup(DataSet flat, string group)
    {
        var grouped = new DataSet();
        foreach (var name in flat.Groups)                 // flat → only the DefaultGroup ("")
            foreach (var kvp in flat.CubesIn(name))
                grouped.AddToGroup(group, kvp.Key, kvp.Value);
        return grouped;
    }

    [Fact]
    public void GroupedSurface_MatchesFlatSurface()
    {
        var path = SplFile();
        if (path is null) return;   // fixture absent in this checkout — skip (mirrors RfCore tests)

        var flat    = SplReader.ReadSpl(path);
        var grouped = ReGroup(flat, "LP1");

        // Recognition locates the LP1 group (brief 08).
        var views = LoadpullRecognition.FindLoadpullViews(grouped);
        Assert.Equal("LP1", Assert.Single(views).Group);

        var flatSfc    = new LoadpullSurface(flat, "");
        var groupedSfc = new LoadpullSurface(grouped, "LP1");   // the group-aware path the UI uses

        // Parity 1: same frequency set + grid-point count (group read found the cubes).
        Assert.Equal(flatSfc.Frequencies.Count, groupedSfc.Frequencies.Count);
        Assert.True(groupedSfc.Frequencies.Count > 0, "grouped surface must not be empty");
        Assert.Equal(flatSfc.GridPointCount(0), groupedSfc.GridPointCount(0));

        // Parity 2: same MXP (max-power) location for Pout @ 3 dB compression.
        var constraint = ConstraintSpec.AtCompression(3.0);
        var flatMxp    = flatSfc.MaxPower(0, constraint, SurfacePlane.Gamma);
        var groupedMxp = groupedSfc.MaxPower(0, constraint, SurfacePlane.Gamma);
        Assert.NotNull(flatMxp);
        Assert.NotNull(groupedMxp);
        Assert.Equal(flatMxp!.Interpolated.Real, groupedMxp!.Interpolated.Real, precision: 9);
        Assert.Equal(flatMxp.Interpolated.Imaginary, groupedMxp.Interpolated.Imaginary, precision: 9);

        // Parity 3: same resampled Pout grid maximum (W) — no W↔dBm double-conversion crept in.
        var flatFit    = flatSfc.Fit(0, "Pout_dBm", constraint, SurfacePlane.Gamma);
        var groupedFit = groupedSfc.Fit(0, "Pout_dBm", constraint, SurfacePlane.Gamma);
        Assert.NotNull(flatFit);
        Assert.NotNull(groupedFit);
        var flatGrid    = flatSfc.Resample(flatFit!, resolution: 40);
        var groupedGrid = groupedSfc.Resample(groupedFit!, resolution: 40);
        double flatMax    = flatGrid.Values.Where(d => !double.IsNaN(d)).Max();
        double groupedMax = groupedGrid.Values.Where(d => !double.IsNaN(d)).Max();
        Assert.Equal(flatMax, groupedMax, precision: 9);
    }
}

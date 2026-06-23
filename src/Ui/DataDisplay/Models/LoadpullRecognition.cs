using System.Collections.Generic;
using RfCore.Data;

namespace CircuitRF.Ui.DataDisplay;

/// <summary>
/// Shape-based, group-aware recognizer for loadpull result data (Loadpull UI 08).
///
/// A loadpull "view" is recognized by its canonical cube signature — NOT by source kind — so a
/// simulated Loadpull <c>run.npy</c> (cubes nested under the analysis-name group, e.g. <c>LP1</c>)
/// is treated identically to an ingested flat <c>.spl</c>/<c>.lpcwave</c> file (cubes at top level).
///
/// The signature (matching <c>LoadpullEngine.BuildLoadpullDataSet</c>) requires BOTH, within one group:
///   1. a termination cube — <c>GammaLoad</c> OR <c>ZLoad</c> — over a single axis named <c>gridPoint</c>, AND
///   2. at least one FOM cube — <c>Pout</c>/<c>Gt</c>/<c>Gp</c>/<c>DE</c>/<c>PAE</c> — over axes
///      <c>{gridPoint, pinStep}</c> (in that order).
///
/// Cube/axis names are matched with the same Ordinal casing the engine emits.
/// </summary>
public static class LoadpullRecognition
{
    private const string GridPointAxis = "gridPoint";
    private const string PinStepAxis   = "pinStep";

    private static readonly string[] TerminationCubes = ["GammaLoad", "ZLoad"];
    private static readonly string[] FomCubes         = ["Pout", "Gt", "Gp", "DE", "PAE"];

    /// <summary>
    /// A loadpull-shaped view inside a source DataSet: the group holding the loadpull cubes.
    /// <see cref="Group"/> is null for the top level / DefaultGroup; otherwise the named group
    /// (e.g. <c>"LP1"</c>) — carry it forward when constructing the LoadpullSurface (brief 09).
    /// </summary>
    public readonly record struct LoadpullView(string? Group);

    /// <summary>
    /// Returns every loadpull-shaped view in <paramref name="ds"/>: the top level AND each named
    /// group carrying the canonical signature. Empty when none (HB/DC/S-param/etc.).
    /// A run.npy with LP1 + LP2 returns two views; a flat .spl returns one (Group == null).
    /// </summary>
    public static IReadOnlyList<LoadpullView> FindLoadpullViews(DataSet ds)
    {
        var views = new List<LoadpullView>();
        if (ds is null) return views;

        foreach (var group in ds.Groups)
        {
            var cubes = ds.CubesIn(group);
            if (HasTermination(cubes) && HasFom(cubes))
                views.Add(new LoadpullView(group == DataSet.DefaultGroup ? null : group));
        }
        return views;
    }

    /// <summary>Convenience: true when at least one loadpull view exists.</summary>
    public static bool IsLoadpull(DataSet ds) => FindLoadpullViews(ds).Count > 0;

    // ── Signature checks ──────────────────────────────────────────────────────

    // A termination cube (GammaLoad or ZLoad) over a single axis named gridPoint.
    private static bool HasTermination(IReadOnlyDictionary<string, DataCube> cubes)
    {
        foreach (var name in TerminationCubes)
            if (cubes.TryGetValue(name, out var c)
                && c.Rank == 1
                && c.Axes[0].Name == GridPointAxis)
                return true;
        return false;
    }

    // At least one FOM cube over axes {gridPoint, pinStep} (in that order).
    private static bool HasFom(IReadOnlyDictionary<string, DataCube> cubes)
    {
        foreach (var name in FomCubes)
            if (cubes.TryGetValue(name, out var c)
                && c.Rank == 2
                && c.Axes[0].Name == GridPointAxis
                && c.Axes[1].Name == PinStepAxis)
                return true;
        return false;
    }
}

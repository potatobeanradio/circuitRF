using System;
using System.Collections.Generic;
using RfCore.Data;
using RfCore.Loadpull;

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
    private static readonly string[] FomCubes         = ["Pout_dBm", "Gt_dB", "Gp_dB", "Efficiency", "PAE"];

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

    // A termination cube (GammaLoad or ZLoad) whose trailing axis is gridPoint, optionally preceded by a
    // single leading sweep axis of ANY name — a built-in frequency sweep ("freq") OR a parametric sweep
    // wrapping the loadpull/pursuit over any variable (e.g. "RFfreq", "Vds"). Recognition keys on the
    // trailing signature, NOT the leading axis name; LoadpullSurface.BuildFreqSlices slices that leading
    // axis by position, so any name works.
    private static bool HasTermination(IReadOnlyDictionary<string, DataCube> cubes)
    {
        foreach (var name in TerminationCubes)
            if (cubes.TryGetValue(name, out var c)
                && (c.Rank == 1 || c.Rank == 2)
                && c.Axes[^1].Name == GridPointAxis)
                return true;
        return false;
    }

    // At least one FOM cube over trailing axes {gridPoint, pinStep}, optionally preceded by a single
    // leading sweep axis of any name (see HasTermination — covers parametric-swept loadpull/pursuit).
    private static bool HasFom(IReadOnlyDictionary<string, DataCube> cubes)
    {
        foreach (var name in FomCubes)
            if (cubes.TryGetValue(name, out var c)
                && (c.Rank == 2 || c.Rank == 3)
                && c.Axes[^2].Name == GridPointAxis
                && c.Axes[^1].Name == PinStepAxis)
                return true;
        return false;
    }

    // ── §4a (brief-dd-loadpull-contour-ux-round8): Γ-grid vs impedance-grid detector ──────────

    /// <summary>
    /// <c>GammaLoad</c> and <c>ZLoad</c> are BOTH always emitted for every loadpull (anchor 6 —
    /// <c>LoadpullEngine.BuildLoadpullDataSet</c>), so which one the run was actually authored/swept
    /// in cannot be told from cube presence. This is a HEURISTIC, not a fact of the data: a grid
    /// swept in the Γ-plane clusters near the unit circle (high VSWR); one swept in the impedance
    /// plane is typically a modest, bounded Z sweep (low VSWR). The owner picked this threshold by
    /// eye against two real fixtures (§4a fixture measurements, brief-dd-loadpull-contour-ux-round8):
    /// a Γ-grid run's max VSWR measured far above it, an impedance-grid run's measured far below —
    /// see <c>RESOLVED.md</c> for the recorded numbers. A grid that happens to straddle it would be
    /// misclassified; nothing here detects that case.
    /// </summary>
    public const double GammaGridVswrThreshold = 15.0;

    /// <summary>
    /// Classifies a recognized loadpull view's termination grid as authored in the Γ-plane or the
    /// impedance plane, from the GEOMETRY of its <c>GammaLoad</c> points (see
    /// <see cref="GammaGridVswrThreshold"/>) — never from which cube happens to exist, since both
    /// always do. Returns <see cref="SurfacePlane.Z"/> (impedance) when the cube is missing or
    /// carries no finite points, so an unrecognizable grid degrades to the more conservative Rect
    /// rendering rather than an arbitrary Smith chart.
    /// </summary>
    public static SurfacePlane DetectGridPlane(DataSet ds, LoadpullView view)
    {
        string spec = view.Group is null ? "GammaLoad" : $"{view.Group}.GammaLoad";
        if (ds is null || !ds.Contains(spec)) return SurfacePlane.Z;

        double maxVswr = 0.0;
        foreach (var gamma in ds[spec].ComplexValues)
        {
            double mag = gamma.Magnitude;
            if (!double.IsFinite(mag)) continue;          // skip a non-finite point rather than let it decide
            double clamped = Math.Min(mag, 0.999999);     // guard |Γ|→1, which would otherwise give VSWR=∞
            double vswr    = (1.0 + clamped) / (1.0 - clamped);
            if (vswr > maxVswr) maxVswr = vswr;
        }
        return maxVswr > GammaGridVswrThreshold ? SurfacePlane.Gamma : SurfacePlane.Z;
    }
}

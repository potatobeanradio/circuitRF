// Framework-free. No Avalonia, no SkiaSharp.
//
// brief-sdl-8 D7 / R-sdl8-5 — the mesh held for every evaluation of an EM cell solved through its 3D view. A 3D mesh
// changes with the geometry, and that change is noise between nearby points that an optimiser reads as a slope. What
// can be held is the mesh's RULE: the same sizes and the same number of refinement passes at every point.
//
// Per solver (what each setup offers and how each is fixed):
//   Palace  — the element sizes (MaxElementWavelengths, EdgeRefinement, Grading, ElementOrder) are the setup's at every
//             point already. Its adaptive refinement stops at AdaptiveTol or after AdaptiveMaxIterations passes; the
//             tolerance is made unreachable (Palace's schema needs it positive), so every point makes exactly the setup's
//             passes.
//   EMerge  — the sizes likewise; its adaptive refinement stops once a run of passes has converged, so the run is made
//             longer than the passes there are (HoldMeshPasses → min_refined_passes), and every pass is made.
//   openEMS — no adaptive refinement: the grid follows the geometry's edges by construction. Its sizes are the setup's,
//             except the merge distance (MinCellUm), which by default is a tenth of the smallest metal feature and so
//             moves with the geometry; it is held at the value the design's own (saved) values give.
//   Every solver — a mesh region's element size and grading, which a VAR may drive, are held at the values the design's
//             own values give.
//
// "THE START" IS THE DESIGN AS SAVED, NOT THE RUN'S FIRST POINT. Holding at whatever point a run happened to begin would
// make the cache key of every geometry depend on that run, so a sweep and a later optimisation of the same cell could not
// share one result. The passes are the setup's maximum for the same reason: the number the first solve happened to need is
// known only after it, and a key that depended on it could not be computed before any solve (src/Design/RESOLVED.md).

using System.Globalization;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.Layout.Em.ParametricEm;

public static class EmMeshHold
{
    /// <summary>Palace's refinement tolerance when the passes are held: positive, as its schema requires, and never reached.</summary>
    public const double HeldPalaceTolerance = 1e-30;

    /// <summary>
    /// The setup every evaluation of a 3D EM cell solves with: <paramref name="setup"/> with each solver's refinement held
    /// at a fixed pass count and, for openEMS with no merge distance stated, the merge distance
    /// <paramref name="savedMinCellM"/> the design's own values give. <paramref name="notes"/> says what was held.
    /// </summary>
    public static EmSetup Hold(EmSetup setup, double? savedMinCellM, List<string> notes)
    {
        var s = setup.Clone();
        var held = new List<string>();
        if (s.Solver3D.Runs(Em3dSolverId.Palace))
        {
            var palace = PalaceSettings.Resolve(s.Palace);
            s.Palace = (s.Palace?.Clone() ?? new CemPalace());
            if (palace.AdaptiveMaxIterations > 0)
            {
                s.Palace.AdaptiveTol = HeldPalaceTolerance;
                s.Palace.AdaptiveMaxIterations = palace.AdaptiveMaxIterations;
                held.Add($"Palace makes all {palace.AdaptiveMaxIterations} of its refinement passes at every point");
            }
            else held.Add("Palace solves every point on its initial mesh (no refinement passes)");
        }
        if (s.Solver3D.Runs(Em3dSolverId.Emerge))
        {
            var emerge = EmergeSettings.Resolve(s.Emerge);
            s.Emerge = s.Emerge?.Clone() ?? new CemEmerge();
            if (emerge.AdaptiveMesh > 0)
            {
                s.Emerge.HoldMeshPasses = true;
                held.Add($"EMerge makes all {emerge.AdaptiveMesh} of its refinement passes at every point");
            }
            else held.Add("EMerge solves every point on its initial mesh (no refinement passes)");
        }
        if (s.Solver3D.Runs(Em3dSolverId.OpenEms))
        {
            if (s.OpenEms?.MinCellUm is null && savedMinCellM is { } m && m > 0)
            {
                s.OpenEms = s.OpenEms?.Clone() ?? new CemOpenEms();
                s.OpenEms.MinCellUm = Math.Round(m * 1e6, 6);
                held.Add($"openEMS merges grid lines closer than {s.OpenEms.MinCellUm.Value.ToString("0.######", CultureInfo.InvariantCulture)} µm " +
                         "at every point (the distance the design's own values give)");
            }
            else held.Add("openEMS grids every point by the setup's own sizes");
        }
        notes.Add("The mesh is held for every evaluation of this cell: " + string.Join("; ", held) +
                  ", so nearby points differ by their geometry and not by how they were meshed.");
        return s;
    }

    /// <summary>
    /// Each mesh region of <paramref name="point"/> takes the element size and grading <paramref name="saved"/> (the same
    /// document resolved at the design's own values) gives it; the region's box still follows the point. Returns the regions
    /// whose size an expression had moved.
    /// </summary>
    public static IReadOnlyList<string> HoldRegions(C3dDocument point, C3dDocument saved)
    {
        var moved = new List<string>();
        foreach (var r in point.MeshRegions)
        {
            if (saved.MeshRegions.FirstOrDefault(x => x.Name == r.Name) is not { } at) continue;
            if (r.SizeUm != at.SizeUm || r.Grading != at.Grading) moved.Add(r.Name);
            r.SizeUm = at.SizeUm;
            r.Grading = at.Grading;
            r.Exprs?.Remove(nameof(C3dMeshRegion.SizeUm));
            r.Exprs?.Remove(nameof(C3dMeshRegion.Grading));
        }
        return moved;
    }

    /// <summary>
    /// The smallest element the held mesh makes in <paramref name="problem"/>, metres (D7's step floor): the element at
    /// conductors for Palace and EMerge (their edge refinement of the smallest per-material element), openEMS's merge
    /// distance, and every mesh region's size — the least of those the setup's solvers use. Null when none can be said.
    /// </summary>
    public static double? SmallestElementM(EmSetup setup, Em3dProblem problem)
    {
        var sizes = new List<double>();
        try
        {
            var byName = problem.Materials.ToDictionary(m => m.Name, StringComparer.Ordinal);
            double f = GmshGeoWriter.SizingFrequencyHz(problem);
            string fill = problem.Boundary.Material ?? GmshGeoWriter.BackgroundMaterial(problem.Solids, problem.Materials);
            var background = byName.TryGetValue(fill, out var bg) ? bg : new Em3dMaterial("(free space)", 1, null, 0, 1, 0);
            double EdgeOf(PalaceSettings ps)
            {
                var all = problem.Solids.Where(x => x.Role != Em3dRole.Conductor && byName.ContainsKey(x.Material))
                                 .Select(x => GmshGeoWriter.MaxElementSizeM(byName[x.Material], f, ps))
                                 .Append(GmshGeoWriter.MaxElementSizeM(background, f, ps));
                return ps.EdgeRefinement * all.Min();
            }
            if (!problem.IsStatic && setup.Solver3D.Runs(Em3dSolverId.Palace)) sizes.Add(EdgeOf(PalaceSettings.Resolve(setup.Palace)));
            if (!problem.IsStatic && setup.Solver3D.Runs(Em3dSolverId.Emerge))
            {
                var em = EmergeSettings.Resolve(setup.Emerge);
                sizes.Add(EdgeOf(PalaceSettings.Default with { MaxElementWavelengths = em.MaxElementWavelengths, EdgeRefinement = em.EdgeRefinement }));
            }
            if (setup.Solver3D.Runs(Em3dSolverId.OpenEms) && setup.OpenEms?.MinCellUm is { } um) sizes.Add(um * 1e-6);
            sizes.AddRange(problem.MeshRegions.Select(r => r.SizeM));
        }
        catch (Exception e) when (e is not OperationCanceledException) { }
        var positive = sizes.Where(x => x > 0 && double.IsFinite(x)).ToList();
        return positive.Count == 0 ? null : positive.Min();
    }
}

// brief-convex-decomposition.md — M0: CLASSIFY THE CELLS THE CONFORMAL MESHER REFUSES.
//
// §2 is a measurement and it decides the phase. The refusal at SurfaceMesher's convexity test is a
// SUFFICIENT condition being used as a necessary one; what the strip construction in RooftopSupport
// actually needs is FLOW-SIMPLICITY, per direction. This reports, for every refused cell:
// x-simplicity, y-simplicity, its reflex-vertex count and its area as a fraction of its grid
// rectangle — for the three shipping PCells on both starters at three densities, with the 96-point
// disc as the zero-fallback control.
//
// It lives here rather than in Engine.Tests because MBendPCell / MTaperPCell / MKlopfPCell are in
// src/Ui and the reference graph is Ui -> Engine. Nothing here solves anything: the whole file is
// meshing, which is milliseconds even at cells/λ = 320.

using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Em;
using CircuitRF.Ui.Layout.PCells;

namespace CircuitRF.Ui.Tests.Em;

public class ConvexDecompositionM0Tests
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;

    private static StackupLayer LowestSignalMetal(Technology tech) =>
        tech.Stackup.Layers.Last(l => l.Kind == StackupKind.Conductor && !l.IsGroundReference
                                                                      && l.DrawingLayers.Count > 0);

    private static IEnumerable<(string Label, IReadOnlyList<LayoutShape> Shapes)> ShippingParts(
        Technology tech, PCellLayerSelection sel, double s)
    {
        IReadOnlyList<LayoutShape> Gen(string id, params (string, double)[] ps)
        {
            Assert.True(PCellRegistry.TryGet(id, out var gen));
            var map = new Dictionary<string, PCellValue>(StringComparer.Ordinal);
            foreach (var (n, v) in ps) map[n] = PCellValue.Real(v);
            return gen(map, tech, sel).Shapes;
        }

        yield return ("MBend mitred",   Gen("MBEND",  ("W", 2.9e-3 * s), ("Angle", 90), ("Miter", 2)));
        yield return ("MTaper",         Gen("MTAPER", ("W1", 2.9e-3 * s), ("W2", 1.0e-3 * s), ("L", 10e-3 * s)));
        yield return ("MKlopf on-axis", Gen("MKLOPF", ("Z1", 50), ("Z2", 100), ("GammaMax", 0.05),
                                                      ("L", 20e-3 * s), ("Offset", 0.0), ("SmoothSteps", 1)));
        yield return ("MKlopf Offset",  Gen("MKLOPF", ("Z1", 50), ("Z2", 100), ("GammaMax", 0.05),
                                                      ("L", 20e-3 * s), ("Offset", 5e-3 * s), ("SmoothSteps", 1)));
    }

    private static PlanarMeshSettings At(int cpw) =>
        new(Auto: false, CellsPerWavelength: cpw, EdgeMesh: true, EdgeCells: 3,
            BoundaryCells: PlanarBoundaryCells.Conformal);

    /// <summary>
    /// <b>§7 gate 1 — M0's table, and it is the deliverable even if the answer stops the phase.</b>
    ///
    /// <para>It is taken over the cells the OLD convexity predicate refused — which after M1 are the
    /// cells the mesher ADMITS, so the instrument collects them rather than the refusal site doing
    /// it. Same set, same four quantities; the table stays re-takeable instead of going quiet the
    /// moment the refusal it measured was removed.</para>
    /// </summary>
    [Fact]
    public void M0_TheNonConvexCells_ClassifiedByFlowSimplicity()
    {
        Console.WriteLine("[M0] non-convex cut cells, classified. xy/x-/-y/-- = flow-simple in both / " +
                          "x only / y only / neither.");
        Console.WriteLine("  technology            part              cells/λ  nonconvex    xy   x-   -y   --   " +
                          "reflex(min/med/max)   area/rect(min/med/max)");

        int total = 0, both = 0, neither = 0, staircased = 0;

        foreach (var tech in new[] { StarterTechnologies.Pcb2Layer(), StarterTechnologies.MmicGaAs() })
        {
            bool mmic = tech.Name.Contains("MMIC", StringComparison.OrdinalIgnoreCase);
            double scale = mmic ? 1.0 / 40.0 : 1.0;
            var settings = mmic
                ? new EmExtractionSettings(SignalStackupLayerName: LowestSignalMetal(tech).Name)
                : null;
            var layerSel = mmic
                ? new PCellLayerSelection(LowestSignalMetal(tech).Name, null)
                : PCellLayerSelection.Default;

            foreach (var (label, shapes) in ShippingParts(tech, layerSel, scale))
            {
                var x = PlanarExtractor.Extract(shapes, tech, Dbu, 10e9, settings);
                if (!x.Ok) continue;

                foreach (int cpw in new[] { 20, 80, 320 })
                {
                    var diag = new ConformalDiagnostics();
                    SurfaceMesher.Mesh(x.Problem!, At(cpw), diagnostics: diag);
                    var f = diag.AdmittedNonConvex;

                    int bx = f.Count(c => c.XSimple && c.YSimple);
                    int xo = f.Count(c => c.XSimple && !c.YSimple);
                    int yo = f.Count(c => !c.XSimple && c.YSimple);
                    int no = f.Count(c => !c.XSimple && !c.YSimple);

                    total += f.Count; both += bx; neither += no;
                    staircased += diag.Fallbacks.Count;

                    Console.WriteLine(
                        $"  {tech.Name,-20}  {label,-16}  {cpw,6}   {f.Count,7}  {bx,4} {xo,4} {yo,4} {no,4}   " +
                        $"{Spread(f.Select(c => (double)c.ReflexVertices)),-21} " +
                        $"{Spread(f.Select(c => c.AreaFraction))}");
                }
            }
        }

        Console.WriteLine($"[M0] {total} non-convex cut cells over the whole table: {both} flow-simple in " +
                          $"BOTH directions, {total - both - neither} in exactly one, {neither} in " +
                          $"neither. {staircased} cell(s) were staircased.");

        // NON-VACUITY: the table has to have something in it, or nothing below it means anything.
        Assert.True(total > 0, "no non-convex cell was admitted anywhere in the table, so M0 measured " +
                               "nothing and M1's predicate swap cannot be the reason MKlopf now tiles");

        // §2's prediction, recorded so it can be wrong. MEASURED: 1,152 of 1,158 in both directions,
        // 6 in exactly one, 0 in neither — outcome 1, so Route A alone, and Route B is not needed.
        Assert.True(both > 0.5 * total,
            $"§2's prediction is refuted: only {both} of {total} non-convex cells are flow-simple in " +
            "both directions. Route A is not sufficient — stop and re-scope rather than pressing on.");

        // The one that decides the phase: a cell describable in NEITHER direction genuinely cannot be
        // carried by strips and is the only case Route B would exist for. There are none.
        Assert.Equal(0, neither);
        Assert.Equal(0, staircased);
    }

    /// <summary>
    /// <b>The CONTROL, and §2 says a failure here outranks the rest of the brief.</b> A 96-point
    /// disc's rim is convex everywhere, so no cell of it can straddle a reflex vertex at any density.
    /// </summary>
    [Fact]
    public void M0_TheDisc_RefusesNothingAtAnyDensity()
    {
        var tech = StarterTechnologies.Pcb2Layer();
        var x = PlanarExtractor.Extract([Disc(96, 1.45e-3)], tech, Dbu, 10e9);
        Assert.True(x.Ok, x.Refusal);

        foreach (int cpw in new[] { 20, 80, 320 })
        {
            var diag = new ConformalDiagnostics();
            var rep = SurfaceMesher.Mesh(x.Problem!, At(cpw), diagnostics: diag);
            Console.WriteLine($"[M0-disc] cells/λ {cpw,4}: N {rep.UnknownCount,6}  cut {rep.CutCellCount,5}  " +
                              $"refused {diag.Fallbacks.Count}");
            Assert.True(rep.CutCellCount > 0, "the disc produced no cut cells, so the control is vacuous");
            Assert.Empty(diag.Fallbacks);
        }
    }

    /// <summary>
    /// The instrument must not move the mesh — the standing rule for every diagnostic in this area
    /// (PlanarFillDiagnostics, L9's Tier 1).
    /// </summary>
    [Fact]
    public void M0_AttachingTheInstrument_LeavesTheMeshBitIdentical()
    {
        var tech = StarterTechnologies.Pcb2Layer();
        var shapes = ShippingParts(tech, PCellLayerSelection.Default, 1.0)
                     .First(p => p.Label == "MKlopf on-axis").Shapes;
        var x = PlanarExtractor.Extract(shapes, tech, Dbu, 10e9);
        Assert.True(x.Ok, x.Refusal);

        var without = SurfaceMesher.Mesh(x.Problem!, At(20));
        var with    = SurfaceMesher.Mesh(x.Problem!, At(20), diagnostics: new ConformalDiagnostics());

        Assert.Equal(without.UnknownCount,      with.UnknownCount);
        Assert.Equal(without.Mesh.Cells.Count,  with.Mesh.Cells.Count);
        Assert.Equal(without.CutCellCount,      with.CutCellCount);
        Assert.Equal(without.MeshedAreaM2,      with.MeshedAreaM2);
        for (int i = 0; i < without.Mesh.Cells.Count; i++)
            Assert.Equal(without.Mesh.Cells[i].Area, with.Mesh.Cells[i].Area);
    }

    private static string Spread(IEnumerable<double> xs)
    {
        var a = xs.Order().ToArray();
        return a.Length == 0 ? "—" : $"{a[0]:F3}/{a[a.Length / 2]:F3}/{a[^1]:F3}";
    }

    /// <summary>A regular polygon with its vertices ON the axes — a half-step offset would make the
    /// four edges straddling each axis exactly axis-parallel, and the mesher would adopt them as
    /// gridlines (the fixture trap recorded in the edge-mesh note).</summary>
    private static PolygonShape Disc(int n, double rM)
    {
        var xy = new List<long>(2 * n);
        for (int i = 0; i < n; i++)
        {
            double a = 2 * Math.PI * i / n;
            xy.Add((long)Math.Round(rM * Math.Cos(a) * 1e6 * Dbu));
            xy.Add((long)Math.Round(rM * Math.Sin(a) * 1e6 * Dbu));
        }
        return new PolygonShape { Layer = new(1, 0), Xy = [.. xy] };
    }
}

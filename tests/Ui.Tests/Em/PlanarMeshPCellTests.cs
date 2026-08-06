// L8b Tier 6 + the PCell half of Tier 7 — staircasing measured on REAL library geometry.
//
// D2's decision is that diagonals and curves are STAIRCASED in L8b, and that the error is measured on
// the shipping PCells rather than on a synthetic 45° square — because the shipping PCells are what a
// user will actually select, and because **L8's own phase gate is not all-Manhattan**: it reads "a
// quarter-wave open stub resonates at the right frequency; A BEND'S S-PARAMETERS ARE PHYSICALLY SANE;
// A and B agree on a uniform line." MBendPCell cuts a 45° mitre, and R-pc-18 records that mitred and
// unmitred are DISTINCT discontinuities — which is the entire reason a bend is interesting to a
// full-wave kernel. A staircased mitre is an unmitred bend with a rough corner, which is the one thing
// the gate is asking the kernel to tell apart.
//
// These tests live in Ui.Tests rather than Engine.Tests for a structural reason, not a stylistic one:
// MBendPCell/MKlopfPCell/MTaperPCell are in src/Ui and the reference graph is Ui -> Engine, so an
// Engine test cannot reach them.

using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Em;
using CircuitRF.Ui.Layout.PCells;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Tests.Em;

public class PlanarMeshPCellTests
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;
    private const double Fghz10 = 10e9;

    // ── Harness ───────────────────────────────────────────────────────────────────────────────

    private static IReadOnlyList<LayoutShape> Generate(
        string generatorId, Technology tech, params (string Name, double Value)[] parameters)
    {
        Assert.True(PCellRegistry.TryGet(generatorId, out var gen), $"no generator '{generatorId}'");
        var map = new Dictionary<string, PCellValue>(StringComparer.Ordinal);
        foreach (var (n, v) in parameters) map[n] = PCellValue.Real(v);
        return gen(map, tech, PCellLayerSelection.Default).Shapes;
    }

    private static (PlanarProblem Problem, PlanarMeshReport Report) MeshOf(
        IReadOnlyList<LayoutShape> shapes, Technology tech, double fHz, PlanarMeshSettings? settings = null)
    {
        var x = PlanarExtractor.Extract(shapes, tech, Dbu, fHz);
        Assert.True(x.Ok, x.Refusal);
        return (x.Problem!, SurfaceMesher.Mesh(x.Problem!, settings));
    }

    private static double MeshArea(PlanarMeshReport r)
    {
        double a = 0;
        foreach (var c in r.Mesh.Cells) a += c.Area;
        return a;
    }

    private static double TrueArea(PlanarProblem p)
    {
        double a = 0;
        foreach (var l in p.Layers) foreach (var poly in l.Polygons) a += poly.Area();
        return a;
    }

    /// <summary>The polygon's own y-extent at a given x — an independent even–odd ray cast, not the
    /// mesher's, so the width comparison below is against the geometry rather than against the thing
    /// under test.</summary>
    private static double TrueHeightAt(PlanarPolygon poly, double x)
    {
        var ys = new List<double>();
        void Cast(IReadOnlyList<EmPoint> ring)
        {
            int n = ring.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                double a = ring[j].X, b = ring[i].X;
                if (a > x == b > x) continue;
                double t = (x - a) / (b - a);
                ys.Add(ring[j].Y + t * (ring[i].Y - ring[j].Y));
            }
        }
        Cast(poly.Outer);
        foreach (var h in poly.HoleRings) Cast(h);
        ys.Sort();
        double sum = 0;
        for (int i = 0; i + 1 < ys.Count; i += 2) sum += ys[i + 1] - ys[i];
        return sum;
    }

    /// <summary>Meshed height of one grid column — the sum of the cell heights sharing an IX.</summary>
    private static double MeshedHeightOfColumn(PlanarMeshReport r, int ix)
    {
        double sum = 0;
        foreach (var c in r.Mesh.Cells) if (c.IX == ix) sum += c.Height;
        return sum;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Tier 6a — the mitred MBend. FIRST, because it is on L8's own phase gate.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void T6_1_TheMitredBend_IsDISTINGUISHABLEFromTheUnmitredOne_AfterStaircasing()
    {
        // The load-bearing question, and the one the phase gate turns on: after staircasing, is the
        // mitre still THERE? If the two meshes were identical, the kernel would be structurally
        // unable to tell apart the two discontinuities R-pc-18 says are distinct.
        var tech = StarterTechnologies.Pcb2Layer();

        var square = Generate("MBEND", tech, ("W", 2.9e-3), ("Angle", 90), ("Miter", 0));
        var mitred = Generate("MBEND", tech, ("W", 2.9e-3), ("Angle", 90), ("Miter", 2));

        var (sqProblem, sq) = MeshOf(square, tech, Fghz10);
        var (miProblem, mi) = MeshOf(mitred, tech, Fghz10);

        double trueCut  = TrueArea(sqProblem) - TrueArea(miProblem);
        double meshCut  = MeshArea(sq)        - MeshArea(mi);
        double areaErr  = Math.Abs(meshCut - trueCut) / trueCut;
        int    cellsCut = sq.CellCount - mi.CellCount;

        Console.WriteLine(
            $"[L8b Tier 6] MBend mitre @10 GHz FR-4: true cut = {trueCut * 1e6:G4} mm², " +
            $"staircased cut = {meshCut * 1e6:G4} mm² ({areaErr:P1} error), " +
            $"cells removed = {cellsCut}, N square = {sq.UnknownCount}, N mitred = {mi.UnknownCount}, " +
            $"cell size = {mi.MaxCellEdgeM * 1e6:G4} µm");

        Assert.True(trueCut > 0, "the mitre removed no area — the fixture is wrong, not the mesher");
        Assert.True(cellsCut > 0,
            $"the staircased mitre removed NO cells at the mesher's own cell size ({mi.MaxCellEdgeM * 1e6:G4} µm) " +
            "— the mesh cannot represent the discontinuity L8's phase gate is about");
        Assert.NotEqual(sq.UnknownCount, mi.UnknownCount);
    }

    [Fact]
    public void T6_2_TheMitreStaircasingError_AsAFunctionOfCellSize()
    {
        // "…and as a function of cell size" — the number that says whether refining fixes it.
        var tech = StarterTechnologies.Pcb2Layer();
        var square = Generate("MBEND", tech, ("W", 2.9e-3), ("Angle", 90), ("Miter", 0));
        var mitred = Generate("MBEND", tech, ("W", 2.9e-3), ("Angle", 90), ("Miter", 2));

        foreach (int cpw in new[] { 10, 20, 40, 80 })
        {
            var s = new PlanarMeshSettings(Auto: false, CellsPerWavelength: cpw);
            var (sqP, sq) = MeshOf(square, tech, Fghz10, s);
            var (miP, mi) = MeshOf(mitred, tech, Fghz10, s);

            double trueCut = TrueArea(sqP) - TrueArea(miP);
            double meshCut = MeshArea(sq) - MeshArea(mi);
            Console.WriteLine(
                $"[L8b Tier 6] MBend mitre @{cpw} cells/λ: cell = {mi.MaxCellEdgeM * 1e6:G4} µm, " +
                $"cut error = {Math.Abs(meshCut - trueCut) / trueCut:P1}, " +
                $"cells removed = {sq.CellCount - mi.CellCount}, N = {mi.UnknownCount} " +
                $"({mi.Verdict})");
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Tier 6b — the smooth outlines: MKlopf (194 vertices) and MTaper
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void T6_3_TheSmoothTapers_LocalWidthErrorAlongTheTaper_NotOnlyAGlobalAreaError()
    {
        // A Klopfenstein profile's whole value is a controlled equiripple |Γ|, and a width error that
        // is negligible as a fraction of TOTAL AREA can still be large compared to the ripple the
        // taper was designed for. So the number reported here is the LOCAL width error.
        var tech = StarterTechnologies.Pcb2Layer();

        foreach (var (label, shapes) in new (string, IReadOnlyList<LayoutShape>)[]
        {
            ("MTaper 2.9→1.0 mm, 10 mm", Generate("MTAPER", tech, ("W1", 2.9e-3), ("W2", 1.0e-3), ("L", 10e-3))),
            ("MKlopf 50→100 Ω, 20 mm, on-axis",
                Generate("MKLOPF", tech, ("Z1", 50), ("Z2", 100), ("GammaMax", 0.05), ("L", 20e-3),
                         ("Offset", 0.0), ("SmoothSteps", 1))),
            ("MKlopf 50→100 Ω, 20 mm, Offset 5 mm",
                Generate("MKLOPF", tech, ("Z1", 50), ("Z2", 100), ("GammaMax", 0.05), ("L", 20e-3),
                         ("Offset", 5e-3), ("SmoothSteps", 1))),
        })
        {
            var (problem, r) = MeshOf(shapes, tech, Fghz10);
            var poly = problem.Layers[0].Polygons[0];

            double worst = 0, sumSq = 0;
            int n = 0;
            var gx = r.Mesh.GridX;
            for (int ix = 0; ix + 1 < gx.Count; ix++)
            {
                double xc  = 0.5 * (gx[ix] + gx[ix + 1]);
                double tru = TrueHeightAt(poly, xc);
                if (!(tru > 0)) continue;
                double err = Math.Abs(MeshedHeightOfColumn(r, ix) - tru) / tru;
                worst = Math.Max(worst, err);
                sumSq += err * err;
                n++;
            }
            double rms = n > 0 ? Math.Sqrt(sumSq / n) : 0;

            double areaErr = Math.Abs(MeshArea(r) - TrueArea(problem)) / TrueArea(problem);
            Console.WriteLine(
                $"[L8b Tier 6] {label}: local width error worst = {worst:P2}, RMS = {rms:P2}; " +
                $"global area error = {areaErr:P2}; cell = {r.MaxCellEdgeM * 1e6:G4} µm; " +
                $"cells = {r.CellCount}, N = {r.UnknownCount} ({r.Verdict})");

            Assert.True(r.StaircasedPolygons > 0, $"{label} was not detected as non-Manhattan");
            Assert.Contains(r.Notes, note => note.Contains("STAIRCASE"));
        }
    }

    [Fact]
    public void T6_4_MTaperHasNoOffsetParameter_SoTheOffsetVariantIsMKlopfs()
    {
        // Stated as a fact rather than left as an apparent omission: the brief asks for MTaper "both
        // on-axis and with the Offset variant", but MTaperPCell declares W1/W2/L only — Offset is
        // MKlopf's, and T6_3 measures it there.
        var defaults = ComponentTypeRegistry.DefaultParameters(SymbolKind.Mtaper, 2).Select(p => p.Name).ToList();
        Assert.DoesNotContain("Offset", defaults);
        Assert.Contains("Offset", ComponentTypeRegistry.DefaultParameters(SymbolKind.Mklopf, 2).Select(p => p.Name));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Tier 7 (PCell half) — N for the three non-Manhattan library PCells, on BOTH starters,
    // against R17's 5,000 ceiling. This is the number that decides D8.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void T7_4_NForTheThreeNonManhattanLibraryPCells_OnBothStarterTechnologies()
    {
        foreach (var tech in new[] { StarterTechnologies.Pcb2Layer(), StarterTechnologies.MmicGaAs() })
        {
            // On the MMIC starter the artwork must land on the LOWEST signal metal (Metal1, directly
            // on the GaAs); Metal2 sits above an explicit air layer, which D2's one-slab limit
            // correctly refuses. Scale the geometry to MMIC dimensions so the measurement is of a
            // realistic part rather than of a PCB-sized part on a die.
            bool mmic = tech.Name.Contains("MMIC", StringComparison.OrdinalIgnoreCase);
            double scale = mmic ? 1.0 / 40.0 : 1.0;
            var settings = mmic
                ? new EmExtractionSettings(SignalStackupLayerName: LowestSignalMetal(tech).Name)
                : null;
            var layerSel = mmic
                ? new PCellLayerSelection(LowestSignalMetal(tech).Name, null)
                : PCellLayerSelection.Default;

            foreach (var (label, shapes) in Parts(tech, layerSel, scale))
            {
                var x = PlanarExtractor.Extract(shapes, tech, Dbu, Fghz10, settings);
                if (!x.Ok) { Console.WriteLine($"[L8b Tier 7] {tech.Name} / {label}: REFUSED — {x.Refusal}"); continue; }

                var r = SurfaceMesher.Mesh(x.Problem!);
                Console.WriteLine(
                    $"[L8b Tier 7] {tech.Name} / {label}: cells = {r.CellCount}, N = {r.UnknownCount} " +
                    $"(ceiling {SurfaceMesher.UnknownCeiling}), verdict = {r.Verdict}, " +
                    $"cell = {r.MaxCellEdgeM * 1e6:G4} µm, across narrowest = {r.CellsAcrossNarrowestConductor}");
            }
        }
    }

    private static StackupLayer LowestSignalMetal(Technology tech) =>
        tech.Stackup.Layers.Last(l => l.Kind == StackupKind.Conductor && !l.IsGroundReference
                                                                     && l.DrawingLayers.Count > 0);

    private static IEnumerable<(string Label, IReadOnlyList<LayoutShape> Shapes)> Parts(
        Technology tech, PCellLayerSelection sel, double s)
    {
        IReadOnlyList<LayoutShape> Gen(string id, params (string, double)[] ps)
        {
            Assert.True(PCellRegistry.TryGet(id, out var gen));
            var map = new Dictionary<string, PCellValue>(StringComparer.Ordinal);
            foreach (var (n, v) in ps) map[n] = PCellValue.Real(v);
            return gen(map, tech, sel).Shapes;
        }

        yield return ("MBend mitred",        Gen("MBEND",  ("W", 2.9e-3 * s), ("Angle", 90), ("Miter", 2)));
        yield return ("MTaper 2.9→1.0 mm",   Gen("MTAPER", ("W1", 2.9e-3 * s), ("W2", 1.0e-3 * s), ("L", 10e-3 * s)));
        yield return ("MKlopf on-axis",      Gen("MKLOPF", ("Z1", 50), ("Z2", 100), ("GammaMax", 0.05),
                                                           ("L", 20e-3 * s), ("Offset", 0.0), ("SmoothSteps", 1)));
        yield return ("MKlopf Offset",       Gen("MKLOPF", ("Z1", 50), ("Z2", 100), ("GammaMax", 0.05),
                                                           ("L", 20e-3 * s), ("Offset", 5e-3 * s), ("SmoothSteps", 1)));
    }
}

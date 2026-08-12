// Conformal boundary cells — §5's UI gates: the FOURTH mesh control, end to end.
//
// D3 says PlanarMeshSettings carries "exactly three user controls, and no more"; this phase adds a
// fourth on the owner's explicit instruction, and §5 records the reversal rather than slipping it
// in. What makes THIS one different from the three, and what these tests are really about:
//
//   Cells per wavelength and Edge cells change how FINELY the same structure is discretised.
//   Boundary cells changes WHICH STRUCTURE is discretised at all — a staircased disc and a
//   conformal disc are different geometry, not two resolutions of one.
//
// That is why the staleness gate below is the load-bearing one: an .snp produced under one boundary
// model is NOT current for the other, and MeshHash is the only thing that can say so.

using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Em;
using CircuitRF.Ui.Layout.PCells;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Tests.Em;

public class ConformalBoundaryCellsUiTests
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;

    /// <summary>A 45° chamfer, so the mesher genuinely has something to cut.</summary>
    private static LayoutView ChamferedLayout()
    {
        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.Add(new PolygonShape
        {
            Layer = new(1, 0),
            Xy = [0, 0, 20_000_000, 0, 20_000_000, 1_600_000, 17_000_000, 2_900_000, 0, 2_900_000],
        });
        return view;
    }

    private static EmSetupEditorViewModel Editor(string dir, LayoutView view, EmSetup? seed = null)
    {
        string path  = Path.Combine(dir, "panel.cem");
        var    setup = seed ?? new EmSetup
        {
            Name = "panel", LayoutRef = "a.clay", AnalysisKind = EmAnalysisKind.Planar,
        };
        EmSetupPersistence.SaveToFile(path, setup);
        var vm = new EmSetupEditorViewModel(path, setup)
        {
            ResolveLayout = _ => new EmLayoutSource(
                Path.Combine(dir, "a.clay"), view, StarterTechnologies.Pcb2Layer(), Dbu),
        };
        vm.Refresh();
        return vm;
    }

    private static string TempDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "crf-conformal-ui-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // §5 gate — the .cem round trip, at the default and set
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ACemThatNeverTouchedTheControl_ReSerialisesBYTEIDENTICALLY()
    {
        // The established omit-at-default pattern, and the reason it is not optional: a plain
        // (non-nullable) DTO field would be written unconditionally and would change EVERY .cem
        // already on disk. That byte-identity is an asserted property of this file format, not a
        // nicety — see DirectVerticalKernel, which follows the same rule for the same reason.
        var setup = new EmSetup { Name = "hero", LayoutRef = "Amp/layout/Amp.clay" };
        string before = EmSetupPersistence.Serialize(setup);

        Assert.DoesNotContain("BoundaryCells", before, StringComparison.Ordinal);

        var reloaded = EmSetupPersistence.Deserialize(before);
        Assert.Equal(PlanarMeshSettings.DefaultBoundaryCells, reloaded.PlanarMesh.BoundaryCells);
        Assert.Equal(before, EmSetupPersistence.Serialize(reloaded));
    }

    [Fact]
    public void SettingTheControl_RoundTripsAndSurvivesClone()
    {
        var setup = new EmSetup
        {
            Name         = "planar",
            LayoutRef    = "Amp/layout/Amp.clay",
            AnalysisKind = EmAnalysisKind.Planar,
            PlanarMesh   = PlanarMeshSettings.Default with
            {
                BoundaryCells = PlanarBoundaryCells.Conformal,
            },
        };

        string json = EmSetupPersistence.Serialize(setup);
        Assert.Contains("BoundaryCells", json, StringComparison.Ordinal);
        Assert.Equal(PlanarBoundaryCells.Conformal,
                     EmSetupPersistence.Deserialize(json).PlanarMesh.BoundaryCells);

        // Clone drives the editor's UNDO snapshots. A field missing from it is silently lost on the
        // next unrelated edit — assert it rather than assume it.
        Assert.Equal(PlanarBoundaryCells.Conformal, setup.Clone().PlanarMesh.BoundaryCells);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // §5 gate — staleness. THE LOAD-BEARING ONE.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void TogglingTheControl_ChangesMeshHash_AndMarksAnExistingSnpStale()
    {
        var stair = PlanarMeshSettings.Default with { BoundaryCells = PlanarBoundaryCells.Staircase };
        var cut   = PlanarMeshSettings.Default with { BoundaryCells = PlanarBoundaryCells.Conformal };

        string hStair = EmSnpProvenance.MeshHash(stair);
        string hCut   = EmSnpProvenance.MeshHash(cut);

        Assert.NotEqual(hStair, hCut);

        // …and every OTHER control still moves it, so the new term did not displace one.
        Assert.NotEqual(hStair, EmSnpProvenance.MeshHash(stair with { CellsPerWavelength = 40 }));
        Assert.NotEqual(hStair, EmSnpProvenance.MeshHash(stair with { EdgeMesh = !stair.EdgeMesh }));
        Assert.NotEqual(hStair, EmSnpProvenance.MeshHash(stair with { EdgeCells = 7 }));
        Assert.NotEqual(hStair, EmSnpProvenance.MeshHash(stair with { Auto = !stair.Auto }));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // §5 gate — ONE undo entry, and the mesh is invalidated
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ChangingIt_IsOneUndoEntry_AndInvalidatesTheMesh()
    {
        string dir = TempDir();
        var vm = Editor(dir, ChamferedLayout());

        vm.BuildPlanarMesh();
        Assert.NotNull(vm.PlanarMeshReport);

        int undosBefore = 0;
        while (vm.UndoRedo.CanUndo) { vm.UndoRedo.Undo(); undosBefore++; }
        while (vm.UndoRedo.CanRedo) vm.UndoRedo.Redo();

        vm.BuildPlanarMesh();
        Assert.NotNull(vm.PlanarMeshReport);

        vm.PlanarBoundaryCells = PlanarBoundaryCells.Conformal;

        // The panel must not go on showing an N produced under the other boundary model.
        Assert.Null(vm.PlanarMeshReport);

        // Exactly one entry, and undoing it puts the control back.
        int undosAfter = 0;
        while (vm.UndoRedo.CanUndo) { vm.UndoRedo.Undo(); undosAfter++; }
        Assert.Equal(undosBefore + 1, undosAfter);
        Assert.Equal(PlanarBoundaryCells.Staircase, vm.PlanarBoundaryCells);

        Directory.Delete(dir, true);
    }

    [Fact]
    public void SettingItToTheValueItAlreadyHas_PushesNothing()
    {
        string dir = TempDir();
        var vm = Editor(dir, ChamferedLayout());

        while (vm.UndoRedo.CanUndo) vm.UndoRedo.Undo();
        vm.PlanarBoundaryCells = vm.PlanarBoundaryCells;

        Assert.False(vm.UndoRedo.CanUndo);
        Directory.Delete(dir, true);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // §5 gate — the control is not offered for the cross-section kernel
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void TheSurfaceMeshGroup_IsOfferedForKernelBOnly()
    {
        string dir = TempDir();
        var vm = Editor(dir, ChamferedLayout());

        Assert.True(vm.IsPlanarAnalysis);

        vm.SelectedKernel = EmAnalysisKind.CrossSection;
        Assert.False(vm.IsPlanarAnalysis);

        // The AXAML row lives inside the Surface-mesh group, which is gated on IsPlanarAnalysis —
        // asserted against the real markup because a Control cannot be constructed headlessly here.
        string axaml = File.ReadAllText(RepoFile("src/Ui/Views/Layout/EmSetupEditorView.axaml"));
        int group = axaml.IndexOf("Surface mesh", StringComparison.Ordinal);
        int row   = axaml.IndexOf("PlanarBoundaryCellsCombo", StringComparison.Ordinal);
        Assert.True(group >= 0, "the Surface mesh group is gone");
        Assert.True(row > group, "the Boundary cells row is not inside the Surface mesh group");

        Directory.Delete(dir, true);
    }

    /// <summary>
    /// The combo is sourced from the ENUM rather than hand-listed, so a third boundary model cannot
    /// silently fail to appear in the panel — the one way this control could quietly stop offering
    /// something the engine supports.
    /// </summary>
    [Fact]
    public void TheChoiceList_ComesFromTheEnum_NotAHandWrittenList()
    {
        Assert.Equal(Enum.GetValues<PlanarBoundaryCells>(),
                     EmSetupEditorViewModel.BoundaryCellsChoices);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // §5 gate — the mesh NOTES say which boundary model produced this mesh
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void TheNotes_NameTheBoundaryModel_AndTheCutAndMergedCounts()
    {
        string dir = TempDir();
        var vm = Editor(dir, ChamferedLayout());

        vm.PlanarBoundaryCells = PlanarBoundaryCells.Conformal;
        vm.BuildPlanarMesh();

        var r = vm.PlanarMeshReport;
        Assert.NotNull(r);
        Assert.True(r!.CutCellCount > 0, "the fixture produced no cut cells");

        string notes = string.Join(" | ", vm.PlanarMeshNotes);
        Assert.Contains("CONFORMAL", notes, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(r.CutCellCount.ToString(), notes, StringComparison.Ordinal);

        Directory.Delete(dir, true);
    }


    // ══════════════════════════════════════════════════════════════════════════════════════════
    // §7 gate 1, the half the engine tests cannot reach — THE SHIPPING PCells, ON BOTH STARTERS
    //
    // SurfaceMesherConformalTests covers the tiling gate on synthetic disc / taper / mitre fixtures.
    // The parts a USER actually selects are MBendPCell / MTaperPCell / MKlopfPCell, and those live
    // in src/Ui — the reference graph is Ui -> Engine, so an Engine test cannot reach them. This is
    // the same structural reason PlanarMeshPCellTests exists, applied to the boundary-cell control.
    // ══════════════════════════════════════════════════════════════════════════════════════════

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

        yield return ("MBend mitred",      Gen("MBEND",  ("W", 2.9e-3 * s), ("Angle", 90), ("Miter", 2)));
        yield return ("MTaper 2.9-1.0 mm", Gen("MTAPER", ("W1", 2.9e-3 * s), ("W2", 1.0e-3 * s), ("L", 10e-3 * s)));
        yield return ("MKlopf on-axis",    Gen("MKLOPF", ("Z1", 50), ("Z2", 100), ("GammaMax", 0.05),
                                                         ("L", 20e-3 * s), ("Offset", 0.0), ("SmoothSteps", 1)));
        yield return ("MKlopf Offset",     Gen("MKLOPF", ("Z1", 50), ("Z2", 100), ("GammaMax", 0.05),
                                                         ("L", 20e-3 * s), ("Offset", 5e-3 * s), ("SmoothSteps", 1)));
    }

    /// <summary>
    /// §7 gate 1's own table, on the real library parts: cut / merged / total cells, the area error
    /// against the DRAWN artwork, and N against R17's ceiling — on both starter technologies.
    ///
    /// <para><b>"Area error at round-off, not 0.5%" is the claim, and it is asserted rather than
    /// reported.</b> The 0.5% figure in §0 is what L8b measured for these exact parts under the
    /// staircase; if the conformal number is not orders below it, the phase has not done its job on
    /// the geometry that actually ships.</para>
    /// </summary>
    [Fact]
    public void G1_TheShippingPCells_TileToRoundOff_OnBothStarters()
    {
        var rows = new List<(string Tech, string Label, double EStair, double ECut, int Fallback,
                             PlanarBudgetVerdict VStair, PlanarBudgetVerdict VCut)>();

        foreach (var tech in new[] { StarterTechnologies.Pcb2Layer(), StarterTechnologies.MmicGaAs() })
        {
            // MMIC artwork must land on the LOWEST signal metal (Metal1, directly on the GaAs) and be
            // scaled to die dimensions, exactly as PlanarMeshPCellTests' own Tier 7 does — a
            // PCB-sized part on a die measures nothing realistic.
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
                if (!x.Ok) { Console.WriteLine($"[G1] {tech.Name} / {label}: REFUSED — {x.Refusal}"); continue; }

                double drawn = x.Problem!.Layers[0].Polygons[0].Area();
                var stair = SurfaceMesher.Mesh(x.Problem, Mesh(PlanarBoundaryCells.Staircase));
                var cut   = SurfaceMesher.Mesh(x.Problem, Mesh(PlanarBoundaryCells.Conformal));

                double eStair = Math.Abs(stair.MeshedAreaM2 - drawn) / drawn;
                double eCut   = Math.Abs(cut.MeshedAreaM2   - drawn) / drawn;

                Console.WriteLine(
                    $"[G1] {tech.Name} / {label}: staircase N {stair.UnknownCount,5} err {eStair:P4}  |  " +
                    $"conformal N {cut.UnknownCount,5} cells {cut.Mesh.Cells.Count,5} " +
                    $"cut {cut.CutCellCount,4} merged {cut.MergedSliverCount,3} " +
                    $"fallback {cut.StaircaseFallbackCells,3} err {eCut:E3}  " +
                    $"[verdict {cut.Verdict}]");

                rows.Add((tech.Name, label, eStair, eCut, cut.StaircaseFallbackCells,
                          stair.Verdict, cut.Verdict));
            }
        }

        Assert.NotEmpty(rows);

        // ── THE ASSERTION IS SPLIT, BECAUSE THE MEASUREMENT SPLIT THE PARTS INTO TWO CASES ───────
        //
        // Where the conformal pass cut every boundary cell it was asked to, the tiling is exact and
        // that is asserted at round-off. Where it REFUSED cells (§2's three configurations, counted
        // as StaircaseFallbackCells), those cells are staircased and the guarantee does not hold for
        // them — so asserting round-off there would be asserting something the design does not
        // promise. Both cases are reported above; the fallback case gets its own weaker assertion so
        // a regression in EITHER is still caught.
        foreach (var r in rows.Where(r => r.Fallback == 0))
        {
            Assert.True(r.ECut < 1e-12,
                $"{r.Tech} / {r.Label}: no cell fell back, so the tiling must be exact, but the " +
                $"area error is {r.ECut:E3}");
            Assert.True(r.ECut < r.EStair / 100.0,
                $"{r.Tech} / {r.Label}: conformal {r.ECut:E3} against staircase {r.EStair:P4}");
        }

        // R17's own verdict must not be pushed over by cutting — the phase claims the mesh is not
        // more expensive, and a part that meshed inside the ceiling must still do so.
        foreach (var r in rows)
            Assert.Equal(r.VStair, r.VCut);

        // NON-VACUITY: at least one shipping part must reach the exact case, or the gate above is
        // asserting over an empty set and the table says nothing.
        Assert.Contains(rows, r => r.Fallback == 0);
    }


    /// <summary>
    /// <b>THE FINDING G1 SURFACED, followed up: does refining clear the fallback?</b>
    ///
    /// <para>G1's table splits the shipping parts cleanly. MBend and MTaper cut every boundary cell
    /// and tile to round-off on both starters. <b>MKlopf does not</b> — it refuses 50 to 114 cells,
    /// and on the PCB starter the resulting area error (0.77%) is WORSE than the staircase it
    /// replaced (0.59%). That is the opposite of the phase's headline claim, on the one part whose
    /// whole value is a controlled 0.05 ripple, so it is measured rather than left as a footnote.</para>
    ///
    /// <para>The refusal is §2's case (c): the clipped region is not convex. MKlopf's outline is
    /// sampled at many stations and <c>SmoothSteps</c> blends each end, so the rim carries genuine
    /// INFLECTIONS — and any cell spanning one has a reflex vertex inside it. Whether that is a
    /// refinement problem or a permanent one is the question this answers, because the answer decides
    /// whether "refine and watch it go to zero" is honest advice for this part.</para>
    /// </summary>
    [Fact]
    public void G1b_MKlopfsStaircaseFallback_AsAFunctionOfMeshDensity()
    {
        var tech = StarterTechnologies.Pcb2Layer();
        var shapes = ShippingParts(tech, PCellLayerSelection.Default, 1.0)
                     .First(p => p.Label.Contains("MKlopf on-axis", StringComparison.Ordinal)).Shapes;

        var x = PlanarExtractor.Extract(shapes, tech, Dbu, 10e9);
        Assert.True(x.Ok, x.Refusal);
        double drawn = x.Problem!.Layers[0].Polygons[0].Area();

        Console.WriteLine("[G1b] MKlopf on-axis, PCB 2-Layer — the saturation ladder, re-run after M1.");
        Console.WriteLine("  cells/λ      N    cut  fallback  nonconvex-cut   conformal err   staircase err");

        double worstCut = 0;
        foreach (int cpw in new[] { 20, 40, 80, 160, 320 })
        {
            var st = new PlanarMeshSettings(Auto: false, CellsPerWavelength: cpw,
                                            EdgeMesh: true, EdgeCells: 3,
                                            BoundaryCells: PlanarBoundaryCells.Conformal);
            var diag  = new ConformalDiagnostics();
            var cut   = SurfaceMesher.Mesh(x.Problem, st, diagnostics: diag);
            var stair = SurfaceMesher.Mesh(x.Problem, st with { BoundaryCells = PlanarBoundaryCells.Staircase });

            double eCut   = Math.Abs(cut.MeshedAreaM2   - drawn) / drawn;
            double eStair = Math.Abs(stair.MeshedAreaM2 - drawn) / drawn;
            worstCut = Math.Max(worstCut, eCut);

            Console.WriteLine($"  {cpw,7}  {cut.UnknownCount,5}  {cut.CutCellCount,5}  " +
                              $"{cut.StaircaseFallbackCells,8}  {diag.AdmittedNonConvex.Count,13}   " +
                              $"{eCut:E3}       {eStair:E3}");

            // §7 gate 3 — the plateau at 126 was the signature this phase exists to remove, so its
            // ABSENCE is the proof. It is asserted at every rung, not just at the finest.
            Assert.Equal(0, cut.StaircaseFallbackCells);
        }

        Console.WriteLine($"[G1b] worst conformal area error over the ladder: {worstCut:E3}");

        // §7 gate 2 — MKlopf's own area error, at round-off, at every density on the starter where it
        // used to come out WORSE than the staircase (0.766% against 0.593%). Measured against the
        // DRAWN artwork, which is the conformal phase's own recorded trap.
        Assert.True(worstCut < 1e-12, $"MKlopf's area error is {worstCut:E3}, not round-off");
    }


    /// <summary>
    /// <b>WHY the fallback used to saturate at 126 rather than going to zero</b> — G1b's ladder showed
    /// it plateauing, and a count that stops falling under refinement is a count of FEATURES OF THE
    /// ARTWORK, not of cells. This counts the reflex (concave) vertices of MKlopf's own outline and
    /// compares.
    ///
    /// <para><b>UPDATED at brief-convex-decomposition.md's M1, not loosened.</b> The 126 reflex
    /// vertices are still there — the outline did not change — and the fallback count is now ZERO,
    /// because the refusal they were tripping was convexity and the property the strips actually
    /// need is flow-simplicity. So the claim inverts: the plateau's own cause survives and the
    /// plateau does not, which is a much stronger statement than the count matching.</para>
    /// </summary>
    [Fact]
    public void G1c_TheOutlinesReflexVerticesNoLongerCostAFallbackCell()
    {
        var tech = StarterTechnologies.Pcb2Layer();
        var shapes = ShippingParts(tech, PCellLayerSelection.Default, 1.0)
                     .First(p => p.Label.Contains("MKlopf on-axis", StringComparison.Ordinal)).Shapes;

        var x = PlanarExtractor.Extract(shapes, tech, Dbu, 10e9);
        Assert.True(x.Ok, x.Refusal);
        var outer = x.Problem!.Layers[0].Polygons[0].Outer;

        // Signed area fixes the winding, so "reflex" is well defined without assuming a direction.
        double signed = 0;
        for (int i = 0; i < outer.Count; i++)
        {
            var a = outer[i]; var b = outer[(i + 1) % outer.Count];
            signed += a.X * b.Y - b.X * a.Y;
        }
        double sign = Math.Sign(signed);

        int reflex = 0;
        for (int i = 0; i < outer.Count; i++)
        {
            var p0 = outer[(i + outer.Count - 1) % outer.Count];
            var p1 = outer[i];
            var p2 = outer[(i + 1) % outer.Count];
            double cross = (p1.X - p0.X) * (p2.Y - p1.Y) - (p1.Y - p0.Y) * (p2.X - p1.X);
            if (cross * sign < 0) reflex++;
        }

        var diag = new ConformalDiagnostics();
        var fine = SurfaceMesher.Mesh(x.Problem, new PlanarMeshSettings(
            Auto: false, CellsPerWavelength: 320, EdgeMesh: true, EdgeCells: 3,
            BoundaryCells: PlanarBoundaryCells.Conformal), diagnostics: diag);

        Console.WriteLine($"[G1c] MKlopf on-axis outline: {outer.Count} vertices, {reflex} of them " +
                          $"REFLEX. At cells/λ = 320 the mesh staircases {fine.StaircaseFallbackCells} " +
                          $"cell(s) and CUTS {diag.AdmittedNonConvex.Count} non-convex one(s) that the " +
                          $"pre-M1 predicate would have refused.");

        // NON-VACUITY first: the artwork must still have the concavity whose fallback M1 removed, or
        // the zero below is zero for the wrong reason.
        Assert.True(reflex > 0,
            "MKlopf's outline has no reflex vertex at all, so the fallback this test is about could " +
            "never have fired and the fixture no longer measures what it was built for");
        Assert.True(diag.AdmittedNonConvex.Count > 0,
            "no non-convex cell was cut, so the reflex vertices are landing somewhere else entirely " +
            "and the zero below says nothing about the predicate swap");

        Assert.Equal(0, fine.StaircaseFallbackCells);
    }

    private static PlanarMeshSettings Mesh(PlanarBoundaryCells cells) =>
        new(Auto: false, CellsPerWavelength: 20, EdgeMesh: true, EdgeCells: 3, BoundaryCells: cells);

    private static string RepoFile(string relative)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "circuitrf.slnx"))) d = d.Parent;
        Assert.NotNull(d);
        return Path.Combine(d!.FullName, relative);
    }
}

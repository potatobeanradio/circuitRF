// L8b Tier 8 — the plan-view overlay contract, plus the extractor and the D7 .cem field.
//
// Copies EmMeshOverlayTests' shape deliberately: the overlay draws nothing when the toggle is off,
// contributes zero to every LayoutFrameCounters geometry count, is absent from every exporter's
// output by construction, and is cleared by a .clay edit. Plus the one thing D5 adds — **the inset
// cross-section panel still works**: kernel A's mesh did not stop existing.

using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Em;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.Theming;
using SkiaSharp;

namespace CircuitRF.Ui.Tests.Em;

public class PlanarMeshOverlayTests
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;

    /// <summary>§10.7's hero: a 2.9 mm × 20 mm strip on Top Copper.</summary>
    internal static LayoutView HeroLayout()
    {
        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.Add(new RectShape
        { Layer = new(1, 0), X1 = 0, Y1 = 0, X2 = 20_000_000, Y2 = 2_900_000 });
        return view;
    }

    internal static PlanarMeshReport HeroPlanarMesh(double fHz = 10e9)
    {
        var r = PlanarExtractor.Extract(HeroLayout().Shapes, StarterTechnologies.Pcb2Layer(), Dbu, fHz);
        Assert.True(r.Ok, r.Refusal);
        return SurfaceMesher.Mesh(r.Problem!);
    }

    private static (SKSurface Surface, LayoutRenderResult Result) Render(
        LayoutView view, Technology? tech, bool showPlanarMesh, PlanarMeshReport? mesh,
        bool showEmMesh = false, EmMeshReport? emMesh = null)
    {
        var bb = Bbox.Empty;
        foreach (var s in view.Shapes) bb = bb.Union(LayoutGeometry.BboxOf(s));
        var vp = LayoutViewport.ZoomToFit(bb, 900, 480, 0.05);

        var surface = SKSurface.Create(new SKImageInfo((int)vp.Width, (int)vp.Height));
        var opts = new LayoutRenderOptions
        {
            Theme = LayoutRenderTheme.Light, ShowGrid = false,
            ShowPlanarMesh = showPlanarMesh, PlanarMesh = mesh,
            ShowEmMesh = showEmMesh, EmMesh = emMesh,
        };
        var result = LayoutRenderer.Draw(surface.Canvas, view, tech, vp, opts);
        return (surface, result);
    }

    private static int CountMatching(SKSurface surface, Func<SKColor, bool> predicate)
    {
        using var img = surface.Snapshot();
        using var bmp = SKBitmap.FromImage(img);
        int n = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
                if (predicate(bmp.GetPixel(x, y))) n++;
        return n;
    }

    private static bool IsPlanarMeshish(SKColor c)
    {
        var want = LayoutRenderTheme.Light.PlanarMeshCell;
        return Math.Abs(c.Red - want.Red) < 45
            && Math.Abs(c.Green - want.Green) < 45
            && Math.Abs(c.Blue - want.Blue) < 45;
    }

    // ── Tier 8: the overlay contract ─────────────────────────────────────────────────────────

    [Fact]
    public void T8_1_WithTheToggleOff_NoMeshPixelIsPainted_AndWithItOnTheOverlayActuallyPaints()
    {
        var view = HeroLayout();
        var tech = StarterTechnologies.Pcb2Layer();
        var mesh = HeroPlanarMesh();

        var (offSurface, _) = Render(view, tech, showPlanarMesh: false, mesh);
        using (offSurface)
            Assert.Equal(0, CountMatching(offSurface, IsPlanarMeshish));

        var (onSurface, _) = Render(view, tech, showPlanarMesh: true, mesh);
        using (onSurface)
            Assert.True(CountMatching(onSurface, IsPlanarMeshish) > 200,
                "the overlay must actually paint when it IS asked for — otherwise the off-case " +
                "assertion above passes for the wrong reason");
    }

    [Fact]
    public void T8_2_TheToggleDefaultsToOffAtTheRENDERLayer()
    {
        // Every export / one-shot render call site builds LayoutRenderOptions without touching these,
        // so they are default(bool)/null. That is the SAME by-construction argument R-bmp-3 and
        // R-L5g-13 already rest on, and it is what makes "never reachable by an exporter" structural.
        var opts = new LayoutRenderOptions { Theme = LayoutRenderTheme.Light };
        Assert.False(opts.ShowPlanarMesh);
        Assert.Null(opts.PlanarMesh);

        var dflt = LayoutRenderOptions.Default(LayoutRenderTheme.Light);
        Assert.False(dflt.ShowPlanarMesh);
        Assert.Null(dflt.PlanarMesh);
    }

    [Fact]
    public void T8_3_TheOverlayContributesToNoGeometryCounter()
    {
        var view = HeroLayout();
        var tech = StarterTechnologies.Pcb2Layer();
        var mesh = HeroPlanarMesh();

        var (offSurface, off) = Render(view, tech, showPlanarMesh: false, mesh);
        var (onSurface,  on)  = Render(view, tech, showPlanarMesh: true,  mesh);
        using (offSurface) using (onSurface)
        {
            Assert.Equal(off.ShapesExamined,   on.ShapesExamined);
            Assert.Equal(off.ShapesDrawn,      on.ShapesDrawn);
            Assert.Equal(off.PathsConstructed, on.PathsConstructed);
            Assert.Equal(off.DrawCalls,        on.DrawCalls);
            Assert.Equal(off.LayersVisited,    on.LayersVisited);
        }
    }

    [Fact]
    public void T8_4_AnEditedLayoutCLEARSTheDisplayedMesh()
    {
        // R-em-17, and it matters more here than for the inset: a plan-view mesh drawn over EDITED
        // artwork looks like it still matches.
        var vm = new LayoutEditorViewModel(HeroLayout())
        {
            PlanarMeshReport = HeroPlanarMesh(),
            EmMeshReport     = null,
        };
        Assert.NotNull(vm.PlanarMeshReport);

        vm.Model.Shapes.Add(new RectShape { Layer = new(1, 0), X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000 });
        vm.Model.NotifyChanged();

        Assert.Null(vm.PlanarMeshReport);
    }

    [Fact]
    public void T8_5_TheINSETCrossSectionPanelStillWorks_KernelAsMeshDidNotStopExisting()
    {
        // D5: the plan-view overlay is ADDED; the inset is not replaced. Both are rendered here in
        // one frame — which is also the strongest available statement that they do not collide.
        var view = HeroLayout();
        var tech = StarterTechnologies.Pcb2Layer();

        var xs = CrossSectionExtractor.Extract(view.Shapes, tech, Dbu, null);
        Assert.True(xs.Ok, xs.Refusal);
        var inset = new QuasiStaticKernel().Mesh(xs.Problem!, EmMeshSettings.Default);

        var (surface, _) = Render(view, tech, showPlanarMesh: true, HeroPlanarMesh(),
                                  showEmMesh: true, emMesh: inset);
        using (surface)
        {
            var want = LayoutRenderTheme.Light.EmMeshConductor;
            int insetPixels = CountMatching(surface, c =>
                Math.Abs(c.Red - want.Red) < 40 && Math.Abs(c.Green - want.Green) < 40
                                                && Math.Abs(c.Blue - want.Blue) < 40);
            Assert.True(insetPixels > 20, "the cross-section inset stopped painting");
            Assert.True(CountMatching(surface, IsPlanarMeshish) > 100, "the plan-view overlay stopped painting");
        }
    }

    [Fact]
    public void T8_6_TheOverlayIsInTHEPLANEOfTheArtwork_NotAScreenSpaceInset()
    {
        // The whole reason this overlay can exist (D5). Panning the viewport must move the mesh with
        // the artwork; a screen-space inset would stay put. Rendered at two pans and compared.
        var view = HeroLayout();
        var tech = StarterTechnologies.Pcb2Layer();
        var mesh = HeroPlanarMesh();

        var bb = Bbox.Empty;
        foreach (var s in view.Shapes) bb = bb.Union(LayoutGeometry.BboxOf(s));
        var a = LayoutViewport.ZoomToFit(bb, 900, 480, 0.05);
        var b = a with { PanX = a.PanX + 4_000_000 };

        int Painted(LayoutViewport vp)
        {
            using var surface = SKSurface.Create(new SKImageInfo((int)vp.Width, (int)vp.Height));
            LayoutRenderer.Draw(surface.Canvas, view, tech, vp, new LayoutRenderOptions
            {
                Theme = LayoutRenderTheme.Light, ShowGrid = false,
                ShowPlanarMesh = true, PlanarMesh = mesh,
            });
            // Count only in the LEFT third: panning right moves the strip out of it.
            using var img = surface.Snapshot();
            using var bmp = SKBitmap.FromImage(img);
            int n = 0;
            for (int y = 0; y < bmp.Height; y++)
                for (int x = 0; x < bmp.Width / 3; x++)
                    if (IsPlanarMeshish(bmp.GetPixel(x, y))) n++;
            return n;
        }

        Assert.NotEqual(Painted(a), Painted(b));
    }

    // ── D7: the .cem analysis-kind field ─────────────────────────────────────────────────────

    [Fact]
    public void D7_ACemWrittenBeforeL8b_LoadsAndReSerialisesBYTEIDENTICALLY()
    {
        // The field is omitted when it holds the default, so no pre-L8b file gains a byte.
        //
        // **L8e moved the default from CrossSection to Auto, and the omit rule moved with it**, so
        // this claim is unchanged: a pre-L8b .cem has no field, loads as the default, and
        // re-serialises with no field. What the default MEANS changed; byte-identity did not.
        var setup = new EmSetup { Name = "hero", LayoutRef = "Amp/layout/Amp.clay" };
        string before = EmSetupPersistence.Serialize(setup);

        Assert.DoesNotContain("AnalysisKind", before);
        Assert.DoesNotContain("PlanarMesh", before);

        var reloaded = EmSetupPersistence.Deserialize(before);
        Assert.Equal(EmAnalysisKind.Auto, reloaded.AnalysisKind);
        Assert.Equal(PlanarMeshSettings.Default, reloaded.PlanarMesh);
        Assert.Equal(before, EmSetupPersistence.Serialize(reloaded));
    }

    [Fact]
    public void D7_APlanarSetupRoundTrips_IncludingItsThreeControls()
    {
        var setup = new EmSetup
        {
            Name         = "planar",
            LayoutRef    = "Amp/layout/Amp.clay",
            AnalysisKind = EmAnalysisKind.Planar,
            PlanarMesh   = new PlanarMeshSettings(Auto: false, CellsPerWavelength: 30,
                                                  EdgeMesh: false, EdgeCells: 2),
        };

        var round = EmSetupPersistence.Deserialize(EmSetupPersistence.Serialize(setup));
        Assert.Equal(EmAnalysisKind.Planar, round.AnalysisKind);
        Assert.Equal(setup.PlanarMesh, round.PlanarMesh);
        Assert.Equal(setup.PlanarMesh, setup.Clone().PlanarMesh);
        Assert.Equal(EmAnalysisKind.Planar, setup.Clone().AnalysisKind);
    }

    /// <summary>
    /// <b>SUPERSEDED BY L8e, and updated rather than deleted.</b> At L8b this asserted
    /// <c>CrossSection</c>, with the comment "the registry is L8e's — nothing in this slice may
    /// choose a kernel from the geometry". The registry arrived, so the default is now
    /// <c>Auto</c>.
    ///
    /// <para>The reason that is safe, and the reason this test still exists: auto-selection is
    /// CONSERVATIVE. A geometry kernel A accepts still goes to kernel A and still produces the
    /// identical number, so no existing <c>.cem</c> changes its answer. The only behaviour that
    /// changes is that geometry which used to be refused now runs on kernel B.</para>
    /// </summary>
    [Fact]
    public void D7_TheDefaultIsAuto_AndAutoStillPrefersKernelA()
    {
        Assert.Equal(EmAnalysisKind.Auto, new EmSetup().AnalysisKind);

        var choice = EmKernelRegistry.Choose(
            EmAnalysisKind.Auto, EmExtractorVerdict.Yes, EmExtractorVerdict.Yes);

        Assert.True(choice.Ok, choice.Refusal);
        Assert.Equal(EmAnalysisKind.CrossSection, choice.Kind);
    }

    // ── The extractor ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Extractor_PreservesLAYOUTCoordinates_SoTheOverlayMapsBackWithOneScalar()
    {
        var view = HeroLayout();
        var r = PlanarExtractor.Extract(view.Shapes, StarterTechnologies.Pcb2Layer(), Dbu, 10e9);
        Assert.True(r.Ok, r.Refusal);

        var (x0, y0, x1, y1) = r.Problem!.Bounds();
        Assert.Equal(0.0,      x0, 12);
        Assert.Equal(0.0,      y0, 12);
        Assert.Equal(20e-3,    x1, 12);
        Assert.Equal(2.9e-3,   y1, 12);
    }

    [Fact]
    public void Extractor_ReadsTheSlabFromTheStackup_OnBothStarterTechnologies()
    {
        var pcb = PlanarExtractor.Extract(HeroLayout().Shapes, StarterTechnologies.Pcb2Layer(), Dbu, 10e9);
        Assert.True(pcb.Ok, pcb.Refusal);
        Assert.Equal(4.4, pcb.Problem!.Slab.Material.EpsR, 6);
        Assert.Equal(1.6e-3, pcb.Problem.Slab.HeightM, 9);

        // MMIC: the artwork has to be on the LOWEST signal metal — Metal1, which sits directly on the
        // GaAs. Metal2 sits above an explicit air layer, so between it and the backside ground there
        // are TWO dielectrics, and D2's one-slab limit correctly refuses that (see the sibling test).
        // Stackup.Layers is ordered top-to-bottom, so Last() is the lowest.
        var mmicTech = StarterTechnologies.MmicGaAs();
        var metal1 = mmicTech.Stackup.Layers.Last(l => l.Kind == StackupKind.Conductor && !l.IsGroundReference
                                                        && l.DrawingLayers.Count > 0);
        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.Add(new RectShape
        { Layer = metal1.DrawingLayers[0], X1 = 0, Y1 = 0, X2 = 2_000_000, Y2 = 72_000 });

        var mmic = PlanarExtractor.Extract(view.Shapes, mmicTech, Dbu, 10e9,
            new EmExtractionSettings(SignalStackupLayerName: metal1.Name));
        Assert.True(mmic.Ok, mmic.Refusal);
        Assert.Equal(12.9, mmic.Problem!.Slab.Material.EpsR, 6);
    }

    [Fact]
    public void Extractor_AHoleSurvivesToTheProblem()
    {
        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.Add(new PolygonShape
        {
            Layer = new(1, 0),
            Xy    = [0, 0, 4_000_000, 0, 4_000_000, 4_000_000, 0, 4_000_000],
            Holes = [[1_000_000, 1_000_000, 2_000_000, 1_000_000, 2_000_000, 2_000_000, 1_000_000, 2_000_000]],
        });

        var r = PlanarExtractor.Extract(view.Shapes, StarterTechnologies.Pcb2Layer(), Dbu, 10e9);
        Assert.True(r.Ok, r.Refusal);
        var poly = r.Problem!.Layers[0].Polygons[0];
        Assert.Single(poly.HoleRings);
        Assert.Equal(4e-3 * 4e-3 - 1e-3 * 1e-3, poly.Area(), 12);
    }

    /// <summary>
    /// <b>L9d/M4 — UPDATED, NOT LOOSENED.</b> This test asserted L8's own refusal of two metal levels
    /// ("…arrives at L9"). L9 has arrived and this is exactly the capability it delivers, so the test
    /// now asserts what the refusal used to point AT: two levels extract, each on its own interface
    /// of a general medium, and the whole thing passes the kernel's own CanSolve.
    /// </summary>
    [Fact]
    public void Extractor_TwoMetalLevels_ExtractAsATwoLevelProblemOnAGeneralMedium()
    {
        var tech = StarterTechnologies.MmicGaAs();
        var conductors = tech.Stackup.Layers
            .Where(l => l.Kind == StackupKind.Conductor && !l.IsGroundReference && l.DrawingLayers.Count > 0)
            .ToList();
        Assert.True(conductors.Count >= 2, "the MMIC starter is expected to have two signal metals");

        var view = new LayoutView { DbuPerMicron = Dbu };
        foreach (var c in conductors.Take(2))
            view.Shapes.Add(new RectShape { Layer = c.DrawingLayers[0], X1 = 0, Y1 = 0, X2 = 1_000_000, Y2 = 100_000 });

        var r = PlanarExtractor.Extract(view.Shapes, tech, Dbu, 10e9);
        Assert.True(r.Ok, r.Refusal);

        var p = r.Problem!;
        Assert.Equal(2, p.Layers.Count);
        Assert.NotNull(p.MediumStack);
        Assert.True(p.RequiresGeneralKernel);

        // Bottom-to-top, each strictly above the last, and each ON an interface of its own medium —
        // which is L9c's first earned refusal and is what CanSolve checks.
        Assert.True(p.LevelZ(1) > p.LevelZ(0));
        Assert.True(p.CanSolve().Ok, p.CanSolve().Reason);
        Assert.True(new PlanarKernel().CanSolve(p).Ok, new PlanarKernel().CanSolve(p).Reason);

        // The lowest level sits on the slab's own top surface, which is what D3's de-embedding needs.
        Assert.True(p.LevelIsOnSlabTop(0));
        Assert.Equal(12.9, p.Slab.Material.EpsR, 6);

        // R-em-4a in the plan view: the metal's own z band is absorbed into the dielectric ABOVE it,
        // so no spurious air gap the thickness of Metal1 appears in the medium.
        var stack = p.MediumStack!;
        Assert.Equal(p.LevelZ(0), stack.InterfaceZ[1], 12);
        Assert.Equal(p.LevelZ(1), stack.TopZ, 12);
    }

    [Fact]
    public void Extractor_NamedAnalysisLevels_SelectThemAndReportWhatWasLeftOut()
    {
        // D5's own control: which levels are in the analysis is the setup's to say, and artwork on a
        // level that was left out is DROPPED — with a note, because a shape that silently vanishes
        // from a full-wave solve is exactly the failure that note exists to prevent.
        var tech = StarterTechnologies.MmicGaAs();
        var conductors = tech.Stackup.Layers
            .Where(l => l.Kind == StackupKind.Conductor && !l.IsGroundReference && l.DrawingLayers.Count > 0)
            .ToList();

        var view = new LayoutView { DbuPerMicron = Dbu };
        foreach (var c in conductors.Take(2))
            view.Shapes.Add(new RectShape { Layer = c.DrawingLayers[0], X1 = 0, Y1 = 0, X2 = 1_000_000, Y2 = 100_000 });

        // The LOWER metal only — a one-level analysis of a two-level drawing.
        var lower = conductors.OrderBy(c => tech.Stackup.Layers.IndexOf(c)).Last();  // Layers are TOP-to-bottom
        var r = PlanarExtractor.Extract(view.Shapes, tech, Dbu, 10e9,
            new EmExtractionSettings(AnalysisLevelNames: [lower.Name]));

        Assert.True(r.Ok, r.Refusal);
        Assert.Single(r.Problem!.Layers);
        Assert.Equal(lower.Name, r.Problem!.Layers[0].Name);

        // …and a one-level analysis stays on L8's shipped path bit-for-bit.
        Assert.False(r.Problem!.RequiresGeneralKernel);
        Assert.Contains(r.Notes, n => n.Contains("NOT in this EM setup's analysis levels"));

        // Naming a level the technology does not have is refused BY NAME rather than ignored.
        var bad = PlanarExtractor.Extract(view.Shapes, tech, Dbu, 10e9,
            new EmExtractionSettings(AnalysisLevelNames: ["NoSuchMetal"]));
        Assert.False(bad.Ok);
        Assert.Contains("NoSuchMetal", bad.Refusal);
    }

    /// <summary>
    /// <b>L9d/D5 — the ungrounded refusal, narrowed to what L9b MEASURED.</b> It used to point at a
    /// phase number; it now names two separate reasons — the spectrum's second branch point (refused
    /// permanently for a denser bottom) and the de-embedding's grounded-slab C_pul (what actually
    /// blocks it here). The accepted set did not widen, and the test asserts the narrowing rather
    /// than a capability.
    /// </summary>
    [Fact]
    public void Extractor_UngroundedStack_RefusalNamesBothReasons_NotAPhaseNumber()
    {
        var tech = StarterTechnologies.MmicGaAs();
        foreach (var l in tech.Stackup.Layers) l.IsGroundReference = false;
        tech.Stackup.Bottom = BoundaryCondition.Open;

        var signal = tech.Stackup.Layers.First(l =>
            l.Kind == StackupKind.Conductor && l.DrawingLayers.Count > 0);
        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.Add(new RectShape { Layer = signal.DrawingLayers[0], X1 = 0, Y1 = 0, X2 = 1_000_000, Y2 = 100_000 });

        var r = PlanarExtractor.Extract(view.Shapes, tech, Dbu, 10e9,
            new EmExtractionSettings(SignalStackupLayerName: signal.Name));

        Assert.False(r.Ok);
        Assert.Contains("branch point", r.Refusal);      // reason 1, measured, structural
        Assert.Contains("C_pul", r.Refusal);             // reason 2, what actually blocks it here
        Assert.Contains("ground reference", r.Refusal);  // …and what to do about it
    }

    // ── The panel numbers ────────────────────────────────────────────────────────────────────

    private static EmSetupEditorViewModel PlanarPanel()
    {
        var vm = new EmSetupEditorViewModel(
            Path.Combine(Path.GetTempPath(), "unused.cem"),
            new EmSetup { Name = "x", LayoutRef = "a.clay", AnalysisKind = EmAnalysisKind.Planar })
        {
            ResolveLayout = _ => EmSetupDocumentTests.HeroSource(),
        };
        return vm;
    }

    [Fact]
    public void Panel_MeshBuildsTheSurfaceMesh_AndSurfacesTheENGINESOwnNumbersAndNotes()
    {
        // R-em-16, unchanged for kernel B: print the engine's report verbatim; do not re-word it.
        var vm = PlanarPanel();
        vm.BuildPlanarMeshCommand.Execute(null);

        Assert.Null(vm.PlanarExtractionRefusal);
        Assert.NotNull(vm.PlanarMeshReport);
        Assert.Equal(PlanarBudgetVerdict.Ok, vm.PlanarMeshReport!.Verdict);
        Assert.Null(vm.PlanarBudgetRefusal);

        foreach (string note in vm.PlanarMeshReport.Notes) Assert.Contains(note, vm.PlanarMeshNotes);
        Assert.Contains(vm.PlanarMeshReport.UnknownCount.ToString("N0"), vm.PlanarMeshSummary);
        Assert.Contains("across the narrowest conductor", vm.PlanarMeshSummary);
    }

    [Fact]
    public void Panel_MeshNeverSolves()
    {
        // §10.5 puts the mesh viewer BEFORE the solver, and this slice has no solver at all. The VM
        // holds no planar DataSet and no planar solve seam, which is what makes that structural.
        var vm = PlanarPanel();
        vm.BuildPlanarMeshCommand.Execute(null);
        Assert.NotNull(vm.PlanarMeshReport);
        Assert.Null(typeof(EmSetupEditorViewModel).GetProperty("PlanarSolution"));
    }

    [Fact]
    public void Panel_AnEditedLayoutInvalidatesTheSurfaceMeshToo()
    {
        var vm = PlanarPanel();
        vm.BuildPlanarMeshCommand.Execute(null);
        Assert.NotNull(vm.PlanarMeshReport);

        vm.InvalidateMesh();

        Assert.Null(vm.PlanarMeshReport);
        Assert.Null(vm.PlanarProblem);
        Assert.Empty(vm.PlanarMeshNotes);
        Assert.Equal("", vm.PlanarMeshSummary);
    }

    [Fact]
    public void Panel_TheMeshIsComputedOncePerSweep_FromItsHIGHESTFrequencyOnly()
    {
        // D4 — the mesh is frequency-dependent but computed once per sweep, and the report names the
        // frequency it was derived at so a user who widens the sweep is not confused by N moving.
        var vm = PlanarPanel();
        vm.BuildPlanarMeshCommand.Execute(null);
        double top = vm.Working.Frequency.Expand().Max();

        Assert.Equal(top, vm.PlanarMeshReport!.FrequencyHz, 6);
        Assert.Contains(vm.PlanarMeshNotes, n => n.Contains("highest frequency of the sweep"));
    }

    [Fact]
    public void Extractor_ThePathToTheMesher_ProducesTheSameNAsTheEngineTestsReport()
    {
        // The Ui half and the engine half must agree about the hero, or one of them is measuring
        // something else. 552 is the number Tier 7 reports; this pins the two together.
        var r = HeroPlanarMesh();
        Assert.Equal(PlanarBudgetVerdict.Ok, r.Verdict);
        Assert.True(r.UnknownCount is > 100 and < 1000, $"N = {r.UnknownCount}");
    }

    // ── M2 (brief-gazz-accuracy-ceiling) — the direct ẑẑ kernel, reachable from the panel ──────

    [Fact]
    public void ZzM2_ACemThatNeverTurnedOnTheDirectKernel_ReSerialisesBYTEIDENTICALLY()
    {
        // Same additive convention as AnalysisKind / PlanarMesh / PortZ0s: the DTO field is
        // NULLABLE and the document's DefaultIgnoreCondition is WhenWritingNull, so a .cem written
        // before M2 gains no byte. A plain `bool` would have been written unconditionally and
        // changed every existing file.
        var setup = new EmSetup { Name = "hero", LayoutRef = "Amp/layout/Amp.clay" };
        string before = EmSetupPersistence.Serialize(setup);

        Assert.DoesNotContain("DirectVerticalKernel", before);

        var reloaded = EmSetupPersistence.Deserialize(before);
        Assert.False(reloaded.DirectVerticalKernel);
        Assert.Equal(before, EmSetupPersistence.Serialize(reloaded));
    }

    [Fact]
    public void ZzM2_TurningItOn_RoundTripsAndSurvivesClone()
    {
        var setup = new EmSetup
        {
            Name                 = "planar",
            LayoutRef            = "Amp/layout/Amp.clay",
            AnalysisKind         = EmAnalysisKind.Planar,
            DirectVerticalKernel = true,
        };

        string json = EmSetupPersistence.Serialize(setup);
        Assert.Contains("DirectVerticalKernel", json);
        Assert.True(EmSetupPersistence.Deserialize(json).DirectVerticalKernel);

        // Clone drives the editor's UNDO snapshots — a field missing from it is silently lost on
        // the next unrelated edit, which is the failure this asserts against rather than assumes.
        Assert.True(setup.Clone().DirectVerticalKernel);
    }

    // ── Defaults that have to be useful on the COMMON case, not on a debugging fixture ─────────

    [Fact]
    public void ACemWrittenBeforeAdaptiveSampling_LoadsWithItON_AndReSerialisesBYTEIDENTICALLY()
    {
        // Opposite polarity to every other flag here, because the DEFAULT is on: null means ON, and
        // only an explicit opt-OUT is ever written. A pre-adaptive file therefore gains no byte and
        // picks the new default up — which is the point, since solving all 101 default points on the
        // full-wave kernel is 80 minutes to nearly three hours.
        var setup = new EmSetup { Name = "hero", LayoutRef = "Amp/layout/Amp.clay" };
        string before = EmSetupPersistence.Serialize(setup);

        Assert.True(setup.AdaptiveSampling);
        Assert.DoesNotContain("AdaptiveSampling", before);

        var reloaded = EmSetupPersistence.Deserialize(before);
        Assert.True(reloaded.AdaptiveSampling);
        Assert.Equal(before, EmSetupPersistence.Serialize(reloaded));
    }

    [Fact]
    public void TurningAdaptiveSamplingOFF_IsWrittenExplicitly_RoundTripsAndSurvivesClone()
    {
        var setup = new EmSetup
        {
            Name             = "planar",
            LayoutRef        = "Amp/layout/Amp.clay",
            AnalysisKind     = EmAnalysisKind.Planar,
            AdaptiveSampling = false,
        };

        string json = EmSetupPersistence.Serialize(setup);
        Assert.Contains("AdaptiveSampling", json);
        Assert.False(EmSetupPersistence.Deserialize(json).AdaptiveSampling);
        Assert.False(setup.Clone().AdaptiveSampling);   // Clone drives the editor's undo snapshots
    }

    [Fact]
    public void DispersionCorrection_DefaultsON_ForANewSetup_ButAnExistingFilesFALSE_IsPreserved()
    {
        // The default flipped because the default sweep runs to 20 GHz, where kernel A's static C
        // puts eps_eff 23% high (L8d). The field is NON-nullable in the file, so every .cem ever
        // written carries an explicit value and no existing setup changes behaviour — asserted here
        // against a hand-written file rather than reasoned about.
        Assert.True(new EmSetup().DispersionCorrection);

        const string olderFile = """
        {
          "FormatVersion": 1,
          "Name": "older",
          "LayoutRef": "Amp/layout/Amp.clay",
          "DispersionCorrection": false
        }
        """;

        var loaded = EmSetupPersistence.Deserialize(olderFile);
        Assert.False(loaded.DispersionCorrection);
        Assert.True(loaded.AdaptiveSampling);           // …while a field it never had takes the default
    }
}

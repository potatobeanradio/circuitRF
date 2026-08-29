// Owner round, 2026-08-11 — the EM Setup panel's decluttering, plus the two behavioural changes
// that arrived with it: EM results stop colliding with a schematic's .npy, and a port's coordinate
// is printed in the LAYOUT's own display unit.
//
// The note-removal tests are deliberately phrased as ABSENCE checks against the exact sentence the
// owner asked to be gone, with a non-vacuity guard beside each (the extraction really did happen, so
// a test cannot pass by nothing having been produced at all).

using System.Numerics;
using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Em;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.Theming;
using SkiaSharp;

namespace CircuitRF.Ui.Tests.Em;

public class EmPanelDeclutterTests
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;
    private static readonly LayerKey TopCopper = new(1, 0);

    private static long Mm(double mm) => (long)Math.Round(mm * 1000 * Dbu);

    private static RectShape Line() =>
        new() { Layer = TopCopper, X1 = 0, Y1 = 0, X2 = Mm(20), Y2 = Mm(2.9) };

    private static LabelShape Port(string text, double xMm, double yMm) =>
        new() { Layer = TopCopper, X = Mm(xMm), Y = Mm(yMm), Text = text, Height = Mm(0.5), IsPort = true };

    private static PlanarProblem Problem(params LayoutShape[] shapes)
    {
        var r = PlanarExtractor.Extract(shapes, StarterTechnologies.Pcb2Layer(), Dbu, 10e9);
        Assert.True(r.Ok, r.Refusal);
        return r.Problem!;
    }

    // ── The three notes the owner asked to be gone ────────────────────────────────────────────

    [Fact]
    public void CrossSection_NoLongerReports_IgnoredLabelsAndBitmaps()
    {
        LayoutShape[] shapes =
        [
            Line(),
            new LabelShape { Layer = TopCopper, X = Mm(5), Y = Mm(1), Text = "silkscreen", Height = Mm(0.5) },
        ];

        var r = CrossSectionExtractor.Extract(shapes, StarterTechnologies.Pcb2Layer(), Dbu,
                                              new EmExtractionSettings());

        Assert.True(r.Ok, r.Refusal);                       // non-vacuity: the label WAS classified
        Assert.DoesNotContain(r.Notes, n => n.Contains("label/bitmap", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(r.Notes, n => n.Contains("annotation is not artwork", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Flatten_NoLongerClaims_TheLayoutItselfIsUnchanged()
    {
        // A layout whose metal is entirely inside a placed instance is the case that produces the
        // note at all — an unresolvable CellRef still counts the instance, which is enough here.
        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.Add(Line());
        view.Instances.Add(new LayoutInstance { CellRef = "nowhere", X = 0, Y = 0, Mag = 1.0 });

        var r = EmGeometry.Flatten(view, Path.Combine(Path.GetTempPath(), "x.clay"));

        Assert.NotEmpty(r.Notes);                           // non-vacuity: it did have something to say
        Assert.DoesNotContain(r.Notes, n => n.Contains("layout itself is unchanged", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PortNotes_NoLongerRestate_TheDeEmbeddingReferencePlane()
    {
        LayoutShape[] shapes = [Line(), Port("1", 0, 1.45), Port("2", 20, 1.45)];
        var r = EmPortExtraction.Extract(shapes, Problem(shapes), Dbu);

        Assert.True(r.Ok, r.Refusal);
        Assert.Equal(2, r.Ports.Count);                     // non-vacuity: both ports resolved
        Assert.NotEmpty(r.Notes);
        Assert.DoesNotContain(r.Notes, n => n.Contains("reference plane", StringComparison.OrdinalIgnoreCase));
    }

    // ── A port's coordinate is in the LAYOUT's own display unit ───────────────────────────────

    [Theory]
    [InlineData(LayoutUnit.Mil,  "mil")]
    [InlineData(LayoutUnit.Mm,   "mm")]
    [InlineData(LayoutUnit.Um,   "µm")]
    public void PortNotes_PrintCoordinatesInTheLayoutsOwnDisplayUnit(LayoutUnit unit, string suffix)
    {
        LayoutShape[] shapes = [Line(), Port("1", 0, 1.45), Port("2", 20, 1.45)];
        var r = EmPortExtraction.Extract(shapes, Problem(shapes), Dbu, null, unit);

        Assert.True(r.Ok, r.Refusal);
        Assert.Contains(r.Notes, n => n.Contains(suffix, StringComparison.Ordinal));
    }

    [Fact]
    public void PortNotes_OnAMilLayout_DoNotPrintMicrons()
    {
        LayoutShape[] shapes = [Line(), Port("1", 0, 1.45), Port("2", 20, 1.45)];
        var r = EmPortExtraction.Extract(shapes, Problem(shapes), Dbu, null, LayoutUnit.Mil);

        Assert.True(r.Ok, r.Refusal);
        // The µ glyph appears nowhere else in a port note, so its absence is a clean signal.
        Assert.DoesNotContain(r.Notes, n => n.Contains('µ'));
    }

    // ── The .npy no longer collides with a schematic's ────────────────────────────────────────

    [Fact]
    public void EmNpyKey_DiffersFromTheSchematicKeyOfTheSameName()
    {
        // The reported collision: cell "MLin" has a schematic AND an EM setup created beside it, both
        // named after the cell, and results/ is one flat shared folder.
        string schematicKey = CircuitRF.Ui.Schematic.RunResultsWriter.SchematicKey(
            Path.Combine("ws", "MLin", "schematic", "MLin.csch"), "scratch");

        var setup = new EmSetup { Name = "MLin", LayoutRef = "MLin/layout/MLin.clay" };

        Assert.Equal("MLin", schematicKey);
        Assert.NotEqual(schematicKey, EmRunService.ResolveNpyKey(setup));
    }

    [Fact]
    public void EmSnpKeep_TheirUnsuffixedName_SoSchematicReferencesSurvive()
    {
        // Only the .npy moved. The .snp is what a schematic references by path, and renaming it would
        // orphan every existing reference — the whole reason ResolveResultKey stayed as it was.
        var setup = new EmSetup { Name = "MLin", LayoutRef = "MLin/layout/MLin.clay" };
        string root = Path.Combine(Path.GetTempPath(), "results");

        Assert.Equal("MLin", EmRunService.ResolveResultKey(setup));
        Assert.Equal(Path.Combine(root, "MLin.s2p"), EmRunService.ResolveSnpPath(root, setup, 2));
    }

    // ── The panel's two new derived strings ───────────────────────────────────────────────────

    [Fact]
    public void PortsHelpText_FollowsTheChosenKernel_AndNeverRestatesTheReferencePlane()
    {
        var vm = new EmSetupEditorViewModel(
            Path.Combine(Path.GetTempPath(), "x.cem"),
            new EmSetup { Name = "x" });

        vm.SelectedKernel = EmAnalysisKind.CrossSection;
        string a = vm.PortsHelpText;
        vm.SelectedKernel = EmAnalysisKind.Planar;
        string b = vm.PortsHelpText;

        Assert.NotEqual(a, b);
        Assert.Contains("no meshed port", a, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Port tool", b, StringComparison.OrdinalIgnoreCase);
        foreach (string s in new[] { a, b })
            Assert.DoesNotContain("reference plane", s, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnalysisLevelsSummary_AnswersTheQuestionTheCollapsedListWould()
    {
        var vm = new EmSetupEditorViewModel(
            Path.Combine(Path.GetTempPath(), "x.cem"),
            new EmSetup { Name = "x" });

        // No layout resolves here, so there are no rows — the "none selected" wording still has to be
        // a sentence rather than a bare "0 of 0".
        Assert.Contains("Analysis levels", vm.AnalysisLevelsSummary, StringComparison.Ordinal);
        Assert.Contains("every level with artwork", vm.AnalysisLevelsSummary, StringComparison.Ordinal);
    }

    // ── No heading is ever left standing over nothing ─────────────────────────────────────────

    [Fact]
    public void HasNotes_GatesTheNotesHeadingWithItsOwnList()
    {
        var vm = new EmSetupEditorViewModel(
            Path.Combine(Path.GetTempPath(), "x.cem"),
            new EmSetup { Name = "x" });

        Assert.False(vm.HasNotes);                       // no layout resolves: nothing to say
        vm.Notes = ["something"];
        Assert.True(vm.HasNotes);
    }

    [Fact]
    public void CrossSectionSubgroup_IsGatedOnTheReadback_HeadingIncluded()
    {
        // Readback is the cross-section kernel's own product and is null for a full-wave setup, so
        // the whole block — header, summary and grid — has to hang off one gate. AXAML cannot be
        // exercised headlessly here (this suite constructs no Avalonia controls), so the wiring is
        // pinned by reading the markup, the same fallback this repo already uses elsewhere.
        string xaml = File.ReadAllText(RepoFile("src/Ui/Views/Layout/EmSetupEditorView.axaml"));

        int block = xaml.IndexOf("Text=\"Cross-section\"", StringComparison.Ordinal);
        Assert.True(block > 0, "the Cross-section subgroup heading is gone");

        // The nearest enclosing StackPanel before the heading must carry the Readback gate.
        int panel = xaml.LastIndexOf("<StackPanel", block, StringComparison.Ordinal);
        Assert.True(panel > 0);
        string open = xaml[panel..block];
        Assert.Contains("ViewModel.Readback", open, StringComparison.Ordinal);
        Assert.Contains("IsNotNull", open, StringComparison.Ordinal);
    }

    // ── Header geometry: two "…" buttons of one size, and a bottom-aligned button cluster ─────

    [Fact]
    public void BothEllipsisPickers_ShareOneStyle_SoTheyCannotDriftApartInSize()
    {
        string xaml = File.ReadAllText(RepoFile("src/Ui/Views/Layout/EmSetupEditorView.axaml"));

        // One style definition, and both buttons wearing it — rather than two hand-matched sets of
        // Width/Height/Padding, which is what drifts the next time one of them is touched.
        Assert.Contains("Selector=\"Button.ellipsis\"", xaml, StringComparison.Ordinal);
        foreach (string name in new[] { "ChangeLayoutButton", "BrowseSnpOutputButton" })
        {
            int at = xaml.IndexOf($"Name=\"{name}\"", StringComparison.Ordinal);
            Assert.True(at > 0, $"{name} is gone");
            int end = xaml.IndexOf("/>", at, StringComparison.Ordinal);
            Assert.Contains("Classes=\"ellipsis\"", xaml[at..end], StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheButtonCluster_IsTopRightAligned_SoItLandsOnTheOutputFileRow()
    {
        // UPDATED, not loosened (owner request, 2026-08-12: "move the Mesh, Simulate, Undo, Redo,
        // Save and Save As buttons up one row so they are on the same row as the Output file, and
        // have them hug the right side"). This asserted BOTTOM alignment before, which put the
        // cluster on the Change Layout button's baseline — the 2026-08-11 arrangement this replaces.
        //
        // The mechanism is the same trick against the other end: the identity block's FIRST row is
        // the output-file row, so top-aligning the cluster lands on it with no margin arithmetic to
        // go stale. Both halves are asserted — the alignment, and the row order it depends on.
        string xaml = File.ReadAllText(RepoFile("src/Ui/Views/Layout/EmSetupEditorView.axaml"));

        int cluster = xaml.IndexOf(
            "<StackPanel Grid.Column=\"1\" Orientation=\"Horizontal\"", StringComparison.Ordinal);
        Assert.True(cluster > 0, "the button cluster is no longer the header Grid's second column");

        int clusterEnd = xaml.IndexOf('>', cluster);
        string open = xaml[cluster..clusterEnd];
        Assert.Contains("HorizontalAlignment=\"Right\"", open, StringComparison.Ordinal);
        Assert.Contains("VerticalAlignment=\"Top\"", open, StringComparison.Ordinal);
        Assert.Contains("Name=\"MeshButton\"", xaml[cluster..], StringComparison.Ordinal);

        // Output file ABOVE the layout reference — that ordering is what makes top-alignment land on
        // the output-file row rather than on Change Layout.
        Assert.True(xaml.IndexOf("Name=\"BrowseSnpOutputButton\"", StringComparison.Ordinal)
                    < xaml.IndexOf("Name=\"ChangeLayoutButton\"", StringComparison.Ordinal),
                    "the output-file row must be the identity block's FIRST row");
    }

    [Fact]
    public void TheAnalysisGroup_SplitsIntoTwoRealColumns_NotAReflowingWrapPanel()
    {
        // A WrapPanel only saves height while both blocks happen to fit side by side; in a docked
        // panel it reflows to one column and the group comes out TALLER, which is what the first
        // attempt did. A 50/50 Grid always splits.
        string xaml = File.ReadAllText(RepoFile("src/Ui/Views/Layout/EmSetupEditorView.axaml"));

        int hdr = xaml.IndexOf("Text=\"Analysis\"", StringComparison.Ordinal);
        Assert.True(hdr > 0);
        int grid = xaml.IndexOf("<Grid ColumnDefinitions=\"*,*\"", hdr, StringComparison.Ordinal);
        Assert.True(grid > hdr, "the Analysis group is not a two-column Grid");
        Assert.Contains("ColumnSpacing", xaml[grid..(grid + 120)], StringComparison.Ordinal);

        // The notes are the tallest thing in the group, so they have to be IN the right-hand column
        // (beside the description) rather than full width below both, or the split buys nothing.
        // Checked positionally against the column marker rather than against a closing tag, since
        // the readback carries a nested Grid of its own.
        int rightColumn = xaml.IndexOf("Grid.Column=\"1\"", grid, StringComparison.Ordinal);
        int notes       = xaml.IndexOf("ViewModel.HasNotes", grid, StringComparison.Ordinal);
        int nextGroup   = xaml.IndexOf("Text=\"Frequency\"", grid, StringComparison.Ordinal);
        Assert.True(rightColumn > grid, "the Analysis group has no right-hand column");
        Assert.InRange(notes, rightColumn, nextGroup);
    }

    // ── The conformal overlay renders as CUT CELLS at ordinary zoom ───────────────────────────

    /// <summary>A taper — oblique edges, which is what conformal boundary cells are FOR. A Manhattan
    /// rectangle is bit-identical between the two models (R-cut-2) and could not tell them apart.</summary>
    private static LayoutView TaperLayout()
    {
        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.Add(new PolygonShape
        {
            Layer = TopCopper,
            Xy    = [0, 0, Mm(20), Mm(2), Mm(20), Mm(4), 0, Mm(3)],
        });
        return view;
    }

    private static PlanarMeshReport TaperMesh(PlanarBoundaryCells boundary)
    {
        var r = PlanarExtractor.Extract(TaperLayout().Shapes, StarterTechnologies.Pcb2Layer(), Dbu, 10e9);
        Assert.True(r.Ok, r.Refusal);
        return SurfaceMesher.Mesh(
            r.Problem!, PlanarMeshSettings.Default with { BoundaryCells = boundary },
            PlanarEdgeReference.ConductorWidth, null);
    }

    private static int MeshPixels(LayoutView view, PlanarMeshReport mesh)
    {
        var bb = Bbox.Empty;
        foreach (var s in view.Shapes) bb = bb.Union(LayoutGeometry.BboxOf(s));
        var vp = LayoutViewport.ZoomToFit(bb, 900, 480, 0.05);

        using var surface = SKSurface.Create(new SKImageInfo((int)vp.Width, (int)vp.Height));
        LayoutRenderer.Draw(surface.Canvas, view, StarterTechnologies.Pcb2Layer(), vp,
            new LayoutRenderOptions
            {
                Theme = LayoutRenderTheme.Light, ShowGrid = false,
                ShowPlanarMesh = true, PlanarMesh = mesh,
            });

        using var img = surface.Snapshot();
        using var bmp = SKBitmap.FromImage(img);
        var want = LayoutRenderTheme.Light.PlanarMeshCell;
        int n = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                if (Math.Abs(c.Red - want.Red) < 45 && Math.Abs(c.Green - want.Green) < 45
                                                    && Math.Abs(c.Blue - want.Blue) < 45) n++;
            }
        return n;
    }

    [Fact]
    public void ConformalMesh_DrawsRealCutCells_AtTheZoomThatFitsTheDesign()
    {
        // The direct oracle for the owner's report (2026-08-11): the decimated branch draws GRID
        // rectangles and ignores every cut, and it was firing at the ordinary fit-the-design zoom
        // because it keyed off the globally SMALLEST cell edge — a refined rim sliver well below the
        // typical grid step.
        //
        // Gated on the decision rather than on pixels ON PURPOSE. A pixel comparison of conformal
        // against staircase cannot separate "the cuts were drawn" from "the cell set differs":
        // conformal MERGES sliver cells, so even the two decimated pictures differ, and a first
        // version of this test passed against the unfixed code for exactly that reason.
        var view = TaperLayout();
        var mesh = TaperMesh(PlanarBoundaryCells.Conformal);

        var bb = Bbox.Empty;
        foreach (var s in view.Shapes) bb = bb.Union(LayoutGeometry.BboxOf(s));
        var vp = LayoutViewport.ZoomToFit(bb, 900, 480, 0.05);
        double scaleUm = vp.Zoom * Dbu;      // device px per DBU -> per micron

        Assert.False(LayoutRenderer.WouldDecimatePlanarMesh(mesh, scaleUm),
                     "the conformal mesh is still being decimated at the fit-the-design zoom");

        // Non-vacuity in BOTH directions: the fixture is genuinely graded (so the old minimum-based
        // rule really would have decimated here), and zooming far enough out still decimates, so the
        // fix widened the readable range rather than deleting the branch.
        Assert.True(mesh.MinCellEdgeM * scaleUm * 1e6 < 2.5,
                    "the fixture is not graded enough to exercise the bug");
        Assert.True(LayoutRenderer.WouldDecimatePlanarMesh(mesh, scaleUm / 50),
                    "the decimated branch no longer fires at any zoom");
    }

    [Fact]
    public void ConformalAndStaircase_BothRender_AtTheFitZoom()
    {
        // Weaker than the decision gate above, but it drives the REAL renderer end to end, which the
        // decision gate does not — a branch chosen correctly and then drawn by broken code would
        // still show up here as nothing painted.
        var view = TaperLayout();
        Assert.True(MeshPixels(view, TaperMesh(PlanarBoundaryCells.Staircase)) > 0);
        Assert.True(MeshPixels(view, TaperMesh(PlanarBoundaryCells.Conformal)) > 0);
    }

    [Fact]
    public void ConformalMesh_HasCutCellsToDraw_SoTheOracleAboveIsNotVacuous()
    {
        var mesh = TaperMesh(PlanarBoundaryCells.Conformal);
        int cut = mesh.Mesh.Cells.Count(c => c.IsCut);
        Assert.True(cut > 0, "the fixture produced no cut cells, so it cannot test conformal drawing");
        Assert.DoesNotContain(TaperMesh(PlanarBoundaryCells.Staircase).Mesh.Cells, c => c.IsCut);
    }

    [Fact]
    public void AFailedRun_PostsTheErrorLAST_AndNoNotesAfterIt()
    {
        // Owner request, 2026-08-11: on a failure the error must be the last line, with no pile of
        // info under it, and no second "not solved" line restating it in weaker words.
        //
        // WorkspaceViewModel cannot be constructed headlessly (its constructor stands up a Dock
        // factory and posts to the UI thread), so this is a source scan — the same fallback this
        // repo already uses for view-model-only wiring. What it pins is ORDER, which is the whole
        // of the fix.
        string src = File.ReadAllText(RepoFile("src/Ui/ViewModels/WorkspaceViewModel.cs"));

        int start = src.IndexOf(
            "if (result.Status is EmRunStatus.NoLayout or EmRunStatus.Refused or EmRunStatus.EngineError)",
            StringComparison.Ordinal);
        Assert.True(start > 0, "the EM failure branch is gone");
        int end = src.IndexOf("            return;", start, StringComparison.Ordinal);
        Assert.True(end > start);
        string block = src[start..end];

        // The engine's descriptive notes are not posted on this path at all.
        Assert.DoesNotContain("result.Notes", block, StringComparison.Ordinal);

        // Owner report, 2026-08-11: the two live rows both said the same sentence, so the panel
        // showed one message twice. Both rows must be resolved (each carries a bar), so the fix is
        // that they say DIFFERENT things — never the same string.
        var completes = System.Text.RegularExpressions.Regex
            .Matches(block, @"(?:stage|sweep)Live\.Complete\([^,]+,\s*\$?""([^""]*)""")
            .Select(m => m.Groups[1].Value)
            .ToList();
        Assert.Equal(2, completes.Count);
        Assert.NotEqual(completes[0], completes[1]);

        // Warnings and per-file errors come BEFORE the run's own error, which is posted last.
        int warnings = block.IndexOf("Messages.Warning(w)", StringComparison.Ordinal);
        int errors   = block.IndexOf("Messages.Errors", StringComparison.Ordinal);
        int fileErrs = block.IndexOf("Messages.Error(e)", StringComparison.Ordinal);
        int theError = block.IndexOf("Messages.Error(result.Error", StringComparison.Ordinal);
        _ = errors;
        Assert.True(warnings > 0 && fileErrs > warnings, "warnings must still be reported, first");
        Assert.True(theError > fileErrs, "the run's own error must be posted last");

        // And nothing at all after it.
        Assert.DoesNotContain("Messages.", block[(theError + "Messages.Error(result.Error".Length)..],
                              StringComparison.Ordinal);

        // The retired second line. Comment-only lines are stripped first — this repo has been caught
        // by exactly this before: the code's own note explaining that a string is GONE contains the
        // string, so an unstripped absence scan fails on its own documentation.
        Assert.DoesNotContain("not solved", StripCommentLines(src), StringComparison.Ordinal);
    }

    private static string StripCommentLines(string source)
        => string.Join('\n', source.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    private static string RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, relative);
    }

    // ── The Simulate/Cancel messages ──────────────────────────────────────────────────────────

    [Fact]
    public void RunStartText_NamesThePointCount_AndWhetherAdaptiveActuallyApplies()
    {
        var on  = new EmSetup { Name = "x", AdaptiveSampling = true };
        var off = new EmSetup { Name = "x", AdaptiveSampling = false };

        string p = ViewModels.WorkspaceViewModel.EmRunStartText(on, 101, EmAnalysisKind.Planar);
        Assert.Contains("101", p, StringComparison.Ordinal);
        Assert.Contains("solves a subset", p, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("is off", ViewModels.WorkspaceViewModel.EmRunStartText(off, 101, EmAnalysisKind.Planar),
                        StringComparison.OrdinalIgnoreCase);

        // The cross-section kernel is closed-form per point, so claiming adaptive sampling is in play
        // would be false — it says so instead.
        Assert.Contains("does not apply",
                        ViewModels.WorkspaceViewModel.EmRunStartText(off, 101, EmAnalysisKind.CrossSection),
                        StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Owner report, 2026-08-29: the started line hedged with "will be used if the full-wave analysis
    /// is chosen" on a run that was already under way. The kernel is RESOLVED by the time the line is
    /// posted, so the line states an outcome — and where that outcome contradicts the checkbox, it
    /// gives the reason in the same sentence.
    /// </summary>
    [Fact]
    public void RunStartText_NeverHedges_AndSaysWhyWhenItContradictsTheCheckbox()
    {
        var auto = new EmSetup { Name = "x", AnalysisKind = EmAnalysisKind.Auto, AdaptiveSampling = true };

        string planar = ViewModels.WorkspaceViewModel.EmRunStartText(auto, 101, EmAnalysisKind.Planar);
        Assert.Contains("is on", planar, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("if the full-wave analysis is chosen", planar, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("will be used if", planar, StringComparison.OrdinalIgnoreCase);

        // Ticked, but the registry chose kernel A: say so, say every point is solved, and say why.
        string cross = ViewModels.WorkspaceViewModel.EmRunStartText(auto, 101, EmAnalysisKind.CrossSection);
        Assert.Contains("is on", cross, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("full-wave analysis only", cross, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cross-section", cross, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("every point is solved", cross, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("if the full-wave analysis is chosen", cross, StringComparison.OrdinalIgnoreCase);
    }
}

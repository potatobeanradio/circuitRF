// Designer report, round 18 — Gerber sets that imported with an empty stackup or a plane drawn as rings, a pad
// analysed as a trace, layers that could not be hidden, and a via spanning one conductor. One test per claim:
//   * abbreviated names (TOP-COP, BTM-COP, TOP-SLD, HOLE, PCB FRAME, SLK-NS, SM-NS) are identified;
//   * unidentified files mapped onto a chosen technology's conductors import, in the technology's order;
//   * a plane painted with a fat pen beside its own legend is one region, and the legend stays strokes;
//   * a trace drawn as many end-to-end strokes is not a painted region;
//   * a silkscreen of hundreds of strokes is not called a vector-filled pour;
//   * a declared component pad is not analysed as a trace;
//   * a layer the technology does not define can be hidden for the session, and the technology is untouched;
//   * a via cannot be given one conductor at both ends in the editor, and validation flags one that has.

using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Tests;

public sealed class DesignerRound18Tests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("crf-r18-").FullName;
    private const string TwoLayerId = "pcb-2layer_FR-4_70mil_1oz";

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void AbbreviatedNames_AreIdentified()
    {
        string dir = Folder("abbrev");
        string[] names = ["TOP-COP.GBR", "BTM-COP.GBR", "TOP-SLD.GBR", "HOLE.GBR", "PCB FRAME.GBR", "SLK-NS.PHO", "SM-NS.PHO"];
        for (int i = 0; i < names.Length; i++) File.WriteAllText(Path.Combine(dir, names[i]), Artwork(i + 1));

        var result = GerberImport.Import(FilesIn(dir), _root, "abbrev", null, 1000);

        string LayerOf(string file) => result.Layers.Single(l => l.FileName == file).LayerName;
        Assert.Equal(["Top Copper", "Bottom Copper", "Soldermask Top", "Drill", "Outline", "Silk Top", "Soldermask Top"],
                     names.Select(LayerOf));
    }

    /// <summary>Named so nothing identifies them, and in the alphabet's order the wrong way up — the table lists
    /// by shape count and the stack must come from the technology.</summary>
    [Fact]
    public void UnidentifiedFilesMappedOntoConductors_ImportIntoTheTechnology_InItsOrder()
    {
        string techPath = WorkspaceCreate.InstallTechnology(_root, TwoLayerId);
        var tech = TechPersistence.LoadFromFile(techPath);
        LayerKey KeyOf(string name) => tech.Layers.Single(l => l.Name == name).Key;
        string dir = Folder("foils");
        File.WriteAllText(Path.Combine(dir, "artA.art"), Artwork(1, flashes: 1));
        File.WriteAllText(Path.Combine(dir, "artB.art"), Artwork(2, flashes: 2));

        var result = GerberImport.Import(FilesIn(dir), _root, "foils", null, 1000,
            target: ImportTechnologyTarget.Use(techPath),
            resolveMapping: request => new ImportMappingAnswer(
                [.. request.Rows.Select(r => r.SourceName switch
                {
                    "artA" => r with { Choice = new(LayoutFragment.LayerReconciliationAction.MapToExisting, KeyOf("Bottom Copper")) },
                    "artB" => r with { Choice = new(LayoutFragment.LayerReconciliationAction.MapToExisting, KeyOf("Top Copper")) },
                    _ => r,
                })],
                request.Target));

        Assert.Null(result.Refusal);
        Assert.Contains(result.Messages, m => m.Contains("top to bottom, is now: artB.art, artA.art", StringComparison.Ordinal));
    }

    [Fact]
    public void AFatPenPlaneBesideItsLegend_IsOneRegion_AndTheLegendStaysStrokes()
    {
        var plane = NestedSquares(count: 20, stepUm: 200, penUm: 400);
        var legend = Enumerable.Range(0, 60)
            .Select(i => Stroke(-2_000 + i * 300, -12_000, -2_000 + i * 300 + 150, -12_000, 100)).ToList();
        var candidates = plane.Concat(legend).Select(s => new LayoutRasterFillCoalesce.Candidate(s, "k", "Bottom")).ToList();

        var plan = LayoutRasterFillCoalesce.Build(candidates, 100, perRegion: true);

        var replacement = Assert.Single(plan.Replacements);
        Assert.Single(replacement.Regions);
        Assert.Equal(plane.Count, replacement.Replaced.Count);
        Assert.DoesNotContain(replacement.Replaced, legend.Contains);
        Assert.True(LayoutRasterFillCoalesce.Build(candidates, 100).IsEmpty);   // the file-wide count alone does not
    }

    [Fact]
    public void ATraceDrawnAsManyStrokes_IsNotAPaintedRegion()
    {
        // Twenty segments, each five widths long, meeting end to end.
        var strokes = Enumerable.Range(0, 20).Select(i => Stroke(i * 750, 0, (i + 1) * 750, 0, 150)).ToList();

        var plan = LayoutRasterFillCoalesce.Build(
            [.. strokes.Select(s => new LayoutRasterFillCoalesce.Candidate(s, "k", "Top"))], 100, perRegion: true);

        Assert.True(plan.IsEmpty);
    }

    [Fact]
    public void ASilkscreenOfHundredsOfStrokes_IsNotCalledAPour()
    {
        string dir = Folder("silk");
        var sb = new System.Text.StringBuilder("%FSLAX46Y46*%\n%MOMM*%\n%ADD10C,0.150*%\nD10*\n");
        for (int i = 0; i < 250; i++) sb.Append($"X{i * 1_000_000}Y0D02*\nX{i * 1_000_000 + 400_000}Y0D01*\n");
        File.WriteAllText(Path.Combine(dir, "silk top.gbr"), sb.Append("M02*\n").ToString());

        var result = GerberImport.Import(FilesIn(dir), _root, "silk", null, 1000);

        Assert.DoesNotContain(result.Messages, m => m.Contains("vector-filled pour", StringComparison.Ordinal));
    }

    /// <summary>An edge-launch connector's centre pad, five times longer than wide: the same copper with no
    /// pin declared is a trace, so the declaration is what decides it.</summary>
    [Fact]
    public void ADeclaredComponentPad_IsNotAnalysedAsATrace()
    {
        RectShape Pad(string? pin) => new()
        {
            Layer = new LayerKey(1, 0), X1 = Um(0), Y1 = Um(-500), X2 = Um(5_000), Y2 = Um(500), Pin = pin,
        };
        LayoutShape Plane() => new RectShape { Layer = new LayerKey(2, 0), X1 = Um(-8_000), Y1 = Um(-8_000), X2 = Um(13_000), Y2 = Um(8_000) };
        var options = new TraceImpedanceOptions { Layers = [new LayerKey(1, 0)] };

        var undeclared = TraceImpedanceAnalysis.Analyze([Pad(null), Plane()], ImpedanceTech(), 1000, options);
        var declared = TraceImpedanceAnalysis.Analyze([Pad("J1,1"), Plane()], ImpedanceTech(), 1000, options);

        Assert.Single(undeclared.AllTraces);
        Assert.Empty(declared.AllTraces);
    }

    /// <summary>A layer the technology does not define (an import's paste stencil, say) is a row of the
    /// layout's own Layers list, and hiding it leaves the technology untouched
    /// (brief-layout-layers-panel.md R-lyr-2; it was a session-only row in the technology editor).</summary>
    [Fact]
    public void ALayerTheTechnologyLacks_CanBeHiddenInTheLayout_AndTheTechnologyIsUntouched()
    {
        string techPath = WorkspaceCreate.InstallTechnology(_root, TwoLayerId);
        var tech = TechPersistence.LoadFromFile(techPath);
        var paste = new LayerKey(11, 0);
        var view = new LayoutView { DbuPerMicron = 1000 };
        view.Shapes.Add(new RectShape { Layer = paste, X1 = 0, Y1 = 0, X2 = 1_000, Y2 = 1_000 });
        var vm = new LayoutEditorViewModel(view);
        vm.ApplyTechResolution(new TechResolution(tech, techPath, TechResolutionSource.WorkspaceDefault, []));

        var row = Assert.Single(vm.LayerRows, r => r.Key == paste);
        Assert.False(row.IsDefined);
        Assert.Same(tech, vm.DrawTechnology);                    // nothing hidden, nothing copied
        row.Visible = false;

        Assert.False(vm.DrawTechnology!.Layers.Single(l => l.Key == paste).Visible);
        Assert.DoesNotContain(tech.Layers, l => l.Key == paste);
        Assert.False(vm.IsDirty);
    }

    /// <summary>Picking the conductor the other end is on makes the ends trade places, so the boxes can still
    /// move a via one conductor at a time — and can never leave both ends on one.</summary>
    [Fact]
    public void AViaSpanningOneConductor_CannotBePickedInTheEditor_AndIsFlaggedByValidation()
    {
        string techPath = WorkspaceCreate.InstallTechnology(_root, TwoLayerId);
        var editor = new TechEditorViewModel(techPath, TechPersistence.LoadOwnFromFile(techPath));
        var via = editor.StackupLayers.First(r => r.Layer.Kind == StackupKind.Via);
        var (from, to) = (via.Layer.SpanFromLayer, via.Layer.SpanToLayer);

        via.SelectedSpanTo = from!;

        var after = editor.Working.Stackup.Layers.First(l => l.Name == via.Layer.Name);
        Assert.Equal((to, from), (after.SpanFromLayer, after.SpanToLayer));
        var tech = TechPersistence.LoadFromFile(techPath);
        var entry = tech.Stackup.Layers.First(l => l.Kind == StackupKind.Via);
        entry.SpanToLayer = entry.SpanFromLayer;
        Assert.Contains(TechValidation.Validate(tech), p => p.Contains("connects nothing", StringComparison.Ordinal));
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────────

    private static long Um(double um) => (long)Math.Round(um * LayoutUnits.DefaultDbuPerMicron);

    private static PathShape Stroke(double x0, double y0, double x1, double y1, double widthUm) => new()
    {
        Layer = new LayerKey(2, 0), Xy = [Um(x0), Um(y0), Um(x1), Um(y1)], Width = Um(widthUm), End = PathEndStyle.Round,
    };

    /// <summary>Closed squares, each <paramref name="stepUm"/> inside the last and stroked with a pen wider than
    /// the step, so every band overlaps the next — how a photoplot file paints a solid plane without a region.</summary>
    private static List<PathShape> NestedSquares(int count, double stepUm, double penUm)
    {
        var squares = new List<PathShape>();
        for (int i = 0; i < count; i++)
        {
            double h = 5_000 - i * stepUm;
            if (h <= 0) break;
            squares.Add(new PathShape
            {
                Layer = new LayerKey(2, 0), Width = Um(penUm), End = PathEndStyle.Round,
                Xy = [Um(-h), Um(-h), Um(h), Um(-h), Um(h), Um(h), Um(-h), Um(h), Um(-h), Um(-h)],
            });
        }
        return squares;
    }

    private static Technology ImpedanceTech() => new()
    {
        Name = "r18",
        Layers =
        [
            new LayerDef { Key = new LayerKey(1, 0), Name = "Top", Purpose = "conductor" },
            new LayerDef { Key = new LayerKey(2, 0), Name = "Plane", Purpose = "conductor" },
        ],
        Stackup = new Stackup
        {
            Top = BoundaryCondition.Open,
            Bottom = BoundaryCondition.Open,
            Layers =
            [
                new StackupLayer { Kind = StackupKind.Conductor, Name = "Top", ThicknessDbu = Um(35), SigmaSm = 5.8e7, DrawingLayers = [new LayerKey(1, 0)] },
                new StackupLayer { Kind = StackupKind.Dielectric, Name = "Core", ThicknessDbu = Um(500), Epsr = 4.4 },
                new StackupLayer { Kind = StackupKind.Conductor, Name = "Plane", ThicknessDbu = Um(35), SigmaSm = 5.8e7, DrawingLayers = [new LayerKey(2, 0)] },
            ],
        },
    };

    /// <summary>Artwork with no <c>%TF.FileFunction</c>: the name is the only evidence.</summary>
    private static string Artwork(double xMm, int flashes = 1) =>
        "%FSLAX46Y46*%\n%MOMM*%\n%ADD10C,0.400*%\nD10*\n" +
        string.Concat(Enumerable.Range(0, flashes).Select(k =>
            $"X{(long)Math.Round(xMm * 1_000_000)}Y{1_000_000 + k * 1_000_000}D03*\n")) +
        "M02*\n";

    private string Folder(string name)
    {
        string dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static IReadOnlyList<string> FilesIn(string dir) =>
        [.. Directory.EnumerateFiles(dir).OrderBy(p => p, StringComparer.Ordinal)];
}

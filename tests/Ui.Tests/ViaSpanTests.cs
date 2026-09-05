using System.Linq;
using CircuitRF.Ui.Layout;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.Theming;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// <b>A via's span is the technology's to state, and every consumer must ask the same question of the
/// same place.</b>
///
/// <para>The span has lived on the <see cref="StackupKind.Via"/> stackup entry since the via primitive
/// landed (R-via-3: <c>SpanFromLayer</c>/<c>SpanToLayer</c>), and <c>DrcConnectivity</c> and
/// <c>PlanarExtractor.BuildVias</c> have both read it. The interchange writers had not — each invented
/// its own answer instead, and the two inventions were both wrong in ways that reach a fab:</para>
/// <list type="bullet">
///   <item><c>PcbWriter</c> wrote every via from the pad's own copper to the OPPOSITE OUTER copper, so
///   a blind or buried via left circuitRF as a hole drilled clean through the board.</item>
///   <item><c>GdsiiWriter</c>/<c>DxfWriter</c>/<c>GerberExport</c> keyed the pad off the per-shape
///   <see cref="ViaShape.LandingLayer"/>, which <c>CommitViaPlacement</c> has never set — so every via
///   drawn in the editor lost its pad (GDSII/DXF) or flashed it into the DRILL file (Gerber).</item>
/// </list>
/// <para>These gate the shared <see cref="ViaSpanResolver"/> and each consumer of it.</para>
/// </summary>
public class ViaSpanTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("via-span-test-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;
    private static readonly LayerKey TopCu = new(1, 0);
    private static readonly LayerKey In1Cu = new(2, 0);
    private static readonly LayerKey BotCu = new(3, 0);
    private static readonly LayerKey PthDrill = new(7, 0);
    private static readonly LayerKey BlindDrill = new(8, 0);

    /// <summary>Three conductors and TWO via entries on separate drawing layers: a through via
    /// (Top → Bottom) and a blind one (Top → Inner 1). Separate drawing layers is the whole mechanism —
    /// the layer a via is drawn on is what selects its entry, and therefore its span.</summary>
    private static Technology ThreeLayerTech()
        => new()
        {
            Name = "T",
            Layers =
            [
                new LayerDef { Key = TopCu,      Name = "Top Copper",    Color = new Rgba(0xC8, 0x7A, 0x3E) },
                new LayerDef { Key = In1Cu,      Name = "Inner 1",       Color = new Rgba(0xA0, 0x60, 0x30) },
                new LayerDef { Key = BotCu,      Name = "Bottom Copper", Color = new Rgba(0x8A, 0x50, 0x28) },
                new LayerDef { Key = PthDrill,   Name = "PTH Drill",     Color = new Rgba(0x20, 0x20, 0x20) },
                new LayerDef { Key = BlindDrill, Name = "Blind Drill",   Color = new Rgba(0x30, 0x30, 0x30) },
            ],
            Stackup = new Stackup
            {
                Layers =
                [
                    new StackupLayer { Kind = StackupKind.Conductor,  Name = "Top",    ThicknessDbu = 35_000, SigmaSm = 5.8e7, DrawingLayers = [TopCu] },
                    new StackupLayer { Kind = StackupKind.Dielectric, Name = "Prepreg", ThicknessDbu = 200_000, Epsr = 4.4 },
                    new StackupLayer { Kind = StackupKind.Conductor,  Name = "Inner",  ThicknessDbu = 35_000, SigmaSm = 5.8e7, DrawingLayers = [In1Cu] },
                    new StackupLayer { Kind = StackupKind.Dielectric, Name = "Core",    ThicknessDbu = 1_200_000, Epsr = 4.4 },
                    new StackupLayer { Kind = StackupKind.Conductor,  Name = "Bottom", ThicknessDbu = 35_000, SigmaSm = 5.8e7, DrawingLayers = [BotCu] },
                    new StackupLayer { Kind = StackupKind.Via, Name = "PTH",   DrawingLayers = [PthDrill],
                                       SpanFromLayer = "Top", SpanToLayer = "Bottom" },
                    new StackupLayer { Kind = StackupKind.Via, Name = "Blind", DrawingLayers = [BlindDrill],
                                       SpanFromLayer = "Top", SpanToLayer = "Inner" },
                ],
            },
        };

    private string CreateCell(string name, Action<LayoutView> populate)
    {
        var cellDir = CellFolder.CreateCellFolder(_dir, name);
        var layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        var view = new LayoutView { DbuPerMicron = Dbu };
        populate(view);
        LayoutPersistence.SaveToFile(Path.Combine(layoutDir, $"{name}.clay"), view);

        var ccellPath = Path.Combine(cellDir, CellFolder.CcellFileName);
        var ccell = CellPersistence.LoadFromFile(ccellPath);
        ccell.PrimaryLayout = $"{name}.clay";
        CellPersistence.SaveToFile(ccellPath, ccell);
        return cellDir;
    }

    /// <summary>A via exactly as the editor's own Via tool commits one: layer, position, pad, drill —
    /// and NO <see cref="ViaShape.LandingLayer"/>, which is the state every writer below has to
    /// handle because it is the state every hand-drawn via is in.</summary>
    private static ViaShape EditorVia(LayerKey layer)
        => new() { Layer = layer, X = 1_000_000, Y = -2_000_000, PadSize = 800_000, DrillSize = 400_000 };

    // ── The resolver itself ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheSpanComesFromTheStackupEntryTheDrawingLayerSelects()
    {
        var tech = ThreeLayerTech();

        var through = ViaSpanResolver.Resolve(PthDrill, tech);
        Assert.NotNull(through);
        Assert.Equal("Top", through!.Top.Name);
        Assert.Equal("Bottom", through.Bottom.Name);
        Assert.True(ViaSpanResolver.IsThrough(through, tech));

        var blind = ViaSpanResolver.Resolve(BlindDrill, tech);
        Assert.NotNull(blind);
        Assert.Equal("Top", blind!.Top.Name);
        Assert.Equal("Inner", blind.Bottom.Name);
        Assert.False(ViaSpanResolver.IsThrough(blind, tech));
    }

    /// <summary><c>SpanFromLayer</c>/<c>SpanToLayer</c> carry no ordering promise — a hand-authored
    /// technology may name them either way round — so the resolver takes the direction from the
    /// stackup's own top-to-bottom order rather than from which field said what.</summary>
    [Fact]
    public void SpanFromAndSpanTo_AreOrderedByTheStackup_NotByWhichFieldNamedWhich()
    {
        var tech = ThreeLayerTech();
        var entry = tech.Stackup.Layers.Single(l => l.Name == "Blind");
        (entry.SpanFromLayer, entry.SpanToLayer) = (entry.SpanToLayer, entry.SpanFromLayer);

        var span = ViaSpanResolver.Resolve(BlindDrill, tech);
        Assert.Equal("Top", span!.Top.Name);
        Assert.Equal("Inner", span.Bottom.Name);
    }

    [Fact]
    public void AViaOnANonViaLayer_ResolvesNothing_AndTheExplanationNamesTheLayersThatWouldWork()
    {
        var tech = ThreeLayerTech();
        Assert.Null(ViaSpanResolver.Resolve(TopCu, tech));

        string why = ViaSpanResolver.Explain(TopCu, tech)!;
        Assert.Contains("Top Copper", why, StringComparison.Ordinal);
        Assert.Contains("PTH Drill", why, StringComparison.Ordinal);
        Assert.Contains("Blind Drill", why, StringComparison.Ordinal);
    }

    [Fact]
    public void AViaEntryWithNoSpan_ExplainsThatRatherThanTheLayerBinding()
    {
        var tech = ThreeLayerTech();
        var entry = tech.Stackup.Layers.Single(l => l.Name == "Blind");
        entry.SpanFromLayer = entry.SpanToLayer = null;

        Assert.Null(ViaSpanResolver.Resolve(BlindDrill, tech));
        Assert.Contains("names no Spans conductors", ViaSpanResolver.Explain(BlindDrill, tech)!, StringComparison.Ordinal);
    }

    // ── Board format: the real span, in both directions ─────────────────────────────────────────

    private string ExportBoard(string cellDir, Technology? tech, out PcbExport.ExportPlan plan)
    {
        plan = PcbExport.Analyze(cellDir, tech, Dbu);
        var path = Path.Combine(_dir, "out.kicad_pcb");
        PcbExport.Write(path, plan);
        return File.ReadAllText(path);
    }

    /// <summary><b>The bug this whole change exists for.</b> Before it, this via was written
    /// <c>(layers "F.Cu" "B.Cu")</c> — a hole drilled clean through the board — with nothing said.</summary>
    [Fact]
    public void ABlindVia_WritesItsRealSpan_AndSaysItIsBlind()
    {
        var cellDir = CreateCell("BOARD", v => v.Shapes.Add(EditorVia(BlindDrill)));
        string text = ExportBoard(cellDir, ThreeLayerTech(), out var plan);

        Assert.Contains("(layers \"F.Cu\" \"In1.Cu\")", text);
        Assert.DoesNotContain("(layers \"F.Cu\" \"B.Cu\")", text);
        Assert.Contains("(via blind ", text);

        Assert.Equal(1, plan.Summary.BlindOrBuriedVias);
        Assert.Equal(0, plan.Summary.UnspannedVias);
    }

    [Fact]
    public void AThroughVia_StillWritesTheOuterPair_AndCarriesNoKindWord()
    {
        var cellDir = CreateCell("BOARD", v => v.Shapes.Add(EditorVia(PthDrill)));
        string text = ExportBoard(cellDir, ThreeLayerTech(), out var plan);

        Assert.Contains("(layers \"F.Cu\" \"B.Cu\")", text);
        Assert.DoesNotContain("(via blind ", text);
        Assert.Equal(0, plan.Summary.BlindOrBuriedVias);
    }

    /// <summary>The written blind via comes back through <c>PcbReader</c> carrying the SPAN, not merely
    /// the kind word — the far half of the cycle, and the thing brief-via-span-import.md §2(a) exists to
    /// close. Until it landed, <c>ReadVia</c> identified the span correctly and then dropped it.</summary>
    [Fact]
    public void AWrittenBlindVia_IsReadBackWithItsSpan_NotJustItsKindWord()
    {
        var cellDir = CreateCell("BOARD", v => v.Shapes.Add(EditorVia(BlindDrill)));
        string text = ExportBoard(cellDir, ThreeLayerTech(), out _);

        var read = PcbReader.Read(text, Dbu);
        Assert.Null(read.Refusal);

        // The names are the BOARD-format ones the writer chose for those two conductors (PcbLayerNaming),
        // not circuitRF's own layer names — which is exactly right: they are what the file says, and what
        // its own entities reference.
        Assert.Contains("(layers \"F.Cu\" \"In1.Cu\")", text);
        var via = Assert.Single(read.Board!.Shapes, sh => sh.Shape is ViaShape);
        Assert.Equal("F.Cu", via.SpanFromName);
        Assert.Equal("In1.Cu", via.SpanToName);

        // Nothing was degraded: the file said blind, the pair says blind, and both survived the read.
        Assert.Empty(read.Board.DegradedCounts);
    }

    /// <summary>A technology that states no span still writes a via — a written via must name SOME
    /// span — but as a through via, reported. Silence here is what put a blind via on a board.</summary>
    [Fact]
    public void AViaWhoseSpanNothingStates_IsWrittenThrough_AndReported()
    {
        var tech = ThreeLayerTech();
        tech.Stackup.Layers.RemoveAll(l => l.Kind == StackupKind.Via);

        var cellDir = CreateCell("BOARD", v => v.Shapes.Add(EditorVia(BlindDrill)));
        string text = ExportBoard(cellDir, tech, out var plan);

        Assert.Contains("(layers \"F.Cu\" \"B.Cu\")", text);
        Assert.Equal(1, plan.Summary.UnspannedVias);
        Assert.False(plan.HasNothingToReport);
        Assert.Contains(PcbExport.Describe(plan), m => m.Contains("THROUGH", StringComparison.Ordinal));
    }

    // ── The pad layer: the same root cause, three more writers ──────────────────────────────────

    /// <summary>The editor's Via tool sets no <see cref="ViaShape.LandingLayer"/>, so before this the
    /// pad was skipped outright and the via exported as a bare barrel — no annular ring at all.</summary>
    [Fact]
    public void Gdsii_AnEditorDrawnVia_GetsItsPadFromTheSpan_NotFromAnUnsetLandingLayer()
    {
        var cellDir = CreateCell("TOP", v => v.Shapes.Add(EditorVia(BlindDrill)));
        var plan = GdsiiExport.Analyze(cellDir, ThreeLayerTech(), Dbu);
        Assert.Equal(0, plan.ViaPadsSkipped);

        var outPath = Path.Combine(_dir, "out.gds");
        GdsiiExport.Write(outPath, plan);

        using var stream = File.OpenRead(outPath);
        var top = GdsiiReader.Open(stream).ReadStructures().Single(s => s.Name == "TOP");
        Assert.Contains(top.Shapes, s => s.Layer == BlindDrill); // barrel
        Assert.Contains(top.Shapes, s => s.Layer == TopCu);      // pad, on the span's TOP conductor
    }

    /// <summary>Gerber's failure was worse than a missing pad: the flash went into the DRILL layer's
    /// own file, which is copper etched where the annular ring belongs — the exact fabrication bug
    /// L4h's round trip identified, fixed then only for vias that came from an import.</summary>
    [Fact]
    public void Gerber_AnEditorDrawnViaPad_LandsInTheCopperFile_NotTheDrillFile()
    {
        var tech = ThreeLayerTech();
        var cellDir = CreateCell("TOP", v => v.Shapes.Add(EditorVia(BlindDrill)));
        var view = LayoutPersistence.LoadFromFile(Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Layout), "TOP.clay"));

        var plan = GerberExport.Analyze(cellDir, tech, Dbu, view, null);
        Assert.Equal(0, plan.UnspannedViaPads);

        var result = GerberExport.Write(Path.Combine(_dir, "gbr"), "TOP", plan);
        var gerbers = result.FilesWritten.Where(f => !f.EndsWith(".drl", StringComparison.Ordinal)
                                                  && !f.EndsWith(".gbrjob", StringComparison.Ordinal)).ToList();

        // Exactly one Gerber file, and it is the one carrying the pad's copper. The failure this gates
        // is a SECOND file, for the drill layer, holding a flash — copper etched where the hole goes.
        string only = Assert.Single(gerbers);
        Assert.Contains("D03*", File.ReadAllText(only), StringComparison.Ordinal); // the pad flash itself
        Assert.DoesNotContain("Blind", Path.GetFileName(only), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AViaWithNoResolvablePadLayer_IsStillCountedRatherThanSilent()
    {
        var tech = ThreeLayerTech();
        tech.Stackup.Layers.RemoveAll(l => l.Kind == StackupKind.Via);

        var cellDir = CreateCell("TOP", v => v.Shapes.Add(EditorVia(BlindDrill)));
        var view = LayoutPersistence.LoadFromFile(Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Layout), "TOP.clay"));

        var plan = GerberExport.Analyze(cellDir, tech, Dbu, view, null);
        Assert.Equal(1, plan.UnspannedViaPads);
        Assert.False(plan.HasNothingToReport);
    }

    /// <summary>An importer's own statement about a file it read must not be overridden by the
    /// technology's default.</summary>
    [Fact]
    public void AnExplicitLandingLayer_StillWins()
    {
        var via = EditorVia(BlindDrill);
        via.LandingLayer = BotCu;
        Assert.Equal(BotCu, ViaSpanResolver.PadLayer(via, ThreeLayerTech()));
    }

    // ── The editor ──────────────────────────────────────────────────────────────────────────────

    /// <summary>A via on a layer no via entry claims has no span, so it is inert in DRC, in EM and in
    /// every export — it draws perfectly and does nothing. Refusing beats placing one.</summary>
    [Fact]
    public void TheViaTool_RefusesOnALayerNoViaEntryClaims_WithAReasonNamingTheOnesThatWork()
    {
        var vm = new LayoutEditorViewModel(new LayoutView { DbuPerMicron = Dbu })
        {
            Technology = ThreeLayerTech(),
        };
        vm.CurrentLayerKey = In1Cu;

        Assert.False(vm.ViaToolAvailability.CanExecute);
        Assert.Contains("PTH Drill", vm.ViaToolAvailability.DisabledReason!, StringComparison.Ordinal);
    }

    /// <summary>Two via entries means the layer choice IS the span choice, so arming the tool must not
    /// pick one — but it must not leave the user stuck either, which is what the reason is for.</summary>
    [Fact]
    public void ArmingTheViaTool_DoesNotChooseBetweenTwoViaLayers()
    {
        var vm = new LayoutEditorViewModel(new LayoutView { DbuPerMicron = Dbu })
        {
            Technology = ThreeLayerTech(),
        };
        vm.CurrentLayerKey = In1Cu;
        vm.ActiveTool = LayoutEditorViewModel.Tool.Via;

        Assert.Equal(In1Cu, vm.CurrentLayerKey);
        Assert.False(vm.ViaToolAvailability.CanExecute);
    }

    /// <summary>One via entry is no choice at all, so arming the tool moves to it — the ordinary
    /// two-layer board keeps working with no extra step.</summary>
    [Fact]
    public void ArmingTheViaTool_MovesToTheSoleViaLayer()
    {
        var tech = ThreeLayerTech();
        tech.Stackup.Layers.RemoveAll(l => l.Name == "Blind");

        var vm = new LayoutEditorViewModel(new LayoutView { DbuPerMicron = Dbu }) { Technology = tech };
        vm.CurrentLayerKey = In1Cu;
        vm.ActiveTool = LayoutEditorViewModel.Tool.Via;

        Assert.Equal(PthDrill, vm.CurrentLayerKey);
        Assert.True(vm.ViaToolAvailability.CanExecute);
    }

    [Fact]
    public void ArmingTheViaTool_LeavesAViaLayerTheUserAlreadyPicked()
    {
        var vm = new LayoutEditorViewModel(new LayoutView { DbuPerMicron = Dbu }) { Technology = ThreeLayerTech() };
        vm.CurrentLayerKey = BlindDrill;
        vm.ActiveTool = LayoutEditorViewModel.Tool.Via;

        Assert.Equal(BlindDrill, vm.CurrentLayerKey);
        Assert.True(vm.ViaToolAvailability.CanExecute);
    }

    // ── The inspector ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheInspector_ShowsTheSpanOfTheSelectedVia()
    {
        var model = new LayoutView { DbuPerMicron = Dbu };
        model.Shapes.Add(EditorVia(BlindDrill));
        var vm = new LayoutEditorViewModel(model) { Technology = ThreeLayerTech() };
        var props = new LayoutShapePropertiesViewModel();
        props.SetContext(vm);
        vm.SelectAllCommand.Execute(null); // the model holds exactly this one shape

        Assert.True(props.ShowVia);
        Assert.Equal("Top → Inner", props.ViaSpanText);
        Assert.False(props.ViaSpanIsProblem);
    }

    [Fact]
    public void TheInspector_SaysSoWhenTheSelectedViaResolvesNoSpan()
    {
        var model = new LayoutView { DbuPerMicron = Dbu };
        model.Shapes.Add(EditorVia(TopCu));
        var vm = new LayoutEditorViewModel(model) { Technology = ThreeLayerTech() };
        var props = new LayoutShapePropertiesViewModel();
        props.SetContext(vm);
        vm.SelectAllCommand.Execute(null); // the model holds exactly this one shape

        Assert.True(props.ViaSpanIsProblem);
        Assert.Contains("not bound to any via entry", props.ViaSpanText, StringComparison.Ordinal);
    }
}

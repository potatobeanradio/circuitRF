using CircuitRF.Ui.Layout;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// The board-format WRITER (L4d follow-up). Import-only was L4d's own scope; this adds the outward
/// direction, deliberately at one epoch (<see cref="PcbLayerNaming.TargetVersion"/>) rather than the
/// version-agnostic dispatch the reader must do.
///
/// <para><b>The oracle here is honest but narrower than L4b's or L4a's, and that has to be said out
/// loud.</b> Those phases validated against an independent third-party implementation. No board tool is
/// installed in this environment, so what these tests prove is that the write is SELF-CONSISTENT —
/// every one of them re-reads the written file through <c>PcbReader</c>, which was itself written from
/// four measured real files and is therefore not a mirror of the writer's own assumptions, but is still
/// this repo's code. Before trusting an exported board for fabrication, open one.</para>
/// </summary>
public class PcbExportTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pcb-export-test-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;
    private static readonly LayerKey TopCu = new(1, 0);
    private static readonly LayerKey BotCu = new(2, 0);
    private static readonly LayerKey SilkTop = new(5, 0);
    private static readonly LayerKey Drill = new(7, 0);

    /// <summary>Two copper layers bound to the stackup, one silkscreen layer with an explicit board
    /// layer name, and one layer with nothing saying where it belongs.</summary>
    private static Technology Tech()
    {
        var tech = new Technology
        {
            Name = "T",
            Layers =
            [
                new LayerDef { Key = TopCu, Name = "Top Copper", Color = new Rgba(0xC8, 0x7A, 0x3E) },
                new LayerDef { Key = BotCu, Name = "Bottom Copper", Color = new Rgba(0x8A, 0x50, 0x28) },
                new LayerDef { Key = SilkTop, Name = "Silk Top", Color = new Rgba(0xF2, 0xF2, 0xF2),
                               Interchange = new InterchangeMapping(null, null, null, null, null, "F.SilkS") },
                new LayerDef { Key = Drill, Name = "Drill", Color = new Rgba(0x20, 0x20, 0x20) },
            ],
            Stackup = new Stackup
            {
                Layers =
                [
                    new StackupLayer { Kind = StackupKind.Conductor, Name = "Top", ThicknessDbu = 35_000, SigmaSm = 5.8e7, DrawingLayers = [TopCu] },
                    new StackupLayer { Kind = StackupKind.Dielectric, Name = "Core", ThicknessDbu = 1_510_000, Epsr = 4.5, TanD = 0.02 },
                    new StackupLayer { Kind = StackupKind.Conductor, Name = "Bottom", ThicknessDbu = 70_000, SigmaSm = 5.8e7, DrawingLayers = [BotCu] },
                ],
            },
        };
        return tech;
    }

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

    private (PcbExport.ExportPlan Plan, string Text) ExportOf(string cellDir, Technology? tech = null)
    {
        var plan = PcbExport.Analyze(cellDir, tech ?? Tech(), Dbu);
        var path = Path.Combine(_dir, "out.kicad_pcb");
        PcbExport.Write(path, plan);
        return (plan, File.ReadAllText(path));
    }

    /// <summary>Re-reads the written file through the reader L4d built from four measured real files.</summary>
    private static PcbBoard ReadBack(string text)
    {
        var read = PcbReader.Read(text, Dbu);
        Assert.Null(read.Refusal);
        return read.Board!;
    }

    // ── Handedness and units, both directions ───────────────────────────────────────────────────

    /// <summary>
    /// The Y flip has to happen exactly once on the way out too, and an L asymmetric on BOTH axes is
    /// the only geometry that can tell a correct write from a mirrored one — the same WB-C lesson the
    /// import gate rests on, applied in reverse.
    /// </summary>
    [Fact]
    public void AnAsymmetricOutline_SurvivesAWriteAndReReadUnchanged()
    {
        long[] xy = [0, 0, 10_000_000, 0, 10_000_000, -2_000_000, 2_000_000, -2_000_000, 2_000_000, -6_000_000, 0, -6_000_000];
        var cellDir = CreateCell("BOARD", v => v.Shapes.Add(new PolygonShape { Layer = TopCu, Xy = xy, Net = "GND" }));

        var (_, text) = ExportOf(cellDir);
        var poly = Assert.Single(ReadBack(text).Shapes.Select(s => s.Shape).OfType<PolygonShape>());

        Assert.Equal(xy, poly.Xy);
        Assert.Equal("GND", poly.Net);
    }

    [Fact]
    public void AMirroredWrite_WouldFailThatAssertion()
    {
        long[] xy = [0, 0, 10_000_000, 0, 10_000_000, -2_000_000, 2_000_000, -2_000_000, 2_000_000, -6_000_000, 0, -6_000_000];
        var cellDir = CreateCell("BOARD", v => v.Shapes.Add(new PolygonShape { Layer = TopCu, Xy = xy }));
        var (_, text) = ExportOf(cellDir);
        var poly = Assert.Single(ReadBack(text).Shapes.Select(s => s.Shape).OfType<PolygonShape>());

        long[] mirrored = [.. xy.Select((v, i) => i % 2 == 1 ? -v : v)];
        Assert.NotEqual(mirrored, poly.Xy);
    }

    /// <summary>At 1000 DBU/µm one DBU is one nanometre and six decimal millimetre places represent it
    /// exactly, so this is the one interchange path in this repo that is lossless in both directions —
    /// asserted on the negative side, where a truncating conversion would show.</summary>
    [Fact]
    public void CoordinatesRoundTripExactly_IncludingTheNegativeCase()
    {
        var cellDir = CreateCell("BOARD", v => v.Shapes.Add(new PathShape
        {
            Layer = TopCu, Width = 200_000, End = PathEndStyle.Round,
            Xy = [-12_345_600, 12_345_600, 100, -100],
        }));

        var (_, text) = ExportOf(cellDir);
        Assert.Contains("-12.3456", text);

        var track = Assert.Single(ReadBack(text).Shapes.Select(s => s.Shape).OfType<PathShape>());
        Assert.Equal(-12_345_600, track.Xy[0]);
        Assert.Equal(12_345_600, track.Xy[1]);
        Assert.Equal(100, track.Xy[2]);
        Assert.Equal(-100, track.Xy[3]);
        Assert.Equal(200_000, track.Width);
    }

    [Fact]
    public void NoNumberIsWrittenInExponentNotation()
    {
        // A very small and a very large coordinate together — the two ends where a default double
        // format switches to "1E-06" and produces a file the format cannot express.
        var cellDir = CreateCell("BOARD", v => v.Shapes.Add(new PathShape
        {
            Layer = TopCu, Width = 1, Xy = [1, 1, 900_000_000, 900_000_000],
        }));
        var (_, text) = ExportOf(cellDir);
        Assert.DoesNotContain("E-", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("E+", text, StringComparison.OrdinalIgnoreCase);
    }

    // ── Design rules: none, on purpose ──────────────────────────────────────────────────────────

    /// <summary>
    /// The user's own question, pinned. Design rules left this format's board file at the 20211014
    /// epoch (measured), and circuitRF's rule model is per-layer process geometry rather than
    /// per-net-class routing constraints — only minimum width and spacing have any counterpart, and
    /// there are no net classes to attach them to. So none are written, and the report SAYS none were
    /// written rather than leaving the user to assume they carried.
    /// </summary>
    [Fact]
    public void NoDesignRulesOrNetClassesAreWritten_AndTheReportSaysSo()
    {
        var tech = Tech();
        tech.DrcRules.Add(new DrcRule { Name = "min width", Kind = DrcRuleKind.MinWidth, Layer = TopCu, ValueDbu = 150_000 });
        tech.DrcRules.Add(new DrcRule { Name = "min space", Kind = DrcRuleKind.MinSpacing, Layer = TopCu, ValueDbu = 150_000 });

        var cellDir = CreateCell("BOARD", v => v.Shapes.Add(new PathShape { Layer = TopCu, Width = 200_000, Xy = [0, 0, 1_000_000, 0] }));
        var (plan, text) = ExportOf(cellDir, tech);

        Assert.DoesNotContain("net_class", text);
        Assert.DoesNotContain("trace_min", text);
        Assert.DoesNotContain("clearance", text);
        Assert.DoesNotContain("via_min", text);

        var note = Assert.Single(PcbExport.Describe(plan), m => m.Contains("DRC rule"));
        Assert.Contains("2 DRC rule(s) were NOT written", note);
        Assert.Contains("no net classes", note);
    }

    [Fact]
    public void NoPlotOrProjectConfigurationIsInvented()
    {
        var cellDir = CreateCell("BOARD", v => v.Shapes.Add(new PathShape { Layer = TopCu, Width = 200_000, Xy = [0, 0, 1_000_000, 0] }));
        var (_, text) = ExportOf(cellDir);
        Assert.DoesNotContain("pcbplotparams", text);
        Assert.DoesNotContain("aux_axis_origin", text);
    }

    // ── The stackup, which is the reason any of this is worth doing ─────────────────────────────

    [Fact]
    public void TheStackupRoundTrips_ValuesAndTopToBottomOrder()
    {
        var cellDir = CreateCell("BOARD", v => v.Shapes.Add(new PathShape { Layer = TopCu, Width = 200_000, Xy = [0, 0, 1_000_000, 0] }));
        var (_, text) = ExportOf(cellDir);

        var back = ReadBack(text);
        var stack = PcbStackupMapping.Build(back.Stackup, back.OverallThicknessMm, Dbu, _ => null).Stackup!;

        Assert.Equal([StackupKind.Conductor, StackupKind.Dielectric, StackupKind.Conductor], stack.Layers.Select(l => l.Kind));
        Assert.Equal(35_000, stack.Layers[0].ThicknessDbu);
        Assert.Equal(1_510_000, stack.Layers[1].ThicknessDbu);
        Assert.Equal(70_000, stack.Layers[2].ThicknessDbu);   // asymmetric, so a swapped pair shows
        Assert.Equal(4.5, stack.Layers[1].Epsr);
        Assert.Equal(0.02, stack.Layers[1].TanD);
    }

    /// <summary>The export-side counterpart of R-L4d-6: a technology with no stackup writes no stackup
    /// section, rather than a plausible default nothing downstream would question.</summary>
    [Fact]
    public void ATechnologyWithNoStackup_WritesNoStackupSection()
    {
        var tech = Tech();
        tech.Stackup = new Stackup();
        var cellDir = CreateCell("BOARD", v => v.Shapes.Add(new PolygonShape { Layer = TopCu, Xy = [0, 0, 1_000_000, 0, 1_000_000, -1_000_000] }));
        var (_, text) = ExportOf(cellDir, tech);

        Assert.DoesNotContain("stackup", text);
        Assert.DoesNotContain("epsilon_r", text);
    }

    // ── Copper, nets and vias ───────────────────────────────────────────────────────────────────

    /// <summary>A copper region is written as a filled zone specifically so it CARRIES ITS NET — no
    /// top-level graphic in any measured real board carries one. The trade is stated in the report.</summary>
    [Fact]
    public void ACopperRegion_BecomesAZoneCarryingItsNet_AndTheCostIsReported()
    {
        var cellDir = CreateCell("BOARD", v => v.Shapes.Add(new RectShape
        { Layer = TopCu, X1 = 0, Y1 = -2_000_000, X2 = 4_000_000, Y2 = 0, Net = "VDD" }));

        var (plan, text) = ExportOf(cellDir);
        Assert.Contains("(zone", text);
        Assert.Contains("filled_polygon", text);

        var back = ReadBack(text);
        var fill = Assert.Single(back.Shapes.Select(s => s.Shape).OfType<PolygonShape>());
        Assert.Equal("VDD", fill.Net);
        Assert.Equal(4_000_000, fill.Xy.Where((_, i) => i % 2 == 0).Max() - fill.Xy.Where((_, i) => i % 2 == 0).Min());

        Assert.Single(PcbExport.Describe(plan), m => m.Contains("Refilling zones"));
    }

    [Fact]
    public void NetZero_IsWrittenAsTheUnassignedNet_AndComesBackNull()
    {
        var cellDir = CreateCell("BOARD", v =>
        {
            v.Shapes.Add(new PathShape { Layer = TopCu, Width = 200_000, Xy = [0, 0, 1_000_000, 0], Net = "SIG" });
            v.Shapes.Add(new PathShape { Layer = TopCu, Width = 200_000, Xy = [0, -1_000_000, 1_000_000, -1_000_000] });
        });

        var (_, text) = ExportOf(cellDir);
        Assert.Contains("(net 0 \"\")", text);

        var tracks = ReadBack(text).Shapes.Select(s => s.Shape).OfType<PathShape>().OrderBy(p => -p.Xy[1]).ToList();
        Assert.Equal("SIG", tracks[0].Net);
        Assert.Null(tracks[1].Net);
    }

    /// <summary>R-L4d-10 in the outward direction: the span is named by the PAD's layer, and the hole is
    /// the drill value rather than a layer — this format has no drill layer at all.</summary>
    [Fact]
    public void AVia_WritesItsSpanFromTheLandingLayer_AndItsHoleAsADrill()
    {
        var cellDir = CreateCell("BOARD", v => v.Shapes.Add(new ViaShape
        { Layer = Drill, LandingLayer = TopCu, X = 3_000_000, Y = -4_000_000, PadSize = 800_000, DrillSize = 400_000, Net = "GND" }));

        var (_, text) = ExportOf(cellDir);
        Assert.Contains("(layers \"F.Cu\" \"B.Cu\")", text);

        var via = Assert.Single(ReadBack(text).Shapes.Select(s => s.Shape).OfType<ViaShape>());
        Assert.Equal(3_000_000, via.X);
        Assert.Equal(-4_000_000, via.Y);
        Assert.Equal(800_000, via.PadSize);
        Assert.Equal(400_000, via.DrillSize);
        Assert.Equal("GND", via.Net);
    }

    // ── Footprints ──────────────────────────────────────────────────────────────────────────────

    private string CreatePartCell()
        => CreateCell("PART", v =>
        {
            v.Shapes.Add(new RectShape { Layer = TopCu, X1 = -1_600_000, Y1 = -400_000, X2 = -400_000, Y2 = 400_000, Net = "A" });
            v.Shapes.Add(new RectShape { Layer = TopCu, X1 = 400_000, Y1 = -400_000, X2 = 1_600_000, Y2 = 400_000, Net = "B" });
            v.Shapes.Add(new PathShape { Layer = SilkTop, Width = 120_000, Xy = [-1_500_000, 1_000_000, 1_500_000, 1_000_000] });
            v.Pins.Add(new LayoutPin { Name = "1", X = -1_000_000, Y = 0, WidthDbu = 800_000, Layer = TopCu });
            v.Pins.Add(new LayoutPin { Name = "2", X = 1_000_000, Y = 0, WidthDbu = 800_000, Layer = TopCu });
        });

    /// <summary>
    /// A pin claims the copper it sits inside, and THAT becomes the pad. Without this a pad would
    /// either be invented from the pin's bare width (losing the real artwork) or duplicated as both a
    /// pad and a graphic on the same copper.
    /// </summary>
    [Fact]
    public void EachPinClaimsItsOwnCopper_AndBecomesAPadOfThatShape()
    {
        var partDir = CreatePartCell();
        var boardDir = CreateCell("BOARD", v => v.Instances.Add(new LayoutInstance
        {
            CellRef = Path.GetRelativePath(CellFolder.SubFolderPath(Path.Combine(_dir, "BOARD"), ViewType.Layout), partDir),
            X = 10_000_000, Y = -10_000_000, RotationDegrees = 0,
        }));

        var (plan, text) = ExportOf(boardDir);
        Assert.Equal(1, plan.Summary.Footprints);
        Assert.Equal(2, plan.Summary.PadsFromPins);
        Assert.Equal(0, plan.Summary.PinsWithNoArtwork);

        var back = ReadBack(text);
        var cell = Assert.Single(back.FootprintCells.Values);
        Assert.Equal(2, cell.Pins.Count);
        Assert.Equal(["1", "2"], cell.Pins.Select(p => p.Pin.Name));

        // The pads came back as the 1.2 x 0.8 mm rectangles they are, not as the pin's bare width.
        var pads = cell.Shapes.Select(s => s.Shape).OfType<RectShape>().ToList();
        Assert.Equal(2, pads.Count);
        Assert.All(pads, p => Assert.Equal(1_200_000, p.X2 - p.X1));
        Assert.All(pads, p => Assert.Equal(800_000, p.Y2 - p.Y1));

        // The silkscreen line is footprint artwork, not a pad, and it kept its layer.
        Assert.Contains(cell.Shapes, s => s.LayerName == "F.SilkS");
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(90.0)]
    [InlineData(37.5)]
    public void APlacementAngle_SurvivesTheRoundTrip(double degrees)
    {
        var partDir = CreatePartCell();
        var boardDir = CreateCell("BOARD", v => v.Instances.Add(new LayoutInstance
        {
            CellRef = Path.GetRelativePath(CellFolder.SubFolderPath(Path.Combine(_dir, "BOARD"), ViewType.Layout), partDir),
            X = 10_000_000, Y = -10_000_000, RotationDegrees = degrees,
        }));

        var (_, text) = ExportOf(boardDir);
        var placement = Assert.Single(ReadBack(text).Placements);
        Assert.Equal(degrees, placement.RotationDegrees, 6);
        Assert.Equal(10_000_000, placement.X);
        Assert.Equal(-10_000_000, placement.Y);
    }

    /// <summary>
    /// The mirror is BAKED into the written child geometry and child layers, never emitted as a
    /// transform — L4d measured that this is how the format stores a flipped footprint, and a writer
    /// that emitted the transform instead would produce a part that flips a second time.
    /// </summary>
    [Fact]
    public void AMirroredInstance_WritesBackSideArtworkWithNegatedLocalY()
    {
        var partDir = CreatePartCell();
        var boardDir = CreateCell("BOARD", v => v.Instances.Add(new LayoutInstance
        {
            CellRef = Path.GetRelativePath(CellFolder.SubFolderPath(Path.Combine(_dir, "BOARD"), ViewType.Layout), partDir),
            X = 0, Y = 0, RotationDegrees = 0, MirrorX = true,
        }));

        var (_, text) = ExportOf(boardDir);
        Assert.Contains("(footprint \"circuitrf:PART\"", text);
        Assert.Contains("(layer \"B.Cu\")", text);

        var back = ReadBack(text);
        var cell = Assert.Single(back.FootprintCells.Values);

        // The silk line was at local y = +1.0 mm; flipped, it must be on the BACK silkscreen at -1.0 mm.
        var silk = Assert.Single(cell.Shapes, s => s.LayerName == "B.SilkS");
        var path = Assert.IsType<PathShape>(silk.Shape);
        Assert.Equal(-1_000_000, path.Xy[1]);

        // And the placement carries no mirror of its own — it must not flip twice.
        Assert.Single(back.Placements);
    }

    /// <summary>A cell with no pins has no pads, and a footprint with no pads is one nothing can route
    /// to — so its artwork is flattened onto the board instead, and the report says how many.</summary>
    [Fact]
    public void ACellWithNoPins_IsFlattenedOntoTheBoard_AndReported()
    {
        var blankDir = CreateCell("BLANK", v =>
            v.Shapes.Add(new RectShape { Layer = TopCu, X1 = 0, Y1 = -1_000_000, X2 = 1_000_000, Y2 = 0 }));
        var boardDir = CreateCell("BOARD", v => v.Instances.Add(new LayoutInstance
        {
            CellRef = Path.GetRelativePath(CellFolder.SubFolderPath(Path.Combine(_dir, "BOARD"), ViewType.Layout), blankDir),
            X = 5_000_000, Y = -5_000_000,
        }));

        var (plan, text) = ExportOf(boardDir);
        Assert.Equal(1, plan.CellsFlattenedForLackOfPins);
        Assert.Equal(0, plan.Summary.Footprints);

        var back = ReadBack(text);
        Assert.Empty(back.FootprintCells);
        Assert.Single(back.Shapes.Select(s => s.Shape).OfType<PolygonShape>());   // the artwork survived

        Assert.Single(PcbExport.Describe(plan), m => m.Contains("declare no pins"));
    }

    // ── Layers ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CopperTakesItsNameFromTheStackupOrder_AndAnAliasWins()
    {
        var cellDir = CreateCell("BOARD", v =>
        {
            v.Shapes.Add(new PathShape { Layer = TopCu, Width = 200_000, Xy = [0, 0, 1_000_000, 0] });
            v.Shapes.Add(new PathShape { Layer = BotCu, Width = 200_000, Xy = [0, -1_000_000, 1_000_000, -1_000_000] });
            v.Shapes.Add(new PathShape { Layer = SilkTop, Width = 120_000, Xy = [0, -2_000_000, 1_000_000, -2_000_000] });
        });

        var (_, text) = ExportOf(cellDir);
        Assert.Contains("(0 \"F.Cu\" signal)", text);      // topmost conductor
        Assert.Contains("(31 \"B.Cu\" signal)", text);     // bottom-most
        Assert.Contains("(37 \"F.SilkS\" user)", text);    // the explicit PcbLayerName alias
    }

    /// <summary>A layer with nothing saying where it belongs goes to a general-purpose drawing layer
    /// and is named in the report — never silently given a fabrication meaning it does not have.</summary>
    [Fact]
    public void AnUnmappedLayer_LandsOnADrawingLayer_AndIsNamedInTheReport()
    {
        var cellDir = CreateCell("BOARD", v =>
            v.Shapes.Add(new PathShape { Layer = Drill, Width = 100_000, Xy = [0, 0, 1_000_000, 0] }));

        var (plan, text) = ExportOf(cellDir);
        Assert.Contains(PcbLayerNaming.FallbackName, text);

        Assert.Contains("Drill", plan.Summary.UnmappedLayerNames);
        var note = Assert.Single(PcbExport.Describe(plan), m => m.Contains(PcbLayerNaming.FallbackName));
        Assert.Contains("Drill", note);
    }

    [Fact]
    public void ABoardOutlineLayerIsAlwaysDeclared_EvenWhenNothingIsDrawnOnIt()
    {
        var cellDir = CreateCell("BOARD", v => v.Shapes.Add(new PathShape { Layer = TopCu, Width = 200_000, Xy = [0, 0, 1_000_000, 0] }));
        var (_, text) = ExportOf(cellDir);
        Assert.Contains("\"Edge.Cuts\"", text);
    }

    // ── Fidelity limits, all reported ───────────────────────────────────────────────────────────

    [Fact]
    public void AHoleIsWrittenAsThisFormatsOwnSlit_AndComesBackAsOneOutline()
    {
        var cellDir = CreateCell("BOARD", v => v.Shapes.Add(new PolygonShape
        {
            Layer = TopCu,
            Xy = [0, 0, 10_000_000, 0, 10_000_000, -10_000_000, 0, -10_000_000],
            Holes = [[3_000_000, -3_000_000, 7_000_000, -3_000_000, 7_000_000, -7_000_000, 3_000_000, -7_000_000]],
        }));

        var (plan, text) = ExportOf(cellDir);
        Assert.Equal(1, plan.Summary.HolesKeyholed);

        // One self-touching outline, exactly as a real board's own filled zones are built.
        var fill = Assert.Single(ReadBack(text).Shapes.Select(s => s.Shape).OfType<PolygonShape>());
        Assert.Null(fill.Holes);
        Assert.True(fill.Xy.Length >= 20, "the slit must carry the hole's own vertices into the outline");
    }

    [Fact]
    public void ABitmapIsSkippedAndReported()
    {
        var cellDir = CreateCell("BOARD", v =>
        {
            v.Shapes.Add(new PathShape { Layer = TopCu, Width = 200_000, Xy = [0, 0, 1_000_000, 0] });
            v.Shapes.Add(new BitmapShape { Layer = SilkTop, X = 0, Y = 0, W = 1_000_000, H = 1_000_000 });
        });

        var (plan, _) = ExportOf(cellDir);
        Assert.Equal(1, plan.Summary.BitmapsSkipped);
        Assert.Single(PcbExport.Describe(plan), m => m.Contains("bitmap"));
    }

    [Fact]
    public void ACubicOnATrack_IsFlattenedAndReported()
    {
        var cellDir = CreateCell("BOARD", v => v.Shapes.Add(new PathShape
        {
            Layer = TopCu, Width = 200_000,
            Xy = [0, 0, 4_000_000, 0],
            Edges = [new LayoutEdge { Kind = EdgeKind.Cubic, C1X = 1_000_000, C1Y = -2_000_000, C2X = 3_000_000, C2Y = -2_000_000 }],
        }));

        var (plan, text) = ExportOf(cellDir);
        Assert.Equal(1, plan.Summary.CubicsFlattened);
        Assert.True(plan.Summary.Segments > 1, "a flattened cubic must produce more than one segment");
        Assert.Single(PcbExport.Describe(plan), m => m.Contains("cubic"));

        // The endpoints are still exact — a flattened curve may deviate in the middle, never at the ends.
        var tracks = ReadBack(text).Shapes.Select(s => s.Shape).OfType<PathShape>().ToList();
        Assert.Equal(0, tracks.First().Xy[0]);
        Assert.Equal(4_000_000, tracks.Last().Xy[2]);
    }

    [Fact]
    public void AnArcOnATrack_SurvivesAsAnArc()
    {
        var cellDir = CreateCell("BOARD", v => v.Shapes.Add(new PathShape
        {
            Layer = TopCu, Width = 200_000,
            Xy = [2_000_000, 0, 0, -2_000_000],
            Edges = [new LayoutEdge { Kind = EdgeKind.Arc, Bulge = Math.Tan(Math.PI / 8) }],
        }));

        var (plan, text) = ExportOf(cellDir);
        Assert.Equal(1, plan.Summary.Arcs);

        var arc = Assert.Single(ReadBack(text).Shapes.Select(s => s.Shape).OfType<PathShape>());
        Assert.NotNull(arc.Edges);
        Assert.Equal(EdgeKind.Arc, arc.Edges![0].Kind);
        Assert.Equal(Math.Tan(Math.PI / 8), arc.Edges[0].Bulge, 4);
        Assert.Equal(2_000_000, arc.Xy[0]);
        Assert.Equal(0, arc.Xy[1]);
    }

    // ── The ceiling ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AnOversizedLayout_IsRefusedBeforeAnythingIsWritten()
    {
        var cellDir = CreateCell("BOARD", v =>
        {
            for (long i = 0; i <= PcbExport.ShapeHardCeiling; i++)
                v.Shapes.Add(new PathShape { Layer = TopCu, Width = 1000, Xy = [0, 0, 1000, 0] });
        });

        var plan = PcbExport.Analyze(cellDir, Tech(), Dbu);
        Assert.False(plan.CanWrite);
        var refusal = Assert.Single(PcbExport.Describe(plan));
        Assert.Contains(PcbExport.ShapeHardCeiling.ToString("N0"), refusal);

        var path = Path.Combine(_dir, "never.kicad_pcb");
        Assert.Throws<InvalidOperationException>(() => PcbExport.Write(path, plan));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void ASmallLayout_IsNotRefused()
    {
        // The oracle can fail: without this, a writer that refused everything would pass the gate above.
        var cellDir = CreateCell("BOARD", v => v.Shapes.Add(new PathShape { Layer = TopCu, Width = 200_000, Xy = [0, 0, 1_000_000, 0] }));
        Assert.True(PcbExport.Analyze(cellDir, Tech(), Dbu).CanWrite);
    }

    // ── The written file is one this repo's own reader accepts as a board ───────────────────────

    [Fact]
    public void TheWrittenFileIsRecognisedAsABoard_AtTheTargetEpoch()
    {
        var cellDir = CreateCell("BOARD", v => v.Shapes.Add(new PathShape { Layer = TopCu, Width = 200_000, Xy = [0, 0, 1_000_000, 0] }));
        var (_, text) = ExportOf(cellDir);

        Assert.StartsWith("(kicad_pcb (version " + PcbLayerNaming.TargetVersion + ")", text);
        var back = ReadBack(text);
        Assert.Equal(PcbLayerNaming.TargetVersion, back.Version);
        Assert.Empty(back.UnknownTokenCounts);      // nothing we wrote is a token our own reader cannot name
    }

    // ── Text anchoring survives the round trip (owner report, 2026-08-25) ───────────────────────

    /// <summary>
    /// <b>Silence is not neutral for text in this format.</b> Its unstated justification is centred on
    /// both axes, while a <see cref="LabelShape"/>'s own default anchor is the left end of the baseline
    /// — so a writer that omits <c>(justify …)</c> displaces every string it writes by half its own
    /// width, and the displacement comes straight back on re-import.
    /// </summary>
    [Theory]
    [InlineData(null, null, LabelHAlign.Left, LabelVAlign.Bottom)]              // the historical default
    [InlineData(LabelHAlign.Left, LabelVAlign.Top, LabelHAlign.Left, LabelVAlign.Top)]
    [InlineData(LabelHAlign.Right, LabelVAlign.Bottom, LabelHAlign.Right, LabelVAlign.Bottom)]
    [InlineData(LabelHAlign.Center, LabelVAlign.Middle, LabelHAlign.Center, LabelVAlign.Middle)]
    public void ALabelsAnchor_SurvivesAWriteAndReRead(
        LabelHAlign? h, LabelVAlign? v, LabelHAlign expectedH, LabelVAlign expectedV)
    {
        var cellDir = CreateCell("BOARD", view => view.Shapes.Add(new LabelShape
        {
            Layer = SilkTop, X = 1_000_000, Y = 2_000_000, Text = "ANCHOR", Height = 1_000_000,
            HAlign = h, VAlign = v,
        }));

        var (_, text) = ExportOf(cellDir);
        var label = Assert.Single(ReadBack(text).Shapes.Select(s => s.Shape).OfType<LabelShape>());

        Assert.Equal(expectedH, label.HAlign);
        Assert.Equal(expectedV, label.VAlign);
        Assert.Equal(1_000_000, label.X);
        Assert.Equal(2_000_000, label.Y);
    }

    // ── Footprint pads: no duplicated copper, correct mask side, honest span ────────────────────

    /// <summary>Builds a one-pin footprint cell whose pad is <paramref name="padLayers"/> copper plus a
    /// drilled barrel, and places it once.</summary>
    private string TwoLayerThroughHoleCell(params LayerKey[] padLayers)
        => CreateCell("PART", v =>
        {
            foreach (var layer in padLayers)
                v.Shapes.Add(new RectShape { Layer = layer, X1 = -500_000, Y1 = -1_000_000, X2 = 500_000, Y2 = 1_000_000 });
            v.Shapes.Add(new ViaShape { Layer = Drill, X = 0, Y = 0, PadSize = 400_000, DrillSize = 400_000 });
            v.Pins.Add(new LayoutPin { Name = "1", X = 0, Y = 0, WidthDbu = 1_000_000, Layer = padLayers[0] });
        });

    private string BoardPlacing(string partDir)
    {
        var boardCellDir = CellFolder.CreateCellFolder(_dir, "BOARD");
        var layoutDir = CellFolder.SubFolderPath(boardCellDir, ViewType.Layout);
        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Instances.Add(new LayoutInstance { CellRef = Path.GetRelativePath(layoutDir, partDir) });
        LayoutPersistence.SaveToFile(Path.Combine(layoutDir, "BOARD.clay"), view);

        var ccellPath = Path.Combine(boardCellDir, CellFolder.CcellFileName);
        var ccell = CellPersistence.LoadFromFile(ccellPath);
        ccell.PrimaryLayout = "BOARD.clay";
        CellPersistence.SaveToFile(ccellPath, ccell);
        return boardCellDir;
    }

    /// <summary>
    /// <b>The pad's copper is written ONCE.</b> A pin claims the copper shape it sits in and the drill
    /// beside it; the drill must never win that claim, or the pad exports as a plain circle of the
    /// BARREL's diameter and its real outline is written again as footprint graphics — the same copper
    /// twice, in two different shapes, on the same layer.
    /// </summary>
    [Fact]
    public void AThroughHolePad_IsWrittenOnce_AsItsRealOutline_NotAsACircleAndAGraphic()
    {
        var text = ExportOf(BoardPlacing(TwoLayerThroughHoleCell(TopCu, BotCu))).Text;

        Assert.Contains("(pad \"1\" thru_hole rect", text);
        Assert.DoesNotContain("(pad \"1\" thru_hole circle", text);
        Assert.DoesNotContain("fp_rect", text);      // no second copy as graphics
        Assert.DoesNotContain("fp_poly", text);

        // One pad per copper layer and no more. It comes back as a rect because the hole is written as
        // (drill …) and re-punched by the reader rather than carried in the outline — so what re-imports
        // is the same annulus that was exported, not a keyholed one.
        var back = ReadBack(text);
        var cell = Assert.Single(back.FootprintCells.Values);
        Assert.Equal(2, cell.Shapes.Count(sh => sh.Shape is not ViaShape));
    }

    /// <summary>A drilled pad spans <c>*.Cu</c> only when its copper really is on every copper layer.
    /// Writing it unconditionally puts copper on layers the design left bare.</summary>
    [Fact]
    public void ADrilledPadWhoseCopperIsOnOneLayer_IsNotWrittenOnEveryCopperLayer()
    {
        var text = ExportOf(BoardPlacing(TwoLayerThroughHoleCell(TopCu))).Text;

        Assert.DoesNotContain("\"*.Cu\"", text);
        Assert.Contains("(layers \"F.Cu\" \"F.Mask\")", text);

        var cell = Assert.Single(ReadBack(text).FootprintCells.Values);
        Assert.Single(cell.Shapes, sh => sh.Shape is not ViaShape);
    }

    [Fact]
    public void ADrilledPadOnEveryCopperLayer_IsWrittenAsAStarSpan()
    {
        var text = ExportOf(BoardPlacing(TwoLayerThroughHoleCell(TopCu, BotCu))).Text;
        Assert.Contains("(layers \"*.Cu\" \"*.Mask\")", text);
    }

    /// <summary>
    /// The mask opening belongs to the side the PAD's copper is on. It used to follow the placement's
    /// mirror flag instead — and an imported back-side footprint has that flag FALSE (the flip is baked
    /// into its cell), so its B.Cu pad was paired with the FRONT mask: a solder-mask opening on the
    /// wrong face of the board, which is a fabrication error, not a display one.
    /// </summary>
    [Fact]
    public void ABackCopperPad_OpensTheBackMask_NotTheFront()
    {
        var text = ExportOf(BoardPlacing(CreateCell("PART", v =>
        {
            v.Shapes.Add(new RectShape { Layer = BotCu, X1 = -500_000, Y1 = -500_000, X2 = 500_000, Y2 = 500_000 });
            v.Pins.Add(new LayoutPin { Name = "1", X = 0, Y = 0, WidthDbu = 1_000_000, Layer = BotCu });
        }))).Text;

        Assert.Contains("(layers \"B.Cu\" \"B.Mask\")", text);
        Assert.DoesNotContain("\"B.Cu\" \"F.Mask\"", text);
    }

    /// <summary>
    /// <b>A drilled pad's hole is written as <c>(drill …)</c>, not carried in the outline.</b> An
    /// imported through-hole pad's copper is an ANNULUS — the hole really is drilled through it — and
    /// this format re-punches it from the drill, so writing the hole in the outline as well cuts it
    /// twice and leaves a keyhole slit across the copper.
    /// </summary>
    [Fact]
    public void ADrilledPadWhoseCopperIsAnAnnulus_WritesTheOuterRing_NotAKeyhole()
    {
        var partDir = CreateCell("PART", v =>
        {
            // A 2 x 2 mm pad with a 0.8 mm hole through it, as the importer now produces.
            v.Shapes.Add(new PolygonShape
            {
                Layer = TopCu,
                Xy = [-1_000_000, -1_000_000, 1_000_000, -1_000_000, 1_000_000, 1_000_000, -1_000_000, 1_000_000],
                Holes = [[-400_000, -400_000, 400_000, -400_000, 400_000, 400_000, -400_000, 400_000]],
            });
            v.Shapes.Add(new ViaShape { Layer = Drill, X = 0, Y = 0, PadSize = 800_000, DrillSize = 800_000 });
            v.Pins.Add(new LayoutPin { Name = "1", X = 0, Y = 0, WidthDbu = 2_000_000, Layer = TopCu });
        });

        var (plan, text) = ExportOf(BoardPlacing(partDir));

        Assert.Equal(0, plan.Summary.HolesKeyholed);
        Assert.Contains("(drill 0.8)", text);

        // What re-imports is the same annulus that was exported: one outer ring, one hole.
        var cell = Assert.Single(ReadBack(text).FootprintCells.Values);
        var copper = Assert.Single(cell.Shapes, sh => sh.Shape is not ViaShape).Shape;
        var rings = LayoutFlattener.Flatten(copper, 200);
        Assert.Equal(2, rings.Count);
        var outer = LayoutGeometry.BboxOf(copper);
        Assert.Equal(2_000_000, outer.MaxX - outer.MinX);
        Assert.Equal(2_000_000, outer.MaxY - outer.MinY);
    }

    /// <summary>A two-point round-capped stroke IS this format's <c>oval</c> pad. It used to fall
    /// through to a plain circle of the pin's declared width, silently replacing the pad's outline.</summary>
    [Fact]
    public void AStrokedPathPad_IsWrittenAsAnOval_NotSubstitutedWithACircle()
    {
        var partDir = CreateCell("PART", v =>
        {
            v.Shapes.Add(new PathShape
            {
                Layer = TopCu, Width = 600_000, End = PathEndStyle.Round,
                Xy = [-1_000_000, 0, 1_000_000, 0],
            });
            v.Pins.Add(new LayoutPin { Name = "1", X = 0, Y = 0, WidthDbu = 600_000, Layer = TopCu });
        });
        var text = ExportOf(BoardPlacing(partDir)).Text;

        Assert.Contains("(pad \"1\" smd oval", text);
        Assert.DoesNotContain("(pad \"1\" smd circle", text);

        // 2 mm of segment plus one width of round caps, by 0.6 mm across — and it reads back as the
        // same stroke it started as.
        var cell = Assert.Single(ReadBack(text).FootprintCells.Values);
        var path = Assert.IsType<PathShape>(Assert.Single(cell.Shapes).Shape);
        Assert.Equal(600_000, path.Width);
        Assert.Equal(2_000_000, path.Xy[2] - path.Xy[0]);
    }

    // ── A drilled pad's hole survives the round trip (owner report, 2026-08-25) ─────────────────

    /// <summary>
    /// <b>A drilled pad's <c>at</c> is the HOLE; the copper's displacement goes back out as the drill's
    /// <c>(offset …)</c>.</b> Writing the COPPER's centre as <c>at</c> with a bare <c>(drill …)</c>
    /// instead moves the hole onto the copper centre — so the pad drifts by the offset on every round
    /// trip, and the hole ends up somewhere the file never put it.
    /// </summary>
    [Fact]
    public void ADrilledPadWhoseCopperIsOffsetFromItsHole_WritesTheOffset_AndBothSurvive()
    {
        var partDir = CreateCell("PART", v =>
        {
            // Copper centred at (+0.6, 0); the hole on the footprint origin.
            v.Shapes.Add(new PolygonShape
            {
                Layer = TopCu,
                Xy = [-400_000, -1_000_000, 1_600_000, -1_000_000, 1_600_000, 1_000_000, -400_000, 1_000_000],
                Holes = [[-200_000, -200_000, 200_000, -200_000, 200_000, 200_000, -200_000, 200_000]],
            });
            v.Shapes.Add(new ViaShape { Layer = Drill, X = 0, Y = 0, PadSize = 400_000, DrillSize = 400_000 });
            v.Pins.Add(new LayoutPin { Name = "1", X = 0, Y = 0, WidthDbu = 2_000_000, Layer = TopCu });
        });

        var (_, text) = ExportOf(BoardPlacing(partDir));
        Assert.Contains("(offset 0.6 0)", text);

        var cell = Assert.Single(ReadBack(text).FootprintCells.Values);
        var copper = Assert.Single(cell.Shapes, sh => sh.Shape is not ViaShape).Shape;
        var via = Assert.Single(cell.Shapes.Select(sh => sh.Shape).OfType<ViaShape>());

        // The hole stayed on the origin; the copper stayed 0.6 mm away from it.
        Assert.Equal(0, via.X);
        Assert.Equal(0, via.Y);
        var bbox = LayoutGeometry.BboxOf(copper);
        Assert.Equal(600_000, (bbox.MinX + bbox.MaxX) / 2);
        Assert.Equal(2, LayoutFlattener.Flatten(copper, 200).Count);   // still an annulus
    }

    /// <summary>
    /// <b>Only the DRILL's own hole is omitted from the outline.</b> A pad's other holes are real copper
    /// features — a custom pad built from unfilled circle primitives is a pad full of annuli — and
    /// dropping them along with the drill silently fills them in. Measured on a real board before this:
    /// three custom pads lost every hole they had.
    /// </summary>
    [Fact]
    public void ADrilledPadsOtherHoles_AreKept_WhileOnlyTheDrillsOwnIsOmitted()
    {
        var partDir = CreateCell("PART", v =>
        {
            // A 6 x 2 mm pad with TWO holes: the drill on the left, an unrelated one on the right.
            v.Shapes.Add(new PolygonShape
            {
                Layer = TopCu,
                Xy = [-3_000_000, -1_000_000, 3_000_000, -1_000_000, 3_000_000, 1_000_000, -3_000_000, 1_000_000],
                Holes =
                [
                    [-1_800_000, -400_000, -1_000_000, -400_000, -1_000_000, 400_000, -1_800_000, 400_000],
                    [ 1_000_000, -400_000,  1_800_000, -400_000,  1_800_000, 400_000,  1_000_000, 400_000],
                ],
            });
            v.Shapes.Add(new ViaShape { Layer = Drill, X = -1_400_000, Y = 0, PadSize = 800_000, DrillSize = 800_000 });
            v.Pins.Add(new LayoutPin { Name = "1", X = -1_400_000, Y = 0, WidthDbu = 2_000_000, Layer = TopCu });
        });

        var (plan, text) = ExportOf(BoardPlacing(partDir));

        // The drill's hole leaves as (drill …); the other one is keyholed, because this format's
        // graphics have no inner rings and a keyhole is the only way to state one.
        Assert.Equal(1, plan.Summary.HolesKeyholed);
        // The pin sits on the drill, and the copper's centre does not — so the displacement goes out as
        // the offset, exactly as the previous test pins.
        Assert.Contains("(drill 0.8 (offset 1.4 0))", text);

        // Re-imported, the drill re-punches its own hole. (A KEYHOLED hole is a zero-width slit, and a
        // slit does not survive the Clipper difference that re-punches the drill — so the second hole
        // fills back in. That is a limitation of stating a hole as a slit in a format whose graphics
        // have no inner rings, not of this omission rule; without the rule BOTH holes were lost.)
        var cell = Assert.Single(ReadBack(text).FootprintCells.Values);
        var copper = Assert.Single(cell.Shapes, sh => sh.Shape is not ViaShape).Shape;
        Assert.Equal(2, LayoutFlattener.Flatten(copper, 200).Count);
        Assert.Equal(12.0 - Math.PI * 0.4 * 0.4, NetAreaMm2(copper), 2);
    }

    /// <summary>
    /// <c>(drill oval W H)</c> states the slot's X and Y extents IN THE PAD'S OWN FRAME, so which of
    /// the two carries the span depends on which way the slot runs. Writing it X-major unconditionally
    /// turns every vertical slot on its side — a hole in the wrong place, not a rounding.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnOvalDrill_IsWrittenOnTheAxisItsSlotRunsAlong(bool alongY)
    {
        long dx = alongY ? 0 : 1_200_000, dy = alongY ? 1_200_000 : 0;
        var partDir = CreateCell("PART", v =>
        {
            v.Shapes.Add(new RectShape { Layer = TopCu, X1 = -2_000_000, Y1 = -2_000_000, X2 = 2_000_000, Y2 = 2_000_000 });
            v.Shapes.Add(new ViaShape { Layer = Drill, X = 0, Y = 0, PadSize = 800_000, DrillSize = 800_000 });
            v.Shapes.Add(new PathShape
            {
                Layer = Drill, Width = 800_000, End = PathEndStyle.Round,
                Xy = [-dx / 2, -dy / 2, dx / 2, dy / 2],
            });
            v.Pins.Add(new LayoutPin { Name = "1", X = 0, Y = 0, WidthDbu = 2_000_000, Layer = TopCu });
        });

        var (_, text) = ExportOf(BoardPlacing(partDir));
        // 0.8 across, 0.8 + 1.2 along.
        Assert.Contains(alongY ? "(drill oval 0.8 2)" : "(drill oval 2 0.8)", text);

        // …and it comes back running the same way.
        var cell = Assert.Single(ReadBack(text).FootprintCells.Values);
        var slot = Assert.Single(cell.Shapes.Select(sh => sh.Shape).OfType<PathShape>());
        Assert.Equal(800_000, slot.Width);
        Assert.Equal(alongY ? 0 : 1_200_000, Math.Abs(slot.Xy[2] - slot.Xy[0]));
        Assert.Equal(alongY ? 1_200_000 : 0, Math.Abs(slot.Xy[3] - slot.Xy[1]));
    }

    /// <summary>Outer ring minus inner rings, in mm².</summary>
    private static double NetAreaMm2(LayoutShape shape)
    {
        double total = 0;
        var rings = LayoutFlattener.Flatten(shape, 200);
        for (int r = 0; r < rings.Count; r++)
        {
            double a = 0;
            var ring = rings[r];
            for (int i = 0; i < ring.Length; i += 2)
            {
                int j = (i + 2) % ring.Length;
                a += (double)ring[i] * ring[j + 1] - (double)ring[j] * ring[i + 1];
            }
            total += (r == 0 ? 1 : -1) * Math.Abs(a) / 2.0;
        }
        return total / 1e12;
    }
}

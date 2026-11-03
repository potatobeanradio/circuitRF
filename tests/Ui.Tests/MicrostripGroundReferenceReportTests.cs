using CircuitRF.Design.Layout.Lvs;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Schematic;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Update Layout draws the ground the schematic states (designer feedback round 11, reversing round 10's offer-only,
/// inner-only pour): a via beside every pad whose pin is on a ground symbol, and the pour on every ground conductor the
/// lines and those vias return through — inner or outer. Both are GENERATED copper, redrawn by every run from where the
/// parts are then; what the designer moved, deleted or drew is theirs.
/// </summary>
public sealed class MicrostripGroundReferenceReportTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-mlin-ground-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static EditableComponent Place(SchematicEditModel m, SymbolKind kind, string name, double x, double y)
    {
        var c = new EditableComponent { InstanceName = name, Symbol = kind, X = x, Y = y };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(kind, 0))
            c.Parameters.Add(new EditableParameter { Name = dp.Name, Expression = dp.Expression, Unit = dp.Unit, Dimension = dp.Dimension });
        m.Components.Add(c);
        return c;
    }

    private static void Set(EditableComponent c, string name, string expression)
    {
        if (c.Parameters.FirstOrDefault(p => p.Name == name) is { } p) p.Expression = expression;
        else c.Parameters.Add(new EditableParameter { Name = name, Expression = expression });
    }

    private (string SchematicDir, string LayoutDir, string CellDir) Cell()
    {
        Directory.CreateDirectory(_root);
        string cellDir = CellFolder.CreateCellFolder(_root, "Pdn");
        return (CellFolder.SubFolderPath(cellDir, ViewType.Schematic), CellFolder.SubFolderPath(cellDir, ViewType.Layout), cellDir);
    }

    /// <summary>The designer's PDN2: an MLIN with a shunt inductor to ground at one end and a shunt capacitor to ground
    /// at the other, 0402 parts — connected pin on pin, which is a connection by the extractor's own rule.</summary>
    /// <param name="viaGnd">Each part grounded through a VIAGND component rather than a ground symbol (round 12's PDN3).</param>
    /// <param name="longLine">The line 400 mm long, as round 12's was, rather than the default.</param>
    private static SchematicEditModel Pdn(string schematicDir, bool viaGnd = false, bool longLine = false)
    {
        var m = new SchematicEditModel { SchematicDirectory = schematicDir };
        var line = Place(m, SymbolKind.Mlin, "ML1", 0, 0);
        if (longLine) Set(line, "L", "400");
        if (longLine) line.Parameters.Single(p => p.Name == "L").Unit = "mm";
        var ends = m.PortDefsOf(line).Select(d => m.PortWorldOf(line, d)).OrderBy(p => p.X).ToList();

        foreach (var (kind, name, end) in new[] { (SymbolKind.Inductor, "L1", ends[0]), (SymbolKind.Capacitor, "C1", ends[1]) })
        {
            var part = Place(m, kind, name, 0, 0);
            Set(part, "Footprint", longLine && kind == SymbolKind.Inductor ? "smt:0603" : "smt:0402");
            var at0 = m.PortDefsOf(part).Select(d => m.PortWorldOf(part, d)).ToList();
            if (Math.Abs(at0[0].Y - at0[1].Y) < Math.Abs(at0[0].X - at0[1].X)) part.Rotation = SymbolRotation.R90;   // shunt: vertical
            var pins = m.PortDefsOf(part).Select(d => m.PortWorldOf(part, d)).OrderBy(p => p.Y).ToList();
            part.X += end.X - pins[0].X;   // its upper pin on the line's end
            part.Y += end.Y - pins[0].Y;
            var lower = m.PortDefsOf(part).Select(d => m.PortWorldOf(part, d)).OrderBy(p => p.Y).Last();
            if (!viaGnd) { Place(m, SymbolKind.Ground, "GND_" + name, lower.X, lower.Y); continue; }
            var via = Place(m, SymbolKind.ViaGnd, "VIAG_" + name, 0, 0);
            var a = m.PortWorldOf(via, m.PortDefsOf(via).First());
            via.X += lower.X - a.X;   // its pin A on the part's lower pin
            via.Y += lower.Y - a.Y;
        }
        return m;
    }

    [Fact]
    public void TwoLayerBoard_OneUpdateLayout_GroundsEveryGroundPin_PoursBottom_AndLvsSeesTheGround()
    {
        var (schematicDir, layoutDir, cellDir) = Cell();
        var tech = StarterTechnologies.Pcb2Layer();
        var bottom = tech.Stackup.Layers.Single(l => l.IsGroundReference);
        var drill = tech.Stackup.Layers.Single(l => l.Kind == StackupKind.Via).DrawingLayers[0];
        var model = Pdn(schematicDir);
        var target = new LayoutView();

        var run = SchematicToLayoutGenerator.Run(model, target, schematicDir, _root, layoutDir, tech, null, null);
        run.Command!.Execute();

        // A via beside each grounded pad, tied to it, and the Bottom Copper poured under the line and both vias.
        var vias = target.Shapes.OfType<ViaShape>().Where(GroundArtwork.IsGroundVia).ToList();
        Assert.Equal(2, vias.Count);
        Assert.All(vias, v => Assert.Equal(drill, v.Layer));
        Assert.Equal(["C1:2", "L1:2"], target.GroundViaKeys.Order(StringComparer.Ordinal));
        var pour = Assert.Single(run.GroundArtwork.Pours);
        Assert.Equal(bottom.Name, pour.Ground.Name);
        Assert.Equal((1, 2, 0), (pour.Lines, pour.GroundVias, pour.ViaClearances));
        var poured = target.Shapes.Where(s => s.Generated == GroundArtwork.PourTag).ToList();
        Assert.All(poured, s => Assert.Contains(s.Layer, bottom.DrawingLayers));
        var area = poured.Select(LayoutGeometry.BboxOf).Aggregate((a, b) => a.Union(b));
        Assert.All(vias, v => Assert.True(area.Contains(v.X, v.Y)));
        Assert.All(target.Instances, i => Assert.True(area.Contains(CellHierarchy.InstanceBbox(i, layoutDir).MinX,
                                                                   CellHierarchy.InstanceBbox(i, layoutDir).MinY)));

        // LVS reads both ground terminals on one net, the ground.
        var lvs = LvsRun.Run(target, Path.Combine(layoutDir, "Pdn.clay"), cellDir, tech, model,
                             Path.Combine(schematicDir, "Pdn.csch"));
        foreach (var d in lvs.Diagnostics) output.WriteLine(d.ToString());
        int GroundNet(string designator) => lvs.Layout.Devices.Single(d => d.Designator == designator)
                                                .Terminals.Single(t => t.Port == 2).NetIndex;
        Assert.Equal(GroundNet("L1"), GroundNet("C1"));
        // The opens left are the line to each part, which Update Layout does not route. Without the vias (the layout
        // as round 10 left it) the ground pins are an open too — the oracle that the vias are what grounds them.
        static bool GroundOpen(LvsRunResult r)
            => r.Diagnostics.Any(d => System.Text.RegularExpressions.Regex.IsMatch(d.ToString(), @"\b(L1|C1)\.2\b"));
        Assert.False(GroundOpen(lvs));
        var unGrounded = LayoutPersistence.Deserialize(LayoutPersistence.Serialize(target));
        Assert.Equal(target.Shapes.Count(s => s.Generated is not null), unGrounded.Shapes.Count(s => s.Generated is not null));
        Assert.Equal(target.GroundViaKeys.Order(), unGrounded.GroundViaKeys.Order());   // both survive a save
        unGrounded.Shapes.RemoveAll(s => s.Generated is not null);
        Assert.True(GroundOpen(LvsRun.Run(unGrounded, Path.Combine(layoutDir, "Pdn.clay"), cellDir, tech, model,
                                          Path.Combine(schematicDir, "Pdn.csch"))));

        // A second run with nothing moved changes nothing.
        Assert.Null(SchematicToLayoutGenerator.Run(model, target, schematicDir, _root, layoutDir, tech, null, null).Command);

        // The designer moves C1: its via follows on the next run, and the pour is redrawn over it.
        var c1 = target.Instances.Single(i => i.SchematicId == "C1");
        var c1Via = vias.Single(v => GroundArtwork.KeyOfShape(v) == "C1:2");
        long cx = c1Via.X, cy = c1Via.Y;
        c1.X += 3_000_000;
        var follow = SchematicToLayoutGenerator.Run(model, target, schematicDir, _root, layoutDir, tech, null, null);
        Assert.Equal(1, follow.GroundArtwork.ViasFollowed);
        follow.Command!.Execute();
        var moved = target.Shapes.OfType<ViaShape>().Single(v => GroundArtwork.KeyOfShape(v) == "C1:2");
        Assert.Equal((cx + 3_000_000, cy), (moved.X, moved.Y));

        // A via the designer moves stays put; one they delete is not put back.
        moved.X += 500_000;
        var l1Via = target.Shapes.OfType<ViaShape>().Single(v => GroundArtwork.KeyOfShape(v) == "L1:2");
        target.Shapes.RemoveAll(s => GroundArtwork.KeyOfShape(s) == "L1:2");
        c1.X += 1_000_000;
        var kept = SchematicToLayoutGenerator.Run(model, target, schematicDir, _root, layoutDir, tech, null, null);
        Assert.Equal((0, 0, 1, 1), (kept.GroundArtwork.ViasPlaced, kept.GroundArtwork.ViasFollowed,
                                    kept.GroundArtwork.ViasLeftMoved, kept.GroundArtwork.ViasLeftDeleted));
        Assert.NotEqual(0, l1Via.X);
    }

    /// <summary>The two-layer starter with an inner ground plane between its coppers, so a through via PASSES the
    /// plane the lines return through, and the bottom copper is no longer ground.</summary>
    private static Technology WithInnerGround(out StackupLayer inner)
    {
        var tech = StarterTechnologies.Pcb2Layer();
        var key = new LayerKey(10, 0);
        tech.Layers.Add(new LayerDef { Key = key, Name = "Inner Ground", Purpose = "drawing" });
        var layers = tech.Stackup.Layers;
        layers.Single(l => l.IsGroundReference).IsGroundReference = false;
        int fr4 = layers.FindIndex(l => l.Kind == StackupKind.Dielectric);
        layers[fr4].ThicknessDbu /= 2;
        inner = new StackupLayer
        {
            Kind = StackupKind.Conductor, Name = "Inner Ground", ThicknessDbu = 35_000, SigmaSm = 5.8e7,
            DrawingLayers = [key], IsGroundReference = true,
        };
        layers.Insert(fr4 + 1, inner);
        layers.Insert(fr4 + 2, new StackupLayer
        {
            Kind = StackupKind.Dielectric, Name = "FR-4 lower", ThicknessDbu = layers[fr4].ThicknessDbu, Epsr = 4.4, TanD = 0.02,
        });
        return tech;
    }

    [Fact]
    public void InnerGround_IsPoured_ADesignerViaThroughItGetsAClearance_TheGroundViasDoNot_AndDrawnCopperIsLeftAlone()
    {
        var (schematicDir, layoutDir, _) = Cell();
        var tech = WithInnerGround(out var ground);
        var model = Pdn(schematicDir);
        var target = new LayoutView();

        var first = SchematicToLayoutGenerator.Run(model, target, schematicDir, _root, layoutDir, tech, null, null);
        first.Command!.Execute();
        var pour = Assert.Single(first.GroundArtwork.Pours);
        Assert.Equal(ground.Name, pour.Ground.Name);
        Assert.Equal(2, first.GroundArtwork.ViasPlaced);

        // A through via the designer draws inside the lines' region passes the inner plane: the redrawn pour gets a
        // hole for it, and none for the two ground vias, whose drill passes the same plane.
        var drill = tech.Stackup.Layers.Single(l => l.Kind == StackupKind.Via).DrawingLayers[0];
        var line = CellHierarchy.InstanceBbox(target.Instances.Single(i => i.SchematicId == "ML1"), layoutDir);
        target.Shapes.Add(new ViaShape { Layer = drill, X = line.MinX, Y = line.MinY, PadSize = 600_000, DrillSize = 300_000 });
        var second = SchematicToLayoutGenerator.Run(model, target, schematicDir, _root, layoutDir, tech, null, null);
        var redrawn = Assert.Single(second.GroundArtwork.Pours);
        Assert.Equal((1, 2), (redrawn.ViaClearances, redrawn.GroundVias));
        second.Command!.Execute();
        var poly = Assert.IsType<PolygonShape>(Assert.Single(target.Shapes, s => s.Generated == GroundArtwork.PourTag));
        Assert.Single(poly.Holes!);

        // Copper the designer draws on the plane makes it theirs: no pour is drawn over it, and the old one stays.
        target.Shapes.Add(new RectShape { Layer = ground.DrawingLayers[0], X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000 });
        var third = SchematicToLayoutGenerator.Run(model, target, schematicDir, _root, layoutDir, tech, null, null);
        Assert.Empty(third.GroundArtwork.Pours);
        Assert.Equal([ground.Name], third.GroundArtwork.PoursLeftAlone.Select(l => l.Name));
        Assert.Null(third.Command);
    }

    /// <summary>
    /// Round 12's PDN3: the same circuit grounded through VIAGND components, with a 400 mm line. Each part lands beside
    /// the line rather than a line-length away, each VIAGND lands beside the pad it grounds — where a ground via would
    /// — tied to it, no generated ground via is stacked on it, and the pour reaches it on the plane it lands on. The
    /// shipped four-layer board is the inner-ground case, with ground symbols as well as VIAGNDs.
    /// </summary>
    [Theory]
    [InlineData(true, "pcb-2layer_RO4350B_20mil_1oz", "Bottom Copper (1 oz)")]
    [InlineData(true, "pcb-4layer_FR-4_62mil_1oz", "Inner 1 (Ground Plane)")]
    [InlineData(false, "pcb-4layer_FR-4_62mil_1oz", "Inner 1 (Ground Plane)")]
    public void ALongLine_LeavesThePartsInView_AndAViaGndGroundsItsPadOnce(bool viaGnd, string techId, string groundName)
    {
        var (schematicDir, layoutDir, cellDir) = Cell();
        var tech = ShippedTechnologies.Load(techId);
        var model = Pdn(schematicDir, viaGnd, longLine: true);
        var target = new LayoutView();

        var run = SchematicToLayoutGenerator.Run(model, target, schematicDir, _root, layoutDir, tech, null, null);
        run.Command!.Execute();
        foreach (var l in run.Lines) output.WriteLine(l.Text);
        Bbox Box(string id) => CellHierarchy.InstanceBbox(target.Instances.Single(i => i.SchematicId == id), layoutDir);

        // The parts sit at the line's ends — millimetres from it, not the 600 mm a pitch of 1.5 lines put them at. Since
        // brief-sdl-2 each is joined to the end the schematic puts it on (L1 the start, C1 the far end), so it is the
        // GAP between the boxes that is millimetres, on whichever side.
        var line = Box("ML1");
        static long Gap(Bbox a, Bbox b) => Math.Max(Math.Max(a.MinX - b.MaxX, b.MinX - a.MaxX), Math.Max(a.MinY - b.MaxY, b.MinY - a.MaxY));
        Assert.All(new[] { "L1", "C1" }, id => Assert.InRange(Gap(Box(id), line), long.MinValue, 5_000_000));

        var pour = Assert.Single(run.GroundArtwork.Pours);
        Assert.Equal(groundName, pour.Ground.Name);
        Assert.Equal(2, pour.GroundVias);
        Assert.Equal(0, pour.ViasJoined);
        var area = target.Shapes.Where(s => s.Generated == GroundArtwork.PourTag).Select(LayoutGeometry.BboxOf)
                                .Aggregate((a, b) => a.Union(b));

        var lvs = LvsRun.Run(target, Path.Combine(layoutDir, "Pdn.clay"), cellDir, tech, model,
                             Path.Combine(schematicDir, "Pdn.csch"));
        foreach (var d in lvs.Diagnostics) output.WriteLine(d.ToString());
        int Net(string designator, int port) => lvs.Layout.Devices.Single(d => d.Designator == designator)
                                                  .Terminals.Single(t => t.Port == port).NetIndex;
        if (!viaGnd)
        {
            Assert.Equal(2, run.GroundArtwork.ViasPlaced);
            Assert.Equal(Net("L1", 2), Net("C1", 2));
            return;
        }

        // The VIAGND is the via: nothing generated beside it, nothing remembered.
        Assert.DoesNotContain(target.Shapes.OfType<ViaShape>(), GroundArtwork.IsGroundVia);
        Assert.Empty(target.GroundViaKeys);
        foreach (var (part, via) in new[] { ("L1", "VIAG_L1"), ("C1", "VIAG_C1") })
        {
            var v = Box(via);
            Assert.True(area.Contains((v.MinX + v.MaxX) / 2, (v.MinY + v.MaxY) / 2));
            var p = Box(part);
            Assert.InRange(Math.Abs((v.MinY + v.MaxY) / 2 - (p.MinY + p.MaxY) / 2), 1, 2_500_000);   // beside its pad
            Assert.Equal((p.MinX + p.MaxX) / 2, (v.MinX + v.MaxX) / 2);
            Assert.Equal(Net(part, 2), Net(via, 1));   // the tie joins them
            Assert.NotEqual(Net(part, 2), Net(via, 2));   // and the via, not its own copper, joins them to ground
        }
        Assert.DoesNotContain(lvs.Diagnostics, d => d.ToString().StartsWith("Short", StringComparison.Ordinal));

        // A second run changes nothing: the VIAGNDs are placed parts now, and their ties ordinary copper.
        Assert.Null(SchematicToLayoutGenerator.Run(model, target, schematicDir, _root, layoutDir, tech, null, null).Command);
    }

    /// <summary>
    /// A ground via is "deleted, not put back" only while its part is there to keep it. Round 12: the part (or the
    /// whole layout) deleted and placed again by the next run inherited the old key, so its pad was never grounded.
    /// </summary>
    [Fact]
    public void APartPlacedAfresh_TakesAFreshGroundVia()
    {
        var (schematicDir, layoutDir, _) = Cell();
        var tech = StarterTechnologies.Pcb2Layer();
        var model = Pdn(schematicDir);
        var target = new LayoutView();
        SchematicToLayoutGenerator.Run(model, target, schematicDir, _root, layoutDir, tech, null, null).Command!.Execute();

        // L1 and its ground via deleted; C1's via alone deleted.
        target.Instances.RemoveAll(i => i.SchematicId == "L1");
        target.Shapes.RemoveAll(s => GroundArtwork.KeyOfShape(s) is "L1:2" or "C1:2");

        var again = SchematicToLayoutGenerator.Run(model, target, schematicDir, _root, layoutDir, tech, null, null);
        Assert.Equal((1, 1), (again.GroundArtwork.ViasPlaced, again.GroundArtwork.ViasLeftDeleted));
        again.Command!.Execute();
        Assert.Equal(["L1:2"], target.Shapes.OfType<ViaShape>().Where(GroundArtwork.IsGroundVia)
                                     .Select(v => GroundArtwork.KeyOfShape(v)!));
    }
}

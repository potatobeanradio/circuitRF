using CircuitRF.Design.Layout.Interchange;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.SchematicSync;

/// <summary>
/// brief-sdl-2 §5: footprints in lines, one test per claim — the facing rule every reader shares (D1), where a line
/// ends on a pad (D2, the series D-joint), the board-read footprint against the generated one (F6), the side-on shunt
/// parts (D4, D5), the bridging misfit (D6) and the side rule (D8). The designs themselves (F1-F7) are gated by
/// <see cref="SdlDesignSetTests"/>.
/// </summary>
// ComponentImport.Import walks cell folders through CellStat's process-global counter; see that collection's note.
[Collection(CellStatGlobalsCollection.Name)]
public sealed class FootprintChainTests(ITestOutputHelper output) : IDisposable
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "crf-sdl2-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { /* best-effort */ }
    }

    // ── D1: one facing rule ──────────────────────────────────────────────────────────────────────

    /// <summary>An 0603 whose pads are TALLER than they are wide — the shape a long-axis rule gets wrong in its axis,
    /// not only its sign — read by the board reader, the XML library reader and the artwork builder, and imported:
    /// every one of them stores pad 1 facing −X and pad 2 facing +X.</summary>
    [Fact]
    public void D1_An0603ReadThroughEveryReaderAndImportedFacesAwayFromItsOtherPad()
    {
        const string mod = """
            (module CAP0603 (layer F.Cu)
              (pad 1 smd rect (at -0.75 0) (size 0.8 0.95) (layers F.Cu F.Paste F.Mask))
              (pad 2 smd rect (at 0.75 0) (size 0.8 0.95) (layers F.Cu F.Paste F.Mask)))
            """;
        var board = PcbReader.ReadFootprint(mod, Dbu);
        Assert.Null(board.Refusal);

        var xml = ComponentLibraryXmlReader.Read(Xml0603, Dbu);
        Assert.Null(xml.Refusal);

        var artwork = new ComponentArtwork { Name = "CAP0603" };
        artwork.Pads.Add(new ComponentPadSpec("1", -0.75, 0, 0.8, 0.95, ComponentPadForm.Rectangle));
        artwork.Pads.Add(new ComponentPadSpec("2", 0.75, 0, 0.8, 0.95, ComponentPadForm.Rectangle));
        var built = ComponentFootprintBuilder.Build(artwork, mm => (long)Math.Round(mm * 1000 * Dbu));

        var import = ComponentImport.Import(xml.Part!, _scratch, null, Dbu);
        Assert.NotNull(import.CellDir);
        string layoutDir = CellFolder.SubFolderPath(import.CellDir!, ViewType.Layout);
        var imported = LayoutPersistence.LoadFromFile(
            Path.Combine(layoutDir, CellFolder.ResolvePrimary(import.CellDir!, ViewType.Layout).ResolvedName!));

        static Dictionary<string, double> Facing(IEnumerable<LayoutPin> pins) => pins.ToDictionary(p => p.Name, p => p.OutwardDeg);
        var expected = new Dictionary<string, double> { ["1"] = 180, ["2"] = 0 };
        Assert.Equal(expected, Facing(board.Cell!.Pins.Select(p => p.Pin)));
        Assert.Equal(expected, Facing(xml.Part!.Footprints[0].Cell.Pins.Select(p => p.Pin)));
        Assert.Equal(expected, Facing(built.Cell.Pins.Select(p => p.Pin)));
        Assert.Equal(expected, Facing(imported.Pins));
    }

    /// <summary>A footprint whose file stores both pads facing +X — pad 1 facing INTO the part — is corrected; a
    /// rotated part keeps its own frame; a part with one pad keeps what it stores and says it had nothing to face from.</summary>
    [Fact]
    public void D1_AReversedDirectionIsCorrected_ARotatedPartKeepsItsFrame_AndASinglePadIsReported()
    {
        var reversed = PadFacing.Effective([Pin("1", -725_000, 0, 0), Pin("2", 725_000, 0, 0)]);
        Assert.Equal([180.0, 0.0], reversed.Deg);
        Assert.False(reversed.Degenerate);

        // An 0603 turned 30°: its pads are stored along 30°, and stay in that frame rather than snapping to the nearest 90°.
        var (c, s) = LayoutAngle.CosSin(30);
        var turned = PadFacing.Effective([Pin("1", (long)(-725_000 * c), (long)(-725_000 * s), 30),
                                          Pin("2", (long)(725_000 * c), (long)(725_000 * s), 30)]);
        Assert.Equal(210.0, turned.Deg[0], 9);
        Assert.Equal(30.0, turned.Deg[1], 9);

        var single = PadFacing.Effective([Pin("1", 100, 200, 45)]);
        Assert.True(single.Degenerate);
        Assert.Equal([45.0], single.Deg);
    }

    // ── D2: the series joint, and the board-read footprint ───────────────────────────────────────

    /// <summary>D-joint: in F1 the line pins that meet the capacitor are exactly its outer-edge-to-outer-edge span
    /// apart — 2.4 mm for the generated 0603 — so each line's L is the exposed line.</summary>
    [Fact]
    public void DJoint_F1sLineEndsAreTheCapacitorsOuterPadSpanApart()
    {
        var p = SdlChecker.Prepare("F1-dc-block", _scratch);
        var layout = p.Applied(p.Plan());
        var (x1, y1) = PinAt(layout, p, "TL1", "2");
        var (x2, y2) = PinAt(layout, p, "TL2", "1");
        Assert.Equal(2_400_000, Math.Sqrt(Math.Pow(x2 - x1, 2) + Math.Pow(y2 - y1, 2)), 1.0);
    }

    /// <summary>F6's capacitor is F1's copper in a file whose pads both face +X, as a board reader stored them: it
    /// lands the same way round, and every part's copper is where F1's is.</summary>
    [Fact]
    public void F6_ABoardReadFootprintLandsTheSameCopperAsTheGeneratedOne()
    {
        var f1 = SdlChecker.Prepare("F1-dc-block", Path.Combine(_scratch, "f1"));
        var f6 = SdlChecker.Prepare("F6-board-footprint", Path.Combine(_scratch, "f6"));
        Assert.Equal(Copper(f1, f1.Applied(f1.Plan())), Copper(f6, f6.Applied(f6.Plan())));
    }

    // ── D4, D5: side-on parts ────────────────────────────────────────────────────────────────────

    /// <summary>Three shunt capacitors on the one joint of F2: the first goes on the left of the direction of travel,
    /// the second on the right, and the third is left to the row with a line that says so.</summary>
    [Fact]
    public void D4_ThreeShuntPartsOnOneJoint_TwoGoSideOnAndTheThirdIsReported()
    {
        var p = SdlChecker.Prepare("F2b-three-shunts", _scratch);
        var plan = p.Plan();
        var layout = p.Applied(plan);
        var line = Instance(layout, "TL1");
        var box1 = CellHierarchy.InstanceBbox(Instance(layout, "C1"), p.LayoutDir);
        var box2 = CellHierarchy.InstanceBbox(Instance(layout, "C2"), p.LayoutDir);

        // TL1 runs along +X from port 1, so left is +Y: C1 above the line, C2 below, both centred on the joint.
        Assert.True(box1.MinY > line.Y && box2.MaxY < line.Y, $"C1 {box1}, C2 {box2}, line at y {line.Y}");
        var joint = PinAt(layout, p, "TL1", "2");
        Assert.Equal(joint.X, (box1.MinX + box1.MaxX) / 2.0, 1.0);
        Assert.Equal(joint.X, (box2.MinX + box2.MaxX) / 2.0, 1.0);

        var third = Assert.Single(plan.Lines, l => l.InstanceName == "C3" && l.Severity == SchematicLayoutPlanner.ReportSeverity.Warning);
        Assert.Contains("third part at the joint on net n2", third.Text);
    }

    /// <summary>D5 in F5: of the two pads on the net where TL2 starts, the capacitor's leads on to TL1, so TL2 joins
    /// it end to end; the inductor, whose other end leads to no line, goes side-on against TL2's start.</summary>
    [Fact]
    public void D5_TheLineJoinsThePartThatContinuesTheChain_AndTheOtherGoesSideOn()
    {
        var p = SdlChecker.Prepare("F5-bias-tee", _scratch);
        var layout = p.Applied(p.Plan());
        var start = PinAt(layout, p, "TL2", "1");
        Assert.Equal(start, PinAt(layout, p, "C1", "2"));   // C1's pad 2 joint is TL2's pin — its centre is not
        var inductor = Instance(layout, "L1");
        Assert.Equal(90.0, LayoutAngle.Normalize(inductor.RotationDegrees) % 180.0, 9);   // square to the line
        Assert.Equal(start.X, inductor.X, 1.0);                                           // on the joint's centre line
    }

    // ── D6: a bridging part that does not fit ────────────────────────────────────────────────────

    /// <summary>F4 with the tee's arms 0.1 mm longer each, so the arm ends are 0.2 mm further apart than the resistor's
    /// pads span: exactly one bridging warning, naming the resistor and both spacings.</summary>
    [Fact]
    public void D6_AResistorShortOfTheArmGapGivesOneBridgingWarningWithBothSpacings()
    {
        var p = SdlChecker.Prepare("F4-wilkinson", _scratch);
        var tee = p.Model.Components.Single(c => c.InstanceName == "MT1");
        foreach (var name in new[] { "L1", "L2" }) tee.Parameters.Single(q => q.Name == name).Expression = "0.775";

        var plan = p.Plan();
        var warning = Assert.Single(plan.Lines, l => l.Text.Contains("bridges", StringComparison.Ordinal));
        output.WriteLine(warning.Text);
        Assert.Equal(SchematicLayoutPlanner.ReportSeverity.Warning, warning.Severity);
        Assert.Contains("R1 bridges", warning.Text);
        Assert.Contains("1550 µm apart", warning.Text);
        Assert.Contains("its pads span 1350 µm", warning.Text);
    }

    // ── D8: sides must match ─────────────────────────────────────────────────────────────────────

    /// <summary>F1 with its capacitor turned over to the bottom side: its pads cannot join the top-side lines without a
    /// via, so neither joint is made and each says why.</summary>
    [Fact]
    public void D8_APadOnTheBottomSideIsNotJoinedToATopSideLine()
    {
        var p = SdlChecker.Prepare("F1-dc-block", _scratch);
        var layout = p.Applied(p.Plan());
        Instance(layout, "C1").Side = PlacementSide.Bottom;

        var again = (p with { Target = layout }).Plan();
        var sides = again.Lines.Where(l => l.Text.Contains("different sides of the board", StringComparison.Ordinal)).ToList();
        Assert.Equal(["Net n2", "Net n3"], sides.Select(l => l.Text.Split(' ', 3)[0] + " " + l.Text.Split(' ', 3)[1]));
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────

    private const string Xml0603 = """
        <?xml version="1.0" encoding="utf-8"?>
        <partlib version="1.0">
         <drawing>
          <layers>
           <layer number="1" name="Top" color="4" fill="1" visible="yes" active="yes"/>
           <layer number="16" name="Bottom" color="1" fill="1" visible="yes" active="yes"/>
           <layer number="94" name="Symbols" color="4" fill="1" visible="yes" active="yes"/>
          </layers>
          <library>
           <packages>
            <package name="CAP0603">
             <smd name="1" x="-0.75" y="0" dx="0.8" dy="0.95" layer="1"/>
             <smd name="2" x="0.75" y="0" dx="0.8" dy="0.95" layer="1"/>
            </package>
           </packages>
           <symbols>
            <symbol name="CAP">
             <wire x1="-1.27" y1="0" x2="1.27" y2="0" width="0.254" layer="94"/>
             <pin name="A" x="-5.08" y="0" length="short" direction="pas" rot="R0"/>
             <pin name="B" x="5.08" y="0" length="short" direction="pas" rot="R180"/>
            </symbol>
           </symbols>
           <devicesets>
            <deviceset name="CAP0603" prefix="C">
             <gates><gate name="G$1" symbol="CAP" x="0" y="0"/></gates>
             <devices>
              <device name="" package="CAP0603">
               <connects>
                <connect gate="G$1" pin="A" pad="1"/>
                <connect gate="G$1" pin="B" pad="2"/>
               </connects>
               <technologies><technology name=""/></technologies>
              </device>
             </devices>
            </deviceset>
           </devicesets>
          </library>
         </drawing>
        </partlib>
        """;

    private static LayoutPin Pin(string name, long x, long y, double deg) => new() { Name = name, X = x, Y = y, OutwardDeg = deg };

    private static LayoutInstance Instance(LayoutView layout, string id) => layout.Instances.Single(i => i.SchematicId == id);

    /// <summary>Where a placed part's pin — its PadJoint for a footprint, its own pin for a line — lands.</summary>
    private static (double X, double Y) PinAt(LayoutView layout, SdlChecker.Prepared p, string id, string pin)
    {
        var inst = Instance(layout, id);
        var cell = CellLayoutResolver.Resolve(inst, p.LayoutDir, p.Tech).View!;
        var own = CellPins.Resolve(cell, p.Tech).Single(q => q.Name == pin);
        var at = cell.PCellOrigin is { } o && !o.GeneratorId.StartsWith("smt:", StringComparison.Ordinal)
            ? own
            : PadJoint.Of(cell, p.Tech).For(own)!.Joint;
        var pose = ChainPose.Of(inst);
        var (x, y, _) = pose.Apply(at);
        return (Math.Round(x), Math.Round(y));
    }

    /// <summary>Every part's top-copper rectangles in the layout's coordinates, by part, sorted.</summary>
    private static List<string> Copper(SdlChecker.Prepared p, LayoutView layout)
    {
        var result = new List<string>();
        foreach (var inst in layout.Instances.OrderBy(i => i.SchematicId, StringComparer.Ordinal))
        {
            var view = new LayoutView { DbuPerMicron = layout.DbuPerMicron, TechRef = layout.TechRef };
            view.Instances.Add(inst);
            var flat = LayoutDesignFlatten.Flatten(view, p.CellDir, p.Tech, null, resolvedCrossTechMappings: null);
            result.AddRange(flat.Shapes.Where(s => s.Layer == new LayerKey(1, 0))
                                .Select(s => $"{inst.SchematicId} {LayoutGeometry.BboxOf(s)}").Order(StringComparer.Ordinal));
        }
        return result;
    }
}

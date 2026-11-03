using CircuitRF.Ui.Layout;
using Xunit;

namespace CircuitRF.Ui.Tests.SchematicSync;

/// <summary>
/// brief-sdl-1 §6: connected placement, one test per decision (D2-D5), the turned branch-line against the upright
/// one, the setting off against the plan from before the series, and the run's single undo step. The designs
/// themselves (C1-C7) are gated by <see cref="SdlDesignSetTests"/>.
/// </summary>
public sealed class ChainPlacementTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "crf-sdl1-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { /* best-effort */ }
    }

    // ── D2: mirror comes from the schematic ──────────────────────────────────────────────────────

    /// <summary>C4 draws each bend kind right-handed and then mirrored: every mirrored bend turns the other way, so
    /// the line heading out of each pair heads the way the one coming in did.</summary>
    [Fact]
    public void D2_AMirroredBendTurnsTheOtherWay()
    {
        var p = SdlChecker.Prepare("C4-bends", _scratch);
        var layout = p.Applied(p.Plan());
        double Heading(string id) => layout.Instances.Single(i => i.SchematicId == id).RotationDegrees;
        double Turn(string from, string to) => Normalize(Heading(to) - Heading(from));

        Assert.Equal(-90, Turn("TL1", "TL2"), 9);   // BD1 as drawn: a right turn
        Assert.Equal(90, Turn("TL2", "TL3"), 9);    // BD2 mirrored: back left
        Assert.Equal(-30, Turn("TL3", "TL4"), 9);   // CV1 / CV2 at 30°
        Assert.Equal(30, Turn("TL4", "TL5"), 9);
        Assert.Equal(-45, Turn("TL5", "TL6"), 9);   // MI1 / MI2 at 45°
        Assert.Equal(45, Turn("TL6", "TL7"), 9);

        static double Normalize(double d) => LayoutAngle.Normalize(d + 180) - 180;
    }

    // ── D3: poses are exact until the instance is written ────────────────────────────────────────

    /// <summary>Twelve 30° curves in a row: the last free pin, read back through the WRITTEN instances, is within a
    /// DBU of the same chain computed in doubles from the curve's own pins.</summary>
    [Fact]
    public void D3_ATwelvePartThirtyDegreeChainEndsWithinOneDbuOfTheExactResult()
    {
        Assert.True(PCellRegistry.TryGet("MCURVE", out var curve));
        var pins = curve(new Dictionary<string, PCellValue>
                         {
                             ["W"] = PCellValue.Real(0.0011), ["Angle"] = PCellValue.Real(30), ["Radius"] = PCellValue.Real(0.003),
                         }, null, PCellLayerSelection.Default).Pins
                       .Select(q => new LayoutPin { Name = q.Name, X = q.X, Y = q.Y, WidthDbu = q.WidthDbu, OutwardDeg = q.OutwardDirectionDeg })
                       .ToList();

        var parts = Enumerable.Range(1, 12)
            .Select(k => new JointGraph.Part($"CV{k}", new LayoutInstance(), [pins[0], pins[1]], false, JointGraph.PartKind.Line) { Order = k - 1 })
            .ToList();
        var joints = parts.Zip(parts.Skip(1), (a, b) => new JointGraph.Joint($"n{a.Order}",
            new JointGraph.End(a, 2, pins[1]), new JointGraph.End(b, 1, pins[0]))).ToList();
        var result = ChainPlacer.Place(new JointGraph.Chain(parts, joints), parts[0], new ChainPose(0, 0, 0, false), _ => false);
        foreach (var part in parts) result.Poses[part].WriteTo(part.Instance);

        var (wx, wy) = LayoutInstanceTransform.TransformPoint(pins[1].X, pins[1].Y, parts[^1].Instance, 0, 0);

        // The same chain, written out independently: each curve starts where the last ended, turned 30° further.
        double x = 0, y = 0;
        for (int k = 0; k < 12; k++)
        {
            double t = k * 30 * Math.PI / 180;
            x += pins[1].X * Math.Cos(t) - pins[1].Y * Math.Sin(t);
            y += pins[1].X * Math.Sin(t) + pins[1].Y * Math.Cos(t);
        }
        Assert.True(Math.Abs(wx - x) <= 1 && Math.Abs(wy - y) <= 1, $"last pin ({wx}, {wy}), exact ({x:F3}, {y:F3})");
        Assert.Empty(result.Gaps);
    }

    // ── D4: a loop that does not close is reported, never forced ─────────────────────────────────

    /// <summary>Two parts joined at both ends, the second's far pin <paramref name="gap"/> DBU wider than the
    /// first's: within a DBU it is joined, beyond it is a gap, measured, and nothing moves to close it.</summary>
    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    public void D4_AOneDbuClosingGapIsJoinedAndATwoDbuGapIsReported(int gap, bool joined)
    {
        LayoutPin Pin(string name, long x, double deg) => new() { Name = name, X = x, WidthDbu = 100, OutwardDeg = deg };
        var a = new JointGraph.Part("A1", new LayoutInstance(), [Pin("1", 0, 90), Pin("2", 1000, 90)], false, JointGraph.PartKind.Line) { Order = 0 };
        var b = new JointGraph.Part("B1", new LayoutInstance(), [Pin("1", 0, 270), Pin("2", 1000 + gap, 270)], false, JointGraph.PartKind.Line) { Order = 1 };
        var chain = new JointGraph.Chain([a, b],
        [
            new("n1", new(a, 1, a.Terminals[0]!), new(b, 1, b.Terminals[0]!)),
            new("n2", new(a, 2, a.Terminals[1]!), new(b, 2, b.Terminals[1]!)),
        ]);

        var result = ChainPlacer.Place(chain, a, new ChainPose(0, 0, 0, false), _ => false);

        Assert.Equal(new ChainPose(0, 0, 0, false), result.Poses[b]);   // placed by the first joint, and left there
        if (joined)
        {
            Assert.Empty(result.Gaps);
            Assert.Equal(2, result.JoinedCount);
        }
        else
        {
            var g = Assert.Single(result.Gaps);
            Assert.Equal((2.0, 0.0, 0.0), (g.Dx, g.Dy, g.AngleDeg));
            Assert.Equal(["A1", "B1"], g.Loop.Select(part => part.SchematicId));
            Assert.Equal(1, result.JoinedCount);
        }
    }

    // ── D5: existing parts are anchors ───────────────────────────────────────────────────────────

    /// <summary>C1 placed, then TL1 moved by hand and TL3 deleted: a re-run moves neither TL1 nor TL2, puts TL3 back
    /// exactly on TL2's free end, and reports the TL1-TL2 joint the hand move opened.</summary>
    [Fact]
    public void D5_AnExistingPartDoesNotMove()
    {
        var p = SdlChecker.Prepare("C1-through-line", _scratch);
        var layout = p.Applied(p.Plan());
        var tl1 = layout.Instances.Single(i => i.SchematicId == "TL1");
        tl1.X += 5_000; tl1.Y += 7_000;
        layout.Instances.RemoveAll(i => i.SchematicId == "TL3");
        var before = LayoutPersistence.Serialize(layout);

        var rerun = (p with { Target = layout }).Plan();

        Assert.DoesNotContain(rerun.Edits, e => e is SchematicLayoutEdit.ReplaceInstance);
        var add = Assert.IsType<SchematicLayoutEdit.AddInstance>(Assert.Single(rerun.Edits));
        Assert.Equal("TL3", add.Instance.SchematicId);
        Assert.Equal(before, LayoutPersistence.Serialize(layout));   // the plan mutated nothing it was handed

        var tl2 = layout.Instances.Single(i => i.SchematicId == "TL2");
        Assert.Equal(PinAt(tl2, "2", p), PinAt(add.Instance, "1", p));
        Assert.Contains(rerun.Lines, l => l.Severity == SchematicLayoutPlanner.ReportSeverity.Warning
                                          && l.Text.StartsWith("TL1 pin 2 and TL2 pin 1 are joined in the schematic but do not meet",
                                                               StringComparison.Ordinal));
    }

    // ── C7: the same copper, drawn turned ────────────────────────────────────────────────────────

    /// <summary>C7 is C2 drawn a quarter turn round with every line mirrored end for end. Mapped by the rigid motion
    /// that takes C2's anchor (TL1) onto C7's, every pin of every part lands on its counterpart's, on the same cell.</summary>
    [Fact]
    public void C7_IsC2sCopperUpToTheRigidTransformThatMapsTheirAnchors()
    {
        var upright = SdlChecker.Prepare("C2-branch-line", _scratch);
        var turned = SdlChecker.Prepare("C7-turned", _scratch);
        var u = upright.Applied(upright.Plan());
        var t = turned.Applied(turned.Plan());

        (long X, long Y) U(string id, string pin) => PinAt(u.Instances.Single(i => i.SchematicId == id), pin, upright);
        (long X, long Y) T(string id, string pin) => PinAt(t.Instances.Single(i => i.SchematicId == id), pin, turned);
        var (a1, a2, b1, b2) = (U("TL1", "1"), U("TL1", "2"), T("TL1", "1"), T("TL1", "2"));
        double phi = Math.Atan2(b2.Y - b1.Y, b2.X - b1.X) - Math.Atan2(a2.Y - a1.Y, a2.X - a1.X);
        var (c, s) = (Math.Round(Math.Cos(phi), 12), Math.Round(Math.Sin(phi), 12));

        Assert.Equal(u.Instances.Count, t.Instances.Count);
        foreach (var inst in u.Instances)
        {
            var other = t.Instances.Single(i => i.SchematicId == inst.SchematicId);
            Assert.Equal(inst.CellRef, other.CellRef);
            foreach (var pin in Pins(inst, upright))
            {
                var (x, y) = U(inst.SchematicId!, pin.Name);
                double mx = b1.X + c * (x - a1.X) - s * (y - a1.Y), my = b1.Y + s * (x - a1.X) + c * (y - a1.Y);
                var (tx, ty) = T(inst.SchematicId!, pin.Name);
                Assert.True(Math.Abs(mx - tx) <= 1 && Math.Abs(my - ty) <= 1,
                            $"{inst.SchematicId} pin {pin.Name}: mapped ({mx}, {my}), turned design ({tx}, {ty})");
            }
        }
    }

    // ── R-sdl1-4: the setting ────────────────────────────────────────────────────────────────────

    /// <summary>Connect parts OFF: C2's layout is byte for byte the one the planner wrote before this brief —
    /// <c>golden/C2-branch-line.rows.clay</c>, written by the SDL-0 planner on this same design and technology.</summary>
    [Fact]
    public void SettingOff_TheC2PlanEqualsThePlanFromBeforeTheSeries()
    {
        var p = SdlChecker.Prepare("C2-branch-line", _scratch);
        string written = LayoutPersistence.Serialize(p.Applied(p.Plan(connectParts: false)));
        string golden = File.ReadAllText(Path.Combine(SdlChecker.DesignsRoot, "golden", "C2-branch-line.rows.clay"));
        Assert.Equal(golden.ReplaceLineEndings("\n"), written.ReplaceLineEndings("\n"));
    }

    // ── One undo step ────────────────────────────────────────────────────────────────────────────

    /// <summary>The GUI's command for C2 places twelve joined parts and records each, the anchor marked; one Undo
    /// takes every part, every placement record and every ground shape back out.</summary>
    [Fact]
    public void OneUndoRevertsTheWholeRun_PlacementRecordsIncluded()
    {
        var p = SdlChecker.Prepare("C2-branch-line", _scratch);
        var run = SchematicToLayoutGenerator.Run(p.Model, p.Target, p.SchematicDir, p.Workspace, p.LayoutDir, p.Tech,
                                                 p.TechPath, null, targetLayoutPath: p.ClayPath);
        run.Command!.Execute();

        Assert.Equal(12, p.Target.Instances.Count);
        Assert.Equal(12, p.Target.GeneratedPlacements.Count);
        Assert.Equal(["TL1"], p.Target.GeneratedPlacements.Where(e => e.Value.ChainAnchor).Select(e => e.Key));

        run.Command.Undo();

        Assert.Empty(p.Target.Instances);
        Assert.Empty(p.Target.GeneratedPlacements);
        Assert.Empty(p.Target.Shapes);
    }

    private static IReadOnlyList<LayoutPin> Pins(LayoutInstance inst, SdlChecker.Prepared p) =>
        CellPins.Resolve(CellLayoutResolver.Resolve(inst, p.LayoutDir, p.Tech).View!, p.Tech);

    private static (long X, long Y) PinAt(LayoutInstance inst, string name, SdlChecker.Prepared p)
    {
        var pin = Pins(inst, p).Single(q => q.Name == name);
        return LayoutInstanceTransform.TransformPoint(pin.X, pin.Y, inst, 0, 0);
    }
}

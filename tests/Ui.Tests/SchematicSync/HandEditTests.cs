using CircuitRF.Design.Layout.Pdn;
using CircuitRF.Ui.Layout;
using Xunit;
using Severity = CircuitRF.Design.Layout.SchematicSync.SchematicLayoutPlanner.ReportSeverity;
using Kind = CircuitRF.Design.Layout.SchematicSync.SchematicLayoutPlanner.ReportKind;

namespace CircuitRF.Ui.Tests.SchematicSync;

/// <summary>
/// brief-sdl-4 §5: re-runs that respect hand edits, scenarios E1-E14 over C1, C2 and F1. Each places the design, makes
/// the edits a designer would, changes the schematic, re-runs, and asserts exactly what moved and by how much — then
/// passes the result through <see cref="SdlChecker.Verify"/> (LVS, open nets, overlaps), naming any net the scenario
/// expects left open. Plus the three commands (Lock, Re-snap, Restore Removed Parts) and the lock's file form.
/// </summary>
public sealed class HandEditTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "crf-sdl4-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { /* best-effort */ }
    }

    // ── E1-E3: a straight chain arranged by hand, its middle line changed ────────────────────────

    /// <summary>E1: C1's far half turned into a U by hand (TL2 down from TL1's end, TL3 back beside it), the middle line
    /// lengthened: the U keeps its shape, TL3 moving exactly by how far TL2's end moved, along TL2's turned axis.</summary>
    [Fact]
    public void E1_AUShapeByHandKeepsItsShapeAndTheFarHalfMovesAlongTheMiddleLine()
    {
        var (p, layout) = Placed("C1-through-line");
        var a = PinAt(p, layout, "TL1", "2");
        Turn(layout, a, -90, "TL2", "TL3");
        Turn(layout, PinAt(p, layout, "TL2", "2"), -90, "TL3");
        var tl3 = Inst(layout, "TL3");
        var (wasX, wasY, wasDeg) = (tl3.X, tl3.Y, tl3.RotationDegrees);
        var end = PinAt(p, layout, "TL2", "2");

        Set(p, "TL2", "L", "4");
        var (plan, after) = Rerun(p, layout);

        var moved = PinAt(p, after, "TL2", "2");
        Assert.Equal((0L, -1_000_000L), (moved.X - end.X, moved.Y - end.Y));   // TL2 is turned to point down
        Assert.Equal((wasX, wasY - 1_000_000L, wasDeg), (Inst(after, "TL3").X, Inst(after, "TL3").Y, Inst(after, "TL3").RotationDegrees));
        Assert.Equal((Inst(layout, "TL1").X, Inst(layout, "TL1").Y), (Inst(after, "TL1").X, Inst(after, "TL1").Y));
        Assert.Contains(plan.Lines, l => l.Text.StartsWith("TL2 — moved TL3 by", StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Lines, l => l.Kind == Kind.LoopGap);
        Assert.Empty(SdlChecker.Verify(p, after));
    }

    /// <summary>E2: TL3 turned a quarter turn by hand about its joint: it moves along TL2's axis and stays turned.</summary>
    [Fact]
    public void E2_AFarHalfTurnedByHandMovesAlongTheMiddleLineAndStaysTurned()
    {
        var (p, layout) = Placed("C1-through-line");
        Turn(layout, PinAt(p, layout, "TL3", "1"), 90, "TL3");
        var was = Clone(Inst(layout, "TL3"));

        Set(p, "TL2", "L", "4");
        var (_, after) = Rerun(p, layout);

        Assert.Equal((was.X + 1_000_000L, was.Y, was.RotationDegrees),
                     (Inst(after, "TL3").X, Inst(after, "TL3").Y, Inst(after, "TL3").RotationDegrees));
        Assert.Empty(SdlChecker.Verify(p, after));
    }

    /// <summary>E3: the middle line shortened by 1 mm: the far half moves back by exactly that.</summary>
    [Fact]
    public void E3_AShorterMiddleLinePullsTheFarHalfBack()
    {
        var (p, layout) = Placed("C1-through-line");
        var was = Clone(Inst(layout, "TL3"));

        Set(p, "TL2", "L", "2");
        var (_, after) = Rerun(p, layout);

        Assert.Equal((was.X - 1_000_000L, was.Y), (Inst(after, "TL3").X, Inst(after, "TL3").Y));
        Assert.Empty(SdlChecker.Verify(p, after));
    }

    // ── E4, E5: the branch-line's loop ───────────────────────────────────────────────────────────

    /// <summary>E4: L90 changes every arm alike, so the loop closes again with no gap: MT2 moves by ΔL along ML1, MT3 by
    /// ΔL along ML3, and MT4 by both — what the two routes round the loop agree on.</summary>
    [Fact]
    public void E4_BothArmsChangedAlikeMoveTheFarSideCleanly()
    {
        var (p, layout) = Placed("C2-branch-line");
        Set(p, "VAR1", "L90", "4.7604");
        var (plan, after) = Rerun(p, layout);

        Assert.Equal(100_000, Shift(layout, after, "MT2"), 1.0);
        Assert.Equal(100_000, Shift(layout, after, "MT3"), 1.0);
        Assert.Equal(100_000 * Math.Sqrt(2), Shift(layout, after, "MT4"), 2.0);
        Assert.Equal(0, Shift(layout, after, "MT1"));
        Assert.DoesNotContain(plan.Lines, l => l.Kind == Kind.LoopGap);
        Assert.Empty(SdlChecker.Verify(p, after));
    }

    /// <summary>E5: only ML1 longer: its far side (MT2 and what hangs from it, round to MT4) moves by ΔL, the loop's
    /// other side does not, and the one joint between them that cannot close — at ML2 — is the run's one loop gap.</summary>
    [Fact]
    public void E5_OneArmChangedMovesItsFarSideAndReportsOneLoopGap()
    {
        var (p, layout) = Placed("C2-branch-line");
        Set(p, "ML1", "L", "L90-w50/2+100e-6");   // the row states no unit: SI
        var (plan, after) = Rerun(p, layout);

        foreach (string id in new[] { "MT2", "ML4", "TL2", "MT4", "TL3" }) Assert.Equal(100_000, Shift(layout, after, id), 1.0);
        foreach (string id in new[] { "MT1", "ML3", "MT3", "ML2", "TL4", "TL1" }) Assert.Equal(0, Shift(layout, after, id));
        var gap = Assert.Single(plan.Lines, l => l.Kind == Kind.LoopGap);
        Assert.Equal(Severity.Warning, gap.Severity);
        Assert.Contains("ML2", gap.Ids);
        Assert.Empty(SdlChecker.Verify(p, after, openAllowed: [NetOf(p, "ML2", 2)], lvsAllowed: ["lvs.net.open"]));
    }

    // ── E6-E8: which side stays ──────────────────────────────────────────────────────────────────

    /// <summary>E6: the far port's line locked: the near side moves instead, back by ΔL.</summary>
    [Fact]
    public void E6_ALockedFarPartMakesTheNearSideMove()
    {
        var (p, layout) = Placed("C1-through-line");
        Inst(layout, "TL3").Locked = true;
        var (tl1, tl3) = (Clone(Inst(layout, "TL1")), Clone(Inst(layout, "TL3")));

        Set(p, "TL2", "L", "4");
        var (_, after) = Rerun(p, layout);

        Assert.Equal((tl3.X, tl3.Y), (Inst(after, "TL3").X, Inst(after, "TL3").Y));
        Assert.Equal((tl1.X - 1_000_000L, tl1.Y), (Inst(after, "TL1").X, Inst(after, "TL1").Y));
        Assert.Empty(SdlChecker.Verify(p, after));
    }

    /// <summary>E7: both end parts locked: nothing moves, and one warning names both holds and the gap left.</summary>
    [Fact]
    public void E7_BothSidesLockedMovesNothingAndReportsTheGap()
    {
        var (p, layout) = Placed("C1-through-line");
        Inst(layout, "TL1").Locked = true;
        Inst(layout, "TL3").Locked = true;

        Set(p, "TL2", "L", "4");
        var (plan, after) = Rerun(p, layout);

        foreach (string id in new[] { "TL1", "TL2", "TL3" }) Assert.Equal(0, Shift(layout, after, id));
        var line = Assert.Single(plan.Lines, l => l.Kind == Kind.LoopGap);
        Assert.Equal(Severity.Warning, line.Severity);
        Assert.Equal("TL2 is 1000 µm longer; both sides are held (TL1 is locked; TL3 is locked). Left a 1000 µm gap at TL2 pin 2.",
                     line.Text);
        Assert.Empty(SdlChecker.Verify(p, after, openAllowed: [NetOf(p, "TL3", 1)], lvsAllowed: ["lvs.net.open"]));
    }

    /// <summary>E8: a connector with no schematic link placed against the far line holds that side: lengthening the near
    /// line moves the near line itself, back by ΔL, and nothing else.</summary>
    [Fact]
    public void E8_AnUnlinkedConnectorHoldsTheFarSide()
    {
        var (p, layout) = Placed("F1-dc-block");
        var tl2 = Inst(layout, "TL2");
        var (a, b) = (PinAt(p, layout, "TL2", "1"), PinAt(p, layout, "TL2", "2"));
        var connector = Clone(tl2);
        (connector.SchematicId, connector.OrientationLink) = (null, null);
        connector.X += b.X - a.X;
        connector.Y += b.Y - a.Y;
        layout.Instances.Add(connector);
        var tl1 = Clone(Inst(layout, "TL1"));

        Set(p, "TL1", "L", "6");
        var (_, after) = Rerun(p, layout);

        Assert.Equal((tl1.X - 1_000_000L, tl1.Y), (Inst(after, "TL1").X, Inst(after, "TL1").Y));
        foreach (string id in new[] { "C1", "TL2" }) Assert.Equal(0, Shift(layout, after, id));
        Assert.Equal((connector.X, connector.Y), (after.Instances[^1].X, after.Instances[^1].Y));
        Assert.Empty(SdlChecker.Verify(p, after, lvsAllowed: ["lvs.device.unmatched-layout"]));   // the connector itself
    }

    /// <summary>E9: ML1 deleted and a meander drawn from MT1 to MT2 in its place; L90 changed. The meander holds the top
    /// together: MT3 and MT4 move down their arms by ΔL, the top stays, and the bottom arm's own change cannot be taken —
    /// reported as both sides held, one of them by the drawn copper.</summary>
    [Fact]
    public void E9_AMeanderDrawnInPlaceOfALineHoldsTheTop()
    {
        var (p, layout) = Placed("C2-branch-line");
        var (a, b) = (PinAt(p, layout, "ML1", "1"), PinAt(p, layout, "ML1", "2"));
        var pin = Pins(Inst(layout, "ML1"), p).Single(q => q.Name == "1");
        layout.Instances.RemoveAll(i => i.SchematicId == "ML1");
        var mt3 = Inst(layout, "MT3");
        long side = Math.Sign(a.Y - mt3.Y) * 1_000_000L;   // away from the loop: ML1 runs along x
        var meander = new PathShape
        {
            Layer = pin.Layer, Width = pin.WidthDbu, Xy = [a.X, a.Y, a.X, a.Y + side, b.X, b.Y + side, b.X, b.Y],
        };
        layout.Shapes.Add(meander);

        Set(p, "VAR1", "L90", "4.7604");
        var (plan, after) = Rerun(p, layout);

        foreach (string id in new[] { "TL1", "MT1", "MT2", "TL2" }) Assert.Equal(0, Shift(layout, after, id));
        foreach (string id in new[] { "MT3", "MT4", "TL3", "TL4" }) Assert.Equal(100_000, Shift(layout, after, id), 1.0);
        Assert.Equal(meander.Xy, ((PathShape)after.Shapes.Single(s => s is PathShape { Generated: null })).Xy);
        Assert.Contains(plan.Lines, l => l.Text.StartsWith("ML1 — removed from the layout by hand", StringComparison.Ordinal));
        var held = Assert.Single(plan.Lines, l => l.Kind == Kind.LoopGap);
        Assert.StartsWith("ML2 is 100 µm longer; both sides are held (TL1 anchors the chain; MT2 is joined to copper drawn on ",
                          held.Text);
        Assert.EndsWith("gap at ML2 pin 2.", held.Text);
        // What the edit itself makes LVS say: ML1 is gone, and drawn copper joins the two nets it separated.
        Assert.Empty(SdlChecker.Verify(p, after, openAllowed: [NetOf(p, "ML2", 2)],
                                       lvsAllowed: ["lvs.net.open", "lvs.device.unmatched-schematic", "lvs.net.short"]));
    }

    /// <summary>E10: a short stub drawn against TL3 alone moves with it, and the report says so (D7).</summary>
    [Fact]
    public void E10_AStubTouchingOnlyTheFarSideMovesWithIt()
    {
        var (p, layout) = Placed("C1-through-line");
        var (a, b) = (PinAt(p, layout, "TL3", "1"), PinAt(p, layout, "TL3", "2"));
        var pin = Pins(Inst(layout, "TL3"), p).Single(q => q.Name == "1");
        long mid = (a.X + b.X) / 2, edge = a.Y + pin.WidthDbu / 2;
        var stub = new RectShape { Layer = pin.Layer, X1 = mid - 100_000, X2 = mid + 100_000, Y1 = edge - 10_000, Y2 = edge + 500_000 };
        layout.Shapes.Add(stub);

        Set(p, "TL2", "L", "4");
        var (plan, after) = Rerun(p, layout);

        var moved = Assert.IsType<RectShape>(after.Shapes.Single(s => s is RectShape { Generated: null }));
        Assert.Equal((stub.X1 + 1_000_000L, stub.Y1, stub.X2 + 1_000_000L, stub.Y2), (moved.X1, moved.Y1, moved.X2, moved.Y2));
        Assert.Contains(plan.Lines, l => l.Text.StartsWith("TL2 — moved TL3 and 1 shape of drawn copper by", StringComparison.Ordinal));
        Assert.Empty(SdlChecker.Verify(p, after));
    }

    /// <summary>E11: TL3 pulled 2 mm away by hand: the run does not follow the open joint, TL3 stays, and the joint is
    /// said as Info.</summary>
    [Fact]
    public void E11_AJointPulledOpenByHandStaysOpenAndIsInfo()
    {
        var (p, layout) = Placed("C1-through-line");
        Inst(layout, "TL3").X += 2_000_000;
        Port(layout, "P2").X += 2_000_000;   // the line pulled away with its port (brief-sdl-5)
        var was = Clone(Inst(layout, "TL3"));

        Set(p, "TL2", "L", "4");
        var (plan, after) = Rerun(p, layout);

        Assert.Equal((was.X, was.Y), (Inst(after, "TL3").X, Inst(after, "TL3").Y));
        var open = Assert.Single(plan.Lines, l => l.Kind == Kind.LoopGap);
        Assert.Equal(Severity.Info, open.Severity);
        Assert.StartsWith("TL2 pin 2 and TL3 pin 1 are 1000 µm apart", open.Text);
        Assert.Empty(SdlChecker.Verify(p, after, openAllowed: [NetOf(p, "TL3", 1)], lvsAllowed: ["lvs.net.open"]));
    }

    /// <summary>E12: ML1's W set in the layout, then changed in the schematic too: the layout's W is kept, with a warning
    /// naming both values, and nothing moves (a width moves no pin).</summary>
    [Fact]
    public void E12_AWidthChangedOnBothSidesKeepsTheLayoutsAndWarns()
    {
        var (p, layout) = Placed("C2-branch-line");
        var ml1 = Inst(layout, "ML1");
        var origin = CellLayoutResolver.Resolve(ml1.CellRef, p.LayoutDir).View!.PCellOrigin!;
        string cell = GeneratedCellStore.GetOrCreate(p.Workspace, "MLIN",
            new Dictionary<string, PCellValue>(origin.Parameters) { ["W"] = 300e-6 }, p.Tech, p.TechPath, PCellLayerSelection.Default);
        ml1.CellRef = CircuitRF.Core.RefPath.ToStored(Path.GetRelativePath(p.LayoutDir, cell));

        Set(p, "ML1", "W", "290");
        var (plan, after) = Rerun(p, layout);

        Assert.Equal(300e-6, CellLayoutResolver.Resolve(Inst(after, "ML1").CellRef, p.LayoutDir).View!.PCellOrigin!.Parameters.Real("W"), 9);
        var line = Assert.Single(plan.Lines, l => l.Text.Contains("W was changed in both", StringComparison.Ordinal));
        Assert.Equal(Severity.Warning, line.Severity);
        Assert.Contains("is kept; the schematic changed it from", line.Text);
        foreach (var inst in layout.Instances) Assert.Equal(0, Shift(layout, after, inst.SchematicId!));
    }

    // ── E13: one undo step ───────────────────────────────────────────────────────────────────────

    /// <summary>E13: several of the edits above on one design — a turn by hand, drawn copper, a layout width, a deleted
    /// part — and a schematic change that moves parts and shapes and writes records, snapshots and baselines: one Undo
    /// leaves the layout byte-identical to before the re-run.</summary>
    [Fact]
    public void E13_OneUndoRestoresTheLayoutByteForByte()
    {
        var p = SdlChecker.Prepare("C2-branch-line", _scratch);
        var first = SchematicToLayoutGenerator.Run(p.Model, p.Target, p.SchematicDir, p.Workspace, p.LayoutDir, p.Tech,
                                                   p.TechPath, null, targetLayoutPath: p.ClayPath);
        first.Command!.Execute();
        var layout = p.Target;

        Turn(layout, PinAt(p, layout, "TL2", "1"), 90, "TL2");
        var pin = Pins(Inst(layout, "TL3"), p).Single(q => q.Name == "1");
        var (a, b) = (PinAt(p, layout, "TL3", "1"), PinAt(p, layout, "TL3", "2"));
        layout.Shapes.Add(new RectShape
        {
            Layer = pin.Layer, X1 = (a.X + b.X) / 2 - 100_000, X2 = (a.X + b.X) / 2 + 100_000,
            Y1 = a.Y - pin.WidthDbu / 2 - 500_000, Y2 = a.Y - pin.WidthDbu / 2 + 10_000,
        });
        var ml3 = Inst(layout, "ML3");
        var origin = CellLayoutResolver.Resolve(ml3.CellRef, p.LayoutDir).View!.PCellOrigin!;
        ml3.CellRef = CircuitRF.Core.RefPath.ToStored(Path.GetRelativePath(p.LayoutDir, GeneratedCellStore.GetOrCreate(
            p.Workspace, "MLIN", new Dictionary<string, PCellValue>(origin.Parameters) { ["W"] = 300e-6 }, p.Tech, p.TechPath,
            PCellLayerSelection.Default)));
        layout.Instances.RemoveAll(i => i.SchematicId == "TL4");
        string before = LayoutPersistence.Serialize(layout);

        Set(p, "VAR1", "L90", "4.7604");
        Set(p, "ML3", "W", "290");
        var rerun = SchematicToLayoutGenerator.Run(p.Model, layout, p.SchematicDir, p.Workspace, p.LayoutDir, p.Tech,
                                                   p.TechPath, null, targetLayoutPath: p.ClayPath);
        rerun.Command!.Execute();
        Assert.NotEqual(before, LayoutPersistence.Serialize(layout));
        Assert.True(layout.GeneratedPlacements["TL4"].Removed);

        rerun.Command.Undo();
        Assert.Equal(before, LayoutPersistence.Serialize(layout));
    }

    // ── E14: an older layout ─────────────────────────────────────────────────────────────────────

    /// <summary>E14: a layout with no placement table: the parts beyond the change move (the anchor falls back to the
    /// part on port 1), and the run says once that they had no record.</summary>
    [Fact]
    public void E14_ALayoutWithNoPlacementTableStillMovesWhatIsBeyondTheChange()
    {
        var (p, layout) = Placed("C1-through-line");
        layout.GeneratedPlacements.Clear();
        var was = Clone(Inst(layout, "TL3"));

        Set(p, "TL2", "L", "4");
        var (plan, after) = Rerun(p, layout);

        Assert.Equal((was.X + 1_000_000L, was.Y), (Inst(after, "TL3").X, Inst(after, "TL3").Y));
        Assert.Equal(0, Shift(layout, after, "TL1"));
        var line = Assert.Single(plan.Lines, l => l.Text.Contains("placed before Update Layout kept a placement record",
                                                                  StringComparison.Ordinal));
        Assert.Equal(["TL3"], line.Ids);
        Assert.Empty(SdlChecker.Verify(p, after));
    }

    // ── R-sdl4-2: the three commands ─────────────────────────────────────────────────────────────

    /// <summary>A deleted part is not put back, and says so; Restore Removed Parts puts it back on its neighbour's end.</summary>
    [Fact]
    public void RestoreRemovedParts_PutsADeletedPartBackJoined()
    {
        var (p, layout) = Placed("C1-through-line");
        layout.Instances.RemoveAll(i => i.SchematicId == "TL3");

        var (plain, left) = Rerun(p, layout);
        Assert.DoesNotContain(left.Instances, i => i.SchematicId == "TL3");
        Assert.Contains(plain.Lines, l => l.Severity == Severity.Info && l.Text.Contains("Restore Removed Parts"));
        Assert.True(left.GeneratedPlacements["TL3"].Removed);

        var (_, restored) = Rerun(p, left, restoreRemoved: true);
        Assert.Equal(PinAt(p, restored, "TL2", "2"), PinAt(p, restored, "TL3", "1"));
        Assert.False(restored.GeneratedPlacements["TL3"].Removed);
        Assert.Empty(SdlChecker.Verify(p, restored));
    }

    /// <summary>Re-snap on a part pulled away closes the joint it had opened; nothing else moves.</summary>
    [Fact]
    public void Resnap_ClosesAJointTheUserOpened()
    {
        var (p, layout) = Placed("C1-through-line");
        Inst(layout, "TL3").X += 2_000_000;

        var (plan, after) = Rerun(p, layout, resnap: new HashSet<string> { "TL3" });

        Assert.Equal(PinAt(p, after, "TL2", "2"), PinAt(p, after, "TL3", "1"));
        Assert.Equal(0, Shift(layout, after, "TL2"));
        Assert.DoesNotContain(plan.Lines, l => l.Kind == Kind.LoopGap);
        Assert.Empty(SdlChecker.Verify(p, after));
    }

    /// <summary>The lock is written only when set, and survives a clone (every properties edit and move goes through
    /// one).</summary>
    [Fact]
    public void Locked_IsWrittenOnlyWhenSetAndSurvivesAClone()
    {
        var view = new LayoutView();
        view.Instances.Add(new LayoutInstance { CellRef = "a" });
        Assert.DoesNotContain("Locked", LayoutPersistence.Serialize(view));
        view.Instances[0].Locked = true;
        Assert.Contains("\"Locked\": true", LayoutPersistence.Serialize(view));
        Assert.True(LayoutPersistence.Deserialize(LayoutPersistence.Serialize(view)).Instances[0].Locked);
        Assert.True(LayoutGeometry.Clone(view.Instances[0]).Locked);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────

    private (SdlChecker.Prepared P, LayoutView Layout) Placed(string design)
    {
        var p = SdlChecker.Prepare(design, _scratch);
        return (p, p.Applied(p.Plan()));
    }

    private static (SchematicLayoutPlan Plan, LayoutView After) Rerun(SdlChecker.Prepared p, LayoutView layout,
                                                                     IReadOnlySet<string>? resnap = null, bool restoreRemoved = false)
    {
        var q = p with { Target = layout };
        var plan = q.Plan(resnap: resnap, restoreRemoved: restoreRemoved);
        return (plan, q.Applied(plan));
    }

    private static void Set(SdlChecker.Prepared p, string id, string param, string expression) =>
        p.Model.Components.Single(c => c.InstanceName == id).Parameters.Single(q => q.Name == param).Expression = expression;

    private static LayoutInstance Inst(LayoutView layout, string id) => layout.Instances.Single(i => i.SchematicId == id);

    private static LabelShape Port(LayoutView layout, string id) =>
        layout.Shapes.OfType<LabelShape>().Single(l => l.IsPort && l.SchematicId == id);

    private static LayoutInstance Clone(LayoutInstance inst) => LayoutGeometry.Clone(inst);

    /// <summary>How far a part's origin moved between two layouts, in DBU.</summary>
    private static double Shift(LayoutView from, LayoutView to, string id)
    {
        var (a, b) = (Inst(from, id), Inst(to, id));
        return Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2));
    }

    /// <summary>Turns the named parts about <paramref name="pivot"/> as one, as a drag-and-rotate by hand would.</summary>
    private static void Turn(LayoutView layout, (long X, long Y) pivot, double deg, params string[] ids)
    {
        var (c, s) = LayoutAngle.CosSin(deg);
        foreach (string id in ids)
        {
            var inst = Inst(layout, id);
            double dx = inst.X - pivot.X, dy = inst.Y - pivot.Y;
            inst.X = pivot.X + (long)Math.Round(dx * c - dy * s);
            inst.Y = pivot.Y + (long)Math.Round(dx * s + dy * c);
            inst.RotationDegrees += deg;
        }
    }

    private static string NetOf(SdlChecker.Prepared p, string id, int terminal) =>
        PdnSchematicNets.Of(p.Model, p.SchematicPath).For(id)!.Nets[terminal - 1];

    private static IReadOnlyList<LayoutPin> Pins(LayoutInstance inst, SdlChecker.Prepared p) =>
        CellPins.Resolve(CellLayoutResolver.Resolve(inst, p.LayoutDir, p.Tech).View!, p.Tech);

    private static (long X, long Y) PinAt(SdlChecker.Prepared p, LayoutView layout, string id, string name)
    {
        var inst = Inst(layout, id);
        var pin = Pins(inst, p).Single(q => q.Name == name);
        return LayoutInstanceTransform.TransformPoint(pin.X, pin.Y, inst, 0, 0);
    }
}

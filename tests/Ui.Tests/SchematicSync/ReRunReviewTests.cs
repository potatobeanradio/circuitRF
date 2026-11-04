using Xunit;
using Severity = CircuitRF.Design.Layout.SchematicSync.SchematicLayoutPlanner.ReportSeverity;
using Kind = CircuitRF.Design.Layout.SchematicSync.SchematicLayoutPlanner.ReportKind;

namespace CircuitRF.Ui.Tests.SchematicSync;

/// <summary>
/// Re-runs of Update Layout from Schematic over the test designs that the scenario files did not reach: a chain whose
/// first part is a side-on one, placement records outliving their parts, both ends of a chain held, and a locked part
/// asked to turn or re-snap.
/// </summary>
public sealed class ReRunReviewTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "crf-sdl-rerun-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>F3's first part in schematic-id order is a shunt resistor, which has no joint of its own: a re-run with
    /// nothing changed must still walk the chain from a part that has one, and count what the first run joined.</summary>
    [Fact]
    public void F3_AReRunWithNothingChanged_CountsTheJointsTheFirstRunMade()
    {
        var (p, layout) = Placed("F3-pi-attenuator");
        var first = Joined(p.Plan());
        var (plan, _) = Rerun(p, layout);
        Assert.Equal(first, Joined(plan));
    }

    /// <summary>A footprint set to None takes its part out of the layout, and set back puts it in again: the run
    /// removed it, not the user, so it is not "removed by hand".</summary>
    [Fact]
    public void F1_AFootprintSetToNoneAndBack_IsPlacedAgain()
    {
        var (p, layout) = Placed("F1-dc-block");
        var footprint = p.Model.Components.Single(c => c.InstanceName == "C1").Parameters.Single(q => q.Name == "Footprint");
        string was = footprint.Expression;

        footprint.Expression = "";
        var (_, none) = Rerun(p, layout);
        Assert.DoesNotContain(none.Instances, i => i.SchematicId == "C1");
        Assert.False(none.GeneratedPlacements.ContainsKey("C1"));

        footprint.Expression = was;
        var (plan, back) = Rerun(p, none);
        Assert.Contains(back.Instances, i => i.SchematicId == "C1");
        Assert.DoesNotContain(plan.Lines, l => l.Text.Contains("removed from the layout by hand", StringComparison.Ordinal));
    }

    /// <summary>A part gone from the schematic and the layout both leaves no record, so the next part given its name is a
    /// new part rather than one "removed by hand".</summary>
    [Fact]
    public void C1_APartGoneFromBothSides_LeavesNoRecord()
    {
        var (p, layout) = Placed("C1-through-line");
        layout.Instances.RemoveAll(i => i.SchematicId == "TL3");
        p.Model.Components.RemoveAll(c => c.InstanceName == "TL3");
        var (_, after) = Rerun(p, layout);
        Assert.False(after.GeneratedPlacements.ContainsKey("TL3"));
    }

    /// <summary>H1's two ends locked and a line changed inside it: nothing moves, whichever line changed — so the answer
    /// does not depend on which locked part sorts first — and the gap is reported at the changed line's own pin.</summary>
    [Theory]
    [InlineData("TL2", "4", "6")]
    [InlineData("TL4", "4", "6")]
    public void H1_BothEndsLocked_AChangeInsideMovesNothing(string id, string from, string to)
    {
        var (p, layout) = Placed("H1-stepped-lowpass");
        Inst(layout, "TL1").Locked = true;
        Inst(layout, "TL5").Locked = true;
        var param = p.Model.Components.Single(c => c.InstanceName == id).Parameters.Single(q => q.Name == "L");
        Assert.Equal(from, param.Expression);
        param.Expression = to;

        var (plan, after) = Rerun(p, layout);

        foreach (string part in new[] { "TL1", "TL2", "TL3", "TL4", "TL5" })
            Assert.Equal((Inst(layout, part).X, Inst(layout, part).Y), (Inst(after, part).X, Inst(after, part).Y));
        var line = Assert.Single(plan.Lines, l => l.Kind == Kind.LoopGap);
        Assert.Equal(Severity.Warning, line.Severity);
        Assert.Equal($"{id} is 2000 µm longer; both sides are held (TL1 is locked; TL5 is locked). {id} pin 2 now overlaps {(id == "TL2" ? "TL3" : "TL5")} by 2000 µm.",
                     line.Text);
    }

    /// <summary>A locked part is not turned by a rotation carried from the schematic (a turn about its origin moves its
    /// pins), and Re-snap leaves it as it is; both say so.</summary>
    [Fact]
    public void C1_ALockedPart_IsNeitherTurnedNorReSnapped()
    {
        var (p, layout) = Placed("C1-through-line");
        var tl3 = Inst(layout, "TL3");
        tl3.Locked = true;
        var was = (tl3.X, tl3.Y, tl3.RotationDegrees);

        p.Model.Components.Single(c => c.InstanceName == "TL3").Rotation = SymbolRotation.R90;
        var (plan, after) = Rerun(p, layout);
        Assert.Equal(was, (Inst(after, "TL3").X, Inst(after, "TL3").Y, Inst(after, "TL3").RotationDegrees));
        Assert.Contains(plan.Lines, l => l.Text == "TL3 — locked, so the schematic's turn was not carried (unlock it and run again to turn it)");

        var (resnapped, again) = Rerun(p, after, resnap: new HashSet<string> { "TL3" });
        Assert.Equal(was, (Inst(again, "TL3").X, Inst(again, "TL3").Y, Inst(again, "TL3").RotationDegrees));
        Assert.Contains(resnapped.Lines, l => l.Text == "TL3 — locked, so Re-snap left it as it is (unlock it to re-snap it)");
    }

    private (SdlChecker.Prepared P, LayoutView Layout) Placed(string design)
    {
        var p = SdlChecker.Prepare(design, _scratch);
        return (p, p.Applied(p.Plan()));
    }

    private static (SchematicLayoutPlan Plan, LayoutView After) Rerun(SdlChecker.Prepared p, LayoutView layout,
                                                                     IReadOnlySet<string>? resnap = null)
    {
        var q = p with { Target = layout };
        var plan = q.Plan(resnap: resnap);
        return (plan, q.Applied(plan));
    }

    private static LayoutInstance Inst(LayoutView layout, string id) => layout.Instances.Single(i => i.SchematicId == id);

    private static string Joined(SchematicLayoutPlan plan) =>
        Assert.Single(plan.Lines, l => l.Text.StartsWith("Joined ", StringComparison.Ordinal)).Text;
}

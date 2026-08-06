using System.Linq;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Drc;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// L5b: a waiver must be "per-violation, persisted, and visible" (§9A.1). Persisted means it survives
/// a save/reload of the layout it belongs to — and, just as importantly, that a waiver-free layout
/// re-serializes byte-for-byte so no existing <c>.clay</c> changes on disk merely by being opened.
/// </summary>
public class DrcWaiverPersistenceTests
{
    private static LayoutView Layout()
    {
        var v = new LayoutView { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 100 };
        v.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 1000, Y2 = 60 });
        return v;
    }

    [Fact]
    public void AWaiver_SurvivesASaveAndReload()
    {
        var view = Layout();
        view.DrcWaivers.Add(new DrcWaiver
        {
            Key      = "MinWidth|M1 min width|1/0|0,0,1000,60",
            Reason   = "deliberate taper into the pad",
            RuleName = "M1 min width",
        });

        var json     = LayoutPersistence.Serialize(view);
        var reloaded = LayoutPersistence.Deserialize(json);

        var w = Assert.Single(reloaded.DrcWaivers);
        Assert.Equal("MinWidth|M1 min width|1/0|0,0,1000,60", w.Key);
        Assert.Equal("deliberate taper into the pad", w.Reason);
        Assert.Equal("M1 min width", w.RuleName);
    }

    /// <summary>
    /// Additive, no <c>FormatVersion</c> bump — the same contract every other additive `.clay` field
    /// in this codebase holds itself to.
    /// </summary>
    [Fact]
    public void AWaiverFreeLayout_ReSerializesByteForByte_AndMentionsNoWaiverField()
    {
        var view = Layout();
        string json = LayoutPersistence.Serialize(view);

        Assert.DoesNotContain("DrcWaivers", json);
        Assert.Equal(json, LayoutPersistence.Serialize(LayoutPersistence.Deserialize(json)));
    }

    [Fact]
    public void AClayWrittenBeforeWaiversExisted_StillLoads()
    {
        // Hand-authored: no DrcWaivers property at all.
        const string json = """
            {
              "FormatVersion": 1,
              "DbuPerMicron": 1000,
              "DisplayUnit": "Um",
              "SnapDbu": 100,
              "AngleMode": "AnyAngle",
              "Shapes": [],
              "Instances": []
            }
            """;

        var view = LayoutPersistence.Deserialize(json);
        Assert.Empty(view.DrcWaivers);
    }

    /// <summary>
    /// The round trip that actually matters: waive a real violation, save, reload, re-check — and the
    /// same violation comes back already waived. If the key were derived from anything that moves
    /// between runs, this is where it would show.
    /// </summary>
    [Fact]
    public void WaiveThenSaveThenReloadThenRecheck_KeepsTheSameViolationWaived()
    {
        var view = Layout();
        var tech = new Technology
        {
            Name   = "T",
            Layers = [new LayerDef { Key = new LayerKey(1, 0), Name = "M1" }],
            DrcRules =
            [
                new DrcRule { Name = "M1 min width", Kind = DrcRuleKind.MinWidth, Layer = new LayerKey(1, 0), ValueDbu = 100 },
            ],
        };

        var first = DrcEngine.Run(view.Shapes, tech);
        var key   = Assert.Single(first.Violations).Key;
        view.DrcWaivers.Add(new DrcWaiver { Key = key, Reason = "reviewed", RuleName = "M1 min width" });

        var reloaded = LayoutPersistence.Deserialize(LayoutPersistence.Serialize(view));
        var second   = DrcEngine.Run(reloaded.Shapes, tech, reloaded.DrcWaivers);

        var v = Assert.Single(second.Violations);
        Assert.True(v.Waived);
        Assert.Equal("reviewed", v.WaiverReason);
        Assert.True(second.IsClean);
    }
}

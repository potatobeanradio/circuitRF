using System.Collections.Generic;
using System.Linq;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Tests;

// ── Phase L1g gates 2/4/5: docs/sonnet-briefs/brief-L1g-technology-retarget.md
// Pure, framework-free tests of LayoutLayerMapping.Propose — the shared component behind both
// cross-technology paste reconciliation and technology retargeting. See LayoutRetargetTests.cs for
// the VM-level (command/undo/Messages) gates, and LayoutClipboardViewModelTests.cs for the paste-path
// wiring (gate 2's Drill/Soldermask regression, gate 3's same-tech-stays-silent).

public class LayoutLayerMappingTests
{
    private static readonly LayerKey Drill = new(7, 0);
    private static readonly LayerKey Soldermask = new(3, 0);

    private static List<LayoutShape> ShapesOn(params (LayerKey Key, int Count)[] spec)
    {
        var shapes = new List<LayoutShape>();
        foreach (var (key, count) in spec)
            for (int i = 0; i < count; i++)
                shapes.Add(new RectShape { Layer = key, X1 = 0, Y1 = 0, X2 = 100, Y2 = 100 });
        return shapes;
    }

    // ── The Drill->Substrate trap (§0 of the brief) — both starter technologies use the same
    // (Layer, Datatype) key range with completely different meanings ─────────────────────────────

    [Fact]
    public void Propose_PcbToMmic_DrillAndSoldermask_AreSameKeyDifferentName_NeverPreSelected()
    {
        var pcb = StarterTechnologies.Pcb2Layer();
        var mmic = StarterTechnologies.MmicGaAs();
        var shapes = ShapesOn((Drill, 3), (Soldermask, 2));

        var rows = LayoutLayerMapping.Propose(shapes, pcb.Layers, mmic);

        Assert.Equal(2, rows.Count);
        // Both keys DO exist in MMIC GaAs (Via at 3/0, Substrate at 7/0) — this is exactly the trap:
        // present-but-semantically-wrong, not absent. The match kind must flag it as low confidence...
        Assert.All(rows, r => Assert.Equal(LayerMatchKind.SameKeyDifferentName, r.Match));
        // ...and — the actual bug this brief fixes — never pre-selected as the destination layer.
        Assert.All(rows, r => Assert.Equal(LayoutFragment.LayerReconciliationAction.KeepUnknown, r.Choice.Action));
        Assert.True(LayoutLayerMapping.RequiresConfirmation(rows));
    }

    [Fact]
    public void Propose_NoDestinationTechnology_ReturnsNoRows_NothingToReconcile()
    {
        var shapes = ShapesOn((Drill, 1));
        var rows = LayoutLayerMapping.Propose(shapes, [], null);

        Assert.Empty(rows);
        Assert.False(LayoutLayerMapping.RequiresConfirmation(rows)); // no dialog when there's nothing to ask
    }

    // ── Match kinds, in priority order (gate 4) ───────────────────────────────────────────────────

    [Fact]
    public void Propose_SameKeySameName_IsHighConfidence_PreSelectedMapToExisting()
    {
        var destTech = new Technology { Layers = [new LayerDef { Key = Drill, Name = "Drill" }] };
        var sourceLayers = new List<LayerDef> { new() { Key = Drill, Name = "Drill" } };
        var shapes = ShapesOn((Drill, 1));

        var row = Assert.Single(LayoutLayerMapping.Propose(shapes, sourceLayers, destTech));

        Assert.Equal(LayerMatchKind.SameKeySameName, row.Match);
        Assert.Equal(Drill, row.Proposed);
        Assert.Equal(LayoutFragment.LayerReconciliationAction.MapToExisting, row.Choice.Action);
        Assert.Equal(Drill, row.Choice.MapTarget);
    }

    [Fact]
    public void Propose_SameKeySameName_IsCaseAndWhitespaceInsensitive()
    {
        var destTech = new Technology { Layers = [new LayerDef { Key = Drill, Name = "  drill  " }] };
        var sourceLayers = new List<LayerDef> { new() { Key = Drill, Name = "DRILL" } };
        var shapes = ShapesOn((Drill, 1));

        var row = Assert.Single(LayoutLayerMapping.Propose(shapes, sourceLayers, destTech));

        Assert.Equal(LayerMatchKind.SameKeySameName, row.Match);
    }

    [Fact]
    public void Propose_ExactName_DifferentKey_IsHighConfidence_PreSelected()
    {
        var renamedKey = new LayerKey(15, 0);
        var destTech = new Technology { Layers = [new LayerDef { Key = renamedKey, Name = "Drill" }] };
        var sourceLayers = new List<LayerDef> { new() { Key = Drill, Name = "Drill" } };
        var shapes = ShapesOn((Drill, 1));

        var row = Assert.Single(LayoutLayerMapping.Propose(shapes, sourceLayers, destTech));

        Assert.Equal(LayerMatchKind.ExactName, row.Match);
        Assert.Equal(renamedKey, row.Proposed);
        Assert.Equal(LayoutFragment.LayerReconciliationAction.MapToExisting, row.Choice.Action);
    }

    [Fact]
    public void Propose_ExactName_IsCaseAndWhitespaceInsensitive()
    {
        var renamedKey = new LayerKey(15, 0);
        var destTech = new Technology { Layers = [new LayerDef { Key = renamedKey, Name = " Drill  " }] };
        var sourceLayers = new List<LayerDef> { new() { Key = Drill, Name = "DRILL" } };
        var shapes = ShapesOn((Drill, 1));

        var row = Assert.Single(LayoutLayerMapping.Propose(shapes, sourceLayers, destTech));

        Assert.Equal(LayerMatchKind.ExactName, row.Match);
        Assert.Equal(renamedKey, row.Proposed);
    }

    [Fact]
    public void Propose_ExactNameMatch_BeatsACompetingSameKeyNumericMatch()
    {
        // Dest defines something ELSE at the source's own key (7,0) — a numeric coincidence — AND
        // defines "Drill" itself at a different key. The name match must win (§1's priority order).
        var elsewhereKey = new LayerKey(15, 0);
        var destTech = new Technology
        {
            Layers =
            [
                new LayerDef { Key = Drill, Name = "Substrate" },       // same key, different name
                new LayerDef { Key = elsewhereKey, Name = "Drill" },    // different key, exact name
            ],
        };
        var sourceLayers = new List<LayerDef> { new() { Key = Drill, Name = "Drill" } };
        var shapes = ShapesOn((Drill, 1));

        var row = Assert.Single(LayoutLayerMapping.Propose(shapes, sourceLayers, destTech));

        Assert.Equal(LayerMatchKind.ExactName, row.Match);
        Assert.Equal(elsewhereKey, row.Proposed);
    }

    [Fact]
    public void Propose_SameKeyDifferentName_IsLowConfidence_NeverPreSelected()
    {
        var destTech = new Technology { Layers = [new LayerDef { Key = Drill, Name = "Substrate" }] };
        var sourceLayers = new List<LayerDef> { new() { Key = Drill, Name = "Drill" } };
        var shapes = ShapesOn((Drill, 1));

        var row = Assert.Single(LayoutLayerMapping.Propose(shapes, sourceLayers, destTech));

        Assert.Equal(LayerMatchKind.SameKeyDifferentName, row.Match);
        Assert.Equal(Drill, row.Proposed); // proposed, so the dialog can show it...
        Assert.Equal(LayoutFragment.LayerReconciliationAction.KeepUnknown, row.Choice.Action); // ...but never pre-selected
    }

    [Fact]
    public void Propose_NoMatch_DefaultsToKeepAsUnknown()
    {
        var destTech = new Technology { Layers = [new LayerDef { Key = new LayerKey(99, 0), Name = "Something Else" }] };
        var shapes = ShapesOn((Drill, 1));

        var row = Assert.Single(LayoutLayerMapping.Propose(shapes, [], destTech));

        Assert.Equal(LayerMatchKind.NoMatch, row.Match);
        Assert.Null(row.Proposed);
        Assert.Equal(LayoutFragment.LayerReconciliationAction.KeepUnknown, row.Choice.Action);
    }

    // ── Shape counts + sort order (gate 5) ────────────────────────────────────────────────────────

    [Fact]
    public void Propose_ShapeCountsAreCorrect_AndRowsSortByCountDescending()
    {
        var layerA = new LayerKey(1, 0);
        var layerB = new LayerKey(2, 0);
        var layerC = new LayerKey(3, 0);
        var destTech = new Technology { Layers = [new LayerDef { Key = new LayerKey(50, 0), Name = "X" }] };
        var shapes = ShapesOn((layerA, 2), (layerB, 5), (layerC, 1));

        var rows = LayoutLayerMapping.Propose(shapes, [], destTech);

        Assert.Equal([layerB, layerA, layerC], rows.Select(r => r.Source));
        Assert.Equal([5, 2, 1], rows.Select(r => r.ShapeCount));
    }

    // ── RequiresConfirmation (R-L1g-2) ────────────────────────────────────────────────────────────

    [Fact]
    public void RequiresConfirmation_AllHighConfidenceRows_IsFalse()
    {
        var destTech = new Technology { Layers = [new LayerDef { Key = Drill, Name = "Drill" }] };
        var sourceLayers = new List<LayerDef> { new() { Key = Drill, Name = "Drill" } };
        var rows = LayoutLayerMapping.Propose(ShapesOn((Drill, 1)), sourceLayers, destTech);

        Assert.False(LayoutLayerMapping.RequiresConfirmation(rows));
    }

    [Theory]
    [InlineData(LayerMatchKind.SameKeyDifferentName)]
    [InlineData(LayerMatchKind.NoMatch)]
    public void RequiresConfirmation_AnyLowConfidenceRow_IsTrue(LayerMatchKind lowConfidenceKind)
    {
        var destTech = lowConfidenceKind == LayerMatchKind.SameKeyDifferentName
            ? new Technology { Layers = [new LayerDef { Key = Drill, Name = "Substrate" }] }
            : new Technology { Layers = [new LayerDef { Key = new LayerKey(99, 0), Name = "Unrelated" }] };
        var rows = LayoutLayerMapping.Propose(ShapesOn((Drill, 1)), [], destTech);

        Assert.True(LayoutLayerMapping.RequiresConfirmation(rows));
    }

    // ── BuildChoices projects settled rows into ApplyReconciliation's shape ───────────────────────

    [Fact]
    public void BuildChoices_ProjectsOneEntryPerRow_KeyedBySource()
    {
        var destTech = new Technology { Layers = [new LayerDef { Key = Drill, Name = "Drill" }] };
        var sourceLayers = new List<LayerDef> { new() { Key = Drill, Name = "Drill" } };
        var rows = LayoutLayerMapping.Propose(ShapesOn((Drill, 1)), sourceLayers, destTech);

        var choices = LayoutLayerMapping.BuildChoices(rows);

        var choice = Assert.Single(choices);
        Assert.Equal(Drill, choice.Key);
        Assert.Equal(LayoutFragment.LayerReconciliationAction.MapToExisting, choice.Value.Action);
    }
}

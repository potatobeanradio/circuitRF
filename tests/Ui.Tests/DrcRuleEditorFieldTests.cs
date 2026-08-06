using System.Linq;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Theming;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// The Technology Editor's v2 DRC fields. Until these existed the editor could SHOW an imported rule
/// and not edit it — a field a user can see and cannot change reads as a bug, and every rule the
/// deck importer produces uses them.
/// </summary>
public class DrcRuleEditorFieldTests
{
    private static (TechEditorViewModel Vm, DrcRuleRowViewModel Row) Editor(DrcRule rule)
    {
        var tech = new Technology { Name = "T" };
        tech.Layers.Add(new LayerDef { Key = new LayerKey(1, 0), Name = "M1", Color = new Rgba(1, 2, 3, 255) });
        tech.Layers.Add(new LayerDef { Key = new LayerKey(2, 0), Name = "M2", Color = new Rgba(1, 2, 3, 255) });
        tech.DrcRules.Add(rule);

        var vm = new TechEditorViewModel("/tmp/t.ctech", tech);
        return (vm, vm.DrcRules.Single());
    }

    private static DrcRule Rule(DrcRuleKind kind = DrcRuleKind.MinWidth) =>
        new() { Name = "R", Kind = kind, Layer = new LayerKey(1, 0), ValueDbu = 100 };

    // ── The expression operands ─────────────────────────────────────────────────────────────────

    [Fact]
    public void CommittingARegion_StoresTheCanonicalRendering_NotTheTypedSpacing()
    {
        var (_, row) = Editor(Rule());

        row.StagedRegionA = "  and( 1/0 ,2/0 )  ";
        row.CommitRegionA();

        // One stable spelling in the file, so a re-save does not churn the `.ctech` on whitespace.
        Assert.Equal("and(1/0, 2/0)", row.Rule.RegionA);
        Assert.Equal("and(1/0, 2/0)", row.StagedRegionA);
        Assert.Null(row.RegionAError);
    }

    /// <summary>Blank means "this rule's own layer" — the same convention the engine reads, so the
    /// editor cannot express something the checker would interpret differently.</summary>
    [Fact]
    public void ClearingARegion_MeansTheRulesOwnLayer()
    {
        var (_, row) = Editor(new DrcRule
        {
            Name = "R", Kind = DrcRuleKind.MinWidth, Layer = new LayerKey(1, 0),
            ValueDbu = 100, RegionA = "and(1/0, 2/0)",
        });

        row.StagedRegionA = "   ";
        row.CommitRegionA();

        Assert.Null(row.Rule.RegionA);
    }

    [Fact]
    public void AnInvalidRegion_ShowsAnError_AndDoesNotTouchTheModel()
    {
        var (_, row) = Editor(Rule());

        row.StagedRegionA = "and(1/0";
        row.CommitRegionA();

        Assert.True(row.HasRegionAError);
        Assert.Null(row.Rule.RegionA);
        Assert.Equal("and(1/0", row.StagedRegionA);   // the user's text is not discarded
    }

    [Fact]
    public void EditingARegion_IsUndoable()
    {
        var (vm, row) = Editor(Rule());

        row.StagedRegionA = "not(1/0, 2/0)";
        row.CommitRegionA();
        Assert.Equal("not(1/0, 2/0)", vm.Working.DrcRules[0].RegionA);

        vm.UndoRedo.Undo();
        Assert.Null(vm.Working.DrcRules[0].RegionA);
    }

    // ── Kind-specific fields appear only for the kinds that carry them ──────────────────────────

    [Theory]
    [InlineData(DrcRuleKind.MinWidth,      false, true,  false, false, false)]
    [InlineData(DrcRuleKind.MinSpacing,    false, true,  false, false, true)]
    [InlineData(DrcRuleKind.MinSeparation, true,  true,  false, false, true)]
    [InlineData(DrcRuleKind.MinEnclosure,  true,  true,  false, false, false)]
    [InlineData(DrcRuleKind.MinArea,       false, true,  false, false, false)]
    [InlineData(DrcRuleKind.Density,       false, false, true,  true,  false)]
    [InlineData(DrcRuleKind.AntennaRatio,  true,  false, false, true,  false)]
    public void EachKindShowsExactlyTheFieldsItUses(
        DrcRuleKind kind, bool regionB, bool value, bool density, bool maxRatio, bool netScope)
    {
        var (_, row) = Editor(Rule());
        row.StagedKind = kind;

        Assert.Equal(regionB,  row.ShowRegionB);
        Assert.Equal(value,    row.ShowValue);
        Assert.Equal(density,  row.ShowDensity);
        Assert.Equal(maxRatio, row.ShowMaxRatio);
        Assert.Equal(netScope, row.ShowNetScope);
    }

    /// <summary>The one place `ValueDbu` is not a distance, surfaced in the label so a user is not
    /// left to infer it.</summary>
    [Fact]
    public void MinArea_LabelsItsValueAsAnArea()
    {
        var (_, row) = Editor(Rule());

        row.StagedKind = DrcRuleKind.MinWidth;
        Assert.Equal("Value:", row.ValueLabel);

        row.StagedKind = DrcRuleKind.MinArea;
        Assert.Equal("Area:", row.ValueLabel);
    }

    // ── Density and antenna values ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A density is a fraction, but people type percentages. Accepting both is friendlier than
    /// rejecting "40" — and the conversion is scoped to density, because an antenna limit is a plain
    /// ratio that is legitimately far above 1.
    /// </summary>
    [Fact]
    public void ADensityRatioTypedAsAPercentage_IsNormalised_ButAnAntennaRatioIsNot()
    {
        var (_, density) = Editor(Rule(DrcRuleKind.Density));
        density.StagedMaxRatioText = "40";
        density.CommitMaxRatio();
        Assert.Equal(0.4, density.Rule.MaxRatio!.Value, 6);

        var (_, antenna) = Editor(Rule(DrcRuleKind.AntennaRatio));
        antenna.StagedMaxRatioText = "40";
        antenna.CommitMaxRatio();
        Assert.Equal(40.0, antenna.Rule.MaxRatio!.Value, 6);
    }

    [Fact]
    public void TheDensityWindow_ParsesAsALength_AndClearsWhenBlank()
    {
        var (_, row) = Editor(Rule(DrcRuleKind.Density));

        row.StagedWindowText = "2u";
        row.CommitWindow();
        Assert.Equal(2000, row.Rule.WindowDbu);

        row.StagedWindowText = "";
        row.CommitWindow();
        Assert.Null(row.Rule.WindowDbu);
    }

    [Fact]
    public void NetScope_CommitsAndIsUndoable()
    {
        var (vm, row) = Editor(Rule(DrcRuleKind.MinSpacing));

        row.StagedNetScope = DrcNetScope.DifferentNet;
        row.CommitNetScope();
        Assert.Equal(DrcNetScope.DifferentNet, vm.Working.DrcRules[0].NetScope);

        vm.UndoRedo.Undo();
        Assert.Equal(DrcNetScope.Any, vm.Working.DrcRules[0].NetScope);
    }

    /// <summary>Every kind must be reachable from the combo, or a rule the importer produces cannot
    /// be created or corrected by hand.</summary>
    [Fact]
    public void TheKindCombo_OffersEveryKind()
    {
        Assert.Equal(System.Enum.GetValues<DrcRuleKind>().Length, DrcRuleRowViewModel.AllKinds.Count);
        Assert.Contains(DrcRuleKind.Density, DrcRuleRowViewModel.AllKinds);
        Assert.Contains(DrcRuleKind.AntennaRatio, DrcRuleRowViewModel.AllKinds);
    }

    // ── Merging through the editor ──────────────────────────────────────────────────────────────

    /// <summary>
    /// One snapshot, not one per item: a user who imports a layer table and does not like the result
    /// wants Ctrl+Z to undo "the import", not to walk back out of it three hundred times.
    /// </summary>
    [Fact]
    public void MergeFrom_IsOneUndoableEdit_AndRebuildsTheRows()
    {
        var (vm, _) = Editor(Rule());

        var source = new Technology { Name = "Other" };
        source.DrcRules.Add(new DrcRule
        {
            Name = "New1", Kind = DrcRuleKind.MinSpacing, Layer = new LayerKey(1, 0), ValueDbu = 50,
        });
        source.DrcRules.Add(new DrcRule
        {
            Name = "New2", Kind = DrcRuleKind.MinSpacing, Layer = new LayerKey(1, 0), ValueDbu = 60,
        });

        var report = vm.MergeFrom(source, TechSection.DrcRules, TechMergeMode.AddMissingOnly);

        Assert.Equal(2, report.RulesAdded);
        Assert.Equal(3, vm.Working.DrcRules.Count);
        Assert.Equal(3, vm.DrcRules.Count);          // rows rebuilt, not stale

        vm.UndoRedo.Undo();
        Assert.Single(vm.Working.DrcRules);
    }

    [Fact]
    public void MergeFrom_ThatChangesNothing_PushesNoUndoEntry()
    {
        var (vm, _) = Editor(Rule());
        bool couldUndoBefore = vm.UndoRedo.CanUndo;

        vm.MergeFrom(new Technology { Name = "Empty" }, TechSection.All, TechMergeMode.Replace);

        Assert.Equal(couldUndoBefore, vm.UndoRedo.CanUndo);
    }
}

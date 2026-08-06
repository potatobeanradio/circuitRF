using System.Collections.Generic;
using System.Linq;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Theming;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Combining technologies section by section (docs/design/layout-view.md §2.4a) — the mechanism
/// behind re-import, mix-and-match, "import just the DRC rules" and "send someone my rules".
/// </summary>
public class TechnologyMergeTests
{
    private static LayerDef Layer(int l, int d, string name, byte r = 10) =>
        new() { Key = new LayerKey(l, d), Name = name, Color = new Rgba(r, 20, 30, 255) };

    private static DrcRule Rule(string name, DrcRuleKind kind = DrcRuleKind.MinWidth, long value = 100) =>
        new() { Name = name, Kind = kind, Layer = new LayerKey(1, 0), ValueDbu = value };

    private static Technology Tech(string name = "T") => new() { Name = name };

    // ── Section selection ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void OnlyTheChosenSectionsAreTouched()
    {
        var target = Tech();
        target.Layers.Add(Layer(1, 0, "Mine"));

        var source = Tech();
        source.Layers.Add(Layer(9, 0, "Theirs"));
        source.DrcRules.Add(Rule("R1"));
        source.Stackup.Layers.Add(new StackupLayer { Kind = StackupKind.Conductor, Name = "M1", ThicknessDbu = 5 });

        var report = TechnologyMerge.Merge(target, source, TechSection.DrcRules, TechMergeMode.AddMissingOnly);

        Assert.Single(target.DrcRules);
        Assert.Single(target.Layers);              // untouched
        Assert.Empty(target.Stackup.Layers);       // untouched
        Assert.Equal(1, report.RulesAdded);
        Assert.Equal(0, report.LayersAdded);
    }

    // ── Collision policy ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The default, and the reason it is the default: a user who has tuned a technology and then
    /// imports a process update almost never wants their own edits silently reverted. A missed
    /// update is visible and fixable; a silently reverted edit is neither.
    /// </summary>
    [Fact]
    public void AddMissingOnly_KeepsTheExistingItem()
    {
        var target = Tech();
        target.DrcRules.Add(Rule("width rule", value: 111));

        var source = Tech();
        source.DrcRules.Add(Rule("width rule", value: 999));
        source.DrcRules.Add(Rule("spacing rule", value: 222));

        var report = TechnologyMerge.Merge(target, source, TechSection.DrcRules, TechMergeMode.AddMissingOnly);

        Assert.Equal(111, target.DrcRules.Single(r => r.Name == "width rule").ValueDbu);
        Assert.Equal(222, target.DrcRules.Single(r => r.Name == "spacing rule").ValueDbu);
        Assert.Equal(1, report.RulesAdded);
        Assert.Equal(1, report.RulesKept);
        Assert.Equal(0, report.RulesReplaced);
    }

    [Fact]
    public void Replace_TakesTheIncomingItem_AtItsOriginalPosition()
    {
        var target = Tech();
        target.DrcRules.Add(Rule("A", value: 1));
        target.DrcRules.Add(Rule("B", value: 2));
        target.DrcRules.Add(Rule("C", value: 3));

        var source = Tech();
        source.DrcRules.Add(Rule("B", value: 999));

        var report = TechnologyMerge.Merge(target, source, TechSection.DrcRules, TechMergeMode.Replace);

        // Position matters — rules are listed in the technology's own order and a replace that
        // appended would reorder the list under the user.
        Assert.Equal(["A", "B", "C"], target.DrcRules.Select(r => r.Name));
        Assert.Equal(999, target.DrcRules[1].ValueDbu);
        Assert.Equal(1, report.RulesReplaced);
    }

    // ── Identity is per section, and deliberately not uniform ───────────────────────────────────

    /// <summary>A layer IS its key (§2.1) — the name is a label, so a renamed layer at the same key
    /// is the same layer.</summary>
    [Fact]
    public void ALayerIsIdentifiedByItsKey_AndARenameIsReported()
    {
        var target = Tech();
        target.Layers.Add(Layer(8, 0, "Metal1"));

        var source = Tech();
        source.Layers.Add(Layer(8, 0, "M1"));

        var report = TechnologyMerge.Merge(target, source, TechSection.Layers, TechMergeMode.Replace);

        Assert.Single(target.Layers);
        Assert.Equal("M1", target.Layers[0].Name);
        Assert.Equal(1, report.LayersReplaced);

        // Every name-first match in this codebase keys on LayerDef.Name, so a silent rename changes
        // how future pastes and retargets land.
        Assert.Contains(report.Warnings, w => w.Contains("renamed") && w.Contains("Metal1"));
    }

    /// <summary>A stackup entry is its NAME — which is also what SpanFromLayer/SpanToLayer
    /// reference, so matching on anything else would break those references.</summary>
    [Fact]
    public void AStackupEntryIsIdentifiedByName_AndAppendsAreFlaggedAsOrderSensitive()
    {
        var target = Tech();
        target.Stackup.Layers.Add(new StackupLayer { Kind = StackupKind.Conductor, Name = "Top", ThicknessDbu = 1 });

        var source = Tech();
        source.Stackup.Layers.Add(new StackupLayer { Kind = StackupKind.Conductor, Name = "Bottom", ThicknessDbu = 2 });

        var report = TechnologyMerge.Merge(target, source, TechSection.Stackup, TechMergeMode.AddMissingOnly);

        Assert.Equal(2, target.Stackup.Layers.Count);
        Assert.Equal(1, report.StackupAdded);
        Assert.Contains(report.Warnings, w => w.Contains("BOTTOM of the stack"));
    }

    // ── The misuse this feature invites ─────────────────────────────────────────────────────────

    /// <summary>
    /// "Just send me the rules" is the most likely way this is used and the most likely way it goes
    /// wrong: a rule references layers by number, and without them it looks healthy in the editor
    /// and measures nothing. Said at merge time, where the user can still act on it.
    /// </summary>
    [Fact]
    public void RulesWithoutTheirLayers_AreWarnedAbout()
    {
        var target = Tech();
        target.Layers.Add(Layer(1, 0, "Only layer here"));

        var source = Tech();
        source.Layers.Add(Layer(77, 0, "Not coming across"));
        source.DrcRules.Add(new DrcRule
        {
            Name = "R", Kind = DrcRuleKind.MinWidth,
            Layer = new LayerKey(77, 0), ValueDbu = 100,
        });

        var report = TechnologyMerge.Merge(target, source, TechSection.DrcRules, TechMergeMode.Replace);

        Assert.Contains(report.Warnings, w => w.Contains("do not define") || w.Contains("does not define"));
    }

    [Fact]
    public void BringingTheLayersToo_RaisesNoSuchWarning()
    {
        var target = Tech();
        var source = Tech();
        source.Layers.Add(Layer(77, 0, "L"));
        source.DrcRules.Add(new DrcRule
        {
            Name = "R", Kind = DrcRuleKind.MinWidth, Layer = new LayerKey(77, 0), ValueDbu = 100,
        });

        var report = TechnologyMerge.Merge(
            target, source, TechSection.Layers | TechSection.DrcRules, TechMergeMode.Replace);

        Assert.DoesNotContain(report.Warnings, w => w.Contains("define"));
    }

    // ── Nothing is aliased across the merge ─────────────────────────────────────────────────────

    /// <summary>
    /// The two technologies outlive the merge independently, so a later edit to one must never
    /// silently change the other.
    /// </summary>
    [Fact]
    public void MergedItemsAreClones_NotSharedReferences()
    {
        var target = Tech();
        var source = Tech();
        source.DrcRules.Add(Rule("R", value: 100));

        TechnologyMerge.Merge(target, source, TechSection.DrcRules, TechMergeMode.Replace);
        source.DrcRules[0].ValueDbu = 999;

        Assert.Equal(100, target.DrcRules[0].ValueDbu);
    }

    /// <summary>Every v2 field has to survive the clone, or a merged rule quietly loses its
    /// expression and starts measuring its bare layer instead.</summary>
    [Fact]
    public void EveryRuleFieldSurvivesTheMerge()
    {
        var source = Tech();
        source.DrcRules.Add(new DrcRule
        {
            Name = "full", Kind = DrcRuleKind.Density, Layer = new LayerKey(3, 1),
            RegionA = "and(1/0, 2/0)", RegionB = "4/0",
            ValueDbu = 42, WindowDbu = 1000, MinRatio = 0.2, MaxRatio = 0.8,
            NetScope = DrcNetScope.DifferentNet, Severity = DrcSeverity.Warning,
        });

        var target = Tech();
        TechnologyMerge.Merge(target, source, TechSection.DrcRules, TechMergeMode.Replace);

        var r = Assert.Single(target.DrcRules);
        Assert.Equal("and(1/0, 2/0)", r.RegionA);
        Assert.Equal("4/0", r.RegionB);
        Assert.Equal(1000, r.WindowDbu);
        Assert.Equal(0.2, r.MinRatio);
        Assert.Equal(0.8, r.MaxRatio);
        Assert.Equal(DrcNetScope.DifferentNet, r.NetScope);
        Assert.Equal(DrcSeverity.Warning, r.Severity);
        Assert.Equal(new LayerKey(3, 1), r.Layer);
    }

    // ── Export ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// "Export my DRC rules" writes an ordinary `.ctech` with the other sections empty — no second
    /// file format, no second reader, no second version field.
    /// </summary>
    [Fact]
    public void Extract_ProducesAValidTechnologyCarryingOnlyTheChosenSections()
    {
        var source = Tech("Full");
        source.Layers.Add(Layer(1, 0, "L"));
        source.Stackup.Layers.Add(new StackupLayer { Kind = StackupKind.Conductor, Name = "M", ThicknessDbu = 5 });
        source.DrcRules.Add(Rule("R"));

        var rulesOnly = TechnologyMerge.Extract(source, TechSection.DrcRules, "Rules");

        Assert.Empty(rulesOnly.Layers);
        Assert.Empty(rulesOnly.Stackup.Layers);
        Assert.Single(rulesOnly.DrcRules);
        Assert.Equal("Rules", rulesOnly.Name);

        // And it survives the real `.ctech` round trip, because it is just a technology.
        var reloaded = TechPersistence.Deserialize(TechPersistence.Serialize(rulesOnly));
        Assert.Single(reloaded.DrcRules);
        Assert.Empty(reloaded.Layers);
    }

    [Fact]
    public void SectionsPresentIn_ReportsWhatAFileActuallyOffers()
    {
        var t = Tech();
        Assert.Equal(TechSection.None, TechnologyMerge.SectionsPresentIn(t));

        t.DrcRules.Add(Rule("R"));
        Assert.Equal(TechSection.DrcRules, TechnologyMerge.SectionsPresentIn(t));

        t.Layers.Add(Layer(1, 0, "L"));
        Assert.Equal(TechSection.Layers | TechSection.DrcRules, TechnologyMerge.SectionsPresentIn(t));
    }

    // ── Per-item choice ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A blanket policy answers "what usually happens"; this answers "what about THIS rule". A
    /// process update typically changes a handful of values out of a hundred, and the user often
    /// wants most of them and not the two they deliberately tuned.
    /// </summary>
    [Fact]
    public void Selective_ReplacesExactlyTheTickedItems()
    {
        var target = Tech();
        target.DrcRules.Add(Rule("keep", value: 1));
        target.DrcRules.Add(Rule("take", value: 2));

        var source = Tech();
        source.DrcRules.Add(Rule("keep", value: 999));
        source.DrcRules.Add(Rule("take", value: 888));

        var conflicts = TechnologyMerge.FindConflicts(target, source, TechSection.DrcRules);
        Assert.Equal(2, conflicts.Count);

        var ticked = conflicts.Where(c => c.Label.Contains("take")).Select(c => c.Key).ToHashSet();
        var report = TechnologyMerge.Merge(target, source, TechSection.DrcRules, TechMergeMode.Selective, ticked);

        Assert.Equal(1, target.DrcRules.Single(r => r.Name == "keep").ValueDbu);   // held back
        Assert.Equal(888, target.DrcRules.Single(r => r.Name == "take").ValueDbu); // taken
        Assert.Equal(1, report.RulesReplaced);
        Assert.Equal(1, report.RulesKept);
    }

    /// <summary>
    /// The safe reading of "the user was asked and ticked none" — never "the user was asked and
    /// everything wins".
    /// </summary>
    [Fact]
    public void Selective_WithNothingTicked_ReplacesNothing()
    {
        var target = Tech();
        target.DrcRules.Add(Rule("R", value: 1));

        var source = Tech();
        source.DrcRules.Add(Rule("R", value: 999));

        TechnologyMerge.Merge(target, source, TechSection.DrcRules, TechMergeMode.Selective, null);
        Assert.Equal(1, target.DrcRules[0].ValueDbu);
    }

    /// <summary>New items are never a conflict — they are added under every mode, because there is
    /// nothing of the user's to overwrite.</summary>
    [Fact]
    public void Selective_StillAddsItemsThatDoNotCollide()
    {
        var target = Tech();
        target.DrcRules.Add(Rule("existing"));

        var source = Tech();
        source.DrcRules.Add(Rule("existing", value: 999));
        source.DrcRules.Add(Rule("brand new", value: 5));

        var report = TechnologyMerge.Merge(
            target, source, TechSection.DrcRules, TechMergeMode.Selective, new HashSet<string>());

        Assert.Equal(1, report.RulesAdded);
        Assert.Contains(target.DrcRules, r => r.Name == "brand new");
    }

    [Fact]
    public void FindConflicts_ShowsBothSides_SoTheChoiceIsInformed()
    {
        var target = Tech();
        target.DrcRules.Add(Rule("R", value: 111));

        var source = Tech();
        source.DrcRules.Add(Rule("R", value: 222));

        var c = Assert.Single(TechnologyMerge.FindConflicts(target, source, TechSection.DrcRules));
        Assert.Contains("111", c.Mine);
        Assert.Contains("222", c.Theirs);
        Assert.Contains("R", c.Label);
    }

    /// <summary>Conflict keys are section-qualified, because a layer and a rule may share a name and
    /// ticking one must never silently replace the other.</summary>
    [Fact]
    public void ConflictKeys_AreUniqueAcrossSections()
    {
        var target = Tech();
        target.Layers.Add(Layer(1, 0, "Same"));
        target.DrcRules.Add(Rule("Same"));

        var source = Tech();
        source.Layers.Add(Layer(1, 0, "Same"));
        source.DrcRules.Add(Rule("Same", value: 999));

        var conflicts = TechnologyMerge.FindConflicts(
            target, source, TechSection.Layers | TechSection.DrcRules);

        Assert.Equal(2, conflicts.Count);
        Assert.Equal(2, conflicts.Select(c => c.Key).Distinct().Count());
    }

    // ── Reporting ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AMergeThatChangedNothing_SaysSo_RatherThanReportingSuccess()
    {
        var target = Tech();
        target.DrcRules.Add(Rule("R"));

        var source = Tech();
        source.DrcRules.Add(Rule("R"));

        var report = TechnologyMerge.Merge(target, source, TechSection.DrcRules, TechMergeMode.AddMissingOnly);

        Assert.True(report.ChangedNothing);
        Assert.Contains("Nothing changed", report.Summary());
    }

    [Fact]
    public void MergingNothing_IsANoOp()
    {
        var target = Tech();
        target.DrcRules.Add(Rule("R"));

        var report = TechnologyMerge.Merge(target, Tech(), TechSection.None, TechMergeMode.Replace);

        Assert.Same(TechMergeReport.Empty, report);
        Assert.Single(target.DrcRules);
    }
}

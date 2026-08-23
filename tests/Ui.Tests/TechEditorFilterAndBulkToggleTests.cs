using System;
using System.IO;
using System.Linq;
using Avalonia.Input;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Theming;
using Xunit;

namespace CircuitRF.Ui.Tests;

// ──────────────────────────────────────────────────────────────────────────────
//  .ctech editor — per-tab row filter, bulk Visible/Selectable, keyboard scrolling.
//
//  Owner request (2026-08-22): a filter row above the rows on every tab, bulk "all visible" /
//  "all selectable" toggles aligned with the two columns they act on, and Page Up/Down/Home/End
//  scrolling for each tab's list.
//
//  The behaviour worth pinning is not "a filter filters" but the three ways it interacts with
//  everything already here: the filters are PER TAB (Layers and Interchange list the same layers),
//  they survive the wholesale row rebuild every committed edit and every undo performs, and a bulk
//  toggle is scoped to what the filter is SHOWING and lands as ONE undo entry.
//
//  The .axaml half is a source scan — an AXAML layout change has no headlessly assertable rendered
//  output (the convention this file follows from TechEditorNarrowWidthTests).
// ──────────────────────────────────────────────────────────────────────────────

public class TechEditorFilterAndBulkToggleTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"techfilter-{Guid.NewGuid():N}.ctech");

    private static Technology FreshTech() => new()
    {
        Name = "Filter Test",
        DefaultDisplayUnit = LayoutUnit.Um,
        DefaultSnapDbu = 1000,
        Layers =
        [
            new LayerDef { Key = new LayerKey(1, 0),   Name = "Metal1",           Color = new Rgba(200, 100, 50), ZOrder = 1 },
            new LayerDef { Key = new LayerKey(2, 0),   Name = "Metal2",           Color = new Rgba(50, 100, 200), ZOrder = 2 },
            new LayerDef { Key = new LayerKey(126, 0), Name = "TopMetal1.drawing", Color = new Rgba(9, 9, 9),      ZOrder = 3 },
            new LayerDef { Key = new LayerKey(1, 25),  Name = "Activ.text",       Color = new Rgba(1, 2, 3),      ZOrder = 4 },
        ],
        Stackup = new Stackup
        {
            Layers =
            [
                new StackupLayer { Kind = StackupKind.Conductor,  Name = "Metal1 sheet", ThicknessDbu = 1000 },
                new StackupLayer { Kind = StackupKind.Dielectric, Name = "Oxide",        ThicknessDbu = 2000 },
            ],
        },
        DrcRules =
        [
            new DrcRule { Name = "Metal1 min width",   Kind = DrcRuleKind.MinWidth,   Layer = new LayerKey(1, 0) },
            new DrcRule { Name = "Poly min spacing",   Kind = DrcRuleKind.MinSpacing, Layer = new LayerKey(2, 0) },
        ],
    };

    private static TechEditorViewModel Vm() => new(TempPath(), FreshTech());

    // ── The filters ───────────────────────────────────────────────────────────

    [Fact]
    public void NoFilter_EveryTabListsEveryRow()
    {
        var vm = Vm();

        Assert.Equal(vm.Layers.Count,        vm.FilteredLayers.Count);
        Assert.Equal(vm.Layers.Count,        vm.FilteredInterchangeLayers.Count);
        Assert.Equal(vm.StackupLayers.Count, vm.FilteredStackupLayers.Count);
        Assert.Equal(vm.DrcRules.Count,      vm.FilteredDrcRules.Count);

        // Order is the technology's own order, not a re-sort.
        Assert.Equal(vm.Layers.ToArray(), vm.FilteredLayers.ToArray());
    }

    [Fact]
    public void LayerFilter_KeepsOnlyNameSubstringMatches_CaseInsensitive()
    {
        var vm = Vm();

        vm.LayerFilter = "metal";   // lower case against "Metal1"/"Metal2"/"TopMetal1.drawing"

        Assert.Equal(["Metal1", "Metal2", "TopMetal1.drawing"], vm.FilteredLayers.Select(r => r.StagedName));
        Assert.DoesNotContain(vm.FilteredLayers, r => r.StagedName == "Activ.text");
    }

    [Fact]
    public void ClearingTheFilter_RestoresEveryRow()
    {
        var vm = Vm();
        vm.LayerFilter = "activ";
        Assert.Single(vm.FilteredLayers);

        vm.LayerFilter = "";
        Assert.Equal(vm.Layers.Count, vm.FilteredLayers.Count);
    }

    [Fact]
    public void EachTabOwnsItsOwnFilter_NarrowingOneNeverNarrowsAnother()
    {
        var vm = Vm();

        vm.LayerFilter = "Activ";

        Assert.Single(vm.FilteredLayers);
        Assert.Equal(vm.Layers.Count, vm.FilteredInterchangeLayers.Count);   // same layers, different tab

        vm.InterchangeFilter = "Metal";
        Assert.Equal(3, vm.FilteredInterchangeLayers.Count);
        Assert.Single(vm.FilteredLayers);                                    // unchanged by the other tab
    }

    [Fact]
    public void StackupAndDrcTabs_FilterTheirOwnRowsByName()
    {
        var vm = Vm();

        vm.StackupFilter = "oxide";
        Assert.Equal(["Oxide"], vm.FilteredStackupLayers.Select(r => r.StagedName));

        vm.DrcFilter = "spacing";
        Assert.Equal(["Poly min spacing"], vm.FilteredDrcRules.Select(r => r.StagedName));
    }

    [Fact]
    public void FilterSummary_ReportsShownOfTotal_OnlyWhileNarrowing()
    {
        var vm = Vm();
        Assert.Equal("4", vm.LayerFilterSummary);

        vm.LayerFilter = "metal";
        Assert.Equal("3 of 4", vm.LayerFilterSummary);
    }

    // Every committed edit (and every undo/redo) replaces the row VMs wholesale — the filtered
    // views are rebuilt from the new rows, so a filter set before an edit still holds after it.
    [Fact]
    public void AFilterSurvivesACommittedEdit_AndAnUndo()
    {
        var vm = Vm();
        vm.LayerFilter = "metal";
        Assert.Equal(3, vm.FilteredLayers.Count);

        vm.Layers[3].StagedName = "Activ.renamed";   // a layer the filter is hiding
        vm.Layers[3].CommitName();
        Assert.Equal(3, vm.FilteredLayers.Count);
        Assert.All(vm.FilteredLayers, r => Assert.Contains("metal", r.StagedName, StringComparison.OrdinalIgnoreCase));

        vm.UndoRedo.Undo();
        Assert.Equal(3, vm.FilteredLayers.Count);
    }

    [Fact]
    public void RenamingALayerIntoTheFilter_MakesItAppear()
    {
        var vm = Vm();
        vm.LayerFilter = "metal";
        Assert.Equal(3, vm.FilteredLayers.Count);

        vm.Layers[3].StagedName = "MetalOxide";
        vm.Layers[3].CommitName();

        Assert.Equal(4, vm.FilteredLayers.Count);
    }

    // ── Bulk Visible / Selectable ─────────────────────────────────────────────

    [Fact]
    public void ToggleAllVisible_SweepsEveryListedLayer_AsOneUndoEntry()
    {
        var vm = Vm();
        Assert.True(vm.AllShownLayersVisible);   // LayerDef defaults to Visible

        vm.AllShownLayersVisible = false;

        Assert.All(vm.Layers, r => Assert.False(r.Visible));
        Assert.All(vm.Working.Layers, l => Assert.False(l.Visible));
        Assert.False(vm.AllShownLayersVisible);

        vm.UndoRedo.Undo();                      // ONE undo, not one per layer
        Assert.All(vm.Working.Layers, l => Assert.True(l.Visible));
        Assert.True(vm.AllShownLayersVisible);
        Assert.False(vm.UndoRedo.CanUndo);
    }

    [Fact]
    public void ToggleAllSelectable_IsIndependentOfVisibility()
    {
        var vm = Vm();

        vm.AllShownLayersSelectable = false;

        Assert.All(vm.Working.Layers, l => Assert.False(l.Selectable));
        Assert.All(vm.Working.Layers, l => Assert.True(l.Visible));
        Assert.False(vm.AllShownLayersSelectable);
        Assert.True(vm.AllShownLayersVisible);
    }

    // The scoping decision: a bulk toggle acts on what the filter is SHOWING. "Hide everything I am
    // looking at" is the useful operation; sweeping the 300 layers the filter just hid is not.
    [Fact]
    public void WithAFilterActive_TheSweepTouchesOnlyTheListedLayers()
    {
        var vm = Vm();
        vm.LayerFilter = "metal";

        vm.AllShownLayersVisible = false;

        Assert.All(vm.Working.Layers.Where(l => l.Name.Contains("Metal", StringComparison.OrdinalIgnoreCase)),
                   l => Assert.False(l.Visible));
        Assert.True(vm.Working.Layers.Single(l => l.Name == "Activ.text").Visible);
    }

    [Fact]
    public void ToggleState_IsFalseWhileAnyListedLayerDiffers()
    {
        var vm = Vm();

        vm.Layers[0].Visible = false;   // one row, via its own checkbox

        Assert.False(vm.AllShownLayersVisible);

        vm.AllShownLayersVisible = true;
        Assert.True(vm.AllShownLayersVisible);
        Assert.All(vm.Working.Layers, l => Assert.True(l.Visible));
    }

    [Fact]
    public void AFilterThatMatchesNothing_MakesTheSweepANoOp_AndPushesNoUndoEntry()
    {
        var vm = Vm();
        vm.LayerFilter = "no-such-layer";
        Assert.Empty(vm.FilteredLayers);

        vm.AllShownLayersVisible = false;

        Assert.False(vm.UndoRedo.CanUndo);
        Assert.All(vm.Working.Layers, l => Assert.True(l.Visible));
    }

    // ── Page Up / Page Down / Home / End ──────────────────────────────────────

    [Fact]
    public void PageKeys_AlwaysScroll_EvenFromInsideAnEditableCell()
    {
        Assert.Equal(TechScrollAction.PageUp,   TechEditorScrollKeys.ActionFor(Key.PageUp,   sourceIsTextInput: true));
        Assert.Equal(TechScrollAction.PageDown, TechEditorScrollKeys.ActionFor(Key.PageDown, sourceIsTextInput: true));
    }

    // Every row here is built out of TextBoxes, where Home/End are caret motion. Taking them would
    // break text editing across the whole editor to add a shortcut nobody asked for there.
    [Fact]
    public void HomeAndEnd_ScrollOnlyWhenTheKeystrokeIsNotComingFromATextField()
    {
        Assert.Equal(TechScrollAction.Home, TechEditorScrollKeys.ActionFor(Key.Home, sourceIsTextInput: false));
        Assert.Equal(TechScrollAction.End,  TechEditorScrollKeys.ActionFor(Key.End,  sourceIsTextInput: false));

        Assert.Null(TechEditorScrollKeys.ActionFor(Key.Home, sourceIsTextInput: true));
        Assert.Null(TechEditorScrollKeys.ActionFor(Key.End,  sourceIsTextInput: true));
    }

    [Fact]
    public void OtherKeys_AreLeftAlone()
    {
        foreach (var k in new[] { Key.Up, Key.Down, Key.Enter, Key.A, Key.Space })
        {
            Assert.Null(TechEditorScrollKeys.ActionFor(k, sourceIsTextInput: false));
            Assert.Null(TechEditorScrollKeys.ActionFor(k, sourceIsTextInput: true));
        }
    }

    // ── The view (source scans) ───────────────────────────────────────────────

    private static string Axaml() => File.ReadAllText(RepoFile(Path.Combine(
        "src", "Ui", "Views", "Layout", "TechEditorView.axaml")));

    private static string CodeBehind() => File.ReadAllText(RepoFile(Path.Combine(
        "src", "Ui", "Views", "Layout", "TechEditorView.axaml.cs")));

    private static string RepoFile(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "circuitrf.slnx")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return Path.Combine(dir!, relative);
    }

    // Binding a list to the UNFILTERED collection is the one regression that disables the whole
    // feature while still compiling and still looking right until someone types in the box.
    [Theory]
    [InlineData("ViewModel.FilteredLayers")]
    [InlineData("ViewModel.FilteredInterchangeLayers")]
    [InlineData("ViewModel.FilteredStackupLayers")]
    [InlineData("ViewModel.FilteredDrcRules")]
    public void EveryRowListBindsToItsFilteredCollection(string binding)
    {
        Assert.Contains($"ItemsSource=\"{{Binding {binding}}}\"", Axaml());
    }

    [Theory]
    [InlineData("ViewModel.LayerFilter")]
    [InlineData("ViewModel.InterchangeFilter")]
    [InlineData("ViewModel.StackupFilter")]
    [InlineData("ViewModel.DrcFilter")]
    public void EveryTabHasItsOwnFilterBox(string binding)
    {
        Assert.Contains($"Text=\"{{Binding {binding}, Mode=TwoWay}}\"", Axaml());
    }

    // The alignment requirement: the toggles sit in the Vis (6) and Sel (7) columns of the SAME
    // column list the header and the rows declare, so each one is directly above the checkboxes it
    // sweeps. A StackPanel of buttons at the left edge would satisfy "there are two toggles" and
    // fail the thing that was actually asked for.
    [Fact]
    public void TheBulkTogglesSitInTheVisAndSelColumns()
    {
        var axaml = Axaml();

        int vis = axaml.IndexOf("ViewModel.AllShownLayersVisible, Mode=TwoWay", StringComparison.Ordinal);
        int sel = axaml.IndexOf("ViewModel.AllShownLayersSelectable, Mode=TwoWay", StringComparison.Ordinal);
        Assert.True(vis > 0 && sel > vis, "both bulk toggles must be declared, visibility first");

        // The ToggleButton opening tag each binding belongs to carries the column.
        Assert.Contains("Grid.Column=\"1\"", axaml[axaml.LastIndexOf("<ToggleButton", vis, StringComparison.Ordinal)..vis]);
        Assert.Contains("Grid.Column=\"2\"", axaml[axaml.LastIndexOf("<ToggleButton", sel, StringComparison.Ordinal)..sel]);

        // …and the Vis/Sel column headers are columns 1 and 2 of that same list, which is what makes
        // 1 and 2 the right answer rather than two numbers that happen to be there.
        //
        // These indices are spelled out twice on purpose but they are not the ORDERING's home: the
        // Layers tab's full column order lives once, in TechEditorLayerColumnLayoutTests.Columns, and
        // is what a reorder should be edited into. This test is about the toggles being ABOVE their
        // columns rather than parked in a button bar, which is a separate claim worth its own failure.
        Assert.Contains("<TextBlock Grid.Column=\"1\"  Classes=\"colhdr\" Text=\"Vis\"", axaml);
        Assert.Contains("<TextBlock Grid.Column=\"2\"  Classes=\"colhdr\" Text=\"Sel\"", axaml);
    }

    [Fact]
    public void TheFilterRowRepeatsTheSameColumnWidths_OrNothingLinesUp()
    {
        var axaml = Axaml();

        // Three copies of the layer column list now: filter row, header row, item template.
        int copies = 0;
        for (int i = axaml.IndexOf("<ColumnDefinition Width=\"30\"/>", StringComparison.Ordinal); i >= 0;
                 i = axaml.IndexOf("<ColumnDefinition Width=\"30\"/>", i + 1, StringComparison.Ordinal))
            copies++;

        // Two 30 px columns (Vis, Sel) per copy of the list.
        Assert.Equal(6, copies);
    }

    // Bubbling would let the ListBox move its (invisible) selection first and swallow the key.
    [Fact]
    public void TheScrollKeyHandlerTunnels()
    {
        var cs = CodeBehind();
        Assert.Contains("AddHandler(KeyDownEvent, OnScrollKeyDown, RoutingStrategies.Tunnel)", cs);
        Assert.Contains("TechEditorScrollKeys.ActionFor", cs);
    }
}

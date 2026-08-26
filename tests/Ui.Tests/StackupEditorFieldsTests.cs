using System.Linq;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// The <c>.ctech</c> editor's stackup row, after three owner reports from importing a real process:
/// the drawing-layer selector could not be used (or fitted) at a real layer count, and a via's span
/// — which the import resolves correctly — was shown nowhere at all.
/// </summary>
public class StackupEditorFieldsTests
{
    private static string TempPath() => System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), $"stackuptest-{System.Guid.NewGuid():N}.ctech");

    /// <summary>A process-sized layer table — the case the old WrapPanel-of-checkboxes could not serve.</summary>
    private static Technology Tech(int layerCount = 4)
    {
        var t = new Technology
        {
            Name = "Test Tech",
            DefaultDisplayUnit = LayoutUnit.Um,
            DefaultSnapDbu = 1000,
        };

        for (int i = 1; i <= layerCount; i++)
            t.Layers.Add(new LayerDef
            {
                Key = new LayerKey(i, 0),
                Name = i == 1 ? "Metal1" : i == 2 ? "Metal2" : $"Aux{i}",
                Color = new CircuitRF.Design.Theming.Rgba(10, 20, 30),
                ZOrder = i,
            });

        t.Stackup.Layers.Add(new StackupLayer
        { Kind = StackupKind.Conductor, Name = "Metal2", ThicknessDbu = 500 });
        t.Stackup.Layers.Add(new StackupLayer
        { Kind = StackupKind.Dielectric, Name = "ox1", ThicknessDbu = 500, Epsr = 4.1 });
        t.Stackup.Layers.Add(new StackupLayer
        { Kind = StackupKind.Conductor, Name = "Metal1", ThicknessDbu = 420 });
        t.Stackup.Layers.Add(new StackupLayer
        { Kind = StackupKind.Via, Name = "Via1", SpanFromLayer = "Metal1", SpanToLayer = "Metal2" });

        return t;
    }

    private static TechEditorViewModel Vm(int layerCount = 4)
        => new(TempPath(), Tech(layerCount));

    private static StackupLayerRowViewModel Row(TechEditorViewModel vm, string name)
        => vm.StackupLayers.Single(r => r.StagedName == name);

    // ── Cardinality: §10.4 states two, so the editor offers two controls ──────

    [Fact]
    public void AViaAndADielectric_GetTheSingleSelectCombo_AConductorTheMultiSelectList()
    {
        var vm = Vm();

        Assert.True(Row(vm, "Metal1").AllowMultipleDrawingLayers);
        Assert.False(Row(vm, "Metal1").IsSingleDrawingLayer);

        Assert.True(Row(vm, "Via1").IsSingleDrawingLayer);
        Assert.True(Row(vm, "ox1").IsSingleDrawingLayer);
    }

    /// <summary>"(none)" is a selection, so clearing a binding is a pick rather than a hunt for the
    /// ticked box among several hundred.</summary>
    [Fact]
    public void TheSingleSelectCombo_OffersNone_First_AndThenEveryLayer()
    {
        var choices = Row(Vm(), "Via1").DrawingLayerChoices;

        Assert.Same(DrawingLayerChoice.None, choices[0]);
        Assert.Equal(5, choices.Count);   // (none) + four layers
    }

    /// <remarks>Every committed edit replaces <c>Working</c> and rebuilds the row collection, so a
    /// row is re-fetched after each one — the same orphan-and-rebuild convention the whole editor
    /// runs on.</remarks>
    [Fact]
    public void PickingThroughTheCombo_BindsExactlyOneLayer_AndIsUndoable()
    {
        var vm = Vm();

        var via = Row(vm, "Via1");
        via.SelectedDrawingLayerChoice = via.DrawingLayerChoices[1];
        Assert.Equal([new LayerKey(1, 0)], Row(vm, "Via1").Layer.DrawingLayers);

        via = Row(vm, "Via1");
        via.SelectedDrawingLayerChoice = via.DrawingLayerChoices[2];
        Assert.Equal([new LayerKey(2, 0)], Row(vm, "Via1").Layer.DrawingLayers);   // replaced, never added

        vm.UndoRedo.Undo();
        Assert.Equal([new LayerKey(1, 0)], Row(vm, "Via1").Layer.DrawingLayers);
    }

    /// <summary>The combo shows what the model holds after a rebuild, not a stale selection.</summary>
    [Fact]
    public void TheComboReflectsTheModel_AfterEveryRebuild()
    {
        var vm  = Vm();
        var via = Row(vm, "Via1");
        via.SelectedDrawingLayerChoice = via.DrawingLayerChoices[2];

        var rebuilt = Row(vm, "Via1");
        Assert.Equal(new LayerKey(2, 0), rebuilt.SelectedDrawingLayerChoice!.Key);
    }

    [Fact]
    public void SelectingNone_ClearsTheBinding()
    {
        var vm  = Vm();
        var via = Row(vm, "Via1");
        via.SelectedDrawingLayerChoice = via.DrawingLayerChoices[1];

        Row(vm, "Via1").SelectedDrawingLayerChoice = DrawingLayerChoice.None;

        Assert.Empty(Row(vm, "Via1").Layer.DrawingLayers);
    }

    [Fact]
    public void AConductor_StillBindsMoreThanOne()
    {
        var vm = Vm();
        var m1 = Row(vm, "Metal1");

        m1.DrawingLayerOptions[0].IsChecked = true;
        Row(vm, "Metal1").DrawingLayerOptions[2].IsChecked = true;

        var after = Row(vm, "Metal1");
        Assert.Equal(2, after.Layer.DrawingLayers.Count);
        Assert.Contains("Metal1", after.DrawingLayerSummary);
        Assert.Contains("Aux3", after.DrawingLayerSummary);
    }

    // ── Findability at a real layer count ─────────────────────────────────────

    [Fact]
    public void TheConductorList_IsFilterable_SoAProcessSizedLayerTableStaysUsable()
    {
        var vm = Vm(layerCount: 377);
        var m1 = Row(vm, "Metal1");

        Assert.Equal(377, m1.FilteredDrawingLayerOptions.Count);

        m1.DrawingLayerFilter = "Metal";
        Assert.Equal(["Metal1", "Metal2"], m1.FilteredDrawingLayerOptions.Select(o => o.Name));

        m1.DrawingLayerFilter = "";
        Assert.Equal(377, m1.FilteredDrawingLayerOptions.Count);
    }

    /// <summary>Filtering is a VIEW over the options; ticking a filtered row still binds it.</summary>
    [Fact]
    public void TickingAFilteredRow_BindsTheRealLayer()
    {
        var vm = Vm(layerCount: 377);
        var m1 = Row(vm, "Metal1");

        m1.DrawingLayerFilter = "Metal2";
        m1.FilteredDrawingLayerOptions.Single().IsChecked = true;

        Assert.Equal([new LayerKey(2, 0)], Row(vm, "Metal1").Layer.DrawingLayers);
    }

    // ── Via span: imported correctly, and previously shown nowhere ────────────

    [Fact]
    public void AViasSpan_IsReadable_AndOnlyConductorsAreOffered()
    {
        var via = Row(Vm(), "Via1");

        Assert.Equal("Metal1", via.SelectedSpanFrom);
        Assert.Equal("Metal2", via.SelectedSpanTo);

        // "(none)" plus the two conductors — a dielectric is not a span end.
        Assert.Equal([StackupLayerRowViewModel.SpanNone, "Metal2", "Metal1"], via.SpanChoices);
    }

    [Fact]
    public void EditingASpan_Commits_AndIsUndoable()
    {
        var vm  = Vm();
        var via = Row(vm, "Via1");

        via.SelectedSpanTo = StackupLayerRowViewModel.SpanNone;
        Assert.Null(Row(vm, "Via1").Layer.SpanToLayer);

        vm.UndoRedo.Undo();
        Assert.Equal("Metal2", Row(vm, "Via1").Layer.SpanToLayer);
    }

    [Fact]
    public void AViasFill_AndItsWallThickness_AreEditable()
    {
        var vm = Vm();

        // A via with no fill stated defaults to Plated, so the wall field is offered.
        Assert.True(Row(vm, "Via1").IsPlatedVia);

        var via = Row(vm, "Via1");
        via.StagedWallThickness = "2";      // technology default display unit is µm
        via.CommitWallThickness();
        Assert.Equal(2 * LayoutUnits.DefaultDbuPerMicron, Row(vm, "Via1").Layer.WallThicknessDbu);

        Row(vm, "Via1").SelectedFill = ViaFillKind.Solid;
        Assert.False(Row(vm, "Via1").IsPlatedVia);
    }

    /// <summary>Blank is the right answer for a wall the process never stated — not a parse error.</summary>
    [Fact]
    public void ABlankWallThickness_ClearsIt()
    {
        var vm  = Vm();
        var via = Row(vm, "Via1");
        via.StagedWallThickness = "2";
        via.CommitWallThickness();

        via = Row(vm, "Via1");
        via.StagedWallThickness = "";
        via.CommitWallThickness();

        Assert.Null(Row(vm, "Via1").Layer.WallThicknessDbu);
    }
}

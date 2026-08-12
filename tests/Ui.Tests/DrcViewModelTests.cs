using System.IO;
using System.Linq;
using CircuitRF.Ui.Commands.Layout;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Drc;
using CircuitRF.Ui.Theming;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// L5b, editor side: the violations panel's own behaviour, and R16b's "DRC never blocks editing"
/// expressed as the one way a check IS allowed to affect the editor — a stale result is dropped.
/// </summary>
public class DrcViewModelTests
{
    private static readonly LayerKey M1 = new(1, 0);

    private static Technology Tech(long minWidth = 100, long minSpacing = 0) => new()
    {
        Name   = "TestTech",
        Layers = [new LayerDef { Key = M1, Name = "M1", Color = new Rgba(200, 200, 200, 255) }],
        DrcRules = minSpacing > 0
            ?
            [
                new DrcRule { Name = "M1 min width",   Kind = DrcRuleKind.MinWidth,   Layer = M1, ValueDbu = minWidth },
                new DrcRule { Name = "M1 min spacing", Kind = DrcRuleKind.MinSpacing, Layer = M1, ValueDbu = minSpacing },
            ]
            : [new DrcRule { Name = "M1 min width", Kind = DrcRuleKind.MinWidth, Layer = M1, ValueDbu = minWidth }],
    };

    /// <summary>A scratch document with one too-narrow trace on it.</summary>
    private static LayoutEditorViewModel Vm(long traceHeight = 60)
    {
        var model = new LayoutView { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 100 };
        model.Shapes.Add(new RectShape { Layer = M1, X1 = 0, Y1 = 0, X2 = 1000, Y2 = traceHeight });

        var vm = new LayoutEditorViewModel(model);
        vm.ApplyTechResolution(new TechResolution(Tech(), null, TechResolutionSource.WorkspaceDefault, []));
        return vm;
    }

    [Fact]
    public void RunDrc_PopulatesThePanel_AndNamesTheTechnologyItCheckedAgainst()
    {
        var vm = Vm();

        var result = vm.RunDrc();

        Assert.False(result.IsClean);
        Assert.Single(vm.DrcViolations);
        Assert.True(vm.HasDrcResult);

        // The one thing a DRC surface must never leave implicit — a clean result checked against the
        // wrong process's rules looks exactly like a clean one checked against the right process.
        Assert.Contains("TestTech", vm.DrcTechnologyText);
        Assert.Contains("error", vm.DrcSummaryText);
    }

    [Fact]
    public void ACleanLayout_ReportsCleanRatherThanUnchecked()
    {
        var vm = Vm(traceHeight: 500);

        var result = vm.RunDrc();

        Assert.True(result.IsClean);
        Assert.Empty(vm.DrcViolations);
        Assert.True(vm.HasDrcResult);
        Assert.Contains("No violations", vm.DrcSummaryText);
    }

    [Fact]
    public void BeforeAnyCheck_ThePanelSaysNotChecked_RatherThanClean()
    {
        var vm = Vm();

        Assert.False(vm.HasDrcResult);
        Assert.Equal("Not checked.", vm.DrcSummaryText);
        Assert.Equal("", vm.DrcTechnologyText);
        Assert.Empty(vm.Overlay.DrcMarkers);
    }

    /// <summary>
    /// A marker drawn over geometry that has moved is worse than no marker, and a violation count
    /// that no longer matches the artwork is worse than no count.
    /// </summary>
    [Fact]
    public void EditingTheGeometry_DropsTheResult_RatherThanLeavingItStale()
    {
        var vm = Vm();
        vm.RunDrc();
        Assert.True(vm.HasDrcResult);

        vm.Execute(new AddShapeCommand(vm.Model,
            new RectShape { Layer = M1, X1 = 5000, Y1 = 0, X2 = 6000, Y2 = 500 }));

        Assert.False(vm.HasDrcResult);
        Assert.Empty(vm.DrcViolations);
        Assert.Empty(vm.Overlay.DrcMarkers);
    }

    [Fact]
    public void Markers_ReachTheOverlay_AndTheToggleSuppressesThem()
    {
        var vm = Vm();
        vm.RunDrc();

        Assert.Single(vm.Overlay.DrcMarkers);
        Assert.NotEmpty(vm.Overlay.DrcMarkers[0].Rings);

        vm.ShowDrcMarkers = false;
        Assert.Empty(vm.Overlay.DrcMarkers);

        vm.ShowDrcMarkers = true;
        Assert.Single(vm.Overlay.DrcMarkers);
    }

    [Fact]
    public void SelectingARow_MarksThatMarkerSelected_SoClickToZoomLandsSomewhereObvious()
    {
        var vm = Vm();
        vm.RunDrc();

        Assert.All(vm.Overlay.DrcMarkers, m => Assert.False(m.Selected));

        vm.SelectedDrcViolation = vm.DrcViolations[0];
        Assert.Single(vm.Overlay.DrcMarkers, m => m.Selected);
    }

    [Fact]
    public void ZoomToSelectedViolation_RaisesTheViewLayerSeam_WithTheMarkersOwnRegion()
    {
        var vm = Vm();
        vm.RunDrc();
        vm.SelectedDrcViolation = vm.DrcViolations[0];

        Bbox? requested = null;
        vm.ZoomToRegionRequested += b => requested = b;

        vm.ZoomToSelectedViolationCommand.Execute(null);

        Assert.NotNull(requested);
        Assert.Equal(vm.DrcViolations[0].Violation.Marker, requested!.Value);
    }

    // ── Waivers, through the panel ───────────────────────────────────────────

    [Fact]
    public void Waiving_SuppressesTheCount_KeepsTheRowListed_AndDirtiesTheDocument()
    {
        var vm = Vm();
        vm.RunDrc();
        vm.MarkSaved();
        Assert.False(vm.IsDirty);

        vm.SetWaived(vm.DrcViolations[0], waived: true, reason: "reviewed with the fab");

        Assert.True(vm.IsDirty);
        Assert.Single(vm.Model.DrcWaivers);

        var row = Assert.Single(vm.DrcViolations);
        Assert.True(row.IsWaived);
        Assert.False(row.IsError);
        Assert.True(vm.DrcResult!.IsClean);
        Assert.Equal(1, vm.DrcResult.WaivedCount);
    }

    [Fact]
    public void UnWaiving_BringsTheViolationBack()
    {
        var vm = Vm();
        vm.RunDrc();
        vm.SetWaived(vm.DrcViolations[0], waived: true, reason: "x");
        Assert.True(vm.DrcResult!.IsClean);

        vm.SetWaived(vm.DrcViolations[0], waived: false);

        Assert.Empty(vm.Model.DrcWaivers);
        Assert.False(vm.DrcResult!.IsClean);
        Assert.Equal(1, vm.DrcResult.ErrorCount);
    }

    [Fact]
    public void UnWaivingSomethingThatWasNeverWaived_IsANoOp_AndNeverDirtiesTheDocument()
    {
        var vm = Vm();
        vm.RunDrc();
        vm.MarkSaved();

        vm.SetWaived(vm.DrcViolations[0], waived: false);

        Assert.False(vm.IsDirty);
        Assert.Empty(vm.Model.DrcWaivers);
    }

    /// <summary>
    /// Waiving is a review judgement recorded against the design, not a geometry edit — putting it on
    /// the shape-editing undo stack would let Ctrl+Z after an unrelated edit silently revoke it.
    /// </summary>
    [Fact]
    public void Waiving_PushesNothingOntoTheUndoStack()
    {
        var vm = Vm();
        vm.RunDrc();
        bool couldUndoBefore = vm.UndoRedo.CanUndo;

        vm.SetWaived(vm.DrcViolations[0], waived: true, reason: "x");

        Assert.Equal(couldUndoBefore, vm.UndoRedo.CanUndo);
    }

    [Fact]
    public void RunDrc_WithNoTechnology_ReportsWhyRatherThanThrowing()
    {
        var model = new LayoutView { DbuPerMicron = 1000, SnapDbu = 100 };
        model.Shapes.Add(new RectShape { Layer = M1, X1 = 0, Y1 = 0, X2 = 1000, Y2 = 60 });
        var vm = new LayoutEditorViewModel(model);

        var result = vm.RunDrc();

        Assert.Empty(result.Violations);
        Assert.NotEmpty(result.Diagnostics);
        Assert.Equal("No technology resolved.", vm.DrcTechnologyText);
    }

    [Fact]
    public void RowText_ReadsInTheDocumentsOwnDisplayUnit()
    {
        var vm = Vm();
        vm.RunDrc();

        var row = vm.DrcViolations[0];
        Assert.Contains("narrower than", row.DetailText);
        Assert.Contains("µm", row.DetailText);        // 100 DBU at 1000 DBU/µm, shown in µm
        Assert.Contains("µm", row.LocationText);
    }
}

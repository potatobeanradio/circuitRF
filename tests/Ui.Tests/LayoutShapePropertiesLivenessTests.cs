using System.Collections.Generic;
using Avalonia.Input;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Messages;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Tests;

// Phase L1j — docs/sonnet-briefs/brief-L1j-properties-inspector.md gates 2-7, 13: liveness during a
// drag, read-only mid-drag, the focus guard, R-L1j-4's size/position semantics, and validation.
// Drives LayoutEditorViewModel's real OnPointerPressed/Moved/Released state machine (never reaching
// into VM internals) so a wiring bug fails here exactly as it would through the real canvas.

public class LayoutShapePropertiesLivenessTests
{
    private sealed class RecordingSink : IMessageSink
    {
        public List<(MessageLevel Level, string Text)> Messages { get; } = [];
        public void Post(MessageLevel level, string text, string? filePath = null) => Messages.Add((level, text));
        public void Clear() => Messages.Clear();
    }

    private static LayoutView FreshModel(long snapDbu = 1000) => new()
    {
        DbuPerMicron = 1000,
        DisplayUnit  = LayoutUnit.Um,
        SnapDbu      = snapDbu,
    };

    private static (LayoutEditorViewModel Vm, LayoutShapePropertiesViewModel Props) Setup(LayoutView model, IMessageSink? sink = null)
    {
        var vm = new LayoutEditorViewModel(model, messageSink: sink) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        var props = new LayoutShapePropertiesViewModel();
        props.SetContext(vm);
        return (vm, props);
    }

    private static void Click(LayoutEditorViewModel vm, double wx, double wy, KeyModifiers mods = default, long tolDbu = 40)
    {
        vm.OnPointerPressed(wx, wy, mods, 1, tolDbu);
        vm.OnPointerReleased(wx, wy, mods);
    }

    // ── Gate 2: live during a handle drag (the headline requirement, R-L1j-1) ──────────────────

    [Fact]
    public void RectCornerDrag_UpdatesWidthAndHeightOnEveryPointerMove_BeforeRelease()
    {
        var model = FreshModel();
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000 });
        var (vm, props) = Setup(model);

        Click(vm, 5000, 5000);
        Assert.Equal("10", props.RectWidthText);
        Assert.Equal("10", props.RectHeightText);

        vm.OnPointerPressed(10_000, 10_000, KeyModifiers.None, 1, 40); // top-right corner handle
        vm.OnPointerMoved(15_000, 12_000, true, KeyModifiers.None, 40); // BEFORE release

        Assert.Equal("15", props.RectWidthText);
        Assert.Equal("12", props.RectHeightText);
        Assert.False(props.IsEditingEnabled); // R-L1j-2, exercised again in gate 5

        vm.OnPointerMoved(18_000, 9_000, true, KeyModifiers.None, 40); // a SECOND move, still no release
        Assert.Equal("18", props.RectWidthText);
        Assert.Equal("9", props.RectHeightText);

        vm.OnPointerReleased(18_000, 9_000, KeyModifiers.None);
        Assert.True(props.IsEditingEnabled);
    }

    [Fact]
    public void CircleRadiusDrag_UpdatesRadiusLive_FreeFromTheSameChange()
    {
        var model = FreshModel();
        model.Shapes.Add(new CircleShape { Layer = new LayerKey(1, 0), Cx = 5000, Cy = 5000, R = 2000 });
        var (vm, props) = Setup(model);

        Click(vm, 5000, 5000);
        Assert.Equal("2", props.RadiusText);

        vm.OnPointerPressed(7000, 5000, KeyModifiers.None, 1, 40); // radius handle at Cx+R
        vm.OnPointerMoved(9000, 5000, true, KeyModifiers.None, 40); // new R = 4000

        Assert.Equal("4", props.RadiusText);
        vm.OnPointerReleased(9000, 5000, KeyModifiers.None);
    }

    [Fact]
    public void RoundedRectCornerRadiusDrag_UpdatesCornerRadiusLive_FreeFromTheSameChange()
    {
        var model = FreshModel();
        model.Shapes.Add(new RoundedRectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000, CornerRadius = 1000 });
        var (vm, props) = Setup(model);

        Click(vm, 5000, 5000);
        Assert.Equal("1", props.CornerRadiusText);

        vm.OnPointerPressed(1000, 10_000, KeyModifiers.None, 1, 40); // corner-radius handle at (X1+R, Y2)
        vm.OnPointerMoved(2000, 10_000, true, KeyModifiers.None, 40); // new radius = 2000

        Assert.Equal("2", props.CornerRadiusText);
        vm.OnPointerReleased(2000, 10_000, KeyModifiers.None);
    }

    // ── Gate 3: live during a move drag ──────────────────────────────────────────────────────────

    [Fact]
    public void WholeShapeMoveDrag_UpdatesPositionFieldsContinuously()
    {
        var model = FreshModel();
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 5000, Y2 = 5000 });
        var (vm, props) = Setup(model);

        Click(vm, 2500, 2500);
        Assert.Equal("0", props.RectXText);
        Assert.Equal("0", props.RectYText);

        vm.OnPointerPressed(2500, 2500, KeyModifiers.None, 1, 40); // press inside body -> move drag
        vm.OnPointerMoved(5500, 4500, true, KeyModifiers.None, 40); // dx=3000, dy=2000, no release yet

        Assert.Equal("3", props.RectXText);
        Assert.Equal("2", props.RectYText);

        vm.OnPointerReleased(5500, 4500, KeyModifiers.None);
        Assert.Equal("3", props.RectXText); // committed value matches the live preview
    }

    // ── Gate 4: live on commit paths (undo/redo/boolean/paste) — the trigger already existed ────

    [Fact]
    public void Undo_RefreshesThePanel()
    {
        var model = FreshModel();
        model.Shapes.Add(new RoundedRectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000, CornerRadius = 1000 });
        var (vm, props) = Setup(model);

        Click(vm, 5000, 5000);
        props.CommitCornerRadiusText("2000nm");
        Assert.Equal("2", props.CornerRadiusText);

        vm.UndoRedo.Undo();
        Assert.Equal("1", props.CornerRadiusText);
    }

    // ── Gate 5: read-only while dragging (R-L1j-2) ───────────────────────────────────────────────

    [Fact]
    public void CommitAttemptedMidDrag_IsRefused_FieldsReEnableOnRelease()
    {
        var model = FreshModel();
        var rect = new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000 };
        model.Shapes.Add(rect);
        var (vm, props) = Setup(model);

        Click(vm, 5000, 5000);
        vm.OnPointerPressed(10_000, 10_000, KeyModifiers.None, 1, 40);
        vm.OnPointerMoved(15_000, 15_000, true, KeyModifiers.None, 40);

        Assert.False(props.IsEditingEnabled);
        props.CommitRectWidthText("999nm"); // attempted while a drag is live
        Assert.Equal(10_000, rect.X2); // refused — model unchanged

        vm.OnPointerReleased(15_000, 15_000, KeyModifiers.None);
        Assert.True(props.IsEditingEnabled);
    }

    // ── Gate 6: focus is never clobbered (R-L1j-3) ──────────────────────────────────────────────

    [Fact]
    public void FocusedField_SurvivesARefreshTriggeredByAnUnrelatedModelChange()
    {
        var model = FreshModel();
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000 });
        var (vm, props) = Setup(model);

        Click(vm, 5000, 5000);
        props.SetFocusedField("RectWidth");
        props.RectWidthText = "in-progress-typing"; // simulates the user's own uncommitted edit

        model.NotifyChanged(); // exactly what every command's Execute()/Undo() calls internally

        Assert.Equal("in-progress-typing", props.RectWidthText);

        props.SetFocusedField(null); // focus genuinely leaves
        model.NotifyChanged();
        Assert.Equal("10", props.RectWidthText); // now legitimately reformatted
    }

    // ── Gate 7: R-L1j-4 — Width/Height keep the min corner fixed; RoundedRect clamps + reports ──

    [Fact]
    public void CommitWidth_KeepsX1Fixed_MovesX2()
    {
        var model = FreshModel();
        var rect = new RectShape { Layer = new LayerKey(1, 0), X1 = 5000, Y1 = 0, X2 = 15_000, Y2 = 10_000 };
        model.Shapes.Add(rect);
        var (vm, props) = Setup(model);

        Click(vm, 10_000, 5000);
        props.CommitRectWidthText("2.9mm");

        long expectedW = LayoutUnits.ToDbu(2.9m, LayoutUnit.Mm, model.DbuPerMicron);
        Assert.Equal(5000, rect.X1);
        Assert.Equal(5000 + expectedW, rect.X2);
    }

    [Fact]
    public void CommitHeight_KeepsY1Fixed_MovesY2()
    {
        var model = FreshModel();
        var rect = new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 5000, X2 = 10_000, Y2 = 15_000 };
        model.Shapes.Add(rect);
        var (vm, props) = Setup(model);

        Click(vm, 5000, 10_000);
        props.CommitRectHeightText("2.9mm");

        long expectedH = LayoutUnits.ToDbu(2.9m, LayoutUnit.Mm, model.DbuPerMicron);
        Assert.Equal(5000, rect.Y1);
        Assert.Equal(5000 + expectedH, rect.Y2);
    }

    [Fact]
    public void ShrinkingRoundedRectWidth_ClampsCornerRadiusToHalfTheShorterSide_AndReportsIt()
    {
        var model = FreshModel();
        var rr = new RoundedRectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000, CornerRadius = 4000 };
        model.Shapes.Add(rr);
        var sink = new RecordingSink();
        var (vm, props) = Setup(model, sink);

        Click(vm, 5000, 5000);
        props.CommitRectWidthText("2000nm"); // width -> 2000; height stays 10000; half of shorter side = 1000

        Assert.Equal(2000, rr.X2 - rr.X1);
        Assert.Equal(1000, rr.CornerRadius); // clamped
        Assert.Contains(sink.Messages, m => m.Level == MessageLevel.Warning);
    }

    // ── Gate 13: validation ─────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("2.9mm")]
    [InlineData("115 mil")]
    [InlineData("50u")]
    [InlineData("7")]
    public void DimensionField_AcceptsEveryLayoutUnitsForm(string text)
    {
        var model = FreshModel();
        var circle = new CircleShape { Layer = new LayerKey(1, 0), Cx = 0, Cy = 0, R = 1000 };
        model.Shapes.Add(circle);
        var (vm, props) = Setup(model);

        Click(vm, 0, 0);
        props.CommitRadiusText(text);

        Assert.False(props.HasRadiusError);
        Assert.True(circle.R > 0);
    }

    [Fact]
    public void InvalidText_ShowsVisibleErrorState_KeepsUserTextUntouched_NeverMutatesTheModel()
    {
        var model = FreshModel();
        var circle = new CircleShape { Layer = new LayerKey(1, 0), Cx = 0, Cy = 0, R = 1000 };
        model.Shapes.Add(circle);
        var (vm, props) = Setup(model);

        Click(vm, 0, 0);
        props.SetFocusedField("Radius"); // the box the user is actively typing into
        props.RadiusText = "garbage";    // what a real TwoWay binding would already have set
        props.CommitRadiusText("garbage");

        Assert.True(props.HasRadiusError);
        Assert.Equal("Invalid value", props.RadiusError);
        Assert.Equal("garbage", props.RadiusText); // NOT silently discarded
        Assert.Equal(1000, circle.R);              // model untouched
    }

    [Fact]
    public void OutOfRangeInput_GetsASpecificReason_NotAGenericRejection()
    {
        var model = FreshModel();
        var circle = new CircleShape { Layer = new LayerKey(1, 0), Cx = 0, Cy = 0, R = 1000 };
        model.Shapes.Add(circle);
        var (vm, props) = Setup(model);

        Click(vm, 0, 0);
        props.CommitRadiusText("-5um");

        Assert.True(props.HasRadiusError);
        Assert.Equal("Radius must be greater than 0", props.RadiusError);
        Assert.Equal(1000, circle.R);
    }

    [Fact]
    public void Escape_RevertsToCanonicalValue_AndClearsTheError()
    {
        var model = FreshModel();
        var circle = new CircleShape { Layer = new LayerKey(1, 0), Cx = 0, Cy = 0, R = 1000 };
        model.Shapes.Add(circle);
        var (vm, props) = Setup(model);

        Click(vm, 0, 0);
        string canonical = props.RadiusText;
        props.CommitRadiusText("garbage");
        Assert.True(props.HasRadiusError);

        props.RevertField("Radius");

        Assert.False(props.HasRadiusError);
        Assert.Equal(canonical, props.RadiusText);
    }

    [Fact]
    public void MultiSelection_DifferingRectWidth_ShowsBlank_CommitAppliesToAll_OneUndoEntry()
    {
        var model = FreshModel();
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000 });
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 20_000, Y1 = 0, X2 = 23_000, Y2 = 1000 });
        var (vm, props) = Setup(model);

        Click(vm, 500, 500);
        Click(vm, 21_500, 500, KeyModifiers.Shift);
        Assert.Equal(2, vm.SelectedIndices.Count);
        Assert.Equal("", props.RectWidthText); // 1000 vs 3000 differ

        props.CommitRectWidthText("2000nm");
        Assert.Equal(2000, ((RectShape)model.Shapes[0]).X2 - ((RectShape)model.Shapes[0]).X1);
        Assert.Equal(2000, ((RectShape)model.Shapes[1]).X2 - ((RectShape)model.Shapes[1]).X1);

        vm.UndoRedo.Undo(); // one undo entry restores both
        Assert.Equal(1000, ((RectShape)model.Shapes[0]).X2 - ((RectShape)model.Shapes[0]).X1);
        Assert.Equal(3000, ((RectShape)model.Shapes[1]).X2 - ((RectShape)model.Shapes[1]).X1);
    }
}

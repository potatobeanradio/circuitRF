using System.Linq;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.ViewModels;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests.Layout;

/// <summary>
/// docs/design/layout-view.md §9B.3/§9B.6 — gate 13. Multi-selection ruler editing is the EXISTING
/// mechanism (R-rul-11a), and a mixed size-mode selection disables the one size field rather than
/// guessing a unit (R-rul-3a).
/// </summary>
public class LayoutRulerPropertiesTests : System.IDisposable
{
    public LayoutRulerPropertiesTests() => LayoutTextOutline.TestOverrideTypeface = SKTypeface.Default;

    public void Dispose()
    {
        LayoutTextOutline.TestOverrideTypeface = null;
        System.GC.SuppressFinalize(this);
    }

    private static (LayoutEditorViewModel Vm, LayoutShapePropertiesViewModel Panel, LayoutView Model) Fixture(
        int count, System.Action<int, RulerAnnotation>? tweak = null)
    {
        var model = new LayoutView { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um };
        for (int i = 0; i < count; i++)
        {
            var r = new RulerAnnotation
            {
                X1 = 0, Y1 = i * 1_000, X2 = 5_000, Y2 = i * 1_000,
                SizeMode = RulerSizeMode.Fixed, TextSizePt = 11.0, TextHeightDbu = 800,
            };
            tweak?.Invoke(i, r);
            model.Rulers.Add(r);
        }
        var vm = new LayoutEditorViewModel(model);
        var panel = new LayoutShapePropertiesViewModel();
        panel.SetContext(vm);
        vm.SelectRulers(Enumerable.Range(0, count));
        return (vm, panel, model);
    }

    [Fact]
    public void ARulerSelection_ShowsTheRulerContext_WithTheMeasuredDistanceReadOnly()
    {
        var (_, panel, _) = Fixture(1);
        Assert.True(panel.IsRulerContext);
        Assert.False(panel.IsInstanceContext);
        Assert.Equal("Ruler", panel.SelectionSummaryText);
        Assert.Equal("5 µm", panel.RulerDistanceText);
    }

    [Fact]
    public void TenRulers_OneTextSize_AllTenChange_AndOneUndoRestoresThemAll()
    {
        var (vm, panel, model) = Fixture(10);
        Assert.Equal("11", panel.RulerSizeText);
        Assert.Equal("Text size (pt)", panel.RulerSizeCaption);

        panel.CommitRulerSizeText("16");

        Assert.All(model.Rulers, r => Assert.Equal(16.0, r.TextSizePt));
        vm.UndoCommand.Execute(null);
        Assert.All(model.Rulers, r => Assert.Equal(11.0, r.TextSizePt));
    }

    [Fact]
    public void Style_Caption_AndTheDeltaToggle_AreAlsoOneUndoEntryEach()
    {
        var (vm, panel, model) = Fixture(10);

        panel.RulerStyleValue = LabelFontStyle.Bold;
        Assert.All(model.Rulers, r => Assert.Equal(LabelFontStyle.Bold, r.Style));
        vm.UndoCommand.Execute(null);
        Assert.All(model.Rulers, r => Assert.Equal(LabelFontStyle.Regular, r.Style));

        panel.CommitRulerCaptionText("min trace gap");
        Assert.All(model.Rulers, r => Assert.Equal("min trace gap", r.Caption));
        vm.UndoCommand.Execute(null);
        Assert.All(model.Rulers, r => Assert.Null(r.Caption));

        panel.RulerShowComponentsValue = true;
        Assert.All(model.Rulers, r => Assert.True(r.ShowComponents));
        vm.UndoCommand.Execute(null);
        Assert.All(model.Rulers, r => Assert.False(r.ShowComponents));
    }

    [Fact]
    public void AFieldWithMixedValues_ReadsBlank_NotTheFirstRulersValue()
    {
        var (_, panel, _) = Fixture(3, (i, r) => r.TextSizePt = 11.0 + i);
        Assert.Equal("", panel.RulerSizeText);

        var (_, panel2, _) = Fixture(3, (i, r) => { if (i == 0) r.Caption = "only me"; });
        Assert.Equal("", panel2.RulerCaptionText);

        var (_, panel3, _) = Fixture(3, (i, r) => { if (i == 0) r.Style = LabelFontStyle.Bold; });
        Assert.Null(panel3.RulerStyleValue);

        var (_, panel4, _) = Fixture(3, (i, r) => { if (i == 0) r.ShowComponents = true; });
        Assert.Null(panel4.RulerShowComponentsValue);
    }

    // ── R-rul-3a: mixed size MODES ────────────────────────────────────────────────────────────────

    [Fact]
    public void MixedSizeModes_DisableTheSizeField_WithItsStatedReason()
    {
        var (_, panel, _) = Fixture(10, (i, r) => { if (i >= 7) r.SizeMode = RulerSizeMode.Scaled; });

        Assert.Null(panel.RulerSizeModeValue);            // the combo's mixed state
        Assert.False(panel.IsRulerSizeEnabled);
        Assert.Equal("Set every selected ruler to the same size mode first.", panel.RulerSizeDisabledReason);
        Assert.Equal("", panel.RulerSizeText);
    }

    [Fact]
    public void SettingTheModeAcrossTheSelection_RelightsTheSizeField()
    {
        var (_, panel, model) = Fixture(10, (i, r) => { if (i >= 7) r.SizeMode = RulerSizeMode.Scaled; });
        Assert.False(panel.IsRulerSizeEnabled);

        // Setting the mode is itself a multi-edit, so the fix is one click.
        panel.RulerSizeModeValue = RulerSizeMode.Scaled;

        Assert.All(model.Rulers, r => Assert.Equal(RulerSizeMode.Scaled, r.SizeMode));
        Assert.True(panel.IsRulerSizeEnabled);
        Assert.Null(panel.RulerSizeDisabledReason);
        Assert.Equal("Text height (µm)", panel.RulerSizeCaption);
        Assert.Equal("0.8", panel.RulerSizeText);
    }

    [Fact]
    public void AMixedModeSelection_NeverAcceptsANumber()
    {
        var (vm, panel, model) = Fixture(4, (i, r) => { if (i >= 2) r.SizeMode = RulerSizeMode.Scaled; });

        panel.CommitRulerSizeText("11");

        // Nothing written into either backing value, in either mode — R-rul-3a's whole point is that
        // "11" would mean 11 pt in two of them and 11 µm in the other two.
        Assert.All(model.Rulers, r => Assert.Equal(11.0, r.TextSizePt));
        Assert.All(model.Rulers, r => Assert.Equal(800, r.TextHeightDbu));
        Assert.False(vm.UndoCommand.CanExecute(null));
    }

    // ── R-rul-3: ONE size field, whose meaning follows the mode ───────────────────────────────────

    [Fact]
    public void TheOneSizeField_MeansPointsInFixed_AndAWorldLengthInScaled()
    {
        var (_, panel, model) = Fixture(1);
        Assert.Equal("Text size (pt)", panel.RulerSizeCaption);
        panel.CommitRulerSizeText("14.5");
        Assert.Equal(14.5, model.Rulers[0].TextSizePt);
        Assert.Equal(800, model.Rulers[0].TextHeightDbu);   // the other backing value is untouched

        panel.RulerSizeModeValue = RulerSizeMode.Scaled;
        Assert.Equal("Text height (µm)", panel.RulerSizeCaption);
        panel.CommitRulerSizeText("2.5");
        Assert.Equal(2_500, model.Rulers[0].TextHeightDbu);

        // §9B.7/R-rul-3: switching back finds the point size exactly as it was left.
        panel.RulerSizeModeValue = RulerSizeMode.Fixed;
        Assert.Equal("14.5", panel.RulerSizeText);
        Assert.Equal(14.5, model.Rulers[0].TextSizePt);
    }

    // ── R-L1j-1/R-L1j-2: the inspector is drag-override-aware, and disabled while one is live ─────

    [Fact]
    public void DuringARulerMoveDrag_TheInspectorShowsTheLiveEndpoints_AndIsNotEditable()
    {
        var (vm, panel, _) = Fixture(1);
        vm.NoteZoomPxPerDbu(0.01);

        // Select it through the canvas (a programmatic selection begins no drag), then drag it.
        vm.OnPointerPressed(2_500, 0, Avalonia.Input.KeyModifiers.None, 1, 40, 0.01);
        vm.OnPointerReleased(2_500, 0, Avalonia.Input.KeyModifiers.None);
        vm.OnPointerPressed(2_500, 0, Avalonia.Input.KeyModifiers.None, 1, 40, 0.01);
        vm.OnPointerMoved(2_500, 3_000, leftDown: true, Avalonia.Input.KeyModifiers.None, 40);

        Assert.Equal("3", panel.RulerY1Text);   // the LIVE position, not the stored 0
        Assert.False(panel.IsEditingEnabled);

        vm.OnPointerReleased(2_500, 3_000, Avalonia.Input.KeyModifiers.None);
        Assert.True(panel.IsEditingEnabled);
        Assert.Equal("3", panel.RulerY1Text);
    }

    [Fact]
    public void EndpointsAreEditable_AndTheDistanceFollows()
    {
        var (_, panel, model) = Fixture(1);
        panel.CommitField("RulerX2", "12");     // µm
        Assert.Equal(12_000, model.Rulers[0].X2);
        Assert.Equal(12_000, model.Rulers[0].DistanceDbu);
        Assert.Equal("12 µm", panel.RulerDistanceText);
    }
}

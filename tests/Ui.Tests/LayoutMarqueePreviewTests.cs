using System.Collections.Generic;
using System.Linq;
using Avalonia.Input;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.Tests;

// Phase L1i — docs/sonnet-briefs/brief-L1i-live-marquee-selection.md: while dragging a selection
// marquee, the highlight now updates LIVE (previously it appeared only on release, via
// LayoutEditorViewModel.CommitMarquee alone). R-L1i-1's whole point is that the live preview and the
// eventual commit share ONE compute function (LayoutEditorViewModel.ComputeMarqueeSelection) — these
// tests deliberately read the live preview through vm.Overlay.SelectedIndices (what the renderer
// actually highlights, per RebuildOverlay's "one highlight path" rule), not by reaching into VM
// internals or re-deriving expected hits by hand, so a divergence between preview and commit would
// show up exactly the way the user would see it.

public class LayoutMarqueePreviewTests
{
    private static LayoutView FreshModel(long snapDbu = 1000) => new()
    {
        DbuPerMicron = 1000,
        DisplayUnit  = LayoutUnit.Um,
        SnapDbu      = snapDbu,
    };

    private static LayoutEditorViewModel SelectVm(LayoutView model) =>
        new(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };

    private static (LayoutView Model, LayoutEditorViewModel Vm) MarqueeFixture()
    {
        var model = FreshModel();
        // A: fully enclosed by the -5000,-5000 -> 11_000,11_000 rect used throughout below.
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000 });
        // B: crosses that rect's right edge — not fully enclosed by it, but intersects it, and IS
        // fully enclosed by the larger -5000,-5000 -> 21_000,11_000 rect.
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 10_500, Y1 = 0, X2 = 20_000, Y2 = 10_000 });
        // C: far away, outside both.
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 30_000, Y1 = 30_000, X2 = 40_000, Y2 = 40_000 });
        return (model, SelectVm(model));
    }

    private static List<int> Sorted(IEnumerable<int> xs) { var l = new List<int>(xs); l.Sort(); return l; }

    private static void ClickAt(LayoutEditorViewModel vm, double wx, double wy, KeyModifiers mods = default, long tolDbu = 40)
    {
        vm.OnPointerPressed(wx, wy, mods, 1, tolDbu);
        vm.OnPointerReleased(wx, wy, mods);
    }

    // ── Gate 2: live highlight ──────────────────────────────────────────────────

    [Fact]
    public void ShapeEnteringRect_HighlightsBeforeRelease_ThenLeaving_UnHighlights()
    {
        var (_, vm) = MarqueeFixture();

        vm.OnPointerPressed(-5000, -5000, KeyModifiers.None, 1, 40);
        Assert.DoesNotContain(0, vm.Overlay.SelectedIndices); // zero-size rect — nothing hit yet

        vm.OnPointerMoved(11_000, 11_000, true, KeyModifiers.None, 40); // now encloses A
        Assert.Contains(0, vm.Overlay.SelectedIndices);          // highlighted BEFORE release
        Assert.Empty(vm.SelectedIndices);                        // NOT committed — R-L1i-2

        vm.OnPointerMoved(-2000, -2000, true, KeyModifiers.None, 40); // shrink away from A again
        Assert.DoesNotContain(0, vm.Overlay.SelectedIndices);         // un-highlights live

        vm.OnPointerReleased(-2000, -2000, KeyModifiers.None);
        Assert.Empty(vm.SelectedIndices); // rect ended up empty — nothing committed
    }

    // ── Gate 3: preview equals outcome, for all three modifier states ───────────

    [Theory]
    [InlineData(KeyModifiers.None)]
    [InlineData(KeyModifiers.Shift)]
    [InlineData(KeyModifiers.Control)]
    public void PreviewAtMomentOfRelease_IsIdenticalToCommittedSelection(KeyModifiers mods)
    {
        var (_, vm) = MarqueeFixture();
        if (mods != KeyModifiers.None)
            ClickAt(vm, 35_000, 35_000); // pre-select C so Shift/Ctrl have a base to combine against

        vm.OnPointerPressed(-5000, -5000, mods, 1, 40);
        vm.OnPointerMoved(21_000, 11_000, true, mods, 40); // encloses A and B

        var previewBeforeRelease = Sorted(vm.Overlay.SelectedIndices);
        vm.OnPointerReleased(21_000, 11_000, mods);
        var committed = Sorted(vm.SelectedIndices);

        Assert.Equal(previewBeforeRelease, committed); // same function, not a reimplementation
        Assert.NotEmpty(committed); // sanity — the rect actually hit something in every mode
    }

    // ── Gate 4: modifiers preview correctly ──────────────────────────────────────

    [Fact]
    public void PlainDrag_PreviewsHitsAlone()
    {
        var (_, vm) = MarqueeFixture();
        vm.OnPointerPressed(-5000, -5000, KeyModifiers.None, 1, 40);
        vm.OnPointerMoved(11_000, 11_000, true, KeyModifiers.None, 40); // encloses only A
        Assert.Equal(new List<int> { 0 }, Sorted(vm.Overlay.SelectedIndices));
    }

    [Fact]
    public void ShiftDrag_PreviewsBaseUnionHits()
    {
        var (_, vm) = MarqueeFixture();
        ClickAt(vm, 35_000, 35_000); // base = {C}
        Assert.Equal(new List<int> { 2 }, Sorted(vm.SelectedIndices));

        vm.OnPointerPressed(-5000, -5000, KeyModifiers.Shift, 1, 40);
        vm.OnPointerMoved(11_000, 11_000, true, KeyModifiers.Shift, 40); // encloses A
        Assert.Equal(new List<int> { 0, 2 }, Sorted(vm.Overlay.SelectedIndices)); // base ∪ hits, live
    }

    [Fact]
    public void CtrlDrag_PreviewsBaseXorHits_AlreadySelectedShapeVisiblyUnHighlightsLive()
    {
        var (_, vm) = MarqueeFixture();
        ClickAt(vm, 5000, 5000); // base = {A}
        Assert.Equal(new List<int> { 0 }, Sorted(vm.SelectedIndices));

        vm.OnPointerPressed(-5000, -5000, KeyModifiers.Control, 1, 40);
        vm.OnPointerMoved(1000, 1000, true, KeyModifiers.Control, 40); // tiny rect — does not yet reach A
        Assert.Contains(0, vm.Overlay.SelectedIndices); // still base-selected, un-toggled so far

        vm.OnPointerMoved(11_000, 11_000, true, KeyModifiers.Control, 40); // rect grows to enclose A -> toggles OFF
        Assert.DoesNotContain(0, vm.Overlay.SelectedIndices); // Ctrl-drag crossing an already-selected shape un-highlights it
    }

    // ── Gate 5: base selection is not corrupted by many intermediate moves ──────

    [Fact]
    public void ManyIntermediateMoves_UnderShift_ProduceSameResultAsOneMoveToTheSameEndpoint()
    {
        var (_, vmMany) = MarqueeFixture();
        ClickAt(vmMany, 35_000, 35_000); // base = {C}
        vmMany.OnPointerPressed(-5000, -5000, KeyModifiers.Shift, 1, 40);
        for (int i = 0; i <= 50; i++)
        {
            long x = -5000 + i * 26_000 / 50;
            long y = -5000 + i * 16_000 / 50;
            vmMany.OnPointerMoved(x, y, true, KeyModifiers.Shift, 40);
        }
        vmMany.OnPointerReleased(21_000, 11_000, KeyModifiers.Shift);
        var manyResult = Sorted(vmMany.SelectedIndices);

        var (_, vmOne) = MarqueeFixture();
        ClickAt(vmOne, 35_000, 35_000);
        vmOne.OnPointerPressed(-5000, -5000, KeyModifiers.Shift, 1, 40);
        vmOne.OnPointerMoved(21_000, 11_000, true, KeyModifiers.Shift, 40);
        vmOne.OnPointerReleased(21_000, 11_000, KeyModifiers.Shift);
        var oneResult = Sorted(vmOne.SelectedIndices);

        Assert.Equal(oneResult, manyResult); // a preview writing into _selectedIndices would corrupt the base and diverge
        Assert.Equal(new List<int> { 0, 1, 2 }, manyResult); // base {C} ∪ hits {A, B} at the final rect
    }

    // ── Gate 6: direction flip mid-drag ──────────────────────────────────────────

    [Fact]
    public void DirectionFlipMidDrag_HighlightAndRectStyleBothUpdateLive_CommitMatchesFinalDirection()
    {
        var (_, vm) = MarqueeFixture();

        vm.OnPointerPressed(11_000, 11_000, KeyModifiers.None, 1, 40);
        vm.OnPointerMoved(-5000, -5000, true, KeyModifiers.None, 40); // press.X > cur.X -> crossing (dashed)
        Assert.True(vm.Overlay.Marquee is { IsLeftToRight: false });
        Assert.Equal(new List<int> { 0, 1 }, Sorted(vm.Overlay.SelectedIndices)); // crossing: A enclosed, B intersects

        vm.OnPointerMoved(15_000, 9_000, true, KeyModifiers.None, 40); // past the press point -> enclose (solid)
        Assert.True(vm.Overlay.Marquee is { IsLeftToRight: true });
        Assert.Empty(vm.Overlay.SelectedIndices); // this small enclose rect contains neither shape fully

        vm.OnPointerReleased(15_000, 9_000, KeyModifiers.None);
        Assert.Empty(vm.SelectedIndices); // committed result matches the FINAL direction, not the crossing preview
    }

    // ── Gate 7: Escape mid-marquee ────────────────────────────────────────────────

    [Fact]
    public void Escape_MidMarquee_RestoresPriorHighlightImmediately_NotJustTheRealSelection()
    {
        var (_, vm) = MarqueeFixture();
        ClickAt(vm, 5000, 5000); // pre-select A
        Assert.Equal(new List<int> { 0 }, Sorted(vm.SelectedIndices));

        vm.OnPointerPressed(500_000, 500_000, KeyModifiers.Shift, 1, 40);
        vm.OnPointerMoved(600_000, 600_000, true, KeyModifiers.Shift, 40);
        Assert.NotNull(vm.Overlay.Marquee);

        vm.OnKeyDown(Key.Escape, KeyModifiers.None);

        Assert.Null(vm.Overlay.Marquee);
        Assert.Equal(new List<int> { 0 }, Sorted(vm.SelectedIndices));
        Assert.Equal(new List<int> { 0 }, Sorted(vm.Overlay.SelectedIndices)); // the rendered highlight, not just the model
    }

    // ── Gate 8: hidden / non-selectable layers are never previewed ──────────────

    [Fact]
    public void HiddenLayer_NeverPreviewedDuringDrag_MatchingTheCommitFilter()
    {
        var model = FreshModel();
        var hiddenKey = new LayerKey(2, 0);
        model.Shapes.Add(new RectShape { Layer = hiddenKey, X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000 });
        var vm = SelectVm(model);
        vm.Technology = new Technology
        {
            Name = "T",
            DefaultDisplayUnit = LayoutUnit.Um,
            DefaultSnapDbu = 1000,
            Layers = { new LayerDef { Key = hiddenKey, Name = "Hidden", Visible = false, Color = new Rgba(1, 2, 3) } },
        };

        vm.OnPointerPressed(-5000, -5000, KeyModifiers.None, 1, 40);
        vm.OnPointerMoved(11_000, 11_000, true, KeyModifiers.None, 40); // would enclose the shape if visible/selectable
        Assert.Empty(vm.Overlay.SelectedIndices);

        vm.OnPointerReleased(11_000, 11_000, KeyModifiers.None);
        Assert.Empty(vm.SelectedIndices);
    }

    [Fact]
    public void NonSelectableLayer_NeverPreviewedDuringDrag_MatchingTheCommitFilter()
    {
        var model = FreshModel();
        var lockedKey = new LayerKey(2, 0);
        model.Shapes.Add(new RectShape { Layer = lockedKey, X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000 });
        var vm = SelectVm(model);
        vm.Technology = new Technology
        {
            Name = "T",
            DefaultDisplayUnit = LayoutUnit.Um,
            DefaultSnapDbu = 1000,
            Layers = { new LayerDef { Key = lockedKey, Name = "Locked", Selectable = false, Color = new Rgba(1, 2, 3) } },
        };

        vm.OnPointerPressed(-5000, -5000, KeyModifiers.None, 1, 40);
        vm.OnPointerMoved(11_000, 11_000, true, KeyModifiers.None, 40);
        Assert.Empty(vm.Overlay.SelectedIndices);
    }

    // ── Gate 9: no recompute when the rectangle is unchanged ─────────────────────

    [Fact]
    public void SubPixelMoves_DoNotRecompute_ButCrossingOnePixelDoes()
    {
        var (_, vm) = MarqueeFixture();
        vm.OnPointerPressed(-5000, -5000, KeyModifiers.None, 1, 40); // BeginMarquee computes once
        int afterPress = vm.MarqueeRecomputeCount;

        vm.OnPointerMoved(-5000, -5000, true, KeyModifiers.None, 40, 100); // 0 DBU of movement, 100 DBU/px threshold
        Assert.Equal(afterPress, vm.MarqueeRecomputeCount);

        vm.OnPointerMoved(-4950, -5000, true, KeyModifiers.None, 40, 100); // 50 DBU — under the threshold
        Assert.Equal(afterPress, vm.MarqueeRecomputeCount);

        vm.OnPointerMoved(-4800, -5000, true, KeyModifiers.None, 40, 100); // 150 DBU from the last COMPUTED corner
        Assert.Equal(afterPress + 1, vm.MarqueeRecomputeCount);
    }

    // ── Gate 10: screen-pixel coverage through the real canvas conversion ───────

    public static IEnumerable<object[]> StarterTechs()
    {
        yield return new object[] { "Pcb2Layer", StarterTechnologies.Pcb2Layer() };
        yield return new object[] { "MmicGaAs", StarterTechnologies.MmicGaAs() };
    }

    [Theory]
    [MemberData(nameof(StarterTechs))]
    public void ScreenPixelMarquee_ThroughCanvasConversion_HighlightsLive_AtTheRealDefaultViewport(string name, Technology tech)
    {
        const double width = 1200, height = 800;
        var model = new LayoutView
        {
            DbuPerMicron = LayoutUnits.DefaultDbuPerMicron,
            DisplayUnit  = tech.DefaultDisplayUnit,
            SnapDbu      = tech.DefaultSnapDbu,
        };
        var vp = LayoutViewport.Default(width, height, model.SnapDbu, model.DbuPerMicron);

        long half = model.SnapDbu * 15;
        long cx = (long)System.Math.Round(vp.ScreenToWorldX(width / 2));
        long cy = (long)System.Math.Round(vp.ScreenToWorldY(height / 2));
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = cx - half, Y1 = cy - half, X2 = cx + half, Y2 = cy + half });

        var vm = SelectVm(model);
        long tolDbu = (long)System.Math.Round(4.0 / vp.Zoom);

        var (pwx, pwy) = (vp.ScreenToWorldX(50), vp.ScreenToWorldY(50));
        vm.OnPointerPressed(pwx, pwy, KeyModifiers.None, 1, tolDbu);

        var (mwx, mwy) = (vp.ScreenToWorldX(width - 50), vp.ScreenToWorldY(height - 50));
        vm.OnPointerMoved(mwx, mwy, true, KeyModifiers.None, tolDbu);
        Assert.True(vm.Overlay.SelectedIndices.Count == 1, $"{name}: shape should highlight live before release");

        vm.OnPointerReleased(mwx, mwy, KeyModifiers.None);
        Assert.True(vm.SelectedIndices.Count == 1, $"{name}: shape should be committed at release");
    }
}

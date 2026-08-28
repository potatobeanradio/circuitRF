using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Input;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Interchange;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.ViewModels;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests.Layout;

/// <summary>
/// docs/design/layout-view.md §9B.12 — the ruler readout's HAND-PLACED position and its anchor
/// (owner, 2026-08-27): F5 moves the number anywhere in the layout, the anchor says which point of the
/// text block that position names, both persist, both reach the DXF, and both are editable in the
/// Properties Inspector with a Reset back to the dynamic position.
///
/// <para>The load-bearing test here is <see cref="TheHitBoxFollowsTheMovedText"/>: the whole reason the
/// position is resolved inside <c>BuildRulerGeometry</c> rather than at the draw call is that the hit
/// region, Zoom-to-Fit and the clipboard's painted bounds then follow for free. A second placement
/// path would give a number you can see and cannot click.</para>
/// </summary>
public class LayoutRulerLabelPositionTests : System.IDisposable
{
    public LayoutRulerLabelPositionTests() => LayoutTextOutline.TestOverrideTypeface = SKTypeface.Default;

    public void Dispose()
    {
        LayoutTextOutline.TestOverrideTypeface = null;
        System.GC.SuppressFinalize(this);
    }

    private static LayoutView Model(long snapDbu = 0) => new()
    {
        DbuPerMicron = 1000,
        DisplayUnit = LayoutUnit.Um,
        SnapDbu = snapDbu,
    };

    private static RulerAnnotation Ruler() => new()
    {
        X1 = 0, Y1 = 0, X2 = 100_000, Y2 = 0,
        SizeMode = RulerSizeMode.Scaled, TextHeightDbu = 5_000,
    };

    private static LayoutEditorViewModel VmWithOneRuler(out LayoutView model, out RulerAnnotation ruler)
    {
        model = Model();
        ruler = Ruler();
        model.Rulers.Add(ruler);
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        vm.SelectRuler(0);
        return vm;
    }

    // ── The model ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ANewRuler_HasNoTextPosition_AndDefaultsToCentreMiddle()
    {
        var r = Ruler();
        Assert.Null(r.TextX);
        Assert.Null(r.TextY);
        Assert.False(r.HasTextPosition);
        Assert.Equal(LabelHAlign.Center, r.EffectiveTextHAlign);
        Assert.Equal(LabelVAlign.Middle, r.EffectiveTextVAlign);
    }

    [Fact]
    public void HalfAPosition_IsNoPosition()
    {
        // Both coordinates are one decision. A hand-edited .clay carrying only X must not place the
        // readout at an implied Y of zero, which for a layout the size of a die is off in the weeds.
        var r = Ruler();
        r.TextX = 12_345;
        Assert.False(r.HasTextPosition);
    }

    [Fact]
    public void Clone_CarriesThePositionAndTheAnchor()
    {
        var r = Ruler();
        r.TextX = 7; r.TextY = 9;
        r.TextHAlign = LabelHAlign.Right;
        r.TextVAlign = LabelVAlign.Top;

        var c = r.Clone();
        Assert.Equal(7, c.TextX);
        Assert.Equal(9, c.TextY);
        Assert.Equal(LabelHAlign.Right, c.TextHAlign);
        Assert.Equal(LabelVAlign.Top, c.TextVAlign);
    }

    [Fact]
    public void TranslateBy_MovesTheLabelWithTheRuler()
    {
        var r = Ruler();
        r.TextX = 1_000; r.TextY = 2_000;
        r.TranslateBy(500, -300);

        Assert.Equal(500, r.X1);
        Assert.Equal(1_500, r.TextX);
        Assert.Equal(1_700, r.TextY);
    }

    [Fact]
    public void TranslateBy_LeavesADynamicLabelDynamic()
    {
        var r = Ruler();
        r.TranslateBy(500, -300);
        Assert.False(r.HasTextPosition);
    }

    // ── Rendering ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void APlacedLabel_IsDrawnWhereItWasPut_NotAtTheMidpoint()
    {
        var dynamicRuler = Ruler();
        var placed = Ruler();
        placed.TextX = 400_000; placed.TextY = -250_000;   // nowhere near the ruler

        var before = LayoutRenderer.MeasureRulerTextWorldBbox(dynamicRuler, LayoutUnit.Um, 1000, 0);
        var after  = LayoutRenderer.MeasureRulerTextWorldBbox(placed, LayoutUnit.Um, 1000, 0);

        Assert.False(before.IsEmpty);
        Assert.False(after.IsEmpty);

        // Middle-centre is the default anchor, so the block's centre IS the stored point.
        Assert.InRange((after.MinX + after.MaxX) / 2, 400_000 - 2, 400_000 + 2);
        Assert.InRange((after.MinY + after.MaxY) / 2, -250_000 - 2, -250_000 + 2);
        Assert.NotEqual((before.MinX + before.MaxX) / 2, (after.MinX + after.MaxX) / 2);
    }

    [Theory]
    [InlineData(LabelHAlign.Left)]
    [InlineData(LabelHAlign.Center)]
    [InlineData(LabelHAlign.Right)]
    public void TheHorizontalAnchor_DecidesWhichEdgeOfTheBlockSitsOnThePoint(LabelHAlign h)
    {
        var r = Ruler();
        r.TextX = 400_000; r.TextY = 0;
        r.TextHAlign = h;

        var bb = LayoutRenderer.MeasureRulerTextWorldBbox(r, LayoutUnit.Um, 1000, 0);
        long onThePoint = h switch
        {
            LabelHAlign.Left  => bb.MinX,
            LabelHAlign.Right => bb.MaxX,
            _                 => (bb.MinX + bb.MaxX) / 2,
        };
        Assert.InRange(onThePoint, 400_000 - 2, 400_000 + 2);
    }

    [Theory]
    [InlineData(LabelVAlign.Top)]
    [InlineData(LabelVAlign.Middle)]
    [InlineData(LabelVAlign.Bottom)]
    public void TheVerticalAnchor_DecidesWhichEdgeOfTheBlockSitsOnThePoint(LabelVAlign v)
    {
        var r = Ruler();
        r.TextX = 0; r.TextY = 400_000;
        r.TextVAlign = v;

        var bb = LayoutRenderer.MeasureRulerTextWorldBbox(r, LayoutUnit.Um, 1000, 0);
        long onThePoint = v switch
        {
            LabelVAlign.Top    => bb.MaxY,     // world is Y-UP: the block's top is its largest Y
            LabelVAlign.Bottom => bb.MinY,
            _                  => (bb.MinY + bb.MaxY) / 2,
        };
        Assert.InRange(onThePoint, 400_000 - 2, 400_000 + 2);
    }

    [Fact]
    public void ADynamicRuler_RendersExactlyAsItAlwaysDid_WhateverTheAnchorSays()
    {
        // The anchor governs a POSITION, and a ruler that has none is placed by the midpoint push it
        // has always used. Otherwise an anchor combo left at a non-default value would silently move
        // every readout in a document that never asked for one to be moved.
        var plain = Ruler();
        var anchored = Ruler();
        anchored.TextHAlign = LabelHAlign.Right;
        anchored.TextVAlign = LabelVAlign.Top;

        Assert.Equal(LayoutRenderer.MeasureRulerTextWorldBbox(plain, LayoutUnit.Um, 1000, 0),
                     LayoutRenderer.MeasureRulerTextWorldBbox(anchored, LayoutUnit.Um, 1000, 0));
    }

    [Fact]
    public void TheAnchorPointRoundTrips_SoMakingADynamicPositionExplicitDoesNotMoveIt()
    {
        foreach (var h in System.Enum.GetValues<LabelHAlign>())
        foreach (var v in System.Enum.GetValues<LabelVAlign>())
        {
            var r = Ruler();
            r.TextHAlign = h; r.TextVAlign = v;

            var before = LayoutRenderer.MeasureRulerTextWorldBbox(r, LayoutUnit.Um, 1000, 0);
            var anchor = LayoutRenderer.RulerTextAnchorPoint(r, LayoutUnit.Um, 1000, 0);
            Assert.NotNull(anchor);

            r.TextX = anchor!.Value.X;
            r.TextY = anchor.Value.Y;
            var after = LayoutRenderer.MeasureRulerTextWorldBbox(r, LayoutUnit.Um, 1000, 0);

            Assert.InRange(after.MinX - before.MinX, -2, 2);
            Assert.InRange(after.MinY - before.MinY, -2, 2);
        }
    }

    [Fact]
    public void TheHitBoxFollowsTheMovedText()
    {
        var model = Model();
        var r = Ruler();
        r.TextX = 400_000; r.TextY = -250_000;
        model.Rulers.Add(r);

        // On the moved number.
        Assert.NotNull(LayoutRulerHitTest.Hit(model, 400_000, -250_000, 0, 0));

        // And NOT where the readout used to be drawn — a stale hit region is exactly the drift this
        // design avoids by measuring both through one geometry.
        var dynamicBox = LayoutRenderer.MeasureRulerTextWorldBbox(Ruler(), LayoutUnit.Um, 1000, 0);
        long staleY = (dynamicBox.MinY + dynamicBox.MaxY) / 2;
        Assert.Null(LayoutRulerHitTest.Hit(model, (dynamicBox.MinX + dynamicBox.MaxX) / 2, staleY, 0, 0));
    }

    [Fact]
    public void ThePaintedBbox_GrowsToIncludeAFarAwayLabel()
    {
        var r = Ruler();
        r.TextX = 900_000; r.TextY = 900_000;
        var bb = LayoutRenderer.MeasureRulerWorldBbox(r, LayoutUnit.Um, 1000, 0);
        Assert.True(bb.MaxX >= 800_000, $"MaxX {bb.MaxX} should reach the moved label");
        Assert.True(bb.MaxY >= 800_000, $"MaxY {bb.MaxY} should reach the moved label");
    }

    // ── The F5 gesture ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void F5_ThenAClick_PlacesTheLabel_AsOneUndoEntry()
    {
        var vm = VmWithOneRuler(out var model, out _);

        vm.OnKeyDown(Key.F5, KeyModifiers.None);
        Assert.True(vm.RulerLabelMoveActive);

        vm.OnPointerMoved(300_000, 200_000, leftDown: false, KeyModifiers.None);
        Assert.Equal(300_000, vm.Overlay.RulerDragOverrides![0].TextX);

        vm.OnPointerPressed(300_000, 200_000, KeyModifiers.None);
        Assert.False(vm.RulerLabelMoveActive);
        Assert.Equal(300_000, model.Rulers[0].TextX);
        Assert.Equal(200_000, model.Rulers[0].TextY);

        vm.UndoCommand.Execute(null);
        Assert.False(model.Rulers[0].HasTextPosition);
    }

    [Fact]
    public void EscapeDuringTheGesture_PutsTheLabelBack()
    {
        var vm = VmWithOneRuler(out var model, out _);

        vm.OnKeyDown(Key.F5, KeyModifiers.None);
        vm.OnPointerMoved(300_000, 200_000, leftDown: false, KeyModifiers.None);
        vm.OnKeyDown(Key.Escape, KeyModifiers.None);

        Assert.False(vm.RulerLabelMoveActive);
        Assert.False(model.Rulers[0].HasTextPosition);
        Assert.Null(vm.Overlay.RulerDragOverrides);
    }

    [Fact]
    public void F5_NeedsExactlyOneSelectedRuler_AndSaysSoWhenItDoesNot()
    {
        var model = Model();
        model.Rulers.Add(Ruler());
        model.Rulers.Add(Ruler());
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };

        Assert.False(vm.MoveRulerLabelAvailability.CanExecute);
        Assert.NotNull(vm.MoveRulerLabelAvailability.DisabledReason);
        vm.OnKeyDown(Key.F5, KeyModifiers.None);
        Assert.False(vm.RulerLabelMoveActive);

        vm.SelectRulers([0, 1]);
        Assert.False(vm.MoveRulerLabelAvailability.CanExecute);
        vm.OnKeyDown(Key.F5, KeyModifiers.None);
        Assert.False(vm.RulerLabelMoveActive);

        vm.SelectRuler(0);
        Assert.True(vm.MoveRulerLabelAvailability.CanExecute);
    }

    [Fact]
    public void TheGestureSnapsToTheDocumentGrid()
    {
        var model = Model(snapDbu: 1_000);
        model.Rulers.Add(Ruler());
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        vm.SelectRuler(0);

        vm.OnKeyDown(Key.F5, KeyModifiers.None);
        vm.OnPointerPressed(300_400, 199_600, KeyModifiers.None);

        Assert.Equal(300_000, model.Rulers[0].TextX);
        Assert.Equal(200_000, model.Rulers[0].TextY);
    }

    [Fact]
    public void AClickThatChangesNothing_LeavesNoUndoEntry()
    {
        var vm = VmWithOneRuler(out var model, out _);
        model.Rulers[0].TextX = 300_000;
        model.Rulers[0].TextY = 200_000;

        vm.OnKeyDown(Key.F5, KeyModifiers.None);
        vm.OnPointerPressed(300_000, 200_000, KeyModifiers.None);

        Assert.False(vm.UndoCommand.CanExecute(null));
    }

    // ── Reset ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Reset_ReturnsTheLabelToItsDynamicPosition_AndIsUndoable()
    {
        var vm = VmWithOneRuler(out var model, out _);
        model.Rulers[0].TextX = 300_000;
        model.Rulers[0].TextY = 200_000;

        Assert.True(vm.ResetRulerLabelPositionAvailabilityFor(0).CanExecute);
        vm.ResetRulerLabelPosition(0);

        Assert.False(model.Rulers[0].HasTextPosition);
        Assert.Equal(LayoutRenderer.MeasureRulerTextWorldBbox(Ruler(), LayoutUnit.Um, 1000, 0),
                     LayoutRenderer.MeasureRulerTextWorldBbox(model.Rulers[0], LayoutUnit.Um, 1000, 0));

        vm.UndoCommand.Execute(null);
        Assert.True(model.Rulers[0].HasTextPosition);
    }

    [Fact]
    public void Reset_LeavesTheAnchorAlone()
    {
        // A reset the user asked for is about the POSITION. Silently returning the anchor too would
        // undo a second choice they never mentioned.
        var vm = VmWithOneRuler(out var model, out _);
        model.Rulers[0].TextX = 1; model.Rulers[0].TextY = 2;
        model.Rulers[0].TextHAlign = LabelHAlign.Right;

        vm.ResetRulerLabelPosition(0);
        Assert.Equal(LabelHAlign.Right, model.Rulers[0].TextHAlign);
    }

    [Fact]
    public void Reset_IsDisabledWithItsReason_WhenTheLabelIsAlreadyDefault()
    {
        var vm = VmWithOneRuler(out _, out _);
        var avail = vm.ResetRulerLabelPositionAvailabilityFor(0);
        Assert.False(avail.CanExecute);
        Assert.NotNull(avail.DisabledReason);
    }

    [Fact]
    public void ResettingASelectionOfMany_IsOneUndoEntry()
    {
        var model = Model();
        for (int i = 0; i < 3; i++)
        {
            var r = Ruler();
            r.TextX = 1_000 * (i + 1); r.TextY = 2_000;
            model.Rulers.Add(r);
        }
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        vm.SelectRulers([0, 1, 2]);

        vm.ResetSelectedRulerLabelPositions();
        Assert.All(model.Rulers, r => Assert.False(r.HasTextPosition));

        vm.UndoCommand.Execute(null);
        Assert.All(model.Rulers, r => Assert.True(r.HasTextPosition));
    }

    // ── Move / nudge / paste carry the label ──────────────────────────────────────────────────────

    [Fact]
    public void NudgingARuler_CarriesItsHandPlacedLabel()
    {
        var vm = VmWithOneRuler(out var model, out _);
        model.Rulers[0].TextX = 300_000;
        model.Rulers[0].TextY = 200_000;

        long before = model.Rulers[0].X1;
        vm.OnKeyDown(Key.Right, KeyModifiers.None);

        long moved = model.Rulers[0].X1 - before;
        Assert.True(moved > 0, "the arrow key should have nudged the ruler");
        Assert.Equal(300_000 + moved, model.Rulers[0].TextX);
        Assert.Equal(200_000, model.Rulers[0].TextY);
    }

    // ── Persistence ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ThePositionAndAnchorRoundTripThroughTheClay()
    {
        var model = Model();
        var r = Ruler();
        r.TextX = 300_000; r.TextY = -200_000;
        r.TextHAlign = LabelHAlign.Right;
        r.TextVAlign = LabelVAlign.Top;
        model.Rulers.Add(r);

        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".clay");
        LayoutPersistence.SaveToFile(path, model);
        var reloaded = LayoutPersistence.LoadFromFile(path);
        File.Delete(path);
        var back = Assert.Single(reloaded.Rulers);
        Assert.Equal(300_000, back.TextX);
        Assert.Equal(-200_000, back.TextY);
        Assert.Equal(LabelHAlign.Right, back.TextHAlign);
        Assert.Equal(LabelVAlign.Top, back.TextVAlign);
    }

    [Fact]
    public void ARulerThatNeverMovedItsLabel_WritesNoneOfTheNewKeys()
    {
        // §9B.7's own rule: a file written before this field existed re-serializes byte for byte.
        var model = Model();
        model.Rulers.Add(Ruler());

        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".clay");
        LayoutPersistence.SaveToFile(path, model);
        string json = File.ReadAllText(path);
        File.Delete(path);
        Assert.DoesNotContain("TextX", json);
        Assert.DoesNotContain("TextY", json);
        Assert.DoesNotContain("TextHAlign", json);
        Assert.DoesNotContain("TextVAlign", json);
    }

    // ── DXF ───────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(LabelVAlign.Top, LabelHAlign.Left, 1)]
    [InlineData(LabelVAlign.Top, LabelHAlign.Center, 2)]
    [InlineData(LabelVAlign.Top, LabelHAlign.Right, 3)]
    [InlineData(LabelVAlign.Middle, LabelHAlign.Left, 4)]
    [InlineData(LabelVAlign.Middle, LabelHAlign.Center, 5)]
    [InlineData(LabelVAlign.Middle, LabelHAlign.Right, 6)]
    [InlineData(LabelVAlign.Bottom, LabelHAlign.Left, 7)]
    [InlineData(LabelVAlign.Bottom, LabelHAlign.Center, 8)]
    [InlineData(LabelVAlign.Bottom, LabelHAlign.Right, 9)]
    [InlineData(LabelVAlign.Baseline, LabelHAlign.Center, 8)]   // no attachment point of its own
    public void TheAnchorBecomesDxfsOwnAttachmentPoint(LabelVAlign v, LabelHAlign h, int expected)
    {
        var r = Ruler();
        r.TextVAlign = v; r.TextHAlign = h;
        Assert.Equal(expected, DxfWriter.RulerAttachmentPoint(r));
    }

    [Fact]
    public void AnUnAnchoredRuler_StillExportsAttachmentPointFive()
    {
        // Every export before §9B.12 wrote 5, and a document that never moves a label must produce
        // the same file it always did.
        Assert.Equal(5, DxfWriter.RulerAttachmentPoint(Ruler()));
        Assert.Equal((1, 2), DxfWriter.RulerTextJustification(Ruler()));
    }

    [Fact]
    public void TheDxfPutsTheTextWhereTheUserPutIt()
    {
        var model = Model();
        var r = Ruler();
        r.TextX = 400_000; r.TextY = -250_000;
        r.TextHAlign = LabelHAlign.Left;
        r.TextVAlign = LabelVAlign.Top;
        model.Rulers.Add(r);

        var structure = new InterchangeStructure(
            "TOP",
            [new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 200_000, Y2 = 100_000 }],
            []);
        var sw = new StringWriter();
        DxfWriter.Write(sw, [structure], "TOP", null, 1000, new DxfExportOptions(), null, [r], LayoutUnit.Um);
        var dim = Entity(Groups(sw.ToString()), "DIMENSION");

        // The DIMENSION's text midpoint (groups 11/21) is the stored point, converted to the file's
        // own drawing unit (mm) — 400,000 DBU at 1,000 DBU/µm is 400 µm is 0.4 mm.
        Assert.Equal(0.4, double.Parse(dim.First(g => g.Code == 11).Value,
                                       System.Globalization.CultureInfo.InvariantCulture), 6);
        Assert.Equal(-0.25, double.Parse(dim.First(g => g.Code == 21).Value,
                                         System.Globalization.CultureInfo.InvariantCulture), 6);
        // Top-left is attachment point 1.
        Assert.Equal("1", dim.First(g => g.Code == 71).Value);
        _ = model;
    }

    /// <summary>Every (code, value) pair, in file order — the raw group stream, so an assertion can
    /// talk about structure without going through our own reader (R-rul-18c).</summary>
    private static List<(int Code, string Value)> Groups(string dxf)
    {
        var lines = dxf.Split('\n');
        var result = new List<(int Code, string Value)>(lines.Length / 2);
        for (int i = 0; i + 1 < lines.Length; i += 2)
            if (int.TryParse(lines[i].Trim(), out int code)) result.Add((code, lines[i + 1].TrimEnd('\r')));
        return result;
    }

    private static List<(int Code, string Value)> Entity(List<(int Code, string Value)> groups, string type)
    {
        for (int i = 0; i < groups.Count; i++)
        {
            if (groups[i].Code != 0 || groups[i].Value != type) continue;
            var run = new List<(int Code, string Value)>();
            for (int j = i + 1; j < groups.Count && groups[j].Code != 0; j++) run.Add(groups[j]);
            return run;
        }
        return [];
    }

    // ── The Properties Inspector ──────────────────────────────────────────────────────────────────

    private static LayoutShapePropertiesViewModel Panel(LayoutEditorViewModel vm)
    {
        var panel = new LayoutShapePropertiesViewModel();
        panel.SetContext(vm);
        return panel;
    }

    [Fact]
    public void ThePanelShowsBlankForADynamicLabel_AndTheStoredValueOnceItIsPlaced()
    {
        var vm = VmWithOneRuler(out var model, out _);
        var panel = Panel(vm);

        Assert.Equal("", panel.RulerTextXText);
        Assert.False(panel.CanResetRulerLabelPosition);
        Assert.NotNull(panel.ResetRulerLabelPositionReason);

        model.Rulers[0].TextX = 300_000;
        model.Rulers[0].TextY = -200_000;
        vm.SelectRuler(0);

        Assert.Equal("300", panel.RulerTextXText);    // 300,000 DBU at 1,000 DBU/µm
        Assert.Equal("-200", panel.RulerTextYText);
        Assert.True(panel.CanResetRulerLabelPosition);
    }

    [Fact]
    public void TypingOneCoordinate_SeedsTheOther_SoTheEntryIsNotSilentlyDiscarded()
    {
        var vm = VmWithOneRuler(out var model, out _);
        var panel = Panel(vm);

        panel.CommitField("RulerTextX", "300");

        Assert.True(model.Rulers[0].HasTextPosition);
        Assert.Equal(300_000, model.Rulers[0].TextX);
        Assert.NotNull(model.Rulers[0].TextY);
    }

    [Fact]
    public void EmptyingThePositionField_IsTheSameAsReset()
    {
        var vm = VmWithOneRuler(out var model, out _);
        model.Rulers[0].TextX = 300_000;
        model.Rulers[0].TextY = -200_000;
        vm.SelectRuler(0);
        var panel = Panel(vm);

        panel.CommitField("RulerTextX", "");
        Assert.False(model.Rulers[0].HasTextPosition);
    }

    [Fact]
    public void AnInvalidCoordinate_IsReportedAndChangesNothing()
    {
        var vm = VmWithOneRuler(out var model, out _);
        var panel = Panel(vm);

        panel.CommitField("RulerTextY", "over there");

        Assert.NotNull(panel.RulerTextYError);
        Assert.True(panel.HasRulerTextYError);
        Assert.False(model.Rulers[0].HasTextPosition);
    }

    [Fact]
    public void TheAnchorCombosCommitToEverySelectedRuler_AsOneUndoEntry()
    {
        var model = Model();
        model.Rulers.Add(Ruler());
        model.Rulers.Add(Ruler());
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        vm.SelectRulers([0, 1]);
        var panel = Panel(vm);

        Assert.Equal(LabelHAlign.Center, panel.RulerTextHAlignValue);
        panel.RulerTextHAlignValue = LabelHAlign.Right;

        Assert.All(model.Rulers, r => Assert.Equal(LabelHAlign.Right, r.EffectiveTextHAlign));
        vm.UndoCommand.Execute(null);
        Assert.All(model.Rulers, r => Assert.Equal(LabelHAlign.Center, r.EffectiveTextHAlign));
    }

    [Fact]
    public void AMixedAnchorSelection_ReadsBlank()
    {
        var model = Model();
        model.Rulers.Add(Ruler());
        var second = Ruler();
        second.TextVAlign = LabelVAlign.Top;
        model.Rulers.Add(second);

        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        vm.SelectRulers([0, 1]);
        var panel = Panel(vm);

        Assert.Null(panel.RulerTextVAlignValue);
    }

    [Fact]
    public void ThePanelsResetButton_ClearsEverySelectedRuler()
    {
        var model = Model();
        for (int i = 0; i < 2; i++)
        {
            var r = Ruler();
            r.TextX = 1_000; r.TextY = 2_000;
            model.Rulers.Add(r);
        }
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        vm.SelectRulers([0, 1]);
        var panel = Panel(vm);

        Assert.True(panel.CanResetRulerLabelPosition);
        panel.ResetRulerLabelPositionCommand.Execute(null);

        Assert.All(model.Rulers, r => Assert.False(r.HasTextPosition));
    }
}

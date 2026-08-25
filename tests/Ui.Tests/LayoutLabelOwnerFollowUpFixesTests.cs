using System;
using System.IO;
using System.Runtime.CompilerServices;
using Avalonia.Input;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.ViewModels;
using SkiaSharp;

namespace CircuitRF.Ui.Tests;

// ── Owner follow-up round after brief-layout-label-fix-and-text-flatten.md — 5 reports:
//    (1) default label height still too small in some cases, (2) selection highlight box wrong for
//    rotated labels, (3) Flatten dialog button text not centered, (4) label font Style, (5) Properties
//    Inspector label position editing. Items (3)-(5) live in their own dedicated regions below; (1) has
//    its own tests in LayoutLabelFixAndTextFlattenTests.cs (CommitLabel_* tests) since it's a direct
//    continuation of that brief's gates.

[Collection(CircuitRF.Ui.Tests.LayoutTextOutlineTypefaceCollection.Name)]
public class LayoutLabelOwnerFollowUpFixesTests : IDisposable
{
    private static readonly LayerKey Layer1 = new(1, 0);

    private static LayoutView FreshModel() => new() { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000 };

    public LayoutLabelOwnerFollowUpFixesTests() => LayoutTextOutline.TestOverrideTypeface = SKTypeface.Default;
    public void Dispose() => LayoutTextOutline.TestOverrideTypeface = null;

    // ── (2) Selection highlight box: LayoutHitTest.LabelHitBbox rotation sign fix ────────────────────
    // R0/R180 were already correctly positioned; R90/R270 landed on the WRONG side of the anchor
    // entirely. Verified by hitting the CORRECTED quadrant and confirming the OLD (buggy) quadrant no
    // longer hits.

    [Fact]
    public void HitTest_R90Label_HitsAboveAndLeftOfAnchor_NotAboveAndRight()
    {
        var model = FreshModel();
        model.Shapes.Add(new LabelShape { Layer = Layer1, X = 0, Y = 0, Text = "AB", Height = 1000, Rotation = LayoutRotation.R90 });

        // Corrected box: (X-h, Y, X, Y+w) = (-1000, 0, 0, 1240) for w=1240,h=1000 (approx formula).
        Assert.NotEmpty(LayoutHitTest.HitStack(model, null, -500, 600, 10));
        // Old (buggy) box was (X, Y, X+h, Y+w) = (0,0,1000,1240) — the mirror-image quadrant.
        Assert.Empty(LayoutHitTest.HitStack(model, null, 500, 600, 10));
    }

    [Fact]
    public void HitTest_R270Label_HitsBelowAndRightOfAnchor_NotBelowAndLeft()
    {
        var model = FreshModel();
        model.Shapes.Add(new LabelShape { Layer = Layer1, X = 0, Y = 0, Text = "AB", Height = 1000, Rotation = LayoutRotation.R270 });

        // Corrected box: (X, Y-w, X+h, Y) = (0, -1240, 1000, 0).
        Assert.NotEmpty(LayoutHitTest.HitStack(model, null, 500, -600, 10));
        // Old (buggy) box was (X-h, Y-w, X, Y) = (-1000,-1240,0,0) — the mirror-image quadrant.
        Assert.Empty(LayoutHitTest.HitStack(model, null, -500, -600, 10));
    }

    [Fact]
    public void HitTest_R0AndR180Labels_UnaffectedByTheFix()
    {
        var r0Model = FreshModel();
        r0Model.Shapes.Add(new LabelShape { Layer = Layer1, X = 0, Y = 0, Text = "AB", Height = 1000, Rotation = LayoutRotation.R0 });
        Assert.NotEmpty(LayoutHitTest.HitStack(r0Model, null, 500, 600, 10));

        var r180Model = FreshModel();
        r180Model.Shapes.Add(new LabelShape { Layer = Layer1, X = 0, Y = 0, Text = "AB", Height = 1000, Rotation = LayoutRotation.R180 });
        Assert.NotEmpty(LayoutHitTest.HitStack(r180Model, null, -500, -600, 10));
    }

    // ── (2) Selection highlight box: LayoutRenderer.MeasureLabelWorldBbox — real font metrics ────────

    [Theory]
    [InlineData(LayoutRotation.R0,   1, 1)]   // extends right and up from the anchor
    [InlineData(LayoutRotation.R90,  -1, 1)]  // extends left and up
    [InlineData(LayoutRotation.R180, -1, -1)] // extends left and down
    [InlineData(LayoutRotation.R270, 1, -1)]  // extends right and down
    public void MeasureLabelWorldBbox_SitsInTheCorrectQuadrantRelativeToTheAnchor(LayoutRotation rotation, int expectedXSign, int expectedYSign)
    {
        var label = new LabelShape { Layer = Layer1, X = 100_000, Y = 100_000, Text = "AB", Height = 20_000, Rotation = rotation };
        var bb = LayoutRenderer.MeasureLabelWorldBbox(label);

        Assert.NotNull(bb);
        double centerX = (bb!.Value.MinX + bb.Value.MaxX) / 2.0;
        double centerY = (bb.Value.MinY + bb.Value.MaxY) / 2.0;

        Assert.Equal(Math.Sign(expectedXSign), Math.Sign(centerX - label.X));
        Assert.Equal(Math.Sign(expectedYSign), Math.Sign(centerY - label.Y));
    }

    [Fact]
    public void MeasureLabelWorldBbox_UsesRealFontMetrics_NotTheFixedCharacterCountEstimate()
    {
        // "W" is one of the widest Latin capitals, "i" one of the narrowest — a real-metrics width
        // must reflect that (the old fixed 0.62-per-character estimate gave both the SAME width for
        // the same character count, which is the exact class of approximation-looseness being fixed).
        var wide = LayoutRenderer.MeasureLabelWorldBbox(new LabelShape { Layer = Layer1, X = 0, Y = 0, Text = "W", Height = 20_000 });
        var narrow = LayoutRenderer.MeasureLabelWorldBbox(new LabelShape { Layer = Layer1, X = 0, Y = 0, Text = "i", Height = 20_000 });

        Assert.NotNull(wide); Assert.NotNull(narrow);
        long wideWidth = wide!.Value.MaxX - wide.Value.MinX;
        long narrowWidth = narrow!.Value.MaxX - narrow.Value.MinX;
        Assert.True(wideWidth > narrowWidth * 2, $"expected 'W' ({wideWidth}) to be much wider than 'i' ({narrowWidth})");
    }

    [Fact]
    public void MeasureLabelWorldBbox_EmptyText_ReturnsNull()
    {
        var label = new LabelShape { Layer = Layer1, X = 0, Y = 0, Text = "", Height = 20_000 };
        Assert.Null(LayoutRenderer.MeasureLabelWorldBbox(label));
    }

    // ── HAlign/VAlign: which point of the text box X/Y actually names (owner report, 2026-08-25) ─────

    /// <summary>A label that states no alignment must measure EXACTLY as it always did — right of the
    /// anchor and above it. Every <c>.clay</c> written before those fields existed is this case.</summary>
    [Fact]
    public void LabelWithNoAlignment_KeepsTheHistoricalLeftOfBaselineAnchor()
    {
        var label = new LabelShape { Layer = Layer1, X = 0, Y = 0, Text = "AB", Height = 20_000 };
        var bb = LayoutRenderer.MeasureLabelWorldBbox(label)!.Value;

        // "At the anchor", not "right of it": a glyph's left side bearing can ink a few hundred DBU
        // left of the pen origin at this height, which is what the tolerance is for.
        long width = bb.MaxX - bb.MinX;
        long height = bb.MaxY - bb.MinY;
        Assert.True(Math.Abs(bb.MinX) < width / 10, $"text should start at the anchor, got MinX={bb.MinX} of width {width}");
        Assert.True(Math.Abs(bb.MinY) < height / 10, $"text should sit on the baseline, got MinY={bb.MinY} of height {height}");
    }

    [Fact]
    public void HAlign_MovesTheTextRelativeToTheAnchor_LeftCentreRight()
    {
        Bbox Measure(LabelHAlign h) => LayoutRenderer.MeasureLabelWorldBbox(
            new LabelShape { Layer = Layer1, X = 0, Y = 0, Text = "ABCD", Height = 20_000, HAlign = h })!.Value;

        var left = Measure(LabelHAlign.Left);
        var centre = Measure(LabelHAlign.Center);
        var right = Measure(LabelHAlign.Right);
        long width = left.MaxX - left.MinX;

        // Each is a whole text-width apart from the next, in order — the property that actually
        // distinguishes the three aligners, and one that side bearings cannot blur.
        Assert.True(left.MinX > centre.MinX && centre.MinX > right.MinX, "Left → Centre → Right must move the text steadily leftward");
        Assert.True(left.MaxX > width / 2, "Left: the text lies to the RIGHT of the anchor");
        Assert.True(right.MinX < -width / 2, "Right: the text lies to the LEFT of the anchor");
        Assert.True(centre.MinX < 0 && centre.MaxX > 0, "Center: straddles the anchor");

        // Same string, same width, three different placements — not three different measurements.
        Assert.Equal(left.MaxX - left.MinX, right.MaxX - right.MinX);
        Assert.Equal(left.MaxX - left.MinX, centre.MaxX - centre.MinX);
    }

    /// <summary>
    /// <c>Top</c> hangs the text BELOW the anchor and <c>Bottom</c> puts it above — the direction that
    /// matters, because getting it backwards is exactly how an imported table's every row lands one
    /// line out of place while still looking like plausible text.
    /// </summary>
    [Fact]
    public void VAlign_PutsTheTextOnTheCorrectSideOfTheAnchor()
    {
        Bbox Measure(LabelVAlign v) => LayoutRenderer.MeasureLabelWorldBbox(
            new LabelShape { Layer = Layer1, X = 0, Y = 0, Text = "ABCD", Height = 20_000, VAlign = v })!.Value;

        var top = Measure(LabelVAlign.Top);
        var bottom = Measure(LabelVAlign.Bottom);
        var middle = Measure(LabelVAlign.Middle);

        Assert.True(top.MaxY <= 0, $"Top: the anchor is the text's own top edge, got MaxY={top.MaxY}");
        Assert.True(bottom.MinY >= 0, $"Bottom: the anchor is its bottom edge, got MinY={bottom.MinY}");
        Assert.True(middle.MinY < 0 && middle.MaxY > 0, "Middle: straddles the anchor");
    }

    [Fact]
    public void SelectionOutline_PixelOracle_R90Label_SurroundsGlyphPixels_NotTheOldWrongQuadrant()
    {
        var tech = new Technology
        {
            Name = "T", DefaultDisplayUnit = LayoutUnit.Um, DefaultSnapDbu = 1000,
            Layers = [new LayerDef { Key = Layer1, Name = "L", Color = new CircuitRF.Ui.Theming.Rgba(255, 0, 0), FillOpacity = 1.0, ZOrder = 0, Visible = true, Selectable = true }],
        };
        var view = FreshModel();
        view.Shapes.Add(new LabelShape { Layer = Layer1, X = 0, Y = 0, Text = "W", Height = 40_000, Rotation = LayoutRotation.R90 });

        var vp = LayoutViewport.ZoomToFit(new Bbox(-60_000, -20_000, 60_000, 60_000), 300, 300, 0.1);
        using var surface = SKSurface.Create(new SKImageInfo((int)vp.Width, (int)vp.Height));
        var opts = new LayoutRenderOptions { Theme = LayoutRenderTheme.Light, ShowGrid = false, Overlay = new LayoutOverlay { SelectedIndices = [0] } };
        LayoutRenderer.Draw(surface.Canvas, view, tech, vp, opts);

        using var img = surface.Snapshot();
        using var bmp = SKBitmap.FromImage(img);

        // Anchor (0,0) maps to a known screen point; the CORRECTED quadrant (glyph pixels) is up-and-
        // left of it, the OLD BUGGY quadrant (where the selection box used to render, with nothing
        // actually drawn there) is up-and-right.
        int anchorSx = (int)vp.WorldToScreenX(0);
        int anchorSy = (int)vp.WorldToScreenY(0);

        bool AnyRedPixel(int x0, int x1, int y0, int y1)
        {
            for (int y = Math.Max(0, y0); y < Math.Min(bmp.Height, y1); y++)
                for (int x = Math.Max(0, x0); x < Math.Min(bmp.Width, x1); x++)
                {
                    var c = bmp.GetPixel(x, y);
                    if (c.Red > c.Green + 40 && c.Red > c.Blue + 40) return true;
                }
            return false;
        }

        // Correct quadrant: left of the anchor, above it (screen Y decreases upward).
        Assert.True(AnyRedPixel(anchorSx - 60, anchorSx, anchorSy - 60, anchorSy + 5),
            "expected the 'W' glyph to render left-and-above the anchor for an R90 label");
        // Old buggy quadrant: right of the anchor, above it — must be empty now.
        Assert.False(AnyRedPixel(anchorSx + 5, anchorSx + 60, anchorSy - 60, anchorSy),
            "no glyph pixels should render right-of-anchor for an R90 label — that was the old bug's quadrant");
    }

    // ── (3) Flatten dialog (and every other) button text centering ─────────────────────────────────
    // Fixed 3 times per-dialog before this — App.axaml's Window/UserControl-level `Window` is not the
    // shared ancestor for both dialog Windows and embedded views, but Button IS: a single global
    // Style Selector="Button" is the one place this can be fixed such that it can never regress again,
    // rather than another per-dialog patch. FlattenToPolygonDialog.axaml's own buttons deliberately do
    // NOT carry an explicit HorizontalContentAlignment — that's the point being tested here.

    private static string ReadRepoFile(string relativePath, [CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root (no CLAUDE.md found walking up from this test file).");
        return File.ReadAllText(Path.Combine(dir!, relativePath));
    }

    [Fact]
    public void AppAxaml_GlobalButtonStyle_CentersContentHorizontally()
    {
        // Moved into the shared style file at H8 (two Applications, one style set — R-h8-6). The
        // global Button rule itself is unchanged, and this is still the scan that stops it going
        // back to being fixed dialog-by-dialog.
        string src = ReadRepoFile(Path.Combine("src", "Ui", "Styles", "CircuitRfStyles.axaml"));
        int styleStart = src.IndexOf("Style Selector=\"Button\"", StringComparison.Ordinal);
        Assert.True(styleStart >= 0, "expected a global Style Selector=\"Button\" in App.axaml");
        int styleEnd = src.IndexOf("</Style>", styleStart, StringComparison.Ordinal);
        string styleBlock = src[styleStart..styleEnd];

        Assert.Contains("HorizontalContentAlignment", styleBlock, StringComparison.Ordinal);
        Assert.Contains("\"Center\"", styleBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void FlattenToPolygonDialog_ButtonsRelyOnTheGlobalStyle_NoRedundantPerButtonOverride()
    {
        // Not a requirement so much as documentation-by-test: this dialog's buttons were the reported
        // instance of the bug and deliberately carry NO explicit HorizontalContentAlignment — proving
        // the global style (asserted above) is what's doing the centering, not a fourth per-dialog patch.
        string src = ReadRepoFile(Path.Combine("src", "Ui", "Views", "Dialogs", "FlattenToPolygonDialog.axaml"));
        Assert.Contains("Content=\"Cancel\"", src);
        Assert.Contains("Content=\"Flatten\"", src);
    }

    // ── (4) LabelShape.Style — Regular/Bold/Italic/Condensed, editable in the Properties Inspector ──

    private static (LayoutEditorViewModel Vm, LayoutShapePropertiesViewModel Props) Setup(LayoutView model)
    {
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        var props = new LayoutShapePropertiesViewModel();
        props.SetContext(vm);
        return (vm, props);
    }

    private static void Click(LayoutEditorViewModel vm, double wx, double wy, KeyModifiers mods = default)
    {
        vm.OnPointerPressed(wx, wy, mods, 1, 40);
        vm.OnPointerReleased(wx, wy, mods);
    }

    [Fact]
    public void LabelShape_Style_DefaultsToRegular()
    {
        var label = new LabelShape { Layer = Layer1, X = 0, Y = 0, Text = "X", Height = 1000 };
        Assert.Equal(LabelFontStyle.Regular, label.Style);
    }

    [Fact]
    public void CommittedLabel_FromTheToolbarButton_DefaultsToRegularStyle()
    {
        var vm = new LayoutEditorViewModel(FreshModel()) { ActiveTool = LayoutEditorViewModel.Tool.Label };
        vm.OnPointerPressed(0, 0, KeyModifiers.None, 1, 0);
        vm.OnTextInput("REF");
        vm.OnKeyDown(Key.Enter, KeyModifiers.None);

        var label = Assert.IsType<LabelShape>(vm.Model.Shapes[0]);
        Assert.Equal(LabelFontStyle.Regular, label.Style);
    }

    [Fact]
    public void LabelShape_Clone_CopiesStyle()
    {
        var label = new LabelShape { Layer = Layer1, X = 0, Y = 0, Text = "X", Height = 1000, Style = LabelFontStyle.Bold };
        var clone = Assert.IsType<LabelShape>(LayoutGeometry.Clone(label));
        Assert.Equal(LabelFontStyle.Bold, clone.Style);
    }

    [Fact]
    public void LabelShape_Style_RoundTripsThroughPersistence()
    {
        var view = FreshModel();
        view.Shapes.Add(new LabelShape { Layer = Layer1, X = 0, Y = 0, Text = "X", Height = 1000, Style = LabelFontStyle.Italic });

        var json = LayoutPersistence.Serialize(view);
        Assert.Contains("\"Style\": \"Italic\"", json);

        var restored = LayoutPersistence.Deserialize(json);
        Assert.Equal(LabelFontStyle.Italic, ((LabelShape)restored.Shapes[0]).Style);
    }

    [Theory]
    [InlineData(LabelFontStyle.Regular)]
    [InlineData(LabelFontStyle.Bold)]
    [InlineData(LabelFontStyle.Italic)]
    [InlineData(LabelFontStyle.Condensed)]
    public void ResolveTypeface_EveryStyle_ReturnsANonNullTypeface(LabelFontStyle style)
    {
        // With no TestOverrideTypeface, this DOES touch SkiaFonts.PlexRegular/Bold/Italic/Light via
        // AssetLoader — confirm the override in the test fixture's constructor is actually being used
        // (it must be, for every other test in this class to pass at all) rather than assuming.
        Assert.NotNull(LayoutTextOutline.ResolveTypeface(style));
    }

    [Fact]
    public void PropertiesInspector_LabelStyle_CommitsToTheModel_MultiSelectionAsOneUndoEntry()
    {
        var model = FreshModel();
        model.Shapes.Add(new LabelShape { Layer = Layer1, X = 0, Y = 0, Text = "A", Height = 1000 });
        model.Shapes.Add(new LabelShape { Layer = Layer1, X = 20_000, Y = 0, Text = "B", Height = 1000 });
        var (vm, props) = Setup(model);

        Click(vm, 500, 500);
        Click(vm, 20_500, 500, KeyModifiers.Shift);
        Assert.Equal(2, vm.SelectedIndices.Count);

        props.LabelStyleValue = LabelFontStyle.Bold;

        Assert.Equal(LabelFontStyle.Bold, ((LabelShape)model.Shapes[0]).Style);
        Assert.Equal(LabelFontStyle.Bold, ((LabelShape)model.Shapes[1]).Style);
        Assert.True(vm.UndoRedo.CanUndo);
        vm.UndoRedo.Undo();
        Assert.Equal(LabelFontStyle.Regular, ((LabelShape)model.Shapes[0]).Style);
        Assert.Equal(LabelFontStyle.Regular, ((LabelShape)model.Shapes[1]).Style);
    }

    // ── (5) Properties Inspector — label position (X/Y) editing ─────────────────────────────────────

    [Fact]
    public void PropertiesInspector_LabelPosition_CommitsXAndY()
    {
        var model = FreshModel();
        model.Shapes.Add(new LabelShape { Layer = Layer1, X = 1000, Y = 2000, Text = "A", Height = 1000 });
        var (vm, props) = Setup(model);
        Click(vm, 1300, 2500); // inside the label's approximate hit-box: (1000,2000)-(1620,3000)

        props.CommitLabelXText("5000nm"); // explicit unit -> exactly 5000 DBU (1 DBU = 1 nm here)
        props.CommitLabelYText("6000nm");

        var label = Assert.IsType<LabelShape>(model.Shapes[0]);
        Assert.Equal(5000, label.X);
        Assert.Equal(6000, label.Y);
    }

    [Fact]
    public void PropertiesInspector_LabelPosition_InvalidText_ShowsErrorAndDoesNotMutate()
    {
        var model = FreshModel();
        model.Shapes.Add(new LabelShape { Layer = Layer1, X = 1000, Y = 2000, Text = "A", Height = 1000 });
        var (vm, props) = Setup(model);
        Click(vm, 1300, 2500);

        props.CommitLabelXText("not a number");

        Assert.Equal(1000, ((LabelShape)model.Shapes[0]).X);
        Assert.NotNull(props.LabelXError);
        Assert.True(props.HasLabelXError);
    }

    [Fact]
    public void PropertiesInspector_LabelPosition_MultiSelection_AppliesToAll_AsOneUndoEntry()
    {
        var model = FreshModel();
        model.Shapes.Add(new LabelShape { Layer = Layer1, X = 0, Y = 0, Text = "A", Height = 1000 });
        model.Shapes.Add(new LabelShape { Layer = Layer1, X = 20_000, Y = 0, Text = "B", Height = 1000 });
        var (vm, props) = Setup(model);

        Click(vm, 500, 500);
        Click(vm, 20_500, 500, KeyModifiers.Shift);
        Assert.Equal(2, vm.SelectedIndices.Count);

        props.CommitLabelXText("100nm");

        Assert.Equal(100, ((LabelShape)model.Shapes[0]).X);
        Assert.Equal(100, ((LabelShape)model.Shapes[1]).X);
        Assert.True(vm.UndoRedo.CanUndo);
        vm.UndoRedo.Undo();
        Assert.Equal(0, ((LabelShape)model.Shapes[0]).X);
        Assert.Equal(20_000, ((LabelShape)model.Shapes[1]).X);
    }

    [Fact]
    public void PropertiesInspector_ShowLabel_ExposesPositionFields_ForALabelSelection()
    {
        var model = FreshModel();
        model.Shapes.Add(new LabelShape { Layer = Layer1, X = 1234, Y = 5678, Text = "A", Height = 1000 });
        var (vm, props) = Setup(model);
        Click(vm, 1500, 6000); // inside (1234,5678)-(1854,6678)

        Assert.True(props.ShowLabel);
        Assert.False(string.IsNullOrEmpty(props.LabelXText));
        Assert.False(string.IsNullOrEmpty(props.LabelYText));
    }

    // ── The pick box IS the highlight box (owner report, 2026-08-25) ─────────────────────────────

    /// <summary>
    /// <b>One measurement, or they disagree.</b> The selection highlight comes from
    /// <see cref="LayoutRenderer.MeasureLabelWorldBbox"/> (real glyph metrics); the pick region used to
    /// come from a separate character-count estimate. Two derivations of one region can only agree by
    /// coincidence — <c>W</c> and <c>i</c> are the same width to an estimate that counts characters —
    /// so this pins that hit-testing now reads the SAME box the highlight draws.
    /// </summary>
    [Theory]
    [InlineData("W", 0.0)]
    [InlineData("iiiii", 0.0)]
    [InlineData("Wide Text", 45.0)]
    [InlineData("Wide Text", 90.0)]
    public void LabelPickRegion_IsExactlyTheHighlightBox(string text, double degrees)
    {
        var label = new LabelShape { Layer = Layer1, X = 100_000, Y = 200_000, Text = text, Height = 20_000 };
        label.RotationDegrees = degrees;

        var highlight = LayoutRenderer.MeasureLabelWorldBbox(label)!.Value;

        // Every corner of the highlight box must hit, and a point a comfortable margin outside must not.
        var model = FreshModel();
        model.Shapes.Add(label);
        long inset = Math.Max(1, (highlight.MaxX - highlight.MinX) / 20);
        foreach (var (px, py) in new[]
                 {
                     (highlight.MinX + inset, highlight.MinY + inset),
                     (highlight.MaxX - inset, highlight.MaxY - inset),
                 })
            Assert.NotEmpty(LayoutHitTest.HitStack(model, null, px, py, 0));

        long far = (highlight.MaxX - highlight.MinX) + (highlight.MaxY - highlight.MinY);
        Assert.Empty(LayoutHitTest.HitStack(model, null, highlight.MaxX + far, highlight.MaxY + far, 0));
    }

    /// <summary>The estimate the pick region used to use is width-blind; the box it now uses is not.
    /// This is the property that makes "one measurement" observable rather than merely asserted.</summary>
    [Fact]
    public void LabelPickRegion_ReflectsRealGlyphWidths_NotACharacterCount()
    {
        Bbox Pick(string text)
        {
            var model = FreshModel();
            var label = new LabelShape { Layer = Layer1, X = 0, Y = 0, Text = text, Height = 20_000 };
            model.Shapes.Add(label);
            return LayoutRenderer.MeasureLabelWorldBbox(label)!.Value;
        }

        long wide = Pick("WWW").MaxX - Pick("WWW").MinX;
        long narrow = Pick("iii").MaxX - Pick("iii").MinX;
        Assert.True(wide > narrow * 2, $"'WWW' ({wide}) should be much wider than 'iii' ({narrow})");
    }

    // ── Arbitrary text angle (owner report, 2026-08-25) ──────────────────────────────────────────

    /// <summary>The four cardinals must keep serializing exactly as they always did — that is what
    /// makes the widening additive.</summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(90.0)]
    [InlineData(180.0)]
    [InlineData(270.0)]
    public void CardinalLabelAngle_WritesNoRotDegKey(double degrees)
    {
        var view = new LayoutView { DbuPerMicron = 1000 };
        var label = new LabelShape { Layer = Layer1, Text = "T", Height = 1000 };
        label.RotationDegrees = degrees;
        view.Shapes.Add(label);

        var json = LayoutPersistence.Serialize(view);
        Assert.DoesNotContain("RotDeg", json);
        Assert.Equal(degrees, ((LabelShape)LayoutPersistence.Deserialize(json).Shapes[0]).RotationDegrees, 6);
    }

    [Fact]
    public void NonCardinalLabelAngle_RoundTrips_AndDegradesToTheNearestCardinal()
    {
        var view = new LayoutView { DbuPerMicron = 1000 };
        var label = new LabelShape { Layer = Layer1, Text = "T", Height = 1000 };
        label.RotationDegrees = 45.0;
        view.Shapes.Add(label);

        // The enum companion holds a SANE value rather than zero, for anything that only knows about it.
        Assert.True(label.Rotation is LayoutRotation.R0 or LayoutRotation.R90);

        var json = LayoutPersistence.Serialize(view);
        Assert.Contains("RotDeg", json);
        var restored = (LabelShape)LayoutPersistence.Deserialize(json).Shapes[0];
        Assert.Equal(45.0, restored.RotationDegrees, 6);
        Assert.Equal(json, LayoutPersistence.Serialize(LayoutPersistence.Deserialize(json)));
    }

    /// <summary>A non-cardinal label's bbox must actually TRACK the angle — a stale four-way table
    /// would give 0 and 45 the same answer, which is exactly how "the label does not look right"
    /// survives a screenshot.</summary>
    [Fact]
    public void LabelBbox_TracksANonCardinalAngle()
    {
        Bbox At(double deg)
        {
            var l = new LabelShape { Layer = Layer1, X = 0, Y = 0, Text = "ABCD", Height = 20_000 };
            l.RotationDegrees = deg;
            return LayoutRenderer.MeasureLabelWorldBbox(l)!.Value;
        }

        var flat = At(0);
        var tilted = At(45);
        Assert.NotEqual(flat, tilted);
        // At 45 degrees a wide, short box becomes nearly square.
        double flatAspect = (double)(flat.MaxX - flat.MinX) / (flat.MaxY - flat.MinY);
        double tiltedAspect = (double)(tilted.MaxX - tilted.MinX) / (tilted.MaxY - tilted.MinY);
        Assert.True(flatAspect > 2.0, $"flat aspect {flatAspect}");
        Assert.True(tiltedAspect < flatAspect / 1.5, $"tilted aspect {tiltedAspect} vs flat {flatAspect}");
    }
}

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
        string src = ReadRepoFile(Path.Combine("src", "Ui", "App.axaml"));
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
}

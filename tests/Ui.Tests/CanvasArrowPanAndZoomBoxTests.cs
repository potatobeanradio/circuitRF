using System;
using System.IO;
using System.Text.RegularExpressions;
using Avalonia.Input;
using CircuitRF.Ui.Controls;
using CircuitRF.Ui.DataDisplay.ViewModels;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// <b>Two gestures a trackpad needs</b> (owner, 2026-09-11).
///
/// <list type="number">
/// <item><b>Arrow keys pan every document that pans on the middle mouse button</b> — a trackpad has
///   no middle button, and two-finger scroll is spent on zoom in all five of these canvases. Only
///   when nothing is selected: the arrow-key NUDGE is the older gesture and keeps the key whenever
///   it has something to move.</item>
/// <item><b>The Zoom In toolbar button became a magnifier that ARMS.</b> Clicking it no longer zooms
///   — it takes the left button for one drag, and the box that drag draws is what gets framed.
///   Escape disarms and hands the button back to Select. Ctrl/Cmd +/- is where a single step
///   lives.</item>
/// </list>
///
/// <para>The arithmetic and the view-model state are exercised directly. The canvas WIRING is
/// source-scanned, for the reason <c>ZoomToFitShortcutTests</c> already records: no
/// <c>UserControl</c> can be constructed headlessly in this project, so a real <c>KeyEventArgs</c>
/// cannot be raised.</para>
/// </summary>
public class CanvasArrowPanAndZoomBoxTests
{
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "circuitrf.slnx")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return dir!;
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine([RepoRoot(), .. parts]));

    /// <summary>Source with comments stripped — a comment naming a symbol must not pass for a
    /// binding, which is the one thing a scan like this can be fooled by.</summary>
    private static string CodeOf(params string[] parts)
    {
        var text = Read(parts);
        text = Regex.Replace(text, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        return Regex.Replace(text, @"//[^\n]*", " ");
    }

    // ── The step itself ───────────────────────────────────────────────────────

    /// <summary>Right is +X and Down is +Y, in SCREEN pixels — the convention every canvas converts
    /// from, negating Y where its own world points the other way.</summary>
    [Theory]
    [InlineData(Key.Left,  -CanvasArrowPan.StepPixels, 0.0)]
    [InlineData(Key.Right,  CanvasArrowPan.StepPixels, 0.0)]
    [InlineData(Key.Up,    0.0, -CanvasArrowPan.StepPixels)]
    [InlineData(Key.Down,  0.0,  CanvasArrowPan.StepPixels)]
    public void AnArrowKey_StepsTheViewInItsOwnDirection(Key key, double dx, double dy)
    {
        var step = CanvasArrowPan.ScreenStep(key, KeyModifiers.None);

        Assert.NotNull(step);
        Assert.Equal(dx, step!.Value.Dx, 9);
        Assert.Equal(dy, step.Value.Dy, 9);
    }

    /// <summary>Shift is the coarse step, the same spelling the nudge gestures use.</summary>
    [Fact]
    public void Shift_MultipliesTheStep()
    {
        var step = CanvasArrowPan.ScreenStep(Key.Right, KeyModifiers.Shift);

        Assert.NotNull(step);
        Assert.Equal(CanvasArrowPan.StepPixels * CanvasArrowPan.CoarseMultiplier, step!.Value.Dx, 9);
    }

    /// <summary>
    /// Ctrl/Cmd/Alt arrows are DECLINED, not panned. Those combinations belong to whatever else has
    /// claimed them; quietly panning under one would make an unrelated shortcut scroll the page.
    /// </summary>
    [Theory]
    [InlineData(KeyModifiers.Control)]
    [InlineData(KeyModifiers.Meta)]
    [InlineData(KeyModifiers.Alt)]
    public void AModifiedArrow_IsNotAPan(KeyModifiers modifiers)
        => Assert.Null(CanvasArrowPan.ScreenStep(Key.Right, modifiers));

    /// <summary>Anything that is not an arrow key is not a pan.</summary>
    [Theory]
    [InlineData(Key.A)]
    [InlineData(Key.Escape)]
    [InlineData(Key.Space)]
    public void ANonArrowKey_IsNotAPan(Key key)
        => Assert.Null(CanvasArrowPan.ScreenStep(key, KeyModifiers.None));

    // ── Every pannable document asks first, and pans in its own sense ─────────

    /// <summary>
    /// All five canvases that pan on the middle button also pan on the arrows. Named individually
    /// rather than globbed: a canvas that gains a middle-button pan and forgets the keyboard one is
    /// exactly what this is here to catch, and it can only be caught by a list somebody has to add
    /// to.
    /// </summary>
    [Theory]
    [InlineData("src/Ui/Controls/SchematicCanvas.cs")]
    [InlineData("src/Ui/Controls/SymbolEditorCanvas.cs")]
    [InlineData("src/Ui/Controls/LayoutCanvas.cs")]
    [InlineData("src/Ui/Controls/WBondProfileCanvas.cs")]
    [InlineData("src/Ui/Views/DataDisplay/PlotCanvasView.axaml.cs")]
    public void EveryMiddleButtonPanningCanvas_AlsoPansOnTheArrowKeys(string path)
    {
        var code = CodeOf(path.Split('/'));

        Assert.Contains("IsMiddleButtonPressed", code, StringComparison.Ordinal);
        Assert.Contains("CanvasArrowPan.ScreenStep", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// …and every one of them gates the pan on an EMPTY selection, which is the whole rule. Each
    /// asks its own view model in its own vocabulary, so the assertion is that the guard names one
    /// of them — not that they agree on a spelling they have no reason to share.
    /// </summary>
    [Theory]
    [InlineData("src/Ui/Controls/SchematicCanvas.cs",                  "Selection.IsEmpty")]
    [InlineData("src/Ui/Controls/SymbolEditorCanvas.cs",               "HasSelection")]
    [InlineData("src/Ui/Controls/LayoutCanvas.cs",                     "HasSelection")]
    [InlineData("src/Ui/Controls/WBondProfileCanvas.cs",               "Selection.IsEmpty")]
    [InlineData("src/Ui/Views/DataDisplay/PlotCanvasView.axaml.cs",    "HasAnySelection")]
    public void TheArrowPan_IsGatedOnAnEmptySelection(string path, string guard)
    {
        var code = CodeOf(path.Split('/'));

        int at = code.IndexOf("CanvasArrowPan.ScreenStep", StringComparison.Ordinal);
        Assert.True(at >= 0, $"{path} should consult CanvasArrowPan.");

        // The guard sits in the 800 characters before the step — the same method, never further off.
        var before = code[Math.Max(0, at - 800)..at];
        Assert.Contains(guard, before, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The Y sign is per canvas and getting it wrong is invisible until someone presses Down.</b>
    /// A Y-up world (layout DBU, wBond z) must SUBTRACT the step's Y where a screen-sense one
    /// (schematic, symbol) adds it — see <c>LayoutViewport</c>'s own convention note.
    /// </summary>
    [Theory]
    [InlineData("src/Ui/Controls/SchematicCanvas.cs",    @"_panY \+= step\.Dy / _zoom")]
    [InlineData("src/Ui/Controls/SymbolEditorCanvas.cs", @"_panY \+= step\.Dy / _zoom")]
    [InlineData("src/Ui/Controls/LayoutCanvas.cs",       @"_panY -= step\.Dy / _zoom")]
    [InlineData("src/Ui/Controls/WBondProfileCanvas.cs", @"_panZ\s*-= step\.Dy / _zoom")]
    public void EachCanvas_AppliesTheStepInItsOwnVerticalSense(string path, string pattern)
        => Assert.Matches(pattern, CodeOf(path.Split('/')));

    // ── Selection queries the canvases read ───────────────────────────────────

    /// <summary>The symbol editor reports primitives AND pins — both are things an arrow key moves,
    /// so either one must keep the key.</summary>
    [Fact]
    public void TheSymbolEditor_ReportsBothPrimitiveAndPinSelection()
    {
        var sym = new EditableSymbol();
        sym.Primitives.Add(new LinePrimitive(SymbolColorRole.SymbolLine, SymbolStrokeTier.Normal,
                                             -80, 0, 80, 0));
        var vm = new SymbolEditorViewModel(sym);

        Assert.False(vm.HasSelection);

        vm.SelectAll();
        Assert.True(vm.HasSelection);
    }

    // ── The Data Display's zoom box ───────────────────────────────────────────

    /// <summary>The magnifier is a toggle: a second click on a lit button turns it back off.</summary>
    [Fact]
    public void TheMagnifier_ArmsAndDisarms()
    {
        var display = new DataDisplayViewModel(new DataSourceLibraryViewModel(), addEmptyPlot: false);

        Assert.False(display.ZoomBoxArmed);

        display.ToggleZoomBox();
        Assert.True(display.ZoomBoxArmed);

        display.ToggleZoomBox();
        Assert.False(display.ZoomBoxArmed);
    }

    /// <summary>
    /// <c>DisarmZoomBox</c> reports whether it DID anything, which is what lets Escape unwind one
    /// thing at a time: armed magnifier first, selection only once it is off.
    /// </summary>
    [Fact]
    public void DisarmingReportsWhetherItWasArmed()
    {
        var display = new DataDisplayViewModel(new DataSourceLibraryViewModel(), addEmptyPlot: false);

        Assert.False(display.DisarmZoomBox());

        display.ToggleZoomBox();
        Assert.True(display.DisarmZoomBox());
        Assert.False(display.ZoomBoxArmed);
    }

    /// <summary>
    /// A box the same shape as the canvas lands centred and exactly framed: the drawn rectangle fills
    /// the viewport, with no margin of its own.
    /// </summary>
    [Fact]
    public void AZoomBox_FramesExactlyWhatWasDrawn()
    {
        var display = new DataDisplayViewModel(new DataSourceLibraryViewModel(), addEmptyPlot: false);
        const double canvasW = 800, canvasH = 600;

        // A quarter-size box in the top-left quadrant, at the resting 1:1 view.
        display.ZoomToScreenRect(0, 0, 400, 300, canvasW, canvasH);

        Assert.Equal(2.0, display.ZoomLevel, 9);
        Assert.Equal(0.0, display.ViewOffsetX, 9);
        Assert.Equal(0.0, display.ViewOffsetY, 9);
    }

    /// <summary>
    /// A box of a different aspect ratio is LETTERBOXED, never cropped — everything inside it is
    /// visible afterwards, which is the promise the gesture makes. The tall box below therefore
    /// takes the HEIGHT ratio (the smaller of the two), and is centred horizontally.
    /// </summary>
    [Fact]
    public void AZoomBoxOfADifferentAspect_IsLetterboxedRatherThanCropped()
    {
        var display = new DataDisplayViewModel(new DataSourceLibraryViewModel(), addEmptyPlot: false);
        const double canvasW = 800, canvasH = 600;

        // 200 x 300: 4.0x would fit the width, 2.0x fits the height. The smaller one wins.
        display.ZoomToScreenRect(100, 0, 200, 300, canvasW, canvasH);

        Assert.Equal(2.0, display.ZoomLevel, 9);

        // The box's own 200-pixel width scales to 400 and is centred in 800, so its left edge lands
        // at 200 — which, for content starting at x = 100, means an offset of 200 - 100*2 = 0.
        Assert.Equal(0.0, display.ViewOffsetX, 9);
        Assert.Equal(0.0, display.ViewOffsetY, 9);
    }

    /// <summary>A degenerate box changes nothing rather than dividing by zero.</summary>
    [Fact]
    public void ADegenerateZoomBox_LeavesTheViewAlone()
    {
        var display = new DataDisplayViewModel(new DataSourceLibraryViewModel(), addEmptyPlot: false);
        display.ZoomLevel = 1.5;

        display.ZoomToScreenRect(10, 10, 0, 0, 800, 600);

        Assert.Equal(1.5, display.ZoomLevel, 9);
    }

    // ── The button arms; it does not zoom ─────────────────────────────────────

    /// <summary>
    /// <b>Every Zoom In button is gone, and the magnifier in its place ARMS.</b> The owner's own
    /// words, 2026-09-11: clicking the button must not zoom — the user draws a box. A button still
    /// wired to a step-zoom command is the regression this catches.
    /// </summary>
    [Theory]
    [InlineData("src/Ui/Views/Layout/LayoutEditorView.axaml")]
    [InlineData("src/Ui/Views/WBond/WBondEditorView.axaml")]
    [InlineData("src/Ui/Views/DataDisplay/DataDisplayView.axaml")]
    public void NoToolbarStillOffersAStepZoomInButton(string path)
    {
        var xaml = Read(path.Split('/'));

        Assert.DoesNotContain("ToolTip.Tip=\"Zoom In", xaml, StringComparison.Ordinal);
        Assert.Contains("Zoom Box", xaml, StringComparison.Ordinal);
    }

    /// <summary>
    /// …and Ctrl/Cmd +/- still steps the zoom in each of them, which is the whole reason the button
    /// could give it up. The Data Display had the binding already; the other two gained one.
    /// </summary>
    [Theory]
    [InlineData("src/Ui/Controls/LayoutCanvas.cs")]
    [InlineData("src/Ui/Views/WBond/WBondEditorView.axaml.cs")]
    public void TheKeyboardStillStepsTheZoom(string path)
    {
        var code = CodeOf(path.Split('/'));

        Assert.Matches(@"Key\.OemPlus\s+or\s+Key\.Add", code);
        Assert.Matches(@"Key\.OemMinus\s+or\s+Key\.Subtract", code);
        Assert.Contains("ZoomIn", code, StringComparison.Ordinal);
        Assert.Contains("ZoomOut", code, StringComparison.Ordinal);
    }

    /// <summary>The Data Display's own pair was already bound and must stay bound.</summary>
    [Fact]
    public void TheDataDisplayKeepsItsCtrlPlusBinding()
    {
        var xaml = Read("src", "Ui", "Views", "DataDisplay", "DataDisplayView.axaml");

        Assert.Contains("Gesture=\"Ctrl+OemPlus\"  Command=\"{Binding ViewModel.Window.ZoomInCommand}\"",
                        xaml, StringComparison.Ordinal);
        Assert.Contains("Gesture=\"Meta+OemPlus\"  Command=\"{Binding ViewModel.Window.ZoomInCommand}\"",
                        xaml, StringComparison.Ordinal);
    }

    // ── Escape disarms, everywhere ────────────────────────────────────────────

    /// <summary>
    /// <b>Escape disarms the magnifier before it means anything else</b>, in all three editors that
    /// gained one. Each unwinds in its own place, and that place is not negotiable: the layout
    /// editor's view TUNNEL claims Escape and forwards it straight to the view model, so a disarm
    /// left in the canvas would never run; the wBond editor's <c>HandleEscape</c> is its documented
    /// single unwind; the Data Display's Escape is a document-level KeyBinding onto DeselectAll.
    /// </summary>
    [Fact]
    public void TheLayoutEditorsEscape_DisarmsTheMagnifierFirst()
    {
        var code = CodeOf("src", "Ui", "Views", "Layout", "LayoutEditorView.axaml.cs");

        int at = code.IndexOf("e.Key != Key.Escape", StringComparison.Ordinal);
        Assert.True(at >= 0);

        var after = code[at..Math.Min(code.Length, at + 500)];
        int disarm = after.IndexOf("DisarmZoomBox", StringComparison.Ordinal);
        int forward = after.IndexOf("OnKeyDown", StringComparison.Ordinal);
        Assert.True(disarm >= 0, "Escape should disarm the magnifier.");
        Assert.True(forward < 0 || disarm < forward,
                    "The disarm must come BEFORE the key reaches the view model, or it never runs.");
    }

    /// <summary>The wBond editor's unwind takes the magnifier as step 0, ahead of the tool and the
    /// selection — it is the most recently armed thing, so it is what Escape means.</summary>
    [Fact]
    public void ThewBondEditorsEscape_DisarmsTheMagnifierFirst()
    {
        var code = CodeOf("src", "Ui", "Views", "WBond", "WBondEditorView.axaml.cs");

        int at = code.IndexOf("private bool HandleEscape()", StringComparison.Ordinal);
        Assert.True(at >= 0);

        var body = code[at..Math.Min(code.Length, at + 900)];
        int disarm = body.IndexOf("DisarmZoomBoxes", StringComparison.Ordinal);
        int tool   = body.IndexOf("WBondTool.Select", StringComparison.Ordinal);
        Assert.True(disarm >= 0 && tool >= 0);
        Assert.True(disarm < tool, "The magnifier unwinds before the tool does.");
    }

    /// <summary>The Data Display's Escape command disarms before it deselects, for the same reason.</summary>
    [Fact]
    public void TheDataDisplaysEscape_DisarmsTheMagnifierFirst()
    {
        var code = CodeOf("src", "Ui", "DataDisplay", "ViewModels", "DisplayWindowViewModel.cs");

        int at = code.IndexOf("private void DeselectAll()", StringComparison.Ordinal);
        Assert.True(at >= 0);

        var body = code[at..Math.Min(code.Length, at + 300)];
        int disarm = body.IndexOf("DisarmZoomBox", StringComparison.Ordinal);
        int clear  = body.IndexOf("DeselectAll()", disarm < 0 ? 0 : disarm, StringComparison.Ordinal);
        Assert.True(disarm >= 0 && clear > disarm,
                    "Escape should disarm the magnifier before it drops the selection.");
    }

    // ── Z arms it, everywhere ─────────────────────────────────────────────────

    /// <summary>
    /// <b>Z arms the magnifier in every editor that has one</b> (owner, 2026-09-11) — the key the
    /// schematic editor has always used for its Zoom Box, so it is one gesture across the
    /// application rather than three spellings of it. Each editor claims the key in whichever of its
    /// own key paths already owns bare letters.
    /// </summary>
    [Theory]
    [InlineData("src/Ui/Controls/LayoutCanvas.cs")]
    [InlineData("src/Ui/Views/WBond/WBondEditorView.axaml.cs")]
    [InlineData("src/Ui/Views/DataDisplay/DataDisplayView.axaml.cs")]
    public void Z_ArmsTheMagnifier(string path)
    {
        var code = CodeOf(path.Split('/'));

        int at = code.IndexOf("Key.Z", StringComparison.Ordinal);
        Assert.True(at >= 0, $"{path} should claim Z for the magnifier.");

        // The arming call is in the same branch — within a few lines of the key it is guarded by.
        var branch = code[at..Math.Min(code.Length, at + 400)];
        Assert.True(branch.Contains("ZoomBox", StringComparison.Ordinal),
                    "Z should reach the zoom box, not something else.");
    }

    /// <summary>
    /// <b>…and not while a label, a marker name or an axis limit is being typed.</b> 'z' is an
    /// ordinary character, and a bare-letter tool key with no typing guard arms the magnifier
    /// mid-word — the exact failure the layout editor's F already had to answer.
    /// </summary>
    [Theory]
    [InlineData("src/Ui/Controls/LayoutCanvas.cs",                        "IsTypingLabel")]
    [InlineData("src/Ui/Views/WBond/WBondEditorView.axaml.cs",            "IsTypingInAField")]
    [InlineData("src/Ui/Views/DataDisplay/DataDisplayView.axaml.cs",      "IsTypingInAField")]
    public void ZIsSuppressedWhileTyping(string path, string guard)
    {
        var code = CodeOf(path.Split('/'));

        int at = code.IndexOf("Key.Z", StringComparison.Ordinal);
        Assert.True(at >= 0);

        // The guard is either on the branch itself or on the block that encloses it.
        var around = code[Math.Max(0, at - 1200)..Math.Min(code.Length, at + 200)];
        Assert.Contains(guard, around, StringComparison.Ordinal);
    }

    /// <summary>Every Zoom Box button advertises the key, in the schematic editor's own spelling.</summary>
    [Theory]
    [InlineData("src/Ui/Views/Content/SchematicView.axaml")]
    [InlineData("src/Ui/Views/Layout/LayoutEditorView.axaml")]
    [InlineData("src/Ui/Views/WBond/WBondEditorView.axaml")]
    [InlineData("src/Ui/Views/DataDisplay/DataDisplayView.axaml")]
    public void EveryZoomBoxButton_AdvertisesTheKey(string path)
        => Assert.Contains("Zoom Box  (Z)", Read(path.Split('/')), StringComparison.Ordinal);

    // ── The armed pointer says so ─────────────────────────────────────────────

    /// <summary>
    /// The armed magnifier reads as a crosshair in all three — the same pointer the schematic
    /// editor's Zoom Box has always used. A mode with no pointer change is one the user discovers by
    /// clicking.
    /// </summary>
    [Theory]
    [InlineData("src/Ui/Controls/LayoutCanvas.cs")]
    [InlineData("src/Ui/Controls/WBondProfileCanvas.cs")]
    [InlineData("src/Ui/Views/DataDisplay/PlotCanvasView.axaml.cs")]
    public void AnArmedMagnifier_ShowsACrosshair(string path)
    {
        var code = CodeOf(path.Split('/'));

        int at = code.IndexOf("ZoomBoxArmed", StringComparison.Ordinal);
        Assert.True(at >= 0);
        Assert.Contains("StandardCursorType.Cross", code, StringComparison.Ordinal);
    }
}

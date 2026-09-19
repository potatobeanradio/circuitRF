// ================================================================
//  RailBoardViewTests.cs — brief-railrf-8-board-view.md §5
//
//  ── THE HEADLINE GATE IS A COMPARISON, NOT A CHECKLIST ──────────────────────────────────────
//
//  §11.6's instruction is that railRF's board pans and zooms EXACTLY as the layout editor does —
//  "not 'similarly' — the same gestures, the same step sizes, the same anchoring", because "a
//  near-miss is worse than an absence because it is discovered by being wrong". The brief turns
//  that into one test shape:
//
//      Every gesture in §11.6's table is driven on the railRF board view AND on a layout editor
//      view holding the same geometry, and the two resulting viewports must be identical.
//      A list of features can be satisfied by an approximation of each one; asserting the same
//      viewport cannot.
//
//  So the "railRF board view" below is a real LayoutCanvas carrying railRF's overlay and railRF's
//  widened keyboard gate, and the "layout editor view" is a bare one over the same shapes. Every
//  gesture is raised as a real Avalonia event on both, and the two CurrentViewports are compared
//  field for field. The test passes for the only acceptable reason: they are the same control
//  running the same code.
//
//  ── ONE PLATFORM SERVICE IS STOOD IN FOR, AND ONLY ONE ──────────────────────────────────────
//
//  LayoutCanvas.SetCursor constructs an Avalonia Cursor, which needs ICursorFactory — a platform
//  service this test project stands up no app for. Space-to-pan and the middle-drag pan both set
//  the hand cursor on their first line, so without it those two gestures of the seven cannot be
//  driven at all. ICursorFactory is marked not-implementable by client code, so the stand-in is a
//  DispatchProxy bound through the locator (CursorFactoryShim below). Nothing else about Avalonia
//  is faked: the events are real, the routing is real, and the control is the shipped one.
// ================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Pdn;
using CircuitRF.Design.RailRf;
using CircuitRF.Render;
using CircuitRF.Ui.Controls;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.RailRf;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests.RailRf;

public sealed class RailBoardViewTests
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;   // 1000 DBU/µm
    private static readonly LayerKey Top = new(1, 0);
    private static readonly LayerKey Bot = new(2, 0);

    private static long Mm(double v) => (long)Math.Round(v * 1e3 * Dbu);
    private static long Um(double v) => (long)Math.Round(v * Dbu);

    private const double CanvasWidth = 420, CanvasHeight = 300;

    // ══ R-rail18-4 — a box in DBU with text in points ════════════════════════════════════════

    /// <summary>
    /// At every canvas width from a phone-sized pane up to the one the documentation figures capture
    /// at, <b>the legend's label rectangles do not intersect</b> — and the minimum and the maximum
    /// are drawn at all of them.
    /// </summary>
    /// <remarks>
    /// <c>RailMapLegend.Box</c> is a <see cref="Bbox"/> in DBU and <c>RailMapRenderer</c>'s font is
    /// sized in screen points, so the plate shrinks with the canvas and the three strings do not.
    /// Below roughly 600 px the minimum, the caption and the maximum overlapped — in the window, and
    /// in every <c>.svg</c> and <c>.pdf</c> <c>RailReportPage</c> and <c>RailGraphicExport</c> draw
    /// through this same renderer.
    ///
    /// <para><b>The ladder is the test.</b> A gate that only checked the widest case is the case
    /// that already passed; the narrow rungs are the ones that were red.</para>
    /// </remarks>
    [Theory]
    [InlineData(160)]
    [InlineData(200)]
    [InlineData(240)]
    [InlineData(320)]
    [InlineData(640)]
    [InlineData(1600)]
    public void R_rail18_4_TheLegendsThreeLabelsNeverOverlap(int widthPx)
    {
        var scene = RailMapScene.Build(ResultOf(out _), RailMapKind.Drop, Dbu);
        var legend = scene.Legend;
        Assert.NotNull(legend);

        int heightPx = Math.Max(120, widthPx * 3 / 4);
        var vp = LayoutViewport.ZoomToFit(scene.Bounds, widthPx, heightPx);

        Assert.True(RailMapRenderer.TryPlate(legend!, vp, out _, out var bar, out float baseline),
                    "the plate collapsed to nothing at this width.");

        var layout = RailMapRenderer.LayOutLabels(legend!, bar.Left, bar.Right, baseline);

        // All three are always drawn: the caption carries the model kind, and §2.9 rule 1 makes that
        // the one thing a picture which has left the window must still say.
        Assert.True(layout.Cold.Width > 0 && layout.Caption.Width > 0 && layout.Hot.Width > 0,
                    "the plate lost one of its three labels.");

        Assert.False(layout.Cold.IntersectsWith(layout.Caption),
                     $"the minimum and the caption overlap at {widthPx} px.");
        Assert.False(layout.Caption.IntersectsWith(layout.Hot),
                     $"the caption and the maximum overlap at {widthPx} px.");
        Assert.False(layout.Cold.IntersectsWith(layout.Hot),
                     $"the minimum and the maximum overlap at {widthPx} px.");

        // ── and the half that gives the assertions above their teeth ──────────────────────────
        //
        // Wherever the renderer had to shrink, the OLD full-size layout really did overlap. Without
        // this the ladder could pass on a renderer that never had a problem — which is how this
        // defect survived brief 8's own gate.
        Assert.True(widthPx > 240 || layout.TextSizePx < RailMapRenderer.LegendSizePx,
                    $"nothing was shrunk at {widthPx} px, so the narrow rungs of this ladder are not "
                  + "exercising the fix.");

        if (layout.TextSizePx >= RailMapRenderer.LegendSizePx) return;

        var full = FullSizeLabels(legend!, bar, baseline);
        Assert.True(full.Cold.IntersectsWith(full.Caption) || full.Caption.IntersectsWith(full.Hot)
                 || full.Cold.IntersectsWith(full.Hot),
                    $"the text was shrunk at {widthPx} px but the full-size labels did not overlap, "
                  + "so the shrink was not the fix for anything.");
    }

    /// <summary>The three label boxes as they were laid out before R-rail18-4 — full size, left,
    /// centre and right. Built from the SAME measurement the renderer uses (a layout over a plate
    /// wide enough that nothing scales), so this is the old behaviour rather than a second guess at
    /// the string widths.</summary>
    private static RailMapLabelLayout FullSizeLabels(RailMapLegend legend, SKRect bar, float baseline)
    {
        var unscaled = RailMapRenderer.LayOutLabels(legend, 0, 100_000f, baseline);
        Assert.Equal(RailMapRenderer.LegendSizePx, unscaled.TextSizePx);

        float top = baseline - RailMapRenderer.LegendSizePx;
        float centre = (bar.Left + bar.Right) / 2f;

        return new RailMapLabelLayout(
            RailMapRenderer.LegendSizePx,
            new SKRect(bar.Left, top, bar.Left + unscaled.Cold.Width, baseline),
            new SKRect(centre - unscaled.Caption.Width / 2f, top,
                       centre + unscaled.Caption.Width / 2f, baseline),
            new SKRect(bar.Right - unscaled.Hot.Width, top, bar.Right, baseline));
    }

    // ══ fixtures ═════════════════════════════════════════════════════════════════════════════

    private static LayoutView Artwork()
    {
        var view = new LayoutView { DbuPerMicron = Dbu, DisplayUnit = LayoutUnit.Um, SnapDbu = Um(10) };
        view.Shapes.Add(new RectShape { Layer = Top, X1 = 0, Y1 = 0, X2 = Mm(30), Y2 = Mm(0.4) });
        view.Shapes.Add(new RectShape { Layer = Bot, X1 = 0, Y1 = 0, X2 = Mm(30), Y2 = Mm(0.4) });
        return view;
    }

    /// <summary>A canvas over the artwork, laid out and framed, with no overlay — the LAYOUT EDITOR
    /// side of every comparison below.</summary>
    private static LayoutCanvas EditorView()
    {
        CursorFactoryShim.Install();
        var canvas = new LayoutCanvas { ViewModel = new LayoutEditorViewModel(Artwork()) };
        canvas.Measure(new Size(CanvasWidth, CanvasHeight));
        canvas.Arrange(new Rect(0, 0, CanvasWidth, CanvasHeight));
        canvas.ZoomToFit();
        return canvas;
    }

    /// <summary>
    /// The same canvas with railRF's overlay on it and railRF's widened keyboard gate wired up —
    /// the BOARD VIEW side.
    /// </summary>
    /// <remarks>
    /// The overlay deliberately carries no result: a scene with a legend in it would change what Zoom
    /// to Fit frames, and that is R-rail8-7's subject rather than this one's. What is being compared
    /// here is navigation, and the overlay's presence is the thing that could have broken it.
    /// </remarks>
    private static (LayoutCanvas Canvas, RailLayoutOverlay Overlay, Func<bool> Gate) BoardView()
    {
        CursorFactoryShim.Install();
        var overlay = new RailLayoutOverlay();
        bool suppress = false;

        var canvas = new LayoutCanvas
        {
            ViewModel = new LayoutEditorViewModel(Artwork()),
            CanvasOverlay = overlay,
            NavigationKeysSuppressed = () => suppress,
        };
        overlay.OverlayChanged += canvas.InvalidateOverlay;

        canvas.Measure(new Size(CanvasWidth, CanvasHeight));
        canvas.Arrange(new Rect(0, 0, CanvasWidth, CanvasHeight));
        canvas.ZoomToFit();

        return (canvas, overlay, () => suppress = !suppress);
    }

    // ══ the headline gate ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Drives one gesture on both views and asserts the viewports came out identical.
    /// </summary>
    /// <remarks>
    /// Exact equality, not a tolerance: both sides are the same arithmetic on the same doubles, so
    /// anything but bit equality means they are not the same code.
    /// </remarks>
    private static void SameViewport(Action<LayoutCanvas> gesture)
    {
        var editor = EditorView();
        var (board, _, _) = BoardView();

        Assert.Equal(editor.CurrentViewport, board.CurrentViewport);   // and they start together

        gesture(editor);
        gesture(board);

        Assert.Equal(editor.CurrentViewport, board.CurrentViewport);
    }

    [Fact]
    public void Wheel_ZoomsAnchoredOnTheWorldPointUnderTheCursor_Identically() =>
        SameViewport(c =>
        {
            var before = c.CurrentViewport;
            Wheel(c, 137, 91, +1);
            Assert.NotEqual(before.Zoom, c.CurrentViewport.Zoom);

            // Anchored, not centred: the world point under the cursor must not have moved.
            Assert.Equal(before.ScreenToWorldX(137), c.CurrentViewport.ScreenToWorldX(137), 3);
            Assert.Equal(before.ScreenToWorldY(91), c.CurrentViewport.ScreenToWorldY(91), 3);
        });

    [Fact]
    public void SpaceThenLeftDrag_Pans_Identically() =>
        SameViewport(c =>
        {
            var before = c.CurrentViewport;
            Key(c, Avalonia.Input.Key.Space);
            Drag(c, new Point(120, 120), new Point(170, 155), RawInputModifiers.LeftMouseButton,
                 PointerUpdateKind.LeftButtonPressed);
            Assert.NotEqual(before.PanX, c.CurrentViewport.PanX);
        });

    [Fact]
    public void MiddleButtonDrag_Pans_Identically() =>
        SameViewport(c =>
        {
            var before = c.CurrentViewport;
            Drag(c, new Point(120, 120), new Point(90, 140), RawInputModifiers.MiddleMouseButton,
                 PointerUpdateKind.MiddleButtonPressed);
            Assert.NotEqual(before.PanX, c.CurrentViewport.PanX);
        });

    [Fact]
    public void F_ZoomsToFit_Identically() =>
        SameViewport(c =>
        {
            Wheel(c, 40, 40, +1);              // move away from the fit first, or F proves nothing
            Key(c, Avalonia.Input.Key.F);
        });

    [Fact]
    public void ZThenLeftDrag_ZoomsToTheMarquee_Identically() =>
        SameViewport(c =>
        {
            Key(c, Avalonia.Input.Key.Z);
            Assert.True(c.ZoomBoxArmed);

            Drag(c, new Point(80, 70), new Point(260, 200), RawInputModifiers.LeftMouseButton,
                 PointerUpdateKind.LeftButtonPressed);

            // ONE SHOT, self-disarming — the gesture is not a mode.
            Assert.False(c.ZoomBoxArmed);
        });

    [Theory]
    [InlineData(Avalonia.Input.Key.OemPlus)]
    [InlineData(Avalonia.Input.Key.Add)]          // numeric keypad: a different Key for the same character
    [InlineData(Avalonia.Input.Key.OemMinus)]
    [InlineData(Avalonia.Input.Key.Subtract)]
    public void CtrlZoomStep_MainRowAndKeypad_Identically(Avalonia.Input.Key key) =>
        SameViewport(c =>
        {
            var before = c.CurrentViewport;
            Key(c, key, KeyModifiers.Control);
            Assert.NotEqual(before.Zoom, c.CurrentViewport.Zoom);
        });

    [Theory]
    [InlineData(Avalonia.Input.Key.Left,  KeyModifiers.None)]
    [InlineData(Avalonia.Input.Key.Right, KeyModifiers.None)]
    [InlineData(Avalonia.Input.Key.Up,    KeyModifiers.Shift)]   // the coarse step
    [InlineData(Avalonia.Input.Key.Down,  KeyModifiers.Shift)]
    public void ArrowKeys_Pan_Identically(Avalonia.Input.Key key, KeyModifiers modifiers) =>
        SameViewport(c =>
        {
            var before = c.CurrentViewport;
            Key(c, key, modifiers);
            Assert.True(before.PanX != c.CurrentViewport.PanX || before.PanY != c.CurrentViewport.PanY);
        });

    [Fact]
    public void Escape_DisarmsTheMagnifier_Identically() =>
        SameViewport(c =>
        {
            Key(c, Avalonia.Input.Key.Z);
            Assert.True(c.ZoomBoxArmed);
            Key(c, Avalonia.Input.Key.Escape);
            Assert.False(c.ZoomBoxArmed);
        });

    /// <summary>
    /// The four toolbar buttons, which are what a user who has never read a tooltip finds.
    /// </summary>
    /// <remarks>
    /// Driven as the CANVAS methods the window's four handlers call, because that is the whole of
    /// what those handlers do — see <c>RailRfWindow.OnBoardZoomToFit</c> and its three siblings, and
    /// <c>LayoutEditorView.OnZoomToFit</c> and its three. The XAML's own half (that there are four of
    /// them, in that order, with those tooltips) is asserted separately below.
    /// </remarks>
    [Fact]
    public void TheFourToolbarButtons_DoTheSameThing_Identically()
    {
        SameViewport(c => { Wheel(c, 40, 40, +1); c.ZoomToFit(); });
        SameViewport(c => c.ZoomOut());
        SameViewport(c => c.Zoom1To1());
        SameViewport(c => { c.ArmZoomBox(); Assert.True(c.ZoomBoxArmed); c.DisarmZoomBox(); });
    }

    /// <summary>
    /// <b>The same four buttons, in the same order and with the same tooltips</b> (§11.6) — read out
    /// of both windows' AXAML, because that is where the decision lives.
    /// </summary>
    [Fact]
    public void TheBoardPanelCarriesTheLayoutEditorsOwnFourButtons()
    {
        string rail = Read("src", "Ui", "Views", "RailRf", "RailRfWindow.axaml");
        string editor = Read("src", "Ui", "Views", "Layout", "LayoutEditorView.axaml");

        string[] tips =
        [
            "Zoom to Fit  (F)",
            "Zoom Box  (Z) — drag a box to zoom to it  (Esc cancels; Ctrl+ +/- steps)",
            "Zoom Out",
            "Zoom 1:1 (1 px per display-unit tick)",
        ];

        int at = -1;
        foreach (string tip in tips)
        {
            Assert.Contains(tip, editor);

            int found = rail.IndexOf(tip, StringComparison.Ordinal);
            Assert.True(found > at,
                $"railRF's board panel is missing '{tip}', or carries it out of the layout editor's order.");
            at = found;
        }
    }

    // ══ R-rail8-4 — the gate is WIDER here, and it is a gate rather than a gesture ════════════

    /// <summary>
    /// With focus in a text field, <c>F</c>, <c>Z</c> and the arrows change <b>nothing</b>.
    /// </summary>
    /// <remarks>
    /// §11.6 trap 1: railRF's whole left column is editable rows, so the gate is wider here, not
    /// narrower. <b>The negative is what gives it teeth</b> — the same keys on the same canvas with
    /// the gate open DO move the view, so a gate that had quietly stopped being consulted fails this
    /// test rather than passing it for want of anything happening.
    ///
    /// <para>The caret half of the brief's wording is not asserted: a <see cref="TextBox"/> with no
    /// application host has no template applied, so it moves no caret here whatever is raised at it.
    /// What IS asserted is the half that can go wrong — the board does not move — plus that the key
    /// was left unhandled, which is what lets the field have it.</para>
    /// </remarks>
    [Theory]
    [InlineData(Avalonia.Input.Key.F)]
    [InlineData(Avalonia.Input.Key.Z)]
    [InlineData(Avalonia.Input.Key.Left)]
    [InlineData(Avalonia.Input.Key.Down)]
    public void WithFocusInATextField_NoNavigationKeyFires(Avalonia.Input.Key key)
    {
        var (canvas, _, toggleGate) = BoardView();
        Wheel(canvas, 40, 40, +1);                       // somewhere F could visibly return from
        var before = canvas.CurrentViewport;

        toggleGate();                                    // focus is now in a left-column value field
        var args = Key(canvas, key);

        Assert.Equal(before, canvas.CurrentViewport);
        Assert.False(canvas.ZoomBoxArmed);
        Assert.False(args.Handled);                      // …so the field still gets the character

        // The negative, and it is per key because Z is the one that arms rather than moves: with the
        // gate OPEN every one of them does its own thing, so a gate that had quietly stopped being
        // consulted fails here rather than passing for want of anything happening.
        toggleGate();
        Key(canvas, key);

        if (key == Avalonia.Input.Key.Z) Assert.True(canvas.ZoomBoxArmed);
        else Assert.NotEqual(before, canvas.CurrentViewport);
    }

    /// <summary>
    /// The gate's own rule: the focused element is, or sits inside, a <see cref="TextBox"/>.
    /// </summary>
    /// <remarks>
    /// <c>InlineEditText</c> — what railRF's left column is made of — cannot be constructed without a
    /// platform cursor factory, so it is not instantiated here. It does not need to be: its editor IS
    /// a real <c>TextBox</c> and that box is what takes focus, which is exactly why the gate is stated
    /// as one rule about TextBox rather than as a list of control types that would go stale.
    /// </remarks>
    [Fact]
    public void TheGateRecognisesATextBox_AndNothingElse()
    {
        Assert.True(RailKeyboardGate.IsTextEntry(new TextBox { Text = "80 mV" }));

        Assert.False(RailKeyboardGate.IsTextEntry(null));            // nothing focused is not a field
        Assert.False(RailKeyboardGate.IsTextEntry(new Button()));
        Assert.False(RailKeyboardGate.IsTextEntry(new Border()));
        Assert.False(RailKeyboardGate.IsTextEntry("not a visual"));

        // The ancestor branch — a focused TextPresenter INSIDE a box — is not driven here: a TextBox
        // builds no visual children until a template is applied, and this project stands up no
        // application to supply one. It is what makes the rule cover InlineEditText, whose editor is a
        // real TextBox with a real template under it.
    }

    // ══ R-rail8-5 — the arrow keys are the case the instruction turns on ═════════════════════

    /// <summary>
    /// With focus on the board and nothing selected an arrow key pans by
    /// <see cref="CanvasArrowPan.ScreenStep"/>; with something selected it does not pan at all.
    /// </summary>
    /// <remarks>
    /// §11.6 trap 2: the arrow-key NUDGE is the older gesture and keeps the key whenever it has
    /// something to move. It matters because <b>a trackpad has no middle button</b> and the two-finger
    /// scroll is spent on zoom — on a laptop the arrow keys are the only pan there is.
    /// </remarks>
    [Fact]
    public void AnArrowKeyPansByTheCanvasOwnStep_AndOnlyWithNothingSelected()
    {
        var (canvas, _, _) = BoardView();
        var before = canvas.CurrentViewport;

        Key(canvas, Avalonia.Input.Key.Right);

        var step = CanvasArrowPan.ScreenStep(Avalonia.Input.Key.Right, KeyModifiers.None)!.Value;
        Assert.Equal(before.PanX + step.Dx / before.Zoom, canvas.CurrentViewport.PanX, 6);
        Assert.Equal(before.PanY, canvas.CurrentViewport.PanY, 6);

        // …and with something selected the key belongs to the nudge, so the view stays put.
        canvas.ViewModel!.SelectAllCommand.Execute(null);
        Assert.True(canvas.ViewModel.HasSelection);

        var held = canvas.CurrentViewport;
        Key(canvas, Avalonia.Input.Key.Right);
        Assert.Equal(held, canvas.CurrentViewport);
    }

    // ══ R-rail8-6 — every held-key latch is dropped on LostFocus ═════════════════════════════

    /// <summary>
    /// <b>The recorded bug, reproduced as a test.</b> Hold Space, move focus away, release Space
    /// elsewhere, then left-drag: it is a marquee, not a pan.
    /// </summary>
    /// <remarks>
    /// §11.6 trap 3. The key-up goes to whatever took focus and the canvas never sees it, so without
    /// the <c>LostFocus</c> drop the latch stays set and from that moment <i>every</i> left-drag is a
    /// pan with nothing on screen explaining it. The symptom named the wrong subsystem entirely —
    /// "marquee select stopped working in the layout view" — and in a window whose board panel is
    /// ringed by controls it is MORE likely here than it was there.
    /// </remarks>
    [Fact]
    public void SpaceReleasedElsewhere_LeavesAMarqueeAndNotAPan()
    {
        var (canvas, _, _) = BoardView();

        Key(canvas, Avalonia.Input.Key.Space);          // held
        LoseFocus(canvas);

        var before = canvas.CurrentViewport;
        Drag(canvas, new Point(120, 120), new Point(180, 160), RawInputModifiers.LeftMouseButton,
             PointerUpdateKind.LeftButtonPressed);

        Assert.Equal(before, canvas.CurrentViewport);           // the view did not pan …
        Assert.True(canvas.ViewModel!.MarqueeRecomputeCount > 0, // … and the drag WAS a marquee
            "the left-drag panned (or did nothing) instead of running a marquee, which is the latch "
          + "this test exists for.");

        // The negative: the SAME drag with the latch still held IS a pan, so this is passing because
        // LostFocus dropped it rather than because a left-drag never pans.
        var (stuck, _, _) = BoardView();
        Key(stuck, Avalonia.Input.Key.Space);
        var held = stuck.CurrentViewport;
        Drag(stuck, new Point(120, 120), new Point(180, 160), RawInputModifiers.LeftMouseButton,
             PointerUpdateKind.LeftButtonPressed);

        Assert.NotEqual(held, stuck.CurrentViewport);
        Assert.Equal(0, stuck.ViewModel!.MarqueeRecomputeCount);
    }

    /// <summary>The overlay's own latch goes with it, because the seam's contract is that
    /// <c>OnFocusLost</c> drops everything.</summary>
    [Fact]
    public void TheOverlayDropsItsReadoutOnFocusLost()
    {
        var overlay = WithResult(out _);
        overlay.Kind = RailMapKind.Drop;

        overlay.OnPointerMoved(Mm(15), Mm(0.2), Um(20), leftButtonDown: false, KeyModifiers.None);
        Assert.NotNull(overlay.Readout);

        overlay.OnFocusLost();
        Assert.Null(overlay.Readout);
    }

    // ══ R-rail8-7 — Zoom to Fit must include the overlay's own extent ════════════════════════

    /// <summary>
    /// A layout whose overlay legend sits outside the copper's bbox fits to the <b>union</b>.
    /// </summary>
    /// <remarks>
    /// <b>With the negative that proves the gate has teeth</b>: a second overlay returning only the
    /// copper's bbox frames the legend out. §11.6 trap 4 is that this looks harmless because a drop
    /// map is co-extensive with the copper — right up until a legend, a source marker or a flagged-via
    /// callout sits outside it.
    /// </remarks>
    [Fact]
    public void ZoomToFitFramesTheLegend_AndTheCopperBboxAloneWouldNot()
    {
        var overlay = WithResult(out var result);
        overlay.Kind = RailMapKind.Drop;

        var legend = overlay.Scene.Legend;
        Assert.NotNull(legend);

        // The legend is genuinely outside the copper, or this gate is about nothing.
        var copper = CopperBounds(result);
        Assert.True(legend!.Box.MinY < copper.MinY,
            "the legend must sit outside the copper's own bbox for this gate to mean anything");

        // ── The canvas is sized to the COPPER'S OWN ASPECT, and that is load-bearing ────────────
        //
        // Zoom to Fit LETTERBOXES: on a canvas whose aspect has nothing to do with the content's, the
        // margin it leaves on the short axis is larger than everything this gate is about, and a fit
        // that framed only the copper would still have the legend somewhere inside that margin. The
        // supply trace here is 75:1, which is an ordinary shape for a rail and the worst case for
        // this trap — so the canvas is given the same aspect and the framing question becomes a real
        // one.
        double w = 600;
        double h = Math.Max(2, w * (copper.MaxY - copper.MinY) / (double)(copper.MaxX - copper.MinX));

        static LayoutCanvas On(ILayoutCanvasOverlay overlay, double w, double h)
        {
            CursorFactoryShim.Install();
            var canvas = new LayoutCanvas
            {
                ViewModel = new LayoutEditorViewModel(Artwork()),
                CanvasOverlay = overlay,
            };
            canvas.Measure(new Size(w, h));
            canvas.Arrange(new Rect(0, 0, w, h));
            canvas.ZoomToFit();
            return canvas;
        }

        var framed = On(overlay, w, h).CurrentViewport;
        Assert.True(framed.VisibleMinY <= legend.Box.MinY,
            $"Zoom to Fit framed down to {framed.VisibleMinY} DBU and the legend starts at "
          + $"{legend.Box.MinY} DBU, so it was cut off.");

        // The negative: an overlay that answers with the copper's bbox instead.
        var blind = On(new StubOverlay(copper), w, h).CurrentViewport;
        Assert.True(blind.VisibleMinY > legend.Box.MinY,
            "the negative did not reproduce: returning the copper bbox still framed the legend, so "
          + "this gate would pass whatever ContentBounds returned.");
    }

    // ══ R-rail8-2 — a map repaint is a repaint ═══════════════════════════════════════════════

    /// <summary>
    /// Switching tabs, taking a result and moving the cursor repaint the overlay and leave the
    /// layout's path cache <b>untouched</b>.
    /// </summary>
    /// <remarks>
    /// Asserted on the cache's own count, fed the change stream the canvas feeds it
    /// (<c>LayoutView.Changed</c> → <c>LayoutPathCache.Apply</c>). A rebuild counter that moves is the
    /// 500k-shape defect the overlay seam exists to prevent: on a real board this is the difference
    /// between a repaint and a rebuild of half a million shapes.
    /// </remarks>
    [Fact]
    public void AMapRepaintDoesNotInvalidateThePathCache()
    {
        var view = Artwork();
        var cache = new LayoutPathCache(1000);
        int modelChanges = 0;
        view.Changed += (_, e) => { modelChanges++; cache.Apply(e); };

        // Warm it, the way a frame does.
        var counters = new LayoutFrameCounters();
        for (int i = 0; i < view.Shapes.Count; i++)
            cache.GetOrBuild(i, view.Shapes[i], 1.0 / Dbu, 0, counters, out _);
        int warm = cache.Count;
        Assert.True(warm > 0);

        var overlay = WithResult(out _);
        int repaints = 0;
        overlay.OverlayChanged += () => repaints++;

        overlay.Kind = RailMapKind.Drop;
        overlay.Kind = RailMapKind.Class;
        overlay.Kind = RailMapKind.Copper;
        overlay.OnPointerMoved(Mm(15), Mm(0.2), Um(20), leftButtonDown: false, KeyModifiers.None);
        overlay.Theme = RailMapTheme.Dark;

        Assert.True(repaints >= 4, "the overlay did not ask for a repaint at all, so this proves nothing.");
        Assert.Equal(0, modelChanges);
        Assert.Equal(warm, cache.Count);
    }

    /// <summary>The scope rule, asserted directly: the overlay consumes <b>no</b> gesture, so every
    /// one of them reaches the canvas's own state machine.</summary>
    [Fact]
    public void TheOverlayDeclinesEveryGesture()
    {
        var overlay = WithResult(out _);
        overlay.Kind = RailMapKind.Drop;

        Assert.False(overlay.OnPointerPressed(Mm(15), Mm(0.2), Um(20), KeyModifiers.None, 1));
        Assert.False(overlay.OnPointerMoved(Mm(15), Mm(0.2), Um(20), true, KeyModifiers.None));
        Assert.False(overlay.OnPointerReleased(Mm(15), Mm(0.2)));

        foreach (var key in new[] { Avalonia.Input.Key.F, Avalonia.Input.Key.Z, Avalonia.Input.Key.Left,
                                    Avalonia.Input.Key.Escape, Avalonia.Input.Key.Space })
            Assert.False(overlay.OnKeyDown(key, KeyModifiers.None));
    }

    // ══ R-rail8-3 — the one unit conversion, at the default that fails silently ══════════════

    /// <summary>
    /// Metres ↔ DBU round-trips, on geometry <b>off both axes and off a round number</b>, at the
    /// 1000 DBU/µm default.
    /// </summary>
    /// <remarks>
    /// The recorded wBond finding is that the nm↔DBU bridge fails <i>silently</i> at exactly that
    /// default — 1000 is also a plausible wrong factor — so a test on an axis-aligned round value
    /// passes whether the conversion is right or not. Every value below is off both axes and carries
    /// digits a factor-of-ten error would move.
    /// </remarks>
    [Theory]
    [InlineData(0.003_724_1)]    // 3.7241 mm
    [InlineData(0.000_137_9)]    // 137.9 µm — a trace width nobody rounds
    [InlineData(0.091_6)]        // 91.6 mm — a board edge
    public void MetresAndDbuRoundTripAtTheDefaultResolution(double metres)
    {
        long dbu = RailMapScene.MetresToDbu(metres, Dbu);

        // The factor itself, stated rather than inferred: 1 m is 10^6 µm is 10^9 DBU at 1000 DBU/µm.
        Assert.Equal(1e9, RailMapScene.DbuPerMetre(Dbu));
        Assert.Equal((long)Math.Round(metres * 1e9), dbu);
        Assert.Equal(metres, RailMapScene.DbuToMetres(dbu, Dbu), 12);

        // …and it is not the identity, which is what a wrong factor would look like here.
        Assert.NotEqual(dbu, (long)Math.Round(metres));
    }

    // ══ R-rail8-10 — the map is opaque paint ═════════════════════════════════════════════════

    /// <summary>
    /// The map renders <b>identically</b> on two different page backgrounds.
    /// </summary>
    /// <remarks>
    /// §11.7 point 2, decided in the design note "rather than discovered in an export". The stackup
    /// renderer is the counter-example: it uses the background colour as PAINT, to cut a drill hole,
    /// and not painting the bore did not make it transparent — it showed the dielectric behind it.
    /// Nothing in a railRF map may depend on the page's background being any particular colour.
    /// </remarks>
    [Fact]
    public void TheMapRendersIdenticallyOnAnyBackground()
    {
        var overlay = WithResult(out _);
        overlay.Kind = RailMapKind.Drop;

        AssertNothingButThePageShowsThrough(overlay, SKColors.White, SKColors.Black, tolerance: 1);
        AssertNothingButThePageShowsThrough(overlay, SKColors.White, new SKColor(0, 160, 60), tolerance: 2);
    }

    /// <summary>
    /// <b>Every colour a railRF map can paint is fully opaque</b> — the ramp included, in both
    /// variants.
    /// </summary>
    /// <remarks>
    /// This is the half of §11.7 point 2 that a pixel comparison cannot reach: a translucent fill is a
    /// blend of paint and page, and a blend is indistinguishable from antialiasing when all you have
    /// is two renders. So the alpha is asserted at the source, where the decision is actually made.
    /// The ramp is sampled across its whole range because it is INTERPOLATED, and an interpolation
    /// through a translucent stop is opaque at both ends and not in the middle.
    /// </remarks>
    [Fact]
    public void EveryColourAMapCanPaintIsOpaque()
    {
        foreach (var theme in new[] { RailMapTheme.Light, RailMapTheme.Dark, RailMapTheme.Fallback })
        {
            foreach (var colour in new[]
            {
                theme.MapCold, theme.MapMid, theme.MapHot,
                theme.LegendBackground, theme.LegendInk,
                theme.Source, theme.Load, theme.ViaFlag,
                theme.ClassTrace, theme.ClassSpreading, theme.ClassForced, theme.CopperHighlight,
            })
                Assert.Equal(255, colour.Alpha);

            for (int i = 0; i <= 64; i++)
                Assert.Equal(255, theme.Ramp(i / 64.0).Alpha);
        }
    }

    /// <summary>
    /// Renders the same scene over two backgrounds and asserts that <b>the only thing that changed is
    /// how much of the page shows through</b>.
    /// </summary>
    /// <remarks>
    /// <b>What this can and cannot see.</b> An antialiased edge is by construction a mix of the paint
    /// and whatever is behind it, and that is true of every stroke and every glyph this application
    /// draws — so a differing pixel is not by itself a defect. What it must be is a BLEND: the two
    /// values must differ by the same factor on every channel, which is what <c>f + (1−α)(bg − f)</c>
    /// gives. A pixel that changed in some other way is paint that consulted the background, which is
    /// what §11.7 point 2 forbids — the stackup renderer's drill bore is the case that decided it, and
    /// its symptom is a hole that showed the dielectric behind it rather than nothing.
    ///
    /// <para>The companion half, translucency, is asserted at the source in
    /// <see cref="EveryColourAMapCanPaintIsOpaque"/>: a translucent fill IS a blend and no comparison
    /// of two renders can tell it from an antialiased one.</para>
    ///
    /// <para><paramref name="tolerance"/> is per channel and it is ROUNDING, not slack: Skia blends in
    /// bytes, so a coverage of 6 % lands one LSB apart on two channels of the same pixel. One is what
    /// that costs; the green page needs two because its own channels are further apart.</para>
    /// </remarks>
    private static void AssertNothingButThePageShowsThrough(
        RailLayoutOverlay overlay, SKColor first, SKColor second, int tolerance)
    {
        byte[] a = RenderOverlay(overlay, first);
        byte[] b = RenderOverlay(overlay, second);
        Assert.Equal(a.Length, b.Length);

        // How far apart the two pages are, per channel, in the surface's own byte order — read off a
        // corner pixel, which nothing paints.
        double[] gap = [a[0] - (double)b[0], a[1] - (double)b[1], a[2] - (double)b[2]];
        int widest = Math.Abs(gap[0]) >= Math.Abs(gap[1]) && Math.Abs(gap[0]) >= Math.Abs(gap[2]) ? 0
                   : Math.Abs(gap[1]) >= Math.Abs(gap[2]) ? 1 : 2;
        Assert.NotEqual(0.0, gap[widest]);

        int identicalPaint = 0, blended = 0;
        for (int i = 0; i + 3 < a.Length; i += 4)
        {
            if (a[i] == b[i] && a[i + 1] == b[i + 1] && a[i + 2] == b[i + 2] && a[i + 3] == b[i + 3])
            {
                if (!IsColour(a, i, first)) identicalPaint++;    // covered by opaque paint, both times
                continue;
            }

            blended++;

            // How much page is showing, read off the channel the two pages differ most on …
            double alpha = (a[i + widest] - (double)b[i + widest]) / gap[widest];
            Assert.InRange(alpha, -0.01, 1.01);

            // … and every other channel must agree with it, or this is not the page showing through.
            for (int c = 0; c < 3; c++)
                Assert.True(Math.Abs((a[i + c] - (double)b[i + c]) - alpha * gap[c]) <= tolerance + 0.5,
                    $"the pixel at ({(i / 4) % PixelsWide}, {(i / 4) / PixelsWide}) changed with the "
                  + "page in a way no amount of page showing through explains, so something in the map "
                  + "is reading the background rather than covering it.");
        }

        Assert.True(identicalPaint > 500,
            $"only {identicalPaint} pixels are opaque paint, so this gate is not looking at a map.");
        Assert.True(blended > 0,
            "the two renders are identical everywhere, including the page — the backgrounds were "
          + "never different, so this proves nothing.");
    }

    private static bool IsColour(byte[] pixels, int i, SKColor colour)
    {
        // SKSurface.Create with no explicit SKImageInfo colour type gives the platform's native order;
        // matched by VALUE in both orders rather than assumed, because which one it is varies and the
        // assertion is about equality, not about channel identity.
        bool rgba = pixels[i] == colour.Red && pixels[i + 1] == colour.Green && pixels[i + 2] == colour.Blue;
        bool bgra = pixels[i] == colour.Blue && pixels[i + 1] == colour.Green && pixels[i + 2] == colour.Red;
        return rgba || bgra;
    }

    /// <summary>Two renders of one scene are byte-identical — brief 9's clipboard gate and brief 17's
    /// documentation figures both depend on it.</summary>
    [Fact]
    public void TheSameResultRenderedTwiceIsByteIdentical()
    {
        var overlay = WithResult(out _);

        foreach (var kind in new[] { RailMapKind.Copper, RailMapKind.Drop, RailMapKind.Class })
        {
            overlay.Kind = kind;
            Assert.Equal(RenderOverlay(overlay, SKColors.White), RenderOverlay(overlay, SKColors.White));
        }
    }

    // ══ R-rail8-11 — the classification is a REQUIREMENT, and it is correctable ══════════════

    /// <summary>
    /// Forcing a region changes its draw state and its <c>Forced</c> flag, and the override survives a
    /// document round trip.
    /// </summary>
    /// <remarks>
    /// §2.9 rule 2: "a silent misclassification is the one failure mode of this design" — a wide supply
    /// polygon mistaken for a trace is optimistic and the number looks entirely ordinary. Drawing it is
    /// what makes it neither, and an override that did not survive the file would put the design back
    /// where it started the next time it was opened.
    /// </remarks>
    [Fact]
    public void ForcingARegion_ChangesItsDrawState_AndSurvivesADocumentRoundTrip()
    {
        var overlay = WithResult(out _);
        overlay.Kind = RailMapKind.Class;

        var region = overlay.Scene.Regions.First(r => !r.Forced);

        // What the overlay ASKS for. A callback, because the override is a DOCUMENT edit and a
        // re-solve, and neither belongs to a thing that draws.
        PdnRegionRef? asked = null;
        PdnCopperClass? askedClass = null;
        overlay.ForceRegion = (r, c) => { asked = r; askedClass = c; };

        var rows = overlay.BuildContextMenuItems(
            (region.Bounds.MinX + region.Bounds.MaxX) / 2.0,
            (region.Bounds.MinY + region.Bounds.MaxY) / 2.0,
            Um(20), null, new Border());

        var items = rows.OfType<MenuItem>().ToList();
        Assert.Equal(3, items.Count);
        Assert.All(items, m => Assert.Equal(MenuItemToggleType.Radio, m.ToggleType));

        // Before any override the ticked row is "use the measured classification" — an override the
        // user cannot see is one they cannot clear.
        Assert.Same(items[2], Assert.Single(items.Where(m => m.IsChecked)));

        items[0].Command!.Execute(null);                       // "treat as a trace"
        Assert.Equal(region.Region, asked);
        Assert.Equal(PdnCopperClass.Trace, askedClass);

        // ── The override travels: the document keys it the way the classifier keys it ─────────
        var doc = new RailDocument { Name = "forced" };
        doc.Rails.Add(new RailSpec { Name = "VDD" });
        doc.ClassOverrides[region.Region] = PdnCopperClass.Trace;

        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".crail");
        try
        {
            RailDocumentIo.SaveToFile(path, doc);
            var read = RailDocumentIo.LoadFromFile(path);
            Assert.Equal(PdnCopperClass.Trace, Assert.Contains(region.Region, read.ClassOverrides));
        }
        finally { File.Delete(path); }

        // ── …and the PICTURE then draws it differently ─────────────────────────────────
        var forced = WithOverrides(doc.ClassOverrides, out _);
        var drawn = RailMapScene.Build(forced, RailMapKind.Class, Dbu)
                                .Regions.Single(r => r.Region == region.Region);

        Assert.True(drawn.Forced);
        Assert.NotEqual(region.Forced, drawn.Forced);
        Assert.Equal(PdnCopperClass.Trace, drawn.Class);
    }

    /// <summary>
    /// The other direction of the same override, taken at brief 4's own function rather than through
    /// a solve.
    /// </summary>
    /// <remarks>
    /// <b>Forcing this fixture's trace to <c>Spreading</c> is a REFUSAL, not a different number</b> —
    /// R-rail4-5: the closed form has no bounded error across copper the current fans out in, and the
    /// error it would make is optimistic, so the fast model produces no number rather than a smaller
    /// one. That is correct and it is why the flip is asserted on the classification rather than on a
    /// re-solve: what the class tab has to be able to say is "you overrode this, and here is what was
    /// measured", which is exactly what the record carries.
    /// </remarks>
    [Fact]
    public void ForcingTheOtherWayFlipsTheClassAndKeepsWhatWasMeasured()
    {
        var copper = new Clipper2Lib.Paths64
        {
            new Clipper2Lib.Path64
            {
                new(0, 0), new(Mm(30), 0), new(Mm(30), Mm(0.4)), new(0, Mm(0.4)),
            },
        };

        var id = PdnCopperClassifier.RefOf(Top, copper);

        var inferred = Assert.Single(PdnCopperClassifier.Classify(
            Top, copper, isReference: false, dbuPerMetre: Dbu * 1e6,
            overrides: null, PdnCopperClassifier.TraceSquaresThreshold));

        Assert.Equal(PdnCopperClass.Trace, inferred.Class);
        Assert.False(inferred.Forced);

        var flipped = Assert.Single(PdnCopperClassifier.Classify(
            Top, copper, isReference: false, dbuPerMetre: Dbu * 1e6,
            overrides: new Dictionary<PdnRegionRef, PdnCopperClass> { [id] = PdnCopperClass.Spreading },
            PdnCopperClassifier.TraceSquaresThreshold));

        Assert.Equal(PdnCopperClass.Spreading, flipped.Class);
        Assert.True(flipped.Forced);
        Assert.Equal(PdnCopperClass.Trace, flipped.Inferred);   // what was MEASURED is still there
        Assert.Contains("FORCED", flipped.Reason);
    }

    // ══ R-rail8-12 — the tabs switch the OVERLAY, never the geometry ═════════════════════════

    /// <summary>Switching tabs leaves the viewport unchanged — a tab that rebuilt the canvas would
    /// drop the viewport and undo R-rail8-8.</summary>
    [Fact]
    public void SwitchingTabsLeavesTheViewportAlone()
    {
        var (canvas, overlay, _) = BoardView();
        overlay.Result = ResultOf(out _);

        Wheel(canvas, 90, 190, +1);                 // somewhere that is not the fit
        var held = canvas.CurrentViewport;

        foreach (var kind in new[] { RailMapKind.Drop, RailMapKind.Impedance, RailMapKind.Class,
                                     RailMapKind.Copper })
        {
            overlay.Kind = kind;
            Assert.Equal(held, canvas.CurrentViewport);
        }

        // And it really did change the picture, so this is not passing because nothing happened.
        overlay.Kind = RailMapKind.Drop;
        Assert.NotEmpty(overlay.Scene.Tiles);
        overlay.Kind = RailMapKind.Class;
        Assert.Empty(overlay.Scene.Tiles);
    }

    // ══ R-rail8-8 — the viewport is per document and it persists ═════════════════════════════

    /// <summary>
    /// The board's viewport survives the canvas being re-realised, through the mechanism the layout
    /// editor already uses — <c>LayoutEditorViewModel.LastViewport</c>, which outlives the control.
    /// </summary>
    [Fact]
    public void TheBoardViewportSurvivesTheCanvasBeingRebuilt()
    {
        var (canvas, _, _) = BoardView();
        Wheel(canvas, 300, 60, +1);
        var held = canvas.CurrentViewport;

        var session = canvas.ViewModel!;
        Assert.Equal(held, session.LastViewport);

        // A new canvas over the SAME session — what a dock rebuild, a tear-off or a re-open does.
        CursorFactoryShim.Install();
        var reborn = new LayoutCanvas { ViewModel = session };
        Lay(reborn);

        Assert.Equal(held.PanX, reborn.CurrentViewport.PanX, 6);
        Assert.Equal(held.PanY, reborn.CurrentViewport.PanY, 6);
        Assert.Equal(held.Zoom, reborn.CurrentViewport.Zoom, 12);
    }

    // ══ the readout, which is why the field has to be interpolable ═══════════════════════════

    /// <summary>
    /// The cursor reads out the absolute voltage <b>anywhere on the net</b>, and it is
    /// <see cref="RailDcResult.VoltageAt"/>'s answer rather than the tile's.
    /// </summary>
    /// <remarks>
    /// R-rail5-2 put the interpolation rule in exactly one place so the window and the headless report
    /// cannot differ at the same coordinate. A readout that quoted the tile under the pointer would be
    /// a second rule, and the two would agree at cell centres and nowhere else — which is the kind of
    /// disagreement nobody finds by looking at either one.
    /// </remarks>
    [Fact]
    public void TheReadoutIsTheFieldsOwnAnswer_BetweenCellsAsWellAsAtThem()
    {
        var overlay = WithResult(out var result);
        overlay.Kind = RailMapKind.Drop;

        // Deliberately not a cell centre and not on a round number.
        long x = Mm(13.731), y = Mm(0.217);
        overlay.OnPointerMoved(x, y, Um(20), leftButtonDown: false, KeyModifiers.None);

        double expected = Assert.NotNull(result.VoltageAt(Top, isReference: false, x, y));

        Assert.NotNull(overlay.Readout);
        Assert.Contains($"{expected:0.####} V", overlay.Readout);

        // …and it is not simply the nearest cell's own value, which is the second rule R-rail5-2
        // exists to prevent: interpolating between cells is the whole reason that method takes a
        // coordinate rather than a node.
        double nearest = NearestCellVoltage(result, Top, x, y);
        Assert.NotEqual(nearest, expected, 9);
    }

    // ══ helpers ══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Raises the canvas's own <c>LostFocus</c>.
    /// </summary>
    /// <remarks>
    /// The event's args type is <c>FocusChangedEventArgs</c>, whose constructors are not public, and
    /// Avalonia's routing casts to it — so the instance is made uninitialised and given its routed
    /// event. Nothing on it is read by <c>OnCanvasLostFocus</c>, which takes the event as a signal and
    /// not as data.
    /// </remarks>
    private static void LoseFocus(LayoutCanvas canvas)
    {
        var args = (Avalonia.Interactivity.RoutedEventArgs)System.Runtime.CompilerServices
            .RuntimeHelpers.GetUninitializedObject(typeof(FocusChangedEventArgs));
        args.RoutedEvent = InputElement.LostFocusEvent;
        canvas.RaiseEvent(args);
    }

    /// <summary>The value at the nearest cell centre — what a readout that quoted the tile under the
    /// pointer would have said.</summary>
    private static double NearestCellVoltage(RailDcResult result, LayerKey layer, long x, long y)
    {
        double best = double.MaxValue, value = 0;
        foreach (var (node, cell) in result.Netlist.NodeCells)
        {
            if (cell.IsReference || cell.Layer != layer) continue;
            if (!result.NodeVoltages.TryGetValue(node, out double v)) continue;

            double dx = cell.CentreX - x, dy = cell.CentreY - y;
            double d2 = dx * dx + dy * dy;
            if (d2 < best) { best = d2; value = v; }
        }
        return value;
    }

    private static void Lay(LayoutCanvas canvas)
    {
        canvas.Measure(new Size(CanvasWidth, CanvasHeight));
        canvas.Arrange(new Rect(0, 0, CanvasWidth, CanvasHeight));
    }

    private static KeyEventArgs Key(LayoutCanvas canvas, Avalonia.Input.Key key,
                                    KeyModifiers modifiers = KeyModifiers.None)
    {
        var args = new KeyEventArgs
        {
            Key = key, KeyModifiers = modifiers, RoutedEvent = InputElement.KeyDownEvent,
        };
        canvas.RaiseEvent(args);
        return args;
    }

    private static void Wheel(LayoutCanvas canvas, double x, double y, double delta) =>
        canvas.RaiseEvent(new PointerWheelEventArgs(
            canvas, new Avalonia.Input.Pointer(1, PointerType.Mouse, true), canvas, new Point(x, y), 0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.Other),
            KeyModifiers.None, new Vector(0, delta)));

    private static void Drag(LayoutCanvas canvas, Point from, Point to,
                             RawInputModifiers button, PointerUpdateKind pressed)
    {
        var pointer = new Avalonia.Input.Pointer(1, PointerType.Mouse, true);

        canvas.RaiseEvent(new PointerPressedEventArgs(
            canvas, pointer, canvas, from, 0,
            new PointerPointProperties(button, pressed), KeyModifiers.None, 1));

        canvas.RaiseEvent(new PointerEventArgs(
            InputElement.PointerMovedEvent, canvas, pointer, canvas, to, 0,
            new PointerPointProperties(button, PointerUpdateKind.Other), KeyModifiers.None));

        canvas.RaiseEvent(new PointerReleasedEventArgs(
            canvas, pointer, canvas, to, 0,
            new PointerPointProperties(RawInputModifiers.None, Released(pressed)), KeyModifiers.None,
            pressed == PointerUpdateKind.MiddleButtonPressed ? MouseButton.Middle : MouseButton.Left));
    }

    private static PointerUpdateKind Released(PointerUpdateKind pressed) =>
        pressed == PointerUpdateKind.MiddleButtonPressed
            ? PointerUpdateKind.MiddleButtonReleased
            : PointerUpdateKind.LeftButtonReleased;

    /// <summary>An overlay carrying a real solved rail — the one the map tests draw.</summary>
    private static RailLayoutOverlay WithResult(out RailDcResult result)
    {
        result = ResultOf(out _);
        return new RailLayoutOverlay { Result = result, DbuPerMicron = Dbu };
    }

    private static RailDcResult ResultOf(out RailDcRunResult run) =>
        WithOverrides(new Dictionary<PdnRegionRef, PdnCopperClass>(), out run);

    /// <summary>
    /// One straight supply trace over a reference strip, a source at one end and a load at the other
    /// — solved by <see cref="RailDcRun"/> itself, so the scene is built from a real extraction
    /// rather than from a hand-made result that could not have come out of one.
    /// </summary>
    private static RailDcResult WithOverrides(
        IReadOnlyDictionary<PdnRegionRef, PdnCopperClass> overrides, out RailDcRunResult run)
    {
        var doc = new RailDocument { Name = "board view" };
        foreach (var (k, v) in overrides) doc.ClassOverrides[k] = v;

        var rail = new RailSpec { Name = "VDD", ReferenceLayer = Bot };
        rail.Sources.Add(new RailSource
        {
            Anchor = new RailPortAnchor { Refdes = "BT1", Pin = "1" },
            OpenCircuitVoltageV = 3.7,
            SeriesResistanceOhms = 0.2,
        });
        rail.Loads.Add(new RailLoad
        {
            Anchor = new RailPortAnchor { Refdes = "U1", Pin = "VDD" },
            DcCurrentA = 0.12,
        });
        doc.Rails.Add(rail);

        run = RailDcRun.Run(new RailDcRequest
        {
            Document = doc,
            Technology = TestBoard(),
            DbuPerMicron = Dbu,
            Shapes =
            [
                new RectShape { Layer = Top, X1 = 0, Y1 = 0, X2 = Mm(30), Y2 = Mm(0.4) },
                new RectShape { Layer = Bot, X1 = 0, Y1 = 0, X2 = Mm(30), Y2 = Mm(0.4) },
            ],
            Pads =
            [
                new PdnPad("BT1", "1", "VDD", Mm(0.2), Mm(0.2)),
                new PdnPad("U1", "VDD", "VDD", Mm(29.8), Mm(0.2)),
            ],
        });

        Assert.Null(run.Refusal);
        return run.Rails[0];
    }

    private static Technology TestBoard()
    {
        var tech = new Technology { Name = "test board" };
        tech.Stackup.Layers =
        [
            new StackupLayer
            {
                Kind = StackupKind.Conductor, Name = "TOP",
                ThicknessDbu = Um(35), SigmaSm = 5.8e7, DrawingLayers = [Top],
            },
            new StackupLayer
            {
                Kind = StackupKind.Dielectric, Name = "CORE",
                ThicknessDbu = Mm(1.6), Epsr = 4.3, TanD = 0.02,
            },
            new StackupLayer
            {
                Kind = StackupKind.Conductor, Name = "BOT",
                ThicknessDbu = Um(35), SigmaSm = 5.8e7, DrawingLayers = [Bot],
                IsGroundReference = true,
            },
        ];
        return tech;
    }

    private static Bbox CopperBounds(RailDcResult result)
    {
        var bb = Bbox.Empty;
        foreach (var island in result.Regions!.Power)
            bb = bb.Union(island.Bounds);
        return bb;
    }

    private const int PixelsWide = 320, PixelsHigh = 240;

    private static byte[] RenderOverlay(RailLayoutOverlay overlay, SKColor background)
    {
        using var surface = SKSurface.Create(new SKImageInfo(PixelsWide, PixelsHigh));
        surface.Canvas.Clear(background);

        var bounds = overlay.Scene.Bounds;
        var viewport = bounds.IsEmpty
            ? new LayoutViewport(0, 0, 1, PixelsWide, PixelsHigh)
            : LayoutViewport.ZoomToFit(bounds, PixelsWide, PixelsHigh);

        overlay.Draw(surface.Canvas, viewport, LayoutRenderTheme.Light);
        surface.Canvas.Flush();

        using var image = surface.Snapshot();
        using var pixels = image.PeekPixels();
        return pixels.GetPixelSpan().ToArray();
    }

    /// <summary>The pixels of <paramref name="a"/> that differ from <paramref name="b"/> replaced by a
    /// sentinel, so two renders can be compared over the region both of them PAINTED.</summary>
    private static byte[] PaintedOnly(byte[] a, byte[] b)
    {
        var copy = (byte[])a.Clone();
        for (int i = 0; i < copy.Length; i++) if (copy[i] != b[i]) copy[i] = 0;
        return copy;
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine([RepoRoot(), .. parts]));

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "circuitrf.slnx")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return dir!;
    }

    /// <summary>An overlay that answers <see cref="ILayoutCanvasOverlay.ContentBounds"/> with whatever
    /// it was given — the negative half of R-rail8-7.</summary>
    private sealed class StubOverlay(Bbox bounds) : ILayoutCanvasOverlay
    {
        public void Draw(SKCanvas canvas, LayoutViewport viewport, LayoutRenderTheme theme) { }
        public Bbox ContentBounds() => bounds;
        public bool CompanionPressResolvedNewSelection { get; set; }
        public bool OnPointerPressed(long x, long y, long tol, KeyModifiers m, int clicks) => false;
        public bool OnPointerMoved(long x, long y, long tol, bool left, KeyModifiers m) => false;
        public bool OnPointerReleased(long x, long y) => false;
        public bool OnKeyDown(Avalonia.Input.Key key, KeyModifiers m) => false;
        public void OnKeyUp(Avalonia.Input.Key key, KeyModifiers m) { }
    }

    /// <summary>
    /// Stands Avalonia's cursor factory in, and nothing else.
    /// </summary>
    /// <remarks>
    /// <c>LayoutCanvas.SetCursor</c> constructs a real <c>Cursor</c>, which resolves
    /// <c>ICursorFactory</c> through the locator — absent with no application host, so Space-to-pan and
    /// the middle-drag pan would throw on their first line and two of §11.6's seven gestures could not
    /// be driven at all. <c>ICursorFactory</c> is marked not-implementable by client code, hence the
    /// <see cref="DispatchProxy"/>; the locator's registration API is internal, hence the reflection.
    /// Installed once per process and never uninstalled, because nothing else in this assembly asks
    /// for a cursor.
    /// </remarks>
    private static class CursorFactoryShim
    {
        private static bool _installed;

        public class Proxy : DispatchProxy
        {
            protected override object? Invoke(MethodInfo? method, object?[]? args) =>
                method?.ReturnType == typeof(ICursorImpl) ? Create<ICursorImpl, Proxy>() : null;
        }

        public static void Install()
        {
            if (_installed) return;

            var locator = typeof(AvaloniaLocator);
            object current = locator
                .GetProperty("CurrentMutable", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!
                .GetValue(null)!;

            object registration = locator
                .GetMethod("Bind", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
                .MakeGenericMethod(typeof(ICursorFactory))
                .Invoke(current, null)!;

            var toConstant = registration.GetType().GetMethod("ToConstant")!;
            if (toConstant.IsGenericMethodDefinition)
                toConstant = toConstant.MakeGenericMethod(typeof(ICursorFactory));

            toConstant.Invoke(registration, [DispatchProxy.Create<ICursorFactory, Proxy>()]);
            _installed = true;
        }
    }
}

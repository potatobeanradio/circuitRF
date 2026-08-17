using System;
using System.Linq;
using Avalonia.Input;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.WBond;
using CircuitRF.WBond;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// The owner-reported wBond editor defects, each pinned by the smallest thing that can tell the fix
/// from the bug.
///
/// <para>Three of the reported items — Escape's three behaviours — live in
/// <c>WBondEditorView</c>'s own key handler and touch ToggleButton state, so they are not reachable
/// from this project (Ui.Tests calls no Avalonia runtime API, by the project's own rule). They are
/// covered by the code-behind's own documentation rather than by a test here.</para>
/// </summary>
public class WBondEditorFixesTests
{
    private static WBondDesign Design(int wires = 3, double azimuthDegrees = 0.0)
    {
        var profile = LoopProfile.BallBond(WBondUnits.ToNm(20.0, WBondUnit.Mil), points: 7);
        var design = new WBondDesign();
        design.Profiles.Add(profile);

        double radians = azimuthDegrees * Math.PI / 180.0;
        double cos = Math.Cos(radians), sin = Math.Sin(radians);

        var array = new WireArray { Name = "G1", Profile = profile.Name };
        for (int w = 0; w < wires; w++)
        {
            // Wires laid end-to-end along the requested azimuth, offset perpendicular to it.
            double ox = -sin * w * 6, oy = cos * w * 6;
            array.Wires.Add(profile.CreateWire(
                Point3.Mils(ox, oy, 4),
                Point3.Mils(ox + 100 * cos, oy + 100 * sin, 1),
                WBondUnits.ToNm(1.0, WBondUnit.Mil), "Gold"));
        }

        design.Arrays.Add(array);
        return design;
    }

    // ---------------------------------------------------------------- panel units (§6.5)

    /// <summary>
    /// <b>The panel's length rows follow the display unit.</b> They were hard-coded to millimetres, so
    /// a document set to mil reported its total wire length in mm with no way to tell from the panel.
    /// </summary>
    [Theory]
    [InlineData(WBondUnit.Mil, "mil")]
    [InlineData(WBondUnit.Um, "um")]
    [InlineData(WBondUnit.Mm, "mm")]
    [InlineData(WBondUnit.Inch, "in")]
    public void ThePanelsLengthRows_AreInTheDisplayUnit(WBondUnit unit, string suffix)
    {
        var document = new WBondDocumentViewModel(new WBondViewModel(Design(wires: 2)));
        document.Editor.DisplayUnit = unit;

        var row = Assert.Single(document.Panel.Rows);

        // Every length row, including the three settable ones the 2026-08-16 round added — "Landing
        // span" was replaced by the wires' own median Span, which is a length like the rest.
        Assert.EndsWith(" " + suffix, row.TotalLength, StringComparison.Ordinal);
        Assert.EndsWith(" " + suffix, row.Span, StringComparison.Ordinal);
        Assert.EndsWith(" " + suffix, row.LoopHeight, StringComparison.Ordinal);
        Assert.EndsWith(" " + suffix, row.Diameter, StringComparison.Ordinal);
    }

    /// <summary>
    /// Switching the unit re-formats what is ALREADY on screen. Waiting for the next edit would leave
    /// the panel showing one unit while the toolbar says another — the same defect, one step later.
    /// </summary>
    [Fact]
    public void ChangingTheDisplayUnit_ReformatsThePanelImmediately()
    {
        var document = new WBondDocumentViewModel(new WBondViewModel(Design(wires: 2)));

        document.Editor.DisplayUnit = WBondUnit.Mm;
        string inMillimetres = document.Panel.Rows[0].TotalLength;

        document.Editor.DisplayUnit = WBondUnit.Mil;
        string inMils = document.Panel.Rows[0].TotalLength;

        Assert.NotEqual(inMillimetres, inMils);
        Assert.EndsWith(" mil", inMils, StringComparison.Ordinal);
    }

    /// <summary>
    /// The conversion itself: 100 mil of wire is 2.54 mm, whichever unit it is asked for in — and each
    /// unit is shown at ITS OWN precision, so one digit is worth roughly the same physical amount
    /// everywhere. Mil is the owner's stated case and is pinned at one decimal.
    /// </summary>
    [Fact]
    public void FormatLength_ConvertsAtEachUnitsOwnPrecision()
    {
        Assert.Equal("2.540 mm", WBondPanelViewModel.FormatLength(2.54, WBondUnit.Mm));
        Assert.Equal("100.0 mil", WBondPanelViewModel.FormatLength(2.54, WBondUnit.Mil));
        Assert.Equal("2540.0 um", WBondPanelViewModel.FormatLength(2.54, WBondUnit.Um));
        Assert.Equal("0.1000 in", WBondPanelViewModel.FormatLength(2.54, WBondUnit.Inch));
        Assert.Equal("2540000 nm", WBondPanelViewModel.FormatLength(2.54, WBondUnit.Nm));
    }

    /// <summary>Mil gets exactly one decimal — stated separately because it is the owner's own rule.</summary>
    [Fact]
    public void MilIsShownToOneDecimal()
    {
        Assert.Equal(1, WBondPanelViewModel.Decimals(WBondUnit.Mil));
        Assert.Equal("12.3 mil", WBondPanelViewModel.FormatLength(12.3456 * 0.0254, WBondUnit.Mil));
    }

    /// <summary>Inductance is one decimal too — the same readout, the same legibility rule.</summary>
    [Fact]
    public void Picohenries_AreShownToOneDecimal()
    {
        Assert.Equal("123.5 pH", WBondPanelViewModel.FormatPicoHenries(123.456));
    }

    /// <summary>
    /// Inductance is NOT unit-following (WB27a / D9) — the panel exists to compare inductances during
    /// a drag, and a pH that became nH mid-drag would destroy that. Stated as a test so the length fix
    /// above cannot be "tidied" into applying here too.
    /// </summary>
    [Fact]
    public void Inductance_StaysInPicohenries_WhateverTheDisplayUnit()
    {
        var document = new WBondDocumentViewModel(new WBondViewModel(Design(wires: 2)));

        foreach (var unit in Enum.GetValues<WBondUnit>())
        {
            document.Editor.DisplayUnit = unit;
            Assert.EndsWith(" pH", document.Panel.Rows[0].Self, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The toolbar picker's labels are the SUFFIXES — lower case, and exactly what the parser accepts,
    /// so the picker and the "type a value with a unit" fields can never disagree.
    /// </summary>
    [Fact]
    public void TheUnitLabels_AreLowercaseAndParseBackToTheirOwnUnit()
    {
        foreach (var unit in Enum.GetValues<WBondUnit>())
        {
            string label = WBondUnits.Suffix(unit);

            Assert.Equal(label.ToLowerInvariant(), label);
            Assert.True(WBondUnits.TryParseUnit(label, out var round));
            Assert.Equal(unit, round);
        }
    }

    // ---------------------------------------------------------------- the profile view's axis

    /// <summary>
    /// The profile plane is the USER's setting, not something derived from the geometry — the toolbar
    /// combo's text goes in, an azimuth comes out, and the canonical spelling comes back.
    /// </summary>
    [Theory]
    [InlineData("Auto", null, "Auto")]
    [InlineData("", null, "Auto")]            // an emptied box means the default
    [InlineData("XZ", 0.0, "XZ")]
    [InlineData("X-Z", 0.0, "XZ")]      // the old hyphenated spelling is still read
    [InlineData("xz", 0.0, "XZ")]
    [InlineData("YZ", 90.0, "YZ")]
    [InlineData("90", 90.0, "YZ")]            // a typed right angle IS the YZ plane
    [InlineData("45", 45.0, "45°")]
    [InlineData("37.5°", 37.5, "37.5°")]      // its own output is accepted back
    [InlineData("-90 deg", -90.0, "YZ")]      // a plane and its opposite are one plane
    public void TheProfilePlane_RoundTripsThroughTheToolbarsOwnText(
        string typed, double? expectedDegrees, string shownBack)
    {
        var vm = new WBondViewModel(Design(wires: 1));

        Assert.True(vm.CommitProfileAxisText(typed));

        if (expectedDegrees is null) Assert.Null(vm.ProfileAzimuthRadians);
        else Assert.Equal(expectedDegrees.Value, vm.ProfileAzimuthRadians!.Value * 180.0 / Math.PI, 9);

        Assert.Equal(shownBack, vm.ProfileAxisText);
    }

    /// <summary>Text that means no plane is REFUSED, so the view can put the box back.</summary>
    [Fact]
    public void TheProfilePlane_RefusesTextThatIsNotAPlane()
    {
        var vm = new WBondViewModel(Design(wires: 1));
        vm.CommitProfileAxisText("YZ");

        Assert.False(vm.CommitProfileAxisText("sideways"));
        Assert.Equal("YZ", vm.ProfileAxisText);    // unchanged
    }

    /// <summary>
    /// <b>A fixed plane really is a projection.</b> A wire running north-south has no extent in the
    /// XZ plane, and the profile view must draw it foreshortened to nothing rather than quietly
    /// falling back on its own chord — which would show a picture the plane setting does not describe.
    /// </summary>
    [Fact]
    public void AFixedPlane_ForeshortensAWirePerpendicularToIt()
    {
        var wire = Design(wires: 1, azimuthDegrees: 90.0).AllWires().First();
        int last = wire.Points.Count - 1;

        var alongY = ProfileProjection.Project(wire, last, ProfileProjection.SpanMode.Absolute,
                                               azimuthRadians: Math.PI / 2.0);
        var alongX = ProfileProjection.Project(wire, last, ProfileProjection.SpanMode.Absolute,
                                               azimuthRadians: 0.0);

        Assert.Equal(WBondUnits.ToNm(100, WBondUnit.Mil), alongY.Span, 0);
        Assert.Equal(0.0, alongX.Span, 0);
    }

    /// <summary>
    /// <b>The profile view's absolute axis has a FIXED origin — moving the input foot moves the input
    /// foot.</b>
    ///
    /// <para>It used to be measured from the wire's own <c>Points[0]</c>, which put that point at span
    /// 0 permanently: it could not move in the view no matter what happened to it in the world, and
    /// any motion of it was rendered as motion of everything ELSE. That single fact is both of the
    /// owner's reports — an alt-drag anchored on the output foot drew the output foot moving (while
    /// the layout view drew the truth, so the two views disagreed), and dragging the start point
    /// horizontally left it glued in place while the rest of the curve slid.</para>
    /// </summary>
    [Fact]
    public void MovingTheInputFoot_MovesItInTheProfileView_AndLeavesTheOutputFootAlone()
    {
        var design = Design(wires: 1);
        var wire = design.AllWires().First();
        int last = wire.Points.Count - 1;

        double inputBefore = ProfileProjection.Project(wire, 0).Span;
        double outputBefore = ProfileProjection.Project(wire, last).Span;

        // The alt-drag anchor case: pin the OUTPUT foot, move the input one.
        WireEdits.ScaleSpan(wire, 0.5, moveOutputFoot: false);

        Assert.Equal(outputBefore, ProfileProjection.Project(wire, last).Span, 0);
        Assert.NotEqual(inputBefore, ProfileProjection.Project(wire, 0).Span);
    }

    /// <summary>
    /// <b>The two views agree about which foot moved.</b> That is the invariant the owner's report was
    /// really about — the layout view drew the anchor correctly while the profile view drew the
    /// opposite one, for the same gesture, at the same moment.
    /// </summary>
    [Theory]
    [InlineData(true)]    // the output foot moves
    [InlineData(false)]   // the input foot moves
    public void BothViewsAgreeAboutWhichFootAnAltDragMoved(bool moveOutputFoot)
    {
        var design = Design(wires: 1);
        var wire = design.AllWires().First();
        int last = wire.Points.Count - 1;

        var worldBefore = new[] { wire.Points[0], wire.Points[last] };
        double[] spanBefore = [ProfileProjection.Project(wire, 0).Span,
                               ProfileProjection.Project(wire, last).Span];

        WireEdits.ScaleSpan(wire, 1.6, moveOutputFoot);

        for (int end = 0; end < 2; end++)
        {
            int index = end == 0 ? 0 : last;
            bool movedInWorld = wire.Points[index] != worldBefore[end];
            bool movedInProfile = ProfileProjection.Project(wire, index).Span != spanBefore[end];

            Assert.Equal(movedInWorld, movedInProfile);
        }
    }

    /// <summary>The other half of the same fact: pinning the input foot leaves IT alone.</summary>
    [Fact]
    public void MovingTheOutputFoot_LeavesTheInputFootAlone()
    {
        var design = Design(wires: 1);
        var wire = design.AllWires().First();
        int last = wire.Points.Count - 1;

        double inputBefore = ProfileProjection.Project(wire, 0).Span;

        WireEdits.ScaleSpan(wire, 0.5, moveOutputFoot: true);

        Assert.Equal(inputBefore, ProfileProjection.Project(wire, 0).Span, 0);
    }

    /// <summary>
    /// A plain horizontal drag of the START point moves that point under the cursor by exactly the
    /// distance dragged — "it should just move the point". Every other point stays where it was.
    /// </summary>
    [Fact]
    public void DraggingTheStartPointHorizontally_MovesThatPointAndNothingElse()
    {
        var design = Design(wires: 1);
        var wire = design.AllWires().First();
        int last = wire.Points.Count - 1;

        double before = ProfileProjection.Project(wire, 0).Span;
        double outputBefore = ProfileProjection.Project(wire, last).Span;
        long step = WBondUnits.ToNm(10.0, WBondUnit.Mil);

        WireEdits.Translate(design, new WireSelection { Points = { new PointRef(0, 0) } },
                            step, 0, EditorView.Profile);

        Assert.Equal(before + step, ProfileProjection.Project(wire, 0).Span, 0);
        Assert.Equal(outputBefore, ProfileProjection.Project(wire, last).Span, 0);
    }

    /// <summary>AUTO puts every wire on its own chord, whichever way it runs — §6.2's parameterisation.</summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(37.0)]
    [InlineData(90.0)]
    public void AutoPutsEveryWireOnItsOwnChord(double azimuth)
    {
        var wire = Design(wires: 1, azimuthDegrees: azimuth).AllWires().First();

        var end = ProfileProjection.Project(wire, wire.Points.Count - 1,
                                            ProfileProjection.SpanMode.Absolute, azimuthRadians: null);

        Assert.Equal(WBondUnits.ToNm(100, WBondUnit.Mil), end.Span, 0);
    }

    // ---------------------------------------------------------------- horizontal drag in the profile

    /// <summary>
    /// <b>A horizontal drag in the profile view moves the wire along its own chord.</b> That is what
    /// the view's horizontal axis IS, so a wire running north-south moves in y and one running
    /// east-west moves in x. Treating the profile's dx as world x — the old behaviour — moved a
    /// north-south wire sideways OFF its chord and barely changed its span at all.
    /// </summary>
    [Theory]
    [InlineData(0.0, 1.0, 0.0)]      // east-west wire: dx lands in x
    [InlineData(90.0, 0.0, 1.0)]     // north-south wire: dx lands in y
    [InlineData(180.0, -1.0, 0.0)]   // reversed: the chord direction, not its absolute value
    public void AProfileHorizontalDrag_MovesAlongTheWiresOwnChord(
        double azimuth, double expectedXFraction, double expectedYFraction)
    {
        var design = Design(wires: 1, azimuthDegrees: azimuth);
        var wire = design.AllWires().First();
        var before = wire.Points[3];

        long step = WBondUnits.ToNm(10.0, WBondUnit.Mil);
        WireEdits.Translate(design, new WireSelection { Wires = { 0 } }, step, 0, EditorView.Profile);

        var after = wire.Points[3];
        Assert.Equal((long)Math.Round(step * expectedXFraction), after.X - before.X);
        Assert.Equal((long)Math.Round(step * expectedYFraction), after.Y - before.Y);
        Assert.Equal(0L, after.Z - before.Z);   // horizontal only: z is untouched
    }

    /// <summary>The layout view is unchanged: there, dx is world x and dy is world y.</summary>
    [Fact]
    public void ALayoutDrag_StillMovesInWorldXAndY()
    {
        var design = Design(wires: 1, azimuthDegrees: 90.0);
        var wire = design.AllWires().First();
        var before = wire.Points[3];

        WireEdits.Translate(design, new WireSelection { Wires = { 0 } }, 111, 222, EditorView.Layout);

        Assert.Equal(111L, wire.Points[3].X - before.X);
        Assert.Equal(222L, wire.Points[3].Y - before.Y);
    }

    /// <summary>
    /// A wire whose feet coincide in XY has no chord direction, so the horizontal component is skipped
    /// rather than guessed — a vertical bond wire must not shoot off in an arbitrary direction.
    /// </summary>
    [Fact]
    public void AProfileHorizontalDrag_IsRefusedForAWireWithNoXyChord()
    {
        var design = Design(wires: 1);
        var wire = design.AllWires().First();
        for (int i = 0; i < wire.Points.Count; i++)
            wire.Points[i] = new Point3(0, 0, wire.Points[i].Z);

        var before = wire.Points[3];
        WireEdits.Translate(design, new WireSelection { Wires = { 0 } }, 50_000, 0, EditorView.Profile);

        Assert.Equal(before, wire.Points[3]);
    }

    // ---------------------------------------------------------------- the live marquee

    private static WBondLayoutOverlay Overlay(WBondViewModel vm) => new(vm) { SnapEnabled = false };

    /// <summary>
    /// <b>The marquee highlight is live.</b> It used to resolve only at release, so the user dragged a
    /// box over their wires with no indication of what was in it.
    /// </summary>
    [Fact]
    public void TheMarqueeHighlight_UpdatesWhileTheBoxIsDragged()
    {
        var vm = new WBondViewModel(Design(wires: 3));
        var overlay = Overlay(vm);

        long far = WBondUnits.ToNm(500.0, WBondUnit.Mil);

        // Press on empty space, well clear of every wire.
        overlay.OnPointerPressed(far, far, 0, KeyModifiers.None, 1);
        Assert.Empty(overlay.MarqueePreview!.TouchedWires());

        // Drag back across all three wires.
        overlay.OnPointerMoved(-far, -far, 0, leftButtonDown: true, KeyModifiers.None);

        Assert.Equal(3, overlay.MarqueePreview!.TouchedWires().Count);

        // ...and the COMMITTED selection is still untouched until release.
        Assert.True(vm.Selection.IsEmpty);
    }

    /// <summary>
    /// The preview and the commit come through one rule, so what the highlight showed is what the
    /// release gives. Two implementations would be two chances to disagree, and the disagreement would
    /// only ever be visible as "the marquee selected something else".
    /// </summary>
    [Fact]
    public void WhatTheMarqueePreviewed_IsWhatTheReleaseCommits()
    {
        var vm = new WBondViewModel(Design(wires: 3));
        var overlay = Overlay(vm);

        long far = WBondUnits.ToNm(500.0, WBondUnit.Mil);

        overlay.OnPointerPressed(far, far, 0, KeyModifiers.None, 1);
        overlay.OnPointerMoved(-far, -far, 0, leftButtonDown: true, KeyModifiers.None);

        var previewed = overlay.MarqueePreview!.TouchedWires().OrderBy(i => i).ToList();

        overlay.OnPointerReleased(-far, -far);

        Assert.Equal(previewed, vm.Selection.TouchedWires().OrderBy(i => i).ToList());
        Assert.Null(overlay.MarqueePreview);   // no stale highlight outlives the gesture
    }

    /// <summary>
    /// A Shift-marquee's base is the selection as it stood when the box STARTED, not the previewed
    /// one — otherwise the box accumulates its own previews and can never shrink again when dragged
    /// back over itself.
    /// </summary>
    [Fact]
    public void AShiftMarqueePreview_DoesNotAccumulateItsOwnPreviews()
    {
        var vm = new WBondViewModel(Design(wires: 3));
        var overlay = Overlay(vm);

        long far = WBondUnits.ToNm(500.0, WBondUnit.Mil);

        overlay.OnPointerPressed(far, far, 0, KeyModifiers.Shift, 1);
        overlay.OnPointerMoved(-far, -far, 0, leftButtonDown: true, KeyModifiers.Shift);
        Assert.Equal(3, overlay.MarqueePreview!.TouchedWires().Count);

        // Drag back to an empty box: the preview must go back to empty, not stay at three.
        overlay.OnPointerMoved(far, far, 0, leftButtonDown: true, KeyModifiers.Shift);
        Assert.Empty(overlay.MarqueePreview!.TouchedWires());
    }

    // ---------------------------------------------------------------- clipping and marquee style

    private static SKColor PixelAt(WBondLayoutOverlay overlay, LayoutViewport viewport,
                                   LayoutRenderTheme theme, int x, int y)
    {
        using var surface = SKSurface.Create(new SKImageInfo(800, 600));
        surface.Canvas.Clear(SKColors.Black);
        overlay.Draw(surface.Canvas, viewport, theme);
        surface.Canvas.Flush();

        using var image = surface.Snapshot();
        using var bitmap = SKBitmap.FromImage(image);
        return bitmap.GetPixel(x, y);
    }

    /// <summary>
    /// <b>The wire pass is clipped to the canvas.</b> Nothing else clips it — the layout underneath is
    /// culled against the viewport before it is drawn, but every wire in the design is drawn whether
    /// or not it is on screen. Unclipped, a wire outside the canvas painted straight over the
    /// inductance panel docked beside it.
    ///
    /// <para>The fixture is chosen so it can fail: a viewport 400 px wide inside an 800 px surface,
    /// with the wires framed to run off its right edge. The pixels between 400 and 800 are the panel's
    /// territory, and before the fix the wire was there.</para>
    /// </summary>
    [Fact]
    public void TheWirePass_DrawsNothingOutsideTheCanvasBounds()
    {
        var vm = new WBondViewModel(Design(wires: 3));
        var overlay = Overlay(vm);
        var theme = LayoutRenderTheme.Light;

        // 400 x 300 of canvas, zoomed so a 100 mil wire is ~1000 px long: it leaves the right edge.
        var viewport = new LayoutViewport { Zoom = 0.4e-3, PanX = -50, PanY = -50, Width = 400, Height = 300 };

        // On-canvas, the wires ARE drawn — otherwise this test would pass on an overlay that drew
        // nothing at all.
        int lit = 0;
        using (var surface = SKSurface.Create(new SKImageInfo(800, 600)))
        {
            surface.Canvas.Clear(SKColors.Black);
            overlay.Draw(surface.Canvas, viewport, theme);
            surface.Canvas.Flush();

            using var image = surface.Snapshot();
            using var bitmap = SKBitmap.FromImage(image);

            for (int y = 0; y < 300; y++)
                for (int x = 0; x < 400; x++)
                    if (bitmap.GetPixel(x, y) != SKColors.Black) lit++;

            for (int y = 0; y < 600; y++)
                for (int x = 0; x < 800; x++)
                    if (x >= 400 || y >= 300)
                        Assert.Equal(SKColors.Black, bitmap.GetPixel(x, y));
        }

        Assert.True(lit > 100, $"The wires must still be drawn inside the canvas; {lit} lit pixels.");
    }

    /// <summary>
    /// <b>The marquee is drawn in the LAYOUT theme's own selection accent</b>, handed down from the
    /// same theme object the layout underneath was drawn with. A second selection rectangle in a
    /// different colour reads as a different kind of selection; this is the wBond editor's own canvas
    /// showing the layout editor's own artwork, so there is only one kind.
    /// </summary>
    [Fact]
    public void TheMarquee_UsesTheLayoutThemesSelectionAccent()
    {
        var vm = new WBondViewModel(Design(wires: 1));
        var overlay = Overlay(vm);
        var theme = LayoutRenderTheme.Light;
        var viewport = new LayoutViewport { Zoom = 1e-3, PanX = -100, PanY = -100, Width = 800, Height = 600 };

        // A marquee dragged left-to-right (enclose: a solid edge, so a pixel ON the edge is the stroke).
        long a = WBondSnap.ToNm((long)viewport.ScreenToWorldX(200), 1000);
        long b = WBondSnap.ToNm((long)viewport.ScreenToWorldY(400), 1000);
        long c = WBondSnap.ToNm((long)viewport.ScreenToWorldX(600), 1000);
        long d = WBondSnap.ToNm((long)viewport.ScreenToWorldY(200), 1000);

        overlay.OnPointerPressed(a, b, 0, KeyModifiers.None, 1);
        overlay.OnPointerMoved(c, d, 0, leftButtonDown: true, KeyModifiers.None);

        // Inside the box, away from any wire: the translucent fill over black.
        var inside = PixelAt(overlay, viewport, theme, 400, 300);

        Assert.NotEqual(SKColors.Black, inside);

        // Oracle: the accent at alpha 50 over black — the paint LayoutRenderer.DrawMarquee fills its
        // own marquee with, transcribed. Compared as a RENDERED pixel rather than as arithmetic, so
        // Skia's own blend rounding is on both sides of the comparison.
        Assert.Equal(BlendedOverBlack(theme.Selection.WithAlpha(50)), inside);
    }

    /// <summary>One flat colour composited over black by Skia — the reference the marquee fill must match.</summary>
    private static SKColor BlendedOverBlack(SKColor colour)
    {
        using var surface = SKSurface.Create(new SKImageInfo(4, 4));
        surface.Canvas.Clear(SKColors.Black);

        using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = colour };
        surface.Canvas.DrawRect(new SKRect(0, 0, 4, 4), paint);
        surface.Canvas.Flush();

        using var image = surface.Snapshot();
        using var bitmap = SKBitmap.FromImage(image);
        return bitmap.GetPixel(2, 2);
    }
}

using System.Linq;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.WBond;
using CircuitRF.WBond;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// The owner's EIGHTH batch (2026-08-19) — the parts of it that are testable without a window.
///
/// <para>Two of the six reports land here: the selected wire has to be VISIBLE in the profile view
/// (it was being drawn under its neighbours), and the Array Inductance panel's frequency row has to
/// hold its place when capacitance is switched off (it was vanishing and taking every self-inductance
/// card up a row with it, mid-comparison).</para>
///
/// <para>The other four are gated where they live: Ctrl/Cmd+A in <c>WBondOverlayTests</c>, the wire
/// point's size and hitbox in <c>WBondRound4Tests</c> and <c>WBond.Tests/HitTestAndLadderTests</c>,
/// the span gesture's z in <c>WBond.Tests/WireEditsTests</c>, and the snap glyph's z-order in
/// <c>LayoutSnapRenderingTests</c>.</para>
/// </summary>
public class WBondRound8Tests
{
    // ---------------------------------------------------------------- profile view z-order

    /// <summary>
    /// Two arrays, one wire each, laid on exactly the same geometry — the profile view's own version
    /// of the owner's report: <i>"sometimes other wires (even wires within its own Group) are rendered
    /// overtop of the selected wire, so user can't see that the wire was selected in the Wire Profile
    /// view."</i>
    /// </summary>
    private static WBondDesign TwoCoincidentArrays()
    {
        long loopNm = WBondUnits.ToNm(20.0, WBondUnit.Mil);
        var design = new WBondDesign();

        foreach (string name in new[] { "G1", "G2" })
        {
            var array = new WireArray { Name = name };
            array.Wires.Add(LoopShape.CreateSeedWire(
                Point3.Mils(0, 0, 4), Point3.Mils(100, 0, 1),
                WBondUnits.ToNm(1.0, WBondUnit.Mil), "Gold", loopHeightNm: loopNm));
            design.Arrays.Add(array);
        }

        return design;
    }

    private static int AccentPixels(WBondDesign design, WireSelection? selection)
    {
        var theme = WBondRenderTheme.Fallback;

        using var surface = SKSurface.Create(new SKImageInfo(800, 600));
        surface.Canvas.Clear(SKColors.Black);

        WBondRenderer.DrawProfile(
            surface.Canvas, design, theme,
            span => (float)(span / 4000.0), z => (float)(600 - z / 2000.0),
            selection: selection);
        surface.Canvas.Flush();

        using var image = surface.Snapshot();
        using var bitmap = SKBitmap.FromImage(image);

        int lit = 0;
        for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
            {
                var p = bitmap.GetPixel(x, y);
                if (p.Red == theme.Selected.Red && p.Green == theme.Selected.Green && p.Blue == theme.Selected.Blue)
                    lit++;
            }

        return lit;
    }

    /// <summary>
    /// <b>A selected wire is drawn over the wires it shares pixels with, not under them.</b>
    ///
    /// <para>The oracle is the accent's own pixel count measured two ways: with the selected wire
    /// ALONE on the canvas, and with the coincident neighbour also drawn. Those two numbers agree only
    /// if the neighbour went underneath — drawn afterwards it repaints the same polyline in the plain
    /// wire colour and the accent count collapses towards zero, which is exactly what the owner was
    /// looking at.</para>
    ///
    /// <para>Counting rather than probing one pixel, because a single pixel could survive by luck at
    /// an antialiased edge; the count cannot.</para>
    /// </summary>
    [Fact]
    public void TheProfileView_DrawsASelectedWireOverTheOnesItCoincidesWith()
    {
        var selection = new WireSelection { Wires = [0] };

        var alone = new WBondDesign();
        alone.Arrays.Add(TwoCoincidentArrays().Arrays[0]);

        int accentAlone = AccentPixels(alone, selection);
        Assert.True(accentAlone > 0, "sanity check: a selected wire has to paint SOME accent on its own");

        int accentWithNeighbour = AccentPixels(TwoCoincidentArrays(), selection);

        Assert.True(accentWithNeighbour >= accentAlone * 0.9,
                    $"the selected wire is being covered: {accentWithNeighbour} accent pixels with a " +
                    $"coincident neighbour vs {accentAlone} alone");
    }

    /// <summary>
    /// …and nothing else moved: every wire is still drawn, and an unselected design renders exactly as
    /// it did. Deferring the selected wire re-orders the stack; it must not add to it or drop from it.
    /// </summary>
    [Fact]
    public void TheProfileView_StillDrawsEveryWire_WhateverIsSelected()
    {
        var design = TwoCoincidentArrays();

        static int Drawn(WBondDesign d, WireSelection? selection)
        {
            using var surface = SKSurface.Create(new SKImageInfo(800, 600));
            return WBondRenderer.DrawProfile(
                surface.Canvas, d, WBondRenderTheme.Fallback,
                span => (float)(span / 4000.0), z => (float)(600 - z / 2000.0),
                selection: selection).WiresDrawn;
        }

        Assert.Equal(2, Drawn(design, null));
        Assert.Equal(2, Drawn(design, new WireSelection { Wires = [0] }));
        Assert.Equal(2, Drawn(design, new WireSelection { Wires = [1] }));
        Assert.Equal(2, Drawn(design, new WireSelection { Wires = [0, 1] }));
    }

    // ---------------------------------------------------------------- the panel's frequency row

    /// <summary>
    /// <b>The frequency row keeps its place with capacitance switched off</b> (owner: "keep the self
    /// inductance group listings fixed in position… when unchecked, the Frequency row disappears which
    /// causes all the inductances to shift so it is difficult for user to make quick comparisons while
    /// toggling the button").
    ///
    /// <para>What the row prints is the view-model's business and is what this asserts; the row's own
    /// presence is now unconditional in XAML, and <see cref="WBondPanelViewModel.ShowFrequency"/> has
    /// stopped being a visibility flag and become "is there anything to say".</para>
    /// </summary>
    [Fact]
    public void ThePanelsFrequencyRow_SaysNothingRatherThanVanishing_WhenCapacitanceIsOff()
    {
        var panel = new WBondPanelViewModel { Frequency = "10 GHz", ShowFrequency = true };
        Assert.Equal("10 GHz", panel.FrequencyDisplay);

        panel.ShowFrequency = false;
        Assert.Equal("—", panel.FrequencyDisplay);

        panel.ShowFrequency = true;
        Assert.Equal("10 GHz", panel.FrequencyDisplay);
    }

    /// <summary>
    /// The display follows BOTH of its inputs through change notification — a row bound to it and
    /// never told would print the old frequency after the switch was flipped, which is worse than the
    /// row that used to disappear.
    /// </summary>
    [Fact]
    public void ThePanelsFrequencyDisplay_NotifiesOnBothOfItsInputs()
    {
        var panel = new WBondPanelViewModel { Frequency = "10 GHz", ShowFrequency = true };
        var seen = new System.Collections.Generic.List<string?>();
        panel.PropertyChanged += (_, e) => seen.Add(e.PropertyName);

        panel.ShowFrequency = false;
        Assert.Contains(nameof(WBondPanelViewModel.FrequencyDisplay), seen);

        seen.Clear();
        panel.ShowFrequency = true;
        panel.Frequency = "25 GHz";
        Assert.Contains(nameof(WBondPanelViewModel.FrequencyDisplay), seen);
        Assert.Equal("25 GHz", panel.FrequencyDisplay);
    }
}

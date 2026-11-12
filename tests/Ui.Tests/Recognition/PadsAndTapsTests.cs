// What the board drawn as DXF outlines found wrong between pads, lines and the parts on them, each on a synthetic board
// built in memory: parts with no land of their own, a body outline on the silkscreen over a gap in the copper (PartReading's
// rung 3½), named by a label beside it. Coordinates in µm.

using System.Collections.Generic;
using System.Linq;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Recognition;
using Xunit;
using static CircuitRF.Ui.Tests.Recognition.LineBoards;
using static CircuitRF.Ui.Tests.Recognition.RecognitionBoards;

namespace CircuitRF.Ui.Tests.Recognition;

public sealed class PadsAndTapsTests
{
    private static readonly LayerKey Silk = new(6, 0);

    private static Technology WithSilk()
    {
        var tech = TwoLayer();
        tech.Layers = [.. tech.Layers, new LayerDef { Key = Silk, Name = "Silk Top", Purpose = "silkscreen" }];
        return tech;
    }

    /// <summary>An 0603's body outline centred at (<paramref name="cx"/>, <paramref name="cy"/>), along x or y, and its
    /// designator typed beside it.</summary>
    private static IEnumerable<LayoutShape> Part(string refdes, double cx, double cy, bool alongX)
    {
        double hx = alongX ? 800 : 400, hy = alongX ? 400 : 800;
        yield return new PolygonShape
        {
            Layer = Silk, Xy = [Um(cx - hx), Um(cy - hy), Um(cx + hx), Um(cy - hy), Um(cx + hx), Um(cy + hy), Um(cx - hx), Um(cy + hy)],
        };
        yield return new LabelShape { Layer = Silk, Text = refdes, X = Um(cx + hx + 300), Y = Um(cy + hy), Height = Um(800) };
    }

    private static RecognitionResult Read(LayoutView view) =>
        ArtworkRecognition.Recognize(new RecognitionInput { View = view, Technology = WithSilk(), Shapes = view.Shapes });

    /// <summary>
    /// A 30 × 20 mm board: a 1.1 mm line in from the left edge, C8 across a gap into a 2.6 mm square pad, then 5.6 mm of 1.7 mm
    /// line and 1.1 mm line on to the right edge; with the top pour <paramref name="above"/> µm over the 1.7 mm line and the
    /// bottom one <paramref name="below"/> µm under it, or no pours.
    /// </summary>
    private static LayoutView PadThenWideLine(double? above, double? below)
    {
        var view = new LayoutView();
        view.Shapes.Add(Rect(Bottom, 0, 0, 30_000, 20_000));
        view.Shapes.Add(Rect(Top, 0, 9_450, 5_000, 10_550));
        view.Shapes.Add(Rect(Top, 5_500, 8_700, 8_100, 11_300));
        view.Shapes.Add(Rect(Top, 8_100, 9_150, 13_700, 10_850));
        view.Shapes.Add(Rect(Top, 13_700, 9_450, 30_000, 10_550));
        view.Shapes.AddRange(Part("C8", 5_250, 10_000, alongX: true));
        if (above is { } a && below is { } b)
        {
            view.Shapes.Add(Rect(Top, 0, 10_850 + a, 30_000, 20_000));
            view.Shapes.Add(Rect(Top, 0, 0, 30_000, 9_150 - b));
            foreach (double x in new double[] { 2_000, 10_000, 18_000, 26_000 })
            {
                view.Shapes.Add(ViaAt(x, 17_000));
                view.Shapes.Add(ViaAt(x, 3_000));
            }
        }
        return view;
    }

    /// <summary>
    /// The 1.7 mm run from the pad is a line, its whole 5.6 mm, from C8's terminal on the pad: whether its coplanar gaps
    /// differ by more than 1.5× (a TLIN), are equal (CPWG), or there is no coplanar ground (MLIN). The review's end trim took
    /// it for the pad's land — 3.3 of its widths long, wider than the line after it — and the line was gone.
    /// </summary>
    [Theory]
    [InlineData(800.0, 510.0, LineElementType.TLIN)]
    [InlineData(650.0, 650.0, LineElementType.CPWG)]
    [InlineData(null, null, LineElementType.MLIN)]
    public void ALineFromAPad_IsKept_WhateverItsCoplanarGround(double? above, double? below, LineElementType type)
    {
        var result = Read(PadThenWideLine(above, below));

        Assert.True(result.Ok, result.Refusal);
        var c8 = Assert.Single(result.Parts.Rows);
        int onPad = c8.Terminals.ToList().FindIndex(t => t.X > Um(5_250));
        var wide = Assert.Single(Lines(result), e => e.Width is { } w && System.Math.Abs(w - 1.7e-3) < 1e-6);
        Assert.Equal(type, wide.Type);
        Assert.Equal(5_600, wide.Micro("L"), 1.0);
        Assert.Contains(result.Lines.NodeOf($"C8.{onPad + 1}"), wide.Nodes);
        Assert.Equal(0, result.Report.Count(RecognitionFindingClass.CopperJoinedWithoutLine));
        Assert.Equal(0, result.Report.Count(RecognitionFindingClass.CircuitSplitsCopper));
    }
}

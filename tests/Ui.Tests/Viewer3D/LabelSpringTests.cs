// ================================================================
//  LabelSpringTests.cs — the 3D view's labels swing to a new side with some weight instead of jumping there in one frame,
//  stay fastened to their point while they do, and come to rest exactly where the layout put them.
// ================================================================

using Avalonia;
using CircuitRF.Ui.Viewer3D;
using Xunit;

namespace CircuitRF.Ui.Tests.Viewer3D;

public sealed class LabelSpringTests
{
    private static Rect Step(Viewer3DLabelSpring s, Point anchor, Rect target)
    {
        s.BeginFrame(1.0 / 60);
        var box = s.Follow("label", [anchor], ["P1"], [target])[0];
        s.EndFrame();
        return box;
    }

    [Fact]
    public void A_label_changing_side_swings_over_with_its_point_and_settles_on_its_target()
    {
        var s = new Viewer3DLabelSpring();
        var a = new Point(100, 100);
        var upRight = new Rect(100, 84, 30, 16);
        Assert.Equal(upRight, Step(s, a, upRight));                    // first sight: in place, no fly-in
        Assert.False(s.Moving);

        var downLeft = new Rect(70, 102, 30, 16);
        var first = Step(s, a, downLeft);
        Assert.True(first.X < upRight.X && first.X > downLeft.X);     // part way, not there in one frame
        Assert.True(s.Moving);

        // The point moves (the camera orbits) mid-swing: the box moves with it by exactly as much.
        var a2 = new Point(140, 90);
        var moved = Step(s, a2, downLeft.Translate(a2 - a));
        Assert.True(moved.X - a2.X < upRight.X - a.X && moved.X - a2.X > downLeft.X - a.X);

        Rect box = moved;
        for (int i = 0; i < 120 && s.Moving; i++) box = Step(s, a2, downLeft.Translate(a2 - a));
        Assert.False(s.Moving);
        Assert.Equal(downLeft.Translate(a2 - a), box);
    }
}

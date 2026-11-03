// ================================================================
//  ViewNavigationRound14Tests.cs — general designer feedback round 14, the 3D view on a laptop: Fit frames tightly from the
//  current direction and keeps it; the keyboard zooms (Ctrl/Cmd + − 0), pans (arrows) and orbits (Shift+arrows); and the
//  drawing's labels never overlap. Pixels were not seen: these read the camera and the placed boxes.
// ================================================================

using System.Numerics;
using Avalonia;
using Avalonia.Input;
using CircuitRF.Render.Scene3D;
using CircuitRF.Ui.Tests.ThreeD;
using CircuitRF.Ui.Viewer3D;
using Xunit;

namespace CircuitRF.Ui.Tests.Viewer3D;

[Collection(Viewer3DCollection.Name)]
public sealed class ViewNavigationRound14Tests
{
    /// <summary>The Die to Heatsink stack seen from the Front: a 12 mm wide, 1.8 mm tall slab. The sphere fit left it a third of
    /// the width; a tight fit puts its widest corner at the margin, whichever projection, and turns nothing.</summary>
    [Theory]
    [InlineData(Projection3D.Perspective)]
    [InlineData(Projection3D.Orthographic)]
    public void FrameBounds_AFlatStackSeenFromTheFront_FillsTheWidth_AndKeepsTheDirection(Projection3D projection)
    {
        var min = new Vector3(-6e-3f, -5e-3f, -0.1e-3f);
        var max = new Vector3(6e-3f, 5e-3f, 1.75e-3f);
        const float w = 800, h = 500;
        var c = new Camera3D { FovY = Camera3D.DefaultFovY, Projection = projection };
        c.SetStandardView(StandardView3D.Front);
        c.FrameBounds(min, max, w / h);
        Assert.Equal(-MathF.PI / 2, c.Yaw, 5);
        Assert.Equal(0f, c.Pitch, 5);

        float widest = 0;
        for (int k = 0; k < 8; k++)
        {
            var p = new Vector3((k & 1) == 0 ? min.X : max.X, (k & 2) == 0 ? min.Y : max.Y, (k & 4) == 0 ? min.Z : max.Z);
            var (x, y, visible) = c.Project(p, w, h);
            Assert.True(visible && x >= 0 && x <= w && y >= 0 && y <= h, $"corner {k} at ({x}, {y})");
            widest = MathF.Max(widest, MathF.Abs(x - w / 2));
        }
        Assert.InRange(widest / (w / 2), 0.85f, 1f / Camera3D.FitMargin + 1e-3f);
    }

    [Fact]
    public void TheKeyboard_ZoomsPansOrbitsAndFits_WithNoWheelAndNoMiddleButton()
    {
        using var pane = new Viewer3DViewModel(Path.Combine(Path.GetTempPath(), "setup.cem"),
            () => new Viewer3DInputs(new CircuitRF.Design.Layout.Em.EmSetup(), null, "no layout", CircuitRF.Render.ColorTheme.BuiltIn,
                                     CircuitRF.Render.ColorVariant.Light),
            () => new PatchRecordingBackend(), () => null, a => a());
        pane.Resized(400, 300);
        pane.View.Camera = Camera3D.Fit(new Vector3(-1e-3f), new Vector3(1e-3f), 400 / 300f);
        bool Key(Key k, KeyModifiers m = KeyModifiers.None) => pane.HandleKey(k, m, gestureInProgress: false);

        float d = pane.View.Camera.Distance;
        Assert.True(Key(Avalonia.Input.Key.OemPlus, KeyModifiers.Control));
        Assert.True(pane.View.Camera.Distance < d);
        Assert.True(Key(Avalonia.Input.Key.Subtract, KeyModifiers.Meta));
        Assert.Equal(d, pane.View.Camera.Distance, d * 1e-5f);

        var right = pane.View.Camera.Right;
        var target = pane.View.Camera.Target;
        Assert.True(Key(Avalonia.Input.Key.Right));                            // the view moves right: the target with it
        Assert.True(Vector3.Dot(pane.View.Camera.Target - target, right) > 0);

        float yaw = pane.View.Camera.Yaw;
        Assert.True(Key(Avalonia.Input.Key.Left, KeyModifiers.Shift));        // the camera travels left round the target
        Assert.Equal(Viewer3DViewModel.OrbitStepDegrees * MathF.PI / 180f, yaw - pane.View.Camera.Yaw, 4);
        Assert.True(Vector3.Dot(pane.View.Camera.Eye - pane.View.Camera.Target, right) < 0);

        int fits = pane.Fits;
        Assert.True(Key(Avalonia.Input.Key.D0, KeyModifiers.Control));
        Assert.Equal(fits + 1, pane.Fits);
        Assert.False(Key(Avalonia.Input.Key.Right, KeyModifiers.Control));   // a modified arrow is someone else's shortcut
    }

    /// <summary>The shape in the designer's Front view: three probes on one vertical, two of them at one point, and a block's
    /// label beside them. No two boxes overlap, none covers another's point, and the ones moved away are given a leader.</summary>
    [Fact]
    public void Labels_AtOnePointAndOnOneVertical_NeverOverlap()
    {
        var anchors = new Point[] { new(300, 200), new(300, 212), new(300, 212), new(300, 224), new(290, 214) };
        var sizes = new Size[] { new(60, 20), new(100, 20), new(110, 20), new(90, 20), new(220, 20) };
        var placed = Viewer3DLabelLayout.Place(anchors, sizes, 800, 500);

        for (int i = 0; i < placed.Length; i++)
        {
            Assert.Equal(sizes[i], placed[i].Box.Size);
            for (int j = i + 1; j < placed.Length; j++)
                Assert.False(placed[i].Box.Intersects(placed[j].Box), $"labels {i} and {j} overlap");
        }
        Assert.Contains(placed, p => p.Leader);
        Assert.All(placed.Where(p => !p.Leader), p => Assert.True(p.Box.Inflate(3).Contains(anchors[Array.IndexOf(placed, p)])));
    }

    /// <summary>A label whose point is off the view goes with it: placed against its point on the side away from the view,
    /// with no leader, never slid back on screen — while a label just inside the edge is still kept in view.</summary>
    [Fact]
    public void Labels_WhosePointIsOffScreen_StayOffScreen()
    {
        var anchors = new Point[] { new(400, 505), new(805, 250), new(-3, 100), new(400, -2), new(400, 495) };
        var sizes = Enumerable.Repeat(new Size(80, 20), anchors.Length).ToArray();
        var view = new Rect(0, 0, 800, 500);
        var placed = Viewer3DLabelLayout.Place(anchors, sizes, view.Width, view.Height);

        for (int i = 0; i < 4; i++)
        {
            Assert.False(placed[i].Leader, $"label {i} has a leader");
            Assert.False(placed[i].Box.Intersects(view), $"label {i} was pulled into view: {placed[i].Box}");
        }
        Assert.True(view.Contains(placed[4].Box));
    }
}

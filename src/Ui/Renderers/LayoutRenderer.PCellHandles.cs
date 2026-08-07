using SkiaSharp;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Renderers;

/// <summary>
/// Drawing a PCell instance's draggable PARAMETER grips — see
/// <c>docs/design/pcell-parameter-handles.md</c> §4.2.
///
/// <para><b>The glyph is L1d's own grab square, reused, not a new shape.</b> The editor already has a
/// visual language for "this is draggable"; a second one would be a second thing to learn, and the
/// obvious candidates were already taken — an earlier draft used a hollow diamond, which is L1d's
/// BULGE handle. What marks a grip as parametric instead is three subtle additions on top of the
/// shared glyph: its own colour role (<see cref="LayoutRenderTheme.PCellHandle"/>, never the
/// selection accent), a small hollow centre showing it is not a plain vertex, and — the part L1d has
/// no equivalent of at all — a visible AXIS HINT showing which way it travels, drawn before the user
/// commits to a drag.</para>
///
/// <para>The two are never on screen together (a shape shows geometry handles, an instance shows
/// parameter grips, and an instance has never had geometry handles), so the job of the difference is
/// to be legible across a switch of selection rather than side by side.</para>
/// </summary>
public static partial class LayoutRenderer
{
    /// <summary>Screen-space, computed per frame from the current zoom like every other overlay
    /// dimension here. Slightly larger than <c>HandleSizeDevicePixels</c> so a grip reads as the more
    /// consequential of the two when a user switches between a selected shape and a selected
    /// instance.</summary>
    private const double PCellHandleSizeDevicePixels = 9.0;

    /// <summary>How far the axis hint reaches from the grip, each way. Long enough to read as a
    /// direction, short enough not to be mistaken for geometry.</summary>
    private const double PCellHandleAxisHintDevicePixels = 18.0;

    private static void DrawPCellHandles(
        SKCanvas canvas, IReadOnlyList<PCellHandleMarker> handles,
        LayoutRenderTheme theme, PathSpace ps, double scaleUm)
    {
        if (handles.Count == 0) return;

        float half     = DevicePixelsToPathSpace(scaleUm, PCellHandleSizeDevicePixels) / 2f;
        float reach    = DevicePixelsToPathSpace(scaleUm, PCellHandleAxisHintDevicePixels);
        float hairline = DevicePixelsToPathSpace(scaleUm, 1.5);

        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = theme.PCellHandle };
        using var outline = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = hairline, Color = theme.PCellHandle,
        };
        using var hint = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = hairline,
            Color = theme.PCellHandle.WithAlpha(150),
            PathEffect = SKPathEffect.CreateDash([4f, 3f], 0),
        };

        foreach (var h in handles)
        {
            float cx = ps.X(h.X), cy = ps.Y(h.Y);

            // The axis hint. Path space is Y-DOWN while the marker's direction is in Y-UP world DBU,
            // so the Y component is negated here — the same flip PathSpace.Y applies to a coordinate,
            // applied to a direction. Getting this wrong points the hint at the mirror of where the
            // grip actually travels, which is worse than not drawing it at all.
            float hx = (float)h.AxisDx * reach, hy = -(float)h.AxisDy * reach;
            if (h.IsAngular)
            {
                // An angular grip's hint is an ARC through the grip, centred on the anchor — the path
                // the grip will actually take. A straight tangent is the obvious cheap substitute and
                // is misleading in exactly the way that matters: it reads as unbounded travel in one
                // direction, when the grip is on a circle.
                float ax = ps.X(h.AnchorX), ay = ps.Y(h.AnchorY);
                float rx = cx - ax, ry = cy - ay;
                float radius = (float)Math.Sqrt(rx * rx + ry * ry);
                if (radius > 0.0001f)
                {
                    // Sweep chosen so the arc's LENGTH is the same `reach` a linear hint spans, so the
                    // two kinds of hint read as the same weight of affordance at any radius.
                    float sweepDeg = (float)(reach / radius * (180.0 / Math.PI));
                    float startDeg = (float)(Math.Atan2(ry, rx) * (180.0 / Math.PI)) - sweepDeg;
                    var oval = new SKRect(ax - radius, ay - radius, ax + radius, ay + radius);
                    using var arc = new SKPath();
                    arc.AddArc(oval, startDeg, sweepDeg * 2f);
                    canvas.DrawPath(arc, hint);
                }
            }
            else if (hx != 0f || hy != 0f)
            {
                canvas.DrawLine(cx - hx, cy - hy, cx + hx, cy + hy, hint);
                // A two-axis grip (R-pch-4a) hints BOTH directions, so "you can also drag this the
                // other way" is visible rather than something to discover by accident.
                if (h.HasCrossAxis) canvas.DrawLine(cx - hy, cy + hx, cx + hy, cy - hx, hint);
            }

            // L1d's own grab square, shared (see DrawGrabSquare) — the grip being dragged is filled
            // solid, the rest hollow, so which one is live reads without moving the pointer off it.
            DrawGrabSquare(canvas, cx, cy, half, h.Active ? fill : outline);

            // The one mark that says "parameter, not vertex": a small hollow centre. Cheap, and it
            // survives at the small on-screen sizes where a shape difference would not.
            if (!h.Active) canvas.DrawCircle(cx, cy, half * 0.35f, outline);
        }
    }
}

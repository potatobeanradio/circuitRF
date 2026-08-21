using System;
using Avalonia;

namespace CircuitRF.Ui.Views.Match;

/// <summary>
/// Where an unowned editor window opens relative to the window that opened it.
/// </summary>
/// <remarks>
/// <b>Pure arithmetic, deliberately out of the window class.</b> Placement is one of those things a
/// developer can only check by opening the application and looking — and looking told the owner twice
/// that it was wrong before it told anyone WHY. Everything here is a function of the owner's position,
/// its scaling and its screen, so a test can ask it directly.
///
/// <para><b>Owner-reported, 2026-08-20 (twice).</b> First: "window opens in wrong spot now" — it was
/// centred on the workspace, which covers exactly the part of it a user reaches for to get back to it,
/// so it became a cascade off the top-left instead. Then: "window placement on open is still in top
/// left corner of my screen" — the cascade was computed correctly and then thrown away by its own
/// safety clamp. See <see cref="Cascade"/>.</para>
/// </remarks>
public static class MatchWindowPlacement
{
    /// <summary>
    /// How far down and right of the owner's own corner the window opens, in DIPs.
    /// </summary>
    /// <remarks>
    /// Enough that the owner's title bar and its left edge stay visible and clickable, which is the
    /// whole point of the offset — it is the way back to the window underneath.
    /// </remarks>
    public const double CascadeOffset = 36.0;

    /// <summary>
    /// How much of the new window's top-left corner is kept inside the working area, in the units
    /// <see cref="Window.Position"/> is measured in.
    /// </summary>
    /// <remarks>
    /// Roughly a title bar's height and a few buttons' width — enough to grab and move the window with
    /// if the owner was itself parked at the very bottom-right of its screen.
    /// </remarks>
    public const int MinOnScreen = 240;

    /// <summary>
    /// The position for a window cascaded off <paramref name="ownerPosition"/>.
    /// </summary>
    /// <param name="ownerPosition">The owner's own <see cref="Window.Position"/>.</param>
    /// <param name="scaling">The owner's <c>RenderScaling</c>; anything not positive is read as 1.</param>
    /// <param name="workingArea">
    /// The owner's screen's working area, in the same units as <paramref name="ownerPosition"/>, or
    /// null when the platform cannot name a screen for it — which happens while a window is being
    /// dragged between displays and is not worth refusing to open over.
    /// </param>
    /// <remarks>
    /// <b>The clamp must not contain the new window's SIZE, and that is the whole of the second bug.</b>
    /// The obvious guard — "keep the whole window inside the working area", i.e. clamp x to
    /// <c>area.X + area.Width − windowWidth</c> — needs the width in the units
    /// <see cref="Window.Position"/> uses, and those units are not knowable here. Converting a DIP
    /// width with <c>RenderScaling</c> assumes <see cref="Screen.WorkingArea"/> is in physical pixels;
    /// on macOS it is in points, the same space as the DIP width, so a 1360-DIP window became 2720
    /// against a 1728-wide area, the upper bound went negative, <c>Math.Max</c> floored it at
    /// <c>area.X</c>, and the clamp pinned the window to <b>exactly the screen's top-left corner</b> —
    /// which is what the owner saw, and it looked identical to the offset never having been applied.
    ///
    /// <para>So the guard is expressed without the size at all: keep <see cref="MinOnScreen"/> of the
    /// window's leading corner inside the working area. That is unit-agnostic, it is what the guard was
    /// actually for (a window you can still reach), and cascading a small offset off a window that is
    /// itself on screen means it essentially never fires.</para>
    ///
    /// <para>The OFFSET keeps its scaling: it only ever adds, so it cannot pin anything, and without it
    /// the cascade is a different physical distance on every display.</para>
    /// </remarks>
    public static PixelPoint Cascade(PixelPoint ownerPosition, double scaling, PixelRect? workingArea)
    {
        int offset = (int)Math.Round(CascadeOffset * (scaling <= 0 ? 1.0 : scaling));

        int x = ownerPosition.X + offset;
        int y = ownerPosition.Y + offset;

        if (workingArea is { } area)
        {
            x = Math.Clamp(x, area.X, Math.Max(area.X, area.X + area.Width - MinOnScreen));
            y = Math.Clamp(y, area.Y, Math.Max(area.Y, area.Y + area.Height - MinOnScreen));
        }
        return new PixelPoint(x, y);
    }
}

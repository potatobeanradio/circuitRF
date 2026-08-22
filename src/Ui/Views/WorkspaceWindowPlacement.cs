using System;

namespace CircuitRF.Ui.Views;

/// <summary>
/// The opening size of a top-level workspace window, fitted to the display it opens on.
/// </summary>
/// <remarks>
/// <b>Owner-reported, 2026-08-21:</b> "on Windows, when a new workspace is created, its window appears
/// lower on the screen than macOS … the lower portion of the window is cut off". Two separate causes,
/// and fixing only the visible one leaves the window unusable on a small display:
///
/// <list type="number">
/// <item><b>Nothing asked for the window to be centred.</b> <c>WorkspaceWindow</c> declared no
/// <see cref="Avalonia.Controls.WindowStartupLocation"/>, so it took the default — <c>Manual</c> with no
/// <c>Position</c> — which hands placement to the OS. macOS cascades within the visible frame and never
/// pushes a window past the bottom of the screen; Win32's <c>CW_USEDEFAULT</c> cascades down-and-right
/// from the top-left and does not care whether the result fits. Same code, different placement, and the
/// Windows one steps further down with every window. The window now asks for <c>CenterScreen</c>.</item>
///
/// <item><b>1200×800 DIPs is bigger than a common Windows working area, and centring alone does not
/// help that</b> — it splits the overflow between top and bottom instead of putting it all at the
/// bottom. A 1920×1080 display at 150% scaling is 1280×693 DIPs of working area once the taskbar is
/// gone, so the declared 800-DIP height overflows by ~110 DIPs no matter where it is placed. macOS never
/// showed this because its Retina working area is reported in points and is far taller in DIPs (measured
/// on the owner's own machine, 2026-08-21: 1920×996 points, <see cref="Avalonia.Platform.Screen.Scaling"/>
/// <b>1</b>, not 2).</item>
/// </list>
///
/// <para><b>Why <see cref="Avalonia.Platform.Screen.Scaling"/> and not <c>RenderScaling</c>, given
/// <see cref="Match.MatchWindowPlacement"/> documents exactly the opposite trap.</b> A screen's
/// <c>WorkingArea</c> is in physical pixels on Windows and in points on macOS, so converting a DIP size
/// with the WINDOW's <c>RenderScaling</c> (2 on Retina) doubles it against an area that was never scaled
/// — the bug that pinned the Match Designer to the screen corner. <c>Screen.Scaling</c> is the factor
/// that maps DIPs into <i>that screen's own</i> units on both platforms (1 on macOS even at
/// <c>RenderScaling</c> 2, 1.5 on a 150% Windows display), and it is the same factor Avalonia's own
/// <c>CenterScreen</c> uses to convert <c>ClientSize</c> — so the fit computed here and the centring
/// Avalonia then performs agree by construction.</para>
/// </remarks>
public static class WorkspaceWindowPlacement
{
    /// <summary>
    /// Breathing room left between the window and the working-area edge, in DIPs, per axis (total).
    /// </summary>
    /// <remarks>
    /// A window flush against the taskbar or the dock reads as "cut off" even when it is not, and the
    /// resize grip on the bottom edge becomes hard to hit. Small enough that it never fires on a display
    /// the declared size already fits.
    /// </remarks>
    public const double EdgeMargin = 48.0;

    /// <summary>
    /// The size to open at: the declared size, shrunk to fit <paramref name="workingAreaWidth"/> ×
    /// <paramref name="workingAreaHeight"/>, never below the window's own minimum.
    /// </summary>
    /// <param name="desiredWidth">Declared width in DIPs.</param>
    /// <param name="desiredHeight">Declared height in DIPs.</param>
    /// <param name="workingAreaWidth">The screen's working-area width, in that screen's own units.</param>
    /// <param name="workingAreaHeight">The screen's working-area height, in that screen's own units.</param>
    /// <param name="screenScaling">
    /// <see cref="Avalonia.Platform.Screen.Scaling"/> — DIPs × this = that screen's units. Anything not
    /// positive is read as 1.
    /// </param>
    /// <param name="minWidth">The window's <c>MinWidth</c> in DIPs.</param>
    /// <param name="minHeight">The window's <c>MinHeight</c> in DIPs.</param>
    /// <remarks>
    /// A working area that is not positive means the platform could not name a screen (it happens while a
    /// display is being attached, and while a window is being dragged between two). The declared size is
    /// returned unchanged rather than guessed at — the failure mode of guessing is a permanently tiny
    /// window, which is worse than the one this method exists to fix.
    ///
    /// <para>The minimum wins over the fit, deliberately. On a display too small for
    /// <c>MinWidth</c>/<c>MinHeight</c> the window cannot honour both, and Avalonia would clamp back up to
    /// the minimum anyway; returning something smaller here would only make the returned size a lie.</para>
    /// </remarks>
    public static (double Width, double Height) Fit(
        double desiredWidth,
        double desiredHeight,
        double workingAreaWidth,
        double workingAreaHeight,
        double screenScaling,
        double minWidth,
        double minHeight)
    {
        if (workingAreaWidth <= 0.0 || workingAreaHeight <= 0.0)
            return (desiredWidth, desiredHeight);

        double scaling = screenScaling > 0.0 ? screenScaling : 1.0;

        double availableWidth  = workingAreaWidth  / scaling - EdgeMargin;
        double availableHeight = workingAreaHeight / scaling - EdgeMargin;

        return (Math.Max(Math.Min(desiredWidth,  availableWidth),  minWidth),
                Math.Max(Math.Min(desiredHeight, availableHeight), minHeight));
    }
}

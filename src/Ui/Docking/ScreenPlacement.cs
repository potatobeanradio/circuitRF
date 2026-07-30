using System;
using System.Collections.Generic;

namespace CircuitRF.Ui.Docking;

/// <summary>
/// A rectangle in LOGICAL (DPI-independent) units. Framework-free on purpose: this is the one
/// coordinate type <see cref="ScreenPlacement"/> reasons about, so the whole off-screen problem
/// is unit-testable against synthetic screens with no display attached.
/// </summary>
public readonly record struct ScreenRect(double X, double Y, double Width, double Height)
{
    public double Right  => X + Width;
    public double Bottom => Y + Height;

    public static ScreenRect FromLtrb(double l, double t, double r, double b) => new(l, t, r - l, b - t);

    /// <summary>True when <paramref name="inner"/> lies wholly within this rectangle (1e-6 slack).</summary>
    public bool Contains(ScreenRect inner) =>
        inner.X      >= X      - Eps &&
        inner.Y      >= Y      - Eps &&
        inner.Right  <= Right  + Eps &&
        inner.Bottom <= Bottom + Eps;

    /// <summary>Width of the horizontal overlap with <paramref name="other"/> (0 when disjoint).</summary>
    public double OverlapWidth(ScreenRect other) =>
        Math.Max(0.0, Math.Min(Right, other.Right) - Math.Max(X, other.X));

    /// <summary>Height of the vertical overlap with <paramref name="other"/> (0 when disjoint).</summary>
    public double OverlapHeight(ScreenRect other) =>
        Math.Max(0.0, Math.Min(Bottom, other.Bottom) - Math.Max(Y, other.Y));

    private const double Eps = 1e-6;
}

/// <summary>
/// Validates a saved floating-window rectangle against the screens that actually exist right now
/// (brief-dock-layout-persistence.md §4, R-dock-6/7/8).
///
/// <para>The rule that matters, and the one an "intersects any screen" test gets wrong: it is not
/// enough for SOME of the window to be on a screen — the <b>title bar must be reachable</b>, because
/// a window whose title bar is off-screen cannot be dragged back. That failure mode is
/// unrecoverable by the user, which is why this class exists at all.</para>
///
/// <para>Everything here is in LOGICAL units (R-dock-7). A window saved on a scaled 4K display and
/// restored on a 1080p one must come back the right <i>apparent</i> size; converting device pixels
/// to logical at the boundary (see <c>AvaloniaScreenSource</c>) is what makes that true, and is why
/// nothing in this file knows what a device pixel is.</para>
/// </summary>
public static class ScreenPlacement
{
    /// <summary>
    /// Height of the draggable strip at the top of a window, in logical units. A generous, fixed
    /// value rather than a per-platform chrome query: this is a REACHABILITY test, and being a few
    /// pixels conservative only ever relocates a window that was already marginal.
    /// </summary>
    public const double TitleBarHeight = 32.0;

    /// <summary>
    /// Minimum width of title bar that must be visible for the window to count as graspable when the
    /// screen configuration is unchanged from the one it was saved on.
    /// </summary>
    public const double MinGraspableTitleBarWidth = 96.0;

    /// <summary>Offset applied per collision when several relocated windows would land identically.</summary>
    public const double CascadeStep = 28.0;

    /// <summary>Floor for a restored window so a corrupt/zero size never produces an invisible window.</summary>
    public const double MinWindowSize = 120.0;

    private const double SamePositionTolerance = 1.0;
    private const int    MaxCascadeSteps       = 24;

    /// <summary>
    /// True when <paramref name="saved"/> describes the same screen arrangement as
    /// <paramref name="current"/> — same count, same working areas (1 logical unit tolerance).
    /// R-dock-8: this is what lets restore distinguish "the same setup as last time" from
    /// "a different setup"; it is deliberately NOT a licence to skip validation (gate 19 —
    /// a window saved off-screen on this very setup is still relocated), only to accept a
    /// window that was demonstrably usable here before, e.g. one straddling two monitors.
    /// </summary>
    public static bool SameConfiguration(IReadOnlyList<ScreenRect> saved, IReadOnlyList<ScreenRect> current)
    {
        if (saved.Count != current.Count) return false;
        for (int i = 0; i < saved.Count; i++)
        {
            var a = saved[i];
            var b = current[i];
            if (Math.Abs(a.X - b.X) > 1.0 || Math.Abs(a.Y - b.Y) > 1.0 ||
                Math.Abs(a.Width - b.Width) > 1.0 || Math.Abs(a.Height - b.Height) > 1.0)
                return false;
        }
        return true;
    }

    /// <summary>
    /// R-dock-7's actual arithmetic, kept here (framework-free) rather than inside the Avalonia
    /// adapter so it can be unit-tested with no display attached — a DPI bug is invisible on a
    /// single monitor, which is exactly why it needs a test that does not need two.
    /// </summary>
    public static double DeviceToLogical(double devicePixels, double scaling) =>
        devicePixels / (scaling > 0.0 ? scaling : 1.0);

    /// <inheritdoc cref="DeviceToLogical"/>
    public static double LogicalToDevice(double logical, double scaling) =>
        logical * (scaling > 0.0 ? scaling : 1.0);

    /// <summary>A device-pixel working area expressed in that screen's own logical units.</summary>
    public static ScreenRect WorkingAreaToLogical(ScreenRect devicePixels, double scaling)
    {
        var s = scaling > 0.0 ? scaling : 1.0;
        return new ScreenRect(devicePixels.X / s, devicePixels.Y / s, devicePixels.Width / s, devicePixels.Height / s);
    }

    /// <summary>The title-bar strip of <paramref name="window"/>.</summary>
    public static ScreenRect TitleBarOf(ScreenRect window) =>
        new(window.X, window.Y, window.Width, Math.Min(TitleBarHeight, Math.Max(window.Height, 1.0)));

    /// <summary>
    /// R-dock-6 step 2. <paramref name="strict"/> (a DIFFERENT screen configuration) requires the whole
    /// title-bar strip inside one screen's working area; otherwise at least
    /// <see cref="MinGraspableTitleBarWidth"/> of it must be, which keeps a window that legitimately
    /// straddles two monitors on an unchanged setup exactly where the user left it.
    /// </summary>
    public static bool IsTitleBarReachable(ScreenRect window, IReadOnlyList<ScreenRect> screens, bool strict)
    {
        if (screens.Count == 0) return true;   // nothing to validate against — never relocate blind
        var bar = TitleBarOf(window);

        foreach (var s in screens)
        {
            if (strict)
            {
                if (s.Contains(bar)) return true;
            }
            else
            {
                // Graspable: a real horizontal run of the strip is on this screen, and the strip's
                // own vertical band is too (a bar below the screen bottom is not grabbable).
                if (s.OverlapWidth(bar) >= MinGraspableTitleBarWidth &&
                    s.OverlapHeight(bar) >= bar.Height - 1e-6)
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Returns a rectangle guaranteed to be usable on the CURRENT screens.
    ///
    /// <para>In order (R-dock-6): reachability check → nearest screen → clamp size to the working area
    /// → clamp position so the title bar is inside it → cascade off any already-placed window.</para>
    ///
    /// <para>A window that is already reachable is returned <b>byte-identical</b> — no nudge, ever
    /// (gate 11). Relocation is a repair, not a policy.</para>
    /// </summary>
    /// <param name="saved">The saved window rectangle, in logical units.</param>
    /// <param name="screens">Working areas of the current screens, in logical units.</param>
    /// <param name="alreadyPlaced">Rectangles already assigned in this restore pass, for cascading.</param>
    /// <param name="sameConfiguration">Result of <see cref="SameConfiguration"/> for this restore.</param>
    public static ScreenRect Place(
        ScreenRect saved,
        IReadOnlyList<ScreenRect> screens,
        IReadOnlyList<ScreenRect> alreadyPlaced,
        bool sameConfiguration = false)
    {
        // Degenerate/corrupt size is repaired regardless of position (a 0×0 window is unreachable
        // in the most literal sense).
        var w = new ScreenRect(
            saved.X, saved.Y,
            Math.Max(saved.Width,  MinWindowSize),
            Math.Max(saved.Height, MinWindowSize));

        bool sizeRepaired = Math.Abs(w.Width - saved.Width) > 1e-6 || Math.Abs(w.Height - saved.Height) > 1e-6;

        if (screens.Count == 0)
            return w;   // no screen information — leave it alone rather than guess

        if (!sizeRepaired && IsTitleBarReachable(w, screens, strict: !sameConfiguration))
            return w;   // already usable — return exactly as saved

        var target = NearestScreen(w, screens);

        // 4. Clamp the size to the target working area BEFORE positioning — a window saved on a large
        //    display must never be restored larger than the screen it lands on.
        double width  = Math.Min(w.Width,  target.Width);
        double height = Math.Min(w.Height, target.Height);

        // 3. Move it onto that screen: clamping both axes puts the whole window (title bar included)
        //    inside the working area, which is strictly stronger than the reachability test above.
        double x = Clamp(w.X, target.X, target.Right  - width);
        double y = Clamp(w.Y, target.Y, target.Bottom - height);

        var placed = new ScreenRect(x, y, width, height);

        // 5. Cascade so several relocated windows do not land exactly on top of each other.
        //
        //    The naive +step-only cascade is wrong for the case that actually happens: several
        //    windows saved at the same far-off position all clamp to the SAME corner of the target
        //    screen, and from a corner a positive offset re-clamps straight back to where it started
        //    — so every window lands identically, which is exactly what the cascade exists to
        //    prevent. Stepping back toward the screen when the forward step is pinned is what makes
        //    the cascade work from any corner.
        //    The direction is chosen ONCE and then held. Re-choosing per step oscillates between two
        //    positions (forward is pinned, so it steps back; from there forward is free again, so it
        //    steps straight back onto the first window) and the cascade never converges.
        double direction = Offset(placed, CascadeStep, target, width, height) != placed
            ? CascadeStep
            : -CascadeStep;

        for (int step = 0; step < MaxCascadeSteps && CollidesWithPlaced(placed, alreadyPlaced); step++)
        {
            var next = Offset(placed, direction, target, width, height);
            if (next == placed) break;   // pinned in that direction — nothing further to offer
            placed = next;
        }

        return placed;
    }

    /// <summary>
    /// Nearest screen to <paramref name="w"/>'s own saved position, so a three-monitor layout
    /// collapsing to one keeps the relative ordering intelligible rather than stacking everything
    /// at the origin. Distance is measured centre-to-nearest-point, which behaves sensibly for a
    /// window that is far off to one side and for one that merely overhangs an edge.
    /// </summary>
    private static ScreenRect NearestScreen(ScreenRect w, IReadOnlyList<ScreenRect> screens)
    {
        double cx = w.X + w.Width  / 2.0;
        double cy = w.Y + w.Height / 2.0;

        var best     = screens[0];
        double bestD = double.MaxValue;

        foreach (var s in screens)
        {
            double dx = cx < s.X ? s.X - cx : cx > s.Right  ? cx - s.Right  : 0.0;
            double dy = cy < s.Y ? s.Y - cy : cy > s.Bottom ? cy - s.Bottom : 0.0;
            double d  = dx * dx + dy * dy;
            if (d < bestD) { bestD = d; best = s; }
        }
        return best;
    }

    private static ScreenRect Offset(ScreenRect r, double delta, ScreenRect target, double width, double height) =>
        new(Clamp(r.X + delta, target.X, target.Right  - width),
            Clamp(r.Y + delta, target.Y, target.Bottom - height),
            width, height);

    private static bool CollidesWithPlaced(ScreenRect r, IReadOnlyList<ScreenRect> placed)
    {
        foreach (var p in placed)
        {
            if (Math.Abs(p.X - r.X) < SamePositionTolerance &&
                Math.Abs(p.Y - r.Y) < SamePositionTolerance)
                return true;
        }
        return false;
    }

    private static double Clamp(double v, double lo, double hi) =>
        hi < lo ? lo : v < lo ? lo : v > hi ? hi : v;
}

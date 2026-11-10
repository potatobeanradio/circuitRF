using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace CircuitRF.Ui.Controls;

/// <summary>
/// <b>Eases a <see cref="ScrollViewer"/>'s vertical offset to a target instead of jumping to it.</b>
///
/// <para>Owner, 2026-09-13: clicking a band on the stackup cross-section scrolled the card list to
/// that entry's card, and the jump read as a flash. A scroll that arrives instantly gives the eye
/// nothing to follow, so the list appears to have been REPLACED rather than moved — and on a list of
/// near-identical cards that is the difference between "it went there" and "something happened".</para>
///
/// <para><b>It is not an Avalonia <c>Animation</c>.</b> <c>ScrollViewer.Offset</c> is a direct
/// property whose setter the scroller's own layout also writes (a wheel, a drag, a
/// <c>ScrollIntoView</c>), and an animation bound to it fights those rather than yielding to them. A
/// timer that writes the property is the same thing every other transient write to it is, and
/// <see cref="Cancel"/> is one call — which is what lets a second selection land cleanly on top of a
/// first one that is still moving.</para>
///
/// <para>One animator per surface, held by the view; it holds no reference to a scroller between
/// runs.</para>
/// </summary>
internal sealed class ScrollOffsetAnimator
{
    /// <summary>How long a scroll takes, at its longest. Short enough that it is never something to
    /// wait for and long enough to be followed — the same range a list flick settles in.</summary>
    public const double DurationMs = 180;

    /// <summary>Below this the animation is skipped and the offset is simply set: a sub-pixel ease is
    /// four frames of nothing.</summary>
    public const double MinPixels = 1.0;

    /// <summary>~60 Hz. <see cref="DispatcherPriority.Render"/> so a tick is not starved by the
    /// background work the editor does after a committed edit.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(16);

    /// <summary>
    /// How far into the scroll a <c>retarget</c> callback is still listened to.
    ///
    /// <para>A virtualizing list cannot say where an unrealised item is, so a long scroll starts on an
    /// ESTIMATE and is corrected as it travels — see <see cref="AnimateTo"/>. The corrections stop
    /// before the end so the landing is stable: a target still moving in the last frames is a scroll
    /// that visibly settles twice.</para>
    /// </summary>
    private const double RetargetUntil = 0.85;

    private readonly DispatcherTimer _timer;
    private ScrollViewer? _target;
    private Func<double?>? _retarget;
    private Action? _landed;
    private double _from;
    private double _to;
    private long _startedAt;

    public ScrollOffsetAnimator()
        => _timer = new DispatcherTimer(Interval, DispatcherPriority.Render, OnTick);

    /// <summary>Whether a scroll is in flight — for the gate, and for a caller that wants to know
    /// whether its own write would be overwritten.</summary>
    public bool IsRunning => _timer.IsEnabled;

    /// <summary>Stops an in-flight scroll where it is. Deliberately NOT "finish it first": the reason
    /// to cancel is that something else now decides where the list should be.</summary>
    public void Cancel()
    {
        _timer.Stop();
        _target = null;
        _retarget = null;
        _landed = null;
    }

    /// <summary>
    /// Eases <paramref name="scroll"/> to <paramref name="y"/>, clamped to what it can actually
    /// reach. A target it is already at (or within <see cref="MinPixels"/> of) is set outright.
    /// </summary>
    /// <param name="retarget">
    /// <b>Asked on every frame for a better answer, and this is what makes the scroll work over a
    /// VIRTUALIZING list.</b>
    ///
    /// <para>Such a list cannot say where an item it has not realised is, so a caller scrolling to a
    /// distant one can only estimate. The alternative — jump there, measure, jump back, then animate —
    /// is what this replaced: the jump de-realises everything at the origin, and the restored offset
    /// is rendered before the panel has realised it again, which is a blank viewport for one frame
    /// (owner, 2026-09-13: a flash, and only when scrolling DOWN, which is the direction that leaves
    /// the most behind).
    ///
    /// <para>Returning null means "still cannot tell", which is the normal answer until the scroll
    /// gets close enough to realise the target. A value re-aims the ease WITHOUT restarting it, so the
    /// motion stays one continuous movement. It is stopped listening to at
    /// <see cref="RetargetUntil"/>.</para>
    /// </param>
    /// <param name="landed">Called once the scroll has ENDED where it was going (not when it is cancelled): the
    /// moment a caller can ask whether the item it was scrolling to is really there now.</param>
    public void AnimateTo(ScrollViewer scroll, double y, Func<double?>? retarget = null, Action? landed = null)
    {
        ArgumentNullException.ThrowIfNull(scroll);
        Cancel();

        double from    = scroll.Offset.Y;
        double clamped = Clamp(scroll, y);

        if (Math.Abs(clamped - from) < MinPixels)
        {
            scroll.Offset = scroll.Offset.WithY(clamped);
            landed?.Invoke();
            return;
        }

        _target    = scroll;
        _retarget  = retarget;
        _landed    = landed;
        _from      = from;
        _to        = clamped;
        _startedAt = Stopwatch.GetTimestamp();
        _timer.Start();
    }

    private static double Clamp(ScrollViewer scroll, double y)
        => Math.Clamp(y, 0, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height));

    private void OnTick(object? sender, EventArgs e)
    {
        if (_target is not { } scroll) { _timer.Stop(); return; }

        double t = Math.Clamp(Stopwatch.GetElapsedTime(_startedAt).TotalMilliseconds / DurationMs, 0, 1);

        // The estimate the scroll set out on is replaced by the truth as soon as the list can tell it
        // — which happens once the target has been realised, i.e. once the scroll is nearly there.
        if (t < RetargetUntil && _retarget?.Invoke() is { } exact) _to = Clamp(scroll, exact);

        scroll.Offset = scroll.Offset.WithY(_from + (_to - _from) * Ease(t));
        if (t < 1) return;

        // The last frame is written as the exact target rather than as Ease(1), so a scroll always
        // ENDS where it was asked to — floating-point drift in a position the user then scrolls from
        // is a defect that accumulates. One last ask, for the case the target was realised only in
        // the final frames: a correction at this point is a pixel or two, not a visible move.
        double landed = _retarget?.Invoke() is { } last ? Clamp(scroll, last) : _to;
        scroll.Offset = scroll.Offset.WithY(landed);
        var done = _landed;
        Cancel();
        done?.Invoke();
    }

    /// <summary>
    /// Cubic ease-out: fast at once, settling at the end. Out and not in-out, because the scroll is a
    /// RESPONSE to a click that has already happened — easing IN would put a lag at the front of it,
    /// which is the one thing a response must not have.
    /// </summary>
    internal static double Ease(double t)
    {
        double u = 1 - Math.Clamp(t, 0, 1);
        return 1 - u * u * u;
    }
}

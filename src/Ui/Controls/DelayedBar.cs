using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Threading;

namespace CircuitRF.Ui.Controls;

/// <summary>
/// A progress bar that appears only once its work has run for <see cref="Delay"/>, and goes the moment the work ends. It FADES
/// — its place in the layout is always kept — so nothing beside it moves either way. Hidden, it is not indeterminate: an indeterminate
/// bar animates, and an animation nobody can see still redraws the window every frame. The Phased Array tool's bars and the Filter
/// Designer's are this one class.
/// <para><b>The delay is measured, not only timed.</b> A first version waited on a <c>DispatcherTimer</c> alone, at its default
/// Background priority — below every post the work makes, and a scan posts its progress once per direction — so the tick could be
/// starved for the whole run and the bar never appeared. Now each progress update checks the clock itself, and the timer (at Normal
/// priority) is only the fallback for work that reports nothing while it runs.</para>
/// </summary>
/// <param name="shown">What fades in and out: the bar itself, or a panel holding it and its label.</param>
/// <param name="bar">The bar: its value when the work can say how far it is, indeterminate when it cannot (NaN).</param>
internal sealed class DelayedBar(Control shown, ProgressBar bar)
{
    /// <summary>How long work must run before its bar appears: quick work never shows one.</summary>
    internal static readonly TimeSpan Delay = TimeSpan.FromSeconds(0.1);

    private DispatcherTimer? _timer;
    private bool _working;
    private double _progress = double.NaN;
    private readonly Stopwatch _since = new();

    /// <summary>On the UI thread: whether the work runs, and how far it is (0-1, or NaN when it cannot say).</summary>
    public void Sync(bool working, double progress = double.NaN)
    {
        _progress = progress;
        if (!working)
        {
            _working = false;
            _since.Reset();
            Stop();
            shown.Opacity = 0;
            bar.IsIndeterminate = false;
            return;
        }
        if (!_working) { _working = true; _since.Restart(); }
        if (shown.Opacity > 0) { Show(); return; }
        if (_since.Elapsed >= Delay) { Elapsed(); return; }
        if (_timer is not null) return;
        _timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = Delay - _since.Elapsed };
        _timer.Tick += (_, _) => Elapsed();
        _timer.Start();
    }

    /// <summary>The delay has passed: shown, if the work still runs. (A headless capture calls this itself: the headless platform
    /// never fires a dispatcher timer.)</summary>
    public void Elapsed()
    {
        Stop();
        if (!_working) return;
        shown.Opacity = 1;
        Show();
    }

    public void Stop()
    {
        _timer?.Stop();
        _timer = null;
    }

    private void Show()
    {
        bool known = double.IsFinite(_progress);
        bar.IsIndeterminate = !known;
        if (known) bar.Value = _progress;
    }
}

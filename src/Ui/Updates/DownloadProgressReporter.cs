using System;
using CircuitRF.Ui.Messages;

namespace CircuitRF.Ui.Updates;

/// <summary>
/// Drives one live Messages row from <see cref="UpdateDownloader"/>'s byte counter.
///
/// <para><b>It exists to throttle.</b> The downloader reports once per 80 KB buffer, which on a fast
/// link is several hundred calls a second, and every one of them would marshal a mutation onto the UI
/// thread and raise <c>PropertyChanged</c> into a bound control. A 160 MB payload is ~2,000 reports;
/// at the shipping interval it becomes a few dozen. The bar cannot move perceptibly faster than this
/// anyway — the throttle costs the user nothing and is the difference between a progress bar and a
/// denial-of-service on the dispatcher.</para>
///
/// <para><b>The byte count is the counter, not the text.</b> <see cref="IProgressMessage.Update"/>
/// draws the bar immediately after the text, so a figure that grows inside the text moves the bar
/// sideways with it. The text is fixed for the life of the download and only the counter changes.</para>
///
/// <para>The clock is injected for the same reason the marshaller is in
/// <see cref="LiveProgressMessage"/>: a test asserting that 2,000 reports produce a bounded number of
/// updates cannot do it against a real one.</para>
/// </summary>
internal sealed class DownloadProgressReporter : IProgress<long>
{
    /// <summary>How often the row may be rewritten. Not a tuning knob anyone has needed to change.</summary>
    internal static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(100);

    private readonly IProgressMessage _live;
    private readonly string _text;
    private readonly long _total;
    private readonly long _intervalMs;
    private readonly Func<long> _nowMs;

    private long _lastReportMs;
    private bool _reportedAny;

    /// <param name="total">
    /// The asset's advertised size, or 0 when the feed did not publish one — in which case the row
    /// shows the bytes so far against an indeterminate bar, because a percentage of an unknown total
    /// is a number we would be inventing.
    /// </param>
    internal DownloadProgressReporter(
        IProgressMessage live, string text, long total,
        TimeSpan? interval = null, Func<long>? nowMs = null)
    {
        _live       = live;
        _text       = text;
        _total      = total;
        _intervalMs = (long)(interval ?? DefaultInterval).TotalMilliseconds;
        _nowMs      = nowMs ?? (() => Environment.TickCount64);

        // Deliberately NOT "now": the first report must always land, so the row shows a figure as
        // soon as the first buffer arrives rather than staying blank for the first interval.
        _lastReportMs = long.MinValue;
    }

    /// <summary>How many times the row was actually rewritten. The counter the throttle's gate reads.</summary>
    internal int Updates { get; private set; }

    /// <summary>
    /// Called from the download loop's thread — never the UI thread. Everything expensive is behind
    /// the throttle, and <see cref="LiveProgressMessage"/> does the marshalling on the far side.
    /// </summary>
    public void Report(long bytes)
    {
        long now = _nowMs();

        // The final byte always lands, whatever the clock says: a download that finishes inside one
        // interval would otherwise leave the row reading 0 and then jump straight to its outcome.
        bool complete = _total > 0 && bytes >= _total;

        if (!complete && _reportedAny && now - _lastReportMs < _intervalMs) return;

        _lastReportMs = now;
        _reportedAny  = true;
        Updates++;

        if (_total > 0)
            _live.Update(_text,
                         $"{UpdateSpace.FormatBytes(bytes)} of {UpdateSpace.FormatBytes(_total)}",
                         100.0 * bytes / _total);
        else
            _live.Update(_text, UpdateSpace.FormatBytes(bytes), indeterminate: true);
    }
}

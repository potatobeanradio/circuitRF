using System;
using Avalonia.Threading;

namespace CircuitRF.Ui.Messages;

/// <summary>
/// A live progress row that only APPEARS if the work it describes is still running after a delay,
/// and that accepts progress from any thread.
///
/// <h3>Why deferred rather than posted immediately</h3>
/// <para>Opening a document is usually instantaneous and occasionally takes tens of seconds — and
/// which one it will be is not knowable before the read, because the cost is not proportional to the
/// file size (see <see cref="CircuitRF.Design.Layout.LayoutPersistence.LoadFromFile(string,
/// System.Threading.CancellationToken, Action{int, int})"/>: it is the per-shape hole normalization,
/// not the parse). Posting a row unconditionally would put a bar and a settled summary into the
/// Messages panel every time anyone opened anything, which buries the log the panel exists to be;
/// posting none would leave a user staring at a window that is doing something invisible.
/// A row that appears only once the operation has proved itself slow is both, and needs no estimate
/// of the cost up front.</para>
///
/// <para>Progress reported before the row exists is not lost — it is retained and applied the moment
/// the row is posted, so a bar that appears two seconds in appears already showing where the work has
/// got to rather than at zero.</para>
///
/// <h3>Threading</h3>
/// <para><see cref="Report"/> is called from whatever thread the work runs on and marshals itself to
/// the UI thread; every other member is UI-thread only. <see cref="Finish"/> must run on the caller's
/// completion path — see the note on posting the ordinary outcome message.</para>
/// </summary>
internal sealed class DeferredProgressRow
{
    private readonly IMessageSink _sink;
    private readonly string _title;
    private readonly RunCancellation? _cancellation;
    private readonly DispatcherTimer _appear;

    private IProgressMessage? _row;
    private bool _settled;

    // Last reported state, so a row that appears late appears already up to date.
    private string _text;
    private string? _counter;
    private double? _percent;
    private bool _indeterminate = true;

    /// <param name="appearAfter">How long the work must still be running before the row is posted.</param>
    public DeferredProgressRow(IMessageSink sink, string title, TimeSpan appearAfter,
                               RunCancellation? cancellation = null)
    {
        _sink = sink;
        _title = title;
        _text = title;
        _cancellation = cancellation;

        _appear = new DispatcherTimer(appearAfter, DispatcherPriority.Background, (_, _) => Appear());
        _appear.Start();
    }

    /// <summary>True once the row is actually on screen — the caller reads this to decide whether it
    /// still owes the user an ordinary "opened" message, or whether this row will say so itself.</summary>
    public bool IsVisible => _row is not null;

    private void Appear()
    {
        _appear.Stop();
        if (_settled || _row is not null) return;

        _row = _sink.BeginProgress(_title);
        _row.BindCancellation(_cancellation);
        _row.Update(_text, _counter, _percent, _indeterminate);
    }

    /// <summary>Reports progress from the working thread. Cheap when the row has not appeared yet —
    /// it only records the state.</summary>
    public void Report(string text, string? counter = null, double? percent = null, bool indeterminate = false)
    {
        if (Dispatcher.UIThread.CheckAccess()) Apply();
        else Dispatcher.UIThread.Post(Apply, DispatcherPriority.Background);

        void Apply()
        {
            if (_settled) return;
            _text = text; _counter = counter; _percent = percent; _indeterminate = indeterminate;
            _row?.Update(text, counter, percent, indeterminate);
        }
    }

    /// <summary>
    /// Settles the row, if there is one, and reports whether it did.
    ///
    /// <para>Returns false when the work finished before the row ever appeared — the ordinary,
    /// fast case — so the caller posts whatever plain message it would have posted anyway and the
    /// Messages panel looks exactly as it did before this existed. Returns true when the row appeared
    /// and has just been settled with <paramref name="outcome"/>, which is then the only line about
    /// this operation and must therefore say everything worth saying.</para>
    /// </summary>
    public bool Finish(MessageLevel level, string outcome, string? filePath = null)
    {
        _appear.Stop();
        if (_settled) return _row is not null;
        _settled = true;

        if (_row is null) return false;
        _row.Complete(level, outcome);
        _ = filePath;   // the live row carries no file link; the caller posts one if it needs it
        return true;
    }
}

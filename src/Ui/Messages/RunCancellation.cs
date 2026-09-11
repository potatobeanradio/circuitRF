using System;

namespace CircuitRF.Ui.Messages;

/// <summary>
/// One long operation's Cancel, as a thing that can be handed to a progress bar.
///
/// <h3>Why a handle rather than a <see cref="System.Threading.CancellationTokenSource"/></h3>
/// <para>A token source can be cancelled but cannot be ASKED anything a UI needs to know: whether
/// cancelling is still meaningful (the run may already have settled), whether somebody has already
/// asked (so a second press is not a second request), or what to say while the stop is pending —
/// cancellation here lands at a work boundary, never instantly, so "stopping" is a state that lasts
/// seconds and has to be visible. This carries those three facts and raises
/// <see cref="StateChanged"/> when they change, which is what lets a progress bar's context menu, a
/// panel's Cancel button and a dialog's own button all read one truth instead of three copies.</para>
///
/// <h3>One operation, however many bars it is drawn on</h3>
/// <para>An EM run and a wirebond run each post TWO live rows — a sweep row and a stage row — because
/// one bar cannot answer both "how far through" and "what is it doing". They are two views of ONE
/// computation, so both bind the SAME instance of this and either one's Cancel stops the whole run.
/// Binding a second instance per row would give the user two Cancels that each stop half of
/// nothing.</para>
///
/// <h3>UI thread</h3>
/// <para>Every member is called from the UI thread — a right-click, a button, or the run's own
/// completion path, which is already marshalled there. The lock is defensive rather than load-bearing:
/// it costs nothing at this call rate and makes a stray background <see cref="Finish"/> harmless.</para>
/// </summary>
public sealed class RunCancellation
{
    private readonly Action  _cancel;
    private readonly Action? _stop;
    private readonly object _gate = new();
    private bool _requested;
    private bool _stopRequested;
    private bool _finished;

    /// <param name="what">
    /// What is being stopped, in a form that reads inside a sentence — "the EM run", "the Touchstone
    /// export". Used for the menu item's tooltip, so it says which operation the bar belongs to.
    /// </param>
    /// <param name="cancel">
    /// What actually stops it. Called at most once, on the UI thread. Typically
    /// <c>CancellationTokenSource.Cancel</c> plus whatever the host wants to say about it.
    /// </param>
    /// <param name="stop">
    /// <b>What FINISHES it early and keeps the results</b>, or null when the operation has no such
    /// thing — which is every operation but the EM run. See <see cref="Stop"/>.
    /// </param>
    public RunCancellation(string what, Action cancel, Action? stop = null)
    {
        What    = what ?? "";
        _cancel = cancel ?? throw new ArgumentNullException(nameof(cancel));
        _stop   = stop;
    }

    /// <summary>What is being stopped — "the EM run".</summary>
    public string What { get; }

    /// <summary>True once somebody has asked. The work usually keeps running for a while after this:
    /// cancellation is answered at the next work boundary.</summary>
    public bool IsCancellationRequested { get { lock (_gate) return _requested; } }

    /// <summary>True once the operation is over, cancelled or not — after which Cancel is a no-op.</summary>
    public bool IsFinished { get { lock (_gate) return _finished; } }

    /// <summary>Whether asking now would do anything: nothing has asked yet and the run is still going.</summary>
    public bool CanCancel { get { lock (_gate) return !_requested && !_finished; } }

    /// <summary>Raised whenever <see cref="CanCancel"/> may have changed, so every surface showing this
    /// operation's Cancel can re-read it.</summary>
    public event EventHandler? StateChanged;

    /// <summary>
    /// Asks the operation to stop. Idempotent, and a no-op once the run has settled — so a Cancel
    /// pressed on a bar that finished while the menu was open does nothing rather than cancelling
    /// whatever ran next.
    /// </summary>
    public void Cancel()
    {
        lock (_gate)
        {
            if (_requested || _finished) return;
            _requested = true;
        }

        _cancel();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// <b>Whether this operation can be STOPPED — finished early with its results kept</b> (owner
    /// request, 2026-09-11). False for everything that was only ever given a Cancel.
    ///
    /// <para>The distinction is not cosmetic and the two are not alternatives: Cancel THROWS THE RUN
    /// AWAY, which is right when it should not have been started, and Stop finishes it now and
    /// publishes everything solved so far, which is right when it has already produced what was
    /// wanted. An EM run is where that second case lives — a resonance search can go on adding
    /// full-wave points long after the resonance a user came for is on screen.</para>
    /// </summary>
    public bool CanOfferStop => _stop is not null;

    /// <summary>True once somebody has asked to stop. Like a cancel, the work keeps running for a
    /// while after: the engine answers at its next work boundary.</summary>
    public bool IsStopRequested { get { lock (_gate) return _stopRequested; } }

    /// <summary>Whether asking to stop now would do anything: this operation offers one, nobody has
    /// asked yet — for either kind of halt — and the run is still going.</summary>
    public bool CanStop
    {
        get { lock (_gate) return _stop is not null && !_stopRequested && !_requested && !_finished; }
    }

    /// <summary>
    /// Asks the operation to finish early and keep what it has. Idempotent, a no-op once the run has
    /// settled or a CANCEL has already been asked for — a cancel is the stronger request and a stop
    /// after it would be asking for a result that is already being thrown away.
    /// </summary>
    public void Stop()
    {
        Action? stop;
        lock (_gate)
        {
            if (_stop is null || _stopRequested || _requested || _finished) return;
            _stopRequested = true;
            stop = _stop;
        }

        stop!();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Marks the operation over. Called from the host's <c>finally</c>, so it runs on every
    /// exit path — completed, cancelled, or thrown.</summary>
    public void Finish()
    {
        lock (_gate)
        {
            if (_finished) return;
            _finished = true;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}

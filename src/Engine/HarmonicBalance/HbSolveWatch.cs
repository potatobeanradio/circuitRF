using System.Diagnostics;
using System.Globalization;

namespace CircuitRF.Engine.HarmonicBalance;

/// <summary>
/// A Newton loop's one check per iteration: the run's Stop, and — once the solve has been going long enough for anyone to
/// wonder — which iteration it is on and how far the residual has come.
///
/// <para><b>Why the loop checks at all</b> (designer round 38). A two-tone rectifier at MaxMixOrder 10 is one HB solve of
/// about two minutes: 31 iterations, each a dense factorisation of a few thousand unknowns. Checking only at point boundaries
/// meant Stop said "stopping" and the solve ran on, so the next Run was refused as "already running" until it finished. A
/// token read once per iteration costs nothing beside that factorisation, and a cancelled solve throws
/// <see cref="OperationCanceledException"/>, which every caller already treats as an abandoned run.</para>
///
/// <para><b>Why it says nothing for the first second.</b> A sweep runs thousands of sub-millisecond solves, and every report
/// is a post to the UI thread; a solve that is over before a reader could look at it has nothing to report.</para>
/// </summary>
internal sealed class HbSolveWatch
{
    /// <summary>How long a solve runs before it starts reporting its iterations.</summary>
    internal const long QuietMs = 1000;

    /// <summary>The least time between two reports once it has started.</summary>
    internal const long IntervalMs = 250;

    private readonly RunControl _control;
    private readonly Stopwatch  _clock = Stopwatch.StartNew();
    private long _lastMs = -IntervalMs;

    private HbSolveWatch(RunControl control) => _control = control;

    /// <summary>The watch for a solve under <paramref name="settings"/>; null when the run has no control to answer to.</summary>
    internal static HbSolveWatch? For(AnalysisSettings settings) =>
        settings.Control is { } control ? new HbSolveWatch(control) : null;

    /// <summary>Iteration <paramref name="iter"/> (zero-based) is about to start from residual <paramref name="residual"/>
    /// (amperes, the loop's own ‖F‖). Throws when the run was stopped.</summary>
    internal void Iteration(int iter, double residual)
    {
        _control.ThrowIfCancellationRequested();
        long ms = _clock.ElapsedMilliseconds;
        if (ms < QuietMs || ms - _lastMs < IntervalMs) return;
        _lastMs = ms;
        _control.SetStageDetail(Describe(iter, residual));
    }

    /// <summary>The detail shown beside the stage: "Newton iteration 12, residual 3.4E-04 A".</summary>
    internal static string Describe(int iter, double residual) =>
        string.Create(CultureInfo.InvariantCulture, $"Newton iteration {iter + 1}, residual {residual:0.0E+00} A");
}

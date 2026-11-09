using System.Globalization;
using CircuitRF.Diagnostics;

namespace CircuitRF.Engine.HarmonicBalance;

/// <summary>The oscillator analysis's messages (brief NZ-9, <see cref="OscEngine"/>), as coded diagnostics.</summary>
public static class OscDiagnostics
{
    private static string Ghz(double hz) => (hz / 1e9).ToString("G6", CultureInfo.InvariantCulture);

    /// <summary>No OscProbe in the circuit.</summary>
    public static Diagnostic NoProbe(string analysis) => Diagnostic.Create(
        "osc.no-probe", DiagnosticSeverity.Error,
        "Oscillator analysis '{analysis}' needs one OscProbe, in series in the oscillator's loop or across its resonator, " +
        "and this circuit has none.",
        ("analysis", analysis));

    /// <summary>Several OscProbes and no Probe= to choose one.</summary>
    public static Diagnostic SeveralProbes(string analysis, string probes) => Diagnostic.Create(
        "osc.several-probes", DiagnosticSeverity.Error,
        "Oscillator analysis '{analysis}' needs exactly one OscProbe, and this circuit has several ({probes}). Remove the " +
        "others.",
        ("analysis", analysis), ("probes", probes));

    /// <summary>Probe= naming no OscProbe.</summary>
    public static Diagnostic UnknownProbe(string analysis, string probe) => Diagnostic.Create(
        "osc.unknown-probe", DiagnosticSeverity.Error,
        "Oscillator analysis '{analysis}': Probe={probe} names no OscProbe in this circuit.",
        ("analysis", analysis), ("probe", probe));

    /// <summary>An unusable frequency key.</summary>
    public static Diagnostic BadSpan(string analysis, double startHz, double guessHz, double stopHz) => Diagnostic.Create(
        "osc.bad-span", DiagnosticSeverity.Error,
        "Oscillator analysis '{analysis}': the start-up search needs 0 < Fstart < Fguess < Fstop, and has Fstart={start} GHz, " +
        "Fguess={guess} GHz, Fstop={stop} GHz.",
        ("analysis", analysis), ("start", Ghz(startHz)), ("guess", Ghz(guessHz)), ("stop", Ghz(stopHz)));

    /// <summary>The start-up condition is met nowhere in the span.</summary>
    public static Diagnostic NoStartup(string analysis, string probe, bool shunt, double startHz, double stopHz) => Diagnostic.Create(
        "osc.no-startup", DiagnosticSeverity.Error,
        "Oscillator analysis '{analysis}': the small-signal {what} at {probe} has no negative real part at a zero of its " +
        "imaginary part between {start} and {stop} GHz, so the circuit does not start to oscillate there and there is no " +
        "oscillation to solve for. A WSProbe in the same place reports the same: no growing mode in that span. Widen " +
        "Fstart/Fstop, or check the bias and the loop gain.",
        ("analysis", analysis), ("probe", probe), ("what", shunt ? "admittance" : "impedance"),
        ("start", Ghz(startHz)), ("stop", Ghz(stopHz)));

    /// <summary>The ramp from a small amplitude never reached a limit.</summary>
    public static Diagnostic NoLimit(string analysis, double startupHz, string lastAmplitude) => Diagnostic.Create(
        "osc.no-limit", DiagnosticSeverity.Error,
        "Oscillator analysis '{analysis}': the circuit starts at {f} GHz, but its negative resistance never runs out as the " +
        "amplitude grows (still negative at {a}), so the oscillation has no limit for harmonic balance to find. A real " +
        "oscillator limits through a nonlinearity: check that the active device is a nonlinear model.",
        ("analysis", analysis), ("f", Ghz(startupHz)), ("a", lastAmplitude));

    /// <summary>A harmonic-balance solve failed on the way.</summary>
    public static Diagnostic SolveFailed(string analysis, double hz, string amplitude, string reason) => Diagnostic.Create(
        "osc.solve-failed", DiagnosticSeverity.Error,
        "Oscillator analysis '{analysis}': the harmonic-balance solve at {f} GHz with the probe's generator at {a} did not " +
        "converge ({reason}). Give Aguess near the expected amplitude, or raise MaxHarm.",
        ("analysis", analysis), ("f", Ghz(hz)), ("a", amplitude), ("reason", reason));

    /// <summary>The outer Newton on amplitude and frequency stopped short.</summary>
    public static Diagnostic NotConverged(string analysis, double hz, string amplitude, double residual) => Diagnostic.Create(
        "osc.not-converged", DiagnosticSeverity.Error,
        "Oscillator analysis '{analysis}': the search for the oscillation stopped at {f} GHz and {a} with the probe's " +
        "generator still doing work ({r} of the probe's own immittance scale). Give Fguess and Aguess nearer the answer, or " +
        "narrow Fstart/Fstop around the start-up frequency.",
        ("analysis", analysis), ("f", Ghz(hz)), ("a", amplitude), ("r", residual.ToString("E2", CultureInfo.InvariantCulture)));
}

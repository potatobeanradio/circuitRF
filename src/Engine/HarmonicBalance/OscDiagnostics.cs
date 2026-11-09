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

    // ── Brief NZ-10: phase noise ─────────────────────────────────────────────

    private static string Hz(double hz) => hz.ToString("G6", CultureInfo.InvariantCulture) + " Hz";

    /// <summary>A phase-noise key that does not resolve.</summary>
    public static Diagnostic PnBadKey(string analysis, string key, string text) => Diagnostic.Create(
        "osc.pn.bad-key", DiagnosticSeverity.Error,
        "Oscillator analysis '{analysis}': {key}={text} does not resolve. PnStart and PnStop are positive offsets from the " +
        "carrier (a number or a variable, with an optional Hz, kHz, MHz or GHz), PnNpts a whole number of points and " +
        "PnPort the Num of a Port or Term.",
        ("analysis", analysis), ("key", key), ("text", text));

    /// <summary>An offset sweep that is empty.</summary>
    public static Diagnostic PnBadSweep(string analysis, double startHz, double stopHz, string npts) => Diagnostic.Create(
        "osc.pn.bad-sweep", DiagnosticSeverity.Error,
        "Oscillator analysis '{analysis}': the phase-noise offsets need PnStart < PnStop and at least two points, and have " +
        "PnStart={start}, PnStop={stop}, PnNpts={npts}.",
        ("analysis", analysis), ("start", Hz(startHz)), ("stop", Hz(stopHz)), ("npts", npts));

    /// <summary>Nowhere to read the noise.</summary>
    public static Diagnostic PnNoOutput(string analysis, string probe) => Diagnostic.Create(
        "osc.pn.no-output", DiagnosticSeverity.Error,
        "Oscillator analysis '{analysis}': the OscProbe {probe} is in series, so it holds no voltage to read the phase noise " +
        "across, and the circuit has no single Port or Term to read it at instead. Give PnPort=<Num> of the output port.",
        ("analysis", analysis), ("probe", probe));

    /// <summary>PnPort naming no port.</summary>
    public static Diagnostic PnNoPort(string analysis, int num) => Diagnostic.Create(
        "osc.pn.no-port", DiagnosticSeverity.Error,
        "Oscillator analysis '{analysis}': PnPort={num} names no top-level Port or Term.",
        ("analysis", analysis), ("num", num));

    /// <summary>No carrier where the noise is read.</summary>
    public static Diagnostic PnNoCarrier(string analysis, string where) => Diagnostic.Create(
        "osc.pn.no-carrier", DiagnosticSeverity.Error,
        "Oscillator analysis '{analysis}': the oscillation has no voltage at the fundamental across {where}, so there is no " +
        "carrier to refer the noise to. Read it where the oscillation is (PnPort=).",
        ("analysis", analysis), ("where", where));

    public const string PnAboveHalfKey = "osc.pn.above-half";
    /// <summary>Offsets at or beyond half the carrier.</summary>
    public static Diagnostic PnAboveHalf(string analysis, double foscHz) => Diagnostic.Create(
        PnAboveHalfKey, DiagnosticSeverity.Warning,
        "Oscillator analysis '{analysis}': offsets at or beyond half the carrier ({half}) are NaN. There the upper sideband " +
        "of one harmonic is the lower sideband of the next, so an offset no longer names one side of the carrier.",
        ("analysis", analysis), ("half", Hz(foscHz / 2)));

    public const string PnSingularKey = "osc.pn.singular";
    /// <summary>A conversion matrix that would not factorise.</summary>
    public static Diagnostic PnSingular(string analysis, double offsetHz) => Diagnostic.Create(
        PnSingularKey, DiagnosticSeverity.Warning,
        "Oscillator analysis '{analysis}': the conversion matrix at the offset {f} is singular; the phase noise there is NaN.",
        ("analysis", analysis), ("f", Hz(offsetHz)));

    public const string PnNoRelaxationKey = "osc.pn.no-relaxation";
    /// <summary>The amplitude has no restoring force the join can be placed against.</summary>
    public static Diagnostic PnNoRelaxation(string analysis) => Diagnostic.Create(
        PnNoRelaxationKey, DiagnosticSeverity.Warning,
        "Oscillator analysis '{analysis}': the oscillation's amplitude shows no restoring force at the probe, so the offset " +
        "below which the frequency-sensitivity description holds cannot be placed. The phase noise is the conversion " +
        "matrix's at every offset.",
        ("analysis", analysis));

    public const string PnJoinKey = "osc.pn.join";
    /// <summary>The two descriptions disagree where they are joined.</summary>
    public static Diagnostic PnJoinMismatch(string analysis, double joinHz, double dbDiff) => Diagnostic.Create(
        PnJoinKey, DiagnosticSeverity.Warning,
        "Oscillator analysis '{analysis}': at the join offset {f} the close-in phase noise (from the frequency's sensitivity) " +
        "and the conversion matrix's differ by {d} dB. Both should hold there; a difference this large means one of them " +
        "does not: tighten Tol, or raise MaxHarm.",
        ("analysis", analysis), ("f", Hz(joinHz)), ("d", dbDiff.ToString("F2", CultureInfo.InvariantCulture)));

    public const string PnCornerKey = "osc.pn.corner";
    /// <summary>A model whose flicker cannot be separated from its white noise.</summary>
    public static Diagnostic PnCornerUnsplit(string analysis, string parts) => Diagnostic.Create(
        PnCornerKey, DiagnosticSeverity.Info,
        "Oscillator analysis '{analysis}': PNcorner is NaN because {parts} report their noise as one density, not as white " +
        "and flicker parts. PN itself includes their flicker.",
        ("analysis", analysis), ("parts", parts));
}

using CircuitRF.Diagnostics;

namespace CircuitRF.Engine;

/// <summary>The S-parameter noise analysis' messages (<see cref="SParameterNoise"/>), as coded diagnostics.</summary>
public static class NoiseDiagnostics
{
    /// <summary>The key the noiseless-parts warning is reported once under.</summary>
    public const string NoiselessKey = "sparam-noise-noiseless";

    /// <summary>Noise=yes on the legacy path: a port whose Z0 has no positive real part terminates nothing.</summary>
    public static Diagnostic ReactiveReference(int port, double reZ0) => Diagnostic.Create(
        "sparam.noise.reactive-z0", DiagnosticSeverity.Error,
        "Noise=yes needs every port terminated in a Z0 with a positive real part, and port {port} has Re Z0 = {re} Ohm. " +
        "The noise waves are defined against that termination: give the port a resistive part, or set Noise=no.",
        ("port", port), ("re", reZ0));

    /// <summary>The key prefix an S-parameter block's out-of-band noise data is reported once under, per instance.</summary>
    public const string DataBandKey = "sparam-noise-data-band";

    /// <summary>The key prefix an S-parameter block's unphysical noise data is reported once under, per instance.</summary>
    public const string DataUnphysicalKey = "sparam-noise-data-unphysical";

    /// <summary>The sweep leaves the band of a block's noise rows; the end rows are held outside it.</summary>
    public static Diagnostic DataOutsideBand(string who, double loHz, double hiHz, double sweepLoHz, double sweepHiHz) =>
        Diagnostic.Create(
            "sparam.noise.data-band", DiagnosticSeverity.Warning,
            "Noise: {who}'s noise data covers {lo} to {hi} GHz and the sweep runs from {from} to {to} GHz; outside its " +
            "rows the nearest row's noise parameters are used.",
            ("who", who), ("lo", Ghz(loHz)), ("hi", Ghz(hiHz)), ("from", Ghz(sweepLoHz)), ("to", Ghz(sweepHiHz)));

    /// <summary>A block's noise rows are not a physical two-port's; they are used as given.</summary>
    public static Diagnostic DataUnphysical(string who, double firstHz, int bad, int rows) => Diagnostic.Create(
        "sparam.noise.data-unphysical", DiagnosticSeverity.Warning,
        "Noise: {who}'s noise data is not physical in {bad} of its {rows} rows, first at {at} GHz: a two-port needs " +
        "0 <= Fmin - 1 <= 4*Rn*Re(Yopt). The data is used as given.",
        ("who", who), ("bad", bad), ("rows", rows), ("at", Ghz(firstHz)));

    private static string Ghz(double hz) => (hz / 1e9).ToString("G6", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Parts the noise analysis has no model for, named once per run.</summary>
    public static Diagnostic Noiseless(IReadOnlyList<string> instances)
    {
        const int Shown = 10;
        string names = string.Join(", ", instances.Take(Shown)) +
                       (instances.Count > Shown ? $" and {instances.Count - Shown} more" : "");
        return Diagnostic.Create(
            "sparam.noise.noiseless", DiagnosticSeverity.Warning,
            "Noise: {names} {verb} no noise model and {count} taken as noiseless (passive linear parts are given " +
            "thermal noise, and an S-parameter block the noise data in its file).",
            ("names", names), ("verb", instances.Count == 1 ? "has" : "have"), ("count", instances.Count == 1 ? "is" : "are"));
    }
}

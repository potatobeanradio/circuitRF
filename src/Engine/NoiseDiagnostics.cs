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

    /// <summary>Parts the noise analysis has no model for, named once per run.</summary>
    public static Diagnostic Noiseless(IReadOnlyList<string> instances)
    {
        const int Shown = 10;
        string names = string.Join(", ", instances.Take(Shown)) +
                       (instances.Count > Shown ? $" and {instances.Count - Shown} more" : "");
        return Diagnostic.Create(
            "sparam.noise.noiseless", DiagnosticSeverity.Warning,
            "Noise: {names} {verb} no noise model and {count} taken as noiseless (only passive linear parts are given " +
            "thermal noise here).",
            ("names", names), ("verb", instances.Count == 1 ? "has" : "have"), ("count", instances.Count == 1 ? "is" : "are"));
    }
}

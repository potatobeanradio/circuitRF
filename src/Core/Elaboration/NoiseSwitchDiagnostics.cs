using CircuitRF.Diagnostics;

namespace CircuitRF.Core.Elaboration;

/// <summary>
/// The refusal of a part's noise switch (<see cref="Elaborator.NoiseParamName"/>, brief NZ-1), as a coded diagnostic
/// rather than a bare sentence (tests/Firewall.Tests/user-facing-text-allowlist.txt says why).
/// </summary>
public static class NoiseSwitchDiagnostics
{
    public static Diagnostic NotYesOrNo(string instance, string got) => Diagnostic.Create(
        "elaborate.noise.switch", DiagnosticSeverity.Error,
        "'{instance}': Noise={got} is not yes or no. Noise=no makes the part noiseless in the S-parameter noise analysis; " +
        "Noise=yes, the default, leaves it as noisy as its model.",
        ("instance", instance), ("got", got));
}

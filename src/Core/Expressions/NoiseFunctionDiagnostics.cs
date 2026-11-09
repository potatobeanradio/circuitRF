using CircuitRF.Diagnostics;

namespace CircuitRF.Core.Expressions;

/// <summary>
/// The refusals of <c>nf(SP1.S, Γs)</c> and <c>nf_circle(SP1.S, NFdB[, npts])</c> (Evaluator.Network.cs), as coded
/// diagnostics rather than bare sentences (tests/Firewall.Tests/user-facing-text-allowlist.txt says why).
/// </summary>
public static class NoiseFunctionDiagnostics
{
    public static Diagnostic NotTwoPort(string name, int ports) => Diagnostic.Create(
        "measure.noise.not-two-port", DiagnosticSeverity.Error,
        "{name}(): noise parameters belong to a two-port, and this network has {ports} ports. Its noise is the Cs cube.",
        ("name", name), ("ports", ports));

    public static Diagnostic NotRun(string name) => Diagnostic.Create(
        "measure.noise.not-run", DiagnosticSeverity.Error,
        "{name}(): the analysis that produced this S cube ran without noise, so it has no NFmin, Rn and GammaOpt. " +
        "Add Noise=yes to its sparam directive.",
        ("name", name));

    public static Diagnostic CubeShape(string name, string cube, int got, int want) => Diagnostic.Create(
        "measure.noise.cube-shape", DiagnosticSeverity.Error,
        "{name}(): the analysis's {cube} cube holds {got} values, and its S cube has {want} frequency points.",
        ("name", name), ("cube", cube), ("got", got), ("want", want));

    public static Diagnostic Source(string name, string got, int want) => Diagnostic.Create(
        "measure.noise.source", DiagnosticSeverity.Error,
        "{name}(): the source reflection is a number, or one value per frequency point of the S cube ({want}); got {got}.",
        ("name", name), ("got", got), ("want", want));

    public static Diagnostic Figure(string name, string got) => Diagnostic.Create(
        "measure.noise.figure", DiagnosticSeverity.Error,
        "{name}(): the noise figure is a real number in dB, as in {name}(SP1.S, 2); got {got}.",
        ("name", name), ("got", got));

    public static Diagnostic CirclePoints(string name, string got) => Diagnostic.Create(
        "measure.noise.circle-points", DiagnosticSeverity.Error,
        "{name}(): the point count is a whole number of at least 3; got {got}.",
        ("name", name), ("got", got));
}

using CircuitRF.Diagnostics;

namespace CircuitRF.Core.Expressions;

/// <summary>
/// The refusals of the noise functions <c>nf</c>, <c>te</c>, <c>rn</c> and <c>ns_circle</c> (Evaluator.Network.cs), as
/// coded diagnostics rather than bare sentences (tests/Firewall.Tests/user-facing-text-allowlist.txt says why).
/// </summary>
public static class NoiseFunctionDiagnostics
{
    public static Diagnostic NotTwoPort(string name, int ports) => Diagnostic.Create(
        "measure.noise.not-two-port", DiagnosticSeverity.Error,
        "{name}(): noise parameters belong to a two-port, and this network has {ports} ports. On more ports, nf() and te() " +
        "take the output port, as in nf(SP1.S, 0, 3), and the whole network's noise is the Cs cube.",
        ("name", name), ("ports", ports));

    public static Diagnostic NotRun(string name) => Diagnostic.Create(
        "measure.noise.not-run", DiagnosticSeverity.Error,
        "{name}(): the analysis that produced this S cube ran without noise, so it has no Cs cube. " +
        "Add Noise=yes to its sparam directive.",
        ("name", name));

    public static Diagnostic CubeShape(string name, string cube, int got, int want) => Diagnostic.Create(
        "measure.noise.cube-shape", DiagnosticSeverity.Error,
        "{name}(): the analysis's {cube} cube holds {got} values, and its S cube's shape needs {want}.",
        ("name", name), ("cube", cube), ("got", got), ("want", want));

    public static Diagnostic Port(string name, int ports, string got) => Diagnostic.Create(
        "measure.noise.port", DiagnosticSeverity.Error,
        "{name}(): port 1 is the source, so the output port is a whole number from 2 to {ports}; got {got}.",
        ("name", name), ("ports", ports), ("got", got));

    public static Diagnostic Source(string name, string got, int want) => Diagnostic.Create(
        "measure.noise.source", DiagnosticSeverity.Error,
        "{name}(): the source reflection is a number, or one value per frequency point of the S cube ({want}); got {got}.",
        ("name", name), ("got", got), ("want", want));

    public static Diagnostic Figure(string name, string got) => Diagnostic.Create(
        "measure.noise.figure", DiagnosticSeverity.Error,
        "{name}(): the noise figure is a real number in dB, as in {name}(SP1.S, 2), or one per frequency point, as in " +
        "{name}(SP1.S, SP1.NFmin + 0.5); got {got}.",
        ("name", name), ("got", got));

    public static Diagnostic CirclePoints(string name, string got) => Diagnostic.Create(
        "measure.noise.circle-points", DiagnosticSeverity.Error,
        "{name}(): the point count is a whole number of at least 3; got {got}.",
        ("name", name), ("got", got));

    public static Diagnostic CircleForm(string name, string got) => Diagnostic.Create(
        "measure.noise.circle-form", DiagnosticSeverity.Error,
        "{name}(): got {got}. A noise circle is {name}(SP1.S, NFdB[, npts]) on the S cube of a run with Noise=yes, " +
        "or {name}(NFdB, NFmin, Sopt, rn[, npts]) from the noise parameters.",
        ("name", name), ("got", got));

    public static Diagnostic Parameter(string name, string parameter, string what, string got) => Diagnostic.Create(
        "measure.noise.parameter", DiagnosticSeverity.Error,
        "{name}(): {parameter} is {what}: a number, or a cube with as many values as the other cube arguments; got {got}.",
        ("name", name), ("parameter", parameter), ("what", what), ("got", got));
}

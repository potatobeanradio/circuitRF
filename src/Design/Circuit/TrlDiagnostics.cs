using CircuitRF.Diagnostics;

namespace CircuitRF.Design.Circuit;

/// <summary>
/// A TRL analysis's refusals (brief-de-4), as coded diagnostics rather than bare sentences
/// (tests/Firewall.Tests/user-facing-text-allowlist.txt says why). The text is what the analysis said before; each opens
/// with <c>TRL '&lt;name&gt;'</c>, and <c>key</c> is the directive's own key (<c>thru</c>, <c>reflect</c>, <c>line</c>).
/// </summary>
public static class TrlDiagnostics
{
    public static Diagnostic NoBlockNamed(string analysis, string key) => Diagnostic.Create(
        "trl.standard.unnamed", DiagnosticSeverity.Error,
        "TRL '{analysis}': {key}= names no SnP block.", ("analysis", analysis), ("key", key));

    /// <summary><paramref name="snps"/> is the bench's SnP blocks, joined, or empty when it has none.</summary>
    public static Diagnostic NoInstance(string analysis, string key, string instance, string snps) => snps.Length == 0
        ? Diagnostic.Create("trl.standard.not-found", DiagnosticSeverity.Error,
              "TRL '{analysis}': {key}={instance} names no instance on this bench, which has no SnP block.",
              ("analysis", analysis), ("key", key), ("instance", instance))
        : Diagnostic.Create("trl.standard.not-found", DiagnosticSeverity.Error,
              "TRL '{analysis}': {key}={instance} names no instance on this bench; its SnP blocks are {snps}.",
              ("analysis", analysis), ("key", key), ("instance", instance), ("snps", snps));

    public static Diagnostic NotAnSnp(string analysis, string key, string instance, string type) => Diagnostic.Create(
        "trl.standard.not-snp", DiagnosticSeverity.Error,
        "TRL '{analysis}': {key}={instance} is a {type}; it must be an SnP holding the measured {key}.",
        ("analysis", analysis), ("key", key), ("instance", instance), ("type", type));

    public static Diagnostic NotTwoPort(string analysis, string key, string instance, int ports) => Diagnostic.Create(
        "trl.standard.not-two-port", DiagnosticSeverity.Error,
        "TRL '{analysis}': {key}={instance} is a {ports}-port SnP; TRL reads two-ports.",
        ("analysis", analysis), ("key", key), ("instance", instance), ("ports", ports));

    public static Diagnostic NoFile(string analysis, string key, string instance) => Diagnostic.Create(
        "trl.standard.no-file", DiagnosticSeverity.Error,
        "TRL '{analysis}': {key}={instance} has no Touchstone file set.",
        ("analysis", analysis), ("key", key), ("instance", instance));

    public static Diagnostic FileMissing(string analysis, string key, string instance, string path) => Diagnostic.Create(
        "trl.standard.file-missing", DiagnosticSeverity.Error,
        "TRL '{analysis}': {key}={instance}'s file was not found: '{path}'.",
        ("analysis", analysis), ("key", key), ("instance", instance), ("path", path));

    /// <summary>The solver's own refusal (<c>TrlCalibration.Solve</c>), forwarded with the analysis's name in front.</summary>
    public static Diagnostic SolveRefused(string analysis, string refusal) => Diagnostic.Create(
        "trl.solve.refused", DiagnosticSeverity.Error,
        "TRL '{analysis}': {refusal}.", ("analysis", analysis), ("refusal", refusal));

    public static Diagnostic SameFixtureFile(string analysis, string stored) => Diagnostic.Create(
        "trl.fixtures.same-file", DiagnosticSeverity.Error,
        "TRL '{analysis}': fixtureA and fixtureB are the same file, '{stored}'.",
        ("analysis", analysis), ("stored", stored));
}

/// <summary>A TRL analysis refused, carrying its <see cref="Diagnostic"/>; its message is the diagnostic rendered.</summary>
public sealed class TrlRefusalException(Diagnostic diagnostic) : InvalidOperationException(diagnostic.Render())
{
    public Diagnostic Diagnostic { get; } = diagnostic;
}

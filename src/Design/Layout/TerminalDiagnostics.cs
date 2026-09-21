using CircuitRF.Diagnostics;

namespace CircuitRF.Design.Layout;

/// <summary>
/// The terminal map's findings, as coded diagnostics (<c>brief-lvs-1-terminal-map.md</c> §5).
///
/// <para><b>They are authored HERE, below the UI firewall, and not in <c>CliDiagnostics</c></b>
/// — R-lvs1-4c: a rule that exists only in <c>check</c> is a rule the application does not enforce,
/// so a design would pass headlessly and be refused when someone opened it. <c>circuitrf check</c>
/// and the cell Properties panel report the same id, the same severity and the same sentence because
/// there is only one of each.</para>
///
/// <para><b>The ids are the durable part</b>, as <c>EmDiagnostics</c>' own header says: reword a
/// template freely; changing an id is making a new diagnostic.</para>
/// </summary>
public static class TerminalDiagnostics
{
    /// <summary>A <c>LayoutPin</c> the primary <c>.clay</c> has no pin named.</summary>
    public static Diagnostic UnknownLayoutPin(string path, int port, string pin) => Diagnostic.Create(
        "check.terminals.unknown-layout-pin", DiagnosticSeverity.Error,
        "{path}: terminal for port {port} names layout pin '{pin}', which the primary layout does not have.",
        ("path", path), ("port", port), ("pin", pin));

    /// <summary>A <c>Port</c> outside <c>1..NumPorts</c>.</summary>
    public static Diagnostic PortOutOfRange(string path, int port, int numPorts) => Diagnostic.Create(
        "check.terminals.port-out-of-range", DiagnosticSeverity.Error,
        "{path}: terminal names port {port}, and this cell declares {numPorts} port(s).",
        ("path", path), ("port", port), ("numPorts", numPorts));

    /// <summary>Two rows claim one port.</summary>
    public static Diagnostic DuplicatePort(string path, int port) => Diagnostic.Create(
        "check.terminals.duplicate-port", DiagnosticSeverity.Error,
        "{path}: two terminals claim port {port}.",
        ("path", path), ("port", port));

    /// <summary>Two terminals claim one layout pin.</summary>
    public static Diagnostic PinClaimedTwice(string path, string pin, int firstPort, int secondPort)
        => Diagnostic.Create(
            "check.terminals.pin-claimed-twice", DiagnosticSeverity.Error,
            "{path}: layout pin '{pin}' is claimed by port {firstPort} and again by port {secondPort}.",
            ("path", path), ("pin", pin), ("firstPort", firstPort), ("secondPort", secondPort));

    /// <summary>A declared port no row names.</summary>
    public static Diagnostic UnmappedPort(string path, int port) => Diagnostic.Create(
        "check.terminals.unmapped-port", DiagnosticSeverity.Warning,
        "{path}: port {port} is mapped to no layout pin.",
        ("path", path), ("port", port));

    /// <summary>A layout pin no row names. <b>A warning and no more</b> — a mounting or shield pad is
    /// exactly this and is entirely ordinary.</summary>
    public static Diagnostic UnmappedPin(string path, string pin) => Diagnostic.Create(
        "check.terminals.unmapped-pin", DiagnosticSeverity.Warning,
        "{path}: layout pin '{pin}' is not any port's terminal. A mounting or shield pad is ordinarily this.",
        ("path", path), ("pin", pin));

    /// <summary>
    /// R-lvs1-3c fired. <b>Reported on every run that derives this way, including an otherwise clean
    /// one</b> — it is a guess that happens to be right most of the time, and a guess that is never
    /// announced is the shape of a wrong answer nobody finds.
    /// </summary>
    public static Diagnostic DerivedByOrder(string path, int count) => Diagnostic.Create(
        "check.terminals.derived-by-order", DiagnosticSeverity.Warning,
        "{path}: no pin on either side is named, so the {count} terminal(s) were read positionally. "
        + "Name the pins, or state the terminals in the cell, to make this a fact rather than a guess.",
        ("path", path), ("count", count));

    /// <summary>R-lvs1-3d. The notes name BOTH unmatched lists, which is the part that makes it a
    /// two-minute fix rather than a hunt.</summary>
    public static Diagnostic Underivable(string path, IReadOnlyList<string> notes) => Diagnostic.Create(
        "check.terminals.underivable", DiagnosticSeverity.Error,
        "{path}: this cell's layout pins cannot be matched to its schematic ports. {detail}",
        ("path", path), ("detail", string.Join(" ", notes)));
}

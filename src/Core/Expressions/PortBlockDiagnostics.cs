using CircuitRF.Diagnostics;

namespace CircuitRF.Core.Expressions;

/// <summary>
/// The refusals of a port matrix read by port number, <c>SP1.S(1:2, 1:2)</c> and <c>DUT_1(2, 1)</c>
/// (Evaluator.PortBlock.cs), as coded diagnostics rather than bare sentences
/// (tests/Firewall.Tests/user-facing-text-allowlist.txt says why).
/// </summary>
public static class PortBlockDiagnostics
{
    public static Diagnostic RangeBackwards(string name, int lo, int hi, string axis) => Diagnostic.Create(
        "measure.ports.range-backwards", DiagnosticSeverity.Error,
        "{name}: the port range {lo}:{hi} on axis '{axis}' runs backwards; write the lower port first, {hi}:{lo}.",
        ("name", name), ("lo", lo), ("hi", hi), ("axis", axis));

    public static Diagnostic RangeEmpty(string name, int lo, int hi, string axis, string ports) => Diagnostic.Create(
        "measure.ports.range-empty", DiagnosticSeverity.Error,
        "{name}: no port of the result is in {lo}:{hi} on axis '{axis}' (the result's ports are {ports}).",
        ("name", name), ("lo", lo), ("hi", hi), ("axis", axis), ("ports", ports));

    public static Diagnostic PortMissing(string name, int port, string axis, string ports) => Diagnostic.Create(
        "measure.ports.not-found", DiagnosticSeverity.Error,
        "{name}: port {port} is not on axis '{axis}' (the result's ports are {ports}).",
        ("name", name), ("port", port), ("axis", axis), ("ports", ports));

    public static Diagnostic PortNotWhole(string name, string axis, string got) => Diagnostic.Create(
        "measure.ports.not-whole", DiagnosticSeverity.Error,
        "{name}: a port on axis '{axis}' is a whole number; got {got}.",
        ("name", name), ("axis", axis), ("got", got));

    public static Diagnostic RangeOutsidePorts() => new(
        "measure.ports.range-misplaced", DiagnosticSeverity.Error,
        "A port range a:b only goes inside a port matrix's parentheses, as in SP1.S(1:2, 1:2).");

    public static Diagnostic BracketRange(string axis, string target) => Diagnostic.Create(
        "measure.ports.bracket-range", DiagnosticSeverity.Error,
        "A range on port axis '{axis}' inside [ ] counts positions, not ports. For a block of ports write them " +
        "in parentheses: {target}(1:2, 1:2) is ports 1 to 2, renumbered from 1.",
        ("axis", axis), ("target", target));
}

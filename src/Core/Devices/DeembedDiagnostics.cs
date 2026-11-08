using CircuitRF.Diagnostics;

namespace CircuitRF.Core.Devices;

/// <summary>
/// A Deembed's refusals (brief-de-2), as coded diagnostics rather than bare sentences
/// (tests/Firewall.Tests/user-facing-text-allowlist.txt says why). The text is what the block said before.
/// </summary>
public static class DeembedDiagnostics
{
    public static Diagnostic NumPortsMissing() => new(
        "deembed.numports.missing", DiagnosticSeverity.Error,
        "Deembed: NumPorts parameter is missing or not a number");

    public static Diagnostic NumPortsOdd(int ports) => Diagnostic.Create(
        "deembed.numports.odd", DiagnosticSeverity.Error,
        "Deembed: NumPorts={ports}; a Deembed needs an even number of ports, one side matching the other.",
        ("ports", ports));

    public static Diagnostic OffsetRefused(string key) => Diagnostic.Create(
        "deembed.offset.refused", DiagnosticSeverity.Error,
        "Deembed: '{key}' sets an S-parameter offset, which a Deembed does not take; set it on the SnP whose " +
        "network this one removes, or take it off.",
        ("key", key));

    /// <summary>The network has no inverse at one frequency. <paramref name="why"/> is the inverse's own reason; <paramref name="hint"/>
    /// the 0 Hz advice, or empty.</summary>
    public static Diagnostic CannotUndo(string who, string file, string at, string why, string hint) => Diagnostic.Create(
        "deembed.not-invertible", DiagnosticSeverity.Error,
        "{who}: the network in '{file}' cannot be undone at {at}: {why}.{hint}",
        ("who", who), ("file", file), ("at", at), ("why", why), ("hint", hint));
}

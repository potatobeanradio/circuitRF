using System.Collections.Generic;
using System.Linq;

namespace CircuitRF.Ui.Controls;

/// <summary>
/// The file-picker patterns for a Touchstone file of any port count: <c>*.s1p</c> … <c>*.s99p</c> and <c>*.snp</c>, in
/// lower and upper case. Every picker that opens an N-port file takes its patterns from here, because a list written out
/// by hand stops somewhere (<c>*.s4p</c>, <c>*.s12p</c>) and the file one past it is not shown in the dialog at all.
/// A pattern is spelled out per port count rather than as <c>*.s*p</c>: a wildcard inside an extension is not honoured by
/// every platform's dialog, and where it is it also admits <c>.step</c> and <c>.sp</c>. Both cases are listed because
/// the Linux dialog matches case-sensitively.
/// </summary>
public static class TouchstonePatterns
{
    /// <summary>The largest port count offered: the bound the workspace archive and the viewer read too.</summary>
    public const int MaxPorts = 99;

    /// <summary><c>*.s1p</c> … <c>*.s99p</c> and <c>*.snp</c>, each in lower and upper case.</summary>
    public static IReadOnlyList<string> Snp { get; } =
    [
        .. Enumerable.Range(1, MaxPorts).Select(n => $"*.s{n}p"), "*.snp",
        .. Enumerable.Range(1, MaxPorts).Select(n => $"*.S{n}P"), "*.SNP",
    ];

    /// <summary><see cref="Snp"/> and the version-2 <c>*.ts</c> spelling.</summary>
    public static IReadOnlyList<string> SnpAndTs { get; } = [.. Snp, "*.ts", "*.TS"];
}

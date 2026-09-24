// The human-readable LVS report — ONE spelling, for `circuitrf lvs -o report.txt` and for the LVS
// panel's Copy alike.
//
// It lived in src/Cli/Lvs.cs until the panel needed to copy it. The panel's findings are rows of
// wrapped, unselectable text, so the only way to get a comparison into an email or a bug report was
// to retype it; a second formatter in the panel would have been a second answer to "what did the
// comparison say", worded slightly differently from the file the CLI writes. Moved, not copied.

using System.Linq;
using System.Text;
using CircuitRF.Diagnostics;

namespace CircuitRF.Design.Layout.Lvs;

public static class LvsReportText
{
    /// <summary>
    /// One cell: the summary line, then the findings grouped by severity, each naming its objects.
    /// </summary>
    /// <remarks>
    /// <b>The technology, the reduction mode and the counts are on the face of it</b> (R-lvs8-1a/1d,
    /// R-lvs6-5c). A report that did not say which process the layout was read against, or whether
    /// the collapse ran, is one two people can read differently — one counting eight fingers and
    /// one counting one FET, each certain the other is looking at a different design.
    /// </remarks>
    public static void Write(StringBuilder into, string name, LvsRunResult result)
    {
        into.AppendLine($"{name}: {Verdict(result)}");
        into.AppendLine(
            $"  technology {(result.TechnologyName is { Length: > 0 } t ? $"'{t}'" : "none")}, " +
            $"reduction {(result.Reduction == ReductionMode.On ? "on" : "off")}");
        into.AppendLine($"  {result.Counts.Describe()}");

        foreach (var severity in new[]
                 { DiagnosticSeverity.Error, DiagnosticSeverity.Warning, DiagnosticSeverity.Info })
        {
            foreach (var finding in result.Findings.Where(f => f.Severity == severity))
            {
                into.AppendLine($"  {Channel(severity)}: {finding.Render()}"
                              + (finding.Waived ? $"  [waived: {finding.WaiverReason}]" : ""));
                // R-lvs8-2b: the designer's OWN names, un-reduced. A finding that could only say
                // "the merged group at net 14" is one a user cannot act on.
                if (finding.Objects.Count > 0)
                    into.AppendLine($"      {string.Join(", ", finding.Objects)}");
            }
        }

        into.AppendLine();
    }

    /// <summary><see cref="Write(StringBuilder, string, LvsRunResult)"/> as one string.</summary>
    public static string Of(string name, LvsRunResult result)
    {
        var sb = new StringBuilder();
        Write(sb, name, result);
        return sb.ToString();
    }

    /// <summary>One finding as the report lists it — its line, then its objects.</summary>
    public static string Of(LvsFinding finding)
    {
        var sb = new StringBuilder();
        sb.Append($"{Channel(finding.Severity)}: {finding.Id}: {finding.Render()}");
        if (finding.Waived) sb.Append($"  [waived: {finding.WaiverReason}]");
        if (finding.Objects.Count > 0) sb.Append($"\n    {string.Join(", ", finding.Objects)}");
        return sb.ToString();
    }

    public static string Verdict(LvsRunResult result) =>
        result.IsClean
            ? "matches"
            : $"{result.ErrorCount} error(s), {result.WarningCount} warning(s)";

    private static string Channel(DiagnosticSeverity severity) => severity switch
    {
        DiagnosticSeverity.Error   => "error",
        DiagnosticSeverity.Warning => "warning",
        _                          => "note",
    };
}

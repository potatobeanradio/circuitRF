// brief-icp-6 R-icp6-1 — `--component-ports` and `--port-at`, as `em`, `explain`, `check` and a dry run take them. Parsing is here;
// what each entry selects is ICP-1's (EmPartPads.Select), so a spelling means on the command line exactly what the same entry means
// in a .cem.

using CircuitRF.Design.Layout.Em;

namespace CircuitRF.Cli;

internal static class ComponentPortSpellings
{
    /// <summary>The grammar, as a refusal states it.</summary>
    public const string Grammar =
        "--component-ports takes a comma list of \"*\" (every placed two-terminal part and device), a reference designator or " +
        "instance path (Q1, X1/Q3, X1/*, @2) and cell:<name> (every instance of that cell); --port-at pin|cell applies to every entry.";

    /// <summary>
    /// The entries <paramref name="text"/> names, each with <paramref name="portAt"/> when given; null with the reason (naming the
    /// grammar) when a spelling is not one of them.
    /// </summary>
    public static List<EmComponentPort>? Parse(string text, string? portAt, out string? error)
    {
        error = null;
        EmComponentPortAt? at = null;
        if (portAt is not null)
        {
            switch (portAt.Trim().ToLowerInvariant())
            {
                case "pin": at = EmComponentPortAt.Pin; break;
                case "cell": at = EmComponentPortAt.Cell; break;
                default:
                    error = $"--port-at '{portAt}' is not pin or cell. {Grammar}";
                    return null;
            }
        }
        var entries = new List<EmComponentPort>();
        foreach (string token in text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            EmComponentPort entry;
            if (token.StartsWith("cell:", StringComparison.OrdinalIgnoreCase))
            {
                string cell = token[5..].Trim();
                if (cell.Length == 0 || cell.IndexOfAny(['/', '\\', '*', ':']) >= 0)
                {
                    error = $"'{token}' names no cell. {Grammar}";
                    return null;
                }
                entry = new EmComponentPort("") { Cell = cell };
            }
            else if (Path(token) is { } bad)
            {
                error = $"'{token}' {bad}. {Grammar}";
                return null;
            }
            else entry = new EmComponentPort(token);
            entries.Add(at is { } a ? entry with { PortAt = a } : entry);
        }
        if (entries.Count == 0)
        {
            error = $"--component-ports names nothing. {Grammar}";
            return null;
        }
        return entries;
    }

    /// <summary>Why <paramref name="token"/> is not a reference designator, an instance path or <c>"*"</c>; null when it is.</summary>
    private static string? Path(string token)
    {
        if (token == "*") return null;
        string[] segments = token.Split('/');
        for (int i = 0; i < segments.Length; i++)
        {
            string s = segments[i];
            if (s.Length == 0) return "has an empty path segment";
            if (s == "*")
            {
                if (i != segments.Length - 1) return "has \"*\" before its last segment: \"*\" only ends a path (X1/*)";
                continue;
            }
            foreach (char ch in s)
                if (!(char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.' or '@' or '+' or '$' or '#' or '[' or ']'))
                    return $"is not a reference designator or instance path ('{ch}' is not part of one)";
        }
        return null;
    }
}

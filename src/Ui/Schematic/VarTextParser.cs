using System;
using System.Collections.Generic;
using System.Linq;

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// Framework-free static helper that parses and serializes the multi-line
/// "name = expression" text format used by the VAR editor (Mode A).
/// Pulled into a static class so it is directly testable without any VM.
/// </summary>
public static class VarTextParser
{
    /// <summary>
    /// Result of parsing one line of the VAR text editor.
    /// Exactly one of <see cref="IsValid"/>, <see cref="IsBlank"/>,
    /// <see cref="IsComment"/>, or (<see cref="IsValid"/>==false with an error)
    /// describes the line.
    /// </summary>
    public sealed record VarLine(
        string  RawText,
        string? Name,
        string? Expression,
        bool    IsValid,
        bool    IsBlank,
        bool    IsComment,
        string? ErrorMessage = null
    );

    /// <summary>
    /// Parses every line in <paramref name="text"/> and returns a structured result.
    /// Blank lines → <see cref="VarLine.IsBlank"/>.
    /// Lines starting with '#' or '//' → <see cref="VarLine.IsComment"/>.
    /// Lines containing '=' split into name / expression.
    ///   • Empty name after trim → invalid with error.
    /// Lines without '=' → invalid with error.
    /// </summary>
    public static IReadOnlyList<VarLine> ParseLines(string text)
    {
        if (string.IsNullOrEmpty(text))
            return [];

        var result = new List<VarLine>();
        foreach (var raw in text.Split('\n'))
        {
            var trimmed = raw.TrimEnd('\r').Trim();

            if (trimmed.Length == 0)
            {
                result.Add(new VarLine(raw, null, null, false, IsBlank: true, IsComment: false));
                continue;
            }

            if (trimmed.StartsWith('#') || trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                result.Add(new VarLine(raw, null, null, false, IsBlank: false, IsComment: true));
                continue;
            }

            int eqIdx = trimmed.IndexOf('=');
            if (eqIdx < 0)
            {
                result.Add(new VarLine(raw, null, null, false, IsBlank: false, IsComment: false,
                    ErrorMessage: "Expected 'name = expression'"));
                continue;
            }

            var name = trimmed[..eqIdx].Trim();
            var expr = trimmed[(eqIdx + 1)..].Trim();

            if (name.Length == 0)
            {
                result.Add(new VarLine(raw, null, expr, false, IsBlank: false, IsComment: false,
                    ErrorMessage: "Variable name cannot be empty"));
                continue;
            }

            result.Add(new VarLine(raw, name, expr, IsValid: true, IsBlank: false, IsComment: false));
        }
        return result;
    }

    /// <summary>
    /// Finds names that appear more than once among the valid lines in
    /// <paramref name="lines"/>.  Used to surface duplicate-name warnings.
    /// </summary>
    public static IReadOnlyList<string> FindDuplicateNames(IReadOnlyList<VarLine> lines)
    {
        var seen    = new HashSet<string>(StringComparer.Ordinal);
        var dupes   = new HashSet<string>(StringComparer.Ordinal);
        foreach (var l in lines.Where(l => l.IsValid && l.Name is not null))
        {
            if (!seen.Add(l.Name!))
                dupes.Add(l.Name!);
        }
        return [.. dupes.OrderBy(n => n, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Serialises the current <paramref name="parameters"/> back to the
    /// "name = expression" text format (one per line, same order).
    /// Parameters with empty names are skipped.
    /// </summary>
    public static string SerializeLines(IEnumerable<EditableParameter> parameters)
        => string.Join('\n',
            parameters
                .Where(p => !string.IsNullOrWhiteSpace(p.Name))
                .Select(p => $"{p.Name} = {p.Expression}"));
}

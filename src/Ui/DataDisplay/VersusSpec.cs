// ================================================================
//  VersusSpec.cs  —  The "plot versus" separator.
//
//  A trace spec may name its own X data instead of taking X from the
//  cube's swept axis:
//
//      <y-expression>  vs  <x-expression>
//
//  "vs" (or "versus", case-insensitive) is a LOWEST-PRECEDENCE infix
//  separator, split off BEFORE any cube-name scanning, at top level
//  only — never inside [ ], ( ), or a quoted label. At most one per
//  spec. Both sides are ordinary trace specs (single-cube shorthand or
//  a multi-cube expression), so nothing about either grammar changes.
//
//  Degenerate case, documented rather than defended: a cube literally
//  named "vs" cannot be written bare on either side (write it as
//  vs[:] — the bracket keeps it out of the separator scan).
// ================================================================

using System;
using System.Collections.Generic;

namespace CircuitRF.Ui.DataDisplay;

public static class VersusSpec
{
    /// <summary>The canonical separator emitted by the picker.</summary>
    public const string Keyword = "vs";

    /// <summary>Joins a Y and an X spec into the canonical "<c>Y vs X</c>" form.</summary>
    public static string Join(string ySpec, string xSpec) => $"{ySpec} {Keyword} {xSpec}";

    /// <summary>True when <paramref name="text"/> carries a top-level versus separator.</summary>
    public static bool ContainsSeparator(string text) => FindSeparators(text).Count > 0;

    /// <summary>
    /// Splits a spec on its top-level <c>vs</c>/<c>versus</c> separator.
    /// Returns false with an EMPTY <paramref name="error"/> when there is no separator at all
    /// (an ordinary spec — not a failure), and false with a message when the separator is
    /// duplicated or a side is empty.
    /// </summary>
    public static bool TrySplit(string text, out string ySpec, out string xSpec, out string error)
    {
        ySpec = text; xSpec = ""; error = "";
        if (string.IsNullOrWhiteSpace(text)) return false;

        var seps = FindSeparators(text);
        if (seps.Count == 0) return false;
        if (seps.Count > 1)
        {
            error = "Only one 'vs' is allowed — a trace has one X axis.";
            return false;
        }

        var (start, length) = seps[0];
        ySpec = text[..start].Trim();
        xSpec = text[(start + length)..].Trim();
        if (ySpec.Length == 0 || xSpec.Length == 0)
        {
            error = "'vs' needs an expression on each side (e.g. \"Gain vs Pout\").";
            ySpec = text; xSpec = "";
            return false;
        }
        return true;
    }

    /// <summary>Positions of every top-level separator token, as (start, length) pairs.</summary>
    private static List<(int Start, int Length)> FindSeparators(string text)
    {
        var hits = new List<(int, int)>();
        if (string.IsNullOrEmpty(text)) return hits;

        int depth = 0;          // [] and () nesting
        bool inQuote = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '"') { inQuote = !inQuote; continue; }
            if (inQuote) continue;
            if (c is '[' or '(') { depth++; continue; }
            if (c is ']' or ')') { if (depth > 0) depth--; continue; }
            if (depth != 0) continue;
            if (c is not ('v' or 'V')) continue;

            // Must be whitespace-delimited on both sides: " vs " / " versus ".
            if (i == 0 || !char.IsWhiteSpace(text[i - 1])) continue;
            foreach (var kw in Keywords)
            {
                int end = i + kw.Length;
                if (end > text.Length) continue;
                if (!text.AsSpan(i, kw.Length).Equals(kw.AsSpan(), StringComparison.OrdinalIgnoreCase)) continue;
                if (end < text.Length && !char.IsWhiteSpace(text[end])) continue;
                hits.Add((i, kw.Length));
                i = end - 1;
                break;
            }
        }
        return hits;
    }

    // Longest first, so "versus" is not read as "vs".
    private static readonly string[] Keywords = { "versus", "vs" };
}

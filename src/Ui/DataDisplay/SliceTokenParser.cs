// ================================================================
//  SliceTokenParser.cs  —  Shared bracket-token classifier for cube
//  slice specs. Used by CubeTraceSpecParser (picker text) and
//  TraceExpression (multi-cube expressions) so the accepted grammar
//  never drifts between them.
//
//  Grammar (per axis slot):
//    ":"  |  ".."  |  "All"  → KeepWhole (the whole axis, ≡ Range.All)
//    "a..b"  |  "..b"  |  "a.."  → KeepRange  (end-exclusive sub-range)
//    "label"  →  PinLabel resolved to an integer index
//    integer  →  PinIndex (pins/removes the axis)
// ================================================================

using System;
using RfCore.Data;

namespace CircuitRF.Ui.DataDisplay;

/// <summary>Classifies one bracket token of a cube slice spec. Shared by CubeTraceSpecParser
/// (single-slice picker text) and TraceExpression (multi-cube expressions) so the accepted
/// grammar can never drift between them. Conforms to src/Core/Data/CLAUDE.md slice semantics:
/// every slot is an axis INDEX; <c>int</c> pins/removes an axis; <c>:</c>/<c>All</c>/<c>a..b</c>
/// keep it; ranges are END-EXCLUSIVE (NumPy/C#, not MATLAB).</summary>
public static class SliceTokenParser
{
    public enum Kind { KeepWhole, KeepRange, PinIndex, PinLabel, Invalid, Family }

    public readonly record struct Token(
        Kind Kind, int Index = 0, int RangeStart = 0, int RangeEndExclusive = 0, string Label = "");

    /// <summary>Parses one trimmed token against an axis of the given length.
    /// Resolves quoted labels against <paramref name="axisLabels"/> (may be null).
    /// On failure returns <c>Invalid</c> and sets <paramref name="error"/>.</summary>
    public static Token Parse(string tk, int axisLength, string[]? axisLabels, string axisName, out string error)
    {
        error = "";
        tk = tk.Trim();

        // Whole-axis: ":", "All" (case-insensitive), or ".."
        if (tk == ":" || tk == ".." || string.Equals(tk, "All", StringComparison.OrdinalIgnoreCase))
            return new Token(Kind.KeepWhole);

        // Family-iterate marker: "~" (also "fam"/"family"). Keeps the whole axis but renders it as a
        // curve family rather than the X axis. Lets the picker encode an explicit X/Family split that
        // the positional default (last-kept = X) cannot express.
        if (tk == "~" || string.Equals(tk, "fam", StringComparison.OrdinalIgnoreCase)
                      || string.Equals(tk, "family", StringComparison.OrdinalIgnoreCase))
            return new Token(Kind.Family);

        // Range "a..b" (end-exclusive), with open ends "..b", "a..", "..".
        int dots = tk.IndexOf("..", StringComparison.Ordinal);
        if (dots >= 0)
        {
            string loStr = tk[..dots].Trim();
            string hiStr = tk[(dots + 2)..].Trim();
            int lo = 0, hiEx = axisLength;
            if (loStr.Length > 0 && !int.TryParse(loStr, out lo))
            { error = $"Bad range start '{loStr}' for axis '{axisName}'."; return new Token(Kind.Invalid); }
            if (hiStr.Length > 0 && !int.TryParse(hiStr, out hiEx))
            { error = $"Bad range end '{hiStr}' for axis '{axisName}'."; return new Token(Kind.Invalid); }
            lo   = Math.Clamp(lo,   0, axisLength);
            hiEx = Math.Clamp(hiEx, 0, axisLength);
            if (hiEx <= lo)
            { error = $"Empty range '{tk}' for axis '{axisName}' (end-exclusive)."; return new Token(Kind.Invalid); }
            return new Token(Kind.KeepRange, RangeStart: lo, RangeEndExclusive: hiEx);
        }

        // Quoted label "Vout".
        if (tk.Length >= 2 && tk[0] == '"' && tk[^1] == '"')
        {
            string label = tk[1..^1];
            if (axisLabels is null)
            { error = $"Axis '{axisName}' has no labels; use a numeric index."; return new Token(Kind.Invalid); }
            int idx = Array.IndexOf(axisLabels, label);
            if (idx < 0)
            { error = $"No label '{label}' in axis '{axisName}'."; return new Token(Kind.Invalid); }
            return new Token(Kind.PinIndex, Index: idx, Label: label);
        }

        // Integer index (pins/removes the axis).
        if (int.TryParse(tk, out int index))
        {
            // S/Y/Z port axes (i, j) use 1-based PORT NUMBERS, not 0-based indices: S[:, 2, 1] = S21.
            if (axisName is "i" or "j")
            {
                if (index < 1 || index > axisLength)
                { error = $"Port {index} out of range for axis '{axisName}' (1..{axisLength})."; return new Token(Kind.Invalid); }
                return new Token(Kind.PinIndex, Index: index - 1);
            }
            if (index < 0 || index >= axisLength)
            { error = $"Index {index} out of range for axis '{axisName}' (0..{axisLength - 1})."; return new Token(Kind.Invalid); }
            return new Token(Kind.PinIndex, Index: index);
        }

        error = $"Cannot parse token '{tk}' for axis '{axisName}'.";
        return new Token(Kind.Invalid);
    }
}

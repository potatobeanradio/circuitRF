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
//    "@name"  →  PinLink: pinned at whatever index the link source
//                "name" (a marker) chooses (brief-ddl-1 R-ddl1-5)
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using RfCore.Data;

namespace CircuitRF.Render.DataDisplay;

/// <summary>Classifies one bracket token of a cube slice spec. Shared by CubeTraceSpecParser
/// (single-slice picker text) and TraceExpression (multi-cube expressions) so the accepted
/// grammar can never drift between them. Conforms to src/Core/Data/CLAUDE.md slice semantics:
/// every slot is an axis INDEX; <c>int</c> pins/removes an axis; <c>:</c>/<c>All</c>/<c>a..b</c>
/// keep it; ranges are END-EXCLUSIVE (NumPy/C#, not MATLAB).</summary>
public static class SliceTokenParser
{
    public enum Kind { KeepWhole, KeepRange, PinIndex, PinLabel, Invalid, Family, PinLink }

    /// <summary>Splits a bracket slice body into per-axis tokens on <b>top-level</b> commas only — a comma
    /// inside a double-quoted label (e.g. the two-tone mixIndex tag <c>"(1,-1)"</c>) is NOT a separator.
    /// A blank body yields an empty array (a bare / rank-0 reference). This is the single comma-splitter
    /// for cube slice bodies; both CubeTraceSpecParser and TraceExpression call it so a naive
    /// <c>Split(',')</c> can never break a quoted label again.</summary>
    public static string[] SplitTokens(string sliceBody)
    {
        if (string.IsNullOrWhiteSpace(sliceBody)) return Array.Empty<string>();

        var tokens  = new List<string>();
        int start   = 0;
        bool inQuote = false;
        for (int i = 0; i < sliceBody.Length; i++)
        {
            char c = sliceBody[i];
            if (c == '"') inQuote = !inQuote;
            else if (c == ',' && !inQuote)
            {
                tokens.Add(sliceBody[start..i].Trim());
                start = i + 1;
            }
        }
        tokens.Add(sliceBody[start..].Trim());
        return tokens.ToArray();
    }

    public readonly record struct Token(
        Kind Kind, int Index = 0, int RangeStart = 0, int RangeEndExclusive = 0, string Label = "");

    /// <summary>Parses one trimmed token against an axis of the given length.
    /// Resolves quoted labels against <paramref name="axisLabels"/> (may be null).
    /// On failure returns <c>Invalid</c> and sets <paramref name="error"/>.</summary>
    public static Token Parse(string tk, int axisLength, string[]? axisLabels, string axisName, out string error)
        => Parse(tk, axisLength, axisLabels, axisName, null, out error);

    /// <summary>
    /// The same, with the axis's own VALUES — which is what a <c>port</c> axis needs.
    ///
    /// <para><b>An integer on a port axis is a 1-based PORT NUMBER, not an index</b> (ANT-7 §3, and
    /// the same trap the <c>i</c>/<c>j</c> axes already record). Those two resolve it by arithmetic
    /// (<c>index = n − 1</c>) because an S matrix's ports are 1…N by construction; a <c>port</c> axis
    /// is not — it carries the numbers of the ports that were actually DRIVEN
    /// (<c>PlanarFarFieldSet.PortNumbers</c>), which need not start at 1 and need not be contiguous.
    /// So the number is looked up in the values rather than shifted, and a port the run does not
    /// have is refused by NAME instead of silently becoming a neighbour.</para>
    ///
    /// <para>harmonicaRF's own <c>port</c> axis holds 0, 1, 2 … and is therefore unaffected: the
    /// lookup of the value <c>k</c> in <c>[0, 1, 2 …]</c> is the index <c>k</c>, which is exactly
    /// what it meant before.</para>
    /// </summary>
    public static Token Parse(string tk, int axisLength, string[]? axisLabels, string axisName,
                              double[]? axisValues, out string error)
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

        // A linked pin "@m1" (brief-ddl-1 R-ddl1-5): the axis is pinned, at the index the named link source
        // resolves to. The NAME is all a token can carry — binding it to a source id, and choosing the index, need
        // the display, which is the caller's (LinkCommit at commit, LinkResolve at draw).
        if (tk.Length > 0 && tk[0] == '@')
        {
            string name = tk[1..].Trim();
            if (name.Length == 0 || !name.All(c => char.IsLetterOrDigit(c) || c is '_' or '.' or '-'))
            { error = $"'{tk}' is not a link — write '@' and a marker's name, as in '@m1'."; return new Token(Kind.Invalid); }
            if (!IsLinkableAxis(axisName, axisLabels, axisLength))
            { error = $"Axis '{axisName}' names its entries, so it cannot follow a marker; pin it by name or index."; return new Token(Kind.Invalid); }
            return new Token(Kind.PinLink, Label: name);
        }

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
            if (axisName == "port" && axisValues is { Length: > 0 })
            {
                for (int k = 0; k < axisValues.Length && k < axisLength; k++)
                    if (Math.Abs(axisValues[k] - index) < 1e-9)
                        return new Token(Kind.PinIndex, Index: k);

                error = $"Port {index} is not a port of axis '{axisName}' "
                      + $"(it has {string.Join(", ", axisValues.Take(axisLength)
                                                               .Select(v => v.ToString("0.###",
                                                                   System.Globalization.CultureInfo.InvariantCulture)))}).";
                return new Token(Kind.Invalid);
            }

            // S/Y/Z port axes (i, j) use 1-based PORT NUMBERS, not 0-based indices: S[:, 2, 1] = S21.
            // The WSProbe matrix's row/col are the same case and must read the same way: the cube's
            // own axis VALUES are 1-based (WspCubePacker writes k+1), so the document's wsp(1,1) is
            // wsp[:, 1, 1] with no index arithmetic between the paper and the card.
            //
            // DFR27-2: an S matrix's ports are NOT always 1…N — they are the Terms' own numbers, so a
            // bench with Terms 1, 2, 9, 10 has i = [1, 2, 9, 10]. The number is checked against the
            // axis VALUES and the token still carries number − 1: that is the slice's identity for a
            // port, which PortSlices.ToPositions turns into a position against whichever cube the
            // trace is resolved on — so a re-run that drops Term 9 makes S[:, 10, 9] invalid instead
            // of quietly reading whatever now sits at that position.
            if (axisValues is not null && PortSlices.IsPortNumberAxis(axisName, axisValues) && !PortSlices.IsOneToN(axisValues))
            {
                if (PortSlices.PositionOf(axisValues, index) < 0)
                { error = PortSlices.NotAPort(index, axisName, axisValues); return new Token(Kind.Invalid); }
                return new Token(Kind.PinIndex, Index: index - 1);
            }
            if (axisName is "i" or "j" or "row" or "col")
            {
                if (index < 1 || index > axisLength)
                { error = $"{(axisName is "row" or "col" ? "Index" : "Port")} {index} out of range for axis '{axisName}' (1..{axisLength})."; return new Token(Kind.Invalid); }
                return new Token(Kind.PinIndex, Index: index - 1);
            }
            if (index < 0 || index >= axisLength)
            { error = $"Index {index} out of range for axis '{axisName}' (0..{axisLength - 1})."; return new Token(Kind.Invalid); }
            return new Token(Kind.PinIndex, Index: index);
        }

        error = $"Cannot parse token '{tk}' for axis '{axisName}'.";
        return new Token(Kind.Invalid);
    }

    /// <summary>
    /// Whether a pinned axis may follow a link source. A LABEL axis — a node, a branch, a port pair, a WSProbe row or
    /// column, a category axis that names every entry — cannot in this series (R-ddl1-6): what a marker sits on is a
    /// point along a quantity, and "the nearest node" is not a question with an answer. Spectral axes (frequency,
    /// harmonic order, a two-tone product) carry labels too and stay linkable.
    /// </summary>
    public static bool IsLinkableAxis(string axisName, string[]? axisLabels, int axisLength)
    {
        if (axisName is "node" or "branch" or "i" or "j" or "row" or "col" or "port") return false;
        if (axisLabels is { Length: > 0 } l && l.Length == axisLength
            && axisName is not ("freq" or "harmonic" or "mixIndex")) return false;
        return true;
    }
}

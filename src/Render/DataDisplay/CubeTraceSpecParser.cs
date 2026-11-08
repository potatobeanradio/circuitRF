// ================================================================
//  CubeTraceSpecParser.cs  —  Parses a DataCube shorthand string
//  into (CubeName, AxisSlice[], CubeTransform).
//
//  Inverse of Trace.CubeShorthand.  Syntax:
//    [transform] CubeName[token, token, :]
//  where each token is ":", a quoted label "Vout", or an integer index.
// ================================================================

using System;
using System.Linq;
using RfCore;
using RfCore.Data;

namespace CircuitRF.Render.DataDisplay
{
    public static class CubeTraceSpecParser
    {
        public static bool TryParse(
            string text,
            DataSet ds,
            out string cubeName,
            out AxisSlice[]? slice,
            out CubeTransform transform,
            out string error)
        {
            cubeName  = "";
            slice     = null;
            transform = CubeTransform.None;
            error     = "";

            text = PortPairSpelling.Rewrite(text.Trim(), ds);   // DUT_1(2,1) → DUT_1[:, 2, 1]
            if (string.IsNullOrEmpty(text))
            {
                error = "Empty specification.";
                return false;
            }

            // Split off everything before '['.
            int bracketPos = text.IndexOf('[');

            // Bare cube name (no '[') — the whole cube.
            if (bracketPos < 0)
            {
                if (!TryParseBareName(text, ds, out var bareName, out var bareTransform))
                {
                    error = "Missing '['.";
                    return false;
                }
                cubeName  = bareName;
                transform = bareTransform;
                var bareCube = ds[cubeName];
                if (bareCube.Rank == 0)
                {
                    slice = Array.Empty<AxisSlice>();
                    return true;
                }
                // Three or more axes cannot all be X, and "Gain" on a [VDD, RFfreq, Pin] cube still
                // means something: the roles the trace card seeds (innermost sweep X, the next one out
                // the family, the rest pinned). Up to two axes keep the positional reading below.
                if (bareCube.Rank > 2)
                {
                    slice = SeedSlice.Build(bareCube);
                    return true;
                }
                var bareTokens = Enumerable.Repeat(":", bareCube.Rank).ToArray();
                // Build the slice by recursively parsing as if the user typed "[transform ]Name[:, :, ...]"
                string transformStr = bareTransform != CubeTransform.None
                    ? Trace.TransformFunctionName(bareTransform) + " "
                    : "";
                string synth = transformStr + cubeName + "[" + string.Join(", ", bareTokens) + "]";
                return TryParse(synth, ds, out cubeName, out slice, out transform, out error);
            }

            // Parse optional transform + cube name from the prefix.
            // Accepts two forms:
            //   CubeName[...]            — no transform
            //   transform CubeName[...]  — space-separated (legacy spec-box entry)
            //   transform(CubeName[...]) — function-call form emitted by BuildPickerExpression
            string prefix = text[..bracketPos].Trim();

            int parenIdx = prefix.IndexOf('(');
            if (parenIdx >= 0)
            {
                // Function-call form: "mag(V" → transform="mag", cubeName="V"
                string rawTransform = prefix[..parenIdx].Trim();
                cubeName = prefix[(parenIdx + 1)..].Trim();
                if (!TryParseTransform(rawTransform, out transform))
                {
                    error = $"Unknown transform '{rawTransform}'.";
                    return false;
                }
            }
            else
            {
                var prefixParts = prefix.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                if (prefixParts.Length == 0)
                {
                    error = "Missing cube name.";
                    return false;
                }

                if (prefixParts.Length == 1)
                {
                    cubeName  = prefixParts[0];
                    transform = CubeTransform.None;
                }
                else if (prefixParts.Length == 2)
                {
                    if (!TryParseTransform(prefixParts[0], out transform))
                    {
                        error = $"Unknown transform '{prefixParts[0]}'.";
                        return false;
                    }
                    cubeName = prefixParts[1];
                }
                else
                {
                    error = $"Unexpected tokens before '[': '{prefix}'.";
                    return false;
                }
            }

            // Validate cube name.
            if (!ds.Contains(cubeName))
            {
                var names = string.Join(", ", ds.Groups
                    .SelectMany(g => ds.CubesIn(g).Keys.Select(c => g == DataSet.DefaultGroup ? c : $"{g}.{c}")));
                error = $"No cube '{cubeName}' in dataset. Available: {names}.";
                return false;
            }
            var cube = ds[cubeName];

            // Extract slice from [...].
            int closeBracket = text.LastIndexOf(']');
            if (closeBracket <= bracketPos)
            {
                error = "Missing ']'.";
                return false;
            }

            string sliceStr = text[(bracketPos + 1)..closeBracket];
            var tokens = SliceTokenParser.SplitTokens(sliceStr);

            if (tokens.Length != cube.Rank)
            {
                var axisNames = string.Join(", ", cube.Axes.Select(a => a.Name));
                error = $"Expected {cube.Rank} axis token(s), got {tokens.Length}. Axes: {axisNames}.";
                return false;
            }

            slice = new AxisSlice[tokens.Length];
            if (slice.Length == 0)
                return true;    // rank-0 cube: no axes, empty slice is valid
            int xCount = 0;
            int famCount = 0;
            for (int i = 0; i < tokens.Length; i++)
            {
                var axis = cube.Axes[i];
                var t = SliceTokenParser.Parse(tokens[i], axis.Length, axis.Labels, axis.Name, axis.Values, out error);
                switch (t.Kind)
                {
                    case SliceTokenParser.Kind.KeepWhole:
                        slice[i] = new AxisSlice(axis.Name, AxisRole.KeepAsX, 0); xCount++; break;
                    case SliceTokenParser.Kind.KeepRange:
                        slice[i] = new AxisSlice(axis.Name, AxisRole.KeepAsX, t.RangeStart,
                                                 t.RangeStart, t.RangeEndExclusive); xCount++; break;
                    case SliceTokenParser.Kind.Family:
                        slice[i] = new AxisSlice(axis.Name, AxisRole.FamilyIterate, 0); famCount++; break;
                    case SliceTokenParser.Kind.PinIndex:
                        slice[i] = new AxisSlice(axis.Name, AxisRole.PinToIndex, t.Index, Label: t.Label); break;
                    default:
                        return false;   // error already set by SliceTokenParser
                }
            }

            var localSlice = slice;  // avoid capturing out parameter in lambda
            var keptDims = Enumerable.Range(0, localSlice.Length)
                .Where(i => localSlice[i].Role == AxisRole.KeepAsX).ToList();

            if (famCount > 0)
            {
                // Explicit family marker present — honor it; skip the positional convention.
                if (famCount > 1)
                { error = "Only one axis may be the curve family ('~')."; return false; }
                if (keptDims.Count == 0)
                { error = "A family needs one swept X axis (':') alongside the family ('~')."; return false; }
                if (keptDims.Count > 1)
                { error = "A family supports one X axis (':') + one family ('~'); pin the extra axes."; return false; }
                // exactly one family + one X — roles already set per token.
            }
            else if (keptDims.Count == 0)
            {
                // Fully pinned → scalar (operating-point value). Valid: renders on a Table.
            }
            else if (keptDims.Count == 1)
            {
                // single curve — leave as-is
            }
            else if (keptDims.Count == 2)
            {
                // No explicit family → positional convention: last kept axis = X; earlier = Family —
                // UNLESS the last is a category axis (a wBond's `wire array`, a DC `node`) and the earlier
                // is not. A sweep stacks its axis IN FRONT, so a swept `WireTemp[IDC, wire array]` read
                // positionally plots along the array names with one curve per sweep point: 101 table
                // columns of a single number each. The categories are the curves; the sweep is the X.
                int fDim = keptDims[0], xDim = keptDims[1];
                if (IsCategoryAxis(cube.Axes[xDim]) && !IsCategoryAxis(cube.Axes[fDim]))
                    (fDim, xDim) = (xDim, fDim);
                slice[fDim] = slice[fDim] with { Role = AxisRole.FamilyIterate };
                slice[xDim] = slice[xDim] with { Role = AxisRole.KeepAsX };
            }
            else
            {
                error = "A family supports one swept (Family) axis + one X axis. Pin the extra axes.";
                return false;
            }

            return true;
        }

        /// <summary>
        /// The cube and transform of a spec with no brackets — <c>Gain</c>, <c>mag(IMD2)</c> or the
        /// legacy <c>mag IMD2</c>. A transform can be attached two ways with NO brackets: the
        /// function-call form (what BuildPickerExpression emits for a transformed bare measurement;
        /// it MUST round-trip) and the space-separated spec-box entry. False when the text names no
        /// cube in <paramref name="ds"/>.
        /// </summary>
        public static bool TryParseBareName(string text, DataSet ds, out string cubeName, out CubeTransform transform)
        {
            text      = text.Trim();
            cubeName  = text;
            transform = CubeTransform.None;
            int fnParen = text.IndexOf('(');
            if (fnParen > 0 && text.EndsWith(")"))
            {
                string potentialTransform = text[..fnParen].Trim();
                string potentialCube      = text[(fnParen + 1)..^1].Trim();
                if (TryParseTransform(potentialTransform, out var ft) && ds.Contains(potentialCube))
                {
                    transform = ft;
                    cubeName  = potentialCube;
                }
            }
            else
            {
                int spaceIdx = text.IndexOf(' ');
                if (spaceIdx >= 0)
                {
                    string potentialTransform = text[..spaceIdx].Trim();
                    string potentialCube      = text[(spaceIdx + 1)..].Trim();
                    if (TryParseTransform(potentialTransform, out var bt) && ds.Contains(potentialCube))
                    {
                        transform = bt;
                        cubeName  = potentialCube;
                    }
                }
            }
            return ds.Contains(cubeName);
        }

        /// <summary>
        /// An axis whose entries are NAMED things rather than points along a quantity: it states a label for
        /// every entry and is not spectral (frequency, harmonic order and a two-tone product carry labels too,
        /// and are exactly what a curve runs along).
        /// </summary>
        private static bool IsCategoryAxis(Axis axis)
            => axis.Labels is { Length: > 0 } l && l.Length == axis.Length
               && axis.Name is not ("freq" or "harmonic" or "mixIndex")
               && !axis.Unit.EndsWith("Hz", StringComparison.Ordinal);

        /// <summary>
        /// The transform-name table, exposed because the <c>plot</c> verb's <c>y=</c> has to reach
        /// it on a trace whose CUBE is not what is being transformed. A WSProbe trace's cube is the
        /// run's 2N x 2N matrix and its VALUES are a metric of it, so <c>db(wsp)</c> is not a spec
        /// the parser can be asked to read — but the name <c>db</c> must still mean here exactly
        /// what it means everywhere else, which is what this being one table is for.
        /// </summary>
        public static bool TryParseTransformName(string s, out CubeTransform transform)
            => TryParseTransform(s, out transform);

        private static bool TryParseTransform(string s, out CubeTransform transform)
        {
            switch (s.ToLowerInvariant())
            {
                case "db20":  transform = CubeTransform.dB20;  return true;
                case "db10":  transform = CubeTransform.dB10;  return true;
                case "db":    transform = CubeTransform.dB;    return true;
                case "mag":   transform = CubeTransform.Mag;   return true;
                case "phase": transform = CubeTransform.Phase; return true;
                case "real":  transform = CubeTransform.Real;  return true;
                case "imag":  transform = CubeTransform.Imag;  return true;
                case "conj":  transform = CubeTransform.Conj;  return true;
                case "unwrap": transform = CubeTransform.PhaseUnwrapped; return true;
                case "none":  transform = CubeTransform.None;  return true;
                default:      transform = CubeTransform.None;  return false;
            }
        }
    }
}

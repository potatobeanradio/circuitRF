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

namespace CircuitRF.Ui.DataDisplay
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

            text = text.Trim();
            if (string.IsNullOrEmpty(text))
            {
                error = "Empty specification.";
                return false;
            }

            // Split off everything before '['.
            int bracketPos = text.IndexOf('[');

            // Bare cube name (no '[') — synthesize all-':' tokens for the whole cube.
            if (bracketPos < 0)
            {
                // The whole text (after any transform prefix) must be a cube name.
                // Determine if there's a transform prefix by checking for a space.
                string barePrefix = text;
                CubeTransform bareTransform = CubeTransform.None;
                int spaceIdx = text.IndexOf(' ');
                if (spaceIdx >= 0)
                {
                    string potentialTransform = text[..spaceIdx].Trim();
                    string potentialCube      = text[(spaceIdx + 1)..].Trim();
                    if (TryParseTransform(potentialTransform, out var bt) && ds.Contains(potentialCube))
                    {
                        bareTransform = bt;
                        barePrefix    = potentialCube;
                    }
                }
                if (!ds.Contains(barePrefix))
                {
                    error = "Missing '['.";
                    return false;
                }
                cubeName  = barePrefix;
                transform = bareTransform;
                var bareCube   = ds[cubeName];
                var bareTokens = Enumerable.Repeat(":", bareCube.Rank).ToArray();
                // Build the slice by recursively parsing as if the user typed "[transform ]Name[:, :, ...]"
                string transformStr = bareTransform != CubeTransform.None
                    ? bareTransform.ToString().ToLowerInvariant() + " "
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
                var names = string.Join(", ", ds.Cubes.Keys);
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
            var tokens = sliceStr.Split(',').Select(t => t.Trim()).ToArray();

            if (tokens.Length != cube.Rank)
            {
                var axisNames = string.Join(", ", cube.Axes.Select(a => a.Name));
                error = $"Expected {cube.Rank} axis token(s), got {tokens.Length}. Axes: {axisNames}.";
                return false;
            }

            slice = new AxisSlice[tokens.Length];
            int xCount = 0;
            int famCount = 0;
            for (int i = 0; i < tokens.Length; i++)
            {
                var axis = cube.Axes[i];
                var t = SliceTokenParser.Parse(tokens[i], axis.Length, axis.Labels, axis.Name, out error);
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
                error = "Need at least one swept axis (':', 'All', or a range).";
                return false;
            }
            else if (keptDims.Count == 1)
            {
                // single curve — leave as-is
            }
            else if (keptDims.Count == 2)
            {
                // No explicit family → positional convention: last kept axis = X; earlier = Family.
                int fDim = keptDims[0];
                slice[fDim] = slice[fDim] with { Role = AxisRole.FamilyIterate };
                // slice[keptDims[^1]] stays KeepAsX
            }
            else
            {
                error = "A family supports one swept (Family) axis + one X axis. Pin the extra axes.";
                return false;
            }

            return true;
        }

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
                case "none":  transform = CubeTransform.None;  return true;
                default:      transform = CubeTransform.None;  return false;
            }
        }
    }
}

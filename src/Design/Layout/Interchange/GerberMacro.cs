// Aperture macros (docs/sonnet-briefs/brief-L4e-gerber-import-reader.md R-L4e-8). A macro is a named
// template of primitives, instantiated by %ADD<code><name>,<arg>X<arg>…*%. Two properties of the
// language drive the whole design of this file:
//
//   * THE EXPOSURE MODIFIER CAN BE 0, meaning the primitive ERASES within the aperture. That is how
//     thermal reliefs and annular rings are drawn, so a macro flash is a shape that may have holes and
//     the compositing happens INSIDE the aperture, before the flash is ever placed. Hence Paths64 and
//     a running boolean rather than a list of outlines.
//   * MODIFIERS ARE ARITHMETIC EXPRESSIONS over the macro's own $1, $2, … arguments — see
//     GerberMacroExpression for why that grammar gets its own evaluator.
//
// Primitive 6 (moire) is a fiducial/annotation construct with no copper meaning; it is skipped and
// counted by name (R-L4e-8), never silently dropped.

using Clipper2Lib;

namespace CircuitRF.Design.Layout.Interchange;

/// <summary>One <c>%AM</c> definition — its name and its raw blocks, kept unevaluated because the
/// same macro is instantiated with different arguments by different <c>%ADD</c>s.</summary>
internal sealed class GerberMacroDefinition(string name, IReadOnlyList<string> blocks)
{
    internal string Name { get; } = name;
    internal IReadOnlyList<string> Blocks { get; } = blocks;

    /// <summary>Builds the aperture's geometry, in DBU relative to the aperture origin, for one set of
    /// instantiation arguments. <paramref name="skipped"/> receives the name of anything deliberately
    /// not drawn (an unimplemented primitive, a modifier that would not evaluate) so the reader can
    /// report it once with a count instead of dropping it silently.</summary>
    internal Paths64 Instantiate(IReadOnlyList<double> args, GerberCoordinateFormat format, Action<string> skipped)
    {
        var vars = new Dictionary<int, double>();
        for (int i = 0; i < args.Count; i++) vars[i + 1] = args[i];

        var accumulated = new Paths64();
        foreach (string raw in Blocks)
        {
            // THE COMMENT PRIMITIVE IS RECOGNIZED BEFORE ANYTHING ELSE TOUCHES THE BLOCK, because its
            // text is free-form: a real macro comment reads `0 Free polygon` or
            // `0 $1 to $14 corner X`, and stripping the whitespace out of it first (which every other
            // block genuinely needs) turns it into `0Freepolygon`, which parses as no integer at all
            // and is then reported as an unrecognized primitive. Measured on real artwork: one
            // four-layer board produced 27 distinct "unknown primitive" names and 150 counted
            // occurrences, every one of them a comment — R-L4e-6's report of what was skipped is only
            // worth reading if the things in it are actually things we skipped.
            if (IsCommentBlock(raw)) continue;

            string block = Strip(raw);
            if (block.Length == 0) continue;

            if (block[0] == '$')
            {
                ApplyAssignment(block, vars, skipped);
                continue;
            }

            var fields = block.Split(',');
            if (!int.TryParse(fields[0].Trim(), out int code))
            {
                skipped($"aperture macro primitive \"{fields[0].Trim()}\"");
                continue;
            }

            double[] mods;
            try
            {
                mods = new double[fields.Length - 1];
                for (int i = 1; i < fields.Length; i++) mods[i - 1] = GerberMacroExpression.Evaluate(fields[i], vars);
            }
            catch (GerberMacroExpressionException)
            {
                skipped($"aperture macro primitive {code} (modifier would not evaluate)");
                continue;
            }

            bool exposeOn = true;
            Paths64? geometry = BuildPrimitive(code, mods, format, ref exposeOn, skipped);
            if (geometry is null || geometry.Count == 0) continue;

            accumulated = exposeOn
                ? Clipper.Union(accumulated, geometry, LayoutClipper.Rule)
                : Clipper.Difference(accumulated, geometry, LayoutClipper.Rule);
        }
        return accumulated;
    }

    private void ApplyAssignment(string block, Dictionary<int, double> vars, Action<string> skipped)
    {
        int eq = block.IndexOf('=');
        if (eq <= 1) { skipped("aperture macro variable assignment"); return; }
        if (!int.TryParse(block[1..eq], out int index)) { skipped("aperture macro variable assignment"); return; }
        try { vars[index] = GerberMacroExpression.Evaluate(block[(eq + 1)..], vars); }
        catch (GerberMacroExpressionException) { skipped("aperture macro variable assignment"); }
    }

    private static Paths64? BuildPrimitive(
        int code, double[] m, GerberCoordinateFormat f, ref bool exposeOn, Action<string> skipped)
    {
        double D(int i) => i < m.Length ? m[i] * f.DbuPerFileUnit : 0.0;   // a length modifier, in DBU
        double V(int i) => i < m.Length ? m[i] : 0.0;                       // a bare number (count, angle, exposure)

        switch (code)
        {
            case 1:   // circle: exposure, diameter, centreX, centreY [, rotation]
            {
                exposeOn = V(0) != 0;
                var paths = new Paths64 { GerberPrimitives.Circle(D(2), D(3), D(1) / 2.0) };
                return GerberPrimitives.Rotate(paths, V(4));
            }

            case 2:
            case 20:  // vector line: exposure, width, startX, startY, endX, endY, rotation
            {
                exposeOn = V(0) != 0;
                var paths = new Paths64 { GerberPrimitives.VectorLine(D(2), D(3), D(4), D(5), D(1)) };
                return GerberPrimitives.Rotate(paths, V(6));
            }

            case 21:  // centre line: exposure, width, height, centreX, centreY, rotation
            {
                exposeOn = V(0) != 0;
                var paths = new Paths64 { GerberPrimitives.Rect(D(3), D(4), D(1), D(2)) };
                return GerberPrimitives.Rotate(paths, V(5));
            }

            case 22:  // lower-left line: exposure, width, height, lowerLeftX, lowerLeftY, rotation
            {
                exposeOn = V(0) != 0;
                double w = D(1), h = D(2);
                var paths = new Paths64 { GerberPrimitives.Rect(D(3) + w / 2.0, D(4) + h / 2.0, w, h) };
                return GerberPrimitives.Rotate(paths, V(5));
            }

            case 4:   // outline: exposure, vertexCount n, startX, startY, (x,y) x n, rotation
            {
                exposeOn = V(0) != 0;
                int n = (int)Math.Round(V(1));
                if (n < 1) return null;
                var path = new Path64(n + 1);
                for (int i = 0; i <= n; i++)
                {
                    int at = 2 + 2 * i;
                    if (at + 1 >= m.Length) break;
                    path.Add(GerberPrimitives.Point(D(at), D(at + 1)));
                }
                // The format repeats the first point as the last; LayoutShape polygons are implicitly
                // closed, so drop it here rather than let a zero-length edge into the model.
                if (path.Count > 1 && path[0] == path[^1]) path.RemoveAt(path.Count - 1);
                if (path.Count < 3) return null;
                return GerberPrimitives.Rotate(new Paths64 { path }, V(2 + 2 * (n + 1)));
            }

            case 5:   // regular polygon: exposure, vertexCount, centreX, centreY, diameter, rotation
            {
                exposeOn = V(0) != 0;
                var paths = new Paths64
                {
                    GerberPrimitives.RegularPolygon(D(2), D(3), D(4), (int)Math.Round(V(1)), V(5)),
                };
                return paths;
            }

            case 6:   // moire — a fiducial/annotation construct, not copper. R-L4e-8: skip and count.
                skipped("aperture macro primitive 6 (moire)");
                return null;

            case 7:   // thermal: centreX, centreY, outerDia, innerDia, gap, rotation — no exposure field
            {
                exposeOn = true;
                var paths = GerberPrimitives.Thermal(D(0), D(1), D(2), D(3), D(4));
                return GerberPrimitives.Rotate(paths, V(5));
            }

            default:
                skipped($"aperture macro primitive {code}");
                return null;
        }
    }

    /// <summary>A macro comment: primitive code <c>0</c>, then free text to the end of the block. The
    /// test is on the RAW block because the text is not tokenizable — the leading digit run must be
    /// exactly "0" (so a zero-padded <c>01</c> is still primitive 1) and whatever follows it must not
    /// be another digit.</summary>
    private static bool IsCommentBlock(string raw)
    {
        string trimmed = raw.TrimStart();
        int i = 0;
        while (i < trimmed.Length && trimmed[i] == '0') i++;
        if (i == 0) return false;
        return i == trimmed.Length || !char.IsAsciiDigit(trimmed[i]);
    }

    private static string Strip(string s)
    {
        Span<char> buffer = s.Length <= 256 ? stackalloc char[s.Length] : new char[s.Length];
        int n = 0;
        foreach (char c in s) if (!char.IsWhiteSpace(c)) buffer[n++] = c;
        return new string(buffer[..n]);
    }
}

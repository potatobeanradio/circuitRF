// The older one-record-per-line symbol library — `.lib` (docs/sonnet-briefs/
// brief-PL1-component-library-import.md §6).
//
// Agrees with the S-expression symbol format on everything this reader depends on: the pin's stated
// point is its FREE END, the pad identifier is the field after the pin name, and the drawing is +y UP.
// It differs in surface only — one record per line instead of nested lists, and lengths in MILS rather
// than millimetres, so this reader's scale is 1 where the newer one divides by 0.0254 (R-PL1-17).
//
// Fields are separated by runs of whitespace, with quoted strings in the slots that carry text.

using System.Globalization;
using CircuitRF.Core.Pdk;

namespace CircuitRF.Design.Layout.Interchange;

public static class ComponentSymbolLegacyReader
{
    public sealed record ReadResult(ComponentPart? Part, string? Refusal);

    private const string Header = "EESchema-LIBRARY";

    /// <summary>Reads the FIRST definition in <paramref name="text"/>, naming every other one.</summary>
    public static ReadResult Read(string text, string? wanted = null)
    {
        var lines = text.Split('\n');
        if (!lines.Any(l => l.TrimStart().StartsWith(Header, StringComparison.OrdinalIgnoreCase)))
            return new ReadResult(null,
                $"This file does not open with \"{Header}\" — it is not a symbol library of this format.");

        // ── Split into definitions first, so "the rest, by name" costs nothing ──────────────────
        var blocks = new List<(string Name, List<string> Lines)>();
        List<string>? current = null;
        string currentName = "";
        foreach (var raw in lines)
        {
            string line = raw.TrimEnd('\r');
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith("DEF ", StringComparison.Ordinal))
            {
                current = [line];
                currentName = Fields(trimmed).ElementAtOrDefault(1) ?? "symbol";
                continue;
            }
            if (current is null) continue;
            current.Add(line);
            if (trimmed.StartsWith("ENDDEF", StringComparison.Ordinal))
            {
                blocks.Add((currentName, current));
                current = null;
            }
        }

        if (blocks.Count == 0)
            return new ReadResult(null, "This library declares no DEF … ENDDEF definition at all.");

        int chosen = wanted is null ? 0 : Math.Max(0, blocks.FindIndex(b => b.Name == wanted));
        var part = new ComponentPart { Name = blocks[chosen].Name.TrimStart('~') };
        for (int i = 0; i < blocks.Count; i++)
            if (i != chosen) part.UnimportedSections.Add(blocks[i].Name);

        part.Symbol = ReadBlock(blocks[chosen].Lines, part);
        return new ReadResult(part, null);
    }

    private static ComponentSymbolDrawing ReadBlock(IReadOnlyList<string> lines, ComponentPart part)
    {
        var drawing = new ComponentSymbolDrawing { Name = part.Name };
        bool drawing_ = false;

        foreach (var line in lines)
        {
            string t = line.TrimStart();
            if (t.Length == 0 || t[0] == '#') continue;

            if (t.StartsWith("DRAW", StringComparison.Ordinal)) { drawing_ = true; continue; }
            if (t.StartsWith("ENDDRAW", StringComparison.Ordinal)) { drawing_ = false; continue; }
            if (t.StartsWith("$FPLIST", StringComparison.Ordinal) || t.StartsWith("$ENDFPLIST", StringComparison.Ordinal))
                continue;

            if (t.StartsWith("DEF ", StringComparison.Ordinal)) { ReadDef(t, part); continue; }
            if (t.Length > 1 && t[0] == 'F' && char.IsDigit(t[1])) { ReadField(t, part); continue; }
            if (!drawing_) continue;

            switch (t[0])
            {
                case 'X': ReadPin(t, drawing); break;
                case 'S': ReadRect(t, drawing); break;
                case 'P': ReadPoly(t, drawing); break;
                case 'A': ReadArc(t, drawing); break;
                case 'C': ReadCircle(t, drawing); break;
                case 'T': break;                       // annotation text — see the S-expression reader
                case 'B': break;                       // a Bézier: no symbol primitive reads from here
            }
        }

        return drawing;
    }

    /// <summary>
    /// <c>DEF name reference unused text_offset draw_pinnumber draw_pinname unit_count units_locked
    /// option_flag</c>. Only the section count matters to this phase (R-PL1-23).
    /// </summary>
    private static void ReadDef(string line, ComponentPart part)
    {
        var f = Fields(line);
        if (f.Count > 2 && f[2] is { Length: > 0 } prefix && prefix != "~")
            part.Metadata["Reference"] = prefix;
        if (f.Count > 7 && int.TryParse(f[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out int units))
            for (int u = 2; u <= units; u++)
                part.UnimportedSections.Add($"{part.Name} section {u}");
    }

    /// <summary>
    /// <c>F0</c>…<c>F3</c> are the reference, the value, the footprint reference and the datasheet, in
    /// that fixed order; <c>F4</c> and beyond carry a NAME of their own in a trailing quoted field.
    /// Carried verbatim (R-PL1-7) — never parsed, never used to infer a model.
    /// </summary>
    private static void ReadField(string line, ComponentPart part)
    {
        var f = Fields(line);
        if (f.Count < 2) return;
        string value = Unquote(f[1]);
        if (value.Length == 0 || value == "~") return;

        string name = f[0] switch
        {
            "F0" => "Reference",
            "F1" => "Value",
            "F2" => "Footprint",
            "F3" => "Datasheet",
            _ => f.Count > 0 && f[^1].StartsWith('"') ? Unquote(f[^1]) : f[0],
        };
        if (name.Length > 0) part.Metadata[name] = value;
    }

    /// <summary>
    /// <c>X name pad posx posy length orientation snum snom unit convert etype [shape]</c>.
    ///
    /// <para>R-PL1-19 again: <c>posx posy</c> is the pin's FREE END and the body edge sits one
    /// <c>length</c> inward along <c>orientation</c>. R-PL1-9 again: <c>pad</c> is read as the STRING
    /// it is, so a thermal pad named <c>EPAD</c> survives.</para>
    /// </summary>
    private static void ReadPin(string line, ComponentSymbolDrawing drawing)
    {
        var f = Fields(line);
        if (f.Count < 5) return;
        string name = Unquote(f[1]);
        string pad = Unquote(f[2]);
        if (!Num(f[3], out double x) || !Num(f[4], out double y)) return;

        int px = (int)Math.Round(x, MidpointRounding.AwayFromZero);
        int py = (int)Math.Round(y, MidpointRounding.AwayFromZero);
        drawing.Pins.Add(new ComponentSymbolPin(
            name.Length > 0 && name != "~" ? name : pad,
            pad.Length > 0 && pad != "~" ? pad : null,
            px, py));

        // The lead from the terminal inward to the body, as the format draws it — one length along the
        // orientation LETTER, which is this format's spelling of the newer one's angle.
        if (f.Count > 6 && Num(f[5], out double length) && length > 0)
        {
            var (dx, dy) = ComponentSymbolLead.FromLetter(f[6]);
            int len = (int)Math.Round(length, MidpointRounding.AwayFromZero);
            drawing.Shapes.Add(new KitSymbolLine(px, py, px + dx * len, py + dy * len));
        }
    }

    /// <summary><c>S startx starty endx endy unit convert thickness cc</c>.</summary>
    private static void ReadRect(string line, ComponentSymbolDrawing drawing)
    {
        var f = Fields(line);
        if (f.Count < 5) return;
        if (!Num(f[1], out double x1) || !Num(f[2], out double y1) ||
            !Num(f[3], out double x2) || !Num(f[4], out double y2)) return;
        drawing.Shapes.Add(new KitSymbolRectangle(x1, y1, x2, y2, IsFilled(f.ElementAtOrDefault(8))));
    }

    /// <summary><c>P count unit convert thickness x1 y1 … cc</c>.</summary>
    private static void ReadPoly(string line, ComponentSymbolDrawing drawing)
    {
        var f = Fields(line);
        if (f.Count < 6) return;
        if (!int.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) || count < 2) return;

        var pts = new List<double>(count * 2);
        for (int i = 0; i < count * 2 && 5 + i < f.Count; i++)
        {
            if (!Num(f[5 + i], out double v)) return;
            pts.Add(v);
        }
        if (pts.Count < 4) return;

        string? fill = f.ElementAtOrDefault(5 + count * 2);
        bool closed = pts.Count >= 6
            && Math.Abs(pts[0] - pts[^2]) < 1e-9 && Math.Abs(pts[1] - pts[^1]) < 1e-9;
        if (closed) pts.RemoveRange(pts.Count - 2, 2);
        drawing.Shapes.Add(new KitSymbolPath(pts, closed || IsFilled(fill), IsFilled(fill)));
    }

    /// <summary>
    /// <c>A posx posy radius start end unit convert thickness cc startx starty endx endy</c>.
    ///
    /// <para><b>The angles are TENTHS of a degree</b>, counter-clockwise in this format's +y-up frame —
    /// which is exactly what <see cref="KitSymbolArc"/> takes, so no conversion happens here beyond the
    /// factor of ten. Reading them as whole degrees draws a tenth of the intended arc, which on a
    /// small feature is a barely-visible nick rather than an obvious error.</para>
    /// </summary>
    private static void ReadArc(string line, ComponentSymbolDrawing drawing)
    {
        var f = Fields(line);
        if (f.Count < 6) return;
        if (!Num(f[1], out double cx) || !Num(f[2], out double cy) || !Num(f[3], out double r)) return;
        if (!Num(f[4], out double start10) || !Num(f[5], out double end10)) return;

        double start = start10 / 10.0, end = end10 / 10.0;
        // Into [-180, 180]. Deliberately INCLUSIVE at both ends rather than the half-open (-180, 180]:
        // a stated half-circle carries its own direction in the sign, and folding -180 onto +180 would
        // silently mirror it — the same arc drawn the other way round, which still looks like an arc.
        double sweep = end - start;
        while (sweep < -180) sweep += 360;
        while (sweep > 180) sweep -= 360;
        drawing.Shapes.Add(new KitSymbolArc(cx, cy, r, start, sweep));
    }

    /// <summary><c>C posx posy radius unit convert thickness cc</c>.</summary>
    private static void ReadCircle(string line, ComponentSymbolDrawing drawing)
    {
        var f = Fields(line);
        if (f.Count < 4) return;
        if (!Num(f[1], out double cx) || !Num(f[2], out double cy) || !Num(f[3], out double r)) return;
        drawing.Shapes.Add(new KitSymbolArc(cx, cy, r, 0, 360));
    }

    // ── Line splitting ──────────────────────────────────────────────────────────────────────────

    /// <summary>Whitespace-separated fields, with double-quoted runs kept whole. The quoted fields are
    /// free text and routinely contain spaces — a description, a datasheet URL, a field name.</summary>
    internal static List<string> Fields(string line)
    {
        var fields = new List<string>();
        int i = 0;
        while (i < line.Length)
        {
            while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
            if (i >= line.Length) break;

            if (line[i] == '"')
            {
                int start = i++;
                while (i < line.Length && line[i] != '"') i++;
                if (i < line.Length) i++;
                fields.Add(line[start..i]);
            }
            else
            {
                int start = i;
                while (i < line.Length && !char.IsWhiteSpace(line[i])) i++;
                fields.Add(line[start..i]);
            }
        }
        return fields;
    }

    private static string Unquote(string field)
        => field.Length >= 2 && field[0] == '"' && field[^1] == '"' ? field[1..^1] : field;

    private static bool Num(string field, out double value)
        => double.TryParse(field, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    /// <summary><c>F</c> fills with the line colour, <c>f</c> with the background one, <c>N</c> not at
    /// all. circuitRF's symbol primitives carry one line role by design, so both fills read as fill.</summary>
    private static bool IsFilled(string? cc) => cc is "F" or "f";
}

// The S-expression symbol library — `.kicad_sym` (docs/sonnet-briefs/
// brief-PL1-component-library-import.md §6).
//
// Parsed with PcbSexpr, the same tokenizer the footprint and board readers use.
//
// Two conventions, both of which fail silently when broken:
//
// 1. UNITS (R-PL1-17). This epoch states millimetres; the older epoch states mils on a 100-mil pin
//    grid, and the two differ by exactly 0.0254. One mil is one symbol-editor local unit
//    (SymbolModel.cs: 100 local units = one connection-grid square P, and DsnSymbolReader.PinGrid is
//    100), so mm / 0.0254 puts a 2.54 mm pin on exactly 100 — no rounding and no fitting.
//
// 2. HANDEDNESS (R-PL1-18). This format is +y UP; `.csym` is +y DOWN. The flip is NOT done here: this
//    reader emits the file's own coordinates and ComponentImport negates them, the same split
//    KitSymbolArc's doc comment draws for its angles. A symbol imported without that flip renders
//    upside down while the footprint beside it renders correctly, so the two are gated separately.

using System.Globalization;
using CircuitRF.Core.Pdk;

namespace CircuitRF.Design.Layout.Interchange;

public static class ComponentSymbolSexprReader
{
    /// <summary>Millimetres per mil — exact, and the whole of R-PL1-17's conversion.</summary>
    public const double MillimetresPerMil = 0.0254;

    public sealed record ReadResult(ComponentPart? Part, string? Refusal);

    /// <summary>
    /// Reads the FIRST symbol in <paramref name="text"/>, and names every other one.
    /// </summary>
    /// <param name="wanted">Which symbol to read, when the library holds several. Null takes the
    /// first — and the rest are reported by name, never silently dropped.</param>
    public static ReadResult Read(string text, string? wanted = null)
    {
        var parsed = PcbSexpr.Parse(text);
        if (parsed.Root is null)
            return new ReadResult(null, "This file contains no S-expression at all — it is not a symbol library.");

        // A library, or a bare (symbol …) fragment — dispatch on the token present, never on a
        // version stamp (R-PL1-14 applies to both halves of this import).
        var symbols = parsed.Root.Tag == "symbol"
            ? new List<PcbNode> { parsed.Root }
            : [.. parsed.Root.Children("symbol")];

        if (symbols.Count == 0)
            return new ReadResult(null,
                $"This file's root is ({parsed.Root.Tag} …) and it declares no (symbol …) — it is not a " +
                "symbol library.");

        var chosen = wanted is null
            ? symbols[0]
            : symbols.FirstOrDefault(s => string.Equals(s.Atom(0), wanted, StringComparison.Ordinal)) ?? symbols[0];

        var part = new ComponentPart { Name = chosen.Atom(0) ?? "symbol" };
        part.Messages.AddRange(parsed.Diagnostics);

        foreach (var other in symbols)
            if (!ReferenceEquals(other, chosen) && other.Atom(0) is { Length: > 0 } name)
                part.UnimportedSections.Add(name);

        if (chosen.Child("extends") is { } ext)
            part.Messages.Add(
                $"This symbol is derived from \"{ext.Atom(0)}\" — a derived symbol inherits its base's " +
                "drawing, which this phase does not resolve. Only what this definition states itself " +
                "was read.");

        ReadProperties(chosen, part);
        part.Symbol = ReadDrawing(chosen, part);
        return new ReadResult(part, null);
    }

    // ── Properties ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// R-PL1-7: every free-text property the file carries, verbatim.
    ///
    /// <para>Never parsed, and never used to infer a model. The reference-designator prefix and the
    /// footprint library reference are carried too, since they name which land pattern the file
    /// intended.</para>
    /// </summary>
    private static void ReadProperties(PcbNode symbol, ComponentPart part)
    {
        foreach (var p in symbol.Children("property"))
        {
            string name = p.Atom(0) ?? "";
            string value = p.Atom(1) ?? "";
            if (name.Length == 0 || value.Length == 0) continue;

            // "ki_description" is the same field as a plain "Description" written by a later epoch, and
            // as the XML library's own <description>; spelling them differently on the cell would hide
            // one of them from a user who searched.
            if (name.Length > 3 && name.StartsWith("ki_", StringComparison.OrdinalIgnoreCase))
                name = char.ToUpperInvariant(name[3]) + name[4..];

            part.Metadata[name] = value;
        }
    }

    // ── The drawing ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The body and the pins, gathered across the sub-symbols.
    ///
    /// <para>A sub-symbol's name ends <c>_&lt;unit&gt;_&lt;style&gt;</c>. Unit <b>0</b> is shared by
    /// every unit of a multi-section part; style <b>2</b> is the alternate body, which is a second
    /// drawing of the same terminals and not a second part. This phase reads unit 0 and unit 1 at
    /// style 1, and NAMES the rest (R-PL1-23) — multi-section parts are a real feature with real
    /// semantics and belong in their own phase, not in a footnote of this one.</para>
    /// </summary>
    private static ComponentSymbolDrawing ReadDrawing(PcbNode symbol, ComponentPart part)
    {
        var drawing = new ComponentSymbolDrawing { Name = part.Name };

        var units = new SortedSet<int>();
        foreach (var sub in symbol.Children("symbol"))
        {
            var (unit, style) = UnitAndStyleOf(sub.Atom(0) ?? "", part.Name);
            if (unit > 0) units.Add(unit);
        }

        foreach (var sub in symbol.Children("symbol"))
        {
            var (unit, style) = UnitAndStyleOf(sub.Atom(0) ?? "", part.Name);
            if (unit > 1) continue;
            if (style > 1) continue;

            foreach (var node in sub.Nodes)
                ReadDrawingNode(node, drawing, part);
        }

        // Some libraries put the pins directly on the symbol rather than in a sub-symbol.
        foreach (var node in symbol.Nodes)
            if (node.Tag is not "symbol" and not "property")
                ReadDrawingNode(node, drawing, part);

        foreach (int u in units)
            if (u > 1) part.UnimportedSections.Add($"{part.Name} section {u}");

        return drawing;
    }

    /// <summary>The trailing <c>_unit_style</c> of a sub-symbol's name. Returns (1, 1) when the name
    /// carries neither — a single-section symbol written without the suffix.</summary>
    internal static (int Unit, int Style) UnitAndStyleOf(string subName, string parentName)
    {
        string tail = subName.StartsWith(parentName, StringComparison.Ordinal)
            ? subName[parentName.Length..]
            : subName;

        var parts = tail.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return (1, 1);
        if (!int.TryParse(parts[^2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int unit)) return (1, 1);
        if (!int.TryParse(parts[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int style)) return (unit, 1);
        return (unit, style);
    }

    private static void ReadDrawingNode(PcbNode node, ComponentSymbolDrawing drawing, ComponentPart part)
    {
        switch (node.Tag)
        {
            case "pin": ReadPin(node, drawing); break;

            case "rectangle":
            {
                var s = Xy(node.Child("start"));
                var e = Xy(node.Child("end"));
                if (s is null || e is null) break;
                drawing.Shapes.Add(new KitSymbolRectangle(s.Value.X, s.Value.Y, e.Value.X, e.Value.Y, FilledOf(node)));
                break;
            }

            case "polyline":
            {
                var pts = Points(node);
                if (pts.Count < 4) break;
                bool closed = pts.Count >= 6
                    && Math.Abs(pts[0] - pts[^2]) < 1e-9 && Math.Abs(pts[1] - pts[^1]) < 1e-9;
                if (closed) pts.RemoveRange(pts.Count - 2, 2);
                drawing.Shapes.Add(new KitSymbolPath(pts, closed, FilledOf(node)));
                break;
            }

            case "circle":
            {
                var c = Xy(node.Child("center"));
                double r = Mils(node.ChildNum("radius") ?? 0);
                if (c is null || r <= 0) break;
                // KitSymbolShape has no circle of its own; a full-sweep arc is the same curve, and the
                // consumer's arc conversion renders it as one.
                drawing.Shapes.Add(new KitSymbolArc(c.Value.X, c.Value.Y, r, 0, 360));
                break;
            }

            case "arc":
            {
                var s = Xy(node.Child("start"));
                var m = Xy(node.Child("mid"));
                var e = Xy(node.Child("end"));
                if (s is null || m is null || e is null) break;
                if (ArcThroughThreePoints(s.Value, m.Value, e.Value) is { } arc) drawing.Shapes.Add(arc);
                else part.Messages.Add("An arc whose three points are colinear was skipped — it states no circle.");
                break;
            }

            case "bezier" or "gr_curve":
                part.Messages.Add("A Bézier curve in the symbol was not imported — circuitRF's symbol " +
                                  "primitives state no cubic curve read from this format.");
                break;

            case "text" or "text_box":
                // Not imported: circuitRF's symbol text primitive carries its own font and style, and
                // this format states neither in terms circuitRF can honour.
                break;
        }
    }

    private static void ReadPin(PcbNode node, ComponentSymbolDrawing drawing)
    {
        var at = node.Child("at");
        if (at is null) return;

        // R-PL1-19: the stated coordinate is the pin's FREE END, not where it meets the body — the
        // pin carries a length and the body edge sits one length inward along the pin's own angle.
        // circuitRF's SymbolPin coordinate IS the connection point, so the stated point is exactly
        // what to write, and the body geometry must NOT be shifted to match it. Getting this wrong
        // yields a symbol whose pins float one pin-length off the box, which looks like a scale bug.
        int x = (int)Math.Round(Mils(at.Num(0) ?? 0), MidpointRounding.AwayFromZero);
        int y = (int)Math.Round(Mils(at.Num(1) ?? 0), MidpointRounding.AwayFromZero);

        string name = node.Child("name")?.Atom(0) ?? "";
        // R-PL1-9: a pad identifier is a STRING. A reader that parses it as an integer silently drops
        // the thermal pad — the one terminal on an RF part whose grounding decides the answer.
        string number = node.Child("number")?.Atom(0) ?? "";

        drawing.Pins.Add(new ComponentSymbolPin(
            name.Length > 0 ? name : number,
            number.Length > 0 ? number : null,
            x, y));

        // The lead the format draws from the terminal INWARD to the body. Emitted rather than left
        // implicit: the file states a length and an angle, the authoring editor draws that line, and a
        // symbol imported without it has its pins floating off its own box.
        int length = (int)Math.Round(Mils(node.ChildNum("length") ?? 0), MidpointRounding.AwayFromZero);
        if (length > 0)
        {
            var (dx, dy) = ComponentSymbolLead.Direction(at.Num(2) ?? 0);
            drawing.Shapes.Add(new KitSymbolLine(x, y, x + dx * length, y + dy * length));
        }
    }

    // ── Geometry helpers ────────────────────────────────────────────────────────────────────────

    internal static double Mils(double millimetres) => millimetres / MillimetresPerMil;

    private static (double X, double Y)? Xy(PcbNode? node)
        => node?.Num(0) is { } x && node.Num(1) is { } y ? (Mils(x), Mils(y)) : null;

    private static List<double> Points(PcbNode node)
    {
        var pts = new List<double>();
        foreach (var xy in node.Child("pts")?.Children("xy") ?? [])
            if (xy.Num(0) is { } x && xy.Num(1) is { } y) { pts.Add(Mils(x)); pts.Add(Mils(y)); }
        return pts;
    }

    /// <summary>A <c>(fill (type …))</c> of anything but <c>none</c> is filled. <c>background</c> is a
    /// theme colour rather than the line colour, but circuitRF's symbol primitives carry one line role
    /// by design (the file's own colours are the authoring editor's palette), so both read as fill.</summary>
    private static bool FilledOf(PcbNode node)
        => node.Child("fill")?.ChildAtom("type") is { } type && type != "none";

    /// <summary>
    /// The circle through three points, as a start angle and a signed sweep.
    ///
    /// <para>Angles are the file's own — counter-clockwise in its +y-UP frame — which is what
    /// <see cref="KitSymbolArc"/> documents and what <c>ComponentImport</c>'s sign flip expects.</para>
    /// </summary>
    internal static KitSymbolArc? ArcThroughThreePoints(
        (double X, double Y) s, (double X, double Y) m, (double X, double Y) e)
    {
        double a = s.X - m.X, b = s.Y - m.Y, c = e.X - m.X, d = e.Y - m.Y;
        double det = 2 * (a * d - b * c);
        if (Math.Abs(det) < 1e-12) return null;

        double sm = (s.X * s.X + s.Y * s.Y) - (m.X * m.X + m.Y * m.Y);
        double em = (e.X * e.X + e.Y * e.Y) - (m.X * m.X + m.Y * m.Y);
        double cx = (d * sm - b * em) / det;
        double cy = (a * em - c * sm) / det;
        double r = Math.Sqrt((s.X - cx) * (s.X - cx) + (s.Y - cy) * (s.Y - cy));
        if (!(r > 0) || !double.IsFinite(r)) return null;

        double a0 = Deg(s, cx, cy), am = Deg(m, cx, cy), a1 = Deg(e, cx, cy);
        double d1 = Wrap(am - a0), d2 = Wrap(a1 - am);
        double sweep = d1 + d2;
        // The two hops must run the same way round the circle; when they do not, the mid point does
        // not lie on the swept span and the shortest consistent sweep is the honest answer.
        if (d1 * d2 < 0) sweep = Wrap(a1 - a0);

        return new KitSymbolArc(cx, cy, r, a0, sweep);
    }

    private static double Deg((double X, double Y) p, double cx, double cy)
        => Math.Atan2(p.Y - cy, p.X - cx) * 180.0 / Math.PI;

    /// <summary>Into (−180, 180].</summary>
    private static double Wrap(double deg)
    {
        while (deg <= -180) deg += 360;
        while (deg > 180) deg -= 360;
        return deg;
    }
}

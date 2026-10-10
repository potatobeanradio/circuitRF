// The drawings a symbol region is compared with — brief-img-8-schematic-symbols.md R-im8-1, R-im8-2.
//
// Two sources, one shape:
//  * BUILT-IN: circuitRF's own symbols, sampled from BuiltInSymbols.Primitives — the single source of the geometry every
//    renderer draws, so if a symbol's look changes its template follows. Nothing here repeats a coordinate of them. The
//    two stubs are composed of the TLIN and ground drawings the same way TermG composes Term and Ground.
//  * ALTERNATE: the drawings other conventions use, one stroke file each under resources/image-symbol-templates/ (an
//    embedded resource; adding a drawing is adding a file).
//
// Either way a drawing is centre lines plus the TIPS of its pins, and the same step turns it into a template: each pin's
// lead — the straight run from its tip inward — is cut off, because on a picture the lead is wire and IM-7 has already
// taken it away. What is left is the BODY, and a pin is where its lead met the body, with the direction the lead left in.
// The template is then scaled to unit pin span (the largest distance between two pins; a one-pin drawing's body is one
// unit across) about the pins' centroid.
//
// What a template leaves out: text (IM-7 removes words before symbols are read) and filled dots (an inductor's polarity
// mark — a picture from elsewhere rarely has one, and a matcher that wanted it would penalise every picture without).
// A filled outline (a diode's triangle) is its outline: the matcher hollows a region before it reads it.

using System.Globalization;
using System.Reflection;
using CircuitRF.Design.Symbol;

namespace CircuitRF.Design.Schematic.Recognition;

/// <summary>What a symbol on a picture is (overview D9).</summary>
public enum ImageSymbolKind
{
    /// <summary>Nothing matched: shown as <c>?</c>, generated as a C (as the artwork series does).</summary>
    Unknown,
    Resistor,
    Inductor,
    Capacitor,
    Ground,

    /// <summary>A port, a terminal, a connector.</summary>
    Port,
    TransmissionLine,
    ShortStub,
    OpenStub,
    Diode,

    /// <summary>A FET or a BJT — cut out (D9).</summary>
    Transistor,

    /// <summary>An amplifier triangle — cut out (D9).</summary>
    Amplifier,

    /// <summary>A generic IC box — cut out (D9).</summary>
    Ic,
}

/// <summary>The structure rule a template's match must also pass (R-im8-4).</summary>
public enum StructureCheck
{
    None,

    /// <summary>Two bars across the axis with a gap between them.</summary>
    Capacitor,

    /// <summary>A zig-zag of at least three alternating vertices.</summary>
    ZigZag,

    /// <summary>At least three humps on one side of the axis.</summary>
    Coil,

    /// <summary>Bars shrinking away from the one attachment.</summary>
    Ground,
}

/// <summary>Whether a template is drawn filled.</summary>
public enum TemplateFill
{
    /// <summary>Either: a diode is drawn both ways.</summary>
    Any,

    /// <summary>Only an outline: a hollow rectangle is a resistor.</summary>
    Hollow,

    /// <summary>Only filled: a filled rectangle is an inductor.</summary>
    Solid,
}

/// <summary>A template's pin: where its lead met the body, and the unit direction the lead left in (outward).</summary>
public sealed record TemplatePin(string Name, double X, double Y, double DirX, double DirY);

/// <summary>One drawing of one kind, as centre lines at unit pin span (y down).</summary>
/// <param name="Name">The built-in symbol kind's name, or the stroke file's name without its extension.</param>
/// <param name="Convention">One line: the drawing convention it follows.</param>
/// <param name="Strokes">The body's centre lines, each flat x,y pairs.</param>
/// <param name="Source">The <see cref="BuiltInSymbols"/> entry it is drawn from; null for a stroke file.</param>
/// <param name="Composed">Drawn from <paramref name="Source"/> with something added or left out (a stub), so its pins
/// are not that entry's.</param>
public sealed record SymbolTemplate(
    string Name, ImageSymbolKind Kind, string Convention, IReadOnlyList<TemplatePin> Pins,
    IReadOnlyList<double[]> Strokes, StructureCheck Check, TemplateFill Fill, SymbolKind? Source, bool Composed = false)
{
    public override string ToString() => Name;
}

public static class SymbolTemplates
{
    /// <summary>The resource name prefix of the alternate drawings.</summary>
    public const string ResourcePrefix = "CircuitRF.Design.ImageSymbolTemplates.";

    private static readonly Lazy<IReadOnlyList<SymbolTemplate>> _builtIn = new(BuildBuiltIn);
    private static readonly Lazy<IReadOnlyList<SymbolTemplate>> _alternates = new(LoadAlternates);

    /// <summary>circuitRF's own symbols (R-im8-1).</summary>
    public static IReadOnlyList<SymbolTemplate> BuiltIn() => _builtIn.Value;

    /// <summary>The drawings other conventions use (R-im8-2).</summary>
    public static IReadOnlyList<SymbolTemplate> Alternates() => _alternates.Value;

    /// <summary>Every template, built-in first.</summary>
    public static IReadOnlyList<SymbolTemplate> All() => [.. BuiltIn(), .. Alternates()];

    // ── Built-in ────────────────────────────────────────────────────────────────────────────────────────────────

    private static IReadOnlyList<SymbolTemplate> BuildBuiltIn()
    {
        var r = new List<SymbolTemplate>
        {
            FromSymbol(SymbolKind.Resistor, ImageSymbolKind.Resistor, "A zig-zag between two leads.", StructureCheck.ZigZag),
            FromSymbol(SymbolKind.Inductor, ImageSymbolKind.Inductor, "Four humps on one side of the leads' line.", StructureCheck.Coil),
            FromSymbol(SymbolKind.Capacitor, ImageSymbolKind.Capacitor, "A flat plate and a curved plate across the leads.", StructureCheck.Capacitor),
            FromSymbol(SymbolKind.Ground, ImageSymbolKind.Ground, "Three bars shrinking away from the lead.", StructureCheck.Ground),
            FromSymbol(SymbolKind.Term, ImageSymbolKind.Port, "A termination: a zig-zag in a box."),
            FromSymbol(SymbolKind.Pin, ImageSymbolKind.Port, "A pin: a pointed flag on one lead."),
            FromSymbol(SymbolKind.Tline, ImageSymbolKind.TransmissionLine, "A rounded box with a line along it."),
            FromSymbol(SymbolKind.Mlin, ImageSymbolKind.TransmissionLine, "A rounded box: a trace."),
            FromSymbol(SymbolKind.Cpwg, ImageSymbolKind.TransmissionLine, "A trace between two coplanar grounds."),
            FromSymbol(SymbolKind.Slin, ImageSymbolKind.TransmissionLine, "A trace between two planes."),
            FromSymbol(SymbolKind.Diode, ImageSymbolKind.Diode, "A triangle pointing at a bar."),
            FromSymbol(SymbolKind.FetCurtice, ImageSymbolKind.Transistor, "An n-channel FET: a gate onto an unbroken channel."),
            FromSymbol(SymbolKind.PFetCurtice, ImageSymbolKind.Transistor, "A p-channel FET: a gate onto an unbroken channel."),
            FromSymbol(SymbolKind.BjtNpn, ImageSymbolKind.Transistor, "An n-p-n BJT: base bar, arrow out of the emitter."),
            FromSymbol(SymbolKind.BjtPnp, ImageSymbolKind.Transistor, "A p-n-p BJT: base bar, arrow into the base."),
        };
        r.AddRange(Stubs());
        return r;
    }

    private static SymbolTemplate FromSymbol(SymbolKind source, ImageSymbolKind kind, string convention,
                                             StructureCheck check = StructureCheck.None)
    {
        var sym = BuiltInSymbols.Primitives(source);
        var tips = sym.Pins.OrderBy(p => p.PortIndex).Select(p => (p.Name ?? (p.PortIndex + 1).ToString(CultureInfo.InvariantCulture), p.LocalX, p.LocalY)).ToList();
        // Drawn with a filled shape (a diode's triangle) it may be read filled or hollow; drawn in outline only, a
        // filled region is not it.
        bool filled = sym.Primitives.Any(p => p is PolygonPrimitive { Filled: true } or RectPrimitive { Filled: true }
                                                  or RoundedRectPrimitive { Filled: true });
        return Make(source.ToString(), kind, convention, Flatten(sym.Primitives), tips, check,
                    filled ? TemplateFill.Any : TemplateFill.Hollow, source);
    }

    /// <summary>The two stubs, composed of the TLIN drawing: its right lead left off is an open stub, and a ground's bars
    /// set against its right end, facing away, a short one.</summary>
    private static IEnumerable<SymbolTemplate> Stubs()
    {
        var tline = BuiltInSymbols.Primitives(SymbolKind.Tline);
        var ground = BuiltInSymbols.Primitives(SymbolKind.Ground);
        var left = tline.Pins.OrderBy(p => p.PortIndex).First();
        var right = tline.Pins.OrderBy(p => p.PortIndex).Last();
        var tips = new List<(string, double, double)> { (left.Name ?? "1", left.LocalX, left.LocalY) };

        // The right lead is the stroke ending on the right pin.
        var body = Flatten(tline.Primitives).Where(s => !EndsAt(s, right.LocalX, right.LocalY)).ToList();
        yield return Make("OpenStub", ImageSymbolKind.OpenStub, "A transmission line's box with one lead: an open stub.",
                          body, tips, StructureCheck.None, TemplateFill.Hollow, SymbolKind.Tline, composed: true);

        // The ground's bars, turned so its lead points left, with the lead's inner end on the box's right edge.
        var gpin = ground.Pins[0];
        var bars = Flatten(ground.Primitives);
        var (lead, _) = TrimLead(bars, gpin.LocalX, gpin.LocalY);
        double edge = body.SelectMany(Xs).Max();
        var turned = bars.Select(s =>
        {
            var t = new double[s.Length];
            // (x, y) → (y, −x) puts a lead leaving upwards leaving to the left; then the lead's end onto the edge.
            for (int i = 0; i < s.Length; i += 2)
            {
                double dx = s[i] - lead.X, dy = s[i + 1] - lead.Y;
                t[i] = edge + dy;
                t[i + 1] = right.LocalY - dx;
            }
            return t;
        });
        yield return Make("ShortStub", ImageSymbolKind.ShortStub,
                          "A transmission line's box with one lead and a ground on its far end: a shorted stub.",
                          [.. body, .. turned], tips, StructureCheck.None, TemplateFill.Hollow, SymbolKind.Tline, composed: true);
    }

    // ── Alternates ──────────────────────────────────────────────────────────────────────────────────────────────

    private static IReadOnlyList<SymbolTemplate> LoadAlternates()
    {
        var asm = typeof(SymbolTemplates).Assembly;
        var r = new List<SymbolTemplate>();
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var stream = asm.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            var file = name[ResourcePrefix.Length..];
            r.Add(Parse(Path.GetFileNameWithoutExtension(file), reader.ReadToEnd()));
        }
        return r;
    }

    /// <summary>Reads one stroke file. Lines, blank and <c>#</c> comments aside:
    /// <code>
    /// kind &lt;ImageSymbolKind&gt;          convention &lt;one line&gt;
    /// check none|capacitor|zigzag|coil|ground    fill hollow|any|solid   (default: none, hollow)
    /// pin &lt;name&gt; x y                   (the pin's TIP, in pin order)
    /// line x y x y …                   (an open polyline)
    /// loop x y x y …                   (a closed polygon)
    /// rect x0 y0 x1 y1
    /// circle cx cy r
    /// arc cx cy r startDeg sweepDeg    (clockwise from +x, y down — the ArcPrimitive convention)
    /// ellipse cx cy rx ry [startDeg sweepDeg]
    /// </code>
    /// Coordinates in any unit; the template is scaled to unit pin span. A drawing is set in the frame of its kind's
    /// built-in symbol — a two-terminal part upright with pin 1 on top, a line or an amplifier across with pin 1 on the
    /// left, a ground with its lead up, a terminal with its lead to the right — so an orientation means the same thing
    /// whichever drawing matched.</summary>
    public static SymbolTemplate Parse(string name, string text)
    {
        ImageSymbolKind? kind = null;
        string? convention = null;
        var check = StructureCheck.None;
        var fill = TemplateFill.Hollow;
        var tips = new List<(string, double, double)>();
        var strokes = new List<double[]>();
        int lineNo = 0;
        foreach (var raw in text.Split('\n'))
        {
            lineNo++;
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            int sp = line.IndexOf(' ');
            string word = sp < 0 ? line : line[..sp], rest = sp < 0 ? "" : line[(sp + 1)..].Trim();
            double[] N() => rest.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                                .Select(t => double.Parse(t, NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray();
            try
            {
                switch (word)
                {
                    case "kind": kind = Enum.Parse<ImageSymbolKind>(rest, ignoreCase: true); break;
                    case "convention": convention = rest; break;
                    case "check": check = Enum.Parse<StructureCheck>(rest, ignoreCase: true); break;
                    case "fill": fill = Enum.Parse<TemplateFill>(rest, ignoreCase: true); break;
                    case "pin":
                    {
                        var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        tips.Add((parts[0], Num(parts[1]), Num(parts[2])));
                        break;
                    }
                    case "line": strokes.Add(N()); break;
                    case "loop": { var p = N(); strokes.Add([.. p, p[0], p[1]]); break; }
                    case "rect":
                    {
                        var p = N();
                        strokes.Add([p[0], p[1], p[2], p[1], p[2], p[3], p[0], p[3], p[0], p[1]]);
                        break;
                    }
                    case "circle": { var p = N(); strokes.Add(Arc(p[0], p[1], p[2], 0, 360)); break; }
                    case "arc": { var p = N(); strokes.Add(Arc(p[0], p[1], p[2], p[3], p[4])); break; }
                    case "ellipse":
                    {
                        var p = N();
                        var e = Arc(0, 0, 1, p.Length > 4 ? p[4] : 0, p.Length > 4 ? p[5] : 360);
                        for (int i = 0; i < e.Length; i += 2) { e[i] = p[0] + p[2] * e[i]; e[i + 1] = p[1] + p[3] * e[i + 1]; }
                        strokes.Add(e);
                        break;
                    }
                    default: throw new FormatException($"unknown word '{word}'");
                }
            }
            catch (Exception e) when (e is FormatException or IndexOutOfRangeException or ArgumentException)
            {
                throw new FormatException($"{name}, line {lineNo}: {e.Message}", e);
            }
        }
        if (kind is null) throw new FormatException($"{name}: no kind.");
        if (convention is null) throw new FormatException($"{name}: no convention.");
        if (tips.Count == 0) throw new FormatException($"{name}: no pin.");
        return Make(name, kind.Value, convention, strokes, tips, check, fill, null);

        static double Num(string t) => double.Parse(t, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    // ── One drawing → one template ──────────────────────────────────────────────────────────────────────────────

    private static SymbolTemplate Make(string name, ImageSymbolKind kind, string convention, List<double[]> strokes,
                                       List<(string Name, double X, double Y)> tips, StructureCheck check, TemplateFill fill,
                                       SymbolKind? source, bool composed = false)
    {
        var body = strokes.Select(s => (double[])s.Clone()).ToList();
        var pins = new List<(string Name, double X, double Y, double Dx, double Dy)>();
        foreach (var (pinName, tx, ty) in tips)
        {
            var (inner, dir) = TrimLead(body, tx, ty);
            pins.Add((pinName, inner.X, inner.Y, dir.X, dir.Y));
        }
        body.RemoveAll(s => s.Length < 4);
        if (body.Count == 0) throw new InvalidOperationException($"Symbol template {name}: nothing but leads.");

        double cx, cy, span;
        if (pins.Count >= 2)
        {
            cx = pins.Average(p => p.X);
            cy = pins.Average(p => p.Y);
            span = 0;
            for (int i = 0; i < pins.Count; i++)
                for (int j = i + 1; j < pins.Count; j++)
                    span = Math.Max(span, Math.Sqrt(Sq(pins[i].X - pins[j].X) + Sq(pins[i].Y - pins[j].Y)));
        }
        else
        {
            double x0 = body.SelectMany(Xs).Min(), x1 = body.SelectMany(Xs).Max();
            double y0 = body.SelectMany(Ys).Min(), y1 = body.SelectMany(Ys).Max();
            (cx, cy, span) = ((x0 + x1) / 2, (y0 + y1) / 2, Math.Max(x1 - x0, y1 - y0));
        }
        if (span <= 0) throw new InvalidOperationException($"Symbol template {name}: no extent.");

        var unit = body.Select(s =>
        {
            var t = new double[s.Length];
            for (int i = 0; i < s.Length; i += 2) { t[i] = (s[i] - cx) / span; t[i + 1] = (s[i + 1] - cy) / span; }
            return t;
        }).ToList();
        var unitPins = pins.Select(p => new TemplatePin(p.Name, (p.X - cx) / span, (p.Y - cy) / span, p.Dx, p.Dy)).ToList();
        return new SymbolTemplate(name, kind, convention, unitPins, unit, check, fill, source, composed);
    }

    /// <summary>Cuts the lead off the stroke ending at the tip (<paramref name="tx"/>, <paramref name="ty"/>): the
    /// straight run from the tip to the first turn. Returns where the lead met the body and the unit direction from
    /// there to the tip. A tip with no stroke on it is its own inner end, pointing away from the body's centre.</summary>
    private static ((double X, double Y) Inner, (double X, double Y) Dir) TrimLead(List<double[]> strokes, double tx, double ty)
    {
        for (int k = 0; k < strokes.Count; k++)
        {
            var s = strokes[k];
            if (s.Length < 4) continue;
            int n = s.Length / 2;
            bool atStart = Near(s[0], s[1], tx, ty), atEnd = Near(s[^2], s[^1], tx, ty);
            if (!atStart && !atEnd) continue;
            if (!atStart) s = strokes[k] = Reverse(s);
            double dx = s[2] - s[0], dy = s[3] - s[1];
            int j = 1;
            while (j + 1 < n)
            {
                double ex = s[2 * j + 2] - s[2 * j], ey = s[2 * j + 3] - s[2 * j + 1];
                double cross = dx * ey - dy * ex, dot = dx * ex + dy * ey;
                if (dot <= 0 || Math.Abs(cross) > 1e-9 * Math.Sqrt((dx * dx + dy * dy) * (ex * ex + ey * ey))) break;
                j++;
            }
            var inner = (s[2 * j], s[2 * j + 1]);
            strokes[k] = s[(2 * j)..];
            double lx = tx - inner.Item1, ly = ty - inner.Item2, l = Math.Sqrt(lx * lx + ly * ly);
            return (inner, l > 0 ? (lx / l, ly / l) : (0, 0));
        }
        double bx = strokes.SelectMany(Xs).Average(), by = strokes.SelectMany(Ys).Average();
        double ox = tx - bx, oy = ty - by, ol = Math.Sqrt(ox * ox + oy * oy);
        return ((tx, ty), ol > 0 ? (ox / ol, oy / ol) : (0, 0));
    }

    private static bool EndsAt(double[] s, double x, double y) =>
        s.Length >= 4 && (Near(s[0], s[1], x, y) || Near(s[^2], s[^1], x, y));

    private static bool Near(double ax, double ay, double bx, double by) => Math.Abs(ax - bx) < 1e-6 && Math.Abs(ay - by) < 1e-6;

    private static double[] Reverse(double[] s)
    {
        var r = new double[s.Length];
        for (int i = 0; i < s.Length; i += 2) { r[s.Length - 2 - i] = s[i]; r[s.Length - 1 - i] = s[i + 1]; }
        return r;
    }

    // ── Primitives → centre lines ───────────────────────────────────────────────────────────────────────────────

    /// <summary>A symbol's primitives as polylines — what the renderer strokes, sampled. Text and filled dots are left
    /// out (see the header).</summary>
    internal static List<double[]> Flatten(IEnumerable<SymbolPrimitive> prims)
    {
        var r = new List<double[]>();
        foreach (var p in prims)
            switch (p)
            {
                case LinePrimitive l: r.Add([l.X1, l.Y1, l.X2, l.Y2]); break;
                case PolylinePrimitive pl: r.Add([.. pl.Points.SelectMany(q => q)]); break;
                case PolygonPrimitive pg when pg.Points.Count > 0:
                    r.Add([.. pg.Points.SelectMany(q => q), pg.Points[0][0], pg.Points[0][1]]);
                    break;
                case RectPrimitive rc:
                    r.Add(RoundedRect(rc.Cx, rc.Cy, rc.W, rc.H, 0));
                    break;
                case RoundedRectPrimitive rr:
                    r.Add(RoundedRect(rr.Cx, rr.Cy, rr.W, rr.H, rr.Radius));
                    break;
                case CirclePrimitive c when !c.Filled: r.Add(Arc(c.Cx, c.Cy, c.R, 0, 360)); break;
                case EllipsePrimitive e when !e.Filled:
                    r.Add([.. Enumerable.Range(0, 49).SelectMany(i =>
                    {
                        double t = 2 * Math.PI * i / 48;
                        return new[] { e.Cx + e.Rx * Math.Cos(t), e.Cy + e.Ry * Math.Sin(t) };
                    })]);
                    break;
                case ArcPrimitive a: r.Add(Arc(a.Cx, a.Cy, a.R, a.StartDeg, a.SweepDeg)); break;
                case QuadCurvePrimitive q:
                    r.Add([.. Enumerable.Range(0, 17).SelectMany(i =>
                    {
                        double t = i / 16.0, u = 1 - t;
                        return new[] { u * u * q.P0X + 2 * u * t * q.CtrlX + t * t * q.P2X, u * u * q.P0Y + 2 * u * t * q.CtrlY + t * t * q.P2Y };
                    })]);
                    break;
                case CubicCurvePrimitive cc:
                    r.Add([.. Enumerable.Range(0, 17).SelectMany(i =>
                    {
                        double t = i / 16.0, u = 1 - t;
                        return new[]
                        {
                            u * u * u * cc.P0X + 3 * u * u * t * cc.C1X + 3 * u * t * t * cc.C2X + t * t * t * cc.P3X,
                            u * u * u * cc.P0Y + 3 * u * u * t * cc.C1Y + 3 * u * t * t * cc.C2Y + t * t * t * cc.P3Y,
                        };
                    })]);
                    break;
                case SinePrimitive sn:
                {
                    int segs = Math.Max(2, (int)Math.Ceiling(sn.Cycles * Math.Max(1, sn.PtsPerCycle)));
                    r.Add([.. Enumerable.Range(0, segs + 1).SelectMany(i =>
                    {
                        double t = (double)i / segs, along = (t - 0.5) * sn.Length, off = sn.Amp * Math.Sin(2 * Math.PI * sn.Cycles * t);
                        return sn.Axis == SineAxis.Horizontal ? new[] { sn.Cx + along, sn.Cy - off } : new[] { sn.Cx + off, sn.Cy + along };
                    })]);
                    break;
                }
            }
        return r;
    }

    private static double[] Arc(double cx, double cy, double r, double startDeg, double sweepDeg)
    {
        int n = Math.Max(4, (int)Math.Ceiling(Math.Abs(sweepDeg) / 7.5));
        var p = new double[2 * (n + 1)];
        for (int i = 0; i <= n; i++)
        {
            double a = (startDeg + sweepDeg * i / n) * Math.PI / 180;
            p[2 * i] = cx + r * Math.Cos(a);
            p[2 * i + 1] = cy + r * Math.Sin(a);
        }
        return p;
    }

    private static double[] RoundedRect(double cx, double cy, double w, double h, double radius)
    {
        double x0 = cx - w / 2, x1 = cx + w / 2, y0 = cy - h / 2, y1 = cy + h / 2;
        double rr = Math.Clamp(radius, 0, Math.Min(w, h) / 2);
        if (rr <= 0) return [x0, y0, x1, y0, x1, y1, x0, y1, x0, y0];
        var pts = new List<double>();
        void Corner(double ccx, double ccy, double start)
        {
            for (int i = 0; i <= 6; i++)
            {
                double a = (start + 90.0 * i / 6) * Math.PI / 180;
                pts.Add(ccx + rr * Math.Cos(a));
                pts.Add(ccy + rr * Math.Sin(a));
            }
        }
        Corner(x1 - rr, y0 + rr, 270);
        Corner(x1 - rr, y1 - rr, 0);
        Corner(x0 + rr, y1 - rr, 90);
        Corner(x0 + rr, y0 + rr, 180);
        pts.Add(pts[0]);
        pts.Add(pts[1]);
        return [.. pts];
    }

    internal static IEnumerable<double> Xs(double[] s) { for (int i = 0; i < s.Length; i += 2) yield return s[i]; }
    internal static IEnumerable<double> Ys(double[] s) { for (int i = 1; i < s.Length; i += 2) yield return s[i]; }
    private static double Sq(double v) => v * v;
}

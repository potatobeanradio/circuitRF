using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace CircuitRF.Ui.Schematic;

// ── Record-based ASCII symbol description (.dsn) ──────────────────────────────
//
// A general reader for the record-oriented ASCII drawing format some kits ship
// their schematic symbols in. NOTHING here is specific to any one part, kit, or
// supplier — it reads the FORMAT. A file that follows the grammar below imports;
// one that does not is reported, never guessed at.
//
// Grammar (one record per line; the leading integer is the record type):
//
//   1                                    file header
//   10  <?> "NAME" …                     symbol name
//   20  <viewIndex> … "a.prf" "b.lay"    open a view section
//   21                                   close the current view section
//   40  <layer> …                        layer/level switch (ignored)
//   44  minX minY maxX maxY …            view bounding box
//   50  <kind> minX minY maxX maxY …     open a graphic object; kind selects the shape
//   60  <geomKind> <?> <n> …             geometry descriptor for the open object
//   62  <?> h <?> x y rot … "font" `TEXT`  text payload for an open kind-6 object
//   70  x y  x y  …                      geometry points (may continue across lines)
//   90  "propName" … `value`             property attached to the open object
//   42  <id> <?> "name" num <?> <?> x y angle …   pin
//
// Object kinds seen on record 50:
//   1 → closed polygon    2 → open polyline    5 → arc/circle
//   6 → text box          7 → rectangle
//
// Coordinate conventions, and the two conversions this reader performs:
//
//   • The file is Y-UP (a pin at +500 sits above the origin). circuitRF symbol
//     local coordinates are Y-DOWN. Every y is negated on the way in — including
//     arc angles, whose handedness flips with the axis (see BuildArc).
//   • Pin tips must land on the connection grid P=100 (SymbolModel's own rule),
//     so every pin is snapped after scaling. Snapping moves a pin at most 50
//     units relative to the artwork; two pins that snap onto the same point are
//     REPORTED rather than silently merged.
//
public sealed record DsnPin(string Name, int Number, double X, double Y, double AngleDeg);

/// <summary>Outcome of reading one symbol-description file.</summary>
public sealed class DsnSymbolReadResult
{
    /// <summary>Symbol name as declared by the file, or the file stem when absent.</summary>
    public string Name { get; init; } = "";

    /// <summary>The converted symbol, or null when nothing usable was found.</summary>
    public Symbol? Symbol { get; init; }

    /// <summary>Pins in declaration order, carrying the names the file gave them.</summary>
    public IReadOnlyList<DsnPin> Pins { get; init; } = [];

    /// <summary>Scale applied from file units to symbol-local units.</summary>
    public double Scale { get; init; } = 1.0;

    /// <summary>Everything the reader could not use, and why. Never silently dropped.</summary>
    public IReadOnlyList<string> Diagnostics { get; init; } = [];

    /// <summary>
    /// Things worth saying that are not faults. Kept apart from <see cref="Diagnostics"/> because a
    /// wall of warnings undermines the one line that is a real warning — and because a kit routinely
    /// ships perfectly good drawings that are not wireable parts (title blocks, annotations), which
    /// a reader has no way to tell apart from a part and no business calling broken.
    /// </summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    public bool Success => Symbol is not null;
}

/// <summary>
/// Reads a record-based ASCII symbol description into circuitRF's own <see cref="Symbol"/>.
///
/// <para>Never throws for malformed input — an unreadable file comes back with
/// <see cref="DsnSymbolReadResult.Success"/> false and a diagnostic saying what stopped it.
/// A file that is <em>partly</em> readable imports what it can and reports the rest, because a
/// symbol missing one decoration is far more useful than no symbol at all.</para>
/// </summary>
public static class DsnSymbolReader
{
    /// <summary>Connection grid. Every pin tip must be an exact multiple of this.</summary>
    private const double PinGrid = 100.0;

    /// <summary>
    /// Which translation a workspace's recorded kits were produced by.
    ///
    /// <para><b>Bump this whenever a change here could move a PIN.</b> Pins snap to
    /// <see cref="PinGrid"/>, so a scale change, a snap change, or anything touching pin placement
    /// moves them — and wires attached to them silently disconnect. A workspace records the version
    /// its kits were translated under; a mismatch is reported and REFUSED rather than re-translated,
    /// so the user asks for the upgrade instead of discovering it as broken connections.</para>
    ///
    /// <para>A change that cannot move a pin — a rendering-only fix to text or an arc — does not
    /// need a bump, and bumping for one costs every user a re-import for nothing.</para>
    /// </summary>
    public const int TranslationVersion = 1;

    /// <summary>
    /// Target band for the symbol's larger dimension, in local units. A power-of-ten scale is
    /// chosen to land inside it, so a kit authored in a different drawing unit still produces a
    /// legible symbol without this reader knowing anything about that kit.
    /// </summary>
    private const double MinExtent = 300.0;
    private const double MaxExtent = 30_000.0;

    public static DsnSymbolReadResult ReadFile(string path)
    {
        try
        {
            using var sr = new StreamReader(path);
            var r = Read(sr, Path.GetFileNameWithoutExtension(path));
            return r;
        }
        catch (IOException ex)
        {
            return Failed(Path.GetFileNameWithoutExtension(path), $"Could not read the file: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return Failed(Path.GetFileNameWithoutExtension(path), $"Access denied: {ex.Message}");
        }
    }

    public static DsnSymbolReadResult Read(TextReader reader, string fallbackName = "")
    {
        var diags = new List<string>();

        // ── Pass 1: pull the raw objects out of the chosen view, in file units ──
        var raw = new RawView();
        try
        {
            ParseView(reader, raw, diags);
        }
        catch (Exception ex)
        {
            diags.Add($"Parsing stopped: {ex.Message}");
        }

        string name = !string.IsNullOrWhiteSpace(raw.SymbolName) ? raw.SymbolName : fallbackName;

        if (raw.Objects.Count == 0 && raw.Pins.Count == 0)
        {
            diags.Add("No drawing objects or pins were found. The file may use a different format.");
            return new DsnSymbolReadResult { Name = name, Diagnostics = diags };
        }

        // ── Pass 2: choose a scale from the real extent ─────────────────────────
        double scale = ChooseScale(ComputeExtent(raw));

        // ── Pass 3: convert ─────────────────────────────────────────────────────
        var prims = new List<SymbolPrimitive>();
        foreach (var o in raw.Objects)
        {
            var p = Convert(o, scale, diags);
            if (p is not null) prims.Add(p);
        }

        var pins = new List<DsnPin>();
        var symbolPins = new List<SymbolPin>();
        var occupied = new Dictionary<(long, long), string>();

        foreach (var rp in raw.Pins.OrderBy(p => p.Number))
        {
            double px = SnapToPinGrid(rp.X * scale);
            double py = SnapToPinGrid(-rp.Y * scale);          // Y-up → Y-down

            var key = ((long)px, (long)py);
            if (occupied.TryGetValue(key, out var other))
                diags.Add($"Pins '{other}' and '{rp.Name}' land on the same point after snapping to " +
                          $"the {PinGrid:0} connection grid — they are closer together in the file than " +
                          "one grid step. Both are kept; move one before wiring.");
            else
                occupied[key] = rp.Name;

            pins.Add(rp with { X = px, Y = py });
            symbolPins.Add(new SymbolPin(px, py, rp.Number, rp.Name));
        }

        // A NOTE, not a problem. A drawing with no pins is very often exactly what it looks like — a
        // title block or an annotation the kit draws alongside its real parts — and the reader cannot
        // tell one from the other. It still installs and still renders; it just cannot be wired, which
        // is worth saying once and not worth calling a fault.
        var notes = new List<string>();
        if (symbolPins.Count == 0)
            notes.Add("No pins were declared, so this symbol cannot be wired.");

        return new DsnSymbolReadResult
        {
            Name        = name,
            Symbol      = new Symbol(prims, symbolPins, symbolPins.Count),
            Pins        = pins,
            Scale       = scale,
            Diagnostics = diags,
            Notes       = notes,
        };
    }

    // ── Scale ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// The drawing's larger dimension. The file's own declared view bounding box wins when it is
    /// non-degenerate — it is the author's statement of the drawing extent, and it stays stable
    /// even for a symbol whose visible content happens to sit in one corner. Only when that record
    /// is absent or empty does this fall back to measuring the content.
    /// </summary>
    private static double ComputeExtent(RawView raw)
    {
        if (raw.HasDeclaredBox)
        {
            double dw = Math.Abs(raw.BoxMaxX - raw.BoxMinX);
            double dh = Math.Abs(raw.BoxMaxY - raw.BoxMinY);
            if (dw > 0.0 || dh > 0.0) return Math.Max(dw, dh);
        }

        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;

        void Grow(double x, double y)
        {
            if (x < minX) minX = x;
            if (y < minY) minY = y;
            if (x > maxX) maxX = x;
            if (y > maxY) maxY = y;
        }

        foreach (var o in raw.Objects)
        {
            Grow(o.MinX, o.MinY);
            Grow(o.MaxX, o.MaxY);
        }
        foreach (var p in raw.Pins) Grow(p.X, p.Y);

        if (minX > maxX) return 0.0;
        return Math.Max(maxX - minX, maxY - minY);
    }

    /// <summary>
    /// Power-of-ten scale placing the symbol's larger dimension inside
    /// [<see cref="MinExtent"/>, <see cref="MaxExtent"/>). Returns 1.0 for a degenerate extent.
    /// </summary>
    internal static double ChooseScale(double extent)
    {
        if (!double.IsFinite(extent) || extent <= 0.0) return 1.0;

        double scale = 1.0;
        int guard = 0;
        while (extent * scale < MinExtent && guard++ < 12) scale *= 10.0;
        guard = 0;
        while (extent * scale >= MaxExtent && guard++ < 12) scale /= 10.0;
        return scale;
    }

    private static double SnapToPinGrid(double v) => Math.Round(v / PinGrid, MidpointRounding.AwayFromZero) * PinGrid;

    // ── Conversion ────────────────────────────────────────────────────────────

    private static SymbolPrimitive? Convert(RawObject o, double s, List<string> diags) => o.Kind switch
    {
        1 => BuildPolygon(o, s),
        2 => BuildPolyline(o, s),
        5 => BuildArc(o, s, diags),
        6 => BuildText(o, s),
        7 => BuildRect(o, s),
        _ => Unsupported(o, diags),
    };

    private static SymbolPrimitive? Unsupported(RawObject o, List<string> diags)
    {
        diags.Add($"Drawing object type {o.Kind} is not one this reader knows; it was skipped.");
        return null;
    }

    private static SymbolPrimitive? BuildPolygon(RawObject o, double s)
    {
        var pts = MapPoints(o, s);
        if (pts.Count < 3) return BuildPolyline(o, s);
        return new PolygonPrimitive
        {
            ColorRole  = SymbolColorRole.SymbolLine,
            StrokeTier = o.StrokeTier,
            Filled     = false,
            Points     = pts,
        };
    }

    private static SymbolPrimitive? BuildPolyline(RawObject o, double s)
    {
        var pts = MapPoints(o, s);
        if (pts.Count < 2) return null;

        if (pts.Count == 2)
            return new LinePrimitive(SymbolColorRole.SymbolLine, o.StrokeTier,
                                     pts[0][0], pts[0][1], pts[1][0], pts[1][1]);

        return new PolylinePrimitive
        {
            ColorRole  = SymbolColorRole.SymbolLine,
            StrokeTier = o.StrokeTier,
            Points     = pts,
        };
    }

    private static SymbolPrimitive BuildRect(RawObject o, double s) => new RectPrimitive
    {
        ColorRole  = SymbolColorRole.SymbolLine,
        StrokeTier = o.StrokeTier,
        Filled     = false,
        Cx         = (o.MinX + o.MaxX) * 0.5 * s,
        Cy         = -(o.MinY + o.MaxY) * 0.5 * s,
        W          = Math.Abs(o.MaxX - o.MinX) * s,
        H          = Math.Abs(o.MaxY - o.MinY) * s,
    };

    /// <summary>
    /// Arc geometry is <c>startX startY  centerX centerY  sweepMillidegrees …</c>.
    ///
    /// <para>The Y flip is a REFLECTION, so it reverses the sense of rotation: an angle measured
    /// counter-clockwise in the file's Y-up frame is the same physical direction as the negated
    /// angle measured clockwise in circuitRF's Y-down frame. Both the start angle and the sweep
    /// are negated. Getting this wrong still draws an arc — a mirrored one — which is exactly the
    /// kind of error that survives review, so it is stated here rather than left implicit.</para>
    /// </summary>
    private static SymbolPrimitive? BuildArc(RawObject o, double s, List<string> diags)
    {
        if (o.Points.Count < 5)
        {
            diags.Add("An arc object carried too few geometry values and was skipped.");
            return null;
        }

        double sx = o.Points[0], sy = o.Points[1];
        double cx = o.Points[2], cy = o.Points[3];
        double sweepDeg = o.Points[4] / 1000.0;

        double r = Math.Sqrt((sx - cx) * (sx - cx) + (sy - cy) * (sy - cy)) * s;
        if (r <= 0.0)
        {
            diags.Add("An arc object had zero radius and was skipped.");
            return null;
        }

        double ccx = cx * s;
        double ccy = -cy * s;

        if (Math.Abs(sweepDeg) >= 359.999)
            return new CirclePrimitive
            {
                ColorRole  = SymbolColorRole.SymbolLine,
                StrokeTier = o.StrokeTier,
                Filled     = false,
                Cx = ccx, Cy = ccy, R = r,
            };

        double startDsn = Math.Atan2(sy - cy, sx - cx) * 180.0 / Math.PI;

        return new ArcPrimitive
        {
            ColorRole  = SymbolColorRole.SymbolLine,
            StrokeTier = o.StrokeTier,
            Cx = ccx, Cy = ccy, R = r,
            StartDeg = -startDsn,
            SweepDeg = -sweepDeg,
        };
    }

    /// <summary>
    /// Text is anchored from the object's own bounding box, deliberately NOT from the coordinate
    /// fields on the text record — those are min-corner in some files and centre in others,
    /// distinguished only by a flag whose meaning is not documented. The bounding box is
    /// unambiguous in every file, so centring on it is correct without having to guess.
    /// </summary>
    private static SymbolPrimitive? BuildText(RawObject o, double s)
    {
        if (string.IsNullOrEmpty(o.Text)) return null;

        double h = Math.Abs(o.MaxY - o.MinY) * s;

        return new TextPrimitive
        {
            Content   = o.Text,
            AnchorX   = (o.MinX + o.MaxX) * 0.5 * s,
            AnchorY   = -(o.MinY + o.MaxY) * 0.5 * s,
            FontSize  = h > 0.0 ? h : 12.0,
            Align     = SymbolTextAlign.Center,
            VAlign    = SymbolTextVAlign.Middle,
            ColorRole = SymbolColorRole.SymbolText,
        };
    }

    private static List<double[]> MapPoints(RawObject o, double s)
    {
        var pts = new List<double[]>(o.Points.Count / 2);
        for (int i = 0; i + 1 < o.Points.Count; i += 2)
            pts.Add([o.Points[i] * s, -o.Points[i + 1] * s]);
        return pts;
    }

    // ── Parsing ───────────────────────────────────────────────────────────────

    private sealed class RawObject
    {
        public int    Kind;
        public double MinX, MinY, MaxX, MaxY;
        public int    ExpectedPoints;
        public List<double> Points = [];
        public string? Text;
        public SymbolStrokeTier StrokeTier = SymbolStrokeTier.Normal;
    }

    private sealed class RawView
    {
        public string SymbolName = "";
        public List<RawObject> Objects = [];
        public List<DsnPin>    Pins    = [];

        public bool   HasDeclaredBox;
        public double BoxMinX, BoxMinY, BoxMaxX, BoxMaxY;
    }

    private static void ParseView(TextReader reader, RawView raw, List<string> diags)
    {
        bool sawAnyView   = false;
        bool inView       = false;
        bool viewCaptured = false;   // the schematic view has already been consumed
        bool capture      = false;   // currently inside the view we want

        RawObject? open = null;

        void CloseOpen()
        {
            if (open is null) return;
            if (capture) raw.Objects.Add(open);
            open = null;
        }

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var t = Tokenize(line);
            if (t.Count == 0) continue;
            if (!int.TryParse(t[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int rec)) continue;

            switch (rec)
            {
                case 10:
                    if (t.Count > 2) raw.SymbolName = Unquote(t[2]);
                    break;

                case 20:
                {
                    CloseOpen();
                    sawAnyView = true;
                    inView     = true;
                    // A view is the schematic one when it names a schematic profile/layer file, or
                    // — when nothing says either way — when it is the first view in the file.
                    bool looksSchematic = t.Skip(1).Any(x => Unquote(x).Contains("schematic", StringComparison.OrdinalIgnoreCase));
                    bool looksLayout    = t.Skip(1).Any(x => Unquote(x).Contains("layout",    StringComparison.OrdinalIgnoreCase));
                    capture = !viewCaptured && (looksSchematic || (!looksLayout && raw.Objects.Count == 0 && raw.Pins.Count == 0));
                    break;
                }

                case 21:
                    CloseOpen();
                    if (capture) viewCaptured = true;
                    inView  = false;
                    capture = false;
                    break;

                case 50:
                {
                    CloseOpen();
                    if (!capture) break;
                    if (t.Count < 6) break;
                    open = new RawObject
                    {
                        Kind = ParseInt(t[1]),
                        MinX = ParseDouble(t[2]),
                        MinY = ParseDouble(t[3]),
                        MaxX = ParseDouble(t[4]),
                        MaxY = ParseDouble(t[5]),
                    };
                    break;
                }

                case 60:
                    // Geometry descriptor: field 3 is the declared point count for a polyline.
                    if (open is not null && t.Count > 3) open.ExpectedPoints = ParseInt(t[3]);
                    break;

                case 70:
                    if (open is null) break;
                    for (int i = 1; i < t.Count; i++)
                    {
                        if (TryParseDouble(t[i], out double v)) open.Points.Add(v);
                    }
                    break;

                case 62:
                    // Text payload — the content is the trailing backtick-quoted token.
                    if (open is not null && t.Count > 0)
                        open.Text = Unquote(t[^1]);
                    break;

                case 90:
                    if (open is not null && t.Count >= 2 &&
                        Unquote(t[1]).Contains("thickness", StringComparison.OrdinalIgnoreCase) &&
                        t.Count > 0 && TryParseDouble(Unquote(t[^1]), out double th))
                    {
                        open.StrokeTier = th <= 1.0 ? SymbolStrokeTier.Thin
                                        : th >= 3.0 ? SymbolStrokeTier.Thick
                                        : SymbolStrokeTier.Normal;
                    }
                    break;

                case 42:
                {
                    CloseOpen();
                    if (!capture) break;
                    if (t.Count < 10) break;
                    string pinName = Unquote(t[3]);
                    raw.Pins.Add(new DsnPin(
                        Name:     string.IsNullOrWhiteSpace(pinName) ? $"p{raw.Pins.Count + 1}" : pinName,
                        Number:   ParseInt(t[4]),
                        X:        ParseDouble(t[7]),
                        Y:        ParseDouble(t[8]),
                        AngleDeg: ParseDouble(t[9]) / 1000.0));
                    break;
                }

                case 44:
                    // Declared view bounding box — captured only for the view being read.
                    if (capture && t.Count >= 5)
                    {
                        raw.HasDeclaredBox = true;
                        raw.BoxMinX = ParseDouble(t[1]);
                        raw.BoxMinY = ParseDouble(t[2]);
                        raw.BoxMaxX = ParseDouble(t[3]);
                        raw.BoxMaxY = ParseDouble(t[4]);
                    }
                    break;

                default:
                    // 1 / 40 and anything else carry nothing this reader needs.
                    break;
            }
        }

        CloseOpen();

        if (!sawAnyView)
            diags.Add("No view section was found; the file does not follow the expected record layout.");
        else if (!viewCaptured && !inView)
            diags.Add("No schematic view was found; only non-schematic views are present.");
    }

    // ── Tokenizer ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Splits one record into tokens. Both quoting styles the format uses are honoured, so a
    /// quoted font name or a back-quoted label containing spaces stays one token.
    /// </summary>
    internal static List<string> Tokenize(string line)
    {
        var outp = new List<string>();
        int i = 0, n = line.Length;

        while (i < n)
        {
            while (i < n && char.IsWhiteSpace(line[i])) i++;
            if (i >= n) break;

            char c = line[i];
            if (c is '"' or '`')
            {
                char close = c;
                int start = ++i;
                while (i < n && line[i] != close) i++;
                outp.Add(line.Substring(start, Math.Max(0, i - start)));
                if (i < n) i++;                     // consume the closing quote
                continue;
            }

            int s0 = i;
            while (i < n && !char.IsWhiteSpace(line[i])) i++;
            outp.Add(line[s0..i]);
        }

        return outp;
    }

    private static string Unquote(string s) => s.Trim('"', '`');

    private static int ParseInt(string s) =>
        int.TryParse(Unquote(s), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;

    private static double ParseDouble(string s) => TryParseDouble(s, out double v) ? v : 0.0;

    private static bool TryParseDouble(string s, out double v) =>
        double.TryParse(Unquote(s), NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    private static DsnSymbolReadResult Failed(string name, string why) =>
        new() { Name = name, Diagnostics = [why] };
}

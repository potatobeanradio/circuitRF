// `.scr` — a COMMAND SCRIPT, and the only format in either phase that must be INTERPRETED
// (docs/sonnet-briefs/brief-PL2-component-library-breadth.md §4.5).
//
// ── R-PL2-15: this is a state machine, not a data file ────────────────────────────────────────────
//
// It is a sequence of imperative commands with mutable state. `Edit` opens a target, `Layer` sets the
// current layer for everything that follows, `Grid` sets units, `Change` mutates a default that
// subsequent commands inherit. `Layer` commands are INTERLEAVED with the geometry throughout — the
// brief counts hundreds of each in one file, essentially alternating — so the geometry is meaningless
// without replaying the state machine. A reader that pattern-matches `Smd` and `Wire` lines and
// ignores the `Layer` lines between them puts every shape on one layer.
//
// ── R-PL2-17: an unmodelled command is a REFUSAL, and here is why that differs from every other
//    reader in PL1 and PL2 ───────────────────────────────────────────────────────────────────────
//
// Everywhere else in these two phases, an unrecognised token costs exactly one skipped entity and is
// reported with a count. That is correct for a DATA format, where entities are independent.
//
// It is wrong here. An unknown command in a SCRIPT may have changed state — the current layer, the
// current target, the coordinate units — and every command after it then executes against a state the
// interpreter believes is something else. The damage is unbounded, silent, and downstream of the
// thing that caused it. So the first command this interpreter does not model stops the import, names
// itself, and creates nothing.
//
// ── Statement termination ─────────────────────────────────────────────────────────────────────────
//
// Most statements end with `;`, but `Pin` statements are terminated by the NEWLINE alone — a run of
// them carries no semicolon at all until whatever command follows. So a statement ends at a semicolon
// OR at end of line, and a reader that splits on `;` alone swallows an entire run of pins into
// whichever statement comes after them.

using System.Globalization;
using System.Text;
using CircuitRF.Core.Pdk;

namespace CircuitRF.Design.Layout.Interchange;

public static class ComponentScrReader
{
    public sealed record ReadResult(ComponentPart? Part, string? Refusal);

    /// <summary>
    /// Commands this interpreter models. A command outside this set stops the import by name
    /// (R-PL2-17) — the set is stated explicitly rather than inferred from a switch's default so that
    /// "is this modelled?" has one answer in one place.
    /// </summary>
    private static readonly HashSet<string> Modelled = new(StringComparer.OrdinalIgnoreCase)
    {
        "Grid", "Set", "Edit", "Layer", "Smd", "Pad", "Wire", "Pin", "Connect",
        "Attribute", "Change", "Text", "Technology", "Package", "Value", "Prefix",
        "Description", "Add", "Circle", "Rect", "Polygon", "Hole",
    };

    /// <summary>What <c>Edit</c> opened.</summary>
    private enum Target { None, Footprint, Symbol, Device }

    public static ReadResult Read(string text, int dbuPerMicron)
    {
        var part = new ComponentPart();
        var drawing = new ComponentSymbolDrawing();

        // ── The interpreter's whole state ───────────────────────────────────────────────────────
        var target = Target.None;
        ComponentArtwork? artwork = null;
        int layer = 0;
        double unitScale = 1.0;                     // source length -> mils
        var artworks = new List<ComponentArtwork>();
        var connected = new HashSet<(string Pin, string Pad)>();

        foreach (var (statement, lineNumber) in Statements(text))
        {
            var words = Words(statement);
            if (words.Count == 0) continue;

            string command = words[0];
            if (!Modelled.Contains(command))
                return new ReadResult(null,
                    $"Line {lineNumber}: \"{command}\" is a command this reader does not model. " +
                    "Nothing was imported — this is a script, so a command whose effect is unknown may " +
                    "have changed the layer, the units or the open target, and every shape after it " +
                    "would land somewhere this reader cannot vouch for.");

            switch (command.ToLowerInvariant())
            {
                case "grid":
                    // `Grid mil;` — the unit every coordinate after it is stated in.
                    unitScale = words.Count > 1 ? ScaleOf(words[1]) : unitScale;
                    break;

                case "edit":
                {
                    // `Edit 'NAME.pac';` — .pac is a land pattern, .sym the symbol, .dev the device.
                    string name = words.Count > 1 ? Unquote(words[1]) : "";
                    string extension = Path.GetExtension(name).ToLowerInvariant();
                    string stem = Path.GetFileNameWithoutExtension(name);

                    switch (extension)
                    {
                        case ".pac":
                        {
                            var (baseName, variant) = ComponentRecordsReader.SplitVariant(stem);
                            artwork = new ComponentArtwork { Name = baseName, Variant = variant };
                            artworks.Add(artwork);
                            target = Target.Footprint;
                            break;
                        }
                        case ".sym":
                            drawing.Name = stem;
                            target = Target.Symbol;
                            break;
                        default:
                            if (part.Name.Length == 0) part.Name = stem;
                            target = Target.Device;
                            break;
                    }

                    // A new target starts with no current layer — carrying the previous target's over
                    // is exactly the kind of state leak this format punishes.
                    layer = 0;
                    break;
                }

                case "layer":
                    layer = words.Count > 1 ? (int)Num(words[1]) : layer;
                    break;

                case "smd":
                case "pad":
                {
                    // `Smd '<pad>' <w> <h> <rot> R<n> (x y)`
                    if (artwork is null || words.Count < 3) break;
                    var numbers = words.Skip(2).Where(IsNumber).Select(Num).ToList();
                    var point = LastPoint(statement);
                    if (numbers.Count < 2 || point is null) break;

                    artwork.Pads.Add(new ComponentPadSpec(
                        Unquote(words[1]),
                        point.Value.X * unitScale, point.Value.Y * unitScale,
                        numbers[0] * unitScale, numbers[1] * unitScale,
                        command.Equals("pad", StringComparison.OrdinalIgnoreCase)
                            ? ComponentPadForm.Round
                            : ComponentPadForm.Rectangle,
                        RotationOf(words),
                        command.Equals("pad", StringComparison.OrdinalIgnoreCase) && numbers.Count > 2
                            ? numbers[2] * unitScale
                            : 0));
                    break;
                }

                case "wire":
                {
                    var points = Points(statement);
                    if (points.Count < 2) break;

                    var numbers = words.Skip(1).Where(IsNumber).Select(Num).ToList();
                    double width = numbers.Count > 0 ? numbers[0] * unitScale : 0;

                    if (target == Target.Symbol)
                    {
                        drawing.Shapes.Add(new KitSymbolLine(
                            points[0].X * unitScale, points[0].Y * unitScale,
                            points[1].X * unitScale, points[1].Y * unitScale));
                        break;
                    }

                    if (artwork is null) break;
                    var role = RoleOf(layer);
                    if (role == ComponentLayerRole.Unknown) artwork.NoteUnknownLayer(layer);
                    artwork.Paths.Add(new ComponentArtworkPath(
                        [points[0].X * unitScale, points[0].Y * unitScale,
                         points[1].X * unitScale, points[1].Y * unitScale],
                        width, role, false, false, layer));
                    break;
                }

                case "circle":
                {
                    var points = Points(statement);
                    if (artwork is null || points.Count < 2) break;
                    double r = Math.Sqrt(
                        Math.Pow(points[1].X - points[0].X, 2) + Math.Pow(points[1].Y - points[0].Y, 2));
                    var role = RoleOf(layer);
                    if (role == ComponentLayerRole.Unknown) artwork.NoteUnknownLayer(layer);
                    artwork.Circles.Add(new ComponentArtworkCircle(
                        points[0].X * unitScale, points[0].Y * unitScale, r * unitScale, 0, role, layer));
                    break;
                }

                case "pin":
                {
                    // `Pin '<name>' <type> <?> <length> R<n> <?> <?> (x y)` — the point is the pin's
                    // FREE END (PL1 R-PL1-19), as the body outline drawn around it confirms.
                    if (words.Count < 2) break;
                    var point = LastPoint(statement);
                    if (point is null) break;

                    drawing.Pins.Add(new ComponentSymbolPin(
                        Unquote(words[1]), null,
                        (int)Math.Round(point.Value.X * unitScale, MidpointRounding.AwayFromZero),
                        (int)Math.Round(point.Value.Y * unitScale, MidpointRounding.AwayFromZero)));
                    break;
                }

                case "connect":
                {
                    // R-PL2-16: `Connect '<gate>.<pinName>' '<pad>'` — this is the pin↔pad map, and it
                    // appears once per terminal.
                    if (words.Count < 3) break;
                    string pin = Unquote(words[1]);
                    int dot = pin.IndexOf('.');
                    if (dot >= 0) pin = pin[(dot + 1)..];
                    string pad = Unquote(words[2]);

                    // The script restates the whole map once per land-pattern variant it edits — the
                    // same nine joins three times over in a part with three density variants. They are
                    // one map, not three, so a repeat is dropped rather than added: ComponentTerminals
                    // reads this table as a set of joins and a triplicated entry misreports the pin
                    // count of every part that ships density variants.
                    if (!connected.Add((pin, pad)))
                        break;
                    part.ConnectTable.Add(new ComponentConnect(pin, pad));
                    break;
                }

                case "attribute":
                    if (words.Count >= 3) part.Metadata[words[1]] = Unquote(words[2]);
                    break;

                case "prefix":
                    if (words.Count >= 2) part.Metadata["Reference"] = Unquote(words[1]);
                    break;

                case "description":
                    if (words.Count >= 2 && Unquote(words[1]).Length > 0)
                        part.Metadata["Description"] = Unquote(words[1]);
                    break;

                case "package":
                    if (words.Count >= 2 && part.Name.Length == 0) part.Metadata["Package"] = Unquote(words[1]);
                    break;

                case "add":
                    if (words.Count >= 2 && part.Name.Length == 0) part.Name = Unquote(words[1]);
                    break;

                // Modelled, and deliberately without geometric effect: a text style default, a
                // wire-bend preference, a technology name, a value. Named here rather than left to a
                // default arm so that "modelled as a no-op" and "not modelled" stay distinguishable.
                case "set":
                case "change":
                case "text":
                case "technology":
                case "value":
                    break;
            }
        }

        // ── Assemble ────────────────────────────────────────────────────────────────────────────
        foreach (var art in artworks.OrderBy(a => a.Variant.Length == 0 ? 0 : 1))
        {
            if (art.Pads.Count == 0 && art.Paths.Count == 0) continue;

            var built = ComponentFootprintBuilder.Build(
                art, mils => ComponentFootprintBuilder.Mils(mils, dbuPerMicron));

            var footprint = new ComponentFootprint
            {
                Name = art.Name,
                Variant = art.Variant,
                Cell = built.Cell,
                LayerTable = built.LayerTable,
            };
            footprint.PadNames.AddRange(built.PadNames);
            part.Footprints.Add(footprint);
            foreach (var m in built.Messages) part.Messages.Add($"{art.Name}: {m}");
        }

        if (drawing.Pins.Count > 0 || drawing.Shapes.Count > 0)
        {
            if (part.Name.Length == 0) part.Name = drawing.Name;
            part.Symbol = drawing;
        }

        if (part.Symbol is null && part.Footprints.Count == 0)
            return new ReadResult(null, "This script opens neither a package nor a symbol.");

        return new ReadResult(part, null);
    }

    /// <summary>
    /// Statements, with the line each began on so a refusal can name it.
    ///
    /// <para>A statement ends at a semicolon OR at end of line — see this file's header for the run of
    /// newline-terminated <c>Pin</c> commands that makes the second half necessary.</para>
    /// </summary>
    private static IEnumerable<(string Statement, int Line)> Statements(string text)
    {
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd('\r').Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            foreach (var piece in line.Split(';'))
            {
                string statement = piece.Trim();
                if (statement.Length > 0) yield return (statement, i + 1);
            }
        }
    }

    /// <summary>Whitespace-separated words, with quoted strings kept whole and parenthesised point
    /// groups skipped (they are read separately by <see cref="Points"/>).</summary>
    private static List<string> Words(string statement)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        bool quoted = false;
        int depth = 0;

        foreach (char c in statement)
        {
            if (c == '\'') { quoted = !quoted; current.Append(c); continue; }
            if (!quoted && c == '(') { depth++; continue; }
            if (!quoted && c == ')') { depth--; continue; }
            if (depth > 0) continue;

            if (!quoted && char.IsWhiteSpace(c))
            {
                if (current.Length > 0) { result.Add(current.ToString()); current.Clear(); }
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0) result.Add(current.ToString());
        return result;
    }

    /// <summary>Every <c>(x y)</c> group in a statement, in order.</summary>
    private static List<(double X, double Y)> Points(string statement)
    {
        var result = new List<(double, double)>();
        int i = 0;
        while (i < statement.Length)
        {
            int open = statement.IndexOf('(', i);
            if (open < 0) break;
            int close = statement.IndexOf(')', open);
            if (close < 0) break;

            var parts = statement[(open + 1)..close]
                .Split([' ', ',', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2) result.Add((Num(parts[0]), Num(parts[1])));
            i = close + 1;
        }
        return result;
    }

    private static (double X, double Y)? LastPoint(string statement)
    {
        var points = Points(statement);
        return points.Count > 0 ? points[^1] : null;
    }

    /// <summary><c>R90</c>, <c>SR180</c>, <c>MR270</c> — the rotation word this dialect writes before
    /// the coordinate group.</summary>
    private static double RotationOf(List<string> words)
    {
        foreach (var word in words)
        {
            int r = word.LastIndexOf('R');
            if (r < 0 || r + 1 >= word.Length) continue;
            if (word[..r].Any(char.IsDigit)) continue;
            if (double.TryParse(word[(r + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                return v;
        }
        return 0;
    }

    /// <summary>Source unit → mils, so everything downstream is stated in one unit.</summary>
    private static double ScaleOf(string unit) => unit.ToLowerInvariant() switch
    {
        "mil" or "mils" => 1.0,
        "inch" or "inches" => 1000.0,
        "mm" => 1000.0 / 25.4,
        "mic" or "micron" or "microns" => 1.0 / 25.4,
        _ => 1.0,
    };

    /// <summary>Layer numbers of this dialect. Anything else is reported by number with a count
    /// (R-PL2-14) — which is a per-SHAPE report and quite separate from R-PL2-17's refusal, that one
    /// being about a COMMAND whose effect on state is unknown.</summary>
    private static ComponentLayerRole RoleOf(int layer) => layer switch
    {
        1 => ComponentLayerRole.TopCopper,
        16 => ComponentLayerRole.BottomCopper,
        20 => ComponentLayerRole.BoardOutline,
        21 => ComponentLayerRole.TopSilkscreen,
        31 => ComponentLayerRole.TopPaste,
        29 => ComponentLayerRole.TopMask,
        39 => ComponentLayerRole.TopCourtyard,
        51 => ComponentLayerRole.TopAssembly,
        _ => ComponentLayerRole.Unknown,
    };

    private static bool IsNumber(string word)
        => double.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

    private static double Num(string s)
        => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;

    private static string Unquote(string s)
    {
        string t = s.Trim();
        return t.Length >= 2 && t[0] == '\'' && t[^1] == '\'' ? t[1..^1] : t.Trim('\'');
    }
}

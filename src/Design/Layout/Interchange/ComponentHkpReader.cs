// The `.hkp` set — four files, TWO grammars, one extension
// (docs/sonnet-briefs/brief-PL2-component-library-breadth.md §4.2).
//
//   part file      dotted   the pin<->pad map, plus every metadata property
//   cell file      dotted   the land patterns — ALL density variants, in one file (R-PL2-9)
//   padstack file  dotted   pad geometry, reached through a two-step name indirection
//   symbol file    starred  the schematic drawing, and its own pin<->pad map
//
// ── R-PL2-6: dispatch on CONTENT, never on the file's name ────────────────────────────────────────
//
// The symbol file is `*KEYWORD` records with coordinates in angle brackets (`<300,-100>`); the other
// three are dotted-depth records where the number of leading dots is the nesting level (`.PACKAGE_CELL`
// / `..PIN` / `...XY`). The file NAMES are not part of any specification and have been observed to
// vary between sources (R-PL2-6), so `Grammar` looks at the first non-comment character and nothing
// else.
//
// Two things the dotted grammar does that a naive line reader gets wrong:
//   * a record may be INDENTED, so the dots are not necessarily at column 0;
//   * a multi-point `....XY` continues on following lines that carry NO dots at all, which a reader
//     keying on the dot prefix drops silently — losing every vertex of a polyline but the first.
//
// ── The map, and the trap that mirrors R-PL2-12 ───────────────────────────────────────────────────
//
// The part file states the map as THREE parallel lists — SwapIDs, PinNames, PinNumbers — joined by
// POSITION, and that map is ordered by pad. The symbol file numbers its own pins in DRAWING order and
// carries its own name/number text records keyed by that ordinal. **The two orders are different.**
// Joining the symbol's pin ordinal into the part file's positional lists therefore produces a fully
// populated, correctly-shaped, wrongly-wired part — exactly the failure R-PL2-12 describes for
// `.PLX`, in a format the brief does not flag for it.
//
// So the symbol's own text records are authoritative for the symbol's pins, the part file's lists are
// authoritative for the part, and the two are CROSS-CHECKED as sets: a disagreement is a refusal, not
// a preference. ComponentImportBreadthTests' Gate3a pins it, over a fixture whose symbol draws its
// pins in an order the pad numbering does not share.

using System.Globalization;
using System.Text;
using CircuitRF.Core.Pdk;

namespace CircuitRF.Design.Layout.Interchange;

/// <summary>Which of the two grammars a <c>.hkp</c> file is written in (R-PL2-6).</summary>
public enum HkpGrammar
{
    Unknown,

    /// <summary>Dotted-depth records — the part, cell and padstack files.</summary>
    Dotted,

    /// <summary><c>*KEYWORD</c> records with <c>&lt;x,y&gt;</c> coordinates — the symbol file.</summary>
    Starred,
}

/// <summary>What a dotted file declares at its top level, so one already-parsed file can be routed
/// without re-reading it.</summary>
public enum HkpDottedKind { Unknown, Parts, Cells, Padstacks }

public static class ComponentHkpReader
{
    public sealed record ReadResult(ComponentPart? Part, string? Refusal);

    /// <summary>The magic an encrypted twin opens with (R-PL2-7). Detected by CONTENT: the plaintext
    /// files' names are not specified and the encrypted ones' names follow them.</summary>
    private static readonly byte[] EncryptedMagic = [0x05, 0x06, 0x13, 0x00];

    /// <summary>True for the encrypted sibling that ships beside each of the four plaintext files
    /// (R-PL2-7). Skipped SILENTLY — the plaintext original sits right there, so reporting both
    /// doubles the chooser's noise for no information.</summary>
    public static bool IsEncryptedTwin(ReadOnlySpan<byte> head)
        => head.Length >= EncryptedMagic.Length && head[..EncryptedMagic.Length].SequenceEqual(EncryptedMagic);

    /// <summary>R-PL2-6: the first non-comment, non-blank character decides. <c>!</c> opens a comment
    /// in the starred grammar; the dotted grammar has none.</summary>
    public static HkpGrammar Grammar(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0 || line[0] == '!') continue;
            if (line[0] == '*') return HkpGrammar.Starred;
            if (line[0] == '.') return HkpGrammar.Dotted;
            return HkpGrammar.Unknown;
        }
        return HkpGrammar.Unknown;
    }

    /// <summary>Which dotted file this is, from its own <c>.FileType</c>/<c>.FILETYPE</c> declaration
    /// — again content, not name.</summary>
    public static HkpDottedKind DottedKind(string text)
    {
        foreach (var raw in text.Split('\n').Take(20))
        {
            string line = raw.TrimEnd('\r').Trim();
            if (!line.StartsWith('.')) continue;
            string upper = line.ToUpperInvariant();
            if (!upper.StartsWith(".FILETYPE", StringComparison.Ordinal)) continue;
            if (upper.Contains("PDB")) return HkpDottedKind.Parts;
            if (upper.Contains("CELL")) return HkpDottedKind.Cells;
            if (upper.Contains("PADSTACK")) return HkpDottedKind.Padstacks;
        }
        return HkpDottedKind.Unknown;
    }

    /// <summary>
    /// Reads the set. Every file is optional — a folder holding only some of the four is a partial
    /// read, reported rather than refused.
    /// </summary>
    public static ReadResult Read(
        string? partsText, string? cellsText, string? padsText, string? symbolsText, int dbuPerMicron)
    {
        var part = new ComponentPart();

        // ── The part file: metadata, and the map in pad order ───────────────────────────────────
        var partMap = new List<(string Pin, string Pad)>();
        if (partsText is not null)
        {
            var root = ParseDotted(partsText);
            ReadPartsFile(root, part, partMap);
        }

        // ── The symbol file, with its own authoritative map ─────────────────────────────────────
        List<(string Pin, string Pad)>? symbolMap = null;
        if (symbolsText is not null)
        {
            var symbol = ReadSymbolFile(symbolsText, part);
            if (symbol.Refusal is { } r) return new ReadResult(null, r);
            part.Symbol = symbol.Drawing;
            symbolMap = symbol.Map;
        }

        // ── The free consistency check the format hands us ──────────────────────────────────────
        if (partMap.Count > 0 && symbolMap is { Count: > 0 })
        {
            var fromPart = partMap.ToDictionary(m => m.Pad, m => m.Pin, StringComparer.Ordinal);
            foreach (var (pin, pad) in symbolMap)
            {
                if (!fromPart.TryGetValue(pad, out string? expected)) continue;
                if (!string.Equals(expected, pin, StringComparison.Ordinal))
                    return new ReadResult(null,
                        $"The part file and the symbol file disagree about pad \"{pad}\": the part " +
                        $"names it \"{expected}\" and the symbol names it \"{pin}\". Nothing was " +
                        "imported — one of the two files is stale, and picking either silently would " +
                        "wire the part wrongly.");
            }
        }

        foreach (var (pin, pad) in partMap) part.ConnectTable.Add(new ComponentConnect(pin, pad));

        // ── The cells, through the padstack indirection ─────────────────────────────────────────
        if (cellsText is not null)
        {
            var padGeometry = padsText is not null ? ReadPadstackFile(padsText, part) : [];
            var cells = ReadCellFile(cellsText, padGeometry, part);

            foreach (var artwork in cells)
            {
                var built = ComponentFootprintBuilder.Build(
                    artwork, mils => ComponentFootprintBuilder.Mils(mils, dbuPerMicron));

                var footprint = new ComponentFootprint
                {
                    Name = artwork.Name,
                    Variant = artwork.Variant,
                    Cell = built.Cell,
                    LayerTable = built.LayerTable,
                };
                footprint.PadNames.AddRange(built.PadNames);
                part.Footprints.Add(footprint);
                foreach (var m in built.Messages) part.Messages.Add($"{artwork.Name}: {m}");
            }
        }

        if (part.Symbol is null && part.Footprints.Count == 0)
            return new ReadResult(null, "This set declares neither a symbol nor a package cell.");

        return new ReadResult(part, null);
    }

    // ── The dotted grammar ────────────────────────────────────────────────────────────────────────

    /// <summary>One dotted record: its tag, the raw remainder of its line, and its children.</summary>
    internal sealed class HkpNode
    {
        public string Tag { get; init; } = "";
        public string Args { get; set; } = "";
        public List<HkpNode> Children { get; } = [];

        public HkpNode? First(string tag)
            => Children.FirstOrDefault(c => c.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase));

        public IEnumerable<HkpNode> All(string tag)
            => Children.Where(c => c.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase));

        /// <summary>The record's first quoted argument, or its whole argument text unquoted.</summary>
        public string Value => Unquote(Args);
    }

    /// <summary>
    /// Builds the record tree. <b>The two traps are handled here and nowhere else</b>: a record may be
    /// indented before its dots, and a line carrying no dots at all is a CONTINUATION of the record
    /// above it (which is how a multi-point <c>XY</c> states its second and later vertices).
    /// </summary>
    internal static HkpNode ParseDotted(string text)
    {
        var root = new HkpNode { Tag = "" };
        var stack = new List<HkpNode> { root };
        HkpNode? last = null;

        foreach (var raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            string trimmed = line.TrimStart();
            if (trimmed.Length == 0) continue;

            if (trimmed[0] != '.')
            {
                // A continuation — append to the record above rather than dropping it.
                if (last is not null) last.Args = last.Args.Length == 0 ? trimmed : last.Args + " " + trimmed;
                continue;
            }

            int depth = 0;
            while (depth < trimmed.Length && trimmed[depth] == '.') depth++;

            string rest = trimmed[depth..];
            int space = rest.IndexOf(' ');
            var node = new HkpNode
            {
                Tag = space < 0 ? rest : rest[..space],
                Args = space < 0 ? "" : rest[(space + 1)..].Trim(),
            };

            while (stack.Count > depth) stack.RemoveAt(stack.Count - 1);
            while (stack.Count < depth) stack.Add(stack[^1]);

            stack[^1].Children.Add(node);
            stack.Add(node);
            last = node;
        }

        return root;
    }

    /// <summary>
    /// The part file. Its map is three parallel lists joined by POSITION — see this file's header for
    /// why that map must never be indexed by a symbol's own pin ordinal.
    /// </summary>
    private static void ReadPartsFile(HkpNode root, ComponentPart part, List<(string Pin, string Pad)> map)
    {
        var number = root.First("Number");
        if (number is null) return;

        part.Name = number.Value;
        if (number.First("Name") is { } named && named.Value.Length > 0) part.Name = named.Value;

        // `..Prop "Key", "Value", "Type"` — carried verbatim (PL1 R-PL1-7).
        foreach (var prop in number.All("Prop"))
        {
            var parts = SplitQuotedList(prop.Args);
            if (parts.Count >= 2 && parts[0].Length > 0 && parts[1].Length > 0)
                part.Metadata[parts[0]] = parts[1];
        }

        var names = new List<string>();
        foreach (var symbol in number.All("Symbol"))
            foreach (var group in symbol.Children.Where(c => c.Tag.EndsWith("Swapgroup", StringComparison.OrdinalIgnoreCase)))
                names.AddRange(group.All("PinName").Select(n => n.Value));

        var numbers = new List<string>();
        foreach (var slots in number.All("Slots"))
            foreach (var group in slots.Children.Where(c => c.Tag.EndsWith("SwapGroup", StringComparison.OrdinalIgnoreCase)))
                numbers.AddRange(group.All("PinNumber").Select(n => n.Value));

        for (int i = 0; i < Math.Min(names.Count, numbers.Count); i++)
            map.Add((names[i], numbers[i]));

        if (names.Count != numbers.Count)
            part.Messages.Add(
                $"The part file states {names.Count} pin names and {numbers.Count} pin numbers. " +
                $"The first {Math.Min(names.Count, numbers.Count)} were joined; the rest carry no map.");

        // The cell names, so a reader knows which package cells belong to this part.
        if (number.First("TopCell") is { } top && top.Value.Length > 0)
            part.Metadata["Package"] = top.Value;
    }

    /// <summary>
    /// The padstack file, resolved through its own two-step indirection: a cell's pin names a
    /// <c>PADSTACK</c>, which names a <c>PAD</c>, which finally states the geometry.
    ///
    /// <para><b>R-PL2-8</b>: the same <c>.PAD</c> is declared once per pin that uses it, byte-identical.
    /// Deduplicated by name; a repeat is NOT a redefinition conflict and must not become one, and N
    /// identical padstacks are not N padstacks.</para>
    /// </summary>
    private static Dictionary<string, ComponentPadSpec> ReadPadstackFile(string text, ComponentPart part)
    {
        var root = ParseDotted(text);

        var pads = new Dictionary<string, (double W, double H, ComponentPadForm Form)>(StringComparer.Ordinal);
        int repeats = 0;

        foreach (var pad in root.All("PAD"))
        {
            string name = pad.Value;
            if (name.Length == 0) continue;
            if (pads.ContainsKey(name)) { repeats++; continue; }

            foreach (var shape in pad.Children)
            {
                var form = shape.Tag.ToUpperInvariant() switch
                {
                    "RECTANGLE" => ComponentPadForm.Rectangle,
                    "SQUARE" => ComponentPadForm.Rectangle,
                    "ROUND" or "CIRCLE" => ComponentPadForm.Round,
                    "OBLONG" or "OVAL" => ComponentPadForm.Oval,
                    "ROUNDED_RECTANGLE" => ComponentPadForm.RoundedRectangle,
                    _ => (ComponentPadForm?)null,
                } ?? default;

                if (shape.First("WIDTH") is not { } w) continue;
                double width = Num(w.Value);
                double height = shape.First("HEIGHT") is { } h ? Num(h.Value) : width;
                pads[name] = (width, height, form);
                break;
            }
        }

        if (repeats > 0)
            part.Messages.Add(
                $"The padstack library repeats {repeats:N0} pad definition(s) already declared — " +
                $"{pads.Count:N0} distinct pad(s) were kept.");

        // .PADSTACK -> TOP_PAD -> .PAD
        var byStack = new Dictionary<string, ComponentPadSpec>(StringComparer.Ordinal);
        foreach (var stack in root.All("PADSTACK"))
        {
            string stackName = stack.Value;
            if (stackName.Length == 0) continue;

            string? padName = stack.Children
                .SelectMany(c => c.Children.Concat([c]))
                .FirstOrDefault(c => c.Tag.Equals("TOP_PAD", StringComparison.OrdinalIgnoreCase))?.Value;

            if (padName is null || !pads.TryGetValue(padName, out var geometry)) continue;

            byStack[stackName] = new ComponentPadSpec(
                "", 0, 0, geometry.W, geometry.H, geometry.Form);
        }

        return byStack;
    }

    /// <summary>
    /// The cell file. <b>R-PL2-9: all density variants live in ONE file</b> — the nominal pattern and
    /// its siblings are three <c>PACKAGE_CELL</c> blocks here, so this returns a LIST. Returning the
    /// first and stopping loses two thirds of the file with no error.
    /// </summary>
    private static List<ComponentArtwork> ReadCellFile(
        string text, Dictionary<string, ComponentPadSpec> padstacks, ComponentPart part)
    {
        var root = ParseDotted(text);
        var result = new List<ComponentArtwork>();

        foreach (var cell in root.All("PACKAGE_CELL"))
        {
            var (baseName, variant) = ComponentRecordsReader.SplitVariant(cell.Value);
            var art = new ComponentArtwork { Name = baseName, Variant = variant };

            foreach (var pin in cell.All("PIN"))
            {
                string pad = pin.Value;
                var xy = Points(pin.First("XY")?.Args ?? "");
                if (xy.Count < 2) continue;

                double rotation = pin.First("ROTATION") is { } r ? Num(r.Value) : 0;
                string stackName = pin.First("PADSTACK")?.Value ?? "";

                var geometry = padstacks.TryGetValue(stackName, out var g)
                    ? g
                    : new ComponentPadSpec("", 0, 0, 0, 0, ComponentPadForm.Rectangle);

                art.Pads.Add(new ComponentPadSpec(
                    pad, xy[0], xy[1], geometry.Width, geometry.Height, geometry.Form, rotation));
            }

            // Each outline section names its own MEANING, so this format needs no layer-number legend
            // at all — the one place in this phase where that is true.
            AddOutlines(cell, "ASSEMBLY_OUTLINE", ComponentLayerRole.TopAssembly, art);
            AddOutlines(cell, "SILKSCREEN_OUTLINE", ComponentLayerRole.TopSilkscreen, art);
            AddOutlines(cell, "PLACEMENT_OUTLINE", ComponentLayerRole.TopCourtyard, art);

            result.Add(art);
        }

        if (result.Count > 1)
            part.Messages.Add(
                $"This cell library states {result.Count} package cells — they were imported as sibling " +
                "layout views of one cell.");

        var ordered = result
            .Select((a, i) => (Artwork: a, Index: i))
            .OrderBy(t => t.Artwork.Variant.Length == 0 ? 0 : 1)
            .ThenBy(t => t.Index)
            .Select(t => t.Artwork)
            .ToList();

        return ordered;
    }

    private static void AddOutlines(HkpNode cell, string tag, ComponentLayerRole role, ComponentArtwork art)
    {
        foreach (var outline in cell.All(tag))
            foreach (var path in outline.All("POLYLINE_PATH"))
            {
                var xy = Points(path.First("XY")?.Args ?? "");
                if (xy.Count < 4) continue;
                double width = path.First("WIDTH") is { } w ? Num(w.Value) : 0;
                art.Paths.Add(new ComponentArtworkPath(xy, width, role));
            }
    }

    // ── The starred grammar ───────────────────────────────────────────────────────────────────────

    private sealed record SymbolFileResult(
        ComponentSymbolDrawing? Drawing, List<(string Pin, string Pad)>? Map, string? Refusal);

    /// <summary>
    /// The symbol file. Its <c>*TEXT</c> records carry the pin's NAME and its PAD identifier, each
    /// keyed by the drawing pin's own ordinal — which is what makes this file self-sufficient, and
    /// what makes indexing the part file's lists by that ordinal wrong (see the header).
    /// </summary>
    private static SymbolFileResult ReadSymbolFile(string text, ComponentPart part)
    {
        var drawing = new ComponentSymbolDrawing();
        var pinPoint = new Dictionary<int, (double X, double Y)>();
        var pinName = new Dictionary<int, string>();
        var pinPad = new Dictionary<int, string>();
        var order = new List<int>();

        // `*UNITS 1000.000000 per_inch` — read, never assumed (§4.2). 1000 per inch is one mil per
        // unit, which is what ComponentSymbolPin wants; anything else is scaled to it.
        double perInch = 1000;

        foreach (var raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0 || line[0] == '!') continue;
            if (line[0] != '*') continue;

            var f = StarFields(line);
            if (f.Count == 0) continue;

            switch (f[0].ToUpperInvariant())
            {
                case "*UNITS":
                    if (f.Count > 1 && Num(f[1]) > 0) perInch = Num(f[1]);
                    break;

                case "*CELL_OPEN":
                    if (f.Count > 1 && drawing.Name.Length == 0) drawing.Name = Unquote(f[1]);
                    break;

                case "*GFX_LINE":
                {
                    var pts = f.Where(IsPoint).Select(ParsePoint).ToList();
                    if (pts.Count >= 2)
                        drawing.Shapes.Add(new KitSymbolLine(pts[0].X, pts[0].Y, pts[1].X, pts[1].Y));
                    break;
                }

                case "*GFX_RECTANGLE":
                {
                    var pts = f.Where(IsPoint).Select(ParsePoint).ToList();
                    if (pts.Count >= 2)
                        drawing.Shapes.Add(new KitSymbolRectangle(pts[0].X, pts[0].Y, pts[1].X, pts[1].Y, false));
                    break;
                }

                case "*PIN":
                {
                    if (f.Count < 2 || !int.TryParse(f[1], out int ordinal)) break;
                    var pt = f.Where(IsPoint).Select(ParsePoint).FirstOrDefault();
                    pinPoint[ordinal] = pt;
                    if (!order.Contains(ordinal)) order.Add(ordinal);
                    break;
                }

                case "*TEXT":
                {
                    // `*TEXT <size> <?> <type> <?> <?> <?> <pinOrdinal> … "<text>"`, where type 3 is
                    // the pin's name and type 4 its pad identifier. Both are read as STRINGS — a
                    // thermal pad is named, not numbered (PL1 R-PL1-9).
                    if (f.Count < 8) break;
                    if (!int.TryParse(f[3], out int type)) break;
                    if (!int.TryParse(f[7], out int ordinal) || ordinal == 0) break;

                    string value = Unquote(f[^1]);
                    if (value.Length == 0) break;
                    if (type == 3) pinName[ordinal] = value;
                    else if (type == 4) pinPad[ordinal] = value;
                    break;
                }
            }
        }

        if (pinPoint.Count == 0 && drawing.Shapes.Count == 0)
            return new SymbolFileResult(null, null, "This symbol file draws nothing and declares no pin.");

        double scale = perInch > 0 ? 1000.0 / perInch : 1.0;
        var map = new List<(string Pin, string Pad)>();

        foreach (int ordinal in order)
        {
            var (x, y) = pinPoint[ordinal];
            string name = pinName.TryGetValue(ordinal, out var n) ? n : $"pin{ordinal}";
            string? pad = pinPad.TryGetValue(ordinal, out var p) ? p : null;

            drawing.Pins.Add(new ComponentSymbolPin(
                name, pad,
                (int)Math.Round(x * scale, MidpointRounding.AwayFromZero),
                (int)Math.Round(y * scale, MidpointRounding.AwayFromZero)));

            if (pad is not null) map.Add((name, pad));
        }

        if (drawing.Name.Length > 0 && part.Name.Length == 0) part.Name = drawing.Name;
        return new SymbolFileResult(drawing, map, null);
    }

    // ── Shared scanning ───────────────────────────────────────────────────────────────────────────

    private static bool IsPoint(string field) => field.StartsWith('<') && field.EndsWith('>');

    private static (double X, double Y) ParsePoint(string field)
    {
        string inner = field.Trim('<', '>');
        int comma = inner.IndexOf(',');
        if (comma < 0) return (0, 0);
        return (Num(inner[..comma]), Num(inner[(comma + 1)..]));
    }

    /// <summary>Whitespace-separated fields, with quoted strings and <c>&lt;x,y&gt;</c> points kept
    /// whole — a point contains a comma but never a space, and a font name contains both.</summary>
    private static List<string> StarFields(string line)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        bool quoted = false;

        foreach (char c in line)
        {
            if (c == '"') { quoted = !quoted; current.Append(c); continue; }
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

    /// <summary><c>(x, y) (x, y) …</c> — a run of parenthesised pairs, which is how a multi-point
    /// <c>XY</c> reads once its continuation lines have been folded in.</summary>
    private static List<double> Points(string args)
    {
        var result = new List<double>();
        int i = 0;
        while (i < args.Length)
        {
            int open = args.IndexOf('(', i);
            if (open < 0) break;
            int close = args.IndexOf(')', open);
            if (close < 0) break;

            string inner = args[(open + 1)..close];
            int comma = inner.IndexOf(',');
            if (comma >= 0)
            {
                result.Add(Num(inner[..comma]));
                result.Add(Num(inner[(comma + 1)..]));
            }
            i = close + 1;
        }
        return result;
    }

    /// <summary><c>"a", "b", "c"</c> → the three values, quotes removed.</summary>
    private static List<string> SplitQuotedList(string args)
    {
        var result = new List<string>();
        int i = 0;
        while (i < args.Length)
        {
            int open = args.IndexOf('"', i);
            if (open < 0) break;
            int close = args.IndexOf('"', open + 1);
            if (close < 0) break;
            result.Add(args[(open + 1)..close]);
            i = close + 1;
        }
        return result;
    }

    private static string Unquote(string s)
    {
        string t = s.Trim();
        if (t.Length >= 2 && t[0] == '"')
        {
            int close = t.IndexOf('"', 1);
            return close > 0 ? t[1..close] : t[1..];
        }
        int space = t.IndexOf(' ');
        return space < 0 ? t : t[..space];
    }

    private static double Num(string s)
        => double.TryParse(s.Trim().Trim('"'), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;
}

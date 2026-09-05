// `.cxf` — flat, tab-separated, one record per line
// (docs/sonnet-briefs/brief-PL2-component-library-breadth.md §4.4).
//
// Each line is a record type followed by a run of `KEY=VALUE` fields: COMPONENT, PACKAGE, SYMBOL,
// PAD, PIN, LINE, RECTANGLE, ARC, POLYGON, TEXT. The simplest of the five formats.
//
// A `PACKAGE` record opens the land pattern and a `SYMBOL` record opens the schematic drawing; the
// geometry records that follow belong to whichever was opened last, which is the only state this
// reader carries.
//
// ── R-PL2-13: coordinates are in NANOMETRES, which is circuitRF's DBU exactly ─────────────────────
//
// A pad at -2.140001 mm is written `XM=-2140001`, so at the default 1000 DBU/µm the conversion is the
// IDENTITY — no scaling, no rounding, nothing to get wrong on the negative side. That is worth an
// assertion rather than trust: the gate lands the same pad from this format and from a mil-stated
// format on the identical DBU coordinate, negative case included, rather than checking this file's
// arithmetic against itself.
//
// ── R-PL2-14: FORM= and LAYER= are small integer enums with no in-file legend ─────────────────────
//
// So an unmapped value is never guessed into a shape. The values observed are imported; anything else
// is reported by NUMBER with a count and the entity is skipped, exactly as every other importer here
// reports an unknown token.

using System.Globalization;
using CircuitRF.Core.Pdk;

namespace CircuitRF.Design.Layout.Interchange;

public static class ComponentCxfReader
{
    public sealed record ReadResult(ComponentPart? Part, string? Refusal);

    /// <summary>The text FUNCTION value that marks a pin-name label. Other values mark the reference
    /// designator, the type and the value, which are not pin names and must not be read as any.</summary>
    private const int PinNameFunction = 5;

    public static ReadResult Read(string text, int dbuPerMicron)
    {
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        if (!lines.Any(l => l.StartsWith("COMPONENT", StringComparison.Ordinal)))
            return new ReadResult(null, "This file declares no COMPONENT record — it is not a library of this format.");

        var part = new ComponentPart();
        var artwork = new ComponentArtwork();
        var drawing = new ComponentSymbolDrawing();

        var symbolPins = new List<(string Pad, double X, double Y)>();
        var pinNameLabels = new List<(string Text, double X, double Y)>();
        var unknownForms = new Dictionary<int, int>();

        // Which section the geometry records currently belong to.
        bool inSymbol = false;
        int propertiesRemaining = 0;

        foreach (var raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;

            // The property lines that follow COMPONENT are bare KEY=VALUE with no record type, and
            // they are consumed by the count COMPONENT declared — so a property whose value happens
            // to look like a record type cannot be mistaken for one.
            if (propertiesRemaining > 0 && !line.Contains('\t'))
            {
                int eq = line.IndexOf('=');
                if (eq > 0)
                {
                    string key = line[..eq];
                    string value = line[(eq + 1)..].Trim();
                    if (value.Length > 0) part.Metadata[Titlecase(key)] = value;
                }
                propertiesRemaining--;
                continue;
            }

            var f = Fields(line);
            if (f.Type.Length == 0) continue;

            switch (f.Type)
            {
                case "COMPONENT":
                    part.Name = f.Text("NAME");
                    if (f.Text("PREFIX") is { Length: > 0 } prefix) part.Metadata["Reference"] = prefix;
                    propertiesRemaining = (int)f.Number("PROPERTIES", 0);
                    break;

                case "PACKAGE":
                    inSymbol = false;
                    artwork.Name = f.Text("NAME");
                    break;

                case "SYMBOL":
                    inSymbol = true;
                    break;

                case "PAD":
                {
                    int form = (int)f.Number("FORM", -1);
                    var padForm = FormOf(form);
                    if (padForm is null)
                    {
                        unknownForms[form] = unknownForms.GetValueOrDefault(form) + 1;
                        break;
                    }

                    // PADNAME is the identifier and PINNUMBER the ordinal; they differ exactly where
                    // it matters — a thermal pad is named (PL1 R-PL1-9).
                    string padName = f.Text("PADNAME");
                    if (padName.Length == 0) padName = f.Text("PINNUMBER");

                    artwork.Pads.Add(new ComponentPadSpec(
                        padName,
                        f.Number("XM", 0), f.Number("YM", 0),
                        f.Number("WIDTH", 0), f.Number("HEIGHT", 0),
                        padForm.Value,
                        f.Number("ROTATION", 0),
                        f.Number("DRILL", 0)));
                    break;
                }

                case "PIN":
                    symbolPins.Add((f.Text("PADNAME"), f.Number("X1", 0), f.Number("Y1", 0)));
                    break;

                case "LINE":
                {
                    int layer = (int)f.Number("LAYER", 0);
                    double[] xy = [f.Number("X1", 0), f.Number("Y1", 0), f.Number("X2", 0), f.Number("Y2", 0)];
                    if (inSymbol) drawing.Shapes.Add(new KitSymbolLine(Mil(xy[0]), Mil(xy[1]), Mil(xy[2]), Mil(xy[3])));
                    else AddPath(artwork, xy, f.Number("WIDTH", 0), layer, false);
                    break;
                }

                case "RECTANGLE":
                {
                    int layer = (int)f.Number("LAYER", 0);
                    double x1 = f.Number("X1", 0), y1 = f.Number("Y1", 0);
                    double x2 = f.Number("X2", 0), y2 = f.Number("Y2", 0);
                    if (inSymbol)
                        drawing.Shapes.Add(new KitSymbolRectangle(Mil(x1), Mil(y1), Mil(x2), Mil(y2), false));
                    else
                        AddPath(artwork, [x1, y1, x2, y1, x2, y2, x1, y2], f.Number("WIDTH", 0), layer, true);
                    break;
                }

                case "ARC":
                {
                    int layer = (int)f.Number("LAYER", 0);
                    double cx = f.Number("XM", 0), cy = f.Number("YM", 0), r = f.Number("RADIUS", 0);
                    double start = f.Number("START", 0), end = f.Number("END", 360);
                    if (inSymbol)
                        drawing.Shapes.Add(new KitSymbolArc(Mil(cx), Mil(cy), Mil(r), start, end - start));
                    else
                    {
                        var role = RoleOf(layer);
                        if (role == ComponentLayerRole.Unknown) artwork.NoteUnknownLayer(layer);
                        artwork.Circles.Add(new ComponentArtworkCircle(cx, cy, r, f.Number("WIDTH", 0), role, layer));
                    }
                    break;
                }

                case "POLYGON":
                {
                    int layer = (int)f.Number("LAYER", 0);
                    int nodes = (int)f.Number("NODES", 0);
                    var xy = new List<double>(nodes * 2);
                    for (int n = 1; n <= nodes; n++)
                    {
                        if (!f.Has($"X{n}") || !f.Has($"Y{n}")) break;
                        xy.Add(f.Number($"X{n}", 0));
                        xy.Add(f.Number($"Y{n}", 0));
                    }
                    if (xy.Count < 4) break;

                    if (inSymbol) drawing.Shapes.Add(new KitSymbolPath([.. xy.Select(Mil)], true, false));
                    else AddPath(artwork, [.. xy], f.Number("WIDTH", 0), layer, true);
                    break;
                }

                case "TEXT":
                    if (inSymbol && (int)f.Number("FUNCTION", -1) == PinNameFunction)
                        pinNameLabels.Add((f.Text("CONTENT"), f.Number("X1", 0), f.Number("Y1", 0)));
                    break;
            }
        }

        JoinSymbolPins(drawing, symbolPins, pinNameLabels, part);

        foreach (var (form, count) in unknownForms.OrderBy(kv => kv.Key))
            part.Messages.Add(
                $"FORM={form} is not a pad outline this reader models — {count:N0} pad(s) were skipped. " +
                "This format states no legend for FORM, so the shape was not guessed at.");

        if (artwork.Pads.Count > 0 || artwork.Paths.Count > 0)
        {
            if (artwork.Name.Length == 0) artwork.Name = part.Name;

            // R-PL2-13: the identity at the default resolution, and a real conversion elsewhere.
            var built = ComponentFootprintBuilder.Build(
                artwork, nm => ComponentFootprintBuilder.Nanometres(nm, dbuPerMicron));

            var footprint = new ComponentFootprint
            {
                Name = artwork.Name,
                Cell = built.Cell,
                LayerTable = built.LayerTable,
            };
            footprint.PadNames.AddRange(built.PadNames);
            part.Footprints.Add(footprint);
            foreach (var m in built.Messages) part.Messages.Add($"{artwork.Name}: {m}");
        }

        if (drawing.Pins.Count > 0 || drawing.Shapes.Count > 0)
        {
            drawing.Name = part.Name;
            part.Symbol = drawing;
        }

        if (part.Symbol is null && part.Footprints.Count == 0)
            return new ReadResult(null, "This file declares neither a package nor a symbol.");

        return new ReadResult(part, null);
    }

    /// <summary>
    /// Joins each symbol pin to its drawn name.
    ///
    /// <para>The <c>PIN</c> record states the PAD but no pin name; the name is a separate
    /// <c>TEXT … FUNCTION=5</c> record. The two runs are stated in the same order, which this pairs
    /// positionally — and then VERIFIES geometrically, because a positional pairing that happens to be
    /// wrong produces a wrongly-wired part exactly as R-PL2-12's ordinal join does. A label whose
    /// nearest pin is not the one it was paired with fails the check, and the whole join falls back to
    /// the pad identifier with the reason reported.</para>
    /// </summary>
    private static void JoinSymbolPins(
        ComponentSymbolDrawing drawing,
        List<(string Pad, double X, double Y)> pins,
        List<(string Text, double X, double Y)> labels,
        ComponentPart part)
    {
        bool paired = labels.Count == pins.Count && pins.Count > 0;

        if (paired)
            for (int i = 0; i < pins.Count && paired; i++)
            {
                // The label sits at a fixed offset from its own pin, so the pin NEAREST it must be the
                // one it was paired with.
                //
                // Distance is Euclidean, not |Δy|: a symbol with pins down both sides has two pins at
                // the SAME Y, so |Δy| ties and the tie breaks toward whichever side was declared first
                // — rejecting a pairing that is in fact correct. X is what separates the two sides, and
                // it is the larger of the two distances, so including it settles every such tie.
                double best = double.MaxValue;
                int bestIndex = -1;
                for (int j = 0; j < pins.Count; j++)
                {
                    double dx = pins[j].X - labels[i].X;
                    double dy = pins[j].Y - labels[i].Y;
                    double d = (dx * dx) + (dy * dy);
                    if (d < best) { best = d; bestIndex = j; }
                }
                if (bestIndex != i) paired = false;
            }

        for (int i = 0; i < pins.Count; i++)
        {
            var (pad, x, y) = pins[i];
            string name = paired ? labels[i].Text : pad;
            drawing.Pins.Add(new ComponentSymbolPin(
                name, pad.Length > 0 ? pad : null,
                (int)Math.Round(Mil(x), MidpointRounding.AwayFromZero),
                (int)Math.Round(Mil(y), MidpointRounding.AwayFromZero)));
        }

        if (!paired && pins.Count > 0)
            part.Messages.Add(
                $"This format states no pin name inside its PIN record, and the {labels.Count:N0} " +
                $"name label(s) could not be matched to the {pins.Count:N0} pin(s) with confidence. " +
                "Each pin carries its pad identifier as its name; the drawn labels were left as artwork.");
    }

    private static void AddPath(ComponentArtwork art, double[] xy, double width, int layer, bool closed)
    {
        var role = RoleOf(layer);
        if (role == ComponentLayerRole.Unknown) art.NoteUnknownLayer(layer);
        art.Paths.Add(new ComponentArtworkPath(xy, width, role, closed, false, layer));
    }

    /// <summary>Nanometres to mils — the symbol half's own unit, since
    /// <see cref="ComponentSymbolPin"/> is stated in mils (PL1 R-PL1-17).</summary>
    private static double Mil(double nanometres) => nanometres / 25400.0;

    /// <summary>The only pad outline observed in this format. R-PL2-14: an unmapped value is reported
    /// by number, never turned into a rectangle on the grounds that most pads are rectangles.</summary>
    private static ComponentPadForm? FormOf(int form) => form switch
    {
        1 => ComponentPadForm.Round,
        2 => ComponentPadForm.Rectangle,
        3 => ComponentPadForm.Oval,
        _ => null,
    };

    /// <summary>The layer numbers this format assigns fixed meanings to. Anything else is reported by
    /// number with a count (R-PL2-14).</summary>
    private static ComponentLayerRole RoleOf(int layer) => layer switch
    {
        2 => ComponentLayerRole.TopCopper,
        4 => ComponentLayerRole.TopAssembly,
        10 => ComponentLayerRole.TopCourtyard,
        12 => ComponentLayerRole.TopPaste,
        15 => ComponentLayerRole.TopMask,
        _ => ComponentLayerRole.Unknown,
    };

    private static string Titlecase(string key)
    {
        if (key.Length == 0) return key;
        var parts = key.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => char.ToUpperInvariant(p[0]) + p[1..].ToLowerInvariant());
        return string.Join('_', parts);
    }

    /// <summary>One record: its type word plus its <c>KEY=VALUE</c> fields.</summary>
    private readonly struct Record(string type, Dictionary<string, string> fields)
    {
        public string Type { get; } = type;
        private readonly Dictionary<string, string> _fields = fields;

        public bool Has(string key) => _fields.ContainsKey(key);

        public string Text(string key) => _fields.TryGetValue(key, out var v) ? v : "";

        public double Number(string key, double fallback)
            => _fields.TryGetValue(key, out var v)
            && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
                ? d : fallback;
    }

    /// <summary>
    /// Splits one record. Fields are tab-separated, and a VALUE may itself contain spaces (a
    /// description, a padstack name) — so the split is on the tab and never on whitespace.
    /// </summary>
    private static Record Fields(string line)
    {
        var parts = line.Split('\t');
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 1; i < parts.Length; i++)
        {
            int eq = parts[i].IndexOf('=');
            if (eq <= 0) continue;
            fields[parts[i][..eq].Trim()] = parts[i][(eq + 1)..].Trim();
        }

        return new Record(parts[0].Trim(), fields);
    }
}

// The XML component library — `.lbr` (docs/sonnet-briefs/brief-PL1-component-library-import.md §7).
//
// One file carrying <layers>, <packages>, <symbols> and <devicesets>. Of this phase's three formats it
// is the one that states the pin↔pad map as a SEPARATE TABLE, so it is also the one that can state a
// pin bonded to several pads (R-PL1-11).
//
// Four conventions, all of which fail silently when broken:
//
// 1. UNITS ARE MILLIMETRES THROUGHOUT, and Y IS UP — in the packages as well as the symbols. That
//    second half matters: the board format this folder already reads is y-DOWN and its reader negates
//    Y at the millimetre-to-DBU boundary (PcbUnits.Y). Doing that here would mirror every land pattern
//    and it would still look like a land pattern. So this reader converts with PcbUnits.Length for
//    BOTH axes and never calls PcbUnits.Y.
//
// 2. A PIN'S `length` IS A WORD, NOT A NUMBER (R-PL1-22) — `length="middle"`. The format's four named
//    lengths are a fixed enum in tenths of an inch. Parsing it as a number yields zero, which collapses
//    every pin's lead onto the body edge and produces a symbol that is wrong but not obviously wrong.
//
// 3. LAYERS ARE NUMBERED, with the name table at the head of the file, and the table is AUTHORITATIVE
//    (R-PL1-21). Never hard-code a number: everything below resolves through the file's own table, so
//    a file whose table is written in a different order imports identically.
//
// 4. DESCRIPTIONS CARRY HTML (R-PL1-24). Stripped to plain text for the cell's description parameter —
//    not rendered, and the markup is not stored.
//
// The root element's name is never matched, here or in the classifier. Dispatch is on the STRUCTURE —
// a <library> holding <packages>/<symbols>/<devicesets> — so the root element may be named anything.

using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using CircuitRF.Core.Pdk;

namespace CircuitRF.Design.Layout.Interchange;

public static class ComponentLibraryXmlReader
{
    public sealed record ReadResult(ComponentPart? Part, string? Refusal);

    /// <summary>The format's four named pin lengths, in mils. Words, not numbers — see convention 2
    /// above.</summary>
    internal static readonly IReadOnlyDictionary<string, int> NamedPinLengths =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["point"] = 0,
            ["short"] = 100,
            ["middle"] = 200,
            ["long"] = 300,
        };

    /// <summary>Millimetres per mil, for the symbol side. The package side stays in millimetres all
    /// the way to DBU.</summary>
    private const double MillimetresPerMil = ComponentSymbolSexprReader.MillimetresPerMil;

    /// <summary>Reads the FIRST device set, naming every other one.</summary>
    public static ReadResult Read(string text, int dbuPerMicron, string? wanted = null)
    {
        // ── The DOCTYPE is TOLERATED and never RESOLVED ─────────────────────────────────────────
        //
        // A file of this format opens with a <!DOCTYPE … SYSTEM "….dtd">, and XDocument.Parse prohibits
        // DTDs outright — it throws "For security reasons DTD is prohibited" on the second line of an
        // ordinary library. DtdProcessing.Ignore skips the declaration without expanding any entity in
        // it, and a null XmlResolver means the named .dtd is never fetched.
        XDocument doc;
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
            using var reader = XmlReader.Create(new StringReader(text), settings);
            doc = XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException ex)
        {
            return new ReadResult(null, $"This file is not well-formed XML: {ex.Message}");
        }

        var drawing = doc.Root?.Element("drawing") ?? doc.Root;
        var library = drawing?.Descendants("library").FirstOrDefault();
        if (library is null)
            return new ReadResult(null,
                "This XML file declares no <library> — it is not a component library of this format.");

        var layerNames = ReadLayerTable(drawing);
        var sets = library.Element("devicesets")?.Elements("deviceset").ToList() ?? [];
        var packages = library.Element("packages")?.Elements("package").ToList() ?? [];
        var symbols = library.Element("symbols")?.Elements("symbol").ToList() ?? [];

        if (sets.Count == 0 && packages.Count == 0)
            return new ReadResult(null, "This library declares neither a device set nor a package.");

        var part = new ComponentPart();

        // ── No device set at all: a package library. Import the first package, name the rest. ────
        if (sets.Count == 0)
        {
            part.Name = Attr(packages[0], "name") ?? "package";
            for (int i = 1; i < packages.Count; i++)
                part.UnimportedSections.Add(Attr(packages[i], "name") ?? $"package {i + 1}");
            AddFootprint(part, packages[0], layerNames, dbuPerMicron);
            Describe(part, packages[0]);
            return new ReadResult(part, null);
        }

        var chosen = wanted is null
            ? sets[0]
            : sets.FirstOrDefault(s => Attr(s, "name") == wanted) ?? sets[0];
        part.Name = Attr(chosen, "name") ?? "part";
        foreach (var other in sets)
            if (!ReferenceEquals(other, chosen) && Attr(other, "name") is { Length: > 0 } n)
                part.UnimportedSections.Add(n);

        Describe(part, chosen);
        if (Attr(chosen, "prefix") is { Length: > 0 } prefix) part.Metadata["Reference"] = prefix;

        // ── R-PL1-23: one gate and one device ───────────────────────────────────────────────────
        //
        // <gate> is a symbol section of a multi-section part; <device> is a package variant. This phase
        // reads the first of each and names the rest in ComponentPart.UnimportedSections /
        // UnimportedDeviceVariants; nothing is merged and nothing is dropped silently.
        var gates = chosen.Element("gates")?.Elements("gate").ToList() ?? [];
        var devices = chosen.Element("devices")?.Elements("device").ToList() ?? [];

        for (int i = 1; i < gates.Count; i++)
            part.UnimportedSections.Add($"{part.Name} gate {Attr(gates[i], "name") ?? (i + 1).ToString()}");
        for (int i = 1; i < devices.Count; i++)
            part.UnimportedDeviceVariants.Add(VariantName(devices[i], i));

        var gate = gates.FirstOrDefault();
        var device = devices.FirstOrDefault();

        // ── The symbol ──────────────────────────────────────────────────────────────────────────
        if (gate is not null && Attr(gate, "symbol") is { Length: > 0 } symbolName)
        {
            var symbol = symbols.FirstOrDefault(s => Attr(s, "name") == symbolName);
            if (symbol is null)
                part.Messages.Add($"This device set names a symbol \"{symbolName}\" the file does not declare.");
            else
                part.Symbol = ReadSymbol(symbol, part);
        }

        // ── The pin↔pad map, from its own table ─────────────────────────────────────────────────
        string? gateName = gate is null ? null : Attr(gate, "name");
        foreach (var connect in device?.Element("connects")?.Elements("connect") ?? [])
        {
            string section = Attr(connect, "gate") ?? "";
            if (gateName is { Length: > 0 } g && section.Length > 0 && section != g) continue;

            string pin = StripBondSuffix(Attr(connect, "pin") ?? "");
            // One <connect> may name SEVERAL pads, space separated — the format's own spelling for a
            // pin bonded to more than one.
            foreach (var pad in (Attr(connect, "pad") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
                if (pin.Length > 0) part.ConnectTable.Add(new ComponentConnect(pin, pad, section));
        }

        // ── The package ─────────────────────────────────────────────────────────────────────────
        if (device is not null && Attr(device, "package") is { Length: > 0 } packageName)
        {
            var package = packages.FirstOrDefault(p => Attr(p, "name") == packageName);
            if (package is null)
                part.Messages.Add($"This device names a package \"{packageName}\" the file does not declare.");
            else
                AddFootprint(part, package, layerNames, dbuPerMicron);
        }

        foreach (var attribute in device?.Element("technologies")?.Elements("technology")
                                        .SelectMany(t => t.Elements("attribute")) ?? [])
            if (Attr(attribute, "name") is { Length: > 0 } an && Attr(attribute, "value") is { Length: > 0 } av)
                part.Metadata[an] = av;

        return new ReadResult(part, null);
    }

    private static string VariantName(XElement device, int index)
    {
        string name = Attr(device, "name") ?? "";
        string package = Attr(device, "package") ?? "";
        if (name.Length > 0 && package.Length > 0) return $"{name} ({package})";
        if (package.Length > 0) return package;
        return name.Length > 0 ? name : $"variant {index + 1}";
    }

    // ── The layer table ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// R-PL1-21: number → name, from the file's own table. Never hard-coded, and never inferred from a
    /// range — a file whose table rows are written in a different order resolves identically, which is
    /// gate 13.
    /// </summary>
    internal static IReadOnlyDictionary<int, string> ReadLayerTable(XElement? drawing)
    {
        var names = new Dictionary<int, string>();
        foreach (var layer in drawing?.Descendants("layer") ?? [])
            if (int.TryParse(Attr(layer, "number"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
                && Attr(layer, "name") is { Length: > 0 } name)
                names[n] = name;
        return names;
    }

    /// <summary>
    /// The copper layers, taken from the file rather than from the numbering.
    ///
    /// <para>This format's layer table carries no type word (the board format's does), so copper is
    /// identified two ways: every layer an <c>&lt;smd&gt;</c> element draws on, since that element is
    /// copper by definition, plus the table's lowest-numbered row (R-PL1-21's table POSITION), which
    /// covers a library holding no surface-mount pad at all.</para>
    ///
    /// <para>A through-hole <c>&lt;pad&gt;</c> states no layer — the format defines it as spanning all
    /// copper — so its annulus is placed on the first and last of this set. When the set has one member
    /// the import says so.</para>
    /// </summary>
    internal static List<int> CopperLayerNumbers(XElement library, IReadOnlyDictionary<int, string> table)
    {
        var copper = new SortedSet<int>();
        foreach (var smd in library.Descendants("smd"))
            if (int.TryParse(Attr(smd, "layer"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
                copper.Add(n);

        if (table.Count > 0) copper.Add(table.Keys.Min());
        return [.. copper];
    }

    // ── Packages ────────────────────────────────────────────────────────────────────────────────

    private static void AddFootprint(
        ComponentPart part, XElement package, IReadOnlyDictionary<int, string> layerNames, int dbuPerMicron)
    {
        var cell = new PcbFootprintCell { LibraryName = Attr(package, "name") ?? part.Name };
        var root = package.AncestorsAndSelf().FirstOrDefault(e => e.Name == "library") ?? package;
        var copper = CopperLayerNumbers(root, layerNames);

        // The board format's table carries a TYPE word that says which rows are copper; this one does
        // not, so the type is filled in from the file's own evidence (see CopperLayerNumbers) before the
        // table reaches reconciliation. Without it every layer, copper included, would arrive as an
        // unknown source name and the front copper of a land pattern would map like a silkscreen line.
        var footprint = new ComponentFootprint
        {
            Name = cell.LibraryName,
            Cell = cell,
            LayerTable =
            [
                .. layerNames.OrderBy(kv => kv.Key).Select(kv =>
                    new PcbLayerTableEntry(kv.Key, kv.Value, copper.Contains(kv.Key) ? "signal" : "user", null)),
            ],
        };
        string LayerName(int n) => layerNames.TryGetValue(n, out var name) ? name : $"layer {n}";

        long Mm(double mm) => PcbUnits.Length(mm, dbuPerMicron);      // both axes: this format is Y-UP

        foreach (var e in package.Elements())
        {
            switch (e.Name.LocalName)
            {
                case "wire": ReadWire(e, cell, Mm, LayerName, part); break;
                case "rectangle": ReadRectangle(e, cell, Mm, LayerName); break;
                case "circle": ReadCircle(e, cell, Mm, LayerName); break;
                case "polygon": ReadPolygon(e, cell, Mm, LayerName); break;
                case "smd": ReadSmd(e, footprint, Mm, LayerName); break;
                case "pad": ReadPad(e, footprint, Mm, LayerName, copper, part); break;
                case "hole": ReadHole(e, cell, Mm); break;
                case "text": case "description": case "dimension": break;
            }
        }

        part.Footprints.Add(footprint);
    }

    private static void ReadWire(
        XElement e, PcbFootprintCell cell, Func<double, long> mm, Func<int, string> layerName, ComponentPart part)
    {
        if (!Num(e, "x1", out double x1) || !Num(e, "y1", out double y1) ||
            !Num(e, "x2", out double x2) || !Num(e, "y2", out double y2)) return;
        Num(e, "width", out double width);

        var pts = new List<long>();
        if (Num(e, "curve", out double curve) && Math.Abs(curve) > 1e-9)
        {
            foreach (var (px, py) in FlattenBulge(x1, y1, x2, y2, curve))
            {
                pts.Add(mm(px));
                pts.Add(mm(py));
            }
        }
        else
        {
            pts.AddRange([mm(x1), mm(y1), mm(x2), mm(y2)]);
        }

        cell.Shapes.Add(new PcbImportedShape(
            new PathShape { Xy = [.. pts], Width = mm(width), End = PathEndStyle.Round },
            layerName(LayerOf(e))));
    }

    private static void ReadRectangle(XElement e, PcbFootprintCell cell, Func<double, long> mm, Func<int, string> layerName)
    {
        if (!Num(e, "x1", out double x1) || !Num(e, "y1", out double y1) ||
            !Num(e, "x2", out double x2) || !Num(e, "y2", out double y2)) return;

        double rot = RotationOf(e);
        LayoutShape shape = Math.Abs(rot) < 1e-9
            ? new RectShape { X1 = mm(Math.Min(x1, x2)), Y1 = mm(Math.Min(y1, y2)), X2 = mm(Math.Max(x1, x2)), Y2 = mm(Math.Max(y1, y2)) }
            : new PolygonShape { Xy = RotatedBox(mm, (x1 + x2) / 2, (y1 + y2) / 2, Math.Abs(x2 - x1), Math.Abs(y2 - y1), rot) };
        cell.Shapes.Add(new PcbImportedShape(shape, layerName(LayerOf(e))));
    }

    private static void ReadCircle(XElement e, PcbFootprintCell cell, Func<double, long> mm, Func<int, string> layerName)
    {
        if (!Num(e, "x", out double x) || !Num(e, "y", out double y) || !Num(e, "radius", out double r)) return;
        Num(e, "width", out double width);

        // A zero-width circle is a filled disc; any other is a ring of that stroke width, which is a
        // closed path rather than a disc — importing it as a disc would fill a keep-out ring solid.
        LayoutShape shape = width <= 0
            ? new CircleShape { Cx = mm(x), Cy = mm(y), R = mm(r) }
            : new PathShape { Xy = CirclePath(mm, x, y, r), Width = mm(width), End = PathEndStyle.Round };
        cell.Shapes.Add(new PcbImportedShape(shape, layerName(LayerOf(e))));
    }

    private static void ReadPolygon(XElement e, PcbFootprintCell cell, Func<double, long> mm, Func<int, string> layerName)
    {
        var xy = new List<long>();
        foreach (var v in e.Elements("vertex"))
            if (Num(v, "x", out double x) && Num(v, "y", out double y)) { xy.Add(mm(x)); xy.Add(mm(y)); }
        if (xy.Count < 6) return;
        cell.Shapes.Add(new PcbImportedShape(new PolygonShape { Xy = [.. xy] }, layerName(LayerOf(e))));
    }

    private static void ReadSmd(XElement e, ComponentFootprint footprint, Func<double, long> mm, Func<int, string> layerName)
    {
        if (!Num(e, "x", out double x) || !Num(e, "y", out double y) ||
            !Num(e, "dx", out double dx) || !Num(e, "dy", out double dy)) return;

        string name = Attr(e, "name") ?? "";
        double rot = RotationOf(e);
        int layer = LayerOf(e);
        string layerNameText = layerName(layer);

        Num(e, "roundness", out double roundness);          // per cent of the SHORT side, halved
        long radius = (long)Math.Round(mm(Math.Min(dx, dy)) * roundness / 200.0, MidpointRounding.AwayFromZero);

        LayoutShape shape;
        if (Math.Abs(rot % 180) < 1e-9)
            shape = radius > 0
                ? new RoundedRectShape { X1 = mm(x - dx / 2), Y1 = mm(y - dy / 2), X2 = mm(x + dx / 2), Y2 = mm(y + dy / 2), CornerRadius = radius }
                : new RectShape { X1 = mm(x - dx / 2), Y1 = mm(y - dy / 2), X2 = mm(x + dx / 2), Y2 = mm(y + dy / 2) };
        else if (Math.Abs(Math.Abs(rot % 180) - 90) < 1e-9)
            shape = radius > 0
                ? new RoundedRectShape { X1 = mm(x - dy / 2), Y1 = mm(y - dx / 2), X2 = mm(x + dy / 2), Y2 = mm(y + dx / 2), CornerRadius = radius }
                : new RectShape { X1 = mm(x - dy / 2), Y1 = mm(y - dx / 2), X2 = mm(x + dy / 2), Y2 = mm(y + dx / 2) };
        else
            shape = new PolygonShape { Xy = RotatedBox(mm, x, y, dx, dy, rot) };

        footprint.Cell.Shapes.Add(new PcbImportedShape(shape, layerNameText));

        bool longIsX = dx >= dy;
        footprint.Cell.Pins.Add(new PcbImportedPin(new LayoutPin
        {
            Name = name,
            X = mm(x), Y = mm(y),
            WidthDbu = longIsX ? mm(dy) : mm(dx),
            OutwardDeg = LayoutAngle.Normalize(rot + (longIsX ? 0 : 90)),
        }, layerNameText));
        if (name.Length > 0) footprint.PadNames.Add(name);
    }

    /// <summary>
    /// A through-hole pad: an annulus on the outer copper plus the hole itself.
    ///
    /// <para>The format states no layer on a <c>&lt;pad&gt;</c> because it defines one as spanning all
    /// copper, so the span is taken from <see cref="CopperLayerNumbers"/> — the file's own evidence,
    /// not the numbering.</para>
    /// </summary>
    private static void ReadPad(
        XElement e, ComponentFootprint footprint, Func<double, long> mm, Func<int, string> layerName,
        IReadOnlyList<int> copper, ComponentPart part)
    {
        if (!Num(e, "x", out double x) || !Num(e, "y", out double y)) return;
        Num(e, "drill", out double drill);

        // A file that states no diameter leaves it to the fabrication rules, which are not in this
        // file. The assumed annular ring is reported rather than applied silently.
        double diameter;
        if (!Num(e, "diameter", out diameter) || diameter <= 0)
        {
            diameter = drill + 0.5;
            part.Messages.Add(
                $"Pad \"{Attr(e, "name")}\" states a drill but no pad diameter — this format leaves that " +
                "to the fabrication rules, which a library file does not carry. Imported at the drill " +
                "plus a 0.25 mm annular ring; check it before fabricating.");
        }

        string name = Attr(e, "name") ?? "";
        double rot = RotationOf(e);
        string shapeWord = Attr(e, "shape") ?? "round";

        var layers = copper.Count > 0 ? copper : [0];
        if (copper.Count < 2)
            part.Messages.Add(
                "This library declares only one copper layer, so a through-hole pad's copper was placed " +
                "on that layer alone — the format states no layer on a through-hole pad.");

        foreach (int layer in new[] { layers[0], layers[^1] }.Distinct())
        {
            LayoutShape shape = shapeWord.ToLowerInvariant() switch
            {
                "square" => new RectShape { X1 = mm(x - diameter / 2), Y1 = mm(y - diameter / 2), X2 = mm(x + diameter / 2), Y2 = mm(y + diameter / 2) },
                "long" => new PathShape
                {
                    Xy = LongPadAxis(mm, x, y, diameter, rot),
                    Width = mm(diameter),
                    End = PathEndStyle.Round,
                },
                "octagon" => new PolygonShape { Xy = Octagon(mm, x, y, diameter / 2, rot) },
                _ => new CircleShape { Cx = mm(x), Cy = mm(y), R = mm(diameter / 2) },
            };
            footprint.Cell.Shapes.Add(new PcbImportedShape(shape, layerName(layer)));
        }

        if (drill > 0)
            footprint.Cell.Shapes.Add(new PcbImportedShape(
                new ViaShape { X = mm(x), Y = mm(y), PadSize = mm(drill), DrillSize = mm(drill) },
                PcbReader.DrillLayerName));

        footprint.Cell.Pins.Add(new PcbImportedPin(new LayoutPin
        {
            Name = name,
            X = mm(x), Y = mm(y),
            WidthDbu = mm(diameter),
            OutwardDeg = 0,
        }, layerName(layers[0])));
        if (name.Length > 0) footprint.PadNames.Add(name);
    }

    private static void ReadHole(XElement e, PcbFootprintCell cell, Func<double, long> mm)
    {
        if (!Num(e, "x", out double x) || !Num(e, "y", out double y) || !Num(e, "drill", out double drill)) return;
        cell.Shapes.Add(new PcbImportedShape(
            new ViaShape { X = mm(x), Y = mm(y), PadSize = mm(drill), DrillSize = mm(drill) },
            PcbReader.DrillLayerName));
    }

    // ── Symbols ─────────────────────────────────────────────────────────────────────────────────

    private static ComponentSymbolDrawing ReadSymbol(XElement symbol, ComponentPart part)
    {
        var drawing = new ComponentSymbolDrawing { Name = Attr(symbol, "name") ?? part.Name };

        foreach (var e in symbol.Elements())
        {
            switch (e.Name.LocalName)
            {
                case "wire":
                {
                    if (!Num(e, "x1", out double x1) || !Num(e, "y1", out double y1) ||
                        !Num(e, "x2", out double x2) || !Num(e, "y2", out double y2)) break;
                    if (Num(e, "curve", out double curve) && Math.Abs(curve) > 1e-9)
                    {
                        var pts = new List<double>();
                        foreach (var (px, py) in FlattenBulge(x1, y1, x2, y2, curve)) { pts.Add(Mil(px)); pts.Add(Mil(py)); }
                        drawing.Shapes.Add(new KitSymbolPath(pts, false, false));
                    }
                    else
                    {
                        drawing.Shapes.Add(new KitSymbolLine(Mil(x1), Mil(y1), Mil(x2), Mil(y2)));
                    }
                    break;
                }

                case "rectangle":
                {
                    if (!Num(e, "x1", out double x1) || !Num(e, "y1", out double y1) ||
                        !Num(e, "x2", out double x2) || !Num(e, "y2", out double y2)) break;
                    drawing.Shapes.Add(new KitSymbolRectangle(Mil(x1), Mil(y1), Mil(x2), Mil(y2), true));
                    break;
                }

                case "circle":
                {
                    if (!Num(e, "x", out double x) || !Num(e, "y", out double y) || !Num(e, "radius", out double r)) break;
                    drawing.Shapes.Add(new KitSymbolArc(Mil(x), Mil(y), Mil(r), 0, 360));
                    break;
                }

                case "polygon":
                {
                    var pts = new List<double>();
                    foreach (var v in e.Elements("vertex"))
                        if (Num(v, "x", out double x) && Num(v, "y", out double y)) { pts.Add(Mil(x)); pts.Add(Mil(y)); }
                    if (pts.Count >= 6) drawing.Shapes.Add(new KitSymbolPath(pts, true, true));
                    break;
                }

                case "pin": ReadSymbolPin(e, drawing, part); break;
            }
        }

        return drawing;
    }

    private static void ReadSymbolPin(XElement e, ComponentSymbolDrawing drawing, ComponentPart part)
    {
        if (!Num(e, "x", out double x) || !Num(e, "y", out double y)) return;

        string name = StripBondSuffix(Attr(e, "name") ?? "");
        string lengthWord = Attr(e, "length") ?? "long";
        if (!NamedPinLengths.TryGetValue(lengthWord, out int lengthMil))
        {
            lengthMil = NamedPinLengths["long"];
            part.Messages.Add($"Pin \"{name}\" states an unknown length \"{lengthWord}\" — drawn at the format's longest.");
        }

        int px = (int)Math.Round(Mil(x), MidpointRounding.AwayFromZero);
        int py = (int)Math.Round(Mil(y), MidpointRounding.AwayFromZero);
        drawing.Pins.Add(new ComponentSymbolPin(name, null, px, py));

        // The lead from the terminal to the body. This is the only place the named length is used; a
        // numeric parse yields 0 for all four names and collapses every lead to nothing.
        var (dx, dy) = ComponentSymbolLead.Direction(RotationOf(e));
        if (lengthMil > 0)
            drawing.Shapes.Add(new KitSymbolLine(px, py, px + dx * lengthMil, py + dy * lengthMil));
    }

    // ── Text, geometry and attribute helpers ────────────────────────────────────────────────────

    private static void Describe(ComponentPart part, XElement owner)
    {
        if (owner.Element("description")?.Value is { Length: > 0 } description)
            part.Metadata["Description"] = StripMarkup(description);
    }

    /// <summary>R-PL1-24: <c>&lt;description&gt;</c> holds escaped markup. Stripped to plain text — not
    /// rendered, and the markup is not stored.</summary>
    internal static string StripMarkup(string html)
    {
        var sb = new System.Text.StringBuilder(html.Length);
        bool inTag = false;
        foreach (char c in html)
        {
            if (c == '<') { inTag = true; continue; }
            if (c == '>') { inTag = false; sb.Append(' '); continue; }
            if (!inTag) sb.Append(c);
        }
        return string.Join(' ', sb.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>R-PL1-11: <c>GND@1</c> and <c>GND@2</c> are ONE logical pin bonded to two pads. The
    /// trailing <c>@&lt;digits&gt;</c> belongs to the format and is removed from the name.</summary>
    internal static string StripBondSuffix(string pinName)
    {
        int at = pinName.LastIndexOf('@');
        if (at <= 0) return pinName;
        return pinName[(at + 1)..].All(char.IsDigit) ? pinName[..at] : pinName;
    }

    private static string? Attr(XElement e, string name) => e.Attribute(name)?.Value;

    private static bool Num(XElement e, string name, out double value)
        => double.TryParse(e.Attribute(name)?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static int LayerOf(XElement e)
        => int.TryParse(Attr(e, "layer"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 0;

    /// <summary><c>rot="R90"</c>, or <c>rot="MR90"</c> for a mirrored placement. Degrees CCW.</summary>
    internal static double RotationOf(XElement e)
    {
        string rot = Attr(e, "rot") ?? "";
        int i = 0;
        while (i < rot.Length && !char.IsDigit(rot[i]) && rot[i] != '-') i++;
        return double.TryParse(rot[i..], NumberStyles.Float, CultureInfo.InvariantCulture, out double deg) ? deg : 0;
    }

    private static double Mil(double millimetres) => millimetres / MillimetresPerMil;

    private static long[] RotatedBox(Func<double, long> mm, double cx, double cy, double w, double h, double deg)
    {
        double rad = deg * Math.PI / 180.0;
        double cos = Math.Cos(rad), sin = Math.Sin(rad);
        var pts = new (double X, double Y)[] { (-w / 2, -h / 2), (w / 2, -h / 2), (w / 2, h / 2), (-w / 2, h / 2) };
        var xy = new long[8];
        for (int i = 0; i < 4; i++)
        {
            xy[i * 2] = mm(cx + pts[i].X * cos - pts[i].Y * sin);
            xy[i * 2 + 1] = mm(cy + pts[i].X * sin + pts[i].Y * cos);
        }
        return xy;
    }

    private static long[] Octagon(Func<double, long> mm, double cx, double cy, double r, double deg)
    {
        var xy = new long[16];
        for (int i = 0; i < 8; i++)
        {
            double a = (deg + 22.5 + i * 45) * Math.PI / 180.0;
            xy[i * 2] = mm(cx + r * Math.Cos(a));
            xy[i * 2 + 1] = mm(cy + r * Math.Sin(a));
        }
        return xy;
    }

    /// <summary>An oblong pad's own axis — one diameter long, in the pad's stated direction.</summary>
    private static long[] LongPadAxis(Func<double, long> mm, double x, double y, double diameter, double deg)
    {
        double rad = deg * Math.PI / 180.0;
        double hx = diameter / 2 * Math.Cos(rad), hy = diameter / 2 * Math.Sin(rad);
        return [mm(x - hx), mm(y - hy), mm(x + hx), mm(y + hy)];
    }

    private static long[] CirclePath(Func<double, long> mm, double cx, double cy, double r)
    {
        const int Segments = 48;
        var xy = new long[(Segments + 1) * 2];
        for (int i = 0; i <= Segments; i++)
        {
            double a = 2 * Math.PI * i / Segments;
            xy[i * 2] = mm(cx + r * Math.Cos(a));
            xy[i * 2 + 1] = mm(cy + r * Math.Sin(a));
        }
        return xy;
    }

    /// <summary>
    /// A curved wire, as points. <c>curve</c> is the included angle in degrees, positive
    /// counter-clockwise — the same bulge convention the DXF reader already handles, expressed as an
    /// angle rather than a tangent.
    /// </summary>
    internal static IEnumerable<(double X, double Y)> FlattenBulge(
        double x1, double y1, double x2, double y2, double curveDeg)
    {
        double chord = Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1));
        double half = curveDeg / 2 * Math.PI / 180.0;
        if (chord <= 0 || Math.Abs(Math.Sin(half)) < 1e-12)
        {
            yield return (x1, y1);
            yield return (x2, y2);
            yield break;
        }

        double radius = chord / (2 * Math.Sin(half));
        double mx = (x1 + x2) / 2, my = (y1 + y2) / 2;
        double h = Math.Sqrt(Math.Max(0, radius * radius - chord * chord / 4)) * Math.Sign(Math.Cos(half));
        double ux = -(y2 - y1) / chord, uy = (x2 - x1) / chord;
        double cx = mx + ux * h, cy = my + uy * h;

        double a0 = Math.Atan2(y1 - cy, x1 - cx);
        double sweep = curveDeg * Math.PI / 180.0;
        int segments = Math.Max(2, (int)Math.Ceiling(Math.Abs(curveDeg) / 10.0));
        for (int i = 0; i <= segments; i++)
        {
            double a = a0 + sweep * i / segments;
            yield return (cx + Math.Abs(radius) * Math.Cos(a), cy + Math.Abs(radius) * Math.Sin(a));
        }
    }
}

/// <summary>
/// Which way a pin's lead runs from its stated terminal toward the body.
///
/// <para>Shared by all three readers, which agree on it: angle 0 (or <c>R</c>, or <c>rot="R0"</c>) runs
/// the lead in <b>+x</b> from the terminal, so the body sits to the right and the terminal is the free
/// end (R-PL1-19).</para>
/// </summary>
public static class ComponentSymbolLead
{
    /// <summary>The unit direction for an angle in degrees, snapped to the four cardinals the symbol
    /// formats can state.</summary>
    public static (int Dx, int Dy) Direction(double degrees)
    {
        double d = degrees % 360;
        if (d < 0) d += 360;
        return d switch
        {
            >= 45 and < 135 => (0, 1),
            >= 135 and < 225 => (-1, 0),
            >= 225 and < 315 => (0, -1),
            _ => (1, 0),
        };
    }

    /// <summary>The same direction for the older text format's orientation letter.</summary>
    public static (int Dx, int Dy) FromLetter(string orientation) => orientation switch
    {
        "L" => (-1, 0),
        "U" => (0, 1),
        "D" => (0, -1),
        _ => (1, 0),
    };
}

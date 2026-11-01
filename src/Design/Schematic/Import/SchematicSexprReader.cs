// The `.kicad_sch` reader — brief-schimp-2, overview D2.
//
// Bytes → ForeignSchematic and nothing else: no CellFolder, no Messages, no circuitRF component (R-si2-1). Parsed with
// PcbSexpr, the tokenizer the board and symbol-library readers use; every symbol the file embeds in its `lib_symbols`
// goes through ComponentSymbolSexprReader, because that section is written in exactly the symbol library's grammar.
// No external library is ever looked up — the file is self-contained.
//
// ── TWO CONVENTIONS IN ONE FILE ─────────────────────────────────────────────────────────────────
//
// Everything is millimetres. The SHEET is Y down and converts to mils with no flip; the embedded SYMBOLS are Y up, and
// reach ForeignSymbol in mils still Y up (ForeignSchematic's own convention, R-si1-1).
//
// ── A PLACED SYMBOL'S TRANSFORM (R-si2 §2) ──────────────────────────────────────────────────────
//
// The format turns the symbol counter-clockwise on screen by its angle FIRST, then mirrors it on screen: `(mirror x)`
// flips it top to bottom, `(mirror y)` left to right. ForeignInstance's convention is the other order — mirror across
// the symbol's vertical axis, then turn clockwise — so:
//     no mirror   →  Mirror = false, Rotation = −angle
//     (mirror y)  →  Mirror = true,  Rotation = angle            (a flip after a turn is the flip before the opposite turn)
//     (mirror x)  →  Mirror = true,  Rotation = angle + 180      (a top-to-bottom flip is a left-right flip and a half turn)
// DERIVED, not assumed: against files the authoring editor wrote, of 87 pins on symbols turned a quarter AND mirrored,
// 86 land on a wire under this order and 59 under the other (src/Design/RESOLVED.md); the opamp_units fixture holds it.
//
// ── EPOCHS ──────────────────────────────────────────────────────────────────────────────────────
//
// The file's version is REPORTED and branched on nowhere — the `.kicad_pcb` rule. What differs between epochs is
// read by which tokens are present: a reference on the symbol's own `instances` block, or in the root file's
// `symbol_instances` block keyed by sheet path; `hide` as a bare atom or as `(hide yes)`; "Sheet name" or "Sheetname".
// A hidden power-input pin joins the global net its pin names in every version this format has had to date
// (ForeignPin.ImplicitNet); the report says how many did, and under which version.

using System.Globalization;
using System.Text;
using CircuitRF.Core.Pdk;
using CircuitRF.Design.Layout.Interchange;

namespace CircuitRF.Design.Schematic.Import;

/// <summary>What a schematic reader produced: the design, or why it could not (R-si2-1).</summary>
public sealed record ForeignSchematicRead(ForeignSchematic? Schematic, string? Refusal)
{
    public bool Ok => Schematic is not null;
}

public static class SchematicSexprReader
{
    /// <summary>The format's name, by its extension (overview D7).</summary>
    public const string Format = "kicad_sch";

    /// <summary>
    /// Reads <paramref name="path"/> and every sheet it references (R-si2-1). A child file used by several sheets is read
    /// once (overview D5); a missing child, a sheet that contains itself, or a file that is not this format is a refusal
    /// naming it.
    /// </summary>
    public static ForeignSchematicRead Read(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var state = new State(Path.GetFullPath(path));
        try
        {
            string? refusal = state.Visit(state.RootPath, [], null);
            return refusal is null ? new ForeignSchematicRead(state.Result(), null) : new ForeignSchematicRead(null, refusal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ForeignSchematicRead(null, $"The file could not be read: {ex.Message}");
        }
    }

    /// <summary>Millimetres to mils, exactly — kept to 1e-4 mil, so 2.54 mm is 100 and not 100.00000000000001.</summary>
    internal static double Mils(double mm) => Math.Round(mm / ComponentSymbolSexprReader.MillimetresPerMil, 4);

    /// <summary>
    /// A placed symbol's file orientation in ForeignInstance's terms (see the header): the angle the format turns it
    /// counter-clockwise, and its <c>mirror</c> token (<c>x</c>, <c>y</c> or null).
    /// </summary>
    internal static (int Rotation, bool Mirror) Orientation(double angle, string? mirror)
    {
        int a = (((int)Math.Round(angle / 90.0) * 90) % 360 + 360) % 360;
        return mirror switch
        {
            "y" => (a, true),
            "x" => ((a + 180) % 360, true),
            _ => ((360 - a) % 360, false),
        };
    }

    /// <summary>The members a bus label names: <c>D[0..7]</c> is D0…D7, <c>{SDA SCL}</c> is SDA and SCL, and
    /// <c>I2C{SDA SCL}</c> is I2C.SDA and I2C.SCL. Null when the text names no bus.</summary>
    internal static IReadOnlyList<string>? BusMembers(string text)
    {
        var range = System.Text.RegularExpressions.Regex.Match(text, @"^(.*?)\[(\d+)\.\.(\d+)\]$");
        if (range.Success)
        {
            int a = int.Parse(range.Groups[2].Value, CultureInfo.InvariantCulture), b = int.Parse(range.Groups[3].Value, CultureInfo.InvariantCulture);
            if (Math.Abs(a - b) > 1024) return null;
            int step = a <= b ? 1 : -1;
            var list = new List<string>();
            for (int k = a; ; k += step) { list.Add(range.Groups[1].Value + k.ToString(CultureInfo.InvariantCulture)); if (k == b) break; }
            return list;
        }
        // `~{X}`, `_{x}` and `^{x}` are text markup (an overbar, a subscript, a superscript), never a bus group.
        var group = System.Text.RegularExpressions.Regex.Match(text, @"^([^{}]*)\{([^{}]*)\}$");
        if (group.Success && !group.Groups[1].Value.EndsWith('~') && !group.Groups[1].Value.EndsWith('_') && !group.Groups[1].Value.EndsWith('^'))
        {
            string prefix = group.Groups[1].Value;
            var names = group.Groups[2].Value.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
            return names.Length == 0 ? null : [.. names.Select(n => prefix.Length > 0 ? $"{prefix}.{n}" : n)];
        }
        return null;
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A symbol as one file embeds it, before it is given a key unique across the design.</summary>
    private sealed record LibSymbol(ForeignSymbol Symbol, PowerKind Power, string Canonical);

    private enum PowerKind { None, Global, Local }

    private sealed class State(string rootPath)
    {
        public string RootPath { get; } = rootPath;

        private readonly List<ForeignSheet> _sheets = [];
        private readonly List<ForeignSymbol> _symbols = [];
        private readonly Dictionary<string, string> _sheetOfFile = new(StringComparer.Ordinal);   // full path → sheet name
        private readonly HashSet<string> _reading = new(StringComparer.Ordinal);
        private readonly HashSet<string> _sheetNames = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _symbolKeyOfCanonical = new(StringComparer.Ordinal);
        private readonly SortedDictionary<string, int> _notCarried = new(StringComparer.Ordinal);
        private readonly List<string> _notes = [];
        private readonly SortedSet<string> _versions = new(StringComparer.Ordinal);
        private Dictionary<string, (string Reference, int? Unit)> _oldInstances = new(StringComparer.Ordinal);
        private string _rootUuid = "";
        private int _implicitPins, _flags, _buses, _busLabels, _alternateBodies, _derived, _pinless, _flagGrounds;

        /// <summary>Where the current sheet's left-out power flags had their pin.</summary>
        private readonly List<(double X, double Y)> _flagPins = [];
        private readonly SortedSet<string> _busesAcrossSheets = new(StringComparer.Ordinal);

        public ForeignSchematic Result()
        {
            var notes = new List<string>();
            if (_versions.Count > 0) notes.Add($"Read as .kicad_sch file version {string.Join(", ", _versions)}.");
            if (_implicitPins > 0)
                notes.Add($"{_implicitPins} hidden power-input pin(s) join the net their pin names with no wire, as this format does "
                        + $"in every version to date (file version {string.Join(", ", _versions)}).");
            if (_flags > 0) notes.Add($"{_flags} power flag(s) were left out: they mark a net for the source's rule check and connect nothing."
                                      + (_flagGrounds > 0 ? $" {_flagGrounds} power symbol(s) that touched only a flag went with them." : ""));
            if (_buses > 0 || _busLabels > 0)
                notes.Add(string.Join(" and ", new[] { (_buses, "bus segment(s)"), (_busLabels, "bus label(s)") }
                                               .Where(x => x.Item1 > 0).Select(x => $"{x.Item1} {x.Item2}"))
                        + " were not imported: a bus is drawing, and each of its members connects by its own label.");
            if (_busesAcrossSheets.Count > 0)
                notes.Add($"{_busesAcrossSheets.Count} bus(es) cross a sheet boundary and were not expanded, so their members are not "
                        + $"joined across it: {string.Join(", ", _busesAcrossSheets)}.");
            if (_pinless > 0) notes.Add($"{_pinless} part(s) with no pins (mounting holes, logos and the like) were not imported: they connect nothing.");
            if (_alternateBodies > 0)
                notes.Add($"{_alternateBodies} part(s) are drawn with their alternate body style; each is imported with its standard body.");
            if (_derived > 0) notes.Add($"{_derived} embedded symbol(s) are derived from another; only what each states itself was read.");
            notes.AddRange(_notes);
            foreach (var (kind, count) in _notCarried) notes.Add($"{count} {kind} not imported.");

            bool hierarchical = _sheets.Any(s => s.SheetInstances.Count > 0);
            return new ForeignSchematic(Format, RootPath, _sheets, _symbols, _sheets[0].Name, hierarchical)
            {
                Notes = notes,
                Version = _versions.Count > 0 ? string.Join(", ", _versions) : null,
            };
        }

        /// <summary>Reads one file as one sheet, then its children; returns a refusal or null. <paramref name="sheetPath"/>
        /// is the chain of sheet uuids from the root to this file.</summary>
        public string? Visit(string file, IReadOnlyList<string> sheetPath, string? placedBy)
        {
            if (_sheetOfFile.ContainsKey(file)) return null;
            if (!File.Exists(file))
                return $"The sheet '{placedBy}' refers to {Path.GetFileName(file)}, which is not at {file}.";
            if (!_reading.Add(file))
                return $"The sheet '{placedBy}' refers to {Path.GetFileName(file)}, which contains it: the hierarchy has a loop.";

            var parsed = PcbSexpr.Parse(File.ReadAllText(file));
            if (parsed.Root is not { Tag: "kicad_sch" } root)
                return $"{Path.GetFileName(file)} is not a .kicad_sch file.";
            foreach (var d in parsed.Diagnostics) _notes.Add($"{Path.GetFileName(file)}: {d}");
            if (root.ChildAtom("version") is { } version) _versions.Add(version);

            bool isRoot = sheetPath.Count == 0 && placedBy is null;
            if (isRoot)
            {
                _rootUuid = root.ChildAtom("uuid") ?? "";
                _oldInstances = OldInstances(root);
            }

            string name = UniqueSheetName(Path.GetFileNameWithoutExtension(file));
            _sheetOfFile[file] = name;
            int index = _sheets.Count;
            _sheets.Add(new ForeignSheet(name));   // placeholder: the root stays first, children follow in reading order

            var libs = LibSymbols(root);
            var instances = new List<ForeignInstance>();
            var wires = new List<ForeignWire>();
            var junctions = new List<ForeignJunction>();
            var labels = new List<ForeignLabel>();
            var noConnects = new List<ForeignNoConnect>();
            var texts = new List<ForeignText>();
            var graphics = new List<ForeignGraphic>();
            var busSegments = new List<((double X, double Y) A, (double X, double Y) B)>();
            var children = new List<(PcbNode Node, string ChildFile, string SheetName, string Uuid)>();

            foreach (var node in root.Nodes)
            {
                switch (node.Tag)
                {
                    case "symbol":
                        if (node.Child("lib_id") is null) break;
                        PlaceSymbol(node, libs, sheetPath, instances, labels);
                        break;
                    case "wire":
                        if (Points(node) is { Count: >= 2 } wp) wires.Add(new ForeignWire(wp));
                        break;
                    case "bus":
                        if (Points(node) is { Count: >= 2 } bp)
                            for (int i = 0; i + 1 < bp.Count; i++) busSegments.Add((bp[i], bp[i + 1]));
                        _buses++;
                        break;
                    case "bus_entry":
                        break;   // the bus side of it is drawing; the wire side connects by its label
                    case "junction":
                        if (At(node) is { } j) junctions.Add(new ForeignJunction(j.X, j.Y));
                        break;
                    case "no_connect":
                        if (At(node) is { } nc) noConnects.Add(new ForeignNoConnect(nc.X, nc.Y));
                        break;
                    case "label" or "global_label" or "hierarchical_label":
                        if (At(node) is { } lp && node.Atom(0) is { Length: > 0 } text)
                            labels.Add(new ForeignLabel(node.Tag switch
                            {
                                "global_label" => ForeignLabelKind.Global,
                                "hierarchical_label" => ForeignLabelKind.Hierarchical,
                                _ => ForeignLabelKind.Local,
                            }, text, lp.X, lp.Y));
                        break;
                    case "text":
                        if (At(node) is { } tp && node.Atom(0) is { Length: > 0 } t)
                            texts.Add(SheetText(node, t, tp));
                        break;
                    case "text_box":
                        if (XyOf(node.Child("at") ?? node.Child("start")) is { } bp2 && node.Atom(0) is { Length: > 0 } bt)
                            texts.Add(new ForeignText(bt, bp2.X, bp2.Y, TextRotation(node), TextSize(node)));
                        break;
                    case "polyline":
                        if (Points(node) is { Count: >= 2 } pp)
                        {
                            bool closed = pp.Count >= 3 && pp[0] == pp[^1];
                            graphics.Add(new ForeignGraphic(closed ? pp[..^1] : pp, closed));
                        }
                        break;
                    case "rectangle":
                        if (XyOf(node.Child("start")) is { } rs && XyOf(node.Child("end")) is { } re)
                            graphics.Add(new ForeignGraphic([rs, (re.X, rs.Y), re, (rs.X, re.Y)], Closed: true));
                        break;
                    case "sheet":
                        if (SheetChild(node, file) is { } child) children.Add(child);
                        break;
                    case "circle": NotCarried("circle(s) drawn on the sheet were"); break;
                    case "arc": NotCarried("arc(s) drawn on the sheet were"); break;
                    case "bezier": NotCarried("curve(s) drawn on the sheet were"); break;
                    case "image": NotCarried("image(s) were"); break;
                    case "table": NotCarried("table(s) were"); break;
                    case "rule_area": NotCarried("rule area(s) were"); break;
                    case "netclass_flag" or "directive_label": NotCarried("net-class directive(s) were"); break;
                    case "group": NotCarried("group(s) were (their members were imported)"); break;
                }
            }

            // A power symbol that reaches nothing but a left-out flag — the ground a flag is stood on, directly or by a
            // short wire — joins nothing once the flag is gone, and would come in as a lone symbol with an unconnected
            // marker. It goes, with any wire that only joined the two.
            if (_flagPins.Count > 0)
            {
                var pinPoints = new List<(double X, double Y)>();
                foreach (var inst in instances)
                    if (libs.Values.FirstOrDefault(l => l.Symbol.Key == inst.SymbolKey)?.Symbol.Unit(inst.Unit) is { } iu)
                        foreach (var ip in iu.Pins)
                        {
                            var (wx, wy) = SchematicGeometry.LocalToWorld((float)ip.X, (float)-ip.Y, inst.X, inst.Y, RotationOf(inst.Rotation), inst.Mirror);
                            pinPoints.Add((wx, wy));
                        }
                static bool Near((double X, double Y) a, (double X, double Y) b) => Math.Abs(a.X - b.X) < 0.01 && Math.Abs(a.Y - b.Y) < 0.01;
                static bool OnWire(ForeignWire w, (double X, double Y) p) => Enumerable.Range(0, w.Points.Count - 1).Any(i =>
                    SchematicGeometry.PointOnSegment(p.X, p.Y, w.Points[i].X, w.Points[i].Y, w.Points[i + 1].X, w.Points[i + 1].Y, 0.01));

                var gone = new HashSet<ForeignWire>();
                foreach (var label in labels.Where(l => l.Kind == ForeignLabelKind.Power).ToList())
                {
                    // The wires reachable from the label, through shared points.
                    var reach = new List<ForeignWire>();
                    var points = new List<(double X, double Y)> { (label.X, label.Y) };
                    for (bool grew = true; grew;)
                    {
                        grew = false;
                        foreach (var w in wires)
                            if (!reach.Contains(w) && points.Any(p => OnWire(w, p) || w.Points.Any(q => Near(p, q))))
                            { reach.Add(w); points.AddRange(w.Points); grew = true; }
                    }
                    bool onFlag = points.Any(p => _flagPins.Any(f => Near(p, f)));
                    bool joinsSomething = points.Any(p => pinPoints.Any(q => Near(p, q)))
                        || labels.Any(o => !ReferenceEquals(o, label) && (points.Any(p => Near(p, (o.X, o.Y))) || reach.Any(w => OnWire(w, (o.X, o.Y)))))
                        || reach.Any(w => pinPoints.Any(q => OnWire(w, q)));
                    if (!onFlag || joinsSomething) continue;
                    labels.Remove(label);
                    foreach (var w in reach) gone.Add(w);
                    _flagGrounds++;
                }
                wires.RemoveAll(gone.Contains);
                _flagPins.Clear();
            }

            // A label on a bus names the bus, not a net: it goes with the bus.
            int before = labels.Count;
            labels.RemoveAll(l => busSegments.Any(s => SchematicGeometry.PointOnSegment(l.X, l.Y, s.A.X, s.A.Y, s.B.X, s.B.Y, 0.01))
                               || l.Kind == ForeignLabelKind.Hierarchical && BusMembers(l.Text) is not null && Track(l.Text));
            _busLabels += before - labels.Count;

            // ── the children ─────────────────────────────────────────────────────────────────────
            var sheetInstances = new List<ForeignSheetInstance>();
            foreach (var (node, childFile, sheetName, uuid) in children)
            {
                if (Visit(childFile, [.. sheetPath, uuid], sheetName) is { } refusal) return refusal;
                var pins = new List<ForeignSheetPin>();
                foreach (var pin in node.Children("pin"))
                    if (pin.Atom(0) is { Length: > 0 } pinName && At(pin) is { } at)
                    {
                        if (BusMembers(pinName) is not null) { Track(pinName); continue; }
                        pins.Add(new ForeignSheetPin(pinName, at.X, at.Y));
                    }
                var origin = At(node) ?? (0, 0);
                var size = node.Child("size");
                sheetInstances.Add(new ForeignSheetInstance(sheetName, _sheetOfFile[childFile], origin.X, origin.Y,
                    Mils(size?.Num(0) ?? 0), Mils(size?.Num(1) ?? 0), pins));
            }

            _reading.Remove(file);
            _sheets[index] = new ForeignSheet(name)
            {
                Instances = instances, Wires = wires, Junctions = junctions, Labels = labels, NoConnects = noConnects,
                SheetInstances = sheetInstances, Texts = texts, Graphics = graphics,
            };
            return null;
        }

        private bool Track(string busName)
        {
            _busesAcrossSheets.Add(busName);
            return true;
        }

        private void NotCarried(string kind) => _notCarried[kind] = _notCarried.GetValueOrDefault(kind) + 1;

        private string UniqueSheetName(string stem)
        {
            string n = stem.Length > 0 ? stem : "sheet";
            for (int k = 2; !_sheetNames.Add(n); k++) n = $"{stem}_{k}";
            return n;
        }

        // ── the embedded symbols ────────────────────────────────────────────────────────────────

        /// <summary>This file's <c>lib_symbols</c>, by the name its placed symbols use. Two files embedding the same name
        /// with the same definition share one ForeignSymbol; a different definition under the same name gets its own key.</summary>
        private Dictionary<string, LibSymbol> LibSymbols(PcbNode root)
        {
            var map = new Dictionary<string, LibSymbol>(StringComparer.Ordinal);
            foreach (var node in root.Child("lib_symbols")?.Children("symbol") ?? [])
            {
                if (node.Atom(0) is not { Length: > 0 } libName) continue;
                var power = node.Child("power") is { } p ? (p.HasAtom("local") ? PowerKind.Local : PowerKind.Global) : PowerKind.None;
                string canonical = Canonical(node);
                if (!_symbolKeyOfCanonical.TryGetValue(canonical, out var key))
                {
                    key = libName;
                    for (int k = 2; _symbols.Any(s => s.Key == key); k++) key = $"{libName}#{k}";
                    _symbolKeyOfCanonical[canonical] = key;
                    var symbol = ReadLibSymbol(node, key);
                    if (power == PowerKind.None) _symbols.Add(symbol);
                    map[libName] = new LibSymbol(symbol, power, canonical);
                    if (node.Child("extends") is not null) _derived++;
                }
                else map[libName] = new LibSymbol(_symbols.FirstOrDefault(s => s.Key == key) ?? ReadLibSymbol(node, key), power, canonical);
            }
            return map;
        }

        /// <summary>One embedded symbol, unit by unit: pins read here (they carry what the neutral model needs — type,
        /// hidden, length, side), drawing through the symbol library's own reader.</summary>
        private static ForeignSymbol ReadLibSymbol(PcbNode node, string key)
        {
            string full = node.Atom(0) ?? key;
            int colon = full.IndexOf(':');
            string display = colon >= 0 ? full[(colon + 1)..] : full;
            string library = colon >= 0 ? full[..colon] : "";

            var part = ComponentSymbolSexprReader.ReadSymbol(node);
            var unitNumbers = new SortedSet<int>();
            foreach (var sub in node.Children("symbol"))
            {
                var (u, _) = ComponentSymbolSexprReader.UnitAndStyleOf(sub.Atom(0) ?? "", part.Name);
                if (u > 0) unitNumbers.Add(u);
            }
            if (unitNumbers.Count == 0) unitNumbers.Add(1);

            var units = new List<ForeignSymbolUnit>();
            foreach (int u in unitNumbers)
            {
                var pins = new List<ForeignPin>();
                foreach (var sub in node.Children("symbol"))
                {
                    var (su, style) = ComponentSymbolSexprReader.UnitAndStyleOf(sub.Atom(0) ?? "", part.Name);
                    if (su != 0 && su != u || style > 1) continue;
                    foreach (var pin in sub.Children("pin")) if (PinOf(pin) is { } fp) pins.Add(fp);
                }
                foreach (var pin in node.Children("pin")) if (PinOf(pin) is { } fp) pins.Add(fp);
                var drawing = ComponentSymbolSexprReader.ReadUnit(node, u, part);
                units.Add(new ForeignSymbolUnit(u, pins, drawing.Shapes));
            }
            // (pin_names (offset …) hide) in one epoch, (pin_names (hide yes)) in a later one.
            var pinNames = node.Child("pin_names");
            bool namesHidden = pinNames is not null
                && (pinNames.HasAtom("hide") || pinNames.Child("hide") is { } h && h.Atom(0) is null or "yes");
            return new ForeignSymbol(key, display, library, units) { PinNamesHidden = namesHidden };
        }

        private static ForeignPin? PinOf(PcbNode pin)
        {
            if (pin.Child("at") is not { } at) return null;
            string Clean(string? s) => s is null or "~" ? "" : s;
            string name = Clean(pin.Child("name")?.Atom(0));
            string number = Clean(pin.Child("number")?.Atom(0));
            var type = pin.Atom(0) switch
            {
                "input" => ForeignPinType.Input,
                "output" => ForeignPinType.Output,
                "bidirectional" => ForeignPinType.Bidirectional,
                "tri_state" => ForeignPinType.TriState,
                "passive" => ForeignPinType.Passive,
                "power_in" => ForeignPinType.PowerIn,
                "power_out" => ForeignPinType.PowerOut,
                "open_collector" => ForeignPinType.OpenCollector,
                "open_emitter" => ForeignPinType.OpenEmitter,
                "no_connect" => ForeignPinType.NoConnect,
                _ => ForeignPinType.Unspecified,
            };
            bool hidden = ComponentSymbolSexprReader.IsHidden(pin);
            var side = ((((int)Math.Round((at.Num(2) ?? 0) / 90.0)) % 4 + 4) % 4) switch
            {
                1 => ForeignPinDirection.Up,
                2 => ForeignPinDirection.Left,
                3 => ForeignPinDirection.Down,
                _ => ForeignPinDirection.Right,
            };
            return new ForeignPin(number, name, type, Mils(at.Num(0) ?? 0), Mils(at.Num(1) ?? 0), side,
                                  Mils(pin.ChildNum("length") ?? 0), hidden,
                                  hidden && type == ForeignPinType.PowerIn && name.Length > 0 ? name : null);
        }

        // ── placed symbols ──────────────────────────────────────────────────────────────────────

        private void PlaceSymbol(PcbNode node, Dictionary<string, LibSymbol> libs, IReadOnlyList<string> sheetPath,
                                 List<ForeignInstance> instances, List<ForeignLabel> labels)
        {
            string libId = node.ChildAtom("lib_name") ?? node.ChildAtom("lib_id") ?? "";
            if (!libs.TryGetValue(libId, out var lib) && !libs.TryGetValue(node.ChildAtom("lib_id") ?? "", out lib))
            {
                _notes.Add($"A placed symbol names '{libId}', which the file does not embed; it was not imported.");
                return;
            }
            var at = node.Child("at");
            double x = Mils(at?.Num(0) ?? 0), y = Mils(at?.Num(1) ?? 0);
            var (rotation, mirror) = Orientation(at?.Num(2) ?? 0, node.ChildAtom("mirror"));
            int unit = (int)(node.ChildNum("unit") ?? 1);
            if ((node.ChildNum("convert") ?? node.ChildNum("body_style") ?? 1) > 1) _alternateBodies++;

            var properties = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var p in node.Children("property"))
                if (p.Atom(0) is { Length: > 0 } key) properties[key] = p.Atom(1) ?? "";
            string? value = properties.GetValueOrDefault("Value");

            if (lib.Power != PowerKind.None)
            {
                // A power symbol is a label: its Value names the net (R-si2 §2). One whose pin is not a power input — a flag
                // for the source's rule check — names nothing.
                var u = lib.Symbol.Unit(unit) ?? lib.Symbol.Units.FirstOrDefault();
                var pin = u?.Pins.FirstOrDefault();
                if (pin is null || pin.ElectricalType != ForeignPinType.PowerIn)
                {
                    _flags++;
                    if (pin is not null)
                    {
                        var (fx, fy) = SchematicGeometry.LocalToWorld((float)pin.X, (float)-pin.Y, x, y, RotationOf(rotation), mirror);
                        _flagPins.Add((Math.Round(fx, 4), Math.Round(fy, 4)));
                    }
                    return;
                }
                string net = value is { Length: > 0 } ? value : pin.Name;
                var (px, py) = SchematicGeometry.LocalToWorld((float)pin.X, (float)-pin.Y, x, y, RotationOf(rotation), mirror);
                labels.Add(new ForeignLabel(lib.Power == PowerKind.Local ? ForeignLabelKind.Local : ForeignLabelKind.Power,
                                            net, Math.Round(px, 4), Math.Round(py, 4)));
                return;
            }

            var (reference, instanceUnit) = ReferenceOf(node, sheetPath, properties.GetValueOrDefault("Reference") ?? "?");
            if (instanceUnit is int iu && node.Child("unit") is null) unit = iu;
            if (lib.Symbol.Unit(unit) is not { Pins.Count: > 0 }) { _pinless++; return; }   // a mounting hole, a logo
            _implicitPins += lib.Symbol.Unit(unit)!.Pins.Count(p => p.ImplicitNet is not null);
            instances.Add(new ForeignInstance(reference, unit, lib.Symbol.Key, x, y, rotation, mirror, value, properties)
            {
                Fields = FieldsOf(node, rotation, mirror, reference, value),
            });
        }

        /// <summary>A stroke-font character's average advance, as a fraction of the font's width — only how far a justified
        /// field's text reaches from its anchor depends on it.</summary>
        private const double CharAdvance = 0.8;

        /// <summary>
        /// A placed symbol's <c>Reference</c> and <c>Value</c> where the sheet shows them (schematic-import.md §4, labels). A property's
        /// <c>at</c> is its anchor on the sheet; its justification and its angle are in the symbol's own frame, so both turn
        /// and mirror with the symbol, and a symbol turned a quarter shows a horizontal field upright
        /// (src/Design/RESOLVED.md). The anchor is turned into the line's middle across the text and
        /// its justified end — or middle — along it.
        /// </summary>
        private static List<ForeignField> FieldsOf(PcbNode node, int rotation, bool mirror, string reference, string? value)
        {
            var fields = new List<ForeignField>();
            foreach (var p in node.Children("property"))
            {
                if (p.Atom(0) is not ("Reference" or "Value") || p.Child("at") is not { } at) continue;
                string name = p.Atom(0)!;
                double ax = Mils(at.Num(0) ?? 0), ay = Mils(at.Num(1) ?? 0);
                int turns = (((int)Math.Round((at.Num(2) ?? 0) / 90.0)) % 4 + 4) % 4;
                var effects = p.Child("effects");
                bool hidden = ComponentSymbolSexprReader.IsHidden(p) || effects is not null && ComponentSymbolSexprReader.IsHidden(effects);
                var size = effects?.Child("font")?.Child("size");
                double height = Mils(size?.Num(0) ?? 1.27), width = Mils(size?.Num(1) ?? 1.27);
                string text = name == "Reference" ? reference : value ?? p.Atom(1) ?? "";
                double length = text.Length * width * CharAdvance;

                // From the anchor to the text's centre, in the text's own frame (Y down), then turned by the text's angle
                // (counter-clockwise on screen) and by the symbol's own transform.
                var justify = effects?.Child("justify");
                double dx = justify?.HasAtom("left") == true ? length / 2 : justify?.HasAtom("right") == true ? -length / 2 : 0;
                double dy = justify?.HasAtom("top") == true ? height / 2 : justify?.HasAtom("bottom") == true ? -height / 2 : 0;
                for (int k = 0; k < turns; k++) (dx, dy) = (dy, -dx);
                var (tx, ty) = SchematicGeometry.LocalToWorld((float)dx, (float)dy, 0, 0, RotationOf(rotation), mirror);

                bool vertical = (turns % 2 == 1) != (rotation % 180 != 0);
                var along = vertical || Math.Abs(tx) < 1e-6 ? ForeignJustify.Center : tx > 0 ? ForeignJustify.Left : ForeignJustify.Right;
                double fx = along == ForeignJustify.Center ? ax + tx : ax;
                fields.Add(new ForeignField(name, text, Math.Round(fx, 4), Math.Round(ay + ty, 4), along, vertical, hidden));
            }
            return fields;
        }

        /// <summary>
        /// A placed symbol's reference on the sheet path it was reached by: the symbol's own <c>instances</c> block, else
        /// the root file's <c>symbol_instances</c> block keyed by path, else its Reference property. A child sheet used
        /// several times is read once, by the first path that reaches it.
        /// </summary>
        private (string Reference, int? Unit) ReferenceOf(PcbNode node, IReadOnlyList<string> sheetPath, string property)
        {
            string here = "/" + string.Join("/", new[] { _rootUuid }.Concat(sheetPath));
            var paths = node.Child("instances")?.Children("project").SelectMany(p => p.Children("path")).ToList() ?? [];
            var match = paths.FirstOrDefault(p => p.Atom(0) == here) ?? (paths.Count == 1 ? paths[0] : null);
            if (match?.ChildAtom("reference") is { Length: > 0 } r)
                return (r, match.ChildNum("unit") is { } u ? (int)u : null);

            if (node.ChildAtom("uuid") is { } uuid)
            {
                string tail = string.Concat(sheetPath.Select(s => "/" + s)) + "/" + uuid;
                if (_oldInstances.TryGetValue(tail, out var old) || _oldInstances.TryGetValue("/" + _rootUuid + tail, out old))
                    return old;
            }
            return (property, null);
        }

        private static Dictionary<string, (string Reference, int? Unit)> OldInstances(PcbNode root)
        {
            var map = new Dictionary<string, (string, int?)>(StringComparer.Ordinal);
            foreach (var path in root.Child("symbol_instances")?.Children("path") ?? [])
                if (path.Atom(0) is { } key && path.ChildAtom("reference") is { Length: > 0 } r)
                    map[key] = (r, path.ChildNum("unit") is { } u ? (int)u : null);
            return map;
        }

        // ── child sheets ────────────────────────────────────────────────────────────────────────

        private (PcbNode, string, string, string)? SheetChild(PcbNode node, string parentFile)
        {
            string? Prop(params string[] names) => node.Children("property")
                .FirstOrDefault(p => names.Contains(p.Atom(0), StringComparer.Ordinal))?.Atom(1);
            string? file = Prop("Sheetfile", "Sheet file");
            string sheetName = Prop("Sheetname", "Sheet name") ?? Path.GetFileNameWithoutExtension(file ?? "sheet");
            if (file is not { Length: > 0 })
            {
                _notes.Add($"The sheet '{sheetName}' names no file; it was not imported.");
                return null;
            }
            string childPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(parentFile) ?? "", file.Replace('\\', Path.DirectorySeparatorChar)));
            return (node, childPath, sheetName, node.ChildAtom("uuid") ?? "");
        }
    }

    // ── small readers ───────────────────────────────────────────────────────────────────────────

    private static SymbolRotation RotationOf(int degrees) => degrees switch
    {
        90 => SymbolRotation.R90,
        180 => SymbolRotation.R180,
        270 => SymbolRotation.R270,
        _ => SymbolRotation.R0,
    };

    private static (double X, double Y)? At(PcbNode node) => XyOf(node.Child("at"));

    private static (double X, double Y)? XyOf(PcbNode? node)
        => node?.Num(0) is { } x && node.Num(1) is { } y ? (Mils(x), Mils(y)) : null;

    private static List<(double X, double Y)> Points(PcbNode node)
    {
        var pts = new List<(double X, double Y)>();
        foreach (var xy in node.Child("pts")?.Children("xy") ?? [])
            if (xy.Num(0) is { } x && xy.Num(1) is { } y) pts.Add((Mils(x), Mils(y)));
        return pts;
    }

    /// <summary>
    /// Free text on the sheet. Its angle is a SPIN, not a turn: the format draws text at 180 degrees left to right and
    /// at 270 bottom to top, so it is always readable, and only its stated justification says which end is anchored.
    /// Taken as a turn, a label at 180 came in upside down.
    /// </summary>
    private static ForeignText SheetText(PcbNode node, string text, (double X, double Y) at)
    {
        double angle = node.Child("at")?.Num(2) ?? 0;
        int quarter = (((int)Math.Round(angle / 90.0)) % 4 + 4) % 4;
        int rotation = Orientation(quarter % 2 * 90, null).Rotation;
        var justify = node.Child("effects")?.Child("justify");
        CircuitRF.Design.Symbol.SymbolTextAlign? align = justify is null ? null
            : justify.HasAtom("left") ? CircuitRF.Design.Symbol.SymbolTextAlign.Left
            : justify.HasAtom("right") ? CircuitRF.Design.Symbol.SymbolTextAlign.Right
            : CircuitRF.Design.Symbol.SymbolTextAlign.Center;
        CircuitRF.Design.Symbol.SymbolTextVAlign? valign = justify is null ? null
            : justify.HasAtom("top") ? CircuitRF.Design.Symbol.SymbolTextVAlign.Top
            : justify.HasAtom("bottom") ? CircuitRF.Design.Symbol.SymbolTextVAlign.Bottom
            : CircuitRF.Design.Symbol.SymbolTextVAlign.Middle;
        return new ForeignText(text, at.X, at.Y, rotation, TextSize(node), align, valign);
    }

    /// <summary>Text turns counter-clockwise in this format and clockwise in circuitRF's.</summary>
    private static int TextRotation(PcbNode node)
        => Orientation(node.Child("at")?.Num(2) ?? 0, null).Rotation;

    /// <summary>The text's height in mils; 50 (1.27 mm, the format's default) when it states none.</summary>
    private static double TextSize(PcbNode node)
        => node.Child("effects")?.Child("font")?.ChildNum("size") is { } h ? Mils(h) : 50;

    /// <summary>A node written back as text, for telling two embedded definitions of one name apart.</summary>
    private static string Canonical(PcbNode node)
    {
        var sb = new StringBuilder();
        void Write(PcbNode n)
        {
            sb.Append('(').Append(n.Tag);
            foreach (var item in n.Items)
            {
                sb.Append(' ');
                if (item is PcbNode c) Write(c);
                else sb.Append('"').Append(((string)item).Replace("\"", "\\\"")).Append('"');
            }
            sb.Append(')');
        }
        Write(node);
        return sb.ToString();
    }
}

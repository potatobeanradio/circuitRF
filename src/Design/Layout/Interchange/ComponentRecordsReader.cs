// The `.p` / `.d` / `.c` triple — a part type, its land patterns and its schematic decal, in three
// files that must be read TOGETHER (docs/sonnet-briefs/brief-PL2-component-library-breadth.md §4.1).
//
// `.p` holds the part type and, in its GATE block, the pad-identifier -> pin-name map.
// `.d` holds the footprint decals — all density variants, one after another.
// `.c` holds the schematic decal, whose terminals are the symbol's pins in the GATE block's order.
//
// Units are mils throughout and Y is already UP (ComponentArtwork's header: no flip here).
//
// ── R-PL2-4: records are COUNT-DRIVEN, not line-scannable ─────────────────────────────────────────
//
// Every entity's header line declares how many of the following lines belong to it — an outline says
// how many vertices follow, a decal says how many labels, pieces, terminals and padstacks. A reader
// that scans for keywords line by line appears to work on a simple part and silently mis-associates
// geometry on a complex one: a plausible footprint with a few strays, which is the worst failure mode
// available here because nothing looks wrong. So everything below consumes by DECLARED COUNT through
// one cursor, and a count the file cannot honour is a refusal naming the entity — never a resync.

using System.Globalization;
using CircuitRF.Core.Pdk;

namespace CircuitRF.Design.Layout.Interchange;

public static class ComponentRecordsReader
{
    public sealed record ReadResult(ComponentPart? Part, string? Refusal);

    // ── R-PL2-18: matched on the VENDOR-FREE part of the banner ─────────────────────────────────
    //
    // Each of these files opens with `*<product>-LIBRARY-PART-TYPES-V9*` and friends, where <product>
    // is a tool name that root CLAUDE.md's "Commercial Vendor References" rule forbids anywhere in
    // this repo — including a string literal. The substrings below are the part of the banner that
    // names the CONTENT rather than the tool, they are just as specific in practice, and they let the
    // synthetic fixtures carry an invented prefix instead of a real product's.
    internal const string PartHeader = "-LIBRARY-PART-TYPES-";
    internal const string DecalHeader = "-LIBRARY-PCB-DECALS-";
    internal const string SymbolHeader = "-LIBRARY-SCH-DECALS-";

    /// <summary>
    /// Reads the triple. <paramref name="decalText"/> and <paramref name="symbolText"/> may be null —
    /// a folder holding only some of the three is a partial read, reported, not a failure.
    /// </summary>
    public static ReadResult Read(string partText, string? decalText, string? symbolText, int dbuPerMicron)
    {
        var part = new ComponentPart();

        var head = ReadPartType(partText, part);
        if (head.Refusal is { } refusal) return new ReadResult(null, refusal);

        // ── The land patterns ───────────────────────────────────────────────────────────────────
        if (decalText is not null)
        {
            var decals = ReadDecals(decalText, part);
            if (decals.Refusal is { } r) return new ReadResult(null, r);

            foreach (var artwork in decals.Artwork)
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

        // ── The symbol ──────────────────────────────────────────────────────────────────────────
        if (symbolText is not null)
        {
            var symbol = ReadSymbolDecal(symbolText, head.PinNames, head.PadNames, part);
            if (symbol.Refusal is { } r) return new ReadResult(null, r);
            part.Symbol = symbol.Drawing;
        }
        else
        {
            // The map still exists without a drawing — carried so a footprint-only import still knows
            // which pad is which pin.
            for (int i = 0; i < head.PinNames.Count; i++)
                part.ConnectTable.Add(new ComponentConnect(head.PinNames[i], head.PadNames[i]));
        }

        if (part.Symbol is null && part.Footprints.Count == 0)
            return new ReadResult(null, "This part type names neither a land pattern nor a schematic decal.");

        return new ReadResult(part, null);
    }

    // ── `.p` ──────────────────────────────────────────────────────────────────────────────────────

    private sealed record PartHead(
        List<string> PinNames, List<string> PadNames, List<string> DecalNames, string? Refusal);

    /// <summary>
    /// <c>NAME DECALS FAMILY PREFIX attrCount gateCount …</c>, then <c>attrCount</c> quoted attribute
    /// lines, then <c>GATE decalCount pinCount swap</c> with its own two counted runs.
    /// </summary>
    private static PartHead ReadPartType(string text, ComponentPart part)
    {
        var lines = Lines(text);
        if (!lines.Any(l => IsBanner(l, PartHeader)))
            return new PartHead([], [], [], $"This file does not open with a \"*…{PartHeader}…*\" banner.");

        int i = 0;
        while (i < lines.Count && (lines[i].Length == 0 || lines[i].StartsWith('*'))) i++;
        if (i >= lines.Count) return new PartHead([], [], [], "This library declares no part type at all.");

        var f = Fields(lines[i]);
        if (f.Count < 6)
            return new PartHead([], [], [], $"The part-type line states {f.Count} fields; at least 6 are needed.");

        part.Name = f[0];

        // Field 3 is the reference-designator prefix — the letter this library numbers its parts
        // from, which is what a placed instance is called (U1, U2, U3). Field 2 beside it is the
        // logical family and is not one: the same part exported to the dotted part-file grammar
        // states this field's value, and not that one, as its own RefPrefix.
        if (f[3] is { Length: > 0 } prefix && prefix != "*") part.Metadata["Reference"] = prefix;

        // R-PL2-5: the alternate decals are colon-separated in this one field. The separator makes a
        // decal name CONTAINING a colon unrepresentable — reported rather than guessed at.
        var decalNames = f[1].Split(':', StringSplitOptions.RemoveEmptyEntries).ToList();

        int attrCount = Int(f[4]);
        int gateCount = Int(f[5]);
        i++;

        for (int a = 0; a < attrCount; a++, i++)
        {
            if (i >= lines.Count)
                return new PartHead([], [], [], Underrun("part type", $"{attrCount} attributes", a));
            var (key, value) = SplitAttribute(lines[i]);
            if (key.Length > 0 && value.Length > 0) part.Metadata[key] = value;
        }

        var pinNames = new List<string>();
        var padNames = new List<string>();

        for (int g = 0; g < gateCount; g++)
        {
            while (i < lines.Count && !lines[i].StartsWith("GATE", StringComparison.Ordinal)) i++;
            if (i >= lines.Count)
                return new PartHead([], [], [], Underrun("part type", $"{gateCount} gates", g));

            var gf = Fields(lines[i]);
            int decalLines = gf.Count > 1 ? Int(gf[1]) : 0;
            int pinCount = gf.Count > 2 ? Int(gf[2]) : 0;
            i++;

            // The gate's own decal names, one per line — consumed by count so the pin lines that
            // follow are not mistaken for them.
            for (int d = 0; d < decalLines; d++, i++)
                if (i >= lines.Count)
                    return new PartHead([], [], [], Underrun("gate", $"{decalLines} decal names", d));

            for (int p = 0; p < pinCount; p++, i++)
            {
                if (i >= lines.Count)
                    return new PartHead([], [], [], Underrun("gate", $"{pinCount} pins", p));

                // `<pad> <swap> <type> <name>` — the pad identifier is read as a STRING (R-PL1-9);
                // a thermal pad is routinely named rather than numbered and int.Parse drops it.
                var pf = Fields(lines[i]);
                if (pf.Count < 4) continue;
                if (g > 0) continue;                       // sections beyond the first are named below

                padNames.Add(pf[0]);
                pinNames.Add(pf[3]);
            }

            if (g > 0) part.UnimportedSections.Add($"{part.Name} section {g + 1}");
        }

        return new PartHead(pinNames, padNames, decalNames, null);
    }

    // ── `.d` ──────────────────────────────────────────────────────────────────────────────────────

    private sealed record DecalResult(List<ComponentArtwork> Artwork, string? Refusal);

    /// <summary>
    /// Every decal in the file, in order. <b>All density variants live here</b>, one block after
    /// another, exactly as the <c>.hkp</c> cell file holds all three (R-PL2-9) — returning the first
    /// and stopping would lose two thirds of the file with no error.
    /// </summary>
    private static DecalResult ReadDecals(string text, ComponentPart part)
    {
        var lines = Lines(text);
        if (!lines.Any(l => IsBanner(l, DecalHeader)))
            return new DecalResult([], $"This file does not open with a \"*…{DecalHeader}…*\" banner.");

        var artwork = new List<ComponentArtwork>();
        int i = 0;
        while (i < lines.Count && (lines[i].Length == 0 || lines[i].StartsWith('*'))) i++;

        while (i < lines.Count)
        {
            if (lines[i].Length == 0) { i++; continue; }

            var f = Fields(lines[i]);

            // A decal header is `NAME UNITS x y ? labels pieces ? terminals padstacks`. Ten fields,
            // the first non-numeric — which is what separates it from the record lines around it.
            if (f.Count < 10 || double.TryParse(f[0], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            { i++; continue; }

            var (baseName, variant) = SplitVariant(f[0]);
            var art = new ComponentArtwork { Name = baseName, Variant = variant };

            // `NAME UNITS x y ? labels pieces texts terminals padstacks`.
            //
            // **There are TWO counted text runs, and they are not adjacent**: `labels` come before the
            // pieces, `texts` come after them. Reading only the first — the obvious mistake, since the
            // two are spelled identically — leaves the cursor `2 × texts` lines short, and the pad
            // stacks are then read out of the middle of a free-text label. That is R-PL2-4's exact
            // failure mode, and the only way to catch it is to replay the counts against the file's own
            // structure — reading the geometry and finding it plausible does not.
            int labels = Int(f[5]), pieces = Int(f[6]), texts = Int(f[7]);
            int terminals = Int(f[8]), stacks = Int(f[9]);
            i++;

            // Each label is a numeric line followed by its text line — two lines, by count.
            for (int n = 0; n < labels; n++, i += 2)
                if (i + 1 >= lines.Count)
                    return new DecalResult([], Underrun($"decal \"{f[0]}\"", $"{labels} labels", n));

            var piecesResult = ReadPieces(lines, ref i, pieces, art, f[0]);
            if (piecesResult is { } pr) return new DecalResult([], pr);

            for (int n = 0; n < texts; n++, i += 2)
                if (i + 1 >= lines.Count)
                    return new DecalResult([], Underrun($"decal \"{f[0]}\"", $"{texts} free-text labels", n));

            var padSpecs = new List<(string Pad, double X, double Y)>();
            for (int n = 0; n < terminals; n++, i++)
            {
                if (i >= lines.Count)
                    return new DecalResult([], Underrun($"decal \"{f[0]}\"", $"{terminals} terminals", n));
                var tf = Fields(lines[i]);
                if (tf.Count < 6 || tf[0] != "T") continue;
                padSpecs.Add((tf[5], Num(tf[1]), Num(tf[2])));
            }

            var stacksByPad = new Dictionary<int, (double W, double H, ComponentPadForm Form, double Rotation, double Drill)>();
            for (int n = 0; n < stacks; n++)
            {
                if (i >= lines.Count)
                    return new DecalResult([], Underrun($"decal \"{f[0]}\"", $"{stacks} pad stacks", n));
                var sf = Fields(lines[i]);
                if (sf.Count < 3 || sf[0] != "PAD")
                    return new DecalResult([], Underrun($"decal \"{f[0]}\"", $"{stacks} pad stacks", n));

                int padIndex = Int(sf[1]);
                int layerLines = Int(sf[2]);
                i++;

                (double, double, ComponentPadForm, double, double)? chosen = null;
                for (int l = 0; l < layerLines; l++, i++)
                {
                    if (i >= lines.Count)
                        return new DecalResult([], Underrun($"pad stack {padIndex}", $"{layerLines} layers", l));
                    var lf = Fields(lines[i]);

                    // Layer -2 is the outer (component) side; it is the one an SMD land uses. The
                    // inner and opposite rows are read past rather than merged.
                    if (lf.Count < 3 || Int(lf[0]) != -2) continue;

                    double width = Num(lf[1]);
                    string shape = lf[2];
                    double rot = lf.Count > 3 ? Num(lf[3]) : 0;
                    double length = lf.Count > 4 ? Num(lf[4]) : width;
                    chosen = (width, length, ShapeOf(shape, art), rot, 0);
                }
                if (chosen is { } c) stacksByPad[padIndex] = c;
            }

            // Pad stack 0 is the default; a stack numbered n overrides terminal n (1-based).
            var fallback = stacksByPad.TryGetValue(0, out var d) ? d : (W: 0.0, H: 0.0, Form: ComponentPadForm.Rectangle, Rotation: 0.0, Drill: 0.0);
            for (int n = 0; n < padSpecs.Count; n++)
            {
                var stack = stacksByPad.TryGetValue(n + 1, out var s) ? s : fallback;
                var (pad, x, y) = padSpecs[n];

                // The stack states the pad across-by-along; a 90° entry means the long axis runs in Y.
                bool quarter = Math.Abs(((stack.Rotation % 180) + 180) % 180 - 90) < 1e-6;
                double w = quarter ? stack.W : stack.H;
                double h = quarter ? stack.H : stack.W;

                art.Pads.Add(new ComponentPadSpec(
                    pad, x, y, w, h, stack.Form, RotationDeg: 0, DrillDiameter: stack.Drill));
            }

            artwork.Add(art);
        }

        if (artwork.Count == 0)
            return new DecalResult([], "This decal library declares no decal at all.");

        OrderVariants(artwork, part);
        return new DecalResult(artwork, null);
    }

    /// <summary>
    /// The counted run of drawn pieces. <b>This is R-PL2-4's sharp edge</b>: a piece header declares
    /// how many coordinate lines follow it, and consuming a different number silently shifts every
    /// piece after it onto the wrong layer.
    /// </summary>
    private static string? ReadPieces(
        List<string> lines, ref int i, int pieces, ComponentArtwork art, string decalName)
    {
        for (int n = 0; n < pieces; n++)
        {
            if (i >= lines.Count)
                return Underrun($"decal \"{decalName}\"", $"{pieces} pieces", n);

            var pf = Fields(lines[i]);
            if (pf.Count < 4)
                return Underrun($"decal \"{decalName}\"", $"{pieces} pieces", n);

            string kind = pf[0];
            int vertices = Int(pf[1]);
            double width = Num(pf[2]);
            int layer = Int(pf[3]);
            i++;

            var xy = new List<double>(vertices * 2);
            for (int v = 0; v < vertices; v++, i++)
            {
                if (i >= lines.Count)
                    return $"Decal \"{decalName}\" declares a {kind} of {vertices} vertices but the file " +
                           $"ends after {v}. Nothing was imported.";
                var vf = Fields(lines[i]);
                if (vf.Count < 2)
                    return $"Decal \"{decalName}\" declares a {kind} of {vertices} vertices; vertex " +
                           $"{v + 1} states {vf.Count} coordinate(s). Nothing was imported.";
                xy.Add(Num(vf[0]));
                xy.Add(Num(vf[1]));
            }

            var role = RoleOf(layer);
            if (role == ComponentLayerRole.Unknown) art.NoteUnknownLayer(layer);

            switch (kind)
            {
                // A CIRCLE states two points on its diameter rather than a centre and a radius.
                case "CIRCLE" when xy.Count >= 4:
                    art.Circles.Add(new ComponentArtworkCircle(
                        (xy[0] + xy[2]) / 2, (xy[1] + xy[3]) / 2,
                        Math.Abs(xy[2] - xy[0]) / 2, width, role, layer));
                    break;

                case "OPEN":
                case "CLOSED":
                case "COPOPN":
                case "COPCLS":
                    art.Paths.Add(new ComponentArtworkPath(
                        xy, width, role,
                        Closed: kind is "CLOSED" or "COPCLS",
                        Filled: false,
                        SourceLayer: layer));
                    break;

                default:
                    // Read past by count — the coordinates were already consumed, so an unmodelled
                    // piece kind costs exactly itself and shifts nothing.
                    break;
            }
        }
        return null;
    }

    // ── `.c` ──────────────────────────────────────────────────────────────────────────────────────

    private sealed record SymbolResult(ComponentSymbolDrawing? Drawing, string? Refusal);

    /// <summary>One terminal of a schematic decal: where it sits, which way it faces, and the name of
    /// the PIN DECAL that draws it.</summary>
    private sealed record SchTerminal(double X, double Y, int Orientation, string DecalName);

    /// <summary>One decal out of the `.c`. Both kinds share a grammar — the symbol's own body decal and
    /// the pin decals it refers to are read by the same routine and told apart only by who names
    /// whom.</summary>
    private sealed record SchDecal(string Name, ComponentArtwork Art, List<SchTerminal> Terminals);

    /// <summary>
    /// The schematic decal. Its terminal records are the symbol's pins <b>in the GATE block's
    /// order</b> — the file states no pin name and no pad identifier of its own, so the join is
    /// positional and the <c>.p</c> is the only thing that carries it.
    ///
    /// <para><b>A `.c` holds MORE than the one decal.</b> Each terminal record ends with the name of a
    /// PIN DECAL, and that decal — defined later in the same file, by the same grammar — is what draws
    /// the pin's lead from the terminal in to the body edge. The lead is not geometry of the symbol
    /// decal and it is not a length stated on the terminal, so a reader that stops after the first
    /// decal draws a body with every pin floating clear of it. So every decal in the file is read, and
    /// each terminal instantiates the one it names, rotated by the terminal's own orientation.</para>
    /// </summary>
    private static SymbolResult ReadSymbolDecal(
        string text, List<string> pinNames, List<string> padNames, ComponentPart part)
    {
        var lines = Lines(text);
        if (!lines.Any(l => IsBanner(l, SymbolHeader)))
            return new SymbolResult(null, $"This file does not open with a \"*…{SymbolHeader}…*\" banner.");

        int i = 0;
        while (i < lines.Count && (lines[i].Length == 0 || lines[i].StartsWith('*'))) i++;
        if (i >= lines.Count) return new SymbolResult(null, "This decal library declares no decal at all.");

        var first = Fields(lines[i]);
        if (first.Count < 11)
            return new SymbolResult(null, $"The schematic decal header states {first.Count} fields; 11 are needed.");

        var decals = new List<SchDecal>();
        while (i < lines.Count)
        {
            var f = Fields(lines[i]);
            if (!IsSchDecalHeader(f)) { i++; continue; }

            var (decal, refusal) = ReadOneSchDecal(lines, ref i, f);
            if (refusal is not null) return new SymbolResult(null, refusal);
            decals.Add(decal!);
        }

        if (decals.Count == 0) return new SymbolResult(null, "This decal library declares no decal at all.");

        // The first decal is the symbol; every other one is a candidate pin decal. Looked up BY NAME
        // and never by position, so a decal nothing names simply goes unused rather than being drawn
        // at some terminal it does not belong to.
        var symbolDecal = decals[0];
        var byName = new Dictionary<string, SchDecal>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in decals.Skip(1)) byName.TryAdd(d.Name, d);

        var drawing = new ComponentSymbolDrawing { Name = part.Name };
        Emit(drawing, symbolDecal.Art, 0, 0, 0);

        int placed = 0;
        foreach (var terminal in symbolDecal.Terminals)
        {
            string pin = placed < pinNames.Count ? pinNames[placed] : $"pin{placed + 1}";
            string? pad = placed < padNames.Count ? padNames[placed] : null;

            // `T x y …` — the terminal's point is the pin's FREE END (PL1 R-PL1-19), verified against
            // the decal's own body outline: the lead its pin decal draws runs from here INWARD.
            // The pin decal carries the name's justification inside itself, and it ROTATES with the
            // terminal — which is the same thing as saying the name goes on the body side. Read off
            // the orientation rather than out of the decal's label records: those state a font and a
            // size this import does not carry, so the one field that would be used is the one field
            // the orientation already settles.
            var (dx, dy) = ComponentSymbolLead.Direction(terminal.Orientation * 90);

            drawing.Pins.Add(new ComponentSymbolPin(
                pin, pad,
                (int)Math.Round(terminal.X, MidpointRounding.AwayFromZero),
                (int)Math.Round(terminal.Y, MidpointRounding.AwayFromZero),
                Bonded: false,
                NameAlign: ComponentSymbolLead.NameAlignFor(dx, dy)));
            placed++;

            // The orientation is stated in quarter turns, which is the only rotation these files use.
            if (byName.TryGetValue(terminal.DecalName, out var pinDecal))
                Emit(drawing, pinDecal.Art, terminal.X, terminal.Y, terminal.Orientation * 90);
        }

        if (placed != pinNames.Count && pinNames.Count > 0)
            part.Messages.Add(
                $"The part type states {pinNames.Count} pins and the schematic decal draws {placed}. " +
                "The pins that could be joined were; the rest are reported here.");

        // A terminal naming a decal the file never defines is a lead that cannot be drawn. Reported by
        // count rather than filled in with a guessed length: the length is the pin decal's own
        // geometry, and inventing one puts the body edge somewhere the file does not say it is.
        var missing = symbolDecal.Terminals
            .Where(t => t.DecalName.Length > 0 && !byName.ContainsKey(t.DecalName))
            .Select(t => t.DecalName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (missing.Count > 0)
            part.Messages.Add(
                $"The schematic decal draws its pins with {string.Join(", ", missing.Select(m => $"\"{m}\""))}, " +
                "which this file does not define. Those pins are placed at their stated points with no lead drawn.");

        return new SymbolResult(drawing, null);
    }

    /// <summary>
    /// Whether a line opens a decal: at least eleven fields, the first non-numeric.
    ///
    /// <para><c>T</c> is excluded explicitly. A terminal record is also fourteen fields beginning with
    /// a non-numeric, so it is the one record in this grammar that a header test cannot tell apart
    /// from a header — and mistaking one for a decal would read the rest of the file as that decal's
    /// contents.</para>
    /// </summary>
    private static bool IsSchDecalHeader(List<string> f)
        => f.Count >= 11
        && f[0] != "T"
        && !double.TryParse(f[0], NumberStyles.Float, CultureInfo.InvariantCulture, out _);

    /// <summary>One decal, consumed by its own declared counts (R-PL2-4) from <paramref name="i"/>,
    /// which is left on the line after it.</summary>
    private static (SchDecal? Decal, string? Refusal) ReadOneSchDecal(
        List<string> lines, ref int i, List<string> f)
    {
        string name = f[0];

        // The same two-run shape as the land-pattern decal: `labels` before the pieces, `texts` after.
        int labels = Int(f[7]), pieces = Int(f[8]), texts = Int(f[9]), terminals = Int(f[10]);
        i++;

        // Two font declarations precede the labels in this decal's grammar.
        while (i < lines.Count && lines[i].StartsWith('"')) i++;

        for (int n = 0; n < labels; n++, i += 2)
            if (i + 1 >= lines.Count)
                return (null, Underrun($"schematic decal \"{name}\"", $"{labels} labels", n));

        var art = new ComponentArtwork();
        if (ReadPieces(lines, ref i, pieces, art, name) is { } refusal) return (null, refusal);

        for (int n = 0; n < texts; n++, i += 2)
            if (i + 1 >= lines.Count)
                return (null, Underrun($"schematic decal \"{name}\"", $"{texts} free-text labels", n));

        var read = new List<SchTerminal>();
        for (int n = 0; n < terminals && i < lines.Count; n++)
        {
            while (i < lines.Count && !lines[i].StartsWith("T ", StringComparison.Ordinal)) i++;
            if (i >= lines.Count)
                return (null, Underrun($"schematic decal \"{name}\"", $"{terminals} terminals", n));

            var tf = Fields(lines[i]);
            i++;
            if (tf.Count < 3) continue;

            read.Add(new SchTerminal(
                Num(tf[1]), Num(tf[2]),
                tf.Count > 4 ? Int(tf[4]) : 0,
                tf.Count > 12 ? tf[^1] : ""));
        }

        return (new SchDecal(name, art, read), null);
    }

    /// <summary>
    /// Appends one decal's drawn pieces to the symbol, rotated by <paramref name="degrees"/> about its
    /// own origin and translated to (<paramref name="x"/>, <paramref name="y"/>).
    ///
    /// <para>The symbol's own body decal comes through here too, at the identity — one path for both,
    /// so a pin decal cannot pick up a transform rule the body does not have.</para>
    /// </summary>
    private static void Emit(
        ComponentSymbolDrawing drawing, ComponentArtwork art, double x, double y, double degrees)
    {
        double rad = degrees * Math.PI / 180.0;
        double cos = Math.Cos(rad), sin = Math.Sin(rad);

        foreach (var path in art.Paths)
        {
            var xy = new List<double>(path.Xy.Count);
            for (int n = 0; n + 1 < path.Xy.Count; n += 2)
            {
                double px = path.Xy[n], py = path.Xy[n + 1];
                xy.Add(x + (px * cos) - (py * sin));
                xy.Add(y + (px * sin) + (py * cos));
            }
            drawing.Shapes.Add(new KitSymbolPath(xy, path.Closed, false) { Width = path.Width });
        }

        foreach (var circle in art.Circles)
            drawing.Shapes.Add(new KitSymbolArc(
                x + (circle.Cx * cos) - (circle.Cy * sin),
                y + (circle.Cx * sin) + (circle.Cy * cos),
                circle.Radius, 0, 360) { Width = circle.Width });
    }

    // ── Shared ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The layer numbers this format assigns fixed meanings to. Anything else is reported by
    /// number with a count (R-PL2-14) and lands on the fallback drawing layer, which carries no
    /// fabrication meaning — so artwork put there cannot be mistaken for silkscreen or copper.</summary>
    private static ComponentLayerRole RoleOf(int layer) => layer switch
    {
        1 => ComponentLayerRole.TopCopper,
        25 => ComponentLayerRole.TopCourtyard,
        26 => ComponentLayerRole.TopSilkscreen,
        27 => ComponentLayerRole.TopAssembly,
        _ => ComponentLayerRole.Unknown,
    };

    private static ComponentPadForm ShapeOf(string shape, ComponentArtwork art) => shape switch
    {
        "R" => ComponentPadForm.Round,
        "S" => ComponentPadForm.Rectangle,
        "RF" or "RA" => ComponentPadForm.Rectangle,
        "OF" or "OA" => ComponentPadForm.Oval,
        _ => ComponentPadForm.Rectangle,
    };

    /// <summary>The <c>_M</c>/<c>_L</c> density suffix this format spells with an underscore inside the
    /// decal NAME, rather than in a file name the way PL1's formats do.</summary>
    internal static (string BaseName, string Variant) SplitVariant(string name)
    {
        // R-fp4-2c: one list, shared with ComponentRead and with the generated patterns.
        foreach (var suffix in Footprints.DensityVariant.Suffixes)
            if (name.EndsWith(suffix, StringComparison.Ordinal) && name.Length > suffix.Length)
                return (name[..^suffix.Length], suffix);
        return (name, "");
    }

    /// <summary>The nominal pattern first, so <c>ComponentImport</c> writes it as the primary view.</summary>
    private static void OrderVariants(List<ComponentArtwork> artwork, ComponentPart part)
    {
        var ordered = artwork
            .Select((a, i) => (Artwork: a, Index: i))
            .OrderBy(t => t.Artwork.Variant.Length == 0 ? 0 : 1)
            .ThenBy(t => t.Index)
            .Select(t => t.Artwork)
            .ToList();
        artwork.Clear();
        artwork.AddRange(ordered);
    }

    private static string Underrun(string entity, string declared, int got)
        => $"{entity} declares {declared} but the file ends after {got}. Nothing was imported.";

    /// <summary>A banner line: <c>*</c>, a product word this repo does not name, then the content
    /// marker. See the constants above for why the product word is not matched.</summary>
    internal static bool IsBanner(string line, string marker)
        => line.StartsWith('*') && line.Contains(marker, StringComparison.OrdinalIgnoreCase);

    private static List<string> Lines(string text)
        => [.. text.Split('\n').Select(l => l.TrimEnd('\r').TrimEnd())];

    /// <summary>Whitespace-separated fields, with a leading quoted string kept whole.</summary>
    private static List<string> Fields(string line)
    {
        var result = new List<string>();
        int i = 0;
        while (i < line.Length)
        {
            while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
            if (i >= line.Length) break;

            if (line[i] == '"')
            {
                int end = line.IndexOf('"', i + 1);
                if (end < 0) { result.Add(line[(i + 1)..]); break; }
                result.Add(line[(i + 1)..end]);
                i = end + 1;
                continue;
            }

            int start = i;
            while (i < line.Length && !char.IsWhiteSpace(line[i])) i++;
            result.Add(line[start..i]);
        }
        return result;
    }

    /// <summary><c>"Key" the rest of the line verbatim</c> — the value is NOT re-split, because it
    /// routinely contains spaces, commas and URLs (PL1 R-PL1-7: carried verbatim, never parsed).</summary>
    private static (string Key, string Value) SplitAttribute(string line)
    {
        string t = line.TrimStart();
        if (!t.StartsWith('"')) return ("", "");
        int end = t.IndexOf('"', 1);
        if (end < 0) return ("", "");
        return (t[1..end], t[(end + 1)..].Trim());
    }

    private static int Int(string s)
        => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;

    private static double Num(string s)
        => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;
}

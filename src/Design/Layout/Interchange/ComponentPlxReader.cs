// The `.PLX` / `.DSL` dialect — ONE reader, two extensions
// (docs/sonnet-briefs/brief-PL2-component-library-breadth.md §4.3).
//
// R-PL2-10: they are the same S-expression dialect from a shared lineage — identical section tags
// (padStyleDef, patternDef, symbolDef, compDef), identical header `(asciiHeader (fileUnits MIL))`,
// differing ONLY in the first line's format banner. The banner names the format in messages; nothing
// below it branches on which extension arrived.
//
// ── R-PL2-12: the second indirection, and it is this phase's worst trap ───────────────────────────
//
// `compDef` lists one `compPin` per pad identifier, and each carries BOTH a `pinName` and a
// `symPinNum` — the symbol's own pin ordinal — and **symPinNum is not the pad number**. The brief's
// own worked example: pad 2 is symbol pin 9, pad 4 is symbol pin 8, and a string-named thermal pad is
// symbol pin 6.
//
// A reader that joins the symbol to the footprint by ordinal produces a fully populated,
// correctly-shaped, WRONGLY WIRED part. Nothing about it looks wrong.
//
// The indirection is frequently the IDENTITY — a part whose symbol happens to be drawn in pad order
// has symPinNum == padNum throughout — which is exactly why a reader must never notice that on the
// file in front of it and take the shortcut. The join is followed always.
//
// The format hands us a free consistency check and this reader takes it: `attachedPattern` carries a
// redundant `padPinMap` of pad ordinal -> compPin identifier. The two are CROSS-CHECKED and a
// disagreement is a refusal, never a preference — picking one silently is exactly how this class of
// bug ships.

using CircuitRF.Core.Pdk;

namespace CircuitRF.Design.Layout.Interchange;

public static class ComponentPlxReader
{
    public sealed record ReadResult(ComponentPart? Part, string? Refusal);

    // ── R-PL2-18: matched on the VENDOR-FREE part of the banner ─────────────────────────────────
    //
    // The first line is `<PRODUCT>_LIBRARY_ASCII "…"` in one extension and
    // `<PRODUCT>_INTERMEDIATE_ASCII "…"` in the other, where <PRODUCT> is a tool name root
    // CLAUDE.md forbids this repo from carrying even as a string literal. These suffixes are the part
    // that names the FILE's kind rather than the tool, and are what distinguishes the two extensions —
    // which is all this reader ever needed the banner for (R-PL2-10).
    internal const string PlxBanner = "_LIBRARY_ASCII";
    internal const string DslBanner = "_INTERMEDIATE_ASCII";

    /// <summary>True for either banner — the classifier's content test. Anchored to the first line so
    /// a body that happens to contain the word is not mistaken for a banner.</summary>
    public static bool IsThisDialect(string head)
    {
        int nl = head.IndexOf('\n');
        string first = nl < 0 ? head : head[..nl];
        return first.Contains(PlxBanner, StringComparison.Ordinal)
            || first.Contains(DslBanner, StringComparison.Ordinal);
    }

    public static ReadResult Read(string text, int dbuPerMicron)
    {
        var parsed = ComponentPlxSexpr.Parse(text);

        var library = parsed.Roots.FirstOrDefault(r => r.Tag.Equals("library", StringComparison.OrdinalIgnoreCase));
        if (library is null)
            return new ReadResult(null, "This file declares no (library …) section — it is not a library of this dialect.");

        var comp = library.All("compDef").FirstOrDefault();
        if (comp is null)
            return new ReadResult(null, "This library declares no (compDef …) — there is no component in it to import.");

        var part = new ComponentPart { Name = comp.Atom };

        foreach (var other in library.All("compDef").Skip(1))
            part.UnimportedSections.Add(other.Atom);

        // `(attr "Key" "Value")` — carried verbatim (PL1 R-PL1-7), first spelling wins so the
        // duplicated block these files routinely carry does not thrash.
        foreach (var attr in comp.All("attr"))
            if (attr.Atoms.Count >= 2 && attr.Atoms[0].Length > 0 && attr.Atoms[1].Length > 0)
                part.Metadata.TryAdd(attr.Atoms[0], attr.Atoms[1]);

        if (comp.First("compHeader") is { } header && header.AtomOf("refDesPrefix") is { Length: > 0 } prefix)
            part.Metadata.TryAdd("Reference", prefix);

        // ── The map, in the file's own stated order ─────────────────────────────────────────────
        var compPins = new List<(string Pad, string Pin, int SymPinNum)>();
        foreach (var pin in comp.All("compPin"))
        {
            string pad = pin.Atom;
            if (pad.Length == 0) continue;
            compPins.Add((pad, pin.AtomOf("pinName"), (int)pin.NumberOf("symPinNum", 0)));
        }

        if (compPins.Count == 0)
            return new ReadResult(null, $"Component \"{part.Name}\" declares no compPin — it states no pin↔pad map.");

        // ── The cross-check the format hands us ────────────────────────────────────────────────
        var patterns = comp.All("attachedPattern").ToList();
        foreach (var attached in patterns)
        {
            if (attached.First("padPinMap") is not { } padPinMap) continue;

            // The map alternates `(padNum n) (compPinRef "id")`, so it is read as a SEQUENCE of the
            // two tags rather than as two independent lists — the pairing is positional.
            var pairs = new List<(int PadNum, string Ref)>();
            int? pendingPadNum = null;
            foreach (var node in padPinMap.Children)
            {
                if (node.Tag.Equals("padNum", StringComparison.OrdinalIgnoreCase))
                    pendingPadNum = (int)node.Numbers().FirstOrDefault();
                else if (node.Tag.Equals("compPinRef", StringComparison.OrdinalIgnoreCase) && pendingPadNum is { } n)
                {
                    pairs.Add((n, node.Atom));
                    pendingPadNum = null;
                }
            }

            foreach (var (padNum, reference) in pairs)
            {
                // padNum is the pattern's pad ORDINAL, 1-based, into the compPin list's own order.
                if (padNum < 1 || padNum > compPins.Count) continue;
                string stated = compPins[padNum - 1].Pad;
                if (string.Equals(stated, reference, StringComparison.Ordinal)) continue;

                return new ReadResult(null,
                    $"Component \"{part.Name}\" contradicts itself about pad {padNum}: its compPin list " +
                    $"names it \"{stated}\" and its padPinMap names it \"{reference}\". Nothing was " +
                    "imported — the two spellings of this map must agree, and choosing one silently " +
                    "would wire the part wrongly.");
            }
        }

        foreach (var (pad, pin, _) in compPins)
            if (pin.Length > 0) part.ConnectTable.Add(new ComponentConnect(pin, pad));

        // ── The symbol, joined by symPinNum and never by ordinal ────────────────────────────────
        string wantedSymbol = comp.All("attachedSymbol").FirstOrDefault()?.AtomOf("symbolName") ?? "";
        var symbolDefs = library.All("symbolDef").ToList();
        var symbolDef = symbolDefs.FirstOrDefault(s => s.Atom == wantedSymbol) ?? symbolDefs.FirstOrDefault();

        if (symbolDef is not null)
        {
            part.Symbol = ReadSymbol(symbolDef, compPins);
            foreach (var other in symbolDefs.Where(s => !ReferenceEquals(s, symbolDef)))
                part.UnimportedSections.Add(other.Atom);
        }

        // ── The land patterns ──────────────────────────────────────────────────────────────────
        var padStyles = ReadPadStyles(library);
        var wantedPatterns = patterns.Select(p => p.AtomOf("patternName")).Where(n => n.Length > 0).ToHashSet(StringComparer.Ordinal);

        var patternDefs = library.All("patternDef").ToList();
        foreach (var patternDef in patternDefs)
        {
            string name = patternDef.Atom;

            // Density variants are siblings of the attached pattern, named with the same suffixes PL1
            // R-PL2-25 already handles; a patternDef belonging to a different component is skipped.
            var (baseName, variant) = ComponentRecordsReader.SplitVariant(name);
            if (wantedPatterns.Count > 0 && !wantedPatterns.Contains(name) && !wantedPatterns.Contains(baseName))
                continue;

            var artwork = ReadPattern(patternDef, baseName, variant, padStyles, compPins);
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

        var ordered = part.Footprints
            .Select((f, i) => (Footprint: f, Index: i))
            .OrderBy(t => t.Footprint.Variant.Length == 0 ? 0 : 1)
            .ThenBy(t => t.Index)
            .Select(t => t.Footprint)
            .ToList();
        part.Footprints.Clear();
        part.Footprints.AddRange(ordered);

        if (part.Symbol is null && part.Footprints.Count == 0)
            return new ReadResult(null, $"Component \"{part.Name}\" names neither a symbol nor a pattern this library declares.");

        return new ReadResult(part, null);
    }

    /// <summary>
    /// The symbol drawing. Each <c>pin</c> states its own <c>pinNum</c>, and <b>that ordinal is what
    /// <c>symPinNum</c> refers to</b> — so the name and pad come from the compPin whose symPinNum
    /// matches, never from the pin's position in this list.
    /// </summary>
    private static ComponentSymbolDrawing ReadSymbol(
        PlxNode symbolDef, List<(string Pad, string Pin, int SymPinNum)> compPins)
    {
        var drawing = new ComponentSymbolDrawing { Name = symbolDef.Atom };
        var bySymPin = new Dictionary<int, (string Pad, string Pin)>();
        foreach (var (pad, pin, symPinNum) in compPins)
            if (symPinNum > 0) bySymPin.TryAdd(symPinNum, (pad, pin));

        foreach (var pin in symbolDef.All("pin"))
        {
            int pinNum = (int)pin.NumberOf("pinNum", 0);
            var (x, y) = pin.PointOf("pt");

            string name = bySymPin.TryGetValue(pinNum, out var joined) ? joined.Pin : "";
            string? pad = bySymPin.TryGetValue(pinNum, out var j2) ? j2.Pad : null;

            // Falls back to the pin's own drawn label only when the map does not reach it, so a pin
            // the compDef never mentions is still drawn rather than dropped.
            if (name.Length == 0)
                name = pin.First("pinName")?.First("text")?.Atoms.FirstOrDefault(a => a.Length > 0) ?? $"pin{pinNum}";

            drawing.Pins.Add(new ComponentSymbolPin(
                name, pad,
                (int)Math.Round(x, MidpointRounding.AwayFromZero),
                (int)Math.Round(y, MidpointRounding.AwayFromZero)));
        }

        foreach (var line in symbolDef.All("line"))
        {
            var pts = line.All("pt").Select(p => p.Numbers()).Where(n => n.Count >= 2).ToList();
            if (pts.Count >= 2)
                drawing.Shapes.Add(new KitSymbolLine(pts[0][0], pts[0][1], pts[1][0], pts[1][1]));
        }

        foreach (var poly in symbolDef.All("poly"))
        {
            var xy = new List<double>();
            foreach (var pt in poly.All("pt"))
            {
                var n = pt.Numbers();
                if (n.Count >= 2) { xy.Add(n[0]); xy.Add(n[1]); }
            }
            if (xy.Count >= 4) drawing.Shapes.Add(new KitSymbolPath(xy, true, false));
        }

        foreach (var arc in symbolDef.All("arc"))
        {
            var (cx, cy) = arc.PointOf("pt");
            double r = arc.NumberOf("radius", 0);
            if (r > 0) drawing.Shapes.Add(new KitSymbolArc(cx, cy, r, arc.NumberOf("startAngle", 0), arc.NumberOf("sweepAngle", 360)));
        }

        return drawing;
    }

    /// <summary>
    /// <c>padStyleDef</c> by name. The outer-layer <c>padShape</c> is the land; the inner and mask
    /// rows state the same outline and are read past rather than merged.
    /// </summary>
    private static Dictionary<string, ComponentPadSpec> ReadPadStyles(PlxNode library)
    {
        var result = new Dictionary<string, ComponentPadSpec>(StringComparer.Ordinal);

        foreach (var style in library.All("padStyleDef"))
        {
            string name = style.Atom;
            if (name.Length == 0) continue;

            double drill = style.NumberOf("holeDiam", 0);

            foreach (var shape in style.All("padShape"))
            {
                int layer = (int)shape.NumberOf("layerNumRef", 0);
                if (layer != 1) continue;                               // the component-side land

                double w = shape.NumberOf("shapeWidth", 0);
                double h = shape.NumberOf("shapeHeight", 0);
                if (w <= 0 && h <= 0) continue;

                var form = shape.AtomOf("padShapeType").ToUpperInvariant() switch
                {
                    "RECT" => ComponentPadForm.Rectangle,
                    "ELLIPSE" when Math.Abs(w - h) < 1e-9 => ComponentPadForm.Round,
                    "ELLIPSE" => ComponentPadForm.Oval,
                    "ROUNDEDRECT" => ComponentPadForm.RoundedRectangle,
                    "OVAL" => ComponentPadForm.Oval,
                    _ => ComponentPadForm.Rectangle,
                };

                result[name] = new ComponentPadSpec("", 0, 0, w, h, form, 0, drill);
                break;
            }
        }

        return result;
    }

    private static ComponentArtwork ReadPattern(
        PlxNode patternDef,
        string baseName,
        string variant,
        Dictionary<string, ComponentPadSpec> padStyles,
        List<(string Pad, string Pin, int SymPinNum)> compPins)
    {
        var art = new ComponentArtwork { Name = baseName, Variant = variant };

        foreach (var layerGroup in patternDef.Children.Where(c =>
                     c.Tag.Equals("multiLayer", StringComparison.OrdinalIgnoreCase)
                  || c.Tag.Equals("layerContents", StringComparison.OrdinalIgnoreCase)))
        {
            int layer = (int)layerGroup.NumberOf("layerNumRef", 0);
            var role = RoleOf(layerGroup.Tag.Equals("multiLayer", StringComparison.OrdinalIgnoreCase) ? 1 : layer);

            foreach (var pad in layerGroup.All("pad"))
            {
                int padNum = (int)pad.NumberOf("padNum", 0);
                var (x, y) = pad.PointOf("pt");
                string styleRef = pad.AtomOf("padStyleRef");

                var style = padStyles.TryGetValue(styleRef, out var s)
                    ? s
                    : new ComponentPadSpec("", 0, 0, 0, 0, ComponentPadForm.Rectangle);

                // padNum is the pad ORDINAL into the compPin list; the pad IDENTIFIER is what the
                // compPin states, which is how a thermal pad keeps its name (PL1 R-PL1-9).
                string padName = padNum >= 1 && padNum <= compPins.Count
                    ? compPins[padNum - 1].Pad
                    : padNum.ToString();

                art.Pads.Add(new ComponentPadSpec(
                    padName, x, y, style.Width, style.Height, style.Form,
                    pad.NumberOf("rotation", 0), style.DrillDiameter));
            }

            if (role == ComponentLayerRole.Unknown
                && layerGroup.All("line").Any() is var hasLines && hasLines)
                art.NoteUnknownLayer(layer);

            foreach (var line in layerGroup.All("line"))
            {
                var pts = line.All("pt").Select(p => p.Numbers()).Where(n => n.Count >= 2).ToList();
                if (pts.Count < 2) continue;
                art.Paths.Add(new ComponentArtworkPath(
                    [pts[0][0], pts[0][1], pts[1][0], pts[1][1]],
                    line.NumberOf("width", 0), role, false, false, layer));
            }

            foreach (var poly in layerGroup.All("poly"))
            {
                var xy = new List<double>();
                foreach (var pt in poly.All("pt"))
                {
                    var n = pt.Numbers();
                    if (n.Count >= 2) { xy.Add(n[0]); xy.Add(n[1]); }
                }
                if (xy.Count >= 4)
                    art.Paths.Add(new ComponentArtworkPath(xy, poly.NumberOf("width", 0), role, true, false, layer));
            }
        }

        return art;
    }

    /// <summary>The layer numbers this dialect assigns fixed meanings to. Anything else is reported by
    /// number with a count (R-PL2-14) rather than guessed at.</summary>
    private static ComponentLayerRole RoleOf(int layer) => layer switch
    {
        1 => ComponentLayerRole.TopCopper,
        2 => ComponentLayerRole.BottomCopper,
        18 => ComponentLayerRole.TopSilkscreen,
        20 => ComponentLayerRole.TopMask,
        22 => ComponentLayerRole.TopPaste,
        30 => ComponentLayerRole.TopAssembly,
        _ => ComponentLayerRole.Unknown,
    };
}

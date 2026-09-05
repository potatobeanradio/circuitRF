// The neutral, still-format-shaped result of reading a component — a symbol, one or more footprints,
// and the map between them (docs/sonnet-briefs/brief-PL1-component-library-import.md).
//
// The same split PcbBoard/PcbImport already draw: the readers in this folder touch nothing but bytes
// and produce one of these; the orchestrator is the only thing that turns it into cell folders, layer
// reconciliation and Messages. Nothing here references a UI framework or a Symbol — SymbolPrimitive
// lives beside the renderer in src/Ui, which is exactly why KitSymbolShape exists (R-PL1-16).
//
// ── Units and handedness, stated once ────────────────────────────────────────────────────────────
//
// Symbol coordinates in this file are MILS and Y is UP, both as the source formats state them
// (R-PL1-17, R-PL1-18). One mil is one symbol-editor local unit, so the consumer scales by exactly 1
// and negates Y; neither of those decisions belongs in a format reader. Footprint coordinates are
// already DBU with Y up, because PcbReader put them there.

using CircuitRF.Core.Pdk;

namespace CircuitRF.Design.Layout.Interchange;

/// <summary>
/// One terminal of a symbol drawing.
///
/// <para><b><paramref name="PadName"/> is the join, and it is a STRING</b> (R-PL1-9). Two of the three
/// formats state it inside the pin; the third states it in a separate table, and
/// <see cref="ComponentPart.ConnectTable"/> carries that case. A pad identifier is not necessarily
/// numeric — a thermal pad is routinely named — so parsing one as an integer drops that terminal.</para>
/// </summary>
/// <param name="XMil">Mils, X right.</param>
/// <param name="YMil">Mils, <b>Y up</b> — the source's own handedness, flipped by the consumer.</param>
/// <param name="Bonded">
/// This declaration carried the format's own bonding suffix — <c>GND@1</c>, <c>GND@2</c> — which says
/// it is one of SEVERAL drawn pins of ONE logical pin (R-PL1-11). Two declarations that are bonded and
/// share a name are one terminal; two that merely share a name are two. Only the format's suffix
/// separates those, and it is stripped from <paramref name="Name"/>, so it is recorded here instead.
/// </param>
public sealed record ComponentSymbolPin(string Name, string? PadName, int XMil, int YMil, bool Bonded = false);

/// <summary>One symbol section — a whole symbol for a single-section part, one gate of a multi-section
/// one (R-PL1-23).</summary>
public sealed class ComponentSymbolDrawing
{
    public string Name { get; set; } = "";

    public List<ComponentSymbolPin> Pins { get; } = [];

    /// <summary>The drawn body, in mils with Y up. Read, never substituted for a generated box
    /// (R-PL1-20) — these files carry polylines, rectangles, arcs and polygons that are what
    /// distinguishes a part from a rectangle.</summary>
    public List<KitSymbolShape> Shapes { get; } = [];
}

/// <summary>
/// One land pattern.
///
/// <para><see cref="Variant"/> is R-PL1-25's density level — the <c>-M</c>/<c>-L</c> suffix a file name
/// carries, empty for the nominal pattern. Density levels of one pattern become sibling layout views of
/// one cell rather than separate cells.</para>
/// </summary>
public sealed class ComponentFootprint
{
    public string Name { get; set; } = "";

    /// <summary>The density suffix the file name carries — <c>""</c> for the nominal pattern, which is
    /// the one that becomes <c>PrimaryLayout</c>.</summary>
    public string Variant { get; set; } = "";

    public PcbFootprintCell Cell { get; set; } = new();

    /// <summary>The table the pads' layer specs were expanded against — synthesised for a standalone
    /// footprint file (R-PL1-13), read from the file itself for an XML library.</summary>
    public IReadOnlyList<PcbLayerTableEntry> LayerTable { get; set; } = [];

    /// <summary>Pad identifiers in the file's own stated order — what R-PL1-8's "non-numeric
    /// identifiers last in their own stated order" is ordered by.</summary>
    public List<string> PadNames { get; } = [];

    /// <summary>The file this pattern was read from, by name. Copied into the cell folder (R-PL1-2).</summary>
    public string SourceFileName { get; set; } = "";
}

/// <summary>
/// Everything the read recovered about ONE part.
///
/// <para>Carries all three of the symbol, the footprints and the map, whichever files they came from,
/// so <c>ComponentImport</c> has one shape to write from rather than one per reader.</para>
/// </summary>
public sealed class ComponentPart
{
    public string Name { get; set; } = "";

    /// <summary>The first section. Null when only a footprint was found, which is a legitimate — and
    /// reported — outcome, not a failure.</summary>
    public ComponentSymbolDrawing? Symbol { get; set; }

    public List<ComponentFootprint> Footprints { get; } = [];

    /// <summary>
    /// Pin → pad joins stated in a SEPARATE table (R-PL1-8's third spelling). Empty when the join
    /// lives inside the pin, which is what the other two formats do.
    ///
    /// <para>This is the only one of the three that can express a pin bonded to several pads, so it is
    /// a list of pairs rather than a dictionary.</para>
    /// </summary>
    public List<ComponentConnect> ConnectTable { get; } = [];

    /// <summary>The free-text properties the file states — manufacturer, part identifier, description,
    /// datasheet URL. Carried verbatim to read-only cell parameters (R-PL1-7); never parsed, and never
    /// used to infer a model.</summary>
    public Dictionary<string, string> Metadata { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every file this part was read from, by absolute path — copied into the cell folder so a
    /// re-import after a reader improvement has the bytes (R-PL1-2).</summary>
    public List<string> SourceFiles { get; } = [];

    /// <summary>Sections and package variants this phase did not import, by name (R-PL1-23). Reported,
    /// never silently picked and never silently merged.</summary>
    public List<string> UnimportedSections { get; } = [];

    public List<string> UnimportedDeviceVariants { get; } = [];

    public List<string> Messages { get; } = [];
}

/// <summary>One row of a separate pin↔pad table.</summary>
/// <param name="Pin">The SYMBOL pin's name, with any format-level bonding suffix already removed:
/// <c>GND@1</c> and <c>GND@2</c> are one logical pin bonded to two pads, and the <c>@n</c> belongs to
/// the format rather than to the name (R-PL1-11).</param>
/// <param name="Bonded">As <see cref="ComponentSymbolPin.Bonded"/> — this row named the pin with the
/// format's bonding suffix, so it is one of several pads of one logical pin.</param>
public sealed record ComponentConnect(string Pin, string Pad, string? Section = null, bool Bonded = false);

// ── The one place PortIndex is decided ───────────────────────────────────────────────────────────

/// <summary>
/// One electrical terminal of the imported part: a pad, a symbol pin, or — in the ordinary case —
/// both, joined.
/// </summary>
/// <param name="PortIndex">1-based, assigned ONCE and shared by both views (R-PL1-8).</param>
/// <param name="PadName">Null for a symbol pin the map joins to no pad.</param>
/// <param name="PinName">Null for a pad no symbol pin references — a mounting or shield pad, or the
/// second pad of a pin bonded to two.</param>
/// <param name="SymbolPinIndex">
/// WHICH declaration in <see cref="ComponentSymbolDrawing.Pins"/> this terminal is, or -1 for a pad no
/// symbol pin references.
///
/// <para><b>A pin name is not an identity.</b> A real part declares <c>VSS</c> seven times and
/// <c>VDD</c> six, each its own pin bonded to its own pad, so a join keyed on the name reads six of
/// those seven as "one logical pin bonded to seven pads" — it drops six terminals' symbol side and
/// gives all seven declarations one <c>PortIndex</c>. The declaration is the identity; the name is
/// text that happens to repeat.</para>
/// </param>
public sealed record ComponentTerminal(int PortIndex, string? PadName, string? PinName, int SymbolPinIndex = -1);

/// <summary>
/// Builds the ordered terminal table — <b>the invariant this whole phase exists to preserve</b>.
///
/// <para><c>SymbolPin.PortIndex</c> <i>i</i> and <c>LayoutView.Pins[i-1]</c> name the same terminal,
/// and this is the one place that numbering is decided for both views.</para>
///
/// <para><b>Symbol pin declaration order is NOT pad order</b> (R-PL1-10), so the numbering is never
/// taken from the order the pins arrived in. Terminals are ordered by PAD identifier, numeric-aware so
/// <c>2</c> precedes <c>10</c>, with non-numeric identifiers last in the order the footprint states
/// them.</para>
/// </summary>
public static class ComponentTerminals
{
    /// <summary>What <see cref="Build"/> decided, plus the two counts R-PL1-11 requires reported.</summary>
    /// <param name="PinsWithNoPad">Symbol pins the map joins to nothing.</param>
    /// <param name="PadsWithNoPin">Pads no symbol pin references.</param>
    public sealed record Result(
        IReadOnlyList<ComponentTerminal> Terminals,
        int PinsWithNoPad,
        int PadsWithNoPin);

    /// <summary>
    /// Joins <paramref name="part"/>'s pads and symbol pins into one numbered table.
    /// </summary>
    /// <param name="padOrder">Pad identifiers in the FOOTPRINT's own stated order. The primary
    /// pattern's, when a part carries several density variants — they are one land pattern and state
    /// the same pads.</param>
    public static Result Build(ComponentPart part, IReadOnlyList<string> padOrder)
    {
        var symbolPins = part.Symbol?.Pins ?? [];

        // ── Which declarations are ONE pin ──────────────────────────────────────────────────────
        //
        // Ordinarily a declaration is a terminal: a part declares VSS seven times, each its own pin on
        // its own pad, and a join keyed on the name would collapse six of them. The one exception is
        // the format that spells one logical pin's several drawn halves `GND@1`/`GND@2` — those share
        // an identity, which is what makes the second pad a pad with no symbol pin rather than a second
        // GND terminal. The suffix is the ONLY thing separating the two cases.
        var identity = new int[symbolPins.Count];
        for (int i = 0; i < symbolPins.Count; i++)
        {
            identity[i] = i;
            if (!symbolPins[i].Bonded) continue;
            for (int j = 0; j < i; j++)
                if (symbolPins[j].Bonded
                    && string.Equals(symbolPins[j].Name, symbolPins[i].Name, StringComparison.Ordinal))
                {
                    identity[i] = identity[j];
                    break;
                }
        }

        // pad → the symbol pin bonded to it, as (declaration index, name). The INDEX is the identity —
        // see ComponentTerminal.SymbolPinIndex for why a name is not one.
        var pinByPad = new Dictionary<string, (int Index, string Name)>(StringComparer.Ordinal);
        var padsByPin = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        void Join(int index, string pin, string pad)
        {
            if (pad.Length == 0) return;
            pinByPad.TryAdd(pad, (index, pin));
            string key = KeyOf(index, pin);
            if (!padsByPin.TryGetValue(key, out var pads)) padsByPin[key] = pads = [];
            if (!pads.Contains(pad)) pads.Add(pad);
        }

        // ── The two spellings of the map ────────────────────────────────────────────────────────
        //
        // A separate table names the pin, and it is the one spelling that can bond ONE pin to several
        // pads — so its rows are resolved to declarations by name, in order: the k-th row naming a name
        // takes the k-th declaration of it, and rows past the last declaration bond to that one. Two
        // pins called VSS with two rows are therefore two terminals, while one pin called GND with two
        // rows (the format's own `GND@1`/`GND@2`, already stripped) is one pin on two pads.
        foreach (var group in part.ConnectTable.GroupBy(c => c.Pin, StringComparer.Ordinal))
        {
            var declarations = Enumerable.Range(0, symbolPins.Count)
                .Where(i => string.Equals(symbolPins[i].Name, group.Key, StringComparison.Ordinal))
                .ToList();
            int k = 0;
            foreach (var c in group)
            {
                int declaration = declarations.Count == 0 ? -1 : declarations[Math.Min(k, declarations.Count - 1)];
                Join(declaration < 0 ? -1 : identity[declaration], c.Pin, c.Pad);
                k++;
            }
        }

        // The other spelling states the pad inside the pin, so each DECLARATION joins its own pad and
        // no name is consulted at all.
        for (int i = 0; i < symbolPins.Count; i++)
            if (symbolPins[i].PadName is { Length: > 0 } pad) Join(identity[i], symbolPins[i].Name, pad);

        // ── Padded terminals first, in pad order ────────────────────────────────────────────────
        var padded = new List<string>(padOrder.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pad in padOrder)
            if (pad.Length > 0 && seen.Add(pad)) padded.Add(pad);

        // A pad the map names but the footprint does not is still a terminal: the two halves can
        // disagree, and neither side is invented or dropped (R-PL1-11).
        foreach (var pad in pinByPad.Keys)
            if (seen.Add(pad)) padded.Add(pad);

        // Numerals ascending numerically, then everything else in the order the footprint stated it —
        // ordered by the stated INDEX rather than by returning 0 from a comparison, because
        // List<T>.Sort is not stable and "keep the stated order" would then be luck. OrderBy is.
        padded =
        [
            .. padded
                .Select((pad, i) => (Pad: pad, Index: i, IsNumeric: long.TryParse(pad, out long v), Value: v))
                .OrderBy(t => t.IsNumeric ? 0 : 1)
                .ThenBy(t => t.IsNumeric ? t.Value : 0)
                .ThenBy(t => t.Index)
                .Select(t => t.Pad),
        ];

        var terminals = new List<ComponentTerminal>(padded.Count);
        int index = 1;
        int padsWithNoPin = 0;
        var pinPlaced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pad in padded)
        {
            (int Index, string Name)? pin = pinByPad.TryGetValue(pad, out var p) ? p : null;

            // A pin bonded to SEVERAL pads is ONE declaration, so only its first pad carries the symbol
            // pin. The rest are real terminals with real copper and no drawn pin of their own —
            // reported, never dropped and never invented (R-PL1-11).
            if (pin is { } joined && !pinPlaced.Add(KeyOf(joined.Index, joined.Name)))
            {
                pin = null;
                padsWithNoPin++;
            }
            else if (pin is null) padsWithNoPin++;

            terminals.Add(new ComponentTerminal(index++, pad, pin?.Name, pin?.Index ?? -1));
        }

        // ── Then the symbol pins that joined to nothing, in declaration order ───────────────────
        int pinsWithNoPad = 0;
        for (int i = 0; i < symbolPins.Count; i++)
        {
            string key = KeyOf(identity[i], symbolPins[i].Name);
            if (padsByPin.ContainsKey(key)) continue;
            if (!pinPlaced.Add(key)) continue;
            pinsWithNoPad++;
            terminals.Add(new ComponentTerminal(index++, null, symbolPins[i].Name, identity[i]));
        }

        return new Result(terminals, pinsWithNoPad, padsWithNoPin);
    }

    /// <summary>The identity of one symbol pin: its declaration when it has one, its name only when
    /// the map named a pin the symbol does not declare.</summary>
    private static string KeyOf(int index, string name) => index >= 0 ? "#" + index : "$" + name;
}

// One page stream of a version-3.2 design — brief-schimp-4 R-si4-3. Layout in src/Design/RESOLVED.md.
//
// The SAME sections in the same order as version 2 (DsnPageV2), every item now a sized record (DsnBytes.Record) whose
// body opens with a counted block (DsnBytes.SkipBlob) — so a section this reader does not read is SKIPPED by its sizes and
// reported, never misread:
//
//   page record  0x0A; body: u32 0, NAME, SIZE ("A3"), two timestamps, the same 150-byte block (width and height in the
//                unit its grid is stated in: µm with grid 2540, or 1/1000 inch with grid 100), then the
//                title block(s) as 0x41 records
//   nets         u16 count of 0x34 records
//   buses        u16 count of 0x35 records, each FOLLOWED (outside its size) by u16 n and n × u32 member net ids
//   net names    u16 count of (name, u32 net id), plain as in version 2; every net is named, auto-named ones "N…"
//   wires        u16 count of 0x14 records (0x15: a bus's wire, the same layout, skipped): u32 id, u32 NET, u32 colour, x1 y1 x2 y2 (i32), a byte, u16 count of ALIAS
//                records (u32 0, x y (i32), u32 colour, u32 rotation, u32, NAME), display properties, 8 bytes
//   parts        u16 count of records: 0x0D parts (see Part) and 0x0C hierarchical blocks (see Block)
//   ports        u16 count of 0x17 hierarchical ports (see Port); empty on a page with none
//   globals      u16 count of 0x25 records, each followed by u32 WIRE and a byte
//   off-page     u16 count of 0x26 records, each followed by u32 WIRE and a byte — the same layout as a global
//   (lists)      u16 counts of records: two empty, then the page's text and graphics, then two empty
//
// Coordinates are 1/100 inch, Y down, as in version 2.

namespace CircuitRF.Design.Schematic.Import.BinaryDsn;

internal static class DsnPageV3
{
    private const int FixedHeader = 150;

    public static DsnPage Read(DsnBytes r, DsnCache cache)
    {
        var (pageEnd, _, _) = r.Record(0x0A);
        r.SkipBlob();
        string name = r.Str();
        r.Str();                                 // the page size's name
        r.Skip(8);
        int fixedAt = r.Pos;
        r.Skip(16);
        int width = r.I32(), height = r.I32(), grid = r.I32();
        r.Pos = fixedAt + FixedHeader;
        while (r.Peek() == 0x41 && IsRecord(r)) r.Pos = r.Record().End;

        var skipped = new List<string>();
        r.SkipRecords();                         // nets: the wires name theirs
        Buses(r);
        int na = r.U16();
        var netNames = new List<(string, uint)>(na);
        for (int i = 0; i < na; i++) netNames.Add((r.Str(), r.U32()));

        int nw = r.U16();
        var wires = new List<DsnWire>(nw);
        var aliases = new List<DsnAlias>();
        int busWires = 0;
        for (int i = 0; i < nw; i++)
        {
            // 0x15 is a bus's wire: a wire's layout on the bus's own net. Its members are joined by their names, so it is
            // not drawn as a wire, which would join every member it touches.
            if (r.Peek() == 0x15) { r.Pos = r.Record().End; busWires++; continue; }
            wires.Add(Wire(r, aliases));
        }
        if (busWires > 0) skipped.Add($"{busWires} bus wire segment(s) on page {name} were not drawn; the nets on the bus are joined by name.");
        int np = r.U16();
        var parts = new List<DsnPart>(np);
        var blocks = new List<DsnBlock>();
        for (int i = 0; i < np; i++)
            if (r.Peek() == 0x0C) blocks.Add(Block(r, cache));
            else parts.Add(Part(r));
        var ports = new List<DsnGlobal>();
        int nports = r.U16();
        for (int i = 0; i < nports; i++)
            if (r.Peek() == 0x17) ports.Add(Port(r));
            else
            {
                skipped.Add($"An item of type 0x{r.Peek():X2} among page {name}'s ports was not read; this reader has not seen one.");
                r.Pos = r.Record().End;
            }
        var globals = Globals(r, 0x25);
        var offPages = Globals(r, 0x26);
        Unread(r, "first list after the off-page connectors", skipped);
        Unread(r, "second list after the off-page connectors", skipped);
        int graphics = r.SkipRecords();
        if (graphics > 0) skipped.Add($"{graphics} text or graphic item(s) on page {name} were not imported.");
        while (r.Pos + 2 <= pageEnd) Unread(r, "list at the end of the page", skipped);
        return new DsnPage(name, width, height, grid, netNames, wires, parts, globals)
        {
            OffPages = offPages,
            Aliases = aliases,
            Blocks = blocks,
            Ports = ports,
            Skipped = skipped,
        };
    }

    private static bool IsRecord(DsnBytes r)
    {
        int at = r.Pos;
        if (at + 9 > r.Length) return false;
        uint size = BitConverter.ToUInt32(r.Bytes, at + 1), zero = BitConverter.ToUInt32(r.Bytes, at + 5);
        return zero == 0 && at + 9 + (long)size <= r.Length;
    }

    /// <summary>A list this reader does not read: skipped by its sizes, and said.</summary>
    private static void Unread(DsnBytes r, string what, List<string> skipped)
    {
        int at = r.Pos;
        int n = r.SkipRecords();
        if (n > 0) skipped.Add($"{n} item(s) in the page's {what} (at byte {at}) were not read; this reader has not seen one.");
    }

    /// <summary>
    /// The list after the nets: 0x35 records, one per bus (DISP_C[0-3] on a field design, with four members), each followed
    /// OUTSIDE its stated size by a u16 count and that many u32 member net ids. Skipping the record by its size alone left
    /// the cursor on the member list, read as the next record's header. Nothing here is needed: a bus's members are joined
    /// by their names, as the wires and aliases carry them. Any other record is skipped by its size, as before.
    /// </summary>
    private static void Buses(DsnBytes r)
    {
        int n = r.U16();
        for (int i = 0; i < n; i++)
        {
            byte type = r.Peek();
            r.Pos = r.Record().End;
            if (type != 0x35) continue;
            int members = r.U16();
            if (r.Pos + 4L * members > r.Length) throw r.Fail($"a bus claims {members} member nets");
            r.Skip(4 * members);
        }
    }

    private static DsnWire Wire(DsnBytes r, List<DsnAlias> aliases)
    {
        var (end, _, _) = r.Record(0x14);
        r.SkipBlob();
        uint id = r.U32(), net = r.U32();
        r.U32();                                 // colour
        var a = (r.I32(), r.I32());
        var b = (r.I32(), r.I32());
        r.U8();
        int n = r.U16();
        for (int i = 0; i < n; i++)
        {
            var (aliasEnd, _, _) = r.Record();
            r.SkipBlob();
            var at = (r.I32(), r.I32());
            r.Skip(12);                          // colour, rotation, font
            aliases.Add(new DsnAlias(r.Str(), at, id));
            r.Pos = aliasEnd;
        }
        r.Pos = end;
        return new DsnWire(id, net, a, b);
    }

    /// <summary>
    /// 0x0D with the instance's (name, value) property pairs in its header; body: the counted block (37 bytes on most
    /// parts, holding "1 9 Reference 0 0  0  "; empty on some), two u32s, the symbol NAME, u32 INSTANCE id (DsnOccurrences), 8 bytes, x y (i16), colour, ROTATION (as version 2), 2 bytes,
    /// display-property records, a byte, the REFERENCE, u32 VALUE (string index), 8 bytes, u16, u16 pin count, the pin
    /// records (the counted block, u16 index, x y (i16), u32 WIRE, …), the source part's name, u16 UNIT (0-based).
    /// </summary>
    private static DsnPart Part(DsnBytes r)
    {
        var (end, _, pairs) = r.Record(0x0D);
        r.SkipBlob();
        r.Skip(8);
        string symbol = r.Str();
        uint instance = r.U32();
        r.Skip(8);
        var pos = ((int)r.I16(), (int)r.I16());
        r.U8();
        int rot = r.U8();
        r.Skip(2);
        var display = r.DisplayRecords();
        r.U8();
        string reference = r.Str();
        int value = (int)r.U32();
        r.Skip(8);
        r.U16();
        int np = r.U16();
        if (np > 10_000) throw r.Fail($"a part claims {np} pins");
        var pins = new List<DsnPlacedPin>(np);
        for (int i = 0; i < np; i++)
        {
            var (pinEnd, _, _) = r.Record(0x10);
            r.SkipBlob();
            // Some pins store their index NEGATED (−24 for pin 24): on one field design, 20 pins of six parts, the
            // negative ones exactly the numbers missing from each part's list, most with no wire (a no-connect mark is
            // the likely flag) and one wired. Read as an unsigned number, pin 24 became pin 65512, on no symbol pin,
            // and the file's declared nets disagreed with its own drawing.
            int index = Math.Abs((int)r.I16());
            var at = ((int)r.I16(), (int)r.I16());
            uint wire = r.U32();
            pins.Add(new DsnPlacedPin(index, at, wire));
            r.Pos = pinEnd;
        }
        r.Str();                                 // the source part's name
        int unit = r.U16();
        r.Pos = end;
        return new DsnPart(reference, symbol, value, pairs, pos, rot & 3, (rot & 4) != 0, pins, unit + 1)
        {
            Display = display,
            InstanceId = instance,
        };
    }

    /// <summary>
    /// 0x0C: a part's layout up to its displayed properties and the byte after them (no symbol NAME — an empty string),
    /// then the block's own rectangle symbol as an inline 0x18 record (DsnCache.Inline), then the block's NAME ("HPF"), u32
    /// IMPLEMENTATION (string index: the schematic it places), 8 bytes, u16, u16 pin count and the pins as a part's.
    /// </summary>
    private static DsnBlock Block(DsnBytes r, DsnCache cache)
    {
        var (end, _, _) = r.Record(0x0C);
        r.SkipBlob();
        r.Skip(8);
        r.Str();
        r.U32();
        r.Skip(8);
        var pos = ((int)r.I16(), (int)r.I16());
        r.U8();
        int rot = r.U8();
        r.Skip(2);
        r.SkipRecords();                         // displayed properties
        r.U8();
        var body = cache.Inline(r);
        string name = r.Str();
        int implementation = (int)r.U32();
        r.Skip(8);
        r.U16();
        int np = r.U16();
        if (np > 10_000) throw r.Fail($"a hierarchical block claims {np} pins");
        var pins = new List<DsnPlacedPin>(np);
        for (int i = 0; i < np; i++)
        {
            var (pinEnd, _, _) = r.Record(0x10);
            r.SkipBlob();
            int index = r.U16();
            var at = ((int)r.I16(), (int)r.I16());
            pins.Add(new DsnPlacedPin(index, at, r.U32()));
            r.Pos = pinEnd;
        }
        r.Pos = end;
        return new DsnBlock(name, implementation, pos, rot & 3, (rot & 4) != 0, body, pins);
    }

    /// <summary>0x17: as a global's body (u32 0, u32 NAME, u32 library, the SYMBOL's name, u32 id, 8 bytes, x y, colour,
    /// rotation), then 2 bytes, display-property records, a byte and u32 WIRE — inside the record, where a global's wire
    /// follows it.</summary>
    private static DsnGlobal Port(DsnBytes r)
    {
        var (end, _, _) = r.Record(0x17);
        r.SkipBlob();
        int net = (int)r.U32();
        r.U32();
        string symbol = r.Str();
        r.U32();
        r.Skip(8);
        var pos = ((int)r.I16(), (int)r.I16());
        r.U8();
        int rot = r.U8();
        r.Skip(2);
        r.SkipRecords();
        r.U8();
        uint wire = r.U32();
        r.Pos = end;
        return new DsnGlobal(symbol, net, pos, rot & 3, (rot & 4) != 0, wire);
    }

    /// <summary>A u16 count of records of <paramref name="type"/> (0x25 globals, 0x26 off-page connectors): body u32 0,
    /// u32 NET NAME (string index), u32 library, the SYMBOL's name, u32 id, 8 bytes, x y (i16), colour, rotation; each
    /// record followed by u32 WIRE and a byte.</summary>
    private static List<DsnGlobal> Globals(DsnBytes r, byte type)
    {
        int n = r.U16();
        var list = new List<DsnGlobal>(n);
        for (int i = 0; i < n; i++)
        {
            var (end, _, _) = r.Record(type);
            r.SkipBlob();
            int net = (int)r.U32();
            r.U32();
            string symbol = r.Str();
            r.U32();
            r.Skip(8);
            var pos = ((int)r.I16(), (int)r.I16());
            r.U8();
            int rot = r.U8();
            r.Pos = end;
            uint wire = r.U32();
            r.U8();
            list.Add(new DsnGlobal(symbol, net, pos, rot & 3, (rot & 4) != 0, wire));
        }
        return list;
    }
}

// Framework-free. No Avalonia, no SkiaSharp — `circuitrf check`, the cell Properties panel and (from
// brief 3 on) both halves of LVS read this, and none of them may pull a UI framework behind it.

using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using CircuitRF.Design.Cells;
using CircuitRF.Diagnostics;

namespace CircuitRF.Design.Layout;

/// <summary>One terminal: a schematic port, its name, and the layout pin (or PINS) that are it.</summary>
/// <param name="Port">1-based, the number both sides already use.</param>
/// <param name="Name">The terminal's own name, for reports. May be empty.</param>
/// <param name="LayoutPins">Zero, one or several entries in the primary <c>.clay</c>'s pins.</param>
public sealed record Terminal(int Port, string Name, IReadOnlyList<string> LayoutPins);

/// <summary>Which of <see cref="TerminalMap"/>'s rules produced a map.</summary>
public enum TerminalMapOrigin
{
    /// <summary>The cell's own <c>.ccell</c> says so (R-lvs1-1).</summary>
    Declared,

    /// <summary>The cell carries import provenance, so <c>ComponentTerminals</c>' own numbering
    /// applies: <c>SymbolPin.PortIndex i</c> ↔ <c>LayoutView.Pins[i-1]</c> (R-lvs1-3a).</summary>
    ImportTable,

    /// <summary>Symbol pin names and layout pin names matched, case-insensitively (R-lvs1-3b).</summary>
    ByName,

    /// <summary>Every pin on both sides is unnamed and the counts match, so the two lists were read
    /// positionally (R-lvs1-3c). <b>Always reported at warning</b>, including on an otherwise clean
    /// run — it is a guess that happens to be right most of the time, and a guess that is never
    /// announced is the shape of a wrong answer nobody finds.</summary>
    ByOrder,

    /// <summary>No map. Either there is nothing to map — a cell with no layout view, which is most
    /// cells — or the two sides disagree in a way no rule may paper over (R-lvs1-3d).</summary>
    None,
}

/// <param name="Terminals">The map, ordered by port. Empty when <paramref name="Origin"/> is
/// <see cref="TerminalMapOrigin.None"/>.</param>
/// <param name="Origin">Which rule answered. <b>Returned always, and every consumer that reports
/// anything about this cell states it</b> (R-lvs1-2b) — a derived map that does not say it was
/// derived is indistinguishable from a declared one, and the two have very different failure
/// modes.</param>
/// <param name="Notes">What the derivation had to say, in the order it said it. For
/// <see cref="TerminalMapOrigin.None"/> this names BOTH unmatched lists, because that is the part a
/// user needs in order to make the two-minute fix.</param>
public sealed record TerminalMapResult(
    IReadOnlyList<Terminal> Terminals,
    TerminalMapOrigin Origin,
    IReadOnlyList<string> Notes)
{
    /// <summary>
    /// For <see cref="TerminalMapOrigin.None"/>, the two lists <see cref="Notes"/> states in prose —
    /// carried STRUCTURALLY as well because R-lvs1-4c's panel shows them side by side, and splitting a
    /// sentence back apart to do that would be a second copy of the rule that built it. Empty for
    /// every other origin.
    /// </summary>
    public IReadOnlyList<string> UnmatchedSymbolPins { get; init; } = [];

    /// <inheritdoc cref="UnmatchedSymbolPins"/>
    public IReadOnlyList<string> UnmatchedLayoutPins { get; init; } = [];
}

/// <summary>
/// Which layout pin is which schematic port — the ONE place that question is answered
/// (<c>docs/design/lvs.md</c> §4.2, <c>brief-lvs-1-terminal-map.md</c>).
///
/// <para><b>Why it has to be answered at all.</b> The schematic orders a cell's ports by the
/// <c>Num</c> parameter on its <c>Port</c> components (<c>NetExtractor.BuildCellPorts</c>), and
/// nothing relates that to the order pins were appended to a <c>.clay</c>. <see cref="LayoutPin.Name"/>
/// is explicitly allowed to be empty. And position is not a correspondence either — a symbol's pin
/// positions and a land pattern's pad positions have no reason to agree and usually do not.</para>
///
/// <para><b>One function, because a second copy of a four-rule precedence is exactly the shape
/// <see cref="CellPins"/>' own header warns about</b> (R-lvs1-2a). It has <c>CellPins</c>' character
/// otherwise too: total, never throws, and cached on the resolved view reference.</para>
/// </summary>
public static class TerminalMap
{
    // ── Cache ────────────────────────────────────────────────────────────────
    //
    // Keyed by the resolved LayoutView REFERENCE, exactly as CellPins is — a file or in-session edit
    // produces a new reference on the next resolve, which is a miss here. The `.ccell` is the part
    // that reference does NOT cover: the Properties panel writes a terminal block without touching
    // the layout at all, so the entry also records the file's stamp and a change to it is a miss.
    // Getting that wrong would show the user the map they just replaced.

    private sealed class Entry
    {
        public required string CellDir;
        public required object? SymbolRef;
        public required long Ticks;
        public required long Length;
        public required TerminalMapResult Result;
    }

    private static readonly ConditionalWeakTable<LayoutView, Entry> Cache = new();

    /// <summary>
    /// <paramref name="cellDir"/>'s terminal map: what its <c>.ccell</c> declares, else the first of
    /// §4's four derivation rules that answers.
    ///
    /// <para><b>Absent is not a warning and not a failure</b> (R-lvs1-3) — it is a derivation with its
    /// provenance stated. Nothing here throws, nothing is retrofitted onto the cell, and nothing is
    /// written: this is a reader.</para>
    /// </summary>
    /// <param name="symbol">The cell's PRIMARY symbol, or null when it has none.</param>
    /// <param name="layout">The cell's PRIMARY layout, or null when it has none — which is most
    /// cells, and is why a missing layout is silence rather than a finding.</param>
    public static TerminalMapResult Resolve(
        string cellDir, CircuitRF.Design.Symbol.Symbol? symbol, LayoutView? layout)
    {
        var stamp = StampOf(CcellPathOf(cellDir));

        if (layout is not null
            && Cache.TryGetValue(layout, out var cached)
            && string.Equals(cached.CellDir, cellDir, System.StringComparison.Ordinal)
            && ReferenceEquals(cached.SymbolRef, symbol)
            && cached.Ticks == stamp.Ticks
            && cached.Length == stamp.Length)
            return cached.Result;

        var result = ResolveUncached(cellDir, symbol, layout);

        if (layout is not null)
            Cache.AddOrUpdate(layout, new Entry
            {
                CellDir   = cellDir,
                SymbolRef = symbol,
                Ticks     = stamp.Ticks,
                Length    = stamp.Length,
                Result    = result,
            });

        return result;
    }

    private static TerminalMapResult ResolveUncached(
        string cellDir, CircuitRF.Design.Symbol.Symbol? symbol, LayoutView? layout)
    {
        var ccell = ReadCcell(cellDir);

        // ── Declared ────────────────────────────────────────────────────────
        //
        // An EMPTY list is not a declaration — it is CellFolder.CreateCellFolder's "no terminals
        // yet", and a cell that has since been drawn has terminals it does not know about.
        if (ccell?.Terminals is { Count: > 0 } rows)
            return new TerminalMapResult(
                [.. rows.OrderBy(r => r.Port).Select(r => new Terminal(r.Port, r.Name, [.. r.LayoutPin]))],
                TerminalMapOrigin.Declared,
                []);

        var symbolPins = symbol?.Pins ?? [];
        var layoutPins = layout is null ? [] : CellPins.Resolve(layout, tech: null);

        // ── Nothing to map ──────────────────────────────────────────────────
        //
        // A cell with no layout view is MOST cells, and a cell with no symbol cannot have ports. In
        // neither case do the two sides disagree about anything, so neither is R-lvs1-3d's failure —
        // reporting one would bury the real findings under one per cell in the workspace.
        if (layout is null)
            return new TerminalMapResult([], TerminalMapOrigin.None, ["This cell has no layout view, so it has no terminals to map."]);
        if (symbol is null)
            return new TerminalMapResult([], TerminalMapOrigin.None, ["This cell has no symbol view, so it declares no ports to map its layout pins to."]);
        if (symbolPins.Count == 0 && layoutPins.Count == 0)
            return new TerminalMapResult([], TerminalMapOrigin.None, ["Neither view has any pins."]);

        // ── R-lvs1-3a — the import table ────────────────────────────────────
        if (ccell?.ImportedFrom is not null && symbolPins.Count > 0 && layoutPins.Count > 0)
            return FromImportTable(symbolPins, layoutPins, ccell.NumPorts);

        // ── R-lvs1-3b — by name ─────────────────────────────────────────────
        if (ByName(symbolPins, layoutPins) is { } byName) return byName;

        // ── R-lvs1-3c — by order ────────────────────────────────────────────
        //
        // ONLY when every pin on both sides is unnamed and the counts match exactly. R-lvs1-3e: the
        // partial-name case must NOT fall through to here. Three of five names matching is evidence
        // the author meant them to match and got two wrong, and reading the whole thing positionally
        // would produce a confident wrong answer over a visible clue.
        if (symbolPins.Count == layoutPins.Count
            && symbolPins.All(p => string.IsNullOrEmpty(p.Name))
            && layoutPins.All(p => p.Name.Length == 0))
            return new TerminalMapResult(
                [.. Enumerable.Range(0, symbolPins.Count)
                    .Select(i => new Terminal(PortOf(symbolPins[i], i), "", [PinKey(layoutPins, i)]))],
                TerminalMapOrigin.ByOrder,
                [$"No pin on either side is named, and both sides have {symbolPins.Count} of them, so they were read positionally."]);

        // ── R-lvs1-3d — nothing ─────────────────────────────────────────────
        return Underivable(symbolPins, layoutPins);
    }

    /// <summary>
    /// The same answer for a cell folder alone — it reads the cell's own primary symbol and primary
    /// layout. <b>One place</b>, so <c>check</c>, the Properties panel and (from brief 3) LVS itself
    /// resolve the same two files rather than each choosing its own.
    /// </summary>
    public static TerminalMapResult ResolveCell(string cellDir)
    {
        var (symbol, layout) = PrimaryViewsOf(cellDir);
        return Resolve(cellDir, symbol, layout);
    }

    /// <summary><see cref="Validate"/> for a cell folder alone. The finding names the folder.</summary>
    public static IReadOnlyList<Diagnostic> ValidateCell(string cellDir)
    {
        var (symbol, layout) = PrimaryViewsOf(cellDir);
        return Validate(cellDir, cellDir, symbol, layout);
    }

    /// <summary>
    /// A cell's primary symbol and primary layout, or null for either when it has none or the file
    /// cannot be read.
    ///
    /// <para><b>Unreadable is null rather than a finding here.</b> Every view file is validated on its
    /// own account by whoever is walking the cell, and reporting one broken file twice under two ids
    /// helps nobody.</para>
    /// </summary>
    public static (CircuitRF.Design.Symbol.Symbol? Symbol, LayoutView? Layout) PrimaryViewsOf(string cellDir)
        => (ReadPrimary(cellDir, ViewType.Symbol, CircuitRF.Design.Symbol.SymbolPersistence.LoadFromFile),
            ReadPrimary(cellDir, ViewType.Layout, LayoutPersistence.LoadFromFile));

    private static T? ReadPrimary<T>(string cellDir, ViewType view, System.Func<string, T> load) where T : class
    {
        try
        {
            var primary = CellFolder.ResolvePrimary(cellDir, view);
            if (primary.ResolvedName is not { Length: > 0 } name) return null;

            string path = System.IO.Path.Combine(CellFolder.SubFolderPath(cellDir, view), name);
            return System.IO.File.Exists(path) ? load(path) : null;
        }
        catch (System.Exception) { return null; }
    }

    // ── The derivations ──────────────────────────────────────────────────────

    /// <summary>
    /// R-lvs1-3a. <c>ComponentTerminals.Build</c> decided ONE numbering for both views, and
    /// <c>ComponentImport.AddPins</c> wrote the layout pins in that order — so
    /// <c>SymbolPin.PortIndex i</c> and <c>LayoutView.Pins[i-1]</c> name the same terminal. This is
    /// the guaranteed case; it needs writing down, not recomputing.
    ///
    /// <para>The one place the positional rule stops short is a terminal with NO pad — a symbol pin
    /// the source's map joined to nothing. The import drops those from the pin list, so every port
    /// after one is off by one. It is reported rather than silently trusted: the counts are in the
    /// note, and an import performed from this brief on writes the block outright and never reaches
    /// here at all.</para>
    /// </summary>
    private static TerminalMapResult FromImportTable(
        IReadOnlyList<CircuitRF.Design.Symbol.SymbolPin> symbolPins, IReadOnlyList<LayoutPin> layoutPins,
        int numPorts)
    {
        // The table is PAD-ordered and numbered 1..N, so the walk is over PORTS rather than over symbol
        // pins: a pad no symbol pin references — a mounting or shield pad — is a real terminal with a
        // real port number, and walking the symbol would drop it and then report its pin as unmapped.
        int ports = numPorts > 0 ? numPorts : System.Math.Max(symbolPins.Count, layoutPins.Count);

        var nameByPort = new Dictionary<int, string>();
        for (int i = 0; i < symbolPins.Count; i++)
            nameByPort.TryAdd(PortOf(symbolPins[i], i), symbolPins[i].Name ?? "");

        var notes = new List<string>
        {
            "This cell was imported, so its symbol pin numbering and its layout pin order were decided "
            + "together and are read positionally.",
        };
        if (ports != layoutPins.Count)
            notes.Add($"It declares {ports} port(s) and the layout has {layoutPins.Count} pin(s); "
                      + "ports past the shorter list are unmapped.");

        var terminals = new List<Terminal>(ports);
        for (int port = 1; port <= ports; port++)
            terminals.Add(new Terminal(
                port,
                nameByPort.TryGetValue(port, out string? name) ? name : "",
                port - 1 < layoutPins.Count ? [PinKey(layoutPins, port - 1)] : []));

        return new TerminalMapResult(terminals, TerminalMapOrigin.ImportTable, notes);
    }

    /// <summary>
    /// R-lvs1-3b, and the rule that turns <c>pcell-contract.md</c> R3's unenforced sentence into a
    /// checked one. Case-insensitive, ordinal-ignore-case, between <c>SymbolPin.Name</c> and
    /// <c>LayoutPin.Name</c>.
    ///
    /// <para>Answers only when EVERY symbol pin is named and every one of those names is a layout pin
    /// (one, or several — a bonded ground is several pins of one name). Layout pins no symbol pin
    /// names are fine and stay out of the map: a mounting or shield pad is exactly that. Anything
    /// less is R-lvs1-3e's partial case and returns null rather than a map.</para>
    /// </summary>
    private static TerminalMapResult? ByName(
        IReadOnlyList<CircuitRF.Design.Symbol.SymbolPin> symbolPins, IReadOnlyList<LayoutPin> layoutPins)
    {
        if (symbolPins.Count == 0) return null;
        if (symbolPins.Any(p => string.IsNullOrEmpty(p.Name))) return null;

        var byName = new Dictionary<string, List<string>>(System.StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < layoutPins.Count; i++)
        {
            if (layoutPins[i].Name.Length == 0) continue;
            if (!byName.TryGetValue(layoutPins[i].Name, out var list)) byName[layoutPins[i].Name] = list = [];
            list.Add(PinKey(layoutPins, i));
        }

        var terminals = new List<Terminal>(symbolPins.Count);
        for (int i = 0; i < symbolPins.Count; i++)
        {
            if (!byName.TryGetValue(symbolPins[i].Name!, out var pins)) return null;
            terminals.Add(new Terminal(PortOf(symbolPins[i], i), symbolPins[i].Name!, [.. pins]));
        }

        return new TerminalMapResult(
            [.. terminals.OrderBy(t => t.Port)],
            TerminalMapOrigin.ByName,
            ["Every symbol pin name is a layout pin name, so the two were matched by name."]);
    }

    /// <summary>
    /// R-lvs1-3d. <b>The note names BOTH lists</b> — the unmatched symbol pins and the unmatched
    /// layout pins, by name — because a user can only make the two-minute fix if they can see both.
    /// </summary>
    private static TerminalMapResult Underivable(
        IReadOnlyList<CircuitRF.Design.Symbol.SymbolPin> symbolPins, IReadOnlyList<LayoutPin> layoutPins)
    {
        var layoutNames = new HashSet<string>(
            layoutPins.Where(p => p.Name.Length > 0).Select(p => p.Name), System.StringComparer.OrdinalIgnoreCase);
        var symbolNames = new HashSet<string>(
            symbolPins.Where(p => !string.IsNullOrEmpty(p.Name)).Select(p => p.Name!), System.StringComparer.OrdinalIgnoreCase);

        var unmatchedSymbol = symbolPins
            .Select((p, i) => string.IsNullOrEmpty(p.Name) ? $"(unnamed, port {PortOf(p, i)})" : p.Name!)
            .Where(n => n.StartsWith('(') || !layoutNames.Contains(n))
            .ToList();
        var unmatchedLayout = layoutPins
            .Select((p, i) => p.Name.Length == 0 ? $"(unnamed, #{i + 1})" : p.Name)
            .Where(n => n.StartsWith('(') || !symbolNames.Contains(n))
            .ToList();

        return new TerminalMapResult([], TerminalMapOrigin.None,
        [
            $"The symbol declares {symbolPins.Count} pin(s) and the layout has {layoutPins.Count}, and they "
            + "neither match by name nor qualify to be read positionally.",
            "Symbol pins with no layout pin: " + Join(unmatchedSymbol),
            "Layout pins with no symbol pin: " + Join(unmatchedLayout),
        ])
        {
            UnmatchedSymbolPins = unmatchedSymbol,
            UnmatchedLayoutPins = unmatchedLayout,
        };
    }

    // ── Validation (R-lvs1-4) ────────────────────────────────────────────────

    /// <summary>
    /// Every finding <c>circuitrf check</c> reports about a cell's terminals, and the same ones the
    /// cell Properties panel shows — <b>authored here, once</b> (R-lvs1-4c: a rule that exists only
    /// in <c>check</c> is a rule the application does not enforce, so a design would pass headlessly
    /// and be refused when someone opened it).
    ///
    /// <para><c>check</c> adds the walk and the reporting and no rule of its own.</para>
    /// </summary>
    /// <param name="path">What the finding names — the cell folder.</param>
    public static IReadOnlyList<Diagnostic> Validate(
        string path, string cellDir, CircuitRF.Design.Symbol.Symbol? symbol, LayoutView? layout,
        TerminalMapResult? resolved = null)
    {
        var findings = new List<Diagnostic>();
        var ccell = ReadCcell(cellDir);
        var map = resolved ?? Resolve(cellDir, symbol, layout);

        var layoutPins = layout is null ? [] : CellPins.Resolve(layout, tech: null);
        var pinKeys = new HashSet<string>(
            Enumerable.Range(0, layoutPins.Count).Select(i => PinKey(layoutPins, i)), System.StringComparer.OrdinalIgnoreCase);

        int numPorts = ccell?.NumPorts > 0 ? ccell.NumPorts : symbol?.Pins.Count ?? 0;

        // ── The declared block's own defects ────────────────────────────────
        if (ccell?.Terminals is { Count: > 0 } rows)
        {
            var seenPort = new HashSet<int>();
            var claimed = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);

            foreach (var row in rows)
            {
                if (row.Port < 1 || (numPorts > 0 && row.Port > numPorts))
                    findings.Add(TerminalDiagnostics.PortOutOfRange(path, row.Port, numPorts));

                if (!seenPort.Add(row.Port))
                    findings.Add(TerminalDiagnostics.DuplicatePort(path, row.Port));

                foreach (var pin in row.LayoutPin)
                {
                    if (layout is not null && !pinKeys.Contains(pin))
                        findings.Add(TerminalDiagnostics.UnknownLayoutPin(path, row.Port, pin));

                    if (claimed.TryGetValue(pin, out int firstPort))
                        findings.Add(TerminalDiagnostics.PinClaimedTwice(path, pin, firstPort, row.Port));
                    else
                        claimed[pin] = row.Port;
                }
            }
        }

        // ── What the derivation had to say ──────────────────────────────────
        if (map.Origin == TerminalMapOrigin.ByOrder)
            findings.Add(TerminalDiagnostics.DerivedByOrder(path, map.Terminals.Count));

        // R-lvs1-3d only. "No layout view" and "no symbol view" are NOT failures: most cells have no
        // layout, and one finding per cell in the workspace would bury every real one.
        if (map.Origin == TerminalMapOrigin.None && symbol is not null && layout is not null
            && (symbol.Pins.Count > 0 || layoutPins.Count > 0))
            findings.Add(TerminalDiagnostics.Underivable(path, map.Notes));

        // ── Coverage, on whatever map came back ─────────────────────────────
        //
        // Only where there are two sides to cover. A cell with no layout has nothing unmapped.
        if (layout is not null && map.Origin != TerminalMapOrigin.None)
        {
            var mappedPorts = new HashSet<int>(map.Terminals.Where(t => t.LayoutPins.Count > 0).Select(t => t.Port));
            foreach (int port in Enumerable.Range(1, numPorts).Where(p => !mappedPorts.Contains(p)))
                findings.Add(TerminalDiagnostics.UnmappedPort(path, port));

            var mappedPins = new HashSet<string>(
                map.Terminals.SelectMany(t => t.LayoutPins), System.StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < layoutPins.Count; i++)
                if (!mappedPins.Contains(PinKey(layoutPins, i)))
                    findings.Add(TerminalDiagnostics.UnmappedPin(path, PinKey(layoutPins, i)));
        }

        return findings;
    }

    // ── Authoring (R-lvs1-5) ─────────────────────────────────────────────────

    /// <summary>
    /// The block an authoring path writes: one row per terminal, ports ascending. Used by
    /// <c>ComponentImport</c> and by the generated-PCell store, so the two write the same shape and a
    /// reader has one thing to read.
    /// </summary>
    public static List<CcellTerminal> ToBlock(IEnumerable<Terminal> terminals)
        => [.. terminals.OrderBy(t => t.Port)
               .Select(t => new CcellTerminal { Port = t.Port, Name = t.Name, LayoutPin = [.. t.LayoutPins] })];

    /// <summary>
    /// R-lvs1-5b's pairing, and the refusal that goes with it: a generated cell's own pins against the
    /// symbol its generator is registered with, by NAME, which is what <c>pcell-contract.md</c> R3
    /// already requires. Returns the map; <paramref name="refusal"/> names both lists when the two
    /// disagree, and the caller must not write the cell.
    ///
    /// <para><b>The refusal is the feature.</b> A generator whose pin names do not match its symbol's
    /// used to produce a cell that silently could not be compared — the mismatch surfaced, if at all,
    /// as wrong connectivity much later.</para>
    /// </summary>
    /// <param name="symbolPinNames">The registered symbol's pin names in PORT order, or an empty list
    /// when the generator has no registered symbol — a land pattern has none, and its own pin order is
    /// then the port order.</param>
    public static IReadOnlyList<Terminal>? FromGeneratorPins(
        IReadOnlyList<string> generatorPinNames, IReadOnlyList<string> symbolPinNames, out string? refusal)
    {
        refusal = null;

        if (symbolPinNames.Count == 0)
            return [.. generatorPinNames.Select((n, i) => new Terminal(i + 1, n, [n]))];

        var generated = new Dictionary<string, List<string>>(System.StringComparer.OrdinalIgnoreCase);
        foreach (string name in generatorPinNames.Where(n => n.Length > 0))
        {
            if (!generated.TryGetValue(name, out var list)) generated[name] = list = [];
            list.Add(name);
        }

        var missing = symbolPinNames.Where(n => !generated.ContainsKey(n)).ToList();
        var extra = generatorPinNames
            .Where(n => !symbolPinNames.Contains(n, System.StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (missing.Count > 0 || extra.Count > 0)
        {
            refusal =
                "the generator's pin names do not match its symbol's. "
                + "Symbol pins with no generated pin: " + Join(missing) + ". "
                + "Generated pins the symbol does not declare: " + Join(extra) + ". "
                + "pcell-contract.md R3 requires them to agree, or the schematic and the layout "
                + "disagree about connectivity.";
            return null;
        }

        return [.. symbolPinNames.Select((n, i) => new Terminal(i + 1, n, [.. generated[n]]))];
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>The name a terminal row uses for layout pin <paramref name="index"/>. An UNNAMED pin
    /// still has to be referable — it is a real connection point, just one nobody named — so it is
    /// keyed by its 1-based position, which is the only handle it has.
    ///
    /// <para><b>The rule lives in <see cref="CircuitRF.Design.Layout.Extraction.PlacedPins.PinKeyOf"/>
    /// and is CALLED, not copied</b> (R-lvs3-5). LVS joins a terminal's <c>LayoutPin</c> list to a
    /// projected pad by comparing these strings; two spellings of one key would match nothing and
    /// read as every device being open.</para></summary>
    private static string PinKey(IReadOnlyList<LayoutPin> pins, int index)
        => CircuitRF.Design.Layout.Extraction.PlacedPins.PinKeyOf(pins, index);

    /// <summary>A symbol pin's port number: its own <c>PortIndex</c> when it states one, else its
    /// 1-based position. A <c>.csym</c> written before port indices were assigned has zeroes.</summary>
    private static int PortOf(CircuitRF.Design.Symbol.SymbolPin pin, int index)
        => pin.PortIndex > 0 ? pin.PortIndex : index + 1;

    private static string Join(IReadOnlyList<string> names)
        => names.Count == 0 ? "(none)" : string.Join(", ", names);

    private static string CcellPathOf(string cellDir)
        => System.IO.Path.Combine(cellDir, CellFolder.CcellFileName);

    private static (long Ticks, long Length) StampOf(string path)
    {
        try
        {
            var info = new System.IO.FileInfo(path);
            return info.Exists ? (info.LastWriteTimeUtc.Ticks, info.Length) : (0, 0);
        }
        catch (System.IO.IOException) { return (0, 0); }
        catch (System.UnauthorizedAccessException) { return (0, 0); }
    }

    /// <summary>The cell's <c>.ccell</c>, or null when there is not one or it cannot be read. Never
    /// throws: R-lvs1-1f makes an unreadable block an ABSENT map, which is a defined state that
    /// derives — and every other reader of this file reports its own defect on its own account.</summary>
    private static CcellFile? ReadCcell(string cellDir)
    {
        try
        {
            string path = CcellPathOf(cellDir);
            return System.IO.File.Exists(path) ? CellPersistence.LoadFromFile(path) : null;
        }
        catch (System.Exception) { return null; }
    }
}

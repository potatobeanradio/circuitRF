using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using CircuitRF.Core.Devices;
using CircuitRF.Design.Modeling;
using CircuitRF.Design.Symbol;
namespace CircuitRF.Design.Schematic;

// See SpiceModelSymbolProvider for why this alias sits inside the namespace.
using Symbol = global::CircuitRF.Design.Symbol.Symbol;

/// <summary>
/// The symbol of a placed <see cref="SymbolKind.VerilogA"/> when it is not the box (brief-vendorlib-7): the one its
/// <c>Symbol</c> parameter names, or — blank — the FET glyph, when the model's own terminal names say which pin is
/// the gate, drain and source.
///
/// <para><b>Why the box is still the default for most files.</b> circuitRF does not know what a compiled model is.
/// The person who placed it does, and the model's own terminal names are the model saying it; blank therefore
/// means the FET glyph only when the connected terminal count is 3 or 4 AND <see cref="FetPinBinding"/> binds every
/// one of them by name. Anything else is the box, as before. The default never guesses.</para>
///
/// <para><b>Everything it decides from is STORED on the instance</b> — <c>Symbol</c>, <c>Pins</c> and
/// <c>Terminals</c>, the model's terminal names as the editor last read them. The binding decides which wire lands
/// on which terminal, so it cannot depend on <see cref="VerilogAModelIntrospection"/>'s per-user cache: a headless
/// run has no cache, and a recompiled model drops its entry, and either would move the pins under the wires.</para>
///
/// <para><b>An instance with no <c>Symbol</c> parameter at all is the box.</b> It was saved before the parameter
/// existed and its wires sit on the box's pins; a new placement is seeded with a blank one, which is the default.
/// </para>
///
/// <para><b>How the binding reaches the netlist.</b> Each glyph pin's <see cref="SymbolPin.PortIndex"/> is the
/// MODEL TERMINAL it binds to, and the extractor lists a built-in component's nets in PortIndex order — so
/// <c>d g s</c> in the model and <c>g d s</c> on the glyph give the same netlist line as the box wired the same
/// way, with nothing special in the extractor.</para>
/// </summary>
public static class VerilogASymbolProvider
{
    public const string Scheme = "veriloga://";

    /// <summary>The instance parameter naming the symbol.</summary>
    public const string SymbolParameter = ComponentModelFactory.VerilogASymbolParam;

    /// <summary>The instance parameter holding the model's terminal names.</summary>
    public const string TerminalsParameter = ComponentModelFactory.VerilogATerminalsParam;

    /// <summary>The <c>Symbol</c> value that draws the box whatever the model's terminals are — the kind's own name.</summary>
    public const string BoxName = nameof(SymbolKind.VerilogA);

    /// <summary>Marks a terminal the model declares thermal in a stored <c>Terminals</c> list.</summary>
    private const string ThermalMark = ":thermal";

    private const char Separator = '|';

    // ── Terminals: what the editor stores from the model ─────────────────────

    /// <summary>
    /// The <c>Terminals</c> value for a model's terminal labels: names separated by spaces, in the model's order; a
    /// terminal the model names nothing is written as its 1-based number, and one it declares THERMAL carries
    /// <c>:thermal</c> — the discipline is part of what binds a fourth pin and is not always in the name.
    /// </summary>
    public static string FormatTerminals(IReadOnlyList<string> labels, IReadOnlyList<int> thermal)
        => string.Join(' ', labels.Select((l, i) =>
               (string.IsNullOrWhiteSpace(l) ? (i + 1).ToString(CultureInfo.InvariantCulture) : l.Trim())
               + (thermal.Contains(i) ? ThermalMark : "")));

    /// <summary>The names and thermal indices a stored <c>Terminals</c> value holds; empty when it holds none.</summary>
    public static (IReadOnlyList<string> Names, IReadOnlyList<int> Thermal) ParseTerminals(string? text)
    {
        var names   = new List<string>();
        var thermal = new List<int>();
        foreach (string word in Unquote(text).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (word.EndsWith(ThermalMark, StringComparison.OrdinalIgnoreCase))
            {
                thermal.Add(names.Count);
                names.Add(word[..^ThermalMark.Length]);
            }
            else names.Add(word);
        }
        return (names, thermal);
    }

    // ── The reference ────────────────────────────────────────────────────────

    /// <summary>
    /// The reference an instance draws through, or null when it draws the BOX — which then stays on its own
    /// built-in path, unchanged. <paramref name="symbol"/> null means the instance carries no <c>Symbol</c>
    /// parameter at all (a pre-existing instance: the box); blank means the default.
    /// </summary>
    public static string? RefFor(int pins, string? terminals, string? symbol)
    {
        if (symbol is null) return null;
        string name = Unquote(symbol);
        if (name == BoxName) return null;
        if (name.Length == 0 && DefaultKind(pins, terminals) is null) return null;
        // The symbol goes LAST, so a .csym path containing the separator cannot be mistaken for a field.
        return Scheme + pins.ToString(CultureInfo.InvariantCulture) + Separator + Unquote(terminals) + Separator + name;
    }

    public static bool IsVerilogARef(string? symbolRef)
        => symbolRef is not null && symbolRef.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase);

    public static (int Pins, string Terminals, string Symbol)? Parse(string? symbolRef)
    {
        if (!IsVerilogARef(symbolRef)) return null;
        var f = symbolRef![Scheme.Length..].Split(Separator, 3);
        return f.Length == 3 && int.TryParse(f[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int pins)
            ? (pins, f[1], f[2])
            : null;
    }

    public static CellSymbolResolution Resolve(string symbolRef, string? schematicDir)
        => new() { State = CellSymbolState.Resolved, Symbol = Choose(symbolRef, schematicDir).Glyph };

    /// <summary>
    /// What this instance's symbol has to say, for the extractor to report — that a symbol it names does not
    /// resolve, or that a FET symbol was chosen on a model whose names do not bind (R-vl7-2c). Empty when nothing.
    /// </summary>
    public static IReadOnlyList<string> Diagnose(string instanceName, int pins, string? terminals, string? symbol,
                                                 string? schematicDir)
    {
        if (RefFor(pins, terminals, symbol) is not { } r) return [];
        var c = Choose(r, schematicDir);
        var said = new List<string>();
        if (c.Missing is { } missing)
            said.Add($"VerilogA '{instanceName}': its symbol '{missing}' cannot be drawn for this model; "
                     + (c.Box ? "the box is drawn." : "the standard FET symbol is drawn."));
        if (c.ByPosition)
            said.Add($"VerilogA '{instanceName}': its terminals ({string.Join(", ", c.Names)}) do not name gate, drain "
                     + "and source; the FET symbol's pins take them in order.");
        return said;
    }

    /// <summary>The glyph blank stands for: the FET glyph for three bound terminals, the thermal-pin glyph or the
    /// MOS glyph (whose fourth pin is a bulk) for four; null — the box — otherwise.</summary>
    public static SymbolKind? DefaultKind(int pins, string? terminals)
    {
        var (names, thermal) = ParseTerminals(terminals);
        if (pins is not (3 or 4) || names.Count < pins) return null;
        return FetPinBinding.For([.. names.Take(pins)], [.. thermal.Where(t => t < pins)]) is not { } b ? null
             : b.Fourth is null ? SymbolKind.FetCurtice
             : b.FourthRole == FetPinRole.Bulk ? SymbolKind.Mos1N
             : SymbolKind.ComposedFET;
    }

    // ── The choices (R-vl7-3a) ───────────────────────────────────────────────

    /// <summary>
    /// Every symbol a model with <paramref name="pins"/> connected terminals can take, as the Parameter Editor's
    /// menu lists them: blank (the default, labelled with what it draws), the box, and the transistor glyphs with
    /// that many pins. A <c>.csym</c> is chosen with Browse…, not listed.
    /// </summary>
    public static IReadOnlyList<SymbolChoice> Choices(int pins, string? terminals)
    {
        var list = new List<SymbolChoice>
        {
            new("", DefaultKind(pins, terminals) is { } k ? $"Default ({Describe(k)})" : "Default (box)", pins),
            new(BoxName, "Box", pins),
        };
        foreach (var kind in GlyphKinds(pins))
            list.Add(new(kind.ToString(), Describe(kind), pins));
        return list;
    }

    /// <summary>A label for a glyph kind, as the menu shows it.</summary>
    public static string Describe(SymbolKind kind) => kind switch
    {
        SymbolKind.FetCurtice  => "FET",
        SymbolKind.PFetCurtice => "FET, p-channel",
        SymbolKind.ComposedFET => "FET with thermal pin",
        SymbolKind.Mos1N       => "MOSFET with bulk",
        SymbolKind.Mos1P       => "MOSFET with bulk, p-channel",
        SymbolKind.VerilogA    => "box",
        _                      => kind.ToString(),
    };

    /// <summary>The transistor glyphs the menu offers for <paramref name="pins"/> terminals — one of each picture.
    /// Any built-in kind whose pins all name a transistor role is ACCEPTED as a <c>Symbol</c> value; these are the
    /// ones worth listing, since the FET laws all draw the same two pictures.</summary>
    private static IEnumerable<SymbolKind> GlyphKinds(int pins) => pins switch
    {
        3 => [SymbolKind.FetCurtice, SymbolKind.PFetCurtice],
        4 => [SymbolKind.ComposedFET, SymbolKind.Mos1N, SymbolKind.Mos1P],
        _ => [],
    };

    /// <summary>The reference a menu row's picture is drawn from — what an instance carrying that choice draws.</summary>
    public static string GlyphRefFor(int pins, string? terminals, string value)
        => RefFor(pins, terminals, value) ?? Scheme + pins.ToString(CultureInfo.InvariantCulture) + Separator
                                              + Unquote(terminals) + Separator + BoxName;

    // ── Choosing and binding ─────────────────────────────────────────────────

    private sealed record Choice(Symbol Glyph, string? Missing, bool ByPosition, bool Box, IReadOnlyList<string> Names);

    private static Choice Choose(string symbolRef, string? schematicDir)
    {
        var parsed = Parse(symbolRef);
        int pins = parsed?.Pins is > 0 and var p ? p : 2;
        var (allNames, allThermal) = ParseTerminals(parsed?.Terminals);
        IReadOnlyList<string> names = [.. Enumerable.Range(0, pins).Select(i =>
            i < allNames.Count ? allNames[i] : (i + 1).ToString(CultureInfo.InvariantCulture))];
        IReadOnlyList<int> thermal = [.. allThermal.Where(t => t < pins)];
        var binding = allNames.Count >= pins ? FetPinBinding.For(names, thermal) : null;

        Choice Box(string? missing) =>
            new(BuiltInSymbols.PrimitivesForVerilogA(pins, allNames.Count > 0 ? names : null), missing, false, true, names);

        // What blank draws, and also what a symbol that cannot be drawn falls back to.
        Choice Default(string? missing) =>
            DefaultKind(pins, parsed?.Terminals) is { } k ? Bind(BuiltInSymbols.Primitives(k, pins), missing) : Box(missing);

        Choice Bind(Symbol glyph, string? missing)
        {
            var bound = BindPins(glyph, binding, names, out bool byPosition);
            return bound is null ? Box(missing) : new(bound, missing, byPosition, false, names);
        }

        string name = (parsed?.Symbol ?? "").Trim();
        if (name.Length == 0) return Default(null);
        if (name == BoxName) return Box(null);

        if (!name.EndsWith(".csym", StringComparison.OrdinalIgnoreCase))
        {
            if (!Enum.TryParse<SymbolKind>(name, ignoreCase: false, out var kind) || !IsTransistorGlyph(kind))
                return Default(name);
            // A transistor glyph adapts to the connected count (R-vl7-2d): the 4-pin glyph is drawn only when four
            // terminals are connected, and three take the three-pin FET.
            int count = SymbolPortDefs.For(kind).Length;
            if (count == pins) return Bind(BuiltInSymbols.Primitives(kind, pins), null);
            if (pins == 3) return Bind(BuiltInSymbols.Primitives(SymbolKind.FetCurtice, 3), null);
            if (pins == 4) return Bind(BuiltInSymbols.Primitives(
                binding?.FourthRole == FetPinRole.Bulk ? SymbolKind.Mos1N : SymbolKind.ComposedFET, 4), null);
            return Box(name);
        }

        // A .csym resolves by File's own rule, so a model and its symbol travel together.
        string? path = SnpPathPolicy.Resolve(name, WBondSymbolProvider.WorkspaceRootOf(schematicDir), schematicDir);
        if (path is null || !File.Exists(path)) return Default(name);
        Symbol loaded;
        try { loaded = SymbolPersistence.LoadFromFile(path); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return Default(name); }
        if (loaded.Pins.Select(q => q.PortIndex).Distinct().Count() != pins || loaded.Pins.Count != pins) return Default(name);
        var csym = BindPins(loaded, binding, names, out bool csymByPosition, keepNames: true);
        return csym is null ? Default(name) : new(csym, null, csymByPosition, false, names);
    }

    /// <summary>True for a built-in kind of 3 or 4 pins every one of which names a transistor role, once each.</summary>
    private static bool IsTransistorGlyph(SymbolKind kind)
    {
        var defs = SymbolPortDefs.For(kind);
        if (defs.Length is not (3 or 4)) return false;
        var roles = defs.Select(d => FetPinBinding.RoleOf(d.Name)).ToList();
        return roles.All(r => r is not null)
            && roles.Contains(FetPinRole.Gate) && roles.Contains(FetPinRole.Drain) && roles.Contains(FetPinRole.Source)
            && roles.Distinct().Count() == roles.Count;
    }

    /// <summary>
    /// The glyph with each pin's PortIndex re-pointed at the model terminal it stands for: by name when the
    /// binding covers every pin, otherwise by position (pin k is terminal k — ComposedFET's by-number rule), with
    /// every pin then labelled with the model's own name so what is wired where can be seen (R-vl7-2c). A built-in
    /// glyph bound by name carries no labels: its shape says which lead is the gate. Null when the glyph's pins
    /// cannot be numbered onto the model's terminals at all.
    /// </summary>
    private static Symbol? BindPins(Symbol glyph, FetPinBinding? binding, IReadOnlyList<string> names,
                                    out bool byPosition, bool keepNames = false)
    {
        var ordered = glyph.Pins.OrderBy(q => q.PortIndex).ToList();
        if (ordered.Count != names.Count) { byPosition = false; return null; }

        int[]? byName = null;
        if (binding is not null && binding.Count == ordered.Count)
        {
            var map = ordered.Select(q => FetPinBinding.RoleOf(q.Name) is { } r ? binding.TerminalFor(r) : null).ToList();
            if (map.All(t => t is not null) && map.Distinct().Count() == map.Count)
                byName = [.. map.Select(t => t!.Value)];
        }

        byPosition = byName is null;
        var pins = new List<SymbolPin>(ordered.Count);
        for (int k = 0; k < ordered.Count; k++)
        {
            var q = ordered[k];
            int terminal = byName?[k] ?? k;
            string? label = byPosition ? names[terminal] : keepNames ? q.Name : null;
            pins.Add(new SymbolPin(q.LocalX, q.LocalY, terminal, label) { NameAlign = q.NameAlign });
        }
        return new Symbol(glyph.Primitives, pins, glyph.PortCount);
    }

    private static string Unquote(string? s)
    {
        string t = (s ?? "").Trim();
        return t.Length >= 2 && t[0] == '"' && t[^1] == '"' ? t[1..^1].Trim() : t;
    }
}

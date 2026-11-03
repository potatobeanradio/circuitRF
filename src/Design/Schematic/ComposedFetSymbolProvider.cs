using System;
using System.IO;
using CircuitRF.Design.Workspace;
using System.Linq;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Design.Symbol;
namespace CircuitRF.Design.Schematic;

// See SpiceModelSymbolProvider for why this alias sits inside the namespace.
using Symbol = global::CircuitRF.Design.Symbol.Symbol;

/// <summary>
/// The symbol of a placed <see cref="SymbolKind.ComposedFET"/> (brief-modelcomposer-1 R-mc1-9): the
/// one the instance's <c>Symbol</c> parameter names — copied from the model when it is placed, and
/// changed per instance in the Inspector — or, when that is blank, the one the <c>.cmodel</c> itself
/// names.
///
/// <para><b>What a symbol may be.</b> A library symbol, by its kind's name (<c>FetAngelov</c>), or a
/// <c>.csym</c> path relative to the <c>.cmodel</c>. Its pins 1, 2 and 3 bind to <c>g d s</c> by NUMBER,
/// so a <c>.csym</c> lacking any of them cannot be this device's symbol; a pin 4 is the thermal node <c>t</c>. The
/// standard glyph has the fourth when the model self-heats.</para>
///
/// <para><b>It always draws.</b> A symbol that does not resolve falls back to the standard
/// three-terminal FET glyph, in the FET family's pin geometry, so the instance stays placeable and
/// wired; <see cref="Diagnose"/> is the one warning that says so, which the extractor reports. A
/// resolution is three states wide and has nowhere to carry a sentence — the same split
/// <see cref="SpiceModelSymbolProvider"/> makes.</para>
/// </summary>
public static class ComposedFetSymbolProvider
{
    public const string Scheme = "composedfet://";

    /// <summary>The instance parameter naming the <c>.cmodel</c>.</summary>
    public const string FileParameter = "File";

    /// <summary>The instance parameter naming the symbol. Artwork only — never reaches the engine.</summary>
    public const string SymbolParameter = "Symbol";

    private const char Separator = '|';

    /// <summary>The symbol name that draws the standard glyph WITH its thermal pin whatever the model — what the Model
    /// Composer's symbol picker previews for a self-heating model before it is exported.</summary>
    public const string ThermalGlyph = "ComposedFET";

    /// <summary>The reference for an instance carrying these parameters. The file goes LAST, so a path
    /// containing the separator cannot be mistaken for the symbol field.</summary>
    public static string RefFor(string? file, string? symbol)
        => Scheme + Unquote(symbol) + Separator + Unquote(file);

    public static bool IsComposedFetRef(string? symbolRef)
        => symbolRef is not null && symbolRef.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase);

    public static (string Symbol, string File)? Parse(string? symbolRef)
    {
        if (!IsComposedFetRef(symbolRef)) return null;
        var fields = symbolRef![Scheme.Length..].Split(Separator, 2);
        return fields.Length == 2 ? (fields[0], fields[1]) : null;
    }

    /// <summary>The absolute path a stored <c>File</c> names — SnP's rule (<see cref="SnpPathPolicy"/>),
    /// which is also what the elaborator applies, so the drawn and the simulated model are one file.</summary>
    public static string? ResolvePath(string? file, string? schematicDir)
        => SnpPathPolicy.Resolve(Unquote(file), OwningRoot.Of(schematicDir), schematicDir);

    public static CellSymbolResolution Resolve(string symbolRef, string? schematicDir)
    {
        var (glyph, _) = Choose(symbolRef, schematicDir);
        return new CellSymbolResolution { State = CellSymbolState.Resolved, Symbol = glyph };
    }

    /// <summary>
    /// The one warning a composed FET's symbol can produce — naming the symbol that did not resolve —
    /// or null when it resolved or none was named.
    /// </summary>
    public static string? Diagnose(string instanceName, string? file, string? symbol, string? schematicDir)
        => Choose(RefFor(file, symbol), schematicDir).Missing is { } missing
            ? $"ComposedFET '{instanceName}': its symbol '{missing}' does not resolve; the standard FET symbol is drawn."
            : null;

    /// <summary>The model's own <c>Symbol</c>, or null — what placing an instance copies into it.</summary>
    public static string? ModelSymbol(string? file, string? schematicDir)
    {
        if (ResolvePath(file, schematicDir) is not { } path || !File.Exists(path)) return null;
        try { return ComposedModelCache.Load(path).Model.Symbol; }
        catch (Exception) { return null; }
    }

    /// <summary>True when the model the stored <c>File</c> names self-heats — it then has a thermal node to offer.</summary>
    public static bool ModelSelfHeats(string? file, string? schematicDir)
    {
        if (ResolvePath(file, schematicDir) is not { } path || !File.Exists(path)) return false;
        try { return ComposedModelCache.Load(path).Model.Thermal is not null; }
        catch (Exception) { return false; }
    }

    private static (Symbol Glyph, string? Missing) Choose(string symbolRef, string? schematicDir)
    {
        // The standard glyph carries the thermal pin t only for a model that has a thermal node, so a model that does not
        // self-heat never shows a pin it would refuse.
        var parsed = Parse(symbolRef);
        var fallback = Unlabelled(parsed is { } p0 && ModelSelfHeats(p0.File, schematicDir)
            ? BuiltInSymbols.Primitives(SymbolKind.ComposedFET, 4)
            : BuiltInSymbols.Primitives(SymbolKind.FetCurtice, 3));
        if (parsed is not { } r) return (fallback, null);

        string name = r.Symbol.Trim();
        string? modelPath = ResolvePath(r.File, schematicDir);
        if (name.Length == 0) name = (ModelSymbol(r.File, schematicDir) ?? "").Trim();
        if (name.Length == 0) return (fallback, null);

        if (name == ThermalGlyph) return (Unlabelled(BuiltInSymbols.Primitives(SymbolKind.ComposedFET, 4)), null);
        if (!name.EndsWith(".csym", StringComparison.OrdinalIgnoreCase))
            return Enum.TryParse<SymbolKind>(name, ignoreCase: false, out var kind)
                   && SymbolPortDefs.For(kind).Length == 3
                ? (Unlabelled(BuiltInSymbols.Primitives(kind, 3)), null)
                : (fallback, name);

        // A .csym is relative to the .cmodel, which is what makes a model and its symbol travel together.
        string? baseDir = modelPath is not null ? Path.GetDirectoryName(modelPath) : schematicDir;
        string? csym = Path.IsPathRooted(name) ? name
                     : baseDir is null ? null
                     : Path.GetFullPath(Path.Combine(baseDir, name.Replace('\\', '/')));
        if (csym is null || !File.Exists(csym)) return (fallback, name);

        Symbol loaded;
        try { loaded = SymbolPersistence.LoadFromFile(csym); }
        catch (Exception) { return (fallback, name); }

        bool hasGds = Enumerable.Range(0, 3).All(k => loaded.Pins.Any(p => p.PortIndex == k));
        return hasGds ? (loaded, null) : (fallback, name);
    }

    /// <summary>
    /// A built-in glyph with its pins' names cleared. A placed composed FET is drawn through the cell-symbol path, which
    /// prints a pin's stated name beside it — right for a library part, whose artwork carries no labels, but a FET glyph
    /// says which lead is the gate by its shape, and g, d and s printed on it read as clutter. A <c>.csym</c> keeps the
    /// names its author drew.
    /// </summary>
    private static Symbol Unlabelled(Symbol glyph) =>
        new(glyph.Primitives,
            [.. glyph.Pins.Select(p => new SymbolPin(p.LocalX, p.LocalY, p.PortIndex) { NameAlign = p.NameAlign })],
            glyph.PortCount);

    private static string Unquote(string? s)
    {
        string t = (s ?? "").Trim();
        return t.Length >= 2 && t[0] == '"' && t[^1] == '"' ? t[1..^1] : t;
    }
}

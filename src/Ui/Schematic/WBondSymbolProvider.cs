using CircuitRF.WBond;

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// How far apart a wBond symbol's port rows sit — <b>SnP's own two values, meaning the same two
/// things</b> (owner, 2026-08-16: "it should support tight or loose geometry just like the SnP
/// component already does").
///
/// <para>A separate enum from <see cref="SnpPitch"/> rather than a shared one because the two are
/// carried by different instance parameters on different components and are free to diverge; the
/// SPACINGS behind them are deliberately identical, and that is stated where they are used
/// (<c>WBondSymbolGenerator.RowPitch</c>).</para>
/// </summary>
public enum WBondSymbolPitch
{
    /// <summary>One connection grid between rows — an eight-array wBond in a four-array footprint.</summary>
    Tight,

    /// <summary>Two connection grids. The default, and the geometry every wBond shipped with.</summary>
    Loose,
}

/// <summary>
/// The symbol of a placed <c>wBond</c>, generated from the design the component CARRIES
/// (wbond.md §5.1, brief-wbond-wbb2 R-wbb2-1).
///
/// <h3>Why this is a FOURTH mechanism, and what it was chosen on</h3>
/// <para>Three mechanisms already produce a component's symbol, and none of them fits:</para>
/// <list type="bullet">
///   <item><b>A built-in <c>SymbolKind</c></b> has fixed artwork; a wBond has no fixed pin count.</item>
///   <item><b>A variadic <c>SymbolKind</c> + <c>PortCount</c></b> (SnP, SDD, ZPort) lets the USER set
///     the count; a wBond's is a property of its own wire arrays, and its pins carry NAMES that route
///     has nowhere to put.</item>
///   <item><b>A <c>CellRef</c> to a cell folder</b> needs a <c>.csym</c> on disk. Writing one makes a
///     second copy of the array list, and that copy goes stale the moment the design is edited — the
///     exact MTee failure <c>project-brief-L5-followups</c> already records.</item>
/// </list>
///
/// <h3>The symbol is a pure function of the ORDERED ARRAY NAMES, and that is the whole reference</h3>
/// <para><see cref="WBondSymbolGenerator"/> reads nothing else — two pins per array plus REF — so the
/// reference this resolver takes is exactly that list (<see cref="RefFor"/>), derived from the
/// component's own <c>Design</c> payload. It is short, so it is a cheap cache key; it is derived, so
/// there is no second field to keep in step; and because the payload travels IN the schematic there
/// is nothing to resolve at render time and no file that can go missing. <b>A stale or unresolvable
/// symbol is not a bug to avoid but a state that cannot be represented.</b></para>
///
/// <para>It plugs into the seam <see cref="CellSymbolResolver"/> already has for
/// <see cref="PdkKitRegistry"/>, checked ahead of the path branch and for the same reason: the
/// reference is not a path and must not be reported as a bad one.</para>
/// </summary>
public static class WBondSymbolProvider
{
    /// <summary>
    /// Marks a symbol reference as naming a wBond's array list rather than a cell folder. Same role
    /// <see cref="PdkKitRegistry.Scheme"/> plays, for the same reason — a reference that states its
    /// own kind can never be mistaken for a mistyped relative path.
    /// </summary>
    public const string Scheme = "wbond://";

    /// <summary>Separator between array names in a reference and in the <c>Arrays</c> record.</summary>
    private const char ArraySeparator = '|';

    // ── The reference form ────────────────────────────────────────────────────

    /// <summary>
    /// The symbol reference for a wBond component carrying <paramref name="designPayload"/>, drawn at
    /// <paramref name="pitch"/>.
    ///
    /// <para>Never null for a wBond — a payload that declares no arrays yields a reference that
    /// resolves to <see cref="CellSymbolState.PrimaryMissing"/>, which reports rather than silently
    /// falling back to a two-pin built-in glyph.</para>
    ///
    /// <para><b>The pitch is the FIRST field, positionally</b>, so no array name can ever be mistaken
    /// for it — an array may be called anything, including "Tight". There is no compatibility concern
    /// in changing the reference's shape: it is DERIVED from the component's own parameters on every
    /// access (<c>EditableComponent.ExternalSymbolRef</c>) and never written to a file.</para>
    /// </summary>
    public static string RefFor(string? designPayload, WBondSymbolPitch pitch = WBondSymbolPitch.Loose,
                                bool referencePin = false)
        => Scheme + pitch + ArraySeparator + (referencePin ? "ref" : "noref") + ArraySeparator
                  + string.Join(ArraySeparator, WBondEmbedding.ArrayNamesOf(designPayload));

    /// <summary>
    /// The pitch an instance's <c>Pitch</c> parameter asks for, or <see cref="WBondSymbolPitch.Loose"/>
    /// for anything unset or unrecognised — an artwork option can never be a reason not to draw.
    /// </summary>
    public static WBondSymbolPitch ParsePitch(string? text)
        => Enum.TryParse<WBondSymbolPitch>(text, ignoreCase: true, out var pitch)
            ? pitch
            : WBondSymbolPitch.Loose;

    /// <summary>True when this reference names a wBond's array list.</summary>
    public static bool IsWBondRef(string? symbolRef)
        => symbolRef is not null && symbolRef.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase);

    // ── The array list a placed instance was wired against ────────────────────

    /// <summary>
    /// The design's array names in order — the identity a placed instance's wiring was drawn against
    /// (§5 question 3). Deliberately NOT <c>WBondSymbolGenerator.ContentKey</c>: that carries the
    /// generator's own content version, so bumping it would report an array reorder on every placed
    /// instance in the field.
    /// </summary>
    public static string ArraysKeyOf(WBondDesign design)
    {
        ArgumentNullException.ThrowIfNull(design);
        return string.Join(ArraySeparator, design.Arrays.Select(a => a.Name));
    }

    /// <summary>The array key of an encoded payload, without decoding it twice.</summary>
    public static string ArraysKeyOfPayload(string? designPayload)
        => string.Join(ArraySeparator, WBondEmbedding.ArrayNamesOf(designPayload));

    /// <summary>The array key a freshly-placed wBond records — the default design's own.</summary>
    public static string DefaultArraysKey => ArraysKeyOfPayload(WBondEmbedding.DefaultPayload);

    // ── The CellSymbolResolver seam ───────────────────────────────────────────

    /// <summary>
    /// Resolves a <c>wbond://</c> reference to the three-state result every other symbol source
    /// produces, so the renderer, the hit-test and the extractor need no wBond-specific branch.
    ///
    /// <list type="bullet">
    ///   <item><b>Resolved</b> — the component carries at least one wire array.</item>
    ///   <item><b>PrimaryMissing</b> — it carries none, so there is nothing to wire. There is
    ///     deliberately no <b>NotFound</b> case any more: nothing is looked up, so nothing can be
    ///     missing.</item>
    /// </list>
    /// </summary>
    public static CellSymbolResolution Resolve(string symbolRef, string? schematicDir)
    {
        _ = schematicDir;   // nothing is resolved against the filesystem; the design travels in the file

        if (!IsWBondRef(symbolRef)) return CellSymbolResolution.NotFoundResult;

        // Two positional fields — the pitch, then the reference pin — and everything after them is
        // the array list. A reference missing either is carrying no arrays either, whatever its
        // leading fields say.
        var fields = symbolRef[Scheme.Length..].Split(ArraySeparator, 3);
        if (fields.Length < 3) return CellSymbolResolution.PrimaryMissingResult;

        var pitch = ParsePitch(fields[0]);
        bool referencePin = fields[1].Equals("ref", StringComparison.OrdinalIgnoreCase);

        string names = fields[2];
        if (names.Length == 0) return CellSymbolResolution.PrimaryMissingResult;

        var symbol = SymbolFor(names, pitch, referencePin);
        return symbol is not null
            ? new CellSymbolResolution { State = CellSymbolState.Resolved, Symbol = symbol }
            : CellSymbolResolution.PrimaryMissingResult;
    }

    // Keyed by the array-name list, which IS everything the symbol depends on, plus the generator's
    // own content version — so a generator change that moves a pin cannot hit a stale entry.
    private static readonly Dictionary<string, Symbol?> _cache = new(StringComparer.Ordinal);
    private static readonly Lock _gate = new();

    private static Symbol? SymbolFor(string arrayNames, WBondSymbolPitch pitch, bool referencePin)
    {
        string key = WBondSymbolGenerator.ContentVersion + ":" + pitch + ":" + referencePin + ":" + arrayNames;

        lock (_gate)
            if (_cache.TryGetValue(key, out var hit)) return hit;

        var built = WBondSymbolGenerator.Build(arrayNames.Split(ArraySeparator), pitch, referencePin);

        lock (_gate) _cache[key] = built;
        return built;
    }

    /// <summary>Clears the generated-symbol cache — called when a workspace is left.</summary>
    public static void InvalidateAll()
    {
        lock (_gate) _cache.Clear();
    }

    // ── Reading a .wBond from disk (the IMPORT routes only) ───────────────────

    /// <summary>
    /// The workspace root a schematic belongs to — the directory holding the nearest ancestor
    /// <c>.cws</c>, or null for a loose/scratch schematic.
    /// </summary>
    public static string? WorkspaceRootOf(string? schematicDir)
        => WorkspaceRootFinder.FindAncestorCws(schematicDir) is { } cws
            ? Path.GetDirectoryName(cws)
            : null;
}

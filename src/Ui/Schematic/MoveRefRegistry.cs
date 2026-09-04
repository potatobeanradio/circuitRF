using System.Text.Json.Nodes;
using CircuitRF.Ui.WBond;

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// One writable string slot inside a parsed document — an object property, or an element of a string
/// array. The rewriter needs both shapes because a <c>.cws</c> stores <c>KnownFiles</c> as a bare
/// array of strings while everything else stores a named property.
/// </summary>
public sealed class RefSlot
{
    private readonly JsonObject? _owner;
    private readonly string?     _key;
    private readonly JsonArray?  _array;
    private readonly int         _index;

    private RefSlot(JsonObject? owner, string? key, JsonArray? array, int index, string stored)
    {
        _owner = owner; _key = key; _array = array; _index = index; Stored = stored;
    }

    /// <summary>The value as the file holds it, verbatim.</summary>
    public string Stored { get; }

    /// <summary>Replaces it. The document is re-serialized by the caller, once, if anything changed.</summary>
    public void Set(string value)
    {
        if (_owner is not null && _key is not null) _owner[_key] = JsonValue.Create(value);
        else if (_array is not null)                _array[_index] = JsonValue.Create(value);
    }

    /// <summary>The slot at <paramref name="key"/>, or null when it is absent, null or not a
    /// non-empty string — the three ways a path-shaped field is simply not there.</summary>
    public static RefSlot? For(JsonNode? node, string key)
    {
        if (node is not JsonObject obj) return null;
        if (obj[key] is not JsonValue v) return null;
        if (v.GetValueKind() != System.Text.Json.JsonValueKind.String) return null;
        string? s = v.GetValue<string?>();
        return string.IsNullOrWhiteSpace(s) ? null : new RefSlot(obj, key, null, 0, s);
    }

    /// <summary>Every element of a string array, as slots.</summary>
    public static IEnumerable<RefSlot> ForArray(JsonNode? node, string key)
    {
        if (node is not JsonObject obj || obj[key] is not JsonArray arr) yield break;
        for (int i = 0; i < arr.Count; i++)
        {
            if (arr[i] is not JsonValue v) continue;
            if (v.GetValueKind() != System.Text.Json.JsonValueKind.String) continue;
            string? s = v.GetValue<string?>();
            if (!string.IsNullOrWhiteSpace(s)) yield return new RefSlot(null, null, arr, i, s);
        }
    }
}

/// <summary>
/// One kind of path-shaped reference a document can carry: where it lives, what it is relative to,
/// and the pair of functions that read one and write one back.
/// </summary>
/// <param name="Id">Stable name, used in tests and in the move report. Not a file format.</param>
/// <param name="MatchesFile">Which files carry this kind of reference.</param>
/// <param name="Locate">Every slot of this kind in a parsed document.</param>
/// <param name="BaseDirOf">What a stored value in THIS file is relative to — its own directory,
/// the workspace root, or whatever else that field's own rule says.</param>
/// <param name="Resolve">Stored value + base → the absolute path it names, or null when the value
/// is not a path at all (a <c>pdk://</c> or <c>wbond://</c> reference, a <c>${TOKEN}</c> that is not
/// this operation's business, a string the OS will not parse).</param>
/// <param name="Store">Absolute target + base + the value as it was stored → the new stored value.
/// The third argument is what keeps R-tm1-5's absolute cases absolute: a reference the user chose to
/// store rooted stays rooted, and one stored relative stays relative.</param>
public sealed record MoveRefSite(
    string                                Id,
    Func<string, bool>                    MatchesFile,
    Func<JsonNode, IEnumerable<RefSlot>>  Locate,
    Func<string, string?>                 BaseDirOf,
    Func<string, string, string?>         Resolve,
    Func<string, string, string, string>  Store);

/// <summary>
/// <b>The single registry of every path-shaped reference a move has to repair</b> (TM1 R-tm1-4).
///
/// <para>A format that is not registered here is not rewritten, and that is the point: the
/// alternative is a rewrite at each call site, which is how this table acquires a row nobody
/// rewrites. The symptom of a missed row is a dangling reference in a file the user did not touch,
/// which reads as data loss rather than as a missing feature.</para>
///
/// <para><b>Every row states its own base directory, and the bases genuinely differ.</b> A
/// <c>CellRef</c> is relative to the document that holds it; an SnP <c>File</c> is relative to the
/// WORKSPACE ROOT; a <c>.cem</c>'s <c>LayoutRef</c> is relative to the workspace root with the
/// <c>.cem</c>'s own directory as the no-workspace fallback. Getting a base wrong does not throw —
/// it silently repoints a reference at a file that is not there — so each row names the shared
/// producer it routes through rather than doing its own <c>Path.Combine</c>.</para>
///
/// <para><b>What is deliberately NOT here, because it is already immune:</b> a <c>.cdd</c>'s source
/// references are relative to the RESULTS ROOT (<c>DataDisplayViewModel.ComputeSourceKey</c>), not
/// to the <c>.cdd</c>, so moving a <c>.cdd</c> changes nothing; a <c>.ccell</c>'s primaries are bare
/// file names inside the cell folder, which travels whole; and a <c>.wBond</c>'s EMBEDDED geometry
/// is a self-contained snapshot whose <c>CellRef</c>s resolve inside the scratch directory
/// <c>WBondGeometryEmbedding.Unpack</c> writes, never against the workspace.</para>
/// </summary>
public static class MoveRefRegistry
{
    // ── File predicates ───────────────────────────────────────────────────────

    private static Func<string, bool> Ext(string extension) =>
        f => Path.GetExtension(f).Equals(extension, StringComparison.OrdinalIgnoreCase);

    // `.cws` is the WHOLE file name, so Path.GetExtension returns "" for it — matching by extension
    // would silently skip every workspace manifest, which is a third of this table.
    private const string CwsFileName = ".cws";

    private static readonly Func<string, bool> IsCws =
        f => Path.GetFileName(f).Equals(CwsFileName, StringComparison.OrdinalIgnoreCase);

    // ── Base-directory rules ──────────────────────────────────────────────────

    private static string? OwnDir(string file) => Path.GetDirectoryName(Path.GetFullPath(file));

    private static string? WorkspaceRootOf(string file) =>
        WorkspaceRootFinder.WorkspaceDirOf(Path.GetDirectoryName(Path.GetFullPath(file)));

    // ── Generic path resolve/store, used by most rows ─────────────────────────

    /// <summary>A plain relative-or-rooted path against a base. Null for anything the OS will not
    /// parse, and for a value carrying an unexpanded <c>${TOKEN}</c> — see <see cref="HasToken"/>.</summary>
    private static string? PlainResolve(string stored, string baseDir)
    {
        if (HasToken(stored)) return null;
        try
        {
            string raw = stored.Replace('\\', '/');
            return Path.IsPathRooted(raw)
                ? Path.GetFullPath(raw)
                : Path.GetFullPath(Path.Combine(baseDir, raw));
        }
        catch { return null; }
    }

    /// <summary>
    /// The inverse, preserving the FORM the value was stored in (R-tm1-5): a rooted value stays
    /// rooted and is still relocated; a relative one stays relative, forward-slash.
    /// </summary>
    private static string PlainStore(string absTarget, string baseDir, string wasStored)
    {
        if (Path.IsPathRooted(wasStored)) return absTarget;
        try
        {
            string rel = Path.GetRelativePath(baseDir, absTarget);
            return Path.IsPathRooted(rel) ? absTarget : rel.Replace('\\', '/');
        }
        catch { return absTarget; }
    }

    /// <summary>
    /// The same, for a field whose base is the WORKSPACE ROOT — where a path that climbs out of the
    /// base is stored ABSOLUTE rather than as a <c>../../..</c> chain. That is not a stylistic
    /// choice: <c>WorkspaceRefs.ToStoredRef</c> and <c>EmSetupResolver.MakeLayoutRef</c> both make
    /// it, and for the same stated reason — a relative chain out of a workspace breaks the moment
    /// the workspace itself moves, which is exactly what those fields exist to survive.
    /// </summary>
    private static string RootStore(string absTarget, string baseDir, string wasStored)
    {
        if (Path.IsPathRooted(wasStored)) return absTarget;
        try
        {
            string rel = Path.GetRelativePath(baseDir, absTarget);
            return Path.IsPathRooted(rel) || rel.StartsWith("..", StringComparison.Ordinal)
                ? absTarget
                : rel.Replace('\\', '/');
        }
        catch { return absTarget; }
    }

    /// <summary>
    /// True for a value carrying a <c>${NAME}</c> site token (SL1 R-sl1-5). Such a value names a
    /// location OUTSIDE the workspace by construction, so it cannot have moved — and resolving it to
    /// re-store it would silently replace the token with one machine's expansion of it, which is the
    /// opposite of what the token is for.
    /// </summary>
    private static bool HasToken(string stored) => stored.Contains("${", StringComparison.Ordinal);

    // ── Cell references ───────────────────────────────────────────────────────

    /// <summary>
    /// A <c>CellRef</c>, through the one producer and the one resolver every other site already uses.
    /// The three virtual forms — <c>pdk://</c>, <c>wbond://</c>, <c>spicemodel://</c> — are not paths
    /// and are never rewritten; a rewriter that treated one as a path would produce a reference that
    /// resolves to nothing and reads like a typo the user made.
    /// </summary>
    private static string? ResolveCellRef(string stored, string baseDir)
    {
        // Asked as three explicit predicates rather than through
        // CellSymbolResolver.NeedsNoBaseDirectory, which answers a DIFFERENT question — it lets a
        // SpiceModel through when its own File is rooted, which is right for rendering and wrong
        // here. A fourth virtual form joins this list, not that one.
        if (PdkKitRegistry.IsKitRef(stored)) return null;
        if (WBondSymbolProvider.IsWBondRef(stored)) return null;
        if (SpiceModelSymbolProvider.IsSpiceModelRef(stored)) return null;
        return ExternalCellRef.ResolveCellDir(stored, baseDir);
    }

    private static string StoreCellRef(string absTarget, string baseDir, string _)
        => ExternalCellRef.MakeCellRef(baseDir, absTarget);

    // ── The table ─────────────────────────────────────────────────────────────

    /// <summary>Every registered reference site. Ordered as the design doc's own table is.</summary>
    public static IReadOnlyList<MoveRefSite> Sites { get; } =
    [
        // ── .csch ─────────────────────────────────────────────────────────────
        new("csch/CellRef", Ext(".csch"),
            n => Items(n, "Components").Select(c => RefSlot.For(c, "CellRef")).OfType<RefSlot>(),
            OwnDir, ResolveCellRef, StoreCellRef),

        new("csch/ImagePath", Ext(".csch"),
            n => Items(n, "CanvasObjects").Select(o => RefSlot.For(o, "ImagePath")).OfType<RefSlot>(),
            OwnDir, PlainResolve, PlainStore),

        // A wBond link is relative to the SCHEMATIC (WBondPlacement.ResolveLinkedPath); every other
        // `File` parameter in a `.csch` is relative to the WORKSPACE ROOT. They share a parameter
        // name and differ by four orders of directory, so the component's own `Symbol` is the
        // discriminator and there is no defensible default.
        new("csch/wBondLink", Ext(".csch"),
            n => FileParamSlots(n, wBond: true),
            OwnDir, PlainResolve, PlainStore),

        // SnP, SpiceModel and VerilogA all resolve their `File` through SnpPathPolicy — the same
        // rule Elaborator.ResolveSnpFilePath applies at Run — so they are one row, not three.
        new("csch/ModelFile", Ext(".csch"),
            n => FileParamSlots(n, wBond: false),
            WorkspaceRootOf,
            (s, b) => HasToken(s) ? null : SnpPathPolicy.Resolve(s, b, null),
            (abs, b, was) => Path.IsPathRooted(was) ? abs : SnpPathPolicy.ToStored(abs, b)),

        // ── .clay ─────────────────────────────────────────────────────────────
        new("clay/CellRef", Ext(".clay"),
            n => Items(n, "Instances").Select(i => RefSlot.For(i, "CellRef")).OfType<RefSlot>(),
            OwnDir, ResolveCellRef, StoreCellRef),

        new("clay/ImagePathRef", Ext(".clay"),
            n => Items(n, "Shapes").Select(s => RefSlot.For(s, "ImagePathRef")).OfType<RefSlot>(),
            OwnDir, PlainResolve, PlainStore),

        new("clay/TechRef", Ext(".clay"),
            n => One(RefSlot.For(n, "TechRef")),
            OwnDir, PlainResolve, PlainStore),

        // ── .cem ──────────────────────────────────────────────────────────────
        // EmSetupResolver's rule exactly: the workspace root when there is one, the `.cem`'s own
        // directory when there is not — which is what makes a loose `.cem` beside its `.clay`
        // already-specified behaviour rather than a new case.
        new("cem/LayoutRef", Ext(".cem"),
            n => One(RefSlot.For(n, "LayoutRef")),
            f => WorkspaceRootOf(f) ?? OwnDir(f),
            PlainResolve, RootStore),

        // ── .wBond ────────────────────────────────────────────────────────────
        // NOT in the brief's own table and found while building it: a `.wBond` may carry an
        // AssemblyRef of its own, relative to its own directory, exactly as a `.clay` carries a
        // TechRef (WasmResolver.Resolve).
        new("wBond/AssemblyRef", Ext(WBondCell.FileExtension),
            n => One(RefSlot.For(n, "AssemblyRef")),
            OwnDir, PlainResolve, PlainStore),

        // ── .cws ──────────────────────────────────────────────────────────────
        // Every one of these is relative to the workspace root, which is the `.cws`'s own directory.
        new("cws/LibraryRefs",          IsCws, n => RefSlot.ForArray(n, "LibraryRefs"),   OwnDir, PlainResolve, RootStore),
        new("cws/KnownFiles",           IsCws, n => RefSlot.ForArray(n, "KnownFiles"),    OwnDir, PlainResolve, RootStore),
        new("cws/DefaultTechRef",       IsCws, n => One(RefSlot.For(n, "DefaultTechRef")),     OwnDir, PlainResolve, RootStore),
        new("cws/DefaultAssemblyRef",   IsCws, n => One(RefSlot.For(n, "DefaultAssemblyRef")), OwnDir, PlainResolve, RootStore),
        new("cws/ActiveDocumentPath",   IsCws, n => One(RefSlot.For(n, "ActiveDocumentPath")), OwnDir, PlainResolve, RootStore),

        new("cws/PdkRefs",              IsCws,
            n => Items(n, "PdkRefs").Select(p => RefSlot.For(p, "Path")).OfType<RefSlot>(),
            OwnDir, PlainResolve, RootStore),

        // A referenced workspace's entry names the OTHER `.cws`. It is in the table because the rule
        // is uniform, not because a move inside this workspace usually touches it — but a workspace
        // referenced from a sub-folder of this one is a real arrangement and it does.
        new("cws/ReferencedWorkspaces", IsCws,
            n => Items(n, "ReferencedWorkspaces").Select(w => RefSlot.For(w, "Path")).OfType<RefSlot>(),
            OwnDir, PlainResolve, RootStore),

        new("cws/OpenDocuments",        IsCws,
            n => Items(n, "OpenDocuments").Select(d => RefSlot.For(d, "Path")).OfType<RefSlot>(),
            OwnDir, PlainResolve, RootStore),

        // The dock layout carries the same document paths a second time — the tab order, the active
        // tab, every torn-off window's list, and the split document region's tree. Left out, a moved
        // cell's schematic reopens as a missing file on the next workspace open, which looks like the
        // move lost it.
        new("cws/DockLayout",           IsCws, DockLayoutSlots, OwnDir, PlainResolve, RootStore),
    ];

    // ── Locator helpers ───────────────────────────────────────────────────────

    private static IEnumerable<JsonNode> Items(JsonNode node, string arrayKey)
    {
        if (node is not JsonObject obj || obj[arrayKey] is not JsonArray arr) yield break;
        foreach (var item in arr)
            if (item is not null) yield return item;
    }

    private static IEnumerable<RefSlot> One(RefSlot? slot)
    {
        if (slot is not null) yield return slot;
    }

    /// <summary>
    /// The <c>File</c> parameter of every component whose <c>Symbol</c> is (or is not) <c>WBond</c>.
    /// Split on that flag because the two halves have DIFFERENT bases; see the two rows above.
    /// </summary>
    private static IEnumerable<RefSlot> FileParamSlots(JsonNode node, bool wBond)
    {
        foreach (var comp in Items(node, "Components"))
        {
            string symbol = (comp as JsonObject)?["Symbol"]?.GetValue<string?>() ?? "";
            bool isWBond  = symbol.Equals(nameof(SymbolKind.WBond), StringComparison.OrdinalIgnoreCase);
            if (isWBond != wBond) continue;

            foreach (var p in Items(comp, "Parameters"))
            {
                if ((p as JsonObject)?["Name"]?.GetValue<string?>() is not { } name) continue;
                if (!name.Equals(WBondPlacement.FileParameter, StringComparison.Ordinal)) continue;
                if (RefSlot.For(p, "Expression") is { } slot) yield return slot;
            }
        }
    }

    /// <summary>Every workspace-relative document path inside the <c>.cws</c>'s dock-layout block,
    /// including the recursive split-pane tree.</summary>
    private static IEnumerable<RefSlot> DockLayoutSlots(JsonNode node)
    {
        if (node is not JsonObject obj || obj["DockLayout"] is not JsonObject dock) yield break;

        foreach (var s in RefSlot.ForArray(dock, "DocumentOrder")) yield return s;
        if (RefSlot.For(dock, "ActiveDocument") is { } active) yield return active;

        foreach (var w in Items(dock, "FloatingDocumentWindows"))
        {
            foreach (var s in RefSlot.ForArray(w, "Documents")) yield return s;
            if (RefSlot.For(w, "Active") is { } wActive) yield return wActive;
        }

        foreach (var s in RegionSlots(dock["DocumentRegion"])) yield return s;
    }

    private static IEnumerable<RefSlot> RegionSlots(JsonNode? region)
    {
        if (region is not JsonObject obj) yield break;

        foreach (var s in RefSlot.ForArray(obj, "Documents")) yield return s;
        if (RefSlot.For(obj, "Active") is { } active) yield return active;

        if (obj["Children"] is not JsonArray children) yield break;
        foreach (var child in children)
        foreach (var s in RegionSlots(child))
            yield return s;
    }
}

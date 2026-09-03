namespace CircuitRF.Design.Workspace;

/// <summary>
/// The <c>ws://</c> cell-reference scheme — a cell in ANOTHER workspace, addressed by an alias the
/// referencing document's own <c>.cws</c> resolves (MW2 R-mw2-2/-3/-4).
///
/// <code>
/// CellRef:   ws://RfFrontEnd/cells/Amp
/// .cws:      ReferencedWorkspaces: [ { Alias: "RfFrontEnd", Path: "../rf-front-end/.cws" } ]
/// </code>
///
/// <para><b>Why an alias rather than the raw <c>../../Other/cells/Amp</c> the path machinery already
/// produces.</b> <c>workspace-and-project-tree.md</c> §5A R37 names this exact decision and warns
/// against answering it by accident. Five reasons, in the order they matter: relocating the other
/// project is one <c>.cws</c> edit instead of a rewrite of every document that referenced it; it
/// reuses the shape <c>pdk://</c> already has, and a reference that states its own kind cannot be
/// mistaken for a typo, which is what lets a repair flow say anything useful; it NAMES the other
/// workspace, which the technology check and the kit walk-up both need and which a raw path can only
/// infer — an inference that fails silently when the path is stale; the <c>.cws</c> already has the
/// slot beside its referenced libraries; and the Project Tree already knows how to draw a referenced
/// sub-tree and mark an unresolvable one.</para>
///
/// <para><b>A raw <c>../../Other/cells/Amp</c> is not blessed and not rejected</b> (R-mw2-5). It
/// resolves today by accident — every producing site writes <c>Path.GetRelativePath</c> and every
/// reading site does a plain <c>Path.Combine</c> — and removing that would break the LIBRARY case,
/// which legitimately points outside the workspace. So it goes on resolving, nothing here ever
/// writes one, and it is not a documented feature.</para>
///
/// <para><b>Every path-shaped cell reference resolves through <see cref="ResolveCellDir"/>.</b> The
/// two forms differ only in how the absolute cell folder is arrived at, and a call site that splits
/// them itself is a call site that will be missed when a third form arrives — which is the trap
/// <c>CellSymbolResolver.NeedsNoBaseDirectory</c> already records for the virtual forms.</para>
/// </summary>
public static class ExternalCellRef
{
    /// <summary>Marks a cell reference as addressed through a workspace alias.</summary>
    public const string Scheme = "ws://";

    /// <summary>True when this reference names a cell in a referenced workspace.</summary>
    public static bool IsExternalRef(string? cellRef) =>
        cellRef is not null && cellRef.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Splits <c>ws://alias/rel/path</c> into its alias and the workspace-relative remainder. The
    /// alias runs to the FIRST separator and the path takes the rest — the same split
    /// <c>PdkKitRegistry.TryParse</c> makes, and for the same reason: the remainder is the other
    /// workspace's own spelling and circuitRF does not get to constrain it.
    /// </summary>
    public static bool TryParse(string? cellRef, out string alias, out string workspaceRelPath)
    {
        alias = workspaceRelPath = "";
        if (!IsExternalRef(cellRef)) return false;

        string rest = cellRef![Scheme.Length..];
        int slash = rest.IndexOf('/');
        if (slash <= 0 || slash == rest.Length - 1) return false;

        alias            = rest[..slash];
        workspaceRelPath = rest[(slash + 1)..];
        return true;
    }

    /// <summary>The stored form for one cell of one referenced workspace. Separators are <c>/</c>,
    /// the same convention <c>WorkspaceRefs.ToStoredRef</c> normalises to and for the same
    /// reason — it only fails when a workspace crosses platforms, i.e. never on the machine that
    /// wrote it.</summary>
    public static string RefFor(string alias, string workspaceRelPath) =>
        $"{Scheme}{alias}/{workspaceRelPath.Replace('\\', '/').TrimStart('/')}";

    // ── Resolution ────────────────────────────────────────────────────────────

    /// <summary>
    /// The absolute cell folder <paramref name="cellRef"/> names, for EITHER form — null when it
    /// cannot be worked out at all (no base directory for a relative reference, an alias the
    /// referencing workspace does not declare, a malformed path). <b>Existence is not checked
    /// here</b>: a reference that resolves to a folder which is not there is <c>NotFound</c>, the
    /// existing reported and repairable state, and the callers already distinguish the two.
    /// </summary>
    public static string? ResolveCellDir(string? cellRef, string? baseDir)
    {
        if (string.IsNullOrEmpty(cellRef)) return null;

        if (IsExternalRef(cellRef)) return ResolveExternal(cellRef!, baseDir);

        if (baseDir is null) return null;
        try { return Path.GetFullPath(Path.Combine(baseDir, cellRef)); }
        catch { return null; }
    }

    /// <summary>
    /// The workspace ROOT a <c>ws://</c> reference's alias resolves to — the directory holding the
    /// other <c>.cws</c> — or null when the alias is not declared by the workspace
    /// <paramref name="baseDir"/> belongs to. This is what §3's technology check and §5's marking
    /// both ask, and it is the reason the alias names the workspace explicitly.
    /// </summary>
    public static string? ResolveAliasWorkspaceRoot(string? cellRef, string? baseDir)
    {
        if (!TryParse(cellRef, out string alias, out _)) return null;
        return WorkspaceRootForAlias(WorkspaceRootFinder.WorkspaceDirOf(baseDir), alias);
    }

    /// <summary>
    /// The workspace root <paramref name="alias"/> names, as declared by the <c>.cws</c> in
    /// <paramref name="referencingWorkspaceRoot"/>. Null when the workspace declares no such alias,
    /// or when the alias's stored path does not name an existing <c>.cws</c>.
    /// </summary>
    public static string? WorkspaceRootForAlias(string? referencingWorkspaceRoot, string alias)
    {
        if (string.IsNullOrEmpty(referencingWorkspaceRoot) || string.IsNullOrEmpty(alias)) return null;
        return AliasMapFor(referencingWorkspaceRoot!).GetValueOrDefault(alias);
    }

    private static string? ResolveExternal(string cellRef, string? baseDir)
    {
        if (!TryParse(cellRef, out string alias, out string rel)) return null;

        string? otherRoot = WorkspaceRootForAlias(WorkspaceRootFinder.WorkspaceDirOf(baseDir), alias);
        if (otherRoot is null) return null;

        try
        {
            return Path.GetFullPath(
                Path.Combine(otherRoot, rel.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch { return null; }
    }

    // ── Producing one ─────────────────────────────────────────────────────────

    /// <summary>
    /// The stored reference for <paramref name="cellAbsDir"/> as written into a document in
    /// <paramref name="baseDir"/>: <c>ws://alias/…</c> when the cell lives inside a workspace
    /// <paramref name="baseDir"/>'s own workspace REFERENCES, and the ordinary relative path
    /// otherwise.
    ///
    /// <para>Every producing site routes through here rather than calling
    /// <c>Path.GetRelativePath</c> itself, so the alias form is written in exactly the case it is
    /// meant for and nowhere else. A cell in a referenced LIBRARY keeps its relative path: a library
    /// is not a workspace, it brings no technology and no kit set, and rewriting those references
    /// would be the second convention R37 warns against arriving by a different door.</para>
    /// </summary>
    public static string MakeCellRef(string baseDir, string cellAbsDir)
    {
        string? ownRoot = WorkspaceRootFinder.WorkspaceDirOf(baseDir);
        string abs;
        try { abs = Path.GetFullPath(cellAbsDir); }
        catch { return cellAbsDir; }

        if (ownRoot is not null && WorkspaceRootFinder.IsOutside(abs, ownRoot))
        {
            // The DEEPEST matching referenced workspace wins, and the tie is broken by alias so the
            // answer never depends on dictionary order. Two referenced workspaces can nest (a project
            // inside a delivery folder that is itself a workspace), and the innermost is the one the
            // cell actually belongs to — the same "nearest ancestor" rule the .cws walk-up uses.
            (string Alias, string Root)? best = null;
            foreach (var (alias, otherRoot) in AliasMapFor(ownRoot))
            {
                if (otherRoot is null || WorkspaceRootFinder.IsOutside(abs, otherRoot)) continue;
                if (best is null
                    || otherRoot.Length > best.Value.Root.Length
                    || (otherRoot.Length == best.Value.Root.Length
                        && string.CompareOrdinal(alias, best.Value.Alias) < 0))
                    best = (alias, otherRoot);
            }
            if (best is { } hit)
                return RefFor(hit.Alias, Path.GetRelativePath(hit.Root, abs));
        }

        try { return Path.GetRelativePath(baseDir, abs); }
        catch { return cellAbsDir; }
    }

    // ── The alias table, memoised ─────────────────────────────────────────────
    //
    // Asked per cell instance per render, exactly as the workspace walk-up above it is (R-mw1-6),
    // so it is memoised on the same terms and dropped at the same moments — WorkspaceRootFinder's
    // InvalidateCache clears both, because a .cws appearing, disappearing or being rewritten changes
    // the answer to both questions and nothing else would notice.

    private static readonly Dictionary<string, Dictionary<string, string?>> _aliasMemo =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock _memoGate = new();

    /// <summary>Alias → other workspace ROOT (null when the alias's stored path does not resolve to
    /// an existing <c>.cws</c>, which is R-mw2-11's "broken" state rather than an absent alias).</summary>
    internal static Dictionary<string, string?> AliasMapFor(string workspaceRoot)
    {
        string key = WorkspaceRootFinder.Normalize(workspaceRoot);

        lock (_memoGate)
            if (_aliasMemo.TryGetValue(key, out var memo)) return memo;

        var map = ReadAliasMap(key);

        lock (_memoGate) _aliasMemo[key] = map;
        return map;
    }

    private static Dictionary<string, string?> ReadAliasMap(string workspaceRoot)
    {
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(workspaceRoot)) return map;

        CwsFile cws;
        try { cws = WorkspacePersistence.LoadFromFile(Path.Combine(workspaceRoot, ".cws")); }
        catch { return map; }   // no .cws, or one this build cannot read — no aliases, not an error

        foreach (var entry in cws.ReferencedWorkspaces ?? [])
        {
            if (string.IsNullOrWhiteSpace(entry.Alias) || map.ContainsKey(entry.Alias)) continue;
            map[entry.Alias] = ResolveOtherRoot(workspaceRoot, entry.Path);
        }
        return map;
    }

    /// <summary>
    /// The other workspace's ROOT from its stored <c>.cws</c> path — relative resolves against the
    /// referencing workspace root, rooted stays as it is, and <c>/</c> converts back to the platform
    /// separator. That is <c>WorkspaceRefs.Resolve</c>'s rule, repeated here in three lines rather
    /// than called: that helper lives in <c>src/Ui</c>, on the far side of the firewall, and a
    /// headless <c>circuitrf convert</c> or EM run has to resolve these references too.
    ///
    /// <para>Null when no <c>.cws</c> is there, which is what makes a moved or deleted project a
    /// reported state rather than a reference that half-resolves.</para>
    /// </summary>
    private static string? ResolveOtherRoot(string workspaceRoot, string storedPath)
    {
        if (string.IsNullOrWhiteSpace(storedPath)) return null;
        try
        {
            string native = storedPath.Replace('/', Path.DirectorySeparatorChar);
            string abs = Path.IsPathRooted(native)
                ? native
                : Path.GetFullPath(Path.Combine(workspaceRoot, native));
            // The entry names the .cws itself (R-mw2-4); tolerate one naming the folder, since a
            // hand-edited .cws is a supported way to repair one and the two are one keystroke apart.
            if (File.Exists(abs))
                return WorkspaceRootFinder.Normalize(Path.GetDirectoryName(abs));
            if (Directory.Exists(abs) && File.Exists(Path.Combine(abs, ".cws")))
                return WorkspaceRootFinder.Normalize(abs);
            return null;
        }
        catch { return null; }
    }

    /// <summary>Forgets the memoised alias tables. Called by
    /// <see cref="WorkspaceRootFinder.InvalidateCache"/>, which is already invoked wherever the
    /// resolvers that depend on it are invalidated.</summary>
    public static void InvalidateCache()
    {
        lock (_memoGate) _aliasMemo.Clear();
    }
}

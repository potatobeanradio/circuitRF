using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;

namespace CircuitRF.Design.Workspace;

/// <summary>Whether an external cell reference may be created, and the sentence to show when it may
/// not. <see cref="Refusal"/> is non-null exactly when <see cref="Permitted"/> is false.</summary>
public sealed record ExternalRefCheck(bool Permitted, string? Refusal)
{
    public static readonly ExternalRefCheck Ok = new(true, null);
    public static ExternalRefCheck No(string reason) => new(false, reason);
}

/// <summary>
/// The one hazard that has to be settled before an external cell reference can be created at all
/// (MW2 §3): <b>a layout's whole instance hierarchy is compiled against ONE technology.</b>
///
/// <para><c>LayoutRenderer.Instances</c> passes the same <c>Technology?</c> down every level and
/// looks layers up by numeric key, so an external cell's shapes are drawn using the HOST workspace's
/// layer table. Both starter technologies use keys (1,0)–(8,0), so a Drill shape from one workspace
/// silently becomes Substrate in the other — right colours, right geometry, wrong meaning, nothing
/// missing and no warning. That is the collision <c>brief-foreign-documents.md</c> §2 already
/// named, arriving through a new door.</para>
///
/// <para><b>The comparison is of the LAYER TABLE, not of the file path</b> — see
/// <see cref="LayerTableDifference"/>. MW2 shipped comparing resolved absolute paths and recorded
/// the case that would break as the one most likely to need revisiting; it did, on the first real
/// two-project pair. Two workspaces that each keep their own COPY of one technology are the ordinary
/// way to lay out two projects on one process, and a path comparison refuses them while printing two
/// identical file names — a refusal the user cannot act on because it is not true.</para>
///
/// <para><b>Per-instance technology is an explicit NON-GOAL, not an oversight</b> (R-mw2-7).
/// Rendering a sub-hierarchy under a different layer table changes <c>CompileCell</c>'s signature and
/// its caching, makes DRC's meaning ambiguous, and makes one layout view mean two different things at
/// once. It is a real feature and it is not this one.</para>
///
/// <para><b>Schematics are unaffected</b> — a schematic carries no technology — so the refusal is
/// asked only where a LAYOUT is actually involved: <see cref="CheckCellTechnology"/>, which knows
/// which cell is being placed. What a workspace's DEFAULT technology can support is a warning
/// (<see cref="WorkspaceTechnologyWarning"/>) and not a refusal, because a workspace holds as many
/// <c>.ctech</c> files as it likes and its default says nothing about the one cell anybody will
/// place.</para>
/// </summary>
public static class ExternalWorkspaceGate
{
    /// <summary>
    /// An advisory sentence for <c>File ▸ Reference Workspace…</c> when the two workspaces' DEFAULT
    /// technologies disagree, or null when they agree (or when either states none).
    ///
    /// <para><b>Advisory, deliberately, and this is a change from MW2 as briefed.</b> Creating the
    /// reference writes one alias into a <c>.cws</c> and draws nothing; the hazard arrives when a
    /// cell is PLACED, and <see cref="CheckCellTechnology"/> is asked there with both real documents
    /// in hand. Refusing on the workspace defaults instead blocks two legitimate cases outright: a
    /// workspace holding several technologies whose default is not the one the cell in question uses,
    /// and a purely schematic reference, which §3 exempts and which a default-technology refusal
    /// would nonetheless stop.</para>
    /// </summary>
    public static string? WorkspaceTechnologyWarning(
        string referencingWorkspaceRoot, string otherWorkspaceRoot, TechnologyCache? cache = null)
    {
        cache ??= new TechnologyCache();
        string? mine  = DefaultTechPath(referencingWorkspaceRoot);
        string? their = DefaultTechPath(otherWorkspaceRoot);

        // Nothing stated on one side is not a disagreement — it is a workspace nobody has drawn a
        // layout in yet, which is exactly when the reference can do no harm.
        if (mine is null || their is null) return null;
        if (SameTechnology(mine, their, cache, out string? difference)) return null;

        return $"'{FolderName(otherWorkspaceRoot)}' and '{FolderName(referencingWorkspaceRoot)}' have "
             + "different default technologies, so a LAYOUT cell drawn with either one cannot be "
             + "placed in the other — a layout's whole instance hierarchy is drawn with one layer "
             + "table. The reference is created; schematic cells, and layout cells that do share a "
             + "technology, are unaffected.\n\n"
             + TechLines(referencingWorkspaceRoot, mine, otherWorkspaceRoot, their)
             + (difference is null ? "" : $"\n{difference}\n");
    }

    /// <summary>
    /// Whether one specific external CELL may be instanced into one specific host layout — the check
    /// at placement, where both sides have a real document and a <c>.clay</c> may deviate from its
    /// workspace default by carrying its own <c>TechRef</c>. <b>This is the refusal</b>; everything
    /// else about the technology is advice.
    ///
    /// <para><paramref name="hostResolvedTechPath"/> is the technology the host layout is ALREADY
    /// drawing with — the renderer's own answer, not a second derivation of it — and null means the
    /// host resolved none, in which case its workspace default stands in.</para>
    /// </summary>
    public static ExternalRefCheck CheckCellTechnology(
        string? hostResolvedTechPath, string? hostWorkspaceRoot,
        string externalCellDir, TechnologyCache? cache = null)
    {
        cache ??= new TechnologyCache();
        string? mine  = hostResolvedTechPath is null
            ? DefaultTechPath(hostWorkspaceRoot)
            : Path.GetFullPath(hostResolvedTechPath);
        string? layout = PrimaryLayoutOf(externalCellDir);
        string? their  = ResolvedTechPathOfLayout(layout, null, cache);

        // The referenced cell has no layout at all: a schematic-only reference, which §3 exempts.
        if (layout is null) return ExternalRefCheck.Ok;

        if (SameTechnology(mine, their, cache, out string? difference)) return ExternalRefCheck.Ok;

        return ExternalRefCheck.No(Describe(
            hostWorkspaceRoot ?? "this workspace", mine,
            WorkspaceRootFinder.WorkspaceDirOf(externalCellDir) ?? externalCellDir, their, difference));
    }

    // ── The sentence ──────────────────────────────────────────────────────────

    /// <summary>
    /// The refusal, naming BOTH technologies and BOTH workspaces (R-mw2-7), what actually differs
    /// between them, and the two routes the user has. Naming the difference is not decoration: two
    /// workspaces on one process usually hold copies of one <c>.ctech</c> under the same file NAME,
    /// so a refusal that prints only the names prints the same string twice and reads like a bug.
    /// </summary>
    private static string Describe(
        string mineRoot, string? mineTech, string? theirRoot, string? theirTech, string? difference) =>
        $"'{FolderName(theirRoot)}' uses a different technology from '{FolderName(mineRoot)}', so a "
      + "cell in it cannot be referenced: a layout's whole instance hierarchy is drawn with one "
      + "layer table, and the layer keys would be reinterpreted rather than reported.\n\n"
      + TechLines(mineRoot, mineTech, theirRoot, theirTech)
      + (difference is null ? "" : $"\n{difference}\n")
      + "\nCopy the cell into this workspace instead, or change one workspace's technology.";

    private static string TechLines(
        string? mineRoot, string? mineTech, string? theirRoot, string? theirTech) =>
        $"    {FolderName(mineRoot)}: {TechDisplay(mineRoot, mineTech)}\n"
      + $"    {FolderName(theirRoot)}: {TechDisplay(theirRoot, theirTech)}\n";

    /// <summary>The <c>.ctech</c> as the workspace that owns it spells it — its path within that
    /// workspace, which distinguishes two same-named files without printing an absolute path the
    /// user did not choose to share.</summary>
    private static string TechDisplay(string? workspaceRoot, string? techPath)
    {
        if (techPath is null) return "(no technology)";
        if (string.IsNullOrEmpty(workspaceRoot)) return Path.GetFileName(techPath);
        try
        {
            string rel = Path.GetRelativePath(workspaceRoot, techPath);
            return rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel)
                ? Path.GetFileName(techPath)
                : rel.Replace('\\', '/');
        }
        catch { return Path.GetFileName(techPath); }
    }

    private static string FolderName(string? root) =>
        string.IsNullOrEmpty(root) ? "(no workspace)"
            : Path.GetFileName(root!.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    // ── Technology identity ───────────────────────────────────────────────────

    /// <summary>
    /// Whether two resolved <c>.ctech</c> paths name the SAME technology for the purpose the gate
    /// exists to serve, with <paramref name="difference"/> set to a sentence naming the first
    /// disagreement when they do not.
    ///
    /// <para>One path is settled without a load — a file is identical to itself, and that is the
    /// common case (two projects sharing one <c>.ctech</c>). Otherwise the LAYER TABLES are compared:
    /// see <see cref="LayerTableDifference"/> for what "same" means and why it is not "same
    /// bytes".</para>
    /// </summary>
    private static bool SameTechnology(
        string? a, string? b, TechnologyCache cache, out string? difference)
    {
        difference = null;
        if (a is null && b is null) return true;
        if (SamePath(a, b))         return true;
        if (a is null || b is null) return false;

        var techA = TryLoad(a, cache);
        var techB = TryLoad(b, cache);
        if (techA is null || techB is null)
        {
            // One of them cannot be read at all. That is not a comparison, so it cannot be a permit:
            // the refusal stands, and the file it names is the one to look at.
            difference = "One of these technologies could not be read.";
            return false;
        }

        difference = LayerTableDifference(techA, techB);
        return difference is null;
    }

    private static Technology? TryLoad(string absPath, TechnologyCache cache)
    {
        try { return cache.Get(absPath); }
        catch { return null; }   // corrupt or unreadable — the caller reports it as such
    }

    /// <summary>
    /// The first way two layer tables disagree, or null when they agree.
    ///
    /// <para><b>What is compared is what the renderer would reinterpret</b>: the key set, and each
    /// key's NAME and PURPOSE. Those are the layer's meaning, and a shape carries nothing but its
    /// key across the boundary. Colour, z-order, visibility, opacity and stipple are deliberately NOT
    /// compared — they are how one workspace chose to DRAW a layer, not what it means, and requiring
    /// them to match would refuse two copies of one process that differ by somebody's palette. Nor
    /// is the stackup, the DRC rule set or the technology's own Name: a stackup difference changes
    /// what a SOLVER computes, and this gate is about what the layout VIEW means. A stackup that
    /// disagrees is a real problem and it is not one an instance reference introduces — both
    /// technologies already had it.</para>
    ///
    /// <para>A key present on one side and absent on the other counts, and is reported as such:
    /// that shape does not land on a wrong layer, it lands on no layer at all, which is a different
    /// failure with the same cause.</para>
    /// </summary>
    private static string? LayerTableDifference(Technology mine, Technology theirs)
    {
        var mineByKey  = ByKey(mine);
        var theirByKey = ByKey(theirs);

        // Ordered so the sentence a user sees is the same one on every machine and every run.
        var keys = mineByKey.Keys.Concat(theirByKey.Keys).Distinct()
            .OrderBy(k => k.Layer).ThenBy(k => k.Datatype).ToList();

        foreach (var key in keys)
        {
            bool hasMine  = mineByKey.TryGetValue(key, out var a);
            bool hasTheir = theirByKey.TryGetValue(key, out var b);

            if (hasMine && !hasTheir)
                return $"They differ: layer {Spell(key)} is '{a!.Name}' here and is not declared there.";
            if (!hasMine && hasTheir)
                return $"They differ: layer {Spell(key)} is '{b!.Name}' there and is not declared here.";

            if (!string.Equals(a!.Name, b!.Name, StringComparison.Ordinal))
                return $"They differ: layer {Spell(key)} is '{a.Name}' here and '{b.Name}' there.";

            if (!string.Equals(a.Purpose ?? "", b.Purpose ?? "", StringComparison.Ordinal))
                return $"They differ: layer {Spell(key)} ('{a.Name}') has purpose "
                     + $"'{a.Purpose ?? "(none)"}' here and '{b.Purpose ?? "(none)"}' there.";
        }
        return null;
    }

    /// <summary>Layer key → definition. A table declaring one key twice is malformed; the FIRST
    /// entry wins, which is what <c>LayoutRenderer</c>'s own <c>ToDictionary</c> would throw on and
    /// what every other reader here treats as the live one.</summary>
    private static Dictionary<LayerKey, LayerDef> ByKey(Technology tech)
    {
        var map = new Dictionary<LayerKey, LayerDef>();
        foreach (var layer in tech.Layers)
            map.TryAdd(layer.Key, layer);
        return map;
    }

    private static string Spell(LayerKey key) => $"{key.Layer}/{key.Datatype}";

    // ── Resolution helpers ────────────────────────────────────────────────────

    private static bool SamePath(string? a, string? b) =>
        (a is null && b is null) || (a is not null && b is not null
            && string.Equals(a, b, StringComparison.OrdinalIgnoreCase));

    /// <summary>The workspace's own <c>DefaultTechRef</c>, resolved to an absolute path, or null.</summary>
    private static string? DefaultTechPath(string? workspaceRoot)
    {
        if (string.IsNullOrEmpty(workspaceRoot)) return null;
        try
        {
            var cws = WorkspacePersistence.LoadFromFile(Path.Combine(workspaceRoot, ".cws"));
            if (cws.DefaultTechRef is not { Length: > 0 } techRef) return null;
            return Path.GetFullPath(Path.Combine(workspaceRoot, techRef));
        }
        catch { return null; }
    }

    /// <summary>
    /// The <c>.ctech</c> one <c>.clay</c> actually resolves to, through the SAME resolver the
    /// renderer uses — R-fgn-3's ancestor-workspace walk, so each side answers from its own project
    /// rather than from whichever workspace happens to be open.
    /// </summary>
    private static string? ResolvedTechPathOfLayout(
        string? layoutPath, string? fallbackWorkspaceRoot, TechnologyCache cache)
    {
        if (layoutPath is null)
            return DefaultTechPath(fallbackWorkspaceRoot);

        string? techRef = null;
        try { techRef = LayoutPersistence.LoadFromFile(layoutPath).TechRef; }
        catch { /* unreadable layout — fall back to the workspace default below */ }

        string? fallbackCws = fallbackWorkspaceRoot is null
            ? null : Path.Combine(fallbackWorkspaceRoot, ".cws");

        var (resolution, _) = TechnologyResolver.ResolveForDocument(techRef, layoutPath, fallbackCws, cache);
        return resolution.ResolvedPath is null ? null : Path.GetFullPath(resolution.ResolvedPath);
    }

    /// <summary>The cell's primary <c>.clay</c>, or null when it has no layout view at all.</summary>
    private static string? PrimaryLayoutOf(string? cellDir)
    {
        if (string.IsNullOrEmpty(cellDir) || !Directory.Exists(cellDir)) return null;
        try
        {
            var pr = CellFolder.ResolvePrimary(cellDir, ViewType.Layout);
            if (pr.State is not (PrimaryState.SoleFile or PrimaryState.NamedPresent)) return null;
            return Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Layout), pr.ResolvedName!);
        }
        catch { return null; }
    }
}

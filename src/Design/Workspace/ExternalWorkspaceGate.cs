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
/// layer table. Both starter technologies use keys (1,0)–(8,0), so a Drill shape from workspace A
/// silently becomes Substrate in workspace B — right colours, right geometry, wrong meaning, nothing
/// missing and no warning. That is the collision <c>brief-foreign-documents.md</c> §2 already
/// named, arriving through a new door.</para>
///
/// <para><b>Per-instance technology is an explicit NON-GOAL, not an oversight</b> (R-mw2-7).
/// Rendering a sub-hierarchy under a different layer table changes <c>CompileCell</c>'s signature and
/// its caching, makes DRC's meaning ambiguous, and makes one layout view mean two different things at
/// once. It is a real feature and it is not this one.</para>
///
/// <para><b>Schematics are unaffected</b> — a schematic carries no technology — so this gate is asked
/// only when a LAYOUT view is involved.</para>
/// </summary>
public static class ExternalWorkspaceGate
{
    /// <summary>
    /// Whether workspace <paramref name="otherWorkspaceRoot"/> may be referenced from
    /// <paramref name="referencingWorkspaceRoot"/>, on technology grounds. Asked when the REFERENCE
    /// is created (<c>File ▸ Reference Workspace…</c>), where the only technology either side has
    /// stated is its workspace default.
    ///
    /// <para><b>Two workspaces with no default technology between them are permitted.</b> That is
    /// the schematic-only case, and it is also the state a workspace is in before anyone has drawn a
    /// layout — refusing it would make the reference impossible to create in exactly the situation
    /// where it cannot yet do any harm.</para>
    /// </summary>
    public static ExternalRefCheck CheckWorkspaceTechnology(
        string referencingWorkspaceRoot, string otherWorkspaceRoot, TechnologyCache cache)
    {
        string? mine  = DefaultTechPath(referencingWorkspaceRoot);
        string? their = DefaultTechPath(otherWorkspaceRoot);

        if (SamePath(mine, their)) return ExternalRefCheck.Ok;

        return ExternalRefCheck.No(Describe(
            referencingWorkspaceRoot, mine, otherWorkspaceRoot, their));
    }

    /// <summary>
    /// Whether one specific external CELL may be instanced into one specific host layout — the check
    /// at placement, where both sides have a real document and a <c>.clay</c> may deviate from its
    /// workspace default by carrying its own <c>TechRef</c>.
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
        string? their = ResolvedTechPathOfLayout(PrimaryLayoutOf(externalCellDir), null, cache);

        // The referenced cell has no layout at all: a schematic-only reference, which §3 exempts.
        if (their is null && PrimaryLayoutOf(externalCellDir) is null) return ExternalRefCheck.Ok;

        if (SamePath(mine, their)) return ExternalRefCheck.Ok;

        return ExternalRefCheck.No(Describe(
            hostWorkspaceRoot ?? "this workspace", mine,
            WorkspaceRootFinder.WorkspaceDirOf(externalCellDir) ?? externalCellDir, their));
    }

    // ── The sentence ──────────────────────────────────────────────────────────

    /// <summary>
    /// The refusal, naming BOTH technologies and BOTH workspaces (R-mw2-7) and the two routes the
    /// user actually has. A refusal that names only one side leaves them guessing which to change.
    /// </summary>
    private static string Describe(
        string mineRoot, string? mineTech, string? theirRoot, string? theirTech) =>
        $"'{FolderName(theirRoot)}' uses a different technology from '{FolderName(mineRoot)}', so a "
      + "cell in it cannot be referenced: a layout's whole instance hierarchy is drawn with one "
      + "layer table, and the layer keys would be reinterpreted rather than reported.\n\n"
      + $"    {FolderName(mineRoot)}: {TechName(mineTech)}\n"
      + $"    {FolderName(theirRoot)}: {TechName(theirTech)}\n\n"
      + "Copy the cell into this workspace instead, or change one workspace's technology.";

    private static string TechName(string? techPath) =>
        techPath is null ? "(no technology)" : Path.GetFileName(techPath);

    private static string FolderName(string? root) =>
        string.IsNullOrEmpty(root) ? "(no workspace)"
            : Path.GetFileName(root!.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

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

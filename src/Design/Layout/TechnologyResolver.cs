namespace CircuitRF.Design.Layout;

/// <summary>Where a resolved <see cref="Technology"/> came from, or that none was found.</summary>
public enum TechResolutionSource { LayoutRef, WorkspaceDefault, None }

/// <summary>
/// Result of resolving a layout's effective <see cref="Technology"/>. <see cref="Tech"/> is null
/// when nothing resolved — that is a normal, fully-supported state (§2.4 "never block on it"), not
/// an error. <see cref="Diagnostics"/> may be non-empty even when <see cref="Tech"/> is non-null
/// (a resolved technology that fails <see cref="TechValidation"/> still resolves).
/// </summary>
public sealed record TechResolution(
    Technology?           Tech,
    string?               ResolvedPath,
    TechResolutionSource  Source,
    IReadOnlyList<string> Diagnostics);

/// <summary>
/// Resolves a layout's effective <see cref="Technology"/>. Framework-free and side-effect-free —
/// it returns diagnostics but never posts them (no <c>IMessageSink</c>, no Avalonia), so it stays
/// headless-testable exactly like the workspace scanner it was written beside. The caller
/// (<c>WorkspaceViewModel</c>) is the one that posts whatever comes back.
///
/// <b>Resolution order — exactly this:</b>
/// <list type="number">
/// <item>A non-null layout <c>TechRef</c> resolves relative to the <c>.clay</c> file's own directory.</item>
/// <item>Otherwise, the workspace default (<c>CwsFile.DefaultTechRef</c>) resolves relative to the
/// workspace root.</item>
/// <item>Otherwise, <see cref="TechResolution.Tech"/> is null and
/// <see cref="TechResolution.Source"/> is <see cref="TechResolutionSource.None"/>.</item>
/// </list>
///
/// <b>A null <c>TechRef</c> means "use the workspace default" — it is not an error, and it is the
/// normal case.</b> A <c>.clay</c> only stores a <c>TechRef</c> when it deliberately deviates from
/// the workspace default. This convention matters: it means Save-As and cell moves never have to
/// rewrite a relative path, which is the kind of brittle bookkeeping that silently breaks later.
///
/// <b>All failure modes are non-fatal</b> — a missing file, unreadable/corrupt JSON, or a newer
/// <c>FormatVersion</c> each produce <see cref="TechResolution.Tech"/> = null plus one diagnostic;
/// the layout still opens and still edits. On success, <see cref="TechValidation.Validate"/> runs
/// and its findings are appended to <see cref="TechResolution.Diagnostics"/> — a technology with
/// problems still resolves and is still usable.
/// </summary>
public static class TechnologyResolver
{
    /// <param name="techRef">The layout's own <c>LayoutView.TechRef</c> (relative path, or null).</param>
    /// <param name="clayDir">Absolute directory containing the .clay file, or null for a not-yet-saved
    /// (scratch) layout — a non-null <paramref name="techRef"/> cannot be resolved without it.</param>
    /// <param name="workspaceRootDir">Absolute workspace root directory, or null when no workspace
    /// is open.</param>
    /// <param name="workspaceDefaultTechRef">The workspace's <c>CwsFile.DefaultTechRef</c> (relative
    /// path, or null when the workspace has no default technology).</param>
    /// <param name="cache">The cache to load through — never bypassed, so repeated resolutions of
    /// the same path load the file once.</param>
    public static TechResolution Resolve(
        string?         techRef,
        string?         clayDir,
        string?         workspaceRootDir,
        string?         workspaceDefaultTechRef,
        TechnologyCache cache)
    {
        if (techRef is not null && clayDir is not null)
        {
            var path = Path.GetFullPath(Path.Combine(clayDir, techRef));
            return Load(path, TechResolutionSource.LayoutRef, cache);
        }

        if (workspaceDefaultTechRef is not null && workspaceRootDir is not null)
        {
            var path = Path.GetFullPath(Path.Combine(workspaceRootDir, workspaceDefaultTechRef));
            return Load(path, TechResolutionSource.WorkspaceDefault, cache);
        }

        return new TechResolution(null, null, TechResolutionSource.None, []);
    }

    /// <summary>
    /// The resolution a DOCUMENT gets: brief-foreign-documents.md R-fgn-3's ancestor-workspace walk,
    /// then <see cref="Resolve"/>. Split out of <c>WorkspaceViewModel.ResolveTechFor</c> when the EM
    /// pipeline crossed the UI firewall (brief-cli-em-verb.md R-emcli-5) — headless there is no
    /// "current workspace", so the rule that was already correct is the rule that applies, and one
    /// implementation is what keeps <c>circuitrf em</c> and Simulate resolving the same technology.
    ///
    /// <para>R-fgn-3: the walk starts at <paramref name="documentPath"/>'s own directory and finds the
    /// nearest ancestor <c>.cws</c> — never "whichever workspace happens to be open". That is what
    /// keeps a foreign layout's layers from being reinterpreted by a different technology sharing the
    /// same numeric keys. Re-run on every call rather than cached, so a Save-As that moves the file is
    /// picked up for free.</para>
    ///
    /// <para><paramref name="fallbackCwsPath"/> is used ONLY when <paramref name="documentPath"/> is
    /// null — a not-yet-saved scratch layout has no path to walk up from, so the GUI passes its
    /// currently-open workspace and a headless caller passes null.</para>
    ///
    /// <para>Diagnostics are returned, never posted: this project has no Messages region. The caller
    /// decides — the GUI posts warnings, the CLI writes them to stderr.</para>
    /// </summary>
    /// <returns>The resolution, and the <c>.cws</c> the walk landed on (null for a document with no
    /// ancestor workspace at all — which the GUI treats as R-fgn-4's "loose file" prompt case).</returns>
    public static (TechResolution Resolution, string? OwnCwsPath) ResolveForDocument(
        string?         techRef,
        string?         documentPath,
        string?         fallbackCwsPath,
        TechnologyCache cache)
    {
        string? normalized = documentPath is null ? null : Path.GetFullPath(documentPath);

        string? ownCwsPath = normalized is not null
            ? Workspace.WorkspaceRootFinder.FindAncestorCws(Path.GetDirectoryName(normalized))
            : fallbackCwsPath;

        string? workspaceDir = ownCwsPath is null ? null : Path.GetDirectoryName(ownCwsPath);

        string? defaultTechRef = null;
        if (ownCwsPath is not null)
        {
            // A corrupt .cws is treated as "no default" rather than as a failure — the layout still
            // opens, and it is the same bargain every other .cws read in the app strikes.
            try { defaultTechRef = Workspace.WorkspacePersistence.LoadFromFile(ownCwsPath).DefaultTechRef; }
            catch { /* no default */ }
        }

        string? clayDir = normalized is null ? null : Path.GetDirectoryName(normalized);
        return (Resolve(techRef, clayDir, workspaceDir, defaultTechRef, cache), ownCwsPath);
    }

    /// <summary>
    /// Loads and validates a technology directly from a known absolute path — the same logic
    /// <see cref="Resolve"/> itself uses for both its branches, exposed so a caller that already knows
    /// exactly which file it wants (brief-foreign-documents.md R-fgn-4's session-scoped "pick a
    /// specific .ctech" override) can reuse it instead of re-deriving a second load path.
    /// </summary>
    internal static TechResolution LoadDirect(string path, TechResolutionSource source, TechnologyCache cache)
        => Load(path, source, cache);

    private static TechResolution Load(string path, TechResolutionSource source, TechnologyCache cache)
    {
        Technology? tech;
        try
        {
            tech = cache.Get(path);
        }
        catch (Exception ex)
        {
            return new TechResolution(null, path, source,
                [$"Could not load technology '{path}': {ex.Message}"]);
        }

        if (tech is null)
            return new TechResolution(null, path, source, [$"Technology file not found: {path}"]);

        var diagnostics = TechValidation.Validate(tech);
        return new TechResolution(tech, path, source, diagnostics);
    }
}

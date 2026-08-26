// What a `.cem` POINTS AT: the layout it names and the technology that layout resolves to.
//
// This is the caller-side half of an EM run — everything between "here is a path to a `.cem`" and the
// EmLayoutSource EmRunService.Run takes. It lived inside WorkspaceViewModel (ResolveEmLayoutPath /
// ResolveEmLayout / ResolveTechFor) until brief-cli-em-verb.md, and it moved for R-emcli-1's reason
// rather than for tidiness: `circuitrf em` has to resolve the SAME layout and the SAME technology the
// Simulate button does, and two implementations of a path rule are two implementations that disagree
// the day one of them is fixed.
//
// R-emcli-5 — both rules were already defined, and both are WALK-UPS rather than flags:
//
//   the layout      EmSetup.LayoutRef is relative to the WORKSPACE ROOT — the nearest ancestor `.cws`
//                   walking up from the `.cem` — and absolute when it names something outside it.
//                   With no workspace at all it falls back to the `.cem`'s own directory, which is
//                   what makes a loose `.cem` beside its `.clay` already-specified behaviour rather
//                   than a new case.
//
//   the technology  resolves against THE LAYOUT'S OWN parent workspace, found by walking up from the
//                   `.clay` (brief-foreign-documents.md R-fgn-3) — never against "the current
//                   workspace". Headless there is no current workspace, so the rule applies
//                   unchanged. See TechnologyResolver.ResolveForDocument.
//
// Note the two walks start from DIFFERENT files and can land on different workspaces. That is not an
// oversight: a `.cem` in one workspace may point at a layout in another, and the layers of that
// layout must be read by ITS technology.

using CircuitRF.Design.Workspace;

namespace CircuitRF.Design.Layout.Em;

/// <summary>An <see cref="EmLayoutSource"/> plus everything the resolution had to say about itself.
/// <see cref="Source"/> is null when the reference resolved to nothing; <see cref="Diagnostics"/> may
/// be non-empty even on success (a technology that resolves but fails validation).</summary>
public sealed record EmSetupResolution(
    EmLayoutSource?       Source,
    string?               LayoutPath,
    string?               TechnologyPath,
    IReadOnlyList<string> Diagnostics);

/// <summary>
/// Resolves a <c>.cem</c>'s references. Framework-free and side-effect-free — it returns diagnostics
/// but never posts them, exactly like <see cref="TechnologyResolver"/>, so the GUI can raise them as
/// Messages and the CLI can write them to stderr.
/// </summary>
public static class EmSetupResolver
{
    /// <summary>
    /// The absolute <c>.clay</c> path a <see cref="EmSetup.LayoutRef"/> names, or null when it names
    /// nothing. Exposed separately from <see cref="Resolve"/> so a caller can ask "does this
    /// <c>.cem</c> point at THAT layout?" without loading the geometry — the two must agree about
    /// what a reference resolves to, and one method is how that is guaranteed.
    /// </summary>
    /// <param name="cemPath">Absolute path of the <c>.cem</c>.</param>
    /// <param name="layoutRef">The setup's own <see cref="EmSetup.LayoutRef"/>.</param>
    /// <param name="workspaceCwsPath">The workspace the reference is relative to, or null to fall
    /// back to the <c>.cem</c>'s own directory.</param>
    public static string? ResolveLayoutPath(string cemPath, string layoutRef, string? workspaceCwsPath)
    {
        if (layoutRef.Length == 0) return null;

        if (Path.IsPathRooted(layoutRef)) return Path.GetFullPath(layoutRef);

        string baseDir = workspaceCwsPath is { } cws
            ? Path.GetDirectoryName(cws)!
            : Path.GetDirectoryName(Path.GetFullPath(cemPath))!;

        return Path.GetFullPath(Path.Combine(baseDir, layoutRef));
    }

    /// <summary>The inverse of <see cref="ResolveLayoutPath"/>: how an absolute <c>.clay</c> path is
    /// STORED in a <c>.cem</c> — relative to the workspace root when the layout sits inside it,
    /// absolute otherwise. Written beside its inverse so the pair cannot drift; a Change Layout that
    /// wrote a reference the resolver could not read would look like a corrupt file rather than a bad
    /// conversion.</summary>
    public static string MakeLayoutRef(string cemPath, string absoluteClayPath, string? workspaceCwsPath)
    {
        string full = Path.GetFullPath(absoluteClayPath);
        string baseDir = workspaceCwsPath is { } cws
            ? Path.GetDirectoryName(cws)!
            : Path.GetDirectoryName(Path.GetFullPath(cemPath))!;

        string rel = Path.GetRelativePath(baseDir, full);
        // A path that climbs out of the base is not usefully "relative to the workspace" — store it
        // absolutely rather than as a ../../.. chain that breaks the moment the workspace moves.
        return rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel) ? full : rel;
    }

    /// <summary>
    /// Resolves the layout and its technology.
    ///
    /// <para>R-em-10: the geometry is read HERE, at use time, and never embedded in the <c>.cem</c> —
    /// which is the whole reason re-running after a layout edit picks the edit up.</para>
    /// </summary>
    /// <param name="liveView">Optional hook returning the LIVE model for an already-open
    /// <c>.clay</c>, keyed by absolute path — so the GUI analyses an unsaved edit rather than the
    /// last save. A headless caller passes null and always reads from disk, which is the correct
    /// behaviour there: there is nothing open.</param>
    public static EmSetupResolution Resolve(
        string                    cemPath,
        string                    layoutRef,
        string?                   workspaceCwsPath,
        TechnologyCache           cache,
        Func<string, LayoutView?>? liveView = null)
    {
        if (ResolveLayoutPath(cemPath, layoutRef, workspaceCwsPath) is not { } abs)
            return new EmSetupResolution(null, null, null,
                ["This EM setup names no layout, so there is no geometry to analyse."]);

        var view = liveView?.Invoke(abs);

        if (view is null)
        {
            if (!File.Exists(abs))
                return new EmSetupResolution(null, abs, null, [$"Layout file not found: {abs}"]);

            try { view = LayoutPersistence.LoadFromFile(abs); }
            catch (Exception ex)
            {
                return new EmSetupResolution(null, abs, null,
                    [$"Could not read layout '{abs}': {ex.Message}"]);
            }
        }

        var (tech, _) = TechnologyResolver.ResolveForDocument(view.TechRef, abs, workspaceCwsPath, cache);

        return new EmSetupResolution(
            new EmLayoutSource(abs, view, tech.Tech, view.DbuPerMicron),
            abs, tech.ResolvedPath, tech.Diagnostics);
    }
}

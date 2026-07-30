namespace CircuitRF.Ui.Layout.PCells;

/// <summary>
/// brief-L5-followups-2.md §4/R-L5g-6/7/8: framework-free (no Avalonia) implementation of the
/// generated-cell lifecycle policy — "a generated cell is a pure, deletable, rebuildable-from-the-
/// layout cache, never authoritative" — factored out of <c>WorkspaceViewModel</c> so it is directly
/// unit-testable without constructing a VM that needs a live Avalonia app host (this codebase's own
/// standing constraint; see <c>src/Ui/CLAUDE.md</c>'s "Testing without the Avalonia runtime" note).
///
/// <b>Why this is safe (§4.1's own warning) — the property R-L5g-6 establishes first:</b> every
/// generated cell a layout references has a matching <see cref="LayoutView.PCellSnapshots"/> entry
/// carrying everything <see cref="GeneratedCellStore.GetOrCreate"/> needs to rebuild it byte-
/// identically — schematic-linked, palette-dropped, and layout-authored instances alike, since
/// <see cref="GeneratedCellStore.RecordSnapshot"/> is called from every site that ever calls
/// <c>GetOrCreate</c> from a layout context. Deleting the folder therefore never loses data that
/// cannot be reconstructed.
/// </summary>
public static class GeneratedCellsLifecycle
{
    /// <summary>R-L5g-7: delete the whole <c>.generated-cells</c> folder under
    /// <paramref name="workspaceRootDir"/> — leaves a clean workspace on disk (close) and guarantees a
    /// clean start even after a crash (open, called again before <see cref="RegenerateAll"/>).
    /// Best-effort: throws are caught by the caller if it wants to report them; a missing folder is a
    /// silent no-op.</summary>
    public static void DeleteGeneratedCellsFolder(string workspaceRootDir)
    {
        var dir = Path.Combine(workspaceRootDir, GeneratedCellStore.ReservedFolderName);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    /// <summary>
    /// R-L5g-8: eagerly rebuilds every generated cell any <c>.clay</c> under
    /// <paramref name="workspaceRootDir"/> actually references, from each layout's own
    /// <see cref="LayoutView.PCellSnapshots"/> record — so every layout renders correctly the moment
    /// it opens, with no per-render lazy-regeneration plumbing needed anywhere else.
    /// <paramref name="resolveTech"/> resolves a snapshot's own recorded technology identity (its
    /// resolved <c>.ctech</c> path) to a live <see cref="Technology"/> for the generator to consume —
    /// the caller supplies this (typically backed by a small memoized <c>TechPersistence.LoadFromFile</c>
    /// call) so this class stays free of any technology-CACHING policy decision of its own.
    /// A corrupt/unreadable <c>.clay</c>, or a single bad snapshot entry, is skipped (best-effort)
    /// rather than blocking the rest of the workspace from opening.
    /// </summary>
    public static void RegenerateAll(string workspaceRootDir, Func<string?, Technology?> resolveTech)
    {
        string genRootPrefix = Path.GetFullPath(Path.Combine(workspaceRootDir, GeneratedCellStore.ReservedFolderName))
            + Path.DirectorySeparatorChar;

        IEnumerable<string> clayFiles;
        try { clayFiles = Directory.EnumerateFiles(workspaceRootDir, "*.clay", SearchOption.AllDirectories); }
        catch { return; }

        foreach (var clayPath in clayFiles)
        {
            // .generated-cells itself is the REGENERATION TARGET, never a source of further snapshots
            // to walk — its own .clay files were just deleted (DeleteGeneratedCellsFolder) and are
            // about to be recreated here.
            if (Path.GetFullPath(clayPath).StartsWith(genRootPrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            LayoutView view;
            try { view = LayoutPersistence.LoadFromFile(clayPath); }
            catch { continue; }
            if (view.PCellSnapshots.Count == 0) continue;

            foreach (var snap in view.PCellSnapshots.Values)
            {
                try
                {
                    GeneratedCellStore.GetOrCreate(
                        workspaceRootDir, snap.GeneratorId, snap.Parameters, resolveTech(snap.TechIdentity),
                        snap.TechIdentity, new PCellLayerSelection(snap.SignalLayerNameOverride, snap.GroundLayerNameOverride));
                }
                catch { /* best-effort — a corrupt snapshot must not block opening the rest of the workspace */ }
            }
        }
    }
}

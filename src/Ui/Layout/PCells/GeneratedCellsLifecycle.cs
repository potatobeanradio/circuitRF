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
    /// <param name="report">Where a snapshot that could not be rebuilt goes. Null discards, which is
    /// the pre-B7 behaviour — but a script that fails is exactly what an author needs told, so the
    /// application supplies one.</param>
    /// <param name="skipPaths">Layouts the caller is holding open in memory and will repoint itself.
    /// Rewriting the file under an open document would fight whatever is unsaved in it.</param>
    public static RegenerateOutcome RegenerateAll(
        string workspaceRootDir,
        Func<string?, Technology?> resolveTech,
        Action<string>? report = null,
        IReadOnlySet<string>? skipPaths = null)
    {
        string genRootPrefix = Path.GetFullPath(Path.Combine(workspaceRootDir, GeneratedCellStore.ReservedFolderName))
            + Path.DirectorySeparatorChar;

        IEnumerable<string> clayFiles;
        try { clayFiles = Directory.EnumerateFiles(workspaceRootDir, "*.clay", SearchOption.AllDirectories); }
        catch { return default; }

        int repointed = 0, rewritten = 0;

        foreach (var clayPath in clayFiles)
        {
            // .generated-cells itself is the REGENERATION TARGET, never a source of further snapshots
            // to walk — its own .clay files were just deleted (DeleteGeneratedCellsFolder) and are
            // about to be recreated here.
            if (Path.GetFullPath(clayPath).StartsWith(genRootPrefix, StringComparison.OrdinalIgnoreCase))
                continue;
            if (skipPaths is not null && skipPaths.Contains(Path.GetFullPath(clayPath))) continue;

            LayoutView view;
            try { view = LayoutPersistence.LoadFromFile(clayPath); }
            catch { continue; }
            if (view.PCellSnapshots.Count == 0) continue;

            int moved = Regenerate(workspaceRootDir, view, resolveTech, report);
            if (moved == 0) continue;

            repointed += moved;
            try { LayoutPersistence.SaveToFile(clayPath, view); rewritten++; }
            catch (Exception ex) { report?.Invoke($"'{clayPath}' could not be updated: {ex.Message}"); }
        }

        return new RegenerateOutcome(repointed, rewritten);
    }

    /// <summary>
    /// Rebuilds every generated cell <paramref name="view"/> references and — the part that matters
    /// once a generator can be EDITED — repoints its instances when the rebuild lands somewhere new.
    /// Returns how many instances moved; zero means nothing about the view changed.
    ///
    /// <para><b>This closes a gap B5's content hash opened.</b> A generated cell's folder name is a
    /// hash that now includes the generator's own content, so editing a script changes the name.
    /// <see cref="LayoutView.PCellSnapshots"/> is keyed by that name and an instance's
    /// <see cref="LayoutInstance.CellRef"/> points at it — so without this, editing a script and
    /// reopening the workspace regenerates every cell under a NEW name and leaves every placed
    /// instance pointing at a folder that will now never be built. The design would open full of
    /// Not Found placeholders, and nothing would say why.</para>
    ///
    /// <para>Mutates <paramref name="view"/> in place; the caller decides whether that means saving a
    /// file or marking an open document dirty.</para>
    /// </summary>
    public static int Regenerate(
        string workspaceRootDir,
        LayoutView view,
        Func<string?, Technology?> resolveTech,
        Action<string>? report = null)
    {
        var rekeyed = new Dictionary<string, PCellSnapshot>(StringComparer.Ordinal);
        int repointed = 0;
        bool changed = false;

        foreach (var (oldName, snap) in view.PCellSnapshots)
        {
            string cellDir;
            try
            {
                cellDir = GeneratedCellStore.GetOrCreate(
                    workspaceRootDir, snap.GeneratorId, snap.Parameters, resolveTech(snap.TechIdentity),
                    snap.TechIdentity, new PCellLayerSelection(snap.SignalLayerNameOverride, snap.GroundLayerNameOverride));
            }
            catch (Exception ex)
            {
                // Best-effort per snapshot — one generator that will not run must not stop the rest of
                // the workspace opening. Reported rather than swallowed: for an author editing a
                // script, this message IS the error report.
                report?.Invoke($"The cells generated by '{snap.GeneratorId}' could not be rebuilt: {ex.Message}");
                rekeyed[oldName] = snap;
                continue;
            }

            string newName = Path.GetFileName(cellDir);
            if (string.Equals(newName, oldName, StringComparison.Ordinal)) { rekeyed[oldName] = snap; continue; }

            // The cell moved because its generator changed. Every instance naming the old folder now
            // names the new one; a CellRef is a relative path, so only its last segment moves.
            foreach (var inst in view.Instances)
            {
                if (!NamesCell(inst.CellRef, oldName)) continue;
                inst.CellRef = ReplaceLastSegment(inst.CellRef, newName);
                repointed++;
            }

            rekeyed[newName] = snap;
            changed = true;
        }

        if (!changed) return 0;

        view.PCellSnapshots.Clear();
        foreach (var (name, snap) in rekeyed) view.PCellSnapshots[name] = snap;
        return repointed;
    }

    private static bool NamesCell(string? cellRef, string cellName)
        => cellRef is { Length: > 0 }
           && string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(cellRef)), cellName,
                            StringComparison.OrdinalIgnoreCase);

    private static string ReplaceLastSegment(string cellRef, string newName)
    {
        string trimmed = Path.TrimEndingDirectorySeparator(cellRef);
        string? parent = Path.GetDirectoryName(trimmed);
        return string.IsNullOrEmpty(parent) ? newName : Path.Combine(parent, newName);
    }
}

/// <summary>What a regeneration pass actually changed. Zero of both is the ordinary case — nothing
/// about the generators moved since last time.</summary>
public readonly record struct RegenerateOutcome(int InstancesRepointed, int LayoutsRewritten);

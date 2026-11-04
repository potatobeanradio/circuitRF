using CircuitRF.Design.Cells;

namespace CircuitRF.Design.Layout.PCells;

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
    /// Whether the whole generated-cell folder is wiped on every workspace open and close — the
    /// original R-L5g-7 policy — instead of being kept across sessions and pruned.
    ///
    /// <para><b>Off by default now, because the assumption underneath R-L5g-7 stopped holding.</b>
    /// "Both are cheap once R-L5g-6 holds" was true while every generator was a built-in drawing
    /// geometry in-process. A kit's own generators are SCRIPTS, and a script runs per cell: measured
    /// on a real kit, ten cells cost 2-800 ms each and 1.65 s in total, paid in full on every single
    /// open of that workspace. Nothing about that work is startup — it is per-cell geometry.</para>
    ///
    /// <para><b>What makes keeping the folder safe is the same property that made deleting it
    /// safe.</b> The folder's NAME is a content hash of the generator's own content, the resolved
    /// parameters, the layer selection and the technology's content (see
    /// <see cref="GeneratedCellStore.GetOrCreate"/>), so a folder that is there is by construction
    /// the right geometry — which is why <c>GetOrCreate</c> already returns a hit without
    /// re-generating or re-verifying anything. Editing a generator or a technology produces a
    /// DIFFERENT name, so the stale one is never read; it becomes garbage, and
    /// <see cref="RegenerateAll"/>'s prune pass is what collects it.</para>
    ///
    /// <para>Kept as a switch rather than deleted: this reverses a deliberate policy decision, and
    /// setting it back to true restores R-L5g-7 exactly — the wipe on open, the wipe on close, and
    /// the full rebuild in between — with no other change anywhere. The prune pass turns itself off
    /// while it is set, since a folder that is about to be wiped has nothing to collect.</para>
    /// </summary>
    public static bool WipeOnOpenAndClose { get; set; }

    /// <summary>
    /// Removes generated cell folders no live layout names any more.
    ///
    /// <para>This is what replaces the wipe. Without it the folder only ever grows — editing a
    /// generator, a parameter or the technology renames a cell rather than replacing it, and the
    /// old one would sit there for the life of the workspace (the brief's own "there is no garbage
    /// collection for them by design", which the wipe was quietly doing).</para>
    ///
    /// <para><b>Refuses to run rather than guess.</b> Deleting a cell some layout still references
    /// costs that layout its artwork, so the live set has to be COMPLETE to be actionable: if any
    /// <c>.clay</c> could not be read, or the caller is holding layouts out of the walk
    /// (<c>skipPaths</c>), nothing is pruned. An uncollected folder is harmless; a wrongly collected
    /// one is not.</para>
    /// </summary>
    /// <returns>How many cell folders were removed.</returns>
    private static int PruneUnreferenced(string workspaceRootDir, IReadOnlySet<string> liveCellNames)
    {
        var genRoot = Path.Combine(workspaceRootDir, GeneratedCellStore.ReservedFolderName);
        if (!Directory.Exists(genRoot)) return 0;

        int removed = 0;
        IEnumerable<string> present;
        try { present = Directory.EnumerateDirectories(genRoot); }
        catch { return 0; }

        foreach (var dir in present)
        {
            if (liveCellNames.Contains(Path.GetFileName(dir))) continue;
            try { Directory.Delete(dir, recursive: true); removed++; }
            catch { /* a locked folder stays; it is a cache entry, not a result */ }
        }

        return removed;
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
        int repointed = 0, rewritten = 0;

        // What the prune pass is allowed to keep. Complete or nothing: see PruneUnreferenced.
        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        bool? walked = WalkSnapshotLayouts(workspaceRootDir, skipPaths, (clayPath, view) =>
        {
            int moved = Regenerate(workspaceRootDir, view, resolveTech, report);

            // Read AFTER the rebuild, so a cell whose generator/technology changed contributes its
            // NEW name — the old one is exactly what the prune is there to collect.
            foreach (var name in view.PCellSnapshots.Keys) live.Add(name);
            // A placed generated cell that has no snapshot — an EM component's variant (brief-sdl-10 D3) — is
            // kept because an instance names it.
            foreach (var name in GeneratedCellRefs(view)) live.Add(name);

            if (moved == 0) return;

            repointed += moved;
            try { LayoutPersistence.SaveToFile(clayPath, view); rewritten++; }
            catch (Exception ex) { report?.Invoke($"'{clayPath}' could not be updated: {ex.Message}"); }
        });
        if (walked is not { } sawEverything) return default;

        int pruned = WipeOnOpenAndClose || !sawEverything
            ? 0
            : PruneUnreferenced(workspaceRootDir, live);

        return new RegenerateOutcome(repointed, rewritten, pruned);
    }

    /// <summary>
    /// The generated cells the layouts under <paramref name="workspaceRootDir"/> reference, as they
    /// stand on disk now — cell folder name → the generator that drew it. The SAME walk
    /// <see cref="RegenerateAll"/>'s prune keeps its live set from, minus the rebuild, so an archive
    /// that carries "the live cells" carries exactly what the prune would keep.
    ///
    /// <para><see cref="LiveGeneratedCells.Complete"/> is false when some <c>.clay</c> could not be
    /// read. A caller deciding what to LEAVE OUT must then leave nothing out — the prune's own
    /// refuse-rather-than-guess rule.</para>
    /// </summary>
    public static LiveGeneratedCells LiveCells(string workspaceRootDir)
    {
        var cells = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool? walked = WalkSnapshotLayouts(workspaceRootDir, skipPaths: null, (_, view) =>
        {
            // A variant (brief-sdl-10 D3) has no generator: it is named by an instance, with an empty id.
            foreach (var name in GeneratedCellRefs(view)) cells.TryAdd(name, "");
            foreach (var (name, snap) in view.PCellSnapshots) cells[name] = snap.GeneratorId;
        });
        return new LiveGeneratedCells(cells, walked == true);
    }

    /// <summary>
    /// Every <c>.clay</c> under <paramref name="workspaceRootDir"/> that carries PCell snapshots or places a
    /// generated cell, loaded and handed to <paramref name="visit"/> — then the layout of every VARIANT those
    /// layouts place (brief-sdl-10 D3), and of every variant a variant places. A variant lives in the
    /// generated-cells folder but is placed like a cell: its own layout's snapshots name the PCells drawn at
    /// that placement's values, which no layout outside the folder names. Returns whether every layout was
    /// read (false when one would not parse or <paramref name="skipPaths"/> held some out), or null when the
    /// workspace could not be walked at all.
    /// </summary>
    private static bool? WalkSnapshotLayouts(
        string workspaceRootDir, IReadOnlySet<string>? skipPaths, Action<string, LayoutView> visit)
    {
        string genRootPrefix = Path.GetFullPath(Path.Combine(workspaceRootDir, GeneratedCellStore.ReservedFolderName))
            + Path.DirectorySeparatorChar;

        IEnumerable<string> clayFiles;
        try { clayFiles = Directory.EnumerateFiles(workspaceRootDir, "*.clay", SearchOption.AllDirectories); }
        catch { return null; }

        bool sawEverything = skipPaths is null || skipPaths.Count == 0;
        var variants = new Queue<string>();

        foreach (var clayPath in clayFiles)
        {
            // .generated-cells itself is the REGENERATION TARGET, never a source of further snapshots
            // to walk — its own .clay files were just deleted (DeleteGeneratedCellsFolder) and are
            // about to be recreated here.
            if (Path.GetFullPath(clayPath).StartsWith(genRootPrefix, StringComparison.OrdinalIgnoreCase))
                continue;
            if (skipPaths is not null && skipPaths.Contains(Path.GetFullPath(clayPath))) continue;

            LayoutView view;
            try
            {
                // A layout carrying no snapshots contributes nothing to this pass, and establishing
                // that must not cost a full load. It used to: this runs on WORKSPACE OPEN, over every
                // layout under the workspace, and loading an imported board is seconds rather than
                // milliseconds (LayoutPersistence.LoadFromFile's own remarks say why). A workspace
                // holding three of them spent 4.96 s here before its window was usable, and found no
                // snapshots in any of them — the overwhelming common case.
                //
                // The sniff answers that in 0.07 s and answers it conservatively: anything but a
                // definite no still loads the file and asks it properly, and a file that does not
                // parse throws, which is what keeps `sawEverything` — and therefore the prune — honest
                // about a layout nobody could read.
                if (!LayoutPersistence.MightCarryPCellSnapshots(clayPath) && !MightPlaceGeneratedCell(clayPath)) continue;
                view = LayoutPersistence.LoadFromFile(clayPath);
            }
            catch { sawEverything = false; continue; }
            if (view.PCellSnapshots.Count == 0 && !GeneratedCellRefs(view).Any()) continue;

            visit(clayPath, view);
            foreach (var name in GeneratedCellRefs(view))
                if (name.Contains(GeneratedCellStore.VariantSeparator, StringComparison.Ordinal)) variants.Enqueue(name);
        }

        // brief-sdl-10 D3 — the variants, transitively. A variant whose folder is missing has nothing to keep.
        var walkedVariants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (variants.TryDequeue(out var name))
        {
            if (!walkedVariants.Add(name)) continue;
            string cellDir = Path.Combine(genRootPrefix, name);
            string clay;
            try
            {
                if (!Directory.Exists(cellDir)
                    || CellFolder.ResolvePrimary(cellDir, ViewType.Layout).ResolvedName is not { Length: > 0 } file) continue;
                clay = Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Layout), file);
            }
            catch { sawEverything = false; continue; }
            if (skipPaths is not null && skipPaths.Contains(Path.GetFullPath(clay))) continue;
            LayoutView view;
            try { view = LayoutPersistence.LoadFromFile(clay); }
            catch { sawEverything = false; continue; }
            visit(clay, view);
            foreach (var inner in GeneratedCellRefs(view))
                if (inner.Contains(GeneratedCellStore.VariantSeparator, StringComparison.Ordinal)) variants.Enqueue(inner);
        }

        return sawEverything;
    }

    /// <summary>
    /// The generated-cell folders <paramref name="view"/>'s instances name: the segment after the reserved folder's
    /// name in each <c>CellRef</c>, read with either separator. Distinct, in instance order.
    /// </summary>
    internal static IEnumerable<string> GeneratedCellRefs(LayoutView view)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var inst in view.Instances)
        {
            if (inst.CellRef is not { Length: > 0 } r) continue;
            var segments = r.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < segments.Length - 1; i++)
                if (string.Equals(segments[i], GeneratedCellStore.ReservedFolderName, StringComparison.OrdinalIgnoreCase))
                {
                    if (seen.Add(segments[i + 1])) yield return segments[i + 1];
                    break;
                }
        }
    }

    /// <summary>Whether the file's text names the generated-cells folder at all — the cheap test that lets a layout
    /// with no snapshots and no generated cell skip its full load. True over-reports, as the snapshot sniff does.</summary>
    private static bool MightPlaceGeneratedCell(string clayPath)
    {
        ReadOnlySpan<byte> text = GzipTextFile.ReadAllBytesAutoGzip(clayPath);
        ReadOnlySpan<byte> folder = System.Text.Encoding.UTF8.GetBytes(GeneratedCellStore.ReservedFolderName);
        return text.IndexOf(folder) >= 0;
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

        foreach (var (oldName, recorded) in view.PCellSnapshots)
        {
            var snap = Canonical(workspaceRootDir, recorded);

            string cellDir;
            try
            {
                cellDir = Rebuild(workspaceRootDir, snap, resolveTech, GeneratedCellTarget.Disk);
            }
            catch (Exception ex)
            {
                // Best-effort per snapshot — one generator that will not run must not stop the rest of
                // the workspace opening. Reported rather than swallowed: for an author editing a
                // script, this message IS the error report. The GUI keeps its placeholder; when the
                // folder is MISSING, the line is the sentence a headless run refuses with (R-gc2-3).
                string oldDir = Path.Combine(workspaceRootDir, GeneratedCellStore.ReservedFolderName, oldName);
                report?.Invoke(IsOnDisk(oldDir)
                    ? $"The cells generated by '{snap.GeneratorId}' could not be rebuilt: {ex.Message}"
                    : CouldNotRebuild(oldName, snap.GeneratorId, ex.Message));
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

    /// <summary>
    /// The snapshot as it is rebuilt: its technology identity in the WORKSPACE-RELATIVE spelling,
    /// resolved here for the loader — GeneratedCellStore's CanonicalTechIdentity says why. A snapshot
    /// recorded as an absolute path is rewritten to it, which renames the cell.
    /// </summary>
    internal static PCellSnapshot Canonical(string workspaceRootDir, PCellSnapshot recorded)
    {
        string? identity = GeneratedCellStore.CanonicalTechIdentity(workspaceRootDir, recorded.TechIdentity);
        return identity == recorded.TechIdentity ? recorded : recorded with { TechIdentity = identity };
    }

    /// <summary>
    /// ONE snapshot, rebuilt — the step the application's open and a headless run share
    /// (brief-generated-cells-2 R-gc2-2), so the two can never generate different artwork. Returns the
    /// cell folder, which is a new name when the generator or technology changed since the snapshot
    /// was recorded. Throws what the generator throws.
    /// </summary>
    internal static string Rebuild(
        string workspaceRootDir, PCellSnapshot snap, Func<string?, Technology?> resolveTech, GeneratedCellTarget target)
        => GeneratedCellStore.GetOrCreate(
            workspaceRootDir, snap.GeneratorId, snap.Parameters,
            resolveTech(GeneratedCellStore.ResolveTechPath(workspaceRootDir, snap.TechIdentity)),
            snap.TechIdentity, new PCellLayerSelection(snap.SignalLayerNameOverride, snap.GroundLayerNameOverride),
            target: target);

    /// <summary>
    /// The sentence for a placed generated cell that is not on disk and cannot be rebuilt — said by
    /// the application's Messages line and by a headless refusal alike (R-gc2-3), so a user who sees
    /// one has already read the other.
    /// </summary>
    public static string CouldNotRebuild(string cellName, string? generatorId, string reason)
        => generatorId is { Length: > 0 }
            ? $"The generated cell '{cellName}' (generator '{generatorId}') is not on disk and could not be rebuilt: {reason}"
            : $"The generated cell '{cellName}' is not on disk and could not be rebuilt: {reason}";

    internal static bool IsOnDisk(string cellDir)
    {
        try { return CellFolder.ResolvePrimary(cellDir, ViewType.Layout).ResolvedName is { Length: > 0 }; }
        catch { return false; }
    }

    // ── EITHER SEPARATOR, WHATEVER MACHINE THIS IS (owner, 2026-09-24) ─────────────────────────
    //
    // A CellRef is stored as its author's machine spelled it, and a layout placed on Windows says
    // `..\..\..\.generated-cells\smt-0402@N_…`. Path.GetFileName on macOS and Linux does not split
    // on a backslash, so the whole string was compared with the cell name, nothing matched, nothing
    // was repointed — and a designer's board opened here with every footprint missing and no pads,
    // after its cells had been regenerated under their new names right beside it. The resolver that
    // LOADS a CellRef already reads both separators; the repoint has to as well.

    private static bool NamesCell(string? cellRef, string cellName)
        => cellRef is { Length: > 0 }
           && string.Equals(LastSegment(cellRef, out _), cellName, StringComparison.OrdinalIgnoreCase);

    /// <summary>The ref with its last segment renamed, keeping the author's own separators — a
    /// Windows-written layout stays Windows-spelled, so the diff is the one name that changed.</summary>
    private static string ReplaceLastSegment(string cellRef, string newName)
    {
        LastSegment(cellRef, out int start);
        return cellRef.TrimEnd('/', '\\')[..start] + newName;
    }

    private static string LastSegment(string cellRef, out int start)
    {
        string trimmed = cellRef.TrimEnd('/', '\\');
        start = trimmed.LastIndexOfAny(['/', '\\']) + 1;
        return trimmed[start..];
    }
}

/// <summary>What a regeneration pass actually changed. Zero of both is the ordinary case — nothing
/// about the generators moved since last time.</summary>
/// <param name="CellsPruned">Generated cell folders removed because no layout names them any more.
/// Always 0 when the walk was incomplete or the wipe policy is on — see
/// <see cref="GeneratedCellsLifecycle.WipeOnOpenAndClose"/>.</param>
public readonly record struct RegenerateOutcome(int InstancesRepointed, int LayoutsRewritten, int CellsPruned);

/// <summary>The generated cells a workspace's layouts reference — see
/// <see cref="GeneratedCellsLifecycle.LiveCells"/>.</summary>
/// <param name="Cells">Cell folder name → the generator id its snapshot records.</param>
/// <param name="Complete">False when some layout could not be read, so the set may be missing cells.</param>
public sealed record LiveGeneratedCells(IReadOnlyDictionary<string, string> Cells, bool Complete);

namespace CircuitRF.Ui.Schematic;

// ──────────────────────────────────────────────────────────────────────────────
//  WorkspaceScanner — turns a workspace folder into the in-memory project tree.
//  Phase 6g Step 2.  Framework-free (no Avalonia / Skia); headless-testable.
//
//  The filesystem IS the workspace.  Membership is discovered by scanning;
//  the .cws is read only for referenced libraries + Known Files.  Primacy is
//  never re-derived here — always delegated to CellFolder.ResolvePrimary.
//
//  Stable ordering: alphabetical (OrdinalIgnoreCase) within every level.
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>
/// SL4 R-sl4-10 — whether one scan walks the REFERENCED sub-trees (a referenced library, a
/// referenced workspace) or reuses what the last walk found.
///
/// <para>The workspace's own folders are always walked, on every scan, exactly as before: they are
/// local almost always, they are the ones the user is editing, and they are the reason the on-focus
/// rescan exists at all. A referenced library is neither — it changes on someone else's schedule,
/// possibly at the far end of a wire, and the user's own gesture (expanding it, or pressing Refresh)
/// is a better trigger for re-reading it than alt-tab is.</para>
/// </summary>
public enum ReferencedSubtrees
{
    /// <summary>Walk them. Workspace open, explicit Refresh, and first expansion.</summary>
    Walk,

    /// <summary>
    /// Reuse the previous walk's contents for each referenced root, and render one that has never
    /// been walked as <see cref="NodeKind.NotReadYet"/> rather than as empty (R-sl4-11). The
    /// on-focus rescan.
    /// </summary>
    Reuse,
}

public static class WorkspaceScanner
{
    private const string CwsFileName = ".cws";

    // ── Public entry point ────────────────────────────────────────────────────

    /// <summary>
    /// Scans <paramref name="workspaceRootDir"/> and returns the root
    /// <see cref="ProjectTreeNode"/> for the workspace.  Idempotent and cheap
    /// enough to call on every focus or Refresh command.
    /// </summary>
    /// <param name="referenced">
    /// SL4 R-sl4-10. <see cref="ReferencedSubtrees.Walk"/> (the default, and what every caller did
    /// before SL4) reads the referenced libraries and workspaces from disk;
    /// <see cref="ReferencedSubtrees.Reuse"/> takes their contents from <paramref name="previous"/>.
    /// </param>
    /// <param name="previous">
    /// The tree currently on screen, whose referenced sub-trees <see cref="ReferencedSubtrees.Reuse"/>
    /// carries forward. Ignored when walking. Null with Reuse means nothing has been read yet, which
    /// renders as itself (R-sl4-11) rather than as an empty library.
    /// </param>
    public static ProjectTreeNode Scan(
        string workspaceRootDir,
        ReferencedSubtrees referenced = ReferencedSubtrees.Walk,
        ProjectTreeNode? previous = null)
    {
        workspaceRootDir = Path.GetFullPath(workspaceRootDir);
        var carried = referenced == ReferencedSubtrees.Reuse ? IndexReferencedRoots(previous) : null;

        var root = new ProjectTreeNode(
            NodeKind.Workspace,
            name: FolderDisplayName(workspaceRootDir),
            absolutePath: workspaceRootDir,
            relativePath: "");

        CwsFile cws = TryLoadCws(workspaceRootDir);

        // Cells and user folders — intermixed, alphabetical. The reserved generated-cells folder
        // (R-L5-3) is excluded here and rendered instead as one synthetic group below, exactly as
        // Libraries/Known Files already are — never as a peer UserFolder full of machine-named cells.
        foreach (string subDir in SubDirsSorted(workspaceRootDir))
        {
            root.AddChild(File.Exists(Path.Combine(subDir, CellFolder.CcellFileName))
                ? BuildCellNode(subDir, workspaceRootDir)
                : BuildUserFolderNode(subDir, workspaceRootDir));
        }

        // Loose files at the workspace root (e.g. .cdd, .ccolor) — alphabetical; .cws excluded.
        foreach (string f in Directory.GetFiles(workspaceRootDir)
            .OrderBy(fn => Path.GetFileName(fn) ?? fn, StringComparer.OrdinalIgnoreCase))
        {
            if (string.Equals(Path.GetFileName(f), CwsFileName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (IsHiddenTreeFile(f))
                continue;
            root.AddChild(BuildFileNode(f, workspaceRootDir));
        }

        // Referenced libraries (from .cws) — alphabetical by ref string
        if (cws.LibraryRefs.Count > 0)
        {
            var libGroup = new ProjectTreeNode(NodeKind.LibrariesGroup, "Libraries", workspaceRootDir, "");
            foreach (string libRef in cws.LibraryRefs.OrderBy(r => r, StringComparer.OrdinalIgnoreCase))
                libGroup.AddChild(ResolveLibrary(libRef, workspaceRootDir, carried));
            root.AddChild(libGroup);
        }

        // Referenced workspaces (from .cws) — alphabetical by alias, AT THE ROOT, one row each.
        //
        // <b>No group node</b> (owner, 2026-09-04). "Referenced Workspaces" was a heading whose only
        // content is a handful of rows that already say what they are: the network-folder icon marks
        // a row as a reference, so the heading spent a row of the tree repeating it. Each is still its
        // own sub-tree of cells, and one that does not resolve is still a node carrying its reason
        // rather than a silently absent row (§3.1/§3.2 — MW2 R-mw2-11's "broken" state).
        //
        // An alias recorded CellsOnly is NOT rendered here: it exists so that the individual cells
        // listed below can be addressed through it, and drawing it would put the other workspace's
        // whole catalogue back in the tree — the exact thing the per-cell reference exists to avoid.
        foreach (var entry in (cws.ReferencedWorkspaces ?? [])
            .Where(r => !r.CellsOnly)
            .OrderBy(r => r.Alias, StringComparer.OrdinalIgnoreCase))
        {
            root.AddChild(ResolveReferencedWorkspace(entry, workspaceRootDir, carried));
        }

        // Referenced CELLS (from .cws) — one root-level row per cell, alphabetical by cell name.
        // A reference to one cell brings in one cell; its sub-cells come along by reference through
        // its own documents (R-mw2-17) and are not listed separately, exactly as a local cell's are
        // not.
        foreach (var node in (cws.ReferencedCells ?? [])
            .Select(r => ResolveReferencedCell(r, workspaceRootDir))
            .OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase))
        {
            root.AddChild(node);
        }

        // Known Files (from .cws) — alphabetical by ref string.
        //
        // A reference that resolves to something the scan has ALREADY placed in the tree is skipped.
        // A Known File is a bookmark to a file the tree cannot otherwise show; once the file is
        // inside the workspace it is visible where it actually lives, and a second entry under
        // Known Files is a duplicate the user has to learn to ignore. Membership is tested against
        // the tree that was just built, not against the workspace root, because those are not the
        // same question: .DS_Store and *.source live inside the workspace and are deliberately
        // hidden from the ordinary scan, so naming one as a Known File is still the only way to see
        // it (IsHiddenTreeFile's opt-in) and must keep working.
        //
        // The .cws list itself is NOT filtered — only its rendering. The Data Display's data-source
        // library reads KnownFiles rather than the tree (GetKnownTouchstoneFiles /
        // GetKnownLoadpullFiles), so dropping an in-workspace .sNp/.spl from the list would remove
        // an imported measurement from every trace picker.
        if (cws.KnownFiles.Count > 0)
        {
            var alreadyInTree = CollectAbsolutePaths(root);
            var kfGroup = new ProjectTreeNode(NodeKind.KnownFilesGroup, "Known Files", workspaceRootDir, "");
            foreach (string kfRef in cws.KnownFiles.OrderBy(r => r, StringComparer.OrdinalIgnoreCase))
            {
                if (alreadyInTree.Contains(PathKey(ResolveRef(kfRef, workspaceRootDir))))
                    continue;
                kfGroup.AddChild(BuildKnownFileNode(kfRef, workspaceRootDir));
            }
            if (kfGroup.Children.Count > 0)
                root.AddChild(kfGroup);
        }

        // R-L5g-9 (brief-L5-followups-2.md §4): generated cells are NEVER shown in the Project Tree —
        // not even as one collapsed group. This supersedes R-L5-3's original "one synthetic group node"
        // decision, which never fully landed as "infrastructure, not content" anyway: a group node still
        // let a user open/browse individual generated cells as if they were ordinary content. Per §4.2,
        // a generated cell is now a pure, deletable, rebuildable-from-the-layout regeneration cache
        // (GeneratedCellStore.RecordSnapshot / LayoutView.PCellSnapshots) — there is nothing in it for a
        // user to look at that isn't better read from the referencing instance's own Properties Inspector
        // (R-L5f-8/9's PCell parameter list). IsReservedTreeDir above already excludes the folder from
        // the regular per-directory scan; this is simply "and don't add it back in any other form."

        return root;
    }

    /// <summary>True for the reserved generated-cells folder (R-L5-3), excluded from the ordinary
    /// per-directory scan and rendered instead as the <see cref="NodeKind.GeneratedCellsGroup"/>
    /// synthetic group above.</summary>
    private static bool IsReservedTreeDir(string dir)
        => string.Equals(Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            CircuitRF.Ui.Layout.PCells.GeneratedCellStore.ReservedFolderName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Every absolute path the tree already renders, as <see cref="PathKey"/> keys.</summary>
    private static HashSet<string> CollectAbsolutePaths(ProjectTreeNode node)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Walk(node);
        return seen;

        void Walk(ProjectTreeNode n)
        {
            if (!string.IsNullOrEmpty(n.AbsolutePath))
                seen.Add(PathKey(n.AbsolutePath));
            foreach (var child in n.Children) Walk(child);
        }
    }

    /// <summary>
    /// Comparison form of a path: fully qualified, no trailing separator. Compared
    /// case-insensitively by the caller, matching how the .cws list is de-duplicated everywhere
    /// else — a workspace on a case-sensitive filesystem holding two files whose names differ only
    /// in case is not a case worth splitting the comparison over.
    /// </summary>
    private static string PathKey(string path)
    {
        try { path = Path.GetFullPath(path); } catch { /* keep the raw form; it still compares */ }
        return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    // ── Cell ──────────────────────────────────────────────────────────────────

    private static ProjectTreeNode BuildCellNode(
        string cellDir, string workspaceRoot, bool isReferencedCell = false)
    {
        // Read .ccell for IsTestBench (tolerate corrupt file)
        bool isTestBench = false;
        try
        {
            var ccell = CellPersistence.LoadFromFile(Path.Combine(cellDir, CellFolder.CcellFileName));
            isTestBench = ccell.IsTestBench;
        }
        catch { }

        // Resolve primacy for all three view types up front (avoids re-reading .ccell)
        var warnings = new List<string>();
        var resolutions = new Dictionary<ViewType, PrimaryResolution>();
        foreach (ViewType vt in Enum.GetValues<ViewType>())
        {
            var res = CellFolder.ResolvePrimary(cellDir, vt);
            resolutions[vt] = res;
            if (res.State == PrimaryState.MissingNamedPrimary)
                warnings.Add($"Primary {ViewTypeName(vt)} reference broken: {res.MissingName} not found.");
        }

        var cellNode = new ProjectTreeNode(
            NodeKind.Cell,
            name: FolderDisplayName(cellDir),
            absolutePath: cellDir,
            relativePath: Rel(cellDir, workspaceRoot),
            isTestBench: isTestBench,
            warningReason: warnings.Count > 0 ? string.Join(" ", warnings) : null,
            isReferencedCell: isReferencedCell);

        // CellViewFolder children — empty sub-folders produce no node (§3.1)
        foreach (ViewType vt in Enum.GetValues<ViewType>())
        {
            string subDir = CellFolder.SubFolderPath(cellDir, vt);
            if (!Directory.Exists(subDir)) continue;

            string[] files = Directory.GetFiles(subDir, "*" + CellFolder.ViewExtension(vt));
            if (files.Length == 0) continue;

            var res = resolutions[vt];
            string? primaryName = res.State is PrimaryState.SoleFile or PrimaryState.NamedPresent
                ? res.ResolvedName : null;

            var viewFolder = new ProjectTreeNode(
                NodeKind.CellViewFolder,
                name: CellFolder.SubFolderName(vt),
                absolutePath: subDir,
                relativePath: Rel(subDir, workspaceRoot));

            foreach (string f in files.OrderBy(f => Path.GetFileName(f) ?? f, StringComparer.OrdinalIgnoreCase))
            {
                string fn = FileName(f);
                bool isPrimary = primaryName is not null &&
                    string.Equals(fn, primaryName, StringComparison.OrdinalIgnoreCase);
                viewFolder.AddChild(new ProjectTreeNode(NodeKind.ViewFile, fn, f, Rel(f, workspaceRoot), isPrimary: isPrimary));
            }

            cellNode.AddChild(viewFolder);
        }

        return cellNode;
    }

    // ── User folder ───────────────────────────────────────────────────────────

    private static ProjectTreeNode BuildUserFolderNode(string dir, string workspaceRoot)
    {
        var node = new ProjectTreeNode(
            NodeKind.UserFolder,
            name: FolderDisplayName(dir),
            absolutePath: dir,
            relativePath: Rel(dir, workspaceRoot));

        // Files classified by extension
        foreach (string f in Directory.GetFiles(dir).OrderBy(f => Path.GetFileName(f) ?? f, StringComparer.OrdinalIgnoreCase))
        {
            if (IsHiddenTreeFile(f))
                continue;
            node.AddChild(BuildFileNode(f, workspaceRoot));
        }

        // Sub-folders: cell or user folder (recursive)
        foreach (string subDir in SubDirsSorted(dir))
        {
            node.AddChild(File.Exists(Path.Combine(subDir, CellFolder.CcellFileName))
                ? BuildCellNode(subDir, workspaceRoot)
                : BuildUserFolderNode(subDir, workspaceRoot));
        }

        return node;
    }

    private static ProjectTreeNode BuildFileNode(string file, string workspaceRoot)
        => new(ClassifyFile(file), FileName(file), file, Rel(file, workspaceRoot));

    /// <summary>
    /// What a loose file's EXTENSION says it is. The one extension-to-kind table in the app: a Known
    /// File carries no kind of its own (every one of them scans as <see cref="NodeKind.KnownFile"/>,
    /// wherever it lives), so the tree asks this to decide whether a bookmarked path is a circuitRF
    /// document circuitRF can open — and a second copy of the table would be the thing that forgets
    /// a newly-added document type.
    /// </summary>
    /// <returns>
    /// <see cref="NodeKind.OtherFile"/> for any extension with no document type behind it. Purely
    /// lexical — the path is never touched, so a directory named <c>x.clay</c> classifies as a
    /// layout and callers that care check <c>IsDirectory</c> themselves.
    /// </returns>
    public static NodeKind ClassifyFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".csym"   => NodeKind.ViewFile,
            ".csch"   => NodeKind.ViewFile,
            ".clay"   => NodeKind.ViewFile,
            ".cdd"    => NodeKind.DataDisplayFile,
            ".charm"  => NodeKind.HarmonicaFile,
            ".wbond"  => NodeKind.WBondFile,
            ".ccolor" => NodeKind.ColorThemeFile,
            ".ctech"  => NodeKind.TechFile,
            ".cem"    => NodeKind.EmSetupFile,
            _         => NodeKind.OtherFile,
        };

    // ── Library ───────────────────────────────────────────────────────────────

    private static ProjectTreeNode ResolveLibrary(
        string libRef, string workspaceRoot, Dictionary<string, ProjectTreeNode>? carried)
    {
        string resolved = ResolveRef(libRef, workspaceRoot);

        // Accept either a directory path or a .clib file path (use its parent dir)
        string? libDir = null;
        if (Directory.Exists(resolved))
            libDir = resolved;
        else if (string.Equals(Path.GetExtension(resolved), ".clib", StringComparison.OrdinalIgnoreCase)
                 && File.Exists(resolved))
            libDir = Path.GetDirectoryName(resolved);

        if (libDir is null)
        {
            string displayName = FolderDisplayName(resolved);
            if (string.IsNullOrEmpty(displayName)) displayName = libRef;
            return new ProjectTreeNode(
                NodeKind.Library, displayName, resolved, Rel(resolved, workspaceRoot),
                warningReason: UnresolvedReason("Library path unresolved", libRef));
        }

        var libNode = new ProjectTreeNode(
            NodeKind.Library,
            name: FolderDisplayName(libDir),
            absolutePath: libDir,
            relativePath: Rel(libDir, workspaceRoot));

        // Cells at ANY depth (R-sl1-1), by the same rule the workspace's own scan uses: a folder
        // that is not a cell is a folder, and a cell inside it is an ordinary cell node. A library of
        // two hundred parts is organised into folders on the first day, and until R-sl1-1 such a
        // library rendered EMPTY here while every reference into it still resolved — resolution is
        // path arithmetic and never consults the tree, so only the browsing was missing.
        //
        // SL4 R-sl4-10: unless this scan is the on-focus one, in which case the walk is skipped and
        // the last one's children are carried forward (R-sl4-11 — never an empty node).
        if (carried is not null)
            CarryForward(libNode, carried);
        else
            foreach (var child in BuildReferencedChildren(libDir, workspaceRoot))
                libNode.AddChild(child);

        return libNode;
    }

    // ── Referenced workspace (MW2) ────────────────────────────────────────────

    /// <summary>
    /// One referenced workspace as a sub-tree of its cells. The node's own path is the OTHER
    /// workspace's root, so everything downstream — placement, Reveal, the properties header — sees
    /// where the cell really lives rather than a path inside this workspace.
    /// </summary>
    private static ProjectTreeNode ResolveReferencedWorkspace(
        CwsWorkspaceRef entry, string workspaceRoot, Dictionary<string, ProjectTreeNode>? carried)
    {
        string? otherRoot = ExternalCellRef.WorkspaceRootForAlias(workspaceRoot, entry.Alias);

        if (otherRoot is null)
            return new ProjectTreeNode(
                NodeKind.ReferencedWorkspace, entry.Alias,
                ResolveRef(entry.Path, workspaceRoot), "",
                warningReason: UnresolvedReason("Referenced workspace unresolved", entry.Path));

        var node = new ProjectTreeNode(
            NodeKind.ReferencedWorkspace,
            name: entry.Alias,
            absolutePath: otherRoot,
            relativePath: "");

        // Cells at any depth (R-sl1-1), and cells only — the same shape a Library sub-tree has. The
        // other workspace's own libraries, known files and referenced workspaces are ITS business:
        // rendering them here would let a reference reach transitively through a chain nobody chose,
        // and R-mw2-17's "a reference is to one cell, its sub-cells come along by reference" is about
        // a cell's own hierarchy, not about a second workspace's configuration. R-sl1-2 keeps that
        // standing decision verbatim and adds the depth rule to it: recurse through FOLDERS, never
        // through another .cws.
        //
        // SL4 R-sl4-10, exactly as for a library above: the on-focus rescan carries the last walk's
        // children forward instead of re-reading someone else's disk on every alt-tab.
        if (carried is not null)
            CarryForward(node, carried);
        else
            foreach (var child in BuildReferencedChildren(otherRoot, otherRoot))
                node.AddChild(child);

        return node;
    }

    // ── Referenced cell (one cell of another workspace) ───────────────────────

    /// <summary>
    /// One <c>ws://alias/…</c> cell reference as a single root-level row: an ordinary cell node — same
    /// views, same double-click, same placement — flagged <see cref="ProjectTreeNode.IsReferencedCell"/>
    /// so it draws the network-file glyph and rides its own filter toggle.
    ///
    /// <para>An unresolvable reference is a node carrying its reason, never an absent row: the alias
    /// may have been removed, or the other workspace moved away, and both are states the user has to
    /// be able to see in order to repair. The name then falls back to the reference's own last
    /// segment, because there is no folder on disk to take one from.</para>
    /// </summary>
    private static ProjectTreeNode ResolveReferencedCell(string cellRef, string workspaceRoot)
    {
        string? cellDir = ExternalCellRef.ResolveCellDir(cellRef, workspaceRoot);

        if (cellDir is null || !File.Exists(Path.Combine(cellDir, CellFolder.CcellFileName)))
            return new ProjectTreeNode(
                NodeKind.Cell, RefLeaf(cellRef), cellDir ?? cellRef, "",
                warningReason: UnresolvedReason("Referenced cell unresolved", cellRef),
                isReferencedCell: true);

        // Relative to the OTHER workspace's root where there is one, so the tooltip reads as the path
        // the cell actually has over there rather than a ../../ climb out of this workspace.
        string relativeTo = WorkspaceRootFinder.WorkspaceDirOf(cellDir) ?? Path.GetDirectoryName(cellDir)!;
        return BuildCellNode(cellDir, relativeTo, isReferencedCell: true);
    }

    /// <summary>The last path segment of a <c>ws://alias/a/b/Cell</c> reference — what to call a row
    /// whose folder cannot be found.</summary>
    private static string RefLeaf(string cellRef)
    {
        string trimmed = cellRef.TrimEnd('/');
        int slash = trimmed.LastIndexOf('/');
        return slash >= 0 && slash < trimmed.Length - 1 ? trimmed[(slash + 1)..] : trimmed;
    }

    // ── Referenced sub-trees: cells at any depth (SL1) ────────────────────────

    /// <summary>
    /// The children of one referenced root — a library folder or another workspace's root — as
    /// cell nodes and folder nodes, recursively (R-sl1-1).
    ///
    /// <para>Three rules, and they are the whole of it:</para>
    /// <list type="bullet">
    /// <item><b>Cells at any depth.</b> A cell folder becomes an ordinary <see cref="BuildCellNode"/>
    /// wherever it sits, with the same icon, tooltip and double-click behaviour it has anywhere else.
    /// There is no second rule to learn and no depth limit.</item>
    /// <item><b>Folders, never another <c>.cws</c></b> (R-sl1-2). The recursion crosses directories;
    /// it never opens a nested workspace's configuration, so one reference cannot reach transitively
    /// through a chain nobody chose. A folder that is itself a workspace root renders as a folder and
    /// its cells are reached the same way any other folder's are — what stops is the CONFIGURATION,
    /// which is what "the other workspace's libraries are its business" was always about.</item>
    /// <item><b>A folder with no cell anywhere beneath it is not rendered.</b> A referenced sub-tree
    /// carries cells and nothing else — no loose files, exactly as before this change — so an empty
    /// folder node here is a dead end the user opens once and learns to distrust. The workspace's OWN
    /// scan keeps such folders because it renders their files too.</item>
    /// </list>
    /// </summary>
    private static List<ProjectTreeNode> BuildReferencedChildren(string dir, string relativeTo)
    {
        var children = new List<ProjectTreeNode>();
        foreach (string subDir in SubDirsSorted(dir))
        {
            if (File.Exists(Path.Combine(subDir, CellFolder.CcellFileName)))
            {
                children.Add(BuildCellNode(subDir, relativeTo));
                continue;
            }

            var grandChildren = BuildReferencedChildren(subDir, relativeTo);
            if (grandChildren.Count == 0) continue;      // no cell anywhere beneath — not a folder worth showing

            var folder = new ProjectTreeNode(
                NodeKind.UserFolder,
                name: FolderDisplayName(subDir),
                absolutePath: subDir,
                relativePath: Rel(subDir, relativeTo));
            foreach (var gc in grandChildren) folder.AddChild(gc);
            children.Add(folder);
        }
        return children;
    }

    // ── Carrying a referenced sub-tree forward (SL4 R-sl4-10/-11) ─────────────

    /// <summary>
    /// Every <see cref="NodeKind.Library"/> and <see cref="NodeKind.ReferencedWorkspace"/> node in
    /// the tree currently on screen, by absolute path — what an on-focus rescan carries forward
    /// instead of re-walking. Null <paramref name="previous"/> gives an empty index, which is the
    /// "nothing has been read yet" case R-sl4-11 renders honestly.
    /// </summary>
    private static Dictionary<string, ProjectTreeNode> IndexReferencedRoots(ProjectTreeNode? previous)
    {
        var index = new Dictionary<string, ProjectTreeNode>(StringComparer.OrdinalIgnoreCase);
        if (previous is null) return index;

        // A referenced WORKSPACE is a root child of its own; only the Libraries group holds the rest,
        // so this is a two-level walk rather than a full one.
        foreach (var group in previous.Children)
        {
            if (group.Kind == NodeKind.ReferencedWorkspace)
            {
                index[group.AbsolutePath] = group;
                continue;
            }

            if (group.Kind is not NodeKind.LibrariesGroup)
                continue;
            foreach (var node in group.Children)
                index[PathKey(node.AbsolutePath)] = node;
        }
        return index;
    }

    /// <summary>
    /// Gives <paramref name="node"/> the children the last walk found for the same referenced root,
    /// or the single <see cref="NodeKind.NotReadYet"/> placeholder when there was no last walk.
    ///
    /// <para><b>R-sl4-11 is the whole of this method.</b> A referenced sub-tree that has not been
    /// walked must render as ITSELF — an empty library is the exact symptom SL1 exists to remove, and
    /// it must not come back through a caching rule. Carrying the previous contents forward is honest
    /// (it is what was there a moment ago, and re-reading it is one gesture away); a placeholder that
    /// says nothing has been read yet is honest; rendering nothing at all is not.</para>
    ///
    /// <para>The previous walk's node objects are REUSED rather than copied. A
    /// <see cref="ProjectTreeNode"/> is transient and immutable once built — nothing persists an Id,
    /// and the view models are rebuilt from it every time — so sharing a sub-tree between two scan
    /// results costs nothing and keeps the signature stable, which is what stops the tree flashing.
    /// </para>
    /// </summary>
    private static void CarryForward(ProjectTreeNode node, Dictionary<string, ProjectTreeNode> carried)
    {
        if (carried.TryGetValue(PathKey(node.AbsolutePath), out var before) && before.Children.Count > 0)
        {
            foreach (var child in before.Children) node.AddChild(child);
            return;
        }

        node.AddChild(new ProjectTreeNode(
            NodeKind.NotReadYet,
            name: "Not read yet — expand or Refresh to browse",
            absolutePath: node.AbsolutePath,
            relativePath: node.RelativePath));
    }

    // ── Known File ────────────────────────────────────────────────────────────

    private static ProjectTreeNode BuildKnownFileNode(string kfRef, string workspaceRoot)
    {
        string resolved = ResolveRef(kfRef, workspaceRoot);
        string name = FileName(resolved);
        if (string.IsNullOrEmpty(name)) name = kfRef;
        bool isDir  = Directory.Exists(resolved);
        bool exists = isDir || File.Exists(resolved);
        return new ProjectTreeNode(
            NodeKind.KnownFile, name, resolved, Rel(resolved, workspaceRoot),
            warningReason: exists ? null : UnresolvedReason("Known File path not found", kfRef),
            isDirectory: isDir);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // A file the tree hides by default (still shown if the user adds it as a Known File).
    private static bool IsHiddenTreeFile(string path)
    {
        var name = Path.GetFileName(path);
        return string.Equals(name, ".DS_Store", StringComparison.OrdinalIgnoreCase)
            // SL4 R-sl4-1's advisory lock. This list is NOT "dotfiles in general" — it is an explicit
            // set — so a file circuitRF itself drops in a workspace root has to be named here or it
            // renders as a loose file node and travels into an archive. SL2's write probe learned
            // this the same way.
            || string.Equals(name, CircuitRF.Design.Workspace.WorkspaceLock.FileName, StringComparison.OrdinalIgnoreCase)
            // TM1 R-tm1-20 / TM2 — the forwarding record a move leaves at a workspace or library
            // root. Same argument as the lock file above: this predicate is an explicit set, not a
            // dotfile rule, so a file circuitRF itself drops has to be named here or it renders as a
            // loose file node in every tree and travels into every archive.
            || string.Equals(name, CircuitRF.Design.Workspace.MoveRedirects.FileName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetExtension(path), ".source", StringComparison.OrdinalIgnoreCase);
    }

    private static CwsFile TryLoadCws(string workspaceRoot)
    {
        string cwsPath = Path.Combine(workspaceRoot, CwsFileName);
        if (!File.Exists(cwsPath)) return new CwsFile();
        try { return WorkspacePersistence.LoadFromFile(cwsPath); }
        catch { return new CwsFile(); }
    }

    /// <summary>
    /// The sub-directories of <paramref name="dir"/> the tree may show, alphabetical
    /// (OrdinalIgnoreCase). This is the ONE place the reserved generated-cells folder is excluded
    /// (R-sl1-3): every walk — the root loop, <see cref="BuildUserFolderNode"/>'s recursion, and both
    /// referenced-subtree builders — passes through here, so the exclusion cannot be true in three
    /// places and false in the fourth. It used to be applied only in <see cref="Scan"/>'s root loop,
    /// which was latent rather than correct: <c>.generated-cells</c> only ever exists at a workspace
    /// root, and the "has a .ccell" predicate happened to skip it there.
    /// </summary>
    private static IEnumerable<string> SubDirsSorted(string dir)
    {
        if (!Directory.Exists(dir)) return Array.Empty<string>();
        return Directory.GetDirectories(dir)
            .Where(d => !IsReservedTreeDir(d))
            .OrderBy(d => Path.GetFileName(d) ?? d, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A stored ref (a library ref, a Known File) as an absolute path: rooted stays as it is,
    /// relative resolves against the workspace root, and <c>${NAME}</c> is expanded from the
    /// environment first (R-sl1-5/-6).
    ///
    /// <para>An UNSET token returns the stored text unchanged rather than a half-expanded path
    /// (R-sl1-7). Nothing on disk is named <c>${CRF_LIB}/stdlib</c>, so the node builders' existence
    /// checks fail as they should, and <see cref="UnresolvedReason"/> is what turns that into a
    /// sentence naming the variable to set.</para>
    /// </summary>
    private static string ResolveRef(string path, string baseDir)
    {
        if (!PathTokens.TryExpand(path, out string expanded, out _)) return path;
        return Path.IsPathRooted(expanded) ? expanded : Path.GetFullPath(Path.Combine(baseDir, expanded));
    }

    /// <summary>
    /// The <c>WarningReason</c> for a reference that did not resolve. When the cause is an unset
    /// <c>${NAME}</c> the message names the VARIABLE — the user's repair is one environment setting,
    /// and "path unresolved: ${CRF_LIB}/stdlib/.cws" makes them work that out for themselves
    /// (R-sl1-7).
    /// </summary>
    private static string UnresolvedReason(string prefix, string storedRef)
        => PathTokens.UnsetTokenIn(storedRef) is { } token
            ? $"{prefix}: {token} is not set on this machine."
            : $"{prefix}: {storedRef}";

    private static string Rel(string absPath, string workspaceRoot)
    {
        try { return Path.GetRelativePath(workspaceRoot, absPath); }
        catch { return absPath; }
    }

    private static string FolderDisplayName(string path)
    {
        string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.GetFileName(trimmed) ?? trimmed;
    }

    private static string FileName(string path) => Path.GetFileName(path) ?? path;

    private static string ViewTypeName(ViewType vt) => vt switch
    {
        ViewType.Schematic => "schematic",
        ViewType.Symbol    => "symbol",
        ViewType.Layout    => "layout",
        _                  => vt.ToString().ToLowerInvariant(),
    };
}

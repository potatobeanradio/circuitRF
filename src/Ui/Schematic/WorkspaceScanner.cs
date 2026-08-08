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

public static class WorkspaceScanner
{
    private const string CwsFileName = ".cws";

    // ── Public entry point ────────────────────────────────────────────────────

    /// <summary>
    /// Scans <paramref name="workspaceRootDir"/> and returns the root
    /// <see cref="ProjectTreeNode"/> for the workspace.  Idempotent and cheap
    /// enough to call on every focus or Refresh command.
    /// </summary>
    public static ProjectTreeNode Scan(string workspaceRootDir)
    {
        workspaceRootDir = Path.GetFullPath(workspaceRootDir);

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
            if (IsReservedTreeDir(subDir)) continue;
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
                libGroup.AddChild(ResolveLibrary(libRef, workspaceRootDir));
            root.AddChild(libGroup);
        }

        // Known Files (from .cws) — alphabetical by ref string
        if (cws.KnownFiles.Count > 0)
        {
            var kfGroup = new ProjectTreeNode(NodeKind.KnownFilesGroup, "Known Files", workspaceRootDir, "");
            foreach (string kfRef in cws.KnownFiles.OrderBy(r => r, StringComparer.OrdinalIgnoreCase))
                kfGroup.AddChild(BuildKnownFileNode(kfRef, workspaceRootDir));
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

    // ── Cell ──────────────────────────────────────────────────────────────────

    private static ProjectTreeNode BuildCellNode(string cellDir, string workspaceRoot)
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
            warningReason: warnings.Count > 0 ? string.Join(" ", warnings) : null);

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
    {
        string ext = Path.GetExtension(file).ToLowerInvariant();
        NodeKind kind = ext switch
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
        return new ProjectTreeNode(kind, FileName(file), file, Rel(file, workspaceRoot));
    }

    // ── Library ───────────────────────────────────────────────────────────────

    private static ProjectTreeNode ResolveLibrary(string libRef, string workspaceRoot)
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
                warningReason: $"Library path unresolved: {libRef}");
        }

        var libNode = new ProjectTreeNode(
            NodeKind.Library,
            name: FolderDisplayName(libDir),
            absolutePath: libDir,
            relativePath: Rel(libDir, workspaceRoot));

        // Scan cells within the library (same cell logic)
        foreach (string cellDir in SubDirsSorted(libDir)
            .Where(d => File.Exists(Path.Combine(d, CellFolder.CcellFileName))))
        {
            libNode.AddChild(BuildCellNode(cellDir, workspaceRoot));
        }

        return libNode;
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
            warningReason: exists ? null : $"Known File path not found: {kfRef}",
            isDirectory: isDir);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // A file the tree hides by default (still shown if the user adds it as a Known File).
    private static bool IsHiddenTreeFile(string path)
    {
        var name = Path.GetFileName(path);
        return string.Equals(name, ".DS_Store", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetExtension(path), ".source", StringComparison.OrdinalIgnoreCase);
    }

    private static CwsFile TryLoadCws(string workspaceRoot)
    {
        string cwsPath = Path.Combine(workspaceRoot, CwsFileName);
        if (!File.Exists(cwsPath)) return new CwsFile();
        try { return WorkspacePersistence.LoadFromFile(cwsPath); }
        catch { return new CwsFile(); }
    }

    private static IEnumerable<string> SubDirsSorted(string dir)
    {
        if (!Directory.Exists(dir)) return Array.Empty<string>();
        return Directory.GetDirectories(dir).OrderBy(d => Path.GetFileName(d) ?? d, StringComparer.OrdinalIgnoreCase);
    }

    private static string ResolveRef(string path, string baseDir)
        => Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(baseDir, path));

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

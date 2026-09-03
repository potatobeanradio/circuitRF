namespace CircuitRF.Ui.Schematic;

/// <summary>The three states an external cell reference can be in (MW2 R-mw2-11). Each is visually
/// distinct and each explains itself; a user must be able to tell them apart without opening
/// anything, because the repair differs for each.</summary>
public enum ExternalCellState
{
    /// <summary>Not an external reference at all — an ordinary cell in this workspace.</summary>
    NotExternal,

    /// <summary>Resolves, and is fine. Marked anyway (R-mw2-13): a cell in your layout that is not
    /// yours is exactly the thing a reference exists to make visible.</summary>
    Resolved,

    /// <summary>The cell resolves, but a kit part inside it does not, because the workspace that
    /// declares that kit is not open in any window (R-mw2-9). Repaired by opening it.</summary>
    WorkspaceNotOpen,

    /// <summary>The alias does not resolve, the folder is gone, or the cell has kit content and no
    /// workspace of its own to resolve it against (R-mw2-10). Repaired by relocating or copying.</summary>
    Broken,
}

/// <summary>What one external reference resolves to, and what to say about it.</summary>
/// <param name="State">Which of the three states, or <see cref="ExternalCellState.NotExternal"/>.</param>
/// <param name="Alias">The alias the reference carries, when it is external.</param>
/// <param name="WorkspaceRoot">The other workspace's root, when the alias resolves.</param>
/// <param name="CellDir">The resolved cell folder, when it resolves.</param>
/// <param name="Explanation">One sentence for the Properties panel and Messages; null when fine.</param>
/// <param name="Repair">The action offered, in the user's words; null when there is nothing to repair.</param>
public sealed record ExternalCellStatus(
    ExternalCellState State,
    string?           Alias,
    string?           WorkspaceRoot,
    string?           CellDir,
    string?           Explanation,
    string?           Repair)
{
    public static readonly ExternalCellStatus NotExternal =
        new(ExternalCellState.NotExternal, null, null, null, null, null);

    /// <summary>The source workspace's own folder name — what
    /// <c>brief-foreign-documents.md</c> R-fgn-7's <c>Amp — [RfFrontEnd]</c> convention shows.
    /// Falls back to the alias when the workspace itself cannot be reached.</summary>
    public string? SourceName => WorkspaceRoot is { Length: > 0 } root
        ? Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        : Alias;
}

/// <summary>
/// Classifies one external cell reference (MW2 §5). The placeholder for a bad cell already existed —
/// <c>CellSymbolResolution.NotFoundResult</c> draws a pin-less box — and what was missing is that it
/// says nothing about WHY. An external reference has three distinct failure modes with three
/// different repairs, and a user who cannot tell them apart cannot act on any of them.
/// </summary>
public static class ExternalCellStatusResolver
{
    /// <summary>
    /// Classifies <paramref name="cellRef"/> as read from a document in <paramref name="baseDir"/>.
    /// Cheap enough for a Properties panel or a Messages line; NOT for a per-frame render path, which
    /// asks <see cref="ExternalCellRef.IsExternalRef"/> instead and marks on that alone.
    /// </summary>
    public static ExternalCellStatus Classify(string? cellRef, string? baseDir)
    {
        if (!ExternalCellRef.TryParse(cellRef, out string alias, out _))
            return ExternalCellStatus.NotExternal;

        string? otherRoot = ExternalCellRef.ResolveAliasWorkspaceRoot(cellRef, baseDir);
        if (otherRoot is null)
            return new ExternalCellStatus(
                ExternalCellState.Broken, alias, null, null,
                $"This workspace declares no reference named \"{alias}\", or the workspace it named has moved.",
                "Locate the workspace, or copy the cell into this workspace.");

        string? cellDir = ExternalCellRef.ResolveCellDir(cellRef, baseDir);
        if (cellDir is null || !Directory.Exists(cellDir))
            return new ExternalCellStatus(
                ExternalCellState.Broken, alias, otherRoot, cellDir,
                $"\"{alias}\" resolves, but the cell is not in it any more.",
                "Locate the cell, or copy it into this workspace.");

        // R-mw2-9/-10: a kit part inside the referenced cell resolves against the cell's OWN parent
        // workspace (MW1 R-mw1-5), so the reference itself being fine says nothing about them. An
        // unmounted kit is the reported, repairable state and its repair is "open that workspace" —
        // never "mount the kit anyway", which would make "which workspace is this part from" an
        // unanswerable question.
        if (FirstUnresolvedKitRef(cellDir) is { } kitRef)
        {
            string kitName = PdkKitRegistry.TryParse(kitRef, out string k, out _) ? k : kitRef;

            // R-mw2-10: no ancestor .cws at all is a PERMANENT error, not a workspace to open. Unlike
            // a missing technology — where brief-foreign-documents.md R-fgn-4's three routes exist
            // because a .ctech can be CHOSEN — a kit cannot be chosen: its identity is the reference.
            if (WorkspaceRootFinder.WorkspaceDirOf(cellDir) is null)
                return new ExternalCellStatus(
                    ExternalCellState.Broken, alias, otherRoot, cellDir,
                    $"This cell uses the kit \"{kitName}\" but belongs to no workspace, so there is "
                  + "nothing to resolve that kit against.",
                    "Copy the cell into this workspace, or put it inside a workspace that declares the kit.");

            return new ExternalCellStatus(
                ExternalCellState.WorkspaceNotOpen, alias, otherRoot, cellDir,
                $"This cell uses the kit \"{kitName}\", which is declared by \"{FolderLeaf(otherRoot)}\" "
              + "and is not mounted because that workspace is not open.",
                $"Open '{FolderLeaf(otherRoot)}' in a new window.");
        }

        return new ExternalCellStatus(
            ExternalCellState.Resolved, alias, otherRoot, cellDir, null, null);
    }

    /// <summary>
    /// The first <c>pdk://</c> reference in the cell's own primary schematic that does NOT resolve
    /// against that cell's parent workspace, or null when every one of them does (which includes the
    /// overwhelmingly common case of a cell using no kit at all).
    ///
    /// <para>The cell's own schematic is read rather than its whole hierarchy: a sub-cell's kit part
    /// reports itself where the sub-cell is drawn, and walking every level here would turn a
    /// Properties-panel question into a recursive disk scan.</para>
    /// </summary>
    private static string? FirstUnresolvedKitRef(string cellDir)
    {
        string schDir = CellFolder.SubFolderPath(cellDir, ViewType.Schematic);
        if (!Directory.Exists(schDir)) return null;

        foreach (var file in Directory.EnumerateFiles(schDir, "*.csch"))
        {
            SchematicEditModel parsed;
            try { (parsed, _, _) = SchematicPersistence.LoadFromFile(file); }
            catch { continue; }

            foreach (var comp in parsed.Components)
            {
                if (comp.CellRef is not { } r || !PdkKitRegistry.IsKitRef(r)) continue;
                if (CellSymbolResolver.Resolve(r, schDir).State == CellSymbolState.Resolved) continue;
                return r;
            }
        }
        return null;
    }

    private static string FolderLeaf(string dir) =>
        Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
}

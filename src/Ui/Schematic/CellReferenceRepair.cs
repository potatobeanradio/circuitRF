namespace CircuitRF.Ui.Schematic;

/// <summary>How a broken cell reference's target was found again.</summary>
public enum CellRefFoundBy
{
    /// <summary>Nothing was found; the user has to say where the cell is.</summary>
    NotFound,

    /// <summary>The reference resolves as it stands — whatever broke it has already been repaired,
    /// and the document is simply showing a stale render.</summary>
    AlreadyResolves,

    /// <summary>A candidate workspace holds a cell at exactly the path the reference names. This is
    /// the ordinary case after a reference is removed and the workspace is still where it was: the
    /// alias goes back and the document is not touched at all.</summary>
    SamePath,

    /// <summary>Exactly one cell of that NAME was found among the places worth looking. The cell has
    /// moved, so the stored reference has to be rewritten to point at where it is now.</summary>
    UniqueName,
}

/// <param name="FoundBy">How, or that it was not.</param>
/// <param name="CellDir">The cell folder to point at; null when nothing was found.</param>
public readonly record struct CellRefRepairFind(CellRefFoundBy FoundBy, string? CellDir);

/// <summary>
/// Finding a cell whose reference no longer resolves (owner, 2026-09-04: removing a cell reference
/// leaves the instances that placed it reading "Not Found", with no way back short of re-creating the
/// reference by hand and hoping the spelling matches).
///
/// <para><b>Search first, ask second.</b> The overwhelmingly common break is one this can undo without
/// asking anything: the reference was removed, or the other workspace was closed and reopened
/// elsewhere, and the cell is still sitting exactly where the reference says it is. Only when that
/// fails is the user asked to point at the folder — and then their answer is recorded as a reference
/// like any other, so it is repaired once rather than once per instance.</para>
///
/// <para><b>Framework-free on purpose</b>: the search is pure filesystem arithmetic and is tested as
/// such. The dialog, the picker and the <c>.cws</c> write belong to the view-model that calls this.</para>
/// </summary>
public static class CellReferenceRepair
{
    /// <summary>
    /// True for a reference that NAMES A FOLDER — the only kind pointing at a cell folder can repair.
    ///
    /// <para>A kit part, a wBond and a SPICE model card all resolve through machinery of their own and
    /// all read <c>NotFound</c> when their own thing is missing: an unimported kit, a blank
    /// <c>File</c> parameter. Offering to "locate the cell folder" for one of those would ask the user
    /// to answer a question that has no answer — the repair for an unloaded kit is to import the kit.</para>
    /// </summary>
    public static bool IsRepairable(string? cellRef) =>
        !string.IsNullOrWhiteSpace(cellRef)
        && !PdkKitRegistry.IsKitRef(cellRef!)
        && !WBondSymbolProvider.IsWBondRef(cellRef!)
        && SpiceModelSymbolProvider.Parse(cellRef!) is null;

    /// <summary>
    /// Looks for the cell <paramref name="cellRef"/> names, without asking the user anything.
    /// </summary>
    /// <param name="baseDir">The referencing document's own folder — what the reference is relative to.</param>
    /// <param name="candidateRoots">
    /// Workspace roots worth looking in, MOST LIKELY FIRST — the caller's own workspace, the other
    /// windows' workspaces, the recent list. Order decides ties, so it is the caller's to state:
    /// a workspace the user has open right now is a better guess than one they opened last month.
    /// </param>
    public static CellRefRepairFind Find(
        string cellRef, string? baseDir, IReadOnlyList<string> candidateRoots)
    {
        if (!IsRepairable(cellRef)) return new(CellRefFoundBy.NotFound, null);

        // 0. It may simply resolve — the reference was put back and nothing here is broken any more.
        if (ExternalCellRef.ResolveCellDir(cellRef, baseDir) is { } asIs && IsCellFolder(asIs))
            return new(CellRefFoundBy.AlreadyResolves, asIs);

        string relPath = RelativePartOf(cellRef);
        string leaf    = LeafOf(relPath);
        if (leaf.Length == 0) return new(CellRefFoundBy.NotFound, null);

        var roots = Distinct(candidateRoots);

        // 1. The same path in one of the candidates. Cheap, unambiguous, and the answer whenever the
        //    workspace is still where it was — which is what makes "remove the reference, put it
        //    back" a repair that asks the user nothing.
        foreach (string root in roots)
        {
            string? candidate = SafeCombine(root, relPath);
            if (candidate is not null && IsCellFolder(candidate))
                return new(CellRefFoundBy.SamePath, Path.GetFullPath(candidate));
        }

        // 2. One cell of that name, anywhere in the candidates. AMBIGUITY IS NOT RESOLVED HERE: two
        //    cells named Amp in two projects is exactly the case where guessing wrong is worse than
        //    asking, so more than one match falls through to the picker.
        string? onlyMatch = null;
        foreach (string root in roots)
        {
            foreach (string hit in FindCellsNamed(root, leaf))
            {
                if (onlyMatch is not null && !PathEquals(onlyMatch, hit))
                    return new(CellRefFoundBy.NotFound, null);
                onlyMatch ??= hit;
            }
        }

        return onlyMatch is null
            ? new(CellRefFoundBy.NotFound, null)
            : new(CellRefFoundBy.UniqueName, onlyMatch);
    }

    /// <summary>The part of a reference below the workspace it belongs to: a <c>ws://</c> reference's
    /// remainder, or a relative path with its <c>../</c> climb removed (what is left is the path
    /// INSIDE whatever workspace the cell lives in, which is what a candidate root is combined with).</summary>
    internal static string RelativePartOf(string cellRef)
    {
        if (ExternalCellRef.TryParse(cellRef, out _, out string rel)) return Normalize(rel);

        string path = Normalize(cellRef);
        while (path.StartsWith("../", StringComparison.Ordinal)) path = path[3..];
        return path.TrimStart('/');
    }

    private static string LeafOf(string relPath)
    {
        string trimmed = relPath.TrimEnd('/');
        int slash = trimmed.LastIndexOf('/');
        return slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
    }

    private static string Normalize(string p) => p.Replace('\\', '/').Trim();

    private static string? SafeCombine(string root, string relPath)
    {
        try { return Path.GetFullPath(Path.Combine(root, relPath.Replace('/', Path.DirectorySeparatorChar))); }
        catch { return null; }
    }

    /// <summary>Every cell folder called <paramref name="name"/> under <paramref name="root"/>, at any
    /// depth — the same "cells at any depth" shape the tree walks (R-sl1-1). A workspace's own
    /// generated-cell cache is skipped: it holds machine-named regeneration artifacts, never a cell a
    /// reference was written against.</summary>
    private static IEnumerable<string> FindCellsNamed(string root, string name)
    {
        if (!Directory.Exists(root)) yield break;

        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            string dir = stack.Pop();
            string[] subDirs;
            try { subDirs = Directory.GetDirectories(dir); }
            catch { continue; }

            foreach (string sub in subDirs)
            {
                string leaf = Path.GetFileName(sub);
                if (leaf.StartsWith('.')) continue;
                if (string.Equals(leaf, Layout.PCells.GeneratedCellStore.ReservedFolderName,
                                  StringComparison.OrdinalIgnoreCase)) continue;

                if (IsCellFolder(sub))
                {
                    // A cell folder's own sub-folders are its views, never more cells.
                    if (string.Equals(leaf, name, StringComparison.OrdinalIgnoreCase))
                        yield return Path.GetFullPath(sub);
                    continue;
                }
                stack.Push(sub);
            }
        }
    }

    private static bool IsCellFolder(string dir)
    {
        try { return File.Exists(Path.Combine(dir, CellFolder.CcellFileName)); }
        catch { return false; }
    }

    private static bool PathEquals(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
                      Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
                      StringComparison.OrdinalIgnoreCase);

    private static List<string> Distinct(IReadOnlyList<string> roots)
    {
        var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(roots.Count);
        foreach (string r in roots)
        {
            if (string.IsNullOrWhiteSpace(r)) continue;
            string full;
            try { full = Path.GetFullPath(r).TrimEnd(Path.DirectorySeparatorChar); }
            catch { continue; }
            if (seen.Add(full)) result.Add(full);
        }
        return result;
    }
}

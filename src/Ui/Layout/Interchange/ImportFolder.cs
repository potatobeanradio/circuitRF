// Where an import's cells land. Framework-free (no Avalonia) so it is headlessly testable, the same
// reason every other decision in this folder lives outside the code-behind that calls it.

namespace CircuitRF.Ui.Layout.Interchange;

/// <summary>
/// The one rule for grouping an import's generated cells under a folder named after the file it came
/// from (owner, 2026-08-25: a board file "can generate a lot of cells, which makes it difficult for
/// the user to see their other cells").
///
/// <para><b>This needed no new capability anywhere else.</b> <c>WorkspaceScanner</c> already recurses
/// into a user folder, <c>InstanceCellChoices.Collect</c> already finds cells inside one and names
/// them by relative path, and a <c>CellRef</c> is a relative path that resolves from any depth — and
/// <c>PcbImport.Import</c> already took the parent directory as a parameter. What was missing was only
/// the decision of WHICH directory to hand it.</para>
///
/// <para>Naming follows the same sanitize-then-suffix algorithm the imported CELLS themselves go
/// through (<see cref="DxfNaming.NameCellsForImport"/>): a file name is not a safe path component in
/// general, and importing the same board twice must not silently merge two boards' cells into one
/// folder — the second becomes <c>board_2</c>, the third <c>board_3</c>.</para>
/// </summary>
public static class ImportFolder
{
    /// <summary>
    /// The folder name to use under <paramref name="parentDir"/> for an import of
    /// <paramref name="desiredName"/> — sanitized, and suffixed until it names nothing that already
    /// exists there. Pure: creates nothing, so a test can assert the name without a filesystem
    /// round trip and the caller decides when (and whether) to commit.
    /// </summary>
    public static string UniqueName(string parentDir, string desiredName)
    {
        // Sanitize through the same predicate the imported cells use — one rule, one place.
        string sanitized = DxfNaming.NameCellsForImport([desiredName])[desiredName];

        string candidate = sanitized;
        int suffixNum = 2;
        while (Exists(parentDir, candidate))
        {
            candidate = $"{sanitized}_{suffixNum}";
            suffixNum++;
        }
        return candidate;
    }

    /// <summary>Creates (and returns the absolute path of) the folder <see cref="UniqueName"/> picks.</summary>
    public static string Create(string parentDir, string desiredName)
    {
        string dir = Path.Combine(parentDir, UniqueName(parentDir, desiredName));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Removes a folder created by <see cref="Create"/> when the import that was going to
    /// fill it did not — a cancelled import must leave nothing behind. Never removes a folder that
    /// has anything in it, so this can be called unconditionally on the failure path.</summary>
    public static void RemoveIfEmpty(string dir)
    {
        try
        {
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
        }
        catch
        {
            // Best-effort cleanup: an empty folder left behind is not worth failing an import over,
            // and the import's own message already said what happened.
        }
    }

    // A name is taken when EITHER a file or a directory already carries it — the source file itself
    // sitting in the workspace root is exactly that case, since the folder is named after it.
    private static bool Exists(string parentDir, string name)
    {
        string p = Path.Combine(parentDir, name);
        return Directory.Exists(p) || File.Exists(p);
    }
}

namespace CircuitRF.Design.Cells;

/// <summary>
/// Crash-safe text file write: serialize to a sibling temp file, then atomically rename over the
/// target. A crash mid-write leaves the previous file intact. Temp lives in the SAME directory as
/// the target so the rename stays on one volume (atomic) and lands in the right place.
/// </summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string contents)
    {
        var tmp = path + ".tmp";
        try
        {
            File.WriteAllText(tmp, contents);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // Don't leave an orphaned temp file behind if the write or rename failed (e.g. a
            // read-only / unwritable target directory). Best-effort cleanup, then rethrow so the
            // caller can surface the original error to the user.
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* ignore cleanup failure */ }
            throw;
        }
    }

    /// <summary>
    /// <see cref="WriteAllText"/> for a file changed on the way through another operation (a rename repointing the
    /// documents that reference a cell). A rename needs permission on the folder and not on the file, so on macOS and
    /// Linux it would replace a read-only file that an in-place write refuses.
    /// </summary>
    public static void RewriteText(string path, string contents)
    {
        if (File.Exists(path) && new FileInfo(path).IsReadOnly)
            throw new UnauthorizedAccessException($"'{Path.GetFileName(path)}' is read-only.");
        WriteAllText(path, contents);
    }
}

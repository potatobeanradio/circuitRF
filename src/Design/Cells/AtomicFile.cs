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
}

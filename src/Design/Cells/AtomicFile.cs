using CircuitRF.Diagnostics;

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
            MoveOver(tmp, path);
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

    /// <summary>How many times a refused rename over an existing file is tried on Windows, and the first wait between tries
    /// (doubled each time: 50 + 100 + 200 + 400 ms, under a second in all).</summary>
    /// <summary>The same write for bytes that must land exactly as given — a file written back after a
    /// partial edit, where a text round trip could re-encode what was not changed (HZN-1).</summary>
    public static void WriteAllBytes(string path, byte[] contents)
    {
        var tmp = path + ".tmp";
        try
        {
            File.WriteAllBytes(tmp, contents);
            MoveOver(tmp, path);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* ignore cleanup failure */ }
            throw;
        }
    }

    internal const int MoveAttempts = 5;
    private const int FirstRetryMs = 50;

    /// <summary>
    /// The rename that makes the write visible. On Windows, replacing a file that another process holds open without
    /// delete sharing is refused with "Access to the path is denied". An antivirus scanner, the search indexer or a network
    /// share's client often holds a file for a moment after it is written, so the rename is tried again a few times before
    /// the error is reported. That error comes from File.Move with no path in it, so the one rethrown names the file.
    /// Elsewhere a rename needs permission on the folder only, and a refusal is not transient, so it is not retried.
    /// Also the rename a run uses to move a file it made into a workspace folder (the Palace mesh, a result's publish).
    /// </summary>
    internal static void MoveOver(string tmp, string path, bool overwrite = true)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(tmp, path, overwrite);
                return;
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException
                                      && e is not (FileNotFoundException or DirectoryNotFoundException or PathTooLongException))
            {
                if (OperatingSystem.IsWindows() && attempt < MoveAttempts)
                {
                    Thread.Sleep(FirstRetryMs << (attempt - 1));
                    continue;
                }
                if (e is UnauthorizedAccessException && !e.Message.Contains(path, StringComparison.OrdinalIgnoreCase))
                    throw new UnauthorizedAccessException(Diagnostic.Create("file.access.denied", DiagnosticSeverity.Error,
                        "Access to the path '{path}' is denied.", ("path", path)).Render(), e);
                throw;
            }
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

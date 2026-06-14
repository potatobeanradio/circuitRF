namespace CircuitRF.Ui.Schematic;

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
        File.WriteAllText(tmp, contents);
        File.Move(tmp, path, overwrite: true);
    }
}

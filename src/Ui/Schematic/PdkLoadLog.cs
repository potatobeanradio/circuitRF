namespace CircuitRF.Ui.Schematic;

/// <summary>
/// Where the detail of a failed PDK load goes.
///
/// <para><b>Why a file and not more messages.</b> A kit with forty parts can fail forty ways, and
/// forty warnings is not a report — it is a wall the one line that matters is lost in. Messages
/// carries a single summary per kit plus a clickable path to here, which is the existing
/// file-path-link mechanism rather than a second reporting channel.</para>
///
/// <para><b>It is a diagnostic artifact, never project state.</b> Overwritten per load, never read
/// back, and its absence or failure changes nothing: a workspace opens the same either way. Writing
/// it must not be able to stop an open, which is why every failure here is swallowed.</para>
/// </summary>
public static class PdkLoadLog
{
    public const string FileName = "pdk-load.log";

    /// <summary>Where the log for this workspace lives.</summary>
    public static string PathFor(string workspaceRootDir) => Path.Combine(workspaceRootDir, FileName);

    /// <summary>True once this session has begun a fresh log, so a load appends rather than truncates.</summary>
    private static readonly HashSet<string> _started = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock _gate = new();

    /// <summary>
    /// Starts a fresh log for a workspace about to load its kits. Called once per open, so the file
    /// describes THIS load and not an accumulation of every one before it.
    /// </summary>
    public static void Begin(string workspaceRootDir)
    {
        lock (_gate) _started.Remove(workspaceRootDir);
    }

    /// <summary>
    /// Records one kit's failure. Never throws — a log that cannot be written must not become the
    /// reason a workspace does not open.
    /// </summary>
    public static void Record(string workspaceRootDir, string kitName, string detail)
    {
        if (string.IsNullOrWhiteSpace(workspaceRootDir)) return;

        try
        {
            string path = PathFor(workspaceRootDir);
            bool fresh;
            lock (_gate) fresh = _started.Add(workspaceRootDir);

            string entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {kitName}: {detail}{Environment.NewLine}";

            if (fresh)
                File.WriteAllText(path,
                    $"circuitRF — PDK load report{Environment.NewLine}" +
                    $"Kits referenced by this workspace that could not be loaded.{Environment.NewLine}" +
                    $"Repair a reference in File > Manage PDKs.{Environment.NewLine}{Environment.NewLine}" +
                    entry);
            else
                File.AppendAllText(path, entry);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing to do and nothing worth saying: the summary in Messages already named the kit.
        }
    }
}

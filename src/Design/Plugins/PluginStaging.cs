using CircuitRF.Design.Layout.PCells.Wire;

namespace CircuitRF.Design.Plugins;

/// <summary>
/// The one folder a plugin session may write into, and the check that circuitRF never USES a file from anywhere
/// else (brief-plg-1 §4, <c>plugins.md</c> P-5). circuitRF cannot stop a separate process writing elsewhere —
/// §9.4 says so plainly — but every path a plugin names is resolved here, links included, before anything reads it.
/// </summary>
public static class PluginStaging
{
    /// <summary>Creates <c>&lt;scratch&gt;/plugins/&lt;id&gt;/&lt;session-guid&gt;/</c> and returns it, links along it
    /// resolved so later comparisons see one spelling.</summary>
    public static string Create(string scratchRoot, string pluginId)
    {
        string dir = Path.Combine(scratchRoot, "plugins", pluginId, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return PCellTrustStore.ResolvedDirectory(dir);
    }

    /// <summary>
    /// <paramref name="named"/> — relative to the staging folder or absolute — as a full path under
    /// <paramref name="stagingDir"/>, or the sentence refusing it. Every directory along it and the file itself
    /// are followed through links, so a link inside the folder that points out of it is refused like the path
    /// it points at.
    /// </summary>
    public static (string? Path, string? Refusal) Resolve(string stagingDir, string named)
    {
        if (string.IsNullOrWhiteSpace(named)) return (null, "the plugin named an empty path.");
        string root = PCellTrustStore.ResolvedDirectory(stagingDir);
        string full;
        try { full = System.IO.Path.GetFullPath(named, root); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return (null, $"the plugin named '{Quote(named)}', which is not a path.");
        }

        string? parent = System.IO.Path.GetDirectoryName(full);
        string real = parent is null ? full : System.IO.Path.Combine(PCellTrustStore.ResolvedDirectory(parent), System.IO.Path.GetFileName(full));
        try
        {
            if (File.Exists(real) && new FileInfo(real).ResolveLinkTarget(returnFinalTarget: true) is { } target)
                real = target.FullName;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

        return IsUnder(real, root)
            ? (real, null)
            : (null, $"the plugin named {Quote(named)}, which is outside its staging folder {root}; circuitRF uses only files in that folder.");
    }

    /// <summary>Deletes a session's folder, best effort — a file a plugin still holds open stays for the OS to clear.</summary>
    public static void Delete(string stagingDir)
    {
        try { if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static bool IsUnder(string path, string root)
    {
        var cmp = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        string withSep = root.EndsWith(System.IO.Path.DirectorySeparatorChar) ? root : root + System.IO.Path.DirectorySeparatorChar;
        return path.StartsWith(withSep, cmp);
    }

    private static string Quote(string s) => s.Length <= 200 ? s : s[..200] + "…";
}

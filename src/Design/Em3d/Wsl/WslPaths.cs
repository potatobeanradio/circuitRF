namespace CircuitRF.Design.Em3d.Wsl;

/// <summary>
/// brief-em3d-26 R-em3d26-4 — every path that crosses the boundary goes through ONE function each way:
/// Linux to Windows here, through the <c>\\wsl.localhost\</c> share; Windows to Linux through
/// <c>wslpath -u</c> inside the distribution (<see cref="WslSession.ToLinux"/>), which knows the
/// distribution's own mount table where a <c>/mnt/&lt;drive&gt;</c> string built here would only guess at it.
///
/// <para>Linux paths are composed with <see cref="Combine"/>, never <see cref="Path.Combine(string, string)"/>:
/// on Windows that joins with a backslash, and <c>/home/u\.circuitrf</c> is a file name, not a path.</para>
/// </summary>
public static class WslPaths
{
    /// <summary>
    /// The Windows path of <paramref name="linuxPath"/> (absolute) inside <paramref name="distribution"/>.
    /// Only the separators change; a space or a non-ASCII character is carried as it is, because the
    /// share passes names through unaltered.
    /// </summary>
    public static string ToWindows(string distribution, string linuxPath)
    {
        if (!linuxPath.StartsWith('/')) throw new ArgumentException($"'{linuxPath}' is not an absolute Linux path.", nameof(linuxPath));
        return WslExe.SharePrefix + distribution + linuxPath.Replace('/', '\\');
    }

    /// <summary>Joins Linux path segments with <c>/</c>.</summary>
    public static string Combine(string first, params string[] rest)
    {
        string path = first;
        foreach (string part in rest)
        {
            if (part.Length == 0) continue;
            path = part.StartsWith('/') ? part : path.TrimEnd('/') + "/" + part;
        }
        return path;
    }

    /// <summary>The Linux parent directory, or <c>/</c>.</summary>
    public static string Parent(string linuxPath)
    {
        string trimmed = linuxPath.TrimEnd('/');
        int slash = trimmed.LastIndexOf('/');
        return slash <= 0 ? "/" : trimmed[..slash];
    }

    /// <summary>The last segment of a Linux path.</summary>
    public static string Name(string linuxPath)
    {
        string trimmed = linuxPath.TrimEnd('/');
        return trimmed[(trimmed.LastIndexOf('/') + 1)..];
    }

    /// <summary>Whether <paramref name="path"/> lies under the Windows drives a distribution mounts. The
    /// gate's tell for a run staged where §7.4 says never to stage.</summary>
    public static bool IsMountedWindowsDrive(string path) => path.Contains("/mnt/", StringComparison.Ordinal);
}

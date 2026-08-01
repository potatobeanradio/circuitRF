namespace CircuitRF.Core.Netlist;

/// <summary>
/// Anchors a data file a kit's netlist names by a RELATIVE path to where that file actually is.
///
/// <para><b>Why a search rather than one anchor.</b> A kit writes <c>File=strcat(DataPath,
/// "X.s15p")</c> with <c>DataPath="SomeKit_Data\"</c> — a path relative to the simulator's own data
/// search path, which the kit's installation puts there. circuitRF has no such search path, and the
/// directory the netlist sits in is NOT the answer: a kit keeps its netlists in one folder and
/// its data in a sibling. So the file is looked for around the netlist rather than resolved against
/// a single fixed root.</para>
///
/// <para><b>Nothing is rewritten unless a real file is found</b>, which is what makes this safe to
/// try on every value rather than on a list of parameter names. A kit names its files with whatever
/// keyword it likes, so a name list would be a guess that silently covers some and not others; a
/// value that is not a path simply does not match anything and is handed back untouched.</para>
///
/// <para>Without this, the relative path survives into the generated <c>.cnl</c> and is finally
/// resolved against THAT file's folder — the workspace — so the run fails naming a file in a
/// directory the kit has nothing to do with.</para>
/// </summary>
public sealed class KitDataFileResolver(string? netlistDirectory)
{
    /// <summary>
    /// How far above the netlist to look. Two levels plus one level of children reaches a data
    /// folder that is a sibling of the netlist folder OR a sibling of the folder holding it, which
    /// covers the layouts kits use.
    ///
    /// <para><b>The bound is the point, not a limit to relax when something is not found.</b> Each
    /// ancestor's children are listed, so climbing one level too far starts listing a directory that
    /// has nothing to do with the kit — a home or temp directory — and a value that happens to match
    /// a file in there resolves to something the kit never named. Wrong, and expensive.</para>
    /// </summary>
    private const int AncestorLevels = 2;

    private readonly Dictionary<string, string?> _memo = new(StringComparer.Ordinal);

    /// <summary>
    /// The outermost directory a search from <paramref name="netlistDirectory"/> can reach — so a
    /// caller can offer exactly that tree to somewhere the files must also be readable from, and
    /// nothing wider.
    ///
    /// <para><b>Why this belongs here rather than being restated by the caller.</b> A file resolved
    /// by this class and a file reachable by whoever must open it have to be the same set. Two
    /// independent notions of "near the kit" would drift, and the failure when they do is a path
    /// that resolves perfectly at import and cannot be opened at run time — which is exactly the bug
    /// this was written for, one level further along.</para>
    ///
    /// <para>Null when there is no directory to search.</para>
    /// </summary>
    public static string? OutermostSearchRoot(string? netlistDirectory)
    {
        if (string.IsNullOrEmpty(netlistDirectory)) return null;

        string? directory;
        try { directory = Path.GetFullPath(netlistDirectory); }
        catch (Exception ex) when (ex is ArgumentException or IOException) { return null; }

        for (int level = 0; level < AncestorLevels; level++)
        {
            string? parent;
            try { parent = Path.GetDirectoryName(directory); }
            catch (ArgumentException) { break; }

            if (string.IsNullOrEmpty(parent)) break;   // the filesystem root — go no further
            directory = parent;
        }

        return directory;
    }

    /// <summary>
    /// The absolute path this value names, or null when it is not a relative path to a file that
    /// exists near the netlist — in which case the caller keeps what the kit wrote.
    /// </summary>
    public string? Resolve(string value)
    {
        if (netlistDirectory is null or "" || !LooksLikeRelativePath(value)) return null;

        if (_memo.TryGetValue(value, out string? cached)) return cached;

        string? found = Search(value.Replace('\\', '/'));
        _memo[value] = found;
        return found;
    }

    /// <summary>
    /// A value worth looking for: relative, and shaped like a file rather than a number, a keyword
    /// or a model name. The existence check is the real gate — this only keeps the search off the
    /// overwhelming majority of values that could not possibly be one.
    /// </summary>
    private static bool LooksLikeRelativePath(string value)
    {
        if (value.Length == 0 || value.Length > 512) return false;
        if (value.Contains('"')) return false;

        try { if (Path.IsPathRooted(value)) return false; }
        catch (ArgumentException) { return false; }

        if (value.Contains('/') || value.Contains('\\')) return true;

        // A bare filename still counts, but only with an extension — otherwise every unquoted model
        // name in the file would be probed against the filesystem.
        string extension = Path.GetExtension(value);
        return extension.Length is >= 2 and <= 6;
    }

    private string? Search(string relative)
    {
        string? directory;
        try { directory = Path.GetFullPath(netlistDirectory!); }
        catch (Exception ex) when (ex is ArgumentException or IOException) { return null; }

        for (int level = 0; level <= AncestorLevels && !string.IsNullOrEmpty(directory); level++)
        {
            if (Combined(directory, relative) is { } here) return here;

            // One level of children per ancestor, which is what reaches a sibling data folder. Going
            // deeper would be a search of the kit rather than a look around it.
            foreach (string child in Children(directory))
                if (Combined(child, relative) is { } there) return there;

            try { directory = Path.GetDirectoryName(directory); }
            catch (ArgumentException) { return null; }
        }

        return null;
    }

    private static string? Combined(string directory, string relative)
    {
        string candidate;
        try { candidate = Path.GetFullPath(Path.Combine(directory, relative)); }
        catch (Exception ex) when (ex is ArgumentException or IOException) { return null; }

        try { return File.Exists(candidate) ? candidate : null; }
        catch (IOException) { return null; }
    }

    private static IEnumerable<string> Children(string directory)
    {
        try { return Directory.EnumerateDirectories(directory); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }
}

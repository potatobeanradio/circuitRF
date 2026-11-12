// HZN-3 R-hzn3-1 — the designs a `.hzn` holds, in file order, for the Project Tree.
//
// A tree row only needs the project's name and each design's name and kind, so this reads the file once, keeps
// those, and answers again from memory until the file's modification time or length changes. The tree asks on a
// node's first expansion and on every rescan that finds the node already open; the cache keeps a rescan from
// reading every open project again.

using CircuitRF.Diagnostics;

namespace CircuitRF.Design.Interchange.Hzn;

/// <summary>One design of a project, as the tree lists it.</summary>
public sealed record HznDesignEntry(string Name, HznDesignKind Kind);

/// <summary>A project's designs, or the sentence saying why they cannot be listed.</summary>
/// <param name="ProjectName">The project's <c>name</c>, or null when it states none.</param>
/// <param name="Designs">In file order.</param>
/// <param name="Refusal">Set when the file could not be read or fails the version gate; <see cref="Designs"/> is then empty.</param>
public sealed record HznDesignListing(string? ProjectName, IReadOnlyList<HznDesignEntry> Designs, Diagnostic? Refusal)
{
    public bool Ok => Refusal is null;
}

public static class HznDesignList
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, (DateTime ModifiedUtc, long Length, HznDesignListing Listing)> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The designs of the project at <paramref name="path"/>. Read lazily and cached against the file's modification time
    /// and length, so asking again for an unchanged file touches no content.
    /// </summary>
    public static HznDesignListing Of(string path)
    {
        string full = Path.GetFullPath(path);
        DateTime modified;
        long length;
        try
        {
            var info = new FileInfo(full);
            if (!info.Exists) return Unreadable(full, "the file is not there");
            (modified, length) = (info.LastWriteTimeUtc, info.Length);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return Unreadable(full, e.Message);
        }

        lock (Gate)
            if (Cache.TryGetValue(full, out var hit) && hit.ModifiedUtc == modified && hit.Length == length)
                return hit.Listing;

        var listing = Read(full);
        lock (Gate) Cache[full] = (modified, length, listing);
        return listing;
    }

    /// <summary>The project's name without keeping the designs, for a tooltip; null when the file cannot be read.</summary>
    public static string? ProjectNameOf(string path) => Of(path).ProjectName;

    /// <summary>Forgets what was read for <paramref name="path"/> (or everything), so the next ask reads the file.</summary>
    public static void Forget(string? path = null)
    {
        lock (Gate)
        {
            if (path is null) Cache.Clear();
            else Cache.Remove(Path.GetFullPath(path));
        }
    }

    private static HznDesignListing Read(string full)
    {
        var read = HznFile.Read(full);
        if (!read.Ok) return new(null, [], read.Refusal);
        var project = read.File!.Project;
        // A design with no name cannot be addressed; it is listed by the position the file gives it so it is not lost.
        var designs = project.Designs
            .Select((d, i) => new HznDesignEntry(d.Name is { Length: > 0 } n ? n : $"Design {i + 1}", d.Kind))
            .ToList();
        return new(project.Name, designs, null);
    }

    private static HznDesignListing Unreadable(string full, string reason) => new(null, [], HznDiagnostics.Unreadable(full, reason));
}

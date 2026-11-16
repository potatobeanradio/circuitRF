using SharpCompress.Archives.SevenZip;

namespace CircuitRF.Design.Em3d.Install;

/// <summary>
/// brief TRN-7 R-trn7-2 — unpacks a 7z archive: upstream publishes its Windows ngspice build as <c>.7z</c> only, and the
/// install assistant's <c>extract</c> step reads a recipe's archive by its extension. Every entry must land under
/// <paramref name="into"/>: an entry whose path climbs out of it is refused, as <see cref="System.IO.Compression.ZipFile"/>
/// refuses one.
/// </summary>
internal static class SevenZip
{
    public static void Extract(string archive, string into, Action? checkCancelled = null)
    {
        string root = Path.GetFullPath(into);
        string rootWithSep = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        using var z = SevenZipArchive.OpenArchive(archive);
        // A 7z archive is usually SOLID: one compressed stream for every file. Reading it entry by entry in order is one pass;
        // opening each entry on its own would decompress from the start each time.
        using var reader = z.ExtractAllEntries();
        while (reader.MoveToNextEntry())
        {
            checkCancelled?.Invoke();
            var entry = reader.Entry;
            if (entry.Key is not { Length: > 0 } key) continue;
            string target = Path.GetFullPath(Path.Combine(root, key.Replace('\\', '/')));
            if (!target.StartsWith(rootWithSep, StringComparison.Ordinal) && target != root)
                throw new IOException($"the archive entry '{key}' would be written outside {root}");
            if (entry.IsDirectory) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
            reader.WriteEntryTo(output);
        }
    }
}

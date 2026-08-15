using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace CircuitRF.Ui.Archive;

/// <summary>The result of unpacking an archive: where it went, and what to open.</summary>
public sealed class ArchiveExtractResult
{
    /// <summary>Absolute path of the extracted workspace's <c>.cws</c>, or null when the zip held none.</summary>
    public string? CwsPath { get; init; }

    /// <summary>The folder the workspace was extracted into.</summary>
    public string WorkspaceDir { get; init; } = "";

    public int FileCount { get; init; }

    /// <summary>Entries refused for pointing outside the destination (a "zip slip").</summary>
    public List<string> Rejected { get; init; } = [];
}

/// <summary>
/// Unpacks a <c>.zip</c> written by <see cref="WorkspaceArchiveWriter"/> — and, as far as it can, any
/// other zip holding a workspace.
///
/// <para><b>Every entry path is verified to land inside the destination.</b> A zip is an untrusted
/// file that arrives from someone else, and an entry named <c>../../.ssh/authorized_keys</c> is a
/// well-known way to write outside the folder the user chose. Rejected entries are reported, never
/// silently dropped.</para>
/// </summary>
public static class WorkspaceArchiveExtractor
{
    /// <summary>
    /// Extracts <paramref name="zipPath"/> under <paramref name="destinationParent"/> and returns
    /// where the workspace landed.
    ///
    /// <para>The archive's own single root folder is preserved, so extracting into a folder that
    /// already holds other work adds one folder rather than merging into it. An archive that has no
    /// single root (one made by other means) is given one, named after the zip.</para>
    /// </summary>
    public static ArchiveExtractResult Extract(string zipPath, string destinationParent, bool overwrite = false)
    {
        destinationParent = Path.GetFullPath(destinationParent);
        Directory.CreateDirectory(destinationParent);

        using var zip = ZipFile.OpenRead(zipPath);

        var entries = zip.Entries.Where(e => e.FullName.Length > 0 && !e.FullName.EndsWith('/')).ToList();
        var root    = CommonRootFolder(entries.Select(e => e.FullName));

        // No shared root folder — synthesise one so the extract cannot scatter files.
        var synthetic = root is null ? Path.GetFileNameWithoutExtension(zipPath) : null;
        var targetDir = Path.GetFullPath(Path.Combine(destinationParent, root ?? synthetic ?? "workspace"));

        if (!overwrite && Directory.Exists(targetDir) && Directory.EnumerateFileSystemEntries(targetDir).Any())
            throw new IOException($"'{Path.GetFileName(targetDir)}' already exists here and is not empty.");

        var rejected = new List<string>();
        int count = 0;

        foreach (var entry in entries)
        {
            // Where this entry lands: below the destination parent when the zip carried its own root,
            // below the synthesised folder otherwise.
            var relative = synthetic is null ? entry.FullName : $"{synthetic}/{entry.FullName}";
            var full     = Path.GetFullPath(Path.Combine(destinationParent, relative.Replace('/', Path.DirectorySeparatorChar)));

            if (!WorkspaceArchiveScanner.IsInside(full, destinationParent))
            {
                rejected.Add(entry.FullName);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            entry.ExtractToFile(full, overwrite: true);
            count++;
        }

        var cws = Path.Combine(targetDir, ".cws");

        return new ArchiveExtractResult
        {
            CwsPath      = File.Exists(cws) ? cws : FindCwsBelow(targetDir),
            WorkspaceDir = targetDir,
            FileCount    = count,
            Rejected     = rejected,
        };
    }

    /// <summary>The one folder every entry sits under, or null when they do not share one.</summary>
    public static string? CommonRootFolder(IEnumerable<string> entryNames)
    {
        string? root = null;

        foreach (var name in entryNames)
        {
            var slash = name.IndexOf('/');
            if (slash <= 0) return null;                      // a file at the top level — no shared root
            var first = name[..slash];
            if (root is null) root = first;
            else if (!string.Equals(root, first, StringComparison.Ordinal)) return null;
        }

        return root;
    }

    /// <summary>A <c>.cws</c> one level down, for an archive whose root folder is not the workspace itself.</summary>
    private static string? FindCwsBelow(string dir)
    {
        try
        {
            foreach (var sub in Directory.GetDirectories(dir))
            {
                var candidate = Path.Combine(sub, ".cws");
                if (File.Exists(candidate)) return candidate;
            }
        }
        catch { }
        return null;
    }
}

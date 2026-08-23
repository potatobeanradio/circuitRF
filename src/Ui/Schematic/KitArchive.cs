using System;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// Unpacks a kit delivered as a <c>.zip</c> into the workspace, so the rest of the import sees an
/// ordinary kit FOLDER.
///
/// <para><b>Why importing the archive directly could not work.</b> <c>PdkImporter</c> reads entries
/// straight out of an archive, which is enough to say what a kit CONTAINS and nothing more —
/// everything after that resolves paths against a directory that does not exist. Measured against
/// the installer's own <c>haveRoot</c> branch, an archive import produced: no symbol artwork and no
/// palette icons, no netlist discovery, no compiled models, no simulation settings and no manifest —
/// and then recorded the <c>.zip</c> ITSELF as the kit's location, so the very next workspace open
/// reported "the kit folder does not exist" and every part placed from it went unresolved. The door
/// was offered in the picker and led there.</para>
///
/// <para>Extracted into the workspace rather than beside the archive, under the same
/// <c>kits/</c> folder the archive/share path already uses for a kit it carries: a kit that lives
/// inside the workspace travels with it, which is the whole reason someone was handed a
/// <c>.zip</c>.</para>
/// </summary>
public static class KitArchive
{
    /// <summary>Where an unpacked kit lands, relative to the workspace root.</summary>
    public const string KitsFolderName = Archive.WorkspaceArchiveScanner.KitsFolder;

    /// <summary>True when <paramref name="path"/> is an archive rather than a kit folder.</summary>
    public static bool IsArchive(string? path)
        => !string.IsNullOrEmpty(path)
           && Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase);

    /// <summary>Where <paramref name="archivePath"/> would be unpacked to.</summary>
    public static string DestinationFor(string archivePath, string workspaceRootDir)
        => Path.Combine(workspaceRootDir, KitsFolderName,
                        Path.GetFileNameWithoutExtension(archivePath));

    /// <summary>
    /// Unpacks <paramref name="archivePath"/> and returns the folder the kit actually lives in.
    ///
    /// <para><b>The returned folder is not always the destination.</b> A kit archive is nearly always
    /// packed with its own name as a single top-level folder, so extracting gives
    /// <c>kits/foo/foo/…</c> and the kit is one level down.</para>
    /// </summary>
    /// <param name="overwrite">
    /// Whether an existing destination may be replaced. False and the folder is already there throws,
    /// so the caller can ask rather than silently discarding a kit the user may have edited.
    /// </param>
    public static string ExtractInto(string archivePath, string workspaceRootDir, bool overwrite)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRootDir);

        string destination = DestinationFor(archivePath, workspaceRootDir);

        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
        {
            if (!overwrite)
                throw new IOException($"'{destination}' already holds an unpacked kit.");

            Directory.Delete(destination, recursive: true);
        }

        Directory.CreateDirectory(destination);
        // .NET's own extraction refuses an entry that would escape the destination, so a maliciously
        // pathed archive cannot write outside kits/<name>/.
        ZipFile.ExtractToDirectory(archivePath, destination, overwriteFiles: true);

        return UnwrapSelfNamedFolder(destination, Path.GetFileNameWithoutExtension(archivePath));
    }

    /// <summary>
    /// The wrapper folder inside <paramref name="dir"/>, or <paramref name="dir"/> itself.
    ///
    /// <para><b>The test is the folder's NAME, not that it is the only one, and the difference is
    /// not academic.</b> "One directory and no files" also describes a kit whose top level is a
    /// single <c>cells/</c> — descending into that returns something that is not the kit at all, and
    /// every path resolved from it is wrong in a way nothing downstream can detect. A wrapper is
    /// recognisable because archiving tools NAME it after what was archived, so matching the
    /// archive's own name is the evidence; anything else is left alone.</para>
    ///
    /// <para>Not unwrapping is the safe failure. An extra level costs the kit's folder name and one
    /// path segment on every relative path, both of which the importer already handles — it walks
    /// subdirectories and resolves assets against the root it was given.</para>
    /// </summary>
    private static string UnwrapSelfNamedFolder(string dir, string archiveName)
    {
        try
        {
            if (Directory.EnumerateFiles(dir).Any()) return dir;

            var children = Directory.EnumerateDirectories(dir).Take(2).ToList();
            if (children.Count != 1) return dir;

            return string.Equals(Path.GetFileName(children[0]), archiveName,
                                 StringComparison.OrdinalIgnoreCase)
                ? children[0]
                : dir;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return dir;
        }
    }
}

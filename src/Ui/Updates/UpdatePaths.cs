using System;
using System.IO;

namespace CircuitRF.Ui.Updates;

/// <summary>
/// Every directory the updater owns — and, just as importantly, the complete list of what it is
/// allowed to delete.
///
/// <para>All of it hangs off <see cref="AppDataRoot.SubDir"/>, which is already redirectable, so
/// <c>tools/DocGen</c> and the whole test suite are isolated <b>by construction</b> rather than by
/// remembering to disable something.</para>
/// </summary>
public static class UpdatePaths
{
    /// <summary>Everything below here belongs to the updater. Nothing above it may be touched.</summary>
    public static string Root => AppDataRoot.SubDir("updates");

    /// <summary>Partial downloads. Never executed from; always safe to delete.</summary>
    public static string Staging => Path.Combine(Root, "staging");

    /// <summary>Where a macOS bundle is unpacked before the swap, and where the replaced one is kept.</summary>
    public static string Staged   => Path.Combine(Root, "staged");

    /// <summary>The previous macOS bundle, retained until the new one has launched successfully once.</summary>
    public static string Previous => Path.Combine(Root, "previous");

    /// <summary>The updater's own small state file — <b>not</b> a preference. See <see cref="UpdateState"/>.</summary>
    public static string StateFile => Path.Combine(Root, "state.json");

    /// <summary>The suffix nothing incomplete is ever without.</summary>
    public const string PartialSuffix = ".partial";

    /// <summary>True when <paramref name="path"/> is strictly inside <paramref name="root"/>.</summary>
    public static bool IsUnder(string? path, string? root)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root)) return false;
        try
        {
            string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            string r    = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            return full.Length > r.Length
                   && full.StartsWith(r, StringComparison.Ordinal)
                   && full[r.Length] == Path.DirectorySeparatorChar;
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// True when <paramref name="path"/> is one of the two things the updater is allowed to destroy
    /// or to exchange: something inside <see cref="Root"/>, or an <c>app-*</c> child of the install
    /// root.
    ///
    /// <para><b>Design §13 rule 6 stated as a predicate, so it can be enforced rather than intended.</b>
    /// <see cref="UpdateReclaimer"/> already held this line for its own sweep; every other path that
    /// deletes or swaps took its target from <c>state.json</c> and trusted it. That file is ordinary
    /// JSON in the user's application-data directory, so its contents are whatever is in the file
    /// rather than whatever the updater last wrote — and <c>staged_path</c> reached
    /// <c>Directory.Delete(recursive)</c> and, on macOS, the directory exchange that lands in
    /// <c>/Applications</c> (security review, 2026-08-25).</para>
    /// </summary>
    public static bool IsOurs(string? path, string? installRoot)
    {
        if (string.IsNullOrEmpty(path)) return false;

        if (IsUnder(path, Root)) return true;
        if (string.IsNullOrEmpty(installRoot)) return false;

        try
        {
            string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            return string.Equals(Path.GetDirectoryName(full),
                                 Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot)),
                                 StringComparison.Ordinal)
                   && (Path.GetFileName(full)?.StartsWith(UpdateInstallSite.VersionDirPrefix,
                                                          StringComparison.Ordinal) ?? false);
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

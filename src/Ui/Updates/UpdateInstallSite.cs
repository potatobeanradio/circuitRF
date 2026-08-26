using System;
using System.IO;

namespace CircuitRF.Ui.Updates;

/// <summary>How the installation on disk is laid out — detected structurally, never from the OS.</summary>
public enum InstallShape
{
    /// <summary>A macOS <c>.app</c> bundle. The bundle itself is the launch path and is swapped whole.</summary>
    MacOsBundle,

    /// <summary>Versioned <c>app-&lt;ver&gt;</c> directories behind a <c>current</c> pointer — the user-local channel.</summary>
    VersionedPointer,

    /// <summary>A plain directory of files: the per-machine <c>.msi</c> and the <c>.deb</c>. Never updated in place.</summary>
    Flat,
}

/// <summary>Where this application is installed, and whether the running user may rewrite it.</summary>
/// <param name="Root">The root of the layout — the <c>.app</c>, or the directory holding <c>current</c>.</param>
/// <param name="Shape">Which layout was found.</param>
/// <param name="IsWritable">Probed by attempting a write, never inferred from the path.</param>
/// <param name="ProbeDirectory">The directory whose writability was probed; useful in a log line.</param>
public sealed record InstallSite(string Root, InstallShape Shape, bool IsWritable, string ProbeDirectory)
{
    /// <summary>
    /// True when this installation can be updated silently. Everything else is
    /// <b>notify-only</b>: check, post one Message Panel line with a link, write nothing.
    /// </summary>
    public bool CanSelfUpdate => IsWritable && Shape is InstallShape.MacOsBundle or InstallShape.VersionedPointer;
}

/// <summary>
/// The one runtime check the whole feature rests on:
///
/// <blockquote>Can this process write its own install tree, and is that tree laid out the way the
/// updater expects?</blockquote>
///
/// <para><b>One predicate, not three platform branches.</b> It covers macOS-as-a-standard-user, the
/// per-machine MSI and the <c>.deb</c> identically, because none of them differ in any way the
/// updater cares about beyond the answer to that question. If this ever grows a <c>switch</c> on
/// <c>RuntimeInformation.OSPlatform</c> to decide <i>policy</i>, it has gone wrong — platform
/// branches belong only in the primitives that move bytes.</para>
///
/// <para><b>Writability is probed, not inferred.</b> <c>/Applications</c> is writable for an admin
/// user and not for a standard one, and no amount of path inspection reveals which. So the check
/// creates a file and deletes it.</para>
/// </summary>
public static class UpdateInstallSite
{
    /// <summary>The pointer file naming the version directory to run.</summary>
    public const string CurrentPointerName = "current";

    /// <summary>The prefix a versioned application directory carries.</summary>
    public const string VersionDirPrefix = "app-";

    private static InstallSite? _cached;
    private static readonly object CacheLock = new();

    /// <summary>
    /// Detects the site this process is actually running from, once per process.
    ///
    /// <para><b>Cached because detection WRITES.</b> Writability is probed by creating and deleting a
    /// file (<see cref="IsDirectoryWritable"/>) — the only way to get a true answer — and
    /// <c>UpdatePolicy.Current</c> calls this on every read, which is every settings-dialog load and
    /// every checkbox click. Uncached, that put a probe file into <c>/Applications</c> or
    /// <c>%ProgramFiles%</c> on each one. None of the three answers can change within a session: the
    /// process cannot move its own install tree, and a permission change mid-session is not something
    /// to chase.</para>
    /// </summary>
    public static InstallSite Detect()
    {
        lock (CacheLock) return _cached ??= DetectFrom(AppContext.BaseDirectory);
    }

    /// <summary>For tests, which build a fresh temp-directory fixture per case.</summary>
    internal static void ResetCacheForTests()
    {
        lock (CacheLock) _cached = null;
    }

    /// <summary>
    /// The testable form: detect from an arbitrary base directory, so every layout in design §2 gets a
    /// temp-directory fixture and no real installation is involved.
    /// </summary>
    public static InstallSite DetectFrom(string baseDirectory)
    {
        string baseDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory));

        // 1. A macOS bundle: some ancestor is `<name>.app` with a `Contents` directory in it. Found by
        //    walking up, so it holds for `Contents/MacOS` and for anything nested deeper inside.
        for (string? d = baseDir; d is not null; d = Path.GetDirectoryName(d))
        {
            if (!d.EndsWith(".app", StringComparison.Ordinal)) continue;
            if (!Directory.Exists(Path.Combine(d, "Contents"))) continue;

            // The bundle is REPLACED, not written into, so what must be writable is the directory
            // that HOLDS it — /Applications for an admin, and not for a standard user.
            string parent = Path.GetDirectoryName(d) ?? d;
            return new InstallSite(d, InstallShape.MacOsBundle, IsDirectoryWritable(parent), parent);
        }

        // 2. The versioned layout: we are inside `app-<ver>/` and the parent holds `current`.
        string? name   = Path.GetFileName(baseDir);
        string? parentDir = Path.GetDirectoryName(baseDir);
        if (name is not null && parentDir is not null &&
            name.StartsWith(VersionDirPrefix, StringComparison.Ordinal) &&
            PointerExists(parentDir))
        {
            return new InstallSite(parentDir, InstallShape.VersionedPointer,
                                   IsDirectoryWritable(parentDir), parentDir);
        }

        // 3. Anything else is a flat install — the .msi and the .deb. Notify-only whatever the
        //    permissions say, because there is nowhere to put a second version.
        return new InstallSite(baseDir, InstallShape.Flat, IsDirectoryWritable(baseDir), baseDir);
    }

    /// <summary>
    /// True when <paramref name="name"/> is a plain versioned-application directory name — the only
    /// thing that may ever be written into <c>current</c> or joined to the install root.
    ///
    /// <para><b>Why this is checked and not assumed.</b> The names in question travel through
    /// <c>state.json</c>, which is ordinary JSON in the user's application-data directory and is
    /// therefore whatever is in that file rather than whatever the updater last wrote. Everything
    /// downstream treats those strings as path components and as the contents of the launch pointer.
    /// Checking the shape here — <c>app-</c>, then the characters a version can contain, and nothing
    /// else — makes the state file unable to name anything outside the install root, which is a
    /// property worth having whether or not a route to abusing it exists today (security review,
    /// 2026-08-25).</para>
    /// </summary>
    public static bool IsSafeVersionDirectoryName(string? name)
        => !string.IsNullOrEmpty(name)
           && name.StartsWith(VersionDirPrefix, StringComparison.Ordinal)
           && IsSafeVersionText(name[VersionDirPrefix.Length..]);

    /// <summary>
    /// True when <paramref name="text"/> is safe to use as a path SEGMENT — the version half of an
    /// <c>app-&lt;ver&gt;</c> directory, and the <c>updates/staged/&lt;ver&gt;/</c> segment macOS
    /// staging uses.
    ///
    /// <para><b>Why a release tag needs this and <see cref="SemanticVersion"/> is not enough.</b>
    /// <c>ReleaseInfo.VersionText</c> is the TAG'S OWN SPELLING, deliberately — the packaging
    /// scripts interpolate the <c>VERSION</c> file verbatim, so a <c>1.0</c> tag names a
    /// <c>circuitRF-1.0-arm64.dmg</c> that a normalised <c>1.0.0</c> would never match. But
    /// <c>SemanticVersion.TryParse</c> <c>Trim()</c>s before it validates, so a tag written with
    /// leading or trailing whitespace parses while <c>VersionText</c> keeps the whitespace — and
    /// that string is then joined to the install root and to the staging directory (security review,
    /// 2026-08-25). Nothing traversed, because the charset the parser enforces on the identifiers
    /// leaves no separator to traverse with; what it produced was a junk directory in the live
    /// install root and an update that could never be applied, silently. Checking the segment
    /// itself removes the class rather than relying on the parser's incidental strictness.</para>
    /// </summary>
    public static bool IsSafeVersionText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        if (text.Length > SemanticVersion.MaxLength) return false;

        foreach (char c in text)
            if (!char.IsAsciiLetterOrDigit(c) && c != '.' && c != '-' && c != '+' && c != '_')
                return false;

        // Separators are already excluded by the charset, so `..` could only be a whole component.
        // Belt on top of braces, and free.
        return !text.Contains("..", StringComparison.Ordinal);
    }

    /// <summary>
    /// True when <paramref name="root"/> holds a <c>current</c> pointer. A symlink whose target has
    /// gone is still a pointer, so this asks the filesystem for an entry rather than for a readable
    /// file — <see cref="File.Exists"/> alone answers false for a dangling symlink, which would
    /// silently demote a working Linux install to notify-only.
    /// </summary>
    public static bool PointerExists(string root)
    {
        return AtomicFile.ExistsIncludingLink(Path.Combine(root, CurrentPointerName));
    }

    /// <summary>
    /// Probes write access the only way that answers the question: by writing. Permission bits,
    /// ownership and ACLs all lie in at least one real case each.
    /// </summary>
    public static bool IsDirectoryWritable(string directory)
    {
        if (!Directory.Exists(directory)) return false;

        string probe = Path.Combine(directory, $".crf-update-probe-{Guid.NewGuid():N}");
        try
        {
            using (FileStream fs = File.Create(probe, 1, FileOptions.DeleteOnClose))
            {
                fs.WriteByte(0);
            }
            return true;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or NotSupportedException)
        {
            return false;
        }
        finally
        {
            try { if (File.Exists(probe)) File.Delete(probe); } catch { /* best effort */ }
        }
    }
}

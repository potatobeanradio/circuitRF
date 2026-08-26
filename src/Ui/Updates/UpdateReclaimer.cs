using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CircuitRF.Ui.Updates;

/// <summary>The steps <see cref="UpdateReclaimer"/> takes, in the order it takes them.</summary>
public enum ReclaimStep
{
    /// <summary>Partial downloads. Always safe: nothing is ever executed from <c>staging/</c>.</summary>
    Staging,

    /// <summary>Any <c>.partial</c> tree. Safe by construction — nothing incomplete ever held a real name.</summary>
    PartialTrees,

    /// <summary>Staged versions that were abandoned or blacklisted.</summary>
    AbandonedStaged,

    /// <summary>The retained previous version — <b>only</b> once the running one has cleared its startup counter.</summary>
    PreviousVersion,
}

/// <summary>
/// Gives back the updater's own footprint when space is short, and clears debris at every launch.
///
/// <para><b>The rule with teeth: it never leaves our own directories.</b> The updater has no opinions
/// about the user's disk. It does not clear caches, it does not touch workspaces, and it does not
/// "helpfully" find space anywhere it did not itself consume. Every path this class deletes is
/// checked against <see cref="UpdatePaths.Root"/> or an <c>app-*</c> directory under the install
/// root, and a path that is neither is skipped rather than trusted.</para>
///
/// <para>The practical consequence of running this at every launch is that a disk-full event is
/// <b>self-limiting</b>: the wasted space comes back the next time the application starts, whether or
/// not the update ever completes, and no sequence of failures accumulates debris.</para>
/// </summary>
public sealed class UpdateReclaimer
{
    private readonly string _updatesRoot;
    private readonly string? _installRoot;
    private readonly string? _runningDirName;
    private readonly string? _previousDirName;

    /// <param name="updatesRoot">Normally <see cref="UpdatePaths.Root"/>; a temp directory in tests.</param>
    /// <param name="installRoot">The versioned-layout root, when there is one. Only its <c>app-*</c>
    /// children are ever eligible, and never the one currently running.</param>
    /// <param name="runningDirectoryName">
    /// The <c>app-&lt;ver&gt;</c> directory THIS PROCESS is executing from. Defaults to the real one
    /// and is a parameter only so a test can drive it.
    ///
    /// <para><b>This is not belt-and-braces; without it the reclaimer deletes the running
    /// application</b> (found in review, 2026-08-25). The session that flips <c>current</c> to
    /// <c>app-v2</c> keeps running out of <c>app-v1</c> for the rest of its life — the swap is for
    /// the NEXT launch. Deriving "abandoned" from the pointer alone therefore classifies the tree
    /// under the running process's feet as debris, and removes it when the first window appears. On
    /// Windows the file locks mostly hide it (with a half-deleted tree left behind); on Linux the
    /// unlinks succeed and every not-yet-loaded assembly and lazily-resolved resource is gone. It
    /// also destroys the one tree a rollback would need.</para>
    /// </param>
    /// <param name="previousVersionDirectoryName">
    /// The retained previous version, when the versioned layout is in use. Held back from the
    /// <see cref="ReclaimStep.AbandonedStaged"/> sweep so that R-AU-17's order is real: it is only
    /// ever released at the <see cref="ReclaimStep.PreviousVersion"/> step, and only then when the
    /// running version has cleared its startup counter.
    /// </param>
    public UpdateReclaimer(
        string updatesRoot,
        string? installRoot = null,
        string? runningDirectoryName = null,
        string? previousVersionDirectoryName = null)
    {
        _updatesRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(updatesRoot));
        _installRoot = installRoot is null
            ? null
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot));

        _runningDirName  = runningDirectoryName ?? RunningDirectoryName();
        _previousDirName = previousVersionDirectoryName;
    }

    /// <summary>
    /// The name of the directory this process is running out of — <c>app-&lt;ver&gt;</c> in the
    /// versioned layout, and something irrelevant everywhere else, which costs nothing because only
    /// <c>app-*</c> names are ever eligible in the first place.
    /// </summary>
    public static string RunningDirectoryName()
    {
        try
        {
            return Path.GetFileName(Path.TrimEndingDirectorySeparator(
                       Path.GetFullPath(AppContext.BaseDirectory))) ?? "";
        }
        catch (Exception e) when (e is IOException or ArgumentException) { return ""; }
    }

    /// <summary>Every path this instance actually removed, in order — what the reclaim-order test reads.</summary>
    public List<string> Deleted { get; } = [];

    /// <summary>Every path it declined to remove because it fell outside our own directories.</summary>
    public List<string> Refused { get; } = [];

    /// <summary>
    /// Unconditional launch-time cleanup: <c>staging/</c> and every <c>.partial</c> tree. Both are safe
    /// to delete without asking anything, because nothing incomplete has ever been given a real name.
    /// </summary>
    public void ReclaimDebris()
    {
        Remove(Path.Combine(_updatesRoot, "staging"));
        foreach (string p in PartialTrees()) Remove(p);
    }

    /// <summary>
    /// Frees space in the fixed order of design §13 rule 5, stopping as soon as
    /// <paramref name="enough"/> says there is room. Returns the steps actually taken.
    ///
    /// <para><paramref name="previousVersionReleasable"/> is the startup counter: the retained previous
    /// version is only eligible once the running version has cleared it, since until then it is the
    /// rollback this whole design's insurance rests on.</para>
    /// </summary>
    public IReadOnlyList<ReclaimStep> ReclaimUntil(
        Func<bool> enough,
        bool previousVersionReleasable,
        IReadOnlyCollection<string>? runningVersionDirs = null)
    {
        var taken = new List<ReclaimStep>();
        if (enough()) return taken;

        Remove(Path.Combine(_updatesRoot, "staging"));
        taken.Add(ReclaimStep.Staging);
        if (enough()) return taken;

        foreach (string p in PartialTrees()) Remove(p);
        taken.Add(ReclaimStep.PartialTrees);
        if (enough()) return taken;

        foreach (string p in AbandonedStaged(runningVersionDirs)) Remove(p);
        taken.Add(ReclaimStep.AbandonedStaged);
        if (enough()) return taken;

        if (previousVersionReleasable)
        {
            Remove(Path.Combine(_updatesRoot, "previous"));

            // The versioned layout keeps its previous generation as a sibling app-<ver> directory
            // rather than under updates/, so releasing "the previous version" has to release both —
            // and only HERE, never in the sweep above.
            if (_installRoot is not null && !string.IsNullOrEmpty(_previousDirName))
                Remove(Path.Combine(_installRoot, _previousDirName));

            taken.Add(ReclaimStep.PreviousVersion);
        }

        return taken;
    }

    private IEnumerable<string> PartialTrees()
    {
        foreach (string root in Roots())
        {
            if (!Directory.Exists(root)) continue;

            foreach (string d in Directory.EnumerateDirectories(root))
                if (d.EndsWith(UpdatePaths.PartialSuffix, StringComparison.Ordinal)) yield return d;

            foreach (string f in Directory.EnumerateFiles(root))
                if (f.EndsWith(UpdatePaths.PartialSuffix, StringComparison.Ordinal)) yield return f;
        }
    }

    private IEnumerable<string> AbandonedStaged(IReadOnlyCollection<string>? keep)
    {
        string staged = Path.Combine(_updatesRoot, "staged");
        if (Directory.Exists(staged))
            foreach (string d in Directory.EnumerateDirectories(staged)) yield return d;

        // Versioned installs: an app-<ver> directory that is neither running nor the current pointer's
        // target is an abandoned stage. Anything not named app-* is somebody else's and is left alone.
        if (_installRoot is null || !Directory.Exists(_installRoot)) yield break;

        foreach (string d in Directory.EnumerateDirectories(_installRoot))
        {
            string name = Path.GetFileName(d);
            if (!name.StartsWith(UpdateInstallSite.VersionDirPrefix, StringComparison.Ordinal)) continue;
            if (IsProtected(name)) continue;
            if (keep is not null && keep.Contains(name, StringComparer.Ordinal)) continue;
            yield return d;
        }
    }

    /// <summary>
    /// The two directories under the install root that are never debris, whatever the pointer says:
    /// the one this process is executing from, and the retained previous version (which has its own
    /// step, further down the order).
    /// </summary>
    public bool IsProtected(string versionDirectoryName)
        => (!string.IsNullOrEmpty(_runningDirName)
            && string.Equals(versionDirectoryName, _runningDirName, StringComparison.Ordinal))
           || (!string.IsNullOrEmpty(_previousDirName)
            && string.Equals(versionDirectoryName, _previousDirName, StringComparison.Ordinal));

    private IEnumerable<string> Roots()
    {
        yield return _updatesRoot;
        if (_installRoot is not null) yield return _installRoot;
    }

    /// <summary>
    /// True when <paramref name="path"/> is inside the updater's own tree, or is an <c>app-*</c> child
    /// of the install root. This is the guarantee, and it is asserted directly by a test rather than
    /// left as an intention.
    /// </summary>
    public bool IsOurs(string path)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

        if (IsUnder(full, _updatesRoot)) return true;

        if (_installRoot is not null &&
            string.Equals(Path.GetDirectoryName(full), _installRoot, StringComparison.Ordinal) &&
            (Path.GetFileName(full)?.StartsWith(UpdateInstallSite.VersionDirPrefix, StringComparison.Ordinal) ?? false))
            return true;

        return false;
    }

    private static bool IsUnder(string path, string root)
        => path.Length > root.Length
           && path.StartsWith(root, StringComparison.Ordinal)
           && path[root.Length] == Path.DirectorySeparatorChar;

    private void Remove(string path)
    {
        if (!IsOurs(path)) { Refused.Add(path); return; }

        // The last line of defence, so that no future caller can route around IsProtected by
        // constructing a path itself. The PreviousVersion step passes `force` for the one directory
        // it is expressly allowed to take.
        if (_installRoot is not null &&
            string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), _installRoot, StringComparison.Ordinal) &&
            !string.IsNullOrEmpty(_runningDirName) &&
            string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(path)), _runningDirName,
                          StringComparison.Ordinal))
        {
            Refused.Add(path);
            return;
        }

        try
        {
            if (Directory.Exists(path) && !AtomicFile.IsSymlink(path))
            {
                Directory.Delete(path, recursive: true);
                Deleted.Add(path);
            }
            else if (File.Exists(path) || AtomicFile.IsSymlink(path))
            {
                File.Delete(path);
                Deleted.Add(path);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A file another process is holding open is not worth failing an update over; the next
            // launch tries again, which is what makes this self-limiting rather than fatal.
        }
    }
}

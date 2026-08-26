using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace CircuitRF.Ui.Updates;

/// <summary>What staging produced, or why it did not.</summary>
public enum StageOutcome { Staged, UnpackFailed, VerificationFailed, Unsupported }

/// <summary>The staged version, ready and inert until the next launch.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="StagedPath">The completed tree or bundle, holding its real (non-<c>.partial</c>) name.</param>
/// <param name="Detail">For the application log only. Never shown to the user.</param>
public sealed record StageResult(StageOutcome Outcome, string? StagedPath, string Detail)
{
    public bool Ok => Outcome == StageOutcome.Staged;
}

/// <summary>
/// Unpacks a verified payload into place — and gives it a real name only once it is complete.
///
/// <para><b>Nothing incomplete ever holds a real name.</b> The tree is written as
/// <c>app-&lt;ver&gt;.partial</c> (or a <c>.partial</c> bundle) and renamed to the real name only
/// when it is finished and verified. A rename within one filesystem is atomic and needs no space,
/// so an interrupted unpack is harmless: nothing executes from, or counts as, a <c>.partial</c>
/// path, and the next launch reclaims it. That single naming rule is the whole mechanism.</para>
/// </summary>
public sealed class UpdateStager
{
    private readonly IFreeSpaceProbe _space;

    public UpdateStager(IFreeSpaceProbe space) => _space = space;

    /// <summary>The suffix a tree carries while it is being written.</summary>
    public static string PartialNameFor(string finalPath) => finalPath + UpdatePaths.PartialSuffix;

    /// <summary>
    /// True when <paramref name="path"/> is something the updater may treat as staged. A
    /// <c>.partial</c> path is never staged, never executed from, and never counted.
    /// </summary>
    public static bool IsStageable(string path)
        => !Path.TrimEndingDirectorySeparator(path)
                .EndsWith(UpdatePaths.PartialSuffix, StringComparison.Ordinal);

    // ── macOS ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Extracts <c>&lt;app&gt;.app</c> from the downloaded <c>.dmg</c> into
    /// <paramref name="destinationBundle"/>.
    ///
    /// <para><b><c>ditto</c>, never <see cref="System.IO.Compression.ZipFile"/> and never a recursive
    /// <c>File.Copy</c>.</b> Both drop Unix mode bits and symlinks; a bundle missing its executable
    /// bit and its <c>Frameworks</c> links has a BROKEN code signature and is refused at launch —
    /// the exact failure this feature exists to prevent, arriving by the least obvious route, and
    /// only on a real signed build, so no unit test catches it.</para>
    ///
    /// <para>The image is detached in a <c>finally</c>. A leaked mount is a leaked disk, and the
    /// symptom is a volume the user cannot eject with no explanation of what is holding it.</para>
    ///
    /// <para><paramref name="verifyPartial"/> runs on the <c>.partial</c> bundle, BEFORE the rename
    /// that gives it a real name — same rule as the archive path, for the same reason.</para>
    /// </summary>
    public async Task<StageResult> StageMacBundleAsync(
        string dmgPath,
        string appBundleNameInImage,
        string destinationBundle,
        CancellationToken ct,
        Func<string, Task<VerifyResult>>? verifyPartial = null)
    {
        if (!OperatingSystem.IsMacOS())
            return new StageResult(StageOutcome.Unsupported, null, "not macOS");

        string partial = PartialNameFor(destinationBundle);
        string mount   = Path.Combine(Path.GetTempPath(), "crf-update-" + Guid.NewGuid().ToString("N")[..8]);

        SafeDelete(partial);
        Directory.CreateDirectory(mount);

        bool attached = false;
        try
        {
            // -noautoopen: an image may nominate something to open on mount, and the updater must
            //   not be the process that honours it.
            // -owners off: ownership recorded IN the image is ignored, so nothing it contains can
            //   arrive claiming to belong to another account.
            // Both added in the security review, 2026-08-25, alongside the signature check that now
            // runs on the image before this line is reached at all.
            ProcessResult a = await ProcessRunner.RunAsync(
                "hdiutil", ["attach", dmgPath, "-nobrowse", "-readonly", "-noautoopen",
                            "-owners", "off", "-mountpoint", mount, "-quiet"],
                ct, TimeSpan.FromMinutes(5)).ConfigureAwait(false);

            if (!a.Ok) return new StageResult(StageOutcome.UnpackFailed, null, "hdiutil attach: " + a.StdErr);
            attached = true;

            string source = Path.Combine(mount, appBundleNameInImage);
            if (!Directory.Exists(source))
                return new StageResult(StageOutcome.UnpackFailed, null, $"{appBundleNameInImage} is not in the image");

            ProcessResult d = await ProcessRunner.RunAsync(
                "ditto", [source, partial], ct, TimeSpan.FromMinutes(10)).ConfigureAwait(false);

            if (!d.Ok) return new StageResult(StageOutcome.UnpackFailed, null, "ditto: " + d.StdErr);
        }
        finally
        {
            if (attached)
            {
                try
                {
                    await ProcessRunner.RunAsync("hdiutil", ["detach", mount, "-quiet"],
                                                 CancellationToken.None, TimeSpan.FromMinutes(2))
                          .ConfigureAwait(false);
                }
                catch { /* the mount is the OS's problem now; nothing further we can do */ }
            }
            try { Directory.Delete(mount, false); } catch { /* best effort */ }
        }

        if (!Directory.Exists(partial))
            return new StageResult(StageOutcome.UnpackFailed, null, "ditto produced no bundle");

        if (verifyPartial is not null)
        {
            VerifyResult v = await verifyPartial(partial).ConfigureAwait(false);
            if (!v.Ok)
            {
                SafeDelete(partial);
                return new StageResult(StageOutcome.VerificationFailed, null, v.Detail);
            }
        }

        return Promote(partial, destinationBundle);
    }

    // ── Windows and Linux ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Unpacks a <c>.zip</c> or <c>.tar.gz</c> publish tree into <paramref name="destinationDir"/>.
    ///
    /// <para>Windows uses <see cref="System.IO.Compression.ZipFile"/>, which is correct there — the
    /// mode bits and symlinks it drops do not exist on that platform, and nothing is code-signed as
    /// a directory. Linux uses <c>tar</c>, which preserves both, and then the executable bit is set
    /// explicitly because a <c>.tar.gz</c> produced on a machine with an odd umask can arrive
    /// without one.</para>
    ///
    /// <para><b>The two archives do not have the same shape, and assuming they did broke Linux
    /// updates completely and silently</b> (found in review, 2026-08-25). <c>build-msi.ps1</c> runs
    /// <c>Compress-Archive -Path publish\*</c>, so the <c>.zip</c> holds the publish tree at its
    /// root. <c>build-tarball.sh</c> packs <c>circuitRF-&lt;ver&gt;/</c> holding <c>install.sh</c>,
    /// an icon, a <c>current</c> seed <b>and</c> <c>app-&lt;ver&gt;/</c> — because that archive is
    /// also the first-install payload and its shape IS the installed shape. A fixed
    /// <c>--strip-components</c> count cannot serve both, and getting it wrong produces
    /// <c>UnpackFailed</c> on every Linux machine, forever, with no error anywhere. So the tree is
    /// <b>located</b> after extraction — <see cref="FindPayloadRoot"/> — rather than assumed.</para>
    ///
    /// <para><paramref name="verifyPartial"/> runs against the <c>.partial</c> tree, BEFORE it is
    /// given its real name. Verification used to happen after the promotion, which left an
    /// unverified <c>app-&lt;ver&gt;</c> holding a real name in the live install root if the process
    /// died in between — the one place R-AU-27's naming rule was relaxed.</para>
    /// </summary>
    public async Task<StageResult> StageArchiveAsync(
        string archivePath,
        string destinationDir,
        string executableName,
        CancellationToken ct,
        Func<string, Task<VerifyResult>>? verifyPartial = null)
    {
        string partial = PartialNameFor(destinationDir);
        SafeDelete(partial);
        Directory.CreateDirectory(partial);

        try
        {
            if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                await Task.Run(
                    () => System.IO.Compression.ZipFile.ExtractToDirectory(archivePath, partial, true), ct)
                    .ConfigureAwait(false);
            }
            else
            {
                // No --strip-components: the shape is discovered below rather than assumed, so this
                // extracts verbatim and FindPayloadRoot descends to whatever actually holds the exe.
                ProcessResult t = await ProcessRunner.RunAsync(
                    "tar", ["--no-same-owner", "-xzf", archivePath, "-C", partial],
                    ct, TimeSpan.FromMinutes(10)).ConfigureAwait(false);

                if (!t.Ok) return new StageResult(StageOutcome.UnpackFailed, null, "tar: " + t.StdErr);

                // A tar archive can carry symlinks, and a symlink is a write instruction aimed
                // wherever it points. GNU tar refuses a member NAMED with `..` and strips a leading
                // `/`, but a link whose TARGET escapes is an ordinary, valid member — and the tree it
                // lands in is about to be renamed into the live install root and executed from. So
                // the tree is checked, once, before anything else looks at it (security review,
                // 2026-08-25).
                string? escape = FirstEscapingLink(partial);
                if (escape is not null)
                {
                    SafeDelete(partial);
                    return new StageResult(StageOutcome.UnpackFailed, null,
                                           $"the archive contains a link out of its own tree: {escape}");
                }
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            SafeDelete(partial);
            return new StageResult(StageOutcome.UnpackFailed, null, e.Message);
        }

        string? payload = FindPayloadRoot(partial, executableName);
        if (payload is null)
        {
            SafeDelete(partial);
            return new StageResult(StageOutcome.UnpackFailed, null, $"the archive contains no {executableName}");
        }

        string exe = Path.Combine(payload, executableName);

        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(exe, File.GetUnixFileMode(exe)
                                          | UnixFileMode.UserExecute
                                          | UnixFileMode.GroupExecute
                                          | UnixFileMode.OtherExecute);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Not fatal on its own; the launch would report it far more usefully than we can.
            }
        }

        // The signature and the publisher identity, on the tree that is ACTUALLY going to run, while
        // it still carries a .partial name and abandoning it costs nothing.
        if (verifyPartial is not null)
        {
            VerifyResult v = await verifyPartial(payload).ConfigureAwait(false);
            if (!v.Ok)
            {
                SafeDelete(partial);
                return new StageResult(StageOutcome.VerificationFailed, null, v.Detail);
            }
        }

        // The payload may be nested (Linux). Lift it out so the promoted directory IS the app tree,
        // and drop the first-install scaffolding (install.sh, the icon, the `current` seed) that the
        // updater has no use for.
        if (!string.Equals(Path.TrimEndingDirectorySeparator(payload),
                           Path.TrimEndingDirectorySeparator(partial), StringComparison.Ordinal))
        {
            string lifted = partial + ".lifted";
            SafeDelete(lifted);
            try
            {
                Directory.Move(payload, lifted);
                SafeDelete(partial);
                Directory.Move(lifted, partial);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                SafeDelete(lifted);
                SafeDelete(partial);
                return new StageResult(StageOutcome.UnpackFailed, null, e.Message);
            }
        }

        return Promote(partial, destinationDir);
    }

    /// <summary>
    /// Finds the directory inside an extracted archive that actually holds
    /// <paramref name="executableName"/>: the extraction root itself (the Windows <c>.zip</c>), or a
    /// child, or a grandchild (the Linux <c>circuitRF-&lt;ver&gt;/app-&lt;ver&gt;/</c>). Null when
    /// the archive holds no such executable at all, which is the honest refusal.
    ///
    /// <para>Bounded to two levels deliberately: an unbounded walk of an archive we are about to run
    /// is a search for something to execute, and the two real layouts are both within it.</para>
    /// </summary>
    public static string? FindPayloadRoot(string extractionRoot, string executableName)
    {
        if (File.Exists(Path.Combine(extractionRoot, executableName))) return extractionRoot;

        foreach (string child in SafeChildren(extractionRoot))
        {
            if (File.Exists(Path.Combine(child, executableName))) return child;

            foreach (string grandchild in SafeChildren(child))
                if (File.Exists(Path.Combine(grandchild, executableName))) return grandchild;
        }

        return null;
    }

    /// <summary>
    /// The first symbolic link under <paramref name="root"/> whose target leaves it, or null when
    /// every link stays inside.
    ///
    /// <para>Relative links are resolved against the link's OWN directory, which is what the
    /// filesystem does — so <c>current -&gt; app-1.0.0</c>, the one legitimate link the Linux
    /// tarball carries, is inside and stays. An unreadable entry answers as an escape: unknown must
    /// not authorise an execute any more than it authorises a delete.</para>
    /// </summary>
    public static string? FirstEscapingLink(string root)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(full, "*", SearchOption.AllDirectories);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return root;
        }

        foreach (string entry in entries)
        {
            string? target;
            try
            {
                target = File.ResolveLinkTarget(entry, returnFinalTarget: false)?.FullName;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return entry;
            }

            if (target is null) continue;   // not a link

            string resolved;
            try
            {
                resolved = Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(target, Path.GetDirectoryName(entry) ?? full));
            }
            catch (Exception e) when (e is ArgumentException or IOException) { return entry; }

            if (!string.Equals(resolved, full, StringComparison.Ordinal) &&
                !resolved.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                return entry;
        }

        return null;
    }

    private static string[] SafeChildren(string dir)
    {
        try   { return Directory.GetDirectories(dir); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>
    /// The rename that turns wreckage into a staged update. Everything before this point can be
    /// abandoned at no cost; nothing after it consumes space.
    /// </summary>
    private static StageResult Promote(string partial, string final)
    {
        // THE LAST LINE OF DEFENCE, and it is here rather than only in the caller for the same reason
        // UpdateReclaimer's running-directory refusal is: no future caller should be able to route
        // around it by constructing a path itself.
        //
        // SafeDelete(final) is the most destructive line in this file. If `final` is what a sibling
        // `current` pointer names, deleting it and then failing to rename leaves the stub with a
        // pointer to a directory that is not there and an application that will not start. That was
        // reachable — a version already swapped in and pending is not recorded in StagedVersion, so a
        // manual check re-staged it straight over the live tree (found in a second review,
        // 2026-08-25). Refusing costs one file read and makes the whole class impossible.
        if (Directory.Exists(final) && IsLivePointerTarget(final))
        {
            SafeDelete(partial);
            return new StageResult(StageOutcome.UnpackFailed, null,
                                   $"{Path.GetFileName(final)} is what `current` names; it is not a place to stage into");
        }

        try
        {
            SafeDelete(final);
            Directory.Move(partial, final);
            return new StageResult(StageOutcome.Staged, final, "staged");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            SafeDelete(partial);
            return new StageResult(StageOutcome.UnpackFailed, null, e.Message);
        }
    }

    /// <summary>
    /// True when <paramref name="versionDirectory"/> is the directory a sibling <c>current</c> pointer
    /// names — i.e. the tree the stub or the symlink would launch right now.
    ///
    /// <para>Answers false for anything that is not in a versioned layout, which is the macOS case and
    /// costs one <c>File.Exists</c> there.</para>
    /// </summary>
    public static bool IsLivePointerTarget(string versionDirectory)
    {
        try
        {
            string full   = Path.TrimEndingDirectorySeparator(Path.GetFullPath(versionDirectory));
            string? root  = Path.GetDirectoryName(full);
            if (root is null) return false;

            string? current = UpdateSwap.ReadCurrent(root);
            return current is not null
                   && string.Equals(current, Path.GetFileName(full), StringComparison.Ordinal);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Unreadable means unknown, and unknown must not authorise a delete.
            return true;
        }
    }

    /// <summary>
    /// Removes a staged tree after a verification failure, or when a preference change no longer
    /// justifies keeping it. Silent.
    ///
    /// <para><b><paramref name="installRoot"/> is not optional decoration: without it this method is
    /// an arbitrary recursive delete driven by <c>state.json</c></b> (security review, 2026-08-25).
    /// Its callers take the path from <c>staged_path</c>, which is ordinary JSON in the user's
    /// application-data directory — so unchecking "Automatic updates" ran
    /// <c>Directory.Delete(recursive: true)</c> on whatever that field named. Design §13 rule 6 says
    /// the updater never deletes anything outside its own directories; this is the line that
    /// consumes the value, so this is where the rule is enforced.</para>
    ///
    /// <para>A path that is not ours is left alone rather than reported: every caller is on a silent
    /// path already, and refusing is the whole of the remedy.</para>
    /// </summary>
    public static void Discard(string? path, string? installRoot = null)
    {
        if (string.IsNullOrEmpty(path)) return;
        if (!UpdatePaths.IsOurs(path, installRoot)) return;
        SafeDelete(path);
    }

    /// <summary>Bytes free where staging happens — so the caller can re-check between phases.</summary>
    public long AvailableAt(string path) => _space.AvailableFreeSpace(path);

    private static void SafeDelete(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (Directory.Exists(path) && !AtomicFile.IsSymlink(path)) Directory.Delete(path, true);
            else if (File.Exists(path) || AtomicFile.IsSymlink(path))  File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Another process is holding it; the next launch's reclaim will take it.
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace CircuitRF.Ui.Updates;

/// <summary>
/// Writes that land by <b>rename</b>, never by truncation — the mechanism that makes every failure
/// mode in this feature recoverable rather than destructive.
///
/// <para><b>The disaster this exists to make impossible.</b> On Windows and Linux an update is
/// finished by writing a one-line <c>current</c> pointer. The obvious implementation opens it for
/// truncation and writes; if that write fails with ENOSPC, <c>current</c> is now <b>empty</b>, the
/// stub launcher no longer knows what to run, and the application will not start at all. A full disk
/// has become an uninstallation, and nobody would ever connect the two. It is the single most
/// destructive failure available to this design and it costs nothing to remove: write
/// <c>current.tmp</c>, then rename it over the original. If the temp write fails there is nothing to
/// clean up and <c>current</c> was never touched.</para>
/// </summary>
public static class AtomicFile
{
    /// <summary>
    /// Renames <paramref name="source"/> over <paramref name="destination"/>, replacing it.
    ///
    /// <para><c>File.Move(…, overwrite: true)</c> is <c>MoveFileEx</c> with
    /// <c>MOVEFILE_REPLACE_EXISTING</c> on Windows and <c>rename(2)</c> everywhere else — which is
    /// exactly the primitive wanted, and is atomic within one filesystem.</para>
    /// </summary>
    public static void ReplaceOrMove(string source, string destination)
        => File.Move(source, destination, overwrite: true);

    /// <summary>Writes text so that <paramref name="path"/> is either its old content or its new one, never neither.</summary>
    public static void WriteAllTextAtomic(string path, string contents)
    {
        string tmp = path + ".tmp";
        try
        {
            File.WriteAllText(tmp, contents);
            ReplaceOrMove(tmp, path);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }
            throw;
        }
    }

    /// <summary>
    /// Re-points a symlink atomically: create it under a temp name, then rename it over the original.
    /// The Linux <c>current</c> pointer is a symlink, and the naive form — delete then re-create —
    /// has a window in which the application has no launch path at all.
    /// </summary>
    public static void WriteSymlinkAtomic(string linkPath, string target)
    {
        string tmp = linkPath + ".tmp";
        try
        {
            if (ExistsIncludingLink(tmp)) DeleteLinkOrFile(tmp);

            File.CreateSymbolicLink(tmp, target);

            // rename(2), NOT File.Move: File.Move gates on File.Exists(source), which answers FALSE
            // for a symlink whose target is a DIRECTORY — which is exactly what `current` is. It
            // throws FileNotFoundException naming a file that is plainly there. Measured, not
            // assumed. rename(2) is the primitive the design names for this case anyway.
            if (!NativeFileOps.TryRename(tmp, linkPath))
                ReplaceOrMove(tmp, linkPath);
        }
        catch
        {
            try { DeleteLinkOrFile(tmp); } catch { /* best effort */ }
            throw;
        }
    }

    /// <summary>
    /// True when <paramref name="path"/> is a symbolic link. <see cref="File.ResolveLinkTarget"/>
    /// THROWS <see cref="FileNotFoundException"/> for a path that does not exist at all, rather than
    /// returning null, so every caller needs this guard and none of them should have to remember it.
    /// </summary>
    public static bool IsSymlink(string path)
    {
        try   { return File.ResolveLinkTarget(path, returnFinalTarget: false) is not null; }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException or IOException)
        {
            return false;
        }
    }

    /// <summary>True when something is at <paramref name="path"/> — file, directory or dangling link.</summary>
    public static bool ExistsIncludingLink(string path)
        => File.Exists(path) || Directory.Exists(path) || IsSymlink(path);

    private static void DeleteLinkOrFile(string path)
    {
        // A symlink to a directory answers Directory.Exists, and File.Delete removes the LINK rather
        // than following it — which is what is wanted here in both cases.
        if (File.Exists(path) || IsSymlink(path)) File.Delete(path);
        else if (Directory.Exists(path)) Directory.Delete(path, recursive: false);
    }

    /// <summary>
    /// Exchanges two directories in one operation, or reports that it had to fall back.
    ///
    /// <para>macOS gets <c>renamex_np(…, RENAME_SWAP)</c>, which is a true atomic exchange and
    /// allocates nothing — so it is immune to a full disk. <b><c>File.Move</c> will not atomically
    /// swap two directories</b>, so there is no managed equivalent and this is a small P/Invoke.</para>
    ///
    /// <para>Everywhere else the fallback is three renames with a sub-millisecond window in which
    /// nothing is at the path. Acceptable, but it is a fallback, and <paramref name="wasAtomic"/> says
    /// which happened so the completion note can record it rather than guess.</para>
    /// </summary>
    /// <summary>
    /// What the fallback names the displaced original while the exchange is in flight, and the only
    /// thing that identifies one afterwards. <see cref="SwapAsidesOf"/> is the reader.
    /// </summary>
    public const string SwapAsideMarker = ".swapaside-";

    /// <summary>Hex characters of the id that follows <see cref="SwapAsideMarker"/>.</summary>
    private const int IdLength = 8;

    /// <summary>
    /// Every <c>&lt;original&gt;.swapaside-&lt;id&gt;</c> lying beside <paramref name="original"/>,
    /// newest first — the debris a process killed BETWEEN the fallback's renames leaves behind.
    ///
    /// <para>It is listed rather than deleted here because the primitive has no standing to decide
    /// what an interrupted exchange meant; only the caller that knows the install site does. What
    /// this guarantees is that a match cannot be anything else: the name is the original's own file
    /// name, plus a fixed marker, plus exactly <see cref="IdLength"/> hex digits.</para>
    /// </summary>
    public static IReadOnlyList<string> SwapAsidesOf(string original)
    {
        var found = new List<string>();
        try
        {
            string full     = Path.TrimEndingDirectorySeparator(Path.GetFullPath(original));
            string? parent  = Path.GetDirectoryName(full);
            string name     = Path.GetFileName(full);
            if (parent is null || name.Length == 0 || !Directory.Exists(parent)) return found;

            string prefix = name + SwapAsideMarker;
            foreach (string d in Directory.EnumerateDirectories(parent))
            {
                string n = Path.GetFileName(d);
                if (n.Length != prefix.Length + IdLength) continue;
                if (!n.StartsWith(prefix, StringComparison.Ordinal)) continue;

                bool hex = true;
                for (int i = prefix.Length; i < n.Length; i++)
                    if (!Uri.IsHexDigit(n[i])) { hex = false; break; }

                if (hex) found.Add(d);
            }

            // Newest first: with more than one, the most recent is the exchange that was interrupted.
            found.Sort((x, y) => Directory.GetLastWriteTimeUtc(y).CompareTo(Directory.GetLastWriteTimeUtc(x)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }

        return found;
    }

    public static void SwapDirectories(string a, string b, out bool wasAtomic)
    {
        if (OperatingSystem.IsMacOS() && NativeFileOps.TrySwap(a, b))
        {
            wasAtomic = true;
            return;
        }

        wasAtomic = false;

        string aside = a + SwapAsideMarker + Guid.NewGuid().ToString("N")[..IdLength];
        Directory.Move(a, aside);
        try
        {
            Directory.Move(b, a);
        }
        catch
        {
            // Put it back rather than leaving the launch path empty — the one outcome worth any
            // amount of trouble to avoid.
            try { Directory.Move(aside, a); } catch { /* nothing further we can do */ }
            throw;
        }
        Directory.Move(aside, b);
    }
}

/// <summary>The one place a platform call lives — a primitive that moves bytes, never a policy.</summary>
internal static class NativeFileOps
{
    private const int RENAME_SWAP = 0x0002;

    // DllImport rather than the newer LibraryImport source generator: that one requires
    // AllowUnsafeBlocks, and turning unsafe code on for the whole UI project to reach one libc
    // entry point is a much larger change than this call is worth.
#pragma warning disable SYSLIB1054
    [DllImport("libc", EntryPoint = "renamex_np", SetLastError = true)]
    private static extern int RenameX(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string from,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string to,
        uint flags);
#pragma warning restore SYSLIB1054

#pragma warning disable SYSLIB1054
    [DllImport("libc", EntryPoint = "rename", SetLastError = true)]
    private static extern int Rename(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string from,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string to);
#pragma warning restore SYSLIB1054

    /// <summary>
    /// True when the two paths were exchanged atomically. False means "this platform or filesystem
    /// cannot", not "the swap failed" — the caller falls back rather than giving up.
    /// </summary>
    internal static bool TrySwap(string a, string b)
    {
        try   { return RenameX(a, b, RENAME_SWAP) == 0; }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    /// <summary>
    /// <c>rename(2)</c> — atomic within one filesystem, and unlike <see cref="File.Move"/> it renames
    /// a SYMLINK rather than refusing one whose target is a directory. False on any platform without
    /// libc, so the caller falls back.
    /// </summary>
    internal static bool TryRename(string from, string to)
    {
        if (OperatingSystem.IsWindows()) return false;
        try   { return Rename(from, to) == 0; }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }
}

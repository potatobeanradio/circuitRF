using System;
using System.IO;

namespace CircuitRF.Ui.Updates;

/// <summary>How free space is measured. Faked in tests; there is one shipping implementation.</summary>
public interface IFreeSpaceProbe
{
    /// <summary>Bytes available on the volume holding <paramref name="path"/>, or 0 if unknown.</summary>
    long AvailableFreeSpace(string path);
}

/// <summary>
/// <see cref="DriveInfo.AvailableFreeSpace"/> — the raw <c>statfs</c> figure, deliberately.
///
/// <para><b>Do not "fix" this to use macOS's <c>volumeAvailableCapacityForImportantUsageKey</c>.</b>
/// On APFS the raw number can be dramatically lower than what the volume could provide, because
/// local snapshots and other evictable content count as used: a Mac whose Finder window says 20 GB
/// available can report ~2 GB through <c>statfs</c>. Apple exposes the optimistic figure separately,
/// and someone will eventually notice circuitRF declining to update on a Mac that looks empty and
/// reach for it.</para>
///
/// <para><b>That would be a regression.</b> The two errors are not symmetric: over-caution costs one
/// skipped update, which nobody notices and the next check may well fix; over-optimism starts a
/// ~500 MB write against space that only exists if the OS cooperates promptly, on a volume that is
/// already nearly full, while the user has unsaved work open. This is recorded as a decision so it
/// does not have to be rediscovered.</para>
/// </summary>
/// <remarks>
/// <para><b>The volume is found by longest mount-point match, not by <c>Path.GetPathRoot</c></b>
/// (corrected in review, 2026-08-25). On Unix <c>GetPathRoot</c> answers <c>"/"</c> for every path
/// there is, so the probe reported the ROOT filesystem no matter which volume was about to be
/// written — and a separate <c>/home</c> is an ordinary Linux install. The whole argument this class
/// exists to make, that the updater must never be the reason a user cannot save their work, was
/// being made about a different disk.</para>
/// </remarks>
public sealed class DriveFreeSpaceProbe : IFreeSpaceProbe
{
    public long AvailableFreeSpace(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);

            DriveInfo? best = null;
            int bestLength = -1;

            foreach (DriveInfo d in DriveInfo.GetDrives())
            {
                string mount;
                try
                {
                    if (!d.IsReady) continue;
                    mount = Path.TrimEndingDirectorySeparator(d.RootDirectory.FullName);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }

                if (mount.Length == 0) mount = Path.DirectorySeparatorChar.ToString();
                if (!IsUnderMount(full, mount) || mount.Length <= bestLength) continue;

                best = d;
                bestLength = mount.Length;
            }

            if (best is not null) return best.AvailableFreeSpace;

            // Nothing matched — a network path on Windows, or an enumeration that told us nothing.
            // Fall back to the containing root rather than reporting a volume that is not there.
            string? root = Path.GetPathRoot(full);
            return string.IsNullOrEmpty(root) ? 0 : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>
    /// True when <paramref name="path"/> lies under <paramref name="mount"/>. Compared a component at
    /// a time, so <c>/homework</c> is not read as being under <c>/home</c>.
    /// </summary>
    private static bool IsUnderMount(string path, string mount)
    {
        StringComparison cmp = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (string.Equals(path, mount, cmp)) return true;

        string prefix = mount.EndsWith(Path.DirectorySeparatorChar) ? mount : mount + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, cmp);
    }
}

/// <summary>
/// The disk-space arithmetic, as a pure function, because the naive form is wrong by roughly 3×.
///
/// <para><b>The rule this exists to guarantee: the updater must never be the reason a user cannot
/// save their work.</b> A user with a workspace open and 400 MB free must not have circuitRF quietly
/// consume 495 MB in the background and then fail to write a <c>.cws</c>. Nobody would ever connect
/// the two, and the thing lost is the user's afternoon.</para>
/// </summary>
public static class UpdateSpace
{
    /// <summary>
    /// 1 GB of headroom, on top of the payload, reserved for the <b>user's</b> work — a workspace
    /// save, an EM result set, the recovery snapshots, the crash reports, and the few percent of free
    /// space macOS and Windows both want in order to behave.
    ///
    /// <para>Deliberately generous. On an Apple Silicon Mac it means circuitRF declines to update
    /// below roughly 1.5 GB free; that is the intended behaviour, not a number to tune away.</para>
    /// </summary>
    public const long DefaultReserveBytes = 1L << 30;

    /// <summary>
    /// What must be free before a download starts: <c>download + expanded + reserve</c>.
    ///
    /// <para><b>Why both payload terms.</b> At peak the compressed download and its expanded copy
    /// exist at the same time — the download can only be deleted once unpacking has succeeded. A
    /// check that knows only the download size is wrong by about a factor of three (160 MB against
    /// the 495 MB an arm64 macOS update actually needs).</para>
    ///
    /// <para>The check is made against peak even though the requirement drops to the transient figure
    /// partway through, because a check that has to be right about <i>when</i> it is measured is a
    /// check that will eventually be measured at the wrong moment.</para>
    /// </summary>
    public static long RequiredFreeSpace(long downloadBytes, long expandedBytes,
                                         long reserveBytes = DefaultReserveBytes)
    {
        if (downloadBytes < 0) throw new ArgumentOutOfRangeException(nameof(downloadBytes));
        if (expandedBytes < 0) throw new ArgumentOutOfRangeException(nameof(expandedBytes));
        if (reserveBytes  < 0) throw new ArgumentOutOfRangeException(nameof(reserveBytes));

        return downloadBytes + expandedBytes + reserveBytes;
    }

    /// <summary>
    /// What stays consumed after the swap: the retained previous version, released as soon as the new
    /// one clears its startup counter. A one-generation cost, never an accumulating one — steady
    /// state is zero.
    /// </summary>
    public static long TransientAfterSwap(long expandedBytes) => expandedBytes;

    /// <summary>
    /// How large the unpacked payload will be, estimated from the compressed size when the feed does
    /// not say. The multipliers are measured from <c>dist/</c>: an arm64 <c>.dmg</c> of 160 MB expands
    /// to ~335 MB (2.09×), the Windows and Linux archives to ~2.4×. Rounded UP to 3× so the estimate
    /// errs the safe way, per this file's own asymmetry argument.
    /// </summary>
    public static long EstimateExpandedBytes(long downloadBytes) => checked(downloadBytes * 3);

    /// <summary>Human-readable, for the two places design §13.5 lets a space problem be visible.</summary>
    public static string FormatBytes(long bytes)
    {
        if (bytes >= 1L << 30) return $"{bytes / (double)(1L << 30):0.#} GB";
        if (bytes >= 1L << 20) return $"{bytes / (double)(1L << 20):0} MB";
        if (bytes >= 1L << 10) return $"{bytes / (double)(1L << 10):0} KB";
        return $"{bytes} bytes";
    }
}

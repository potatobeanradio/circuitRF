using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// Moves a file or directory to the OS Trash/Recycle Bin. Cross-platform helper; never
/// hard-deletes — if the OS path fails, returns false so the caller can report the error.
/// </summary>
public static class SystemTrash
{
    /// <summary>
    /// Moves <paramref name="path"/> (file or directory) to the OS Trash/Recycle Bin.
    /// Returns true on success. On failure, returns false and sets <paramref name="error"/>
    /// to a human-readable message. Never hard-deletes: failure leaves the original untouched.
    /// </summary>
    public static bool TryMoveToTrash(string path, out string? error)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return MoveToTrashMacOS(path, out error);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return MoveToTrashWindows(path, out error);

            return MoveToTrashLinux(path, out error);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    // ── macOS: osascript → Finder's "delete" (moves to Trash, works for files and dirs) ──

    private static bool MoveToTrashMacOS(string path, out string? error)
    {
        // Escape the path for an AppleScript string: backslash then double-quote.
        var escaped = path.Replace("\\", "\\\\").Replace("\"", "\\\"");
        var script  = $"tell application \"Finder\" to delete POSIX file \"{escaped}\"";

        var psi = new ProcessStartInfo("osascript", new[] { "-e", script })
        {
            UseShellExecute       = false,
            RedirectStandardError = true,
        };
        using var proc = Process.Start(psi);
        if (proc is null) { error = "Failed to start osascript."; return false; }

        // Read stderr before WaitForExit to avoid deadlock on full pipe buffer.
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        if (proc.ExitCode == 0) { error = null; return true; }
        error = string.IsNullOrWhiteSpace(stderr)
            ? $"osascript exited with code {proc.ExitCode}."
            : stderr.Trim();
        return false;
    }

    // ── Windows: Shell32 SHFileOperation with FOF_ALLOWUNDO → Recycle Bin ───────────────
    // Guard: only reached when RuntimeInformation.IsOSPlatform(OSPlatform.Windows) is true.
    // DllImport compiles cross-platform; the method is never called on non-Windows.

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref ShFileOpStruct fileOp);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr hwnd;
        public int    wFunc;
        public string pFrom;
        public string? pTo;
        public short  fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    private const int FoDelete      = 0x0003;
    private const int FofAllowUndo  = 0x0040; // send to Recycle Bin
    private const int FofNoConfirm  = 0x0010; // no "Are you sure?" dialog
    private const int FofSilent     = 0x0004; // no progress dialog

    private static bool MoveToTrashWindows(string path, out string? error)
    {
        // SHFileOperation requires the path to be double-null-terminated.
        var fileOp = new ShFileOpStruct
        {
            wFunc  = FoDelete,
            pFrom  = path + "\0\0",
            fFlags = (short)(FofAllowUndo | FofNoConfirm | FofSilent),
        };
        int result = SHFileOperation(ref fileOp);
        if (result == 0 && !fileOp.fAnyOperationsAborted)
        {
            error = null;
            return true;
        }
        error = fileOp.fAnyOperationsAborted
            ? "Operation was cancelled."
            : $"SHFileOperation failed with code {result}.";
        return false;
    }

    // ── Linux: gio trash (GNOME VFS; KDE / other DEs may need trash-put / kioclient) ────
    // NOTE: if gio is absent (not a GNOME system), this returns false with a clear message.
    // trash-put (trash-cli) and kioclient (KDE) are not attempted — follow-up work if needed.

    private static bool MoveToTrashLinux(string path, out string? error)
    {
        var psi = new ProcessStartInfo("gio", new[] { "trash", path })
        {
            UseShellExecute       = false,
            RedirectStandardError = true,
        };
        using var proc = Process.Start(psi);
        if (proc is null)
        {
            error = "Failed to start 'gio'. On Linux, 'gio' (GNOME) is required for Trash support. " +
                    "Other desktop environments may need 'trash-put' (trash-cli) or 'kioclient' (KDE).";
            return false;
        }

        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        if (proc.ExitCode == 0) { error = null; return true; }
        error = string.IsNullOrWhiteSpace(stderr)
            ? $"gio trash exited with code {proc.ExitCode}. " +
              "'gio' may not be installed or the filesystem may not support Trash."
            : stderr.Trim();
        return false;
    }
}

using System;
using System.Diagnostics;
using System.IO;

namespace CircuitRF.Ui;

/// <summary>
/// "Reveal in Finder / Explorer / File Manager" — the platform detection and the per-platform
/// argument forms, stated once.
///
/// The argument forms are not interchangeable and getting one subtly wrong is a security bug, not
/// a cosmetic one: see RESOLVED.md §4 (2026-08-25). <b>ArgumentList, never the single-string
/// overload</b> — on Unix .NET parses that string into <c>argv</c> itself, honouring quotes, so a
/// file whose NAME contains a double quote closes ours and everything after it becomes further
/// arguments to <c>open</c>, which takes <c>-a &lt;application&gt;</c>. Paths reach here from
/// whatever a workspace or an imported kit put on disk.
/// </summary>
public static class FileReveal
{
    /// <summary>Platform-correct label for a "Reveal in …" menu item.</summary>
    public static string Label =>
        OperatingSystem.IsMacOS()     ? "Reveal in Finder"
        : OperatingSystem.IsWindows() ? "Reveal in Explorer"
        : "Reveal in File Manager";

    /// <summary>
    /// The nearest folder at or above <paramref name="path"/> that still exists on disk, or null if
    /// none does (a whole volume gone, or a path with no existing ancestor at all).
    ///
    /// <para>This is what a BROKEN reference reveals. A Known File whose target has been moved or
    /// deleted is precisely when the user reaches for "Reveal" — to go and look — and revealing
    /// nothing answers nothing. <c>/myfiles/folder1/test.txt</c> missing while
    /// <c>/myfiles/folder1/</c> is still there opens <c>folder1</c>, which is where the file was and
    /// where its replacement most likely is.</para>
    ///
    /// <para>A path that exists is returned as its own answer only when it is a DIRECTORY; an
    /// existing file returns its parent, matching what the reveal itself does with one.</para>
    /// </summary>
    public static string? NearestExistingDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        string? candidate;
        try   { candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch { return null; }

        if (Directory.Exists(candidate)) return candidate;

        // Walk up. GetDirectoryName returns null at a root and returns the SAME string for a path
        // that cannot go further, so both terminate the loop — an unrooted or malformed path must
        // not spin here.
        while (true)
        {
            var parent = Path.GetDirectoryName(candidate);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, candidate, StringComparison.Ordinal))
                return null;
            if (Directory.Exists(parent)) return parent;
            candidate = parent;
        }
    }

    /// <summary>
    /// Shows <paramref name="path"/> in the platform's file manager. A file is selected in its
    /// containing folder; a directory is opened. A path that is no longer there is a no-op —
    /// callers that owe the user an answer ("it is not there any more") check first and say so.
    /// </summary>
    /// <param name="onError">
    /// Reports a launch failure. Null means swallow it: a file manager that will not start is not
    /// worth an error banner on every surface that offers Reveal.
    /// </param>
    public static void Reveal(string? path, Action<Exception>? onError = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        bool isDir  = Directory.Exists(path);
        bool isFile = !isDir && File.Exists(path);
        if (!isDir && !isFile) return;

        try
        {
            if (OperatingSystem.IsMacOS())
            {
                // -R selects a file; bare open opens a directory.
                var psi = new ProcessStartInfo("open") { UseShellExecute = false };
                if (isFile) psi.ArgumentList.Add("-R");
                psi.ArgumentList.Add(path);
                Process.Start(psi);
            }
            else if (OperatingSystem.IsWindows())
            {
                // /select highlights a file; bare path opens a directory. Explorer wants
                // `/select,<path>` as ONE argument, which is why this is not two.
                var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
                psi.ArgumentList.Add(isFile ? $"/select,{path}" : path);
                Process.Start(psi);
            }
            else
            {
                // Linux: xdg-open on the directory (it does not highlight), or on the containing
                // directory for a file.
                var target = isDir ? path : Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(target))
                {
                    var psi = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
                    psi.ArgumentList.Add(target);
                    Process.Start(psi);
                }
            }
        }
        catch (Exception ex)
        {
            onError?.Invoke(ex);
        }
    }
}

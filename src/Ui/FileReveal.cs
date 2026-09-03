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
            var psi = BuildCommand(path, isFile,
                OperatingSystem.IsMacOS()     ? Platform.MacOS
                : OperatingSystem.IsWindows() ? Platform.Windows
                : Platform.Other);
            if (psi is not null) Process.Start(psi);
        }
        catch (Exception ex)
        {
            onError?.Invoke(ex);
        }
    }

    /// <summary>Which argument form to build. A parameter rather than a query so it can be tested.</summary>
    internal enum Platform { MacOS, Windows, Other }

    /// <summary>
    /// The launch for one path on one platform, or null when there is nothing to launch.
    ///
    /// <para><b>Windows selects a file through <c>Arguments</c>, not <c>ArgumentList</c>, and that is
    /// not a relapse of the security fix above.</b> Explorer's command-line parser is not the standard
    /// one: it needs <c>/select,"&lt;path&gt;"</c> — the switch bare, the path quoted. <c>ArgumentList</c>
    /// cannot express that. .NET quotes an argument as a whole when it contains a space, so
    /// <c>/select,C:\Users\First Last\x.log</c> is handed to Explorer as
    /// <c>"/select,C:\Users\First Last\x.log"</c> — Explorer does not recognise a quoted switch,
    /// treats the whole thing as a path, fails, and silently opens its DEFAULT folder instead. That is
    /// the "Reveal opened the wrong directory" report (Windows, 2026-09-03); it cannot happen on a
    /// path without spaces, which is why it looked intermittent, and never on macOS, where
    /// <c>open -R</c> takes an ordinary argv.</para>
    ///
    /// <para>Building that string cannot be broken out of, because <b>a double quote is a RESERVED
    /// character in a Windows path</b> (<c>&lt; &gt; : " / \ | ? *</c>) — there is no path that can
    /// close ours. The 2026-08-25 finding is a UNIX one: there <c>"</c> is a legal filename character
    /// and .NET parses a single argument string into argv itself, so those branches keep
    /// <c>ArgumentList</c>. The rule is per-platform, and this is the whole of it.</para>
    ///
    /// <para>Only the <c>/select,</c> form needs it. A Windows DIRECTORY is passed as an ordinary
    /// argument, where normal quoting is both correct and safe — which also keeps the raw-string form
    /// away from a trailing separator, whose backslash would escape the closing quote.</para>
    /// </summary>
    internal static ProcessStartInfo? BuildCommand(string path, bool isFile, Platform platform)
    {
        switch (platform)
        {
            case Platform.MacOS:
            {
                // -R selects a file; bare open opens a directory.
                var psi = new ProcessStartInfo("open") { UseShellExecute = false };
                if (isFile) psi.ArgumentList.Add("-R");
                psi.ArgumentList.Add(path);
                return psi;
            }

            case Platform.Windows:
            {
                var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
                if (isFile) psi.Arguments = $"/select,\"{path}\"";
                else        psi.ArgumentList.Add(path);
                return psi;
            }

            default:
            {
                // Linux: xdg-open on the directory (it does not highlight), or on the containing
                // directory for a file.
                var target = isFile ? Path.GetDirectoryName(path) : path;
                if (string.IsNullOrEmpty(target)) return null;
                var psi = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
                psi.ArgumentList.Add(target);
                return psi;
            }
        }
    }
}

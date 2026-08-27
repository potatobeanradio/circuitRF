namespace CircuitRF.Diagnostics;

/// <summary>
/// Turns a file-access failure into a diagnostic that names the actual cause.
///
/// <para><b>Why this exists.</b> .NET reports an operating-system privacy block and a genuine
/// permissions problem with the same sentence: <c>Access to the path '…' is denied.</c> On macOS
/// those are very different things with very different fixes, and the message points at the wrong
/// one — it sends the reader to check file permissions that are, in the privacy case, perfectly
/// normal (<c>-rw-r--r--</c>, owned by them, readable by every other program they own).</para>
///
/// <para><b>The case that motivated it</b> (owner report, 2026-08-27): every workspace under
/// <c>~/Documents</c> failed to open or save with "Access to the path … is denied", while the same
/// operation on <c>~/Desktop</c> worked. macOS gates <c>~/Documents</c>, <c>~/Desktop</c>,
/// <c>~/Downloads</c> and a few others behind per-folder privacy grants (TCC), evaluated per
/// application and cached for the life of a process — so a grant that changes takes effect on the
/// next launch, which is why this appears to strike out of nowhere after a restart.</para>
///
/// <para><b>The part that is genuinely hard to work out</b>, and therefore the part most worth
/// saying: for a build launched from a terminal, the grant macOS checks belongs to the TERMINAL, not
/// to circuitRF. Looking for "circuitRF" in the privacy list finds nothing, and the natural
/// conclusion — that the setting is missing or irrelevant — is wrong.</para>
///
/// <para>Lives in the diagnostics leaf rather than next to any one caller because every project that
/// opens a file can hit this: workspaces, layouts, technologies, Touchstone, every export.</para>
/// </summary>
public static class FileAccessDiagnostics
{
    /// <summary>
    /// A diagnostic describing <paramref name="exception"/>, or <c>null</c> when it is not a file
    /// access failure this can improve on — in which case the caller should report as it always has.
    /// Returning null rather than a vague diagnostic is deliberate: a wrong explanation is worse
    /// than the raw message.
    /// </summary>
    public static Diagnostic? TryDescribe(string path, Exception exception)
    {
        if (exception is not UnauthorizedAccessException) return null;

        if (OperatingSystem.IsMacOS() && TryGetProtectedFolder(path) is var (plain, toggle) && plain is not null)
            return IsLaunchedFromAppBundle()
                ? BundledAppRefusal(path, plain, toggle!)
                : TerminalLaunchRefusal(path, plain, toggle!);

        return Diagnostic.Create(
            "file.access.denied",
            DiagnosticSeverity.Error,
            "Access to '{path}' was denied. Check that the file is not read-only, not owned by " +
            "another user, and not open in another program.",
            ("path", path));
    }

    /// <summary>
    /// The shipped-app case. The likely history is that macOS asked and the answer was no — or that
    /// it never asked — so this leads with the toggle and then covers the not-listed case, which is
    /// the one where the obvious instruction is impossible to follow.
    ///
    /// <para>Short sentences on purpose. This is read by someone whose work has just failed to open,
    /// and the earlier single-sentence version of it ran to sixty words.</para>
    /// </summary>
    private static Diagnostic BundledAppRefusal(string path, string plain, string toggle) => Diagnostic.Create(
        "file.access.macos-protected-folder",
        DiagnosticSeverity.Error,
        "macOS is blocking circuitRF from opening '{path}'. This is a privacy setting, not a file " +
        "permission — the file itself is fine, and nothing is wrong with your workspace. macOS asks " +
        "before an app may use your {plain} folder, and circuitRF does not currently have that " +
        "permission. To fix it: open System Settings, go to Privacy & Security > Files and Folders, " +
        "find circuitRF in the list, and switch on \"{toggle}\". Then quit circuitRF and open it " +
        "again. If circuitRF is not in that list at all, granting it Full Disk Access under Privacy " +
        "& Security has the same effect.",
        ("path", path), ("plain", plain), ("toggle", toggle));

    /// <summary>
    /// The development case, and the one that wastes the most time: macOS attributes a file request
    /// to the app RESPONSIBLE for launching the process, which for a build started from a shell is
    /// the terminal. Someone looking for "circuitRF" in the privacy list finds nothing and
    /// reasonably concludes the setting does not apply — so this says which app to look for, and
    /// why, before giving the steps.
    /// </summary>
    private static Diagnostic TerminalLaunchRefusal(string path, string plain, string toggle) => Diagnostic.Create(
        "file.access.macos-protected-folder.terminal-launch",
        DiagnosticSeverity.Error,
        "macOS is blocking access to '{path}'. This is a privacy setting, not a file permission — " +
        "the file itself is fine. This copy of circuitRF was started from a terminal, so macOS " +
        "applies the TERMINAL's privacy permissions rather than circuitRF's own. Look for your " +
        "terminal app in the settings, not for circuitRF; circuitRF will not be listed. To fix it: " +
        "open System Settings, go to Privacy & Security > Files and Folders, find your terminal app " +
        "(Terminal or iTerm, for example), and switch on \"{toggle}\". Then quit the terminal and " +
        "start it again — the permission is read when it launches. Running circuitRF as a packaged " +
        "app instead gives it its own permission and avoids this.",
        ("path", path), ("plain", plain), ("toggle", toggle));

    /// <summary>A bundled app lives inside <c>…/Foo.app/Contents/MacOS/</c>; a `dotnet build` host
    /// does not.</summary>
    private static bool IsLaunchedFromAppBundle() =>
        AppContext.BaseDirectory.Replace('\\', '/').Contains(".app/Contents/", StringComparison.Ordinal);

    /// <summary>
    /// The protected folder <paramref name="path"/> sits in, as a pair: a PLAIN name for prose
    /// ("your Documents folder") and the EXACT System Settings row label to switch on
    /// ("Documents Folder"). They differ, and using one where the other belongs is what turns
    /// clear instructions into instructions that do not match what is on screen.
    ///
    /// <para>Matched against the real home directory rather than by substring, so a project that
    /// merely happens to be called "Documents" elsewhere is not mis-described.</para>
    /// </summary>
    private static (string? Plain, string? Toggle) TryGetProtectedFolder(string path)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home.Length == 0) return (null, null);

        string full;
        try { full = Path.GetFullPath(path); }
        catch { return (null, null); }

        // The per-folder TCC services macOS actually ships, with their System Settings labels.
        foreach (var (relative, plain, toggle) in new[]
                 {
                     ("Documents",                "Documents",       "Documents Folder"),
                     ("Desktop",                  "Desktop",         "Desktop Folder"),
                     ("Downloads",                "Downloads",       "Downloads Folder"),
                     ("Pictures",                 "Pictures",        "Photos"),
                     ("Movies",                   "Movies",          "Movies"),
                     ("Music",                    "Music",           "Music"),
                     ("Library/Mobile Documents", "iCloud Drive",    "iCloud Drive"),
                 })
        {
            string root = Path.Combine(home, relative.Replace('/', Path.DirectorySeparatorChar));
            if (IsUnder(full, root)) return (plain, toggle);
        }

        if (IsUnder(full, "/Volumes")) return ("removable or network volume", "Removable Volumes");

        return (null, null);
    }

    /// <summary>Containment by PATH SEGMENT, not by prefix — <c>/a/Documents2</c> is not inside
    /// <c>/a/Documents</c>. Case-insensitive, matching the default macOS filesystem.</summary>
    private static bool IsUnder(string candidate, string root)
    {
        string r = root.TrimEnd(Path.DirectorySeparatorChar);
        return candidate.Length > r.Length
            && candidate.StartsWith(r, StringComparison.OrdinalIgnoreCase)
            && candidate[r.Length] == Path.DirectorySeparatorChar;
    }
}

using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace CircuitRF.Ui.Tests;

// A ⌘, pressed while an EM simulation was running took the whole process down with
// InvalidOperationException("Cannot show window with non-visible owner"): the macOS application
// menu's Settings… item owned its window to `desktop.Windows.FirstOrDefault()`, and the lifetime's
// window list keeps HIDDEN windows in it — only closing one removes it. This application hides two
// kinds of window and never closes them there and then: `_bgMenuWindow` (the 1x1 transparent window
// that holds the macOS menu bar up, hidden the moment a workspace window opens) and a floating tool
// window between its close box and the later dispatcher pass that tears it down. The throw happens
// on the dispatcher, where no call-site try/catch reaches it.
//
// WorkspaceWindow is a real Window subclass and cannot be constructed in this headless suite, so —
// as every prior menu/dialog phase here has done — the invariant is held against the real source.
public class DialogOwnerVisibilityTests
{
    private static string RepoRoot([CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root (no CLAUDE.md found walking up from this test file).");
        return dir!;
    }

    private static string StripComments(string src)
    {
        src = Regex.Replace(src, @"/\*.*?\*/", "", RegexOptions.Singleline);
        return Regex.Replace(src, @"//[^\n]*", "");
    }

    /// <summary>
    /// Nothing may take "the first window" off the lifetime unfiltered — that is the defect verbatim.
    /// Picking one to own a dialog means picking a VISIBLE one.
    /// </summary>
    [Fact]
    public void NoCodeTakesTheFirstWindowOffTheLifetimeUnfiltered()
    {
        var root = RepoRoot();
        var offenders = Directory
            .EnumerateFiles(Path.Combine(root, "src", "Ui"), "*.cs", SearchOption.AllDirectories)
            .Select(f => (File: Path.GetRelativePath(root, f), Text: StripComments(File.ReadAllText(f))))
            .Where(x => Regex.IsMatch(x.Text, @"\.Windows\s*\.\s*(FirstOrDefault\s*\(\s*\)|First\s*\(\s*\)|LastOrDefault\s*\(\s*\)|Last\s*\(\s*\))")
                     || Regex.IsMatch(x.Text, @"\.Windows\s*\[\s*0\s*\]"))
            .Select(x => x.File)
            .ToList();

        Assert.True(offenders.Count == 0,
            "These take a window off the lifetime with no visibility filter; Show/ShowDialog throws on a " +
            "hidden owner and the throw is unreachable by any try/catch: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// The one place that chooses a dialog owner has to apply both filters: visible, and never the
    /// background menu window (which IS visible in the no-workspace state, but is 1x1, transparent,
    /// parked off-screen, and hidden again the instant a workspace window appears).
    /// </summary>
    [Fact]
    public void OwnerWindowForDialogRequiresVisibleAndExcludesTheBackgroundMenuWindow()
    {
        var src = StripComments(File.ReadAllText(Path.Combine(RepoRoot(), "src", "Ui", "App.axaml.cs")));

        var body = Regex.Match(src, @"private Window\? OwnerWindowForDialog\(\)\s*\{(.*?)\n    \}", RegexOptions.Singleline);
        Assert.True(body.Success, "App.OwnerWindowForDialog() is gone — every dialog owner in this application is chosen there.");

        Assert.Contains("IsVisible", body.Groups[1].Value);
        Assert.Contains("_bgMenuWindow", body.Groups[1].Value);
    }

    /// <summary>The two callers that had no window of their own to start from go through it.</summary>
    [Fact]
    public void TheAppMenuSettingsItemAndThePCellTrustPromptBothUseIt()
    {
        var root = RepoRoot();

        var app = StripComments(File.ReadAllText(Path.Combine(root, "src", "Ui", "App.axaml.cs")));
        var settings = Regex.Match(app, @"settingsItem\.Click \+=(.*?)\n        \};", RegexOptions.Singleline);
        Assert.True(settings.Success, "The application menu's Settings… handler could not be found.");
        Assert.Contains("OwnerWindowForDialog()", settings.Groups[1].Value);

        var trust = StripComments(File.ReadAllText(
            Path.Combine(root, "src", "Ui", "Views", "Dialogs", "PCellTrustDialog.axaml.cs")));
        Assert.Contains("App.DialogOwner()", trust);
    }
}

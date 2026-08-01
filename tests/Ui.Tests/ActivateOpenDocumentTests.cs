using System;
using System.Linq;
using System.Runtime.CompilerServices;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Double-clicking an already-open document in the project tree must bring its WINDOW forward, not
/// just select its tab — reported for a torn-off Data Display, where the double-click looked like it
/// did nothing at all.
///
/// <para><b>The actual defect was duplication, not the Data Display.</b> Four places had their own
/// copy of "activate an already-open document"; only <c>ActivateIfOpen</c> was ever given the window
/// half, and the <c>.cdd</c> paths did not use it. So the same gesture worked for a schematic and
/// silently failed for a data display. These tests pin the convergence, because a fifth copy is
/// exactly how it comes back.</para>
///
/// <para><see cref="CircuitRF.Ui.ViewModels.WorkspaceViewModel"/> cannot be constructed headlessly
/// (its constructor builds the Dock layout and posts to the Dispatcher), so this is a source scan —
/// the same fallback this codebase already uses for every other WorkspaceViewModel-only rule.</para>
/// </summary>
public class ActivateOpenDocumentTests
{
    private static string ReadRepoFile(string relativePath, [CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root.");
        return File.ReadAllText(Path.Combine(dir!, relativePath));
    }

    private static string[] Source() =>
        ReadRepoFile("src/Ui/ViewModels/WorkspaceViewModel.cs").Split('\n');

    /// <summary>
    /// The headline rule. Every place that finds an already-open document and shows it must route
    /// through the one helper; a bare SetActiveDockable there selects the tab and leaves a torn-off
    /// window wherever it was.
    /// </summary>
    [Fact]
    public void EveryOpenDocumentLookupThatActivates_RoutesThroughTheOneHelper()
    {
        var lines = Source();
        int checkedSites = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains("_openDocsByPath.TryGetValue", StringComparison.Ordinal)) continue;

            // The block that acts on the lookup, plus the comment lines immediately above it — a
            // site's stated reason for opting out is written before the code, not inside it.
            int from = Math.Max(0, i - 8);
            string block = string.Join('\n', lines.Skip(from).Take(i - from + 10));
            if (!block.Contains("SetActiveDockable", StringComparison.Ordinal)
                && !block.Contains("ActivateOpenDocument", StringComparison.Ordinal)) continue;

            checkedSites++;

            // One site deliberately selects the tab only, and says so in its own words. Keyed on
            // that stated reason rather than a line number, so the exemption cannot drift onto a
            // different site as the file changes.
            if (block.Contains("Tab selection ONLY", StringComparison.Ordinal)) continue;

            Assert.True(block.Contains("ActivateOpenDocument", StringComparison.Ordinal),
                $"WorkspaceViewModel.cs line {i + 1}: this activates an already-open document with a " +
                "bare SetActiveDockable. That selects the tab and leaves a torn-off window behind the " +
                "shell — the reported Data Display bug. Call ActivateOpenDocument instead.");
        }

        Assert.True(checkedSites >= 4,
            $"expected at least 4 open-document activation sites, found {checkedSites} — the scan is " +
            "no longer finding them, so it is not guarding anything.");
    }

    /// <summary>
    /// The helper does BOTH halves. Selecting the tab without raising the window is the original bug;
    /// raising without selecting would show the right window on the wrong tab.
    /// </summary>
    [Fact]
    public void TheHelper_SelectsTheTabAndRaisesTheWindow()
    {
        var lines = Source();
        int start = Array.FindIndex(lines, l => l.Contains("private void ActivateOpenDocument(", StringComparison.Ordinal));
        Assert.True(start >= 0, "ActivateOpenDocument is gone — the four copies have come back.");

        string body = string.Join('\n', lines.Skip(start).Take(6));
        Assert.Contains("SetActiveDockable", body, StringComparison.Ordinal);
        Assert.Contains("BringDockableWindowToFront", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// A floating dock carries its own IDockWindow; the shell's root does not (it is hosted by a
    /// DockControl inside WorkspaceWindow). Resolving the floating host is the entire fix — without
    /// it every document falls back to the shell and a torn-off window is never touched.
    /// </summary>
    [Fact]
    public void RaisingResolvesTheFloatingHostWindow_BeforeFallingBackToTheShell()
    {
        var lines = Source();
        int start = Array.FindIndex(lines, l => l.Contains("private void BringDockableWindowToFront(", StringComparison.Ordinal));
        Assert.True(start >= 0, "BringDockableWindowToFront is gone.");

        string body = string.Join('\n', lines.Skip(start).Take(30));

        Assert.Contains("FindRoot", body, StringComparison.Ordinal);
        Assert.Contains("Window.Host", body, StringComparison.Ordinal);
        Assert.Contains("Activate()", body, StringComparison.Ordinal);

        // The editor still has to take the keyboard, or the user lands on a raised window whose
        // canvas ignores their first keystroke.
        Assert.Contains("RequestActivationFocus", body, StringComparison.Ordinal);

        // A window built but never presented (headless) has no PlatformImpl; touching it throws.
        Assert.Contains("PlatformImpl", body, StringComparison.Ordinal);
    }
}

// ================================================================
//  ToolActivationFocusTests.cs — owner, 2026-08-25:
//
//    "if I click on the title bar of a window (like Project, or Library) or when it generally gets
//     focus, I cannot use <page up>, <page down> etc. keystrokes. I am forced to click somewhere
//     inside the window before the keystrokes will register."
//
//  Clicking a tool's TAB leaves keyboard focus on the tab — Dock's chrome, which lives OUTSIDE the
//  panel's own view — so a key event's route never passes through the view and its tunnel handler is
//  never called. Document tabs have solved this since IActivatableDocument ("without a preliminary
//  click on the canvas"); tool panels were simply never given the same treatment.
// ================================================================

using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using CircuitRF.Ui.ViewModels.Dock;
using Xunit;

namespace CircuitRF.Ui.Tests;

public class ToolActivationFocusTests
{
    private static string ReadRepoFile(string relativePath, [CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root.");
        return File.ReadAllText(Path.Combine(dir!, relativePath));
    }

    // ── Both panels participate ───────────────────────────────────────────────

    [Fact]
    public void BothScrollablePanels_AreActivatableTools()
    {
        Assert.IsAssignableFrom<IActivatableTool>(new ProjectTreeTool());
        Assert.IsAssignableFrom<IActivatableTool>(new PaletteTool());
    }

    // ── Signal 1: the tab was chosen ──────────────────────────────────────────

    [Fact]
    public void SelectingTheTab_RequestsFocus()
    {
        var tool = new ProjectTreeTool();
        int requests = 0;
        tool.ActivationFocusRequested += () => requests++;

        tool.OnSelected();

        Assert.Equal(1, requests);
    }

    [Fact]
    public void SelectingThePaletteTab_RequestsFocus()
    {
        var tool = new PaletteTool();
        int requests = 0;
        tool.ActivationFocusRequested += () => requests++;

        tool.OnSelected();

        Assert.Equal(1, requests);
    }

    // ── Signal 2: the panel became active without a tab change ────────────────
    //
    //  Genuinely a different event, not belt-and-braces: focus moving into a pinned or floating panel
    //  makes it active with no tab selected, and a tab can be re-selected in a dock that was already
    //  active. Each signal misses a case the other catches.

    [Fact]
    public void BecomingActive_RequestsFocus()
    {
        var tool = new ProjectTreeTool();
        int requests = 0;
        tool.ActivationFocusRequested += () => requests++;

        tool.IsActive = true;

        Assert.Equal(1, requests);
    }

    [Fact]
    public void BecomingINactive_DoesNotRequestFocus()
    {
        var tool = new ProjectTreeTool();
        tool.IsActive = true;

        int requests = 0;
        tool.ActivationFocusRequested += () => requests++;

        tool.IsActive = false;

        Assert.Equal(0, requests);
    }

    // ── The pending half: activated before the view exists ────────────────────

    [Fact]
    public void ARequestMadeBeforeAnyViewExists_IsHeldUntilOneConsumesIt()
    {
        var tool = new ProjectTreeTool();

        tool.OnSelected();                       // nobody subscribed yet — first layout

        Assert.True(tool.ConsumeActivationFocus());
        Assert.False(tool.ConsumeActivationFocus());   // and only once
    }

    [Fact]
    public void WithNoActivation_ThereIsNothingPendingToConsume()
    {
        Assert.False(new ProjectTreeTool().ConsumeActivationFocus());
    }

    // ── The views act on it ───────────────────────────────────────────────────

    [Fact]
    public void TheProjectTreeViewFocusesItsContentOnActivation()
    {
        var cs = ReadRepoFile("src/Ui/Views/ProjectTree/ProjectTreeView.axaml.cs");

        Assert.Contains("ActivationFocusRequested += OnActivationFocusRequested", cs);
        Assert.Contains("ConsumeActivationFocus()", cs);

        // The TreeView first, so an activated panel takes the ARROW keys too and is fully navigable;
        // the scroller is the fallback for states where the tree cannot take focus at all.
        Assert.Contains("if (!TheTreeView.Focus()) TreeScroll.Focus();", cs);
    }

    [Fact]
    public void ThePaletteViewFocusesItsContentOnActivation()
    {
        var cs = ReadRepoFile("src/Ui/Views/Palette/PaletteToolView.axaml.cs");

        Assert.Contains("ActivationFocusRequested += OnActivationFocusRequested", cs);
        Assert.Contains("ConsumeActivationFocus()", cs);
        Assert.Contains("TileScroll.Focus()", cs);
    }

    // ── The general rule (owner: "this should be true for any window that uses this") ─────
    //
    //  A key handler only fires when the focused element is on the event's ROUTE, i.e. inside that
    //  view. So any panel that binds Page Up/Down must also claim focus when it is activated, or it
    //  works only after a preliminary click inside it — which is the whole bug. Enumerated from the
    //  source rather than listed by hand, so a NEW scrollable panel cannot be added without either
    //  wiring this up or deliberately editing this test.
    //
    //  DOCKED TOOL PANELS ONLY, and that exclusion is the rule's own reasoning rather than a hole in
    //  it (2026-08-28, when the Match Designer bound the same four keys). Activation focus exists
    //  because a dock TAB is chrome living OUTSIDE the panel it names: clicking it leaves the focus
    //  on something the panel's key route never passes through, so the panel has to reach out and
    //  take the focus back. A top-level Window has no such gap — when nothing inside it has focus the
    //  window itself is the key event's target, so a handler the window registered ON ITSELF is
    //  always on the route. Requiring ActivationFocusRequested there would be requiring a mechanism
    //  that does not exist for windows, to fix a bug they cannot have.
    [Fact]
    public void EveryViewThatBindsTheScrollKeys_AlsoClaimsFocusOnActivation()
    {
        var viewsDir = RepoPath("src/Ui/Views");
        var all = Directory
            .EnumerateFiles(viewsDir, "*.axaml.cs", SearchOption.AllDirectories)
            .Where(f => File.ReadAllText(f).Contains("PanelScrollKeys.ActionFor", StringComparison.Ordinal))
            .ToList();

        var users = all
            .Where(f => !Regex.IsMatch(File.ReadAllText(f), @"class\s+\w+\s*:\s*Window\b"))
            .ToList();

        // If this ever reads zero, the scan is broken and the assertion below is vacuous.
        Assert.True(users.Count >= 3,
            $"expected the .ctech editor, the Project Tree and the Library palette; found {users.Count}");

        foreach (var f in users)
        {
            var cs = File.ReadAllText(f);
            Assert.True(
                cs.Contains("ActivationFocusRequested", StringComparison.Ordinal),
                $"{Path.GetFileName(f)} binds the scroll keys but never claims focus on activation, so "
                + "they only work after a click inside it.");
            Assert.True(
                cs.Contains("ConsumeActivationFocus", StringComparison.Ordinal),
                $"{Path.GetFileName(f)} does not consume a focus request made before it was bound — a "
                + "panel activated during the first layout pass would be left unfocused.");
        }
    }

    private static string RepoPath(string relativePath, [CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root.");
        return Path.Combine(dir!, relativePath);
    }

    // Unsubscribing matters: these views are re-created and re-bound by every dock rearrangement, and
    // a leaked handler would go on focusing a control belonging to a torn-down view.
    // The fix is a silent no-op without this: TreeView and ScrollViewer are BOTH Focusable=false by
    // default, so Focus() on either simply returns false and nothing is focused.
    [Fact]
    public void TheTargetsOfThatFocusCall_CanActuallyTakeFocus()
    {
        Assert.Contains("Focusable=\"True\"", ReadRepoFile("src/Ui/Views/ProjectTree/ProjectTreeView.axaml"));
        Assert.Contains("Focusable=\"True\"", ReadRepoFile("src/Ui/Views/Palette/PaletteToolView.axaml"));
        Assert.Contains("Focusable=\"True\"", ReadRepoFile("src/Ui/Views/Layout/TechEditorView.axaml"));
    }

    [Fact]
    public void TheViewsDropTheirSubscriptionWhenReBound()
    {
        foreach (var path in new[]
        {
            "src/Ui/Views/ProjectTree/ProjectTreeView.axaml.cs",
            "src/Ui/Views/Palette/PaletteToolView.axaml.cs",
        })
            Assert.Contains("ActivationFocusRequested -= OnActivationFocusRequested", ReadRepoFile(path));
    }
}

// ================================================================
//  PanelScrollKeysTests.cs — Page Up / Page Down / Home / End in the Project Tree and the
//  Library palette (owner, 2026-08-25: "useful for UX when there are lots of components in the
//  Library palette, and in the Project Tree").
//
//  The RULE is a framework-free type (PanelScrollKeys) so it is testable without a rendered
//  window; the WIRING is a source scan, the same fallback this codebase already uses for every
//  view/code-behind rule it cannot exercise headlessly.
// ================================================================

using System.IO;
using System.Runtime.CompilerServices;
using Avalonia.Input;
using CircuitRF.Ui.Controls;
using Xunit;

namespace CircuitRF.Ui.Tests;

public class PanelScrollKeysTests
{
    private static string ReadRepoFile(string relativePath, [CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root.");
        return File.ReadAllText(Path.Combine(dir!, relativePath));
    }

    // ── The rule ──────────────────────────────────────────────────────────────

    [Fact]
    public void PageKeys_AlwaysScroll_EvenFromInsideATextField()
    {
        // The palette's search box is the case that matters: type a query, then page through what
        // it matched without having to leave the box first.
        Assert.Equal(PanelScrollAction.PageUp,   PanelScrollKeys.ActionFor(Key.PageUp,   sourceIsTextInput: true));
        Assert.Equal(PanelScrollAction.PageDown, PanelScrollKeys.ActionFor(Key.PageDown, sourceIsTextInput: true));
    }

    [Fact]
    public void HomeAndEnd_ScrollOnlyWhenTheKeystrokeIsNotComingFromATextField()
    {
        Assert.Equal(PanelScrollAction.Home, PanelScrollKeys.ActionFor(Key.Home, sourceIsTextInput: false));
        Assert.Equal(PanelScrollAction.End,  PanelScrollKeys.ActionFor(Key.End,  sourceIsTextInput: false));

        // In a text field these are caret motion, and taking them would break typing to add a
        // shortcut nobody asked for there.
        Assert.Null(PanelScrollKeys.ActionFor(Key.Home, sourceIsTextInput: true));
        Assert.Null(PanelScrollKeys.ActionFor(Key.End,  sourceIsTextInput: true));
    }

    [Fact]
    public void OtherKeys_AreLeftAlone()
    {
        // Up/Down especially: the TreeView's own arrow-key selection movement is untouched.
        foreach (var k in new[] { Key.Up, Key.Down, Key.Left, Key.Right, Key.Enter, Key.A, Key.Space })
        {
            Assert.Null(PanelScrollKeys.ActionFor(k, sourceIsTextInput: false));
            Assert.Null(PanelScrollKeys.ActionFor(k, sourceIsTextInput: true));
        }
    }

    // ── The wiring ────────────────────────────────────────────────────────────

    // Bubbling would let the focused TreeViewItem move the SELECTION on Home/End and swallow the
    // key — the tree must SCROLL past a long listing without changing what is selected.
    [Fact]
    public void TheProjectTreeHandlerTunnels_AndTargetsTheNamedScroller()
    {
        var cs    = ReadRepoFile("src/Ui/Views/ProjectTree/ProjectTreeView.axaml.cs");
        var axaml = ReadRepoFile("src/Ui/Views/ProjectTree/ProjectTreeView.axaml");

        Assert.Contains("AddHandler(KeyDownEvent, OnScrollKeyDown, RoutingStrategies.Tunnel)", cs);
        Assert.Contains("PanelScrollKeys.ActionFor", cs);
        Assert.Contains("PanelScrollKeys.Apply(action.Value, TreeScroll)", cs);

        // The TreeView is wrapped in an EXPLICIT ScrollViewer (its own template's inner one measures
        // against infinite height in that arrangement and never scrolls), so the handler has to name
        // the outer one rather than hunt for "the first ScrollViewer".
        Assert.Contains("x:Name=\"TreeScroll\"", axaml);
    }

    [Fact]
    public void ThePaletteHandlerTunnels_AndTargetsTheNamedScroller()
    {
        var cs    = ReadRepoFile("src/Ui/Views/Palette/PaletteToolView.axaml.cs");
        var axaml = ReadRepoFile("src/Ui/Views/Palette/PaletteToolView.axaml");

        Assert.Contains("AddHandler(KeyDownEvent, OnScrollKeyDown, RoutingStrategies.Tunnel)", cs);
        Assert.Contains("PanelScrollKeys.Apply(action.Value, TileScroll)", cs);
        Assert.Contains("x:Name=\"TileScroll\"", axaml);
    }

    // Without BOTH of these the palette keys are dead on arrival: a PaletteTile is a plain
    // UserControl and takes no focus, so clicking a tile leaves keyboard focus wherever it was and
    // the panel never sees a key at all.
    [Fact]
    public void ThePaletteTileAreaCanHoldKeyboardFocus()
    {
        var axaml = ReadRepoFile("src/Ui/Views/Palette/PaletteToolView.axaml");
        var cs    = ReadRepoFile("src/Ui/Views/Palette/PaletteToolView.axaml.cs");

        Assert.Contains("Focusable=\"True\"", axaml);
        Assert.Contains("TileScroll.Focus()", cs);
    }

    // One rule, one place: the .ctech editor was here first and now reads from the same type.
    [Fact]
    public void TheTechEditorUsesTheSameSharedRule()
    {
        var cs = ReadRepoFile("src/Ui/Views/Layout/TechEditorView.axaml.cs");
        Assert.Contains("PanelScrollKeys.ActionFor", cs);
        Assert.DoesNotContain("TechEditorScrollKeys", cs);
    }
}

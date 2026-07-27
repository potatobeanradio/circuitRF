using System.IO;
using System.Runtime.CompilerServices;

namespace CircuitRF.Ui.Tests;

// L1 fix — docs/sonnet-briefs/brief-L1-fix-context-menu-stacking.md: right-clicking the layout canvas
// used to construct a BRAND-NEW ContextMenu on every click and open it manually
// (`new ContextMenu { ItemsSource = items }; menu.Open(this);`), so popups stacked — each right-click
// added another one underneath the last, dismissed one at a time. The fix converges on the same
// framework-owned pattern SymbolEditorCanvas/SchematicCanvas already use: ONE ContextMenu instance,
// declared once in LayoutEditorView.axaml, opened by Avalonia itself, rebuilt fresh on every
// `Opening` — never `new`-ed per click.
//
// LayoutCanvas is a Control subclass and this project's test suite must not call any Avalonia
// runtime API (tests/Ui.Tests/CircuitRF.Ui.Tests.csproj's own header comment), so the menu-building
// and event-wiring behavior cannot be driven directly (matching every prior Layout Editor phase's
// note that context-menu construction "cannot be unit-tested headlessly"). Per the brief's own
// fallback (§5 gate 2): assert the STRUCTURAL invariant that makes stacking impossible by
// construction — LayoutCanvas.cs itself never constructs a ContextMenu at runtime; the single
// instance comes from XAML.

public class LayoutContextMenuStackingTests
{
    private static string ReadRepoFile(string relativePath, [CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root (no CLAUDE.md found walking up from this test file).");
        return File.ReadAllText(Path.Combine(dir!, relativePath));
    }

    [Fact]
    public void LayoutCanvas_NeverConstructsAContextMenu_TheSingleInstanceComesFromXaml()
    {
        string src = ReadRepoFile(Path.Combine("src", "Ui", "Controls", "LayoutCanvas.cs"));

        Assert.DoesNotContain("new ContextMenu", src);
        // Ten-consecutive-right-clicks-leave-exactly-one-menu (gate 2's headline scenario) is
        // structurally guaranteed once this holds: with no `new ContextMenu` anywhere in this file,
        // there is no code path left that could construct a second instance.
    }

    [Fact]
    public void LayoutCanvas_NeverCallsOpenOnAContextMenu_AvaloniaOwnsOpeningIt()
    {
        string src = ReadRepoFile(Path.Combine("src", "Ui", "Controls", "LayoutCanvas.cs"));

        Assert.DoesNotContain(".Open(this)", src);
    }

    [Fact]
    public void LayoutEditorView_DeclaresExactlyOneContextMenu_OnTheCanvas()
    {
        string src = ReadRepoFile(Path.Combine("src", "Ui", "Views", "Layout", "LayoutEditorView.axaml"));

        int count = 0, idx = 0;
        while ((idx = src.IndexOf("<ContextMenu", idx, System.StringComparison.Ordinal)) >= 0) { count++; idx++; }
        Assert.Equal(1, count);
        Assert.Contains("Opening=\"OnLayoutContextMenuOpening\"", src);
    }
}

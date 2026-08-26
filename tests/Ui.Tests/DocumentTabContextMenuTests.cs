using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;

namespace CircuitRF.Ui.Tests;

// The document tab-strip context menu — "Reveal in Finder/Explorer" on a tab header.
//
// Dock's Fluent theme binds the tab's menu with
//     <Setter Property="DocumentContextMenu" Value="{DynamicResource DocumentTabStripItemContextMenu}"/>
// so circuitRF overrides it by defining that same key in the application-scope dictionary. Two
// things about that arrangement fail SILENTLY and are what these tests hold shut:
//
//  1. The key is the whole contract. Misspell it and nothing errors — the DynamicResource simply
//     resolves to Dock's own menu and the Reveal item never appears anywhere.
//  2. DocumentContextMenu is a single ContextMenu property, not a collection, so adding one item
//     means restating Dock's entire menu. A partial copy also does not error: it just silently
//     drops Float / Close other tabs / Tab Layout / … from every document tab.
//
// This project's tests must not call any Avalonia runtime API (see the .csproj header), so these
// are structural assertions over the source, the same fallback LayoutContextMenuStackingTests uses.
public class DocumentTabContextMenuTests
{
    private static string RepoRoot([CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root (no CLAUDE.md walking up from this test file).");
        return dir!;
    }

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine(RepoRoot(), Path.Combine(parts)));

    private static string Menu()      => Read("src", "Ui", "Styles", "DocumentTabContextMenu.axaml");
    private static string Resources() => Read("src", "Ui", "Styles", "CircuitRfResources.axaml");

    // ── The switch ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheSwitchIsOn_TheSharedResourceDictionaryMergesOurMenu()
    {
        // Half one of the switch: commenting out this single line is how the app goes back to
        // Dock's own (native) tab context menu. If it is ever commented out deliberately, this
        // test is the thing that says so out loud rather than the menu quietly changing.
        string src = Resources();
        int line = src.Split('\n').Count(l =>
            l.Contains("Styles/DocumentTabContextMenu.axaml", StringComparison.Ordinal)
            && l.TrimStart().StartsWith("<ResourceInclude", StringComparison.Ordinal));

        Assert.Equal(1, line);
    }

    [Fact]
    public void TheOverrideUsesTheKeyDockActuallyLooksUp()
    {
        // Half two: the key. Dock.Avalonia.Themes.Fluent 12.0.0.2's DocumentTabStripItem
        // ControlTheme reads exactly this name via DynamicResource; any other spelling is an
        // override of nothing.
        Assert.Contains("<ContextMenu x:Key=\"DocumentTabStripItemContextMenu\">", Menu(), StringComparison.Ordinal);
    }

    [Fact]
    public void ExactlyOneContextMenuIsDefined()
    {
        string src = Menu();
        int count = 0, idx = 0;
        while ((idx = src.IndexOf("<ContextMenu", idx, StringComparison.Ordinal)) >= 0) { count++; idx++; }
        Assert.Equal(1, count);
    }

    // ── circuitRF's own entry ─────────────────────────────────────────────────────────────────

    [Fact]
    public void RevealIsTheFirstItem_AndASeparatorFollowsIt()
    {
        string src = Menu();
        int menu      = src.IndexOf("<ContextMenu x:Key=", StringComparison.Ordinal);
        int reveal    = src.IndexOf("cmds:DocumentTabCommands.Reveal}", menu, StringComparison.Ordinal);
        int separator = src.IndexOf("<Separator", menu, StringComparison.Ordinal);
        int firstDock = src.IndexOf("DocumentTabStripItemFloatString", menu, StringComparison.Ordinal);

        Assert.True(reveal > menu,          "The Reveal item is not inside the ContextMenu.");
        Assert.True(reveal < separator,     "The separator must come AFTER the Reveal item.");
        Assert.True(separator < firstDock,  "The separator must sit between Reveal and Dock's own items.");
    }

    [Fact]
    public void RevealAndItsSeparatorHideTogetherForAScratchDocument()
    {
        // A scratch document has no file, so the entry does not apply — and a separator left
        // behind on its own would render as a stray rule at the top of the menu.
        string src  = Menu();
        int menu    = src.IndexOf("<ContextMenu x:Key=", StringComparison.Ordinal);
        int firstDock = src.IndexOf("DocumentTabStripItemFloatString", menu, StringComparison.Ordinal);
        string ours = src[menu..firstDock];

        const string guard = "IsVisible=\"{Binding Converter={x:Static conv:FileBackedDocumentConverter.Instance}}\"";
        Assert.Equal(2, ours.Split(guard).Length - 1);
    }

    [Fact]
    public void RevealPassesTheDockableItself_NotAWorkspaceRoutedBinding()
    {
        // A torn-off document lives in a floating host window whose DataContext is not the
        // WorkspaceViewModel: a $parent[DockControl].DataContext route would work in the main
        // window and silently do nothing in a floating one.
        string src = Menu();
        Assert.Contains("Command=\"{x:Static cmds:DocumentTabCommands.Reveal}\"", src, StringComparison.Ordinal);
        Assert.Contains("CommandParameter=\"{Binding}\"", src, StringComparison.Ordinal);
        Assert.DoesNotContain("$parent[dock:DockControl]", src, StringComparison.Ordinal);
    }

    // ── Dock's own entries, all of them ───────────────────────────────────────────────────────

    [Theory]
    // Every header key in Dock.Avalonia.Themes.Fluent 12.0.0.2's Controls/ControlStrings.axaml
    // DocumentTabStripItem block. Copying the upstream menu is all-or-nothing; a missed item is
    // a menu entry that silently disappears from the shipping app.
    [InlineData("DocumentTabStripItemFloatString")]
    [InlineData("DocumentTabStripItemFloatAllString")]
    [InlineData("DocumentTabStripItemCloseString")]
    [InlineData("DocumentTabStripItemCloseOtherTabsString")]
    [InlineData("DocumentTabStripItemCloseAllTabsString")]
    [InlineData("DocumentTabStripItemCloseTabsLeftString")]
    [InlineData("DocumentTabStripItemCloseTabsRightString")]
    [InlineData("DocumentTabStripItemCloseTabsAboveString")]
    [InlineData("DocumentTabStripItemCloseTabsBelowString")]
    [InlineData("DocumentTabStripItemNewHorizontalDockString")]
    [InlineData("DocumentTabStripItemNewVerticalDockString")]
    [InlineData("DocumentTabStripItemTabLayoutString")]
    [InlineData("DocumentTabStripItemTabLayoutLeftString")]
    [InlineData("DocumentTabStripItemTabLayoutTopString")]
    [InlineData("DocumentTabStripItemTabLayoutRightString")]
    [InlineData("DocumentTabStripItemLayoutModeString")]
    [InlineData("DocumentTabStripItemLayoutModeTabbedString")]
    [InlineData("DocumentTabStripItemLayoutModeMdiString")]
    public void EveryStockMenuEntryIsReplicated(string headerKey)
    {
        Assert.Contains($"{{DynamicResource {headerKey}}}", Menu(), StringComparison.Ordinal);
    }

    [Fact]
    public void StockHeadersStayBoundToDocksOwnStrings_NotForkedIntoOurOwnEnglish()
    {
        // The copied block keeps {DynamicResource …String}, resolved from Dock's own
        // ControlStrings.axaml, so the item text follows the theme instead of drifting from it.
        string src = Menu();
        foreach (var literal in new[] { "\"_Float\"", "\"_Close\"", "\"Close all tabs\"", "\"Tab Layout\"" })
            Assert.DoesNotContain(literal, src, StringComparison.Ordinal);
    }

    [Fact]
    public void StockCommandsAreRoutedThroughDocksFactory()
    {
        string src = Menu();
        foreach (var command in new[]
                 {
                     "FloatDockable", "FloatAllDockables", "CloseDockable", "CloseOtherDockables",
                     "CloseAllDockables", "CloseLeftDockables", "CloseRightDockables",
                     "NewHorizontalDocumentDock", "NewVerticalDocumentDock",
                     "SetDocumentDockTabsLayoutLeft", "SetDocumentDockTabsLayoutTop",
                     "SetDocumentDockTabsLayoutRight",
                     "SetDocumentDockLayoutModeTabbed", "SetDocumentDockLayoutModeMdi",
                 })
            Assert.Contains($"Owner.Factory.{command}", src, StringComparison.Ordinal);
    }

    // ── The documents the menu acts on ────────────────────────────────────────────────────────

    [Fact]
    public void EveryDocumentTypeWithAFilePathDeclaresTheInterface()
    {
        // DERIVED from the source tree, not a hand-written list, because a hand-written list is
        // exactly what shipped the first version of this menu with the item missing from half the
        // document kinds: four of the ten Document subclasses were covered and SymbolEditor / Tech
        // / EmSetup / Harmonica were not. Nothing errors in that state — the converter sees a plain
        // IDockable and the entry silently hides on those tabs only.
        var missing = new System.Collections.Generic.List<string>();

        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src", "Ui"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                continue;

            string src = File.ReadAllText(file);
            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(src, @"class\s+(\w+)\s*:\s*Document\b[^\r\n]*"))
            {
                string declaration = m.Value;
                // A document is "file-backed" if it declares a FilePath property at all.
                if (!System.Text.RegularExpressions.Regex.IsMatch(src, @"public\s+string\??\s+FilePath\s*(\{|=>)"))
                    continue;
                if (!declaration.Contains("IFileBackedDocument", StringComparison.Ordinal))
                    missing.Add($"{m.Groups[1].Value} ({Path.GetFileName(file)})");
            }
        }

        Assert.True(missing.Count == 0,
            "These document types declare a FilePath but not IFileBackedDocument, so the tab menu's "
            + "Reveal item hides on their tabs: " + string.Join(", ", missing));
    }

    [Fact]
    public void TheDerivedGuardActuallySeesTheKnownDocumentTypes()
    {
        // Guards the guard: a regex that matched nothing would pass the test above vacuously.
        var found = new System.Collections.Generic.List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src", "Ui"), "*Document.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;
            string src = File.ReadAllText(file);
            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(src, @"class\s+(\w+)\s*:\s*Document\b"))
                found.Add(m.Groups[1].Value);
        }

        foreach (var expected in new[]
                 {
                     "LayoutDocument", "SchematicDocument", "DataDisplayDocument", "WBondDocument",
                     "SymbolEditorDocument", "TechDocument", "EmSetupDocument", "HarmonicaDocument",
                     "CellParameterEditorDocument", "StubDocument",
                 })
            Assert.Contains(expected, found);
    }

    [Fact]
    public void EveryDocumentTypeIsAccountedFor_OnlyTheWelcomeStubHasNoFile()
    {
        // The test above can only police documents that ALREADY declare a FilePath, which is how
        // .ccell was missed: CellParameterEditorDocument had no FilePath property at all, so there
        // was nothing for the derived guard to notice. This one closes that hole from the other
        // side — every Document subclass must either be file-backed or be on this list, so adding
        // an eleventh forces the decision rather than defaulting to "no Reveal on that tab".
        var notFileBacked = new System.Collections.Generic.List<string>();

        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src", "Ui"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                continue;

            string src = File.ReadAllText(file);
            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(src, @"class\s+(\w+)\s*:\s*Document\b[^\r\n]*"))
                if (!m.Value.Contains("IFileBackedDocument", StringComparison.Ordinal))
                    notFileBacked.Add(m.Groups[1].Value);
        }

        // StubDocument is the Welcome tab: an in-memory placeholder with no file, ever.
        Assert.Equal(new[] { "StubDocument" }, notFileBacked.OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }

    // ── The label ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheLabelIsPlatformCorrect_AndAllThreeSpellingsExist()
    {
        // Pinned from the source so the Windows and Linux wording is held even when the suite runs
        // on macOS. Same three spellings ProjectTreeItemViewModel and ProjectTreeTool already use,
        // so the tab menu does not introduce a fourth wording for the same action.
        string src = Read("src", "Ui", "FileReveal.cs");
        Assert.Contains("\"Reveal in Finder\"", src, StringComparison.Ordinal);         // macOS
        Assert.Contains("\"Reveal in Explorer\"", src, StringComparison.Ordinal);       // Windows
        Assert.Contains("\"Reveal in File Manager\"", src, StringComparison.Ordinal);   // everything else

        string expected =
            OperatingSystem.IsMacOS()     ? "Reveal in Finder"
            : OperatingSystem.IsWindows() ? "Reveal in Explorer"
            : "Reveal in File Manager";
        Assert.Equal(expected, CircuitRF.Ui.FileReveal.Label);
        Assert.Equal(expected, CircuitRF.Ui.Commands.DocumentTabCommands.RevealLabel);
    }

    // ── One reveal implementation ─────────────────────────────────────────────────────────────

    [Fact]
    public void TheTabMenuDoesNotAddAnotherCopyOfTheRevealLogic()
    {
        // RESOLVED.md §4: three copies of these per-platform argument forms had already drifted
        // into a security bug once. The tab menu routes through FileReveal, and MessagesTool —
        // whose copy was byte-identical — now does too.
        Assert.Contains("FileReveal.Reveal", Read("src", "Ui", "Commands", "DocumentTabCommands.cs"), StringComparison.Ordinal);

        string messages = Read("src", "Ui", "ViewModels", "Dock", "MessagesTool.cs");
        Assert.Contains("FileReveal.Reveal", messages, StringComparison.Ordinal);
        Assert.DoesNotContain("Process.Start", messages, StringComparison.Ordinal);
    }
}

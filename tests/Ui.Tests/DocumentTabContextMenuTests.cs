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
    public void RevealHidesForAScratchDocument_AndNoStrayRuleIsLeftBehind()
    {
        // A scratch document has no file, so the Reveal entry does not apply — and a separator left
        // behind on its own would render as a stray rule at the top of the menu.
        //
        // The separator used to carry Reveal's own converter, which was right while Reveal was the
        // only entry. Since Save and Save As joined it (2026-09-09) it tracks the WHOLE block
        // (DocumentTabMenuVisibility.Any) instead: bound to Reveal alone it would now vanish from a
        // scratch schematic tab that still shows two entries, fusing ours onto Dock's.
        string src  = Menu();
        int menu    = src.IndexOf("<ContextMenu x:Key=", StringComparison.Ordinal);
        int firstDock = src.IndexOf("DocumentTabStripItemFloatString", menu, StringComparison.Ordinal);
        string ours = src[menu..firstDock];

        const string guard = "IsVisible=\"{Binding Converter={x:Static conv:FileBackedDocumentConverter.Instance}}\"";
        Assert.Equal(3, ours.Split(guard).Length - 1);   // Reveal, Copy Path and Copy Full Path, and only they

        const string block = "IsVisible=\"{Binding Converter={x:Static conv:DocumentTabMenuVisibility.Any}}\"";
        Assert.Equal(1, ours.Split(block).Length - 1);   // the separator that closes our block
    }

    /// <summary>Owner request, 2026-10-28: Copy Path and Copy Full Path directly under Reveal, then a
    /// separator — one that shows only when a save item follows, so it never stacks on the block's.</summary>
    [Fact]
    public void CopyPathItemsFollowReveal_ThenTheirOwnSeparator()
    {
        string src  = Menu();
        int menu    = src.IndexOf("<ContextMenu x:Key=", StringComparison.Ordinal);
        int reveal  = src.IndexOf("cmds:DocumentTabCommands.Reveal}", menu, StringComparison.Ordinal);
        int path    = src.IndexOf("cmds:DocumentTabCommands.CopyPath}", menu, StringComparison.Ordinal);
        int full    = src.IndexOf("cmds:DocumentTabCommands.CopyFullPath}", menu, StringComparison.Ordinal);
        int sep     = src.IndexOf("<Separator", menu, StringComparison.Ordinal);
        int save    = src.IndexOf("cmds:DocumentTabCommands.Save}", menu, StringComparison.Ordinal);

        Assert.True(reveal < path && path < full && full < sep && sep < save);
        Assert.Contains("DocumentTabMenuVisibility.AfterPaths", src[sep..save], StringComparison.Ordinal);
    }

    [Fact]
    public void CopyPath_IsTheEnclosingFolder_CopyFullPath_ThePathItself()
    {
        string file = Path.Combine(Path.GetTempPath(), "ws", "Amp", "schematic", "Amp.csch");
        Assert.Equal(Path.Combine(Path.GetTempPath(), "ws", "Amp", "schematic"), PathClipboard.EnclosingFolder(file));
        // A folder's own name is the last part too: a cell row copies the folder the cell sits in.
        string cell = Path.Combine(Path.GetTempPath(), "ws", "Amp") + Path.DirectorySeparatorChar;
        Assert.Equal(Path.Combine(Path.GetTempPath(), "ws"), PathClipboard.EnclosingFolder(cell));
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

    // ── Save / Save As on the tab (owner request, 2026-09-09) ─────────────────────────────────

    [Fact]
    public void SaveAndSaveAsFollowReveal_AndTheSeparatorStillClosesOurBlock()
    {
        // The owner asked for both entries BELOW "Reveal in Finder"; the separator that already
        // divided circuitRF's block from Dock's has to stay the last thing in it, or a rule lands in
        // the middle of our own items.
        string src = Menu();
        int menu      = src.IndexOf("<ContextMenu x:Key=", StringComparison.Ordinal);
        int reveal    = src.IndexOf("cmds:DocumentTabCommands.Reveal}", menu, StringComparison.Ordinal);
        int save      = src.IndexOf("cmds:DocumentTabCommands.Save}", menu, StringComparison.Ordinal);
        int saveAs    = src.IndexOf("cmds:DocumentTabCommands.SaveAs}", menu, StringComparison.Ordinal);
        // The separator that closes the block — not the one under Copy Path / Copy Full Path, which sits above Save.
        int separator = src.IndexOf("<Separator IsVisible=\"{Binding Converter={x:Static conv:DocumentTabMenuVisibility.Any}}", menu, StringComparison.Ordinal);
        int firstDock = src.IndexOf("DocumentTabStripItemFloatString", menu, StringComparison.Ordinal);

        Assert.True(reveal > menu,       "Reveal is not inside the ContextMenu.");
        Assert.True(save   > reveal,     "Save must come after Reveal.");
        Assert.True(saveAs > save,       "Save As… must come after Save.");
        Assert.True(separator > saveAs,  "The separator must come after both save entries.");
        Assert.True(separator < firstDock, "The separator must sit between our block and Dock's own items.");
    }

    [Fact]
    public void RevealInWorkspaceTreeSitsAboveSave_WithItsOwnSeparatorUnderIt()
    {
        string src = Menu();
        int menu    = src.IndexOf("<ContextMenu x:Key=", StringComparison.Ordinal);
        int paths   = src.IndexOf("cmds:DocumentTabCommands.CopyFullPath}", menu, StringComparison.Ordinal);
        int reveal  = src.IndexOf("Command=\"{x:Static cmds:DocumentTabCommands.RevealInTree}\"", menu, StringComparison.Ordinal);
        int sep     = src.IndexOf("<Separator IsVisible=\"{Binding Converter={x:Static conv:DocumentTabMenuVisibility.AfterRevealInTree}}", menu, StringComparison.Ordinal);
        int save    = src.IndexOf("cmds:DocumentTabCommands.Save}", menu, StringComparison.Ordinal);

        Assert.True(reveal > paths, "Reveal in Workspace Tree must follow Copy Full Path.");
        Assert.True(sep > reveal && sep < save, "Its separator must sit between it and Save.");
        Assert.Contains("CommandParameter=\"{Binding}\"", src.Substring(reveal, 200), StringComparison.Ordinal);
        Assert.Contains("Header=\"Reveal in Workspace Tree\"", src, StringComparison.Ordinal);
    }

    [Fact]
    public void BothSaveEntriesPassTheDockableItself_SoAFloatingTabSavesToo()
    {
        // Same reason Reveal takes the dockable: a torn-off document lives in a floating host window
        // whose DataContext is the DOCUMENT, not the WorkspaceViewModel — any route through the
        // visual tree or the shell's DataContext works docked and silently does nothing floating.
        // The workspace is resolved FROM the dockable instead (WorkspaceViewModel.WorkspaceOf).
        string menu = Menu();
        foreach (var command in new[] { "Save", "SaveAs" })
        {
            int at = menu.IndexOf($"Command=\"{{x:Static cmds:DocumentTabCommands.{command}}}\"", StringComparison.Ordinal);
            Assert.True(at > 0, $"The {command} item is not bound to DocumentTabCommands.{command}.");
            Assert.Contains("CommandParameter=\"{Binding}\"",
                            menu.Substring(at, Math.Min(300, menu.Length - at)), StringComparison.Ordinal);
        }

        string commands = Read("src", "Ui", "Commands", "DocumentTabCommands.cs");
        Assert.Contains("WorkspaceViewModel.WorkspaceOf(d)", commands, StringComparison.Ordinal);
        Assert.DoesNotContain("WorkspaceLocator.Any()", commands, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSeparatorHidesOnlyWhenEveryCircuitRfEntryDoes()
    {
        // It used to track the Reveal item alone, which was right while Reveal was the only entry.
        // With three, a separator bound to Reveal's own rule would vanish from a scratch schematic
        // tab that still shows Save and Save As, leaving our items fused onto Dock's.
        string src = Menu();
        Assert.Contains("<Separator IsVisible=\"{Binding Converter={x:Static conv:DocumentTabMenuVisibility.Any}}\" />",
                        src, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryDocumentTypeIsNamedInTheSaveRouteTable()
    {
        // DERIVED, for the reason the Reveal item's own guards are: the first version of this menu
        // shipped with the entry missing from six of the ten document kinds because the list was
        // hand-written. A document type absent from the table below answers false to HasSaveRoute
        // and its tab silently offers no Save at all — so an eleventh document kind has to be named
        // here (even if the answer is "no route"), rather than defaulting to one.
        string table = Read("src", "Ui", "ViewModels", "WorkspaceViewModel.TabSave.cs");
        var missing = new System.Collections.Generic.List<string>();

        foreach (var type in DocumentTypeNames())
            if (!table.Contains(type, StringComparison.Ordinal))
                missing.Add(type);

        Assert.True(missing.Count == 0,
            "These document types are not named in WorkspaceViewModel.TabSave.cs, so their tabs "
            + "silently get no Save entry: " + string.Join(", ", missing));
    }

    [Fact]
    public void TheSaveRouteGuardActuallySeesTheDocumentTypes()
    {
        // Guards the guard above: a scan that found nothing would pass it vacuously.
        Assert.True(DocumentTypeNames().Count >= 10,
            "The Document-subclass scan found " + DocumentTypeNames().Count + " types; it should see all of them.");
        Assert.Contains("HarmonicaDocument", DocumentTypeNames());
        Assert.Contains("StubDocument", DocumentTypeNames());
    }

    [Fact]
    public void TheWelcomeTabOffersNeitherSave()
    {
        // The one document kind that can be constructed here without a view model. StubDocument has
        // no file, ever, so both entries hide rather than greying out.
        var welcome = new CircuitRF.Ui.ViewModels.Dock.StubDocument();
        Assert.False(CircuitRF.Ui.ViewModels.WorkspaceViewModel.HasSaveRoute(welcome));
        Assert.False(CircuitRF.Ui.ViewModels.WorkspaceViewModel.HasSaveAsRoute(welcome));
        Assert.False(CircuitRF.Ui.ViewModels.WorkspaceViewModel.HasSaveRoute(null));
        Assert.False(CircuitRF.Ui.ViewModels.WorkspaceViewModel.HasSaveAsRoute(null));
    }

    [Fact]
    public void EverySaveableDocumentKindAlsoOffersSaveAs()
    {
        // The owner's own report the day this shipped: Save As was missing on a .ctech tab, because
        // TechDocument had been left out of the Save As table on the theory that a technology is
        // referenced by path and a Save As would leave designs pointing at the old file. It does —
        // and so does a schematic's Save As, which circuitRF has always offered, so the answer is to
        // SAY that (WorkspaceViewModel.OnTechSavedAs) rather than to withhold the item.
        //
        // Pinned as an identity between the two tables: a kind that can be saved can be saved
        // elsewhere. Diverging again has to be a deliberate edit to this test, not an omission.
        string table = Read("src", "Ui", "ViewModels", "WorkspaceViewModel.TabSave.cs");

        var save   = TypesListedAfter(table, "internal static bool HasSaveRoute");
        var saveAs = TypesListedAfter(table, "internal static bool HasSaveAsRoute");

        Assert.NotEmpty(save);
        Assert.Equal(save, saveAs);
        Assert.Contains("TechDocument", save);
    }

    /// <summary>The <c>XDocument or YDocument or …</c> type list in one of the two route predicates.</summary>
    private static System.Collections.Generic.List<string> TypesListedAfter(string source, string signature)
    {
        int at = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at > 0, $"'{signature}' was not found — the route table has been renamed or moved.");

        int end = source.IndexOf(';', at);
        return System.Text.RegularExpressions.Regex.Matches(source[at..end], @"\b(\w+Document)\b")
                   .Select(m => m.Groups[1].Value)
                   .Distinct()
                   .OrderBy(n => n, StringComparer.Ordinal)
                   .ToList();
    }

    [Fact]
    public void TheTabMenuWritesNoFileOfItsOwn()
    {
        // The entries exist to REACH the routes File ▸ Save already uses, not to be a second one.
        // A save written here would be a second spelling of "saved" for the same document kind —
        // the defect RESOLVED.md §4 records for the three copies of Reveal's own argument forms.
        string table = Read("src", "Ui", "ViewModels", "WorkspaceViewModel.TabSave.cs");
        foreach (var write in new[] { "Persistence.SaveToFile", "File.WriteAllText", "SaveAllAsync" })
            Assert.DoesNotContain(write, table, StringComparison.Ordinal);
    }

    [Fact]
    public void HarmonicaHasExactlyOneSaveImplementation()
    {
        // The .charm write used to live in HarmonicaView's code-behind, which the tab menu cannot
        // reach: a background tab's view may never have been realized, so a route that starts at the
        // view does nothing on the tab the user actually right-clicked. It moved to a shared static
        // (harmonicaRF also runs standalone, with no workspace, so it cannot live on the workspace).
        string view = Read("src", "Ui", "Views", "Harmonica", "HarmonicaView.axaml.cs");
        Assert.Contains("HarmonicaDocumentSave.RunAsync", view, StringComparison.Ordinal);
        Assert.DoesNotContain(".ToCharmJson(", view, StringComparison.Ordinal);

        string shared = Read("src", "Ui", "Harmonica", "HarmonicaDocumentSave.cs");
        Assert.Contains(".ToCharmJson(", shared, StringComparison.Ordinal);
    }

    [Fact]
    public void ATechnologySaveAsFollowsTheNewFile_AndSaysWhoStillPointsAtTheOld()
    {
        // A .ctech is referenced by PATH (a layout's TechRef, or the workspace's DefaultTechRef), so
        // the editor moving to the new file leaves every design resolving the original. The route
        // re-keys the open-document map, invalidates BOTH cache entries, and says so — a Save As that
        // silently changed nothing visible is what withholding this entry was meant to avoid, and
        // reporting it is the better answer.
        string ws = Read("src", "Ui", "ViewModels", "WorkspaceViewModel.cs");
        Assert.Contains("private void OnTechSavedAs(string oldPath, string newPath)", ws, StringComparison.Ordinal);
        Assert.Contains("_techCache.Invalidate(oldPath)", ws, StringComparison.Ordinal);
        Assert.Contains("_techCache.Invalidate(newPath)", ws, StringComparison.Ordinal);
        Assert.Contains("still do — repoint a layout's technology", ws, StringComparison.Ordinal);

        // The VM carries the OLD path in the event, so a SECOND Save As moves the key that is
        // actually current rather than removing the original one again.
        string vm = Read("src", "Ui", "Layout", "TechEditorViewModel.cs");
        Assert.Contains("public event Action<string, string>? TechSavedAs;", vm, StringComparison.Ordinal);
        Assert.Contains("TechSavedAs?.Invoke(oldPath, newPath)", vm, StringComparison.Ordinal);
    }

    [Fact]
    public void TheEmSetupSaveAsPickerIsStatedOnce()
    {
        // Same shape: the .cem picker moved off the editor view so the tab menu can open the same
        // one. Two pickers would be two suggested names, two extensions and two overwrite rules.
        string view = Read("src", "Ui", "Views", "Layout", "EmSetupEditorView.axaml.cs");
        Assert.Contains("SaveEmSetupAs(doc, owner)", view, StringComparison.Ordinal);
        Assert.DoesNotContain("Save EM Setup As", view, StringComparison.Ordinal);

        Assert.Contains("Save EM Setup As",
                        Read("src", "Ui", "ViewModels", "WorkspaceViewModel.TabSave.cs"), StringComparison.Ordinal);
    }

    /// <summary>Every <c>Document</c> subclass in src/Ui, read from the source tree.</summary>
    private static System.Collections.Generic.List<string> DocumentTypeNames()
    {
        var found = new System.Collections.Generic.List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src", "Ui"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                continue;

            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(file), @"class\s+(\w+)\s*:\s*Document\b"))
                found.Add(m.Groups[1].Value);
        }
        return found;
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

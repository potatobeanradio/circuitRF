using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;
using Xunit;

namespace CircuitRF.Ui.Tests;

// brief-file-menu-restructure.md — File-menu restructure + View-menu cleanup. WorkspaceWindow is a
// real Window subclass and cannot be constructed in this headless suite (the same constraint every
// prior menu/dialog phase in this codebase has hit) — so, per that established precedent, menu
// STRUCTURE is verified by parsing the real .axaml source as XML (not a screenshot, not a mock), and
// the R-menu-4 per-window resolution's own core algorithm (FindAnyDocumentInDock) is verified directly
// against real Dock.Model.Mvvm.Controls types, which — like SchematicDocument/SymbolEditorDocument —
// are plain C# and construct fine without Avalonia's platform.
public class FileMenuRestructureTests
{
    private static string ReadRepoFile(string relativePath, [CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root (no CLAUDE.md found walking up from this test file).");
        return File.ReadAllText(Path.Combine(dir!, relativePath));
    }

    private sealed record MenuNode(string Header, bool IsSeparator, IReadOnlyList<MenuNode> Children)
    {
        public override string ToString() => IsSeparator ? "---" : Header;
    }

    private static readonly MenuNode Sep = new("", true, Array.Empty<MenuNode>());

    /// <summary>Walks the DIRECT children of an in-window &lt;Menu&gt;/&lt;MenuItem&gt; element —
    /// a MenuItem's own children ARE its submenu, so this recurses straight through.</summary>
    private static IReadOnlyList<MenuNode> ExtractMenuItems(XElement parent)
    {
        var result = new List<MenuNode>();
        foreach (var child in parent.Elements())
        {
            var local = child.Name.LocalName;
            if (local == "Separator")
                result.Add(Sep);
            else if (local == "MenuItem")
                result.Add(new MenuNode(child.Attribute("Header")?.Value ?? "", false, ExtractMenuItems(child)));
        }
        return result;
    }

    /// <summary>Walks the DIRECT children of a &lt;NativeMenu&gt;/&lt;NativeMenuItem&gt; element — a
    /// NativeMenuItem's submenu is wrapped in a &lt;NativeMenuItem.Menu&gt;&lt;NativeMenu&gt; property
    /// element, unlike MenuItem's direct children.</summary>
    private static IReadOnlyList<MenuNode> ExtractNativeMenuItems(XElement parent)
    {
        var result = new List<MenuNode>();
        foreach (var child in parent.Elements())
        {
            var local = child.Name.LocalName;
            if (local == "NativeMenuItemSeparator")
            {
                result.Add(Sep);
            }
            else if (local == "NativeMenuItem")
            {
                var menuProp  = child.Elements().FirstOrDefault(e => e.Name.LocalName == "NativeMenuItem.Menu");
                var nativeMenu = menuProp?.Elements().FirstOrDefault(e => e.Name.LocalName == "NativeMenu");
                var children  = nativeMenu is not null ? ExtractNativeMenuItems(nativeMenu) : Array.Empty<MenuNode>();
                result.Add(new MenuNode(child.Attribute("Header")?.Value ?? "", false, children));
            }
        }
        return result;
    }

    private static XElement LoadWorkspaceWindowXaml()
    {
        var src = ReadRepoFile(Path.Combine("src", "Ui", "Views", "WorkspaceWindow.axaml"));
        return XDocument.Parse(src).Root!;
    }

    private static MenuNode FindTopLevel(IReadOnlyList<MenuNode> topLevel, string header)
        => topLevel.First(n => !n.IsSeparator && n.Header == header);

    /// <summary>The in-window &lt;Menu&gt;'s File MenuItem's own children.</summary>
    private static IReadOnlyList<MenuNode> InWindowFileChildren()
    {
        var root = LoadWorkspaceWindowXaml();
        var menu = root.Descendants().First(e => e.Name.LocalName == "Menu" && e.Parent?.Name.LocalName != "NativeMenuItem.Menu");
        var top  = ExtractMenuItems(menu);
        return FindTopLevel(top, "_File").Children;
    }

    /// <summary>The macOS &lt;NativeMenu&gt;'s File NativeMenuItem's own children.</summary>
    private static IReadOnlyList<MenuNode> NativeFileChildren()
    {
        var root = LoadWorkspaceWindowXaml();
        var nativeMenuMenu = root.Descendants().First(e => e.Name.LocalName == "NativeMenu.Menu");
        var nativeMenu = nativeMenuMenu.Elements().First(e => e.Name.LocalName == "NativeMenu");
        var top = ExtractNativeMenuItems(nativeMenu);
        return FindTopLevel(top, "File").Children;
    }

    // ── Gate 2 — structure matches §1 exactly, both surfaces ─────────────────────────────────────

    private static readonly string[] ExpectedTopLevelHeadersInWindow =
    [
        "_New", "New _Workspace…", "---",
        "Open _Workspace…", "Open _Recent", "_Open", "---",
        "{Binding SaveMenuHeader}", "Save Schematic _As…", "Save S_ymbol As…", "Save _Layout As…", "Save Workspace _As…", "---",
        "_Import", "_Export", "---",
        "{Binding CloseWorkspaceOrWindowHeader}", "---",
        "_Settings…", "---",
        "_Quit circuitRF",
    ];

    private static readonly string[] ExpectedTopLevelHeadersNative =
    [
        "New", "New Workspace…", "---",
        "Open Workspace…", "Open Recent", "Open", "---",
        "Save", "Save Schematic As…", "Save Symbol As…", "Save Layout As…", "Save Workspace As…", "---",
        "Import", "Export", "---",
        "{Binding CloseWorkspaceOrWindowHeader}",
    ];

    [Fact]
    public void InWindowFileMenu_TopLevelOrder_MatchesBrief()
    {
        var children = InWindowFileChildren();
        var actual = children.Select(n => n.IsSeparator ? "---" : n.Header).ToArray();
        Assert.Equal(ExpectedTopLevelHeadersInWindow, actual);
    }

    [Fact]
    public void NativeFileMenu_TopLevelOrder_MatchesBrief()
    {
        var children = NativeFileChildren();
        var actual = children.Select(n => n.IsSeparator ? "---" : n.Header).ToArray();
        Assert.Equal(ExpectedTopLevelHeadersNative, actual);
    }

    // §1.1 — New submenu, exact order, both surfaces.
    private static readonly string[] ExpectedNewSubmenuInWindow =
        ["New _Cell…", "New _Schematic", "New S_ymbol", "New _Layout", "New _Data Display", "New _Technology…"];
    private static readonly string[] ExpectedNewSubmenuNative =
        ["New Cell…", "New Schematic", "New Symbol", "New Layout", "New Data Display", "New Technology…"];

    [Fact]
    public void InWindowNewSubmenu_ExactOrder()
    {
        var newItem = FindTopLevel(InWindowFileChildren(), "_New");
        Assert.Equal(ExpectedNewSubmenuInWindow, newItem.Children.Select(c => c.Header).ToArray());
    }

    [Fact]
    public void NativeNewSubmenu_ExactOrder()
    {
        var newItem = FindTopLevel(NativeFileChildren(), "New");
        Assert.Equal(ExpectedNewSubmenuNative, newItem.Children.Select(c => c.Header).ToArray());
    }

    // ── Gate 4 — Open submenu: exactly 4 items; Open Workspace…/Open Recent NOT in it; no separator
    //    among the three top-level items ─────────────────────────────────────────────────────────

    [Fact]
    public void InWindowOpenSubmenu_ExactlyFourItems_InOrder()
    {
        var openItem = FindTopLevel(InWindowFileChildren(), "_Open");
        Assert.Equal(
            ["Open Sc_hematic…", "Open S_ymbol…", "Open _Layout…", "Open Data _Display…"],
            openItem.Children.Select(c => c.Header).ToArray());
        Assert.All(openItem.Children, c => Assert.False(c.IsSeparator));
    }

    [Fact]
    public void NativeOpenSubmenu_ExactlyFourItems_InOrder()
    {
        var openItem = FindTopLevel(NativeFileChildren(), "Open");
        Assert.Equal(
            ["Open Schematic…", "Open Symbol…", "Open Layout…", "Open Data Display…"],
            openItem.Children.Select(c => c.Header).ToArray());
    }

    [Fact]
    public void OpenWorkspaceAndOpenRecent_AreDirectFileChildren_NotInsideOpenSubmenu()
    {
        foreach (var children in new[] { InWindowFileChildren(), NativeFileChildren() })
        {
            // Present directly on File (order already pinned by the top-level order tests above)...
            Assert.Contains(children, n => n.Header.Replace("_", "").Contains("Open Workspace"));
            Assert.Contains(children, n => n.Header.Replace("_", "").Contains("Open Recent"));
            // ...and NOT duplicated inside the Open ▸ submenu.
            var openParent = children.First(n => n.Header is "_Open" or "Open");
            Assert.DoesNotContain(openParent.Children, c => c.Header.Contains("Workspace") || c.Header.Contains("Recent"));
        }
    }

    [Fact]
    public void NoSeparatorBetween_OpenWorkspace_OpenRecent_Open()
    {
        foreach (var children in new[] { InWindowFileChildren(), NativeFileChildren() })
        {
            var idxOpenWs   = IndexOfHeaderContaining(children, "Open Workspace");
            var idxRecent   = IndexOfHeaderContaining(children, "Open Recent");
            var idxOpen     = children.ToList().FindIndex(n => n.Header is "_Open" or "Open");
            // The three must be contiguous, in this order, with nothing (least of all a separator)
            // between them.
            Assert.Equal(idxOpenWs + 1, idxRecent);
            Assert.Equal(idxRecent + 1, idxOpen);
        }
    }

    private static int IndexOfHeaderContaining(IReadOnlyList<MenuNode> nodes, string substring)
        => nodes.ToList().FindIndex(n => n.Header.Replace("_", "").Contains(substring, StringComparison.Ordinal));

    // ── Gate 6a — Import submenu contents: Data/GDSII/DXF/PDK, never Gerber ──────────────────────
    //
    // The count is pinned to the exact expected item SET rather than a bare number, so adding a
    // format has to name it here. Gerber stays absent on purpose — it is export-only (L4c).

    [Fact]
    public void ImportSubmenu_ContainsExpectedFormats_NoGerber()
    {
        string[] expected = ["Data", "GDSII", "DXF", "PDK"];

        foreach (var children in new[] { InWindowFileChildren(), NativeFileChildren() })
        {
            var import = children.First(n => n.Header is "_Import" or "Import");

            var actual = import.Children
                .Select(c => c.Header.Replace("_", "").Replace("…", "").Trim())
                .ToArray();

            Assert.Equal(expected, actual);
            Assert.DoesNotContain(import.Children, c => c.Header.Contains("Gerber", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void ExportSubmenu_ContainsGerber_SameGatingAsGdsiiAndDxf()
    {
        // Phase L4c (brief-L4c-gerber-export.md) implemented Gerber export — the File menu's Gerber
        // entry is gated on an active layout document exactly like GDSII/DXF, not permanently disabled.
        var src = ReadRepoFile(Path.Combine("src", "Ui", "Views", "WorkspaceWindow.axaml"));
        Assert.Contains("ExportGerberCommand", src);
        Assert.DoesNotContain("Gerber export is not yet implemented.", src);
    }

    // ── Gate 5 — ellipsis convention (R-menu-1) ───────────────────────────────────────────────────

    public static IEnumerable<object[]> EllipsisCases()
    {
        // (header substring unique enough to find it, expectEllipsis)
        yield return ["New _Cell…", true];
        yield return ["New _Schematic", false];
        yield return ["New S_ymbol", false];
        yield return ["New _Layout", false];
        yield return ["New _Data Display", false];
        yield return ["New _Technology…", true];
        yield return ["New _Workspace…", true];
        yield return ["Open _Workspace…", true];
        yield return ["Open _Recent", false];
        yield return ["_Open", false];
        yield return ["_New", false];
        yield return ["Open Sc_hematic…", true];
        yield return ["Open S_ymbol…", true];
        yield return ["Open _Layout…", true];
        yield return ["Open Data _Display…", true];
        yield return ["Save Schematic _As…", true];
        yield return ["Save S_ymbol As…", true];
        yield return ["Save _Layout As…", true];
        yield return ["Save Workspace _As…", true];
        yield return ["_Import", false];
        yield return ["_Export", false];
        yield return ["_Settings…", true];
        yield return ["_Quit circuitRF", false];
    }

    [Theory]
    [MemberData(nameof(EllipsisCases))]
    public void InWindowMenu_EllipsisMatchesRule(string header, bool expectEllipsis)
    {
        var found = FindNodeByHeader(InWindowFileChildren(), header)
                    ?? FindNodeByHeader(InWindowViewChildren(), header);
        Assert.True(found is not null, $"Menu item with header '{header}' was not found.");
        Assert.Equal(expectEllipsis, found!.Header.EndsWith('…'));
    }

    /// <summary>`Save` (SaveMenuHeader) and `Close Workspace`/`Close Window` (CloseWorkspaceOrWindowHeader)
    /// are computed bindings, not literal XAML text, so the ellipsis rule is checked directly against
    /// their C# source instead of the parsed tree.</summary>
    [Fact]
    public void SaveMenuHeader_NeitherBranchHasEllipsis()
    {
        var src = ReadRepoFile(Path.Combine("src", "Ui", "ViewModels", "WorkspaceViewModel.cs"));
        Assert.Contains("public string SaveMenuHeader => ActiveSaveScope == SaveScope.SingleDoc ? \"Save\" : \"Save All\";", src);
    }

    [Fact]
    public void CloseWorkspaceOrWindowHeader_NeitherBranchHasEllipsis()
    {
        // R-menu-1: this item acts directly (the unsaved-changes prompt is a consequence of dirty
        // state, not an input the command needs), so neither branch takes an ellipsis.
        //
        // The CONDITION moved from `_focusedWindowDocument is not null` to `ClosesASingleDocumentWindow`
        // when a focused floating TOOL window was made to read "Close Workspace" (a tool panel belongs
        // to the workspace, not to a document) — see DockWindowBehaviourTests. The two LABELS, which
        // are what this test is actually about, are unchanged.
        var src = ReadRepoFile(Path.Combine("src", "Ui", "ViewModels", "WorkspaceViewModel.cs"));
        Assert.Contains("=> ClosesASingleDocumentWindow ? \"Close Window\" : \"Close Workspace\";", src);
    }

    private static MenuNode? FindNodeByHeader(IReadOnlyList<MenuNode> nodes, string header)
    {
        foreach (var n in nodes)
        {
            if (n.Header == header) return n;
            var inChildren = FindNodeByHeader(n.Children, header);
            if (inChildren is not null) return inChildren;
        }
        return null;
    }

    private static IReadOnlyList<MenuNode> InWindowViewChildren()
    {
        var root = LoadWorkspaceWindowXaml();
        var menu = root.Descendants().First(e => e.Name.LocalName == "Menu" && e.Parent?.Name.LocalName != "NativeMenuItem.Menu");
        var top  = ExtractMenuItems(menu);
        return FindTopLevel(top, "_View").Children;
    }

    // ── Gate 7 — no adjacent separators (R-menu-2), including the Open Recent-empty case ─────────
    //    Open Recent is disabled-not-hidden (never conditionally removed from the tree), so the
    //    static markup is the whole story here — no dynamic collapse logic exists to defeat.

    [Fact]
    public void FileMenu_NeverHasAdjacentSeparators_BothSurfaces()
    {
        foreach (var children in new[] { InWindowFileChildren(), NativeFileChildren() })
            for (var i = 1; i < children.Count; i++)
                Assert.False(children[i - 1].IsSeparator && children[i].IsSeparator,
                    $"Adjacent separators at index {i - 1}/{i}.");
    }

    // ── Gate 8 — View menu no longer duplicates "Open Symbol Editor…"; File ▸ Open ▸ Open Symbol…
    //    still exists (checked above by NativeOpenSubmenu/InWindowOpenSubmenu already asserting it) ──

    [Fact]
    public void ViewMenu_NoLongerContainsOpenSymbolEditorItems_EitherSurface()
    {
        var src = ReadRepoFile(Path.Combine("src", "Ui", "Views", "WorkspaceWindow.axaml"));
        Assert.DoesNotContain("Open Symbol Editor", src);
    }

    // ── Gate 9 — accelerators preserved, even where an item moved into a submenu ──────────────────

    [Fact]
    public void Accelerators_PreservedOnMovedItems()
    {
        var src = ReadRepoFile(Path.Combine("src", "Ui", "Views", "WorkspaceWindow.axaml"));
        Assert.Contains("InputGesture=\"Ctrl+N\"", src);           // New Workspace…
        // Ctrl+Shift+N moved from New Schematic to New Cell (brief-cell-first-and-ui-fixes.md R-cc-2,
        // which explicitly supersedes this brief's own accelerator-preservation rule for this one binding).
        Assert.Contains("InputGesture=\"Ctrl+Shift+N\"", src);     // New Cell… (now in New ▸)
        Assert.Contains("InputGesture=\"Ctrl+Shift+D\"", src);     // New Data Display (now in New ▸)
        Assert.Contains("InputGesture=\"Ctrl+O\"", src);           // Open Workspace…
        Assert.Contains("InputGesture=\"Ctrl+S\"", src);           // Save
        Assert.Contains("InputGesture=\"Ctrl+Shift+S\"", src);     // Save Workspace As…
        Assert.Contains("Gesture=\"Meta+N\"", src);
        Assert.Contains("Gesture=\"Meta+Shift+N\"", src);
        Assert.Contains("Gesture=\"Meta+Shift+D\"", src);
        Assert.Contains("Gesture=\"Meta+O\"", src);
        Assert.Contains("Gesture=\"Meta+S\"", src);
        Assert.Contains("Gesture=\"Meta+Shift+S\"", src);
    }

    // ── §1's own separator ambiguity — resolved as TWO groups (Save, then Import/Export), per the
    //    brief's own assumed default. Pinned directly so a future edit can't silently merge them. ──

    [Fact]
    public void SaveGroup_AndImportExportGroup_AreTwoSeparateGroups_NotMerged()
    {
        foreach (var children in new[] { InWindowFileChildren(), NativeFileChildren() })
        {
            var lastSaveIdx   = children.ToList().FindLastIndex(n => n.Header.Contains("Save"));
            var importIdx     = children.ToList().FindIndex(n => n.Header is "_Import" or "Import");
            Assert.True(importIdx > lastSaveIdx);
            Assert.True(children[lastSaveIdx + 1].IsSeparator,
                "Expected a separator between the Save group and the Import/Export group (two groups, not one).");
        }
    }

    // ── Gate 3 — New Symbol routes through the existing scratch-symbol creation path ─────────────

    [Fact]
    public void NewSymbolCommand_ReusesExistingScratchCreationPath_NotASecondOne()
    {
        var src = ReadRepoFile(Path.Combine("src", "Ui", "ViewModels", "WorkspaceViewModel.cs"));

        // Exactly one scratch-symbol creation method exists, and it carries [RelayCommand] (so the
        // menu command IS that method, not a wrapper calling a copy of it).
        var occurrences = System.Text.RegularExpressions.Regex.Matches(src, @"private\s+void\s+NewScratchSymbol\(\)").Count;
        Assert.Equal(1, occurrences);
        Assert.Contains("[RelayCommand]\n    private void NewScratchSymbol()", src);

        // The on-launch path (New Symbol at startup) calls the SAME method.
        Assert.Contains("case LaunchAction.NewSymbol:", src);
        var launchSection = src[src.IndexOf("case LaunchAction.NewSymbol:", StringComparison.Ordinal)..];
        launchSection = launchSection[..launchSection.IndexOf("break;", StringComparison.Ordinal)];
        Assert.Contains("NewScratchSymbol();", launchSection);
    }

    // ── R-menu-4 core mechanism — FindAnyDocumentInDock ────────────────────────────────────────────
    // The pure, Window-independent half of the per-window active-document resolution. Constructed
    // directly against real Dock.Model.Mvvm.Controls types (RootDock/DocumentDock/Tool), which — like
    // SchematicDocument/SymbolEditorDocument — are plain C# and need no Avalonia platform.

    [Fact]
    public void FindAnyDocumentInDock_ActiveDockableIsADocument_ReturnsItDirectly()
    {
        var vm  = new SchematicViewModel(new SchematicEditModel());
        var doc = new SchematicDocument("Amp", vm);
        var root = new RootDock { ActiveDockable = doc, VisibleDockables = new List<IDockable> { doc } };

        var found = WorkspaceViewModel.FindAnyDocumentInDock(root);

        Assert.Same(doc, found);
    }

    [Fact]
    public void FindAnyDocumentInDock_ActiveDockableIsATool_FallsThroughToADocumentInVisibleDockables()
    {
        var vm  = new SchematicViewModel(new SchematicEditModel());
        var doc = new SchematicDocument("Amp", vm);
        var tool = new Tool();
        var root = new RootDock { ActiveDockable = tool, VisibleDockables = new List<IDockable> { tool, doc } };

        var found = WorkspaceViewModel.FindAnyDocumentInDock(root);

        Assert.Same(doc, found);
    }

    [Fact]
    public void FindAnyDocumentInDock_OnlyToolsPresent_ReturnsNull()
    {
        var tool = new Tool();
        var root = new RootDock { ActiveDockable = tool, VisibleDockables = new List<IDockable> { tool } };

        var found = WorkspaceViewModel.FindAnyDocumentInDock(root);

        Assert.Null(found);
    }

    [Fact]
    public void FindAnyDocumentInDock_RecursesThroughNestedDock_FindsTheDocument()
    {
        var vm  = new SchematicViewModel(new SchematicEditModel());
        var doc = new SchematicDocument("Amp", vm);
        var innerDocumentDock = new DocumentDock { VisibleDockables = new List<IDockable> { doc } };
        var root = new RootDock { VisibleDockables = new List<IDockable> { innerDocumentDock } };

        var found = WorkspaceViewModel.FindAnyDocumentInDock(root);

        Assert.Same(doc, found);
    }

    // ── SymbolEditorDocument.OnSavedAs — Save Symbol As… on an already-materialized document ────────
    // The genuine gap this brief closed: unlike LayoutDocument/SchematicDocument, SymbolEditorDocument
    // had Materialize (one-way, scratch→materialized) but nothing repeatable for "already materialized,
    // save to a NEW path." Mirrors LayoutDocument.OnSavedAs's exact shape.

    [Fact]
    public void SymbolEditorDocument_OnSavedAs_UpdatesFilePathTitleAndId()
    {
        var editable = new EditableSymbol { UserEditable = true };
        var vm       = new SymbolEditorViewModel(editable);
        var doc      = new SymbolEditorDocument("OldName", vm);
        doc.Materialize("/tmp/OldName.csym");

        doc.OnSavedAs("/tmp/NewName.csym", "NewName");

        Assert.Equal("/tmp/NewName.csym", doc.FilePath);
        Assert.Equal("/tmp/NewName.csym", vm.CurrentSymbolPath);
        Assert.Equal("NewName", doc.Id);
        Assert.Equal("NewName", doc.Title);
        Assert.False(doc.IsDirty);
    }

    [Fact]
    public void SymbolEditorDocument_OnSavedAs_CanBeCalledRepeatedly()
    {
        var editable = new EditableSymbol { UserEditable = true };
        var vm       = new SymbolEditorViewModel(editable);
        var doc      = new SymbolEditorDocument("A", vm);
        doc.Materialize("/tmp/A.csym");

        doc.OnSavedAs("/tmp/B.csym", "B");
        doc.OnSavedAs("/tmp/C.csym", "C");

        Assert.Equal("/tmp/C.csym", doc.FilePath);
        Assert.Equal("C", doc.Id);
        Assert.Equal("C", doc.Title);
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
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
        // MW1 §1: New Window sits directly under New Workspace…, and Open Workspace in New
        // Window… directly under its own companion — the two gestures that create a SECOND
        // workspace window, each beside the one-window form it mirrors.
        "_New", "New _Workspace…", "New Win_dow", "---",
        "Open _Workspace…", "Open Workspace in _New Window…", "Open _Recent", "_Open", "---",
        "{Binding SaveMenuHeader}", "Save Schematic _As…", "Save S_ymbol As…", "Save _Layout As…", "Save Workspace _As…", "---",
        // Sharing a workspace with someone on another machine (owner request, 2026-08-15) — placed
        // under Save Workspace As… behind its own separator, because it is a different KIND of
        // save: it writes one portable file, not the workspace itself.
        // MW2 §2.1: Reference Workspace… is its own band above the archive pair. It is neither a
        // save nor a share — it is a change to what this workspace can REACH — and putting it beside
        // Archive would read as a third way of packaging one.
        // MW3: Add Cell to Workspace… sits directly above it, in the same band — the two are the same
        // subject (what this workspace can reach), and the drag gesture's dialog offers both outcomes.
        "Add _Cell to Workspace…", "_Reference Workspace…", "---",
        "_Archive Workspace…", "_Unarchive Workspace…", "---",
        "_Import", "_Export", "_Manage PDKs…", "---",
        // "Close Workspace Window", not "Close Window": that name is already taken by the item
        // above, which closes the active DOCUMENT tab (MW1 §1's own deviation, recorded there).
        "Close _Window", "Close Wor_kspace", "Close Workspace Window", "---",
        "_Settings…", "---",
        "_Quit circuitRF",
    ];

    private static readonly string[] ExpectedTopLevelHeadersNative =
    [
        "New", "New Workspace…", "New Window", "---",
        "Open Workspace…", "Open Workspace in New Window…", "Open Recent", "Open", "---",
        "Save", "Save Schematic As…", "Save Symbol As…", "Save Layout As…", "Save Workspace As…", "---",
        "Add Cell to Workspace…", "Reference Workspace…", "---",
        "Archive Workspace…", "Unarchive Workspace…", "---",
        "Import", "Export", "Manage PDKs…", "---",
        "Close Window", "Close Workspace", "Close Workspace Window",
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
    // brief-L6-L7-em-ui.md D1/R-em-9 added "New EM Setup…" after "New Technology…" — a .cem is a
    // workspace-scoped document alongside the technology it reads, so it belongs beside it. These
    // two lists stay EXACT and ORDERED on purpose: that is what keeps the hand-mirrored in-window
    // and macOS menus from drifting apart, so an addition updates them rather than loosening them.
    private static readonly string[] ExpectedNewSubmenuInWindow =
        ["New _Cell…", "New _Schematic", "New S_ymbol", "New _Layout", "New _Data Display",
         "New _Technology…", "New _EM Setup…"];
    private static readonly string[] ExpectedNewSubmenuNative =
        ["New Cell…", "New Schematic", "New Symbol", "New Layout", "New Data Display",
         "New Technology…", "New EM Setup…"];

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

    // ── Gate 4 — Open submenu: exactly its document types (five since Open wBond… was withdrawn for
    //    v1 — see the entry-point test below); Open Workspace…/Open Recent NOT in it; no separator
    //    among the three top-level items ─────────────────────────────────────────────────────────

    [Fact]
    public void InWindowOpenSubmenu_ExactItemsInOrder()
    {
        var openItem = FindTopLevel(InWindowFileChildren(), "_Open");
        Assert.Equal(
            ["Open Sc_hematic…", "Open _Technology…", "Open S_ymbol…", "Open _Layout…", "Open Data _Display…"],
            openItem.Children.Select(c => c.Header).ToArray());
        Assert.All(openItem.Children, c => Assert.False(c.IsSeparator));
    }

    [Fact]
    public void NativeOpenSubmenu_ExactItemsInOrder()
    {
        var openItem = FindTopLevel(NativeFileChildren(), "Open");
        Assert.Equal(
            ["Open Schematic…", "Open Technology…", "Open Symbol…", "Open Layout…", "Open Data Display…"],
            openItem.Children.Select(c => c.Header).ToArray());
    }

    /// <summary>
    /// <b>Nothing in this window opens the standalone wirebond WINDOW</b> — it is a v2 feature (owner,
    /// 2026-08-17). Both of its entry points are commented out: Tools ▸ wBond, and File ▸ Open ▸
    /// Open wBond….
    ///
    /// <para><b>Asserted on the COMMAND, not on the header</b>, because the header is not the property
    /// being kept. The exact-order tests above would still pass if the item came back under a different
    /// spelling or in another submenu, and a header scan would misfire on File ▸ Import ▸ Wirebond
    /// Wires… the day someone respells it — that one and Wirebond as Cell… are the COMPONENT path and
    /// must keep working. A binding to one of these two commands is what "a way into that window
    /// exists" actually means.</para>
    ///
    /// <para><c>XDocument</c> parses a comment as <c>XComment</c>, never as an element or an attribute,
    /// so a commented-out item is genuinely invisible here — this test cannot be satisfied by
    /// commenting the binding out halfway. The commands and the whole <c>.wBond</c> document type stay
    /// in place; only the way in is deferred.</para>
    /// </summary>
    [Fact]
    public void NeitherEntryPointToTheStandaloneWBondWindow_IsWiredUp_WhileItIsDeferredToV2()
    {
        var bound = LoadWorkspaceWindowXaml()
            .DescendantsAndSelf()
            .SelectMany(e => e.Attributes())
            .Select(a => a.Value)
            .ToList();

        Assert.DoesNotContain(bound, v => v.Contains("NewWBondCommand", StringComparison.Ordinal));
        Assert.DoesNotContain(bound, v => v.Contains("OpenWBondFileCommand", StringComparison.Ordinal));

        // …and the component path is untouched, so this is a withdrawal of one window and not of
        // wirebonds. Both are File ▸ Import children.
        Assert.Contains(bound, v => v.Contains("ImportWirebondWiresCommand", StringComparison.Ordinal));
        Assert.Contains(bound, v => v.Contains("ImportWirebondAsCellCommand", StringComparison.Ordinal));
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

            // The Open band runs unbroken from the first Open Workspace item to Open — no separator
            // anywhere inside it. Open Workspace… now has a companion (…in New Window…, MW1 §1)
            // sitting between it and Open Recent, which is another OPEN item, not a break in the
            // band; Open Recent and Open stay adjacent.
            Assert.True(idxOpenWs >= 0 && idxRecent > idxOpenWs && idxOpen == idxRecent + 1);
            for (int i = idxOpenWs; i <= idxOpen; i++)
                Assert.False(children[i].IsSeparator, "The Open band must contain no separator.");
        }
    }

    private static int IndexOfHeaderContaining(IReadOnlyList<MenuNode> nodes, string substring)
        => nodes.ToList().FindIndex(n => n.Header.Replace("_", "").Contains(substring, StringComparison.Ordinal));

    // ── Gate 6a — Import submenu contents: Data/GDSII/DXF/Board/Gerber/PDK ───────────────────────
    //
    // The count is pinned to the exact expected item SET rather than a bare number, so adding a
    // format has to name it here.

    [Fact]
    public void ImportSubmenu_ContainsExpectedFormats()
    {
        // "Technology" joined with C0 (process technology import); "Into Open Technology" with the
        // technology-merge work, which is a genuinely different destination — one creates a `.ctech`,
        // the other merges into the one being edited. The assertion stays exact
        // and ordered rather than loosened to "contains" — this is what keeps the in-window Menu and
        // the macOS NativeMenu, which are hand-mirrored, from drifting apart.
        // "Wirebond Wires" / "Wirebond as Cell" joined with WB-B2's embedded-design model: a placed
        // wBond carries its own wires, so a `.wBond` reaches a schematic by IMPORT rather than by
        // reference. The two are separate entries because they have different destinations — one
        // replaces a component's wires, the other unpacks the design's layout artwork as a new cell.
        // "Board" (L4d) sits with the other geometry importers and has NO Export counterpart, by
        // design — brief-L4d-kicad-pcb-import.md §1: writing a board file means authoring board-setup
        // and design-rule state circuitRF has no opinion about, and Export DXF already serves the
        // outward handoff. ExportSubmenu_* below is what holds that asymmetry shut.
        string[] expected =
        [
            // "Model or Subcircuit" sits beside "PDK" and not with the geometry importers above it:
            // both bring in a DEVICE MODEL, and a user looking for one is looking at the other.
            // "Gerber" (L4h) joined the geometry importers when Gerber stopped being export-only. It
            // sits after Board because the two are the same kind of thing — a whole fabricated board
            // arriving as one flat cell — and because the export side already lists them in that order.
            "Data", "GDSII", "DXF", "Board", "Gerber", "PDK", "Model or Subcircuit", "Technology", "Into Open Technology",
            "Wirebond Table", "Wirebond Wires", "Wirebond as Cell",
        ];

        foreach (var children in new[] { InWindowFileChildren(), NativeFileChildren() })
        {
            var import = children.First(n => n.Header is "_Import" or "Import");

            var actual = import.Children
                .Select(c => c.Header.Replace("_", "").Replace("…", "").Trim())
                .ToArray();

            Assert.Equal(expected, actual);
        }
    }

    /// <summary>
    /// Gate 2 of brief-L4h: <b>File ▸ Import ▸ Gerber…</b> exists on every surface, is bound to a real
    /// command, and is gated on the same condition as the other import commands. The header assertion
    /// above covers the two menus this file parses as XML; this adds the third surface — a torn-off
    /// document window's own File menu, which is hand-mirrored and has drifted before — and the
    /// enablement, which no header scan can see.
    /// </summary>
    [Fact]
    public void ImportGerber_IsOnEverySurface_AndGatedOnAnOpenWorkspace()
    {
        foreach (var view in (string[])
                 [
                     Path.Combine("src", "Ui", "Views", "WorkspaceWindow.axaml"),
                     Path.Combine("src", "Ui", "Views", "Shared", "TornOffFileMenuView.axaml"),
                 ])
            Assert.Contains("ImportGerberCommand", ReadRepoFile(view));

        var vm = StripComments(ReadRepoFile(Path.Combine("src", "Ui", "ViewModels", "WorkspaceViewModel.cs")));
        Assert.Contains("private bool CanImportGerber() => CurrentWorkspacePath is not null;", vm);

        // The re-notification trap EveryWorkspaceGatedCommand_IsRenotifiedWhenTheWorkspaceChanges
        // covers generically — asserted by name too, because this is the entry the phase adds.
        Assert.Contains("ImportGerberCommand.NotifyCanExecuteChanged()", vm);
    }

    /// <summary>
    /// <b>Every command gated on "a workspace is open" must be re-evaluated when that changes.</b>
    ///
    /// <para>A missing <c>NotifyCanExecuteChanged()</c> is invisible in every automated check that
    /// exists: the command is declared, its predicate is correct, the menu entry binds to it, and the
    /// XAML tests above all pass — the entry is simply greyed out forever, because
    /// <c>CanExecute</c> was evaluated once at construction (when no workspace was open) and never
    /// again. Caught by the owner clicking File → Import → Board on the day L4d landed; this test is
    /// what makes the next one caught by the suite instead.</para>
    ///
    /// <para>Written as a scan over the source rather than against a live view model because
    /// <c>WorkspaceViewModel</c> needs an Avalonia application to construct, which these tests
    /// deliberately never do. Comments are stripped first — a command name mentioned only in prose is
    /// not a notification (the H8 lesson).</para>
    /// </summary>
    [Fact]
    public void EveryWorkspaceGatedCommand_IsRenotifiedWhenTheWorkspaceChanges()
    {
        var src = StripComments(ReadRepoFile(Path.Combine("src", "Ui", "ViewModels", "WorkspaceViewModel.cs")));

        // The body of OnCurrentWorkspacePathChanged — the one place these notifications live.
        int start = src.IndexOf("partial void OnCurrentWorkspacePathChanged(", StringComparison.Ordinal);
        Assert.True(start >= 0, "OnCurrentWorkspacePathChanged not found");
        int end = src.IndexOf("\n    }", start, StringComparison.Ordinal);
        var handler = src[start..(end < 0 ? src.Length : end)];

        // Predicates whose whole body is "a workspace is open".
        var gated = Regex.Matches(src, @"private bool (\w+)\(\)\s*=>\s*CurrentWorkspacePath is not null;")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(gated);

        // Each [RelayCommand(CanExecute = nameof(PRED))] and the method it decorates.
        var missing = new List<string>();
        foreach (System.Text.RegularExpressions.Match m in Regex.Matches(src,
            @"\[RelayCommand\(CanExecute = nameof\((\w+)\)\)\]\s*private\s+(?:async\s+)?[\w<>?.]+\s+(\w+)\("))
        {
            if (!gated.Contains(m.Groups[1].Value)) continue;
            string command = m.Groups[2].Value + "Command";
            if (!handler.Contains(command + ".NotifyCanExecuteChanged()", StringComparison.Ordinal))
                missing.Add(command);
        }

        Assert.True(missing.Count == 0,
            "These commands are gated on an open workspace but are never re-evaluated when one opens, " +
            "so their menu entries stay greyed out forever: " + string.Join(", ", missing));
    }

    /// <summary>
    /// The same class of bug as the test above, for the OTHER gate: a command gated on which DOCUMENT
    /// is active must be re-notified from BOTH fan-outs.
    ///
    /// <para>The view model's own source calls this a "standing gotcha" in as many words, twice — and
    /// it had bitten twice anyway: <c>ExportGerberCommand</c> was in neither fan-out since L4c, so
    /// File → Export → Gerber had been greyed out permanently, and <c>ExportBoardCommand</c> would have
    /// joined it. A comment warning about a trap is not a test; this is.</para>
    /// </summary>
    [Fact]
    public void EveryDocumentGatedCommand_IsRenotifiedFromBothFanOuts()
    {
        var src = StripComments(ReadRepoFile(Path.Combine("src", "Ui", "ViewModels", "WorkspaceViewModel.cs")));

        // Predicates that ask which document is active. Deliberately only the layout one: the others
        // (schematic, symbol) have their own smaller fan-outs and are not this test's subject.
        const string predicate = "IsLayoutDocumentActive";

        var commands = Regex.Matches(src,
                @"\[RelayCommand\(CanExecute = nameof\(" + predicate + @"\)\)\]\s*private\s+(?:async\s+)?[\w<>?.]+\s+(\w+)\(")
            .Select(m => m.Groups[1].Value + "Command")
            .Distinct(StringComparer.Ordinal)
            .ToList();
        Assert.NotEmpty(commands);

        var missing = commands
            .Where(c => CountOccurrences(src, c + ".NotifyCanExecuteChanged()") < 2)
            .ToList();

        Assert.True(missing.Count == 0,
            "These commands are gated on the active document but are not re-evaluated from both " +
            "fan-outs, so their menu entries stay stuck at whatever they were on construction: " +
            string.Join(", ", missing));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { count++; i += needle.Length; }
        return count;
    }

    /// <summary>Line and block comments removed, so a scan sees code and not prose.</summary>
    private static string StripComments(string src)
        => Regex.Replace(Regex.Replace(src, @"/\*.*?\*/", "", RegexOptions.Singleline), @"//[^\n]*", "");

    [Fact]
    public void ExportSubmenu_ContainsGerber_SameGatingAsGdsiiAndDxf()
    {
        // Phase L4c (brief-L4c-gerber-export.md) implemented Gerber export — the File menu's Gerber
        // entry is gated on an active layout document exactly like GDSII/DXF, not permanently disabled.
        var src = ReadRepoFile(Path.Combine("src", "Ui", "Views", "WorkspaceWindow.axaml"));
        Assert.Contains("ExportGerberCommand", src);
        Assert.DoesNotContain("Gerber export is not yet implemented.", src);

        // Board export (the L4d follow-up) sits alongside them under the same gate.
        Assert.Contains("ExportBoardCommand", src);
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

    /// <summary>`Save` (SaveMenuHeader) is a computed binding, not literal XAML text, so the ellipsis
    /// rule is checked directly against its C# source instead of the parsed tree. (`Close Window` and
    /// `Close Workspace` used to be a second such binding; since 2026-08-25 they are two literal items
    /// and are covered by the parsed-tree cases above.)</summary>
    [Fact]
    public void SaveMenuHeader_NeitherBranchHasEllipsis()
    {
        var src = ReadRepoFile(Path.Combine("src", "Ui", "ViewModels", "WorkspaceViewModel.cs"));
        Assert.Contains("public string SaveMenuHeader => ActiveSaveScope == SaveScope.SingleDoc ? \"Save\" : \"Save All\";", src);
    }

    /// <summary>
    /// R-menu-1: both Close items act directly (the unsaved-changes prompt is a consequence of dirty
    /// state, not an input the command needs), so neither takes an ellipsis. Owner request 2026-08-25
    /// split the former single dynamic-header item into two literal ones — `Close Window` (Ctrl+W /
    /// Cmd+W, closes the active DOCUMENT) above `Close Workspace` (unconditionally the whole-workspace
    /// teardown) — which is why this is now a header check on the parsed tree rather than on C# source.
    /// </summary>
    [Theory]
    [InlineData("Close _Window")]
    [InlineData("Close Wor_kspace")]
    public void CloseItems_TakeNoEllipsis(string header)
    {
        var found = FindNodeByHeader(InWindowFileChildren(), header);
        Assert.True(found is not null, $"Menu item with header '{header}' was not found.");
        Assert.False(found!.Header.EndsWith('…'));
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

    /// <summary>
    /// Manage PDKs is Ctrl/⌘+P, on every surface. The in-window menu shows the gesture but does not
    /// fire it — a window-level <c>KeyBinding</c> does — so an item carrying only the display string
    /// looks bound and is not. Both halves are pinned.
    ///
    /// <para>P was free: the schematic canvas's bare <c>P</c> (place Pin) sits behind a modifier guard,
    /// so a modified P never reaches it. It IS conventionally Print elsewhere, and is taken here only
    /// because circuitRF has no Print command to collide with.</para>
    /// </summary>
    [Fact]
    public void ManagePdks_IsBoundToCtrlP_OnEverySurface()
    {
        var src  = ReadRepoFile(Path.Combine("src", "Ui", "Views", "WorkspaceWindow.axaml"));
        var torn = ReadRepoFile(Path.Combine("src", "Ui", "Views", "Shared", "TornOffFileMenuView.axaml"));

        Assert.Contains("InputGesture=\"Ctrl+P\"", src);    // shown in the in-window menu
        Assert.Contains("Gesture=\"Ctrl+P\"", src);         // ...and actually bound
        Assert.Contains("Gesture=\"Meta+P\"", src);         // macOS: native menu item + key binding
        Assert.Contains("InputGesture=\"Ctrl+P\"", torn);
    }

    /// <summary>
    /// Owner report, 2026-08-29: a FLOATING layout document did not save on ⌘S — the layout editor's
    /// geometry-snap toggle ran instead.
    ///
    /// <para>Two halves, and this is the one that makes Save work: Ctrl/Meta+S was bound on
    /// WorkspaceWindow only, plus (by two views that had already hit this) on DataDisplayView and
    /// EmSetupEditorView themselves. A torn-off document is a separate TopLevel that shares none of
    /// the shell's KeyBindings, so a floating .clay — and a floating schematic, symbol or technology
    /// — had no Save gesture at all. Injected in <c>WireWindowUndo</c> beside the Ctrl+W the same
    /// window already gets, rather than on each view, because the gap was every document's, not one
    /// view's; the window is passed as the CommandParameter so the command's own per-window
    /// resolution (R-menu-4) names the document that window is showing.</para>
    ///
    /// <para>The two views that bind Save themselves are unaffected: Avalonia walks KeyBindings from
    /// the focused element UP to the root and stops at the first that handles, so a binding on the
    /// view is reached before the window's.</para>
    /// </summary>
    [Fact]
    public void Save_IsBoundOnTornOffDocumentWindows_NotOnlyOnTheShell()
    {
        var vm = ReadRepoFile(Path.Combine("src", "Ui", "ViewModels", "WorkspaceViewModel.cs"));

        Assert.Contains("new KeyGesture(Key.S, KeyModifiers.Control)", vm);
        Assert.Contains("new KeyGesture(Key.S, KeyModifiers.Meta)", vm);

        // Both must reach the same command the shell's own Ctrl+S runs, with THIS window handed to it
        // — the injected binding lives in the same method as the Ctrl+W one, so scan that method only.
        int start = vm.IndexOf("private void WireWindowUndo(", StringComparison.Ordinal);
        Assert.True(start > 0, "WireWindowUndo is gone — the torn-off window key injection moved");
        int end = vm.IndexOf("// ---- Tab-close prompt", start, StringComparison.Ordinal);
        string method = vm[start..end];

        foreach (var modifier in (string[])["Control", "Meta"])
            Assert.Matches(
                $@"new KeyGesture\(Key\.S, KeyModifiers\.{modifier}\),\s*Command = SaveAllDocumentsCommand, CommandParameter = window",
                method);
    }

    /// <summary>
    /// Owner request, 2026-08-25: <b>Close Window</b> sits directly above <b>Close Workspace</b> and
    /// carries Ctrl+W / ⌘+W, on all three platforms.
    ///
    /// <para>The same in-window trap the Manage PDKs gate above exists for: a <c>MenuItem</c>'s
    /// <c>InputGesture</c> is DISPLAY ONLY in Avalonia — an item carrying just that string looks bound
    /// and is not — so the window-level <c>KeyBinding</c> is pinned beside it. Windows/Linux read the
    /// in-window Menu (and, in a torn-off document window, TornOffFileMenuView, whose live key comes
    /// from the KeyBinding <c>WireWindowUndo</c> injects); macOS reads the app-global NativeMenu, whose
    /// item's own <c>Gesture</c> is the real accelerator there.</para>
    /// </summary>
    [Fact]
    public void CloseWindow_IsBoundToCtrlW_OnEverySurface_AndSitsAboveCloseWorkspace()
    {
        var src  = ReadRepoFile(Path.Combine("src", "Ui", "Views", "WorkspaceWindow.axaml"));
        var torn = ReadRepoFile(Path.Combine("src", "Ui", "Views", "Shared", "TornOffFileMenuView.axaml"));
        var vm   = ReadRepoFile(Path.Combine("src", "Ui", "ViewModels", "WorkspaceViewModel.cs"));

        Assert.Contains("InputGesture=\"Ctrl+W\"", src);     // shown in the in-window menu
        Assert.Contains("Gesture=\"Ctrl+W\"", src);          // ...and actually bound
        Assert.Contains("Gesture=\"Meta+W\"", src);          // macOS: native menu item + key binding
        Assert.Contains("InputGesture=\"Ctrl+W\"", torn);

        // A torn-off document window is a separate TopLevel with no share of the shell's KeyBindings,
        // so Windows/Linux need the key injected there too (macOS's NativeMenu is app-global).
        Assert.Contains("new KeyGesture(Key.W, KeyModifiers.Control)", vm);
        Assert.Contains("new KeyGesture(Key.W, KeyModifiers.Meta)", vm);

        foreach (var (children, window, workspace) in new[]
                 {
                     (InWindowFileChildren(), "Close _Window", "Close Wor_kspace"),
                     (NativeFileChildren(),   "Close Window",  "Close Workspace"),
                 })
        {
            var list = children.ToList();
            var wIdx = list.FindIndex(n => n.Header == window);
            var kIdx = list.FindIndex(n => n.Header == workspace);
            Assert.True(wIdx >= 0, $"'{window}' not found.");
            Assert.True(kIdx >= 0, $"'{workspace}' not found.");
            Assert.Equal(wIdx + 1, kIdx);
        }
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
        Assert.Equal("NewName", doc.Id);           // Id is the stem — picker name, cell name
        Assert.Equal("NewName.csym", doc.Title);   // …the TAB reads the file name, as a re-open does
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
        Assert.Equal("C.csym", doc.Title);
    }
}

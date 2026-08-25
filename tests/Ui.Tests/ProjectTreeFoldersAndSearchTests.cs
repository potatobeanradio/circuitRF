// ================================================================
//  ProjectTreeFoldersAndSearchTests.cs — owner, 2026-08-25:
//
//    "If user imports a Board file, there are potentially a lot of cells that can get generated
//     (and then populated in the Project Tree). This makes it difficult for user to see their
//     other cells."
//
//  Three answers, none of which needed a new capability in the model layer — cells in a
//  sub-folder ALREADY worked everywhere (WorkspaceScanner recurses, InstanceCellChoices recurses,
//  and a CellRef is a relative path). What was missing was the tree's own affordances:
//    1. an import lands in a folder named after the file (ImportFolder),
//    2. a folder can be made from the tree (CanCreateInside),
//    3. the tree can be searched by name (ProjectTreeFilterState.SearchQuery).
// ================================================================

using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using CircuitRF.Ui.Layout.Interchange;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels.ProjectTree;
using Xunit;

namespace CircuitRF.Ui.Tests;

public class ProjectTreeFoldersAndSearchTests : IDisposable
{
    private readonly string _root;

    public ProjectTreeFoldersAndSearchTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"crftest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string Cell(string relativePath)
    {
        var dir = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ".ccell"),
            $$"""{"format_version":1,"name":"{{Path.GetFileName(dir)}}"}""");
        return dir;
    }

    private ProjectTreeNodeViewModel Tree(ProjectTreeFilterState filter) =>
        new(WorkspaceScanner.Scan(_root), filter);

    // Source scans must run on comment-stripped text: a comment that explains why an API was NOT used
    // reads identically to a use. (Same lesson as harmonicaRF H8's own source-scan tests.)
    private static string StripComments(string src)
    {
        src = System.Text.RegularExpressions.Regex.Replace(src, @"/\*.*?\*/", "",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        return System.Text.RegularExpressions.Regex.Replace(src, @"//[^\n]*", "");
    }

    private static string StripXmlComments(string src) =>
        System.Text.RegularExpressions.Regex.Replace(src, @"<!--.*?-->", "",
            System.Text.RegularExpressions.RegexOptions.Singleline);

    private static string ReadRepoFile(string relativePath, [CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root.");
        return File.ReadAllText(Path.Combine(dir!, relativePath));
    }

    // ── 1. Where an import lands ──────────────────────────────────────────────

    [Fact]
    public void ImportFolder_UsesTheFileName_WhenNothingIsInTheWay()
    {
        Assert.Equal("myboard", ImportFolder.UniqueName(_root, "myboard"));
    }

    // Importing the same board twice must not merge two boards' cells into one folder — that would
    // silently overwrite by cell name, which is the exact failure PcbImport's own content-key
    // de-duplication was added to prevent inside a single import.
    [Fact]
    public void ImportFolder_SuffixesRatherThanReusingAnExistingFolder()
    {
        Directory.CreateDirectory(Path.Combine(_root, "myboard"));
        Assert.Equal("myboard_2", ImportFolder.UniqueName(_root, "myboard"));

        Directory.CreateDirectory(Path.Combine(_root, "myboard_2"));
        Assert.Equal("myboard_3", ImportFolder.UniqueName(_root, "myboard"));
    }

    // The folder is named after a FILE, so the file itself sitting beside it is the ordinary case,
    // not an exotic one — a directory cannot share a name with a file in the same parent.
    [Fact]
    public void ImportFolder_TreatsAFileOfTheSameNameAsTaken()
    {
        File.WriteAllText(Path.Combine(_root, "myboard"), "");
        Assert.Equal("myboard_2", ImportFolder.UniqueName(_root, "myboard"));
    }

    [Fact]
    public void ImportFolder_SanitizesANameThatIsNotASafePathComponent()
    {
        var name = ImportFolder.UniqueName(_root, "rev/2:final");
        Assert.DoesNotContain('/', name);
        Assert.DoesNotContain(':', name);
    }

    // A cancelled import has to leave nothing behind — but a folder that DID get written into is
    // real content and must survive.
    [Fact]
    public void ImportFolder_RemoveIfEmpty_OnlyRemovesAnEmptyFolder()
    {
        var empty = ImportFolder.Create(_root, "cancelled");
        ImportFolder.RemoveIfEmpty(empty);
        Assert.False(Directory.Exists(empty));

        var used = ImportFolder.Create(_root, "kept");
        File.WriteAllText(Path.Combine(used, "something.clay"), "{}");
        ImportFolder.RemoveIfEmpty(used);
        Assert.True(Directory.Exists(used));
    }

    // The whole point of the folder: the cells inside it are still ordinary cells to everything that
    // consumes them. This is the claim the design rests on, so it is measured, not assumed.
    [Fact]
    public void ACellInsideAnImportFolder_IsStillFoundByTheInstanceCellPicker()
    {
        Cell("myboard/R0402");
        Cell("MyAmp");

        var found = CircuitRF.Ui.Layout.InstanceCellChoices.Collect(_root, parentCellDir: null);

        Assert.Contains(found, c => c.AbsoluteCellDir.EndsWith("R0402", StringComparison.Ordinal));
        Assert.Contains(found, c => c.AbsoluteCellDir.EndsWith("MyAmp", StringComparison.Ordinal));
    }

    [Fact]
    public void ACellInsideAnImportFolder_IsRenderedUnderThatFolderInTheTree()
    {
        Cell("myboard/R0402");

        var root = Tree(new ProjectTreeFilterState());
        var folder = root.Children.Single(c => c.Kind == NodeKind.UserFolder);

        Assert.Equal("myboard", folder.Name);
        Assert.Contains(folder.Children, c => c is { Kind: NodeKind.Cell, Name: "R0402" });
    }

    // ── 2. Making a folder from the tree ──────────────────────────────────────

    // A user folder was always scanned and rendered, but nothing could be created in one — so the
    // feature looked absent when only its affordance was.
    [Fact]
    public void CanCreateInside_IsTrueForAUserFolder_NotJustTheWorkspaceRoot()
    {
        Cell("myboard/R0402");

        var root = Tree(new ProjectTreeFilterState());
        var folder = root.Children.Single(c => c.Kind == NodeKind.UserFolder);

        Assert.True(root.CanCreateInside);
        Assert.True(folder.CanCreateInside);
        Assert.False(folder.Children.Single(c => c.Kind == NodeKind.Cell).CanCreateInside);
    }

    [Fact]
    public void TheTreeOffersNewFolder_OnEverythingItOffersNewCellOn()
    {
        var axaml = ReadRepoFile("src/Ui/Views/ProjectTree/ProjectTreeView.axaml");

        Assert.Contains("{Binding NewFolderCommand}", axaml);
        Assert.Contains("{Binding NewFolderInWorkspaceCommand}", axaml);

        // Both create verbs share ONE gate — a node where a cell can be made is a node where a
        // folder can be made, and two separate bindings would be free to drift apart.
        Assert.Contains("Command=\"{Binding NewCellCommand}\"", axaml);
        Assert.DoesNotContain("IsVisible=\"{Binding IsWorkspaceOrLibrary}\"/>\n", axaml);
    }

    // ── 3. Searching the tree by name ─────────────────────────────────────────

    [Fact]
    public void AnEmptySearch_ChangesNothing()
    {
        Cell("MyAmp");
        Cell("Mixer");

        var filter = new ProjectTreeFilterState();
        var root = Tree(filter);
        int before = root.FilteredChildren.Count;

        filter.SearchQuery = "   ";   // whitespace is "no filter", never "match nothing"
        Assert.Equal(before, root.FilteredChildren.Count);
        Assert.False(filter.HasSearchQuery);
    }

    [Fact]
    public void ASearch_KeepsOnlyMatchingCells_CaseInsensitively()
    {
        Cell("MyAmp");
        Cell("Mixer");

        var filter = new ProjectTreeFilterState();
        var root = Tree(filter);

        filter.SearchQuery = "amp";

        var kept = root.FilteredChildren.Select(c => c.Name).ToList();
        Assert.Equal(["MyAmp"], kept);
    }

    // A match inside a folder has to bring its folder with it, or the row cannot be reached.
    [Fact]
    public void ASearch_KeepsTheFoldersOnThePathToAMatch()
    {
        Cell("myboard/R0402");
        Cell("myboard/C0603");
        Cell("MyAmp");

        var filter = new ProjectTreeFilterState();
        var root = Tree(filter);

        filter.SearchQuery = "R0402";

        var folder = root.FilteredChildren.Single();
        Assert.Equal("myboard", folder.Name);
        Assert.Equal(["R0402"], folder.FilteredChildren.Select(c => c.Name));
    }

    // …and a match must still be OPENABLE. A cell's own children are its view folders, named for
    // the views (schematic/symbol/layout) and never for the cell — so filtering them by the same
    // text would show the one row the user searched for and then empty it out.
    [Fact]
    public void ASearch_KeepsEverythingBeneathAMatch_EvenThoughTheNamesDoNotMatch()
    {
        var cellDir = Cell("MyAmp");
        Directory.CreateDirectory(Path.Combine(cellDir, "schematic"));
        File.WriteAllText(Path.Combine(cellDir, "schematic", "MyAmp.csch"), "{}");

        var filter = new ProjectTreeFilterState();
        var root = Tree(filter);

        filter.SearchQuery = "MyAmp";

        var cell = root.FilteredChildren.Single();
        Assert.NotEmpty(cell.FilteredChildren);   // the "schematic" view folder survived
    }

    // Owner, 2026-08-25: "if I enter the project name into the filter search, all the files show up."
    // The root's Name IS the workspace name, and a matched node passes its whole subtree through —
    // so matching the root handed the entire tree a free pass.
    [Fact]
    public void ASearch_ForTheWorkspacesOwnName_DoesNotPassTheWholeTree()
    {
        Cell("MyAmp");
        Cell("Mixer");

        var filter = new ProjectTreeFilterState();
        var root = Tree(filter);
        Assert.Equal(2, root.FilteredChildren.Count);   // fixture precondition

        filter.SearchQuery = root.Name;                 // the workspace folder's own name
        Assert.Empty(root.FilteredChildren);
    }

    // The same bug on a PARTIAL name — the reported case is "type the project name", but any
    // substring of it hit the identical path.
    [Fact]
    public void ASearch_ForPartOfTheWorkspacesName_DoesNotPassTheWholeTree()
    {
        Cell("MyAmp");

        var filter = new ProjectTreeFilterState();
        var root = Tree(filter);

        filter.SearchQuery = root.Name[..6];
        Assert.Empty(root.FilteredChildren);
    }

    // …and the rule the fix must NOT break. A folder the user can SEE still passes its contents
    // through: that is the whole point of typing an import folder's name. The root is excluded
    // because it is invisible and universal, not because subtree pass-through is wrong.
    [Fact]
    public void ASearch_ForAVisibleFoldersName_StillShowsWhatIsInsideIt()
    {
        Cell("myboard/R0402");
        Cell("MyAmp");

        var filter = new ProjectTreeFilterState();
        var root = Tree(filter);

        filter.SearchQuery = "myboard";

        var folder = root.FilteredChildren.Single();
        Assert.Equal("myboard", folder.Name);
        Assert.Equal(["R0402"], folder.FilteredChildren.Select(c => c.Name));
    }

    // ── 3b. What a keystroke costs (owner, 2026-08-25: "search is a little slow") ─────
    //
    //  COUNTERS, not wall-clock. Every number below is a count of work handed to the UI thread, which
    //  is what "slow" actually was — the matching pass itself measured ~1 ms for a 4,221-node tree and
    //  was never the problem.

    private int CountCollectionEvents(ProjectTreeNodeViewModel n, Action bump)
    {
        n.FilteredChildren.CollectionChanged += (_, _) => bump();
        int k = 1;
        foreach (var c in n.Children) k += CountCollectionEvents(c, bump);
        return k;
    }

    // Every node used to Clear() + re-Add its whole filtered set on every keystroke, and
    // ObservableCollection.Clear() raises its Reset EVEN WHEN EMPTY — so every leaf announced a
    // container teardown while its (zero) children could not have changed. Measured at ~16,882
    // notifications per keystroke on a 600-cell workspace.
    [Fact]
    public void AKeystrokeThatChangesNothingVisible_NotifiesNothing()
    {
        for (int i = 0; i < 8; i++) Cell($"board/Part{i}");

        var filter = new ProjectTreeFilterState();
        var root = Tree(filter);
        filter.SearchQuery = "Part";

        int events = 0;
        CountCollectionEvents(root, () => events++);

        // Same visible set, more characters typed — nothing about the tree changed.
        filter.SearchQuery = "Part";
        Assert.Equal(0, events);
    }

    // HasSearchQuery is DERIVED from SearchQuery and raised alongside it. Reacting to both ran the
    // entire filter twice for every keystroke.
    [Fact]
    public void AKeystroke_FiltersTheTreeExactlyOnce()
    {
        Cell("MyAmp");

        var filter = new ProjectTreeFilterState();
        var root = Tree(filter);

        int passes = 0;
        root.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ProjectTreeNodeViewModel.FilteredChildren)) passes++;
        };

        filter.SearchQuery = "zzz";     // MyAmp drops out — a real, single change
        Assert.Equal(1, passes);
    }

    // The auto-expand used to open every node with visible children — which is every node a match
    // passes its subtree through. Typing one character therefore flung the whole tree open, and
    // Avalonia's TreeView does NOT virtualize (its ItemsPanel is a plain StackPanel), so every
    // revealed row builds a real control. Search must REVEAL its matches, not open them wide.
    [Fact]
    public void MatchingAFolder_OpensTheFolder_ButNotEveryCellInsideIt()
    {
        for (int i = 0; i < 5; i++)
        {
            var cd = Cell($"myboard/Part{i}");
            Directory.CreateDirectory(Path.Combine(cd, "layout"));
            File.WriteAllText(Path.Combine(cd, "layout", $"Part{i}.clay"), "{}");
        }

        var filter = new ProjectTreeFilterState();
        var root = Tree(filter);

        filter.SearchQuery = "myboard";

        var folder = root.FilteredChildren.Single();
        Assert.True(folder.IsExpanded);                                  // the match is revealed…
        Assert.All(folder.FilteredChildren, c => Assert.False(c.IsExpanded));  // …not flung open
    }

    // The other half of the same rule: a match six levels down still has its PATH opened, or the row
    // the user searched for is invisible behind a collapsed folder.
    [Fact]
    public void MatchingACellInsideAFolder_OpensThePathDownToIt()
    {
        Cell("myboard/R0402");

        var filter = new ProjectTreeFilterState();
        var root = Tree(filter);

        filter.SearchQuery = "R0402";

        var folder = root.FilteredChildren.Single();
        Assert.True(folder.IsExpanded);
    }

    // Several keystrokes in a burst must cost ONE filter pass, not one each.
    [Fact]
    public void ABurstOfKeystrokes_CollapsesToASingleFilterPass()
    {
        var tool = new CircuitRF.Ui.ViewModels.Dock.ProjectTreeTool();

        Action? queued = null;
        tool.FilterScheduler = a => queued = a;   // stand in for the dispatcher post

        int applied = 0;
        tool.FilterState.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ProjectTreeFilterState.SearchQuery)) applied++;
        };

        tool.SearchText = "m";
        tool.SearchText = "my";
        tool.SearchText = "myb";
        tool.SearchText = "myboard";

        Assert.Equal(0, applied);          // nothing has run yet — the box kept up on its own
        queued!();
        Assert.Equal(1, applied);          // …and the burst cost exactly one pass
        Assert.Equal("myboard", tool.FilterState.SearchQuery);   // against the LATEST text
    }

    // The typed text drives the clear button, so the X tracks the caret rather than the filter pass
    // behind it.
    [Fact]
    public void TheClearButtonFollowsTheTypedText_NotTheAppliedFilter()
    {
        var tool = new CircuitRF.Ui.ViewModels.Dock.ProjectTreeTool();
        tool.FilterScheduler = _ => { };    // never apply

        Assert.False(tool.HasSearchText);
        tool.SearchText = "amp";
        Assert.True(tool.HasSearchText);

        tool.ClearSearchCommand.Execute(null);
        Assert.Equal("", tool.SearchText);
        Assert.False(tool.HasSearchText);
    }

    // ── 3c. The search field is behind a toggle in the header (owner, 2026-08-25) ─────

    [Fact]
    public void TheSearchFieldIsClosedUntilTheMagnifierIsClicked()
    {
        var tool = new CircuitRF.Ui.ViewModels.Dock.ProjectTreeTool();
        Assert.False(tool.IsSearchOpen);

        tool.ToggleSearchCommand.Execute(null);
        Assert.True(tool.IsSearchOpen);

        tool.ToggleSearchCommand.Execute(null);
        Assert.False(tool.IsSearchOpen);
    }

    // The worst state this panel could be in: a filter still applied with nothing on screen to
    // explain it — the tree silently hides cells and the one affordance that would say why has just
    // been put away.
    [Fact]
    public void ClosingTheSearch_ClearsTheQuery()
    {
        var tool = new CircuitRF.Ui.ViewModels.Dock.ProjectTreeTool();
        Action? queued = null;
        tool.FilterScheduler = a => queued = a;

        tool.IsSearchOpen = true;
        tool.SearchText = "amp";
        queued!();
        Assert.Equal("amp", tool.FilterState.SearchQuery);

        tool.IsSearchOpen = false;
        Assert.Equal("", tool.SearchText);
        queued!();
        Assert.Equal("", tool.FilterState.SearchQuery);
        Assert.False(tool.FilterState.HasSearchQuery);
    }

    // EXACTLY TWO ways to collapse the field — Escape and the X — and both are explicit
    // (owner, 2026-08-25, revising an earlier round in which emptying it also closed it).

    // Backspacing to the start of a re-typed query is the common case, and it is not a request to put
    // the field away. Clearing the text and collapsing the field are two different intentions.
    [Fact]
    public void EmptyingTheFieldByHand_LeavesItOpen()
    {
        var tool = new CircuitRF.Ui.ViewModels.Dock.ProjectTreeTool();
        tool.FilterScheduler = a => a();

        tool.IsSearchOpen = true;
        tool.SearchText = "amp";

        tool.SearchText = "";              // backspaced to empty

        Assert.True(tool.IsSearchOpen);                          // …the field stays
        Assert.Equal("", tool.FilterState.SearchQuery);          // …and the tree un-filters
    }

    [Fact]
    public void TheXButton_ClosesTheFieldAsWellAsClearingIt()
    {
        var tool = new CircuitRF.Ui.ViewModels.Dock.ProjectTreeTool();
        tool.FilterScheduler = a => a();

        tool.IsSearchOpen = true;
        tool.SearchText = "amp";

        tool.ClearSearchCommand.Execute(null);

        Assert.Equal("", tool.SearchText);
        Assert.False(tool.IsSearchOpen);
        Assert.Equal("", tool.FilterState.SearchQuery);
    }

    // The X has to do BOTH halves itself now — an emptied field no longer closes on its own, so a
    // ClearSearch that only cleared the text would leave the box sitting open.
    [Fact]
    public void TheXButton_ClosesTheField_EvenWhenTheTextWasAlreadyEmpty()
    {
        var tool = new CircuitRF.Ui.ViewModels.Dock.ProjectTreeTool();
        tool.FilterScheduler = a => a();

        tool.IsSearchOpen = true;          // opened, nothing typed

        tool.ClearSearchCommand.Execute(null);

        Assert.False(tool.IsSearchOpen);
    }

    // Escape is the other of the two — the view routes it to IsSearchOpen = false, which clears on
    // its way (see ClosingTheSearch_ClearsTheQuery above).
    //
    // handledEventsToo: true is LOAD-BEARING and its absence is a silent no-op, which is exactly how
    // it shipped broken once: WorkspaceWindow.axaml binds Escape to DisarmPlacementCommand, and
    // Window.KeyBindings are processed before visual-tree routing and always mark the event Handled,
    // so a handler that skips handled events never sees Escape at all. Three other views in this repo
    // carry the same argument on their own Escape handlers.
    [Fact]
    public void TheViewRoutesEscapeToClosingTheSearch_AndClaimsTheAlreadyHandledKey()
    {
        var cs = ReadRepoFile("src/Ui/Views/ProjectTree/ProjectTreeView.axaml.cs");
        Assert.Contains("Key.Escape", cs);
        Assert.Contains("tool.IsSearchOpen = false", cs);
        Assert.Contains(
            "AddHandler(KeyDownEvent, OnSearchKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true)",
            cs);

        // The window binding this has to out-rank. If it is ever removed, the argument above stops
        // applying and this test should be revisited rather than silently kept.
        Assert.Contains("Gesture=\"Escape\"", ReadRepoFile("src/Ui/Views/WorkspaceWindow.axaml"));
    }

    // A ScrollViewer's DEFAULT HorizontalScrollBarVisibility is Disabled, and Disabled constrains the
    // content to the viewport width rather than merely hiding a bar — so a long cell or file name was
    // clipped with no way to reach the rest of it.
    [Fact]
    public void TheTreeScrollsHorizontally_ForNamesWiderThanThePanel()
    {
        var axaml = ReadRepoFile("src/Ui/Views/ProjectTree/ProjectTreeView.axaml");
        Assert.Contains("HorizontalScrollBarVisibility=\"Auto\"", axaml);
    }

    // Clicking a row focuses it, and focus handling asks the nearest scroller to bring it into view on
    // BOTH axes. With horizontal scrolling on, a row wider than the panel is never fully "in view", so
    // merely SELECTING a long cell name nudged the horizontal scrollbar.
    [Fact]
    public void SelectingARow_DoesNotMoveTheHorizontalScroll()
    {
        var cs = ReadRepoFile("src/Ui/Views/ProjectTree/ProjectTreeView.axaml.cs");

        Assert.Contains("AddHandler(RequestBringIntoViewEvent, OnTreeBringIntoView)", cs);

        // Zero WIDTH is the whole mechanism: the rect keeps its Y and Height so vertical
        // bring-into-view (arrow-key navigation) still works, and carries no horizontal extent to
        // scroll toward.
        Assert.Contains("new Rect(-tx, e.TargetRect.Y, 0, e.TargetRect.Height)", cs);

        // The one-line alternative kills BOTH axes and would take keyboard navigation with it. Checked
        // against comment-stripped source — the code-behind NAMES it while explaining why it is the
        // wrong tool, and a raw substring scan reads that explanation as a use.
        Assert.DoesNotContain("BringIntoViewOnFocusChange", StripComments(cs));
        Assert.DoesNotContain("BringIntoViewOnFocusChange",
            StripXmlComments(ReadRepoFile("src/Ui/Views/ProjectTree/ProjectTreeView.axaml")));
    }

    // Closing leaves the caret in a control that is no longer on screen, which both swallows keys and
    // makes OnScrollKeyDown read `e.Source is TextBox` as true — so Home/End would go on yielding to a
    // field the user cannot see.
    [Fact]
    public void ClosingTheSearch_HandsFocusBackToTheTree()
    {
        var cs = ReadRepoFile("src/Ui/Views/ProjectTree/ProjectTreeView.axaml.cs");
        Assert.Contains("TheTreeView.Focus()", cs);
    }

    [Fact]
    public void TheHeaderCarriesTheSearchToggle_AndTheFieldOnlyShowsWhenItIsOn()
    {
        var axaml = ReadRepoFile("src/Ui/Views/ProjectTree/ProjectTreeView.axaml");
        var cs    = ReadRepoFile("src/Ui/Views/ProjectTree/ProjectTreeView.axaml.cs");

        Assert.Contains("{Binding ToggleSearchCommand}", axaml);
        Assert.Contains("IsVisible=\"{Binding IsSearchOpen}\"", axaml);

        // The field takes the workspace NAME's place rather than overlaying the buttons, so the
        // toolbar stays one row and every button stays where it was.
        Assert.Contains("IsVisible=\"{Binding !IsSearchOpen}\"", axaml);

        // Revealing a box the user then has to click is two gestures for one intent.
        Assert.Contains("SearchBox.Focus()", cs);
    }

    // A search that opened the tree to find something must not leave every folder hanging open.
    [Fact]
    public void ASearch_ExpandsThePathToAMatch_AndRestoresExpansionWhenCleared()
    {
        Cell("myboard/R0402");

        var filter = new ProjectTreeFilterState();
        var root = Tree(filter);
        var folder = root.Children.Single(c => c.Kind == NodeKind.UserFolder);

        Assert.False(folder.IsExpanded);   // fixture precondition: collapsed to start with

        filter.SearchQuery = "R0402";
        Assert.True(folder.IsExpanded);

        filter.SearchQuery = "";
        Assert.False(folder.IsExpanded);
    }

    // The two filters are ANDed. Clearing a category checkbox while a search is running must still
    // hide that category, or the checkbox appears to stop working the moment anything is typed.
    [Fact]
    public void ASearchAndACategoryToggle_AreBothApplied()
    {
        Cell("MyAmp");

        var filter = new ProjectTreeFilterState();
        var root = Tree(filter);

        filter.SearchQuery = "amp";
        Assert.Single(root.FilteredChildren);

        filter.Cells = false;
        Assert.Empty(root.FilteredChildren);
    }
}

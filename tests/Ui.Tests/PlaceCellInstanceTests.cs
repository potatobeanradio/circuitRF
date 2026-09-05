using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

// ──────────────────────────────────────────────────────────────────────────────
//  Design ▸ Place Cell Instance… (owner, 2026-09-05) — a findable, menu-driven way to put an instance
//  of a cell into a schematic OR a layout, beside the drag-and-drop that both editors already had and
//  the Instance toolbar button the layout editor already had.
//
//  Three things are pinned here:
//    1. What the picker OFFERS — the same list in both editors, differing only in what a missing view
//       means, plus the cells this workspace REFERENCES (which the Project Tree already shows and a
//       drag can already place).
//    2. How a chosen cell is ARMED in a schematic — through the one app-level PlacementService, so
//       Escape and the palette's armed tile behave as they do for every other placement.
//    3. The menu WIRING, by source scan: WorkspaceViewModel cannot be constructed headlessly (its
//       ctor builds a Dock layout and posts to the Dispatcher — a standing constraint recorded in
//       src/Ui/CLAUDE.md), so the two menu surfaces and the two CanExecute fan-outs are asserted
//       against the source text, exactly as WindowMenuTests does.
//
//  In CellStatGlobalsCollection: the reference fixtures call WorkspaceRootFinder.InvalidateCache,
//  which drops CellStat's cache — see that collection's own note.
// ──────────────────────────────────────────────────────────────────────────────

[Collection(CellStatGlobalsCollection.Name)]
public sealed class PlaceCellInstanceTests : IDisposable
{
    private readonly string _root;
    private readonly string _mine;
    private readonly string _theirs;

    public PlaceCellInstanceTests()
    {
        _root   = Path.Combine(Path.GetTempPath(), "crfPlaceCell_" + Guid.NewGuid().ToString("N")[..8]);
        _mine   = Path.Combine(_root, "Mine");
        _theirs = Path.Combine(_root, "Theirs");
        Directory.CreateDirectory(_mine);
        Directory.CreateDirectory(_theirs);
        WriteCws(_mine);
        WriteCws(_theirs);
    }

    public void Dispose()
    {
        WorkspaceRootFinder.InvalidateCache();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // ── Fixture ───────────────────────────────────────────────────────────────

    private static void WriteCws(string root, Action<CwsFile>? edit = null)
    {
        var cws = new CwsFile();
        edit?.Invoke(cws);
        WorkspacePersistence.SaveToFile(Path.Combine(root, ".cws"), cws);
        WorkspaceRootFinder.InvalidateCache();
    }

    private static Symbol TwoPinSymbol() => new(
        primitives: [new LinePrimitive(SymbolColorRole.SymbolLine, SymbolStrokeTier.Normal, -100, 0, 100, 0)],
        pins:       [new SymbolPin(-200, 0, 1, "a"), new SymbolPin(200, 0, 2, "b")],
        portCount:  2);

    /// <summary>A cell folder with whichever views are asked for — the picker's whole question is
    /// "does this cell have the view I am about to draw from?", so the fixture must be able to say no
    /// to each independently.</summary>
    private static string CreateCell(string root, string name, bool symbol, bool layout)
    {
        string cellDir = CellFolder.CreateCellFolder(root, name);

        if (symbol)
            SymbolPersistence.SaveToFile(
                Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Symbol), name + ".csym"), TwoPinSymbol());

        if (layout)
        {
            string layDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
            Directory.CreateDirectory(layDir);
            var view = new LayoutView { DbuPerMicron = 1000, SnapDbu = 1000 };
            view.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 5000, Y2 = 2000 });
            LayoutPersistence.SaveToFile(Path.Combine(layDir, name + ".clay"), view);
        }

        return cellDir;
    }

    // ── 1. What the picker offers ─────────────────────────────────────────────

    /// <summary>
    /// The one real difference between the two editors' lists, and it is not cosmetic: a cell with no
    /// LAYOUT view has nothing to draw and is refused, while the same cell in a SCHEMATIC is placeable
    /// because the placement path offers to generate the missing symbol from the cell's ports. A
    /// disabled row there would hide a working gesture behind a refusal that is not true.
    /// </summary>
    [Fact]
    public void AMissingView_RefusesInALayout_ButOnlyRemarksInASchematic()
    {
        CreateCell(_mine, "SchematicOnly", symbol: false, layout: false);

        var forLayout = InstanceCellChoices.Collect(_mine, null, ViewType.Layout);
        var row = Assert.Single(forLayout, i => i.DisplayName == "SchematicOnly");
        Assert.False(row.IsEnabled);
        Assert.Equal("No layout view", row.DisabledReason);

        var forSchematic = InstanceCellChoices.Collect(_mine, null, ViewType.Symbol);
        var srow = Assert.Single(forSchematic, i => i.DisplayName == "SchematicOnly");
        Assert.True(srow.IsEnabled);                 // placeable — the symbol is generated on the way in
        Assert.Null(srow.DisabledReason);
        Assert.True(srow.HasAnnotation);             // …and the row says so rather than staying silent
        Assert.Contains("generated", srow.Annotation!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ACellWithTheViewBeingPlaced_IsOfferedWithNothingToSay()
    {
        CreateCell(_mine, "Amp", symbol: true, layout: true);

        foreach (var view in new[] { ViewType.Layout, ViewType.Symbol })
        {
            var row = Assert.Single(InstanceCellChoices.Collect(_mine, null, view), i => i.DisplayName == "Amp");
            Assert.True(row.IsEnabled);
            Assert.False(row.HasAnnotation);
        }
    }

    /// <summary>
    /// A cell referenced individually is in the Project Tree and a drag already places it, so the
    /// picker offers it too — otherwise the menu is strictly less capable than the gesture it exists
    /// to replace. The alias rides in the display name: two projects can both have an "Amp".
    /// </summary>
    [Fact]
    public void AnIndividuallyReferencedCell_IsOffered_AndNamesTheAliasItCameThrough()
    {
        string theirCell = CreateCell(_theirs, "Amp", symbol: true, layout: true);
        CreateCell(_theirs, "NotReferenced", symbol: true, layout: true);
        ReferenceOneCell(alias: "Lib", cellName: "Amp");

        var items = InstanceCellChoices.CollectWithReferences(_mine, null, ViewType.Layout);

        var row = Assert.Single(items, i => InstanceCellChoices.NormalizeDir(i.AbsoluteCellDir)
                                         == InstanceCellChoices.NormalizeDir(theirCell));
        Assert.Contains("Lib", row.DisplayName, StringComparison.Ordinal);
        Assert.True(row.IsEnabled);

        // …and referencing ONE cell does not drag the other project's whole catalogue in with it —
        // the same rule the Project Tree follows for a CellsOnly alias.
        Assert.DoesNotContain(items, i => i.DisplayName.Contains("NotReferenced", StringComparison.Ordinal));
    }

    /// <summary>A referenced WORKSPACE is drawn in full by the tree, so it is listed in full here.</summary>
    [Fact]
    public void AFullyReferencedWorkspace_ContributesItsCells()
    {
        CreateCell(_theirs, "Amp",  symbol: true, layout: true);
        CreateCell(_theirs, "Bias", symbol: true, layout: true);
        WriteCws(_mine, c => c.ReferencedWorkspaces =
            [new CwsWorkspaceRef { Alias = "Lib", Path = Path.GetRelativePath(_mine, Path.Combine(_theirs, ".cws")) }]);

        var items = InstanceCellChoices.CollectWithReferences(_mine, null, ViewType.Layout);

        Assert.Contains(items, i => i.DisplayName == "Amp (Lib)");
        Assert.Contains(items, i => i.DisplayName == "Bias (Lib)");
    }

    /// <summary>R-fix-1's one exclusion still holds across the reference sources — a cell cannot be
    /// placed inside itself however it was reached.</summary>
    [Fact]
    public void TheParentCellIsStillTheOneExclusion_EvenWhenReachedThroughAReference()
    {
        string theirCell = CreateCell(_theirs, "Amp", symbol: true, layout: true);
        ReferenceOneCell(alias: "Lib", cellName: "Amp");

        var items = InstanceCellChoices.CollectWithReferences(_mine, theirCell, ViewType.Layout);

        Assert.DoesNotContain(items, i => InstanceCellChoices.NormalizeDir(i.AbsoluteCellDir)
                                       == InstanceCellChoices.NormalizeDir(theirCell));
    }

    private void ReferenceOneCell(string alias, string cellName)
    {
        WriteCws(_mine, c =>
        {
            c.ReferencedWorkspaces =
                [new CwsWorkspaceRef
                 {
                     Alias = alias,
                     Path  = Path.GetRelativePath(_mine, Path.Combine(_theirs, ".cws")),
                     // The alias exists only so this ONE cell can be addressed — the tree draws no row
                     // for it, and neither does the picker.
                     CellsOnly = true,
                 }];
            c.ReferencedCells = [ExternalCellRef.RefFor(alias, cellName)];
        });
    }

    // ── 2. Arming a schematic placement ───────────────────────────────────────

    /// <summary>
    /// The picker's answer goes through the ONE app-level armed state, so Escape, the palette's armed
    /// tile and the rotation keys all keep working — a second armed state in the schematic would have
    /// to re-implement each of those and could disagree with the first.
    /// </summary>
    [Fact]
    public void ArmCell_ArmsTheAppLevelPlacement_AsACellAndNotAsAKitPart()
    {
        var svc = new PlacementService();
        string cellDir = CreateCell(_mine, "Amp", symbol: true, layout: true);

        svc.ArmCell(cellDir);

        Assert.NotNull(svc.Pending);
        Assert.Equal(cellDir, svc.Pending!.CellDir);
        // Never as a kit part: PdkPartRef carries a kit+part identity the palette compares armed
        // tiles by, and a workspace cell has none.
        Assert.Null(svc.Pending.Pdk);
    }

    [Fact]
    public void ArmCell_AlwaysArms_NeverTogglesItselfOff()
    {
        var svc = new PlacementService();
        string cellDir = CreateCell(_mine, "Amp", symbol: true, layout: true);

        svc.ArmCell(cellDir);
        svc.ArmCell(cellDir);   // the picker is a deliberate act; the second one must not disarm

        Assert.Equal(cellDir, svc.Pending?.CellDir);
        svc.Disarm();
        Assert.Null(svc.Pending);
    }

    [Fact]
    public void ArmingAPaletteComponentAfterACell_ClearsTheCell()
    {
        var svc = new PlacementService();
        svc.ArmCell(CreateCell(_mine, "Amp", symbol: true, layout: true));

        svc.Toggle(SymbolKind.Resistor, 2);

        Assert.Equal(SymbolKind.Resistor, svc.Pending?.Kind);
        Assert.Null(svc.Pending?.CellDir);
    }

    // ── 3. Menu wiring (source scan — see the header note) ────────────────────

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "circuitrf.slnx")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return dir!;
    }

    private static string ReadRepoFile(string rel) => File.ReadAllText(Path.Combine(RepoRoot(), rel));

    private static string WorkspaceWindowXaml() =>
        ReadRepoFile(Path.Combine("src", "Ui", "Views", "WorkspaceWindow.axaml"));

    /// <summary>
    /// Owner, 2026-09-05: View sits immediately left of Window, because window arrangement and view
    /// arrangement are the same kind of question and belong next to each other. Asserted on BOTH menu
    /// surfaces — they are hand-mirrored and the whole hazard here is that one of them drifts.
    /// </summary>
    [Fact]
    public void TheViewMenu_SitsImmediatelyLeftOfTheWindowMenu_OnBothSurfaces()
    {
        string xaml = WorkspaceWindowXaml();

        foreach (var (view, window, design, surface) in new[]
        {
            ("<NativeMenuItem Header=\"View\">", "<NativeMenuItem Header=\"Window\">",
             "<NativeMenuItem Header=\"Design\">", "macOS NativeMenu"),
            ("<MenuItem Header=\"_View\"", "<MenuItem Header=\"_Window\"",
             "<MenuItem Header=\"_Design\"", "in-window Menu"),
        })
        {
            int v = xaml.IndexOf(view,   StringComparison.Ordinal);
            int w = xaml.IndexOf(window, StringComparison.Ordinal);
            int d = xaml.IndexOf(design, StringComparison.Ordinal);
            Assert.True(v >= 0 && w >= 0 && d >= 0, surface);
            Assert.True(v < w, $"{surface}: View must come before Window");
            // …and it moved from where it used to be, rather than Window moving: Design still leads
            // the middle of the bar.
            Assert.True(d < v, $"{surface}: Design must still come before View");
        }
    }

    [Fact]
    public void PlaceCellInstance_IsInTheDesignMenu_OnBothSurfaces_AndBoundToTheCommand()
    {
        string xaml = WorkspaceWindowXaml();

        Assert.Contains("Place Cell Instance", xaml, StringComparison.Ordinal);
        // Four times — once per menu surface, plus the two key bindings — and always bound to the
        // command, never to a code-behind Click handler.
        Assert.Equal(4, CountOf(xaml, "{Binding PlaceCellInstanceCommand}"));

        foreach (var menu in new[] { "<NativeMenuItem Header=\"Design\">", "<MenuItem Header=\"_Design\"" })
        {
            int start = xaml.IndexOf(menu, StringComparison.Ordinal);
            Assert.True(start >= 0, menu);
            string body = xaml[start..Math.Min(xaml.Length, start + 2500)];
            Assert.Contains("PlaceCellInstanceCommand", body, StringComparison.Ordinal);
        }
    }

    /// <summary>The gesture is a shortcut on both key conventions, as every other Design-menu entry
    /// is — a Ctrl binding and a Meta one, since the in-window MenuItem's InputGesture only DRAWS the
    /// shortcut and the Window's own KeyBindings are what execute it.</summary>
    [Fact]
    public void PlaceCellInstance_HasBothKeyBindings()
    {
        string xaml = WorkspaceWindowXaml();
        Assert.Contains("<KeyBinding Gesture=\"Ctrl+Shift+I\"  Command=\"{Binding PlaceCellInstanceCommand}\"/>",
                        xaml, StringComparison.Ordinal);
        Assert.Contains("<KeyBinding Gesture=\"Meta+Shift+I\"  Command=\"{Binding PlaceCellInstanceCommand}\"/>",
                        xaml, StringComparison.Ordinal);
    }

    /// <summary>
    /// The standing gotcha this file's neighbours keep re-learning: a
    /// <c>[RelayCommand(CanExecute=…)]</c> gated on the ACTIVE DOCUMENT is not re-evaluated on its
    /// own. It has to be notified from BOTH fan-outs, or it stays stuck at whatever it was when the
    /// window was constructed — which for this one means permanently greyed out.
    /// </summary>
    [Fact]
    public void PlaceCellInstance_IsNotifiedFromBothActiveDocumentFanOuts()
    {
        string src = ReadRepoFile(Path.Combine("src", "Ui", "ViewModels", "WorkspaceViewModel.cs"));
        Assert.Equal(2, CountOf(src, "PlaceCellInstanceCommand.NotifyCanExecuteChanged();"));
    }

    /// <summary>
    /// Both editors reach ONE picker and ONE arming path. The menu raises a request on the document;
    /// the view — which already owns the dialog for its toolbar button — runs it. A second dialog, or
    /// a second arming path, is what this keeps from appearing.
    /// </summary>
    [Fact]
    public void BothEditors_RouteTheMenuThroughTheirOwnExistingPickerPath()
    {
        string vm = ReadRepoFile(Path.Combine("src", "Ui", "ViewModels", "WorkspaceViewModel.cs"));
        Assert.Contains("layout.RequestPlaceCellInstance();",    vm, StringComparison.Ordinal);
        Assert.Contains("schematic.RequestPlaceCellInstance();", vm, StringComparison.Ordinal);

        // The layout's menu request and its Instance toolbar button land on the same method.
        string layoutView = ReadRepoFile(Path.Combine("src", "Ui", "Views", "Layout", "LayoutEditorView.axaml.cs"));
        Assert.Contains("OnPlaceCellInstanceRequestedFromMenu() => _ = BeginInstancePlacementAsync();",
                        layoutView, StringComparison.Ordinal);
        Assert.Contains("OnInstanceTool(object? sender, RoutedEventArgs e) => await BeginInstancePlacementAsync();",
                        layoutView, StringComparison.Ordinal);

        // Both views subscribe AND unsubscribe — a document event subscribed once and never dropped
        // is how a torn-off tab ends up placing into a document nobody is looking at.
        foreach (var rel in new[]
        {
            Path.Combine("src", "Ui", "Views", "Layout",  "LayoutEditorView.axaml.cs"),
            Path.Combine("src", "Ui", "Views", "Content", "SchematicView.axaml.cs"),
        })
        {
            string src = ReadRepoFile(rel);
            Assert.Contains("PlaceCellInstanceRequested +=", src, StringComparison.Ordinal);
            Assert.Contains("PlaceCellInstanceRequested -=", src, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The Reference Cell… button must not open a second modal over the same owner window — the
    /// picker CLOSES and the caller runs the flow. Pinned because the natural implementation is the
    /// wrong one, and the failure it produces (a hung dialog) is platform-dependent.
    /// </summary>
    [Fact]
    public void ReferenceCell_ClosesThePicker_AndTheCallerRunsTheFlow()
    {
        string dialog = ReadRepoFile(
            Path.Combine("src", "Ui", "Views", "Dialogs", "InstanceCellPickerDialog.axaml.cs"));
        Assert.Contains("Close(CellPickResult.Reference);", dialog, StringComparison.Ordinal);
        // Nothing in the dialog knows how to bring a cell in.
        Assert.DoesNotContain("ReferenceExternalCellAsync", dialog, StringComparison.Ordinal);

        foreach (var rel in new[]
        {
            Path.Combine("src", "Ui", "Views", "Layout",  "LayoutEditorView.axaml.cs"),
            Path.Combine("src", "Ui", "Views", "Content", "SchematicView.axaml.cs"),
        })
        {
            string src = ReadRepoFile(rel);
            Assert.Contains("ReferenceRequested", src, StringComparison.Ordinal);
            Assert.Contains("ReferenceExternalCellAsync()", src, StringComparison.Ordinal);
        }
    }

    /// <summary>One flow for taking a cell in, not two: the picker's button reaches the same code
    /// File ▸ Add Cell to Workspace… and the cross-workspace drag already run, so "reference or
    /// copy?", "bring its technology?" and the collision prompts cannot drift apart.</summary>
    [Fact]
    public void ReferenceCell_ReusesTheOneCrossWorkspaceFlow()
    {
        string src = ReadRepoFile(Path.Combine("src", "Ui", "ViewModels", "WorkspaceViewModel.CellDrop.cs"));
        int at = src.IndexOf("public async Task<string?> ReferenceExternalCellAsync()", StringComparison.Ordinal);
        Assert.True(at >= 0);
        string body = src[at..Math.Min(src.Length, at + 1600)];
        Assert.Contains("AcceptCellFromOtherWorkspaceCoreAsync(", body, StringComparison.Ordinal);
    }

    private static int CountOf(string haystack, string needle)
    {
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }
}

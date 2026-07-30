using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using CircuitRF.Ui.Docking;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels.Dock;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// brief-dock-layout-persistence.md §1–§3 and §4A gates.
///
/// <para>The Dock model types (<c>RootDock</c>/<c>ToolDock</c>/<c>Tool</c>) are plain C# and construct
/// without an Avalonia platform, so the whole capture → serialize → read → build → capture loop runs
/// headlessly against the REAL factory. <c>WorkspaceViewModel</c> itself cannot be constructed here
/// (its ctor touches the Dispatcher) — the handful of assertions that live only there are pinned by
/// source scan, this codebase's established fallback for exactly that case.</para>
/// </summary>
public sealed class DockLayoutPersistenceTests
{
    /// <summary>
    /// A factory with the default layout built, plus that layout's root.
    ///
    /// <para><c>InitLayout</c> is deliberately NOT called: it executes
    /// <c>IRootDock.ShowWindows</c>, which constructs a real <c>HostWindow</c> and therefore needs
    /// an Avalonia windowing platform this test host does not have. Everything these tests assert —
    /// the dock tree, the tool docks, the window MODELS and their geometry — is built by
    /// <c>CreateLayout</c>/<c>CreateLayoutFromState</c> alone; <c>InitLayout</c> only wires
    /// Factory/Owner back-references and presents windows.</para>
    /// </summary>
    private static (CircuitRfDockFactory Factory, IRootDock Root) NewShell()
    {
        var f = new CircuitRfDockFactory();
        return (f, f.CreateLayout());
    }

    private static IRootDock Apply(CircuitRfDockFactory f, CwsDockLayout state) =>
        f.CreateLayoutFromState(state);

    private static CwsDockLayout Capture(IRootDock root) =>
        DockLayoutCapture.Capture(root, []);

    private static CwsDockLayout RoundTripThroughJson(CwsDockLayout layout)
    {
        var node = DockLayoutSerialization.Write(layout);
        var read = DockLayoutSerialization.TryRead(node);
        Assert.Null(read.Report);
        Assert.NotNull(read.Layout);
        return read.Layout!;
    }

    private static CwsDockPanel Panel(CwsDockLayout l, string id) =>
        Assert.Single(l.Panels, p => p.Id == id);

    // ── Gate 2 — round trip ───────────────────────────────────────────────────

    /// <summary>
    /// Panels on all four sides, one closed, two tabbed together with the SECOND selected, a third
    /// floated: save, reopen, and every one of those facts comes back — including which tab was
    /// active, which is the part most likely to be dropped.
    /// </summary>
    [Fact]
    public void Gate2_FourSidesClosedTabbedAndFloated_RoundTripThroughJsonAndTheRealFactory()
    {
        var original = new CwsDockLayout
        {
            Sides = [new CwsDockSide { Side = DockSide.Left, Proportion = 0.28 }],
            Panels =
            [
                // Tabbed pair on the left, with the SECOND tab selected.
                new CwsDockPanel { Id = DockPanelIds.ProjectTree, Side = DockSide.Left,   Group = 0, Order = 0, Active = false, Proportion = 0.7 },
                new CwsDockPanel { Id = DockPanelIds.Palette,     Side = DockSide.Left,   Group = 0, Order = 1, Active = true,  Proportion = 0.7 },
                new CwsDockPanel { Id = DockPanelIds.Properties,  Side = DockSide.Bottom, Group = 0, Order = 0, Active = true,  Proportion = 0.3 },
                // Closed.
                new CwsDockPanel { Id = DockPanelIds.Analyses,    Open = false, Side = DockSide.Left, Group = 1, Order = 0 },
            ],
            FloatingWindows =
            [
                new CwsFloatingWindow { X = 300, Y = 220, Width = 520, Height = 380, Panels = [DockPanelIds.Messages], Active = DockPanelIds.Messages },
            ],
        };

        var reloaded = RoundTripThroughJson(original);

        var (factory, _) = NewShell();
        var captured     = Capture(Apply(factory, reloaded));

        Assert.Equal(DockSide.Left,   Panel(captured, DockPanelIds.ProjectTree).Side);
        Assert.Equal(DockSide.Left,   Panel(captured, DockPanelIds.Palette).Side);
        Assert.Equal(DockSide.Bottom, Panel(captured, DockPanelIds.Properties).Side);

        // Which tab was active is preserved — the Palette, not the Project Tree.
        Assert.False(Panel(captured, DockPanelIds.ProjectTree).Active);
        Assert.True (Panel(captured, DockPanelIds.Palette).Active);
        Assert.Equal(0, Panel(captured, DockPanelIds.ProjectTree).Order);
        Assert.Equal(1, Panel(captured, DockPanelIds.Palette).Order);

        // Closed stays closed.
        Assert.False(Panel(captured, DockPanelIds.Analyses).Open);

        // Size along the docking axis survives.
        Assert.Equal(0.7,  Panel(captured, DockPanelIds.Palette).Proportion, 6);
        Assert.Equal(0.28, Assert.Single(captured.Sides, s => s.Side == DockSide.Left).Proportion, 6);

        // The floated panel is floated, with its geometry.
        var floated = Assert.Single(captured.FloatingWindows);
        Assert.Equal([DockPanelIds.Messages], floated.Panels);
        Assert.Equal(300, floated.X, 6);
        Assert.Equal(220, floated.Y, 6);
        Assert.Equal(520, floated.Width, 6);
        Assert.Equal(380, floated.Height, 6);
        Assert.DoesNotContain(captured.Panels, p => p.Id == DockPanelIds.Messages && p.Open);
    }

    [Fact]
    public void Gate2_RightAndTopSidesAlsoRoundTrip()
    {
        var state = new CwsDockLayout
        {
            Sides  = [new CwsDockSide { Side = DockSide.Right, Proportion = 0.25 }],
            Panels =
            [
                new CwsDockPanel { Id = DockPanelIds.Properties,  Side = DockSide.Right, Group = 0, Order = 0, Active = true, Proportion = 0.5 },
                new CwsDockPanel { Id = DockPanelIds.Analyses,    Side = DockSide.Right, Group = 1, Order = 0, Active = true, Proportion = 0.5 },
                new CwsDockPanel { Id = DockPanelIds.Messages,    Side = DockSide.Top,   Group = 0, Order = 0, Active = true, Proportion = 0.2 },
                new CwsDockPanel { Id = DockPanelIds.ProjectTree, Side = DockSide.Left,  Group = 0, Order = 0, Active = true, Proportion = 1.0 },
                new CwsDockPanel { Id = DockPanelIds.Palette,     Open = false },
            ],
        };

        var (factory, _) = NewShell();
        var captured     = Capture(Apply(factory, RoundTripThroughJson(state)));

        Assert.Equal(DockSide.Right, Panel(captured, DockPanelIds.Properties).Side);
        Assert.Equal(DockSide.Right, Panel(captured, DockPanelIds.Analyses).Side);
        Assert.Equal(DockSide.Top,   Panel(captured, DockPanelIds.Messages).Side);
        Assert.Equal(DockSide.Left,  Panel(captured, DockPanelIds.ProjectTree).Side);
        Assert.False(Panel(captured, DockPanelIds.Palette).Open);

        // Two groups on the right stay two groups, not one merged tab strip.
        Assert.NotEqual(Panel(captured, DockPanelIds.Properties).Group,
                        Panel(captured, DockPanelIds.Analyses).Group);
    }

    [Fact]
    public void DefaultLayout_CapturesBackAsTheDefault_SoTheBuilderIsExercisedOnEveryLaunch()
    {
        var (_, root) = NewShell();
        var captured  = Capture(root);

        Assert.Equal(DockSide.Left,   Panel(captured, DockPanelIds.ProjectTree).Side);
        Assert.Equal(DockSide.Left,   Panel(captured, DockPanelIds.Palette).Side);
        Assert.Equal(DockSide.Left,   Panel(captured, DockPanelIds.Properties).Side);
        Assert.Equal(DockSide.Left,   Panel(captured, DockPanelIds.Analyses).Side);
        Assert.Equal(DockSide.Bottom, Panel(captured, DockPanelIds.Messages).Side);

        Assert.True(Panel(captured, DockPanelIds.ProjectTree).Active);
        Assert.True(Panel(captured, DockPanelIds.Properties).Active);
        Assert.Equal(Panel(captured, DockPanelIds.ProjectTree).Group, Panel(captured, DockPanelIds.Palette).Group);
        Assert.NotEqual(Panel(captured, DockPanelIds.ProjectTree).Group, Panel(captured, DockPanelIds.Properties).Group);
        Assert.All(captured.Panels, p => Assert.True(p.Open));
    }

    // ── Gate 3 — absent block ─────────────────────────────────────────────────

    [Fact]
    public void Gate3_AbsentBlock_UsesTheDefaultLayout_Silently()
    {
        var read = DockLayoutSerialization.TryRead(null);
        Assert.Null(read.Layout);
        Assert.Null(read.Report);   // silence is the requirement — no block is the ordinary case
    }

    [Fact]
    public void Gate3_CwsWithNoLayoutBlock_LoadsAndOmitsTheFieldOnWrite()
    {
        var dir  = Directory.CreateTempSubdirectory("crf-dock-absent");
        var path = Path.Combine(dir.FullName, ".cws");
        try
        {
            WorkspacePersistence.SaveToFile(path, new CwsFile());
            Assert.DoesNotContain("DockLayout", File.ReadAllText(path));
            Assert.Null(WorkspacePersistence.LoadFromFile(path).DockLayout);
        }
        finally { dir.Delete(true); }
    }

    // ── Gate 4 — malformed and future-version ─────────────────────────────────

    [Fact]
    public void Gate4_FutureVersion_UsesTheDefaultLayout_AndReports()
    {
        var node = JsonNode.Parse($$"""{"Version": {{CwsDockLayout.CurrentVersion + 1}}, "Panels": []}""");
        var read = DockLayoutSerialization.TryRead(node);

        Assert.Null(read.Layout);
        Assert.NotNull(read.Report);
        Assert.Contains("newer", read.Report!, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Gate4_MalformedBlock_UsesTheDefaultLayout_AndReports()
    {
        // Structurally wrong: Panels is a string where an array belongs.
        var node = JsonNode.Parse("""{"Version": 1, "Panels": "not an array"}""");
        var read = DockLayoutSerialization.TryRead(node);

        Assert.Null(read.Layout);
        Assert.NotNull(read.Report);
    }

    /// <summary>
    /// R-dock-5's structural half: because the block is stored as a raw <c>JsonNode</c>, a malformed
    /// layout cannot take the rest of the <c>.cws</c> — tree state, open documents — down with it.
    /// The workspace itself still opens normally.
    /// </summary>
    [Fact]
    public void Gate4_MalformedBlockInARealCws_LeavesEveryOtherFieldIntact()
    {
        var dir  = Directory.CreateTempSubdirectory("crf-dock-malformed");
        var path = Path.Combine(dir.FullName, ".cws");
        try
        {
            var ws = new CwsFile
            {
                KnownFiles    = ["a.snp"],
                TreeViewState = new CwsTreeViewState { Cells = false },
                OpenDocuments = [new CwsOpenDocument { Path = "Amp/schematic/Amp.csch", Kind = "schematic", TabOrder = 0 }],
                DockLayout    = JsonNode.Parse("""{"Version": 1, "Panels": 42}"""),
            };
            WorkspacePersistence.SaveToFile(path, ws);

            var loaded = WorkspacePersistence.LoadFromFile(path);   // must NOT throw

            Assert.Equal(["a.snp"], loaded.KnownFiles);
            Assert.False(loaded.TreeViewState!.Cells);
            Assert.Single(loaded.OpenDocuments!);

            var read = DockLayoutSerialization.TryRead(loaded.DockLayout);
            Assert.Null(read.Layout);
            Assert.NotNull(read.Report);
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void UnknownPanelIdFromAnOlderBuild_IsDroppedRatherThanFailingTheWholeLayout()
    {
        var node = JsonNode.Parse($$"""
            {"Version": 1, "Panels": [
               {"Id": "GhostPanelFromAnOlderBuild", "Side": "Left"},
               {"Id": "{{DockPanelIds.Messages}}", "Side": "Bottom", "Active": true}
            ]}
            """);
        var read = DockLayoutSerialization.TryRead(node);

        Assert.NotNull(read.Layout);
        Assert.Equal([DockPanelIds.Messages], read.Layout!.Panels.Select(p => p.Id));
    }

    [Fact]
    public void FloatingWindowNamingAVanishedDockable_IsDropped_NotAnEmptyGhostWindow()
    {
        var node = JsonNode.Parse("""
            {"Version": 1, "FloatingWindows": [{"X":10,"Y":10,"Width":300,"Height":200,"Panels":["NoSuchPanel"]}]}
            """);
        var read = DockLayoutSerialization.TryRead(node);

        Assert.NotNull(read.Layout);
        Assert.Empty(read.Layout!.FloatingWindows);
    }

    [Fact]
    public void APanelListedBothDockedAndFloating_ResolvesToOnePlace()
    {
        var node = JsonNode.Parse($$"""
            {"Version": 1,
             "Panels": [{"Id":"{{DockPanelIds.Messages}}","Side":"Left"}],
             "FloatingWindows": [{"X":0,"Y":0,"Width":300,"Height":200,"Panels":["{{DockPanelIds.Messages}}"]}]}
            """);
        var layout = DockLayoutSerialization.TryRead(node).Layout;

        Assert.NotNull(layout);
        Assert.DoesNotContain(layout!.Panels, p => p.Id == DockPanelIds.Messages);
        Assert.Single(layout.FloatingWindows);
    }

    // ── Gate 5 — membership conflict (R-dock-2) ───────────────────────────────

    [Fact]
    public void Gate5_LayoutNamingADocumentThatIsNotOpen_DropsThatEntry()
    {
        var order = DockLayoutSerialization.ReconcileDocumentOrder(
            savedOrder: ["b.csch", "gone.csch", "a.csch"],
            openKeys:   ["a.csch", "b.csch"]);

        Assert.Equal(["b.csch", "a.csch"], order);
    }

    [Fact]
    public void Gate5_DocumentOpenButAbsentFromTheLayout_AppearsInTheDefaultPosition()
    {
        var order = DockLayoutSerialization.ReconcileDocumentOrder(
            savedOrder: ["b.csch"],
            openKeys:   ["a.csch", "b.csch", "c.csch"]);

        Assert.Equal(["b.csch", "a.csch", "c.csch"], order);
    }

    [Fact]
    public void Gate5_NoLayoutOrder_KeepsTheOpenListsOwnOrder()
    {
        var open  = new[] { "a.csch", "b.csch" };
        var order = DockLayoutSerialization.ReconcileDocumentOrder([], open);
        Assert.Equal(open, order);
    }

    [Fact]
    public void Gate5_DuplicateEntriesInTheLayout_DoNotDuplicateATab()
    {
        var order = DockLayoutSerialization.ReconcileDocumentOrder(
            savedOrder: ["a.csch", "a.csch"],
            openKeys:   ["a.csch"]);
        Assert.Equal(["a.csch"], order);
    }

    // ── Foreign documents are never recorded (R-fgn-6, extended to the new block) ──

    /// <summary>
    /// A document opened from OUTSIDE the current workspace — File ▸ Open, or one orphaned by a
    /// workspace switch — keeps full editing privileges but has no place in this workspace's own
    /// restore state. The new layout block must obey that rule too: it records document ARRANGEMENT,
    /// and a foreign document has no arrangement here to record.
    /// </summary>
    [Fact]
    public void ForeignDocument_IsExcludedFromTheLayoutsDocumentOrderAndActiveDocument()
    {
        var wsDir  = Path.Combine(Path.GetTempPath(), "crf-ws-A");
        var inside = Path.Combine(wsDir, "Amp", "schematic", "Amp.csch");
        var alien  = Path.Combine(Path.GetTempPath(), "crf-ws-B", "Other", "schematic", "Other.csch");

        Assert.False(WorkspaceRootFinder.IsOutside(inside, wsDir));
        Assert.True (WorkspaceRootFinder.IsOutside(alien,  wsDir));

        // Mirrors WorkspaceViewModel.DocumentKeyFor: relative key inside, null outside.
        string? Key(string abs) =>
            WorkspaceRootFinder.IsOutside(abs, wsDir) ? null : Path.GetRelativePath(wsDir, abs);

        var insideDoc = new StubDocument("Amp", StubDocument.StubKind.Welcome);
        var alienDoc  = new StubDocument("Other", StubDocument.StubKind.Welcome);

        var factory      = new CircuitRfDockFactory();
        var documentDock = new DocumentDock
        {
            Id               = "Documents",
            VisibleDockables = factory.CreateList<IDockable>(insideDoc, alienDoc),
            ActiveDockable   = alienDoc,              // the FOREIGN one is the active tab
        };
        var root = new RootDock
        {
            VisibleDockables = factory.CreateList<IDockable>(documentDock),
            ActiveDockable   = documentDock,
        };

        var captured = DockLayoutCapture.Capture(
            root, [],
            documentKey: d => d switch
            {
                _ when ReferenceEquals(d, insideDoc) => Key(inside),
                _ when ReferenceEquals(d, alienDoc)  => Key(alien),
                _                                    => null,
            });

        Assert.Equal([Path.GetRelativePath(wsDir, inside)], captured.DocumentOrder);
        Assert.Null(captured.ActiveDocument);   // a foreign active tab records nothing, not a path
    }

    /// <summary>
    /// The three places a document path can reach <c>.cws</c> — the open list (membership), the
    /// active-document path, and the new layout block's arrangement — each carry the same
    /// outside-the-workspace guard. Pinned together so adding a fourth writer without the guard
    /// fails here rather than silently leaking an absolute path from another workspace.
    /// </summary>
    [Fact]
    public void EveryCwsWriterThatCanSeeADocumentPath_CarriesTheForeignGuard()
    {
        var vm = ReadRepoFile("src/Ui/ViewModels/WorkspaceViewModel.cs");
        Assert.Contains("if (docPath is null || kind is null || WorkspaceRootFinder.IsOutside(docPath, wsDir)) continue;", vm);
        Assert.Contains("if (activeAbsPath is not null && !WorkspaceRootFinder.IsOutside(activeAbsPath, wsDir))", vm);

        var docking = ReadRepoFile("src/Ui/ViewModels/WorkspaceViewModel.Docking.cs");
        Assert.Contains("if (abs is null || WorkspaceRootFinder.IsOutside(abs, wsDir)) return null;", docking);
    }

    // ── Torn-off DOCUMENT windows ─────────────────────────────────────────────

    private static (CircuitRfDockFactory Factory, IRootDock Root, IDockWindow Window) ShellWithFloatedDocuments(
        double x, double y, double w, double h, params IDockable[] docs)
    {
        var f          = new CircuitRfDockFactory();
        var floatedDock = new DocumentDock
        {
            VisibleDockables = f.CreateList(docs),
            ActiveDockable   = docs.Length > 0 ? docs[0] : null,
        };

        var win = f.CreateDockWindow();
        win.X = x; win.Y = y; win.Width = w; win.Height = h;
        win.Layout = new RootDock { VisibleDockables = f.CreateList<IDockable>(floatedDock) };

        var root = new RootDock
        {
            VisibleDockables = f.CreateList<IDockable>(new DocumentDock()),
            Windows          = f.CreateList(win),
        };
        return (f, root, win);
    }

    [Fact]
    public void ATornOffDocumentWindow_IsCapturedWithItsGeometryDocumentsAndActiveTab()
    {
        var a = new StubDocument("A", StubDocument.StubKind.Welcome);
        var b = new StubDocument("B", StubDocument.StubKind.Welcome);
        var (_, root, _) = ShellWithFloatedDocuments(420, 260, 700, 500, a, b);

        var captured = DockLayoutCapture.Capture(root, [],
            documentKey: d => ReferenceEquals(d, a) ? "Amp/schematic/Amp.csch"
                            : ReferenceEquals(d, b) ? "Amp/results/Amp.cdd"
                            : null);

        var win = Assert.Single(captured.FloatingDocumentWindows);
        Assert.Equal(420, win.X, 6);
        Assert.Equal(260, win.Y, 6);
        Assert.Equal(700, win.Width, 6);
        Assert.Equal(500, win.Height, 6);
        Assert.Equal(["Amp/schematic/Amp.csch", "Amp/results/Amp.cdd"], win.Documents);
        Assert.Equal("Amp/schematic/Amp.csch", win.Active);

        // A document float is not a tool-panel placement.
        Assert.Empty(captured.FloatingWindows);
    }

    [Fact]
    public void ATornOffWindowHoldingOnlyForeignOrScratchDocuments_IsNotRecordedAtAll()
    {
        // Both resolve to a null key — a scratch tab has no stable identity, a foreign document has
        // no place in this workspace's restore state. Neither needs a second guard here.
        var scratch = new StubDocument("Untitled-Schematic-1", StubDocument.StubKind.Welcome);
        var foreign = new StubDocument("Elsewhere", StubDocument.StubKind.Welcome);
        var (_, root, _) = ShellWithFloatedDocuments(100, 100, 400, 300, scratch, foreign);

        var captured = DockLayoutCapture.Capture(root, [], documentKey: _ => null);
        Assert.Empty(captured.FloatingDocumentWindows);
    }

    [Fact]
    public void ATornOffWindowMixingAKnownAndAForeignDocument_RecordsOnlyTheKnownOne()
    {
        var mine   = new StubDocument("Mine", StubDocument.StubKind.Welcome);
        var alien  = new StubDocument("Alien", StubDocument.StubKind.Welcome);
        var (_, root, _) = ShellWithFloatedDocuments(50, 60, 400, 300, mine, alien);

        var captured = DockLayoutCapture.Capture(root, [],
            documentKey: d => ReferenceEquals(d, mine) ? "Amp/schematic/Amp.csch" : null);

        var win = Assert.Single(captured.FloatingDocumentWindows);
        Assert.Equal(["Amp/schematic/Amp.csch"], win.Documents);
    }

    [Fact]
    public void FloatingDocumentWindows_RoundTripThroughJson()
    {
        var original = new CwsDockLayout
        {
            FloatingDocumentWindows =
            [
                new CwsFloatingDocumentWindow
                {
                    X = 120, Y = 90, Width = 800, Height = 600,
                    Documents = ["a.csch", "b.cdd"],
                    Active    = "b.cdd",
                },
            ],
        };

        var win = Assert.Single(RoundTripThroughJson(original).FloatingDocumentWindows);
        Assert.Equal(120, win.X, 6);
        Assert.Equal(800, win.Width, 6);
        Assert.Equal(["a.csch", "b.cdd"], win.Documents);
        Assert.Equal("b.cdd", win.Active);
    }

    [Fact]
    public void ADocumentNamedInTwoFloatingWindows_LandsInExactlyOne()
    {
        var node = JsonNode.Parse("""
            {"Version":1,"FloatingDocumentWindows":[
              {"X":0,"Y":0,"Width":400,"Height":300,"Documents":["a.csch"]},
              {"X":50,"Y":50,"Width":400,"Height":300,"Documents":["a.csch","b.csch"]}]}
            """);
        var layout = DockLayoutSerialization.TryRead(node).Layout;

        Assert.NotNull(layout);
        Assert.Equal(["a.csch"], layout!.FloatingDocumentWindows[0].Documents);
        Assert.Equal(["b.csch"], layout.FloatingDocumentWindows[1].Documents);
    }

    [Fact]
    public void AFloatingDocumentWindowLeftWithNoDocuments_IsDropped()
    {
        var node = JsonNode.Parse("""
            {"Version":1,"FloatingDocumentWindows":[
              {"X":0,"Y":0,"Width":400,"Height":300,"Documents":["a.csch"]},
              {"X":50,"Y":50,"Width":400,"Height":300,"Documents":["a.csch"]}]}
            """);
        var layout = DockLayoutSerialization.TryRead(node).Layout;

        Assert.NotNull(layout);
        Assert.Single(layout!.FloatingDocumentWindows);
    }

    [Fact]
    public void AnActiveTabThatIsNotInItsOwnWindow_FallsBackRatherThanPointingAtNothing()
    {
        var node = JsonNode.Parse("""
            {"Version":1,"FloatingDocumentWindows":[
              {"X":0,"Y":0,"Width":400,"Height":300,"Documents":["a.csch"],"Active":"elsewhere.csch"}]}
            """);
        var win = Assert.Single(DockLayoutSerialization.TryRead(node).Layout!.FloatingDocumentWindows);
        Assert.Null(win.Active);
    }

    /// <summary>
    /// A torn-off document window is a live OS window hosting live documents. Rebuilding the shell —
    /// for a layout restore, Reset Layout, or the Hide/Show Dockers toggle — must not orphan it.
    /// </summary>
    [Fact]
    public void RebuildingTheShell_CarriesOverATornOffDocumentWindow_ButRebuildsToolFloats()
    {
        var (factory, _) = NewShell();

        // A restored arrangement with one floating TOOL window …
        var withToolFloat = new CwsDockLayout
        {
            Panels = [new CwsDockPanel { Id = DockPanelIds.ProjectTree, Side = DockSide.Left, Active = true, Proportion = 1.0 }],
            FloatingWindows =
            [
                new CwsFloatingWindow { X = 10, Y = 10, Width = 300, Height = 200, Panels = [DockPanelIds.Messages] },
            ],
        };
        var root = Apply(factory, withToolFloat);
        Assert.Single(root.Windows!);

        // … plus a torn-off DOCUMENT window that already exists on screen.
        var docWin = factory.CreateDockWindow();
        docWin.X = 500; docWin.Y = 300; docWin.Width = 640; docWin.Height = 480;
        docWin.Layout = new RootDock
        {
            VisibleDockables = factory.CreateList<IDockable>(new DocumentDock
            {
                VisibleDockables = factory.CreateList<IDockable>(new StubDocument("Torn", StubDocument.StubKind.Welcome)),
            }),
        };
        root.Windows!.Add(docWin);
        Assert.Equal(2, root.Windows.Count);

        // Collapse the dockers: the tool float goes, the document float stays — same instance.
        var collapsed = Apply(factory, DockLayoutDefaults.Collapsed(withToolFloat));
        Assert.Same(docWin, Assert.Single(collapsed.Windows!));
        Assert.Same(collapsed, docWin.Owner);
    }

    // ── Reported bug: a phantom empty document window appeared ───────────────

    /// <summary>
    /// Owner report: tearing off a tool panel also produced a blank document window, sized and
    /// positioned like one that had recently been closed.
    ///
    /// <para>Root cause: a floating window whose documents are gone can still sit in
    /// <c>root.Windows</c> with a non-null but EMPTY layout — the close cascade runs through this
    /// factory's async <c>CloseDockable</c> confirm hook, so the window's own removal and its
    /// dockables' removals do not complete in lockstep. <c>InitLayout</c> then runs
    /// <c>IRootDock.ShowWindows</c>, which presents EVERY window in the list, and the leftover
    /// surfaces as a blank window at its old geometry.</para>
    /// </summary>
    [Fact]
    public void AnEmptyFloatingWindow_IsDroppedByARebuild_NotCarriedAndRePresented()
    {
        var (factory, _) = NewShell();
        var state = DockLayoutDefaults.Default();
        var root  = Apply(factory, state);

        // A document float whose documents have gone: still listed, layout present but empty.
        var emptyWin = factory.CreateDockWindow();
        emptyWin.X = 700; emptyWin.Y = 400; emptyWin.Width = 800; emptyWin.Height = 600;
        emptyWin.Layout = new RootDock
        {
            VisibleDockables = factory.CreateList<IDockable>(new DocumentDock()),
        };
        root.Windows ??= factory.CreateList<IDockWindow>();
        root.Windows.Add(emptyWin);

        var rebuilt = Apply(factory, state);

        Assert.True(rebuilt.Windows is null || rebuilt.Windows.Count == 0);
        Assert.DoesNotContain(emptyWin, root.Windows!);   // and dropped from the old root too
    }

    [Fact]
    public void ANonEmptyDocumentFloat_IsStillCarried_AndListedUnderExactlyOneRoot()
    {
        var (factory, _) = NewShell();
        var state = DockLayoutDefaults.Default();
        var root  = Apply(factory, state);

        var docWin = factory.CreateDockWindow();
        docWin.Layout = new RootDock
        {
            VisibleDockables = factory.CreateList<IDockable>(new DocumentDock
            {
                VisibleDockables = factory.CreateList<IDockable>(new StubDocument("Real", StubDocument.StubKind.Welcome)),
            }),
        };
        root.Windows ??= factory.CreateList<IDockWindow>();
        root.Windows.Add(docWin);

        var rebuilt = Apply(factory, state);

        Assert.Same(docWin, Assert.Single(rebuilt.Windows!));
        // Listing it under two roots at once would present it twice on the next ShowWindows.
        Assert.DoesNotContain(docWin, root.Windows!);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HasContent_DistinguishesARealDockableFromEmptyContainers(bool withDocument)
    {
        var f    = new CircuitRfDockFactory();
        var inner = withDocument
            ? new DocumentDock { VisibleDockables = f.CreateList<IDockable>(new StubDocument("D", StubDocument.StubKind.Welcome)) }
            : new DocumentDock();

        var layout = new RootDock { VisibleDockables = f.CreateList<IDockable>(inner) };

        Assert.Equal(withDocument, CircuitRfDockFactory.HasContent(layout));
    }

    [Fact]
    public void HasContent_IsFalseForNullAndForADockWithNoChildrenAtAll()
    {
        Assert.False(CircuitRfDockFactory.HasContent(null));
        Assert.False(CircuitRfDockFactory.HasContent(new RootDock()));
    }

    /// <summary>
    /// The crash: <c>WindowActivationHelper.ActivateAllWindows</c> — reached on every floating-window
    /// drag — passes <c>HostWindows.OfType&lt;Window&gt;()</c> to <c>Window.SortWindowsByZOrder</c>,
    /// which throws for any entry whose <c>PlatformImpl</c> is null (a closed window). Needs real
    /// windows to exercise, so the two things that keep the collection clean are pinned instead.
    /// </summary>
    [Fact]
    public void ClosedHostWindowsAreDeregistered_SoTheNextDragCannotCrash()
    {
        var factory = ReadRepoFile("src/Ui/ViewModels/Dock/CircuitRfDockFactory.cs");

        // The factory deregisters the host itself rather than relying on window.Factory, which is
        // null once Dock has already run RemoveWindow.
        Assert.Contains("if (host is not null) HostWindows.Remove(host);", factory);
        Assert.Contains("public void PurgeClosedHostWindows()", factory);
        Assert.Contains("PlatformImpl: null", factory);

        // Swept before a rebuild, and before a tear-off's own drag can begin.
        Assert.Contains("PurgeClosedHostWindows();\n        CloseFloatingToolWindows(_currentRoot);",
                        factory.Replace("\r\n", "\n"));
        Assert.Contains("_factory.PurgeClosedHostWindows();",
                        ReadRepoFile("src/Ui/ViewModels/WorkspaceViewModel.cs"));
    }

    // ── Reported bug: a moved floating window saved its OLD position ─────────

    /// <summary>
    /// Owner report: moving a floating tool window, saving the workspace, then closing and reopening
    /// it put the window back at its old position.
    ///
    /// <para>Root cause: <c>IDockWindow.X/Y/Width/Height</c> are not live — they hold whatever
    /// <c>HostAdapter.Present</c> last wrote. Only <c>IDockWindow.Save()</c> pulls the real geometry
    /// back out of the host, and dragging a window by its ordinary OS title bar never routes through
    /// Dock, so nothing called it. The capture therefore recorded where the window was first PLACED.</para>
    ///
    /// <para>This pins the seam the fix works through: whatever <c>windowGeometry</c> reports wins over
    /// the model's own (possibly stale) numbers.</para>
    /// </summary>
    [Fact]
    public void CapturedGeometryComesFromTheLiveWindow_NotTheStaleModelValues()
    {
        var factory = new CircuitRfDockFactory();
        var win     = factory.CreateDockWindow();

        // What Present() wrote when the window was first shown …
        win.X = 100; win.Y = 100; win.Width = 300; win.Height = 200;
        win.Layout = new RootDock
        {
            VisibleDockables = factory.CreateList<IDockable>(new ToolDock
            {
                VisibleDockables = factory.CreateList<IDockable>(new MessagesTool()),
            }),
        };

        var root = new RootDock
        {
            VisibleDockables = factory.CreateList<IDockable>(new DocumentDock()),
            Windows          = factory.CreateList(win),
        };

        // … versus where the user actually dragged it to.
        var live = new ScreenRect(880, 460, 520, 340);

        var captured = DockLayoutCapture.Capture(root, [], windowGeometry: _ => live);
        var saved    = Assert.Single(captured.FloatingWindows);

        Assert.Equal(live.X,      saved.X, 6);
        Assert.Equal(live.Y,      saved.Y, 6);
        Assert.Equal(live.Width,  saved.Width, 6);
        Assert.Equal(live.Height, saved.Height, 6);
    }

    [Fact]
    public void TheCaptureRefreshesEachWindowFromItsHostBeforeReadingIt()
    {
        // Save() is the only thing that syncs the model from the live host, and it needs a real host —
        // so the call itself is pinned here rather than exercised.
        var src = ReadRepoFile("src/Ui/ViewModels/WorkspaceViewModel.Docking.cs");
        Assert.Contains("try { window.Save(); }", src);
        Assert.Contains("windowGeometry: w => LiveGeometryOf(w, shell)", src);
    }

    // ── Reported bug: a floating tool window outlived its workspace ───────────

    /// <summary>
    /// Owner report: closing a workspace left the floating tool window open, so reopening the same
    /// workspace produced TWO floating windows holding the same panel.
    ///
    /// <para>Root cause: replacing the layout swaps the MODEL, but a floating window is a real OS
    /// window that nothing closed — and Dock's own <c>Exit()</c> could not close it, because it calls
    /// <c>Close()</c> and <c>CrfHostWindow.OnClosing</c> cancels that for any window hosting a tool
    /// (the guard that works around Dock's crashing tool teardown). The panel therefore outlived every
    /// rebuild.</para>
    /// </summary>
    [Fact]
    public void ClosingAWorkspace_DoesNotLeaveTheOldFloatingToolWindowBehind()
    {
        var withFloat = new CwsDockLayout
        {
            Panels = [new CwsDockPanel { Id = DockPanelIds.ProjectTree, Side = DockSide.Left, Active = true, Proportion = 1.0 }],
            FloatingWindows =
            [
                new CwsFloatingWindow { X = 200, Y = 150, Width = 320, Height = 240, Panels = [DockPanelIds.Messages] },
            ],
        };

        var (factory, _) = NewShell();
        var opened = Apply(factory, withFloat);
        var oldWindow = Assert.Single(opened.Windows!);

        // Close Workspace resets to the default layout — the old tool float must not survive it.
        var closed = factory.CreateDefaultLayout();

        Assert.True(closed.Windows is null || closed.Windows.Count == 0);
        Assert.Empty(opened.Windows!);                 // detached from the root it belonged to
        Assert.Null(oldWindow.Owner);

        // Reopening the same workspace yields ONE floating window, not two.
        var reopened = Apply(factory, withFloat);
        Assert.Single(reopened.Windows!);
    }

    [Fact]
    public void RepeatedCollapseAndRestore_NeverAccumulatesFloatingToolWindows()
    {
        var withFloat = new CwsDockLayout
        {
            Panels = [new CwsDockPanel { Id = DockPanelIds.ProjectTree, Side = DockSide.Left, Active = true, Proportion = 1.0 }],
            FloatingWindows =
            [
                new CwsFloatingWindow { X = 40, Y = 40, Width = 300, Height = 200, Panels = [DockPanelIds.Messages] },
            ],
        };

        var (factory, _) = NewShell();
        Apply(factory, withFloat);

        for (int i = 0; i < 3; i++)
        {
            var collapsed = Apply(factory, DockLayoutDefaults.Collapsed(withFloat));
            Assert.True(collapsed.Windows is null || collapsed.Windows.Count == 0);

            var restored = Apply(factory, withFloat);
            Assert.Single(restored.Windows!);
        }
    }

    [Fact]
    public void ARebuild_ClosesToolFloats_ButLeavesATornOffDocumentWindowAlone()
    {
        // R-fgn-2: a torn-off DOCUMENT is the user's own work and survives a workspace switch. Only
        // tool panels — app-level singletons the new layout is about to re-place — are torn down.
        var (factory, _) = NewShell();
        var withToolFloat = new CwsDockLayout
        {
            Panels = [new CwsDockPanel { Id = DockPanelIds.ProjectTree, Side = DockSide.Left, Active = true, Proportion = 1.0 }],
            FloatingWindows =
            [
                new CwsFloatingWindow { X = 10, Y = 10, Width = 300, Height = 200, Panels = [DockPanelIds.Messages] },
            ],
        };
        var root = Apply(factory, withToolFloat);

        var docWin = factory.CreateDockWindow();
        docWin.Layout = new RootDock
        {
            VisibleDockables = factory.CreateList<IDockable>(new DocumentDock
            {
                VisibleDockables = factory.CreateList<IDockable>(new StubDocument("Torn", StubDocument.StubKind.Welcome)),
            }),
        };
        root.Windows!.Add(docWin);
        Assert.Equal(2, root.Windows.Count);

        var rebuilt = Apply(factory, withToolFloat);

        // One tool float (freshly built) + the carried-over document float.
        Assert.Equal(2, rebuilt.Windows!.Count);
        Assert.Contains(docWin, rebuilt.Windows);
        Assert.Single(rebuilt.Windows, w => CircuitRfDockFactory.ContainsTool(w.Layout));
    }

    /// <summary>
    /// <c>SplitToWindow</c> — the path both a drag tear-off and the layout restore take — supplies
    /// <c>DockWindowOptions</c>, and <c>DockWindowOptions.ApplyTo</c> assigns
    /// <c>window.OwnerMode</c> UNCONDITIONALLY. Overriding only the single-argument
    /// <c>CreateWindowFrom</c> would therefore have been silently overwritten.
    /// </summary>
    [Fact]
    public void OwnerMode_SurvivesTheOptionsTakingCreateWindowFromOverload()
    {
        var factory  = new CircuitRfDockFactory();
        var toolDock = new ToolDock { VisibleDockables = factory.CreateList<IDockable>(new MessagesTool()) };

        var window = factory.CreateWindowFrom(toolDock, new DockWindowOptions { OwnerMode = DockWindowOwnerMode.None });

        Assert.NotNull(window);
        Assert.Equal(DockWindowOwnerMode.Default, window!.OwnerMode);   // owned — not the option's None
    }

    /// <summary>
    /// The restore pass itself calls <c>SplitToWindow</c>, which presents a real window and so needs
    /// an Avalonia windowing platform this host does not have. Its wiring is pinned here and named as
    /// not-interactively-verified in the completion note.
    /// </summary>
    [Fact]
    public void FloatingDocumentRestore_ReusesTheDragTearOffPath_AndReWiresPerWindowState()
    {
        var src = ReadRepoFile("src/Ui/ViewModels/WorkspaceViewModel.Docking.cs");

        Assert.Contains("_factory.SplitToWindow(shellDock, docs[0]", src);
        Assert.Contains("_factory.MoveDockable(shellDock, targetDock, extra, null)", src);

        // Without these, a restored torn-off window shows "Close Workspace" and no macOS menu bar.
        Assert.Contains("TryWireHostWindowsUndo", src);
        Assert.Contains("TryWireWindowFocusTracking", src);

        // Membership stays the open list's call.
        Assert.Contains("ReferenceEquals(d.Owner, shellDock)", src);
    }

    [Fact]
    public void OnePlacerCoversToolAndDocumentWindows_SoTheCascadeSpansBoth()
    {
        var screens = new List<ScreenRect> { new(0, 0, 1920, 1040) };
        var placer  = new FloatingWindowPlacer(screens, sameConfiguration: false);

        // Two windows saved at the same lost position — one a tool panel, one a document.
        var first  = placer.Place(4000, 4000, 400, 300);
        var second = placer.Place(4000, 4000, 400, 300);

        Assert.NotEqual((first.X, first.Y), (second.X, second.Y));
        Assert.Equal(2, placer.Placed.Count);
    }

    [Fact]
    public void AFloatedDocumentWindow_ContributesNoToolPanelPlacement()
    {
        // The floating-window section of the layout records TOOL panels. A torn-off document window
        // is skipped entirely — including a foreign one, which must leave no trace in .cws at all.
        var factory = new CircuitRfDockFactory();
        var docWin  = factory.CreateDockWindow();
        docWin.X = 100; docWin.Y = 100; docWin.Width = 400; docWin.Height = 300;
        docWin.Layout = new RootDock
        {
            VisibleDockables = factory.CreateList<IDockable>(new DocumentDock
            {
                VisibleDockables = factory.CreateList<IDockable>(new StubDocument("Foreign", StubDocument.StubKind.Welcome)),
            }),
        };

        var root = new RootDock
        {
            VisibleDockables = factory.CreateList<IDockable>(new DocumentDock()),
            Windows          = factory.CreateList(docWin),
        };

        Assert.Empty(DockLayoutCapture.Capture(root, []).FloatingWindows);
    }

    // ── Gate 12/13 — Hide/Show Dockers (§4A) ──────────────────────────────────

    [Fact]
    public void Gate12_CollapseThenRestore_BringsBackEveryFact_Exactly()
    {
        var arrangement = new CwsDockLayout
        {
            Sides = [new CwsDockSide { Side = DockSide.Left, Proportion = 0.22 }],
            Panels =
            [
                new CwsDockPanel { Id = DockPanelIds.ProjectTree, Side = DockSide.Left,   Group = 0, Order = 0, Active = false, Proportion = 0.55 },
                new CwsDockPanel { Id = DockPanelIds.Palette,     Side = DockSide.Left,   Group = 0, Order = 1, Active = true,  Proportion = 0.55 },
                new CwsDockPanel { Id = DockPanelIds.Properties,  Side = DockSide.Left,   Group = 1, Order = 0, Active = true,  Proportion = 0.45 },
                new CwsDockPanel { Id = DockPanelIds.Analyses,    Side = DockSide.Bottom, Group = 0, Order = 0, Active = true,  Proportion = 0.31 },
            ],
            FloatingWindows =
            [
                new CwsFloatingWindow { X = 640, Y = 180, Width = 420, Height = 300, Panels = [DockPanelIds.Messages], Active = DockPanelIds.Messages },
            ],
        };

        var (factory, _) = NewShell();
        var before = Capture(Apply(factory, arrangement));

        // Collapse …
        Apply(factory, DockLayoutDefaults.Collapsed(before));

        // … then restore from the stash, which is §2's schema — no second representation (R-dock-10).
        var after = Capture(Apply(factory, before));

        foreach (var id in DockPanelIds.All.Where(i => i != DockPanelIds.Messages))
        {
            var b = Panel(before, id);
            var a = Panel(after, id);
            Assert.Equal(b.Open, a.Open);
            Assert.Equal(b.Side, a.Side);
            Assert.Equal(b.Group, a.Group);
            Assert.Equal(b.Order, a.Order);
            Assert.Equal(b.Active, a.Active);
            Assert.Equal(b.Proportion, a.Proportion, 6);   // R-dock-11 — panel widths come back
        }

        var f = Assert.Single(after.FloatingWindows);
        Assert.Equal(640, f.X, 6);
        Assert.Equal(180, f.Y, 6);
        Assert.Equal(420, f.Width, 6);
        Assert.Equal(300, f.Height, 6);

        Assert.Equal(0.22, Assert.Single(after.Sides, s => s.Side == DockSide.Left).Proportion, 6);
    }

    [Fact]
    public void Gate13_Collapsing_HidesToolDocksAndFloatingToolWindows_ButKeepsTheDocuments()
    {
        var (factory, root) = NewShell();
        var before          = Capture(root);
        var docDockBefore   = factory.DocumentDock;

        var collapsed = Apply(factory, DockLayoutDefaults.Collapsed(before));

        Assert.Empty(DockLayoutCapture.EnumerateToolDocks(collapsed));
        Assert.True(collapsed.Windows is null || collapsed.Windows.Count == 0);

        // Document tabs stay — same DocumentDock instance, same documents.
        Assert.Same(docDockBefore, DockLayoutCapture.FindDocumentDock(collapsed));
        Assert.NotEmpty(factory.DocumentDock!.VisibleDockables!);
    }

    [Fact]
    public void Gate13_CollapsingAlsoRemovesAFloatingToolWindow()
    {
        var withFloat = new CwsDockLayout
        {
            Panels = [new CwsDockPanel { Id = DockPanelIds.ProjectTree, Side = DockSide.Left, Active = true, Proportion = 1.0 }],
            FloatingWindows =
            [
                new CwsFloatingWindow { X = 10, Y = 10, Width = 300, Height = 200, Panels = [DockPanelIds.Messages] },
            ],
        };

        var (factory, _) = NewShell();
        var root         = Apply(factory, withFloat);
        Assert.Single(root.Windows!);

        var collapsed = Apply(factory, DockLayoutDefaults.Collapsed(withFloat));
        Assert.True(collapsed.Windows is null || collapsed.Windows.Count == 0);
    }

    [Fact]
    public void Gate14_CollapsedIsNotWhatGetsPersisted_TheUnderlyingArrangementIs()
    {
        var underlying = DockLayoutDefaults.Default();
        var collapsed  = DockLayoutDefaults.Collapsed(underlying);

        // Collapsing produces a separate value and never mutates the arrangement it was derived from.
        Assert.All(collapsed.Panels, p => Assert.False(p.Open));
        Assert.All(underlying.Panels, p => Assert.True(p.Open));

        // What lands in .cws is the underlying arrangement, so reopening shows the panels.
        var json = DockLayoutSerialization.Write(underlying)!.ToJsonString();
        Assert.Contains("\"Open\":true", json.Replace(" ", ""));
        Assert.DoesNotContain("\"Open\":false", json.Replace(" ", ""));
    }

    /// <summary>
    /// The one line of gate 14 that lives only on <c>WorkspaceViewModel</c> (which cannot be
    /// constructed headlessly): while collapsed, the persisted layout is the STASH, not a fresh
    /// capture of the collapsed shell.
    /// </summary>
    [Fact]
    public void Gate14_ViewModelPersistsTheStashWhileCollapsed_SourceScan()
    {
        var src = ReadRepoFile("src/Ui/ViewModels/WorkspaceViewModel.Docking.cs");
        Assert.Contains("DockersCollapsed ? _preCollapseLayout : CaptureDockLayout()", src);

        var vm = ReadRepoFile("src/Ui/ViewModels/WorkspaceViewModel.cs");
        Assert.Contains("DockLayoutToPersist()", vm);
    }

    // ── Gate 15 — the menu reflects the state ─────────────────────────────────

    [Fact]
    public void Gate15_MenuLabelTellsTheUserWhichWayTheNextPressGoes()
    {
        var src = ReadRepoFile("src/Ui/ViewModels/WorkspaceViewModel.Docking.cs");
        Assert.Contains("DockersCollapsed ? \"Show Dockers\" : \"Hide Dockers\"", src);

        var axaml = ReadRepoFile("src/Ui/Views/WorkspaceWindow.axaml");
        Assert.Contains("Header=\"{Binding DockersMenuHeader}\"", axaml);
        Assert.Contains("ToolTip.Tip=\"{Binding DockersMenuHeader}\"", axaml);

        // A NativeMenuItem cannot bind (no DataContext), so it is relabelled in code-behind instead.
        var codeBehind = ReadRepoFile("src/Ui/Views/WorkspaceWindow.axaml.cs");
        Assert.Contains("UpdateNativeDockersHeader", codeBehind);
        Assert.Contains("DockersCollapsedChanged", codeBehind);
    }

    [Fact]
    public void HideShowDockers_HasAKeyboardShortcutOnBothModifierConventions()
    {
        var axaml = ReadRepoFile("src/Ui/Views/WorkspaceWindow.axaml");
        Assert.Contains("Gesture=\"Ctrl+Shift+H\"  Command=\"{Binding HideShowDockersCommand}\"", axaml);
        Assert.Contains("Gesture=\"Meta+Shift+H\"  Command=\"{Binding HideShowDockersCommand}\"", axaml);
    }

    /// <summary>The gesture audit R-dock's own instruction demands: H was free before it was taken.</summary>
    [Fact]
    public void HideShowDockersShortcut_CollidesWithNothingElseInTheWindow()
    {
        var axaml = ReadRepoFile("src/Ui/Views/WorkspaceWindow.axaml");
        var hits  = System.Text.RegularExpressions.Regex
            .Matches(axaml, @"(?:Input)?Gesture=""([^""]*\+H)""")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();

        Assert.Equal(["Ctrl+Shift+H", "Meta+Shift+H"], hits.OrderBy(s => s).ToList());
    }

    [Fact]
    public void HideShowDockersIsNoLongerAPlaceholderThatOnlyPostsAMessage()
    {
        // R13a: a command either does something or is disabled with a stated reason. This one used
        // to be enabled, look actionable, and only post "use Dock title-bar controls to …".
        var src = ReadRepoFile("src/Ui/ViewModels/WorkspaceViewModel.cs");
        Assert.DoesNotContain("use Dock title-bar controls", src);
    }

    // ── Gate 19 — a restored floating window still goes through validation ────

    [Fact]
    public void Gate19_FloatingToolWindowSavedOffScreen_IsRelocatedByTheBuilder()
    {
        var state = new CwsDockLayout
        {
            Panels = [new CwsDockPanel { Id = DockPanelIds.ProjectTree, Side = DockSide.Left, Active = true, Proportion = 1.0 }],
            FloatingWindows =
            [
                new CwsFloatingWindow { X = 3000, Y = 200, Width = 640, Height = 480, Panels = [DockPanelIds.Messages] },
            ],
        };

        var screens = new List<ScreenRect> { new(0, 0, 1920, 1040) };
        var placed  = new List<ScreenRect>();

        var (factory, _) = NewShell();
        var root = factory.CreateLayoutFromState(state, w =>
        {
            var r = ScreenPlacement.Place(new ScreenRect(w.X, w.Y, w.Width, w.Height), screens, placed);
            placed.Add(r);
            return r;
        });

        var window = Assert.Single(root.Windows!);
        Assert.NotEqual(3000, window.X);
        Assert.True(window.X + window.Width <= 1920 + 1e-6);

        var bar = ScreenPlacement.TitleBarOf(new ScreenRect(window.X, window.Y, window.Width, window.Height));
        Assert.True(screens[0].Contains(bar), "an owned window still needs its title bar reachable");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string ReadRepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var full = Path.Combine(dir!.FullName, relativePath);
        Assert.True(File.Exists(full), $"expected repo file not found: {relativePath}");
        return File.ReadAllText(full);
    }
}

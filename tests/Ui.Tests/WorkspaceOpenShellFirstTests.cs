using System;
using System.IO;
using System.Linq;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui.Docking;
using CircuitRF.Ui.ViewModels;
using CircuitRF.Ui.ViewModels.Dock;
using Xunit;

namespace CircuitRF.Ui.Tests;

// ──────────────────────────────────────────────────────────────────────────────
//  A large workspace opens SHELL FIRST.
//
//  Owner instruction, 2026-09-04: set the dock panels up in the workspace's own positions, get them
//  rendered, and only then open the documents — so a workspace carrying a big .clay opens in a
//  visible, systematic order instead of sitting on the default arrangement until every read is done.
//
//  It used to be one call made LAST, for a reason that was real: the apply rebuilds the whole tree
//  and re-hosts the existing DocumentDock, so applying it after the restore meant the rebuilt shell
//  picked up a dock that already held its tabs. Splitting it into a SHELL phase and a DOCUMENTS phase
//  keeps that property (the primary dock is still preserved and still pane zero) and moves everything
//  the user is waiting to see to the front of the open.
//
//  Two of the three properties below are drivable headlessly — the membership predicate, and the real
//  factory building a split from it. The ORDER of the steps inside SwitchToWorkspace is not: it is a
//  sequence of awaits on the UI thread, which this project's tests may not run (see the header of
//  AsyncDocumentOpenRoutingTests, which pins its own call-site properties the same way).
// ──────────────────────────────────────────────────────────────────────────────

public sealed class WorkspaceOpenShellFirstTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "crf-open-shell-" + Guid.NewGuid().ToString("N")[..8]);

    public WorkspaceOpenShellFirstTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private string Touch(string relative)
    {
        var abs = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllText(abs, "");
        return abs;
    }

    private string MakeDir(string relative)
    {
        var abs = Path.Combine(_root, relative);
        Directory.CreateDirectory(abs);
        return abs;
    }

    private static CwsFile WithDocuments(params CwsOpenDocument[] docs) =>
        new() { OpenDocuments = docs.ToList() };

    // ── The membership predicate, one step earlier in time ────────────────────

    /// <summary>
    /// R-dock-2 is unchanged — the open list decides membership — but the shell phase runs BEFORE any
    /// document is open, so the answer has to come from the list itself. Asking
    /// <c>_openDocsByPath</c> there would report "nothing is open" and drop every split pane on every
    /// single open, which is the one way this refactor could have gone quietly wrong.
    /// </summary>
    [Fact]
    public void WillRestoreDocument_AnswersFromTheOpenList_NotFromWhatIsAlreadyOpen()
    {
        Touch("Amp/schematic/Amp.csch");
        Touch("tech/pcb.ctech");

        var will = WorkspaceViewModel.WillRestoreDocument(
            WithDocuments(
                new CwsOpenDocument { Path = "Amp/schematic/Amp.csch", Kind = "schematic" },
                new CwsOpenDocument { Path = "tech/pcb.ctech",         Kind = "tech" }),
            _root);

        Assert.True(will("Amp/schematic/Amp.csch"));
        Assert.True(will("tech/pcb.ctech"));
        Assert.False(will("never/mentioned.csch"));
    }

    /// <summary>
    /// The existence checks mirror the restore loop's own, case for case. A document listed in the
    /// <c>.cws</c> whose file has been deleted is skipped there, so counting it here would rebuild the
    /// blank half-window R-dock-2 exists to prevent.
    /// </summary>
    [Fact]
    public void WillRestoreDocument_ExcludesADocumentThatIsNoLongerOnDisk()
    {
        Touch("Amp/schematic/Amp.csch");

        var will = WorkspaceViewModel.WillRestoreDocument(
            WithDocuments(
                new CwsOpenDocument { Path = "Amp/schematic/Amp.csch", Kind = "schematic" },
                new CwsOpenDocument { Path = "gone/deleted.csch",      Kind = "schematic" }),
            _root);

        Assert.True(will("Amp/schematic/Amp.csch"));
        Assert.False(will("gone/deleted.csch"));
    }

    /// <summary>A cell is a FOLDER — checking it with File.Exists would drop every cell tab's pane.</summary>
    [Fact]
    public void WillRestoreDocument_ChecksACellAsADirectory()
    {
        MakeDir("Amp.ccell");

        var will = WorkspaceViewModel.WillRestoreDocument(
            WithDocuments(new CwsOpenDocument { Path = "Amp.ccell", Kind = "cell" }), _root);

        Assert.True(will("Amp.ccell"));
    }

    [Fact]
    public void WillRestoreDocument_ResolvesAnAbsolutePathEntryToo()
    {
        var abs = Touch("Amp/schematic/Amp.csch");

        var will = WorkspaceViewModel.WillRestoreDocument(
            WithDocuments(new CwsOpenDocument { Path = abs, Kind = "schematic" }), _root);

        // The key stays exactly as .cws spelled it — that is what the layout block's own document
        // keys are reconciled against.
        Assert.True(will(abs));
    }

    [Fact]
    public void WillRestoreDocument_OnAWorkspaceWithNoOpenDocuments_IsEmptyRatherThanEverythingTrue()
    {
        var will = WorkspaceViewModel.WillRestoreDocument(new CwsFile(), _root);
        Assert.False(will("anything.csch"));
    }

    // ── The shell the predicate produces ──────────────────────────────────────

    /// <summary>
    /// THE property this refactor rests on: driven through the real factory with the shell phase's own
    /// predicate and NOTHING open, a saved side-by-side split still builds both panes. The old
    /// predicate — "is it open right now" — would have returned false for both and collapsed the
    /// arrangement to a single tab strip.
    /// </summary>
    [Fact]
    public void TheShellPhase_BuildsBothPanesOfASavedSplit_WithNoDocumentOpenYet()
    {
        Touch("Amp/schematic/Amp.csch");
        Touch("tech/pcb.ctech");

        var f = new CircuitRfDockFactory();
        f.CreateLayout();
        var preserved = f.DocumentDock;

        var will = WorkspaceViewModel.WillRestoreDocument(
            WithDocuments(
                new CwsOpenDocument { Path = "Amp/schematic/Amp.csch", Kind = "schematic" },
                new CwsOpenDocument { Path = "tech/pcb.ctech",         Kind = "tech" }),
            _root);

        var root = f.CreateLayoutFromState(StateWithSplit(), floatingGeometry: null, documentIsOpen: will);

        Assert.Equal(2, DockLayoutCapture.EnumerateDocumentDocks(root).Count());
        Assert.Equal(2, f.RestoredDocumentPanes.Count);

        // …and the preserved dock is still pane zero, so the documents opened AFTER this phase land
        // in the visible tree rather than in an orphaned dock.
        Assert.Same(preserved, f.RestoredDocumentPanes[0].Dock);
        Assert.Same(preserved, f.DocumentDock);
    }

    /// <summary>
    /// The panes the shell phase builds are left EMPTY on purpose — phase two moves the documents in.
    /// Asserted so the "build them, then fill them" split cannot be quietly turned back into one step.
    /// </summary>
    [Fact]
    public void TheShellPhase_LeavesTheSecondaryPaneEmpty_ForPhaseTwoToFill()
    {
        Touch("Amp/schematic/Amp.csch");
        Touch("tech/pcb.ctech");

        var f = new CircuitRfDockFactory();
        f.CreateLayout();

        var will = WorkspaceViewModel.WillRestoreDocument(
            WithDocuments(
                new CwsOpenDocument { Path = "Amp/schematic/Amp.csch", Kind = "schematic" },
                new CwsOpenDocument { Path = "tech/pcb.ctech",         Kind = "tech" }),
            _root);

        f.CreateLayoutFromState(StateWithSplit(), floatingGeometry: null, documentIsOpen: will);

        var secondary = f.RestoredDocumentPanes[1];
        Assert.Empty(secondary.Dock.VisibleDockables ?? []);
        Assert.Equal(["tech/pcb.ctech"], secondary.Documents);
    }

    /// <summary>A pane whose documents have ALL been deleted is still dropped, not built blank.</summary>
    [Fact]
    public void TheShellPhase_StillDropsAPaneWhoseDocumentsAreGoneFromDisk()
    {
        Touch("Amp/schematic/Amp.csch");   // the .ctech is deliberately absent

        var f = new CircuitRfDockFactory();
        f.CreateLayout();

        var will = WorkspaceViewModel.WillRestoreDocument(
            WithDocuments(
                new CwsOpenDocument { Path = "Amp/schematic/Amp.csch", Kind = "schematic" },
                new CwsOpenDocument { Path = "tech/pcb.ctech",         Kind = "tech" }),
            _root);

        var root = f.CreateLayoutFromState(StateWithSplit(), floatingGeometry: null, documentIsOpen: will);

        Assert.Single(DockLayoutCapture.EnumerateDocumentDocks(root));
        Assert.Empty(f.RestoredDocumentPanes);
    }

    // ── Persistence stays suspended for the whole open ────────────────────────

    /// <summary>
    /// The open now spans several dispatcher turns, and for most of them the dock tree is deliberately
    /// incomplete (panes built, not yet filled). The three-second <c>.cws</c> debounce is a
    /// DispatcherTimer and fires perfectly happily inside that window; what it would write is the
    /// half-built arrangement over the user's real one.
    /// </summary>
    [Fact]
    public void SuspendLayoutPersistence_HoldsAcrossNestingAndReleasesExactlyOnce()
    {
        var vm = new WorkspaceViewModel();
        Assert.False(vm.LayoutPersistenceSuspended);

        using (vm.SuspendLayoutPersistence())
        {
            Assert.True(vm.LayoutPersistenceSuspended);

            // A layout apply inside the open nests its own suppression — leaving it must not lift the
            // outer one.
            vm.WhileRebuildingLayout(() => Assert.True(vm.LayoutPersistenceSuspended));
            Assert.True(vm.LayoutPersistenceSuspended);
        }

        Assert.False(vm.LayoutPersistenceSuspended);
    }

    [Fact]
    public void DisposingTheSuspensionTwice_DoesNotLeaveItPermanentlySuspended()
    {
        var vm = new WorkspaceViewModel();
        var scope = vm.SuspendLayoutPersistence();

        scope.Dispose();
        scope.Dispose();

        Assert.False(vm.LayoutPersistenceSuspended);
    }

    // ── The order of the open itself ──────────────────────────────────────────

    /// <summary>
    /// Source-scanned, and deliberately: the owner-facing property is the ORDER of a sequence of
    /// awaits on the UI thread, which this project's tests may not run. What can go wrong is a step
    /// being moved back behind the documents — which no behavioural test in this suite would notice,
    /// because the workspace still opens either way, just with the panels arriving last.
    /// </summary>
    [Fact]
    public void SwitchToWorkspace_PutsTheShellUpAndRendersIt_BeforeAnythingSlow()
    {
        var vm = ReadWorkspaceViewModelSource();
        int body = vm.IndexOf("private async Task SwitchToWorkspace(string cwsPath)", StringComparison.Ordinal);
        Assert.True(body > 0, "SwitchToWorkspace has been renamed; this test tracks its step order.");

        int Step(string needle)
        {
            int at = vm.IndexOf(needle, body, StringComparison.Ordinal);
            Assert.True(at > 0, $"SwitchToWorkspace no longer contains '{needle}'.");
            return at;
        }

        int shell      = Step("ApplyRestoredDockShell(dockLayoutRead");
        int rendered   = Step("await ShellRenderedAsync()");
        int generated  = Step("await RegenerateAllGeneratedCellsAsync(cwsPath)");
        int documents  = Step("await RestoreOpenDocumentsAsync(cws, workspaceDir");
        int finished   = Step("FinishRestoredDockLayout(dockLayoutRead)");

        // The shell, then a frame, then everything the user is waiting through, then the documents,
        // then the documents into their panes.
        Assert.True(shell < rendered,     "the shell must be applied before the frame is waited for");
        Assert.True(rendered < generated, "the shell must be on screen before the generated-cell pass");
        Assert.True(generated < documents, "generated cells are warmed before any layout opens");
        Assert.True(documents < finished,  "documents move into their panes only once they exist");
    }

    /// <summary>
    /// The clean-slate default layout is installed a few steps above the shell phase, and NOTHING may
    /// await between the two — an await there paints the default arrangement for a frame, which is
    /// exactly the flash this change removes.
    /// </summary>
    [Fact]
    public void NoAwaitSitsBetweenTheCleanSlateRebuildAndTheWorkspacesOwnArrangement()
    {
        var vm = ReadWorkspaceViewModelSource();
        int body      = vm.IndexOf("private async Task SwitchToWorkspace(string cwsPath)", StringComparison.Ordinal);
        int rebuild   = vm.IndexOf("_factory.CreateDefaultLayout(", body, StringComparison.Ordinal);
        int shell     = vm.IndexOf("ApplyRestoredDockShell(dockLayoutRead", body, StringComparison.Ordinal);
        Assert.True(rebuild > 0 && shell > rebuild);

        Assert.DoesNotContain("await ", vm[rebuild..shell]);
    }

    /// <summary>
    /// The debounced write is suppressed for the whole open, not just for each individual apply — and
    /// a debounce already counting down when the switch begins is stopped, because the suppression
    /// flag only stops NEW ones being armed.
    /// </summary>
    [Fact]
    public void TheWholeOpenRunsUnderSuspendedLayoutPersistence()
    {
        var vm = ReadWorkspaceViewModelSource();

        Assert.Contains("using (SuspendLayoutPersistence())", vm);
        Assert.Contains("if (LayoutPersistenceSuspended) return;", vm);
        Assert.Contains("_cwsSaveTimer?.Stop();", vm);
    }

    private static string ReadWorkspaceViewModelSource() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "src", "Ui", "ViewModels", "WorkspaceViewModel.cs"));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static CwsDockLayout StateWithSplit() => new()
    {
        DocumentRegion = new CwsDocumentRegion
        {
            Orientation = "Horizontal",
            Children =
            {
                new CwsDocumentRegion { Documents = { "Amp/schematic/Amp.csch" } },
                new CwsDocumentRegion { Documents = { "tech/pcb.ctech" }, Active = "tech/pcb.ctech" },
            },
        },
    };
}

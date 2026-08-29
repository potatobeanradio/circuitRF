using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Ui.Docking;
using CircuitRF.Ui.ViewModels.Dock;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;
using Xunit;

namespace CircuitRF.Ui.Tests;

// ──────────────────────────────────────────────────────────────────────────────
//  "The active document" in a SPLIT document area.
//
//  Owner report, 2026-08-29: with a .clay and a .csch docked side by side, ⌘S saved the layout and
//  never the schematic.
//
//  WorkspaceViewModel resolved the active document as `_focusedWindowDocument ??
//  _factory.DocumentDock?.ActiveDockable`, and `_factory.DocumentDock` is the PRIMARY pane and only
//  the primary — it is assigned when the layout is built and never re-pointed. Only that one dock was
//  subscribed for ActiveDockable changes, too. So in a split, every command resolving through there —
//  Save, Close Window, Run Analysis, Generate Netlist, Check Design Rules, the exports, the undo
//  target — was pinned to whatever pane 0 happened to show.
//
//  These run headlessly against the REAL factory driving Dock's own SplitToDock, for the reason
//  SplitDocumentAreaLayoutTests states at length: a hand-built "two DocumentDocks" tree is the wrong
//  shape and passes tests the app then fails.
// ──────────────────────────────────────────────────────────────────────────────

public sealed class SplitDocumentAreaActiveDocumentTests
{
    /// <summary>
    /// The defect itself, stated as a property of the dock tree: after a split there are two panes,
    /// and the primary — the only one the resolution consulted — cannot name the other's document.
    /// </summary>
    [Fact]
    public void TheSecondPanesDocument_IsUnreachable_ThroughThePrimaryDockAlone()
    {
        var (root, layout, schematic) = SplitShell();

        var primary = DockLayoutCapture.EnumerateDocumentDocks(root).Single();

        // Pane 0 keeps the layout; the schematic was dragged out into a pane of its own.
        Assert.Same(layout, primary.ActiveDockable);
        Assert.NotSame(schematic, primary.ActiveDockable);
        Assert.DoesNotContain(schematic, primary.VisibleDockables ?? []);
    }

    /// <summary>
    /// And why a walk over <c>IDocumentDock</c> cannot repair it: the pane Dock builds for a dragged
    /// document is a plain ProportionalDock. Identifying a pane by what it HOLDS finds both.
    /// </summary>
    [Fact]
    public void ADraggedOutPane_IsNotADocumentDock_SoPanesAreFoundByWhatTheyHold()
    {
        var (root, layout, schematic) = SplitShell();

        // The type-based walk sees one pane — this is the trap, not a bug in the walk.
        Assert.Single(DockLayoutCapture.EnumerateDocumentDocks(root));

        var panes = DockLayoutCapture.EnumerateDocumentPanes(root).ToList();
        Assert.Equal(2, panes.Count);

        // Exactly one pane per document, and the dragged-out one is NOT an IDocumentDock.
        var schematicPane = panes.Single(p => p.VisibleDockables?.Contains(schematic) == true);
        var layoutPane    = panes.Single(p => p.VisibleDockables?.Contains(layout) == true);

        Assert.NotSame(schematicPane, layoutPane);
        Assert.IsNotAssignableFrom<IDocumentDock>(schematicPane);

        // Each pane names its own document as active, which is what makes a pane a usable answer to
        // "which document is the user working on".
        Assert.Same(schematic, schematicPane.ActiveDockable);
        Assert.Same(layout,    layoutPane.ActiveDockable);

        // Owner-side: the pane the document reports as its owner is the pane the walk found, so
        // recording `document.Owner` on a canvas click names something the walk can still verify.
        Assert.Same(schematicPane, schematic.Owner);
    }

    /// <summary>
    /// A pane that is gone must not go on answering. When the last document leaves the dragged-out
    /// pane, the walk stops reporting it — which is what makes WorkspaceViewModel fall back to the
    /// primary instead of naming a document nothing can show.
    /// </summary>
    [Fact]
    public void APaneThatLosesItsLastDocument_IsNoLongerFound()
    {
        var (root, _, schematic) = SplitShell();

        var schematicPane = DockLayoutCapture.EnumerateDocumentPanes(root)
            .Single(p => p.VisibleDockables?.Contains(schematic) == true);

        new CircuitRfDockFactory().RemoveDockable(schematic, collapse: true);

        Assert.DoesNotContain(schematicPane, DockLayoutCapture.EnumerateDocumentPanes(root));
    }

    // ── The wiring, by source scan — WorkspaceViewModel needs an Avalonia app host ────────────────

    /// <summary>
    /// The resolution itself, and the two subscriptions that feed it. A source scan because
    /// WorkspaceViewModel cannot be constructed headlessly, which is the same fallback the rest of
    /// this suite uses for view-model wiring claims.
    /// </summary>
    [Fact]
    public void TheActiveDocument_ResolvesThroughTheFocusedPane_NotThePrimaryDock()
    {
        string vm = ReadWorkspaceViewModel();

        // The resolution consults the tracked pane before falling back to the primary.
        Assert.Contains("?? ActiveDocumentPaneInShell?.ActiveDockable", vm, System.StringComparison.Ordinal);

        // EVERY pane is subscribed, not just _factory.DocumentDock. The old spelling must be gone, or
        // a second pane's tab change would still be invisible.
        Assert.Contains("SubscribeToDocumentPanes()", vm, System.StringComparison.Ordinal);
        Assert.DoesNotContain("newNpc.PropertyChanged += OnDocumentDockPropertyChanged", vm, System.StringComparison.Ordinal);

        // The activation fan-out reads the pane that RAISED the change, not the primary.
        Assert.Contains("var pane = sender as IDock ?? _factory.DocumentDock;", vm, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// Clicking into a document changes no ActiveDockable when it is already its pane's active tab —
    /// the everyday gesture in a side-by-side layout. Two signals cover it: Dock's own focus event
    /// (every document type) and the editors' CanvasInteracted (known to fire).
    /// </summary>
    [Fact]
    public void ClickingIntoADocument_RecordsItsPane_ByBothAvailableSignals()
    {
        string vm = ReadWorkspaceViewModel();

        Assert.Contains("_factory.FocusedDockableChanged +=", vm, System.StringComparison.Ordinal);

        // Guarded on the pane AND the document — SetFocusedDockable runs inside SetActiveDockable,
        // which the CanvasInteracted handlers call, so an unguarded fan-out here would re-enter.
        Assert.Contains("ReferenceEquals(pane, _activeDocumentPane)", vm, System.StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(document, _lastActivatedDocument)", vm, System.StringComparison.Ordinal);

        // It runs the SAME activation the tab change runs — which is what retargets Undo — but
        // without grabbing keyboard focus, because the user's own click already placed it.
        Assert.Contains("ActivateDocument(document, requestActivationFocus: false);", vm, System.StringComparison.Ordinal);

        // All three editor canvases report their pane.
        foreach (string handler in (string[])
                 ["OnSchematicCanvasInteracted", "OnLayoutCanvasInteracted", "OnSymbolCanvasInteracted"])
        {
            int at = vm.IndexOf($"private void {handler}(", System.StringComparison.Ordinal);
            Assert.True(at > 0, $"{handler} is gone");
            Assert.Contains("MarkActiveDocumentPane(doc);", vm[at..(at + 400)], System.StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Undo specifically (owner, 2026-08-29: undo did not respect which document was in focus).
    /// <c>SetActiveUndoTarget</c> was reached only from the ActiveDockable change, and a pane holding
    /// ONE document never changes it — so moving between two side-by-side panes retargeted nothing
    /// and Ctrl+Z went on editing the pane the user had left. Both signals now run the one activation
    /// path, and that path is where the undo target is set.
    /// </summary>
    [Fact]
    public void TheUndoTarget_IsRetargeted_ByAPaneChangeAndNotOnlyByATabChange()
    {
        string vm = ReadWorkspaceViewModel();

        int at = vm.IndexOf("private void ActivateDocument(", System.StringComparison.Ordinal);
        Assert.True(at > 0, "ActivateDocument is gone — the activation path moved");

        int end = vm.IndexOf("\n    // ---- Per-window active document", at, System.StringComparison.Ordinal);
        Assert.True(end > at);
        string body = vm[at..end];

        // The undo target is set by the shared path, so both entry points get it.
        Assert.Contains("SetActiveUndoTarget(activeDockable as IUndoableDocument);", body, System.StringComparison.Ordinal);

        // …and SetActiveUndoTarget must not be reachable from the tab change ALONE any more.
        Assert.Contains("ActivateDocument(pane?.ActiveDockable);", vm, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// The schematic had no canvas-focus signal at all — the layout and symbol editors did. That gap
    /// is the reason the report named the schematic specifically.
    /// </summary>
    [Fact]
    public void TheSchematicCanvas_ReportsItsOwnFocus_LikeTheLayoutAndSymbolCanvasesAlreadyDid()
    {
        string doc  = ReadRepo(Path.Combine("src", "Ui", "Schematic", "SchematicDocument.cs"));
        string view = ReadRepo(Path.Combine("src", "Ui", "Views", "Content", "SchematicView.axaml.cs"));

        Assert.Contains("public event Action? CanvasInteracted;", doc, System.StringComparison.Ordinal);
        Assert.Contains("public void NotifyCanvasInteracted()", doc, System.StringComparison.Ordinal);
        Assert.Contains(
            "SchematicCanvasCtrl.GotFocus += (_, _) => _subscribedDoc?.NotifyCanvasInteracted();",
            view,
            System.StringComparison.Ordinal);
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────

    private static string ReadWorkspaceViewModel()
        => ReadRepo(Path.Combine("src", "Ui", "ViewModels", "WorkspaceViewModel.cs"));

    private static string ReadRepo(string relative)
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, relative));
    }

    /// <summary>
    /// A shell whose document area is split by the LIBRARY'S OWN operation — the layout stays in the
    /// original strip, the schematic is dragged out to the right. See
    /// <see cref="SplitDocumentAreaLayoutTests"/>'s own fixture note for why a hand-built two-dock
    /// tree would prove nothing.
    /// </summary>
    private static (IRootDock Root, IDockable Layout, IDockable Schematic) SplitShell()
    {
        var f = new CircuitRfDockFactory();

        var layout    = new StubDocument("Amp.clay", StubDocument.StubKind.Welcome);
        var schematic = new StubDocument("Amp.csch", StubDocument.StubKind.Welcome);

        var documents = new DocumentDock
        {
            Id               = "Documents",
            VisibleDockables = f.CreateList<IDockable>(layout, schematic),
            ActiveDockable   = layout,
        };

        var column = new ProportionalDock
        {
            Orientation      = Orientation.Vertical,
            VisibleDockables = f.CreateList<IDockable>(documents),
            ActiveDockable   = documents,
        };

        var root = f.CreateRootDock();
        root.VisibleDockables = f.CreateList<IDockable>(column);
        root.ActiveDockable   = column;
        f.InitLayout(root);

        // The real gesture. The removal matters — SplitToDock only WRAPS; the drag manager is what
        // takes the dockable out of its source first, and without it the document ends up in both.
        f.RemoveDockable(schematic, collapse: false);
        f.SplitToDock(documents, schematic, DockOperation.Right);

        return (root, layout, schematic);
    }
}

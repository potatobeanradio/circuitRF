using System.Linq;
using CircuitRF.Ui.Docking;
using CircuitRF.Ui.ViewModels.Dock;
using Dock.Model.Controls;
using Dock.Model.Core;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// <b>An emptied floating window is never left on screen</b> (owner, 2026-09-12: the Workspace panel,
/// undocked and then closed — the contents disappeared and the window stayed).
///
/// <para>The same shape had already been fixed twice, each time at the route that was known to produce
/// it: the P/A panel toggle (2026-08-17) and the tool chrome's ✕ (2026-09-02). What kept being wrong was
/// the LIST of routes, not the handling of any one of them — a float can also be emptied by Dock's own
/// close cascade, by a tab's context menu, or by a hide that files the panel under the FLOAT's own root
/// rather than the shell's, and none of those pass through this codebase's close paths at all. So the
/// rule is stated once as a post-condition instead: after anything closes a dockable, a floating window
/// that now shows nothing is closed.</para>
///
/// <para>These drive the REAL factory and the real <c>FactoryBase</c> operations, because the whole risk
/// is a mismatch between what Dock does to its model and what this codebase believes it does. No window
/// is ever presented — a float with no host is exactly what the headless paths are built for.</para>
/// </summary>
public sealed class EmptiedFloatingWindowTests
{
    private static (CircuitRfDockFactory Factory, IRootDock Root) NewShell()
    {
        var f = new CircuitRfDockFactory();
        var root = f.CreateLayout();
        f.InitLayout(root);
        return (f, root);
    }

    /// <summary>
    /// Tears <paramref name="tool"/> off into a floating window the way a tab drag does — Dock's own
    /// <c>RemoveDockable</c> + <c>CreateWindowFrom</c> — but registers the window without resolving a
    /// host, which is what keeps this headless (<c>CrfHostWindow</c> needs a windowing platform).
    /// </summary>
    private static IDockWindow FloatOut(CircuitRfDockFactory f, IRootDock root, ITool tool)
    {
        f.RemoveDockable(tool, collapse: true);

        var window = f.CreateWindowFrom(tool)!;
        root.Windows ??= f.CreateList<IDockWindow>();
        root.Windows.Add(window);
        window.Owner   = root;
        window.Factory = f;
        if (window.Layout is { } layout) f.InitDockable(layout, null);
        return window;
    }

    /// <summary>
    /// Dock's OWN close cascade deregisters the window itself — <c>RemoveDockable</c> → <c>CollapseDock</c>
    /// walks the emptied floating tree up to <c>RemoveWindow</c>. Measured rather than assumed, because
    /// it is the reason the sweep has nothing to do here, and if a future Dock release stops doing it this
    /// test says so instead of the symptom reappearing in the app.
    /// </summary>
    [Fact]
    public void AFloatEmptiedByDocksOwnCloseEndsUpClosed()
    {
        var (f, root) = NewShell();
        var tool = f.ProjectTreeTool!;

        FloatOut(f, root, tool);
        Assert.Single(root.Windows!);

        // The route a tab's context menu takes, and one this codebase's own close paths never see.
        f.ForceCloseDockable(tool);

        f.CloseEmptiedFloatingWindows();
        Assert.Empty(root.Windows!);
    }

    /// <summary>
    /// The 2026-08-17 measurement, restated as the case the sweep has to cover: <c>HideDockable</c> files
    /// a FLOATING tool under the float's own root, so the window is left showing nothing at all.
    /// </summary>
    [Fact]
    public void AFloatEmptiedByAHideIsClosedToo()
    {
        var (f, root) = NewShell();
        var tool = f.ProjectTreeTool!;

        var window = FloatOut(f, root, tool);
        var floatRoot = (IRootDock)window.Layout!;

        f.HideDockable(tool);

        // The panel went to the FLOAT's hidden list, not the shell's — the fact this whole area turns on.
        Assert.Contains(tool, floatRoot.HiddenDockables ?? []);
        Assert.DoesNotContain(tool, root.HiddenDockables ?? []);

        Assert.Equal(1, f.CloseEmptiedFloatingWindows());
        Assert.Empty(root.Windows!);
    }

    [Fact]
    public void AFloatThatStillShowsAPanelIsLeftAlone()
    {
        var (f, root) = NewShell();

        FloatOut(f, root, f.ProjectTreeTool!);

        Assert.Equal(0, f.CloseEmptiedFloatingWindows());
        Assert.Single(root.Windows!);
    }

    /// <summary>
    /// A window holding nothing but grab-handles is empty. An emptied proportional dock keeps its
    /// splitters, and a splitter is a real <c>IDockable</c> — so the "not a dock, therefore content" test
    /// counted it and kept the window.
    /// </summary>
    [Fact]
    public void SplittersAreNotContent()
    {
        var (f, root) = NewShell();
        var tool = f.ProjectTreeTool!;

        var window = FloatOut(f, root, tool);
        var floatRoot = (IRootDock)window.Layout!;

        // Replace the float's tool dock with a bare splitter, as a collapse leaves behind.
        floatRoot.VisibleDockables!.Clear();
        floatRoot.VisibleDockables.Add(f.CreateProportionalDockSplitter());
        floatRoot.ActiveDockable = null;

        Assert.False(CircuitRfDockFactory.HasContent(floatRoot));
        Assert.Equal(1, f.CloseEmptiedFloatingWindows());
        Assert.Empty(root.Windows!);
    }

    /// <summary>
    /// Closing a DOCKED panel must not take a co-existing float with it: the sweep is about windows that
    /// have been emptied, not about every window that happens to be open when something closes.
    /// </summary>
    [Fact]
    public void ClosingADockedPanelDoesNotDisturbAnotherFloat()
    {
        var (f, root) = NewShell();

        FloatOut(f, root, f.ProjectTreeTool!);
        f.ForceCloseDockable(f.MessagesTool!);   // a docked panel, elsewhere in the shell

        Assert.Equal(0, f.CloseEmptiedFloatingWindows());
        Assert.Single(root.Windows!);
    }

    // ── The wiring, which cannot be exercised without a window ────────────────

    /// <summary>
    /// <c>CloseToolPanel</c> must ask the live TREE where the panel is before it asks the shell root's
    /// pinned lists. Asked the other way round, a panel that is in a float and also named in one of those
    /// lists is HIDDEN — and a floating tool's hide is the vanished-contents-and-a-window-left-open bug
    /// itself.
    /// </summary>
    [Fact]
    public void CloseToolPanelAsksTheTreeBeforeThePinnedLists()
    {
        var src  = ReadRepoFile("src/Ui/ViewModels/WorkspaceViewModel.Docking.cs");
        var i    = src.IndexOf("internal bool CloseToolPanel(ITool tool)", System.StringComparison.Ordinal);
        Assert.True(i > 0, "CloseToolPanel not found");
        var body = src[i..(i + 3000)];

        var tree   = body.IndexOf("var inTree = _factory.TryFindTool(", System.StringComparison.Ordinal);
        var pinned = body.IndexOf("_factory.IsToolAutoHidden(tool)", System.StringComparison.Ordinal);

        Assert.True(tree   > 0, "CloseToolPanel no longer resolves the tree up front");
        Assert.True(pinned > 0, "CloseToolPanel no longer handles an auto-hidden panel");
        Assert.True(tree < pinned, "the pinned-list question is being asked before the tree");
    }

    [Fact]
    public void EveryCloseRaisesTheSweep()
    {
        var src = ReadRepoFile("src/Ui/ViewModels/WorkspaceViewModel.Docking.cs");

        // Dock's own close cascade included — the routes that never reach this file's close paths.
        Assert.Contains("_factory.DockableClosed    += (_, _) => CloseEmptiedFloatingToolWindows();", src);

        // …and a rebuild, which empties and refills windows on its way through, is not a verdict.
        var i = src.IndexOf("internal void CloseEmptiedFloatingToolWindows()", System.StringComparison.Ordinal);
        Assert.True(i > 0, "CloseEmptiedFloatingToolWindows not found");
        Assert.Contains("if (_layoutRebuildDepth > 0) return;", src[i..(i + 400)]);
    }

    /// <summary>
    /// A float torn off by a DRAG is built by <c>DockControl.HostWindowFactory</c>, not by the factory's
    /// own locator — so it needs the same workspace stamp, or every float made that way is attributed by
    /// the fallback guess instead (MW1 R-mw1-11).
    /// </summary>
    [Fact]
    public void ADraggedTearOffIsStampedWithItsWorkspace()
    {
        var src = ReadRepoFile("src/Ui/Views/WorkspaceWindow.axaml.cs");
        var i   = src.IndexOf("MainDockControl.HostWindowFactory", System.StringComparison.Ordinal);
        Assert.True(i > 0, "HostWindowFactory is no longer set on the dock control");
        Assert.Contains("OwningWorkspace = DataContext as WorkspaceViewModel", src[i..(i + 400)]);
    }

    private static string ReadRepoFile(string relative)
    {
        var dir = System.AppContext.BaseDirectory;
        while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir, "circuitrf.slnx")))
            dir = System.IO.Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return System.IO.File.ReadAllText(System.IO.Path.Combine(dir!, relative));
    }
}

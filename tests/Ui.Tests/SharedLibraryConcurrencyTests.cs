using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels.Dock;
using CircuitRF.Ui.ViewModels.ProjectTree;
using Xunit;

namespace CircuitRF.Ui.Tests;

// ═══════════════════════════════════════════════════════════════════════════════
//  SL4 gate — brief-shared-library-4-concurrency-and-latency.md §4.
//
//  Two people, and a wire. Everything else in the shared-library series is about one user and a set
//  of files; this is about what happens when the files are at the far end of a cable and someone else
//  has them open too.
//
//  NO TIMING TESTS. CLAUDE.md's benchmark-tier rule and the repo's standing preference both apply:
//  what is asserted here is COUNTERS for the structural property (CellStat.Calls) and behaviour
//  driven through injected clock/host/pid seams — never a wall-clock threshold, which measures the
//  machine, flakes under parallel load, and inverts under a debug build.
// ═══════════════════════════════════════════════════════════════════════════════

[Collection(CellStatGlobalsCollection.Name)]
public sealed class SharedLibraryConcurrencyTests : IDisposable
{
    private readonly string _root;
    private readonly string _ws;
    private readonly string _lib;

    public SharedLibraryConcurrencyTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf_sl4_" + Guid.NewGuid().ToString("N")[..8]);
        _ws   = Path.Combine(_root, "workspace");
        _lib  = Path.Combine(_root, "stdlib");
        Directory.CreateDirectory(_ws);
        Directory.CreateDirectory(_lib);
        WorkspacePersistence.SaveToFile(Path.Combine(_ws, ".cws"), new CwsFile());
        ResetGlobals();
    }

    public void Dispose()
    {
        ResetGlobals();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static void ResetGlobals()
    {
        CellStat.Clock        = null;
        CellStat.CacheEnabled = true;
        WorkspaceLock.Clock            = null;
        WorkspaceLock.HostName         = null;
        WorkspaceLock.UserName         = null;
        WorkspaceLock.ProcessId        = null;
        WorkspaceLock.ProcessIsRunning = null;
        WorkspaceLock.StaleAfter       = TimeSpan.FromHours(8);
        WorkspaceWritability.WritabilityProbe = null;
        WorkspaceWritability.ClearAllSessionReadOnly();
        CellSymbolResolver.InvalidateAll();
    }

    // ── Fixture ───────────────────────────────────────────────────────────────

    private static Symbol OnePin(double x) => new(
        primitives: [new LinePrimitive(SymbolColorRole.SymbolLine, SymbolStrokeTier.Normal, -100, 0, x, 0)],
        pins:       [new SymbolPin(-200, 0, 1, "a"), new SymbolPin(200, 0, 2, "b")],
        portCount:  2);

    /// <summary>A cell folder at <paramref name="relPath"/> under <paramref name="root"/>, with one
    /// <c>.csym</c> in it. Intermediate user folders are created — `passives/R0402` is the shape any
    /// librarian arrives at on the first day.</summary>
    private static string MakeCell(string root, string relPath, Symbol? symbol = null)
    {
        string parent = Path.Combine(root,
            Path.GetDirectoryName(relPath.Replace('/', Path.DirectorySeparatorChar)) ?? "");
        Directory.CreateDirectory(parent);
        string name    = Path.GetFileName(relPath);
        string cellDir = CellFolder.CreateCellFolder(parent, name);
        string symDir  = CellFolder.SubFolderPath(cellDir, ViewType.Symbol);
        SymbolPersistence.SaveToFile(Path.Combine(symDir, name + ".csym"), symbol ?? OnePin(100));
        return cellDir;
    }

    /// <summary>A schematic model in this workspace referencing <paramref name="n"/> cells.</summary>
    private (SchematicEditModel Model, string SchDir) SchematicOver(int n)
    {
        string schDir = Path.Combine(_ws, "top", "schematic");
        Directory.CreateDirectory(schDir);
        var model = new SchematicEditModel { SchematicDirectory = schDir };
        for (int i = 0; i < n; i++)
        {
            string cellDir = MakeCell(_ws, $"C{i}");
            model.Components.Add(new EditableComponent
            {
                InstanceName = $"X{i}", Symbol = SymbolKind.Generic,
                CellRef = Path.GetRelativePath(schDir, cellDir), X = i * 400, Y = 0,
            });
        }
        CellSymbolResolver.InvalidateAll();
        return (model, schDir);
    }

    /// <summary>Filesystem calls one render-model rebuild — one EDIT — makes.</summary>
    private static long CallsForOneEdit(SchematicEditModel model)
    {
        CellStat.ResetCalls();
        model.BuildRenderModel();
        return CellStat.Calls;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  §4 item 1 — filesystem calls per edit, before and after the cache
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// R-sl4-6's measurement, as a gate. <b>Four calls per referenced component per edit</b> —
    /// <c>Directory.Exists</c> on the cell folder, <c>Directory.Exists</c> and
    /// <c>Directory.GetFiles</c> on its symbol sub-folder, and the primary's mtime — and nothing
    /// amortises them, because <c>EditableSchematic.BuildRenderModel</c> re-resolves every component
    /// on every model change and the symbol cache cannot be consulted until the mtime is in hand.
    ///
    /// <para>This is the test that catches a future change re-introducing a per-component walk. It is
    /// deliberately an EXACT number rather than an upper bound: an inequality here would let the cost
    /// drift upward one call at a time without anything going red.</para>
    /// </summary>
    [Fact]
    public void WithoutTheCache_OneEditCostsFourFilesystemCallsPerReferencedComponent()
    {
        var (model, _) = SchematicOver(10);
        CellStat.CacheEnabled = false;
        model.BuildRenderModel();            // warm the SYMBOL cache; the .csym loads happen here

        Assert.Equal(40, CallsForOneEdit(model));
        Assert.Equal(40, CallsForOneEdit(model));   // and again — nothing amortises
    }

    /// <summary>
    /// R-sl4-7: with the cache on, a second edit inside T costs NOTHING, and the edit after T pays
    /// the full price again. That second half matters as much as the first — it is what makes the
    /// weakening a BOUND rather than an unbounded staleness.
    /// </summary>
    [Fact]
    public void WithTheCache_EditsInsideTAreFree_AndTheOneAfterTPaysAgain()
    {
        var (model, _) = SchematicOver(10);
        // Warm the SYMBOL cache with the stat cache off, so the .csym loads are not what is being
        // counted below and the first measured edit is a genuine stat-cache miss.
        CellStat.CacheEnabled = false;
        model.BuildRenderModel();

        var now = new DateTime(2026, 9, 3, 12, 0, 0, DateTimeKind.Utc);
        CellStat.Clock        = () => now;
        CellStat.CacheEnabled = true;               // both setters drop the cache

        Assert.Equal(40, CallsForOneEdit(model));   // first after the drop — full cost
        Assert.Equal(0,  CallsForOneEdit(model));   // same instant

        now += CellStat.Freshness;                  // exactly T later: still inside the bound
        Assert.Equal(0, CallsForOneEdit(model));

        now += TimeSpan.FromMilliseconds(1);        // past it
        Assert.Equal(40, CallsForOneEdit(model));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  §4 item 2 — the freshness bound holds
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// R-sl4-7. The guarantee being weakened is <i>"a change on disk is seen at the next resolve"</i>
    /// — the property that makes the librarian's edit reach every user without a restart. It is now
    /// <i>"seen within T of the next resolve"</i>, and this asserts both halves: the change is NOT
    /// seen inside T, and IS seen after it.
    ///
    /// <para>The clock is driven through the seam. A test that slept for T would take T, would flake
    /// under full-suite load, and would be measuring the machine.</para>
    /// </summary>
    [Fact]
    public void AChangedSymbolIsSeen_WithinTheStatedFreshnessBound()
    {
        string schDir = Path.Combine(_ws, "top", "schematic");
        Directory.CreateDirectory(schDir);
        string cellDir = MakeCell(_ws, "Amp", OnePin(100));
        string cellRef = Path.GetRelativePath(schDir, cellDir);

        var now = new DateTime(2026, 9, 3, 12, 0, 0, DateTimeKind.Utc);
        CellStat.Clock = () => now;
        CellSymbolResolver.InvalidateAll();

        var before = CellSymbolResolver.Resolve(cellRef, schDir);
        Assert.Equal(CellSymbolState.Resolved, before.State);
        Assert.Equal(100, ((LinePrimitive)before.Symbol!.Primitives[0]).X2);

        // The librarian saves a different symbol. Nothing in this process is told.
        SymbolPersistence.SaveToFile(
            Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Symbol), "Amp.csym"), OnePin(777));
        File.SetLastWriteTimeUtc(
            Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Symbol), "Amp.csym"),
            DateTime.UtcNow.AddSeconds(5));

        // Inside T the old drawing is still what renders — deliberately, and this is the whole of
        // what SL4 traded away.
        now += CellStat.Freshness - TimeSpan.FromMilliseconds(1);
        Assert.Equal(100, ((LinePrimitive)CellSymbolResolver.Resolve(cellRef, schDir).Symbol!.Primitives[0]).X2);

        // Past T, the mtime is re-read and the new symbol loads. No restart, no explicit refresh.
        now += TimeSpan.FromMilliseconds(2);
        Assert.Equal(777, ((LinePrimitive)CellSymbolResolver.Resolve(cellRef, schDir).Symbol!.Primitives[0]).X2);
    }

    /// <summary>The bound is a STATED one, and it is stated where a reader will find it. Two seconds:
    /// short enough to be invisible to a person walking between two machines, which is the fastest
    /// way the weakening could ever be observed.</summary>
    [Fact]
    public void TheFreshnessBoundIsOrderOneToTwoSeconds()
    {
        Assert.InRange(CellStat.Freshness, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  §4 item 3 — a negative is never cached
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// R-sl4-8. A share that blinks, or a folder the librarian is half-way through renaming, must not
    /// leave a design full of Not-Found glyphs that persist after the network has recovered — that
    /// reads as data loss and is not. The cell is resolved while it does not exist, created, and
    /// resolved again IMMEDIATELY, on a frozen clock: no time passes at all, so only a rule against
    /// caching the negative can make this pass.
    /// </summary>
    [Fact]
    public void ACellThatDidNotResolve_IsReAskedImmediately_NotAfterT()
    {
        string schDir = Path.Combine(_ws, "top", "schematic");
        Directory.CreateDirectory(schDir);

        var now = new DateTime(2026, 9, 3, 12, 0, 0, DateTimeKind.Utc);
        CellStat.Clock = () => now;                     // frozen — nothing can expire
        CellSymbolResolver.InvalidateAll();

        string cellRef = Path.GetRelativePath(schDir, Path.Combine(_ws, "Latecomer"));
        Assert.Equal(CellSymbolState.NotFound, CellSymbolResolver.Resolve(cellRef, schDir).State);

        MakeCell(_ws, "Latecomer");

        Assert.Equal(CellSymbolState.Resolved, CellSymbolResolver.Resolve(cellRef, schDir).State);
    }

    /// <summary>
    /// The same rule one level in: the cell folder was there all along and its symbol was not, which
    /// is what a cell mid-creation looks like — and what a folder being written over a slow share
    /// looks like too. An empty <c>Directory.GetFiles</c> is a negative in exactly R-sl4-8's sense.
    /// </summary>
    [Fact]
    public void ACellWhoseFirstSymbolHasJustBeenWritten_ResolvesImmediately()
    {
        string schDir = Path.Combine(_ws, "top", "schematic");
        Directory.CreateDirectory(schDir);
        string cellDir = CellFolder.CreateCellFolder(_ws, "Empty");
        string cellRef = Path.GetRelativePath(schDir, cellDir);

        var now = new DateTime(2026, 9, 3, 12, 0, 0, DateTimeKind.Utc);
        CellStat.Clock = () => now;
        CellSymbolResolver.InvalidateAll();

        Assert.Equal(CellSymbolState.PrimaryMissing, CellSymbolResolver.Resolve(cellRef, schDir).State);

        SymbolPersistence.SaveToFile(
            Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Symbol), "Empty.csym"), OnePin(100));

        Assert.Equal(CellSymbolState.Resolved, CellSymbolResolver.Resolve(cellRef, schDir).State);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  §4 item 4 — the referenced subtree is not walked on focus
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>Makes this workspace reference <c>stdlib</c> as a workspace, with cells in folders —
    /// the shape SL1 exists to make browsable.</summary>
    private void ReferenceTheLibrary(int cells = 6)
    {
        WorkspacePersistence.SaveToFile(Path.Combine(_lib, ".cws"), new CwsFile());
        for (int i = 0; i < cells; i++) MakeCell(_lib, $"passives/R{i}");

        WorkspacePersistence.SaveToFile(Path.Combine(_ws, ".cws"), new CwsFile
        {
            ReferencedWorkspaces = [new CwsWorkspaceRef { Alias = "L", Path = _lib }],
        });
        WorkspaceRootFinder.InvalidateCache();
    }

    /// <summary>The referenced sub-tree's own cells, by name.</summary>
    private static string[] ReferencedCellNames(ProjectTreeNode root) =>
        root.Children.Where(c => c.Kind == NodeKind.ReferencedWorkspacesGroup)
            .SelectMany(g => g.Children)
            .SelectMany(w => w.Children)
            .SelectMany(f => f.Kind == NodeKind.UserFolder ? f.Children : [f])
            .Where(n => n.Kind == NodeKind.Cell)
            .Select(n => n.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// R-sl4-10. Three assertions on one counting seam: the on-focus rescan reads NOTHING from the
    /// referenced library, an explicit Refresh reads it, and a workspace open reads it.
    ///
    /// <para>The workspace itself holds no cells of its own, so every call counted here comes from
    /// the referenced sub-tree. That walk is the whole cost: SL1 measured ~2,800 filesystem round
    /// trips for a 200-cell library, and it was happening on open, on every alt-tab back and on every
    /// dialog close.</para>
    /// </summary>
    [Fact]
    public void AReferencedSubtree_IsWalkedOnOpenAndRefresh_ButNotOnFocus()
    {
        ReferenceTheLibrary();
        CellStat.CacheEnabled = false;      // count the WALK, not the cache

        // On open.
        CellStat.ResetCalls();
        var opened = WorkspaceScanner.Scan(_ws);
        long onOpen = CellStat.Calls;
        Assert.True(onOpen > 0, "a workspace open must read the referenced library");
        Assert.Equal(6, ReferencedCellNames(opened).Length);

        // On focus — the reuse scan.
        CellStat.ResetCalls();
        var focused = WorkspaceScanner.Scan(_ws, ReferencedSubtrees.Reuse, opened);
        Assert.Equal(0, CellStat.Calls);

        // …and it still shows the library (R-sl4-11), not an empty node.
        Assert.Equal(6, ReferencedCellNames(focused).Length);

        // On explicit Refresh.
        CellStat.ResetCalls();
        WorkspaceScanner.Scan(_ws);
        Assert.Equal(onOpen, CellStat.Calls);
    }

    /// <summary>
    /// R-sl4-10, through the tool the view actually calls: <c>RefreshAsync</c> is the on-focus path
    /// (it fires on open, on every alt-tab back and on every dialog close) and <c>Refresh</c> is the
    /// button. Asserted here rather than only at the scanner because the routing is the part a later
    /// change would get wrong — a scanner that CAN skip the walk is no use if the focus path stops
    /// asking it to.
    /// </summary>
    [Fact]
    public async Task TheOnFocusRefresh_DoesNotReadTheReferencedLibrary_ButTheButtonDoes()
    {
        ReferenceTheLibrary();
        CellStat.CacheEnabled = false;

        var tool = new ProjectTreeTool();
        tool.SetWorkspace(_ws);

        CellStat.ResetCalls();
        await tool.RefreshAsync();
        Assert.Equal(0, CellStat.Calls);

        CellStat.ResetCalls();
        tool.Refresh();
        Assert.True(CellStat.Calls > 0, "pressing Refresh is the user asking for the library to be re-read");
    }

    /// <summary>
    /// R-sl4-10's third trigger. A reference added to the <c>.cws</c> AFTER the workspace was opened
    /// is the case where a sub-tree genuinely has not been walked — the on-focus rescan finds the new
    /// entry and, by rule, does not read it. Expanding it is what reads it.
    /// </summary>
    [Fact]
    public async Task ExpandingAnUnreadReferencedSubtree_IsWhatWalksIt()
    {
        var tool = new ProjectTreeTool();
        tool.SetWorkspace(_ws);                     // opened with no references at all

        ReferenceTheLibrary();                       // the librarian's alias arrives afterwards
        await tool.RefreshAsync();                   // a focus rescan sees the entry, unread

        var node = FindReferencedWorkspaceNode(tool);
        Assert.True(node.HoldsUnreadReference);
        Assert.Equal(NodeKind.NotReadYet, node.Children[0].Kind);

        node.IsExpanded = true;                      // the gesture

        var walked = FindReferencedWorkspaceNode(tool);
        Assert.False(walked.HoldsUnreadReference);
        Assert.Equal(6, ReferencedCellNames(WorkspaceScanner.Scan(_ws)).Length);
        Assert.True(walked.IsExpanded, "the node the user clicked must come back open");
    }

    private static ProjectTreeNodeViewModel FindReferencedWorkspaceNode(ProjectTreeTool tool)
    {
        var group = tool.RootItems[0].Children
            .First(c => c.Kind == NodeKind.ReferencedWorkspacesGroup);
        return group.Children.First(c => c.Kind == NodeKind.ReferencedWorkspace);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  §4 item 5 — a partially-walked referenced node renders its previous contents
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// R-sl4-11. <b>An empty library is the exact symptom SL1 exists to remove, and it must not come
    /// back through a caching rule.</b> A reuse scan keeps what the last walk found — including,
    /// deliberately, a cell the librarian has since DELETED, because "what was there a moment ago" is
    /// honest and "nothing" is not.
    /// </summary>
    [Fact]
    public void AReusedReferencedSubtree_KeepsThePreviousWalksContents()
    {
        ReferenceTheLibrary();
        var walked = WorkspaceScanner.Scan(_ws);
        Assert.Equal(6, ReferencedCellNames(walked).Length);

        Directory.Delete(Path.Combine(_lib, "passives", "R0"), recursive: true);

        var reused = WorkspaceScanner.Scan(_ws, ReferencedSubtrees.Reuse, walked);
        Assert.Equal(6, ReferencedCellNames(reused).Length);   // the previous walk, not a fresh one

        var refreshed = WorkspaceScanner.Scan(_ws);
        Assert.Equal(5, ReferencedCellNames(refreshed).Length); // and Refresh catches up
    }

    /// <summary>
    /// R-sl4-11's other half: a sub-tree with no previous walk at all renders as ITSELF — a node
    /// saying nothing has been read yet — rather than as an empty library. The placeholder is also
    /// the mechanism, since a node with no children draws no expander and could never be expanded.
    /// </summary>
    [Fact]
    public void AReferencedSubtreeNeverWalked_SaysSo_RatherThanRenderingEmpty()
    {
        ReferenceTheLibrary();

        var never = WorkspaceScanner.Scan(_ws, ReferencedSubtrees.Reuse, previous: null);

        var node = never.Children
            .First(c => c.Kind == NodeKind.ReferencedWorkspacesGroup).Children
            .First(c => c.Kind == NodeKind.ReferencedWorkspace);

        var placeholder = Assert.Single(node.Children);
        Assert.Equal(NodeKind.NotReadYet, placeholder.Kind);
        Assert.Contains("Not read yet", placeholder.Name, StringComparison.Ordinal);
        // Not an ERROR state: an unread library is an ordinary, expected result of the on-focus rule.
        Assert.Null(placeholder.WarningReason);
    }

    /// <summary>The same rule for a referenced LIBRARY, which takes the other of the two builders.
    /// The two used to diverge (SL1 unified their recursion); nothing here may let them diverge
    /// again.</summary>
    [Fact]
    public void AReferencedLIBRARY_FollowsTheSameRuleAsAReferencedWorkspace()
    {
        for (int i = 0; i < 3; i++) MakeCell(_lib, $"grp/C{i}");
        WorkspacePersistence.SaveToFile(Path.Combine(_ws, ".cws"),
            new CwsFile { LibraryRefs = { _lib } });
        WorkspaceRootFinder.InvalidateCache();
        CellStat.CacheEnabled = false;

        var walked = WorkspaceScanner.Scan(_ws);
        var libNode = walked.Children.First(c => c.Kind == NodeKind.LibrariesGroup).Children[0];
        Assert.Equal(3, libNode.Children[0].Children.Count);

        CellStat.ResetCalls();
        var reused = WorkspaceScanner.Scan(_ws, ReferencedSubtrees.Reuse, walked);
        Assert.Equal(0, CellStat.Calls);
        var reusedLib = reused.Children.First(c => c.Kind == NodeKind.LibrariesGroup).Children[0];
        Assert.Equal(3, reusedLib.Children[0].Children.Count);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  §4 item 6 — the advisory lock
    // ═══════════════════════════════════════════════════════════════════════════

    private static void PretendWeAre(string host, string user, int pid, DateTime now)
    {
        WorkspaceLock.HostName  = () => host;
        WorkspaceLock.UserName  = () => user;
        WorkspaceLock.ProcessId = () => pid;
        WorkspaceLock.Clock     = () => now;
    }

    [Fact]
    public void AWritableOpen_TakesTheLock_AndCloseRemovesIt()
    {
        PretendWeAre("lab-07", "engineer-a", 4242, new DateTime(2026, 9, 3, 9, 0, 0, DateTimeKind.Utc));

        Assert.True(WorkspaceLock.Take(_ws));
        var held = WorkspaceLock.Read(_ws);
        Assert.NotNull(held);
        Assert.Equal("lab-07",     held!.Host);
        Assert.Equal("engineer-a", held.User);
        Assert.Equal(4242,         held.ProcessId);
        Assert.True(WorkspaceLock.IsOurs(held));

        WorkspaceLock.Release(_ws);
        Assert.Null(WorkspaceLock.Read(_ws));
        Assert.False(File.Exists(Path.Combine(_ws, WorkspaceLock.FileName)));
    }

    /// <summary>
    /// R-sl4-1: <b>a read-only workspace takes no lock and needs none</b> — nobody can write it, so
    /// there is nothing to lose to a last-writer-wins race. This is the shared-library case, which is
    /// the whole workflow the series is measured against.
    /// </summary>
    [Fact]
    public void AReadOnlyWorkspace_TakesNoLock()
    {
        WorkspaceWritability.WritabilityProbe = _ => false;

        Assert.False(WorkspaceLock.Take(_ws));
        Assert.False(File.Exists(Path.Combine(_ws, WorkspaceLock.FileName)));
        Assert.Null(WorkspaceLock.Read(_ws));
    }

    /// <summary>
    /// The notice is suppressed for a session that cannot write. A read-only opener is not a party to
    /// last-writer-wins, which is the only thing the notice exists to bound — and the shared library
    /// IS this case, read-only to everyone but the librarian, so warning every engineer who opens it
    /// that the librarian is in there would put a modal in front of the workflow the whole series was
    /// written to support, about a hazard they cannot cause.
    ///
    /// <para>Asserted at the level that decides it: the lock is present and live, and the workspace is
    /// unwritable, so `Take` declines and the reading side has the same fact to key off.</para>
    /// </summary>
    [Fact]
    public void AReadOnlyOpener_IsNotAPartyToTheRace()
    {
        var now = new DateTime(2026, 9, 3, 9, 0, 0, DateTimeKind.Utc);
        PretendWeAre("lab-99", "librarian", 1234, now);
        WorkspaceLock.Take(_ws);                        // the librarian, who CAN write, has it open

        var held = WorkspaceLock.Read(_ws)!;
        PretendWeAre("lab-07", "engineer-a", 4242, now.AddMinutes(5));
        WorkspaceLock.ProcessIsRunning = _ => true;
        Assert.False(WorkspaceLock.IsStale(held));      // it is a live lock — the notice WOULD fire

        // …but this engineer's copy of the share is read-only, so nothing they do can reach it.
        WorkspaceWritability.WritabilityProbe = _ => false;
        Assert.True(WorkspaceWritability.IsReadOnly(_ws));
        Assert.False(WorkspaceLock.Take(_ws));
    }

    /// <summary>
    /// R-sl4-3, rule one: the lock names THIS host and a process id that is not running. That is a
    /// crash or a kill, and it is the case a user can do nothing about and should never be asked
    /// about.
    /// </summary>
    [Fact]
    public void AStaleLock_IsDetectedByADeadProcessOnThisHost()
    {
        var now = new DateTime(2026, 9, 3, 9, 0, 0, DateTimeKind.Utc);
        PretendWeAre("lab-07", "engineer-a", 4242, now);
        WorkspaceLock.Take(_ws);

        // A second circuitRF on the same machine, five minutes later. The recorded pid is gone.
        PretendWeAre("lab-07", "engineer-a", 9999, now.AddMinutes(5));
        WorkspaceLock.ProcessIsRunning = pid => pid != 4242;

        var held = WorkspaceLock.Read(_ws)!;
        Assert.False(WorkspaceLock.IsOurs(held));
        Assert.True(WorkspaceLock.IsStale(held));
        Assert.Contains("no longer running", WorkspaceLock.StaleNoticeFor(held, "workspace"),
                        StringComparison.Ordinal);

        // And a process that IS running is not stale — the rule must not fire on a live sibling.
        WorkspaceLock.ProcessIsRunning = _ => true;
        Assert.False(WorkspaceLock.IsStale(held));
    }

    /// <summary>
    /// R-sl4-3, rule two, and the one that has to carry a lock from ANOTHER host: that machine's
    /// process ids mean nothing here, so age is the only evidence available. Hours, not minutes —
    /// an engineer leaves a workspace open over lunch, and a threshold short enough to catch a crash
    /// promptly is short enough to declare a colleague's live session dead.
    /// </summary>
    [Fact]
    public void AStaleLock_IsDetectedByAge_AndAgeIsTheOnlyRuleForAnotherHost()
    {
        var opened = new DateTime(2026, 9, 3, 9, 0, 0, DateTimeKind.Utc);
        PretendWeAre("lab-99", "engineer-b", 1234, opened);
        WorkspaceLock.Take(_ws);

        PretendWeAre("lab-07", "engineer-a", 4242, opened.AddMinutes(20));
        // A live pid 1234 on OUR machine must not make the other host's lock look alive OR dead.
        WorkspaceLock.ProcessIsRunning = _ => false;

        var held = WorkspaceLock.Read(_ws)!;
        Assert.False(WorkspaceLock.IsStale(held));          // twenty minutes is a colleague at lunch

        PretendWeAre("lab-07", "engineer-a", 4242, opened + WorkspaceLock.StaleAfter + TimeSpan.FromMinutes(1));
        Assert.True(WorkspaceLock.IsStale(held));

        Assert.True(WorkspaceLock.StaleAfter >= TimeSpan.FromHours(1),
                    "R-sl4-3: hours, not minutes — a short threshold declares a live session dead");
    }

    /// <summary>
    /// R-sl4-2, which the brief calls non-negotiable: the notice names WHO and WHERE, and it never
    /// says the workspace is locked, blocked or unavailable — because it is none of those, and both
    /// answers follow it. A lock this product treated as authoritative would become a stale file that
    /// locks out a team.
    /// </summary>
    [Fact]
    public void TheNotice_NamesWhoAndWhere_AndClaimsNoAuthority()
    {
        var opened = new DateTime(2026, 9, 3, 9, 0, 0, DateTimeKind.Utc);
        PretendWeAre("lab-99", "engineer-b", 1234, opened);
        WorkspaceLock.Take(_ws);
        PretendWeAre("lab-07", "engineer-a", 4242, opened.AddMinutes(20));

        string notice = WorkspaceLock.NoticeFor(WorkspaceLock.Read(_ws)!, "stdlib");

        Assert.Contains("engineer-b", notice, StringComparison.Ordinal);
        Assert.Contains("lab-99",     notice, StringComparison.Ordinal);
        Assert.Contains("20 minutes ago", notice, StringComparison.Ordinal);
        Assert.Contains("cannot tell",    notice, StringComparison.Ordinal);
        foreach (string forbidden in new[] { "locked", "blocked", "unavailable", "denied" })
            Assert.DoesNotContain(forbidden, notice, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Both answers exist, always — the dialog offers read-only, open-anyway and cancel, and
    /// there is no state in which it offers fewer.</summary>
    [Fact]
    public void TheDialogOffersBothAnswers_Always()
    {
        var choices = Enum.GetValues<Views.Dialogs.WorkspaceInUseDialog.Choice>();
        Assert.Contains(Views.Dialogs.WorkspaceInUseDialog.Choice.ReadOnly,   choices);
        Assert.Contains(Views.Dialogs.WorkspaceInUseDialog.Choice.OpenAnyway, choices);
        Assert.Contains(Views.Dialogs.WorkspaceInUseDialog.Choice.Cancel,     choices);
    }

    /// <summary>
    /// The "open read-only" answer routes through SL2's own concept rather than a second flag, so it
    /// inherits every behaviour already built and tested there — Save disabled with a reason, the
    /// <c>.cws</c> write choke point skipping silently, the provenance band. Asserted at the level
    /// that decides all of them, and for a document INSIDE the workspace as well as its root, since
    /// the per-document question is the one Save actually asks (R-sl2-4).
    /// </summary>
    [Fact]
    public void OpeningReadOnlyByChoice_MakesTheWholeWorkspaceUnwritable_AndIsReversible()
    {
        string inside = Path.Combine(_ws, "top", "schematic");
        Directory.CreateDirectory(inside);
        Assert.True(WorkspaceWritability.IsWritable(_ws));
        Assert.True(WorkspaceWritability.IsWritable(inside));

        WorkspaceWritability.OpenReadOnlyThisSession(_ws);
        Assert.True(WorkspaceWritability.IsReadOnly(_ws));
        Assert.True(WorkspaceWritability.IsReadOnly(inside));
        Assert.True(WorkspaceWritability.IsDocumentReadOnly(Path.Combine(inside, "top.csch")));

        // A sibling directory outside the workspace is untouched — this is a prefix rule, not a
        // process-wide switch.
        Assert.True(WorkspaceWritability.IsWritable(_lib));

        WorkspaceWritability.ClearSessionReadOnly(_ws);
        Assert.True(WorkspaceWritability.IsWritable(_ws));
        Assert.True(WorkspaceWritability.IsWritable(inside));
    }

    /// <summary>
    /// The lock file is circuitRF's own bookkeeping and must never render in the project tree or
    /// travel into an archive. The tree hides only an explicit list — <b>not dotfiles in general</b>
    /// — which is the trap SL2's write probe fell into first.
    /// </summary>
    [Fact]
    public void TheLockFile_IsHiddenFromTheProjectTree()
    {
        WorkspaceLock.Take(_ws);
        Assert.True(File.Exists(Path.Combine(_ws, WorkspaceLock.FileName)));

        var root = WorkspaceScanner.Scan(_ws);
        Assert.DoesNotContain(root.Children, n =>
            string.Equals(n.Name, WorkspaceLock.FileName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// And out of an ARCHIVE, which has a skip list of its own rather than the tree's. The lock
    /// carries a user name and a host name: archiving one would put a colleague's account into a file
    /// sent outside the company, and would greet the recipient with a notice about a session that has
    /// nothing to do with them. Matched by the <c>.crf-</c> prefix, which covers SL2's write probe
    /// too — the same class of file, and a pre-existing leak.
    /// </summary>
    [Fact]
    public void CircuitRFsOwnSessionBookkeeping_IsNeverArchived()
    {
        Assert.True(Archive.WorkspaceArchiveScanner.IsSkipped(WorkspaceLock.FileName));
        Assert.True(Archive.WorkspaceArchiveScanner.IsSkipped(".crf-write-probe-0123456789abcdef"));
        // …and an ordinary document is still archived.
        Assert.False(Archive.WorkspaceArchiveScanner.IsSkipped("Amp/schematic/Amp.csch"));
    }

    /// <summary>
    /// R-sl4-1: release removes only OUR lock. A lock naming another host belongs to a session that
    /// is still running, and deleting it because we closed a window would silently disarm the notice
    /// for the person who is actually in there.
    /// </summary>
    [Fact]
    public void ReleaseNeverRemovesSomebodyElsesLock()
    {
        var now = new DateTime(2026, 9, 3, 9, 0, 0, DateTimeKind.Utc);
        PretendWeAre("lab-99", "engineer-b", 1234, now);
        WorkspaceLock.Take(_ws);

        PretendWeAre("lab-07", "engineer-a", 4242, now.AddMinutes(1));
        WorkspaceLock.Release(_ws);

        Assert.NotNull(WorkspaceLock.Read(_ws));
        Assert.Equal("lab-99", WorkspaceLock.Read(_ws)!.Host);
    }

    /// <summary>
    /// R-sl4-4: no open file handle. <c>CrashReporter</c> and <c>Program</c>'s single-instance check
    /// both hold one deliberately, and that is right LOCALLY — its guarantees do not survive SMB, NFS
    /// or a dropped connection, and a handle-based lock over a share fails in the direction that
    /// produces a confident false statement about another person. Asserted by the file being freely
    /// openable, deletable and re-creatable while the lock is "held".
    /// </summary>
    [Fact]
    public void TheLockHoldsNoOpenFileHandle()
    {
        WorkspaceLock.Take(_ws);
        string path = Path.Combine(_ws, WorkspaceLock.FileName);

        using (var s = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.True(s.Length > 0);

        File.Delete(path);                       // an exclusive handle would refuse this on Windows
        Assert.True(WorkspaceLock.Take(_ws));
    }

    /// <summary>
    /// A lock file we cannot understand is no evidence about another person, and refusing to open a
    /// workspace over one would be exactly the stale-file failure R-sl4-2 forbids.
    /// </summary>
    [Fact]
    public void AMalformedLockFile_IsNoEvidenceAtAll()
    {
        File.WriteAllText(Path.Combine(_ws, WorkspaceLock.FileName), "{not json at all");
        Assert.Null(WorkspaceLock.Read(_ws));
    }
}

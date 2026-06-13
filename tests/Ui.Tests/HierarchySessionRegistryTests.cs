using CircuitRF.Ui.Commands;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Gate tests for <see cref="SchematicSessionRegistry"/> (hier1 — editing-session registry).
/// All tests are framework-free: no Avalonia, no disk I/O.
/// </summary>
public class HierarchySessionRegistryTests
{
    // A trivial no-op command — just enough to push onto the UndoRedo stack so CanUndo fires.
    private sealed class StubCommand : IUiCommand
    {
        public string Description => "stub";
        public void Execute() { }
        public void Undo()    { }
    }

    private static SchematicViewModel MakeVm()
    {
        var model = new SchematicEditModel();
        return new SchematicViewModel(model);
    }

    // ── Reuse: same path → same VM instance ──────────────────────────────────

    [Fact]
    public void Register_ThenTryGet_ReturnsSameInstance()
    {
        var registry = new SchematicSessionRegistry();
        var vm       = MakeVm();
        const string path = "/abs/cell/schematic/top.csch";

        registry.Register(path, vm, _ => { });

        var found = registry.TryGet(path, out var retrieved);

        Assert.True(found);
        Assert.Same(vm, retrieved);
    }

    [Fact]
    public void Register_SamePath_OverwritesWith_NewVm_StillReusedInstance()
    {
        // Registering the same path a second time (e.g. after ExecuteSavePlan materialises a
        // scratch doc) overwrites safely; TryGet returns the latest registration.
        var registry = new SchematicSessionRegistry();
        var vm1 = MakeVm();
        var vm2 = MakeVm();
        const string path = "/abs/cell/schematic/top.csch";

        registry.Register(path, vm1, _ => { });
        registry.Register(path, vm2, _ => { });  // safe overwrite

        registry.TryGet(path, out var retrieved);
        Assert.Same(vm2, retrieved);
    }

    [Fact]
    public void TryGet_UnknownPath_ReturnsFalse()
    {
        var registry = new SchematicSessionRegistry();
        Assert.False(registry.TryGet("/no/such/file.csch", out _));
    }

    // ── Dirty tracking ────────────────────────────────────────────────────────

    [Fact]
    public void AfterEdit_SessionReportsDirty_ViaHasOrphanedDirtySession()
    {
        var registry = new SchematicSessionRegistry();
        var vm       = MakeVm();
        const string path = "/ws/amp/schematic/amp.csch";

        registry.Register(path, vm, _ => { });

        // Simulate an undoable edit.
        vm.UndoRedo.Execute(new StubCommand());

        // The registry knows the session is dirty.
        Assert.True(registry.IsDirty(path));
        // With no open tab (isReferenced → false), it is an orphaned dirty session.
        Assert.True(registry.HasOrphanedDirtySession(_ => false));
    }

    [Fact]
    public void AfterEdit_MarkSaved_SessionIsNoLongerDirty()
    {
        var registry = new SchematicSessionRegistry();
        var vm       = MakeVm();
        const string path = "/ws/amp/schematic/amp.csch";

        registry.Register(path, vm, _ => { });
        vm.UndoRedo.Execute(new StubCommand());

        Assert.True(registry.IsDirty(path));

        registry.MarkSaved(path);

        Assert.False(registry.IsDirty(path));
        Assert.False(registry.HasOrphanedDirtySession(_ => false));
    }

    [Fact]
    public void AfterEdit_ThenUndoToBaseline_SessionIsClean()
    {
        // Undoing back to the saved baseline (here: the empty/opened state) must clear dirty,
        // matching the schematic tab bullet — the project-tree cell indicator depends on this.
        var registry = new SchematicSessionRegistry();
        var vm       = MakeVm();
        const string path = "/ws/amp/schematic/amp.csch";

        string? lastNotified = null;
        registry.Register(path, vm, p => lastNotified = p);

        vm.UndoRedo.Execute(new StubCommand());
        Assert.True(registry.IsDirty(path));

        vm.UndoRedo.Undo();
        Assert.False(registry.IsDirty(path));               // undo to baseline → clean
        Assert.Equal(path, lastNotified);                   // callback fired on the clean transition
        Assert.False(registry.HasOrphanedDirtySession(_ => false));
    }

    [Fact]
    public void AfterSave_ThenEdit_SessionIsDirtyAgain()
    {
        // Editing after a save must re-dirty the session even though the undo stack never emptied.
        var registry = new SchematicSessionRegistry();
        var vm       = MakeVm();
        const string path = "/ws/amp/schematic/amp.csch";

        registry.Register(path, vm, _ => { });
        vm.UndoRedo.Execute(new StubCommand());
        registry.MarkSaved(path);
        Assert.False(registry.IsDirty(path));

        vm.UndoRedo.Execute(new StubCommand());             // edit after save
        Assert.True(registry.IsDirty(path));
    }

    [Fact]
    public void DirtyCallback_InvokedOnFirstEdit()
    {
        var registry = new SchematicSessionRegistry();
        var vm       = MakeVm();
        const string path = "/ws/cb/schematic/cb.csch";

        string? notifiedPath = null;
        registry.Register(path, vm, p => notifiedPath = p);

        vm.UndoRedo.Execute(new StubCommand());

        Assert.Equal(path, notifiedPath);
    }

    [Fact]
    public void DirtySession_ReferencedByTab_IsNotOrphaned()
    {
        var registry = new SchematicSessionRegistry();
        var vm       = MakeVm();
        const string path = "/ws/amp/schematic/amp.csch";

        registry.Register(path, vm, _ => { });
        vm.UndoRedo.Execute(new StubCommand());

        // Session is referenced (open tab exists).
        bool isReferenced(string p) => string.Equals(p, path, StringComparison.OrdinalIgnoreCase);

        Assert.True(registry.IsDirty(path));
        Assert.False(registry.HasOrphanedDirtySession(isReferenced));
    }

    // ── Retire: clean+unreferenced removed; dirty NOT removed ────────────────

    [Fact]
    public void RetireIfUnreferenced_CleanUnreferenced_RemovesSession()
    {
        var registry = new SchematicSessionRegistry();
        var vm       = MakeVm();
        const string path = "/ws/lna/schematic/lna.csch";

        registry.Register(path, vm, _ => { });

        // Session is clean (no edit pushed) and unreferenced (no open tab).
        registry.RetireIfUnreferenced(path, _ => false);

        Assert.False(registry.TryGet(path, out _));
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void RetireIfUnreferenced_DirtySession_IsNeverRetired()
    {
        var registry = new SchematicSessionRegistry();
        var vm       = MakeVm();
        const string path = "/ws/lna/schematic/lna.csch";

        registry.Register(path, vm, _ => { });
        vm.UndoRedo.Execute(new StubCommand());   // make dirty

        registry.RetireIfUnreferenced(path, _ => false);

        // Still in registry — dirty sessions survive retirement.
        Assert.True(registry.TryGet(path, out _));
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void RetireIfUnreferenced_CleanButReferenced_IsNotRetired()
    {
        var registry = new SchematicSessionRegistry();
        var vm       = MakeVm();
        const string path = "/ws/lna/schematic/lna.csch";

        registry.Register(path, vm, _ => { });
        // Clean but still referenced by an open tab.
        registry.RetireIfUnreferenced(path, _ => true);

        Assert.True(registry.TryGet(path, out _));
    }

    // ── Clear resets all state ────────────────────────────────────────────────

    [Fact]
    public void Clear_RemovesAllSessionsAndDirtyFlags()
    {
        var registry = new SchematicSessionRegistry();
        var vm       = MakeVm();
        const string path = "/ws/cell/schematic/cell.csch";

        registry.Register(path, vm, _ => { });
        vm.UndoRedo.Execute(new StubCommand());

        registry.Clear();

        Assert.Equal(0, registry.Count);
        Assert.Equal(0, registry.DirtyCount);
    }
}

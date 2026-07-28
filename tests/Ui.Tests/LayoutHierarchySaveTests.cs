using CircuitRF.Ui.Commands.Layout;
using CircuitRF.Ui.Layout;
using Xunit;

namespace CircuitRF.Ui.Tests;

// ──────────────────────────────────────────────────────────────────────────────
//  Phase L3b — save and dirty-propagation gates (§3), mirroring the schematic's own
//  HierarchySaveTests exactly, retargeted to LayoutDocument/LayoutSessionRegistry.
//  Framework-free: no Avalonia, disk I/O only. WorkspaceViewModel itself cannot be constructed
//  headlessly (per this project's established "Testing without the Avalonia runtime" convention),
//  so these tests exercise a Simulate* helper that mirrors WorkspaceViewModel.SaveMaterializedLayoutDoc's
//  actual logic exactly — the same "simulate the production seam" pattern HierarchySaveTests uses.
// ──────────────────────────────────────────────────────────────────────────────

public sealed class LayoutHierarchySaveTests : IDisposable
{
    private readonly string _tempDir;
    private static readonly LayerKey LayerA = new(1, 0);

    public LayoutHierarchySaveTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "crftest_layhiersave_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    private static LayoutView MakeModel() => new() { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000 };

    private static LayoutEditorViewModel MakeVm() => new(MakeModel());

    /// <summary>Mirrors production wiring exactly: GetOrCreateLayoutSession/OpenOrActivateLayout
    /// always construct a materialized document's base VM WITH its own path, so
    /// <see cref="LayoutEditorViewModel.CurrentLayoutPath"/> is never null for a base session that
    /// backs a materialized <see cref="LayoutDocument"/>.</summary>
    private static LayoutEditorViewModel MakeVm(string path) => new(MakeModel(), path);

    /// <summary>Writes an empty .clay to disk and returns the path.</summary>
    private string MakeClay(string fileName)
    {
        var path = Path.Combine(_tempDir, fileName);
        LayoutPersistence.SaveToFile(path, MakeModel());
        return path;
    }

    /// <summary>
    /// Mirrors WorkspaceViewModel.SaveMaterializedLayoutDoc's actual logic: write the base
    /// unconditionally, then flush every OTHER dirty nav-frame session to its own .clay.
    /// </summary>
    private static void SimulateSaveMaterializedLayoutDoc(LayoutDocument doc, LayoutSessionRegistry registry)
    {
        if (doc.ViewModel.CurrentLayoutPath is { } basePath)
        {
            LayoutPersistence.SaveToFile(basePath, doc.ViewModel.Model);
            doc.ViewModel.MarkSaved();
            registry.MarkSaved(basePath);
        }

        foreach (var (session, _) in doc.NavFrames)
        {
            if (ReferenceEquals(session, doc.ViewModel)) continue;
            if (!session.IsDirty) continue;
            if (!registry.TryGetPath(session, out var subPath) || subPath is null) continue;
            LayoutPersistence.SaveToFile(subPath, session.Model);
            session.MarkSaved();
            registry.MarkSaved(subPath);
        }
    }

    // ── Gate 7: saving while pushed in writes the SUB-CELL's file ─────────────────────────────────

    [Fact]
    public void PushedIn_Edit_SingleSave_PersistsSubCell_AndClearsItsDirtyFlag()
    {
        var parentPath = MakeClay("parent.clay");
        var childPath  = MakeClay("child.clay");

        var registry = new LayoutSessionRegistry();
        var baseVm   = MakeVm(parentPath);
        var childVm  = MakeVm();
        registry.Register(childPath, childVm, _ => { });

        var doc = new LayoutDocument("parent", baseVm, parentPath);
        doc.PushIn(childVm, "X1");
        Assert.Same(childVm, doc.ActiveViewModel);

        // Edit on the active (child) session — a detectable, round-trip-able change.
        childVm.Execute(new AddInstanceCommand(childVm.Model, new LayoutInstance { CellRef = "Leaf", X = 0, Y = 0, Mag = 1.0 }));
        Assert.True(childVm.IsDirty);

        SimulateSaveMaterializedLayoutDoc(doc, registry);

        var reloadedChild = LayoutPersistence.LoadFromFile(childPath);
        Assert.Single(reloadedChild.Instances);
        Assert.False(childVm.IsDirty);
    }

    [Fact]
    public void PushedIn_Edit_SingleSave_LeavesParentFileContentUnchanged()
    {
        var parentPath = MakeClay("parent.clay");
        var childPath  = MakeClay("child.clay");
        var parentBytesBefore = File.ReadAllBytes(parentPath);

        var registry = new LayoutSessionRegistry();
        var baseVm   = MakeVm(parentPath);
        var childVm  = MakeVm();
        registry.Register(childPath, childVm, _ => { });

        var doc = new LayoutDocument("parent", baseVm, parentPath);
        doc.PushIn(childVm, "X1");
        childVm.Execute(new AddInstanceCommand(childVm.Model, new LayoutInstance { CellRef = "Leaf", X = 0, Y = 0, Mag = 1.0 }));

        SimulateSaveMaterializedLayoutDoc(doc, registry);

        // The base is technically rewritten (unconditionally, mirroring the schematic exactly), but
        // since it was never edited its CONTENT — the thing "unmodified on disk" actually means — is
        // byte-identical.
        var parentBytesAfter = File.ReadAllBytes(parentPath);
        Assert.Equal(parentBytesBefore, parentBytesAfter);
        var reloadedParent = LayoutPersistence.LoadFromFile(parentPath);
        Assert.Empty(reloadedParent.Instances);
    }

    [Fact]
    public void BaseEdit_SingleSave_Unchanged()
    {
        var parentPath = MakeClay("parent.clay");
        var registry = new LayoutSessionRegistry();
        var baseVm   = MakeVm(parentPath);
        var doc      = new LayoutDocument("parent", baseVm, parentPath);

        baseVm.Execute(new AddInstanceCommand(baseVm.Model, new LayoutInstance { CellRef = "Leaf", X = 0, Y = 0, Mag = 1.0 }));
        Assert.True(baseVm.IsDirty);

        SimulateSaveMaterializedLayoutDoc(doc, registry);

        var reloaded = LayoutPersistence.LoadFromFile(parentPath);
        Assert.Single(reloaded.Instances);
    }

    [Fact]
    public void CleanPushedInFrame_IsNotRewritten()
    {
        var parentPath = MakeClay("parent.clay");
        var childPath  = MakeClay("child.clay");

        var registry = new LayoutSessionRegistry();
        var baseVm   = MakeVm(parentPath);
        var childVm  = MakeVm();
        registry.Register(childPath, childVm, _ => { });

        var doc = new LayoutDocument("parent", baseVm, parentPath);
        doc.PushIn(childVm, "X1");

        baseVm.Execute(new AddInstanceCommand(baseVm.Model, new LayoutInstance { CellRef = "Leaf", X = 0, Y = 0, Mag = 1.0 }));
        Assert.False(childVm.IsDirty);

        SimulateSaveMaterializedLayoutDoc(doc, registry);

        var reloadedChild = LayoutPersistence.LoadFromFile(childPath);
        Assert.Empty(reloadedChild.Instances);
    }

    // ── Gate 8: dirty propagation — a dirty sub-cell is never silently lost ────────────────────────

    [Fact]
    public void DirtySubCell_PoppedOutWithoutSaving_SurvivesAsAnOrphanedDirtySession()
    {
        var parentPath = MakeClay("parent.clay");
        var childPath  = MakeClay("child.clay");

        var registry = new LayoutSessionRegistry();
        var baseVm   = MakeVm(parentPath);
        var childVm  = MakeVm();
        registry.Register(childPath, childVm, _ => { });

        var doc = new LayoutDocument("parent", baseVm, parentPath);
        doc.PushIn(childVm, "X1");
        childVm.Execute(new AddInstanceCommand(childVm.Model, new LayoutInstance { CellRef = "Leaf", X = 0, Y = 0, Mag = 1.0 }));
        Assert.True(childVm.IsDirty);

        // Pop back out WITHOUT saving — the base's own path is the only thing "referenced," so the
        // popped child session is now orphaned (no open frame references it) but must NOT be
        // silently discarded, since dirty sessions are never retired.
        doc.PopOut();
        bool isReferenced(string path) => string.Equals(path, parentPath, StringComparison.OrdinalIgnoreCase);
        registry.RetireIfUnreferenced(childPath, isReferenced);

        Assert.True(registry.TryGet(childPath, out var stillThere));
        Assert.Same(childVm, stillThere);
        Assert.True(childVm.IsDirty);
        Assert.Contains(childPath, registry.GetOrphanedDirtyPaths(isReferenced));
    }

    [Fact]
    public void CleanPoppedSubCell_IsRetired_NotTrackedAsOrphanedDirty()
    {
        var parentPath = MakeClay("parent.clay");
        var childPath  = MakeClay("child.clay");

        var registry = new LayoutSessionRegistry();
        var baseVm   = MakeVm(parentPath);
        var childVm  = MakeVm();
        registry.Register(childPath, childVm, _ => { });

        var doc = new LayoutDocument("parent", baseVm, parentPath);
        doc.PushIn(childVm, "X1");
        // No edit — child stays clean.
        doc.PopOut();

        bool isReferenced(string path) => string.Equals(path, parentPath, StringComparison.OrdinalIgnoreCase);
        registry.RetireIfUnreferenced(childPath, isReferenced);

        Assert.False(registry.TryGet(childPath, out _));
        Assert.Empty(registry.GetOrphanedDirtyPaths(isReferenced));
    }

    // ── R-L3b-1: GetOrCreateSession-equivalent sharing (one session per path, both open-as-tab and
    //    push-in funnel through it) — the property that makes live edits visible everywhere ────────

    [Fact]
    public void SamePathRegisteredTwice_ReturnsAndKeepsTheSameSession()
    {
        var childPath = MakeClay("child.clay");
        var registry = new LayoutSessionRegistry();
        var childVm  = MakeVm();
        registry.Register(childPath, childVm, _ => { });

        Assert.True(registry.TryGet(childPath, out var same));
        Assert.Same(childVm, same);
    }
}

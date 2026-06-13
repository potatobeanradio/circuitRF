using System;
using System.IO;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Gate tests for hier3: Push In / Pop Out / Open Cell in New Tab (hier3).
/// All tests are framework-free: no Avalonia, no Dock factory, disk I/O only where required.
/// </summary>
public sealed class HierarchyPushInTests : IDisposable
{
    private readonly string _tempDir;

    public HierarchyPushInTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "crftest_hier3_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    private static SchematicViewModel MakeVm() => new(new SchematicEditModel());
    private static SchematicDocument  MakeDoc(string title, SchematicViewModel vm) => new(title, vm);

    private sealed class StubCommand : IUiCommand
    {
        public string Description => "stub";
        public void Execute() { }
        public void Undo()    { }
    }

    // ── Helper: build a cell fixture on disk ───────────────────────────────────

    /// <summary>
    /// Creates a cell folder under <see cref="_tempDir"/> with a schematic sub-folder.
    /// When <paramref name="writeCsch"/> is <c>true</c>, drops a minimal .csch there.
    /// </summary>
    private string MakeCellFixture(string cellName, bool writeCsch)
    {
        var cellDir = Path.Combine(_tempDir, cellName);
        var schDir  = Path.Combine(cellDir, CellFolder.SchematicSubFolder);
        Directory.CreateDirectory(schDir);
        CellPersistence.SaveToFile(Path.Combine(cellDir, CellFolder.CcellFileName), new CcellFile());

        if (writeCsch)
        {
            var cschPath = Path.Combine(schDir, $"{cellName}.csch");
            SchematicPersistence.SaveToFile(cschPath, new SchematicEditModel(), cellName);
        }
        return cellDir;
    }

    // ── CanPushInto — framework-free resolver ────────────────────────────────

    [Fact]
    public void CanPushInto_BuiltInComponent_ReturnsFalse()
    {
        var comp  = new EditableComponent { Symbol = SymbolKind.Resistor }; // CellRef = null
        var model = new SchematicEditModel { SchematicDirectory = _tempDir };

        var ok = HierarchyResolver.CanPushInto(comp, model, out var reason);

        Assert.False(ok);
        Assert.NotNull(reason);
    }

    [Fact]
    public void CanPushInto_ScratchParent_ReturnsFalse()
    {
        var comp  = new EditableComponent { CellRef = "SomeCell" };
        var model = new SchematicEditModel(); // SchematicDirectory = null → scratch

        var ok = HierarchyResolver.CanPushInto(comp, model, out var reason);

        Assert.False(ok);
        Assert.NotNull(reason);
    }

    [Fact]
    public void CanPushInto_ResolvableCell_ReturnsTrue()
    {
        MakeCellFixture("AmpCell", writeCsch: true);
        var comp  = new EditableComponent { CellRef = "AmpCell" };
        var model = new SchematicEditModel { SchematicDirectory = _tempDir };

        var ok = HierarchyResolver.CanPushInto(comp, model, out var reason);

        Assert.True(ok);
        Assert.Null(reason);
    }

    [Fact]
    public void CanPushInto_NoViewCell_ReturnsFalse()
    {
        MakeCellFixture("EmptyCell", writeCsch: false); // schematic/ exists but is empty
        var comp  = new EditableComponent { CellRef = "EmptyCell" };
        var model = new SchematicEditModel { SchematicDirectory = _tempDir };

        var ok = HierarchyResolver.CanPushInto(comp, model, out var reason);

        Assert.False(ok);
        Assert.NotNull(reason);
    }

    // ── PushIn effect on the document ─────────────────────────────────────────

    [Fact]
    public void PushIn_ActiveViewModelBecomesChildSession()
    {
        var registry = new SchematicSessionRegistry();
        const string path = "/ws/cell/schematic/cell.csch";

        var baseVm = MakeVm();
        var cellVm = MakeVm();
        registry.Register(path, cellVm, _ => { });

        var doc = MakeDoc("Top", baseVm);
        registry.TryGet(path, out var session);
        doc.PushIn(session!, "X1");

        Assert.Same(cellVm, doc.ActiveViewModel);
        Assert.Equal(1, doc.NavDepth);
        Assert.True(doc.CanPopOut);
    }

    // ── PopOut + session retirement ───────────────────────────────────────────

    [Fact]
    public void PopOut_ReturnsCorrectSession_TryGetPathFindsIt()
    {
        var registry = new SchematicSessionRegistry();
        const string path = "/ws/cell/schematic/cell.csch";

        var baseVm = MakeVm();
        var cellVm = MakeVm();
        registry.Register(path, cellVm, _ => { });

        var doc = MakeDoc("Top", baseVm);
        doc.PushIn(cellVm, "X1");

        var popped = doc.PopOut();

        Assert.Same(cellVm, popped);
        Assert.True(registry.TryGetPath(cellVm, out var foundPath));
        Assert.Equal(path, foundPath, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void PopOut_CleanUnreferenced_SessionRetired()
    {
        var registry = new SchematicSessionRegistry();
        const string path = "/ws/cell/schematic/cell.csch";

        var baseVm = MakeVm();
        var cellVm = MakeVm();
        registry.Register(path, cellVm, _ => { });

        var doc = MakeDoc("Top", baseVm);
        doc.PushIn(cellVm, "X1");

        var popped = doc.PopOut();
        Assert.True(registry.TryGetPath(popped!, out var poppedPath));

        // Simulate RetireSessionIfUnreferenced: session is clean + no open tab.
        registry.RetireIfUnreferenced(poppedPath!, _ => false);

        Assert.False(registry.TryGet(path, out _));
    }

    [Fact]
    public void PopOut_DirtySession_IsNotRetired()
    {
        var registry = new SchematicSessionRegistry();
        const string path = "/ws/cell/schematic/cell.csch";

        var baseVm = MakeVm();
        var cellVm = MakeVm();
        registry.Register(path, cellVm, _ => { });
        cellVm.UndoRedo.Execute(new StubCommand()); // make dirty

        var doc = MakeDoc("Top", baseVm);
        doc.PushIn(cellVm, "X1");
        var popped = doc.PopOut();
        Assert.True(registry.TryGetPath(popped!, out var poppedPath));

        registry.RetireIfUnreferenced(poppedPath!, _ => false);

        // Dirty session must survive.
        Assert.True(registry.TryGet(path, out _));
    }

    // ── TryGetPath reverse lookup ─────────────────────────────────────────────

    [Fact]
    public void TryGetPath_RegisteredVm_ReturnsPath()
    {
        var registry = new SchematicSessionRegistry();
        var vm       = MakeVm();
        const string path = "/ws/amp/schematic/amp.csch";
        registry.Register(path, vm, _ => { });

        var found = registry.TryGetPath(vm, out var foundPath);

        Assert.True(found);
        Assert.Equal(path, foundPath, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryGetPath_UnknownVm_ReturnsFalse()
    {
        var registry  = new SchematicSessionRegistry();
        var stranger  = MakeVm();

        Assert.False(registry.TryGetPath(stranger, out _));
    }

    // ── Shared session: same path → same VM instance ─────────────────────────

    [Fact]
    public void SamePath_RegistryReturnsSameVmInstance()
    {
        var registry = new SchematicSessionRegistry();
        var vm       = MakeVm();
        const string path = "/ws/shared/schematic/top.csch";
        registry.Register(path, vm, _ => { });

        registry.TryGet(path, out var first);
        registry.TryGet(path, out var second);

        Assert.Same(first, second);
        Assert.Same(vm, first);
    }

    [Fact]
    public void ResolvePrimaryPath_ResolvableCell_ReturnsAbsPath()
    {
        MakeCellFixture("LnaCell", writeCsch: true);
        var comp  = new EditableComponent { CellRef = "LnaCell" };
        var model = new SchematicEditModel { SchematicDirectory = _tempDir };

        var path = HierarchyResolver.ResolvePrimaryPath(comp, model);

        Assert.NotNull(path);
        Assert.EndsWith(".csch", path, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(path));
    }
}

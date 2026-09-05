using CircuitRF.Ui.Messages;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

// ──────────────────────────────────────────────────────────────────────────────
//  Edit-time cycle rejection for SCHEMATIC cell instances.
//
//  The layout view has refused a cycle-closing placement since R-L3a-2; a schematic
//  refused nothing, and the loop surfaced only at extraction as a conflict. These
//  build real cell folders on disk because the walk reads the primary .csch of each
//  level — an in-memory double would agree with itself about a rule the filesystem
//  is the authority on.
// ──────────────────────────────────────────────────────────────────────────────

[Collection(CellStatGlobalsCollection.Name)]
public sealed class SchematicCycleRejectionTests : IDisposable
{
    private readonly string _root;
    private readonly string _wsA;
    private readonly string _wsB;

    public SchematicCycleRejectionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf_schcyc_" + Guid.NewGuid().ToString("N")[..8]);
        _wsA  = Path.Combine(_root, "workspaceA");
        _wsB  = Path.Combine(_root, "workspaceB");
        Directory.CreateDirectory(_wsA);
        Directory.CreateDirectory(_wsB);
        CellSymbolResolver.InvalidateAll();
    }

    public void Dispose()
    {
        CellSymbolResolver.InvalidateAll();
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

    /// <summary>A cell folder with a primary schematic holding one instance per given CellRef.</summary>
    private static string Cell(string workspaceRoot, string name, params string[] cellRefs)
    {
        string cellDir = CellFolder.CreateCellFolder(workspaceRoot, name);
        WriteSchematic(cellDir, cellRefs);
        return cellDir;
    }

    private static void WriteSchematic(string cellDir, params string[] cellRefs)
    {
        string schDir = CellFolder.SubFolderPath(cellDir, ViewType.Schematic);
        Directory.CreateDirectory(schDir);

        var model = new SchematicEditModel();
        int n = 1;
        foreach (var r in cellRefs)
            model.Components.Add(new EditableComponent
            { InstanceName = "X" + n++, Symbol = SymbolKind.Generic, CellRef = r, X = 0, Y = 200 * n });

        SchematicPersistence.SaveToFile(
            Path.Combine(schDir, Path.GetFileName(cellDir) + ".csch"), model);
    }

    private static string SchDir(string cellDir) =>
        CellFolder.SubFolderPath(cellDir, ViewType.Schematic);

    /// <summary>The reference from <paramref name="fromCellDir"/>'s schematic to another cell.</summary>
    private static string RefTo(string fromCellDir, string toCellDir) =>
        Path.GetRelativePath(SchDir(fromCellDir), toCellDir);

    // ── The direct case ───────────────────────────────────────────────────────

    [Fact]
    public void ACellPlacedIntoITSELF_IsRefused()
    {
        WriteCws(_wsA);
        string amp = Cell(_wsA, "Amp");

        Assert.True(SchematicHierarchy.WouldCreateCycle(amp, RefTo(amp, amp), SchDir(amp)));
    }

    [Fact]
    public void AnOrdinaryChildPlacement_IsNotRefused()
    {
        WriteCws(_wsA);
        string amp  = Cell(_wsA, "Amp");
        string bias = Cell(_wsA, "Bias");

        Assert.False(SchematicHierarchy.WouldCreateCycle(amp, RefTo(amp, bias), SchDir(amp)));
    }

    // ── The indirect case, which is the one nobody can see in a single file ───

    [Fact]
    public void ATwoStepLoop_IsRefused_AndTheLOOPIsNamed()
    {
        WriteCws(_wsA);
        string amp  = Cell(_wsA, "Amp");
        string buf  = Cell(_wsA, "Buf");
        string bias = Cell(_wsA, "Bias");

        // Buf already instances Bias, and Bias instances Amp. Adding Buf to Amp closes Amp→Buf→Bias→Amp.
        WriteSchematic(bias, RefTo(bias, amp));
        WriteSchematic(buf,  RefTo(buf, bias));

        Assert.True(SchematicHierarchy.WouldCreateCycle(amp, RefTo(amp, buf), SchDir(amp)));

        var loop = SchematicHierarchy.DescribeCycle(amp, RefTo(amp, buf), SchDir(amp));
        Assert.NotNull(loop);
        Assert.Equal(["Amp", "Buf", "Bias", "Amp"], loop);
    }

    // ── Across two workspaces — MW2's own reason this matters ─────────────────

    [Fact]
    public void ALoopTHROUGHAnotherWorkspace_IsRefused()
    {
        // A/Amp → ws://B/Buf → ws://A/Amp. No single file shows the loop, and the middle of it lives
        // in a project that need not even be open.
        WriteCws(_wsA, c => c.ReferencedWorkspaces =
            [new CwsWorkspaceRef { Alias = "B", Path = Path.GetRelativePath(_wsA, Path.Combine(_wsB, ".cws")) }]);
        WriteCws(_wsB, c => c.ReferencedWorkspaces =
            [new CwsWorkspaceRef { Alias = "A", Path = Path.GetRelativePath(_wsB, Path.Combine(_wsA, ".cws")) }]);

        string amp = Cell(_wsA, "Amp");
        string buf = Cell(_wsB, "Buf");
        WriteSchematic(buf, ExternalCellRef.RefFor("A", "Amp"));
        WorkspaceRootFinder.InvalidateCache();

        string candidate = ExternalCellRef.RefFor("B", "Buf");
        Assert.True(SchematicHierarchy.WouldCreateCycle(amp, candidate, SchDir(amp)));

        // And an unrelated cell in that same workspace is not refused — the guard is a reachability
        // question, not "anything in a workspace that reaches back".
        Cell(_wsB, "Pad");
        WorkspaceRootFinder.InvalidateCache();
        Assert.False(SchematicHierarchy.WouldCreateCycle(
            amp, ExternalCellRef.RefFor("B", "Pad"), SchDir(amp)));
    }

    // ── The things that must NOT hang or throw ────────────────────────────────

    [Fact]
    public void APreExistingCycleInTheSubGraph_DoesNotHangTheCheck()
    {
        // Two cells that already reference each other — a state a hand-edited file or an older build
        // can produce. Asking about a third, unrelated cell must terminate and answer false.
        WriteCws(_wsA);
        string p = Cell(_wsA, "P");
        string q = Cell(_wsA, "Q");
        WriteSchematic(p, RefTo(p, q));
        WriteSchematic(q, RefTo(q, p));

        string top = Cell(_wsA, "Top");
        Assert.False(SchematicHierarchy.WouldCreateCycle(top, RefTo(top, p), SchDir(top)));
    }

    [Fact]
    public void AVirtualReferenceIsNotAPath_AndIsSkipped()
    {
        // A pdk:// part, a wBond design or an unconfigured SPICE model resolves by its own rule and
        // can never name a cell folder that reaches back. Taking one apart as a path is the trap
        // CellSymbolResolver.NeedsNoBaseDirectory exists to prevent.
        WriteCws(_wsA);
        string amp = Cell(_wsA, "Amp");

        Assert.False(SchematicHierarchy.WouldCreateCycle(
            amp, PdkKitRegistry.RefFor("KitOne", "P1"), SchDir(amp)));

        // And one sitting INSIDE the candidate's schematic does not break the walk either.
        string buf = Cell(_wsA, "Buf", PdkKitRegistry.RefFor("KitOne", "P1"));
        Assert.False(SchematicHierarchy.WouldCreateCycle(amp, RefTo(amp, buf), SchDir(amp)));
    }

    [Fact]
    public void AScratchDocumentCannotCycle()
    {
        WriteCws(_wsA);
        string amp = Cell(_wsA, "Amp");

        // Nothing can reference back to a path that does not exist yet.
        Assert.False(SchematicHierarchy.WouldCreateCycle(null, RefTo(amp, amp), SchDir(amp)));
    }

    // ── The gesture itself, which is what the user actually meets ─────────────

    private sealed class CaptureSink : IMessageSink
    {
        public readonly List<(MessageLevel Level, string Text)> Posts = [];
        public void Post(MessageLevel level, string text, string? filePath = null)
            => Posts.Add((level, text));
        public void Clear() => Posts.Clear();
    }

    [Fact]
    public async Task PlacingACycleClosingCell_PlacesNothing_AndSaysWHICHLoop()
    {
        WriteCws(_wsA);
        string amp = Cell(_wsA, "Amp");
        string buf = Cell(_wsA, "Buf");
        WriteSchematic(buf, RefTo(buf, amp));      // Buf already instances Amp

        var (model, _, _) = SchematicPersistence.LoadFromFile(
            Path.Combine(SchDir(amp), "Amp.csch"));
        var sink = new CaptureSink();
        var vm   = new SchematicViewModel(model, messageSink: sink);

        await vm.CommitCellPlacementAsync(buf, 0, 0, SymbolRotation.R0);

        // Nothing placed, one error, and it names the loop rather than only its existence.
        Assert.Empty(model.Components);
        var error = Assert.Single(sink.Posts.Where(p => p.Level == MessageLevel.Error));
        Assert.Contains("cycle", error.Text);
        Assert.Contains("Amp", error.Text);
        Assert.Contains("Buf", error.Text);
    }

    [Fact]
    public async Task PlacingAnOrdinaryCell_StillPlacesIt()
    {
        // The other half of the gate: the guard must not refuse the ordinary case.
        WriteCws(_wsA);
        string amp  = Cell(_wsA, "Amp");
        string bias = Cell(_wsA, "Bias");

        var (model, _, _) = SchematicPersistence.LoadFromFile(
            Path.Combine(SchDir(amp), "Amp.csch"));
        var sink = new CaptureSink();
        var vm   = new SchematicViewModel(model, messageSink: sink);

        await vm.CommitCellPlacementAsync(bias, 0, 0, SymbolRotation.R0);

        Assert.Single(model.Components);
        Assert.DoesNotContain(sink.Posts, p => p.Level == MessageLevel.Error);
    }

    [Fact]
    public void ACellWithNoSchematicView_IsALeaf_NotAnError()
    {
        WriteCws(_wsA);
        string amp  = Cell(_wsA, "Amp");
        string bare = CellFolder.CreateCellFolder(_wsA, "Bare");   // no schematic at all

        Assert.False(SchematicHierarchy.WouldCreateCycle(amp, RefTo(amp, bare), SchDir(amp)));
    }
}

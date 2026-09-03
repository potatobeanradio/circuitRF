using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

// ──────────────────────────────────────────────────────────────────────────────
//  SL3 — reporting a changed cell interface
//  (docs/sonnet-briefs/brief-shared-library-3-interface-change.md §6).
//
//  Every test builds a REAL cell on disk and then edits it, because the whole
//  feature is about a file changing underneath a design that has already been
//  written: an in-memory double would agree with itself about a comparison the
//  filesystem is the authority on.
//
//  The symbol cache is keyed by (cell dir, primary name, mtime) and a test that
//  rewrites a .csym within the same clock tick can otherwise take a stale hit —
//  so every mutation drops the cache explicitly rather than trusting the mtime.
// ──────────────────────────────────────────────────────────────────────────────

public sealed class CellInterfaceChangeTests : IDisposable
{
    private readonly string _root;
    private readonly string _ws;

    public CellInterfaceChangeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf_sl3_" + Guid.NewGuid().ToString("N")[..8]);
        _ws   = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(_ws);
        WorkspacePersistence.SaveToFile(Path.Combine(_ws, ".cws"), new CwsFile());
        CellSymbolResolver.InvalidateAll();
    }

    public void Dispose()
    {
        CellSymbolResolver.InvalidateAll();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // ── Fixture ───────────────────────────────────────────────────────────────

    private static Symbol TwoPinSymbol(
        double bx = 200, double by = 0, string? bName = "b", int portCount = 2,
        double artworkX = 100) => new(
        primitives: [new LinePrimitive(SymbolColorRole.SymbolLine, SymbolStrokeTier.Normal,
                                       -100, 0, artworkX, 0)],
        pins:       [new SymbolPin(-200, 0, 1, "a"), new SymbolPin(bx, by, 2, bName)],
        portCount:  portCount);

    /// <summary>A cell with the given symbol and one declared parameter.</summary>
    private string CreateCell(string name, Symbol? symbol = null, params string[] parameterNames)
    {
        string cellDir = CellFolder.CreateCellFolder(_ws, name);
        WriteSymbol(cellDir, name, symbol ?? TwoPinSymbol());

        string ccellPath = Path.Combine(cellDir, CellFolder.CcellFileName);
        var ccell = CellPersistence.LoadFromFile(ccellPath);
        ccell.NumPorts = 2;
        foreach (string p in parameterNames.Length > 0 ? parameterNames : ["W"])
            ccell.Parameters.Add(new CcellParameter { Name = p, DefaultExpression = "10u" });
        CellPersistence.SaveToFile(ccellPath, ccell);
        return cellDir;
    }

    private static void WriteSymbol(string cellDir, string name, Symbol symbol)
    {
        string dir = CellFolder.SubFolderPath(cellDir, ViewType.Symbol);
        Directory.CreateDirectory(dir);
        SymbolPersistence.SaveToFile(Path.Combine(dir, name + ".csym"), symbol);
        CellSymbolResolver.InvalidateAll();
    }

    private static void RewriteCcell(string cellDir, Action<CcellFile> edit)
    {
        string path = Path.Combine(cellDir, CellFolder.CcellFileName);
        var ccell = CellPersistence.LoadFromFile(path);
        edit(ccell);
        CellPersistence.SaveToFile(path, ccell);
        CellSymbolResolver.InvalidateAll();
    }

    /// <summary>
    /// A "Board" cell whose schematic holds one instance of <paramref name="targetCellDir"/> at the
    /// origin, with a wire on EACH pin so that a pin which moves is the only one that goes
    /// unconnected — which is what makes §6.5's assertion sharp rather than incidental.
    /// </summary>
    private (string SchPath, string SchDir) CreateBoard(string targetCellDir, bool recordHash = true)
    {
        string board  = CellFolder.CreateCellFolder(_ws, "Board");
        string schDir = CellFolder.SubFolderPath(board, ViewType.Schematic);
        Directory.CreateDirectory(schDir);

        string cellRef = Path.GetRelativePath(schDir, targetCellDir);
        var model = new SchematicEditModel { SchematicDirectory = schDir };
        var comp  = new EditableComponent
        {
            InstanceName = "X1",
            Symbol       = SymbolKind.Generic,
            CellRef      = cellRef,
            X = 0, Y = 0,
        };
        if (recordHash) comp.CellInterfaceHash = PlacedCellRef.HashFor(cellRef, schDir);
        model.Components.Add(comp);

        var left = new EditableWire();
        left.Points.Add((-400, 0));
        left.Points.Add((-200, 0));
        var right = new EditableWire();
        right.Points.Add((200, 0));
        right.Points.Add((400, 0));
        model.Wires.Add(left);
        model.Wires.Add(right);

        string schPath = Path.Combine(schDir, "Board.csch");
        SchematicPersistence.SaveToFile(schPath, model);
        return (schPath, schDir);
    }

    private static SchematicEditModel Load(string schPath)
    {
        var (model, _, _) = SchematicPersistence.LoadFromFile(schPath);
        return model;
    }

    // ── §6.1 — hash stability ─────────────────────────────────────────────────

    [Fact]
    public void Hash_IsStable_AcrossTwoResolves()
    {
        string cell = CreateCell("Amp");
        string dir  = CellFolder.SubFolderPath(cell, ViewType.Schematic);
        Directory.CreateDirectory(dir);
        string cellRef = Path.GetRelativePath(dir, cell);

        string? first = CellInterfaceHash.For(cellRef, dir);
        CellSymbolResolver.InvalidateAll();
        string? second = CellInterfaceHash.For(cellRef, dir);

        Assert.NotNull(first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Hash_IsStable_AcrossAProcessRestart()
    {
        // A process restart cannot be run inside a test, so what is actually asserted is the property
        // that makes one safe: the hash is a pure function of the interface's canonical text, with
        // nothing per-process, per-machine or per-locale in it. Pinned against a LITERAL — a hash that
        // silently started depending on a randomized string seed, a dictionary order or a decimal
        // comma would move this value and nothing else in the suite would notice.
        var symbol = new Symbol(
            primitives: [],
            pins:       [new SymbolPin(-200, 0, 1, "a"), new SymbolPin(200, 0, 2, "b")],
            portCount:  2);
        var ccell = new CcellFile { Parameters = { new CcellParameter { Name = "W" } } };

        Assert.Equal("5a375b91d741", CellInterfaceHash.Of(symbol, ccell));
    }

    [Fact]
    public void Hash_IgnoresTheDrawingPrimitives()
    {
        // R-sl3-2's exclusion, asserted rather than assumed: a redrawn glyph that keeps its pins
        // breaks nothing, so reporting it would train the user to dismiss the report.
        string cell = CreateCell("Amp");
        string dir  = CellFolder.SubFolderPath(cell, ViewType.Schematic);
        Directory.CreateDirectory(dir);
        string cellRef = Path.GetRelativePath(dir, cell);

        string? before = CellInterfaceHash.For(cellRef, dir);
        WriteSymbol(cell, "Amp", TwoPinSymbol(artworkX: 140));   // same pins, different artwork
        string? after = CellInterfaceHash.For(cellRef, dir);

        Assert.Equal(before, after);
    }

    // ── §6.2 — each interface change fires ────────────────────────────────────

    [Fact]
    public void APinMoved_Fires()
        => AssertFires(cell => WriteSymbol(cell, "Amp", TwoPinSymbol(bx: 200, by: 100)));

    [Fact]
    public void APinAdded_Fires()
        => AssertFires(cell => WriteSymbol(cell, "Amp", new Symbol(
            primitives: [],
            pins:       [new SymbolPin(-200, 0, 1, "a"), new SymbolPin(200, 0, 2, "b"),
                         new SymbolPin(0, 200, 3, "c")],
            portCount:  3)));

    [Fact]
    public void APinRenamed_Fires()
        => AssertFires(cell => WriteSymbol(cell, "Amp", TwoPinSymbol(bName: "vg")));

    [Fact]
    public void PortCountChanged_Fires()
        => AssertFires(cell => WriteSymbol(cell, "Amp", TwoPinSymbol(portCount: 5)));

    [Fact]
    public void ADeclaredParameterRemoved_Fires()
        => AssertFires(cell => RewriteCcell(cell, c => c.Parameters.Clear()));

    // ── §6.3 — each non-change does NOT fire ──────────────────────────────────

    [Fact]
    public void PrimitivesRedrawn_DoesNotFire()
        => AssertDoesNotFire(cell => WriteSymbol(cell, "Amp", TwoPinSymbol(artworkX: 140)));

    [Fact]
    public void AParameterDEFAULTChanged_DoesNotFire()
        // R-sl3-2: an instance that overrides it is unaffected, and one that does not is MEANT to
        // follow the library. This is the exclusion most likely to be wrong — see src/Ui/RESOLVED.md.
        => AssertDoesNotFire(cell => RewriteCcell(cell, c => c.Parameters[0].DefaultExpression = "99u"));

    [Fact]
    public void TheCellsOwnSchematicEdited_DoesNotFire()
        => AssertDoesNotFire(cell =>
        {
            string schDir = CellFolder.SubFolderPath(cell, ViewType.Schematic);
            Directory.CreateDirectory(schDir);
            var inner = new SchematicEditModel();
            inner.Components.Add(new EditableComponent { InstanceName = "R1", Symbol = SymbolKind.Resistor });
            SchematicPersistence.SaveToFile(Path.Combine(schDir, "Amp.csch"), inner);
            CellSymbolResolver.InvalidateAll();
        });

    [Fact]
    public void TheCellsLayoutViewEdited_DoesNotFire()
        => AssertDoesNotFire(cell =>
        {
            string layDir = CellFolder.SubFolderPath(cell, ViewType.Layout);
            Directory.CreateDirectory(layDir);
            var view = new LayoutView { DbuPerMicron = 1000, SnapDbu = 1000 };
            view.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 5000, Y2 = 2000 });
            LayoutPersistence.SaveToFile(Path.Combine(layDir, "Amp.clay"), view);
            CellSymbolResolver.InvalidateAll();
        });

    // ── §6.4 — an absent recorded hash renders exactly as today ───────────────

    [Fact]
    public void AnAbsentRecordedHash_ReportsNothingAndMarksNothing()
    {
        // R-sl3-5. Every file written before SL3 has no recorded hash, and so does every instance
        // placed by hand-editing a file. A feature whose first act is to mark every existing design as
        // suspect is a feature that gets turned off.
        string cell = CreateCell("Amp");
        var (schPath, _) = CreateBoard(cell, recordHash: false);

        // The cell changes in exactly the way that WOULD fire, had anything been recorded.
        WriteSymbol(cell, "Amp", TwoPinSymbol(bx: 200, by: 100));

        var model   = Load(schPath);
        var changes = CellInterfaceWatch.Scan(model);

        Assert.Empty(changes);
        Assert.All(model.Components, c => Assert.False(c.InterfaceChanged));
        Assert.Equal(0, CellInterfaceWatch.LastScanHashCount);   // and it costs nothing to find out
    }

    [Fact]
    public void AnAbsentRecordedHash_IsNotWrittenBackIntoTheFile()
    {
        string cell = CreateCell("Amp");
        var (schPath, _) = CreateBoard(cell, recordHash: false);
        string before = File.ReadAllText(schPath);

        var model = Load(schPath);
        CellInterfaceWatch.Scan(model);
        SchematicPersistence.SaveToFile(schPath, model);

        Assert.DoesNotContain("CellInterfaceHash", File.ReadAllText(schPath));
        Assert.Equal(before, File.ReadAllText(schPath));
    }

    // ── §6.5 — the report names the newly-unconnected ports ───────────────────

    [Fact]
    public void TheReport_NamesTheNewlyUnconnectedPorts()
    {
        // The electrical consequence is the point of the feature (R-sl3-8), so it is gated — not just
        // the detection. Pin "b" moves off the wire that met it; pin "a" is untouched and stays wired.
        string cell = CreateCell("Amp");
        var (schPath, _) = CreateBoard(cell);

        WriteSymbol(cell, "Amp", TwoPinSymbol(bx: 200, by: 100, bName: "vg"));

        var change = Assert.Single(CellInterfaceWatch.Scan(Load(schPath)));

        Assert.Equal(["X1.vg"], change.UnconnectedPorts);
        Assert.Equal(["X1"], change.InstanceNames);
        Assert.Contains("X1.vg", change.Message);
        Assert.Contains("Amp", change.Message);
    }

    [Fact]
    public void TheReport_IsOnePerCELL_NotOnePerInstance()
    {
        // R-sl3-9. Forty instances of one changed cell is one problem.
        string cell = CreateCell("Amp");
        var (schPath, schDir) = CreateBoard(cell);

        var model = Load(schPath);
        string cellRef = model.Components[0].CellRef!;
        for (int i = 2; i <= 5; i++)
            model.Components.Add(new EditableComponent
            {
                InstanceName = $"X{i}", Symbol = SymbolKind.Generic, CellRef = cellRef,
                X = i * 1000, Y = 0,
                CellInterfaceHash = PlacedCellRef.HashFor(cellRef, schDir),
            });
        SchematicPersistence.SaveToFile(schPath, model);

        WriteSymbol(cell, "Amp", TwoPinSymbol(bName: "vg"));

        var change = Assert.Single(CellInterfaceWatch.Scan(Load(schPath)));
        Assert.Equal(5, change.InstanceNames.Count);
        Assert.Equal(5, CellInterfaceWatch.LastScanHashCount);   // one hash per instance, not per cell
    }

    // ── §6.6 — Accept rewrites the hash; open and save do not ─────────────────

    [Fact]
    public void Accept_RewritesTheHashAndClearsTheState()
    {
        string cell = CreateCell("Amp");
        var (schPath, schDir) = CreateBoard(cell);
        string recorded = Load(schPath).Components[0].CellInterfaceHash!;

        WriteSymbol(cell, "Amp", TwoPinSymbol(bName: "vg"));

        var model = Load(schPath);
        Assert.Single(CellInterfaceWatch.Scan(model));
        Assert.True(model.Components[0].InterfaceChanged);

        int accepted = CellInterfaceWatch.Accept(model.Components, schDir);

        Assert.Equal(1, accepted);
        Assert.False(model.Components[0].InterfaceChanged);
        Assert.NotEqual(recorded, model.Components[0].CellInterfaceHash);
        Assert.Equal(CellInterfaceHash.For(model.Components[0].CellRef, schDir),
                     model.Components[0].CellInterfaceHash);
        Assert.Empty(CellInterfaceWatch.Scan(model));
    }

    [Fact]
    public void OpenAndSave_DoNotRewriteTheRecordedHash()
    {
        // Its own test, deliberately (R-sl3-10): this is the half that would be lost to a convenience
        // change later. The recorded hash is the ONLY evidence the design was authored against a
        // different interface, and a product that erases it on open has implemented nothing.
        string cell = CreateCell("Amp");
        var (schPath, _) = CreateBoard(cell);
        string recorded = Load(schPath).Components[0].CellInterfaceHash!;

        WriteSymbol(cell, "Amp", TwoPinSymbol(bName: "vg"));

        var model = Load(schPath);                        // open
        Assert.Equal(recorded, model.Components[0].CellInterfaceHash);

        CellInterfaceWatch.Scan(model);                   // and the scan the open performs
        Assert.Equal(recorded, model.Components[0].CellInterfaceHash);

        SchematicPersistence.SaveToFile(schPath, model);  // save
        Assert.Equal(recorded, Load(schPath).Components[0].CellInterfaceHash);

        // Still reported after the round trip — the evidence survived.
        Assert.Single(CellInterfaceWatch.Scan(Load(schPath)));
    }

    // ── §6.7 — a local (non-ws://) cell reports identically ───────────────────

    [Fact]
    public void ALocalCellReference_ReportsIdentically_ToAnExternalOne()
    {
        // R-sl3-11. The same failure exists for a cell in your own workspace, with a smaller blast
        // radius; conditioning the check on the reference form would make it fire only sometimes,
        // which is a rule nobody learns. Both halves are built here so "identically" is COMPARED
        // rather than asserted about one of them — the two reports must differ in exactly one field,
        // the source alias, which is a fact about where the cell lives and not about what changed.
        string local = CreateCell("Amp");
        var (localSch, _) = CreateBoard(local);
        WriteSymbol(local, "Amp", TwoPinSymbol(bx: 200, by: 100, bName: "vg"));
        var localChange = Assert.Single(CellInterfaceWatch.Scan(Load(localSch)));

        var externalChange = BuildExternalFixtureAndScan();

        Assert.False(ExternalCellRef.IsExternalRef(Load(localSch).Components[0].CellRef));
        Assert.Null(localChange.SourceAlias);
        Assert.Equal("A", externalChange.SourceAlias);

        Assert.Equal(localChange.CellName,         externalChange.CellName);
        Assert.Equal(localChange.InstanceNames,    externalChange.InstanceNames);
        Assert.Equal(localChange.UnconnectedPorts, externalChange.UnconnectedPorts);
        Assert.Equal(["X1.vg"], localChange.UnconnectedPorts);
        Assert.Equal(localChange.PinCount,       externalChange.PinCount);
        Assert.Equal(localChange.ParameterCount, externalChange.ParameterCount);
    }

    /// <summary>A SECOND workspace holding the same cell, referenced through an alias — the
    /// <c>ws://</c> half of R-sl3-11's comparison.</summary>
    private CellInterfaceChange BuildExternalFixtureAndScan()
    {
        string libWs = Path.Combine(_root, "library");
        Directory.CreateDirectory(libWs);
        WorkspacePersistence.SaveToFile(Path.Combine(libWs, ".cws"), new CwsFile());

        string libCell = CellFolder.CreateCellFolder(libWs, "Amp");
        WriteSymbol(libCell, "Amp", TwoPinSymbol());
        string libCcell = Path.Combine(libCell, CellFolder.CcellFileName);
        var lc = CellPersistence.LoadFromFile(libCcell);
        lc.NumPorts = 2;
        lc.Parameters.Add(new CcellParameter { Name = "W", DefaultExpression = "10u" });
        CellPersistence.SaveToFile(libCcell, lc);

        WorkspacePersistence.SaveToFile(Path.Combine(_ws, ".cws"), new CwsFile
        {
            ReferencedWorkspaces =
                [new CwsWorkspaceRef { Alias = "A", Path = Path.GetRelativePath(_ws, Path.Combine(libWs, ".cws")) }],
        });
        WorkspaceRootFinder.InvalidateCache();

        string board  = CellFolder.CreateCellFolder(_ws, "ExtBoard");
        string schDir = CellFolder.SubFolderPath(board, ViewType.Schematic);
        Directory.CreateDirectory(schDir);

        string cellRef = ExternalCellRef.RefFor("A", "Amp");
        var model = new SchematicEditModel { SchematicDirectory = schDir };
        model.Components.Add(new EditableComponent
        {
            InstanceName = "X1", Symbol = SymbolKind.Generic, CellRef = cellRef, X = 0, Y = 0,
            CellInterfaceHash = PlacedCellRef.HashFor(cellRef, schDir),
        });
        var left = new EditableWire();  left.Points.Add((-400, 0)); left.Points.Add((-200, 0));
        var right = new EditableWire(); right.Points.Add((200, 0));  right.Points.Add((400, 0));
        model.Wires.Add(left);
        model.Wires.Add(right);

        string schPath = Path.Combine(schDir, "ExtBoard.csch");
        SchematicPersistence.SaveToFile(schPath, model);

        WriteSymbol(libCell, "Amp", TwoPinSymbol(bx: 200, by: 100, bName: "vg"));
        return Assert.Single(CellInterfaceWatch.Scan(Load(schPath)));
    }

    // ── The recorded field survives the round trip, and placement records it ──

    [Fact]
    public void PlacedCellRef_RecordsTheHash_AndThePersistenceRoundTripKeepsIt()
    {
        string cell = CreateCell("Amp");
        var (schPath, schDir) = CreateBoard(cell);

        string cellRef = Path.GetRelativePath(schDir, cell);
        var (producedRef, producedHash) = PlacedCellRef.For(schDir, cell);

        Assert.Equal(cellRef, producedRef);
        Assert.NotNull(producedHash);
        Assert.Equal(producedHash, Load(schPath).Components[0].CellInterfaceHash);
        Assert.Contains("CellInterfaceHash", File.ReadAllText(schPath));
    }

    [Fact]
    public void AnUnresolvableCell_RecordsNothingAndReportsNothing()
    {
        // A cell that cannot be read is §4.2's own NotFound state, already reported with its own
        // remedy — it must not also become an interface report saying something different about the
        // same fact, and there is nothing to hash anyway.
        string cell = CreateCell("Amp");
        var (schPath, schDir) = CreateBoard(cell);
        Directory.Delete(cell, recursive: true);
        CellSymbolResolver.InvalidateAll();

        Assert.Null(PlacedCellRef.HashFor(Path.GetRelativePath(schDir, cell), schDir));
        Assert.Empty(CellInterfaceWatch.Scan(Load(schPath)));
    }

    // ── The layout half ───────────────────────────────────────────────────────

    [Fact]
    public void ALayoutInstance_RecordsCompares_AndAccepts()
    {
        string cell = CreateCell("Amp");
        string board = CellFolder.CreateCellFolder(_ws, "BoardLayout");
        string layDir = CellFolder.SubFolderPath(board, ViewType.Layout);
        Directory.CreateDirectory(layDir);

        string cellRef = Path.GetRelativePath(layDir, cell);
        var view = new LayoutView { DbuPerMicron = 1000, SnapDbu = 1000 };
        view.Instances.Add(new LayoutInstance
        {
            CellRef = cellRef, X = 0, Y = 0, Mag = 1.0,
            CellInterfaceHash = PlacedCellRef.HashFor(cellRef, layDir),
        });
        string clayPath = Path.Combine(layDir, "BoardLayout.clay");
        LayoutPersistence.SaveToFile(clayPath, view);
        Assert.Contains("CellInterfaceHash", File.ReadAllText(clayPath));

        WriteSymbol(cell, "Amp", TwoPinSymbol(bName: "vg"));

        var loaded = LayoutPersistence.LoadFromFile(clayPath);
        var change = Assert.Single(CellInterfaceWatch.Scan(loaded, layDir));
        Assert.Equal("Amp", change.CellName);
        Assert.Equal(["instance #1"], change.InstanceNames);

        Assert.Equal(1, CellInterfaceWatch.Accept(loaded.Instances, layDir));
        Assert.Empty(CellInterfaceWatch.Scan(loaded, layDir));
    }

    [Fact]
    public void CloningASchematicComponent_CarriesTheRecordedHash()
    {
        // Copy/paste, Duplicate and every command that rebuilds a component go through Clone.
        var comp = new EditableComponent
        {
            InstanceName = "X1", Symbol = SymbolKind.Generic,
            CellRef = "../Amp", CellInterfaceHash = "abc123def456",
        };
        Assert.Equal("abc123def456", comp.Clone().CellInterfaceHash);
    }

    [Fact]
    public void CloningALayoutInstance_CarriesTheRecordedHash()
    {
        // Every properties-panel edit, move drag and paste goes through LayoutGeometry.Clone —
        // dropping the hash there would erase the evidence on an ordinary edit (R-sl3-10).
        var inst  = new LayoutInstance { CellRef = "../Amp", CellInterfaceHash = "abc123def456" };
        var clone = LayoutGeometry.Clone(inst);
        Assert.Equal("abc123def456", clone.CellInterfaceHash);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void AssertFires(Action<string> mutate)
    {
        string cell = CreateCell("Amp");
        var (schPath, _) = CreateBoard(cell);
        mutate(cell);

        var model   = Load(schPath);
        var changes = CellInterfaceWatch.Scan(model);

        Assert.Single(changes);
        Assert.True(model.Components[0].InterfaceChanged);
        Assert.Equal(1, CellInterfaceWatch.LastScanHashCount);
    }

    private void AssertDoesNotFire(Action<string> mutate)
    {
        string cell = CreateCell("Amp");
        var (schPath, _) = CreateBoard(cell);
        mutate(cell);

        var model   = Load(schPath);
        var changes = CellInterfaceWatch.Scan(model);

        Assert.Empty(changes);
        Assert.All(model.Components, c => Assert.False(c.InterfaceChanged));
    }
}

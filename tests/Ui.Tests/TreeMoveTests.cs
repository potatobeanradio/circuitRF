using System.Text.Json;
using System.Text.Json.Nodes;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

// ──────────────────────────────────────────────────────────────────────────────
//  TM1 — moving cells and files inside the Project Tree
//  (docs/sonnet-briefs/brief-tree-move-1-moving-within-a-workspace.md §8).
//
//  Headless, over real temp-directory workspaces — the same shape RenameCellAsync's own
//  tests use, and for the same reason: the feature IS a rule about the filesystem, and an
//  in-memory double would agree with itself about the thing being tested.
//
//  Gate 2 (OUTBOUND) is the one that matters most. It fails against any implementation that
//  only extends the Rename rewriter, because that one repoints references INTO the renamed
//  cell and has no concept of the ones stored inside it.
// ──────────────────────────────────────────────────────────────────────────────

[Collection(CellStatGlobalsCollection.Name)]
public sealed class TreeMoveTests : IDisposable
{
    private readonly string _root;
    private readonly string _ws;

    public TreeMoveTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf_tm1_" + Guid.NewGuid().ToString("N")[..8]);
        _ws   = Path.Combine(_root, "workspaceA");
        Directory.CreateDirectory(_ws);
        WriteCws(_ws);
        CellSymbolResolver.InvalidateAll();
    }

    public void Dispose()
    {
        CellSymbolResolver.InvalidateAll();
        WorkspaceRootFinder.InvalidateCache();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // ── Fixture ───────────────────────────────────────────────────────────────

    private static void WriteCws(string root, Action<CwsFile>? edit = null)
    {
        var cws = new CwsFile();
        edit?.Invoke(cws);
        WorkspacePersistence.SaveToFile(Path.Combine(root, ".cws"), cws);
        WorkspaceRootFinder.InvalidateCache();
        ExternalCellRef.InvalidateCache();
    }

    private string Cell(string relativePath)
    {
        string parent = Path.Combine(_ws, Path.GetDirectoryName(relativePath) ?? "");
        Directory.CreateDirectory(parent);
        return CellFolder.CreateCellFolder(parent, Path.GetFileName(relativePath));
    }

    /// <summary>Gives <paramref name="cellDir"/> a schematic placing <paramref name="targetCellDir"/>,
    /// with the reference written by the SAME producer the editor uses.</summary>
    private static string SchematicPlacing(string cellDir, params string[] targetCellDirs)
    {
        string schDir = CellFolder.SubFolderPath(cellDir, ViewType.Schematic);
        Directory.CreateDirectory(schDir);
        string path = Path.Combine(schDir, Path.GetFileName(cellDir) + ".csch");

        var model = new SchematicEditModel();
        int n = 1;
        foreach (var target in targetCellDirs)
            model.Components.Add(new EditableComponent
            {
                InstanceName = "X" + n++,
                Symbol       = SymbolKind.Generic,
                CellRef      = ExternalCellRef.MakeCellRef(schDir, target),
            });
        SchematicPersistence.SaveToFile(path, model);
        return path;
    }

    private static string LayoutPlacing(string cellDir, string targetCellDir)
    {
        string layDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        Directory.CreateDirectory(layDir);
        string path = Path.Combine(layDir, Path.GetFileName(cellDir) + ".clay");

        var view = new LayoutView { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000 };
        view.Instances.Add(new LayoutInstance
        {
            CellRef = ExternalCellRef.MakeCellRef(layDir, targetCellDir), X = 0, Y = 0, Mag = 1.0,
        });
        LayoutPersistence.SaveToFile(path, view);
        return path;
    }

    /// <summary>Runs the move exactly as the drop handler does: capture, move, apply.</summary>
    private MoveRewriteResult Move(string sourceAbs, string destFolderAbs, params string[] extraRoots)
    {
        var intent = TreeMove.For(sourceAbs, TreeMove.ClassifyForMove(sourceAbs), destFolderAbs, _ws);
        Assert.True(intent.Permitted, intent.Message);

        var roots = new List<string> { _ws };
        roots.AddRange(extraRoots);

        var capture = WorkspaceMove.Capture(roots);

        if (Directory.Exists(sourceAbs)) Directory.Move(sourceAbs, intent.DestPath);
        else                             File.Move(sourceAbs, intent.DestPath);

        WorkspaceRootFinder.InvalidateCache();
        ExternalCellRef.InvalidateCache();
        return WorkspaceMove.Apply(capture, sourceAbs, intent.DestPath);
    }

    private static string CellRefIn(string cschPath, int index = 0)
    {
        var node = JsonNode.Parse(File.ReadAllText(cschPath));
        return node!["Components"]!.AsArray()[index]!["CellRef"]!.GetValue<string>();
    }

    private static string InstanceRefIn(string clayPath, int index = 0)
    {
        var node = JsonNode.Parse(File.ReadAllText(clayPath));
        return node!["Instances"]!.AsArray()[index]!["CellRef"]!.GetValue<string>();
    }

    private static string Resolved(string storedRef, string documentPath) =>
        WorkspaceMove.Normalize(
            ExternalCellRef.ResolveCellDir(storedRef, Path.GetDirectoryName(documentPath))!);

    private static string Norm(string p) => WorkspaceMove.Normalize(p);

    // ── 1. Inbound ────────────────────────────────────────────────────────────

    [Fact]
    public void Gate1_Inbound_AReferencesB_MovingBRepointsA()
    {
        var a = Cell("A");
        var b = Cell("B");
        string aSch = SchematicPlacing(a, b);
        Directory.CreateDirectory(Path.Combine(_ws, "sub"));

        var result = Move(b, Path.Combine(_ws, "sub"));

        Assert.Contains(aSch, result.RewrittenFiles);
        Assert.Equal(Norm(Path.Combine(_ws, "sub", "B")), Resolved(CellRefIn(aSch), aSch));
        Assert.True(Directory.Exists(Resolved(CellRefIn(aSch), aSch)));
    }

    [Fact]
    public void Gate1_Inbound_TheReferrerCanBeALayout()
    {
        var a = Cell("A");
        var b = Cell("B");
        string aLay = LayoutPlacing(a, b);
        Directory.CreateDirectory(Path.Combine(_ws, "sub"));

        var result = Move(b, Path.Combine(_ws, "sub"));

        Assert.Contains(aLay, result.RewrittenFiles);
        Assert.Equal(Norm(Path.Combine(_ws, "sub", "B")), Resolved(InstanceRefIn(aLay), aLay));
    }

    // ── 2. Outbound — the gate that catches R-tm1-1 ───────────────────────────

    [Fact]
    public void Gate2_Outbound_MovedCellsOwnReferenceStillResolves()
    {
        var b = Cell("B");
        var c = Cell("C");
        string bSch = SchematicPlacing(b, c);       // B → C, and only B moves
        Directory.CreateDirectory(Path.Combine(_ws, "sub"));

        Move(b, Path.Combine(_ws, "sub"));

        string movedSch = Path.Combine(_ws, "sub", "B", "schematic", "B.csch");
        Assert.True(File.Exists(movedSch));
        Assert.False(File.Exists(bSch));

        // C did NOT move. B's own reference to it must still land on it — which requires the depth
        // change to have been written back, and is exactly what a Rename-derived rewriter misses.
        Assert.Equal(Norm(c), Resolved(CellRefIn(movedSch), movedSch));
        Assert.True(Directory.Exists(Resolved(CellRefIn(movedSch), movedSch)));
    }

    // ── 3. Both together ──────────────────────────────────────────────────────

    [Fact]
    public void Gate3_MovingAFolderRewritesOnlyWhatCrossesItsBoundary()
    {
        Directory.CreateDirectory(Path.Combine(_ws, "grp"));
        var b = Cell("grp/B");
        var c = Cell("grp/C");
        var a = Cell("A");

        string bSch = SchematicPlacing(b, c);   // inside the moved folder, both ends move
        string aSch = SchematicPlacing(a, b);   // outside, only the target moves

        string bBefore = File.ReadAllText(bSch);
        Directory.CreateDirectory(Path.Combine(_ws, "dest"));

        var result = Move(Path.Combine(_ws, "grp"), Path.Combine(_ws, "dest"));

        string bAfter = Path.Combine(_ws, "dest", "grp", "B", "schematic", "B.csch");
        Assert.Equal(bBefore, File.ReadAllText(bAfter));       // moved together — byte identical
        Assert.DoesNotContain(bAfter, result.RewrittenFiles);

        Assert.Contains(aSch, result.RewrittenFiles);
        Assert.Equal(Norm(Path.Combine(_ws, "dest", "grp", "B")), Resolved(CellRefIn(aSch), aSch));
    }

    // ── 4. The near-miss ──────────────────────────────────────────────────────

    [Fact]
    public void Gate4_SameNamedCellInAnotherFolderIsUntouchedByteForByte()
    {
        var partsR = Cell("parts/R0402");
        var boardR = Cell("board/R0402");
        var usesParts = Cell("UsesParts");
        var usesBoard = Cell("UsesBoard");

        string partsRef = SchematicPlacing(usesParts, partsR);
        string boardRef = SchematicPlacing(usesBoard, boardR);
        string boardBefore = File.ReadAllText(boardRef);

        Directory.CreateDirectory(Path.Combine(_ws, "sub"));
        var result = Move(partsR, Path.Combine(_ws, "sub"));

        Assert.Contains(partsRef, result.RewrittenFiles);
        Assert.DoesNotContain(boardRef, result.RewrittenFiles);
        Assert.Equal(boardBefore, File.ReadAllText(boardRef));
        Assert.Equal(Norm(boardR), Resolved(CellRefIn(boardRef), boardRef));
    }

    [Fact]
    public void Gate4_APrefixSharingSiblingIsUntouchedByteForByte()
    {
        var amp  = Cell("cells/Amp");
        var ampX = Cell("cells/AmpX");
        var usesAmp  = Cell("UsesAmp");
        var usesAmpX = Cell("UsesAmpX");

        string ampRef  = SchematicPlacing(usesAmp,  amp);
        string ampXRef = SchematicPlacing(usesAmpX, ampX);
        string ampXBefore = File.ReadAllText(ampXRef);

        Directory.CreateDirectory(Path.Combine(_ws, "sub"));
        var result = Move(amp, Path.Combine(_ws, "sub"));

        Assert.Contains(ampRef, result.RewrittenFiles);
        Assert.DoesNotContain(ampXRef, result.RewrittenFiles);
        Assert.Equal(ampXBefore, File.ReadAllText(ampXRef));
        Assert.Equal(Norm(ampX), Resolved(CellRefIn(ampXRef), ampXRef));
    }

    // ── 5. ws:// from a second open workspace, and the redirect record ────────

    [Fact]
    public void Gate5_ExternalReferenceFromAnotherOpenWorkspaceIsRepointedInTheSamePass()
    {
        var b = Cell("B");

        string wsB = Path.Combine(_root, "workspaceB");
        Directory.CreateDirectory(wsB);
        WriteCws(wsB, cws => cws.ReferencedWorkspaces =
        [
            new CwsWorkspaceRef { Alias = "A", Path = Path.Combine(_ws, ".cws") },
        ]);

        string other = CellFolder.CreateCellFolder(wsB, "Uses");
        string otherSch = SchematicPlacing(other, b);
        Assert.StartsWith(ExternalCellRef.Scheme, CellRefIn(otherSch));   // the ws:// form, not a path

        Directory.CreateDirectory(Path.Combine(_ws, "sub"));
        var result = Move(b, Path.Combine(_ws, "sub"), wsB);

        Assert.Contains(otherSch, result.RewrittenFiles);
        Assert.StartsWith(ExternalCellRef.Scheme, CellRefIn(otherSch));
        Assert.Equal(Norm(Path.Combine(_ws, "sub", "B")), Resolved(CellRefIn(otherSch), otherSch));
    }

    [Fact]
    public void Gate5_ARedirectRecordIsWrittenForTheSameMove()
    {
        var b = Cell("B");
        Directory.CreateDirectory(Path.Combine(_ws, "sub"));
        Move(b, Path.Combine(_ws, "sub"));

        Assert.True(MoveRedirects.Append(
            _ws,
            MoveRedirects.ToRootRelative(_ws, b)!,
            MoveRedirects.ToRootRelative(_ws, Path.Combine(_ws, "sub", "B"))!,
            out _));

        var records = MoveRedirects.Read(_ws);
        var record  = Assert.Single(records);
        Assert.Equal("B", record.From);
        Assert.Equal("sub/B", record.To);
        Assert.False(string.IsNullOrWhiteSpace(record.When));
    }

    [Fact]
    public void Gate5_RedirectRecordsAppendAndSurviveARoundTrip()
    {
        Assert.True(MoveRedirects.Append(_ws, "Amp", "rf/Amp", out _));
        Assert.True(MoveRedirects.Append(_ws, "rf/Amp", "rf/pa/Amp", out _));

        var records = MoveRedirects.Read(_ws);
        Assert.Equal(2, records.Count);
        Assert.Equal("Amp",       records[0].From);
        Assert.Equal("rf/pa/Amp", records[1].To);

        // The file itself carries the format version TM2 reads.
        var node = JsonNode.Parse(File.ReadAllText(MoveRedirects.PathFor(_ws)));
        Assert.Equal(MoveRedirects.FormatVersion, node!["FormatVersion"]!.GetValue<int>());
    }

    // ── 6. One test per registered field ──────────────────────────────────────

    [Fact]
    public void Gate6_Registry_EverySiteIsUniquelyNamed()
    {
        var ids = MoveRefRegistry.Sites.Select(s => s.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.NotEmpty(ids);
    }

    [Fact]
    public void Gate6_CschBitmapPathFollowsTheSchematicThatMoves()
    {
        var a = Cell("A");
        string art = Path.Combine(_ws, "art.png");
        File.WriteAllText(art, "not really a png");

        string schDir = CellFolder.SubFolderPath(a, ViewType.Schematic);
        string sch    = Path.Combine(schDir, "A.csch");
        var model = new SchematicEditModel();
        model.CanvasObjects.Add(new EditableBitmap
        {
            ImagePath = Path.GetRelativePath(schDir, art).Replace('\\', '/'),
            X = 0, Y = 0, Width = 10, Height = 10,
        });
        SchematicPersistence.SaveToFile(sch, model);

        Directory.CreateDirectory(Path.Combine(_ws, "sub"));
        Move(a, Path.Combine(_ws, "sub"));

        string moved = Path.Combine(_ws, "sub", "A", "schematic", "A.csch");
        var node = JsonNode.Parse(File.ReadAllText(moved));
        string stored = node!["CanvasObjects"]!.AsArray()[0]!["ImagePath"]!.GetValue<string>();
        Assert.Equal(Norm(art), Norm(Path.Combine(Path.GetDirectoryName(moved)!, stored)));
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(moved)!, stored)));
    }

    [Fact]
    public void Gate6_ClayTechRefFollowsTheLayoutThatMoves()
    {
        var a = Cell("A");
        string tech = Path.Combine(_ws, "board.ctech");
        TechPersistence.SaveToFile(tech, StarterTechnologies.Pcb2Layer());

        string layDir = CellFolder.SubFolderPath(a, ViewType.Layout);
        string clay   = Path.Combine(layDir, "A.clay");
        LayoutPersistence.SaveToFile(clay, new LayoutView
        {
            DbuPerMicron = 1000, SnapDbu = 1000,
            TechRef = Path.GetRelativePath(layDir, tech).Replace('\\', '/'),
        });

        Directory.CreateDirectory(Path.Combine(_ws, "sub"));
        Move(a, Path.Combine(_ws, "sub"));

        string moved = Path.Combine(_ws, "sub", "A", "layout", "A.clay");
        var view = LayoutPersistence.LoadFromFile(moved);
        Assert.Equal(Norm(tech),
            Norm(Path.Combine(Path.GetDirectoryName(moved)!, view.TechRef!)));
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(moved)!, view.TechRef!)));
    }

    [Fact]
    public void Gate6_CemLayoutRefFollowsTheLayoutThatMoves()
    {
        var a = Cell("A");
        string layDir = CellFolder.SubFolderPath(a, ViewType.Layout);
        string clay   = Path.Combine(layDir, "A.clay");
        LayoutPersistence.SaveToFile(clay, new LayoutView { DbuPerMicron = 1000, SnapDbu = 1000 });

        string cem = Path.Combine(_ws, "run.cem");
        var setup = new EmSetup { LayoutRef = EmSetupResolver.MakeLayoutRef(cem, clay, Path.Combine(_ws, ".cws")) };
        EmSetupPersistence.SaveToFile(cem, setup);

        Directory.CreateDirectory(Path.Combine(_ws, "sub"));
        Move(a, Path.Combine(_ws, "sub"));

        var reloaded = EmSetupPersistence.LoadFromFile(cem);
        string? resolved = EmSetupResolver.ResolveLayoutPath(cem, reloaded.LayoutRef, Path.Combine(_ws, ".cws"));
        Assert.Equal(Norm(Path.Combine(_ws, "sub", "A", "layout", "A.clay")), Norm(resolved!));
        Assert.True(File.Exists(resolved));
    }

    [Fact]
    public void Gate6_WBondLinkFollowsTheSchematicThatMoves()
    {
        var a = Cell("A");
        string layDir = CellFolder.SubFolderPath(a, ViewType.Layout);
        string wb     = Path.Combine(layDir, "A" + CircuitRF.Ui.WBond.WBondCell.FileExtension);
        File.WriteAllText(wb, "{}");

        string schDir = CellFolder.SubFolderPath(a, ViewType.Schematic);
        string sch    = Path.Combine(schDir, "A.csch");
        var model = new SchematicEditModel();
        var comp  = new EditableComponent { InstanceName = "WB1", Symbol = SymbolKind.WBond };
        WBondPlacement.LinkTo(comp, wb, schDir);
        model.Components.Add(comp);
        SchematicPersistence.SaveToFile(sch, model);

        Directory.CreateDirectory(Path.Combine(_ws, "sub"));
        Move(a, Path.Combine(_ws, "sub"));

        string movedSch = Path.Combine(_ws, "sub", "A", "schematic", "A.csch");
        string movedWb  = Path.Combine(_ws, "sub", "A", "layout", "A" + CircuitRF.Ui.WBond.WBondCell.FileExtension);

        var reloaded = SchematicPersistence.LoadFromFile(movedSch).model;
        Assert.Equal(Norm(movedWb),
            Norm(WBondPlacement.ResolveLinkedPath(
                reloaded.Components[0], Path.GetDirectoryName(movedSch))!));
        Assert.True(File.Exists(movedWb));
    }

    [Fact]
    public void Gate6_CwsKnownFileFollowsTheFileThatMoves()
    {
        string known = Path.Combine(_ws, "data.s2p");
        File.WriteAllText(known, "! touchstone");
        WriteCws(_ws, cws => cws.KnownFiles = [WorkspaceRefs.ToStoredRef(known, _ws)]);

        Directory.CreateDirectory(Path.Combine(_ws, "sub"));
        Move(known, Path.Combine(_ws, "sub"));

        var cws = WorkspacePersistence.LoadFromFile(Path.Combine(_ws, ".cws"));
        string resolved = WorkspaceRefs.Resolve(Assert.Single(cws.KnownFiles), _ws);
        Assert.Equal(Norm(Path.Combine(_ws, "sub", "data.s2p")), Norm(resolved));
        Assert.True(File.Exists(resolved));
    }

    [Fact]
    public void Gate6_CwsOpenDocumentListFollowsTheCellThatMoves()
    {
        var a = Cell("A");
        string sch = SchematicPlacing(a);

        WriteCws(_ws, cws =>
        {
            cws.OpenDocuments = [new CwsOpenDocument
            {
                Path = WorkspaceRefs.ToStoredRef(sch, _ws), Kind = "schematic", TabOrder = 0,
            }];
            cws.ActiveDocumentPath = WorkspaceRefs.ToStoredRef(sch, _ws);
        });

        Directory.CreateDirectory(Path.Combine(_ws, "sub"));
        Move(a, Path.Combine(_ws, "sub"));

        var cws = WorkspacePersistence.LoadFromFile(Path.Combine(_ws, ".cws"));
        string expected = Norm(Path.Combine(_ws, "sub", "A", "schematic", "A.csch"));

        Assert.Equal(expected, Norm(WorkspaceRefs.Resolve(cws.OpenDocuments![0].Path, _ws)));
        Assert.Equal(expected, Norm(WorkspaceRefs.Resolve(cws.ActiveDocumentPath!, _ws)));
        Assert.True(File.Exists(expected));
    }

    [Fact]
    public void Gate6_SnpFileParameterIsRelativeToTheROOT_SoAMovedSchematicDoesNotTouchIt()
    {
        var a = Cell("A");
        string snp = Path.Combine(_ws, "data.s2p");
        File.WriteAllText(snp, "! touchstone");

        string schDir = CellFolder.SubFolderPath(a, ViewType.Schematic);
        string sch    = Path.Combine(schDir, "A.csch");
        var model = new SchematicEditModel();
        var comp  = new EditableComponent { InstanceName = "S1", Symbol = SymbolKind.Snp };
        comp.Parameters.Add(new EditableParameter { Name = "File", Expression = SnpPathPolicy.ToStored(snp, _ws) });
        model.Components.Add(comp);
        SchematicPersistence.SaveToFile(sch, model);
        string before = File.ReadAllText(sch);

        Directory.CreateDirectory(Path.Combine(_ws, "sub"));
        var result = Move(a, Path.Combine(_ws, "sub"));

        // Its base is the workspace root, which did not move — so the stored value is correct
        // unchanged, and R-tm1-6 says it must be left alone rather than re-derived.
        string moved = Path.Combine(_ws, "sub", "A", "schematic", "A.csch");
        Assert.DoesNotContain(moved, result.RewrittenFiles);
        Assert.Equal(before, File.ReadAllText(moved));
    }

    [Fact]
    public void Gate6_SnpFileParameterFollowsTheDATAFileWhenTHATMoves()
    {
        var a = Cell("A");
        string snp = Path.Combine(_ws, "data.s2p");
        File.WriteAllText(snp, "! touchstone");

        string schDir = CellFolder.SubFolderPath(a, ViewType.Schematic);
        string sch    = Path.Combine(schDir, "A.csch");
        var model = new SchematicEditModel();
        var comp  = new EditableComponent { InstanceName = "S1", Symbol = SymbolKind.Snp };
        comp.Parameters.Add(new EditableParameter { Name = "File", Expression = SnpPathPolicy.ToStored(snp, _ws) });
        model.Components.Add(comp);
        SchematicPersistence.SaveToFile(sch, model);

        Directory.CreateDirectory(Path.Combine(_ws, "data"));
        Move(snp, Path.Combine(_ws, "data"));

        var node = JsonNode.Parse(File.ReadAllText(sch));
        string stored = node!["Components"]!.AsArray()[0]!["Parameters"]!.AsArray()
            .First(p => p!["Name"]!.GetValue<string>() == "File")!["Expression"]!.GetValue<string>();
        string? resolved = SnpPathPolicy.Resolve(stored, _ws, schDir);

        Assert.Equal(Norm(Path.Combine(_ws, "data", "data.s2p")), Norm(resolved!));
        Assert.True(File.Exists(resolved));
    }

    // ── 7. Refusals — asserted on the RULE, not through the view ──────────────

    [Fact]
    public void Gate7_AFolderCannotBeMovedInsideItself()
    {
        Directory.CreateDirectory(Path.Combine(_ws, "grp", "inner"));

        var intent = TreeMove.For(
            Path.Combine(_ws, "grp"), NodeKind.UserFolder, Path.Combine(_ws, "grp", "inner"), _ws);

        Assert.Equal(MoveRefusal.IntoItself, intent.Refusal);
        Assert.NotEmpty(intent.Message);
    }

    [Fact]
    public void Gate7_ADestinationThatAlreadyHoldsThatNameIsRefused()
    {
        Cell("B");
        Directory.CreateDirectory(Path.Combine(_ws, "sub", "B"));

        var intent = TreeMove.For(
            Path.Combine(_ws, "B"), NodeKind.Cell, Path.Combine(_ws, "sub"), _ws);

        Assert.Equal(MoveRefusal.NameTaken, intent.Refusal);
        Assert.NotEmpty(intent.Message);
    }

    [Fact]
    public void Gate7_AReadOnlyDestinationIsRefused()
    {
        var b = Cell("B");
        string dest = Path.Combine(_ws, "sub");
        Directory.CreateDirectory(dest);

        WorkspaceWritability.WritabilityProbe = d => !d.Contains("sub", StringComparison.Ordinal);
        try
        {
            var intent = TreeMove.For(b, NodeKind.Cell, dest, _ws);
            Assert.Equal(MoveRefusal.NotWritable, intent.Refusal);
            Assert.NotEmpty(intent.Message);
        }
        finally { WorkspaceWritability.WritabilityProbe = null; }
    }

    [Fact]
    public void Gate7_ACellBelongingToAnotherWorkspaceIsRefused()
    {
        string wsB = Path.Combine(_root, "workspaceB");
        Directory.CreateDirectory(wsB);
        WriteCws(wsB);
        string foreign = CellFolder.CreateCellFolder(wsB, "Foreign");

        var intent = TreeMove.For(foreign, NodeKind.Cell, _ws, _ws);

        Assert.Equal(MoveRefusal.NotOwned, intent.Refusal);
        Assert.NotEmpty(intent.Message);
    }

    [Fact]
    public void Gate7_DroppingSomethingBackWhereItAlreadyIsIsANoOpWithNoMessage()
    {
        var b = Cell("B");
        var intent = TreeMove.For(b, NodeKind.Cell, _ws, _ws);

        Assert.Equal(MoveRefusal.AlreadyThere, intent.Refusal);
        Assert.Empty(intent.Message);
    }

    // ── 8. Cell insides are not draggable ─────────────────────────────────────

    [Fact]
    public void Gate8_CellViewFoldersAndViewFilesAreNotMovable()
    {
        Assert.False(TreeMove.IsMovable(NodeKind.CellViewFolder));
        Assert.False(TreeMove.IsMovable(NodeKind.ViewFile));
        Assert.False(TreeMove.IsMovable(NodeKind.Workspace));
        Assert.False(TreeMove.IsMovable(NodeKind.Library));
        Assert.False(TreeMove.IsMovable(NodeKind.LibrariesGroup));
        Assert.False(TreeMove.IsMovable(NodeKind.KnownFilesGroup));
        Assert.False(TreeMove.IsMovable(NodeKind.ReferencedWorkspace));
        Assert.False(TreeMove.IsMovable(NodeKind.ReferencedWorkspacesGroup));
        Assert.False(TreeMove.IsMovable(NodeKind.NotReadYet));

        Assert.True(TreeMove.IsMovable(NodeKind.Cell));
        Assert.True(TreeMove.IsMovable(NodeKind.UserFolder));
        Assert.True(TreeMove.IsMovable(NodeKind.EmSetupFile));
    }

    [Fact]
    public void Gate8_ACellsViewFolderIsRefusedEvenThoughItLooksLikeAnOrdinaryFolderOnDisk()
    {
        var a = Cell("A");
        string schDir = CellFolder.SubFolderPath(a, ViewType.Schematic);

        // From DISK alone it is an ordinary directory with no .ccell — which is why the kind check
        // alone is not enough and IsInsideACell exists.
        Assert.Equal(NodeKind.UserFolder, TreeMove.ClassifyForMove(schDir));
        Assert.True(TreeMove.IsInsideACell(schDir, _ws));

        var intent = TreeMove.For(schDir, TreeMove.ClassifyForMove(schDir), _ws, _ws);
        Assert.Equal(MoveRefusal.NotMovable, intent.Refusal);
    }

    [Fact]
    public void Gate8_ThePayloadShapeIsWhatTheDropReads()
    {
        var folder = Path.Combine(_ws, "grp");
        Directory.CreateDirectory(folder);

        var intent = TreeDrop.ForPayload(new FolderDragPayload(folder).Serialize(), _ws);
        Assert.Equal(TreeDropAction.Move, intent.Action);
        Assert.Equal(Norm(folder), Norm(intent.Path));

        // A cell from this same workspace is now a Move too — the drop MW3 left inert.
        var cell = Cell("B");
        Assert.Equal(TreeDropAction.Move, TreeDrop.ForPayload(new CellDragPayload(cell).Serialize(), _ws).Action);

        // …and one from ANOTHER workspace is still MW3's copy-or-reference.
        string wsB = Path.Combine(_root, "workspaceB");
        Directory.CreateDirectory(wsB);
        WriteCws(wsB);
        string foreign = CellFolder.CreateCellFolder(wsB, "Foreign");
        Assert.Equal(TreeDropAction.Cell, TreeDrop.ForPayload(new CellDragPayload(foreign).Serialize(), _ws).Action);
    }

    // ── 9. A read-only workspace refuses and writes NOTHING ───────────────────

    [Fact]
    public void Gate9_AReadOnlyWorkspaceRefusesTheMoveAndWritesNoRedirectRecord()
    {
        var b = Cell("B");
        string dest = Path.Combine(_ws, "sub");
        Directory.CreateDirectory(dest);

        WorkspaceWritability.WritabilityProbe = _ => false;
        try
        {
            var intent = TreeMove.For(b, NodeKind.Cell, dest, _ws);
            Assert.Equal(MoveRefusal.NotWritable, intent.Refusal);
        }
        finally { WorkspaceWritability.WritabilityProbe = null; }

        Assert.True(Directory.Exists(b));                                  // still where it was
        Assert.False(File.Exists(MoveRedirects.PathFor(_ws)));             // and no record was left
    }

    // ── The registry's own immunity claims, confirmed rather than assumed ─────

    [Fact]
    public void APdkReferenceIsNotAPathAndIsNeverRewritten()
    {
        var a = Cell("A");
        string schDir = CellFolder.SubFolderPath(a, ViewType.Schematic);
        string sch    = Path.Combine(schDir, "A.csch");

        var model = new SchematicEditModel();
        model.Components.Add(new EditableComponent
        {
            InstanceName = "X1", Symbol = SymbolKind.Generic, CellRef = "pdk://SomeKit/SomePart",
        });
        SchematicPersistence.SaveToFile(sch, model);
        string before = File.ReadAllText(sch);

        Directory.CreateDirectory(Path.Combine(_ws, "sub"));
        Move(a, Path.Combine(_ws, "sub"));

        string moved = Path.Combine(_ws, "sub", "A", "schematic", "A.csch");
        Assert.Equal(before, File.ReadAllText(moved));
        Assert.Equal("pdk://SomeKit/SomePart", CellRefIn(moved));
    }

    // ── Owner-reported, 2026-09-03: no indicator and no drop, in a workspace whose cells all sit
    //    at the root beside one user folder. Three defects, all in the view; these pin the one that
    //    is expressible as a rule.

    [Fact]
    public void Regression_ARootLevelCellDroppedOnAUserFolderIsPermitted()
    {
        // The reported workspace's shape: every cell at the root, one ordinary folder beside them.
        var cell = Cell("Amp");
        Cell("Board");
        string folder = Path.Combine(_ws, "my_folder");
        Directory.CreateDirectory(folder);

        var intent = TreeMove.For(cell, TreeMove.ClassifyForMove(cell), folder, _ws);

        Assert.Equal(MoveRefusal.None, intent.Refusal);
        Assert.Equal(Norm(Path.Combine(folder, "Amp")), Norm(intent.DestPath));
    }

    [Fact]
    public void Regression_ANullDestinationMeansTheRoot_WhichForARootLevelCellIsAlreadyThere()
    {
        // Why the view must hit-test the row under the POINTER rather than trust e.Source: a drag
        // event is raised against the AllowDrop element, which is the whole panel, so a destination
        // derived from it is null → the workspace root → "already there" for every root-level cell.
        // That is silent by construction — no highlight, no cursor badge, no drop — which is exactly
        // what was reported.
        var cell = Cell("Amp");

        var atRoot = TreeMove.For(cell, NodeKind.Cell, null, _ws);
        Assert.Equal(MoveRefusal.AlreadyThere, atRoot.Refusal);
        Assert.Empty(atRoot.Message);          // and it says nothing, by design

        // The same drag, with the destination actually resolved, is a real move.
        string folder = Path.Combine(_ws, "my_folder");
        Directory.CreateDirectory(folder);
        Assert.True(TreeMove.For(cell, NodeKind.Cell, folder, _ws).Permitted);
    }

    [Fact]
    public void RelocateLeavesAPathOutsideTheMovedSubtreeExactlyAsItWas()
    {
        string outside = Path.Combine(_ws, "elsewhere", "x.txt");
        string oldRoot = Path.Combine(_ws, "grp");
        string newRoot = Path.Combine(_ws, "dest", "grp");

        Assert.Equal(Norm(outside), WorkspaceMove.Relocate(outside, oldRoot, newRoot));
        Assert.Equal(Norm(newRoot), WorkspaceMove.Relocate(oldRoot, oldRoot, newRoot));
        Assert.Equal(Norm(Path.Combine(newRoot, "B", "schematic")),
                     WorkspaceMove.Relocate(Path.Combine(oldRoot, "B", "schematic"), oldRoot, newRoot));
    }
}

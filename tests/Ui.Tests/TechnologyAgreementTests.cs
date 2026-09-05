using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

// ──────────────────────────────────────────────────────────────────────────────
//  §5C.2a — making two workspaces agree on a technology
//  (docs/design/workspace-and-project-tree.md, R47c–R47i).
//
//  R47 refuses correctly and used to leave the user to repair it by hand; the
//  repair people reached for — copy the other workspace's .ctech in and make it
//  the default — works and silently re-points every other layout in the
//  workspace. These tests pin the four rules that close that gap, plus the walk
//  R47h's narrowed comparison is built on.
//
//  Two real workspaces on disk, for the same reason MW2's own tests use them: the
//  filesystem is the authority on resolution, and a double would agree with
//  itself about a rule it does not implement.
// ──────────────────────────────────────────────────────────────────────────────

[Collection(CellStatGlobalsCollection.Name)]
public sealed class TechnologyAgreementTests : IDisposable
{
    private readonly string _root;
    private readonly string _wsA;
    private readonly string _wsB;

    public TechnologyAgreementTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf_tech5c2a_" + Guid.NewGuid().ToString("N")[..8]);
        _wsA  = Path.Combine(_root, "workspaceA");
        _wsB  = Path.Combine(_root, "workspaceB");
        Directory.CreateDirectory(_wsA);
        Directory.CreateDirectory(_wsB);
        CellSymbolResolver.InvalidateAll();
        CellLayoutResolver.InvalidateAll();
    }

    public void Dispose()
    {
        CellSymbolResolver.InvalidateAll();
        CellLayoutResolver.InvalidateUnder(_root);
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

    private static string WriteTechnology(string root, string fileName, Technology? tech = null)
    {
        string path = Path.Combine(root, fileName);
        TechPersistence.SaveToFile(path, tech ?? StarterTechnologies.Pcb2Layer());
        return path;
    }

    /// <summary>A cell whose primary layout draws one rectangle per key in <paramref name="keys"/>.</summary>
    private static string CreateLayoutCell(string workspaceRoot, string name, params LayerKey[] keys)
    {
        string cellDir = CellFolder.CreateCellFolder(workspaceRoot, name);
        string layDir  = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        Directory.CreateDirectory(layDir);

        var view = new LayoutView { DbuPerMicron = 1000, SnapDbu = 1000 };
        foreach (var key in keys.DefaultIfEmpty(new LayerKey(1, 0)))
            view.Shapes.Add(new RectShape { Layer = key, X1 = 0, Y1 = 0, X2 = 5000, Y2 = 2000 });

        LayoutPersistence.SaveToFile(Path.Combine(layDir, name + ".clay"), view);
        return cellDir;
    }

    private static LayoutView LoadPrimaryLayout(string cellDir)
    {
        string layDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        return LayoutPersistence.LoadFromFile(Directory.GetFiles(layDir, "*.clay")[0]);
    }

    private static string PrimaryLayoutPath(string cellDir) =>
        Directory.GetFiles(CellFolder.SubFolderPath(cellDir, ViewType.Layout), "*.clay")[0];

    // ── R47h — the transitive occupied-key walk the narrowed comparison rests on ──

    [Fact]
    public void OccupiedLayerKeys_CoversSubCells_ViaLandingLayers_AndPins()
    {
        // Each of the three is a key something really lands on, and each was a separate chance to
        // permit a placement whose shapes then take another table's meaning. The via is the sharpest:
        // its barrel and its pad are DIFFERENT fields on one shape, so reading only Layer would miss
        // where the copper goes.
        WriteCws(_wsA);
        string leaf = CreateLayoutCell(_wsA, "Leaf", new LayerKey(3, 0));

        string topDir = CellFolder.CreateCellFolder(_wsA, "Top");
        string topLay = CellFolder.SubFolderPath(topDir, ViewType.Layout);
        Directory.CreateDirectory(topLay);

        var top = new LayoutView { DbuPerMicron = 1000 };
        top.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 100, Y2 = 100 });
        top.Shapes.Add(new ViaShape
        {
            Layer = new LayerKey(7, 0), LandingLayer = new LayerKey(2, 0),
            X = 0, Y = 0, PadSize = 200, DrillSize = 100,
        });
        top.Pins.Add(new LayoutPin { Name = "P1", Layer = new LayerKey(9, 0) });
        top.Instances.Add(new LayoutInstance
        {
            CellRef = Path.GetRelativePath(topLay, leaf).Replace('\\', '/'), Mag = 1.0,
        });
        LayoutPersistence.SaveToFile(Path.Combine(topLay, "Top.clay"), top);
        WorkspaceRootFinder.InvalidateCache();

        var keys = CellHierarchy.OccupiedLayerKeys(LoadPrimaryLayout(topDir), topLay);
        Assert.NotNull(keys);

        Assert.Contains(new LayerKey(1, 0), keys);   // own shape
        Assert.Contains(new LayerKey(7, 0), keys);   // via barrel
        Assert.Contains(new LayerKey(2, 0), keys);   // via landing pad — the second field
        Assert.Contains(new LayerKey(9, 0), keys);   // pin
        Assert.Contains(new LayerKey(3, 0), keys);   // through the instance, transitively
        Assert.DoesNotContain(new LayerKey(8, 0), keys);
    }

    [Fact]
    public void OccupiedLayerKeys_VisitsEachCellONCE_HoweverManyPathsReachIt()
    {
        // The regression this is here for: the first version carried only the DFS-PATH set, so a
        // shared sub-cell was re-walked once per path to it — exponential in depth. A 43-cell fixture
        // took 5.8 s and a real library cell dropped into a workspace hung the UI for a minute
        // (owner, 2026-09-05). Asserting a COUNTER, not a duration: the structural property is "each
        // cell contributes once", and a stopwatch here would measure the machine.
        //
        // Depth 6, fan-out 5 is 15,625 paths to each leaf and 31 unique cells. Undeduped it cannot
        // finish quickly; deduped the leaf is READ once, which is what the resolver's load count says.
        WriteCws(_wsA);
        const int Depth = 6, Fanout = 5;

        var leaves = Enumerable.Range(0, Fanout)
            .Select(i => CreateLayoutCell(_wsA, $"Leaf{i}", new LayerKey(3, 0)))
            .ToList();

        List<string> level = leaves;
        for (int d = 0; d < Depth; d++)
        {
            var next = new List<string>();
            for (int n = 0; n < Fanout; n++)
            {
                string dir = CellFolder.CreateCellFolder(_wsA, $"L{d}_{n}");
                string lay = CellFolder.SubFolderPath(dir, ViewType.Layout);
                Directory.CreateDirectory(lay);
                var v = new LayoutView { DbuPerMicron = 1000 };
                v.Shapes.Add(new RectShape { Layer = new LayerKey(2, 0), X1 = 0, Y1 = 0, X2 = 10, Y2 = 10 });
                foreach (var child in level)
                    v.Instances.Add(new LayoutInstance
                    { CellRef = Path.GetRelativePath(lay, child).Replace('\\', '/'), Mag = 1.0 });
                LayoutPersistence.SaveToFile(Path.Combine(lay, $"L{d}_{n}.clay"), v);
                next.Add(dir);
            }
            level = next;
        }

        string top = CellFolder.CreateCellFolder(_wsA, "Top");
        string topLay = CellFolder.SubFolderPath(top, ViewType.Layout);
        Directory.CreateDirectory(topLay);
        var tv = new LayoutView { DbuPerMicron = 1000 };
        foreach (var child in level)
            tv.Instances.Add(new LayoutInstance
            { CellRef = Path.GetRelativePath(topLay, child).Replace('\\', '/'), Mag = 1.0 });
        LayoutPersistence.SaveToFile(Path.Combine(topLay, "Top.clay"), tv);
        WorkspaceRootFinder.InvalidateCache();

        var keys = CellHierarchy.OccupiedLayerKeys(LoadPrimaryLayout(top), topLay);

        Assert.NotNull(keys);
        Assert.Contains(new LayerKey(2, 0), keys!);
        Assert.Contains(new LayerKey(3, 0), keys!);   // the leaves WERE reached, through all six levels
    }

    [Fact]
    public void OccupiedLayerKeys_ReturnsNullRatherThanAShortAnswer_WhenTheWalkIsTruncated()
    {
        // Deduping made cycles harmless (a union over a graph is well defined), but DEPTH is not: a
        // chain past MaxDepth is cut off by ResolveForWalk, and a SHORT key set is a permit the gate
        // did not earn. Null means "unknown", and the gate falls back to the whole table.
        WriteCws(_wsA);

        string deepest = CreateLayoutCell(_wsA, "Deep0", new LayerKey(5, 0));
        string child   = deepest;
        for (int d = 1; d <= CellHierarchy.MaxDepth + 2; d++)
        {
            string dir = CellFolder.CreateCellFolder(_wsA, $"Deep{d}");
            string lay = CellFolder.SubFolderPath(dir, ViewType.Layout);
            Directory.CreateDirectory(lay);
            var v = new LayoutView { DbuPerMicron = 1000 };
            v.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 10, Y2 = 10 });
            v.Instances.Add(new LayoutInstance
            { CellRef = Path.GetRelativePath(lay, child).Replace('\\', '/'), Mag = 1.0 });
            LayoutPersistence.SaveToFile(Path.Combine(lay, $"Deep{d}.clay"), v);
            child = dir;
        }
        WorkspaceRootFinder.InvalidateCache();

        Assert.Null(CellHierarchy.OccupiedLayerKeys(
            LoadPrimaryLayout(child), CellFolder.SubFolderPath(child, ViewType.Layout)));
    }

    [Fact]
    public void ATruncatedWalk_FallsBackToTheWHOLETable_RatherThanPermitting()
    {
        // The other end of the same rule, at the gate: an over-deep cell is compared conservatively,
        // never waved through. Its own shapes sit on (1,0), which both tables agree about — so a
        // permit here would mean the narrowed comparison had been used on an answer it could not
        // trust.
        var renamed = StarterTechnologies.Pcb2Layer();
        renamed.Layers[6].Name = "Substrate";              // key (7,0) — which nothing below draws on

        string techA = WriteTechnology(_wsA, "processA.ctech", renamed);
        string techB = WriteTechnology(_wsB, "processB.ctech");
        WriteCws(_wsA, c => c.DefaultTechRef = Path.GetRelativePath(_wsA, techA));
        WriteCws(_wsB, c => c.DefaultTechRef = Path.GetRelativePath(_wsB, techB));

        string child = CreateLayoutCell(_wsA, "Deep0");
        for (int d = 1; d <= CellHierarchy.MaxDepth + 2; d++)
        {
            string dir = CellFolder.CreateCellFolder(_wsA, $"Deep{d}");
            string lay = CellFolder.SubFolderPath(dir, ViewType.Layout);
            Directory.CreateDirectory(lay);
            var v = new LayoutView { DbuPerMicron = 1000 };
            v.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 10, Y2 = 10 });
            v.Instances.Add(new LayoutInstance
            { CellRef = Path.GetRelativePath(lay, child).Replace('\\', '/'), Mag = 1.0 });
            LayoutPersistence.SaveToFile(Path.Combine(lay, $"Deep{d}.clay"), v);
            child = dir;
        }
        WorkspaceRootFinder.InvalidateCache();

        Assert.False(ExternalWorkspaceGate.CheckCellTechnology(null, _wsB, child, new TechnologyCache()).Permitted);
    }

    [Fact]
    public void TechnologyDisplay_NamesTheFILE_NotJustTheTechnologysName()
    {
        // Owner, 2026-09-05: the dialog said only the technology's Name, and a workspace can hold
        // several files claiming one name — so the sentence did not identify what was coming across.
        string techA = WriteTechnology(_wsA, "processA.ctech");
        WriteCws(_wsA, c => c.DefaultTechRef = Path.GetRelativePath(_wsA, techA));
        WriteCws(_wsB);
        string amp = CreateLayoutCell(_wsA, "Amp");
        WorkspaceRootFinder.InvalidateCache();

        var check = ExternalWorkspaceGate.CheckCellTechnology(null, _wsB, amp, new TechnologyCache());

        Assert.Contains("processA.ctech", check.TechnologyDisplay);
        Assert.Contains(check.TheirTechName!, check.TechnologyDisplay);
    }

    // ── R47i — a side with no technology has nothing to reinterpret ───────────

    [Fact]
    public void AHostWithNoTechnologyAdoptsTheirs_RatherThanRefusing()
    {
        // The likeliest first encounter with R47: a workspace where layout work is starting from
        // somebody else's cell. Refusing named "(no technology)" on this side, which the user cannot
        // act on — there is nothing here to reconcile with.
        string techA = WriteTechnology(_wsA, "processA.ctech");
        WriteCws(_wsA, c => c.DefaultTechRef = Path.GetRelativePath(_wsA, techA));
        WriteCws(_wsB);                                   // no DefaultTechRef at all
        string amp = CreateLayoutCell(_wsA, "Amp");
        WorkspaceRootFinder.InvalidateCache();

        var check = ExternalWorkspaceGate.CheckCellTechnology(null, _wsB, amp, new TechnologyCache());

        Assert.Equal(ExternalRefOutcome.AdoptTheirTechnology, check.Outcome);
        Assert.True(check.Permitted);
        Assert.Equal(Path.GetFullPath(techA), Path.GetFullPath(check.TheirTechPath!));
    }

    [Fact]
    public void AnExternalCellWithNoTechnologyIsPermitted_TheMirrorOfR47i()
    {
        // The same argument the other way round: its shapes were authored against no layer table, so
        // there is no author's meaning for the host's table to contradict — and no .ctech to adopt.
        string techB = WriteTechnology(_wsB, "processB.ctech");
        WriteCws(_wsB, c => c.DefaultTechRef = Path.GetRelativePath(_wsB, techB));
        WriteCws(_wsA);
        string amp = CreateLayoutCell(_wsA, "Amp");
        WorkspaceRootFinder.InvalidateCache();

        var check = ExternalWorkspaceGate.CheckCellTechnology(null, _wsB, amp, new TechnologyCache());

        Assert.Equal(ExternalRefOutcome.Permitted, check.Outcome);
    }

    // ── R47h — the permit is re-asked, and it can expire ──────────────────────

    [Fact]
    public void AuditPlacedExternalRefs_ReportsAPermitThatTheReferencedCellHasOutgrown()
    {
        // The cost of narrowing the comparison: a permit is a statement about the referenced cell's
        // contents at one moment, and that cell lives in someone else's workspace. Nothing is stored,
        // so the re-check is simply the same question asked again.
        var renamed = StarterTechnologies.Pcb2Layer();
        renamed.Layers[6].Name = "Substrate";             // key (7,0) — Drill on the starter

        string techA = WriteTechnology(_wsA, "processA.ctech", renamed);
        string techB = WriteTechnology(_wsB, "processB.ctech");
        WriteCws(_wsA, c => c.DefaultTechRef = Path.GetRelativePath(_wsA, techA));
        WriteCws(_wsB, c =>
        {
            c.DefaultTechRef = Path.GetRelativePath(_wsB, techB);
            c.ReferencedWorkspaces =
            [
                new CwsWorkspaceRef { Alias = "A", Path = Path.GetRelativePath(_wsB, Path.Combine(_wsA, ".cws")) },
            ];
        });

        string amp   = CreateLayoutCell(_wsA, "Amp", new LayerKey(1, 0));
        string board = CreateLayoutCell(_wsB, "Board", new LayerKey(1, 0));
        WorkspaceRootFinder.InvalidateCache();

        string boardLay = CellFolder.SubFolderPath(board, ViewType.Layout);
        var boardView = LoadPrimaryLayout(board);
        boardView.Instances.Add(new LayoutInstance { CellRef = ExternalCellRef.RefFor("A", "Amp"), Mag = 1.0 });

        // Placed while Amp drew only on (1,0), which both tables agree about.
        Assert.Empty(ExternalWorkspaceGate.AuditPlacedExternalRefs(boardView, techB, boardLay, new TechnologyCache()));

        // Amp then grows a shape on the one key the two tables disagree about.
        var ampView = LoadPrimaryLayout(amp);
        ampView.Shapes.Add(new RectShape { Layer = new LayerKey(7, 0), X1 = 0, Y1 = 0, X2 = 10, Y2 = 10 });
        LayoutPersistence.SaveToFile(PrimaryLayoutPath(amp), ampView);
        CellLayoutResolver.InvalidateUnder(_root);

        var findings = ExternalWorkspaceGate.AuditPlacedExternalRefs(
            boardView, techB, boardLay, new TechnologyCache());

        string only = Assert.Single(findings);
        Assert.Contains("layer 7/0", only);
        Assert.Contains("Substrate", only);
        Assert.Contains("Drill", only);
    }

    [Fact]
    public void AuditPlacedExternalRefs_SaysOneThingPerCell_NotPerPlacement()
    {
        // A cell placed forty times has one problem. Forty identical warnings would bury it.
        var renamed = StarterTechnologies.Pcb2Layer();
        renamed.Layers[0].Name = "Ground Plane";          // key (1,0), which every fixture cell draws on

        string techA = WriteTechnology(_wsA, "processA.ctech", renamed);
        string techB = WriteTechnology(_wsB, "processB.ctech");
        WriteCws(_wsA, c => c.DefaultTechRef = Path.GetRelativePath(_wsA, techA));
        WriteCws(_wsB, c =>
        {
            c.DefaultTechRef = Path.GetRelativePath(_wsB, techB);
            c.ReferencedWorkspaces =
            [
                new CwsWorkspaceRef { Alias = "A", Path = Path.GetRelativePath(_wsB, Path.Combine(_wsA, ".cws")) },
            ];
        });
        CreateLayoutCell(_wsA, "Amp");
        string board = CreateLayoutCell(_wsB, "Board");
        WorkspaceRootFinder.InvalidateCache();

        var boardView = LoadPrimaryLayout(board);
        for (int i = 0; i < 5; i++)
            boardView.Instances.Add(new LayoutInstance
            {
                CellRef = ExternalCellRef.RefFor("A", "Amp"), X = i * 10_000, Mag = 1.0,
            });

        var findings = ExternalWorkspaceGate.AuditPlacedExternalRefs(
            boardView, techB, CellFolder.SubFolderPath(board, ViewType.Layout), new TechnologyCache());

        Assert.Single(findings);
    }

    // ── R47f — the blast radius of a workspace-default change ─────────────────

    [Fact]
    public void LayoutsFollowingWorkspaceDefault_CountsNullAndDanglingTechRefs_NotDeviatedOnes()
    {
        // TechRef = null is the ordinary case and MEANS the workspace default (§5A.2), so those
        // layouts move when it moves. One that chose its own does not — unless the file it chose is
        // gone, in which case it is already falling back and moves too.
        string tech = WriteTechnology(_wsA, "processA.ctech");
        WriteCws(_wsA, c => c.DefaultTechRef = Path.GetRelativePath(_wsA, tech));

        string follows  = CreateLayoutCell(_wsA, "Follows");                    // TechRef null
        string deviates = CreateLayoutCell(_wsA, "Deviates");
        string dangling = CreateLayoutCell(_wsA, "Dangling");

        var dv = LoadPrimaryLayout(deviates);
        dv.TechRef = Path.GetRelativePath(
            CellFolder.SubFolderPath(deviates, ViewType.Layout), tech).Replace('\\', '/');
        LayoutPersistence.SaveToFile(PrimaryLayoutPath(deviates), dv);

        var gv = LoadPrimaryLayout(dangling);
        gv.TechRef = "../../nowhere/gone.ctech";
        LayoutPersistence.SaveToFile(PrimaryLayoutPath(dangling), gv);
        WorkspaceRootFinder.InvalidateCache();

        var affected = ExternalWorkspaceGate.LayoutsFollowingWorkspaceDefault(_wsA);

        Assert.Contains(affected, p => p == PrimaryLayoutPath(follows));
        Assert.Contains(affected, p => p == PrimaryLayoutPath(dangling));
        Assert.DoesNotContain(affected, p => p == PrimaryLayoutPath(deviates));
    }

    [Fact]
    public void CompareTechnologies_IsSilentBetweenTwoCopiesOfOneTable()
    {
        // R47f's confirmation exists to be actionable, so the case that changes nothing must not
        // raise it: two copies of one process are one technology (R47a).
        string a = WriteTechnology(_wsA, "processA.ctech");
        string b = WriteTechnology(_wsB, "a-different-name.ctech");

        Assert.Null(ExternalWorkspaceGate.CompareTechnologies(a, b, new TechnologyCache()));
        Assert.NotNull(ExternalWorkspaceGate.CompareTechnologies(
            a, WriteTechnology(_wsB, "mmic.ctech", StarterTechnologies.MmicGaAs()), new TechnologyCache()));
    }

    // ── The dialog opens before the walk finishes ─────────────────────────────

    [Fact]
    public void HasSubCells_GivesTheSameAnswerAsTheFullWalk_WithoutDoingIt()
    {
        // The dialog is shown before Plan finishes (owner, 2026-09-05: a large cell froze the UI for
        // ~60 s before the window appeared), and the sub-cell choice has to be right at that moment.
        // The claim this rests on: CollectHierarchy seeds the reachable set with the top cell and adds
        // a cell only through an in-workspace reference, so "more than one folder travels" holds
        // exactly when the top cell has at least one such reference. The walk changes HOW MANY
        // sub-cells there are; it cannot change WHETHER there are any.
        WriteCws(_wsA);
        WriteCws(_wsB);

        string leaf   = CreateLayoutCell(_wsA, "Leaf");
        string middle = CreateLayoutCell(_wsA, "Middle");
        string top    = CreateLayoutCell(_wsA, "Top");
        string lonely = CreateLayoutCell(_wsA, "Lonely");

        Instance(middle, leaf);
        Instance(top, middle);          // two levels deep, so a one-level answer could disagree
        WorkspaceRootFinder.InvalidateCache();

        foreach (var cell in new[] { top, middle, leaf, lonely })
        {
            bool quick = CrossWorkspaceCellCopy.HasSubCells(cell, _wsA);
            bool full  = CrossWorkspaceCellCopy.Plan(cell, _wsB, _wsB, SubCellMode.Copy).Folders.Count > 1;
            Assert.Equal(full, quick);
        }

        Assert.True(CrossWorkspaceCellCopy.HasSubCells(top, _wsA));
        Assert.False(CrossWorkspaceCellCopy.HasSubCells(lonely, _wsA));
    }

    [Fact]
    public void HasSubCells_IsFalse_ForACellInNoWorkspaceAtAll()
    {
        // The dialog passes the source's own workspace root, which is null for a loose cell folder.
        // A null there must answer "no sub-cell choice", not throw on the way to showing the window.
        string loose = CreateLayoutCell(_root, "Loose");
        Assert.False(CrossWorkspaceCellCopy.HasSubCells(loose, null));
    }

    /// <summary>Places <paramref name="child"/> in <paramref name="parent"/>'s primary layout.</summary>
    private static void Instance(string parent, string child)
    {
        string lay  = CellFolder.SubFolderPath(parent, ViewType.Layout);
        string clay = Directory.GetFiles(lay, "*.clay")[0];
        var view = LayoutPersistence.LoadFromFile(clay);
        view.Instances.Add(new LayoutInstance
        { CellRef = Path.GetRelativePath(lay, child).Replace('\\', '/'), Mag = 1.0 });
        LayoutPersistence.SaveToFile(clay, view);
    }

    // ── R47g — the copy path, which was the unchecked door ────────────────────

    [Fact]
    public void Copy_AsksTheSameTechnologyQuestionThePlacementDoes()
    {
        string techA = WriteTechnology(_wsA, "processA.ctech", StarterTechnologies.MmicGaAs());
        string techB = WriteTechnology(_wsB, "processB.ctech");
        WriteCws(_wsA, c => c.DefaultTechRef = Path.GetRelativePath(_wsA, techA));
        WriteCws(_wsB, c => c.DefaultTechRef = Path.GetRelativePath(_wsB, techB));
        string amp = CreateLayoutCell(_wsA, "Amp");
        WorkspaceRootFinder.InvalidateCache();

        var plan = CrossWorkspaceCellCopy.Plan(amp, _wsB, _wsB, SubCellMode.Copy);

        Assert.True(plan.TechnologyNeedsAnswer);
        Assert.Equal(ExternalRefOutcome.Refused, plan.Technology.Outcome);
    }

    [Fact]
    public void CopyBringingTheTechnology_LandsItInTech_AndPointsTheCopyAtIt()
    {
        // The remedy the refusal recommends, made real: without this the copied .clay kept TechRef =
        // null and silently adopted the destination's table — the very reinterpretation R47 refuses
        // at placement, arriving through the route the refusal itself points at.
        string techA = WriteTechnology(_wsA, "processA.ctech", StarterTechnologies.MmicGaAs());
        string techB = WriteTechnology(_wsB, "processB.ctech");
        WriteCws(_wsA, c => c.DefaultTechRef = Path.GetRelativePath(_wsA, techA));
        WriteCws(_wsB, c => c.DefaultTechRef = Path.GetRelativePath(_wsB, techB));
        string amp = CreateLayoutCell(_wsA, "Amp");
        WorkspaceRootFinder.InvalidateCache();

        var plan = CrossWorkspaceCellCopy.Plan(amp, _wsB, _wsB, SubCellMode.Copy)
            with { BringTechnology = true };
        string written = CrossWorkspaceCellCopy.Execute(plan);

        string landed = Path.Combine(_wsB, "tech", "processA.ctech");
        Assert.True(File.Exists(landed));

        var copied = LoadPrimaryLayout(written);
        Assert.NotNull(copied.TechRef);

        string clayDir = CellFolder.SubFolderPath(written, ViewType.Layout);
        Assert.Equal(Path.GetFullPath(landed),
                     Path.GetFullPath(Path.Combine(clayDir, copied.TechRef!)));
    }

    [Fact]
    public void CopyWithoutTheTechnology_ClearsATechRefThatNoLongerResolves()
    {
        // The other half of R47g. A non-null TechRef is relative to the .clay's own directory: it
        // travels verbatim, and unless the file it names travelled too it now resolves to nothing —
        // so the layout renders on fallback colours rather than on anything anyone chose. Null at
        // least means the destination default, which is a real, resolvable, stated answer.
        string techA = WriteTechnology(_wsA, "processA.ctech");
        WriteCws(_wsA, c => c.DefaultTechRef = Path.GetRelativePath(_wsA, techA));
        WriteCws(_wsB, c => c.DefaultTechRef = Path.GetRelativePath(_wsB, WriteTechnology(_wsB, "processB.ctech")));
        string amp = CreateLayoutCell(_wsA, "Amp");

        var av = LoadPrimaryLayout(amp);
        av.TechRef = Path.GetRelativePath(
            CellFolder.SubFolderPath(amp, ViewType.Layout), techA).Replace('\\', '/');
        LayoutPersistence.SaveToFile(PrimaryLayoutPath(amp), av);
        WorkspaceRootFinder.InvalidateCache();

        string written = CrossWorkspaceCellCopy.Execute(
            CrossWorkspaceCellCopy.Plan(amp, _wsB, _wsB, SubCellMode.Copy));

        Assert.Null(LoadPrimaryLayout(written).TechRef);
    }

    [Fact]
    public void CopyWithoutTheTechnology_LeavesATechRefThatTRAVELLEDWithTheCell()
    {
        // A technology kept beside its own cell goes with the folder, so its relative reference still
        // resolves and must not be cleared — the fix for the dangling case must not break the case
        // that was already right.
        WriteCws(_wsA);
        WriteCws(_wsB, c => c.DefaultTechRef = Path.GetRelativePath(_wsB, WriteTechnology(_wsB, "processB.ctech")));
        string amp = CreateLayoutCell(_wsA, "Amp");

        string inCell = Path.Combine(amp, "amp-process.ctech");
        TechPersistence.SaveToFile(inCell, StarterTechnologies.MmicGaAs());

        var av = LoadPrimaryLayout(amp);
        av.TechRef = "../amp-process.ctech";
        LayoutPersistence.SaveToFile(PrimaryLayoutPath(amp), av);
        WorkspaceRootFinder.InvalidateCache();

        string written = CrossWorkspaceCellCopy.Execute(
            CrossWorkspaceCellCopy.Plan(amp, _wsB, _wsB, SubCellMode.Copy));

        Assert.Equal("../amp-process.ctech", LoadPrimaryLayout(written).TechRef);
        Assert.True(File.Exists(Path.Combine(written, "amp-process.ctech")));
    }

    [Fact]
    public void BringingATechnologyTwice_ReusesTheIdenticalFileRatherThanNumberingIt()
    {
        // Two copies of one process sitting in one workspace is the confusion the Change Technology
        // picker's duplicate-name disambiguation had to be widened for; not creating it is cheaper
        // than labelling it. A DIFFERENT file of the same name still gets its own number.
        string techA = WriteTechnology(_wsA, "processA.ctech", StarterTechnologies.MmicGaAs());
        WriteCws(_wsA, c => c.DefaultTechRef = Path.GetRelativePath(_wsA, techA));
        WriteCws(_wsB);

        string first  = CrossWorkspaceCellCopy.PlaceTechnology(techA, _wsB);
        string second = CrossWorkspaceCellCopy.PlaceTechnology(techA, _wsB);
        Assert.Equal(first, second);

        string other = WriteTechnology(_root, "processA.ctech");   // same NAME, different table
        string third = CrossWorkspaceCellCopy.PlaceTechnology(other, _wsB);
        Assert.NotEqual(first, third);
        Assert.Equal(Path.Combine(_wsB, "tech", "processA_1.ctech"), third);
    }
}

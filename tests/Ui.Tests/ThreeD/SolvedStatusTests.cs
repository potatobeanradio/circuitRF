// brief-em3d-98 — "Solved": which of a 3D view's setups have a current result. No solve in any gate: run directories are built
// by hand (document.c3d, inputs.json, status.json) in a copy of the Die to Heatsink example.
//
//    Gate 1   SerializeForRun: every display property leaves it unchanged; Model changes it.
//    Gate 2   a record kept before this brief (its document holds "Hidden": true) checks as Current.
//    Gate 3   C3dSolveStatus.Of: EM1 (Palace, complete), EM2 (Both: Palace complete, openEMS cancelled), T1 (thermal, no run).
//    Gate 4   a placed .clay's edit makes EM1 OutOfDate naming it; restoring its bytes makes it Current again.
//    Gate 5   status.json left at running: a dead pid is Partial (interrupted); this process's pid is running.
//    Gate 6   no status.json but a document.c3d: complete, no duration.
//    Gate 7   a leg cancelled through Em3dRunService.Recorded leaves status.json at cancelled and no record that reads Current.
//    Gate 8   the badge table, one row per line.
//    Gate 9   Title is untouched by a current result ("Cell.c3d", and "• Cell.c3d" dirty).
//    Gate 10  explain --json, as a process, carries the Solved section for the gate-3 document.
//    Per setup  another setup's edit leaves a result current; its own setup's, or a submodel's From setup's, does not.
//    Per solver a heat source never makes an EM result out of date, nor a port a thermal one, unless a thermal current drives it.
//
// Gate 11 (the em3d-solve-badges figure) needs a headless Avalonia platform, which this project has none of: it was rendered
// through tools/DocGen's HeadlessHost in a scratch harness and looked at (src/Ui/RESOLVED.md). LegendCellsDrawTheirRow holds
// the part a test can: every legend cell is the glyph its row names.

using System.Text.Json;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Diagnostics;
using CircuitRF.Engine.Em3d;
using CircuitRF.Ui.Diagnostics.Fixtures;
using CircuitRF.Ui.Tests.Em3d;
using CircuitRF.Ui.ThreeD;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

public sealed class SolvedStatusTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-98-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly List<C3dEditorViewModel> _open = [];
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _posted = new();

    public void Dispose()
    {
        foreach (var vm in _open) vm.Dispose();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    // ── Gate 1 ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Each display property of the audit (C3dPersistence.SerializeForRun's comment) leaves the run text unchanged; each
    /// Model switch changes it. An instance's and a port's visibility are the view's alone — never written — so they have no
    /// row: there is nothing in the document to toggle.
    /// </summary>
    [Theory]
    [InlineData("object Hidden", false)]
    [InlineData("operand Hidden", false)]
    [InlineData("object Transparency", false)]
    [InlineData("object Group", false)]
    [InlineData("instance Transparency", false)]
    [InlineData("instance Group", false)]
    [InlineData("field plots", false)]
    [InlineData("air box hidden", false)]
    [InlineData("document air box", false)]
    [InlineData("look", false)]
    [InlineData("active setup", false)]
    [InlineData("display unit", false)]
    [InlineData("snap grid", false)]
    [InlineData("object Model", true)]
    [InlineData("instance Model", true)]
    [InlineData("port Model", true)]
    public void Gate1_SerializeForRun_LeavesDisplayOut_AndKeepsModel(string edit, bool changesTheRun)
    {
        var doc = SmallDocument();
        string before = C3dPersistence.SerializeForRun(doc);
        string file = C3dPersistence.Serialize(doc);
        switch (edit)
        {
            case "object Hidden":         doc.Objects[0].Hidden = true; break;
            case "operand Hidden":        ((C3dBoolean)doc.Objects[1]).Tools[0].Hidden = true; break;
            case "object Transparency":   doc.Objects[0].Transparency = 40; break;
            case "object Group":          doc.Objects[0].Group = "pa"; break;
            case "instance Transparency": doc.Instances[0].Transparency = 50; break;
            case "instance Group":        doc.Instances[0].Group = "pa"; break;
            case "field plots":           doc.FieldPlots.Add(new C3dFieldPlot { Name = "F1" }); break;
            case "air box hidden":        doc.AirBoxHidden = true; break;
            case "document air box":      doc.AirBox = new CemAirBox { XMin = new CemAirBoxFace { PaddingPercent = 5 } }; break;
            case "look":                  doc.Look = new C3dLook { Environment = "Dark", Exposure = 1 }; break;
            case "active setup":          doc.ActiveSetup = "EM2"; break;
            case "display unit":          doc.DisplayUnit = LayoutUnit.Mm; break;
            case "snap grid":             doc.SnapDbu = 5000; break;
            case "object Model":          doc.Objects[0].Model = false; break;
            case "instance Model":        doc.Instances[0].Model = false; break;
            case "port Model":            doc.Ports[0].Model = false; break;
        }
        string edited = C3dPersistence.Serialize(doc);
        Assert.NotEqual(file, edited);                                           // the edit is real: the FILE changed
        Assert.Equal(changesTheRun, before != C3dPersistence.SerializeForRun(doc));
        Assert.Equal(edited, C3dPersistence.Serialize(doc));                     // and the call put every display property back
    }

    // ── Gate 2 ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A run kept before this brief kept the document with its Hidden flags; it is compared on today's terms, so an
    /// unchanged model is Current — with the object hidden and with it shown again.</summary>
    [Fact]
    public void Gate2_ARecordKeptWithHidden_ChecksCurrent()
    {
        var doc = SmallDocument();
        doc.Objects[0].Hidden = true;
        string path = Path.Combine(_root, "old", "Cell.c3d");
        string run = Path.Combine(_root, "old", "results", "run");
        Directory.CreateDirectory(run);
        File.WriteAllText(C3dRunDocument.PathIn(run), C3dPersistence.Serialize(doc));     // what the pre-98 record held
        Assert.Contains("\"Hidden\": true", File.ReadAllText(C3dRunDocument.PathIn(run)));

        Assert.False(C3dRunDocument.Check(run, doc, path)!.Stale);
        doc.Objects[0].Hidden = false;
        Assert.False(C3dRunDocument.Check(run, doc, path)!.Stale);
        doc.Objects[0].Model = false;
        Assert.True(C3dRunDocument.Check(run, doc, path)!.DocumentChanged);
    }

    // ── Gates 3 and 4 ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate3_TheThreeSetups_ReadAsTheirRunsLeftThem()
    {
        var f = Fixture();
        var rows = C3dSolveStatus.Of(f.Doc, f.C3d, f.Results);

        Assert.Equal(["EM1 Fem", "EM2 Fem", "EM2 Fdtd", "T1 Thermal"], rows.Select(r => $"{r.Setup} {r.Solver}"));
        var em1 = rows[0];
        Assert.Equal((SolveState.Current, false), (em1.State, em1.Partial));
        Assert.Equal(TimeSpan.FromMinutes(18), em1.Took);
        Assert.Equal((SolveState.Current, false), (rows[1].State, rows[1].Partial));
        Assert.Equal((SolveState.Current, true, C3dRunState.Cancelled), (rows[2].State, rows[2].Partial, rows[2].EndedAs));
        Assert.Equal((SolveState.NotRun, false), (rows[3].State, rows[3].Partial));
        Assert.Equal("Not run", rows[3].Words);
    }

    [Fact]
    public void Gate4_APlacedLayoutsEdit_MakesItOutOfDate_AndRestoringItsBytesMakesItCurrent()
    {
        var f = Fixture();
        string clay = Path.Combine(f.Ws, "Board", "layout", "Board.clay");
        byte[] original = File.ReadAllBytes(clay);
        var stamp = File.GetLastWriteTimeUtc(clay);

        string text = File.ReadAllText(clay);
        Assert.Contains("\"X\": -2000000, \"Y\": -1000000", text);
        File.WriteAllText(clay, text.Replace("\"X\": -2000000, \"Y\": -1000000", "\"X\": -1900000, \"Y\": -1000000"));
        File.SetLastWriteTimeUtc(clay, stamp.AddSeconds(5));                       // a new version, whatever the clock's grain
        var em1 = C3dSolveStatus.OfSetup(f.Doc, f.C3d, f.Results, "EM1").Single();
        Assert.Equal(SolveState.OutOfDate, em1.State);
        Assert.Equal("'Board.clay'", em1.StaleWhat);
        Assert.Equal("Out of date: 'Board.clay' has changed", em1.Words);

        File.WriteAllBytes(clay, original);
        File.SetLastWriteTimeUtc(clay, stamp.AddSeconds(10));
        Assert.Equal(SolveState.Current, C3dSolveStatus.OfSetup(f.Doc, f.C3d, f.Results, "EM1").Single().State);
    }

    // ── Gates 5 and 6 ────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Gate5_ARunLeftRunning_IsInterruptedWhenItsProcessIsGone_AndRunningWhileItLives(bool alive)
    {
        var f = Fixture();
        string dir = f.Dir("EM1", SolverKind.Fem);
        int pid = alive ? Environment.ProcessId : int.MaxValue - 7;                 // no such process
        C3dRunStatus.Write(dir, new C3dRunStatusRecord(C3dRunState.Running, DateTime.UtcNow, null, null, pid));
        var em1 = C3dSolveStatus.OfSetup(f.Doc, f.C3d, f.Results, "EM1").Single();
        Assert.Equal(!alive, em1.Partial);
        Assert.Equal(alive, em1.Running);
        Assert.Equal(alive ? "Running" : "Solved", em1.Words.Split(' ')[0]);
    }

    [Fact]
    public void Gate6_ARecordWithNoStatus_IsComplete_WithNoDuration()
    {
        var f = Fixture();
        string dir = f.Dir("EM1", SolverKind.Fem);
        File.Delete(C3dRunStatus.PathIn(dir));
        var em1 = C3dSolveStatus.OfSetup(f.Doc, f.C3d, f.Results, "EM1").Single();
        Assert.Equal((SolveState.Current, false, C3dRunState.Complete), (em1.State, em1.Partial, em1.EndedAs));
        Assert.Null(em1.Took);
        Assert.True(C3dRunStatus.Read(dir)!.Legacy);
    }

    // ── Gate 7 ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The leg's wiring, with no solver: a cancellation — returned as the leg's status, or thrown — leaves
    /// status.json at cancelled, keeps no inputs record, and removes the previous one, so nothing reads as Current.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Gate7_ACancelledLeg_LeavesCancelled_AndNoRecordThatReadsCurrent(bool thrown)
    {
        var f = Fixture();
        var run = C3dSetups.ForRun(C3dSetups.Read(f.Doc).Single(s => s.Name == "EM1").Setup!, f.C3d);
        Assert.Equal(SolveState.Current, C3dSolveStatus.OfSetup(f.Doc, f.C3d, f.Results, "EM1").Single().State);
        bool kept = false;
        try
        {
            Em3dRunService.Recorded(run, f.Results, Em3dSolverId.Palace, (_, _) => kept = true, () => thrown
                ? throw new OperationCanceledException()
                : Em3dRunService.Leg.Failed(Em3dSolverId.Palace, EmRunStatus.Cancelled, EmDiagnostics.Cancelled()));
        }
        catch (OperationCanceledException) when (thrown) { }

        string dir = f.Dir("EM1", SolverKind.Fem);
        Assert.False(kept);
        Assert.Equal(C3dRunState.Cancelled, C3dRunStatus.Read(dir)!.State);
        Assert.False(File.Exists(C3dRunDocument.PathIn(dir)));
        var em1 = C3dSolveStatus.OfSetup(f.Doc, f.C3d, f.Results, "EM1").Single();
        Assert.NotEqual(SolveState.Current, em1.State);
        Assert.True(em1.Partial);
        Assert.False(C3dSolveStatus.AllCurrent([em1]));
    }

    // ── Per setup (owner, 2026-10-02) ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Adding, editing or removing ANOTHER setup leaves a setup's result current; editing the setup itself makes it out of
    /// date; and a thermal submodel's result follows its From setup, whose result fixes its cut faces.
    /// </summary>
    [Theory]
    [InlineData("add a setup", "EM1", SolveState.Current)]
    [InlineData("edit another setup", "EM1", SolveState.Current)]
    [InlineData("remove another setup", "EM1", SolveState.Current)]
    [InlineData("edit this setup", "EM1", SolveState.OutOfDate)]
    [InlineData("edit a submodel's From setup", "T2", SolveState.OutOfDate)]
    public void OnlyTheSetupsARunIsSolvedFrom_CanMakeItOutOfDate(string edit, string setup, SolveState expected)
    {
        var f = Fixture();
        void Change(string name, Action<EmSetup> mutate)
        {
            int i = C3dSetups.Read(f.Doc).Single(s => s.Name == name).Index;
            var s = EmSetupPersistence.FromEmbedded(f.Doc.Setups[i]);
            mutate(s);
            f.Doc.Setups[i] = EmSetupPersistence.ToEmbedded(s);
        }
        if (edit == "edit a submodel's From setup")
        {
            f.Doc.Setups.Add(EmSetupPersistence.ToEmbedded(new EmSetup
            {
                Name = "T2", Solver3D = Em3dSolverChoice.None, Problem3D = Em3dProblemType.Thermal,
                Thermal = new CemThermal { Submodel = new CemThermalSubmodel { From = "T1", Region = "r" } },
            }));
            C3dRunInputs.Take(f.Doc, f.C3d, f.Files).KeepIn(f.Dir("T2", SolverKind.Thermal));
            Assert.Equal(SolveState.Current, C3dSolveStatus.OfSetup(f.Doc, f.C3d, f.Results, "T2").Single().State);
        }
        switch (edit)
        {
            case "add a setup":          f.Doc.Setups.Add(EmSetupPersistence.ToEmbedded(new EmSetup { Name = "EM3", Solver3D = Em3dSolverId.OpenEms })); break;
            case "edit another setup":   Change("EM2", s => s.OperatingTempC = 85); break;
            case "remove another setup": f.Doc.Setups.RemoveAt(C3dSetups.Read(f.Doc).Single(s => s.Name == "T1").Index); break;
            case "edit this setup":      Change("EM1", s => s.OperatingTempC = 85); break;
            default:                     Change("T1", s => s.OperatingTempC = 85); break;
        }
        var row = C3dSolveStatus.OfSetup(f.Doc, f.C3d, f.Results, setup).Single();
        Assert.Equal(expected, row.State);
        if (expected == SolveState.OutOfDate) Assert.Equal("the model", row.StaleWhat);
        if (setup != "EM1") Assert.Equal(SolveState.Current, C3dSolveStatus.OfSetup(f.Doc, f.C3d, f.Results, "EM1").Single().State);
    }

    /// <summary>
    /// A setup's result is compared only with what its own solver reads: a heat source is thermal and a port EM, so neither
    /// makes the other kind of result out of date — except a port a thermal current is driven through, which that run reads.
    /// </summary>
    [Theory]
    [InlineData("add a heat source", "EM1", SolveState.Current)]
    [InlineData("edit a port", "EM1", SolveState.OutOfDate)]
    [InlineData("add a port", "T1", SolveState.Current)]
    [InlineData("edit a port no thermal current names", "T1", SolveState.Current)]
    [InlineData("edit the port a thermal current drives", "T1", SolveState.OutOfDate)]
    public void EachSolverIsComparedOnWhatItReads(string edit, string setup, SolveState expected)
    {
        var f = Fixture();
        const long Um = 1000;
        C3dPort Port(int n) => new() { Number = n, Name = $"P{n}", Kind = Em3dPortKind.Lumped, Rect = new C3dRect { Size = new(10 * Um, 10 * Um) } };
        f.Doc.Ports.AddRange([Port(1), Port(2)]);
        int t1 = C3dSetups.Read(f.Doc).Single(s => s.Name == "T1").Index;
        var thermal = EmSetupPersistence.FromEmbedded(f.Doc.Setups[t1]);
        thermal.Thermal!.Currents = [new CemThermalCurrent { Port = 1, Dc = "1" }];
        f.Doc.Setups[t1] = EmSetupPersistence.ToEmbedded(thermal);
        foreach (var (name, kind) in new[] { ("EM1", SolverKind.Fem), ("T1", SolverKind.Thermal) })
            C3dRunInputs.Take(f.Doc, f.C3d, f.Files).KeepIn(f.Dir(name, kind));
        Assert.Equal(SolveState.Current, C3dSolveStatus.OfSetup(f.Doc, f.C3d, f.Results, setup).Single().State);

        switch (edit)
        {
            case "add a heat source":                       f.Doc.HeatSources.Add(new C3dHeatSource { Name = "H9", Solid = "die", Power = "2" }); break;
            case "add a port":                              f.Doc.Ports.Add(Port(3)); break;
            case "edit a port":
            case "edit a port no thermal current names":    f.Doc.Ports[1].Z0 = "25"; break;
            default:                                        f.Doc.Ports[0].Z0 = "25"; break;
        }
        Assert.Equal(expected, C3dSolveStatus.OfSetup(f.Doc, f.C3d, f.Results, setup).Single().State);
    }

    // ── Gate 8 ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The brief's table, one row per line: which glyph a solver kind shows.</summary>
    [Theory]
    [InlineData("active current", "Solid", false)]
    [InlineData("active out of date, another current", "Hollow", false)]
    [InlineData("active not run, another current", "Faded", false)]
    [InlineData("nothing current", "", false)]
    [InlineData("active current, partial", "Solid", true)]
    public void Gate8_TheBadgeTable(string situation, string look, bool star)
    {
        var at = DateTime.Today.AddHours(14).AddMinutes(32);
        SetupSolveStatus Fem(string setup, SolveState s, bool partial = false)
            => new(setup, SolverKind.Fem, s, partial, s == SolveState.OutOfDate ? "the model" : null, at, TimeSpan.FromMinutes(18), null,
                   EndedAs: partial ? C3dRunState.Cancelled : C3dRunState.Complete);
        IReadOnlyList<SetupSolveStatus> rows = situation switch
        {
            "active current"                       => [Fem("EM1", SolveState.Current)],
            "active out of date, another current"  => [Fem("EM1", SolveState.OutOfDate), Fem("EM2", SolveState.Current)],
            "active not run, another current"      => [Fem("EM1", SolveState.NotRun), Fem("EM2", SolveState.Current)],
            "nothing current"                      => [Fem("EM1", SolveState.NotRun), Fem("EM2", SolveState.OutOfDate)],
            _                                      => [Fem("EM1", SolveState.Current, partial: true)],
        };
        var glyphs = SolveBadgeRules.Pick(rows, "EM1");
        if (look.Length == 0) { Assert.Empty(glyphs); return; }
        var g = Assert.Single(glyphs);
        Assert.Equal((SolverKind.Fem, Enum.Parse<SolveBadgeLook>(look), star), (g.Kind, g.Look, g.Partial));
        if (look == "Faded") Assert.Contains("setup 'EM2'", g.Tooltip);           // a faded glyph names the setup it is from
        if (look == "Solid") Assert.Equal(star ? "solved (FEM, partial)" : "solved (FEM)", SolveBadgeRules.TitleSuffix(new(rows, "EM1")));
    }

    /// <summary>The legend figure's cells are drawn by the real rule: each cell is the glyph its row names.</summary>
    [Fact]
    public void LegendCellsDrawTheirRow()
    {
        foreach (var row in DocSolvedFixtures.Rows)
            foreach (var kind in SolveBadgeRules.Order)
            {
                var g = Assert.Single(DocSolvedFixtures.SetFor(kind, row).Glyphs);
                Assert.Equal((kind, row.Look, row.Partial), (g.Kind, g.Look, g.Partial));
            }
    }

    // ── Gate 9 ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate9_ACurrentResult_LeavesTheTitleAsItWas()
    {
        var f = Fixture();
        var cws = Path.Combine(f.Ws, ".cws");
        var vm = new C3dEditorViewModel(f.C3d, C3dPersistence.LoadFromFile(f.C3d), () => new PatchRecordingBackend(), () => cws, _posted.Enqueue)
        {
            ResultsRootProvider = () => f.Results,
        };
        _open.Add(vm);
        vm.RestoreActiveSetup(null);
        var doc = new C3dEditorDocument(vm);
        Assert.True(SpinWait.SpinUntil(() =>
        {
            while (_posted.TryDequeue(out var a)) a();
            return vm.SolveStatuses.Count > 0;
        }, TimeSpan.FromSeconds(30)), "the status check never answered");

        Assert.Equal(SolveState.Current, vm.SolveStatuses.First(s => s.Setup == "EM1").State);
        Assert.Contains(doc.SolveBadges.Glyphs, g => g is { Kind: SolverKind.Fem, Look: SolveBadgeLook.Solid });
        Assert.Equal("Die to Heatsink.c3d", doc.Title);

        vm.SetActiveSetup("EM2");                                                  // a saved setting: dirty, and nothing stale
        Assert.Equal("• " + Path.GetFileName(f.C3d), doc.Title);
        Assert.Equal("EM2", vm.Document.ActiveSetup);
        Assert.Equal(SolveState.Current, C3dSolveStatus.OfSetup(vm.Document, f.C3d, f.Results, "EM1").Single().State);
    }

    // ── Gate 10 ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate10_ExplainJson_CarriesTheSolvedSection()
    {
        var f = Fixture();
        var (exit, stdout, stderr) = CliProcess.Run(PalaceBackendTests.RepoRoot(), [], "explain", f.C3d, "--json");
        Assert.True(exit is 0 or 1, stderr);
        using var json = JsonDocument.Parse(stdout);
        var solved = json.RootElement.GetProperty("result").GetProperty("explain").GetProperty("solved").EnumerateArray().ToList();
        Assert.Equal(["EM1 fem current False", "EM2 fem current False", "EM2 fdtd current True", "T1 thermal notRun False"],
                     solved.Select(s => $"{s.GetProperty("setup").GetString()} {s.GetProperty("solver").GetString()} " +
                                        $"{s.GetProperty("state").GetString()} {s.GetProperty("partial").GetBoolean()}"));
        Assert.Equal("cancelled", solved[2].GetProperty("endedAs").GetString());
        Assert.Equal(1080, solved[0].GetProperty("tookSeconds").GetDouble());
    }

    // ── fixtures ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A box, a boolean with an operand, an instance and a port — enough to toggle every property of gate 1.</summary>
    private static C3dDocument SmallDocument()
    {
        const long Um = 1000;
        C3dBox Box(string name, long x) => new() { Name = name, Material = "Copper", Min = new(x * Um, 0, 0), Size = new(100 * Um, 100 * Um, 100 * Um) };
        return new C3dDocument
        {
            Objects = [Box("a", 0), new C3dBoolean { Name = "b", Op = C3dBooleanOp.Subtract, Blank = Box("", 200), Tools = [Box("t", 220)] }],
            Instances = [new C3dInstance { Name = "I1", CellRef = "Part" }],
            Ports = [new C3dPort { Number = 1, Name = "P1", Kind = Em3dPortKind.Lumped, Rect = new C3dRect { Size = new(10 * Um, 10 * Um) } }],
            Setups = [EmSetupPersistence.ToEmbedded(new EmSetup { Name = "EM1", Solver3D = Em3dSolverId.Palace }),
                      EmSetupPersistence.ToEmbedded(new EmSetup { Name = "EM2", Solver3D = Em3dSolverId.Palace })],
        };
    }

    private sealed record Workspace(string Ws, string C3d, C3dDocument Doc, string Results, IReadOnlyList<string> Files)
    {
        public string Dir(string setup, SolverKind kind)
            => C3dSolveStatus.RunDirectories(C3dSetups.ForRun(C3dSetups.Read(Doc).Single(s => s.Name == setup).Setup!, C3d), Results)
                             .Single(x => x.Kind == kind).Directory;
    }

    /// <summary>
    /// The Die to Heatsink example (a placed board layout, a technology looking through a material library) with three setups
    /// and the runs gate 3 describes: EM1 Palace complete (18 min); EM2 Both, Palace complete and openEMS cancelled — its
    /// record present, so its result reads Current and partial; T1 thermal, never run.
    /// </summary>
    private Workspace Fixture()
    {
        string ws = Copy("Thermal Die to Heatsink");
        string c3d = Path.Combine(ws, "Die to Heatsink", "3d", "Die to Heatsink.c3d");
        var doc = C3dPersistence.LoadFromFile(c3d);
        doc.Setups =
        [
            EmSetupPersistence.ToEmbedded(new EmSetup { Name = "EM1", Solver3D = Em3dSolverId.Palace }),
            EmSetupPersistence.ToEmbedded(new EmSetup { Name = "EM2", Solver3D = CircuitRF.Design.Em3d.Em3dSolvers.Both }),
            EmSetupPersistence.ToEmbedded(new EmSetup { Name = "T1", Solver3D = Em3dSolverChoice.None, Problem3D = Em3dProblemType.Thermal, Thermal = new CemThermal() }),
        ];
        doc.ActiveSetup = null;
        C3dPersistence.SaveToFile(c3d, doc);
        var files = C3dElaborator.ElaborateOnce(doc, c3d, Path.Combine(ws, ".cws")).FilesRead;
        var f = new Workspace(ws, c3d, doc, Path.Combine(ws, "results"), files);
        var started = DateTime.UtcNow.AddMinutes(-30);
        void Run(string setup, SolverKind kind, C3dRunState state, TimeSpan took)
        {
            string dir = f.Dir(setup, kind);
            C3dRunInputs.Take(doc, c3d, files).KeepIn(dir);
            C3dRunStatus.Write(dir, new C3dRunStatusRecord(state, started, started + took, null, Environment.ProcessId));
        }
        Run("EM1", SolverKind.Fem, C3dRunState.Complete, TimeSpan.FromMinutes(18));
        Run("EM2", SolverKind.Fem, C3dRunState.Complete, TimeSpan.FromMinutes(12));
        Run("EM2", SolverKind.Fdtd, C3dRunState.Cancelled, TimeSpan.FromMinutes(3));
        return f;
    }

    private string Copy(string example)
    {
        string src = Path.Combine(PalaceBackendTests.RepoRoot(), "examples", example);
        string dst = Path.Combine(_root, example);
        foreach (string file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "results" + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
            string to = Path.Combine(dst, Path.GetRelativePath(src, file));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to);
        }
        return Path.GetFullPath(dst);
    }
}

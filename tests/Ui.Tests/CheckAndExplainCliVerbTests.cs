// ================================================================
//  CheckAndExplainCliVerbTests.cs — the gate for `circuitrf check` and `circuitrf explain`
//  (brief-automation-4-check-and-explain.md §5).
//
//  ── What these tests are actually pinning ─────────────────────────────────────────────────────
//
//  R-aut4-2 says `check` writes NO validation logic: every finding comes from a validator that
//  already exists. That claim cannot be checked by asserting a message — a message would be just as
//  green if the verb had grown its own rule. So §5.1 asks for the opposite: one deliberately broken
//  fixture per validator in R-aut4-2's table, asserting the DIAGNOSTIC ID, which is the stable
//  contract (R-aut1-8) and the thing a caller filters on.
//
//  §5.4, §5.5 and §5.6 are the agreement gates and they are written the way EmCliVerbTests is
//  written: the answer `explain` reports is compared against what the RUN actually does — the real
//  CLI as a separate process for `hb`, the real `EmSetupResolver.Resolve` in process for `em`, the
//  real `Elaborator` for `--expr`. Asserting against a transcription of SelectTop would pass on the
//  day the two parted company, which is the one failure these gates exist to catch.
//
//  §5.7 is deliberately NOT a test: a wall-clock assertion measures the machine and flakes
//  (feedback-no-new-timing-benchmark-tests). The number is measured and reported in
//  src/Cli/RESOLVED.md instead.
//
//  Every process launch is the already-built CircuitRF.Cli.dll rather than `dotnet run --project`,
//  which is a hang inside `dotnet test` and not merely a cost (EmCliVerbTests.RunCli records why).
//
//  Fixture paths are anonymized to the SHAPE of a path (§5.1) — a temp folder and invented cell
//  names, never anything from a real tree.
// ================================================================

using System.Diagnostics;
using System.Text.Json;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Assembly;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Symbol;
using Symbol = CircuitRF.Design.Symbol.Symbol;
using CircuitRF.Design.Theming;
using CircuitRF.Design.Workspace;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that skips WITH A REASON on Windows — the repo's existing
/// <c>FixtureFact</c> shape (a <c>Skip</c> set in the constructor), applied to a platform rather than
/// to a missing file. A reported skip is the honest answer; a test that quietly asserts nothing is not.
/// </summary>
internal sealed class NonWindowsFactAttribute : FactAttribute
{
    public NonWindowsFactAttribute(string why)
    {
        if (OperatingSystem.IsWindows()) Skip = why;
    }
}

// In CellStatGlobalsCollection, but NOT for that collection's usual reason — checked rather than
// assumed. Nothing here calls CellStat in process: the fixtures use CellFolder.CreateCellFolder and
// SubFolderPath, and every ResolvePrimary happens inside the CLI's own PROCESS, where it cannot
// reach this assembly's counter.
//
// It belongs here because of what it COSTS. This class launches ~35 real CLI processes, and the
// collection's other members measure filesystem-CALL COUNTS against a cache that is bounded by TIME
// (SL4: a positive answer is cached within a stated freshness). A frame that takes longer than that
// window re-stats and the count doubles — which is a statement about the scheduler, not about the
// code, and is exactly what this collection exists to prevent. Membership costs those classes
// nothing (they still run in parallel with the other ~200) and removes a flake that says nothing.
[Collection(CellStatGlobalsCollection.Name)]
public sealed class CheckAndExplainCliVerbTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "crf-check-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    // ══ §5.1 — one broken fixture per validator, asserted by diagnostic id ════════════════════════

    /// <summary><c>CellViewFileValidator.DescribeDefect</c>: a `.clay` renamed to `.csch`
    /// deserializes CLEANLY into an empty schematic, so only the required-key check separates them.</summary>
    [Fact]
    public void CellViewFileValidator_ALayoutRenamedToACsch_IsReported()
    {
        string csch = Write("Amp.csch", """{ "Shapes": [], "DbuPerMicron": 1000 }""");
        AssertHasDiagnostic(RunCli("check", csch, "--json"), "check.view.defect");
    }

    /// <summary><c>CellFolder.ResolvePrimary</c>: the `.ccell` names a primary that is not there.
    /// <c>PrimaryState</c>'s own remarks say do not collapse this into NoPrimary, so it has its own id.</summary>
    [Fact]
    public void CellFolderResolvePrimary_MissingNamedPrimary_IsReported()
    {
        string cell = CellFolder.CreateCellFolder(Dir("ws"), "Amp");
        WriteSymbol(Path.Combine(CellFolder.SubFolderPath(cell, ViewType.Symbol), "Amp.csym"));
        WriteSymbol(Path.Combine(CellFolder.SubFolderPath(cell, ViewType.Symbol), "Amp_alt.csym"));
        CellPersistence.SaveToFile(Path.Combine(cell, CellFolder.CcellFileName),
            new CcellFile { PrimarySymbol = "gone.csym" });

        AssertHasDiagnostic(RunCli("check", cell, "--json"), "check.cell.primary-missing");
    }

    /// <summary><c>CellFolder.ResolvePrimary</c>'s other reportable state — several views and none
    /// chosen. A WARNING, because that enum's own remarks call it "not an error".</summary>
    [Fact]
    public void CellFolderResolvePrimary_NoPrimaryChosen_IsAWarningNotAnError()
    {
        string cell = CellFolder.CreateCellFolder(Dir("ws"), "Amp");
        WriteSymbol(Path.Combine(CellFolder.SubFolderPath(cell, ViewType.Symbol), "Amp.csym"));
        WriteSymbol(Path.Combine(CellFolder.SubFolderPath(cell, ViewType.Symbol), "Amp_alt.csym"));

        var run = RunCli("check", cell, "--json");
        AssertHasDiagnostic(run, "check.cell.no-primary");
        Assert.Equal(0, run.ExitCode);                       // R-aut4-5: warnings alone still exit 0
        Assert.Equal(1, RunCli("check", cell, "--severity", "warning").ExitCode);
    }

    /// <summary><c>NameValidator</c>: a headless caller must not be able to create — or to be told is
    /// fine — a name the GUI would reject. The validator's own reason, never a second rule set.</summary>
    [NonWindowsFact("Every name NameValidator rejects is one Windows itself refuses, so the broken "
                  + "fixture cannot be created there — see InvalidNameThisPlatformAllows.")]
    public void NameValidator_AnInvalidCellFolderName_IsReported()
    {
        string bad = InvalidNameThisPlatformAllows()!;
        string cell = Path.Combine(Dir("ws"), bad);
        Directory.CreateDirectory(Path.Combine(cell, CellFolder.SubFolderName(ViewType.Schematic)));

        AssertHasDiagnostic(RunCli("check", cell, "--json"), "check.name.invalid");
    }

    /// <summary><c>TechValidation.Analyze</c>: its <c>TechProblemArea</c> travels as a typed argument
    /// rather than being flattened into the sentence (R-aut4-4).</summary>
    [Fact]
    public void TechValidation_ADuplicateLayer_IsReported_WithItsAreaAsAnArgument()
    {
        var tech = new Technology
        {
            Name   = "Broken",
            Layers =
            [
                new LayerDef { Key = new LayerKey(1, 0), Name = "M1", Color = new Rgba(200, 200, 200, 255) },
                new LayerDef { Key = new LayerKey(1, 0), Name = "M1 again", Color = new Rgba(200, 200, 200, 255) },
            ],
        };
        string path = Path.Combine(Dir("tech"), "broken.ctech");
        TechPersistence.SaveToFile(path, tech);

        var doc = AssertHasDiagnostic(RunCli("check", path, "--json"), "check.tech.problem");
        Assert.Equal("Layers", Argument(doc, "check.tech.problem", "area"));
    }

    /// <summary><c>TechnologyResolver</c>: a `.clay` whose own TechRef names nothing. The resolver's
    /// sentence is forwarded whole — it has no typed values to hand.</summary>
    [Fact]
    public void TechnologyResolver_ATechRefThatNamesNothing_IsReported()
    {
        string clay = Path.Combine(Dir("cell/layout"), "Amp.clay");
        LayoutPersistence.SaveToFile(clay, new LayoutView { TechRef = "../tech/gone.ctech" });

        AssertHasDiagnostic(RunCli("check", clay, "--json"), "check.resolver.note");
    }

    /// <summary>A layout resolving no technology at all — a normal, fully-supported state
    /// (layout-view.md §2.4), so a warning rather than an error.</summary>
    [Fact]
    public void TechnologyResolver_NoTechnologyAtAll_IsAWarning()
    {
        string clay = Path.Combine(Dir("cell/layout"), "Amp.clay");
        LayoutPersistence.SaveToFile(clay, new LayoutView());

        var run = RunCli("check", clay, "--json");
        AssertHasDiagnostic(run, "check.technology.none");
        Assert.Equal(0, run.ExitCode);
    }

    /// <summary><c>EmSetupResolver.Resolve</c>: a `.cem` naming a layout that is not there.</summary>
    [Fact]
    public void EmSetupResolver_AMissingLayout_IsReported()
    {
        string cem = Path.Combine(Dir("em"), "Setup.cem");
        EmSetupPersistence.SaveToFile(cem, new EmSetup { LayoutRef = "gone.clay" });

        var run = RunCli("check", cem, "--json");
        AssertHasDiagnostic(run, "check.em.unresolved");
        Assert.Equal(1, run.ExitCode);
    }

    /// <summary>
    /// A data display whose run has not been made yet is <b>clean</b>, and a workspace holding one
    /// still exits 0.
    ///
    /// <para><b>The bug this pins.</b> <c>.cdd</c> classifies as <c>DocumentKind.DataDisplay</c> and
    /// <c>check</c> had no arm for it, so a folder walk fell through to <c>check.path.unknown-kind</c>
    /// — <i>"Nothing circuitRF reads is named 'X.cdd'"</i>, which is false about a document the
    /// application opens, renders and ships. It surfaced the moment an example carried displays:
    /// `check` on that workspace reported six errors and exited 1 with nothing wrong with it.</para>
    ///
    /// <para>The second half matters as much as the first. Results are deliberately not shipped
    /// beside a design — a display is a view of a run, and the run is not a document — so a
    /// reference to a <c>.npy</c> that is not there is a NOTE. If it were an error, every workspace
    /// anyone had not yet simulated would fail its own check, which is how a check stops being run.</para>
    /// </summary>
    [Fact]
    public void ADataDisplayWithNoResultsBesideIt_IsANoteAndNotAnError()
    {
        string ws = Dir("display-ws");
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile());

        File.WriteAllText(Path.Combine(ws, "Bench.cdd"), """
            { "FormatVersion": 2, "SelectedDataSource": "Bench.npy", "SourceAliases": {},
              "Tabs": [ { "Name": "Tab 1", "Plots": [ { "Left": 0, "Top": 0,
                          "Width": 660, "Height": 430, "PlotType": "Rect",
                          "Traces": [ { "SourcePath": "Bench.npy", "CubeName": "Pout_dBm" } ] } ] } ] }
            """);

        var run = RunCli("check", ws, "--json");
        output.WriteLine(run.StdErr);

        AssertHasDiagnostic(run, "check.cdd.source-not-run");

        var ids = Json(run).RootElement.GetProperty("diagnostics").EnumerateArray()
                      .Select(d => d.GetProperty("id").GetString()).ToArray();
        Assert.DoesNotContain("check.path.unknown-kind", ids);

        Assert.Equal(0, run.ExitCode);
    }

    /// <summary><c>CellSymbolResolver</c>: a schematic component referencing a cell folder that does
    /// not exist. The three states that resolver keeps distinct stay distinct here.</summary>
    [Fact]
    public void CellSymbolResolver_AReferenceToNothing_IsReported()
    {
        var model = new SchematicEditModel();
        model.Components.Add(new EditableComponent
        {
            InstanceName = "X1",
            Symbol       = SymbolKind.Generic,   // as MatchFlattenService places one: rendering resolves through CellRef
            CellRef      = "../NoSuchCell",
        });
        string csch = Path.Combine(Dir("docs"), "Top.csch");
        File.WriteAllText(csch, SchematicPersistence.Serialize(model, "Top"));

        AssertHasDiagnostic(RunCli("check", csch, "--json"), "check.ref.not-found");
    }

    /// <summary><c>Elaborator.Elaborate</c>: the cycle detector, which is the one check the brief
    /// calls mandatory. Its message is the expression engine's own.</summary>
    [Fact]
    public void Elaborator_AVariableCycle_IsReported()
    {
        string cnl = Write("cycle.cnl", """
            a = b
            b = a
            R:R1 n1 0 R=a
            analysis SP1 type=sparam start=1e9 stop=2e9 step=1e8
            """);

        var run = RunCli("check", cnl, "--json");
        AssertHasDiagnostic(run, "check.elaboration.failed");
        Assert.Equal(1, run.ExitCode);
    }

    /// <summary>Chain selection: a netlist declaring an HB whose chain is DISABLED. The distinction
    /// SelectTop draws — "declares none" versus "declares one that is disabled" — is the actionable
    /// half, and it is forwarded rather than re-authored.</summary>
    [Fact]
    public void ChainSelection_ADisabledHbChain_IsReported()
    {
        string cnl = Write("disabled.cnl", """
            RFfreq = 2e9
            R:R1 n1 0 R=50
            analysis HB1 type=hb Tone=RFfreq MaxHarm=3 Enabled=false
            """);

        var doc = AssertHasDiagnostic(RunCli("check", cnl, "--json"), "check.analysis.none");
        Assert.Contains("disabled", Argument(doc, "check.analysis.none", "reasons")!);
    }

    /// <summary>
    /// <c>DrcEngine</c>, running headlessly — which is the whole of R-aut4-3. Two conductors 40 DBU
    /// apart against a 100 DBU spacing rule, exactly the fixture <c>DrcEngineTests</c> uses, reached
    /// this time through the CLI as a process. If the engine had not crossed the firewall this test
    /// could not exist at all.
    /// </summary>
    [Fact]
    public void DrcEngine_ASpacingViolation_IsReportedByTheCliAsAProcess()
    {
        string ws   = Dir("ws");
        string tech = Path.Combine(ws, "proc.ctech");
        var key     = new LayerKey(1, 0);
        var t = new Technology
        {
            Name   = "Proc",
            Layers = [new LayerDef { Key = key, Name = "M1", Color = new Rgba(200, 200, 200, 255) }],
        };
        t.DrcRules.Add(new DrcRule
        {
            Name = "M1 min spacing", Kind = DrcRuleKind.MinSpacing, Layer = key, ValueDbu = 100,
        });
        TechPersistence.SaveToFile(tech, t);

        string clay = Path.Combine(Dir("ws/cell/layout"), "Amp.clay");
        var view = new LayoutView { TechRef = Path.GetRelativePath(Path.GetDirectoryName(clay)!, tech) };
        view.Shapes.Add(new RectShape { Layer = key, Net = "A", X1 = 0,   Y1 = 0, X2 = 100, Y2 = 100 });
        view.Shapes.Add(new RectShape { Layer = key, Net = "B", X1 = 140, Y1 = 0, X2 = 240, Y2 = 100 });
        LayoutPersistence.SaveToFile(clay, view);

        var doc = AssertHasDiagnostic(RunCli("check", clay, "--json"), "check.drc.violation");
        Assert.Equal("M1 min spacing", Argument(doc, "check.drc.violation", "rule"));
        Assert.Equal("MinSpacing",     Argument(doc, "check.drc.violation", "kind"));
    }

    /// <summary>The `.wasm` predicate parser — the assembly rule set's own authority on its rules,
    /// and the same one the DRC engine compiles them with.</summary>
    [Fact]
    public void WasmPredicateParser_AnUnparseableRule_IsReported()
    {
        var file = new WasmFile { Name = "House" };
        file.Process.Add(new WasmRule { Name = "bad", Expression = "wire_spacing(all) >= " });
        string wasm = Path.Combine(Dir("rules"), "house.wasm");
        WasmPersistence.SaveToFile(wasm, file);

        AssertHasDiagnostic(RunCli("check", wasm, "--json"), "check.wasm.rule-invalid");
    }

    /// <summary>A `.cws` reference list that names nothing. The GUI shows these as warning nodes in
    /// the project tree; this is the same finding, said headlessly.</summary>
    [Fact]
    public void Workspace_AnUnresolvableReference_IsReported()
    {
        string ws = Dir("ws");
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"),
            new CwsFile { DefaultTechRef = "tech/gone.ctech" });

        AssertHasDiagnostic(RunCli("check", ws, "--json"), "check.workspace.ref-unresolved");
    }

    /// <summary>
    /// R-aut4-11: one verb over every document type, and the type comes from the PATH. An
    /// interchange file is recognised through `convert`'s own classifier rather than being called
    /// unknown — a GDSII file circuitRF can read must not be reported as one it cannot.
    /// </summary>
    [Fact]
    public void Check_AnInterchangeFile_IsNamedRatherThanCalledUnknown()
    {
        string gds = Path.Combine(Dir("interchange"), "Amp.gds");
        File.WriteAllBytes(gds, new byte[64]);

        var run = RunCli("check", gds, "--json");
        AssertHasDiagnostic(run, "check.path.interchange");
        Assert.Equal(0, run.ExitCode);                       // Info only — nothing is wrong with it
    }

    [Fact]
    public void Check_APathCircuitrfDoesNotRead_IsRefusedRatherThanPassedSilently()
    {
        string other = Write("notes.md", "# not a design");
        var run = RunCli("check", other, "--json");
        AssertHasDiagnostic(run, "check.path.unknown-kind");
        Assert.Equal(1, run.ExitCode);
    }

    // ══ §5.2 — a clean tree checks clean ═════════════════════════════════════════════════════════

    /// <summary>
    /// The schematics this repository actually ships — the four New-Schematic templates and the four
    /// the user documentation is generated from. Exit 0 with no error-severity diagnostic.
    ///
    /// <para>§5.2's instruction if this fails is explicit: that is a finding about the REPO, to be
    /// reported rather than adjusted away. It is asserted here rather than left to a manual run
    /// precisely so nobody has to remember to look.</para>
    /// </summary>
    [Theory]
    [InlineData("src/Ui/resources/schematic-templates")]
    [InlineData("src/Ui/resources/doc-schematics")]
    public void ShippedSchematics_CheckClean(string relativePath)
    {
        string dir = Path.Combine(RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(Directory.Exists(dir), $"The shipped schematics are missing: {dir}");

        var run = RunCli("check", dir, "--recursive", "--json");
        output.WriteLine(run.StdErr);

        var doc = Json(run);
        Assert.Equal(0, run.ExitCode);
        Assert.Equal(0, doc.RootElement.GetProperty("result").GetProperty("check")
                          .GetProperty("errors").GetInt32());
    }

    /// <summary>The shipped technologies, which every new workspace copies one of.</summary>
    [Fact]
    public void ShippedTechnologies_CheckClean()
    {
        string dir = Path.Combine(RepoRoot(), "src", "Design", "resources", "technologies");
        var run = RunCli("check", dir, "--severity", "warning");
        output.WriteLine(run.StdErr);
        Assert.Equal(0, run.ExitCode);
    }

    // ══ §5.3 — check never writes ════════════════════════════════════════════════════════════════

    /// <summary>
    /// R-aut4-6. Run against a tree whose files are read-only and assert both a clean exit and
    /// unchanged mtimes — a caller must be able to check a read-only checkout, and a workspace
    /// another process has open.
    ///
    /// <para>mtimes are recorded to the tick and compared exactly. A repair, a re-save or a cache
    /// file would move one of them, and the read-only bit alone would not catch a write into a
    /// sibling path.</para>
    /// </summary>
    [Fact]
    public void Check_WritesNothing_EvenOnAReadOnlyTree()
    {
        string ws = Dir("ws");
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile());

        string cell = CellFolder.CreateCellFolder(ws, "Amp");
        WriteSymbol(Path.Combine(CellFolder.SubFolderPath(cell, ViewType.Symbol), "Amp.csym"));
        LayoutPersistence.SaveToFile(
            Path.Combine(CellFolder.SubFolderPath(cell, ViewType.Layout), "Amp.clay"), new LayoutView());

        var before = Snapshot(ws);
        foreach (string f in before.Keys) File.SetAttributes(f, FileAttributes.ReadOnly);

        try
        {
            var run = RunCli("check", ws);
            output.WriteLine(run.StdErr);
            Assert.Equal(0, run.ExitCode);

            var after = Snapshot(ws);
            Assert.Equal(before.Keys.Order(StringComparer.Ordinal),
                         after.Keys.Order(StringComparer.Ordinal));
            foreach (var (file, when) in before)
                Assert.Equal(when, after[file]);
        }
        finally
        {
            foreach (string f in before.Keys)
                if (File.Exists(f)) File.SetAttributes(f, FileAttributes.Normal);
        }
    }

    // ══ §5.4 — explain --analysis agrees with what a run dispatches ══════════════════════════════

    /// <summary>
    /// The promotion, reported without a run and asserted against the RUN — not against a
    /// transcription of <c>SelectTop</c>. `hb -a HB1` prints its promotion note on stderr; `explain
    /// --analysis HB1` reports the same wrapper as the chain that would dispatch, and names HB1 as
    /// what it was promoted from.
    /// </summary>
    [Fact]
    public void ExplainAnalysis_ReportsThePromotion_AndAgreesWithWhatHbDispatches()
    {
        string cnl = Write("swept.cnl", """
            RFfreq = 2e9
            Pin = -10
            R:R1 n1 0 R=50
            analysis HB1 type=hb Tone=RFfreq MaxHarm=3
            analysis SW1 type=parametric_sweep Var=Pin Start=-10 Stop=-6 Step=2 Unit=dBm Inner=HB1
            """);

        // What the RUN does, from the run verb's own stderr note.
        var run = RunCli("hb", cnl, "-a", "HB1");
        Assert.Contains("is the inner analysis of 'SW1'", run.StdErr);

        // What explain says, with nothing run.
        var doc = Json(RunCli("explain", cnl, "--analysis", "HB1", "--json"));
        var analyses = doc.RootElement.GetProperty("result").GetProperty("explain")
                          .GetProperty("analyses").EnumerateArray().ToArray();

        var sw1 = analyses.Single(a => a.GetProperty("name").GetString() == "SW1");
        Assert.True(sw1.GetProperty("dispatched").GetBoolean());
        Assert.Equal("HB1", sw1.GetProperty("promotedFrom").GetString());

        var hb1 = analyses.Single(a => a.GetProperty("name").GetString() == "HB1");
        Assert.False(hb1.GetProperty("dispatched").GetBoolean());
        Assert.False(hb1.GetProperty("isRoot").GetBoolean());
    }

    /// <summary>
    /// R-aut4-9. The sweep is reported in BASE SI with the unit named AND the scale that got it
    /// there — reading a mark without its scale has already produced a run at 2 Hz that looked
    /// entirely normal. Start/Stop/Step are the engine's own numbers, so a stated unit of dBm
    /// (scale 1) and one of GHz (scale 1e9) are told apart by the numbers, not by the label.
    /// </summary>
    [Fact]
    public void ExplainAnalysis_ReportsTheSweepInBaseSi_WithItsUnitAndItsScale()
    {
        string cnl = Write("freqswept.cnl", """
            RFfreq = 2e9
            R:R1 n1 0 R=50
            analysis HB1 type=hb Tone=RFfreq MaxHarm=3
            analysis SW1 type=parametric_sweep Var=RFfreq Start=1 Stop=3 Step=1 Unit=GHz Inner=HB1
            """);

        var doc = Json(RunCli("explain", cnl, "--analysis", "--json"));
        var sweep = doc.RootElement.GetProperty("result").GetProperty("explain")
                       .GetProperty("analyses").EnumerateArray()
                       .Single(a => a.GetProperty("name").GetString() == "SW1")
                       .GetProperty("sweep");

        Assert.Equal("RFfreq", sweep.GetProperty("variable").GetString());
        Assert.Equal(1e9,  sweep.GetProperty("start").GetDouble());
        Assert.Equal(3e9,  sweep.GetProperty("stop").GetDouble());
        Assert.Equal(1e9,  sweep.GetProperty("step").GetDouble());
        Assert.Equal(1e9,  sweep.GetProperty("scale").GetDouble());
        Assert.Equal("GHz", sweep.GetProperty("statedUnit").GetString());
        Assert.Equal("Hz",  sweep.GetProperty("baseUnit").GetString());
        Assert.Equal(3,     sweep.GetProperty("points").GetInt32());
    }

    // ══ §5.5 — explain agrees with em ════════════════════════════════════════════════════════════

    /// <summary>
    /// A `.cem` in one workspace pointing at a layout in ANOTHER — the shape <c>cli.md</c> §8.1 calls
    /// deliberate, and the one a caller cannot otherwise see. The reported paths are compared against
    /// what <c>EmSetupResolver.Resolve</c> — the function <c>circuitrf em</c> itself calls — returns
    /// for the same file.
    /// </summary>
    [Fact]
    public void Explain_ACemWhoseTwoWalksLandOnDifferentWorkspaces_ReportsWhatEmActuallyUses()
    {
        // Workspace A holds the .cem. Workspace B holds the layout and its technology.
        string wsA = Dir("A");
        WorkspacePersistence.SaveToFile(Path.Combine(wsA, ".cws"), new CwsFile());

        string wsB = Dir("B");
        var key = new LayerKey(1, 0);
        var tech = new Technology
        {
            Name   = "ProcB",
            Layers = [new LayerDef { Key = key, Name = "M1", Color = new Rgba(200, 200, 200, 255) }],
        };
        string techPath = Path.Combine(wsB, "procB.ctech");
        TechPersistence.SaveToFile(techPath, tech);
        WorkspacePersistence.SaveToFile(Path.Combine(wsB, ".cws"),
            new CwsFile { DefaultTechRef = "procB.ctech" });

        string clay = Path.Combine(Dir("B/cell/layout"), "Amp.clay");
        var view = new LayoutView();
        view.Shapes.Add(new RectShape { Layer = key, X1 = 0, Y1 = 0, X2 = 1000, Y2 = 100 });
        LayoutPersistence.SaveToFile(clay, view);

        string cem = Path.Combine(wsA, "Setup.cem");
        EmSetupPersistence.SaveToFile(cem, new EmSetup
        {
            LayoutRef = Path.GetRelativePath(wsA, clay),
        });

        // The authority: the same call `circuitrf em` makes.
        string? cwsA = WorkspaceRootFinder.FindAncestorCws(Path.GetDirectoryName(Path.GetFullPath(cem)));
        var truth = EmSetupResolver.Resolve(
            Path.GetFullPath(cem), Path.GetRelativePath(wsA, clay), cwsA, new TechnologyCache());

        var doc = Json(RunCli("explain", cem, "--json"));
        var walks = doc.RootElement.GetProperty("result").GetProperty("explain")
                       .GetProperty("walks").EnumerateArray().ToArray();

        Assert.Equal(Path.GetFullPath(truth.LayoutPath!),
                     Path.GetFullPath(Step(walks, "layout").GetProperty("resolved").GetString()!));
        Assert.Equal(Path.GetFullPath(truth.TechnologyPath!),
                     Path.GetFullPath(Step(walks, "technology").GetProperty("resolved").GetString()!));

        // The point of the fixture: the technology came from workspace B, not from the .cem's own.
        Assert.StartsWith(Path.GetFullPath(wsB), Path.GetFullPath(truth.TechnologyPath!), StringComparison.Ordinal);
        Assert.Equal(Path.GetFullPath(Path.Combine(wsA, ".cws")),
                     Path.GetFullPath(Step(walks, "workspace").GetProperty("resolved").GetString()!));
    }

    // ══ §5.6 — explain --expr agrees with elaboration ════════════════════════════════════════════

    /// <summary>
    /// The same expression, before and after a <c>--set</c> override, compared against what the
    /// ELABORATED netlist holds for the same override — not against a number written down here.
    /// R-aut4-7: it evaluates through the one expression engine, never by substitution.
    /// </summary>
    [Fact]
    public void ExplainExpr_AgreesWithElaboration_BeforeAndAfterASetOverride()
    {
        string cnl = Write("scoped.cnl", """
            f0 = 2e9
            fStop = 2*f0
            R:R1 n1 0 R=50
            analysis SP1 type=sparam start=1e9 stop=2e9 step=1e8
            """);

        double Elaborated(string? setName, string? setExpr)
        {
            var (lib, tb) = CnlReader.ReadFile(cnl);
            if (setName is not null)
            {
                tb.GlobalVariables.RemoveAll(v => v.Name == setName);
                tb.GlobalVariables.Add(new Variable(setName, setExpr!));
            }
            using var nl = new Elaborator(lib).Elaborate(tb);
            return nl.ResolvedGlobals["fStop"].AsReal();
        }

        double before = Value(Json(RunCli("explain", cnl, "--expr", "fStop", "--json")));
        double after  = Value(Json(RunCli("explain", cnl, "--set", "f0=3e9", "--expr", "fStop", "--json")));

        Assert.Equal(Elaborated(null, null), before);
        Assert.Equal(Elaborated("f0", "3e9"), after);
        Assert.NotEqual(before, after);                     // the override actually moved something

        static double Value(JsonDocument d) =>
            d.RootElement.GetProperty("result").GetProperty("explain")
             .GetProperty("expression").GetProperty("real").GetDouble();
    }

    /// <summary>R-aut4-8: an expression that does not evaluate is answered with the failure, named
    /// and coded, rather than with a null or a fallback.</summary>
    [Fact]
    public void ExplainExpr_AnUnresolvedName_IsTheAnswer()
    {
        string cnl = Write("plain.cnl", """
            f0 = 2e9
            R:R1 n1 0 R=50
            analysis SP1 type=sparam start=1e9 stop=2e9 step=1e8
            """);

        var run = RunCli("explain", cnl, "--expr", "nosuchname", "--json");
        AssertHasDiagnostic(run, "explain.expr.failed");
        Assert.Equal(1, run.ExitCode);
    }

    /// <summary>Kinds are reported, never coerced — a Bool forced to a number is a different answer.</summary>
    [Fact]
    public void ExplainExpr_ReportsTheKind_ForRealComplexAndBool()
    {
        string cnl = Write("kinds.cnl", """
            f0 = 2e9
            R:R1 n1 0 R=50
            analysis SP1 type=sparam start=1e9 stop=2e9 step=1e8
            """);

        Assert.Equal("real",    Kind("f0"));
        Assert.Equal("complex", Kind("1+2*j"));
        Assert.Equal("bool",    Kind("f0 > 1e9"));

        string? Kind(string expr) =>
            Json(RunCli("explain", cnl, "--expr", expr, "--json"))
                .RootElement.GetProperty("result").GetProperty("explain")
                .GetProperty("expression").GetProperty("kind").GetString();
    }

    // ══ --ref ════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A relative reference, resolved from the document's own directory — including the
    /// answer that it leaves the workspace, which is the case a caller cannot see from the path.</summary>
    [Fact]
    public void ExplainRef_ReportsWhatAReferenceResolvesTo_AndWhetherItLeavesTheWorkspace()
    {
        string ws = Dir("ws");
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile());

        string inside = CellFolder.CreateCellFolder(ws, "Amp");
        WriteSymbol(Path.Combine(CellFolder.SubFolderPath(inside, ViewType.Symbol), "Amp.csym"));

        string outsideRoot = Dir("elsewhere");
        string outside = CellFolder.CreateCellFolder(outsideRoot, "Ext");
        WriteSymbol(Path.Combine(CellFolder.SubFolderPath(outside, ViewType.Symbol), "Ext.csym"));

        string csch = Path.Combine(CellFolder.SubFolderPath(inside, ViewType.Schematic), "Amp.csch");
        File.WriteAllText(csch, SchematicPersistence.Serialize(new SchematicEditModel(), "Amp"));

        var here = Reference(RunCli("explain", csch, "--ref", "../../Amp", "--json"));
        Assert.Equal("resolved", here.GetProperty("state").GetString());
        Assert.False(here.GetProperty("outsideWorkspace").GetBoolean());

        var away = Reference(RunCli("explain", csch, "--ref",
            Path.GetRelativePath(Path.GetDirectoryName(csch)!, outside), "--json"));
        Assert.Equal("resolved", away.GetProperty("state").GetString());
        Assert.True(away.GetProperty("outsideWorkspace").GetBoolean());

        var gone = RunCli("explain", csch, "--ref", "../../NoSuchCell", "--json");
        AssertHasDiagnostic(gone, "explain.ref.not-found");
        Assert.Equal(1, gone.ExitCode);

        static JsonElement Reference(CliRun run) =>
            Json(run).RootElement.GetProperty("result").GetProperty("explain").GetProperty("reference");
    }

    // ══ shape ════════════════════════════════════════════════════════════════════════════════════

    /// <summary>R-aut4-10 and R-aut1-3: a FAILED invocation still emits a document, because a caller
    /// must never have to tell "no output" apart from "output I could not parse".</summary>
    [Fact]
    public void BothVerbs_EmitADocumentEvenWhenTheyRefuse()
    {
        foreach (string verb in new[] { "check", "explain" })
        {
            var run = RunCli(verb, Path.Combine(_root, "nothing-here"), "--json");
            Assert.Equal(1, run.ExitCode);
            var doc = Json(run);
            Assert.Equal("failed", doc.RootElement.GetProperty("status").GetString());
            Assert.Equal(verb, doc.RootElement.GetProperty("circuitrf").GetProperty("verb").GetString());
            Assert.NotEmpty(doc.RootElement.GetProperty("diagnostics").EnumerateArray());
        }
    }

    /// <summary>R-aut4-1: neither verb runs an analysis, so neither writes a result file. Nothing
    /// lands in <c>outputs</c> — a caller looking for a produced file must not find one invented.</summary>
    [Fact]
    public void NeitherVerb_ClaimsAnOutput()
    {
        string cnl = Write("plain2.cnl", """
            f0 = 2e9
            R:R1 n1 0 R=50
            analysis SP1 type=sparam start=1e9 stop=2e9 step=1e8
            """);

        foreach (var run in new[] { RunCli("check", cnl, "--json"), RunCli("explain", cnl, "--json") })
            Assert.Empty(Json(run).RootElement.GetProperty("outputs").EnumerateArray());
    }

    /// <summary>The questions are refused together rather than ordered — each asks something
    /// different and a precedence nobody stated would be an invention. RND-3 took the count from three
    /// to six and they all obey the same rule (R-rnd3-2); the three new ones are gated in
    /// <c>Render/ExplainQueryCliVerbTests</c>.</summary>
    [Fact]
    public void Explain_MoreThanOneQuestion_IsRefused()
    {
        string cnl = Write("plain3.cnl", "R:R1 n1 0 R=50\n");
        var run = RunCli("explain", cnl, "--analysis", "--ref", "../x", "--json");
        AssertHasDiagnostic(run, "explain.args.one-question");
        Assert.Equal(1, run.ExitCode);
    }

    // ── fixtures and helpers ─────────────────────────────────────────────────────────────────────

    private string Dir(string name)
    {
        string d = Path.Combine(_root, name.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(d);
        return d;
    }

    private string Write(string name, string text)
    {
        string path = Path.Combine(Dir("docs"), name);
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>A minimal but VALID `.csym` — the required-key check needs the key that only a symbol
    /// file writes, and the format's own reader has to accept it.</summary>
    private static void WriteSymbol(string path)
        => File.WriteAllText(path, SymbolPersistence.Serialize(new Symbol([], [], 0)));

    /// <summary>
    /// A folder name <c>NameValidator</c> rejects that this platform will nonetheless let us create.
    ///
    /// <para><b>On Windows there is none, and that is a fact about the validator rather than a gap in
    /// the test.</b> Every name it rejects — the nine disallowed characters, a trailing space or dot,
    /// a reserved device name — is a name Windows itself refuses, which is precisely why the rule
    /// exists (workspace-and-project-tree.md §1.4). So the fixture cannot be built there and the test
    /// says so rather than asserting something weaker.</para>
    /// </summary>
    private static string? InvalidNameThisPlatformAllows()
    {
        if (OperatingSystem.IsWindows()) return null;

        foreach (string candidate in new[] { "Amp.", "Amp*", "Amp?", "CON" })
            if (NameValidator.Validate(candidate) is not null)
                return candidate;

        return null;
    }

    private static Dictionary<string, DateTime> Snapshot(string dir)
        => Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                    .ToDictionary(f => f, File.GetLastWriteTimeUtc, StringComparer.Ordinal);

    private static JsonElement Step(JsonElement[] walks, string step)
        => walks.Single(w => w.GetProperty("step").GetString() == step);

    private static JsonDocument Json(CliRun run) => JsonDocument.Parse(run.StdOut);

    /// <summary>Asserts the run reported <paramref name="id"/>, and returns the parsed document when
    /// the run was made with <c>--json</c>. The ID is the contract; the sentence is not (R-aut1-8),
    /// so nothing here asserts on prose.</summary>
    private JsonDocument AssertHasDiagnostic(CliRun run, string id)
    {
        output.WriteLine(run.StdErr);

        // Every call site passes --json, because the ID only exists in the document: stderr carries
        // the SENTENCE, and asserting on a sentence is what R-aut1-8 says not to do. A run without
        // the flag is a broken test rather than a silently vacuous one.
        Assert.True(run.StdOut.TrimStart().StartsWith('{'),
            "AssertHasDiagnostic needs --json — the diagnostic id is only in the document.");

        var doc = Json(run);
        var ids = doc.RootElement.GetProperty("diagnostics").EnumerateArray()
                     .Select(d => d.GetProperty("id").GetString()).ToArray();
        Assert.Contains(id, ids);
        return doc;
    }

    private static string? Argument(JsonDocument doc, string id, string name)
    {
        var d = doc.RootElement.GetProperty("diagnostics").EnumerateArray()
                    .First(e => e.GetProperty("id").GetString() == id);
        return d.GetProperty("arguments").GetProperty(name).ToString();
    }

    private readonly record struct CliRun(int ExitCode, string StdOut, string StdErr);

    private CliRun RunCli(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory       = RepoRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        psi.ArgumentList.Add(CliDll());
        foreach (string a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return new CliRun(proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }

    private static string CliDll()
    {
        string cliDir = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(
                typeof(CheckAndExplainCliVerbTests).Assembly)
            .First(a => a.Key == "CliDir").Value!;
        return Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll"));
    }

    private static string RepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is { Length: > 0 } && !File.Exists(Path.Combine(dir, "circuitRF.slnx")))
            dir = Path.GetDirectoryName(dir) ?? "";
        return dir;
    }
}

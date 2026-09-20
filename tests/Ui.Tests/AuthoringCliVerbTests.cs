// ================================================================
//  AuthoringCliVerbTests.cs — the gate for `circuitrf new` and `circuitrf import part`
//  (brief-automation-3-authoring-verbs.md §7).
//
//  The verbs' whole claim is R-aut3-1: each calls a capability the GUI's own command also calls, and
//  there is exactly one implementation of each. So the gate is written the way EmCliVerbTests and
//  ConvertCliVerbTests are written — the REAL CLI as a separate process, compared BYTE FOR BYTE
//  against the in-process call the GUI makes for the same inputs. Asserting exit code 0 and a
//  non-empty file would pass just as happily if the verb had grown its own copy of workspace
//  creation, which is the one failure this brief exists to prevent.
//
//  §7.2 is the other half of that claim and cannot be checked by running anything: a byte-identical
//  result proves the two paths AGREE today, not that they are the same code. So the view model's own
//  source is scanned for the calls — with comments stripped first, the trap recorded in
//  project-brief-harmonicarf-h8.
//
//  Not tagged Benchmark, measured rather than assumed: the whole class is a few seconds, and every
//  process launch is the already-built CircuitRF.Cli.dll rather than `dotnet run --project src/Cli`,
//  which is a hang inside `dotnet test` and not merely a cost (EmCliVerbTests.RunCli records why).
// ================================================================

using System.Diagnostics;
using System.Text.RegularExpressions;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui.Schematic;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests;

// Two process-globals, one collection. The in-process half of these gates calls CellCreate,
// CellFolder.ResolvePrimary and ComponentImport.Import, and CellFolder routes every filesystem call
// through CellStat's PROCESS-GLOBAL counter — which is why this class was in CellStatGlobalsCollection.
// It also redirects the per-user STATE DIRECTORY now (see StateDir), and that is a second global with
// one slot: a concurrent class moving it makes the byte-for-byte comparisons compare two different
// installations. UserStateDirectoryCollection is DisableParallelization, so it subsumes the first
// collection's promise rather than trading it away.
[Collection(UserStateDirectoryCollection.Name)]
public sealed class AuthoringCliVerbTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "crf-authoring-" + Guid.NewGuid().ToString("N")[..12]);

    /// <summary>
    /// A throwaway per-user state directory, for BOTH halves of every gate here.
    ///
    /// <para><b>It became necessary when the default technology stopped being a constant.</b> It is
    /// the user's own choice now (Settings ▸ Technology), read out of <c>preferences.json</c> by
    /// <c>TechnologyCatalog</c>, and <c>TechnologyCatalog.UserDirectory</c> can hold technologies the
    /// shipped list does not. Without this, "the verb writes what the GUI's call writes" would be
    /// compared between a child process reading the DEVELOPER's preferences and an in-process call
    /// reading them too — agreeing by luck on a machine that had never opened the tab, and failing on
    /// one that had. Redirected, both see a first-launch installation.</para>
    ///
    /// <para>In-process through <see cref="CircuitRF.Ui.AppDataRoot"/> (which also drops the caches
    /// resolved against the old location) and out-of-process through <c>CRF_STATE_DIR</c>, which is
    /// what that variable exists for — <c>RunCli</c> sets it on every launch.</para>
    /// </summary>
    private string StateDir => Path.Combine(_root, "state");

    private readonly ITestOutputHelper _output;

    public AuthoringCliVerbTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(StateDir);
        CircuitRF.Ui.AppDataRoot.RedirectTo(StateDir);
    }

    public void Dispose()
    {
        CircuitRF.Ui.AppDataRoot.RedirectTo(null);
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    private string Dir(string name)
    {
        string d = Path.Combine(_root, name);
        Directory.CreateDirectory(d);
        return d;
    }

    // ── §7.1  Byte identity against the capability the GUI calls ──────────────────────────────────

    /// <summary>
    /// The `.cws` and the copied `.ctech` the VERB writes are byte-identical to what
    /// <see cref="WorkspaceCreate.Create"/> — the call File ▸ New Workspace now makes — writes for the
    /// same inputs. This is R-aut0-6's standard applied to workspace creation.
    /// </summary>
    [Fact]
    public void NewWorkspace_WritesTheSameBytesAsTheCallTheGuiMakes()
    {
        string viaCli = Path.Combine(Dir("cli"), "Amp");
        var run = RunCli("new", "workspace", viaCli);
        Assert.Equal(0, run.ExitCode);

        var viaCapability = WorkspaceCreate.Create(Dir("gui"), "Amp", WorkspaceCreate.DefaultTechnologyId);

        AssertSameBytes(viaCapability.CwsPath, Path.Combine(viaCli, ".cws"));
        AssertSameBytes(viaCapability.TechPath!,
            Path.Combine(viaCli, "tech", WorkspaceCreate.DefaultTechnologyId + ".ctech"));
    }

    /// <summary>
    /// R-aut3-4: the technology copy is the shipped entry's OWN RAW BYTES, never a re-serialization
    /// through <c>TechPersistence</c> on the way — which would produce a different file. Pinned
    /// against the embedded resource itself rather than against a second write, because "the two
    /// writes agree" would still pass if both had been re-serialized.
    /// </summary>
    [Fact]
    public void NewWorkspace_CopiesTheShippedTechnologysOwnBytes()
    {
        var created = WorkspaceCreate.Create(Dir("raw"), "Amp", WorkspaceCreate.DefaultTechnologyId);

        var entry = ShippedTechnologies.All.First(e => e.Id == WorkspaceCreate.DefaultTechnologyId);
        Assert.Equal(ShippedTechnologies.LoadRawJson(entry), File.ReadAllText(created.TechPath!));
    }

    /// <summary>R-aut3-3: no <c>--tech</c> selects what the New Workspace dialog pre-selects. A
    /// headless default that differs from the dialog's is a second product.
    ///
    /// <para><b>Both sides are <see cref="TechnologyCatalog.DefaultId"/> now</b>, which is the whole
    /// point of the rule rather than a weaker version of it: the dialog's pre-selection is the user's
    /// own choice since Settings ▸ Technology, and a verb still naming
    /// <c>ShippedTechnologies.DefaultId</c> would have gone on creating workspaces on circuitRF's
    /// default while the dialog beside it opened on theirs. That this state directory is a fresh one
    /// is what makes the second assertion below say something: with no preference recorded, the
    /// catalog's answer IS the shipped one.</para>
    /// </summary>
    [Fact]
    public void NewWorkspace_DefaultTechnologyIsTheOneTheDialogPreSelects()
    {
        Assert.Equal(TechnologyCatalog.DefaultId, WorkspaceCreate.DefaultTechnologyId);
        Assert.Equal(ShippedTechnologies.DefaultId, TechnologyCatalog.DefaultId);

        string ws = Path.Combine(Dir("dflt"), "Amp");
        Assert.Equal(0, RunCli("new", "workspace", ws).ExitCode);

        var cws = WorkspacePersistence.LoadFromFile(Path.Combine(ws, ".cws"));
        Assert.Equal(
            Path.Combine("tech", ShippedTechnologies.DefaultId + ".ctech"),
            cws.DefaultTechRef);
    }

    /// <summary>Every file of a cell folder, and the folder structure, byte for byte against
    /// <see cref="CellCreate"/> — the writers the GUI's New Cell / New Symbol / New Layout now
    /// call.</summary>
    [Fact]
    public void NewCell_WritesTheSameCellFolderAsTheCallTheGuiMakes()
    {
        string cliWs = Path.Combine(Dir("cli2"), "Amp");
        Assert.Equal(0, RunCli("new", "workspace", cliWs, "--tech", "none").ExitCode);
        Assert.Equal(0, RunCli("new", "cell", cliWs, "Stage1", "--views", "schematic,symbol,layout").ExitCode);

        string guiWs = Path.Combine(Dir("gui2"), "Amp");
        WorkspaceCreate.Create(Dir("gui2"), "Amp", technologyId: null);
        CellCreate.Create(guiWs, "Stage1", CellViews.Schematic | CellViews.Symbol | CellViews.Layout);

        AssertSameTree(Path.Combine(guiWs, "Stage1"), Path.Combine(cliWs, "Stage1"));
    }

    /// <summary>An imported part, byte for byte against <see cref="ComponentImport.Import"/> — the
    /// call the GUI's Import Component makes, with the same null <c>resolveLayerMapping</c> the
    /// headless path uses (R-aut3-11: the dialog's own pre-selected default).</summary>
    [Fact]
    public void ImportPart_WritesTheSameCellAsTheCallTheGuiMakes()
    {
        string source = Path.Combine(RepoRoot(), "testdata", "component-samples", "widget9");

        string cliWs = Path.Combine(Dir("cli3"), "Amp");
        Assert.Equal(0, RunCli("new", "workspace", cliWs, "--tech", "none").ExitCode);
        var run = RunCli("import", "part", source, "--into", cliWs, "--variant", ".kicad_sym");
        Assert.Equal(0, run.ExitCode);

        string guiWs = Dir("gui3");
        var candidate = ComponentFolderScan.Scan(source).Candidates
            .First(c => c.SymbolFile is { } f && f.Path.EndsWith(".kicad_sym", StringComparison.Ordinal));
        var part = ComponentRead.Read(candidate, LayoutUnits.DefaultDbuPerMicron).Part!;
        var result = ComponentImport.Import(part, guiWs, destTech: null, LayoutUnits.DefaultDbuPerMicron);

        AssertSameTree(result.CellDir!, Path.Combine(cliWs, "WIDGET9"));
    }

    // ── §7.2  The GUI path actually calls the extracted capability ────────────────────────────────

    /// <summary>
    /// R-aut3-1's whole value evaporates if the view model keeps its own copy, and no amount of
    /// byte-comparing catches that — two copies agree right up until one is edited. So the source is
    /// scanned. COMMENTS ARE STRIPPED FIRST: this file's own prose names every symbol involved, and so
    /// does the view model's, so a scan of raw text would pass on a comment
    /// (project-brief-harmonicarf-h8 records that trap).
    /// </summary>
    [Theory]
    [InlineData("ViewModels/WorkspaceViewModel.cs", "WorkspaceCreate.Create(")]
    [InlineData("ViewModels/WorkspaceViewModel.cs", "CellCreate.WriteSchematicView(")]
    [InlineData("ViewModels/WorkspaceViewModel.cs", "CellCreate.WriteSymbolView(")]
    [InlineData("ViewModels/WorkspaceViewModel.cs", "CellCreate.WriteLayoutView(")]
    [InlineData("ViewModels/WorkspaceViewModel.cs", "ComponentImport.Import(")]
    [InlineData("ViewModels/WorkspaceViewModel.ReadOnly.cs", "WorkspaceCreate.UnwritableParentRefusal(")]
    public void TheGuiCallsTheExtractedCapability(string file, string call)
    {
        string code = StripComments(File.ReadAllText(Path.Combine(RepoRoot(), "src", "Ui", file)));
        Assert.Contains(call, code, StringComparison.Ordinal);
    }

    /// <summary>
    /// The other direction: what was extracted is GONE from the view model, so there is no second
    /// implementation left to drift.
    ///
    /// <para>The technology copy is the one step that can be checked this way and it is the one worth
    /// checking: <c>LoadRawJson</c> exists for exactly one purpose (R-aut3-4's byte-for-byte copy) and
    /// a reappearance in the view model IS a second copy of it. The <c>.cws</c> write cannot be
    /// checked the same way — <c>SaveToFileAtomic</c> is also how the debounced dock-layout save
    /// writes, which is a different operation on the same file and stays in the shell.</para>
    /// </summary>
    [Fact]
    public void TheTechnologyCopyIsNoLongerInTheViewModel()
    {
        string code = StripComments(File.ReadAllText(
            Path.Combine(RepoRoot(), "src", "Ui", "ViewModels", "WorkspaceViewModel.cs")));
        Assert.DoesNotContain("ShippedTechnologies.LoadRawJson(", code, StringComparison.Ordinal);
    }

    // ── §7.3  Round trip: the GUI's own scanner reads what the verbs wrote ────────────────────────

    /// <summary>
    /// <c>new workspace</c> → <c>new cell</c> → <c>import part</c>, then the GUI's OWN
    /// <see cref="WorkspaceScanner"/> over the result. Nothing carries a warning, and both cells are
    /// there with their views — which is R-aut3-7 stated as an outcome rather than as an intention: a
    /// verb that creates a folder the resolver then reports as broken has produced something worse
    /// than nothing.
    /// </summary>
    [Fact]
    public void TheThreeVerbsProduceAWorkspaceTheGuiScannerReadsCleanly()
    {
        string ws = Path.Combine(Dir("trip"), "Amp");
        Assert.Equal(0, RunCli("new", "workspace", ws).ExitCode);
        Assert.Equal(0, RunCli("new", "cell", ws, "Stage1", "--views", "schematic,symbol").ExitCode);
        Assert.Equal(0, RunCli("import", "part",
            Path.Combine(RepoRoot(), "testdata", "component-samples", "widget9"),
            "--into", ws, "--variant", ".kicad_sym").ExitCode);

        var root = WorkspaceScanner.Scan(ws);

        var cells = Flatten(root).Where(n => n.Kind == NodeKind.Cell).Select(n => n.Name).OrderBy(n => n).ToList();
        Assert.Equal(["Stage1", "WIDGET9"], cells);

        // Not one warning anywhere — MissingNamedPrimary is the state that would show up here, and it
        // is the one a badly-created cell folder produces.
        var warned = Flatten(root).Where(n => n.WarningReason is not null).ToList();
        Assert.Empty(warned);

        var views = Flatten(root).Where(n => n.Kind == NodeKind.ViewFile).Select(n => n.Name).OrderBy(n => n).ToList();
        Assert.Contains("Stage1.csch", views);
        Assert.Contains("Stage1.csym", views);
        Assert.Contains("WIDGET9.csym", views);
        Assert.Contains("WIDGET9.clay", views);
    }

    // ── §7.4  The end-to-end that justifies the series ────────────────────────────────────────────

    /// <summary>
    /// <b>The first test in this repository that proves a design can be authored and simulated with
    /// no display involved at any step.</b>
    ///
    /// <para><c>new workspace</c>, <c>new cell</c>, a <c>.csch</c> written by hand into the cell the
    /// verb made, extracted to a <c>.cnl</c>, run through <c>circuitrf sparam</c>, and the S-parameter
    /// checked against the closed form. The oracle is a series resistor between two 50 Ω ports, whose
    /// S21 is 2·Z0/(2·Z0+R) — an independent number, not a golden file, so a wrong netlist cannot pass
    /// by agreeing with a previous run of itself.</para>
    /// </summary>
    [Fact]
    public void AWorkspaceAuthoredHeadlesslySimulatesHeadlessly()
    {
        string ws = Path.Combine(Dir("e2e"), "Amp");
        Assert.Equal(0, RunCli("new", "workspace", ws).ExitCode);
        Assert.Equal(0, RunCli("new", "cell", ws, "Divider").ExitCode);

        string csch = Path.Combine(ws, "Divider", "schematic", "Divider.csch");
        Assert.True(File.Exists(csch), "`new cell` did not create the schematic view it reported");

        // The document is JSON and the format is the contract (R-aut-5) — so the way to put a circuit
        // in it is to WRITE it, which is exactly what a caller of these verbs does next. It is written
        // through the model rather than as raw text only because the model IS below the firewall now
        // (AUT-2); nothing here touches a canvas, an editor or a view model.
        var loaded = SchematicPersistence.LoadFromFile(csch);
        BuildSeriesResistor(loaded.model, resistanceOhms: 50.0);
        SchematicPersistence.SaveToFile(csch, loaded.model, cellName: "Divider");

        var extracted = NetExtractor.Extract(SchematicPersistence.LoadFromFile(csch).model, "tb");
        string cnl = Path.Combine(ws, "Divider", "divider.cnl");
        File.WriteAllText(cnl, CnlWriter.Write(extracted.TestBench, extracted.Library, "authored headlessly"));

        string snp = Path.Combine(ws, "Divider", "divider.s2p");
        var run = RunCli("sparam", cnl, "--freq", "1GHz:1GHz:1GHz", "-o", snp);
        _output.WriteLine(run.StdErr);
        Assert.Equal(0, run.ExitCode);
        Assert.True(File.Exists(snp), "sparam wrote no Touchstone");

        // S21 = 2·Z0 / (2·Z0 + R) for a series R between two Z0 ports: 100/150 = 0.6667.
        double s21 = FirstS21Magnitude(snp);
        Assert.Equal(2.0 * 50.0 / (2.0 * 50.0 + 50.0), s21, 6);
    }

    // ── §7.5  Refusals ────────────────────────────────────────────────────────────────────────────

    /// <summary>R-aut3-6: creating over an existing workspace is refused. <c>WorkspaceLock</c>'s own
    /// header explains what is at stake when two things believe they own one <c>.cws</c> — so the verb
    /// neither overwrites nor merges.</summary>
    [Fact]
    public void NewWorkspace_RefusesOverAnExistingOne()
    {
        string ws = Path.Combine(Dir("dup"), "Amp");
        Assert.Equal(0, RunCli("new", "workspace", ws).ExitCode);
        string before = File.ReadAllText(Path.Combine(ws, ".cws"));

        var again = RunCli("new", "workspace", ws);
        Assert.Equal(1, again.ExitCode);
        Assert.Contains("already exists", again.StdErr, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, File.ReadAllText(Path.Combine(ws, ".cws")));
    }

    /// <summary>And the guard is in the CAPABILITY, not only in each caller's pre-flight — so no route
    /// into workspace creation can skip it.</summary>
    [Fact]
    public void WorkspaceCreate_ItselfRefusesOverAnExistingDirectory()
    {
        string parent = Dir("dup2");
        WorkspaceCreate.Create(parent, "Amp", technologyId: null);
        Assert.Throws<IOException>(() => WorkspaceCreate.Create(parent, "Amp", technologyId: null));
    }

    /// <summary>R-aut3-5: an unknown <c>--tech</c> id lists the valid ones and creates nothing. Not a
    /// fallback to the default — a caller that asked for a specific process and silently got another
    /// has a wrong design and no way to know.</summary>
    [Fact]
    public void NewWorkspace_RefusesAnUnknownTechnologyIdAndListsTheRealOnes()
    {
        string ws = Path.Combine(Dir("badtech"), "Amp");
        var run = RunCli("new", "workspace", ws, "--tech", "no-such-process");

        Assert.Equal(1, run.ExitCode);
        Assert.False(Directory.Exists(ws), "a refused creation left a directory behind");
        foreach (var e in TechnologyCatalog.All)
            Assert.Contains(e.Id, run.StdErr, StringComparison.Ordinal);
    }

    /// <summary>R-aut3-9: name validation is <see cref="NameValidator"/>'s, not the verb's — a second
    /// rule set would let a headless caller create a name the GUI rejects.</summary>
    [Theory]
    [InlineData("bad/name")]
    [InlineData("trailing ")]
    [InlineData("CON")]
    public void NewCell_RefusesANameTheGuiWouldReject(string cellName)
    {
        Assert.NotNull(NameValidator.Validate(cellName));   // the premise, stated

        string ws = Path.Combine(Dir("badname" + cellName.GetHashCode()), "Amp");
        Assert.Equal(0, RunCli("new", "workspace", ws, "--tech", "none").ExitCode);

        var run = RunCli("new", "cell", ws, cellName);
        Assert.Equal(1, run.ExitCode);
        Assert.Contains("Invalid cell name", run.StdErr, StringComparison.Ordinal);
        Assert.Empty(Directory.GetDirectories(ws));
    }

    /// <summary>
    /// R-aut3-11: what the chooser dialog would have ASKED is a refusal that names the flags that
    /// answer it — never a guess at which of three parts was meant.
    /// </summary>
    [Fact]
    public void ImportPart_RefusesWhenTheSourceHoldsSeveralPartsAndSaysHowToChoose()
    {
        string ws = Path.Combine(Dir("ambig"), "Amp");
        Assert.Equal(0, RunCli("new", "workspace", ws, "--tech", "none").ExitCode);

        var run = RunCli("import", "part",
            Path.Combine(RepoRoot(), "testdata", "component-samples", "widget9"), "--into", ws);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("--cell", run.StdErr, StringComparison.Ordinal);
        Assert.Contains("--variant", run.StdErr, StringComparison.Ordinal);
        Assert.Contains("--list-parts", run.StdErr, StringComparison.Ordinal);
        Assert.DoesNotContain(Directory.GetDirectories(ws), d => Path.GetFileName(d) != "tech");
    }

    /// <summary>
    /// R-aut3-12: <c>ImportResult.Messages</c> reaches the caller in full. They are how a caller learns
    /// a pin was inferred, a layer was dropped or a variant was skipped — on stderr, and in the
    /// <c>--json</c> document as diagnostics.
    /// </summary>
    [Fact]
    public void ImportPart_ForwardsEveryImportMessage()
    {
        string source = Path.Combine(RepoRoot(), "testdata", "component-samples", "widget9");

        string ws = Path.Combine(Dir("msgs"), "Amp");
        Assert.Equal(0, RunCli("new", "workspace", ws, "--tech", "none").ExitCode);
        var run = RunCli("import", "part", source, "--into", ws, "--variant", ".kicad_sym");
        Assert.Equal(0, run.ExitCode);

        var candidate = ComponentFolderScan.Scan(source).Candidates
            .First(c => c.SymbolFile is { } f && f.Path.EndsWith(".kicad_sym", StringComparison.Ordinal));
        var part = ComponentRead.Read(candidate, LayoutUnits.DefaultDbuPerMicron).Part!;
        var expected = ComponentImport.Import(part, Dir("msgs-ref"), null, LayoutUnits.DefaultDbuPerMicron);

        Assert.NotEmpty(expected.Messages);
        foreach (var m in expected.Messages)
            Assert.Contains(m, run.StdErr, StringComparison.Ordinal);
    }

    /// <summary>
    /// The layers a part needs that its technology does not define are REPORTED even when nothing is
    /// written, and written when <c>--add-layers</c> says so.
    ///
    /// <para>Reporting is not optional: a layer silently dropped is the trap the repo's own `convert`
    /// note already records. WRITING is opt-in because the GUI's own install is session-only and says
    /// in as many words that nothing was written to disk — headless there is no session to hold them
    /// in, so which of report-or-write a caller wants is not something to guess.</para>
    /// </summary>
    [Fact]
    public void ImportPart_ReportsTheLayersItDidNotInstall_AndWritesThemOnRequest()
    {
        string ws = Path.Combine(Dir("layers"), "Amp");
        Assert.Equal(0, RunCli("new", "workspace", ws, "--tech", "none").ExitCode);

        // A technology that declares no layers at all: every layer the part uses is then genuinely
        // new, which is the state the warning is about. (A technology is needed rather than none —
        // with NO destination there is nothing to reconcile against and nothing is reported as new,
        // which the verb warns about separately.)
        string techPath = Path.Combine(ws, "bare.ctech");
        TechPersistence.SaveToFile(techPath, new Technology { Name = "bare" });

        string source = Path.Combine(RepoRoot(), "testdata", "component-samples", "widget9");

        var reported = RunCli("import", "part", source, "--into", ws,
            "--variant", ".kicad_sym", "--tech", techPath);
        Assert.Equal(0, reported.ExitCode);
        Assert.Contains("--add-layers", reported.StdErr, StringComparison.Ordinal);
        Assert.Empty(TechPersistence.LoadFromFile(techPath).Layers);   // reported, not written

        var written = RunCli("import", "part", source, "--into", ws,
            "--variant", ".kicad_sym", "--tech", techPath, "--add-layers");
        Assert.Equal(0, written.ExitCode);
        Assert.NotEmpty(TechPersistence.LoadFromFile(techPath).Layers);
    }

    /// <summary>The other half of the same rule: with NO technology at all, the reconciliation has
    /// nothing to compare against and reports nothing as new — so the verb says so rather than
    /// letting the caller find out when the layout opens unnamed.</summary>
    [Fact]
    public void ImportPart_SaysWhenNoTechnologyResolved()
    {
        string ws = Path.Combine(Dir("notech"), "Amp");
        Assert.Equal(0, RunCli("new", "workspace", ws, "--tech", "none").ExitCode);

        var run = RunCli("import", "part",
            Path.Combine(RepoRoot(), "testdata", "component-samples", "widget9"),
            "--into", ws, "--variant", ".kicad_sym");

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("No technology resolved", run.StdErr, StringComparison.Ordinal);
    }

    // ── --json ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>R-aut0-7 and the brief's §1: <c>--json</c> applies to all three verbs, and the PATHS
    /// created are the result — a caller's next step is to read or rewrite one of them.</summary>
    [Fact]
    public void EveryAuthoringVerbAnswersJsonWithThePathsItCreated()
    {
        string ws = Path.Combine(Dir("json"), "Amp");

        var wsRun = RunCli("--json", "new", "workspace", ws);
        Assert.Equal(0, wsRun.ExitCode);
        Assert.Contains("\"verb\": \"new workspace\"", wsRun.StdOut, StringComparison.Ordinal);
        Assert.Contains("\"kind\": \"cws\"", wsRun.StdOut, StringComparison.Ordinal);
        Assert.Contains("\"kind\": \"ctech\"", wsRun.StdOut, StringComparison.Ordinal);

        var cellRun = RunCli("--json", "new", "cell", ws, "Stage1", "--views", "schematic,symbol");
        Assert.Equal(0, cellRun.ExitCode);
        Assert.Contains("\"kind\": \"cell\"", cellRun.StdOut, StringComparison.Ordinal);
        Assert.Contains("\"kind\": \"schematic\"", cellRun.StdOut, StringComparison.Ordinal);
        Assert.Contains("\"kind\": \"symbol\"", cellRun.StdOut, StringComparison.Ordinal);

        // R-aut1-3: a FAILED run still emits a document, so a caller never has to tell "no output"
        // apart from "output I could not parse".
        var failed = RunCli("--json", "new", "workspace", ws);
        Assert.Equal(1, failed.ExitCode);
        Assert.Contains("\"status\": \"failed\"", failed.StdOut, StringComparison.Ordinal);
        Assert.Contains("new.refused", failed.StdOut, StringComparison.Ordinal);
    }

    // ── the fixture circuit ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Port 1 — R — port 2, wired on the schematic grid. Deliberately built out of the same
    /// <c>SchematicEditModel</c> a hand-written <c>.csch</c> deserializes into, so what this proves is
    /// that a document authored outside the application elaborates and runs.
    /// </summary>
    private static void BuildSeriesResistor(SchematicEditModel model, double resistanceOhms)
    {
        // A Term at (x, y) puts "+" at (x, y-200) and "−" at (x, y+200); a resistor at (x, y) with no
        // rotation puts pin0 and pin1 at the same two offsets. So the whole circuit is two columns of
        // pins on y = 0 and y = 400, which is why the wires below are single segments.
        model.Components.Add(Term("P1", 100, 200, num: 1));
        model.Components.Add(Ground("GP1", 100, 400));
        model.Components.Add(new EditableComponent
        {
            InstanceName = "R1",
            Symbol       = SymbolKind.Resistor,
            X = 300, Y = 200,
            Parameters   = { new EditableParameter
                { Name = "R", Expression = resistanceOhms.ToString(System.Globalization.CultureInfo.InvariantCulture) } },
        });
        model.Components.Add(Term("P2", 500, 600, num: 2));
        model.Components.Add(Ground("GP2", 500, 800));

        model.Wires.Add(Wire((100, 0), (300, 0)));       // P1."+" → R1.pin0
        model.Wires.Add(Wire((300, 400), (500, 400)));   // R1.pin1 → P2."+"

        static EditableComponent Term(string name, double x, double y, int num)
        {
            var c = new EditableComponent { InstanceName = name, Symbol = SymbolKind.Term, X = x, Y = y };
            c.Parameters.Add(new EditableParameter { Name = "Num", Expression = num.ToString() });
            c.Parameters.Add(new EditableParameter { Name = "Z",   Expression = "50" });
            return c;
        }

        static EditableComponent Ground(string name, double x, double y)
            => new() { InstanceName = name, Symbol = SymbolKind.Ground, X = x, Y = y };

        static EditableWire Wire(params (double X, double Y)[] points)
        {
            var w = new EditableWire();
            w.Points.AddRange(points);
            return w;
        }
    }

    /// <summary>|S21| out of the first data line of a Touchstone written in the CLI's own default
    /// format. Read here rather than through <c>TouchstoneIO</c> on purpose: the point of the test is
    /// that the FILE is right, and reading it back with the writer's own partner would hide a format
    /// disagreement the whole chain depends on.</summary>
    private static double FirstS21Magnitude(string snpPath)
    {
        string? optionLine = null;
        foreach (string raw in File.ReadLines(snpPath))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('!')) continue;
            if (line.StartsWith('#')) { optionLine = line.ToUpperInvariant(); continue; }

            var f = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            // freq S11a S11b S21a S21b S12a S12b S22a S22b — S21 is fields 3 and 4.
            double a = double.Parse(f[3], System.Globalization.CultureInfo.InvariantCulture);
            double b = double.Parse(f[4], System.Globalization.CultureInfo.InvariantCulture);
            return optionLine is not null && optionLine.Contains(" MA", StringComparison.Ordinal)
                ? a
                : optionLine is not null && optionLine.Contains(" DB", StringComparison.Ordinal)
                    ? Math.Pow(10, a / 20.0)
                    : Math.Sqrt(a * a + b * b);
        }
        throw new InvalidOperationException($"{snpPath} holds no data line.");
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────────

    private static IEnumerable<ProjectTreeNode> Flatten(ProjectTreeNode node)
    {
        yield return node;
        foreach (var c in node.Children)
            foreach (var d in Flatten(c))
                yield return d;
    }

    private void AssertSameBytes(string expectedPath, string actualPath)
    {
        Assert.True(File.Exists(actualPath), $"missing: {actualPath}");
        byte[] expected = File.ReadAllBytes(expectedPath), actual = File.ReadAllBytes(actualPath);
        if (!expected.AsSpan().SequenceEqual(actual))
        {
            _output.WriteLine($"expected ({expectedPath}):\n{File.ReadAllText(expectedPath)}");
            _output.WriteLine($"actual   ({actualPath}):\n{File.ReadAllText(actualPath)}");
        }
        Assert.Equal(expected, actual);
    }

    /// <summary>Every file under both roots, by relative path, and every one byte-identical — the
    /// folder STRUCTURE is part of what a cell folder is, so comparing only the files a test happens
    /// to name would miss a missing sub-folder entirely.</summary>
    private void AssertSameTree(string expectedRoot, string actualRoot)
    {
        string[] Rel(string root) =>
            [.. Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories)
                .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/'))
                .OrderBy(p => p, StringComparer.Ordinal)];

        Assert.Equal(Rel(expectedRoot), Rel(actualRoot));

        foreach (string rel in Rel(expectedRoot))
        {
            string e = Path.Combine(expectedRoot, rel);
            if (Directory.Exists(e)) continue;
            AssertSameBytes(e, Path.Combine(actualRoot, rel));
        }
    }

    /// <summary>Line and block comments removed, so a source scan cannot be satisfied by prose. String
    /// literals are left alone: none of the symbols scanned for appears in one.</summary>
    private static string StripComments(string code)
        => Regex.Replace(Regex.Replace(code, @"/\*.*?\*/", "", RegexOptions.Singleline), @"//[^\n]*", "");

    private (int ExitCode, string StdOut, string StdErr) RunCli(params string[] args)
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

        // The same throwaway per-user state the in-process half is redirected onto — see StateDir.
        // An unset variable here would have the child read the developer's own preferences, and the
        // byte-for-byte comparisons would then be comparing two different installations.
        psi.Environment[CircuitRF.Design.UserStateDirectory.EnvironmentVariable] = StateDir;

        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }

    /// <summary>The built CLI, in whichever configuration this test assembly was built in — read from
    /// the <c>CliDir</c> assembly metadata the `.csproj` stamps, so a Release test run cannot silently
    /// exercise a stale Debug CLI. EmCliVerbTests records why this is not `dotnet run`.</summary>
    private static string CliDll()
    {
        string cliDir = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(AuthoringCliVerbTests).Assembly)
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

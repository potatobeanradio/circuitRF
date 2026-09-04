using System.Text.Json.Nodes;
using CircuitRF.Design.Layout;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

// ──────────────────────────────────────────────────────────────────────────────
//  TM2 — a cell moved in a workspace or library NOBODY here has open, and the
//  forwarding record is what finds it again
//  (docs/sonnet-briefs/brief-tree-move-2-moves-across-a-shared-library.md §7).
//
//  Headless, over real temp directories. The mechanism IS a rule about the
//  filesystem — an in-memory double would agree with itself about the thing under
//  test — and the whole point of the feature is the case where the referring
//  workspace is CLOSED, which is exactly what a temp directory reproduces.
//
//  Gate 3 (existence wins) and gate 6 (adoption is explicit) are the two that
//  matter most. The first is what makes the mechanism safe rather than a silent
//  reroute; the second is the half that would be lost to a convenience change.
// ──────────────────────────────────────────────────────────────────────────────

[Collection(CellStatGlobalsCollection.Name)]
public sealed class TreeMoveRedirectTests : IDisposable
{
    private readonly string _root;
    private readonly string _ws;      // "U" — the user's own workspace
    private readonly string _lib;     // "L" — a referenced LIBRARY, with no .cws of its own

    public TreeMoveRedirectTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf_tm2_" + Guid.NewGuid().ToString("N")[..8]);
        _ws   = Path.Combine(_root, "workspaceU");
        _lib  = Path.Combine(_root, "StdParts");
        Directory.CreateDirectory(_ws);
        Directory.CreateDirectory(_lib);
        WriteCws(_ws);
        Invalidate();
    }

    public void Dispose()
    {
        Invalidate();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static void Invalidate()
    {
        CellSymbolResolver.InvalidateAll();
        WorkspaceRootFinder.InvalidateCache();
    }

    // ── Fixture ───────────────────────────────────────────────────────────────

    private static void WriteCws(string root, Action<CwsFile>? edit = null)
    {
        var cws = new CwsFile();
        edit?.Invoke(cws);
        WorkspacePersistence.SaveToFile(Path.Combine(root, ".cws"), cws);
        Invalidate();
    }

    /// <summary>A cell folder with a real primary symbol, so a resolution that succeeds actually
    /// carries pins rather than landing on <c>PrimaryMissing</c>.</summary>
    private static string CellWithSymbol(string parentDir, string relativePath, int pins = 2)
    {
        string parent = Path.Combine(parentDir, Path.GetDirectoryName(relativePath) ?? "");
        Directory.CreateDirectory(parent);
        string cellDir = CellFolder.CreateCellFolder(parent, Path.GetFileName(relativePath));

        string symDir = CellFolder.SubFolderPath(cellDir, ViewType.Symbol);
        Directory.CreateDirectory(symDir);

        var pinList = new List<SymbolPin>();
        for (int i = 0; i < pins; i++) pinList.Add(new SymbolPin(0, i * 100, i + 1, "p" + (i + 1)));
        var sym = new Symbol(primitives: [], pins: pinList, portCount: pins);
        SymbolPersistence.SaveToFile(Path.Combine(symDir, Path.GetFileName(cellDir) + ".csym"), sym);
        return cellDir;
    }

    /// <summary>A schematic in <paramref name="ownerCellDir"/> placing each target, with the
    /// reference written by the SAME producer the editor uses.</summary>
    private static (string Path, SchematicEditModel Model) SchematicPlacing(
        string ownerCellDir, params string[] targetCellDirs)
    {
        string schDir = CellFolder.SubFolderPath(ownerCellDir, ViewType.Schematic);
        Directory.CreateDirectory(schDir);
        string path = Path.Combine(schDir, Path.GetFileName(ownerCellDir) + ".csch");

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
        return (path, model);
    }

    /// <summary>Re-reads a schematic from disk the way opening it does, so a scan runs against what
    /// the file actually says rather than against an in-memory object the test just built.</summary>
    private static SchematicEditModel Reopen(string cschPath)
    {
        Invalidate();
        var (model, _, _) = SchematicPersistence.LoadFromFile(cschPath);
        return model;
    }

    /// <summary>The move itself, as the librarian's machine performs it: rename the folder, then
    /// append the forwarding record. The referring workspace is not open and is not touched.</summary>
    private static void MoveAndRecord(string root, string fromAbs, string toAbs)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(toAbs)!);
        Directory.Move(fromAbs, toAbs);
        Assert.True(MoveRedirects.Append(
            root, MoveRedirects.ToRootRelative(root, fromAbs)!,
                  MoveRedirects.ToRootRelative(root, toAbs)!, out string? err), err);
        Invalidate();
    }

    private static string StoredRefIn(string cschPath, int index = 0) =>
        JsonNode.Parse(File.ReadAllText(cschPath))!["Components"]!.AsArray()[index]!["CellRef"]!.GetValue<string>();

    private static string Norm(string p) => WorkspaceMove.Normalize(p);

    // ── 1. The core case ──────────────────────────────────────────────────────

    [Fact]
    public void Gate1_MovedInAClosedLibrary_ResolvesThroughTheRecord_AndReportsOncePerCell()
    {
        var amp = CellWithSymbol(_lib, "Amp", pins: 3);
        var top = CellWithSymbol(_ws, "Top");
        // Three instances of the one cell — R-tm2-12's "forty instances of one moved cell is one
        // problem" is the assertion at the bottom, not a remark.
        var (csch, _) = SchematicPlacing(top, amp, amp, amp);

        MoveAndRecord(_lib, amp, Path.Combine(_lib, "rf", "Amp"));

        var model  = Reopen(csch);
        var report = Assert.Single(CellMoveWatch.Scan(model, _ws));

        Assert.Equal("Amp", report.CellName);
        Assert.Equal("Amp", report.Redirect.From);
        Assert.Equal("rf/Amp", report.Redirect.To);
        Assert.Equal("StdParts", report.Redirect.RootName);
        Assert.Equal(3, report.InstanceNames.Count);

        // It resolves, and it renders its REAL pins — the whole reason a redirect is worth having.
        var res = CellSymbolResolver.Resolve(
            model.Components[0].CellRef!, Path.GetDirectoryName(csch), _ws);
        Assert.Equal(CellSymbolState.Resolved, res.State);
        Assert.Equal(3, res.Symbol!.Pins.Count);
        Assert.NotNull(res.Redirect);
        Assert.Equal(Norm(Path.Combine(_lib, "rf", "Amp")), Norm(res.Redirect!.ResolvedDir));

        // And every component carries the runtime mark the canvas and the Properties panel read.
        Assert.All(model.Components, c => Assert.NotNull(c.MovedRedirect));
    }

    // ── 2. The ws:// variant ──────────────────────────────────────────────────

    [Fact]
    public void Gate2_MovedInAReferencedWorkspace_ResolvesThroughTheRecord()
    {
        // W is a real workspace, referenced from U by alias. The remainder in a ws:// reference is
        // W's OWN relative spelling, which is exactly what the move invalidates.
        string w = Path.Combine(_root, "workspaceW");
        Directory.CreateDirectory(w);
        WriteCws(w);
        WriteCws(_ws, c => c.ReferencedWorkspaces =
            [new CwsWorkspaceRef { Alias = "W", Path = Path.Combine(w, ".cws") }]);

        var amp = CellWithSymbol(w, "Amp");
        var top = CellWithSymbol(_ws, "Top");
        var (csch, _) = SchematicPlacing(top, amp);

        string stored = StoredRefIn(csch);
        Assert.StartsWith("ws://W/", stored, StringComparison.Ordinal);

        MoveAndRecord(w, amp, Path.Combine(w, "rf", "Amp"));

        var model  = Reopen(csch);
        var report = Assert.Single(CellMoveWatch.Scan(model, _ws));
        Assert.Equal("rf/Amp", report.Redirect.To);
        Assert.Equal("ws://W/rf/Amp", report.NewCellRef);

        Assert.Equal(CellSymbolState.Resolved,
            CellSymbolResolver.Resolve(stored, Path.GetDirectoryName(csch), _ws).State);
    }

    // ── 3. Existence wins ─────────────────────────────────────────────────────

    [Fact]
    public void Gate3_ANewCellAtTheOldPathWins_AndNoRedirectFires()
    {
        var amp = CellWithSymbol(_lib, "Amp", pins: 3);
        var top = CellWithSymbol(_ws, "Top");
        var (csch, _) = SchematicPlacing(top, amp);

        MoveAndRecord(_lib, amp, Path.Combine(_lib, "rf", "Amp"));

        // Somebody creates a DIFFERENT cell at the old path. R-tm2-8: the new cell wins, the redirect
        // never fires, and the reference means what it says. A redirect consulted first would
        // silently reroute a live reference to a different cell.
        var replacement = CellWithSymbol(_lib, "Amp", pins: 7);
        Invalidate();

        var model = Reopen(csch);
        Assert.Empty(CellMoveWatch.Scan(model, _ws));

        var res = CellSymbolResolver.Resolve(
            model.Components[0].CellRef!, Path.GetDirectoryName(csch), _ws);
        Assert.Equal(CellSymbolState.Resolved, res.State);
        Assert.Null(res.Redirect);
        Assert.Equal(7, res.Symbol!.Pins.Count);          // the NEW cell, not the moved one
        Assert.Equal(Norm(replacement),
            Norm(ExternalCellRef.ResolveCellDir(model.Components[0].CellRef, Path.GetDirectoryName(csch))!));
    }

    // ── 4. Chains, and a cycle ────────────────────────────────────────────────

    [Fact]
    public void Gate4_TwoSuccessiveMovesResolveInOneCall()
    {
        var amp = CellWithSymbol(_lib, "Amp");
        var top = CellWithSymbol(_ws, "Top");
        var (csch, _) = SchematicPlacing(top, amp);

        MoveAndRecord(_lib, amp, Path.Combine(_lib, "rf", "Amp"));
        MoveAndRecord(_lib, Path.Combine(_lib, "rf", "Amp"), Path.Combine(_lib, "rf", "pa", "Amp"));

        Assert.Equal(2, MoveRedirects.Read(_lib).Count);

        var model  = Reopen(csch);
        var report = Assert.Single(CellMoveWatch.Scan(model, _ws));
        Assert.Equal("Amp",          report.Redirect.From);
        Assert.Equal("rf/pa/Amp",    report.Redirect.To);
        Assert.Equal(CellSymbolState.Resolved,
            CellSymbolResolver.Resolve(model.Components[0].CellRef!, Path.GetDirectoryName(csch), _ws).State);
    }

    [Fact]
    public void Gate4_AHandEditedCycleProducesNotFound_AndTerminates()
    {
        var amp = CellWithSymbol(_lib, "Amp");
        var top = CellWithSymbol(_ws, "Top");
        var (csch, _) = SchematicPlacing(top, amp);

        Directory.Delete(amp, recursive: true);
        // A .cmoves nobody could have produced by moving anything: A → B → A, and neither exists.
        Assert.True(MoveRedirects.Append(_lib, "Amp", "rf/Amp", out _));
        Assert.True(MoveRedirects.Append(_lib, "rf/Amp", "Amp", out _));
        Invalidate();

        var model = Reopen(csch);
        Assert.Empty(CellMoveWatch.Scan(model, _ws));
        Assert.Equal(CellSymbolState.NotFound,
            CellSymbolResolver.Resolve(model.Components[0].CellRef!, Path.GetDirectoryName(csch), _ws).State);
    }

    // ── 5. Longest prefix — one record covers a whole moved folder ────────────

    [Fact]
    public void Gate5_MovingAFolderOfThreeCellsWritesOneRecord_AndAllThreeResolve()
    {
        var a = CellWithSymbol(_lib, Path.Combine("passives", "R0402"));
        var b = CellWithSymbol(_lib, Path.Combine("passives", "C0402"));
        var c = CellWithSymbol(_lib, Path.Combine("passives", "L0402"));
        var top = CellWithSymbol(_ws, "Top");
        var (csch, _) = SchematicPlacing(top, a, b, c);

        MoveAndRecord(_lib, Path.Combine(_lib, "passives"), Path.Combine(_lib, "smd", "passives"));

        Assert.Single(MoveRedirects.Read(_lib));           // ONE record, not three

        var model   = Reopen(csch);
        var reports = CellMoveWatch.Scan(model, _ws);
        Assert.Equal(3, reports.Count);                    // three cells, one problem each
        // The hit reports where THIS cell was and is — not the record's own From/To, which name the
        // moved folder. One record, three cells, three honest per-cell sentences.
        Assert.All(reports, r => Assert.StartsWith("passives/", r.Redirect.From, StringComparison.Ordinal));
        Assert.All(reports, r => Assert.StartsWith("smd/passives/", r.Redirect.To, StringComparison.Ordinal));

        string schDir = Path.GetDirectoryName(csch)!;
        foreach (var comp in model.Components)
            Assert.Equal(CellSymbolState.Resolved,
                CellSymbolResolver.Resolve(comp.CellRef!, schDir, _ws).State);
    }

    [Fact]
    public void Gate5_APrefixMatchNeverCrossesASegmentBoundary()
    {
        // "cells/Amp" moving must not capture "cells/AmpX" — TM1's own near-miss, arriving here by a
        // different door.
        var amp  = CellWithSymbol(_lib, Path.Combine("cells", "Amp"));
        var ampX = CellWithSymbol(_lib, Path.Combine("cells", "AmpX"));
        var top  = CellWithSymbol(_ws, "Top");
        var (csch, _) = SchematicPlacing(top, amp, ampX);

        MoveAndRecord(_lib, amp, Path.Combine(_lib, "rf", "Amp"));

        var model   = Reopen(csch);
        var reports = CellMoveWatch.Scan(model, _ws);
        Assert.Single(reports);
        Assert.Equal("Amp", reports[0].CellName);

        // AmpX never moved: it resolves directly and carries no mark.
        Assert.Null(model.Components[1].MovedRedirect);
    }

    // ── 6. Adoption is explicit ───────────────────────────────────────────────

    [Fact]
    public void Gate6_OpenRenderEditAndSaveLeaveTheStoredRefByteIdentical()
    {
        var amp = CellWithSymbol(_lib, "Amp");
        var top = CellWithSymbol(_ws, "Top");
        var (csch, _) = SchematicPlacing(top, amp);

        string before = StoredRefIn(csch);
        MoveAndRecord(_lib, amp, Path.Combine(_lib, "rf", "Amp"));

        var model = Reopen(csch);
        CellMoveWatch.Scan(model, _ws);         // open + report
        model.BuildRenderModel();               // render
        var wire = new EditableWire();                                            // an unrelated edit
        wire.Points.Add((0, 0));
        wire.Points.Add((100, 0));
        model.Wires.Add(wire);
        SchematicPersistence.SaveToFile(csch, model);                             // save

        Assert.Equal(before, StoredRefIn(csch));
    }

    [Fact]
    public void Gate6_UpdateReferencesRewritesIt_AndTheReportThenGoesQuiet()
    {
        var amp = CellWithSymbol(_lib, "Amp");
        var top = CellWithSymbol(_ws, "Top");
        var (csch, _) = SchematicPlacing(top, amp);

        string before = StoredRefIn(csch);
        MoveAndRecord(_lib, amp, Path.Combine(_lib, "rf", "Amp"));

        var model  = Reopen(csch);
        var report = Assert.Single(CellMoveWatch.Scan(model, _ws));
        Assert.NotNull(report.NewCellRef);
        Assert.NotEqual(before, report.NewCellRef);

        // The gesture, through the same producing rule a placement uses.
        model.Components[0].CellRef       = report.NewCellRef!;
        model.Components[0].MovedRedirect = null;
        SchematicPersistence.SaveToFile(csch, model);

        var after = Reopen(csch);
        Assert.Empty(CellMoveWatch.Scan(after, _ws));
        Assert.Equal(CellSymbolState.Resolved,
            CellSymbolResolver.Resolve(after.Components[0].CellRef!, Path.GetDirectoryName(csch), _ws).State);
        Assert.Null(
            CellSymbolResolver.Resolve(after.Components[0].CellRef!, Path.GetDirectoryName(csch), _ws).Redirect);
    }

    // ── 8. No new cost on the common path ─────────────────────────────────────

    [Fact]
    public void Gate8_ADesignWithNoMovedReferencesPaysNothingExtra()
    {
        var amp = CellWithSymbol(_lib, "Amp");
        var top = CellWithSymbol(_ws, "Top");
        var (csch, _) = SchematicPlacing(top, amp);

        var model  = Reopen(csch);
        string schDir = Path.GetDirectoryName(csch)!;
        string cellRef = model.Components[0].CellRef!;

        // Warm: the first resolve of a session pays for the whole chain either way, and it is the
        // STEADY state — a re-resolve per edit, which BuildRenderModel does on every model change —
        // that R-tm2-8 must not have made more expensive.
        CellSymbolResolver.Resolve(cellRef, schDir, _ws);

        CellStat.ResetCalls();
        for (int i = 0; i < 20; i++) CellSymbolResolver.Resolve(cellRef, schDir, _ws);
        long warm = CellStat.Calls;

        // Zero: every question the redirect could ask is behind an existence check that hits the
        // CellStat cache, and the record itself is never read because step 2 short-circuited.
        Assert.Equal(0, warm);

        // And the cold count is the pre-TM2 count: the existence question ResolveCellDir now asks is
        // the SAME question CellSymbolResolver asked immediately afterwards, so it is one call in
        // both worlds rather than two.
        Invalidate();
        CellStat.ResetCalls();
        CellSymbolResolver.Resolve(cellRef, schDir, _ws);
        long cold = CellStat.Calls;
        Assert.Equal(4, cold);
    }

    // ── 9. A move whose record cannot be written is refused ───────────────────

    [Fact]
    public void Gate9_CanRecordRefusesWhenTheRecordCannotBeWritten()
    {
        Assert.True(MoveRedirects.CanRecord(_ws, out _));
        Assert.False(MoveRedirects.CanRecord(Path.Combine(_root, "no-such-directory"), out string? err));
        Assert.False(string.IsNullOrEmpty(err));

        // The probe leaves nothing behind — in particular it does not create an empty .cmoves for a
        // move that is about to be refused for some other reason.
        Assert.False(File.Exists(MoveRedirects.PathFor(_ws)));
        Assert.Empty(Directory.GetFiles(_ws, "*.probe"));
    }

    // ── The layout half ───────────────────────────────────────────────────────

    [Fact]
    public void ALayoutInstanceResolvesThroughTheRecordAndIsReportedOnce()
    {
        var amp = CellWithSymbol(_lib, "Amp");
        var top = CellWithSymbol(_ws, "Top");

        string layDir = CellFolder.SubFolderPath(top, ViewType.Layout);
        Directory.CreateDirectory(layDir);
        string clay = Path.Combine(layDir, "Top.clay");

        var view = new LayoutView { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000 };
        for (int i = 0; i < 2; i++)
            view.Instances.Add(new LayoutInstance
            {
                CellRef = ExternalCellRef.MakeCellRef(layDir, amp), X = 0, Y = i * 1000, Mag = 1.0,
            });
        LayoutPersistence.SaveToFile(clay, view);

        MoveAndRecord(_lib, amp, Path.Combine(_lib, "rf", "Amp"));

        var reloaded = LayoutPersistence.LoadFromFile(clay);
        var report   = Assert.Single(CellMoveWatch.Scan(reloaded, layDir, _ws));
        Assert.Equal(2, report.InstanceNames.Count);

        Assert.Equal(2, CellMoveWatch.UpdateReferences(reloaded.Instances, [report]));
        Assert.Empty(CellMoveWatch.Scan(reloaded, layDir, _ws));
    }

    // ── 7. The headless path gets it for free ─────────────────────────────────

    /// <summary>
    /// TM2 gate 7, and §6's table row that justifies the whole design decision: <b>a headless run
    /// resolves a moved reference with no code of its own</b>, because the redirect lives in
    /// <see cref="ExternalCellRef.ResolveCellDir"/> and <c>src/Cli</c> already resolves through it.
    ///
    /// <para><b>The brief names <c>circuitrf sparam</c>; this uses <c>circuitrf convert</c>, and the
    /// substitution is a finding rather than a shortcut.</b> <c>sparam</c> takes a <c>.cnl</c>, which
    /// is a FLAT netlist — it carries no cell reference of any kind, so there is nothing there for a
    /// redirect to repair and the gate as literally written cannot be built. The verbs that do resolve
    /// a stored <c>CellRef</c> headlessly are <c>convert</c> (through
    /// <c>GdsiiExport</c>/<c>DxfExport</c>/<c>PcbExport</c>) and <c>em</c> (through
    /// <c>CellLayoutResolver</c>). This drives the first, which makes the same claim about the same
    /// code and additionally proves the ARTWORK came out — an exit-code check would pass just as
    /// happily on an export that silently dropped the placement.</para>
    ///
    /// <para>Byte identity is the assertion, over everything but the WRITE TIMESTAMP —
    /// <c>GdsiiWriter</c> stamps <c>DateTime.UtcNow</c> into every <c>BGNLIB</c> and <c>BGNSTR</c>
    /// record, so those payloads are zeroed on both sides before the comparison and every other byte
    /// has to match. That is the same bargain <c>EmCliVerbTests</c> strikes with the <c>.sNp</c>
    /// provenance line, and for the same reason: the file carries the stamp by design.</para>
    /// </summary>
    [Fact]
    public void Gate7_AHeadlessExportResolvesThroughTheRecord_AndWritesTheSameBytes()
    {
        string repo = RepoRoot();
        Assert.False(string.IsNullOrEmpty(repo), "could not locate the repository root");

        // A technology and a workspace default, so `convert` resolves the tech by the same walk-up
        // the GUI uses rather than by anything this test arranges.
        TechPersistence.SaveToFile(Path.Combine(_ws, "pcb.ctech"), StarterTechnologies.Pcb2Layer());
        WorkspacePersistence.SaveToFile(
            Path.Combine(_ws, ".cws"), new CwsFile { DefaultTechRef = "pcb.ctech" });
        Invalidate();

        // The library cell carries real artwork, so an export that lost the placement is visible as a
        // byte difference rather than as an equally-empty file.
        string amp = CellWithSymbol(_lib, "Amp");
        string ampLayoutDir = CellFolder.SubFolderPath(amp, ViewType.Layout);
        Directory.CreateDirectory(ampLayoutDir);
        var ampView = new LayoutView { DbuPerMicron = 1000 };
        ampView.Shapes.Add(new RectShape
        { Layer = new(1, 0), X1 = 0, Y1 = 0, X2 = 5_000_000, Y2 = 1_000_000 });
        LayoutPersistence.SaveToFile(Path.Combine(ampLayoutDir, "Amp.clay"), ampView);

        string top = CellWithSymbol(_ws, "Top");
        string topLayoutDir = CellFolder.SubFolderPath(top, ViewType.Layout);
        Directory.CreateDirectory(topLayoutDir);
        var topView = new LayoutView { DbuPerMicron = 1000 };
        topView.Shapes.Add(new RectShape
        { Layer = new(1, 0), X1 = 0, Y1 = 0, X2 = 20_000_000, Y2 = 2_900_000 });
        topView.Instances.Add(new LayoutInstance
        { CellRef = ExternalCellRef.MakeCellRef(topLayoutDir, amp), X = 0, Y = 4_000_000, Mag = 1.0 });
        string topClay = Path.Combine(topLayoutDir, "Top.clay");
        LayoutPersistence.SaveToFile(topClay, topView);

        string before = Path.Combine(_root, "before.gds");
        var runBefore = RunCli(repo, "convert", topClay, "-o", before);
        Assert.True(runBefore.ExitCode == 0, runBefore.StdErr + runBefore.StdOut);
        Assert.True(File.Exists(before), $"convert wrote nothing: {runBefore.StdErr}");
        byte[] expected = WithoutGdsTimestamps(File.ReadAllBytes(before));

        // The librarian tidies up, in a library this process has never opened.
        MoveAndRecord(_lib, amp, Path.Combine(_lib, "rf", "Amp"));

        string after = Path.Combine(_root, "after.gds");
        var runAfter = RunCli(repo, "convert", topClay, "-o", after);
        Assert.True(runAfter.ExitCode == 0, runAfter.StdErr + runAfter.StdOut);
        Assert.True(File.Exists(after), $"convert wrote nothing after the move: {runAfter.StdErr}");

        Assert.Equal(expected, WithoutGdsTimestamps(File.ReadAllBytes(after)));

        // And the control: with the forwarding record gone, the same export is NOT the same export —
        // which is what makes the equality above evidence that the record did the work, rather than
        // evidence that the placement never mattered.
        File.Delete(MoveRedirects.PathFor(_lib));
        Invalidate();
        string without = Path.Combine(_root, "without.gds");
        RunCli(repo, "convert", topClay, "-o", without);
        Assert.NotEqual(expected, WithoutGdsTimestamps(File.ReadAllBytes(without)));
    }

    /// <summary>
    /// The GDSII with its two time-stamped record payloads zeroed. A GDSII record is
    /// <c>[uint16 length][uint8 type][uint8 dataType][payload]</c>; <c>BGNLIB</c> (0x01) and
    /// <c>BGNSTR</c> (0x05) each carry twelve int16 date/time fields written from
    /// <c>DateTime.UtcNow</c>, and nothing else in the file depends on the clock. Zeroing exactly
    /// those two leaves every structure name, layer, coordinate and reference in the comparison.
    /// </summary>
    private static byte[] WithoutGdsTimestamps(byte[] gds)
    {
        var copy = (byte[])gds.Clone();
        int i = 0;
        while (i + 4 <= copy.Length)
        {
            int length = (copy[i] << 8) | copy[i + 1];       // GDSII is big-endian
            if (length < 4 || i + length > copy.Length) break;
            if (copy[i + 2] is 0x01 or 0x05)
                Array.Clear(copy, i + 4, length - 4);
            i += length;
        }
        return copy;
    }

    /// <summary>Runs the BUILT <c>CircuitRF.Cli.dll</c> — never <c>dotnet run --project src/Cli</c>,
    /// which starts a nested MSBuild inside a <c>dotnet test</c> that already holds this repository's
    /// build locks and does not finish. Both pipes are drained concurrently, for the reason
    /// <c>EmCliVerbTests.RunCli</c> records: reading one to the end first deadlocks as soon as the
    /// child fills the other's buffer.</summary>
    private static (int ExitCode, string StdOut, string StdErr) RunCli(string repo, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("dotnet")
        {
            WorkingDirectory       = repo,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        psi.ArgumentList.Add(CliDll());
        foreach (string a in args) psi.ArgumentList.Add(a);

        using var proc = System.Diagnostics.Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }

    private static string CliDll()
    {
        string cliDir = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(
                typeof(TreeMoveRedirectTests).Assembly)
            .First(a => a.Key == "CliDir").Value!;
        string path = Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll"));
        Assert.True(File.Exists(path), $"the CLI was not built beside these tests: {path}");
        return path;
    }

    private static string RepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is { Length: > 0 } && !File.Exists(Path.Combine(dir, "circuitRF.slnx")))
            dir = Path.GetDirectoryName(dir) ?? "";
        return dir;
    }

    // ── The record is written where a LIBRARY can hold one ────────────────────

    [Fact]
    public void RootAbove_FindsALibraryThatIsNotAWorkspace_AndStopsAtAWorkspaceRoot()
    {
        var amp = CellWithSymbol(_lib, Path.Combine("rf", "Amp"));
        Assert.True(MoveRedirects.Append(_lib, "x", "y", out _));
        Invalidate();

        // The library has no .cws at all — WorkspaceRootFinder would answer null here, which is
        // exactly why the walk-up is its own.
        Assert.Null(WorkspaceRootFinder.WorkspaceDirOf(amp));
        Assert.Equal(Norm(_lib), Norm(MoveRedirects.RootAbove(amp)!));

        // A path inside a workspace that has no record of its own answers null rather than escaping
        // upward into a root that owns a different tree.
        var inWs = CellWithSymbol(_ws, "Local");
        Assert.Null(MoveRedirects.RootAbove(inWs));
    }
}

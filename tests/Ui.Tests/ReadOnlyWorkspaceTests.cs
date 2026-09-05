using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.IO;
using System.Linq;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.PCells;
using CircuitRF.Ui.Messages;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

// ──────────────────────────────────────────────────────────────────────────────
//  SL2 — read-only workspaces
//  (docs/sonnet-briefs/brief-shared-library-2-read-only-workspaces.md §5).
//
//  The gate is in two halves, deliberately, because of a platform problem the brief
//  names rather than discovers:
//
//   • Almost every test here drives the injectable predicate
//     WorkspaceWritability.WritabilityProbe. Those are the tests that protect the
//     BEHAVIOUR (R-sl2-5 … R-sl2-13) and they run identically on Windows, macOS and
//     Linux.
//   • ONE test drives the real filesystem, asserting the probe itself gives the right
//     answer against a directory the test actually made unwritable. Where the platform
//     cannot express that, it SKIPS with a reason (UnwritableDirFactAttribute) — a gate
//     that silently passes on one platform is not a gate.
//
//  The probe memoises per directory (R-sl2-3), so every test here restores the seam and
//  drops the memo in Dispose. Without that, one test's answer would be another test's
//  starting condition — and the memo is dropped through WorkspaceRootFinder.Invalidate-
//  Cache, which is exactly where SL2 hung it.
// ──────────────────────────────────────────────────────────────────────────────

[Collection(CellStatGlobalsCollection.Name)]
public sealed class ReadOnlyWorkspaceTests : IDisposable
{
    private readonly string _root;
    private readonly string _library;   // the read-only share
    private readonly string _mine;      // the user's own workspace

    public ReadOnlyWorkspaceTests()
    {
        _root    = Path.Combine(Path.GetTempPath(), "crf_sl2_" + Guid.NewGuid().ToString("N")[..8]);
        _library = Path.Combine(_root, "stdlib");
        _mine    = Path.Combine(_root, "myproject");
        Directory.CreateDirectory(_library);
        Directory.CreateDirectory(_mine);
        WorkspacePersistence.SaveToFile(Path.Combine(_library, ".cws"), new CwsFile());
        WorkspacePersistence.SaveToFile(Path.Combine(_mine, ".cws"), new CwsFile());
        WorkspaceRootFinder.InvalidateCache();
    }

    public void Dispose()
    {
        WorkspaceWritability.WritabilityProbe = null;   // also drops the memo
        WorkspaceRootFinder.InvalidateCache();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>Everything under <paramref name="readOnlyRoot"/> answers "read-only"; everything else
    /// is writable. Containment by path prefix, which is what a share actually looks like.</summary>
    private void MakeReadOnly(string readOnlyRoot)
    {
        string ro = Path.TrimEndingDirectorySeparator(Path.GetFullPath(readOnlyRoot));
        WorkspaceWritability.WritabilityProbe = dir =>
            !dir.StartsWith(ro, StringComparison.OrdinalIgnoreCase);
    }

    // ── R-sl2-1/-2/-3: discovering it ────────────────────────────────────────

    /// <summary>
    /// R-sl2-3: the answer is memoised per directory and dropped by the SAME call that drops the
    /// other two per-workspace memos. The point is not the caching — it is that there is exactly one
    /// invalidation, so a third memo cannot be the one somebody forgets.
    /// </summary>
    [Fact]
    public void TheAnswerIsMemoised_AndDroppedByWorkspaceRootFinderInvalidateCache()
    {
        int probes = 0;
        WorkspaceWritability.WritabilityProbe = _ => { probes++; return true; };

        Assert.True(WorkspaceWritability.IsWritable(_library));
        Assert.True(WorkspaceWritability.IsWritable(_library));
        Assert.True(WorkspaceWritability.IsWritable(_library));
        Assert.Equal(1, probes);

        WorkspaceRootFinder.InvalidateCache();
        Assert.True(WorkspaceWritability.IsWritable(_library));
        Assert.Equal(2, probes);
    }

    /// <summary>
    /// R-sl2-2: a probe that throws for ANY reason means read-only — not "read-only unless the
    /// exception was UnauthorizedAccessException". A full disk, a disconnected share and a
    /// locked-down directory all mean the same thing to every caller downstream.
    /// </summary>
    [Fact]
    public void AProbeThatThrowsForAnyReasonMeansReadOnly()
    {
        WorkspaceWritability.WritabilityProbe = _ => throw new IOException("There is not enough space on the disk.");
        Assert.True(WorkspaceWritability.IsReadOnly(_library));
    }

    /// <summary>
    /// A scratch document has no directory, and answering "read-only" for it would disable Save on a
    /// document with no read-only workspace behind it at all — it saves through a picker, which asks
    /// the filesystem its own question about wherever the user points it.
    /// </summary>
    [Fact]
    public void NoPathIsNeverReadOnly()
    {
        MakeReadOnly(_root);   // everything is read-only
        Assert.False(WorkspaceWritability.IsDocumentReadOnly(null));
        Assert.True(WorkspaceWritability.IsWritable(null));
    }

    /// <summary>
    /// R-sl2-4: there is a per-DOCUMENT question too, and it is the same probe on the document's own
    /// directory. A workspace can be writable while one cell folder inside it is not — asking about
    /// the WORKSPACE would call this document saveable and discover otherwise at the write.
    /// </summary>
    [Fact]
    public void ThePerDocumentQuestionIsAskedOfTheDocumentsOwnDirectory()
    {
        string lockedCell = Path.Combine(_mine, "Locked");
        Directory.CreateDirectory(lockedCell);
        MakeReadOnly(lockedCell);

        Assert.False(WorkspaceWritability.IsWorkspaceReadOnly(Path.Combine(_mine, "Free", "a.clay")));
        Assert.True(WorkspaceWritability.IsDocumentReadOnly(Path.Combine(lockedCell, "a.clay")));
        Assert.False(WorkspaceWritability.IsDocumentReadOnly(Path.Combine(_mine, "a.clay")));
    }

    /// <summary>
    /// SL2 §5 item 2 — the one real-filesystem test. Everything above proves the BEHAVIOUR against a
    /// predicate; this proves the PREDICATE against a directory the test actually made unwritable,
    /// which is the half a seam cannot check. It skips with a reason where the platform cannot
    /// express an unwritable directory, and never passes vacuously.
    /// </summary>
    [UnwritableDirFact]
    public void TheRealProbeAnswersCorrectlyAgainstARealUnwritableDirectory()
    {
        WorkspaceWritability.WritabilityProbe = null;   // the real thing, not the seam

        Assert.True(WorkspaceWritability.IsWritable(_library));

        File.SetUnixFileMode(_library, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            WorkspaceRootFinder.InvalidateCache();
            Assert.True(WorkspaceWritability.IsReadOnly(_library));

            // And it leaves nothing behind: a probe that littered a librarian's share with temp
            // files every time a workspace was opened would be worse than the problem it solves.
            Assert.False(Directory.EnumerateFiles(_library, ".crf-write-probe-*").Any());
        }
        finally
        {
            File.SetUnixFileMode(_library,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        WorkspaceRootFinder.InvalidateCache();
        Assert.True(WorkspaceWritability.IsWritable(_library));
        Assert.False(Directory.EnumerateFiles(_library, ".crf-write-probe-*").Any());
    }

    /// <summary>
    /// A probe file orphaned by a CRASH is swept by the next probe, and a live one is not.
    ///
    /// <para><b>Why this test exists:</b> <c>FileOptions.DeleteOnClose</c> is a kernel flag on Windows
    /// but is emulated on Unix by unlinking when the handle closes — and a <c>SIGKILL</c> closes no
    /// handles. Measured directly: killing a process holding an open DeleteOnClose stream on macOS
    /// leaves the 1-byte file every time. That matters here rather than being harmless litter,
    /// because <c>WorkspaceScanner.IsHiddenTreeFile</c> hides only <c>.DS_Store</c> and
    /// <c>*.source</c> — NOT dotfiles in general — so an orphan would show as a loose file node at
    /// the workspace root and would travel into an archive.</para>
    ///
    /// <para>The crash is simulated by planting the file the killed process would have left, rather
    /// than by killing a process: the thing under test is the sweep, and a test that spawns and
    /// SIGKILLs a child to produce a file it could have written directly is a slower test of the
    /// same line.</para>
    /// </summary>
    [Fact]
    public void AProbeFileOrphanedByACrashIsSweptByTheNextProbe_AndALiveOneIsNot()
    {
        WorkspaceWritability.WritabilityProbe = null;   // the real probe, including its sweep

        string orphan = Path.Combine(_mine, ".crf-write-probe-" + new string('a', 32));
        File.WriteAllBytes(orphan, [0]);
        File.SetLastWriteTimeUtc(orphan, DateTime.UtcNow.AddHours(-2));   // a previous session's

        string live = Path.Combine(_mine, ".crf-write-probe-" + new string('b', 32));
        File.WriteAllBytes(live, [0]);                                    // another window, right now

        Assert.True(WorkspaceWritability.IsWritable(_mine));

        Assert.False(File.Exists(orphan));   // swept
        Assert.True(File.Exists(live));      // untouched — the age cut-off is what avoids the race
    }

    /// <summary>The sweep must never change the ANSWER. A directory that cannot be written is
    /// read-only whatever it happens to contain, and the sweep does not even run there — it is
    /// reached only after a probe has already succeeded.</summary>
    [Fact]
    public void TheSweepNeverChangesTheAnswer()
    {
        MakeReadOnly(_library);
        File.WriteAllBytes(Path.Combine(_library, ".crf-write-probe-" + new string('c', 32)), [0]);

        Assert.True(WorkspaceWritability.IsReadOnly(_library));
    }

    // ── R-sl2-5/-6: a read-only workspace writes nothing ─────────────────────

    /// <summary>
    /// SL2 §5 item 3, at the choke point (R-sl2-6). BYTE equality, not "no exception" — the point is
    /// that nothing was ATTEMPTED, and an atomic temp-file-then-rename that failed half way could
    /// leave the old bytes intact while still having tried.
    /// </summary>
    [Fact]
    public void TheChokePointWritesNothingAndSaysSo_WhenTheWorkspaceIsReadOnly()
    {
        string cws = Path.Combine(_library, ".cws");
        byte[] before = File.ReadAllBytes(cws);

        MakeReadOnly(_library);

        bool written = WorkspacePersistence.SaveToFileAtomic(cws, new CwsFile
        {
            ColorSchemeName    = "something-different",
            PythonInterpreter  = "/usr/bin/python3",
            ActiveDocumentPath = "Amp/schematic/Amp.csch",
        });

        Assert.False(written);
        Assert.Equal(before, File.ReadAllBytes(cws));
    }

    /// <summary>
    /// The other half of R-sl2-6: the guard is at the LOWEST level, so it is not a rule fifteen
    /// callers have to remember. A writable workspace is completely unaffected.
    /// </summary>
    [Fact]
    public void TheChokePointWritesNormallyWhenTheWorkspaceIsWritable()
    {
        string cws = Path.Combine(_mine, ".cws");
        MakeReadOnly(_library);   // the OTHER workspace

        Assert.True(WorkspacePersistence.SaveToFileAtomic(cws, new CwsFile { ColorSchemeName = "dusk" }));
        Assert.Equal("dusk", WorkspacePersistence.LoadFromFile(cws).ColorSchemeName);
    }

    /// <summary>
    /// R-sl2-5 names the whole list — dock layout, open-document list, active document, settled
    /// interpreter, kit settings, tree view state — and this asserts the list is enforced in ONE
    /// place rather than item by item: every one of them arrives as a <c>CwsFile</c> at the same
    /// method, so a field added to <c>CwsFile</c> next year is covered without anybody noticing.
    /// </summary>
    [Fact]
    public void EverySessionFieldIsCoveredBecauseTheyAllArriveAtTheSameMethod()
    {
        string cws = Path.Combine(_library, ".cws");
        byte[] before = File.ReadAllBytes(cws);
        MakeReadOnly(_library);

        var everything = new CwsFile
        {
            ColorSchemeName    = "dusk",
            PythonInterpreter  = "/usr/bin/python3",
            DefaultTechRef     = "tech/pcb.ctech",
            ActiveDocumentPath = "Amp/schematic/Amp.csch",
            TreeViewState      = new CwsTreeViewState { Cells = false },
            OpenDocuments      = [new CwsOpenDocument { Path = "Amp", Kind = "cell", TabOrder = 0 }],
        };
        everything.KnownFiles.Add("results/run.npy");

        Assert.False(WorkspacePersistence.SaveToFileAtomic(cws, everything));
        Assert.Equal(before, File.ReadAllBytes(cws));
    }

    /// <summary>
    /// SL2 §5 item 3, end to end: the whole session-state assembly — the dock layout, the tree view
    /// state, the open-document list, the active document, the colour scheme — run against a
    /// read-only workspace, asserting the <c>.cws</c> BYTES are unchanged. This is the test that
    /// covers the fourteen-of-fifteen failure mode R-sl2-6 exists to prevent: the choke-point tests
    /// above prove the guard works, and this one proves the real write path goes through it.
    ///
    /// <para>Asserted on the BYTES rather than on the message list: <c>MessagesTool.Post</c> marshals
    /// through <c>Dispatcher.UIThread</c>, which is a direct call in an isolated run and a queued one
    /// nobody pumps once another test in the same process has established an Avalonia dispatcher
    /// thread — so a message assertion here would pass alone and fail under full-suite load. What was
    /// said is checked by the wording tests above, which need no view model.</para>
    /// </summary>
    [Fact]
    public void WritingTheWorkspaceFileOnAReadOnlyWorkspaceChangesNoBytes()
    {
        string cws = Path.Combine(_library, ".cws");
        byte[] before = File.ReadAllBytes(cws);

        var vm = new WorkspaceViewModel { CurrentWorkspacePath = cws };
        MakeReadOnly(_library);

        vm.WriteWorkspaceFile(cws);

        Assert.Equal(before, File.ReadAllBytes(cws));
    }

    /// <summary>The same path on a WRITABLE workspace still writes — the guard is a branch on one
    /// question, not a new mode. Without this the test above would pass on a method that had simply
    /// stopped working.</summary>
    [Fact]
    public void WritingTheWorkspaceFileOnAWritableWorkspaceStillWrites()
    {
        string cws = Path.Combine(_mine, ".cws");
        byte[] before = File.ReadAllBytes(cws);

        var vm = new WorkspaceViewModel { CurrentWorkspacePath = cws };
        MakeReadOnly(_library);   // the OTHER one

        vm.WriteWorkspaceFile(cws);

        // The dock layout this view model just captured is written, so the bytes MUST differ.
        Assert.NotEqual(before, File.ReadAllBytes(cws));
        Assert.NotNull(WorkspacePersistence.LoadFromFile(cws).DockLayout);
    }

    // ── R-sl2-7/-8: Save is disabled, Save As is offered ─────────────────────

    /// <summary>
    /// R-sl2-7: Save is DISABLED on a read-only document — before the edit, not refused after it —
    /// and the reason names the WORKSPACE the file belongs to, because that is the thing the user
    /// recognises and the thing they would have to ask the librarian about.
    /// </summary>
    [Fact]
    public void SaveIsDisabledOnAReadOnlyDocument_AndTheReasonNamesTheWorkspace()
    {
        string cellDir = CellFolder.CreateCellFolder(_library, "Amp");
        string layDir  = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        Directory.CreateDirectory(layDir);
        string clay = Path.Combine(layDir, "Amp.clay");
        LayoutPersistence.SaveToFile(clay, new LayoutView { DbuPerMicron = 1000, SnapDbu = 1000 });
        WorkspaceRootFinder.InvalidateCache();

        var doc = new LayoutDocument("Amp", NewLayoutVm(clay), clay);

        Assert.Null(WorkspaceViewModel.ReadOnlyDocumentReason(doc));   // writable: no reason, Save works

        MakeReadOnly(_library);

        Assert.True(WorkspaceViewModel.IsDocumentReadOnly(doc));
        string reason = Assert.IsType<string>(WorkspaceViewModel.ReadOnlyDocumentReason(doc));
        Assert.Contains("stdlib", reason);           // the workspace, named
        Assert.Contains("read-only", reason);
        Assert.Contains("Save a copy", reason);      // and the route out, in the same breath
    }

    /// <summary>
    /// R-sl2-8: editing a read-only document is still allowed. Reading a library cell and pulling it
    /// about to understand it is a legitimate and common thing to do, and the product must not make a
    /// file un-scrollable because it is un-writable. Only the SAVE changes.
    /// </summary>
    [Fact]
    public void AReadOnlyDocumentIsStillEditable()
    {
        string clay = Path.Combine(_library, "loose.clay");
        LayoutPersistence.SaveToFile(clay, new LayoutView { DbuPerMicron = 1000, SnapDbu = 1000 });
        MakeReadOnly(_library);

        var vm = NewLayoutVm(clay);
        Assert.True(vm.IsDocumentReadOnly);

        vm.Model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 10, Y2 = 10 });
        Assert.Single(vm.Model.Shapes);   // nothing refused the edit itself
    }

    // ── R-sl2-9: the marking reuses the foreign-document chrome ──────────────

    /// <summary>
    /// R-sl2-9: read-only is added to the EXISTING band's wording, not to a fourth surface or a
    /// second colour, and the band now appears for a read-only document that is not foreign at all —
    /// the case where the whole open workspace IS the read-only share, which previously had no band.
    /// </summary>
    [Fact]
    public void TheBandSaysReadOnly_AndAppearsEvenWhenTheDocumentIsNotForeign()
    {
        string clay = Path.Combine(_library, "Amp.clay");
        LayoutPersistence.SaveToFile(clay, new LayoutView { DbuPerMicron = 1000, SnapDbu = 1000 });
        WorkspaceRootFinder.InvalidateCache();

        var vm = NewLayoutVm(clay);
        // Not foreign: the currently open workspace IS the one this file lives in.
        vm.CurrentWorkspaceRootDirProvider = () => _library;

        Assert.False(vm.IsForeign);
        Assert.False(vm.ShowProvenanceBand);

        MakeReadOnly(_library);

        Assert.False(vm.IsForeign);           // still not foreign — read-only is the OTHER axis
        Assert.True(vm.ShowProvenanceBand);
        Assert.Contains("read-only", vm.ProvenanceBandText);
        Assert.Contains("stdlib", vm.ProvenanceBandText);
        Assert.Contains("Save As", vm.ProvenanceBandText);
        // "Open Workspace" is meaningless when the file already belongs to the open workspace.
        Assert.False(vm.CanOpenSourceWorkspace);
    }

    /// <summary>
    /// The other combination: foreign AND read-only, which is the corporate-library case itself. One
    /// band, one sentence, both halves — §5A.4's three surfaces already say "this belongs to another
    /// workspace", and read-only is the other half of the same message.
    /// </summary>
    [Fact]
    public void AForeignReadOnlyDocumentGetsOneBandCarryingBothHalves()
    {
        string clay = Path.Combine(_library, "Amp.clay");
        LayoutPersistence.SaveToFile(clay, new LayoutView { DbuPerMicron = 1000, SnapDbu = 1000 });
        WorkspaceRootFinder.InvalidateCache();
        MakeReadOnly(_library);

        var vm = NewLayoutVm(clay);
        vm.CurrentWorkspaceRootDirProvider = () => _mine;   // a DIFFERENT workspace is open

        Assert.True(vm.IsForeign);
        Assert.True(vm.IsDocumentReadOnly);
        Assert.Contains("stdlib", vm.ProvenanceBandText);
        Assert.Contains("read-only", vm.ProvenanceBandText);
        Assert.True(vm.CanOpenSourceWorkspace);             // this one CAN be opened
    }

    /// <summary>A writable foreign document is unchanged by SL2 — §5A.3 stands for every writable
    /// case, and this is the test that says so.</summary>
    [Fact]
    public void AWritableForeignDocumentIsUnchanged()
    {
        string clay = Path.Combine(_library, "Amp.clay");
        LayoutPersistence.SaveToFile(clay, new LayoutView { DbuPerMicron = 1000, SnapDbu = 1000 });
        WorkspaceRootFinder.InvalidateCache();

        var vm = NewLayoutVm(clay);
        vm.CurrentWorkspaceRootDirProvider = () => _mine;

        Assert.True(vm.IsForeign);
        Assert.False(vm.IsDocumentReadOnly);
        Assert.Equal("From workspace: stdlib", vm.ProvenanceBandText);
    }

    // ── R-sl2-10: the PCell refusal names the workspace, not the parameters ──

    /// <summary>
    /// SL2 §5 item 6. Before this, <c>GetOrCreate</c>'s <c>Directory.CreateDirectory</c> under the
    /// document's own (correct, unwritable) workspace root threw into a catch whose whole sentence
    /// blames the PARAMETERS — so a user whose generator was fine and whose directory was not spent
    /// the afternoon on the parameters.
    /// </summary>
    [Fact]
    public void ThePCellRefusalNamesTheWorkspaceAndNotTheParameters()
    {
        string clay = Path.Combine(_library, "Amp.clay");
        LayoutPersistence.SaveToFile(clay, new LayoutView { DbuPerMicron = 1000, SnapDbu = 1000 });
        WorkspaceRootFinder.InvalidateCache();
        MakeReadOnly(_library);

        var sink = new CollectingMessageSink();
        var vm   = NewLayoutVm(clay, sink);

        string generatorId = PCellRegistry.AllKnownGeneratorIds().First();
        Assert.False(vm.BeginPCellPlacement(generatorId, new Dictionary<string, PCellValue>()));

        string message = Assert.Single(sink.Errors);
        Assert.Contains("stdlib", message);
        Assert.Contains("read-only", message);
        Assert.DoesNotContain("parameters", message, StringComparison.OrdinalIgnoreCase);
    }

    // ── R-sl2-13: creating INTO an unwritable place refuses at the picker ────

    /// <summary>
    /// R-sl2-13, as one rule behind all three sites (New Workspace, Save Workspace As, and the save
    /// plan's own workspace step). The refusal NAMES the directory, because "somewhere you picked" is
    /// not something a user can act on — and it comes before anything is created, because
    /// SavePlanExecutor makes the folder and the .cws before it makes any cell.
    /// </summary>
    [Fact]
    public void CreatingAWorkspaceInAnUnwritableParentRefusesAndNamesTheDirectory()
    {
        MakeReadOnly(_library);

        Assert.Null(WorkspaceViewModel.UnwritableParentRefusal(_mine, "Nothing was saved"));

        string refusal = Assert.IsType<string>(
            WorkspaceViewModel.UnwritableParentRefusal(_library, "The workspace was not created"));
        Assert.Contains(_library, refusal);
        Assert.Contains("read-only", refusal);
        Assert.Contains("The workspace was not created", refusal);
    }

    // ── Structural gates: R-sl2-6's whole point, and R-sl2-8's quit-prompt route ─

    /// <summary>
    /// R-sl2-6, asserted STRUCTURALLY because it is a structural claim: every <c>.cws</c> write in
    /// production code goes through <c>WorkspacePersistence.SaveToFileAtomic</c>. There were fifteen
    /// call sites and no choke point when SL2 started; the guard lives in the one method precisely so
    /// that a SIXTEENTH site inherits the rule without knowing it exists — and this test is what
    /// notices if somebody writes a <c>.cws</c> some other way instead.
    ///
    /// <para>The one exception is the non-atomic <c>SaveToFile</c> the DOC-FIXTURE generator uses,
    /// which builds throwaway workspaces under a scratch directory to screenshot; it is named here
    /// rather than left to be rediscovered.</para>
    /// </summary>
    [Fact]
    public void EveryCwsWriteInProductionCodeGoesThroughTheChokePoint()
    {
        var offenders = new List<string>();
        string srcRoot = Path.Combine(RepoRoot(), "src");

        foreach (string file in Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (file.EndsWith("WorkspacePersistence.cs", StringComparison.Ordinal)) continue;
            if (file.EndsWith("DocWorkspaceFixtures.cs", StringComparison.Ordinal)) continue;   // named above

            string code = StripComments(File.ReadAllText(file));
            foreach (System.Text.RegularExpressions.Match m in Regex.Matches(code, @"WorkspacePersistence\.SaveToFile\s*\("))
                offenders.Add($"{Path.GetFileName(file)}: {m.Value.Trim()} — use SaveToFileAtomic");
        }

        Assert.Empty(offenders);
    }

    /// <summary>
    /// R-sl2-8 / SL2 §5 item 4, second half: the quit prompt routes a read-only document through
    /// Save As rather than offering a Save that cannot succeed. Asserted at source level because
    /// <c>PromptSaveBeforeClose</c> shows a modal and needs a real <c>Window</c>, which no headless
    /// test can supply — the same shape <c>MultiWorkspaceShellTests</c> already uses, and comments
    /// are stripped first, because a rule that only matches because the sentence NAMING it is in a
    /// comment is not a rule.
    /// </summary>
    [Fact]
    public void TheQuitPromptRoutesAReadOnlyDocumentThroughSaveAs()
    {
        string code = StripComments(
            File.ReadAllText(Path.Combine(RepoRoot(), "src", "Ui", "ViewModels", "WorkspaceViewModel.cs")));

        int start = code.IndexOf("public async Task<bool> PromptSaveBeforeClose", StringComparison.Ordinal);
        Assert.True(start >= 0, "PromptSaveBeforeClose was renamed — this gate needs re-pointing.");
        string body = code[start..];

        // Each of the three materialized-document save loops asks the read-only question and takes
        // the Save-As branch when the answer is yes.
        Assert.Contains("dirtyMat.Where(IsDocumentReadOnly)", body, StringComparison.Ordinal);
        Assert.Contains("await SaveSchematicAs(doc, owner);", body, StringComparison.Ordinal);
        Assert.Contains("await SaveSymbolAs(symDoc, owner);", body, StringComparison.Ordinal);
        Assert.Contains("await SaveLayoutAs(layDoc, owner);", body, StringComparison.Ordinal);

        // And the direct-write loop no longer sees them.
        Assert.Contains("dirtyMat.Where(d => !IsDocumentReadOnly(d))", body, StringComparison.Ordinal);
    }

    private static string RepoRoot([CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root.");
        return dir!;
    }

    /// <summary>Strips block and line comments so a scan cannot be satisfied by prose.</summary>
    private static string StripComments(string code)
        => Regex.Replace(Regex.Replace(code, @"/\*.*?\*/", "", RegexOptions.Singleline),
                         @"//[^
]*", "");

    private static LayoutEditorViewModel NewLayoutVm(string clayPath, IMessageSink? sink = null)
        => new(new LayoutView { DbuPerMicron = 1000, SnapDbu = 1000 }, clayPath, sink);

    private sealed class CollectingMessageSink : IMessageSink
    {
        public List<string> Errors { get; } = [];
        public void Post(MessageLevel level, string text, string? filePath = null)
        {
            if (level == MessageLevel.Error) Errors.Add(text);
        }
        public void Clear() => Errors.Clear();
    }
}

using System;
using System.IO;
using System.Linq;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

// ──────────────────────────────────────────────────────────────────────────────
//  Leaving a workspace must record its session, or its open tabs are forgotten.
//
//  Owner-reported (2026-07-30): a .ctech tab was not restored after opening another workspace and
//  coming back.
//
//  The cause was NOT technology-specific. `WorkspaceViewModel.WriteWorkspaceFile` is the one place
//  both the open-document list and the dock layout are captured, and every caller was an explicit
//  save (Save Workspace, Save All, a per-document save), the tree-filter debounce, or clean exit.
//  No path that LEAVES a workspace called it — so the outgoing session was only ever recorded by
//  accident, whenever some unrelated action happened to trigger a save while those tabs were open.
//
//  That accident is exactly why the report named .ctech: a schematic gets edited and saved, and
//  SaveAllDocuments writes .cws as a side effect. A technology opened, read, and left clean triggers
//  none of that. Every document type was affected; .ctech is just the one whose normal usage never
//  hits the accidental save.
//
//  `WorkspaceViewModel` cannot be constructed headlessly (its ctor builds a Dock layout and posts to
//  Dispatcher.UIThread — see src/Ui/CLAUDE.md), so the wiring is pinned by source scan, the
//  established fallback here. The .cws round trip underneath it is exercised for real.
// ──────────────────────────────────────────────────────────────────────────────

public sealed class WorkspaceSessionPersistedOnLeaveTests : IDisposable
{
    private readonly string _root;

    public WorkspaceSessionPersistedOnLeaveTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf-ws-leave-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // ── The wiring: every leave path records the session first ────────────────

    [Theory]
    [InlineData("SwitchToWorkspace")]   // Open Workspace, Open Recent, Open Source Workspace
    [InlineData("NewWorkspace")]        // File ▸ New Workspace
    [InlineData("ResetToBlankShell")]   // File ▸ Close Workspace
    public void EveryPathThatLeavesAWorkspace_RecordsItsSessionBeforeTearingItDown(string method)
    {
        var src = RepoFile(Path.Combine("src", "Ui", "ViewModels", "WorkspaceViewModel.cs"));
        var body = MethodBody(src, method);

        var persist = body.IndexOf("PersistOutgoingWorkspaceSession()", StringComparison.Ordinal);
        Assert.True(persist >= 0, $"{method} must record the outgoing workspace's session");

        // Ordering is load-bearing, not cosmetic. CloseFloatedDocumentsOwnedByWorkspace removes
        // torn-off documents from _openDocsByPath, and the clear empties it outright — a write after
        // either one records a session that is already partly or wholly gone.
        foreach (var teardown in new[] { "CloseFloatedDocumentsOwnedByWorkspace", "_openDocsByPath.Clear()" })
        {
            var at = body.IndexOf(teardown, StringComparison.Ordinal);
            if (at >= 0)
                Assert.True(persist < at,
                    $"{method}: the session must be recorded BEFORE {teardown}, or the record is already incomplete");
        }
    }

    [Fact]
    public void RecordingTheSession_IsSilent_AndSurvivesAWorkspaceDeletedFromUnderUs()
    {
        var src  = RepoFile(Path.Combine("src", "Ui", "ViewModels", "WorkspaceViewModel.cs"));
        var body = MethodBody(src, "PersistOutgoingWorkspaceSession");

        // Leaving a workspace is not a save the user asked for, so it must not announce itself...
        Assert.Contains("silent: true", body);
        // ...and a workspace whose .cws is gone must fail quietly rather than posting an error on the
        // way out, which would be a confusing thing to show while the shell is being torn down.
        Assert.Contains("File.Exists", body);
    }

    [Fact]
    public void WriteWorkspaceFile_IsStillTheOnePlaceTheSessionIsCaptured()
    {
        var src = RepoFile(Path.Combine("src", "Ui", "ViewModels", "WorkspaceViewModel.cs"));

        // The fix works by routing the leave paths through the EXISTING capture, not by adding a
        // second one. A parallel writer would drift from this one the first time either changed.
        var body = MethodBody(src, "PersistOutgoingWorkspaceSession");
        Assert.Contains("WriteWorkspaceFile(", body);
        Assert.DoesNotContain("SaveToFileAtomic", body);
    }

    // ── The .cws round trip underneath it, exercised for real ─────────────────

    [Fact]
    public void ACtechTab_RoundTripsThroughCws_AsAnOrdinaryOpenDocument()
    {
        var cwsPath = Path.Combine(_root, ".cws");

        var written = new CwsFile
        {
            OpenDocuments = new()
            {
                new CwsOpenDocument { Path = Path.Combine("tech", "pcb.ctech"), Kind = "tech",       TabOrder = 0 },
                new CwsOpenDocument { Path = Path.Combine("Amp", "schematic", "Amp.csch"), Kind = "schematic", TabOrder = 1 },
            },
            ActiveDocumentPath = Path.Combine("tech", "pcb.ctech"),
        };

        WorkspacePersistence.SaveToFileAtomic(cwsPath, written);
        var read = WorkspacePersistence.LoadFromFile(cwsPath);

        var tech = Assert.Single(read.OpenDocuments!, d => d.Kind == "tech");
        Assert.Equal(Path.Combine("tech", "pcb.ctech"), tech.Path);
        Assert.Equal(0, tech.TabOrder);
        Assert.Equal(Path.Combine("tech", "pcb.ctech"), read.ActiveDocumentPath);
    }

    [Fact]
    public void TheRestoreSwitch_HandlesEveryKindTheWriterCanEmit()
    {
        var src = RepoFile(Path.Combine("src", "Ui", "ViewModels", "WorkspaceViewModel.cs"));

        var writer  = MethodBody(src, "WriteWorkspaceFile");
        var restore = MethodBody(src, "RestoreOpenDocuments");

        // A kind the writer emits but the restorer has no case for is a silently-dropped tab — the
        // same class of bug as not writing it at all, just one step later.
        foreach (var kind in new[] { "schematic", "symbol", "cell", "datadisplay", "layout", "tech" })
        {
            Assert.Contains($"\"{kind}\"", writer);
            Assert.Contains($"case \"{kind}\"", restore);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Body of a method, from its declaration to the matching closing brace.
    ///
    /// <para>Anchors on the DECLARATION — a member-level line carrying an access modifier — not on
    /// any occurrence of the name. Matching a bare "name(" instead finds the first CALL SITE, and
    /// brace-walking from there silently returns some unrelated block (an interpolated string, in
    /// practice), which reads as a passing-or-failing test about the wrong code entirely.</para>
    /// </summary>
    private static string MethodBody(string src, string name)
    {
        var decl = System.Text.RegularExpressions.Regex.Match(
            src,
            $@"\n\s+(?:private|public|internal|protected)[^\n(]*\b{System.Text.RegularExpressions.Regex.Escape(name)}\s*\(");
        Assert.True(decl.Success, $"{name} must be declared");

        var open = src.IndexOf('{', decl.Index + decl.Length);
        Assert.True(open >= 0);

        int depth = 0;
        for (int i = open; i < src.Length; i++)
        {
            if (src[i] == '{') depth++;
            else if (src[i] == '}' && --depth == 0) return src[open..i];
        }

        Assert.Fail($"unbalanced braces walking {name}");
        return "";
    }

    private static string RepoFile(string rel)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "circuitrf.slnx")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!, rel));
    }
}

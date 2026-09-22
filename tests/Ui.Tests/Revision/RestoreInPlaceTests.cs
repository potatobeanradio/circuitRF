using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CircuitRF.Design.Revision;
using CircuitRF.Ui.ViewModels;
using Xunit;
using static CircuitRF.Ui.Tests.Revision.RestorePointsTests;

namespace CircuitRF.Ui.Tests.Revision;

/// <summary>
/// <b>Going back to an earlier state does not rebuild the window</b> —
/// <c>docs/sonnet-briefs/brief-history-restore-in-place.md</c> §5.
///
/// <para>Owner report, 2026-09-08: changing state from the History window stalled and then flashed as
/// the whole window was torn down and rebuilt, for a restore that had changed one schematic. The cause
/// was scope: a restore reused the function that opens a DIFFERENT workspace, which correctly drops
/// every registry, every panel and every tab — and correctly rewrote every file in the workspace on the
/// way, whether it differed or not.</para>
///
/// <para><b>The gates are counters, not clocks.</b> This repository does not add timing tests: they
/// measure the machine and flake. What is asserted is the structural property the speed follows from —
/// only the files that differ are written, and only the documents whose files changed are reloaded.</para>
/// </summary>
[Collection(AppDataRootCollection.Name)]
public class RestoreInPlaceTests
{
    // ── No file the restore did not change is written ────────────────────────────────────────────

    /// <summary>
    /// The whole of §2's fact: git already knows exactly which files differ, and it is almost never
    /// many. Before this, <c>checkout-index -a -f</c> rewrote every file in the workspace, so
    /// <c>FilesWritten</c> reported a number that had nothing to do with what changed — and every
    /// untouched file came back with a new modification time, which is what made a one-schematic
    /// restore look downstream like a workspace that had changed entirely.
    /// </summary>
    [GitFact]
    public void ARestoreWritesOnlyTheFilesThatDiffer()
    {
        using var ws = Armed();
        var git = ws.Git();

        ws.Write("cells/a/thing.csch", "tuesday");
        ws.Write("cells/b/other.csch", "never touched");
        ws.Write("cells/c/third.csch", "never touched either");
        var tuesday = Take(ws, "as it was on Tuesday");

        ws.Write("cells/a/thing.csch", "thursday afternoon");

        string untouched = ws.File_("cells/b/other.csch");
        var    stamped   = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(untouched, stamped);

        var result = WorkspaceRestore.Restore(git, tuesday);

        Assert.True(result.Ok);
        Assert.Equal("tuesday", File.ReadAllText(ws.File_("cells/a/thing.csch")));

        // One file differed, so one file was written and one path is named.
        Assert.Equal(1, result.FilesWritten);
        Assert.NotNull(result.ChangedPaths);
        Assert.Equal(new[] { "cells/a/thing.csch" },
                     result.ChangedPaths!.Where(c => c.Kind == RestoredPathKind.Written)
                                         .Select(c => c.RelativePath));

        // And the two that did not differ were not rewritten — the assertion the count alone cannot
        // make, since a rewrite with identical bytes is invisible in the content.
        Assert.Equal(stamped, File.GetLastWriteTimeUtc(untouched));
    }

    /// <summary>
    /// A file created since the state being restored is named as removed, so the caller can close the
    /// tab holding it. <b>The removal pass itself is untouched</b> — R-rc5-12c rule 2 is about which
    /// files a removal may act on and is not a performance question.
    /// </summary>
    [GitFact]
    public void WhatCameAfterIsNamedAsRemoved()
    {
        using var ws = Armed();
        var git = ws.Git();

        ws.Write("cells/a/thing.csch", "tuesday");
        var tuesday = Take(ws, "as it was on Tuesday");

        ws.Write("cells/e/new-cell.csch", "created after");

        var result = WorkspaceRestore.Restore(git, tuesday);

        Assert.True(result.Ok);
        Assert.NotNull(result.ChangedPaths);
        Assert.Equal(new[] { "cells/e/new-cell.csch" },
                     result.ChangedPaths!.Where(c => c.Kind == RestoredPathKind.Removed)
                                         .Select(c => c.RelativePath));
        Assert.False(File.Exists(ws.File_("cells/e/new-cell.csch")));
    }

    /// <summary>
    /// §3(b), held shut. <b>The act of reading history must not write history.</b> Routing a restore
    /// through the workspace-switch path took a close checkpoint of a workspace nobody was leaving —
    /// and it could never be skipped as unchanged, because the same switch had just written the
    /// <c>.cws</c> a step earlier (§3(a)). Every single restore recorded an automatic entry labelled
    /// as a workspace close.
    ///
    /// <para>Asserted where the count is observable: the entry list after a restore holds the
    /// pre-restore checkpoint and nothing else. The window's half is held by the source scan below,
    /// which is what stops that call coming back.</para>
    /// </summary>
    [GitFact]
    public void RestoringAddsThePreRestoreEntryAndNoOther()
    {
        using var ws = Armed();
        var git = ws.Git();

        ws.Write("cells/a/thing.csch", "tuesday");
        var tuesday = Take(ws, "as it was on Tuesday");

        ws.Write("cells/a/thing.csch", "thursday afternoon");

        int before = RestorePoints.List(git).Count;
        var result = WorkspaceRestore.Restore(git, tuesday);

        Assert.True(result.Ok);
        Assert.NotNull(result.PreRestore);
        Assert.Equal(before + 1, RestorePoints.List(git).Count);
    }

    // ── Only the documents whose files changed are reloaded ──────────────────────────────────────

    /// <summary>
    /// The counter the brief asks for: a restore that changes one of five open documents reloads
    /// exactly one. <see cref="WorkspaceViewModel"/> cannot be built with real documents in it
    /// headlessly, so the decision itself is what is exercised — which is the part that can be wrong.
    ///
    /// <para>A cell's document key is its FOLDER, not a file, so a changed file anywhere inside it is
    /// a change to that document. Reading that as equality would leave a cell parameter editor showing
    /// the state that was just replaced.</para>
    /// </summary>
    [Fact]
    public void OnlyTheOpenDocumentsWhoseFilesChangedAreReloaded()
    {
        string root = Path.Combine(Path.GetTempPath(), "crf-restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "cells", "amp"));
        Directory.CreateDirectory(Path.Combine(root, "cells", "mixer"));
        try
        {
            string Abs(params string[] parts) => Path.Combine([root, .. parts]);

            string[] open =
            [
                Abs("cells", "amp", "amp.csch"),
                Abs("cells", "amp", "amp.clay"),
                Abs("cells", "mixer", "mixer.csch"),
                Abs("cells", "mixer"),                 // a cell folder — the key is a DIRECTORY
                Abs("board.cdd"),
            ];

            // One schematic came back, and nothing else.
            var affected = WorkspaceViewModel.OpenDocumentsAffectedBy(
                open, [Abs("cells", "amp", "amp.csch")]);

            Assert.Equal([Abs("cells", "amp", "amp.csch")], affected);

            // A file inside the cell folder reaches BOTH the document for that file and the folder's
            // own document, and still reaches nothing else.
            var inCell = WorkspaceViewModel.OpenDocumentsAffectedBy(
                open, [Abs("cells", "mixer", "mixer.csch")]);

            Assert.Equal([Abs("cells", "mixer", "mixer.csch"), Abs("cells", "mixer")], inCell);

            // Nothing changed reaches nothing.
            Assert.Empty(WorkspaceViewModel.OpenDocumentsAffectedBy(open, []));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    // ── The window is not rebuilt ────────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>The dock layout is not rebuilt, and neither is anything else the restore did not change.</b>
    /// Observing that needs a running Avalonia application, so it is held at the source, which is the
    /// shape this repository's other window-level rules already use — comments stripped first, because
    /// a rule that only matched because the sentence naming it was in a comment is not a rule.
    ///
    /// <para>The one permitted reach for the full reopen is the null fallback, and it is named here
    /// rather than left to be rediscovered: a restore whose diff could not be produced must reload
    /// everything, because reloading nothing would leave every tab showing the replaced state.</para>
    /// </summary>
    [Fact]
    public void GoingBackNeverReopensTheWorkspace()
    {
        string source = StripComments(ReadSource("src/Ui/ViewModels/WorkspaceViewModel.Revision.cs"));

        // Every one of the four callers reaches the narrow reload.
        Assert.Equal(4, Regex.Matches(source, @"await ReloadChangedDocuments\(").Count);

        // And the full reopen is reached from exactly one place: the fallback inside it.
        Assert.Single(Regex.Matches(source, @"await ReloadWorkspaceAfterFilesChangedUnderneath\(\)"));

        var body = Between(source, "private async Task ReloadChangedDocuments(", "private void RestoreTabPosition(");

        // The fallback, and nothing else, may reopen the workspace.
        Assert.Single(Regex.Matches(body, @"ReloadWorkspaceAfterFilesChangedUnderneath"));
        Assert.DoesNotContain("SwitchToWorkspace", body, StringComparison.Ordinal);

        // The panels, the arrangement and the caches the restore did not touch are left alone.
        Assert.DoesNotContain("Layout =",            body, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateDefaultLayout", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Messages.Clear",      body, StringComparison.Ordinal);
        Assert.DoesNotContain("ResetTechCache",      body, StringComparison.Ordinal);
        Assert.DoesNotContain("RegenerateAllGeneratedCells", body, StringComparison.Ordinal);
        Assert.DoesNotContain("PersistOutgoingWorkspaceSession", body, StringComparison.Ordinal);
        Assert.DoesNotContain("TakeCloseCheckpoint", body, StringComparison.Ordinal);
    }

    // ── The two things the window owes a designer who has just clicked (owner, 2026-09-08) ──────

    /// <summary>
    /// <b>The context menu goes at the click, and the pointer says the window is working.</b> Both
    /// need a running Avalonia application to observe, so both are held at the source.
    ///
    /// <para>Going back is the only item on that menu whose action is long. Avalonia closes a context
    /// menu when an item is clicked, but the handler runs first and inline, so the close could not
    /// paint until the restore was over and the menu sat on top of the work it had started. The fix is
    /// both halves — close it explicitly, and post the action behind a frame — so the gate asserts the
    /// two slow items go through the helper and that the helper still does both.</para>
    /// </summary>
    [Fact]
    public void TheMenuGoesAtTheClickAndThePointerSaysTheWindowIsWorking()
    {
        string view = StripComments(ReadSource("src/Ui/Views/Revision/HistoryToolView.axaml.cs"));

        // The two actions that start a restore, and only those, defer.
        Assert.Contains("=> AfterTheClickHasPainted(tool => tool.GoBack());",     view, StringComparison.Ordinal);
        Assert.Contains("=> AfterTheClickHasPainted(tool => tool.ComeForward());", view, StringComparison.Ordinal);

        // Both halves: the menu goes now, the work starts after the frame without it.
        Assert.Contains("HistoryRows.ContextMenu?.Close();",  view, StringComparison.Ordinal);
        Assert.Contains("DispatcherPriority.Background",      view, StringComparison.Ordinal);

        // The busy cursor covers the workspace switch and both ways back — and is put back, which is
        // the half that is invisible when it is missing until a window is left with a wait pointer.
        string revision = StripComments(ReadSource("src/Ui/ViewModels/WorkspaceViewModel.Revision.cs"));
        string vm       = StripComments(ReadSource("src/Ui/ViewModels/WorkspaceViewModel.cs"));
        // WhileAsync, not While: the callers block on the next line, and a cursor assigned with no
        // dispatcher turn after it is one the platform has had no moment to draw.
        Assert.Equal(2, Regex.Matches(revision, @"await Views\.BusyCursorScope\.WhileAsync\(").Count);
        Assert.Single(Regex.Matches(vm,       @"await Views\.BusyCursorScope\.WhileAsync\("));

        string scope = StripComments(ReadSource("src/Ui/Views/BusyCursorScope.cs"));
        Assert.Contains("StandardCursorType.Wait", scope, StringComparison.Ordinal);
        Assert.Contains("DispatcherPriority.Background", scope, StringComparison.Ordinal);
        // Restored to what it WAS, never to Default — assigning Default would discard a cursor
        // something else had set on the window.
        Assert.Contains("_window.Cursor = _previous;", scope, StringComparison.Ordinal);
        Assert.DoesNotContain("Cursor.Default", scope, StringComparison.Ordinal);
    }

    private static string Between(string source, string from, string to)
    {
        int a = source.IndexOf(from, StringComparison.Ordinal);
        Assert.True(a >= 0, $"'{from}' is no longer in the source.");
        int b = source.IndexOf(to, a, StringComparison.Ordinal);
        Assert.True(b > a, $"'{to}' is no longer after '{from}' in the source.");
        return source[a..b];
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────

    private static GitWorkspace Armed()
    {
        var ws = new GitWorkspace();
        File.WriteAllText(ws.GlobalConfig,
            "[user]\n\tname = A Designer\n\temail = designer@example.invalid\n");

        Assert.True(WorkspaceArming.Arm(ws.Root, CheckpointOrigin.SavePoint, true, null, true).Armed);
        return ws;
    }

    private static RestorePoint Take(GitWorkspace ws, string label)
    {
        var outcome = WorkspaceCheckpoints.Take(ws.Git(), CheckpointOrigin.SavePoint, label);
        Assert.True(outcome.Recorded);
        return outcome.Point!;
    }
}

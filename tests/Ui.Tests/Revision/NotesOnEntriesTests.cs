using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using CircuitRF.Design.Revision;
using CircuitRF.Ui.Messages;
using CircuitRF.Ui.Revision;
using CircuitRF.Ui.ViewModels.Dock;
using Xunit;

namespace CircuitRF.Ui.Tests.Revision;

/// <summary>
/// <b>The longer note</b> — <c>docs/design/revision-control.md</c> §5.12.
///
/// <para><b>What this is for.</b> A title is one line and some decisions need a paragraph: what was
/// tried first, what the measurement said, which of two options this is and why. None of that fits on
/// the line a designer scans for, and none of it can be recovered from the files afterwards. So both
/// dialogs that record something ask for both, and both halves are corrected in the one dialog that
/// corrects what a person wrote.</para>
///
/// <para><b>Two things here are worth more than the rest.</b> An entry nobody wrote a note for must be
/// written <b>byte for byte</b> as it was before this existed — otherwise every workspace grows a line
/// saying nothing. And every operation that REBUILDS an entry's message has to carry the note through:
/// a mark-keep or a rename that rebuilt without it would delete somebody's paragraph as a side effect
/// of an operation about a label, silently, and the loss would not be noticed for weeks.</para>
///
/// <para><b>Every fixture path here is the SHAPE of a path, never a real one.</b></para>
///
/// <para>In <see cref="AppDataRootCollection"/> for the reason RC-5's, RC-7's, RC-10's and RC-11's own
/// gates are: the identity path redirects the per-user state directory, and the git-environment
/// isolation these tests install is process-wide.</para>
/// </summary>
[Collection(AppDataRootCollection.Name)]
public class NotesOnEntriesTests
{
    // ══ 1. An entry with no note is what it always was ════════════════════════════════════════════

    /// <summary>
    /// <b>Byte for byte.</b> Most entries carry no note, and the ordinary one must not acquire a block,
    /// a blank line or a trailer announcing that it has none — which is the shape of a format change
    /// that looks harmless and shows up in every diff anyone takes of a message afterwards.
    /// </summary>
    [Fact]
    public void AnEntryWithNoNoteIsWrittenExactlyAsItWasBefore()
    {
        Assert.Equal(CommitMessage.Build("Output match retuned"),
                     CommitMessage.Build("Output match retuned", null, null));
        Assert.Equal(CommitMessage.Build("Output match retuned", null, "   \n  \n"),
                     CommitMessage.Build("Output match retuned"));

        Assert.DoesNotContain(MessageNotes.LinesKey, CommitMessage.Build("Output match retuned"));
        Assert.DoesNotContain(MessageNotes.LinesKey,
                              CheckpointMessage.Build(CheckpointOrigin.SavePoint, "Widen it", 4));

        // And an entry that never carried one reads back an empty note rather than the explanation
        // circuitRF wrote under the subject.
        Assert.Equal("", CommitMessage.Read(CommitMessage.Build("Output match retuned")).Note);
        Assert.Equal("", CheckpointMessage.Read(
                             CheckpointMessage.Build(CheckpointOrigin.SavePoint, "Widen it", 4)).Note);
    }

    // ══ 2. The round trip, including the two shapes that break a naive one ════════════════════════

    /// <summary>
    /// <b>A note is free text, and both of the awkward things a person actually types survive it</b>: a
    /// blank line between paragraphs, which a naive reader would treat as the end of the note, and a
    /// line that happens to look like one of circuitRF's own trailers, which a naive reader would parse
    /// as a key.
    ///
    /// <para>That second one is why the extent is COUNTED and why the count is read from the trailing
    /// block alone. A note able to describe its own extent is a note that can be made to hide the rest
    /// of the message.</para>
    /// </summary>
    [Fact]
    public void ANoteSurvivesAParagraphBreakAndALineThatLooksLikeATrailer()
    {
        string note = "Series inductor moved to the drain side.\n"
                    + "\n"
                    + "The 3.9 pF shunt measured 0.4 dB better than the 4.7 pF.\n"
                    + CommitMessage.OriginKey + ": not a trailer, a sentence somebody typed";

        string message = CommitMessage.Build("Output match retuned", null, note);
        var    read    = CommitMessage.Read(message);

        Assert.Equal(note, read.Note);
        Assert.Equal("Output match retuned", read.Title);

        // The trailer-shaped line inside the note did NOT become the origin, and the real one still
        // reads — which is the whole reason the note's extent is stepped over on the way in.
        Assert.True(read.Explicit);

        // The same, on the other builder.
        string kept = CheckpointMessage.Build(CheckpointOrigin.SavePoint, "Widen it", 7, note: note);
        var    back = CheckpointMessage.Read(kept);

        Assert.Equal(note, back.Note);
        Assert.Equal("Widen it", back.Subject);
        Assert.Equal(7, back.Sequence);
        Assert.Equal(CheckpointOrigin.SavePoint, back.Origin);
    }

    /// <summary>
    /// <b>It is prose in the body, not an encoded field</b> — §4.1's escape hatch is a stated value of
    /// this whole feature, and somebody reading the message with <c>git log</c> has to find the note as
    /// ordinary text under the title rather than as an escaped one-liner in a trailer.
    /// </summary>
    [Fact]
    public void TheNoteReadsAsProseUnderTheTitle()
    {
        string message = CommitMessage.Build("Output match retuned", null,
                                             "Series inductor moved to the drain side.");

        string[] lines = message.Split('\n');
        Assert.Equal("Output match retuned", lines[0]);
        Assert.Equal("",                     lines[1]);
        Assert.Equal("Series inductor moved to the drain side.", lines[MessageNotes.FirstLine]);
    }

    // ══ 3. What is recorded comes back, through the panel the designer reads ══════════════════════

    /// <summary>
    /// <b>End to end on a version</b>: the dialog's second field reaches the panel's expander, and the
    /// row says there is one to open.
    /// </summary>
    [GitFact]
    public void AVersionKeepsItsNoteAndThePanelShowsIt()
    {
        using var state = new AppDataRootScope();
        using var ws    = Armed();

        var service = new WorkspaceHistoryService(new Sink());
        service.NoteWorkspaceWrite();
        ws.Write("cells/Amp/thing.csch", "the first state");

        const string note = "Series inductor moved to the drain side.\n"
                          + "\n"
                          + "The 3.9 pF shunt measured 0.4 dB better than the 4.7 pF.";

        Assert.True(service.KeepVersion(ws.Root, "Output match retuned", note: note).Ok);

        var entry = service.Entries(ws.Root, HistoryFilter.Default).Rows.First(r => r.IsVersion);
        Assert.Equal(note, entry.Note);
        Assert.True(entry.HasNote);

        var row = new HistoryRowItem(entry, DateTimeOffset.UtcNow, showAuthor: false);
        Assert.True(row.HasNote);
        Assert.Equal(note, row.Note);

        // The row still shows the TITLE and nothing of the paragraph: R-rc10-14's split is what keeps
        // the list scannable, and a paragraph in a row is not a list.
        Assert.Equal("Output match retuned", row.Title);
    }

    /// <summary>The same, on the other boundary a designer asks for.</summary>
    [GitFact]
    public void ASavePointKeepsItsNote()
    {
        using var state = new AppDataRootScope();
        using var ws    = Armed();

        var service = new WorkspaceHistoryService(new Sink());
        service.NoteWorkspaceWrite();
        ws.Write("cells/Amp/thing.csch", "halfway through");

        Assert.True(service.TakeSavePoint(ws.Root, "Widen the output match",
                                          "The input side is untouched and 2.4 GHz still fails."));

        var point = RestorePoints.List(ws.Git()).Single();
        Assert.Equal("Widen the output match", point.Label);
        Assert.Equal("The input side is untouched and 2.4 GHz still fails.", point.Note);
    }

    // ══ 4. Every rebuild carries it through ═══════════════════════════════════════════════════════

    /// <summary>
    /// <b>Marking an entry <i>keep</i> must not delete its note.</b> That operation rebuilds the whole
    /// message over the identical tree, so anything it does not write back is gone — and this is the
    /// failure that would never be reported as a bug, because nobody watches a paragraph disappear.
    /// </summary>
    [GitFact]
    public void MarkingAnEntryKeepLeavesItsNoteAlone()
    {
        using var state = new AppDataRootScope();
        using var ws    = Armed();
        var git = ws.Git();

        ws.Write("cells/Amp/thing.csch", "before the assistant");
        var taken = WorkspaceCheckpoints.Take(git, CheckpointOrigin.BeforeBatch, "retune the match",
                                              attended: false, forceRecord: true,
                                              note: "The assistant was asked for the 3.5 GHz band only.");
        Assert.True(taken.Recorded);

        var before = RestorePoints.List(git).Single();
        Assert.Equal("The assistant was asked for the 3.5 GHz band only.", before.Note);

        Assert.True(RestorePoints.MarkKept(git, before).Ok);

        var after = RestorePoints.List(git).Single();
        Assert.True(after.Kept);
        Assert.Equal(before.Note,  after.Note);
        Assert.Equal(before.Label, after.Label);
        Assert.Equal(before.TreeId, after.TreeId);
    }

    /// <summary>
    /// <b>A correction that touches only the note is still a correction.</b> The refusal that short-cuts
    /// an unchanged label must look at both halves, or editing a paragraph reports success and writes
    /// nothing — which is the shape of "I edited it and it did not save".
    /// </summary>
    [GitFact]
    public void RenamingCanChangeTheNoteAlone()
    {
        using var state = new AppDataRootScope();
        using var ws    = Armed();
        var git = ws.Git();

        ws.Write("cells/Amp/thing.csch", "halfway through");
        Assert.True(WorkspaceCheckpoints.Take(git, CheckpointOrigin.SavePoint, "Widen the match",
                                              note: "First attempt.").Recorded);

        var before = RestorePoints.List(git).Single();

        var renamed = RestorePoints.Rename(git, before, before.Label,
                                           "First attempt, and the band edge is the part that failed.");
        Assert.True(renamed.Ok, renamed.Diagnostic?.Render());

        var after = RestorePoints.List(git).Single();
        Assert.Equal("Widen the match", after.Label);
        Assert.Equal("First attempt, and the band edge is the part that failed.", after.Note);
        Assert.Equal(before.Sequence, after.Sequence);
        Assert.Equal(before.TreeId,   after.TreeId);

        // And an empty note REMOVES it — the way somebody takes back a paragraph they did not mean.
        Assert.True(RestorePoints.Rename(git, after, after.Label, "").Ok);
        Assert.Equal("", RestorePoints.List(git).Single().Note);
    }

    // ══ 5. §5.11's three cases all carry both halves ══════════════════════════════════════════════

    /// <summary>
    /// §5.11 case (b) with §5.12's second field. <b>Both halves are replaced and every trailer
    /// survives</b> — including R-rc7-6's restored-from pair, which this operation must not know about
    /// in order to preserve.
    /// </summary>
    [GitFact]
    public void CorrectingAnUnsharedVersionReplacesBothHalves()
    {
        using var state = new AppDataRootScope();
        using var ws    = Armed();
        var git = ws.Git();

        ws.Write("cells/Amp/thing.csch", "the first state");
        Assert.True(WorkspaceCommit.Commit(git, "Ouptut match retunned",
                                           note: "Sereis inductor moved.").Ok);

        var before = HistoryBrowser.Versions(git).Single();
        Assert.Equal("Sereis inductor moved.", before.Note);

        var outcome = VersionCorrections.CorrectTitle(
            git, before, "Output match retuned",
            "Series inductor moved to the drain side; the 3.9 pF shunt won by 0.4 dB.");
        Assert.True(outcome.Ok, outcome.Diagnostic.Render());

        var after = HistoryBrowser.Versions(git).Single();
        Assert.Equal("Output match retuned", after.Title);
        Assert.Equal("Series inductor moved to the drain side; the 3.9 pF shunt won by 0.4 dB.",
                     after.Note);

        // R-rc11-10: not restamped, and the same state.
        Assert.Equal(before.TreeId,  after.TreeId);
        Assert.Equal(before.WhenUtc, after.WhenUtc);

        // R-rc11-4: circuitRF's own explanation and its origin trailer are still there, so the entry
        // still says how it came about.
        string message = ws.Raw("log", "-1", "--format=%B", after.CommitId).Out;
        Assert.Contains(CommitMessage.ExplicitLine, message);
        Assert.Contains(CommitMessage.OriginKey + ": " + CommitMessage.ExplicitCommitOrigin, message);

        // Null means "leave it alone", which is what every caller that predates notes means.
        var titleOnly = VersionCorrections.CorrectTitle(git, after, "Output match retuned again");
        Assert.True(titleOnly.Ok, titleOnly.Diagnostic.Render());
        Assert.Equal(after.Note, HistoryBrowser.Versions(git).Single().Note);
    }

    /// <summary>
    /// §5.11 case (c) with §5.12's second field. <b>The annotation carries both halves</b> — its first
    /// line is the corrected title and the rest is the corrected note — and <b>a single-line annotation
    /// written before notes existed still means a title and no note</b>.
    /// </summary>
    [Fact]
    public void AnAnnotationCarriesBothHalvesAndAnOldOneStillMeansATitle()
    {
        var both = new VersionCorrection("Output match retuned",
                                         "Series inductor moved to the drain side.");
        Assert.Equal(both, VersionCorrection.Of(both.Text));

        var legacy = VersionCorrection.Of("Output match retuned");
        Assert.Equal("Output match retuned", legacy.Title);
        Assert.Equal("", legacy.Note);

        // Nothing at all is how a correction is taken back, and it must read as nothing rather than as
        // a correction to an empty string.
        Assert.True(new VersionCorrection("", "").IsEmpty);
    }

    /// <summary>
    /// The same, through the repository: a shared version takes a corrected note beside its original,
    /// the original is <b>not</b> altered, and the row shows the correction with the original one click
    /// away (R-rc11-13, R-rc11-14).
    /// </summary>
    [GitFact]
    public void ASharedVersionTakesACorrectedNoteAndKeepsTheOriginal()
    {
        using var state = new AppDataRootScope();
        using var ws    = Armed();
        var git = ws.Git();

        ws.Write("cells/Amp/thing.csch", "the first state");
        Assert.True(WorkspaceCommit.Commit(git, "Ouptut match retunned",
                                           note: "Sereis inductor moved.").Ok);

        var version = HistoryBrowser.Versions(git).Single();

        var outcome = VersionCorrections.Annotate(git, version, "Output match retuned",
                                                  "Series inductor moved to the drain side.");
        Assert.True(outcome.Ok, outcome.Diagnostic.Render());

        var correction = VersionCorrections.Annotations(git)[version.CommitId];
        Assert.Equal("Output match retuned", correction.Title);
        Assert.Equal("Series inductor moved to the drain side.", correction.Note);

        // THE ORIGINAL IS NOT ALTERED. A note attaches to a commit; it does not rewrite one.
        var unchanged = HistoryBrowser.Versions(git).Single();
        Assert.Equal(version.CommitId, unchanged.CommitId);
        Assert.Equal("Ouptut match retunned", unchanged.Title);
        Assert.Equal("Sereis inductor moved.", unchanged.Note);

        var entry = HistoryList.Read(git, HistoryFilter.Default).Rows.First(r => r.IsVersion);
        Assert.Equal("Series inductor moved to the drain side.", entry.Note);
        Assert.Equal("Sereis inductor moved.", entry.OriginalNote);
        Assert.True(entry.IsNoteCorrected);

        var row = new HistoryRowItem(entry, DateTimeOffset.UtcNow, showAuthor: false);
        Assert.Equal("It originally said: Sereis inductor moved.", row.OriginalNoteText);
    }

    /// <summary>
    /// <b>A correction to the note alone keeps the title it already had.</b> The annotation's first
    /// line is what stands in for the title wherever the version is listed, so an annotation written
    /// with no title would blank it — and the request itself is a real one: <c>history correct --note
    /// …</c> with no <c>--text</c>.
    /// </summary>
    [GitFact]
    public void CorrectingTheNoteAloneLeavesTheTitleSayingWhatItSaid()
    {
        using var state = new AppDataRootScope();
        using var ws    = Armed();
        var git = ws.Git();

        ws.Write("cells/Amp/thing.csch", "the first state");
        Assert.True(WorkspaceCommit.Commit(git, "Output match retuned").Ok);

        var version = HistoryBrowser.Versions(git).Single();

        var outcome = VersionCorrections.Annotate(git, version, null,
                                                  "The 3.9 pF shunt won by 0.4 dB.");
        Assert.True(outcome.Ok, outcome.Diagnostic.Render());

        var correction = VersionCorrections.Annotations(git)[version.CommitId];
        Assert.Equal("Output match retuned", correction.Title);
        Assert.Equal("The 3.9 pF shunt won by 0.4 dB.", correction.Note);

        var entry = HistoryList.Read(git, HistoryFilter.Default).Rows.First(r => r.IsVersion);
        Assert.Equal("Output match retuned", entry.Title);
        Assert.False(entry.IsCorrected);

        // And nothing at all still takes the whole annotation back.
        Assert.True(VersionCorrections.Annotate(git, version, null, "").Ok);
        Assert.Empty(VersionCorrections.Annotations(git));
    }

    // ══ 6. Finding it again ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The search reads the note</b> (§5.12). It is the half most worth searching: a title is four
    /// words somebody chose under pressure, and the paragraph under it is where they wrote down the
    /// part they would later go looking for.
    /// </summary>
    [GitFact]
    public void TheSearchFindsAnEntryByWhatItsNoteSays()
    {
        using var state = new AppDataRootScope();
        using var ws    = Armed();
        var git = ws.Git();

        ws.Write("cells/Amp/thing.csch", "the first state");
        Assert.True(WorkspaceCommit.Commit(
            git, "Output match retuned",
            note: "The 3.9 pF shunt measured 0.4 dB better than the 4.7 pF.").Ok);

        var found = HistoryList.Read(git, HistoryFilter.Default with { Search = "4.7 pF" });
        Assert.Contains(found.Rows, r => r.IsVersion);

        var missed = HistoryList.Read(git, HistoryFilter.Default with { Search = "6.8 pF" });
        Assert.DoesNotContain(missed.Rows, r => r.IsVersion);
    }

    // ══ 7. The headless spelling writes the same bytes ════════════════════════════════════════════

    /// <summary>
    /// §5.3d. <b>Every revision-control operation has a spelling on <c>history</c></b>, and a note
    /// written headlessly must be the same bytes the window writes — not a second encoding that agrees
    /// today.
    /// </summary>
    [GitFact]
    public void TheCliWritesTheSameNoteTheWindowDoes()
    {
        using var state = new AppDataRootScope();

        const string note = "Series inductor moved to the drain side.\n"
                          + "\n"
                          + "The 3.9 pF shunt measured 0.4 dB better than the 4.7 pF.";

        string windowMessage;
        using (var viaWindow = Armed())
        {
            var service = new WorkspaceHistoryService(new Sink());
            service.NoteWorkspaceWrite();
            viaWindow.Write("cells/Amp/thing.csch", "identical content\n");

            var kept = service.KeepVersion(viaWindow.Root, "Output match retuned", note: note);
            Assert.True(kept.Ok);
            windowMessage = viaWindow.Raw("log", "-1", "--format=%B", kept.Version!.CommitId).Out;
        }

        using var viaCli = Armed();
        viaCli.Write("cells/Amp/thing.csch", "identical content\n");

        var run = RunCli(viaCli, "history", "commit", viaCli.Root,
                         "--title", "Output match retuned", "--note", note);
        Assert.Equal(0, run.ExitCode);

        string cliCommit  = viaCli.Raw("rev-parse", "HEAD").Out.Trim();
        string cliMessage = viaCli.Raw("log", "-1", "--format=%B", cliCommit).Out;

        Assert.Equal(windowMessage, cliMessage);
        Assert.Equal(note, HistoryBrowser.Versions(viaCli.Git()).Single().Note);
    }

    // ══ 8. The field is as wide as the dialog ═════════════════════════════════════════════════════

    /// <summary>
    /// <b>The note field spans its dialog</b> (owner-reported, 2026-09-11: the Correct What You
    /// Wrote… field rendered a fraction of the dialog's width).
    ///
    /// <para><b>The theme sizes an Expander to its CONTENT and does not stretch the presenter inside
    /// its template</b>, so an empty multi-line TextBox asks for its minimum and gets it. Measured
    /// headlessly before and after rather than reasoned about: 438 px of a 620 px dialog before,
    /// 588 px after, with the field itself going 400 → 550. The two Keep dialogs measured full width
    /// all along and <b>only by accident</b> — a long <c>PlaceholderText</c> was padding out their
    /// desired width — so they carry the same class rather than keep depending on that.</para>
    ///
    /// <para>Gated by the class, not by a measurement: this project has no headless Avalonia host, and
    /// <c>Expander.fill</c> is the one definition of the three template-level setters the fix needs.
    /// A field that quietly loses it is the regression.</para>
    /// </summary>
    [Fact]
    public void TheNoteFieldSpansItsDialog()
    {
        Assert.Contains("<Style Selector=\"Expander.fill /template/ ContentPresenter\">",
                        RestorePointsTests.ReadSource("src/Ui/Styles/CircuitRfStyles.axaml"),
                        StringComparison.Ordinal);

        foreach (string dialog in (string[])
                 ["src/Ui/Views/Dialogs/CorrectWhatYouWroteDialog.axaml",
                  "src/Ui/Views/Dialogs/KeepThisVersionDialog.axaml",
                  "src/Ui/Views/Dialogs/KeepThisStateDialog.axaml"])
        {
            string xaml = RestorePointsTests.ReadSource(dialog);

            int at = xaml.IndexOf("x:Name=\"NoteExpander\"", StringComparison.Ordinal);
            Assert.True(at > 0, dialog + " no longer has a note field at all.");

            int end = xaml.IndexOf('>', at);
            Assert.Contains("Classes=\"fill\"", xaml[at..end], StringComparison.Ordinal);
        }
    }

    // ── the fixture ───────────────────────────────────────────────────────────────────────────────

    private static GitWorkspace Armed()
    {
        var ws = new GitWorkspace();
        File.WriteAllText(ws.GlobalConfig,
            "[user]\n\tname = A Designer\n\temail = designer@example.invalid\n");

        var armed = WorkspaceArming.Arm(ws.Root, CheckpointOrigin.SavePoint, true, null, true);
        Assert.True(armed.Armed);
        return ws;
    }

    /// <inheritdoc cref="CommitAndHistoryTests"/>
    private static (int ExitCode, string StdOut, string StdErr) RunCli(
        GitWorkspace ws, params string[] args)
    {
        string cliDir = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(
                typeof(NotesOnEntriesTests).Assembly)
            .First(a => a.Key == "CliDir").Value!;

        string dll = Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll"));
        Assert.True(File.Exists(dll), $"the CLI was not built beside these tests: {dll}");

        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory       = ws.Root,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        psi.ArgumentList.Add(dll);
        foreach (string a in args) psi.ArgumentList.Add(a);

        psi.Environment["GIT_CONFIG_GLOBAL"]   = ws.GlobalConfig;
        psi.Environment["GIT_CONFIG_SYSTEM"]   = Path.Combine(ws.HomeDir, "no-such-system-config");
        psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        psi.Environment["HOME"]                = ws.HomeDir;
        psi.Environment["USERPROFILE"]         = ws.HomeDir;
        psi.Environment["XDG_CONFIG_HOME"]     = ws.HomeDir;

        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }

    private sealed class Sink : IMessageSink
    {
        public List<string> Texts { get; } = [];

        public void Post(MessageLevel level, string text, string? filePath = null) => Texts.Add(text);
        public void Clear() => Texts.Clear();
    }
}

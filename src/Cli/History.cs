using CircuitRF.Design.Revision;
using CircuitRF.Design.Workspace;
using CircuitRF.Diagnostics;
using RfCore.Export;

namespace CircuitRF.Cli;

/// <summary>
/// <c>circuitrf history &lt;checkpoint|list|restore|commit|versions&gt;</c> — <b>every history
/// operation has a headless spelling</b> (<c>docs/design/revision-control.md</c> §5.3d, R-rc5-23,
/// RC-7 R-rc7-22).
///
/// <para><b>Why it exists at all.</b> The governing motive (§1.2) is a floor under an AI-authored
/// edit, and §1.2's agent is <b>out of process</b>: a safety net reachable only from a window is
/// worth nothing to it. rev 4 left every history operation on a window or an agent's tool call, which
/// breaks the rule <c>Authoring.cs</c> exists to hold — <b>an operation that lives only in a view
/// model is not a capability</b>, and <c>serve</c>'s tools are the CLI's verbs by construction.</para>
///
/// <para><b>This file contains no revision-control logic of its own</b>: argument parsing, refusals,
/// and reporting. Each noun calls the <c>src/Design</c> function the GUI's own command calls —
/// <see cref="WorkspaceCheckpoints.Take"/>, <see cref="RestorePoints.List"/>,
/// <see cref="WorkspaceRestore.Restore"/>, <see cref="WorkspaceCommit.Commit"/> — so the two cannot
/// diverge, and a source scan proves the view model kept no second copy.</para>
///
/// <para><b><c>commit</c> is the narrative half, and it refuses in the same four ways the window
/// does</b> (R-rc7-22): held, off, no history here, and nothing to keep. Those are circuitRF's own
/// refusals rather than git's — every git failure that reaches a caller is one of RC-3's translated
/// rows or is carried verbatim (R-rc7-19), and a sentence invented here would be a sentence the
/// window does not have.</para>
///
/// <para><b>The batch's open and close are deliberately NOT here</b> (§5.3d). They hold session
/// state, and a process that exits after one command cannot; they stay on <c>serve</c>, which is the
/// only surface that can. <c>history checkpoint --intent</c> IS what a batch's open takes, which is
/// why that flag exists on this verb.</para>
///
/// <para><b>No repository is created by accident</b> (R-rc3-8). RC-5's arming decides when one
/// appears; here it is an explicit flag, because a repository turning up in a folder because somebody
/// ran a command once is the surprise §0 forbids.</para>
/// </summary>
internal static class History
{
    public static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            JsonRun.Report(CliDiagnostics.HistoryNounRequired());
            return Usage();
        }

        string noun = args[0].ToLowerInvariant();
        JsonRun.Verb = "history " + noun;

        return noun switch
        {
            "checkpoint" => Checkpoint(args[1..]),
            "list"       => List(args[1..]),
            "restore"    => Restore(args[1..]),
            "commit"     => Commit(args[1..]),
            "versions"   => Versions(args[1..]),
            // RC-9 R-rc9-20. A build machine reproducing a signed-off result is the reason the pin
            // exists at all, so clone, pin and pin resolution have to be reachable with no display.
            "clone"      => Clone(args[1..]),
            "pins"       => Pins(args[1..]),
            "pin"        => Pin(args[1..], clear: false),
            "unpin"      => Pin(args[1..], clear: true),
            "fetch"      => Exchange(args[1..], send: false),
            "send"       => Exchange(args[1..], send: true),
            // RC-11 §5.3d. Every revision-control operation has a spelling on this verb, and §5.11's
            // corrections are operations: an agent that can write a careless intent into a checkpoint
            // label must be able to correct one, and a designer whose only interface is a terminal
            // must not have fewer of these than one with a window.
            "rename"     => Rename(args[1..]),
            "forget"     => Forget(args[1..]),
            "retitle"    => Retitle(args[1..]),
            "correct"    => Correct(args[1..]),
            "review"     => Review(args[1..]),
            _            => UnknownNoun(noun),
        };
    }

    /// <summary>
    /// §5.12's note, as the two argument helpers read it.
    ///
    /// <para><b>A field rather than a fifth out-parameter</b>, because the helpers already hand back
    /// four and every one of their callers would have to name a discard for a flag it does not take.
    /// It is cleared at the top of each helper and this process runs one verb, so there is nothing for
    /// it to leak into. <b>Null means "leave whatever is there alone"</b> — which is what a rename or a
    /// retitle with no <c>--note</c> means, and is why it is not an empty string.</para>
    /// </summary>
    private static string? _note;

    private static int UnknownNoun(string noun)
    {
        JsonRun.Report(CliDiagnostics.HistoryUnknownNoun(noun));
        return Usage();
    }

    // ── history checkpoint ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// An explicit save-point (R-rc5-4's second boundary), and the same call a batch's open makes.
    ///
    /// <para><b>It is ATTENDED</b> — somebody typed it — so the large-file guard's question belongs
    /// to the caller and nothing is silently left out. <c>--leave-out</c> is how a caller that has
    /// already asked passes the answer.</para>
    /// </summary>
    private static int Checkpoint(string[] args)
    {
        string?      workspace = null;
        string?      intent    = null;
        string?      note      = null;
        bool         create       = false;
        bool         unattended   = false;
        bool         includeLarge = false;
        List<string> leaveOut     = [];

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--intent" when i + 1 < args.Length:     intent = args[++i]; break;
                // §5.12's longer note — what the one-line intent has no room for.
                case "--note" when i + 1 < args.Length:       note = args[++i]; break;
                // The RC-3 spelling, kept so a caller that learned it still works.
                case "--message" when i + 1 < args.Length:    intent = args[++i]; break;
                case "--leave-out" when i + 1 < args.Length:  leaveOut.Add(args[++i]); break;
                case "--create-repository":                   create = true; break;
                // R-rc5-15a's other side, for a caller that genuinely has nobody to ask — a scheduled
                // run. Named rather than inferred: "is there a person here" is not something a
                // process can work out about itself.
                case "--unattended":                          unattended = true; break;
                // R-rc5-15's "Include it", as the flag that answers the question below.
                case "--include-large":                       includeLarge = true; break;
                default:
                    if (args[i].StartsWith('-'))
                        return JsonRun.Fail(CliDiagnostics.RunUnknownOption("history checkpoint", args[i]));
                    if (workspace is not null)
                        return JsonRun.Fail(CliDiagnostics.RunUnknownOption("history checkpoint", args[i]));
                    workspace = args[i];
                    break;
            }
        }

        if (Bind(workspace, "history checkpoint", create, out string root, out var git, out int failure) is false)
            return failure;

        // R-rc5-15, and the rule `new` and `import` already hold: anything the GUI's dialog would have
        // ASKED is a refusal naming the flag that answers it, never a guess. §9A.1 decides the
        // direction — including a file is irreversible — so an unanswered one stops the recording
        // rather than being quietly kept.
        if (!unattended && !includeLarge)
        {
            var unanswered = LargeFileGuard
                .Find(git!, RestorePoints.Newest(git!)?.TreeId)
                .Where(f => !leaveOut.Contains(f.RelativePath, StringComparer.Ordinal))
                .ToList();

            if (unanswered.Count > 0)
                return JsonRun.Fail(CliDiagnostics.HistoryLargeFilesNeedAnAnswer(
                    string.Join(", ", unanswered.Select(f => f.RelativePath)), unanswered.Count));
        }

        var outcome = WorkspaceCheckpoints.Take(
            git!, CheckpointOrigin.SavePoint, intent, attended: !unattended, exclusions: leaveOut,
            note: note);

        foreach (var d in outcome.Diagnostics)
            JsonRun.Report(d);

        JsonRun.History = new HistoryReportJson(
            Recorded: outcome.Recorded,
            Point:    outcome.Point is { } p ? ToJson(p) : null);

        if (outcome.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
            return JsonRun.Finish(1);

        Console.WriteLine(outcome.Recorded
            ? $"kept: {outcome.Point!.Sequence}  {outcome.Point.Label}"
            : "nothing changed");

        return JsonRun.Finish(0);
    }

    // ── history list ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>The panel's list, headless</b> (RC-10 R-rc10-21, R-rc10-22) — versions, restore points and
    /// off periods in one ordering, under the same filter and the same default the window opens on.
    ///
    /// <para><b>A verb that cannot express what the window shows means an agent and a designer are
    /// reading two different histories.</b> Before RC-10 this listed the restore points alone, so an
    /// agent asking what had happened to a workspace was told about the safety net and nothing about
    /// the versions somebody had deliberately kept.</para>
    ///
    /// <para><b>The default is the panel's default</b>, which is one value —
    /// <see cref="HistoryFilter.Default"/> — rather than two that agree today. Every entry somebody
    /// stated an intent for is shown and the workspace-close entries are not;
    /// <c>--include-automatic</c> is what reveals them.</para>
    ///
    /// <para><c>history versions</c> is unchanged and stays: it answers a different question,
    /// narrowly.</para>
    /// </summary>
    private static int List(string[] args)
    {
        string? workspace = null;
        int     limit     = 0;
        var     filter    = HistoryFilter.Default;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--limit" when i + 1 < args.Length && int.TryParse(args[i + 1], out int n):
                    limit = n; i++; break;

                // R-rc10-21's first flag. The panel's toggle, spelled for a caller with no panel.
                case "--include-automatic":
                case "--all":
                    filter = filter with { Automatic = true }; break;

                // Its second: which kinds, by the same five names the flyout uses.
                case "--kinds" when i + 1 < args.Length:
                    if (ParseKinds(args[++i], ref filter) is { } bad)
                        return JsonRun.Fail(CliDiagnostics.HistoryUnknownKind(bad));
                    break;

                // Its third. Over what a person wrote — titles, batch intents and the author.
                case "--search" when i + 1 < args.Length:
                    filter = filter with { Search = args[++i] }; break;

                default:
                    if (args[i].StartsWith('-'))
                        return JsonRun.Fail(CliDiagnostics.RunUnknownOption("history list", args[i]));
                    if (workspace is not null)
                        return JsonRun.Fail(CliDiagnostics.RunUnknownOption("history list", args[i]));
                    workspace = args[i];
                    break;
            }
        }

        if (Bind(workspace, "history list", create: false, out string root, out var git, out int failure) is false)
            return failure;

        // ONE query, the panel's (R-rc10-22). RC-6 R-rc6-4's tidied-away entries are in it, marked —
        // a list that omitted them would make "thins, never prunes" invisible to the one caller who
        // cannot open a panel to check.
        var result = HistoryList.Read(git!, filter);
        var rows   = limit > 0 && result.Rows.Count > limit
                   ? result.Rows.Take(limit).ToList()
                   : result.Rows;

        JsonRun.History = new HistoryReportJson(
            Points:   [.. rows.Where(r => r.Point is not null).Select(r => ToJson(r.Point!))],
            Versions: [.. rows.Where(r => r.Version is not null).Select(r => ToJson(r.Version!))]);

        if (rows.Count == 0)
        {
            JsonRun.Report(CliDiagnostics.HistoryNothingKeptYet(root));
            return JsonRun.Finish(0);
        }

        foreach (var row in rows)
            Console.WriteLine(HistoryList.Line(row));

        // R-rc10-10. A search whose answer is INCOMPLETE says so, and on stderr rather than in the
        // list: stdout is the answer, and a caller piping it into something must not find a sentence
        // among the rows.
        if (result.ThinnedMatchesNotShown > 0)
            JsonRun.Report(CliDiagnostics.HistoryThinnedAlsoMatch(result.ThinnedMatchesNotShown));

        return JsonRun.Finish(0);
    }

    /// <summary>
    /// <c>--kinds versions,save-points,ai-batches,automatic,tidied-away</c> — the five the flyout has,
    /// under the names a person would type. Anything named is on and everything else is off, so the
    /// flag SETS the view rather than adding to a default a caller cannot see.
    /// </summary>
    /// <returns>The first unknown name, or null when every one was understood.</returns>
    private static string? ParseKinds(string spec, ref HistoryFilter filter)
    {
        var next = new HistoryFilter(false, false, false, false, false, filter.Search);

        foreach (string raw in spec.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.Trim().ToLowerInvariant())
            {
                case "versions":    case "version":    next = next with { Versions   = true }; break;
                case "save-points": case "save-point": next = next with { SavePoints = true }; break;
                case "ai-batches":  case "ai-batch":   next = next with { AiBatches  = true }; break;
                case "automatic":                      next = next with { Automatic  = true }; break;
                case "tidied-away": case "tidied":     next = next with { TidiedAway = true }; break;
                case "":                                                                       break;
                default: return raw.Trim();
            }
        }

        filter = next;
        return null;
    }

    // ── history restore ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Puts the workspace back to one entry.
    ///
    /// <para><b>The state being replaced is recorded first, always</b> (R-rc5-12a) —
    /// <see cref="WorkspaceRestore"/> does that and there is no flag here to switch it off, because a
    /// restore that could discard an afternoon's work would be a second cliff rather than a safety
    /// net.</para>
    ///
    /// <para><b>Open documents are the GUI's half</b> (R-rc5-12b). Headless there is no window and no
    /// undo stack, so this leaves R-rc5-14's sentence on the record instead: what a restore covers,
    /// and what it deliberately does not.</para>
    /// </summary>
    private static int Restore(string[] args)
    {
        string? workspace = null;
        long?   sequence  = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--point" when i + 1 < args.Length && long.TryParse(args[i + 1], out long n):
                    sequence = n; i++; break;
                default:
                    if (args[i].StartsWith('-'))
                        return JsonRun.Fail(CliDiagnostics.RunUnknownOption("history restore", args[i]));
                    if (workspace is not null)
                        return JsonRun.Fail(CliDiagnostics.RunUnknownOption("history restore", args[i]));
                    workspace = args[i];
                    break;
            }
        }

        if (Bind(workspace, "history restore", create: false, out string root, out var git, out int failure) is false)
            return failure;

        if (sequence is not { } wanted)
        {
            JsonRun.Report(CliDiagnostics.HistoryRestoreNeedsAPoint());
            return Usage();
        }

        var point = RestorePoints.ListIncludingThinned(git!).FirstOrDefault(p => p.Sequence == wanted);
        if (point is null) return JsonRun.Fail(CliDiagnostics.HistoryNoSuchPoint(wanted));

        // R-rc6-4. A thinned entry is put back in the live list first — one reference update from the
        // journal — so that going back to it leaves a workspace whose history says where it came from.
        if (point.Thinned)
        {
            var back = RestorePoints.RestoreThinned(git!, point);
            if (!back.Ok) return JsonRun.Fail(back.Diagnostic!);
        }

        var result = WorkspaceRestore.Restore(git!, point);
        foreach (var d in result.Diagnostics) JsonRun.Report(d);

        JsonRun.History = new HistoryReportJson(
            Recorded:     result.Ok,
            Point:        ToJson(point),
            FilesWritten: result.FilesWritten,
            FilesRemoved: result.FilesRemoved);

        if (!result.Ok) return JsonRun.Finish(1);

        Console.WriteLine($"restored: {point.Sequence}  {point.Label}");
        Console.WriteLine(RestorePointMessages.RestoreReferenceCaveat(
            WorkspacePins.Survey(root).Any(p => p.Pin is not null)));
        Console.WriteLine(RestorePointMessages.RestoreLeavesResultsAlone);
        return JsonRun.Finish(0);
    }

    // ── history commit (RC-7 R-rc7-22) ────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>The explicit commit — the narrative half</b> (§5.2, R-rc7-1, R-rc7-22).
    ///
    /// <para><b>It calls the same <c>src/Design</c> function the Commit action calls</b> and holds no
    /// logic of its own, which is what gate 12 asserts by comparing the two byte for byte.</para>
    ///
    /// <para><b>The title is an argument, and a blank one is allowed</b> — the window's dialog lets a
    /// designer press the button with the field empty, and a verb that refused where the dialog does
    /// not would be a different operation wearing the same name.</para>
    /// </summary>
    private static int Commit(string[] args)
    {
        string?      workspace = null;
        string?      title     = null;
        string?      note      = null;
        List<string> leaveOut  = [];

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--title" when i + 1 < args.Length:      title = args[++i]; break;
                // §5.12's longer note — what the title has no room for.
                case "--note" when i + 1 < args.Length:       note = args[++i]; break;
                // The spelling anyone who has used a version-control tool will reach for first.
                case "--message" when i + 1 < args.Length:    title = args[++i]; break;
                case "-m" when i + 1 < args.Length:           title = args[++i]; break;
                case "--leave-out" when i + 1 < args.Length:  leaveOut.Add(args[++i]); break;
                default:
                    if (args[i].StartsWith('-'))
                        return JsonRun.Fail(CliDiagnostics.RunUnknownOption("history commit", args[i]));
                    if (workspace is not null)
                        return JsonRun.Fail(CliDiagnostics.RunUnknownOption("history commit", args[i]));
                    workspace = args[i];
                    break;
            }
        }

        if (Bind(workspace, "history commit", create: false, out string root, out var git, out int failure) is false)
            return failure;

        // R-rc7-2's second half, and the refusal the window makes at the same moment. HELD is checked
        // before anything is read: a repository circuitRF does not manage is not circuitRF's to write
        // to, and R-rc6-6 does not bend because somebody typed a command.
        if (!EnclosingRepository.Detect(git!).MayRecord)
            return JsonRun.Fail(HistoryMessages.CannotKeepAVersionHeld());

        // OFF is the workspace's own setting, which outranks the per-user preference — and headless
        // there is no per-user preference to fall back to but the documented default.
        var setting = WorkspaceRevisionSetting.Read(WorkspaceRevisionSetting.CwsPathFor(root));
        if (!RevisionArming.IsArmed(RevisionArming.KeepHistoryDefault, setting))
            return JsonRun.Fail(HistoryMessages.CannotKeepAVersionOff());

        var result = WorkspaceCommit.Commit(git!, title, leaveOut, note);
        foreach (var d in result.Diagnostics) JsonRun.Report(d);

        JsonRun.History = new HistoryReportJson(
            Recorded: result.Ok,
            Version:  result.Version is { } v ? ToJson(v) : null);

        if (result.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
            return JsonRun.Finish(1);

        Console.WriteLine(result.Ok
            ? $"kept version: {result.Version!.Title}"
            : "nothing changed");

        // R-rc7-21. §8.3's escape hatch, in plain language, beside the action a user would look for it
        // from — which headless is the verb that keeps a version. Said once, on a successful commit,
        // and never as an offer to do it.
        if (result.Ok) Console.WriteLine(HistoryMessages.RewritingIsYoursToDo);

        return JsonRun.Finish(0);
    }

    // ── history versions (RC-7 R-rc7-9) ───────────────────────────────────────────────────────────

    /// <summary>
    /// The versions a designer kept, newest first — <b>the narrative, which is a different list from
    /// <c>history list</c>'s safety net and is never merged with it</b> (R-rc7-9).
    ///
    /// <para>An off period appears as its own row with its reason (R-rc7-10), because rendering it as
    /// an ordinary interval between two versions is the false-belief failure in its purest form.</para>
    /// </summary>
    private static int Versions(string[] args)
    {
        string? workspace = null;
        string? compare   = null;
        int     limit     = 0;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--limit" when i + 1 < args.Length && int.TryParse(args[i + 1], out int n):
                    limit = n; i++; break;
                // R-rc7-11. What changed between this version and the one before it, at the
                // granularity of documents — which is the design layer's question.
                case "--changes" when i + 1 < args.Length:
                    compare = args[++i]; break;
                default:
                    if (args[i].StartsWith('-'))
                        return JsonRun.Fail(CliDiagnostics.RunUnknownOption("history versions", args[i]));
                    if (workspace is not null)
                        return JsonRun.Fail(CliDiagnostics.RunUnknownOption("history versions", args[i]));
                    workspace = args[i];
                    break;
            }
        }

        if (Bind(workspace, "history versions", create: false, out string root, out var git, out int failure) is false)
            return failure;

        var versions = HistoryBrowser.Versions(git!, limit);

        if (compare is { Length: > 0 } wanted)
        {
            var version = versions.FirstOrDefault(
                              v => v.CommitId.StartsWith(wanted, StringComparison.OrdinalIgnoreCase))
                       ?? HistoryBrowser.Versions(git!).FirstOrDefault(
                              v => v.CommitId.StartsWith(wanted, StringComparison.OrdinalIgnoreCase));

            if (version is null) return JsonRun.Fail(CliDiagnostics.HistoryNoSuchVersion(wanted));
            return ReportChanges(git!, version);
        }

        var rows = HistoryBrowser.Rows(versions, RestorePoints.ListIncludingThinned(git!));

        JsonRun.History = new HistoryReportJson(Versions: [.. versions.Select(ToJson)]);

        if (rows.Count == 0)
        {
            JsonRun.Report(CliDiagnostics.HistoryNoVersionsYet(root));
            return JsonRun.Finish(0);
        }

        foreach (var row in rows)
            Console.WriteLine(row.IsGap ? "        " + HistoryMessages.GapBetween(row.Gap!.FromUtc, row.Gap.ToUtc)
                                        : Describe(row.Version!));

        return JsonRun.Finish(0);
    }

    /// <summary>What one version changed against the one before it. An initial version has none to
    /// compare against and says so rather than printing an empty list.</summary>
    private static int ReportChanges(GitCommand git, HistoryVersion version)
    {
        var parent = git.Run(["rev-parse", "--verify", "--quiet", version.CommitId + "^"],
                             new GitRunOptions(ReadOnly: true));

        var changes = parent.Ok && parent.Line.Length > 0
                    ? HistoryBrowser.Compare(git, parent.Line, version.CommitId)
                    : [];

        JsonRun.History = new HistoryReportJson(
            Versions: [ToJson(version)],
            Changes:  [.. changes.Select(c => new DocumentChangeJson(c.RelativePath,
                                                                     c.Kind.ToString().ToLowerInvariant(),
                                                                     c.PreviousPath))]);

        Console.WriteLine(HistoryMessages.ComparisonIsByDocument);
        if (changes.Count == 0)
        {
            Console.WriteLine(HistoryMessages.NothingDiffers);
            return JsonRun.Finish(0);
        }

        foreach (var c in changes)
            Console.WriteLine($"  {c.Kind.ToString().ToLowerInvariant(),-8}  {c.RelativePath}"
                            + (c.PreviousPath is { } was ? $"  (was {was})" : ""));

        return JsonRun.Finish(0);
    }

    /// <summary>One line per version: when, what it was called, and — when it applies — what it was
    /// brought back from, which is the line R-rc7-6 exists for.</summary>
    private static string Describe(HistoryVersion v)
        => $"{v.WhenUtc.ToLocalTime():yyyy-MM-dd HH:mm}  {v.Title}"
         + (v.RestoredFrom is { } from ? $"  [brought back from '{from.Label}']" : "");

    // ── history rename / forget / retitle / correct / review (RC-11 §5.11, §5.3d) ─────────────────

    /// <summary>
    /// §5.11 case (a). <b>Renames a restore point</b> — a new parentless commit over the identical
    /// tree, one reference update, every trailer but the label preserved (R-rc11-3, R-rc11-4).
    ///
    /// <para>The entry is named by its SEQUENCE, as <c>history restore</c> names one: §5.6 rule 2's
    /// number is the entry's own identity in every headless surface, and the wall clock is the label a
    /// human reads and never what decides which entry is which.</para>
    /// </summary>
    private static int Rename(string[] args)
    {
        if (Entry(args, "history rename", out long sequence, out string? text,
                  out var git, out int failure, takesNote: true) is false)
            return failure;

        var point = RestorePoints.ListIncludingThinned(git!).FirstOrDefault(p => p.Sequence == sequence);
        if (point is null) return JsonRun.Fail(CliDiagnostics.HistoryNoSuchPoint(sequence));

        var outcome = RestorePoints.Rename(git!, point, text, _note);
        JsonRun.Report(outcome.Diagnostic ?? HistoryMessages.RestorePointRenamed(text ?? ""));
        if (!outcome.Ok) return JsonRun.Finish(1);

        JsonRun.History = new HistoryReportJson(Recorded: true, Point: ToJson(point with { Label = text! }));
        Console.WriteLine($"renamed: {point.Sequence}  {text}");
        return JsonRun.Finish(0);
    }

    /// <summary>
    /// §5.11 case (a). <b>Lets a restore point go — and it goes exactly where retention sends one</b>
    /// (R-rc11-5): journalled, still listed under <i>tidied away</i>, brought back by
    /// <c>history restore</c>, and freeing nothing until an explicit reclaim.
    ///
    /// <para><b>There is no <c>--force</c> and nothing to confirm</b>, because nothing is destroyed. A
    /// flag here would say otherwise, and the one thing this whole brief must not do is imply an
    /// erasure it does not perform.</para>
    /// </summary>
    private static int Forget(string[] args)
    {
        if (Entry(args, "history forget", out long sequence, out _, out var git, out int failure,
                  needsText: false) is false)
            return failure;

        var point = RestorePoints.ListIncludingThinned(git!).FirstOrDefault(p => p.Sequence == sequence);
        if (point is null) return JsonRun.Fail(CliDiagnostics.HistoryNoSuchPoint(sequence));

        var outcome = RestorePoints.Forget(git!, point);
        JsonRun.Report(outcome.Diagnostic ?? HistoryMessages.RestorePointLetGo(point.Label));
        if (!outcome.Ok) return JsonRun.Finish(1);

        JsonRun.History = new HistoryReportJson(Recorded: true, Point: ToJson(point with { Thinned = true }));
        Console.WriteLine($"tidied away: {point.Sequence}  {point.Label}");
        return JsonRun.Finish(0);
    }

    /// <summary>
    /// §5.11 case (b). <b>Corrects the newest unshared version's title</b> (R-rc11-7), and refuses by
    /// naming why on anything else — a shared version, or an older one whose correction would rewrite
    /// every version after it. Both refusals offer <c>history correct</c>, which applies to any version
    /// at all.
    /// </summary>
    private static int Retitle(string[] args)
    {
        if (Version(args, "history retitle", out string? which, out string? text,
                    out var git, out int failure) is false)
            return failure;

        if (Find(git!, which) is not { } version)
            return JsonRun.Fail(CliDiagnostics.HistoryNoSuchVersion(which ?? ""));

        var outcome = VersionCorrections.CorrectTitle(git!, version, text, _note);
        JsonRun.Report(outcome.Diagnostic);
        if (!outcome.Ok) return JsonRun.Finish(1);

        JsonRun.History = new HistoryReportJson(Recorded: true, Version: ToJson(outcome.Version!));
        Console.WriteLine($"retitled: {outcome.Version!.Title}");
        return JsonRun.Finish(0);
    }

    /// <summary>
    /// §5.11 case (c). <b>Adds a correction to a version without altering it</b> (R-rc11-13) — shown in
    /// place of the original with the original still there, and carried alongside the version when it
    /// is sent.
    ///
    /// <para>An empty <c>--text</c> takes the correction back, which is not an erasure of anything: the
    /// original was never altered, so removing the annotation puts the original wording back in
    /// front.</para>
    /// </summary>
    private static int Correct(string[] args)
    {
        if (Version(args, "history correct", out string? which, out string? text,
                    out var git, out int failure, needsText: false) is false)
            return failure;

        if (Find(git!, which) is not { } version)
            return JsonRun.Fail(CliDiagnostics.HistoryNoSuchVersion(which ?? ""));

        var outcome = VersionCorrections.Annotate(git!, version, text, _note);
        JsonRun.Report(outcome.Diagnostic);
        if (!outcome.Ok) return JsonRun.Finish(1);

        JsonRun.History = new HistoryReportJson(Recorded: true, Version: ToJson(version));
        Console.WriteLine(text is { Length: > 0 } ? $"corrected: {text}" : "correction removed");

        // R-rc11-14, headless. The sentence that may not be softened, said where a caller with no
        // dialog would otherwise never meet it — and a caller with no dialog is very often an agent
        // acting for somebody who believes a correction removes something.
        Console.WriteLine(HistoryMessages.ACorrectionDoesNotErase);
        return JsonRun.Finish(0);
    }

    /// <summary>
    /// §5.11's review (R-rc11-16, §12 Q36) — <b>the version titles one journey would take off this
    /// machine</b>, from the one function the three window journeys call.
    ///
    /// <para>It reads and writes nothing, so it runs on a read-only tree and on a workspace another
    /// process has open, and it is the one noun here a build machine can put in front of a publishing
    /// step of its own.</para>
    /// </summary>
    private static int Review(string[] args)
    {
        string? workspace = null;
        var     journey   = LeavingJourney.Send;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--send":    journey = LeavingJourney.Send;    break;
                case "--copy":    journey = LeavingJourney.Copy;    break;
                case "--archive": journey = LeavingJourney.Archive; break;
                default:
                    if (args[i].StartsWith('-'))
                        return JsonRun.Fail(CliDiagnostics.RunUnknownOption("history review", args[i]));
                    if (workspace is not null)
                        return JsonRun.Fail(CliDiagnostics.RunUnknownOption("history review", args[i]));
                    workspace = args[i];
                    break;
            }
        }

        if (Bind(workspace, "history review", create: false, out _, out var git, out int failure) is false)
            return failure;

        var titles = TitlesLeaving.For(git!, journey);

        JsonRun.History = new HistoryReportJson(
            Versions: [.. titles.Select(t => new VersionJson(
                t.CommitId,
                t.WhenUtc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                t.Shown, t.Who, null))]);

        Console.WriteLine(HistoryMessages.TitlesLeaving(titles.Count, TitlesLeaving.Describe(journey)));
        foreach (var t in titles) Console.WriteLine("  " + TitlesLeaving.Line(t));

        return JsonRun.Finish(0);
    }

    /// <summary>
    /// The two nouns that name a restore point: a workspace, a <c>--point</c> sequence and, for a
    /// rename, the words. Shared so the two cannot parse their arguments differently.
    /// </summary>
    private static bool Entry(string[] args, string verb, out long sequence, out string? text,
                              out GitCommand? git, out int failure, bool needsText = true,
                              bool takesNote = false)
    {
        string? workspace = null;
        long?   wanted    = null;
        text    = null;
        git     = null;
        failure = 0;
        sequence = 0;
        _note    = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--point" when i + 1 < args.Length && long.TryParse(args[i + 1], out long n):
                    wanted = n; i++; break;
                case "--label" when i + 1 < args.Length: text   = args[++i]; break;
                case "--text"  when i + 1 < args.Length: text   = args[++i]; break;
                // §5.12. The paragraph the label has no room for, on the surface an agent has. A verb
                // that could write one and not correct one would leave the terminal with fewer of
                // these operations than the window has, which is §5.3d's whole rule.
                case "--note"  when takesNote && i + 1 < args.Length: _note = args[++i]; break;
                default:
                    if (args[i].StartsWith('-') || workspace is not null)
                    { failure = JsonRun.Fail(CliDiagnostics.RunUnknownOption(verb, args[i])); return false; }
                    workspace = args[i];
                    break;
            }
        }

        if (Bind(workspace, verb, create: false, out _, out git, out failure) is false) return false;

        if (wanted is not { } number)
        {
            JsonRun.Report(CliDiagnostics.HistoryRestoreNeedsAPoint());
            failure = Usage();
            return false;
        }

        if (needsText && text is not { Length: > 0 })
        {
            JsonRun.Report(HistoryMessages.ACorrectionNeedsWords());
            failure = Usage();
            return false;
        }

        sequence = number;
        return true;
    }

    /// <summary>The same, for the two nouns that name a version.</summary>
    private static bool Version(string[] args, string verb, out string? which, out string? text,
                                out GitCommand? git, out int failure, bool needsText = true)
    {
        string? workspace = null;
        which   = null;
        text    = null;
        git     = null;
        failure = 0;
        _note   = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--version" when i + 1 < args.Length: which = args[++i]; break;
                case "--title"   when i + 1 < args.Length: text  = args[++i]; break;
                case "--text"    when i + 1 < args.Length: text  = args[++i]; break;
                // §5.12, and see Entry's note: the correction covers both halves of what a person
                // wrote, here as in the window.
                case "--note"    when i + 1 < args.Length: _note = args[++i]; break;
                default:
                    if (args[i].StartsWith('-') || workspace is not null)
                    { failure = JsonRun.Fail(CliDiagnostics.RunUnknownOption(verb, args[i])); return false; }
                    workspace = args[i];
                    break;
            }
        }

        if (Bind(workspace, verb, create: false, out _, out git, out failure) is false) return false;

        if (needsText && text is not { Length: > 0 })
        {
            JsonRun.Report(HistoryMessages.ACorrectionNeedsWords());
            failure = Usage();
            return false;
        }

        return true;
    }

    /// <summary>
    /// The version an identity names, or <b>the newest when none was given</b> — which is the entry
    /// §5.11 case (b) is about, so <c>history retitle</c> with no <c>--version</c> does the thing
    /// almost every caller means.
    /// </summary>
    private static HistoryVersion? Find(GitCommand git, string? which)
    {
        var versions = HistoryBrowser.Versions(git);
        if (versions.Count == 0) return null;

        return which is { Length: > 0 } wanted
            ? versions.FirstOrDefault(v => v.CommitId.StartsWith(wanted, StringComparison.OrdinalIgnoreCase))
            : versions[0];
    }

    // ── history clone (RC-9 R-rc9-1, R-rc9-20) ────────────────────────────────────────────────────

    /// <summary>
    /// Copies a workspace here from wherever git can reach it.
    ///
    /// <para><b>Both positions are required</b> — git would derive a folder name from the address and
    /// circuitRF will not: a folder appearing somewhere the caller did not name is the surprise §0
    /// forbids, and on a build machine there is nobody to notice it.</para>
    ///
    /// <para><b>It never asks for a credential and it never blocks on one</b> (§9.1, R-rc9-7a).
    /// circuitRF supplies whatever git is already configured to supply; an operation that would have
    /// asked <b>refuses</b>, which is what <c>GIT_TERMINAL_PROMPT=0</c> buys and is the whole
    /// difference between a sentence and a process that never returns.</para>
    /// </summary>
    private static int Clone(string[] args)
    {
        string? source      = null;
        string? destination = null;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith('-'))
                return JsonRun.Fail(CliDiagnostics.RunUnknownOption("history clone", args[i]));
            if (source is null)           source      = args[i];
            else if (destination is null) destination = args[i];
            else return JsonRun.Fail(CliDiagnostics.RunUnknownOption("history clone", args[i]));
        }

        if (source is null || destination is null)
        {
            JsonRun.Report(CliDiagnostics.CloneNeedsSourceAndDestination());
            return Usage();
        }

        if (GitDiscovery.Find(out _) is not { } installation)
            return JsonRun.Fail(CliDiagnostics.HistoryNoGit());

        var result = WorkspaceClone.Clone(installation, source, destination);
        foreach (var d in result.Diagnostics) JsonRun.Report(d);

        JsonRun.History = new HistoryReportJson(
            Copy: new CloneJson(result.Destination, result.WorkspaceCwsPath, RestorePoints: false));

        if (!result.Ok) return JsonRun.Finish(1);

        Console.WriteLine(result.IsWorkspace
            ? $"copied: {result.Destination}"
            : $"copied (not a workspace): {result.Destination}");
        return JsonRun.Finish(0);
    }

    // ── history pins / pin / unpin (RC-9 R-rc9-8, R-rc9-9, R-rc9-12) ──────────────────────────────

    /// <summary>
    /// Which version of each referenced workspace this design is built against.
    ///
    /// <para><b>This is the noun a build machine reads</b>, and it is why RC-9 has a headless spelling
    /// at all: reproducing a signed-off result means resolving the same library content, and the only
    /// thing that says which content that is, is the pin.</para>
    /// </summary>
    private static int Pins(string[] args)
    {
        string? workspace = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith('-'))
                return JsonRun.Fail(CliDiagnostics.RunUnknownOption("history pins", args[i]));
            if (workspace is not null)
                return JsonRun.Fail(CliDiagnostics.RunUnknownOption("history pins", args[i]));
            workspace = args[i];
        }

        if (BindWorkspace(workspace, "history pins", out string root, out int failure) is false)
            return failure;

        var states = WorkspacePins.Survey(root);

        JsonRun.History = new HistoryReportJson(Pins: [.. states.Select(ToJson)]);

        if (states.Count == 0)
        {
            JsonRun.Report(CliDiagnostics.NoWorkspaceReferencesHere(root));
            return JsonRun.Finish(0);
        }

        // R-rc9-16. An unhonourable pin is an ERROR and the exit code says so: a build machine that
        // treated it as a warning would produce a result against content the design was never verified
        // against — which is the one outcome the pin exists to prevent.
        bool broken = false;
        foreach (var state in states)
        {
            if (state.Diagnostic is { } d) JsonRun.Report(d);
            if (state.Status == PinStatus.CannotBeHonoured) broken = true;
            Console.WriteLine(Describe(state));
        }

        return JsonRun.Finish(broken ? 1 : 0);
    }

    private static string Describe(PinState s)
    {
        string what = s.Status switch
        {
            PinStatus.Unpinned         => "follows the newest",
            PinStatus.Current          => "newest",
            PinStatus.NewerAvailable   => "a newer one is available",
            PinStatus.CannotBeHonoured => "CANNOT BE REACHED",
            _                          => "keeps no history",
        };
        return $"{s.Alias,-24}  {s.Pin ?? "-",-14}  {what}";
    }

    /// <summary>
    /// Fixes this design to one version of a referenced workspace, moves it to another, or lets it go.
    ///
    /// <para><b>The pin is on the ALIAS</b> (R-rc9-9) — there is deliberately no way to pin a cell, and
    /// no <c>--cell</c> flag to add one later. One referenced workspace is one repository with one
    /// commit identity, and per-cell pinning would allow one design to reference two mutually
    /// inconsistent versions of one library: a state nobody wants and nothing detects.</para>
    ///
    /// <para><b>Moving it is a human decision and this is not a resolver</b> (R-rc9-17). No version
    /// ranges, no transitive constraints, nothing that picks a version on the caller's behalf: with no
    /// <c>--to</c>, it takes the newest, which is what the designer's own "take the newer version"
    /// action does.</para>
    /// </summary>
    private static int Pin(string[] args, bool clear)
    {
        string  verb      = clear ? "history unpin" : "history pin";
        string? workspace = null;
        string? alias     = null;
        string? to        = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--alias" when i + 1 < args.Length: alias = args[++i]; break;
                case "--to" when i + 1 < args.Length && !clear: to = args[++i]; break;
                default:
                    if (args[i].StartsWith('-'))
                        return JsonRun.Fail(CliDiagnostics.RunUnknownOption(verb, args[i]));
                    if (workspace is not null)
                        return JsonRun.Fail(CliDiagnostics.RunUnknownOption(verb, args[i]));
                    workspace = args[i];
                    break;
            }
        }

        if (BindWorkspace(workspace, verb, out string root, out int failure) is false) return failure;

        if (alias is not { Length: > 0 })
        {
            JsonRun.Report(CliDiagnostics.PinNeedsAnAlias());
            return Usage();
        }

        var change = clear
            ? WorkspacePins.Unpin(root, alias)
            : WorkspacePins.Pin(root, alias, to);

        JsonRun.Report(change.Diagnostic);
        JsonRun.History = new HistoryReportJson(
            Recorded: change.Written,
            Pins:     [.. WorkspacePins.Survey(root).Select(ToJson)]);

        if (!change.Ok) return JsonRun.Finish(1);

        // R-rc9-13. The write IS the point: it lands in this workspace's own history with a date and an
        // author, which is what later answers "when did this design start using the new library?".
        Console.WriteLine(change.Written
            ? (change.Pin is null ? $"unpinned: {alias}" : $"pinned: {alias} -> {change.Pin}")
            : "nothing changed");

        return JsonRun.Finish(0);
    }

    private static PinJson ToJson(PinState s) => new(
        s.Alias,
        s.Pin,
        s.Status switch
        {
            PinStatus.Unpinned         => "unpinned",
            PinStatus.Current          => "current",
            PinStatus.NewerAvailable   => "newer-available",
            PinStatus.CannotBeHonoured => "cannot-be-honoured",
            _                          => "no-history-there",
        },
        s.Newest);

    // ── history fetch / send (RC-9 R-rc9-6, R-rc9-7) ──────────────────────────────────────────────

    /// <summary>
    /// Exchanges versions with the other copy this workspace came from.
    ///
    /// <para><b>Explicit, always</b> (R-rc9-6). Nothing in this series contacts a network without being
    /// asked, and there is no automatic caller of either direction anywhere — gate 10 asserts it. An
    /// automatic fetch would silently change what a design resolves against, which is the failure §7A.4
    /// is written to prevent.</para>
    ///
    /// <para><b>A fetch applies nothing to the working tree</b>, so it cannot surprise a document that
    /// is open, and there is no merge in this series at all: the five design formats are marked
    /// unmergeable and resolution is whole-file, pick a side.</para>
    /// </summary>
    private static int Exchange(string[] args, bool send)
    {
        string  verb      = send ? "history send" : "history fetch";
        string? workspace = null;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith('-'))
                return JsonRun.Fail(CliDiagnostics.RunUnknownOption(verb, args[i]));
            if (workspace is not null)
                return JsonRun.Fail(CliDiagnostics.RunUnknownOption(verb, args[i]));
            workspace = args[i];
        }

        if (Bind(workspace, verb, create: false, out string root, out var git, out int failure) is false)
            return failure;

        var result = send ? WorkspaceRemotes.Push(git!) : WorkspaceRemotes.Fetch(git!);
        foreach (var d in result.Diagnostics) JsonRun.Report(d);

        JsonRun.History = new HistoryReportJson(
            Exchange: new ExchangeJson(result.Remote, result.Changed));

        if (!result.Ok) return JsonRun.Finish(1);

        Console.WriteLine(result.Changed ? "changed" : "nothing changed");
        return JsonRun.Finish(0);
    }

    // ── shared ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves the workspace and binds a driver to it, or reports why not.
    ///
    /// <para><b>R-rc3-3's silence, expressed as a refusal rather than as nothing at all</b>: a caller
    /// who TYPED this command asked, so an absent git is reported. Nothing automatic reaches this
    /// path.</para>
    /// </summary>
    private static bool Bind(string? workspace, string verb, bool create,
                             out string root, out GitCommand? git, out int failure)
    {
        root    = "";
        git     = null;
        failure = 0;

        if (workspace is null)
        {
            JsonRun.Report(CliDiagnostics.InputRequired(verb, "workspace folder"));
            failure = Usage();
            return false;
        }

        root = Path.GetFullPath(workspace);
        JsonRun.InputPath = root;

        if (!File.Exists(Path.Combine(root, WorkspacePersistence.FileName)))
        {
            failure = JsonRun.Fail(CliDiagnostics.HistoryNotAWorkspace(root));
            return false;
        }

        if (GitCommand.For(root) is not { } bound)
        {
            failure = JsonRun.Fail(CliDiagnostics.HistoryNoGit());
            return false;
        }

        if (create && !bound.IsRepositoryRoot())
        {
            var made = GitRepository.Create(bound, RevisionManagement.Created);
            if (!made.Ok) { failure = JsonRun.Fail(made.Diagnostic!); return false; }
        }

        if (!bound.IsRepositoryRoot())
        {
            failure = JsonRun.Fail(CliDiagnostics.HistoryNoRepository(root));
            return false;
        }

        // R-rc3-11a: a workspace that predates this feature gets circuitRF's policy files the moment
        // it has a repository, not only when it was created.
        WorkspacePolicyFiles.Ensure(root);

        git = bound;
        return true;
    }

    /// <summary>
    /// Resolves the workspace and nothing else — <b>for the nouns that need no repository at all.</b>
    /// The pin lives in the <c>.cws</c>, so listing or changing one works on a workspace circuitRF has
    /// never kept a history for; requiring a repository here would refuse the very case the pin is most
    /// useful in, which is a design that merely CONSUMES a library somebody else versions.
    /// </summary>
    private static bool BindWorkspace(string? workspace, string verb, out string root, out int failure)
    {
        root    = "";
        failure = 0;

        if (workspace is null)
        {
            JsonRun.Report(CliDiagnostics.InputRequired(verb, "workspace folder"));
            failure = Usage();
            return false;
        }

        root = Path.GetFullPath(workspace);
        JsonRun.InputPath = root;

        if (!File.Exists(Path.Combine(root, WorkspacePersistence.FileName)))
        {
            failure = JsonRun.Fail(CliDiagnostics.HistoryNotAWorkspace(root));
            return false;
        }

        return true;
    }

    /// <summary>The wire shape of one entry. <b>Built here and nowhere else</b>, so the terminal line
    /// and the document cannot describe different things.</summary>
    private static VersionJson ToJson(HistoryVersion v) => new(
        v.CommitId,
        v.WhenUtc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
        v.Title,
        v.Who,
        v.RestoredFrom?.Label,
        v.Note.Length > 0 ? v.Note : null);

    /// <inheritdoc cref="ToJson(HistoryVersion)"/>
    private static RestorePointJson ToJson(RestorePoint p) => new(
        p.Sequence,
        p.TakenUtc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
        CheckpointMessage.Spell(p.Origin),
        p.Label,
        p.Kept,
        p.LeftOut,
        p.Thinned,
        p.Note.Length > 0 ? p.Note : null);

    private static int Usage()
    {
        Console.Error.WriteLine(
            "usage: circuitrf history checkpoint <workspace> [--intent <text>] [--note <text>]\n"
          + "                                    [--leave-out <path>]\n"
          + "                                    [--include-large] [--unattended] [--create-repository]\n"
          + "       circuitrf history list <workspace> [--limit <n>] [--include-automatic]\n"
          + "                                  [--kinds versions,save-points,ai-batches,automatic,tidied-away]\n"
          + "                                  [--search <text>]\n"
          + "       circuitrf history restore <workspace> --point <number>\n"
          + "       circuitrf history commit <workspace> [--title <text>] [--note <text>]\n"
          + "                                  [--leave-out <path>]\n"
          + "       circuitrf history versions <workspace> [--limit <n>] [--changes <version>]\n"
          + "       circuitrf history clone <address> <folder>\n"
          + "       circuitrf history pins <workspace>\n"
          + "       circuitrf history pin <workspace> --alias <name> [--to <version>]\n"
          + "       circuitrf history unpin <workspace> --alias <name>\n"
          + "       circuitrf history fetch <workspace>\n"
          + "       circuitrf history send <workspace>\n"
          + "       circuitrf history rename <workspace> --point <number> --label <text> [--note <text>]\n"
          + "       circuitrf history forget <workspace> --point <number>\n"
          + "       circuitrf history retitle <workspace> [--version <id>] --title <text> [--note <text>]\n"
          + "       circuitrf history correct <workspace> [--version <id>] --text <text> [--note <text>]\n"
          + "       circuitrf history review <workspace> [--send | --copy | --archive]");
        return 2;
    }
}

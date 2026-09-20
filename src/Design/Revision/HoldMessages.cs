using CircuitRF.Diagnostics;

namespace CircuitRF.Design.Revision;

/// <summary>
/// <b>Every sentence RC-6 puts in front of a designer</b> — retention, the enclosing-repository hold,
/// and switching recording off (<c>docs/design/revision-control.md</c> §5.6, §5.7, §12 Q4;
/// R-rc6-3, R-rc6-8 … R-rc6-15).
///
/// <para><b>Diagnostics, not strings</b> (R-rc3-6), for the reason
/// <see cref="RestorePointMessages"/> gives: the Messages panel and the CLI render one wording from
/// one source, and the ids are what dedup and once-per-session suppression key on.</para>
///
/// <para><b>Three reports at three cadences, and the cadences ARE the design</b> (R-rc6-9). On
/// workspace open, one message saying what is not being kept, why, and <b>how to remedy it</b>. On an
/// attempt, a refusal saying why <b>without restating the remedy</b> — the user has been told once,
/// and repeating it on every attempt is how a message becomes noise. And a persistent, non-scrolling
/// indicator, which is the measure most likely to actually prevent the false belief: a scrolling log
/// is read once and then trained against.</para>
///
/// <para><b>No git vocabulary in any of them</b> (R-rc0-6). The one word that had to be named is
/// <c>.git</c> itself, in the sentence that says deleting that folder cannot harm the design — naming
/// the folder is the whole content of that reassurance.</para>
/// </summary>
public static class HoldMessages
{
    // ── Retention (§5.6) ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// R-rc6-3. <b>A pass that wanted to remove more than its share removed nothing, and said so.</b>
    ///
    /// <para>This is what converts a clock fault from silent data loss into a message that is both
    /// true and useful. It names the clock, because a wall clock is the only thing that can make a
    /// whole list look expired at once, and it says plainly that nothing was removed — a designer who
    /// reads "something is wrong" and cannot tell whether they have lost anything is worse off than
    /// one who was told nothing.</para>
    /// </summary>
    public static Diagnostic RetentionSweepRefused(int wanted, int total) => Diagnostic.Create(
        "revision.retention.refused",
        DiagnosticSeverity.Warning,
        "circuitRF was about to tidy away {wanted} of this workspace's {total} restore points at once, "
      + "which is far more than an ordinary tidy-up. Nothing was removed. This usually means "
      + "this machine's clock is wrong — check the date and time, and circuitRF will tidy up normally "
      + "once it is right.",
        ("wanted", (object?)wanted), ("total", total));

    /// <summary>
    /// The record of what was thinned could not be written, so the sweep stopped where it was.
    ///
    /// <para><b>Stopping is the point.</b> Thinning without journalling leaves a state that is still in
    /// the repository and no longer offered anywhere — recoverable only by somebody who knows
    /// <c>git fsck</c>, which is exactly the escape hatch §5.6 rule 4 refuses to rely on.</para>
    /// </summary>
    public static Diagnostic ThinningJournalUnwritable() => new(
        "revision.retention.journal-unwritable",
        DiagnosticSeverity.Warning,
        "circuitRF could not record what it was tidying away, so it stopped. Nothing has been lost and "
      + "every restore point is still listed.");

    // ── The hold (§12 Q4) ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// R-rc6-9, first cadence. <b>On workspace open: what is not being kept, why, and the remedy.</b>
    ///
    /// <para>The ancestor case. circuitRF may not write into somebody else's repository at all — an
    /// automatic recording there would sweep up work that was not circuitRF's to record — so there is
    /// nothing to offer and the remedy is the only one there is.</para>
    /// </summary>
    public static Diagnostic HeldByAncestorOnOpen(string repositoryRoot) => Diagnostic.Create(
        "revision.held.ancestor",
        DiagnosticSeverity.Warning,
        "circuitRF is not keeping a history of this workspace. It sits inside '{root}', which already "
      + "keeps a history of its own, and circuitRF will not record into somebody else's. To have "
      + "restore points here, move or copy this workspace to a folder outside '{root}'.",
        ("root", repositoryRoot));

    /// <summary>
    /// R-rc6-9, first cadence, for the workspace-root case — <b>which is a QUESTION, not a report</b>
    /// (R-rc6-7a). This is what is said when the question has been asked and answered
    /// <i>don't keep history</i>: the state, why, and where to change the answer.
    /// </summary>
    public static Diagnostic HeldByChoiceOnOpen() => new(
        "revision.held.declined",
        DiagnosticSeverity.Info,
        "circuitRF is not keeping a history of this workspace, because you asked it not to when this "
      + "workspace's own history was found. You can change that in Settings ▸ Revision Control.");

    /// <summary>
    /// R-rc6-9, first cadence, for the workspace-root case <b>when the question is not going to be
    /// asked</b> — because keeping a history is switched off here (owner-reported, 2026-09-19).
    ///
    /// <para><b>This row is silent in <see cref="EnclosingRepository.OpenReportFor"/> on purpose</b>:
    /// it is a QUESTION (R-rc6-7a), and a report about it would be answered by a dialog the designer
    /// is already looking at. Where that dialog is suppressed the reasoning inverts, and the one row
    /// whose explanation was left to a dialog becomes the one row with no dialog — a workspace held,
    /// permanently, behind an indicator that says so and names no way out. That is §1.4's false belief
    /// reached from the far side: not a designer who thinks they are protected, but one who cannot
    /// find out why they are not.</para>
    ///
    /// <para><b>It is the sentence a clone most often lands on.</b> Git does not clone configuration,
    /// so a copied workspace arrives with no management marker and is held from its first open — and a
    /// designer who had history switched off at the time is told nothing at all about it.</para>
    ///
    /// <para><b>A line, not a dialog.</b> The 2026-09-17 decision this respects was about a MODAL in
    /// front of somebody who had opted out; a message explaining an indicator they can already see is
    /// the opposite trade, and the indicator is visible whatever the preference says.</para>
    /// </summary>
    public static Diagnostic HeldPendingAnAnswer(string workspaceName) => Diagnostic.Create(
        "revision.held.pending-an-answer",
        DiagnosticSeverity.Info,
        "'{workspace}' already keeps a history of its own, and circuitRF is not writing to it. "
      + "Keeping a history is switched off, so you have not been asked what should happen to it — "
      + "turn it on in Settings ▸ Revision Control and circuitRF will ask.",
        ("workspace", workspaceName));

    /// <summary>
    /// R-rc6-9, second cadence. <b>A refusal, saying why and NOT restating the remedy.</b>
    ///
    /// <para>One id for every hold reason, deliberately: the panel's own dedup keys on the id, and a
    /// designer pressing the same button three times should see one line, not three.</para>
    /// </summary>
    public static Diagnostic HeldRefusal() => new(
        "revision.held.refused",
        DiagnosticSeverity.Error,
        "This workspace's history is not circuitRF's to write to, so nothing was kept.");

    /// <summary>
    /// R-rc6-7a. <b>The workspace-root case, put as a question</b> — with what keeping their own
    /// configuration costs stated at the point of choosing, not in a manual.
    ///
    /// <para><b>The two consequences that lead are recoverability ones, not tidiness ones</b>, because
    /// each silently revokes a guarantee circuitRF makes elsewhere: a fortnight instead of the promised
    /// weeks, and the escape hatch gone at thirty days.</para>
    /// </summary>
    public const string AdoptionQuestion =
        "This workspace already keeps a history of its own. Would you like circuitRF to look after it?";

    /// <inheritdoc cref="AdoptionQuestion"/>
    public const string AdoptionKeepingCost =
        "Keeping your own settings is a legitimate choice, and two of them change what circuitRF can "
      + "promise you:\n"
      + "• A restore point circuitRF tidies away is permanently destroyed after a fortnight, rather "
      + "than staying recoverable for as long as you leave it.\n"
      + "• The way back from a mistaken reset or rewrite of your own work disappears after thirty "
      + "days.\n"
      + "Your own history, and every change you have recorded, are untouched either way.";

    /// <summary>
    /// R-rc6-7a's line that must not be misread. <b>The answer governs repository settings only.</b>
    /// Bypassing hooks is not a setting anyone can keep, because it is not a property of the repository
    /// at all: an automatic recording must neither fire somebody's tooling nor be blocked by it.
    /// </summary>
    public const string AdoptionCoversSettingsOnly =
        "Either way, circuitRF records under the name in Settings ▸ Revision Control and never runs "
      + "the scripts your own tools attach to a change.";

    // ── Off, and what it is not (§5.7) ────────────────────────────────────────────────────────────

    /// <summary>
    /// R-rc6-11. Turning it off <b>stops writing and deletes nothing</b>, and the confirmation says so
    /// in those words — an off switch that quietly discarded a history would be the single most
    /// damaging control in the application.
    /// </summary>
    public static Diagnostic RecordingSwitchedOff(string workspaceName) => Diagnostic.Create(
        "revision.off.switched-off",
        DiagnosticSeverity.Info,
        "circuitRF has stopped keeping a history of '{workspace}'. Nothing was deleted: every restore "
      + "point already taken is still listed and can still be restored, and turning it back on carries "
      + "on where it left off.",
        ("workspace", workspaceName));

    /// <summary>R-rc6-11's other half — <b>off and on are symmetric</b>, and the resumption is recorded
    /// so the quiet stretch has two ends rather than one.</summary>
    public static Diagnostic RecordingSwitchedOn(string workspaceName) => Diagnostic.Create(
        "revision.off.switched-on",
        DiagnosticSeverity.Info,
        "circuitRF is keeping a history of '{workspace}' again. Every restore point from before is "
      + "still there, and the stretch in between is marked so you can see that nothing was recorded "
      + "then.",
        ("workspace", workspaceName));

    /// <summary>
    /// R-rc6-14c. <b>The flag travels</b>, so a workspace that arrives switched off is reported at the
    /// recipient's first boundary rather than silently doing nothing — with the setting one click away.
    /// </summary>
    public static Diagnostic ArrivedSwitchedOff(string workspaceName) => Diagnostic.Create(
        "revision.off.arrived-off",
        DiagnosticSeverity.Info,
        "'{workspace}' was set not to keep a history, and that setting travelled with it. circuitRF is "
      + "recording nothing here. Turn it on for this workspace in Settings ▸ Revision Control.",
        ("workspace", workspaceName));

    /// <summary>
    /// R-rc6-15. <b>An AI edit requested while recording is off says so before anything is modified,
    /// and offers to turn it back on.</b>
    ///
    /// <para>Switching revision control off for a workspace switches off the one checkpoint RC-4 makes
    /// non-switchable, because it is §1.2 — the reason the whole feature exists. That is a legitimate
    /// thing to choose and an illegitimate thing to stumble into, so the conflict is resolved out loud,
    /// at the moment it matters, and never silently.</para>
    /// </summary>
    public static Diagnostic AiEditWhileOff() => new(
        "revision.off.ai-edit-refused",
        DiagnosticSeverity.Warning,
        "This workspace does not keep a history, so circuitRF cannot keep a restore point before an "
      + "assistant changes anything and there would be nothing to fall back to. Nothing has been "
      + "changed. Turn history on for this workspace to go ahead.");

    /// <summary>The action offered beside <see cref="AiEditWhileOff"/> — the remedy OFFERED rather than
    /// merely named (§7A.3), which the Messages panel can carry.</summary>
    public const string TurnRecordingOnAction = "Turn history on for this workspace";

    /// <summary>
    /// R-rc6-17. <b>The single most calming thing that can be said to a designer nervous about letting
    /// version control near their work</b>, and it is a fact rather than a reassurance: the design was
    /// never inside git in any meaningful sense.
    /// </summary>
    public const string RemovingCannotHarmTheDesign =
        "Removing the history cannot harm your design. A workspace is ordinary files in a folder, and "
      + "the history lives beside them in one plainly-named folder called '.git' that holds only "
      + "copies. Delete it and the workspace opens exactly as it did, with every file present and "
      + "current.";

    // ── The persistent indicator (R-rc6-8, R-rc6-10) ──────────────────────────────────────────────

    /// <summary>
    /// <b>One indicator, three reasons, and the reason is in its text</b> (R-rc6-10). Held, off and a
    /// failure all mean the same thing to a designer — <i>nothing is being recorded right now</i> — and
    /// three separate indicators would be three things to notice.
    /// </summary>
    public static string IndicatorFor(RecordingState state) => state switch
    {
        RecordingState.Off    => "History off",
        RecordingState.Held   => "History held",
        RecordingState.Failed => "History failing",
        _                     => "",
    };

    /// <summary>The sentence behind the indicator, which is where the reason lives.</summary>
    public static string IndicatorDetailFor(RecordingState state) => state switch
    {
        RecordingState.Off =>
            "Nothing is being recorded for this workspace. Restore points already taken are still "
          + "listed and can still be restored. Settings ▸ Revision Control turns it back on.",
        RecordingState.Held =>
            "Nothing is being recorded for this workspace, because its history is not circuitRF's to "
          + "write to. Restore points already taken are still listed and can still be restored.",
        RecordingState.Failed =>
            "circuitRF tried to record this workspace and could not. Nothing since then is in the "
          + "history. The Messages panel says what happened.",
        _ => "",
    };
}

/// <summary>
/// What the indicator is saying, and what a refusal means (R-rc6-10).
///
/// <para><b>Three reasons, one meaning.</b> <see cref="Off"/>, <see cref="Held"/> and
/// <see cref="Failed"/> arrive by completely different routes and say the same thing to a designer:
/// nothing is being recorded right now. That is why they share an indicator instead of having one
/// each.</para>
/// </summary>
public enum RecordingState
{
    /// <summary>Recording normally. The indicator is not shown at all — a badge that is always there
    /// is a badge nobody reads.</summary>
    On,

    /// <summary>Switched off, for this workspace or everywhere (§5.7).</summary>
    Off,

    /// <summary>A repository circuitRF does not manage (§12 Q4).</summary>
    Held,

    /// <summary>A boundary was reached and could not be recorded (§4.4's reporting rule).</summary>
    Failed,
}

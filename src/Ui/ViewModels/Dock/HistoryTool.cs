using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Dock.Model.Mvvm.Controls;
using CircuitRF.Design.Revision;
using CircuitRF.Ui.Docking;

namespace CircuitRF.Ui.ViewModels.Dock;

/// <summary>
/// One row of the merged history — a version, a restore point, or a stretch in which circuitRF
/// recorded nothing (<c>docs/design/revision-control.md</c> §5.10; RC-10 R-rc10-2, R-rc10-11,
/// R-rc10-12, R-rc10-14).
///
/// <para><b>One mark carries the whole difference between the two kinds</b> (R-rc10-2). A version keeps
/// the accent-coloured tag; a restore point carries none. The mark says <i>titled, permanent,
/// travels</i> — three promises the unmarked rows do not make. It is not an importance badge, and there
/// is deliberately no second colour scheme: a second one is noise on a list whose entire problem is
/// noise.</para>
///
/// <para><b>The row is what a designer SCANS; the expander is what they open when scanning was not
/// enough</b>, and the split is between <i>which moment was this</i> and <i>what exactly is this</i>.
/// The commit identity is in the second of those and nowhere else — R-rc10-15 permits it because
/// opening an expander is the explicit action R-rc7-4's rule turns on, and it is the one string with
/// which a designer, or somebody helping them, can ask a question circuitRF's own window cannot
/// answer.</para>
/// </summary>
public sealed class HistoryRowItem
{
    public HistoryRowItem(HistoryEntry entry, DateTimeOffset nowUtc, bool showAuthor)
    {
        Entry      = entry;
        ShowAuthor = showAuthor && entry.Who.Length > 0;
        Day        = entry.IsGap ? "" : HistoryDates.Day(entry.WhenUtc, nowUtc);
        _nowUtc    = nowUtc;
    }

    private readonly DateTimeOffset _nowUtc;

    public HistoryEntry Entry { get; }

    public bool IsGap     => Entry.IsGap;
    public bool IsVersion => Entry.IsVersion;
    public bool IsPoint   => Entry.IsPoint;

    /// <summary>Whether this row can be gone back to. A gap has nothing behind it.</summary>
    public bool CanGoBack => !IsGap;

    // ── The row (R-rc10-12) ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The time a human reads — <b>how long ago inside the last day</b>, and the clock time beyond it
    /// (owner, 2026-09-07). <b>The label only</b>: what is oldest is decided by circuitRF's own
    /// sequence, because a wall clock is user-writable state.
    ///
    /// <para>Measured against the moment the list was built, not a live clock. The panel is rebuilt at
    /// every boundary, so a phrase does not go stale while anything is happening; one left on screen
    /// through a quiet hour is out of date by that hour, and the exact moment is in the expander,
    /// which is where R-rc10-14 puts everything a row cannot carry.</para>
    /// </summary>
    public string When => Entry.IsGap ? "" : HistoryDates.Time(Entry.WhenUtc, _nowUtc);

    /// <summary>
    /// The day, <b>with its year whenever the entry is not from the current year</b> (R-rc10-13).
    /// Before this the two panels rendered <c>ddd d MMM</c>, so a version kept in December read in
    /// January as one kept this week.
    /// </summary>
    public string Day { get; }

    /// <summary>The title, or the origin sentence for an entry nobody titled — never a bare
    /// time. <b>And the correction in place of either</b>, when the author wrote one (R-rc11-13).</summary>
    public string Title => Entry.Title;

    /// <summary>
    /// R-rc11-13. <b>The correction shows in place of the original, and the original is one click
    /// away</b> — in the expander, which is where everything a row cannot carry lives.
    /// </summary>
    public bool IsCorrected => Entry.IsCorrected;

    /// <summary>What was actually written down. <b>Never gone</b> (R-rc11-14): it is still in the
    /// history and can still be read by anyone holding the workspace, and the dialog that offered the
    /// correction said so in a sentence that may not be softened.</summary>
    public string OriginalTitleText
        => Entry.IsCorrected ? "Corrected. It originally said: " + Entry.OriginalTitle : "";

    /// <summary>
    /// <b>§5.12's longer note</b> — the corrected one where the author wrote a correction, and
    /// otherwise what they wrote when they recorded the entry. Empty on most rows.
    ///
    /// <para>It lives in the expander and never on the row, by R-rc10-14's split: the row answers
    /// <i>which moment was this</i> in one line, and a paragraph cannot be scanned. What the row
    /// carries is <see cref="HasNote"/>'s mark, which says there is one to open.</para>
    /// </summary>
    public string Note => Entry.Note;

    public bool HasNote => Entry.HasNote;

    /// <summary>What the note originally said, when a correction is standing in front of it — the
    /// same promise <see cref="OriginalTitleText"/> keeps, for the same reason (R-rc11-14).</summary>
    public string OriginalNoteText
        => Entry.IsNoteCorrected ? "It originally said: " + Entry.OriginalNote : "";

    public bool IsNoteCorrected => Entry.IsNoteCorrected;

    /// <summary>What the gap row says: the dates and the reason, because the reason is the whole
    /// content of the row (R-rc10-11).</summary>
    public string GapText
        => Entry.Gap is { } gap ? HistoryMessages.GapBetween(gap.FromUtc, gap.ToUtc) : "";

    /// <summary>
    /// R-rc10-2's mark. <b>The only thing that distinguishes the two kinds of row</b>, and it means
    /// three specific things rather than "this one is important".
    /// </summary>
    public bool HasVersionMark => Entry.IsVersion;

    /// <summary>Who kept it — <b>only on a workspace with more than one author</b>, where it answers a
    /// question. On a single-designer workspace it says nothing and is absent.</summary>
    public bool   ShowAuthor { get; }
    public string Who        => Entry.Who;

    /// <summary>R-rc7-6. What this version was brought back from, when it was — carrying its year
    /// under R-rc10-13's rule, since this is exactly the sentence a wrong year misleads in.</summary>
    public string RestoredFrom
        => Entry.Version?.RestoredFrom is { } from
            ? $"brought back from '{from.Label}', {HistoryDates.DayAndTime(from.TakenUtc, _nowUtc)}"
            : "";

    public bool WasRestored => RestoredFrom.Length > 0;

    /// <summary>R-rc5-15a. Something was left out because nobody was there to be asked, and the row
    /// says so with the names rather than looking like a complete one.</summary>
    public bool IsIncomplete => Entry.Point is { IsIncomplete: true };

    public string LeftOutSummary
        => Entry.Point is { IsIncomplete: true } p ? "Left out: " + string.Join(", ", p.LeftOut) : "";

    /// <summary>RC-6 R-rc6-4. Retention tidied this one away, and it is still offered — marked rather
    /// than hidden, because a row that silently vanished is indistinguishable from one destroyed.</summary>
    public bool Thinned => Entry.Thinned;

    /// <summary>R-rc9-6. On the copy this workspace came from and not here yet.</summary>
    public bool   OnTheOtherCopy => Entry.Version is { OnTheOtherCopy: true };
    public string OtherCopyNote  => OnTheOtherCopy ? "on the copy this came from — not here yet" : "";

    /// <summary>The word the row shows on the right — one at a time, because an entry the designer
    /// marked keep is never one retention thinned.</summary>
    public string Mark => Thinned ? "tidied away" : Entry.Point is { Kept: true } ? "kept" : "";

    public bool HasMark => Mark.Length > 0;

    /// <summary>A thinned row reads dimmer, and so does an incoming one — <b>present but not being
    /// kept for you</b>, and <b>present but not yours yet</b> are different from an ordinary row and
    /// from each other, and both are hard to say in a word.</summary>
    public double RowOpacity => Thinned ? 0.55 : OnTheOtherCopy ? 0.75 : 1.0;

    // ── The expander (R-rc10-14) ─────────────────────────────────────────────────────────────────

    /// <summary>The full timestamp with its zone.</summary>
    public string FullWhen => Entry.IsGap ? "" : HistoryDates.Full(Entry.WhenUtc);

    /// <summary>The origin, spelled out (§5.5) — the same sentence the entry itself carries, so the
    /// panel and the record cannot disagree about what happened.</summary>
    public string Origin => Entry switch
    {
        { IsVersion: true } => "You kept this version under a title you wrote.",
        { Point: { } p }    => CheckpointMessage.Explanation(p.Origin),
        _                   => "",
    };

    /// <summary>
    /// Whether this entry has left the machine. <b>A restore point never has</b> (§5.2a) and says so —
    /// which is the fact §5.11 turns on and the one a designer most often assumes the other way round.
    /// </summary>
    public string Reach => Entry switch
    {
        { IsGap: true }              => "",
        { IsVersion: true } v        => v.Shared
                                      ? "Shared — this has been sent to the copy this workspace exchanges with."
                                      : "On this machine only, so far. It travels with a copy of this workspace.",
        _                            => "On this machine only. Restore points never travel with a copy.",
    };

    /// <summary>R-rc10-14, R-rc10-15. The identity, short — the full one is on the menu, to copy.</summary>
    /// <summary>
    /// <b>The WHOLE identity — the same string "copy the identifier" puts on the clipboard</b> (owner,
    /// 2026-09-07).
    ///
    /// <para>It used to show the first twelve characters, on the reasoning that a person could retype
    /// that without transcribing it wrongly. But the menu item exists precisely so that nobody retypes
    /// it, and a designer who pastes forty characters after reading twelve has no way to tell whether
    /// the copy was the right thing — which is what got reported. A shown value that differs from the
    /// copied one costs more trust than a long string costs space.</para>
    /// </summary>
    public string Identity => Entry.Identity;

    public bool HasIdentity => Identity.Length > 0;

    /// <summary>
    /// <b>What names this row</b> (owner, 2026-09-08). Every automatic entry rendered under circuitRF's
    /// own wording for its origin, so a workspace closed forty times listed forty rows reading the same
    /// three words and the panel could not be scanned. The time separates them; it does not let a
    /// designer refer to one. See <see cref="HistoryIds"/> for why seven characters and why this is not
    /// a widening of the vocabulary rule.
    ///
    /// <para>The WHOLE identity stays on the expander and is what the copy action puts on the
    /// clipboard — R-rc10-15's finding, unchanged: a value shown short and copied long is exactly the
    /// trust problem that was reported once already, and this is a second rendering of the same string
    /// rather than a second value.</para>
    /// </summary>
    public string ShortIdentity => HistoryIds.Short(Entry.Identity);

    public bool HasShortIdentity => ShortIdentity.Length > 0;

    /// <summary>§5.6 rule 2's ordering number, on the entries that carry one.</summary>
    public string SequenceText
        => Entry.Sequence is { } n ? "Entry " + n.ToString(System.Globalization.CultureInfo.CurrentCulture) : "";

    public bool HasSequence => Entry.Sequence is not null;

    /// <summary>The kept mark, spelled out where there is room for the sentence.</summary>
    public string KeptText => Entry.Point is { Kept: true }
        ? "Kept — tidying up will never remove this one."
        : "";

    public bool HasKeptText => KeptText.Length > 0;

    /// <summary>
    /// What the go-back menu item says, with the title truncated: titles are free text and a menu is
    /// not a place a paragraph can go.
    ///
    /// <para><b>Only what a PERSON wrote is quoted</b> (owner, 2026-09-07). Quoting circuitRF's own
    /// wording back at the designer produced "Go back to 'before going back'" — a menu item quoting a
    /// phrase nobody wrote, about an entry whose name already read as an instruction. Quotation marks
    /// say <i>these are your words</i>, and on a generated label that is simply untrue; the row beside
    /// the menu says which entry this is in any case.</para>
    /// </summary>
    public string GoBackText
    {
        get
        {
            string title = HasWrittenTitle ? Title.ReplaceLineEndings(" ").Trim() : "";

            // NOBODY WROTE A TITLE, so the destination is named by its IDENTITY (owner, 2026-09-08).
            // "Go back to this state" read the same on every row, and the rows it appeared on are
            // exactly the ones whose generated labels already read alike — so the button under the list
            // said nothing about which of forty entries it would act on. The wording is
            // HistoryMessages.GoBackToId's, which the way-forward button already carries: two buttons
            // that perform one action say it one way, and neither quotes a phrase nobody wrote. The
            // word "back" came off both (owner, 2026-09-08): the destination is on the button already.
            if (title.Length == 0) return HistoryMessages.GoBackToId(Entry.Identity);

            if (title.Length > 40) title = title[..39] + "…";
            return $"Go to “{title}”";
        }
    }

    /// <summary>
    /// Whether this row's title is words a person wrote. A version always is — nobody keeps one
    /// without titling it. A restore point is when it carries an intent, which is what a named
    /// save-point, a batch and a rename all write.
    /// </summary>
    public bool HasWrittenTitle => Entry.Kind switch
    {
        HistoryEntryKind.Version      => true,
        HistoryEntryKind.RestorePoint => Entry.Point!.Intent is { Length: > 0 },
        _                             => false,
    };
}

/// <summary>One document that differs between two states (R-rc7-11). Moved here unchanged when RC-10
/// merged the two panels — the type was never about which panel it was shown in.</summary>
public sealed class VersionChangeRow
{
    public VersionChangeRow(DocumentChange change) => Change = change;

    public DocumentChange Change { get; }

    public string Path => Change.RelativePath;

    /// <summary>What happened to it, in a word a designer reads rather than a status letter.</summary>
    public string Kind => Change.Kind switch
    {
        DocumentChangeKind.Added   => "added",
        DocumentChangeKind.Removed => "removed",
        DocumentChangeKind.Renamed => "moved",
        _                          => "changed",
    };

    public string Detail => Change.PreviousPath is { } was ? "was " + was : "";
    public bool   HasDetail => Detail.Length > 0;
}

/// <summary>
/// <b>The history panel — one panel, two kinds of row</b> (<c>docs/design/revision-control.md</c>
/// §5.10; RC-10). <b>Supersedes RC-7 R-rc7-9</b>, which asked for two.
///
/// <para><b>§5's argument is about two kinds of ENTRY, and rev 2 spent it as an argument for two
/// WINDOWS.</b> The reason actually given for the split — a list of three hundred automatic entries
/// with four deliberate ones among them is unreadable — is an argument for a FILTER, and a filter is
/// strictly better: the default view is the deliberate entries and <b>the designer never has to know
/// which panel to open</b>. That question is harder than it looks at the moment it is asked, which is
/// the moment something has gone wrong.</para>
///
/// <para><b>And two panels produced a safety failure rather than clutter</b> (R-rc10-19). §5.8's
/// restore takes a checkpoint of the current state first and files it among the restore points, so a
/// restore begun from the Versions panel left the designer looking at a window with no evidence that
/// the afternoon they had just replaced still existed. The reassurance was implemented, correct, and
/// in the room the designer was not in — which is what <see cref="WayForward"/> puts right.</para>
///
/// <para><b>Nothing about the merge changes what is recorded</b> (R-rc10-4, §5.10 rule 6). Checkpoints
/// stay on §5.2a's per-checkpoint references and still do not clone; versions stay ordinary commits on
/// the line of work. The moment the two are stored alike, §5.2a's travel table stops being true and
/// §5.6 has a human-written commit in its scope.</para>
///
/// <para><b>The panel holds no history of its own.</b> The list is read from the workspace on demand
/// and refreshed when a boundary says it changed — a stale list would offer a designer a way back to a
/// state that is no longer there.</para>
/// </summary>
public partial class HistoryTool : Tool, IActivatableTool
{
    // ── Activation focus ──────────────────────────────────────────────────────
    //
    //  Owner, 2026-09-07: cancelling Keep This State / Keep This Version left focus on the WORKSPACE,
    //  not on the panel the button was pressed in. A dialog dismissed should hand the keyboard back to
    //  where the gesture started — anything else makes the next keystroke go somewhere the designer
    //  was not looking.
    //
    //  The relay is the mechanism the Project Tree and the Library palette already use: the view model
    //  cannot touch the view, so it asks, and the view focuses its own content. The pending half
    //  matters here for the same reason it does there — the panel can be asked before its view exists.

    private readonly ActivationFocusRelay _activationFocus = new();

    public event Action? ActivationFocusRequested
    {
        add    => _activationFocus.Requested += value;
        remove => _activationFocus.Requested -= value;
    }

    public void RequestActivationFocus() => _activationFocus.Request();

    public bool ConsumeActivationFocus() => _activationFocus.Consume();

    /// <summary>What the list currently holds, newest first.</summary>
    public ObservableCollection<HistoryRowItem> Rows { get; } = [];

    /// <summary>What the selected version changed, against the one before it (R-rc7-11).</summary>
    public ObservableCollection<VersionChangeRow> Changes { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(CanGoBack))]
    [NotifyPropertyChangedFor(nameof(CanGoBackToSelection))]
    [NotifyPropertyChangedFor(nameof(CanKeepPermanently))]
    [NotifyPropertyChangedFor(nameof(CanBringBack))]
    [NotifyPropertyChangedFor(nameof(CanCompare))]
    [NotifyPropertyChangedFor(nameof(CanRename))]
    [NotifyPropertyChangedFor(nameof(CanLetGo))]
    [NotifyPropertyChangedFor(nameof(CanCorrect))]
    private HistoryRowItem? _selected;

    partial void OnSelectedChanged(HistoryRowItem? value)
    {
        // A title left over from the previous row's comparison would label the new row's changes with
        // the wrong question — the same class of defect as a stale trace mode rendering over a real
        // curve. A selection change resets it; the menu item sets it again.
        ComparisonTitle = "What this version changed:";
        IsShowingComparison = false;
        SelectionChanged?.Invoke(value?.Entry.Version);
    }

    /// <summary>Raised so the host can fill in <see cref="Changes"/> without this panel reaching below
    /// the firewall itself.</summary>
    public event Action<HistoryVersion?>? SelectionChanged;

    public bool HasSelection       => Selected is not null;
    public bool CanGoBack          => Selected is { CanGoBack: true };

    /// <summary>
    /// <b>Whether the panel offers going back to the selected row as a BUTTON</b> (owner, 2026-09-08:
    /// there was no go-back button in this window at all).
    ///
    /// <para>Going back has lived only on the row's right-click menu since R-rc10-17 moved every
    /// row action there, and that rule is right for the other seven — but it put the panel's ONE
    /// primary action behind a gesture a designer has to guess at, at the moment they are least
    /// inclined to go hunting. So the action is on the surface as well, named after its destination
    /// exactly as the menu item is (<see cref="HistoryRowItem.GoBackText"/>), and the menu keeps its
    /// entry: this is a second site for one action, never a second implementation of it.</para>
    ///
    /// <para><b>And there is no button when it would land where the workspace already is</b> (owner,
    /// 2026-09-08) — the same rule <see cref="WayForward.LeadsSomewhereElse"/> withdraws the way-back
    /// under, applied to the selection. This session knows which entry the workspace was last put
    /// into; a go-back to that entry is a whole reload that changes nothing, and offering one teaches a
    /// designer that the button does not work. Nothing is asked of the repository for this: the
    /// identity is already in hand.</para>
    /// </summary>
    public bool CanGoBackToSelection
        => Selected is { CanGoBack: true } row && !IsWhereTheWorkspaceIs(row);

    /// <summary>
    /// Whether this row holds the state the workspace is already in. <b>Two ways of knowing that, and
    /// neither costs a repository read</b> — which is the constraint: the honest answer is <i>hash every
    /// file and compare the tree</i>, and paying a whole checkpoint's work to decide whether to draw a
    /// button is the cost RC-11 removed from this panel once already.
    ///
    /// <list type="number">
    ///   <item><description><b>The workspace's own content, when it is known</b>
    ///   (<see cref="WorkspaceTreeId"/>) — matched on the TREE, so the second of two entries recorded
    ///   over one state is withdrawn as well as the first.</description></item>
    ///   <item><description><b>Where this session's last restore put it</b> — matched on identity,
    ///   which is what the session knows for certain and what the way-forward line is already built
    ///   from.</description></item>
    /// </list>
    ///
    /// <para><b>Unknown is not "where the workspace is."</b> Both facts are absent by default, and a
    /// workspace nothing is known about offers every row as a way back — which is the direction that
    /// matters, because a button withheld when it was needed is the failure §1.4 is written against,
    /// while one offered needlessly costs a reload.</para>
    /// </summary>
    private bool IsWhereTheWorkspaceIs(HistoryRowItem row)
        => (WorkspaceTreeId.Length > 0
            && string.Equals(WorkspaceTreeId, row.Entry.TreeId, StringComparison.Ordinal))
        || (WayForward is { WentBackToId.Length: > 0 } w
            && string.Equals(w.WentBackToId, row.Entry.Identity, StringComparison.Ordinal));

    /// <summary>
    /// <b>The content the workspace on disk holds, when that is known for free</b> (owner, 2026-09-08:
    /// a go-back button offered for the state the workspace was already in).
    ///
    /// <para>The host sets it to the newest entry's tree when nothing has been written into the
    /// workspace since that entry was recorded, and to nothing otherwise —
    /// <c>WorkspaceHistoryService.WrittenSinceLastEntry</c> answers the second half with no git at all,
    /// and <c>HistorySources.Newest</c> the first from the read the panel was refreshed by. Empty means
    /// unknown, and unknown offers every row.</para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoBackToSelection))]
    private string _workspaceTreeId = "";

    // ── Nothing to record (owner, 2026-09-08) ────────────────────────────────────────────────────

    /// <summary>
    /// <b>Whether a keep would record anything at all.</b> Both keep actions used to open their dialog,
    /// take a title, and then create nothing: the tree test that declines a duplicate lives in
    /// <c>GitCheckpoint.Record</c>, which is after the dialog, so the refusal was correct, correctly
    /// worded, and arrived once the designer had already done the work of naming something.
    ///
    /// <para><b>Decided by the host, not here</b> — it is a fact about the workspace, and the same two
    /// actions are on the File menu with every tool panel closed. See
    /// <c>WorkspaceHistoryService.WrittenSinceLastEntry</c> for why the signal is a per-boundary write
    /// flag rather than a tree comparison, and <c>WorkspaceViewModel.CanKeepAnything</c> for the three
    /// states that force it true regardless.</para>
    ///
    /// <para><b>True by default</b>, so a panel nobody has told anything is the panel that was here
    /// before this existed.</para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanKeep))]
    [NotifyPropertyChangedFor(nameof(SavePointTooltip))]
    [NotifyPropertyChangedFor(nameof(KeepVersionTooltip))]
    private bool _canKeepAnything = true;

    /// <summary>Whether either header button is live. One property over both, because there is one
    /// reason and it is the same reason.</summary>
    public bool CanKeep => HasWorkspace && CanKeepAnything;

    /// <summary>
    /// <b>The reason is on the control</b> (R-rc6-8's argument, one control over). A greyed button with
    /// nothing said about it is indistinguishable from a feature that was never built; one that says why
    /// — and what would bring it back — reads as finished.
    /// </summary>
    public string SavePointTooltip => CanKeepAnything
        ? "Keep this state — writes a restore point for the workspace as it stands, with a line saying what it is"
        : HistoryMessages.NothingToKeepYet;

    public string KeepVersionTooltip => CanKeepAnything
        ? "Keep this version — records the whole workspace under a title you write, so you can find it again and send it out"
        : HistoryMessages.NothingToKeepYet;
    public bool CanKeepPermanently => Selected is { IsPoint: true, Entry.Point.Kept: false };
    public bool CanBringBack       => Selected is { Thinned: true };
    public bool CanCompare         => Selected is { IsVersion: true };

    // ── RC-11's corrections (§5.11) ──────────────────────────────────────────────────────────────

    /// <summary>
    /// §5.11 case (a). <b>A restore point's label is its author's to correct, at any time</b> — it is
    /// parentless, no copy takes it and no send carries it, so there is nothing a rename could
    /// invalidate. A thinned entry is excluded: its reference is gone, and the way back to it is
    /// <see cref="BringBack"/>, which is on the same menu.
    /// </summary>
    public bool CanRename => Selected is { IsPoint: true, Thinned: false };

    /// <summary>§5.11 case (a). And it may be let go — <b>which is exactly what tidying up does</b>
    /// (R-rc11-5), journalled and reversible, freeing nothing until a reclaim.</summary>
    public bool CanLetGo => Selected is { IsPoint: true, Thinned: false };

    /// <summary>
    /// §5.11 cases (b) and (c). <b>One menu item over both</b>, because the difference between them is
    /// the dialog's to explain and not the designer's to know before opening it. A version that has
    /// not left the machine is retitled outright; one that has takes a correction — and which of the
    /// two applies is <see cref="VersionSharing"/>'s answer, computed rather than assumed.
    /// </summary>
    public bool CanCorrect => Selected is { IsVersion: true, OnTheOtherCopy: false };

    /// <summary>True when there is a workspace with a history to show. Otherwise the panel says why it
    /// is empty rather than looking broken.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyText))]
    [NotifyPropertyChangedFor(nameof(CanKeep))]
    private bool _hasWorkspace;

    /// <summary>
    /// R-rc10-5's own completion question, answered in the panel rather than in a note.
    ///
    /// <para>A workspace whose only history is workspace-close entries opens on an EMPTY list under the
    /// default filter — and that is precisely the workspace §1 is written for, because its owner never
    /// thought about history at all. So the empty line counts them and names the control that reveals
    /// them, rather than claiming nothing has been kept.</para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyText))]
    private int _hiddenAutomaticCount;

    /// <summary>
    /// What an empty list means — <b>four different facts and four different sentences</b>, and
    /// collapsing them is how the false one ends up in front of the population that most needs the
    /// true one.
    ///
    /// <para>The order is the order of specificity: no workspace at all; a workspace whose only
    /// history is the automatic entries the default view hides; a filter the designer has narrowed;
    /// and a workspace nothing has ever been recorded in. <b>Only the third depends on what the
    /// designer did to the panel</b>, which is why it is decided by comparing the filter with the
    /// default rather than by noticing that the list came back empty — the last would say "nothing
    /// matches your filter" about a workspace with no history and an untouched filter.</para>
    /// </summary>
    public string EmptyText
        => !HasWorkspace            ? HistoryMessages.NoWorkspaceOpenForHistory
         : HiddenAutomaticCount > 0 ? HistoryMessages.OnlyAutomaticEntries(HiddenAutomaticCount)
         : Filter != HistoryFilter.Default
                                    ? HistoryMessages.EverythingIsFilteredOut
                                    : HistoryMessages.NothingRecordedYet;

    /// <summary>R-rc7-11's caveat, said where a designer might expect more of the list than it
    /// gives.</summary>
    public string ComparisonNote => HistoryMessages.ComparisonIsByDocument;

    /// <summary>R-rc10-2's tooltip: what the mark on a version row actually promises.</summary>
    public string VersionMarkMeans => HistoryMessages.VersionMarkMeans;

    // ── The filter and the search (R-rc10-7, R-rc10-8, R-rc10-9) ─────────────────────────────────

    /// <summary>
    /// What is being shown. <b>Per-user view state in the <c>.cwsuser</c></b> (R-rc10-8) — like every
    /// other fact about how somebody arranged their view, and deliberately NOT a Settings row: every
    /// row on §10A's tab changes what is KEPT, and a filter a designer flips while hunting is not a
    /// preference about what exists.
    /// </summary>
    public HistoryFilter Filter { get; private set; } = HistoryFilter.Default;

    /// <summary>Raised when the designer changes the filter or the search, so the host re-reads the
    /// list and records the new state in the per-user file.</summary>
    public event Action? FilterChanged;

    // The five checkboxes. Each is a property rather than a bound record so a flyout can two-way bind
    // to it; the record is rebuilt from them, which keeps ONE definition of the default.
    public bool ShowVersions   { get => Filter.Versions;   set => Set(Filter with { Versions   = value }); }
    public bool ShowSavePoints { get => Filter.SavePoints; set => Set(Filter with { SavePoints = value }); }
    public bool ShowAiBatches  { get => Filter.AiBatches;  set => Set(Filter with { AiBatches  = value }); }
    public bool ShowAutomatic  { get => Filter.Automatic;  set => Set(Filter with { Automatic  = value }); }
    public bool ShowTidiedAway { get => Filter.TidiedAway; set => Set(Filter with { TidiedAway = value }); }

    /// <summary>R-rc10-9. Over what a person wrote — titles, batch intents and the author.</summary>
    public string SearchText
    {
        get => Filter.Search;
        set => Set(Filter with { Search = value ?? "" });
    }

    public bool HasSearchText => Filter.Search.Length > 0;

    /// <summary>
    /// R-rc10-9's field, in <c>ProjectTreeView</c>'s shape: <b>the magnifier is the affordance and the
    /// field exists only while it is on</b>. An always-present field spends the width of the panel on a
    /// control most sessions never touch.
    /// </summary>
    [ObservableProperty] private bool _isSearchOpen;

    public void ToggleSearch() => IsSearchOpen = !IsSearchOpen;

    /// <summary>
    /// Closing the field CLEARS the query, always — and it lives here rather than in
    /// <see cref="ToggleSearch"/> because the magnifier is not the only way the field is put away.
    /// Escape closes it too, and a filter still applied with nothing on screen to say so is the worst
    /// state this panel can be in: rows are hidden and the affordance that would explain it has just
    /// gone. <c>ProjectTreeTool</c> carries the identical argument.
    /// </summary>
    partial void OnIsSearchOpenChanged(bool value)
    {
        if (!value && HasSearchText) SearchText = "";
    }

    public void ClearSearch() => SearchText = "";

    /// <summary>
    /// <b>The last read of the repository, so a filter change does not start one</b> (RC-11).
    ///
    /// <para>Every checkbox in this flyout used to re-read the whole workspace — versions, restore
    /// points, the thinning journal, the sharing set, the corrections, the incoming versions and a
    /// second full pass for the empty line's count — a dozen git subprocesses on the UI thread for a
    /// decision that changes nothing but which rows are drawn. <see cref="HistorySources.Under"/> is
    /// pure, so the toggle re-filters what is already here and the read stays where it belongs, on a
    /// boundary.</para>
    ///
    /// <para>It is replaced whole by <see cref="SetRows"/> and never patched, so it cannot drift from
    /// what the repository last said.</para>
    /// </summary>
    private HistorySources _sources = HistorySources.Nothing;

    private void Set(HistoryFilter next)
    {
        if (next == Filter) return;

        Filter = next;
        OnPropertyChanged(nameof(ShowVersions));
        OnPropertyChanged(nameof(ShowSavePoints));
        OnPropertyChanged(nameof(ShowAiBatches));
        OnPropertyChanged(nameof(ShowAutomatic));
        OnPropertyChanged(nameof(ShowTidiedAway));
        OnPropertyChanged(nameof(SearchText));
        OnPropertyChanged(nameof(HasSearchText));
        OnPropertyChanged(nameof(EmptyText));

        // The list is redrawn from what is already in hand, BEFORE the host is told. That ordering is
        // the whole of the responsiveness: the checkbox and the rows change in the same frame, and
        // what the host then does — writing the choice into the `.cwsuser` — is a save, not a redraw.
        Redraw();
        FilterChanged?.Invoke();
    }

    /// <summary>Re-filters the last read. Pure, and the only thing a checkbox does.</summary>
    private void Redraw() => Show(_sources.Under(Filter), HasWorkspace, _sources.AutomaticCount);

    /// <summary>
    /// <b>Tidying an entry away, and bringing one back, redrawn from what the panel already holds</b>
    /// (owner, 2026-09-07: "Tidy this away blocks the UI thread momentarily").
    ///
    /// <para>Measured: the reference write is 18 ms and the full re-read that followed it is 180 ms on
    /// a sixty-entry workspace, growing with the list. Nothing that read touches can have changed —
    /// the versions, the incoming set, the sharing set and the annotations are all untouched by either
    /// operation, and the one thing that did change is a boolean already in hand. So the panel does
    /// what a filter checkbox does: it redraws, and starts no process.</para>
    ///
    /// <para>Called only after the write it mirrors reported success. A write that FAILED must go the
    /// long way round, because then the panel and the repository genuinely disagree and the panel is
    /// the one that is wrong.</para>
    /// </summary>
    public void MarkTidiedAway(string commitId, bool thinned)
    {
        var next = _sources.WithThinned(commitId, thinned);
        if (ReferenceEquals(next, _sources)) return;

        _sources = next;
        Redraw();
    }

    /// <summary>Restores the arrangement recorded in the per-user file, <b>without raising
    /// <see cref="FilterChanged"/></b> — this is the state being applied, not a change to it.</summary>
    public void ApplyStoredFilter(HistoryFilter filter)
    {
        Filter       = filter;
        IsSearchOpen = filter.IsSearching;

        OnPropertyChanged(nameof(ShowVersions));
        OnPropertyChanged(nameof(ShowSavePoints));
        OnPropertyChanged(nameof(ShowAiBatches));
        OnPropertyChanged(nameof(ShowAutomatic));
        OnPropertyChanged(nameof(ShowTidiedAway));
        OnPropertyChanged(nameof(SearchText));
        OnPropertyChanged(nameof(HasSearchText));
        OnPropertyChanged(nameof(EmptyText));
    }

    /// <summary>
    /// R-rc10-10. <b>How many entries retention tidied away also match the search and are not shown.</b>
    /// Said on a line of its own — the difference between an incomplete answer and a wrong one.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasThinnedMatches))]
    [NotifyPropertyChangedFor(nameof(ThinnedMatchText))]
    private int _thinnedMatchesNotShown;

    public bool   HasThinnedMatches => ThinnedMatchesNotShown > 0;
    public string ThinnedMatchText  => ThinnedMatchesNotShown > 0
                                     ? HistoryMessages.ThinnedAlsoMatch(ThinnedMatchesNotShown) : "";

    // ── The way forward (R-rc10-18, §5.8, §12 Q35) ───────────────────────────────────────────────

    /// <summary>
    /// <b>What a restore just did, reported here on arrival and by name.</b>
    ///
    /// <para>It creates nothing: the entry it points at already exists, and going to it is an ordinary
    /// restore taking an ordinary checkpoint of its own. This is the control that makes <i>going back
    /// is never a one-way door</i> visible at the one moment a designer is most likely to believe they
    /// have lost the day.</para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWayForward))]
    [NotifyPropertyChangedFor(nameof(WayForwardText))]
    [NotifyPropertyChangedFor(nameof(ComeForwardText))]
    [NotifyPropertyChangedFor(nameof(CanComeForward))]
    // Where the workspace IS decides whether the selected row is a way back at all — so the button
    // over the list is re-evaluated when a restore reports itself, not only when the selection moves.
    [NotifyPropertyChangedFor(nameof(CanGoBackToSelection))]
    private WayForward? _wayForward;

    public bool   HasWayForward  => WayForward is not null;

    public string WayForwardText => WayForward is { } w
        ? HistoryMessages.WayForward(
              w.WentBackToId, w.WentBackToUtc ?? w.KeptAs.TakenUtc,
              // The second clause is about a state to come BACK to, so it is dropped when there is
              // none — see WayForward.LeadsSomewhereElse. The empty id is what omits it.
              w.LeadsSomewhereElse ? w.KeptAs.CommitId : "", w.KeptAs.TakenUtc,
              DateTimeOffset.UtcNow)
        : "";

    /// <summary>
    /// <b>Whether there is a way back to offer at all.</b> A restore that replaced content identical
    /// to what it wrote left the designer nowhere earlier to return to, and offering one anyway named
    /// the state they were already looking at (owner, 2026-09-08).
    /// </summary>
    public bool CanComeForward => WayForward is { LeadsSomewhereElse: true };

    /// <summary>
    /// What the button says. <b>The destination, not the direction</b> — see
    /// <see cref="HistoryMessages.GoBackToId"/> for why a direction was the wrong thing to put on it.
    /// </summary>
    public string ComeForwardText => WayForward is { } w
                                   ? HistoryMessages.GoBackToId(w.KeptAs.CommitId) : "";

    /// <summary>Invoked when the designer follows the way forward.</summary>
    public Action<RestorePoint>? ComeForwardRequested { get; set; }

    public void ComeForward()
    {
        // Guarded as well as hidden: the button is not the only thing that could reach this, and a
        // restore to the state the workspace is already in is a whole reload that changes nothing.
        if (WayForward is { LeadsSomewhereElse: true } w) ComeForwardRequested?.Invoke(w.KeptAs);
    }

    // ── Incoming versions (RC-9 R-rc9-6) ─────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIncoming))]
    [NotifyPropertyChangedFor(nameof(IncomingText))]
    private int _incomingCount;

    public bool HasIncoming => IncomingCount > 0;

    public string IncomingText => IncomingCount switch
    {
        0 => "",
        1 => "1 version on the copy this workspace came from is not here yet. "
           + "Select it to see what it changes, or go back to it to bring it in.",
        _ => $"{IncomingCount} versions on the copy this workspace came from are not here yet. "
           + "Select one to see what it changes, or go back to it to bring it in.",
    };

    // ── Held must not look like absent (R-rc6-8, R-rc7-2) ────────────────────────────────────────

    /// <summary>
    /// Non-empty when nothing is being recorded. The actions stay VISIBLE and refuse rather than
    /// disappearing: a hidden control is indistinguishable from a feature that was never built, and the
    /// failure this whole feature guards against is a designer who believes they are protected and is
    /// not. RC-3 hides everything when git is MISSING, because absent is harmless; the pair is the
    /// point.
    /// </summary>
    [ObservableProperty] private string _recordingState = "";

    public bool IsRecordingBlocked => RecordingState.Length > 0;

    partial void OnRecordingStateChanged(string value) => OnPropertyChanged(nameof(IsRecordingBlocked));

    // ── What the host wires up ───────────────────────────────────────────────────────────────────

    /// <summary>Invoked for the explicit save-point (R-rc5-4c), which is also a File-menu action.</summary>
    public Action? SavePointRequested { get; set; }

    /// <summary>Invoked for the explicit commit (R-rc7-1).</summary>
    public Action? KeepVersionRequested { get; set; }

    /// <summary>Invoked when the designer asks to go back. <b>One action over both kinds of row</b> —
    /// a version goes back through RC-5's restore, so there is no second implementation.</summary>
    public Action<HistoryEntry>? GoBackRequested { get; set; }

    /// <summary>§10B.3's "make it permanent" (R-rc5-1f, §5.6 rule 6).</summary>
    public Action<RestorePoint>? KeepRequested { get; set; }

    /// <summary>RC-6 R-rc6-4's way back for an entry retention tidied away.</summary>
    public Action<RestorePoint>? BringBackRequested { get; set; }

    /// <summary>R-rc10-17. The identity, on the clipboard — the one string that answers a question
    /// circuitRF's own window cannot.</summary>
    public Action<string>? CopyIdentityRequested { get; set; }

    /// <summary>R-rc10-17, R-rc7-11. What this entry holds that the workspace does not.</summary>
    public Action<HistoryEntry>? CompareRequested { get; set; }

    /// <summary>§5.11 case (a). The label is the designer's to correct (R-rc11-3).</summary>
    public Action<RestorePoint>? RenameRequested { get; set; }

    /// <summary>§5.11 case (a). Letting one go, through RC-6's journal (R-rc11-5).</summary>
    public Action<RestorePoint>? LetGoRequested { get; set; }

    /// <summary>§5.11 cases (b) and (c). <b>One request</b> — the host asks whether the version has
    /// left the machine and opens the dialog that applies.</summary>
    public Action<HistoryVersion>? CorrectRequested { get; set; }

    public HistoryTool()
    {
        Id    = DockPanelIds.History;
        Title = HistoryMessages.PanelTitle;
    }

    /// <summary>
    /// Replaces the list. Called on every boundary and every workspace switch — the panel holds no list
    /// of its own.
    /// </summary>
    /// <param name="result">What <see cref="HistoryList.Read"/> answered under the current filter.</param>
    /// <param name="hasWorkspace">Whether there is a workspace at all.</param>
    /// <param name="hiddenAutomatic">How many workspace-close entries the default filter is hiding, for
    /// the empty line.</param>
    /// <param name="recordingState">R-rc6-8's sentence, or empty when recording is normal.</param>
    /// <param name="nowUtc">What "this year" and "today" are measured against — a parameter so the gate
    /// can assert both sides of the year boundary without waiting for January.</param>
    public void SetRows(HistoryList.Result result, bool hasWorkspace, int hiddenAutomatic = 0,
                        string recordingState = "", DateTimeOffset? nowUtc = null)
    {
        RecordingState = recordingState;
        Show(result, hasWorkspace, hiddenAutomatic, nowUtc);
    }

    /// <summary>
    /// <b>The panel's read of the repository</b> (RC-11), which is what a boundary and a workspace
    /// switch hand it — and what a filter toggle then re-filters without going near the repository
    /// again.
    /// </summary>
    public void SetSources(HistorySources sources, bool hasWorkspace, string recordingState = "",
                           DateTimeOffset? nowUtc = null)
    {
        _sources       = sources;
        RecordingState = recordingState;

        Show(sources.Under(Filter), hasWorkspace, sources.AutomaticCount, nowUtc);
    }

    private void Show(HistoryList.Result result, bool hasWorkspace, int hiddenAutomatic,
                      DateTimeOffset? nowUtc = null)
    {
        var now = nowUtc ?? DateTimeOffset.UtcNow;

        // "More than one author" is a property of the whole list, not of a row: on a single-designer
        // workspace the name says nothing and is absent, which is most workspaces.
        bool showAuthor = result.Rows.Select(r => r.Who)
                                     .Where(w => w.Length > 0)
                                     .Distinct(StringComparer.Ordinal)
                                     .Count() > 1;

        // Kept across the rebuild so a refresh does not throw away what the designer had selected.
        string? keepId  = Selected?.Entry.Identity;
        long?   keepSeq = Selected?.Entry.Sequence;

        Rows.Clear();
        foreach (var entry in result.Rows) Rows.Add(new HistoryRowItem(entry, now, showAuthor));

        HasWorkspace           = hasWorkspace;
        HiddenAutomaticCount   = Rows.Count == 0 ? hiddenAutomatic : 0;
        ThinnedMatchesNotShown = result.ThinnedMatchesNotShown;
        IncomingCount          = Rows.Count(r => r.OnTheOtherCopy);

        OnPropertyChanged(nameof(EmptyText));

        Selected = keepSeq is { } s
                 ? Rows.FirstOrDefault(r => r.Entry.Sequence == s)
                 : keepId is { Length: > 0 } id
                 ? Rows.FirstOrDefault(r => r.Entry.Identity == id)
                 : null;
    }

    /// <summary>Replaces what the selected version changed.</summary>
    public void SetChanges(IReadOnlyList<DocumentChange> changes)
    {
        Changes.Clear();
        foreach (var c in changes) Changes.Add(new VersionChangeRow(c));
        OnPropertyChanged(nameof(NothingDiffers));
    }

    /// <summary>
    /// Whether the list underneath is the answer to an EXPLICIT comparison rather than the panel's own
    /// default. Only the explicit one can meaningfully come back empty — a restore point selected in
    /// the ordinary way has no per-version change list at all, and labelling that "no differences"
    /// would be an answer to a question nobody asked.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NothingDiffers))]
    private bool _isShowingComparison;

    /// <summary>
    /// <b>A comparison that finds nothing says so</b> (owner, 2026-09-07). An empty list under a
    /// changed heading is indistinguishable from a menu item that did nothing at all, and "these are
    /// the same" is the one answer this comparison gives most often — it is what a designer checks
    /// before deciding they do not need to go back.
    /// </summary>
    public bool NothingDiffers => IsShowingComparison && Changes.Count == 0;

    public void SavePoint()   => SavePointRequested?.Invoke();
    public void KeepVersion() => KeepVersionRequested?.Invoke();

    public void GoBack()   { if (Selected is { CanGoBack: true } row) GoBackRequested?.Invoke(row.Entry); }
    public void Keep()     { if (Selected?.Entry.Point is { Kept: false } p) KeepRequested?.Invoke(p); }
    public void BringBack(){ if (Selected?.Entry.Point is { Thinned: true } p) BringBackRequested?.Invoke(p); }

    public void CopyIdentity()
    {
        if (Selected?.Entry.Identity is { Length: > 0 } id) CopyIdentityRequested?.Invoke(id);
    }

    // ── RC-11's three actions (§5.11) ────────────────────────────────────────────────────────────

    public void Rename()  { if (CanRename  && Selected?.Entry.Point   is { } p) RenameRequested?.Invoke(p); }
    public void LetGo()   { if (CanLetGo   && Selected?.Entry.Point   is { } p) LetGoRequested?.Invoke(p); }
    public void Correct() { if (CanCorrect && Selected?.Entry.Version is { } v) CorrectRequested?.Invoke(v); }

    /// <summary>
    /// R-rc10-17. Compares the selected entry with the workspace as it stands, and says which
    /// comparison the list underneath is showing — the panel's own default is "what this version
    /// changed", and a reader who could not tell the two apart would read one as the other.
    /// </summary>
    public void CompareWithWorkspace()
    {
        if (Selected is not { CanGoBack: true } row) return;

        ComparisonTitle     = "What this holds that the workspace does not:";
        IsShowingComparison = true;
        CompareRequested?.Invoke(row.Entry);
    }

    /// <summary>Which comparison <see cref="Changes"/> is currently showing.</summary>
    [ObservableProperty] private string _comparisonTitle = "What this version changed:";
}

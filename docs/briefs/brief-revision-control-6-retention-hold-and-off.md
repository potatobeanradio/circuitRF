# Sonnet Brief — RC-6: retention, the enclosing-repository hold, and turning it off

**Read `brief-revision-control-0-overview.md` first.** The architecture is
`docs/design/revision-control.md` §5.6, §5.6a, §5.7, §7A.1, §7A.5, §8.1a and §12 Q4, plus §5.2b for the
checkpoint shape thinning depends on. **Depends on RC-5.** **rev 5 corrected the way back after a sweep**
(R-rc6-4 — a journal, not the reflog), **settled reclaim** (R-rc6-4b — explicit, never automatic),
**exempted three kinds of checkpoint** (R-rc6-5a), **gave the nested row a detection** (R-rc6-7), and
**made the transition checkpoints kept** (R-rc6-14a).

**Scope: the three ways a designer can end up with less history than they think they have** — it aged
out, circuitRF never started, or they switched it off. Each one is a false-belief failure under §1.4,
and each is answered by making the state visible rather than by keeping more data.

---

## 1. Retention, and what a clock change must not do

§1.3 argues the recovery window is measured in **weeks**, because the failure being guarded against —
a well-formed design that is wrong, an agent that widened the right trace on the wrong layer — may not
be noticed for weeks. That implies thinning old checkpoints rather than unbounded growth. The duration
and the floor are user preferences (RC-4).

**The hazard is not hypothetical: a wall clock is user-writable state.** A machine whose clock jumps a
century forward makes every checkpoint expired on the next sweep. A clock set backwards makes nothing
expire and stamps new checkpoints as older than the ones they follow. Timezone changes, a dead CMOS
battery, a laptop resuming against a re-synced time server, and a dual-boot machine disagreeing about
whether the hardware clock is UTC all produce the same class of fault, and all of them are ordinary.

**A retention policy that can be triggered into mass deletion by a clock change is unacceptable.** Five
rules; the first two are sufficient on their own and the rest are defence in depth.

**R-rc6-1. A count floor that age can never override.** The newest *N* checkpoints are kept
unconditionally, whatever any timestamp says, with a hard minimum below which the preference cannot be
set (RC-4). **A clock jump then costs the user nothing at all.**

**R-rc6-2. Ordering comes from RC-5's monotonic sequence, never from the commit timestamp.** The wall
clock supplies the label a human reads; it never decides what is oldest. This is also what fixes the
backwards-clock case, where timestamp order and actual order disagree.

**R-rc6-3. A sweep is bounded.** No single retention pass may remove more than a small fraction of the
existing checkpoints. **A pass that wants to remove more is refused and reported** — which converts a
clock fault from silent data loss into a Messages entry saying something is wrong with the clock, which
is both true and useful.

**R-rc6-4. Retention thins; it never prunes — and the way back is a journal, not the reflog** (§5.6
rule 4, §12 Q20, corrected in rev 5). Dropping a checkpoint reference leaves its objects in the
repository as unreachable objects. **This works because RC-5 gives each checkpoint its own reference
AND no parent** (R-rc5-1, R-rc5-1b) — a chain, by reference or by parent, leaves everything before the
cut reachable and thins nothing; RC-5 gate 14a is what proves the shape. Dropping one is a single
reference delete: the pointer goes, the objects stay.

**rev 4 said the objects were "recoverable via `git reflog`", and they are not.** A deleted reference
takes its reflog with it, and references outside `refs/heads/` carry none by default in any case. What
finds an unreachable commit is `git fsck --unreachable`, which is an escape hatch only for someone who
already knows git. So **this brief keeps a journal of what it thinned** — each dropped reference's name,
commit identity and the time it was thinned, appended under `.git/circuitrf/` — and a thinned restore
point is **listed** in the same list as the live ones, marked as thinned, with restoring it being one
reference update from the journal. The journal is what makes rule 4 a promise to a designer rather than
to a git user, and it is what R-rc6-4b's reclaim reads.

**And the objects survive only because RC-3 CONFIGURED them to** (R-rc3-16, corrected in rev 3). Not
passing `--prune=now` is not sufficient: plain `git gc` prunes unreachable objects at `gc.pruneExpire`
(**default two weeks**), unasked. Without `gc.pruneExpire=never`, **this rule's promise expires after a
fortnight** — inside §1.3's own recovery window. **If RC-3's back-dated unreachable-object gate did not
pass, this requirement is not met, whatever this brief does.** The two reflog settings stay for the
designer's own branch in Stage 3; they are not what this rule rests on.

**R-rc6-4a. A sweep runs at most once per session, on workspace close, after the close checkpoint and
in the same window as RC-3's packing** (§5.6, §12 Q16 — new in rev 4). **R-rc6-3's bound guarantees
nothing until the passes are counted**: a sweep on every checkpoint at one tenth empties the set inside
twenty checkpoints, while a sweep once per session cannot. Three alternatives each fail on a stated
principle — **on open** puts work in front of the thing the designer asked for, **on a timer** is
R-rc5-5's rejected idle trigger under a third name, and **on every checkpoint** is the case that makes
R-rc6-3 vacuous. Close is the boundary R-rc5-4 already identifies as reliably present in every session,
it is behind the user rather than in front of them, and it composes with packing — which stores what
thinning made unreachable compactly, and never reclaims it (R-rc6-4b).

**And a session that recorded nothing sweeps nothing and packs nothing** (§5.6, §12 Q24, rev 5). RC-5
R-rc5-4a keeps a colleague's glance from creating a repository on a share; on its own it did not keep
that glance from running a sweep, under the *reader's* retention preference, over the *owner's*
checkpoints — a per-user setting acting on a shared artifact, which is the identity mistake §4.4
corrected, in a third file. A close that took no checkpoint does no housekeeping. Two writers with
different preferences on one share still apply whichever closed last, bounded by R-rc6-1's floor; the
architecture records that as open (§12), and it is this brief's to report on.

**R-rc6-4b. Nothing reclaims what thinning freed unless a person asks** (§5.6a, §12 Q20, rev 5). rev 4
said *"until ordinary packing eventually reclaims them"* in the same document that set `gc.pruneExpire`
to `never`; both could not be true, and §1.4 decided for never. So the repository grows by what thinning
frees — kilobytes per checkpoint for design documents, and the whole of any import a designer included
and later regretted — and the answer to *"where did the disk go"* is RC-4's reclaim action, which calls
RC-3 R-rc3-16a with an age. **This brief owns the journal that age is measured against**: git's own
expiry is by object age, which is when a state was *made*, not when it was thinned, so R-rc3-16a
protects every journal entry newer than the age for its duration and removes the entries it acted on.
Reclaim is the one destructive control in the feature, it destroys only what has already been thinned,
and it is neither R-rc6-16's "delete all history" nor history rewriting: every live restore point and
every commit survives it.

**R-rc6-5. Human-written commits are out of scope for retention entirely.** They are small, they are
the designer's own record, and **no automatic process gets to delete them.**

**R-rc6-5a. Kept checkpoints are out of scope too** (§5.6 rule 6, §12 Q27, rev 5). RC-5 R-rc5-1f marks
three kinds: an explicit save-point, because the user's judgement about what matters beats any
heuristic and thinning it would discard exactly that judgement; the pair that brackets an off period
(R-rc6-14a), because they are what gives the gap its ends; and any restore point the designer marked
**keep**. A sweep counts them toward nothing and removes none of them, and R-rc6-3's fraction is
computed over the unkept.

---

## 2. The enclosing repository — hold, visibly

**R-rc6-6. circuitRF commits to exactly one repository: the one whose root is the open workspace.**
Not a repository above it, not a referenced workspace, not one nested inside. The reason is the same in
all three directions: **an automatic commit in a repository the user also uses for something else will
sweep up work that was not circuitRF's to commit.** A checkpoint is `commit -a`-shaped by nature
(RC-5 R-rc5-7) — it must capture everything, because the whole point is capturing files the user did
not open. In someone else's repository that is not a safety net, it is an ambush.

**R-rc6-7. What counts is decided by `git rev-parse --show-toplevel` from the workspace folder:**

| situation | behaviour |
|---|---|
| a repository circuitRF created at the workspace root | normal operation |
| a repository the **user** created at the workspace root | **hold, and ask** — R-rc6-7a. Their intent is unambiguous and the root is exactly right; taking it over unasked is presumptuous, but refusing to offer is unhelpful. |
| the repository root is an **ancestor** of the workspace | **hold, no adoption offered.** This is the serious case. Nor may circuitRF write `.gitignore`/`.gitattributes` into someone else's repository root. |
| a repository **nested inside** the workspace | that subtree is excluded and reported; the workspace's own history is otherwise normal. |

**`rev-parse` walks up, never down, so the fourth row needs a detection of its own** (§12 Q4 and Q30,
rev 5). The first three rows are one `rev-parse`; the fourth is a walk of the workspace tree for
directories named `.git`, done as the checkpoint enumerates files anyway (RC-5 R-rc5-1d), so it costs
nothing extra. It matters mechanically as well as by principle: handing such a directory to `git add`
records it as an *embedded repository* — a gitlink to that repository's commit — which is exactly the
*"committed as something by the enclosing workspace"* §7A.5 forbids, with a warning nobody is reading.
This brief finds it and reports it once; RC-5 R-rc5-7d keeps it out of every checkpoint by pathspec.

**R-rc6-7a. The workspace-root case is a QUESTION, not an offer** (§12 Q4 *Refinement 3*, owner's
decision, new in rev 4). rev 3 offered adoption as a one-click action and never said what the user was
choosing between. **The user is asked, told what keeping their own configuration costs, and encouraged
to adopt circuitRF's.** Three answers, not two:

| answer | what happens |
|---|---|
| **Adopt circuitRF's settings** *(recommended, and said to be)* | RC-3's repository configuration (R-rc3-7) and policy files (R-rc3-11a) are written; normal operation from then on |
| **Keep my settings** | circuitRF keeps history in the repository under the user's own configuration. A legitimate choice, and the consequences below are stated **at the point of choosing** |
| **Don't keep history for this workspace** | the hold state, unchanged — rev 3's behaviour, still available |

**The consequences of keeping are named specifically, not generally**, because each of R-rc3-7's rows
exists to prevent one particular failure — and **the first two silently revoke guarantees this brief
makes elsewhere**, which is why they lead:

- **`gc.pruneExpire` at its two-week default destroys thinned checkpoints permanently after a
  fortnight**, so **R-rc6-4's grace period does not exist** — inside §1.3's own recovery window.
- **`gc.reflogExpireUnreachable` at thirty days removes the way back from a reset or amend on the
  designer's own branch** before §1.3 says the designer looks for it — and with the row above, R-rc6-4's
  journal points at objects that are no longer there.
- `gc.auto` at 6,700 leaves two packing schedules running against each other (R-rc3-13).
- On Windows, `core.autocrlf` makes the `.clay` on disk a different file from the one
  `LayoutPersistence` wrote (R-rc3-12a), and `core.longpaths` unset is a refusal on that machine class
  only.

**One line must be drawn or "keep my settings" is read as "do everything their way": the answer governs
REPOSITORY CONFIGURATION ONLY.** R-rc3-1a's per-invocation set is circuitRF's whichever answer is given
— the commit identity, and the hook bypass. **Bypassing a hook is not a setting the user can keep**,
because it is not a property of the repository at all: it is R-rc6-6's rule that an automatic checkpoint
must neither fire somebody's tooling nor be blocked by it (R-rc3-7a), and it holds in every case.

**The ancestor row is not asked about**, because circuitRF may not write into someone else's repository
root at all — there is nothing to offer. **And this is where RC-3's management marker is written**
(R-rc3-7b), whichever answer is given: that is what makes the question asked **once** rather than on
every open, and what lets R-rc5-6g advertise a state it can actually determine.

**R-rc6-8. Held must not look like absent.** RC-3 hides every affordance when git is missing, because
absent is harmless. **Held is a designer who may believe they are protected**, so in the hold state the
commit action **stays visible and refuses, saying why** — a hidden button is indistinguishable from a
feature that was never there.

**R-rc6-9. Three reports, at three cadences**, and the cadences are the design:

- **On workspace open** — a message stating that circuitRF is not keeping history for this workspace,
  why, and **how to remedy it**.
- **On an attempted commit** — a refusal saying why, **without restating the remedy**. The user has
  already been told once; repeating it on every attempt is how a message becomes noise.
- **A persistent, non-scrolling indicator** — a status-bar item or a badge on the project-tree root,
  reading that history is off for this workspace. **This is the measure most likely to actually prevent
  the false belief**: a scrolling log is read once and then trained against, while the state is
  permanent for the session and should be displayed permanently.
- **And a subtraction: automatic checkpoints do not each post a message.** A skipped AI-batch
  checkpoint posts once per session (RC-5 R-rc5-11), and failure reporting follows the same rule.

**R-rc6-10. The indicator is shared with §5.7's "off" state and with §4.4's failure state**, because
all three mean the same thing to the designer: *nothing is being recorded right now.* One indicator,
three reasons, and the reason is in its text. Three separate indicators would be three things to
notice.

---

## 3. Off, and what it is not

"Can I switch this off for a workspace, and does that delete the `.git`?" has three answers, and
conflating any two is how a designer loses history they meant to keep.

| state | what circuitRF does | what happens to the history already taken |
|---|---|---|
| **On** | takes checkpoints, offers commits | accumulates |
| **Off** | **writes nothing at all** | **kept, browsable, restorable** |
| **Removed** | not a circuitRF action at all | gone, permanently |

**R-rc6-11. Off means circuitRF stops writing. It never deletes anything.** Three reasons, the first
sufficient on its own:

- An off switch that destroyed a history would be **the single most damaging control in the
  application.** Nobody expects a checkbox to be irreversible, and by the time they discover it was,
  there is nothing to discover it with.
- **Off and on must be symmetric.** Turning it back on resumes the existing history. If off deleted, on
  would silently start from nothing and the restore points a designer remembers would be gone with no
  event that explained it.
- **The escape hatch requires the repository stay an ordinary git repository.** Off is circuitRF
  declining to write; it is not a change to the repository's format or contents.

**R-rc6-12. While off, the history stays readable.** Restore points already taken remain browsable and
restorable. There is no reason to hide them, and hiding them would imply they were lost.

**R-rc6-13. The off period is shown as a gap, not as a quiet interval.** A designer scanning the
history later must be able to see that nothing was recorded between two dates **because recording was
off**, not because nothing happened. Rendering it as an ordinary interval between two commits is the
false-belief failure of §1.4 in its purest form. (RC-7 owns the browser; this brief owns the fact the
gap is recorded and the requirement that the browser show it.)

**R-rc6-14. Off is per-workspace state in the `.cws`** — RC-4 R-rc4-12 places it, and the reason is
that an installation-wide flag is correct for the first workspace and silently wrong for the second, a
mistake this repo has already made once and recorded. It goes in the **versioned** half, which has a
pleasing side effect: **turning it off is itself a recorded change**, so the last commit before the
history goes quiet is the one that says why.

**R-rc6-14a. The off transition is ordered, and the ordering is the whole of R-rc6-14's side effect**
(§5.7, §12 Q17 — new in rev 4). Turning it off **writes the `.cws`, then takes one final checkpoint
recording that change, and only then stops writing.** Reverse the two and the flag is set, circuitRF is
already off, nothing is committed, and the history simply stops with no entry saying why — which is
precisely what R-rc6-14 claims does not happen. Turning it back on records the resumption at the next
boundary. **The pair is what gives the gap two ends**, which is what R-rc6-13 needs in order to be
renderable as a gap rather than as a quiet interval. This is R-rc5-21's ordering problem in another
place: the `.cws` is written before the checkpoint that is supposed to contain it. **Both carry the
kept mark** (R-rc6-5a, rev 5): a pair that retention could thin is a gap that retention could erase,
and the erased gap renders as exactly the quiet interval R-rc6-13 forbids. **And a restore preserves
the flag** (RC-5 R-rc5-12c): the `.cws` travels with the tree, so without that rule a restore across
an off period would silently switch recording on or off — a restore is a decision about content, not
about recording.

**R-rc6-14b. The per-workspace flag defaults to RC-4's application preference** (§5.7a, R-rc0-13,
R-rc4-12a). A workspace that has never recorded a setting takes the preference's value; one that has
keeps its own, and **is not silently rewritten when the preference changes.** The two answer different
questions — *"do I want this at all"* versus *"not for this one"* — and R-rc6-14's rule that an
installation-wide flag cannot gate per-workspace state is untouched: the preference is not that flag, it
is the value the flag falls back to.

**R-rc6-14c. Because the flag is in the `.cws`, it travels** (§5.7, rev 5). A clone or an archive of a
workspace that was switched off arrives switched off, and the recipient's preference does not override
it: the flag says *not for this one* about the workspace, and the workspace is what travelled. The
recipient is told at their first boundary through R-rc6-10's indicator, exactly as the hold is
reported, with the setting one click away. RC-9 R-rc9-5c is corrected accordingly.

**R-rc6-15. An AI edit requested while revision control is off states plainly that no restore point
will be taken, and offers to turn it back on — before anything is modified.** RC-4 makes the AI-batch
checkpoint non-switchable because it is §1.2; switching revision control off for the workspace switches
it off too. **That is a legitimate thing for a user to choose and an illegitimate thing for them to
stumble into**, so the conflict is resolved out loud, at the moment it matters, and never silently.

**R-rc6-16. There is no "delete all history" command, at any stage.** A user who genuinely wants the
history gone deletes the `.git` folder — one folder, plainly named, using their file manager or the
escape hatch. An irreversible, total, one-click destruction of the thing this feature exists to protect
has no safe place in the UI, for the same reason there is no history-rewriting command.

**R-rc6-17. The reassurance is stated in the product and in the docs: removing revision control cannot
damage the design.** The design was never *inside* git in any meaningful sense — a workspace is
ordinary files in a folder, and `.git` is a sibling directory holding copies. Delete it and the
workspace opens exactly as it did, every file present and current. **This is the single most calming
thing that can be said to a designer nervous about letting version control near their work**, and it is
a direct consequence of git being a storage engine rather than a user interface.

---

## 4. Gates

1. **The count floor beats age** (R-rc6-1): set every checkpoint's timestamp a century in the past and
   assert the newest *N* survive a sweep untouched.
2. **A backwards clock does not reorder** (R-rc6-2): interleave checkpoints with the fixture clock
   moving backwards and assert the sweep removes the ones RC-5's sequence says are oldest.
3. **A bounded sweep refuses and reports** (R-rc6-3): construct the clock-jump case and assert **no
   deletion and one message**, not a partial deletion.
4. **Thinning is not pruning, and the journal is the way back** (R-rc6-4): after a sweep, the dropped
   checkpoints' objects are still in the repository, each dropped checkpoint is listed as thinned, and
   restoring one from the journal produces its exact tree. **Then, in a scratch copy, prune with an
   immediate expiry and assert the thinned commits are gone and every kept and live one is present** —
   the consumer-side half of RC-5 gate 14a, which is what proves the sweep freed anything at all.
4a. **Reclaim honours the journal's ages and removes what it acted on** (R-rc6-4b): thin three
   checkpoints at three journal times, reclaim with an age between them, and assert the newer two are
   still listed as thinned and restorable, the older one is gone from both the repository and the
   journal, and no live or kept restore point moved.
5. **Human commits are never swept** (R-rc6-5), including one older than every checkpoint.
5a. **Kept checkpoints are never swept** (R-rc6-5a): a save-point, both transition checkpoints and a
   user-kept restore point, each older than every unkept one, survive a sweep that removes the unkept —
   and are not counted toward R-rc6-3's fraction.
6. **All four situations** (R-rc6-7) are driven by a real fixture tree and produce the right behaviour —
   the **ancestor** case is the one that matters most, and it must assert **nothing was committed and
   nothing was written into the ancestor's root**. The **nested** case is a `git init` inside a cell
   folder, found by the walk and not by `rev-parse` — assert it is reported once, and that RC-5's
   checkpoint carries neither its files nor a gitlink.
7. **Adoption writes the policy files and the configuration** into the user's repository, and a
   `pre-commit` hook in it neither blocks nor fires on the checkpoints that follow (R-rc6-7,
   R-rc3-7a).
7a. **All three answers to the ask do what they say** (R-rc6-7a): *adopt* writes R-rc3-7's configuration
   and the policy files; *keep* writes **neither**, and the repository's own configuration is
   byte-for-byte unchanged; *don't keep* is the hold state. **And in all three, R-rc3-1a's per-invocation
   set is still circuitRF's** — assert that a `pre-commit` hook does not fire even under *keep*, which is
   the reading of "keep my settings" that must not be built.
7b. **The question is asked once** (R-rc6-7a, R-rc3-7b): answer it, close, reopen — assert no second
   prompt, and that the recorded answer is what drives the behaviour.
8. **The grace period is measured, not assumed** (R-rc6-4): thin a checkpoint, back-date its now
   unreachable objects past two weeks, run RC-3's packing path, and assert they are still recoverable.
   **This is RC-3's back-dated unreachable-object gate seen from the consumer's side**, and it is the
   assertion that makes this
   brief's central promise true rather than intended.
9. **Held is visible** (R-rc6-8): the commit action is present and refuses. Assert presence, not
   absence — this is the inverse of RC-3's absence gate and the pair is the point.
10. **Cadence** (R-rc6-9): one message on open, a refusal per attempt with no remedy restated, one
   persistent indicator, and no per-checkpoint messages.
10a. **A sweep runs on close and only once** (R-rc6-4a): drive ten checkpoints in one session and
    assert **no sweep ran**; then close, and assert exactly one did. This is what makes R-rc6-3's bound
    a guarantee rather than an arithmetic curiosity.
10a'. **A session that recorded nothing does no housekeeping** (R-rc6-4a): open an armed workspace,
    change nothing, close — assert no sweep, no pack, and that the repository directory's bytes are
    unchanged. This is the share case, and the assertion is on the bytes.
10b. **The off transition is ordered** (R-rc6-14a): switch off, then assert the **last** thing in the
    history is a checkpoint that contains the `.cws` with the flag already off. Reversed, the flag is
    set and nothing records it — which is the defect, and it is invisible from the flag alone.
10c. **The workspace flag defaults to the preference and is not rewritten by it** (R-rc6-14b): a
    workspace with no recorded setting follows the preference; one that recorded a setting keeps it when
    the preference is changed. **The second half is the one that would be missed.**
11. **Off writes nothing and deletes nothing** (R-rc6-11): switch off, edit, close, reopen — assert the
   repository is byte-for-byte unchanged and every prior restore point is still listed and still
   restorable (R-rc6-12).
12. **On resumes the same history** (R-rc6-11): off, then on, and the restore points from before the
    off period are still there and still ordered correctly.
13. **The gap is recorded** (R-rc6-13) in a form the browser can render as a gap — assert the data
    exists, since the rendering is RC-7's.
14. **Two workspaces hold independent off state** (R-rc6-14) — the mistake the repo made before.
14a. **The flag travels** (R-rc6-14c): switch a workspace off, archive it and clone it, open each with
    the preference on, and assert both are off, both show the indicator, and neither wrote a checkpoint.
15. **An AI edit while off refuses first and offers to re-arm** (R-rc6-15), and **nothing is modified**
    before the answer. Assert the file mtimes, not just the prompt.
16. **No delete-history command exists** (R-rc6-16): source-scan for one and fail on a hit, comments
    stripped.

---

## 5. Documentation (§10B.1, §10B.2 scenarios 7, 8, 9)

- **Scenario 7** — *"I turned the retention setting down and now I can't get back to last month."*
  **The scenario this documentation exists for. It must appear before a user lives it.**
- **Scenario 8** — *"My workspace is inside another git repository."* Why circuitRF stopped, what is
  therefore not being kept, and how to fix it.
- **Scenario 9** — *"I want to turn this off."* Two things a user actually needs: switching it off keeps
  everything already recorded, and getting rid of it entirely means deleting one folder, which
  **cannot harm the design**. The second half is the reassurance and belongs in the docs because the
  people who most need it are the ones least likely to ask.
- **Scenario 14** — *"this workspace already had version control and circuitRF asked me something."*
  (R-rc6-7a.) The three answers in plain words, and what keeping their own settings actually costs.
  **The two consequences that matter are recoverability ones, not tidiness ones** — a fortnight instead
  of the promised weeks, and an escape hatch gone at thirty days — so the chapter says that, rather than
  listing configuration keys at a reader who did not come for them.
- **Scenario 15** — *"My disk is full and it says the history is taking the space."* (§5.6a,
  R-rc6-4b.) That thinning keeps the states, that reclaiming is the one action that does not, and
  exactly what it destroys. RC-4 owns the control; this brief owns the explanation, because the journal
  is what the explanation is about.
- **The §10B.1 rows**: anything saved while revision control is off is not kept and not recoverable —
  *but everything recorded before it was switched off is still there*; anything at all when an enclosing
  repository was detected is not kept and not recoverable; **a restore point that retention thinned is
  still restorable, marked as thinned, until a reclaim — which asks first.** **Wherever a row says no,
  the docs say no plainly.**

---

## 6. On completion

Findings to `src/Design/RESOLVED.md` and `src/Ui/RESOLVED.md` — **never to a `CLAUDE.md`**.

**Report, do not silently absorb:**
- Any clock-fault shape the five rules do not cover.
- **Whether RC-3's packing configuration actually held.** If a thinned checkpoint's objects
  did not survive a back-dated pack, this brief's grace period does not exist, and that is a stop —
  rev 2 of the architecture asserted the opposite and was wrong.
- **How many people, in review, read "keep my settings" as "and run my hooks"** (R-rc6-7a). If the
  wording admits that reading at all, change the wording — it is the one answer in the dialog that can
  quietly re-enable the ambush R-rc6-6 exists to prevent.
- Whether the persistent indicator found a home that is genuinely non-scrolling, and where.
- Any path by which a checkpoint could reach a repository other than the open workspace's own
  (R-rc6-6). That is the finding that matters most in this brief.
- **Two writers with different retention preferences on one shared workspace** (R-rc6-4a): whether it
  arose in review, and whether retention should become per-workspace state in the `.cws`. The
  architecture holds this open until this brief; do not settle it silently in either direction.
- Whether the journal survived a hand-run `git gc` and a `git clone` (R-rc6-4) — it lives under
  `.git/circuitrf/`, which the first ignores and the second does not copy, and both are the intended
  answers.

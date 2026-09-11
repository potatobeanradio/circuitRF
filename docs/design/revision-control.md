# circuitRF — Revision Control (architecture)

**Status:** Proposal — **rev 6**, owner decisions §12 Q1–Q31 taken and Q32–Q36 added. rev 5 settled the
mechanics; **rev 6 is the first revision driven by looking at the shipped panels rather than by
re-reading the document** (owner UX review, 2026-09-07), and it changes two things this document had
settled: the safety net and the narrative are read in **one panel** (§5.10), and **what a person wrote
about a state may be corrected by that person** (§5.11) ·
**RC-1 (§3.1/§3.1a, the `.cwsuser` split) is BUILT** — 2026-09-06; the last "still open" item at the
end of §12 was settled with it. **RC-2 (§7A.2/§7A.3, referenced workspaces read-only by default) is
BUILT** — 2026-09-06, and it needs no git: it is the defect in the workspace model this investigation
found, specified now in `workspace-and-project-tree.md` §5C.1a where the next reader will look.
**RC-3 (§4, §4.5–§4.7, §5.2b's commit primitive, §5.6a's reclaim, §2.4's packing, §8.1/§8.1a's policy
files, §3.2's revoked gzip reserve) is BUILT** — 2026-09-06, in `src/Design/Revision/`, with no
user-facing revision-control surface: §2.4's overhang re-measured at **26×** on this machine class (148
MB loose against 5.7 MB packed, at **138 loose objects** — 2% of git's own 6,700 trigger), and §3.2's
gzip penalty reproduced at **594×** per mid-file drag and **1,594×** per deletion. The version floor is
**2.9.0**, set by `core.hooksPath`; `safe.directory` does not raise it. Findings in
`src/Design/RESOLVED.md`. **Stage 1's substrate is complete** — its remaining item, §8.2's large-file
guard, lands with RC-5 for the reason R-rc0-2 gives: it is a prompt shown before a commit, and Stage 1
takes none, so a dialog with no call site would be a dead and untestable feature.
**RC-4 (§10A, the Settings tab) is BUILT** — 2026-09-06: the nine application preferences, §5.7a's
per-user *keep a history of my workspaces* (shipping **on**), §5.7's per-workspace flag in the `.cws`,
§5.6's retention age and count floor, §2.4's packing threshold and §5.6a's reclaim as the tab's one
confirmed destructive action. Measured while building it: five tab headers need the scoped `TabItem`
style — on the theme's own metrics they come to **850 px against 592 px** of usable width at
`MinWidth="620"` and two of them wrap, and **the four-header strip was already over that line at 656 px
before this tab existed**. §5.6 rule 4's journal had no format, so RC-4 defined the reader RC-6's writer
inherits (`.git/circuitrf/thinned.jsonl`, one appended JSON object per line). Findings in
`src/Ui/RESOLVED.md`. **Stage 2 has its settings surface and no checkpoint yet; everything from RC-5
onward remains proposal.** ·
**Date:** 2026-09-06 · **Phase:** unassigned

Specifies how circuitRF gives a workspace a **history** — the ability to see what changed, and to get
back to a state that worked — using **git at arm's length** (the installed `git` executable, driven as
a subprocess) as the storage engine.

This is an architecture note. It states the principles, the measurements that justify them, the
file-format consequences, and what is deliberately excluded. It is **not** an implementation plan; the
phase briefs come after this document is agreed.

Companions: `project-file-formats.md` (`.cws`/`.ccell`/`.csch`/`.clay` conventions),
`workspace-and-project-tree.md` (filesystem-is-truth, external cell references §5C, referenced
workspaces §5B), `scratch-and-save-lifecycle.md` (the save lifecycle this hooks into),
`layout-view.md` §4 (`.clay` and the gzip reserve — **§3.2 below revokes that reserve when git is in
play**), `automation-architecture.md` (the headless surface a checkpoint operation must not depend
on), `user-docs-factory.md` (where §10B's user-facing chapters are authored and built).

**What changed in rev 2.** §2 and §3.2 are re-measured against a **real 28.4 MB board `.clay`**
rather than a synthetic file, per rev 1's own §2.3 — both hold, and one new and consequential finding
appears (§2.4, packing). §3.1's split is decided and is larger than rev 1 described. §5.3's checkpoint
boundaries are decided. §5.5 (commit wording and Messages reporting), §5.6 (retention, and what a
clock change must not do), §5.7 (off, paused and removed), §7A (managed collections of workspaces —
the librarian), §8.2a (the add-once-then-ignore option), §9A (archiving, and the one default in this
document that guards an irreversible failure), §10A (the Settings tab) and §10B (what the user must be
told) are new. **§7A.2's read-only default is not a revision-control feature** and ships whether or not
git is present — see §7A.2 and §11.

**What changed in rev 3.** rev 2 was reviewed against the nine implementation briefs written from it.
The briefs cover it; the review found things the **architecture** had not settled. Two of them are
corrections to rev 2 rather than additions, and both are load-bearing:

- **§2.4's "routine packing prunes nothing" was wrong.** Plain `git gc` prunes unreachable objects
  older than two weeks and expires the reflog, by default, without being asked. §5.6 rule 4's grace
  period was therefore two weeks and then permanent — a guarantee that quietly expired. **§4.5 now
  specifies the repository configuration** that makes the claim true, because the guarantee belongs in
  configuration and not in which flags circuitRF remembers to leave off.
- **Checkpoints kept on a reference outside the branch do not survive a clone** and are not pushed.
  **§5.2a** settles their exact shape — which is also what makes §5.6's thinning implementable at all —
  and states which of the three ways a workspace leaves a machine carries them.
- **§4.4 put the commit identity in the wrong place.** rev 2 reasoned correctly that circuitRF must not
  write `user.name` into the user's *global* git config, and then wrote it into the *repository's*
  config instead — which makes a person's identity a property of a directory. On a network share, the
  second designer to open the workspace commits under the first one's name. **§4.4 and §4.5 now hold
  the identity as a per-user circuitRF preference, supplied per invocation, written into no config file
  at all.**

The rest are gaps rather than errors: **§4.5** (what circuitRF configures, and what in the user's own
git configuration would otherwise break every checkpoint silently), **§4.6** (two processes, one
repository), **§4.7** (the version floor, and `safe.directory` on a network share), **§5.3a** (what an
AI-initiated *batch* actually is — the governing motive's trigger was undesigned), **§5.8** (restoring,
which writes over every open document and must take a checkpoint of its own first), **§5.9** (scratch
workspaces, which have no folder to hold a repository), **§8.1a** (existing workspaces, which under
rev 2 would never receive a `.gitignore` at all, and the first commit into one), **§9.1** (credentials,
and the hang that follows from not thinking about them), and **§9A.6** (Save Workspace As — the third
way a workspace leaves a machine, and the only one rev 2 did not consider).

**What changed in rev 4.** rev 3 was reviewed against the nine briefs a second time. The coverage is
complete — every section of rev 3 is claimed by a brief, every §10B.1 row and all twelve §10B.2
scenarios are assigned — so rev 4 adds no mechanism that rev 3 described and the briefs missed. What it
adds is the set of questions rev 3 **did not ask**, three of which were load-bearing:

- **Nothing said what arms a workspace in the first place.** "Arming" was used throughout as a settled
  concept and its default was stated nowhere. **§5.7a and §12 Q13** settle it: a per-user application
  preference is the default, an explicit per-workspace setting outranks it, and a workspace is armed at
  the first boundary that would record something rather than merely at open.
- **An out-of-process agent edits files under an open window, and nothing said what the window does
  about it.** §5.8 works this out carefully for a *restore* and §5.3b rule 5 forbids the agent from
  reaching it through git — but the agent's own edits, through the batch, are the identical situation.
  **§5.3c** is that answer, and it is §1.2's own motive finishing its sentence.
- **"A repository circuitRF created" was load-bearing three times with no stated mechanism.** §12 Q4,
  §9A.5 and §5.3b all turn on it, and §5.3b says outright that a `.git` directory alone cannot
  distinguish the two. **§4.5 now carries the marker**, and **§12 Q4 becomes a question the user is
  asked** rather than an action offered silently.

The rest are smaller and each is silent when missed: **§5.6** now says *when* a sweep runs, without
which its bound guarantees nothing; **§5.7** gives the off transition a commit ordering, without which
its "turning it off is itself a recorded change" is aspirational; **§3.1a** splits the `.cwsuser`'s
archive treatment from its copy treatment, because the two consumers share one skip list and want
opposite answers; **§9A.3** says which references the deleted-file warning walks, which is the one
computation in this document whose incompleteness cannot be undone; and **§10A** gains the row §12 Q13
creates.

**What changed in rev 5.** rev 4 was reviewed against the nine briefs a third time. Coverage was again
complete, and the rev 3 and rev 4 corrections were re-checked against git's documented behaviour and
stand. What this pass found is different in kind: **mechanisms the document treated as settled by
naming them, which on inspection do not do what the sentence around them says.** Five of those silently
revoke a guarantee made elsewhere, and each is now specified rather than assumed:

- **A checkpoint's parent was never stated, and with the wrong parent §5.6 thins nothing.** One
  reference per checkpoint (§5.2a) makes a deletion free objects only if the next checkpoint does not
  name the previous one as its parent. **§5.2b** fixes the shape: parentless commits, built through a
  temporary index, capturing every file the workspace holds including ones no checkpoint has seen.
- **The reflog was named as the escape hatch after a sweep, and it is not one.** Deleting a reference
  deletes its reflog with it, and references outside `refs/heads/` carry none by default. What survives
  a sweep is the unreachable objects, because §4.5 forbids pruning them; **§5.6 rule 4** now says so,
  and circuitRF keeps a journal of what it thinned so recovery is one action rather than an `fsck`.
- **§4.5 said objects are never pruned and §5.6 said packing eventually reclaims them.** Both cannot be
  true. **§5.6a** decides: nothing reclaims automatically, ever — which is what §1.4 already required —
  and reclaiming is an explicit, confirmed action on the Settings tab.
- **§4.6 asked the advisory lock to serialise writers, and its own header says it cannot.**
  `WorkspaceLock` holds no handle, is overridable in both directions, and exists to *notice* two
  sessions on one workspace — precisely the situation §5.3c designs for. **§4.6** now lets git's own
  atomic locks serialise, behind a temporary index that keeps checkpoints out of the shared index.
- **The headless batch could not read the identity §4.4 captures.** `AppPreferences` lives in
  `src/Ui`, so `serve` fell through to git's own resolution, which on the fresh Windows machine §4.4
  describes names nobody — and the governing motive's checkpoint was refused for exactly the population
  it targets. **§4.4** moves the identity's storage to a per-user file a `src/Design` reader serves to
  both processes.

Two are corrections to premises rather than to mechanisms. **§6.3 asserted that git requires a branch
after a restore.** It does not: a restore that writes a checkpoint's tree into the working tree and
never moves `HEAD` makes restore-then-edit linear, and the variant concept, its UI and its
documentation scenario are withdrawn (**§12 Q19**). **§4.3 assumed that a `git` on `PATH` is a git.**
On macOS it is a stub that opens an Apple installation dialog when run, so detection must check for
the developer tools first (**§4.3a**).

The rest are rules for cases that had none: what *"would record something"* means before a repository
exists (**§5.7a**); what a restore does to files created after the checkpoint, to ignored results, to
the `.cws`'s own off flag and to the policy files, and how an interrupted restore is detected
(**§5.8**); what a checkpoint does with an unexpectedly large file at a boundary where nobody can
answer §8.2's question (**§8.2b**); which checkpoints retention may never thin (**§5.6 rule 6**); that
a session which recorded nothing on a shared workspace sweeps and packs nothing (**§5.6**); how a
nested repository is actually found, since `rev-parse` walks up and not down (**§12 Q4**); the
concrete channel by which `serve` and the window exchange the two facts §5.3c needs (**§5.3c**); the
headless surface for every history operation (**§5.3d**); §4.7's remedy, supplied per invocation
rather than left to a config write; and §5.5's fourth row, the explicit save-point, whose message the
brief that creates it must own.

**What changed in rev 6.** The first five revisions were reviews of this document against itself and
against the briefs written from it. **rev 6 is a review of the shipped feature by the person it is
for** — Stages 1–4 are built, and the owner opened the two panels and asked why there are two. That is
a different kind of evidence and it found a different kind of defect: nothing here is unspecified or
mechanically wrong. What it changes is **what the mechanisms are presented as**, and in one case a
principle that was inherited rather than derived.

Two of the five are decisions this document is reversing, and both reversals are stated as such rather
than folded in quietly:

- **§5's two histories were being read in two panels, and the second panel was never argued for.**
  §5's opening argument is that the safety net and the narrative have different authors, granularity
  and audiences — which is an argument for two *kinds of entry*, and it was silently spent as an
  argument for two *windows*. The reason actually given for the split, in the user documentation, is
  that a list of three hundred automatic entries with four deliberate ones among them is unreadable —
  and that is **an argument for a filter**. **§5.10** merges the panels, keeps the two kinds visibly
  distinct, and makes the default view the entries somebody stated an intent for. It supersedes RC-7
  R-rc7-9, which said in terms that they are not merged.
- **§8.3's refusal to rewrite history was imported from a situation circuitRF is not in.** The rule is
  git's, and git's reason is that a rewrite invalidates every existing clone — which is true, and which
  §8.3 states correctly. It is also irrelevant to the overwhelmingly common circuitRF workspace: one
  designer, one machine, no clone, and a title typed thirty seconds ago with a word spelt wrong in it.
  Applying the many-contributor constraint to the single-designer case inherits a cost with none of the
  benefit, and it produces a designer who will not share a history they cannot tidy. **§5.11** replaces
  the blanket rule with the line that actually holds: **what was recorded is never altered; what a
  person wrote about it may be corrected by that person, until it has been shared — after which it can
  only be annotated.** §8.3 keeps its own subject, which was never titles.

The other three are smaller, and one of them is a defect in built code rather than in this document:

- **The entry that makes *going back is never a one-way door* true is thinnable.** §5.8's pre-restore
  checkpoint is what a designer comes forward to, and §5.6 rule 6's kept list does not include it —
  `WorkspaceCheckpoints.IsAlwaysKept` covers a save-point and the two recording transitions and nothing
  else. It is not data loss (the objects survive §4.5, and RC-6's journal brings the entry back), but
  the one entry whose entire purpose is to undo a destructive operation can leave the list on its own.
  **§5.6 rule 6 gains a fourth kind.**
- **The reassurance lands in the panel the designer is not looking at.** A restore begun from the
  Versions panel puts its pre-restore checkpoint in the Restore Points panel, so the window the
  designer just acted in shows no evidence their afternoon survived. **§5.8** now requires the way
  forward to be offered where the way back was taken — which §5.10's merge makes possible at all.
- **The card says less than it knows, and one of the omissions is wrong rather than sparse.**
  A version's date renders as `ddd d MMM` with no year, so a version kept last December reads as one
  kept this week (`VersionHistoryTool.cs`). §5.10 states what a row carries, what its expander carries,
  and that the *go back* action may not wear the undo glyph — the application's own undo arrow, on the
  one action the documentation is at pains to say is not an undo.

---

## 0. The governing principle

**Git is a storage engine, not a user interface.**

Every design decision below follows from this. The designer is never shown a commit graph, a branch
name, a detached HEAD, or a merge conflict. They are shown *their own design at an earlier moment*.
Git is what makes that cheap and durable; it is not what the feature is about.

The corollary is equally binding: **because git is at arm's length, the user's own `git` remains the
escape hatch.** Anything circuitRF's UI cannot fix, the command line can. That property is worth more
than any convenience an embedded library would buy, and it is lost the moment circuitRF starts writing
a repository format only circuitRF understands.

**One qualification added in rev 2, because §5.5 would otherwise look like a violation.** "No git
vocabulary" governs what is shown to a designer who never asked for version control. It does not
govern what is shown to someone who has just pressed **Commit** — that person has opted in, and
withholding the identifier of the thing they just created would be coyness, not clarity. The rule is
therefore: *nothing git-shaped appears unbidden;* what an explicit action produces may be named
precisely.

---

## 1. Why this exists

Two motives, and they are not equally strong.

**1.1 The weaker motive: designers want history.** True, but they cope today with archived workspaces
and dated copies, and they cope adequately. On its own this does not justify the surface area.

**1.2 The governing motive: AI-authored edits need a floor.** As circuitRF gains AI design capability,
an agent will modify files the user did not open, in numbers the user cannot review, faster than the
user can watch. The existing undo stack does not cover this: it is per-view, it does not span files,
and it does not survive a crash or a restart.

What that motive actually demands is narrow and specific:

> **an automatic checkpoint taken immediately before every AI-initiated batch, spanning every file in
> the workspace, surviving a crash, revertible in one action.**

Note what it does *not* demand: branches, merges, a commit message, or any git vocabulary whatsoever.
This is the single most important observation in this document, because it means **the safety feature
and the designer-facing feature are separable**, are wanted by different people, and carry very
different risk. §5 and §11 build on that split.

**1.3 What "corrupt" means here.** Not file corruption — malformed JSON is caught by the validators
already (`CellViewFileValidator`, and `circuitrf check`). The failure this guards against is a
*well-formed design that is wrong*: an agent that widened the right trace on the wrong layer, or
retuned a match that was already correct. Nothing detects that but the designer, and they may not
detect it for a week. **The recovery window must therefore be measured in weeks, not in an undo
stack.**

**1.4 The standing constraint this whole document is written under.** Losing a designer's work, or
their employer's design IP, is not one failure among many — it is the failure that ends the feature's
credibility and possibly the product's. Two consequences run through every section below and are
called out where they bite:

- **Every setting that can reduce what is kept must say so where the setting is** (§10A), not only in
  a manual. A designer who believes they can get back to last Tuesday and cannot has been failed by
  this design, whatever the release notes said.
- **No automatic operation may destroy history.** Retention thins; it never prunes to permanence
  (§5.6), and nothing reclaims what thinning freed unless a person asks (§5.6a). History rewriting is
  not offered at all (§8.3).

---

## 2. Is git viable on these files? — measured on a real board

The concern that motivated this investigation was that layout files are large and saved often, so a
repository would grow without bound.

**It does not.** rev 1 measured a synthetic file and flagged (its §2.3) that the check had to be
repeated against a real one. It has been. The subject is a board cell from a real workspace:
**28,418,662 bytes, 1,570,212 lines, 3,284 shapes** (1,928 `Poly`, 1,172 `Path`, 113 `Rect`, 71
`Circle`) totalling **673,345 vertices**, from 2-vertex paths to a 2,620-vertex pour. That is denser
than the synthetic stand-in (2,700 shapes) in every dimension.

Edits applied one commit at a time, `.git` measured after each group:

| operation | `.git` after | cost per commit |
|---|---|---|
| initial commit of the 28.4 MB `.clay` | **4.92 MB** | — |
| + 20 commits, each adding a shape | **4.96 MB** | **2.1 KB** |
| + 20 commits, each dragging a mid-file polygon (every coordinate of one shape rewritten) | **5.07 MB** | **5.3 KB** |
| + 5 commits, each deleting a mid-file shape | **5.08 MB** | **1.3 KB** |
| + 1 commit reordering all 3,284 shapes | **5.12 MB** | **41 KB** |

**Forty-six design commits cost 197 KB against a 28.4 MB layout.** rev 1's conclusion stands and is
strengthened: **no change to how `.clay` is written is required**, and the "minimal-diff serializer"
this investigation set out to design is unnecessary.

**2.1 Why it already works.** Four existing properties, none of them adopted for this reason:

1. **`WriteIndented = true`** (`LayoutPersistence.cs`) — the file is line-oriented text.
2. **Coordinates are `long` DBU**, never floats — no formatting or round-trip churn, ever. This is the
   quiet hero: a float-coordinate format would produce diff noise on every save that touched nothing.
3. **Undo/redo re-inserts at the original index** (`AddShapeCommand`, `DeleteShapesCommand`,
   `ReplaceShapesCommand`) — shape order is stable across an edit-undo-redo cycle.
4. **`LayoutClipper.EnsureValidHoles` fast-paths already-valid shapes** — load→save is idempotent, so
   merely opening and closing a layout produces no commit-worthy change.

**2.2 The result that overturns the premise.** The reorder row is the surprising one, and the real
file is *better* than the synthetic was: shuffling all 3,284 shapes costs **41 KB**, where the
synthetic predicted 220 KB. Git's delta compression is **content-based** (a binary delta over the
whole blob), not line-based. **Shape-order stability matters far less than intuition suggests**, and
no future feature needs to preserve it *for git's sake*. It should still be preserved for the human
reading a diff — but that is a different, weaker requirement, and it must not be allowed to constrain
the serializer.

**2.3 The premise that survived measurement, and the one that did not.** The file-size premise is
dead: the repository is cheap. **The premise that replaced it is §2.4, and it is the one a brief must
actually design against.**

### 2.4 The real cost is packing, not history — and nothing runs it *(new in rev 2)*

Every figure above is a **post-`git gc`** figure. Between commits, git writes each new version of the
file as a **loose object**: a standalone zlib-compressed copy of the *entire* blob, with no delta
against anything. Measured on the same board:

| | |
|---|---|
| 21 commits (initial + 20 mid-file drags), no `gc` | **63 loose objects, 124.3 MB** |
| the same repository after one `git gc --prune=now` | **12 MB** |

**A tenfold overhang, and it does not clear itself.** Git's automatic housekeeping (`gc --auto`, which
porcelain commands including `git commit` invoke) triggers on **`gc.auto`, whose default is 6,700
loose objects** — a *count*, with no notion of size. A workspace whose history is a handful of
enormous files will sit at a few dozen loose objects indefinitely, hundreds of megabytes over its
packed size, and git will never once decide to do anything about it.

This is exactly the shape of complaint the feature exists to avoid — *"circuitRF filled my disk"* —
and rev 1 missed it because it measured after `gc` at every step.

**Three consequences for the architecture:**

- **circuitRF owns packing.** A brief must schedule `git gc` itself, on a **byte-based** trigger
  (loose-object bytes over a threshold), not on git's count-based one. Set `gc.auto = 0` in the
  repository circuitRF creates so the two schedulers cannot fight.
- **It runs where it cannot be noticed.** Packing 124 MB is seconds of CPU and heavy I/O; it belongs
  after a workspace closes, or idle, never in front of a save or a checkpoint. It must be
  interruptible and must be safe to abandon — `git gc` is, which is part of why it is the right tool.
- **Packing must be *configured* not to prune, and omitting `--prune=now` does not achieve that.**
  This is a correction to rev 2, which asserted that routine packing prunes nothing. It does: `git gc`
  runs a prune at **`gc.pruneExpire`, whose default is two weeks**, and expires the reflog at
  `gc.reflogExpire` (90 days) and `gc.reflogExpireUnreachable` (**30 days**) — all unasked, and all
  invisible. §5.6 rule 4's "grace period measured in weeks" and §1.3's weeks-long recovery window both
  depend on that not happening, so **the guarantee lives in the repository's own configuration** (§4.5),
  not in a flag circuitRF has to remember to leave off. Leaving it off remains necessary; it was never
  sufficient.

**2.5 Standing caveat, now much narrower.** The figures above are one real board on one machine. What
they establish is the *shape* of the cost — kilobytes per commit for text, an unbounded loose-object
overhang without packing — not a budget. A brief should re-measure its own threshold on the machine
class it targets, but it no longer needs to re-establish the premise.

---

## 3. The file-format consequences

Both are consequences of putting a workspace under version control; neither is a minimal-diff
optimization.

### 3.1 The `.cws` must be split — and it is mostly per-user state *(decided: §12 Q1 = split)*

`WorkspacePersistence` writes the panel/tab/floating-window arrangement into the `.cws`, so that file
changes **on every session close**, for reasons that have nothing to do with the design. Two
consequences, and the second is the serious one:

- every history contains a majority of commits whose only content is "someone moved a panel"; and
- in a **shared** repository, two designers on different monitors conflict on the `.cws` on every
  single exchange — on a file that also carries real design configuration, so the conflict cannot be
  resolved by discarding either side.

**Rev 1 understated this.** It named `dock_layout` alone. Measured on the same real workspace's `.cws`
(2,178 bytes of content):

| field | bytes | versioned? |
|---|---|---|
| `DockLayout` | 1,700 | **no** — per-user, per-monitor |
| `TreeViewState` | 221 | **no** — which tree categories this user expanded |
| `OpenDocuments` | 170 | **no** — which tabs this user left open, in what order |
| `ActiveDocumentPath` | 41 | **no** — which tab this user was looking at |
| `ColorSchemeName` | (absent here) | **no** — per-user, decided 2026-09-06; see below |
| `DefaultTechRef` | 41 | **yes** — design configuration |
| `LibraryRefs`, `KnownFiles` | 4 (empty here) | **yes** |
| `FormatVersion` | 1 | **yes** |

**Roughly 96% of a real `.cws` is per-user session state.** The split is not a tidy-up around one
field; it is a separation of two documents that were only ever one by accident.

*Re-measured after the split was built* (2026-09-06), by loading and re-saving this repo's own demo
workspace through `WorkspacePersistence` itself rather than by counting fields: **`.cws` 2,914 bytes →
65; `.cwsuser` 2,873; 97.8% per-user.** Slightly worse than the estimate above, and the shape is what
matters — what is left of the `.cws` on a workspace with no libraries and no kits is a format version
and two empty arrays.

**What moves:** `DockLayout`, `TreeViewState`, `OpenDocuments`, `ActiveDocumentPath` — and, by the
owner's decision of **2026-09-06**, `ColorSchemeName` (see below).
**What stays:** `FormatVersion`, `LibraryRefs`, `KnownFiles`, `DefaultTechRef`, `DefaultAssemblyRef`,
`PythonInterpreter`, `ReferencedWorkspaces`, `ReferencedCells`, `PdkRefs` — everything that is a
property of the *project* rather than of one person's afternoon.

**`ColorSchemeName` is per-USER** *(owner's decision, 2026-09-06 — the "still open" item at the end of
§12, now closed)*. rev 2 put it on the versioned side on the grounds that a house style is a real
thing; it was the one field in the table above whose side was **assigned rather than measured**, and it
was settled the other way: a theme is one person's preference, and a shared workspace imposing its
author's theme on everyone who opens it is the more common annoyance of the two. The cost accepted with
it is that a workspace deliberately shipping a house theme no longer activates it for a colleague (the
`.ccolor` files still travel; the *selection* does not), and that deleting the sidecar — the supported
repair — resets the theme along with the panels. **`PythonInterpreter` stays**, and is the one
judgement call in the other direction: it is per-*machine* rather than per-user, but the `.cwsuser` is
not a per-machine file either, and moving it would cost a kit-using workspace a process-launch storm on
every fresh clone for no gain.

The sidecar is excluded by the generated `.gitignore`. **This is worth doing whether or not revision
control ships** — the same state is already the wrong thing to put in an archived or shared
workspace, and §7A's read-only referenced workspaces make it wrong in a third way.

**3.1a The sidecar's extension: `.cwsuser`, and either file opens the workspace** *(owner's decision, §12 Q1)*

The sidecar is **`.cwsuser`** — literally named `.cwsuser`, beside the `.cws`, one per workspace, the
same no-stem convention the `.cws` already uses.

**The name states its role, which is the whole reason for it.** §1.4 requires that a designer never be
confused about what is kept. A `.gitignore` line reading `.cwsuser` next to a tracked `.cws` is
self-explanatory in a directory listing, in a diff, and to whoever inherits the workspace — where two
four-letter extensions differing by one character would be the kind of pair that gets transcribed
wrongly exactly once and then quietly does the wrong thing for a year.

**Double-clicking either file opens the whole workspace.** `.cws` and `.cwsuser` are two halves of one
document; a user who double-clicks either one means the same thing by it, and a file that shows a
circuitRF icon and then does nothing reads as a broken file — which is the exact complaint
`App.OpenFiles` was written to fix (its own header records that both entry points were once stubs that
opened nothing). So both extensions route to the workspace-open path, and a `.cwsuser` resolves to the
workspace via the folder that contains it, exactly as its sibling `.cws` does.

**The `.cwsuser` is optional, always. A workspace with no `.cwsuser` — missing, never written, or
deleted — opens normally.** This is the invariant that makes the split safe, and it is not a
nicety:

- **It is what a gitignored file has to be.** A versioned document that cannot be opened without an
  unversioned one is not split, it is broken. Every clone, every archive, every workspace handed to a
  colleague arrives without a `.cwsuser`, so "absent" is not an edge case — it is the **normal state
  of a shared workspace**, and by far the most common way a workspace will ever be opened by someone
  other than its author.
- **Its absence is never an error, and never reported as one.** No warning, no repair prompt, no
  "recovering your layout" message. The workspace opens on the default arrangement, exactly as a
  freshly-created one does. Anything else would train users to think something is wrong when nothing
  is.
- **Deleting it is therefore a supported repair**, and worth documenting as one: a designer whose
  panels have ended up somewhere unusable can close the workspace, delete one file, and reopen. That
  is a genuinely useful property and it comes free — but only for as long as nothing on the versioned
  side is allowed to depend on it. **Nothing may.** If a future field needs to survive that deletion,
  it belongs in the `.cws`.
- **A malformed `.cwsuser` is treated as an absent one** — the same posture `CwsFile.DockLayout`
  already takes with a structurally malformed block, and for the same reason: per-user convenience
  state must never be able to prevent a design from opening.

**Two designers on a share have one `.cwsuser` between them, and that is acceptable here for the reason
it is not acceptable in §4.4.** The sidecar lives in the workspace folder, so on the network share §4.7
and §7A both assume, the second designer to close overwrites the first one's panel arrangement. That is
the same shape as the identity mistake §4.4 corrects — per-user state held in a shared location — and
it is worth saying why the same reasoning does not force the same fix. §4.4's cost is a **durable
falsehood in a record** (work attributed to someone who did not do it, discovered late or never); this
one's cost is a **panel that moved**, in a file whose absence is already specified as normal (above) and
whose deletion is already a supported repair. The failure is visible, immediate and self-correcting.
Splitting it per user would mean a per-user path outside the workspace folder, which loses the one
property that makes the sidecar comprehensible — that it sits beside the thing it describes.

**The sidecar is excluded from an archive and kept in a copy, and the two must not be conflated.** They
are decided by one shared skip list in the implementation, and they want opposite answers: an archive
goes to somebody else, who has no use for the sender's monitor layout (§9A.2), while **File ▸ Save
Workspace As** produces the same person's own workspace on the same machine, where losing the panel
arrangement is a small annoyance with nothing bought by it. This is the reverse of `.git`, where both
consumers want the same answer for the same reason (§9A.6) — so the asymmetry has to be deliberate in
the code, or the next person tidies the two lists into one and silently changes whichever half they were
not thinking about.

One case in the other direction, because it is silent if missed: **a `.cwsuser` with no `.cws` beside
it is not a workspace.** Someone copying one file out of a folder must get a sentence saying so, not
an empty window — the `OpenFiles` failure mode that section exists to have fixed, arriving by a new
route.

Three things must change together, and each is currently held by a test:

1. **`App.OpenFiles` gains `case ".cwsuser":`** on the workspace branch, resolving through the
   containing folder.
2. **All three OS registrations gain the type** — the macOS `Info.plist` UTI declarations, the WiX
   `.wxs` extension list, and the Linux shared-mime-info glob. The three parity tests
   (`WBondStandaloneTests`, `PackagingScriptTests`) assert that every type declared to an operating
   system has a case in `OpenFiles` and vice versa; they are what makes "registered but opens nothing"
   impossible, and they must be extended rather than worked around.
3. **The generated `.gitignore` excludes it** (§3.1), and — per §10B.1 — the user documentation says
   in plain words that this file is not kept and that nothing is lost by that.

**`.crfw` is left exactly as it is, and is not part of this.** It remains a registered spelling of a
workspace that no build of circuitRF has ever written, matched by one `case` label in `OpenFiles`. It
is inert, it is harmless, and retiring it is a packaging question with its own three-registration blast
radius — not something to fold into a file-format change. Recorded here only because establishing that
nothing writes one is what made the question of reusing it answerable, and the next person to wonder
should not have to re-derive it.

### 3.2 `.clay` must never be gzipped while a workspace is a repository

`layout-view.md` §4 holds gzip in reserve, and `GzipTextFile.ReadAllTextAutoGzip`
(`LayoutPersistence.cs`) already sniffs the magic bytes so that a future gzip **writer** needs no
`FormatVersion` bump. Re-measured in rev 2 on the same real board, the identical edit sequence as §2:

| operation | plain `.clay` | gzipped `.clay` | penalty |
|---|---|---|---|
| initial commit | 4.92 MB | 4.40 MB | gzip wins, once |
| 20 shape **additions** | 2.1 KB/commit | 6.6 KB/commit | 3× |
| 20 mid-file **polygon drags** | **5.3 KB/commit** | **2.69 MB/commit** | **~508×** |
| 5 mid-file **deletions** | 1.3 KB/commit | 2.14 MB/commit | ~1,600× |
| final `.git` after 46 commits | **5.12 MB** | **74.22 MB** | **14.5×** |

**Worse on the real file than rev 1's synthetic estimate (~400×), and the trap is exactly the shape
rev 1 described.** Deflate resynchronises after an **append**, so the addition row looks almost
respectable and an append-only test reports a false pass. It is the **mid-file edit** — moving one
polygon, which is what designing actually consists of — that destroys the delta, and the **deletion**
case is worse still.

So: the gzip reserve is revoked for any workspace under version control, and that comment in
`LayoutPersistence` becomes a warning rather than an invitation. The general rule it exemplifies is
worth stating on its own, because it also governs `.npy` (§8):

> **Compressed or binary content does not delta.** A format that saves disk once costs the repository
> a full copy on every save. In a versioned workspace, plain text is the compressed format.

---

## 4. Arm's length: the process boundary

circuitRF invokes the installed `git` executable as a subprocess. It does not link a git library.

**4.1 Why.** The licensing consideration (Core is MIT) is real but secondary. The architectural
reasons matter more:

- **The escape hatch survives.** The repository a designer's workspace lives in is an ordinary git
  repository, readable and repairable by every existing tool, including the one their IT department
  already supports.
- **The failure mode is a process exit code**, not an in-process fault in a native library — which
  matters given the cross-platform native-dependency policy in the root `CLAUDE.md`.
- **Absence is detectable and cheap.** No git on `PATH` (and none configured in Settings) means the
  feature is simply not there.

**4.2 What arm's length costs, and who pays it.** Git's diagnostics are written for people who already
understand git. Shelling out means inheriting them. **The architecture must therefore include a
translation layer**: a bounded set of recognised failures (no identity configured, nothing to commit,
a lock file left by a crashed process, a non-fast-forward push, an embedded repository refused) each
mapped to a sentence in circuitRF's own vocabulary, with the raw git output available but not
foregrounded. Anything unrecognised is reported verbatim and honestly, never swallowed — the same
posture the run services already take with an engine error.

**4.3 Absence is the default, and the UI must reflect it.** Most of the target audience — RF designers
on Windows — have no git installed. The revision-control affordances are therefore **hidden entirely**
when git is unavailable, rather than shown disabled. A designer who does not want this feature should
never learn it exists.

**4.3b Settings ▸ Revision Control is the one exception** *(owner's decision, 2026-09-07; §12 Q31)*.
The tab is shown on **every** machine, whatever this section says about the affordances. Hiding it kept
absence silent, and it also hid — from the designer who *would* have wanted a history — the fact that
circuitRF can keep one and is not keeping one here. That is §1.4's false belief reached from the other
side, and the remedy it concealed is a program the designer could install in five minutes.

The arrangement is honest in both directions, and the split is the whole of it:

- **The rows are visible**, so what circuitRF would keep, and on what terms, is legible before anyone
  installs anything.
- **They are disabled**, so nobody can come away believing something is being kept. One sentence in
  place of the controls says that there is no usable git, that nothing is being kept in any workspace,
  and what to do about it.
- **The git path and its Detect stay live**, because that row is the remedy. A disabled Detect would
  leave a machine unable to answer its own question — and answering it is precisely why somebody
  without git opens this tab.

**This is not §12 Q4's hold**, which is also visible-and-refusing and means something else: held is a
repository circuitRF may not write to, where a designer may believe they are protected. Here nothing has
been promised at all, and the tab says so where the controls are.

**Two consequences.** The git-path row's **second host on Security & Permissions is withdrawn** — it
existed only so that hiding the tab did not also hide that field, and with the tab always present a
second copy is a row nobody could ever reach. And §10A's opening sentence is superseded by this one.
Everywhere else in the application §4.3's silence is unchanged: no Versions panel, no restore-point
list, no menu item, no badge.

**4.3a Detection must not itself be the surprise** *(new in rev 5)*. On macOS, `/usr/bin/git` is
present on every machine whether or not git is installed: it is a shim that, when the Command Line
Tools are absent, **opens Apple's "install the developer tools?" dialog** instead of running. A
discovery that establishes presence by invoking `git --version` would therefore show an unbidden system
dialog to precisely the users §4.3 promises will never learn the feature exists — on first launch, and
on every launch after it. Discovery on macOS checks that the developer tools are installed
(`xcode-select -p` succeeds, or an equivalent that never invokes the shim) **before** running anything
named `git`, and treats the shim without tools as absent. The other two platforms have no such shim.
§10A's Detect action is the one place the shim may be reported by name, because there the user asked.

**4.4 Identity is a prerequisite, not an error to translate** *(new in rev 2)*. `git commit` refuses
outright when `user.name` and `user.email` are unset, and on a fresh Windows machine they are unset.
The first checkpoint circuitRF ever takes would therefore fail — silently, since a checkpoint is not
user-initiated and has no dialog to fail into. Two rules follow:

- **Identity is captured before the feature is armed**, in the Settings tab (§10A), and it is a
  **per-user circuitRF preference — not a git config entry anywhere.** It is supplied to each
  invocation (§4.5) and persisted only in circuitRF's own per-user state, which is the scope the thing
  actually has: one person, every workspace they touch on that machine.

  **The two places rev 2 considered are both wrong, and for different reasons.** The user's *global*
  git config is wrong because circuitRF has no business changing a setting that affects every other
  repository on the machine — that reasoning was right and stands. But the *repository's* config, which
  rev 2 chose instead, makes a person's identity a property of a **directory**: one entry, serving
  whoever opens that folder. §4.7 already establishes that these workspaces live on network shares and
  §7A assumes several designers reach one; the second of them to commit would do so under the first
  one's name, silently, until somebody read a history and disbelieved it. **This is §3.1's mistake in
  another file** — per-user state written into a shared document — and the fix is the same one:
  separate it out.

  Two further consequences make the repository's config the worse of the two:

  - **An archive with history included copies `.git/config`** (§9A). The recipient would inherit the
    sender's name and email and commit under them. The sender's identity is in the commits regardless,
    so nothing new is *disclosed* — but the recipient's own future work would be attributed to someone
    else, which is a durable falsehood in a record §1.4 exists to keep honest.
  - **It is a write, and this design's posture is to make fewer of them.** §5.7's "off means circuitRF
    writes nothing" and §7A.1's "one repository" are both weakened by a design that writes a per-user
    value into a shared artifact for no functional gain.

  **The preference is stored where both processes can read it, and that is a rev 5 correction.** rev 3
  and rev 4 let the headless case fall through to git's own resolution, on the grounds that `src/Cli`
  cannot read a GUI preference across the firewall. That is true of `AppPreferences`, which lives in
  `src/Ui` — and it defeats the governing motive: §1.2's agent reaches circuitRF through `serve`, out
  of process, and on the fresh Windows machine this very section describes git's own resolution names
  nobody. The AI-batch checkpoint would be refused for exactly the population it exists to protect, and
  the only remedy would be the global config write circuitRF refuses to make. **So the identity is
  written by the Settings tab into circuitRF's per-user preference file and read back by a small
  `src/Design` reader that the GUI and `src/Cli` both use.** Reading a JSON file in the per-user
  directory crosses no firewall; only the type that owns the dialog does. **Where that file names
  nobody, git's ordinary resolution applies** — the user's global config, if they have one — which is
  still the right fallback for a headless run on a machine where circuitRF was never configured. If
  neither can name a committer, the feature does not arm, which is the whole of §4.4's first sentence,
  and `serve`'s refusal names the Settings tab rather than a git command.

  **The Settings tab should offer the user's existing global git identity as the pre-filled default**
  (§10A). *Reading* someone's global config is unobjectionable; only writing it was ever the problem.
  For the population that already uses git, setup then costs one glance.
- **A checkpoint that could not be taken is reported.** §1.4 forbids a designer believing they are
  protected when they are not; a checkpoint failing quietly is the purest form of that failure. The
  Messages entry says what was not saved and why, and the persistent indicator of §12 Q4 shows the
  same state without requiring anyone to be reading the log.

### 4.5 What circuitRF configures, and what would otherwise break it silently *(new in rev 3)*

Arm's length means inheriting the user's machine as well as git's diagnostics. Every invocation runs
under the user's global and system configuration, and several entirely ordinary settings in it turn an
automatic checkpoint into a hang or a refusal nobody sees. **So the repository circuitRF creates
carries its own configuration, and every invocation carries its own environment.** Both belong in the
architecture because each entry exists to prevent one specific silent failure, and because a
configuration written down nowhere is a configuration the next person removes as clutter.

**The line between the two is what a setting is a property OF.** Repository configuration carries
properties of the **repository** — how it is packed, how its files reach disk — and those are correctly
shared by everyone who opens the folder. Anything that is a property of the **person**, or of
circuitRF's own behaviour rather than the repository's, is supplied **per invocation** and persisted in
no git config file. Identity is the clearest case (§4.4) and the one rev 2 got wrong.

**Repository configuration**, written once at creation into the repository's own config and never the
user's global one:

| setting | value | the failure it prevents |
|---|---|---|
| `gc.auto` | `0` | circuitRF's byte-based packing schedule and git's count-based one fighting (§2.4) |
| `gc.pruneExpire` | `never` | **§5.6 rule 4.** Otherwise a routine pack destroys thinned checkpoints permanently after two weeks |
| `gc.reflogExpire`, `gc.reflogExpireUnreachable` | `never` | the reflog is the escape hatch for the **designer's own branch** — a mistaken reset or amend from a shell (§5.2); at its 30-day default it is gone before §1.3 says the designer looks. **It is not the escape hatch after a sweep** *(corrected in rev 5, §5.6 rule 4)*: a deleted reference takes its reflog with it |
| `core.longpaths` | `true` (Windows) | nested cell folders plus git's own object paths pass 260 characters; the failure is a refusal on one machine class only |
| `core.autocrlf` | `false` | see *line endings*, below |
| a **management marker** naming circuitRF and recording §12 Q4's answer | written once | **§12 Q4, §9A.5 and §5.3b all turn on "did circuitRF create this repository", and a `.git` directory alone cannot answer it.** See below |

**The management marker is the entry that is not a git setting**, and it is in the repository's config
because that is exactly where §4.5's dividing line puts it: whether circuitRF manages this repository is
a property of the **repository**, correctly shared by everyone who opens the folder. Two consequences
fall out of that placement and both are the ones wanted:

- **An archive with history carries it** (§9A copies the directory), so an extracted archive is
  recognised as circuitRF's without ceremony — which is what §9A.5 asserts and previously had no
  mechanism for.
- **A clone does not** (git does not clone a repository's config), which is also right: a clone is a new
  workspace on a new machine and should reach §5.7a's arming path on its own terms. A marker in a
  *reference* would have been pushed, making one designer's management decision travel into everybody
  else's clone.

It records **which answer §12 Q4 got**, not merely that circuitRF was here, so the question is asked once
and the Settings tab can show what was decided.

**Every commit circuitRF makes bypasses hooks.** §12 Q4 offers *adoption* of a repository the user
created, which may carry a `pre-commit` hook written for their own workflow. Running someone's hook on
a checkpoint they did not initiate is §7A.1's ambush by another route: the checkpoint is circuitRF's,
not theirs, and it must neither be blocked by their tooling nor set it off. **`--no-verify` does not achieve that on its own**
*(rev 5)*: it skips `pre-commit` and `commit-msg` and leaves `post-commit` and the rest to fire. The
bypass is an empty `core.hooksPath`, supplied per invocation like everything else that is a statement
about circuitRF's commits rather than about the repository.

**Invocation environment.** Every subprocess starts with:

- **`GIT_TERMINAL_PROMPT=0`.** Without it, an operation needing credentials — §9's fetch or push
  against a private repository — **blocks forever** on a prompt written to a terminal that does not
  exist. **A hang is the worst failure mode available**, because it is the only one with no message,
  no exit code and no end. §9.1 is the rest of that answer.
- **A locale pinned to `C`, and machine-readable output wherever a result is parsed.** §4.2's table
  must key on exit codes and stable formats, never on English prose a localised git will not produce.
- **`--no-pager`, an explicit working directory, and a timeout.** A subprocess circuitRF cannot bound
  is a subprocess that can wedge a workspace close. The bound is wall-clock for a local operation and
  **inactivity** for a network one (§9.1): a fixed limit kills the legitimate slow clone of a large
  library, which is not a hang.
- **`safe.directory` naming the open workspace's root** (§4.7), so that a workspace on a share or an
  extracted archive works without a config write.
- **The commit identity** (§4.4) — author *and* committer, since git resolves the two separately and
  supplying only one leaves the other to config or to a guess at `user@hostname`. Nothing is written to
  a config file; unset in circuitRF, git's own resolution applies.
- **Signing off** for circuitRF's own commits. A designer who signs globally would otherwise have every
  automatic checkpoint block on a passphrase prompt with no window to appear in. This is supplied per
  invocation rather than written into the repository, because it is a statement about **circuitRF's**
  commits, not about the repository — the same designer's own commits from a shell in that folder
  should still sign, exactly as they configured.

**Line endings are the entry that looks like nothing and is not.** §2's whole measurement rests on
`.clay` being byte-stable, line-oriented text with `long` coordinates. Git's end-of-line conversion
would make the bytes on disk platform-dependent — a whole-file diff on every cross-platform exchange,
and a `.clay` that is not the file `LayoutPersistence` wrote. **The generated `.gitattributes`
therefore pins end-of-line treatment for every circuitRF document type**, alongside §6.1's unmergeable
marking. The two live in one file for one reason: a design document's bytes are the design.

### 4.6 Two processes, one repository *(new in rev 3)*

**§1.2's agent is out of process** (`src/Cli`), the GUI is another, §2.4's packing runs when a workspace
closes, and §5.3b's agent may hold a shell of its own. They drive one repository, and a concurrent pair
of writers produces a lock failure that §4.2 translates — but **a failure this predictable should be
prevented where it can be, and narrated honestly where it cannot.**

**rev 3 assigned the serialisation to the workspace's advisory lock, and that was a category error**
*(corrected in rev 5)*. `WorkspaceLock` (`src/Design/Workspace/WorkspaceLock.cs`) is a notice, not a
lock: its own header says it holds no file handle, that both "open anyway" and "open read-only" are
always available, and that a lock this product treated as authoritative *"would become a stale file that
locks out a team."* It also fires in exactly the situation §5.3c designs for — the GUI and `serve`
holding one workspace at once — so it cannot be what keeps them apart. Three rules replace it:

- **Checkpoints never touch the shared index.** A checkpoint is built through a temporary index file
  (§5.2b), so the one resource git's writers actually contend on is left to the designer's own shell,
  and two circuitRF processes checkpointing at once contend only on reference updates, which git makes
  atomic.
- **Git's own lock files serialise what remains.** They are atomic-create files, which is the correct
  primitive and the one a second implementation would have had to reinvent. A collision is retried for
  a short bound before it is translated, because a collision between two automatic operations is a
  delay and not an error.
- **A read never waits on a writer.** Listing restore points, resolving a pin, or computing §9A.3's
  archive figures must not block behind a pack. Git's read paths do not take the index lock unless
  asked to refresh it, and circuitRF's must not ask.

**Packing yields, and the advisory notice is the right thing for it to yield to.** §2.4 already requires
packing to be interruptible and safe to abandon; it must also not start while another process has the
workspace open — which is precisely the question the advisory notice answers — and must abandon rather
than queue when one arrives. That is the notice used as a notice.

### 4.7 The version floor, and the failure that only appears on a share *(new in rev 3)*

A version floor exists whether or not it is written down; leaving it unwritten means discovering it
from a bug report. It is stated once, checked at discovery — §10A's Detect reports the version it found
— and **a git below the floor is treated exactly as an absent one** (§4.3) rather than as a broken one.
A designer with an old git does not want to be told about a feature they cannot have.

The floor is set by the oldest behaviour §4.5 and §12 Q4 depend on, not by novelty. **`safe.directory`
is the entry that matters most.** Git 2.35.2 and later refuse to operate on a repository owned by
another user — *"detected dubious ownership"* — and that is the ordinary state of **a workspace on a
network share** and of **an archive extracted by someone else**. RF workspaces live on shares, and
§7A's librarian scenario assumes one. This is a recognised failure in §4.2's table, with its own
sentence and its own remedy; it must never reach a designer in git's wording.

**And the remedy is supplied per invocation, not written anywhere** *(new in rev 5)*. rev 4 left the
remedy implicit, and the only one a designer could be handed is a global git config write — the thing
§4.4 rules out, in git vocabulary, shown unbidden. Git accepts `safe.directory` on the command line, so
every invocation names the open workspace's root as safe (§4.5). The trade is stated rather than hidden:
the ownership check exists because a foreign-owned repository's configuration can name programs to run,
and naming the root trusts a folder the user chose to open and whose contents circuitRF already loads.
That is the trust the open already expressed. It is narrowed to the one root, never `*`, and the
translated failure stays in §4.2's table for the git that is too old to accept it — which is one of the
behaviours the floor is set from.

---

## 5. Two histories, one repository

A single history cannot serve both motives in §1. The dense, automatic, machine-written safety net and
the sparse, deliberate, human-written narrative have different authors, different granularity, and
different audiences. Conflating them produces a log no human will read — which then makes the safety
net useless too, because nobody looks at it.

**5.1 The safety net (machine-written).** Automatic checkpoints, committed to a **reference outside
the branch the designer sees**, so they never appear in the history the designer browses. Presented in
the UI as restore points — *"14:32 · before 'Widen the output match'"* — with no git vocabulary
anywhere. The pattern is not novel: some development environments keep exactly such a local history,
independent of whatever version control the project itself uses, for exactly this reason. Git's own
reflog is the same idea.

**5.2 The narrative (human-written).** Ordinary commits on the ordinary branch, created deliberately,
with a title the designer wrote. This is what a history browser shows, what is shared, and what is
pushed.

**Two histories, and — from rev 6 — one panel to read them in.** §5.10. The distinction above is a
distinction between *entries*, and rev 2 spent it as an argument for two windows without saying so. It
survives intact: what each kind promises, where it is stored, and which journeys it survives are
unchanged, and §5.10 rule 6 forbids the merge from ever becoming a storage change.

**Status: BUILT 2026-09-06** · `brief-revision-control-7-commit-and-history.md` · `WorkspaceCommit`,
`CommitMessage`, `RestoreProvenance`, `HistoryBrowser` and `DocumentClash` in `src/Design/Revision/`,
the Versions panel in `src/Ui`, `history commit`/`history versions` in `src/Cli`, gated by
`tests/Ui.Tests/Revision/CommitAndHistoryTests.cs`. **Three things the build settled that this section
did not say.** §5.5's restored-from line **cannot be derived** from content — a commit that went back to
Tuesday is indistinguishable from one that undid three days by hand — so the restore writes it down
(`RestoreProvenance`) and the commit reports and clears it. **The commit must write the repository's
SHARED index and a checkpoint still must not**: the index is defined relative to `HEAD`, so the moment
`HEAD` names a commit and the index is empty, git's own porcelain reads the whole design as deleted and
`git checkout` refuses to run — which breaks §4.1's promise about the escape hatch, and is invisible from
inside circuitRF. And **"does this workspace have a history" became two questions**: §5.7's off/on
transition was gated on the restore-point count alone, so a workspace holding only versions recorded no
transition and its off period rendered as a quiet interval — §5.7's own failure, reached from the other
side.

### 5.2a Where checkpoints live, and which journeys they survive *(new in rev 3)*

§5.1 says checkpoints go on "a reference outside the branch the designer sees", which rev 2 left as a
phrase. Two consequences follow from the exact shape, and both were missed.

**Each checkpoint is independently referenced — not chained behind one moving reference.** §5.6
*thins*: it drops individual checkpoints out of the middle of a series while keeping the newest *N*.
A single reference walking a chain of commits cannot express that. From a chain you can only truncate
the oldest end, and truncating leaves everything before the cut still reachable, so it thins nothing at
all. **A per-checkpoint reference in circuitRF's own namespace is what makes §5.6 implementable**, and
dropping one is then a single reference delete — which is exactly §5.6 rule 4's "thins, never prunes":
the pointer goes, the objects stay.

**Checkpoints are local, and do not travel over a network.** A clone does not copy them — git's default
fetch takes branches and tags, and nothing under a private namespace — and a push does not carry them.
**That is correct behaviour, not an accident to repair later.** The safety net is a property of one
machine and one designer's sessions: it is ordered by a monotonic sequence that means nothing anywhere
else (§5.6 rule 2) and thinned by that machine's retention preference. §5.2's narrative is what is
shared; §5.1's safety net is not.

**But it makes the three ways a workspace leaves a machine disagree, and the disagreement must be
stated rather than discovered:**

| how the workspace travels | checkpoints | human-written commits |
|---|---|---|
| **archive with history included** (§9A) | **yes** — the archive copies the repository directory | yes |
| **clone** (§9) | **no** | yes |
| **Save Workspace As** (§9A.6) | **no** — and neither does anything else in the history | **no** |

§9A's promise — *"every earlier version of every file it ever tracked"* — holds for the archive because
an archive copies a directory. It does not hold for a clone, and §10B.1 says so in a row of its own.
Someone who clones gets the narrative and starts a safety net of their own, which is what they want;
someone handed an archive gets the sender's restore points too, which is a stronger handover and is
exactly why §9A's checkbox needs §9A.3's warning.

### 5.2b What a checkpoint is, mechanically *(new in rev 5, §12 Q18)*

§5.2a fixed the reference shape and left the commit's shape unstated. Four properties, each of which
decides something elsewhere in this document:

- **A checkpoint commit has no parent.** This is not a preference. §5.2a's whole argument — that
  deleting one reference frees that checkpoint's objects — holds only if the next checkpoint does not
  name this one as its parent. Chained, a deletion frees nothing, §5.6 thins nothing, and no gate that
  asserts *survival* would notice. The sequence trailer (§5.6 rule 2) carries the order a parent chain
  would have carried; `git log <ref>` shows one commit, which is the truth.
- **It is built through a temporary index**, never the repository's shared one: add every file into a
  private index, write the tree, write the commit, update the reference. `HEAD` and the designer's
  branch never move, the designer's own staged state from a shell is untouched, and §4.6's contention
  on the index disappears. This is also what makes §6.3's linear restore possible: a restore is a
  working-tree write, and nothing ever checks anything out.
- **It captures every file the workspace holds that `.gitignore` does not exclude — including files no
  checkpoint has seen before.** The phrase *"`commit -a`-shaped"* elsewhere in this document names the
  intent, not the command: a literal `commit -a` skips untracked files, and a new cell folder an agent
  just created is exactly the file §1.2 exists to capture. Two exclusions apply beyond `.gitignore`: a
  nested repository's subtree (§7A.5, §12 Q4), and a file left out under §8.2b, which the checkpoint's
  own metadata records.
- **Its metadata is in the commit message, as trailers**: the sequence, the origin (§5.5), the intent,
  whether it is **kept** (§5.6 rule 6), and any file left out. The sequence is derived from the
  references that exist, never from a counter file, so it survives a crash, a clone and a hand-deleted
  reference. A boundary whose tree equals the newest checkpoint's records nothing (§5.3).

### 5.3 When is a checkpoint taken? *(decided: §12 Q2)*

Not on every save. The measurement in §2 says a commit per save is *affordable* (single-digit KB). It
is still wrong, for two reasons that have nothing to do with cost:

- **A save is not a boundary.** One logical design action can write a `.clay`, the workspace file, and
  a `.csch`. A commit per file save records fragments of an edit, some of which are not internally
  consistent.
- **A history nobody can read is not a history.** Hundreds of undifferentiated entries per session
  defeat the one thing the safety net is for: finding the moment before it went wrong.

Checkpoints attach to **boundaries** — moments already meaningful in the application. **Three ship in
Stage 2:**

| boundary | why | can the user turn it off? |
|---|---|---|
| **before an AI-initiated batch** | §1.2 — this is the motive | **no** (§10A) |
| **an explicit save-point the user asks for** | the user's own judgement about what matters is better than any heuristic | n/a — it is the request |
| **on workspace close** | the one boundary that reliably exists in every session, including the ones where the designer never thought about history at all | yes |

**The explicit save-point takes a one-line label, optionally**, because §5.3a rule 2's reasoning
applies to it: the intent is the whole value of the entry, and a save-point with no label is shown as
one — *save-point*, with its time — never as a bare time. **And a boundary at which nothing changed
records nothing** *(new in rev 5)*: a close after a session that only looked, or a save-point pressed
twice, would otherwise add an entry indistinguishable from the one before it, which is the unreadable
log this section opens by rejecting. The test is the tree, not the clock: a checkpoint is taken only if
the workspace's tree differs from the newest checkpoint's (§5.2b).

**Deliberately not shipping, and the reasons are worth keeping** so they are not revisited casually:

- **On simulation run.** Attractive because a run is a natural "this is the state I measured" marker —
  but runs are frequent, often unchanged from the last one, and a designer sweeping a parameter would
  generate dozens of near-identical checkpoints. It also silently couples history to the analysis
  engine, which §11's staging is specifically arranged to avoid.
- **Idle timeout.** A checkpoint the user cannot predict, at a moment that is meaningful to nobody,
  labelled with a time rather than an intent. It produces exactly the unreadable log §5.3 opens by
  rejecting. Worse, "idle" during a long simulation is not idle at all.

The principle is unchanged and is what the exclusions protect: **a checkpoint marks a *boundary*,
never a *write*, and never merely the passage of time.**

### 5.3a What an "AI-initiated batch" actually is *(new in rev 3)*

§1.2 is the governing motive of this document, and rev 2 never said what triggers it. *"Before every
AI-initiated batch"* names a boundary without saying who declares it — so the most important checkpoint
in the design was the only one with no mechanism. **Per §12 Q5 this ships before AI design capability
exists, so the boundary cannot be discovered by building the capability.** It is specified now, against
the surface that exists today: `src/Cli/Serve/`, the stdio protocol adapter, out of process and
headless, which is the one place an agent reaches circuitRF.

Five rules:

1. **The agent declares the batch; circuitRF does not infer it.** A batch is opened and closed
   explicitly, and the checkpoint is taken when it opens — before the first modification. Inferring a
   batch from a run of write operations requires a timeout, and a timeout is §5.3's rejected idle
   trigger wearing a different hat.
2. **The declaration carries the intent, in the agent's own words, because that is the label.**
   *"before: widen the output match"* is the whole value of the entry. A batch that supplies no intent
   is labelled as what it is — an unnamed batch — and never with a bare time.
3. **Batches do not nest.** A second open inside an open batch is the same batch. Two checkpoints
   around one logical action is the unreadable log §5.3 opens by rejecting, and the designer's question
   is *"what did it look like before the agent touched it"*, which has exactly one answer.
4. **An unclosed batch is not a failure and needs no repair.** The checkpoint was taken before anything
   was modified, which is the whole of what §1.2 asks; if the agent dies mid-batch the restore point is
   precisely where it should be, and the next boundary closes it. **The safety net must have no failure
   mode that depends on an agent behaving well** — that is the population it exists to protect against.
5. **Reverting a batch is restoring to its checkpoint** (§5.8), and it is offered to the *designer*,
   not to the agent. §1.2 asks for *revertible in one action*; it does not ask for a tool call with
   which an agent can erase what it did.

**A batch declared against a workspace that cannot be checkpointed is refused before anything is
modified, never after.** §5.7 covers revision control being off, §12 Q4 the held case, §5.9 a scratch
workspace. All three resolve the same way and at the same moment — before the first write — because a
floor announced after the fall is not a floor.

### 5.3b The agent-facing contract: what `serve` advertises, and the rules that come with it *(new in rev 3)*

§5.3a settles the mechanism. This settles what the agent is *told*, and it matters because **a capable
agent has a shell and a `git` of its own.** It can read this repository, and it can write to it, whether
or not anyone intended that. An agent that does not know a workspace is under circuitRF's revision
control will either duplicate the safety net badly or damage it — and the failure will look like
circuitRF's.

**The state is advertised, not inferred.** The `serve` surface reports, for the open workspace: whether
revision control is **on, off, held or unavailable** (§4.3, §5.7, §12 Q4); whether a batch is currently
open and what its intent was; and where the repository root is. An agent that has to deduce this from
the filesystem will deduce it wrongly — a `.git` directory alone does not distinguish a repository
circuitRF created from one the user did (§12 Q4), and those are opposite situations.

**The rules travel with the state.** They are published by the surface itself, next to the state, so an
agent learns them from the server rather than from having read this document. They are short on purpose:

> **Working with revision control in a circuitRF workspace**
>
> 1. **Open a batch before your first modification, and state your intent in one line.** The checkpoint
>    is taken then. A batch opened afterwards protects nothing.
> 2. **Read git freely; write only through the batch.** `log`, `show`, `diff`, `status` and
>    `rev-parse` are yours to use and are the fastest way to answer *what changed*. Every operation
>    that writes to the repository is circuitRF's.
> 3. **Do not commit.** Not to the designer's branch — that branch is their own record and a commit
>    there claims they made a decision they did not make — and not to circuitRF's checkpoint namespace,
>    which carries metadata and an ordering sequence you cannot supply.
> 4. **Do not rewrite, and do not reclaim.** No amend, rebase, reset, filter or forced push; no `gc`,
>    `prune` or `reflog expire`. §4.5's configuration exists so that a mistaken deletion stays
>    recoverable until the designer chooses otherwise (§5.6a), and a single housekeeping command undoes
>    that for everyone.
> 5. **Do not change what the working tree contains.** No checkout, no branch, no switch, no stash, no
>    `restore`. The files are open documents in a running application; changing them underneath it
>    produces a design nobody has (§5.8).
> 6. **Do not touch configuration or `.git` itself.** The repository's config is circuitRF's (§4.5);
>    the global config is the user's; deleting `.git` is the user's own act and never yours (§5.7).
> 7. **Do not reach the network.** Fetch, push and clone are explicit user actions (§9).
> 8. **Do not force a file past `.gitignore`.** It records what the workspace decided not to keep, and
>    the reason is reproducibility rather than size (§8.1).
> 9. **If the batch cannot be opened — off, held, no git, or an unsaved workspace — stop and say so.**
>    Do not substitute a commit of your own, a copy of the folder, or any other improvised backup. The
>    user may have chosen this state deliberately (§5.7), and an agent that works around a refusal
>    turns a decision into a surprise.
> 10. **Never report a checkpoint you did not take.** If the tool did not confirm one, say that the
>     work is unprotected. This whole feature exists because §1.4's unacceptable failure is a designer
>     who believes they can get back and cannot; an agent is perfectly capable of causing that failure
>     with a reassuring sentence.

**Rules 3 and 9 are the load-bearing pair.** Everything else prevents damage; those two prevent the
*helpful* failure — an agent that, finding no checkpoint mechanism available, makes its own arrangements
and reports success. That produces commits in a history the designer did not author (§7A.1), or a
private backup nothing knows about, and in both cases the designer is told they are protected by a
mechanism that is not the one they can restore from.

**This is also why §4.6 exists.** An agent holding its own `git` is a third writer on one repository.
Rules 2 and 4 keep it out of the index; §4.6 is what handles the case where they are not enough.

### 5.3c The window the batch is editing underneath *(new in rev 4)*

§1.2's agent is out of process (`src/Cli/Serve/`), and the workspace it edits may be open in a window at
the time. §5.8 works this out in full for a **restore** — reload the documents, discard their undo
stacks, because *"an undo after a restore would produce a document that existed at no moment ever"* —
and §5.3b rule 5 forbids the agent from producing the same state through `checkout` or `stash`. **The
agent's own edits, through the batch, are that identical situation arriving by the front door**, and
rev 3 did not say what happens.

It matters because the answer today is nothing. The project tree refreshes manually and on focus and
there is no filesystem watcher (`workspace-and-project-tree.md` §9 defers one deliberately) — and a
tree rescan is not a document reload in any case. So a batch that edits a `.csch` the designer has open
leaves the window showing the old content, over an undo stack describing edits to a file that no longer
contains them, and the window's next save discards everything the agent did. **That is §7A.3's
two-editors-one-file failure, which the architecture already calls worse than the divergence §7A.2
exists to prevent, reached here by the mechanism §1.2 is the motive for.** A checkpoint protects the
history and does nothing whatever about it.

Three rules, and the first is what makes the other two small:

1. **A batch is refused while the window holds unsaved changes to that workspace**, before anything is
   modified — the window is asked over the channel rule 3 names. §5.8 resolves this for a restore by
   *asking* the designer — but a batch is headless and out of process,
   and there is nobody to answer a prompt. A refusal is the only honest answer, it is the fourth member
   of §5.3a's family (off, held, scratch, dirty), it arrives at the same moment as the other three, and
   §5.3b rule 9 already tells the agent exactly what to do with it. The designer saves and asks again.
2. **On batch close, the documents the batch modified are reloaded and their undo stacks discarded** —
   §5.8's rule, for §5.8's reason, through §5.8's code path. Rule 1 is what keeps this cheap: with no
   unsaved work anywhere, a reload cannot lose anything.
3. **The window learns the batch closed; it does not discover it on focus.** The on-focus rescan is the
   wrong mechanism twice over — it rebuilds the tree rather than the documents, and a designer watching
   the agent work never leaves the window, so it may not fire at all. **The channel is the one
   the application already has for a second process to reach a running window** *(named in rev 5,
   §12 Q28)*: the single-instance forwarding in `src/Ui/Program.cs` — a named pipe on Windows and a
   Unix-domain socket on Linux, which is brought up on macOS as well for this purpose, since there the
   OS carries the double-click and nothing else had needed it. Two messages travel on it, one in each
   direction: `serve` asks the window whether it holds unsaved changes to this workspace (rule 1), and
   tells it which documents a batch modified when the batch closes (this rule). No window listening
   means no window to protect, and the batch proceeds with nothing to reload. **Not the advisory lock
   file**: a dirty flag rewritten there is a write to a shared folder on every edit, and reading it back
   is a timer. Not a watcher, for the reason above.

**What this deliberately does not do is make the window an editor the agent shares.** One file, one
editor (§7A.3) still holds: during a batch the workspace's documents belong to the batch, and after it
they belong to the window again. Any design in which both are live at once is the failure above with
more machinery.

### 5.3d Every history operation has a headless spelling *(new in rev 5, §12 Q29)*

§5.3a puts the batch on `serve`, and rev 4 left it there: the only ways to take a checkpoint, list
restore points, restore one or commit were a window and an agent's tool call. That breaks a rule this
repository already holds for every other capability — *an operation that lives only in a view model is
not a capability* — and `serve`'s tools are the CLI's verbs by construction, not a parallel surface. A
build machine reproducing a signed-off result, a script that wants a save-point before it rewrites a
technology, and a person with no window all need the same thing.

**One verb, `history`, with nouns**: `checkpoint` (an explicit save-point, with `--intent`, which is
also what a batch's open does), `list`, `restore`, and, from Stage 3, `commit`. Each calls the function
the GUI's own command calls, in `src/Design`; the verb is argument parsing, refusals and reporting,
exactly as `new` and `import` are. The batch's open and close remain `serve`'s, because they carry
session state a one-shot process cannot hold. Adding the verb to the repo-root `CLAUDE.md`'s list and
to `docs/design/cli.md` is the owner's edit, not a brief's.

---

### 5.4 The relationship to undo

Deliberately none. The undo stack is per-view, keystroke-grained, and session-lived; a checkpoint is
workspace-wide, action-grained, and durable. Fusing them would force one granularity on both and gain
nothing. **The checkpoint is the bridge between them**: undo covers the last few minutes within a
view, the checkpoint covers everything else.

### 5.5 What a commit says, and what the Messages panel reports *(new in rev 2)*

**A commit message must record how the commit came about**, because six weeks later "which of these
did I mean?" is the only question anyone asks of a history. Three origins, three distinguishable
messages:

| origin | where it lives | message shape |
|---|---|---|
| the user pressed **Commit** and typed a title *(Stage 3)* | the designer's branch | the user's title, plus a line recording that this was an explicit commit by the user — and, after a restore, which restore point it was restored from (§6.3) |
| the user asked for a **save-point** *(Stage 2)* | a checkpoint reference | the user's label, or *save-point* when they gave none, plus a line recording that the user asked for it |
| the workspace was **closed** | a checkpoint reference | a message that states the workspace was closed — the designer did not choose this moment, and the history must not imply they did |
| **before an AI batch** | a checkpoint reference | the batch's own intent — *"before: widen the output match"* — never on the designer's branch |

**rev 4 had three rows for four events, and put the wording of every checkpoint row in the Stage 3
brief, which creates none of them** *(corrected in rev 5)*. The brief that creates a commit owns its
message.

The distinction between the second and third rows is the point, and it is drawn in the restore-point
list, where those two appear together. A designer scanning it has to be able to tell *"I decided this
was worth keeping"* from *"circuitRF kept this because I shut the lid"* without opening either. The
automatic one is not lesser — it is frequently the one that saves them — but it means something
different, and a list that renders them identically is lying by omission. The same distinction reaches
the escape hatch through the message, which is why it is in the message and not only in the list.

**The Messages panel reports an explicit commit, and names it.** On a user-initiated commit, one entry:
what was committed, and **the commit's identity**. That identifier is what makes the escape hatch of
§4.1 usable — it is what the designer, or someone helping them, types into `git show` when
circuitRF's own UI cannot answer the question. See §0's qualification for why naming it here does not
contradict "no git vocabulary": the user asked for this commit, and it is theirs to refer to.

Automatic checkpoints do **not** post a Messages entry each time. They would drown the panel, and §5.3
already exists to keep the safety net readable. They appear in the restore-point list, which is where
someone looking for one will look. **The exception is failure** (§4.4): a checkpoint that could not be
taken is always reported.

### 5.6 Retention, and what a clock change must not do *(new in rev 2, answers §12 Q3)*

**Status: BUILT 2026-09-06** · `brief-revision-control-6-retention-hold-and-off.md` ·
`RetentionPolicy`/`RetentionSweep`/`SessionHousekeeping` in `src/Design/Revision/`, gated by
`tests/Ui.Tests/Revision/RetentionHoldAndOffTests.cs`. **Two things the build settled that this section
left open:** the bound is a quarter of the unkept entries and it cannot distinguish a clock jump from a
long absence — that refusal is reported rather than tuned away; and rule 1's floor is computed over the
UNKEPT entries only, since kept ones "count toward nothing". `src/Design/RESOLVED.md` has both.

§1.3 argues the recovery window is weeks, because the failure being guarded against may not be noticed
for weeks. That implies a retention policy — thin out old checkpoints, keep human-written commits
forever — rather than unbounded growth. Retention is a user preference (§10A).

**The hazard the owner identified is real and is not hypothetical: a wall clock is user-writable
state.** A machine whose clock jumps a century forward makes every checkpoint "expired" on the next
sweep. A clock set backwards makes nothing ever expire, and stamps new checkpoints as older than the
ones they follow. Timezone changes, a dead CMOS battery, a laptop resuming from suspend against a
re-synced NTP server, and a dual-boot machine disagreeing about whether the hardware clock is UTC all
produce the same class of fault, and all of them are ordinary.

**A retention policy that can be triggered into mass deletion by a clock change is unacceptable under
§1.4.** Six rules; the first two are sufficient on their own and the rest are defence in depth:

1. **A count floor that age can never override.** The newest *N* checkpoints are kept unconditionally,
   whatever any timestamp says. *N* has a hard minimum below which the preference cannot be set. A
   clock jump then costs the user nothing at all.
2. **Ordering comes from a monotonic sequence circuitRF maintains itself**, recorded in the
   checkpoint's own metadata — not from the commit timestamp. The wall clock supplies the *label* a
   human reads; it never decides what is oldest. This also fixes the backwards-clock case, where
   timestamp order and actual order disagree.
3. **A sweep is bounded.** No single retention pass may remove more than a small fraction of the
   existing checkpoints. A pass that wants to remove more is **refused and reported** — which converts
   a clock fault from silent data loss into a Messages entry saying something is wrong with the
   clock, which is both true and useful.
4. **Retention thins; it never prunes.** Dropping a checkpoint reference leaves its objects in the
   repository as unreachable objects, which §4.5's `gc.pruneExpire = never` forbids any pack from
   removing — so a mistaken sweep is never permanent, and §5.6a says what is. **rev 3 named the reflog
   as the way back, and it is not** *(corrected in rev 5, §12 Q20)*: a deleted reference takes its
   reflog with it, and references outside `refs/heads/` carry none by default in any case. What finds
   an unreachable commit is `git fsck --unreachable`, which is an escape hatch only for someone who
   already knows git. So **circuitRF keeps a journal of what it thinned** — each dropped reference's
   name, commit identity and the time it was thinned, appended under `.git/circuitrf/` — and restoring
   a thinned checkpoint is one reference update, offered from the same list the live ones are in,
   marked as thinned. The journal is what makes rule 4 a promise to a designer rather than to a git
   user.
5. **Human-written commits are out of scope for retention entirely.** They are small (§2), they are
   the designer's own record, and no automatic process gets to delete them.
6. **Some checkpoints are kept, and retention may not thin them** *(new in rev 5, §12 Q27)*. Three
   kinds carry the mark (§5.2b): an explicit save-point, because §5.3 says the user's judgement about
   what matters beats any heuristic and thinning it would discard exactly that judgement; the two
   checkpoints that bracket an off period (§5.7), because they are what gives the gap its ends and a
   gap with no ends renders as the quiet interval §5.7 forbids; and any restore point the designer
   marks **keep** from the list. That third one is §10B.3's *"turn a restore point into something
   permanent"*, and it exists in Stage 2, before there is a Commit to turn it into.
   **A fourth kind is added in rev 6 (§12 Q34): the pre-restore checkpoint** (§5.8; BUILT 2026-09-07
   with §5.10 — `WorkspaceCheckpoints.IsAlwaysKept`). It is what makes
   *going back is never a one-way door* true, and rev 5's list omitted it — `IsAlwaysKept` covers the
   save-point and the two transitions and nothing else, so the one entry whose entire purpose is to
   undo a destructive operation ages out of the list on its own while the designer keeps working. It is
   not data loss, since §4.5 keeps the objects and rule 4's journal brings the entry back; it is the
   promise leaving the place the promise was made. Restores are rare, so keeping them costs nothing
   measurable.

**When a sweep runs, which rule 3 needs and rev 3 did not say.** *"No single pass may remove more than a
small fraction"* is not a guarantee until the passes are counted: a sweep on every checkpoint at one
tenth removes everything inside twenty checkpoints, while a sweep once per session cannot. **A sweep
runs at most once per session, on workspace close, after the close checkpoint and in the same window as
§2.4's packing** — which it also composes with, since thinning is what makes objects unreachable and
packing is what stores them compactly until §5.6a's explicit reclaim, if that ever comes.

**And a session that recorded nothing sweeps nothing and packs nothing** *(new in rev 5, §12 Q24)*.
§5.7a keeps a colleague's glance from creating a repository on a share; it did not keep it from running
a sweep, under the reader's retention preference, over the owner's checkpoints — a per-user setting
acting on a shared artifact, which is §4.4's mistake in a third file. A close that took no checkpoint
does no housekeeping either. Two writers with different preferences on one share still apply whichever
closed last, bounded by rule 1's floor; §12 records that as open.

Three alternatives were considered and each fails on a stated principle. **On open** puts work in front
of the thing the designer asked for. **On a timer** is §5.3's rejected idle trigger under a third name.
**On every checkpoint** is the case that makes rule 3 vacuous, above. Close is the boundary §5.3 already
identifies as the one that reliably exists in every session, it is behind the user rather than in front
of them, and it is where circuitRF is already doing housekeeping.

### 5.6a Reclaiming space is explicit, and nothing does it unasked *(new in rev 5, §12 Q20)*

**Status: BUILT 2026-09-06** · the operation was RC-3's; RC-6 added the journal it reads, and made
`GitReclaim.Reclaim` remove the entries it acted on — an entry left behind offers a way back to a state
that no longer exists.

rev 3 wrote `gc.pruneExpire = never` into §4.5 and left *"until ordinary packing eventually reclaims
them"* in rule 4. Both cannot be true, and the review found the document quietly relying on each.
**§1.4 decides it: no automatic operation may destroy history, and reclaiming unreachable objects is
destruction** — of nothing a designer can see, but of the only copy of a thinned state. So nothing
automatic reclaims, ever, and the repository grows by the objects thinning frees. For the design
documents that is kilobytes per checkpoint (§2) and it does not matter; for an import a designer
included under §8.2 and later regretted, it is the whole import, for as long as they leave it.

**The answer to *"where did the disk go"* is therefore an action, not a schedule.** §10A gains a row:
reclaim space from restore points thinned more than *N* ago, as a confirmed action carrying the
consequence sentence §10A's first rule demands — after this, those states cannot be brought back by
anyone. It is the one destructive control on the tab, it destroys only what has already been thinned,
and it is neither the "delete all history" §5.7 refuses nor the rewriting §8.3 refuses: every restore
point and every commit survives it unchanged. Rule 4's journal is what makes *"thinned more than N
ago"* answerable — git's own expiry is by object age, which is when the state was *made*, not when it
was thinned — so the reclaim protects the journal's newer entries for its duration and removes the
entries it acted on.

### 5.7 Turning it off — off, paused, and removed *(new in rev 2)*

**Status: BUILT 2026-09-06** · `RevisionSwitch` and `RevisionGaps` in `src/Design/Revision/`. The
ordering is owned by one function so no caller can reverse it, and the two transition entries carry
§5.6 rule 6's kept mark. **Turning it OFF never creates a repository** — arming to record that a
designer does not want a history is §5.7a's surprise pointed at a directory.

"Can I switch this off for a workspace, and does that delete the `.git`?" has three answers, and
conflating any two of them is how a designer loses history they meant to keep.

| state | what circuitRF does | what happens to the history already taken |
|---|---|---|
| **On** | takes checkpoints, offers commits | accumulates |
| **Off** | **writes nothing at all** | **kept, browsable, restorable** |
| **Removed** | not a circuitRF action at all — see below | gone, permanently |

**Off means circuitRF stops writing. It never deletes anything.** Three reasons, and the first is
sufficient on its own:

- **An off switch that destroyed a history would be the single most damaging control in the
  application** (§1.4). Nobody expects a checkbox to be irreversible, and by the time they discover
  it was, there is nothing to discover it with.
- **Off and on must be symmetric.** Turning it back on resumes the existing history. If off deleted,
  on would silently start from nothing, and the restore points a designer remembers would be gone
  with no event that explained it.
- **The escape hatch (§4.1) requires the repository stay an ordinary git repository.** Off is
  circuitRF declining to write; it is not a change to the repository's format or contents.

**While off, the history stays readable.** Restore points already taken remain browsable and
restorable — there is no reason to hide them, and hiding them would imply they were lost. What is
*not* allowed is for the off period to look like a quiet one: **the gap is shown as a gap.** A
designer scanning the history later must be able to see that nothing was recorded between these two
dates *because recording was off*, not because nothing happened. Rendering it as an ordinary interval
between two commits is the false-belief failure of §1.4 in its purest form.

**Off is per-workspace state, stored with the workspace, not an application preference.** An
installation-wide flag cannot gate per-workspace state: it is correct for the first workspace and
silently wrong for the second, which is a mistake this repository has already made once and recorded
(`src/Ui/RESOLVED.md`, the wirebond group work). It therefore lives in the `.cws` — the versioned
half, not the `.cwsuser` — which has a pleasing side effect: **turning it off is itself a recorded
change**, so the last commit before the history goes quiet is the one that says why.

**Because it is in the `.cws`, it travels** *(stated in rev 5)*. A clone or an archive of a workspace
that was switched off arrives switched off, and the recipient's own preference does not override it
(§5.7a): the flag says *not for this one* about the workspace, and the workspace is what travelled.
The recipient is told at their first boundary, exactly as §12 Q4's hold is reported, and the setting is
one click away.

**That side effect is only real if the transition is ordered, and the ordering is the whole of it.**
Turning it off writes the `.cws`, then takes one final checkpoint recording that change, and only then
stops writing. Reverse the two and the flag is set, circuitRF is already off, nothing is committed, and
the history simply stops with no entry saying why — which is the thing this paragraph claims not to
happen. Turning it back on records the resumption at the next boundary. **The pair is what gives the gap
two ends**, which is what the history browser needs in order to render it as a gap rather than as a
quiet interval. Both carry §5.6 rule 6's kept mark, because a pair that retention could thin is a gap
that retention could erase. This is §5.8's ordering problem in another place: the `.cws` is written before the
checkpoint that is supposed to contain it.

**One conflict this creates, and it must not be resolved silently.** §10A makes the AI-batch
checkpoint non-switchable, because it is §1.2 — the reason the feature exists. Switching revision
control off for the workspace switches it off too, which means an agent would edit the design with no
floor under it. That is a legitimate thing for a user to choose and an illegitimate thing for them to
stumble into, so **an AI edit requested while revision control is off states plainly that no restore
point will be taken, and offers to turn it back on**, before anything is modified.

**Removed is a different act, and circuitRF does not offer a button for it.** A user who genuinely
wants the history gone deletes the `.git` folder — one folder, plainly named, using their file
manager or the escape hatch of §4.1. There is no in-application "delete all history" command, for the
same reason there is no history-rewriting command (§8.3): an irreversible, total, one-click
destruction of the thing this feature exists to protect has no safe place in the UI.

**The reassurance that goes with it is worth stating explicitly, in the user documentation as well as
here: removing revision control cannot damage the design.** The design was never *inside* git in any
meaningful sense — a workspace is ordinary files in a folder (filesystem-is-truth), and `.git` is a
sibling directory holding copies. Delete it and the workspace opens exactly as it did, with every
file present and current. That property is a direct consequence of §0's "git is a storage engine, not
a user interface", and it is the single most calming thing that can be said to a designer who is
nervous about letting version control near their work.

### 5.7a What arms a workspace *(new in rev 4, owner's decision, §12 Q13)*

rev 3 used *"armed"* throughout — §4.4 captures identity *"before the feature is armed"*, §10A requires
it *"before the feature arms"* — and never said what does the arming or what the default is. That is
the whole of §1.2 resting on an unstated value, and both answers have consequences: a silent default-on
puts a `.git` in a folder because somebody launched circuitRF once, and a default-off means §12 Q5's
floor is absent for everyone who never opened Settings, which is not a floor.

**There is a per-user application preference — *keep a history of my workspaces* — and it is the
default for a workspace that has not recorded one of its own.**

| | |
|---|---|
| **application preference off** | circuitRF writes nothing, in any workspace. This is §5.7's "off", applied everywhere: nothing is deleted, every existing history stays browsable and restorable, and turning it back on resumes them all. |
| **application preference on, workspace has no recorded setting** | the workspace is armed |
| **application preference on, workspace switched off** (§5.7) | off, for that workspace only |

**The per-workspace setting outranks the preference, and §5.7's reasoning is untouched by this.** The
two answer different questions — the preference answers *"do I want this at all"*, the `.cws` flag
answers *"not for this one"* — and the second still may not be an installation-wide flag, for exactly
the reason §5.7 gives. What rev 3 was missing was not a place to put the per-workspace state; it was the
default that state falls back to.

**It ships on.** §1.2 is the governing motive and §12 Q5 says the floor exists before the capability
arrives; a floor that has to be found in a settings tab is not one. The population this reaches is
already narrow — §4.3 makes the entire feature invisible to anyone without git, which is most of the
target audience — so the default only ever applies to people who chose to install git in the first
place.

**And two guards, because "it ships on" is otherwise exactly the surprise §8.1's own reasoning
rejects:**

- **A workspace is armed at the first boundary that would record something, never at open.** Opening a
  workspace to look at it creates nothing. This matters most for the case §7A and §4.7 both assume — a
  workspace on a share, belonging to somebody else — where creating a repository because a colleague
  glanced at the folder is §7A.1's ambush pointed at a directory instead of a history. **And *"would
  record something"* needs a definition that does not need a repository** *(new in rev 5, §12 Q24)*,
  because before one exists there is nothing to diff against. For a workspace that has never been
  armed, the close boundary records something if circuitRF itself wrote a file into the workspace
  during the session — which it knows without asking the disk; an explicit save-point or a batch
  always does. For an armed workspace, §5.3's tree test applies. Under this rule a colleague's glance
  creates nothing, runs nothing and writes nothing, whichever of the two states the workspace is in.
- **The first time a workspace gains a repository, it is announced**: one Messages entry saying that a
  history is now being kept for this workspace, where the setting is, and — per §5.7's closing
  paragraph — that removing it later is deleting one plainly-named folder and cannot harm the design.
  §1.4's rule is that the state must be **visible**, not that it must be absent, and this is the
  cheaper half of that rule. It is the mirror of §12 Q4's "held" report: circuitRF says once, plainly,
  what it is and is not keeping.

---

### 5.8 Restoring — what it touches, and what it must take first *(new in rev 3)*

rev 2 described restoring as *"shown their own design at an earlier moment"*, which specifies the
presentation and not the operation. **A restore writes over every file in the workspace, and the
workspace is open at the time.** Four rules, and the first is not optional.

**A restore takes a checkpoint of the current state first, always.** The state being replaced may never
have been checkpointed — by §5.3's design it is whatever the designer has done since the last boundary,
which can be a whole afternoon. A restore that discards it is an automatic operation destroying
history, which §1.4 forbids in the same breath as everything else here. It also makes the operation
symmetric: a designer who restores to the wrong point can get back, which is the difference between a
safety net and a second cliff.

**Unsaved work is offered up before the restore, through the prompt that already guards a close, an
archive and a workspace copy.** A restore is built from what is on disk; performed on top of dirty
documents it produces a workspace matching neither state. The application already asks this question,
in these words, at every other point where an operation reads the disk rather than the editor.

**Open documents are reloaded, and their undo stacks are discarded.** This is the part that is silent
if missed. §5.4 says undo and checkpoints are deliberately unrelated — that governs how they are
*implemented*, and it is not permission to leave a stack describing edits to a file that no longer
contains what the stack describes. An undo after a restore re-applies the last few minutes of the
**replaced** state onto the **restored** file, producing a document that existed at no moment ever:
well-formed, openable, and wrong. §1.3 names that as the failure this entire document is written
against, and it would be this feature that caused it.

**A restore restores this workspace's files and nothing else**, and the presentation must not imply
otherwise. Content in a referenced workspace is not in this repository (§7A.1), so until §7's pins
exist a restore is deliberately partial — which is the strongest argument for §7 (§7A.4) and is stated
in the documentation rather than left to be discovered.

**Five more, each silent when missed** *(new in rev 5, §12 Q25)*:

- **A restore never moves `HEAD`.** It writes the checkpoint's tree into the working tree; the
  designer's branch, if there is one yet, stays where it was, and the next checkpoint or commit records
  the restored content as an ordinary next step (§6.3). Nothing is ever checked out.
- **A file created after the checkpoint is removed by the restore**, because a workspace holding last
  Tuesday's files plus Thursday's new cell matches neither state — the failure the second rule above
  exists to prevent, arriving from the other side. The pre-restore checkpoint is what makes that
  removal safe.
- **Ignored files are not touched.** Results are not in the checkpoint (§8.1), so a restore that swept
  them away would destroy hours of simulation to bring back a design that produced them. A restore acts
  on the set of files a checkpoint would capture (§5.2b) and on nothing else.
- **The revision-control flag and the policy files are preserved across a restore.** The `.cws`
  carries §5.7's off flag, so a restore across an off period would silently switch recording on or off
  — a restore is the user's decision about *content*, never about *recording*. And `.gitignore` carries
  §8.2's answers, so a restore to before one was given would silently start including the file it
  excluded. Both are re-applied after the tree is written, and they are the only things a restore
  leaves as it found them.
- **The way forward is offered where the way back was taken** *(new in rev 6, §12 Q35; BUILT
  2026-09-07 with §5.10)*. The
  pre-restore checkpoint is created, is kept (§5.6 rule 6) and is findable — and until §5.10 it was
  findable in the *other panel*, so a designer who restored from the Versions panel was left looking at
  a window showing no evidence that the afternoon they had just replaced still existed. A restore
  therefore reports itself in the history panel, in one line, naming the state it went back to and the
  entry the previous state was kept as, with the action that returns to it. **This is the most
  reassuring control in the feature and it creates nothing** — it points at an entry that already
  exists, and reaching it is an ordinary restore with an ordinary checkpoint of its own.

  **Both states are named by identity and time, and the action is named after its destination**
  *(rev 7, owner 2026-09-08)*. The line quoted the two entries' labels, which on the two entries a
  restore produces are both generated, so it named neither state in a way anybody could act on. And the
  action said *come forward again*, which is a DIRECTION — but going back and coming forward are one
  operation performed twice, so the control could be pressed for ever and never said where the last
  press had landed, which reads as a control that does nothing. Naming the destination is what makes
  the two presses visibly different, and from the third press on the two identities settle into a pair
  because §5.8's restore already declines to record a state the history holds.
- **An interrupted restore is detected on the next open, not discovered by simulating.** A restore over
  thousands of files on a share can be interrupted by a crash or a dropped connection, and what it
  leaves is §1.3's failure exactly: a workspace that opens, is well-formed, and is half of two states.
  circuitRF writes a marker under `.git/circuitrf/` before the first file and removes it after the last;
  a marker found on open is reported, naming both the target restore point and the pre-restore
  checkpoint, with the action to finish or to go back.

### 5.9 A workspace with no folder yet *(new in rev 3)*

`scratch-and-save-lifecycle.md` specifies an in-memory scratch workspace with no disk presence, which
materialises on save. **A repository is a directory inside a workspace folder; a scratch workspace has
no folder, so it has no repository and cannot be given one.** Three consequences:

- **Nothing in this document applies to scratch, and the affordances are simply not there** — the same
  posture §4.3 takes to a missing git, for a better reason: there is nothing to record into.
- **The repository becomes possible at materialisation, not at launch.** §8.1's `.gitignore` is written
  by the function that creates the workspace folder, so a materialised scratch workspace gets one on
  the same terms as any other (§8.1a).
- **An AI batch against a scratch workspace has no floor, and that is §5.3a's refusal rather than a
  silent gap.** It is §5.7's conflict in another guise: a legitimate thing for a user to choose, an
  illegitimate thing to stumble into. The remedy is the one the save lifecycle already offers — save
  the workspace — and it is *offered*, not merely named.

**Crash recovery is not this feature and must not become it.** The scratch world has its own autosave
and its own recovery cache. A checkpoint is a boundary in a workspace's history, not a copy of unsaved
bytes; conflating them would hand the safety net a second, differently-shaped job it is not built for
and would make both worse.

### 5.10 One panel, two kinds of entry *(new in rev 6, §12 Q32)*

**Status: BUILT 2026-09-07** · `brief-revision-control-10-one-history-panel.md` ·
`HistoryList`/`HistoryDates` in `src/Design/Revision/`, `HistoryTool` + `HistoryToolView` and
`DockLayoutRetirement` in `src/Ui/`, gated by `tests/Ui.Tests/Revision/OneHistoryPanelTests.cs`.
**Three things the build settled that this section left open:** three origins have no checkbox of
their own and are always shown (before-restore and the two ends of an off period — none of them is
noise, and a checkbox nobody would think to tick is how R-rc10-20's promise leaves the place the
promise was made); the *tidied-away also match* line reports the entries the filter is HIDING rather
than every thinned match, since a match that is in the list is not an incomplete answer; and
**"stop keeping" was not built** — it writes a second commit object, which the brief's own gate 13
forbids as an unnamed exception. `src/Ui/RESOLVED.md` has all three. **Supersedes RC-7 R-rc7-9.** §5 argues that the safety net and the narrative must not
be *conflated*; rev 6 does not withdraw a word of that. It withdraws the step rev 2 took without
arguing for it — from *two kinds of entry* to *two windows*.

**The reason given for two panels is an argument for a filter.** It is stated in the user
documentation, in these words: *a list with three hundred automatic entries and four deliberate ones
mixed among them is a list nobody reads.* That is true, and a filter answers it completely, while
being strictly better in the case the panels handle worst — because the default view can be the four
deliberate entries with the three hundred one click away, and **the designer never has to know which
panel to open.** That question is harder than it looks at the moment it is asked, which is the moment
something has gone wrong.

**And two panels produce a safety failure, not merely clutter.** §5.8's restore takes a checkpoint of
the current state first, and that checkpoint lands in the *restore point* list. A restore begun from
the Versions panel therefore leaves the designer looking at a window with no evidence that the
afternoon they just replaced still exists. The reassurance is real, it is already implemented, and it
is filed where the person who needs it is not looking. **§5.8 states the affordance; this section is
what makes it possible to put it anywhere sensible.**

**Six rules.**

1. **One panel, named for the thing it lists: History.** Not *Versions* — that word already has a
   precise meaning in this document (§5.2: titled, permanent, travels with a clone) and it is the word
   that distinguishes the two kinds of row. A panel in which everything is called a version has no
   word left for the distinction, and a designer who learns that these are all versions and then finds
   half of them did not arrive with a clone (§5.2a) has been told something false.

2. **The two kinds stay visibly distinct, and the distinction is what the mark means.** A version
   carries the accent-coloured tag it carries today; a restore point carries none. **The mark is not
   decoration and not importance** — it says *this one is titled, permanent and travels*, which are
   three promises the unmarked rows do not make. One mark, one accent, and nothing further: a second
   colour scheme is noise on a list whose whole problem is noise.

3. **The default view is every entry somebody stated an intent for.** Versions, explicit save-points
   and before-batch checkpoints (§5.3a rule 2 makes the batch's intent its label) are shown; workspace
   close checkpoints are not. One toggle — *show automatic entries* — reveals them. This is the whole
   of the answer to the close checkpoint being too much, and it is a display decision on purpose:
   **the close checkpoint keeps being taken.** §5.3's argument for it is the strongest sentence in that
   section — it is the one boundary that reliably exists in every session, including the ones where the
   designer never thought about history at all, which is precisely the designer §1 is written for.
   Stopping the capture to quieten a list would remove the safety net from exactly the population it
   exists for. The list is quietened instead.

4. **The filter state is per-user view state and lives in the `.cwsuser`** (§3.1), like every other
   thing about how a person has arranged their view of a workspace. It is not a Settings row: a filter
   a designer flips while looking for something is not a preference about what is kept, and putting it
   on the tab in §10A — every other row of which changes what exists — would be the category error that
   tab is most vulnerable to.

5. **Search, over what a person wrote.** Titles, batch intents, and the author. §10B's *"I broke the
   match network yesterday"* is a search, and a designer who has to scroll a year of entries to
   perform it is being made to do the machine's work. A search that matches entries retention has
   thinned says so on a line of its own rather than silently returning fewer results than exist —
   RC-6's journal already knows them, and *three tidied-away entries also match* is the difference
   between an incomplete answer and a wrong one.

6. **Nothing about the merge changes what is recorded, where, or what travels.** Checkpoints stay on
   §5.2a's per-checkpoint references and still do not clone; versions stay ordinary commits on the
   branch. **This section is a presentation change and may not become a storage change** — the moment
   the two are stored alike, §5.6's retention has a human-written commit in its scope and §5.2a's
   table stops being true.

**What a row carries, and what its expander carries.** The row is what a designer scans; the expander
is what they open when scanning was not enough, and the split is between *which moment was this* and
*what exactly is this*:

| on the row | in the expander |
|---|---|
| the time, and the date **including the year whenever it is not the current year** | the full timestamp with its zone |
| **the short identity** (rev 7) | the whole identity, and the action that copies it |
| the title, or the origin sentence for an entry nobody titled | the origin, spelled out (§5.5) |
| the tag mark, for a version | whether it is local-only or has been shared (§5.11 turns on this) |
| who kept it, on a workspace with more than one author | |
| the restored-from line (§5.5) and the left-out-file line (§8.2b) | the sequence number (§5.6 rule 2), and the kept mark |

**The short identity moved onto the row in rev 7** *(owner, 2026-09-08)*, and the origin sentences got
shorter to make room for it. **An entry nobody titled had no name** — only circuitRF's wording for how
it came about, which is the same three words on every entry of that kind, so a workspace closed forty
times listed forty rows reading *workspace closed*. The time tells them apart and gives a designer
nothing they can quote, point at, or hand to somebody helping them; and the sentence §5.8 reports a
restore in had to name two different states by two generated phrases, which is how it came to read
*"went back to 'workspace closed'. What you had was kept as 'your work before you went back'"*. The
generated labels are now as short as they can be and say only what KIND of moment it was — *closed*,
*before going back*, *before: <the batch's intent>* — because the identity beside them is what names
the row. **The whole identity is still what the copy action puts on the clipboard** and is still in the
expander: a value shown short and copied long is the trust problem R-rc10-15 already had to fix once,
and this is a second rendering of one string rather than a second value.

**This is not a widening of §0's rule.** R-rc7-4 already exempted the identity of a state — *nothing
git-shaped appears unbidden; what an explicit action produces may be named precisely* — and opening the
History panel is that action. No verb moved: nothing branches, checks out, stashes or merges, and
`NoGitVocabularyReachesADesigner` scans the same literals it always did.

**The year is a defect, not a sparseness.** `VersionHistoryTool.Day` renders `ddd d MMM` and
`RestoredFrom` renders `d MMM HH:mm`; a version kept in December reads in January as one kept this
week. Relative labels for the recent entries — *today*, *yesterday* — are welcome and are safe, because
§5.6 rule 2 already establishes that the wall clock supplies the label and never the ordering.

**The commit identity earns its place in the expander** under RC-7 R-rc7-4's rule: nothing git-shaped
appears unbidden, and what an explicit action produces may be named precisely. Opening an expander on
a row is that action, and the identity is the one string with which a designer, or somebody helping
them, can ask git a question circuitRF's own window cannot answer (§4.1).

**The action that goes back may not wear the undo glyph.** Both panels use `ArrowULeftTop` today,
which is the application's own undo arrow, on the one action every line of this document and its user
chapters is at pains to distinguish from an undo (§5.4). A glyph naming a *point in time* rather than a
step backwards is the requirement; the same glyph in both places was right and stays right.

**A row is right-clickable, and going back is one of several things on the menu.** Going back, copying
the commit identity, keeping permanently (§5.6 rule 6), comparing with the current state (RC-7
R-rc7-11), and §5.11's corrections. The menu is where the actions a header toolbar cannot hold live —
and it is the surface that makes the header a place for the two or three that create something rather
than a row of eight glyphs.

### 5.11 Correcting what a person wrote *(new in rev 6, §12 Q33)*

**Status: BUILT 2026-09-07** · `brief-revision-control-11-correcting-what-you-wrote.md` ·
`VersionSharing`, `VersionCorrections` and `TitlesLeaving` in `src/Design/Revision/`,
`CorrectWhatYouWroteDialog` and `TitlesLeavingDialog` in `src/Ui/`, five nouns on
`circuitrf history`, gated by `tests/Ui.Tests/Revision/CorrectingWhatYouWroteTests.cs`.
**Four things the build settled that this section left open.** `--amend` was permitted in one file and
turned out to buy nothing — it needs the shared index and restamps the committer date, which are the
two things a correction must not do, so a `commit-tree` over the recorded tree with the recorded
identity does the job and RC-7 gate 11's scan stays absolute rather than narrowing. A rename needs an
explicit SUBJECT, not only a label: four of the six origins write their own subject and ignore the
label entirely, so a rename routed through the label alone would silently do nothing on the automatic
entries a designer most wants to name. The annotation lives on `refs/notes/circuitrf`, not git's
default `refs/notes/commits`, and it travels as a SEPARATE best-effort invocation on fetch, send and
clone — a non-wildcard refspec naming a reference the remote does not have is fatal, and a workspace
with no corrections in it is nearly all of them. And the clone journey's review is the SOURCE's titles,
shown only when the source is a local repository this machine can read; an address cannot be enumerated
before it is fetched. `src/Design/RESOLVED.md` and `src/Ui/RESOLVED.md` carry all four. ·
Narrows §8.3, which keeps its own subject.

**The question this answers is not typography.** A designer who believes a title they typed is
permanent and uncorrectable will avoid typing an honest one, and will not share a history they cannot
tidy — which costs this feature the whole of §5.2's motive. The failure the owner named is a designer
who wrote something careless, or something with a customer's name in it, and now cannot show the
history to anybody. **That is a real cost of a rule that was never argued for on circuitRF's own
terms**, and §8.3's argument does not reach it: §8.3 is about *content* — a large file, a file that
should never have left the machine — and a title is not content.

**The line, and everything below follows from it:**

> **What was recorded is never altered. What a person wrote *about* it may be corrected by that
> person, until it has been shared — after which it can only be annotated.**

Content, trees, times and the sequence: immutable, always, with no exception anywhere in this
document. Titles and labels: commentary, and commentary is correctable by its author. The line is
drawn at **sharing**, because sharing is the only event that creates a second reader — and the second
reader is the entire reason git's own rule exists.

**Three cases, and only the third is hard.**

**(a) A restore point may be renamed, and may be deleted.** This is not history rewriting in any sense
§8.3 is about, and the mechanism is already built for another purpose. §5.2b makes each checkpoint a
**parentless** commit under circuitRF's own reference namespace: nothing chains to it, no clone takes
it (§5.2a), no push carries it. Renaming is a new parentless commit over the identical tree with a
corrected message and one reference update. Deleting is one reference delete — **which is exactly what
§5.6's retention already does**, journalled and reversible by the same *bring this back* the thinning
journal serves. A prohibition here guards nothing and costs a designer the ability to tidy their own
machine's safety net.

**(b) A version that has not been shared may have its title corrected.** A commit reachable from no
remote-tracking reference has, by definition, no second reader; correcting it invalidates nothing,
because there is nothing to invalidate. The newest such version is an amend. An older one is a rewrite
of the unshared tail, which is safe for the same reason and is more work over a workspace with
thousands of files — **so the requirement is the newest unshared version, and the tail is a follow-up
that must not be assumed.** Nearly every bad title is noticed within a minute of being typed, which is
the case this buys. §5.2a's checkpoint references are independent of the branch and a tail rewrite does
not disturb them.

**(c) A version that has been shared may be annotated, and may not be erased — and the difference is
stated rather than hidden.** The string is on somebody else's disk. A delete offered here would be
§5.3b rule 10's reassuring sentence about a protection that does not exist, told to the designer
instead of by the agent. So: a **correction**, carried as an annotation git can attach to a commit
without altering it, shown by the panel in place of the original with the original one click away, and
pushed alongside the versions it annotates. **The dialog says plainly that the original wording stays
in the file and can still be read by anyone holding the workspace.** A designer whose problem is
embarrassment specifically needs to know that, and a UI that softened it would cause the exact harm
they came to it to avoid — which is §1.4's false belief, in the one place where the belief is about
other people rather than about their own data.

**And the case that matters most is prevented rather than corrected.** §9 makes send, clone and
archive the only three ways a history leaves this machine. **A review step in front of them, listing
the version titles that are about to leave, is worth more than every correction mechanism above** — it
catches the careless word, and it catches the far likelier and far more expensive version of the same
fear, which is a customer's name or a part number in a title going to a different customer. It is one
dialog, in front of an operation that is already deliberate, and §9A.3 already establishes that this is
where a computation the user cannot perform belongs.

**What does not change.** No content is ever altered; no version is ever deleted; no author is ever
rewritten; nothing rewrites a shared chain; and circuitRF still offers no button for §8.3's rewrite.
The scan that holds that shut (RC-7 gate 11) narrows rather than lifts — see §12 Q33.

---

### 5.12 The longer note *(new in rev 7)*

**Status: BUILT 2026-09-11** · `MessageNotes` in `src/Design/Revision/`, the second field on
`KeepThisVersionDialog`, `KeepThisStateDialog` and `CorrectWhatYouWroteDialog`, `--note` on five nouns
of `circuitrf history` and on the `history checkpoint` tool, gated by
`tests/Ui.Tests/Revision/NotesOnEntriesTests.cs`.

**A title is one line and some decisions need a paragraph.** "Output match retuned" is what a designer
scans a list for six weeks later — and *why* it was retuned, what was tried first, what the measurement
said and which of two options this is are the parts that cannot be recovered from the files at all.
Before this there was nowhere to put them: the dialogs asked for one line and the panel showed one
line, so the reasoning either went into a title too long to scan or went nowhere.

**Both recording dialogs ask for both, and the second is behind an expander, closed.** Most entries do
not need a paragraph, and a second field in front of every designer makes the one that matters harder
to reach rather than easier — the same argument §5.11's escape-hatch paragraph is collapsed under.
**A designer who writes nothing loses nothing:** an entry with no note is written byte for byte as it
was before this existed, with no block and no trailer saying it has none.

**It is corrected where the title is** (§5.11's dialog, and all three of its cases unchanged). They
were written together, about one entry, and a designer who has just noticed the title is careless has
very often noticed the paragraph too. So case (a) rewrites the parentless checkpoint, case (b) rewrites
the unshared commit, and case (c) annotates — with the annotation now holding both halves, its first
line the corrected title and the rest the corrected note. **A single-line annotation written before
this existed still means a title and no note**, which is what it always meant.

**Four decisions worth stating, because each one is the thing that would otherwise fail quietly:**

- **R-rc12-1 — it lives in the BODY of the message the entry already carries**, not in a side file.
  The metadata has to survive everything the object survives (§5.2b's argument, unchanged), and the
  body is where a paragraph belongs in any case: §4.1's escape hatch reads it as ordinary prose under
  the title with no parser anybody has to learn.
- **R-rc12-2 — its extent is COUNTED, in a `CircuitRF-Note-Lines` trailer, never inferred.** The
  obvious alternative is to recognise circuitRF's own generated sentences and treat whatever is left
  as the note; that fails silently the first time one of those sentences is reworded, and every
  workspace recorded before the change then reads its own explanation back as part of somebody's note.
  The count is read from the FINAL run of trailer lines alone, because a note is free text and a line
  of it can look like a trailer — a note able to describe its own extent is a note that can be made to
  hide the rest of the message.
- **R-rc12-3 — every operation that REBUILDS an entry's message carries it through.** Marking an entry
  *keep* and renaming one both rewrite the whole message over the identical tree; either of them
  dropping the note would delete somebody's paragraph as a side effect of an operation about a label,
  with nothing said anywhere. R-rc11-4 already states this rule for the sequence, the origin, the kept
  mark and the left-out record; the note joins that list.
- **R-rc12-4 — the row says there IS one; the expander is where it is read.** §5.10 rule 3's split is
  what keeps the list scannable and a paragraph cannot be scanned — but an expander nobody knows to
  open is a paragraph nobody reads, which is the one way this feature fails quietly. So the row carries
  one small glyph and no accent: it marks content, not importance.

**And the search reads it** (R-rc10-9 extended). It is the half most worth searching: a title is four
words somebody chose under pressure, and the paragraph under it is where they wrote down the part they
would later go looking for.

**What does not change.** Nothing here alters what was recorded — a note is commentary in exactly
§5.11's sense, and it is correctable on exactly §5.11's terms. A restore point's note stays on this
machine because the entry does (§5.2a); a version's travels with the version because the message does.

---

## 6. Where the software analogy breaks

This section exists because the failure mode of this whole idea is importing software-engineering
workflow wholesale into a domain that cannot support it.

**Status: BUILT 2026-09-06** (§6.1's user-facing half, §6.3, §6.4) ·
`brief-revision-control-7-commit-and-history.md` · `DocumentClash` in `src/Design/Revision/`.
**One thing the build settled:** because `.gitattributes` marks the five types `-merge`, git writes **no
conflict markers** for them — so there is nothing in the working tree to find, and the two versions live
only in the index's unmerged entries (stage 2 and stage 3, read with `ls-files -u`). An implementation
that scanned files for markers would report all-clear on every clash it exists for.

**6.1 There is no merge for a layout, and circuitRF must not pretend there is.** A three-way text
merge of a polygon's vertex list can produce geometry that is invalid, or valid and wrong, while
remaining perfectly well-formed JSON that opens without complaint. The same is true of a schematic's
connectivity. **A merged design that is silently wrong is worse than a conflict**, because the
conflict is at least visible.

Therefore: the generated `.gitattributes` marks `.clay`, `.csch`, `.csym`, `.cws` and `.ctech` as
unmergeable. Conflict resolution is **whole-file, pick a side** — presented as a choice between two
named versions, never as a diff to reconcile. The same file pins their end-of-line treatment (§4.5),
because a document whose bytes change on the way to or from disk is not the document that was written.

**6.2 This is why enterprise flows do not use distributed version control.** The general architecture
in large analog/RFIC design management is centralised and **pessimistic**: check-out/check-in locking
so two people cannot edit one cellview concurrently, versioning at the granularity of the *cellview*
rather than the file, and a separate configuration object that binds which version of which cell a
hierarchy resolves to. The locking is not conservatism — it is the direct consequence of §6.1. When
merge is impossible, preventing divergence is the only remaining strategy.

circuitRF is not going to build a lock server. But the observation shapes three things: it confirms
whole-file conflict resolution as correct rather than lazy, it identifies the one genuinely
enterprise-grade feature on this list (§7), and it is the reason §7A's referenced workspaces default
to **read-only** — which is the cheapest available approximation of a lock, and needs no server.

**6.3 Designers will not create branches, and circuitRF creates none either.** The native idiom for
trying an idea in this domain is already **another cell, or another view within a cell** — cheaper,
visible in the project tree, and comparable side by side, which a branch is not.

**rev 4 said branches nevertheless appear in one scenario — restore an old state, then keep editing —
because "git requires a branch there". It does not** *(corrected in rev 5, §12 Q19)*. Git requires a
branch only if the restore is a *checkout* of the old commit. It is not: a restore writes the old
state's tree into the working tree and leaves `HEAD` where it was (§5.8). The next checkpoint or commit
then records the restored content as the next step in one line of work — which is exactly what the
designer meant by "I went back to Tuesday", and exactly what a shared branch needs under §6.1, where a
second line of work would be a merge nobody can perform. The commit that follows says what it was
restored from (§5.5). The *variant*, its silently-created branch, its naming and its documentation
scenario are withdrawn, and the words "branch", "checkout" and "HEAD" now have no scenario in which to
appear.

**6.4 Stashes: excluded.** A stash is a solution to a problem designers do not have (a dirty tree
blocking a branch switch, in a world where they do not switch branches). Their equivalent is already
"save a copy of the cell", and it is better because it is visible.

---

## 7. Cell references and version binding

**Status: BUILT 2026-09-07** · `brief-revision-control-9-clone-and-pins.md` · `CwsWorkspaceRef.Pin`,
`WorkspacePins` and `PinnedContent` in `src/Design/Revision/`, `WorkspaceSharingService` and the
Project-panel rows in `src/Ui`, `circuitrf history pins|pin|unpin`, gated by
`tests/Ui.Tests/Revision/CloneAndPinsTests.cs`. **One thing this section left unstated decided the
whole build**: it says the reference "carries a commit identity" and does not say what a pinned
reference then RESOLVES to. Recording the identity and going on reading the library's working tree
would satisfy every sentence here and be worth nothing — the moment the librarian checks out anything
else, the design resolves against content it was never verified against, which is the failure §7A.4 is
written to prevent arriving through the feature meant to prevent it. So a pinned alias resolves to an
expanded copy of that version, and `src/Design/RESOLVED.md`'s RC-9 entry has the cost of it.

`workspace-and-project-tree.md` §5B/§5C define referenced workspaces and external cell references: a
`.cws` carries a `ReferencedWorkspaces` alias table (`CwsWorkspaceRef` — an alias and a path) and a
`ReferencedCells` list of `ws://alias/rel/path` strings resolving through it.

Under version control, an unversioned reference is a hazard: "this design uses the amplifier cell from
that workspace" resolves to *whatever that workspace contains today*, which is not reproducible and
not what was simulated.

**A reference should be able to carry a commit identity** — pinning the exact version of the
referenced content this design was built and verified against, with an explicit, visible action to
move to a newer one.

**The identity belongs on the alias, not on each cell.** `CwsWorkspaceRef` is already the place a
cross-workspace path is written down exactly once (its own header says so, R-mw2-4); pinning there
pins every `ws://alias/…` through it, consistently, and one referenced workspace is one repository
with one commit identity. Pinning per cell would allow one design to reference two mutually
inconsistent versions of one library, which is a state nobody wants and nothing detects.

This is the same construct as the configuration object in §6.2, and it is the highest-value item in
this document for anyone using circuitRF beyond a single designer. It is also the only feature here
that changes what a *simulation result means*: a result becomes reproducible in the strong sense, from
a commit identity, rather than from "the files as they were".

It is deliberately placed late (§11) because it depends on everything else being solid, and because
it is worthless until sharing (§9) exists. **§7A is what it is for.**

---

## 7A. Managed collections of workspaces — the librarian *(new in rev 2)*

The scenario: someone maintains a set of workspaces — verified component cells, qualified reference
designs, a house PDK's wrappers — that designers reference from their own local workspaces. The
maintainer publishes; the designers consume. This is the ordinary shape of a real RF group, and the
machinery for the *referencing* half already exists (§7). What this section settles is what **history**
does across that boundary.

### 7A.1 The rule everything else follows from

**Status: BUILT 2026-09-06** · `EnclosingRepository` in `src/Design/Revision/`. **Two paths reached a
repository that was not the workspace's own before this landed, and both were silent** — `git init` at
a workspace root inside somebody else's repository, and a rewrite of a user's own configuration at the
workspace root. `src/Design/RESOLVED.md`'s RC-6 entry has the analysis.

> **circuitRF commits to exactly one repository: the one whose root is the open workspace.**

Not to a repository above it (§12 Q4), not to a referenced workspace, not to a repository nested
inside it. One workspace, one history, and circuitRF's writes never leave it.

The reason is the same in all three directions, and it is worth stating once rather than three times:
**an automatic commit in a repository the user also uses for something else will sweep up work that
was not circuitRF's to commit.** A checkpoint is `commit -a`-shaped by nature — it must capture
everything, because §1.2's whole point is capturing files the user did not open. In someone else's
repository that is not a safety net, it is an ambush.

### 7A.2 Referenced workspaces are read-only by default

**Status: BUILT 2026-09-06** · `brief-revision-control-2-read-only-references.md` ·
`workspace-and-project-tree.md` §5C.1a is where it is specified for a reader who arrives from the
workspace side rather than from here. Two things the build settled that this section left to a brief
are marked below.

`CwsWorkspaceRef` gains one field: whether this reference is editable. **The default is read-only**,
and File ▸ Reference Workspace… creates read-only references.

This is not paternalism, and the alternative is not "more freedom" — it is silent divergence. Consider
the writable case honestly. A designer opens a library cell through a `ws://` reference, finds a
wrong pad size, fixes it, and saves. Then:

- The file is written (filesystem-is-truth; circuitRF does not intercept ordinary saves).
- **Their** workspace's history contains no record of it — the file is not in their repository.
- **The librarian's** history contains no record of it either — circuitRF did not commit there (§7A.1),
  and the librarian was not asked.
- The fix works for that designer and for nobody else. Every other designer's simulation still uses
  the wrong pad.
- When the librarian next publishes, the local edit is overwritten, or it conflicts — and §6.1 says
  there is no merge to resolve it with.

Every step of that is silent. Read-only converts the whole chain into one refusal at the moment of
editing, which is the only point where the designer still has the context to do something sensible.

**The refusal must carry the remedy**, and there is a good one that needs no new mechanism: *the
library is a workspace; open it as your own workspace to edit it, with its own history.* **§7A.3
follows that path to the end** — what happens once both workspaces are open, and why the cell still
does not become editable *inside* the referencing window. On a shared
library that is a clone (§9) plus a push, or a request to the librarian — either way it is a
deliberate act with a record, which is what was missing.

**Editable references remain possible**, per reference, as an explicit choice. Two designers working
that way in one library is exactly the concurrent-edit problem §6.2 describes and circuitRF is not
solving it — so a workspace referenced editably is marked as such wherever it appears, and a document
opened from one carries the mark in its tab. §1.4 again: the state must be visible, not inferable.

**This applies to every workspace, whether or not it is under revision control — and that is the
important part of this subsection.** Read the chain above again and ask which steps need git. Only two
of them do: "their history contains no record" and "the librarian's history contains no record" are
vacuous when nobody has a history. **The other three are true today, in the shipped application, with
no git anywhere on the machine** — the fix works for one designer and nobody else, the librarian
overwrites it on the next publish, and §6.1 says there is nothing to merge it with. Publishing does
not even have to be a git operation; a file copy onto a share does it.

So §7A.2 is **not a revision-control feature.** It is a defect in the existing workspace model
(`workspace-and-project-tree.md` §5B/§5C) that this investigation happened to find while thinking
about something else, and it must not be allowed to ship only as part of a git feature. If it did,
the users who never get the fix are exactly the ones §4.3 identifies as the majority — RF designers
with no git installed — and they are the users with **no history to fall back on when it bites**.
That is the wrong way round. Hence §11: read-only ships in Stage 1 and does not depend on git being
present.

**Existing workspaces get the fix too, and this is a behaviour change worth naming.** An older
`.cws` has no editability field on its `CwsWorkspaceRef` entries, so a default must be chosen for
them. The repository's usual instinct for an absent field is "restore the old visible behaviour"
(`CwsTreeViewState.ReferencedCells` reasons exactly that way about a missing value). **That instinct
is wrong here**, because the old behaviour is not a preference the user set — it is the hazard.
Absent means read-only, for existing references as well as new ones. The cost is one-time friction
for anyone who was editing through a reference: a refusal, carrying the remedy, and a per-reference
override one click away. The alternative is that the workspaces most likely to have accumulated this
practice are the only ones never protected from it.

### 7A.3 Editing a referenced cell with both workspaces open

The scenario, because it is the one every designer will actually hit: **A references B. The designer
tries to edit a cell of B from inside A and is refused. They open B in a window of its own. Both are
now open. Can they edit the cell, and does B see it?**

**Yes to both — and the mechanism already exists and is already built.** `ActivateIfOpenInAnotherWindow`
(MW1 R-mw1-10) routes an open request for a file that is open in another workspace window to *that*
window, and its own header already names this case: *"a cell referenced from another workspace must
not become editable through the window that merely references it."* §7A.2's read-only default and this
rule are two halves of one idea.

**But not by the route the question implies, and the difference matters.** The cell does **not** become
editable *inside A*. The attempt from A lands the designer in **B's editor for that cell** — B's
window comes forward, B's tab is selected, the keyboard goes there. From the designer's point of view
they asked to edit the cell and are now editing the cell, which is what they wanted; what they get in
addition is the true answer to "where does this cell actually live", which is information they need
anyway.

**Why the other route is not merely different but harmful.** If A's view became editable, one file
would have two editors, in two windows, with two undo stacks and two dirty flags. Edit in A, edit in
B, save B, save A — and A's save silently discards B's edit. No conflict, no warning, both windows
showing "saved", and §6.1 says there is nothing to merge it back from. That is worse than the
cross-workspace divergence §7A.2 exists to prevent, because it is the same designer losing **their
own** work, minutes apart, in the same session. **One file, one editor, across every open window** is
the invariant, and it is not specific to references — two workspaces can reach one cell folder by
other routes too.

**Does B recognise the change? Trivially, because B *is* the editor.** The save goes through B, the
document is B's, it dirties B's project tree and it lands in **B's** history, per §7A.1. Nothing
special happens; that is the point of routing the edit to the owner.

**Three things this scenario makes concrete that the designer is likely to miss:**

1. **Whether A picks the change up depends on the pin (§7A.4).** With an unpinned reference, A
   refreshes and sees the new cell. **With a pinned one, A deliberately does not** — the pin says
   "the version this design was verified against", and silently moving it would defeat the whole
   feature. A instead shows that a newer version is available. This *will* surprise someone —
   *"I just edited it and my design didn't change"* — so it is a thing to explain, not a thing to
   let people discover.
2. **The edit is in B's history, not A's.** Restoring A to last Tuesday restores A's files; the
   referenced cell is whatever B has, or whatever A's pin names. Correct, and worth saying out loud
   because "restore my workspace" sounds more total than it is.
3. **The read-only mark on the reference does not change.** It stays read-only whether or not B is
   open, because it describes A's *relationship* to B — A does not own that content — and that
   remains true while the designer edits it in B. Making the mark depend on which windows happen to
   be open would produce behaviour nobody can reason about, and would flicker.

**Two gaps this leaves, both small and both worth a brief's attention:**

- **§7A.2's refusal should offer the action, not just name it.** *(BUILT.)* "Open workspace B" as a
  one-click action on the refusal turns the sequence above from a workaround the designer has to
  invent into the supported path it should be. Without it, the remedy is correct advice that still
  costs them a File ▸ Open and a hunt for the folder. **The action opens B AND lands on the cell** —
  an action that opened the folder and left the designer to find the cell again is the File ▸ Open it
  was meant to replace.
- **The existing message is worded for the wrong case.** *(BUILT.)* `ActivateIfOpenInAnotherWindow` says
  *"already open in <window> — shown there rather than opened twice"*, which is right when the
  designer had it open and forgot. Arriving here from a referenced cell they never opened, the true
  reason is ownership, not duplication — the message should say the cell belongs to B and is being
  edited there.

### 7A.4 What the local workspace records about the library's history

It records **which version it was verified against** — §7's pin, on the alias.

That single fact is what makes a managed collection work, and it works by *decoupling*:

- The librarian publishes whenever they like. **Nothing in any designer's workspace changes.** A
  library that silently updates under a design that was signed off is precisely the IP-integrity
  failure §1.4 is written against.
- "A newer version is available" is a visible, per-reference state and an explicit action to take it,
  not an ambient event.
- Moving to a newer version is itself a change to the local workspace's `.cws`, so it lands in the
  local history with a date and an author — the designer can later see *when* their design started
  using the new library, which is usually the question being asked when something stopped working.
- A checkpoint (§5.1) captures the pin along with everything else, so restoring to last Tuesday
  restores which library version last Tuesday's design resolved against. **Without the pin, a restore
  is only partial and does not say so** — the designer gets their files back and silently keeps
  today's library. That is the strongest argument for §7 in this document.

**A pin is not a copy.** The referenced content still lives in the other workspace; the pin records an
identity, not bytes. If that workspace is unreachable or has had its history rewritten, the pin cannot
be honoured — and that must be reported plainly rather than falling back to "whatever is there now",
which would defeat the entire purpose. A designer who needs the content to travel with the design has
the existing workspace-archive path, which is a different tool for a different problem.

### 7A.5 What this is not

- **Not a lock server, and not a permission system.** Read-only here is circuitRF declining to write,
  not the filesystem declining. Someone determined to edit the library's files in Finder can. The
  goal is to make the *accidental* case impossible and the *deliberate* case visible, which is the
  whole of what is achievable without a server (§6.2, §10).
- **Not a package manager.** No version ranges, no resolution, no transitive constraint solving. A pin
  is an exact identity and moving it is a human decision.
- **Not nested repositories.** A repository *inside* a workspace (a library cloned into it) is left
  alone entirely — not committed to, not committed *as* anything by the enclosing workspace. It is
  reported as an excluded subtree in the same way §12 Q4's enclosing repository is reported, for the
  same reason.

---

## 8. Large files: prevent, do not cure

An RF designer unfamiliar with git will, eventually, commit a very large file — an imported GDSII, a
board's Gerber set, a results cube — then delete it, and be surprised that the repository stayed
large. This is predictable enough to design against directly.

**8.1 Prevention.** New Workspace writes a `.gitignore` that excludes results by default —
`.npy`, `.spl`, `.lpcwave`, and simulation output directories. The justification is not size, it is
**reproducibility**: results are a function of the design and the engine, so the design is the thing
worth versioning. (Binary and float-valued, they would also delta terribly — §3.2's rule again.)

### 8.1a Workspaces that already exist, and the first commit into one *(new in rev 3)*

§8.1 puts the `.gitignore` in the function that creates a workspace. That is right, and on its own it
is not enough: **every workspace that exists today was created before that function wrote one.** Under
rev 2 as written they would never receive a `.gitignore` at all and would track results forever — and
they are precisely the workspaces that matter, because they are the ones with years of accumulated
output already sitting in them.

**So the generated files are written whenever a workspace first gains a repository, as well as when a
workspace is created.** The rule is not "on creation" but *"a repository circuitRF created has
circuitRF's policy files"*, and every path that creates one reaches it. §12 Q4's **adoption** is the
third path: adopting a user's repository writes them too, because adoption is circuitRF taking
responsibility for what is kept, and it is the moment the user said yes.

**The first commit into an existing workspace is a different operation from every later one**, and
§8.2's guard does not fit it. That guard names one unexpectedly large newly-added file and offers three
choices; the first commit into a workspace with four years of output has hundreds of them, and **a
dialog that names hundreds of files is not a dialog.** The first commit therefore summarises **by
category and by size**, offers the same three choices *per pattern* rather than per file, and shows
what the generated `.gitignore` already excluded before asking about anything else. The one thing it
must not do is proceed silently: a designer whose repository is three gigabytes on its first day is the
complaint §8 exists to prevent, arriving on day one instead of in month six.

**Once written, the `.gitignore` belongs to the workspace, not to circuitRF.** A designer may edit it,
and §8.2's third choice appends to it. **circuitRF adds lines and never rewrites the file**, and never
removes a line it did not add in that same operation. A policy file that is silently regenerated is a
policy file whose user edits vanish — and the user finds out when something they had excluded turns up
in an archive.

**8.2 A guard at the boundary.** Before a commit, an unexpectedly large newly-added file is named,
with the choice offered plainly. **The choices are about the file's role, not about git mechanics:**

| choice | what it means | what it does |
|---|---|---|
| **Include it** | this is design input — the GDSII the layout was built from | tracked; costs one copy now and one delta whenever it changes (§2 says that is cheap) |
| **Leave it out this time** | undecided; ask again | nothing is written; the file is untracked |
| **Never include files like this** | this is a by-product, not a design | a pattern is added to `.gitignore`, applying to a file that is **not yet tracked** |

That third choice is the one that has to be written carefully, because it is the one with a
consequence the user must understand: a file matching it is **not in the history**, so it is **not in
a restore, and not in a clone**. §10B requires that sentence to appear where the choice is made.

### 8.2a "Add it once, then ignore it" — considered, and recommended against

The proposal: commit the large file once so it is recoverable, then ignore it from then on so it stops
costing anything. It sounds like the best of both. It is not, and the reason is mechanical rather than
a matter of taste.

**`.gitignore` has no effect on a file that is already tracked.** So the option resolves into one of
two implementations, and both are worse than either of the plain choices above:

- **Add the pattern only.** Nothing changes. The file goes on being committed on every save, exactly
  as if the user had chosen "Include it". The user believes they turned something off; they did not.
  Under §1.4 this is the worst outcome in the entire document — not because of the disk cost, but
  because the UI told the designer something false about what is being kept.
- **Add the pattern and untrack the file** (`git rm --cached`). Now the file is present in history at
  one old version, **absent from the current state**, and present only in the working copy of whoever
  happened to be at the machine. A clone gets a workspace with that file **missing** — and if a
  `.clay` references it, the reference dangles. A restore to yesterday brings back the *stale* version
  from whenever "once" was. This is design-IP loss wearing the costume of a convenience feature.

**And the benefit it is reaching for does not exist.** §2 measured the case directly: a tracked file
that does not change costs **nothing per commit**. "Include it" already has the property that
add-once-then-ignore was invented to buy. The only scenario where the two differ is a file that is
large *and* changes often *and* whose current content matters while its history does not — and for
that the correct answer is "Never include files like this" (out of history entirely, current content
always on disk, nothing stale anywhere), which §8.2 already offers.

**If it is nevertheless wanted**, it cannot ship as a third button with a tooltip. The tooltip would
have to say *"the version in your history will not be the version on your disk, and people who clone
this workspace will not get this file at all"* — at which point it is not a convenience. The minimum
conditions would be: the untracking implementation (never the pattern-only one), and a **permanent,
visible marker that this workspace's history is deliberately incomplete**, surfaced anywhere a restore
or a clone is offered. That is a large amount of machinery to support an option whose better
alternative is one row above it in the same dialog.

### 8.2b The boundary nobody is at *(new in rev 5, §12 Q26)*

§8.2's guard and §8.1a's first-commit summary both ask a question, and two of §5.3's three boundaries
have nobody to answer it: a close is at the moment the designer asked to leave, and a batch is
headless. A dialog at quit time listing four years of result files is quitting feeling broken; a batch
that waits on one hangs an agent.

**At a boundary with nobody to answer, the checkpoint proceeds and the file is left out.** §9A.1's rule
decides which way: including a file is irreversible (§8.3 — it is in the history for good), leaving it
out is not (it is asked about next time, and included then if that is the answer). The checkpoint's
metadata records what was left out (§5.2b), the restore-point list shows the entry as incomplete with
the names, one Messages entry per session says so, and **the question is asked at the next interactive
moment** — the next explicit save-point, or the entry's own action. §8.1a's summary follows the same
rule by pattern: the generated `.gitignore` applies, patterns over the threshold are left out, and the
summary is presented at the next interactive moment rather than at the door. What must not happen is
the silent version of either direction, and §10B.1 gains the row.

**8.3 No cure, on purpose.** History rewriting is the only real remedy, and it invalidates every
existing clone. **circuitRF must not offer a button for it.**

**This section is about content, and rev 6 narrows it to say so** *(§12 Q33)*. Its subject is a file
in the history that must not be there — a large one, or one that should never have left the machine —
and the remedy for that is a rewrite of every entry, which is why there is no button. **A title a
designer typed is not content**, and §5.11 governs it on a different rule: what was recorded is never
altered, what a person wrote about it may be corrected by that person until it has been shared. The
two do not conflict, and the reason the distinction was not drawn earlier is that this section's rule
was inherited from git rather than derived from circuitRF's own situation, in which the typical
workspace has one designer, one machine and no clone at all. A user who genuinely needs it has the
escape hatch of §4 and should be told so in plain language. Storing large files out-of-band via an
extension is likewise excluded: it requires server infrastructure and relocates the problem rather
than solving it.

---

## 9. Cloning a repository as a workspace

**Status: BUILT 2026-09-07** · `brief-revision-control-9-clone-and-pins.md` · `WorkspaceClone` and
`WorkspaceRemotes` in `src/Design/Revision/`, `CopyWorkspaceDialog` and three File-menu items in
`src/Ui`, `circuitrf history clone|fetch|send`, gated by
`tests/Ui.Tests/Revision/CloneAndPinsTests.cs`. **Nothing here is host-specific and nothing here is
automatic**, both by construction: the only thing not passed through to git is a source beginning with
`-`, and the gate for the second is a source scan of the open and checkpoint paths rather than an
observation of one run.

If a cloned directory contains a `.cws`, circuitRF can open it as a workspace. Once git is already
being driven as a subprocess this is nearly free, and it is the strongest *product* argument in this
document: a public library of reference designs, opened in one action, from any git host — with
fetch and push for those who want them, and clone-and-read for a manager who only wants to look.

It is also the mechanism §7A.2's refusal points at: the way to edit a library is to open it as a
workspace of its own.

It depends on one property the workspace model must guarantee: **references inside a workspace are
relative and portable.** The workspace-archive work has already established this and already found
the trap (a `.clay`/`.csym` bitmap reference that never resolved relatively until it was made to).
Cloning exercises exactly the same property.

Nothing here is host-specific; the design binds to git, not to any hosting service.

**9.1 Credentials: circuitRF has none, asks for none, and never hangs waiting for them** *(new in
rev 3)*. Fetch, push and clone against a private repository need authentication. circuitRF supplies
**whatever git is already configured to supply** — the user's credential helper, their SSH agent, the
setup their IT department gave them — and adds nothing of its own: no prompt, no stored token, no
keychain entry. It has no business holding a credential for a service it does not integrate with, and a
stored credential is a liability whose only justification would be a convenience §9 does not need.

What makes that minimal posture *safe* rather than merely small is §4.5's environment rule:
**`GIT_TERMINAL_PROMPT=0`, so an operation that would have asked refuses instead**, with a sentence
naming what the remote wanted and what would satisfy it. Without that rule the same minimalism produces
a subprocess blocked on an invisible prompt — no message, no exit code, and a workspace close that
never completes.

**`GIT_TERMINAL_PROMPT=0` reaches git's own prompts and nothing else** *(new in rev 5)*. An SSH key
with a passphrase and no agent prompts through `ssh`, which does not read that variable; with no
terminal it fails rather than hangs, which is the wanted outcome — but only because circuitRF gives it
no terminal. A credential helper that opens its own window, the ordinary Windows case, is the user's
configured setup and may open it: that is not a hang, it is the helper doing what the user installed
it to do, and §9's posture is to let it. What circuitRF must never do is supply a terminal, an
`SSH_ASKPASS`, or a stored answer of its own. And the bound on a network operation is inactivity, not
wall-clock (§4.5): a slow clone of a large library is not a hang, and a fixed limit would turn it into
a reported failure.

---

## 9A. Archiving a workspace — history is excluded by default *(new in rev 2)*

**Status: BUILT 2026-09-07** · `brief-revision-control-8-archive-with-history.md` · `HistoryArchive`
in `src/Design/Revision/`, `ArchiveHistoryPreparation` and the writer's own repository copy in
`src/Ui/Archive/`, the checkbox and its computed block in `ArchiveWorkspaceDialog`, gated by
`tests/Ui.Tests/Revision/ArchiveWithHistoryTests.cs`. **Three things the build settled that this
section did not say.** §9A.3's enumeration cannot be an OBJECT walk: `git rev-list --objects` prints
each object once, so two deleted files with identical content collapse to one name — measured, on a
340-state fixture seventeen deleted files were reported as **one**, which is §9A.4's nearly-true
guarantee reached by accident and in the direction of saying less. It is a PATH walk
(`git log --stdin --root -m --name-only -z`), and **`--root` is load-bearing**: a checkpoint is
parentless by design (§5.2b), so without it git shows no diff for one at all and the whole population
this warning exists for is absent. **And an archive that carries only FILES is not a repository**: git
decides a folder is one by finding `objects` and `refs` inside it, and the pack §9A.3 requires is
exactly what leaves `refs/heads` and `refs/tags` empty — a zip stores files, so the empty directories
have to travel as directory entries or the extracted workspace answers *fatal: not a git repository*
with nothing visible having gone wrong. Findings in `src/Ui/RESOLVED.md`. ·

The workspace archive (`src/Ui/Archive/`) is how a workspace leaves the machine it was made on: to a
colleague, to a customer, to a supplier, into an email. Once a workspace has a history, the archive
has to decide whether to take it, and **the decision is not symmetric.**

### 9A.1 Why this is the most consequential default in the document

Every other failure this document guards against is a *loss*: a checkpoint not taken, a restore point
thinned early, a history switched off. Those are bad, and §5.7 notes the mitigating fact that the
design itself is ordinary files on disk and survives all of them.

**Including history in an archive risks the opposite kind of failure, and it is unrecoverable.** Git
history contains every earlier version of every tracked file — *including files that are no longer in
the workspace at all*. A designer who imported a customer's GDSII, finished with it, deleted it, and
then archived the workspace for a **different** customer would ship that GDSII. Nothing in the visible
file tree would show it. Nobody would find out until someone else did.

That is design-IP disclosure, it is possibly a contractual event, and unlike every loss in this
document **it cannot be undone by anyone at any later time.** So:

> **When a default can be wrong in two directions, it points away from the irreversible one.**

**The checkbox is off by default**, as the owner proposed, and the default is a safety property rather
than a convenience — which is why it is stated here in the architecture rather than left to the
dialog.

### 9A.2 What the two states produce

| | archive contains | recipient gets |
|---|---|---|
| **history excluded** *(default)* | the workspace's current files, exactly as today | a working workspace with no history. Per §5.7 that is completely normal — it opens, everything is present and current. If they switch revision control on, they start a fresh history of their own. |
| **history included** | the same files **plus the repository** | the same workspace, plus every earlier version of every file it ever tracked, and the ability to restore any of them |

Included is genuinely valuable and the owner is right that it is necessary — a design handed to a
partner *with* its history is a far better handover than a snapshot, and internally it is usually what
you want. The point is only that it must be chosen, not inherited.

**The `.cwsuser` is never archived, in either state.** It is one person's panel arrangement (§3.1);
the recipient opens on the default, which §3.1a already requires to work.

### 9A.3 What the dialog must say — and the one thing it must compute

A checkbox reading *"include git history"* would fail on two counts: it is git vocabulary (§0), and it
does not tell the user what they are about to send. The label describes the content, and **when the
box is ticked the dialog states what including it actually adds** — computed, not generalised:

- **how much larger** the archive becomes;
- **how many earlier versions** it carries;
- and the sentence that does the real work: **how many files are in the history that are no longer in
  the workspace, and what they are called.**

That last item is cheap to obtain (git can list every path ever deleted) and it is the single most
effective warning available, because it converts an abstract worry into three filenames the designer
recognises. Someone about to leak a file will almost always recognise it by name; nobody recognises
"the repository contains historical objects".

**The enumeration walks every reference the archive will carry, and that is more than the branch**
*(new in rev 4)*. §5.2a makes the archive the one journey that takes §5.1's checkpoints with it, because
it copies the directory — so the archive's history is the branch **plus** every per-checkpoint reference
in circuitRF's namespace. And a checkpoint is `commit -a`-shaped by design (§7A.1): it captures files
the designer never deliberately committed and may never have opened. **A file that lived only inside
checkpoints is therefore in the archive and absent from a branch-only enumeration** — which is precisely
the population this warning exists for, since a deliberately committed file is one the designer already
knows about. Walking the branch alone would produce a warning that is *nearly* true, and §9A.4 is the
paragraph explaining why that is worse than none: the near-truth is what people act on.

**And one gate that must not be missed, because getting it wrong ships a leak by accident.** The
archive scanner walks the workspace folder. The moment Stage 2 creates a repository there, a `.git`
directory appears inside the folder being walked — so **the archive must be taught to exclude it
explicitly, in the same phase that first creates it.** An archive that carries history because nobody
told the scanner not to is §9A.1's failure arrived at through pure inattention, and it would ship in
the first build that takes a checkpoint. This is a Stage 2 gate, not a Stage 3 feature.

Two further mechanical consequences:

- **Pack before archiving with history included** — **the one deliberate exception to §2.4's rule that
  packing runs where it cannot be noticed** *(stated in rev 4; the two requirements previously did not
  name each other)*. §2.4 keeps packing behind a workspace close because an unasked-for pause is a
  defect; here the user has asked for an archive, the overhang means an unpacked repository can be ten
  times its real size, and §9A.3's own size figure is dishonest without it. It runs in front of the
  user because this is the one time that is the correct place for it — reported as progress, never as a
  stall — and §4.6's rule that packing must yield to another process holding the workspace still
  applies.
- **Referenced cells brought in by the archive scan carry no history** (`workspace-and-project-tree.md`
  R53). They come from another workspace with its own repository, and §7A.1's rule holds in this
  direction too: one workspace, one history. An archive carries files, never someone else's repository.

### 9A.4 The tempting third option, excluded

*"Include only the last N versions"*, or *"include history from this date forward"*, sounds like the
best of both — a useful handover without the old material. **It is history rewriting** (§8.3): it
produces a repository whose identities do not match the original, so the recipient can never compare
against or merge with the sender's, and the pins of §7 do not resolve across it.

Worse, it invites exactly the wrong belief. A user who chose "last 10 versions" to avoid sending
something will assume the something is gone — and a filter that silently kept one referenced object,
or missed a path that was renamed before it was deleted, would be a leak that the user had explicitly
tried to prevent and been told was handled. **A guarantee that is nearly true is worse than no
guarantee**, because the near-truth is what people act on. The two honest states in §9A.2 are all
this offers.

### 9A.5 Extracting an archive that contains history

It arrives as a repository at the workspace root, created by circuitRF, which is the first row of
§12 Q4's table: normal operation, adopted without ceremony. The recipient's own checkpoints continue
on top of the sender's history, which is the entire point of having sent it.

### 9A.6 Save Workspace As is the third way a workspace leaves a machine *(new in rev 3)*

rev 2 considered the archive and the clone. **File ▸ Save Workspace As copies the whole workspace
folder to a new location and switches the window to the copy**, and it filters through the same skip
list the archive scanner uses — so the moment §9A.3's exclusion lands, Save As stops copying the
repository as well.

**That is the right outcome and the wrong silence.** Right, because §9A.1's reasoning is identical here
and the irreversible direction is the same: a copy made for a different customer must not carry the
first customer's deleted files. Wrong, because the designer is not told — and *"I saved a copy and my
history is gone"* is a discovery rather than a decision, where the archive at least asks.

So the copy **says what it did not copy**, in the message that already reports how many files it copied
and how many references it repointed. One sentence: the copy starts a history of its own, and the
original keeps every restore point and every commit. It is true, it is short, and it turns the third
journey into one the designer knows they took.

**A copy is not a fork of the history and must not be presented as one.** Anyone who wants the history
to travel has the archive and §9A's checkbox — a different tool, carrying the warning that belongs to
it.

---

## 10. Explicitly out of scope

Listed so that a later phase does not quietly adopt them:

- **A merge UI.** §6.1 — there is nothing correct for it to do.
- **A branch, in any form.** §6.3 — a restore never checks anything out, so no scenario creates one.
- **Automatic reclaim of what thinning freed.** §5.6a — reclaiming is an explicit, confirmed action,
  never a schedule, and never a side effect of packing.
- **A stash UI.** §6.4.
- **History rewriting.** §8.3 — including the "archive only the last N versions" shape of it, §9A.4.
  **§5.11's corrections are not an exception to this and must not be read as one**: they alter what a
  person wrote about a state and never the state, never an author, and never a shared chain.
- **Deleting a version.** §5.11 — a restore point may be deleted because nothing else can ever have
  seen it; a version is the designer's own record (§5.6 rule 5) and, once shared, is on somebody
  else's disk. What is offered instead is a correction that says so.
- **Erasing a title from a shared history.** §5.11 case (c) — the annotation is shown in place of the
  original and the original is one click away, because a designer whose problem is embarrassment needs
  to know what other people can still read.
- **A Settings row for the history panel's filter.** §5.10 rule 4 — it is per-user view state in the
  `.cwsuser`, and §10A's rows all change what is *kept*.
- **Two panels.** §5.10 — superseded, and listed here so it is not reintroduced as a preference.
- **A "delete all history" command.** §5.7 — removal is deleting one plainly-named folder, not a
  one-click irreversible action inside the application that exists to prevent loss.
- **A commit per file save.** §5.3.
- **A checkpoint on simulation run, or on idle.** §5.3.
- **"Add once, then ignore".** §8.2a.
- **Undo/redo backed by version control.** §5.4.
- **An embedded git library.** §4.1.
- **Committing into any repository other than the open workspace's own.** §7A.1 — this covers an
  enclosing repository (§12 Q4), a referenced workspace (§7A), and a nested one.
- **Version ranges, dependency resolution, or anything else package-manager-shaped.** §7A.5.
- **Anything requiring a server**, including large-file extensions and lock servers. §6.2, §8.3.
- **Storing or prompting for credentials.** §9.1 — circuitRF uses whatever git is already configured
  to use, and refuses rather than asks.
- **Inferring an AI batch from a run of writes.** §5.3a — the agent declares the boundary, or there is
  no checkpoint. An inferred boundary needs a timeout, and a timeout is §5.3's rejected idle trigger.
- **An agent-callable revert.** §5.3a rule 5 — reverting is offered to the designer, not to the thing
  that made the change.
- **A checkpoint as a substitute for crash recovery.** §5.9 — scratch has its own autosave, and the two
  jobs are different shapes.
- **A batch running against a window with unsaved changes.** §5.3c — refused, because a headless batch
  has nobody to answer §5.8's prompt.
- **A filesystem watcher.** §5.3c — the window learns a batch closed over the second-instance channel,
  not by watching the disk. `workspace-and-project-tree.md` §9 defers a watcher and this does not
  un-defer it.
- **A dirty flag in the advisory lock file, or a poll of it.** §5.3c — the notice is a notice; the two
  facts a batch and a window exchange travel on the channel, in both directions.
- **A second implementation of a cross-process lock.** §4.6 — git's own atomic lock files serialise
  what a temporary index does not already keep apart.

---

## 10A. Settings ▸ Revision Control *(new in rev 2)*

A new tab in `SettingsView`, alongside General / Security & Permissions / Color Theme / Wirebonds.
**The tab is shown on every machine** *(owner, 2026-09-07 — §4.3b, which supersedes rev 5's "hidden
when git is unavailable")*. Without a usable git every row below is **disabled**, with one sentence in
their place saying that nothing is being kept and why; the **path field and its Detect stay live**,
because that row is the remedy rather than one of the settings. The path field's former second host on
Security & Permissions is withdrawn with the hiding rule that created it.

| setting | notes |
|---|---|
| **Path to `git`** | blank means "search `PATH`". A Detect action, and the resolved path and version shown once found, so "it says it can't find git" is answerable without a support call. |
| **Commit identity — name and email** | §4.4. **A per-user preference, applying to every workspace this person opens on this machine — written to no git config file, and supplied per invocation** (§4.5). Stored in circuitRF's per-user preference file and read by a `src/Design` reader, so `serve` sees the value this tab captured (§4.4, rev 5). Pre-filled from the user's existing global git identity if they have one, because reading it is unobjectionable and only writing it was ever the problem. Required before the feature arms; the tab says so rather than letting the first checkpoint fail. |
| **Keep a history of my workspaces** | on / off, **a per-user application preference**, and the default every workspace falls back to (§5.7a). **On by default.** Off means circuitRF writes nothing anywhere and deletes nothing anywhere; the control says so in those words, exactly as the per-workspace one does. |
| **Revision control for this workspace** | **outranks the preference above for this workspace only** (§5.7a); on / off, stored **in the `.cws`** because an installation-wide flag cannot gate per-workspace state (§5.7). Off stops circuitRF writing and deletes nothing; the control says so in those words. |
| **Keep restore points for** | the retention age (§5.6). |
| **Always keep at least *N* restore points** | the count floor (§5.6 rule 1), with a hard minimum the field cannot go below. |
| **Take a restore point on workspace close** | on by default (§5.3). |
| **Take a restore point before an AI edit** | shown, **on, and not switchable.** It is §1.2 — the reason the feature exists. Shown rather than hidden so nobody has to wonder whether it is happening. |
| **Pack the repository** | the §2.4 housekeeping threshold and a "do it now" action. Ordinary users never touch this; it exists because §2.4 means someone eventually asks where the disk went. Packing never reclaims. |
| **Reclaim space from thinned restore points** | §5.6a. An explicit, confirmed action with an age — *thinned more than N ago* — and the one destructive control on the tab. It says what it destroys: states already thinned, which after this nobody can bring back. Every live restore point and every commit survives it. Nothing reclaims on a schedule. |

**Two rules bind this tab specifically, both from §1.4:**

1. **Every control that can reduce what is kept states its consequence next to itself**, in the
   settings UI, in a sentence — not in a tooltip, and not only in the manual. "Keep restore points
   for 7 days" must be accompanied by what that means: after seven days, automatic restore points are
   thinned and the states they held are no longer offered. A designer who reads only this tab must
   come away with a correct belief.
2. **Turning revision control off does not delete anything**, and the control says so. An off switch
   that quietly discarded a history would be the single most damaging thing in this document. §5.7
   states the three distinguishable states — on, off, and removed — and why only the first two are
   circuitRF's to offer.

**And one question this tab does not answer, deliberately** *(new in rev 3)*. Security & Permissions is
where circuitRF records what it is permitted to **run** and to **fetch** — the generated-artwork trust,
the external device worker, the updater, and the Verilog-A compiler path, which is on that tab
precisely because it names a program circuitRF may run. Git is both of those things: a program
circuitRF runs, and, in §9, a network operation. **It is nevertheless configured here**, because the
other seven rows are about what is *kept* and a designer looking for them will not look under Security.

What must not follow from that placement is a second consent model. **If running an external program or
reaching a network requires the user's consent in this application, git requires it on the same terms**
— recorded where the other consents are recorded, and visible from the tab that owns them. The
placement decides where a setting is *read*. It is not a statement that this one is exempt.

---

## 10B. What the user must be told *(new in rev 2)*

**This is a requirement of the architecture, not documentation housekeeping.** §1.4 says the
unacceptable failure is a designer who believes a state is recoverable and finds it is not. No
implementation can prevent that; only the explanation can. The user-facing documentation is therefore
part of the feature's definition of done, and a brief that ships the mechanism without it has not
shipped the feature.

Authored under `docs/user/src/` and built by the docs factory (`user-docs-factory.md`).

### 10B.1 The normative table

One table, early, stating what is kept and what is not — the single thing a designer should be able to
find in ten seconds and trust:

| | kept in history? | recoverable? |
|---|---|---|
| schematics, symbols, layouts, technologies | yes | yes |
| workspace configuration (libraries, referenced workspaces, default technology) | yes | yes |
| panel layout, open tabs, tree expansion, **colour theme** | **no** — deliberately, §3.1; they live in the `.cwsuser` | no, and nothing is lost by that |
| simulation results (`.npy`, `.spl`, `.lpcwave`) | **no** — by default, §8.1 | **no.** Re-run the analysis. |
| files excluded by a "never include files like this" choice | **no** | **no**, and they are absent from a clone too |
| anything in a **referenced** workspace | **no** — it is not in this repository, §7A.1 | no; that workspace has its own history |
| anything at all, when an enclosing repository was detected | **no** — §12 Q4 | no |
| anything saved while revision control is switched **off** | **no** — §5.7 | no; but everything recorded *before* it was switched off is still there |
| history, in a workspace **archive** | **no, by default** — §9A | not in the archive; the sender still has it |
| restore points, in a **clone** | **no** — §5.2a | no. The narrative history clones; the safety net does not, and a clone starts one of its own |
| anything at all, in a **Save Workspace As** copy | **no** — §9A.6 | no. The copy begins a fresh history; the original keeps everything |
| anything at all, in a **scratch** workspace | **no** — §5.9 | no. There is no folder yet; save the workspace first |
| anything at all, before a workspace is **armed** | **no** — §5.7a | no. A workspace is armed at its first boundary, and it says so once when it is |
| **panel layout, in a Save Workspace As copy** | n/a — the copy is your own | **yes**, and this is the one thing a copy keeps that an archive does not (§3.1a) |
| a file left out of a restore point because it was unexpectedly large (§8.2b) | **not yet** — the entry says so and names it | no, until the question is answered; the file itself is still on disk |
| a restore point that retention thinned | **no** — but its state is still in the repository until you reclaim it (§5.6a) | **yes**, from the same list, marked as thinned — until a reclaim, which asks first |
| the title you typed on a version you have **not** shared | yes | **yes, and you can correct it** — §5.11 |
| the title you typed on a version you **have** shared | yes | **no.** You can add a correction, which travels with it; the original stays in the file and can still be read |
| the label on a restore point | yes | **yes** — rename it, or delete the whole entry. It never left this machine (§5.2a) |
| the state you were in when you went back | **yes**, as a restore point taken before the restore | **yes**, and the panel offers it to you by name the moment you arrive (§5.8) |

Wherever a row says no, the docs say **no** plainly. Softening any of them is how a designer ends up
with a false belief.

### 10B.2 Scenarios

Prose alone does not produce a correct mental model; a walkthrough does. Each of these is a short
scenario with a concrete starting state, the actions taken, and — the part that matters — **what is
and is not available afterwards.** The last three are the ones people get wrong:

1. **"I broke the match network yesterday and I want it back."** The ordinary case, start to finish.
2. **"I want to keep this version — it's the one I'm sending out."** An explicit commit, its title,
   and where it shows up afterwards.
3. **"I went back to an old version, then kept working."** §6.3 and §5.8 — that the restore is one
   step in one line of work, that the moment before the restore is itself a restore point, and that
   the next commit says what it was restored from. There is no second line of work to explain, and the
   words branch and checkout do not appear because nothing they would name exists.
4. **"Two of us are editing the same workspace."** Whole-file, pick-a-side (§6.1), and why circuitRF
   will not merge them.
5. **"I use a library another team maintains."** §7A — read-only, the pin, what "a newer version is
   available" means, and what happens to the pin when they restore an old state.
6. **"My results are gone."** Not a bug (§8.1). Why results are not versioned, and that the design
   that produced them is.
7. **"I turned the retention setting down and now I can't get back to last month."** The scenario
   this section exists for. It must appear in the docs *before* a user lives it.
8. **"My workspace is inside another git repository."** Why circuitRF stopped (§12 Q4), what is
   therefore not being kept, and how to fix it.
9. **"I want to turn this off"** — and the two things a user actually needs to know: switching it off
   keeps everything already recorded (§5.7), and getting rid of it entirely means deleting one folder,
   which **cannot harm the design**. The second half is the reassurance; it belongs in the docs
   because the people who most need it are the ones least likely to ask.
10. **"I need to fix a cell in a library I reference."** §7A.3 — open the library as a workspace,
   edit it there, and the two consequences that surprise people: the change lands in the *library's*
   history rather than the design's, and a pinned reference deliberately does not move until asked.
11. **"I'm sending this workspace to a customer."** §9A — what the archive contains, what including
   history would additionally contain, and the sentence that matters: **a file deleted from the
   workspace is still in the history.** This is the one scenario in this list where getting it wrong
   cannot be undone.
12. **"I saved a copy of my workspace and the history didn't come with it."** §9A.6 — what a copy is,
   why it starts fresh, and that the archive is the tool for the other intention. Short, and it exists
   because this is the one journey of the three that gives the user no dialog to read.
13. **"circuitRF says it started keeping a history of this workspace."** §5.7a — what was just created,
   where the setting is, and the reassurance that removing it later cannot harm the design. It exists
   because this is the first thing most users will ever see of this feature, and it arrives unasked.
14. **"This workspace already had version control and circuitRF asked me something."** §12 Q4
   *Refinement 3* — the three answers in plain words, and what keeping their own settings actually
   costs. The two consequences that matter are recoverability ones, not tidiness ones, and the chapter
   says so rather than listing configuration keys.
15. **"My disk is full and it says the history is taking the space."** §5.6a — that thinning keeps the
   states, that reclaiming is the one action that does not, and exactly what it destroys.
16. **"I typed the title wrong."** §5.11 — that a restore point's label and an unshared version's
   title are yours to correct, that a shared one takes a correction instead, and the one sentence that
   must not be softened: the original wording stays in the file. It exists because a designer who
   believes a careless line is permanent will write a useless one instead.
17. **"I went back and I want to come forward again."** §5.8 and §5.10 — that the state before the
   restore was kept, that it is in the same list as everything else, and that coming forward is the
   same action as going back rather than a different and scarier one. **Scenario 3 already covers
   restore-then-keep-working; this is the twenty seconds before that**, and it is the moment a
   designer is most likely to believe they have lost the day.

### 10B.3 AI checkpoints are documented separately

In their own section or chapter, **not interleaved with the ordinary history material.** Two reasons,
and the second is the operative one:

- They have a different audience. Someone who never uses AI features should be able to read and fully
  understand the history documentation without encountering them.
- **They mean something different and must not be confused with commits the designer made.** A restore
  point taken before an agent edit is a safety device the user did not ask for; a commit is a
  statement the user made. Presenting them together invites the belief that one substitutes for the
  other — which is exactly the false-confidence failure of §1.4, arrived at from the other direction.

The AI section states plainly: what triggers a checkpoint, that they are automatic, that they are
subject to retention (§5.6) while explicit commits are not, and how to mark one **kept** (§5.6 rule
6), which is what makes it permanent.

**It also states what happens when the floor is not there** *(new in rev 3)*. §5.3a refuses a batch
against a workspace that cannot be checkpointed — off, held, or scratch — and §5.3b rule 9 forbids the
agent from improvising a substitute. A designer needs to know both halves: that they will be stopped
and told, and that being stopped is the feature working rather than failing.

### 10B.4 Developer-facing

This document, plus a `RESOLVED.md` in whichever source directory the implementation lands in,
recording what the briefs actually hit. §2.4 is already an example of the class: a measurement taken
one way (after `gc`) gave a materially wrong picture of the cost, and the next person needs to know
that before they re-measure.

---

## 11. Staging

Ordered so that the risky, user-facing half comes last and everything before it has standalone value.

**Stage 1 — the substrate.** The generated `.gitignore` and `.gitattributes` — including §4.5's
end-of-line pinning — the large-file guard (§8.2), the workspace-file split (§3.1), the never-gzip rule
(§3.2), the packing scheduler (§2.4), **the repository configuration and invocation environment of
§4.5**, the macOS shim rule of §4.3a, the version floor and `safe.directory` handling of §4.7, the
reclaim operation §5.6a's action calls (provided, not exposed), §8.1a's rule that the policy files
follow a *repository* rather than only a creation, and the **read-only default on referenced workspaces
(§7A.2)**. **No user-facing revision-control UI at
all.** Every later stage depends on this. Two parts of it are worth doing on their own merits and,
critically, **neither depends on git being installed**: the workspace-file split, and read-only
referenced workspaces. The second closes a silent-divergence hole that exists in the shipped
application today (§7A.2) and must reach the majority of users who will never have git — otherwise
the only people protected from it are the ones who already have a history to recover from.

**Stage 2 — the safety net.** Automatic checkpoints on the three boundaries of §5.3, on the
per-checkpoint references of §5.2a and in the shape of §5.2b, surfaced as restore points with §5.6 rule
6's kept mark; the `history` verb of §5.3d; §8.2b's rule for the unattended boundary; the batch protocol of §5.3a and the
agent-facing contract of §5.3b; restore and its preconditions (§5.8); scratch's refusal (§5.9);
cross-process serialisation (§4.6); the first-commit summary of §8.1a; retention per §5.6; the Settings
tab (§10A); the identity prerequisite (§4.4); the enclosing-repository hold and its indicator (§12 Q4);
and — mandatorily, because this is the phase that first puts a `.git` inside the folder the archive
scanner walks — **the archive's explicit exclusion of it** (§9A.3), **together with §9A.6's sentence on
Save Workspace As**, which the same exclusion silently changes. Still no git vocabulary. **This is the stage that discharges §1.2**, and per §12 Q5 it **ships before AI design
capability exists** — the floor has to be under the feature before the feature arrives, not beside it.
Its standalone value against human error is what carries it until then.

**Two things Stage 2 gains in rev 4 and neither is optional:** §5.7a's arming — the preference, the
first-boundary rule and the announcement — since Stage 2 is the first stage that creates a repository at
all; and §5.3c's batch-versus-window rules, which belong with the batch protocol that makes them
necessary. §12 Q4's ask (Refinement 3) is Stage 2's too, for the same reason: it is the first stage with
a repository to have a question about. **And rev 5 adds §5.6a's reclaim action to the Settings tab**,
because Stage 2 is the first stage that thins anything.

**Stage 3 — the narrative.** A commit action and a history browser, hidden entirely when git is
absent (§4.3), with the error-translation layer of §4.2 and the commit wording and Messages reporting
of §5.5's first row. §6.3 needs no handling at all: a restore followed by a commit is linear.

**Stage 3 also gains the archive's include-history option** (§9A) — the exclusion is Stage 2's
safety gate, the *choice* needs a history worth offering and the vocabulary to describe it.

**Stage 4 — sharing. BUILT 2026-09-07** (`brief-revision-control-9-clone-and-pins.md`).
Clone-as-workspace (§9), fetch/push with §9.1's credential posture, and the commit-pinned references of
§7 — which is what turns §7A's read-only libraries from a restriction into a managed collection.

**Stage 5 — the one panel, and the designer's own words.** *(new in rev 6.)* The merge of §5.10, its
filter, its search, the row and expander it specifies, §5.8's way forward and §5.6 rule 6's fourth kept
kind; then §5.11's corrections and §9's review before sending. **It comes last for the reason every
other stage came in the order it did**: it changes nothing about what is stored, so it cannot be the
thing that loses anything, and it can only be designed against panels that exist. Stage 5 is two
briefs, RC-10 and RC-11, and the order between them is not arbitrary — RC-10 is display over data that
is already there, RC-11 is the first thing in this document that writes to a reference a designer
already read.

**The user documentation of §10B is not a stage.** Each stage ships the part of it that stage makes
true. A stage that lands mechanism without explanation has not landed. **Stage 5 merges two chapters
into one** for the same reason it merges two panels, and the merge is part of the stage rather than
after it.

---

## 12. Owner decisions — taken

**Q1. The workspace-file split (§3.1). DECIDED: split.** Per-user view state moves to a sibling
excluded by `.gitignore`. rev 2's measurement found the split is larger than rev 1 described —
`TreeViewState`, `OpenDocuments` and `ActiveDocumentPath` move too, and together with `DockLayout`
they are ~96% of a real `.cws`. **Extension: `.cwsuser`**, and **double-clicking either `.cws` or
`.cwsuser` opens the whole workspace** — §3.1a. The name is self-describing where a near-miss on an
existing extension would not have been (§1.4), and the previously-considered `.crfw` is left untouched:
it is inert, and retiring it is a packaging question, not a file-format one.

**Q2. Checkpoint boundaries in Stage 2 (§5.3). DECIDED: three** — before an AI batch (mandatory), an
explicit user save-point, and workspace close. Simulation run and idle timeout are **excluded**, with
reasons recorded in §5.3 so they are not revisited casually. A Messages entry naming the commit
identity accompanies an explicit commit (§5.5).

**Q3. Checkpoint lifetime (§5.6). DECIDED: a user preference**, in the new Settings ▸ Revision Control
tab (§10A). The clock-change hazard the owner raised is answered by §5.6: a count floor that age
cannot override, ordering from circuitRF's own monotonic sequence rather than the wall clock, a bounded
sweep that refuses and reports rather than mass-deletes, thinning that never prunes to permanence, and
human-written commits exempt entirely. **A clock set a century forward costs the user nothing and
produces a message.**

**Q4. An existing non-circuitRF repository (§7A.1). DECIDED: hold, visibly.** Revision control is put
on hold, nothing is committed, and the user is told. The mechanism, and the two refinements offered in
answer to the owner's question about message cadence:

*What counts.* `git rev-parse --show-toplevel` from the workspace folder:

| situation | behaviour |
|---|---|
| a repository circuitRF created at the workspace root | normal operation |
| a repository the **user** created at the workspace root | **hold, and ask** — see *Refinement 3*, rev 4. Their intent is unambiguous and the root is exactly right; taking it over without asking is presumptuous, but refusing to offer is unhelpful. |
| the repository root is an **ancestor** of the workspace | **hold**, no adoption offered. This is the serious case: an automatic `commit -a` here would sweep up unrelated work in progress under a circuitRF message (§7A.1). Nor may circuitRF write `.gitignore`/`.gitattributes` into someone else's repository root. |
| a repository **nested inside** the workspace | that subtree is excluded and reported; the workspace's own history is otherwise normal (§7A.5). |

**`rev-parse` walks up, never down, so the nested row needs a detection of its own** *(new in rev 5,
§12 Q30)*. The three rows above it are one `rev-parse`; the fourth is a walk of the workspace tree for
directories named `.git`, done at the moment the checkpoint enumerates files anyway (§5.2b), so it costs
nothing extra. It matters mechanically as well as by principle: handing a directory that contains a
`.git` to `git add` records it as an *embedded repository* — a pointer to that repository's current
commit — which is precisely §7A.5's *"committed as something by the enclosing workspace"*, with a
warning nobody is reading. The subtree is excluded from every checkpoint by pathspec and named once in
the Messages panel.

*Refinement 1 — "held" must not look like "absent."* §4.3 hides the affordances when git is missing.
Doing the same here would be wrong: absent is harmless, held is a designer who may believe they are
protected. So in the hold state the commit action stays **visible and refuses**, saying why — because a
hidden button is indistinguishable from a feature that was never there.

*Refinement 2 — a persistent indicator, not repetition.* The owner asked for a message on every commit
attempt and on workspace open. Agreed on both, with one addition and one subtraction:

- **On workspace open:** a message stating that circuitRF is not keeping history for this workspace,
  why, and **how to remedy it** — as the owner specified.
- **On an attempted commit:** a refusal saying why, without restating the remedy — as the owner
  specified.
- **Added: a persistent, non-scrolling indicator** (status bar, or a badge on the project tree root)
  reading that history is off for this workspace. A scrolling log is read once and then trained
  against; the state is permanent for the session and should be displayed permanently. This is the
  measure most likely to actually prevent the false belief.
- **Subtracted: automatic checkpoints do not each post a message.** A skipped AI-batch checkpoint
  posts **once per session** — enough to be unmissable, not so often that the panel becomes noise the
  designer learns to scroll past. §4.4's failure reporting follows the same rule.

*Refinement 3 — the workspace-root case is a question, not an offer* **(new in rev 4, owner's
decision, §12 Q14).** rev 3 offered adoption as a one-click action and said nothing about what the user
was choosing between. **The user is asked, told what keeping their own configuration costs, and
encouraged to adopt circuitRF's.** Three answers, not two:

| answer | what happens |
|---|---|
| **Adopt circuitRF's settings** *(recommended, and said to be)* | §4.5's repository configuration and §8.1a's policy files are written; normal operation from then on |
| **Keep my settings** | circuitRF keeps history in the repository under the user's own configuration. A legitimate choice, and the consequences below are stated at the point of choosing |
| **Don't keep history for this workspace** | the hold state, unchanged — which is rev 3's behaviour and stays available |

**The consequences of keeping are specific, not general, because §4.5's rows each exist to prevent one
named failure** — and the first two are the ones that matter, because they silently revoke guarantees
this document makes elsewhere:

- **`gc.pruneExpire` at its two-week default destroys thinned checkpoints permanently after a
  fortnight** — so §5.6 rule 4's grace period does not exist, inside §1.3's own recovery window.
- **`gc.reflogExpireUnreachable` at 30 days removes the way back from a mistaken reset or amend on
  the designer's own branch** before §1.3 says the designer looks for it — and, with `gc.pruneExpire`
  above, the journal of §5.6 rule 4 points at objects that are no longer there.
- `gc.auto` at 6,700 leaves two packing schedules running against each other (§2.4).
- On Windows, `core.autocrlf` makes the `.clay` on disk a different file from the one
  `LayoutPersistence` wrote, and `core.longpaths` unset is a refusal on that machine class only.

**One line must be drawn or "keep my settings" will be read as "do everything their way":** the answer
governs **repository configuration only.** §4.5's per-invocation set is circuitRF's in every case — the
commit identity, and the hook bypass. Bypassing hooks is not a setting the user can keep, because it is
not a property of the repository at all; it is §7A.1's rule that an automatic checkpoint must not fire
somebody's tooling or be blocked by it, and it holds whichever answer is given.

**The ancestor row is unaffected and is not asked about.** circuitRF may not write into someone else's
repository root at all, so there is nothing to offer.

**And this is where §4.5's management marker is written**, whichever answer is given — which is what
makes the question asked once rather than on every open, and what lets §5.3b advertise a state it can
actually determine.

**Q5. Does Stage 2 ship before AI design capability? DECIDED: yes, before.** Reflected in §11.

**Q6 (raised in review). Does a workspace archive carry the history? DECIDED: a checkbox, off by
default** — the owner's proposal, adopted, and §9A records why the default is a safety property
rather than a preference. Every other failure in this document is a recoverable loss; shipping history
to the wrong recipient is **disclosure, and it cannot be undone** — history holds every earlier version
of every tracked file, including files deleted from the workspace long ago and invisible in its file
tree. Two honest states only (§9A.2); the "last N versions" middle option is history rewriting and is
excluded (§9A.4). When the box is ticked the dialog **names the files that are in the history but not
in the workspace** (§9A.3), which is the warning most likely to actually stop a mistake. And the
exclusion itself is a **Stage 2 gate**, not a Stage 3 feature: the phase that first creates a `.git`
inside the folder the archive scanner walks is the phase that must teach it to skip one.

**Q7 (raised in the brief review). Where do checkpoints live, and do they travel? DECIDED: one
reference per checkpoint, in circuitRF's own namespace, local only.** §5.2a. The per-checkpoint shape
is not a preference — a single reference walking a chain **cannot express §5.6's thinning at all**,
since a chain can only be truncated and truncation leaves everything reachable. Local-only follows from
what a checkpoint *is*: ordered by a monotonic sequence that means nothing on another machine, thinned
by this machine's retention preference. **The consequence to state rather than discover is that the
three journeys disagree** — an archive carries checkpoints, a clone does not, a Save As carries no
history at all — and §10B.1 now has a row for each. **rev 5 adds the commit's shape (Q18)**: with a
parent chain between checkpoints, the per-reference shape thins nothing either.

**Q8. What triggers the AI checkpoint? DECIDED: the agent declares the batch, explicitly, with its
intent.** §5.3a. rev 2 named the most important checkpoint in the document and gave it no mechanism;
§12 Q5 says this ships *before* the capability, so it could not be discovered later. Inference is
excluded because inferring a batch needs a timeout and a timeout is §5.3's rejected idle trigger.
**§5.3b is the other half and is new in rev 3 on the owner's instruction:** the `serve` surface
advertises the revision-control state and publishes the rules an agent follows when it reaches for git
itself. Rules 3 and 9 — *do not commit* and *if the batch cannot be opened, stop and say so* — are the
load-bearing pair, because the failure they prevent is the agent being **helpful**: improvising a
backup nobody can restore from, and reporting success.

**Q9. What does a restore do to an open workspace? DECIDED: checkpoint first, then prompt, then
reload.** §5.8. A restore writes over every file while the workspace is open. It takes a checkpoint of
the state it is about to replace — otherwise it is an automatic operation destroying history, which
§1.4 forbids — offers up unsaved work through the prompt that already guards a close, and **discards
the undo stacks of reloaded documents**, because an undo after a restore would produce a document that
existed at no moment ever. **rev 5 adds five rules (Q25)**: it never moves `HEAD`, it removes files
created after the checkpoint, it leaves ignored files alone, it preserves the off flag and the policy
files, and it leaves a marker while it runs.

**Q10. What does circuitRF configure, and what does it run git with? DECIDED: §4.5's split, and it
corrects rev 2 twice.** The dividing line is **what a setting is a property of**: the repository's
config carries properties of the *repository* — packing and how files reach disk — and anything that is
a property of the *person*, or of circuitRF's own behaviour, is supplied per invocation and written to
no config file.

`gc.pruneExpire=never` is what makes §5.6 rule 4 true, and the two reflog expiries guard the
designer's own branch — rev 5 corrected which was which (correction one);
`core.longpaths` and the end-of-line pinning are platform failures appearing on one machine class only.
Hook-free commits and signing off keep the user's own setup from breaking an automatic checkpoint, and
both are **per invocation**, because they are statements about circuitRF's commits rather than about
the repository — a designer's own commits from a shell in that folder should still behave as they
configured them.

**Correction two is the commit identity** (§4.4), raised in review. rev 2 reasoned correctly that
circuitRF must not write `user.name` into the user's global config, and then wrote it into the
**repository's** config — which makes a person's identity a property of a **directory**. On a network
share, which §4.7 and §7A both assume, the second designer to open the workspace commits under the
first one's name, silently. **It is §3.1's mistake in another file**, and it takes §3.1's fix: identity
is a per-user circuitRF preference, supplied per invocation, in no config file at all. Where circuitRF
has none, git's ordinary resolution applies. **rev 5 corrects where the preference lives (Q21)**: a
`src/Design` reader of the per-user file, so `src/Cli` sees the value the tab captured rather than
falling through to a resolution that names nobody on the machines §4.4 describes. `GIT_TERMINAL_PROMPT=0` is the one that prevents a **hang**, which is the worst
failure mode available because it is the only one with no message. §4.6 adds one writer at a time
across processes; §4.7 adds a version floor and `safe.directory`, which is the ordinary state of a
workspace on a network share.

**Q11. What happens to workspaces that already exist? DECIDED: policy files follow the repository, not
the creation — and the first commit summarises.** §8.1a. Under rev 2 as written, every workspace made
before this feature would have tracked results forever, and those are the workspaces with years of
output in them. The first commit into one is a different operation from every later one: §8.2's
one-file-at-a-time guard becomes a dialog naming hundreds of files, so the first commit groups by
pattern. And circuitRF **adds lines to `.gitignore` and never rewrites it** — a regenerated policy file
is one whose user edits vanish silently.

**Q12. Does Save Workspace As carry the history? DECIDED: no, and it says so.** §9A.6. It shares the
archive scanner's skip list, so §9A.3's exclusion silently changes it too. The outcome is right —
§9A.1's irreversible direction is the same one — but the silence is not, so the existing copy report
gains one sentence.

**Q13. What arms a workspace, and what is the default? DECIDED: a per-user application preference,
on by default, outranked per workspace.** §5.7a. rev 3 used "armed" throughout and never said what did
the arming — §1.2's whole floor resting on an unstated value. The preference answers *"do I want this at
all"* and is the default a workspace with no recorded setting falls back to; §5.7's per-workspace flag
answers *"not for this one"* and still may not be an installation-wide flag, for the reason §5.7 gives.
**Two guards keep "on by default" from being a surprise:** a workspace is armed at the first boundary
that would record something rather than at open — so looking at a colleague's workspace on a share
creates nothing — and the first time a workspace gains a repository it is **announced**, once, with the
setting's location and §5.7's reassurance that removing it later cannot harm the design.

**Q14. What does circuitRF do with a repository the user created at the workspace root? DECIDED: ask,
state the cost of keeping, and encourage adopting.** §12 Q4 *Refinement 3*. rev 3 offered adoption as a
one-click action without saying what the alternative cost. Three answers — adopt, keep, or don't keep
history here — with the two load-bearing consequences of keeping named specifically, because they
silently revoke guarantees made elsewhere in this document: `gc.pruneExpire`'s two-week default destroys
thinned checkpoints inside §1.3's own recovery window, and `gc.reflogExpireUnreachable`'s thirty days
removes the way back on the designer's own branch before anyone looks for it. **"Keep my settings" governs repository
configuration only** — §4.5's per-invocation set, the identity and the hook bypass, is circuitRF's
whichever answer is given, because bypassing a hook is not a property of the repository.

**Q15. What happens to the open window while an out-of-process batch edits its files? DECIDED: refuse
while dirty, reload and discard undo stacks on close, and observe rather than discover.** §5.3c. §5.8
had already worked this out for a restore and §5.3b rule 5 forbids the agent from reaching the same
state through git; the agent's own edits were the identical situation with no rule. A refusal is the
only honest answer to the dirty case because a batch is headless and there is nobody to answer §5.8's
prompt — and it makes the reload cheap, since with no unsaved work a reload cannot lose anything.

**Q16. When does a retention sweep run? DECIDED: at most once per session, on workspace close, after
the close checkpoint.** §5.6. Rule 3's bound — *"no single pass may remove more than a small
fraction"* — guarantees nothing until the passes are counted: a sweep per checkpoint at one tenth
empties the set inside twenty checkpoints. Close is the boundary §5.3 already identifies as reliably
present, it is behind the user, and it composes with §2.4's packing, which stores what thinning made
unreachable compactly until §5.6a's explicit reclaim, if that ever comes.

**Q17. Three smaller things rev 3 left implicit, each silent when missed.** The **off transition is
ordered** — write the `.cws`, take one final checkpoint, then stop — or §5.7's "turning it off is itself
a recorded change" is aspirational and the gap has only one end (§5.7). The **deleted-file warning walks
the checkpoint references as well as the branch** (§9A.3), because §5.2a makes the archive carry them
and a `commit -a`-shaped checkpoint holds files the designer never deliberately committed — a
branch-only enumeration produces exactly the nearly-true guarantee §9A.4 rejects. And the **`.cwsuser`
is excluded from an archive but kept in a Save Workspace As copy** (§3.1a) — one shared skip list, two
consumers, opposite correct answers, which is the reverse of `.git` and has to be deliberate in the code
or it gets tidied away.

**Q18. What is a checkpoint commit, mechanically? DECIDED: parentless, built through a temporary
index, capturing every non-ignored file including new ones, with its metadata in trailers and its
sequence derived from the references that exist.** §5.2b. Parentless is the load-bearing half: §5.2a's
per-reference shape thins nothing if each checkpoint names the previous one as its parent, and no gate
that asserts survival would notice. The temporary index is what keeps `HEAD` and the designer's own
staged state untouched, which is what Q19 and Q22 rest on.

**Q19. Does restore-then-edit create a branch? DECIDED: no, and no branch is ever created.** §6.3,
§5.8. rev 4's "git requires a branch there" is true only of a checkout, and a restore is a working-tree
write that never moves `HEAD`. The following commit records the restored content as the next step in
one line of work and says what it was restored from (§5.5). The variant, its branch, its naming and
§10B.2 scenario 3's "two lines of work" are withdrawn. *(A rev 5 judgement taken on the review's
recommendation; reversible before RC-5 lands and not after.)*

**Q20. What survives a sweep, and what ever reclaims it? DECIDED: the unreachable objects survive
because §4.5 forbids pruning them, recovery is through a journal circuitRF keeps rather than the
reflog, and reclaim is an explicit, confirmed action — never automatic.** §5.6 rule 4, §5.6a. rev 3's
reflog claim was wrong for references outside `refs/heads/`, and rev 3's "packing eventually reclaims"
contradicted its own `gc.pruneExpire = never`. §1.4 settles the contradiction in favour of never, and
the Settings tab gains the one destructive control, which destroys only what is already thinned.

**Q21. Where does the commit identity live so that `serve` can read it? DECIDED: in circuitRF's
per-user preference file, behind a `src/Design` reader both processes use.** §4.4. Falling through to
git's own resolution names nobody on the fresh Windows machine §4.4 itself describes, which refused the
governing motive's checkpoint for exactly its target population.

**Q22. Who serialises two processes on one repository? DECIDED: git's own atomic lock files, behind a
temporary index, with a short retry — not the advisory lock.** §4.6. `WorkspaceLock`'s header says it
is a notice and must not be treated as authoritative; it is what packing yields to, and nothing else.

**Q23. What does discovery do on macOS? DECIDED: check for the developer tools before invoking
anything named `git`, and treat the shim without them as absent.** §4.3a. The shim opens an Apple
dialog when run, which is §4.3's forbidden surprise delivered by the detection itself.

**Q24. What does "would record something" mean before a repository exists, and who does housekeeping
on a shared workspace? DECIDED: an unarmed workspace arms on close only if circuitRF wrote a file this
session, and always on a save-point or a batch; an armed one applies the tree test; a session that
recorded nothing sweeps nothing and packs nothing.** §5.7a, §5.6, §5.3. The colleague's glance creates
nothing, runs nothing and writes nothing.

**Q25. What else does a restore do? DECIDED: it never moves `HEAD`, removes files created after the
checkpoint, leaves ignored files alone, preserves the off flag and the policy files, and leaves a
marker so an interrupted restore is reported on the next open.** §5.8.

**Q26. What happens to an unexpectedly large file at a boundary nobody is at? DECIDED: the checkpoint
proceeds, the file is left out, the entry says so and names it, and the question is asked at the next
interactive moment.** §8.2b. §9A.1's rule chooses the reversible direction: leaving out can be undone
next time, including cannot. *(A rev 5 judgement.)*

**Q27. Which checkpoints may retention never thin? DECIDED: explicit save-points, the pair that
brackets an off period, and any the designer marks kept.** §5.6 rule 6. The third is §10B.3's "make it
permanent", available in Stage 2.

**Q28. How do `serve` and the window exchange the dirty state and the batch-closed notice? DECIDED:
over the existing second-instance channel, brought up on macOS for the purpose — not the lock file,
not a watcher, not a timer.** §5.3c.

**Q29. Is there a headless spelling for history operations? DECIDED: one verb, `history`, with the
nouns `checkpoint`, `list`, `restore` and (Stage 3) `commit`, each calling the function the GUI calls.**
§5.3d. The batch's open and close stay on `serve`, which is the only surface that can hold them.

**Q30. Two smaller things rev 4 left implicit.** §5.5 has four rows, not three — the explicit
save-point is a commit with a message, and the brief that creates each checkpoint owns its wording.
And a nested repository is found by a walk for `.git` directories, not by `rev-parse`, and excluded by
pathspec, because `git add` would otherwise record it as an embedded repository (§12 Q4).

**Q31. Is Settings ▸ Revision Control hidden on a machine with no git? DECIDED: no — shown, with its
rows disabled and the git path still live.** §4.3b, §10A *(owner, 2026-09-07)*. §4.3's silence was
written against the designer who does not want this feature; it also silenced the one who does, and
concealed a remedy that costs five minutes. The tab is where somebody goes to ask *does git work on
this machine*, so the row that answers that question is the one row that may never be greyed. Hiding
remains right for every other affordance, and this is deliberately not §12 Q4's hold: held means
circuitRF may not write to a repository that exists, and a designer may believe they are protected —
here nothing has been promised, and the tab says so.

**Q32. Two panels or one? DECIDED: one, named History, with the two kinds of row visibly distinct and
a filter whose default hides the automatic entries.** §5.10 *(owner UX review, 2026-09-07)*. §5's
argument is about kinds of entry and was spent as an argument for windows; the reason actually given
for the split is unreadability, which a filter answers better. **The close checkpoint keeps being
taken** — §5.3's argument for it is about the designer who never thought about history, and quietening
a list is a display decision that must not become a capture decision. Supersedes RC-7 R-rc7-9.

**Q33. May a designer correct what they wrote? DECIDED: yes — a restore point's label and an unshared
version's title are correctable by their author, a shared version's title takes an annotation, and no
content, author or shared chain is ever altered.** §5.11, narrowing §8.3. The blanket rule was git's,
and git's reason — every clone is invalidated — does not reach the one-designer, no-clone workspace
that is the common case. **RC-7 gate 11's source scan narrows rather than lifts**: `rebase`,
`filter-branch`, `filter-repo`, `replace` and `reflog` stay forbidden everywhere, and `--amend` is
permitted in exactly one named file, asserted by name so the exemption cannot spread — the same shape
as R-rc7-4's exemption for the commit identity.

**Q34. May retention thin the pre-restore checkpoint? DECIDED: no — §5.6 rule 6 gains a fourth kept
kind.** §5.6, §5.8. It is the entry that makes the one-way-door promise true, and rev 5's list omitted
it; the built `IsAlwaysKept` shows the omission rather than a disagreement.

**Q35. Where is the way forward offered after a restore? DECIDED: in the history panel, on arrival, by
name.** §5.8, §5.10. It was findable in the other panel, which is the two-panel split producing a
safety failure rather than clutter.

**Q36. Does the history leaving this machine get a review step? DECIDED: yes — send, clone and archive
list the version titles that are about to leave.** §5.11, §9, §9A.3. It is worth more than every
correction mechanism above, because the expensive case is not a careless word but a customer's name
going to a different customer, and §9A.3 already establishes that a computation the user cannot perform
belongs in front of the operation.

**Still open from rev 5, and it is not small.** Two writers with different retention preferences on
one shared workspace apply whichever closed last, bounded by rule 1's floor (§5.6). Whether the
retention age and floor should become per-workspace state in the `.cws` — so the workspace's owner
decides how long its history is kept, rather than whoever closed it — is worth deciding before RC-6
lands, and not before, since RC-6 is where the preference is first consumed.

**Closed, 2026-09-06 (owner).** §3.1's split had left `ColorSchemeName` on the versioned side, on the
grounds that it is a property of the project — the one field in §3.1's table whose side was assigned
rather than measured. **It is per-USER and lives in the `.cwsuser`.** The argument ran both ways (a
house style is a real thing) and was settled on the other one: a theme is one person's preference, and
a shared workspace imposing its author's theme on everyone who opens it is the more common annoyance.
§3.1 carries the decision and what it costs.

---

## Appendix A — the measurement harness

rev 1's figures came from a synthetic file matching `LayoutPersistence`'s output shape. **rev 2's come
from a real board** — a 28,418,662-byte `.clay`, 1,570,212 lines, 3,284 shapes, 673,345 vertices, from
a working workspace — which is what rev 1 §2.3 required before any brief was written against the
numbers. That requirement is discharged.

The procedure, which is worth repeating on the machine class a brief targets:

1. Commit the file into a fresh repository; `git gc`; record `.git` size.
2. Apply realistic edits one commit at a time, recording after each group: **append** shapes,
   **drag** a shape (rewrite every coordinate of one mid-file shape), **delete** a mid-file shape,
   and finally **reorder every shape**. Edits are applied as line-level text edits on the writer's own
   output, so the result is byte-identical to what `LayoutPersistence` would have written for the same
   design.
3. Repeat with the same file gzipped, to reproduce §3.2. **The mid-file drag is the case that
   matters** — an append-only test reports a false pass, and §3.2's real numbers show why (3× for
   appends, ~508× for drags).
4. **Then run the whole sequence again with no `gc` at all**, and record the loose-object size. This
   step did not exist in rev 1 and is the one that found §2.4 — the packing overhang that git's own
   count-based `gc.auto` will never clear on files this size.

Step 4 is the general lesson: **measuring a repository only in its tidied state hides the state it is
actually in most of the time.**

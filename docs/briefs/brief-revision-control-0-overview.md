# Sonnet Brief — Revision control: overview, findings and decisions

**Read this first; it is the map for `brief-revision-control-1-workspace-file-split.md` through
`-11-correcting-what-you-wrote.md`. It contains no work of its own.** It records what the tree says
today, fixes the decisions the implementation briefs depend on, and maps the architecture's staging
onto them.

The architecture is `docs/design/revision-control.md` (**rev 6**, owner decisions Q1–Q36 taken).
**This brief does not restate it.** rev 3 added nine sections after these briefs were first written —
§4.5 (repository configuration and invocation environment), §4.6 (two processes, one repository), §4.7
(the version floor and `safe.directory`), §5.2a (where checkpoints live and which journeys they
survive), §5.3a (what an AI batch is), §5.3b (the agent-facing contract), §5.8 (restoring), §5.9
(scratch), §8.1a (existing workspaces and the first commit), §9.1 (credentials) and §9A.6 (Save
Workspace As) — and corrected one rev 2 claim outright (§2.4: plain `git gc` **does** prune). The
briefs below are updated for all of it; where a brief and rev 3 disagree, rev 3 is right.

**rev 4 added five sections and revised one, after a second review of these briefs against it.** The
coverage was found complete — nothing rev 3 specified was missing from a brief — so rev 4 adds what rev 3
**did not ask**: §5.3c (what an out-of-process batch does to the window holding the workspace), §5.7a
(what arms a workspace, and the default), §5.6's sweep trigger, §5.7's off-transition ordering, §9A.3's
enumeration scope, §4.5's management marker, and §12 Q4 *Refinement 3* (the workspace-root repository is
a question the user is asked, not an action offered). §3 and §5 below assign all of it.

**rev 5 corrected five mechanisms and one premise, after a third review.** Each had been named as if
settled and did not do what the sentence around it said: a checkpoint's **parent** (§5.2b — parentless,
or thinning frees nothing), the **reflog** as the way back after a sweep (§5.6 rule 4 — a deleted
reference takes its reflog with it; circuitRF keeps a journal instead), **reclaim** (§5.6a — never
automatic, an explicit action, because §4.5's `never` and §5.6's "eventually reclaims" could not both be
true), the **advisory lock** as a serialiser (§4.6 — its own header says it is a notice; git's own locks
and a temporary index do the job), and the **identity** `serve` could not read (§4.4 — a `src/Design`
reader of the per-user file). The premise was §6.3's *"git requires a branch"* after a restore: it does
not, a restore never moves `HEAD`, and **variants are withdrawn** (§12 Q19). rev 5 also settled the
macOS git shim (§4.3a), what "would record something" means before a repository exists (§5.7a), five
restore rules (§5.8), the unattended large-file boundary (§8.2b), the kept mark (§5.6 rule 6), the
channel between `serve` and the window (§5.3c), the `history` verb (§5.3d), §5.5's fourth row, and
nested-repository detection (§12 Q4). §3 and §5 below carry all of it. **Where a brief and rev 5
disagree, rev 5 is right.**

**rev 6 is the first revision driven by the shipped panels rather than by re-reading the document**
(owner UX review, 2026-09-07), and it adds **two briefs, RC-10 and RC-11, as Stage 5**. Nothing in
Stages 1–4 was found unspecified or mechanically wrong; what rev 6 changes is what the mechanisms are
*presented as*, and one principle that was inherited rather than derived. Two decisions are reversals
and both are stated as such: **§5.10 merges the two panels into one** (§12 Q32 — §5's argument is about
kinds of entry and rev 2 spent it as an argument for windows; the reason actually given for the split
is unreadability, which a filter answers better), which **supersedes RC-7 R-rc7-9**; and **§5.11 lets a
designer correct what they wrote** (§12 Q33 — §8.3's blanket rule is git's, and git's reason is that a
rewrite invalidates every clone, which does not reach a workspace with one designer and no clone).
rev 6 also adds a fourth kept kind (§5.6 rule 6, §12 Q34 — the pre-restore checkpoint, which
`IsAlwaysKept` omits today), the way forward after a restore (§5.8, §12 Q35), and a review of the
titles about to leave the machine (§12 Q36). **Where a brief and rev 6 disagree, rev 6 is right.**

Every requirement below is either a decision the architecture left to the briefs, or a fact read out
of the tree with a file and line so a later reader can check it rather than trust it.

---

## 1. The two things this series is for, and why they are separable

The architecture's §1 states the motive twice, at very different strengths, and the split it produces
is what the staging below is built on:

- **The weak motive** — designers want history. They cope today with dated copies.
- **The governing motive (§1.2)** — an AI-authored edit needs a floor: *an automatic checkpoint taken
  immediately before every AI-initiated batch, spanning every file in the workspace, surviving a
  crash, revertible in one action.* It demands no branches, no commit messages and no git vocabulary.

**The safety net and the designer-facing history are therefore different features** with different
audiences and very different risk. Stages 2 and 3 are that split. Per §12 Q5, **Stage 2 ships before
AI design capability exists** — the floor goes in before the thing it is a floor under.

**The measure of done, per §10B:** a stage that ships mechanism without the user-facing explanation
has not shipped. §5 below assigns each documentation obligation to the brief that makes it true.

---

## 2. What the tree says today

Every line reference below was read, not recalled.

### 2.1 The `.cws` write path already has the choke point the split needs

`WorkspacePersistence.SaveToFileAtomic` (`src/Design/Workspace/WorkspacePersistence.cs:397`) became
the single write choke point in SL2 — its own header records that writes previously had fifteen call
sites and no place to ask a question, and that enforcing a rule by fifteen callers agreeing makes it
true in fourteen places and found by a user in the fifteenth. **Eighteen call sites route through it
today.** The `.cwsuser` split (RC-1) is one write becoming two, inside that one function.

Reads have had a choke point since the beginning (`TryLoadCws`, `WorkspaceViewModel.cs:2531`).

### 2.2 Read-only workspaces already exist — but for a different reason

SL2 shipped `WorkspaceWritability` (`src/Design/Workspace/WorkspaceWritability.cs`): read-only
**discovered by attempting a write**, memoised per workspace root, dropped by
`WorkspaceRootFinder.InvalidateCache`. That answers *"can circuitRF write here?"*

§7A.2's read-only is a different question — *"should circuitRF write here?"* — a **policy** on the
reference, true even when the filesystem would allow the write. **RC-2 adds the policy and reuses
SL2's behaviour**: the disabled Save, the Save As in its place, the refusal wording. It does not
build a second read-only concept.

### 2.3 `CwsWorkspaceRef` already has the shape, and the precedent for the default

`CwsWorkspaceRef` (`WorkspacePersistence.cs:228`) carries `Alias`, `Path` and `CellsOnly`.
`CellsOnly`'s own header states the house rule for an absent field: *false on every entry written
before this existed, which is the old behaviour.* **§7A.2 deliberately breaks that rule** — absent
means read-only, because the old behaviour there is the hazard, not a preference anyone set. RC-2
must state that inversion in the field's own header, next to `CellsOnly` stating the opposite.

### 2.4 The document-open dispatcher and its three OS registrations are held shut by tests

`App.OpenFiles` (`src/Ui/App.axaml.cs:608`) switches on ten extensions (`:627`–`:647`): `.crfw`/`.cws`
on the workspace branch, eight document types on the other. Its header records that both entry points
were once stubs that opened nothing, which read to a user as a broken file.

Three parity tests hold the list shut against the macOS `Info.plist` UTI declarations
(`WBondStandaloneTests.cs:494`), the WiX `.wxs`, and the Linux `circuitrf-mime.xml`
(`:732`–`:752`, `:851`–`:862`): a type declared to an operating system with no case here launches
circuitRF and opens nothing. **They must be extended, never worked around** (RC-1).

### 2.5 The archive scanner has a skip list and does not know about `.git`

`WorkspaceArchiveScanner.IsSkipped` (`src/Ui/Archive/WorkspaceArchiveScanner.cs:53`) filters three
lists declared at `:36`, `:42` and `:47` — `.generated-cells`, file-manager clutter, temp extensions.
**`.git` is in none of them.** The phase that first creates a repository inside a workspace folder is
the phase that must add it (§9A.3); this is RC-5's gate, not RC-8's feature.

### 2.6 The gzip reserve is a live invitation in a comment

`LayoutPersistence.cs:17` and `GzipTextFile` (`:316`–`:335`) record that the reader already sniffs the
gzip magic bytes so a future gzip **writer** needs no format-version bump. §3.2 measured what that
writer would cost a repository: **~508× per mid-file polygon drag, ~1,600× per deletion, 14.5× on the
finished `.git`.** RC-3 turns the invitation into a warning.

### 2.7 There is a program-path-in-Settings precedent, and it is exact

`VerilogACompilerSettingsView` (`src/Ui/Views/Dialogs/VerilogACompilerSettingsView.axaml`, 49 lines):
a text box whose **blank default means "look on PATH"**, a Browse button, a Test button that runs the
program and reports what it says it is, and a status line. Its preference is one nullable string
(`AppPreferences.cs:315`). It is hosted by two dialogs that share no other code. **RC-4's git path row
is the same control with a different program**, and copying its shape is the point.

### 2.8 Adding a Settings tab is not free, and three things renumber

- `SettingsView.axaml:17` has a bare `<TabControl>` with **no `ItemsPanel` override and no scoped
  `TabItem` style**. Four `TabItem`s at `:20`, `:100`, `:146`, `:270`.
- **`TabControl`'s default `ItemsPanel` is a `WrapPanel`.** `TechEditorView.axaml:251`–`:272` records
  this from a real failure: four headers *spilled onto a second row* as the window narrowed rather
  than clipping, and the fix already in the tree is a horizontal `StackPanel` plus a scoped `TabItem`
  style pinning `FontSize`, `Padding` and `MinHeight` — because inheriting the theme's header metrics
  makes one row a promise rather than a fit.
- `DocSettingsFixtures` (`src/Ui/Diagnostics/Fixtures/DocSettingsFixtures.cs:30`–`:39`) selects each
  documentation figure **by tab index**, `Tab(0)`…`Tab(3)`. Inserting at index 2 renumbers Color Theme
  and Wirebonds.
- `SettingsDialogHelpAndTooltipsTests.EveryTabOfTheDialogHasItsOwnFigureAndThePageCitesThemAll`
  (`:132`) asserts **exactly four** `<TabItem Header=` and one `FigureCatalog` record plus one
  `{{ui: …}}` citation per tab.

### 2.9 The Messages panel can already carry an action

`IMessageSink` (`src/Ui/Messages/IMessageSink.cs`) has `Post` (`:14`), `BeginProgress` (`:22`) and a
message carrying **a single invokable action** (`:29`), whose default implementation drops the action
and posts the text. That is the mechanism for §7A.3's *"offer the action, not just name it"* and for
§12 Q4's remedy — neither needs anything new.

### 2.10 The only AI-facing surface today is `serve`, and it must advertise the boundary

`src/Cli/Serve/` (`ToolCatalog`, `McpServer`, `ServeVerb`) is the stdio protocol adapter — the one
place an agent reaches circuitRF. It is **out of process and headless**, which is exactly why RC-5's
checkpoint boundary must be defined where the *batch* is known, not where a file is written.

**And it is where §5.3b lands.** An agent connected here typically has a shell and a `git` of its own:
it can read this repository and it can write to it, whether or not anyone intended that. So the surface
does two things it does not do today — it **advertises** the workspace's revision-control state (on,
off, held, unavailable; whether a batch is open; where the repository root is), and it **publishes the
rules** an agent follows when it reaches for git directly. Both belong to RC-5.

Advertising rather than letting the agent infer is the load-bearing half: **a `.git` directory alone
does not distinguish a repository circuitRF created from one the user created**, and §12 Q4 makes those
opposite situations.

---

## 3. Decisions the nine briefs depend on

**R-rc0-1. Order and dependencies.**

| brief | stage | depends on | needs git installed? |
|---|---|---|---|
| **RC-1** workspace-file split | 1 | — | **no** |
| **RC-2** read-only referenced workspaces | 1 | — | **no** |
| **RC-3** the git substrate | 1 | RC-1 (the `.gitignore` line) | yes, to do anything |
| **RC-4** Settings ▸ Revision Control | 2 | RC-3 | tab hidden without it |
| **RC-5** checkpoints and restore points | 2 | RC-3, RC-4 | yes |
| **RC-6** retention, the hold, off/on | 2 | RC-5 | yes |
| **RC-7** commit and the history browser | 3 | RC-5, RC-6 | yes |
| **RC-8** the archive's include-history option | 3 | RC-5, RC-7 | yes |
| **RC-9** clone-as-workspace, fetch/push, pins | 4 | all | yes |
| **RC-10** one history panel | 5 | RC-5, RC-6, RC-7, RC-9 | yes |
| **RC-11** correcting what you wrote, and the review before sending | 5 | RC-10 | yes |

**RC-1 and RC-2 can land in either order and neither waits for anything.** They are the two parts of
Stage 1 the architecture says are worth doing on their own merits, and both must reach the users
§4.3 identifies as the majority — designers with no git installed.

**R-rc0-2. The large-file guard (§8.2) lands in RC-5, not RC-3, and the architecture's staging is not
being overridden.** §11 stages "the large-file guard" at Stage 1 alongside the generated `.gitignore`.
The *prevention* half — the `.gitignore` that keeps results out from the start — is RC-3 and is Stage
1 as written. The *guard* half is a prompt shown **before a commit**, and Stage 1 takes no commits: a
dialog with no call site is a dead feature and an untestable one. It therefore lands with the first
thing that commits, which is RC-5. Nothing about it changes; only which brief builds it.

**R-rc0-3. No brief in this series changes numeric behaviour, and none touches `src/Engine`,
`src/RfCore` or any analysis.** A `.cnl` that produced a number before produces the same number
after.

**R-rc0-4. Every git invocation is a subprocess, through one type, and that type is in `src/Design`.**
`src/Cli` must be able to take a checkpoint headlessly (§1.2's agent is out of process), and `src/Cli`
cannot reference `src/Ui`. No git library is linked, ever (§4.1). The firewall test
(`tests/Firewall.Tests`) is what holds this. **And the commit identity reaches that type from a
`src/Design` reader of circuitRF's per-user preference file** (§4.4, rev 5) — RC-4's tab writes it,
RC-3's reader serves it to the GUI and to `src/Cli` alike. rev 4 let the headless case fall through to
git's own resolution, which names nobody on the fresh Windows machine §4.4 describes; that refused the
governing motive's checkpoint for exactly the population it targets.

**R-rc0-5. circuitRF writes to exactly one repository: the one whose root is the open workspace**
(§7A.1). Not an ancestor, not a referenced workspace, not a nested one. This is the rule every brief
from RC-3 onward is written against, and RC-6 builds the detection that enforces it.

**R-rc0-6. No git vocabulary reaches a designer who did not ask for it** (§0), with the one
qualification §0 states: what an **explicit** action produces may be named precisely. "Branch",
"checkout", "HEAD", "detached", "stash" and "merge conflict" appear in no user-visible string in RC-1
through RC-6. RC-7 may name a commit identity the user just created, and nothing else.

**R-rc0-7. No automatic operation destroys history.** Retention thins and never prunes to permanence;
packing never passes `--prune=now` and never reclaims; nothing reclaims what thinning freed unless a
person asks (§5.6a); there is no "delete all history" command at any stage (§5.6, §5.7, §8.3, §10).

**R-rc0-8. A gate that measures wall-clock time is not written.** Packing, checkpoint cost and
repository growth are **measured and reported in the completion write-up**, never asserted in a test —
a timing assertion measures the machine and flakes. Where a structural property is what matters,
assert a counter or a call count. This follows the repo's standing practice.

**R-rc0-9. Where a brief needs a repository-size or timing number, it re-measures on its own machine
class** and records the figure, per §2.5. The architecture's figures establish the *shape* of the cost
— kilobytes per commit for text, an unbounded loose-object overhang without packing — not a budget.
**And it measures without `gc` as well as after it**: measuring only the tidied state is what made
rev 1 miss §2.4 entirely.

**R-rc0-10a. Two processes on one repository are kept apart by git's own locks behind a temporary
index — not by the advisory lock** (§4.6, corrected in rev 5). rev 4 assigned the serialisation to
`WorkspaceLock`, whose own header says it holds no handle, is overridable both ways, and must never be
treated as authoritative — and which fires in precisely the situation §5.3c designs for. **RC-3 owns
this**: every checkpoint is built through a private index (§5.2b), so two circuitRF processes contend
only on reference updates, which git makes atomic; what remains is serialised by git's atomic lock
files, retried briefly before it is translated. Reads never wait on a writer. Packing yields to the
advisory notice, which is the one thing that notice is for here.

**R-rc0-10b. Every brief that adds a git-driven test states what happens on a machine with no git.**
Every gate from RC-3 onward needs a git executable, and not every developer machine has one. The repo
already has the idiom — `RfCore.Tests`' `FixtureFact`/`FixtureTheory` **skip with a reason** rather than
failing when their fixtures are absent — and it is the right one here: a skip that names why is honest,
a red suite on a machine missing a prerequisite trains people to ignore red. **CI has git on all three
platforms, so coverage is not lost.** Do not commit a git binary, and do not vendor one.

**R-rc0-11. The rules an agent is given are part of the feature, not documentation** (§5.3b). They are
published by the `serve` surface next to the state, so an agent learns them from the server rather than
from having read the architecture. Two of the ten carry the weight: **do not commit**, and **if the
batch cannot be opened, stop and say so.** Everything else prevents damage; those two prevent the
*helpful* failure — an agent that, finding no checkpoint mechanism available, improvises a backup
nobody can restore from and reports success. That is §1.4's false belief manufactured by the one
participant fast enough to manufacture it at scale.

**R-rc0-13. A workspace is armed by a per-user application preference, which an explicit per-workspace
setting outranks** (§5.7a, §12 Q13). The preference ships **on** — §12 Q5 puts the floor in before the
capability, and a floor found only in a settings tab is not one; §4.3 already makes the whole feature
invisible to everyone without git. **Two guards make "on" honest rather than a surprise:** a workspace
is armed **at the first boundary that would record something, never at open** — so opening a colleague's
workspace on a share creates nothing — and the first time a workspace gains a repository, that is
**announced once**, with the setting's location and §5.7's reassurance that removing it later cannot
harm the design. RC-4 owns the preference, RC-5 the boundary and the announcement, RC-6 the
per-workspace override. **"Would record something" is defined without a repository** (rev 5, §12 Q24):
an unarmed workspace arms on close only if circuitRF itself wrote a file during the session, and always
on a save-point or a batch; an armed one records only when the tree differs from the newest checkpoint.
And **a session that recorded nothing sweeps nothing and packs nothing** — a colleague's glance at a
shared workspace runs no housekeeping under the reader's preferences over the owner's history.

**R-rc0-14. A batch and the window holding the workspace are never both live** (§5.3c, §12 Q15). This is
§5.8's problem arriving by the front door: an out-of-process batch edits files the window has open, over
undo stacks describing content that is no longer there, and the window's next save discards the agent's
work — §7A.3's two-editors-one-file failure, reached through the mechanism §1.2 is the motive for. There
is no filesystem watcher today and a tree rescan is not a document reload, so the answer is three rules
in RC-5: **a batch is refused while the window holds unsaved changes** (the fourth member of R-rc5-6f's
family, refused at the same moment as off, held and scratch), **documents the batch modified are
reloaded with their undo stacks discarded on close** (§5.8's rule, §5.8's path), and **the window
observes the batch state it is already advertised** rather than discovering the change on focus. Rule
one is what makes rule two cheap: with nothing unsaved, a reload cannot lose anything.

**R-rc0-15. "Did circuitRF create this repository" is answered by a marker in the repository's own
config, not by inference** (§4.5). §12 Q4, §9A.5 and R-rc5-6g all turn on the distinction, and a `.git`
directory alone cannot make it. It is repository configuration because it is a property of the
repository, which gives the two behaviours wanted: **an archive carries it** (§9A copies the directory,
so an extracted archive is recognised without ceremony) and **a clone does not** (config is not cloned,
so a clone reaches R-rc0-13's arming path on its own terms). It records **which answer §12 Q4 got**, so
the question is asked once. RC-3 writes it; RC-6 reads it.

**R-rc0-12. Absence is silent; a hold is loud.** Git missing means the affordances are not there at
all (§4.3) — a designer who does not want this never learns it exists. Git present but **held**
(§12 Q4) means the affordances stay visible and refuse, because a hidden control is
indistinguishable from a feature that was never built, and the failure this whole document guards
against is a designer who believes they are protected and is not. **On macOS, "present" is checked
without running it** (§4.3a): `/usr/bin/git` is a shim that opens Apple's install dialog when the
developer tools are absent, and detection by invocation would be the surprise §4.3 forbids.

**R-rc0-16. A checkpoint commit has no parent, is built through a temporary index, and captures every
non-ignored file including new ones** (§5.2b, §12 Q18). Parentless is what makes thinning real: with a
chain, deleting a reference frees nothing. **The thinning gate asserts unreachability, not survival** —
RC-5 gates the shape, RC-6 gates the sweep, and both would pass on a chained implementation if they only
checked that the other references still resolve.

**R-rc0-17. No branch, ever.** A restore is a working-tree write that never moves `HEAD` (§5.8); the
next checkpoint or commit records the restored content as the next step in one line of work (§6.3,
§12 Q19). RC-5 builds the restore; RC-7 inherits it and builds no variant.

**R-rc0-18. Nothing reclaims what thinning freed unless a person asks** (§5.6a, §12 Q20). `gc.pruneExpire`
stays `never`; the reflog is not the way back (a deleted reference takes its reflog with it); RC-6
keeps the journal that is; RC-3 provides the reclaim operation; RC-4 exposes it as the tab's one
confirmed destructive action.

**R-rc0-19. Every history operation has a headless spelling** (§5.3d, §12 Q29): one verb, `history`,
with nouns `checkpoint`, `list`, `restore` (RC-5) and `commit` (RC-7), each calling the `src/Design`
function the GUI's own command calls. The batch's open and close stay on `serve`. Adding the verb to the
repo-root `CLAUDE.md` and `docs/design/cli.md` is the owner's edit — flag it, do not make it.

---

## 4. What this series does not do

Restated from §10 so no brief quietly adopts one:

- **No merge UI** (§6.1) — `.clay`, `.csch`, `.csym`, `.cws` and `.ctech` are marked unmergeable and
  conflict resolution is whole-file, pick a side.
- **No branch, in any form** (§6.3, rev 5) — a restore never checks anything out, so nothing creates one.
- **No automatic reclaim** (§5.6a) — reclaiming what thinning freed is an explicit, confirmed action.
- **No batch against a window with unsaved changes** (§5.3c) — refused, because nobody can answer §5.8's
  prompt headlessly.
- **No filesystem watcher, no dirty flag in the lock file, no poll** (§5.3c) — the second-instance
  channel carries both facts.
- **No second cross-process lock** (§4.6) — git's own atomic lock files, behind a temporary index.
- **No stash UI** (§6.4), **no history rewriting** (§8.3), **no "archive the last N versions"**
  (§9A.4), **no "delete all history" command** (§5.7).
- **No commit per file save**, and **no checkpoint on simulation run or on idle** (§5.3).
- **No "add it once, then ignore it"** (§8.2a) — `.gitignore` has no effect on a tracked file, so the
  option resolves to either a lie or design-IP loss.
- **No embedded git library** (§4.1); **nothing requiring a server** — no lock server, no large-file
  extension (§6.2, §8.3).
- **No commit into any repository other than the open workspace's own** (§7A.1).
- **No undo/redo backed by version control** (§5.4).
- **No credential prompt, and no stored credential** (§9.1) — circuitRF uses whatever git is already
  configured to use and refuses rather than asks. `GIT_TERMINAL_PROMPT=0` is what turns "refuses" into
  a sentence instead of a hang.
- **No inferred AI batch** (§5.3a) — the agent declares the boundary or there is no checkpoint.
  Inference needs a timeout, and a timeout is §5.3's rejected idle trigger under another name.
- **No agent-callable revert** (§5.3a rule 5) — reverting is offered to the designer, not to the thing
  that made the change.
- **No checkpoint standing in for crash recovery** (§5.9) — scratch has its own autosave and the two
  jobs are different shapes.

---

## 5. The documentation obligations, assigned

§10B is a requirement of the architecture, not housekeeping, and it is explicitly **not a stage** —
each brief ships the part of it that brief makes true. Authored under `docs/user/src/` and built by
the docs factory (`docs/design/user-docs-factory.md`).

| §10B item | owned by |
|---|---|
| §10B.1 normative table — the `.cwsuser` row | RC-1 |
| §10B.2 scenario 10 — fixing a cell in a referenced library | RC-2 |
| §10B.1 rows for results and for "never include files like this" | RC-3 (results), RC-5 (the choice) |
| the Settings chapter's new tab section, and its figure | RC-4 |
| §10B.2 scenarios 1 and 6; §10B.3 the AI-checkpoint chapter | RC-5 |
| §10B.2 scenarios 7, 8 and 9; the §10B.1 rows for retention, the hold, and "off" | RC-6 |
| §10B.2 scenarios 2, 3 and 4 | RC-7 |
| §10B.2 scenario 11 — sending a workspace to a customer | RC-8 |
| §10B.2 scenario 5 — using a library another team maintains | RC-9 |
| §10B.2 scenario 12 — *"I saved a copy and the history didn't come with it"* | RC-5 (it is RC-5's exclusion that causes it) |
| §10B.1 rows for a **clone**, a **Save As** copy and a **scratch** workspace | RC-9, RC-5, RC-5 |
| §10B.1 row for *anything in a referenced workspace* | RC-2 |
| §10B.1 row for *history in a workspace archive* | RC-8 |
| §8.3's plain-language sentence that history rewriting is the user's own to do, with `git`, outside circuitRF | RC-7 (it sits beside the commit action a user would look for it from) |
| §5.3b's agent rules, published by `serve` | RC-5 |
| §10B.2 scenario 13 — *"circuitRF says it started keeping a history of this workspace"* | RC-5 (RC-4 owns the preference the chapter points at) |
| §10B.2 scenario 14 — *"this workspace already had version control and circuitRF asked me something"* | RC-6 |
| §10B.1 row for *anything at all, before a workspace is armed* | RC-5 |
| §10B.1 row for *panel layout, in a Save Workspace As copy* | RC-1 |
| the §10B.1 table itself, and its two **yes** rows (design documents; workspace configuration) | RC-5 (the first brief with a chapter) |
| §10B.1 row for *a file left out of a restore point at an unattended boundary* | RC-5 |
| §10B.1 row for *a restore point that retention thinned* | RC-6 |
| §10B.2 scenario 15 — *"my disk is full and it says the history is taking the space"* | RC-6 (RC-4 owns the control it points at) |
| §10B.3's "when the floor is not there" paragraph | RC-5 |
| the two user chapters merged into one, and the retired slugs | RC-10 |
| §10B.2 scenario 17 — *"I went back and I want to come forward again"* | RC-10 |
| §10B.2 scenario 16 — *"I typed the title wrong"* | RC-11 |
| §10B.1 rows for a title on a shared and an unshared version, and for a restore point's label | RC-11 |
| §10B.1 row for *the state you were in when you went back* | RC-10 |
| §8.3's sentence, re-pointed to content now that §5.11 governs titles | RC-11 |

**§10B.3 stands on its own:** AI checkpoints are documented in their own chapter, never interleaved
with the ordinary history material, because a safety device the user did not ask for and a statement
the user made mean different things and presenting them together invites the belief that one
substitutes for the other.

---

## 6. On completion of the series

Findings go to `src/Design/RESOLVED.md` and `src/Ui/RESOLVED.md` — and to `src/Cli/RESOLVED.md` for
anything headless. **Never to a `CLAUDE.md`**; create a `RESOLVED.md` where the implementation lands
if none exists. §10B.4 names the class of finding that matters most: `docs/design/revision-control.md`
§2.4 is an example — a measurement taken one way gave a materially wrong picture of the cost, and the
next person needs to know that before re-measuring.

Update `docs/design/revision-control.md`'s status line as each stage lands, and
`docs/design/project-file-formats.md` when RC-1 adds a format. The verb and format lists in the
repo-root `CLAUDE.md` are standing project memory — **flag additions to the owner rather than editing
them as part of a findings write-up.**

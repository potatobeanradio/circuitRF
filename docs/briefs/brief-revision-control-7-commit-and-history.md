# Sonnet Brief — RC-7: the commit action and the history browser

**Read `brief-revision-control-0-overview.md` first.** The architecture is
`docs/design/revision-control.md` §5.2, §5.5 (its first row — the other three are RC-5's), §6.1, §6.3,
§6.4, §5.3d and §4.2. **Depends on RC-5 and RC-6.** **rev 5 withdrew variants from this brief** (§6.3,
§12 Q19): a restore never checks anything out, so restore-then-edit is linear and there is no branch to
name. §5 below is what replaced them.

**Scope: the narrative half** — the sparse, deliberate, human-written history a designer creates on
purpose and browses later. This is the risky, user-facing stage, which is why everything else came
first.

---

## 1. What this is, and what it is not

**R-rc7-1. Ordinary commits on the ordinary branch, created deliberately, with a title the designer
wrote.** This is what the history browser shows, what is shared, and what is pushed (RC-9).

**R-rc7-2. Hidden entirely when git is absent** (§4.3), visible-and-refusing when held (RC-6
R-rc6-8). The pair is deliberate and the brief must ship both halves.

**R-rc7-3. The designer is never shown a commit graph, a branch name, a detached HEAD, or a merge
conflict. They are shown their own design at an earlier moment.** Git is what makes that cheap and
durable; it is not what the feature is about.

**R-rc7-4. The one qualification, and it is not a loophole.** "No git vocabulary" governs what is shown
to a designer who never asked for version control. It does not govern what is shown to someone who has
just pressed **Commit** — that person has opted in, and withholding the identifier of the thing they
just created would be coyness, not clarity. The rule is: **nothing git-shaped appears unbidden; what an
explicit action produces may be named precisely.**

---

## 2. What a commit says

**R-rc7-5. A commit message records how the commit came about**, because six weeks later *"which of
these did I mean?"* is the only question anyone asks of a history. §5.5 has four rows; **this brief owns
the first and RC-5 R-rc5-9a owns the other three**, because the brief that creates a commit owns its
message (corrected in rev 5 — rev 4 put all four here, three stages after the first three were created):

| origin | where it lives | message shape |
|---|---|---|
| the user pressed **Commit** and typed a title | the designer's branch | the user's title, plus a line recording that this was an explicit commit by the user — and, when the working tree was restored since the previous commit, **which restore point it was restored from** (R-rc7-16) |

**R-rc7-6. The commit after a restore says what it came from, and that is the whole of what the
narrative needs to record about a restore.** The save-point-versus-close distinction rev 4 placed here is
drawn in the restore-point list, where those two entries appear together (RC-5 R-rc5-9a); an explicit
commit never shares a list with either. What the narrative can lie about by omission is a restore: two
consecutive commits where the second silently reverts the first is a history that reads as a change of
mind with no record of the moment. The line names the restore point, and it is written by the commit
path from the state R-rc5-12c's restore leaves behind.

**R-rc7-7. The Messages panel reports an explicit commit, and names it.** One entry: what was
committed, and **the commit's identity**. That identifier is what makes the escape hatch usable — it is
what the designer, or someone helping them, types into a git command when circuitRF's own UI cannot
answer the question. R-rc7-4 is why naming it here does not contradict the vocabulary rule.

**R-rc7-8. Automatic checkpoints still post nothing** (RC-5 R-rc5-11). They are in the restore-point
list, which is where someone looking for one looks.

---

## 3. The browser

**R-rc7-9. SUPERSEDED BY rev 6 (§5.10, §12 Q32) — RC-10 merges the two panels into one.** What
survives is the sentence's subject: the two *kinds of entry* stay distinct, distinctly marked, and
distinctly stored, and §5.10 rule 6 forbids the merge from becoming a storage change. What is
withdrawn is the second window. Gate 5 below narrows with it — checkpoints and commits still may not
be confused for one another, and they are now confusable in one list, which is what the mark and the
filter are for. The original text follows.

*The browser shows the narrative; the restore-point list shows the safety net; they are not
merged.* Conflating them produces a log no human will read, which then makes the safety net useless
too because nobody looks at it. They may sit side by side and must not interleave.

**R-rc7-10. An "off" period renders as a gap, with its reason** (RC-6 R-rc6-13). Rendering it as an
ordinary interval between two commits is the false-belief failure in its purest form.

**R-rc7-11. A commit can be compared, and comparison is the browser's real job.** What changed between
two states, at the granularity of documents rather than lines — this is the design layer's question,
not the text layer's. Where a per-document diff is genuinely useful (a `.csch`'s connectivity, a
`.ctech`'s layer table) that is a legitimate follow-up and **not this brief's scope**; naming a
changed document is.

---

## 4. Conflicts: whole-file, pick a side

**R-rc7-12. There is no merge for a layout, and circuitRF must not pretend there is.** A three-way text
merge of a polygon's vertex list can produce geometry that is invalid, or valid and wrong, while
remaining perfectly well-formed JSON that opens without complaint. The same is true of a schematic's
connectivity. **A merged design that is silently wrong is worse than a conflict**, because the conflict
is at least visible.

RC-3's generated `.gitattributes` already marks the five document types unmergeable, so git will not
attempt one. This brief builds what the user sees.

**R-rc7-13. Conflict resolution is presented as a choice between two NAMED versions, never as a diff to
reconcile.** Who wrote each one and when, and what changed in each — enough to choose, and no
invitation to merge by hand.

**R-rc7-14. This is not conservatism, and the observation belongs in the code comment.** Large
analog/RF design-management architectures are centralised and **pessimistic** — check-out/check-in
locking so two people cannot edit one cellview concurrently, versioning at the granularity of the
cellview rather than the file, and a separate configuration object binding which version of which cell
a hierarchy resolves to. **The locking is the direct consequence of no-merge**: when merge is
impossible, preventing divergence is the only remaining strategy. circuitRF builds no lock server, but
that is why whole-file resolution is correct rather than lazy, why RC-2's read-only default is the
cheapest available approximation of a lock, and why RC-9's pins are the one genuinely enterprise-grade
item on the list.

---

## 5. Restore, then commit — one line of work, and no branch anywhere

**R-rc7-15. Designers will not create branches, and circuitRF creates none either.** The native idiom
for trying an idea in this domain is already **another cell, or another view within a cell** — cheaper,
visible in the project tree, and comparable side by side, which a branch is not.

**R-rc7-16. Restore-then-keep-editing is linear** (§6.3, §12 Q19, corrected in rev 5). rev 4 said git
requires a branch there and had this brief build a silently-created *variant*. It does not: a branch is
required only if the restore is a *checkout*, and RC-5 R-rc5-12c's restore is a working-tree write that
never moves `HEAD`. The next commit records the restored content as the next step on the same branch —
what the designer meant by "I went back to Tuesday", and the only shape that survives R-rc7-12 on a
shared branch, where a second line of work would be a merge nobody can perform. **The variant, its
branch, its naming and its tree marking are withdrawn.** What this brief builds is R-rc7-5's line —
the commit says which restore point it came from — and the browser shows that line. The words branch,
checkout and HEAD have no scenario in which to appear, and gate 8 scans for the commands that would
create one.

**R-rc7-16a. The restore this builds on is RC-5's and already has preconditions** (§5.8, R-rc5-12a/b/c):
it takes a checkpoint of the state it replaces, offers up unsaved work through the existing prompt,
**discards the undo stacks of reloaded documents**, removes files created after the restore point,
leaves ignored files alone, preserves the off flag and the policy files, and leaves a marker while it
runs. This brief does not re-implement any of that and must not weaken it — in particular, a commit
made after a restore does not make the pre-restore checkpoint redundant, because that checkpoint is what
a designer who chose the wrong restore point gets back from.

**R-rc7-17. A restore reached from the history browser — restoring to a COMMIT rather than to a
checkpoint — goes through the same restore** (R-rc5-12c), with the commit's tree as the source. There is
no second restore implementation, and the one difference is what the following commit's line names.

**R-rc7-18. Stashes are excluded** (§6.4). A stash solves a problem designers do not have — a dirty
tree blocking a branch switch, in a world where they do not switch branches. Their equivalent is
already "save a copy of the cell", and it is better because it is visible.

---

## 6. The translation layer, now in front of a user

**R-rc7-19. RC-3's recognised-failure table is what this brief renders**, and this is the first stage
where a failure lands in front of a designer who initiated the action. Every failure reaching the UI is
one of RC-3's translated sentences or is reported verbatim and honestly (RC-3 R-rc3-5) — never
swallowed, never replaced with a generic apology.

**R-rc7-20. Any failure this brief encounters that RC-3's table does not name is added to that table**,
not handled locally. A translation living only in the UI is a translation `src/Cli` does not have.

**R-rc7-22. `history commit` is the headless spelling** (§5.3d, RC-5 R-rc5-23): the fourth noun on
RC-5's verb, calling the same `src/Design` function the Commit action calls, with the title as an
argument and the same refusals — held, off, absent, and nothing to commit. Gate 12 is what holds it.

**R-rc7-21. The one place §8.3's escape hatch is stated in plain language is here** (RC-0 §5). History
rewriting is the only real remedy for a large file already committed, circuitRF must not offer a button
for it (R-rc0-7), and a user who genuinely needs it should be **told so plainly rather than left to
conclude the application has no answer**. It sits beside the commit action because that is where
someone looking for it will be. One sentence: this is something `git` can do, circuitRF will not do it
for you, and it invalidates every existing clone — which is why.

---

## 7. Gates

1. **An explicit commit says it was explicit, and a commit after a restore says what it restored from**
   (R-rc7-5, R-rc7-6): restore to a restore point, commit, and assert the message names it — and that a
   commit with no restore since the previous one carries no such line.
2. **The commit identity is reported once, on an explicit commit only** (R-rc7-7, R-rc7-8).
3. **No git vocabulary except where R-rc7-4 permits it**: source-scan every user-visible string for
   branch, checkout, HEAD, detached, stash, merge — comments stripped. The one permitted appearance is
   the identity in R-rc7-7's entry, and it is asserted by name so the exemption cannot spread.
4. **Hidden without git, visible-and-refusing when held** (R-rc7-2) — both halves.
5. **Checkpoints do not appear in the narrative browser**, and commits do not appear in the restore-point
   list (R-rc7-9).
6. **An off period renders as a gap with its reason** (R-rc7-10), driven by the data RC-6 records.
7. **A conflict offers two named versions and no reconcile path** (R-rc7-13): assert there is no code
   path that writes a merged file for any of the five unmergeable types.
8. **Restore-then-edit is linear and creates nothing** (R-rc7-16): restore, edit, commit, and assert
   the branch has one new commit, `HEAD` never left it, and no reference under `refs/heads/` was created.
   Source-scan the paths this brief touches for `checkout`, `switch`, `branch`, `stash` and `reset`
   invocations and fail on a hit, comments stripped — RC-5 gate 24's scan, over this brief's paths.
8a. **Restoring to a commit uses RC-5's restore** (R-rc7-17): restore from the browser to an old commit
   and assert the pre-restore checkpoint exists, ignored files survived, and a file added since is gone —
   the same assertions as RC-5 gates 15 and 17a, reached from this entry point.
9. **Every failure surfaced to the user came from RC-3's table** (R-rc7-19): a local sentence for a git
   failure fails the gate.
10. **A restore reached from the history browser still checkpoints first** (R-rc7-16a): assert the
   pre-restore state is restorable after a restore-then-edit-then-commit.
11. **The §8.3 sentence exists and offers no button** (R-rc7-21): source-scan for a rewrite,
   amend, rebase or filter code path and fail on a hit, comments stripped.
12. **`history commit` is byte-identical to the Commit action** (R-rc7-22), exempting only a
    legitimately-varying timestamp and naming it, and a comment-stripped source scan proves the view
    model kept no second copy — the standard `AuthoringCliVerbTests` already holds.

---

## 8. Documentation (§10B.2 scenarios 2, 3, 4)

- **Scenario 2** — *"I want to keep this version — it's the one I'm sending out."* An explicit commit,
  its title, and where it shows up afterwards.
- **Scenario 3** — *"I went back to an old version, then kept working."* That the restore is one step
  in one line of work, that the moment before it is itself a restore point, and that the next commit
  says what it was restored from. **There is no second line of work to explain**, and the words branch
  and checkout do not appear because nothing they would name exists.
- **Scenario 4** — *"Two of us are editing the same workspace."* Whole-file, pick a side, and **why
  circuitRF will not merge them** — which is a statement about geometry and connectivity, not about
  effort.

---

## 9. On completion

Findings to `src/Ui/RESOLVED.md` and `src/Design/RESOLVED.md` — **never to a `CLAUDE.md`**.

**Report, do not silently absorb:**
- Every git failure encountered that RC-3's table did not name (R-rc7-20), and whether it was added
  there rather than handled locally.
- Whether the restored-from line read correctly when the restore point was itself a thinned one
  restored from RC-6's journal, and when it was the pre-restore checkpoint of an earlier restore — two
  restores in a row is where the wording is most likely to confuse.
- Whether the browser could show what changed at document granularity without a per-document diff, and
  what it cost if not (R-rc7-11).
- Whether a restore-then-commit ever left two restore points a designer cannot tell apart
  (R-rc7-16a) — that would be a finding, and the fix is wording, not mechanism.

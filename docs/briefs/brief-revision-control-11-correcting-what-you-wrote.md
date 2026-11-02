# Sonnet Brief — RC-11: correcting what you wrote, and the review before sending

**Read `brief-revision-control-0-overview.md` first.** The architecture is
`docs/design/revision-control.md` **rev 6** — §5.11 (the three cases), §8.3 as rev 6 narrows it, §9 and
§9A.3 (the review), §12 Q33 and Q36. **Depends on RC-10**, which builds the list and the context menu
these actions hang from, and on RC-7 and RC-9, whose commit and sharing paths this brief constrains.

**Scope: what a person wrote, and the moment before it leaves the machine.** No content is altered, no
version is deleted, no author is rewritten, and no shared chain is rewritten. **This is the first brief
in the series that writes to a reference a designer has already read**, which is why it is last.

**Why it exists, and it is not typography.** A designer who believes a title they typed is permanent
and uncorrectable will type a useless one, and will not share a history they cannot tidy — which costs
§5.2's whole motive. The failure named in review was concrete: somebody writes a careless line, or one
with a customer's name in it, and can now never show the history to anyone. §8.3's rule did not
actually reach that case — §8.3 is about *content*, a large file or one that should never have left the
machine — and its reasoning is git's: a rewrite invalidates every clone. **True, and irrelevant to a
workspace with one designer, one machine and no clone**, which is the common case here.

---

## 1. The line everything follows from

**R-rc11-1.** *What was recorded is never altered. What a person wrote **about** it may be corrected by
that person, until it has been shared — after which it can only be annotated.*

Content, trees, times and the sequence are immutable with no exception anywhere. Titles and labels are
commentary, and commentary is correctable by its author. **The line is drawn at sharing** because
sharing is the only event that creates a second reader, and the second reader is the entire reason
git's own rule exists.

**R-rc11-2. "Shared" is computed, never assumed.** A version is shared when it is reachable from a
remote-tracking reference; anything else is local. The computation is one query and it is the hinge of
this whole brief, so **it is a named function with its own tests**, not a condition inlined at three
call sites. Where it cannot be computed — a remote configured but never fetched — the answer is
**shared**, because the reversible direction is the safe one (§9A.1's rule) and a correction refused is
recoverable while an erasure believed is not.

---

## 2. Case (a) — a restore point may be renamed, and may be deleted

**R-rc11-3. This is not §8.3's rewriting and the mechanism already exists for another purpose.** §5.2b
makes each checkpoint a **parentless** commit under circuitRF's own reference namespace: nothing chains
to it, no clone takes it, no push carries it (§5.2a). A rename is a new parentless commit over the
**identical tree** with a corrected message and one reference update. A delete is one reference delete
— **exactly what §5.6's retention already does**.

**R-rc11-4. A rename preserves everything the message carries but the label** — the sequence, the
origin, the kept mark, the left-out record (§5.2b). Losing a trailer here is silent and shows up weeks
later as an entry retention thins that it should not have, or as a row whose origin sentence has
changed to something the designer did not do.

**R-rc11-5. A delete goes through RC-6's thinning journal, not around it.** It is journalled the way a
sweep's deletion is, it is reversible by the same *bring this back*, and it therefore frees nothing
until §5.6a's reclaim — which is right: a designer tidying a label should not be the one path in this
document that destroys a state.

**R-rc11-6. Renaming or deleting an entry a batch created does not erase that the batch happened.**
§1.2's checkpoint is the floor under an AI edit; a label is a label. If the origin sentence stops
saying an agent made the next change, the trailer is being lost — see R-rc11-4.

---

## 3. Case (b) — an unshared version's title may be corrected

**R-rc11-7. The newest unshared version is the requirement.** A commit reachable from no
remote-tracking reference has by definition no second reader, so correcting it invalidates nothing.
Nearly every bad title is noticed within a minute of being typed, and that is the case this buys.

**R-rc11-8. The unshared *tail* is a follow-up and must not be assumed.** Correcting an older unshared
version is a rewrite of every version after it — safe for the same reason, and materially more work
over a workspace with thousands of files. **If it is not built, the refusal names why and offers the
annotation of case (c)**, which is available for any version at all.

**R-rc11-9. `--amend` is permitted in exactly one file, and RC-7 gate 11 narrows rather than lifts**
(§12 Q33). `rebase`, `filter-branch`, `filter-repo`, `replace` and `reflog` stay forbidden across every
revision source. The amend is asserted **by name** at its one site, the way R-rc7-4's identity
exemption is, so it cannot spread. A second file acquiring it fails the gate.

**R-rc11-10. A correction does not restamp the version.** The author, the time and the tree are the
ones already recorded. A title correction that quietly moved a version's date would make §5.6 rule 2's
ordering argument false in the one list a designer reads it from.

**R-rc11-11. §5.2a's checkpoint references are independent of the branch** and a tail rewrite does not
disturb them. Assert it rather than reason about it: the failure is invisible until somebody restores.

---

## 4. Case (c) — a shared version may be annotated, never erased

**R-rc11-12. The string is on somebody else's disk, and the UI says so.** A delete offered here would
be §5.3b rule 10's reassuring sentence about a protection that does not exist — told to the designer
this time instead of by the agent.

**R-rc11-13. A correction is an annotation git attaches to a commit without altering it**, shown by the
panel in place of the original with the original one click away, and carried alongside the versions it
annotates when they are sent (§9). It is written and re-written freely; it is the one mutable thing in
the history and that is what makes it the right home for commentary.

**R-rc11-14. The dialog says plainly that the original wording stays in the file and can still be read
by anyone holding the workspace.** **This sentence may not be softened.** A designer whose problem is
embarrassment specifically needs it, and a UI that hid it would cause the exact harm they came to it to
avoid — §1.4's false belief, in the one place where the belief is about other people rather than about
their own data.

**R-rc11-15. `HistoryMessages.RewritingIsYoursToDo` is re-pointed, not deleted.** It currently says
that circuitRF *will not alter* a kept version, full stop, which after this brief is untrue of its
title and remains true of its content. It keeps its subject — a file that must not be in the history —
and stops claiming the part §5.11 now governs. It sits beside the action a user looks for it from
(RC-7 gate 11 asserts both its sites).

---

## 5. The review before a history leaves the machine

**R-rc11-16. Send, clone and archive list the version titles that are about to leave** (§9, §9A,
§12 Q36). One list, in front of an operation that is already deliberate.

**R-rc11-17. It is worth more than every correction mechanism above.** The expensive case is not a
careless word; it is a customer's name or a part number in a title going to a different customer. §9A.3
already establishes that a computation the user cannot perform belongs in front of the operation, and
this is that computation: nobody can recall from memory what forty titles say.

**R-rc11-18. It composes with §9A.3's existing warning rather than replacing it.** That warning is
about *files* a history still holds after they were deleted from the workspace; this is about *titles*.
Both are true, they are about different things, and the archive dialog carries both.

**R-rc11-19. It is not a confirmation prompt with a checkbox.** It is a readable list with the
corrections of §5.11 reachable from it, because the useful response to reading a bad title is fixing
it, not abandoning the send.

---

## 6. Gates

`tests/Ui.Tests/Revision/CorrectingWhatYouWroteTests.cs`.

1. **A renamed restore point keeps its tree and every trailer but the label** (R-rc11-3, R-rc11-4):
   assert the tree id is byte-identical to the original's and that the sequence, origin, kept mark and
   left-out record survive.
2. **A deleted restore point is journalled and comes back** (R-rc11-5): delete, assert RC-6's journal
   holds it, bring it back, assert the tree.
3. **A deletion frees nothing until a reclaim** (R-rc11-5): the RC-6 fixture's assertion, reached from
   this entry point.
4. **The newest unshared version's title corrects, and the tree, author and time do not move**
   (R-rc11-7, R-rc11-10).
5. **A shared version refuses the correction and offers the annotation** (R-rc11-2, R-rc11-12): a
   workspace with a remote-tracking reference covering the version, and one with a remote configured
   but never fetched — **both refuse**, and the second is the case the safe-direction rule decides.
6. **The annotation shows in place of the original, and the original is reachable** (R-rc11-13), and it
   travels with a send (RC-9's fixture, with an annotation added).
7. **The un-softened sentence is present** (R-rc11-14): assert the dialog's text contains the statement
   that the original stays readable — the same shape as RC-7 gate 11's assertion on
   `RewritingIsYoursToDo`.
8. **RC-7 gate 11 narrows and does not lift** (R-rc11-9): `rebase`, `filter-branch`, `filter-repo`,
   `replace`, `reflog` still absent everywhere; `--amend` present in exactly one named file and absent
   from every other revision source.
9. **Checkpoint references survive a title correction** (R-rc11-11): correct, then restore to a
   checkpoint taken before it, and assert the tree.
10. **No content is ever altered** (R-rc11-1): after every correction path in this brief, assert every
    tree in the repository is the tree it was.
11. **The review lists the titles about to leave, for all three journeys** (R-rc11-16): send, clone and
    archive, each showing the same list from the same function, and the archive showing §9A.3's file
    warning as well (R-rc11-18).
12. **`history` has a spelling for each correction** (§5.3d): the verb as a process against the
    in-process call, byte for byte, and the comment-stripped scan proving the view model kept no second
    copy — the standard `AuthoringCliVerbTests` shape.

---

## 7. Documentation

- **§10B.2 scenario 16** — *"I typed the title wrong."* That a restore point's label and an unshared
  version's title are yours to correct, that a shared one takes a correction instead, and the one
  sentence that must not be softened: **the original wording stays in the file.** It exists because a
  designer who believes a careless line is permanent will write a useless one instead.
- **The §10B.1 rows rev 6 added** — the two title rows, the restore-point label row.
- **The chapter's philosophy paragraph changes**, and it is the paragraph the merged chapter opens
  with: history records what you did and never edits it; what you *wrote about* it is yours until you
  have shared it. Saying it once, early, is what stops a designer discovering the rule at the moment
  they most want it to be different.
- **Scenario 11** (*sending a workspace to a customer*) gains the review step.

---

## 8. On completion

Findings to `src/Design/RESOLVED.md` and `src/Ui/RESOLVED.md` — **never to a `CLAUDE.md`**.

**Report, do not silently absorb:**
- What the shared computation (R-rc11-2) actually costs on a workspace with a remote, and whether it
  needed caching to keep the context menu responsive. If it is per-row, say what per-row cost.
- Whether the annotation survived every journey it was supposed to and which one it did not — the
  travel table in §5.2a is the reference, and an annotation that silently fails to travel is a
  correction a colleague never sees while the sender believes they made it.
- Whether the unshared *tail* correction (R-rc11-8) turned out cheap after all. If it did, say so with
  the measurement; if it did not, say what the refusal reads like, because that sentence is what most
  designers will actually meet.
- Whether any wording in this brief's dialogs drifted toward implying an erasure. That drift is the
  failure mode of this entire feature and it is a wording finding, not a mechanism one.

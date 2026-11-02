# Sonnet Brief — RC-2: referenced workspaces are read-only by default

**Read `brief-revision-control-0-overview.md` first.** The architecture is
`docs/design/revision-control.md` §7A.2, §7A.3, §7A.5 and §6.2. **This brief depends on nothing, needs
no git installed, and is not a revision-control feature.**

**Scope: a cell reached through a `ws://` reference is not editable through the window that merely
references it.** One field on `CwsWorkspaceRef`, one refusal that carries its own remedy, and one
message reworded.

---

## 1. This is a defect in the shipped application, found while thinking about something else

The chain §7A.2 describes, step by step. A designer opens a library cell through a `ws://` reference,
finds a wrong pad size, fixes it, saves:

1. The file is written — filesystem-is-truth; circuitRF does not intercept ordinary saves.
2. **Their** workspace has no record of it — the file is not in their repository.
3. **The librarian's** workspace has no record of it either — circuitRF committed nothing there
   (§7A.1) and the librarian was not asked.
4. The fix works for that designer and for nobody else. Every other designer still simulates the wrong
   pad.
5. On the next publish the local edit is overwritten, or it conflicts — and §6.1 says there is nothing
   to merge it with.

**Ask which of those steps need git. Only 2 and 3 do, and they are vacuous when nobody has a
history.** Steps 1, 4 and 5 are true today, in the shipped application, on a machine with no git
anywhere — publishing does not even have to be a git operation, a file copy onto a share does it.

**So this must not ship only as part of a git feature.** If it did, the users who never get the fix
are exactly the ones §4.3 identifies as the majority — RF designers with no git — and they are the
users with no history to fall back on when it bites. That is the wrong way round, and it is why this
brief is independent of RC-3 and can land first.

Read-only converts a five-step silent chain into **one refusal at the moment of editing**, which is
the only point where the designer still has the context to do something sensible.

---

## 2. What already exists, and what this is not

**R-rc2-1. SL2's read-only is a different question and this brief reuses it rather than repeating
it.** `WorkspaceWritability` (`src/Design/Workspace/WorkspaceWritability.cs`) answers *"can circuitRF
write into this directory?"* — discovered by attempting a write, memoised per root, dropped by
`WorkspaceRootFinder.InvalidateCache`. This brief adds *"should circuitRF write here?"*, which is a
**policy on the reference** and is true even when the filesystem would allow the write.

The two combine as an OR: a document is read-only if either says so. **The behaviours SL2 already
built are the behaviours here** — Save disabled with Save As offered in its place, the mark on the
document, `FileAccessDiagnostics`' vocabulary. Do not build a second read-only concept, a second
disabled-Save path, or a second mark.

**R-rc2-2. This is not a lock server and not a permission system** (§7A.5). Read-only here is
circuitRF declining to write, not the filesystem declining. Someone determined to edit the library's
files outside circuitRF can. The goal is to make the **accidental** case impossible and the
**deliberate** case visible, which is the whole of what is achievable without a server.

---

## 3. The field, and the default that inverts the house rule

**R-rc2-3. `CwsWorkspaceRef` gains one field: whether this reference is editable**
(`src/Design/Workspace/WorkspacePersistence.cs:228`). `File ▸ Reference Workspace…` creates
read-only references. Editable is a per-reference, explicit choice.

**R-rc2-4. An absent field means read-only — including on every `.cws` written before this existed —
and that deliberately inverts the rule the sibling field states.** `CellsOnly`'s own header, three
lines away, says the house instinct for an absent field: *false on every entry written before this
existed, which is the old behaviour.* `CwsTreeViewState.ReferencedCells` reasons the same way.

**That instinct is wrong here, and the new field's header must say so where a reader will hit it.**
The old behaviour is not a preference anyone set — it is the hazard. The cost of inverting is one-time
friction for anyone who was editing through a reference: a refusal, carrying its remedy, and a
per-reference override one click away. The alternative is that the workspaces most likely to have
accumulated the practice are the only ones never protected from it.

**R-rc2-5. An editably-referenced workspace is marked wherever it appears**, and a document opened
from one carries the mark in its tab. Two designers working that way in one library is exactly the
concurrent-edit problem §6.2 describes and circuitRF is not solving it — so the state must be visible,
not inferable.

---

## 4. The refusal, and the path it points at

**R-rc2-6. The refusal carries the remedy as an invokable action, not as advice.** The remedy needs no
new mechanism: *the library is a workspace; open it as your own workspace to edit it, with its own
history.* `IMessageSink` already carries a message with a single invokable action
(`src/Ui/Messages/IMessageSink.cs:29`). **"Open workspace B" is that action.**

§7A.3 names this as one of two gaps the architecture deliberately left to a brief. Without it the
remedy is correct advice that still costs the designer a File ▸ Open and a hunt for the folder;
with it the supported path is one click, which is what turns a refusal into a route.

**R-rc2-7. The cell becomes editable in the OTHER window, and not in this one.** The mechanism is
already built: `ActivateIfOpenInAnotherWindow` (`WorkspaceViewModel.cs:9155`, MW1 R-mw1-10) routes an
open request for a file open elsewhere to *that* window, and its own header already names this exact
case — a cell referenced from another workspace must not become editable through the window that
merely references it.

From the designer's point of view they asked to edit the cell and are now editing the cell. What they
get in addition is the true answer to *where does this cell actually live*, which they need anyway.

**Why the other route is not merely different but harmful, and belongs in the code comment:** if the
referencing window's view became editable, one file would have two editors, two undo stacks and two
dirty flags. Edit in one, edit in the other, save both — and the second save silently discards the
first. No conflict, no warning, both windows showing "saved", and nothing to merge it back from. That
is **worse** than the cross-workspace divergence this brief exists to prevent, because it is the same
designer losing their own work minutes apart in one session. **One file, one editor, across every open
window** is the invariant, and it is not specific to references: two workspaces can reach one cell
folder by other routes.

**R-rc2-8. The existing message is worded for the wrong case and is reworded.** §7A.3's second gap.
`ActivateIfOpenInAnotherWindow` says the file is *already open elsewhere — shown there rather than
opened twice*, which is right when the designer had it open and forgot. **Arriving from a referenced
cell they never opened, the true reason is ownership, not duplication.** The message says the cell
belongs to the other workspace and is being edited there. Both wordings are needed; which one appears
depends on how the request arrived.

**R-rc2-9. The read-only mark does not depend on which windows happen to be open.** It describes this
workspace's *relationship* to the other one — this workspace does not own that content — and that
stays true while the designer edits it over there. A mark that flickered with window state would
produce behaviour nobody can reason about.

---

## 5. Gates

1. **The default.** A `.cws` with a `ReferencedWorkspaces` entry and no editability field: the
   reference is read-only. A fixture written before the field existed, byte for byte, is the case that
   matters (R-rc2-4).
2. **The refusal fires on the edit, not on the save.** Assert the point of refusal is the attempt to
   modify, not the write — the whole value is that it arrives while the designer still has context.
3. **The refusal carries an invokable action** (R-rc2-6), and invoking it opens the owning workspace.
   Assert the action exists on the posted message, not only that the text mentions it.
4. **Routing.** With both workspaces open, an edit request from the referencing window activates the
   owning window and its tab. With only the referencing window open, the refusal's action opens the
   other one and then routes.
5. **The save lands in the owner.** The document is the owning workspace's, dirties its project tree,
   and is written to its own path — assert the referencing workspace wrote nothing at all.
6. **Editable references still work**, are marked in the tree and in the tab (R-rc2-5), and the mark
   does not change when the other workspace is opened or closed (R-rc2-9).
7. **The two wordings are distinguishable** (R-rc2-8): a file the user had open elsewhere, and a
   referenced cell they never opened, produce different sentences.
8. **No git anywhere.** Every test in this brief passes on a machine with no git installed, and
   nothing in the implementation reaches for one. This is the gate that keeps the brief honest about
   §7A.2 not being a revision-control feature.

---

## 6. Documentation (§10B.2 scenario 10)

*"I need to fix a cell in a library I reference."* The walkthrough: open the library as a workspace,
edit it there, save. And the two consequences that surprise people:

- **the change lands in the library's history, not the design's** — restoring a design to last Tuesday
  restores that design's files, and the referenced cell is whatever the library has;
- **a pinned reference deliberately does not move until asked** — which is RC-9's feature and is
  mentioned here only as the forward pointer, because *"I just edited it and my design didn't change"*
  is a thing to explain rather than a thing to let people discover.

**The §10B.1 row this brief makes true:** *anything in a referenced workspace — not kept in this
workspace's history, not recoverable from it; that workspace has its own.* It belongs here rather than
in RC-9 because the read-only default is what makes the ownership boundary visible in the first place,
and the row is the same statement in the normative table's voice.

`docs/design/workspace-and-project-tree.md` §5B/§5C gains the read-only default, since that is where
referenced workspaces are specified and where the next reader will look.

---

## 7. On completion

Findings to `src/Design/RESOLVED.md` (the field and its inverted default) and `src/Ui/RESOLVED.md`
(the refusal, the routing, the reworded message) — **never to a `CLAUDE.md`**.

**Report, do not silently absorb:**
- Any other route by which two windows can reach one cell folder (R-rc2-7's invariant is broader than
  references) — that is a real finding whether or not this brief fixes it.
- Whether SL2's disabled-Save path took the policy read-only without modification (R-rc2-1). If it
  needed a second path, say exactly why.

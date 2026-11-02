# Sonnet Brief — RC-8: archiving a workspace with its history

**Read `brief-revision-control-0-overview.md` first.** The architecture is
`docs/design/revision-control.md` §9A, §5.2a, §12 Q6 and §12 Q17. **Depends on RC-5** (which already excluded `.git`
from the archive — that exclusion is not this brief's to build) **and RC-7** (a history worth offering,
and the vocabulary to describe it).

**Scope: one checkbox, off by default, and the sentence that makes ticking it safe.** The smallest
brief in the series and the one whose failure mode cannot be undone.

---

## 1. Why the default is a safety property rather than a preference

Every other failure the architecture guards against is a **loss**: a checkpoint not taken, a restore
point thinned early, a history switched off. Those are bad, and the mitigating fact is that the design
itself is ordinary files on disk and survives all of them.

**Including history in an archive risks the opposite kind of failure, and it is unrecoverable.** Git
history contains every earlier version of every tracked file — **including files that are no longer in
the workspace at all.** A designer who imported a customer's GDSII, finished with it, deleted it, and
then archived the workspace for a **different** customer would ship that GDSII. Nothing in the visible
file tree would show it. Nobody would find out until someone else did.

That is design-IP disclosure, it is possibly a contractual event, and **it cannot be undone by anyone
at any later time.** Hence the rule:

> **R-rc8-1. When a default can be wrong in two directions, it points away from the irreversible one.**

**R-rc8-2. The checkbox is off by default**, and it is stated in the architecture rather than left to
the dialog precisely because it is a safety property.

**R-rc8-3. Including history is genuinely valuable and is not discouraged.** A design handed to a
partner *with* its history is a far better handover than a snapshot, and internally it is usually what
you want. **The point is only that it must be chosen, not inherited.** The dialog must not read as a
warning against its own feature.

---

## 2. What the two states produce

| | archive contains | recipient gets |
|---|---|---|
| **history excluded** *(default)* | the workspace's current files, exactly as today | a working workspace with no history — **completely normal**: it opens, everything is present and current. Switching revision control on starts a fresh history of their own. |
| **history included** | the same files **plus the repository** | the same workspace, plus every earlier version of every file it ever tracked, and the ability to restore any of them |

**R-rc8-4. The `.cwsuser` is never archived, in either state.** It is one person's panel arrangement;
the recipient opens on the default, which RC-1 R-rc1-4 already requires to work. **The exclusion itself
is RC-1's, not this brief's** (R-rc1-15a, moved there in rev 4 — RC-1 creates the file three stages
before this brief lands, and it must not be archived in the interval). This brief inherits it, and gate
7 is a regression check rather than the thing that builds it.

**R-rc8-5. Extracting an archive that contains history yields a repository circuitRF created at the
workspace root** — the first row of RC-6's `rev-parse` table, adopted without ceremony. The recipient's
own checkpoints continue on top of the sender's history, which is the entire point of having sent it.

**R-rc8-5a. The archive is the ONLY one of the three journeys that carries restore points, and the
dialog may say so** (§5.2a). An archive copies the repository directory, so it takes RC-5's
per-checkpoint references with it; **a clone does not** (git's default fetch takes branches and tags,
and nothing under a private namespace), and **a Save Workspace As copy carries no history at all**
(RC-5 R-rc5-19). That asymmetry is deliberate rather than a defect for RC-9 to repair, and it is worth
one line in R-rc8-7's computed summary: what the recipient gets here is strictly more than what a clone
would give them.

**R-rc8-5b. A recipient who extracts on another machine hits `safe.directory` if the extraction left
foreign ownership** — RC-3's translated failure (R-rc3-4), not git's wording. This is the most likely
real-world first impression of an archive-with-history, so verify it rather than assuming the extractor
already normalises ownership.

---

## 3. What the dialog must say, and the one thing it must compute

**R-rc8-6. The label describes the content, not the mechanism.** A checkbox reading *"include git
history"* fails on two counts: it is git vocabulary, and it does not tell the user what they are about
to send.

**R-rc8-7. When the box is ticked, the dialog states what including it actually adds — computed, not
generalised:**

- **how much larger** the archive becomes;
- **how many earlier versions** it carries;
- and the item that does the real work: **how many files are in the history that are no longer in the
  workspace, and what they are called.**

**R-rc8-7a. The enumeration walks every reference the archive will carry, and that is MORE than the
branch** (§9A.3, new in rev 4). R-rc8-5a makes this the one journey that takes RC-5's checkpoints with
it, because it copies the directory — so the archive's history is the branch **plus every per-checkpoint
reference** in circuitRF's namespace. And a checkpoint is `commit -a`-shaped by design (R-rc5-7): it
captures files the designer never deliberately committed and may never have opened. **A file that lived
only inside checkpoints is therefore in the archive and absent from a branch-only enumeration** — which
is exactly the population this warning exists for, since a deliberately committed file is one the
designer already knows about. Walking the branch alone produces a warning that is *nearly* true, and
R-rc8-11 is the paragraph explaining why that is worse than none.

**R-rc8-8. That last item is the single most effective warning available**, and it is cheap to obtain —
git can list every path ever deleted. It converts an abstract worry into three filenames the designer
recognises. **Someone about to leak a file will almost always recognise it by name; nobody recognises
"the repository contains historical objects".** If the list is long, show the first few and the count;
never replace it with a number alone.

**R-rc8-9. Pack before archiving with history included — the ONE deliberate exception to R-rc3-14**,
which otherwise keeps packing behind a workspace close *(the two requirements did not name each other
before rev 4)*. RC-3's overhang means an unpacked repository can be ten times its real size, and an
archive is exactly where that surprises someone; it also makes R-rc8-7's size figure honest. It runs in
front of the user because **this is the one time that is the correct place for it** — the user asked for
the archive — so **report it as progress, never let it read as a stall** (`IMessageSink.BeginProgress`
already exists for this). R-rc3-1b's rule that packing yields to another process holding the workspace
still applies. **And packing never reclaims** (§5.6a, rev 5): the archive carries every thinned restore
point's objects too, which the enumeration of R-rc8-7a must therefore walk — RC-6's journal is how it
finds them — and the size figure includes them. A designer who wants a smaller archive has RC-4's
reclaim action, which asks first; this dialog does not offer it.

**R-rc8-10. Referenced cells brought in by the archive scan carry no history.** They come from another
workspace with its own repository, and the one-workspace-one-history rule holds in this direction too:
**an archive carries files, never someone else's repository.**

---

## 4. The third option, excluded

**R-rc8-11. "Include only the last N versions" / "history from this date forward" is not built.**

It sounds like the best of both. **It is history rewriting**: it produces a repository whose identities
do not match the original, so the recipient can never compare against the sender's, and RC-9's pins do
not resolve across it.

Worse, **it invites exactly the wrong belief.** A user who chose "last 10 versions" to avoid sending
something will assume the something is gone — and a filter that silently kept one referenced object, or
missed a path renamed before it was deleted, would be a leak the user had explicitly tried to prevent
and been told was handled. **A guarantee that is nearly true is worse than no guarantee**, because the
near-truth is what people act on. The two honest states in §2 are all this offers.

---

## 5. Gates

1. **Default off**, on a workspace with a history, with no prior state remembered that could flip it.
2. **Excluded produces exactly today's archive** — byte for byte against the pre-RC-5 archive of the
   same workspace, which is the strongest available statement that the default changed nothing.
3. **Included round-trips**: archive, extract, open, and assert every restore point and every commit
   from the sender is present and restorable (R-rc8-5) — **restore points included**, which is what
   distinguishes this journey from a clone (R-rc8-5a).
4. **Extraction on a foreign-owned tree reports circuitRF's sentence**, not git's (R-rc8-5b).
5. **The deleted-file list is real** (R-rc8-7, R-rc8-8): a fixture that adds a file, commits, deletes
   it, commits, then archives — assert the file is **named** in the dialog's text. Anonymize the fixture
   to the *shape* of a filename; a real customer-shaped name must never enter the repo.
5a. **A file that only ever existed inside a checkpoint is named too** (R-rc8-7a): create it, take a
   **checkpoint** (never an explicit commit), delete it, checkpoint again, then archive with history —
   assert it is named. **A branch-only enumeration passes gate 5 and fails this one**, which is the
   whole reason the gate is separate.
5b. **A file that only ever existed inside a THINNED checkpoint is named too** (R-rc8-9, rev 5): repeat
   5a, then let RC-6 thin that checkpoint, and assert the file is still named — its objects are still in
   the directory the archive copies, and a live-references-only enumeration would miss it.
6. **The size figure is post-pack** (R-rc8-9): assert packing ran before the figure was computed, and
   that the figure matches the archive actually written.
7. **No `.cwsuser` in either state** (R-rc8-4).
8. **No third option exists** (R-rc8-11): source-scan for a version-count or date filter on the archive
   path and fail on a hit, comments stripped.
9. **A referenced workspace's repository never enters the archive** (R-rc8-10), even when the reference
   is editable.

---

## 6. Documentation (§10B.2 scenario 11)

*"I'm sending this workspace to a customer."* What the archive contains, what including history would
additionally contain, and the sentence that matters: **a file deleted from the workspace is still in
the history.**

**The §10B.1 row this brief owns:** *history, in a workspace archive — not kept by default; the sender
still has it.* And per R-rc8-5a it is the one journey that also carries restore points, which the row
should say, because a designer comparing an archive with a clone will otherwise assume they are the
same.

**This is the one scenario in the whole documentation where getting it wrong cannot be undone**, and it
is written that way.

---

## 7. On completion

Findings to `src/Ui/RESOLVED.md` — **never to a `CLAUDE.md`**.

**Report, do not silently absorb:**
- What the deleted-path enumeration actually costs on a large history, and whether it is fast enough to
  run when the box is ticked rather than on a delay (R-rc8-8). If it is not, say so — a computed
  warning that arrives after the user has clicked through is not a warning.
- **What the enumeration cost once it walked the checkpoint references as well** (R-rc8-7a) — that is
  strictly more history than a branch walk, and R-rc8-8's own question about whether the warning arrives
  before the user clicks through is asked against the larger number, not the smaller one.
- Whether RC-5's `.git` exclusion held unmodified once the include path existed. A single exclusion
  serving both states is the correct shape; two would be the bug.

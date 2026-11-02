# Brief — Save Point and Keep a Version, disabled when there is nothing to record

**Status:** not started, and **deliberately not implemented on the spot** — the obvious version has a
failure mode worse than the bug. Investigation 2026-09-08 against `main` @ `ff0dfc37`.

**Reported (owner, 2026-09-08):** with nothing changed, both keep actions still open their dialog and
take a title, and then no entry is created. They should be greyed out instead.

---

## 1. The bug is real and the diagnosis is short

`KeepThisState` and `KeepThisVersion` (`WorkspaceViewModel.Revision.cs`) show their dialog first and
record second. The tree test that declines to write a duplicate lives in `GitCheckpoint.Record` —
after the tree has been computed, which is after the dialog. So the refusal (`NothingToRecord`, and
`HistoryMessages.NothingChangedSinceLastVersion`) is correct, correctly worded, and arrives after the
designer has already done the work of naming something.

## 2. Why it is not a one-liner, and the two traps

### Trap 1 — the cheap signal is not free, and the free signal is not cheap

"Would this record anything" is *the computed tree differs from the newest entry's tree*. Computing
it is `git add --all` + `write-tree` over the whole workspace, and `GitCheckpoint.Record` **deletes
the private index at the start and end of every call on purpose**, so nothing is cached and every
call re-hashes every file. That is the cost already measured as `LastCloseCheckpointMs`. Running it
on every panel refresh puts a whole checkpoint's work in front of somebody who is only looking at the
panel — which is exactly the cost RC-11 removed when a filter checkbox was found to be starting a
dozen git subprocesses. `HistoryBrowser.CompareWithWorkspace` has the same shape and the same cost.

There **is** a free signal, and it is already in the codebase:
`WorkspaceHistoryService.CircuitRfWroteAFileThisSession`, set by every path that writes into the open
workspace. Made per-boundary rather than per-session — reset it wherever a boundary records — it
answers *has anything been written since the last entry* with no git at all, and it errs in the safe
direction: a write that happened to produce identical bytes leaves the button enabled and the dialog
still declines, which is the bug's current behaviour and no worse.

### Trap 2 — the dangerous case, and it is the whole reason this is a brief

**Neither keep path saves dirty documents first.** `KeepThisState` runs the dialog and calls
`History.TakeSavePoint` directly; what is recorded is what is on disk. So a designer with an unsaved
schematic has *nothing written since the last boundary* — the free signal says false, the button greys
out, **and the one action they reached for is not available at the moment they most want it.** §1.4 is
explicit that a designer who believes they can get back to something and cannot is the failure this
whole feature is written against, and a greyed control is indistinguishable from a feature that was
never built (R-rc6-8's own argument, one control over).

Note the same fact bites today, silently and worse: a save point taken with unsaved work **records the
previous content under the title the designer just wrote.** They believe they kept this afternoon;
they kept this morning. That is a defect on its own account and it does not depend on this brief.

## 3. Recommended shape

Do all three, in this order — the first is the one that makes the second safe:

1. **A keep boundary saves the workspace's dirty documents first.** The close boundary already does
   exactly this and R-rc5-21 states why (an entry that keeps a workspace file one save out of date is
   invisible when wrong). An explicit save-point has the same claim, and stronger: the designer is
   naming *this* state. Reuse the existing save path — do not write a second one — and if anything
   refuses to save, do not record and say so.
2. **The enabled state comes from the free signal**, per boundary, not per session: enabled when
   anything has been written into the workspace since the last recorded entry, and enabled when there
   is no history yet at all. With step 1 in place, unsaved work is no longer a case where the signal
   is false.
3. **The button is disabled, and the reason is on it** — a tooltip saying the current state is already
   kept. A control that is greyed with no reason given is the shape R-rc6-8 objects to; a control that
   is greyed *and says why* is the one that reads as finished rather than broken.

**Do not disable on the held / off / no-git states.** Those already have their own answers and they
are deliberately not this one: held stays visible and refuses out loud, and no-git hides the whole
affordance. Conflating "nothing to record" with "not allowed to record" would undo both.

## 4. Gates

Counters and states, not clocks:

- a workspace with a boundary just taken and nothing written since reports both actions disabled;
- one write into the workspace re-enables them, with no git invoked — asserted by there being no
  repository at all in the fixture;
- **a workspace with an unsaved open document reports them ENABLED**, which is trap 2 held shut;
- taking a save point with an unsaved document records the document's CURRENT content, which is
  step 1 held shut;
- the held and off states are unchanged: visible, enabled, and refusing with their own sentence.

## 5. On completion

Findings to `src/Ui/RESOLVED.md` — **not to any `CLAUDE.md`**. Record trap 2's silent half separately
from the reported bug; it was found while investigating this and it is the more severe of the two.

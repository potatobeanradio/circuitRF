# `src/Ui/Layout/Lvs/` — findings from brief-lvs-12-gui.md

The LVS panel, its markers, cross-probing and waivers. Everything here consumes a finished
`LvsRunResult`; nothing computes a finding (R-lvs12-5a).

---

## 1. A record-valued `[ObservableProperty]` silently swallows the refresh that matters most

**Found by the gate, not by reading it.** `LvsResult` was an ordinary `[ObservableProperty]`, and
the generated setter equality-checks before notifying. `LvsRunResult` is a **record**, so that check
is STRUCTURAL — and two structurally equal results are routinely not the same answer:

- **Un-waiving the last waiver.** `LvsWaivers.Apply` returns the list unchanged when there is
  nothing to apply, so the new result compares equal to the pre-waiver one. The setter was skipped,
  the rows were never rebuilt, and the orphan list went on showing a waiver that had just been
  removed.
- **Re-running after an edit that was undone.** Same findings, same order, equal record — so
  `IsLvsStale` was never reset and the panel went on refusing to cross-probe against a result it
  had just recomputed.

`LvsResult` is therefore hand-written and notifies **unconditionally**. The lesson generalises: an
`[ObservableProperty]` whose type is a record or a value-equal struct is de-duplicating on *value*
when the view model means *identity of the answer*. Every other result property in this editor
(`DrcResult`) is a record too and is safe only because it is cleared to `null` on every edit, which
is a different mechanism rather than an absence of the problem.

## 2. The LVS result is MARKED stale, not cleared — and that is the opposite of DRC's rule

`ClearDrcResultOnEdit` drops a DRC result outright, because a DRC result is only ever about
geometry and geometry that moved makes it meaningless. An LVS result also carries the
**correspondence**, which is what cross-probing runs on and is the part a user still wants after
nudging a pad. So `MarkLvsStaleOnEdit` sets a flag, the panel says so, and the probe refuses
(R-lvs12-3d). Both hooks are in the same `Model.Changed` handler in `LayoutEditorViewModel.cs` and
they deliberately do different things — a future reader tidying them into one will break this.

## 3. The panel compares what is ON DISK, and the staleness mark is what makes that honest

The verb takes a cell folder, and R-lvs12-1c requires both surfaces to pass the same arguments, so
`RunLvs` calls `LvsRun.Run(cellDir, …)` — which re-reads both views. A document edited since is not
what was compared. That is the same sentence R-lvs12-3d already required the panel to carry, so one
mark serves both facts; it is not two bugs' worth of wording sharing a flag by accident.

**The argument had a hole, closed on review (2026-09-21): a fresh result CLEARS the mark.** So the
one sequence the mechanism did not cover was the one it exists for — edit, press Run, and be told
the artwork implements a drawing you have already changed, with the banner gone because the result
was new. `RunLvs` now reads `IsDirty` before the run and re-raises the mark after it, with its own
sentence: the generic one says the design changed since the comparison, this one says the
comparison read the saved files and names saving as the fix. One flag still, because
cross-probing is refused for the same reason in both cases.

## 4. A device path carries two decorations neither editor's own part list has

`R1[0,2]` (an array element) and `U3/M1` (a part inside a placed cell) are paths, not names. The
layout editor holds `R1` and `U3`. `LvsCrossProbe.Same` strips both, which makes clicking a finding
inside a placed module select **the placement** — the right answer, because brief 9 reports a
cell's findings once under its first placement and opening the cell is how its own parts are
reached.

## 5. Both sides are un-reduced before the probe

The pairs in `LvsComparison.Devices` are over the **reduced** netlists, so a merged four-finger
device is one pair standing for four parts. Probing through `LvsDevice.Group` on both sides is not
a nicety: without it, clicking one finger of a merged FET finds nothing at all.

## 6. The export gate's default is the one place this deliberately differs from DRC

`CheckLvsOnExport` defaults **off** (R-lvs12-1e). LVS needs a schematic and an artwork-only export
is a legitimate thing to do — a fab package for a board whose drawing lives elsewhere, a footprint
library, a panel of test coupons. On by default, every one of those would meet a comparison that
could not run and a dialog about it, which is the prompt people learn to dismiss unread; once
learned, it gets dismissed on the export that mattered too. The box is present in Settings ▸ General
and in the gate dialog, because off with the box visible is not the same as absent.

## 7. Waivers are dirty-but-not-undoable, following `DrcWaiver`'s ACTUAL rule

R-lvs12-4g says a waiver is made "through the ordinary undo stack … exactly as a DRC waiver is",
and a DRC waiver is explicitly **not** on the undo stack (`LayoutView.DrcWaivers`: putting a review
judgement on the shape-editing undo stack would let Ctrl+Z after an unrelated edit silently revoke
it). R-lvs12-4a's "DrcWaiver's rules verbatim" is the instruction that was followed. What
R-lvs12-4g is actually about — and what IS implemented — is the distinction the note needs: the
**user** edits their document and saves it; the **run** reads the list and never writes it back.

# Brief 6 — the Review panel: thresholds, results, markers, accept with a reason

**Series:** [Layout Review](brief-review-0-overview.md) · **Tag:** `R-rev6-n` · **Phase:** UI
**Area:** new `src/Ui/Layout/Review/ReviewPanelViewModel.cs`, `src/Ui/ViewModels/Dock/ReviewTool.cs`,
`src/Ui/Views/Review/ReviewToolView.axaml(.cs)`, the layout editor's menu and canvas overlay, `tools/UiSnapshot`
**Depends on:** 2, 3, 4, 5 · **Blocks:** 7

---

## 0. What this brief builds

The surface a reviewer works in. It is modelled on the Impedance panel (brief-impedance-3), which is modelled on the DRC
panel, and reuses both wherever they already do the thing: docking, cross-probe to the canvas, the accept dialog, the
marker overlay. The panel computes nothing. Every number it shows comes from `LayoutReview.Run` (brief 2).

## 1. Requirements

**R-rev6-1 — Where it opens.** Design ▸ Review Layout…, beside Design ▸ Check Design Rules, and the panel's own
dockable tool (`ReviewTool`, registered with `CircuitRfDockFactory` like `ImpedanceTool`). A torn-off layout window
reaches it the same way it reaches DRC.

**R-rev6-2 — Checks.** One row per check in the overview's order: enable checkbox, title, and its thresholds inline, each
an editable length in the layout's display unit with its unit shown (a count or a density as a plain number with its
unit). A threshold that differs from the default is visibly marked, and the row has a Reset. The impedance row shows its
target, bands and scope read-only with a button that opens the Impedance panel (brief 5 R-rev5-5). Edits are saved on
the layout (`LayoutView.Review`) and mark it dirty; they are not undoable, on `DrcWaivers`' terms.

**R-rev6-3 — Run.** Run / Cancel, with the run's per-check progress line. Runs off the UI thread. The result replaces the
previous one; a run never mixes in findings from the last.

**R-rev6-4 — Results.** A tree: check (with its count, accepted count, or *not run* and why) → layer → finding. A check
with no findings says so ("No findings"); a *not run* row shows its sentence and no children. The finding rows show the
sentence and the measured value. **The tree virtualises**: board C is expected to produce hundreds of findings in a single
check and the panel must stay responsive with thousands (R-rev6-8 gates that).

**R-rev6-5 — Cross-probe.** Selecting a finding zooms the canvas to its marker with margin and highlights it; selecting
copper on the canvas while the panel is open selects the findings whose markers it touches. Markers are drawn on the
system overlay the DRC panel uses (reuse its overlay type and renderer; add a colour for review findings, distinct from
DRC's). Markers draw only while the panel is open, and at a zoom where markers would overlap they cluster into a
counted badge as brief-impedance-9's report does (share that clustering, do not write a second one).

**R-rev6-6 — Accept with a reason.** A finding's Accept opens the accept dialog the Impedance panel uses (reason required),
writes a `ReviewAcceptance` (an impedance finding writes a `TraceImpedanceAcceptance`, brief 5 R-rev5-4), and moves the
row to ACCEPTED without re-running. Un-accept reverses it. "Show accepted" toggles the accepted rows; stale acceptances
(matching nothing in this run) are listed under their own heading with a Remove.

**R-rev6-7 — Messages.** One line in the Messages panel per run, on `DrcRunReport`'s terms: "Review: 3 checks with
findings (41 findings, 6 accepted), 1 not run — see the Review panel."

**R-rev6-8 — UiSnapshot.** Extend `tools/UiSnapshot` to render the Review panel from a seeded result (it already renders
the Impedance panel). The gate pictures: the checks list with one edited threshold; a result with a *not run* row, an
accepted finding and a check with no findings; and a 5,000-finding seeded result, proving the tree virtualises (count the
realised row containers, a COUNTER, not a timing).

## 2. Tests

`tests/Ui.Tests/Review/ReviewPanelViewModelTests.cs`: editing a threshold writes only that threshold to the layout;
Reset removes it; Accept writes the right kind of acceptance for a review finding and for an impedance finding;
selecting a finding requests a zoom to its marker's bounds; a cancelled run shows the finished checks and marks the rest
*cancelled*. Run only that class, plus the UiSnapshot gate by hand.

## 3. Docs

`docs/user/src/reference/layout-editor.md`: a "Layout Review" section covering what it is (a reviewer's aid, not DRC),
each check in one paragraph with its thresholds and defaults, accepting a finding, and the impedance row's link to the
Impedance panel. Say what each check is for in a reviewer's terms; do not explain to a board designer why a via in a pad
is a problem.

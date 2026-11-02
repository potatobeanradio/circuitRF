# Brief 28 — the pads follow the layout

**Series:** [railRF](brief-railrf-0-overview.md) · **Tag:** `R-rail28-n` · **Phase:** defect
**Area:** `src/Ui/RailRf/RailRfViewModel.Board.cs` (`NotifyArtworkChanged`),
`src/Ui/RailRf/RailRfViewModel.TurnedParts.cs` (`RefreshBoardPads`), `src/Design/RailRf/RailArtwork.cs` (`PadsFor`)
**Depends on:** field report 4 (commit `0cbe6a48`) · **Blocks:** nothing

---

## 0. The defect

`NotifyArtworkChanged` runs on every edit in the layout window next door. It re-flattens the SHAPES
and rebuilds the parts table, but it never re-reads the PADS. `Board.Pads`, `Board.NetPoints`,
`Board.Nets` and `Board.TurnedParts` therefore describe the placements as they were when railRF
opened the board.

So a footprint moved, rotated, added or deleted in the layout while railRF is open leaves railRF
seeding its net walks from where the pins USED to be. The net preview, the reference measurement, the
extraction and the turned-parts reading all answer for the old placements, and nothing says so.
Turning a part by hand in the layout (the natural response to the new turned-parts note) is the
commonest way to hit it: the note keeps listing a part the user has already fixed.

It was left alone deliberately in field report 4. `PadsFor` builds a galvanic partition, and one of
those per keystroke is the shape R-rail19-2c forbids. `RefreshBoardPads` exists and is called only by
the Turn gesture.

## 1. `R-rail28-1` — re-read the pads after an edit that can move a pin, debounced and off the UI thread

- **Only an INSTANCE edit can move a pin.** `LayoutChangeInfo` already distinguishes
  `InstancesOnly`; a shape-only edit changes copper, not pins, so it needs the existing re-flatten
  and not this. Confirm which change kinds reach `NotifyArtworkChanged` and key on them. Do not
  re-read on a shape edit unless a stamped `Net` changed, which renames pads on the stamped path.
- **Debounced.** A drag emits many changes, so settle on the last one (a few hundred ms). The
  answer that lands must be for the model as it is when the job STARTS, and a superseded job's result
  is dropped. That is `BeginCopperJob`'s contract; follow it rather than inventing a second one.
- **Off the UI thread.** Capture the view, technology, netlist and flattened shapes on the UI
  thread, run `PadsFor` in the job, and publish through the backing field the way `RefreshBoardPads`
  does, so the canvas and viewport are not rebuilt.
- **One refresh funnel.** `RefreshBoardPads` becomes the synchronous core that both the Turn gesture
  and the debounced path call. Two copies of "what a pad refresh re-states" is the
  `RebuildAvailableNets` scar again.
- **The strip says it.** While a re-read is pending, the status strip reads that the board's parts
  are being re-read, as `IsReadingCopper` already does for the copper.

## 2. `R-rail28-2` — what a pad refresh must NOT do

- It must not clear the user's pick-list selection when the selected net still exists.
- It must not clear results the edit could not have changed. Pins moved means seeds moved, so the
  results do go (as `NotifyArtworkChanged` already does); state this in the code rather than
  re-deriving it.
- It must not run a partition when the board has no instances.

## 3. Tests (minimal, one per claim)

1. A part moved in a live layout session moves its pads in railRF after the debounce settles (drive
   `NotifyArtworkChanged` with an instance edit; do not assign `Board`, which is a record and
   short-circuits on an equal value, the field-report-3 trap).
2. A part turned by hand in the layout leaves `PartsReadAsTurned` without the Turn button.
3. Ten instance edits in one burst cost ONE `PadsFor` (count it with an internal counter, as
   `NetWalksPerformed` does; no timing).
4. A shape-only edit costs none.

Run only `RailRfFieldReport4Tests` and the new class. Not the full suite.

## 4. On completion

Findings go in `src/Ui/RESOLVED.md`, never in any `CLAUDE.md`.

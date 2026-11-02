# Brief DFR31-5 — Small fixes from the field test: overlay, port labels, a stuck 3D view, a log file, progress text

**Series:** designer feedback round 31 (2026-10-29). · **Tag:** `R-dfr31-5-<n>`
**Area:** per item below. Each item is independent; land and report them one at a time.
Findings go in `src/Ui/RESOLVED.md` (and `src/Design/RESOLVED.md` for item 5).
**Tests:** one per claim, in the test class next to the code each item touches.

---

## 1. The impedance overlay stays on the canvas after the Impedance panel is closed (R-dfr31-5-1)

`LayoutEditorViewModel.TraceImpedance.cs` holds the report until the next run, by design (R-imp3-2e), and draws it
while `ShowImpedanceOverlay` is true. Nothing ties the overlay to the panel. Close the panel and the coloured runs and
junction markers stay drawn over the artwork, with no visible control to turn them off.

**Fix:** draw the overlay only while the Impedance panel is open (docked or floating, but not closed). Keep the
report: reopening the panel shows it again, and Export PDF still reads it. `ShowImpedanceOverlay` keeps its meaning
while the panel is open.
**Test:** closing the panel removes the overlay from the canvas's overlay list, and reopening it restores the overlay
without a re-run.

## 2. Port labels are drawn 1 mm tall on a PCB technology (R-dfr31-5-2)

A port label placed on the shipped 10-layer PCB technology gets `DefaultLabelHeightDbu` = 1,000,000 (1 mm). On a
cropped 6 × 5 mm board, six such labels cover a third of the board and hide the lines the ports sit on (field-test
screenshots). Font height is the wrong unit for a port marker. A port label marks a point on a 0.3 mm line.

**Fix**, choosing in this order:
- (a) A NEW port label takes its height from the conductor it lands on: twice the line width, clamped to the
  technology default. This applies to new labels only; a stored `Height` is never rewritten.
- (b) Lower `DefaultLabelHeightDbu` in the shipped PCB technologies to 250 µm (or a little less), and check that the
  shipped examples still read well (render them).
State in the RESOLVED entry which one was chosen and why. The Properties panel's height field already lets a user
shrink existing labels; add a multi-selection edit if it does not apply to several labels at once.
**Test:** a port dropped on a 300 µm line on the 10-layer technology is at most 600 µm tall.

## 3. The 3D view can hang on "Building the 3D view…" after the GPU context is lost (R-dfr31-5-3)

A crash-report trail from Windows (x64, .NET 10.0.10) shows a burst of unobserved
`COMException: OpenSharedResource failed` and `Avalonia.Platform.PlatformGraphicsContextLostException` on the UI
thread. They arrived after the machine was moved between docking setups. Afterwards, opening a `.c3d` showed the
"Building the 3D view…" bar (`C3dEditorView.axaml`, bound to `Viewer.IsBuildingFirstScene`) indefinitely, until the
app was restarted. Separately, one first "Create 3D View from Layout" opened empty, and a second attempt worked.

**Fix:**
- Find where the 3D viewer's first-scene task awaits the GPU surface. On a lost context, recreate the surface and
  rebuild once. If that fails, fall back to the software renderer, post one Messages line saying so, and clear
  `IsBuildingFirstScene`.
- `IsBuildingFirstScene` must be cleared on EVERY exit path of the build, including faulted and cancelled ones.
- After 30 s with no progress, replace the bar with "The 3D view did not finish building — Retry", and make Retry
  rebuild it.
- Observe the faulted tasks, so the crash trail records one line per event rather than forty.
**Test:** a viewer whose first-scene build throws a context-lost exception ends with `IsBuildingFirstScene == false`
and a Messages line. The empty-first-view case may be the same fault; say in the RESOLVED entry whether the fix
covers it.

## 4. A Messages log file (R-dfr31-5-4)

The Messages panel is the only record of a day's work, and it is gone on exit. The field test asked whether a running
log exists, to send with a report. Write every Messages line, with its date, to
`<state dir>/logs/messages-YYYY-MM-DD.log` (the same state directory as `crash-reports`). Append-only, flushed per
line, and kept for 14 days. Add a **Open Log Folder** item to the Messages panel's menu. No new settings.
**Test:** a posted message appears in today's file, and a file 15 days old is deleted on start.

## 5. Plainer progress text during an adaptive sweep (R-dfr31-5-5)

During a driven Palace run, the status line reads, for example,
`Sweep: sampling, excitation 1 of 6 · error 2.6e-5 → tolerance 1.0e-3 (log scale)` (`PalaceLogProgress.cs`,
`EmergeLog.cs`). Nothing says what "error" measures, or which direction counts as done. The wording should say it
plainly, for example: `Adaptive sweep, port 1 of 6: model change 2.6e-5 (done below 1.0e-3)`. Put the meaning in the
progress bar's tooltip: "How much the frequency model still changes when a new frequency is solved; the sweep for this
port is finished once it stays below the tolerance." Check the numbers against `PalaceStageTracker` before writing the
words, and do not describe the convergence direction the wrong way round.
**Test:** the formatter's output for a sample state, matched exactly.

## 6. Vias reported "wholly off the board" after a crop with a solve region (R-dfr31-5-6, investigate first)

After Crop to Rectangle (which reported "1 via is kept with the new edge through its drill") and a drawn solve region,
the run listed 56 vias as lying "wholly off the board and … not in the 3D problem". Either the crop leaves vias outside
the new outline behind (a crop defect), or the note describes vias outside the solve REGION as off the BOARD (a
wording defect). Reproduce on a synthetic board: crop, draw a solve region smaller than the crop, run to the
generator, and read which list each via lands in. Fix whichever defect is real. A via outside the solve region belongs
in the solve-region line's count, not in an off-board list.

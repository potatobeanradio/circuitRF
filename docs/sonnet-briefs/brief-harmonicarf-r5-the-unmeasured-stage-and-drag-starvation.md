# Brief — harmonicaRF Round 5: the last unmeasured stage, and why a fast frame still stutters

**Read first:** `src/Ui/RESOLVED.md` **§4** and **§5** (both brief-harmonicarf-r4) and **§1.4**
(brief-harmonicarf-r3b) — this brief is the direct consequence of what those three sections measured
and of the one number all three failed to read. Then these files in full:
`src/Ui/Controls/HarmonicaCanvas.cs`, `src/Harmonica/SolvePool.cs`,
`src/Ui/Harmonica/HarmonicaPointer.cs`, `src/Ui/Views/ReadoutStripView` (`SetItems` and `SetInputs`
especially), and `HarmonicaViewModel.RequestFrameOnMarkerRelease` / `RequestFrame` /
`OnFrameChanged`.

**Fixture:** the shipped default document, as in R3B and R4. But note §1 below — the central
deliverable of this brief is an instrument that runs in the LIVE application, because the numbers
this brief needs cannot be produced headlessly and two consecutive briefs have now ended without
them.

---

## 0. Why this brief exists: the measurable side is now fast, and the owner still sees ~11 fps

R4 worked. Its own recorded figures:

| stage | before R4 | after R4 |
|---|---|---|
| SmithPower render @2x | 10.06 ms | **0.53 ms** |
| SmithEfficiency render @2x | 9.84 ms | **0.53 ms** |
| tier-A solve, small drag (policy C) | — | **9.22 ms** |
| tier-A solve, large drag (policy C) | — | **11.88 ms** |
| large/small asymmetry factor | (the owner's complaint) | **1.29×** |

Add the untouched panels (loadline 1.13–1.42 ms, power sweep 0.25–0.42 ms) and **the whole measurable
drag frame is now ~11–15 ms — a 67–91 fps upper bound.**

**The owner still reports ~11 fps and, specifically, stuttering.** So the gap between what is measured
and what is experienced has not narrowed by fixing the render — it has *widened as a fraction*. §5's
own closing note says as much:

> the ~60–70 ms `ReadoutStripView.LastSetItemsMs` gap from §1.4/§4.6 is unmeasured in this headless
> environment and is now, by a wide margin, the largest unaccounted-for piece of the owner's original
> ~90 ms/11 fps observation — bigger than everything §4 and §5 together move.

**And "stuttering" is a different symptom from "slow", which nothing so far has been measuring.** Every
figure in R3B and R4 is a mean or a best-of-N. A best-of-5 frame time cannot see a stutter by
construction. Stutter is *frame-interval variance* — the p95/p99 of the gap between published frames —
and no number anywhere in this repo has ever measured it.

This brief has three jobs, in this order: **build the instrument, then fix the one stage it will
almost certainly implicate, then fix the submission pacing bug that reading alone already found.**

---

## 1. Build the instrument first — this number has now gone unread twice

§1.4 named `ReadoutStripView.LastSetItemsMs` and said reading it during an interactive check was how
it would get a real number. §4.6 then recorded:

> `ReadoutStripView.LastSetItemsMs` was not read this pass. It requires a live interactive Avalonia
> session … which this session had no way to drive.

That is not a one-off. `Ui.Tests` may not call Avalonia runtime APIs — a hard project rule — so *every*
cost on the UI thread and in the Avalonia round trip is structurally unmeasurable by the normal gate.
**Two briefs have now ended with the dominant term unread. A third would be a process failure, not an
accident.** So the instrument comes first and is a real deliverable, not scaffolding.

### 1.1 — A live diagnostics overlay, toggleable from the Display menu

A HUD drawn on the harmonicaRF canvas (or a small dockable panel — implementer's choice, but it must
be readable *while dragging*, so a modal dialog is not acceptable). Off by default; the toggle
persists per document like every other Display option.

**What it must show, live:**

- **Frame-interval distribution, not just the mean.** The wall-clock gap between successive published
  frames: last, mean, **p95, p99, and max** over a rolling window of ~120 frames, plus a count of
  intervals over 33 ms. This is the stutter metric. Everything else on this list is diagnosis; this
  is the symptom.
- **Per-stage cost for the most recent frame:** solve ms (already available), `LastRenderMs` (already
  written by `HarmonicaDrawOperation.Render`), **`ReadoutStripView.LastSetItemsMs`** (already exists,
  never read), and the `SetInputs` half timed the same way if it isn't already.
- **`SolvePool` counters:** `StartedCount`, `CompletedCount`, `SupersededCount` — and the derived
  **`CompletedCount / StartedCount` ratio**, which is the single number §3 below turns on.
- **The counters R4 added:** `NoOpDragFrameSkipCount`, `Lever1DisabledCount`.
- **GC:** `GC.CollectionCount(0)` and `(1)`, as deltas over the rolling window.

**Rolling-window statistics, reset on demand**, so the owner can clear it, do one representative drag,
and read a clean set — rather than reading numbers polluted by the app's startup.

### 1.2 — The overlay must not distort what it measures

It draws on the same canvas whose cost is under investigation. Keep it to plain text, no chart, no
antialiased chrome, and time its own draw so the overlay's cost is visible in the overlay. If the
overlay's own draw exceeds ~0.2 ms, simplify it rather than subtracting it out.

### 1.3 — Gate

The owner performs one drag with the overlay on and reports the numbers. **That reported set IS this
brief's primary measurement** — everything below is scoped against it. Nothing in §2 or §3 gets
declared fixed without a before-and-after pair of overlay readings from a real drag.

---

## 2. `ReadoutStripView.SetItems` — the dominant remaining term, and the fix pattern already exists

**Predicted, so it can be falsified:** the overlay will show `LastSetItemsMs` as the largest single
stage in a drag frame, materially larger than solve or render. If it does not, say so plainly and stop
— §3 then becomes the whole brief.

### 2.1 — What it does today

`src/Ui/RESOLVED.md`'s R3C section records it exactly:

> `SetItems` clears and rebuilds its four columns unconditionally (safe only because none of THOSE
> rows survive a rebuild anyway — an open editor there gets destroyed and reopened as a stale row
> every published frame, a pre-existing gap this brief did not touch)

So: an unconditional `.Clear()` plus construction of **~70–110 real Avalonia controls, on the UI
thread, on every published frame**, plus the layout pass that follows. This cannot be moved off the UI
thread — Avalonia controls are thread-affine — so the fix is to **stop doing it**, not to relocate it.

### 2.2 — The fix is a pattern this file already contains, twice

Do not invent a scheme. `SetInputs` already does the right thing (a shape signature plus per-row
`UpdateInPlace`), and R3C's Settings column extended it to cover mid-edit rows. Apply the same shape to
`SetItems`:

- **A shape signature** over what actually determines the row structure — the marker set per column,
  and whether MXP/MXE are present. Unlike the Settings column, `SetItems`' shape genuinely *can* change
  (a marker added or removed, an optimum appearing), so the signature is load-bearing here rather than
  a formality.
- **Signature unchanged → write values into the existing rows.** Signature changed → rebuild that
  column, and only that column.
- **Per-column, not whole-strip.** Adding an L2 marker must not rebuild the Source column.

### 2.3 — A pre-existing bug this closes for free, and it should be claimed

R3C's note above describes an open Source/Load inline editor being destroyed and reopened as a stale
row on every published frame. Build-once/update-in-place fixes that as a side effect. **Extend R3C's
`SettingsRowMayBeOverwritten(bool isEditing)` predicate — or an equivalent — to these columns too**, so
a row that is mid-edit has its value slot skipped, and pin it with a test. Do not leave the fix
implicit.

### 2.4 — Gate

Overlay reading before and after, on the same drag shape. `LastSetItemsMs` in the steady state of a
drag (no marker added or removed) should fall to the cost of writing ~37 strings, not constructing
~100 controls. **Report the p95/p99 frame interval alongside it** — the mean falling while p99 does not
would mean the stutter is somewhere else and §3 is the answer.

---

## 3. Latest-wins starvation: every pointer move cancels the solve before it can publish

Found by reading, not by measurement, and **not addressed anywhere in R3B, R4, or any RESOLVED.md
section.** R4 §5.4's no-op skip only suppresses frames whose Γ has not moved past 1e-4 — during a real
drag every frame's Γ *has* moved, so that path never fires and this mechanism is untouched by it.

### 3.1 — The mechanism, from the code

`HarmonicaCanvas.OnPointerMoved` → `HarmonicaGesture.PointerMoved` → `HarmonicaPointer.Apply`, whose
extrinsic-marker branch ends:

```csharp
_vm.SetMarkerGamma(Grab.Marker!, gamma);
_vm.RequestFrameOnMarkerRelease(Grab.Marker!.Side, Grab.Marker.Band, dragging);
```

That runs on **every single pointer-move event**, with no throttle, no pacing, no coalescing. And
`SolvePool.Submit` opens with:

```csharp
// Cancel the previous frame BEFORE the new one queues, so a worker frees up promptly.
_current?.Cancel();
```

A pointer delivering 100–1000 events/sec against a 9–14 ms solve means **each solve is cancelled before
it completes.** Nothing publishes until the pointer slows or stops, and then a frame lands. That is a
stutter — long dead intervals punctuated by jumps — and it is invisible to every mean-frame-time
measurement taken so far.

`SolvePool`'s own class comment is right about what latest-wins buys and silent about what it costs:

> Without this a fast drag builds an unbounded backlog and the UI lags *further the faster you move*

True — latest-wins prevents **lag**. It does not prevent **starvation**, and starvation is the symptom
under report. The two look completely different on screen: lag is a glyph trailing the cursor smoothly;
starvation is a glyph that freezes and teleports.

### 3.2 — Confirm before fixing

`CompletedCount / StartedCount` over one drag, from §1's overlay. If it is near 1, this section is
wrong and should be closed with that stated. If it is a small fraction, most of the machine's work
during a drag is being computed and discarded, and that is both the stutter and a large amount of
wasted power.

### 3.3 — The fix: conflate and pace, and keep the glyph decoupled from the solve

**Replace cancel-and-restart with conflate-and-pace on the marker-drag path:**

- Hold the newest pointer-derived Γ in a single pending slot, overwritten by each move.
- **Submit only when no solve is in flight** (or at most one per display refresh — implementer's
  choice, but measure both). On completion, if the pending slot holds a newer Γ, submit that.
- Every solve that starts is then allowed to finish and publish. Fewer solves, more published frames,
  and less Gen0 garbage — the discarded work is allocating spectra it throws away.

**The critical constraint: pacing the SOLVE must not pace the MARKER.** R-h6-3's "the marker IS the
live preview" rule stands. `SetMarkerGamma` and the resulting redraw stay at full pointer-event rate,
so the glyph tracks the cursor exactly as it does now; only the solve-dependent content (power sweep,
loadline, readouts) updates at the rate the solver can actually sustain. **This split is what will
make the drag feel smooth even before it is fast** — a glyph that tracks perfectly while the curves
update at 40 Hz reads as responsive; a glyph that freezes with them reads as broken, at identical
average throughput.

**Latest-wins itself is NOT removed.** It stays as the policy for everything else that submits —
structural changes, grid builds, release frames. This changes the marker-drag path's submission
*rate*, not the pool's supersession semantics.

### 3.4 — Gate

Overlay before/after: `CompletedCount / StartedCount` rises toward 1, `SupersededCount` falls sharply,
**p99 frame interval falls**, and total solves per drag falls. Plus a counter-gated test in the
`HarmonicaGridPointDragTests` / `HarmonicaDragTests` style — N simulated moves during a drag produce at
most the paced number of submissions, and the marker's own Γ still tracks the last move, and release
still submits a real full-quality solve.

---

## 4. Secondary suspects — measure via §1, act only if implicated

Do not chase these speculatively. Each gets a line in the overlay and a decision from it.

- **Dispatcher priority.** `HarmonicaCanvas.OnRedrawRequested` does
  `Dispatcher.UIThread.Post(InvalidateVisual)` at the default priority. Establish where that sits
  relative to `DispatcherPriority.Input` in this Avalonia version **by reading Avalonia's own source,
  not from memory**, and state it in the completion note. A redraw posted above input priority can
  starve pointer delivery in bursts, which is a stutter mechanism independent of §2 and §3.
- **GC.** Each cancelled solve still allocated its spectra before dying. Gen0 collections at an
  irregular cadence read as stutter, not as slowness. The overlay's GC deltas answer this; §3's fix
  should reduce it substantially on its own, so measure GC **after** §3 before deciding it needs
  anything of its own.
- **Contention at drag start/end.** `ContourGrid.BuildParallel` fans out across
  `Environment.ProcessorCount` (10) while `SolvePool` holds `ProcessorCount − 2` (8) slots, and a
  superseded build only cancels "within one Γ point's cost (~20 ms)". Mid-drag this should not fire
  (contours are frozen), but a build in flight when a drag *begins*, or the release frame's build, can
  oversubscribe. Only worth pursuing if the overlay shows the stutter clustering at drag boundaries
  rather than throughout.

---

## 5. Guardrails

1. **Nothing in `PinSearch`, `ContourGrid`, `HarmonicaContext`, or any solver path changes in this
   brief.** R4 settled the seed policy; this is entirely a UI-thread and frame-pump brief.
2. **`SolvePool`'s latest-wins semantics stay.** §3 changes what the marker-drag path submits, not how
   the pool supersedes.
3. **The marker glyph's per-event responsiveness is not traded away** for any throughput gain (§3.3).
4. **`SetItems`' rendered output must not change.** The strip shows exactly what it shows today; only
   how it gets there changes. A visual diff of the strip before and after is part of the gate.
5. **`PlotRenderer` / `AxesRenderer` untouched**, as always. The overlay is harmonicaRF's own.
6. **The overlay ships off by default** and must cost nothing measurable when off — no timers running,
   no rolling buffers being filled.
7. **Every new number is a counter or a timer that is READ**, not one that is added and then reported
   as unread. §4.6 is the precedent this brief exists to not repeat.

## 6. Completion note — what it must contain

- **The owner's own overlay reading from one real drag**, before any fix: frame-interval mean/p95/p99/
  max, `LastSetItemsMs`, `LastRenderMs`, solve ms, `CompletedCount`/`StartedCount`/`SupersededCount`,
  GC deltas. This is the number two briefs have failed to produce; producing it is the minimum bar for
  this brief regardless of what else lands.
- The same reading after §2, and again after §3.
- Whether §2's prediction (the strip dominates) held. If it did not, say so first and plainly.
- Whether §3's starvation was real, with the completed/started ratio that decided it.
- The Avalonia dispatcher-priority finding from §4, cited to Avalonia's source.
- Anything here that turned out to be wrong. R3B §1.1 and R4 §5's "Policy B does not win outright" are
  the standard: state the correction, show the measurement that overturned it, don't reconcile it away.

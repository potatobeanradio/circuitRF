# Brief TO-3 — The live session and the Data Display's in-memory source

**Series:** `brief-tuneopt-0-overview.md` (D7, D8, D9) · **Tag:** `R-to3-<m>` · **Depends on:** TO-2
**Area:** `src/Ui/DataDisplay/` (the data-source library, trace refresh, `PlotInspectorViewModel`,
`TraceRowViewModel`), `src/Ui/Schematic/RunResultsWriter.cs`, a new `src/Ui/Tuning/TuneSession.cs`,
`docs/design/data-display.md` §2.2

---

## 1. Goal

A headless-testable session object that turns a stream of value changes into a stream of results, and a Data Display
that shows those results without a disk round trip, skipping frames when it cannot keep up. No Tuning panel yet —
TO-4 puts a face on this.

## 2. Requirements

**R-to3-1 — `TuneSession`.** Owns one prepared design (TO-2 R-to2-4) for one schematic. `Request(values)` enqueues;
the policy is D7 exactly — one evaluation in flight, at most one pending, a newer request replaces the pending one, the
in-flight evaluation is **not** cancelled by a newer value, and is cancelled by `Stop()`, `Revert()`, `Push()`,
disposal. Evaluations run off the UI thread; results are marshalled to the UI thread once each.

**R-to3-2 — Lag.** The session exposes: the values of the **displayed** result, the values **requested**, whether an
evaluation is running, the last evaluation's duration, and the age of the displayed result. "Lagging" = requested ≠
displayed. TO-4 draws it; this phase only exposes it.

**R-to3-3 — Run on release.** A session flag; when set, intermediate requests are dropped and only a request marked
`final` (the slider's release) is evaluated. The session suggests turning it on (an event) when an evaluation took
> 2 s; it never turns it on by itself.

**R-to3-4 — Analysis scope.** The session evaluates a chosen subset of analyses (default: every enabled analysis).
A parametric sweep in the subset is allowed; the session reports its point count so TO-4 can warn.

**R-to3-5 — Publishing to the Data Display.** Add to the data-source library a way to **publish an in-memory
`DataSet` under an existing source path** — the schematic's `results/<key>/run.npy`. Every trace bound to that source
re-resolves against the published `DataSet` with no change to the trace. A published `DataSet` is a newer version of
the source until the session ends or the file changes on disk. Keep `data-display.md` §2.2's principle: the path is
still the identity; only the bytes come from memory. Update §2.2 to say the future optimization is now built.

**R-to3-6 — Frame coalescing.** A display that receives a new `DataSet` while still redrawing for the previous one
redraws **once** more, for the newest, and skips the intermediates. Count redraws and skips (counters, for tests and
for TO-10's "display skipped n frames" readout).

**R-to3-7 — End of session.** On Stop, the last displayed result is written to `run.npy` by `RunResultsWriter` with
provenance stating it was produced from tuned values and listing them, then the published `DataSet` is dropped. On
Revert, the published `DataSet` is dropped and the display returns to the file as it was. On Push, see TO-4.

**R-to3-8 — Chip.** While a display shows published data it carries a small **Tuning** chip (TO-10 adds
**Optimizing** through the same mechanism). No prose.

**R-to3-9 — Snapshot ghosts (D9).** `Snapshot()` copies the currently displayed `DataSet` into a session-only
snapshot; every trace bound to the schematic's source draws a faded copy of itself from the snapshot (same color,
reduced opacity, no markers, excluded from autoscale ties and from the legend's interactive items). `ClearSnapshot()`
removes it. Not persisted, not exported, not drawn by `render --data`.

**R-to3-10 — Complex values (overview D18; amended 2026-10-07).** The session's requested and displayed values are
keyed by the **value key** — a complex value once, whole, under its own key — so "lagging" compares whole values, and
the provenance written on Stop (R-to3-7) lists `ZL=30+52j Ohm`, not its parts. Nothing else changes here.

## 3. Gates (minimal; run only these classes)
- `TuneSessionPolicyTests` — with a fake evaluator that blocks on a gate: ten requests during one evaluation produce
  exactly two evaluations (the in-flight one and the newest); Stop cancels; run-on-release evaluates only the final.
- `PublishedSourceTests` — a trace bound to `run.npy` shows the published cube's values; Revert restores the file's.
- `FrameCoalescingTests` — five publishes during one redraw → one further redraw, four skips (counters).
- `SnapshotGhostTests` — a snapshot adds one ghost per bound trace and Clear removes them; nothing is written to the
  `.cdd`.

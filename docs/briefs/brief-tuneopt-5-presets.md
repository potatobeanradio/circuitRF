# Brief TO-5 — Presets: lock in, recall, "Last tuned"

**Series:** `brief-tuneopt-0-overview.md` (D3, D5, D6) · **Tag:** `R-to5-<m>` · **Depends on:** TO-4
**Area:** `src/Design/Optimization/` (`TuningPreset`, recall logic), `src/Ui/Tuning/` (the presets drop-down, shared
with TO-10)

---

## 1. Goal

A user who likes what they see can keep it, name it and get back to it — and a preset never breaks because the
schematic moved on.

## 2. Requirements

**R-to5-1 — Lock in.** A toolbar button (lock icon) stores the session's current values of **every tune-enabled
entry** as a new preset, named `Preset n` and immediately renamable in place. One undo step; marks the document dirty.
The Optimizer (TO-10) calls the same function with its best values and the optimizer's cost at that point recorded in
the preset (optional field; TO-1's model gains it here if TO-1 did not add it).

**R-to5-2 — Recall is best effort (headless function, `src/Design`).** `PresetRecall.Apply(preset, catalog)` returns
the values to load and a report: applied, **missing** (key resolves to nothing — the component was deleted or renamed),
**out of range** (value outside the entry's current min/max — applied anyway, and the range is widened to include it,
reported), **no longer tunable** (now an expression — skipped). It never throws for any of these.

**R-to5-3 — Recall in the panel** loads the values into the session (or into the sliders, if not started) — it does
**not** change the schematic; Push does that. The report shows in the status line as one sentence ("Applied 11 of 12 ·
R7 no longer exists") with the full list in the tooltip and in Messages.

**R-to5-4 — The drop-down.** Lists presets newest first, "Last tuned" pinned at the top when present, each with its
time; hover shows its values and, for optimizer presets, its cost. Per-preset ⋮: Recall, Recall and Push (one undo step
for the push), Rename, Duplicate, Delete (undoable), Copy as `.cnl` lines (an agent-friendly form).

**R-to5-5 — "Last tuned" (D6).** On save and on close of a document whose session values differ from the schematic,
write or overwrite the single `IsLastTuned` preset. Not when they are equal. It is never created by any other path, and
deleting it is allowed.

**R-to5-6 — Comparison.** Selecting two presets (or a preset and "Schematic") and choosing **Compare** shows a small
table of keys with both values and the differences. No simulation.

**R-to5-7 — Complex values (overview D18; amended 2026-10-07).**
- Lock in stores a complex value **whole**, under its own key, in the schematic's form (`ZL=30+52j Ohm`) — once,
  however many of its parts are tuned — so recall and Push are the same as typing it. Copy as `.cnl` writes the same.
- Recall of a whole value sets the session's complex number; every part row follows. A whole value outside any range
  of its parts is applied and those ranges are widened to include it (R-to5-2's rule, per part); widening only grows
  the region, so it can never be a D18 conflict. A part key in a hand-written preset is composed with the
  schematic's value (`ComplexValue.Compose`); a combination no value satisfies is reported **no longer applicable**
  and skipped. A value that is no longer a complex literal (now `4+j*X`) is **no longer tunable**, as R-to5-2 says.
- Compare shows complex values whole, with the difference of each tuned part beside it.

## 3. Gates (minimal; run only these classes)
- `PresetRecallTests` — one test each: all present; a deleted instance; a renamed VAR; an out-of-range value (range
  widened, reported); a now-expression parameter (skipped). None throws.
- `LastTunedPresetTests` — written on save when values differ; not written when equal; overwritten, never duplicated.
- `PresetCnlTextTests` — "Copy as `.cnl`" text parses back to the same preset.
- `PresetRecallTests` gains: a whole complex value recalled into two part rows; an out-of-range one widening the
  part's range; an impossible part-key pair reported and skipped.

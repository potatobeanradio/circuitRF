# Sonnet Brief — Multi-file Data Display: UI entry points, alias labelling, path portability

Section 3 of `brief-results-storage-and-data-display.md` landed the *model* — a `.cdd` referencing several
`.npy` files with aliases. **There is no UI to add a second file.** This adds it, without changing the
single-file experience at all.

**Primary references:** `docs/design/data-display.md` **§2.7** (label computation) and **§2.8** (Plot
Inspector, which already has an Add Trace button).

Gate command is plain `dotnet test`.

---

## 1. Labelling — no new rule is needed

**§2.7 already computes labels as "the shortest suffix that still tells the traces apart,"** dropping any
identity component that is constant across the plot, and recomputing when traces are added or removed.

**R-dd-1. Add the dataset alias as another identity component in §2.7's existing scheme. Do not write a
separate qualification rule.**

The behaviour then falls out for free:

- **One dataset** → the alias is constant → dropped → labels are **byte-identical to today**.
- **Two datasets** → the alias varies → included → `baseline:S21` and `tuned:S21`.
- Remove the second dataset → labels revert automatically.

This is why it must go through §2.7 rather than beside it: the single-file case is protected
**structurally**, not by remembering to special-case it.

## 2. Adding traces from another file — extend Add Trace, don't add a surface

**R-dd-2. §2.8's Plot Inspector Add Trace button stays the entry point.** Its picker gains a **source**
selector, and:

- With **one** dataset loaded, the picker looks exactly as it does now — no source column, no extra click.
  The selector appears only once a second dataset exists.
- The source list ends with **"Add from file…"**, so pulling in a new file and picking a trace from it is
  **one gesture**, not two. This is the path most users will take, and it is the one that must not feel like
  configuration.

**R-dd-3. Dragging an `.npy` from the project tree onto a plot adds it as a dataset and opens the trace
picker for it.** The application already has palette→schematic and palette→layout drag-drop; this is the same
idiom, and it makes "compare against that other run" a single motion.

## 3. A Datasets list — needed for three things that currently have nowhere to live

**R-dd-4. Add a compact Datasets section to the docked Properties tool**, beside the plot inspector — not a
new panel. Each row: **alias · filename · status**.

It exists for three reasons, only one of which is adding traces:

- **Aliases must be renameable.** `baseline` and `tuned` are the user's decision; the default is the file
  stem.
- **Missing files must be visible and actionable.** R-res-5 requires reporting a missing dataset by name and
  preserving trace configuration — but without a surface, the user gets a message and nowhere to act. Manual
  deletion in Finder is the entire premise of the flat `results/` directory, so this **will** happen
  routinely. Show missing rows distinctly and let them be re-pointed or removed.
- **Re-pointing is the payoff of the alias indirection.** Traces reference the alias, not the path — so
  pointing `baseline` at `run3.npy` updates **every trace using it at once**. Swapping a baseline and having
  the whole comparison re-plot is the workflow this feature exists to enable, and it is nearly free given the
  model already built.

**R-dd-5. Aliases are unique within a `.cdd`,** and renaming one updates every trace referencing it. Refuse a
duplicate rather than silently disambiguating.

## 4. Path portability — already correct, with one cleanup

The owner asked whether data sources are stored relative to the workspace, for sharing across machines and
for moving a workspace. **They are.** The committed `circuitRF_demo/results/FET_curve_tracer.cdd` contains:

```json
"SelectedDataSource": "FET_curve_tracer.npy"
"SourcePath": "run.npy"
```

and **zero** absolute paths. Nothing needs fixing. Two things need pinning so it stays that way.

**R-dd-6. A stored data source is a bare filename resolved against the workspace's `results/` — never a
rooted path, and never one containing a directory separator.** Bare filenames are maximally portable: no
absolute prefixes, and **no platform separator problem** either, which is the failure that would otherwise
appear only when a macOS workspace is opened on Windows. It also matches R-res-2, which already forbids a
user-specified results filename from escaping `results/`.

**Validate on save**, not only on load — a rooted or separator-bearing value must never reach the file.

**R-dd-7. `"SourcePath": "run.npy"` is a stale field from the pre-flattening convention.** It is relative, so
harmless to portability, but it names a file that no longer exists under the new naming. **Determine whether
anything still reads it.** If yes, that is a latent bug — fix it. If no, drop it on the next write and remove
it from `circuitRF_demo`'s committed `.cdd`, since a stale field in the shipped example teaches the wrong
convention to anyone who opens it.

**R-dd-8. Resolve to absolute only at the point of load.** The runtime `srcAbs` in
`DataDisplayView.axaml.cs` is correct — resolved, not stored. Keep that boundary: relative in the file,
absolute in memory, and never the reverse.

## 5. Guardrails

- Do not write a separate label-qualification rule (§1) — extend §2.7's identity components.
- Do not add a source selector to the picker when only one dataset is loaded (§2).
- Do not create a new top-level panel for datasets (§4) — the docked Properties tool hosts it.
- Do not store absolute paths, and do not store separators (§4).
- Don't touch `src/Core`, `src/Engine`, `RfCore`.

## 6. Gate

1. Builds green; `dotnet test` green; no existing test regresses.
2. **Single-file labels unchanged (R-dd-1)** — with one dataset, plot labels are **identical** to before this
   change. Assert against expected strings, not just that labels exist; this is the regression guard for the
   whole feature.
3. **Two-file labels (R-dd-1)** — the same metric from two datasets renders as `alias:metric` for both;
   removing one dataset reverts the remaining label.
4. **Picker (R-dd-2)** — with one dataset the Add Trace picker shows no source selector; with two it does;
   "Add from file…" adds a dataset and offers its traces in one flow.
5. **Drag-drop (R-dd-3)** — dragging an `.npy` from the project tree onto a plot adds the dataset.
6. **Aliases (R-dd-4/5)** — renaming an alias updates every trace label using it; a duplicate alias is
   refused; the alias round-trips through the `.cdd`.
7. **Missing file** — deleting a referenced `.npy` shows that row as missing, keeps other datasets plotted,
   and **preserves trace configuration**; re-pointing that row restores the traces without re-authoring them.
8. **Re-point (R-dd-4)** — pointing an alias at a different `.npy` updates every trace using it in one action.
9. **Portability (R-dd-6/8)** — a `.cdd` saved with several datasets contains **no** absolute path and **no**
   directory separator; move the whole workspace to a different directory and it opens with all datasets
   resolving. Assert the saved JSON, not just that it reloads in place.
10. **Stale field (R-dd-7)** — report whether `SourcePath` is still read; it is absent from newly written
    `.cdd` files and from `circuitRF_demo`.

## 7. On completion

Record in `src/Ui/CLAUDE.md`: that **the dataset alias is an identity component in §2.7's label scheme**, which
is why single-dataset labels are unchanged by construction; that the **Datasets list is where missing files
become actionable**, not merely where datasets are listed; **R-dd-6's bare-filename rule** and that it also
avoids the cross-platform separator problem; and what became of the stale `SourcePath` field.

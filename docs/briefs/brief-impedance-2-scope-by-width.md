# Brief 2 — the scope model, and choosing traces by width

**Series:** [Impedance review](brief-impedance-0-overview.md) · **Tag:** `R-imp2-n` · **Phase:** model change, format change
**Area:** `src/Design/Layout/Em/TraceImpedanceAnalysis.cs` (new `Survey`, `Analyze`'s layer loop),
new `src/Design/Layout/Em/TraceImpedanceScope.cs`, `src/Design/Layout/LayoutModel.cs` (`LayoutView`),
`src/Design/Layout/LayoutPersistence.cs`, `src/Render/Renderers/TraceImpedanceReportDocument.cs`,
`src/Cli/Impedance.cs`, `src/Cli/Serve/ToolCatalog.cs`,
`src/Ui/Views/Dialogs/TraceImpedanceAnalysisDialog.axaml(.cs)`,
`src/Ui/Layout/LayoutEditorViewModel.TraceImpedance.cs`
**Depends on:** 1 · **Blocks:** 3, 4
**Rule:** the boards, their vendors and the reporter must not be named anywhere in the repo.

---

## 0. Why width first

On both field boards, **trace width alone separates the RF path from everything else**: board A's RF
trace is its only 457 µm trace on Top (the rest are 100 µm), board B's is 381 µm among 99–221 µm
fan-out. It is also how a fabrication drawing's impedance note is written — "457 µm on L1: 50 Ω
controlled" — so it is already how the reviewer thinks. And it works on bare Gerber artwork, which has
no nets and no schematic.

This brief also builds the scope MODEL that briefs 4 (regions, picks, nets) and 5 (accepted findings)
extend, and saves it in the `.clay`.

## 1. `R-imp2-1` — a survey: the traces and their widths, with nothing solved

- **`R-imp2-1a`** `TraceImpedanceAnalysis.Survey(shapes, tech, dbuPerMicron, options, control)` runs
  `Analyze`'s copper reading and `FindLayer` for each requested layer and stops: no cutting, no
  solving. Refactor `Analyze` so both run the same code for those stages — not a copy.
- **`R-imp2-1b`** It returns, per layer, the **width classes**: each chain's **dominant width** (the
  width over the largest share of its length; a 99–650 µm taper is classed by the width most of it
  has) grouped into classes that merge widths within max(1 %, 1 µm). Per class: nominal width, min/max
  of the widths merged, trace count, total length, and a **typical Z0** — ONE cross-section solve at
  the middle station of the class's longest chain, labelled "typical" wherever it is shown.
- **`R-imp2-1c`** Do NOT pre-select classes from their typical Z0. A trace drawn at the wrong width is
  exactly what the review exists to catch; pre-selecting "the widths that are already near 50 Ω"
  would hide it. Every class starts unticked the first time; after that, the saved scope decides.
- **`R-imp2-1d`** The survey reports progress and is cancellable (`RunControl`), like the analysis.
  Measure it on the largest fixture available and record in `src/Design/RESOLVED.md` what share of a
  full run it is; if `FindLayer` itself is the slow stage on a pour-heavy layer, say so there rather
  than optimise it in this brief.

## 2. `R-imp2-2` — `TraceImpedanceScope`, applied before cutting

- **`R-imp2-2a`** New `TraceImpedanceScope` (`src/Design/Layout/Em/`): `Widths` — a list of
  `(LayerName, NominalMicrons, ToleranceMicrons)`; empty means every width. Brief 4 adds `Regions`,
  `Picks`, `Nets` to the same type; leave the semantics comment ready for them:
  *a trace is in scope when its layer is analysed, AND (no selector is set OR it matches any
  selector), AND (no width class is set for its layer OR its dominant width is in one).* Widths are a
  FILTER; regions/picks/nets are SELECTORS. Put that sentence once, in the type's doc comment.
- **`R-imp2-2b`** `TraceImpedanceOptions.Scope` (nullable; null = everything, today's behaviour).
  `Analyze` drops out-of-scope chains **after `FindLayer` and before `Cut`**, so they are never cut and
  never solved. Out-of-scope traces are not numbered: T1… are the traces under review.
- **`R-imp2-2c`** **Scope selects traces, never copper.** Every cross-section still sees every
  conductor on every layer — the neighbouring out-of-scope trace is still a grounded neighbour. Add a
  comment where the filter is applied saying why.
- **`R-imp2-2d`** `TraceLayerResult.OutOfScope` (count), and `TraceImpedanceReport.ScopeText` — the
  scope in words, built in `src/Design` so the PDF, the CLI and the UI say the same sentence:
  *"Top Copper at 457 µm (1 trace). 21 traces on Top Copper and 19 on Inner 1 are outside the scope and
  were not analysed."*

## 3. `R-imp2-3` — saved in the `.clay`

- **`R-imp2-3a`** `LayoutView.ImpedanceReview` (nullable): the dialog's settings — target, tolerance,
  warning, highest frequency, layers (by name) — and the `TraceImpedanceScope`. Persisted by
  `LayoutPersistence` beside `DrcWaivers`/`LvsWaivers`, omitted from the file when null so every
  existing `.clay` round-trips byte-identical. Layer by NAME, as the CLI's `--layers` already names
  them.
- **`R-imp2-3b`** Changing it marks the layout dirty and is **not undoable**, on `DrcWaivers`' terms
  (review state, not artwork). Say so in its doc comment with a pointer to `LayoutView.DrcWaivers`.
- **`R-imp2-3c`** A saved width class that matches nothing in a later survey (the artwork changed) is
  kept and shown struck-through with "no traces at this width now", never silently dropped.

## 4. `R-imp2-4` — the dialog, the report, the CLI

- **`R-imp2-4a`** Dialog: a **TRACES** section under LAYERS. On open it runs the survey in the
  background (progress in place of the table, cancellable by closing), then lists per ticked layer:
  ☐ width · count · total length · typical Z0. Ticking none on a layer means "every width on this
  layer". Unticking a layer hides its rows. Closing the dialog (Export or Cancel) saves the settings
  and scope to the layout (R-imp2-3). Brief 3 moves all of this into a panel; keep the survey and
  scope logic out of the code-behind so it moves as is.
- **`R-imp2-4b`** PDF: the summary page states `ScopeText` under Target, and the by-layer table gains
  an "Out of scope" column. On the map, out-of-scope traces are plain copper — no colour, no label,
  no marker. That is what un-clutters board B's page.
- **`R-imp2-4c`** CLI: **the scope saved in the `.clay` applies by default**, so a headless run
  reports what the GUI reports. `--no-scope` analyses everything. `--width <layer>=<µm>[,<µm>…]`
  (repeatable; a unit suffix accepted, a bare number is µm as `--max-width` already reads it) replaces
  the saved width classes for that run. `--target`/`--tol`/`--warn`/`--layers` override the saved
  values, which override the defaults. The text output's first line after the header is `ScopeText`.
  `--json` gains `scope` (the words) and per layer `outOfScope`. Add to `ToolCatalog`.
- **`R-imp2-4d`** `circuitrf impedance --survey <path>` prints the width classes per layer (and
  `--json`) and solves nothing but the typical-Z0 cuts — the headless way to choose `--width`.

## 5. Tests (minimal — one per claim)

1. **The saving is real, counted not timed**: a synthetic layout with one 450 µm trace and five 100 µm
   traces on one layer; scoped to 450 µm, `SolveCount` is at most the unscoped run's solves for that
   one trace, and exactly one trace is reported (T1).
2. **Scope selects traces, never copper**: the 450 µm trace's Z0 is the same, to the last digit, with
   and without a scope that excludes a 100 µm neighbour running beside it.
3. The survey reports the two classes with their counts and lengths, and performs exactly one solve
   per class.
4. A `.clay` with no review round-trips byte-identical; one with a review round-trips its scope.
5. The verb applies the saved scope by default; `--no-scope` reports all six traces.

Run only the classes you touch (`--filter FullyQualifiedName~TraceImpedance`, plus the persistence
test class).

## 6. Scope

- No region, pick or net selectors (brief 4). No accepted findings (brief 5).
- No automatic classification of "RF" traces by any heuristic.

## 7. On completion

Findings in `src/Design/RESOLVED.md` (the dialog in `src/Ui/RESOLVED.md`). Update the user reference
(layout-editor.md §Impedance Analysis: the TRACES section, the saved review; cli.md: `--width`,
`--no-scope`, `--survey`, the default-applies-saved-scope rule) and the `.clay` format section of the
file-format reference if it lists `LayoutView`'s keys.

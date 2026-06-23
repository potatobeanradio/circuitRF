# circuitRF — Analysis Authoring Design

**Status:** Steps 1–6 done · **Date:** 2026-06-10 · **Phase:** 6e (analysis authoring — complete)

How a user **adds analyses to a schematic** so it can be simulated. Closes the "no analysis" gap from 6e
step 5: a freshly-drawn schematic carries no analysis today, so Run has nothing to do. This adds the GUI
authoring surface for `TestBench.Analyses` (+ `Measurements`). Companions: `net-extraction-and-run.md`
(extraction emits these into the netlist; the Run executes them), `data-model.md` §4 (the Analysis hierarchy),
`harmonic-balance.md` §3.2 / `nonlinear-dc.md` / `linear-engine.md` (per-type field meaning),
`parameter-editor.md` (the expression-field + preview pattern this reuses), `workspace-and-project-tree.md` §2
(IsTestBench), `project-file-formats.md` (`.csch` serialization).

**The decisions (owner-confirmed):**
- **Model B — analyses are testbench metadata authored through a panel**, NOT placed components (Model A).
  *(A thin Model A — an "analysis component" you drop on the canvas that writes into this same list — may be
  added in v2 for users who want the look of other tools; the authority remains the metadata.)*
- **Analyses live in the `.csch`** (not the `.ccell`) so a **scratch schematic can be simulated without
  defining a cell**. A cell's `IsTestBench` flips true when its primary schematic carries analyses (the
  `SchematicHasAnalyses` hook the save-plan step already stubs).
- **v1 types: DC, S-parameter, Harmonic Balance, Loadpull, Loadpull-Pursuit.** Loadpull and
  loadpull-pursuit authoring **shipped** (briefs 05/06): tuner-instance pickers + `.gam` grid/output
  refs, the tone as a coefficient + unit pair resolved with the HB var-unit-wins rule (brief 04b).
  *(v1 limitation: LP/LPP cannot yet be wrapped in a parametric sweep — loadpull is itself a 2-D sweep.)*
- **UI:** an **Analyses dock panel** (the list, tied to the active schematic) **and** the option to present
  that same list as a **modal dialog**; plus an **Add/Edit dialog** (the per-analysis form) — the
  VendorC "Choosing Analyses" pattern.
- **Reuse (analysis setups are long & painful to rebuild):** **copy/paste** analyses between schematics
  (clipboard) **and** named **multi-analysis templates** saved to a user library (`.canl`), with **Save as
  Template** a first-class button. One serialization (the `Analysis` JSON) backs `.csch` + clipboard +
  template — §5.

---

## 1. Inspiration: VendorC (what we borrow, what we simplify)

VendorC's "Choosing Analyses" flow is the proven RF-engineer mental model, so we mirror its **shape** while
staying simpler than VendorC's corner/assembler machinery:
- **An "Analyses" list** of all configured analyses, each with an **Enabled checkbox** — set up several,
  toggle which run, without deleting. (We adopt this exactly.)
- **"Click to add analysis" → a form where picking the type (dc/sp/hb) updates the form to show only that
  type's relevant fields.** This **progressive disclosure** is the anti-intimidation key: a novice picking
  "DC" sees almost nothing; "HB" reveals its fields only once chosen. (We adopt this exactly.)
- **Sweep by range or by number of points.** (We adopt this for S-param frequency sweeps.)
- **Parametric analysis** wraps the setup to run over a variable. (Maps to our `ParametricSweepAnalysis`;
  v1 may expose a simple per-analysis sweep, full parametric nesting deferred — §6.)

What we deliberately **don't** copy in v1: corners/PVT, Monte Carlo, the assembler multi-test matrix,
component-parameter sweeps from the schematic (design-variable sweeps only). Keep it to "configure analyses
and run them."

---

## 2. The model (small additions to `src/Core/Design/Analysis.cs`)

The `Analysis` hierarchy already exists (DC/SParameter/HB/Loadpull/Pursuit/Parametric, framework-free). v1
needs three small additions; everything else is reused.

### 2.1 S-parameter: a LIST of frequency sweep segments (owner decision)
Today `SParameterAnalysis` holds a single `FrequencySpec`. Change to **a list of sweep segments within one
S-param analysis** — "one S-param run over several bands, each with its own range/step." Add:
- `IReadOnlyList<FrequencySpec> Sweeps` on `SParameterAnalysis` (≥1). (Keep a single-segment as the common
  case; the list supports multi-band.)
- The engine run iterates the segments and concatenates/【unions】 their frequency points into the analysis's
  frequency array (confirm the engine/`SParameterEngine` expects a flat freq array — the CLI builds one; the
  authoring just produces the union of segment points, sorted/deduped).

### 2.2 FrequencySpec: Start/Stop/Step OR Start/Stop/N-points (owner decision)
`FrequencySpec` is `(start, stop, step, kind)`. Add a points option so a segment can be specified either way:
- add `int? NumPoints` (and/or a `FreqSpecMode { StepSize, PointCount }` discriminator). When points-mode,
  the step is derived (`(stop-start)/(N-1)` for linear; log-spaced for `kind=Log`). Persist whichever the
  user entered (so the file round-trips the *intent*, not just the derived step) — `parameter-editor.md`
  "store what the user typed" principle.
- `SweepKind` (Linear/Log) already exists — the per-segment toggle reuses it.

### 2.3 Expression-valued fields (already the model's shape)
Every analysis field is already a **raw expression string** (`ToneExpr`, `MaxHarmonicExpr`, …) resolved
against globals at engine time. **No model change** — the authoring UI edits these strings, and the field's
**preview** evaluates the expression against the schematic's VARs (§4.3). For `FrequencySpec` the numeric
start/stop/step become **expression strings too** (so `stop = 2*f0` works) — store as expression strings,
resolve for preview + at run (a small consistency improvement: `FrequencySpec` numbers → expression strings,
or a parallel expr field — pick the cleaner; the engine already resolves expressions elsewhere).

*(No model changes for DC — `DcAnalysis` is essentially fieldless in v1 (operating point); HB is fully
covered by existing fields.)*

---

## 3. Persistence (in `.csch`)

- The `.csch` serialization (modeled on splotRF's `DataDisplayConfig.cs`, `project-file-formats.md`) gains an
  **analyses list** and a **measurements list** — the `Analysis`/`Measurement` records serialized with the
  same System.Text.Json + enum-as-string conventions. They are framework-free already, so this is
  serialization wiring + the `format_version` reject-on-mismatch policy.
- **Polymorphic analyses:** DC/SParameter/HB are different shapes — serialize with a type discriminator
  (`[JsonPolymorphic]` / a `Type` tag), mirroring how `SymbolModel` primitives are persisted.
- **`IsTestBench` linkage:** when a schematic carries ≥1 analysis, its cell's `.ccell` `IsTestBench` should be
  true. The save-plan's `SchematicHasAnalyses(model)` hook (currently `return false; // TODO 6e`) now returns
  **whether the schematic's analyses list is non-empty** — flipping the TestBench flag automatically on save.
- **Scratch:** because analyses live in the `.csch`, a scratch schematic carries its analyses in-memory and
  (via autosave/recovery) in the scratch payload — so a scratch schematic is fully runnable before any cell
  exists.

---

## 4. The UI (HIG-first — this is the complicated surface; do not intimidate)

Two cooperating pieces, mirroring VendorC: a **list** (dock panel, optionally modal) and a **per-analysis form**
(add/edit dialog). The guiding HIG principle throughout: **progressive disclosure** — show the few things a
novice needs, reveal complexity only on demand.

### 4.1 The Analyses list — dock panel (+ modal option)
A panel tied to the **active schematic**, showing its analyses:
- **One row per analysis:** an **Enabled checkbox**, the **name**, the **type** (DC/SP/HB), and a **one-line
  summary** (e.g. "SP · 1–10 GHz, 3 segments" / "HB · f0=2GHz, 7 harmonics"). Empty state: a friendly "No
  analyses yet — Add one to simulate this schematic" with a prominent **＋ Add Analysis**.
- **Actions:** Add (＋), Edit (double-click / pencil), Remove, reorder (analyses run in listed order),
  Duplicate (for "same type, different config" — one click to clone then tweak).
- **Enabled vs. present:** unchecked analyses stay configured but don't run (VendorC's pattern) — lets a user keep
  a slow HB setup around without running it every time.
- **Dock panel** is the default home (always-available, like Project Tree / Properties). A **"Setup
  Analyses…" menu command** opens the **same list as a modal dialog** (for users who prefer a focused modal,
  or a small-screen layout) — one list VM, two hosts (reuse the dock/modal dual-host pattern).
- **Multiple analyses + multiple of one type** are inherent: the list holds any number, each independent
  (your requirement — free from the list model).

### 4.2 The Add/Edit dialog — the "Choosing Analyses" form (progressive disclosure)
Opened by Add or Edit. Mirrors VendorC's type-then-relevant-fields:
1. **Type picker** at the top (segmented control or radio row): **DC · S-Parameter · Harmonic Balance ·
   Load Pull · LP Pursuit** (all enabled as of briefs 05/06). Picking a type **swaps the form body** to
   that type's fields only.
2. **Name** field (defaulted `SP1`/`HB1`/`DC1`, editable, validated).
3. **Enabled** checkbox.
4. **Per-type body:**
   - **DC:** essentially just a name + (optional) "save operating point" — near-empty (the reassuring novice
     case: "DC is easy").
   - **S-Parameter:** a **sub-list of frequency-sweep segments** (§2.1). Each segment row:
     **Start · Stop**, a **Step | Points toggle** (segmented), and the **Step value** *or* **# Points** field,
     plus a **Linear | Log** toggle. Add/remove segments (≥1). A novice adds one segment, Start/Stop/Step,
     done; the multi-band power is there but not forced.
   - **Harmonic Balance:** **progressive disclosure is essential** (it has ~15 fields). Show a **Basic**
     group always (Tone/fundamental, Max harmonics) and an **Advanced** disclosure (collapsed by default)
     for the rest (mix order, tol, λ damping, oversample, guard harmonic, max iter, drive stepping, sweep).
     Single-tone vs. multi-tone is a small toggle that reveals the extra tone fields only when multi-tone.
5. **Buttons (HIG):** **OK/Save** default (trailing, prominent), **Cancel** (Esc). Centered labels. Inline
   validation per field; OK gated on valid.

### 4.3 Expression fields + live preview (reuse parameter-editor)
Every numeric field is an **expression box** (so `stop = 2*f0`, `MaxHarm = N+2` work against the schematic's
VARs). Reuse the **`parameter-editor.md` preview pattern**: a small grey **"≈ <resolved value>"** under/beside
the field, evaluated against the schematic's global variables. Invalid/unknown-var expressions show a quiet
inline hint (not a hard block while typing). This is the same evaluator the parameter editor uses — share it,
don't fork.

### 4.4 Don't intimidate (the HIG throughline)
- **Type picker collapses complexity:** you never see HB's fields unless you choose HB.
- **Sensible defaults everywhere** so OK works immediately (a new SP analysis pre-fills one 1–10 GHz / 101-pt
  segment; a new HB pre-fills f0 + 7 harmonics) — the novice path is "Add → OK → Run."
- **Advanced fields are collapsed**, summaries are plain-language, the empty state guides the first action.
- **Measurements** (post-run performance expressions, `Measurement`) get a **simple secondary list** (name +
  expression + unit) — v1 minimal; de[er rich measurement authoring if it bloats the surface. *(Confirm v1
  measurement scope at build; the field exists in the model.)*

---

## 5. Reuse: copy/paste + templates (analysis setups are long & painful — make them reusable)

Three levels of reuse, all backed by **one serialization** — the same `Analysis`-record JSON used in the
`.csch` (§3). The clipboard payload, the template file, and the `.csch` analyses section are the **identical
bytes** (single source of truth for the analysis encoding; they cannot drift). All three operate on **one or
many** analyses (multi-select / whole-setup), since a set of analyses is the natural unit.

### 5.1 The dangling-reference principle (the one subtlety)
Some analysis fields reference things in the *source* schematic that may not exist in the *destination*:
- **VAR expressions** — e.g. `stop = 2*f0` references global `f0`; paste into a schematic with no `f0` →
  unresolved.
- **Instance references** (loadpull, deferred) — e.g. `LoadTunerName = "T1"` references an instance; paste
  where no `T1` exists → dangling.

**Rule — paste/insert faithfully, surface what doesn't resolve, never silently fix or drop** (the same
"faithful + surfaced" stance as cross-grid paste and broken cell-refs). On paste/insert: append the analyses
verbatim, then evaluate their expressions against the **destination's** VARs; unresolved variables show the
same quiet inline hint the expression preview already uses (§4.3 — "≈ unknown: f0"). The user defines the
missing VAR or edits the field. No auto-rewrite, no auto-drop.

### 5.2 Copy / paste (clipboard — within a session)
- **Copy (⌘C/Ctrl+C)** in the Analyses list → serialize the **selected** analyses (multi-select supported)
  to the clipboard as the `Analysis` JSON. A **"Copy All"** affordance copies the whole schematic's setup in
  one gesture (clone an entire schematic's analyses to another).
- **Paste (⌘V/Ctrl+V)** into another schematic's Analyses list → deserialize + **append**, with
  **name-collision resolution** (`SP1` → `SP1 copy` / next free `SP2`), then the §5.1 unresolved-reference
  surfacing. Paste is one undoable action.
- Works list-host or modal-host (same VM).

### 5.3 Templates (named, cross-session, cross-project) — `.canl`
A **template** is a **named multi-analysis bundle** saved to the user templates dir (alongside `.ccolor`
themes; resolution order workspace → user templates → bundled, mirroring themes). File extension **`.canl`**
("circuitRF analyses"). A `.canl` is just the same `Analysis`-JSON (one or many analyses) + a name + optional
description.

**Save as Template (the UX — designed now):**
- A **"Save as Template…"** button in the Analyses panel (and in the modal list host). It saves **the
  current selection**, or **all** analyses when nothing is selected (the button label reflects which:
  "Save Selected as Template…" vs "Save All as Template…" — or a single "Save as Template…" with a
  selected-vs-all segmented choice in the dialog; pick the clearer at build).
- Opens a small **Save-as-Template dialog** (mirrors `InputNameDialog` + a description field): **Template
  name** (validated via `NameValidator`; the file is `<name>.canl`), an optional **Description** (shown later
  in the picker), and a **preview list** of exactly which analyses will be saved (so the user sees the bundle
  contents before committing). Collision guard: if `<name>.canl` exists, offer overwrite or rename (don't
  silently clobber). **Save** default button, **Cancel** (Esc), centered labels (HIG).
- On save: write the `.canl` atomically (temp + rename) to the user templates dir; report the path via
  Messages (clickable), consistent with the save-reporting convention.

**Insert from Template:**
- An **"Insert from Template…"** item in the Add (➕) menu → a **template picker** (list of `.canl` by name +
  description, from the resolution chain). Selecting one **appends** its bundle to the current schematic's
  analyses (name-collision resolution + §5.1 unresolved-reference surfacing, same as paste).
- The picker offers **Manage** (rename/delete a template `.canl`) — minimal v1 (rename/delete; the files are
  also just visible on disk).

### 5.4 One serialization (locked)
`.csch` analyses section, clipboard payload, and `.canl` template file are **the same serialized
`Analysis`/`Measurement` JSON** (polymorphic, §3). Implement the serialize/deserialize **once** and reuse it
for all three destinations — do not write three encoders. This is the single-source-of-truth principle applied
to the analysis bytes; it guarantees copy/paste, save-as-template, and persistence never diverge.

---

## 6. Wiring into extraction + run (closes the 6e gap)

- The **extractor** (`NetExtractor`, 6e step 1) already produces a `TestBench`; now it also carries the
  **schematic's authored analyses + measurements** into `TestBench.Analyses`/`Measurements` (read from the
  `.csch` model). `CnlWriter` (step 2) already emits typed analyses + `measure` lines — confirm it emits the
  v1 types (DC/SP/HB) in the grammar `CnlReader` parses back (round-trip).
- **Run** (step 5) already dispatches `TestBench.Analyses` to engines and runs **enabled** ones. The "no
  analysis" message now only appears when the list is genuinely empty (or all disabled) — the gap is closed:
  a schematic the user gave an analysis now runs it.
- **S-param multi-segment:** the run flattens the segment list into the engine's frequency array (§2.1).

---

## 7. Implementation order (smallest correct first)
1. **Model additions** (§2): `SParameterAnalysis.Sweeps` list; `FrequencySpec` points-mode + expression-string
   fields; tests. Framework-free, headless. **DONE (2026-06-09).**
2. **`.csch` persistence + the one serialization** (§3/§5.4): analyses + measurements serialized (polymorphic)
   as the **single shared encoder** reused later for clipboard + template; `SchematicHasAnalyses` hook returns
   real; round-trip tests; IsTestBench flips on save. **DONE (2026-06-10).**
   - `src/Ui/Schematic/AnalysisSerialization.cs` — `CschAnalysis`/`CschFrequencySpec`/`CschMeasurement` DTOs
     + `AnalysisSerialization` (the ONE encoder: `Serialize`/`Deserialize` for clipboard/`.canl` + `ToDto`/
     `FromDto` helpers used by `.csch`). Type discriminator: `"dc"` / `"sp"` / `"hb"`; unknown tags skipped.
   - `SchematicEditModel` gains `Analyses: List<Analysis>` + `Measurements: List<Measurement>`.
   - `CschFile` gains `Analyses: List<CschAnalysis>?` + `Measurements: List<CschMeasurement>?` (null →
     omitted in file; absent on read → empty — graceful old-file load).
   - `SchematicPersistence.ToFileModel/FromFileModel` maps via `AnalysisSerialization.ToDto/FromDto`.
   - `SavePlanBuilder.SchematicHasAnalyses` now returns `model.Analyses.Count > 0` (was `false`).
   - 19 new tests in `tests/Ui.Tests/AnalysisSerializationTests.cs`; all 929 tests green.
3. **The Analyses list** (§4.1): dock panel + the list VM, Add/Edit/Remove/Enable/Duplicate/reorder, empty
   state; the modal-host option (same VM). **DONE (2026-06-10).**
4. **The Add/Edit dialog** (§4.2/§4.3): type picker + progressive per-type forms (DC, then S-param segment
   sub-list, then HB basic/advanced); expression fields + preview (reused). **Layer 1 DONE (2026-06-10).**
   **Layer 2 (SP segment sub-list) DONE (2026-06-10). Layer 3 (HB basic/advanced) DONE (2026-06-10).**
5. **Reuse** (§5): copy/paste (clipboard, multi-select + Copy All, name-collision + unresolved-ref surfacing);
   then templates — `.canl` save/insert, the **Save-as-Template dialog** (name + description + preview list,
   atomic write), the template picker (+ minimal Manage). Reuses the §5.4 serialization. **DONE (2026-06-10).**
   - `src/Ui/Schematic/AnalysisSerialization.cs` — `SerializeCanl`/`DeserializeCanl` + `CanlFile` DTO (§5.4 one
     serialization: same DTOs as `.csch` + clipboard; name + optional description wrapper).
   - `src/Ui/Schematic/TemplateManager.cs` — `AnalysisTemplate` record + `TemplateManager` (load all from
     resolution chain workspace→user, atomic `SaveTemplate`, `TemplateExists`, `DeleteTemplate`).
   - `src/Ui/Views/Dialogs/SaveAsTemplateDialog.axaml(.cs)` — name + description + preview list + collision
     guard (overwrite confirm via `SaveChangesDialog`); atomic write + `Messages.Success(path)`.
   - `src/Ui/Views/Dialogs/InsertFromTemplateDialog.axaml(.cs)` — template picker with preview + minimal Manage
     (delete); appends via `PasteAnalysesCommand` (same collision resolution + §5.1 surfacing as clipboard paste).
   - `AnalysesListViewModel` — `SaveAsTemplateCommand` + `InsertFromTemplateCommand` + `SetWorkspaceDir`.
   - `AnalysesListView.axaml` — InsertFromTemplate (FileImportOutline) + SaveAsTemplate (BookmarkPlusOutline) buttons.
   - `WorkspaceViewModel.OnCurrentWorkspacePathChanged` — calls `AnalysesTool.SetWorkspaceDir(dir)`.
   - `SaveChangesDialog` now supports `dontSaveLabel: null` to hide the secondary button.
   - 5 new `.canl` round-trip tests; all 956 tests green; firewall green.
6. **Extraction/run wiring** (§6): carry analyses into the extracted `TestBench`; confirm `CnlWriter`
   round-trips DC/SP/HB; Run executes enabled analyses; the "no analysis" message recedes. **DONE (2026-06-10).**
   - `NetExtractor.Extract` — layer 4 copies `model.Analyses` (enabled only) + `model.Measurements` into `tb`.
   - `CnlReader` — `TryParseDcDirective` (type=dc now round-trips as typed `DcAnalysis`); multi-segment SP
     merge (consecutive `analysis N type=sparam` lines with the same name collapsed into one
     `SParameterAnalysis` with all sweep segments); `TryParseMeasurementLine` extracts trailing unit token
     so `measure Name = expr unit` round-trips name/expression/unit exactly.
   - 8 new tests (5 `NetExtractorAnalysesTests` + 3 `CnlWriterTests`); all 977 tests green; firewall green.
   - "No analysis" message now appears only when the schematic genuinely has no enabled analysis.
7. **Measurements** (§4.4) minimal list; **loadpull/pursuit authoring** still deferred (note carried).

Steps 1–2 are model+persistence (headless, testable); 3–4 are the UI (the HIG-critical part); 5 adds reuse;
6 closes the loop; 7 is the tail.

---

## 8. Open / deferred
- **Loadpull / loadpull-pursuit authoring** — **DONE** (briefs 05/06): tuner-instance pickers, `.gam`
  grid/output refs, tone coeff+unit (var-unit-wins, brief 04b). Remaining v1 limitation: LP/LPP cannot be
  wrapped in a parametric sweep yet (loadpull is itself a 2-D sweep).
- **Thin Model A** (analysis-as-placed-component writing into this metadata list) — v2, owner-noted.
- **Parametric sweep nesting** (`ParametricSweepAnalysis`) — v1 may expose a single per-analysis design-var
  sweep; full nested parametric UI deferred.
- **Corners / Monte Carlo / component-parameter sweeps** — out of scope (VendorC territory); not planned for
  v1.
- **Measurement authoring depth** — v1 minimal (name/expr/unit list); richer measurement builder deferred.
- **Result pass/fail coloring** (VendorC's datasheet view) — belongs with Phase 7 (data display), not here.

# Data Display — Phase 7 Plan

Roadmap for the circuitRF Data Display. This is the **plan** document: it locks the decisions made in
the Phase 7 kickoff, fixes the architecture spine, and breaks the work into sub-phases. Finer design of
each sub-phase happens when we arrive at it (especially 7.4 contours, which is gated on the loadpull
spline white paper + reference code, not yet in hand).

Read with: `data-model.md` and `src/Core/Data/CLAUDE.md` (the DataSet/DataCube contract — the splotRF
seam), `data-export.md` + `RfCore/src/Export/CLAUDE.md` (`.npy` native format, exporter/importer),
`npy-data-consumer-guide.md`, `loadpull.md` (loadpull result shape), `ui-architecture.md` (Dock layout +
tear-off), `ui-design.md` / HIG, and `project-file-formats.md` (where the display-config format is registered).

The Data Display reuses **splotRF's proven Data Display** (its plot/trace/marker model, renderers, and
plot inspector) ported into circuitRF, with the modifications below.

---

## 1. Locked decisions (Phase 7 kickoff)

1. **Surface = a tear-off document tab.** The Data Display is a docked **document** in the workspace,
   consistent with the Schematic and Symbol Editor tabs, and **tears off** into its own window using the
   same mechanism those documents already use.
2. **Free-floating, file-addressed data.** A Data Display is **not** bound to one schematic or one run.
   It plots from **files**: `.npy` (circuitRF native) and Touchstone (`.sNp`). It can pull traces from
   *any* `.npy`, including files produced by other schematics or sessions. (See the data spine, §2.)
3. **Per-run `.npy`, overwritten, in a workspace-level `results/`.** A simulation run writes the
   canonical `.npy`(s) for that schematic and **overwrites them each run** (the "VendorA dataset on disk"
   model), in a workspace-level `results/` directory kept **external to the cell's on-disk folder
   structure** (results are never written inside the cell folder). **Path = `results/<schematicKey>/<analysisName>.npy`**
   (one clean single-analysis DataSet per file; naming rule + collision handling in §3/7.0). These files
   *are* the address the display uses for "this schematic's latest results" — a run-sourced trace is just
   an `.npy`-sourced trace pointing at the well-known path. Strict overwrite (no N-run history for now).
   Alpha "no back-compat / break the format freely" applies (`src/Core/Data/CLAUDE.md`): overwrite in
   place, never migrate.
4. **Starts empty; user authors it.** A new Data Display is a blank canvas the user populates. No
   auto-plot on run.
5. **Full layout persistence.** The authored display (tabs, plots, traces, markers, axes, data-source
   references) is saved to disk and reloads faithfully. **Templates are deferred** (a later, separate
   feature — a saved display reused as a starting point with re-pointed data sources).
6. **Performance is a first-class requirement.** The display must feel snappy and responsive. This
   constrains family-sweep expansion, contour fitting, and redraw (see §4).
7. **Multi-dimensional traces** are authored by **axis-role assignment** over a cube's named axes (X /
   pinned / family) — see §2 and sub-phase 7.3. A **family sweep is ONE trace object** that renders N
   curves; the user never adds/removes a trace per sweep point (see 7.3).
8. **Contours are first-class and fully user-controlled.** Form: *"Metric at constant value of a
   different metric"* — e.g. Pout at constant 3 dB compression, Efficiency at constant Pout = 45 dBm,
   Gain at constant back-off. Plottable in the **Γ-plane (Smith)** *or* the **Z-plane (Rect)**. Detailed
   design gated on the white paper + Python (sub-phase 7.4).

---

## 2. Architecture spine

### 2.1 One data model: DataSet / DataCube (already built)
Everything plotted is a `DataCube` from a `DataSet` (RfCore). This is already true of every engine
result (S-param, HB, parametric sweep, **and loadpull** — `LoadpullEngine.BuildLoadpullDataSet` emits
cubes). A **1-D slice of a cube is a trace**; the cube's named, unit-bearing axes carry the x-axis and
its units. `DataSetBuilder.FromSnp` already lifts a Touchstone file into an `S {freq,i,j}` cube, so
Touchstone overlay is the same machinery, not a parallel path.

splotRF today binds a trace to `(SourcePath, MatrixType, row, col)` → `S(i,j)` vs freq. That is exactly
the special case "slice `S {freq,i,j}` with `i,j` pinned, `freq` kept, apply a Y-format transform." The
Phase-7 generalization is to bind a trace to **(data source, cube name, per-axis slice spec, transform)**.

### 2.2 File-addressed data sources
The display owns a **data-source library** (the generalization of splotRF's `SnpLibrary`). Each source
is a file resolved to a `DataSet`:
- **`.npy`** → `DataSetImporter` → `DataSet` (the native path; includes simulation results).
- **Touchstone `.sNp`** → `TouchstoneIO` → `SNP` → `DataSetBuilder.FromSnp` → `DataSet`.

A source is addressed by **path**. Sources reload on change (splotRF already does file reload + broken-
entry handling). "This schematic's latest results" is the well-known per-run `.npy` (§1.3); when a run
overwrites it, bound traces refresh — no special in-memory coupling.

> **Why file-addressed (not in-memory handle):** it makes free-floating, cross-file overlay, persistence,
> and the VendorA-style on-disk dataset all fall out of one mechanism. A future optimization may plot the
> in-memory `RunResult.DataSet` directly to avoid the write→read latency, but the file address stays the
> canonical, persistable identity of a trace's data.

### 2.3 Trace = source + cube + slice + transform
A trace references: data source (path), cube name, a **slice spec** (per named axis: pin-to-index /
keep-as-X / family-iterate), and a value transform (`dB20`/`dB10`/`mag`/`phase`/`real`/`imag`/`conj` or
none). The existing splotRF trace fields (line/marker style, secondary axis, Z0, table formatting,
per-trace markers) are retained.

**Store trace identity as separate components, never a pre-joined string.** The identity is roughly
`<source> · <analysis> · <cube/quantity> · <slice> · <transform>`, and each must be a distinct field on
the trace. This is what makes the minimal display-name policy (§2.7) cheap; a pre-joined label makes it
painful. (Where source/analysis come from the `.npy` path — `results/<schematicKey>/<analysisName>.npy` —
the analysis component is the file stem.)

### 2.4 Plot types
Reuse splotRF's set: **Rect** (X vs Y), **Smith**, **Polar**, **Table**. Phase 7 adds **contour** as a
rendering capability **on the Γ-plane (Smith) and Z-plane (Rect)** substrates (7.4), not a fifth bare
type. Overlay rules (locked): a contour plot may overlay 1-port Touchstone (`.s1p`) reflection data;
it does **not** mix with power-sweep line traces (e.g. Gain vs Pin) — those are a different plot.

### 2.5 Persistence
The authored display serializes to a circuitRF display-config file with extension **`.cdd`** ("circuitRF
data display", locked). splotRF's `.splot` is the structural template: tabs → plot containers → traces →
markers + axes windows. Open item: whether/how to register `.cdd` in `project-file-formats.md` and the
workspace (a 7.1 detail). Data is **not** embedded; only source paths + slice/style state are stored, and
sources reload on open (matches splotRF). Templates (a display reused with re-pointed sources) are
deferred.

### 2.7 Trace display name — minimal-label policy
The `.npy` **path** is a fully-qualified, stable machine address (verbose is fine). The trace's **display
name** in the plot is the opposite: show the **least** that still disambiguates. The label is computed at
the **plot/legend level** (across the traces sharing that plot), not baked into each trace — it is a
function of *what varies in this plot right now*:
- Any identity component (§2.3) that is **constant across all traces in the plot is dropped**; the label
  is the shortest suffix that still tells the traces apart. One analysis in the plot → analysis name
  dropped (`S21 (dB)`, not `SP1·S21`). Add a second analysis → it reappears only where needed
  (`SP1·S21`, `HB1·V(drain)`). One source → source prefix dropped; two sources → it returns.
- This is the same idea splotRF already ships in `SnpLibraryViewModel.UpdateDisplayNames` (shortest unique
  filename suffix), lifted from file paths up to trace-identity components.
- A **family** factors its shared part into the legend title; each curve is labelled by its family-axis
  value (`Pin = −10 dBm`, …).
- **Auto by default, with a per-trace user override** (an explicit custom name always wins).
- The label **recomputes when traces are added/removed** from the plot.
Detailed in sub-phase 7.2 (it is a 7.2 concern; recorded here so the trace identity model carries the
components separately from the start).

### 2.6 Architectural firewall (unchanged, applies here)
The Data Display is **UI** — it lives in `src/Ui` and may use Avalonia/Skia. `DataSet`/`DataCube`/
`DataSetImporter` are RfCore (no Avalonia). Any contour/spline **math** is framework-free and belongs in
RfCore or `src/Engine` (consumable headless, testable, and potentially shared with splotRF), **not** in
the UI. The in-process `DataSet`/`DataCube` API remains lockstep with splotRF (`src/Core/Data/CLAUDE.md`
→ "Change carefully").

---

## 3. Sub-phases

Each sub-phase is independently shippable and gets its own detailed design + Sonnet briefs when we reach
it. Gates are concrete acceptance checks.

### 7.0 — Data path spine + per-run `.npy` (small, no display UI)
**Goal:** make simulation results reachable on disk in the canonical, addressable form the display will
consume.
**Naming rule (LOCKED):** write **one `.npy` per analysis** at
**`<workspaceRoot>/results/<schematicKey>/<analysisName>.npy`**, where:
- `<schematicKey>` = the **cell name**, or `<Cell>.<View>` **only when the schematic view stem differs
  from the cell name** (a pure function of the schematic's own identity → stable as sibling views are
  added). Derive from the active `SchematicDocument.FilePath` matching `…/<Cell>/schematic/<View>.csch`
  (cell = folder above `schematic/`, view = file stem). For a **loose** schematic not under a
  `…/<Cell>/schematic/` layout, `<schematicKey>` = the file stem.
- `<analysisName>` = the analysis's own name (unique within a testbench). Each file is a **clean
  single-analysis DataSet** with canonical cube names (no analysis-prefixing — `ds.S(2,1)`/`ds.V(...)`
  still resolve).
- **Collision handling = detect-and-warn (Option A):** if a `results/<schematicKey>/` directory is
  already owned by a *different* cell path (two same-named cells from different libraries), do **not**
  silently rename/suffix — surface a `Message` telling the user to rename one cell. Keeps names pristine
  and stable so saved `.cdd` references never break.
- **Scratch / no-workspace:** no workspace `results/` exists → write to the per-session recovery working
  dir: `<recovery-session>/results/<TabTitle>/<analysisName>.npy`, discarded on clean exit (this is the
  scratch-results persistence deferred by `scratch-and-save-lifecycle.md` §9). On materialization, later
  runs write to the real workspace `results/`.
**Deliverables:** on a successful run, write/overwrite the per-analysis `.npy`(s) from `RunResult.DataSets`
via `DataSetExporter` at the path above; clear/overwrite the schematic's `results/<schematicKey>/` set each
run; emit the collision Message when applicable. Confirm the `export → import` round-trip holds for every
analysis type (S-param, HB, parametric sweep, loadpull).
**Gate:** run a schematic with ≥1 analysis → `results/<schematicKey>/<analysisName>.npy` appears/updates;
`DataSetImporter` reconstructs an equivalent DataSet (symmetry oracle) for each analysis type; a forced
same-name-cell collision produces the warn Message and does not clobber the other cell's results.
**Note:** no display UI in 7.0 — this is the data-path spine only.

### 7.1 — Data Display document shell (the port)
**Goal:** splotRF's Data Display canvas living inside circuitRF as a **tear-off document** — empty canvas,
multiple plot containers, pan/zoom, tabs, layout persistence. No simulation data yet (Touchstone-only or
empty is fine for this sub-phase).
**Deliverables:** port `Plot`/`Trace`/`Axes`/`Marker` models, the renderers (`PlotRenderer`,
`AxesRenderer`, `TableRenderer`, trace/marker renderers), and the container/inspector view models; host
them in a Dock **document** that tears off like `SchematicDocument`/`SymbolEditorDocument`; retarget fonts
**DejaVu → IBM Plex** (`SkiaFonts`); define + wire the `.cdd` layout-persistence file.
**Gate:** open a Data Display tab, author plots, tear it off, save + reload layout faithfully (to `.cdd`);
renders match splotRF visually (modulo font).
**Open Q:** how a free-floating display is created/owned in the Dock model (menu/command, not tied to the
active schematic).

### 7.2 — DataSet as the trace data source (1-D parity)
**Goal:** retarget the trace data binding from SNP-only to the unified DataSet source library; reach
splotRF parity for the common cases driven by circuitRF runs and `.npy`/Touchstone files.
**Deliverables:** the data-source library (§2.2); trace bound to `(source, cube, slice, transform)`; the
trace picker for ≤2-D cubes (S(i,j) vs freq, HB spectra, etc.); Smith/Polar/Rect/Table + markers working
off cubes; Touchstone overlay via `FromSnp`.
**Gate:** plot S21 in dB from a run's `.npy` and from a Touchstone file in the same Rect plot; Smith S11;
a table; markers read correctly. Matches splotRF behavior for the `{freq,i,j}` case.

### 7.3 — Multi-dimensional sweep trace dialog
**Goal:** author traces from cubes with >2 axes via **axis-role assignment**, matching the Analyses-
Properties UX style.
**Deliverables:** trace dialog assigns each cube axis a role — **X** (kept), **pinned** (single index,
value picker against the axis), or **family** (iterate → render one curve per index); value cube +
transform choose the Y. Maps 1:1 onto `DataCube.Slice` (int pins, `Range` keeps).
**A family is ONE trace object that renders N curves** — one entry in the trace list, one style
definition (with an automatic color/style progression across the family so curves are distinguishable),
one delete. The user edits a single trace and changes an axis's role; the rendered curve count follows
the data. Do **not** materialize N trace objects. Family guardrails for performance (cap / "every Nth" /
warning — see §4).
**Gate:** from an HB sweep cube (e.g. `V {node,harmonic,Pin}`), plot |V(node,1)| vs Pin; switch X to
harmonic and make Pin a family; confirm trace count + responsiveness within the guardrail.
**Open Q:** exact family guardrail policy (default cap, behavior past it).

### 7.4 — Loadpull contours (Γ-plane + Z-plane)  ⛔ design gated on white paper + Python
**Goal:** first-class contour plotting with full user control, form *"Metric at constant value of a
different metric."*
**Known inputs (from kickoff):**
- Contour examples: Pout @ const 3 dB compression; Efficiency @ const Pout = 45 dBm; Gain @ const back-off.
- Substrates: Γ-plane (Smith) **and** Z-plane (Rect).
- Overlay: 1-port Touchstone (`.s1p`) reflection allowed; power-sweep line traces not mixed in.
- The **2D-spline surface engine** (to be supplied) fits the scattered loadpull field **and** can
  synthesize power drive-ups at load points **off** the simulation grid — i.e. it is a *surface model /
  data-synthesis engine*, not merely an iso-line tracer. The user has a white paper, reference Python,
  and a discussion chat to share; the data-generation method becomes clear from those.
**Data on hand:** loadpull DataSet already carries the raw scattered field — FOM cubes
(`Pout`/`PAE`/`Gt`/`Gp`/`DE`/`Pdc`/…) over `{gridPoint, pinStep}` plus `GammaLoad`/`ZLoad` over
`{gridPoint}`. "At constant 3 dB compression" etc. are per-grid-point reductions/interpolations over
`pinStep`; the spline then fits value-vs-Γ (or vs Z) and extracts iso-levels.
**Key open design question (storage):** does the **trace** own the fitted spline surface (derived,
cached, recomputed on input change) or does the **DataSet/cube** store it? Current lean: the **cube
stores only the honest measured field; a derived surface-model object owns the fit + iso-extraction +
off-grid synthesis**, computed at author/render time and cached. The off-grid-driveup capability argues
the spline may deserve a **first-class `LoadpullSurface` model** (framework-free, RfCore/Engine) that
both contour extraction and off-grid queries call — but the final shape waits on the white paper.
**Deliverables (to be detailed):** the surface-model engine (headless, tested); contour authoring UI
("metric @ constant other-metric = value", level set, substrate Γ/Z); iso-line rendering on Smith and
Rect; `.s1p` overlay.
**Gate:** reproduce a known contour set from a loadpull run (owner-verifiable against the Python
reference); off-grid driveup synthesis matches the reference within tolerance.
**Action:** obtain the white paper, the Python implementation, and the discussion chat before designing
this sub-phase. Likely warrants its own design note (`docs/design/loadpull-contours.md`).

### 7.5 — Data Display templates (DEFERRED)
A saved display reused as a starting point with re-pointed data sources. Out of scope for now; recorded
so 7.1 persistence is designed to not preclude it.

---

## 4. Cross-cutting concerns

- **Performance / responsiveness (locked requirement).** Family-sweep expansion is capped (policy TBD,
  7.3); contour fitting is cached on the surface model and recomputed only on input change (7.4); redraw
  stays incremental. Avoid per-frame full rebuilds (same discipline as the schematic renderer's 10k
  budget). Large cubes: decimate for display, full data for export/markers. Prefer plotting from the
  in-memory DataSet when the file write is the bottleneck (later optimization, §2.2).
- **HIG.** Most of this phase is user-facing chrome; follow `ui-design.md` / platform HIG closely
  (dialogs, spacing, keyboard, focus). The trace and contour dialogs match the **Analyses Properties**
  pattern (list + typed body + clean property rows + live preview).
- **Fonts.** circuitRF uses **IBM Plex**; splotRF ships DejaVu. The port retargets `SkiaFonts` and any
  hard-coded font asset URIs.
- **Firewall.** UI in `src/Ui`; contour/spline math headless in RfCore/Engine; DataSet API lockstep with
  splotRF (§2.6).
- **Alpha file-format freedom.** Both the per-run `.npy` and the `.cdd` layout file are alpha-unstable:
  break freely, upgrade writer+reader together, no migration (`src/Core/Data/CLAUDE.md`).
- **Use "VendorA tool" if referring to 3rd party tool is nessesary.

---

## 5. Open questions to resolve (by sub-phase)

- **7.0:** RESOLVED — `results/<schematicKey>/<analysisName>.npy`, one DataSet per analysis,
  detect-and-warn on same-name-cell collision (Option A), scratch → recovery-session results dir.
- **7.1:** `.cdd` registration in `project-file-formats.md` / workspace; how a free-floating display is
  created/owned in Dock.
- **7.3:** family-sweep guardrail policy (default cap, over-cap behavior).
- **7.4:** spline storage (trace-owned cache vs first-class `LoadpullSurface`); which FOMs are first-class;
  how "constant other-metric = value" is specified in the UI; contour level-set specification. **All gated
  on the white paper + Python + discussion chat.**

---

## 6. Status
- Decisions §1 locked (kickoff). Architecture §2 proposed (file-addressed spine).
- No code yet. Next: confirm §5 7.0/7.1 items, then sub-phase 7.0 (data path + per-run `.npy`).
- Sub-phase 7.4 (contours) blocked pending the loadpull spline white paper, reference Python, and the
  discussion chat.

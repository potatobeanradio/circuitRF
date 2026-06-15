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

### 2.8 Plot Inspector — the AnalysisEditor × splotRF-PlotInspector merge
The Data Display's property editor **merges two existing UIs** (locked direction):
- **Function + panel-feel from splotRF's `PlotInspectorView`:** a **live, docked, per-plot properties
  inspector** (not a modal dialog). Plot-level row (plot Type / Freq unit / table Font), an **Add Trace**
  action, a scrollable **trace-card list**, per-card add/remove, **immediate redraw on every edit**,
  color-swatch combos and Material-icon combos (marker shape, secondary-axis arrow, trash). Every change
  re-renders the plot at once — the inspector is always-available, not an OK/Cancel modal.
- **Visual language from circuitRF's `AnalysisEditorDialog` + body views:** circuitRF theme brushes
  (`SystemRegion`/`SystemChrome*`/`SystemBase*`, `CrfWarningBrush`), **opacity-tiered labels** (~0.6 field
  labels, ~0.55 secondary/preview text), **segmented `.active` toggle buttons** for mode-like choices
  (in place of plain combos where a 2–3-way toggle reads better), **rounded section/row borders** (the
  splotRF `traceCard` becomes a circuitRF rounded row), an **Advanced expander** for less-common controls,
  **live `≈` preview** under any field that takes an expression, compact dense spacing, IBM Plex, HIG.
- **Per-trace-kind card bodies (locked).** A trace card is to a plot what an analysis body is to the
  Analyses editor: the card's body is **swapped by trace kind**. A normal line/marker/table trace today;
  a **contour trace** (7.4) is just another card kind with its own body (metric @ constant other-metric,
  level set, Γ/Z). Build the inspector so trace-kind → card-body is the extension point — this is the
  AnalysisEditorDialog typed-body pattern applied to traces, and it is what makes 7.4 additive rather than
  a rewrite.
- **Net:** keep splotRF's *interaction model and trace-card density*; restyle it in circuitRF's *visual
  idiom* so it sits next to the Analyses editor as a sibling. The trace **data picker** inside each card is
  the DataSet/cube seam (becomes axis-role assignment in 7.2/7.3); 7.1 ports the inspector chrome with the
  picker still SNP/Touchstone-backed.
- **Surface = dual, like Analyses (locked).** The inspector content is **one reusable view** hosted in
  **both** (a) a **per-plot fly-out** on the plot container (splotRF's feel — the primary, always-at-hand
  affordance) **and** (b) the docked **Properties** tool, which shows the selected plot's inspector — exactly
  mirroring how the Analyses editor is available both docked and as its own window. Author the inspector as a
  self-contained `UserControl` + VM so neither host owns it; both just present it.

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

### 7.1 — Data Display document shell (the splotRF port)
Port splotRF's Data Display into circuitRF as a tear-off Dock **document**. **Porting substrate (confirmed
— the port is low-risk):** both apps are on **Avalonia 12.0.3** (circuitRF csproj: "match splotRF versions"
— no 11→12 migration); **Material.Icons.Avalonia 3.0.2** is already referenced (splotRF's inspector icons
port as-is); **Dock.Avalonia 12** tear-off is already wired (`DefaultHostWindowLocator`/`HostWindow`);
`CircuitRfDockFactory.OpenDocument(Document)` adds any document to the center DocumentDock; a document's
view resolves via an explicit `<DataTemplate DataType=...>` in `App.axaml` (as `SchematicDocument` →
`SchematicView`); `StubKind.DataDisplay` is already reserved. The port is: copy splotRF files → rename
namespace `splotRF` → `CircuitRF.Ui.DataDisplay` (views under `CircuitRF.Ui.Views.DataDisplay`) → retarget
fonts (DejaVu → IBM Plex) + colors (RenderTheme → circuitRF ColorTheme) → integrate as a Dock document →
swap data source (7.2). Firewall: it is all UI (`src/Ui`), may use Avalonia/Skia.

**This is large — build in steps, brief one at a time:**

#### 7.1a — Dock document shell + empty canvas (de-risk the integration first)
New `DataDisplayDocument : Document` (clone `SymbolEditorDocument`'s `FilePath?`/`IsScratch`/`IsDirty`/
`Materialize` surface) wrapping a new `DataDisplayViewModel`; a **New Data Display** command in
`WorkspaceViewModel` (next to New Schematic/New Symbol) that constructs it and calls
`_factory.OpenDocument`; a placeholder `DataDisplayView` (empty canvas, "add a plot" affordance); the
`App.axaml` DataTemplate mapping; track open displays for save/close participation. No plots/renderers yet.
**Gate:** New Data Display opens a tab, tears off into its own window, re-docks, closes cleanly.

#### 7.1b — Plot model + renderers (Rect first), Touchstone-sourced, fonts→Plex
Port `Plot`/`Trace`/`Axes`/`Marker`/`Misc` + renderers (`PlotRenderer`/`AxesRenderer`/`TableRenderer`/
trace+marker renderers/`RenderTheme`/`SkiaFonts`) and a `PlotControl`. Render ONE Rect plot from a loaded
Touchstone/`.npy` onto the canvas. Retarget `SkiaFonts` to IBM Plex and `RenderTheme` to circuitRF colors.
(This is the heaviest step.) **Gate:** a Rect S21(dB) trace from a file renders correctly in a plot.

#### 7.1c — Port the splotRF Data Display engine into the document (P1: faithful-first)
**Strategy (locked — P1).** splotRF's canvas/container/`PlotControl`/inspector are one tightly-coupled
engine: the container (`PlotContainerViewModel`) *owns* its `PlotInspectorViewModel`; the full `PlotControl`
bundles pan/zoom + markers + the inspector flyout; the container view subscribes to all of it; pan/zoom
lives up in `DataDisplayViewModel`; move/resize push undo commands; and the container geometry carries
subtle complex-plot viewport / label-strip math (`TopLabelExtraLogical`/`BottomLabelExtraLogical` mirror
the renderer formulas). Stripping it to a skeleton would re-derive that math and invite bugs, so port it
**as intact as possible, splotRF-styled** — get a working multi-plot / multi-tab canvas first, *then*
restyle the inspector (7.1d). **Markers and `.cdd` persistence are the only deferrals** (markers → 7.1d;
persistence → 7.1e). Build in compile-and-run-gated slices:
- **7.1c-1 — VM layer.** Port the view-model stack → namespace `CircuitRF.Ui.DataDisplay.ViewModels`:
  `PlotViewModel`, `PlotContainerViewModel`, `DataDisplayViewModel` (the canvas VM — distinct from the
  7.1a *document* VM), `TabViewModel`, `DisplayWindowViewModel`, `PlotInspectorViewModel`,
  `TraceRowViewModel`, `SnpLibraryViewModel`, `LabelStripViewModel`, the item VMs
  (`ColorItem`/`MarkerTypeItem`/`YAxisItem`/`TraceDataItem`/combo-item/`ComplexStringHelper`), plus
  `UndoRedo` and `AppSettings(ViewModel)`. Keep SNP-backed data (DataSet seam is 7.2). Marker-VM members
  may be ported but left unwired until 7.1d. **Gate:** builds green; design-instance/smoke coverage where
  practical.
- **7.1c-2 — the interactive control + its flyout views.** Grow the 7.1b render-only `PlotControl` into
  splotRF's interactive control (pan, right-drag secondary pan, scroll-zoom, context menu,
  double-tap-to-inspect). **Its flyout views travel with it** (the flyouts host them, so they're needed to
  compile): port `PlotInspectorView` (splotRF-styled — restyle is 7.1d), `AxesLimitsView`, `AxesLabelsFlyout`
  + their converters; also `AxisLabelControl` + `DragSelectOverlay`. **Fix the `canvas.Clear()` from 7.1b**
  — adopt splotRF's no-Clear discipline (the Skia lease is the shared scene canvas; clearing wipes sibling
  plots). **Marker views ride along (compile deps):** `PlotControl`'s context-menu / double-tap hard-reference
  `MarkerEditorView` + `MarkerInfoBoxView.PopulateMarkerMenu` (and `PlotExporter` for Export/Copy), so those
  port here too. Marker *code* ships in this slice; the marker **runtime wiring** (container providers + the
  canvas info-box overlay) completes in 7.1c-3, and splotRF's null-guards keep the single-plot harness safe.
  Verify on a temporary single-`PlotControl` harness (extend the 7.1b one; `EnablePanning=True`, wire
  `DoubleTapped`→`HandleDoubleTapAt`). **Gate:** one plot pans/zooms;
  double-click opens the (splotRF-styled) inspector flyout and edits redraw live; multiple stacked plots
  composite without wiping each other.
- **7.1c-3 — the canvas + containers, wired into the document (replace the harness).** Split into two:
  - **7.1c-3a — the real canvas + containers + provider wiring (single tab).** Port the splotRF per-tab
    canvas (`DataDisplayView` → **rename `PlotCanvasView`** to avoid colliding with circuitRF's document-level
    `DataDisplayView`) + its interaction code-behind (middle-pan / drag-select / scroll-zoom / background-
    deselect) + `PlotContainerView` (move/resize/select code-behind + label strips). **Reconcile the VM:**
    `DataDisplayDocumentViewModel` **owns** a ported `DisplayWindowViewModel` (`Window`) — wrap, don't merge;
    expose `IsDirty`; remove the 7.1b demo harness. The document view hosts the **active tab's** `PlotCanvasView`
    (no tab strip yet). **Wire the container providers** (`NextMarkerIndexProvider`/`FindMarkerInfoBoxVmProvider`/
    `ContainerProvider`/`SelectedMarkersProvider`) to each `PlotControl` so markers + the canvas info-box overlay
    come alive. **Gate:** add/move/resize/select/delete plots of each type (Rect/Smith/Polar/Table) on one canvas;
    pan/zoom/drag-select; markers add/move/show info boxes; the (splotRF-styled) inspector flyout edits live.
  - **7.1c-3b — chrome: tabs + toolbar + library + Load Touchstone + docked inspector.** Port `TabHeaderView`
    + a `TabControl` (tabs → `PlotCanvasView`); an **in-document toolbar** (Add Plot / New Tab / Zoom / Fit /
    Undo-Redo — splotRF's View menu re-homed as a toolbar like the Schematic toolbar; the splotRF app menu is
    dropped, circuitRF's workspace owns it) + the document view's `KeyBindings`; the `SnpLibraryView` panel and
    **re-enable `OpenFileCommand` (Load Touchstone)** (the file dialog stubbed in 7.1c-1) so plots can be authored
    from files; and the splotRF docked inspector panel (right column, faithful — §2.8 Properties-dock unification
    is 7.1d). Save/Open Display (`.cdd`) stays 7.1e. **Gate:** multiple tabs; load a `.sNp` and author plots from
    it across tabs; toolbar + shortcuts work; behavior matches splotRF.

#### 7.1d — Restyle the inspector to the §2.8 merge (+ dual surface, marker polish)
Most of the **marker system** lands in 7.1c (code in `PlotControl`/7.1c-2; overlay + provider wiring in
7.1c-3). 7.1d is the circuitRF **visual restyle** of the inspector + the **dual surface**. Idiom reference
(grounded): `Views/Dialogs/AnalysisEditorDialog.axaml` + `Views/Analyses/SpBodyView.axaml` — segmented
`Button.seg-btn`/`.active` toggles, opacity-tiered labels (≈0.6 field / ≈0.55 preview), `SystemChromeLowColor`
rounded rows (`CornerRadius=4`) inside `SystemRegionColor` body cards (`CornerRadius=6`), live `≈`-style
preview text, `CrfWarningBrush` for errors, FontSize 10/11/12 tiers, inherit the app/dialog default font (same
as the AnalysisEditor — don't hardcode a divergent family). Sliced:
- **7.1d-1 — restyle `PlotInspectorView` (owner visual spec).** Apply the idiom to the flyout inspector.
  **Owner-specified layout:** plot type becomes a **centered segmented glyph-button header row** (Rect/Smith/
  Polar/Table, `.active` highlights the current type — needs `IsRect/Smith/PolarPlot` flags + `SetPlotType…`
  commands); the freq combo moves to a second row with the **`+ Trace` button on the left**. **Denser cards:**
  compact (smaller-font) combos; **MatrixType** → lettered S/Y/Z boxes; **LineType** → drawn line-sample
  glyphs; **MarkerType** → small shape glyphs; **Line/Symbol** enables → equal-size glyph **toggle buttons**
  (fixes the checkbox text-width misalignment). Data picker stays SNP/Touchstone (DataSet seam is 7.2). Trace
  body structured as a per-kind region (the extension point that makes 7.4 contour cards additive). **Gate:**
  the flyout reads as an AnalysisEditor sibling; every edit still redraws live; all plot types + trace controls
  work.
- ~~**7.1d-2 — dual surface (Properties dock).**~~ **COMPLETE.** `PlotInspectorView` hosted in Properties
  dock as a fourth context (`IsDataDisplayActive` / `PlotInspectorVm` on `PropertiesTool`);
  `RouteDataDisplayProperties` in `WorkspaceViewModel` subscribes to `DisplayWindowViewModel.ActiveInspector`
  on activation; tree-selection guard preserves plot inspector when clicking tree nodes. Build 0W/0E; 1206
  tests pass.
- **7.1d-3 — marker polish.** ✅ Restyle `MarkerEditorView` to the idiom; tidy marker undo edge-cases. **Gate:**
  markers add/move/edit/read correctly; the editor matches the inspector idiom.
  **Phase 7.1d-3 — COMPLETE.** Marker editor restyled to the inspector idiom (7.1d-3a) + stale-marker guard
  added to `MarkerEditorViewModel` (7.1d-3b): `MarkerIsLive` check guards all nine model-mutating paths
  (`OnNameChanged`, `OnMatrixFormatChanged`, `OnStyleChanged`, `OnDigitsChanged`, `OnUseNormalizedChanged`,
  `OnFormatStringChanged`, `OnIsMultiChanged`, `OnIsDeltaChanged`, `CommitFrequency`); edits no-op when the
  marker has been removed via undo, and resume correctly after redo restores the same instance. Undo coverage
  intentionally left as-is per owner's MINIMAL decision. Build 0W/0E.

#### 7.1e — `.cdd` layout persistence
Port `DataDisplayConfig` → the `.cdd` model (System.Text.Json, `[JsonStringEnumConverter]`, nullable/
defaulted fields, `format_version` reject-on-mismatch — mirror `project-file-formats.md`). Save/Open the
authored display; wire document dirty/save into the existing Save/close-prompt machinery; register `.cdd`.
**Gate:** author a display, save `.cdd`, reload faithfully (tabs/plots/traces/markers/axes/sources);
tear-off window placement round-trips.

**Open Q (7.1d):** inspector surface (see §2.8). **Open Q (7.1e):** `.cdd` registration in
`project-file-formats.md` / workspace `.cws`.

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
- **7.1:** RESOLVED — re-sliced into 7.1a–7.1e (§3). **Port strategy = P1 (faithful-first):** port the
  coupled engine splotRF-styled (7.1c, sliced VM→controls→views), then restyle the inspector + add markers
  (7.1d). Inspector surface = dual (per-plot fly-out + Properties dock, §2.8); per-trace-kind card bodies;
  Material.Icons.Avalonia reused. Remaining: `.cdd` registration in `project-file-formats.md` / `.cws`
  (7.1e detail).
- **7.3:** family-sweep guardrail policy (default cap, over-cap behavior).
- **7.4:** spline storage (trace-owned cache vs first-class `LoadpullSurface`); which FOMs are first-class;
  how "constant other-metric = value" is specified in the UI; contour level-set specification. **All gated
  on the white paper + Python + discussion chat.**

---

## 6. Status
- Decisions §1 locked (kickoff). Architecture §2 proposed (file-addressed spine).
- **Phase 7.0 — COMPLETE.** Per-run `.npy` results writer; `RunResultsWriter`; `AnalysisResult` record; 16 new tests.
- **Phase 7.1a — COMPLETE.** `DataDisplayDocument`/VM/View shell, `NewDataDisplayCommand`, DataTemplate.
- **Phase 7.1b — COMPLETE.** splotRF plot model + Skia renderers ported, fonts → IBM Plex, render-only `PlotControl`.
- **Phase 7.1c — COMPLETE.** VM layer, interactive `PlotControl` (pan/zoom/flyouts), canvas + containers + provider wiring, full chrome (tabs/toolbar/library/inspector).
- **Phase 7.1d-1 — COMPLETE.** `PlotInspectorView` restyle, `PlotTypeGlyphControl`, `IconSelectButton`, accent colors, line glyph, HighlightSelected, trash button, slider fix.
- **Phase 7.1e — COMPLETE.** `.cdd` layout persistence: `format_version` field (reject-on-mismatch), Save/Open Display dialogs wired in `DataDisplayView.axaml.cs`, Save/Open toolbar buttons. Round-trips active tab, canvas zoom/pan, per-plot axes zoom exactly. Build 0W/0E; 1206 tests pass.
- **Phase 7.1f — COMPLETE.** Data Display workspace/tree integration: File → "Open Data Display…" menu item (`OpenDataDisplayFileCommand`); `.cws` open-doc persistence (kind `"datadisplay"`) + restore; `WorkspaceScanner.Scan` enumerates loose files at workspace root (`.cdd` → `NodeKind.DataDisplayFile`); `OpenNode` double-click opens `.cdd` in the Content pane via `OpenOrActivateDataDisplay`. Core helper `OpenOrActivateDataDisplayCoreAsync` (stream or path); fire-and-forget wrapper for restore/tree paths. Build 0W/0E; 1206 tests pass.
- Sub-phase 7.4 (contours) blocked pending the loadpull spline white paper, reference Python, and the
  discussion chat.
- **Phase 7.1d-2 — COMPLETE.** Properties-dock dual surface for the Data Display inspector. Build 0W/0E; 1206 tests pass.
- **Next:** Phase 7.1d-3 (marker editor polish).

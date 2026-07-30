# Data Display — Phase 7 Plan

Roadmap for the circuitRF Data Display. This is the **plan** document: it locks the decisions made in
the Phase 7 kickoff, fixes the architecture spine, and breaks the work into sub-phases. Finer design of
each sub-phase happens when we arrive at it. Sub-phase 7.4 (contours) now has its own detailed design note,
`docs/design/loadpull-contours.md` (reference materials in hand).

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
   canonical results `.npy` for that schematic and **overwrites it each run** (the "VendorA dataset on
   disk" model), in a workspace-level `results/` directory kept **external to the cell's on-disk folder
   structure** (results are never written inside the cell folder). **Path = `results/<schematicKey>.npy`**
   (flat — see `results-dataset-layout.md`'s own resolved Open Question 1; the per-schematic-
   subdirectory form `results/<schematicKey>/run.npy` that had shipped here was a documented-vs-
   implemented divergence, fixed by brief-results-storage-and-data-display.md, R-res-1)
   — **one grouped DataSet for the whole testbench** (a group per analysis + a `measurements` group),
   superseding the original one-file-per-analysis decision. The grouped layout, `Analysis.Cube`
   addressing, and the storage format are specified in **`results-dataset-layout.md`** (naming rule +
   collision handling still in §3/7.0). This file *is* the address the display uses for "this schematic's
   latest results." Strict overwrite (no N-run history for now). Alpha "no back-compat / break the format
   freely" applies (`src/Core/Data/CLAUDE.md`): overwrite in place, never migrate.
4. **Starts empty; user authors it.** A new Data Display is a blank canvas the user populates. No
   auto-plot on run.
5. **Full layout persistence.** The authored display (tabs, plots, traces, markers, axes, data-source
   references) is saved to disk and reloads faithfully. **Templates are deferred** (a later, separate
   feature — a saved display reused as a starting point with re-pointed data sources).
6. **Performance is a first-class requirement.** The display must feel snappy and responsive. This
   constrains family-sweep expansion, contour fitting, and redraw (see §4).
7. **Multi-dimensional traces** are authored by **axis-role assignment** over a cube's named axes (X /
   pinned / family) — see §2 and sub-phase 7.3. A **family sweep is ONE trace object** that renders N
   curves; the user never adds/removes a trace per sweep point (see 7.3). The family system as shipped —
   roles, the `~` shorthand marker, auto-recognition, and rendering — is documented in `family-curves.md`.
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
painful. (The source is the run's one `run.npy`; the **analysis** component is the cube's **group** within
that file — addressed `Analysis.Cube`, e.g. `HB1.V` — see `results-dataset-layout.md`.)

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

> **SUPERSEDED (grouped-dataset layout), then FLATTENED (brief-results-storage-and-data-display.md).**
> 7.0 originally wrote **one `.npy` per analysis** (`results/<schematicKey>/<analysisName>.npy`). That
> was superseded by one grouped `run.npy` per run — but it shipped as
> `results/<schematicKey>/run.npy` (a per-schematic SUBDIRECTORY), diverging from this document's own
> stated intent. The current, corrected model writes **flat**: `results/<schematicKey>.npy` — one file,
> directly in the shared `results/` directory alongside every other schematic's results and any
> user-named baseline (R-res-2), containing every analysis as a group plus a `measurements` group,
> addressed `Analysis.Cube`. The per-schematic-directory `.source` collision marker is **gone, not
> rehomed** (R-res-0a) — `<schematicKey>` already disambiguates `cell` from `cell.view`. See
> **`results-dataset-layout.md`** for the current spec; the `<schematicKey>` derivation and
> scratch-results rules below are unchanged.

**`<schematicKey>` rule (LOCKED):**
- `<schematicKey>` = the **cell name**, or `<Cell>.<View>` **only when the schematic view stem differs
  from the cell name** (a pure function of the schematic's own identity → stable as sibling views are
  added). Derive from the active `SchematicDocument.FilePath` matching `…/<Cell>/schematic/<View>.csch`
  (cell = folder above `schematic/`, view = file stem). For a **loose** schematic not under a
  `…/<Cell>/schematic/` layout, `<schematicKey>` = the file stem.
- **Collision handling = detect-and-warn (Option A):** if a `results/<schematicKey>/` directory is
  already owned by a *different* cell path (two same-named cells from different libraries), do **not**
  silently rename/suffix — surface a `Message` telling the user to rename one cell. Keeps names pristine
  and stable so saved `.cdd` references never break.
- **Scratch / no-workspace:** no workspace `results/` exists → write to the per-session recovery working
  dir: `<recovery-session>/results/<TabTitle>/run.npy`, discarded on clean exit. On materialization, later
  runs write to the real workspace `results/`.
**Deliverables:** on a successful run, assemble the run's analyses into one grouped DataSet and
write/overwrite `results/<schematicKey>.npy` via `DataSetExporter`, deleting only that one file first
(never a wildcard clear of the shared `results/` directory, R-res-0). Confirm the `export → import`
round-trip holds (flat and grouped) for every analysis type.
**Gate:** run a schematic with ≥1 analysis → `results/<schematicKey>.npy` appears/updates;
`DataSetImporter` reconstructs an equivalent grouped DataSet; running one schematic never touches any
other schematic's results file or a user-named baseline sitting in the same directory.
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

**Design (RESOLVED — dual-source + per-port reference impedance).**
- **Dual-source `Trace` (owner-approved).** A `Trace` is backed by either (a) an **SNP** (Touchstone — the
  existing full S-parameter machinery: S→Y/Z, Z0 renorm, stability circles, Mu/MuPrime/MaxGain, marker
  impedance) **or** (b) a **DataSet cube** bound as `(source, cube, slice, transform)`. The data-source
  library (§2.2) decides which per file. The cube path serves HB V/I spectra + measurement traces (1-D after
  slice; no S-param-specific ops — meaningless for a node voltage) **and** S-cube traces.
- **Trace identity is stored as components** `(source · analysis · cube/quantity · slice · transform)` (§2.3)
  regardless of which compute path runs. S-param-only controls (S/Y/Z, Z0, derived/stability) stay gated to the
  S-cube trace kind — aligns with §2.8 per-trace-kind card bodies.
- **Per-port reference impedance — the honest single-cube model.** A simulator may produce S referenced to
  **per-port, possibly complex** terminations (non-uniform normalization). Touchstone (v1.1) is always uniform.
  Today the `S{freq,i,j}` cube carries **no** Z0 (`DataSetBuilder.ToSnp` fabricates 50 Ω). **Decision:** store
  `S` **once** in its native normalization **plus a `Z0` complex cube** `{port}` (1-based, length = nPorts;
  per-port complex reference impedance) — the single source of truth. **Reject** shipping a second `S_50`
  cube (redundant + lossy-in-the-wrong-direction; renorm is invertible given Z0). Touchstone → a `Z0{port}`
  cube with all entries equal. *"Renormalized to uniform 50 Ω"* is a **render-time transform**
  (`RFNetwork.SToS(mat, z0Old[], 50)`), not a stored cube — it becomes a trace transform option.
- **The math is already per-port-ready (not a gap).** `RFNetwork.SToS`/`SToZ`/`SToY`/`Convert` all have
  `Complex[]` per-port overloads (general Kurokawa power-wave renorm, per-port complex source AND target);
  stability already (correctly) renormalizes to a **uniform real** reference internally before computing
  μ/circles/MaxGain. So non-uniform/complex Z0 is **only** (1) a carrier gap (the `Z0{port}` cube) and (2) a
  plumbing choice — NOT new RF math. A cube-S-trace computes Z0-dependent ops via the per-matrix per-port
  overloads directly (element renorm, S→Y/Z, marker impedance); stability = renorm-per-port→uniform-real,
  then the existing stability formulas. `SNP` stays uniform-only by design (so the uniform case can still
  reuse the SNP path; only non-uniform sources bypass it).
- **Always-on indicator + Messages warning (owner-requested safety).** Because S(i,j) IS Z0-dependent (the
  stored matrix is referenced to the port impedances), a **subtle per-trace badge** appears on **any**
  scattering (S-type) trace whose source `Z0{port}` is **non-uniform across ports OR complex** (uniform-complex
  counts — it is also a "this isn't 50 Ω real" footgun; owner-confirmed). The actual per-port Z0 values are
  surfaced on hover / in the inspector trace card, and the marker impedance line reflects them. A **one-time
  `Message`** ("Detected non-uniform reference impedance…") fires when such a source is loaded/plotted. Goal:
  prevent the VendorA-style failure mode where a user forgets the reference and mis-reads Z0-dependent results.
- **Sequencing.** The `Z0{port}` carrier + the indicator + the Messages warning go in **from the start** (cheap;
  makes the data honest and protects the user immediately). The uniform case keeps working through the existing
  SNP path. Wiring the cube-S-trace to compute Z0-dependent ops via the per-port `RFNetwork` overloads (full
  non-uniform correctness) is an **additive follow-on** so it doesn't bloat the core source-library/picker work.
- **Producer status (VERIFIED — live latent bug, single-site fix).** `SParameterEngine.Run` **already computes
  per-port complex Z0 correctly** — each Term/Port carries its own (complex-capable) `Z`, collected into
  `z0PerPort`, and the S-matrix is built against it via `RFNetwork.YToS(yMat, z0PerPort)`. **But `Run` then
  discards it:** it collapses the reference to **port 1's** Z0 (`refZ0 = z0PerPort[0]`), stuffs it into a uniform
  `SNP`, and `FromSnp` stores no Z0 cube. So a user who sets non-uniform/complex Term `Z` **today** gets correct
  S values but a silently mis-recorded reference (port-1 Z0, then 50 Ω after any round-trip) — the exact footgun
  the indicator targets, currently *created* by circuitRF. **Fix is small + single-site:** `SParameterEngine.Run`
  emits a `Z0{port}` cube from the `z0PerPort` it already holds (build the DataSet directly, or via a new
  `DataSetBuilder` overload that writes S + Z0). The non-uniform path is **testable now** (set per-port Term `Z`),
  so the indicator + warning have a live trigger. **Fold this producer fix into the 7.2 carrier brief** — it is the
  same `Z0{port}` cube and also fixes a real correctness bug. (S-param is not swept via `ParametricSweepEngine`
  today — single producer; if added later, `StackSweepAxis` makes it `Z0{sweep,port}` cleanly.) The consumer reads
  `Z0{port}` when present and degrades to uniform (single value, else 50 Ω) when absent (e.g. Touchstone via
  `FromSnp`, which writes a uniform `Z0` cube).
- **Lockstep.** The `Z0{port}` cube is a **flagged DataSet-API addition** (splotRF must read it too) — recorded
  in `src/Core/Data/CLAUDE.md` → "Change carefully". On-disk `.npy` round-trips it for free (alpha = break
  format freely; upgrade exporter+importer+splotRF together).
- **`Z0` under parametric sweep — two distinct cases (do not conflate).** `StackSweepAxis` prepends the sweep
  axis to **every** cube, so a swept S-param run yields `S{…sweep…,freq,i,j}` **and** `Z0{…sweep…,port}`.
  (1) *Z0 as a consequence of an unrelated sweep* (e.g. 2 bias points — Z0 didn't actually change): handled by
  the **carrier**, not a dialog — the dual-source S-trace pins its sweep indices to locate its `S` slice and
  applies the **same pins** to `Z0` to recover the `{port}` vector for `ClassifyZ0`/renorm/indicator. We keep
  the generic stack (**option A: stack then slice-with-pins**) rather than special-casing `Z0` out, because Z0
  *can* legitimately vary per point (a swept Term `Z`); the redundancy is trivial. `Z0`'s canonical shape is
  `{port}`; consumers locate the `port` axis **by name** (not position) and operate on a single sweep point.
  (2) *Z0 as an intentional sweep variable* (sweep a port `Z`, plot `S11 vs Z0`): Z0 becomes an X-axis or a
  **family** — handled generically by **7.3 axis-role assignment** (Z0 is just another named axis by then), no
  special work. (Shape contract lands in the 7.2a carrier brief; no swept-S test until an S-param sweep
  producer exists — see below.)
- **Brief plan (multiple small briefs):** (a) data-source library `file→DataSet` (+ `Z0{port}` carrier read +
  uniform-vs-non-uniform detection); (b) `Trace` dual-source + identity components; (c) trace picker for ≤2-D
  cubes; (d) minimal-label policy §2.7; (e) indicator + Messages warning; (f) follow-on: per-port Z0-dependent
  compute on cube-S-traces.

### 7.3 — Multi-dimensional sweep trace dialog  ✅ COMPLETE
**Goal:** author traces from cubes with >2 axes via **axis-role assignment**, matching the Analyses-
Properties UX style.

> **COMPLETE.** Axis-role assignment (X / pinned / family) ships as the one generic mechanism; a family
> is ONE trace object rendering N curves (roles, the `~` shorthand, auto-recognition, rendering) per
> `family-curves.md`. The `~`/`:`/index slice grammar, `CubeTraceSpecParser`, `SliceTokenParser`, and the
> All/`a..b` range tokens are in place (`brief-trace-sweep-conformance`); the harmonic stem-plot X-axis
> case is wired. Family guardrail = `Trace.MaxFamilyCurves` (101) with a clamp + one Message past it.
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
**Motivating cases (axis-role assignment is the one generic mechanism for all of these):**
- **DC curve tracer (coming):** sweep Vgs × Vds → a FET `Id` family — Vds = X, Vgs = family (one trace, N
  curves). Classic two-axis sweep; the canonical reason families must be first-class.
- **Swept S-param `Z0` (case 2 from §7.2):** an intentionally-swept port `Z` is just another named axis →
  assign it X or family like any other. No `Z0`-specific UI.
- **`Z0` as a sweep *consequence* (case 1 from §7.2) is NOT a 7.3 concern** — it's resolved by the carrier's
  slice-with-pins; the user never assigns `Z0` a role in that case.
**Producer note (forward-looking, not 7.3 UI work):** `ParametricSweepEngine.RunInner` currently dispatches
only HB + nested sweeps. **DC and S-param need inner-analysis dispatch added** to be sweepable (DC for the
curve-tracer family; S-param for swept-Z0 / bias-swept S). That engine work is separate from the 7.3 dialog and
gated when those sweeps are actually wired.

### 7.4 — Loadpull contours + surface modeling (Γ-plane + Z-plane)  → DETAILED IN `loadpull-contours.md`
**Goal:** first-class contour plotting with full user control, form *"Metric at constant value of a
different metric"*, on a smooth 2-D surface model of the scattered loadpull field (which also synthesizes
power drive-ups at load points **off** the data grid).

> **DESIGN UNBLOCKED — full design in `docs/design/loadpull-contours.md`.** The reference materials are in
> hand (Hart ARFTG-2006 paper + `SPLData.py` + `.spl`/`.lpcwave` test data, in `loadpull-contours-refs/`).
> The method is RBF (multiquadric) 2-D interpolation over scattered Γ, with per-grid-point compression
> preprocessing and a stack-of-surfaces for off-grid drive-up synthesis. Contour **iso-line tracing is ours
> to build** (marching squares; not in the Python, which used matplotlib). See the note for the algorithm,
> the scipy.Rbf scope, and the sub-gate detail.

**Locked decisions (see `loadpull-contours.md` §1):**
- **Surface storage = derived; cube stays honest (Option A).** The cube stores only the measured/simulated
  field; a first-class headless **`LoadpullSurface`** model owns the RBF fit + iso-extraction + off-grid
  synthesis, cached **in-memory** keyed by all fit params. No surface serialization (no format to version).
  A disposable cross-session sidecar cache is a **documented future option**, not built in 7.4.
- **Ingest first.** `.spl`/`.lpcwave` readers come **before** the contour engine — they normalize measured
  loadpull into the **same** DataSet shape the loadpull engine emits, so the Data Display treats measured and
  simulated identically, and 7.4a–c validate against real data.
- **Custom allocation-free dense LDLᵀ/Cholesky solver** for the RBF (dense kernel; sparse solvers are the
  wrong tool). Fast at both N≈20 (zero ceremony) and N≈200 (cache-resident); performance is a benchmarked
  gate. Math is framework-free in RfCore (firewall).

**Sub-gates (detail in `loadpull-contours.md` §3):**
- **7.4f — `.spl`/`.lpcwave` ingest** (FIRST): readers → loadpull DataSet shape; canonical FOM-name
  normalization; wire into the data-source library.
- **7.4a — RBF + interp1d math core** (RfCore): multiquadric `RfBfRbf2D` matching scipy numerically +
  allocation-free LDLᵀ solve; correctness + performance gates.
- **7.4b — `LoadpullSurface` model**: compression preprocessing, metric-@-constant-other-metric surfaces,
  MXP/MXE auto-view-box, lazy cache.
- **7.4c — off-grid power-sweep synthesis** (the surface stack): held-out grid-point gate.
- **7.4d — contour iso-line renderer** (Skia + headless marching squares): Γ-disk / Z-plane clip + labels.
- **7.4e — contour trace card** (inspector extension, §2.8) + `.s1p` overlay.

**Gate (overall):** reproduce a known contour set from a loadpull run (owner-verifiable against the Python
reference); off-grid driveup synthesis matches the reference within tolerance.
**Action:** start 7.4f — request 1–2 `.spl`/`.lpcwave` test files copied into `circuitRF/testdata/` for the
reader's regression tests.

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

- **7.0:** RESOLVED, then **SUPERSEDED** by the grouped-dataset layout (`results-dataset-layout.md`),
  then **FLATTENED** (brief-results-storage-and-data-display.md): one grouped **`results/<schematicKey>.npy`**
  per run (group per analysis + `measurements` group, `Analysis.Cube` addressing), flat in the shared
  `results/` directory — not the per-schematic-directory `results/<schematicKey>/run.npy` that had
  shipped. `<schematicKey>` derivation and scratch → recovery-session results dir are unchanged; the
  detect-and-warn collision check (Option A) is dropped, not rehomed (R-res-0a).
- **7.1:** RESOLVED — re-sliced into 7.1a–7.1e (§3). **Port strategy = P1 (faithful-first):** port the
  coupled engine splotRF-styled (7.1c, sliced VM→controls→views), then restyle the inspector + add markers
  (7.1d). Inspector surface = dual (per-plot fly-out + Properties dock, §2.8); per-trace-kind card bodies;
  Material.Icons.Avalonia reused. Remaining: `.cdd` registration in `project-file-formats.md` / `.cws`
  (7.1e detail).
- **7.2:** RESOLVED — **dual-source `Trace`** (SNP-backed Touchstone keeps full S-param machinery; DataSet-cube
  binding `(source,cube,slice,transform)` for HB/measurements + S-cubes). Per-port (possibly complex) reference
  impedance carried as a **`Z0{port}` complex cube** (single honest `S` + `Z0`; reject `S_50`/`S`); 50 Ω renorm
  is a render-time transform. `RFNetwork` per-port overloads already exist — no new RF math. Always-on indicator
  on scattering traces when Z0 is **non-uniform OR complex** + one-time Messages warning. Carrier+indicator+warning
  ship first; full non-uniform Z0-dependent compute is an additive follow-on. `Z0{port}` is a flagged DataSet-API
  addition (`src/Core/Data/CLAUDE.md`). Producer VERIFIED: `SParameterEngine.Run` already computes per-port Z0
  (`YToS(yMat, z0PerPort)`) but discards it (`refZ0 = z0PerPort[0]` → uniform SNP) — a live latent mis-reference
  bug; single-site fix folded into the 7.2 carrier brief; testable now. See §7.2 "Design (RESOLVED)".
- **7.3:** RESOLVED — family-sweep guardrail = `Trace.MaxFamilyCurves` (101): clamp + one Message past the cap.
- **7.4:** RESOLVED into a full design note (`docs/design/loadpull-contours.md`). Surface storage = derived
  `LoadpullSurface` (cube stays honest), in-memory cache, sidecar deferred. Ingest (`.spl`/`.lpcwave`) first.
  Custom allocation-free dense LDLᵀ RBF solver. Sub-gates 7.4a–f defined. Remaining open items are
  per-sub-gate (FOM-name set, scipy tolerance, level-set UX, stack defaults) — tracked in that note §5.

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
- Sub-phase 7.4 (contours): **design complete** — see `docs/design/loadpull-contours.md`. Materials in hand;
  RBF surface-model method understood; sub-gates 7.4a–f defined (ingest 7.4f runs first). Ready to brief.
- **Grouped-dataset layout — COMPLETE (supersedes 7.0's per-analysis file).** Results now write one
  grouped `results/<schematicKey>.npy` per run — flat in the shared `results/` directory
  (brief-results-storage-and-data-display.md flattened the per-schematic-directory form that had
  actually shipped) — group per analysis + a `measurements` group, addressed `Analysis.Cube`; `DataSet`
  is grouped (default group preserves bare access for Touchstone); the trace picker, `TraceExpression`,
  and `CubeTraceSpecParser` resolve qualified names. Measurements evaluate once into the `measurements`
  group (no per-analysis attachment). A schematic-level "Results file" override (R-res-2) lets a run be
  named to preserve it as a baseline instead of the default `<schematicKey>.npy`, which overwrites every
  run. A `.cdd` now holds several aliased `.npy` sources (R-res-4) instead of one; missing sources report
  by name and never lose trace configuration (R-res-5); running an analysis auto-creates and opens a
  non-empty default `.cdd` when none exists (R-res-8/9/10). Spec: `results-dataset-layout.md`.

### Table with multiple X axes (trace-sweep-conformance, 2026-06-17)

The `Table` plot type supports multiple, distinct X axes — one per trace group. Implementation in
`TableRenderer.BuildColumns` (Part 3 of brief-trace-sweep-conformance):

**Column plan:** `TableRenderer.BuildColumns(plot)` returns `List<TableColumn>` — an explicit ordered
column plan. Two kinds of column: `XAxis` (the independent variable for a group of adjacent traces)
and `TraceValue` (one Y column per trace). For each trace an `XAxis` column is emitted immediately to
its left, UNLESS the immediately preceding trace has exactly the same X identity.

**Adjacent-dedup rule:** two adjacent traces share one `XAxis` column when all three of the following
hold:
1. Same axis **name** (`CubeXAxisName` for cube-bound, `"Freq"` for network traces).
2. Same axis **unit** (`CubeXUnit` for cube-bound, `"Hz"` for network traces).
3. Same **sorted values and count** (exact `==` on doubles — they come from the same underlying axis
   array, so toleranced comparison is wrong here).

A non-matching trace between two matching groups breaks the adjacency run; traces A and C sharing X
but separated by a different-X trace B each get their own `XAxis` column.

**Row count:** each `XAxis` column's `XValues` array sets the row count for its group. The table's
total row count = max across all groups. A trace shorter than the tallest group shows blank (`""`)
cells past its last row.

**Sort:** the ascending/descending sort triangle is drawn on **every** `XAxis` column header.
Clicking any X header flips `plot.TableViewAscendingSortOrder` for the entire table — all X groups
re-sort together (the toggle is plot-wide, not per-column). `BuildColumns` applies the current sort
order to each group's `XValues` at plan-build time.

**Copy Table Data:** `TableRenderer.BuildCopyGrid` is the single source of truth for clipboard copy
and matches the rendered column plan exactly (WYSIWYG: same headers, same column order, blank cells
where a group is shorter than the table's row count).

**TraceValue column widths** come from `trace.ColumnWidth`; **XAxis column widths** come from
`plot.ColumnWidth`. Both are resizable by dragging the column header right edge; double-tapping the
resize handle auto-fits the column.
- **Phase 7.1d-2 — COMPLETE.** Properties-dock dual surface for the Data Display inspector. Build 0W/0E; 1206 tests pass.
- **Phase 7.1d-3 — COMPLETE.** Marker editor restyled to the inspector idiom + stale-marker guard in `MarkerEditorViewModel`.
- **Renderer glyph fix — COMPLETE (pre-7.2 cleanup).** IBM Plex missing-glyph fallback: table sort arrow drawn as an `SKPath`; per-glyph DejaVu fallback for `∠` (and any Plex-missing glyph) in `TableRenderer`/`MarkerRenderer`.
- **Phase 7.2a — COMPLETE.** `Z0{port}` carrier + producer fix: `DataSetBuilder.BuildZ0Cube`, `ClassifyZ0`, `Z0Kind` enum in `RfCore.Data`; `FromSnp` emits a uniform `Z0` cube on every S DataSet; `ToSnp` reads the cube (non-uniform → port-1 value + `RFNetwork.Warn`; absent → 50 Ω); `SParameterEngine.Run` overwrites the placeholder with the true per-port complex values. Build 0W/0E; full suite passes.
- **Phase 7.2b — COMPLETE.** Data-source library generalised to load `.npy` alongside Touchstone. `SnpEntryViewModel` adds `SourceKind {Touchstone, Npy}`, nullable `SNP? Snp`, `DataSet? Data`, `string? FilePath` (single path authority). `IsBroken => _snp?.IsEmpty ?? false` — null Snp (cube-only .npy) is NOT broken. Command properties use `{ get; private set; } = null!` so `InitCommands` can assign them. `SnpLibraryViewModel` routes `LoadFileAsync` by extension: Touchstone → existing path; `.npy` → `DataSetImporter.Import` → new `.npy` entry ctor; broken-entry restore + `ReloadAsync` branch on `SourceKind`. `AddBrokenEntry` routes by extension. `.npy`-with-S: `DataSetBuilder.ToSnp(data)` exposes an `SNP` for the existing picker (the S-param gate). `.npy`-without-S: `Snp = null` (cube-only, not pickable until 7.2c). File-picker filter in `DataDisplayView.axaml.cs` updated to "Data Files" (Touchstone + .npy). All `e.Snp.FilePath` → `e.FilePath`; all `e.Snp.IsEmpty` guards updated with null checks. `SnpLibraryView.axaml.cs` drop handler uses `e.IsBroken` + `e.FilePath`. In-place refresh invariant: `RefreshNpy` calls `_snp.RefreshFrom(ToSnp(data))` when live (preserves trace bindings); replaces reference only when broken/null. Naming debt flagged in comments; rename to `DataSource*` deferred to 7.2c. Build 0W/0E; 1211 tests pass. S-param gate met (`.npy`-with-S pickable + plottable via existing SNP machinery). **Next:** Phase 7.2c — cube-native trace path for non-S cubes + identity components + minimal labels + class rename.
- **Phase 7.2c — COMPLETE.** Cube-native trace path for non-S cubes landed: `Trace` is dual-source (SNP-backed Touchstone keeps full S-param machinery; DataSet-cube binding `(source, cube, slice, transform)` for HB V/I spectra, measurements, and S-cubes). Trace identity stored as separate components (`source · analysis/group · cube · slice · transform`), never a pre-joined string. Qualified `Group.Cube` addressing resolves through the picker, `TraceExpression`, and `CubeTraceSpecParser`. Minimal-label policy (§2.7) computes the shortest disambiguating label at plot/legend level. Non-uniform/complex `Z0` indicator + one-time Messages warning wired. Data-source library classes generalised (the `Snp*`→`DataSource*` naming debt from 7.2b resolved). Build 0W/0E.
- **Phase 7.3 — COMPLETE.** Multi-dimensional sweep via axis-role assignment (X / pinned / family); a family is ONE trace object rendering N curves (`family-curves.md`). Slice grammar (`~`/`:`/index + All/`a..b` ranges), `CubeTraceSpecParser`, `SliceTokenParser`, harmonic stem-plot X-axis case all in place; family guardrail = `Trace.MaxFamilyCurves` (101) with clamp + one Message past it.

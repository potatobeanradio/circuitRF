# circuitRF Trace Markers — Design

**Location:** `/circuitRF/docs/design/trace-markers-design.md`
**Status:** Draft (rev 2 — design decisions incorporated)
**Last updated:** 2026‑06‑21
**Scope:** Defines the behavior, types, rendering, and interaction model for trace markers in circuitRF. This document is the reference of record for marker behavior and is intended to seed the end‑user documentation.

---

## 1. Overview

A **Marker** is an interactive annotation pinned to a trace. It reports the value of the trace at a chosen location through an associated **MarkerInfoBox**, and (where applicable) renders a visible **glyph** at its position on the plot.

circuitRF originally inherited a single marker concept from the legacy *splotRF* S‑parameter plotting tool: a triangle glyph with click/drag behavior plus a `MarkerInfoBoxView`. circuitRF now renders several distinct trace types (polylines, spectra, stability circles, tables, contours), and a single marker definition no longer covers them. This document redefines markers as a family of five types that share a common model but differ in glyph, roaming behavior, the domain they live in, and what their InfoBox reports.

### What already exists (reused, not rebuilt)

- The triangle **marker glyph** and its core interactive (click/drag) behavior.
- The **`MarkerInfoBoxView`** and the information it presents.
- The **Marker Properties editor**.
- The marker **ContextMenu**.
- The **VSWR locus formula** (the equation that computes the locus of points enclosing a marker's impedance / reflection coefficient at a given VSWR). This is reused as‑is; it is not re‑derived here.

### What this design adds or changes

- A formal taxonomy of five marker types and the rules that distinguish them.
- New marker support for spectra (stems), tables, and contour traces.
- Interactive **VSWR circles** around markers that live in a Z or Γ domain.
- New marker properties: a **VSWR enable checkbox** and a **VSWR value field** in the Marker Properties editor, and a **`ShowInfoBox`** flag on the marker.
- A **"Snap to Point"** item on the marker ContextMenu for contour markers.

---

## 2. Terminology

| Term | Meaning |
|---|---|
| **Marker** | An interactive annotation attached to a single trace at a single location. |
| **MarkerInfoBox** | The readout panel associated with a marker; reports the trace value(s) at the marker. Implemented by `MarkerInfoBoxView`. |
| **Glyph** | The visible on‑plot symbol that shows where the marker sits (e.g., the triangle). Not all marker types render a glyph. |
| **Free‑roaming** | The marker may be positioned anywhere within the plot area; its value is read or interpolated at that location. |
| **Snapped** | The marker is constrained to discrete host geometry (a data sample, a grid point, or a specific circle) and cannot be placed between those positions. |
| **Z domain** | The complex impedance plane. |
| **Γ domain (Gamma)** | The complex reflection‑coefficient plane (Smith / polar). |
| **VSWR circle** | A locus drawn around a marker representing all terminations at a constant VSWR relative to the marker's termination. Generally *not* a literal circle (see §6). |
| **FOM** | Figure of merit. VSWR is used here as an FOM for how well two complex impedances / reflection coefficients are matched. |
| **DataDisplay** | The surface/panel that hosts plots and the selectable list of InfoBoxes. |

---

## 3. Common marker model

All five marker types share the following:

- Every marker has exactly one associated **MarkerInfoBox**.
- Every marker is bound to exactly one **host trace**. The host trace's rendering type determines the marker type.
- A marker exposes a **`ShowInfoBox`** flag (default **`true`**). See §7.
- A marker is edited through the **Marker Properties editor** (§8) and acted on through its **ContextMenu**.
- A marker has a **position** expressed in the natural coordinates of its host (e.g., an X value for a polyline, a complex Z/Γ for a Smith/contour plot, a row/column for a table).
- Where the host lives in a Z or Γ domain, a marker may additionally own a **VSWR circle** (§6).

Properties differ by type along these axes: **glyph**, **roaming behavior** (free vs. snapped), **domain**, **VSWR‑circle support**, and **InfoBox contents**.

---

## 4. Marker type summary

| # | Marker type | Glyph | Roaming | Domain | VSWR circle | Implementation status |
|---|---|---|---|---|---|---|
| 1 | X,Y polyline (incl. complex point on Smith/polar) | Triangle | Along trace, snaps to samples | X/Y *or* Z/Γ | Only if host is Z/Γ | Implemented; **may be buggy** |
| 2 | Spectrum (discrete stems, X = harmonic order) | Triangle | Snapped to stems | X/Y (harmonic) | No | **Not implemented** |
| 3 | Stability circle (on Smith chart) | Triangle | Snapped to nearest stability circle (may switch circles) | Γ | **Yes** | Implemented for SNP; **untested for S‑param data cubes** |
| 4 | Table cell | None (cell highlight) | Snapped to cells | n/a | No | Implemented, **may be buggy**; see §5.4 |
| 5 | Contour trace | Mode 1: ringed circle · Mode 2: triangle | Mode 1: free · Mode 2: snapped to grid | Z/Γ | **Yes** | **Never implemented** |

> The "free vs. snapped" rule alone does not determine VSWR support. VSWR support is determined by whether the marker has a well‑defined Z or Γ value (see §6.1). That is why a *snapped* stability‑circle marker (type 3) still supports VSWR, while a *free* polyline marker on a non‑complex axis (type 1 in magnitude‑vs‑frequency mode) does not.

---

## 5. Marker types in detail

### 5.1 Type 1 — Marker on an X,Y polyline

The classic marker. The user plots Y versus X; the marker rides along the polyline and the InfoBox reports the (X, Y) value at the marker. This type also covers a complex point plotted on a **Smith** or **polar** chart, in which case the marker's value is a complex number (Z or Γ).

- **Glyph:** triangle.
- **Roaming:** moves along the trace and **snaps to sampled data points** — the marker always sits on an actual data sample and does not take interpolated positions between samples.
- **Domain:** the host plot's domain. If the host is a Cartesian Y‑vs‑X plot (e.g., magnitude in dB vs. frequency), there is no Z/Γ value and **no VSWR circle**. If the host is a Smith/polar plot, the marker carries a Z/Γ value and **VSWR is available**.
- **InfoBox:** the (X, Y) pair, or the complex value plus its derived forms (magnitude/phase, Z/Γ, etc.) consistent with the existing `MarkerInfoBoxView`.
- **Status:** already present in circuitRF but reported as possibly buggy. Correctness will be addressed through iterative debugging (see §10, D8); treat this as a debugging effort rather than greenfield work.

### 5.2 Type 2 — Marker on a spectrum (discrete stems)

For traces drawn as discrete stems where the **X‑axis is harmonic order** (e.g., harmonic content of a signal).

- **Glyph:** triangle.
- **Roaming:** **snapped** to stems. Because the X‑axis is a discrete harmonic index, the marker cannot sit between stems; it moves stem‑to‑stem (harmonic to harmonic).
- **Domain:** Cartesian (harmonic order vs. amplitude). **No VSWR circle.**
- **InfoBox:** the harmonic order and the stem's value (e.g., amplitude/power, and any derived quantities the existing InfoBox supports).
- **Status:** not yet implemented in circuitRF.

### 5.3 Type 3 — Marker on a stability circle (Smith chart)

A marker pinned to a **stability circle** rendered on a Smith chart. This is **not** a free‑roaming marker: it must be **snapped to a stability circle** and reports stability information **at the frequency corresponding to that circle**.

- **Glyph:** triangle.
- **Roaming:** the marker moves to the **nearest point on any displayed stability circle**. It does not care whether this switches it from one circle to another — if the nearest point belongs to a different circle, the marker hops to that circle. The reported frequency is always that of whichever circle the marker currently sits on.
- **Domain:** Γ (Smith chart).
- **VSWR circle:** **supported** — the marker has a Γ value, so a VSWR circle around it is meaningful.
- **InfoBox:** uses the **existing MarkerInfoBox already implemented for SNP datasets**. No new fields are defined here; the SNP stability readout is reused as‑is.
- **Status:** implemented for SNP traces. Stability circles have **not** been implemented for S‑parameter **data cubes**, so this type is currently untested against data‑cube sources and should be re‑validated once stability‑circle support for data cubes lands.

### 5.4 Type 4 — Marker on a table

A marker attached to a **Table** trace.

- **Glyph:** **none.** Instead, the corresponding **table cell is highlighted** to indicate the marker location.
- **Roaming:** snapped to table cells.
- **Domain:** not applicable.
- **VSWR circle:** **not supported** (tables are explicitly excluded from VSWR circles, and there is no spatial Z/Γ domain in which to draw one).
- **InfoBox:** additional information about the highlighted cell beyond what the cell itself shows.
- **Status:** already implemented, **may be buggy**. Action items: verify that the **MarkerInfoBox is actually displayed for tables**. For **Performance Summary Tables** specifically — if it is straightforward to get marker / InfoBox support working, do so; if it proves difficult, **remove markers from the trace for Performance Summary Tables** rather than ship them broken.

### 5.5 Type 5 — Marker on a contour trace

A marker on a **contour** trace. Contours are a 2D scalar field over the Z or Γ plane (for example, load‑pull contours). This type has **two modes**.

**Mode 1 — Free roaming (interpolated).**
The marker may be placed anywhere in the Z or Γ plane. The InfoBox reports **only the interpolated result on the load‑pull surface at the impedance / Γ represented by the marker** — i.e., the single 2D‑interpolated surface value at the marker's location, nothing more.
- **Glyph:** a dedicated glyph — **a filled circle with a thin black line stroked around it** — to signal to the user that the reported value is a 2D **interpolant** (not a measured/grid value).
- **VSWR circle:** supported.

**Mode 2 — Snapped (grid).**
The marker is **snapped to the grid point** from which the contour was generated, so the InfoBox reports the exact grid value rather than an interpolant.
- **Glyph:** the regular **triangle** glyph (same as types 1–3).
- **VSWR circle:** supported.

- **Domain:** Z or Γ plane.
- **Mode switching:** the user changes a contour marker's mode in **two** equivalent ways:
  1. Through the **Marker Property inspector**.
  2. Through the marker **ContextMenu**, which gains a **"Snap to Point"** item rendered **with a checkbox icon** (checked = Mode 2 snapped, unchecked = Mode 1 free). The marker ContextMenu already exists in the repo; this adds one item to it.
- **Status:** never implemented; this is new work.

The Mode 1 vs. Mode 2 glyph difference is intentional and is the primary visual cue the user has for whether a reading is interpolated or exact.

---

## 6. VSWR circles

For markers that carry a Z or Γ value, the marker may draw a **VSWR circle** around its impedance / reflection coefficient. The purpose is to let the user "measure" the distance between two complex terminations: VSWR is a practical FOM for how well two impedances / reflection coefficients are RF‑matched, so a VSWR circle around a marker shows the locus of all terminations that are matched to the marker's termination to within a chosen VSWR.

The VSWR locus is computed with the **existing VSWR formula**; this design does not re‑derive it. Reuse the implementation that already produces the locus of points enclosing a marker's impedance / reflection termination at a given VSWR.

### 6.1 When a VSWR circle is available

A VSWR circle is available whenever the marker has a well‑defined **Z or Γ** value:

- Type 1 **only when** plotted on a Smith/polar chart (complex value).
- Type 3 (stability‑circle marker) — yes, even though it is snapped.
- Type 5 (contour) — yes, in both modes.

It is **not** available for:

- Type 1 on a Cartesian Y‑vs‑X plot (no complex value).
- Type 2 (spectrum / harmonic order).
- Type 4 (table) — explicitly excluded.

> This refines the original "free‑roaming except Table" wording. The real gate is domain (does the marker have a Z/Γ value?), not roaming mode — which is why the snapped stability‑circle marker still gets a VSWR circle.

### 6.2 Geometry

The VSWR "circle" is a **locus of points** and is **not necessarily a literal circle**. Its shape depends on the domain (Z vs. Γ), and the marker is **not necessarily at the geometric center** of the locus, because of how the constant‑VSWR locus maps into the displayed domain. The renderer must draw whatever shape the locus formula produces; it must not assume a centered circle. VSWR circles are supported in **both** the Z‑plane domain and the Γ domain.

### 6.3 Rendering

- **Stroke:** red.
- **Fill:** none.
- The circle is anchored to the marker's value and **redraws live** whenever the marker moves, reflecting the marker's new Z/Γ.

### 6.4 Interaction

- The VSWR circle is **interactive and independently draggable**. The user can click‑drag the circle to make it larger or smaller, which **changes the VSWR value**.
- During a drag, the **VSWR value is computed from the marker's impedance/Γ and the impedance/Γ at the mouse‑pointer location**: the pointer defines a second termination, and the VSWR between the marker's termination and the pointer's termination sets the circle. The circle is drawn at that VSWR and the readout reflects it.
- Dragging the VSWR circle **does not move the marker**: the marker's value stays fixed while the user changes the VSWR value. (Conversely, moving the marker moves the circle's anchor but keeps the VSWR value.)
- While the user is dragging the circle, a **live VSWR readout** text appears near the mouse pointer and updates continuously as the circle is resized.
  - The text **follows the pointer** but is always placed on the side **away from** the red VSWR locus, so it never sits on top of the stroke.
  - On **mouse release**, the readout text disappears.

### 6.5 Default and value range

- Default VSWR value is **2** (a 2:1 VSWR circle is drawn by default when VSWR is enabled).
- **Any numeric VSWR value is permitted, including negative values.** The value is **not** clamped to the physical ≥ 1 range; the locus formula is applied to whatever value the marker‑vs‑pointer calculation (or the property field) yields.

---

## 7. MarkerInfoBox and the `ShowInfoBox` flag

Every marker has a MarkerInfoBox (`MarkerInfoBoxView`) that reports the trace value(s) at the marker; the precise contents vary by type as described in §5.

New flag: **`ShowInfoBox`** (boolean, default **`true`**).

- When `true` (default): the MarkerInfoBox renders and is selectable in the DataDisplay, as today.
- When `false`: **no MarkerInfoBox is rendered**, and the MarkerInfoBox is **not selectable** in the DataDisplay.

This lets a user keep a marker (and its glyph / VSWR circle) on a plot without the InfoBox occupying space or appearing in the DataDisplay's selectable list. The marker itself, its glyph, and any VSWR circle are unaffected by this flag.

---

## 8. Marker Properties editor

The Marker Properties editor already exists. This design adds the following UI:

- **VSWR enable checkbox** — toggles the VSWR circle display for the marker on/off. Should be disabled (greyed out) for marker types/contexts where VSWR is not applicable per §6.1.
- **VSWR value field** — a text field for the VSWR value (default 2).
  - **Any numeric value is accepted, including negative values; the value is not clamped to ≥ 1** (see §6.5).
  - Must **fail gracefully on invalid input**: non‑numeric or empty input must not crash or corrupt state — revert to the last valid value or reject the edit with a clear indication.
- **Contour mode toggle** — for contour (type 5) markers, the inspector exposes the Mode 1 (free / interpolated) vs. Mode 2 (snapped / grid) selection. This mirrors the **"Snap to Point"** ContextMenu item (§5.5); the two stay in sync.
- **`ShowInfoBox` toggle** — surfaced here if not already present.

---

## 9. Glyph reference

| Context | Glyph |
|---|---|
| Polyline (type 1) | Triangle |
| Spectrum stems (type 2) | Triangle |
| Stability circle (type 3) | Triangle |
| Table (type 4) | None — cell highlight |
| Contour, Mode 1 (free / interpolated) (type 5) | Filled circle with thin black stroked ring |
| Contour, Mode 2 (snapped / grid) (type 5) | Triangle |

The contour Mode 1 ringed‑circle glyph is the only non‑triangle, non‑none glyph, and exists specifically to flag that the reading is interpolated.

---

## 10. Design decisions (resolved)

The following were open questions during drafting and are now decided. They are recorded here for traceability; the body above reflects them.

- **D1 — Type 1 sampling:** Polyline markers **snap to sampled data points**; they do not interpolate between samples.
- **D2 — Type 3 movement:** A stability‑circle marker moves to the **nearest point on any displayed stability circle** and may switch circles freely; it reports the frequency of whichever circle it currently sits on.
- **D3 — Type 3 InfoBox:** Reuse the **existing MarkerInfoBox already implemented for SNP datasets**; no new fields are defined.
- **D4 — Type 4 (tables):** Already implemented (may be buggy). Verify the MarkerInfoBox displays for tables. For **Performance Summary Tables**, enable marker/InfoBox support if straightforward; otherwise **remove markers** from those traces.
- **D5 — Type 5 Mode 1 InfoBox:** Report **only** the interpolated load‑pull‑surface value at the impedance/Γ represented by the marker.
- **D6 — Type 5 mode switch:** Change mode via the **Marker Property inspector**, and via a new **"Snap to Point"** item (checkbox icon) added to the existing marker **ContextMenu**.
- **D7 — VSWR value:** **Any value is allowed, including negative**, with no clamping. During a VSWR‑circle drag, the value is computed from the marker's Z/Γ and the Z/Γ under the mouse pointer.
- **D8 — Type 1 bugs:** Addressed through **iterative debugging** (multiple rounds expected) rather than an upfront bug catalogue.

---

## 11. Implementation notes / status snapshot

- **Reuse:** triangle glyph + interaction, `MarkerInfoBoxView`, Marker Properties editor, marker ContextMenu, and the VSWR locus formula.
- **Type 1:** present; entering iterative debugging (D8). Markers snap to samples (D1).
- **Type 2:** to build (snapped‑to‑stem marker, triangle glyph).
- **Type 3:** present for SNP, reusing the SNP MarkerInfoBox (D3); snaps to the nearest point on any circle (D2); re‑validate once stability circles exist for S‑param **data cubes**.
- **Type 4:** present, may be buggy; verify InfoBox display; for Performance Summary Tables, enable if easy, else remove markers (D4).
- **Type 5:** new — two modes; new ringed‑circle glyph for Mode 1; Mode 1 reports only the interpolated load‑pull value at the marker's Z/Γ (D5); mode switched via inspector and the "Snap to Point" ContextMenu item (D6).
- **VSWR circles:** new interactive overlay; gated by Z/Γ domain (§6.1); red stroke, no fill; value computed from marker‑vs‑pointer terminations during drag and unclamped (incl. negative) (D7); live readout while dragging.
- **`ShowInfoBox` flag:** new; default `true`; suppresses render and DataDisplay selectability when `false`.

---

## 12. Gated implementation plan (rev 1 — 2026‑06‑22)

This plan sequences the work into independently buildable, independently verifiable **gates**. Each gate ends in a state the repo can compile and run; later gates depend only on earlier ones. The headline ask — *markers on contour plots* — lands at Gate 2, but it depends on model + glyph foundations (Gates 0–1) and is only fully useful once VSWR (Gate 3) exists, so those are sequenced around it.

### Code reality findings (grounding for the gates)

Observed while reading `Marker.cs`, `Trace.cs`, `TraceRenderer_MarkerRenderer.cs`, `ContourData.cs`, `LoadpullSurface.cs`:

- **`Marker` is a flat value object** keyed on `Freq` (double) + `PositionStatic` (Vector2, used only by stability circles). It has **no** marker‑kind discriminator, no free‑roaming Z/Γ position, no VSWR state, no `ShowInfoBox`, and no contour‑mode field. The marker type is currently inferred from the **host trace** (`IsStabilityCircle`, `IsCubeBound`, `YAxis == Complex`, …), not stored on the marker.
- **Glyph rendering is hardcoded to the triangle** in `MarkerRenderer.DrawSymbol`. There is no glyph dispatch; the contour Mode‑1 ringed‑circle glyph has nowhere to plug in yet.
- **`Trace.GetMarkerDataLocation` returns `Vector2.Zero` for `IsCubeBound` traces**, and contour traces are cube‑bound. So a marker dropped on a contour today renders at the origin and reads NaN — contour markers are effectively unsupported end‑to‑end (matches the doc's "never implemented").
- **Mode‑1 interpolation needs an evaluator.** The surface fit (`LoadpullFit` → `Rbf2D`) lives in RfCore and can evaluate the metric at an arbitrary Γ/Z. But `ContourData` currently retains only the **resampled `Grid`** (and `FillGrid`/`Scatter`), *not* the `LoadpullFit`/`Rbf2D`. Mode‑1 ("interpolated surface value at the marker's Z/Γ") therefore needs either (a) the fit retained on `ContourData`, or (b) a bilinear interpolation over the existing `Grid`. Decision deferred to Gate 2 (see open question Q1).
- **VSWR is cross‑cutting.** It is an overlay gated purely on whether the marker has a Z/Γ value (§6.1), independent of contours, but contours require it — hence Gate 3 right after contours.
- **Selection algorithm is marked do‑not‑touch** in `DrawSymbol` ("never change this selection algorithm"). Glyph dispatch must preserve the existing selection‑highlight path verbatim.

### Gate 0 — Marker model foundation (no behavior change)

Purely additive model changes so later gates have somewhere to store state. No rendering or interaction change; existing markers behave exactly as today.

- Add to `Marker`: a **`MarkerKind`** enum field (`Polyline`, `Spectrum`, `StabilityCircle`, `Table`, `Contour`) — initially set from the host trace at creation so it is redundant‑but‑correct; later gates read it instead of re‑deriving. Add **`ShowInfoBox`** (bool, default `true`). Add **`ContourSnapped`** (bool, default `false` ⇒ Mode 1) for type 5. Add VSWR state: **`VswrEnabled`** (bool), **`VswrValue`** (double, default 2). **No new position field** — contour Mode‑1's free‑roaming Γ reuses the existing **`PositionStatic`** (`Vector2`), which is already serialized as `PositionStaticX/Y` and threaded through the copy constructor.
- Thread the new fields through the **copy constructor** and the `MarkerConfig` (de)serialization in `DataDisplayConfig.cs` (`Marker ↔ MarkerConfig` map — locate both directions). Alpha no‑back‑compat rules apply: defaulted/nullable additions only, no migration shim.
- **Verify:** build is green; load/save a plot with existing markers round‑trips unchanged; no visual change.

### Gate 1 — Glyph dispatch (rendering seam)

Introduce a glyph‑selection seam in `MarkerRenderer.DrawSymbol` without changing any current output.

- Refactor `DrawSymbol` to choose glyph by `marker.MarkerKind` (+ `ContourSnapped`): triangle for everyone today; add the **contour Mode‑1 ringed‑circle glyph** (filled circle + thin black stroked ring, per §9) as a new branch that nothing selects yet. Preserve the name‑label and the **selection‑highlight block verbatim**.
- **Verify:** build green; every existing marker still renders an identical triangle (the new branch is unreached). Optionally force‑select the new branch in a scratch test to eyeball the ringed circle, then revert.

### Gate 2 — Contour markers (the headline ask) ★

Make markers work on contour traces, both modes, InfoBox included. This is the core deliverable.

- **Hit‑test + add:** allow dropping/dragging a marker on a contour trace (today the cube‑bound guard blocks it). Contour markers are positioned by **Z/Γ** (`GammaPosition`), not `Freq`.
- **Mode 1 (free/interpolated):** marker roams anywhere in the plane; `GetMarkerDataLocation` returns the marker's Γ directly; InfoBox reports **only** the interpolated surface value at that Γ (D5). Resolve Q1 (fit vs. grid‑bilinear). Glyph = ringed circle (Gate 1).
- **Mode 2 (snapped/grid):** marker snaps to the nearest **grid point** the contour was generated from; InfoBox reports the exact grid value. Glyph = triangle.
- **Mode switch:** add **"Snap to Point"** (checkbox icon) to the marker ContextMenu (D6); wire it to `ContourSnapped`. Inspector toggle comes in Gate 5.
- **InfoBox:** extend `BuildMarkerBoxLines` with a contour branch (surface metric name + value + the Γ/Z and, for Mode 1, an "interpolated" cue).
- **Verify:** drop a marker on a load‑pull contour; Mode 1 reads a smooth interpolant and roams freely with the ringed glyph; toggling "Snap to Point" snaps to a grid point, switches to the triangle, and reads the exact grid value.

### Gate 3 — VSWR circles (interactive overlay)

The interactive constant‑VSWR locus around any Z/Γ marker (§6). Needed by contours and also lights up types 1‑on‑Smith and 3.

- **Render:** new overlay drawing the locus from the **existing VSWR formula** (reuse, do not re‑derive); red stroke, no fill; redraws live with the marker (§6.3). Locus is **not assumed circular or centered** (§6.2); works in both Z and Γ domains.
- **Gating:** available iff the marker has a Z/Γ value (§6.1) — type 1‑on‑Smith, type 3, type 5 (both modes). Disabled/absent otherwise.
- **Interaction:** independently draggable to change `VswrValue`; value computed from marker Z/Γ vs. pointer Z/Γ (D7), **unclamped incl. negative**; dragging the circle does **not** move the marker; live readout text near the pointer, placed away from the stroke, gone on release (§6.4).
- **Verify:** enable VSWR on a Smith type‑1 marker and a contour marker; default 2:1 locus draws; dragging resizes it and updates the live readout; negative values accepted; marker stays put.

### Gate 4 — Spectrum markers (type 2)

- Snapped‑to‑stem marker on harmonic‑order stem traces; triangle glyph; InfoBox reports harmonic order + stem value; **no VSWR**. Mirror the stem geometry already in `TraceRenderer`/`PlotControl`'s stem path.
- **Verify:** marker on a harmonic stem plot hops stem‑to‑stem and reads the right order/value.

### Gate 5 — Marker Properties editor + `ShowInfoBox` plumbing (§7, §8)

- Editor UI: **VSWR enable** checkbox + **VSWR value** field (graceful invalid‑input handling, unclamped), **contour mode** toggle (mirrors the ContextMenu "Snap to Point", kept in sync), **`ShowInfoBox`** toggle. Grey out VSWR controls where §6.1 says unavailable.
- `ShowInfoBox=false`: suppress the InfoBox render **and** remove it from the DataDisplay selectable list, leaving glyph + VSWR intact.
- **Verify:** every new control round‑trips to marker state and persists; `ShowInfoBox=false` hides the box but keeps glyph/VSWR and drops it from selection.

### Gate 6 — Type 1/3/4 validation + debugging (D4, D8)

- **Type 1:** iterative debugging pass (D8) — snap‑to‑sample correctness on both Cartesian and Smith/polar hosts.
- **Type 3:** re‑validate once stability circles exist for S‑param **data cubes** (currently SNP‑only).
- **Type 4:** verify the InfoBox actually displays for tables; for **Performance Summary Tables**, enable if straightforward, else **remove markers** from those traces (D4).
- **Verify:** per‑type acceptance checks above; no regressions from Gates 0–5.

### Dependency order

`Gate 0 → Gate 1 → Gate 2 (★ contours) → Gate 3 (VSWR) → {Gate 4, Gate 5} → Gate 6`. Gates 4 and 5 are independent of each other and may be done in either order or in parallel.

### Open questions for this plan

- **Q1 (Gate 2) — RESOLVED:** Mode‑1 uses the **RBF fit**, which is already built and cached in `LoadpullSurface._cache` (keyed by `FitKey`). The evaluator already exists: **`LoadpullSurface.MetricAtCoord(freqIdx, metricY, coord, constraint, plane, z0, nearest, kernel, smooth, epsilon)`** — `nearest:false` evaluates the RBF surface at an arbitrary Γ/Z (= Mode 1, interpolated), and `nearest:true` returns the nearest measured node value (= Mode 2, exact grid value). `ContourData` already carries every parameter the call needs (`MetricName`, `ContourConstraintKind`/`ConstraintValue`, `FreqIndex`, `InterpKernel`, `Smoothing`, `Epsilon`). **The one missing link:** a reference from the contour trace / `ContourData` back to its owning `LoadpullSurface` instance, so the marker code can call `MetricAtCoord`. Gate 2 must add/locate that reference (the VM that builds the contour already holds the surface — `TraceRowViewModel.RebuildContour`).
- **Q2 (Gate 0) — RESOLVED:** Markers persist via `MarkerConfig` in `DataDisplayConfig.cs` (inside `TraceConfig.Markers`). It already serializes **`PositionStaticX`/`PositionStaticY`** (the stability‑circle static world position) — contour Mode‑1's free‑roaming Γ **reuses `Marker.PositionStatic`**, so **no new position field is needed**. New scalar/bool fields (`MarkerKind`, `ShowInfoBox`, `ContourSnapped`, `VswrEnabled`, `VswrValue`) are added to `MarkerConfig` as defaulted properties; per the alpha no‑back‑compat rule, defaulted/nullable additions need no migration shim.
- **Q3 (Gate 3):** confirm the exact name/signature of the existing VSWR locus formula to reuse, and whether it already emits a polyline locus or only a radius (affects the non‑circular‑locus renderer). *Note: RfCore already has `VswrCircleZ` (Z‑plane) + `VswrBoundingBox` (Γ via Z‑plane round‑trip) as **private** helpers in `LoadpullSurface`; Gate 3 likely promotes/relocates a points‑emitting version of these into a reusable spot, since they produce the exact non‑circular Γ locus the renderer needs.*

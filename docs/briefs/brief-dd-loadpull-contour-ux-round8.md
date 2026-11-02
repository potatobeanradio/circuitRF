# Brief DD-C — Loadpull contour UX round 8: marker glyph, defaults, and a useful auto-created display

**Area:** `src/Ui/DataDisplay` (contour renderer + marker renderer + trace card) and the auto-create
path in `src/Ui/ViewModels/WorkspaceViewModel.cs`. **Do not modify anything under
`src/Ui/Harmonica`** — §1 reads harmonicaRF's marker geometry as a reference and copies the numbers;
it changes nothing there. Builds on briefs 7.4h-1..-7 (landed).

**4 items.** The big one is §4 — the auto-created Data Display after a loadpull run.

---

## Verified anchors (on disk)

1. **Data Display's contour marker** — `MarkerRenderer.DrawSymbol`
   (`Renderers/TraceRenderer_MarkerRenderer.cs:272-352`). Two glyph shapes: a ringed circle of
   radius `ts * 0.5f` when `marker.MarkerKind == Contour && !marker.ContourSnapped`, else a
   downward triangle of height `ts`. `ts = SymbolTextSize(marker, canvasSize)`. The disc is filled
   with `theme.TextColor` and the name is **always** drawn above the glyph
   (`dataPx.Y - ts - 4f`), never inside.
2. **harmonicaRF's termination marker** — `HarmonicaPanelRenderer.DrawMarkers`
   (`src/Ui/Harmonica/Renderers/HarmonicaPanelRenderer.cs:495-528`). The reference geometry to copy:
   `r = max(6f, min(W,H) * 0.020)`; label font `ts = r * 1.15f` in `SkiaFonts.PlexBold`; filled
   circle in the band colour; 1.2 px black stroke ring; name centred **inside** at
   `p.Y + ts * 0.36f` in black.
3. **`ContourData.LabelSpacing` defaults to 30.0** (`Models/ContourData.cs:104`); the card mirrors it
   in `TraceRowViewModel._contourLabelSpacing = 30.0` (`:137`). `AddContourTrace`
   (`ViewModels/PlotInspectorViewModel.cs:1143-1179`) does **not** set it, so both defaults apply.
   `AddContourTrace` already switches other defaults on the plane (`ShowFill`, `FadeLineOpacity`,
   `DrawLabels = (plane == SurfacePlane.Z)`), so a plane-dependent `LabelSpacing` fits the existing
   shape exactly.
4. **The Heatmap option** is enumerated straight off the enum:
   `TraceRowViewModel.ContourFillOptions => Enum.GetValues<ContourFillSelection>()` (`:2120`), bound
   by `PlotInspectorView.axaml:832-846`. `ContourFillSelection` is `{ None, Topography, Heatmap }`
   (`Models/ContourData.cs:26`); the VM property `SelectedContourFill` (`:519-531`) maps it onto
   `ContourShowFill` + `ContourSelectedFillKind`.
5. **Auto-create** — `WorkspaceViewModel.AutoOpenOrCreateDataDisplayAsync` (`:8182-8263`). After a
   run it reuses the initial tab's **single** seeded plot, sets its type to Rect or Table from
   `PlotInspectorViewModel.HasPlottableData`, and calls `AddTraceCommand` — which seeds from
   `FirstPlottableCubeName`, i.e. whatever cube happens to come first. For a loadpull run that is
   the nonsensical plot the owner is reporting. It ends by saving the `.cdd`.
6. **You cannot tell a Γ grid from a Z grid by which cube exists.** `LoadpullEngine`
   (`src/Engine/Loadpull/LoadpullEngine.cs:434-435`) emits **both** `ZLoad` and `GammaLoad`,
   always, for every loadpull. So the owner's VSWR heuristic is not a convenience — it is the only
   available signal. (`LoadpullRecognition` accepts either cube for the same reason.)
7. `PlotInspectorViewModel.AddContourTrace` picks `SurfacePlane` from the plot type at creation, and
   `TraceRowViewModel.RebuildContour` re-derives it from `_parent.PlotType` on every rebuild
   (`:549-551`) — so a contour follows a plot-type change on its own; §4 only has to choose the
   right plot type up front.
8. `DataDisplayViewModel.AddPlot(...)` accepts an explicit `left`/`top` and otherwise calls
   `ComputeNewPlotPosition` (`:549-604`), which infers a grid from the in-view plots. §4 places two
   plots deliberately, so pass explicit positions rather than relying on the inferred grid.

---

## §1 — Contour marker glyph: harmonicaRF size, name inside when short [CHANGE]

Bring the **loadpull contour** marker in line with harmonicaRF's termination marker. Scope is the
contour marker only — the polyline/spectrum/table markers keep today's triangle.

- **Size:** same size *relative to the plot* as harmonicaRF's, i.e. adopt anchor (2)'s rule —
  `r = max(6f, min(canvasW, canvasH) * 0.020)` — instead of deriving the radius from
  `SymbolTextSize`. Note this is canvas-proportional, which is also what round-7 §2 established for
  every other in-plot contour element (the canvas already encodes zoom, so do **not** multiply by
  `zoomLevel`).
- **Name placement, by length:**
  - name length **≤ 2** characters (`m1`, `m2`) → render it **inside** the circle, centred, using
    harmonicaRF's metrics: `PlexBold` at `r * 1.15f`, baseline at `centre.Y + ts * 0.36f`, black.
  - name length **> 2** → keep today's behaviour exactly: centred above the glyph at
    `dataPx.Y - ts - 4f`.
- **Interior colour:** derive it from the **Bone** colormap
  (`ContourColormaps.Sample(ContourColorMap.Bone, t)`) and lighten it so the black name has
  contrast. Implement it as a helper with a **luminance floor** — lighten toward white until the
  fill's luminance clears the floor — mirroring the luminance-*ceiling* helper round-7 §3 added for
  iso-line colour (`Renderers/ContourRenderer.cs:404`). Pick the sample point and the floor by
  eye against a Bone-filled contour and state both in the completion note; the floor is what makes
  this robust, not the specific sample point.
- **Keep:** the black ring (it distinguishes an interpolated Mode-1 reading), the
  `ContourSnapped`/mode-2 distinction, the selection highlight (**never** change the selection
  algorithm — see the standing comment at `:341`), and `SymbolHitRadius`' relationship to the drawn
  size (update it if the radius rule changes, so hit-testing still matches the glyph).

**Gate:** a contour marker named `m1` shows its name inside a Bone-toned circle, legible in both
light and dark themes; a marker named `peak1` shows the name above an identical circle; the glyph is
the same on-screen size as a harmonicaRF termination marker on an equally-sized Smith chart; zooming
scales it with the grid; harmonicaRF is untouched (assert no file under `src/Ui/Harmonica` changed).

## §2 — New Rect contour trace defaults to label spacing 150 [CHANGE]

`LabelSpacing = 150` for a contour trace created on a **Rect** plot. Set it in `AddContourTrace`
alongside the other plane-dependent defaults (anchor 3) rather than changing
`ContourData.LabelSpacing`'s own default — Smith/Polar contours keep 30, and an existing `.cdd`
keeps whatever it saved.

**Gate:** `+ Contour` on a Rect plot yields a card showing 150; on Smith it still shows 30; a
reloaded `.cdd` keeps its saved value.

## §3 — Remove Heatmap from the fill-style picker (keep the code) [CHANGE]

The heatmap path is experimental and must not be reachable from the UI. Keep
`ContourFillSelection.Heatmap`, `ContourFillKind.HeatMap`, `ContourFillType.HeatMap`,
`ContourData.Scatter`, and the renderer's heatmap branch **intact** — this is a UI-surface removal
only, so the experiment can be re-enabled by restoring one list.

**Fix:** stop enumerating the enum (anchor 4). `ContourFillOptions` returns
`[None, Topography]` explicitly, with a comment naming this brief and stating that the Heatmap
member is deliberately withheld. Verify nothing else surfaces it: grep the DataDisplay XAML and VMs
for `Heatmap` / `HeatMap` / `IsHeatMapFill` and confirm the remaining hits are render-side only.
A `.cdd` saved earlier with Heatmap selected must still load and render — `SelectedContourFill`'s
getter (`:519-525`) can return a value not in the list; check that this does not blank the
`IconSelectButton`, and if it does, fall the *display* back to Topography without rewriting the
saved model.

**Gate:** the fill-style picker offers None and Topography only; a `.cdd` with a saved heatmap still
opens and renders; no heatmap code is deleted.

## §4 — Auto-created Data Display for a loadpull run: Pout + Efficiency contours [CHANGE]

**Today:** one plot, one arbitrary trace off `FirstPlottableCubeName` (anchor 5) — meaningless for a
loadpull result.

**Required:** when the run that triggered auto-create produced **loadpull** data
(`LoadpullRecognition.IsLoadpull(ds)` — shape-based, so it covers Loadpull and Loadpull Pursuit,
`Models/LoadpullRecognition.cs`), create **two** contour plots side by side:

| plot | metric | constraint |
|---|---|---|
| left  | Pout (dBm) | constant compression, 3 dB |
| right | Efficiency | constant compression, 3 dB |

- **Both** use `ConstraintKind.Compression` with `ConstraintValue = 3.0` (already `ContourData`'s
  default, `:60` — set it explicitly anyway so a later default change cannot silently alter this).
- Metric names must be the canonical cube names the engine emits — `Pout_dBm` and `Efficiency` (see
  `LoadpullRecognition.FomCubes` and `AutoFillSummary`'s column list,
  `PlotInspectorViewModel.cs:1281-1287`). Skip a plot whose metric cube is absent rather than
  creating an empty one.
- **Plot type from the grid plane** — see §4a.
- **Placement:** right plot to the right of the left one with **explicit** positions and a real gap
  between them (anchor 8 — pass `left`/`top` to `AddPlot`; do not let `ComputeNewPlotPosition`
  infer). Size each per its type (square for Smith/Polar, `width / RectAspectRatio` for Rect — the
  same rule brief DD-P §2 applies). Both must be fully inside the initial viewport.
- Reuse the tab's single seeded plot as the **first** of the two (anchor 5's existing rule — only
  one plot exists after a plain auto-create; here exactly two must exist, not three).
- Non-loadpull runs keep today's behaviour byte-identical.
- The existing "no default plot" warning (`:8246`) must not fire when the two contours were created.

### §4a — Detect a Γ grid vs an impedance grid [CHANGE]

Both `GammaLoad` and `ZLoad` are always emitted (anchor 6), so decide from the **geometry**:
compute the VSWR of the grid's termination points and take the maximum;
`VSWR = (1+|Γ|)/(1−|Γ|)` from the `GammaLoad` cube. **Max VSWR > ~15 → Γ grid → Smith Chart for
both plots. Otherwise → impedance grid → Rect for both.**

- Put the threshold in one named constant with a comment recording the owner's rule and that it is
  a heuristic, not a hard fact of the data.
- Guard the singularity: `|Γ| → 1` gives an infinite VSWR — clamp or skip non-finite points rather
  than letting a NaN decide the plot type.
- Put the detector next to `LoadpullRecognition` (shape/geometry recognition of loadpull data is
  already that file's job) so the contour code and any future consumer share it.
- **Verify against both real fixtures** before trusting it — one Γ-grid run and one impedance-grid
  run. If the measured separation is not clean, report the measured numbers and stop rather than
  tuning the threshold until the two fixtures happen to land on opposite sides.

**Gate:** running a Γ-grid loadpull auto-creates two Smith contour plots (Pout dBm left, Efficiency
right, both at 3 dB compression) with visible padding between them, both fully in view, and the
`.cdd` saves and reloads to the same picture. An impedance-grid loadpull produces the same two plots
as Rect. A Loadpull Pursuit run produces the same. An HB or S-parameter run is unchanged.

---

## Slice plan

- **C1 — §3 heatmap removal** and **§2 label spacing.** Two-line changes; land together, get them out
  of the way.
- **C2 — §1 marker glyph.** Renderer-only; owner-verifies against harmonicaRF side by side.
- **C3 — §4a the grid-plane detector**, with the fixture measurement reported.
- **C4 — §4 the auto-created two-plot display.** Last; it consumes C3.

## Constraints / gotchas

- **`src/Ui/Harmonica` is read-only for this brief.** §1 copies numbers out of it; a diff there is a
  bug.
- Round-7 §2's rule stands: size in-plot contour elements from `canvasSize`, never from `zoomLevel`
  — the canvas already encodes zoom, so using both double-counts.
- §4 runs inside `AutoOpenOrCreateDataDisplayAsync`, which is `async` and ends by writing the `.cdd`.
  Contours are built by `TraceRowViewModel.RebuildContour` (RBF fit) — make sure both plots are
  fitted *before* the save, or the reloaded display will differ from the one on screen.
- The RBF fit is the expensive part of a contour. Two plots means two fits; measure the added time
  on a large loadpull and report it. If it is material, say so — do not add a spinner.
- `.cdd` round-trip after each slice.
- TreatWarningsAsErrors: nullable props → locals; no unused privates; no `<`/`>` in XML doc comments.

## Tests

- §1: name-length branch (`"m1"` inside, `"peak1"` above) and the derived fill colour's luminance
  clears the floor — unit-test the colour helper directly; owner-verifies the visual.
- §2: `AddContourTrace` on Rect → `LabelSpacing == 150`; on Smith → 30.
- §3: `ContourFillOptions` has exactly two members and does not contain Heatmap; a `.cdd` carrying
  Heatmap loads and still renders through the heatmap branch.
- §4a: the detector on a Γ-grid fixture and an impedance-grid fixture, plus a `|Γ| == 1` point that
  must not produce NaN/∞.
- §4: after auto-create on a loadpull `run.npy` — exactly 2 plots, correct types, metric names,
  `ConstraintKind.Compression` at 3.0, non-overlapping bounds with a gap, both within the viewport;
  no "no default plot" warning; non-loadpull auto-create unchanged (assert 1 plot).
- `dotnet test tests/Ui.Tests` then `dotnet test tests/Firewall.Tests` (separate invocations).

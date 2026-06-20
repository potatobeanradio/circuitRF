# Brief 7.4e — Contour trace inspector card + `.s1p` overlay (the authoring UI)

**Phase:** 7.4e (Data Display loadpull contours — the inspector card that authors a contour trace end-to-end;
the LAST 7.4 sub-gate).
**Design:** `docs/design/loadpull-contours.md` §1.5 (contour trace = another inspector card kind), §2.5 (fills),
§3 (7.4e), §5 (7.4e open Qs); `data-display.md` §2.8 (per-trace-kind card bodies — the extension point).
**Goal:** make a contour trace fully authorable from the `PlotInspectorView` trace card: pick the loadpull
DataSet + metric + constraint, set the iso-line level set (start:step:stop OR a level count), toggle
iso-lines / fill / labels, choose fill type, and (deferred sub-items) label styling + colormap. Bind
`LoadpullSurface` → `ContourData.Grid`/`Scatter` so the 7.4d renderer draws it. Plus a `.s1p` reflection
overlay.

**Consumes (verified on disk):**
- `Trace.ContourData` (`ContourData?`) + `Trace.IsContourTrace` (7.4d, landed) — the renderer reads these.
- `ContourData` (7.4d): `Grid`, `Scatter`, `Levels` (`ContourLevelSet`), `FillType`
  (`ContourFillType{None,Lines,TopoMap,HeatMap}`), `LineColor`, `StrokeWidth`, `DrawLabels`,
  `GetPolylines()` (cache). **7.4e extends this** with the new authoring/display fields (§2).
- `RfCore.Loadpull.LoadpullSurface` (7.4a–c): `new LoadpullSurface(DataSet, group)`, `Fit(...) → LoadpullFit?`,
  `Resample(fit, box, resolution) → SurfaceGrid`, `Reduce(...) → ScatterReduction`, `RecommendedBox(fit)`,
  `MaxPower`/`MaxEfficiency`, `Frequencies`, `RecommendedCompression(freqIdx)`, the `SurfacePlane{Gamma,Z}` /
  `ConstraintSpec` types.
- `RfCore.Loadpull.ContourExtractor.LevelsByStep(grid, step, anchor)` / `LevelsBetween(grid, n)` (7.4d) — build
  the `ContourLevelSet` from start:step:stop or a count.
- `TraceRowViewModel` (the card VM): `IsStandardTrace => true` placeholder with the comment *"7.4 adds
  IsContourTrace sibling"*; `[ObservableProperty]` + `partial void On…Changed` pattern; wraps `_trace`, calls
  back to `_parent` (`PlotInspectorViewModel`). Per-kind card bodies switch on `IsVisible="{Binding …}"`.
- `PlotInspectorView.axaml`: card conventions — `Classes="label"` (FontSize 10, opacity 0.6), `seg-btn`
  segmented toggles, `ctl:IconSelectButton` compact pickers, compact `ComboBox`/`NumericUpDown`/`Slider`
  styles already defined; `Border.traceCard`. **No native `TabControl`** — see §3.

**Firewall:** all surface math stays in `LoadpullSurface` (RfCore). The card is `src/Ui` MVVM; the VM owns the
`LoadpullSurface` instance + cache and pushes `SurfaceGrid`/`ScatterReduction`/`ContourLevelSet` into
`ContourData`. `Trace` never holds a `DataSet` (mirror the existing cube-bound rule: the owner resolves data
and injects arrays).

---

## 1. What the card must author (owner's spec)

Per-trace contour controls (some render-deferred — build the binding + state now, defer only the *visual*):
- **Iso-line level set** — start : step : stop **OR** number of levels. User may specify either way (a small
  segmented toggle "Range / Count"; Range shows three compact numeric fields, Count shows one).
- **Show iso-lines** (on by default).
- **Show fill** (default depends on plane — see §4).
- **Show labels** (on by default). *(Rendering the label box on the iso-lines is deferred — 7.4d's
  `DrawIsoLines` already has a basic label; the richer label box can come later. Wire the toggle + the two
  styling fields now.)*
- **Label background colour** (state + binding now; render later).
- **Label spacing** (state + binding now; render later).
- **Fill type** — TopoMap / HeatMap selector (plus None), per §2.5.
- **Gradient colour map** — the matplotlib-style names (`gray, bone, pink, spring, summer, autumn, winter,
  cool, Wistia, hot, afmhot, gist_heat, copper`). **Implementation deferred** — add the enum + the picker +
  the persisted field now; the renderer keeps its current palette until a later pass maps these names to
  ramps.

Group the secondary/styling controls behind a collapsible **"Options"** sub-section inside the card (§3) so the
card stays compact: the always-visible part is metric/constraint/level-set + the three primary toggles; the
Options reveal holds label bg colour, label spacing, colormap, and fill-type fine controls.

---

## 2. Model — extend `ContourData` (UI model) + a small VM-side surface holder

Add to `src/Ui/DataDisplay/Models/ContourData.cs` (keep 7.4d's existing fields):
```csharp
public enum ContourLevelMode { Range, Count }
public enum ContourColorMap   // matplotlib names; render mapping deferred
{ Gray, Bone, Pink, Spring, Summer, Autumn, Winter, Cool, Wistia, Hot, Afmhot, GistHeat, Copper }

// Level-set authoring (drives ContourExtractor.LevelsByStep / LevelsBetween → Levels)
public ContourLevelMode LevelMode  { get; set; } = ContourLevelMode.Range;
public double LevelStart { get; set; }
public double LevelStep  { get; set; }
public double LevelStop  { get; set; }
public int    LevelCount { get; set; } = 10;

// Display toggles (FillType already exists; add explicit show flags)
public bool ShowIsoLines { get; set; } = true;
public bool ShowFill     { get; set; }            // default set per-plane at creation (§4)
// DrawLabels already exists (show labels)

// Label styling (state now; richer render deferred)
public SKColor LabelBackground { get; set; } = new SKColor(0, 0, 0, 140);
public double  LabelSpacing    { get; set; } = 1.0;   // along-polyline spacing factor

// Colormap (picker + persist now; render mapping deferred — renderer keeps current palette)
public ContourColorMap ColorMap { get; set; } = ContourColorMap.Hot;
```
The **`FillType` the renderer reads** should be derived from `ShowFill` + the selected fill kind: when
`ShowFill` is false → `ContourFillType.None`; else the chosen TopoMap/HeatMap. Keep a separate
`SelectedFillKind {TopoMap,HeatMap}` so toggling ShowFill off/on remembers the kind. (Either compute `FillType`
in a getter from `ShowFill`+`SelectedFillKind`, or have the VM set `FillType` whenever either changes — pick
one and keep the renderer contract unchanged.)

> Persistence: these are new defaulted fields on a UI model that serializes into `.cdd`
> (`DataDisplayConfig`/`DataDisplayViewModel.BuildPlotContainerConfig`). Per the alpha no-back-compat rule,
> adding defaulted fields is safe; bump nothing, write them, let old files load with defaults. Confirm the
> contour trace round-trips through `.cdd` (a contour trace currently may not serialize at all — see §6).

---

## 3. The custom "Options" sub-tab (NOT native `TabControl`)

Owner's direction: do **not** use Avalonia's `TabControl` (too big/heavy for a trace card). Build a tiny
in-card disclosure with a **subtle title**, matching the inspector's existing visual language (the card already
hand-rolls controls like `IconSelectButton` via a `ControlTheme`, so a bespoke reveal is in-keeping).

Recommended minimal approach (no new control class needed):
- A header row that is a flat toggle: a small chevron (`mi:MaterialIcon Kind="ChevronRight"`/`ChevronDown`) + a
  subtle "Options" label (`Classes="label"`), styled like the card's other subtle titles. Bind its
  `IsChecked` to a VM `OptionsExpanded` bool (or a `ToggleButton` with the chevron rotating).
- Below it, a `StackPanel` whose `IsVisible="{Binding OptionsExpanded}"` holds the deferred/secondary controls.
- Keep it within the existing `traceCard` `Border`; no extra chrome. One subtle separator above if needed.

If a reusable control reads cleaner across multiple future cards, a small `templated` `Expander`-like control
is acceptable — but the bool-toggle + `IsVisible` panel is the smallest thing that satisfies "our own subtle
tab," so prefer it unless there's a second consumer. Either way: **subtle**, compact, no heavy tab chrome.

---

## 4. Defaults (owner-specified — bake these at contour-trace creation)

**Level set start : step : stop**, by metric (when the metric is recognized; else fall back to
`LevelsBetween(grid, 10)`):
| Metric      | Start | Step | Stop |
|-------------|-------|------|------|
| Pout (dBm)      | −30   | 0.5  | 60   |
| Efficiency (%)  | 0     | 5    | 100  |
| Gain (dB)        | −10   | 0.5  | 50   |
| AMPM (°)        | −200  | 5    | 200  |

**Display defaults:**
- **Show fill:** OFF by default on **Smith/Polar (Γ-plane)**; ON by default on **Rect (Z-plane)**.
- **Show iso-lines:** ON by default.
- **Show labels:** ON by default.

These are creation-time defaults. **Per design §6 / owner: the ability to change these defaults in
`SettingsView` (AppSettings) is DEFERRED** — do not build the settings UI now. Put the default values in one
small static helper (e.g. `ContourDefaults`) so a future AppSettings pass can override them in one place; the
card reads from that helper at creation. Note this seam in a `// DEFERRED (AppSettings):` comment.

---

## 5. Wiring — VM owns `LoadpullSurface`, pushes into `ContourData`

In `TraceRowViewModel` (the per-trace card VM), add the contour sibling to the existing per-kind pattern:
- `public bool IsContourTrace => _trace.IsContourTrace;` and **change** the standard-body guard so the standard
  controls hide for a contour trace (today `IsStandardTrace => true`; make it `=> !IsContourTrace`). The 7.4d
  comment already anticipates this.
- `[ObservableProperty]` fields for: selected metric, constraint (compression dB or constant-other-metric),
  frequency pin (when the DataSet is multi-freq), Γ/Z plane (follows plot type but exposed), level mode +
  start/step/stop/count, the three show toggles, fill kind, colormap, label bg + spacing, `OptionsExpanded`.
- On any authoring change, the VM:
  1. ensures a `LoadpullSurface` for the bound loadpull DataSet (build once, cache on the VM — keyed like the
     existing source resolution; the surface itself caches fits),
  2. calls `Fit(freqIdx, metric, constraint, plane, z0)` → if null (too few points) surface a soft message and
     draw nothing,
  3. `Resample(fit, box: null /*auto via RecommendedBox*/, resolution)` → `SurfaceGrid`,
  4. builds `ContourLevelSet` via `LevelsByStep`/`LevelsBetween` from the level-set fields,
  5. assigns `Grid`/`Scatter`/`Levels`/`FillType`/show-flags/colours into `_trace.ContourData`,
  6. triggers the plot redraw (same path other card edits use — `_parent` notify / rebuild).
- **Data resolution mirrors cube-bound traces:** the owner (`PlotInspectorViewModel`) resolves the loadpull
  DataSet from the source library; `Trace` holds no `DataSet`. The VM holds the `LoadpullSurface` (which holds
  the DataSet by reference for its own lifetime) — that's fine, it's a UI-side derived object, not the `Trace`.
- **Metric list / source eligibility:** a source is contour-eligible when it's a loadpull DataSet (has the
  `{gridPoint,pinStep}` FOM cubes + `GammaLoad`). Offer the contour trace kind only for such sources; the
  metric picker lists the FOM cubes present (Pout/DE/PAE/Gt/Gp/…). (How the user *chooses* "contour" as the
  trace kind — e.g. a kind toggle on the card or an "Add contour trace" affordance — follow the existing
  add-trace UX; keep it minimal and HIG-clean.)

---

## 6. `.s1p` reflection overlay
A contour plot may overlay a 1-port Touchstone reflection (`.s1p`) Γ trace on the Smith substrate (design §1.4:
contour overlays `.s1p`; does **not** mix with power-sweep line traces). This is just a normal Γ trace drawn
over the contour: ensure a `.s1p` source can be added as an ordinary reflection trace on the same Smith plot as
a contour trace, and that the two render together (contour fill under, iso-lines, then the `.s1p` Γ-locus line
+ markers on top — the existing trace draw order already puts contour fills under traces). Mostly a
"don't block it" + a styling check; no new overlay machinery if the standard trace already lands on the Smith
plot correctly.

---

## 7. Slice plan (compile-and-test-gated)
- **7.4e-1 — model + defaults.** Extend `ContourData` (§2) + `ContourDefaults` helper (§4). Compile; a unit
  test constructs `ContourData`, applies metric defaults, builds a `ContourLevelSet` via the extractor, asserts
  the level array matches start:step:stop. Confirm `.cdd` round-trip of a contour trace (add serialization if
  missing).
- **7.4e-2 — VM contour sibling + wiring.** `TraceRowViewModel` contour properties; `IsStandardTrace =>
  !IsContourTrace`; the Fit→Resample→Levels→ContourData push (§5). Headless-ish: a VM test drives a loadpull
  DataSet (from `testdata/spl_test_data/Ideal_GaN_FET_1p6_mm_1p8_GHz.spl` via the 7.4f reader) and asserts
  `_trace.ContourData.Grid` is populated and `GetPolylines()` returns non-empty.
- **7.4e-3 — card UI.** The contour card body in `PlotInspectorView.axaml` (metric/constraint/freq pickers,
  level-mode toggle + fields, three show toggles, fill-kind selector) + the custom Options reveal (§3) with the
  deferred controls (label bg, spacing, colormap). HIG-clean, matches card styling. Owner-verified visually.
- **7.4e-4 — `.s1p` overlay + plane defaults.** Confirm fill-default-by-plane (§4), `.s1p` overlays on a Smith
  contour, live edits re-fit + redraw.

## 8. Constraints / gotchas
- **No native `TabControl`** for Options (§3) — bespoke subtle disclosure.
- **Renderer contract unchanged:** 7.4d's `ContourRenderer`/`PlotRenderer` read `ContourData.FillType` + show
  state. Derive `FillType` from `ShowFill`+fill-kind; don't change the renderer signatures. `ShowIsoLines`
  false ⇒ skip the iso-line draw (the `PlotRenderer` contour branch currently always draws lines when
  `FillType != None` — adjust that gate to honour `ShowIsoLines` independently of fill).
- **Deferred = wire-but-don't-render:** label bg colour, label spacing, colormap — add state + binding +
  picker, leave renderer using current behaviour. Mark each `// DEFERRED:`.
- **AppSettings defaults = deferred:** centralize defaults in `ContourDefaults`; no `SettingsView` work now.
- **Firewall / Trace-holds-no-DataSet:** VM owns `LoadpullSurface`; inject arrays into `ContourData`.
- **TreatWarningsAsErrors** (UI): capture nullable props into locals; no unused privates; no `<`/`>` in XML doc
  comments. Compact styles already exist — reuse, don't redefine.
- **HIG:** compact, subtle, consistent with the existing card (label opacity 0.6, seg-btn toggles, 10px fonts).

## 9. Tests
- `ContourData`/`ContourDefaults`: metric-default level sets resolve to the expected start:step:stop arrays;
  `FillType` derivation from `ShowFill`+kind; plane-based show-fill default.
- VM: contour trace over the real `.spl` populates `Grid`+polylines; switching metric/level-mode re-resolves.
- `.cdd` round-trip of a contour trace (defaulted fields survive).
- UI is owner-verified (visual) per gates.

## 10. Out of scope (post-7.4 / deferred, noted in design)
- Rendering the rich label box on iso-lines (richer than 7.4d's basic label); label bg + spacing *render*.
- Colormap *render* mapping (matplotlib names → actual ramps).
- AppSettings UI to change contour defaults (`SettingsView`).
- HeatMap density-surface vectorization (already noted as a future option in design §2.5).
- Power-sweep synthesis (7.4c) plotted as a trace — a separate future trace kind, not part of the contour card.

# Brief 7.4h-1 — Contour UX round 1: card cleanup, Options tab, Import menu, model fields

**Phase:** 7.4h (contour UX iteration, round 1 — part 1 of 2). Pairs with **7.4h-2** (renderer: titles, axis
labels, impedance grid, autoscale, colormap, MXP/MXE/grid-point drawing). This brief = the **card, VM, model,
and File-menu** changes. The two briefs share the `ContourData` model additions in §1 — land §1 first.

**Design:** `docs/design/loadpull-contours.md` §2.5 / §3 (7.4e), `data-display.md` §2.8 (per-trace-kind cards).
**Goal:** make the contour trace card correct and pleasant: remove the confusing inherited identity row, add
the new option fields in the right order on the Options tab, fix control widths, convert Fill to an
`IconSelectButton`, hide Matrix-type and →R for contour traces, and add a File ▸ Import menu that registers
`.spl`/`.lpcwave`/`.npy` as Known Files.

**Consumes (verified on disk):**
- `ContourData` (`src/Ui/DataDisplay/Models/ContourData.cs`): already has `MetricName`, constraint fields,
  level-set fields, `ShowIsoLines`/`ShowFill`/`DrawLabels`, `SelectedFillKind`+derived `FillType`, `LineColor`,
  `LabelBackground`, `LabelSpacing`, `ColorMap`. **§1 adds the new fields.**
- `TraceRowViewModel` (`…/ViewModels/TraceRowViewModel.cs`): contour region already present —
  `IsContourTrace`, `IsStandardTrace => !IsContourTrace`, all the `Contour*` `[ObservableProperty]` fields +
  `On…Changed` handlers + `RebuildContour()` + commands (`SetTopoMapFillCommand`, etc.) + `AllContourColorMaps`.
  **§2/§3 add the new VM members.**
- `PlotInspectorView.axaml`: the identity row (group combo + signal combo + matrix-type ISB + →R button) is
  shared by ALL trace kinds incl. contour (the source of the user's "Signals / GammaLoad" confusion — §4); the
  contour body + Options disclosure already exist (chevron + `ContourOptionsExpanded`).
- `ctl:IconSelectButton` (`…/Controls/IconSelectButton.cs`): the compact picker control with `ItemsSource`/
  `SelectedItem`/`ItemTemplate`/`Highlight`/`HighlightSelected`. Used for Line/Symbol/Matrix pickers — reuse
  for the Fill selector (§6).
- `LoadpullSurface.Fit(freqIdx, metricY, constraint, plane, z0=null, RbfKernel kernel=Multiquadric,
  double smooth=1e-3)` — **kernel + smooth already pluggable**; `RbfKernel{Multiquadric,ThinPlate,Gaussian}`.
  `Rbf2D` ctor already takes `double? epsilon=null`. **Epsilon needs forwarding through `Fit`** (§3, small
  engine add).
- File menu: `src/Ui/Views/WorkspaceWindow.axaml` `<NativeMenu Header="File">`; commands on
  `WorkspaceViewModel` (e.g. `AddLibraryCommand`, `OpenDataDisplayFileCommand`) with file pickers. Known-file
  add path already exists (drag-and-drop registers known files; 7.4g generalized the provider to `.spl`/
  `.lpcwave`).

**Firewall / rules:** UI + VM only here (engine epsilon-forward is the one RfCore touch, §3).
TreatWarningsAsErrors-clean; reuse existing compact styles; HIG (subtle, 10px, `Classes="label"`).

---

## 0. Answering the user's questions (informs §4)
The "Signals" combobox and the combo to its right are the **shared identity row** (group header +
signal/cube picker) that every trace card shows. For a contour trace they are meaningless — the metric is
chosen by the contour body's **Metric** combo, and `GammaLoad`/`ZLoad` are coordinate cubes, not plottable
metrics, so selecting them does nothing. **Resolution: hide the entire identity row for contour traces** (§4).
That removes both comboboxes (and the matrix-type + →R controls) in one move, and the trash button moves into
the freed top-right.

---

## 1. Model — add the new `ContourData` fields (LAND FIRST; shared with 7.4h-2)
Add to `ContourData`:
```csharp
// MXP / MXE markers (drawn by 7.4h-2)
public bool DisplayMxp { get; set; }
public bool DisplayMxe { get; set; }

// Original loadpull grid points (the scatter datapoints behind the surface)
public bool    DisplayGridPoints { get; set; }
public SKColor GridPointColor    { get; set; } = SKColors.Black;   // default black

// Label foreground (text colour); LabelBackground already exists
public SKColor LabelForeground { get; set; } = SKColors.White;

// Engine fit params (wired into LoadpullSurface.Fit / Rbf2D)
public RbfKernel InterpKernel { get; set; } = RbfKernel.Multiquadric;
public double    Smoothing    { get; set; } = 1e-3;
public double?   Epsilon      { get; set; } = null;   // null ⇒ scipy default
```
`using RfCore.Loadpull;` is already present (for `RbfKernel`). These persist in `.cdd` via the existing
`ContourTraceConfig` — add the fields there too (defaulted ⇒ alpha-safe; old files load with defaults). Confirm
round-trip.

---

## 2. VM — new observable fields + handlers (TraceRowViewModel)
Mirror the new model fields with `[ObservableProperty]` + `On…Changed`, following the existing contour pattern
(guard with `_suppressContourCallback`; write to `cd`; `DisplayGridPoints`/colors/MXP/MXE call `_parent.Notify()`
for a redraw-only change; `InterpKernel`/`Smoothing`/`Epsilon` call `RebuildContour()` because they change the
fit):
```csharp
[ObservableProperty] private bool      _contourDisplayMxp;
[ObservableProperty] private bool      _contourDisplayMxe;
[ObservableProperty] private bool      _contourDisplayGridPoints;
[ObservableProperty] private SKColor   _contourGridPointColor = SKColors.Black;
[ObservableProperty] private SKColor   _contourLabelBackground;
[ObservableProperty] private SKColor   _contourLabelForeground;
[ObservableProperty] private RbfKernel _contourInterpKernel = RbfKernel.Multiquadric;
[ObservableProperty] private double    _contourSmoothing = 1e-3;
[ObservableProperty] private double    _contourEpsilon;      // 0 ⇒ treat as null (scipy default)
```
- MXP/MXE/grid-point/color/label-color changes → set `cd.*`, `_parent.Notify()`.
- `InterpKernel`/`Smoothing`/`Epsilon` changes → set `cd.*`, `RebuildContour()`.
- Initialize all from `cd` in the ctor's `if (trace.ContourData is { } cd)` block (mirror existing).
- Add `public static IReadOnlyList<RbfKernel> AllRbfKernels { get; } = Enum.GetValues<RbfKernel>().ToList();`
  for the picker; a short display-name converter (`Multiquadric`→"Multiquadric", `ThinPlate`→"Thin Plate",
  `Gaussian`→"Gaussian").
- Wire `RebuildContour` to pass the fit params:
  `surface.Fit(freqIdx, cd.MetricName, constraint, plane, z0: null, kernel: cd.InterpKernel,
  smooth: cd.Smoothing, epsilon: cd.Epsilon)` (after §3 adds the `epsilon` param).

### Grid-point color picker
Use the **same color-picker pattern as the Settings color setup** (a small square swatch button that opens the
color picker dialog). Find the Settings swatch pattern (`SettingsView.axaml` / `ColorPickerDialog`) and reuse
it: a small square `Button` whose background = `ContourGridPointColor`, click → `ColorPickerDialog`, result →
the property. Add a `PickGridPointColorCommand` (and the same for label bg/fg if you make those swatches too —
see §5 ordering). Keep it tiny and aligned.

---

## 3. Engine — forward `epsilon` through `LoadpullSurface.Fit` (RfCore, small)
`Rbf2D` already accepts `double? epsilon`. Add an `epsilon` param to `LoadpullSurface.Fit` and the `FitKey`
cache key, and forward to `new Rbf2D(...)`:
```csharp
public LoadpullFit? Fit(
    int freqIdx, string metricY, ConstraintSpec constraint, SurfacePlane plane,
    double? z0 = null, RbfKernel kernel = RbfKernel.Multiquadric,
    double smooth = 1e-3, double? epsilon = null)
{
    var key = new FitKey(freqIdx, metricY, constraint, plane, z0, kernel, smooth, epsilon);
    …
    var rbf = new Rbf2D(reduction.Coords, reduction.Values, kernel, smooth, epsilon);
    …
}
```
Add `epsilon` to the `FitKey` record. `DataInterpStack` / power-sweep callers keep the default (pass nothing).
Gate: a unit test that two `Fit` calls with different epsilon produce different cache entries and (on the test
`.spl`) measurably different surface values.

---

## 4. Card — hide the identity row for contour traces
The identity row `<Grid>` (group combo, signal combo/ISB, matrix-type ISB, →R button) currently always renders.
Wrap it so it's hidden for contour traces: add `IsVisible="{Binding !IsContourTrace}"` to that identity-row
Grid. This satisfies three asks at once: removes the "Signals"/`GammaLoad` combos, never shows Matrix-type for
contour, never shows →R for contour. The trash button already sits in `Grid.Column="1"` at the card top-right
and remains; with the identity row hidden, the contour body becomes the top content (trash stays aligned —
verify it doesn't overlap; if needed, give the contour body a small top margin).

> Also independently honor the explicit asks even outside the identity row: ensure no Matrix-type ISB and no
> →R toggle can appear for a contour trace. (Hiding the identity row covers both, but if either control is
> referenced elsewhere, gate with `!IsContourTrace`.)

---

## 5. Card — Options tab contents + order
Per the spec, the Options disclosure (already built) should contain these rows **in this order**:
1. **DisplayMXP** — short label "Max Power" (or "MXP"); seg-btn toggle (`ContourDisplayMxp`).
2. **DisplayMXE** — "Max Eff" (or "MXE"); seg-btn toggle (`ContourDisplayMxe`).
3. **Lines / Fill / Labels** — MOVE the three show-toggles here, reordered to **Labels / Lines / Fill** (§6).
4. **Grid points** — "Grid Pts" seg-btn toggle (`ContourDisplayGridPoints`) + the small square color swatch
   button (`ContourGridPointColor`, default black, opens color picker — §2).
5. **Label colors** — "Label Bg" swatch + "Label Text" swatch (`ContourLabelBackground` / `ContourLabelForeground`).
   Short labels; small square swatches like grid-point color.
6. **Interp / Smoothing / Epsilon** — "Interp" combo (`AllRbfKernels`, display-name template), "Smooth" numeric
   (`ContourSmoothing`, small step e.g. 1e-4–1e-1, scientific format), "Eps" numeric (`ContourEpsilon`,
   0 ⇒ default). Lay out compactly (e.g. two rows: Interp on one; Smooth + Eps sharing a row).

Use **nice, short names** (UX): "Max Power", "Max Eff", "Grid Pts", "Label Bg", "Label Text", "Interp",
"Smooth", "Eps" — or tooltips with the full names. Keep the existing colormap + label-spacing controls in
Options too (place colormap near the fill/label group; label-spacing near label colors). Everything stays in
the bespoke Options `StackPanel` (`IsVisible="{Binding ContourOptionsExpanded}"`), no native TabControl.

> The three show-toggles (Lines/Fill/Labels) currently live in the always-visible part of the contour body.
> The spec puts "Lines, Fill, Labels" on the Options tab (item 3). Move that `StackPanel` into the Options
> panel, reorder to **Labels / Lines / Fill** (§6). The fill-kind selector (§6) can stay just below it.

---

## 6. Card — Labels/Lines/Fill row + Fill as IconSelectButton
- **Reorder** the three toggles from Lines/Fill/Labels to **Labels / Lines / Fill**.
- **Convert Fill** from the current seg-btn toggle + separate TopoMap/HeatMap toggle into a single
  `ctl:IconSelectButton` with **text options "None", "Topography", "Heatmap"**, `HighlightSelected="False"`
  (no highlight). Wire it to drive rendering:
  - Build a small VM list `ContourFillOptions` (e.g. a record/enum-backed item with `Label` + a
    `ContourFillSelection {None, Topography, Heatmap}`), bound `SelectedItem` ↔ a VM property
    `SelectedContourFill`.
  - On change: `None` → `cd.ShowFill = false`; `Topography` → `cd.ShowFill = true; cd.SelectedFillKind =
    TopoMap`; `Heatmap` → `cd.ShowFill = true; cd.SelectedFillKind = HeatMap`. Then `_parent.Notify()`.
  - This **replaces** both the `Fill` show-toggle and the separate TopoMap/HeatMap seg-btn pair. So the
    "Labels / Lines / Fill" row is: Labels (toggle), Lines (toggle), Fill (IconSelectButton with the 3 text
    options). Item template = a `TextBlock` of `Label` (FontSize 10), like the matrix-type ISB text template.
  - Keep `cd.FillType` derivation intact (it already reads `ShowFill`+`SelectedFillKind`), so the renderer
    contract is unchanged.

---

## 7. Card — narrow the wide text-edit boxes (Levels Start/Step/Stop + Constant Value)
The range `NumericUpDown`s (Start/Step/Stop) and the constraint Value `NumericUpDown` are too wide. Constrain
them and keep alignment:
- Give the three range fields a fixed width (e.g. `Width="48"` each) or switch the range grid to fixed columns
  (`ColumnDefinitions="48,Auto,48,Auto,48"` with the `:` separators) so they don't stretch full-width; left-
  align the group.
- Give the constraint Value field a fixed width (e.g. `Width="60"`) instead of `*`.
- Verify they still align with the card's other rows (the label column is 50px; keep the value group starting
  at the same x). Tune widths so nothing clips at the 430px card max-width.

---

## 8. File menu — Import Loadpull Data (Known Files)
Add to `WorkspaceWindow.axaml` File menu (near "Add Library…" / the open items):
```xml
<NativeMenuItem Header="Import Data…" Command="{Binding ImportDataCommand}"
                CommandParameter="{Binding $parent[Window]}"/>
```
Name it "Import Data…" (covers `.spl`/`.lpcwave`/`.npy`; "Import Loadpull Data…" is fine if you prefer the
loadpull emphasis). Add `ImportDataCommand` to `WorkspaceViewModel`:
- Show a file picker filtered to `.spl`, `.lpcwave`, `.npy` (+ maybe Touchstone for symmetry).
- Register each chosen file as a **Known File** via the SAME path drag-and-drop uses (find the DnD handler —
  `DropDiagnostics`/workspace known-file add — and call into it, or the workspace known-file store the 7.4g
  provider reads). Then refresh the Data Display source list (`RefreshAvailableDataSources`) so the file is
  immediately selectable.
- This is the missing non-DnD entry point. Reuse existing picker + known-file plumbing; don't invent a parallel
  store.
**Gate:** File ▸ Import Data… → pick a test `.spl` → it appears as a Known File and as a selectable Data
Display source → a contour trace can bind to it.

---

## 9. Slice plan (compile-and-test-gated)
- **7.4h-1a — model + engine epsilon** (§1, §3): `ContourData` fields + `.cdd` round-trip; `Fit` epsilon
  forward + cache test.
- **7.4h-1b — VM fields/handlers + fill selection + color-picker commands** (§2, §6 VM side).
- **7.4h-1c — card edits** (§4 hide identity row, §5 Options order, §6 Fill ISB + reorder, §7 widths).
- **7.4h-1d — File ▸ Import** (§8).

## 10. Constraints / gotchas
- Identity-row hide (§4) must not break standard traces — it's gated on `!IsContourTrace`, the inverse of the
  contour body's `IsContourTrace`. Verify a standard trace card is unchanged.
- Color swatches: reuse the Settings color-picker dialog exactly; default grid-point color **black**.
- Engine params re-fit (RebuildContour); display/colors redraw-only (Notify). Don't re-fit on a colour change.
- Keep `cd.FillType` getter as the renderer's single source of truth; the new Fill ISB just sets
  `ShowFill`+`SelectedFillKind`.
- TreatWarningsAsErrors: nullable props → locals; no unused privates; no `<`/`>` in XML doc comments.
- HIG: short labels, tooltips for full names, 10px, subtle. Keep the card within 430px.

## 11. Tests
- `ContourData` round-trip incl. new fields (`.cdd`).
- `Fit` epsilon: distinct cache entries + distinct surface values on the test `.spl`.
- VM: setting `SelectedContourFill` to None/Topography/Heatmap sets `ShowFill`/`SelectedFillKind` correctly;
  engine-param changes trigger a re-fit (Grid changes), display toggles do not.
- UI is owner-verified (visual) per gates.

## 12. Out of scope (→ 7.4h-2)
- Plot **titles** (the "P-3dB Pout (dBm)" composition), **axis-label** behavior (Smith/Polar none + not
  rendered; Rect "Real (Ω)"/"Imaginary (Ω)"), **impedance grid** on Rect contour, **autoscale to grid extent**
  on Rect, **MXP/MXE/grid-point drawing**, and the **matplotlib colormap** ramps. All in 7.4h-2.

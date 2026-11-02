# Brief — harmonicaRF H7: Edit Display, the trace picker, interchange, and the colour editor

**Read first:** `docs/design/harmonicarf.md` (**§5**, **§7.5–§7.9**, **§8**), then
`src/Harmonica/CLAUDE.md` and `src/Ui/CLAUDE.md`'s H4–H5 and **H6** entries. H0–H3 built the headless
engine; H4–H5 built the document, the four panels, the pool and the scheduler; H6 built the gesture,
the inverse solve and reachability shading.

**H7 is the phase where harmonicaRF stops being a fixed instrument and becomes an editable one.**
Almost none of it is new machinery — it is `.cdd`'s placeable-plot canvas, `.cdd`'s trace picker,
`.cdd`'s clipboard, circuitRF's colour dialog and the engine's `.gam` reader/writer, pointed at
harmonicaRF's own `DataSet`. **The work is wiring and boundaries, not invention. If you find yourself
writing a second implementation of something in the table below, stop and report.**

---

## 0. What already exists, and what genuinely does not

**Do not rebuild any of this.** If something below seems missing, it is a lookup you have not found —
ask before writing a second one.

| you need | it is here | notes |
|---|---|---|
| the published result | `HarmonicaDataSet.Build(ctx, point, terminations)` | a real `DataSet` of `DataCube`s, §5 |
| placeable plots, add / move / resize / delete | `PlotContainerViewModel` (`src/Ui/DataDisplay/ViewModels/`) | `.cdd`'s own canvas |
| undo/redo for that canvas | `src/Ui/DataDisplay/Models/UndoRedo.cs` | and `src/Ui/Commands/UndoRedoStack.cs` for the app-wide one |
| "plot any cube" trace specs | `CubeTraceSpecParser` (`src/Ui/DataDisplay/`) | what the trace picker parses |
| copy a plot to the clipboard | `PlotExporter.CopyPlotToClipboardAsync` | PDF, SVG, JSON, bitmap |
| `.gam` read / write | `GamReader` / `GamWriter` (`src/Engine/Loadpull/`) | multi-block, already validated |
| the colour dialog and the role-list editor | `Views/Dialogs/ColorPickerDialog`, `Views/Dialogs/SettingsView` | see §7.9.4's two inherited fixes |
| `.ccolor` read / write | `ColorThemeIo.Save/Load/SaveFile/LoadFile` (`src/Ui/Theming/`) | the interchange format already exists |
| the `Harmonica.*` role vocabulary | `ColorRole.All` (22 roles) + `ColorTheme.BuiltIn` both variants | H4–H5 |
| the stored appearance | `CharmAppearance` (`src/Harmonica/`) + `HarmonicaAppearanceBridge` (Ui) | role map as plain data; the bridge owns the mapping |
| the panel layout, as data, unlocked by a flag | `CharmLayout.Locked` + `CharmPanelPlacement` fractions | R-h45-1 built this *for* H7 |
| the Tools ▸ harmonicaRF entry | `WorkspaceWindow.axaml`, both hand-mirrored surfaces | D10 shipped it early at H4–H5 |
| the netlist text a testbench export needs | `HarmonicaContext.NetlistText` | already generated on every rebuild |
| the marker/termination state | `HarmonicaViewModel.Markers` / `.Terminations` | one list, both charts (R-h45-3) |

**Genuinely does not exist yet, and is this phase's work:** any harmonicaRF menu bar at all; Edit
Display mode; the trace picker over harmonicaRF's `DataSet`; `.gam` import/export from a harmonicaRF
document; draggable grid points; *Copy termination set* and *Export testbench*; the colour editor,
`.ccolor` import/export and reset; and the §7.5 input controls (bias, frequency, compression,
compute-charge, multiplicity, and the model's own declared parameters).

---

## 1. Scope

Five milestones, in this order. **M1 and M2 are each independently useful and each a legitimate
stopping point** — M1 makes the instrument configurable at all, and M2 makes every number it computes
plottable, which is the single biggest capability jump in the phase.

1. **The menu set and the §7.5 inputs** (§7.6, §7.5). harmonicaRF documents get their own menus, and
   the readout strip gains its *input* half. No Simulate menu, ever.
2. **The trace picker over the §5 `DataSet`** (§7.7). "Plots anything harmonicaRF solved" — the reason
   publishing a real `DataSet` was a requirement rather than a nicety.
3. **Edit Display** (§7.7). Unlock, add/move/resize/delete plots and readouts, text size and
   alignment, undo/redo. `CharmLayout.Locked` is the flag; the layout is already data.
4. **Interchange** (§7.8). `.gam` import/export, draggable grid points, plot clipboard, *Copy
   termination set*, *Export testbench*.
5. **The colour editor** (§7.9.4). Live preview, `.ccolor` import/export, reset-all and per-role
   revert, and the iso-line fade parameters.

---

## 2. M1 — the menus and the inputs

### R-h7-1 — harmonicaRF's menus are its OWN, and there is no Simulate menu

§7.6 lists File / Edit / Markers / Display / Grid / Help. **harmonicaRF is always simulating**, so a
Simulate menu would be a lie about what the tool does. Mirror the existing pattern: circuitRF's menu
bar is **hand-mirrored across two surfaces** (a macOS `NativeMenu` and an in-window `Menu`, both in
`WorkspaceWindow.axaml`) and the Tools entry already lives on both. **Anything you add must go on
both, or it exists on one platform only.**

### R-h7-2 — the Markers menu is the first thing that can create a marker

§4.2's S2…/L2… bands have been "added and removed from a menu (H7)" since H4. Until now
`HarmonicaViewModel.Markers` has only ever held S1 and L1. Adding a band must:
- create the `HarmonicaMarker` **and** set its `TerminationSet` entry, through the existing
  `SetMarkerImpedance` — two sources for "what is band 2 terminated in" drift the moment either is
  written without the other;
- **not be undoable by removing the marker and getting a different state back.** §4.2: an unmarked
  band is *the absence of a marker*, not a marker with a default value, and `TerminationSet.Remove`
  is what expresses that. Band 1 cannot be removed.

### R-h7-3 — every input is a VALUE change unless it is a structural one, and the difference is already written down

`CircuitModel.StructuralKey` decides. Bias, drive and terminations mutate in place; the DUT, the
embedding stack, `HarmonicaCount`, the frequency, `FftOverSample` and `ComputeCharge` rebuild.
**Changing K or the frequency from the strip must go through the rebuild path and must call
`ResetSchedule()`** — §6.8: the previous model's cost says nothing about this one's.

### R-h7-4 — the model's OWN parameters appear, and nothing is faked

§7.5: "every parameter the loaded model declares (so periphery / finger count appear when — and only
when — the model actually has them, rather than being faked)". For an SDD that is its equation
strings; for a native FET its declared parameter set; for an external model
`ExternalDeviceDescriptor`. A hardcoded list of plausible-looking parameters is worse than none.

### Gate for M1

- Every §7.6 menu exists on **both** menu surfaces, and neither carries a Simulate menu — asserted
  against the AXAML, the way this repo already asserts hand-mirrored menus.
- Adding an L2 marker from the menu creates the marker **and** marks the band; removing it leaves
  `TerminationSet.IsMarked` false, and band 1 refuses removal.
- Changing the frequency rebuilds the context exactly once and resets the ladder; changing the bias
  rebuilds it **zero** times. Counters, not clocks.
- A model whose parameter set differs (SDD vs. a native FET) produces a different input list, read
  from the model rather than from a table.

---

## 3. M2 — the trace picker over the `DataSet`

### R-h7-5 — the picker plots CUBES, through the existing parser

`CubeTraceSpecParser` and the existing trace card already do this for `.cdd`. harmonicaRF's job is to
hand them `HarmonicaDataSet.Build`'s output. **`Zs_conv` is the interesting one** — §4.5.3 publishes
the full source-side conversion matrix precisely so its off-diagonals are plottable, and nothing has
ever displayed them.

### R-h7-6 — the DataSet a picker sees must be the one the panels drew from

A picker that re-solves to populate itself would show a *different* operating point from the glyphs
beside it. The frame's own operating point is the power-sweep cursor's (R-h6-11) and the `DataSet`
must be built at that point. **If that means publishing the `DataSet` on the `HarmonicaFrame`, do
that** — it is the same thread-crossing rule H6 used for the inverse-solve outcome.

### R-h7-7 — a picked trace is part of the document

It persists in the `.charm` (§8) and survives a reload. Follow `DataDisplayConfig`'s
forward-compatible read rule: an unknown block is ignored, a missing block is a default.

### Gate for M2

- A trace picked over `Gamma_intr` renders the same numbers the glyphs are drawn at — compared, not
  asserted.
- `Zs_conv`'s off-diagonal `(i,k)` is pickable and plots something non-zero on a fixture where
  harmonic conversion genuinely occurs (a source lead — the §4.5.3(a) case).
- A `.charm` with picked traces round-trips; one written before this phase still opens.

---

## 4. M3 — Edit Display

### R-h7-8 — unlocking flips `CharmLayout.Locked`, and nothing else changes

R-h45-1 put the layout in fractions *for this milestone*: "H7 only has to flip `Locked` and start
writing to the same field." **If you find yourself replacing the layout mechanism, something has gone
wrong** — re-read `CharmLayout`'s own remarks before writing code.

### R-h7-9 — Edit Display reuses `.cdd`'s canvas, including its undo stack

§7.7: "This is the `.cdd` placeable-plot canvas (`PlotContainerViewModel`, `UndoRedo`) applied to
harmonicaRF's own `DataSet` — not a second implementation."

### R-h7-10 — a degenerate placement is still dropped on read

`CharmLayout` already refuses a zero-width panel on load. Edit mode must not be able to *create* one
that the next load silently discards — either clamp at drag time or refuse the drop, and say which
you chose.

### Gate for M3

- Unlock, move a panel, save, reload: it comes back where it was put, and `Locked` is false.
- One undo entry per gesture, not one per pointer move — the counter assertion this repo already
  uses for PCell drags (`GeneratedCellStore.CellsWrittenCount`'s pattern).
- A panel dragged to zero width cannot be committed.
- Re-locking restores §7.1's default placement, and an untouched document still writes **no** layout
  block (H4–H5 pinned that; do not break it).

---

## 5. M4 — interchange

### R-h7-11 — `.gam` in and out through the EXISTING reader and writer

`GamReader`/`GamWriter` are `src/Engine/Loadpull/` and already handle multi-block files. A grid
imported from `.gam` is an arbitrary scatter, which is exactly what `ContourGrid` was built for
(§6.4: "a scattered set of Γ points, not a lattice"). **Importing a grid invalidates the RBF
factorization** — the node set moved, which is `Rbf2D.Factored`'s own cache key.

### R-h7-12 — draggable grid points, and the ONE Γ sample that costs

§6.4: "Dragging one grid point invalidates exactly one Γ sample — ~8 solves ≈ 8 ms plus a re-fit.
Live." Reuse H6's gesture: the hit test is the same `MarkerToCanvas`/`CanvasToMarker` pair, and grid
points are **beneath** the glyphs in z-order, so they are the third pass of
`HarmonicaHitTest.Resolve`. **Do not re-solve the whole grid for one moved point** — measure and
report what a single-point invalidation actually costs against a full rebuild.

### R-h7-13 — *Export testbench* must produce a runnable `.cnl`, and that is the validation route

§7.8: "writes a runnable `.cnl` that reproduces the current harmonicaRF state through the ordinary
loadpull/HB path. This makes every harmonicaRF finding checkable by the reference engine — which is
also how we validate the tool (§9)." `HarmonicaContext.NetlistText` is the OPEN-port netlist; the
terminations are deliberately **not** in it (§6.2), so the export has to add them as a `Tuner` pair.

### Gate for M4

- **The exported testbench is run through `Cli`'s `hb` verb and its Pout/gain agree with the
  harmonicaRF frame** — this is the gate, and it is the whole point of R-h7-13. A tolerance in the
  same class as H0–H3's Tier 3 (6.7e-5 dB) is the target; state what you actually get.
- A `.gam` written and re-read reproduces the grid, including holes.
- Dragging one grid point costs ~one Γ sample's solves, measured, not ~61.
- *Copy termination set* produces text that pastes into a `.cnl` and parses.

---

## 6. M5 — the colour editor

### R-h7-14 — the two inherited fixes are not optional

§7.9.4 names them: the **hex-field key handling** (Return applies *and* sets `e.Handled = true`, or
the dialog's default button closes the window instead; Escape reverts; LostFocus applies;
`RRGGBBAA`, a 6-digit entry taken as opaque), and **`ColorView` must be given its Fluent theme** or it
instantiates blank. Both are already absorbed by `SettingsView`/`ColorPickerDialog`. Reuse rather than
re-derive.

### R-h7-15 — colours live in the `.charm`, and the divergence from circuitRF's Layer 3 is deliberate

§7.9.4: circuitRF records a theme *name* in the `.cws` and resolves it against workspace → user →
Assets. harmonicaRF **runs with no workspace open and ships standalone**, so a name-plus-search-path
scheme has nothing to resolve against. `CharmAppearance` already stores the resolved role map for
**both variants**; H7 gives it an editor.

### R-h7-16 — a colour change must not invalidate physics, and this is already gated

R-h45-11 holds by construction and has a test that proves a 20× theme swap leaves
`FactorizationCount` and the fit identity unchanged, **with a negative control proving those counters
can move**. Extend that test to the editor path rather than trusting it.

### Gate for M5

- Recolouring a role live re-renders and does **not** re-solve, re-fit or re-factorize — through the
  editor, not just through the property.
- `.ccolor` export → import into a second document reproduces every `Harmonica.*` role.
- Reset-all and per-role revert both land on `ColorTheme.BuiltIn`'s values, for both variants.
- `α_floor = 1` flattens the iso-line fade with no code change (Tier 0 of H4–H5 already asserts the
  ramp maths; this asserts the editor reaches it).

---

## 7. Standing constraints (violating any of these is a bug, not a style choice)

- **`src/Harmonica` references no Avalonia.** `tests/Firewall.Tests` enforces it. The colour EDITOR is
  Ui; the stored role map is `CharmAppearance` and stays plain data.
- **The UI thread never solves.** Everything goes through `SolvePool`; the view publishes. A grid-point
  drag is a solve like any other.
- **Tier A never degrades.** `FramePlan.IncludesTierA` is true on every rung.
- **harmonicaRF never fills contours** — owner ruling. Do not add a fill path, a setting, or a
  benchmark for one. (The reachable-region shading is a *region*, not an iso-line; that distinction is
  already recorded and is not a precedent for filling contours.)
- **Never `PlotRenderer.BuildTransforms` on a harmonicaRF Smith panel.** `GammaToCanvas` /
  `CanvasToGamma` for Γ, `MarkerToCanvas` / `CanvasToMarker` for anything drawn on the compressed
  radial scale — which is markers, glyphs, the reachable region, and now grid points if you draw them
  through the same path.
- **No new physics.** Every number H7 displays already exists in the §5 `DataSet`.
- **Do not touch `src/Engine`, `src/Core` or `src/RfCore`** — except to *call* `GamReader`/`GamWriter`,
  which are `src/Engine/Loadpull` and are used as-is. If you think you need to change one, stop and
  report.
- **`.charm` reads stay forward-compatible.** A file written by H4–H6 must open unchanged, and an
  untouched document must still re-serialise byte-for-byte.

---

## 8. Cost discipline

Any test at or above ~5 s carries `[Trait("Category","Benchmark")]`, lives in a non-parallel
collection (`HarmonicaBenchmarks` in `tests/Harmonica.Tests`, `HarmonicaUiBenchmarks` in
`tests/Ui.Tests`), takes a **best-of-N minimum** (not a mean, not a median — this repo has been bitten
three times), and every reported number is measured **alone**. The grid-point-drag measurement will
land there; the correctness tests must not.

**One trap H6 left behind that will bite here:** `HarmonicaViewModel.PublishFrame` now records the
frame's cost with the scheduler. **A test that also records a synthetic timing double-counts and the
ladder falls two rungs an iteration.** If a ladder assertion behaves oddly, that is the first thing to
check.

---

## 9. Gate command

```
dotnet test tests/Ui.Tests
dotnet test tests/Harmonica.Tests --no-build
dotnet test tests/Firewall.Tests --no-build
dotnet test tests/Engine.Tests --no-build          # M4 touches the CLI/testbench route
```

Baseline going in: Ui **5,184** · Harmonica **89** · Firewall **5** · Engine **1,004 + 1 skip**.

---

## 10. Report back

1. **The exported testbench's agreement with the frame** — the tolerance you actually measured
   through `Cli hb`, and what it disagrees about if it does.
2. **What one dragged grid point costs** against a full grid rebuild, measured.
3. **Whether `Zs_conv`'s off-diagonals show anything worth plotting** on a real fixture — §4.5.3 calls
   them "genuinely useful and rarely-visible", and this is the first phase that can look.
4. **Whether Edit Display needed anything `PlotContainerViewModel` does not already do.** If it did,
   say what, because §7.7's whole claim is that it does not.
5. **Anything the design note got wrong.** H0–H3 found a sign error in §4.5.3(a); H4–H5 found the
   viewport-margin defect and that a colour probe cannot separate an iso-line from chart chrome; H6
   found that §6.6's "30–40 ms" for a per-frame FD rebuild is 12.9 ms, and that the shipped default
   document cannot exercise an intrinsic drag at all. Say so plainly rather than working around it
   quietly.

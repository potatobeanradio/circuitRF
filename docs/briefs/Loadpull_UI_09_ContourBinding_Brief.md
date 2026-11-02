# Brief — Loadpull UI 09: bind the LoadpullSurface + contour card to a simulated LP `run.npy` (group-aware) + end-to-end gate

**Goal:** With recognition in place (brief 08), make a contour trace actually **build a `LoadpullSurface` and
render contours** from a simulated LP `run.npy` group — identical to a `.spl`/`.lpcwave` source. The only new
wrinkle is that the LP cubes live under a **group** (e.g. `LP1`) rather than at top level, so the surface
construction + the contour card's metric binding must be **group-aware**. Then verify end-to-end on the
user's LP run.

**Depends on:** brief 08 (`LoadpullRecognition.FindLoadpullViews` + the eligibility gate).
**Reads with:** `docs/design/loadpull-contours.md` §2 (the `LoadpullSurface` consumer), the existing contour
binding code (grep `LoadpullSurface` and `RebuildContour`), `ContourData.cs`, `src/Engine/Loadpull/
LoadpullEngine.cs` `BuildLoadpullDataSet` (the cube names/units the surface reads).

## What the surface reads (confirm; should need no change)

The 7.4b `LoadpullSurface` (RfCore) builds from a DataSet by **cube name**: the FOM (`Pout`/`Gt`/`Gp`/
`DE`/`PAE`), the power axis (`pinStep`), and the termination (`GammaLoad`/`ZLoad` over `gridPoint`). The LP
engine emits exactly those names/axes, and `Pout` is in **Watts** — same as the `.spl` path (the 7.4f
contract converts dBm→W to match the engine). So the surface's W→dBm conversion for the `Pout` contour
metric (`ContourData.MetricUnit("Pout") == "dBm"`, `LevelRange("Pout") == (-30,0.5,60)`) applies identically.
**Verify this** (a quick read of how `LoadpullSurface` reduces `Pout`): both producers feed Watts; the
surface owns the dBm display conversion. No engine change.

## 1 — Make the surface construction group-aware

Find where the contour trace builds its `LoadpullSurface` from the bound source's DataSet (grep
`new LoadpullSurface` / `LoadpullSurface(` and `RebuildContour` in `TraceRowViewModel`). Today it almost
certainly reads cubes from the **top level** of the source DataSet (correct for flat `.spl`/`.lpcwave`). For
a grouped LP `run.npy`, the cubes are under the `LP1` group, so a top-level read finds nothing.

Fix: resolve the **loadpull group** for the bound source via brief 08's `FindLoadpullViews`, then read the
loadpull cubes from that group. Two clean options — pick whichever fits the surface builder's current shape:
- **(preferred) Pass the group to the builder.** Give the `LoadpullSurface` construction the group name (or
  a group-scoped cube accessor) so it fetches `Pout`/`GammaLoad`/etc. from the right group. For `.spl` the
  group is null/top-level → unchanged behavior.
- **(alt) Project a group view.** Before constructing the surface, build a flat sub-DataSet containing just
  the located group's cubes (re-keyed to bare names) and hand that to the existing builder. Simplest if the
  builder is hard to thread a group through; costs a shallow cube re-reference (no data copy).

When a source has **multiple** loadpull views (e.g. a run with `LP1` and `LP2`, or an LPP follow-on
loadpull), each is independently bindable — surface which view a contour trace uses the same way the trace
picker already chooses an analysis group (group-qualified selection; grep the picker's group handling in
`RebuildSignals`). Default to the first loadpull view when the user hasn't chosen.

## 2 — Offer + bind the contour card on a recognized run source

- The contour trace **kind** must be offerable when the bound source is loadpull-eligible (brief 08's gate)
  — confirm the plot-inspector/trace-kind path now lets a `run.npy` LP source host a contour trace (it was
  gated on `SourceKind.Spl/.Lpcwave`; brief 08 widened the gate — verify the contour card actually appears).
- The contour card's **metric picker** (`Pout`/`Gain`/`Efficiency`/…) reads the available FOM cubes. For a
  grouped source it must look in the loadpull group (group-qualified, like the picker's `HB1.V` handling).
  Ensure the metric list populates from the LP group's cubes.
- `GammaLoad`/`ZLoad` presence drives the Smith (Γ) vs Rect (Z) substrate choice — the LP group has both;
  confirm the substrate toggle works as it does for `.spl`.

## 3 — End-to-end gate (the user's scenario)

1. Run the LP analysis that produced the `.npy` (the one the user reported). Its `run.npy` appears as a
   selectable source (already working) and is now **recognized as loadpull** (brief 08).
2. In a Data Display, add a **contour trace** bound to that source. The metric picker offers
   `Pout`/`Gain`/`Efficiency`; pick `Pout` @ constant 3 dB compression.
3. A `LoadpullSurface` builds from the `LP1` group's cubes; iso-lines/TopoMap render on the Smith chart with
   MXP/MXE overlays — **visually identical** to binding a `.spl` source of the same device. No
   origin-specific affordance; the viewer cannot tell simulated from measured.
4. Cross-check: a `.spl`/`.lpcwave` contour still renders unchanged (the flat path is untouched).
5. If feasible, a regression test: synthesize a grouped loadpull `run.npy`-shaped DataSet (or use a small
   LP run fixture), build the surface through the group-aware path, and assert the resampled `Pout` grid /
   MXP location matches the same data fed flat (parity between flat and grouped ingest).

## Notes / risks
- **Grouping is the whole risk.** If the surface builder or the metric picker assumes top-level cubes, the LP
  run silently yields an empty surface. The flat-vs-grouped parity test (step 5) guards this.
- **Units:** do not let a W↔dBm mismatch creep in. Both producers feed `Pout` in Watts; the surface converts.
  If the contour comes out ~30 dB off, that is a double-conversion bug — check the surface treats the cube as
  Watts for both paths.
- **No engine/model change**, no new on-disk format. This is Data Display wiring only (plus brief 08's
  recognizer). Firewall: `LoadpullSurface`/`ContourExtractor` stay headless; only the binding VMs change.

## Verify
1. `dotnet build` zero warnings; `dotnet test` green incl. the parity test.
2. Manual e2e (steps 1–4) renders a contour from the simulated LP run.
3. Firewall passes.

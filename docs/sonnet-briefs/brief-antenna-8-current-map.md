# Brief — ANT-8: the surface-current map, made usable

**Series:** `brief-antenna-0-overview.md` §1d. **Depends on:** nothing. **Blocks:** nothing.

**Independent of the rest of the series and can be taken at any time.** The physics is built and
shipping; everything here is presentation. It is the cheapest item in the series relative to how much
it looks like a feature.

---

## 1. What already exists

The whole path is built:

`PlanarCurrentDensity` (the engine-side reduction) → `PlanarCurrentDensityMap` →
`EmSetupEditorViewModel.AdoptCurrentDensity` / `WorkspaceViewModel` →
`LayoutEditorViewModel.PlanarCurrentDensity` → `LayoutRenderer.DrawPlanarMeshOverlay(cellScalar)`.

A run on the imported patch board printed:

> Surface current density |J|, 0 … 1.077 A/m (normalised to this map's own peak), port 1 driven at
> 1 V, 1.45 GHz. One port at a time and one frequency: a map that superposed every excitation would be
> a map of nothing.

Per-cell |J| in A/m, with `Jx` and `Jy` kept **complex** per cell and `Iz` for via current, indexed by
the mesh's own cell order, drawn in plan view over the artwork. The reduction is documented once, in
the engine, next to the basis it reduces — `PlanarCurrentDensity`'s header states why that is the
right place and what happens if a renderer-side approximation becomes a second definition of the same
physical quantity.

**Nothing in this brief may move that reduction, or add a second one.**

## 2. M1 — a frequency picker

Today a run produces one map. For an antenna the map at resonance and the map 5 % away are different
pictures and the difference is the whole point — it is how a user sees which edges are radiating and
whether the mode is the one they intended.

- Keep the map for **solved** frequencies only. The adaptive sweep models the rest
  (`PlanarAdaptiveSweep`), and an interpolated current map is not a current map — it is an
  interpolation of a display reduction, which is two removes from anything physical. **Refuse the
  unsolved points by name**, saying which frequencies do have a map.
- Memory is small: N complex per port per solved point. On the measured board that is ~4,000 × 16
  bytes × ~10 solved points ≈ 640 kB. Measure it on a large mesh and report the number rather than
  asserting it is small.
- The picker belongs beside the map, not in the sweep settings — it is a view choice, not a run
  choice, and changing it must not invalidate the mesh or the result.

## 3. M2 — a dB colour scale

Linear |J| on a resonant patch washes out: the feed and the patch rim are orders of magnitude apart
and a linear ramp shows one of them.

- A dB scale with a **floor**, defaulted and controllable, exactly as ANT-7's polar mode needs one.
- **The scale caption already exists and is already the engine's own words** — `CurrentDensityScale`
  reads `PlanarCurrentDensityMap.ScaleCaption`, and R-res-8 requires the normalisation be shown rather
  than implied. The dB mode extends that caption; it does not replace it and the panel still computes
  nothing.
- Linear stays available and stays the default, so every existing screenshot and every recorded
  observation still reproduces.

## 4. M3 — phase, which is nearly free

`Jx` and `Jy` are already complex per cell and nothing downstream uses that yet.

- **An animated real part**, Re{J·e^{jωt}} over one period, shows the standing wave directly — where
  the current nulls sit, and therefore where the charge and the radiating edges are.
- **Or a vector-arrow mode** at a chosen phase, which shows the same thing statically and prints
  better.

Either is a legitimate v1; both is better. **The arrows must come from `Jx`/`Jy`, not from a gradient
of |J|** — a magnitude map has no direction and inferring one is inventing data.

**A caution about the animated mode**: `src/Ui/CLAUDE.md`'s own crawl finding is that a slow layout
frame starves the whole window — there is one compositor and nothing to decouple. An animation over a
500k-shape board must therefore not re-walk the layout each frame. Draw it as an overlay pass over a
cached layout raster, and **measure a frame** on a large board before shipping it, reporting the
number rather than asserting it is fine.

## 5. Gates

- **The existing exactness identity still holds.** `PlanarCurrentDensity`'s header states it: summing
  `J_x · (transverse extent)` across one transverse column gives exactly the **mean** of the currents
  crossing that column's two bounding edges — which is what `PlanarExcitation.LineCurrent` reports,
  and which Tier 4 already pins to machine precision. **Nothing here may disturb that test.**
- The map is still one port and one frequency, enforced by the signature — the picker chooses among
  solved maps, it does not combine them.
- An unsolved frequency **refuses by name** and lists the solved ones.
- The dB mode's caption states its floor and its reference; linear is unchanged and still the default.
- Arrows come from `Jx`/`Jy`; assert it by handing the renderer a map with uniform |J| and a rotating
  phase and checking the arrows rotate.
- The overlay is still overlay: **never layer geometry, never counted in `LayoutFrameCounters`, never
  reachable by an exporter, defaulting to false** — `R-em-15`'s contract, which
  `LayoutRenderer.PlanarMesh.cs` copies exactly and says why.
- A frame-cost measurement on a large board, **reported, not gated** (no new timing test — the repo's
  standing rule; assert the structural property instead, e.g. that the overlay pass does not walk
  layout shapes).
- Memory measurement reported.
- `RESOLVED.md` write-up in `src/Ui` (and `src/Render` if the overlay changes).

## 6. Must NOT

- **Do not move or duplicate the reduction.** `PlanarCurrentDensity` is where |J| is defined, once.
- **Do not superpose ports**, and do not offer a "total" map. The header explains that it would be a
  map of nothing.
- **Do not interpolate a map** across the sweep.
- **Do not derive direction from |J|.**
- **Do not make the dB scale the default**, or existing observations stop reproducing.
- **Do not let the overlay reach an exporter.**

## 7. Reading order

`src/Engine/Mom/PlanarCurrentDensity.cs` — the header, end to end; it is the specification for what
this map means and what it does not · `src/Render/Renderers/LayoutRenderer.PlanarMesh.cs` (the overlay
contract, and the `cellScalar` provision L8b left for exactly this) ·
`src/Ui/Layout/Em/EmSetupEditorViewModel.cs` (`AdoptCurrentDensity`, `CurrentDensityScale`) ·
`src/Ui/CLAUDE.md` on the single compositor · `src/Engine/Mom/PlanarAdaptiveSweep.cs` (which points
are solved and which are modelled).

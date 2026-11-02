# Brief PM-3 — Planar mesh: "Mesh as Merged Shapes" as a setting, why merging turns the test geometry's output reflections, and whether it pays per unknown

**Series:** planar mesh (PM), 2026-10-29. · **Tag:** `R-pm3-<n>` · **Follows:** PM-2 Part 2 (built, flag off)
**Area:** `src/Engine/Mom/PlanarRooftopMerge.cs` (the classification, `LocalSizeField`, `SpreadFactor`),
`SurfaceMesher.BuildRooftopMerge`; for Part 3, `src/Design/Layout/Em/` (`EmSetupModel`, `EmSetupPersistence`,
`EmRunService`, `EmSnpProvenance`), `src/Ui/Layout/Em/EmSetupEditorViewModel.cs`,
`src/Ui/Views/Layout/EmSetupEditorView.axaml` and `src/Render/Renderers/LayoutRenderer.PlanarMesh.cs`. Findings go
in `src/Engine/Mom/RESOLVED.md` (engine) and `src/Ui/Layout/Em/RESOLVED.md` (panel). Read §PM-2 Part 1 and §PM-2
Part 2 in the first, then `src/Engine/Mom/CLAUDE.md` §3.2 and `src/Ui/Layout/Em/CLAUDE.md`.

**Part 1 is a diagnosis and Part 2 a measurement; neither changes a default. Part 3 is the user-facing setting,
"Mesh as Merged Shapes", ON by default in the product — the owner's decision, independent of what Parts 1 and 2
find, so it can land first.**

---

## 0. Why

PM-2 built rooftop merging (`PlanarMeshSettings.RooftopMerging`, off by default). The construction is gated exact:
with nothing merged it is `Fill` to the bit, and with a merge it is `Tᵀ Z T` to 1.4e-16. Two gates disagree:

- **Patch Antenna example — passes.** |ΔS11| 2.4e-4 against the elemental mesh, and its 20 → 40 cells/λ doubling is
  0.0200 against the elemental mesh's 0.0201. The merges are the notch-corner fan columns running up the patch and
  the notch-bottom fan rows running across it, which is exactly what PM-2 was written for.
- **Internal test geometry (built outside the repo; the owner has its location), transmission-line model —
  fails.** S11, S21 and S31 are fine, but both output ports' reflections, S22 and S33 (|S| ≈ 0.9), turn by 4–7°.
  The elemental mesh moves under 1° from 20 to 30 cells/λ, so this is the merge's error and not the mesh's
  uncertainty. On this board nearly all the merging is two thin fan-row strips running the length of each output
  branch (y-rooftops merged along y), plus short patches at the bends and the junction.

Two open questions, and the owner's reason for asking both:

1. **Why.** The classification said every merged cell was at least 4× finer than needed, and on this board that is
   wrong about something. Is it the strips, the junction, or neither alone? And what property of the current does
   the merge take away?
2. **Is "same grid" even the right comparison?** The merged basis is a subset of the elemental one on the same
   grid, so on that grid it can at best match it. The comparison that could favour merging is **per unknown**:
   the merged mesh at N unknowns against an elemental mesh coarsened to about the same N, both measured against a
   refined reference. On some geometries merging may well be the more accurate way to spend N. Nobody has
   measured that.

A 5° turn is about 0.3 % in β over a branch some 3 λ_g long — small physically, but it shows the classification
can be wrong, and that matters more than the size.

## 1. Part 1 — find what the merge removes (R-pm3-1)

Scratch harness only; never the Benchmark tier. Every run in Release, under a memory and wall-clock watchdog. One
test-geometry point costs about 30 s dense at 8–11k unknowns. **Ask the owner before any run over five minutes.**

1. **Ablate by region.** Merge only the two branch strips; then only the bends and the junction; then everything
   except the strips. Report S22/S33 (magnitude and phase) at 55/60/65 GHz against the elemental 20 cells/λ result
   for each. A region mask in the scratch harness is enough; do not add a setting for it.
2. **Reproduce it on something small.** If the strips carry it, build a standalone straight line in the engine
   (`PlanarLineFixtures`) with the same thin fan rows forced into it (a hard gridline pair at the strips' y), and
   compare γ and Z_c merged against unmerged. If a line shows it, the cause can be read off in seconds rather than
   minutes.
3. **Name the property.** Candidates to test, not conclusions: the transverse charge distribution the thin rows
   resolve near the strip edge; the localised field reading a fan as spare because its feature is far away (the
   rows were raised at the junction), when the strip's own edge distribution is what they are really resolving; or
   the gap between the two branches, where neither branch's edge is the attractor that raised the row. Whichever it
   is, say which classification rule would have kept those cells elemental, and what that rule costs on the patch.

**Stop or go:** go to a classification change only if one rule closes the test geometry to within its own 20 → 30
cells/λ movement (≤ 0.015 in S22/S33) and leaves the patch inside PM-2's gate 1. Otherwise record what was found and
stop.

## 2. Part 2 — does merging pay per unknown? (R-pm3-2)

For each geometry: one **reference** (elemental, refined until S stops moving by more than a tenth of what is being
compared, i.e. by doubling cells/λ or the cells across), then at matched unknown counts N within ±5 %:

- **merged** at the shipped settings;
- **elemental, coarsened** to the same N — by lowering cells/λ first, and by lowering cells across second, reported
  separately (they coarsen different things);
- both against the reference: worst |ΔS| over the band, plus wall clock and peak memory.

Geometries, all single-level, since merging applies only there: the Patch Antenna example; the internal test
geometry; `testdata/portcal/separated-pair` (accelerated off); the shipped spiral examples (`--trust-kit`); and
**two or three drawn here** for shapes the set lacks: a branch-line or rat-race coupler, an edge-coupled bandpass
filter, a stepped-impedance lowpass. Draw them with the authoring verbs and keep them outside `testdata/` unless a
test needs them.

**The deliverable is one table:** per geometry, error against the reference for merged and for coarsened-elemental
at the same N. A geometry where merged wins is the evidence the owner asked for; one where it loses says where the
classification fails.

## 3. Part 3 — "Mesh as Merged Shapes" in the EM Setup panel, on by default (R-pm3-3)

The owner wants PM-2's merge reachable as an ordinary setting for the planar solver, ON by default, whatever Parts 1
and 2 conclude: it is a choice a user can reasonably make, and the panel should offer it. Today it exists only as
`PlanarMeshSettings.RooftopMerging` and a hand-written `.cem` key, with no control.

**Know what default-on ships.** On the patch example it is free (|ΔS11| 2.4e-4). On the internal test geometry it
turns two output reflections by 4–7° (PM-2 §5). So default-on publishes that error on artwork like that board until
Part 1 finds the cause. That trade is the owner's decision and is not to be relitigated; state it in the user page
(item 6) without dressing it up.

1. **Engine default stays OFF; the product default is ON.** This follows `EdgePortReturn`, which is `SeriesGap` in
   the engine and `Ground` in the product: `PlanarMeshSettings.RooftopMerging` keeps `false`, so every engine test and
   every number in `HISTORY.md`/`RESOLVED.md` stays reproducible, and the product default lives in exactly one
   place on the Design side (an `EmSetup`-level default constant, or the setup's default `PlanarMesh`, whichever
   keeps one spelling). `EmRunService` carries the value into the mesh settings the run uses, in the same place
   for preflight, `check` and `em` alike, so the panel's count, `circuitrf check` and the run agree (P12's lesson).
2. **`.cem`:** nullable, **omitted at the PRODUCT default (on)**, and a file written before this phase gains no
   byte and reads back ON — `AdaptiveSampling`'s polarity, recorded in `src/Ui/Layout/Em/CLAUDE.md`. An explicit
   `false` survives a round trip and `Clone` (undo snapshots). Replace PM-2's current spelling (omitted at off)
   rather than adding a second key; nothing outside this repo has written it.
3. **Provenance:** `EmSnpProvenance.MeshHash` already adds `|merge=True` only when on. With the product default on,
   every single-level dense planar `.snp` stamped before this reads as stale once. That is correct, because the
   result describes a different solve; say so in the Ui `RESOLVED.md` entry.
4. **The control:** a checkbox labelled **"Mesh as Merged Shapes"** in the Surface-mesh group, visible on the
   planar kernel only (`IsPlanarAnalysis`), with a user-facing tooltip saying what it does and what it costs, in
   one or two plain sentences. It commits one undo entry and calls `InvalidateMesh()`, because the unknown count the
   panel shows changes. It does NOT clear `Auto`: it is not a resolution, the same reasoning as `BoundaryCells`.
   **Where it cannot apply** (accelerated solve on, or a multi-level/via layout), the checkbox stays in place
   **greyed** with its tooltip saying why, never hidden, so nothing in the row shifts (owner rule: UI controls never
   shift). The mesher already writes the matching "not applied" note.
5. **The mesh overlay shows what is solved.** With the setting on, `LayoutRenderer.PlanarMesh` draws an internal
   edge only where it is still the face of its own unknown, so the picture matches the count in the panel. PM-2's
   scratch pictures did exactly this: an edge is a face when some macro-rooftop has weight 1 on the elemental
   rooftop across it. Expose that from `PlanarRooftopMerge` as one public query; the renderer must not re-derive the
   merge. Off, or where nothing merged, the overlay is today's, pixel for pixel.
6. **Words:** the planar page in `docs/user/src/reference/mom-engine.md` gains the setting — what it does, that it is
   on by default, and the measured trade (both numbers above). Edit the source page only; the owner regenerates
   `docs/user` at the end of a series. `circuitrf reference em-setup` is generated from the reader's own type, so
   it needs nothing beyond the property's doc comment.
7. **Tests that move:** anything that runs a single-level dense planar setup through `EmRunService` with a pinned
   count or a pinned answer now sees the merge. For each one, either update it with the counted change, or pin the
   setting off where the test is about something else, and list which was done in the Ui `RESOLVED.md`. Engine-side
   tests use `PlanarMeshSettings` directly and do not move. Add only what the claims need: the `.cem` round trip
   (old file reads on, explicit off survives), the hash both ways, one undo entry plus invalidate without clearing
   Auto, greyed-not-hidden on the accelerated solve, and the overlay drawing fewer edges when on.

## 4. Gates

1. **Engine off is off:** the ENGINE default and every engine-side number stay as they are, and
   `RooftopMergeTests` stays green.
2. **Any classification change from Part 1:** the patch stays inside PM-2's gate 1 (|ΔS11| ≤ 1×10⁻³; doubling no
   worse than 0.0201), and the test geometry stays within its own 20 → 30 cells/λ movement.
3. **Counted:** every geometry in Part 2 reports N (elemental and merged), wall clock and peak memory in Release.
4. **Part 3:** a pre-phase `.cem` gains no byte and reads ON; `circuitrf check`, the panel's mesh report and an
   `em` run quote the same unknown count for the same setup; the overlay's edge count with the setting on equals
   what the merge says is solved; with it off, the overlay is unchanged.

## 5. Not in this brief

- Turning the ENGINE default on; that waits on Parts 1 and 2 and the owner.
- Multi-level, via and accelerated meshes (PM-2 excluded them on purpose).
- The understated memory estimate for a merged run (`PlanarSystem.ResidentBytes` at the merged N) — recorded in
  PM-2 §6; fix it only if this brief moves the flag closer to shipping.

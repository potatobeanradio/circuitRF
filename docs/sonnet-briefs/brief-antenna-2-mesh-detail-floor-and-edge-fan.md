# Brief — ANT-2: the detail floor, and an edge fan that is not globally coupled

**Series:** `brief-antenna-0-overview.md` §1b. **Depends on:** nothing (independent of ANT-1).
**Blocks:** ANT-3, and in practice everything, because a refused mesh has no results to post-process.

Two changes to `SurfaceMesher`, both of which fix **every imported board** and neither of which is
antenna-specific. The antenna-specific one is ANT-3.

---

## Testing rule for this brief

**Mesher tests only.** `SurfaceMesher.Mesh` on a hand-built `PlanarProblem` is milliseconds; a
de-embedded planar point is tens of seconds. No EM solve, no de-embedding, no full `dotnet test`.
`dotnet test tests/Engine.Tests --filter "FullyQualifiedName~<TestClass>"` and nothing wider. This
is the same rule `brief-em-transmission-line-mesh.md` set for the same code, and for the same reason.

Where an accuracy question genuinely needs solved s-parameters — §3's convergence check is the only
one — take it **once, in a scratch harness, and report the number**. It does not become a test.

---

## 1. What was measured

Owner-supplied imported patch board (`brief-antenna-0-overview.md` §1b), one frequency point:

| configuration | unknowns |
|---|---|
| as imported, default mesh (Auto, 20/λ, 4 across, edge on) | 704,482 — refused |
| **connector via-lands deleted**, default mesh | **51,031** — refused |
| connector via-lands deleted, TL on, 4 across, **edge on** | 12,596 — refused |
| connector via-lands deleted, TL on, 4 across, **edge off** | **4,854** — solves |

Two numbers carry this brief:

- **14×** — what deleting the connector's via lands and aperture-rounded corners alone was worth
  (704,482 → 51,031). Those features are ~310 µm, which is λ_g/830 at 1.74 GHz, and they sit ~9 mm
  from anything electrically interesting.
- **61 %** — what the edge fan costs on a patch (12,596 → 4,854). The engine already has this
  finding at the same magnitude from a different board: `SurfaceMesher`'s `LocalConductorWidth` doc
  comment records a connector cutout where "the edge fans WERE the mesh — 513 cells, of which 442
  vanished when the edge mesh was switched off" (2026-09-09).

## 2. Why the existing attractor fix does not reach this

`SurfaceMesher.LocalConductorWidth` gave each attractor its own c₀ — 3 % of the metal run measured
perpendicular to *that* edge, rather than 3 % of the global narrowest conductor. That was the right
fix and it stays. Its own doc comment names the two things it deliberately did not change:

- **"The grading rate g is unchanged — it is still derived from the global narrowest conductor."**
  So the fan's *length* is still globally coupled: `log_g(bulk / c₀)` cells. On the measured board the
  feed rim needs a ~10 µm finest cell and the bulk pitch is ~2.4 mm, which is about **eight** graded
  steps.
- **"It does not, and cannot, stop a fan crossing the part."** A tensor product means an x-attractor
  refines a **column over the full grid height**. So those eight steps become eight grid lines running
  the entire 41 mm width of the patch, and eight more running its 49 mm height.

That is the whole of the 61 %. Localising a fan in the other axis needs T-junctions, which the
rooftop basis and `RectangleIntegrals`' closed forms do not admit — so **the fix is not to localise
the fan, it is to make it short.**

## 3. M1 — the detail floor

**A local width below a λ-relative threshold does not get to drive the pitch.**

Today `MeasureNarrowness` reports the narrowest run anywhere and that sets `hx`/`hy` globally; with
the pitch field on, the field's local width does the same thing locally. Neither has any notion of a
feature being too small to matter electrically.

- The floor is **λ-relative, not absolute** — an absolute number in µm is a different decision on a
  1.7 GHz board and a 40 GHz one, and would be wrong on one of them. Derive it from the same λ_g the
  cell-size cap uses, at the same `MeshFrequencyHz`.
- It **floors, it never refines**. Same invariant `LocalConductorWidth` already states: "every c₀ᵢ is
  floored at the global c₀, so it can only COARSEN". A detail floor can only coarsen too, so the cell
  count is bounded above by today's and no configuration gets worse.
- **The threshold is a control, defaulted, and reported.** The default is the decision to justify with
  a measurement; λ_g/200 and λ_g/500 are the two obvious candidates and the convergence check below
  is what picks between them. Whatever it is, the report must name the number, how many features fell
  below it, and what the pitch would have been without it — a floor that silently coarsens a mesh is
  the same failure mode as a control that silently does nothing.
- **It must survive `Auto`**, on the settled taxonomy: `Auto` means *choose the resolution for me*,
  and "which geometry is electrically real" is not a resolution. Same reasoning that carried
  `BoundaryCells`, `MeshFrequencyHz` and `TransmissionLineMesh` through `Resolved`.

**The convergence check (scratch harness, reported, not a test):** on the measured board with ANT-1
applied, sweep the floor over off / λ_g/1000 / λ_g/500 / λ_g/200 / λ_g/100 and report cell count and
de-embedded S11 at resonance for each. The number to defend is the coarsest floor at which S11 has
not moved. If S11 moves at every rung, say so — that is a real result and it means the connector
detail is electrically live, which would be a surprise worth recording.

## 4. M2 — a fan that is short because its grading rate is local

**Derive the growth ratio from the attractor's own c₀ and the local bulk pitch, not from the global
narrowest conductor.**

The fan's length is `log_g(bulk / c₀)`. `c₀` is already local after `LocalConductorWidth`. Making `g`
local completes it: a rim whose own c₀ is close to the local bulk pitch grades in one or two cells
instead of eight, and no fan is lengthened by a narrow feature somewhere else on the board.

Two secondary levers, in preference order:

1. **A λ-relative floor on the finest edge cell**, composing with M1's. 3 % of the local metal run is
   right in ratio and wrong in absolute terms when the run is a 349 µm feed beside a 41 mm radiator.
2. **A cap on fan length in cells**, as a backstop. Blunter; only if 1 does not close it.

**The design note's 2-5 % and "~1.5-2 growth" stay** — this changes what those are computed *from*,
not the numbers themselves.

**Target, measured on the same board:** the row that reads 12,596 today (TL on, 4 across, edge **on**)
should come in near the 4,854 that edge-off gives, while the edge fan is still there. That is the
whole point — on an antenna the radiating-edge refinement is the part that sets the resonant
frequency, so this must be reached with the edge mesh on, not by turning it off.

## 5. Gates

All mesher-only.

- **Translation invariance is a hard gate.** Moving the artwork 3.7 mm must not change the mesh —
  L8b's own knife edge, recorded in `SurfaceMesher`'s grading comment, and both changes here touch a
  size field.
- **Monotonicity**: raising the detail floor never raises the cell count; lowering the growth rate's
  coupling never raises it either. Both changes can only coarsen, so this is an invariant, not a
  hope.
- **Bit-identity where nothing should change**: a board whose narrowest metal is already above the
  detail floor, with a uniform rim, meshes bit-identically to today. Set `Auto: false` in any fixture
  that varies the edge mesh — `PlanarMeshSettings.Default with { EdgeMesh = false }` is **inert**,
  because `Default` has `Auto = true` and `Resolved` collapses it. That trap is recorded in
  `brief-em-transmission-line-mesh.md` §0a and has already cost this area once.
- The bounded grading ratio still holds and `MaxCellAspect` still binds.
- **`EmSnpProvenance.MeshHash` includes the detail floor** — it changes the mesh, therefore the answer.
- The report names the floor, the features below it, and the fan length.
- The two scratch-harness measurements (§3's convergence table, §4's cell count on the measured board)
  are **reported, not gated**.
- `RESOLVED.md` write-up in `src/Engine/Mom`. `CLAUDE.md` gains only the new default and the new
  control, if the default moves.

## 6. Must NOT

- **Do not break the tensor product.** A fan that stops crossing the part is a different mesher with
  a different fill; it is explicitly out of scope here and `LocalConductorWidth` says why.
- **Do not touch `BoundaryMesher.PartitionFractions`** — that is kernel A's, and every kernel-A
  number sits on it.
- **Do not simplify the polygons.** Removing an aperture-rounded corner changes *what is drawn*, which
  is a modelling decision and the user's. This brief changes only what the mesher lets that corner
  ask for. (Geometry simplification as a user-visible import option is a legitimate separate idea and
  is deliberately not here.)
- **Do not make the detail floor absolute.** See §3.
- **Do not flip a default on the strength of this brief alone** beyond the new control's own default —
  every number in `HISTORY.md` must stay reproducible, and the `PlanarBoundaryCells` precedent is that
  moving a default is a separate deliberate act.
- **Do not let a mesh setting reach a physics refusal.** `PlanarProblem.MaxFrequencyHz` still means
  the sweep's top; `CanSolve` still sees only a `PlanarProblem`.

## 7. A defect to fix while here

**The refusal text is stale when `TransmissionLineMesh` is on.** On the measured board it said:

> …the narrowest conductor run is 310.102 µm, and meshing it 4 cells across forces a 77.526 µm pitch
> over all 55610 µm × 49400 µm of the artwork … LOWERING CELLS PER WAVELENGTH OR MESH FREQUENCY WILL
> NOT REDUCE THIS COUNT.

while the field it had just built spanned **78 µm to 3.485 mm**, and lowering cells/λ is exactly what
took the same board from 23,416 unknowns to 1,909. The sentence is correct for the per-axis rule and
wrong for the field, and it sends a user in the opposite direction from the one that works. It must
branch on which pitch rule actually produced the mesh.

## 8. Reading order

`src/Engine/Mom/SurfaceMesher.cs` — the `LocalConductorWidth` doc comment (~lines 40-80, which
already contains this brief's diagnosis), `MeasureNarrowness`, `EdgeReferenceLength`,
`PartitionGraded`, `BuildGridLines`, and the refusal/notes block around lines 660-760 ·
`src/Engine/Mom/PlanarMeshPitchField.cs` (the field's own floor and its Lipschitz smoothing) ·
`src/Engine/Mom/PlanarMeshSettings.cs` (what survives `Auto`, and why) ·
`brief-em-transmission-line-mesh.md` §0a-0c (the measurements this one continues from) ·
`src/Engine/Mom/HISTORY.md` §L8b (the graded-mesh knife edge).

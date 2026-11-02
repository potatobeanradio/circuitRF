# Brief — the transmission-line mesh: a directed pitch that follows the metal

**Owner request, 2026-09-09.** Two asks, plus one defect found while measuring them. The measurements
below were taken before this brief was written; they change what the first ask should be.

---

## Testing rule for this whole brief

**Every gate here is a MESHER test.** No EM solve, no de-embedding, no full `dotnet test`, no UI
test run. `SurfaceMesher.Mesh` on a hand-built `PlanarProblem` is milliseconds; a de-embedded point
is tens of seconds and a full suite is minutes, and none of the properties this brief is about need
either. Run `dotnet test tests/Engine.Tests --filter "FullyQualifiedName~<TestClass>"` and nothing
wider.

Where a question genuinely needs solved s-parameters — M0's accuracy question is the only one — take
the measurement **once, in a scratch harness, and report the number**. It does not become a test.
(Same reasoning the repo already applies to timing: assert the structural property, not the
expensive observable.)

The two tests already added for §0a follow this rule:
`MeshFrequencyTests.CellsAcrossTheConductor_SetsTheTransversePitch_AndIsNeverClampedOrRefused` is
mesher-only, and `MeshFrequencyUiTests.MinCellsAcross_RoundTrips_…` is persistence/hash only. Both
are milliseconds.

---

## 0. What was measured first, because it changes the ask

### 0a. Cells across the conductor — SHIPPED as a control, 2026-09-09

The ask was to relax the geometry limit from `narrowest/4` to `narrowest/1`. It is now a setting
(`PlanarMeshSettings.MinCellsAcrossConductor`, default 4, `.cem` + panel + provenance hash), 1 is
reachable, and the mesher warns rather than clamping or refusing. Owner's decision: mesh density is
the user's responsibility, warnings may be given and may be ignored.

**Measured before shipping, on a 200 µm × 10 mm FR-4 trace at cells/λ = 20, de-embedded:**

| min cells across | cells | N | S21 @ 10 GHz |
|---|---|---|---|
| 8 | 220 | 409 | −2.1781 dB |
| **4 (default)** | **180** | **331** | **−2.1777 dB** |
| 3 | 160 | 292 | −2.1780 dB |
| 2 | 180 | 331 | −2.1778 dB |
| 1 | 200 | 370 | −2.1780 dB |

**Two findings, both of which shaped how it REPORTS rather than whether it ships.**

1. **Accuracy is not what 4 was buying** — S21 moves under 0.001 dB from 1 to 8 across.
2. **The cell count is NOT monotonic in it today** — and **that is a DEFECT elsewhere, not a
   property of this control.**

**The cause was mis-diagnosed once and must not be mis-diagnosed again.** The first write-up of this
brief blamed the edge fan legitimately spending back what the bulk saved. That was challenged, and
re-measuring per axis on the plainest possible fixture — one rectangle — refuted it. The y-steps
across the metal at 1 cell across read `5.94, 17.87, 53.61, 87.13, 5.94, 5.94, 5.94, 5.94, 5.94,
5.94`: the fan grades correctly away from the near edge and then **collapses into six uniform cells
at the finest size** approaching the far edge. That is §0d's marcher bug, and coarsening the bulk
pitch makes the collapsed run longer, which is the entire non-monotonicity.

**With §0d's marcher, the same ladder is monotonic and cheaper at every rung** (same fixture, cells):

| across | 8 | 6 | 4 | 3 | 2 | 1 |
|---|---|---|---|---|---|---|
| today | 220 | 200 | **180** | 160 | **180** | **200** |
| with the M0 marcher | 264 | 220 | 198 | 176 | 154 | **154** |

So the expectation that a straight line coarsens monotonically as cells-across falls **is correct**,
and the mesher will meet it once M0 lands. Until then the report says the count may not fall, names
the real cause, and points at the edge mesh — which removes the fan entirely.

**A fixture trap found while re-measuring, worth knowing before trusting any table here:**
`PlanarMeshSettings.Default with { EdgeMesh = false }` is **inert**, because `Default` has
`Auto = true` and `Resolved` collapses Auto to the default `EdgeMesh`/`EdgeCells`. Set `Auto: false`
in any fixture that varies the edge mesh. (`MinCellsAcrossConductor` deliberately survives Auto, so
it does take effect either way.) The `.cem`-driven tables in §0b are unaffected — a real `.cem`
carries `Auto: false`.

**What is still open and is M1's:** whether the DEFAULT should move from 4. 3 is the cheapest rung
on both fixtures and is inside the design note's own "3–5 cells across any conductor width", but
changing a default moves every recorded number and is a separate deliberate act.

### 0b. The edge mesh is the only live coarsening control on geometry-bound artwork

Measured on the owner's reported `.cem` (a 6.07 × 1.96 mm connector cutout, 360 µm narrowest run,
one polygon, conformal):

| setting | cells | unknowns | verdict |
|---|---|---|---|
| as set | 1,997 | 3,835 | Warn |
| **edge mesh off** | **1,171** | **2,215** | **Ok** |
| staircase instead of conformal | 1,988 | 3,838 | Warn |
| edge mesh off + staircase | 1,161 | 2,214 | Ok |

Already surfaced: `SurfaceMesher`'s non-binding λ note names it (shipped 2026-09-09).

### 0c. **The mesh is ALREADY anisotropic** — the second ask is half built

`hx` and `hy` are derived per axis from the narrowest run along that axis, so a long thin line
already gets long thin cells. Measured, FR-4, cells/λ = 20 at 10 GHz:

| artwork | bulk dx | median dy | aspect |
|---|---|---|---|
| 2.9 mm × 20 mm hero | 357 µm | 348 µm | 1.03 : 1 |
| **200 µm × 50 mm trace** | **715 µm** | **11.8 µm** | **60 : 1** |

So "rectangular instead of square cells" is not a new capability. What is missing is (i) a way to ask
for MORE aspect than the geometry implies, and (ii) any awareness of DIRECTION — which is the real
content of the request and is §2 below.

### 0d. A real defect found on the way: the marcher crawls into the far end of every interval

**This is the biggest cell-count finding in the whole investigation and it is a bug, not a control.**

`BuildGridLines` grades with `BoundaryMesher.PartitionFractions`, which steps
`s = min(sizeAt(x), sizeAt(x + s/2))` — a half-step look-ahead **clamped to the end of the
interval**. The far end of an interval is very often an attractor (a conductor edge is both a hard
grid line and an edge-mesh attractor), and the size field AT an attractor is `c0` by definition. So
the first step whose look-ahead reaches the end reads `c0`, and every step from there to the
boundary is `c0` — a uniform crawl at the finest cell size across the whole width of the grading
fan, with no grading in it at all.

Measured, 100 µm × 50 mm FR-4 trace at 10 GHz: **184 grid lines in x, of which 110 were consecutive
3 µm steps over the last 330 µm of the line.** The near end graded correctly (3, 9, 27, 81, 243,
bulk) because the field GROWS away from it; only the approach fails. Long thin artwork is where it
bites — which is to say PCB traces, which is to say exactly the geometry this brief is about.

**A fix was written and verified numerically, then reverted** (see M0). The size field
`c0 + growth·d` is Lipschitz in `growth`, so the largest step consistent with the field at its own
far end is closed form:

```
s = min( sizeAt(x),  min over attractors AHEAD of  (c0 + growth·d) / (1 + growth) )
```

no iteration, no look-ahead. Attractors behind need no term — stepping forward only increases their
distance. On the same trace: **184 → 79 lines**, tail reading 714.6, 232, 77.5, 25.8, 8.6, 2.9 —
the ratio-3 fan the design asks for.

**Why it was reverted rather than shipped.** On the 200 µm × 10 mm de-embedded fixture it moves
S21 by **0.063 dB at 2 GHz** and S11 by 0.086 dB. That is an order above this kernel's own stated
de-embedding accuracy (~6.0e-3 |ΔS| at 10 GHz, §5), and it is not obvious which mesh is more right:
the OLD one has 110 extra cells of resolution right at the port, where the edge singularity is, so
it may be accidentally MORE accurate there. **Deciding that needs a convergence study, not a
judgement call** — M0.

---

## 1. What was asked for

A "transmission line" mesh option giving rectangular rather than square cells, for long narrow
transmission-line geometry, aimed at cutting the cell count on PCBs. The suggested mechanism: take
the long axis of the cell from the PORT ORIENTATION, and have the rectangles turn with the trace so
that a bent line still gets cells aligned to it.

Paraphrased into what it costs: **a cell whose long axis follows the local direction of current
flow.** Along a trace the current varies slowly (λ/20 is plenty); across it the current varies fast
(edge singularity) and needs the transverse pitch. Today the mesh gets that right only when the trace
happens to run along x or y, because `hx`/`hy` are global.

---

## 2. The hard part, stated plainly before any milestone

**D8 is one tensor-product grid over the whole layout, shared by every layer.** That is what makes a
cell a grid rectangle, `cellAt` a plain `int[]`, a rooftop a cell PAIR, and the whole
`RectangleIntegrals` closed-form fill possible. A mesh that rotates with a bend is not a tensor
product, and it invalidates, at minimum:

- `PlanarCell.IX/IY` and the `(LayerIndex, IY, IX)` ordering,
- `PlanarBasisFunctions`' rooftop-over-a-cell-pair construction,
- `RectangleIntegrals`' six closed forms (axis-aligned rectangles only) and therefore
  `SingularExtraction`, which is where this kernel's accuracy comes from,
- `PlanarAimGeometry`'s auxiliary grid and stencils,
- every `HISTORY.md` number.

**So a genuinely direction-following mesh is not an option flag. It is a second mesher.** This brief
does not propose that. It proposes the two things that get most of the win inside the tensor product,
and it records the third as the thing that would need its own project.

Three routes, cheapest first:

| route | what it is | keeps the tensor grid? | wins on |
|---|---|---|---|
| **A. Directed pitch** | one global (hx, hy) chosen from the DOMINANT current direction rather than from per-axis narrowness | yes | straight or mostly-straight runs, any angle |
| **B. Per-region pitch** | the grid is still a tensor product, but the LINE SET is denser in the band containing narrow metal and coarse elsewhere | yes | a narrow feature in a large layout — the reported symptom, where one tensor grid makes the narrow end cost the same everywhere |
| **C. Following mesh** | cells rotate with the trace | **no** | a bent trace |

**Route B is the one that answers the owner's actual reported symptom** (a 360 µm feature forcing
90 µm over 6 mm of layout) and it is strictly inside D8 — the grid lines simply need not be uniform.
Route A is the cheap half of the "transmission line mesh" ask. Route C is the ask as literally
worded and is out of scope here.

---

## 3. Milestones

### M0 — settle the marcher (do this FIRST; everything downstream sits on it)

Nothing about cell counts can be trusted while a grading fan silently becomes a uniform run at the
finest cell size.

1. Add `SurfaceMesher.PartitionGraded` — the closed form in §0d. **Leave
   `BoundaryMesher.PartitionFractions` alone**: it is kernel A's cross-section mesher and every
   kernel-A number was taken on it.

2. **Gate on the MESH, structurally — these are the deliverable and they are all cheap.** On a plain
   rectangle (200 µm × 10 mm and 100 µm × 50 mm, FR-4, cells/λ = 20 at 10 GHz):

   - **No collapsed run.** In each axis, the number of consecutive equal-sized cells at (or within a
     few percent of) `c0` must be bounded — today it is 6 on the short line and **110** on the long
     one. This is the defect stated as a testable property, and it is the primary gate.
   - **The fan grades in BOTH directions.** The ratio between consecutive cells approaching a
     conductor's far edge must stay inside the same bounded 1.2–3× band the near edge already meets.
     Assert it on the last cells of the interval, not only the first.
   - **Monotonicity in cells-across.** Cell count must be non-increasing over
     `MinCellsAcrossConductor` = 8, 6, 4, 3, 2, 1. Today it is not (220/200/180/160/180/200); with
     the marcher it is (264/220/198/176/154/154). **This is the gate that closes §0a**, and it is the
     reason M0 comes before everything else.
   - **Translation invariance.** Move the artwork 3.7 mm and the mesh must be unchanged — L8b's own
     knife edge, and a marcher change is exactly the class of edit that reintroduces it.
   - **Axis-aligned Manhattan artwork with the edge mesh OFF is bit-identical** to today (no
     attractors ⇒ no grading ⇒ the marcher is never reached). Cheap safety net; assert it.

3. **The accuracy question, measured ONCE and not as a test.** The marcher moves de-embedded S21 on
   the 200 µm × 10 mm fixture by 0.063 dB at 2 GHz. In a scratch harness (not the suite), run
   cells/λ = 20/40/80/160 with both marchers and report which sequence converges and to what.
   **Report the table to the owner and let them decide** — the old mesh has extra cells right at the
   port where the edge singularity is, so it may be accidentally more accurate there, and that is a
   physics judgement rather than a pass/fail.

4. Either way the collapsed-run behaviour is recorded in `RESOLVED.md` as a named trap. It already
   is, provisionally — update it with the outcome.

### M1 — should the DEFAULT move from 4 to 3?

The control shipped (§0a); this is only about the number an untouched setup starts on. 3 is the
cheapest rung on both fixtures and is inside §10.5's own 3–5 range. **Ask it only after M0** — the
present ladder is distorted by the grading defect, so a default chosen on today's numbers would be
chosen on an artefact. Then it is M0's own scratch-harness accuracy run at 3, reported, not a test.
**Moving a default is a separate deliberate act** — the `PlanarBoundaryCells` precedent.

### M2 — Route A, the directed pitch

`PlanarMeshSettings.TransmissionLineMesh` (default off — every existing number must be reproducible).

1. **Find the dominant direction.** The owner's suggestion — port orientation — is the right
   primary source and it is already resolved before meshing (`PlanarPortResolution`, with `MinX`/
   `MaxX`/`MinY`/`MaxY` sides and, for `InternalDeltaGap`, a required explicit direction). Use the
   vector between the two ports of a 2-port problem; fall back to the principal axis of the
   artwork's own area moment when there are not exactly two ports, and DECLINE (a note, mesh as
   today) when the two disagree by more than ~15°.
2. **Use it.** `hx`/`hy` today come from `MeasureNarrowness`, which scans axis-parallel spans. Rotate
   the scan into the dominant direction: the ALONG pitch takes `min(hWave, alongNarrow/k)` and the
   ACROSS pitch takes `min(hWave, acrossNarrow/k)` — then map back onto x and y. On artwork already
   aligned to an axis this must be **bit-identical**; assert that, it is the cheap safety net.
3. **Aspect cap.** A 60:1 cell is already shipped and fine; an unbounded one is not (the fill's τ
   binning and `SingularExtraction` were measured on moderate aspects). Cap it. **Where the cap goes
   is a fill-accuracy question, so measure it in a scratch harness against
   `PlanarFillAccuracy`'s existing 5.0e-6 yardstick and put the measured number in the doc comment**
   — the mesher-side gate is only that the cap is enforced.
4. **Measure the win in CELL COUNTS** on: a 45° trace, an L-bend, and the reported connector cutout.
   **A 45° trace is the honest case** — that is where the tensor grid is worst today and where
   Route A helps most. Cell counts are a mesher observable; no solve.

**Expected honest outcome, stated up front so it is not a surprise:** Route A helps a trace at an
angle and does approximately nothing for one already along x or y (the per-axis rule already gets
those right, §0c). If the measurement says that, say it and do not dress it up.

### M3 — Route B, non-uniform bulk pitch

The reported symptom is that one tensor grid makes a narrow feature cost the same pitch across the
whole layout. The grid lines do NOT have to be uniform to stay a tensor product.

1. Make the bulk pitch a FIELD in each axis: `h(x) = min(hWave, narrow_local(x)/k)` where
   `narrow_local` is the narrowest run within some neighbourhood of `x`, graded between neighbourhoods
   at the same bounded ratio the edge fan uses (so no knife edge — see L8b's translation-invariance
   finding, which is exactly this class of bug).
2. Gate on the owner's `.cem`: 1,997 cells today; the 360 µm feature occupies a small part of
   6.07 × 1.96 mm, so most of the layout should coarsen toward λ_g/5.
3. **Translation invariance is a hard gate** — the mesh must not change when the artwork moves
   3.7 mm (L8b's own knife edge, recorded in `SurfaceMesher`'s grading comment).
4. Gate on cell count, translation invariance and the bounded grading ratio — all mesher-only. Any
   accuracy question goes to the scratch harness and is reported, not tested.

### M4 — surface it

`.cem` `PlanarMesh.TransmissionLineMesh` under the omit-at-default rule (a pre-phase file gains no
byte), one undo entry, commits on selection, calls `InvalidateMesh()`. UI: one checkbox in the EM
setup's mesh group, beside Edge mesh. `EmSnpProvenance.MeshHash` must include it — it changes the
mesh, therefore the answer.

The report must say what it DID: the direction found, its source (ports or area moment), the along
and across pitches, and the aspect. A control that silently does nothing is the defect this whole
thread started from.

---

## 4. Must NOT

- **Clamp, refuse, or second-guess `MinCellsAcrossConductor`.** It shipped as a control on the
  owner's decision that mesh density is the user's responsibility. It is non-monotonic and the report
  says so; that is the whole of the mesher's job here. A warning may be given and may be ignored.
- **Change `BoundaryMesher.PartitionFractions`** — kernel A's, and every kernel-A number sits on it.
- **Break the tensor product.** Route C is a separate project with its own fill.
- **Default anything on.** Every number in `HISTORY.md` must stay reproducible; flipping a default is
  a separate deliberate act (the `PlanarBoundaryCells` precedent).
- **Let a mesh setting reach a physics refusal.** `PlanarProblem.MaxFrequencyHz` still means the
  sweep's top; `CanSolve` still sees only a `PlanarProblem`.
- Add a UI hint that any of these is "cheaper" without the measurement beside it — §0a and
  `CLAUDE.md` §6 are both records of that hint being wrong.

## 5. Gates

**All mesher-only** (see the testing rule above). M0's four structural gates — no collapsed run,
the fan grades at both ends, monotonicity over the cells-across ladder, translation invariance —
plus its bit-identity net on edge-mesh-off Manhattan artwork · M2's bit-identity assertion on
axis-aligned artwork and its enforced aspect cap · M3's translation-invariance gate and the cutout's
cell count · M4's provenance-hash inclusion and the report's own "what it did" line · the two
scratch-harness measurements (M0's convergence table, M2's aspect-vs-fill-accuracy point) REPORTED
rather than gated · `RESOLVED.md` write-up
(**not `CLAUDE.md`** — `CLAUDE.md` gains only what is still true tomorrow: a new default, a new
refusal, a named trap).

## 6. Reading order

`SurfaceMesher.cs` (`Mesh`'s M0 block, `MeasureNarrowness`, `BuildGridLines`, `SizeAt`,
`GrowthRatioFor`) · `BoundaryMesher.PartitionFractions` · `CLAUDE.md` §3.2 (the mesher) and §6's
mesh-frequency negative result · `HISTORY.md` §L8b (the graded-mesh knife edge, and why the growth
ratio is derived rather than fixed) · `PlanarPortResolution` for the direction source.

# Brief PM-1 — Planar mesh: size it from the conductor, and refine its re-entrant corners

**Series:** planar mesh (PM), 2026-10-29. · **Tag:** `R-pm1-<n>`
**Area:** `src/Design/Layout/Em/PlanarExtractor.cs` (`MergeOverlappingCopper`, `MergeOverlapping`),
`src/Engine/Mom/SurfaceMesher.cs` (`MeasureNarrowness`, `CollectBoundaryLines`, `ConvexCorners`, `Crowds`,
`LocalWidthAt`), `src/Design/Layout/Em/EmSnpProvenance.cs` (only if D1 asks for it). Findings go in `src/Engine/Mom/RESOLVED.md` (mesher) and `src/Design/RESOLVED.md` (extractor).
Read `src/Engine/Mom/CLAUDE.md` §3.2 and §6 before touching the mesher.

**Part 1 ships. Part 2 is a measurement that decides whether a PM-2 brief is written.**

---

## 0. Why

Imported artwork (DXF, Gerber) routinely arrives as many shapes that touch edge to edge rather than as one polygon
per conductor. To measure what that does to the planar mesh, we built an **internal test geometry outside the
repo**: 26 abutting pieces, the way a DXF import delivers them — straight pieces, quarter-ring bends, and
T-junctions whose V-notch is cut into two trapezoids.

The owner has it; ask for its location. Never commit it. Every number below is from `circuitrf check` on its
`.cem`, which runs the same extraction and mesh as Simulate and solves nothing.

| Test geometry | Unknowns | Narrowest conductor reported |
|---|---|---|
| As drawn (26 shapes) | **155,000** (31× the dense ceiling) | **0.0172 mm** |
| Copper unioned into one shape first | **7,790** | 0.176 mm (the real line) |

**Cause 1 — narrowness is measured per drawn shape.** `SurfaceMesher.MeasureNarrowness` takes the 5th-percentile
scan-line run per polygon. A notched T-junction drawn as its own trapezoid has a wedge tip, and runs near that tip
read 17 µm. That sets the cell pitch for the whole part. The λ_g/200 detail floor (12.4 µm at its sweep's top frequency) sits just
below 17 µm, so it does not catch it.

The planar path merges only shapes that **overlap** (`MergeOverlapping(..., touching: false)`); the 3D path also
merges shapes that share an edge. Shared edges between pieces also become hard gridlines, and each piece's edges
are judged for edge fans against that piece's own extent.

**Cause 2 — no refinement at re-entrant corners.** This was found while testing the fix for cause 1.

Three options were measured with a temporary switch (not committed):

- **A** — merge touching copper in planar extraction.
- **B** — keep the drawn pieces for meshing, but measure narrowness on the merged outlines.
- **C** — add graded edge fans at re-entrant (concave) Manhattan corners.

| Unknowns | Current code | A | B | A + C |
|---|---|---|---|---|
| Test geometry, default mesh | 155,000 | 7,790 | 19,811 | 15,236 |
| Test geometry, transmission-line current model | 35,998 | 5,523 | 15,024 | 11,210 |
| Patch Antenna example (5.3–6.3 GHz) | 1,611 | 1,504 | 1,611 | 1,981 |
| Klopfenstein Taper example, 7 port-calibration fixtures | unchanged | unchanged | unchanged | not measured |

B is rejected. It still keeps every piece boundary as a gridline across the whole part, plus each piece's own fans.
On the test geometry it lands above even the accelerated ceiling (12,000).

The **patch example is the one shipped design A changes**, so its S11 was checked for convergence. Each mesh was
solved at its normal density and at double density (cells/λ 20 → 40, Auto off), 11 points:

| Mesh | Worst \|ΔS11\| when density doubles | vs the current code |
|---|---|---|
| Current code | 0.020 | — |
| A | **0.087** (drifts toward the current code) | up to 0.17 apart |
| A + C | 0.020 | **within 7×10⁻⁵** (1.6×10⁻⁴ at double density) |

What happened: in the patch file the inset feed is its own rectangle. Its top edge, where it meets the patch, has
two convex corners, so `Crowds(..., cap: true)` gave it a graded fan (y lines at 3.72, 3.89, 3.95, 4.00, 4.05,
4.19 and 4.62 mm). Merged, that edge is the bottom of the inset notch. Its corners are now **re-entrant**, and the
mesher grades only at convex "caps" and long edges. The current patch mesh is converged **because of how the
artwork happened to be drawn**, not by any rule. A re-entrant corner is where the current is most singular, so
that is a real gap in the mesher, not a patch-specific quirk.

So Part 1 is A + C together. A without C makes the patch example measurably worse.

## 1. Part 1 — what to do

### R-pm1-1 — The test cases

- **The test geometry** (outside the repo, from the owner) is where the problem shows. Measure on it; never copy it into
  the repo. It carries two `.cem`s: the default mesh, and the transmission-line current model.
- **The Patch Antenna example** (`examples/Patch Antenna`, and its copy `testdata/antenna/patch`) is the in-repo
  accuracy anchor. Do not edit either.
- Unit tests build their own small shapes in code (R-pm1-5). No new `testdata` workspace is needed.

### R-pm1-2 — A: merge touching copper in planar extraction

`MergeOverlappingCopper` passes `touching: true`, as the 3D generator's call already does. That makes one
conductor one polygon for every per-polygon question downstream: narrowness, edge attractors, feed-lead
detection, port end faces.
- Update the merge note's wording: "overlapping or touching".
- Check that `PlanarFeedExtension` and `EmPortExtraction` give the same decisions on the port-calibration fixtures
  and the patch. On the patch they did in the experiment: the feed-clearance and calibration notes were identical.
- The comment above the merge call (and `MergeOverlapping`'s own doc) says the planar call leaves touching off
  "so no planar answer moves". That is now false by design. Rewrite it with the reason above.

### R-pm1-3 — C: graded fans at re-entrant Manhattan corners

In `CollectBoundaryLines`, for each **re-entrant vertex whose two edges are one vertical and one horizontal**, add
an x-attractor at its x and a y-attractor at its y. Each takes `LocalWidthAt` of the adjacent edge on that axis.
The experiment did exactly this and got the table above.

Rules:
- **Only when the edge mesh is on**, like every other attractor (`grade`). With `EdgeMesh` off nothing changes,
  bit for bit.
- **Skip a corner if either adjacent edge is shorter than the detail floor**, the same rule edges already follow.
- **Oblique re-entrant corners are out of scope.** A staircased rim gains nothing from a fan: see §6 of the Mom
  `CLAUDE.md`, "Rim / edge grading on curved geometry". Do not retry that.
- A convex corner is already covered by the cap rule. Do not double-count it.
- If an attractor coincides with an existing one (same coordinate within tolerance), keep the finer one rather
  than adding a duplicate.

### R-pm1-4 — Measure

Use a scratch harness, never the Benchmark tier.

1. **Counts:** current code against A+C on the test geometry (both `.cem`s), the patch, the taper, every `.cem` under
   `testdata/portcal`, and the multi-level ones (`examples/PDK PCells`, `testdata/em3d/f0/B-via/planar`). The
   multi-level ones printed no "Mesh budget" line through `check`, so take their counts from the mesh report
   directly.
2. **Patch convergence:** reproduce the table in §0 at 20 and 40 cells/λ. The gate is in §3.
3. **Test geometry S-parameters, A against A + C:** transmission-line current model, accelerated solve, 3 points
   across its sweep. The question is whether C matters on artwork whose re-entrant corners are T-junctions and steps.
   Estimate the run time first, **ask the owner before anything over five minutes**, and put a memory watchdog on
   the process.

One table per item into `src/Engine/Mom/RESOLVED.md`.

### R-pm1-5 — Tests

Minimal: one test per claim, and run only the classes you touched (`--filter`), never the whole suite.

- **A:** two abutting rectangles that make one L-shaped line report the same narrowest width as the same L drawn
  as one polygon.
- **A notched T** drawn as two trapezoids plus a line does not report a narrowest width below the line's real
  width.
- **C:** a notch's re-entrant corners carry attractors.
- **C:** with `EdgeMesh` off, the mesh is bit-identical to today.

Several existing tests pin unknown counts on fixtures drawn from abutting rectangles (grep the Em and Mom test
projects for `UnknownCount` and pinned literals). Any that move must be explained one by one in RESOLVED, never
just re-pinned.

### R-pm1-6 — What the user sees

- The mesh report's narrowest-conductor note is unchanged in form. It just stops reporting a fragment.
- Add a one-line note when C acted: "Edge fans at N re-entrant corner(s)."
- Update the user reference page for the planar engine (`docs/user/src/reference/mom-engine.md`) where it
  describes which edges get a fan. Do not regenerate the docs; the owner does that at the end of a series.

## 2. Part 2 — measure the ceiling on local refinement (R-pm1-7), then stop or go

The tensor grid makes every fan line cross the whole part. That is why C costs 7,446 unknowns on the test geometry:
fans at about a dozen junction corners, each spread across 10 mm. The way to keep rooftops and still refine
locally is **rooftop merging**: fine cells stay where a feature needs them, and the interior rooftops along a fine
line, far from the feature that raised it, are combined into one larger rooftop with fixed weights. The basis is
unchanged, but the fill becomes a weighted sum of elemental interactions.

This brief does **not** build it. It measures how much it could save:

- On the test geometry after Part 1, classify each unknown as **needed** or **spread**. A spread unknown lies on a
  gridline at a point where the size field (`h(x)` from the attractors and the pitch) is more than 2× coarser
  than the cell actually used.
- Report the spread fraction for the test geometry (both current models) and for the patch.
- **Go** (write brief PM-2 for rooftop merging) if the spread fraction is **at least 40 %** on the test geometry.
  Otherwise record the table and stop.
- PM-2 would also need to measure what merging costs in fill time, before any of it ships.

## 3. Gates

1. **Test geometry:** its narrowest conductor reads 0.176 mm, not 0.0172 mm, and its default mesh is
   under 16,000 unknowns. Its transmission-line mesh fits the accelerated ceiling (under 12,000).
2. **Patch:** A + C at 20 cells/λ agrees with today's mesh to |ΔS11| ≤ 1×10⁻³ at every point of an 11-point
   5.3–6.3 GHz sweep. Its own doubling moves it by no more than today's does (0.020).
3. **No regression:** every `testdata/portcal` fixture and the taper have the same unknown count, or a counted and
   explained change.
4. **Off is off:** `EdgeMesh` off gives a bit-identical mesh to today on the patch and the test geometry.
5. Part 2's table and its go/stop verdict in RESOLVED.

## 4. Approaches considered, and why this brief takes only two

**Taken in Part 1.**
- **Size the mesh from the conductor, not from the pieces it was drawn in (A).** A shared edge between two drawn
  pieces is not a feature of the metal. Measured on pieces, a feature size is wrong by construction; measured on
  the merged copper, a narrow place sets a fine pitch only because the metal is narrow there.
- **Keep `narrowest / MinCellsAcrossConductor` as a sizing term.** A line that is narrow against the wavelength
  needs cells small against its width, whatever the wavelength. The fix is to measure "narrowest" correctly, not
  to drop the term.
- **Grade toward re-entrant corners as well as edges (C).** The current is singular at a conductor's edges and
  more strongly at its re-entrant corners. A graded fan is the right shape for both; the 3 % edge cell and the
  derived growth stay as `RESOLVED.md` §EFAN measured them against kernel A.

**Later, only if Part 2 says so.**
- **Rooftop merging** (Part 2, PM-2): local refinement with the basis unchanged. The fill cost must be measured.
- **Refinement driven by an error indicator** (a residual, or the jump in the solution across cell boundaries).
  It needs local refinement to act on, which a tensor grid does not have.
- **Mesh doubling as the convergence check**, as in §0. It is cheap and is what the gates use.

**Not now, and why.**
- **Triangular meshes** (with a band of thin cells along each edge). They help most on curved and 45° artwork,
  and are roughly even with a rectangular grid on Manhattan copper. They mean a new basis, a new fill and new
  singular integrals. Curved artwork is better served first by the conformal cut cells that already exist
  (`PlanarBoundaryCells.Conformal`).
- **Conformal subsections that build the edge-current profile into fixed weights.** Large gains on curved edges,
  but a heavy robustness burden, and they fall back to ordinary rooftops at junctions, open ends and vias.
- **Discontinuous, non-conforming meshes** (hanging nodes). They allow local refinement, but the formulation is
  prone to low-frequency breakdown, and this solver publishes a 0 Hz point.
- **Basis functions that carry the edge singularity.** They need singular quadrature in the fill, for a gain
  that is not established on layered planar media.
- **Polygonal-cell or mixed rectangle/triangle bases.** The same basis-change cost as triangles.
- **Snapping imported copper to a fraction of the edge cell** to remove slivers. A possible addition for imported
  artwork, but A already removes the slivers this brief is about.

## 5. Decisions for the owner

- **D1 — stale results.** `EmSnpProvenance.MeshHash` hashes the mesh **settings**, not the mesher. A `.snp` solved
  before this change, from artwork drawn as touching pieces, will not be reported stale although its mesh changes.
  - (a) **Leave it** (recommended). It is the same rule every earlier mesher fix followed.
  - (b) Add a mesher-revision term to the hash, so every planar `.snp` reads stale once.
- **D2 — C's cost.** On the test geometry, C adds 7,446 unknowns at the default mesh, which is over the accelerated
  ceiling. With the transmission-line model it is 11,210, which runs.
  - (a) **Accept it** (recommended). The patch shows C is what keeps a merged design accurate, and Part 2
    measures the way to make it cheap.
  - (b) Shorten fans at re-entrant corners only (fewer graded cells).

  Take (b) only if R-pm1-4 item 3 shows C does not move the test geometry's S-parameters.

## 6. Not in this brief

- Rooftop merging itself (PM-2, only on Part 2's Go).
- Triangles, conformal subsections, DG, singular bases — see §4.

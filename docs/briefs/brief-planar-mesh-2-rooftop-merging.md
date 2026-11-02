# Brief PM-2 — Planar mesh: rooftop merging (local refinement on the tensor grid)

**Series:** planar mesh (PM), 2026-10-29. · **Tag:** `R-pm2-<n>` · **Follows:** PM-1 (its Part 2 said Go)
**Area:** `src/Engine/Mom/SurfaceMesher.cs`, `PlanarMesh`, `PlanarFill`, the AIM path. Findings go in
`src/Engine/Mom/RESOLVED.md`. Read `src/Engine/Mom/CLAUDE.md` §3.2, §3.3 and §8 first.

**Part 1 is a cost measurement, and nothing ships until it is done. Part 2 builds only if Part 1 says it pays.**

---

## 0. Why

The tensor grid makes every gridline cross the whole part. A fan raised by one feature, and a hard gridline raised
by one edge, are fine everywhere along their length, also where nothing needs them. PM-1 measured how much of the
mesh that is (`src/Engine/Mom/RESOLVED.md` §PM-1, Part 2). An unknown is **spread** when the size field at its
location, with each attractor's fan localised to the feature that raised it, is more than 2× coarser than the cell
it actually sits in:

| Mesh (after PM-1) | Unknowns | Spread (> 2×) | Spread (> 4×) | Finer than even the 1-D tensor field asks (> 2×) |
|---|---|---|---|---|
| Internal test geometry, default mesh | 15,236 | **82.8 %** | 58.1 % | 58.2 % |
| Internal test geometry, transmission-line model | 11,210 | **85.3 %** | 60.8 % | 62.0 % |
| Patch Antenna example | 1,981 | **69.2 %** | 29.6 % | 54.9 % |

The Go threshold was 40 % on the test geometry. Even at 4× it is 58 %.

**The last column changes what PM-2 has to be.** More than half the unknowns are in cells over 2× finer than the
1-D tensor field asks for. That comes from **hard gridlines**, not from fans: every axis-parallel edge coordinate is a
gridline across the whole part, and edges of different features a few tens of µm apart cut thin sliver rows through
all the metal. On the test geometry only 5 of 260 x-intervals and 24 of 129 y-rows are slivers, but those rows run
through the metal, so they hold most of the unknowns. Merging that handles only fan lines would miss most of the
saving.

## 1. What rooftop merging is

Keep the elemental rooftops on the tensor grid. Where a run of consecutive rooftops along one gridline direction sits
in cells finer than the local field needs, replace the run by **one macro-rooftop**: a fixed-weight sum of the
elemental ones, with weights that reproduce a rooftop on the coarser support (piecewise-linear current, continuous
normal component). The basis is unchanged; the system becomes `Tᵀ Z T` with `T` sparse.

- **Never assemble the elemental `Z`.** At 15,236 elemental unknowns it is 3.7 GB. Each macro entry is a weighted
  sum of elemental interactions, filled directly.
- **Charge continuity at the merge boundary** is the part that is easy to get wrong. A macro-rooftop next to an
  unmerged one must share an edge whose normal current matches, or there is a line charge at the join.
- **Near the feature that raised a line, nothing merges.** Only spread unknowns (as defined above) are candidates.

## 2. Part 1 — measure the cost (R-pm2-1)

Scratch harness only, never the Benchmark tier.

1. **Merged unknown count** on the three meshes above, from the same classification PM-1 used: what N would be
   after merging every spread run. That is the ceiling on what this can save.
2. **Fill time:** a macro entry costs (elements in A) × (elements in B) elemental integrals. Measure the fill of the
   merged system against today's fill on the test geometry (transmission-line model, one point). If the merged fill
   costs more than the solve it saves, stop and record it.
3. **The AIM path:** whether macro-rooftops can be projected onto the AIM grid at all, or whether a merged mesh is
   dense-only. If dense-only, say what that does to the 12,000 ceiling's reach.

**Stop or go:** go to Part 2 only if the merged test geometry (default mesh) fits under the dense ceiling (5,000) or
the merged fill plus solve is at least 2× faster than today at the same accuracy.

## 3. Part 2 — build it (R-pm2-2…)

Only on Part 1's go. Off by default behind a `PlanarMeshSettings` flag until its gates pass, and then on.

## 4. Gates

1. **Patch:** merged against PM-1's mesh, |ΔS11| ≤ 1×10⁻³ at every point of the 11-point 5.3–6.3 GHz sweep, and
   its own doubling (20 → 40 cells/λ) no worse than PM-1's (0.020).
2. **Test geometry:** transmission-line model, 3 points, |ΔS| against PM-1's mesh within the doubling spread PM-1
   measured. Ask the owner before any run over five minutes, with a memory watchdog on the process.
3. **Off is off:** with the flag off, the mesh is bit-identical to PM-1's.
4. **Every `testdata/portcal` fixture and the taper:** same unknown count with the flag off; with it on, a counted
   and explained change.

## 5. Not in this brief

- Snapping imported copper so near-coincident edges share one gridline. It attacks the same sliver rows from the
  geometry side. Measure it as a cheaper alternative inside Part 1 if the hard-line share stays above half.
- Triangles, conformal subsections, non-conforming meshes, singular bases — see PM-1 §4.

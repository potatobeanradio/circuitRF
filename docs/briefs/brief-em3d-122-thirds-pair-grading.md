# Brief 122 — openEMS grid: no line inside a thirds pair

**Series:** [3D EM, eighth series](brief-em3d-112-overview.md) · **Tag:** `R-em3d122-n` ·
**Precedent:** R-em3d8-2b/-4 (the thirds rule, its width and gap clamps, and `Fill`'s grading), recorded in
`src/Engine/RESOLVED.md`; brief 116's gate 9 (the coupled pair against its ideal line)
**Area:** `src/Engine/Em3d/FdtdGrid.cs` (`LocalCell` ~1173, `ThirdsGapFraction` 298, the thirds lines ~500, `Fill` 693,
`Smooth` 787), `tests/Engine.Tests/Em3d/FdtdGridTests.cs`, `tests/Ui.Tests/Em3d/` (one Benchmark accuracy gate)
**Depends on:** nothing · **Found by:** brief 117 (`src/Engine/RESOLVED.md` § "openEMS grid: a thirds pair broken by
grading in a narrow gap")

---

## 0. The defect, measured

The thirds rule puts two lines at each metal edge: one h/3 inside the metal and one 2h/3 outside, **with no line
between them** (h is the edge's local cell). That places the edge singularity a third of a cell into a cell. Two strips
facing across a gap S leave a **middle cell** between their outside lines:

  M = S − 4h/3.

The gap clamp (`ThirdsGapFraction` = 3/7) lowers h until M = h, but **only when h > 3S/7**. Just below that, M is a hair
wider than the largest cell allowed, so `Fill` splits it in two. Each half then sits beside an h-wide thirds cell at a
ratio near 2, past `GradingRatio` 1.3, and the grading **inserts a line inside each thirds pair**, a fraction of a cell
from the edge. Nothing warns: the grid summary only names a smaller smallest cell.

Brief 117's first Pair section (PTFE εr 2.1, b 2 mm, W 1.6 mm, S 0.3 mm, 90 cells per wavelength at 18 GHz, so
h = 127.7 µm, MinCellUm 10). The y lines near the gap:

    −281.7  −192.6 | −122.3 | −64.9   0.0   64.9 | 121.8 | 192.6   283.1
                     ^^^^^^                        ^^^^^
            a line inside each thirds pair (−192.6/−64.9 and 64.9/192.6)

M = 300 − 170.3 = 129.8 µm, just above 127.7. The line at 0 is **not required by anything**: `Fill` made it. The
consequences, against Cohn's ideal coupled line (Z₀e 59.80, Z₀o 41.89 Ω):

| | thru at 18 GHz | far-end at 18 GHz | ε_eff | max \|ΔS\| |
|---|---|---|---|---|
| S 0.3 mm (pairs broken) | 15° late | −12.6 dB (ideal −75 dB) | odd mode 2.25–2.48 (should be 2.1) | 0.26 |
| S 0.3 mm in air | 11° late | −14.3 dB (ideal −28.5 dB) | odd mode 1.11–1.15 (should be 1.00) | 0.18 |
| S 0.4 mm (W 1.65; M 229.7, halves 114.9, no insertion) | 0.1° | −61.4 dB (ideal −64.9 dB) | 2.10 per terminal | 0.0027 |

The odd mode's field is in the gap, so it is the mode that runs slow. 117 shipped S = 0.4 mm to stay clear of this.

**Expected shape of the failure** (derive, then confirm by measurement in R-1). M is split into n = ⌈M/h⌉ cells of M/n,
and the pair breaks when M/n < h/r (r = 1.3). That gives bands of S/h ≈ (2.33, 2.87), (3.33, 3.64) and (4.33, 4.41), and
none for n ≥ 5. 117's failing S/h is 2.35 and its passing one is 3.13. `Fill`'s smoothing and slack will move the band
edges a little: the measurement is what counts.

## 1. `R-em3d122-1` — measure first

1. **An invariant check**, `FdtdGrid` internal and reusable by tests: for every required line pair that comes from one
   edge (`FdtdLineKind.ThirdsInside` and `ThirdsOutside` sources with the same feature and `FeatureAtM`), no grid line
   lies strictly between them. Return the violations with the edge's feature name and position.
2. **Sweep:** two facing sheets under a fixed cap, S/h from 1.5 to 6 in steps of 0.01. Record which S/h break pairs
   today, and compare with §0's bands.
3. **The repo:** run the check over every openEMS grid the repo builds without a solver. That means the three example
   `.c3d` openEMS setups (`3D Connector`, `3D Wave Ports` Launch and Pair), the `testdata/em3d/openems-goldens` cases,
   the openEMS fixtures in `OpenEmsBackendTests`, `OpenEmsWavePortTests` and `OpenEmsCylindricalGridTests`, and
   `FdtdGridTests`' fifty seeded Manhattan layouts. Count the violations per problem.
4. **Single edges:** check whether a pair ever breaks against something other than a facing edge (a box face, a fixed
   wave-port feed line, a material face) at a distance that leaves a just-too-wide cell. If R-1.3 finds such a case, the
   fix must cover it.

Record the results in `src/Engine/RESOLVED.md` under the existing section, before changing any code.

## 2. `R-em3d122-2` — the fix

**Default (D1): repair on violation.** After `Fill`, run R-1's check. For each broken pair, lower that edge's local cell
so the interval beside its outside line divides into whole cells of the new h, and rebuild the axis. Repeat until clean;
bound the loop, as `Fill`'s own smoothing is bounded. For two facing edges with ⌈M₀/h₀⌉ = k, that is

  h′ = S / (k + 4/3),

which makes the middle exactly k cells of h′. h′ ≤ h₀ always, so this only ever refines. It reduces to today's 3/7
clamp at k = 1. For S 0.3 mm above: k = 2, h′ = 90 µm, so the lines run −180, −90, 0, 90, 180 µm: four 90 µm cells from one inside line to the other, each edge a third of a
cell into its pair.
The same rule with the single-edge interval (D − 2h/3 = k·h) covers R-1.4's cases if any exist.

- **No churn by construction:** a grid with no violation is built exactly as today, so every golden that R-1.3 finds
  clean stays byte-identical.
- The lowered h is recorded on the thirds line's source (`LocalCellM`, `LocalCellSetBy` naming the facing conductor or
  feature), so `explain` and the grid summary say why the cell there is smaller, the way they already name a gap clamp.
- `Fill` itself is unchanged, and `GradingRatio`, `ThirdsRule` and the 3/5 width clamp keep their meaning.

## 3. Decisions — each with this brief's default

| # | Question | Default |
|---|---|---|
| D1 | Repair on violation, or always size facing edges so the middle is whole cells | **On violation** (zero churn for clean grids). Always would move every grid with two facing edges, golden by golden |
| D2 | A violation the repair cannot clear (the loop's bound reached) | **Throw**, as `Fill` does for its own non-convergence: it is a defect, not a grid to run |
| D3 | A warning when the repair acts | **None.** It is the rule working; `explain` names the cell's cause (§2) |
| D4 | 117's Pair back to S 0.3 mm | **No.** The example is right at 0.4 mm and its numbers are recorded; this brief changes no example |

## 4. Gate

1. **`FdtdGridTests`, the sweep:** two facing sheets, S/h from 1.5 to 6 in 0.01 steps. No line inside any thirds pair,
   `AssertGraded` holds, and `AssertMaxCell` holds. Before the fix, the same test fails in R-1.2's bands. Say so in the
   commit, with the band edges.
2. **`FdtdGridTests`, 117's case:** the geometry of §0 (W 1.6, S 0.3, b 2 mm, the same cap) gives the gap's lines at
   multiples of h′ from the edges, with no line within h′/10 of either edge other than the pair's own.
3. **Gate 2's fifty seeded layouts** also assert the invariant.
4. **No churn:** `OpenEmsBackendTests.Gate4`'s goldens, `OpenEmsWavePortTests`' `coax-wave` golden and
   `OpenEmsCylindricalGridTests`' goldens are byte-identical. If R-1.3 found a violation in one, re-record it, name the
   lines that moved, and say why in `src/Engine/RESOLVED.md`.
5. **Accuracy (Ui.Tests, `[OpenEmsFact]`, `Category=Benchmark`, about a minute):** §0's S 0.3 mm PTFE pair as a `.c3d`,
   built the way `OpenEmsWavePortTests.Pair()` is, with 117's Pair setup (2–18 GHz in 33 points, 90 cells per
   wavelength, MinCellUm 10, PMC sides 10·W out, PEC top and bottom). Against Cohn's line (Z₀e 59.80, Z₀o 41.89 Ω,
   15 mm): thru within 0.1 dB / 1°, near-end and far-end within 0.5 dB / 1° where the ideal entry is above −30 dB, and
   max |ΔS| ≤ 0.015 at every frequency. Today: 0.26.
6. **The examples, once:** `Em3dWavePortsExampleTests.Gate4` and `Em3dConnectorExampleTests.Gate6` (Benchmark) pass
   unchanged. R-1.3 predicts whether they can move; if one does, re-record it under its own example's rules.

Scope the runs: `FdtdGridTests`, the openEMS test classes named above, and the Benchmark gates once each. No full suite.

## 5. Owner check

`circuitrf explain` on the S 0.3 mm pair's `.c3d` (setup *openEMS*): the grid summary's smallest cell names the gap, and
the y lines across the gap are evenly spaced. Run it: the far-end coupling is down where Cohn puts it.

## 6. Scope

- The Cartesian grid only. The cylindrical grid (brief 120) has no thirds rule.
- No new setting, and no change to `GradingRatio`'s default or meaning.
- 117's example is untouched (D4).

# PSV-3 gate 3: a planar via through two dielectrics, against 3D

A via that crosses a dielectric interface with no metal on it, solved by the planar kernel and by a 3D
solver, beside the same structure in one dielectric. No solver runs in CI; `refs/` holds what the runs
wrote.

## The structure

Ground plane, 100 µm of εᵣ 3.0, the buried level `M1`, then 100 µm of εᵣ 10.0 and 100 µm of εᵣ 3.0 up to the
top level `M2` (`tech/psv-via-10-3.ctech`). A 200 µm wide, 1 mm line on `M1` runs into a 300 µm square pad,
a 150 µm square via joins it to the same pad on `M2`, and a 1 mm line leaves on `M2`. Port 1 is the `M1`
line's end, port 2 the `M2` line's. The via crosses the εᵣ 10 / 3 interface 105 µm above `M1`.

`one-region/` is the same layout on `tech/psv-via-3.ctech`, where both upper layers are εᵣ 3.0 and the
extractor merges them, so its via lies in one region.

## The runs

`via/em/planar.cem` (planar kernel, de-embedded, 1,514 unknowns with 12 vertical) and `via/em/emerge.cem`
(3D, sheet conductors, wave ports on the line ends, EMerge Accurate; 35,146 unknowns, 6 s). The same
`emerge.cem` run with `--solver palace` wrote `refs/palace.s2p`. 1, 5 and 10 GHz.

## What they show

Worst |ΔS| of the four entries:

| Pair | 1 GHz | 5 GHz | 10 GHz |
|---|---|---|---|
| **Planar vs EMerge, via through εᵣ 10 and 3** | 0.0056 | 0.0205 | 0.0333 |
| Planar vs EMerge, one region (`one-region/`) | 0.0049 | 0.0245 | 0.0428 |
| Planar: two dielectrics vs one region | 0.0111 | 0.0571 | 0.1200 |
| EMerge: two dielectrics vs one region | 0.0105 | 0.0543 | 0.1147 |

The via through the interface agrees with 3D as closely as the one-region via does, and both solvers
see the same size of difference between the two stacks, which is what describing the via's
surroundings with the εᵣ 3 dielectric alone would cost.

**`refs/palace.s2p` is not a reference.** Both Palace runs are non-reciprocal: |S₂₁ − S₁₂| is 0.11 / 0.28 /
0.34 here and 0.09 / 0.27 / 0.37 in `one-region/`, against 0 for planar and 2e-4 for EMerge. |S₂₁| itself
agrees with the other two; the two phases do not. It happens without the interface too, so it is about
the 3D ports of this fixture, not about the via: Palace leaves each wave port's mode in an arbitrary
phase (−9.86° on port 1's buried line, +0.41° on port 2 at 10 GHz), and the published S carries the
difference. Rotating each port by its mode-voltage phase gives 0.025 against planar and 0.020 against
EMerge at 10 GHz. The fix is `docs/briefs/brief-em3d-141-palace-wave-port-mode-phase.md`; the file is
kept so the defect can be reproduced.

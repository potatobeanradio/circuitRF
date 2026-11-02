# Brief SAR-8b — `MRI_LP_Birdcage`, rebuilt in circuitRF on its own cylindrical grid

**Series:** `brief-sar-0-overview.md` (§4, O5) · **Tag:** `R-sar8b-<m>`
**Depends on:** SAR-8 (the loop-coil gate, `docs/design/sar.md` §10)
**Area:** `src/Design/Em3d/CsxcadWriter.Cylindrical.cs`, `src/Engine/Em3d/FdtdGrid*` (cylindrical given lines), the SAR result path
(`OpenEmsSarResults`, `src/Engine/Em3d/Sar/`), `testdata/sar/circuitrf/birdcage/` (new), `tests/Ui.Tests/Sar/`, `sar.md` §10a

---

## 1. Why this is its own brief

SAR-8's R-sar8-5 asked for the birdcage beside the loop coil. The owner moved it here (2026-10-18), because the birdcage cannot
be expressed in circuitRF today on the grid its reference ran on:

- circuitRF's cylindrical openEMS grid (brief-em3d-120) carries **coaxial wave ports only**: lumped elements and lumped ports are
  refused on it (sar.md §5.3), and so are given grid lines (§5.5) and SAR (§8.1);
- on a Cartesian grid the eight rungs at 45° steps, their capacitor gaps and the two rings can only be staircased, and a capacitor
  on a 45° rung is not an axis-aligned lumped element at all. A Cartesian rebuild could be reported, never held to the reference's
  cylindrical run.

So this brief extends the cylindrical lowering to what the birdcage needs, then replicates it the way SAR-7 and SAR-8 did.

## 2. Requirements

**R-sar8b-1 — Lumped elements and lumped ports on a cylindrical grid.** A `C3dLumpedElement` or lumped `C3dPort` whose gap is
z-directed or α-directed on a cylinder of the grid's axis is written in (ρ, α, z), as the tutorial's `AddLumpedElement` /
`AddLumpedPort` boxes are. Its gap is resolved by the port's own code (sar.md §5.3), and its excitation box is written ON the grid's
lines (sar.md §10.1: openEMS's box test has no tolerance).

**R-sar8b-2 — Conductors on a cylinder.** A rung or a ring segment is a strip of zero radial thickness on the cylinder r = R
between two azimuths and two heights. Decide, and record, whether that is a new sheet kind (a cylindrical sheet) or a `Cylinder`
sector; either way it lowers to one (ρ, α, z) box, exact on the grid.

**R-sar8b-3 — Given cylindrical lines, multigrid, the bore.** `OpenEms.GridLines` accepts a cylindrical `File` (r, α, z, the
closed α circle as the tutorial writes it), and the setup states openEMS's `MultiGrid` radii. The bore's PEC shield is the grid's r⁺
boundary; z is PML with the tutorial's 10 added lines.

**R-sar8b-4 — Local SAR and B1 on a cylindrical grid.** The raw dump on a cylindrical grid, local SAR from it, and B1 from Hρ, Hα
converted to Hx, Hy (sar.md §1.5). Cube averaging stays refused there (cubes are Cartesian), and the run says so.

**R-sar8b-5 — The quadrature feed as superposition.** The tutorial drives rung 1 at 1 and rung 3 at −j (a quarter-period delay).
circuitRF runs each port alone, the other terminated in its 50 Ω, and combines the two runs' complex E and H at 128 MHz with weights
1 and −j before SAR and B1 are formed (SAR is quadratic in E, so the combination is of fields, never of SAR). Accepted power is the
sum over both ports of 2·Re(U·I*) of the combined port waves. This is circuitRF's first superposition; SAR-9 generalises it.

**R-sar8b-6 — Comparison**, on the reference's own cylindrical lines, against SAR-1's `MRI_LP_Birdcage/`, in the loop's form: S11
minimum frequency and depth (over a common time window, sar.md §10.4), |B1+| at the smallest dumped radius per √W at both ports,
the B1+ and local-SAR planes' correlation, peak local SAR per W, and B1+ homogeneity — the coefficient of variation of |B1+| over the
phantom's central slice. The B1 values are compared at the field's own positions, as sar.md §10.3 does for the loop.

## 3. Not in this phase

Capacitors as ports (SAR-9); cube-averaged SAR on a cylindrical grid.

## 4. Gates

`BirdcageReplicationTests` (fixed data, routine); a `Benchmark` rerun (the reference took 53 min on one machine).

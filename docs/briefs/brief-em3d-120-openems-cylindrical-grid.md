# Brief 120 — openEMS: a cylindrical grid, exact cylinders and a weighted source

**Series:** [3D EM, eighth series](brief-em3d-112-overview.md) · **Tag:** `R-em3d120-n` ·
**Precedent:** [brief 113-a](brief-em3d-113-a-terminal-port-redo.md) (`src/Design/RESOLVED.md` § "Terminal wave
ports — brief-em3d-113-a", R-em3d113a-2a), and F0, whose upstream-written XML is `CsxcadWriter`'s structural oracle
**Area:** `src/Engine/Em3d/FdtdGrid.cs` (`OpenEmsGridSettings`, `FdtdGrid.Build`, `FdtdGridResult`),
`src/Design/Em3d/CsxcadWriter.cs` (`Head` ~501, `Primitive`, `ExcitationProperty` ~567, the probes),
`src/Design/ThreeD/` (the setup's `OpenEms` block, the lowering of booleans), `src/Render/Scene3D/FdtdGridOverlay.cs`,
`src/Cli/` (`check`, `explain`), `docs/user/src/reference/`
**Depends on:** 116 (the wave-port feed, probes, current averaging and the Cartesian weighted coax source this brief
extends) · **Blocks:** nothing in this series. 116 meets 0.6 Ω on a Cartesian coax; this brief meets 0.5 Ω and 1°.

---

## 0. Why this brief exists

Brief 113-a measured the 3D Connector's coax (pin ⌀ 0.4 mm, bore ⌀ 1.34 mm, εr 2.1) with openEMS's own coax port:

| grid | Z_L (exact 50.021 Ω) | phase |
|---|---|---|
| Cartesian, r_i/10 | 50.39–50.56 Ω | 2.4 % slow: −2.1° over 5 mm at 10 GHz |
| Cartesian, r_i/20 (upstream `Coax.m`'s own) | 50.39–50.43 Ω | 1.0 % slow |
| **Cylindrical, 141 azimuth lines** | **50.28–50.47 Ω** | **≤ 0.07°** |

A staircased round conductor keeps Z within about 0.5 Ω but slows the wave. **Only a cylindrical grid meets the series'
original coax criterion (0.5 Ω and 1°)**; brief 116 gates its Cartesian coax at 0.6 Ω and leaves the phase to this
brief. The owner decided (2026-10-05) that circuitRF writes one. Three things are missing today:

1. **A cylindrical grid.** `CsxcadWriter.Head` writes `CoordSystem="0"` and a Cartesian `RectilinearGrid`; `FdtdGrid`
   builds three Cartesian axes.
2. **Exact cylinders where they matter.** A plain `Em3dCylinder` is already written as CSXCAD's `<Cylinder>`. But a
   coax shield is usually a boolean (the 3D Connector's housing is a box with its bore subtracted), which reaches the
   writer as a kernel shape and goes out as a tessellated polyhedron (`PolyhedronReader`, PLY). Its bore is then a
   polygon, which on a cylindrical grid lies off the grid's own circles by up to the tessellation's chord error.
3. **A weighted source in cylindrical form.** Brief 116 feeds a coaxial terminal in its own TEM profile, E_ρ ∝ 1/ρ, as
   Cartesian components in a CSXCAD weight expression. On a cylindrical grid the same field is one component,
   `X="1/rho"`, on a Box in (ρ, α, z), as `Coax_CylinderCoords.m` writes it.

### What this does, and what it does not

openEMS has **one grid per run**. A cylindrical grid suits a problem that is round about one axis: a coax section, a
coax step or bead, a coax-to-coax adapter, a pin in a round bore, a cylindrical cavity. **It does not suit the 3D
Connector's Launch**, a coax meeting a microstrip board: around the coax axis the board and its line would be
staircased instead. That problem stays on a Cartesian grid and keeps the Cartesian coax error (about 1 % in phase
velocity over the connector's 4.5 mm of coax, roughly 1.4° at 18 GHz). The run reports it (§5) rather than hiding it.
A hybrid grid is not something openEMS offers.

## 1. The documentation and code this rests on

Read before writing anything, at the pinned openEMS **v0.37.0-rc3**. GPL: learn from it, never copy into `src/`.

| Source | What it settles |
|---|---|
| `matlab/examples/waveguide/Coax_CylinderCoords.m`, `Coax_Cylindrical_MG.m`, `Circ_Waveguide_CylinderCoords.m` | The grid in (ρ, α, z); PEC on ρ faces; a full 2π in α; `MultiGrid` to relieve the time step near the axis; a half coax on α boundaries |
| `CSXCAD/src/CSPrimitives.cpp`, `CSPrimBox.cpp`, and each primitive's source | **A primitive's optional `CoordSystem` attribute** (`m_PrimCoordSystem`): absent means the grid's own system (a Box on a cylindrical grid is read as ρ, α, z), `0` means Cartesian, transformed per point |
| `CSXCAD`'s `CSPrimCylindricalShell` | The shell primitive (`Radius`, `ShellWidth`, two axis points) |
| `testdata/em3d/terminal/openems/coax-cylindrical/model.xml` | Upstream-written XML of a cylindrical coax: `CylinderCoords="1"` on `<FDTD>`, `CoordSystem="1"` on `<ContinuousStructure>` and `<RectilinearGrid>`, a Box in (ρ, α, z), `<Weight X="1/rho" …>` |
| The XML 113-a dumped from upstream's Cartesian `CoaxialPort` (in its findings) | A Cartesian weight expression in x and y, masked to the dielectric |
| docs.openems.de: the cylindrical-coordinates and mesh pages | Axis singularity, MultiGrid, time step |

## 2. `R-em3d120-1` — establish before writing (a short spike, scratch only)

Before any product code, answer these with one-cell-scale runs. Each must be under a minute, written into `RESOLVED.md`:

**a. Every primitive kind `CsxcadWriter` writes, on a cylindrical grid.** Box, LinPoly, Cylinder, Sphere, Polyhedron,
PolyhedronReader (PLY), Wire and Curve, each written once **with `CoordSystem="0"`** and once without. Use a material
dump or a probe to show which grid nodes each one fills. The outcome is a table: kinds that work as Cartesian shapes on
a cylindrical grid (the writer then keeps its current output and adds the attribute), kinds that need rewriting, and
kinds that must be refused.

**b. The axis.** Run the 113-a coax with ρ starting at the pin's surface (the examples' construction: no axis, and a
larger time step), and once starting at ρ = 0 with the pin as a PEC Cylinder. Report Z, phase, cells and time step.

**c. The azimuth rule.** 113-a measured a Z error first order in Δα: +0.72 Ω at 71 lines, +0.36 Ω at 141. Add 211 and 281
lines, and `MultiGrid` at one radius, and choose the default rule from the measurements. The candidate is "the arc ρ·Δα
at the outermost conductor radius no longer than the radial cell".

**d. The replay difference.** 113-a found that its Python-dumped cylindrical XML, replayed, took a time step of
2.52e-14 s where the in-memory run took 3.65e-14 s, with the same result to 0.02 Ω. Find which written attribute
causes it. The writer must produce the file that is actually run, so this has to be understood.

Read the docs and the issue tracker first, and search again whenever a result does not make sense. Judge S entries
below −30 dB by |ΔS|.

## 3. `R-em3d120-2` — the setup chooses the grid

| # | Decision | Recommended default |
|---|---|---|
| C1 | Who chooses a cylindrical grid | **The setup, explicitly**: `OpenEms.Grid` `"Cartesian"` (the default, and what every existing file means) or `"Cylindrical"`, with `OpenEms.Axis` (`X`, `Y` or `Z`) and `OpenEms.AxisOrigin` (a point the axis passes through). Never inferred. `check` **suggests** it, as a note, when every wave port's conductors are coaxial about one line |
| C2 | The domain | **The cylinder inscribed in the air box's cross-section**, the box's two faces normal to the axis becoming the z faces. The four side faces become one ρ_max face and must share one boundary kind, or the file is refused naming them. A solid crossing ρ_max is refused unless it is metal ending on it (a shield reaching the wall) |
| C3 | The axis | **ρ starts at the surface of a PEC solid that contains the axis along the whole z range** (the examples' construction). Otherwise ρ starts at 0 with openEMS's own axis handling. §2b decides whether the second is allowed at all |
| C4 | Azimuth lines | **§2c's rule**, with `OpenEms.AzimuthLines` as an override; `MultiGrid` only if §2c shows it pays |
| C5 | Which files | **`.c3d` only.** A `.cem` is a planar layout problem and stays Cartesian |

Rule 3 of the series holds: a setup that does not say `Cylindrical` writes the same bytes as today.

## 4. `R-em3d120-3` — the grid

`FdtdGridResult` gains its coordinate system, the axis and the origin. The cylindrical build:

- **ρ**: required lines at every conductor and dielectric radius about the axis, graded with the existing ratio; the
  thirds rule does not apply to a curved edge.
- **α**: uniform, C4's count, a full 2π.
- **z**: the existing per-axis build along the axis, with the port planes and feed lines 116 requires.

The cell count, Courant estimate (on the smallest arc, ρ_min·Δα) and memory estimate follow, so `explain` and the size
refusal keep working. Brief 8's per-axis reasons (`FdtdRequiredLine`, merges) carry over, named in ρ, α, z.

## 5. `R-em3d120-4` — the writer

**a. The head.** `<FDTD … CylinderCoords="1">`, `CoordSystem="1"` on `<ContinuousStructure>` and `<RectilinearGrid>`,
lines in ρ (m), α (rad), z (m), each in round-trip form. Boundaries map rmin/rmax/αmin/αmax/zmin/zmax as the examples
use them. A full 2π in α carries no α boundary.

**b. Shapes.** Every primitive gets the treatment §2a's table assigns. In addition, **exact cylinders**:

- An `Em3dCylinder` coaxial with the grid axis is written in the grid's own system as a Box in (ρ, α, z), or as a
  `Cylinder`, whichever §2a shows fills the grid's circles exactly.
- **A boolean whose operands are primitives** (a Box or Cylinder blank, Cylinder tools coaxial with the axis), as the
  3D Connector's housing is, is written as its **operands with priorities** instead of its tessellation. The blank is
  lower; a kept tool is its own material at a higher priority; a tool that is not kept is written as the background
  material at a higher priority. CSXCAD has no booleans, but this is exactly the precedence `CsxcadWriter` already uses
  for overlaps, and it puts the bore on the grid's circle. Do this **only when it is equivalent**: every tool region is
  filled by the kept tool or by background. Otherwise keep the tessellation and say so in the run's notes.
- A cylindrical shell (a coaxial tube) is written as `<CylindricalShell>`.

These apply on a cylindrical grid only. The Cartesian output is unchanged.

**c. The weighted source.** On a cylindrical grid, 116's coaxial source becomes `X="1/rho"` on a Box in (ρ, α, z)
spanning the dielectric: the same radial field, ∝ 1/ρ, in the grid's own components. Written from the physics, never from
upstream's code. On a Cartesian grid 116's form is unchanged.

**d. Probes.** As 116 builds them, in cylindrical form (three voltage planes, two current planes, the current averaged): voltage lines along
ρ at α = 0, and current discs at the half-cells whose radius lies midway between pin and shield. The disc must lie
wholly in the dielectric (113-a R-em3d113a-2f: a contour cutting the shield reads 283 Ω instead of 51 Ω). Place probe
edges on exact grid or dual-grid values computed from the written lines, never on an unresolved tie between two lines
(113-a: a tied current-box edge snapped into the metal and read 55 % of the current).

## 6. `R-em3d120-5` — reports, refusals, UI, docs

- **The Cartesian coax note.** On a Cartesian grid, a wave port whose conductors are coaxial carries a run note naming
  the expected phase-velocity error (113-a's 1–2.4 %, by cells across the pin) and the setting that removes it
  (`OpenEms.Grid: Cylindrical`), or why it cannot be removed (the problem is not round about one axis).
- **Refusals**, before openEMS starts and each naming the fix: the axis is not coaxial with a wave port's conductors; a
  solid lies outside ρ_max; a primitive kind §2a marked unusable; a cylindrical grid on a `.cem`.
- **`explain`**: the grid kind, axis and origin, ρ range, azimuth count, the smallest arc, and the Courant estimate.
- **UI**: the setup editor's openEMS section gains Grid, Axis and Origin. The 3D view's FDTD grid overlay
  (`FdtdGridOverlay`) draws a cylindrical grid as circles and spokes about the axis.
- **User docs** (sources only, no DocGen): `em-setup.md` (the new keys and when to use them, with the Launch caveat of
  §0), `em-solvers.md` (what each solver sees of a round conductor), `em-3d.md` (the overlay).

## 7. Gate

`tests/Ui.Tests/Em3d/OpenEmsCylindricalGridTests.cs`:

1. **Lowering, no solver.** A coax section `.c3d` with `Grid: Cylindrical`. The XML's head, grid, shapes and weighted
   source agree structurally with the upstream-written `testdata/em3d/terminal/openems/coax-cylindrical/model.xml`
   (the same element kinds and attributes, as gate 5 of brief 9 compares with F0). Golden committed.
2. **The boolean's operands.** The 3D Connector's housing on a cylindrical grid is written as box plus bore with
   priorities, and its non-equivalent variant (a tool partly outside the blank and not kept) keeps its tessellation with
   a note.
3. **Replay.** 113-a's `coax-cylindrical` probe files through `OpenEmsRun`'s readers and `FdtdPortTransform` (with 116's
   current averaging): Z within 0.5 Ω of 50.021 Ω, ∠S21 within 1° of −βℓ.
4. **Byte identity.** Every existing openEMS golden (`testdata/em3d/openems-goldens`) is unchanged.
5. **Refusals** as §6, each before openEMS starts.
6. **Real run** *(openEMS; Benchmark if over 5 s)*: the coax section end to end through `circuitrf em`, cylindrical,
   within 0.5 Ω and 1°; and the same `.c3d` with `Grid: Cartesian`, whose note names the phase error it carries.

## 8. Owner check

Open a coax-section `.c3d`, set its openEMS setup to Cylindrical, look at the grid overlay, run it, and compare the result
with the same setup on Cartesian.

## 9. Scope

- No hybrid Cartesian/cylindrical grid (openEMS has none). The 3D Connector's Launch stays Cartesian.
- No conformal FDTD. No change to Palace, to `.cem` problems, or to lumped ports.
- No mode-matching port.

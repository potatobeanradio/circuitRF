# Brief 76 — interfaces, via fields, submodels and symmetry

**Series:** [3D thermal, fifth series](brief-em3d-71-thermal-overview.md) · **Tag:** `R-em3d76-n` ·
**Design note:** [`em-3d.md`](../design/em-3d.md) §9.2; overview §1b, §1e, D4
**Area:** `src/Thermal/` (interface elements, anisotropic k), `src/Design/Thermal/` (lowering, submodels),
`src/Design/ThreeD/C3dDocument.cs` (`EffectiveBlocks`, `SymmetryPlanes`), `src/Ui/ThreeD/`, `tests/`
**Depends on:** 72 (S2, Q3), 73, 74 · **Blocks:** 80 (symmetry), 81

---

## 0. What this brief delivers

The four things that make scenarios 1 and 2 **accurate and affordable** on real packages:

1. **Interface resistances** actually solved — the GaN/SiC boundary, a die attach, a solder or TIM layer
   (§1).
2. **Via fields as an effective block** — hundreds of plated barrels replaced by one anisotropic block (§2).
3. **Two-step solves** — the package coarsely, then the die region finely with the first solve as its boundary
   (§3).
4. **Symmetry planes** — model half or a quarter, see and report the whole (§4).

---

## 1. `R-em3d76-1` — interface resistances

**`R-em3d76-1a` Which contacts.** After the fragment, two touching solids share surfaces (brief 72 Q3 gave the
recipe). For each shared surface, the resistance is: the `.c3d`'s `ContactResistances` override for that
object pair, else the technology's `ThermalInterfaces` value for the two materials, else **none** (perfect
contact). The lowering reports every interface in force in the run notes and in `explain` (brief 73 §6b
already lists them; now they are applied).

**`R-em3d76-1b` The solver.** Nodes on an interface with a resistance are **duplicated**: the elements of one
side are re-pointed at the copies, and **interface elements** — the surface's second-order triangles —
couple the two sides with conductance 1/R″ (a consistent surface "mass" matrix, not lumped). A node where
three or more solids meet gets one copy per side-group, and a resistance-free contact among them keeps its
nodes shared. A zero resistance is perfect contact, not a division by zero.

**`R-em3d76-1c`** The field files carry the jump: the `.vtu` writes duplicated nodes as distinct points, so a
clip plane through an interface shows the step (brief 75 draws it with no change).

## 2. `R-em3d76-2` — via fields as effective blocks

A board under a flange carries hundreds of plated barrels, each a thin copper annulus that forces tiny
elements. A standard and well-understood approximation replaces the region with a block of **anisotropic**
effective conductivity.

**`R-em3d76-2a` The document.** `EffectiveBlocks`: `{ Name, Min, Size, Enabled }` — a box. When enabled, the
lowering **replaces** everything inside the box that is board dielectric, copper plane or via barrel (by the
role and the stackup entry they came from) with one solid whose conductivity is a **diagonal tensor**
(k_xy, k_xy, k_z) computed from what it replaced. A box that cuts through a solid of another kind (a flange, a
die) is refused, naming it. Disabled, the geometry is solved as drawn — so the owner's A/B comparison is one
toggle.

**`R-em3d76-2b` The mixture**, stated once in code and in the reference page:
- per stackup layer inside the box: the copper fraction f of via barrels (from their resolved annular areas)
  gives the layer's through-conductivity k_z = f·k_Cu + (1−f)·k_diel and in-plane k_xy from the
  standard two-phase formula for parallel cylinders in a matrix;
- a copper plane layer contributes its own k (in-plane) and its via-hole fraction;
- layers stack: k_z of the block is the **series** combination over thickness, k_xy the thickness-weighted
  **parallel** combination.
The run notes print the resulting (k_xy, k_z), the via count and f per layer.

**`R-em3d76-2c`** The solver takes a diagonal conductivity tensor per element (`src/Thermal`), gated by a slab
rotated 90° that gives the other axis's answer.

## 3. `R-em3d76-3` — two-step (submodel) solves

**`R-em3d76-3a`** A thermal setup may name `Submodel: { "From": "<setup>", "Region": "<mesh region name>" }`.
The run: solve (or reuse the current result of) the `From` setup; intersect the model with the region's box
(Gmsh's own boolean in the thermal `.geo`); mesh it with the region's size; fix the **cut faces** to the
`From` solution interpolated at their nodes; keep every other boundary as written; solve.

**`R-em3d76-3b` The check that makes it honest.** The run reports the heat flux crossing the cut faces in the
submodel against the same faces in the global solution — a large mismatch means the region is too small (the
fine detail changes the temperature at its edge) and the note says to enlarge it. The source powers inside
the region must equal the global model's, or the run refuses.

**`R-em3d76-3c`** Staleness: a submodel whose `From` result is older than the document is re-solved first;
the notes say which was used.

## 4. `R-em3d76-4` — symmetry planes

**`R-em3d76-4a`** `SymmetryPlanes` on the document: `[{ "Axis": "X", "At": 0 }]`, at most one per axis. A
plane must lie on the model's extent in that axis (the user modelled the half); the faces on it must be
insulated — a boundary condition on one is refused, naming it. What you draw is what is solved: sources carry
the power **in the modelled part**.

**`R-em3d76-4b`** Measures gain the variable `SymmetryFactor` (2ⁿ) so a full-device figure is explicit:
`Rth_full = (Tmax(ch) - Ths) / (Pdiss * SymmetryFactor)`. Nothing is multiplied silently.

**`R-em3d76-4c`** The viewer draws the mirrored halves (a toggle, on by default) with temperature mirrored
too; picking on a mirrored half reads the modelled point's value and says so.

## 5. Gates

1. **S2** composite slab: the jump ΔT = q″R″ to 1e-9, both solvers; the pair value applies from the technology,
   and an override replaces it for one contact only.
2. **Triple junction**: three blocks meeting on an edge, one resistive contact and one perfect — node copies
   counted (a counter), energy balance 1e-9.
3. **Anisotropy**: a slab with k = (1, 1, 10) conducts z at 10 and x at 1 — both orientations to 1e-10.
4. **Effective block**: the §2b arithmetic for a synthetic layer set (hand-checked numbers in the test); a
   4×4 via array solved explicitly and as a block — the difference in the flange temperature is **reported**
   in the RESOLVED entry (no tolerance gate: it is an approximation), and the block run's element count is
   below the explicit one's (a counter).
5. **Submodel**: S5 solved globally and as a submodel round the source — the submodel's peak within the
   reference tolerance, the flux-mismatch note present when the region is shrunk until it cuts the source's
   neighbourhood.
6. **Symmetry**: a symmetric source on a block modelled whole and as a half (the half's power halved) gives
   the same peak to the mesh tolerance; a condition on the symmetry face is refused.

## 6. Owner check list (Debug build)

1. On the die→heatsink model: add a die-attach contact resistance; rerun; clip through the die attach and see
   the step.
2. Draw an effective block over the via field; toggle it; compare the flange temperature and the run times.
3. Make a submodel setup round the fingers; run it; read the flux-mismatch line.
4. Cut the device in half, declare the symmetry plane, see the mirrored picture.

## 7. Scope

- **No wires or currents** (77, 78).
- **The effective block is an approximation**, reported as such in notes and docs; never enabled by default.
- Findings in `src/Thermal/RESOLVED.md` and `src/Design/RESOLVED.md`; never `CLAUDE.md`.

# Brief 74 — the thermal solver and the run

**Series:** [3D thermal, fifth series](brief-em3d-71-thermal-overview.md) · **Tag:** `R-em3d74-n` ·
**Design note:** [`em-3d.md`](../design/em-3d.md) §4.5, §6, §9; overview §1a, §1b, §1f, §1i, D2, D3, D5
**Area:** `src/Thermal/` (new), `tests/Thermal.Tests/` (new), `circuitrf.slnx`, `tests/Firewall.Tests`,
`src/Engine/Em3d/MshReader.cs` (moved), `src/Design/Thermal/` (new), `src/Design/Em3d/GmshGeoWriter.cs`,
`src/Cli/` (`em`), `tests/`
**Depends on:** 72 (Q1–Q7, the references), 73 · **Blocks:** 75, 76, 77, 80

---

## 0. What this brief delivers

A thermal setup runs, headlessly and from the GUI's Simulate, and produces a result the viewer (brief 75) can
draw:

1. **`src/Thermal`** — the numeric solver: second-order tetrahedra, assembly, a direct and a multigrid solver,
   Newton over k(T) (§1–§3).
2. **The thermal lowering** — the `.c3d` through the **existing** Gmsh writer to a mesh of solids with tagged
   sources, faces and probes (§4). **Mesh regions** are lowered for Palace setups too.
3. **The run service** — mesh, solve every sweep point, write a `DataSet` and a field series, evaluate measures,
   and a mesh-convergence check (§5).
4. **`em` runs a thermal setup** (D2), with `check`/`explain` reporting the problem's size (§6).

Wires, currents and interfaces are **not** here (77, 78, 76). A thermal problem with a wire in it is meshed
with the wire **left out** and a run note says so, until brief 77.

---

## 1. `R-em3d74-1` — the project

**`R-em3d74-1a`** `src/Thermal/CircuitRF.Thermal.csproj`, namespace `CircuitRF.Thermal`, in
`circuitrf.slnx`; `tests/Thermal.Tests` beside it. It references **CSparse** and **nothing above
`src/Engine`**, ideally nothing of ours at all: its input is its own `ThermalProblem` (nodes, elements,
per-element conductivity functions, sources, boundary terms), so it knows nothing of `.c3d`, Gmsh or material
names (overview §1f). `tests/Firewall.Tests` gains it in the no-UI list.

**`R-em3d74-1b`** `MshReader` **moves** from `src/Render/Scene3D` to `src/Engine/Em3d` (it has no
dependencies); `src/Render` uses it from there. One reader, not two. Its existing tests move with it
unchanged.

## 2. `R-em3d74-2` — the finite-element core

**`R-em3d74-2a` Elements.** Ten-node (second-order) tetrahedra from Gmsh's `Mesh.ElementOrder = 2` meshes by
default; four-node on request (`Mesh.Order: 1`). Brief 72 Q4 set the default; the RESOLVED entry quotes its
numbers. Curved (isoparametric) geometry is honoured — the mesh's mid-edge nodes are used as given.

**`R-em3d74-2b` Assembly.** Stiffness ∫k∇Nᵢ·∇Nⱼ with a quadrature exact for P2 at constant k (4-point is
not; use the 11- or 14-point rule and say which); volumetric source ∫qNᵢ; surface source (a heat-source sheet,
embedded, brief 72 Q2) ∫q″Nᵢ over its triangles; Robin (convection) ∫hNᵢNⱼ and ∫hT_∞Nᵢ; Dirichlet (fixed T) by
elimination, not by penalty. Assembly is **parallel by element colouring or per-thread triplet lists**,
deterministic: the same mesh gives the same matrix bit for bit on every run (a counter-free gate: hash it).

**`R-em3d74-2c` Solvers.** Behind one interface:
- **Cholesky** (CSparse, fill-reducing order) — the default below the crossover brief 72 Q5 measured;
- **PCG + smoothed-aggregation AMG** — managed, above it. V-cycle, symmetric Gauss–Seidel or Chebyshev
  smoothing (so the preconditioner is symmetric), aggregation by strength of connection. Stops on relative
  residual 1e-10 by default; **the iteration count and final residual go in the run notes**.
- The choice is automatic by unknown count, overridable in the setup (`Mesh.Solver: Direct|Iterative`).
  Both must agree on S5 to the reference tolerance (gate 3).

**`R-em3d74-2d` Newton over k(T).** When any meshed material's k depends on T and `Balance.KOfT` is on: Newton
with the tangent stiffness (the dk/dT term from brief 73's resolver), starting from the constant-k solution
at the nominal k, damped by halving when the residual norm does not fall, converged when the max nodal update
is below `Balance.Tolerance` × (the temperature span) **and** the residual has fallen by 1e-8. The Jacobian is
nonsymmetric: the direct path uses sparse LU; the iterative path BiCGStab with the same AMG (built on the
symmetric part). A non-converging point is reported with its last residual; it is not a runaway (that is a
brief 77 result).

## 3. `R-em3d74-3` — what the solver gives back

Nodal temperatures (°C at the boundary of the project), plus, per probe tag: max, min, area/volume-weighted
average, and for a line probe T sampled at N points along it by element interpolation (not nearest node).
A **spot** probe averages over the disk's area on its face (the triangles clipped to the disk). Heat-flow
through each fixed-T face (the reaction) — so the run can **check energy balance**: Σ source power = Σ heat
out through fixed-T and convection faces, to 1e-6 relative, reported in the notes. A run that fails the
balance says so; it is the cheapest correctness check there is.

## 4. `R-em3d74-4` — the thermal lowering

**`R-em3d74-4a`** `GmshGeoWriter` gains a **thermal mode**, not a second writer: the same solids, the same
fragment, the same names and entities file. In thermal mode:
- **no air box volume** and no background group; conductors and dielectrics alike are volumes with a
  material;
- **heat-source sheets** are embedded surfaces with their own physical group; a volumetric source is its
  solid's group;
- **face groups** for every face a boundary or probe names (the existing boundary-group machinery), and for
  `*exposed*` the set of single-sided surfaces of the fragment;
- **spot probes and line probes are not geometry** — they are evaluated on the mesh;
- **sizing**: no wavelength. The largest element is a fraction of the problem's largest side; near each heat
  source it is the source's smaller side / `Mesh.SizeFromSources`; through a thin solid at least
  `Mesh.MinThroughThickness` elements; growth by `Mesh.Grading` (the existing Distance/Threshold fields).
- ports, the air box and wires are skipped (wires until brief 77, with a note).

**`R-em3d74-4b` Mesh regions**, in every mode that meshes with Gmsh: each is a `Field = Box` with `VIn` its size,
`VOut` the current maximum and a thickness from `Grading`, added to the existing `Min` field. **A `.c3d` with
no mesh region writes a byte-identical `.geo`** (overview gate 3). openEMS writes a note naming each region it
ignored.

**`R-em3d74-4c`** The lowering records, in a `ThermalLowering` record, the tag → meaning table (which group is
which material, source, face, probe), and the material of every volume, so the solver input is built from
tags alone.

## 5. `R-em3d74-5` — the run

**`R-em3d74-5a` `ThermalRunService.Run`** in `src/Design/Thermal`, the function the GUI's Simulate and `em`
both call (CLAUDE.md: an operation in a view model is not a capability):
1. resolve the document and setup; refuse as brief 73 §6a does;
2. lower and mesh **once** (a sweep variable never touches geometry — brief 73 §5b);
3. per sweep point: resolve values, build sources/boundaries, solve (warm start: the previous point's field
   seeds Newton), evaluate probes and measures;
4. write the result.

**`R-em3d74-5b` Where results land** (em-3d.md §4.5's rules, with the solver token `thermal`): run directory
`<results>/<key>.thermal/`, **kept**, holding the `.geo`, the `.msh`, and
`postpro/paraview/thermal/thermal.pvd` — the **same layout Palace writes**, one `.vtu` step per sweep point
(point data `T_C`, cell data `material`), so `FieldRun` (brief 75) opens it with no new reader. The `DataSet`
is written as `<results>/<key>.thermal.npy` (the existing key suffix).

**`R-em3d74-5c` The `DataSet`**: one cube per probe statistic (`T:<probe>:max` …) over the sweep axes; a line
probe's `T:<probe>(s)` over sweep × distance; each measure as a named cube; `Energy:balance`; and a
`Notes` group. Real-valued, single-kind (CLAUDE.md invariant). °C.

**`R-em3d74-5d` Measures** evaluate through the one expression engine, with `Tmax(p)` etc. bound to the probe
cubes at the point. A measure that fails to evaluate is a named error for that point, not a crashed run.

**`R-em3d74-5e` Mesh-convergence check** (`Mesh.Check: true`, off by default): mesh a second time with every
size × 0.7, solve the first sweep point, and report each probe's change in °C and %. It is a report, not a
refinement loop (overview §4).

**`R-em3d74-5f` Progress and cancellation** ride `RunControl` like `em`'s; a cancelled run exits 130 and leaves
no `.npy`.

## 6. `R-em3d74-6` — `em`, `check`, `explain`

- `circuitrf em x.c3d --setup Hot [-o out.npy]` runs it (D2); `-o` moves the `DataSet` only, as for EM.
  Exit codes as `em`'s. No Gmsh is the same refusal a Palace run gets, naming the install assistant.
- `explain --analysis` adds the **size**: estimated elements and unknowns from the sizing rules, the solver it
  will choose, and an estimate of memory — the pattern `Em3dSizeEstimate` uses for Palace.
- The MCP server's `run` reaches it through `em` with no new tool.

## 7. Gates

All solver gates in `tests/Thermal.Tests`, against brief 72's data; each is small.

1. **S1** slab: P2 exact to 1e-10 (linear field); P1 too.
2. **S3** Robin: surface temperature to 1e-6.
3. **S5** spreading: peak and average source temperature within the tolerance brief 72 derived from its own
   refinement ladder, **by both solvers**, which also agree with each other to 1e-8.
4. **S4** k(T): Newton reaches the closed form (Kirchhoff) to 1e-6, and the table case to the BVP's
   tolerance; iteration count ≤ 8 (a counter, asserted).
5. **Energy balance** on S5: 1e-6 relative.
6. **Determinism**: the assembled matrix's hash is identical across two runs and across thread counts.
7. **AMG counter**: on Q6's contrast case, PCG+AMG converges in fewer than a fixed iteration count that
   brief 72 measured (assert a number, not a time).
8. **Lowering**: a thermal `.geo` for a two-solid problem with a source sheet contains no air volume, the
   source's embedded group, the face groups asked for; a Palace `.geo` with a mesh region contains one `Box`
   field; a Palace `.geo` without one is byte-identical to its golden.
9. **Run end to end** — `[GmshFact]`: a two-solid `.c3d` with a fixed-T face and a 1 W sheet, through `em`,
   gives the `.npy`, the `.pvd` with one step per sweep point (2 points), and a measure; under ~5 s or
   `Category=Benchmark`.
10. **Refusals**: no Gmsh; no fixed-T/convection; a material without `ThermalK` (named).

## 8. Owner check list (Debug build)

1. In a scratch `.c3d`: a copper block on an FR-4 slab, a 1 W source on the block's top, the slab's bottom at
   25 °C. Add the thermal setup by hand (brief 75 builds the dialog). Simulate.
2. Read the Messages output: energy balance, solver, iterations.
3. `circuitrf em x.c3d --setup Hot`; `circuitrf read` the `.npy`.

## 9. Scope

- **No wires, currents, interfaces, UI or frequency domain** (75–80).
- **No adaptive refinement** (overview §4).
- **No timing tests**; the crossover is brief 72's measurement.
- Findings in `src/Thermal/RESOLVED.md` (new) and `src/Design/RESOLVED.md`; never `CLAUDE.md`.

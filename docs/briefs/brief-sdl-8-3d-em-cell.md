# Brief SDL-8 — Simulating by EM in 3D: Palace, EMerge and openEMS

**Series:** [Schematic-driven layout](brief-sdl-0-overview-and-groundwork.md) · **Tag:** `R-sdl8-n`
**Area:** `src/Design/ThreeD/` (`C3dHierarchy.NewFromLayout`, `C3dElaborator`, `C3dComponentPorts`,
`C3dDocument`'s `C3dVariable`), `src/Design/Layout/Em/ParametricEm/` (SDL-6), `src/Design/Em3d/`
(the run services), `src/Design/Layout/SchematicSync/ConnectivitySignature.cs` (SDL-7)
**Depends on:** SDL-6 and SDL-7.

---

## 0. What this adds

SDL-6 and SDL-7 simulate a schematic by EM of its own layout with the planar solver, through a `.cem`. Many structures need a
full 3D solve: an enclosure, a connector launch, wire bonds, a lid, a thick or patterned dielectric. A
cell's 3D view (`.c3d`) can already place its layout as a live instance (`C3dInstanceView.Layout`), carry
VARs that link by name to the cell's parameters (`C3dVariable.Linked`, decision D12 of brief em3d-51),
embed EM setups, and run on Palace, EMerge or openEMS. What is missing is the link between that and
SDL-6: a `.c3d` created from a schematic-driven layout cannot yet stand as the EM model of a parameterised
cell, re-evaluated at each parameter value.

## 1. Decisions

- **D1 — The 3D view is a second kind of EM view.** SDL-6's EM-capability rule (§1) extends: a schematic's EM view is either a
  `.cem` (planar) or a `.c3d` with an embedded setup. The analysis's `emSetup=` (Path A) or the instance's
  `EmSetup` property (Path B) names a `.cem`, or a `.c3d` and one of its setups (`board.c3d#Driven1`). The solver (Palace, EMerge or openEMS) is the
  setup's own choice. Nothing else changes; `model=em` still means "this schematic, by EM".
- **D2 — Live layout instances follow the schematic; flattened bodies do not.**
  - A layout placed in the `.c3d` as a live instance is regenerated at each parameter point exactly as
    SDL-6 D2 regenerates it (in memory, hand edits kept) and the `.c3d` elaborates it at that point. The
    copper follows `w50`, `L90` with no extra work.
  - A layout **flattened** into 3D bodies is a copy: its dimensions are `.c3d` expressions now, not the
    layout's. It follows the cell's parameters only through VARs linked to them (D3). The run, `check` and
    `explain` say so for each flattened group: *"Group Board was flattened from the layout; it follows
    w50 and L90 only where its dimensions use them."* SDL-6 D4's "does not reach the copper" report covers
    a schematic VAR or cell parameter that no linked 3D VAR and no live instance uses.
- **D3 — One name drives the layout and the 3D-only bodies.** A 3D view's VAR links by name to a cell
  parameter today (`C3dVariable.Linked`). It now also links, by the same rule, to a VAR of the cell's
  schematic, so `w50` in the schematic's VAR block drives both the layout copper and any 3D-only body
  that uses it (enclosure height, connector offset, lid gap), and the designer tunes it in the schematic
  they drew (SDL-6 Path A) or through an instance override (Path B, SDL-6 D7). A 3D VAR whose name the
  schematic does not define stays the 3D view's own and is not tunable from the schematic; `explain
  --tunables` lists such VARs with the hint that adding a VAR of that name to the schematic links it.
  Where a schematic VAR and a cell parameter share the 3D VAR's name, SDL-6 D7's rule decides, stated
  once.
- **D4 — New 3D View from Layout carries the link.** For a layout linked to a schematic,
  `C3dHierarchy.NewFromLayout` places the layout live (it does today), adds one linked VAR per cell
  parameter, and takes the SDL-5 port labels through the existing Update Ports from Layout path
  (`C3dPort.FromLayout`), so the 3D ports are the schematic's ports. At each evaluation, ports are
  refreshed from the regenerated layout through the same path before the solve.
- **D5 — The cache key covers the whole 3D problem.** SDL-6 D3's key gains the elaborated `.c3d` at the
  point: every body after VAR evaluation, materials, the air box, the setup's solve-affecting settings,
  and the solver id and version. A VAR change that moves nothing still hits the cache.
- **D6 — Connectivity holds in 3D too.** SDL-7 D9's signature gains the 3D view's conductor bodies: which
  conductor bodies touch which, after elaboration (through the existing `C3dBooleans`; no second
  intersection code). A point that makes a bond wire leave its pad, a connector pin leave its line, or the
  lid touch the copper is infeasible, with the reason, and costs no solve.
- **D7 — The mesh is held for the whole run.** A 3D mesh changes with the geometry, and that change adds
  noise between nearby points that an optimiser reads as a slope. At the start of a sweep or optimisation
  the setup's mesh controls (sizes per body, refinement passes, the mesh's own size limits) are fixed at
  their start values for every point, and the run says so. Adaptive refinement that would change between
  points is turned to a fixed number of passes, the number the start point needed. SDL-7's step floor
  (D5) is raised for 3D to the larger of 2 DBU and the smallest fixed mesh size across that dimension.
- **D8 — Budgets reflect 3D cost.** A 3D solve takes minutes to hours. The counting pass (SDL-6 D5) uses
  each solver's own dry-run estimate (`run analysis=em dryRun=true`), runs as an async job in CLI and MCP,
  and the default budgets drop: `em=correct` 6, `em=direct` 15. A run whose estimate exceeds the machine's
  memory is refused before any solve, as a single 3D run is today.

## 2. Work

- **`R-sdl8-1`** EM view selection (D1) through SDL-6's provider: `.c3d` + setup resolution, the
  `EmSetup` spelling, refusals.
- **`R-sdl8-2`** Per-point elaboration of a `.c3d` with live layout instances at regenerated geometry
  (D2), in memory; the user's `.c3d` and `.clay` bytes unchanged.
- **`R-sdl8-3`** `NewFromLayout` additions (D4): linked VARs, ports through `FromLayout`; and the
  per-evaluation port refresh.
- **`R-sdl8-4`** The 3D cache key (D5) and the 3D connectivity signature (D6).
- **`R-sdl8-5`** Mesh holding (D7) per solver: which controls exist in each of Palace, EMerge and
  openEMS setups, how each is fixed, and how each solver's adaptive refinement is turned to a fixed pass
  count. Stop and report if a solver has no way to hold its mesh; that solver is then refused for
  optimisation (sweeps still run) with that reason.
- **`R-sdl8-6`** Budgets and async (D8).

## 3. Tests

- A C1-based cell with a `.c3d` made by New 3D View from Layout: two parameter values give two different
  elaborated geometries whose copper matches the planar path's regenerated layouts exactly.
- A flattened `.c3d`: the D2 report line; a linked VAR drives a flattened dimension and an unlinked one
  is listed as not tunable.
- D6: a bond wire whose landing moves off its pad at one value is infeasible, with no solve.
- D7: the mesh settings written to each solver's input are identical at two points.
- Most tests use SDL-6's stub provider and inspect solver INPUT files rather than solving. At most one
  real solve per solver, at the coarsest mesh that runs; measure it and tag `Benchmark` at or above ~5 s.
  Respect the EM-run memory watchdog practice for any real 3D run.

## 4. Docs

`em-cells.md` gains *By EM in 3D*: choosing a `.c3d` setup, live versus flattened, linked VARs, why the
mesh is held, the 3D budgets. The 3D view's reference page: what New 3D View from Layout now adds.

## 5. Gate

`dotnet test tests/Ui.Tests --no-build --filter "FullyQualifiedName~ThreeD|FullyQualifiedName~Em3d|FullyQualifiedName~SchematicSync"`,
`tests/Firewall.Tests`. Findings in `src/Design/RESOLVED.md`.

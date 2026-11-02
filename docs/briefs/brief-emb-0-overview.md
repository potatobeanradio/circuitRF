# Brief series EMB — board EM: an imported board, its parts, and one co-simulation

**Goal.** An imported board (`.kicad_pcb`, Gerber) with SMT parts placed on it can be EM-simulated and
co-simulated with its part models **in four steps, headlessly, by an agent or a person who has never seen
the EM internals**:

1. `convert` the board to a `.clay` (exists).
2. List the placed parts (`netlist` of the `.clay` already projects refdes, footprint and pads).
3. `run analysis=em` on a 3D setup that names its **component ports** — one lumped port across each part's
   own pads, built and verified by circuitRF (EMB-1).
4. `run analysis=sparam` on the generated co-simulation netlist: the board's N-port with every part model
   across its own port (EMB-1).

**Why a series.** On a 35 × 20 mm two-layer 0.6 mm FR-4 LNA board with a full top-side ground pour, ~60
vias and 15 SMT parts, none of the four steps can be done today without hand-placing port sheets on
imported copper and editing the artwork so they can touch it. Each hand step failed in a different way,
and `check` predicted none of them: sheets reaching into copper (MFEM `STable3D` abort), sheets short of
copper (open-circuit ports, or zero-diagonal aborts), a through-hole pad meshing into 167 zero-volume
tetrahedra, Gmsh's order-2 curving running 17+ minutes on one core, and a lumped port silently driven
across its width instead of across its gap. The board-level physics came out right only once every port
landed exactly on flat copper faces and was referenced to the conductor its part's current returns through.

## The briefs, in order

| Brief | What | Depends on |
|---|---|---|
| `brief-emb-1-component-ports.md` | Component ports from refdes: built on the part's own pads, verified, co-simulation netlist written; headless "3D view from layout" | EMB-2 (R-emb2-1, R-emb2-4), EMB-3 (R-emb3-1) |
| `brief-emb-2-3d-mesh-robustness.md` | 3D lowering and meshing of imported artwork: straight-sided order 2, solid vias for EM, robust meshing on, a post-mesh check that names bad elements, PEC floor on a solid plane, live Gmsh progress | — |
| `brief-emb-3-port-correctness.md` | The cylinder-port twin of F5's silent axis default; port direction in `check`; the Touchstone name the docs promise | — |
| `brief-emb-4-honest-estimates.md` | Palace element estimate on imported artwork; planar memory and AIM wording; via ρ/λ refusal in `check`; the single-threaded near-field factor | — |
| `brief-emb-5-mcp-agent-experience.md` | Long MCP calls as cancellable jobs; an estimate before committing to a run; concurrent read-only tools; one duration parser; small results for every long run; bounded refusals; plot follow that prefers the trace and can name its quantity; the schematic reference expands its tuning types; vendor parts placeable from a `.cnl` | EMB-4 (the estimates it surfaces) |

EMB-2, EMB-3 and EMB-4 are independent of each other. EMB-1 is the payoff and should land last; it is
specified now so the others are built for it.

## Already done — committed in `36ae6a845`

These fixes were made while finding the above:

| # | Fix | Files |
|---|---|---|
| F1 | `check` on a planar `.cem` no longer counts cores (minutes) when the dense matrix alone is already past the memory limit — the verdict could not change. 16+ min → 1.2 s, same verdict | `src/Engine/Mom/SurfaceMesher.cs` |
| F2 | `check`'s EM preflight takes the run's cancellation, so a cancelled MCP `check` stops instead of blocking the server's single worker | `src/Cli/Check.cs` |
| F3 | An unresolved name names the `.cnl` line the reader skipped that defined it; `check` reports read warnings when elaboration fails | `src/Core/Elaboration/Elaborator.cs`, `src/Core/Expressions/ExpressionError.cs`, `src/Cli/Check.cs`, `tests/Core.Tests/Netlist/SkippedLineCauseTests.cs` |
| F4 | MCP `create` documents the `3d` view the CLI already accepts | `src/Cli/Serve/ToolCatalog.cs` |
| F5 | A lumped port with `Positive`/`Negative` stated chose its axis with the contact query's 2-DBU box cut-off; a sheet short of its metal scored every edge pair infinite and silently kept the u axis, so a gap running along y was driven across its width. Now measured at each edge's midpoint with no cut-off, and an unmeasurable pair refuses | `src/Design/ThreeD/C3dPorts.cs` |

RESOLVED entries exist for F1–F3 (`src/Engine/Mom/RESOLVED.md`, `src/Cli/RESOLVED.md`,
`src/Core/RESOLVED.md`). F5's entry is in `src/Design/RESOLVED.md`. **F5 still needs its regression test** — EMB-3 R-emb3-1 owns it,
together with the cylinder-port branch F5 left alone.

## Rules for the whole series

- **No long EM runs as tests.** A gate builds a small fixture (two pads and a gap, one through-hole pad,
  one via) and asserts counts, names and directions — never wall-clock time.
- Run only the targeted test classes each brief names.
- Findings go to the nearest `RESOLVED.md`, never `CLAUDE.md`.
- Every refusal or warning added by this series names the object (refdes, port, conductor, net) and the
  remedy, and is raised by `check` whenever the inputs to decide it exist before meshing.

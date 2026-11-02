# Brief EMDC-0 — A DC row for every 3D EM result: overview

**Series:** EMDC-0 overview · EMDC-1 the conduction solve · EMDC-2 the DC row in every 3D result · **Tag:** `R-emdc<n>-<k>`
· **Origin:** owner request, 2026-10-23 — a user biasing a circuit through an EM result needs that result's `.sNp` to
carry a correct 0 Hz row, and Palace, openEMS and EMerge results carry none.

---

## 1. The defect

**A 3D EM result has no 0 Hz row, and a circuit run then biases through the wrong answer without saying so.**

- **All three 3D solvers drop 0 Hz.** The sweep is cut to its positive frequencies in the one place every 3D problem is
  built — `Em3dGenerator.Go` (`src/Design/Layout/Em3d/Em3dGenerator.cs`, the `.clay` route) and
  `C3dProblemAssembly.Assemble` (`src/Design/ThreeD/C3dProblemAssembly.cs`, the `.c3d` route) — with the note *"The
  sweep's 0 Hz point is not in the 3D problem: a full-wave solver has no DC solution to give it."* The note is true: no
  full-wave solver has a DC answer to give. The `.sNp` header says nothing about it.
- **A circuit's DC solve then reads the file's lowest frequency as if it were DC.** `LinearDcEngine` and
  `NonlinearDcEngine` stamp every component at ω = 0; `SnpModel`'s default `OutOfRangePolicy.WarnClamp` holds the first
  stored point. The interpolator's warning goes to `RFNetwork.OnWarning`, which nothing in `src/` subscribes to.
- **Measured (2026-10-23), `circuitrf dc`, Touchstone files starting at 1 GHz:**

  | Element in the file | Correct DC | circuitRF result |
  |---|---|---|
  | Series 1 pF DC block, 5 V source → 50 Ω load | load at 0 V | **load at 5 V** |
  | 10 nH choke to ground, behind 100 Ω from 5 V | 0.005 V | 0.005 V (right by luck) |
  | Shorted quarter-wave stub to ground, behind 100 Ω | ≈ 0 V | 0.0025 V (right by luck) |

  A DC block read as a short sends bias where it does not go: the one mistake a bias point cannot survive.

**circuitRF's planar engine already does this correctly** (LF1, `src/Engine/Mom/PlanarDcSolve.cs`). It takes 0 Hz off
the full-wave grid and solves the meshed structure as a resistor network, so a MIM cap is open and a trace is its own
resistance. Read that file's header before starting this series: its port model (§3 below) is the one this series uses.

## 2. The decision: circuitRF solves DC itself, on its own mesh, once per run

No full-wave solver can produce this row, so this series does not change Palace, openEMS or EMerge in any way. At
ω = 0 the problem is **steady conduction**: ∇·(σ∇φ) = 0 inside the conductors, with the dielectrics and air carrying
nothing. That is the same operator circuitRF's thermal solver already assembles for k, and the electrothermal code
(brief-em3d-77/85, `src/Thermal/Electrothermal/`) already solves it for φ on a conductor sub-mesh with port contacts.

- **One solve per run, from the solver-neutral `Em3dProblem`.** Both routes (`.clay` and `.c3d`) end in an
  `Em3dProblem`, so a DC solve that reads only that problem covers every 3D setup and all three solvers with one
  implementation. A Compare run (several solvers) solves DC once and every leg carries the same row.
- **Its own mesh: conductors only, through Gmsh's thermal mode** (`GmshGeoWriter.WriteThermal`, brief-em3d-74). This
  is not the EM mesh: in EM a conductor is a void, while here it is the only thing meshed. openEMS does not need Gmsh,
  so an openEMS-only machine gets no DC row, and the run says so (EMDC-2).
- **The result is a fully stated network, never a guess about connectivity.** Connectivity and resistance come out of
  the same solve on the same mesh. PEC is an equipotential, a lumped R is a resistor, a lumped L is a short and a lumped
  C is an open.

## 3. The port model at DC (what EMDC-1 implements, stated once here)

As in `PlanarDcSolve`, **a port is a terminal PAIR** at DC: its positive object's contact and its negative object's
contact, with neither terminal assumed to be ground. A PEC air-box face is a conductor (ground) and every other face is
insulating. **S comes straight out of a terminated solve**: every port is a branch of resistance Re Z₀ₖ between its two
contacts, port j is driven by an EMF of 2√Z₀ⱼ, and S_kj = δ_kj − √Z₀ₖ·I_k. This stays finite for an ideal short, an
ideal open and a zero-ohm PEC tie, so there is never a Y or Z to invert at a singular point.

## 4. Owner decisions (taken as the defaults below; each is one sentence to change)

- **O1 — what asks for the DC row:** a sweep whose expanded grid contains 0 Hz, which is the planar rule (`wantDc` in
  `PlanarSolve`). No new setup key. A log sweep cannot contain 0 Hz and gets no DC row, as on planar.
- **O2 — when it cannot be produced** (no Gmsh, or a port with no DC meaning, such as a hollow waveguide's): the run
  still succeeds with its RF result, the file has no 0 Hz row, and the run's **Warnings** say why and what fixes it.
  It is not a refusal, because the RF answer is still right.
- **O3 — Compare:** the DC row is identical across solvers by construction, so Compare's agreement statistics exclude
  0 Hz and say so once.

## 5. Out of scope

- **Circuit-side warnings** when an S-parameter file without a DC row is evaluated at 0 Hz (`RFNetwork.OnWarning` has
  no subscriber). The owner has ruled these out for this series.
- Sub-floor points (planar's LF2): very low but non-zero frequencies stay with the full-wave solvers.
- Eigenmode, electrostatic, magnetostatic and thermal problems: no `.sNp`, so no DC row.
- Any change to the full-wave lowerings, meshes or results at non-zero frequencies. Every existing 3D golden stays
  byte-identical for a sweep that does not contain 0 Hz.

## 6. Gates (both briefs)

Targeted test classes only (`--filter`), never the full suite. `tests/Thermal.Tests` for EMDC-1's solve, and the
`GmshFact`/`PalaceFact`/`OpenEmsFact`/`EmergeFact` classes each brief names. Findings go in `src/Thermal/RESOLVED.md`
(the solve) and `src/Design/RESOLVED.md` (the lowering and the run), never in a `CLAUDE.md`. Edit doc sources only:
the owner regenerates `docs/user` at the end of the series. No commits.

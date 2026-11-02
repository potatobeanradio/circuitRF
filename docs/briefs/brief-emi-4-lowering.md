# Brief EMI-4 — The lowering: an `Em3dProblem` as an EMerge script

**Tag:** `R-emi4-n` · **Series:** EMI (see `brief-emi-0-overview.md`) · **Depends on:** EMI-1
**Area:** new `src/Design/Em3d/Solvers/EMerge/EMergeScriptWriter.cs` (+ partials), `EmSetupPersistence.cs` (an
`EMerge` settings block), the EMerge descriptor's `Refusal`/`Prepare`, `src/Cli/ExplainEm3d.cs`, `check`. Findings in
`src/Design/RESOLVED.md`.
**Out of scope:** wave ports (EMW), fields and radiation output (EMI-7), running it (EMI-5).

---

## 0. The input file is a Python program, so two rules come first

**R-emi4-0a — no user text is ever executed.** Object, material, port and setup names come from user files. The script
refers to everything by **index**; a name appears only inside a Python string literal written by **one** escaping
function (`PyStr`) that emits `"…"` with every non-printable-ASCII character as `\uXXXX`. A source-scan test forbids
string interpolation into the script anywhere else. Numbers are written with the invariant culture and `R`/`G17`.

**R-emi4-0b — the script is data-driven.** The writer emits a fixed driver program plus one data literal (or a
sibling `problem.json` the driver reads — prefer this: the driver then becomes a constant that can be tested once, and
the per-run file is pure data). The driver is versioned with the pinned EMerge; a change to it is a reviewable diff of
one file.

## 1. `R-emi4-1` — the settings block

`.cem`/`.c3d` setups gain an optional `EMerge` block, mirroring `Palace`'s: `Quality` (the same preset names), `ElementOrder`
(1 | 2, default 2), `AdaptiveSweep` (default true) with `Tolerance` (default 1e-3 on |S|) and `MaxSamples`, and
`AdaptiveMesh` (passes, default by quality). Defaults live in one place, as `PalaceSettings`' do
(`EmSetupPersistence.cs:300`). Absent block = defaults; nothing is written until a value differs.

## 2. `R-emi4-2` — the mapping table (fill it in, measure it, then build it)

First task: a table in `src/Design/RESOLVED.md`, one row per `Em3dProblem` feature, saying how EMerge 3.0 expresses it,
checked against EMerge's own source and one tiny run each. Starting point:

| circuitRF | EMerge (to verify) | If not expressible |
|---|---|---|
| `Em3dBox`, `Em3dCylinder`, `Em3dSphere` | native primitives | — |
| `Em3dExtrudedPolygon`, `Em3dSweep` | a polygon extruded / swept | via STEP (next row) |
| `Em3dShapeSolid` (kernel B-rep: STEP imports, fillets, booleans) | **write it to STEP** with circuitRF's own STEP writer and import it with EMerge's STEP reader, one file per solid | — |
| construction order ("later wins", metal over dielectric, `em-3d.md` §6.3a) | EMerge's priority ordering | refuse if not reproducible |
| `Em3dSheet` (zero-thickness conductor) | a plate with PEC, or a thin-conductor boundary when it has σ | — |
| metal volume with σ | surface-impedance boundary on its faces (interior tets skipped, as EMerge does for good conductors) | — |
| dielectric εr, tanδ, μr | material | frequency-dependent (Cole-Cole tissue) → refuse by name (SAR is openEMS's) |
| `Em3dLumpedElement` (RLC in a port gap) | lumped-element boundary with an impedance function of f | — |
| lumped port (rectangle; coaxial annulus) | lumped port (rectangle); coaxial: a coax port or refuse | refuse coax by name if needed |
| air box faces: PEC / PMC / absorbing / symmetry | PEC default, PMC, absorbing boundary (or PML), symmetry | — |
| `Em3dMeshRegion` | mesher size fields per region/face | — |
| label volumes, voxel lattices | — | refuse by name |
| problem types: Driven, Eigenmode | sweep / adaptive sweep, eigenmode | — |
| Electrostatic, Magnetostatic, Thermal | — | refuse (Palace only / native thermal), as openEMS is refused today |
| wave port | — | refuse **naming EMW** and the solvers that run it |

**Port polarity.** circuitRF's port direction (`Em3dPort.Direction`, negative end = the ground set's) maps onto EMerge's
lumped-port direction axis. Get it right once, with a test that a symmetric thru gives S21 with the phase Palace gives.

## 3. `R-emi4-3` — mesh quality

Map the shared `Quality` presets to EMerge's resolution (a fraction of the wavelength), boundary sizes on conductor
edges (from the setup's existing edge-refinement settings) and adaptive-mesh passes. **Calibrate on the Cavity**: pick
the Draft preset so that the notch frequency is within one 5 MHz step of the converged value (EMI-0 §2 shows 9.8k tets
already is). Record the calibration.

## 4. `R-emi4-4` — refusals, check, explain

Every refusal in the table is the descriptor's `Refusal`, worded like the existing ones (what was asked, why this solver
cannot, which solver can). `check` reports them for an EMerge setup with no solver installed; `explain` on an EMerge
setup reports the lowered objects, the mapping chosen for each, the settings block resolved, and the estimated
unknowns (EMerge's own 0.2 MB per unknown guide gives the memory line).

## 5. Tests (targeted, none needs EMerge)

1. Goldens: the Cavity, Cavity with post, and 3D Connector Launch lowered to `problem.json` (byte for byte).
2. `PyStr`: quotes, backslashes, newlines, non-ASCII and control characters round-trip through a Python-literal
   parser written in the test (no Python process); and the source scan forbidding other interpolation.
3. One refusal test per refused row.
4. The settings block round trip; absent block = defaults; no write when unchanged.
5. A **driver** test that runs only when EMerge is installed (Skipped otherwise): the driver parses each golden and
   builds the EMerge model without solving (mesh only), so a mapping that EMerge rejects is caught.

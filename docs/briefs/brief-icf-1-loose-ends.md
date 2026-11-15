# Brief ICF-1 — Loose ends: references, run notes, landings, mesh-only, two-result plots

**Series:** ICF-0 … ICF-6 · **Tag:** `R-icf1-<n>` · **Depends on:** —
**Area:** `src/Design/Layout/Em/EmPlanarComponentPorts.cs`, `src/Design/ThreeD/C3dCellCuts.cs`, `src/Engine/Mom/PlanarSolve.cs`,
`src/Engine/Mom/SurfaceMesher.cs`, `src/Design/Layout/Em/EmSnpProvenance.cs`, `src/Design/Em3d/Solvers/EMerge/EmergeRun.cs`,
`src/Cli/PlotVerb.cs`, `docs/user/src/reference/em-setup.md`.

## The gap

A field test of component ports on a real kit found a set of small places where a run, a note or a check said something
other than what happened. Each is independent; each is one requirement and one test.

## Requirements

**R-icf1-1 — A Reference that fails its solver's construction falls back to ground.** PR #70 moved a pin to ground when the
shared decision in `EmCellCutout` cannot build its Reference. A pin that passes that decision but then fails the solver's
own construction is still left out: Planar's `Construct` (the strip and delta gap, `EmPlanarComponentPorts.cs` around
"brief-icp-4 R-icp4-2") returns null with `gapWhy`, and 3D's `C3dCellCuts.GapSheet` returns null → `Refused("unreachable")`.
Both now build the pin as an Uncalibrated Port at its face against the run's ground, exactly as the shared fallback does,
with the same sentence shape (`<pin>: <why> <pin> is built against Ground instead of its Reference <ref>: …`). The
reference pin then takes a port of its own when nothing else returns through it (PR #70's second sentence). "unreachable"
is replaced by the reason the sheet was not built. One fallback rule, called from both places, not two copies.

**R-icf1-2 — The edge-port crossover note only when an edge port switches.** The LFE note at the end of
`PlanarSolve.Run` ("Edge ports are ground-referenced below … and series gaps from there up") prints whenever a de-embedded,
ground-return sweep straddles `GroundedEdgePortBelowHz`. Uncalibrated ports are grounded at every frequency and never
switch. Print it only when at least one port is a calibrated, ground-referenced edge port.

**R-icf1-3 — The adaptive note says when nothing was compared.** With a sweep too short to refine (5 points or fewer, or
whatever the actual threshold in the adaptive path is — read it), the note says `CONVERGED` though no interval was ever
checked against the interpolant. Say "Adaptive frequency sampling compared nothing: the sweep has N points, too few to
refine, so every point was solved." and no verdict.

**R-icf1-4 — A non-converged adaptive run warns and names the frequency.** "DID NOT CONVERGE" is a note today. Make it a
warning, and name the interval (or the frequency) where the worst disagreement sits, beside the |ΔS| it already gives.

**R-icf1-5 — The mesher's note counts the local grid.** The sentence "Cells per wavelength and Mesh frequency do NOT set
this mesh" (`SurfaceMesher.cs`, the EFAN/M3 branch) is decided from the base pitch only. With the local grid on, the base
blocks ARE λ_g/`CellsPerWavelength` at the mesh frequency, so the mesh frequency does move the mesh. When the local grid
is on, the note says what sets its base block (the mesh frequency, and the value it took) and what sets the refinement.

**R-icf1-6 — The header's mesh line says what it is.** `circuitRF-EM mesh:` carries `MeshHash`, a hash of the mesh
SETTINGS (`EmSnpProvenance.MeshHash(PlanarMeshSettings)`), so two layouts with the same settings read the same value. The
staleness check depends on it, so it keeps its value. Rename the header's label to say it is the settings
(`mesh settings:`), keep reading the old prefix, and add the realised unknown count to the existing header so two results
can be compared on the mesh they actually solved. A file written before this reads unchanged.

**R-icf1-7 — A cut-port landing is never silent in 3D.** `Em3dGenerator.GenerateCutPorts` returns `Notes` and
`PortsMoved` (the `ReturnWalk` decision: which level a sheet landed on, or that it fell to the floor); `C3dCellCuts.Ports`
reads `g.Problem` and `g.PortsLeftOut` and drops the rest. Forward each note under the layout frame's path, as the
function already does for `build.Notes`. `check`'s 3D pin line then says where each sheet lands (`lands on Metal1, 4 µm
down` / `lands on the ground floor, 755 µm down`) instead of `verified`. No change to where a sheet lands (that is
ICF-6).

**R-icf1-8 — EMerge `--mesh-only`.** `EmergeRun.StartInfo` passes `SolveMode = "solve"` unconditionally, so a mesh-only run
on an EMerge setup is refused. The driver script already has a `mesh` mode. Wire `MeshOnly` to it and report the counts
the `OnMeshed` hook already receives, in the shape the other solvers' mesh-only report uses. Read the driver first to
confirm the mode's output.

**R-icf1-9 — `plot` over more than one result.** `--trace` takes `src=<file>` per trace, bound to the existing
`TraceConfig.SourcePath` (`src/Render/DataDisplay/Models/DataDisplayConfig.cs`); a trace without `src=` takes the
command's result as now. A relative `src=` resolves against the working directory, as the positional result does. A
missing file is a refusal naming it. `--write-cdd` writes the per-trace source.

**R-icf1-10 — The docs say what cutting two coupled parts costs.** One paragraph in `em-setup.md` §cell instances: two cut
instances are coupled only through what the EM still solves, so the coupling between the two cut parts themselves is lost
(the field test measured it ~40 dB lower on two spirals 75 µm apart), and when that coupling matters, cut neither, or
only one. Regenerate the one edited page.

## Gates

One test per requirement, all routine and solve-free except where a fixture already solves:
1. A pin whose faces pass the shared check but whose Planar strip cannot be built builds against ground with the sentence;
   the 3D equivalent likewise; no `unreachable` remains in the source.
2. An all-uncalibrated de-embedded sweep across the crossover has no LFE note; one calibrated edge port brings it back.
3. A 4-point adaptive sweep's note says it compared nothing.
4. A non-converged adaptive run returns a warning naming a frequency.
5. A local-grid mesh's note names the mesh frequency as a setter of the base block.
6. The header carries the new label and the unknown count; a file with the old prefix reads and compares as before.
7. A 3D cut-port setup whose sheet falls to the floor reports the landing in `check`.
8. An EMerge mesh-only run's arguments carry the mesh mode (assert the start info; no EMerge in the test).
9. `plot` with two `src=` traces writes a `.cdd` naming both sources.
10. DocGen's page check passes on the edited page.

## Not here

Where a cut-port sheet lands (ICF-6). Any change to what the local grid builds (ICF-4).

## Completion note (2026-11-15)

All ten built; detail in `src/Design/RESOLVED.md` §brief-icf-1. Where it differs from the requirements:
- R-icf1-1: the fallback is carried into the cut through an in-memory `EmSetup.CutGroundFallbacks`, so `EmCellCutout` stays the
  one place it is decided; in 3D it is asked after the cut elaboration (`C3dCellCuts.FallBackToGround`).
- R-icf1-6: the realised unknown count is written on planar results; a 3D result's header already carries its solver's mesh lines.
- R-icf1-9: the key is `file=`, because `src=` already names a WSProbe trace's source probe.
- R-icf1-8: the 3D view's Mesh button is unchanged (Palace only); `--mesh-only` meshes an EMerge setup.

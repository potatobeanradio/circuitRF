# Brief 9 — the openEMS backend: CSXCAD XML, the run, and circuitRF's own port transform

**Series:** [3D EM, first series](brief-em3d-0-overview.md) · **Tag:** `R-em3d9-n` ·
**Design note:** [`em-3d.md`](../design/em-3d.md) §3, §4.2 (openEMS section), §5.2, §5.3 ("circuitRF computes openEMS's S-parameters itself"), §6.4, §6.5, §6.6 (wires for openEMS)
**Area:** `src/Design/Em3d/CsxcadWriter.cs`, `OpenEmsRun.cs` (new), `src/Engine/Em3d/FdtdPortTransform.cs`
(new), `src/Design/Em3d/Em3dRunService.cs`, `src/Ui/Layout/Em/EmSetupEditorViewModel.cs`
**Depends on:** 1, 6, 8 (3 through 8; 4 for wire gates) · **Blocks:** 10

---

## 0. What this brief delivers

```
circuitrf em via.cem                     # Solver3D: OpenEms
  → Em3dGenerator            (brief 3)    Em3dProblem
  → FdtdGrid                 (brief 8)    lines
  → CsxcadWriter                          via.openems/model.xml       (deterministic text)
  → openEMS, once per port                via.openems/p<k>/<probe files>
  → FdtdPortTransform                     U, I per port per excitation → Z → S
  → results/via.openems.s2p + via.openems_em.npy
```

**openEMS runs natively on Windows** (§3), so this brief is what gives a Windows user a 3D solver
before the next series lands.

---

## 1. `R-em3d9-1` — the rule of §5.2

**`R-em3d9-1a`** circuitRF writes the CSXCAD XML **as text**, runs the `openEMS` executable, and reads
the text files it writes. **Its Python and Octave interfaces are never used by product code.** They
link GPL code into the caller (§5.2). Brief 6's firewall test holds it.

**`R-em3d9-1b` The structural oracle is F0's XML** (brief 1 §2), produced by upstream's own
interface during the spike. The writer's output for case B must match F0's XML **structurally**:
the same property kinds, primitive kinds, port elements and probe types, compared by a test that
parses both. It need not match text for text, since coordinates and names legitimately differ.
Where they differ in structure, F0's file is right until shown otherwise.

---

## 2. `R-em3d9-2` — the CSXCAD writer

**`R-em3d9-2a` Byte-deterministic** (overview rule 2), like brief 7's writers.

**`R-em3d9-2b` Names and priorities** (§6.4, §6.5). Each named solid becomes one CSXCAD
**property named after it**, holding its primitives. The **priority** is the solid's construction
order (brief 3 §1d), so the higher order wins the cell. It is written from the problem, never
re-derived.

**`R-em3d9-2c` Primitives.** Extruded polygons, boxes and cylinders map to CSXCAD's own primitives.
**Hexagonal wires become polyhedra** from brief 5's `Em3dTessellation` (the mitred prisms; §6.6: *it
does not rely on a sweep in CSXCAD*). Round wires use CSXCAD's own round primitive **if** F0 found
it suitable; otherwise they are polyhedra too. **A wire whose section is below the local grid cell**
is written as a thin conductor with the matched perimeter (§6.6), and the run's notes say which
wires were treated that way. F2's viewer will show it (§8.5).

**`R-em3d9-2d` Materials, and the two places FDTD cannot say what FEM says.** Each of these is
**reported on the run, never hidden**:
- **Dielectric loss.** A constant-conductivity FDTD material reproduces a given tanδ **at one
  frequency only**, because tanδ ∝ 1/f for a fixed σ, while Palace holds tanδ constant. The writer
  sets σ = 2π·f_c·ε₀·εr·tanδ at the band centre f_c and states f_c in the notes. **This is the
  largest expected source of FEM/FDTD disagreement on lossy substrates**, and brief 10 must say so
  beside the difference. A broadband dispersive fit (a multi-pole Debye model) is a later brief.
- **Conductor loss.** Sheets become conducting sheets (σ and thickness), which is the FDTD
  counterpart of Palace's finite-conductivity boundary (§3). **Solid conductors** thick enough to
  span several cells are written as perfect conductors unless F0 found a better representation, and
  the notes name them. A lossless via barrel in FDTD against a lossy one in FEM is a real,
  explainable difference, but only if it is written down.

**`R-em3d9-2e` Grid.** Brief 8's three line arrays, written as the rectilinear grid, in metres
(unit 1), with no rescaling.

**`R-em3d9-2f` Boundaries.** `Absorbing` faces become PML with brief 8's `PmlCells`, `Pec` faces
PEC, and `Pmc`/`Symmetry` their FDTD equivalents. Mur is **not** used by default, and the notes
say which boundary each face got.

**`R-em3d9-2g` Excitation.** A Gaussian pulse covering the band: centre (f_min + f_max)/2,
half-width (f_max − f_min)/2, as openEMS's Gaussian is specified. End criterion and maximum time
steps come from the openEMS section (§6).

**`R-em3d9-2h` Lumped ports.** Each port is written the way F0's upstream-generated XML writes one:
the port's resistance, its excitation (on the excited port only), a **voltage probe** along it and a
**current probe** around it. Probe names are `port<k>_u` / `port<k>_i`, deterministic.

---

## 3. `R-em3d9-3` — running it: one run per port

**`R-em3d9-3a` An N-port needs N runs.** openEMS excites one port per simulation, and S_ii needs
every port excited. The runs are **sequential** in `p<k>/` subdirectories, since each run already
uses every core. `explain` (brief 5/8) and the progress line both say "port k of N", so a 4-port
taking four times a 2-port's time is expected rather than alarming.

**`R-em3d9-3b` The grid is written once.** The N runs share `model.xml`'s geometry and differ only
in which port carries the excitation. Write N files that differ in exactly that element, and test
that they do (a diff of two port files touches only the excitation).

**`R-em3d9-3c`** Run via `SolverDiscovery.OpenEms`'s absolute path, with the thread count from
`EmSolveCores`. It runs under `RunControl`, is killed as a tree on cancel, and reports progress
from openEMS's own time-step output. Version check first (brief 6).

**`R-em3d9-3d` The end criterion.** A run that stops at its maximum step count **without reaching the
energy end criterion** has not converged. The result is still written, and it carries a **warning**
stating the energy level reached against the criterion. It is never presented as converged.

**`R-em3d9-3e` Time step.** openEMS prints the time step it chose. Record it in the provenance,
beside brief 8's Courant estimate. A ratio outside [0.5, 1.0] is an internal warning (the grid or
the estimate is wrong), reported and written to `RESOLVED.md` if seen.

---

## 4. `R-em3d9-4` — the port transform, in the numeric layer (§5.3)

**`R-em3d9-4a`** `FdtdPortTransform` in `src/Engine/Em3d/`: pure functions from time series to
frequency-domain quantities, with no files and no processes. `OpenEmsRun` reads the probe files and
hands it arrays.

**`R-em3d9-4b` Each probe's own time column is used** (overview §1f). The voltage and current
probes are sampled on interleaved half steps, and a transform that assumes a shared time axis
produces a phase error growing with frequency. **The DFT is evaluated at the requested frequencies
directly** (Σ x(tₙ)·e^(−jωtₙ)·Δtₙ over each probe's own tₙ), not by an FFT on an assumed uniform
grid. The cost is O(N_t·N_f), which is negligible here.

**`R-em3d9-4c` From U and I to S, through RfCore.** With every port excited once, the transform
has the N×N matrices U(ω) and I(ω) (column k from run k). **Z = U·I⁻¹**, and S follows from
**RfCore's existing Z-to-S conversion** with the setup's reference impedances. This means complex
Z0 is handled by the same definition as planar results, with no second definition of a wave. It
requires that the current probe measure current **into the structure**; F0's Q7 confirms the sign
and placement for the pinned version, and the transform's comment cites it.

**`R-em3d9-4d`** A singular I matrix at some frequency (a port that carries no current, a
disconnected structure) is an `EngineError` naming the port and the frequency. It is never a NaN in
the Touchstone file.

---

## 5. `R-em3d9-5` — results

**`R-em3d9-5a`** `<key>.openems.sNp` and its `.npy`, by the rule brief 7 §5 set (whichever was built
first owns it; overview §2). The run directory `<results>/<key>.openems/` is kept.

**`R-em3d9-5b`** Provenance: solver, version, grid (cells per axis and total), the smallest cell's
feature, Δt (openEMS's and the estimate), steps run, energy reached against criterion, f_c for
dielectric loss, and the list of solid conductors written as PEC.

---

## 6. `R-em3d9-6` — the openEMS section (run part) and the panel

`EndCriterionDb` (default −50), `MaxTimeSteps`, plus brief 8's grid fields. The panel's *Solver*
picker (brief 7 §6b) shows these when openEMS is picked. Pixels are not verified from this session.

---

## 7. Gate

`tests/Ui.Tests/Em3d/OpenEmsBackendTests.cs` and `tests/Engine.Tests/Em3d/FdtdPortTransformTests.cs`.

1. **Transform, synthetic, no openEMS.** A series-R-L-C between two ports with a known analytic S:
   synthesise U(t) and I(t) by convolving the analytic response with the Gaussian. **The current is
   sampled at t + Δt/2.** The transform recovers S to 1e-6. **Then assert that the same data with
   the offset ignored fails** by a phase error growing with f. That proves the test is not vacuous.
2. **Transform through RfCore**: a complex Z0 gives the same S as `RFNetwork.ZToS` on the analytic Z.
3. **Singular I** is an error naming port and frequency.
4. **Writer golden**: `model.xml` for the microstrip and the via transition, byte for byte.
5. **Structure vs F0**: case B's XML has the same property, primitive, port and probe kinds as F0's.
6. **Per-port files** differ only in the excitation element.
7. **Closed form** (§10 Validation): brief 7 gate 6's homogeneous stripline, group delay within
   1 % of ℓ·√εr/c. *(openEMS; tag Benchmark if > ~5 s.)*
8. **F0 reference**: circuitRF's via transition against F0's openEMS run: |S21| within 0.1 dB and
   ∠S21 within 2°, or F0's own grid-convergence spread, cited. *(openEMS; Benchmark.)*
9. **Unconverged run** (a tiny `MaxTimeSteps`) is written with the energy warning. *(openEMS.)*
10. **Cancellation** mid-run: exit 130, no `.sNp`, no child left.
11. **Notes present**: dielectric-loss f_c, PEC solid conductors, sub-cell wires.

## 8. Scope

- **Lumped ports only.** Waveguide and microstrip ports come with wave ports.
- **No broadband dispersive dielectric fit** (§2d); later.
- **No near-to-far-field.** Later, with radiating setups.
- **No automatic grid-convergence re-run.**

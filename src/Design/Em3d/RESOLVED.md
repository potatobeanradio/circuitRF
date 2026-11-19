# `src/Design/Em3d` — resolved briefs

## PWL-2 — the wall port on Palace, measured before building (2026-11-18)

`docs/briefs/brief-pwl-2-palace-spike.md`. Hand-built `.c3d` problems (a lumped port in a gap from a PEC end wall to the
line), run through `circuitrf em` on Palace 0.18.1 at Standard quality with no refinement passes, and calibrated with the
shared `LineCalibrator` (PWL-1) in a scratch harness. Fixtures S and C are in `testdata/pwl/`. Every comparison reports
the complex VSWR between answers (`RfHelpers.VswrFromGamma`) beside |ΔS|, and pairwise between results, not only
against a reference.

**The one-line result:** the calibration works on Palace with independently meshed standards. It takes the port match
from a raw VSWR of up to 1.39:1 to 1.0003–1.005:1 on the stripline. What is left against a closed form is Palace's own
phase-velocity error, not the wall port.

### Q1 — the gap does not matter once calibrated; cost picks it

Stripline, five gaps, the same standards each time. Calibrated results against each other: worst VSWR 1.0022–1.0094,
worst |ΔS21| 5e-5–1.1e-3. Against the exact line, every gap sits at |ΔS| ≈ 2.3e-2: the ε_eff error they all share.
Raw, the gap matters a great deal: at 10 GHz the raw VSWR is 1.99 at w/40 and 1.09 at w/5.

| Gap | Fraction | Solve time per standard | Peak memory |
|---|---|---|---|
| 42.9 µm | w/40 | 187–323 s | 7.0 GB |
| 85.9 µm | w/20 | 138–219 s | 4.9 GB |
| 100 µm | h/10 | 51–139 s | 4.5 GB |
| 171.8 µm | w/10 | 30–66 s | 3.3 GB |
| 343.6 µm | w/5 | 28–67 s | 2.4 GB |

A port sheet is meshed at least four elements across its short side, so a small gap is expensive.
**Decision: g = min(w, h)/5**, at the cheap end of the flat region. It is never a user setting.

### Q2 — independent meshing is enough; the extruded port block was not built

Third-line residual (the de-embedded line's |S11| in its own Z_c, which must vanish) on independently meshed standards:
stripline ≤ 1.8e-3 (≤ 8.6e-4 on a finer mesh), microstrip ≤ 4.8e-3, coupled pair (modal) ≤ 8.1e-3. Halving the largest
element changes the calibrated port match by VSWR 1.004. Box consistency residuals are 1e-7 to 5e-5.
**Decision: (a), independent meshing.** Forcing an identical port region was not needed and was not measured.

### Q3 — C′ from kernel A on the wall's cross-section

| Source | Stripline | Microstrip | Cost |
|---|---|---|---|
| Palace electrostatic, two standards differenced | +4.13 % | +3.20 % | 6–11 s per standard, Palace only |
| Kernel A (2D quasi-static), default mesh | −3.52 % | +0.03 % | 27–55 ms |
| Kernel A, 96 cells across | +0.20 % | — | 0.47 s |

Palace's electrostatic C′ reads high by the same few percent as its driven ε_eff, consistent with an under-resolved
zero-thickness edge in a potential-formulation FEM. Kernel A grades its mesh into the edge.

**Decision: kernel A**, for every solver alike:
- Grounded metal other than the floor (a second plane, a lid, a side wall) is a grounded conductor, and C′ is the
  Maxwell matrix's diagonal block for the group's conductors.
- **The cross-section mesh must be sized for the widest grounded conductor.** Kernel A's single-strip default put six
  cells across a 38 mm top plane and read −3.5 %.
- For a pair, kernel A's [C] must be symmetrised before the modal step. Its default mesh gave C₁₂ and C₂₁ a factor of three
  apart on a 1 µm-thick symmetric pair.
- A conducting layer enters as εr − jσ/(ωε₀), so C* is recomputed per frequency.

### Q4 — the short standard needs about 6 substrate heights

Microstrip, short standard 1, 2, 3, 5 and 7.9 h with the long standards fixed. At 1 GHz (βΔℓ 34–40° for all), ε_eff reads
1.960 / 1.947 / 1.943 / 1.939 / 1.937 and the third-line residual falls from 6.6e-4 to 1.3e-4: the two walls' end fields
overlap in a short standard and bias γ. 5 h against 7.9 h agree to VSWR 1.0035 and |ΔS21| 4e-3; 1 h is up to 1.10:1 off.

**Decision: the end run is 3 substrate heights per wall, and the short standard at least 6 h**, the planar solver's own
default.

(The 5 and 10 GHz rows of the shortest variants are also at βΔℓ 158–168°, because the long standards were held fixed.
That inflates them, and the product sizes separations from the short standard as built, so it is not a property of the
port.)

### Q5 — the conducting-silicon line: recorded from the existing reference, not re-run

The fixture was built (`testdata/em-si-sigma`'s line, 90 µm on 2 µm oxide on 100 µm of 10 S/m silicon) and it is
expensive on Palace. The first standard took 887 s with the adaptive sweep. The second was stopped after 32 minutes at
3–4 minutes per frequency, and the direct solver at five fixed frequencies did not finish one standard in 10 minutes.
The 2 µm oxide meshes to 2.6× the estimate.

`testdata/em-si-sigma/README.md` already records what Q5 asks, on this line:
- A wave port took the wrong mode at low frequency, and its driven S read a uniform line as S11 = −0.76 dB at 5 GHz.
- The reference γ was itself taken from lumped-port line pairs through the eigenvalues of T₂T₁⁻¹, which is this
  calibration's own γ step.

The full calibrated wall port against that reference is left to brief 7's Line over Silicon cell. On such a stack the
β prediction must be history-free, from kernel A's quasi-static γ with C*(ω), as the planar solver's SI-LOW path already
does: ε_eff falls from 53 to 7.6 over 1–40 GHz, and scaling β from the previous point picks the wrong separation.

### Q6 — what the standards cost

Wall clock, Palace, ten processes:

| Fixture | The 20 mm line | Its three standards together | Ratio |
|---|---|---|---|
| Stripline, gap w/10 | 56 s | 139 s | 2.5× |
| Microstrip, gap w/10 | 73 s | 167 s | 2.3× |
| Coupled pair (4-port), gap 100 µm | 468 s | 685 s | 1.5× |

1–10 GHz needs two separations (three standards). The cache (overview D9) is what makes a tuning sweep pay this once.

### Other findings

- **Palace's ε_eff on these fixtures, Standard quality, no refinement:** +0.76 % on the stripline (+0.41 % with the
  largest element halved), about +3 % on the microstrip (+2.4 % with the metal-edge elements halved). The microstrip's
  also falls with frequency, which a microstrip should not do. This floor, not the wall port, sets every |ΔS| against a
  closed form here.
- **Two wave-port reference runs did not finish:**
  - The stripline (PMC sides, PEC top and bottom faces) carries two TEM modes of equal speed on its port face, the
    degenerate case.
  - The microstrip ran 18 minutes in the solve at full CPU. It was stopped, and the cause is not established.
  - The wall port has neither problem: its PEC end walls tie the planes together, and it solves no mode.
- **A conducting dielectric needs `"Role": "Dielectric"`** on its object when its material states both εr and σ, or
  `check` refuses it, by name, as ambiguous.

### The brief's stop rule was met as written, and the owner chose restated gates

The brief asked to stop if no combination brought the stripline within 1e-2 in |ΔS| of its closed form at 10 GHz. Measured:
- **|ΔS|:** 2.3e-2 at Standard, 1.25e-2 with the largest element halved.
- **What it is:** Palace's phase velocity over 20 mm. The port match there is VSWR 1.0012–1.003, the third-line
  residual 8.6e-4, and all five gaps agree with each other to |ΔS21| ≤ 1.1e-3.

**Decided (owner, 2026-11-18): brief 3 proceeds, with its gates restated around the port match and self-consistency rather
than a finer-mesh reference first.** The gates, from these numbers with margin:
- **Gate 3, stripline:** calibrated VSWR against the exact line ≤ 1.01, third-line residual ≤ 3e-3, |ΔS| ≤ 3e-2 at
  10 GHz, ε_eff within 1 %.
- **Gate 4, coupled pair:** calibrated VSWR against kernel A's quasi-static coupled line ≤ 1.025, modal third-line
  residual ≤ 1.5e-2, both modes' ε_eff within 3 % of kernel A's, mode separation ≥ 0.5°.

## PWL-3, first half — a wall port resolves, its face is forced and its line is cut back (2026-11-18)

`docs/briefs/brief-pwl-3-wall-port-model-and-palace.md`, R-pwl3-1, -2, -5 and -15. The calibration (R-pwl3-7…12) is the
second half; until it lands, a wall-port run publishes the RAW answer at each gap and its notes say so.

- **`Em3dPortKind.Wall` is a DOCUMENT kind.** A `.c3d` port with `"Kind": "Wall"` (plane, offset and a rectangle round the
  line's end, as a wave port states) resolves through `C3dWallPorts.Resolve` to an ordinary **lumped** `Em3dPort` carrying an
  `Em3dWallPort` record (face, axis, gap, the line's end and height, the line). No solver lowers a third kind. The Palace
  geometry of a wall-port run was checked by eye: on testdata/pwl's stripline the strip runs 200…19,800 µm and port 1 fills
  0…200 µm against the PEC face.
- **The gap is `min(w, h)/5`** (`C3dWallPorts.GapFraction`): w is the line's width just inside the face, h its height above
  the highest ground conductor beneath its end (a PEC floor counts).
- **The face is forced in ONE place.** `C3dProblemAssembly.AirBox` takes the document and forces its wall-port faces PEC
  with no padding on a clone of the setup. `check`, `explain`, the editor's box and the run therefore all agree on the box.
  Without that, `check` resolved the port against the setup's stated 500 µm padding and refused it as off the face, while
  the run accepted it. `Assemble` does the same before padding and adds the note.
- **The line is cut back in the solved problem** (`C3dWallPorts.Trim`): a sheet's or extruded outline's rings are clipped at
  the gap's inner edge (one Sutherland–Hodgman edge), and a box is shortened. A line with other metal in the gap beside the
  port's end is refused rather than cut there.
- **Refused by name:** a wall port off the faces; one on the floor or lid (this version); a rectangle over two lines (state
  Positive, or one port per line); a face with both wall and wave ports; a line with no ground beneath; a setup on EMerge or
  openEMS (R-pwl3-15, lifted by briefs 5 and 6); and a `.cem` `Ports3D` row of kind Wall. That last one is refused rather
  than silently run as the generator's ordinary lumped port; a layout's wall ports come with the per-setup override.
- **`DeembedUm` is allowed on a wall port** (it moves the reference plane into the structure, as on a wave port); only a
  plain lumped port refuses it.
- Gate: `tests/Ui.Tests/ThreeD/WallPortTests.cs` (5 tests, the run's own assembly, no solver). The existing 3D port classes
  (135 tests) and `Firewall.Tests` are unchanged.

## PWL-3, second half — the calibration, end to end on Palace (2026-11-18)

R-pwl3-7 … R-pwl3-12. `Em3dWallCalibration` (`src/Design/Em3d`) plans, builds the standards and de-embeds; `Em3dRunService`
solves the standards first and the Palace leg calibrates the structure's S before anything is written.

- **One group per face.** Every wall port on a face is one calibration group (scalar at one line, modal at two or three);
  more than three is refused by name. Distance-based splitting of a face into several groups (overview D6) is NOT built: a
  face whose lines are far apart is one group, and if its modes then travel at nearly one speed, the setup check refuses it
  (R-pwl3-3, asked from kernel A's modes at the band's ends and centre over the separation each frequency reads).
- **The standards are the structure's own section.** The face's end run (3 h) is sliced at three stations; every box,
  extruded outline and horizontal sheet crossing it must give the same intervals at all three, or the run refuses, naming
  the object. Each standard is those pieces extruded between two PEC walls (lines keeping their gap at both ends), every
  other face as the structure's, the near ports the structure's and the far ones mirrored. Short standard
  `max(6 h, 8 g)`; separations from `LineCalibration.SuggestDeltas` at kernel A's ε_eff (or, where a layer conducts,
  history-free from kernel A's per-frequency β).
- **Two faces with one cross-section share their standards** (R-pwl3-8): `Section.Signature` (every piece across the face,
  the gap, the ports' count and Z0 — not where the face stands). The two ends of a line cost three standards over 1–10 GHz,
  not six. The first end-to-end stripline run, before this, took 24 minutes for six.
- **C′ and the modes from kernel A** on the section, everything that is not a line grounded (it touches the walls), a PEC
  lid and PEC side faces as grounded conductors, the mesh sized for the widest grounded metal against the narrowest line,
  and **never below 48 cells for a group**: the first coupled run took kernel A's default 6 (the face has no wide metal
  besides the floor) and published an answer up to VSWR 1.036 from the spike's. `[C]` is symmetrised, [L] = μ₀ε₀[C₀]⁻¹
  over the lines, the modes from `MulticonductorLine.VoltageModalMatrix`, **Tv's columns normalised to unit length** so a
  mode's C′ (and the Z_c the notes print) reads in ohms. The first coupled run printed 2e8 Ω; the published S did not
  move, since a column's scale cancels between the box and modal → terminal.
- **The de-embedding is the shared algebra.** `LineCalibrator.At` (one line) or `ModalAt` (a group), `LineDeembed.ApplyBlocks`
  with every other port an identity block, a reference plane moved by `DeembedUm` peeled with the mode's own γ, then
  `ModalToTerminal` to each port's Z0 (the pseudo-wave renormalisation for a group of one).
- **Published:** the calibrated S in the ordinary `.sNp`/`.npy`; the raw S at the gaps as `WallPortRawS` `[f, i, j]` in the
  Palace diagnostics group; per face a note of ε_eff, Z_c and the standards' agreement, and a note of any frequency whose
  separation fell outside 20–160°. Each standard's own result files land in `results/` under the setup's name and the
  standard's label.

### Gate 3, on the product (testdata/pwl stripline, Kind Wall, gap 200 µm, Palace Standard)

| f | VSWR vs exact | \|ΔS21\| | ε_eff |
|---|---|---|---|
| 1 GHz | 1.0026 | 2.4e-3 | 2.1168 |
| 5 GHz | 1.0014 | 1.2e-2 | 2.1166 |
| 10 GHz | 1.0046 | 2.4e-2 | 2.1167 |

Z_c 50.11 Ω (kernel A's +0.2 %), the standards agreeing to 3e-5…4e-4. Inside every restated bound. Held by
`WallPortTests.Gate3_TheCalibratedStriplineIsTheExactLine` (`Category=Benchmark`, launches the built CLI, skips without
Palace and Gmsh).

### Gate 4, on the product (testdata/pwl coupled pair, Kind Wall, two ports a face, Palace Standard)

One group per face, the xmax face sharing the xmin face's three standards (3.048, 9.624 and 23.842 mm). Modes ε_eff
1.753–2.005, Z_c 48.35 / 69.8 Ω, the standards' cascade agreeing to 7e-15. Against PWL-2's spike calibration of the same
pair: VSWR ≤ 1.0046, |ΔS31| ≤ 1.9e-3. Against kernel A's quasi-static coupled line: VSWR 1.0174 / 1.0046 / 1.0062 at
1 / 5 / 10 GHz, inside the restated 1.025. A run took 25 minutes on a shared machine (the 4-port standards dominate); no
benchmark test holds it yet, because its reference needs kernel A's modes in the test, which is the next step's.

## PWL-3, stage 3 (part) — the standards cache and the switch (2026-11-18)

- **R-pwl3-9 — the cache.** `Em3dWallCalibration.CacheKey` hashes (SHA-256, 24 hex) everything that decides a standard's
  solve: its solids' and sheets' geometry and materials, its ports, its box, the materials' values, the frequencies, the
  temperature, the solver, and the SETUP serialized (`EmSetupPersistence.Serialize`). That is conservative on purpose: renaming
  the setup misses the cache. The raw S lands in `results/wall-standards/<key>.json` (written through a `.tmp` and a move; a
  failed write costs only the next run its reuse). `SolveWallStandards` loads a hit instead of solving and says how many it
  reused. Gate: `WallPortTests.AStandardsCacheKeyFollowsItsSolve_AndItsAnswerRoundTrips` (stable for one solve, different for
  another length, other settings, another solver; S round-trips bit for bit).
- **`CalibrateWallPorts`** on `EmSetup` (default on, written only as `false`, the `ThinMetalCorrection` pattern). Off skips
  the standards and publishes the raw S at the gaps, with a note; the assembly's gap note stops promising a calibration.
  Gate: `CalibrateWallPorts_IsWrittenOnlyWhenOff_AndReadsBack`; the 70 setup-document tests are unchanged.
- **R-pwl3-14, in part — `explain` (and so `run dryRun`, which goes through it) shows a `wall ports` block**: each face's
  group, its standards' lengths, whether a face shares another's, the C′ source, and how many standards a run must solve,
  all from `Em3dWallCalibration.Plan` itself, or the refusal a run would give. `ResultDocument.WallPorts` carries it in
  `--json`. The standards are not yet priced in memory and time beside the structure. Gate:
  `WallPortTests.ExplainShowsEachFacesGroupAndItsStandards`; the 169 tests of the classes that run `explain` are unchanged.

## PWL-3, stage 3 — brief 3 closed (2026-11-18)

This section supersedes two lines above: a face is no longer one group, and a `.cem`'s `Ports3D` Wall is no longer refused.

- **Groups by distance (D6).** A face's lines within `CouplingHeights` of each other (the planar solver's own
  `DrivenNeighbourClearanceHeights`, 5, read from `PlanarCalibrationSettings` rather than a second literal, as is the group
  cap of 3) are one group; farther apart, separate groups, each with its own standards and named `xmin (1)`, `xmin (3)`.
  A group's section leaves the OTHER groups' lines on that face out; the far face's port on the same line is not "another
  group" (the first cut excluded it by conductor name and the single stripline lost its own strip).
- **A setup's Wave/Wall choice** (`C3dWallPorts.ApplySetupKinds`): an embedded setup's `Ports3D` rows of kind Wave or Wall
  applied to copies of the document's ports, in `Assemble` and in `AirBox` (so `check`, `explain` and the run agree on the
  box). A row naming no port, or a lumped port, is refused by name; a wave port with terminals run as Wall becomes one wall
  port per terminal. Lumped rows are ignored, not refused as the brief asked: `EmPort3D.Kind` has no "unstated" value, so a
  row that states only a `Return` would read as Lumped. **Two shipped examples** (`Passivated Pad/2 Probe Pad`, `4 Bonded
  Pad`) state `Kind: "Wave"` on ports drawn as Wave, so a setup that changes no kind runs the ORIGINAL document, not a copy.
- **A layout's wall ports** (`Em3dGenerator`): the setup is generated with its Wall rows as Wave (the generator's own face,
  zero padding and line), then `C3dWallPorts.FromWavePorts` turns each into the wall port it stands for: the face PEC, the
  gap from the face to the line, the line cut back by the same `Trim`. EMerge/openEMS refused as on a `.c3d`. Gate:
  `LayoutWallPortTests` (top-copper line over the inner plane: PEC x faces, gap = min(w, h)/5, line starting one gap in).
- **What a run publishes (R-pwl3-12):** the `.sNp` header names each wall port (calibrated, its reference plane; or
  UNCALIBRATED and its gap) and each group (ports, scalar or modal, the standards' lengths, ε_eff, Z_c, residual); the
  diagnostics carry `WallPortGamma`, `WallPortEeff`, `WallPortZc` and `WallPortResidual` `[f, Port]` beside
  `WallPortRawS`; the planar solver's passivity rule (σ_max ≤ 1 + 1e-3, uniform real reference) on the calibrated answer is
  a note when it fails. Gate: `IdealLinesCalibrateToTheLine_AndTheDiagnosticsReadItsEpsEff` (ideal matched lines in place of
  solves give back the 20 mm line to 5e-3 and the fill's ε_eff to 1e-6).
- **`check`** reports each setup's wall refusal (faces, a `Ports3D` row, groups, end runs) as an error, the plan as notes, and
  warns (`check.c3d.wall-grounds`) about every conductor a wall face shorts that is not a port's line, above the face's
  returns (D7). **`explain` / `run dryRun`** list each wall port and where its kind came from, the plan, each standard cached
  or to solve (one `Em3dWallCalibration.CachePath`, used by the run too), the uncached standards' unknowns and memory from
  `EstimatePalace`, and the total with the structure's. On the coupled pair: 234,643 / 324,525 / 263,066 unknowns, 3.8 GB
  peak.
- **The cache through the run:** `WallStandardsCacheRunTests` drives `RunThreeDView` twice with a stand-in solver: three
  standards solved and kept, then none. The standards now take their legs through `LegStandIn` like every other leg did;
  `EmResultRecordTests` and this class share the `Em3dLegStandIn` collection, since that hook is a process-wide static.
- **Gate 4 is a benchmark test** (`Gate4_TheCalibratedPairIsKernelAsCoupledLine`, `Category=Benchmark`, about 25 minutes).
  It computes kernel A's coupled line itself and reads each mode's ε_eff from the published S through its modal 2-port
  (A = cos βℓ, independent of the reference impedance). Checked against the stage-2 product result, not re-run: VSWR inside
  1.025 at every port, the standards agreeing to 7e-15, the odd mode inside 3 %, and the **even mode 3.3 % above kernel A at
  1 GHz** (2.006 against 1.9415). PWL-2's spike read the same mode 1.985–2.003, so the restated 3 % is exceeded by Palace's
  own microstrip phase velocity, not by the port; the owner moved the even-mode bound to 3.5 %, which the test holds.
- **Gate 1:** the classes that lower shipped `.c3d`/`.cem` to Palace (the `ThreeD` namespace, the port, generator and Palace
  classes, 4,443 tests) pass except nine `Em3dGeneratorDumpTests.Gate2` cases on `examples/Dividers and Couplers`, whose
  layouts and `.cem`s changed in a 2026-11-17 commit after their dumps were last written (2026-11-16); nothing here touches
  them. The reference text (`reference em-setup`, `3d-view`) and `docs/user/src/reference/em-setup.md` describe all of it.

## PWL-5 — the wall port on EMerge (2026-11-18)

`docs/briefs/brief-pwl-5-emerge-wall-port.md`. No new physics and no change to `circuitrf_emerge.py`: a wall port lowers to the
ordinary lumped port its gap is, and EMerge accepts a lumped sheet whose edge lies on a PEC face of the box (R-pwl5-1, checked on
one frequency: the raw stripline at 5 GHz, |S11| 0.12, S21 a few degrees past the exact line, as Palace's raw gap reads).

- **The leg calibrates as Palace's does**: `CaptureRaw`, then `run.Wall.Apply`, before `BuildDataSet`; the `WallPort*` cubes into
  EMerge's group, the group lines into its Touchstone header. The port line's wall wording moved to
  `Em3dWallCalibration.SnpPortWords`, which both headers call. The standards are EMerge runs from `SolveWallStandards`, cached
  under EMerge's key (the solver is in it).
- **The refusal is one function now** (`C3dWallPorts.SolverRefusal`): openEMS only, for a `.c3d` and a layout `.cem` alike.
- **R-pwl5-2 — independent meshing, measured.** EMerge meshes each standard and the structure on its own, as Palace does. On
  fixture S (the 20 mm stripline), against the exact line:

| EMerge mesh | tetrahedra (structure) | VSWR 1 / 5 / 10 GHz | \|ΔS21\| worst | ε_eff |
|---|---|---|---|---|
| Standard preset | 2,955 | 1.0071 / 1.0201 / 1.0191 | 6.9e-2 | +2.0 … +2.3 % |
| Accurate preset (adaptive mesh) | 15,818 | 1.0081 / 1.0025 / 1.0046 | 8.7e-3 | +0.29 % |
| 0.05 λ, edge refinement 0.1 | 38,717 | 1.0003 / 1.0005 / 1.0017 | 7.3e-3 | +0.25 % |
| *Palace, Standard (PWL-3)* | | *1.0026 / 1.0014 / 1.0046* | *2.4e-2* | *+0.8 %* |

  On fixture C (the coupled pair), against kernel A's quasi-static coupled line, worst VSWR at 1 / 5 / 10 GHz and each mode's
  ε_eff at 1 GHz:

| EMerge mesh | VSWR | even ε_eff | odd ε_eff |
|---|---|---|---|
| Standard preset (5,658 tetrahedra) | 1.0105 / 1.0166 / 1.0162 | +0.58 % | +2.06 % |
| 0.05 λ, edge refinement 0.1 (98 s, 7.5 GB peak) | 1.0047 / 1.0119 / 1.0118 | −0.13 % | +0.55 % |
| *Palace, Standard (PWL-3)* | *1.0174 / 1.0046 / 1.0062* | *+3.3 %* | *within 3 %* |

  **Standard fails gate 3** (VSWR 1.02, |ΔS21| 6.9e-2, ε_eff +2.3 %) and the finer mesh passes it with margin: the error is the
  mesh's phase velocity in the line and its standards, not the port — the same port at 0.05 λ beats Palace's own numbers.
  Gate 4 passes even at Standard. So it ships with independent meshing, the gates are held at the finer mesh
  (`WallPortTests.Gate3_OnEmerge_…` 41 s and `Gate4_OnEmerge_…` 3 m 41 s under load, `Category=Benchmark`; gate 4 at the
  brief's own 3 %), and the user docs tell a wall-port user to give EMerge that mesh. Not done: raising EMerge's mesh for a
  wall port automatically — the setup's mesh stays the user's.
- **Routine gate**: `OnEmerge_AWallPortsGapIsWrittenAsALumpedPortFromTheWall` (the script's port: origin on the wall, direction
  along the line, the 200 µm gap by the strip's width, no wave port). The script writer is untouched; the 146 routine EMerge
  tests are unchanged. `LayoutWallPortTests` now runs a layout's wall ports on EMerge and refuses them on openEMS.

## PWL-6 — the wall port on openEMS (2026-11-18)

`docs/briefs/brief-pwl-6-openems-wall-port.md`. Every solver now runs a wall port; `C3dWallPorts.SolverRefusal` and its two call
sites are gone. Nothing in `CsxcadWriter` or `FdtdGrid` changed: the wall face was already written as a PEC boundary of the grid
(R-pwl6-1; the CSX `BoundaryCond` reads `PEC` there), and the gap is the ordinary lumped port it lowers to.

- **R-pwl6-2 — the gap is whole cells.** circuitRF's own grid keeps a port's extents as lines and never merges them (with
  `MinCellUm` 500 against a 200 µm gap it still places both, and says "No lines merged"; the pair's 101.6 µm gap forces a cell
  under its 120 µm minimum, with a warning). So the case arises only on a grid the SETUP gives (`OpenEms.GridLines`), where the
  gap's end can fall between lines: `C3dWallPorts.GapsOnGrid` widens it to the next line, cuts the line back with the same
  `Trim`, and notes both lengths. It runs before any leg prepares the problem and before the standards are planned from it.
- **R-pwl6-3 — identical grids.** `Em3dWallCalibration.StandardGridLines`: a standard's y and z lines are the structure's, and
  along the line the structure's lines within the end run of the wall, copied at the near wall and mirrored at the far one,
  with an even fill between no coarser than the structure's coarsest cell. They reach the standard's run as explicit
  `OpenEms.GridLines` in its setup (`Em3dRunService.WallStandardSetup`, which `explain` calls too), so the cache key — the setup
  serialized — covers the structure's grid, which a standard's answer now depends on. Strictly inside the end run the lines are
  equal as numbers (`OnOpenEms_EachStandardsGridRoundItsPorts_IsTheStructuresOwn`); a short standard is exactly two end runs
  long, and its fill's one line lands on the end run's edge.
- **R-pwl6-4** — `C3dWallPorts.BoxModeNote`: PEC faces beside a wall's face close a box with it; its first mode
  (c / (2·w·√εr) across the face) below `FdtdWavePorts.SideWallMargin` (1.5) × the sweep's top is noted with PMC or Absorbing
  as the remedy, on every openEMS run with wall ports. The fixtures' PMC/absorbing sides give none.
- **R-pwl6-5** — `explain` prices openEMS standards from their own explicit grids (cells and `OpenEmsTimeEstimate`), and calls
  one cached against the same standard setup the run keys it by. It had priced every solver's standards with Palace's
  estimate; EMerge's now say only "to solve", as EMerge prices nothing ahead.
- **The leg calibrates as Palace's and EMerge's do**, after `FdtdPortTransform.Solve` (its `Mat<Complex>[]` S into the
  calibration's arrays and back); cubes into the openEMS group, the group lines and `SnpPortWords` into its header.

### The gates, beside Palace's and EMerge's (VSWR at 1 / 5 / 10 GHz)

| Stripline (gate 3) | VSWR | \|ΔS21\| worst | ε_eff | time |
|---|---|---|---|---|
| openEMS, default grid (20 cells/λ, 3,132 cells) | 1.0016 / 1.0001 / 1.0047 | 2.3e-2 | +0.74 % | 8 s |
| openEMS, 40 cells/λ (15,840 cells) | 1.0017 / 1.0003 / 1.0007 | 5.5e-3 | +0.18 % | 8 s |
| Palace, Standard | 1.0026 / 1.0014 / 1.0046 | 2.4e-2 | +0.8 % | ~25 min |
| EMerge, 0.05 λ / edge 0.1 | 1.0003 / 1.0005 / 1.0017 | 7.3e-3 | +0.25 % | 30 s |

| Coupled pair (gate 4) | VSWR | even / odd ε_eff at 1 GHz |
|---|---|---|
| openEMS, default grid (19,380 cells) | 1.0014 / 1.0054 / **1.0255** | +0.69 / −1.82 % |
| openEMS, 40 cells/λ (45,080 cells, 55 s) | 1.0044 / 1.0047 / 1.0195 | +1.01 / −1.04 % |
| Palace, Standard | 1.0174 / 1.0046 / 1.0062 | +3.3 % / within 3 % |
| EMerge, 0.05 λ / edge 0.1 | 1.0047 / 1.0119 / 1.0118 | −0.13 / +0.55 % |

openEMS's default grid meets gate 3 and misses gate 4's VSWR by 5e-4 at 10 GHz; 40 cells/λ meets both. The benchmark tests hold
gate 3 at the default (`Gate3_OnOpenEms_…`, 8 s) and gate 4 at 40 (`Gate4_OnOpenEms_…`, 55 s, at the brief's own 3 %), and the
user docs say a coupled pair wants 40. Routine: the CSX model, the grid identity, the widening on given lines and the box note
(`WallPortTests.OnOpenEms_…`, 4 tests); `LayoutWallPortTests` runs a layout's wall ports on openEMS too. The 148 tests of the
wall-port, openEMS, FDTD, CSX and `check`/`explain` classes pass, and Firewall.

## A wall port on a box-shaped line (PWL-7, 2026-11-18)

`Em3dWallCalibration`'s section builder (`Section.Take`) checks that each conductor in a wall port's cross-section runs
unbroken through the end run. The check compared an `Em3dBox`'s extent with the slab from the face. A wall port's own line is
cut back by its gap g = min(w, h)/5 when the port is made, so its box starts at slab + g, not at the slab, and the check refused
it ("ends inside end run"). A line drawn as an extruded path never met this, which is why PWL-3's tests passed. The fix moves
the near end of the allowed range in by one gap, toward the inward side and only for a line the port owns. Other conductors in
the section must still reach the face. The Port Types example's Microstrip, Coupled Pair and Line over Silicon draw their lines
as boxes, and `check` is clean on all three.

## PWL series review (2026-11-18)

A review of the finished series found four defects; each is fixed and has a routine test.

- **A group's lines of different widths had port sheets that did not reach their lines in the standards.** `Section.Build`
  cut every line back by the GROUP's gap (the largest of its ports'), while each standard's ports kept their own, shorter
  gap sheet: a 400 µm line beside a 1200 µm one (gaps 80 and 101.6 µm) stood 21.6 µm clear of its port, so the narrow
  line's standard was an open circuit and its error box meaningless. Each line now keeps its own gap at both walls, and the
  section's signature (which decides when two faces share standards) carries each port's gap.
  `WallPortTests.AGroupsLinesOfDifferentWidths_EachKeepItsOwnGapInTheStandards`.
- **A comparison calibrated every solver with the first solver's standards.** The standards were solved once, by
  `legs[0]`, and `RunContext.Wall` was shared by every leg, so an EMerge structure was de-embedded with Palace's error box —
  which removes Palace's gap discretisation, not EMerge's. Each leg now solves its own standards (`RunContext.WallBySolver`,
  set into `Wall` before each leg runs; the cache already keyed by solver). `WallStandardsCacheRunTests.AComparison_SolvesEachSolversOwnStandards`.
- **`explain` priced and looked up openEMS standards on the un-widened problem.** The run widens each gap to whole grid
  cells (`GapsOnGrid`) BEFORE planning, so its standards differ from those of the problem `explain` was handed: explain
  called a cached standard "to solve" and priced the wrong geometry. It now applies the same widening first, and lists
  each compared solver's standards.
- **A setup's `Ports3D` asking Wave of one port and Wall of another on the same face was resolved silently.** The run's
  path switches a whole face per row, so the last row won. It is refused now, naming both ports (R-pwl3-5).
  `WallPortTests.ASetupAskingTwoKindsOfOneFace_IsRefusedNamingBothPorts`.
- **The standards' cache key held the whole setup, its name included.** Renaming a setup, or a second setup with the same
  settings, solved every standard again. The key now serializes the setup without what only names it or places its output
  (`Em3dWallCalibration.CacheSettings`: name, layout reference, Touchstone path, schematic options, panel state, `Ports3D`,
  `CalibrateWallPorts`); everything else stays in, so an unlisted field that changes the solve still changes the key.
  Cache files written before this change are not found again, and their standards are solved once more.
- **The editor forced a wall port's face for every setup; the run only for a driven one.** An eigenmode or magnetostatic
  setup carries the ports too, so its run padded the face the editor drew flush, and then refused the wall port as "not a
  face of the air box". One predicate decides both now (`C3dWallPorts.ForcesFaces`: driven, eigenmode or magnetostatic, not
  scattering); those runs load the gap uncalibrated and their gap note no longer claims a calibration.
  `WallPortTests.AnEigenmodeSetup_ForcesTheWallFaceAsTheEditorDraws_AnElectrostaticOneDoesNot`.

## A MOD_Source drives a fields analysis; a receiving array's pattern is its receive pattern (brief PCH-13, 2026-11-19)

**A MOD_Source is a drive.** `drive=` may name one by instance. It stands in the S-parameter solve as its source impedance from its
node to its reference (its drive value lives on the MNA's own right-hand side, which `BlockWaves` does not use), so it is driven
exactly as a port of Z0 = its Z: `SParameterEngine.SourcePorts` numbers the top-level MOD_Sources after the circuit's own ports and
`BlockWaves` resolves those numbers itself, so no caller carries a mapping. Unweighted, its amplitude is √Pavg, as a P1Tone's is
√Pavl, and the two compare only with each other. Notes name it (`DrivenName`: "'GNB' (MOD_Source)", not a port number nobody wrote).

**A receiving array** (incident waves, or a Propagation Channel's `Rx` end) **takes its waves from the receive weights.** Driven from
its feed, a Feed-mode array behind one-way LNAs has a = 0 at every element, so its pattern was empty — on the mmWave Array Link's `PR`
and on the *Phased Array* example's *2 Receive* alike (that example's G/T and SNR were never affected: they read the incident-wave
model). The element's row reads b − S·a = V_src on a source node held by 1 S, so `PortReceiveWeights` on those nodes is each
element's emitted-wave transfer to the port; a = Σ drive·T·(Weigh's scale), b = S·a, the port's reflection and accepted power the
forward drive's. Checked: on a reciprocal array it equals the transmit pattern to 1e-9 dB; behind 20 dB LNAs it is that plus 20 dB.

**The trap: do not solve the adjoint (Mᵀ) and read its node voltages and branch currents as waves.** It looks right — a reciprocal
network's matrix is "its own transpose" — but the wave-form stamps (an S-block's and the channel's scaled branch rows) are not
symmetric even for reciprocal parts, so the adjoint's x is not the physical solution of anything. Tried: `PR` passive read 31.6 dBi
against 19.9 the forward way; *2 Receive* read 42.1 dBi against 29.8. Read the transposed solve only where its meaning is derived,
as `PortReceiveWeights` does on a source node.

Gates: `FarFieldCosimTests.AModSource_DrivesAsAP1ToneOfTheSamePowerAndZ`,
`PhasedArrayReceiveTests.TheReceivePattern_IsTheTransmitOne_OnAReciprocalArray_AndBehindLnasIsRaisedByTheirGain`.

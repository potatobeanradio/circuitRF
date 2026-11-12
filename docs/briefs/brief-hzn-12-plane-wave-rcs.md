# HZN-12 — an incident plane wave and radar cross-section on EMerge, and the Hertzon scattering examples they open

Series: `brief-hzn-0-overview.md`. Needs HZN-2, HZN-7 and HZN-11 (`src/Design/RESOLVED.md` §HZN, "HZN-7" and "HZN-11"), and the
far-field path of the CP series (`circuitrf_emerge.py`'s `farField` request, `PortFarFieldSet`).

## Why

After HZN-11, 22 of Hertzon's 58 example designs open. Of the items that stop the rest, the Incident Plane Wave is the only one that
opens designs by itself. It is the only item on 7 designs:
- `NATO-Sphere` and `SphereRCS`,
- the three `sphere25cm_true_*` mesher projects,
- `sphere35mm_aura` and `sphere35mm_local`.

Three more, the `sphere25cm_faceted_*` projects, are plane-wave runs as well. Their faceted sphere (`NumberOfSegments: 100`) is refused
first, which hides the plane wave. Part 2 opens them. That makes **29 of 58 after Part 1, and 32 after Part 2.**

Every one of the 10 is the same kind of problem:
- one perfectly conducting sphere in free space, at 1 GHz;
- `Auto Open`, with a Radiation, PML or Auto boundary (HZN-2 already translates each one);
- one `Incident Plane Wave`, with `VectorInput: Cartesian`, `KVector: 0,0,1` (travelling along +z) and `E0Vector: 1,0,0`
  (polarised along x), `EMagnitude: 1` and `ZeroPhasePosition: 0,0,0`;
- no ports;
- usually an Infinite Sphere at 2° steps over the full sphere, with a bistatic RCS cut and a monostatic RCS reported in dBsm.

circuitRF has nothing that drives a model without a port. The EMerge build circuitRF installs (3.0.0) has what is needed:
- a background-field boundary condition (`BackgroundField`: angle of incidence, polarisation angle, amplitude, phase origin, under one of
  two angle conventions, `"EA"` or `"SPH"`);
- `run_scattered`, which solves for the scattered field under it;
- a helper giving the direction that looks straight back along the incidence, which is the monostatic direction.

The driver already integrates a far field over the absorbing faces for a port-driven run. RCS is that integral, applied to the scattered
field and normalised to the incident field instead of to an input power.

The sizes differ a lot:
- The 35 mm sphere is small: ka ≈ 0.74, in the resonance region, so its pattern has real structure.
- The 25 cm sphere is large: ka ≈ 5.2, 1.7 wavelengths across, inside 30 cm of PML.

The gates use the small one.

Plane-wave scattering is useful to circuitRF's own users too: the RCS of a radome, a reflector or a package lid, and later a plane wave
falling on an antenna.

## What to build

### Part 1 — the plane wave, the scattered run and RCS

#### R-hzn12-1 — the format

`docs/design/c3d-format.md` §10.3 already reserves this place: a setup-level `Excitation`, because the incident wave belongs to the
analysis and not to the geometry. This brief fixes the names.
- A `Driven` setup may hold `Excitation: { "Kind": "PlaneWave", "Direction": [x, y, z], "EField": [x, y, z], "E0": …, "PhaseOrigin": [x, y, z] }`:
  - `Direction` is the direction of travel and `EField` the polarisation. Each component is a plain number or an expression, as in any
    `.c3d` triple. Neither needs to be unit length.
  - `E0` is the peak amplitude in V/m, with a default of 1.
  - `PhaseOrigin` is a point in DBU, with a default of the origin.
- **Vectors, not angles.** EMerge alone has two angle conventions, and Hertzon's Cartesian form is what all 10 examples use. A vector pair
  has one meaning; an angle pair needs a stated convention and is easy to read in the wrong one. An incidence-angle sweep can still be written
  with expressions in the components. An angle form is a later convenience: list it under **Open**.
- `check` refuses each of these, naming the setup:
  - a zero `Direction` or `EField`;
  - an `EField` that is not at right angles to `Direction` (within a stated tolerance);
  - a plane-wave setup with ports (no example has both; list it under **Open**);
  - a plane-wave setup on a solver that cannot run one (R-hzn12-3);
  - a plane-wave setup whose air box does not absorb on every face, because the scattered field then has nowhere to go.
- A plane-wave setup computes RCS whether or not its Radiation pattern is on. Its far-field grid is the setup's existing θ/φ steps.
- Rewrite §10.3's row as built, and §8's `Problem3D` line to say that a `Driven` setup with a `PlaneWave` excitation is a scattering run.
  `FormatVersion` does not change (§9: a new optional key).
- **UI:** the setup panel offers Excitation as **Ports** or **Plane wave** in a picker. Plane wave shows the vectors, `E0` and the phase origin.
  The 3D view draws the wave at the phase origin when that setup is selected: one arrow for the direction and one for E, in the view's existing
  overlay style.

#### R-hzn12-2 — the run on EMerge

- The driver builds one `BackgroundField` from the excitation and solves with `run_scattered`.
- The conversion from the document's vectors to EMerge's angles is the one place where a silent error is likely. To catch it, the driver
  reads back the k̂ and Ê of the field EMerge actually built, at the phase origin, through EMerge's own object. It writes both into the run's
  `explain` lines and the `.cemr`, so a wrong convention is visible after one run without a plot.
- One solve per frequency of the setup's plan. A plane-wave setup has no port loop.
- The dry run reports unknowns, memory and time as for a port-driven run, and refuses what `check` refuses.

#### R-hzn12-3 — the other solvers

- **Palace** and **openEMS**: refuse a plane-wave setup by name in `check` and in the dry run. The sentence names the solver and says that
  EMerge runs it. Whether either can run one is a follow-up: list both under **Open**, and do not build them here.
- Hertzon's own run (HZN-8) needs nothing new. It runs the `.hzn` as it is.

#### R-hzn12-4 — the result

- Radar cross-section σ(θ, φ) = 4π r² |E_s|² / |E_i|² as r → ∞, in m².
  - **Bistatic** is σ over the setup's far-field grid.
  - **Monostatic** is σ in the one direction straight back along the incidence. It is computed in that direction itself, never interpolated
    from the grid.
- The driver writes a new result group, `RCS`, into the run's `.npy`:
  - `Bistatic`, Real, [frequency, θ, φ], in m²;
  - `Monostatic`, Real, [frequency], in m²;
  - `Etheta` and `Ephi`, Complex, the scattered far field on the same grid, so that a later polarised RCS needs no new run.
- It is not the port far-field group, because that group's readers compute gain and directivity from an input power that a plane-wave run
  does not have. A plane-wave result shows no gain, no directivity and no efficiency anywhere.
- No Touchstone file is written. An `-o *.sNp` on a plane-wave run is refused, naming the setup. The setup's Output name decides the result
  files, as CP-1 already ensures.
- The `.cemr` records the excitation (vectors, `E0`, phase origin) and the k̂/Ê read back from EMerge.
- **Data Display:** a plane-wave result is offered in as few clicks as a radiation pattern is (the bistatic RCS cut in the incidence plane,
  in dBsm, a unit the plot inspector already has), and the monostatic value per frequency. Cuts in the E-plane and H-plane are what the gates
  read.

#### R-hzn12-5 — HZN: translate, save and export

- **HZN-2:** an `Incident Plane Wave` with `VectorInput: Cartesian` translates to the excitation of R-hzn12-1:
  - `KVector` becomes `Direction`, `E0Vector` becomes `EField`, `EMagnitude` becomes `E0`, and `ZeroPhasePosition` becomes `PhaseOrigin`
    (in the design's units).
  - The Infinite Sphere's `ThetaStep`/`PhiStep` become the far-field steps.
  - A design with no Infinite Sphere takes circuitRF's default steps, said in a note.
  - Translate the vectors, never the angles. The example files carry both, and only the vectors are certain.
- Still refused, each as its own item:
  - a spherical `VectorInput`, unless black-box runs establish Hertzon's angle convention;
  - an Infinite Sphere that does not cover the full sphere;
  - several plane waves, or a plane wave beside a port.
- **The excitation's `objects` list** (`["Sphere1"]` on the 25 cm projects, empty elsewhere) and the design setting `FieldType: Total`:
  establish what each one does by black-box runs, `sphere35mm_aura` with and without it on `hertzon-cli solve`.
  - If the RCS does not change, translate it as a note.
  - If it does, refuse it as an item with the reason.
  Record the finding either way.
- **HZN-5:** editing the direction, polarisation, amplitude or phase origin saves as those four keys and changes nothing else.
- **HZN-4:** a native plane-wave setup is written as Hertzon writes one: `VectorInput: Cartesian`, with every other key in the excitation
  as Hertzon's own examples carry it.

### Part 2 — the faceted sphere

The three `sphere25cm_faceted_*` projects differ from the `true` ones only in `NumberOfSegments: 100`.
- **First, establish by black-box what Hertzon builds** for a sphere with N segments:
  - how many faces, and in what pattern (latitude bands, a geodesic, or something else);
  - whether the vertices lie on the sphere or the faces are tangent to it.
  Use `hertzon-cli describe`, Hertzon's offscreen render, and the volume of the solid it reports.
- A `.c3d` `Sphere` gains `Segments` (an integer, default 0 = a true sphere). It is built as the polyhedron Hertzon builds:
  - in the geometry kernel (`src/Design/ThreeD/Occ`);
  - in each solver driver's geometry build (`circuitrf_emerge.py` builds a sphere with `occ.addSphere` today);
  - in the 3D view.
  Its faces are named as a sphere's are, or by the scheme `c3d-format.md` §4.5 already uses for a prism's sides. Decide which, and say why.
- HZN-2 translates a non-zero `NumberOfSegments` on `CreateSphere` instead of refusing it. Any other faceted shape (cylinder, circle) keeps
  its refusal unless the same construction covers it at no extra cost.

## Gates

- **`C3dPlaneWaveTests`**, one test per claim, with no solve:
  - The excitation round-trips through `.c3d` with expressions kept.
  - Each refusal of R-hzn12-1 fires, and names the setup.
  - Palace and openEMS refuse a plane-wave setup by name.
  - `-o *.s1p` on a plane-wave setup is refused.
  - A plane-wave result's group reports no gain, no directivity and no efficiency.
- **`EmergeFact` (no solve):** for `Direction` (0,0,1)/`EField` (1,0,0), and for an oblique pair (θ 60°, φ 30°, `EField` not along any
  axis), the k̂ and Ê read back from EMerge's `BackgroundField` equal the document's, within 1e-9.
- **The oracle, run by hand, its numbers recorded:** a PEC sphere, radius 35.36 mm, at 1 GHz.
  - Compare the bistatic E-plane and H-plane cuts, and the monostatic σ, against the exact series solution for a plane wave on a perfectly
    conducting sphere, evaluated in the test with no circuitRF path in it (`src/Engine/CLAUDE.md`).
  - A sphere looks the same from every direction, so a wrong direction or polarisation would pass a single-incidence check. It is caught
    two ways:
    - The E-plane and H-plane cuts differ at this size, so swapping them is visible.
    - Forward and back scattering differ, so a reversed direction is visible.
    - The oblique incidence above must give the same pattern as the reference, rotated to match.
  - State the tolerance the converged mesh reaches; do not choose it in advance. If the monostatic σ is off by a constant factor (4π, 2, or
    E0²), that is the far-field normalisation: fix it, never fit it.
- **`HznCoverageTests`** under `CIRCUITRF_HERTZON_HOME`: **29 of 58** after Part 1, **32** after Part 2. Record the count and the next items
  in `src/Design/RESOLVED.md` §HZN.
- **Gate 1:** every newly opened design saves with no edit byte for byte.
- **Gate 2:** `SphereRCS` with its radius changed, saved, passes `hertzon-cli describe`. So does a native plane-wave `.c3d` exported.
- **Gate 4:** run `sphere35mm_aura` on circuitRF's EMerge and with `hertzon-cli solve`, and compare the monostatic σ and the bistatic cut in
  the incidence plane, in dBsm. Keep each run to minutes. A gap is a finding to record, not a reason to tune.
- **The 25 cm projects:** dry run only (unknowns, memory and time, recorded). Do not solve one unasked.
- **Part 2:** a faceted sphere's volume equals the closed-form volume of the polyhedron R-hzn12 Part 2 established, within DBU rounding.
  `sphere25cm_faceted_aura` translates, and its dry run refuses nothing.

## Risk

Medium-high: this adds a new kind of run, not a new entry in the translation table.
- **Unknown until tried:** whether EMerge 3.0.0's `run_scattered` solves a closed PEC body under circuitRF's absorbing air box with the
  driver's existing mesh and solver settings. Try it on the 35 mm sphere first, and report before building the rest if it does not solve.
- **High, and silent:** the angle conversion and the far-field normalisation. A sphere hides both: a wrong polarisation gives a plausible
  pattern, and a constant factor in σ gives a plausible curve. That is why the oracle checks both E-plane and H-plane cuts, an oblique
  incidence, and the absolute monostatic value.
- **Medium:** reflections from the absorber. A first-order absorber near a small sphere leaks into the back-scattered field, which is the
  monostatic value. Report the monostatic value at two air-box paddings before calling it converged.
- **Medium:** Part 2's polyhedron. It has to match Hertzon's exactly, or gate 4 would compare two different objects. That is why the
  black-box measurement comes first.
- **Low:** the translation and the format keys.

## Open (not in this brief)

- A plane wave on Palace or openEMS.
- An angle form of the excitation.
- A spherical `VectorInput` in a `.hzn`.
- Several plane waves, and a plane wave beside a port.
- A partial Infinite Sphere.
- `PlaneWave.hzn` (a boundary that is not on the outer extent).
- `DroneRCS` (an imported solid).

## Rules

- **Tests:** one per claim, named classes only. No new `Category=Benchmark` test. No EM solve in a routine test; the oracle run and gate 4
  are run by hand and their numbers recorded.
- **Runs:** watchdog every solve (memory and time), and keep each one to minutes. No 25 cm sphere solve unless asked.
- **UI:** the Excitation picker, the plane-wave fields and the 3D arrows are rendered with UiSnapshot and looked at.
- **Names:** no commercial tool, PDK or laminate names. No paper or author cited for the sphere's exact solution; describe it in our own
  words.
- **Clean room (brief-hzn-0 D1):** what `KVector`, `E0Vector`, `ZeroPhasePosition`, the excitation's `objects`, `FieldType` and
  `NumberOfSegments` mean comes from Hertzon's files, its documentation and black-box runs, never its source.
- **Findings** go in `src/Design/RESOLVED.md` §HZN.

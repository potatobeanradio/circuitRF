# HZN-11 — hollow waveguide wave ports normalised to their mode, and the Hertzon waveguide examples they open

Series: `brief-hzn-0-overview.md`. Needs HZN-2, HZN-7 and HZN-10 (`src/Design/RESOLVED.md` §HZN, "HZN-7" and "HZN-10").

## Why

After HZN-10, 12 of Hertzon's 58 example designs open. The Wave Port without renormalisation is the item that stops the most of the rest: it
is the only item on 9 designs, `WaveguidePost` and 8 of the WR90 post mesher projects (`post_pec_*`, `post_dielectric_*`,
`post_copper_volume_remesh`). A 10th, `post_copper_boundary_remesh`, needs a Finite Conductivity on a solid as well.

Every one of the 9 is a **hollow rectangular waveguide**:
- the guide is a vacuum box with no region, so its outer faces are perfect conductors (HZN-2: an outer face with no boundary is Pec);
- a post is subtracted from it, or sits in it;
- a wave port covers each end face, one mode, `Renormalize: False`, impedance definition `Zpv`.

So two things stop these designs, not one:
- **The reference.** With `Renormalize: False`, Hertzon's S-parameters are normalised to the mode itself: unit power in the mode, with no stated
  impedance. A circuitRF port always states a `Z0`. The HZN review noted that EMerge's own S is already normalised to the mode, so this half is a
  port reference carried through to the result, not new physics.
- **The port.** HZN-7 found that circuitRF's port reader refuses a wave port on a closed guide. It reads a wave port as a mode between two
  conductors, and here the four air-box faces are the only conductors that meet the region. Measured on `WaveguidePost` with renormalisation
  switched on in a scratch copy, converted and dry-run: both ports are refused, each "a wave port on the air box's ymin face, and 4 conductors
  meet its region", with the advice to add Terminals, one per signal conductor. For a hollow guide that advice is wrong: the four faces are one
  conductor, and there is no signal conductor. The EMerge driver also builds every wave port as a
  TEM port (`WavePort(..., modetype="TEM")` in `circuitrf_emerge.py`), and a hollow guide has no TEM mode. Opening these designs without
  this half would let them translate and then fail at run time, which is worse than the refusal they have now.

A hollow waveguide port is useful to circuitRF's own users too: a waveguide filter, a transition, or a horn fed from a guide, drawn in the 3D
editor.

## What to build

### R-hzn11-1 — the format: a wave port normalised to its mode

- A `.c3d` wave port may state `Z0: "mode"` in place of an impedance. Its S-parameters are then normalised to the mode's own power wave, as
  Hertzon's are with `Renormalize: False`.
- `check` accepts `"mode"` on a wave port only. On a lumped port it is an error that names the port.
- `docs/design/c3d-format.md` §7 Ports: say what `"mode"` means, and which solvers honour it.
- The setup panel and the port's Properties offer **Mode** as a choice beside an impedance, as a picker entry (`src/Ui/CLAUDE.md`: prefer a
  picker), never as typed text.

### R-hzn11-2 — the port: a hollow waveguide

- A wave port is a **hollow-guide port** when every conductor that meets its region is one closed metal boundary around it. That boundary is
  either the air box's Pec faces or the walls of one conductor solid. It has no positive or negative conductor.
- `C3dPorts` reads such a port without refusing it. Its mode is the guide's lowest. Its polarity is fixed by a line across the broad dimension,
  through the centre, at right angles to the broad walls, so that S21 of a straight guide keeps a sign that does not depend on the mesh. Say in
  the port's `explain` lines which line was used.
- A guide whose cross-section is not a rectangle is in scope only if the solver needs nothing new for it. Otherwise refuse it by name, and list
  it under **Open**.
- Below the lowest mode's cutoff the port carries no mode. A sweep point there is refused before meshing, naming the cutoff frequency, rather
  than handed to the solver.
- `C3dPorts`' existing two-conductor reading is unchanged. The test that a shielded line still finds its two conductors stays green.
- The many-conductors refusal no longer advises Terminals when the only conductors meeting the region are the air box's Pec faces.

### R-hzn11-3 — the solvers

- **EMerge:** a hollow-guide port is a `WavePort` with a mode type that admits TE and TM modes, not `TEM`. Its integration line is the polarity
  line of R-hzn11-2. A `Z0: "mode"` port writes EMerge's mode-normalised S as it is, with no renormalisation step.
- **Palace:** say whether its wave port solves a hollow guide. If it does, run it and gate it like EMerge. If it does not, refuse it by name in
  `check` and in the dry run.
- **openEMS:** it already refuses a hollow waveguide (`Em3dRunService`). Keep that refusal, and make sure its sentence names the port.
- A `Z0: "mode"` port on a solver that cannot report mode-normalised S is refused in `check`, naming the solver.

### R-hzn11-4 — the result

- A Touchstone file has to state a reference resistance. Decide how a mode-normalised port is written, and record the decision in
  `src/Design/RESOLVED.md` §HZN with the reason. Either:
  - `R 1` with a header comment saying the ports are normalised to their modes, or
  - the mode impedance at each frequency, in the comment lines, with `R 50` and S renormalised to it.
  Whichever is chosen, a Data Display reading the file must not show a 50-ohm renormalisation that did not happen.
- The provenance (`.cemr`) records that the ports were mode-normalised. Comparing a result with a `Z0: "mode"` port against one with a stated
  `Z0` is refused, not silently overlaid.

### R-hzn11-5 — HZN: translate, save and export

- **HZN-2:** a Wave Port with `Renormalize: False` and one mode translates to `Z0: "mode"`. The existing refusals stay: several modes,
  de-embedding, and a mode type other than `Auto`.
- **HZN-2:** a Finite Conductivity boundary on a solid (the 10th design's `Post`) is translated if it is the existing conductive face boundary.
  Otherwise it stays an item, with the reason.
- **HZN-5:** changing a port between a stated impedance and `"mode"` saves as `Renormalize` and `RenormImpedance`, and changes nothing else.
- **HZN-4:** a `Z0: "mode"` port is written as Hertzon writes one: `Renormalize: False`, the `RenormImpedance` key kept as Hertzon writes it,
  `ImpedanceDefinition: Zpv`.

## Gates

- **`C3dHollowWavePortTests`**, one test per claim:
  - An empty WR90 section's S21 has magnitude 1 and phase −βL, with β = sqrt(k0² − (π/a)²), within the tolerance of the mesh it ran at. The
    oracle is closed form, never another circuitRF path (`src/Engine/CLAUDE.md`). This is a solve, so it is run by hand and its numbers are
    recorded; the routine test is the next one.
  - A hollow-guide port resolves with no positive or negative conductor, and with its polarity line where R-hzn11-2 puts it.
  - A sweep point below cutoff is refused, naming the cutoff.
  - `"mode"` on a lumped port is refused.
  - A two-conductor wave port reads as before.
  - A dry run of a hollow guide on EMerge, and on Palace if it is offered, refuses nothing.
- **`HznCoverageTests`** under `CIRCUITRF_HERTZON_HOME`: `WaveguidePost` and the 8 post projects translate. That is 21 of 58, or 22 if the
  copper-boundary post opens too. Record the count and the next items in `src/Design/RESOLVED.md` §HZN.
- **Gate 1:** every newly opened design saves with no edit byte for byte.
- **Gate 2:** a WR90 design with its post moved, saved, passes `hertzon-cli describe`; so does a native hollow-guide `.c3d` exported.
- **Gate 4:** `WaveguidePost` on circuitRF's EMerge against `hertzon-cli solve`, at 3 frequencies across 8–12 GHz, kept to minutes, with the
  tolerance HZN-7 recorded (|ΔS| ≤ 0.035 per entry at converged meshes). Report |S21| and the phase of each. A gap is a finding to record,
  not a reason to tune.

## Risk

Medium.
- **Low:** the translation and the format key.
- **Medium:** the port reader. It decides polarity, and a sign flip in S21 looks plausible on a symmetric guide, so the polarity test must use
  an asymmetric case (a guide with an offset post) as well as the empty section.
- **Medium:** the Touchstone reference (R-hzn11-4). A wrong choice is silent: every tool that reads the file renormalises to the `R` it states.
- **Unknown until tried:** whether EMerge's `WavePort` mode solve on a hollow rectangular face needs anything the driver does not pass (a mode
  count, a target frequency). Try it on the empty section first, and report before building the rest if it does not solve.

## Rules

- **Tests:** one per claim, named classes only. No new `Category=Benchmark` test. No EM solve in a routine test; the oracle run and gate 4 are
  run by hand and their numbers recorded.
- **Runs:** watchdog every solve (memory and time), and keep each one to minutes.
- **UI:** the Mode choice in the port's Properties and the setup panel is rendered with UiSnapshot and looked at.
- **Names:** no commercial tool, PDK or laminate names.
- **Clean room (brief-hzn-0 D1):** what `Renormalize`, `ImpedanceDefinition` and `ModeType` mean comes from Hertzon's files, its
  documentation and black-box runs, never its source.
- **Findings** go in `src/Design/RESOLVED.md` §HZN.

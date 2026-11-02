# Brief 23 — wave ports and eigenmodes: the eigensolver capability

**Series:** [3D EM, second series](brief-em3d-20-overview.md) · **Tag:** `R-em3d23-n` ·
**Design note:** [`em-3d.md`](../design/em-3d.md) §2, §4.4 (reference planes), §7.1 (capability
probe), §10 Validation; F0 findings Q9, Q10
**Area:** `src/Design/Em3d/SolverDiscovery.cs` (capabilities), `src/Design/Em3d/PalaceConfigWriter.cs`,
`src/Design/Em3d/PalaceRun.cs`, `src/Design/Layout/Em3d/Em3dGenerator.cs` (wave-port faces),
`src/Engine/Em3d/Em3dProblem.cs`, `src/Design/Layout/Em/EmSetup*.cs`, the `.cem` panel
**Depends on:** 21 · **Blocks:** 29 (mode shapes), 30
**Owner decision D4:** eigenmode is in this series (default). If the owner takes it out, §4 and gates
6–7 go and the rest stands.

---

## 0. What this brief delivers

Two Palace features that both need an **eigensolver** in the user's Palace build:
- **Wave ports.** A port on the boundary of the air box, excited by the line's own 2D mode. This is how
  a connector, a coax or a waveguide is fed properly, and it removes the ~0.3 nH lumped-port parasitic
  that series 1's brief 7 had to difference out.
- **Eigenmode.** The resonant frequencies and Q of a cavity or package. "Is there a lid resonance
  inside my band?" is a question every package designer has, and no planar tool answers it.

A user-built Palace can lack the eigensolver. The capability probe finds that out in under a second,
before anything runs.

---

## 1. `R-em3d23-1` — the capability probe for the eigensolver

**`R-em3d23-1a`** `SolverCapability` gains `WavePorts` and `Eigenmode`. The mechanism is series 1
brief 6's, unchanged (R-em3d6-3d promised exactly this):
- a `--dry-run` of a tiny generated config naming the feature (F0 Q9: it exits within 0.03 s);
- cached per binary stamp;
- a failed probe is not cached (series 1 review).

**`R-em3d23-1b` What "has an eigensolver" means at the pinned version.** Find out whether `--dry-run`
alone rejects a build without SLEPc or ARPACK, or whether a **one-element solve** is needed. Do this
by reading the pinned Palace source, not by guessing. If `--dry-run` cannot tell, the probe is a
seconds-long solve on a single-tetrahedron cavity, and the probe says which kind it is in `explain`.
Record the finding in `src/Design/RESOLVED.md`.

**`R-em3d23-1c`** A setup that needs a missing capability is refused **before Gmsh**. The refusal
names the capability, the Palace build variant that provides it (`+slepc` or `+arpack`), and
*Install Palace…* (brief 24) as the route to a build that has it.

---

## 2. `R-em3d23-2` — wave ports

**`R-em3d23-2a` A port's `Kind` is `Lumped` (today) or `Wave`.** A wave port must lie on an air-box
face.
- The generator **extends the line to that face**: the air box's padding on that side becomes zero,
  and the conductor runs through it.
- If the port's line does not reach a face, the setup is refused, naming the port and the face it
  would need.

**`R-em3d23-2b` The port face's extent.** A wave port is a region of the boundary face around the
line: its width and height in multiples of the line's width and height above its reference. Use a
published sizing rule for the line type (microstrip, stripline, coax), cited in a comment. The rule is
the starting point, and gate 3 checks that the answer is insensitive to it (±20 % of the extent moves
|S21| by < 0.02 dB). A `.cem` field overrides it per port.

**`R-em3d23-2c` The reference plane** is the port's `Offset` (de-embedding distance), in Palace's own
wave-port field, per §4.4. `explain` states where each port's reference plane is.

**`R-em3d23-2d` What the S-parameters are normalised to.** Find out from a real run of the pinned
version what Palace's wave-port S is referenced to: the mode's own frequency-dependent impedance, or a
stated R. Then:
- If it is the mode impedance, read the port impedance Palace reports, renormalise to the setup's
  stated Z0 with RfCore's existing renormalisation, and say so in the provenance header.
- The `.sNp` always states one real reference impedance per port, and it is the one the numbers are in.

This is the classic place a wave-port S-parameter file is silently wrong, so it gets its own gate (4).

**`R-em3d23-2e`** Wave ports are **Palace only**. openEMS and Both refuse a setup with a wave port,
naming Palace. The openEMS waveguide and microstrip ports are a later brief.

---

## 3. `R-em3d23-3` — mode 1 only

A wave port excites and measures its **first** mode. A port face that supports two propagating modes
in-band (an oversized port) gives an S-parameter that means something different. So if the port
eigenproblem's second mode is propagating in-band, add a **warning**, from Palace's own port-mode
output (`port-*.csv` or its log, whichever the pinned version writes). Multi-mode ports are out of
scope.

---

## 4. `R-em3d23-4` — eigenmode

**`R-em3d23-4a`** `Problem3D: Eigenmode` (brief 22's field). The settings are `Eigenmode.Count` (N) and
`Eigenmode.TargetGHz` (find modes above this), and nothing else unless the pinned schema requires it.

**`R-em3d23-4b` Ports during an eigenmode solve are loads.** Palace treats a lumped port as a resistor
in an eigenproblem, and that is what makes Q loaded rather than unloaded. The result reports Q for
both cases when Palace writes both, and says which is which.

**`R-em3d23-4c` The result:**
- cubes `f` (Hz) and `Q` along axis `Mode`, from the eigenmode CSV read by column name, with the file
  and header found by a real run and committed as a fixture;
- `.npy` key `<key>.palace_eig`;
- no `.sNp`;
- `em` prints a table: mode, f, Q, and, when Palace writes it, the energy participation by domain.
  Participation is what tells a user "this mode lives in the lid cavity".

**`R-em3d23-4d`** Mode fields are saved for the viewer (brief 29) under the same `Save` mechanism brief
29 adds. This brief only makes sure the run directory keeps whatever Palace writes.

---

## 5. Gate

`tests/Ui.Tests/Em3d/PalaceEigenTests.cs`. Closed forms are computed in the test.

1. **Probe, faked.** A stand-in `palace` script that fails the eigensolver dry-run with the pinned
   version's real message makes a wave-port setup refuse. The refusal names `+slepc` / `+arpack`, and
   Gmsh is invoked 0 times. (The script runs on macOS and Linux. On Windows the gate skips with a
   reason.)
2. **Probe, real.** *(Palace.)* The installed Palace reports both capabilities.
3. **WR-90 section, wave ports at both ends**, air-filled, 20 mm long, 9–12 GHz:
   - ∠S21 equals −βℓ with β = √(k₀² − (π/a)²) within **0.5°**;
   - |S11| < −30 dB;
   - ±20 % port extent moves |S21| by < 0.02 dB (2b).
   *(Palace; Benchmark if > 5 s.)*
4. **Normalisation.** For the same guide, the renormalised `.sNp`'s S11 is consistent with a matched
   line **at the stated Z0**. Specifically, re-running with Z0 = 50 Ω and with Z0 = the TE10 wave
   impedance gives files that RfCore converts into one another to 1e-6. *(Palace.)*
5. **Lumped vs wave on a microstrip.** The same 50 Ω microstrip fed by wave ports shows no series
   inductance step. Its |S11| is lower than the lumped version's by the amount the ~0.3 nH parasitic
   predicts at the top of the band, within a factor of 2. *(Palace.)*
6. **Rectangular cavity, PEC walls**, a × b × d:
   - TE101 at f = (c/2)·√((1/a)² + (1/d)²) within **0.1 %**;
   - the next two modes at their closed-form frequencies within 0.2 %.
   *(Palace.)*
7. **Cavity Q.** With finite-conductivity walls, TE101's Q against the closed-form conductor Q within
   **5 %**. *(Palace.)*
8. **Refusals.** Wave port on openEMS, wave port not on a face, eigenmode on openEMS: each refused before
   Gmsh.
9. **Existing files.** Every lumped-port golden is byte-identical, because `Kind` is omitted at
   `Lumped`.

## 6. Owner check

- The port kind picker and the eigenmode table in the panel.

## 7. Scope

- No multi-mode ports, no Floquet ports, no openEMS wave ports.
- No eigenmode on openEMS; FDTD has none (§3).

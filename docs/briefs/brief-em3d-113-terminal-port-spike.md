# Brief 113 — measure first: the terminal-port spike

**Series:** [3D EM, eighth series](brief-em3d-112-overview.md) · **Tag:** `R-em3d113-n` ·
**Precedent:** the F0 findings (`docs/design/em-3d-f0-findings.md`): hand-written solver inputs run against the pinned
binaries before anything was built on them
**Area:** a scratch harness (not committed) and hand-written Palace/openEMS inputs; findings in `src/Design/RESOLVED.md`; the
inputs and outputs that later gates replay, committed under `testdata/em3d/terminal/`
**Depends on:** — · **Blocks:** 114, 115, 116

---

## 0. Why this comes first

Three things the series rests on are, today, **inferences from source code, not measurements**:

1. that two Palace `WavePort` entries sharing a face (mode 1 active, mode 2 inactive) give a usable 2×2 modal S;
2. that `port-V.csv`'s `V_wp` plus that S recovers the terminal voltages, and that `T_I = (T_Vᴴ)⁻¹` holds in Palace's
   power convention;
3. how far an openEMS source must sit behind the reference plane for V and I to be clean.

Each is cheap to measure and expensive to discover after briefs 114–116 are built on it. **This brief ships no product
code.** Its output is numbers, fixtures and a go/no-go per question, recorded in `src/Design/RESOLVED.md` under
"Terminal wave ports — brief-em3d-113", and the decisions in the overview's §3 updated to match.

Keep runs short (a few minutes each, small meshes, a few frequencies) and probe the solvers from a scratch harness, not
from the test tier. The geometries below are all closed-form checkable.

---

## 1. `R-em3d113-1` — Palace: two modes on one face

Hand-write the Palace configuration (start from what `PalaceConfigWriter` emits for a single wave port, so the rest of the
file is known-good), and the Gmsh script from `GmshGeoWriter`'s output for the same box.

**Geometry A, symmetric edge-coupled stripline pair**, air or PTFE filled, thin strips (t/b ≤ 0.02), between two
ground planes, PEC side walls far enough away (≥ 3b) not to matter. 10–20 mm long, wave-port faces at both ends. Two
terminals per face, four Touchstone ports. Homogeneous, so the two modes are **degenerate** (§1d of the overview).

**Geometry B, symmetric microstrip pair** on εr ≈ 3.5, same arrangement. Inhomogeneous, so the modes are **not**
degenerate, and the inactive mode sees a Robin mismatch (overview §1b).

For each, per face: entry `Index` k = 1, 2, `Mode` k, `Active` only on k = 1, the same `Attributes`, `Excitation` k,
`VoltagePath` from the ground to strip k, `Offset` 0.

**Measure and record:**

- **a.** Palace accepts it (0.18.1). Quote the log lines.
- **b.** Whether Palace prints `Port k, mode m: kₙ = …` for the **inactive** entry too (decides overview D6).
- **c.** Run each geometry twice: once with Palace's default `MaxSize`, once with the same `MaxSize` written on every
  entry of a face. Report whether the recovered T_V and the resulting terminal S differ between the two runs (§1d of the
  overview). If the default already gives the same answer, say so; if not, the equal-`MaxSize` rule is required.
- **d.** The terminal transform (overview §1c), by hand in the harness: T_V = V·(1 + S_m)⁻¹, T_I = (T_Vᴴ)⁻¹, U, I, then
  `S = √y·(U − Z₀I)·(U + Z₀I)⁻¹·√z` at Z₀ = 50 Ω per terminal.
- **e.** **Against a closed form.** For A: Cohn's even/odd Z₀ for zero-thickness edge-coupled stripline
  (Z₀e = (30π/√εr)·K(k′e)/K(ke), ke = tanh(πW/2b)·tanh(π(W+S)/2b); odd with coth on the second factor), cited in the
  harness. Build the ideal 4-port of a uniform coupled section of the same length from Z₀e, Z₀o and β = k₀√εr, and
  compare with Palace's terminal S: |S| in dB and ∠S in degrees, worst over frequency. For B: the same comparison against
  circuitRF's planar MoM symmetric coupled-line model (`src/Engine/Mom/ModalDecomposition.cs`, brief L7b), which is an
  independent method for that geometry.
- **f.** **Self-checks** that brief 115 will run on every result: reciprocity ‖S − Sᵀ‖/‖S‖, and passivity (largest
  singular value of S, lossless geometry, ≤ 1 + ε). Report both for A and B, so 115 can set thresholds from data.
- **g.** **The Robin mismatch**, for B: the predicted |(k₁ − k₂)/(k₁ + k₂)| from the printed kₙ, against the error
  measured in (e). Confirms or replaces overview D7's −30 dB threshold.
- **h.** **The power convention.** On a single coax wave port (one terminal), check that |T_V|² from (d) equals Palace's
  `Z_PV` (`port-Z.csv`) to solver precision. That fixes the factor in T_I. A factor of 2 here is the classic peak-versus-RMS
  error, so measure it rather than reasoning about it.
- **i.** Whether `V_wp` is affected by `Offset` (it should not be: it is measured at the face). Settles whether the
  de-embedding shift may be applied by circuitRF after the transform (overview D6).

**Go/no-go:** brief 115 goes ahead as written if (e) for A is within **0.05 dB / 0.5°** and (f) passes. If A fails but B
passes, overview D12's fallback (microstrip pair, with warning) becomes the default, and degenerate faces are refused by
brief 115 with a sentence that says why. If both fail, stop and report to the owner before 114.

---

## 2. `R-em3d113-2` — openEMS: a terminal is a probe pair on a uniform line

Hand-write CSXCAD XML from what `CsxcadWriter` emits today (same grid code, same PML), with the change overview §1g
describes.

**Geometry C, a coax line** (the `3D Connector`'s own: `pin_d` 0.4 mm in `bore_d` 1.34 mm of εr 2.1), 20 mm, terminated in
PML at both ends.
**Geometry D, the stripline pair** of §1, same dimensions.

The port at each end is:

- the line's cross-section **extruded outward** past the air-box face by a feed length L, the box growing by L on that side;
- a soft E-field source (`Excitation Type="0"`) along terminal k's voltage path, at the extension's outer end, in run k
  only;
- at the reference plane (the face, `Offset` 0): a voltage probe (`ProbeBox Type="0"`, weight −1 as our lumped port has it)
  along the voltage path, and a current probe (`Type="1"`) whose box encloses terminal k's conductor and no other.

**Measure and record:**

- **a.** The minimum L at which the result stops changing. Sweep L in multiples of the port region's largest
  transverse size, and report it both in cells and as that multiple. The rule brief 116 adopts is whichever of the two
  holds on both C and D.
- **b.** For C: Z = U/I at the reference plane against 50.06 Ω (closed form, stated in `examples/3D Connector/README.md`),
  and ∠S21 against −βℓ.
- **c.** For D: terminal S against the same Cohn reference as §1e, and against Palace's from §1.
- **d.** Whether `FdtdPortTransform.Solve` needs any change at all (it should not: N terminals are N ports).
- **e.** The current-probe box rule: one cell outside the conductor's cross-section on every side. Report what happens
  when the box touches the shield (coax), so brief 116's refusal sentence describes real behaviour.

**Go/no-go:** brief 116 goes ahead if (b) is within 0.5 Ω and 1°, and (c) is within 0.1 dB / 1° of Palace.

---

## 3. Gate

There is no test gate. The deliverable is:

- the findings section in `src/Design/RESOLVED.md`, every number with its geometry, mesh/grid size, solver version and
  wall-clock;
- `testdata/em3d/terminal/{stripline-pair,microstrip-pair,coax}/`: the Palace configs and `port-S.csv`, `port-V.csv`,
  `port-Z.csv` and log of each go run, and the openEMS probe files of C and D. These are the inputs brief 115's and 116's
  replay gates read, so they never need a solver to run;
- the overview's §3 decisions edited to what was found, each marked as measured.

## 4. Scope

- No product code, no UI, no file-format change.
- Two geometries per solver; anything else is a later brief's.

# Brief EMW-3 — Terminal groups on EMerge: several conductors on one wave-port face

**Tag:** `R-emw3-n` · **Series:** EMW (see `brief-emw-0-overview.md`) · **Depends on:** EMW-2 (the region, the guard, the
shared renormalisation), EMW-1 (`src/Design/RESOLVED.md` § "EMerge wave ports — EMW-1": read it first)
**Area:** `src/Engine/Em3d/ModalTerminalTransform.cs` (one new `ModalRobinForm`), `src/Design/Em3d/PalaceTerminalS.cs`
(its solver-neutral part made shared), `src/Design/Em3d/Solvers/EMerge/` (lowering, driver v6, results, run service),
the EMerge descriptor's `PortRefusal`, `explain`, `check`, Compare, docs. Findings in `src/Design/RESOLVED.md`.
**Out of scope:** the example's EMerge setups (EMW-4); a terminal group whose reference plane is moved (refused, §6); three
or more terminals on a DEGENERATE face (refused, §6).

---

## 0. What EMW-1 settled

- **`ModalTerminalTransform` is reused.** Its C# `Solve` reproduced the spike's Python port to 8e-16 on all four
  EMerge fixtures. On a degenerate face (Pair) it runs unchanged: G = Fit, K = 1, \|ΔS\| ≤ 0.010 from Cohn's ideal line,
  and the fitted g equals the power Gram computed from EMerge's own mode fields.
- **EMerge launches and absorbs every mode of a face at the face's first β**, so a non-degenerate face needs a Robin form
  the transform does not have: **I = M⁻ᴴ K (2 − C)**, K = Re k₁ / Re kₙ. With it the Coupled Microstrip matched Palace to
  ≤ 0.011, passive. Palace's form (2 − K C) gave σ up to 1.14.
- **EMerge's modes carry 1 W**: the transform takes V / √2.
- **The 2D mode solve alone is enough**: U = T_V·C, C = W⁻¹(1 + S), matched the measured-V route to 0.0005 on every case.
  That is what lets a terminal group run under the adaptive sweep.
- **EMerge traps**: its default mode solve misses the second TEM mode of a homogeneous face (a presolve with the shift
  off the TEM eigenvalue fixes it); `align_modes` picks the same mode twice for terminals; a face of 10+ modes collides
  in EMerge's `p.m` numbering; the duplicate-mode defect (EMW-2's guard).

## 1. `R-emw3-1` — the transform's new Robin form

Add `ModalRobinForm.SharedWavenumber`: **every entry is launched and absorbed at its face's first wavenumber**, so
D = K (2 − C) in place of 2 − K C. It is one branch where `D` is formed, plus its doc comment, which states the
derivation in two lines: EMerge's port condition is `γ = j β₁` and its source `−2 j β₁ eₙ` for every mode n, so the modal
current is dₙ = (β₁/βₙ)(2δₙⱼ − Cₙ). **Palace's call site keeps `RealParts`, and every existing transform test and
`PalaceTerminalPortTests` replay stays byte-identical.** On a degenerate face K = 1 and the new form equals the others.

## 2. `R-emw3-2` — `PalaceTerminalS`, made solver-neutral without moving Palace's text

`Faces`, `Leader`, `Convert`, `RouteNotes` and `Checks` hold nothing of Palace's but the words "Palace" and "Palace's Z_PV"
in their sentences, and `Convert`'s fixed `RealParts`. Move them into a shared `WavePortTerminalS` taking the solver's
display name and the Robin form, and leave `PalaceTerminalS` as the Palace-specific reading of `port-S/V/Z.csv` and the
log's kₙ (`Read`, `KnAt`, `KnotFrequenciesHz`), calling the shared code with `"Palace"` and `RealParts`. **Every Palace
note and warning is byte-identical** (the existing replays hold it). EMerge's route note then reads `Port 'Left':
terminals P1, P2 as modes 1 and 2 of one EMerge wave port, converted to terminal S (modes not degenerate, Robin correction
K = 1.051 to 1.060)`. The "Robin correction" wording is the shared sentence; only the solver's name differs.

## 3. `R-emw3-3` — the lowering

`KindRefusal` stops refusing a wave port with a `FaceGroup`. problem.json's `wavePorts` entry gains `terminals`: one
`{number, name, path}` per terminal, in terminal order, the region and the reference shared. A one-terminal wave port is
written exactly as EMW-2 writes it (one entry, no `terminals` key), so EMW-2's goldens do not change. A new golden: the 3D
Wave Ports *Coupled Microstrip*.

## 4. `R-emw3-4` — the driver (version 6)

Per terminal face:

- **`ModalPort(region, number, number_of_modes = terminals, mixed_materials = mixed)`**, never `WavePortIH` (it carries
  one mode). Its integration line is terminal 1's path (it sets the modes' signs; the transform does not care which).
  **Never `align_modes`.**
- **A face of one material (`mixed` false): the presolve.** Before the sweep, `modal_analysis(port, nmodes = terminals + 4,
  target_neff = 0.9·√(εr μr) of that material, TEM = True)` at the sweep's mid frequency, on a port made with
  `modetype = "TEM"`. EMW-1 used `target_neff` 1.3 for √2.1 = 1.449 (ratio 0.90) and `nmodes` 6 for two terminals. A
  port already holding modes is not re-solved by the sweep when it is not `mixed_materials`.
- **More equal-speed modes than terminals is a refusal**, decided from that presolve. Count the TEM modes whose Re β is
  within 1e-3 of the first's. When there are more than the face's terminals, stop with the run's error:
  `Port 'Left''s face carries 5 modes of one speed for 2 terminals: its reference is split into separate conductors on
  the face (two PEC faces with PMC between them are two conductors). Make the reference one conductor on the face, or run
  the setup on openEMS.` EMW-1 found five on Pair with PMC sides. **Fewer** TEM modes than terminals is also a refusal,
  naming what was found (the default solve's failure mode, should the presolve ever miss).
- **A face of several materials**: EMerge's own per-frequency solve, which found the right two modes on the Coupled
  Microstrip. Check each frequency's modes are the face's terminal count and TEM-classified (EMerge classes a quasi-TEM
  mode as TEM); otherwise the same refusal. EMerge's own "variation in port mode propagation constants" warning is
  expected on such a face and is not passed on.
- **What it writes**, per requested frequency and per terminal face (block-diagonal arrays over all wave-port entries,
  `[f, n, n]` complex, n the wave-port entries in circuitRF port order):
  `TV.npy` (path k's integral of mode n's E: the spike's 21-point Gauss-Legendre on the straight path, 7 → 41 points moved
  it by under 5e-5), `W.npy` (the E-overlap EMerge's S projection uses, `<eₙ, eᵢ> / <eᵢ, eᵢ>` over the face's
  triangles), `Gp.npy` (½∫ eₙ × hₘ* · n), and `gamma.npy` (jβ per entry, as EMW-2). Read β, Z0 and the fields from
  `port.get_modes(k0)[m]` and `port_mode_3d_global(…, mode_nr = m + 1)`. **Never** from the result's port properties.
  The spike's `cases.py` computes every one of these, and `tools/emerge-wave-spike/xform.py`'s `exact` is the reference
  arithmetic.
- **Under the adaptive sweep** the modes at each requested frequency come from a 2D solve there (EMW-2's after-sweep
  solve, now with the face's mode count), the guard keeping it fresh. Gate it (§7, test 6) before relying on it: a
  non-degenerate face's basis is fixed per frequency only by EMerge's sign gauge.
- **`V.npy`**, the measured total-field integral of each excitation, only at frequencies the run solved directly (a point
  sweep, or the field frequencies). It is the check of §5, never the transform's input.

## 5. `R-emw3-5` — the result

Per frequency: build a `ModalTerminalSample` from what the driver wrote: `ModalS` = S, `V` = T_V·W⁻¹·(1 + S) / √2 (each
face's block), `ZPv` = |T_V,kk|² / 2, `Kn` = β. Then `WavePortTerminalS.Convert` with `SharedWavenumber`, then the shared
renormalisation (a no-op for terminals, which the transform already put at their Z0) and EMW-2's write path.

**The checks.** `Checks` runs as for Palace, with two EMerge differences:

- **The power-convention check is replaced.** With V formed from T_V it passes by construction, so it tests nothing. In
  its place, where `V.npy` exists: the largest |V_measured / √2 − V_formed| over max|V| per face, as a note. It is a
  **warning above 1 %**: EMW-1 measured 1–2e-4 on homogeneous and one-mode faces, and 4.5e-3 on the Coupled Microstrip's
  ModalPort faces (both meshes). Its wording: the terminal voltages measured in the field disagree with the face's
  modes, so the terminal S is not to be trusted; refine the mesh at the port face.
- **The off-face check** reads the measured V too and is skipped, with a note saying so, where there is none.

The Gram-residual check, the lossless check and the power/singular-value note are unchanged.

## 6. `R-emw3-6` — what EMerge refuses of a terminal group, by name

The EMerge descriptor's `PortRefusal` (`Em3dLegRefusal`, so Compare goes on without it and says so):

- **A terminal group whose reference plane is moved off its face**, naming openEMS. It is Palace's refusal, for a reason
  of EMerge's own: shifting a multimode face means shifting each mode by its own β before the transform, which EMW-1 did
  not measure.
- **A degenerate face with three or more terminals**, naming openEMS. The transform's real Gram fit is stated for two,
  and the field-computed Gram that would remove the limit was measured only for two.
- **More than nine terminals on one face**, naming openEMS (EMerge's `p.m` numbering).
- **A lumped port in the same problem**, naming openEMS, as on Palace: the transform is stated for wave-port entries.

Not refused (Palace refuses it, EMerge does not): **a pair of equal-speed lines**, such as the Pair with PEC sides.

## 7. Tests (targeted; one per claim)

1. **The new form, on constructed inputs** (`ModalTerminalTransformTests`): on a non-degenerate two-mode sample,
   `SharedWavenumber` gives K (2 − C) for the currents (checked against the formula written out in the test), and on a
   degenerate one it equals `RealParts`.
2. **Palace unchanged**: the existing transform tests and the `PalaceTerminalPortTests` replays pass with no expected
   value touched.
3. **The Coupled Microstrip fixture, no EMerge** (`testdata/em3d/terminal/emerge-modal/coupled-microstrip.json`): the
   sample formed as §5 forms it, through `WavePortTerminalS.Convert`, matches Palace's committed terminal S
   (`../coupled-microstrip/palace/CoupledMicrostrip.s4p`) to \|ΔS\| ≤ 0.015 at 2, 5, 7.5 and 11 GHz, and the fixture's
   `expect` (formed from the measured V) to 0.002. Passive: σ_max ≤ 1.001.
4. **The Pair fixture** (`pair-pec-sides.json`): ≤ 0.015 from Cohn's ideal coupled line (Z_even 57.511 Ω, Z_odd 43.10 Ω,
   √2.1, 15 mm; `tools/emerge-wave-spike/xform.py`'s `coupled4` is the reference arithmetic, written again in C# in the
   test), reciprocal to 1e-3, and the route note says the face is degenerate with its g.
5. **Refusals**: each §6 case refused with its sentence, and an equal-speed pair not refused.
6. **Adaptive against point** (`EmergeFact`, `Category=Benchmark`): the Coupled Microstrip at 2–11 GHz, the adaptive
   sweep's terminal S at 2, 5, 7.5 and 11 GHz against a point sweep's at the same frequencies, to 1e-4. If it fails,
   terminal groups run the point sweep and the run says so; record which in RESOLVED.
7. **The Coupled Microstrip end to end** (`EmergeFact`, `Category=Benchmark`), a test workspace's copy of the cell with
   `Solver3D` Emerge: ≤ 0.02 against Palace's committed S4P at its 21 frequencies (EMW-1 measured ≤ 0.014 at four of
   them on its own meshes; Palace and openEMS differ by up to 0.024 on this cell), the route note and no warning. Record
   the worst frequency and its \|ΔS\| in RESOLVED.
8. **The split-reference refusal end to end** (`EmergeFact`): the Pair as drawn (PMC sides) is refused by the presolve's
   count, naming the cause.

## 8. Docs (sources only; the owner regenerates at the series' end)

- `em-setup.md` § Several conductors on one face: an **On EMerge** paragraph. Any number of terminals on a face whose
  modes travel at different speeds, and two on a face whose modes travel at one speed (a stripline pair, which Palace
  refuses). The face's reference must be one conductor there. Plus what the run's notes say.
- `em-solvers.md`: the EMerge column of the capability table and the ports row, now "lumped and wave ports, including
  several terminals on one face".

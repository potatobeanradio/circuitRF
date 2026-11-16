# HZN-20 — wave ports of several modes, and a wave port's reference plane moved by a stated distance

Series: `brief-hzn-0-overview.md`. Needs HZN-11 (hollow guides normalised to their mode) and HZN-16 (a port of several modes).

## Why

After HZN-19, 89 of Hertzon 0.3.26's 99 example designs open. Of the six Showcase designs still refused, `H_TwoModeWaveguide` is the
one that two general capabilities open, and both are worth having in circuitRF beyond Hertzon:

- **A wave port of several modes.** `H_TwoModeWaveguide` is a 22.86 × 15 mm guide, 40 mm long, at 11.2 GHz. Both TE10 (cutoff 6.56 GHz)
  and TE01 (10.0 GHz) propagate there, and a ceramic post tilted 35° in the middle converts one into the other. Each port carries
  `NumberOfModes` 2, so the result is a four-port S-matrix. Refused today as `Wave Port with several modes`.
- **De-embedding.** Port 2 states `Deembed` True with `DeembedDistance` 5mm. Refused today as `Wave Port with de-embedding`, the
  only other item behind the first (probed with the modes set to 1). It is also one of `B_WaveguideIrisFilter_Half`'s five blockers.

That makes **90 of 99**. Nothing else in the Showcase file is close: C needs four material and setup features (one of them a magnetic
loss tangent Palace cannot model), E four unrelated ones, B five, A's chamfers name vertices by index alone, and D has over 25
constructs.

## Established from Hertzon's own documentation (never its source)

- **Mode names and numbering** (tutorial §9): the modes of a port are named `P1_M1`, `P1_M2`…, and terminals are "numbered in port
  order" (1 = `P1_…`, 2 = the next of port 1, then port 2's). Mode 1 is the dominant one: EMerge 3.0.6's `WavePort(number_of_modes=N)`
  orders modes by the largest β² (changelog 0.3.16, 0.3.21).
- **Each mode is absorbed with its own propagation constant**, and its Gamma and Zo rows are its own (changelog, "Wave ports with
  several modes"; feature matrix). With Renormalize off, each mode's S is normalised to that mode, as HZN-11 established for one.
- **De-embedding direction** (changelog 0.3.16): "De-embedding below cutoff uses the decaying port mode (exp(alpha d))". S is
  multiplied by e^{γd}: a positive distance removes guide, moving the reference plane into the structure. The feature matrix
  says it "uses the solved port propagation constant".

## What is already there

- **The engine's reference-plane shift is Hertzon's convention exactly.** `Em3dReferencePlane.ShiftM` is positive into the structure,
  and `WavePortReferences.Shift` applies S′ᵢⱼ = Sᵢⱼ·e^{γᵢdᵢ}·e^{γⱼdⱼ}, each port's γ its own mode's, before any renormalisation.
  EMerge applies it from the run's γ (`Em3dRunService.Emerge`), and Palace writes it as the wave port's `Offset`
  (`PalaceConfigWriter`). Only layout-made wave ports set it (`Em3dGenerator`); a `.c3d` port has no key for it.
- **Several modes on one face.** EMerge 3.0.0's `ModalPort(..., number_of_modes=n)`, which circuitRF's driver already uses for a
  terminal group (brief-emw-3: one `ModalPort`, one mode per terminal). HZN-16's Floquet port is the model in the problem: one
  `Em3dPort` per mode, the modes of one port sharing a `FaceGroup`, numbered consecutively, each mode-normalised.
- **A hollow guide's single mode** (HZN-11): its polarity line crosses the narrow side at the broad side's centre, and the driver
  sets the mode's sign by a row and column flip, because EMerge 3.0.0's `align_modes` does not work on a wave port.

## What to build

### R-hzn20-1 — a hollow wave port of several modes
- **Format.** `.c3d` port key `Modes` (integer, 1 when omitted, written only above 1), on a hollow-guide wave port. The port's modes
  are result ports `Number`, `Number + 1`, …, in cutoff order, as a Floquet port's are. The run names them `P<n>_M<m>` in the
  `.sNp` header and the DataSet's port axis.
- **Which modes, v1.** A rectangular opening (HZN-11's reading) carries its TE and TM modes in cutoff order. Version 1 takes up to two:
  - mode 1 is TE10 across the broad side;
  - mode 2 is whichever of TE01 and TE20 has the lower cutoff.

  Equal cutoffs within a part in 10⁴ are refused by name: the modes are degenerate, and which field a port of one of them carries is
  undefined (HZN-11 already refuses a square guide). More than two modes are refused by name.
- **Polarity.** Each mode has a polarity line through a maximum of its transverse field, from the lower wall to the higher:
  - TE10: across the narrow side at the broad side's centre (as now);
  - TE01: across the broad side at the narrow side's centre;
  - TE20: across the narrow side at a quarter of the broad side.

  The driver sets each mode's sign by its line, as HZN-11 does for one.
- **Below cutoff.** A sweep point below any mode's cutoff is refused by name, as HZN-11 refuses one below the first mode's. Hertzon
  lets such a mode decay at the port; that is a later addition, and `H_TwoModeWaveguide` solves at 11.2 GHz, above both.
- **Solvers.** EMerge runs it, with one `ModalPort` with `number_of_modes = Modes`, each mode's S row and column taken by its order and
  signed by its line. Palace and openEMS refuse it in `Em3dModePorts.SolverRefusal` ("…which circuitRF runs on EMerge only"), as
  HZN-16 does for a Floquet port.

### R-hzn20-2 — a wave port's reference plane moved by a stated distance
- **Format.** `.c3d` port key `DeembedUm` (number, micrometres, positive into the structure, written only when not 0), on a wave port.
  It sets `Em3dReferencePlane.ShiftM`. A lumped port with it is refused by `check`, since a lumped port's reference is its sheet.
- **Solvers.** EMerge and Palace already apply the shift, so a single-mode wave port runs on both. On a port of several modes each
  mode shifts by its own γ, which needs the run's γ per mode; the EMerge reader must keep one per result port, not one per face
  (verify, and fix if it does not).
- **Terminal groups** keep EMW-3's refusal of a moved reference plane: the terminal conversion mixes modes, and shifting before it was
  never measured.

### R-hzn20-3 — translation, save and export
- `NumberOfModes` N on a hollow wave port sets `Modes`; on any other wave port it stays refused.
- Port numbers follow Hertzon's: a port's modes take consecutive result numbers in port order.
- `Deembed` True with `DeembedDistance` d sets `DeembedUm` from d (an expression is evaluated, with a note). `Deembed` False with a
  distance writes nothing, and the distance is listed as unused.
- `C3dToHzn` writes `NumberOfModes` and `Deembed`/`DeembedDistance` back. The patcher already writes a changed port.

### R-hzn20-4 — UI
- The Properties panel for a wave port shows **Modes** (only for a hollow guide; it says which modes, e.g. "TE10, TE01") and
  **De-embed** (a length with the design's unit), each one undo entry. The canvas label of a port of several modes reads `P1 (2
  modes)`.

## Gates

- **`HznMultiModeWavePortTests`** (Ui.Tests/Hzn, a synthetic two-mode guide written by a script, never Hertzon's file):
  - `NumberOfModes` 2 translates to `Modes` 2, the result ports are numbered 1–4 in port order, and port 2 carries `DeembedUm` 5000;
  - the problem has two `Em3dPort`s per port sharing a `FaceGroup`, each hollow and mode-normalised, with the polarity lines above;
  - a square opening, three modes, and a sweep point below the second mode's cutoff are each refused by name;
  - Palace and openEMS refuse a port of several modes, and a lumped port with `DeembedUm` is refused by `check`;
  - export writes `NumberOfModes` and the de-embedding back.
- **Against the analytic guide (EMerge, short runs).**
  - *Empty guide*, 22.86 × 15 mm, 40 mm, 11.2 GHz, two modes per port: S(P2_M1, P1_M1) = e^{−jβ₁₀L} and S(P2_M2, P1_M2) = e^{−jβ₀₁L},
    each within 1°. The cross terms and reflections are below 0.02.
  - *De-embedding*: the same guide with port 2 shifted 5 mm: each mode's S21 phase advances by exactly β_m·d (to the solver's
    precision, since the shift is applied after the solve).
  - *`H_TwoModeWaveguide`* (the real file, `CIRCUITRF_HERTZON_HOME`): it runs, S is reciprocal (Sᵢⱼ = Sⱼᵢ within 0.01), passive
    (each column's power at most 1), and the post converts: |S(P2_M2, P1_M1)| well above the empty guide's.
  - With `hertzon-cli` restored, compare against Hertzon's own run of `H_TwoModeWaveguide`. Without it, say so in the completion note.
- **Census: 90 of 99** under `CIRCUITRF_HERTZON_HOME`.
- **UI:** UiSnapshot of a hollow wave port's Properties (Modes and De-embed) and of the canvas label. Look at each and list it.

## Risk

- **EMerge's mode order and sign on a two-mode face.** Hertzon's changelog records EMerge returning high-order modes in an overmoded
  guide, and signs that flip between adaptive passes (fixed in EMerge `767fb7b`, after circuitRF's 3.0.0). The empty-guide gate is
  what catches it. If 3.0.0 still does it, the driver must pick the modes itself (by cutoff, from the face's own dimensions) or the
  feature waits for a newer EMerge. Find out before building the UI.
- **No comparison with Hertzon** unless `hertzon-cli` is restored (this download has no `.venv`).
- **De-embedding on a port of several modes** depends on the per-mode γ in the EMerge reader (R-hzn20-2).

## Rules

As brief-hzn-10. Two other agents share the repo: stage only this brief's files, and commit with explicit paths (the index may hold
another agent's staged work).

## Completion note (2026-11-16)

Built as above. **Census 89 → 90 of 99.**

- **Risk answered first:** EMerge 3.0.0 fixed TE10 then TE01 on the two-mode face, each signed correctly, so the driver keeps
  EMerge's order and checks it (each mode must be largest along its own polarity line, or the run is refused).
- **Empty guide** (EMerge, Draft): ∠S31 within 0.13° and ∠S42 within 0.53° of e^{−jβL}; cross terms and reflections ≤ 0.0026.
- **De-embedding:** port 2 moved 5 mm advanced S31 by 54.5168° (β₁₀d 54.5169°) and S42 by 30.3641° (β₀₁d 30.366°).
- **`H_TwoModeWaveguide`** (Setup_PortsOnly): runs in 8 s, reciprocal, passive (column power ≤ 0.9999), and the post converts:
  |S(P2_M2, P1_M1)| 0.26–0.28 against 1e-4 in the empty guide.
- **No comparison with Hertzon's own run**: this download has no `hertzon-cli`.
- Beyond the brief: the 3D editor draws each mode's polarity line (it drew only a port's first neutral port), a port's Number
  edit checks the numbers its modes take, and the Hertzon export refuses a moved reference plane on a terminal port rather than
  dropping it. The user reference's line saying EMerge refuses a hollow guide (stale since HZN-11) now says it runs one.
- Gate: `tests/Ui.Tests/Hzn/HznMultiModeWavePortTests` (5 tests) passes, as do the neighbouring hollow-guide, periodic-cell, export,
  EMerge-lowering, terminal and port-map classes (303 tests). `Firewall.Tests` fails two user-facing-text checks, both on
  transient, envelope and expression files this brief did not touch.
- UI looked at with UiSnapshot: the Properties page of a two-mode port (Modes picker "2" with "TE10, TE01" beside it, De-embed
  "5 mm", rows aligned) and the canvas (`P1 (2 modes)`, `P2 (2 modes)`, both polarity lines drawn).

Findings: `src/Design/RESOLVED.md` §HZN-20.

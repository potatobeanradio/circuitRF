# Brief 141 — A Palace wave port's S is published in its mode's arbitrary phase

**Tag:** `R-em3d141-n`
**Area:** `src/Design/Em3d/Solvers/Palace/Em3dRunService.Palace.cs` (the wave-port branch, ~line 430), `src/Design/Em3d/WavePortReferences.cs`, `src/Design/Em3d/PalaceRun.cs` (`ReadWavePortV`), `src/Engine/Em3d/ModalTerminalTransform.cs` (its one-entry-face claim).
Findings go in `src/Design/RESOLVED.md`. Keep Palace runs short: the fixture below solves one frequency in 20 s.

## The gap

A two-port Palace run with ordinary (ungrouped) wave ports can publish a **non-reciprocal** S-matrix: |S₂₁| = |S₁₂| but the two phases differ. On `testdata/em-psv/via-two-dielectrics` (a buried line on one level, a via, a top line on another) |S₂₁ − S₁₂| is 0.11 / 0.28 / 0.34 at 1 / 5 / 10 GHz, and 0.09 / 0.27 / 0.37 on the same layout in one dielectric (`one-region/`). Planar and EMerge are reciprocal to 0 and 2e-4 on both, and agree with each other to 0.033. Palace's published result is 0.19 from either.

The cause, measured at 10 GHz from Palace's own `postpro`:

- **Palace's `port-S.csv` is already non-reciprocal**: arg S₂₁ = −65.25°, arg S₁₂ = −45.18°. circuitRF's renormalisation from Z_PV to 50 Ω is a change of real reference and carries the asymmetry through unchanged.
- **Unit-power normalisation fixes a mode's magnitude, not its phase.** Each port's mode voltage, read from `port-V.csv` as V₁₁/(1 + S₁₁) and V₂₁/S₂₁, has |v|² = Z_PV to 1e-4 and 4e-3, and **phase −9.86° on port 1 against +0.41° on port 2**. Palace's S_ij then carries e^{j(θᵢ − θⱼ)}, which is antisymmetric in i and j and explains the whole split.
- **Rotating each port by its own mode-voltage phase** (S′ = D S D⁻¹, D = diag(e^{jθᵢ})) before the renormalisation gives |S₂₁ − S₁₂| = 0.008, |ΔS| 0.025 against planar and 0.020 against EMerge. That is inside the two independent solvers' own agreement.
- **Why nothing showed it before:** every Palace wave-port fixture so far has two ports of the same cross-section, so θ₁ = θ₂ and the factor cancels. Port 1 here is a line on a buried level, port 2 a line on the top surface.
- **The terminal path is not affected.** `ModalTerminalTransform` forms the terminal voltages from V_wp directly (U = M P = V), so a grouped face already carries the phase. Its header's statement that a one-entry face "reduces to renormalising that port from its Z_PV" is true only when that port's mode voltage is real.

## Decision made

**D1. The phase is applied in the renormalisation path, not by routing every Palace wave port through the terminal transform.** A one-entry face already goes through the renormalisation, and the change is one diagonal phase per port per frequency. Routing ungrouped ports through the transform would also run its Gram fit, its K and its checks on faces that do not need them. The terminal path stays exactly as it is.

## Requirements

- **R-em3d141-1 Reproduce first.** Run `testdata/em-psv/via-two-dielectrics/via/em/emerge.cem` with `--solver palace` at 10 GHz only (a scratch copy; ~20 s). Confirm the raw `port-S.csv` asymmetry and the two mode-voltage phases above before changing anything.
- **R-em3d141-2 The phase.** For every wave port and frequency, take θᵢ = arg vᵢ, with vᵢ the port's mode voltage along its stated voltage path, and apply S′ = D S D⁻¹ before `WavePortReferences.Renormalise` and before any reference-plane `Shift` (both are diagonal, so the order between those two is free). Read vᵢ from the port's own excitation, Vᵢᵢ/(1 + Sᵢᵢ). Where |1 + Sᵢᵢ| is small (a port looking into a short), take it from the excitation with the largest |Sᵢⱼ|, and say in a note which one was used.
- **R-em3d141-3 What has no voltage path.** A wave port with no voltage path writes no V_wp. Decide from the code whether every wave port circuitRF writes states one. If some do not, publish their S with a warning naming the port and the possible phase error. Never publish it silently.
- **R-em3d141-4 Say it in the run.** One note per run: each wave port's mode-voltage phase range over the sweep, and that the S was referred to it. Add the largest |S_ij − S_ji| before and after to the existing reciprocity checks, so a future regression shows in the run's own notes.
- **R-em3d141-5 Correct the comment** in `ModalTerminalTransform`'s header (a one-entry face reduces to the renormalisation *and* the phase), and any `.sNp` provenance line that describes the conversion.
- **R-em3d141-6 Re-record** `testdata/em-psv/via-two-dielectrics/refs/palace.s2p` and `one-region/refs/palace.s2p` (adaptive 1 / 5 / 10 GHz, as committed), and update that README's Palace paragraph from "not a reference" to the measured agreement.

## Gates

1. **Reciprocal.** On the fixture above, |S₂₁ − S₁₂| ≤ 0.01 at every frequency, on both stacks.
2. **Agrees.** Palace against planar and against EMerge, worst |ΔS| within the 0.033 the other two show against each other at 10 GHz, and no worse than that pair's own band at 1 and 5 GHz.
3. **Nothing symmetric moves.** Every committed Palace wave-port reference whose two ports share a cross-section (the TFR-V `thru`/`zigzag` runs and the wave-port examples) is unchanged to its stored precision. Diagonal entries are unchanged by construction; off-diagonals move only by e^{j(θᵢ − θⱼ)}, so this is a statement about θᵢ = θⱼ on those fixtures. Check it from their `port-V.csv` where the run is kept, rather than re-running every one.
4. **The terminal path is untouched.** brief-em3d-115's gates pass unchanged.
5. **One test per claim**, on recorded CSV rows rather than a Palace process: the rotation makes a recorded non-reciprocal row reciprocal, a row with θ₁ = θ₂ is bit-identical, and the small-|1 + Sᵢᵢ| fallback picks the other column.

## Out of scope

- EMerge's and openEMS's wave ports, which are reciprocal on this fixture.
- Why Palace's mode on port 1 comes out at −9.86°. It is a free phase of an eigenvector and not an error in Palace. What circuitRF needs is to state it.

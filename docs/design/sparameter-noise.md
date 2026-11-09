# Small-signal noise in the S-parameter analysis

Design note for `Noise=yes` on an `analysis … type=sparam`. Code: `src/Engine/SParameterNoise.cs`,
`src/RfCore/Data/NoiseMetrics.cs`, `SnpModel.NoiseAt`, the OSDI worker's `noise` command
(`tools/osdi-worker/README.md`, Noise), and `nf`/`nf_circle` in `src/Core/Expressions/Evaluator.Network.cs`.
User reference: `docs/user/src/reference/simulations.md#sp-noise`. Findings: the 2026-11-09 entries in
`src/RfCore/RESOLVED.md`, `src/Engine/RESOLVED.md`, `src/Core/RESOLVED.md` and `tools/osdi-worker/RESOLVED.md`.

## 1. Method

The noise runs on the network the wave path already factorises, with every port terminated in its own noiseless Z0.
This is the adjoint method on that terminated assembly: no new matrix and no new factorisation.

- **Noise waves.** With no drive, port k's outgoing power wave is `b_k = (√Re Z0_k / Z0_k)·V_k`. The port-voltage row
  of `M⁻¹` is one transposed solve per port on the same `SparseLU` the S columns used. `SolveTranspose` solves
  `Mᴴy = e`, so the row is `conj(y)`. For each noisy part e, with node-current correlation `C_e` (A²/Hz):
  `Cs = D·Σ_e H_e·C_e·H_eᴴ·Dᴴ`, where `H_e[k, a] = conj(y_k[node_a])` and `D = diag(√Re Z0 / Z0)`. The cost is N
  back-substitutions per frequency.
- **Passive parts: Bosma / Twiss.** Each eligible part (linear, `ActivityFor` passive; a `Mutual` together with the
  inductors it couples) is recorded from its own stamp, every node tied to ground through R. With `Z_t` the
  terminated part's impedance matrix, the open-circuit noise is `C_V = kT[2(Z_t + Z_tᴴ) − (4/R)·Z_t·Z_tᴴ]` and the
  node currents are `C_j = Z_t⁺·C_V·Z_t⁺ᴴ`, with an SVD pseudo-inverse. Where Y exists this is `2kT(Y + Yᴴ)`. An ideal
  tie (a short, an ideal transformer) makes `Z_t` singular, and the pseudo-inverse drops exactly the injections no
  node voltage sees. A lossless part comes out as zero. T is `Temp`, then `Dtemp` over the ambient, then the ambient.
  The parts are recorded before `StampAll`, because a stamp writes the model's branch index.
- **SnP noise data.** A two-port file's noise rows give `C_A = 4kT0·[[Rn, (Fmin−1)/2 − Rn·Yopt*], [(Fmin−1)/2 −
  Rn·Yopt, Rn·|Yopt|²]]`, linear in Fmin, Γopt and Rn between rows, and the end rows are held outside them. It goes
  onto the block's nodes as `C_Y = T⁻¹·C_A·T⁻ᴴ` with `T⁻¹ = [[−Y11, 1], [−Y21, 0]]`, Y at the file's R, and onto a
  floating reference node with minus the port currents. A passive file with no noise rows is thermal (Bosma); a
  file with gain and none, and every Deembed, are noiseless.
- **OSDI devices.** The worker evaluates the model at the DC bias the run linearises it about, with
  `CALC_NOISE|ANALYSIS_NOISE`, and calls `load_noise` per frequency. Each declared source between device nodes
  (a, b) is a current source on the circuit nodes they map to, times the instance multiplicity.
- **Two-port parameters.** `C_Y = (I + Y·Z0)·R^−½·Cs·R^−½·(I + Y·Z0)ᴴ`, then `C_A = T·C_Y·Tᴴ` and
  `Rn = C11/4kT0`, `Bopt = Im C12/C11`, `Gopt = √(C22/C11 − Bopt²)`, `Fmin = 1 + (Re C12 + C11·Gopt)/2kT0`.
  NF from port 1's Z0 is `1 + Cs22/(kT0·|S21|²)`. Y is the power-wave Y: S is renormalised to Re Z0 first, because
  `RFNetwork.SToY` is the pseudo-wave form.

## 2. Conventions

| Quantity | Convention |
|---|---|
| Spectral densities | Single-sided, absolute: a matched load's outgoing wave is kT W/Hz; k = 1.380649e-23 J/K exact. |
| `Cs` | `⟨b·bᴴ⟩` of the outgoing power waves (Kurokawa), every port in its own noiseless Z0, W/Hz. |
| NF, NFmin | dB, referred to T0 = 290 K. The default ambient is 26.85 °C = 300 K, so a design that states no temperature is not at T0. |
| `GammaOpt`, `Rn` | Γopt on Re Z0 of port 1; Rn in Ω. Touchstone writes Γopt and rn = Rn/R on the option line's R. |
| Data-file noise | Absolute at T0; it does not move with the ambient. |
| Refused | `Noise=yes` with a port whose Re Z0 ≤ 0 (`sparam.noise.reactive-z0`): nothing terminates it. |
| Noiseless, named once | Built-in nonlinear models, active or black-box linear parts, an SnP with gain and no noise data, every Deembed, an external device with no declared sources (`sparam.noise.noiseless`). Ports and independent sources are noiseless silently. |

`Noise=no` (the default) changes nothing: the same cubes, S bit for bit and the same run statistics.

## 3. Not in scope

Harmonic-balance noise, phase noise, an AC noise analysis, and noise in the built-in nonlinear models. NF and the
noise parameters are two-port quantities; an N-port gets `Cs` only.

## 4. References

- H. Hillbrand and P. H. Russer, "An efficient method for computer aided noise analysis of linear amplifier networks,"
  *IEEE Trans. Circuits Syst.*, vol. 23, no. 4, 1976. Correlation matrices and their transformations.
- S. W. Wedge and D. B. Rutledge, "Wave techniques for noise modeling and measurement," *IEEE Trans. Microw. Theory
  Techn.*, vol. 40, no. 11, 1992. Noise waves, and the noise parameters straight from them.
- H. Bosma, "On the theory of linear noisy systems," *Philips Res. Rep. Suppl.*, no. 10, 1967. `Cs = kT(I − S·Sᴴ)`
  for a passive network at one temperature.
- R. Q. Twiss, "Nyquist's and Thevenin's theorems generalized for nonreciprocal linear networks," *J. Appl. Phys.*,
  vol. 26, 1955.
- D. M. Pozar, *Microwave Engineering*, 4th ed., §12 (noise figure, noise circles; Ex. 12.5).

## 5. Validation

`testdata/noise/` (generator `gen_noise_refs.py`, references in `refs.json`, written outside circuitRF):

- closed forms: a matched pad at 290 K and 400 K, Bosma's `kT(I − S·Sᴴ)` entry by entry on a lossy reactive
  network, coupled inductors and an ideal transformer, and the Friis cascade;
- scikit-rf: a mismatched cascade (`connect`), Touchstone noise data at R = 50 and 25 Ω with re-referencing, and
  Pozar's noise circle (`nf_circle`);
- ngspice 47 `.sp … donoise`: mixed temperatures, coupled inductors, a controlled-source amplifier with 75/50 Ω ports;
- the fake OSDI `crf_noise` device against its closed form.

Gates: `tests/RfCore.Tests/NoiseMetricsTests.cs`, `TouchstoneNoiseBlockTests.cs`,
`tests/Engine.Tests/Linear/SParamNoiseTests.cs`, `tests/Engine.Tests/External/OsdiNoiseTests.cs`, and
`tests/Core.Tests/Expressions/NetworkMetricFunctionTests.cs` for `nf`/`nf_circle`.

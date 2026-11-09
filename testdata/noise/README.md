# Noise reference data

Reference values for the S-parameter noise analysis. Every circuit here is synthetic, and every
expected value is computed outside circuitRF by `gen_noise_refs.py`. It uses closed forms, a small
numpy nodal solver (noise by the adjoint method), scikit-rf and ngspice. To regenerate the files,
run the script with numpy, scikit-rf (2.1.0 was used) and ngspice (47 was used) on `PATH`:

    python3 gen_noise_refs.py      # rewrites *.cnl, *.s2p and refs.json in this folder

The output is deterministic. The script asserts that its references agree, and it stops if any
two of them differ by more than 1% of the tolerance.

`plot_noise_compare.py` draws `docs/images/sparam-noise/*.png`, circuitRF against these references.
It runs in two steps, because circuitRF and the references usually live in different environments:

    python3 plot_noise_compare.py run  <CircuitRF.Cli.dll> <results>   # needs dotnet only
    python3 plot_noise_compare.py plot <results> ../../docs/images/sparam-noise

`run` runs every bench here with `Noise=yes`, the ngspice benches again on a 50-point grid, the
`S-Parameter Noise` example's first two benches, and the fake OSDI part over its bias (it needs
`tools/osdi-worker` and `tools/fake-osdi-model` built). `plot` needs what the generator needs, plus
matplotlib, and reruns ngspice and scikit-rf live where a figure wants more points than `refs.json` has.

## Using the benches

The `.cnl` files leave out the noise key, so they still parse with a circuitRF that has no noise
analysis. A test adds ` Noise=yes` to the end of the `analysis SP1 type=sparam …` line. Each sweep
lands exactly on the frequency grid of its `.s2p` files, so interpolation cannot change any
expected value.

`circuitrf check` passes on every bench with 0 errors. A bench that sets `temp =` also gets one
informational warning: "Ambient temperature … from the global 'temp'".

## Conventions

| Quantity | Convention |
|---|---|
| Spectral densities | Single-sided and absolute. |
| `Cs` | `Cs = <b bᴴ>`: the outgoing power waves, with every port terminated in its own noiseless Z0, in W/Hz. |
| Passive part at temperature T | `Cs = kT(I − SSᴴ)`. |
| NF | At T0 = 290 K, with the source at port 1's Z0: `F = 1 + Cs22/(k·T0·\|S21\|²)`. |
| `GammaOpt` | Referenced to port 1's Z0. |
| `Rn` | In Ω. |
| `k` | 1.380649e-23 J/K, exact. |
| Ambient | `temp = 16.85` is 290 K. A bench with no `temp` line runs at circuitRF's default ambient, 26.85 °C = 300 K. |
| Data-file (SnP) noise | Absolute, at T0, so the ambient does not change it. |

## `refs.json`

```
schema, generator, constants {k, q, T0}, conventions
benches.<name>:
  netlist        "<name>.cnl" (null for osdi_crf_noise)
  description, ambient_K, ports_z0_ohm [z1, z2], freq_hz [F]
  expected       nf_db [F], nfmin_db [F], rn_ohm [F], gamma_opt_re [F], gamma_opt_im [F],
                 cs_re [F][2][2], cs_im [F][2][2]  (W/Hz)
  tolerance      db_abs: |NF − ref| and |NFmin − ref|, in dB
                 rn_rel: |Rn − ref| / |ref|
                 gamma_abs: |GammaOpt − ref|, as a complex magnitude
                 cs_rel: max over entries |Cs − ref| / max |ref|, per frequency
  sources        which reference each expected quantity comes from
  agreement      the largest differences found between independent references (same units)
  extras         friis_matched.inputs; touchstone_*.file_reference_ohm and file_gamma_opt_*;
                 pozar_ex12_5.circle; osdi_crf_noise.formula, points and notes
```

## Benches

| Bench | Circuit | Reference |
|---|---|---|
| `pad6_290K`, `pad6_400K` | Matched 6 dB π pad, 50 Ω | Closed form `F = 1 + (T/T0)(L−1)`. `Cs = kT(1−1/L)I`. |
| `lossy_lc` | Unmatched R/L/C 2-port at the default 300 K | Bosma `Cs` (equals the adjoint nodal `Cs`). `F = 1 + (T/T0)(1/G_A − 1)`. |
| `friis_matched` | `amp1.s2p` – 3 dB pad – `amp2.s2p`, all matched, Γopt = 0 | Friis closed form. Also checked against a numpy C_A chain and scikit-rf `connect`. |
| `friis_mismatched` | `ampA.s2p` – lossy R/C interstage – `ampB.s2p` | scikit-rf `connect`, with the interstage's Bosma C_A attached. Equal to numpy nodal. |
| `touchstone_r50`, `touchstone_r25` | `ampA.s2p` (R 50) and `noise_r25.s2p` (R 25), each on ports at its own R | NFmin, Rn and Γopt as in the file. Also checked with scikit-rf. |
| `touchstone_r25_in_50` | `noise_r25.s2p` between 50 Ω ports | The file's values, with Γopt re-referenced to 50 Ω. Also checked with scikit-rf `renormalize`. |
| `pozar_ex12_5` | Pozar, *Microwave Engineering* 4th ed., Ex. 12.5 (4 GHz data) | Noise circle at 2 dB, from the closed form and scikit-rf `nf_circle`. |
| `ngspice_mixed_temp` | R at 290 K, with `Temp=` resistors at 400 K and 77 K | ngspice `.sp … donoise`. Equal to numpy nodal. |
| `ngspice_coupled_l` | Two lossy inductors coupled by `Mutual`, k = 0.7 | ngspice. Equal to numpy nodal. |
| `ngspice_vccs_amp` | VCCS amplifier with a feedback R; port 1 at 75 Ω, port 2 at 50 Ω | ngspice. Equal to numpy nodal. |
| `osdi_crf_noise` | The fake OSDI `crf_noise` as a shunt at a DC-fed node (no `.cnl`) | Closed form. |

### `osdi_crf_noise`

The test builds this bench itself:

```
port 1 – C 1 F – X – C 1 F – port 2
crf_noise from X to ground
Vdc feeding X through L = 1 H
```

The device's noise current PSD is `S_i = 4kTg + 2q|g·Vdc|`. The bench gives:

```
F   = 1 + (T/T0)·g·Z0 + 2q|g·Vdc|·Z0/(4kT0)
S21 = 2/(2+gZ0)
Cs  = S_i·Z0/(2+gZ0)² · [[1,1],[1,1]]
```

The shunt loading by g cancels out of F exactly. The noise is white. NFmin, Rn and Γopt are
degenerate here (Rn → 0, Γopt → −1), so they are not references. `points` lists g ∈ {1, 4} mS,
Vdc ∈ {0.5, 1} V and T ∈ {300, 350} K, all at 1 GHz.

### ngspice 47 conventions

These were established against numpy nodal on the same circuits:

- **Units.** `NF` and `NFmin` are in dB (10·log10). `Rn` is in Ω, not normalised. `SOpt` is
  referenced to port 1's `z0`, and that holds with unequal port impedances too.
- **`Cy_i_j`.** The absolute short-circuit current correlation in A²/Hz, written as `i(cy_i_j)`.
- **Reference temperature.** ngspice uses the circuit temperature, not 290 K. Its `NF`, `NFmin`
  and `Rn` divide by k·T_circuit. At ngspice's default 27 °C this gives:
  - `F_ng − 1 = (F − 1)·290/300.15`, and `Fmin` likewise;
  - `Rn_ng = Rn·290/300.15`;
  - `SOpt` unchanged.

  The generator checks this relation and then runs every reference deck at `temp = 16.85`, where
  the two conventions coincide.
- **Per-instance temperature.** `R … temp=<°C>` sets that resistor's noise temperature.
- **Boltzmann constant.** ngspice and scikit-rf use CODATA 2014's k = 1.38064852e-23.
  - Noise figures and noise parameters are ratios, so this k cancels out of them.
  - The absolute `Cy` from ngspice is rescaled to the exact k.
  - The exact-k C_A attached to scikit-rf is rescaled to scikit-rf's k.

## Notes

- Pozar's printed answers are rounded: N = 0.1019, centre 0.56∠100°, radius 0.24. The exact values
  are N = 0.101895, centre 0.5627∠100° and radius 0.2454. `circle.textbook.tol` is 1e-2.
- Touchstone values are written with 15 significant digits. Each expected value is derived from
  the file as it is read back, not from the unrounded inputs.

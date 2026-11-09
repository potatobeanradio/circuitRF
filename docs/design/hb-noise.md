# Noise in harmonic balance

Design note for `Noise=yes` on an `analysis … type=hb` (brief NZ-5). Code: `src/Engine/HarmonicBalance/HbNoise.cs`, its
call sites in `HbEngine` (single tone, and the two-tone lattice), `HbEngine.NoiseRequest` (the keys), the noise seam
`SParameterNoise.Sources` (passive parts, file data, specified amplifiers), `ComponentModel.EvaluateNoise` and
`NoiseDensitiesOver` (devices), and the OSDI worker's `noise_batch` (`tools/osdi-worker/README.md`, Noise). User reference:
`docs/user/src/reference/simulations.md#hb-noise`. The conventions are the S-parameter noise analysis's
(`sparameter-noise.md`): single-sided absolute densities, NF referred to T0 = 290 K, parts at their own temperature.

## 1. The system

The small-signal sweep's conversion matrix (`HbSmallSignal.BuildJss`, `docs/design/stability-wsprobe.md` §10) relates
the interface voltages at the sidebands `ω_s = ω + Ω_s` of an output frequency ω: `Ω_s = k·ω0` on one tone, a mixing
frequency of the lattice on two. Nothing here builds a second matrix.

Write `A_s` for the full linear partition at `ω_s` (the extractor's MNA, conjugated below zero), `P` for the selection of
the interface nodes, `Y_s` for the interface admittance `(P·A_s⁻¹·Pᵀ)⁻¹`, and `D = J_ss − diag(Y_s)` for the devices' part
of the conversion matrix. A noise current `u_s` injected into the nodes at sideband s gives

```
A_s·x_s = u_s − Pᵀ·i_s,    i = D·v,    v_s = P·x_s,
```

so `J_ss·v = diag(Y_s)·v_oc` with `v_oc,s = P·A_s⁻¹·u_s`. The output is the outgoing wave of the `NoiseOut` port at the
zero sideband, `y = rᵀ·x_0` with `r = (√Re Z0 / Z0)·(e₊ − e₋)` and `Z0` the port's impedance at ω.

## 2. The adjoint

Substituting, `y = Σ_s a_sᵀ·u_s` with

```
a_s = δ_s0·w − z_s,
A_0ᵀ·w   = r                                (one transposed sparse solve at ω)
J_ssᵀ·p  = Dᵀ·(e_0 ⊗ P·w)                    (one transposed dense solve)
A_sᵀ·z_s = Pᵀ·(Y_sᵀ·p_s)                     (one transposed sparse solve per sideband)
```

One output frequency costs one dense factorisation and `S + 1` sparse solves, whatever the number of sources.
CSparse's transposed solve is Hermitian (`src/Engine/RESOLVED.md`): at `+|ω|` the answer to `Aᵀ·x = b` is
`conj(solveᴴ(conj b))`, and at `−|ω|`, whose matrix is `conj(A)`, it is `solveᴴ(b)` itself.

The output noise is `N = Σ_{s,t} a_sᵀ·C_st·conj(a_t)` with `C_st = ⟨u_s·u_tᴴ⟩` (A²/Hz on the source's nodes).

## 3. Sources

**Stationary** sources are uncorrelated between sidebands: `C_st = δ_st·C(ω_s)`. `C(ω)` for `ω < 0` is `conj(C(|ω|))`,
because the sideband at a negative frequency is the conjugate of the positive-frequency component (the same rule as
`Y(−ω) = conj Y(ω)`; no model is evaluated at a negative frequency). These are:

- every entry of `SParameterNoise.Sources` — passive parts by Bosma at `|ω_s|`, SnP noise data, an amplifier's `NF` —
  with the composed FET's parasitics at the temperature of the operating point's DC component;
- the `NoiseIn` port's termination at T0: `4kT0·Re(1/Z(|ω_s|))` across the port, Z the band impedance a P1Tone
  presents there (`P1ToneModel.ImpedanceAt`), or a Port's/Term's Z;
- a Mixer's `NF` (§5).

**Cyclostationary** sources are a nonlinear device's declared noise sources. Each density `p(τ)` is read at every time
sample τ of the converged large-signal waveform. A source `j(τ) = m(τ)·n(τ)` with white `n` and `m² = p` has

```
⟨J_s·J_tᴴ⟩ = P[s − t],     P[d] = the two-sided Fourier coefficient of p at the mixing difference d,
```

looked up through `ISidebands.DifferenceIndex`, the same bookkeeping `G⁽²⁾[s − t]` uses — so the sign and the factor of
two (`P = full-amplitude / 2` off DC, conjugated through a negation) are the conversion matrix's own. A correlated pair
(`NoiseSourceDecl.CorrelatedWith`) adds `ρ·F[√(p_i·p_j)]` on its incidences, and `conj(ρ)·…` on the transpose.

On one tone the samples are the solve's own FFT grid, up to the conversion reach `Kj = min(2K, N/2)`. On two tones the
waveform is re-synthesised on the order-2·MaxMixOrder APFT torus `HbSmallSignal.LatticeSpectra` evaluates the devices
on, so every sideband difference resolves.

## 4. Frequency dependence — the approximation

A density is `White + FlickerCoeff/f^Af`. A built-in model's two parts are read per sample and transformed separately,
so both are modulated by the waveform. **The flicker part of `P[s − t]` is the modulated coefficient's coefficient times
`(|f_s|·|f_t|)^(−Af/2)`** — at s = t that is the sideband's own `1/f^Af`, and between two sidebands it is the geometric
mean of the two. That is an approximation: a 1/f process modulated by a waveform is not exactly a white process modulated
the same way and then shaped, and the cross-sideband term of real flicker noise depends on the physics behind it. It is
exact for white noise, exact on the diagonal, and it reduces to the S-parameter analysis's `1/f` at zero drive.

A compiled device's densities come from its worker per frequency, not split. It is asked once per operating point for
every sample at every sideband frequency the sweep visits, and `P[s − t]` is the Fourier coefficient of
`√(p(τ, |f_s|)·p(τ, |f_t|))`: the same geometric mean, exact for a density that is white or a pure power law, and between
the two where it is a sum of both.

## 5. A Mixer's NF

The system Mixer is a memoryless multiplier with matched ports, so it converts both sidebands equally. Its stated noise is
an equivalent current source across its RF port, `4k·T_e/Zrf` with `T_e = T0·(F_DSB − 1)` — the mixer referred to its own
matched termination. `NFType=ssb` (the default) is taken as `F_DSB = F_SSB/2`. An SSB figure below 3 dB is below a
noiseless DSB mixer's, so the source is zero and the run warns.

## 6. Noise figure

With `N_in,s` the output noise from the input termination at sideband s, `N` the total, and `sig` the `NoiseSideband`:

```
F_SSB = N / N_in,sig         F_DSB = N / (N_in,sig + N_in,image)        image = the sideband at −Ω_sig
Gc    = N_in,sig / (k·T0)    Te    = T0·(F_SSB − 1)                     Nout  = N
```

`NFdsb` is written only when the signal and image sidebands differ. `NFc` is each part's output noise over
`N_in,sig`, with the input termination's other sidebands as one entry, so `Σ NFc = F_SSB − 1`.

**NFsb** is each sideband's share of `N` from the stationary sources. A cyclostationary device is not split by sideband: its
`(s, t)` terms are large and cancel. A diode switched by the LO conducts only half the cycle, and its per-sideband terms
were ±20 % of the output noise while its net share was 2 %. So NFsb sums to 100 % less the devices' share, which is on
`NFc`.

## 7. Refusals and edge cases

- `Noise=yes` with no `SSStart`/`SSStop` is refused, naming them; a bad `NoiseIn`/`NoiseOut` or `NoiseSideband` is refused,
  and so is a port number no top-level Port, Term or P1Tone carries.
- A signal sideband outside the retained set (`SSMaxHarm`) is refused.
- An output frequency where `2ω` is a mixing frequency is degenerate (a sideband and the negative of another coincide),
  exactly as for the WSProbe sweep; its noise is NaN, with one warning. A singular conversion matrix is NaN too.
- Three or more tones are refused, as the small-signal sweep refuses them. The older two-tone grid path
  (`HbTwoToneOnLattice=false`) refuses noise.
- A free-running circuit is refused as any HB run of one is (oscillators are briefs NZ-9/10).

## 8. Gates, as measured

Tests: `tests/Engine.Tests/HarmonicBalance/HbNoiseTests.cs`, `HbNoiseExternalTests.cs`, and
`OsdiNoiseTests.NoiseBatch_EqualsNoise_StateByState`.

1. **Small-signal limit.** A BJT LNA (shot, flicker, Rb thermal; lossy load, bias resistor) at −200 dBm: NF equals the
   S-parameter noise analysis's to 4e-10 dB at four frequencies. The S-parameter reference needs its DC pre-pass
   converged to 1e-14 A: at the default 1e-6 A the 150 kΩ base bias is 2.5e-7 V short, 1e-5 of gm, which is ten times the
   gate's tolerance. A compiled device through `noise_batch` meets the same gate to 2e-7 dB.
2. **Ideal multiplier.** `i_IF = g·v_RF·v_LO` with every port a matched resistor at T0: SSB 3.0103 dB, DSB 0 dB, image
   share 50 %, `Gc = (gA/2)²·4R_L·R_s`, all to 1e-9; with a matched shunt at the RF port, 6.0206 dB and Gc −6.02 dB.
3. **Switching diode.** One diode in series between two 50 Ω ports, switched by a 20 V LO, against an ideal switch of
   duty ½: Gc −9.955 dB against −9.943, NF 6.907 dB against 6.859 at K = 11. The closed form's own error: the diode's
   ~0.8 V turn-on makes its duty ≈0.487 (−0.1 dB in NF), its on resistance costs Gc a few hundredths of a dB, and its
   shot noise is 2.2 % of F − 1 (+0.1 dB). Tolerance 0.05 dB on Gc and 0.2 dB on NF.
4. **Blocker.** The LNA, its input P1dB found by bisection (−24.1 dBm): NF at P1dB − 30 is the small-signal NF to
   0.001 dB, and rises to +0.11 dB at P1dB − 10 and +0.93 dB at P1dB.
5. **Batching.** Two compiled instances in a driven run: one batch each, no single-bias request.
6. **No key, no change.** A probed HB run with `Noise` absent and with `Noise=no` carries the same cubes, value for value,
   the WSProbe's small-signal outputs included, and `Noise=yes` changes none of them. The noise code runs only on a
   non-null request.

## 9. In a loadpull (brief NZ-6)

`Noise=yes` on a `loadpull` (and on a `loadpull_pursuit`, for its follow-on loadpull) reads this analysis at every converged
rung of every grid point's drive ladder: `LoadpullEngine.RunOneTermination` keeps each accepted rung's single-point solve
and, after the ladder, hands it to `HbEngine.NoiseAt` — the same `HbNoise.Add` over an `ssfreq` axis of one point, on the
rung's own conductance and capacitance spectra (`HbEngine.SinglePointLinearization`). The grid point's tuner override is
still in force then, which is what the sideband partitions are stamped from; the drive is the right-hand side only. A
pursuit's search calls `RunOneTermination` outside `Run` and reads no noise. The result is `NF` [gridPoint, pinStep], NaN
where a rung did not converge or was never reached, beside the FOM cubes; the `.spl` writer and the contour metric list
carry it.

**Ports.** `NoiseIn`/`NoiseOut` default to the source and load tuners (`HbNoiseRequest.InTuner`/`OutTuner`). A tuner's port
is its RF termination, the Z_Port the grid overrides: a source tuner's runs from its drive node (held by the drive, a short
to every small-signal current) to its DC block, a load tuner's from its DC block to its reference, at the band impedance
`TunerModel.ImpedanceAt` stamps. A tuner is noiseless by definition, as a port is (`SParameterNoise.IsNoiselessByDefinition`);
the input termination is at T0, as in §3.

**The frequency.** The tone itself is degenerate (§7), and a reading just beside it is not one number: under drive the
conversion matrix carries baseband noise onto the output at the offset's own frequency. On the BJT LNA of §8, from −50 to
−30 dBm, NF read 1 kHz above a 1 GHz tone goes 3.6 → 8.2 → 17.4 dB (flicker at 1 kHz, and the base's 150 kΩ bias network
open at 1 kHz), and 2.54 → 2.62 → 3.29 dB 10 MHz above it. So `NoiseFreq` absent reads 1 % above the tone
(`LoadpullEngine.DefaultNoiseOffset`), the noise a small signal beside the drive sees; a stated frequency on a
half-multiple of the tone is moved 1e-6 of the tone up (`DegenerateNoiseOffset`). Both are said in a run note and by
`explain --analysis`.

**Gates** (`tests/Engine.Tests/Loadpull/LoadpullNoiseTests.cs`):

1. **Small-signal rung.** A sourcepull of the LNA at −130 dBm over five terminations: NF equals the S-parameter noise
   analysis on the same terminations to 6e-8 dB. "The same" includes the tuner's ideal bias tee — a 1 F DC block and a
   1 H choke: against bare ports the loadpull is 3.8e-6 dB low, and so is the S-parameter analysis with the tee drawn.
   Physically the tee is nothing at 1 GHz (1e-10 Ω, 6e9 Ω); the shift is the solve's precision with a j6e9 S admittance
   beside 0.02 S (a 1 mF block moves it back to 1e-9 dB, and the choke's value does not matter). Every loadpull number
   carries the same ~1e-6 relative error from it.
2. **Contours are circles.** The small-signal sourcepull on 85 terminations (rings 0.1 apart to |Γ| = 0.7), its NF contours
   drawn by the Data Display's own fit and extractor, against `ns_circle` from the S-parameter Fmin, Sopt and Rn at
   NFmin + 0.25, 0.5 and 1 dB: centres within 4.2e-3 and radii within 2.6e-3 in Γ, every contour point within 1.5e-2 of
   the circle. That is the RBF's interpolation error on that grid; the grid points themselves are gate 1's.
3. **No key, no change.** `Noise` absent and `Noise=no` give the same cubes value for value and the same `.spl` bytes;
   `Noise=yes` adds `NF` and changes nothing else.

The `.spl` reader maps a column only through `LoadpullFomDialect.Map` and skips a header it does not name, every other
column read at its own position — so `SplReader` from before `NF` reads a file with it (a test shows this with an
unknown column).

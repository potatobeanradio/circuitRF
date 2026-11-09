# Brief NZ-7 — noise and phase noise in the envelope analysis

Series: `brief-nz-0-overview.md`. Depends on NZ-3 (`NF=` on the system blocks); NZ-1's seam.

**Why.** The envelope analysis runs modulated signals through a circuit and reports ACLR, NMSE, AM-AM/AM-PM and the
output spectrum. Every signal in it is noiseless. An LO is a perfect tone. So the spectrum shows no noise floor, a
blocker never mixes LO phase noise into the channel, and NZ-8's EVM would be limited only by distortion. A receiver is
limited by noise.

## What to build

### 1. Thermal noise in an envelope run — `Noise=yes` on `type=envelope`

- **Which parts.** Every part that `SParameterNoise.Sources` makes noisy is noisy in the run: passive parts at their own
  temperature, SnP blocks with noise data, and the system blocks with `NF=` (NZ-3).
- **How it is injected.** At each harmonic band k of the envelope (and each lattice product, for a two-fundamental
  run), a part's noise is a white complex Gaussian current on its nodes. Its correlation is the part's `NoiseCurrents`
  at that band's centre frequency, scaled to the envelope's sample bandwidth.
  - Draw it through a Cholesky factor of that matrix, computed once per part per band per run.
  - Add the draws into the step's right-hand side, beside the drive.
  - Noise is white across one envelope band. That is the approximation, and it is stated in the docs: a part whose noise
    changes noticeably across the signal bandwidth is evaluated at the band centre.
- **What stays out.** A nonlinear device's own noise (NZ-4) varies with its instantaneous state inside a step, and is
  not injected in this brief. Devices are named in the run's noiseless warning, worded for the envelope: "noise not
  modelled in envelope". A later brief can add them once NZ-5's cyclostationary sources exist.
- **Seed.** `Seed=` (default 1) seeds the run. The same seed gives the same bits on every platform: use one counter-based
  generator, not `System.Random`. A `parametric_sweep` uses the same seed at every point, so a swept result's
  differences are the circuit's, not the dice's. `SeedPerPoint=yes` opts out.

### 2. A phase-noise mask on any source

- **The key.** `PhaseNoise="<offset>:<dBc/Hz>, …"` on P1Tone, the tone sources, PnTone and MOD_Source. For example,
  `PhaseNoise="1kHz:-80, 10kHz:-100, 100kHz:-120, 1MHz:-140"`.
  - It is single-sided L(f), and it is interpolated linearly in log-frequency against dB between points.
  - `PhaseNoiseFloor=` sets the level beyond the last point. Default: the last point's level.
  - Below the first point, the slope of the first segment continues down to the record's own frequency resolution.
- **What it does.**
  - The source's drive is multiplied by `exp(j·φ(t))` at the fundamental, and by `exp(j·k·φ(t))` at harmonic k.
  - `φ(t)` is a Gaussian process with S_φ(f) = 2·10^(L(f)/10), made by shaping seeded white noise in the frequency
    domain over the record, with the run's `Seed`.
  - The mask above the envelope's bandwidth cannot be represented. The run says so once, giving the first offset that
    falls outside.
- **In other analyses.** HB, loadpull and S-parameters run at steady state and ignore the mask. `check` reports it as
  INFO on those analyses, never as an error. Converting LO phase noise in HB noise (NZ-5) is out of scope.

### 3. The noise floor shows up in the existing results

`PSD`, `VSD` and `ACLR` already integrate whatever is in the record. With noise on, the PSD shows the floor and ACLR
includes it. Nothing new is written in this brief. NZ-8 adds the signal-quality measures.

## Gates

1. **The linear limit.** A matched pad, and separately an Amplifier block with `NF=3`, driven by a CW tone with
   `Noise=yes`. The output PSD's noise floor away from the tone is `k·T0·F·G` per Hz, to within the statistical error of
   the record length, which the test computes and states (no fixed "close enough").
2. **A phase-noise mask comes back.** One `P1Tone` with a mask, into a matched load. The output PSD around the carrier,
   averaged over 16 seeds, follows the mask to ±1 dB over the offsets the record resolves.
3. **Reciprocal mixing.** An ideal mixer whose LO has a mask, and an RF blocker at offset Δ from the wanted channel. The
   in-channel output noise equals `P_blocker + L(Δ) + 10·log10(B_channel)` (the closed form) to ±1 dB.
4. **Determinism.** Two runs with the same seed are bit-identical. Changing the seed changes the noise and nothing else.
5. **No key, no change.** An envelope run with `Noise` absent and no `PhaseNoise` anywhere is byte-identical.

# Brief NZ-3 — noise figure on the system blocks

Series: `brief-nz-0-overview.md`. Depends on NZ-1 (the `Noise=` key).

**Why.** The system blocks in `src/Core/Devices/System` let a receiver chain be drawn before any transistor exists:
Amplifier, Attenuator, Filter, Coupler, Switch, Circulator, Duplexer and the rest. The passive ones are already thermal
under PR #50, because `SystemBlockPassivity` declares them passive. The Amplifier has gain, IP3, return losses and
isolation, but no noise figure, so a cascade's NF cannot be built from it. The Mixer has no noise figure either.

## What to build

### 1. `NF=` on the Amplifier

`NF=<dB>`, default absent. Absent means noiseless and named in the warning, which is today's behaviour, unchanged.

**What `NF` means.** It is the block's noise figure when driven from a source equal to its own `Zin` (conjugate, as
`Zin` is defined), measured at T0. That is the number on a datasheet, so it must come back out of the analysis exactly.

**The noise model.**
- An input-referred noise wave of temperature `Te = T0·(10^(NF/10) − 1)`, entering port 1, uncorrelated with anything,
  plus nothing at port 2.
- As a chain correlation, that is a noise voltage and current whose noise parameters are `Fmin = F`, `Γopt` = the
  conjugate of the block's own input reflection, and the `Rn` that goes with them.
- Write it through `NoiseMetrics.ChainCorrelation` and `AdmittanceFromChain`, which already turn noise parameters into
  node currents for a Touchstone block (`SParameterNoise.DataFile`). The block's noise is then exactly a two-port with
  noise data, and every downstream path treats it the same way.
- **Optional `NFopt=` and `Rn=`** (default: `NFopt = NF`, `Rn` from the above) describe a block whose noise match is not
  its power match. With `Gopt=` (complex, default as above) they give the full four noise parameters. That is the
  datasheet form of a packaged LNA.

**Mismatch.** A source other than `Zin` raises the NF along the ordinary noise-parameter curve. Nothing extra is needed:
it falls out of the analysis.

### 2. `NF=` on the Mixer

`NF=<dB>`, `NFType=ssb|dsb` (default `ssb`). These are stored on the model now and used by NZ-5's HB noise and NZ-7's
envelope noise. In the S-parameter analysis a mixer has no conversion path (the LO is not there), so it stays out of
the small-signal noise exactly as it stays out of the small-signal S today. `explain` says so.

### 3. The other system blocks

- Confirm, with one test each, that every passive system block is thermal at its `Temp=` today: Attenuator, Filter,
  Coupler, Switch, Circulator, Duplexer, Balun, IdealSBlock when passive.
- Every active one, other than the two above, is named as noiseless. `PimOverlay` adds no noise.
- Any block whose `ActivityFor` is wrong at some setting is a defect to fix here, not a reason to special-case noise.

### 4. Surfaces

The keys go to the parameter editor, the `.cnl`/`.csch` round trip, `reference` (MCP and CLI) and the user docs'
Components page. `explain`'s noise table (NZ-1) lists an amplifier as **specified NF**, with the value.

## Gates

1. **The datasheet number comes back.** Amplifier with `NF=3`, `Zin = Zout = 50`, matched ports: NF = 3 dB to 1e-9. NFmin
   is 3 dB and Sopt is 0.
2. **Friis.** Amplifier (G1, NF1), Attenuator (L), Amplifier (G2, NF2), all matched: NF equals the cascade closed form to
   1e-9. That is the same check as PR #50's Friis bench, built from system blocks.
3. **Mismatch.** An amplifier with `Zin = 25 Ω` driven from 50 Ω: NF follows the noise-parameter formula at that Γs.
4. **Four-parameter form.** `NFopt`, `Gopt` and `Rn` round-trip through NFmin, Sopt and Rn to 1e-9.
5. **No key, no change.** A system design with no `NF=` anywhere is byte-identical.

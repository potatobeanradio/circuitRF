# Brief NZ-1 — which part sets the noise, and the per-part noise keys

Series: `brief-nz-0-overview.md`. Depends on nothing past PR #50.

**Why.** The S-parameter noise analysis reports a two-port's noise figure, but not which part produces the noise. Finding
the part that dominates means re-running with parts changed one at a time.

There is also no way to say "this part is noiseless". A resistor that stands in for an instrument's load, or that is part
of a model, is always thermal.

Two inputs the analysis should honour are dropped without a word:
- `Temp=` on an S-parameter block. `CnlReader.IsIgnoredSnpParam` skips it, so a passive block's noise uses the ambient.
- `Noise=` on an S-parameter block, skipped by the same function.

A resistive `Z_Port` is reported as noiseless, because the type declares itself active for every instance.

## What to build

### 1. Each part's contribution — new cubes beside `Cs`

The adjoint solve already maps every part's noise currents onto the port waves. So each part's own share of `Cs` is
`D·H·C_part·Hᴴ·Dᴴ`, computed at no extra solve.

- **`NCs` [freq, part, i, j]** (W/Hz): each noisy part's contribution to `Cs`.
  - The part axis is labelled by instance path, in `Sources` order.
  - A coupled group (a `Mutual` and its inductors) is one entry, labelled with its members joined by `+`.
  - The sum over parts equals `Cs` to round-off. **That is the gate.**
- **For a two-port, `NFc` [freq, part]** (linear, dimensionless): each part's share of the noise factor's excess.
  - Definition: `F − 1 = Σ_part NFc`, with port 1 driven from its Z0, and NFc the part's own contribution to the
    output noise at port 2 divided by the source's.
  - **`NFpct` [freq, part]** is the same as a percentage of `F − 1`. That is the number a designer sorts by.
- **Cost.** A part's contribution is a small dense product per frequency. With hundreds of noisy parts and hundreds of
  frequencies, `NCs` is the size to watch.
  - Write `NCs` only when `NoiseContrib=yes` (default `no`).
  - Always write `NFc`/`NFpct` for a two-port: they are [freq, part] reals.
  - State the size in `explain --analysis`.

### 2. `Noise=no` on any part

- **The key.** `Noise=yes|no`, default `yes`, is accepted on every part `SParameterNoise.Sources` can make noisy:
  - R, the lossy L and C, every transmission line;
  - the coupled inductors, where `Noise=no` on any member quiets the whole group, and `explain` says so;
  - transformers with loss, `SnP` (the `IsIgnoredSnpParam` entry goes; the key is now read), external (OSDI) devices;
  - and, after briefs 3 and 4, the system blocks and the built-in models.
- **Its effect.** A part with `Noise=no` contributes nothing. It is not listed in the noiseless warning, because it is
  the user's statement and not a gap.
- **On a part that can never be noisy** (a port, an independent source, an ideal element), the key is accepted and
  does nothing. `check` reports it as INFO: "has no noise to switch off".
- **Every surface.** The `.cnl` reader and writer, the `.csch` parameter list, the parameter editor (a checkbox in the
  part's own parameter list, not a dialog), and the symbol's parameter text when it is not the default.

### 3. `Temp=` on an S-parameter block

The reader keeps `Temp=`/`Dtemp=` on `SnP` (and on `Deembed`, where it is read and does nothing, as on any element whose
noise is not thermal). A passive block without noise data is then thermal at its own temperature, through
`Temperature.ResolveDeviceC` as every other part is. A block with noise data is unaffected, because file data is absolute.

### 4. A resistive `Z_Port` is thermal

- `Z_Port`'s per-instance passivity: when the instance's Z (or Y) has `Re ⪰ 0` at the run's frequencies, it is thermal by
  Bosma, like any passive linear part.
- When it is not, it stays named as noiseless.
- The decision is per instance and per run, through the same `ActivityFor(freqs)` call `IsThermal` already uses.

### 5. `explain` lists the noise model of every part

`circuitrf explain <path> --analysis <SP>` gains a noise table when the analysis has `Noise=yes`. Each part is listed
once:
- **thermal**, at its resolved temperature in K;
- **file data**, with the file and the band of its noise rows;
- **device sources**, with the count and their names;
- **switched off** by `Noise=no`;
- or **noiseless**, with the reason.

`--json` carries the same table. An agent driving circuitRF reads this before trusting an NF, the same way it reads the
analysis chain.

## Gates

1. **Contributions sum to the total.** Σ `NCs` = `Cs` and Σ `NFc` = F − 1, to 1e-12 relative. Check on the mixed-temperature, coupled-inductor and controlled-source benches in `testdata/noise`, and on the Friis cascade.
2. **A known split.** In a matched pad, a series R and a shunt R contribute in the closed-form ratio of their
   available-noise powers at the output.
3. **`Noise=no`.** On every resistor of a pad, NF = 0 dB exactly and `Cs` = 0. On one resistor, the result equals the
   result with that resistor's contribution subtracted.
4. **`Temp=` on a passive `.s2p` block** gives Te = T·(L − 1) for a matched attenuator file at 20 K and 400 K.
5. **No key, no change.** With `NoiseContrib` absent and no `Noise=no` anywhere, output is byte-identical to PR #50's.
6. **`explain` table.** One test per row kind, against a bench holding one of each.

## Out of scope

- Contributions for the HB noise of brief 5. That brief reuses this cube layout.
- A contribution plot preset in the Data Display. A bar chart of `NFpct` against part at one frequency is a trace on an
  ordinary rectangular plot with the part axis as x, and needs nothing new. Show that in the brief-11 example.

## Also in this brief

Remove the `ponytail:` marker from the two comments in `src/Engine/SParameterNoise.cs` and `src/Engine/RESOLVED.md`. The
note itself stays: a coupled group at mixed temperatures is taken at the hottest member.

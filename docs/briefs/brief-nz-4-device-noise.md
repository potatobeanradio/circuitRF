# Brief NZ-4 — noise in the built-in nonlinear models and the ComposedFET

Series: `brief-nz-0-overview.md`. Depends on NZ-1 (`Noise=` and the contributions cubes).

**Why.** The built-in nonlinear models accept `Kf` and `Af` and use neither. Their own doc comments say so:
`DiodeModel`, `BjtModel` and `MosfetModelBase` each list flicker noise among what "this model cannot answer". The FET
models (Statz, Curtice quadratic and cubic, Materka, Angelov), the ComposedFET (`.cmodel`) and the SDD carry no noise at
all. So the S-parameter noise analysis names every one of them as noiseless. A designer can only get an LNA's NF from a
compiled Verilog-A model or a measured `.s2p`, never from the device models circuitRF ships or composes.

Compiled (OSDI) models already bring their own noise, through the worker's `noise` command (PR #50). This brief gives
the in-process models the same.

## The seam — one interface, written for HB as well as for S-parameters

Add to `ComponentModel`, next to `Evaluate`:

```csharp
/// The noise sources this model declares: each a current between two of its nodes (internal nodes included).
public virtual IReadOnlyList<NoiseSourceDecl> NoiseSources => [];

/// Each source's density at the instantaneous state v (the same node voltages Evaluate takes) and the device
/// temperature, split into a white part (A²/Hz) and a flicker part, density = white + flickerCoeff / f^flickerExp.
public virtual void EvaluateNoise(ReadOnlySpan<double> v, double kelvin, Span<NoiseDensity> into) { }
```

- **The state is the instantaneous one, on purpose.** In the S-parameter analysis it is the DC operating point. In NZ-5's
  HB noise it is each time sample of the large-signal waveform. The modulated densities ARE the cyclostationary
  sources. So no model is written twice.
- **Uncorrelated by default.** A model that needs correlated sources (induced gate noise in a MOSFET channel, at high
  frequency) declares them as a pair with a complex correlation coefficient. Leave that hook in the declaration, and
  implement it only where a model below needs it.
- **Wiring.** `SParameterNoise.Sources` gains one branch: a model with `NoiseSources.Count > 0` is evaluated at the DC
  bias through the same `BuildBias` path the OSDI devices use, and placed on its nodes. That is exactly
  `DeviceSources`, now fed by an in-process model. Make `ExternalDeviceModel` implement the same interface, so there is
  ONE branch, not two.
- **Temperature.** `kelvin` is the device's own temperature from `Temperature.ResolveDeviceC`. With self-heating it is
  ambient + the thermal node's rise at the operating point, not the ambient. A model with a thermal node reads it from
  `v`, as `Evaluate` does.

## The models

| Model | Sources | Parameters (existing ones used; new ones in **bold**, all default 0 = off unless stated) |
|---|---|---|
| Diode | Shot `2q·|Id|` on the junction; flicker `Kf·|Id|^Af / f`; thermal of `Rs` | `Kf`, `Af` (default 1) |
| BJT | Base and collector shot; flicker on the base current; thermal of `Rb`, `Re`, `Rc` | `Kf`, `Af` |
| MOSFET Level 1/3, VDMOS | Channel thermal `(8/3)·k·T·gm` (the long-channel value; **`Gamma`** overrides 2/3); flicker `Kf·Id^Af / (Cox·L²·f)` as the models state their geometry; thermal of the series resistances | `Kf`, `Af`, **`Gamma`** |
| Statz, Curtice ×2, Materka, Angelov | The two-temperature FET form: the gate-side intrinsic resistance thermal at **`Tg`**, the drain-source output conductance thermal at **`Td`**; thermal of the extrinsic `Rg`, `Rd`, `Rs` at the device temperature; flicker on the drain current | **`Tg`** (default: the device temperature), **`Td`** (default: off, which leaves the noiseless-warning behaviour), `Kf`, `Af` |
| SDD | Not in this brief. An SDD stays named as noiseless. A future `noise` port type would describe its sources in the same equation language | — |

The two-temperature form is the one RF transistor noise is usually characterised in. **`Td` can be a bias expression**
(it is a parameter like any other), which is how a fitted drain temperature that rises with current is entered.

## ComposedFET (`.cmodel`)

- **The parasitic network is thermal** at the device temperature, including the self-heated one, through the same
  Bosma path every passive part uses. Its elements are elaborated as ordinary linear parts inside the instance
  (`ComposedNetworkTemplate`), so this should need no new code: confirm it with a test, or fix the one place that hides
  them.
- **The core gets a `Noise` block** in `model.json`: `{ "Tg": <expr or table>, "Td": <expr or table>, "Kf", "Af" }`.
  - Each value is an equation, a table or a constant, the same constituent kinds the core's currents use, evaluated at
    the core's own terminal voltages and temperature.
  - It is optional. A `.cmodel` without it reads and runs exactly as today, and its core is named as noiseless.
- **`CmodelFile`** reads, validates and writes the block. A newer file read by this build is refused by version, as
  the format already does.
- **The Model Composer** gets a Noise step:
  - It reads measured noise parameters: a `.s2p` noise block, or a table of NFmin, Γopt and Rn against frequency at
    stated biases.
  - It de-embeds the composed parasitics, using the model's own extrinsic network.
  - It fits `Td` (and `Tg`, if the data supports both) per bias.
  - It shows measured against modelled NFmin, Γopt and Rn, with the fit error stated.
  - This is the step a device modeller uses. Keep it to that one fit, and do no other kind of noise extraction.

## Verilog-A, and the vendor library worker

- **Compiled Verilog-A (OSDI)** already declares noise (PR #50). This brief only puts it behind the same interface. It
  also confirms with a test that the worker is told the instance's resolved temperature (`temperatureK`, which the
  worker already reads), so a `Temp=` on a compiled device changes its thermal noise.
- **The compiled vendor model library worker** (`tools/senior-worker`): find out whether its model ABI exposes noise.
  - If it does, wire it to the same interface.
  - If it does not, its devices stay named as noiseless, and the user docs say so plainly.
  - Report the finding in `src/Core/RESOLVED.md`. Do not guess densities.

## Gates

1. **Closed forms, one per source kind, on a single device at a stated bias.** Diode shot (NF of a forward-biased
   junction across a port), BJT collector shot, MOSFET channel thermal (output noise current `(8/3)kTgm`), a FET with
   only `Td` (output noise `4k·Td·gds`), flicker slope −10 dB/decade with `Af = 1`. Each to 1e-9 relative.
2. **The OSDI equivalence.** The crf_noise test device of PR #50 and an in-process model with the same sources give
   the same NF to 1e-12, through the single wiring branch.
3. **Self-heating.** A FET with a thermal network: the parasitic resistors' noise follows the heated temperature, and
   turning `Rth` to zero brings it back to ambient.
4. **ComposedFET round trip.** A `.cmodel` with a Noise block reads, writes and reads identically. The Model Composer fit,
   on synthetic noise parameters made from known `Tg`/`Td`, recovers them to 1e-3 relative.
5. **No keys, no change.** Every shipped example that uses a built-in device, run with `Noise=yes` absent, is
   byte-identical. With `Noise=yes`, a device whose noise parameters are all at their defaults reports the same as
   before, except where the defaults are on by design (Diode and BJT shot noise, and thermal of stated series
   resistances), and the user docs say which.

## Docs

The Components page lists each model's noise sources and parameters in its own section. The Model Composer page gains
the Noise step. The "has no noise model" sentence leaves every model it no longer applies to.

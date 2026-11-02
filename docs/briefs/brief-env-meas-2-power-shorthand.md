# Brief ENV-MEAS-2 — `P("probe")`: a short, sweep-proof spelling for power in a measure line

**Series:** Envelope measurements · **Tag:** `R-envm2-n` · **Briefs:** [1](brief-env-meas-1-probe-power.md), 2 (this), [3](brief-env-meas-3-probes-retired.md)
**Depends on:** ENV-MEAS-1 (the `Pprobe_*` cubes)
**Area:** `src/Core/Expressions/Evaluator.cs` (`EvalQualifiedAccessor`); `src/Engine/HarmonicBalance/HbEngine.cs`
(the dataset builders, §3); `src/Cli/ReferenceTuning.cs` (the `goals` reference topic's function list);
`docs/user/src/reference/measurements.md`, `simulations.md`. Findings go in `src/Core/RESOLVED.md` (evaluator) and
`src/Engine/RESOLVED.md` (HB cubes).

The series rule from brief 1 holds: every shipped example runs exactly as it does today.

---

## 0. The gap

After brief 1, the exact output power of an envelope run is a measure line. But the general spelling is long, and
it says things the user should not have to:

```
measure Pout_W = mean_over(real(0.5*ENV1.V("Vout",1)*conj(ENV1.I("Iout",1))), "time")
```

Four things are wrong with it as the everyday spelling:

- **The probe's node must be named separately**, and it must also be listed in `Probes`, or `V` has no row for it.
- **The ½, the `conj`, the `real` and the averaging** are the same in every power measurement. A slip in any one
  (a missing ½, `I` instead of `conj(I)`) gives a plausible wrong number.
- **It averages over the whole record**, including the settle the built-in scalars skip on a cold start, so it
  disagrees slightly with `Pout_avg_dBm` at a true 50 Ω load. That looks like a bug and is not one.
- **It needs `SaveWaveforms=yes`**, which is the expensive setting.

`Pprobe_fund_W` and `Pprobe_W` (brief 1) already hold the right numbers, averaged in the engine, with every sweep
axis in front. What is missing is a short way to name them.

## 1. `R-envm2-1` — the accessors

Two qualified accessors, in `EvalQualifiedAccessor`, beside `V`/`I`/`S`:

| Spelling | Reads | Result |
|---|---|---|
| `ENV1.P("Iout")` | `Pprobe_fund_W` at branch `Iout` | fundamental-band power through the probe, W |
| `ENV1.P("Iout", k)` | `Pprobe_W` at branch `Iout`, spectral index `k` | power at harmonic `k` (`0` = DC: V₀·I₀), or at a mixing product by its tag `"(1,0)"`, W |
| `ENV1.Pt("Iout")` | `Pprobe_t_W` at branch `Iout` | instantaneous fundamental-band power against `time`, W |

So the everyday measure lines are:

```
measure Pout_dBm = dBm(ENV1.P("Iout"))
measure Pin_dBm  = dBm(ENV1.P("Iin"))
measure Pdc_W    = ENV1.P("IDC", 0)
measure Gain_dB  = Pout_dBm - Pin_dBm
measure DE_pct   = 100 * ENV1.P("Iout") / ENV1.P("IDC", 0)
measure PAE_pct  = 100 * (ENV1.P("Iout") - ENV1.P("Iin")) / ENV1.P("IDC", 0)
```

Two PAs are the same lines with each PA's own probes (`Iout1`, `IDC1`, `Iout2`, `IDC2`). `dBm(…)` already exists and
takes watts.

**Rules:**

- **The branch and spectral axes are found by NAME; every other axis passes through untouched**, whatever their
  number and order. There are **no positional sweep arguments**. That is unlike `V`/`I`, whose third and later
  arguments slice sweep axes by position. Positional arguments are the one thing that changes meaning when a sweep
  is added around an analysis, so they are not offered. One expression therefore gives a scalar with no sweep, a
  `[Pin]` curve under a Pin sweep, and a `[Vdd, Pin]` surface when a Vdd sweep is wrapped around that, with no
  edit. To read one sweep point, use the existing `at(…)`: `at(ENV1.P("Iout"), "Pin", 20)`.
- **Probe names match like `I("…")` does**: case-insensitive, whole name. An unknown name is an error listing the
  analysis's IProbes. An analysis with no IProbes says so and says what `P` reads: "an IProbe in series with the
  load".
- **`k` takes what `V`/`I`'s spectral argument takes** (`ResolveSpectralArg`): an integer, or a quoted product tag on
  a lattice. On a lattice, a bare `P("Iout")` is the sum over the carrier products, the same as `Pprobe_fund_W`.
- **`Pt` needs waveforms.** With `SaveWaveforms=no` it is an error naming that setting. `P` never needs them.
- **Precedence:** the accessor applies when the first argument is a string and the analysis's DataSet carries the
  `Pprobe_*` cube it reads. A DataSet that happens to hold a cube literally named `P` or `Pt` keeps the generic
  positional path. No analysis emits one today, and a test pins that.

## 2. `R-envm2-2` — the long form still works, and the docs say which to use

`ENV1.V(…)` / `ENV1.I(…)` with `mean_over(…, "time")` keep working; brief 1 made `I` exist. The docs present
`P("probe")` as **the** spelling for power. They give the long form once, as what `P` means, with the one
difference stated: `P` averages over the run's measured window and the long form over the whole record.

## 3. `R-envm2-3` — the same spelling on harmonic balance (D1)

A designer moves between HB and envelope on the same schematic. `HB1.P("Iout")` should mean the same thing as
`ENV1.P("Iout")`, so the same measure line can be pointed at either analysis.

HB already solves IProbe currents (`I` cube, `__ProbeBranches`) and every node voltage (`V`). `HbEngine`'s
single-tone and lattice dataset builders also emit, per point:

- `Pprobe_W[branch, harmonic|mixIndex]`: ½·Re(Vₖ·Iₖ*) for k ≥ 1, V₀·I₀ at DC, with V at the probe's first node
- `Pprobe_fund_W[branch]`: harmonic 1, or the sum over each tone's first-order product on a lattice

Each is one point's arithmetic with no time axis, so `ParametricSweepEngine` stacks it as it stacks everything
else. The accessor code in §1 is then analysis-agnostic. HB's existing cubes are unchanged, and the HB examples'
values are bit-identical.

## 4. `R-envm2-4` — docs and the reference topic

- `measurements.md`: a new section **"Power through an IProbe: `P`"** {#power}, holding §1's table and rules, the
  six lines above, the two-PA case, and the sweep rule with `at(…)`. Add `P` and `Pt` to the accessor list under
  `#refs`. In `#refs`' existing `Pout_W` examples, add one line under the long form: "or `HB1.P("Iout")`".
- `simulations.md` `#envelope-power`: the IProbe subsection brief 1 wrote gains the measure-line spelling and links
  `measurements.html#power`.
- `src/Cli/ReferenceTuning.cs`: the `goals` topic's list of functions a goal may use gains `P` and `Pt`, so an agent
  finds them through `reference`.
- Regenerate the two edited pages alone with `DocGen --page`, no figures.

## 5. Gates (one test per claim)

1. **`P` is the cube:** on a single envelope run, `ENV1.P("Iout")` equals `Pprobe_fund_W` at `Iout`, and
   `ENV1.P("IDC", 0)` equals `Pprobe_W[IDC, 0]`, exactly.
2. **One expression, three shapes:** the same measure text evaluated on no sweep, one sweep and two nested sweeps
   gives a scalar, `[Pin]` and `[Vdd, Pin]`, each equal to the corresponding cube slice. This is the
   "adding a sweep never breaks it" gate.
2b. **A trace survives the re-run.** This is a SEQUENCE test, not a single fixture. A Data Display trace of
   `Pout_dBm` (a `P` measure) is made on a one-sweep run (`[Pin]`). The design is re-run with an outer `Vdd` sweep
   added. The trace stays valid and plots a family over the new axis through the existing trace-carry path
   (`SeedSlice`), with the user's axis roles kept. Do the same for a trace of `ENV1 › Pprobe_fund_W › Iout` read
   straight from the cube.
3. **`at` picks a point:** `at(ENV1.P("Iout"), "Pin", p)` equals the 1-D result at `p`.
4. **The PAE line:** the six-line block above evaluates on a short PA envelope run, and PAE equals the closed form
   from the three `Pprobe` values.
5. **Errors:** an unknown probe lists the IProbes; no IProbes says what to place; `Pt` with `SaveWaveforms=no` names
   the setting.
6. **Precedence:** a DataSet holding a cube named `P` still resolves `X.P(0)` positionally.
7. **HB parity** (if D1 is taken): on one CW PA bench, `HB1.P("Iout")` and `ENV1.P("Iout")` agree within 0.01 dB;
   on a two-tone HB, `Pprobe_fund_W` is the sum of the two carriers' `Pprobe_W`.

Run only the evaluator classes in `Core.Tests`, the envelope and HB IProbe classes in `Engine.Tests`, once, and read
the TRX. Envelope runs stay at `Stop` ≤ 20 µs.

## 6. Out of scope

- **`P` on DC and loadpull.** DC has `I:<probe>` cubes and could take `DC1.P("IDC")` = V·I by the same rule. Loadpull
  has its own FOM cubes. Neither is needed for envelope or HB parity; a follow-up if asked.
- **Total power over every harmonic** (`P("Iout", All)`). The fundamental band and single harmonics cover PA work;
  a sum is easy to add once someone needs it, and choosing whether it includes DC is a decision for that day.
- **Voltage-only power at a node** (`P("Vout")` into 50 Ω). That is what the built-in scalars already are, and a
  second spelling of the 50 Ω assumption would hide which one a measure line made.

## 7. Decisions for the owner

- **D1** `P` on harmonic balance too (§3): yes (recommended: one measure line serves both analyses), or envelope
  only.
- **D2** the accessor names: `P` / `Pt` (recommended: shortest, and they read naturally in `dBm(ENV1.P("Iout"))`),
  or `Pwr` / `Pwr_t`.
- **D3** a bare `P("Iout")` on a lattice: the sum over the carrier products (recommended: the same band
  `Pout_avg_dBm` reports), or refuse and require a product tag.
- **D4** a **"PA measurements"** button on the envelope (and HB) card that asks for the output, input and supply
  IProbes and writes the six lines of §1 into the testbench, where they stay ordinary, editable measure lines: build
  it here, as a follow-up (recommended, once the spelling has been used), or not at all.

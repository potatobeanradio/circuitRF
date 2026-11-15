# Brief series PAA — phased antenna arrays

**Goal.** A schematic component, the **Phased Antenna Array** (`.cnl` `PhasedArray`), gives the far field of a whole
array of up to 4096 elements (64×64) from one EM simulation of one element or of a small sub-array. Its lattice,
pitch, weights, phase-shifter settings and tapers are ordinary expressions, so they are swept, tuned and optimized like
any parameter; it works with the System blocks around it, on transmit and on receive, with noise; it is fast enough to
steer by hand; and a small array can instead be simulated by brute force, one PA per element in harmonic balance, to see
each PA loaded by its neighbours. A tool window shows an array too large to look at in a schematic.

**Design note:** [`docs/design/phased-array.md`](../design/phased-array.md). Read it first; this series builds it, and
its section numbers are cited below as "note §n".

## Why a series

An antenna can be simulated, its pattern plotted, and its pattern recombined with the circuit around it (the co-simulation
series). Four things stop that from being a phased array:

1. **Only an array that was solved whole has a pattern.** The co-simulation sums the patterns stored per port, so a
   1024-element array would need a 1024-port EM run. Nothing forms an array's pattern from an element's (note §2.2).
2. **Nothing extends coupling.** A large array's active reflection and scan loss come from coupling between elements,
   and nothing derives a large array's coupling from a small one's (note §3.2).
3. **There is no component.** Steering a small array today means naming each drive and weight on the analysis line; there
   is no lattice, no pitch, no per-element expression, no symbol, and no place for a per-element channel.
4. **Harmonic balance has no pattern.** HB already solves PAs on the ports of an EM block, but the pattern is formed
   only from a linear solve, so a compressed or load-pulled PA's effect on the beam is invisible.

## What already exists and is reused

| Need | Existing piece |
|---|---|
| Per-port far fields of a result, read whole and cached | `PortFarFieldSet` (`src/Design/Em3d/PortFarFieldSet.cs`) |
| Recombination with circuit waves, terminated and planar (shorted) cases | `FieldCosimulation.PointOf`, `PortFarFieldSet.Weights` / `AddWeighted` |
| Pattern metrics, gain per watt accepted, realized gain | `Em3dRadiation.Publish`, `CosimPatternMetrics` |
| The circuit's waves at a block | `SParameterEngine.BlockWaves` |
| A saved result as a schematic block | `EmFieldData` (`EmFieldDataRecord`, an `SnpModel`) |
| Indexed per-path parameters | `Combiner`'s `Phase[k]`, `IL[k]` (`ComponentModelFactory.CreateCombinerModel`) |
| A variadic cached symbol | `BuiltInSymbols.BuildCombiner(n)`, `_combinerCache` |
| Correlated noise through an N-port | `SParameterNoise` (`docs/design/sparameter-noise.md`) |
| Tuning sliders, parametric sweep, optimizer, yield over a `fields` analysis | CP-4, CP-5 |
| Array benches at system level | RF Chain Budget (`docs/design/chain-budget.md`) |

## The briefs, in order

| Brief | What | Depends on |
|---|---|---|
| `brief-paa-1-em-measurement.md` | **The EM evidence first**: element, pairs, 1×4, 2×2, 5×5 and the largest affordable square array on Planar; L0 and L1 against L2; sets every later tolerance | — |
| `brief-paa-2-array-engine.md` | The numeric engine (lattice, pattern sum, coupling extension, Γ_act, metrics) and the record's port positions | 1 |
| `brief-paa-3-component-elements-mode.md` | The `PhasedArray` component, its parameters and units, Elements mode, the `fields` analysis on it, `check`/`explain` | 2 |
| `brief-paa-4-feed-mode-and-weights.md` | Feed mode: element scope, `steer`/`taylor`/…, quantisation, `Off`, the channel cell, the PhaseShifter block | 3 |
| `brief-paa-5-dynamic-symbol.md` | The symbol drawn from the instance's parameters, both modes | 3, 4 |
| `brief-paa-6-brute-force-hb.md` | The pattern from HB's waves, each PA's loading, the loadpull overlay; a 1×4 bench of real PAs | 3 |
| `brief-paa-6b-envelope.md` | The same with a modulated signal: the radiated envelope, ACLR and EVM in named directions, each PA's active load over the record, a time-averaged pattern | 6 |
| `brief-paa-7-receive-and-noise.md` | Incident waves by reciprocity, the array's noise from its pattern, G/T, interferers | 4 |
| `brief-paa-8-speed-and-live.md` | Measured cost, the identical-channel fast path, sliders that keep up | 4 |
| `brief-paa-9-tool-window.md` | Tools ▸ Phased Array: aperture, (u,v) map with drag-to-steer, cuts, readouts, scan maps | 5, 8 |
| `brief-paa-10-docs-and-example.md` | User pages, the format reference, and a shipped example | all |

PAA-1 to PAA-4 are the core: a steerable, sweepable array from one EM run. PAA-6 is the brute-force path for small
arrays and can be built any time after PAA-3.

## Decisions already made

- **D1 — One component, two pin modes.** Feed mode has one pin and an internal ideal beamformer; Elements mode has one
  pin per element and no beamformer. Elements mode is limited to 64 elements: brute force is a small-array tool, and a
  symbol with more pins is not something anyone wires.
- **D2 — No weight between a PA and its element.** In Elements mode the weight fields are unused, because a phase there
  would rotate the PA's load, which no real phase shifter does. Steering in Elements mode is the circuit's.
- **D3 — Three fidelity levels, named, defaulted from the record** (note §3): `isolated` for a one-port record,
  `embedded` for a sub-array, `exact` for a record that is the array. The level used is said in the run's notes and in
  `explain`. Nothing silently mixes them.
- **D4 — Every parameter is an expression.** Pitch is a length in any unit circuitRF knows; weights, phases, delays, bits
  and the steering VARs are expressions; a parametric sweep, a slider, yield and the optimizer reach every one. The two
  exceptions are structural and say so: `Rows`/`Cols` cannot be swept in Elements mode (the pin count would change mid-run),
  and `Record`, `Mode` and `Lattice` are words.
- **D5 — A pitch different from the record's warns, never refuses** (note §3.3). The array factor is exact at any pitch;
  only the coupling and the embedded patterns are tied to the solved one.
- **D6 — Element scope adds five names** (`n`, `row`, `col`, `x`, `y`) to the weight fields and to a channel cell's
  parameters, and nowhere else. The orientation is fixed: seen from boresight, `n` runs row-major from the top left.
- **D7 — The tool is a view of a schematic instance, not a document.** Its edits are the instance's parameters with undo;
  a "new phased array" is a small schematic created for it. No `.carray` format.
- **D8 — The pattern block is the component.** `type=fields block=PA1` names the PhasedArray, as it names an EmFieldData
  block today; no new analysis type.
- **D9 — 4096 elements, and built for it.** Feed mode goes to 4096. Three things are designed for that size rather
  than for 1024 (note §3.2, §5.2, §6.2): the extended coupling and the noise correlation are **sparse**, never dense
  N×N; the noise correlation is computed **by offset**, never pair by pair; and cuts and metrics are formed on a **fine
  local grid** with the element pattern interpolated, never read off the record's 1° grid, which cannot resolve a
  1.6° beam. Every large-N gate runs at 32×32 and 64×64.

## Risks — measured in PAA-1 before anything is built on them

1. **Planar ports in a packed array.** A calibrated edge port grows its feed outward and is refused when a neighbour
   carrying a port is too close (the ICP series met this at device pitch). At half-wavelength pitch on a 30 mil laminate
   the spacing is much larger, but the element is fed with an internal gap port in PAA-1 so the question does not arise;
   if the gap port's pattern is unusable, stop and report.
2. **The size of the reference array.** A 7×7 patch array on Planar may not fit the time and memory budget. PAA-1 runs
   `dryRun` on each before solving, and falls back to the largest square array that fits, or to a 1×8 line plus the 5×5.
3. **Infinite ground.** A planar ground has no edge, so edge diffraction is absent from every planar result. PAA-1
   measures its size by hand on one finite-ground 3D run and the docs say it; nothing gates on it.
4. **Truncated coupling.** L1 drops couplings beyond the sub-array's offsets. Measured in PAA-1 as the power balance
   (note §2.4) and as the error against the reference array.

If PAA-1 finds L1 no better than L0 against the reference array, stop and report: the component would still be built,
but D3's default and the docs' claims change.

## Simulation budget, for every brief

- **Every EM run is listed in a brief**, with `run analysis=em dryRun=true` first and its unknowns, memory and time
  recorded. No run is started that `dryRun` puts beyond ~15 minutes or beyond half the machine's memory without asking.
- Every ad-hoc run has a memory watchdog on the solver's own process id, not on a wrapper.
- **No Palace anywhere.** Planar for the fixtures; EMerge Draft by hand for the one finite-ground check, time-capped.
- Tests read small committed fixtures (one frequency, a coarse angular grid) or synthetic records; a routine test that
  solves stays under ~5 s. No new `Category=Benchmark` test and no timing assertion: performance is recorded in
  `RESOLVED.md`, and gated by counters.
- One test per claim. Run the named test classes only, never all of `Ui.Tests`.
- Findings go to the nearest `RESOLVED.md` (`src/Design/RESOLVED.md` for EM and records, `src/Core/RESOLVED.md` for the
  model and expressions, `src/Engine/RESOLVED.md` for the engine) and to each brief's completion note.

## Not here

Note §11: conformal and arbitrary-position arrays, sub-array time delays, digital beamforming with ADC models, re-solving
the sub-array at each swept pitch (an EM-cell follow-up), a Chain Budget transfer that emits a PhasedArray, and random
per-element errors drawn by the yield engine.

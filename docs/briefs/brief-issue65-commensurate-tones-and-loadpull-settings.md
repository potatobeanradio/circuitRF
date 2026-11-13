# Issue 65 §1 and §3 — commensurate tones on the multi-tone HB, and the settings a loadpull runs under

Issue 65's other parts are fixed: a block whose terminals come in pairs keys each port's current by the pair's first
name (`A1:out+`, no longer `A1:in-`), and a swept loadpull or pursuit now receives the sweep's cancellation. Two parts
are left.

## 1. Two tones that are commensurate within the mixing order

### Why

When the tones share a frequency grid within `MaxMixOrder` (100/300 MHz, 100/200 MHz), distinct mixing indices land
on one frequency: `(3,0)` and `(0,1)` are both 300 MHz, `(2,−1)` and `(1,0)` both 100 MHz. Each tone source is
extracted at every index on its frequency (`ExtractMix(lattice.OmegaOf(m, …))`, on the lattice and on the grid path),
so the device sees each source tone on several indices: four "tones" instead of two. The two transforms then treat the
content the lattice cannot hold differently. The APFT projects it by least squares onto every retained bin, so an odd
nonlinearity shows even-order products. The FFT aliases it. The two paths, which agree to about 1e-4 for
incommensurate tones, then disagree by tens of percent on IM3 and by 1e3 and more on higher bins.

Reproduction: issue 65 §1 (an in-phase coupler with an odd `tanh` PIM; `(1,3)` at 100/300 MHz reads 1.00e-3 V on the
lattice and 8.3e-18 V on the FFT path, where an odd nonlinearity puts nothing).

### Requirements

**R-1 — Detect it.** After the lattice is built, find any two retained indices `m ≠ m'` with
`|ω(m) − ω(m')| < ε·max ω`, with ε the tolerance `CheckCommensurabilityLattice` already uses. A tone set with such a
pair is commensurate within the mixing order.

**R-2 — Owner decision D1, what a commensurate run does:**
- (a) run it as single-tone HB on the common fundamental (the tones' greatest common divisor within ε, 100 MHz here)
  with `MaxHarm` raised to reach the highest retained product, and re-index the result onto the mixing indices the
  analysis asked for, so `(2,−1)` reads the 100 MHz bin. Exact, but every product sharing a frequency reads the same
  number, and that must be said in the run's note; or
- (b) refuse with a sentence naming the coinciding pair and the two tones, and suggest a single-tone HB at the common
  fundamental. Cheaper and honest; a design with an LO at twice the RF stops running.

(a) is the suggestion in the issue. The decision turns on how often a real bench hits this; a mixer with LO = 2·RF is
the common case.

**R-3 — Gates.** The reproduction's three tone pairs:
- 100/310 MHz (the control) bit for bit unchanged;
- 100/300 and 100/200 MHz: under (a), `(1,3)` at 100/300 below 1e-12 V, and every bin within 1e-6 relative of a
  single-tone HB at 100 MHz with `MaxHarm` = the highest retained harmonic; under (b), the refusal names `(3,0)` and
  `(0,1)` (or the pair it finds first) and the two tones.

## 2. The settings a loadpull runs under (§3 of the issue)

### Why

One design gives different loadpull numbers depending on how it is run:

| run | settings |
|---|---|
| `circuitrf lp`/`lpp`, unswept | `HbCircuitRun.Settings`: `ConductanceRegularization = Always`, `--max-iter`, `--diag` |
| `circuitrf lp`/`lpp`, inside a sweep | the parallelism cap only (`ParametricSweepEngine.CapOnly`): default solver settings |
| Simulate in the GUI, swept or not | the parallelism cap only (`CircuitEvaluation`'s `loadpullSettings`): default solver settings |

`CapOnly` is deliberate (LP-1 kept every swept result as it was), and its comment names this as a known gap.

### Decision first (**D2**)

Which settings are a loadpull's, everywhere: the CLI's (`Always`, so the GUI and swept answers move) or the defaults
(`IfNecessary`, so the unswept CLI answer moves)? Measure first: run the shipped loadpull examples and `testdata/Hero3`
both ways and record the largest change in Pout, PAE and the contour optima in `src/Engine/Loadpull/RESOLVED.md`.

### Requirements

**R-4.** One `AnalysisSettings` reaches every loadpull: `ParametricSweepEngine` passes the sweep's settings through
(not `CapOnly`), `CircuitEvaluation` passes the D2 settings for a bare, swept or pursued loadpull, and the CLI's
`--max-iter` reaches a swept loadpull.

**R-5 — Gate.** One loadpull bench, run unswept from the CLI, swept from the CLI (one point) and through
`CircuitEvaluation`, gives the same numbers bit for bit.

# circuitRF — Transient analysis, the ngspice solver, transient-assisted harmonic balance

**Status:** TRN-1 built (§3, §4, §5.1, §5.2: `src/Engine/Transient`, the `tran` verb and card); TRN-4 built (§5.3 lossless lines and CLIN, §5.4 wideband fits with passivity enforcement, EM cells, tone-source terminations); TRN-4b built (the line models' losses carry their phase, and lossy lines run in transient, §5.3); the rest is design · **Series:** [`brief-trn-0-overview.md`](../briefs/brief-trn-0-overview.md) ·
**Builds on:** the envelope engine (`src/Engine/Envelope`: its stepping, `EnvelopeSnpFit`, `EnvelopeLineDelay`), the
nonlinear DC engine ([`nonlinear-dc.md`](nonlinear-dc.md)), harmonic balance ([`harmonic-balance.md`](harmonic-balance.md)),
the SPICE-dialect reader ([`spice-models.md`](spice-models.md)), the solver discovery and install machinery
(`src/Design/Em3d/SolverDiscovery.cs`, `src/Design/Em3d/Install`), measurements ([`measurements.md`](measurements.md)).

circuitRF is a harmonic-balance simulator first, and that does not change. This note adds three things beside it:

1. **A native transient analysis**, `type=tran`, solving the elaborated netlist in the time domain from a DC operating
   point, on the same component models every other analysis uses. It is the default solver for a transient analysis.
2. **ngspice as an optional transient solver**, chosen on the analysis card. circuitRF writes the bench's netlist in the
   SPICE dialect, runs the user's ngspice on it as a separate process, and reads its waveforms back into an ordinary
   `DataSet`, so a design drawn once in circuitRF can be simulated by ngspice without being drawn again. Where an element
   has no exact counterpart, the translation refuses it by name; it never substitutes an approximation silently.
3. **Transient-assisted harmonic balance**: a short native transient run, from the DC point, whose last period becomes
   harmonic balance's starting point. It is meant for circuits harmonic balance struggles to start from DC: switching
   PAs, hard-driven mixers, frequency dividers and oscillators.

The native engine is designed and written here from first principles. ngspice's source code is not read for it; ngspice
is a solver circuitRF calls and a reference circuitRF is measured against, nothing more.

---

## 0. Terms

| Term | Meaning here |
|---|---|
| **Transient analysis** | A solution x(t) of the circuit equations over a time interval, from an initial state, with every source driven by its waveform. `.cnl` `analysis … type=tran`. |
| **Native** | circuitRF's own transient engine (§4). |
| **ngspice** | The open-source SPICE-dialect simulator, installed by the user or by circuitRF's recipe (§9.1), run as a separate process. |
| **Accepted step** | A time step whose local error estimate passed; the engine's own, non-uniform time grid. |
| **Output grid** | The uniform time axis every transient result is reported on (`Step`), interpolated from accepted steps (§4.6). |
| **Breakpoint** | A time at which a source's waveform has a corner (a pulse edge, a PWL vertex, a delay ending). A step always lands on it. |
| **Interface** | The ports of nonlinear devices, as in harmonic balance: the unknowns the nonlinear Newton works on when the linear rest is condensed away (§4.4). |
| **Seed** | The starting point a transient run hands to harmonic balance or the oscillator analysis (§10). |
| **Translation** | The `.cir` file circuitRF writes for ngspice from an elaborated bench (§9.2). |

## 1. What circuitRF already has

| Piece | Where | What transient gets from it |
|---|---|---|
| Charge-based device evaluation: `i(v)`, `q(v)`, `dg`, `dc` | `ComponentModel.Evaluate` | Every nonlinear device works in the time domain as it stands; this is the form transient integration needs |
| A time-stepping circuit solver | `EnvelopeEngine` (BDF2 with a backward-Euler start, the device Jacobian from `HbNewton`) | The structure of a stepped Newton on the interface; its harmonic-0 case is a fixed-step transient |
| Recovery of `G` and `C` from two frequency-domain stamps | `EnvelopeEngine` (exact for any stamp affine in jω) | Every lumped linear model in the time domain with no per-model work |
| Rational realization of frequency-defined elements as extra linear unknowns, `G + jωC` at every ω | `EnvelopeSnpFit` (SnP blocks, System Filters, Duplexers, Z(freq) blocks, tapers; passivity checked) | S-parameter files, filters and EM-cell results in the time domain |
| Lines as delays, method of characteristics, sub-step interpolation that is passive | `EnvelopeLineDelay` | Transmission lines, exactly when lossless |
| A robust DC operating point | `NonlinearDcEngine`, `BiasFreeze` | The transient's starting state |
| A model's slowest time constant | `ComponentModel.SlowestTimeConstant` (envelope warm-up) | Settling estimates for `explain` and for seeding (§10) |
| HB warm starts | `HbEngine`'s `warmStart` (`Complex[N, K]` on the interface) | The seed's destination (§10) |
| The oscillator analysis and its probe | `OscEngine`, `OscProbeModel` (`Fguess`, `Aguess`) | The oscillator seed's destination (§10.3) |
| A SPICE-dialect reader, element mapping and the charge idiom | `src/Core/Netlist/Spice`, `spice-models.md` §8–§9 | The translation's element table, run backwards, and its round-trip gate (§9.2) |
| A placed SPICE model that points at its file | `SpiceModel` component | An exact translation by construction: the file is written into the deck as it is |
| Verilog-A compiled to `.osdi`, per platform | `VerilogACompiler`, `OsdiBuilds` | ngspice loads the same compiled file (§9.2) |
| Solver discovery, consent, recipes, Settings rows | `SolverDiscovery`, `SolverRecipes`, `SolverInstaller`, `SolverInstallRunner`, Settings ▸ Solvers | ngspice is one more `SolverTool` (§9.1) |
| Time-axis cubes, plots and measurements | envelope results, Data Display, `MeasurementEvaluator` | Transient waveforms plot and measure through the same path |

What is missing: an engine with an adaptive step, breakpoints and robust per-step Newton; time-domain waveforms on
sources; a switch; initial conditions; a causal wideband treatment of lossy lines; a translation to the SPICE dialect;
a rawfile reader; and a way to hand a transient's last period to harmonic balance.

## 2. Decisions

- **D1 — One analysis, `type=tran`** (alias `transient`), attached to a TestBench like every other. It runs on circuit
  models; `model=em` on a tran analysis runs EM cells through their fitted results (§5.4).
- **D2 — `Solver=native|ngspice`, native by default.** The solver is a key on the analysis directive, so a bench says
  which solver produced its result and the CLI runs what the GUI runs. **On the analysis card the ngspice choice is
  disabled (greyed) whenever discovery finds no usable ngspice** — not installed, its path gone, or a version that does
  not answer `ngspice -v` — with a tooltip naming Settings ▸ Solvers, where Install… is. It becomes selectable as soon
  as an install finishes or a path is set, with no restart. A bench that already says `Solver=ngspice` (written on
  another machine, or by hand) keeps its value: the card shows it, disabled, with the same note beneath, and a run —
  from the GUI, the CLI or MCP — is refused with that sentence and `circuitrf solver install ngspice`. It never falls
  back to native.
- **D3 — Transient quantities are real.** Voltages, currents and charges in the time domain are `double`; result cubes are
  `DataKind.Real`. The complex-signal invariant covers AC and HB quantities and is untouched.
- **D4 — No new analysis for the seed.** Transient-assisted HB is a key on the existing `hb` and `osc` directives
  (`InitialGuess=dc|transient|fallback`, §10), default `dc`, so every existing result is unchanged.
- **D5 — One source type per waveform, named after it**: `V_Pulse`, `V_PWL`, `V_Exp`, `V_SFFM`, `V_Expr` and their
  `I_` twins, plus `Idc`, the DC current source circuitRF has lacked. Each type carries only its own keys and maps one
  to one onto the dialect's stimulus, and all share one model class underneath, as the tone sources share
  `ToneSourceModelBase`. There is no separate sine type: the tone sources are sines, and gain the transient-only keys
  `Delay`, `Ramp` and `Damping` (§6.2), none of which changes another analysis.
- **D6 — In DC, S-parameter, HB and envelope, a waveform source is its `Dc` value**, default the waveform's value at
  t = 0, as the dialect does. Nothing time-varying leaks into a steady-state analysis.
- **D7 — The translation is exact or it refuses.** An element with no exact counterpart in the dialect is refused by
  name, with the reason, and the run does not start. A parameter of a translated model that the dialect cannot express
  is refused by parameter. There is no "close enough" mapping. Optional approximate translations (a rational fit written
  as a subcircuit, §9.2.3) are asked for by a key and labelled as such in the result.
- **D8 — ngspice is a separate process, never a linked library.** It is run in batch mode on a scratch deck, its
  waveforms read from its binary rawfile. A crash in ngspice is a failed run, not a failed circuitRF, and no native
  dependency is added.
- **D9 — ngspice is named, as the integrated EM solvers are.** It appears in Settings, on the analysis card, in the CLI
  and in the docs. No other simulator is named anywhere; the dialect is "the SPICE dialect", as `spice-models.md` §0
  already has it.
- **D10 — Install from upstream binaries only; no source build is shipped** (§9.1). A platform with no binary gets a
  `ByHand` message, as Gmsh on Linux arm64 does today.
- **D11 — Every result is on a uniform output grid** (`Step`), whichever solver produced it, so plots, FFTs, measurements
  and the native-versus-ngspice comparison all read one layout. The engine's own accepted steps are available on request
  (`SaveSteps=yes`).
- **D12 — Every parameter is an expression**; structural words (`Method`, `Solver`, `InitialState`,
  `InitialGuess`) are words.

## 3. The analysis directive

```
analysis TR1 type=tran Stop=2u Step=1n
analysis TR2 type=tran Stop=50n Step=10p MaxStep=20p Method=gear2 InitialState=op Solver=ngspice
```

| Key | Default | Meaning |
|---|---|---|
| `Stop` | required | End time (s). |
| `Step` | `Stop/1000` | Output grid spacing (s). Also the largest step unless `MaxStep` says otherwise. |
| `Start` | `0` | First reported time; the run still starts at 0. |
| `MaxStep` | `auto` | Largest accepted step: the smaller of `Step` and 1/(20·f) for the highest frequency any source carries. |
| `Method` | `trap` | `trap` (trapezoidal, second order, no numerical damping) or `gear2` (second-order backward difference, damps ringing). Either takes backward-Euler steps after each breakpoint (§4.2). |
| `RelTol` | `1e-3` | Relative tolerance on Newton updates and on the local error estimate. |
| `VTol` | `1e-6` | Absolute voltage tolerance (V). |
| `ITol` | `1e-12` | Absolute current tolerance (A). |
| `InitialState` | `op` | `op`: the DC operating point, with every `IC=` held during the solve and released at t = 0⁺. `zero`: every unknown at 0 except the `IC=`s, with no DC solve. |
| `IC` | | Net voltages held during the operating-point solve: `IC="out=0, bias=1.2"`. Parts carry their own `IC=` (§7.2). |
| `Solver` | `native` | `native` or `ngspice` (§9). |
| `SaveSteps` | `no` | `yes` adds the accepted-step cubes beside the output grid. |
| `MaxSteps` | `10000000` | A refusal, before the run, when `Stop/MaxStep` exceeds it; `explain` gives the estimate (§11). |
| `SpiceOptions` | | ngspice only: written verbatim as one `.options` line. Refused with `Solver=native`. |
| `SpiceLines` | `refuse` | ngspice only: `rlgc` writes lines as ngspice's lossy lines fitted at `SpiceLineFreq`, each with its error reported (§9.2.3). |
| `SpiceLineFreq` | `auto` | ngspice only: the frequency `rlgc` lines match circuitRF's model at; `auto` is the highest source frequency. |
| `SpiceFit` | `no` | ngspice only: `yes` writes frequency-defined elements as subcircuits of circuitRF's rational fit, labelled (§9.2.3). |

Recorded, as in the envelope analysis: every labelled net's voltage and every `IProbe`'s current, with no key needed.

Result cubes: `V[net, time]` (V), `I[probe, time]` (A), `P[probe, time]` (instantaneous v·i, W), `OP.V[net]` (the
starting state), and counters `Tran.AcceptedSteps`, `Tran.RejectedSteps`, `Tran.NewtonIterations`,
`Tran.Factorizations` (the gates' evidence, never timings). With `Solver=ngspice` the same cubes, plus
`Ngspice.Version` and the deck's path in the provenance.

## 4. The native engine

### 4.1 The equations

After elaboration the circuit is

```
G x(t) + C dx/dt + i(x(t)) + dq(x(t))/dt + y_ext(t) = b(t)
```

- `G`, `C` — the linear part, recovered from each lumped model's frequency-domain stamp at two frequencies
  (exact for any stamp affine in jω, as the envelope already does); a rational realization's extra unknowns
  are in the same `G` and `C` (§5.4).
- `i`, `q` — the nonlinear devices' currents and charges at their ports, from `Evaluate`.
- `y_ext` — the delayed-wave terms of lines (§5.3), known at each step from earlier steps.
- `b(t)` — the sources' waveforms (§6).

Everything is real (D3).

### 4.2 Integration

A step from t_n to t_{n+1} = t_n + h replaces each time derivative by a linear combination of the unknown at t_{n+1} and
known values at earlier steps. Two second-order methods are offered:

- **`trap`** (default): the derivative at t_{n+1} is 2/h times the change minus the derivative at t_n. It preserves an
  undamped oscillation's amplitude, which is right for RF waveforms, and it can carry a non-physical alternating error
  after a discontinuity.
- **`gear2`**: the derivative is a fixed combination of the new value and the two previous ones, using the actual
  (unequal) step sizes. It damps high-frequency error, at the cost of some damping of real oscillations at coarse steps.

After every breakpoint and at the first step, two backward-Euler steps are taken whichever method is chosen: a corner
in a source would otherwise excite trapezoidal's alternating error, and the first step has no history for `gear2`.
Each method's stepped companion is `G + α C` with α fixed by the method and h, plus history terms on the right-hand side.

### 4.3 Step control

- **Error estimate.** Each accepted step keeps the charges and fluxes of every reactive element. A predictor
  extrapolates them to t_{n+1} from previous steps; after Newton converges the corrector's value is compared with the
  prediction. The difference, scaled by the method's error constant, estimates the local error. A step is accepted when
  the estimate is within `RelTol·|q| + abs` on every reactive element (abs from `VTol`/`ITol` through the element's
  own scale), and the next h is chosen from the estimate's ratio to the tolerance, with growth capped at 2× per step.
- **A ladder of step sizes.** h is always `MaxStep / 2^k`, k ≥ 0, except the single step that lands on a breakpoint.
  With a fixed set of h values, the factorization of `G + α C` for each rung is computed once and kept, so a run's
  linear cost is one factorization per rung visited plus one back-substitution per Newton iteration. This is the
  envelope engine's "factor once per run" property generalized to an adaptive step.
- **Breakpoints.** Every source reports the corners of its waveform in the run interval. A step that would pass a
  breakpoint is shortened to land on it exactly; that one step's factorization is computed and discarded.
- **The first step after a breakpoint** has no history to predict from, so its error is estimated by step doubling: the
  same interval in two backward-Euler half steps, the full step's error twice the charge difference. It is rejected down
  the ladder like any other step (TRN-4b; before that it was taken unchecked at MaxStep/64, and its damping was outside the
  tolerance whatever RelTol said).
- **Rejection.** A failed Newton or an over-tolerance estimate halves h (one rung down) and retries. Below
  `MaxStep / 2^40` the run stops with the time, the last converged state, and the device whose Newton update was largest.

### 4.4 Newton per step

Two paths, chosen per run from the interface size:

- **Interface path** (few nonlinear ports, the common RF case: a few transistors in a large linear network). The linear
  part is condensed onto the interface through the rung's factorization (a Schur complement), so each Newton iteration
  solves a small dense system on the interface and one sparse back-substitution recovers the rest, as the envelope does.
- **Full path** (many nonlinear ports: logic-like or power circuits). The whole system `G + αC + ∂i/∂x + α∂q/∂x` is
  assembled sparse and factored each iteration (real CSparse LU, symbolic analysis reused).

The threshold between them is measured in TRN-1 and recorded in `src/Engine/RESOLVED.md`. Convergence needs both the
update (within `RelTol·|x| + VTol`/`ITol`) and the residual to pass. Junction-voltage limiting and step damping are the
DC engine's own (`NonlinearDcEngine`), reused rather than written again. Devices that prefer batch evaluation evaluate
one point at a time here; an external (worker) device costs one round trip per evaluation, which TRN-1 measures and
records.

### 4.5 The starting state

`InitialState=op` solves the DC operating point with `NonlinearDcEngine`, waveform sources at their t = 0 value, each
`IC=` net held by a stiff source and each part's `IC=` applied (§7.2), then releases the holds at t = 0⁺.
`InitialState=zero` starts every unknown at 0 except the `IC=`s and solves nothing first. The starting state is reported
(`OP.V`).

### 4.6 The output grid

Accepted steps are non-uniform. The result is reported on `Start, Start+Step, …, Stop` by the integration's own
interpolant over the accepted step containing each output time, which is second order and introduces no extra error
beyond the step's own. Breakpoints are honoured by the steps, not by the grid. `SaveSteps=yes` adds `Steps.time` and
`Steps.V[net, step]` as recorded.

### 4.7 What the envelope engine keeps

Nothing in `EnvelopeEngine` changes. Transient lives in `src/Engine/Transient`. What is shared is moved down, not
copied: the two-stamp `G`/`C` recovery, the rational realization (§5.4) and the line delay (§5.3) become shared pieces
both engines call, each with its existing envelope gates still green.

## 5. Elements in the time domain

### 5.1 Lumped linear models — exact

R, L, C, mutual inductance, the ideal transformer, VCCS, VCVS, ports and terminations, series and parallel RLC, and
every other model whose stamp is affine in jω, through the two-stamp recovery. TRN-1 lists every model the recovery
classifies as affine, and the gate holds that list.

### 5.2 Nonlinear devices — as they are

Every device that implements `Evaluate` works unchanged: diode, BJT, the FET laws, MOS levels, SDD, Verilog-A,
nonlinear capacitors, composed FETs. A device whose charge is linearised at the bias point (`IBiasFrozen`) is frozen at
the starting state, which is what that setting means.

### 5.3 Transmission lines

- **Lossless uniform lines**: exact, as delays, by the method of characteristics: each end is its Zc in series with the
  wave that left the other end τ earlier, interpolated on accepted steps with the envelope's passive quadratic
  interpolation. A breakpoint is added at τ after each source breakpoint, so a pulse edge that travels the line lands
  on a step.
- **Lossy and dispersive lines**: the line is its delay times a residual H̃(f) = e^(−γl)·e^(j2πfτ). The envelope does not
  fit H̃ globally because the line models attenuate with no matching phase, so H̃ is not causal, and a non-causal
  response cannot be stepped in time. Transient covers the band from DC up, so it must. The residual is made causal by
  keeping its magnitude and giving it the phase a causal minimum-phase response with that magnitude has; that response is
  smooth, so a low-order rational fit realizes it as extra linear unknowns at the line's far end. The delay τ is then
  the line's high-frequency delay, so nothing is double-counted. This changes the line's phase by the causality
  correction only; TRN-4 reports its size against the frequency-domain model at the band edge and warns, naming the
  line, when that exceeds a stated tolerance.

  **As built (TRN-4, TRN-4b).** TRN-4 measured this method on the shipped microstrip, stripline and coplanar models and
  found the causality correction alone moving S21 by up to 0.22 at 20 GHz: the models attenuated with no matching phase.
  TRN-4b gave the models that phase (`CausalLoss`: the conductor's internal reactance, equal to its loss above its DC
  floor; the dielectric's permittivity falling with frequency as a flat loss tangent implies, pinned where Er and TanD are
  stated — 1 GHz for MLIN/SLIN/CPWG, `F` for TLIN), which moves every lossy line's S-parameters in every analysis
  (`src/Engine/RESOLVED.md`, brief-trn-4b, has the table). The transient then fits the residual directly rather than
  through its minimum phase, since dispersion is a phase with no magnitude, with τ the smallest phase delay in band less
  1.5 periods of `f_max` (a residual at the smallest delay is barely causal and fits badly). Every shipped line at three
  geometries is realized within |ΔS21| 1e-3 up to 20 GHz; a line that is not, at its run's `f_max`, is refused by name
  with its error. |ΔS11| is limited by the characteristic impedance, which the dispersive models vary with frequency and
  the realization holds at mid-range; above 1e-2 the run warns, naming the line.
- **Coupled lines**: lossless coupled lines by modal decomposition into independent delays with the frequency-independent
  modal transformation; lossy or dispersive coupled lines are refused by name in v1 (their models' losses do not yet carry
  their phase either).

### 5.4 Frequency-defined elements — wideband rational fits

SnP blocks, System Filters, Duplexers, Z(freq) blocks, tapers, and EM cells run with `model=em` are realized as the
envelope's pole-residue fit, but over the whole band the transient sees, DC up to `f_max` = 1/(2·`MaxStep`), rather
than around harmonics. A frequency-defined element must be passive over the whole band, not just at its samples, or the
transient grows without bound. TRN-4 therefore checks passivity on a dense frequency grid beyond the samples, enforces
it by the smallest residue perturbation that restores it, and reports the fit error and the perturbation. A fit that
misses its tolerance is refused naming the element; there is no first-order fallback in transient, since there is no
harmonic to be first order around. Data that stops short of DC is extrapolated to DC from its lowest points and the run
says so; data that stops below `f_max` is held at its last measured value and the run says so.

As built (TRN-4, `TransientRealization`, `RationalPassivity`): the check grid is ten times denser than the samples to
`f_max`, with a logarithmic tail to 100·`f_max`, every pole's frequency and s → ∞; enforcement moves residues and D by the
least change of the response over the samples, constraining each violating band's peak and points spread across it. A
fit order with a pole closer to the jω axis than a damping of 1e-6 is not taken. A tone source's harmonic terminations are
not fitted but placed: one damped resonator pair on each harmonic, the residues solved so every harmonic takes its band's
impedance exactly (a fit asked for the harmonics alone puts undamped poles on them).

### 5.5 Refused elements

Behavioural frequency-domain system blocks whose definition is a spectrum operation (Mixer, the System Amplifier's
spectral form), the Phased Array, the Propagation Channel, Tuners, WSProbe and the EM Field Data block are refused by
name in a tran analysis, each with a sentence naming the analysis that supports it. A `MOD_Source` is refused with
"modulated signals are simulated by the envelope analysis" (its waveform upconverted to RF would be carrier-resolved
over a symbol record, which is what the envelope exists to avoid).

## 6. Sources

### 6.1 Waveform sources

| Type (`V_` and `I_`) | Palette | Keys | Breakpoints |
|---|---|---|---|
| `V_Pulse` | Pulse | `V1 V2 Delay Rise Fall Width Period` (`Period` blank: one pulse) | every corner |
| `V_PWL` | PWL | `Points="0 0, 1n 1, 2n 1, 3n 0"` (time value pairs) or `File=` (two columns, time and value; CSV or whitespace), not both; `Repeat=yes|no` | every vertex |
| `V_Exp` | Exponential | `V1 V2 Delay1 Tau1 Delay2 Tau2` | `Delay1`, `Delay2` |
| `V_SFFM` | SFFM | `Offset Amplitude Carrier ModIndex Signal` | — |
| `V_Expr` | Expression | `Expr=` an expression in `time` and the globals | none; `MaxStep` governs |

The `I_` types take the same keys in amperes (`I1`/`I2` where the voltage type says `V1`/`V2`). Each also takes `Dc=`,
the value in non-transient analyses; blank, its waveform at t = 0 (D6). Every numeric key is an expression.

`Idc:I1 p n Idc=1m` is the DC current source, the counterpart of `Vdc`: constant in every analysis, an open at every
frequency but DC.

### 6.2 Existing sources in transient

- **DC sources** (`Vdc`, `Idc`, and a tone source's `Vdc`/`Idc` offset): constant.
- **Tone sources** (`V_1Tone`, `I_1Tone`, `V_nTone`, `I_nTone`, `P1Tone`, `PnTone`): each tone is a sinusoid at its
  frequency, amplitude and phase, plus the DC offset. Three transient-only keys, ignored by every other analysis:
  `Delay` (the tone starts at this time), `Ramp` (it reaches full amplitude over this time along a raised-cosine
  envelope; default 0, a step) and `Damping` (an exponential decay rate after `Delay`, 1/s; default 0). Breakpoints at
  `Delay` and `Delay + Ramp`. With these, a tone source is everything the dialect's `SIN` stimulus is. `P1Tone`/`PnTone` keep their available power and source impedance; harmonic
  terminations `Z[k]` other than the fundamental impedance are frequency-defined and realized by §5.4's fit.
- **`MOD_Source`**: refused (§5.5).

### 6.3 `time` in expressions

`time` is the simulated time in a tran analysis. It is bound only in transient: in every other analysis an expression
that uses `time` outside a condition is refused naming it, and inside a condition keeps the steady-state reading
`spice-models.md` §8.10 already gives it (`time > 0` true). This one rule serves `V_Expr`, SDD equations
and imported behavioural sources.

## 7. The switch and initial conditions

### 7.1 `Switch`

`Switch:S1 p n cp cn Ron=1 Roff=1e9 Von=1 Voff=0`: a two-terminal conductance controlled by the voltage between
`cp` and `cn`. Its conductance moves between 1/Roff and 1/Ron along a smooth monotone transition between `Voff` and
`Von` (`Von < Voff` gives hysteresis-free inverse logic; a separate `Hyst=` adds hysteresis in transient only). It is an
ordinary nonlinear `ComponentModel`, so it also works in DC, S-parameter (at its bias) and HB, where the smooth
transition is what keeps harmonic balance's spectrum finite. The transition's corners are reported to the step control
as soft breakpoints (the step is limited across them, not forced to land).

### 7.2 Initial conditions

`IC=` on a capacitor (its voltage) and on an inductor (its current), applied in the operating-point solve (§4.5).
The SPICE-dialect reader keeps `IC=` instead of noting and dropping it (`spice-models.md` §8.6), and stimuli
become the matching type instead of `Vdc` at their DC value (§9.3 of that note): `PULSE` → `V_Pulse`, `PWL` → `V_PWL`,
`EXP` → `V_Exp`, `SFFM` → `V_SFFM`, `SIN` → `V_1Tone` with `Delay` and `Damping`, a DC current source → `Idc`, and the
`I_` forms likewise.

## 8. Measurements and display

Time-domain functions in the one expression engine, over a cube's time axis, usable in `measure` lines and traces:

| Function | Returns |
|---|---|
| `rise(x, lo=10, hi=90)`, `fall(…)` | First rise/fall time between the percentages of the settled swing (s) |
| `overshoot(x)`, `undershoot(x)` | Percent of the final step |
| `settle(x, tol=1)` | Time after which x stays within tol % of its final value (s) |
| `period(x)`, `freq(x)` | From interpolated level crossings at the mean, averaged over the last half of the record |
| `at(x, t)`, `tmin(x)`, `tmax(x)`, `avg(x, t1, t2)`, `rms(x, t1, t2)` | Value and statistics over a window |
| `fft(x, f0, periods=1, window=none)` | Complex spectrum of the last whole `periods` of x at f0 and its harmonics, an axis cube shaped like an HB result |
| `thd(x, f0, n=10)` | Total harmonic distortion of the last period (%) |

`fft` of a settled transient is shaped like the HB spectrum on purpose: the transient-versus-HB consistency gate (§12)
and the seed (§10) both read it, and a user can plot a transient's harmonics on the same axes as an HB run's.

The Data Display gains a **Transient** preset: the recorded voltages against time, a second plot of `fft` of the output
probe, and the measures as a table. The analysis card shows `Stop`, `Step`, `Method`, `InitialState` and `Solver`,
with the rest under More; `Solver`'s ngspice choice is enabled only when ngspice is discovered (D2). The note beneath it
has its space reserved whether shown or not, so the card's controls do not move when ngspice appears or disappears.

## 9. ngspice

### 9.1 Discovery and install

`SolverTool.Ngspice` joins Palace, Gmsh, openEMS and EMerge in the existing machinery, with no new mechanism:

- **Discovery** — Settings path, `CIRCUITRF_NGSPICE`, PATH, the default directories (Homebrew's prefixes on macOS,
  `/usr/bin` on Linux, the archive's install folder on Windows), conda environments, and circuitRF's own install.
  Version from `ngspice -v`. The Settings ▸ Solvers tab gets a **Circuit** group above the EM group, with the ngspice row.
- **Install** — an upstream binary per platform through a recipe; no source build (D10):
  - **Windows**: the official release archive, downloaded and extracted (the Gmsh pattern). If upstream publishes no
    arm64 build, Windows on arm64 runs the x64 build under the operating system's emulation and the row says so.
  - **macOS and Linux**: the conda-forge package, installed into circuitRF's own environment by a micromamba bootstrap
    (micromamba's single static binary, downloaded and checksummed as uv is for EMerge). Distribution packages are
    found by discovery but are not what the recipe installs, because their versions lag.
  - A platform with neither gets a `ByHand` message naming upstream's build instructions.
  - Every recipe pins one version and checksums every download, as every existing recipe does.
- **No credential, no network unasked** — the existing consent dialog and `circuitrf solver install ngspice` are the
  only routes that download anything.

### 9.2 Translation

`SpiceDeckWriter` (`src/Core/Netlist/Spice`) writes the elaborated bench, flattened, as one deck: a header comment
naming the circuitRF bench and version, a name map, the elements, `.options` (`RelTol`, `VTol`, `ITol`, `Method`, and
`SpiceOptions` verbatim), `.ic` for held nets, `.tran Step Stop Start MaxStep` with `uic` for `InitialState=zero`, and
`.save` for every recorded net and probe current.

#### 9.2.1 The element table (TRN-6)

| circuitRF | Dialect | Exact? |
|---|---|---|
| R, C, L (with `IC=`) | `R`, `C`, `L` | yes |
| Mutual | `K` | yes |
| Transformer (ideal N:1) | a VCVS/CCCS pair | yes |
| VCCS, VCVS | `G`, `E` | yes |
| `IProbe` | a zero-volt `V` (its current read back) | yes |
| Port / Term (undriven) | `R` of its Z | yes (a complex or frequency-dependent Z is §5.4's, refused) |
| `Vdc`, `Idc`, a tone source's offset | `V`/`I` `DC` | yes |
| `V_Pulse`, `V_PWL`, `V_Exp`, `V_SFFM` and the `I_` types | `PULSE`, `PWL`, `EXP`, `SFFM` | yes; a `File=` PWL written inline |
| `V_Expr`, `I_Expr` | a behavioural source in `time` | yes |
| Tone sources, `P1Tone` | `SIN` with `Delay` and `Damping` (behavioural with `Ramp`), series `R` for a P1Tone's impedance, amplitude from available power | yes; non-default `Z[k]` refused |
| Ideal lossless TLine | `T` | yes |
| `Switch` | a behavioural conductance with the same transition | yes |
| SDD | behavioural current sources; each charge as a behavioural voltage source driving a unit capacitor — the inverse of the charge idiom `spice-models.md` §8.8 reads | yes |
| `SpiceModel` (placed file) | the file included as it is, its instance line as read | yes, by construction |
| Verilog-A, `.osdi` | ngspice's OSDI loader on the same compiled file | yes |
| Diode, BJT, MOS level 1 and 3 | the dialect's models | only where every parameter maps one to one; TRN-6 tabulates each model's parameters and refuses the rest by parameter |
| FET laws (Curtice, Statz, Materka, Angelov), composed FETs, JFET, IGBT, VDMOS, nonlinear and semiconductor capacitors, beads | — | refused by name in v1 |
| Microstrip, stripline and coplanar lines; lossy ideal lines | `TXL` (lossy line, per-unit-length R, L, G, C) | no: refused by name unless `SpiceLines=rlgc` (§9.2.3) |
| Coupled lines of those families | `CPL` (coupled lossy lines, per-unit-length matrices) | no: refused by name unless `SpiceLines=rlgc` (§9.2.3) |
| Discontinuities (bends, tees, gaps, steps, crosses, opens) | `L`, `C`, `R` | yes where circuitRF's model is a fixed lumped network whose values depend only on geometry; otherwise refused by name unless `SpiceFit=yes` (§9.2.3) |
| SnP, filters, EM cells, other frequency-defined elements | — | refused by name unless `SpiceFit=yes` (§9.2.3) |
| System blocks, Phased Array, Propagation Channel, Tuner, probes other than `IProbe` | — | refused by name |

#### 9.2.2 Names

Dialect names are case-insensitive and restricted in their characters; circuitRF's are neither. Every net and
instance path is written as a sanitized name, unique after case folding, and the map is written into the deck's header
as comments and kept beside the run, so every vector read back is renamed to its circuitRF net or instance path. Every
element line carries its circuitRF path as a trailing comment.

#### 9.2.3 Approximate translations, asked for

**Lines as ngspice's own lossy lines — `SpiceLines=rlgc`.** ngspice has lossy-line elements described by per-unit-length
R, L, G and C (`TXL` for one line, `CPL` for a coupled set) but no line described by its geometry and substrate.
circuitRF's line models are: they carry dispersion and a loss that grows roughly with the square root of frequency,
which constant per-unit-length values cannot. With `SpiceLines=rlgc`, each microstrip, stripline, coplanar or lossy
ideal line is written as `TXL`, and each coupled set as `CPL`, with R, L, G and C (matrices for `CPL`) extracted from
circuitRF's own model at `SpiceLineFreq` (default: the highest frequency any source carries): L and C from the
model's characteristic impedance and effective permittivity there, R and G from its conductor and dielectric loss
there. The values therefore match circuitRF's model exactly at that one frequency and drift from it elsewhere.

How far they drift is reported, not left to the user to guess: for each line, the writer compares the S-parameters of
the `TXL`/`CPL` line (computed in closed form from its R, L, G, C) with circuitRF's model on a grid from the lowest to
the highest frequency the transient resolves (`1/Stop` to `f_max`), and the result carries one note per line, "ngspice
ran <line> as a TXL fitted at <f>; largest |ΔS| <x> at <f'>". Absolute |ΔS| is the measure, since a small S entry's dB
error says little. `check` under `Solver=ngspice` reports the same number before a run. Default `SpiceLines=refuse`.

**Fits as subcircuits — `SpiceFit=yes`.** It writes each frequency-defined element (§5.4) as a subcircuit realizing the native engine's rational fit,
and each lossy line as a lossless `T` plus that same causal residual's realization. The result then carries a note per
element: "ngspice ran circuitRF's fit of <element>, not the element itself". Default `no`.

The two can be combined. Where both apply to a line, `SpiceLines=rlgc` wins, because it is ngspice's own element and
the reason a user would ask for it; `SpiceFit=yes` then covers what `rlgc` cannot (SnP, filters, EM cells, and
discontinuities whose models vary with frequency). `SpiceFit` is the closer answer across the band for a line too, and
a user who wants that leaves `SpiceLines` at `refuse`.

#### 9.2.4 The round trip

The writer is gated by circuitRF's own reader: every translatable bench, written and read back, elaborates to the same
netlist. The reader and the writer share the element table, so a mapping cannot drift in one direction only.

### 9.3 Running and reading back

The deck and the map are written to a scratch folder under the run's output directory; ngspice is run in batch mode
with a binary rawfile as its output, on a `RunControl` that kills the process on cancel. ngspice's own messages are
relayed verbatim, prefixed with its name and version. `RawFileReader` (`src/RfCore`) reads the binary and ASCII
rawfile, renames the vectors through the map, and resamples them onto the output grid (D11) with the same interpolation
the native engine uses. A failed run (non-zero exit, no rawfile, a vector missing) is a refusal with ngspice's last
message.

### 9.4 What "the same answer as ngspice" means

With `Solver=ngspice` the waveforms are ngspice's, solved by ngspice, on a deck the user can open and run by hand: it is
ngspice's answer to the circuit as translated. Because the translation is exact or refuses (D7), that is the drawn
circuit's answer, with one caveat the docs state: a model circuitRF and the dialect both name (a diode, a BJT) is
translated only where the equations match parameter for parameter, so the set of translatable benches is deliberately
smaller than the set the native engine runs.

## 10. Transient-assisted harmonic balance

### 10.1 The idea

Harmonic balance solves for a periodic steady state directly, from a starting guess, usually the DC point with the
drive stepped up. A circuit whose steady state is far from that path (a switch that only conducts at full drive, a
divider whose divided solution is not reachable continuously from the undivided one, an oscillator) can fail to
converge or converge to the wrong solution. A transient run approaches the steady state the physical way, from rest.
A few periods of it, transformed, are a starting point that is already near the solution, and harmonic balance then
finishes in a few Newton iterations to its own accuracy.

### 10.2 For `hb`

`InitialGuess=transient` on an `hb` directive (single fundamental, or commensurate tones whose period is the common
one):

1. Solve the DC point (as HB already does).
2. Run the native transient from it with every source at its full HB drive, step `T/(8·(MaxHarm+1))`, for up to
   `SeedPeriods` periods (`auto`: until two consecutive periods of every interface waveform agree within `SeedTol`,
   default 1e-3 relative, capped at 200 periods).
3. Transform the last period on the interface nodes (§8's `fft`) into `V[N, K+1]` and hand it to `HbEngine` as
   `warmStart`. Harmonic balance converges from there with no drive stepping.

**Bias networks.** An RF bias network settles over its choke's and block's time constants, which can be 10⁴–10⁶
carrier periods. Running that long only to start harmonic balance would defeat the purpose. With `SeedFastBias=yes`
(default), the seeding run scales down each inductor and capacitor whose reactance at the fundamental makes it an RF
choke or block (|X| > 10× or < 0.1× the impedance it sees, measured at the DC point), by the factor that brings its
time constant to a few periods while its reactance at the fundamental stays beyond that threshold. They remain chokes
and blocks at RF and settle quickly. Only the seed run is altered, harmonic balance solves the real circuit, and the
run notes which elements were scaled. The seed's DC component is taken from the transient's last-period average and
harmonic balance corrects any residual shift.

`InitialGuess=fallback` tries the DC start first and seeds from transient only when that fails to converge. In a
parametric sweep, only the first point (and any point that fails) is seeded by transient; the rest warm-start from
their neighbour as today. Multi-tone HB that is not commensurate is refused with `transient`, and `fallback` falls back
to the DC start with a note.

### 10.3 For `osc`

`InitialGuess=transient` on an `osc` directive makes `Fguess` optional. The seed run starts from the DC point with a
small kick at the OscProbe's node (an initial condition of `KickV`, default 1 % of the supply, released at t = 0⁺), runs
until the probe node's amplitude is constant within `SeedTol` over consecutive periods, and reads the frequency from its
level crossings and the amplitude from its fundamental. Those become `Fguess` and `Aguess`, and the interface waveforms
of the last period become the oscillator analysis's harmonic starting point. If no oscillation grows within the
period cap, the refusal says so and includes the probe's small-signal start-up result, which says whether the start-up
condition held anywhere.

### 10.4 Reported

`Seed.Periods`, `Seed.Settled` (bool), `Seed.ScaledElements` (names), `Seed.HbIterations`, and the note "harmonic
balance started from a transient seed". The seed is never the result; the result is harmonic balance's, to its own
tolerance.

## 11. CLI, MCP, `check`, `explain`

- **`circuitrf tran <bench>`** — a run verb on `cli.md`'s five-step anatomy, as `hb` is; `--set`, `-o out.{mat,npy,txt}`,
  promotion of a tran inside a `parametric_sweep`. `--solver ngspice` replaces the directive's `Solver`.
- **`circuitrf netlist <bench> -o out.cir`** — the translation (§9.2), written for the bench's tran analysis; any
  refusal exits 1 and writes nothing.
- **`circuitrf solver install ngspice`** — the existing verb, with the new tool.
- **MCP** — `run analysis=tran`, `netlist` with a `.cir` output, `solver` reporting ngspice.
- **`check`** — under `Solver=ngspice`, every element the translation would refuse, coded `check.tran.untranslatable`;
  in any tran bench, refused elements (§5.5); a waveform source whose `Dc` differs from its t = 0 value (info, since it
  means the DC point and the first step disagree on purpose).
- **`explain --analysis`** — the solver, the step bound and its reason, the estimated step count, the slowest time
  constant against `Stop`, and a steering line when a tran is the wrong analysis: "this bench is 2.4·10⁶ periods of a
  2.4 GHz carrier; harmonic balance or envelope answers a steady-state or modulated question in a fraction of the time".

## 12. Validation

1. **Analytic.** RC and RL steps, an RLC ring, a charge-pump, a lossless line's pulse reflection: closed forms, to
   `RelTol`-consistent tolerances stated per gate.
2. **Transient against HB, no external tool.** A circuit driven by a tone, run to steady state, its last period through
   `fft`, equals the HB solution of the same circuit: harmonics within 0.01 dB and 0.1° for a weakly nonlinear amplifier.
   This gates the integration, the device evaluation and `fft` against an engine already gated against the heroes.
3. **Against ngspice, externally generated.** Decks written by the translation, run once by ngspice, their waveforms
   committed under `testdata/transient/` with the deck, the script and the ngspice version: references produced
   independently of circuitRF, in the PRD's sense. The native engine is compared to them on the output grid; tolerances
   are stated per circuit because two adaptive-step engines agree to their tolerances, not to round-off.
4. **The ngspice path itself.** Tests that need a live ngspice skip with a reason when none is discovered (the
   `FixtureFact` pattern), so CI without ngspice stays green and the owner's machine runs them.
5. **Translation round trip** (§9.2.4) and refusal-by-name tests for every refused class.
6. **Seed.** A class-E stage that harmonic balance does not converge from DC without drive stepping converges with
   `InitialGuess=transient` to the same solution as a drive-stepped run where one exists; a ÷2 divider converges to its
   divided solution; an oscillator without `Fguess` finds the frequency and amplitude the `Fguess` run finds.

## 13. Risks

1. **Convergence on hard-switching circuits.** Per-step Newton on a circuit that switches abruptly is the hardest part
   of any transient engine and has no end of special cases. The step ladder, backward-Euler steps at breakpoints,
   junction limiting and the soft breakpoints of §7.1 are the defences; ngspice beside it limits how far this has to go,
   because a user with a pathological switching circuit has a second solver one click away. TRN-1 states which circuits
   it was measured on.
2. **Wideband rational fits and passivity.** An envelope run sees narrow bands around harmonics; transient sees
   everything from DC up, and a fit that is non-passive anywhere grows without bound. §5.4's dense check and enforcement
   are the defence; data that is noisy or stops short of DC is the remaining risk, reported per element.
3. **Lossy-line causality.** §5.3's minimum-phase residual is the designed answer; if it cannot meet a useful tolerance
   for the shipped microstrip models, TRN-4 stops and reports, and lossy lines are refused by name in transient rather
   than shipped with an unchecked approximation.
4. **The translatable set is small at first.** Exact-or-refuse (D7) means most RF benches with microstrip, SnP blocks or
   FET-law devices do not run on ngspice in v1. That is the honest scope, stated in the docs; `SpiceLines=rlgc`, `SpiceFit=yes` and later
   FET-law translations widen it, each with its error or label in the result.
5. **Time-scale disparity.** RF circuits with bias networks are stiff in time. `explain`'s steering line and `MaxSteps`
   keep a user from waiting an hour for an answer harmonic balance gives in seconds; §10.2's fast-bias scaling exists
   for the same reason.
6. **External devices.** A Verilog-A device in a worker process costs a round trip per evaluation, and transient cannot
   batch over time. TRN-1 measures it on the reference worker and records it; if it dominates, a batched Newton on the
   interface (all of one iteration's device evaluations in one round trip) is the remedy, and it is the worker
   protocol's to add.

## 14. Not here

Transient noise; digital and mixed-signal elements; current-controlled switches; lossy coupled lines; Monte Carlo
beyond what a parametric sweep already gives; a periodic-steady-state (shooting) analysis, which this engine makes
possible later; `MOD_Source` in transient; ngspice for any analysis but transient; FET-law translations to the dialect
(each a later brief if wanted); linking ngspice as a library.

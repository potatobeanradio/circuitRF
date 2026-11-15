# Brief series TRN — transient analysis, the ngspice solver, transient-assisted harmonic balance

**Goal.** A schematic drawn in circuitRF can be simulated in the time domain: by circuitRF's own transient engine, the
default, or by the user's ngspice, chosen on the analysis card, without drawing the circuit again. The same engine gives
harmonic balance and the oscillator analysis a starting point taken from a short transient run, for circuits that do not
converge from their DC point. circuitRF stays harmonic-balance first; transient sits beside it.

**Design note:** [`docs/design/transient.md`](../design/transient.md). Read it first; its section numbers are cited
below as "note §n".

## Why a series

circuitRF solves DC, S-parameters, harmonic balance, loadpull, envelope and EM. Four things are missing for time-domain
work:

1. **There is no transient analysis.** A pulse response, a start-up, a switching waveform or a settling time has no
   analysis to run; the envelope engine steps time, but only around carrier harmonics.
2. **Sources have no time-domain waveforms.** Nothing describes a pulse, a piecewise-linear ramp or a delayed,
   ramped tone; there is no switch and no initial condition. The SPICE-dialect reader drops stimuli to their DC value
   and refuses switches and `time`.
3. **A bench cannot be handed to another simulator.** A design that a user wants checked in the SPICE dialect has to be
   drawn again by hand, with no translation, no list of what does not translate, and no way back.
4. **Harmonic balance can only start from its DC point.** A switching PA, a divider or an oscillator whose steady state
   is far from that point either fails to converge or needs a frequency guess the designer has to supply.

## What already exists and is reused

| Need | Existing piece |
|---|---|
| Time-domain device evaluation, charge-based | `ComponentModel.Evaluate` (`i`, `q`, `dg`, `dc`) |
| A stepped Newton on the interface, BDF2 with a backward-Euler start | `EnvelopeEngine`, `EnvelopeNewton` |
| Lumped linear models as `G` and `C` | the envelope's two-stamp recovery |
| SnP, filters, Z(freq), EM results as linear unknowns | `EnvelopeSnpFit` |
| Lines as delays, passive sub-step interpolation | `EnvelopeLineDelay` |
| DC operating point, limiting, damping | `NonlinearDcEngine`, `BiasFreeze` |
| HB and oscillator starting points | `HbEngine` `warmStart`, `OscEngine` (`Fguess`, `Aguess`) |
| The SPICE dialect: reader, element mapping, charge idiom, placed model files | `src/Core/Netlist/Spice`, `SpiceModel`, `spice-models.md` |
| Compiled Verilog-A, per platform | `VerilogACompiler`, `OsdiBuilds` |
| External solver discovery, recipes, consent, Settings rows, CLI install | `SolverDiscovery`, `SolverRecipes`, `SolverInstaller`, `SolverInstallRunner`, `circuitrf solver install` |
| Time-axis cubes, plots, measurements | envelope results, Data Display, `MeasurementEvaluator` |

## The briefs, in order

| Brief | What | Depends on |
|---|---|---|
| `brief-trn-1-engine-core.md` | `type=tran`; the native engine on lumped and nonlinear models: integration, step control, the step ladder, breakpoints, Newton paths, the starting state, the output grid; `circuitrf tran`; MCP; the PRD's scope line | — |
| `brief-trn-2-sources-and-initial-conditions.md` | `V_Pulse`, `V_PWL`, `V_Exp`, `V_SFFM`, `V_Expr`, their `I_` twins and `Idc`; tone sources in transient (`Delay`, `Ramp`, `Damping`), `IC=`, `time` in expressions, the reader's stimuli and `IC=` | 1 |
| `brief-trn-3-switch.md` | The `Switch` device in every analysis; the reader's switch lines | 1 (2 for its tests' sources) |
| `brief-trn-4-distributed-and-frequency-defined.md` | Lossless lines and coupled lines as delays, the causal lossy-line residual, wideband rational fits with passivity enforcement, EM cells, refusals by name | 1 |
| `brief-trn-5-measurements-and-display.md` | Time-domain functions, `fft`, `thd`; the Transient Data Display preset; the analysis card; `check` and `explain` steering; the transient-versus-HB gate | 1, 2 |
| `brief-trn-6-spice-deck-writer.md` | The translation: element table, names, refusals by name and parameter, lines as `TXL`/`CPL` on request with their error, the round trip, `circuitrf netlist -o x.cir` | 2, 3 |
| `brief-trn-7-ngspice-solver.md` | `SolverTool.Ngspice`: discovery, recipes, the Settings row; the run, the rawfile reader, `Solver=ngspice`, cancellation, provenance; references committed for the native gates | 6 |
| `brief-trn-8-transient-assisted-hb.md` | `InitialGuess=dc|transient|fallback` on `hb` and `osc`; fast-bias scaling; sweeps | 1, 4, 5 |
| `brief-trn-9-docs-and-examples.md` | User pages, the reference pages, three shipped examples | all |

TRN-2, TRN-3 and TRN-4 depend only on TRN-1 and can be built in any order. TRN-6 needs no ngspice installed. TRN-7 is
the first brief that runs ngspice, and it commits the references the native engine is measured against from then on.
TRN-8 is the part of the series that serves harmonic balance directly; if the series is cut short, it is the one to
keep.

## Decisions already made

- **D1 — One analysis, `type=tran`**, on a TestBench (note §2 D1, §3).
- **D2 — `Solver=native|ngspice`, native by default.** The card's ngspice choice is disabled (greyed) while no usable
  ngspice is discovered, with a tooltip naming Settings ▸ Solvers; a bench that already says ngspice is refused at run
  time with the install sentence, never run on native instead.
- **D3 — Transient quantities are real**; result cubes `DataKind.Real`. The AC/HB complex invariant is untouched.
- **D4 — No new analysis for the seed**: `InitialGuess=` on `hb` and `osc`, default `dc`, so no existing result moves.
- **D5 — One source type per waveform, named after it** (`V_Pulse`, `V_PWL`, `V_Exp`, `V_SFFM`, `V_Expr`, the `I_`
  twins) plus `Idc`; no separate sine type, since the tone sources gain `Delay`, `Ramp` and `Damping`, transient-only.
- **D6 — A waveform source is its `Dc` value in every other analysis** (default its t = 0 value).
- **D7 — The translation is exact or it refuses**, by element or by parameter. Approximate translations exist only when
  asked for (`SpiceFit=yes`) and are labelled per element in the result.
- **D8 — ngspice runs as a separate process** in batch mode, read back from its rawfile; it is never linked.
- **D9 — ngspice is named**, as Palace, EMerge and openEMS are; no other simulator is named, and the dialect is "the
  SPICE dialect".
- **D10 — Upstream binaries only; no source build is shipped.** Windows: the official archive. macOS and Linux: the
  conda-forge package through a micromamba bootstrap. Elsewhere: a `ByHand` message.
- **D11 — One uniform output grid** for every transient result, whichever solver.
- **D12 — The native engine is circuitRF's own.** ngspice's source code is not read for it; ngspice is a solver to call
  and a reference to measure against.

## Risks — say them now

1. **Hard-switching convergence** is where transient engines spend most of their life. The defences are in note §4 and
   §7.1; ngspice beside the native engine is the release valve. TRN-1 and TRN-3 state the circuits they were measured
   on, and a circuit the native engine cannot finish is recorded in `src/Engine/RESOLVED.md` rather than hidden.
2. **Wideband rational fits must be passive everywhere**, not only at their samples, or a transient grows without bound.
   TRN-4 checks and enforces passivity on a dense grid and reports the perturbation.
3. **Lossy-line causality.** The line models attenuate with no matching phase. TRN-4's minimum-phase residual is the
   design; if it cannot meet a stated tolerance on the shipped microstrip models, TRN-4 stops and reports, and lossy
   lines are refused in transient by name.
4. **The ngspice-translatable set starts small.** Exact-or-refuse leaves microstrip, SnP and the FET laws out of v1
   ngspice runs by default. `SpiceLines=rlgc` brings lines in as ngspice's own lossy lines, with each line's error
   against circuitRF's model reported; `SpiceFit=yes` brings in frequency-defined elements, labelled. The docs say so
   plainly.
5. **Stiffness.** An RF bias network makes a transient 10⁴–10⁶ carrier periods long. `explain` steers users to HB or
   envelope; TRN-8's fast-bias scaling keeps seeding short.
6. **External devices** cost one worker round trip per evaluation in transient. TRN-1 measures and records it.

## Simulation budget, for every brief

- No EM runs in this series. EM cells are tested with committed small results.
- A routine test stays under ~5 s. No new `Category=Benchmark` test and no timing assertion: costs go in `RESOLVED.md`,
  gates use counters (`Tran.AcceptedSteps`, `Tran.Factorizations`, Newton iterations).
- Tests that need a live ngspice skip with a reason when none is discovered; committed references keep the native side
  gated without it.
- Reference waveforms are generated outside circuitRF and committed under `testdata/transient/` with the deck, the
  script and the ngspice version.
- One test per claim. Run the named test classes only, never all of `Ui.Tests`. Ad-hoc measurements carry a memory
  watchdog on the real process id.
- Findings go to the nearest `RESOLVED.md` (`src/Engine/RESOLVED.md` for the engine, `src/Core/RESOLVED.md` for devices,
  sources and the translation, `src/Design/RESOLVED.md` for discovery and recipes, `src/RfCore/RESOLVED.md` for the
  rawfile reader) and to each brief's completion note.

## Not here

Note §14: transient noise, digital and mixed-signal, current-controlled switches, lossy coupled lines, a shooting
analysis, `MOD_Source` in transient, ngspice for analyses other than transient, FET-law translations, linking ngspice.

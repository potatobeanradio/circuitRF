# Brief TRN-1 — the transient engine on lumped and nonlinear models

**Series:** TRN-0 … TRN-9 · **Tag:** `R-trn1-<n>` · **Depends on:** — · **Design note:** §2, §3, §4, §5.1, §5.2, §11
**Area:** `src/Engine/Transient/` (new), the shared pieces moved down out of `src/Engine/Envelope`,
`src/Core/Netlist/AnalysisDirectiveSchema.cs`, the netlist reader and writer, `src/Cli` (`tran` verb, `explain`),
the MCP `run` tool, the analysis card, `docs/PRD.md`, `src/Engine/RESOLVED.md`.

## The gap

There is no time-domain analysis. The envelope engine steps time only around carrier harmonics with a fixed step, so a
pulse response, a start-up or a switching waveform has nothing to run on.

## Requirements

**R-trn1-1 — Scope line.** `docs/PRD.md` §"Not a SPICE simulator" is rewritten to say circuitRF has a transient
analysis beside harmonic balance, which stays the primary analysis, and links the design note. No other PRD scope line
changes.

**R-trn1-2 — The directive.** `type=tran` (alias `transient`) with note §3's keys in `AnalysisDirectiveSchema`, read
and written by the `.cnl` reader and writer; `NetlistContractTests` holds every key. `Solver=ngspice` is accepted by
the schema and refused at run time until TRN-7 ("the ngspice solver is not built yet"); `SpiceOptions` likewise.

**R-trn1-3 — Shared pieces moved down.** The two-stamp `G`/`C` recovery is moved from `EnvelopeEngine` to a shared class
both engines call. Every existing envelope gate stays green; no envelope result changes, bit for bit, on the envelope
tests' own benches.

**R-trn1-4 — Integration** per note §4.2: `trap` (default) and `gear2` with variable steps, two backward-Euler steps
after every breakpoint and at the start.

**R-trn1-5 — Step control** per note §4.3: the predictor–corrector error estimate on every reactive element's charge or
flux; the step ladder `MaxStep/2^k` with one cached factorization per rung; breakpoints landed exactly; halving on
rejection; the stop below `MaxStep/2^40` naming the time and the device with the largest update.

**R-trn1-6 — Newton** per note §4.4: the interface path (Schur complement through the rung's factorization, dense
Newton on the interface) and the full sparse path; the threshold between them measured on at least three circuits
(an amplifier with one FET in a large linear network, a ring of 15 inverters built from MOS level 1, a bridge rectifier)
and recorded. Limiting and damping are `NonlinearDcEngine`'s, called, not copied.

**R-trn1-7 — Starting state** per note §4.5: `InitialState=op|zero`, `IC=` nets held through the DC solve and released
at t = 0⁺; `OP.V` reported. (Part-level `IC=` is TRN-2.)

**R-trn1-8 — Output** per note §3 and §4.6: the uniform grid by the method's interpolant, `SaveSteps`, the counters
`Tran.AcceptedSteps`, `Tran.RejectedSteps`, `Tran.NewtonIterations`, `Tran.Factorizations`; `MaxSteps` refused before
the run with the estimate.

**R-trn1-9 — Elements.** Every lumped model the recovery classifies as affine (listed in the test that holds the list)
and every `Evaluate` device runs. Every other element is refused by name until TRN-4 ("<instance> (<type>) is not yet
supported in transient"), with note §5.5's refusals in their final wording now.

**R-trn1-10 — Sources, minimal.** `Vdc` and the tone sources run as their sinusoids from t = 0 (no `Delay`/`Ramp` yet),
so this brief's gates have drive. Everything else in note §6 is TRN-2.

**R-trn1-11 — CLI and MCP.** `circuitrf tran <bench>` on `cli.md`'s five steps, `--set`, `-o`, promotion inside a
`parametric_sweep`; `cli.md` gains its section. MCP `run analysis=tran`. `explain --analysis` reports the step bound
and its reason, the estimated step count and the slowest time constant against `Stop`.

**R-trn1-12 — The analysis card.** A tran card with `Stop`, `Step`, `Method`, `InitialState` and `Solver` (the ngspice
choice present and always disabled until TRN-7, its tooltip saying the solver is not built yet), the rest under More. UiSnapshot PNG, looked at.

**R-trn1-13 — External devices.** The per-step cost of a worker-hosted device measured on `tools/DeviceWorkerExample`
and recorded with the number of round trips per accepted step.

## Gates

1. **RC step.** A 1 kΩ / 1 nF step from `Vdc` through a switch-free RC (the source steps at t = 0 from `InitialState=zero`):
   v(t) = 1 − e^(−t/τ) within 1e-5 of the swing at every output point, `RelTol=1e-6`.
2. **RLC ring.** Underdamped series RLC: frequency within 1e-4 and decay rate within 1e-3 of the closed form over 20
   cycles with `trap`; `gear2` damps more, and the gate states by how much at the default tolerance.
3. **Ladder reuse.** On gate 2, `Tran.Factorizations` ≤ the number of distinct rungs visited plus breakpoints.
4. **Breakpoint.** A test-only source that reports a breakpoint at 1 µs and steps there (TRN-2 adds the real
   waveforms): an accepted step lands on 1 µs exactly, and the two steps after it are backward Euler.
5. **Nonlinear.** A diode half-wave rectifier into an RC: the settled ripple matches a hand-derived closed form within
   1 %, and both Newton paths give the same waveform to 1e-9 relative.
6. **Envelope unchanged.** The envelope test classes pass with the moved recovery, and the envelope's own fixed result
   files are identical.
7. **CLI.** `circuitrf tran` as a process equals the in-process run byte for byte (the existing CLI gate pattern).

## Not here

Waveform sources, `IC=` on parts, `time` (TRN-2), the switch (TRN-3), lines and frequency-defined elements (TRN-4),
measurements and the Data Display preset (TRN-5), ngspice (TRN-6, TRN-7).

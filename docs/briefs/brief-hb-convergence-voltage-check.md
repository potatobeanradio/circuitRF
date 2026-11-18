# Brief — HB convergence judges the voltage update too, and harmonically related tones are flagged

**Tag:** `R-hbv-<n>` · **Area:** `src/Engine/HarmonicBalance/HbNewton.cs` (`SolveJudged`), `HbNewton2D.cs` (`Solve`),
`HbNewtonNd.cs` (`Solve`), `HbSolveWatch.cs`, `HbEngine.cs` and `HbEngine.Commensurate.cs` (`Commensurate`,
`HbCommensurateDiagnostics`), `src/Engine/AnalysisSettings.cs`, `src/Engine/Loadpull/LoadpullEngine.cs` (its settings copy),
`src/Harmonica/HarmonicaContext.cs` (must stay as it is), `src/Cli/Explain*.cs` (the HB card's notes),
`docs/user/src/reference/simulations.md`, `src/Engine/RESOLVED.md`.

## The gap

A no-load bridge rectifier driven from a two-frequency source shows two HB gaps.

**1. HB reports "converged" while a node's DC level is still unresolved.** The Newton loops' only test is
‖F‖₂ < `Tol`, an absolute residual in amperes (default 1e-6). The DC level of an unloaded peak detector is set by
diode leakage of a few nA, so a residual of 1e-7 A leaves it unconstrained. Measured on the bench below, single tone
at 50 Hz, K = 40:

| `Tol` | Newton iterations | `Vout` DC |
|---|---|---|
| 1e-6 (default) | 15 | 13.033 V |
| 1e-9 | 19 | 13.176 V |
| 1e-12 | — | 13.177 V |

K = 20, 40, 80 and 160 agree at each tolerance, so the error is the stopping rule, not truncation. The settled
answer is 13.18 V. A 3.3 nF version of the bench settles in seconds at the same DC level, and reads 13.173 V on the
native transient and 13.174 V on ngspice (the source replaced by 50 Ω and two `V_1Tone`s), at a 50 µs or a 10 µs step.

**Tightening `Tol` is not the fix.** At `Tol=1e-9`, Hero 2, Hero 4 and most of Hero 5's points run 100 iterations and
fail: their residual floors near 1e-9 A, because a 1 µΩ near-short puts Y ≈ 1e6 S in the interface. A current test
cannot be tight enough for the rectifier and loose enough for the PAs. A test on the voltage update can be both.

**2. Harmonically related tones run as independent tones, silently.** The bench's tones are 50 Hz and 1 kHz from one
source: 1 kHz is harmonic 20 of 50 Hz, so the signal is periodic at 50 Hz with a fixed relative phase.
`HbEngine.Commensurate` runs such a set as single-tone HB on the common fundamental only when retained products
coincide **and** every tone is within `MaxHarm` of it. At 50 Hz and 1 kHz neither holds below MaxMixOrder 21, so
the tones run on independent phase axes. Two things follow:

- the result ignores their relative phase;
- a waveform that one tone at 50 Hz describes needs many more products, so two-tone at MaxMixOrder 5 reads
  13.022 V even at `Tol=1e-12`, against 13.177 V.

Nothing tells the user any of this.

**Already done, do not redo** (uncommitted when this was written; recorded in `src/Engine/RESOLVED.md` under "HB answers
Stop once per Newton iteration"):

- **`HbSolveWatch`** — each of the three loops checks the run's Stop once per iteration. After a second it reports
  "Newton iteration n, residual r A" as the stage detail.
- **`AnalysisSettings.Control`** — carries the run's control into the loops.
- **`RunControl.Stage`** — now clears the stage detail when the stage changes.
- **Envelope `enabled=false`** — the envelope directive now reads it.

R-hbv-1's check goes beside the watch at the top of each loop, which is where `fN < tol` is tested.

### The bench

The bench is generic diode parameters with no external files; the same text is used by every gate below.

```
Diode:D1 a out Is=3.2E-09 A N=1.85 Rs=1.6 Ohm Cj0=6E-13 F Vj=0.0001 V M=0.02 Fc=0.5 Bv=128 V Ibv=5E-05 A Xti=3 Eg=1.1 V
Diode:D2 0 a   (same)
Diode:D3 b out (same)
Diode:D4 0 b   (same)
C:C1 out 0 C=330 uF
PnTone:P1 a b Z=50 Ohm Freq[1]=50 Hz Pavl[1]=25 dBm Freq[2]=1 kHz Pavl[2]=12 dBm
analysis HB1 type=hb Tone=50 ToneUnit=Hz MaxHarm=40
```

Each diode also has, in parallel, a second junction (`Is=3E-13 A N=1.1 Rs=280 Ohm`) and a 5 GΩ resistor. Keep both:
they set the leakage the DC level balances against. The two-tone form replaces the analysis line with
`NumFreqs=2 Tone[1]=50 ToneUnit[1]=Hz Tone[2]=1 ToneUnit[2]=kHz MaxHarm=5 MaxMixOrder=5`.

## Requirements

**R-hbv-1 — A solve converges when its residual AND its last update are small.**

In `HbNewton.SolveJudged`, `HbNewton2D.Solve` and `HbNewtonNd.Solve`, `fN < tol` alone no longer returns converged
after the first iteration. It must also hold that the last ACCEPTED update — the step the line search actually applied,
`λ·ΔV` — is small at every interface node:

  max over k of |λ·ΔV[n,k]|  ≤  `VRelTol` · max over k of |V[n,k]|  +  `VAbsTol`   for every node n

Here k runs over harmonics or mixing products, and the DC bin counts like any other. When the residual passes and
the update does not, the loop takes another iteration, within `HbMaxIter` as now.

- **Where the check lives.** Settings carry it: `AnalysisSettings.HbVRelTol` and `HbVAbsTol`, read through a
  `With…` copy as `WithControl` is. A value of 0 for both is OFF, and OFF is byte-identical to today: same
  iterations, same answer.
  - `HbEngine` turns it on with the defaults from R-hbv-4.
  - `LoadpullEngine` builds its own settings copy. Carry the values across (it copies `HbMaxIter` the same way), so
    loadpull and pursuit judge solves as `hb` does.
  - The envelope engines and `HarmonicaContext` do not turn it on (R-hbv-2, and Not here).
- **The entry state.** A warm-started solve can pass the residual test at iteration 0 with no update to judge. Keep
  today's acceptance there, including `IsToleranceVacuous`'s refusal. A sweep whose neighbouring point is already the
  answer must stay free. Record in `RESOLVED.md` that a warm start passing at entry is judged on the residual only.
- **A directive key, so a user can loosen or disable it.** Add `VTol=` to the `hb` directive: the relative tolerance;
  `0` turns the check off; the absolute floor stays a setting. Wire it through the schema, `CnlWriter`, `CnlReader`
  and the GUI's HB card, as `Tol` is. The key must be one the schema declares, or `NetlistContractTests` fails.
- **Non-convergence says which test failed.** When `HbMaxIter` runs out with the residual under `Tol` but the update
  over its bound, the existing non-convergence warning names the node, the harmonic and the update size: "the residual
  met Tol, but the last update at `Vout` (DC) was 3.1 mV". A user can then raise `MaxIter` or loosen `VTol` knowing
  why.

**R-hbv-2 — harmonicaRF's interactivity is not touched.**

harmonicaRF calls `HbNewton.Solve` directly from `HarmonicaContext` with its own `AnalysisSettings`. Those settings
must leave the check OFF. Every drag, grid drag and inverse solve then runs exactly the iterations it runs today, and
returns the same bits.

- Do not change `HarmonicaSettings`, `EffectiveTol`, or the warm-start path.
- Gates (all must pass unchanged, with no bound edited): `tests/Ui.Tests/Harmonica/HarmonicaDragCostTests.cs`,
  `tests/Ui.Tests/Harmonica/HarmonicaGridDragCostTests.cs`, `tests/Harmonica.Tests/InverseSolveCostTests.cs`.
- Add one test that a `HarmonicaContext` solve's iteration count and interface spectrum are identical with and
  without this brief's code path. Compare against the OFF settings path, not against a recorded number.

**R-hbv-3 — Harmonically related tones are flagged.**

When a multi-tone HB run does NOT take the commensurate path, warn once per run if all of these hold:

- every tone f_t lies within `CoincidenceHz` of n_t · f_min, where f_min is the lowest tone and n_t a whole number;
- n_t ≤ `MaxCommensurateHarmonic`;
- f_min ≥ `CoincidenceHz`.

Use `AddWarningOnce`, keyed on the tone set as `hb-commensurate:` is. The sentence goes in `HbCommensurateDiagnostics`
beside `RanOnCommonFundamental`, with a code like its siblings. It says:

- the tones it names are whole multiples of f_min (give each multiple);
- they are solved as independent tones, so their relative phase has no effect;
- for tones from one generator, run one tone at f_min with `MaxHarm` at least the highest multiple, and more for a
  waveform with sharp features.

For example: "Tones 50 Hz and 1 kHz are harmonics 1 and 20 of 50 Hz. They are solved as independent tones, so their
relative phase has no effect. If they come from one source, run one tone at 50 Hz with MaxHarm ≥ 20."

- **Must stay silent** on closely spaced tones (2.00/2.01/2.02 GHz: 2.01/2.00 is not whole) and on a set the
  commensurate path already ran (100/300 MHz, which announces itself).
- **`explain --analysis`** shows the same sentence under the HB card, which already lists its notes. A user can then
  see it before running.
- **Scan** `examples/` and `testdata/`: list every bench that now warns in `RESOLVED.md`, and say for each whether
  the warning is right.

**R-hbv-4 — Measure before choosing defaults, and stop if the cost is not small.**

Before fixing `VRelTol`/`VAbsTol`, measure with the check OFF and ON at two or three candidate pairs. A reasonable
start is 1e-6 relative with 1e-6 V absolute, then looser. Record Newton iterations (from the `[HB trace]` lines,
`--diag`) and wall time, Release build, for:

- Hero 2, Hero 3/3B, Hero 4, Hero 5 and its `_3tone`, `_6tone`, `_unequal` variants;
- `testdata/Hero2/hero2_convergence.cnl`, a Pin sweep where warm starts matter;
- `examples/Loadpull` (a loadpull bench and `LoadpullPursuit`), and `examples/Receiver Noise/7 VCO Phase Noise` for the
  oscillator;
- the bench above, single tone and two-tone.

Choose the loosest pair that brings the bench's single-tone `Vout` within 1 mV of 13.177 V at the default `Tol`.

- **Stop and report** before going further if that pair adds more than one iteration per solve on average across the
  hero set, or more than 10 % wall time to any loadpull or sweep bench.
- Every hero gate (S-parameters 1e-6; HB Pout/gain ±0.01 dB, efficiency/PAE ±0.1 pp; IM2–IM5) must hold. State the
  largest movement each saw.
- Do not regenerate a golden to absorb a change. A movement beyond its gate is a finding to report.

**R-hbv-5 — Docs.** In `docs/user/src/reference/simulations.md`:

- the `Tol` row says it is a residual in amperes, and that a solve also needs its last update under `VTol`;
- add a `VTol` row;
- the tones text says when tones run on one fundamental, when they are flagged, and what to do about it.

Follow the repo's practice on regenerating the user docs.

## Gates

- `HbVoltageCheckTests` (new):
  - The bench, single tone, default `Tol`: `Vout` DC within 1 mV of 13.177 V. Show once that it reads 13.03 V with
    the check off, and record that number.
  - The same with `VTol=0`: today's iteration count and today's answer, bit for bit.
  - Hero 2 at its gate settings still converges.
- The R-hbv-2 identity test, plus the three harmonicaRF cost tests unchanged.
- `HbHarmonicTonesWarningTests` (new): warns on 50 Hz + 1 kHz, naming harmonic 20; silent on 2.00/2.01 GHz; silent
  on 100/300 MHz (the commensurate path's own note only); the same sentence in `explain --analysis`.
- `NetlistContractTests`: `VTol` is declared, written and read.

## Tests to run

Run only what the change can reach:

- `tests/Engine.Tests` filtered to `HarmonicBalance|Hero|Loadpull|ParametricSweep|Osc`;
- `tests/Harmonica.Tests`;
- `tests/Ui.Tests` filtered to `Harmonica`;
- `tests/Core.Tests` filtered to `NetlistContractTests`.

Read each run's TRX for failures rather than re-running. Run the full suite only if the owner asks.

## Not here

- **The envelope engines' per-step solves** (`EnvelopeEngine`, `EnvelopeEngineMt`). They warm-start every step and are
  judged by the envelope's own step control, and an extra iteration per step multiplies across a run. Leave the check
  OFF there, and note in `RESOLVED.md` whether the bench's envelope run shows the same DC error.
- **A "settle" pass in harmonicaRF** (one strict solve when a drag is released). It may be worth having, but it is a
  UX decision of its own.
- **Running harmonically related tones on one fundamental automatically.** R-hbv-3 only warns. Whether to widen
  `Commensurate` is a separate decision.
- **The transient's start.** It begins from the operating point at t = 0, where a cosine-phase source sits at its peak.
  On an unloaded reservoir capacitor that is a state that takes hours to leave. That is correct for the convention, and
  `explain --analysis` already reports the time constant.

## Findings

Record them in `src/Engine/RESOLVED.md`, under a section after "HB answers Stop once per Newton iteration", never in
a `CLAUDE.md`.

## Completion note (2026-11-18)

- **Done as written, with three departures**, each recorded in `src/Engine/RESOLVED.md` under "HB convergence judges the
  voltage update":
  - **The key is `VRelTol`, not `VTol`.** Transient's `VTol` is an absolute voltage, and one key meaning two things would
    mislead.
  - **The gate bench is a single-diode peak detector at K = 10**, not the four-arm bridge. The bridge is the same physics
    at about a minute per solve in a Debug build, far past the routine tier.
  - **A step taken only for the update test accepts the full Newton step while the residual still meets `Tol`.**
    Without this, a residual already at its floor backtracked 8 times per check.
- **Default `VRelTol` = 1e-3 with a 1 µV floor.** It is the loosest value that brings the bridge within 1 mV of 13.177 V.
  - The HB heroes take no extra iterations.
  - Device evaluations rise by 6 % on Hero 3 and the Loadpull example, and by 1.3 % on the pursuit example.
  - Wall time rises by under 10 % everywhere.
- **Also fixed:** `HbEngine`'s three hand-written `MaxIter` copies of its settings dropped fields, including the run's
  Stop control. They are now `AnalysisSettings.WithMaxIter`.
- **No bench in `examples/` or `testdata/` gives the harmonically-related-tones warning.** 33 multi-tone benches were
  checked with `explain --analysis`.
- **Gates:**
  - `HbVoltageCheckTests` (9), `HarmonicaVoltageCheckOffTests` (1), and `NetlistContractTests` (one new test);
  - Engine.Tests filtered to HB, Hero, Loadpull, ParametricSweep and Osc: 648 passed;
  - Harmonica.Tests: 275 passed;
  - the three harmonicaRF cost tests: passed with no bound edited;
  - Ui.Tests classes covering the HB card, analysis serialization, Duplicate and harmonicaRF: 924 passed;
  - UiSnapshot `--analysis-body hb` was looked at.

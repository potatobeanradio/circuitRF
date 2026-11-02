# Brief ENV-SEG — parallel envelope segments that respect the circuit's memory

**Tag:** `R-envseg-n` · **Depends on:** the envelope analysis as merged (PR #16 and its follow-up 7c2b8e58)
**Area:** `src/Engine/Envelope/EnvelopeRunner.cs` (`Prepare`, `Execute`), the component-model base in `src/Core`
(one new virtual member), `src/Core/Devices/Composed/ComposedCoreModel.cs`, `src/Core/Netlist/AnalysisDirectiveSchema.cs`
(two summaries), `docs/user/src/reference/simulations.md`. Findings go in `src/Engine/RESOLVED.md`. HB, S-parameter, DC
and loadpull runs must be **byte-identical** after this brief, and so must every envelope run whose circuit has no
memory longer than today's warm-up. If any of them changes, stop and say why.

---

## 0. The gap

`Segments=auto` splits an envelope record into parallel-in-time segments, one per core (`EnvelopeRunner.Prepare`,
lines ~165-172). Every segment after the first starts from its own harmonic-balance point and simulates a warm-up
that is then discarded. The idea is sound, but the warm-up length takes no account of the circuit:

- **Auto warm-up is a constant 5 µs** (`double warm = double.IsNaN(plan.WarmupS) ? 5e-6 : plan.WarmupS`). The only
  rule on segment count is "no segment shorter than 4× the warm-up or 20 µs".
- **The schema's description is not what the code does.** It says auto comes "from the network's slowest pole, at
  least 2 us". No code computes a pole. The user page says "auto (5 µs)", which matches the code.
- **The user page also promises too much.** It says the stitched result "is identical to one sequential run (measured
  to round-off)". That holds only when everything in the circuit forgets within the warm-up.
- **A ComposedFET's trap and thermal states do not forget that fast.** The shipped example
  `examples/Model Composer/synthetic-gan/gan_4x125.cmodel` has an emission time of 1 ms on its drain-lag state. That is
  200× the warm-up. Each segment therefore restarts the trap state near its harmonic-balance value. The stitched
  state trajectory then shows a step at every seam, and the record no longer matches a sequential run.
- **The seam error is measured, but it is not usable and nobody is told about it.**
  - `__EnvSeamError` is the worst relative mismatch of harmonic 1, over *every* probe, over the overlap tails, each
    normalised by that probe's own magnitude at that step. So any internal-state probe dominates it: a trap node, a
    thermal node, a node whose fundamental is near zero.
  - Only the CLI prints it, as one line among the results. Nothing posts a warning, and the `__` prefix keeps it out
    of the trace picker.

### Measured (the case that found this)

The fixture is the example cmodel above, in the bench of §3.

| run | segments | wall | seam error | Pout_avg | Gt | ACPR1 L/U at Vout | ACPR2 L/U at Vout |
|---|---|---|---|---|---|---|---|
| `Segments=auto` | 10 | 82 s | **125.47** | 21.794 dBm | 9.769 dB | −24.68 / −25.26 | −36.13 / −36.49 |
| `Segments=1` | 1 | 309 s | 0 | 21.781 dBm | 9.777 dB | −24.70 / −25.25 | −36.07 / −36.46 |

Read this table carefully, because it shows both what is wrong and how big it is:

- **On this run the RF figures barely move: ≤ 0.06 dB.** The drain-lag state behaves as a peak detector. Capture
  takes nanoseconds and the high peaks in the first few µs of each warm-up recapture it, so most of the state is
  restored inside 5 µs. What the warm-up misses is the slow 1 ms leak between peaks.
- **The state trajectory is wrong.** A drain-lag waveform plotted from the segmented run restarts at every seam.
  Anyone who plots `φ2(t)`, the thermal node, or a slow bias node gets a wrong picture.
- **The seam error of 125 says "broken" without saying where or how badly.** The internal trap-node probe is the
  likely source, but nothing in the result says which probe it was.
- **Other cases will move the RF figures.** The 0.06 dB on this run is not a bound. Expect a larger error from a
  state that the warm-up has to *empty* rather than fill: a signal with quiet gaps (TDD or bursts), a gate-lag state,
  a thermal pole of milliseconds, or a large bias-network capacitor. §4 test 2 measures one such case.

## 1. What to build

### 1a. The circuit's memory, asked of the circuit

Add one virtual member to the component-model base. A model that holds a state slower than its own RF behaviour
reports its longest time constant, in seconds, at the operating point; `null` (the default) means it has none. Name
it to fit the base class, for example `double? SlowestTimeConstant(...)`.

- **`ComposedCoreModel` returns the largest of:**
  - each trap state's `TrapDynamics.EmissionTime(k, T)` at the model's temperature. The method exists and the
    composer already calls it (`src/Design/Modeling/TrapFit.cs:434`).
  - the dispersion τ (`_tau`).
  - each thermal pole's `Rth·Cth`. These are read from the model; the pole is also stamped into the linear network.
- **The linear network's slowest pole.** The schema already promises this. It is the largest eigenvalue magnitude
  of `G⁻¹C` at the first segment's operating point. A few power iterations on the LU the solve already factors will
  give it.
  - **Measure the cost before committing.** If it costs more than ~1 % of a 20 µs run, keep only the device
    declarations and say so in RESOLVED.
  - **Do not use a linearisation for the traps.** At an operating point where capture is active it reports the
    nanosecond capture rate, not the millisecond emission. That is why the trap time constant has to be *declared*.

Collect τ_max over the elaborated netlist once, in `Prepare`. Both the standalone run and the swept-points path
(`ParametricSweepEngine.cs:323`) go through `Prepare`, so both get the same answer.

### 1b. Auto warm-up and auto segment count from τ_max

- **Auto warm-up:** `max(5 µs, 5·τ_max)`. Keep the 5 µs floor so that every circuit with short memory gets the same
  warm-up, segment count and bytes as today.
- **Auto segment count:** the existing rule, applied to the new warm-up. When the run cannot hold two segments of at
  least 4× that warm-up, it runs as **one segment**. Post one note through the runner's `note` callback, which
  already reaches the app's warnings as `env-note:`. Name the part and the time constant, for example: *"envelope:
  one segment - X1's drain-lag state remembers 1 ms, longer than this 200 µs run can warm up in parallel;
  Segments=N runs it in parallel anyway"*.
- **An explicit `Segments=N` or `Warmup=…` is obeyed as written.** The warning in §1c then reports what the user
  chose.

Decision D1 (owner): whether auto chooses correctness (one segment, 3.8× slower on the run above) or speed (keep
the parallel split and only warn). This brief **recommends correctness**. The stitched record is what people plot,
and the docs promise that it matches a sequential run. A user who wants the speed can write `Segments=N` and accept
the warning.

### 1c. A seam error that can be read, and a warning

- **Measure it per probe, normalised by that probe's RMS of harmonic 1 over the overlap.** The value at each step is
  the wrong normaliser: one near-zero sample inflates it.
- **Report the worst probe by name.** Keep `__EnvSeamError` as the scalar, so files that use it keep working. Add
  `__EnvSeamProbe`, a label cube, or put the name into the note.
- **Warn once per run, via the `note` callback, when the worst probe exceeds a threshold.** Choose the threshold by
  measurement, using the run in §4 test 2: the seam error at which Pout_avg moves by 0.01 dB or ACPR by 0.1 dB.
  Record that measurement in RESOLVED.
- **Leave internal-state probes out of the warning's worst-probe choice,** but still measure them. These are the
  `__composed_*` nodes and a thermal node such as `X1.t`; a probe on one of them is a user's explicit choice, and its
  harmonic 1 means little. The warning must stay quiet on the table's `Segments=1` run and fire on its `auto` run
  *only if* an external probe disagrees.

### 1d. Text

- **`AnalysisDirectiveSchema.cs:127-128`:** describe what auto actually does once this brief lands, in both
  summaries.
- **`docs/user/src/reference/simulations.md`:**
  - The "Parallel in time" bullet (~159-163): say that the warm-up follows the circuit's slowest memory, and that a
    circuit with long memory runs as one segment unless told otherwise.
  - The `Segments` and `Warmup` table rows (~192-193).
  - No change history in the page. Regenerate that page only (`DocGen --page`).

## 2. Out of scope

- **Parareal-style correction** (a parallel pass, then segments re-run from the previous segment's end state). This
  could keep the speed with long memory, but it is a separate design. Note it in RESOLVED if the measurements suggest
  it is worth a brief.
- **Changing the step size, the accuracy presets, or `EnvelopeEngine`/`EnvelopeEngineMt` themselves.**

## 3. The fixture

Write it into the test as a string, as `EnvelopeConvergenceTests` does, resolving the cmodel path the same way:

```
Pin = 12
MOD_Source:SRC1 n1 0 Fc=2e9 Pavg=Pin dBm Z=50 Ohm Carrier[1]="NR;BW=20MHz;SCS=30kHz;Mod=64QAM;Off=0;P=0;Seed=1" Record=0.0005 Shaping=-80
C:C1 Vin n1 C=100 pF
L:L1 n2 Vin L=100 nH
Vdc:V1 n2 0 Vdc=-0.8 V
ComposedFET:X1 Vin Vout 0 File="{cmodel}" N=8 DrainLag=true
L:L2 VDD Vout L=100 nH
Vdc:V2 VDD 0 Vdc=8 V
C:C2 Vout n5 C=100 pF
R:RL n5 0 R=50 Ohm
analysis ENV1 type=env Stop=200e-6 Accuracy=standard Segments=auto MaxHarm=5 Probes="Vin,X1.di,Vout"
```

A 200 µs sequential run takes ~5 min, so it must not enter the routine gate. Tests 1 and 3 assert on the
**plan** (`Prepare`'s segment count, warm-up and note) and need no stepping. Test 2 uses a small linear circuit.

## 4. Tests (targeted, one per claim)

New file: `tests/Engine.Tests/Envelope/EnvelopeSegmentMemoryTests.cs`.

1. **A trap state forces one segment.** The fixture's `Prepare` with `Segments=auto` gives 1 segment and posts the
   note naming `X1` and ~1 ms. With `DrainLag=false GateLag=false` and no thermal pole above 5 µs, it gives today's
   count and warm-up.
2. **A slow bias network gets a warm-up that settles it, and the stitched run matches sequential.**
   - The circuit is a linear RC or LC bias feed with τ ≈ 20 µs and a 100 µs run.
   - `auto` picks a warm-up of ≥ 100 µs or one segment, and the result matches `Segments=1` to round-off.
   - With `Segments=4 Warmup=1e-6` forced, the seam warning fires and names the probe.
   - This run also sets the threshold in §1c.
3. **Short memory is unchanged.** An existing short-memory envelope fixture produces the same plan (segment count,
   warm-up, boundaries) as before the change. Assert the numbers, not a timing.
4. **The seam metric is not fooled by one small sample.** A probe whose harmonic 1 passes near zero inside the overlap
   does not dominate the reported seam error.

Run only these and the existing `tests/Engine.Tests/Envelope/` classes. Do not run the full suite.

## 5. Gates

- Tests 1-4 pass. The existing envelope and swept-envelope classes pass unchanged.
- The §3 fixture with `Segments=auto` runs as one segment and reports seam error 0.
- `src/Engine/RESOLVED.md` records:
  - the cost of the pole estimate (or why it was dropped);
  - the threshold measurement;
  - the before/after plan for the fixture.

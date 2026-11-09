# Brief series NZ — noise, from the S-parameter analysis to the receiver

**Goal.** A receiver designer can do the whole noise job in circuitRF:
- design an LNA for noise figure on built-in or kit transistors, and see which part sets the noise;
- read a mixer's noise figure, and an LNA's noise figure while a blocker drives it;
- trade noise figure against output power and gain in loadpull and sourcepull;
- see what noise and LO phase noise do to a modulated signal's EVM;
- simulate an oscillator and its phase noise.

**Why a series.** The S-parameter analysis computes noise (`Noise=yes`, from PR #50). It covers passive parts, Touchstone
noise data and compiled (OSDI) devices. Everything past that small-signal, two-port answer is missing:
- **No per-part answer.** Nothing reports which part contributes the noise.
- **No noise in the built-in models.** The built-in nonlinear models accept `Kf`/`Af` and use neither, and the system
  amplifier has no noise figure.
- **No noise in the nonlinear analyses.** Harmonic balance, loadpull and envelope carry no noise at all.
- **No oscillators.** circuitRF has no oscillator analysis, so there is no phase noise to compute.

## The briefs, in order

| Brief | What | Depends on |
|---|---|---|
| `brief-nz-1-noise-contributions.md` | Each part's share of the noise, a `Noise=no` key on any part, `Temp=` on SnP blocks, a resistive `Z_Port` thermal, `explain` lists the noisy parts | — |
| `brief-nz-2-lna-circles.md` | Available-gain, operating-gain and stability circles beside the noise circles, and one Smith chart that shows them together | — |
| `brief-nz-3-system-block-noise.md` | `NF=` on the system Amplifier and Mixer; the other system blocks thermal | 1 |
| `brief-nz-4-device-noise.md` | Noise sources in the built-in nonlinear models: diode, BJT, MOSFET, VDMOS and the FET models | 1 |
| `brief-nz-5-hb-noise.md` | Noise in harmonic balance on the existing conversion-matrix solve: mixer NF (SSB, DSB), NF under a large signal | 3, 4 |
| `brief-nz-6-loadpull-noise.md` | NF as a loadpull and sourcepull metric, with contours | 5 |
| `brief-nz-7-envelope-noise.md` | Envelope: thermal noise on sources and system blocks, and a phase-noise mask on any source | 3 |
| `brief-nz-8-envelope-signal-quality.md` | Envelope: EVM, SNR and the constellation of the output signal | 7 |
| `brief-nz-9-oscillator-analysis.md` | An oscillator analysis: harmonic balance with the frequency as an unknown | — |
| `brief-nz-10-phase-noise.md` | Oscillator phase noise from the circuit, on brief 9's solution | 4, 5, 9 |
| `brief-nz-11-receiver-example.md` | The "Receiver Noise" example workspace, and the docs pass that ties the series together | all |

Briefs 1–4 are independent of the nonlinear engines and can be done in any order, after 1 where the table says so.
Briefs 7–8 do not need 5. Briefs 9–10 are the largest and highest-risk part of the series (§ Risk).

## Decisions already made

- **D1 — One noise seam.** Every noisy element is one entry in `SParameterNoise.Sources`. Each entry is a function giving
  the part's noise current correlation on its own nodes at a frequency (`NoiseCurrents`). The built-in models (brief 4),
  the system blocks (brief 3) and the HB noise (brief 5) add entries or evaluate the same entries. None of them adds a
  second way of describing a part's noise.
- **D2 — Conventions are PR #50's, unchanged.**
  - Densities are single-sided and absolute.
  - NF is referenced to T0 = 290 K, while parts are at their physical temperature.
  - k = 1.380649e-23 exactly.
  - The cube names stay as they are: `Cs`, `NF`, `Fn`, `NFmin`, `Fmin`, `Sopt`, `Zopt`, `Rn`, `Te`, `Tmin`.
- **D3 — `Noise=yes` stays opt-in on every analysis that gains it.** A run without it is byte-identical to today's,
  and that is a gate in every brief.
- **D4 — A noiseless part is never silent.** Any part the analysis cannot give noise to is named in one warning, as
  `NoiseDiagnostics.Noiseless` does today. That includes a model with no noise, an SDD, and a block with gain and no
  data. `Noise=no` (brief 1) is the user's own statement, so it is reported by `explain` and not warned about.
- **D5 — Random noise is seeded.** Envelope noise (briefs 7–8) takes `Seed=` (default 1), and two runs with the same
  seed give the same bits. A result that changes on every run cannot be compared, optimized or tested.
- **D6 — Transient noise is out.** circuitRF has no transient analysis (PRD scope), and none is added here.
- **D7 — No dedicated noise source-pull analysis.** NF at any source follows from the four noise parameters, so the
  circles answer that question for the small-signal case. The large-signal case is a metric in the existing
  loadpull/sourcepull engine (brief 6), not a new analysis.
- **D8 — Link-level simulation is out.** That means bit error rate, channel coding and receiver DSP. EVM, SNR and the
  constellation (brief 8) are where an RF simulator stops.

## Risk, stated before the series starts

- **Briefs 1–4, 6–8:** low. Each extends something that already works, on one already-measured method.
- **Brief 5 (HB noise):** medium. The conversion matrix exists and is gated (`HbSmallSignal`, the WSProbe under HB).
  The new part is cyclostationary sources: a device's noise density modulated by its own large-signal waveform. The risk
  is in the sideband bookkeeping, not the solve. A mixer's SSB/DSB NF has closed forms for an ideal multiplier and a
  switching diode, and those are the gate.
- **Brief 9 (oscillators):** high. Free-running HB is a different solve: the frequency is an unknown, and the
  oscillating solution has to be found and the trivial DC solution rejected. Start-up detection exists (the WSProbe's
  stability metrics). If the brief's own gate fails, stop and report instead of widening the brief.
- **Brief 10 (phase noise):** high, and only after 9 passes. The far-from-carrier noise comes from the conversion matrix.
  The close-in 1/f² and 1/f³ slopes need the solution's phase sensitivity. A result can look plausible and be wrong by
  tens of dB, so the gate is closed forms (a resonator oscillator with a known Q and noise source, whose phase noise has a closed form), not
  a smooth-looking curve.

## Gates, for the whole series

1. **No change without `Noise=yes`.** Every touched analysis gives byte-identical output with the key absent, over the
   shipped examples it already runs.
2. **Small-signal limit.** HB noise with the drive turned down to nothing equals the S-parameter noise analysis on the
   same circuit, to 1e-6 dB in NF (brief 5). Envelope noise on a linear circuit, averaged, gives the S-parameter NF to
   within the statistics the record length allows (brief 7).
3. **Closed forms** for every new noise source: a resistor, a junction's shot noise, a channel's thermal noise, an
   amplifier block's NF, an ideal mixer's SSB/DSB NF, a resonator oscillator's phase noise.
4. **The example runs** headless, with no display, in a few seconds per bench (brief 11).

## Rules that apply throughout

- **Results are `DataSet` cubes** beside the analysis's existing ones, named as D2 says. Nothing invents a result type.
- **The engine stays in `src/Engine`, the math in `src/RfCore`.** The editors only show keys and results.
- **Tests:** one per claim, named classes only. Never all of `Ui.Tests`, never the full suite. No new `Category=Benchmark`
  test and no timing assertion.
- **The GUI, the CLI and MCP get every key at once.** That means the analysis editor, `circuitrf explain`/`check`, and
  the `reference analyses` text. A key in one surface only is a defect.
- **No new citations, and no other tool's name, in the code or the docs.** The equations are written out in our own
  words. PR #50's existing references stay where they are. No commercial tool or PDK names anywhere.
- UI work is checked visually with `tools/UiSnapshot`, and the PNGs looked at.

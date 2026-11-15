# Brief TRN-3 — the switch

**Series:** TRN-0 … TRN-9 · **Tag:** `R-trn3-<n>` · **Depends on:** TRN-1 (TRN-2 for its tests' sources) ·
**Design note:** §7.1, §13.1
**Area:** `src/Core/Devices/SwitchModel.cs` (new), `ComponentModelFactory`, the component type registry,
`src/Engine/Transient` (soft breakpoints), `src/Core/Netlist/Spice` (the reader), `BuiltInSymbols`, the palette,
`src/Core/RESOLVED.md`.

## The gap

circuitRF has no switch. Switching converters, sample-and-holds, T/R switches modelled ideally and class-E stages driven
by an ideal switch cannot be drawn, and the SPICE-dialect reader refuses switch lines by name for that reason.

## Requirements

**R-trn3-1 — The device.** `Switch:S1 p n cp cn Ron Roff Von Voff` per note §7.1: a conductance between `p` and `n`
controlled by v(cp) − v(cn), moving between 1/`Roff` and 1/`Ron` along a smooth monotone transition between `Voff`
and `Von`, continuous with a continuous first derivative everywhere. `Von < Voff` gives inverse logic. `Hyst` adds
hysteresis in transient only; every other analysis ignores it and says so in a note when it is non-zero.

**R-trn3-2 — Every analysis.** An ordinary nonlinear `ComponentModel` with no charge: DC, S-parameter (linearised at its
bias), HB (its smooth transition keeps the spectrum finite), envelope and transient.

**R-trn3-3 — Soft breakpoints.** The transition interval is reported to the step control, which limits the step across
it (at least `TransitionPoints`, default 8, accepted steps across a full transition) without forcing a landing.

**R-trn3-4 — Symbol and palette.** Under Sources & Switches (or the nearest existing group): a switch drawn with its
control pins. UiSnapshot PNG, looked at.

**R-trn3-5 — The reader.** The dialect's voltage-controlled switch line and its `.model` become `Switch`, mapping the
on/off resistances and thresholds; where the dialect's model has a parameter `Switch` cannot express, the line is
refused by that parameter's name. Current-controlled switch lines stay refused by name. `spice-models.md` §8.7 and §9.3
are updated.

**R-trn3-6 — Hard-switching measurement.** A buck converter (switch, diode, LC, load) driven by a 1 MHz pulse is run
over 200 cycles and the result, counters and any failure recorded in `src/Engine/RESOLVED.md` — the first statement of
what the engine does on a hard-switching circuit.

## Gates

1. **Levels.** At `Von` and above the conductance is 1/`Ron` within 1e-12 relative, at `Voff` and below 1/`Roff`.
2. **Smoothness.** The conductance and its first derivative are continuous across the transition (sampled densely).
3. **HB.** A switch driven by a tone in HB converges and its current's spectrum matches a transient's settled `fft`
   (TRN-5's function, or a test-local transform until TRN-5 lands) within 0.05 dB on the first five harmonics.
4. **Buck.** The buck converter's settled output voltage matches the duty-cycle estimate corrected for `Ron` and the
   diode drop within 2 %.
5. **Reader.** A deck with a voltage-controlled switch reads to `Switch` with the mapped values; a current-controlled
   one is refused by name.

## Not here

Current-controlled switches, digital logic elements, a comparator device.

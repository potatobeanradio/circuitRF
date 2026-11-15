# Brief TRN-2 — waveform sources, tone sources in transient, initial conditions, `time`

**Series:** TRN-0 … TRN-9 · **Tag:** `R-trn2-<n>` · **Depends on:** TRN-1 · **Design note:** §2 D5, D6, §6, §7.2
**Area:** `src/Core/Devices/WaveformSourceModel.cs` (new, the one class under every waveform type), `IdcModel` (new), `ToneSourceModelBase`, `P1ToneModel`, `PnToneModel`,
`CapacitorModel`, `InductorModel`, `ComponentModelFactory`, the component type registry, the expression engine,
`src/Core/Netlist/Spice` (the reader), `BuiltInSymbols`, the palette, the parameter editor, `src/Core/RESOLVED.md`.

## The gap

No source describes a pulse, a piecewise-linear ramp or a delayed, ramped tone; no part carries an initial condition;
an expression cannot refer to time. The SPICE-dialect reader turns every stimulus into its DC value and drops `IC=`.

## Requirements

**R-trn2-1 — One type per waveform.** `V_Pulse`, `V_PWL`, `V_Exp`, `V_SFFM`, `V_Expr` and `I_Pulse`, `I_PWL`, `I_Exp`,
`I_SFFM`, `I_Expr` with note §6.1's keys, each type carrying only its own; every numeric key an expression. One model
class serves all ten (as `ToneSourceModelBase` serves the tone sources), so the waveform code exists once. Each reports
its breakpoints in the run interval to the step control. `V_PWL File=` reads two columns (time, value), comma or
whitespace separated, `#` comments, times strictly increasing (refused otherwise, naming the line); `Points=` and
`File=` together are refused. The file resolves like every other file reference and is archived with the workspace.

**R-trn2-2 — `Idc`.** The DC current source, `Idc:I1 p n Idc=…`, the counterpart of `Vdc`, in every analysis. The SPICE
reader's DC current lines become `Idc` instead of the constant-equation SDD `spice-models.md` §9.3 describes.

**R-trn2-3 — Steady-state value.** In DC, S-parameter, HB, loadpull and envelope, a waveform source is its `Dc`, default
its waveform at t = 0 (D6). S-parameter and HB see a voltage source as a short and a current source as an open at
every frequency other than DC, as `Vdc` is.

**R-trn2-4 — Palette, symbols, editor.** Under Sources: Pulse, PWL, Exponential, SFFM, Expression (voltage and current)
and DC Current; each symbol draws its waveform's shape in the body. The parameter editor groups each type's keys
(levels, timing). UiSnapshot PNGs of the symbols and the editor, looked at.

**R-trn2-5 — Tone sources in transient.** `Delay`, `Ramp` and `Damping` (note §6.2), transient-only and ignored by every
other analysis (a test proves an HB result unchanged with them set); breakpoints at `Delay` and `Delay + Ramp`. `P1Tone`/`PnTone` keep available power and source
impedance; a non-default harmonic termination `Z[k]` is refused until TRN-4 realizes it.

**R-trn2-6 — `IC=`** on `C` (volts) and `L` (amperes), applied in the starting-state solve (note §4.5) and ignored by
every other analysis.

**R-trn2-7 — `time`.** Bound to the simulated time in a tran analysis. Everywhere else: refused outside a condition,
naming it; inside a condition, the steady-state reading `spice-models.md` §8.10 already gives. One rule in the
expression engine, not per caller.

**R-trn2-8 — The reader.** `PULSE` → `V_Pulse`, `PWL` → `V_PWL`, `EXP` → `V_Exp`, `SFFM` → `V_SFFM`, `SIN` → `V_1Tone`
with `Delay` and `Damping` (and the `I_` forms likewise) instead of `Vdc` at their DC value; `IC=` is kept on `C` and `L`; a behavioural source using `time` outside a condition
is read instead of refused. `spice-models.md` §8.6, §8.10 and §9.3 are updated to say so.

## Gates

1. **Pulse.** A `V_Pulse` into a resistor: every corner is an accepted step, and the waveform equals the definition at every
   output point to round-off.
2. **PWL file.** `V_PWL File=` equals the same points given as `Points=`, bit for bit; a non-increasing time is refused
   naming the line.
3. **Ramp.** A `V_1Tone` with `Ramp=10/f`: the envelope of the transient output follows the raised cosine within 1e-3.
4. **Steady state.** An HB and an S-parameter run of a bench with a pulse source equal the same bench with `Vdc` at the
   pulse's t = 0 value, bit for bit.
5. **IC.** An RC with `IC=1` on the capacitor and no source decays as e^(−t/τ) from 1 V within 1e-5.
6. **`time`.** `V_Expr Expr="1 - exp(-time/1u)"` equals `V_Exp` with the same constants within
   the run's tolerance; `time` outside a condition in an HB bench is refused naming it.
7. **Reader.** A deck with each stimulus reads to its matching type (`SIN` to `V_1Tone` with `Delay`/`Damping`), a DC
   current line to `Idc`, and `IC=` survives into the elaborated netlist.
8. **Damped sine.** `V_1Tone` with `Delay` and `Damping` equals the closed form A·e^(−θ(t−td))·sin(2πf(t−td)+φ) after
   `Delay` within the run's tolerance.

## Not here

The switch (TRN-3), measurements (TRN-5), writing any of this back to the dialect (TRN-6), `MOD_Source` in transient.

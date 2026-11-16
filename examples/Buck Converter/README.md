# Buck Converter

A 12 V to 5.5 V buck converter switching at 1 MHz, simulated in time: the start-up from rest, the output ripple and
how long the output takes to settle, and how initial conditions skip the start-up when only the steady state is
wanted.

**Run `Startup` first.** `SteadyState`'s initial conditions are numbers read from its result.

## The circuit

`VIN` (12 V) feeds the voltage-controlled switch `S1`, whose control is `VG`, a 5 V gate drive at `Fsw` = 1 MHz with
duty `Duty` = 0.5 (both are variables on the `VAR` block). `S1` is 0.1 Ω on and 1 MΩ off, turning on above 3 V of
control and off below 2 V, with a smooth transition between. When it opens, the inductor current carries on through
the free-wheeling diode `D1`. `L1` (10 µH) and `C1` (1 µF) filter the switched node into a 5 Ω load. The IProbe `IL`
records the inductor current.

The load `RL` is a Term of 5 Ω. Outside S-parameters a Term is a load of its `Z`, so here it is a 5 Ω resistor.

## Startup

`InitialState=zero`: every voltage and current starts at 0, the supply included, so the run begins with the supply
switched on. 200 µs is about ten periods of the LC filter's 50 kHz resonance, which a 5 Ω load damps to a Q of 1.6.

| Measure | Result | What it is |
|---|---|---|
| `Vout` | 5.518 V | `V(out)` averaged over the last switching cycle |
| `Ripple` | 39.9 mV | Peak to peak over the last switching cycle |
| `Overshoot_pct` | 34.4 % | How far the output rises past 5.5 V on the way up |
| `Tsettle` | 34.9 µs | When the output stays within 2 % of its final value |
| `IL_valley` | 0.947 A | The inductor current at its lowest in the last cycle |

The output is below `Duty` × 12 V = 6 V by the diode's drop during the off half of each cycle and the switch's
resistance.

## SteadyState

The same circuit, started where `Startup` ends. A switching cycle begins when `S1` turns on, which is when the
inductor current is at its valley, so the run takes two initial conditions from `Startup`:

- `IC="out=5.518"` on the analysis card holds the output at 5.518 V while the operating point is solved and releases
  it at t = 0. It is `V(out)` at the start of a cycle late in `Startup`.
- `IC=0.947` on `L1` starts the inductor at 0.947 A, the `IL_valley` above.

The output is steady from the first cycle, so 10 µs replaces 200 µs:

| Measure | Startup (last cycle of 200 µs) | SteadyState (last cycle of 10 µs) |
|---|---|---|
| `Vout` | 5.5175 V | 5.5172 V |
| `Ripple` | 39.85 mV | 39.95 mV |
| `IL_valley` | 0.9470 A | 0.9471 A |

Without the inductor's `IC` the output still starts at 5.518 V, but the inductor starts empty and the filter rings
again: the output swings 1.95 V over the first 10 µs and the last cycle reads 5.19 V. Both stored energies have to
be right.

## Things to try

- Change `Duty` to 0.4 and run `Startup`: the output settles at 4.26 V with 38 mV of ripple, and `IL_valley` is
  0.70 A. `SteadyState`'s `IC` values then need reading again.
- `circuitrf tran "Buck Converter/Startup/schematic/Startup.csch"` runs the same bench headlessly and prints the
  measures.

## Settings traded for run time

None. `Step=10n` is the output grid, 100 points per switching cycle, and the largest step. Inside it the engine lands a
step on every corner of `VG` and takes at least eight steps across each turn-on and turn-off of `S1` (its
`TransitionPoints`), so the edges are resolved whatever the grid.

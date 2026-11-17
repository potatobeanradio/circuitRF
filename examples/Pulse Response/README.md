# Pulse Response

Two circuits driven by a fast step, each simulated twice: once by circuitRF's own transient engine and once by
ngspice. The pairs are drawn identically; the only difference is the analysis card's **Solver**.

Run any bench with **Simulate**. Its Data Display opens on the Transient plots: the voltages against time, the output's
spectrum over its last period and the measures as a table.

## Filter_Native and Filter_ngspice

A 5th-order Butterworth low-pass filter, 1 GHz cut-off, between 50 Ω terminations: shunt C, series L, shunt C,
series L, shunt C (1.967 pF, 12.88 nH, 6.366 pF, 12.88 nH, 1.967 pF). `VS` steps from 0 to 1 V at 1 ns with a 50 ps
rise. A `V_Pulse` with no `Width` holds `V2` to the end of the run, so it is a step.

The measures read the output's edge:

| Measure | Native | ngspice | What it is |
|---|---|---|---|
| `Trise` | 408.9 ps | 409.0 ps | 10 % to 90 % of the step at `out` |
| `Overshoot_pct` | 12.73 % | 12.73 % | How far `out` goes past its final 0.5 V |
| `Tsettle` | 3.23 ns | 3.23 ns | When `out` stays within 1 % of its final value (from t = 0) |
| `Vfinal` | 0.5 V | 0.5 V | The 50 Ω divider |

12.7 % is the textbook overshoot of a 5th-order Butterworth step response: a flat passband rings on an edge.

## Line_Native and Line_ngspice

A lossless 50 Ω line, 1 ns long (`E` = 360° at 1 GHz), driven through 50 Ω and terminated in 100 Ω. The step
launches 0.5 V down the line. At the load the reflection coefficient is (100 − 50)/(100 + 50) = 1/3, so after the
round trip of 2 ns the input rises to 0.5 · (1 + 1/3) = 0.667 V and stays there, because the 50 Ω source absorbs
the returning wave.

| Measure | Native | ngspice |
|---|---|---|
| `Vincident` (`V(in)` at 1.5 ns) | 0.5 V | 0.5 V |
| `Vsettled` (`V(in)` at 4 ns) | 0.666667 V | 0.666667 V |
| `Gamma` | 0.333333 | 0.333333 |

Plot `V(in)` and `V(out)` together to see the step leave, arrive 1 ns later at 0.667 V, and the reflection come back.
The native engine steps a lossless line exactly as its delay, which its run message says along with the line's
error against its frequency-domain model.

## The ngspice benches

`Filter_ngspice` and `Line_ngspice` need ngspice. Install it from **Settings ▸ Solvers ▸ Install ngspice…**, or
with `circuitrf solver install ngspice`; an ngspice from your system's package manager is found with nothing to
set. Without one the **ngspice** choice on the card is greyed and a run is refused with a sentence saying how to get
one. It never falls back to the native engine.

Each ngspice run writes the deck it ran into `results/ngspice/TR1-<time>-<id>/deck.cir` and names it in the
Messages panel. It is an ordinary SPICE deck: `ngspice -b deck.cir` runs it by hand. `circuitrf netlist <bench> -o
deck.cir` writes the same deck without running anything.

## Settings traded for run time

None that move a number. `Step=5p` is the output grid. The native engine also takes it as its largest step, and
halving it moves `Trise` by 0.03 ps and `Overshoot_pct` by 0.004 %. Each bench runs in well under a second.
